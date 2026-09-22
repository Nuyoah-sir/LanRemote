using System.Security.Cryptography;
using LanRemote.Core.Models;

namespace LanRemote.Transport;

/// <summary>通过双向访问密钥认证的控制会话，独占 TLS 连接；调用方负责释放。</summary>
/// <remarks>
/// M4 不提供输入发送接口。认证只消费第一个终帧，后续帧（包括第二个 success）由 M5 消费方处理。
/// </remarks>
public sealed class AuthenticatedControlSession : IDisposable
{
    private TlsConnection? _connection;
    private readonly byte[] _sessionToken;

    internal AuthenticatedControlSession(
        TlsConnection connection,
        SessionPermission grantedPermission,
        Guid sessionId,
        string shortCode,
        ReadOnlySpan<byte> sessionToken,
        int videoAttachExpiresInMsHint)
    {
        Identity = connection.Identity;
        GrantedPermission = grantedPermission;
        SessionId = sessionId;
        ShortCode = shortCode;
        VideoAttachExpiresInMsHint = videoAttachExpiresInMsHint;
        _sessionToken = sessionToken.ToArray();
        _connection = connection;
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

    /// <summary>仅供未来 Transport 视频附加使用；释放会话后已有视图也被清零。</summary>
    internal ReadOnlyMemory<byte> SessionToken
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _connection) is null, this);
            return _sessionToken;
        }
    }

    /// <summary>远端视频附加窗口提示，不是权威 TTL；M5 消费时必须再施加本地上限。</summary>
    internal int VideoAttachExpiresInMsHint { get; }

    /// <summary>清零私有 token 并关闭连接；可重复调用。</summary>
    public void Dispose()
    {
        TlsConnection? connection = Interlocked.Exchange(ref _connection, null);
        if (connection is null)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_sessionToken);
        connection.Dispose();
    }
}
