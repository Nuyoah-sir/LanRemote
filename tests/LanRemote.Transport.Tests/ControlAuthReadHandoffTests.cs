using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks.Sources;
using Xunit.Sdk;

namespace LanRemote.Transport.Tests;

public sealed class ControlAuthReadHandoffTests
{
    // Guard 只让失灵测试退出；所有时序断言由未释放的闸或已 await 的任务证明。
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private const int TestTimeout = 120_000;
    private const byte Marker = 0xC7;
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // 独立入口检查：缺方法是契约缺口，不冒充已有生产行为的语义 red。
    [Fact]
    public void Host_Read_Handoff_Entry_Is_Internal_On_The_Real_Private_Nested_Owner()
    {
        Type ownerType = OwnerProbe.RequireOwnerType();
        MethodInfo take = OwnerProbe.RequireTake(ownerType);
        Assert.True(ownerType.IsNestedPrivate);
        Assert.Same(typeof(ControlAuthSession), ownerType.DeclaringType);
        Assert.True(take.IsAssembly);
        Assert.False(take.IsStatic);
        Assert.Equal(typeof(bool), take.ReturnType);
        ParameterInfo argument = Assert.Single(take.GetParameters());
        Assert.True(argument.IsOut);
        Assert.Equal(typeof(byte?).MakeByRefType(), argument.ParameterType);
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false, 0, 199)]
    [InlineData(false, 1, 0)]
    [InlineData(false, 1, 199)]
    [InlineData(false, 1, 255)]
    [InlineData(true, 0, 199)]
    [InlineData(true, 1, 0)]
    [InlineData(true, 1, 199)]
    [InlineData(true, 1, 255)]
    public async Task Host_Read_Handoff_Success_Distinguishes_Eof_And_Byte_And_Clears_Before_Return(
        bool asynchronous, int count, int value)
    {
        TaskCompletionSource<int> original = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource caller = new();
        ProbeStream stream = new((_, memory, _) =>
        {
            memory.Span[0] = (byte)value;
            return asynchronous ? new ValueTask<int>(original.Task) : ValueTask.FromResult(count);
        });
        OwnerProbe owner = new(stream, caller.Token);
        try
        {
            ReadCall call = Assert.Single(stream.Calls);
            Assert.Equal(caller.Token, call.Token);
            Assert.Equal(1, call.Memory.Length);
            Assert.Single(call.Array);
            Assert.Equal(0, SegmentOf(call.Memory).Offset);
            Assert.Same(call.Array, owner.Buffer);
            if (asynchronous)
            {
                Assert.False(owner.Activity.IsCompleted);
                Assert.NotSame(original.Task, owner.Activity);
                original.SetResult(count);
            }
            Assert.Equal(count, await owner.Activity.WaitAsync(Guard));
            Assert.Equal((byte)value, call.Memory.Span[0]);

            byte? firstByte = 0x52;
            Assert.True(owner.Take(ref firstByte));
            Assert.Equal(count == 0 ? null : (byte?)value, firstByte);
            // 直接读取传给 Stream.ReadAsync 的同一 Memory，不以 owner 字段为清零替身。
            Assert.Equal((byte)0, call.Memory.Span[0]);
            Assert.Null(owner.Buffer);
            Assert.Single(stream.Calls);

            call.Memory.Span[0] = 0x6B;
            Assert.Equal(count == 0 ? null : (byte?)value, firstByte);
            AssertNoTake(owner);
            owner.Clear();
            Assert.Equal((byte)0x6B, call.Memory.Span[0]);
            Assert.Single(stream.Calls);
        }
        finally
        {
            original.TrySetResult(count);
            await JoinAllAsync([owner.Activity, original.Task]);
            owner.Clear();
        }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Host_Read_Handoff_Pending_False_Resets_Out_And_Retains_Ownership_For_Retry(int count)
    {
        TaskCompletionSource<int> original = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ProbeStream stream = new((_, memory, _) =>
        {
            memory.Span[0] = Marker;
            return new ValueTask<int>(original.Task);
        });
        OwnerProbe owner = new(stream);
        try
        {
            ReadCall call = Assert.Single(stream.Calls);
            Assert.False(owner.Activity.IsCompleted);
            AssertNoTake(owner);
            Assert.Same(call.Array, owner.Buffer);
            Assert.Equal(Marker, call.Memory.Span[0]);
            owner.Clear();
            AssertNoTake(owner);
            Assert.Same(call.Array, owner.Buffer);
            Assert.Equal(Marker, call.Memory.Span[0]);
            Assert.False(original.Task.IsCompleted);

            original.SetResult(count);
            Assert.Equal(count, await owner.Activity.WaitAsync(Guard));
            byte? firstByte = 0x52;
            Assert.True(owner.Take(ref firstByte));
            Assert.Equal(count == 0 ? null : (byte?)Marker, firstByte);
            Assert.Equal((byte)0, call.Memory.Span[0]);
            Assert.Null(owner.Buffer);
            AssertNoTake(owner);
            Assert.Single(stream.Calls);
        }
        finally
        {
            original.TrySetResult(count);
            await JoinAllAsync([owner.Activity, original.Task]);
            owner.Clear();
        }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Host_Read_Handoff_Ready_Source_Is_Not_Completed_Wrapper_And_GetResult_Is_Exactly_Once(int count)
    {
        DeferredSource source = new();
        ProbeStream stream = new((_, memory, _) =>
        {
            memory.Span[0] = Marker;
            return source.Read();
        });
        OwnerProbe owner = new(stream);
        try
        {
            await source.Registered.Task.WaitAsync(Guard);
            ReadCall call = Assert.Single(stream.Calls);
            Assert.Equal(1, source.Registrations);
            source.CompleteWithoutDispatch(count);
            Assert.Equal(ValueTaskSourceStatus.Succeeded, source.Status);
            Assert.False(owner.Activity.IsCompleted);
            Assert.Equal(0, source.ResultCalls);
            AssertNoTake(owner);
            owner.Clear();
            AssertNoTake(owner);
            Assert.Equal(0, source.ResultCalls);
            Assert.Same(call.Array, owner.Buffer);
            Assert.Equal(Marker, call.Memory.Span[0]);

            source.ReleaseContinuation();
            Assert.Equal(count, await owner.Activity.WaitAsync(Guard));
            Assert.Equal(1, source.ResultCalls);
            byte? firstByte = 0x52;
            Assert.True(owner.Take(ref firstByte));
            Assert.Equal(count == 0 ? null : (byte?)Marker, firstByte);
            Assert.Equal((byte)0, call.Memory.Span[0]);
            AssertNoTake(owner);
            owner.Clear();
            Assert.Equal(1, source.ResultCalls);
            Assert.Equal(1, source.Registrations);
            Assert.Single(stream.Calls);
        }
        finally
        {
            source.CompleteWithoutDispatch(count);
            source.ReleaseContinuation();
            await JoinAllAsync([owner.Activity]);
            owner.Clear();
        }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false, "io")]
    [InlineData(false, "disposed")]
    [InlineData(false, "unexpected")]
    [InlineData(false, "cancelled")]
    [InlineData(true, "io")]
    [InlineData(true, "disposed")]
    [InlineData(true, "unexpected")]
    [InlineData(true, "cancelled")]
    public async Task Host_Read_Handoff_Unsuccessful_Task_Never_Takes_Or_Clears_And_Preserves_Error(
        bool asynchronous, string outcome)
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Exception failure = Failure(outcome, cancellation.Token);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int>? original = null;
        if (!asynchronous) release.SetResult();
        ProbeStream stream = new((_, memory, _) =>
        {
            memory.Span[0] = Marker;
            original = FailAsync();
            return new ValueTask<int>(original);
        });
        OwnerProbe owner = new(stream, cancellation.Token);
        try
        {
            ReadCall call = Assert.Single(stream.Calls);
            Assert.Equal(cancellation.Token, call.Token);
            if (asynchronous)
            {
                Assert.False(owner.Activity.IsCompleted);
                AssertNoTake(owner);
                Assert.Same(call.Array, owner.Buffer);
                release.SetResult();
            }
            await AssertExactFailureAsync(owner.Activity, failure);
            Assert.Equal(outcome == "cancelled", owner.Activity.IsCanceled);
            Assert.Equal(outcome != "cancelled", owner.Activity.IsFaulted);
            AssertNoTake(owner);
            Assert.Same(call.Array, owner.Buffer);
            Assert.Equal(Marker, call.Memory.Span[0]);
            owner.Clear();
            call.Memory.Span[0] = 0x6B;
            AssertNoTake(owner);
            owner.Clear();
            Assert.Same(call.Array, owner.Buffer);
            Assert.Equal((byte)0x6B, call.Memory.Span[0]);
            Assert.Single(stream.Calls);
        }
        finally
        {
            release.TrySetResult();
            await JoinAllAsync([owner.Activity, original], failure);
        }

        async Task<int> FailAsync()
        {
            await release.Task.ConfigureAwait(false);
            throw failure;
        }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("io")]
    [InlineData("unexpected")]
    [InlineData("cancelled")]
    public async Task Host_Read_Handoff_GetResult_Failure_Does_Not_Steal_Or_Clear(string outcome)
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Exception failure = Failure(outcome, cancellation.Token);
        DeferredSource source = new();
        ProbeStream stream = new((_, memory, _) =>
        {
            memory.Span[0] = Marker;
            return source.Read();
        });
        OwnerProbe owner = new(stream, cancellation.Token);
        try
        {
            await source.Registered.Task.WaitAsync(Guard);
            ReadCall call = Assert.Single(stream.Calls);
            source.FailWithoutDispatch(failure);
            Assert.Equal(outcome == "cancelled" ? ValueTaskSourceStatus.Canceled : ValueTaskSourceStatus.Faulted,
                source.Status);
            Assert.False(owner.Activity.IsCompleted);
            Assert.Equal(0, source.ResultCalls);
            AssertNoTake(owner);
            owner.Clear();
            Assert.Same(call.Array, owner.Buffer);
            Assert.Equal(Marker, call.Memory.Span[0]);
            source.ReleaseContinuation();
            await AssertExactFailureAsync(owner.Activity, failure);
            Assert.Equal(1, source.ResultCalls);
            AssertNoTake(owner);
            owner.Clear();
            Assert.Same(call.Array, owner.Buffer);
            Assert.Equal(Marker, call.Memory.Span[0]);
            Assert.Equal(1, source.ResultCalls);
            Assert.Equal(1, source.Registrations);
            Assert.Single(stream.Calls);
        }
        finally
        {
            source.FailWithoutDispatch(failure);
            source.ReleaseContinuation();
            await JoinAllAsync([owner.Activity], failure);
        }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("read", "io")]
    [InlineData("read", "unexpected")]
    [InlineData("read", "cancelled")]
    [InlineData("status", "io")]
    [InlineData("status", "unexpected")]
    [InlineData("status", "cancelled")]
    public async Task Host_Read_Handoff_Synchronous_Prefix_Or_Status_Failure_Keeps_Unknown_Borrow(
        string position, string outcome)
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Exception failure = Failure(outcome, cancellation.Token);
        TaskCompletionSource writerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource writerRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? writer = null;
        StatusFailureSource source = new(failure);
        ProbeStream stream = new((_, memory, _) =>
        {
            memory.Span[0] = Marker;
            writer = WriteLateAsync(memory);
            return position == "read" ? throw failure : new ValueTask<int>(source, 0);
        });
        // 构造异常也必须进入 finally，不能遗留已经启动的未知借用 writer。
        OwnerProbe? owner = null;
        try
        {
            Exception? constructionError = Record.Exception(() => { owner = new OwnerProbe(stream, cancellation.Token); });
            Assert.Null(constructionError);
            Assert.NotNull(owner);
            await writerReady.Task.WaitAsync(Guard);
            ReadCall call = Assert.Single(stream.Calls);
            await AssertExactFailureAsync(owner.Activity, failure);
            Assert.False(Assert.IsAssignableFrom<Task>(writer).IsCompleted);
            AssertNoTake(owner);
            owner.Clear();
            Assert.Same(call.Array, owner.Buffer);
            Assert.Equal(Marker, call.Memory.Span[0]);

            writerRelease.SetResult();
            await writer!.WaitAsync(Guard);
            Assert.Equal((byte)0x6B, call.Memory.Span[0]);
            AssertNoTake(owner);
            owner.Clear();
            Assert.Same(call.Array, owner.Buffer);
            Assert.Equal((byte)0x6B, call.Memory.Span[0]);
            Assert.Equal(position == "status" ? 1 : 0, source.StatusCalls);
            Assert.Equal(0, source.ResultCalls);
            Assert.Equal(0, source.Registrations);
            Assert.Single(stream.Calls);
        }
        finally
        {
            writerRelease.TrySetResult();
            await JoinAllAsync([owner?.Activity, writer], failure);
        }

        async Task WriteLateAsync(Memory<byte> memory)
        {
            writerReady.TrySetResult();
            await writerRelease.Task.ConfigureAwait(false);
            memory.Span[0] = 0x6B;
        }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false, -1)]
    [InlineData(false, 2)]
    [InlineData(true, -1)]
    [InlineData(true, 2)]
    public async Task Host_Read_Handoff_Invalid_Count_Does_Not_Take_But_Clear_Still_Owns_Buffer(
        bool asynchronous, int count)
    {
        TaskCompletionSource<int> original = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ProbeStream stream = new((_, memory, _) =>
        {
            memory.Span[0] = Marker;
            return asynchronous ? new ValueTask<int>(original.Task) : ValueTask.FromResult(count);
        });
        OwnerProbe owner = new(stream);
        try
        {
            if (asynchronous) original.SetResult(count);
            Assert.Equal(count, await owner.Activity.WaitAsync(Guard));
            Assert.True(owner.Activity.IsCompletedSuccessfully);
            ReadCall call = Assert.Single(stream.Calls);
            AssertNoTake(owner);
            Assert.Same(call.Array, owner.Buffer);
            Assert.Equal(Marker, call.Memory.Span[0]);
            owner.Clear();
            Assert.Equal((byte)0, call.Memory.Span[0]);
            Assert.Null(owner.Buffer);
            call.Memory.Span[0] = 0x6B;
            AssertNoTake(owner);
            owner.Clear();
            Assert.Equal((byte)0x6B, call.Memory.Span[0]);
            Assert.Single(stream.Calls);
        }
        finally
        {
            original.TrySetResult(count);
            await JoinAllAsync([owner.Activity, original.Task]);
            owner.Clear();
        }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public async Task Host_Read_Handoff_Take_And_Clear_Share_One_Ownership_In_Both_Orders(bool clearFirst, int count)
    {
        ProbeStream stream = SuccessfulStream(count);
        OwnerProbe owner = new(stream);
        try
        {
            Assert.Equal(count, await owner.Activity.WaitAsync(Guard));
            ReadCall call = Assert.Single(stream.Calls);
            if (clearFirst)
            {
                owner.Clear();
                Assert.Equal((byte)0, call.Memory.Span[0]);
                Assert.Null(owner.Buffer);
                AssertNoTake(owner);
            }
            else
            {
                byte? firstByte = 0x52;
                Assert.True(owner.Take(ref firstByte));
                Assert.Equal(count == 0 ? null : (byte?)Marker, firstByte);
                Assert.Equal((byte)0, call.Memory.Span[0]);
                Assert.Null(owner.Buffer);
            }
            call.Memory.Span[0] = 0x6B;
            for (int repeat = 0; repeat < 3; repeat++)
            {
                AssertNoTake(owner);
                owner.Clear();
                Assert.Equal((byte)0x6B, call.Memory.Span[0]);
                Assert.Null(owner.Buffer);
            }
            Assert.Single(stream.Calls);
        }
        finally
        {
            await JoinAllAsync([owner.Activity]);
            owner.Clear();
        }
    }

    // 并发仅补充确定次序测试。败者返回不代表赢家完成清零，必须先 join 所有竞争调用。
    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_Read_Handoff_Concurrent_Take_Take_And_Clear_Join_All_Before_Buffer_Check(bool includeClear)
    {
        for (int round = 0; round < 256; round++)
        {
            ProbeStream stream = SuccessfulStream(1);
            OwnerProbe owner = new(stream);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource leftReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource rightReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource clearReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<(bool Taken, byte? Value)>? left = null, right = null;
            Task? clear = null;
            try
            {
                Assert.Equal(1, await owner.Activity.WaitAsync(Guard));
                left = CompeteAsync(leftReady);
                right = CompeteAsync(rightReady);
                clear = includeClear ? Task.Run(async () =>
                {
                    clearReady.SetResult();
                    await release.Task.ConfigureAwait(false);
                    owner.Clear();
                }) : Task.CompletedTask;
                if (!includeClear) clearReady.SetResult();
                await Task.WhenAll(leftReady.Task, rightReady.Task, clearReady.Task).WaitAsync(Guard);
                release.SetResult();
                await JoinAllAsync([left, right, clear]);
                var results = new[] { await left, await right };
                int winners = results.Count(result => result.Taken);
                if (includeClear) Assert.InRange(winners, 0, 1);
                else Assert.Equal(1, winners);
                foreach (var result in results)
                    Assert.Equal(result.Taken ? (byte?)Marker : null, result.Value);
                ReadCall call = Assert.Single(stream.Calls);
                Assert.Null(owner.Buffer);
                Assert.Equal((byte)0, call.Memory.Span[0]);
                call.Memory.Span[0] = 0x6B;
                AssertNoTake(owner);
                owner.Clear();
                Assert.Equal((byte)0x6B, call.Memory.Span[0]);
            }
            finally
            {
                release.TrySetResult();
                await JoinAllAsync([owner.Activity, left, right, clear]);
                owner.Clear();
            }

            Task<(bool Taken, byte? Value)> CompeteAsync(TaskCompletionSource ready) => Task.Run(async () =>
            {
                ready.SetResult();
                await release.Task.ConfigureAwait(false);
                byte? value = 0x52;
                bool taken = owner.Take(ref value);
                return (taken, value);
            });
        }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Host_Read_Handoff_Same_Stream_Continuation_Consumes_Only_First_Coalesced_Frame()
    {
        byte[] second = [0, 0, 0, 2, 0x44, 0x55];
        WireFixture wire = new([0, 0, 0, 3, 0x11, 0x22, 0x33, .. second]);
        OwnerProbe owner = new(wire.Stream);
        ControlFrameContinuation? frame = null;
        Task? continuation = null;
        byte[]? payload = null;
        try
        {
            Assert.False(owner.Activity.IsCompleted);
            Assert.Single(wire.Stream.Calls);
            Assert.Equal(1, wire.Stream.Calls[0].Memory.Length);
            Assert.Equal(0, wire.Consumed);
            AssertNoTake(owner);
            wire.ReleaseFirst();
            Assert.Equal(1, await owner.Activity.WaitAsync(Guard));
            Assert.Equal(1, wire.Consumed);
            ReadCall first = Assert.Single(wire.Stream.Calls);
            byte? firstByte = 0x52;
            Assert.True(owner.Take(ref firstByte));
            Assert.Equal((byte?)0, firstByte);
            Assert.Equal((byte)0, first.Memory.Span[0]);
            Assert.Single(wire.Stream.Calls);
            Assert.Equal(1, wire.Consumed);

            wire.HandoffReturned = true;
            frame = new ControlFrameContinuation(wire.Stream, firstByte!.Value, 64);
            Assert.Single(wire.Stream.Calls);
            continuation = frame.Start();
            await continuation.WaitAsync(Guard);
            Assert.Same(continuation, frame.Completion);
            Assert.Equal(ControlFrameContinuation.ReadEnd.Frame, frame.Snapshot.End);
            Assert.Null(frame.Snapshot.Error);
            Assert.True(frame.TryTake(out payload));
            Assert.Equal(new byte[] { 0x11, 0x22, 0x33 }, payload);
            Assert.False(frame.TryTake(out byte[]? repeated));
            Assert.Null(repeated);
            Assert.Equal(7, wire.Consumed);
            Assert.Equal(second, wire.Remaining);
            Assert.Equal(new[] { 1, 3, 3 }, wire.Stream.Calls.Select(call => call.Memory.Length).ToArray());
            Assert.Equal(1, SegmentOf(wire.Stream.Calls[1].Memory).Offset);
            Assert.Equal(1, wire.MaxConcurrentReads);
            Assert.Equal(0, wire.ReadsBeforeHandoff);
            AssertNoTake(owner);
            owner.Clear();
            Assert.Equal(3, wire.Stream.Calls.Length);
        }
        finally
        {
            wire.ReleaseFirst();
            frame?.RequestStop();
            try
            {
                await JoinAllAsync(wire.TasksInJoinOrder(owner.Activity, continuation));
            }
            finally
            {
                if (payload is not null) CryptographicOperations.ZeroMemory(payload);
                owner.Clear();
            }
        }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Host_Read_Handoff_Eof_Does_Not_Open_A_Continuation()
    {
        WireFixture wire = new([]);
        OwnerProbe owner = new(wire.Stream);
        ControlFrameContinuation? frame = null;
        Task? continuation = null;
        try
        {
            wire.ReleaseFirst();
            Assert.Equal(0, await owner.Activity.WaitAsync(Guard));
            ReadCall call = Assert.Single(wire.Stream.Calls);
            Assert.Equal(Marker, call.Memory.Span[0]);
            byte? firstByte = 0x52;
            Assert.True(owner.Take(ref firstByte));
            wire.HandoffReturned = true;
            if (firstByte is { } value)
            {
                frame = new ControlFrameContinuation(wire.Stream, value, 64);
                continuation = frame.Start();
                await continuation.WaitAsync(Guard);
            }
            Assert.Null(firstByte);
            Assert.Null(frame);
            Assert.Null(continuation);
            Assert.Equal((byte)0, call.Memory.Span[0]);
            Assert.Single(wire.Stream.Calls);
            Assert.Equal(0, wire.Consumed);
            Assert.Equal(1, wire.MaxConcurrentReads);
            AssertNoTake(owner);
        }
        finally
        {
            wire.ReleaseFirst();
            frame?.RequestStop();
            await JoinAllAsync(wire.TasksInJoinOrder(owner.Activity, continuation));
            owner.Clear();
        }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Host_Read_Handoff_High_Bit_First_Byte_Produces_Oversized_Header_Not_A_Small_Frame()
    {
        WireFixture wire = new([0x80, 0, 0, 3, 0x11, 0x22, 0x33]);
        OwnerProbe owner = new(wire.Stream);
        ControlFrameContinuation? frame = null;
        Task? continuation = null;
        try
        {
            wire.ReleaseFirst();
            Assert.Equal(1, await owner.Activity.WaitAsync(Guard));
            ReadCall call = Assert.Single(wire.Stream.Calls);
            Assert.Equal((byte)0x80, call.Memory.Span[0]);
            byte? firstByte = 0x52;
            Assert.True(owner.Take(ref firstByte));
            Assert.Equal((byte?)0x80, firstByte);
            Assert.Equal((byte)0, call.Memory.Span[0]);
            Assert.Single(wire.Stream.Calls);
            wire.HandoffReturned = true;
            frame = new ControlFrameContinuation(wire.Stream, firstByte!.Value, 64);
            continuation = frame.Start();
            await continuation.WaitAsync(Guard);
            Assert.Equal(ControlFrameContinuation.ReadEnd.Faulted, frame.Snapshot.End);
            FrameProtocolException error = Assert.IsType<FrameProtocolException>(frame.Snapshot.Error);
            Assert.Equal(FrameReader.RejectTooLarge, error.Reason);
            Assert.False(frame.TryTake(out byte[]? payload));
            Assert.Null(payload);
            Assert.Equal(new[] { 1, 3 }, wire.Stream.Calls.Select(read => read.Memory.Length).ToArray());
            Assert.Equal(4, wire.Consumed);
            Assert.Equal(new byte[] { 0x11, 0x22, 0x33 }, wire.Remaining);
            Assert.Equal(1, wire.MaxConcurrentReads);
            Assert.Equal(0, wire.ReadsBeforeHandoff);
        }
        finally
        {
            wire.ReleaseFirst();
            frame?.RequestStop();
            await JoinAllAsync(wire.TasksInJoinOrder(owner.Activity, continuation));
            owner.Clear();
        }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Host_Read_Handoff_Cleanup_Joins_Later_Task_And_Rejects_Unexpected_Sibling()
    {
        IOException injected = new("明确注入的原读异常。");
        InvalidOperationException sibling = new("不得顺带吞掉的兄弟异常。");
        Task both = Task.WhenAll(Task.FromException(injected), Task.FromException(sibling));
        TaskCompletionSource later = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task cleanup = JoinAllAsync([both, later.Task], injected);
        try
        {
            Assert.False(cleanup.IsCompleted);
            later.SetResult();
            AggregateException error = await Assert.ThrowsAsync<AggregateException>(() => cleanup.WaitAsync(Guard));
            Assert.Same(sibling, Assert.Single(error.Flatten().InnerExceptions));
        }
        finally
        {
            later.TrySetResult();
            await JoinAllAsync([both, later.Task, cleanup], injected, sibling);
        }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_Read_Handoff_Cleanup_Does_Not_Exempt_A_Different_Cancellation_Token(bool matchingToken)
    {
        using CancellationTokenSource expected = new();
        using CancellationTokenSource other = new();
        expected.Cancel();
        other.Cancel();
        CancellationToken actual = matchingToken ? expected.Token : other.Token;
        Task original = Task.FromCanceled(actual);
        Task cleanup = JoinAllAsync([original], new OperationCanceledException(expected.Token));
        try
        {
            if (matchingToken) await cleanup.WaitAsync(Guard);
            else
            {
                AggregateException error = await Assert.ThrowsAsync<AggregateException>(() => cleanup.WaitAsync(Guard));
                OperationCanceledException cancelled = Assert.IsAssignableFrom<OperationCanceledException>(
                    Assert.Single(error.Flatten().InnerExceptions));
                Assert.Equal(actual, cancelled.CancellationToken);
            }
        }
        finally
        {
            // cleanup 的预期失败逐叶核对；不通过大范围 catch 忽略未知故障。
            Exception[] failures = cleanup.Exception?.Flatten().InnerExceptions.ToArray() ?? [];
            await JoinAllAsync(new Task?[] { original, cleanup }, [new OperationCanceledException(actual), .. failures]);
            Assert.All(failures, error =>
            {
                OperationCanceledException cancelled = Assert.IsAssignableFrom<OperationCanceledException>(error);
                Assert.Equal(actual, cancelled.CancellationToken);
            });
        }
    }

    private static ProbeStream SuccessfulStream(int count) => new((_, memory, _) =>
    {
        memory.Span[0] = Marker;
        return ValueTask.FromResult(count);
    });

    private static Exception Failure(string outcome, CancellationToken token) => outcome switch
    {
        "io" => new IOException("受控原读 IO 故障。"),
        "disposed" => new ObjectDisposedException("受控原读"),
        "cancelled" => new OperationCanceledException("受控原读取消。", token),
        _ => new InvalidOperationException("受控原读意外故障。"),
    };

    private static void AssertNoTake(OwnerProbe owner)
    {
        foreach (byte? seed in new byte?[] { 0, Marker, 0xFF, null })
        {
            byte? firstByte = seed;
            bool taken = true;
            Exception? error = Record.Exception(() => { taken = owner.Take(ref firstByte); });
            Assert.Null(error);
            Assert.False(taken);
            Assert.Null(firstByte);
        }
    }

    private static async Task AssertExactFailureAsync(Task task, Exception expected)
    {
        Exception? actual = await Record.ExceptionAsync(() => task.WaitAsync(Guard));
        Assert.Same(expected, actual);
        if (expected is OperationCanceledException cancelled)
            Assert.Equal(cancelled.CancellationToken, Assert.IsAssignableFrom<OperationCanceledException>(actual).CancellationToken);
    }

    private static async Task JoinAllAsync(IEnumerable<Task?> tasks, params Exception[] expected)
    {
        List<Exception> failures = [];
        // 每项分别 await，前项意外 fault 不能使后面的 writer、Activity 或竞争调用失去观察者。
        foreach (Task? task in tasks)
        {
            if (task is null) continue;
            try
            {
                await task.WaitAsync(Guard);
            }
            catch (OperationCanceledException error) when (task.IsCanceled && expected.OfType<OperationCanceledException>()
                .Any(allowed => error.CancellationToken == allowed.CancellationToken))
            {
            }
            catch (Exception error)
            {
                if (task.IsFaulted && task.Exception is { } tree)
                    failures.AddRange(tree.Flatten().InnerExceptions.Where(leaf => !expected.Any(item => ReferenceEquals(item, leaf))));
                else
                    failures.Add(error);
            }
        }
        if (failures.Count != 0) throw new AggregateException("读取移交测试收尾失败。", failures);
    }

    private static ArraySegment<byte> SegmentOf(Memory<byte> memory)
    {
        Assert.True(MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)memory, out ArraySegment<byte> segment));
        Assert.NotNull(segment.Array);
        return segment;
    }

    private sealed class OwnerProbe
    {
        private readonly object _owner;
        private readonly MethodInfo _take;
        private readonly MethodInfo _clear;
        private readonly FieldInfo _buffer;
        internal Task<int> Activity { get; }
        internal byte[]? Buffer => (byte[]?)_buffer.GetValue(_owner);

        internal OwnerProbe(Stream stream, CancellationToken token = default)
        {
            Type type = RequireOwnerType();
            // 必须先解析新入口才启动真正的读；绝不缺方法时退回 Clear 或自造成功结果。
            _take = RequireTake(type);
            _clear = type.GetMethod("ClearCompletedBuffer", InstanceMembers)
                ?? throw new XunitException("真实 owner 缺少 ClearCompletedBuffer。");
            _buffer = type.GetField("_buffer", InstanceMembers)
                ?? throw new XunitException("真实 owner 缺少原缓冲所有权字段。");
            ConstructorInfo constructor = type.GetConstructor(InstanceMembers, null, [typeof(Stream), typeof(CancellationToken)], null)
                ?? throw new XunitException("真实 owner 缺少原 Stream 构造器。");
            PropertyInfo activity = type.GetProperty("Activity", InstanceMembers)
                ?? throw new XunitException("真实 owner 缺少 Activity。");
            _owner = constructor.Invoke([stream, token]);
            Activity = Assert.IsAssignableFrom<Task<int>>(activity.GetValue(_owner));
        }

        internal static Type RequireOwnerType() => typeof(ControlAuthSession).GetNestedType(
            "OwnedControlActivityRead", BindingFlags.NonPublic)
            ?? throw new XunitException("入口缺口：未找到真实 private nested OwnedControlActivityRead。");

        internal static MethodInfo RequireTake(Type type) => type.GetMethod("TryTakeCompleted", InstanceMembers,
            null, [typeof(byte?).MakeByRefType()], null)
            ?? throw new XunitException("入口缺口：真实 owner 尚无 TryTakeCompleted(out byte?)；这不是生产语义 red。");

        internal bool Take(ref byte? firstByte)
        {
            object?[] arguments = [firstByte];
            bool taken = Assert.IsType<bool>(Invoke(_take, arguments));
            firstByte = (byte?)arguments[0];
            return taken;
        }

        internal void Clear() => Invoke(_clear, null);

        private object? Invoke(MethodInfo method, object?[]? arguments)
        {
            try { return method.Invoke(_owner, arguments); }
            catch (TargetInvocationException error) when (error.InnerException is { } inner)
            {
                ExceptionDispatchInfo.Capture(inner).Throw();
                throw;
            }
        }
    }

    private sealed record ReadCall(Memory<byte> Memory, CancellationToken Token)
    {
        internal byte[] Array => SegmentOf(Memory).Array!;
    }

    private sealed class ProbeStream(Func<int, Memory<byte>, CancellationToken, ValueTask<int>> read) : Stream
    {
        private readonly ConcurrentQueue<ReadCall> _calls = new();
        private int _callCount;
        internal ReadCall[] Calls => _calls.ToArray();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _callCount);
            _calls.Enqueue(new ReadCall(buffer, cancellationToken));
            return read(call, buffer, cancellationToken);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class WireFixture
    {
        private readonly byte[] _wire;
        private readonly object _gate = new();
        private readonly TaskCompletionSource _firstRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<Task> _writers = new();
        private int _consumed, _active, _maximum, _early;
        internal ProbeStream Stream { get; }
        internal bool HandoffReturned { get; set; }
        internal int Consumed { get { lock (_gate) return _consumed; } }
        internal byte[] Remaining { get { lock (_gate) return _wire[_consumed..]; } }
        internal int MaxConcurrentReads { get { lock (_gate) return _maximum; } }
        internal int ReadsBeforeHandoff => Volatile.Read(ref _early);
        internal IEnumerable<Task?> TasksInJoinOrder(Task activity, Task? continuation)
        {
            yield return activity;
            yield return continuation;
            // 迭代器在前两项 await 后才快照，避免遗漏接续期间新登记的原 writer。
            foreach (Task writer in _writers.ToArray()) yield return writer;
        }

        internal WireFixture(byte[] wire)
        {
            _wire = wire;
            Stream = new ProbeStream((call, memory, _) =>
            {
                Task<int> writer = ReadAsync(call, memory);
                _writers.Enqueue(writer);
                return new ValueTask<int>(writer);
            });
        }

        internal void ReleaseFirst() => _firstRelease.TrySetResult();

        private async Task<int> ReadAsync(int call, Memory<byte> memory)
        {
            lock (_gate)
            {
                _active++;
                _maximum = Math.Max(_maximum, _active);
                if (call > 1 && !HandoffReturned) Interlocked.Increment(ref _early);
            }
            try
            {
                if (call == 1)
                {
                    memory.Span.Fill(Marker);
                    await _firstRelease.Task.ConfigureAwait(false);
                }
                lock (_gate)
                {
                    int count = Math.Min(memory.Length, _wire.Length - _consumed);
                    _wire.AsMemory(_consumed, count).CopyTo(memory);
                    _consumed += count;
                    return count;
                }
            }
            finally { lock (_gate) _active--; }
        }
    }

    // 两个合法 core 分离 ready 状态与 continuation 派发；不靠 OnCompleted 抛异常伪造悬挂包装。
    private sealed class DeferredSource : IValueTaskSource<int>
    {
        private readonly object _gate = new();
        private ManualResetValueTaskSourceCore<int> _result;
        private ManualResetValueTaskSourceCore<int> _dispatch = new() { RunContinuationsAsynchronously = true };
        private bool _completed, _released;
        private int _registrations, _resultCalls;
        internal TaskCompletionSource Registered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Registrations => Volatile.Read(ref _registrations);
        internal int ResultCalls => Volatile.Read(ref _resultCalls);
        internal ValueTaskSourceStatus Status => _result.GetStatus(_result.Version);
        internal ValueTask<int> Read() => new(this, _result.Version);

        internal void CompleteWithoutDispatch(int result)
        {
            lock (_gate)
            {
                if (_completed) return;
                _result.SetResult(result);
                _completed = true;
            }
        }

        internal void FailWithoutDispatch(Exception failure)
        {
            lock (_gate)
            {
                if (_completed) return;
                _result.SetException(failure);
                _completed = true;
            }
        }

        internal void ReleaseContinuation()
        {
            lock (_gate)
            {
                if (_released) return;
                if (!_completed) throw new InvalidOperationException("须先完成原源再派发 continuation。");
                _released = true;
                _dispatch.SetResult(0);
            }
        }

        public int GetResult(short token)
        {
            Interlocked.Increment(ref _resultCalls);
            return _result.GetResult(token);
        }
        public ValueTaskSourceStatus GetStatus(short token) => _result.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            Interlocked.Increment(ref _registrations);
            _dispatch.OnCompleted(continuation, state, token, flags);
            Registered.TrySetResult();
        }
    }

    private sealed class StatusFailureSource(Exception failure) : IValueTaskSource<int>
    {
        private int _statusCalls, _resultCalls, _registrations;
        internal int StatusCalls => Volatile.Read(ref _statusCalls);
        internal int ResultCalls => Volatile.Read(ref _resultCalls);
        internal int Registrations => Volatile.Read(ref _registrations);
        public ValueTaskSourceStatus GetStatus(short token)
        {
            Interlocked.Increment(ref _statusCalls);
            throw failure;
        }
        public int GetResult(short token)
        {
            Interlocked.Increment(ref _resultCalls);
            throw new XunitException("状态查询失败的未知借用不能取结果。");
        }
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            Interlocked.Increment(ref _registrations);
            throw new XunitException("状态查询失败后不能注册 continuation。");
        }
    }
}
