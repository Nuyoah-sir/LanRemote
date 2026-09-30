using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks.Sources;
using LanRemote.Transport.Auth;
using Xunit.Sdk;

namespace LanRemote.Transport.Tests;

public sealed class SensitiveFrameWriterTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Theory]
    [InlineData(1)]
    [InlineData(257)]
    public async Task Exact_Payload_Limit_Writes_Original_Whole_Array_Once_Then_Flushes(int payloadLength)
    {
        Scenario f = new(ManualWire((uint)payloadLength, payloadLength));
        bool passed = false;
        try
        {
            f.ReleaseIo();
            Task writing = f.Start(payloadLength);
            await SuccessAsync(writing);

            Assert.True(writing.IsCompletedSuccessfully);
            f.AssertWritten(flushes: 1);
            f.AssertZeroed();
            passed = true;
        }
        finally { await f.FinishAsync(passed); }
    }

    [Fact]
    public async Task Real_VideoHello_Serialization_Transfers_The_Same_Array_And_Matches_Independent_Wire()
    {
        const string hello = """{"type":"channel_hello","channel":"video","protocol":1,"sessionId":"00112233-4455-6677-8899-aabbccddeeff","attachNonce":"ICEiIyQlJicoKSorLC0uLw==","attachProof":"YCZW6twCP5OfD5IrYxGjYsOukEiogPz0OB5VLxeLejo="}""";
        byte[] nonce = Convert.FromHexString("202122232425262728292A2B2C2D2E2F");
        byte[] proof = Convert.FromHexString("602656EADC023F939F0F922B6311A362C3AE9048A880FCF4381E552F178B7A3A");
        byte[] wire = VideoHelloWire.SerializeFrame(new Guid("00112233-4455-6677-8899-aabbccddeeff"), nonce, proof);
        Scenario f = new(wire);
        bool passed = false;
        try
        {
            // 黄金期望是独立文本和手写大端前缀，绝不调用生产 Serialize 生成期望。
            byte[] expected = [0x00, 0x00, 0x00, 0xD0, .. System.Text.Encoding.UTF8.GetBytes(hello)];
            Assert.Equal(212, expected.Length);
            Assert.Equal(expected, wire);
            f.Write.TrySetResult();
            Task writing = f.Start(208);
            await AssertAwaitingOriginalAsync(writing, f.Flush.Task);

            f.AssertUnchanged();
            f.AssertWritten(flushes: 1);
            f.Flush.TrySetResult();
            await SuccessAsync(writing);
            f.AssertZeroed();
            Assert.Equal(Convert.FromHexString("202122232425262728292A2B2C2D2E2F"), nonce);
            Assert.Equal(Convert.FromHexString("602656EADC023F939F0F922B6311A362C3AE9048A880FCF4381E552F178B7A3A"), proof);
            passed = true;
        }
        finally { await f.FinishAsync(passed); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Truncated_Prefix_Is_Rejected_And_All_Owned_Bytes_Are_Zeroed(int length)
    {
        Scenario f = new(Enumerable.Repeat((byte)0xA5, length).ToArray());
        bool passed = false;
        try
        {
            Assert.IsType<FrameProtocolException>(await ErrorAsync(f.Start(64)));
            f.AssertNoIo();
            f.AssertZeroed();
            passed = true;
        }
        finally { await f.FinishAsync(passed); }
    }

    [Theory]
    [InlineData(0u, 7, 64)]
    [InlineData(uint.MaxValue, 7, int.MaxValue)]
    [InlineData(7u, 7, 6)]
    [InlineData(6u, 7, 64)]
    [InlineData(8u, 7, 64)]
    public async Task Invalid_Prefix_Limit_Or_Exact_Length_Is_Rejected_Before_Io(
        uint prefix, int payloadLength, int maxBytes)
    {
        Scenario f = new(ManualWire(prefix, payloadLength));
        bool passed = false;
        try
        {
            // 校验被错误删除时，让非法发送确定完成并直接命中合同断言，不能被 I/O 闸门/Guard 遮蔽。
            f.ReleaseIo();
            Assert.IsType<FrameProtocolException>(await ErrorAsync(f.Start(maxBytes)));
            f.AssertNoIo();
            f.AssertZeroed();
            passed = true;
        }
        finally { await f.FinishAsync(passed); }
    }

    [Fact]
    public async Task Null_Stream_Does_Not_Prevent_Entry_Ownership_And_Whole_Array_Cleanup()
    {
        Scenario f = new(ManualWire(7, 7));
        bool passed = false;
        try
        {
            f.Writing = SensitiveFrameWriter.WriteOwnedFrameAsync(null!, f.Wire, 7, f.Caller.Token);
            Assert.IsType<ArgumentNullException>(await ErrorAsync(f.Writing));
            f.AssertNoIo();
            f.AssertZeroed();
            passed = true;
        }
        finally { await f.FinishAsync(passed); }
    }

    [Fact]
    public async Task Already_Canceled_Token_Still_Transfers_Ownership_Without_Any_Io()
    {
        Scenario f = new(ManualWire(7, 7));
        bool passed = false;
        try
        {
            f.Caller.Cancel();
            Task writing = f.Start(7);
            OperationCanceledException error = Assert.IsAssignableFrom<OperationCanceledException>(await ErrorAsync(writing));
            Assert.Equal(f.Caller.Token, error.CancellationToken);
            Assert.True(writing.IsCanceled);
            f.AssertNoIo();
            f.AssertZeroed();
            passed = true;
        }
        finally { await f.FinishAsync(passed); }
    }

    [Theory]
    [InlineData("write", false)]
    [InlineData("write", true)]
    [InlineData("flush", false)]
    [InlineData("flush", true)]
    public async Task Pending_Original_Io_Retains_Whole_Array_And_Cancellation_Cannot_Detach(
        string phase, bool cancel)
    {
        Scenario f = new(ManualWire(257, 257));
        bool passed = false;
        try
        {
            TaskCompletionSource original = f.Hold(phase);
            Task writing = f.Start(257);
            await AssertAwaitingOriginalAsync(writing, original.Task);
            f.AssertUnchanged();
            f.AssertWritten(phase == "write" ? 0 : 1);

            if (cancel) f.Caller.Cancel();
            // 取消后的正向 await 边仍指向同一原 Task，不依赖瞬时 IsCompleted 或延时猜测。
            await AssertAwaitingOriginalAsync(writing, original.Task);
            f.AssertUnchanged();
            original.TrySetResult();

            if (cancel)
            {
                OperationCanceledException error = Assert.IsAssignableFrom<OperationCanceledException>(await ErrorAsync(writing));
                Assert.Equal(f.Caller.Token, error.CancellationToken);
                Assert.True(writing.IsCanceled);
            }
            else
            {
                await SuccessAsync(writing);
            }

            f.AssertWritten(cancel && phase == "write" ? 0 : 1);
            f.AssertZeroed();
            passed = true;
        }
        finally { await f.FinishAsync(passed); }
    }

    [Theory]
    [InlineData("write")]
    [InlineData("flush")]
    public async Task Pending_Original_Failure_Wins_Over_Caller_Cancellation_Without_Flush_Or_Retry(string phase)
    {
        Scenario f = new(ManualWire(7, 7));
        TimeoutException failure = new("原 I/O 超时，不是测试 Guard。");
        bool passed = false;
        try
        {
            TaskCompletionSource original = f.Hold(phase);
            Task writing = f.Start(7);
            await AssertAwaitingOriginalAsync(writing, original.Task);
            f.Caller.Cancel();
            await AssertAwaitingOriginalAsync(writing, original.Task);
            f.AssertUnchanged();

            original.TrySetException(failure);
            Assert.Same(failure, await ErrorAsync(writing));
            Assert.True(writing.IsFaulted);
            Assert.Same(failure, Assert.Single(writing.Exception!.InnerExceptions));
            f.AssertWritten(phase == "write" ? 0 : 1);
            f.AssertZeroed();
            passed = true;
        }
        finally { await f.FinishAsync(passed); }
    }

    [Theory]
    [InlineData("write")]
    [InlineData("flush")]
    public async Task Original_Canceled_Task_Preserves_Its_Own_Token_Instead_Of_Later_Caller_Cancellation(string phase)
    {
        Scenario f = new(ManualWire(7, 7));
        using CancellationTokenSource ioCancellation = new();
        bool passed = false;
        try
        {
            TaskCompletionSource original = f.Hold(phase);
            Task writing = f.Start(7);
            await AssertAwaitingOriginalAsync(writing, original.Task);
            f.Caller.Cancel();
            await AssertAwaitingOriginalAsync(writing, original.Task);
            f.AssertUnchanged();

            ioCancellation.Cancel();
            original.TrySetCanceled(ioCancellation.Token);
            OperationCanceledException error = Assert.IsAssignableFrom<OperationCanceledException>(await ErrorAsync(writing));
            Assert.Equal(ioCancellation.Token, error.CancellationToken);
            Assert.NotEqual(f.Caller.Token, error.CancellationToken);
            Assert.True(writing.IsCanceled);
            f.AssertWritten(phase == "write" ? 0 : 1); // 原写取消也不能再 Flush。
            f.AssertZeroed();
            passed = true;
        }
        finally { await f.FinishAsync(passed); }
    }

    [Theory]
    [InlineData("write", false)]
    [InlineData("write", true)]
    [InlineData("flush", false)]
    [InlineData("flush", true)]
    public async Task Synchronous_Prefix_Is_Tracked_Until_Exit_And_Its_Original_Throw_Wins(
        string phase, bool throws)
    {
        Scenario f = new(ManualWire(257, 257));
        Exception failure = phase == "write"
            ? new TimeoutException("同步写前缀原异常。")
            : new AggregateException("同步 Flush 用户聚合。", new IOException("第一项。"),
                new AggregateException("不可展平的内层。", new InvalidOperationException("第二项。")));
        bool passed = false;
        try
        {
            f.ReleaseIo();
            Action prefix = () =>
            {
                f.Prefix.WaitSynchronously();
                if (throws) throw failure;
            };
            if (phase == "write") f.Stream.BeforeWriteReturns = prefix;
            else f.Stream.BeforeFlushReturns = prefix;

            // 只在测试端隔离阻塞栈，不 Unwrap：外层未返回就是生产同步前缀仍在栈上的证据。
            f.Invocation = Task.Factory.StartNew(() => f.Start(257), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await SuccessAsync(f.Prefix.Entered.Task);
            Assert.False(f.Prefix.IsOpen);
            Assert.False(f.Prefix.Exited.Task.IsCompleted);
            Assert.False(f.Invocation.IsCompleted, "直接 async 方法必须先退出同步前缀才能交还 Task。");
            f.AssertUnchanged();
            f.AssertWritten(phase == "write" ? 0 : 1);

            f.Caller.Cancel();
            Assert.False(f.Prefix.IsOpen);
            Assert.False(f.Prefix.Exited.Task.IsCompleted);
            Assert.False(f.Invocation.IsCompleted);
            f.AssertUnchanged();
            f.Prefix.Open();
            await SuccessAsync(f.Invocation);
            Task writing = await f.Invocation;
            Assert.Same(f.Writing, writing);
            await SuccessAsync(f.Prefix.Exited.Task);

            Exception error = await ErrorAsync(writing);
            if (throws)
            {
                Assert.Same(failure, error);
                Assert.True(writing.IsFaulted);
                Assert.Same(failure, Assert.Single(writing.Exception!.InnerExceptions));
            }
            else
            {
                Assert.Equal(f.Caller.Token, Assert.IsAssignableFrom<OperationCanceledException>(error).CancellationToken);
                Assert.True(writing.IsCanceled);
            }

            f.AssertWritten(phase == "write" ? 0 : 1);
            f.AssertZeroed();
            passed = true;
        }
        finally { await f.FinishAsync(passed); }
    }

    [Theory]
    [InlineData("write")]
    [InlineData("flush")]
    public async Task Synchronously_Successful_Io_Still_Checks_The_Same_Token_After_Return(string phase)
    {
        Scenario f = new(ManualWire(7, 7));
        bool passed = false;
        try
        {
            f.ReleaseIo();
            if (phase == "write") f.Stream.BeforeWriteReturns = f.Caller.Cancel;
            else f.Stream.BeforeFlushReturns = f.Caller.Cancel;

            Task writing = f.Start(7);
            OperationCanceledException error = Assert.IsAssignableFrom<OperationCanceledException>(await ErrorAsync(writing));
            Assert.Equal(f.Caller.Token, error.CancellationToken);
            Assert.True(writing.IsCanceled);
            f.AssertWritten(phase == "write" ? 0 : 1);
            f.AssertZeroed();
            passed = true;
        }
        finally { await f.FinishAsync(passed); }
    }

    [Theory]
    [InlineData("write", "single")]
    [InlineData("flush", "single")]
    [InlineData("write", "aggregate")]
    [InlineData("flush", "aggregate")]
    [InlineData("write", "multiple")]
    [InlineData("flush", "multiple")]
    public async Task Faulted_Original_Task_Preserves_Ordered_Tree_And_Only_Removes_Task_Single_Wrapper(
        string phase, string shape)
    {
        Scenario f = new(ManualWire(7, 7));
        TimeoutException leaf = new("原始叶异常，不是 Guard。");
        AggregateException nested = new("用户内层不可 Flatten。", leaf);
        AggregateException branch = new("用户外层不可重建。", new IOException("第一项。"), nested);
        Exception[] roots = shape switch
        {
            "single" => [leaf],
            "aggregate" => [new AggregateException("用户单项容器也不能继续剥除。", branch)],
            // 重复的原实例也是原 Task 树的一部分，不能去重或把嵌套取消改为全局取消。
            _ => [branch, new OperationCanceledException("第二项是原故障之一。"), branch],
        };
        bool passed = false;
        try
        {
            TaskCompletionSource original = f.Hold(phase);
            if (shape != "multiple") original.TrySetException(roots);
            Task writing = f.Start(7);
            if (shape == "multiple")
            {
                await AssertAwaitingOriginalAsync(writing, original.Task);
                f.Caller.Cancel();
                await AssertAwaitingOriginalAsync(writing, original.Task);
                f.AssertUnchanged();
                original.TrySetException(roots);
            }
            Exception error = await ErrorAsync(writing);

            if (roots.Length == 1)
            {
                Assert.Same(roots[0], error);
            }
            else
            {
                AggregateException preserved = Assert.IsType<AggregateException>(error);
                // Task.Exception 每次访问可能新建自动容器；比较其有序子树的原实例，不能比较两次 getter 的根身份。
                AggregateException originalTree = original.Task.Exception!;
                Assert.Equal(originalTree.InnerExceptions.Count, preserved.InnerExceptions.Count);
                for (int i = 0; i < roots.Length; i++)
                {
                    Assert.Same(roots[i], originalTree.InnerExceptions[i]);
                    Assert.Same(originalTree.InnerExceptions[i], preserved.InnerExceptions[i]);
                }
                Assert.Same(preserved.InnerExceptions[0], preserved.InnerExceptions[2]);
            }

            Assert.Same(nested, branch.InnerExceptions[1]);
            Assert.Same(leaf, Assert.Single(nested.InnerExceptions));
            Assert.True(writing.IsFaulted);
            Assert.Same(error, Assert.Single(writing.Exception!.InnerExceptions));
            f.AssertWritten(phase == "write" ? 0 : 1);
            f.AssertZeroed();
            passed = true;
        }
        finally { await f.FinishAsync(passed); }
    }

    [Fact]
    public async Task Single_Use_ValueTask_Source_Is_Consumed_Once_And_Its_Conversion_Task_Remains_Owned()
    {
        Scenario f = new(ManualWire(7, 7));
        SingleUseWriteSource source = new();
        bool passed = false;
        try
        {
            f.Stream.WriteSource = source;
            f.Flush.TrySetResult();
            Task writing = f.Start(7);
            Task original = Assert.IsAssignableFrom<Task>(source.ConversionTask);
            await AssertAwaitingOriginalAsync(writing, original);
            Assert.Equal(1, source.Subscriptions);
            Assert.Equal(0, source.ResultReads);
            f.Caller.Cancel();
            await AssertAwaitingOriginalAsync(writing, original);
            f.AssertUnchanged();

            source.Complete();
            OperationCanceledException error = Assert.IsAssignableFrom<OperationCanceledException>(await ErrorAsync(writing));
            Assert.Equal(f.Caller.Token, error.CancellationToken);
            Assert.Equal(1, source.Subscriptions);
            Assert.Equal(1, source.ResultReads);
            Assert.True(original.IsCompletedSuccessfully);
            f.AssertWritten(flushes: 0);
            f.AssertZeroed();
            passed = true;
        }
        finally { await f.FinishAsync(passed); }
    }

    [Theory]
    [InlineData(false, "io")]
    [InlineData(true, "io")]
    [InlineData(false, "aggregate")]
    [InlineData(true, "aggregate")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    public async Task Failed_ValueTask_Source_Preserves_GetResult_Error_And_Consumes_Once(
        bool completedBeforeStart, string failureKind)
    {
        Scenario f = new(ManualWire(257, 257));
        SingleUseWriteSource source = new();
        using CancellationTokenSource ioCancellation = new();
        IOException leaf = new("原 source 的 I/O 异常。");
        TimeoutException timeout = new("用户聚合中的原超时，不是 Guard。");
        AggregateException branch = new("用户嵌套分支。", leaf, timeout);
        Exception failure = failureKind switch
        {
            "aggregate" => new AggregateException("用户单项容器不能剥除或 Flatten。", branch),
            "cancel" => new OperationCanceledException("原 GetResult 取消。", ioCancellation.Token),
            _ => leaf,
        };
        bool passed = false;
        try
        {
            if (failureKind == "cancel") ioCancellation.Cancel();
            f.Stream.WriteSource = source;
            f.Flush.TrySetResult();
            if (completedBeforeStart)
            {
                source.Fail(failure);
                Assert.Equal(0, source.ResultReads);
                // 入口仍可用；在返回已完成 source 时取消，原失败必须先于后置取消。
                f.Stream.BeforeWriteReturns = f.Caller.Cancel;
            }

            Task writing = f.Start(257);
            if (completedBeforeStart)
            {
                // 已完成 source 的 AsTask 同步读取结果，不应调用 OnCompleted。
                Assert.Null(source.ConversionTask);
                Assert.Equal(0, source.Subscriptions);
            }
            else
            {
                Task original = Assert.IsAssignableFrom<Task>(source.ConversionTask);
                await AssertAwaitingOriginalAsync(writing, original);
                Assert.Equal(1, source.Subscriptions);
                Assert.Equal(0, source.ResultReads);
                f.AssertUnchanged();
                f.Caller.Cancel();
                await AssertAwaitingOriginalAsync(writing, original);
                f.AssertUnchanged();
                Assert.Null(source.ResultFailure);
                source.Fail(failure);
            }

            Exception error = await ErrorAsync(writing);
            Assert.Same(failure, source.ResultFailure);
            Assert.Same(failure, error);
            Assert.Equal(completedBeforeStart ? 0 : 1, source.Subscriptions);
            Assert.Equal(1, source.ResultReads);
            if (failureKind == "cancel")
            {
                Assert.True(writing.IsCanceled);
                OperationCanceledException canceled = Assert.IsType<OperationCanceledException>(error);
                Assert.Equal(ioCancellation.Token, canceled.CancellationToken);
                Assert.NotEqual(f.Caller.Token, canceled.CancellationToken);
            }
            else
            {
                Assert.True(writing.IsFaulted);
                Assert.Same(failure, Assert.Single(writing.Exception!.InnerExceptions));
                if (failureKind == "aggregate")
                {
                    AggregateException preserved = Assert.IsType<AggregateException>(error);
                    Assert.Same(branch, Assert.Single(preserved.InnerExceptions));
                    Assert.Equal(2, branch.InnerExceptions.Count);
                    Assert.Same(leaf, branch.InnerExceptions[0]);
                    Assert.Same(timeout, branch.InnerExceptions[1]);
                }
            }
            if (source.ConversionTask is { } conversion)
            {
                Assert.Same(failure, await ErrorAsync(conversion));
                Assert.Equal(failureKind == "cancel", conversion.IsCanceled);
                Assert.Equal(1, source.ResultReads);
            }
            f.AssertWritten(flushes: 0);
            f.AssertZeroed();
            passed = true;
        }
        finally { await f.FinishAsync(passed); }
    }

    [Fact]
    public async Task Null_Owned_Wire_Is_Rejected_Without_Any_Io()
    {
        Scenario f = new([]);
        bool passed = false;
        try
        {
            f.Writing = SensitiveFrameWriter.WriteOwnedFrameAsync(f.Stream, null!, 7, f.Caller.Token);
            ArgumentNullException error = Assert.IsType<ArgumentNullException>(await ErrorAsync(f.Writing));
            Assert.Equal("ownedWire", error.ParamName);
            f.AssertNoIo();
            passed = true;
        }
        finally { await f.FinishAsync(passed); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task Nonpositive_MaxBytes_Still_Zeroes_The_Whole_Owned_Array(int maxBytes)
    {
        Scenario f = new(ManualWire(7, 7));
        bool passed = false;
        try
        {
            Task writing = f.Start(maxBytes);
            ArgumentOutOfRangeException error = Assert.IsType<ArgumentOutOfRangeException>(await ErrorAsync(writing));
            Assert.Equal("maxBytes", error.ParamName);
            Assert.True(writing.IsFaulted);
            f.AssertNoIo();
            f.AssertZeroed();
            passed = true;
        }
        finally { await f.FinishAsync(passed); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Null_Flush_Task_Is_InvalidOperation_And_Cleans_Up_Even_With_Late_Cancellation(bool cancel)
    {
        Scenario f = new(ManualWire(257, 257));
        bool passed = false;
        try
        {
            f.ReleaseIo();
            f.Stream.BeforeFlushReturns = () =>
            {
                f.Stream.FlushTask = null!;
                if (cancel) f.Caller.Cancel();
            };
            Task writing = f.Start(257);
            Assert.IsType<InvalidOperationException>(await ErrorAsync(writing));
            Assert.True(writing.IsFaulted);
            f.AssertWritten(flushes: 1);
            f.AssertZeroed();
            passed = true;
        }
        finally { await f.FinishAsync(passed); }
    }

    private static byte[] ManualWire(uint prefix, int payloadLength)
    {
        // 不使用生产 framing/Serialize；257 的前缀同时覆盖多个大端字节。
        byte[] wire = new byte[4 + payloadLength];
        wire[0] = (byte)(prefix >> 24);
        wire[1] = (byte)(prefix >> 16);
        wire[2] = (byte)(prefix >> 8);
        wire[3] = (byte)prefix;
        for (int i = 4; i < wire.Length; i++) wire[i] = (byte)(1 + i % 251);
        return wire;
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task AssertOriginalWonAsync(Task original)
    {
        using CancellationTokenSource guardCancellation = new();
        Task guard = Task.Delay(Guard, guardCancellation.Token);
        try
        {
            Task winner = await Task.WhenAny(original, guard);
            Assert.Same(original, winner);
        }
        finally { guardCancellation.Cancel(); }
    }

    private static async Task SuccessAsync(Task original)
    {
        await AssertOriginalWonAsync(original);
        await original;
    }

    private static async Task<Exception> ErrorAsync(Task original)
    {
        // 不捕获 WaitAsync(Guard) 的 TimeoutException；必须先证明获胜的是原任务。
        await AssertOriginalWonAsync(original);
        Exception? error = await Record.ExceptionAsync(() => original);
        Assert.NotNull(error);
        return error;
    }

    private static async Task ObserveAsync(Task original)
    {
        await AssertOriginalWonAsync(original);
        _ = await Record.ExceptionAsync(() => original);
        _ = original.Exception;
    }

    private static async Task AssertAwaitingOriginalAsync(Task writing, Task original)
    {
        Type stateMachineType = typeof(SensitiveFrameWriter)
            .GetMethod(nameof(SensitiveFrameWriter.WriteOwnedFrameAsync), BindingFlags.Static | BindingFlags.NonPublic)?
            .GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
            ?? throw new XunitException("缺少直接 async 生产方法的状态机元数据，不能跳过挂起证明。");
        using CancellationTokenSource guardCancellation = new();
        Task guard = Task.Delay(Guard, guardCancellation.Token);
        try
        {
            Task? observedAwaitedTask;
            while ((observedAwaitedTask = FindRegisteredAwaitedTask(writing, stateMachineType)) is null)
            {
                Assert.False(original.IsCompleted, "原 I/O 已退出，不能证明受闸等待关系。");
                Assert.False(writing.IsCompleted, "生产 Task 提前退出，已失去原 I/O 的所有权。");
                if (guard.IsCompleted)
                    throw new XunitException("Guard：未观察到生产状态机的实际 await 边；运行时布局不支持时也必须失败。");
                await Task.Yield();
            }
            // 先证明实际挂起，再核对原任务身份；等待取消代理是合同失败，不能耗尽 Guard 才报错。
            Assert.Same(original, observedAwaitedTask);
            Assert.False(original.IsCompleted);
            Assert.False(writing.IsCompleted);
        }
        finally { guardCancellation.Cancel(); }
    }

    private static Task? FindRegisteredAwaitedTask(Task writing, Type stateMachineType)
    {
        object? stateMachine = RuntimeField(writing.GetType(), "StateMachine")?.GetValue(writing);
        if (stateMachine is null || stateMachine.GetType() != stateMachineType) return null;
        // 只从本 writing 的精确状态机 awaiter 取 Task，不读普通字段 original 或其他任务槽。
        IEnumerable<Task> awaitedTasks = stateMachineType.GetFields(Fields)
            .Where(field => field.Name.StartsWith("<>u__", StringComparison.Ordinal))
            .Select(field => field.GetValue(stateMachine))
            .OfType<object>()
            .SelectMany(awaiter => awaiter.GetType().GetFields(Fields)
                .Where(field => typeof(Task).IsAssignableFrom(field.FieldType))
                .Select(field => field.GetValue(awaiter)))
            .OfType<Task>();
        foreach (Task awaited in awaitedTasks)
        {
            if (awaited.IsCompleted) continue;
            // 反向配对真实 continuation，不能仅凭 awaiter 内残留的 Task 引用认定已挂起。
            if (Continuations(awaited).Any(continuation =>
                ReferenceEquals(continuation, writing) ||
                continuation is Delegate action && ReferenceEquals(action.Target, writing) ||
                RuntimeField(continuation.GetType(), "m_action")?.GetValue(continuation) is Delegate scheduled &&
                ReferenceEquals(scheduled.Target, writing))) return awaited;
        }
        return null;
    }

    private static object[] Continuations(Task task)
    {
        FieldInfo field = RuntimeField(typeof(Task), "m_continuationObject")
            ?? throw new XunitException("当前运行时没有 Task continuation 字段，需要适配而非忽略证明。");
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

    private sealed class Scenario(byte[] wire)
    {
        internal byte[] Wire { get; } = wire;
        // 测试端快照只用于观察清零时序，不参与发送，也不是生产黄金序列化器。
        internal byte[] Before { get; } = wire.ToArray();
        internal CancellationTokenSource Caller { get; } = new();
        internal TaskCompletionSource Write { get; } = Signal();
        internal TaskCompletionSource Flush { get; } = Signal();
        internal ProbeStream Stream { get; } = new();
        internal PrefixGate Prefix { get; } = new();
        internal Task? Writing { get; set; }
        internal Task<Task>? Invocation { get; set; }

        internal Task Start(int maxBytes)
        {
            Stream.WriteTask = Write.Task;
            Stream.FlushTask = Flush.Task;
            return Writing = SensitiveFrameWriter.WriteOwnedFrameAsync(Stream, Wire, maxBytes, Caller.Token);
        }

        internal TaskCompletionSource Hold(string phase)
        {
            if (phase == "write")
            {
                Flush.TrySetResult();
                return Write;
            }
            Write.TrySetResult();
            return Flush;
        }

        internal void ReleaseIo()
        {
            Write.TrySetResult();
            Flush.TrySetResult();
        }

        internal void AssertUnchanged() => Assert.Equal(Before, Wire);

        internal void AssertZeroed()
        {
            Assert.All(Wire, value => Assert.Equal((byte)0, value));
            AssertBorrowedStreamUntouched();
        }

        internal void AssertNoIo()
        {
            Assert.Equal(0, Stream.WriteCalls);
            Assert.Equal(0, Stream.FlushCalls);
            AssertBorrowedStreamUntouched();
        }

        internal void AssertWritten(int flushes)
        {
            Assert.Equal(1, Stream.WriteCalls);
            Assert.Equal(flushes, Stream.FlushCalls);
            Assert.True(MemoryMarshal.TryGetArray(Stream.WrittenMemory, out ArraySegment<byte> segment));
            Assert.Same(Wire, segment.Array);
            Assert.Equal(0, segment.Offset);
            Assert.Equal(Wire.Length, segment.Count);
            Assert.Equal(Before, Stream.WriteSnapshot);
            Assert.Equal(Caller.Token, Stream.WriteToken);
            if (flushes != 0)
            {
                Assert.Equal(Caller.Token, Stream.FlushToken);
                Assert.Equal(Before, Stream.FlushSnapshot);
            }
            AssertBorrowedStreamUntouched();
        }

        private void AssertBorrowedStreamUntouched()
        {
            Assert.Equal(0, Stream.LegacyWriteCalls);
            Assert.Equal(0, Stream.SyncWriteCalls);
            Assert.Equal(0, Stream.SyncFlushCalls);
            Assert.Equal(0, Stream.CloseCalls);
            Assert.Equal(0, Stream.DisposeCalls);
            Assert.Equal(0, Stream.DisposeAsyncCalls);
        }

        internal async Task FinishAsync(bool assertCleanup)
        {
            // 先打开所有测试闸，再回收原任务；不替生产清零，主断言一定在清理之前。
            Prefix.Open();
            ReleaseIo();
            try
            {
                Stream.WriteSource?.Complete();
                _ = Write.Task.Exception;
                _ = Flush.Task.Exception;
                if (Invocation is { } invocation)
                {
                    await ObserveAsync(invocation);
                    if (invocation.IsCompletedSuccessfully) Writing = await invocation;
                }
                if (Writing is { } writing) await ObserveAsync(writing);
                if (Stream.WriteSource?.ConversionTask is { } conversion) await ObserveAsync(conversion);
            }
            catch when (!assertCleanup)
            {
                // 清理失败不能覆盖 try 中关于所有权、原异常或 await 边的首个断言。
            }
            finally { Caller.Dispose(); }
        }
    }

    private sealed class PrefixGate
    {
        private readonly TaskCompletionSource _release = Signal();
        internal TaskCompletionSource Entered { get; } = Signal();
        internal TaskCompletionSource Exited { get; } = Signal();
        internal bool IsOpen => _release.Task.IsCompleted;
        internal void Open() => _release.TrySetResult();

        internal void WaitSynchronously()
        {
            using CancellationTokenSource guardCancellation = new();
            Task guard = Task.Delay(Guard, guardCancellation.Token);
            Entered.TrySetResult();
            try
            {
                Task winner = Task.WhenAny(_release.Task, guard).GetAwaiter().GetResult();
                if (!ReferenceEquals(_release.Task, winner))
                    throw new XunitException("Guard：同步测试前缀未放行，不是业务 Timeout。");
                _release.Task.GetAwaiter().GetResult();
            }
            finally
            {
                guardCancellation.Cancel();
                Exited.TrySetResult();
            }
        }
    }

    private sealed class SingleUseWriteSource : IValueTaskSource
    {
        private ManualResetValueTaskSourceCore<bool> _core = new() { RunContinuationsAsynchronously = true };
        private int _completed;
        private int _subscriptions;
        private int _resultReads;
        internal short Version => _core.Version;
        internal Task? ConversionTask { get; private set; }
        internal Exception? ResultFailure { get; private set; }
        internal int Subscriptions => Volatile.Read(ref _subscriptions);
        internal int ResultReads => Volatile.Read(ref _resultReads);

        internal void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0) _core.SetResult(true);
        }

        internal void Fail(Exception error)
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0) _core.SetException(error);
        }

        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            if (Interlocked.Increment(ref _subscriptions) != 1)
                throw new XunitException("同一原 ValueTask 只能转换/订阅一次。");
            ConversionTask = state as Task
                ?? throw new XunitException("需要原 ValueTask.AsTask 转换任务；当前运行时布局不支持时不能跳过。");
            _core.OnCompleted(continuation, state, token, flags);
        }

        public void GetResult(short token)
        {
            if (Interlocked.Increment(ref _resultReads) != 1)
                throw new XunitException("同一原 ValueTask 的结果只能消费一次。");
            try { _core.GetResult(token); }
            catch (Exception error)
            {
                ResultFailure = error;
                throw;
            }
        }
    }

    private sealed class ProbeStream : Stream
    {
        internal Task WriteTask { get; set; } = Task.CompletedTask;
        internal Task FlushTask { get; set; } = Task.CompletedTask;
        internal SingleUseWriteSource? WriteSource { get; set; }
        internal Action? BeforeWriteReturns { get; set; }
        internal Action? BeforeFlushReturns { get; set; }
        internal ReadOnlyMemory<byte> WrittenMemory { get; private set; }
        internal byte[]? WriteSnapshot { get; private set; }
        internal byte[]? FlushSnapshot { get; private set; }
        internal CancellationToken WriteToken { get; private set; }
        internal CancellationToken FlushToken { get; private set; }
        internal int WriteCalls { get; private set; }
        internal int FlushCalls { get; private set; }
        internal int LegacyWriteCalls { get; private set; }
        internal int SyncWriteCalls { get; private set; }
        internal int SyncFlushCalls { get; private set; }
        internal int CloseCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        internal int DisposeAsyncCalls { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            WrittenMemory = buffer;
            WriteSnapshot = buffer.ToArray();
            WriteToken = cancellationToken;
            BeforeWriteReturns?.Invoke();
            return WriteSource is { } source ? new ValueTask(source, source.Version) : new ValueTask(WriteTask);
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCalls++;
            FlushToken = cancellationToken;
            FlushSnapshot = WrittenMemory.ToArray();
            BeforeFlushReturns?.Invoke();
            return FlushTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            LegacyWriteCalls++;
            throw new XunitException("必须调用 ReadOnlyMemory<byte> WriteAsync 重载。");
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            SyncWriteCalls++;
            throw new XunitException("不允许同步 Write。");
        }

        public override void Flush()
        {
            SyncFlushCalls++;
            throw new XunitException("不允许同步 Flush。");
        }

        public override void Close() => CloseCalls++;
        protected override void Dispose(bool disposing) => DisposeCalls++;
        public override ValueTask DisposeAsync()
        {
            DisposeAsyncCalls++;
            return ValueTask.CompletedTask;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
