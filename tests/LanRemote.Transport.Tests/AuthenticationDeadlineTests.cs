namespace LanRemote.Transport.Tests;

public sealed class AuthenticationDeadlineTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Fact]
    public void Starts_With_Full_Budget_At_A_Nonzero_Monotonic_Timestamp()
    {
        ManualDeadlineClock clock = new();
        clock.Advance(TimeSpan.FromDays(3));
        using AuthenticationDeadline deadline = new(clock, Budget, CancellationToken.None);

        Assert.Equal(Budget, deadline.Remaining);
        Assert.False(deadline.IsExpired);
        Assert.True(deadline.Token.CanBeCanceled);
        Assert.False(deadline.Token.IsCancellationRequested);
        Assert.Equal(1, clock.TimerCount);
    }

    [Fact]
    public void Remaining_Decreases_From_The_Original_Start_And_Never_Renews_The_Budget()
    {
        ManualDeadlineClock clock = new();
        using AuthenticationDeadline deadline = new(clock, Budget, CancellationToken.None);

        for (int seconds = 1; seconds <= 12; seconds++)
        {
            clock.Advance(TimeSpan.FromSeconds(1), fireTimers: false);
            TimeSpan expected = TimeSpan.FromSeconds(Math.Max(0, 10 - seconds));
            Assert.Equal(expected, deadline.Remaining);
            Assert.Equal(expected, deadline.Remaining);
            Assert.Equal(seconds >= 10, deadline.IsExpired);
        }

        Assert.False(deadline.Token.IsCancellationRequested);
    }

    [Fact]
    public void Exact_Deadline_Is_Expired_Even_Before_The_Timer_Callback_Is_Dispatched()
    {
        ManualDeadlineClock clock = new();
        using AuthenticationDeadline deadline = new(clock, Budget, CancellationToken.None);
        int callbacks = 0;
        using CancellationTokenRegistration registration = deadline.Token.Register(() => callbacks++);

        clock.Advance(Budget - TimeSpan.FromTicks(1), fireTimers: false);
        Assert.False(deadline.IsExpired);
        Assert.Equal(TimeSpan.FromTicks(1), deadline.Remaining);

        clock.Advance(TimeSpan.FromTicks(1), fireTimers: false);
        Assert.True(deadline.IsExpired);
        Assert.Equal(TimeSpan.Zero, deadline.Remaining);
        Assert.False(deadline.Token.IsCancellationRequested);
        Assert.Equal(0, callbacks);

        clock.FireTimers();
        Assert.True(deadline.Token.IsCancellationRequested);
        Assert.Equal(1, callbacks);
        clock.FireTimers();
        Assert.Equal(1, callbacks);
    }

    [Fact]
    public void Timer_Cancels_The_Linked_Token_At_The_Original_Deadline()
    {
        ManualDeadlineClock clock = new();
        using CancellationTokenSource caller = new();
        using AuthenticationDeadline deadline = new(clock, Budget, caller.Token);

        clock.Advance(Budget - TimeSpan.FromMilliseconds(1));
        Assert.False(deadline.Token.IsCancellationRequested);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(deadline.Token.IsCancellationRequested);
        Assert.True(deadline.IsExpired);
        Assert.False(caller.IsCancellationRequested);
    }

    [Fact]
    public void Utc_Jumps_Do_Not_Affect_Expiration_Remaining_Or_Timer_Dispatch()
    {
        ManualDeadlineClock clock = new();
        using AuthenticationDeadline deadline = new(clock, Budget, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(4));

        clock.AdvanceUtc(TimeSpan.FromDays(365));
        clock.FireTimers();
        Assert.Equal(TimeSpan.FromSeconds(6), deadline.Remaining);
        Assert.False(deadline.IsExpired);
        Assert.False(deadline.Token.IsCancellationRequested);

        clock.AdvanceUtc(TimeSpan.FromDays(-730));
        clock.FireTimers();
        Assert.Equal(TimeSpan.FromSeconds(6), deadline.Remaining);
        Assert.False(deadline.IsExpired);
        Assert.False(deadline.Token.IsCancellationRequested);

        clock.Advance(TimeSpan.FromSeconds(6));
        clock.AdvanceUtc(TimeSpan.FromDays(-365));
        Assert.True(deadline.IsExpired);
        Assert.Equal(TimeSpan.Zero, deadline.Remaining);
        Assert.True(deadline.Token.IsCancellationRequested);
    }

    [Fact]
    public void Timer_Setup_Deducts_Time_Elapsed_After_Capturing_The_Start()
    {
        ManualDeadlineClock manual = new();
        TimestampCaptureClock clock = new(manual, TimeSpan.FromSeconds(3));
        using AuthenticationDeadline deadline = new(clock, Budget, CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(7), deadline.Remaining);
        Assert.False(deadline.Token.IsCancellationRequested);
        manual.Advance(TimeSpan.FromSeconds(7) - TimeSpan.FromTicks(1));
        Assert.False(deadline.Token.IsCancellationRequested);
        Assert.False(deadline.IsExpired);

        manual.Advance(TimeSpan.FromTicks(1));
        Assert.True(deadline.Token.IsCancellationRequested);
        Assert.True(deadline.IsExpired);
        Assert.Equal(TimeSpan.Zero, deadline.Remaining);
    }

    [Fact]
    public void Budget_Exhausted_During_Construction_Cancels_Immediately()
    {
        ManualDeadlineClock manual = new();
        TimestampCaptureClock clock = new(manual, Budget + TimeSpan.FromSeconds(1));
        using AuthenticationDeadline deadline = new(clock, Budget, CancellationToken.None);

        Assert.True(deadline.IsExpired);
        Assert.Equal(TimeSpan.Zero, deadline.Remaining);
        Assert.True(deadline.Token.IsCancellationRequested);
    }

    [Fact]
    public void Synchronous_Timer_Callback_During_Construction_Uses_An_Initialized_Source()
    {
        ManualDeadlineClock manual = new();
        TimerCaptureClock clock = new(manual, () => manual.Advance(Budget));
        using AuthenticationDeadline deadline = new(clock, Budget, CancellationToken.None);

        Assert.True(deadline.Token.IsCancellationRequested);
        Assert.True(deadline.IsExpired);
        Assert.Equal(TimeSpan.Zero, deadline.Remaining);
    }

    [Fact]
    public void Budget_Exhausted_While_Creating_The_Timer_Cancels_Without_Timer_Dispatch()
    {
        ManualDeadlineClock manual = new();
        TimerCaptureClock clock = new(manual, () => manual.Advance(Budget, fireTimers: false));
        using AuthenticationDeadline deadline = new(clock, Budget, CancellationToken.None);

        Assert.True(deadline.Token.IsCancellationRequested);
        Assert.True(deadline.IsExpired);
        Assert.Equal(TimeSpan.Zero, deadline.Remaining);
    }

    [Fact]
    public void Zero_Budget_Is_Immediately_Expired_And_Cancelled()
    {
        ManualDeadlineClock clock = new();
        using AuthenticationDeadline deadline = new(clock, TimeSpan.Zero, CancellationToken.None);

        Assert.True(deadline.IsExpired);
        Assert.Equal(TimeSpan.Zero, deadline.Remaining);
        Assert.True(deadline.Token.IsCancellationRequested);
    }

    [Fact]
    public void Caller_Cancellation_Propagates_Without_Expiring_The_Deadline()
    {
        ManualDeadlineClock clock = new();
        using CancellationTokenSource caller = new();
        using AuthenticationDeadline deadline = new(clock, Budget, caller.Token);
        clock.Advance(TimeSpan.FromSeconds(2));

        caller.Cancel();

        Assert.True(deadline.Token.IsCancellationRequested);
        Assert.False(deadline.IsExpired);
        Assert.Equal(TimeSpan.FromSeconds(8), deadline.Remaining);
    }

    [Fact]
    public void Already_Cancelled_Caller_Produces_A_Cancelled_Token()
    {
        ManualDeadlineClock clock = new();
        using CancellationTokenSource caller = new();
        caller.Cancel();
        using AuthenticationDeadline deadline = new(clock, Budget, caller.Token);

        Assert.True(deadline.Token.IsCancellationRequested);
        Assert.False(deadline.IsExpired);
        Assert.Equal(Budget, deadline.Remaining);
    }

    [Fact]
    public void Explicit_Cancel_Notifies_Once_Without_Cancelling_The_Caller_Or_Changing_The_Deadline()
    {
        ManualDeadlineClock clock = new();
        using CancellationTokenSource caller = new();
        using AuthenticationDeadline deadline = new(clock, Budget, caller.Token);
        int callbacks = 0;
        using CancellationTokenRegistration registration = deadline.Token.Register(() => callbacks++);

        deadline.Cancel();
        deadline.Cancel();

        Assert.Equal(1, callbacks);
        Assert.True(deadline.Token.IsCancellationRequested);
        Assert.False(caller.IsCancellationRequested);
        Assert.False(deadline.IsExpired);
        Assert.Equal(Budget, deadline.Remaining);

        clock.Advance(Budget);
        Assert.Equal(1, callbacks);
        Assert.True(deadline.IsExpired);
    }

    [Fact]
    public void Timer_Cancellation_Isolates_Throwing_Callbacks_And_Notifies_All_Other_Waiters()
    {
        ManualDeadlineClock clock = new();
        using CancellationTokenSource caller = new();
        using AuthenticationDeadline deadline = new(clock, Budget, caller.Token);
        int callbacks = 0;
        int throwingCallbacks = 0;
        using CancellationTokenRegistration before = deadline.Token.Register(() => callbacks++);
        using CancellationTokenRegistration throwing = deadline.Token.Register(() =>
        {
            throwingCallbacks++;
            throw new InvalidOperationException("取消回调故障");
        });
        using CancellationTokenRegistration after = deadline.Token.Register(() => callbacks++);
        clock.Advance(Budget, fireTimers: false);

        Assert.Null(Record.Exception(() => clock.FireTimers()));

        Assert.True(deadline.Token.IsCancellationRequested);
        Assert.True(deadline.IsExpired);
        Assert.False(caller.IsCancellationRequested);
        Assert.Equal(2, callbacks);
        Assert.Equal(1, throwingCallbacks);
        Assert.Null(Record.Exception(() => clock.FireTimers()));
        Assert.Null(Record.Exception(caller.Cancel));
        Assert.Equal(2, callbacks);
        Assert.Equal(1, throwingCallbacks);
    }

    [Fact]
    public void Caller_Cancellation_Isolates_Throwing_Deadline_Callbacks_And_Notifies_All_Other_Waiters()
    {
        ManualDeadlineClock clock = new();
        using CancellationTokenSource caller = new();
        int callerCallbacks = 0;
        using CancellationTokenRegistration callerBefore = caller.Token.Register(() => callerCallbacks++);
        using AuthenticationDeadline deadline = new(clock, Budget, caller.Token);
        using CancellationTokenRegistration callerAfter = caller.Token.Register(() => callerCallbacks++);
        int callbacks = 0;
        int throwingCallbacks = 0;
        using CancellationTokenRegistration before = deadline.Token.Register(() => callbacks++);
        using CancellationTokenRegistration throwing = deadline.Token.Register(() =>
        {
            throwingCallbacks++;
            throw new InvalidOperationException("取消回调故障");
        });
        using CancellationTokenRegistration after = deadline.Token.Register(() => callbacks++);

        Assert.Null(Record.Exception(caller.Cancel));

        Assert.True(deadline.Token.IsCancellationRequested);
        Assert.True(caller.IsCancellationRequested);
        Assert.False(deadline.IsExpired);
        Assert.Equal(Budget, deadline.Remaining);
        Assert.Equal(2, callbacks);
        Assert.Equal(1, throwingCallbacks);
        Assert.Equal(2, callerCallbacks);
        Assert.Null(Record.Exception(caller.Cancel));
        Assert.Equal(2, callbacks);
        Assert.Equal(1, throwingCallbacks);
        Assert.Equal(2, callerCallbacks);
    }

    [Fact]
    public void Explicit_Cancellation_Isolates_Throwing_Callbacks_And_Notifies_All_Other_Waiters()
    {
        ManualDeadlineClock clock = new();
        using CancellationTokenSource caller = new();
        using AuthenticationDeadline deadline = new(clock, Budget, caller.Token);
        int callbacks = 0;
        int throwingCallbacks = 0;
        using CancellationTokenRegistration before = deadline.Token.Register(() => callbacks++);
        using CancellationTokenRegistration throwing = deadline.Token.Register(() =>
        {
            throwingCallbacks++;
            throw new InvalidOperationException("取消回调故障");
        });
        using CancellationTokenRegistration after = deadline.Token.Register(() => callbacks++);

        Assert.Null(Record.Exception(deadline.Cancel));

        Assert.True(deadline.Token.IsCancellationRequested);
        Assert.False(caller.IsCancellationRequested);
        Assert.False(deadline.IsExpired);
        Assert.Equal(Budget, deadline.Remaining);
        Assert.Equal(2, callbacks);
        Assert.Equal(1, throwingCallbacks);
        Assert.Null(Record.Exception(deadline.Cancel));
        Assert.Null(Record.Exception(caller.Cancel));
        Assert.Equal(2, callbacks);
        Assert.Equal(1, throwingCallbacks);
    }

    [Fact]
    public void Dispose_Releases_Both_Sources_Without_Cancelling_Or_Invoking_Callbacks()
    {
        ManualDeadlineClock clock = new();
        using CancellationTokenSource caller = new();
        using AuthenticationDeadline deadline = new(clock, Budget, caller.Token);
        CancellationToken token = deadline.Token;
        int callbacks = 0;
        using CancellationTokenRegistration registration = token.Register(() => callbacks++);

        deadline.Dispose();
        deadline.Dispose();

        Assert.Equal(0, clock.TimerCount);
        Assert.False(token.IsCancellationRequested);
        Assert.False(caller.IsCancellationRequested);
        Assert.Equal(0, callbacks);
        Assert.Throws<ObjectDisposedException>(() => deadline.Cancel());

        caller.Cancel();
        clock.Advance(Budget);
        Assert.False(token.IsCancellationRequested);
        Assert.Equal(0, callbacks);
    }

    [Fact]
    public void Dispose_Prevents_A_Deferred_Deadline_Callback_From_Being_Dispatched()
    {
        ManualDeadlineClock clock = new();
        using AuthenticationDeadline deadline = new(clock, Budget, CancellationToken.None);
        CancellationToken token = deadline.Token;
        int callbacks = 0;
        using CancellationTokenRegistration registration = token.Register(() => callbacks++);
        clock.Advance(Budget, fireTimers: false);
        Assert.True(deadline.IsExpired);
        Assert.False(token.IsCancellationRequested);

        deadline.Dispose();
        clock.FireTimers();

        Assert.Equal(0, clock.TimerCount);
        Assert.False(token.IsCancellationRequested);
        Assert.Equal(0, callbacks);
    }

    [Fact]
    public void Timer_Callback_Already_Queued_Before_Dispose_Does_Not_Throw_Or_Cancel()
    {
        ManualDeadlineClock manual = new();
        TimerCaptureClock clock = new(manual);
        using CancellationTokenSource caller = new();
        using AuthenticationDeadline deadline = new(clock, Budget, caller.Token);
        CancellationToken token = deadline.Token;
        int callbacks = 0;
        using CancellationTokenRegistration registration = token.Register(() => callbacks++);
        manual.Advance(Budget, fireTimers: false);

        deadline.Dispose();

        // 模拟已被调度的回调在 timer 与 CTS 释放后才真正进入。
        Assert.Null(Record.Exception(clock.FireCapturedCallback));
        Assert.Null(Record.Exception(caller.Cancel));
        Assert.Equal(0, manual.TimerCount);
        Assert.False(token.IsCancellationRequested);
        Assert.Equal(0, callbacks);
        Assert.Throws<ObjectDisposedException>(deadline.Cancel);
    }

    [Fact]
    public void Constructor_Rejects_Null_Clock_And_Negative_Budget()
    {
        Assert.Throws<ArgumentNullException>(
            () => new AuthenticationDeadline(null!, Budget, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AuthenticationDeadline(new ManualDeadlineClock(), TimeSpan.FromTicks(-1), CancellationToken.None));
    }

    [Fact]
    public void Manual_Clock_Advances_Utc_And_Monotonic_Time_Independently()
    {
        DateTimeOffset utc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        ManualDeadlineClock clock = new(utc);
        long start = clock.GetTimestamp();

        clock.Advance(TimeSpan.FromSeconds(2), fireTimers: false);
        Assert.Equal(utc, clock.GetUtcNow());
        Assert.Equal(TimeSpan.FromSeconds(2), clock.GetElapsedTime(start));

        clock.AdvanceUtc(TimeSpan.FromDays(-1));
        Assert.Equal(utc - TimeSpan.FromDays(1), clock.GetUtcNow());
        Assert.Equal(TimeSpan.FromSeconds(2), clock.GetElapsedTime(start));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(TimeSpan.FromTicks(-1)));
        Assert.Equal(TimeSpan.FromSeconds(2), clock.GetElapsedTime(start));
    }

    [Fact]
    public void Manual_Timer_Change_Reschedules_Disables_And_Reenables_A_Timer()
    {
        ManualDeadlineClock clock = new();
        object state = new();
        object? receivedState = null;
        int callbacks = 0;
        using ITimer timer = clock.CreateTimer(s =>
        {
            receivedState = s;
            callbacks++;
        }, state, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);

        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.True(timer.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(0, callbacks);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, callbacks);
        Assert.Same(state, receivedState);

        Assert.True(timer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan));
        Assert.True(timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan));
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(1, callbacks);

        Assert.True(timer.Change(TimeSpan.Zero, TimeSpan.Zero));
        Assert.Equal(1, callbacks);
        clock.Advance(TimeSpan.Zero);
        Assert.Equal(2, callbacks);
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(2, callbacks);
    }

    [Fact]
    public void Manual_Timer_Periodic_Callbacks_Can_Be_Changed_And_Disposed()
    {
        ManualDeadlineClock clock = new();
        int callbacks = 0;
        using ITimer timer = clock.CreateTimer(_ => callbacks++, null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, callbacks);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(2, callbacks);
        clock.Advance(TimeSpan.FromSeconds(20), fireTimers: false);
        Assert.Equal(2, callbacks);
        clock.FireTimers();
        Assert.Equal(3, callbacks);
        clock.FireTimers();
        Assert.Equal(3, callbacks);

        Assert.True(timer.Change(TimeSpan.FromSeconds(1), TimeSpan.Zero));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(4, callbacks);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(4, callbacks);

        Assert.True(timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        timer.Dispose();
        timer.Dispose();
        clock.FireTimers();
        Assert.Equal(4, callbacks);
        Assert.Equal(0, clock.TimerCount);
        Assert.False(timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan));
    }

    [Fact]
    public async Task Manual_Timer_DisposeAsync_Removes_A_Deferred_Timer()
    {
        ManualDeadlineClock clock = new();
        int callbacks = 0;
        await using ITimer timer = clock.CreateTimer(_ => callbacks++, null,
            TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        clock.Advance(TimeSpan.FromSeconds(1), fireTimers: false);

        await timer.DisposeAsync();
        await timer.DisposeAsync();
        clock.FireTimers();

        Assert.Equal(0, callbacks);
        Assert.Equal(0, clock.TimerCount);
        Assert.False(timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan));
    }

    [Fact]
    public async Task Manual_Timer_DisposeAsync_Waits_For_InFlight_Callback_Without_Holding_The_Clock_Lock()
    {
        ManualDeadlineClock clock = new();
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        bool callbackReleased = false;
        using ITimer timer = clock.CreateTimer(_ =>
        {
            entered.Set();
            callbackReleased = release.Wait(TimeSpan.FromSeconds(10));
        }, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        Exception? workerError = null;
        Thread worker = new(() =>
        {
            try
            {
                clock.Advance(TimeSpan.FromSeconds(1));
            }
            catch (Exception error)
            {
                workerError = error;
            }
        }) { IsBackground = true };
        worker.Start();

        ValueTask disposal = default;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "timer 回调应当开始执行。");
            // 回调仍在另一线程等待；以下操作必须能取得时钟锁，异步释放则等待回调退出。
            Assert.Equal(TimeSpan.FromSeconds(1).Ticks, clock.GetTimestamp());
            clock.AdvanceUtc(TimeSpan.FromDays(1));
            disposal = timer.DisposeAsync();
            Assert.False(disposal.IsCompleted);
            Assert.Equal(0, clock.TimerCount);
            Assert.False(timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan));
        }
        finally
        {
            release.Set();
            Assert.True(worker.Join(TimeSpan.FromSeconds(10)), "timer 回调线程应当结束。");
        }

        await disposal.AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(callbackReleased);
        Assert.Null(workerError);
    }

    // 捕获入口以模拟释放后才进入的回调，也可在创建返回前推进时钟。
    private sealed class TimerCaptureClock(ManualDeadlineClock inner, Action? duringCreation = null) : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long GetTimestamp() => inner.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            ITimer timer = inner.CreateTimer(callback, state, dueTime, period);
            duringCreation?.Invoke();
            return timer;
        }

        public void FireCapturedCallback() => _callback!(_state);
    }

    // 精确模拟“已经取得起点，但建立 timer 前消耗了时间”，不依赖真实等待。
    private sealed class TimestampCaptureClock(ManualDeadlineClock inner, TimeSpan elapsedAfterCapture) : TimeProvider
    {
        private int _timestampReads;

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long GetTimestamp()
        {
            long timestamp = inner.GetTimestamp();
            if (Interlocked.Increment(ref _timestampReads) == 1)
            {
                inner.Advance(elapsedAfterCapture, fireTimers: false);
            }

            return timestamp;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            inner.CreateTimer(callback, state, dueTime, period);
    }
}
