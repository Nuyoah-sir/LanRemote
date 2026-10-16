using System.Net;
using System.Security.Cryptography;
using LanRemote.Core.Abstractions;
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
        CancellationToken controlCancellationToken,
        DisplayCatalogAgreementBinding agreement)
    {
        ArgumentNullException.ThrowIfNull(agreement);
        if (agreement.SessionId != sessionId)
            throw new ArgumentException("目录约定不属于本控制会话。", nameof(agreement));
        // 仅约束登记入口；约定对象本身不是授权，调用点仍须完成 V2 双向证明、审批和成功写出。
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
                return VideoAttachStatus.NotRegistered;
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

                // 最终核对之后在同一把锁内预约子连接，父句柄持有该单次租约直至子连接收尾。
                VideoAttachLease candidate = new(sessionId, videoSecurity.ConnectionId, entry.Revoked.Task, _gate);
                entry.VideoLease = candidate;
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

    /// <summary>
    /// 为已经附着的真实租约发布一次换代入口；不接管 source，不启动/停止生产者。
    /// 调用方须在 ACK 完整写出及初始生产者安装成功后发布，退出先摘槽，再完整停止并 join。
    /// 此入口不验证网络 ACK，也不代表发送或显示屏障。摘槽后禁止重新发布。
    /// </summary>
    internal IDisposable PublishVideoSource(VideoAttachLease lease, ReplaceableVideoFrameSource source)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(source);
        if (source.SessionId != lease.SessionId)
            throw new ArgumentException("视频来源不属于本租约会话。", nameof(source));
        lock (_gate)
        {
            if (!_sessions.TryGetValue(lease.SessionId, out Entry? entry) ||
                !ReferenceEquals(entry.VideoLease, lease) || entry.ControlCancellationToken.IsCancellationRequested ||
                lease.Completed.IsCompleted || lease.Revoked.IsCompleted)
                throw new InvalidOperationException("视频租约已失效或不属于本登记表。");
            if (entry.VideoSourcePublished)
                throw new InvalidOperationException("本视频租约已发布过换代入口。");
            VideoSourcePublication publication = new(this, entry);
            entry.VideoSourcePublished = true;
            entry.VideoSource = source;
            return publication;
        }
    }

    // 只摘可调用入口；不回收已接管候选，也不停止/等待 source 或提前完成 VideoLease。
    private sealed class VideoSourcePublication(SessionRegistry registry, Entry entry) : IDisposable
    {
        public void Dispose()
        {
            lock (registry._gate) entry.VideoSource = null;
        }
    }

    private void Unregister(Guid sessionId, Entry entryIdentity)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out Entry? entry) && ReferenceEquals(entry, entryIdentity))
            {
                _sessions.Remove(sessionId);
                entry.VideoSource = null;
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
        public VideoAttachLease? VideoLease { get; set; }
        public bool VideoSourcePublished { get; set; }
        public ReplaceableVideoFrameSource? VideoSource { get; set; }
        public SessionRegistration.DisplaySwitchAdmission? DisplaySwitchAdmission { get; set; }
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
        private readonly Entry _entry;
        private int _released;

        internal SessionRegistration(SessionRegistry registry, Guid sessionId, object entryIdentity)
        {
            _registry = registry;
            _sessionId = sessionId;
            _entry = (Entry)entryIdentity;
        }

        /// <summary>本句柄对应的会话 id。</summary>
        public Guid SessionId => _sessionId;

        /// <summary>本句柄预约的视频子连接；未预约时已完成，预约后返回同一个完成任务。</summary>
        internal Task VideoCompletion
        {
            get
            {
                lock (_registry._gate)
                {
                    return _entry.VideoLease?.Completed ?? Task.CompletedTask;
                }
            }
        }

        /// <summary>
        /// 同步核对父资格并交给已发布来源；正常返回原 Task 即候选已移交，异步失败不返还。
        /// 同步拒绝不接管新对象。仅 internal 接缝，无排队、超时代理、网络消息或帧屏障。
        /// </summary>
        internal Task ReplaceVideoProducerAsync(IVideoFrameProducer next) => ReplaceVideoProducerAsync(next, out _);

        internal Task ReplaceVideoProducerAsync(IVideoFrameProducer next,
            out ReplaceableVideoFrameSource.GenerationIdentity generation)
        {
            generation = null!;
            ArgumentNullException.ThrowIfNull(next);
            lock (_registry._gate)
            {
                if (_released != 0 || !_registry._sessions.TryGetValue(_sessionId, out Entry? current) ||
                    !ReferenceEquals(current, _entry) || _entry.ControlCancellationToken.IsCancellationRequested ||
                    _entry.VideoLease is not { } lease || lease.Completed.IsCompleted || lease.Revoked.IsCompleted ||
                    _entry.VideoSource is not { } source)
                    throw new InvalidOperationException("父会话未持有活动的视频换代入口。");
                // 来源仅在自身 gate 发布 worker，不会同步调用外部生产者；锁序为 registry → source。
                return source.ReplaceAsync(next, out generation);
            }
        }

        /// <summary>
        /// 显式绑定本父会话的Host目录与单槽请求准入；同一entry至多创建一次，关闭后不可重开。
        /// 目录必须来自本Host的已验证快照；这里不证明目录已发送、拓扑新鲜或目标可采集。
        /// </summary>
        internal DisplaySwitchAdmission CreateDisplaySwitchAdmission(DisplayCatalogFrame catalog)
        {
            ArgumentNullException.ThrowIfNull(catalog);
            lock (_registry._gate)
            {
                if (!IsDisplaySwitchAvailable())
                    throw new InvalidOperationException("父会话未持有活动的视频换代入口。");
                if (_entry.DisplaySwitchAdmission is not null)
                    throw new InvalidOperationException("本会话已创建过切屏请求准入。");
                var admission = new DisplaySwitchAdmission(this, catalog);
                _entry.DisplaySwitchAdmission = admission;
                return admission;
            }
        }

        // 只在registry gate内使用；不调外部接口、不等待任务、不改变租约生命周期。
        private bool IsDisplaySwitchAvailable() =>
            _released == 0 && _registry._sessions.TryGetValue(_sessionId, out Entry? current) &&
            ReferenceEquals(current, _entry) && !_entry.ControlCancellationToken.IsCancellationRequested &&
            _entry.VideoLease is { } lease && !lease.Completed.IsCompleted && !lease.Revoked.IsCompleted &&
            _entry.VideoSource is { } source && !source.Termination.IsCompleted;

        /// <summary>仅内部准入结果，不是wire错误码或切屏完成回执。</summary>
        internal enum DisplaySwitchAdmissionStatus
        {
            Accepted,
            Unavailable,
            SessionMismatch,
            Busy,
            StaleSequence,
            CatalogMismatch,
            DisplayNotFound,
        }

        /// <summary>
        /// 无资源的单会话准入核；不是factory/Replace协调worker，不创建Task/计时器或后台队列。
        /// 仅accepted消耗序号；busy或无效请求不消费。预约释放不代表换代、发送或显示完成。
        /// 同一会话只绑定一个Host目录快照；seq单调性不替代认证，catalogSequence不证明拓扑新鲜。
        /// </summary>
        internal sealed class DisplaySwitchAdmission : IDisposable
        {
            private readonly SessionRegistration _registration;
            private readonly DisplayCatalogFrame _catalog;
            private long _lastSequence = -1;
            private bool _closed;
            private DisplaySwitchReservation? _active;
            private DisplaySwitchExecutor? _executor;

            internal DisplaySwitchAdmission(SessionRegistration registration, DisplayCatalogFrame catalog)
            {
                _registration = registration;
                _catalog = catalog;
            }

            /// <summary>一次装配拥有型执行器；已有手动预约时拒绝，不接管该预约。</summary>
            internal DisplaySwitchExecutor CreateExecutor(
                Func<DisplayInfo, CancellationToken, Task<IVideoFrameProducer>> prepare) =>
                CreateExecutorCore(prepare, trackSend: false, replyMode: false);

            /// <summary>只附带请求/代际历史句柄，不自动装配追踪sender；执行Task不等待首帧，不扩张单飞或当前权限合同。</summary>
            internal DisplaySwitchExecutor CreateTrackedExecutor(
                Func<DisplayInfo, CancellationToken, Task<IVideoFrameProducer>> prepare) =>
                CreateExecutorCore(prepare, trackSend: true, replyMode: false);

            /// <summary>显式将准入槽保留到回执原任务退出；不自动接入Control writer或产品路径。</summary>
            internal DisplaySwitchExecutor CreateReplyExecutor(
                Func<DisplayInfo, CancellationToken, Task<IVideoFrameProducer>> prepare) =>
                CreateExecutorCore(prepare, trackSend: true, replyMode: true);

            private DisplaySwitchExecutor CreateExecutorCore(
                Func<DisplayInfo, CancellationToken, Task<IVideoFrameProducer>> prepare, bool trackSend, bool replyMode)
            {
                ArgumentNullException.ThrowIfNull(prepare);
                lock (_registration._registry._gate)
                {
                    if (_closed || !ReferenceEquals(_registration._entry.DisplaySwitchAdmission, this) ||
                        !_registration.IsDisplaySwitchAvailable() || _active is not null || _executor is not null)
                        throw new InvalidOperationException("切屏请求执行器不可创建。");
                    return _executor = new DisplaySwitchExecutor(_registration._registry._gate, this, prepare, trackSend, replyMode);
                }
            }

            internal bool OwnsExecutor(DisplaySwitchExecutor executor)
            {
                lock (_registration._registry._gate) return ReferenceEquals(_executor, executor);
            }

            internal void CloseExecutor(DisplaySwitchExecutor executor)
            {
                lock (_registration._registry._gate)
                    if (ReferenceEquals(_executor, executor)) _closed = true;
            }

            internal DisplaySwitchAdmissionStatus TryReserve(
                DisplaySwitchRequestFrame request, out DisplaySwitchReservation? reservation) =>
                TryReserveCore(null, request, out reservation);

            internal DisplaySwitchAdmissionStatus TryReserveForExecutor(DisplaySwitchExecutor executor,
                DisplaySwitchRequestFrame request, out DisplaySwitchReservation? reservation)
            {
                ArgumentNullException.ThrowIfNull(executor);
                return TryReserveCore(executor, request, out reservation);
            }

            private DisplaySwitchAdmissionStatus TryReserveCore(DisplaySwitchExecutor? executor,
                DisplaySwitchRequestFrame request, out DisplaySwitchReservation? reservation)
            {
                ArgumentNullException.ThrowIfNull(request);
                reservation = null;
                lock (_registration._registry._gate)
                {
                    if (_closed || !ReferenceEquals(_executor, executor) ||
                        !ReferenceEquals(_registration._entry.DisplaySwitchAdmission, this) ||
                        !_registration.IsDisplaySwitchAvailable())
                        return DisplaySwitchAdmissionStatus.Unavailable;
                    if (request.SessionId != _registration.SessionId)
                        return DisplaySwitchAdmissionStatus.SessionMismatch;
                    if (_active is not null || _executor?.HasActiveExecution == true)
                        return DisplaySwitchAdmissionStatus.Busy;
                    if (request.Sequence <= _lastSequence) return DisplaySwitchAdmissionStatus.StaleSequence;
                    if (request.CatalogSequence != _catalog.Sequence)
                        return DisplaySwitchAdmissionStatus.CatalogMismatch;
                    DisplayInfo? target = null;
                    foreach (DisplayInfo display in _catalog.Displays)
                        if (display.Id == request.DisplayId) { target = display; break; }
                    if (target is null) return DisplaySwitchAdmissionStatus.DisplayNotFound;
                    var candidate = new DisplaySwitchReservation(this, request, target);
                    _lastSequence = request.Sequence;
                    _active = candidate;
                    reservation = candidate;
                    return DisplaySwitchAdmissionStatus.Accepted;
                }
            }

            /// <summary>仅封口后续准入；不撤回持有者的预约，不停止来源或注销父会话。</summary>
            public void Dispose()
            {
                lock (_registration._registry._gate) _closed = true;
            }

            internal sealed class DisplaySwitchReservation : IDisposable
            {
                private readonly DisplaySwitchAdmission _owner;
                private bool _released;

                internal DisplaySwitchReservation(DisplaySwitchAdmission owner,
                    DisplaySwitchRequestFrame request, DisplayInfo target)
                {
                    _owner = owner;
                    Request = request;
                    Target = target;
                }

                internal DisplaySwitchRequestFrame Request { get; }
                internal DisplayInfo Target { get; }

                /// <summary>同一父gate核预约/执行器/父资格并移交；返回原Task，不调用外部生产者。</summary>
                internal Task ReplaceProducerAsync(DisplaySwitchExecutor executor, IVideoFrameProducer producer) =>
                    ReplaceProducerAsync(executor, producer, out _);

                internal Task ReplaceProducerAsync(DisplaySwitchExecutor executor, IVideoFrameProducer producer,
                    out ReplaceableVideoFrameSource.GenerationIdentity generation)
                {
                    generation = null!;
                    ArgumentNullException.ThrowIfNull(executor);
                    ArgumentNullException.ThrowIfNull(producer);
                    lock (_owner._registration._registry._gate)
                    {
                        if (!ReferenceEquals(_owner._executor, executor) || !IsCurrent)
                            throw new InvalidOperationException("切屏请求预约已失效。");
                        return _owner._registration.ReplaceVideoProducerAsync(producer, out generation);
                    }
                }

                // 调用者必须继续持有同一registry gate登记回执worker；不调用外部委托。
                internal bool TryCommitReply(DisplaySwitchExecutor executor,
                    ReplaceableVideoFrameSource.GenerationIdentity generation)
                {
                    lock (_owner._registration._registry._gate)
                        return ReferenceEquals(_owner._executor, executor) && IsCurrent &&
                            _owner._registration._entry.VideoSource is { } source && source.TryCommitReply(generation) && IsCurrent;
                }

                /// <summary>瞬时可继续快照，不是跨锁授权；真实Replace仍需原父gate最终校验。</summary>
                internal bool IsCurrent
                {
                    get
                    {
                        lock (_owner._registration._registry._gate)
                            return !_released && !_owner._closed && ReferenceEquals(_owner._active, this) &&
                                _owner._registration.IsDisplaySwitchAvailable();
                    }
                }

                public void Dispose()
                {
                    lock (_owner._registration._registry._gate)
                    {
                        if (_released) return;
                        _released = true;
                        if (ReferenceEquals(_owner._active, this)) _owner._active = null;
                    }
                }
            }
        }

        /// <summary>注销；重复调用无副作用。</summary>
        public void Dispose()
        {
            // 注销标记与摘表/清零必须一同线性化；并发重复 Dispose 不能在首个调用
            // 尚未取得登记表锁时提前返回，让父连接误以为注销和子预约核对已结束。
            lock (_registry._gate)
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                {
                    _registry.Unregister(_sessionId, _entry);
                }
            }
        }
    }
}
