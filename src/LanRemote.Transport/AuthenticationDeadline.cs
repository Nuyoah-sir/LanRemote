namespace LanRemote.Transport;

/// <summary>从单调起点计算的认证时限；timer 回调延后不能延长预算。</summary>
internal sealed class AuthenticationDeadline : IDisposable
{
    private readonly TimeProvider _clock;
    private readonly TimeSpan _budget;
    private readonly long _startTimestamp;
    private readonly CancellationTokenSource _cancellation;
    private readonly ITimer? _timer;
    private readonly CancellationTokenRegistration _parentRegistration;

    public AuthenticationDeadline(TimeProvider clock, TimeSpan budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThan(budget, TimeSpan.Zero);

        _clock = clock;
        _budget = budget;
        _startTimestamp = clock.GetTimestamp();

        // 注册与 timer 创建均可能同步回调；必须先初始化回调唯一依赖的 CTS。
        _cancellation = new CancellationTokenSource();
        try
        {
            _parentRegistration = cancellationToken.Register(
                static state => ((AuthenticationDeadline)state!).CancelFromCallback(), this);

            // 先确定起点，再扣除建立 timer 前已经消耗的时间。
            TimeSpan remaining = Remaining;
            if (remaining > TimeSpan.Zero)
            {
                _timer = clock.CreateTimer(
                    static state => ((AuthenticationDeadline)state!).CancelFromCallback(),
                    this, remaining, Timeout.InfiniteTimeSpan);
            }

            // 零预算及初始化期间耗尽的预算不依赖 timer 派发。
            if (remaining == TimeSpan.Zero || IsExpired)
            {
                Cancel();
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public TimeSpan Remaining
    {
        get
        {
            TimeSpan elapsed = _clock.GetElapsedTime(_startTimestamp);
            return elapsed >= _budget ? TimeSpan.Zero : _budget - elapsed;
        }
    }

    public bool IsExpired => _clock.GetElapsedTime(_startTimestamp) >= _budget;

    public CancellationToken Token => _cancellation.Token;

    /// <summary>审批结束时显式通知等待者；不改变时限，也不取消调用方。</summary>
    public void Cancel()
    {
        try
        {
            _cancellation.Cancel(throwOnFirstException: false);
        }
        catch (AggregateException)
        {
            // 所有回调都已得到执行机会，隔离本机回调故障。
        }
    }

    private void CancelFromCallback()
    {
        try
        {
            Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已入队的 timer 或父取消回调可能在释放后才进入。
        }
    }

    public void Dispose()
    {
        // 释放不等于取消，避免在收尾路径同步执行 gate 的取消回调。
        _timer?.Dispose();
        _parentRegistration.Dispose();
        _cancellation.Dispose();
    }
}
