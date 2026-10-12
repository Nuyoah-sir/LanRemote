using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks.Sources;
using Xunit.Sdk;
using ReadEnd = LanRemote.Transport.ControlFrameContinuation.ReadEnd;
using ReadSnapshot = LanRemote.Transport.ControlFrameContinuation.ReadSnapshot;

namespace LanRemote.Transport.Tests;

public sealed class ControlFrameContinuationTests
{
    // 仅用于让失灵测试退出；时序证据来自闸、原 Task 的 await 链和已完成的调用。
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private const int TestTimeout = 60_000;
    private const byte Marker = 0xA5;
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact(Timeout = TestTimeout)]
    public async Task Constructor_DoesNotAccessStream_AndSnapshotsAreIndependent()
    {
        FrameFixture f = new();
        try
        {
            Assert.Equal("stream", Assert.Throws<ArgumentNullException>(
                () => new ControlFrameContinuation(null!, 0, 64)).ParamName);
            Assert.Null(f.Frame.Completion);
            ReadSnapshot initial = f.Frame.Snapshot;
            AssertSnapshot(initial, ReadEnd.Pending);
            Assert.NotSame(initial, f.Frame.Snapshot);
            AssertNoTake(f.Frame);
            Assert.Empty(f.Stream.Calls);
            f.Stream.AssertNoOtherOperations();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1024 * 1024 + 1)]
    [InlineData(int.MaxValue)]
    public async Task Constructor_RejectsInvalidMaximum_WithoutAnyIo(int maximum)
    {
        FrameFixture f = new();
        try
        {
            Assert.Equal("maxBytes", Assert.Throws<ArgumentOutOfRangeException>(
                () => new ControlFrameContinuation(f.Stream, 0, maximum)).ParamName);
            Assert.Empty(f.Stream.Calls);
            f.Stream.AssertNoOtherOperations();
        }
        finally { await f.FinishAsync(); }
    }

    public static IEnumerable<object[]> FragmentSizes()
    {
        for (int prefix = 1; prefix <= 3; prefix++)
            for (int payload = 1; payload <= 3; payload++)
                yield return [prefix, payload];
    }

    [Theory(Timeout = TestTimeout)]
    [MemberData(nameof(FragmentSizes))]
    public async Task Fragments_ReadOnlyRemainingPrefixAndPayload_AndTransferOriginalArrayOnce(
        int prefixFragment, int payloadFragment)
    {
        FrameFixture f = new(read: (fixture, _, memory) =>
        {
            bool prefix = ReferenceEquals(ArrayOf(memory), fixture.Stream.Calls[0].Array);
            int count = Math.Min(memory.Length, prefix ? prefixFragment : payloadFragment);
            if (prefix) fixture.CopyPrefix(memory, count);
            else
            {
                int offset = SegmentOf(memory).Offset;
                for (int i = 0; i < count; i++) memory.Span[i] = (byte)(offset + i + 1);
            }
            return new ValueTask<int>(count);
        });
        try
        {
            ReadSnapshot initial = f.Frame.Snapshot;
            Task completion = f.Frame.Start();
            await AssertDrainedAsync(f.Frame);
            ReadCall[] calls = f.Stream.Calls;
            ReadCall[] prefixCalls = calls.Where(call => ReferenceEquals(call.Array, calls[0].Array)).ToArray();
            ReadCall[] payloadCalls = calls.Where(call => !ReferenceEquals(call.Array, calls[0].Array)).ToArray();
            Assert.Equal(1, SegmentOf(prefixCalls[0].Memory).Offset);
            Assert.Equal(3, prefixCalls[0].Memory.Length);
            Assert.Equal(4, prefixCalls[0].Array.Length);
            AssertRequests(prefixCalls, 1, 4, prefixFragment);
            AssertRequests(payloadCalls, 0, 8, payloadFragment);
            Assert.All(calls, call => Assert.Same(completion, call.Registered));
            Assert.True(calls[0].OnThreadPool);
            Assert.NotNull(calls[0].WorkerTaskId);
            Assert.Same(TaskScheduler.Default, calls[0].Scheduler);
            AssertCleared(prefixCalls[0].Array);
            ReadSnapshot available = f.Frame.Snapshot;
            AssertSnapshot(available, ReadEnd.Frame);
            Assert.True(f.Frame.TryTake(out byte[]? result));
            Assert.NotNull(result);
            Assert.Same(payloadCalls[0].Array, result);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, result);
            AssertNoTake(f.Frame);
            ReadSnapshot taken = f.Frame.Snapshot;
            result[0] = 0x5A;
            f.Frame.RequestStop();
            Assert.Equal((byte)0x5A, result[0]);
            Assert.Equal(new byte[] { 0x5A, 2, 3, 4, 5, 6, 7, 8 }, result);
            AssertNoTake(f.Frame);
            AssertSnapshot(initial, ReadEnd.Pending);
            AssertSnapshot(available, ReadEnd.Frame);
            AssertSnapshot(taken, ReadEnd.Frame, taken: true);
            AssertSnapshot(f.Frame.Snapshot, ReadEnd.Frame, stop: true, taken: true);
            Assert.Throws<InvalidOperationException>(() => { _ = f.Frame.Start(); });
            Assert.Same(completion, f.Frame.Completion);
            f.Stream.AssertNoOtherOperations();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(256 * 1024)]
    [InlineData(1024 * 1024)]
    public async Task ValidLengthAtMaximum_IsAcceptedWithoutReadingPastPayload(int length)
    {
        FrameFixture f = new((uint)length, length);
        try
        {
            _ = f.Frame.Start();
            await AssertDrainedAsync(f.Frame);
            AssertSnapshot(f.Frame.Snapshot, ReadEnd.Frame);
            Assert.Equal(2, f.Stream.Calls.Length);
            Assert.Equal(3, f.Stream.Calls[0].Memory.Length);
            Assert.Equal(length, f.Stream.Calls[1].Memory.Length);
            Assert.True(f.Frame.TryTake(out byte[]? result));
            Assert.NotNull(result);
            Assert.Equal(length, result.Length);
            Assert.Same(f.Stream.Calls[1].Array, result);
            Assert.Equal(Enumerable.Repeat(Marker, length).ToArray(), result);
            AssertCleared(f.Stream.Calls[0].Array);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(0U, 64, FrameReader.RejectZeroLength)]
    [InlineData(65U, 64, FrameReader.RejectTooLarge)]
    [InlineData(262145U, 262144, FrameReader.RejectTooLarge)]
    [InlineData(1048577U, 1048576, FrameReader.RejectTooLarge)]
    [InlineData(0x7FFFFFFFU, 1048576, FrameReader.RejectTooLarge)]
    [InlineData(0x80000000U, 1048576, FrameReader.RejectTooLarge)]
    [InlineData(0xFFFFFFFFU, 1048576, FrameReader.RejectTooLarge)]
    public async Task RejectedUnsignedLength_DoesNotReadPayload(uint length, int maximum, string reason)
    {
        FrameFixture f = new(length, maximum);
        try
        {
            _ = f.Frame.Start();
            await AssertDrainedAsync(f.Frame);
            FrameProtocolException error = Assert.IsType<FrameProtocolException>(f.Frame.Snapshot.Error);
            Assert.Equal(reason, error.Reason);
            AssertSnapshot(f.Frame.Snapshot, ReadEnd.Faulted, error: error);
            ReadCall call = Assert.Single(f.Stream.Calls);
            Assert.Equal(3, call.Memory.Length);
            Assert.Equal(4, call.Array.Length);
            AssertCleared(call.Array);
            AssertNoTake(f.Frame);
        }
        finally { await f.FinishAsync(); }
    }

    // 独立小额度用例供限长变异定向运行，避免对巨量声明长度放宽上限后分配。
    [Fact(Timeout = TestTimeout)]
    public async Task TinyConfiguredLimitRejects65BytesBeforePayload()
    {
        FrameFixture f = new(65U, 64);
        try
        {
            _ = f.Frame.Start();
            await AssertDrainedAsync(f.Frame);
            ReadCall call = Assert.Single(f.Stream.Calls);
            Assert.Equal(3, call.Memory.Length);
            Assert.Equal(4, call.Array.Length);
            AssertCleared(call.Array);
            FrameProtocolException error = Assert.IsType<FrameProtocolException>(f.Frame.Snapshot.Error);
            Assert.Equal(FrameReader.RejectTooLarge, error.Reason);
            AssertSnapshot(f.Frame.Snapshot, ReadEnd.Faulted, error: error);
            AssertNoTake(f.Frame);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ActualReadAheadAndContinuation_ConsumeTwoCoalescedFramesWithoutLosingZeroFirstByte()
    {
        byte[] firstPayload = [0, 0x61, 0xFF, 0x32, 0x70];
        byte[] secondPayload = [0x47, 0, 0x29];
        byte[] firstWire = WireFrame(firstPayload), secondWire = WireFrame(secondPayload);
        byte[] wire = [.. firstWire, .. secondWire, 0xDB];
        int consumed = 0;
        ProbeStream stream = new((_, memory) =>
        {
            int count = Math.Min(memory.Length, wire.Length - consumed);
            wire.AsMemory(consumed, count).CopyTo(memory);
            consumed += count;
            return new ValueTask<int>(count);
        });
        List<ControlReadAhead> aheads = [];
        List<ControlFrameContinuation> frames = [];
        try
        {
            foreach (byte[] expected in new[] { firstPayload, secondPayload })
            {
                int before = consumed;
                ControlReadAhead ahead = new(stream);
                aheads.Add(ahead);
                await ahead.Start().WaitAsync(Guard);
                Assert.True(ahead.TryTake(out ControlReadAhead.ReadResult? held));
                Assert.NotNull(held);
                Assert.Equal((byte?)0, held.FirstByte);
                Assert.Equal(before + 1, consumed);
                Assert.Equal(ControlReadAhead.ReadEnd.Byte, ahead.Snapshot.End);
                Assert.Null(ahead.Snapshot.Error);
                ControlFrameContinuation frame = new(stream, held.FirstByte!.Value, 64);
                frames.Add(frame);
                _ = frame.Start();
                await AssertDrainedAsync(frame);
                AssertSnapshot(frame.Snapshot, ReadEnd.Frame);
                Assert.True(frame.TryTake(out byte[]? result));
                Assert.Equal(expected, result);
                AssertNoTake(frame);
                Assert.Equal(before + 4 + expected.Length, consumed);
                if (frames.Count == 1)
                    Assert.Equal((byte[])[.. secondWire, 0xDB], wire[consumed..]);
            }
            Assert.Equal(new[] { 1, 3, firstPayload.Length, 1, 3, secondPayload.Length },
                stream.Calls.Select(call => call.Memory.Length).ToArray());
            Assert.Equal(new byte[] { 0xDB }, wire[consumed..]);
            stream.AssertNoOtherOperations();
        }
        finally
        {
            foreach (ControlReadAhead ahead in aheads) ahead.RequestStop();
            foreach (ControlFrameContinuation frame in frames) frame.RequestStop();
            try
            {
                await Task.WhenAll(aheads.Select(ahead => ahead.Completion).Concat(frames.Select(frame => frame.Completion))
                    .OfType<Task>()).WaitAsync(Guard);
            }
            finally
            {
                stream.ReleaseRegistrations();
                stream.AssertNoOtherOperations();
            }
        }
    }

    public static IEnumerable<object[]> BrokenReadPositions()
    {
        foreach (bool payload in new[] { false, true })
            foreach (int previous in payload ? new[] { 0, 1, 7 } : new[] { 0, 1, 2 })
                yield return [payload, previous];
    }

    [Theory(Timeout = TestTimeout)]
    [MemberData(nameof(BrokenReadPositions))]
    public async Task TruncatedPrefixOrPayload_ClearsEntireFinishedArrays(bool payload, int previous)
    {
        int targetCall = (payload ? 2 : 1) + (previous == 0 ? 0 : 1);
        FrameFixture f = new(read: (fixture, call, memory) =>
        {
            if (call == targetCall)
            {
                memory.Span.Fill(Marker);
                return new ValueTask<int>(0);
            }
            if (call == 1)
            {
                int count = payload ? 3 : previous;
                fixture.CopyPrefix(memory, count);
                return new ValueTask<int>(count);
            }
            memory.Span[..previous].Fill(Marker);
            return new ValueTask<int>(previous);
        });
        try
        {
            _ = f.Frame.Start();
            await AssertDrainedAsync(f.Frame);
            EndOfStreamException error = Assert.IsType<EndOfStreamException>(f.Frame.Snapshot.Error);
            Assert.Equal(payload ? "control-payload-truncated" : "control-prefix-truncated", error.Message);
            AssertSnapshot(f.Frame.Snapshot, ReadEnd.Faulted, error: error);
            Assert.Equal(targetCall, f.Stream.Calls.Length);
            Assert.All(f.Stream.Calls, call => AssertCleared(call.Array));
            AssertNoTake(f.Frame);
        }
        finally { await f.FinishAsync(); }
    }

    public static IEnumerable<object[]> InvalidCounts()
    {
        foreach (object[] position in BrokenReadPositions())
            foreach (bool oversized in new[] { false, true })
                yield return [position[0], position[1], oversized];
    }

    [Theory(Timeout = TestTimeout)]
    [MemberData(nameof(InvalidCounts))]
    public async Task NegativeOrBeyondRemainingCount_IsRejectedEvenAfterSuccessfulFragment(
        bool payload, int previous, bool oversized)
    {
        int targetCall = (payload ? 2 : 1) + (previous == 0 ? 0 : 1);
        FrameFixture f = new(read: (fixture, call, memory) =>
        {
            if (call == targetCall)
            {
                memory.Span.Fill(Marker);
                return new ValueTask<int>(oversized ? memory.Length + 1 : -1);
            }
            if (call == 1)
            {
                int count = payload ? 3 : previous;
                fixture.CopyPrefix(memory, count);
                return new ValueTask<int>(count);
            }
            memory.Span[..previous].Fill(Marker);
            return new ValueTask<int>(previous);
        });
        try
        {
            _ = f.Frame.Start();
            await AssertDrainedAsync(f.Frame);
            FrameProtocolException error = Assert.IsType<FrameProtocolException>(f.Frame.Snapshot.Error);
            Assert.Equal("control-read-invalid-count", error.Reason);
            AssertSnapshot(f.Frame.Snapshot, ReadEnd.Faulted, error: error);
            Assert.Equal(targetCall, f.Stream.Calls.Length);
            Assert.Equal((payload ? 8 : 3) - previous, f.Stream.Calls[^1].Memory.Length);
            Assert.All(f.Stream.Calls, call => AssertCleared(call.Array));
            AssertNoTake(f.Frame);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task StopBeforeStart_IsIdempotentAndSkipsEveryStreamOperation()
    {
        FrameFixture f = new();
        try
        {
            f.Frame.RequestStop();
            f.Frame.RequestStop();
            ReadSnapshot stopped = f.Frame.Snapshot;
            AssertSnapshot(stopped, ReadEnd.Pending, stop: true);
            Assert.Null(f.Frame.Completion);
            AssertNoTake(f.Frame);
            _ = f.Frame.Start();
            await AssertDrainedAsync(f.Frame);
            AssertSnapshot(f.Frame.Snapshot, ReadEnd.Stopped, stop: true);
            AssertSnapshot(stopped, ReadEnd.Pending, stop: true);
            Assert.Empty(f.Stream.Calls);
            AssertNoTake(f.Frame);
            Assert.Throws<InvalidOperationException>(() => { _ = f.Frame.Start(); });
        }
        finally { await f.FinishAsync(); }
    }

    public static IEnumerable<object[]> StopPositions()
    {
        foreach (object[] position in BrokenReadPositions())
            foreach (bool finishSlice in new[] { false, true })
                yield return [position[0], position[1], finishSlice];
    }

    [Theory(Timeout = TestTimeout)]
    [MemberData(nameof(StopPositions))]
    public async Task StopDuringOriginalRead_DoesNotClearCancelOrCompleteUntilExit_AndDoesNotContinue(
        bool payload, int previous, bool finishSlice)
    {
        TaskCompletionSource<int>? original = null;
        int targetCall = (payload ? 2 : 1) + (previous == 0 ? 0 : 1);
        FrameFixture f = new(read: (fixture, call, memory) =>
        {
            if (call == targetCall)
            {
                if (payload) memory.Span.Fill(Marker);
                else fixture.CopyPrefix(memory, memory.Length);
                return new ValueTask<int>(original!.Task);
            }
            if (call == 1)
            {
                int count = payload ? 3 : previous;
                fixture.CopyPrefix(memory, count);
                return new ValueTask<int>(count);
            }
            memory.Span[..previous].Fill(Marker);
            return new ValueTask<int>(previous);
        });
        original = f.NewOriginal();
        try
        {
            Task completion = f.Frame.Start();
            await AssertAwaitPathAsync(original.Task, completion);
            ReadCall[] calls = f.Stream.Calls;
            byte[][] before = calls.Select(call => call.Array.ToArray()).ToArray();
            ReadSnapshot pending = f.Frame.Snapshot;
            AssertSnapshot(pending, ReadEnd.Pending);
            AssertNoTake(f.Frame);
            f.Frame.RequestStop();
            f.Frame.RequestStop();
            AssertSnapshot(f.Frame.Snapshot, ReadEnd.Pending, stop: true);
            Assert.False(original.Task.IsCompleted);
            Assert.False(completion.IsCompleted);
            Assert.Same(completion, f.Frame.Completion);
            for (int i = 0; i < calls.Length; i++) Assert.Equal(before[i], calls[i].Array);
            AssertNoTake(f.Frame);
            f.Stream.AssertNoOtherOperations();
            await AssertAwaitPathAsync(original.Task, completion);
            original.SetResult(finishSlice ? calls[^1].Memory.Length : 1);
            await AssertDrainedAsync(f.Frame);
            AssertSnapshot(f.Frame.Snapshot, ReadEnd.Stopped, stop: true);
            AssertSnapshot(pending, ReadEnd.Pending);
            Assert.True(original.Task.IsCompletedSuccessfully);
            Assert.Equal(targetCall, f.Stream.Calls.Length);
            Assert.All(calls, call => AssertCleared(call.Array));
            AssertNoTake(f.Frame);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task FullPayloadStillBorrowed_IsNotTakeableUntilOriginalTaskAndWorkerExit()
    {
        TaskCompletionSource<int>? original = null;
        FrameFixture f = new(read: (fixture, call, memory) =>
        {
            if (call == 1)
            {
                fixture.CopyPrefix(memory, 3);
                return new ValueTask<int>(3);
            }
            memory.Span.Fill(Marker);
            return new ValueTask<int>(original!.Task);
        });
        original = f.NewOriginal();
        try
        {
            Task completion = f.Frame.Start();
            await AssertAwaitPathAsync(original.Task, completion);
            byte[] payload = f.Stream.Calls[1].Array;
            Assert.Equal(Enumerable.Repeat(Marker, 8).ToArray(), payload);
            AssertNoTake(f.Frame);
            AssertSnapshot(f.Frame.Snapshot, ReadEnd.Pending);
            Assert.False(completion.IsCompleted);
            original.SetResult(8);
            await AssertDrainedAsync(f.Frame);
            Assert.True(f.Frame.TryTake(out byte[]? result));
            Assert.Same(payload, result);
            AssertSnapshot(f.Frame.Snapshot, ReadEnd.Frame, taken: true);
            AssertNoTake(f.Frame);
            f.Frame.RequestStop();
            Assert.Equal(Enumerable.Repeat(Marker, 8).ToArray(), payload);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task StopAfterSuccessfulWorker_ClearsUntakenPayloadButPreservesHistoricalSnapshot()
    {
        FrameFixture f = new();
        try
        {
            _ = f.Frame.Start();
            await AssertDrainedAsync(f.Frame);
            ReadSnapshot available = f.Frame.Snapshot;
            byte[] payload = f.Stream.Calls[1].Array;
            Assert.Equal(Enumerable.Repeat(Marker, 8).ToArray(), payload);
            f.Frame.RequestStop();
            f.Frame.RequestStop();
            AssertCleared(payload);
            AssertSnapshot(available, ReadEnd.Frame);
            AssertSnapshot(f.Frame.Snapshot, ReadEnd.Frame, stop: true);
            AssertNoTake(f.Frame);
        }
        finally { await f.FinishAsync(); }
    }

    public static IEnumerable<object[]> OriginalFaults()
    {
        foreach (string shape in new[] { "single", "nested", "siblings", "repeated", "disposed" })
            foreach (bool payload in new[] { false, true })
                foreach (bool stop in new[] { false, true })
                    yield return [shape, payload, stop];
    }

    [Theory(Timeout = TestTimeout)]
    [MemberData(nameof(OriginalFaults))]
    public async Task OriginalTaskFaults_PreserveAllRootReferencesAndNesting_EvenAfterStop(
        string shape, bool payload, bool stop)
    {
        IOException leaf = new("首项失败");
        OperationCanceledException cancellation = new("嵌套取消并非原任务取消");
        InvalidOperationException nestedLeaf = new("嵌套失败");
        AggregateException nested = new("嵌套分支", cancellation, nestedLeaf);
        IOException sibling = new("同级失败");
        ObjectDisposedException disposed = new("borrowed-stream", "原读迟到故障不能被 Stop 吞掉");
        Exception[] roots = shape switch
        {
            "single" => [leaf],
            "nested" => [nested],
            "repeated" => [leaf, nested, leaf],
            "disposed" => [disposed],
            _ => [leaf, nested, sibling]
        };
        TaskCompletionSource<int>? original = null;
        int targetCall = payload ? 3 : 1;
        FrameFixture f = new(read: (fixture, call, memory) =>
        {
            if (call == targetCall)
            {
                memory.Span.Fill(Marker);
                return new ValueTask<int>(original!.Task);
            }
            if (call == 1)
            {
                fixture.CopyPrefix(memory, 3);
                return new ValueTask<int>(3);
            }
            memory.Span[..3].Fill(Marker);
            return new ValueTask<int>(3);
        });
        original = f.NewOriginal();
        try
        {
            Task completion = f.Frame.Start();
            await AssertAwaitPathAsync(original.Task, completion);
            ReadSnapshot pending = f.Frame.Snapshot;
            if (stop) f.Frame.RequestStop();
            Assert.False(completion.IsCompleted);
            original.SetException(roots);
            await AssertDrainedAsync(f.Frame);
            Assert.True(original.Task.IsFaulted);
            Exception saved = Assert.IsAssignableFrom<Exception>(f.Frame.Snapshot.Error);
            if (roots.Length == 1) Assert.Same(roots[0], saved);
            else
            {
                AggregateException tree = Assert.IsType<AggregateException>(saved);
                AggregateException originalTree = Assert.IsType<AggregateException>(original.Task.Exception);
                Assert.Equal(roots.Length, tree.InnerExceptions.Count);
                for (int i = 0; i < roots.Length; i++)
                {
                    Assert.Same(roots[i], originalTree.InnerExceptions[i]);
                    Assert.Same(roots[i], tree.InnerExceptions[i]);
                }
                Assert.Same(nested, tree.InnerExceptions[1]);
            }
            Assert.Equal(2, nested.InnerExceptions.Count);
            Assert.Same(cancellation, nested.InnerExceptions[0]);
            Assert.Same(nestedLeaf, nested.InnerExceptions[1]);
            AssertSnapshot(f.Frame.Snapshot, ReadEnd.Faulted, stop: stop, error: saved);
            AssertSnapshot(pending, ReadEnd.Pending);
            Assert.Equal(targetCall, f.Stream.Calls.Length);
            Assert.All(f.Stream.Calls, call => AssertCleared(call.Array));
            AssertNoTake(f.Frame);
            f.Frame.RequestStop();
            AssertSnapshot(f.Frame.Snapshot, ReadEnd.Faulted, stop: true, error: saved);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledOriginalTask_IsRetainedAsErrorWithoutCancelingCompletion(bool stop)
    {
        TaskCompletionSource<int>? original = null;
        FrameFixture f = new(read: (_, _, memory) =>
        {
            memory.Span.Fill(Marker);
            return new ValueTask<int>(original!.Task);
        });
        original = f.NewOriginal();
        using CancellationTokenSource source = new();
        try
        {
            Task completion = f.Frame.Start();
            await AssertAwaitPathAsync(original.Task, completion);
            if (stop) f.Frame.RequestStop();
            source.Cancel();
            original.SetCanceled(source.Token);
            await AssertDrainedAsync(f.Frame);
            Assert.True(original.Task.IsCanceled);
            TaskCanceledException error = Assert.IsType<TaskCanceledException>(f.Frame.Snapshot.Error);
            Assert.Equal(source.Token, error.CancellationToken);
            Assert.Same(original.Task, error.Task);
            AssertSnapshot(f.Frame.Snapshot, ReadEnd.Faulted, stop: stop, error: error);
            AssertCleared(Assert.Single(f.Stream.Calls).Array);
            AssertNoTake(f.Frame);
        }
        finally { await f.FinishAsync(); }
    }

    public static IEnumerable<object[]> UnknownBorrows()
    {
        foreach (bool payload in new[] { false, true })
            for (int previousFragments = 0; previousFragments <= 2; previousFragments++)
                foreach (string failure in new[] { "read", "status", "registration" })
                    yield return [payload, previousFragments, failure];
    }

    [Theory(Timeout = TestTimeout)]
    [MemberData(nameof(UnknownBorrows))]
    public async Task ReadOrAsTaskFailure_WithoutNewOriginalTask_DoesNotReusePreviousTaskToClearCurrentArray(
        bool payload, int previousFragments, string failure)
    {
        AggregateException error = new("同步失败", new IOException("首项"),
            new AggregateException(new InvalidOperationException("保留嵌套")));
        ThrowingSource source = new(error, failure == "registration");
        byte[]? beforeFailure = null;
        int targetCall = (payload ? 2 : 1) + previousFragments;
        // 前缀失败时使用非零已持有/已读字节，整块保护不能只靠尚未读取的切片证明。
        FrameFixture f = new(payload ? 8U : 0x11223308U, read: (fixture, call, memory) =>
        {
            if (call == targetCall)
            {
                memory.Span.Fill(Marker);
                beforeFailure = ArrayOf(memory).ToArray();
                return failure == "read" ? throw error : new ValueTask<int>(source, 0);
            }
            if (!payload || call == 1)
            {
                int count = payload ? 3 : 1;
                fixture.CopyPrefix(memory, count);
                return new ValueTask<int>(count);
            }
            memory.Span[..1].Fill(Marker);
            return new ValueTask<int>(1);
        });
        try
        {
            Task completion = f.Frame.Start();
            await AssertDrainedAsync(f.Frame);
            Assert.Equal(targetCall, f.Stream.Calls.Length);
            ReadCall failed = f.Stream.Calls[^1];
            Assert.Same(completion, failed.Registered);
            AssertSnapshot(f.Frame.Snapshot, ReadEnd.Faulted, unknown: true, error: error);
            Assert.NotNull(beforeFailure);
            Assert.Equal(beforeFailure, failed.Array);
            Assert.Equal((payload ? 0 : 1) + previousFragments, SegmentOf(failed.Memory).Offset);
            if (payload) AssertCleared(f.Stream.Calls[0].Array);
            else Assert.Same(f.Stream.Calls[0].Array, failed.Array);
            Assert.Equal(failure == "read" ? 0 : 1, source.StatusCalls);
            Assert.Equal(failure == "registration" ? 1 : 0, source.OnCompletedCalls);
            Assert.Equal(0, source.GetResultCalls);
            f.Frame.RequestStop();
            AssertSnapshot(f.Frame.Snapshot, ReadEnd.Faulted, stop: true, unknown: true, error: error);
            Assert.Equal(beforeFailure, failed.Array);
            AssertNoTake(f.Frame);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false, "read")]
    [InlineData(false, "status")]
    [InlineData(false, "registration")]
    [InlineData(true, "read")]
    [InlineData(true, "status")]
    [InlineData(true, "registration")]
    public async Task BlockedReadOrAsTask_IsOutsideGate_AndCompletionIsRegisteredBeforeExternalCode(
        bool laterPayload, string blocked)
    {
        IOException error = new("受闸控制的 AsTask 失败");
        SynchronousPause? pause = null;
        TaskCompletionSource<int>? original = null;
        ThrowingSource source = new(error, blocked == "registration", () => pause!.Wait());
        int targetCall = laterPayload ? 3 : 1;
        FrameFixture f = new(read: (fixture, call, memory) =>
        {
            if (call == targetCall)
            {
                if (laterPayload) memory.Span.Fill(Marker);
                else fixture.CopyPrefix(memory, memory.Length);
                if (blocked != "read") return new ValueTask<int>(source, 0);
                pause!.Wait();
                return new ValueTask<int>(original!.Task);
            }
            if (call == 1)
            {
                fixture.CopyPrefix(memory, 3);
                return new ValueTask<int>(3);
            }
            memory.Span[..1].Fill(Marker);
            return new ValueTask<int>(1);
        });
        pause = f.NewPause();
        original = f.NewOriginal();
        int? callerTaskId = null;
        Task<Task> startCall = f.Keep(Task.Factory.StartNew(() =>
        {
            callerTaskId = Task.CurrentId;
            return f.Frame.Start();
        }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default));
        try
        {
            await pause.Reached.Task.WaitAsync(Guard);
            Task completion = await startCall.WaitAsync(Guard);
            ReadCall paused = f.Stream.Calls[^1];
            byte[] before = paused.Array.ToArray();
            Assert.Same(completion, paused.Registered);
            Assert.NotNull(paused.WorkerTaskId);
            Assert.NotEqual(callerTaskId, paused.WorkerTaskId);
            Assert.True(paused.OnThreadPool);
            Assert.Same(TaskScheduler.Default, paused.Scheduler);
            Assert.False(completion.IsCompleted);

            // 这些调用确实在同步前缀仍受闸阻塞时完成，不依赖等待若干毫秒的推测。
            Task<ReadSnapshot> snapshotCall = f.Keep(Task.Run(() => f.Frame.Snapshot));
            ReadSnapshot pending = await snapshotCall.WaitAsync(Guard);
            AssertSnapshot(pending, ReadEnd.Pending);
            Task<Exception?> repeatCall = f.Keep(Task.Run<Exception?>(
                () => Record.Exception(() => { _ = f.Frame.Start(); })));
            Assert.IsType<InvalidOperationException>(await repeatCall.WaitAsync(Guard));
            Task stopCall = f.Keep(Task.Run(f.Frame.RequestStop));
            await stopCall.WaitAsync(Guard);
            Task<Task?> registeredCall = f.Keep(Task.Run(() => Task.FromResult(f.Frame.Completion)));
            Assert.Same(completion, await registeredCall.WaitAsync(Guard));
            Task<bool> takeCall = f.Keep(Task.Run(() => f.Frame.TryTake(out _)));
            Assert.False(await takeCall.WaitAsync(Guard));
            Task<ReadSnapshot> stoppedCall = f.Keep(Task.Run(() => f.Frame.Snapshot));
            AssertSnapshot(await stoppedCall.WaitAsync(Guard), ReadEnd.Pending, stop: true);
            Assert.Equal(before, paused.Array);
            Assert.False(completion.IsCompleted);
            f.Stream.AssertNoOtherOperations();
            pause.Open();

            if (blocked == "read")
            {
                await AssertAwaitPathAsync(original.Task, completion);
                Assert.Equal(before, paused.Array);
                Assert.False(completion.IsCompleted);
                original.SetResult(paused.Memory.Length);
            }
            await AssertDrainedAsync(f.Frame);
            Assert.Equal(targetCall, f.Stream.Calls.Length);
            if (blocked == "read")
            {
                AssertSnapshot(f.Frame.Snapshot, ReadEnd.Stopped, stop: true);
                Assert.All(f.Stream.Calls, call => AssertCleared(call.Array));
            }
            else
            {
                AssertSnapshot(f.Frame.Snapshot, ReadEnd.Faulted, stop: true, unknown: true, error: error);
                Assert.Equal(before, paused.Array);
                if (laterPayload) AssertCleared(f.Stream.Calls[0].Array);
            }
            AssertSnapshot(pending, ReadEnd.Pending);
            AssertNoTake(f.Frame);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ConcurrentStart_HasOneWinnerAndOneFrameRead()
    {
        FrameFixture f = new();
        TaskCompletionSource release = f.NewSignal();
        TaskCompletionSource leftReady = f.NewSignal(), rightReady = f.NewSignal();
        Task<(Task? Completion, Exception? Error)> left = f.Keep(AttemptAsync(leftReady));
        Task<(Task? Completion, Exception? Error)> right = f.Keep(AttemptAsync(rightReady));
        try
        {
            await Task.WhenAll(leftReady.Task, rightReady.Task).WaitAsync(Guard);
            release.SetResult();
            var attempts = await Task.WhenAll(left, right).WaitAsync(Guard);
            var winner = Assert.Single(attempts, attempt => attempt.Error is null);
            var loser = Assert.Single(attempts, attempt => attempt.Error is not null);
            Assert.IsType<InvalidOperationException>(loser.Error);
            Assert.Null(loser.Completion);
            Assert.Same(winner.Completion, f.Frame.Completion);
            await AssertDrainedAsync(f.Frame);
            Assert.Equal(2, f.Stream.Calls.Length);
            AssertSnapshot(f.Frame.Snapshot, ReadEnd.Frame);
            Assert.True(f.Frame.TryTake(out byte[]? result));
            Assert.Same(f.Stream.Calls[1].Array, result);
            AssertNoTake(f.Frame);
        }
        finally { await f.FinishAsync(); }

        Task<(Task? Completion, Exception? Error)> AttemptAsync(TaskCompletionSource ready) =>
            Task.Run<(Task? Completion, Exception? Error)>(async () =>
            {
                ready.SetResult();
                await release.Task.ConfigureAwait(false);
                Task? completion = null;
                Exception? error = Record.Exception(() => { completion = f.Frame.Start(); });
                return (completion, error);
            });
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ConcurrentTake_HasOneWinnerWithOriginalPayloadArray()
    {
        FrameFixture f = new();
        TaskCompletionSource release = f.NewSignal();
        TaskCompletionSource leftReady = f.NewSignal(), rightReady = f.NewSignal();
        try
        {
            _ = f.Frame.Start();
            await AssertDrainedAsync(f.Frame);
            Task<(bool Taken, byte[]? Result)> left = f.Keep(TakeAsync(leftReady));
            Task<(bool Taken, byte[]? Result)> right = f.Keep(TakeAsync(rightReady));
            await Task.WhenAll(leftReady.Task, rightReady.Task).WaitAsync(Guard);
            release.SetResult();
            var attempts = await Task.WhenAll(left, right).WaitAsync(Guard);
            var winner = Assert.Single(attempts, attempt => attempt.Taken);
            var loser = Assert.Single(attempts, attempt => !attempt.Taken);
            Assert.Same(f.Stream.Calls[1].Array, winner.Result);
            Assert.Null(loser.Result);
            AssertSnapshot(f.Frame.Snapshot, ReadEnd.Frame, taken: true);
            AssertNoTake(f.Frame);
        }
        finally { await f.FinishAsync(); }

        Task<(bool Taken, byte[]? Result)> TakeAsync(TaskCompletionSource ready) => Task.Run(async () =>
        {
            ready.SetResult();
            await release.Task.ConfigureAwait(false);
            bool taken = f.Frame.TryTake(out byte[]? result);
            return (taken, result);
        });
    }

    [Fact(Timeout = TestTimeout)]
    public async Task StopAndTakeRace_AllowsOnlyOwnershipTransferOrClearedSuppression()
    {
        FrameFixture f = new();
        TaskCompletionSource release = f.NewSignal();
        TaskCompletionSource takeReady = f.NewSignal(), stopReady = f.NewSignal();
        try
        {
            _ = f.Frame.Start();
            await AssertDrainedAsync(f.Frame);
            byte[] payload = f.Stream.Calls[1].Array;
            Task<(bool Taken, byte[]? Result)> take = f.Keep(Task.Run(async () =>
            {
                takeReady.SetResult();
                await release.Task.ConfigureAwait(false);
                bool taken = f.Frame.TryTake(out byte[]? result);
                return (taken, result);
            }));
            Task stop = f.Keep(Task.Run(async () =>
            {
                stopReady.SetResult();
                await release.Task.ConfigureAwait(false);
                f.Frame.RequestStop();
            }));
            await Task.WhenAll(takeReady.Task, stopReady.Task).WaitAsync(Guard);
            release.SetResult();
            await Task.WhenAll(take, stop).WaitAsync(Guard);
            var result = await take;
            if (result.Taken)
            {
                Assert.Same(payload, result.Result);
                Assert.Equal(Enumerable.Repeat(Marker, 8).ToArray(), payload);
                payload[0] = 0x5A;
                f.Frame.RequestStop();
                Assert.Equal((byte)0x5A, payload[0]);
            }
            else
            {
                Assert.Null(result.Result);
                AssertCleared(payload);
            }
            AssertSnapshot(f.Frame.Snapshot, ReadEnd.Frame, stop: true, taken: result.Taken);
            AssertNoTake(f.Frame);
        }
        finally { await f.FinishAsync(); }
    }

    private static byte[] WireFrame(byte[] payload)
    {
        byte[] bytes = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)payload.Length);
        payload.CopyTo(bytes, 4);
        return bytes;
    }

    private static ArraySegment<byte> SegmentOf(Memory<byte> memory)
    {
        Assert.True(MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)memory, out ArraySegment<byte> segment));
        Assert.NotNull(segment.Array);
        return segment;
    }

    private static byte[] ArrayOf(Memory<byte> memory) => SegmentOf(memory).Array!;

    private static void AssertCleared(byte[] bytes) => Assert.All(bytes, value => Assert.Equal((byte)0, value));

    private static void AssertRequests(ReadCall[] calls, int offset, int total, int fragment)
    {
        Assert.NotEmpty(calls);
        byte[] array = calls[0].Array;
        foreach (ReadCall call in calls)
        {
            Assert.Same(array, call.Array);
            Assert.Equal(offset, SegmentOf(call.Memory).Offset);
            Assert.Equal(total - offset, call.Memory.Length);
            offset += Math.Min(fragment, total - offset);
        }
        Assert.Equal(total, offset);
    }

    private static void AssertSnapshot(ReadSnapshot snapshot, ReadEnd end, bool stop = false,
        bool taken = false, bool unknown = false, Exception? error = null)
    {
        Assert.Equal(end, snapshot.End);
        Assert.Equal(stop, snapshot.StopRequested);
        Assert.Equal(taken, snapshot.Taken);
        Assert.Equal(unknown, snapshot.BorrowExitUnproven);
        Assert.Same(error, snapshot.Error);
    }

    private static void AssertNoTake(ControlFrameContinuation frame)
    {
        Assert.False(frame.TryTake(out byte[]? result));
        Assert.Null(result);
    }

    private static async Task AssertDrainedAsync(ControlFrameContinuation frame)
    {
        Task completion = Assert.IsAssignableFrom<Task>(frame.Completion);
        await completion.WaitAsync(Guard);
        Assert.Equal(TaskStatus.RanToCompletion, completion.Status);
        Assert.Null(completion.Exception);
        Assert.Same(completion, frame.Completion);
    }

    // 复用已建立的运行时探针：只接受原 Task -> 精确 await 状态机 -> Task.Run unwrap 链。
    // 运行时布局不兼容必须显式失败，不把测试信号当成等待或退出证明。
    private static async Task AssertAwaitPathAsync(Task original, Task completion)
    {
        using CancellationTokenSource guard = new(Guard);
        while (!HasAwaitPath(original, completion, new HashSet<Task>()))
        {
            Assert.False(original.IsCompleted, "原读取提前退出，无法证明受闸控制的等待。");
            Assert.False(completion.IsCompleted, "Completion 提前退出，没有排空原读取。");
            if (guard.IsCancellationRequested)
                throw new XunitException("Guard：未找到原读取到 Completion 的真实 await 链。");
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
            bool exactAwait = machine is not null && machine.GetType().GetFields(Fields)
                .Where(field => field.Name.StartsWith("<>u__", StringComparison.Ordinal))
                .Select(field => field.GetValue(machine)).OfType<object>()
                .Any(awaiter => awaiter.GetType().GetFields(Fields)
                    .Any(field => ReferenceEquals(field.GetValue(awaiter), current)));
            bool unwrap = (next.GetType().FullName ?? "").StartsWith("System.Threading.Tasks.UnwrapPromise`", StringComparison.Ordinal);
            if (!exactAwait && !unwrap) continue;
            if (ReferenceEquals(next, target) || HasAwaitPath(next, target, visited)) return true;
        }
        return false;
    }

    private static object[] Continuations(Task task)
    {
        FieldInfo field = RuntimeField(typeof(Task), "m_continuationObject")
            ?? throw new XunitException("运行时缺少 Task continuation 字段，不能跳过 await 证明。");
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

    private sealed class FrameFixture
    {
        private readonly byte[] _header = new byte[4];
        private readonly List<TaskCompletionSource<int>> _originals = [];
        private readonly List<TaskCompletionSource> _signals = [];
        private readonly List<SynchronousPause> _pauses = [];
        private readonly List<Task> _tasks = [];
        internal ProbeStream Stream { get; }
        internal ControlFrameContinuation Frame { get; }

        internal FrameFixture(uint length = 8, int maximum = 64,
            Func<FrameFixture, int, Memory<byte>, ValueTask<int>>? read = null)
        {
            BinaryPrimitives.WriteUInt32BigEndian(_header, length);
            Stream = new ProbeStream((call, memory) =>
            {
                if (read is not null) return read(this, call, memory);
                if (call == 1) CopyPrefix(memory, 3);
                else memory.Span.Fill(Marker);
                return new ValueTask<int>(memory.Length);
            });
            Frame = new ControlFrameContinuation(Stream, _header[0], maximum);
            Stream.Registered = () => Frame.Completion;
        }

        internal void CopyPrefix(Memory<byte> memory, int count) =>
            _header.AsMemory(SegmentOf(memory).Offset, count).CopyTo(memory);

        internal TaskCompletionSource<int> NewOriginal()
        {
            TaskCompletionSource<int> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _originals.Add(source);
            return source;
        }

        internal TaskCompletionSource NewSignal()
        {
            TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _signals.Add(signal);
            return signal;
        }

        internal SynchronousPause NewPause()
        {
            SynchronousPause pause = new();
            _pauses.Add(pause);
            return pause;
        }

        internal T Keep<T>(T task) where T : Task
        {
            _tasks.Add(task);
            return task;
        }

        internal async Task FinishAsync()
        {
            // 先放开所有同步/异步闸，Stop 后再放原读，最后等待调用者与实际 worker。
            foreach (SynchronousPause pause in _pauses) pause.Open();
            foreach (TaskCompletionSource signal in _signals) signal.TrySetResult();
            Frame.RequestStop();
            foreach (TaskCompletionSource<int> original in _originals) original.TrySetResult(0);
            try
            {
                try { await Task.WhenAll(_tasks).WaitAsync(Guard); }
                finally
                {
                    if (Frame.Completion is { } completion) await completion.WaitAsync(Guard);
                }
            }
            finally
            {
                foreach (SynchronousPause pause in _pauses) pause.Dispose();
                Stream.ReleaseRegistrations();
                Stream.AssertNoOtherOperations();
            }
        }
    }

    private sealed class SynchronousPause : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Wait()
        {
            Reached.TrySetResult();
            if (!_release.Wait(Guard)) throw new XunitException("Guard：同步前缀未被释放。");
        }
        internal void Open() => _release.Set();
        public void Dispose() => _release.Dispose();
    }

    private sealed class ThrowingSource(Exception error, bool onRegistration, Action? beforeFailure = null) : IValueTaskSource<int>
    {
        private int _statusCalls, _getResultCalls, _onCompletedCalls;
        internal int StatusCalls => Volatile.Read(ref _statusCalls);
        internal int GetResultCalls => Volatile.Read(ref _getResultCalls);
        internal int OnCompletedCalls => Volatile.Read(ref _onCompletedCalls);
        public ValueTaskSourceStatus GetStatus(short token)
        {
            Interlocked.Increment(ref _statusCalls);
            if (onRegistration) return ValueTaskSourceStatus.Pending;
            beforeFailure?.Invoke();
            throw error;
        }
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            Interlocked.Increment(ref _onCompletedCalls);
            beforeFailure?.Invoke();
            throw error;
        }
        public int GetResult(short token)
        {
            Interlocked.Increment(ref _getResultCalls);
            throw new XunitException("未取得原 Task 的源不应调用 GetResult。");
        }
    }

    private sealed record ReadCall(Memory<byte> Memory, CancellationToken Token, Task? Registered,
        int? WorkerTaskId, bool OnThreadPool, TaskScheduler Scheduler)
    {
        internal byte[] Array => ArrayOf(Memory);
    }

    // 包装委托而非派生 MemoryStream，避免 ReadAsync 的基类转发造成双计数。
    private sealed class ProbeStream(Func<int, Memory<byte>, ValueTask<int>> read) : Stream
    {
        private readonly ConcurrentQueue<ReadCall> _calls = new();
        private readonly ConcurrentQueue<string> _otherOperations = new();
        private readonly ConcurrentQueue<CancellationTokenRegistration> _registrations = new();
        private int _readCalls, _cancellationCallbacks;
        internal Func<Task?> Registered { get; set; } = () => null;
        internal ReadCall[] Calls => _calls.ToArray();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _readCalls);
            _calls.Enqueue(new ReadCall(buffer, cancellationToken, Registered(), Task.CurrentId,
                Thread.CurrentThread.IsThreadPoolThread, TaskScheduler.Current));
            _registrations.Enqueue(cancellationToken.Register(() => Interlocked.Increment(ref _cancellationCallbacks)));
            return read(call, buffer);
        }
        internal void AssertNoOtherOperations()
        {
            Assert.Empty(_otherOperations);
            Assert.Equal(0, Volatile.Read(ref _cancellationCallbacks));
            Assert.Equal(Volatile.Read(ref _readCalls), Calls.Length);
            Assert.All(Calls, call =>
            {
                Assert.Equal(CancellationToken.None, call.Token);
                Assert.False(call.Token.CanBeCanceled);
                Assert.False(call.Token.IsCancellationRequested);
            });
        }
        internal void ReleaseRegistrations()
        {
            while (_registrations.TryDequeue(out CancellationTokenRegistration registration)) registration.Dispose();
        }
        private Exception Unexpected(string operation)
        {
            _otherOperations.Enqueue(operation);
            return new XunitException($"借用流不应执行 {operation}。");
        }
        public override bool CanRead => throw Unexpected(nameof(CanRead));
        public override bool CanWrite => throw Unexpected(nameof(CanWrite));
        public override bool CanSeek => throw Unexpected(nameof(CanSeek));
        public override bool CanTimeout => throw Unexpected(nameof(CanTimeout));
        public override long Length => throw Unexpected(nameof(Length));
        public override long Position
        {
            get => throw Unexpected("Position.get");
            set => throw Unexpected("Position.set");
        }
        public override int ReadTimeout
        {
            get => throw Unexpected("ReadTimeout.get");
            set => throw Unexpected("ReadTimeout.set");
        }
        public override int WriteTimeout
        {
            get => throw Unexpected("WriteTimeout.get");
            set => throw Unexpected("WriteTimeout.set");
        }
        public override int Read(byte[] buffer, int offset, int count) => throw Unexpected("Read(byte[])");
        public override int Read(Span<byte> buffer) => throw Unexpected("Read(Span)");
        public override int ReadByte() => throw Unexpected(nameof(ReadByte));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw Unexpected("ReadAsync(byte[])");
        public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) => throw Unexpected(nameof(BeginRead));
        public override int EndRead(IAsyncResult asyncResult) => throw Unexpected(nameof(EndRead));
        public override void Write(byte[] buffer, int offset, int count) => throw Unexpected("Write(byte[])");
        public override void Write(ReadOnlySpan<byte> buffer) => throw Unexpected("Write(Span)");
        public override void WriteByte(byte value) => throw Unexpected(nameof(WriteByte));
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw Unexpected("WriteAsync(byte[])");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => throw Unexpected("WriteAsync(Memory)");
        public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) => throw Unexpected(nameof(BeginWrite));
        public override void EndWrite(IAsyncResult asyncResult) => throw Unexpected(nameof(EndWrite));
        public override void Flush() => throw Unexpected(nameof(Flush));
        public override Task FlushAsync(CancellationToken cancellationToken) => throw Unexpected(nameof(FlushAsync));
        public override long Seek(long offset, SeekOrigin origin) => throw Unexpected(nameof(Seek));
        public override void SetLength(long value) => throw Unexpected(nameof(SetLength));
        public override void Close() => _otherOperations.Enqueue(nameof(Close));
        protected override void Dispose(bool disposing) => _otherOperations.Enqueue(nameof(Dispose));
        public override ValueTask DisposeAsync()
        {
            _otherOperations.Enqueue(nameof(DisposeAsync));
            return ValueTask.CompletedTask;
        }
    }
}
