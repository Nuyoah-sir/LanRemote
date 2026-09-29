using System.Net;
using System.Security.Cryptography;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport;

/// <summary>
/// 一个已认证控制会话的非秘密摘要（快照 / 观测用；不含 token / proof / key）。
/// </summary>
/// <param name="SessionId">认证会话 id。</param>
/// <param name="ConnectionId">承载连接 id。</param>
/// <param name="ClientDeviceId">客户端设备号。</param>
/// <param name="ClientName">客户端显示名（对端自称，原样保留）。</param>
/// <param name="GrantedPermission">实际授予的权限。</param>
/// <param name="RemoteAddress">对端地址。</param>
/// <param name="RemotePort">对端端口。</param>
/// <param name="RegisteredAt">登记时刻。</param>
public sealed record ControlSessionSummary(
    Guid SessionId,
    Guid ConnectionId,
    Guid ClientDeviceId,
    string ClientName,
    SessionPermission GrantedPermission,
    IPAddress RemoteAddress,
    int RemotePort,
    DateTimeOffset RegisteredAt);

/// <summary>
/// 已认证控制会话的登记表——DoD「auth success 才能有 session」的可执行载体。
/// </summary>
/// <remarks>
/// <para><b>注册入口是 <c>internal</c> 且只被认证状态机在 <see cref="ControlSessionState.Authenticated"/>
/// 状态下调用</b>（ADR-038 第 5 条）：程序集外的产品代码无法登记；程序集内也只有一个调用点。
/// 有反射测试盯着「公开面上没有任何注册方法」。</para>
/// <para><b>会话随连接存活</b>：登记发生在 <c>auth_success</c> 写出成功之后；注销发生在
/// 连接断开（EOF/RST）或停机。sessionToken <b>只存在内存</b>（规格 04 §10）：
/// 登记表不把 token 放进快照、日志或任何导出面；注销时当场清零。</para>
/// </remarks>
public sealed class SessionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _sessions = new();

    /// <summary>当前活动会话数。</summary>
    public int ActiveSessionCount
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    /// <summary>非秘密快照（测试 / 观测用）。</summary>
    /// <returns>按登记顺序无关的摘要列表。</returns>
    public IReadOnlyList<ControlSessionSummary> Snapshot()
    {
        lock (_gate)
        {
            return _sessions.Values
                .Select(entry => entry.Summary)
                .ToArray();
        }
    }

    /// <summary>
    /// 登记一个已认证会话（<b>唯一入口</b>；只允许认证状态机在
    /// <see cref="ControlSessionState.Authenticated"/> 状态调用）。
    /// </summary>
    /// <returns>注销句柄；<see cref="IDisposable.Dispose"/> 幂等。</returns>
    /// <remarks>token 由本表做防御性拷贝持有；调用方对自己的副本做任何处理都不影响登记内容。</remarks>
    internal SessionRegistration Register(
        Guid sessionId,
        Guid connectionId,
        Guid clientDeviceId,
        string clientName,
        SessionPermission grantedPermission,
        IPAddress remoteAddress,
        int remotePort,
        ReadOnlySpan<byte> sessionToken,
        VideoAttachWindow attachWindow,
        ReadOnlySpan<byte> controlCertificateSha256,
        CancellationToken controlCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachWindow);
        ArgumentNullException.ThrowIfNull(remoteAddress);
        if (sessionToken.Length != 32)
        {
            throw new ArgumentException("会话 token 必须恰好 32 字节。", nameof(sessionToken));
        }

        if (controlCertificateSha256.Length != 32)
        {
            throw new ArgumentException("控制连接证书摘要必须恰好 32 字节。", nameof(controlCertificateSha256));
        }

        byte[] ownedToken = sessionToken.ToArray();
        bool registered = false;
        try
        {
            Entry entry = new(
                new ControlSessionSummary(
                    sessionId,
                    connectionId,
                    clientDeviceId,
                    clientName,
                    grantedPermission,
                    remoteAddress,
                    remotePort,
                    DateTimeOffset.UtcNow),
                ownedToken,
                attachWindow,
                controlCertificateSha256.ToArray(),
                controlCancellationToken);
            // 先构造句柄，避免入表后构造失败却无人持有注销责任。
            SessionRegistration registration = new(this, sessionId, entry);
            lock (_gate)
            {
                // 只登记成功写出的控制会话；已过期的窗口不阻止控制登记，也不重置起点。
                _sessions.Add(sessionId, entry);
                registered = true;
            }

            return registration;
        }
        finally
        {
            if (!registered)
            {
                CryptographicOperations.ZeroMemory(ownedToken);
            }
        }
    }

    /// <summary>在登记表内验证视频证明并原子消费一次性资格，不导出共享 token。</summary>
    internal VideoAttachStatus TryAttachVideo(
        Guid sessionId,
        ConnectionSecurityContext videoSecurity,
        ReadOnlySpan<byte> attachNonce,
        ReadOnlySpan<byte> attachProof,
        CancellationToken cancellationToken,
        out VideoAttachLease? lease)
    {
        lease = null;
        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return VideoAttachStatus.Cancelled;
            }

            if (!_sessions.TryGetValue(sessionId, out Entry? entry))
            {
                return VideoAttachStatus.Unavailable;
            }

            VideoAttachStatus? unavailable = CheckAvailability(sessionId, entry, cancellationToken);
            if (unavailable is not null)
            {
                return unavailable.Value;
            }

            if (videoSecurity is null || attachNonce.Length != 16 || attachProof.Length != 32)
            {
                return VideoAttachStatus.InvalidInput;
            }

            Span<byte> remoteAddress = stackalloc byte[4];
            if (videoSecurity.ConnectionId == entry.Summary.ConnectionId ||
                !videoSecurity.RemoteAddress.TryWriteBytes(remoteAddress, out int addressLength) ||
                addressLength != remoteAddress.Length ||
                !remoteAddress.SequenceEqual(entry.ControlRemoteAddress) ||
                !CryptographicOperations.FixedTimeEquals(
                    videoSecurity.ServerCertificateSha256.Span, entry.ControlCertificateSha256))
            {
                return VideoAttachStatus.IdentityMismatch;
            }

            byte[] expectedProof = VideoAttachProof.ComputeProof(
                entry.SessionToken, sessionId, attachNonce, videoSecurity.ServerCertificateSha256.Span);
            try
            {
                bool proofMatches = CryptographicOperations.FixedTimeEquals(expectedProof, attachProof);
                VideoAttachLease? candidate = proofMatches
                    ? new VideoAttachLease(sessionId, videoSecurity.ConnectionId, entry.Revoked.Task)
                    : null;

                // 错误 proof 也必须先服从最终取消/截止，不能抢先返回 InvalidProof。
                unavailable = CheckAvailability(sessionId, entry, cancellationToken);
                if (unavailable is not null)
                {
                    return unavailable.Value;
                }

                if (!proofMatches)
                {
                    return VideoAttachStatus.InvalidProof;
                }

                entry.VideoAttached = true;
                lease = candidate;
                return VideoAttachStatus.Attached;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expectedProof);
            }
        }
    }

    // 仅在 _gate 内调用。生产 TimeProvider 必须快速且可信；不承诺中断阻塞的本机时钟。
    private VideoAttachStatus? CheckAvailability(Guid sessionId, Entry entry, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || entry.ControlCancellationToken.IsCancellationRequested)
        {
            return VideoAttachStatus.Cancelled;
        }

        bool expired = entry.AttachWindow.IsExpired;
        // 取时可以被测试时钟重入：其后不得只检查缓存的 entry / 未消费状态。
        if (cancellationToken.IsCancellationRequested || entry.ControlCancellationToken.IsCancellationRequested)
        {
            return VideoAttachStatus.Cancelled;
        }

        if (!_sessions.TryGetValue(sessionId, out Entry? current) || !ReferenceEquals(current, entry))
        {
            return VideoAttachStatus.Unavailable;
        }

        if (expired)
        {
            return VideoAttachStatus.Expired;
        }

        return entry.VideoAttached ? VideoAttachStatus.AlreadyAttached : null;
    }

    private void Unregister(Guid sessionId, object entryIdentity)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out Entry? entry) && ReferenceEquals(entry, entryIdentity))
            {
                _sessions.Remove(sessionId);
                CryptographicOperations.ZeroMemory(entry.SessionToken);
                // TCS 强制异步 continuation；锁内不调用取消源或任何 handler。
                entry.Revoked.TrySetResult();
            }
        }
    }

    /// <summary>全部可变状态由登记表锁保护；token 仅在本私有条目内借用。</summary>
    private sealed class Entry
    {
        public Entry(
            ControlSessionSummary summary,
            byte[] sessionToken,
            VideoAttachWindow attachWindow,
            byte[] controlCertificateSha256,
            CancellationToken controlCancellationToken)
        {
            Summary = summary;
            SessionToken = sessionToken;
            AttachWindow = attachWindow;
            ControlCertificateSha256 = controlCertificateSha256;
            // 固化控制端 IPv4 字节，比较时不调用可覆写的 IPAddress.Equals。
            ControlRemoteAddress = summary.RemoteAddress.GetAddressBytes();
            ControlCancellationToken = controlCancellationToken;
        }

        public ControlSessionSummary Summary { get; }
        public byte[] SessionToken { get; }
        public VideoAttachWindow AttachWindow { get; }
        public byte[] ControlCertificateSha256 { get; }
        public byte[] ControlRemoteAddress { get; }
        public CancellationToken ControlCancellationToken { get; }
        public bool VideoAttached { get; set; }
        public TaskCompletionSource Revoked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// 会话的注销句柄（幂等）。
    /// </summary>
    /// <remarks>与 <see cref="AdmissionLease"/> 同一风格：连接处理路径有多条退出分支，
    /// 幂等才能让调用方到处写 <c>Dispose</c> 而不必追踪是否已注销。</remarks>
    internal sealed class SessionRegistration : IDisposable
    {
        private readonly SessionRegistry _registry;
        private readonly Guid _sessionId;
        private readonly object _entryIdentity;
        private int _released;

        internal SessionRegistration(SessionRegistry registry, Guid sessionId, object entryIdentity)
        {
            _registry = registry;
            _sessionId = sessionId;
            _entryIdentity = entryIdentity;
        }

        /// <summary>本句柄对应的会话 id。</summary>
        public Guid SessionId => _sessionId;

        /// <summary>注销；重复调用无副作用。</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _registry.Unregister(_sessionId, _entryIdentity);
            }
        }
    }
}
