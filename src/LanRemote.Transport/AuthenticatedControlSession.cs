using System.Security.Authentication;
using System.Security.Cryptography;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport;

/// <summary>通过双向访问密钥认证的控制会话，独占 TLS 连接；调用方负责释放。</summary>
/// <remarks>
/// M4 不提供输入发送接口。认证只消费第一个终帧，后续帧（包括第二个 success）由 M5 消费方处理。
/// </remarks>
public sealed partial class AuthenticatedControlSession : IDisposable
{
    private readonly object _gate = new();
    private TlsConnection? _connection;
    private readonly byte[] _sessionToken;
    private readonly long _successReceivedAt;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _attachBudget;

    internal AuthenticatedControlSession(
        TlsConnection connection,
        SessionPermission grantedPermission,
        Guid sessionId,
        string shortCode,
        ReadOnlySpan<byte> sessionToken,
        int videoAttachExpiresInMsHint,
        long successReceivedAt,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(videoAttachExpiresInMsHint);
        _successReceivedAt = successReceivedAt;
        _clock = clock;
        _attachBudget = TimeSpan.FromMilliseconds(Math.Min(videoAttachExpiresInMsHint, 15_000));
        Identity = connection.Identity;
        GrantedPermission = grantedPermission;
        SessionId = sessionId;
        ShortCode = shortCode;
        VideoAttachExpiresInMsHint = videoAttachExpiresInMsHint;
        _sessionToken = sessionToken.ToArray();
        _connection = connection;
        _ownedConnection = connection;
    }

    /// <summary>连接时冻结的 TLS 身份，不再查询发现缓存。</summary>
    public ConnectionIdentity Identity { get; }

    /// <summary>经过 serverProof 验证的实际授权；不会高于请求权限。</summary>
    public SessionPermission GrantedPermission { get; }

    /// <summary>本次 challenge 中、已绑定到双向 proof 的会话标识。</summary>
    public Guid SessionId { get; }

    /// <summary>六位大写 HEX 人工关联码，不作为认证凭据。</summary>
    public string ShortCode { get; }

    /// <summary>未来 Transport 协议消费方独占使用；不得另起并行读者。</summary>
    internal Stream Stream =>
        (Volatile.Read(ref _connection)
            ?? throw new ObjectDisposedException(nameof(AuthenticatedControlSession))).Stream;

    /// <summary>绑定控制会话与第二 TLS 实际指纹，只返回 proof；不消费附加资格或施加本地 TTL。</summary>
    internal byte[] CreateVideoAttachProof(
        ReadOnlySpan<byte> attachNonce,
        ReadOnlySpan<byte> actualVideoPin,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_connection is null, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (attachNonce.Length != VideoAttachProof.NonceByteLength)
            {
                throw new ArgumentException("attachNonce 必须是 16 字节。", nameof(attachNonce));
            }

            if (actualVideoPin.Length != CertificatePin.LengthBytes)
            {
                throw new ArgumentException("actualVideoPin 必须是 32 字节。", nameof(actualVideoPin));
            }

            Span<byte> nonce = stackalloc byte[VideoAttachProof.NonceByteLength];
            Span<byte> pin = stackalloc byte[CertificatePin.LengthBytes];
            byte[]? proof = null;
            try
            {
                // 校验和 MAC 共用私有快照；不保证调用方并行改写原数组时的复制原子性。
                attachNonce.CopyTo(nonce);
                actualVideoPin.CopyTo(pin);
                if (!Identity.PinsMatch || !CertificatePin.Matches(Identity.PresentedCertSha256.Span, pin))
                {
                    throw new AuthenticationException("视频 TLS 实际证书指纹与控制会话冻结身份不一致。");
                }

                proof = VideoAttachProof.ComputeProof(_sessionToken, SessionId, nonce, pin);
                cancellationToken.ThrowIfCancellationRequested();
                byte[] result = proof;
                proof = null;
                return result;
            }
            finally
            {
                if (proof is not null)
                {
                    CryptographicOperations.ZeroMemory(proof);
                }
                CryptographicOperations.ZeroMemory(nonce);
                CryptographicOperations.ZeroMemory(pin);
            }
        }
    }

    /// <summary>远端视频附加窗口提示，不是权威 TTL；本地消费另施加 15 秒上限。</summary>
    internal int VideoAttachExpiresInMsHint { get; }

    /// <summary>
    /// 从 success 原 payload 收齐时刻计算本地等待上界，不代表服务端准确剩余 TTL。
    /// 仅查询，不创建 timer、不消费附着资格，不因到期撤销或关闭仍有效的 Control。
    /// </summary>
    internal TimeSpan GetRemainingAttachBudget(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_connection is null, this);
            cancellationToken.ThrowIfCancellationRequested();
            long now = _clock.GetTimestamp();
            TimeSpan elapsed = _clock.GetElapsedTime(_successReceivedAt, now);
            // TimeProvider 可重入；取时及频率读取之后，撤销/取消仍先于到期裁决。
            ObjectDisposedException.ThrowIf(_connection is null, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (elapsed < TimeSpan.Zero || elapsed >= _attachBudget)
            {
                throw new TimeoutException("本地视频附着等待预算已耗尽。");
            }
            return _attachBudget - elapsed;
        }
    }

    /// <summary>清零私有 token 并关闭连接；可重复调用。</summary>
    public void Dispose()
    {
        // 首个撤销者请求两条连接关闭，再保持原来的 Control 同步等待合同。
        // 重复 Dispose（包括 owner 已撤销）不请求关闭，也不等待首次慢释放。
        TlsConnection? connection = RevokeCore();
        if (connection is null) return;
        _ = connection.CloseAsync();
        _videoLifetime?.RequestStop();
        connection.Dispose();
    }

    /// <summary>
    /// 未交付会话的 owner 撤销：仅失效并清零，不关闭或等待网络。
    /// 调用方必须已持有原连接，并负责 await CloseAsync 及消费 CleanupErrors。
    /// </summary>
    internal void RevokeForOwnerCleanup() => _ = RevokeCore();

    private TlsConnection? RevokeCore()
    {
        lock (_gate)
        {
            TlsConnection? connection = Interlocked.Exchange(ref _connection, null);
            if (connection is not null)
            {
                _controlMonitor?.ObserveLocalStopUnderGate();
                CryptographicOperations.ZeroMemory(_sessionToken);
                // 仅逻辑失效，不能在认证 owner 撤销路径调度网络或任意取消回调。
                _videoLifetime?.MarkStoppedUnderGate();
            }
            return connection;
        }
    }
}
