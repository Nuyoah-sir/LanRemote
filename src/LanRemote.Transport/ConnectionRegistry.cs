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
/// 卡在不可中断的读里时不够。两段等待各有 deadline；同步取消回调与资源释放异步发起，
/// 完整回收时仍会 join 原任务。</description></item>
/// </list>
/// </remarks>
public sealed class ConnectionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<int, Entry> _entries = new();
    private readonly CancellationTokenSource _shutdown = new();

    private readonly List<Task> _forceDisposals = new();
    private Task? _cancellationTask;
    private int _nextId;
    private bool _stopping;
    private bool _canceling;

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

            _entries.Add(id, new Entry(id, linked, completion, resources));
            return new ConnectionRegistration(this, id, linked, completion);
        }
    }

    /// <summary>
    /// 停止并等待所有已登记连接结束。
    /// </summary>
    /// <param name="timeout">两段等待的总预算；原取消与物理关闭回调仍由完整释放路径继续跟踪。</param>
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
        Task cancellation;
        lock (_gate)
        {
            if (!_stopping)
            {
                _stopping = true;
                _canceling = true;
                snapshot = _entries.Values.ToArray();
                // 已认领的 linked CTS 必须先解绑；取消回调可能同步阻塞，不能拦住 force。
                Task[] pendingDisposals = snapshot.Where(entry => entry.CancellationDisposeStarted)
                    .Select(entry => entry.CancellationDisposed.Task).ToArray();
                _cancellationTask = Task.Run(() => CancelCoreAsync(snapshot, pendingDisposals));
            }
            else
            {
                snapshot = _entries.Values.ToArray();
            }

            cancellation = _cancellationTask!;
        }

        await WaitAllAsync(snapshot, cancellation, Array.Empty<Task>(), Half(timeout)).ConfigureAwait(false);

        // 即使根取消回调仍阻塞，也要在半预算后独立发出物理关闭请求。
        Task[] forced;
        lock (_gate)
        {
            foreach (Entry entry in snapshot)
            {
                if (entry.RegistrationDisposed || entry.ForceDisposeStarted)
                {
                    continue;
                }

                entry.ForceDisposeStarted = true;
                entry.ForceDisposeInProgress = true;
                entry.ForceDisposeTask = Task.Run(() => ForceDispose(entry));
                _forceDisposals.Add(entry.ForceDisposeTask);
            }

            forced = snapshot.Where(entry => entry.ForceDisposeTask is not null)
                .Select(entry => entry.ForceDisposeTask!).ToArray();
        }

        await WaitAllAsync(snapshot, cancellation, forced, timeout - Half(timeout)).ConfigureAwait(false);

        List<Exception> errors = new();
        int unfinished = 0;
        foreach (Entry entry in snapshot)
        {
            if (!entry.Completion.Task.IsCompletedSuccessfully ||
                !entry.CancellationDisposed.Task.IsCompletedSuccessfully)
            {
                unfinished++;
            }

            if (entry.CancellationDisposed.Task.Exception is { } failure)
            {
                errors.AddRange(failure.InnerExceptions);
            }
        }

        if (cancellation.Exception is { } cancellationFailure)
        {
            errors.AddRange(cancellationFailure.InnerExceptions);
        }

        foreach (Task force in forced)
        {
            if (force.Exception is { } failure)
            {
                errors.AddRange(failure.InnerExceptions);
            }
        }

        // 取消或物理关闭超预算时原 Task 继续由完整释放路径持有；
        // 每条连接只有 Completion 与 CancellationDisposed 均成功才算完成。

        if (errors.Count != 0)
        {
            throw new AggregateException("连接停机清理失败。", errors);
        }

        return new ConnectionStopReport(snapshot.Length, unfinished);
    }

    private async Task CancelCoreAsync(Entry[] snapshot, Task[] pendingDisposals)
    {
        List<Exception> errors = new();
        try
        {
            Task all = Task.WhenAll(pendingDisposals);
            try
            {
                await all.ConfigureAwait(false);
            }
            catch
            {
                if (all.Exception is { } failure)
                {
                    errors.AddRange(failure.InnerExceptions);
                }
            }

            try
            {
                _shutdown.Cancel();
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }
        finally
        {
            lock (_gate)
            {
                _canceling = false;
            }

            foreach (Entry entry in snapshot)
            {
                try
                {
                    DisposeCompletedEntry(entry);
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
            }
        }

        if (errors.Count != 0)
        {
            throw new AggregateException("连接根取消清理失败。", errors);
        }
    }

    private void ForceDispose(Entry entry)
    {
        List<Exception> errors = new();
        try
        {
            entry.Resources.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
        finally
        {
            lock (_gate)
            {
                entry.ForceDisposeInProgress = false;
            }

            try
            {
                DisposeCompletedEntry(entry);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }

        if (errors.Count != 0)
        {
            throw new AggregateException("连接物理关闭失败。", errors);
        }
    }

    /// <summary>Host 已完整 join 所有 Stop 后，再 join 原取消、强制释放及各租约。</summary>
    internal async Task JoinAndDisposeAsync()
    {
        Entry[] snapshot;
        Task[] forced;
        Task cancellation;
        lock (_gate)
        {
            if (!_stopping)
            {
                throw new InvalidOperationException("必须先发起连接停机。");
            }

            snapshot = _entries.Values.ToArray();
            forced = _forceDisposals.ToArray();
            cancellation = _cancellationTask!;
        }

        Task[] tasks = snapshot.SelectMany(entry =>
            new[] { entry.Completion.Task, entry.CancellationDisposed.Task })
            .Concat(forced).Append(cancellation).ToArray();
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // WhenAll 的 await 只抛首个故障；下面逐项保留全部原错误树。
        }

        List<Exception> errors = new();
        foreach (Task task in tasks)
        {
            if (task.Exception is { } failure)
            {
                errors.AddRange(failure.InnerExceptions);
            }
        }

        // 根回调与全部强制释放已退出、所有 linked CTS 已解绑，才可释放根。
        if (cancellation.IsCompleted && forced.All(task => task.IsCompleted) &&
            snapshot.All(entry => entry.CancellationDisposed.Task.IsCompletedSuccessfully))
        {
            try
            {
                _shutdown.Dispose();
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }
        else
        {
            errors.Add(new InvalidOperationException(
                "连接根取消源未释放：仍有 linked CTS 解绑失败或取消/物理关闭尚未结束。"));
        }

        if (errors.Count != 0)
        {
            throw new AggregateException("连接完整停机清理失败。", errors);
        }
    }

    internal void Remove(int id)
    {
        Entry? entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out entry))
            {
                return;
            }

            entry.RegistrationDisposed = true;
            // 必须先发布完成再允许摘表；否则新 Stop 可取得空表而旧快照仍等完成通知。
            // continuation 异步派发，不在锁内执行调用方代码。
            entry.Completion.TrySetResult();
        }

        DisposeCompletedEntry(entry);
    }

    private void DisposeCompletedEntry(Entry entry)
    {
        lock (_gate)
        {
            if (!entry.RegistrationDisposed || entry.ForceDisposeInProgress ||
                _canceling || entry.CancellationDisposeStarted)
            {
                return;
            }

            entry.CancellationDisposeStarted = true;
        }

        // linked CTS.Dispose 可能等待父取消回调，绝不能持锁或与根取消交叉执行。
        Exception? failure = null;
        try
        {
            entry.Cancellation.Dispose();
        }
        catch (Exception error)
        {
            failure = error;
        }

        lock (_gate)
        {
            if (failure is null)
            {
                _entries.Remove(entry.Id);
                // 强制 Dispose 已退出且 CTS 已回收，其他 Stop 才能报告完成。
                entry.CancellationDisposed.TrySetResult();
            }
            else
            {
                // 回收失败不能伪装成 Count=0；故障完成通知，避免完整 join 永久悬挂。
                entry.CancellationDisposed.TrySetException(failure);
            }
        }
    }

    private static async Task WaitAllAsync(
        Entry[] entries, Task cancellation, Task[] forced, TimeSpan timeout)
    {
        Task all = Task.WhenAll(entries.SelectMany(entry =>
            new[] { entry.Completion.Task, entry.CancellationDisposed.Task })
            .Concat(forced).Append(cancellation));
        try
        {
            await all.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 同步取消回调及资源释放不能拖住预算；未结束状态在报告阶段明确处理。
        }
        catch (Exception) when (all.IsFaulted)
        {
            // 清理故障由 StopAllAsync 在 force 已发出后逐项上报。
        }
    }

    private static TimeSpan Half(TimeSpan value) => TimeSpan.FromTicks(value.Ticks / 2);

    private sealed class Entry(
        int id,
        CancellationTokenSource cancellation,
        TaskCompletionSource completion,
        IDisposable resources)
    {
        public int Id { get; } = id;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public TaskCompletionSource Completion { get; } = completion;

        public IDisposable Resources { get; } = resources;

        public TaskCompletionSource CancellationDisposed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        // 以下状态只在 _gate 内访问。
        public bool RegistrationDisposed { get; set; }

        public bool ForceDisposeStarted { get; set; }

        public bool ForceDisposeInProgress { get; set; }

        public Task? ForceDisposeTask { get; set; }

        public bool CancellationDisposeStarted { get; set; }
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
/// 它标记租约完成；若根取消或强制释放仍在进行，CTS 回收和表项移除由对应操作收尾。</para>
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
