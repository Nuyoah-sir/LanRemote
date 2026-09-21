namespace LanRemote.Transport;

/// <summary>
/// 有界的活动连接登记表。
/// </summary>
/// <remarks>
/// <para><b>评审 A-18</b>：连接任务必须有界、可观测、可 join。
/// 否则 Host 停机时会有孤儿 handler 继续持有 socket，重启立刻撞端口。</para>
/// <para>停机分两步，不是「优雅等一会儿就算了」：</para>
/// <list type="number">
/// <item><description>先<b>取消</b>，给 handler 自己收尾的机会；</description></item>
/// <item><description>仍没结束的直接<b>砸掉底层 socket</b>——取消只是请求，
/// 卡在不可中断的读里时不够。两步都有 deadline，整体不超过传入的停机预算。</description></item>
/// </list>
/// </remarks>
public sealed class ConnectionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<int, Entry> _entries = new();
    private readonly CancellationTokenSource _shutdown = new();

    private int _nextId;
    private bool _stopping;

    /// <summary>当前登记的连接数。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>是否正在/已经停机。</summary>
    public bool IsStopping
    {
        get
        {
            lock (_gate)
            {
                return _stopping;
            }
        }
    }

    /// <summary>
    /// 登记一条连接。
    /// </summary>
    /// <param name="resources">该连接持有的资源；停机超时未结束时会被强制释放以打断阻塞的读。</param>
    /// <returns>登记成功则为租约对象；正在停机时为 <see langword="null"/>（此时不该再收新连接）。</returns>
    public ConnectionRegistration? TryRegister(IDisposable resources)
    {
        ArgumentNullException.ThrowIfNull(resources);

        lock (_gate)
        {
            if (_stopping)
            {
                return null;
            }

            int id = ++_nextId;
            CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);

            TaskCompletionSource completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            _entries.Add(id, new Entry(linked, completion, resources));
            return new ConnectionRegistration(this, id, linked, completion);
        }
    }

    /// <summary>
    /// 停止并等待所有已登记连接结束。
    /// </summary>
    /// <param name="timeout">总停机预算。</param>
    /// <returns>停机报告：总数、预算内未结束数、是否全部干净结束。</returns>
    /// <remarks>
    /// <b>不要用 bool 表达停机结局</b>（评审 B18）：预算超限与干净成功必须可区分，
    /// 且要给出<b>未完成计数</b>——只报「没干净」而不说几条没干净，等于把后续判断推给运气。
    /// </remarks>
    public async Task<ConnectionStopReport> StopAllAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "停机预算必须为正。");
        }

        Entry[] snapshot;
        lock (_gate)
        {
            _stopping = true;
            snapshot = _entries.Values.ToArray();
        }

        // 阶段 1：只取消。
        _shutdown.Cancel();
        await WaitAllAsync(snapshot, Half(timeout)).ConfigureAwait(false);

        // 阶段 2：还没结束的，砸掉 socket 打断阻塞读。
        foreach (Entry entry in snapshot)
        {
            try
            {
                entry.Resources.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        await WaitAllAsync(snapshot, Half(timeout)).ConfigureAwait(false);

        lock (_gate)
        {
            _entries.Clear();
        }

        foreach (Entry entry in snapshot)
        {
            entry.Cancellation.Dispose();
        }

        int unfinished = 0;
        foreach (Entry entry in snapshot)
        {
            if (!entry.Completion.Task.IsCompletedSuccessfully)
            {
                unfinished++;
            }
        }

        return new ConnectionStopReport(snapshot.Length, unfinished);
    }

    internal void Remove(int id)
    {
        lock (_gate)
        {
            _entries.Remove(id);
        }
    }

    private static async Task WaitAllAsync(Entry[] entries, TimeSpan timeout)
    {
        if (entries.Length == 0)
        {
            return;
        }

        Task all = Task.WhenAll(entries.Select(entry => entry.Completion.Task));
        try
        {
            await all.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 预算内没结束：交给下一阶段兜底，或如实返回 false。
        }
    }

    private static TimeSpan Half(TimeSpan value) => TimeSpan.FromTicks(value.Ticks / 2);

    private sealed class Entry(
        CancellationTokenSource cancellation,
        TaskCompletionSource completion,
        IDisposable resources)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;

        public TaskCompletionSource Completion { get; } = completion;

        public IDisposable Resources { get; } = resources;
    }
}

/// <summary>
/// 一次连接停机的报告。
/// </summary>
/// <param name="Total">停机时登记表中的连接总数。</param>
/// <param name="Unfinished">预算内没有结束的连接数。</param>
/// <remarks>
/// 评审 B18：停机必须能区分「干净成功」与「预算超限」，并给出未完成计数。
/// </remarks>
public sealed record ConnectionStopReport(int Total, int Unfinished)
{
    /// <summary>是否全部在预算内结束（干净成功）。</summary>
    public bool AllFinished => Unfinished == 0;
}

/// <summary>
/// 一条已登记连接的句柄。
/// </summary>
/// <remarks>
/// <para>连接结束时<b>必须</b>调用 <see cref="Dispose"/>（幂等）：
/// 它同时完成「从登记表移除」和「通知停机等待者」两件事。</para>
/// <para>调用方仍然自己负责释放底层 socket；登记表只在停机超时未结束时才代为强制释放。</para>
/// </remarks>
public sealed class ConnectionRegistration : IDisposable
{
    private readonly ConnectionRegistry _registry;
    private readonly int _id;
    private readonly CancellationTokenSource _cancellation;
    private readonly TaskCompletionSource _completion;
    private int _done;

    internal ConnectionRegistration(
        ConnectionRegistry registry,
        int id,
        CancellationTokenSource cancellation,
        TaskCompletionSource completion)
    {
        _registry = registry;
        _id = id;
        _cancellation = cancellation;
        _completion = completion;
    }

    /// <summary>连接专属取消令牌；Host 停机时会被触发。</summary>
    public CancellationToken Cancellation => _cancellation.Token;

    /// <summary>登记表内部编号（用于诊断）。</summary>
    public int Id => _id;

    /// <summary>标记本连接已结束；幂等。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _done, 1) == 0)
        {
            _registry.Remove(_id);
            _completion.TrySetResult();
        }
    }
}
