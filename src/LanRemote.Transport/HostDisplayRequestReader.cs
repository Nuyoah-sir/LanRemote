using System.Security.Cryptography;

namespace LanRemote.Transport;

/// <summary>显式认证后 request_display 唯一读者；首读交接、接续和处理器均等待原任务退出。</summary>
/// <remarks>
/// 借用流，不关闭流。StopRequired/closeRequired仅通知，外层必须独立关线解堵，再join Completion。
/// 首字节预算从本地消费时观察，非网络到达时间；解析后仍复核同一锚点。业务准入只属于handler。
/// 唯一reader持续感知EOF；handler在独立单槽worker执行，槽忙时新合法请求fail-close，不排队或发送失败回执。
/// handler不可反向等待本reader的Completion；同步前缀、原任务及取消回调均可能永久阻塞真实退出。
/// </remarks>
internal sealed class HostDisplayRequestReader : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Stream _stream;
    private readonly Task<byte?> _firstRead;
    private readonly Guid _sessionId;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _budget;
    private readonly Func<DisplaySwitchRequestFrame, CancellationToken, Task> _handle;
    private readonly TaskCompletionSource _closeRequired;
    private readonly CancellationToken _hostToken;
    private readonly CancellationTokenSource _handling = new();
    private readonly TaskCompletionSource _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _completion, _cancelWorker;
    private Task<Exception?>? _handlerWorker;
    private ControlReadAhead? _readAhead;
    private ControlFrameReadAttempt? _attempt;
    private Exception? _error;
    private bool _stopping;

    internal HostDisplayRequestReader(Stream stream, Task<byte?> firstRead, Guid sessionId,
        TimeProvider clock, TimeSpan budget,
        Func<DisplaySwitchRequestFrame, CancellationToken, Task> handle,
        TaskCompletionSource closeRequired, CancellationToken hostToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(firstRead);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(closeRequired);
        if (sessionId == Guid.Empty) throw new ArgumentException("会话标识不可为空。", nameof(sessionId));
        ArgumentOutOfRangeException.ThrowIfLessThan(budget, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(budget, ControlFrameBudget.MaxBudget);
        _stream = stream; _firstRead = firstRead; _sessionId = sessionId;
        _clock = clock; _budget = budget; _handle = handle;
        _closeRequired = closeRequired; _hostToken = hostToken;
    }

    internal Task? Completion { get { lock (_gate) return _completion; } }
    internal Task StopRequired => _stop.Task;
    internal Exception? Error { get { lock (_gate) return _error; } }

    internal Task Start()
    {
        lock (_gate)
        {
            if (_completion is not null) throw new InvalidOperationException("显示请求读者不能重复启动。");
            return _completion = Task.Run(RunAsync);
        }
    }

    internal void RequestStop()
    {
        lock (_gate)
        {
            _stopping = true;
            _closeRequired.TrySetResult();
            _stop.TrySetResult();
            _readAhead?.RequestStop();
            _attempt?.RequestStop();
            // 只登记独立原worker；任意取消回调绝不占住本gate或调用者的同步前缀。
            _cancelWorker ??= Task.Run(() => _handling.Cancel());
        }
    }

    public ValueTask DisposeAsync()
    {
        RequestStop();
        lock (_gate) return new(_completion ??= Task.Run(RunAsync));
    }

    private bool CanContinue() { lock (_gate) return !_stopping; }

    private async Task RunAsync()
    {
        List<Exception> errors = [];
        CancellationTokenRegistration registration = default;
        bool firstJoined = false;
        try
        {
            lock (_gate) { } // 原worker先发布，才可注册回调或进入外部同步前缀。
            registration = _hostToken.Register(static state => ((HostDisplayRequestReader)state!).RequestStop(), this);
            await Task.WhenAny(_firstRead, _stop.Task).ConfigureAwait(false);
            byte? first;
            try { first = await _firstRead.ConfigureAwait(false); }
            catch (Exception caught) { errors.Add(OriginalError(_firstRead, caught)); return; }
            finally { firstJoined = true; }

            while (CanContinue())
            {
                if (first is null) return;
                // 4096字节上限的大端长度前缀首字节必为0；0不是EOF。
                if (first.Value != 0) throw new FrameProtocolException("request-display-invalid-prefix");
                DisplaySwitchRequestFrame? request = await ReadRequestAsync(first.Value, errors).ConfigureAwait(false);
                if (request is null) return;

                lock (_gate)
                {
                    if (_stopping) return;
                    if (_handlerWorker is { IsCompleted: false })
                        throw new FrameProtocolException("request-display-handler-busy");
                    // 仅已完整成功退出的旧槽可被替换。故障worker先Stop，故不会被覆盖而丢失原错误。
                    _handlerWorker = Task.Run(() => HandleAsync(request));
                }
                // handler同步前缀和原Task不能阻塞唯一读者；EOF负责先通知父撤销/关线，再join。
                ControlReadAhead.ReadResult? result = await ReadNextAsync(errors).ConfigureAwait(false);
                if (result is null) return;
                first = result.FirstByte;
            }
        }
        catch (Exception caught) { errors.Add(caught); }
        finally
        {
            RequestStop(); // 所有拒绝/EOF先通知外层关流，绝不等待自己所属的父join。
            if (!firstJoined)
            {
                try { await _firstRead.ConfigureAwait(false); }
                catch (Exception caught) { errors.Add(OriginalError(_firstRead, caught)); }
            }
            Task<Exception?>? handler;
            lock (_gate) handler = _handlerWorker;
            if (handler is not null)
            {
                try { if (await handler.ConfigureAwait(false) is { } failure) errors.Add(failure); }
                catch (Exception caught) { errors.Add(OriginalError(handler, caught)); }
            }
            try { await registration.DisposeAsync().ConfigureAwait(false); }
            catch (Exception caught) { errors.Add(caught); }
            Task cancellation;
            lock (_gate) cancellation = _cancelWorker!;
            try { await cancellation.ConfigureAwait(false); }
            catch (Exception caught) { errors.Add(OriginalError(cancellation, caught)); }
            try { _handling.Dispose(); }
            catch (Exception caught) { errors.Add(caught); }
            lock (_gate)
                _error = errors.Count == 0 ? null : errors.Count == 1 ? errors[0]
                    : new AggregateException("显示请求读取及收尾失败。", errors);
        }
    }

    private async Task<Exception?> HandleAsync(DisplaySwitchRequestFrame request)
    {
        Task? original = null;
        try
        {
            lock (_gate) if (_stopping) return null;
            original = _handle(request, _handling.Token)
                ?? throw new InvalidOperationException("request-display-handler-task-missing");
            await original.ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException cancelled) when (original is { IsCanceled: true } &&
            !CanContinue() && _handling.IsCancellationRequested && cancelled.CancellationToken == _handling.Token)
        { return null; } // 仅合作取消；Faulted OCE及多根原错误绝不豁免。
        catch (Exception caught)
        {
            Exception failure = OriginalError(original, caught);
            RequestStop(); // 唤醒当前首读或接续，不能等reader退出后才通知关线。
            return failure;
        }
    }

    private async Task<ControlReadAhead.ReadResult?> ReadNextAsync(List<Exception> errors)
    {
        ControlReadAhead read = new(_stream);
        Task original;
        lock (_gate)
        {
            if (_stopping) return null;
            _readAhead = read;
            original = read.Start();
        }
        try
        {
            await Task.WhenAny(original, _stop.Task).ConfigureAwait(false);
            if (!CanContinue()) read.RequestStop();
            await original.ConfigureAwait(false);
            if (read.Snapshot.Error is { } error) errors.Add(error);
            if (errors.Count != 0 || !CanContinue()) return null;
            if (!read.TryTake(out ControlReadAhead.ReadResult? result))
                throw new FrameProtocolException("request-display-first-read-rejected");
            return result;
        }
        finally { lock (_gate) _readAhead = null; }
    }

    private async Task<DisplaySwitchRequestFrame?> ReadRequestAsync(byte first, List<Exception> errors)
    {
        if (!CanContinue()) return null;
        long frequency = _clock.TimestampFrequency;
        long anchor = _clock.GetTimestamp();
        if (frequency <= 0 || _clock.TimestampFrequency != frequency)
            throw new InvalidOperationException("request-display-observation-frequency-invalid");
        RequestClock clock = new(_clock, anchor, frequency);
        ControlFrameReadAttempt attempt = ControlFrameReadAttempt.FromObservedFirstByte(
            _stream, first, clock, anchor, frequency, _budget, DisplaySwitchRequestFrame.MaxPayloadByteLength);
        Task<ControlFrameReadAttempt.FrameOutcome> original;
        lock (_gate)
        {
            if (_stopping) return null;
            _attempt = attempt;
            original = attempt.Start();
        }
        byte[]? payload = null;
        try
        {
            await Task.WhenAny(original, attempt.AbortRequired, _stop.Task).ConfigureAwait(false);
            if (attempt.AbortRequired.IsCompleted || !CanContinue()) RequestStop();
            ControlFrameReadAttempt.FrameOutcome result = await original.ConfigureAwait(false);
            payload = result.Payload;
            ControlFrameReadAttempt.AttemptSnapshot snapshot = attempt.Snapshot;
            // 各槽都是独立原错误根；不按引用去重，不Flatten，不按异常类型吞错。
            Add(snapshot.FirstRead?.Error); Add(snapshot.Continuation?.Error);
            Add(snapshot.Budget?.Error); Add(snapshot.Budget?.CleanupError);
            Add(snapshot.ObservationError); Add(snapshot.FinalSampleError); Add(snapshot.CoordinationError);
            if (errors.Count != 0 || !CanContinue()) return null;
            if (result.End != ControlFrameReadAttempt.AttemptEnd.Frame || payload is null)
                throw new FrameProtocolException("request-display-read-rejected");
            bool parsed = DisplaySwitchRequestFrame.TryParse(payload, _sessionId, out DisplaySwitchRequestFrame? frame, out _);
            _ = clock.TimestampFrequency;
            long now = clock.GetTimestamp();
            _ = clock.TimestampFrequency;
            Int128 elapsed = (Int128)now - anchor;
            if (elapsed < 0 || elapsed > long.MaxValue ||
                elapsed * TimeSpan.TicksPerSecond >= (Int128)_budget.Ticks * frequency)
                throw new TimeoutException("request-display-deadline-expired");
            if (!parsed) throw new FrameProtocolException("request-display-invalid-payload");
            return frame;
        }
        finally
        {
            // 仅清理原attempt已交付的静止payload；在途或未知借用区仍由原组件监督。
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
            lock (_gate) _attempt = null;
        }
        void Add(Exception? error) { if (error is not null) errors.Add(error); }
    }

    private static Exception OriginalError(Task? task, Exception caught) =>
        task?.Exception is not { } tree ? caught : tree.InnerExceptions.Count == 1 ? tree.InnerExceptions[0] : tree;

    private sealed class RequestClock(TimeProvider source, long anchor, long frequency) : TimeProvider
    {
        private long _last = anchor;
        public override long TimestampFrequency => source.TimestampFrequency == frequency
            ? frequency : throw new InvalidOperationException("request-display-frequency-changed");
        public override long GetTimestamp()
        {
            long now = source.GetTimestamp();
            if (now < _last) throw new InvalidOperationException("request-display-clock-regressed");
            _last = now;
            return now;
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            source.CreateTimer(callback, state, dueTime, period);
    }
}
