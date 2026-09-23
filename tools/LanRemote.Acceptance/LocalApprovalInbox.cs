using System.Net;
using System.Net.Sockets;
using LanRemote.Core.Models;
using LanRemote.Transport;

namespace LanRemote.Acceptance;

/// <summary>UI 拉取的单条快照；点击必须同时回传 RequestId 和本条 Generation。</summary>
internal sealed record LocalApprovalSnapshot(LocalApprovalRequest Request, Guid Generation)
{
    public Guid RequestId => Request.RequestId;
}

/// <summary>
/// 每个 run 新建的有界审批收件箱；Stop/Dispose 永久关闭，不提供重新启动入口。
/// 只保存待批项，不投递 Dispatcher 操作，也不保存历史决定。没有操作者提交就不会批准；
/// 宿主在审批 UI 不可用时应 Stop，此后请求返回 Unavailable。
/// </summary>
internal sealed class LocalApprovalInbox : ILocalApprovalGate, IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Entry> _pending = new();
    private bool _stopped;

    public LocalApprovalInbox(int capacity = 3)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
    }

    public int Capacity { get; }

    public int PendingCount
    {
        get
        {
            lock (_sync)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>只拉取此刻待批项；快照可以过时，不能绕过提交时的身份与终态检查。</summary>
    public IReadOnlyList<LocalApprovalSnapshot> GetSnapshot()
    {
        lock (_sync)
        {
            return _pending.Values.Select(entry => new LocalApprovalSnapshot(
                CopyRequest(entry.Request), entry.Generation)).ToArray();
        }
    }

    public ValueTask<LocalApprovalDecision> RequestApprovalAsync(
        LocalApprovalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Entry entry;
        lock (_sync)
        {
            if (_stopped)
            {
                return Immediate(request.RequestId, LocalApprovalOutcome.Unavailable);
            }
            if (cancellationToken.IsCancellationRequested)
            {
                return Immediate(request.RequestId, LocalApprovalOutcome.Cancelled);
            }
            if (_pending.Count >= Capacity || _pending.ContainsKey(request.RequestId))
            {
                return Immediate(request.RequestId, LocalApprovalOutcome.Unavailable);
            }

            entry = new Entry(this, CopyRequest(request), cancellationToken);
            _pending.Add(request.RequestId, entry);
        }

        // 注册可能立即同步执行回调，故必须在锁外；不捕获 UI 同步上下文或执行上下文。
        CancellationTokenRegistration registration = cancellationToken.UnsafeRegister(static state =>
        {
            Entry pending = (Entry)state!;
            pending.Owner.Cancel(pending.Request.RequestId, pending.Generation);
        }, entry);

        bool unregister;
        lock (_sync)
        {
            unregister = entry.IsTerminal;
            if (!unregister)
            {
                entry.Registration = registration;
            }
        }
        if (unregister)
        {
            // Stop/提交/同步取消可能抢先完成，迟到的注册仍由创建者负责释放。
            registration.Unregister();
        }

        return new ValueTask<LocalApprovalDecision>(entry.Completion.Task);
    }

    /// <summary>
    /// 只接受合法的批准（grant ≤ request）或无 grant 的拒绝。
    /// true 仅表示收件箱接受决定，不表示 Transport 已授权；未知、旧代、非法、已终态均为 false。
    /// </summary>
    public bool TrySubmit(
        Guid requestId,
        Guid generation,
        LocalApprovalOutcome outcome,
        SessionPermission? grantedPermission = null)
    {
        Entry entry;
        LocalApprovalDecision decision;
        lock (_sync)
        {
            if (!_pending.TryGetValue(requestId, out entry!) || entry.Generation != generation)
            {
                return false;
            }

            // 令牌已触发但回调尚未拿到锁时，取消仍优先于已观测的 UI 决定。
            if (entry.Token.IsCancellationRequested)
            {
                decision = new LocalApprovalDecision(requestId, LocalApprovalOutcome.Cancelled);
            }
            else
            {
                bool valid = outcome switch
                {
                    LocalApprovalOutcome.Approved => grantedPermission is { } granted
                        && IsGrantable(granted, entry.Request.RequestedPermission),
                    LocalApprovalOutcome.Denied => grantedPermission is null,
                    _ => false,
                };
                if (!valid)
                {
                    return false;
                }
                decision = new LocalApprovalDecision(requestId, outcome, grantedPermission);
            }
            DetachLocked(entry);
        }

        return Complete(entry, decision) != LocalApprovalOutcome.Cancelled;
    }

    /// <summary>取消指定代的请求；重复取消或旧快照不能影响后续复用同一 ID 的请求。</summary>
    public bool Cancel(Guid requestId, Guid generation)
    {
        Entry entry;
        lock (_sync)
        {
            if (!_pending.TryGetValue(requestId, out entry!) || entry.Generation != generation)
            {
                return false;
            }
            DetachLocked(entry);
        }
        Complete(entry, new LocalApprovalDecision(requestId, LocalApprovalOutcome.Cancelled));
        return true;
    }

    /// <summary>原子摘除全部待批项，锁外完成 Cancelled；此后新请求一律 Unavailable。</summary>
    public void Stop()
    {
        Entry[] entries;
        lock (_sync)
        {
            if (_stopped)
            {
                return;
            }
            _stopped = true;
            entries = _pending.Values.ToArray();
            _pending.Clear();
            foreach (Entry entry in entries)
            {
                entry.IsTerminal = true;
            }
        }
        foreach (Entry entry in entries)
        {
            Complete(entry, new LocalApprovalDecision(entry.Request.RequestId, LocalApprovalOutcome.Cancelled));
        }
    }

    public void Dispose() => Stop();

    private void DetachLocked(Entry entry)
    {
        entry.IsTerminal = true;
        _pending.Remove(entry.Request.RequestId);
    }

    private static LocalApprovalOutcome Complete(Entry entry, LocalApprovalDecision decision)
    {
        // 不用 Dispose：它可能等待正在运行且等 _sync 的回调，导致锁反转。
        // Unregister 不等待；已开始的回调自行退出。注册尚未发布则由 RequestApprovalAsync 清理。
        entry.Registration.Unregister();

        // 锁外收尾时再复核一次取消；这是收件箱的决定交接，不是授权接受时刻。
        // ExpiresAt 只展示：最终 deadline、断连与授权始终由 Transport 状态机复核。
        if (entry.Token.IsCancellationRequested)
        {
            decision = new LocalApprovalDecision(entry.Request.RequestId, LocalApprovalOutcome.Cancelled);
        }
        entry.Completion.TrySetResult(decision);
        return decision.Outcome;
    }

    private static bool IsGrantable(SessionPermission granted, SessionPermission requested) =>
        (granted, requested) is
            (SessionPermission.ViewOnly, SessionPermission.ViewOnly) or
            (SessionPermission.ViewOnly, SessionPermission.Control) or
            (SessionPermission.Control, SessionPermission.Control);

    private static ValueTask<LocalApprovalDecision> Immediate(Guid requestId, LocalApprovalOutcome outcome) =>
        ValueTask.FromResult(new LocalApprovalDecision(requestId, outcome));

    // record 中的 IPAddress 仍可变（例如 IPv6 ScopeId），不能让调用者或 UI 改动内部快照。
    private static LocalApprovalRequest CopyRequest(LocalApprovalRequest request) => request with
    {
        RemoteAddress = request.RemoteAddress.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPAddress(request.RemoteAddress.GetAddressBytes(), request.RemoteAddress.ScopeId)
            : new IPAddress(request.RemoteAddress.GetAddressBytes()),
    };

    private sealed class Entry(
        LocalApprovalInbox owner, LocalApprovalRequest request, CancellationToken token)
    {
        public LocalApprovalInbox Owner { get; } = owner;
        public LocalApprovalRequest Request { get; } = request;
        // 每条独立、跨 run 也不同；复用 RequestId 无需保存无界的已用 ID 集合。
        public Guid Generation { get; } = Guid.NewGuid();
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<LocalApprovalDecision> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration { get; set; }
        public bool IsTerminal { get; set; }
    }
}
