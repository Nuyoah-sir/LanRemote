namespace LanRemote.Transport;

/// <summary>M6 独立显式锚定预算；没有接入真实 Control，也不拥有帧、流或父会话。</summary>
/// <remarks>
/// owner 保证 observedAt/observedFrequency 是同一 clock 的可信本地观察，非物理到达证明。
/// 构造不取时；唯一 worker 独占 clock/timer 同步前缀及取得的原 DisposeAsync Task。
/// Ready/Decision 仅通知，不是原操作 join；Completion 不证明帧读取、关闭或隐藏操作已退出。
/// RequestFinish 是 owner 声明原帧已读完，不是证明；成功仅在 timer 释放后末检时有效，
/// 不与 payload Take 或父会话撤销原子提交。后续消费仍须自己的统一提交检查。
/// 最后取时与 gate 内写成功也非物理原子事件；成功仅证明最后有效样本尚余预算，非未来许可。
/// Stop 不等待、不关流、不取消任意回调；已越过状态检查的外部操作仍可进入。
/// 不合作 clock/Create/Change/Dispose 永久阻塞时，Completion 不假完成，也不保证及时到期通知。
/// 显式固定频率、无回绕单调区间；观察到倒退/频率变化则拒绝，但无法发现样本间变化后恢复。
/// </remarks>
internal sealed class ControlFrameBudget
{
    internal enum BudgetEnd { Pending, FinishedInBudget, Stopped, Expired, Faulted }
    internal sealed record BudgetSnapshot(
        BudgetEnd End, bool StopRequested, bool FinishRequested,
        bool TimerAcquired, bool TimerReleased, bool CleanupExitUnproven,
        Exception? Error, Exception? CleanupError);

    // 有限系统 timer due-time 支持范围；是原语上限，不是 Control 协议默认时限。
    internal static readonly TimeSpan MaxBudget = TimeSpan.FromTicks(4_294_967_294L * TimeSpan.TicksPerMillisecond);
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly long _observedAt;
    private readonly long _frequency;
    private readonly Int128 _budgetScaled;
    private readonly TaskCompletionSource _ready = NewSignal();
    private readonly TaskCompletionSource _decision = NewSignal();
    private TaskCompletionSource _wake = NewSignal();
    private Task? _completion;
    private long _lastTimestamp;
    private bool _stopRequested, _finishRequested, _quiescing;
    private bool _timerAcquired, _timerReleased, _cleanupExitUnproven;
    private BudgetEnd _end;
    private Exception? _error, _cleanupError;

    internal ControlFrameBudget(TimeProvider clock, long observedAt, long observedFrequency, TimeSpan budget)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(observedFrequency);
        ArgumentOutOfRangeException.ThrowIfLessThan(budget, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(budget, MaxBudget);
        _clock = clock;
        _observedAt = observedAt;
        _lastTimestamp = observedAt;
        _frequency = observedFrequency;
        _budgetScaled = (Int128)budget.Ticks * observedFrequency;
    }

    internal Task Ready => _ready.Task;
    internal Task Decision => _decision.Task;
    internal Task? Completion { get { lock (_gate) return _completion; } }
    internal BudgetSnapshot Snapshot
    {
        get { lock (_gate) return new(_end, _stopRequested, _finishRequested,
            _timerAcquired, _timerReleased, _cleanupExitUnproven, _error, _cleanupError); }
    }

    internal Task Start()
    {
        lock (_gate)
        {
            if (_completion is not null) throw new InvalidOperationException("帧预算不能重复启动。");
            return _completion = Task.Run(RunAsync);
        }
    }

    internal void RequestFinish()
    {
        lock (_gate)
        {
            if (_completion is null) throw new InvalidOperationException("帧预算尚未启动。");
            if (_end != BudgetEnd.Pending) return;
            _finishRequested = true;
            _wake.TrySetResult();
        }
    }

    internal void RequestStop()
    {
        lock (_gate)
        {
            _stopRequested = true;
            if (_end == BudgetEnd.Pending) _end = BudgetEnd.Stopped;
            _ready.TrySetResult();
            _decision.TrySetResult();
            _wake.TrySetResult();
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool IsPending() { lock (_gate) return _end == BudgetEnd.Pending; }
    private bool FinishRequested() { lock (_gate) return _finishRequested; }

    private void OnTimer()
    {
        lock (_gate)
        {
            if (!_quiescing && _end == BudgetEnd.Pending) _wake.TrySetResult();
        }
    }

    private void Reject(BudgetEnd end, Exception error)
    {
        lock (_gate)
        {
            _error ??= error; // Stop 先赢也不能吞掉已在途操作的迟到错误。
            if (_end == BudgetEnd.Pending) _end = end;
            _ready.TrySetResult();
            _decision.TrySetResult();
        }
    }

    // 只由唯一 worker 调用；null 表示已拒绝/停止，不表示需要重新开始全额计时。
    private TimeSpan? SampleDue()
    {
        if (!IsPending()) return null;
        if (_budgetScaled == 0)
        {
            Reject(BudgetEnd.Expired, new TimeoutException("control-frame-budget-expired"));
            return null;
        }
        long frequencyBefore = _clock.TimestampFrequency;
        if (frequencyBefore <= 0 || frequencyBefore != _frequency)
            throw new InvalidOperationException("control-frame-clock-frequency-changed");
        long now = _clock.GetTimestamp();
        long frequencyAfter = _clock.TimestampFrequency;
        if (frequencyAfter <= 0 || frequencyAfter != _frequency)
            throw new InvalidOperationException("control-frame-clock-frequency-changed");
        Int128 delta = (Int128)now - _observedAt;
        if (now < _lastTimestamp || delta < 0 || delta > long.MaxValue)
            throw new InvalidOperationException("control-frame-clock-range-invalid");
        _lastTimestamp = now;
        Int128 remaining = _budgetScaled - delta * TimeSpan.TicksPerSecond;
        if (remaining <= 0)
        {
            Reject(BudgetEnd.Expired, new TimeoutException("control-frame-budget-expired"));
            return null;
        }
        // 向上取整仅影响通知排期；精确接受使用上面的整数 >= 比较，绝无 1ms 宽限。
        Int128 denominator = (Int128)_frequency * TimeSpan.TicksPerMillisecond;
        Int128 milliseconds = remaining / denominator + (remaining % denominator == 0 ? 0 : 1);
        TimeSpan due = TimeSpan.FromTicks((long)milliseconds * TimeSpan.TicksPerMillisecond);
        return IsPending() ? due : null;
    }

    private async Task RunAsync()
    {
        ITimer? timer = null;
        bool finishCandidate = false;
        try
        {
            // 同一 gate 发布屏障：Start 返回的原 worker 在外部同步代码前已登记。
            if (SampleDue() is null) return;
            if (FinishRequested()) finishCandidate = true;
            else
            {
                timer = _clock.CreateTimer(static state => ((ControlFrameBudget)state!).OnTimer(), this,
                    Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan)
                    ?? throw new InvalidOperationException("control-frame-timer-missing");
                // 即使 Create 重入 Stop，迟到 timer 仍无条件归此 worker 释放。
                lock (_gate) _timerAcquired = true;
                int immediateWakeups = 0;
                while (SampleDue() is { } due)
                {
                    if (FinishRequested()) { finishCandidate = true; break; }
                    if (!timer.Change(due, Timeout.InfiniteTimeSpan))
                        throw new InvalidOperationException("control-frame-timer-change-failed");
                    if (SampleDue() is null) break;
                    lock (_gate) _ready.TrySetResult();
                    Task wake;
                    lock (_gate)
                    {
                        if (_end != BudgetEnd.Pending) break;
                        wake = _wake.Task;
                    }
                    immediateWakeups = wake.IsCompleted ? immediateWakeups + 1 : 0;
                    if (immediateWakeups > 32)
                        throw new InvalidOperationException("control-frame-timer-immediate-wake-limit");
                    await wake.ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (_end != BudgetEnd.Pending) break;
                        _wake = NewSignal();
                    }
                }
            }
        }
        catch (Exception error) { Reject(BudgetEnd.Faulted, error); }
        finally
        {
            lock (_gate)
            {
                _quiescing = true;
                _ready.TrySetResult();
            }
            if (timer is not null)
            {
                Task? originalDispose = null;
                try
                {
                    originalDispose = timer.DisposeAsync().AsTask();
                    await originalDispose.ConfigureAwait(false);
                    lock (_gate) _timerReleased = true;
                }
                catch (Exception caught)
                {
                    AggregateException? container = originalDispose?.Exception;
                    Exception saved = container is null ? caught
                        : container.InnerExceptions.Count == 1 ? container.InnerExceptions[0] : container;
                    lock (_gate)
                    {
                        _cleanupError = saved;
                        _cleanupExitUnproven = originalDispose is null;
                        if (_end == BudgetEnd.Pending) _end = BudgetEnd.Faulted;
                        _decision.TrySetResult();
                    }
                }
            }
            // 成功必须等待原释放完成，再按同一锚点末检；清理耗时不返还预算。
            if (finishCandidate && IsPending())
            {
                try
                {
                    if (SampleDue() is not null)
                    {
                        lock (_gate)
                        {
                            if (_end == BudgetEnd.Pending) _end = BudgetEnd.FinishedInBudget;
                        }
                    }
                }
                catch (Exception error) { Reject(BudgetEnd.Faulted, error); }
            }
            lock (_gate)
            {
                // 正常路径均有终态；保守拒绝不完整的 worker 退出，不默认为成功。
                if (_end == BudgetEnd.Pending)
                {
                    _end = BudgetEnd.Faulted;
                    _error ??= new InvalidOperationException("control-frame-budget-no-decision");
                }
                _ready.TrySetResult();
                _decision.TrySetResult();
            }
        }
    }
}
