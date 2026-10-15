using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;

namespace LanRemote.Transport;

/// <summary>拥有专用生产者的单会话来源；先完整退役旧代，再启动候选，不把换代空窗当 EOF。</summary>
/// <remarks>
/// ReplaceAsync 正常返回 Task 即接管尚未 Start 的候选；异步失败不返还所有权。
/// 同步拒绝不接管新对象；重复提交已拥有的对象也不改变其归属。最多一个在途替换和一个读取。
/// 换代完成不是发送/显示屏障：已提交给读者的帧不再归本来源，不能收回。
/// 仅通过读到 EOF/真实故障或 Dispose 终止；不另行监视运行中 producer.Completion。
/// 不提供替换取消/超时代理。生产者、owner 及其回调不得等待本来源的操作或 Completion，避免自 join。
/// </remarks>
internal sealed class ReplaceableVideoFrameSource : IVideoFrameSource, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Guid _sessionId;
    private readonly TaskCompletionSource _closingSignal = Signal();
    private readonly TaskCompletionSource<Exception?> _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _changed = Signal();
    private readonly Task _completion;
    private readonly List<Exception> _readErrors = [];
    private Generation? _active;
    private Generation? _retiring;
    private Generation? _candidate;
    private Task? _replace;
    private Task<EncodedFrame?>? _read;
    private bool _closing;

    internal ReplaceableVideoFrameSource(Guid sessionId)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("会话标识不能为空。", nameof(sessionId));
        _sessionId = sessionId;
        _completion = Task.Run(CompleteAsync);
    }

    internal Guid SessionId => _sessionId;
    internal Task Completion => _completion;
    // 封口即通知监督者；不等待可能仍阻塞的 Start/Read/Stop/Completion。
    internal Task Termination => _closingSignal.Task;

    /// <summary>同步接纳并移交所有权；只允许一个替换，不缓存后续请求。</summary>
    internal Task ReplaceAsync(IVideoFrameProducer next)
    {
        ArgumentNullException.ThrowIfNull(next);
        lock (_gate)
        {
            if (_closing) throw new ObjectDisposedException(nameof(ReplaceableVideoFrameSource));
            if (_replace is { IsCompleted: false }) throw new InvalidOperationException("已有生产者换代在途。");
            if (ReferenceEquals(_active?.Producer, next)) throw new InvalidOperationException("生产者已由此来源拥有。");
            Generation candidate = new(next);
            Generation? previous = _active;
            _candidate = candidate;
            _retiring = previous;
            _active = null;
            if (previous is not null) RetireLocked(previous);
            // 在同一 gate 发布真实 worker，再允许 Dispose 或 Read 观察状态。
            _replace = Task.Run(() => ReplaceCoreAsync(previous, candidate));
            PulseLocked();
            return _replace;
        }
    }

    public ValueTask<EncodedFrame?> ReadNextAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (sessionId != _sessionId) throw new ArgumentException("来源不属于此会话。", nameof(sessionId));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_read is { IsCompleted: false }) throw new InvalidOperationException("视频来源只允许一个读取者。");
            // 终态后不再接纳工作；该读取仅等完整 Completion，不参与其 join 集合。
            if (_closing) return new ValueTask<EncodedFrame?>(ReadTerminalAsync());
            _read = Task.Run(() => ReadCoreAsync(cancellationToken));
            return new ValueTask<EncodedFrame?>(_read);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) CloseLocked();
        return new ValueTask(_completion);
    }

    private async Task ReplaceCoreAsync(Generation? previous, Generation candidate)
    {
        if (previous is not null)
        {
            Exception[] errors = await previous.Retirement!.ConfigureAwait(false);
            if (errors.Length != 0)
            {
                lock (_gate) CloseLocked();
            }
        }
        Task? start;
        lock (_gate)
        {
            if (_closing) start = null;
            else
            {
                // Start 同步前缀也是原拥有操作；停止不得遗弃它。
                candidate.Start = Task.Run(() => Start(candidate));
                start = candidate.Start;
            }
        }
        if (start is not null) await start.ConfigureAwait(false);
        lock (_gate)
        {
            if (!_closing && candidate.StartErrors.Count == 0)
            {
                _active = candidate;
                _candidate = null;
                _retiring = null;
                PulseLocked();
                return;
            }
            CloseLocked();
        }
        // 不等待包含本 Replace 的 Completion；只等待原代际结算根。
        Exception[] candidateErrors = await candidate.Retirement!.ConfigureAwait(false);
        Exception[] previousErrors = previous is null ? [] : await previous.Retirement!.ConfigureAwait(false);
        Exception[] combined;
        lock (_gate) combined = [.. _readErrors, .. previousErrors, .. candidateErrors];
        if (combined.Length != 0) throw new AggregateException("视频生产者换代失败。", combined);
        throw new InvalidOperationException("视频来源已终止，候选未发布。");
    }

    private void Start(Generation generation)
    {
        // 等待发布原 Start Task；停止若已封口则不再调用外部 Start。
        lock (_gate)
        {
            if (generation.Retirement is not null) return;
        }
        try { generation.Producer.Start(); }
        catch (Exception error)
        {
            bool stopped;
            lock (_gate) stopped = generation.Retirement is not null;
            bool proven = false;
            Exception? proofError = null;
            if (stopped && error.GetType() == typeof(InvalidOperationException) &&
                generation.Producer is IVideoFrameProducerStartRejection evidence)
            {
                try { proven = evidence.IsStopBeforeStartRejection((InvalidOperationException)error); }
                catch (Exception failure) { proofError = failure; }
            }
            if (!proven) generation.StartErrors.Add(error);
            if (proofError is not null) generation.StartErrors.Add(proofError);
        }
    }

    private async Task<EncodedFrame?> ReadCoreAsync(CancellationToken token)
    {
        while (true)
        {
            Task? changed = null;
            Task<EncodedFrame?>? attempt = null;
            bool closing;
            lock (_gate)
            {
                closing = _closing;
                if (!closing)
                {
                    token.ThrowIfCancellationRequested();
                    if (_active is { } generation)
                    {
                        generation.Read = Task.Run(() => ReadAttemptAsync(generation, token));
                        attempt = generation.Read;
                    }
                    else changed = _changed.Task;
                }
            }
            if (closing)
            {
                Exception? error = await _settled.Task.ConfigureAwait(false);
                if (error is not null) throw error;
                return null;
            }
            if (attempt is not null)
            {
                EncodedFrame? frame = await attempt.ConfigureAwait(false);
                if (frame is not null) return frame;
            }
            else
            {
                // 仅取消内部状态信号等待；从不代理/截断外部生产者原 Read。
                await changed!.WaitAsync(token).ConfigureAwait(false);
            }
        }
    }

    private async Task<EncodedFrame?> ReadAttemptAsync(Generation generation, CancellationToken token)
    {
        Task<EncodedFrame?>? original = null;
        EncodedFrame? frame = null;
        // 原 attempt 已登记；退役先赢则不进入新的外部调用。
        lock (_gate)
        {
            if (_closing || !ReferenceEquals(_active, generation)) return null;
        }
        try
        {
            original = generation.Producer.ReadNextAsync(token).AsTask();
            frame = await original.ConfigureAwait(false);
            lock (_gate)
            {
                if (!_closing && ReferenceEquals(_active, generation))
                {
                    if (frame is not null)
                    {
                        EncodedFrame delivered = frame;
                        frame = null; // gate 内提交；此后 Replace/Dispose 不再拥有该帧。
                        return delivered;
                    }
                    CloseLocked(); // 仅活动代的真正 EOF 永久封口。
                }
            }
        }
        catch (Exception error)
        {
            Exception failure = OriginalError(original, error);
            if (failure is OperationCanceledException cancelled && token.IsCancellationRequested &&
                cancelled.CancellationToken == token && (original is null || original.IsCanceled))
                throw;
            lock (_gate)
            {
                _readErrors.Add(failure);
                CloseLocked();
            }
        }
        finally
        {
            // 旧代迟到帧先结算，再允许退休任务完成。Dispose 在 gate 外，且不重试。
            if (frame is not null)
            {
                try { frame.Dispose(); }
                catch (Exception error)
                {
                    lock (_gate)
                    {
                        _readErrors.Add(error);
                        CloseLocked();
                    }
                }
            }
        }
        return null;
    }

    private void CloseLocked()
    {
        if (_closing) return;
        _closing = true;
        if (_active is { } active) RetireLocked(active);
        if (_retiring is { } previous) RetireLocked(previous);
        if (_candidate is { } candidate) RetireLocked(candidate);
        _closingSignal.TrySetResult();
        PulseLocked();
    }

    private void RetireLocked(Generation generation)
    {
        // 此标记与读/Start 发布共用 gate。Task.Run 委托内也先取 gate，禁止发布前外部重入。
        generation.Retirement ??= Task.Run(() => RetireAsync(generation));
    }

    private async Task<Exception[]> RetireAsync(Generation generation)
    {
        Task? start;
        Task<EncodedFrame?>? read;
        lock (_gate)
        {
            start = generation.Start;
            read = generation.Read;
        }
        // 每个同步入口独立调度，任何一个阻塞也不妨碍另两个入口发出。
        Task<Observation> stop = Task.Run(() => ObserveAsync(() => generation.Producer.StopAsync()));
        Task<Observation> dispose = Task.Run(() => ObserveAsync(() => generation.Producer.DisposeAsync().AsTask()));
        Task<Observation> completion = Task.Run(() => ObserveAsync(() => generation.Producer.Completion));
        Observation[] observations = await Task.WhenAll(stop, dispose, completion).ConfigureAwait(false);
        if (start is not null) await start.ConfigureAwait(false);
        if (read is not null)
        {
            // 原 Read attempt 包括同步前缀、原异步调用及迟到 owner 清理；不等外层 source Read。
            try { await read.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* 已在读取原边界判定的 caller 取消。 */ }
        }
        List<Exception> errors = [.. generation.StartErrors];
        HashSet<Task> seen = new(ReferenceEqualityComparer.Instance);
        foreach (Observation observation in observations)
        {
            if (observation.Operation is not null && !seen.Add(observation.Operation)) continue;
            if (observation.Error is not null) errors.Add(observation.Error);
        }
        return errors.ToArray();
    }

    private async Task CompleteAsync()
    {
        await _closingSignal.Task.ConfigureAwait(false);
        Generation[] generations;
        Task? replace;
        Task? read;
        lock (_gate)
        {
            generations = new[] { _retiring, _active, _candidate }.OfType<Generation>().Distinct().ToArray();
            replace = _replace;
            read = _read;
        }
        List<Exception> errors = [];
        foreach (Generation generation in generations)
            errors.AddRange(await generation.Retirement!.ConfigureAwait(false));
        // Replace 不等本 Completion 或 _settled，故可以先 join，再发布终态供外层读退出。
        if (replace is not null)
        {
            try { await replace.ConfigureAwait(false); }
            catch (Exception) { /* 原 Start/retirement 错误已逐槽保留，封口拒绝不是清理失败。 */ }
        }
        lock (_gate) errors.InsertRange(0, _readErrors);
        Exception? terminal = errors.Count == 0 ? null : new AggregateException("视频来源已终止。", errors);
        _settled.TrySetResult(terminal);
        if (read is not null)
        {
            try { await read.ConfigureAwait(false); }
            catch (Exception) { /* 原读取真错已在读取边界保留；caller 取消不污染终态。 */ }
        }
        if (terminal is not null) throw terminal;
    }

    private async Task<EncodedFrame?> ReadTerminalAsync()
    {
        await _completion.ConfigureAwait(false);
        return null;
    }

    private static async Task<Observation> ObserveAsync(Func<Task> invoke)
    {
        Task? original = null;
        try
        {
            original = invoke() ?? throw new InvalidOperationException("生产者未返回完整生命周期任务。");
            await original.ConfigureAwait(false);
            return new(original, null);
        }
        catch (Exception error) { return new(original, OriginalError(original, error)); }
    }

    private static Exception OriginalError(Task? original, Exception fallback) =>
        original?.Exception is not { } tree ? fallback : tree.InnerExceptions.Count == 1 ? tree.InnerExceptions[0] : tree;

    private void PulseLocked()
    {
        TaskCompletionSource previous = _changed;
        _changed = Signal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed record Observation(Task? Operation, Exception? Error);

    private sealed class Generation(IVideoFrameProducer producer)
    {
        internal IVideoFrameProducer Producer { get; } = producer;
        internal Task? Start;
        internal Task<EncodedFrame?>? Read;
        internal Task<Exception[]>? Retirement;
        // 仅 Start worker 写；只有 await 原 Start 后读取。
        internal List<Exception> StartErrors { get; } = [];
    }
}
