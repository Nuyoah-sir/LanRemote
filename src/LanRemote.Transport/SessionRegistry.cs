using System.Net;
using System.Security.Cryptography;
using LanRemote.Core.Models;

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
        ReadOnlySpan<byte> sessionToken)
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
            sessionToken.ToArray());

        lock (_gate)
        {
            // 会话 id 冲突 = 编程错误（每会话一个 Guid）；Add 会抛，不静默覆盖。
            _sessions.Add(sessionId, entry);
        }

        return new SessionRegistration(this, sessionId);
    }

    /// <summary>
    /// 读取登记中的 sessionToken（<b>仅程序集内部</b>：M5 的 video attach 校验用；
    /// 绝不出公开面、绝不落日志）。
    /// </summary>
    internal bool TryGetSessionToken(Guid sessionId, out ReadOnlyMemory<byte> token)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out Entry? entry))
            {
                token = entry.SessionToken;
                return true;
            }

            token = default;
            return false;
        }
    }

    private void Unregister(Guid sessionId)
    {
        Entry? entry;
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out entry))
            {
                _sessions.Remove(sessionId);
            }
        }

        // 会话断开立即废弃：token 当场清零（放在锁外也无妨——条目已摘除，外人拿不到引用）。
        if (entry is not null)
        {
            CryptographicOperations.ZeroMemory(entry.SessionToken);
        }
    }

    /// <summary>登记条目：非秘密摘要 + 内存中的 token 副本。</summary>
    private sealed class Entry
    {
        public Entry(ControlSessionSummary summary, byte[] sessionToken)
        {
            Summary = summary;
            SessionToken = sessionToken;
        }

        public ControlSessionSummary Summary { get; }

        public byte[] SessionToken { get; }
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
        private int _released;

        internal SessionRegistration(SessionRegistry registry, Guid sessionId)
        {
            _registry = registry;
            _sessionId = sessionId;
        }

        /// <summary>本句柄对应的会话 id。</summary>
        public Guid SessionId => _sessionId;

        /// <summary>注销；重复调用无副作用。</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _registry.Unregister(_sessionId);
            }
        }
    }
}
