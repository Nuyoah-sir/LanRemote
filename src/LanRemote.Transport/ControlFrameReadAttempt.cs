using System.Security.Cryptography;

namespace LanRemote.Transport;

/// <summary>单次首读/接续/预算组合；外部首字节入口供认证后Client目录接收使用。</summary>
/// <remarks>
/// 借用流且要求唯一读者；外层负责认证/权限和在 AbortRequired 后锁外关闭流、持有关闭原任务。
/// Start 的同一结果 Task 向指定唯一消费者移交数组；重复 await 是同一引用，不是新的所有权。
/// 默认入口的本地锚点在首读 worker 完整退出后采集，不是物理到达，之前的调度间隙不计入帧预算。
/// 外部首字节入口保留 caller 提供的同源可信锚点/频率，不重读、不刷新预算；caller 证明原读已退出。
/// 初始化预算已观察过期时不启动接续；Ready 后调度间隙不保证零 I/O，最终仍按原锚点拒绝过期交付。
/// 最后有效采样和 gate 提交非物理原子事件；结果是历史接受，不是未来业务权限/时间许可。
/// RequestStop 不调用 stream/clock/任意取消回调；通知不等于退出，不合作外部操作可永久阻塞。
/// Completion 不证明外层关闭或违规隐藏 I/O 退出；本对象的 gate 不等于真实父会话 gate。
/// </remarks>
internal sealed class ControlFrameReadAttempt
{
    internal enum AttemptEnd { Pending, Frame, EndOfStream, Rejected }
    internal sealed record FrameOutcome(AttemptEnd End, byte[]? Payload);
    internal sealed record AttemptSnapshot(
        AttemptEnd End, bool StopRequested, bool Committed,
        ControlReadAhead.ReadSnapshot? FirstRead,
        ControlFrameContinuation.ReadSnapshot? Continuation,
        ControlFrameBudget.BudgetSnapshot? Budget,
        Exception? ObservationError, Exception? FinalSampleError, Exception? CoordinationError);

    private static readonly FrameOutcome RejectedOutcome = new(AttemptEnd.Rejected, null);
    private readonly object _gate = new();
    private readonly Stream _stream;
    private readonly ObservedClock _clock;
    private readonly TimeSpan _budget;
    private readonly int _maxBytes;
    private readonly ObservedFirstByte? _observedFirst;
    private sealed record ObservedFirstByte(byte FirstByte, long At, long Frequency);
    private readonly TaskCompletionSource _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _abort = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task<FrameOutcome>? _completion;
    private ControlReadAhead? _first;
    private ControlFrameContinuation? _frame;
    private ControlFrameBudget? _deadline;
    private AttemptEnd _end;
    private bool _stopRequested, _committed;
    private Exception? _observationError, _finalSampleError, _coordinationError;

    internal ControlFrameReadAttempt(Stream stream, TimeProvider clock, TimeSpan budget, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThan(budget, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(budget, ControlFrameBudget.MaxBudget);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBytes, TransportConstants.MaxControlMessageBytes);
        _stream = stream;
        _clock = new ObservedClock(clock);
        _budget = budget;
        _maxBytes = maxBytes;
    }

    /// <summary>接续已取得的数据字节；byte 不表示 EOF，也不证明方向/权限或父会话仍有效。</summary>
    /// <remarks>构造不取时/不碰流。唯一读者必须传同一流、同一 clock 的原锚点；FirstRead 快照始终为 null。</remarks>
    internal static ControlFrameReadAttempt FromObservedFirstByte(
        Stream stream, byte firstByte, TimeProvider clock, long observedAt,
        long observedFrequency, TimeSpan budget, int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(observedFrequency);
        return new ControlFrameReadAttempt(stream, clock, budget, maxBytes,
            new ObservedFirstByte(firstByte, observedAt, observedFrequency));
    }

    private ControlFrameReadAttempt(Stream stream, TimeProvider clock, TimeSpan budget,
        int maxBytes, ObservedFirstByte observedFirst) : this(stream, clock, budget, maxBytes)
    {
        _observedFirst = observedFirst;
    }

    internal Task AbortRequired => _abort.Task;
    internal Task<FrameOutcome>? Completion { get { lock (_gate) return _completion; } }
    internal AttemptSnapshot Snapshot
    {
        get
        {
            // 不同时持有 owner/子组件 gate；运行中非事务快照，Completion 后才稳定（后续 Stop 除外）。
            ControlReadAhead? first;
            ControlFrameContinuation? frame;
            ControlFrameBudget? deadline;
            AttemptSnapshot snapshot;
            lock (_gate)
            {
                first = _first; frame = _frame; deadline = _deadline;
                snapshot = new(_end, _stopRequested, _committed, null, null, null,
                    _observationError, _finalSampleError, _coordinationError);
            }
            return snapshot with { FirstRead = first?.Snapshot, Continuation = frame?.Snapshot, Budget = deadline?.Snapshot };
        }
    }

    internal Task<FrameOutcome> Start()
    {
        lock (_gate)
        {
            if (_completion is not null) throw new InvalidOperationException("单帧组合不能重复启动。");
            return _completion = Task.Run(RunAsync);
        }
    }

    internal void RequestStop()
    {
        lock (_gate)
        {
            _stopRequested = true;
            if (!_committed) { _end = AttemptEnd.Rejected; _abort.TrySetResult(); }
            _stop.TrySetResult();
        }
    }

    private bool CanContinue() { lock (_gate) return !_stopRequested && _end == AttemptEnd.Pending; }
    private void Reject()
    {
        lock (_gate)
        {
            if (!_committed) { _end = AttemptEnd.Rejected; _abort.TrySetResult(); }
        }
    }

    private async Task<FrameOutcome> RunAsync()
    {
        Task? firstTask = null, frameTask = null, deadlineTask = null;
        byte[]? payload = null;
        long anchor = 0, frequency = 0;
        bool candidate = false, eof = false;
        try
        {
            if (!CanContinue()) return RejectedOutcome; // Start 登记之后才越过同 gate 发布屏障。
            byte? firstByte;
            if (_observedFirst is { } observed)
            {
                firstByte = observed.FirstByte;
                anchor = observed.At;
                frequency = observed.Frequency;
            }
            else
            {
                ControlReadAhead first = new(_stream);
                lock (_gate) _first = first;
                if (!CanContinue()) first.RequestStop();
                firstTask = first.Start();
                await Task.WhenAny(firstTask, _stop.Task).ConfigureAwait(false);
                if (!CanContinue()) first.RequestStop();
                await firstTask.ConfigureAwait(false); // 绝不把通知/取消包装当原首读退出。
                if (!CanContinue()) return RejectedOutcome;
                if (!first.TryTake(out ControlReadAhead.ReadResult? firstResult) || firstResult is null)
                { Reject(); return RejectedOutcome; }
                firstByte = firstResult.FirstByte;
                if (firstByte is not null)
                {
                    try
                    {
                        frequency = _clock.TimestampFrequency;
                        if (frequency <= 0) throw new InvalidOperationException("control-frame-observation-frequency-invalid");
                        anchor = _clock.GetTimestamp();
                        if (_clock.TimestampFrequency != frequency)
                            throw new InvalidOperationException("control-frame-observation-frequency-changed");
                    }
                    catch (Exception error)
                    {
                        lock (_gate) _observationError = error;
                        Reject(); return RejectedOutcome;
                    }
                }
            }
            if (firstByte is null) { eof = true; }
            else
            {
                if (!CanContinue()) return RejectedOutcome;
                ControlFrameBudget deadline = new(_clock, anchor, frequency, _budget);
                lock (_gate) _deadline = deadline;
                if (!CanContinue()) deadline.RequestStop();
                deadlineTask = deadline.Start();
                await Task.WhenAny(deadline.Ready, _stop.Task).ConfigureAwait(false);
                if (!CanContinue() || deadline.Snapshot.End != ControlFrameBudget.BudgetEnd.Pending)
                { Reject(); return RejectedOutcome; }

                ControlFrameContinuation frame = new(_stream, firstByte.Value, _maxBytes);
                lock (_gate) _frame = frame;
                if (!CanContinue()) frame.RequestStop();
                frameTask = frame.Start();
                await Task.WhenAny(frameTask, deadline.Decision, _stop.Task).ConfigureAwait(false);
                // 检查全部可见状态；WhenAny 参数位置不决定接受优先级。
                if (!CanContinue() || deadline.Snapshot.End != ControlFrameBudget.BudgetEnd.Pending || !frameTask.IsCompleted)
                { Reject(); return RejectedOutcome; }
                await frameTask.ConfigureAwait(false);
                if (!frame.TryTake(out payload) || payload is null)
                { Reject(); return RejectedOutcome; }
                deadline.RequestFinish();
                await Task.WhenAny(deadline.Decision, _stop.Task).ConfigureAwait(false);
                if (!CanContinue()) return RejectedOutcome;
                if (deadline.Snapshot.End != ControlFrameBudget.BudgetEnd.FinishedInBudget)
                { Reject(); return RejectedOutcome; } // 先通知，不能等悬挂的原 Dispose 才拒绝。
                await deadlineTask.ConfigureAwait(false);
                ControlFrameBudget.BudgetSnapshot budgetState = deadline.Snapshot;
                if (budgetState.End != ControlFrameBudget.BudgetEnd.FinishedInBudget ||
                    budgetState.Error is not null || budgetState.CleanupError is not null ||
                    budgetState.CleanupExitUnproven || (budgetState.TimerAcquired && !budgetState.TimerReleased))
                { Reject(); return RejectedOutcome; }
                candidate = true;
            }
        }
        catch (Exception error)
        {
            lock (_gate) _coordinationError = error;
            Reject();
        }
        finally
        {
            // 拒绝先通知外层，再请求子停止；外层关闭原任务独立于本组合 join，不能回等父自身。
            if (!candidate && !eof)
            {
                Reject();
                _first?.RequestStop();
                _frame?.RequestStop();
                _deadline?.RequestStop();
                // TryTake 后的 payload 已静止且归本 owner；拒绝后清理不能被原释放的 join 拖延。
                if (payload is not null)
                {
                    CryptographicOperations.ZeroMemory(payload);
                    payload = null;
                }
            }
            // 即使一个组件异常也等待其余原 worker；WhenAll 自身也必须完整 await。
            Task all = Task.WhenAll(firstTask ?? Task.CompletedTask, frameTask ?? Task.CompletedTask, deadlineTask ?? Task.CompletedTask);
            try { await all.ConfigureAwait(false); }
            catch (Exception caught)
            {
                AggregateException? container = all.Exception;
                Exception saved = container is null ? caught : container.InnerExceptions.Count == 1 ? container.InnerExceptions[0] : container;
                lock (_gate) _coordinationError ??= saved;
                candidate = eof = false;
                Reject();
            }
            // 早退会跳过下方提交段；未获交付资格的内部所有权不能遗留。
            if (!candidate && payload is not null)
            {
                CryptographicOperations.ZeroMemory(payload);
                payload = null;
            }
        }

        try
        {
            if (!CanContinue() || (!candidate && !eof)) return RejectedOutcome;
            // 分配结果容器在最终采样之前；失败时仍由此 worker 负责数组清理。
            FrameOutcome result = new(eof ? AttemptEnd.EndOfStream : AttemptEnd.Frame, payload);
            if (candidate)
            {
                try
                {
                    if (_clock.TimestampFrequency != frequency)
                        throw new InvalidOperationException("control-frame-final-frequency-changed");
                    long now = _clock.GetTimestamp();
                    if (_clock.TimestampFrequency != frequency)
                        throw new InvalidOperationException("control-frame-final-frequency-changed");
                    Int128 delta = (Int128)now - anchor;
                    if (delta < 0 || delta > long.MaxValue)
                        throw new InvalidOperationException("control-frame-final-range-invalid");
                    if (delta * TimeSpan.TicksPerSecond >= (Int128)_budget.Ticks * frequency)
                        throw new TimeoutException("control-frame-final-budget-expired");
                }
                catch (Exception error)
                {
                    lock (_gate) _finalSampleError = error;
                    Reject(); return RejectedOutcome;
                }
            }
            lock (_gate)
            {
                if (_stopRequested || _end != AttemptEnd.Pending) return RejectedOutcome;
                _committed = true;
                _end = result.End;
                payload = null; // 原协调结果向指定唯一消费者转移；Stop 不回收已移交数组。
            }
            return result;
        }
        finally
        {
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <summary>三阶段严格串行取时；只补跨预算与末检的高水位，不包装 timer 或原 Dispose。</summary>
    private sealed class ObservedClock(TimeProvider inner) : TimeProvider
    {
        private bool _seen;
        private long _last;
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override long GetTimestamp()
        {
            long now = inner.GetTimestamp();
            if (_seen && now < _last) throw new InvalidOperationException("control-frame-observed-clock-regressed");
            _seen = true;
            _last = now;
            return now;
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => inner.CreateTimer(callback, state, dueTime, period);
    }
}
