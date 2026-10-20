namespace LanRemote.Transport;

/// <summary>显式认证后会话写者：目录与切屏回执共享一个无队列写槽和非回绕序号。</summary>
/// <remarks>
/// 借用流，调用者须独占认证后全部写入；不读取、不关闭流。StopRequired/closeRequired仅通知，外层先关线再join。
/// 每次原发送使用既有目录预算底座，序列化/原IO/Flush/timer清理属于同一绝对预算。
/// 槽覆盖原操作完整退出；拒绝Busy不排队、不消费序号。已接纳后失败永久封口，不重试或回收序号。
/// seq只覆盖本显式post-auth写者，不包括Hello/认证帧；不是授权或远端显示证明。
/// </remarks>
internal sealed class HostControlWriter : IAsyncDisposable
{
    internal sealed record WriteResult(long Sequence, DisplayCatalogFrame? Catalog);
    private readonly object _gate = new();
    private readonly Guid _sessionId;
    private readonly Stream _stream;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _budget;
    private readonly TaskCompletionSource _closeRequired;
    private readonly TaskCompletionSource _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _completion;
    private Task<WriteResult>? _active;
    private HostDisplayCatalogSendAttempt? _attempt;
    private long _next;
    private bool _exhausted, _stopping;

    internal HostControlWriter(Stream stream, Guid sessionId, TimeProvider clock, TimeSpan budget,
        TaskCompletionSource closeRequired, long firstSequence = 0)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(closeRequired);
        if (sessionId == Guid.Empty) throw new ArgumentException("会话标识不可为空。", nameof(sessionId));
        ArgumentOutOfRangeException.ThrowIfNegative(firstSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(budget, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(budget, ControlFrameBudget.MaxBudget);
        _stream = stream; _sessionId = sessionId; _clock = clock; _budget = budget;
        _closeRequired = closeRequired; _next = firstSequence;
        _completion = Task.Run(CompleteAsync);
    }

    internal Guid SessionId => _sessionId;
    internal Task StopRequired => _stop.Task;
    internal Task Completion => _completion;
    internal bool HasActiveWrite { get { lock (_gate) return _active is { IsCompleted: false }; } }

    internal Task<WriteResult> WriteCatalogAsync(Func<ValueTask<DisplayCatalogFrame>> source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Start(source, null, cancellationToken);
    }

    // 只接受同会话回执，原Request/FirstSend资格仍由DisplaySwitchExecutor在调用前判定。
    // 外部seq/time只是待写模板；写槽接纳时预约自己的seq，UTC及序列化在预算内执行。
    internal Task WriteDisplayChangedAsync(DisplayChangedFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.SessionId != _sessionId) throw new ArgumentException("回执不属于本写者会话。", nameof(frame));
        return Start(null, frame, cancellationToken);
    }

    private long Timestamp() => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    private Task<WriteResult> Start(Func<ValueTask<DisplayCatalogFrame>>? source, DisplayChangedFrame? reply, CancellationToken caller)
    {
        lock (_gate)
        {
            if (_stopping) throw new InvalidOperationException("Control写者已封口。");
            if (_active is { IsCompleted: false }) throw new InvalidOperationException("Control原写入尚未退出。");
            caller.ThrowIfCancellationRequested();
            if (_exhausted) { RequestStop(); throw new InvalidOperationException("Control序号已耗尽。"); }
            long sequence = _next;
            if (sequence == long.MaxValue) _exhausted = true;
            else _next++;
            DisplayCatalogFrame? catalog = null;
            HostDisplayCatalogSendAttempt attempt = source is not null
                ? HostDisplayCatalogSendAttempt.CreateCatalogPayload(_stream, source, frame =>
                {
                    catalog = new(sequence, Timestamp(), frame.Displays);
                    return catalog.Serialize();
                }, _clock, _budget)
                : HostDisplayCatalogSendAttempt.CreatePayload(_stream, () => ValueTask.FromResult(
                    new DisplayChangedFrame(_sessionId, sequence, Timestamp(), reply!.RequestSequence,
                        reply.CatalogSequence, reply.DisplayId, reply.FirstFrameId, reply.Width, reply.Height).Serialize()),
                    DisplayChangedFrame.MaxPayloadByteLength, _clock, _budget);
            _attempt = attempt;
            return _active = Task.Run(() => WriteAsync(attempt, sequence, () => catalog, caller));
        }
    }

    internal void RequestStop()
    {
        lock (_gate)
        {
            _stopping = true;
            _closeRequired.TrySetResult();
            _stop.TrySetResult();
            _attempt?.RequestStop();
        }
    }

    public ValueTask DisposeAsync() { RequestStop(); return new(_completion); }

    private async Task<WriteResult> WriteAsync(HostDisplayCatalogSendAttempt attempt, long sequence,
        Func<DisplayCatalogFrame?> catalog, CancellationToken caller)
    {
        CancellationTokenRegistration registration = default;
        Task<HostDisplayCatalogSendAttempt.SendOutcome>? original = null;
        List<Exception> errors = [];
        bool sent = false;
        try
        {
            // 外部取消注册不在写者gate内。即使回调先触发，下面发布屏障仍能阻止source。
            registration = caller.Register(static state => ((HostControlWriter)state!).RequestStop(), this);
            lock (_gate)
            {
                if (_stopping) attempt.RequestStop();
                original = attempt.Start();
            }
            await Task.WhenAny(original, attempt.AbortRequired, _stop.Task).ConfigureAwait(false);
            if (!original.IsCompletedSuccessfully || original.Result.End != HostDisplayCatalogSendAttempt.AttemptEnd.Sent)
                RequestStop();
            await original.ConfigureAwait(false);
            sent = original.Result.End == HostDisplayCatalogSendAttempt.AttemptEnd.Sent;
        }
        catch (Exception caught) { errors.Add(OriginalError(original, caught)); RequestStop(); }
        finally
        {
            if (!sent) RequestStop();
            // 原attempt含全部source/write/flush/budget根；有错也必须等待，不遗弃原Task。
            if (original is not null && !original.IsCompleted)
            {
                try { await original.ConfigureAwait(false); }
                catch (Exception caught) { errors.Add(OriginalError(original, caught)); }
            }
            try { await registration.DisposeAsync().ConfigureAwait(false); }
            catch (Exception caught) { errors.Add(caught); RequestStop(); }
        }
        var snapshot = attempt.Snapshot;
        Add(snapshot.OperationError); Add(snapshot.ObservationError); Add(snapshot.FinalSampleError);
        Add(snapshot.CoordinationError); Add(snapshot.Budget?.Error); Add(snapshot.Budget?.CleanupError);
        if (errors.Count != 0) { RequestStop(); throw new AggregateException("Control原写入失败。", errors); }
        lock (_gate)
        {
            if (_stopping || !sent || !snapshot.AllOwnedRootsReleased)
                throw new OperationCanceledException("Control写入未被完整接受。", caller);
            return new(sequence, catalog());
        }
        void Add(Exception? error) { if (error is not null) errors.Add(error); }
    }

    private async Task CompleteAsync()
    {
        await _stop.Task.ConfigureAwait(false);
        Task? active;
        lock (_gate) active = _active;
        if (active is not null)
        {
            try { await active.ConfigureAwait(false); }
            catch (OperationCanceledException) when (active.IsCanceled) { /* 仅无原故障槽的合作停止；聚合原IO/来源取消不豁免。 */ }
        }
    }
    private static Exception OriginalError(Task? task, Exception caught) =>
        task?.Exception is not { } tree ? caught : tree.InnerExceptions.Count == 1 ? tree.InnerExceptions[0] : tree;
}

/// <summary>可选认证装配的原holding/唯一写者监督；目录成功才发布写者，不改变原reader。</summary>
internal sealed class HostControlWriterLifetime
{
    private readonly HostControlWriter _writer;
    private readonly Func<ValueTask<DisplayCatalogFrame>> _catalogSource;
    private readonly Task _holding;
    private readonly CancellationToken _hostToken;
    private readonly TaskCompletionSource _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<HostControlWriter?> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _completion;
    internal HostControlWriterLifetime(HostControlWriter writer, Func<ValueTask<DisplayCatalogFrame>> catalogSource,
        Task holding, CancellationToken hostToken)
    { _writer = writer; _catalogSource = catalogSource; _holding = holding; _hostToken = hostToken; }
    private static readonly Task NoInputStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
    internal Task InputStopRequired { get; init; } = NoInputStop;
    internal Task StopRequired => _stop.Task;
    internal Task<HostControlWriter?> Ready => _ready.Task;
    private readonly object _gate = new();
    internal Task? Completion { get { lock (_gate) return _completion; } }
    internal DisplayCatalogFrame? Catalog { get; private set; }
    internal Task Start()
    {
        lock (_gate)
        {
            if (_completion is not null) throw new InvalidOperationException("认证Control写者不能重复启动。");
            return _completion = Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        lock (_gate) { } // Start的原worker发布先于任何外部调用。
        TaskCompletionSource hostStop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        List<Exception> errors = [];
        Task<HostControlWriter.WriteResult>? sending = null;
        try
        {
            registration = _hostToken.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), hostStop);
            if (_holding.IsCompleted || _hostToken.IsCancellationRequested || InputStopRequired.IsCompleted) return;
            try { sending = _writer.WriteCatalogAsync(_catalogSource, _hostToken); }
            catch (OperationCanceledException cancelled) when (sending is null &&
                _hostToken.IsCancellationRequested && cancelled.CancellationToken == _hostToken)
            { return; } // 仅未取得原写入Task的本地取消准入；来源/IO异步错误不得在此豁免。
            await Task.WhenAny(sending, _holding, hostStop.Task, _writer.StopRequired, InputStopRequired).ConfigureAwait(false);
            if (!sending.IsCompletedSuccessfully || _holding.IsCompleted || _hostToken.IsCancellationRequested || _writer.StopRequired.IsCompleted || InputStopRequired.IsCompleted) return;
            Catalog = sending.Result.Catalog;
            _ready.TrySetResult(_writer);
            await Task.WhenAny(_holding, hostStop.Task, _writer.StopRequired, InputStopRequired).ConfigureAwait(false);
        }
        catch (Exception caught) { errors.Add(caught); }
        finally
        {
            _ready.TrySetResult(null);
            _writer.RequestStop();
            _stop.TrySetResult(); // 父先撤销/关线，之后才join不合作的原holding/IO。
            if (sending is not null)
            {
                try { await sending.ConfigureAwait(false); }
                catch (Exception) { /* 此写入原错误只由writer.Completion报告；本处补原Task join，不重复收集。 */ }
            }
            try { await _writer.Completion.ConfigureAwait(false); }
            catch (Exception caught) { errors.Add(OriginalError(_writer.Completion, caught)); }
            try { await _holding.ConfigureAwait(false); }
            catch (Exception) { /* 原holding由认证栈保持原分类并另行观察。 */ }
            try { await registration.DisposeAsync().ConfigureAwait(false); }
            catch (Exception caught) { errors.Add(caught); }
            if (errors.Count != 0) throw new AggregateException("认证Control写者已停止。", errors);
        }
    }
    private static Exception OriginalError(Task task, Exception caught) =>
        task.Exception is not { } tree ? caught : tree.InnerExceptions.Count == 1 ? tree.InnerExceptions[0] : tree;
}
