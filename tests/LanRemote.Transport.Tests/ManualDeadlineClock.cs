namespace LanRemote.Transport.Tests;

/// <summary>UTC 与单调时间独立推进，到期 timer 可延后至 FireTimers 才派发。</summary>
internal sealed class ManualDeadlineClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _utcNow;
    private long _timestamp;

    public ManualDeadlineClock(DateTimeOffset? utcNow = null)
    {
        _utcNow = utcNow ?? DateTimeOffset.UnixEpoch;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>尚未释放的 timer 数，包含已停用及已触发的一次性 timer。</summary>
    public int TimerCount
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count;
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _timestamp;
        }
    }

    /// <summary>仅推进单调时间；fireTimers 为 false 时保留到期 timer，不执行回调。</summary>
    public void Advance(TimeSpan elapsed, bool fireTimers = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        lock (_gate)
        {
            _timestamp = checked(_timestamp + elapsed.Ticks);
        }

        if (fireTimers)
        {
            FireTimers();
        }
    }

    /// <summary>仅改变 UTC，允许向前或向后跳变，不推进或派发 timer。</summary>
    public void AdvanceUtc(TimeSpan delta)
    {
        lock (_gate)
        {
            _utcNow += delta;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate)
        {
            ManualTimer timer = new(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }
    }

    /// <summary>
    /// 在调用线程派发当前到期的 timer，回调始终在锁外执行。
    /// 已错过的周期合并为一次回调，下一周期从当前单调时间起算。
    /// </summary>
    public void FireTimers()
    {
        while (true)
        {
            ManualTimer? timer;
            lock (_gate)
            {
                timer = _timers.FirstOrDefault(candidate => candidate.IsDue(_timestamp));
                if (timer is null)
                {
                    return;
                }

                timer.BeginCallback(_timestamp);
            }

            timer.InvokeCallback();
        }
    }

    // timer 的所有可变状态均由所属时钟的 _gate 保护。
    private sealed class ManualTimer : ITimer
    {
        private readonly ManualDeadlineClock _clock;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private long? _dueTimestamp;
        private long _periodTicks;
        private bool _disposed;
        private int _callbacksInFlight;
        private TaskCompletionSource? _callbacksDrained;

        public ManualTimer(ManualDeadlineClock clock, TimerCallback callback, object? state)
        {
            _clock = clock;
            _callback = callback;
            _state = state;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            ValidateTimeout(dueTime, nameof(dueTime));
            ValidateTimeout(period, nameof(period));
            lock (_clock._gate)
            {
                if (_disposed)
                {
                    return false;
                }

                _dueTimestamp = dueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : checked(_clock._timestamp + dueTime.Ticks);
                _periodTicks = period > TimeSpan.Zero ? period.Ticks : 0;
                return true;
            }
        }

        public void Dispose()
        {
            lock (_clock._gate)
            {
                _disposed = true;
                _dueTimestamp = null;
                _clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_clock._gate)
            {
                Dispose();
                if (_callbacksInFlight == 0)
                {
                    return ValueTask.CompletedTask;
                }

                _callbacksDrained ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                return new ValueTask(_callbacksDrained.Task);
            }
        }

        // 仅在持有时钟锁时选择并预定回调，随后才离锁执行。
        public bool IsDue(long timestamp) => _dueTimestamp is long due && due <= timestamp;

        public void BeginCallback(long timestamp)
        {
            _dueTimestamp = _periodTicks == 0 ? null : checked(timestamp + _periodTicks);
            _callbacksInFlight++;
        }

        public void InvokeCallback()
        {
            try
            {
                _callback(_state);
            }
            finally
            {
                lock (_clock._gate)
                {
                    _callbacksInFlight--;
                    if (_callbacksInFlight == 0)
                    {
                        _callbacksDrained?.TrySetResult();
                    }
                }
            }
        }

        private static void ValidateTimeout(TimeSpan value, string parameterName)
        {
            if (value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(parameterName, value, "时限必须非负或为无限等待。");
            }
        }
    }
}
