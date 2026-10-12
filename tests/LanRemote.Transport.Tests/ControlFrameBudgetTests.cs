using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;
using Xunit.Sdk;
using BudgetEnd = LanRemote.Transport.ControlFrameBudget.BudgetEnd;
using BudgetSnapshot = LanRemote.Transport.ControlFrameBudget.BudgetSnapshot;

namespace LanRemote.Transport.Tests;

public sealed class ControlFrameBudgetTests
{
    // Guard 只防夹具挂死；预算、先后关系及原操作退出均由显式样本和真实 await 链证明。
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private const int TestTimeout = 60_000;
    private const long Frequency = TimeSpan.TicksPerSecond;
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    [ThreadStatic] private static bool _insideCallback;

    [Fact(Timeout = TestTimeout)]
    public async Task ConstructorIsPureAndSnapshotsAreIndependent()
    {
        Fixture f = new();
        try
        {
            Assert.Equal("clock", Assert.Throws<ArgumentNullException>(() =>
                new ControlFrameBudget(null!, 0, 1, TimeSpan.Zero)).ParamName);
            Assert.Null(f.Budget.Completion);
            Assert.False(f.Budget.Ready.IsCompleted);
            Assert.False(f.Budget.Decision.IsCompleted);
            BudgetSnapshot initial = f.Budget.Snapshot;
            AssertSnapshot(initial, BudgetEnd.Pending);
            Assert.NotSame(initial, f.Budget.Snapshot);
            Assert.Empty(f.Calls);
            f.Budget.RequestStop();
            AssertSnapshot(initial, BudgetEnd.Pending);
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.Stopped, stop: true);
            Assert.Empty(f.Calls);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(long.MinValue)]
    [InlineData(-1L)]
    [InlineData(0L)]
    public async Task ConstructorRejectsNonpositiveObservedFrequencyWithoutClockAccess(long frequency)
    {
        Fixture f = new();
        try
        {
            Assert.Equal("observedFrequency", Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ControlFrameBudget(f.Clock, 0, frequency, TimeSpan.FromSeconds(1))).ParamName);
            Assert.Empty(f.Calls);
        }
        finally { await f.FinishAsync(); }
    }

    public static IEnumerable<object[]> InvalidBudgets()
    {
        yield return [TimeSpan.MinValue];
        yield return [TimeSpan.FromTicks(-1)];
        yield return [ControlFrameBudget.MaxBudget + TimeSpan.FromTicks(1)];
        yield return [TimeSpan.MaxValue];
    }

    [Theory(Timeout = TestTimeout)]
    [MemberData(nameof(InvalidBudgets))]
    public async Task ConstructorRejectsBudgetOutsideClosedSupportedRange(TimeSpan budget)
    {
        Fixture f = new();
        try
        {
            Assert.Equal("budget", Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ControlFrameBudget(f.Clock, long.MinValue, 1, budget)).ParamName);
            Assert.Empty(f.Calls);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ZeroBudgetExpiresWithoutAnyClockOrTimerOperation()
    {
        Fixture f = new(budget: TimeSpan.Zero);
        try
        {
            Task completion = f.Budget.Start();
            await DrainAsync(f);
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.Expired, error: Expiry(f));
            Assert.Same(completion, f.Budget.Completion);
            Assert.Empty(f.Calls);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task FinishBeforeStartThrowsEvenWhenAlreadyStopped()
    {
        Fixture f = new();
        try
        {
            Assert.Throws<InvalidOperationException>(f.Budget.RequestFinish);
            f.Budget.RequestStop();
            Assert.Throws<InvalidOperationException>(f.Budget.RequestFinish);
            Assert.Null(f.Budget.Completion);
            Assert.Empty(f.Calls);
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.Stopped, stop: true);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task StopBeforeStartNotifiesImmediatelyAndSkipsAllExternalOperations()
    {
        Fixture f = new();
        try
        {
            f.Budget.RequestStop();
            f.Budget.RequestStop();
            Assert.True(f.Budget.Ready.IsCompletedSuccessfully);
            Assert.True(f.Budget.Decision.IsCompletedSuccessfully);
            Assert.Null(f.Budget.Completion);
            Task completion = f.Budget.Start();
            await DrainAsync(f);
            f.Budget.RequestFinish();
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.Stopped, stop: true);
            Assert.Empty(f.Calls);
            Assert.Throws<InvalidOperationException>(() => { _ = f.Budget.Start(); });
            Assert.Same(completion, f.Budget.Completion);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task DelayedStartDoesNotRenewAnchor()
    {
        Fixture f = new(anchor: 100, budget: TimeSpan.FromTicks(20));
        f.Clock.Now = 120;
        try
        {
            _ = f.Budget.Start();
            await f.Budget.Ready.WaitAsync(Guard);
            // 若错误地在 Start 续期，Ready 仍会到达，但此断言直接变红，不等待 Guard 到期。
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.Expired, error: Expiry(f));
            Assert.Equal(0, f.Clock.CreateCalls);
            await DrainAsync(f);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task AlreadyExpiredObservedAnchorIsNotRenewedByConstruction()
    {
        // 旧锚点在构造前已经到期；构造和 Start 均不得重新获取锚点续期。
        Fixture f = new(anchor: 100, budget: TimeSpan.FromTicks(20), initialNow: 120);
        try
        {
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.Pending);
            _ = f.Budget.Start();
            await f.Budget.Ready.WaitAsync(Guard);
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.Expired, error: Expiry(f));
            Assert.Equal(0, f.Clock.CreateCalls);
            await DrainAsync(f);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(0, 1000)]
    [InlineData(250, 750)]
    [InlineData(999, 1)]
    public async Task DelayedStartArmsOnlyRemainingOriginalBudget(int elapsedMs, int dueMs)
    {
        Fixture f = new(anchor: 27);
        f.Clock.Now = 27 + elapsedMs * TimeSpan.TicksPerMillisecond;
        try
        {
            _ = f.Budget.Start();
            await f.Budget.Ready.WaitAsync(Guard);
            Assert.Equal(TimeSpan.FromMilliseconds(dueMs), Assert.Single(f.Timer.Changes).Due);
            Assert.Equal(Timeout.InfiniteTimeSpan, f.Clock.CreatedDue);
            Assert.Equal(Timeout.InfiniteTimeSpan, f.Clock.CreatedPeriod);
            f.Budget.RequestFinish();
            await DrainAsync(f);
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.FinishedInBudget, finish: true, acquired: true, released: true);
        }
        finally { await f.FinishAsync(); }
    }

    public static IEnumerable<object[]> ExactBudgets()
    {
        yield return [0L, 3L, 3_333_334L, 1L, false, 1L];
        yield return [0L, 3L, 3_333_333L, 1L, true, 0L];
        yield return [0L, long.MaxValue, 1L, 922_337_203_685L, false, 1L];
        yield return [0L, long.MaxValue, 1L, 922_337_203_686L, true, 0L];
        yield return [0L, long.MaxValue, Frequency, long.MaxValue, true, 0L];
        yield return [long.MinValue, long.MaxValue, Frequency + 1, -1L, false, 1L];
        yield return [0L, 1L, ControlFrameBudget.MaxBudget.Ticks, long.MaxValue, true, 0L];
        yield return [0L, long.MaxValue, ControlFrameBudget.MaxBudget.Ticks, 0L, false, 4_294_967_294L];
        yield return [0L, Frequency, 1L, 0L, false, 1L];
        yield return [0L, Frequency, 1L, 1L, true, 0L];
    }

    [Theory(Timeout = TestTimeout)]
    [MemberData(nameof(ExactBudgets))]
    public async Task ExactInt128ComparisonAndCeilingSchedulingHaveNoGrace(
        long anchor, long frequency, long ticks, long now, bool expired, long dueMs)
    {
        Fixture f = new(anchor, frequency, TimeSpan.FromTicks(ticks));
        f.Clock.Now = now;
        try
        {
            _ = f.Budget.Start();
            await f.Budget.Ready.WaitAsync(Guard);
            if (expired)
            {
                Assert.Equal(BudgetEnd.Expired, f.Budget.Snapshot.End);
                Assert.Equal(0, f.Clock.CreateCalls);
            }
            else
            {
                Assert.Equal(BudgetEnd.Pending, f.Budget.Snapshot.End);
                Assert.Equal(TimeSpan.FromTicks(dueMs * TimeSpan.TicksPerMillisecond), Assert.Single(f.Timer.Changes).Due);
                f.Budget.RequestFinish();
            }
            await DrainAsync(f);
            AssertSnapshot(f.Budget.Snapshot, expired ? BudgetEnd.Expired : BudgetEnd.FinishedInBudget,
                finish: !expired, acquired: !expired, released: !expired, error: expired ? Expiry(f) : null);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task SystemTimerAcceptsExactMaxBudgetForCreateAndChangeWithoutWaitingForExpiry()
    {
        Assert.Equal(TimeSpan.FromTicks(4_294_967_294L * TimeSpan.TicksPerMillisecond), ControlFrameBudget.MaxBudget);
        int callbacks = 0;
        ITimer timer = TimeProvider.System.CreateTimer(_ => Interlocked.Increment(ref callbacks), null,
            ControlFrameBudget.MaxBudget, Timeout.InfiniteTimeSpan);
        try
        {
            Assert.True(timer.Change(ControlFrameBudget.MaxBudget, Timeout.InfiniteTimeSpan));
            Assert.True(timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan));
        }
        finally { await timer.DisposeAsync(); }
        Assert.Equal(0, Volatile.Read(ref callbacks));
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("create")]
    [InlineData("change")]
    public async Task SynchronousSetupTimeIsChargedToOriginalBudget(string point)
    {
        Fixture f = new(budget: TimeSpan.FromTicks(10));
        if (point == "create") f.Clock.CreateHook = () => f.Clock.Now = 10;
        else f.Timer.ChangeHook = _ => f.Clock.Now = 10;
        try
        {
            _ = f.Budget.Start();
            await f.Budget.Ready.WaitAsync(Guard);
            Assert.Equal(BudgetEnd.Expired, f.Budget.Snapshot.End);
            await DrainAsync(f);
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.Expired, acquired: true, released: true, error: Expiry(f));
            Assert.Equal(point == "create" ? 0 : 1, f.Timer.ChangeCalls);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(0L, false)]
    [InlineData(-1L, false)]
    [InlineData(0L, true)]
    [InlineData(-1L, true)]
    [InlineData(Frequency + 1, false)]
    [InlineData(Frequency + 1, true)]
    public async Task ChangedOrNonpositiveFrequencyBeforeOrAfterSampleIsRejected(long invalid, bool after)
    {
        Fixture f = new();
        f.Clock.ReadFrequency = call => after && call == 1 ? Frequency : invalid;
        try
        {
            _ = f.Budget.Start();
            await DrainAsync(f);
            InvalidOperationException error = Assert.IsType<InvalidOperationException>(f.Budget.Snapshot.Error);
            Assert.Equal("control-frame-clock-frequency-changed", error.Message);
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.Faulted, error: error);
            Assert.Equal(after ? 1 : 0, f.Clock.TimestampReads);
            Assert.Equal(0, f.Clock.CreateCalls);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("frequency")]
    [InlineData("below-anchor")]
    [InlineData("above-anchor-regression")]
    public async Task LaterClockRegressionOrFrequencyChangeIsRejected(string failure)
    {
        Fixture f = new(anchor: 10);
        f.Clock.Now = 20;
        try
        {
            Task completion = f.Budget.Start();
            await WaitForWakeAsync(f, completion);
            if (failure == "frequency") f.Clock.Frequency = Frequency + 1;
            else f.Clock.Now = failure == "below-anchor" ? 9 : 19;
            f.Timer.Fire();
            await f.Budget.Decision.WaitAsync(Guard);
            await DrainAsync(f);
            InvalidOperationException error = Assert.IsType<InvalidOperationException>(f.Budget.Snapshot.Error);
            Assert.Equal(failure == "frequency" ? "control-frame-clock-frequency-changed" : "control-frame-clock-range-invalid", error.Message);
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.Faulted, acquired: true, released: true, error: error);
            Assert.Equal(1, f.Timer.ChangeCalls);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(long.MinValue, 0L)]
    [InlineData(-1L, long.MaxValue)]
    [InlineData(10L, 9L)]
    public async Task OutOfRangeTimestampDeltaCannotWrapIntoValidBudget(long anchor, long now)
    {
        Fixture f = new(anchor, long.MaxValue, ControlFrameBudget.MaxBudget);
        f.Clock.Now = now;
        try
        {
            _ = f.Budget.Start();
            await DrainAsync(f);
            InvalidOperationException error = Assert.IsType<InvalidOperationException>(f.Budget.Snapshot.Error);
            Assert.Equal("control-frame-clock-range-invalid", error.Message);
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.Faulted, error: error);
            Assert.Equal(0, f.Clock.CreateCalls);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("frequency", false)]
    [InlineData("timestamp", false)]
    [InlineData("create", false)]
    [InlineData("change", false)]
    [InlineData("frequency", true)]
    [InlineData("timestamp", true)]
    [InlineData("create", true)]
    [InlineData("change", true)]
    public async Task ExternalFailureIsPreservedWithoutFaultingCompletionEvenAfterStop(string point, bool stop)
    {
        Fixture f = new();
        IOException error = new("外部同步调用故障");
        Exception Fail()
        {
            if (stop) f.Budget.RequestStop();
            return error;
        }
        switch (point)
        {
            case "frequency": f.Clock.ReadFrequency = _ => throw Fail(); break;
            case "timestamp": f.Clock.ReadTimestamp = _ => throw Fail(); break;
            case "create": f.Clock.CreateHook = () => throw Fail(); break;
            default: f.Timer.ChangeHook = _ => throw Fail(); break;
        }
        try
        {
            _ = f.Budget.Start();
            await DrainAsync(f);
            AssertSnapshot(f.Budget.Snapshot, stop ? BudgetEnd.Stopped : BudgetEnd.Faulted,
                stop: stop, acquired: point == "change", released: point == "change", error: error);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("null", "control-frame-timer-missing")]
    [InlineData("false", "control-frame-timer-change-failed")]
    public async Task MissingTimerOrFalseChangeIsRejected(string mode, string message)
    {
        Fixture f = new();
        f.Clock.ReturnNull = mode == "null";
        f.Timer.ChangeResult = mode != "false";
        try
        {
            _ = f.Budget.Start();
            await DrainAsync(f);
            InvalidOperationException error = Assert.IsType<InvalidOperationException>(f.Budget.Snapshot.Error);
            Assert.Equal(message, error.Message);
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.Faulted, acquired: mode == "false", released: mode == "false", error: error);
            Assert.Equal(mode == "false" ? 1 : 0, f.Timer.DisposeCalls);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("create")]
    [InlineData("change")]
    public async Task SynchronousEarlyCallbackOnlyWakesAndRearmsOriginalBudget(string point)
    {
        Fixture f = new(budget: TimeSpan.FromTicks(1));
        if (point == "create") f.Clock.CreateHook = f.Timer.Fire;
        else f.Timer.ChangeHook = call => { if (call == 1) f.Timer.Fire(); };
        try
        {
            Task completion = f.Budget.Start();
            await f.Timer.ChangeAtAsync(2);
            await WaitForWakeAsync(f, completion);
            Assert.Equal(2, f.Timer.ChangeCalls);
            Assert.All(f.Timer.Changes, change => Assert.Equal(TimeSpan.FromMilliseconds(1), change.Due));
            f.Clock.Now = 1;
            f.Timer.Fire();
            await DrainAsync(f);
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.Expired, acquired: true, released: true, error: Expiry(f));
            Assert.All(f.Calls, call => Assert.False(call.InsideCallback));
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task SynchronousCallbackStormHitsProductionLimitAtExactly33Changes()
    {
        Fixture f = new();
        f.Timer.ChangeHook = _ => f.Timer.Fire();
        try
        {
            _ = f.Budget.Start();
            await DrainAsync(f);
            Assert.Equal(33, f.Timer.ChangeCalls);
            InvalidOperationException error = Assert.IsType<InvalidOperationException>(f.Budget.Snapshot.Error);
            Assert.Equal("control-frame-timer-immediate-wake-limit", error.Message);
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.Faulted, acquired: true, released: true, error: error);
            Assert.All(f.Timer.Changes, change => Assert.Equal(TimeSpan.FromSeconds(1), change.Due));
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task CallbackStormCoalescesWhileOnlyOriginalWorkerSamplesClock()
    {
        Fixture f = new();
        Pause pause = f.NewPause();
        try
        {
            Task completion = f.Budget.Start();
            await WaitForWakeAsync(f, completion);
            f.Clock.ReadTimestamp = call => { if (call == 4) pause.Wait(); return f.Clock.Now; };
            f.Timer.Fire();
            await pause.Reached.Task.WaitAsync(Guard);
            int calls = f.Calls.Length;
            Task fire = f.Keep(Task.Run(() => { for (int i = 0; i < 1000; i++) f.Timer.Fire(); }));
            await fire.WaitAsync(Guard);
            Assert.Equal(calls, f.Calls.Length);
            Assert.False(completion.IsCompleted);
            pause.Open();
            await f.Timer.ChangeAtAsync(3);
            await WaitForWakeAsync(f, completion);
            Assert.Equal(3, f.Timer.ChangeCalls);
            Assert.Equal(1, f.Clock.MaximumConcurrentTimestampCalls);
            Assert.All(f.Calls, call => Assert.Same(completion, call.Registered));
            Assert.All(f.Calls, call => Assert.False(call.InsideCallback));
            f.Budget.RequestFinish();
            await DrainAsync(f);
            Assert.Equal(BudgetEnd.FinishedInBudget, f.Budget.Snapshot.End);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("frequency-before", false)]
    [InlineData("timestamp", false)]
    [InlineData("frequency-after", false)]
    [InlineData("create", false)]
    [InlineData("change", false)]
    [InlineData("frequency-before", true)]
    [InlineData("timestamp", true)]
    [InlineData("change", true)]
    public async Task BlockedExternalPrefixDoesNotHoldGateOrTurnNotificationsIntoJoin(string point, bool runtime)
    {
        Fixture f = new();
        Pause pause = f.NewPause();
        Task? completion = null;
        try
        {
            if (runtime)
            {
                completion = f.Budget.Start();
                await WaitForWakeAsync(f, completion);
            }
            InstallPause();
            if (runtime) f.Timer.Fire();
            else
            {
                Task<Task> start = f.Keep(Task.Factory.StartNew(f.Budget.Start,
                    CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default));
                await pause.Reached.Task.WaitAsync(Guard);
                completion = await start.WaitAsync(Guard);
            }
            await pause.Reached.Task.WaitAsync(Guard);
            Assert.NotNull(completion);
            Task<Exception?> repeat = f.Keep(Task.Run<Exception?>(() => Record.Exception(() => { _ = f.Budget.Start(); })));
            Assert.IsType<InvalidOperationException>(await repeat.WaitAsync(Guard));
            Task stop = f.Keep(Task.Run(f.Budget.RequestStop));
            await stop.WaitAsync(Guard);
            Assert.True(f.Budget.Ready.IsCompletedSuccessfully);
            Assert.True(f.Budget.Decision.IsCompletedSuccessfully);
            Task<BudgetSnapshot> snapshot = f.Keep(Task.Run(() => f.Budget.Snapshot));
            BudgetSnapshot stopped = await snapshot.WaitAsync(Guard);
            Assert.Equal(BudgetEnd.Stopped, stopped.End);
            Assert.True(stopped.StopRequested);
            Assert.False(completion.IsCompleted);
            Assert.Equal(0, f.Timer.DisposeCalls);
            Assert.All(f.Calls, call => Assert.Same(completion, call.Registered));
            ExternalCall first = f.Calls[0];
            Assert.True(first.OnThreadPool);
            Assert.NotNull(first.TaskId);
            Assert.Same(TaskScheduler.Default, first.Scheduler);
            pause.Open();
            await DrainAsync(f);
            bool acquired = runtime || point is "create" or "change";
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.Stopped, stop: true, acquired: acquired, released: acquired);
            Assert.Equal(acquired ? 1 : 0, f.Timer.DisposeCalls);
            Assert.False(stopped.TimerReleased);
        }
        finally { await f.FinishAsync(); }

        void InstallPause()
        {
            if (point.StartsWith("frequency", StringComparison.Ordinal))
            {
                int target = f.Clock.FrequencyReads + (point == "frequency-after" ? 2 : 1);
                f.Clock.ReadFrequency = call => { if (call == target) pause.Wait(); return Frequency; };
            }
            else if (point == "timestamp") f.Clock.ReadTimestamp = _ => { pause.Wait(); return f.Clock.Now; };
            else if (point == "create") f.Clock.CreateHook = pause.Wait;
            else f.Timer.ChangeHook = _ => pause.Wait();
        }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("frequency")]
    [InlineData("timestamp")]
    [InlineData("create")]
    [InlineData("change")]
    [InlineData("dispose")]
    public async Task ExternalSynchronousReentryCanStopWithoutWaitingForItsOwnWorker(string point)
    {
        Fixture f = new();
        bool reentered = false;
        void Stop()
        {
            Assert.NotNull(f.Budget.Completion);
            f.Budget.RequestStop();
            Assert.True(f.Budget.Decision.IsCompletedSuccessfully);
            Assert.False(f.Budget.Completion!.IsCompleted);
            reentered = true;
        }
        switch (point)
        {
            case "frequency": f.Clock.ReadFrequency = _ => { Stop(); return Frequency; }; break;
            case "timestamp": f.Clock.ReadTimestamp = _ => { Stop(); return f.Clock.Now; }; break;
            case "create": f.Clock.CreateHook = Stop; break;
            case "change": f.Timer.ChangeHook = _ => Stop(); break;
            default: f.Timer.DisposeHook = () => { Stop(); return ValueTask.CompletedTask; }; break;
        }
        try
        {
            _ = f.Budget.Start();
            if (point == "dispose")
            {
                await f.Budget.Ready.WaitAsync(Guard);
                f.Budget.RequestFinish();
            }
            await DrainAsync(f);
            Assert.True(reentered);
            bool acquired = point is "create" or "change" or "dispose";
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.Stopped, stop: true, finish: point == "dispose", acquired: acquired, released: acquired);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task FinishDuringInitialClockSampleIsOnlyADeclarationAndSkipsTimerCreation()
    {
        Fixture f = new();
        Pause pause = f.NewPause();
        f.Clock.ReadTimestamp = call => { if (call == 1) pause.Wait(); return f.Clock.Now; };
        try
        {
            Task<Task> start = f.Keep(Task.Factory.StartNew(f.Budget.Start,
                CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default));
            await pause.Reached.Task.WaitAsync(Guard);
            Task completion = await start.WaitAsync(Guard);
            f.Budget.RequestFinish();
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.Pending, finish: true);
            Assert.False(f.Budget.Ready.IsCompleted);
            Assert.False(f.Budget.Decision.IsCompleted);
            Assert.False(completion.IsCompleted);
            pause.Open();
            await DrainAsync(f);
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.FinishedInBudget, finish: true);
            Assert.Equal(2, f.Clock.TimestampReads);
            Assert.Equal(0, f.Clock.CreateCalls);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task FinishWhileCreateIsBlockedStillOwnsLateTimerAndWaitsForItsOriginalDispose()
    {
        Fixture f = new();
        Pause create = f.NewPause();
        TaskCompletionSource original = f.NewOriginal();
        f.Clock.CreateHook = create.Wait;
        f.Timer.DisposeHook = () => new ValueTask(original.Task);
        try
        {
            Task<Task> start = f.Keep(Task.Factory.StartNew(f.Budget.Start,
                CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default));
            await create.Reached.Task.WaitAsync(Guard);
            Task completion = await start.WaitAsync(Guard);
            f.Budget.RequestFinish();
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.Pending, finish: true);
            Assert.False(completion.IsCompleted);
            create.Open();
            await AssertAwaitAsync(original.Task, completion, f.Budget);
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.Pending, finish: true, acquired: true);
            Assert.Equal(0, f.Timer.ChangeCalls);
            Assert.False(f.Budget.Decision.IsCompleted);
            original.SetResult();
            await DrainAsync(f);
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.FinishedInBudget, finish: true, acquired: true, released: true);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ExpiryDecisionDoesNotJoinPendingOriginalDispose()
    {
        Fixture f = new(budget: TimeSpan.FromTicks(1));
        TaskCompletionSource original = f.NewOriginal();
        f.Timer.DisposeHook = () => new ValueTask(original.Task);
        try
        {
            Task completion = f.Budget.Start();
            await WaitForWakeAsync(f, completion);
            f.Clock.Now = 1;
            f.Timer.Fire();
            await AssertAwaitAsync(original.Task, completion, f.Budget);
            TimeoutException error = Expiry(f);
            BudgetSnapshot expired = f.Budget.Snapshot;
            AssertSnapshot(expired, BudgetEnd.Expired, acquired: true, error: error);
            Assert.True(f.Budget.Ready.IsCompletedSuccessfully);
            Assert.True(f.Budget.Decision.IsCompletedSuccessfully);
            Assert.False(completion.IsCompleted);
            f.Budget.RequestFinish();
            Assert.False(f.Budget.Snapshot.FinishRequested);
            original.SetResult();
            await DrainAsync(f);
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.Expired, acquired: true, released: true, error: error);
            AssertSnapshot(expired, BudgetEnd.Expired, acquired: true, error: error);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task PendingOriginalDisposePreventsCompletion()
    {
        Fixture f = new();
        TaskCompletionSource original = f.NewOriginal();
        f.Timer.DisposeHook = () => new ValueTask(original.Task);
        try
        {
            Task completion = f.Budget.Start();
            await f.Budget.Ready.WaitAsync(Guard);
            Assert.False(f.Budget.Decision.IsCompleted);
            f.Budget.RequestFinish();
            await AssertAwaitAsync(original.Task, completion, f.Budget);
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.Pending, finish: true, acquired: true);
            Assert.True(f.Budget.Ready.IsCompletedSuccessfully);
            Assert.False(f.Budget.Decision.IsCompleted);
            Assert.False(original.Task.IsCompleted);
            Assert.False(completion.IsCompleted);
            f.Budget.RequestStop();
            Assert.True(f.Budget.Decision.IsCompletedSuccessfully);
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.Stopped, stop: true, finish: true, acquired: true);
            await AssertAwaitAsync(original.Task, completion, f.Budget);
            Assert.False(completion.IsCompleted);
            original.SetResult();
            await DrainAsync(f);
            Assert.True(original.Task.IsCompletedSuccessfully);
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.Stopped, stop: true, finish: true, acquired: true, released: true);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task TimerReleaseCrossingDeadlineRejectsFinish()
    {
        Fixture f = new(anchor: 50, budget: TimeSpan.FromTicks(10));
        TaskCompletionSource original = f.NewOriginal();
        f.Timer.DisposeHook = () => new ValueTask(original.Task);
        try
        {
            Task completion = f.Budget.Start();
            await f.Budget.Ready.WaitAsync(Guard);
            f.Clock.Now = 59;
            f.Budget.RequestFinish();
            await AssertAwaitAsync(original.Task, completion, f.Budget);
            BudgetSnapshot pending = f.Budget.Snapshot;
            AssertSnapshot(pending, BudgetEnd.Pending, finish: true, acquired: true);
            f.Clock.Now = 60;
            original.SetResult();
            await DrainAsync(f);
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.Expired, finish: true, acquired: true, released: true, error: Expiry(f));
            AssertSnapshot(pending, BudgetEnd.Pending, finish: true, acquired: true);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task SuccessfulFinishSamplesOnlyAfterOriginalDisposeCompletesAndIgnoresOldCallbacks()
    {
        Fixture f = new();
        TaskCompletionSource original = f.NewOriginal();
        f.Timer.DisposeHook = () => new ValueTask(original.Task);
        try
        {
            Task completion = f.Budget.Start();
            await f.Budget.Ready.WaitAsync(Guard);
            BudgetSnapshot armed = f.Budget.Snapshot;
            f.Budget.RequestFinish();
            await AssertAwaitAsync(original.Task, completion, f.Budget);
            int reads = f.Clock.TimestampReads, changes = f.Timer.ChangeCalls;
            f.Clock.ReadTimestamp = _ =>
            {
                Assert.True(original.Task.IsCompletedSuccessfully, "末检不能仅等待释放通知。");
                return f.Clock.Now;
            };
            for (int i = 0; i < 100; i++) f.Timer.Fire();
            Assert.Equal(reads, f.Clock.TimestampReads);
            Assert.Equal(changes, f.Timer.ChangeCalls);
            original.SetResult();
            await DrainAsync(f);
            Assert.Equal(reads + 1, f.Clock.TimestampReads);
            BudgetSnapshot finished = f.Budget.Snapshot;
            AssertSnapshot(finished, BudgetEnd.FinishedInBudget, finish: true, acquired: true, released: true);
            int calls = f.Calls.Length;
            f.Clock.Now = long.MaxValue;
            for (int i = 0; i < 100; i++) f.Timer.Fire();
            f.Budget.RequestFinish();
            Assert.Equal(calls, f.Calls.Length);
            Assert.Equal(finished, f.Budget.Snapshot);
            f.Budget.RequestStop();
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.FinishedInBudget, stop: true, finish: true, acquired: true, released: true);
            AssertSnapshot(armed, BudgetEnd.Pending, acquired: true);
            AssertSnapshot(finished, BudgetEnd.FinishedInBudget, finish: true, acquired: true, released: true);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task BlockedDisposePrefixIsNotJoinAndReturnedLateTaskStillMustBeAwaited()
    {
        Fixture f = new();
        Pause prefix = f.NewPause();
        TaskCompletionSource original = f.NewOriginal();
        f.Timer.DisposeHook = () => { prefix.Wait(); return new ValueTask(original.Task); };
        try
        {
            Task completion = f.Budget.Start();
            await f.Budget.Ready.WaitAsync(Guard);
            f.Budget.RequestFinish();
            await prefix.Reached.Task.WaitAsync(Guard);
            Task stop = f.Keep(Task.Run(f.Budget.RequestStop));
            await stop.WaitAsync(Guard);
            Assert.True(f.Budget.Ready.IsCompletedSuccessfully);
            Assert.True(f.Budget.Decision.IsCompletedSuccessfully);
            Assert.False(completion.IsCompleted);
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.Stopped, stop: true, finish: true, acquired: true);
            prefix.Open();
            await AssertAwaitAsync(original.Task, completion, f.Budget);
            Assert.False(original.Task.IsCompleted);
            original.SetResult();
            await DrainAsync(f);
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.Stopped, stop: true, finish: true, acquired: true, released: true);
        }
        finally { await f.FinishAsync(); }
    }

    public static IEnumerable<object[]> DisposeFaults()
    {
        foreach (string shape in new[] { "single", "nested", "siblings", "repeated", "disposed" })
            foreach (bool stop in new[] { false, true }) yield return [shape, stop];
    }

    [Theory(Timeout = TestTimeout)]
    [MemberData(nameof(DisposeFaults))]
    public async Task OriginalDisposeFaultsKeepRootReferencesNestedTreesAndDuplicates(string shape, bool stop)
    {
        Fixture f = new();
        TaskCompletionSource original = f.NewOriginal();
        f.Timer.DisposeHook = () => new ValueTask(original.Task);
        IOException leaf = new("首项清理故障");
        OperationCanceledException cancellation = new("嵌套取消不等于原任务取消");
        InvalidOperationException inner = new("嵌套故障");
        AggregateException branch = new("保留嵌套", cancellation, inner);
        IOException sibling = new("同级故障");
        ObjectDisposedException disposed = new("timer");
        Exception[] roots = shape switch
        {
            "single" => [leaf],
            "nested" => [branch],
            "repeated" => [leaf, branch, leaf],
            "disposed" => [disposed],
            _ => [leaf, branch, sibling]
        };
        try
        {
            Task completion = f.Budget.Start();
            await f.Budget.Ready.WaitAsync(Guard);
            f.Budget.RequestFinish();
            await AssertAwaitAsync(original.Task, completion, f.Budget);
            BudgetSnapshot pending = f.Budget.Snapshot;
            if (stop) f.Budget.RequestStop();
            original.SetException(roots);
            await DrainAsync(f);
            Assert.True(original.Task.IsFaulted);
            Exception saved = Assert.IsAssignableFrom<Exception>(f.Budget.Snapshot.CleanupError);
            if (roots.Length == 1) Assert.Same(roots[0], saved);
            else
            {
                AggregateException tree = Assert.IsType<AggregateException>(saved);
                Assert.Equal(roots.Length, tree.InnerExceptions.Count);
                for (int i = 0; i < roots.Length; i++)
                {
                    Assert.Same(roots[i], tree.InnerExceptions[i]);
                    Assert.Same(roots[i], original.Task.Exception!.InnerExceptions[i]);
                }
            }
            Assert.Same(cancellation, branch.InnerExceptions[0]);
            Assert.Same(inner, branch.InnerExceptions[1]);
            AssertSnapshot(f.Budget.Snapshot, stop ? BudgetEnd.Stopped : BudgetEnd.Faulted,
                stop: stop, finish: true, acquired: true, cleanup: saved);
            AssertSnapshot(pending, BudgetEnd.Pending, finish: true, acquired: true);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledOriginalDisposeKeepsOriginalTaskAndCancellationToken(bool stop)
    {
        Fixture f = new();
        TaskCompletionSource original = f.NewOriginal();
        f.Timer.DisposeHook = () => new ValueTask(original.Task);
        using CancellationTokenSource cancellation = new();
        try
        {
            Task completion = f.Budget.Start();
            await f.Budget.Ready.WaitAsync(Guard);
            f.Budget.RequestFinish();
            await AssertAwaitAsync(original.Task, completion, f.Budget);
            if (stop) f.Budget.RequestStop();
            cancellation.Cancel();
            original.SetCanceled(cancellation.Token);
            await DrainAsync(f);
            Assert.True(original.Task.IsCanceled);
            TaskCanceledException error = Assert.IsType<TaskCanceledException>(f.Budget.Snapshot.CleanupError);
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Same(original.Task, error.Task);
            AssertSnapshot(f.Budget.Snapshot, stop ? BudgetEnd.Stopped : BudgetEnd.Faulted,
                stop: stop, finish: true, acquired: true, cleanup: error);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("dispose", false)]
    [InlineData("status", false)]
    [InlineData("registration", false)]
    [InlineData("dispose", true)]
    [InlineData("status", true)]
    [InlineData("registration", true)]
    public async Task DisposeOrAsTaskPrefixFailureMarksUnknownCleanupWithoutInventingOriginalTask(string point, bool stop)
    {
        Fixture f = new();
        Pause pause = f.NewPause();
        AggregateException error = new("同步前缀失败", new IOException("清理故障"),
            new AggregateException(new InvalidOperationException("嵌套故障")));
        ThrowingSource source = new(f, error, point == "registration", pause.Wait);
        f.Timer.DisposeHook = () =>
        {
            if (point != "dispose") return new ValueTask(source, 0);
            pause.Wait();
            throw error;
        };
        try
        {
            Task completion = f.Budget.Start();
            await f.Budget.Ready.WaitAsync(Guard);
            f.Budget.RequestFinish();
            await pause.Reached.Task.WaitAsync(Guard);
            Assert.False(completion.IsCompleted);
            if (stop)
            {
                Task request = f.Keep(Task.Run(f.Budget.RequestStop));
                await request.WaitAsync(Guard);
                Assert.True(f.Budget.Decision.IsCompletedSuccessfully);
            }
            else Assert.False(f.Budget.Decision.IsCompleted);
            Assert.False(f.Budget.Snapshot.CleanupExitUnproven);
            pause.Open();
            await DrainAsync(f);
            AssertSnapshot(f.Budget.Snapshot, stop ? BudgetEnd.Stopped : BudgetEnd.Faulted,
                stop: stop, finish: true, acquired: true, unknown: true, cleanup: error);
            Assert.Equal(point == "dispose" ? 0 : 1, source.StatusCalls);
            Assert.Equal(point == "registration" ? 1 : 0, source.RegistrationCalls);
            Assert.Equal(0, source.ResultCalls);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InFlightPrimaryAndCleanupErrorsRemainSeparateEvenWhenStopWins(bool stop)
    {
        Fixture f = new();
        IOException primary = new("Change 主故障");
        AggregateException cleanup = new("清理树", new IOException("清理故障"));
        TaskCompletionSource original = f.NewOriginal();
        f.Timer.ChangeHook = _ => { if (stop) f.Budget.RequestStop(); throw primary; };
        f.Timer.DisposeHook = () => new ValueTask(original.Task);
        try
        {
            Task completion = f.Budget.Start();
            await AssertAwaitAsync(original.Task, completion, f.Budget);
            Assert.True(f.Budget.Ready.IsCompletedSuccessfully);
            Assert.True(f.Budget.Decision.IsCompletedSuccessfully);
            AssertSnapshot(f.Budget.Snapshot, stop ? BudgetEnd.Stopped : BudgetEnd.Faulted,
                stop: stop, acquired: true, error: primary);
            original.SetException(cleanup);
            await DrainAsync(f);
            AssertSnapshot(f.Budget.Snapshot, stop ? BudgetEnd.Stopped : BudgetEnd.Faulted,
                stop: stop, acquired: true, error: primary, cleanup: cleanup);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("frequency")]
    [InlineData("regression")]
    [InlineData("throw")]
    public async Task FinalSampleAfterReleaseCanStillRejectFinish(string failure)
    {
        Fixture f = new(anchor: 10);
        f.Clock.Now = 20;
        TaskCompletionSource original = f.NewOriginal();
        f.Timer.DisposeHook = () => new ValueTask(original.Task);
        IOException expected = new("末检故障");
        try
        {
            Task completion = f.Budget.Start();
            await f.Budget.Ready.WaitAsync(Guard);
            f.Budget.RequestFinish();
            await AssertAwaitAsync(original.Task, completion, f.Budget);
            if (failure == "frequency") f.Clock.Frequency = Frequency + 1;
            else if (failure == "regression") f.Clock.Now = 19;
            else f.Clock.ReadTimestamp = _ => throw expected;
            original.SetResult();
            await DrainAsync(f);
            Exception error = Assert.IsAssignableFrom<Exception>(f.Budget.Snapshot.Error);
            if (failure == "throw") Assert.Same(expected, error);
            else Assert.IsType<InvalidOperationException>(error);
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.Faulted, finish: true, acquired: true, released: true, error: error);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task StopDuringFinalSampleWinsTheSameCommitGate()
    {
        Fixture f = new();
        TaskCompletionSource original = f.NewOriginal();
        Pause finalSample = f.NewPause();
        f.Timer.DisposeHook = () => new ValueTask(original.Task);
        try
        {
            Task completion = f.Budget.Start();
            await f.Budget.Ready.WaitAsync(Guard);
            f.Budget.RequestFinish();
            await AssertAwaitAsync(original.Task, completion, f.Budget);
            f.Clock.ReadTimestamp = _ => { finalSample.Wait(); return f.Clock.Now; };
            original.SetResult();
            await finalSample.Reached.Task.WaitAsync(Guard);
            Task stop = f.Keep(Task.Run(f.Budget.RequestStop));
            await stop.WaitAsync(Guard);
            Assert.True(f.Budget.Decision.IsCompletedSuccessfully);
            Assert.False(completion.IsCompleted);
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.Stopped, stop: true, finish: true, acquired: true, released: true);
            finalSample.Open();
            await DrainAsync(f);
            AssertSnapshot(f.Budget.Snapshot, BudgetEnd.Stopped, stop: true, finish: true, acquired: true, released: true);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ConcurrentStartPublishesExactlyOneOriginalWorker()
    {
        Fixture f = new();
        TaskCompletionSource release = f.NewSignal();
        TaskCompletionSource leftReady = f.NewSignal(), rightReady = f.NewSignal();
        Task<(Task? Worker, Exception? Error)> left = f.Keep(Attempt(leftReady));
        Task<(Task? Worker, Exception? Error)> right = f.Keep(Attempt(rightReady));
        try
        {
            await Task.WhenAll(leftReady.Task, rightReady.Task).WaitAsync(Guard);
            release.SetResult();
            var attempts = await Task.WhenAll(left, right).WaitAsync(Guard);
            var winner = Assert.Single(attempts, attempt => attempt.Error is null);
            var loser = Assert.Single(attempts, attempt => attempt.Error is not null);
            Assert.IsType<InvalidOperationException>(loser.Error);
            Assert.Null(loser.Worker);
            Assert.Same(winner.Worker, f.Budget.Completion);
            await f.Budget.Ready.WaitAsync(Guard);
            f.Budget.RequestFinish();
            await DrainAsync(f);
            Assert.Equal(1, f.Clock.CreateCalls);
            Assert.Equal(1, f.Timer.DisposeCalls);
            Assert.All(f.Calls, call => Assert.Same(winner.Worker, call.Registered));
        }
        finally { await f.FinishAsync(); }

        Task<(Task? Worker, Exception? Error)> Attempt(TaskCompletionSource ready) => Task.Run<(Task? Worker, Exception? Error)>(async () =>
        {
            ready.SetResult();
            await release.Task.ConfigureAwait(false);
            Task? worker = null;
            Exception? error = Record.Exception(() => { worker = f.Budget.Start(); });
            return (worker, error);
        });
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ConcurrentStopAndFinishPermitOnlyStoppedOrPreviouslyCommittedSuccess()
    {
        Fixture f = new();
        TaskCompletionSource release = f.NewSignal();
        TaskCompletionSource stopReady = f.NewSignal(), finishReady = f.NewSignal();
        try
        {
            _ = f.Budget.Start();
            await f.Budget.Ready.WaitAsync(Guard);
            Task stop = f.Keep(Request(stopReady, f.Budget.RequestStop));
            Task finish = f.Keep(Request(finishReady, f.Budget.RequestFinish));
            await Task.WhenAll(stopReady.Task, finishReady.Task).WaitAsync(Guard);
            release.SetResult();
            await Task.WhenAll(stop, finish).WaitAsync(Guard);
            await DrainAsync(f);
            BudgetSnapshot snapshot = f.Budget.Snapshot;
            Assert.Contains(snapshot.End, new[] { BudgetEnd.Stopped, BudgetEnd.FinishedInBudget });
            Assert.True(snapshot.StopRequested);
            if (snapshot.End == BudgetEnd.FinishedInBudget) Assert.True(snapshot.FinishRequested);
            Assert.True(snapshot.TimerAcquired);
            Assert.True(snapshot.TimerReleased);
            Assert.Null(snapshot.Error);
            Assert.Null(snapshot.CleanupError);
            Assert.False(snapshot.CleanupExitUnproven);
        }
        finally { await f.FinishAsync(); }

        Task Request(TaskCompletionSource ready, Action request) => Task.Run(async () =>
        {
            ready.SetResult();
            await release.Task.ConfigureAwait(false);
            request();
        });
    }

    private static TimeoutException Expiry(Fixture f)
    {
        TimeoutException error = Assert.IsType<TimeoutException>(f.Budget.Snapshot.Error);
        Assert.Equal("control-frame-budget-expired", error.Message);
        return error;
    }

    private static void AssertSnapshot(BudgetSnapshot snapshot, BudgetEnd end, bool stop = false,
        bool finish = false, bool acquired = false, bool released = false, bool unknown = false,
        Exception? error = null, Exception? cleanup = null)
    {
        Assert.Equal(end, snapshot.End);
        Assert.Equal(stop, snapshot.StopRequested);
        Assert.Equal(finish, snapshot.FinishRequested);
        Assert.Equal(acquired, snapshot.TimerAcquired);
        Assert.Equal(released, snapshot.TimerReleased);
        Assert.Equal(unknown, snapshot.CleanupExitUnproven);
        Assert.Same(error, snapshot.Error);
        Assert.Same(cleanup, snapshot.CleanupError);
    }

    private static async Task DrainAsync(Fixture f)
    {
        Task completion = Assert.IsAssignableFrom<Task>(f.Budget.Completion);
        await completion.WaitAsync(Guard);
        Assert.Equal(TaskStatus.RanToCompletion, completion.Status);
        Assert.Null(completion.Exception);
        Assert.Same(completion, f.Budget.Completion);
        Assert.True(f.Budget.Ready.IsCompletedSuccessfully);
        Assert.True(f.Budget.Decision.IsCompletedSuccessfully);
        Assert.Equal(0, f.Timer.SyncDisposeCalls);
        Assert.All(f.Calls, call => Assert.False(call.InsideCallback));
        Assert.DoesNotContain(f.Calls, call => call.Operation == "wall-clock");
        if (f.Clock.CreateCalls != 0)
        {
            Assert.Equal(1, f.Clock.CreateCalls);
            Assert.Equal(Timeout.InfiniteTimeSpan, f.Clock.CreatedDue);
            Assert.Equal(Timeout.InfiniteTimeSpan, f.Clock.CreatedPeriod);
        }
        Assert.All(f.Timer.Changes, change => Assert.Equal(Timeout.InfiniteTimeSpan, change.Period));
        Assert.Equal(f.Budget.Snapshot.TimerAcquired ? 1 : 0, f.Timer.DisposeCalls);
    }

    private static async Task WaitForWakeAsync(Fixture f, Task completion)
    {
        await f.Budget.Ready.WaitAsync(Guard);
        TaskCompletionSource wake = (TaskCompletionSource)typeof(ControlFrameBudget).GetField("_wake", Fields)!.GetValue(f.Budget)!;
        await AssertAwaitAsync(wake.Task, completion, f.Budget);
    }

    // 只认可本实例 RunAsync 的精确 awaiter，以及它到 Start 返回 Task 的实际 unwrap 边。
    private static async Task AssertAwaitAsync(Task original, Task completion, ControlFrameBudget owner)
    {
        using CancellationTokenSource guard = new(Guard);
        while (!HasAwait(original, completion, owner))
        {
            Assert.False(original.IsCompleted, "原任务提前完成，无法证明受闸控制的等待。");
            Assert.False(completion.IsCompleted, "Completion 提前退出，没有等待原任务。");
            if (guard.IsCancellationRequested)
                throw new XunitException("Guard：未观察到原任务的真实 await 链；运行时布局不兼容不能跳过证明。");
            await Task.Yield();
        }
    }

    private static bool HasAwait(Task original, Task completion, ControlFrameBudget owner)
    {
        Type machineType = typeof(ControlFrameBudget).GetMethod("RunAsync", Fields)?
            .GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
            ?? throw new XunitException("缺少 RunAsync 的异步状态机元数据。");
        foreach (object continuation in Continuations(original))
        {
            object? box = continuation is Delegate action ? action.Target : continuation;
            if (box is not Task core) continue;
            object? machine = RuntimeField(box.GetType(), "StateMachine")?.GetValue(box);
            if (machine is null || machine.GetType() != machineType) continue;
            if (!ReferenceEquals(RuntimeField(machineType, "<>4__this")?.GetValue(machine), owner)) continue;
            bool exact = machineType.GetFields(Fields)
                .Where(field => field.Name.StartsWith("<>u__", StringComparison.Ordinal))
                .Select(field => field.GetValue(machine)).OfType<object>()
                .Any(awaiter => awaiter.GetType().GetFields(Fields).Any(field =>
                    typeof(Task).IsAssignableFrom(field.FieldType) && ReferenceEquals(field.GetValue(awaiter), original)));
            if (exact && (ReferenceEquals(core, completion) ||
                Continuations(core).Any(value => ReferenceEquals(value, completion)))) return true;
        }
        return false;
    }

    private static object[] Continuations(Task task)
    {
        FieldInfo field = RuntimeField(typeof(Task), "m_continuationObject")
            ?? throw new XunitException("运行时缺少 Task continuation 字段。");
        object? continuation = field.GetValue(task);
        if (continuation is IList list)
        {
            lock (list) return list.Cast<object?>().OfType<object>().ToArray();
        }
        return continuation is null ? [] : [continuation];
    }

    private static FieldInfo? RuntimeField(Type type, string name)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
            if (current.GetField(name, Fields | BindingFlags.DeclaredOnly) is { } field) return field;
        return null;
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Pause : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        internal TaskCompletionSource Reached { get; } = Signal();
        internal void Wait()
        {
            Reached.TrySetResult();
            if (!_release.Wait(Guard)) throw new XunitException("Guard：同步前缀闸门没有放行。");
        }
        internal void Open() => _release.Set();
        public void Dispose() => _release.Dispose();
    }

    private sealed record ExternalCall(string Operation, Task? Registered, int? TaskId,
        bool OnThreadPool, TaskScheduler Scheduler, bool InsideCallback);
    private readonly record struct ChangeRecord(TimeSpan Due, TimeSpan Period);

    private sealed class Fixture
    {
        private readonly ConcurrentQueue<ExternalCall> _calls = new();
        private readonly List<Pause> _pauses = [];
        private readonly List<TaskCompletionSource> _signals = [];
        private readonly List<TaskCompletionSource> _originals = [];
        private readonly List<Task> _tasks = [];
        internal ControlFrameBudget Budget { get; }
        internal Clock Clock { get; }
        internal Timer Timer { get; }
        internal ExternalCall[] Calls => _calls.ToArray();

        internal Fixture(long anchor = 0, long frequency = Frequency, TimeSpan? budget = null, long? initialNow = null)
        {
            Timer = new Timer(this);
            Clock = new Clock(this) { Now = initialNow ?? anchor, Frequency = frequency };
            Budget = new ControlFrameBudget(Clock, anchor, frequency, budget ?? TimeSpan.FromSeconds(1));
        }

        internal void Record(string operation) => _calls.Enqueue(new ExternalCall(operation, Budget?.Completion,
            Task.CurrentId, Thread.CurrentThread.IsThreadPoolThread, TaskScheduler.Current, _insideCallback));
        internal Pause NewPause()
        {
            Pause pause = new();
            _pauses.Add(pause);
            return pause;
        }
        internal TaskCompletionSource NewSignal()
        {
            TaskCompletionSource signal = Signal();
            _signals.Add(signal);
            return signal;
        }
        internal TaskCompletionSource NewOriginal()
        {
            TaskCompletionSource original = Signal();
            _originals.Add(original);
            return original;
        }
        internal T Keep<T>(T task) where T : Task
        {
            _tasks.Add(task);
            return task;
        }
        internal async Task FinishAsync()
        {
            // 失败时也先放所有闸，再停止，再放原释放；必须 join 原 Task 而非通知。
            foreach (Pause pause in _pauses) pause.Open();
            foreach (TaskCompletionSource signal in _signals) signal.TrySetResult();
            Budget.RequestStop();
            foreach (TaskCompletionSource original in _originals) original.TrySetResult();
            try
            {
                try { await Task.WhenAll(_tasks).WaitAsync(Guard); }
                finally
                {
                    if (Budget.Completion is { } completion) await completion.WaitAsync(Guard);
                    foreach (TaskCompletionSource original in _originals)
                    {
                        Assert.True(original.Task.IsCompleted);
                        try { await original.Task; }
                        catch { _ = original.Task.Exception; }
                    }
                }
            }
            finally
            {
                foreach (Pause pause in _pauses) pause.Dispose();
            }
        }
    }

    private sealed class Clock(Fixture fixture) : TimeProvider
    {
        private long _now, _frequency;
        private int _frequencyReads, _timestampReads, _createCalls, _activeTimestampCalls, _maximumConcurrentTimestampCalls;
        internal long Now { get => Volatile.Read(ref _now); set => Volatile.Write(ref _now, value); }
        internal long Frequency { get => Volatile.Read(ref _frequency); set => Volatile.Write(ref _frequency, value); }
        internal int FrequencyReads => Volatile.Read(ref _frequencyReads);
        internal int TimestampReads => Volatile.Read(ref _timestampReads);
        internal int CreateCalls => Volatile.Read(ref _createCalls);
        internal int MaximumConcurrentTimestampCalls => Volatile.Read(ref _maximumConcurrentTimestampCalls);
        internal Func<int, long>? ReadFrequency { get; set; }
        internal Func<int, long>? ReadTimestamp { get; set; }
        internal Action? CreateHook { get; set; }
        internal bool ReturnNull { get; set; }
        internal TimeSpan CreatedDue { get; private set; }
        internal TimeSpan CreatedPeriod { get; private set; }

        public override long TimestampFrequency
        {
            get
            {
                fixture.Record("frequency");
                int call = Interlocked.Increment(ref _frequencyReads);
                return ReadFrequency?.Invoke(call) ?? Frequency;
            }
        }
        public override long GetTimestamp()
        {
            fixture.Record("timestamp");
            int call = Interlocked.Increment(ref _timestampReads);
            int active = Interlocked.Increment(ref _activeTimestampCalls);
            int maximum;
            do { maximum = Volatile.Read(ref _maximumConcurrentTimestampCalls); }
            while (active > maximum && Interlocked.CompareExchange(ref _maximumConcurrentTimestampCalls, active, maximum) != maximum);
            try { return ReadTimestamp?.Invoke(call) ?? Now; }
            finally { Interlocked.Decrement(ref _activeTimestampCalls); }
        }
        public override DateTimeOffset GetUtcNow()
        {
            fixture.Record("wall-clock");
            throw new XunitException("预算不能读取墙钟。");
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            fixture.Record("create");
            Interlocked.Increment(ref _createCalls);
            CreatedDue = dueTime;
            CreatedPeriod = period;
            fixture.Timer.Bind(callback, state);
            CreateHook?.Invoke();
            return ReturnNull ? null! : fixture.Timer;
        }
    }

    private sealed class Timer(Fixture fixture) : ITimer
    {
        private readonly ConcurrentQueue<ChangeRecord> _changes = new();
        private readonly TaskCompletionSource[] _changed = Enumerable.Range(0, 64).Select(_ => Signal()).ToArray();
        private TimerCallback? _callback;
        private object? _state;
        private int _changeCalls, _disposeCalls, _syncDisposeCalls;
        internal Action<int>? ChangeHook { get; set; }
        internal Func<ValueTask>? DisposeHook { get; set; }
        internal bool ChangeResult { get; set; } = true;
        internal int ChangeCalls => Volatile.Read(ref _changeCalls);
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        internal int SyncDisposeCalls => Volatile.Read(ref _syncDisposeCalls);
        internal ChangeRecord[] Changes => _changes.ToArray();
        internal void Bind(TimerCallback callback, object? state) { _callback = callback; _state = state; }
        internal void Fire()
        {
            TimerCallback callback = Assert.IsType<TimerCallback>(_callback);
            bool previous = _insideCallback;
            _insideCallback = true;
            try { callback(_state); }
            finally { _insideCallback = previous; }
        }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            fixture.Record("change");
            int call = Interlocked.Increment(ref _changeCalls);
            if (call > _changed.Length) throw new XunitException("Guard：生产连续提前唤醒上限失效，已达到夹具记录容量。");
            _changes.Enqueue(new ChangeRecord(dueTime, period));
            try { ChangeHook?.Invoke(call); return ChangeResult; }
            finally { _changed[call - 1].TrySetResult(); }
        }
        internal Task ChangeAtAsync(int call) => _changed[call - 1].Task.WaitAsync(Guard);
        public ValueTask DisposeAsync()
        {
            fixture.Record("dispose-async");
            Interlocked.Increment(ref _disposeCalls);
            return DisposeHook?.Invoke() ?? ValueTask.CompletedTask;
        }
        public void Dispose()
        {
            fixture.Record("dispose-sync");
            Interlocked.Increment(ref _syncDisposeCalls);
            throw new XunitException("必须等待原 DisposeAsync，不能降级为 Dispose。");
        }
    }

    private sealed class ThrowingSource(Fixture fixture, Exception error, bool onRegistration, Action beforeFailure) : IValueTaskSource
    {
        private int _statusCalls, _registrationCalls, _resultCalls;
        internal int StatusCalls => Volatile.Read(ref _statusCalls);
        internal int RegistrationCalls => Volatile.Read(ref _registrationCalls);
        internal int ResultCalls => Volatile.Read(ref _resultCalls);
        public ValueTaskSourceStatus GetStatus(short token)
        {
            fixture.Record("dispose-status");
            Interlocked.Increment(ref _statusCalls);
            if (onRegistration) return ValueTaskSourceStatus.Pending;
            beforeFailure();
            throw error;
        }
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            fixture.Record("dispose-registration");
            Interlocked.Increment(ref _registrationCalls);
            beforeFailure();
            throw error;
        }
        public void GetResult(short token)
        {
            fixture.Record("dispose-result");
            Interlocked.Increment(ref _resultCalls);
            throw new XunitException("没有取得原释放 Task 的源不应调用 GetResult。");
        }
    }
}
