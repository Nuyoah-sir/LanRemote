using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit.Sdk;
using AttemptEnd = LanRemote.Transport.ControlFrameReadAttempt.AttemptEnd;
using AttemptSnapshot = LanRemote.Transport.ControlFrameReadAttempt.AttemptSnapshot;
using FrameOutcome = LanRemote.Transport.ControlFrameReadAttempt.FrameOutcome;
using BudgetEnd = LanRemote.Transport.ControlFrameBudget.BudgetEnd;

namespace LanRemote.Transport.Tests;

public sealed class ControlFrameObservedEntryTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private const int TestTimeout = 60_000;
    private const long Frequency = TimeSpan.TicksPerSecond;
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact(Timeout = TestTimeout)]
    public async Task ParametersAreValidatedWithoutReadingStreamOrClock()
    {
        Fixture f = new();
        try
        {
            Assert.Equal("stream", Assert.Throws<ArgumentNullException>(() =>
                ControlFrameReadAttempt.FromObservedFirstByte(null!, 0, f.Clock, 0, Frequency, TimeSpan.Zero, 64)).ParamName);
            Assert.Equal("clock", Assert.Throws<ArgumentNullException>(() =>
                ControlFrameReadAttempt.FromObservedFirstByte(f.Stream, 0, null!, 0, Frequency, TimeSpan.Zero, 64)).ParamName);
            foreach (long frequency in new[] { long.MinValue, -1L, 0L })
                Assert.Equal("observedFrequency", Assert.Throws<ArgumentOutOfRangeException>(() =>
                    ControlFrameReadAttempt.FromObservedFirstByte(f.Stream, 0, f.Clock, 0, frequency, TimeSpan.Zero, 64)).ParamName);
            foreach (TimeSpan budget in new[] { TimeSpan.MinValue, TimeSpan.FromTicks(-1), ControlFrameBudget.MaxBudget + TimeSpan.FromTicks(1), TimeSpan.MaxValue })
                Assert.Equal("budget", Assert.Throws<ArgumentOutOfRangeException>(() =>
                    ControlFrameReadAttempt.FromObservedFirstByte(f.Stream, 0, f.Clock, 0, Frequency, budget, 64)).ParamName);
            foreach (int maximum in new[] { int.MinValue, -1, 0, TransportConstants.MaxControlMessageBytes + 1, int.MaxValue })
                Assert.Equal("maxBytes", Assert.Throws<ArgumentOutOfRangeException>(() =>
                    ControlFrameReadAttempt.FromObservedFirstByte(f.Stream, 0, f.Clock, 0, Frequency, TimeSpan.Zero, maximum)).ParamName);
            Assert.Empty(f.Stream.Calls);
            Assert.Empty(f.Clock.Calls);
            Assert.Null(f.Attempt.Completion);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ConstructionAcceptsTrustedNegativeAndExtremeAnchorsWithoutExternalOperations()
    {
        Fixture f = new();
        try
        {
            foreach (long anchor in new[] { long.MinValue, -1L, 0L, long.MaxValue })
            {
                ControlFrameReadAttempt attempt = ControlFrameReadAttempt.FromObservedFirstByte(
                    f.Stream, 0, f.Clock, anchor, long.MaxValue, ControlFrameBudget.MaxBudget, TransportConstants.MaxControlMessageBytes);
                AttemptSnapshot initial = attempt.Snapshot;
                Assert.Equal(AttemptEnd.Pending, initial.End);
                Assert.False(initial.Committed);
                Assert.False(initial.StopRequested);
                Assert.Null(initial.FirstRead);
                Assert.Null(initial.Continuation);
                Assert.Null(initial.Budget);
                Assert.Null(attempt.Completion);
                Assert.False(attempt.AbortRequired.IsCompleted);
                Assert.NotSame(initial, attempt.Snapshot);
                attempt.RequestStop();
                Assert.Equal(AttemptEnd.Pending, initial.End);
                Assert.False(initial.StopRequested);
            }
            Assert.Empty(f.Stream.Calls);
            Assert.Empty(f.Clock.Calls);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task StopBeforeStartSkipsFirstReadContinuationBudgetAndClock()
    {
        Fixture f = new();
        try
        {
            f.Attempt.RequestStop();
            f.Attempt.RequestStop();
            Assert.True(f.Attempt.AbortRequired.IsCompletedSuccessfully);
            Assert.Null(f.Attempt.Completion);
            Task<FrameOutcome> completion = f.Attempt.Start();
            AssertRejected(await f.DrainAsync(), f);
            Assert.Same(completion, f.Attempt.Completion);
            Assert.Throws<InvalidOperationException>(() => { _ = f.Attempt.Start(); });
            Assert.Empty(f.Stream.Calls);
            Assert.Empty(f.Clock.Calls);
            Assert.Null(f.Attempt.Snapshot.Continuation);
            Assert.Null(f.Attempt.Snapshot.Budget);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ObservedZeroByteIsPrefixDataAndNeverEofOrAnExtraRead()
    {
        Fixture f = new();
        try
        {
            _ = f.Attempt.Start();
            AssertFrame(await f.DrainAsync(), f);
            Assert.Equal((byte)0, f.Wire[0]);
            Assert.Equal(new[] { 3, f.Payload.Length }, f.Stream.Calls.Select(call => call.Memory.Length));
            Assert.Equal(1, SegmentOf(f.Stream.Calls[0].Memory).Offset);
            Assert.Equal(1, f.Clock.CreateCalls);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(1)]
    [InlineData(255)]
    public async Task NonzeroObservedHighByteParticipatesInLengthValidation(int highByte)
    {
        Fixture f = new(firstByte: (byte)highByte);
        try
        {
            _ = f.Attempt.Start();
            AssertRejected(await f.DrainAsync(), f);
            Assert.IsType<FrameProtocolException>(f.Attempt.Snapshot.Continuation!.Error);
            Assert.Equal(4, f.Consumed);
            Assert.Equal(3, Assert.Single(f.Stream.Calls).Memory.Length);
            AssertCleared(f.Stream.Calls[0].Array);
            Assert.False(f.Attempt.Snapshot.Continuation.Taken);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(3, 8)]
    public async Task FragmentedContinuationConsumesExactlyOneFrameAndTransfersItsArray(int prefixSlice, int payloadSlice)
    {
        Fixture f = new();
        f.ReadHook = (_, memory) => new ValueTask<int>(f.CopyWire(memory, f.Consumed < 4 ? prefixSlice : payloadSlice));
        try
        {
            Task<FrameOutcome> completion = f.Attempt.Start();
            FrameOutcome result = await f.DrainAsync();
            AssertFrame(result, f);
            Assert.Equal(4 + f.Payload.Length, f.Consumed);
            Assert.Equal(new byte[] { 0, 0, 0, 1, 0xDB }, f.Wire[f.Consumed..]);
            ReadCall[] calls = f.Stream.Calls;
            Assert.Equal(3, calls[0].Memory.Length);
            Assert.Equal(4, calls[0].Array.Length);
            Assert.Equal(1, SegmentOf(calls[0].Memory).Offset);
            Assert.All(calls, call => Assert.Same(completion, call.Registered));
            Assert.All(calls, call => Assert.True(call.OnThreadPool));
            AssertCleared(calls[0].Array);
            Assert.Same(calls[^1].Array, result.Payload);
            Assert.Equal((3 + prefixSlice - 1) / prefixSlice + (f.Payload.Length + payloadSlice - 1) / payloadSlice, calls.Length);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task DeliveredOwnershipSurvivesStopAndRepeatedAwaitKeepsSameAlias()
    {
        Fixture f = new();
        try
        {
            Task<FrameOutcome> completion = f.Attempt.Start();
            FrameOutcome delivered = await f.DrainAsync();
            AssertFrame(delivered, f);
            AttemptSnapshot historical = f.Attempt.Snapshot;
            byte[] payload = Assert.IsType<byte[]>(delivered.Payload);
            payload[0] = 0x5A;
            f.Attempt.RequestStop();
            f.Attempt.RequestStop();
            FrameOutcome alias = await completion;
            Assert.Same(delivered, alias);
            Assert.Same(payload, alias.Payload);
            Assert.Equal((byte)0x5A, payload[0]);
            Assert.Equal(f.Payload[1..], payload[1..]);
            Assert.False(f.Attempt.AbortRequired.IsCompleted);
            Assert.True(f.Attempt.Snapshot.Committed);
            Assert.True(f.Attempt.Snapshot.StopRequested);
            Assert.Equal(AttemptEnd.Frame, f.Attempt.Snapshot.End);
            Assert.False(historical.StopRequested);
            Assert.Throws<InvalidOperationException>(() => { _ = f.Attempt.Start(); });
            Assert.Equal(2, f.Stream.Calls.Length);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(10, 10)]
    [InlineData(10, 11)]
    [InlineData(0, 0)]
    public async Task InitialExpiryEqualityAndZeroBudgetRejectWithoutContinuationIo(long budgetTicks, long elapsed)
    {
        Fixture f = new(anchor: -50, budget: TimeSpan.FromTicks(budgetTicks));
        f.Clock.Now = -50 + elapsed;
        try
        {
            _ = f.Attempt.Start();
            AssertRejected(await f.DrainAsync(), f);
            Assert.Empty(f.Stream.Calls);
            Assert.Null(f.Attempt.Snapshot.Continuation);
            Assert.Equal(BudgetEnd.Expired, f.Attempt.Snapshot.Budget!.End);
            Assert.IsType<TimeoutException>(f.Attempt.Snapshot.Budget.Error);
            Assert.Equal(0, f.Clock.CreateCalls);
            Assert.Empty(f.Timer.Changes);
            if (budgetTicks == 0) Assert.Empty(f.Clock.Calls);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateOrChangeExpiryBeforeReadyRejectsWithoutIoButStillJoinsOriginalDispose(bool duringChange)
    {
        Fixture f = new(anchor: 100, budget: TimeSpan.FromTicks(10));
        f.Clock.Now = 106;
        TaskCompletionSource dispose = f.NewDispose();
        f.Timer.DisposeHook = () => new ValueTask(dispose.Task);
        if (duringChange) f.Timer.ChangeHook = () => f.Clock.Now = 110;
        else f.Clock.CreateHook = () => f.Clock.Now = 110;
        try
        {
            Task<FrameOutcome> completion = f.Attempt.Start();
            await ReachedBeforeCompletionAsync(f.Timer.DisposeReached.Task, completion);
            await AssertAwaitAsync(dispose.Task, f.Budget.Completion!);
            await AssertAwaitAsync(f.Budget.Completion!, completion);
            Assert.True(f.Attempt.AbortRequired.IsCompletedSuccessfully);
            Assert.Equal(BudgetEnd.Expired, f.Attempt.Snapshot.Budget!.End);
            Assert.IsType<TimeoutException>(f.Attempt.Snapshot.Budget.Error);
            Assert.True(f.Attempt.Snapshot.Budget.TimerAcquired);
            Assert.False(f.Attempt.Snapshot.Budget.TimerReleased);
            Assert.Null(f.Attempt.Snapshot.FirstRead);
            Assert.Null(f.Attempt.Snapshot.Continuation);
            Assert.Empty(f.Stream.Calls);
            Assert.Equal(1, f.Clock.CreateCalls);
            Assert.Equal(duringChange ? 1 : 0, f.Timer.Changes.Length);
            Assert.False(dispose.Task.IsCompleted);
            Assert.False(completion.IsCompleted);
            dispose.SetResult();
            AssertRejected(await f.DrainAsync(), f);
            Assert.True(f.Attempt.Snapshot.Budget.TimerReleased);
            Assert.Null(f.Attempt.Snapshot.Continuation);
            Assert.Empty(f.Stream.Calls);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(long.MinValue, long.MinValue + 1)]
    [InlineData(-10, -9)]
    [InlineData(long.MaxValue - 1, long.MaxValue)]
    public async Task TrustedExtremeAndNegativeAnchorsUseSafeElapsedArithmetic(long anchor, long now)
    {
        Fixture f = new(anchor: anchor, budget: TimeSpan.FromTicks(2));
        f.Clock.Now = now;
        try
        {
            _ = f.Attempt.Start();
            AssertFrame(await f.DrainAsync(), f);
            Assert.Equal(TimeSpan.FromMilliseconds(1), Assert.Single(f.Timer.Changes));
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(-1L, false)]
    [InlineData(0L, true)]
    public async Task FinalDeltaAcceptsLongMaxButRejectsOneTickBeyondWithoutOverflow(long finalNow, bool rejected)
    {
        Fixture f = new(anchor: long.MinValue, observedFrequency: long.MaxValue, budget: TimeSpan.FromSeconds(2));
        f.Clock.ReadTimestamp = _ => f.IsBudgetFinished ? finalNow : -1;
        try
        {
            _ = f.Attempt.Start();
            FrameOutcome result = await f.DrainAsync();
            Assert.Equal(BudgetEnd.FinishedInBudget, f.Attempt.Snapshot.Budget!.End);
            Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(f.Timer.Changes));
            if (rejected)
            {
                AssertRejected(result, f);
                InvalidOperationException error = Assert.IsType<InvalidOperationException>(f.Attempt.Snapshot.FinalSampleError);
                Assert.Equal("control-frame-final-range-invalid", error.Message);
                AssertCleared(f.Stream.Calls[1].Array);
            }
            else AssertFrame(result, f);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(10, 9)]
    [InlineData(long.MinValue, 0)]
    public async Task NegativeOrBeyondLongMaxInitialDeltaRejectsWithoutIo(long anchor, long now)
    {
        Fixture f = new(anchor: anchor);
        f.Clock.Now = now;
        try
        {
            _ = f.Attempt.Start();
            AssertRejected(await f.DrainAsync(), f);
            Assert.Equal(BudgetEnd.Faulted, f.Attempt.Snapshot.Budget!.End);
            Assert.IsType<InvalidOperationException>(f.Attempt.Snapshot.Budget.Error);
            Assert.Empty(f.Stream.Calls);
            Assert.Null(f.Attempt.Snapshot.Continuation);
            Assert.Equal(0, f.Clock.CreateCalls);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task TimerUsesRemainingBudgetFromSuppliedAnchorAndFrequencyNotFreshObservation()
    {
        Fixture f = new(anchor: 100, observedFrequency: 1_000, budget: TimeSpan.FromMilliseconds(20));
        f.Clock.Now = 113;
        try
        {
            _ = f.Attempt.Start();
            AssertFrame(await f.DrainAsync(), f);
            Assert.Equal(TimeSpan.FromMilliseconds(7), Assert.Single(f.Timer.Changes));
            Assert.All(f.Clock.Samples, sample => Assert.Equal(113, sample));
            Assert.Null(f.Attempt.Snapshot.ObservationError);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(0)]
    [InlineData(Frequency + 1)]
    public async Task RuntimeFrequencyMustMatchSuppliedFrequencyRatherThanReplaceIt(long currentFrequency)
    {
        Fixture f = new();
        f.Clock.Frequency = currentFrequency;
        try
        {
            _ = f.Attempt.Start();
            AssertRejected(await f.DrainAsync(), f);
            Assert.Equal(BudgetEnd.Faulted, f.Attempt.Snapshot.Budget!.End);
            Assert.IsType<InvalidOperationException>(f.Attempt.Snapshot.Budget.Error);
            Assert.Null(f.Attempt.Snapshot.ObservationError);
            Assert.Empty(f.Clock.Samples);
            Assert.Empty(f.Stream.Calls);
            Assert.Equal(0, f.Clock.CreateCalls);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task FrequencyChangeDuringInitialTimestampRejectsBeforeCreatingTimer()
    {
        Fixture f = new();
        f.Clock.ReadTimestamp = _ => { f.Clock.Frequency++; return f.Clock.Now; };
        try
        {
            _ = f.Attempt.Start();
            AssertRejected(await f.DrainAsync(), f);
            Assert.IsType<InvalidOperationException>(f.Attempt.Snapshot.Budget!.Error);
            Assert.Equal(0, f.Clock.CreateCalls);
            Assert.Empty(f.Stream.Calls);
            Assert.Null(f.Attempt.Snapshot.ObservationError);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialFrequencyOrTimestampFailureBelongsToBudgetNotObservation(bool timestamp)
    {
        Fixture f = new();
        IOException error = new("原观察入口运行采样失败");
        if (timestamp) f.Clock.ReadTimestamp = _ => throw error;
        else f.Clock.ReadFrequency = _ => throw error;
        try
        {
            _ = f.Attempt.Start();
            AssertRejected(await f.DrainAsync(), f);
            Assert.Same(error, f.Attempt.Snapshot.Budget!.Error);
            Assert.Equal(BudgetEnd.Faulted, f.Attempt.Snapshot.Budget.End);
            Assert.Null(f.Attempt.Snapshot.ObservationError);
            Assert.Empty(f.Stream.Calls);
            Assert.Equal(0, f.Clock.CreateCalls);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task InitialTimestampPrefixRunsOnWorkerAndStopDoesNotWaitOrTouchClock()
    {
        Fixture f = new();
        Pause sample = f.NewPause();
        f.Clock.ReadTimestamp = _ => { sample.Wait(); return f.Clock.Now; };
        try
        {
            Task<FrameOutcome> completion = f.Attempt.Start();
            await ReachedBeforeCompletionAsync(sample.Reached.Task, completion);
            Assert.Null(f.Attempt.Snapshot.FirstRead);
            Assert.Empty(f.Stream.Calls);
            int before = f.Clock.Calls.Length;
            await f.StopFromOtherThreadAsync();
            Assert.Equal(before, f.Clock.Calls.Length);
            Assert.True(f.Attempt.AbortRequired.IsCompletedSuccessfully);
            Assert.False(completion.IsCompleted);
            sample.Open();
            AssertRejected(await f.DrainAsync(), f);
            Assert.Empty(f.Stream.Calls);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StopDuringOriginalContinuationReadCannotClearBorrowOrFakeJoin(int target)
    {
        Fixture f = new();
        TaskCompletionSource<int> read = f.NewRead();
        f.HoldRead(target, read);
        try
        {
            Task<FrameOutcome> completion = f.Attempt.Start();
            await f.Stream.ReadAtAsync(target, completion);
            await AssertAwaitAsync(read.Task, f.Frame.Completion!);
            byte[] before = f.Stream.Calls[^1].Array.ToArray();
            await f.StopFromOtherThreadAsync();
            Assert.True(f.Attempt.AbortRequired.IsCompletedSuccessfully);
            await AssertAwaitAsync(read.Task, completion);
            Assert.False(read.Task.IsCompleted);
            Assert.False(completion.IsCompleted);
            Assert.Equal(before, f.Stream.Calls[^1].Array);
            Assert.Equal(target, f.Stream.Calls.Length);
            read.SetResult(f.Stream.Calls[^1].Memory.Length);
            AssertRejected(await f.DrainAsync(), f);
            Assert.Equal(target, f.Stream.Calls.Length);
            Assert.All(f.Stream.Calls, call => AssertCleared(call.Array));
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task SynchronousReadPrefixAndItsLateOriginalTaskBothRemainOwnedAfterStop()
    {
        Fixture f = new();
        Pause prefix = f.NewPause();
        TaskCompletionSource<int> read = f.NewRead();
        f.ReadHook = (_, memory) =>
        {
            f.CopyWire(memory);
            prefix.Wait();
            return new ValueTask<int>(read.Task);
        };
        try
        {
            Task<FrameOutcome> completion = f.Attempt.Start();
            await ReachedBeforeCompletionAsync(prefix.Reached.Task, completion);
            byte[] before = f.Stream.Calls[0].Array.ToArray();
            Assert.Same(completion, f.Stream.Calls[0].Registered);
            await f.StopFromOtherThreadAsync();
            Assert.False(completion.IsCompleted);
            Assert.Equal(before, f.Stream.Calls[0].Array);
            prefix.Open();
            await AssertAwaitAsync(read.Task, completion);
            Assert.Equal(before, f.Stream.Calls[0].Array);
            read.SetResult(3);
            AssertRejected(await f.DrainAsync(), f);
            Assert.Single(f.Stream.Calls);
            AssertCleared(f.Stream.Calls[0].Array);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task OriginalTimerDisposeMustExitBeforeObservedFrameDelivery()
    {
        Fixture f = new();
        TaskCompletionSource dispose = f.NewDispose();
        f.Timer.DisposeHook = () => new ValueTask(dispose.Task);
        try
        {
            Task<FrameOutcome> completion = f.Attempt.Start();
            await ReachedBeforeCompletionAsync(f.Timer.DisposeReached.Task, completion);
            await AssertAwaitAsync(dispose.Task, f.Budget.Completion!);
            Assert.True(f.Attempt.Snapshot.Continuation!.Taken);
            Assert.True(f.Attempt.Snapshot.Budget!.FinishRequested);
            Assert.False(f.Attempt.Snapshot.Budget.TimerReleased);
            Assert.False(completion.IsCompleted);
            Assert.False(f.Attempt.AbortRequired.IsCompleted);
            Assert.Equal(f.Payload, f.Stream.Calls[1].Array);
            dispose.SetResult();
            AssertFrame(await f.DrainAsync(), f);
            Assert.True(dispose.Task.IsCompletedSuccessfully);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopDuringDisposeStillJoinsSynchronousPrefixAndOriginalTask(bool blockPrefix)
    {
        Fixture f = new();
        Pause prefix = f.NewPause();
        TaskCompletionSource dispose = f.NewDispose();
        f.Timer.DisposeHook = () =>
        {
            if (blockPrefix) prefix.Wait();
            return new ValueTask(dispose.Task);
        };
        try
        {
            Task<FrameOutcome> completion = f.Attempt.Start();
            if (blockPrefix) await ReachedBeforeCompletionAsync(prefix.Reached.Task, completion);
            else
            {
                await ReachedBeforeCompletionAsync(f.Timer.DisposeReached.Task, completion);
                await AssertAwaitAsync(dispose.Task, f.Budget.Completion!);
            }
            await f.StopFromOtherThreadAsync();
            Assert.True(f.Attempt.AbortRequired.IsCompletedSuccessfully);
            Assert.False(completion.IsCompleted);
            prefix.Open();
            await AssertAwaitAsync(dispose.Task, completion);
            Assert.False(dispose.Task.IsCompleted);
            dispose.SetResult();
            AssertRejected(await f.DrainAsync(), f);
            AssertCleared(f.Stream.Calls[1].Array);
            Assert.True(f.Attempt.Snapshot.Budget!.TimerReleased);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task RejectedStationaryOwnedPayloadIsClearedBeforeBlockedDisposeJoins()
    {
        Fixture f = new();
        TaskCompletionSource dispose = f.NewDispose();
        f.Timer.DisposeHook = () => new ValueTask(dispose.Task);
        try
        {
            Task<FrameOutcome> completion = f.Attempt.Start();
            await ReachedBeforeCompletionAsync(f.Timer.DisposeReached.Task, completion);
            await AssertAwaitAsync(dispose.Task, f.Budget.Completion!);
            Assert.True(f.Frame.Completion!.IsCompletedSuccessfully);
            Assert.True(f.Attempt.Snapshot.Continuation!.Taken);
            byte[] owned = f.Stream.Calls[1].Array;
            Assert.Equal(f.Payload, owned);
            await f.StopFromOtherThreadAsync();
            await AssertAwaitAsync(f.Budget.Completion!, completion);
            Assert.True(f.Attempt.AbortRequired.IsCompletedSuccessfully);
            Assert.False(completion.IsCompleted);
            Assert.False(dispose.Task.IsCompleted);
            AssertCleared(owned);
            dispose.SetResult();
            AssertRejected(await f.DrainAsync(), f);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ExpiryNotificationDoesNotReplaceEitherOriginalReadOrDisposeJoin()
    {
        Fixture f = new(anchor: 100, budget: TimeSpan.FromTicks(10));
        TaskCompletionSource<int> read = f.NewRead();
        TaskCompletionSource dispose = f.NewDispose();
        f.HoldRead(2, read);
        f.Timer.DisposeHook = () => new ValueTask(dispose.Task);
        try
        {
            Task<FrameOutcome> completion = f.Attempt.Start();
            await f.Stream.ReadAtAsync(2, completion);
            await AssertAwaitAsync(read.Task, f.Frame.Completion!);
            f.Clock.Now = 110;
            f.Timer.Fire();
            await AssertAwaitAsync(dispose.Task, f.Budget.Completion!);
            await AssertAwaitAsync(f.Budget.Completion!, completion);
            Assert.True(f.Attempt.AbortRequired.IsCompletedSuccessfully);
            Assert.Equal(BudgetEnd.Expired, f.Attempt.Snapshot.Budget!.End);
            Assert.False(read.Task.IsCompleted);
            Assert.False(dispose.Task.IsCompleted);
            Assert.False(completion.IsCompleted);
            Assert.Equal(f.Payload, f.Stream.Calls[1].Array);
            dispose.SetResult();
            await f.Budget.Completion!.WaitAsync(Guard);
            await AssertAwaitAsync(read.Task, completion);
            Assert.False(completion.IsCompleted);
            read.SetResult(f.Payload.Length);
            AssertRejected(await f.DrainAsync(), f);
            AssertCleared(f.Stream.Calls[1].Array);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task TimerReleaseElapsedTimeRemainsChargedToSuppliedOldAnchor()
    {
        Fixture f = new(anchor: 100, budget: TimeSpan.FromTicks(10));
        f.Clock.Now = 106;
        TaskCompletionSource dispose = f.NewDispose();
        f.Timer.DisposeHook = () => new ValueTask(dispose.Task);
        try
        {
            Task<FrameOutcome> completion = f.Attempt.Start();
            await ReachedBeforeCompletionAsync(f.Timer.DisposeReached.Task, completion);
            await AssertAwaitAsync(dispose.Task, f.Budget.Completion!);
            f.Clock.Now = 110;
            dispose.SetResult();
            AssertRejected(await f.DrainAsync(), f);
            Assert.Equal(BudgetEnd.Expired, f.Attempt.Snapshot.Budget!.End);
            Assert.IsType<TimeoutException>(f.Attempt.Snapshot.Budget.Error);
            Assert.Null(f.Attempt.Snapshot.FinalSampleError);
            AssertCleared(f.Stream.Calls[1].Array);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task FinalAcceptanceRejectsAtOriginalDeadlineAfterBudgetAlreadyFinished()
    {
        Fixture f = new(anchor: 100, budget: TimeSpan.FromTicks(10));
        f.Clock.ReadTimestamp = _ => f.IsBudgetFinished ? 110 : 106;
        try
        {
            _ = f.Attempt.Start();
            AssertRejected(await f.DrainAsync(), f);
            Assert.Equal(BudgetEnd.FinishedInBudget, f.Attempt.Snapshot.Budget!.End);
            TimeoutException error = Assert.IsType<TimeoutException>(f.Attempt.Snapshot.FinalSampleError);
            Assert.Equal("control-frame-final-budget-expired", error.Message);
            AssertCleared(f.Stream.Calls[1].Array);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task CrossStageRegressionIsRejectedEvenWhenStillAboveObservedAnchor()
    {
        Fixture f = new(anchor: 100);
        f.Clock.ReadTimestamp = _ => f.IsBudgetFinished ? 104 : 105;
        try
        {
            _ = f.Attempt.Start();
            AssertRejected(await f.DrainAsync(), f);
            Assert.Equal(BudgetEnd.FinishedInBudget, f.Attempt.Snapshot.Budget!.End);
            Assert.IsType<InvalidOperationException>(f.Attempt.Snapshot.FinalSampleError);
            AssertCleared(f.Stream.Calls[1].Array);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalFrequencyChangeBeforeOrAfterSampleRejectsDelivery(bool afterSample)
    {
        Fixture f = new(observedFrequency: 1_000);
        if (afterSample)
            f.Clock.ReadTimestamp = _ => { if (f.IsBudgetFinished) f.Clock.Frequency++; return f.Clock.Now; };
        else
            f.Clock.ReadFrequency = _ => f.IsBudgetFinished ? 1_001 : 1_000;
        try
        {
            _ = f.Attempt.Start();
            AssertRejected(await f.DrainAsync(), f);
            Assert.Equal(BudgetEnd.FinishedInBudget, f.Attempt.Snapshot.Budget!.End);
            InvalidOperationException error = Assert.IsType<InvalidOperationException>(f.Attempt.Snapshot.FinalSampleError);
            Assert.Equal("control-frame-final-frequency-changed", error.Message);
            AssertCleared(f.Stream.Calls[1].Array);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task StopDuringFinalSampleWinsBeforeCommitAndClearsUndeliveredPayload()
    {
        Fixture f = new();
        Pause final = f.NewPause();
        f.Clock.ReadTimestamp = _ => { if (f.IsBudgetFinished) final.Wait(); return f.Clock.Now; };
        try
        {
            Task<FrameOutcome> completion = f.Attempt.Start();
            await ReachedBeforeCompletionAsync(final.Reached.Task, completion);
            Assert.True(f.Attempt.Snapshot.Continuation!.Taken);
            Assert.Equal(BudgetEnd.FinishedInBudget, f.Attempt.Snapshot.Budget!.End);
            await f.StopFromOtherThreadAsync();
            Assert.True(f.Attempt.AbortRequired.IsCompletedSuccessfully);
            Assert.False(completion.IsCompleted);
            final.Open();
            AssertRejected(await f.DrainAsync(), f);
            Assert.Null(f.Attempt.Snapshot.FinalSampleError);
            AssertCleared(f.Stream.Calls[1].Array);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task FinalSamplingFailurePreservesOriginalExceptionAndClearsPayload()
    {
        Fixture f = new();
        AggregateException error = new("原末检故障", new IOException("叶节点"));
        f.Clock.ReadTimestamp = _ => f.IsBudgetFinished ? throw error : f.Clock.Now;
        try
        {
            _ = f.Attempt.Start();
            AssertRejected(await f.DrainAsync(), f);
            Assert.Same(error, f.Attempt.Snapshot.FinalSampleError);
            Assert.Equal(BudgetEnd.FinishedInBudget, f.Attempt.Snapshot.Budget!.End);
            AssertCleared(f.Stream.Calls[1].Array);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task UnknownContinuationBorrowIsIsolatedInsteadOfInventingReadExit(int target)
    {
        Fixture f = new();
        IOException error = new("未取得原接续Task");
        byte[]? before = null;
        f.ReadHook = (call, memory) =>
        {
            if (call != target) return new ValueTask<int>(f.CopyWire(memory));
            memory.Span.Fill(0xA5);
            before = ArrayOf(memory).ToArray();
            throw error;
        };
        try
        {
            _ = f.Attempt.Start();
            AssertRejected(await f.DrainAsync(), f);
            Assert.Equal(target, f.Stream.Calls.Length);
            Assert.Same(error, f.Attempt.Snapshot.Continuation!.Error);
            Assert.True(f.Attempt.Snapshot.Continuation.BorrowExitUnproven);
            Assert.NotNull(before);
            Assert.Equal(before, f.Stream.Calls[^1].Array);
            f.Attempt.RequestStop();
            Assert.Equal(before, f.Stream.Calls[^1].Array);
            if (target == 2) AssertCleared(f.Stream.Calls[0].Array);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOriginalOrUnknownDisposeRejectsAndKeepsCorrectExitProof(bool unknown)
    {
        Fixture f = new();
        IOException error = new("原释放失败");
        TaskCompletionSource dispose = f.NewDispose();
        f.Timer.DisposeHook = () => unknown ? throw error : new ValueTask(dispose.Task);
        try
        {
            Task<FrameOutcome> completion = f.Attempt.Start();
            if (!unknown)
            {
                await ReachedBeforeCompletionAsync(f.Timer.DisposeReached.Task, completion);
                await AssertAwaitAsync(dispose.Task, f.Budget.Completion!);
                f.Attempt.RequestStop();
                dispose.SetException(error);
            }
            AssertRejected(await f.DrainAsync(), f);
            Assert.Same(error, f.Attempt.Snapshot.Budget!.CleanupError);
            Assert.Equal(unknown, f.Attempt.Snapshot.Budget.CleanupExitUnproven);
            Assert.False(f.Attempt.Snapshot.Budget.TimerReleased);
            AssertCleared(f.Stream.Calls[1].Array);
        }
        finally { await f.FinishAsync(); }
    }

    private static void AssertFrame(FrameOutcome result, Fixture f)
    {
        Assert.Equal(AttemptEnd.Frame, result.End);
        Assert.Equal(f.Payload, result.Payload);
        AttemptSnapshot snapshot = f.Attempt.Snapshot;
        Assert.Null(snapshot.FirstRead);
        Assert.Equal(AttemptEnd.Frame, snapshot.End);
        Assert.True(snapshot.Committed);
        Assert.True(snapshot.Continuation!.Taken);
        Assert.Equal(BudgetEnd.FinishedInBudget, snapshot.Budget!.End);
        Assert.True(snapshot.Budget.TimerReleased);
        Assert.Null(snapshot.ObservationError);
        Assert.Null(snapshot.FinalSampleError);
        Assert.Null(snapshot.CoordinationError);
        Assert.False(f.Attempt.AbortRequired.IsCompleted);
    }

    private static void AssertRejected(FrameOutcome result, Fixture f)
    {
        Assert.Equal(AttemptEnd.Rejected, result.End);
        Assert.Null(result.Payload);
        AttemptSnapshot snapshot = f.Attempt.Snapshot;
        Assert.Null(snapshot.FirstRead);
        Assert.Equal(AttemptEnd.Rejected, snapshot.End);
        Assert.False(snapshot.Committed);
        Assert.True(f.Attempt.AbortRequired.IsCompletedSuccessfully);
        Assert.Null(snapshot.ObservationError);
        Assert.Null(snapshot.CoordinationError);
    }

    private static async Task ReachedBeforeCompletionAsync(Task reached, Task completion)
    {
        await Task.WhenAny(reached, completion).WaitAsync(Guard);
        Assert.True(reached.IsCompletedSuccessfully, "未经过受控阶段，owner就已退出。");
        Assert.False(completion.IsCompleted, "受闸原操作尚未退出，owner不得完成。");
    }

    private static ArraySegment<byte> SegmentOf(Memory<byte> memory)
    {
        Assert.True(MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)memory, out ArraySegment<byte> segment));
        Assert.NotNull(segment.Array);
        return segment;
    }

    private static byte[] ArrayOf(Memory<byte> memory) => SegmentOf(memory).Array!;
    private static void AssertCleared(byte[] bytes) => Assert.All(bytes, value => Assert.Equal((byte)0, value));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // 只用反射证明已有原Task的精确join边，不发现或替代被测入口；WhenAny通知边不算join。
    private static async Task AssertAwaitAsync(Task original, Task completion)
    {
        using CancellationTokenSource guard = new(Guard);
        while (!HasAwaitPath(original, completion, new HashSet<Task>()))
        {
            Assert.False(original.IsCompleted, "原Task提前退出，失去受闸join证据。");
            Assert.False(completion.IsCompleted, "owner未等待指定原Task就退出。");
            if (guard.IsCancellationRequested)
                throw new XunitException("Guard：未观察到精确await/unwrap/WhenAll链，运行时不兼容不能充当语义失败。");
            await Task.Yield();
        }
    }

    private static bool HasAwaitPath(Task current, Task target, HashSet<Task> visited)
    {
        if (!visited.Add(current)) return false;
        foreach (object continuation in Continuations(current))
        {
            object? box = continuation is Delegate action ? action.Target : continuation;
            if (box is not Task && box is not null && RuntimeField(box.GetType(), "m_action")?.GetValue(box) is Delegate scheduled)
                box = scheduled.Target;
            if (box is not Task next) continue;
            object? machine = RuntimeField(next.GetType(), "StateMachine")?.GetValue(next);
            bool exact = machine is not null && machine.GetType().GetFields(Fields)
                .Where(field => field.Name.StartsWith("<>u__", StringComparison.Ordinal))
                .Select(field => field.GetValue(machine)).OfType<object>()
                .Any(awaiter => awaiter.GetType().GetFields(Fields)
                    .Any(field => typeof(Task).IsAssignableFrom(field.FieldType) && ReferenceEquals(field.GetValue(awaiter), current)));
            string type = next.GetType().FullName ?? "";
            bool unwrap = type.StartsWith("System.Threading.Tasks.UnwrapPromise`", StringComparison.Ordinal);
            bool whenAll = type == "System.Threading.Tasks.Task+WhenAllPromise" ||
                type.StartsWith("System.Threading.Tasks.Task+WhenAllPromise`", StringComparison.Ordinal);
            if (!exact && !unwrap && !whenAll) continue;
            if (ReferenceEquals(next, target) || HasAwaitPath(next, target, visited)) return true;
        }
        return false;
    }

    private static object[] Continuations(Task task)
    {
        FieldInfo field = RuntimeField(typeof(Task), "m_continuationObject")
            ?? throw new XunitException("运行时缺少Task continuation字段，不能验证join。");
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

    private sealed class Fixture
    {
        private readonly List<TaskCompletionSource<int>> _reads = [];
        private readonly List<TaskCompletionSource> _disposes = [];
        private readonly List<Pause> _pauses = [];
        private readonly List<Task> _tasks = [];
        private readonly ConcurrentQueue<string> _violations = new();
        internal byte[] Payload { get; } = [1, 0x32, 0, 0xFF, 5, 6, 7, 8];
        internal byte[] Wire { get; }
        // 原读已成功退出，首字节由owner持有；接续流从余三字节前缀开始。
        internal int Consumed { get; private set; } = 1;
        internal Func<int, Memory<byte>, ValueTask<int>>? ReadHook { get; set; }
        internal ProbeStream Stream { get; }
        internal Clock Clock { get; }
        internal Timer Timer { get; }
        internal ControlFrameReadAttempt Attempt { get; }
        internal ControlFrameContinuation Frame => Child<ControlFrameContinuation>("_frame");
        internal ControlFrameBudget Budget => Child<ControlFrameBudget>("_deadline");
        internal bool IsBudgetFinished => Attempt.Snapshot.Budget?.End == BudgetEnd.FinishedInBudget;

        internal Fixture(long anchor = 0, long observedFrequency = Frequency, TimeSpan? budget = null, byte firstByte = 0)
        {
            Wire = new byte[4 + Payload.Length + 5];
            BinaryPrimitives.WriteUInt32BigEndian(Wire, (uint)Payload.Length);
            Wire[0] = firstByte;
            Payload.CopyTo(Wire, 4);
            new byte[] { 0, 0, 0, 1, 0xDB }.CopyTo(Wire, 4 + Payload.Length);
            Stream = new ProbeStream(this);
            Timer = new Timer(this);
            Clock = new Clock(this) { Now = anchor, Frequency = observedFrequency };
            Attempt = ControlFrameReadAttempt.FromObservedFirstByte(Stream, Wire[0], Clock, anchor,
                observedFrequency, budget ?? TimeSpan.FromSeconds(1), 64);
        }

        private T Child<T>(string name) where T : class =>
            Assert.IsType<T>(typeof(ControlFrameReadAttempt).GetField(name, Fields)!.GetValue(Attempt));

        internal void Require(bool condition, string message)
        {
            if (condition) return;
            _violations.Enqueue(message);
            throw new XunitException(message);
        }

        internal int CopyWire(Memory<byte> memory, int limit = int.MaxValue)
        {
            int count = Math.Min(Math.Min(memory.Length, limit), Wire.Length - Consumed);
            Wire.AsMemory(Consumed, count).CopyTo(memory);
            Consumed += count;
            return count;
        }

        internal void HoldRead(int target, TaskCompletionSource<int> original) => ReadHook = (call, memory) =>
        {
            int count = CopyWire(memory);
            return call == target ? new ValueTask<int>(original.Task) : new ValueTask<int>(count);
        };

        internal TaskCompletionSource<int> NewRead()
        {
            TaskCompletionSource<int> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _reads.Add(source);
            return source;
        }

        internal TaskCompletionSource NewDispose()
        {
            TaskCompletionSource source = Signal();
            _disposes.Add(source);
            return source;
        }

        internal Pause NewPause()
        {
            Pause pause = new(this);
            _pauses.Add(pause);
            return pause;
        }

        internal async Task StopFromOtherThreadAsync()
        {
            Task stop = Task.Run(Attempt.RequestStop);
            _tasks.Add(stop);
            await stop.WaitAsync(Guard);
        }

        internal async Task<FrameOutcome> DrainAsync()
        {
            Task<FrameOutcome> completion = Assert.IsAssignableFrom<Task<FrameOutcome>>(Attempt.Completion);
            FrameOutcome result = await completion.WaitAsync(Guard);
            Assert.True(completion.IsCompletedSuccessfully);
            Assert.Same(completion, Attempt.Completion);
            Assert.Empty(_violations);
            Stream.AssertNoOtherOperations();
            Assert.Null(Attempt.Snapshot.FirstRead);
            Assert.Equal(0, Timer.SyncDisposeCalls);
            Assert.DoesNotContain("wall-clock", Clock.Calls);
            if (Attempt.Snapshot.Budget is { TimerAcquired: true }) Assert.Equal(1, Timer.DisposeCalls);
            return result;
        }

        internal async Task FinishAsync()
        {
            // 失败先开放所有同步闸，再Stop和放原Task；即使owner join被变异，也独立排空各原worker。
            foreach (Pause pause in _pauses) pause.Open();
            Attempt.RequestStop();
            foreach (TaskCompletionSource<int> read in _reads) read.TrySetResult(0);
            foreach (TaskCompletionSource dispose in _disposes) dispose.TrySetResult();
            try
            {
                try { await Task.WhenAll(_tasks).WaitAsync(Guard); }
                finally
                {
                    try
                    {
                        if (Attempt.Completion is { } completion) await completion.WaitAsync(Guard);
                    }
                    finally
                    {
                        try
                        {
                            if (Attempt.Snapshot.Continuation is not null && Frame.Completion is { } frame)
                                await frame.WaitAsync(Guard);
                        }
                        finally
                        {
                            try
                            {
                                if (Attempt.Snapshot.Budget is not null && Budget.Completion is { } budget)
                                    await budget.WaitAsync(Guard);
                            }
                            finally
                            {
                                foreach (Task original in _reads.Select(read => (Task)read.Task).Concat(_disposes.Select(dispose => dispose.Task)))
                                {
                                    try { await original; }
                                    catch { _ = original.Exception; }
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                foreach (Pause pause in _pauses) pause.Dispose();
                Assert.Empty(_violations);
                Stream.AssertNoOtherOperations();
            }
        }
    }

    private sealed class Pause(Fixture f) : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        internal TaskCompletionSource Reached { get; } = Signal();
        internal void Wait()
        {
            Reached.TrySetResult();
            f.Require(_release.Wait(Guard), "Guard：同步闸未开放，夹具失败不是语义拒绝。");
        }
        internal void Open() => _release.Set();
        public void Dispose() => _release.Dispose();
    }

    private sealed class Clock(Fixture f) : TimeProvider
    {
        private readonly ConcurrentQueue<string> _calls = new();
        private readonly ConcurrentQueue<long> _samples = new();
        private long _now, _frequency;
        private int _timestamps, _frequencies, _creates;
        internal string[] Calls => _calls.ToArray();
        internal long[] Samples => _samples.ToArray();
        internal long Now { get => Volatile.Read(ref _now); set => Volatile.Write(ref _now, value); }
        internal long Frequency { get => Volatile.Read(ref _frequency); set => Volatile.Write(ref _frequency, value); }
        internal int CreateCalls => Volatile.Read(ref _creates);
        internal Func<int, long>? ReadTimestamp { get; set; }
        internal Func<int, long>? ReadFrequency { get; set; }
        internal Action? CreateHook { get; set; }
        public override long TimestampFrequency
        {
            get
            {
                _calls.Enqueue("frequency");
                return ReadFrequency?.Invoke(Interlocked.Increment(ref _frequencies)) ?? Frequency;
            }
        }
        public override long GetTimestamp()
        {
            _calls.Enqueue("timestamp");
            int call = Interlocked.Increment(ref _timestamps);
            long sample = ReadTimestamp?.Invoke(call) ?? Now;
            _samples.Enqueue(sample);
            return sample;
        }
        public override DateTimeOffset GetUtcNow()
        {
            _calls.Enqueue("wall-clock");
            f.Require(false, "原观察入口不能读取墙钟。");
            return default;
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _calls.Enqueue("create");
            Interlocked.Increment(ref _creates);
            f.Require(dueTime == Timeout.InfiniteTimeSpan && period == Timeout.InfiniteTimeSpan, "创建timer必须保持未排期。");
            f.Timer.Bind(callback, state);
            CreateHook?.Invoke();
            return f.Timer;
        }
        internal void Record(string operation) => _calls.Enqueue(operation);
    }

    private sealed class Timer(Fixture f) : ITimer
    {
        private readonly ConcurrentQueue<TimeSpan> _changes = new();
        private TimerCallback? _callback;
        private object? _state;
        private int _disposes, _syncDisposes;
        internal TaskCompletionSource DisposeReached { get; } = Signal();
        internal Func<ValueTask>? DisposeHook { get; set; }
        internal Action? ChangeHook { get; set; }
        internal TimeSpan[] Changes => _changes.ToArray();
        internal int DisposeCalls => Volatile.Read(ref _disposes);
        internal int SyncDisposeCalls => Volatile.Read(ref _syncDisposes);
        internal void Bind(TimerCallback callback, object? state) { _callback = callback; _state = state; }
        internal void Fire() => Assert.IsType<TimerCallback>(_callback)(_state);
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            f.Clock.Record("change");
            f.Require(period == Timeout.InfiniteTimeSpan, "timer不得周期性排期。");
            _changes.Enqueue(dueTime);
            ChangeHook?.Invoke();
            return true;
        }
        public ValueTask DisposeAsync()
        {
            f.Clock.Record("dispose");
            Interlocked.Increment(ref _disposes);
            DisposeReached.TrySetResult();
            return DisposeHook?.Invoke() ?? ValueTask.CompletedTask;
        }
        public void Dispose()
        {
            Interlocked.Increment(ref _syncDisposes);
            f.Require(false, "不得用同步Dispose冒充原DisposeAsync。");
        }
    }

    private sealed record ReadCall(Memory<byte> Memory, CancellationToken Token, Task? Registered, bool OnThreadPool)
    {
        internal byte[] Array => ArrayOf(Memory);
    }

    private sealed class ProbeStream(Fixture f) : Stream
    {
        private readonly ConcurrentQueue<ReadCall> _calls = new();
        private readonly ConcurrentQueue<string> _other = new();
        private readonly TaskCompletionSource[] _entered = Enumerable.Range(0, 32).Select(_ => Signal()).ToArray();
        private int _readCount;
        internal ReadCall[] Calls => _calls.ToArray();
        internal async Task ReadAtAsync(int call, Task completion)
        {
            await Task.WhenAny(_entered[call - 1].Task, completion).WaitAsync(Guard);
            Assert.True(_entered[call - 1].Task.IsCompletedSuccessfully, "owner未进入指定原读取就已退出。");
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _readCount);
            if (call > _entered.Length) throw Unexpected("读取次数超过测试帧界限");
            _calls.Enqueue(new ReadCall(buffer, cancellationToken, f.Attempt.Completion, Thread.CurrentThread.IsThreadPoolThread));
            _entered[call - 1].TrySetResult();
            return f.ReadHook?.Invoke(call, buffer) ?? new ValueTask<int>(f.CopyWire(buffer));
        }
        internal void AssertNoOtherOperations()
        {
            Assert.Empty(_other);
            Assert.All(Calls, call => Assert.Equal(CancellationToken.None, call.Token));
        }
        private Exception Unexpected(string operation)
        {
            _other.Enqueue(operation);
            return new XunitException($"借用流不应执行{operation}。");
        }
        public override bool CanRead => throw Unexpected(nameof(CanRead));
        public override bool CanWrite => throw Unexpected(nameof(CanWrite));
        public override bool CanSeek => throw Unexpected(nameof(CanSeek));
        public override bool CanTimeout => throw Unexpected(nameof(CanTimeout));
        public override long Length => throw Unexpected(nameof(Length));
        public override long Position { get => throw Unexpected("Position.get"); set => throw Unexpected("Position.set"); }
        public override int ReadTimeout { get => throw Unexpected("ReadTimeout.get"); set => throw Unexpected("ReadTimeout.set"); }
        public override int WriteTimeout { get => throw Unexpected("WriteTimeout.get"); set => throw Unexpected("WriteTimeout.set"); }
        public override int Read(byte[] buffer, int offset, int count) => throw Unexpected("Read(byte[])");
        public override int Read(Span<byte> buffer) => throw Unexpected("Read(Span)");
        public override int ReadByte() => throw Unexpected(nameof(ReadByte));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw Unexpected("ReadAsync(byte[])");
        public override void Write(byte[] buffer, int offset, int count) => throw Unexpected("Write(byte[])");
        public override void Write(ReadOnlySpan<byte> buffer) => throw Unexpected("Write(Span)");
        public override void WriteByte(byte value) => throw Unexpected(nameof(WriteByte));
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw Unexpected("WriteAsync(byte[])");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => throw Unexpected("WriteAsync(Memory)");
        public override void Flush() => throw Unexpected(nameof(Flush));
        public override Task FlushAsync(CancellationToken cancellationToken) => throw Unexpected(nameof(FlushAsync));
        public override long Seek(long offset, SeekOrigin origin) => throw Unexpected(nameof(Seek));
        public override void SetLength(long value) => throw Unexpected(nameof(SetLength));
        public override void Close() => _other.Enqueue(nameof(Close));
        protected override void Dispose(bool disposing) => _other.Enqueue(nameof(Dispose));
        public override ValueTask DisposeAsync() { _other.Enqueue(nameof(DisposeAsync)); return ValueTask.CompletedTask; }
    }
}
