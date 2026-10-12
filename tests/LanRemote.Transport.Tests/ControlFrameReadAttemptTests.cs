using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks.Sources;
using Xunit.Sdk;
using AttemptEnd = LanRemote.Transport.ControlFrameReadAttempt.AttemptEnd;
using AttemptSnapshot = LanRemote.Transport.ControlFrameReadAttempt.AttemptSnapshot;
using FrameOutcome = LanRemote.Transport.ControlFrameReadAttempt.FrameOutcome;
using BudgetEnd = LanRemote.Transport.ControlFrameBudget.BudgetEnd;

namespace LanRemote.Transport.Tests;

public sealed class ControlFrameReadAttemptTests
{
    // Guard 只防夹具挂死；时序依据是显式闸、原 Task 的精确 await 链及已经返回的调用。
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private const int TestTimeout = 60_000;
    private const long Frequency = TimeSpan.TicksPerSecond;
    private const byte Marker = 0xA5;
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact(Timeout = TestTimeout)]
    public async Task ConstructorValidatesWithoutIoOrClockAndSnapshotsAreIndependent()
    {
        Fixture f = new();
        try
        {
            Assert.Equal("stream", Assert.Throws<ArgumentNullException>(() =>
                new ControlFrameReadAttempt(null!, f.Clock, TimeSpan.Zero, 64)).ParamName);
            Assert.Equal("clock", Assert.Throws<ArgumentNullException>(() =>
                new ControlFrameReadAttempt(f.Stream, null!, TimeSpan.Zero, 64)).ParamName);
            foreach (TimeSpan invalid in new[] { TimeSpan.MinValue, TimeSpan.FromTicks(-1), ControlFrameBudget.MaxBudget + TimeSpan.FromTicks(1), TimeSpan.MaxValue })
                Assert.Equal("budget", Assert.Throws<ArgumentOutOfRangeException>(() =>
                    new ControlFrameReadAttempt(f.Stream, f.Clock, invalid, 64)).ParamName);
            foreach (int invalid in new[] { int.MinValue, -1, 0, TransportConstants.MaxControlMessageBytes + 1, int.MaxValue })
                Assert.Equal("maxBytes", Assert.Throws<ArgumentOutOfRangeException>(() =>
                    new ControlFrameReadAttempt(f.Stream, f.Clock, TimeSpan.Zero, invalid)).ParamName);
            Assert.Null(f.Attempt.Completion);
            Assert.False(f.Attempt.AbortRequired.IsCompleted);
            AttemptSnapshot initial = f.Attempt.Snapshot;
            Assert.Equal(AttemptEnd.Pending, initial.End);
            Assert.False(initial.StopRequested);
            Assert.False(initial.Committed);
            Assert.Null(initial.FirstRead);
            Assert.Null(initial.Continuation);
            Assert.Null(initial.Budget);
            Assert.NotSame(initial, f.Attempt.Snapshot);
            Assert.Empty(f.Stream.Calls);
            Assert.Empty(f.Clock.Calls);
            f.Attempt.RequestStop();
            Assert.Equal(AttemptEnd.Pending, initial.End);
            Assert.False(initial.StopRequested);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task StopBeforeStartNotifiesAndSkipsAllExternalOperations()
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
            Assert.Null(f.Attempt.Snapshot.FirstRead);
            Assert.Null(f.Attempt.Snapshot.Continuation);
            Assert.Null(f.Attempt.Snapshot.Budget);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EofDoesNotCreateBudgetOrTouchClockEvenWithZeroBudget(bool zeroBudget)
    {
        Fixture f = new(budget: zeroBudget ? TimeSpan.Zero : null);
        f.ReadHook = (_, memory) => { memory.Span.Fill(Marker); return new ValueTask<int>(0); };
        try
        {
            _ = f.Attempt.Start();
            FrameOutcome result = await f.DrainAsync();
            Assert.Equal(AttemptEnd.EndOfStream, result.End);
            Assert.Null(result.Payload);
            Assert.True(f.Attempt.Snapshot.Committed);
            Assert.Equal(ControlReadAhead.ReadEnd.EndOfStream, f.Attempt.Snapshot.FirstRead!.End);
            Assert.True(f.Attempt.Snapshot.FirstRead.Taken);
            Assert.Null(f.Attempt.Snapshot.Budget);
            Assert.Null(f.Attempt.Snapshot.Continuation);
            Assert.Empty(f.Clock.Calls);
            AssertCleared(Assert.Single(f.Stream.Calls).Array);
            Assert.False(f.Attempt.AbortRequired.IsCompleted);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ZeroFirstByteIsAFramePrefixNotEof()
    {
        Fixture f = new();
        try
        {
            _ = f.Attempt.Start();
            FrameOutcome result = await f.DrainAsync();
            AssertFrame(result, f);
            Assert.Equal((byte)0, f.Wire[0]);
            Assert.Equal(ControlReadAhead.ReadEnd.Byte, f.Attempt.Snapshot.FirstRead!.End);
            Assert.Equal(new[] { 1, 3, f.Payload.Length }, f.Stream.Calls.Select(call => call.Memory.Length));
            Assert.Equal(1, f.Clock.CreateCalls);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task FirstReadOriginalMustExitBeforeObservationOrContinuation()
    {
        Fixture f = new();
        TaskCompletionSource<int> original = f.NewRead();
        f.ReadHook = (call, memory) =>
        {
            int count = f.CopyWire(memory);
            return call == 1 ? new ValueTask<int>(original.Task) : new ValueTask<int>(count);
        };
        f.Clock.ReadTimestamp = _ =>
        {
            Assert.True(original.Task.IsCompletedSuccessfully);
            Assert.True(f.First.Completion!.IsCompletedSuccessfully);
            return f.Clock.Now;
        };
        try
        {
            Task<FrameOutcome> completion = f.Attempt.Start();
            await f.Stream.ReadAtAsync(1, completion);
            await AssertAwaitAsync(original.Task, f.First.Completion!);
            Assert.False(completion.IsCompleted);
            Assert.Empty(f.Clock.Calls);
            Assert.Null(f.Attempt.Snapshot.Continuation);
            Assert.Null(f.Attempt.Snapshot.Budget);
            Assert.Single(f.Stream.Calls);
            original.SetResult(1);
            AssertFrame(await f.DrainAsync(), f);
            AssertCleared(f.Stream.Calls[0].Array);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task SynchronousFirstReadPrefixDoesNotHoldGateAndLateOriginalStillMustExit()
    {
        Fixture f = new();
        Pause prefix = f.NewPause();
        TaskCompletionSource<int> original = f.NewRead();
        f.ReadHook = (_, memory) =>
        {
            memory.Span.Fill(Marker);
            prefix.Wait();
            return new ValueTask<int>(original.Task);
        };
        try
        {
            Task<Task<FrameOutcome>> start = f.Keep(Task.Factory.StartNew<Task<FrameOutcome>>(f.Attempt.Start,
                CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default));
            Task<FrameOutcome> completion = await start.WaitAsync(Guard);
            await ReachedBeforeCompletionAsync(prefix.Reached.Task, completion);
            Assert.Empty(f.Clock.Calls);
            Assert.Null(f.Attempt.Snapshot.Continuation);
            Assert.Same(completion, Assert.Single(f.Stream.Calls).Registered);
            Assert.True(f.Stream.Calls[0].OnThreadPool);
            Task<Exception?> repeat = f.Keep(Task.Run<Exception?>(() => Record.Exception(() => { _ = f.Attempt.Start(); })));
            Assert.IsType<InvalidOperationException>(await repeat.WaitAsync(Guard));
            await f.StopFromOtherThreadAsync();
            Assert.True(f.Attempt.AbortRequired.IsCompletedSuccessfully);
            Assert.False(completion.IsCompleted);
            Assert.Equal(Marker, f.Stream.Calls[0].Array[0]);
            prefix.Open();
            await AssertAwaitAsync(original.Task, completion);
            Assert.Empty(f.Clock.Calls);
            original.SetResult(1);
            AssertRejected(await f.DrainAsync(), f);
            AssertCleared(f.Stream.Calls[0].Array);
            Assert.Single(f.Stream.Calls);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task AnchorIsLocalObservationAfterOriginalExitNotConstructionOrStart()
    {
        Fixture f = new(budget: TimeSpan.FromMilliseconds(20));
        TaskCompletionSource<int> first = f.NewRead();
        f.Clock.Now = 100;
        f.ReadHook = (call, memory) =>
        {
            int count = f.CopyWire(memory);
            return call == 1 ? new ValueTask<int>(first.Task) : new ValueTask<int>(count);
        };
        try
        {
            f.Clock.Now = 1_000_000;
            Task<FrameOutcome> completion = f.Attempt.Start();
            await f.Stream.ReadAtAsync(1, completion);
            await AssertAwaitAsync(first.Task, f.First.Completion!);
            Assert.Empty(f.Clock.Calls);
            f.Clock.Now = 8_000_000;
            first.SetResult(1);
            AssertFrame(await f.DrainAsync(), f);
            Assert.Equal(TimeSpan.FromMilliseconds(20), Assert.Single(f.Timer.Changes));
            Assert.Equal(8_000_000, f.Clock.Samples[0]);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(3, 8)]
    public async Task FragmentedSuccessConsumesExactlyOneFrameAndTransfersOriginalArray(int prefixSlice, int payloadSlice)
    {
        Fixture f = new();
        f.ReadHook = (_, memory) => new ValueTask<int>(f.CopyWire(memory,
            f.Consumed == 0 ? 1 : f.Consumed < 4 ? prefixSlice : payloadSlice));
        try
        {
            Task<FrameOutcome> completion = f.Attempt.Start();
            FrameOutcome result = await f.DrainAsync();
            AssertFrame(result, f);
            Assert.Equal(4 + f.Payload.Length, f.Consumed);
            Assert.Equal(new byte[] { 0, 0, 0, 1, 0xDB }, f.Wire[f.Consumed..]);
            ReadCall[] calls = f.Stream.Calls;
            Assert.Equal(1, calls[0].Memory.Length);
            Assert.Equal(3, calls[1].Memory.Length);
            Assert.Equal(1, SegmentOf(calls[1].Memory).Offset);
            Assert.Equal(4, calls[1].Array.Length);
            Assert.All(calls, call => Assert.Same(completion, call.Registered));
            AssertCleared(calls[0].Array);
            AssertCleared(calls[1].Array);
            Assert.Same(calls[^1].Array, result.Payload);
            Assert.False(f.Attempt.AbortRequired.IsCompleted);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task DeliveredArraySurvivesStopAndRepeatedAwaitIsOnlyTheSameOwnershipAlias()
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
            // 同一 Task 的重复 await 返回同一结果/数组别名，绝不产生第二次所有权移交。
            FrameOutcome alias = await completion;
            Assert.Same(delivered, alias);
            Assert.Same(payload, alias.Payload);
            Assert.Equal((byte)0x5A, payload[0]);
            Assert.Equal(f.Payload[1..], payload[1..]);
            Assert.False(f.Attempt.AbortRequired.IsCompleted);
            Assert.Equal(AttemptEnd.Frame, f.Attempt.Snapshot.End);
            Assert.True(f.Attempt.Snapshot.Committed);
            Assert.True(f.Attempt.Snapshot.StopRequested);
            Assert.False(historical.StopRequested);
            Assert.Throws<InvalidOperationException>(() => { _ = f.Attempt.Start(); });
            Assert.Same(completion, f.Attempt.Completion);
            Assert.Equal(3, f.Stream.Calls.Length);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task StopDuringEachOriginalReadNotifiesBeforeJoinAndDoesNotClearBorrowedArray(int target)
    {
        Fixture f = new();
        TaskCompletionSource<int> original = f.NewRead();
        f.ReadHook = (call, memory) =>
        {
            int count = f.CopyWire(memory);
            return call == target ? new ValueTask<int>(original.Task) : new ValueTask<int>(count);
        };
        try
        {
            Task<FrameOutcome> completion = f.Attempt.Start();
            await f.Stream.ReadAtAsync(target, completion);
            Task child = target == 1 ? f.First.Completion! : f.Frame.Completion!;
            await AssertAwaitAsync(original.Task, child);
            ReadCall borrowed = f.Stream.Calls[^1];
            byte[] before = borrowed.Array.ToArray();
            await f.StopFromOtherThreadAsync();
            Assert.True(f.Attempt.AbortRequired.IsCompletedSuccessfully);
            await AssertAwaitAsync(original.Task, completion);
            Assert.False(original.Task.IsCompleted);
            Assert.False(completion.IsCompleted);
            Assert.Equal(before, borrowed.Array);
            Assert.Equal(target, f.Stream.Calls.Length);
            original.SetResult(borrowed.Memory.Length);
            AssertRejected(await f.DrainAsync(), f);
            Assert.All(f.Stream.Calls, call => AssertCleared(call.Array));
            Assert.Equal(target, f.Stream.Calls.Length);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task SynchronousContinuationPrefixDoesNotHoldGateAndLateOriginalStillMustExit(int target)
    {
        Fixture f = new();
        Pause prefix = f.NewPause();
        TaskCompletionSource<int> original = f.NewRead();
        f.ReadHook = (call, memory) =>
        {
            int count = f.CopyWire(memory);
            if (call != target) return new ValueTask<int>(count);
            prefix.Wait();
            return new ValueTask<int>(original.Task);
        };
        try
        {
            Task<FrameOutcome> completion = f.Attempt.Start();
            await ReachedBeforeCompletionAsync(prefix.Reached.Task, completion);
            ReadCall borrowed = f.Stream.Calls[^1];
            byte[] before = borrowed.Array.ToArray();
            Task<AttemptSnapshot> snapshot = f.Keep(Task.Run<AttemptSnapshot>(() => f.Attempt.Snapshot));
            Assert.False((await snapshot.WaitAsync(Guard)).Committed);
            await f.StopFromOtherThreadAsync();
            Assert.True(f.Attempt.AbortRequired.IsCompletedSuccessfully);
            Assert.False(completion.IsCompleted);
            Assert.Equal(before, borrowed.Array);
            prefix.Open();
            await AssertAwaitAsync(original.Task, completion);
            Assert.False(original.Task.IsCompleted);
            Assert.Equal(before, borrowed.Array);
            original.SetResult(borrowed.Memory.Length);
            AssertRejected(await f.DrainAsync(), f);
            Assert.All(f.Stream.Calls, call => AssertCleared(call.Array));
            Assert.Equal(target, f.Stream.Calls.Length);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task AbortNotificationDoesNotJoinPendingRead()
    {
        Fixture f = new(budget: TimeSpan.FromTicks(10));
        TaskCompletionSource<int> read = f.NewRead();
        TaskCompletionSource dispose = f.NewDispose();
        f.HoldRead(3, read);
        f.Timer.DisposeHook = () => new ValueTask(dispose.Task);
        try
        {
            Task<FrameOutcome> completion = f.Attempt.Start();
            await f.Stream.ReadAtAsync(3, completion);
            await AssertAwaitAsync(read.Task, f.Frame.Completion!);
            f.Clock.Now = 10;
            f.Timer.Fire();
            await AssertAwaitAsync(dispose.Task, f.Budget.Completion!);
            // 用未变异的 Budget 输入边确认 owner 进入 finally；不能靠待验证的 frame 边作前置。
            await AssertAwaitAsync(f.Budget.Completion!, completion);
            // 已看到 owner 的真实 join，直接检查通知；缺失通知不能靠 Guard 超时假杀。
            Assert.True(f.Attempt.AbortRequired.IsCompletedSuccessfully);
            Assert.Equal(BudgetEnd.Expired, f.Attempt.Snapshot.Budget!.End);
            Assert.False(read.Task.IsCompleted);
            Assert.False(dispose.Task.IsCompleted);
            Assert.False(completion.IsCompleted);
            Assert.Equal(f.Payload, f.Stream.Calls[2].Array);
            dispose.SetResult();
            await f.Budget.Completion!.WaitAsync(Guard);
            await AssertAwaitAsync(read.Task, completion);
            Assert.False(completion.IsCompleted);
            read.SetResult(f.Payload.Length);
            AssertRejected(await f.DrainAsync(), f);
            AssertCleared(f.Stream.Calls[2].Array);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task OriginalDisposeMustExitBeforeDelivery()
    {
        Fixture f = new();
        TaskCompletionSource dispose = f.NewDispose();
        f.Timer.DisposeHook = () => new ValueTask(dispose.Task);
        try
        {
            Task<FrameOutcome> completion = f.Attempt.Start();
            await ReachedBeforeCompletionAsync(f.Timer.DisposeReached.Task, completion);
            await AssertAwaitAsync(dispose.Task, f.Budget.Completion!);
            Assert.False(completion.IsCompleted);
            Assert.False(dispose.Task.IsCompleted);
            Assert.Equal(BudgetEnd.Pending, f.Attempt.Snapshot.Budget!.End);
            Assert.True(f.Attempt.Snapshot.Budget.FinishRequested);
            Assert.False(f.Attempt.Snapshot.Budget.TimerReleased);
            Assert.False(f.Attempt.AbortRequired.IsCompleted);
            Assert.Equal(f.Payload, f.Stream.Calls[2].Array);
            dispose.SetResult();
            AssertFrame(await f.DrainAsync(), f);
            Assert.True(f.Attempt.Snapshot.Budget.TimerReleased);
            Assert.True(dispose.Task.IsCompletedSuccessfully);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task RejectedOwnedPayloadIsClearedBeforePendingDisposeJoins()
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
            Assert.True(f.Attempt.Snapshot.Budget!.FinishRequested);
            byte[] owned = f.Stream.Calls[2].Array;
            Assert.Equal(f.Payload, owned);

            await f.StopFromOtherThreadAsync();
            Assert.True(f.Attempt.AbortRequired.IsCompletedSuccessfully);
            // Dispose 仍被闸住，Budget 不可能成功；此精确 await 链只能来自拒绝 finally 的 WhenAll。
            await AssertAwaitAsync(f.Budget.Completion!, completion);
            Assert.Equal(AttemptEnd.Rejected, f.Attempt.Snapshot.End);
            Assert.False(dispose.Task.IsCompleted);
            Assert.False(completion.IsCompleted);
            AssertCleared(owned); // 静止自有数组先清；不能被无关原释放的 join 无限拖延。

            dispose.SetResult();
            AssertRejected(await f.DrainAsync(), f);
            AssertCleared(owned);
            Assert.True(f.Attempt.Snapshot.Budget.TimerReleased);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopDuringDisposeNotifiesButWaitsForPrefixAndLateOriginalTask(bool blockPrefix)
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
            Assert.False(dispose.Task.IsCompleted);
            prefix.Open();
            await AssertAwaitAsync(dispose.Task, completion);
            dispose.SetResult();
            AssertRejected(await f.DrainAsync(), f);
            AssertCleared(f.Stream.Calls[2].Array);
            Assert.True(f.Attempt.Snapshot.Budget!.TimerReleased);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("expired")]
    [InlineData("frequency")]
    [InlineData("sample-error")]
    public async Task FinishRejectionNotifiesBeforePendingOriginalDisposeExits(string failure)
    {
        Fixture f = new(budget: TimeSpan.FromTicks(10));
        TaskCompletionSource dispose = f.NewDispose();
        IOException error = new("Finish 期间采样失败");
        f.Timer.DisposeHook = () => new ValueTask(dispose.Task);
        f.ReadHook = (call, memory) =>
        {
            int count = f.CopyWire(memory);
            if (call == 3)
            {
                if (failure == "expired") f.Clock.Now = 10;
                else if (failure == "frequency") f.Clock.Frequency = Frequency + 1;
                else f.Clock.ReadTimestamp = _ => throw error;
            }
            return new ValueTask<int>(count);
        };
        try
        {
            Task<FrameOutcome> completion = f.Attempt.Start();
            await ReachedBeforeCompletionAsync(f.Timer.DisposeReached.Task, completion);
            await AssertAwaitAsync(dispose.Task, f.Budget.Completion!);
            await AssertAwaitAsync(f.Budget.Completion!, completion);
            Assert.True(f.Attempt.AbortRequired.IsCompletedSuccessfully);
            Assert.True(f.Attempt.Snapshot.Budget!.FinishRequested);
            Assert.Equal(failure == "expired" ? BudgetEnd.Expired : BudgetEnd.Faulted, f.Attempt.Snapshot.Budget.End);
            if (failure == "sample-error") Assert.Same(error, f.Attempt.Snapshot.Budget.Error);
            Assert.False(completion.IsCompleted);
            Assert.False(dispose.Task.IsCompleted);
            dispose.SetResult();
            AssertRejected(await f.DrainAsync(), f);
            AssertCleared(f.Stream.Calls[2].Array);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task TimerReleaseTimeRemainsChargedToOriginalBudget()
    {
        Fixture f = new(budget: TimeSpan.FromTicks(10));
        TaskCompletionSource dispose = f.NewDispose();
        f.Timer.DisposeHook = () => new ValueTask(dispose.Task);
        try
        {
            Task<FrameOutcome> completion = f.Attempt.Start();
            await ReachedBeforeCompletionAsync(f.Timer.DisposeReached.Task, completion);
            await AssertAwaitAsync(dispose.Task, f.Budget.Completion!);
            f.Clock.Now = 10;
            dispose.SetResult();
            AssertRejected(await f.DrainAsync(), f);
            Assert.Equal(BudgetEnd.Expired, f.Attempt.Snapshot.Budget!.End);
            Assert.IsType<TimeoutException>(f.Attempt.Snapshot.Budget.Error);
            Assert.Null(f.Attempt.Snapshot.FinalSampleError);
            AssertCleared(f.Stream.Calls[2].Array);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task FinalAcceptanceRechecksOriginalBudget()
    {
        Fixture f = new(budget: TimeSpan.FromTicks(10));
        bool finalSampled = false;
        f.Clock.ReadTimestamp = _ =>
        {
            if (f.IsBudgetFinished) { finalSampled = true; return 10; }
            return 0;
        };
        try
        {
            _ = f.Attempt.Start();
            AssertRejected(await f.DrainAsync(), f);
            Assert.True(finalSampled);
            Assert.Equal(BudgetEnd.FinishedInBudget, f.Attempt.Snapshot.Budget!.End);
            TimeoutException error = Assert.IsType<TimeoutException>(f.Attempt.Snapshot.FinalSampleError);
            Assert.Equal("control-frame-final-budget-expired", error.Message);
            AssertCleared(f.Stream.Calls[2].Array);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task FinalSampleElapsedTimeIsNotRefundedAfterBudgetFinished()
    {
        Fixture f = new(budget: TimeSpan.FromTicks(10));
        Pause final = f.NewPause();
        f.Clock.ReadTimestamp = _ =>
        {
            if (f.IsBudgetFinished) final.Wait();
            return f.Clock.Now;
        };
        try
        {
            Task<FrameOutcome> completion = f.Attempt.Start();
            await ReachedBeforeCompletionAsync(final.Reached.Task, completion);
            Assert.Equal(BudgetEnd.FinishedInBudget, f.Attempt.Snapshot.Budget!.End);
            Assert.False(completion.IsCompleted);
            f.Clock.Now = 10;
            final.Open();
            AssertRejected(await f.DrainAsync(), f);
            Assert.IsType<TimeoutException>(f.Attempt.Snapshot.FinalSampleError);
            AssertCleared(f.Stream.Calls[2].Array);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task StopDuringFinalSamplePreventsDelivery()
    {
        Fixture f = new();
        bool reentered = false;
        f.Clock.ReadTimestamp = _ =>
        {
            if (f.IsBudgetFinished)
            {
                f.Attempt.RequestStop();
                reentered = true;
                Assert.True(f.Attempt.AbortRequired.IsCompletedSuccessfully);
                Assert.False(f.Attempt.Completion!.IsCompleted);
            }
            return f.Clock.Now;
        };
        try
        {
            _ = f.Attempt.Start();
            AssertRejected(await f.DrainAsync(), f);
            Assert.True(reentered);
            Assert.True(f.Attempt.Snapshot.StopRequested);
            Assert.Equal(BudgetEnd.FinishedInBudget, f.Attempt.Snapshot.Budget!.End);
            Assert.Null(f.Attempt.Snapshot.FinalSampleError);
            AssertCleared(f.Stream.Calls[2].Array);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FrequencyChangeBeforeOrAfterFinalTimestampRejectsFinishedBudget(bool afterTimestamp)
    {
        Fixture f = new();
        if (afterTimestamp)
            f.Clock.ReadTimestamp = _ => { if (f.IsBudgetFinished) f.Clock.Frequency++; return f.Clock.Now; };
        else
            f.Clock.ReadFrequency = _ => f.IsBudgetFinished ? Frequency + 1 : Frequency;
        try
        {
            _ = f.Attempt.Start();
            AssertRejected(await f.DrainAsync(), f);
            Assert.Equal(BudgetEnd.FinishedInBudget, f.Attempt.Snapshot.Budget!.End);
            InvalidOperationException error = Assert.IsType<InvalidOperationException>(f.Attempt.Snapshot.FinalSampleError);
            Assert.Equal("control-frame-final-frequency-changed", error.Message);
            AssertCleared(f.Stream.Calls[2].Array);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrossStageTimestampRegressionIsRejectedEvenAboveOriginalAnchor(bool atFinal)
    {
        Fixture f = new();
        f.Clock.ReadTimestamp = call => call == 1 ? 100 : f.IsBudgetFinished ? 104 : atFinal ? 105 : 99;
        try
        {
            _ = f.Attempt.Start();
            AssertRejected(await f.DrainAsync(), f);
            Exception? saved = atFinal ? f.Attempt.Snapshot.FinalSampleError : f.Attempt.Snapshot.Budget!.Error;
            InvalidOperationException error = Assert.IsType<InvalidOperationException>(saved);
            Assert.Equal("control-frame-observed-clock-regressed", error.Message);
            if (atFinal)
            {
                Assert.Equal(BudgetEnd.FinishedInBudget, f.Attempt.Snapshot.Budget!.End);
                AssertCleared(f.Stream.Calls[2].Array);
            }
            else Assert.Null(f.Attempt.Snapshot.Continuation);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("frequency")]
    [InlineData("timestamp")]
    [InlineData("changed-frequency")]
    public async Task ObservationFailureRejectsWithoutContinuationAndPreservesOriginalError(string point)
    {
        Fixture f = new();
        IOException error = new("本地观察失败");
        if (point == "frequency") f.Clock.ReadFrequency = _ => throw error;
        else if (point == "timestamp") f.Clock.ReadTimestamp = _ => throw error;
        else f.Clock.ReadTimestamp = _ => { f.Clock.Frequency++; return 0; };
        try
        {
            _ = f.Attempt.Start();
            AssertRejected(await f.DrainAsync(), f);
            if (point == "changed-frequency") Assert.IsType<InvalidOperationException>(f.Attempt.Snapshot.ObservationError);
            else Assert.Same(error, f.Attempt.Snapshot.ObservationError);
            Assert.Null(f.Attempt.Snapshot.Budget);
            Assert.Null(f.Attempt.Snapshot.Continuation);
            Assert.Equal(0, f.Clock.CreateCalls);
            Assert.Single(f.Stream.Calls);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task FinalSampleExceptionIsPreservedAfterBudgetSuccess()
    {
        Fixture f = new();
        AggregateException error = new("末检错误树", new IOException("原始错误"));
        f.Clock.ReadTimestamp = _ => f.IsBudgetFinished ? throw error : 0;
        try
        {
            _ = f.Attempt.Start();
            AssertRejected(await f.DrainAsync(), f);
            Assert.Same(error, f.Attempt.Snapshot.FinalSampleError);
            Assert.Equal(BudgetEnd.FinishedInBudget, f.Attempt.Snapshot.Budget!.End);
            AssertCleared(f.Stream.Calls[2].Array);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("observation-frequency")]
    [InlineData("observation-timestamp")]
    [InlineData("budget-timestamp")]
    [InlineData("create")]
    [InlineData("change")]
    [InlineData("dispose")]
    [InlineData("final-timestamp")]
    public async Task BlockedClockAndTimerPrefixesNeverHoldAttemptGate(string point)
    {
        Fixture f = new();
        Pause pause = f.NewPause();
        switch (point)
        {
            case "observation-frequency": f.Clock.ReadFrequency = _ => { pause.Wait(); return Frequency; }; break;
            case "observation-timestamp": f.Clock.ReadTimestamp = call => { if (call == 1) pause.Wait(); return 0; }; break;
            case "budget-timestamp": f.Clock.ReadTimestamp = call => { if (call == 2) pause.Wait(); return 0; }; break;
            case "create": f.Clock.CreateHook = pause.Wait; break;
            case "change": f.Timer.ChangeHook = pause.Wait; break;
            case "dispose": f.Timer.DisposeHook = () => { pause.Wait(); return ValueTask.CompletedTask; }; break;
            default: f.Clock.ReadTimestamp = _ => { if (f.IsBudgetFinished) pause.Wait(); return 0; }; break;
        }
        try
        {
            Task<FrameOutcome> completion = f.Attempt.Start();
            await ReachedBeforeCompletionAsync(pause.Reached.Task, completion);
            Task<AttemptSnapshot> snapshot = f.Keep(Task.Run<AttemptSnapshot>(() => f.Attempt.Snapshot));
            Assert.False((await snapshot.WaitAsync(Guard)).Committed);
            Task<Exception?> repeat = f.Keep(Task.Run<Exception?>(() => Record.Exception(() => { _ = f.Attempt.Start(); })));
            Assert.IsType<InvalidOperationException>(await repeat.WaitAsync(Guard));
            int calls = f.Clock.Calls.Length;
            await f.StopFromOtherThreadAsync();
            Assert.True(f.Attempt.AbortRequired.IsCompletedSuccessfully);
            Assert.False(completion.IsCompleted);
            Assert.Equal(calls, f.Clock.Calls.Length);
            pause.Open();
            AssertRejected(await f.DrainAsync(), f);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(1, "read")]
    [InlineData(1, "status")]
    [InlineData(1, "registration")]
    [InlineData(2, "read")]
    [InlineData(2, "status")]
    [InlineData(2, "registration")]
    [InlineData(3, "read")]
    [InlineData(3, "status")]
    [InlineData(3, "registration")]
    public async Task UnknownReadBorrowRemainsUntouchedAndDoesNotInventExitProof(int target, string point)
    {
        Fixture f = new();
        AggregateException error = new("未知借用前缀失败", new IOException("叶节点"));
        ThrowingReadSource source = new(error, point == "registration");
        byte[]? before = null;
        f.ReadHook = (call, memory) =>
        {
            if (call != target) return new ValueTask<int>(f.CopyWire(memory));
            memory.Span.Fill(Marker);
            before = ArrayOf(memory).ToArray();
            return point == "read" ? throw error : new ValueTask<int>(source, 0);
        };
        try
        {
            _ = f.Attempt.Start();
            AssertRejected(await f.DrainAsync(), f);
            Assert.Equal(target, f.Stream.Calls.Length);
            Assert.NotNull(before);
            Assert.Equal(before, f.Stream.Calls[^1].Array);
            if (target == 1)
            {
                Assert.Same(error, f.Attempt.Snapshot.FirstRead!.Error);
                Assert.Null(f.Attempt.Snapshot.Budget);
            }
            else
            {
                Assert.Same(error, f.Attempt.Snapshot.Continuation!.Error);
                Assert.True(f.Attempt.Snapshot.Continuation.BorrowExitUnproven);
                AssertCleared(f.Stream.Calls[0].Array);
                if (target == 3) AssertCleared(f.Stream.Calls[1].Array);
            }
            f.Attempt.RequestStop();
            Assert.Equal(before, f.Stream.Calls[^1].Array);
            Assert.Equal(point == "read" ? 0 : 1, source.StatusCalls);
            Assert.Equal(point == "registration" ? 1 : 0, source.RegistrationCalls);
            Assert.Equal(0, source.ResultCalls);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("first", false)]
    [InlineData("first", true)]
    [InlineData("continuation", false)]
    [InlineData("continuation", true)]
    [InlineData("dispose", false)]
    [InlineData("dispose", true)]
    public async Task OriginalFaultTreesKeepOrderDuplicatesAndLateErrorsAfterStop(string stage, bool stop)
    {
        Fixture f = new();
        TaskCompletionSource<int> read = f.NewRead();
        TaskCompletionSource dispose = f.NewDispose();
        IOException leaf = new("原始故障");
        OperationCanceledException cancellation = new("嵌套取消不是原任务取消");
        AggregateException branch = new("保留分支", cancellation, new InvalidOperationException("内部故障"));
        Exception[] roots = [leaf, branch, leaf];
        if (stage == "dispose") f.Timer.DisposeHook = () => new ValueTask(dispose.Task);
        else f.HoldRead(stage == "first" ? 1 : 3, read);
        try
        {
            Task<FrameOutcome> completion = f.Attempt.Start();
            if (stage == "dispose")
            {
                await ReachedBeforeCompletionAsync(f.Timer.DisposeReached.Task, completion);
                await AssertAwaitAsync(dispose.Task, f.Budget.Completion!);
            }
            else
            {
                await f.Stream.ReadAtAsync(stage == "first" ? 1 : 3, completion);
                await AssertAwaitAsync(read.Task, stage == "first" ? f.First.Completion! : f.Frame.Completion!);
            }
            if (stop)
            {
                await f.StopFromOtherThreadAsync();
                Assert.True(f.Attempt.AbortRequired.IsCompletedSuccessfully);
            }
            if (stage == "dispose") dispose.SetException(roots);
            else read.SetException(roots);
            AssertRejected(await f.DrainAsync(), f);
            Exception? saved = stage switch
            {
                "first" => f.Attempt.Snapshot.FirstRead!.Error,
                "continuation" => f.Attempt.Snapshot.Continuation!.Error,
                _ => f.Attempt.Snapshot.Budget!.CleanupError
            };
            AggregateException tree = Assert.IsType<AggregateException>(saved);
            Assert.Equal(roots.Length, tree.InnerExceptions.Count);
            for (int i = 0; i < roots.Length; i++) Assert.Same(roots[i], tree.InnerExceptions[i]);
            Assert.Same(cancellation, branch.InnerExceptions[0]);
            Assert.All(f.Stream.Calls, call => AssertCleared(call.Array));
            Assert.Equal(stop, f.Attempt.Snapshot.StopRequested);
            if (stage == "dispose") Assert.False(f.Attempt.Snapshot.Budget!.CleanupExitUnproven);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("first")]
    [InlineData("continuation")]
    [InlineData("dispose")]
    public async Task OriginalCancellationRemainsAnErrorWithOriginalTaskAndToken(string stage)
    {
        Fixture f = new();
        TaskCompletionSource<int> read = f.NewRead();
        TaskCompletionSource dispose = f.NewDispose();
        using CancellationTokenSource cancellation = new();
        if (stage == "dispose") f.Timer.DisposeHook = () => new ValueTask(dispose.Task);
        else f.HoldRead(stage == "first" ? 1 : 3, read);
        try
        {
            Task<FrameOutcome> completion = f.Attempt.Start();
            Task original;
            if (stage == "dispose")
            {
                original = dispose.Task;
                await ReachedBeforeCompletionAsync(f.Timer.DisposeReached.Task, completion);
                await AssertAwaitAsync(original, f.Budget.Completion!);
            }
            else
            {
                original = read.Task;
                await f.Stream.ReadAtAsync(stage == "first" ? 1 : 3, completion);
                await AssertAwaitAsync(original, stage == "first" ? f.First.Completion! : f.Frame.Completion!);
            }
            f.Attempt.RequestStop();
            cancellation.Cancel();
            if (stage == "dispose") dispose.SetCanceled(cancellation.Token);
            else read.SetCanceled(cancellation.Token);
            AssertRejected(await f.DrainAsync(), f);
            Exception? saved = stage switch
            {
                "first" => f.Attempt.Snapshot.FirstRead!.Error,
                "continuation" => f.Attempt.Snapshot.Continuation!.Error,
                _ => f.Attempt.Snapshot.Budget!.CleanupError
            };
            TaskCanceledException error = Assert.IsType<TaskCanceledException>(saved);
            Assert.Same(original, error.Task);
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.True(completion.IsCompletedSuccessfully);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("dispose")]
    [InlineData("status")]
    [InlineData("registration")]
    public async Task UnknownDisposeFailurePreventsDeliveryAndPreservesCleanupUncertainty(string point)
    {
        Fixture f = new();
        IOException error = new("无法取得原释放任务");
        ThrowingDisposeSource source = new(error, point == "registration");
        f.Timer.DisposeHook = () => point == "dispose" ? throw error : new ValueTask(source, 0);
        try
        {
            _ = f.Attempt.Start();
            AssertRejected(await f.DrainAsync(), f);
            Assert.Same(error, f.Attempt.Snapshot.Budget!.CleanupError);
            Assert.True(f.Attempt.Snapshot.Budget.CleanupExitUnproven);
            Assert.False(f.Attempt.Snapshot.Budget.TimerReleased);
            AssertCleared(f.Stream.Calls[2].Array);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ConcurrentStartHasExactlyOneOwnerAndOneFrame()
    {
        Fixture f = new();
        TaskCompletionSource release = f.NewSignal();
        TaskCompletionSource leftReady = f.NewSignal(), rightReady = f.NewSignal();
        Task<(Task<FrameOutcome>? Worker, Exception? Error)> left = f.Keep(StartAsync(leftReady));
        Task<(Task<FrameOutcome>? Worker, Exception? Error)> right = f.Keep(StartAsync(rightReady));
        try
        {
            await Task.WhenAll(leftReady.Task, rightReady.Task).WaitAsync(Guard);
            release.SetResult();
            var attempts = await Task.WhenAll(left, right).WaitAsync(Guard);
            var winner = Assert.Single(attempts, item => item.Error is null);
            var loser = Assert.Single(attempts, item => item.Error is not null);
            Assert.IsType<InvalidOperationException>(loser.Error);
            Assert.Null(loser.Worker);
            Assert.Same(winner.Worker, f.Attempt.Completion);
            AssertFrame(await f.DrainAsync(), f);
            Assert.Equal(3, f.Stream.Calls.Length);
            Assert.Equal(1, f.Clock.CreateCalls);
            Assert.Equal(1, f.Timer.DisposeCalls);
        }
        finally { await f.FinishAsync(); }

        Task<(Task<FrameOutcome>? Worker, Exception? Error)> StartAsync(TaskCompletionSource ready) =>
            Task.Run<(Task<FrameOutcome>? Worker, Exception? Error)>(async () =>
            {
                ready.SetResult();
                await release.Task.ConfigureAwait(false);
                Task<FrameOutcome>? worker = null;
                Exception? error = Record.Exception(() => { worker = f.Attempt.Start(); });
                return (worker, error);
            });
    }

    private static void AssertFrame(FrameOutcome result, Fixture f)
    {
        Assert.Equal(AttemptEnd.Frame, result.End);
        Assert.Equal(f.Payload, result.Payload);
        Assert.Equal(AttemptEnd.Frame, f.Attempt.Snapshot.End);
        Assert.True(f.Attempt.Snapshot.Committed);
        Assert.Equal(BudgetEnd.FinishedInBudget, f.Attempt.Snapshot.Budget!.End);
        Assert.True(f.Attempt.Snapshot.Budget.TimerReleased);
        Assert.Null(f.Attempt.Snapshot.ObservationError);
        Assert.Null(f.Attempt.Snapshot.FinalSampleError);
        Assert.Null(f.Attempt.Snapshot.CoordinationError);
    }

    private static void AssertRejected(FrameOutcome result, Fixture f)
    {
        Assert.Equal(AttemptEnd.Rejected, result.End);
        Assert.Null(result.Payload);
        Assert.Equal(AttemptEnd.Rejected, f.Attempt.Snapshot.End);
        Assert.False(f.Attempt.Snapshot.Committed);
        Assert.True(f.Attempt.AbortRequired.IsCompletedSuccessfully);
        Assert.Null(f.Attempt.Snapshot.CoordinationError);
    }

    private static async Task ReachedBeforeCompletionAsync(Task reached, Task completion)
    {
        await Task.WhenAny(reached, completion).WaitAsync(Guard);
        Assert.True(reached.IsCompletedSuccessfully, "未经过指定外部阶段，owner 就已退出。");
        Assert.False(completion.IsCompleted, "外部同步前缀未返回，owner 不得完成。");
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

    // 仅认可精确 awaiter、Task.Run unwrap 和实际注册的 WhenAll 输入边；WhenAny 是通知而非 join，故排除。
    // 证明链可从原读取/释放跨越子组件到 owner；未知运行时布局必须显式失败，不能略过证明。
    private static async Task AssertAwaitAsync(Task original, Task completion)
    {
        using CancellationTokenSource guard = new(Guard);
        while (!HasAwaitPath(original, completion, new HashSet<Task>()))
        {
            Assert.False(original.IsCompleted, "原任务提前退出，失去受闸等待证据。");
            Assert.False(completion.IsCompleted, "Completion 提前退出，未等待指定原任务。");
            if (guard.IsCancellationRequested)
                throw new XunitException("Guard：未观察到精确 await/unwrap/WhenAll 链，运行时布局不兼容不能跳过。");
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

    private sealed class Fixture
    {
        private readonly List<TaskCompletionSource<int>> _reads = [];
        private readonly List<TaskCompletionSource> _disposes = [];
        private readonly List<TaskCompletionSource> _signals = [];
        private readonly List<Pause> _pauses = [];
        private readonly List<Task> _tasks = [];
        internal byte[] Payload { get; } = [1, 0x32, 0, 0xFF, 5, 6, 7, 8];
        internal byte[] Wire { get; }
        internal int Consumed { get; private set; }
        internal Func<int, Memory<byte>, ValueTask<int>>? ReadHook { get; set; }
        internal ProbeStream Stream { get; }
        internal Clock Clock { get; }
        internal Timer Timer { get; }
        internal ControlFrameReadAttempt Attempt { get; }
        internal ControlReadAhead First => Child<ControlReadAhead>("_first");
        internal ControlFrameContinuation Frame => Child<ControlFrameContinuation>("_frame");
        internal ControlFrameBudget Budget => Child<ControlFrameBudget>("_deadline");
        internal bool IsBudgetFinished => Attempt.Snapshot.Budget?.End == BudgetEnd.FinishedInBudget;

        internal Fixture(TimeSpan? budget = null)
        {
            Wire = new byte[4 + Payload.Length + 5];
            BinaryPrimitives.WriteUInt32BigEndian(Wire, (uint)Payload.Length);
            Payload.CopyTo(Wire, 4);
            new byte[] { 0, 0, 0, 1, 0xDB }.CopyTo(Wire, 4 + Payload.Length);
            Stream = new ProbeStream(this);
            Timer = new Timer(this);
            Clock = new Clock(this);
            Attempt = new ControlFrameReadAttempt(Stream, Clock, budget ?? TimeSpan.FromSeconds(1), 64);
        }
        private T Child<T>(string name) where T : class =>
            Assert.IsType<T>(typeof(ControlFrameReadAttempt).GetField(name, Fields)!.GetValue(Attempt));
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
        internal TaskCompletionSource NewSignal()
        {
            TaskCompletionSource source = Signal();
            _signals.Add(source);
            return source;
        }
        internal Pause NewPause()
        {
            Pause pause = new();
            _pauses.Add(pause);
            return pause;
        }
        internal T Keep<T>(T task) where T : Task { _tasks.Add(task); return task; }
        internal async Task StopFromOtherThreadAsync()
        {
            Task stop = Keep(Task.Run(Attempt.RequestStop));
            await stop.WaitAsync(Guard);
        }
        internal async Task<FrameOutcome> DrainAsync()
        {
            Task<FrameOutcome> completion = Assert.IsAssignableFrom<Task<FrameOutcome>>(Attempt.Completion);
            FrameOutcome result = await completion.WaitAsync(Guard);
            Assert.True(completion.IsCompletedSuccessfully);
            Assert.Same(completion, Attempt.Completion);
            Assert.Null(completion.Exception);
            Stream.AssertNoOtherOperations();
            Assert.Equal(0, Timer.SyncDisposeCalls);
            Assert.DoesNotContain("wall-clock", Clock.Calls);
            if (Attempt.Snapshot.Budget is { TimerAcquired: true }) Assert.Equal(1, Timer.DisposeCalls);
            return result;
        }
        internal async Task FinishAsync()
        {
            // 失败也先放同步/启动闸，再 Stop，再放原任务；通知不代替任何原 Task 的最终 join。
            foreach (Pause pause in _pauses) pause.Open();
            foreach (TaskCompletionSource signal in _signals) signal.TrySetResult();
            Attempt.RequestStop();
            foreach (TaskCompletionSource<int> read in _reads) read.TrySetResult(0);
            foreach (TaskCompletionSource dispose in _disposes) dispose.TrySetResult();
            try
            {
                try { await Task.WhenAll(_tasks).WaitAsync(Guard); }
                finally
                {
                    if (Attempt.Completion is { } completion) await completion.WaitAsync(Guard);
                    // 变异可能破坏 owner 的 join；夹具仍独立等待每个已经创建的原子 worker。
                    if (Attempt.Snapshot.FirstRead is not null && First.Completion is { } first) await first.WaitAsync(Guard);
                    if (Attempt.Snapshot.Continuation is not null && Frame.Completion is { } frame) await frame.WaitAsync(Guard);
                    if (Attempt.Snapshot.Budget is not null && Budget.Completion is { } budget) await budget.WaitAsync(Guard);
                    foreach (Task original in _reads.Select(read => (Task)read.Task).Concat(_disposes.Select(dispose => dispose.Task)))
                    {
                        Assert.True(original.IsCompleted);
                        try { await original; }
                        catch { _ = original.Exception; }
                    }
                }
            }
            finally
            {
                foreach (Pause pause in _pauses) pause.Dispose();
                Stream.AssertNoOtherOperations();
            }
        }
    }

    private sealed class Pause : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        internal TaskCompletionSource Reached { get; } = Signal();
        internal void Wait()
        {
            Reached.TrySetResult();
            if (!_release.Wait(Guard)) throw new XunitException("Guard：同步闸未放行。");
        }
        internal void Open() => _release.Set();
        public void Dispose() => _release.Dispose();
    }

    private sealed class Clock(Fixture f) : TimeProvider
    {
        private readonly ConcurrentQueue<string> _calls = new();
        private readonly ConcurrentQueue<long> _samples = new();
        private long _now, _frequency = ControlFrameReadAttemptTests.Frequency;
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
                int call = Interlocked.Increment(ref _frequencies);
                return ReadFrequency?.Invoke(call) ?? Frequency;
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
            throw new XunitException("单次 owner 不应读取墙钟。");
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _calls.Enqueue("create");
            Interlocked.Increment(ref _creates);
            Assert.Equal(Timeout.InfiniteTimeSpan, dueTime);
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
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
        internal Action? ChangeHook { get; set; }
        internal Func<ValueTask>? DisposeHook { get; set; }
        internal TimeSpan[] Changes => _changes.ToArray();
        internal int DisposeCalls => Volatile.Read(ref _disposes);
        internal int SyncDisposeCalls => Volatile.Read(ref _syncDisposes);
        internal void Bind(TimerCallback callback, object? state) { _callback = callback; _state = state; }
        internal void Fire() => Assert.IsType<TimerCallback>(_callback)(_state);
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            f.Clock.Record("change");
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
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
            throw new XunitException("不能用同步 Dispose 代替原 DisposeAsync。");
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
            Assert.True(_entered[call - 1].Task.IsCompletedSuccessfully, "owner 未进入指定原读取就退出。");
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _readCount);
            if (call > _entered.Length) throw Unexpected("超出有界测试帧的读取次数");
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
            return new XunitException($"借用流不应执行 {operation}。");
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

    private sealed class ThrowingReadSource(Exception error, bool registration) : IValueTaskSource<int>
    {
        internal int StatusCalls { get; private set; }
        internal int RegistrationCalls { get; private set; }
        internal int ResultCalls { get; private set; }
        public ValueTaskSourceStatus GetStatus(short token)
        {
            StatusCalls++;
            return registration ? ValueTaskSourceStatus.Pending : throw error;
        }
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            RegistrationCalls++;
            throw error;
        }
        public int GetResult(short token)
        {
            ResultCalls++;
            throw new XunitException("未取得原读取 Task，不应取得结果。");
        }
    }

    private sealed class ThrowingDisposeSource(Exception error, bool registration) : IValueTaskSource
    {
        public ValueTaskSourceStatus GetStatus(short token) => registration ? ValueTaskSourceStatus.Pending : throw error;
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => throw error;
        public void GetResult(short token) => throw new XunitException("未取得原释放 Task，不应取得结果。");
    }
}
