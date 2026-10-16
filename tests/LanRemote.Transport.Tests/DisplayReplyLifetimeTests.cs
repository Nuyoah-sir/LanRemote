using System.Net;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;
using Admission = LanRemote.Transport.SessionRegistry.SessionRegistration.DisplaySwitchAdmission;
using Receipt = LanRemote.Transport.DisplaySwitchExecutor.RequestExecutionReceipt;
using ReplyResult = LanRemote.Transport.DisplaySwitchExecutor.ReplyResult;
using ReplyStatus = LanRemote.Transport.DisplaySwitchExecutor.ReplyStatus;
using Result = LanRemote.Transport.DisplaySwitchExecutor.ExecutionResult;
using Status = LanRemote.Transport.SessionRegistry.SessionRegistration.DisplaySwitchAdmissionStatus;

namespace LanRemote.Transport.Tests;

public sealed class DisplayReplyLifetimeTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Success_Holds_Reservation_Through_Original_Write_And_Busy_Does_Not_Consume_Sequence()
    {
        await using Fixture f = await Fixture.CreateAsync();
        Producer first = f.Producer(), second = f.Producer(1920, 1080);
        int preparations = 0;
        DisplaySwitchExecutor executor = f.Executor((target, _) =>
        {
            Assert.Same(f.Catalog.Displays[1], target);
            return Task.FromResult<IVideoFrameProducer>(Interlocked.Increment(ref preparations) == 1 ? first : second);
        });
        Gate write = f.Gate();
        Receipt receipt = await f.Ready(executor, first);
        Assert.False(executor.HasActiveExecution);
        AssertBusy(f, executor, 1);
        DisplayChangedFrame? sent = null;
        Task<ReplyResult> reply = f.Send(executor, receipt, (frame, token) =>
        {
            sent = frame;
            return write.HoldAsync(token);
        });
        await Join(write.Entered.Task);
        Assert.True(executor.HasActiveExecution);
        Assert.False(reply.IsCompleted);
        AssertBusy(f, executor, 1);
        AssertRejected(f, executor, receipt, ReplyStatus.AlreadyCommitted);
        Assert.Equal(1, preparations);
        AssertFrame(sent!, f.Request(0), 70, 640, 360);

        write.Release();
        Assert.Equal(new ReplyResult(true, true, true), await Join(reply));
        Assert.False(executor.HasActiveExecution);
        AssertRejected(f, executor, receipt, ReplyStatus.InvalidReceipt);
        Receipt next = await f.Ready(executor, second, 1);
        Assert.NotSame(receipt, next);
        Assert.Equal(2, preparations);
        Task<ReplyResult> nextReply = f.Send(executor, next, (frame, _) =>
        {
            AssertFrame(frame, f.Request(1), 71, 1920, 1080);
            return Task.CompletedTask;
        });
        Assert.Equal(new ReplyResult(true, true, true), await Join(nextReply));
        Assert.False(f.Source.Termination.IsCompleted);
        Assert.False(f.Lease.Completed.IsCompleted);
        Assert.False(f.Lease.Revoked.IsCompleted);
        Assert.Equal(1, f.Registry.ActiveSessionCount);
    }

    [Fact]
    public async Task Forged_Cross_Executor_And_Old_Receipts_Cannot_Write_Or_Release_Current_Reservation()
    {
        await using Fixture f = await Fixture.CreateAsync();
        await using Fixture other = await Fixture.CreateAsync();
        Producer first = f.Producer(), next = f.Producer(), foreign = other.Producer();
        int preparations = 0;
        DisplaySwitchExecutor executor = f.Executor((_, _) =>
            Task.FromResult<IVideoFrameProducer>(Interlocked.Increment(ref preparations) == 1 ? first : next));
        DisplaySwitchExecutor otherExecutor = other.Executor((_, _) => Task.FromResult<IVideoFrameProducer>(foreign));
        Receipt receipt = await f.Ready(executor, first);
        Receipt foreignReceipt = await other.Ready(otherExecutor, foreign);
        Receipt forged = new(receipt.Request);
        forged.Bind(Settled(receipt.Binding));
        AssertRejected(f, executor, forged, ReplyStatus.InvalidReceipt);
        AssertRejected(f, executor, foreignReceipt, ReplyStatus.InvalidReceipt);
        AssertRejected(other, otherExecutor, receipt, ReplyStatus.InvalidReceipt);
        AssertBusy(f, executor, 1);
        AssertBusy(other, otherExecutor, 1);
        Assert.Equal(new ReplyResult(true, true, true), await Join(f.Send(executor, receipt, (_, _) => Task.CompletedTask)));

        Receipt current = await f.Ready(executor, next, 1);
        AssertRejected(f, executor, receipt, ReplyStatus.InvalidReceipt);
        AssertRejected(f, executor, forged, ReplyStatus.InvalidReceipt);
        AssertBusy(f, executor, 2);
        Assert.Equal(new ReplyResult(true, true, true), await Join(f.Send(executor, current, (_, _) => Task.CompletedTask)));
        Assert.Equal(new ReplyResult(true, true, true), await Join(other.Send(otherExecutor, foreignReceipt, (_, _) => Task.CompletedTask)));
    }

    [Fact]
    public async Task Prepare_And_Real_First_Send_Are_Required_Even_If_Public_Receipt_Binding_Is_Forged()
    {
        await using Fixture f = await Fixture.CreateAsync(blockVideo: true);
        await using Fixture donor = await Fixture.CreateAsync();
        Producer foreign = donor.Producer(1920, 1080);
        DisplaySwitchExecutor donorExecutor = donor.Executor((_, _) => Task.FromResult<IVideoFrameProducer>(foreign));
        Receipt donorReceipt = await donor.Ready(donorExecutor, foreign);
        var foreignGeneration = Settled(donorReceipt.Binding)!;
        Assert.NotNull(Settled(foreignGeneration.FirstSend));

        Producer candidate = f.Producer();
        TaskCompletionSource entered = Signal();
        TaskCompletionSource<IVideoFrameProducer> prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.ReleaseBeforeStop(() => prepared.TrySetResult(candidate));
        DisplaySwitchExecutor executor = f.Executor((_, _) =>
        {
            entered.TrySetResult();
            return prepared.Task;
        });
        Task<Result> execution = f.Execute(executor, 0, out Receipt receipt);
        await Join(entered.Task);
        receipt.Bind(foreignGeneration);
        Assert.Same(foreignGeneration, Settled(receipt.Binding));
        AssertRejected(f, executor, receipt, ReplyStatus.NotReady);
        AssertBusy(f, executor, 1);

        prepared.SetResult(candidate);
        Result result = await Join(execution);
        var real = Assert.IsType<DisplaySwitchExecutor.RequestSendReceipt>(result.SendReceipt);
        Assert.NotSame(foreignGeneration, real.Generation);
        Assert.False(real.FirstSend.IsCompleted);
        AssertRejected(f, executor, receipt, ReplyStatus.NotReady);
        f.StartSender();
        await Join(f.Video.WriteBlocked.Task);
        Assert.False(real.FirstSend.IsCompleted);
        AssertRejected(f, executor, receipt, ReplyStatus.NotReady);
        AssertBusy(f, executor, 1);
        f.Video.ReleaseWrite();
        await Join(candidate.NextReadEntered.Task);
        Assert.Equal(new VideoFrameSender.SentFrame(70, 640, 360), Settled(real.FirstSend));
        Assert.Equal(new ReplyResult(true, true, true), await Join(f.Send(executor, receipt, (frame, _) =>
        {
            AssertFrame(frame, f.Request(0), 70, 640, 360);
            return Task.CompletedTask;
        })));
    }

    [Fact]
    public async Task Sealed_Generation_Without_A_Real_First_Send_Cannot_Produce_A_Success_Reply()
    {
        await using Fixture f = await Fixture.CreateAsync();
        Producer candidate = f.Producer();
        DisplaySwitchExecutor executor = f.Executor((_, _) => Task.FromResult<IVideoFrameProducer>(candidate));
        Result execution = await Join(f.Execute(executor, 0, out Receipt receipt));
        var sendReceipt = Assert.IsType<DisplaySwitchExecutor.RequestSendReceipt>(execution.SendReceipt);
        Assert.False(sendReceipt.FirstSend.IsCompleted);
        await Join(f.Track(f.Source.DisposeAsync().AsTask()));
        Assert.Null(Settled(sendReceipt.FirstSend));
        AssertRejected(f, executor, receipt, ReplyStatus.Unavailable);
    }

    [Theory]
    [InlineData("withdraw")]
    [InlineData("unregister")]
    [InlineData("control-cancel")]
    [InlineData("lease-complete")]
    [InlineData("admission-close")]
    [InlineData("source-stop")]
    [InlineData("source-replace")]
    [InlineData("request-stop")]
    public async Task Invalidation_Before_Reply_Admission_Rejects_Without_Calling_Writer(string reason)
    {
        await using Fixture f = await Fixture.CreateAsync();
        Producer candidate = f.Producer();
        DisplaySwitchExecutor executor = f.Executor((_, _) => Task.FromResult<IVideoFrameProducer>(candidate));
        Receipt receipt = await f.Ready(executor, candidate);
        f.Invalidate(reason, executor);
        AssertRejected(f, executor, receipt, ReplyStatus.Unavailable);
        Assert.NotNull(Settled(Settled(receipt.Binding)!.FirstSend));
    }

    [Theory]
    [InlineData("withdraw")]
    [InlineData("unregister")]
    [InlineData("control-cancel")]
    [InlineData("lease-complete")]
    [InlineData("admission-close")]
    [InlineData("source-stop")]
    [InlineData("source-replace")]
    [InlineData("request-stop")]
    public async Task Registered_Worker_Rechecks_Parent_And_Source_Before_External_Write(string reason)
    {
        await using Fixture f = await Fixture.CreateAsync();
        Producer candidate = f.Producer();
        DisplaySwitchExecutor executor = f.Executor((_, _) => Task.FromResult<IVideoFrameProducer>(candidate));
        Receipt receipt = await f.Ready(executor, candidate);
        int calls = 0;
        Task<ReplyResult> reply;
        // 同线程重入真实父 gate：worker 已登记却不可能先取得最终提交资格，无 sleep 或调度猜测。
        lock (ExecutorGate(executor))
        {
            reply = f.Send(executor, receipt, (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.CompletedTask;
            });
            f.Invalidate(reason, executor);
            Assert.False(reply.IsCompleted);
        }
        Assert.Equal(new ReplyResult(false, false, false), await Join(reply));
        await Join(executor.Completion);
        Assert.Equal(0, calls);
        Assert.False(executor.HasActiveExecution);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("fault")]
    [InlineData("cancel")]
    public async Task Stop_Joins_Blocked_Writer_Synchronous_Prefix_And_Late_Original_Task(string outcome)
    {
        await using Fixture f = await Fixture.CreateAsync();
        Producer candidate = f.Producer();
        DisplaySwitchExecutor executor = f.Executor((_, _) => Task.FromResult<IVideoFrameProducer>(candidate));
        Receipt receipt = await f.Ready(executor, candidate);
        Gate prefix = f.Gate();
        TaskCompletionSource returned = Signal(), cancelled = Signal(), original = Signal();
        f.ReleaseBeforeStop(() => original.TrySetResult());
        CancellationToken writerToken = default;
        IOException failure = new("回执原写入迟到失败。");
        Task<ReplyResult> reply = f.Send(executor, receipt, (_, token) =>
        {
            writerToken = token;
            f.DisposeAfterJoin(token.Register(() => cancelled.TrySetResult()));
            prefix.Block();
            returned.TrySetResult();
            return original.Task;
        });
        await Join(prefix.Entered.Task);
        Task completion = executor.Completion;
        // 若外部同步前缀错误地占着父 gate，此原 Stop 调用会被正文 watchdog 诊断；清理仍先放 gate。
        await Join(f.Track(Task.Run(executor.RequestStop)));
        await Join(cancelled.Task);
        Assert.Same(completion, executor.DisposeAsync().AsTask());
        Assert.True(writerToken.IsCancellationRequested);
        Assert.False(reply.IsCompleted);
        Assert.False(completion.IsCompleted);
        prefix.Release();
        await Join(returned.Task);
        Assert.False(original.Task.IsCompleted);
        Assert.False(reply.IsCompleted);
        Assert.False(completion.IsCompleted);
        AssertRejected(f, executor, receipt, ReplyStatus.Unavailable);
        if (outcome == "success")
        {
            original.SetResult();
            Assert.Equal(new ReplyResult(true, true, false), await Join(reply));
            await Join(completion);
        }
        else
        {
            if (outcome == "fault") original.SetException(failure);
            else original.SetCanceled(writerToken);
            var replyError = Assert.IsType<AggregateException>(await Observe(reply));
            var stopError = Assert.IsType<AggregateException>(await Observe(completion));
            if (outcome == "fault")
            {
                Assert.Contains(failure, replyError.Flatten().InnerExceptions);
                Assert.Contains(failure, stopError.Flatten().InnerExceptions);
            }
            else
            {
                var cancelledError = Assert.IsAssignableFrom<OperationCanceledException>(Assert.Single(replyError.Flatten().InnerExceptions));
                Assert.Equal(writerToken, cancelledError.CancellationToken);
                Assert.Contains(stopError.Flatten().InnerExceptions, error => error is OperationCanceledException);
            }
        }
        Assert.True(original.Task.IsCompleted);
        Assert.False(executor.HasActiveExecution);
        Assert.False(f.Source.Termination.IsCompleted);
        Assert.False(f.Lease.Completed.IsCompleted);
    }

    [Fact]
    public async Task Stop_Preserves_All_Original_Write_And_Cancellation_Root_Errors_And_Joins_Callbacks()
    {
        await using Fixture f = await Fixture.CreateAsync();
        Producer candidate = f.Producer();
        DisplaySwitchExecutor executor = f.Executor((_, _) => Task.FromResult<IVideoFrameProducer>(candidate));
        Receipt receipt = await f.Ready(executor, candidate);
        Gate callback = f.Gate();
        TaskCompletionSource writing = Signal(), original = Signal();
        f.ReleaseBeforeStop(() => original.TrySetResult());
        IOException first = new("原写入第一根。"), second = new("原写入第二根。"), third = new("原写入第三根。");
        InvalidOperationException cancelFirst = new("取消第一根。"), cancelSecond = new("取消第二根。");
        Task<ReplyResult> reply = f.Send(executor, receipt, (_, token) =>
        {
            f.DisposeAfterJoin(token.Register(() => throw cancelFirst));
            f.DisposeAfterJoin(token.Register(() => throw cancelSecond));
            f.DisposeAfterJoin(token.Register(callback.Block));
            writing.TrySetResult();
            return original.Task;
        });
        await Join(writing.Task);
        executor.RequestStop();
        await Join(callback.Entered.Task);
        Assert.False(executor.Completion.IsCompleted);
        Assert.False(reply.IsCompleted);
        original.SetException([first, new AggregateException(second, third)]);
        var replyError = Assert.IsType<AggregateException>(await Observe(reply));
        Assert.Equal(3, replyError.Flatten().InnerExceptions.Count);
        foreach (Exception expected in new Exception[] { first, second, third })
            Assert.Contains(expected, replyError.Flatten().InnerExceptions);
        Assert.False(executor.Completion.IsCompleted);
        callback.Release();
        var completionError = Assert.IsType<AggregateException>(await Observe(executor.Completion));
        Assert.Equal(5, completionError.Flatten().InnerExceptions.Count);
        foreach (Exception expected in new Exception[] { first, second, third, cancelFirst, cancelSecond })
            Assert.Contains(expected, completionError.Flatten().InnerExceptions);
    }

    [Theory]
    [InlineData("withdraw")]
    [InlineData("source-replace")]
    public async Task Successful_Original_Write_After_Invalidation_Keeps_Completed_Fact_But_Not_Current_Snapshot(string reason)
    {
        await using Fixture f = await Fixture.CreateAsync();
        Producer candidate = f.Producer();
        DisplaySwitchExecutor executor = f.Executor((_, _) => Task.FromResult<IVideoFrameProducer>(candidate));
        Receipt receipt = await f.Ready(executor, candidate);
        Gate writing = f.Gate();
        Task<ReplyResult> reply = f.Send(executor, receipt, (_, token) => writing.HoldAsync(token));
        await Join(writing.Entered.Task);
        f.Invalidate(reason, executor);
        Assert.False(reply.IsCompleted);
        Assert.True(executor.HasActiveExecution);
        writing.Release();
        Assert.Equal(new ReplyResult(true, true, false), await Join(reply));
        Assert.NotNull(Settled(Settled(receipt.Binding)!.FirstSend));
        Assert.False(executor.StopRequired.IsCompleted);
    }

    [Fact]
    public async Task Reply_Completion_Current_Flag_Is_Only_A_Snapshot_And_Is_Not_Retroactively_Changed()
    {
        await using Fixture f = await Fixture.CreateAsync();
        Producer candidate = f.Producer();
        DisplaySwitchExecutor executor = f.Executor((_, _) => Task.FromResult<IVideoFrameProducer>(candidate));
        Receipt receipt = await f.Ready(executor, candidate);
        ReplyResult result = await Join(f.Send(executor, receipt, (_, _) => Task.CompletedTask));
        f.Publication.Dispose();
        executor.RequestStop();
        await Join(executor.Completion);
        Assert.Equal(new ReplyResult(true, true, true), result);
    }

    [Fact]
    public async Task Invalid_Arguments_Do_Not_Consume_Reply_Eligibility_Or_Release_Reservation()
    {
        await using Fixture f = await Fixture.CreateAsync();
        Producer candidate = f.Producer();
        DisplaySwitchExecutor executor = f.Executor((_, _) => Task.FromResult<IVideoFrameProducer>(candidate));
        Receipt receipt = await f.Ready(executor, candidate);
        int calls = 0;
        Task Write(DisplayChangedFrame _, CancellationToken __)
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        }
        Assert.Throws<ArgumentNullException>(() => executor.TrySendReply(null!, 90, 5678, Write, out _));
        Assert.Throws<ArgumentNullException>(() => executor.TrySendReply(receipt, 90, 5678, null!, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => executor.TrySendReply(receipt, -1, 5678, Write, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => executor.TrySendReply(receipt, 90, -1, Write, out _));
        Assert.Equal(0, calls);
        AssertBusy(f, executor, 1);
        Assert.Equal(new ReplyResult(true, true, true), await Join(f.Send(executor, receipt, Write)));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Null_Original_Task_Or_Synchronous_Writer_Fault_Stops_And_Is_Fully_Observed(bool throws)
    {
        await using Fixture f = await Fixture.CreateAsync();
        Producer candidate = f.Producer();
        DisplaySwitchExecutor executor = f.Executor((_, _) => Task.FromResult<IVideoFrameProducer>(candidate));
        Receipt receipt = await f.Ready(executor, candidate);
        IOException failure = new("回执写者同步失败。");
        int calls = 0;
        Task<ReplyResult> reply = f.Send(executor, receipt, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            if (throws) throw failure;
            return null!;
        });
        var error = Assert.IsType<AggregateException>(await Observe(reply));
        var completionError = Assert.IsType<AggregateException>(await Observe(executor.Completion));
        Exception leaf = Assert.Single(error.Flatten().InnerExceptions);
        if (throws) Assert.Same(failure, leaf);
        else Assert.IsType<InvalidOperationException>(leaf);
        Assert.Contains(leaf, completionError.Flatten().InnerExceptions);
        Assert.Equal(1, calls);
        AssertRejected(f, executor, receipt, ReplyStatus.Unavailable);
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("tracked")]
    public async Task Old_Executor_Constructors_Do_Not_Enable_Replies_Or_Hold_Reservation_For_First_Send(string mode)
    {
        await using Fixture f = await Fixture.CreateAsync();
        Producer first = f.Producer(), second = f.Producer();
        int preparations = 0;
        DisplaySwitchExecutor executor = f.Executor((_, _) =>
            Task.FromResult<IVideoFrameProducer>(Interlocked.Increment(ref preparations) == 1 ? first : second), mode);
        Status status = executor.TryExecute(f.Request(0), out Task<Result>? execution, out Receipt? accepted);
        if (execution is not null) _ = f.Track(execution);
        Assert.Equal(Status.Accepted, status);
        Result result = await Join(Assert.IsAssignableFrom<Task<Result>>(execution));
        if (mode == "legacy")
        {
            Assert.Null(accepted);
            Assert.Null(result.SendReceipt);
        }
        else
        {
            Assert.NotNull(accepted);
            var sendReceipt = Assert.IsType<DisplaySwitchExecutor.RequestSendReceipt>(result.SendReceipt);
            Assert.Same(sendReceipt.Generation, Settled(accepted.Binding));
            Assert.False(sendReceipt.FirstSend.IsCompleted);
        }
        Assert.False(executor.HasActiveExecution);
        AssertRejected(f, executor, accepted ?? new Receipt(f.Request(0)), ReplyStatus.Unavailable);
        status = executor.TryExecute(f.Request(1), out Task<Result>? next);
        if (next is not null) _ = f.Track(next);
        Assert.Equal(Status.Accepted, status);
        Assert.True((await Join(Assert.IsAssignableFrom<Task<Result>>(next))).ReplacementCompleted);
        Assert.Equal(2, preparations);
        Assert.Equal(0, first.ReadCalls);
        Assert.Equal(0, second.ReadCalls);
    }

    [Fact]
    public async Task Original_Reply_Task_Still_Enforces_Single_Flight_After_Reservation_Is_Released()
    {
        await using Fixture f = await Fixture.CreateAsync();
        Producer first = f.Producer(), second = f.Producer();
        int preparations = 0;
        DisplaySwitchExecutor executor = f.Executor((_, _) =>
            Task.FromResult<IVideoFrameProducer>(Interlocked.Increment(ref preparations) == 1 ? first : second));
        Receipt receipt = await f.Ready(executor, first);
        Gate writing = f.Gate();
        Task<ReplyResult> reply = f.Send(executor, receipt, (_, token) => writing.HoldAsync(token));
        await Join(writing.Entered.Task);
        // 独立验证原 Task 门禁：主动释放真实预约，模拟 finally 已释槽但 worker 尚未退出的窗口。
        lock (ExecutorGate(executor))
        {
            object pending = typeof(DisplaySwitchExecutor).GetField("_pendingReply", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(executor)!;
            var reservation = Assert.IsAssignableFrom<IDisposable>(pending.GetType()
                .GetProperty("Reservation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pending));
            reservation.Dispose();
            Assert.False(reply.IsCompleted);
            Assert.True(executor.HasActiveExecution);
            AssertBusy(f, executor, 1);
        }
        writing.Release();
        Assert.Equal(new ReplyResult(true, true, false), await Join(reply));
        Receipt next = await f.Ready(executor, second, 1);
        Assert.Equal(new ReplyResult(true, true, true), await Join(f.Send(executor, next, (_, _) => Task.CompletedTask)));
    }

    private static object ExecutorGate(DisplaySwitchExecutor executor) => typeof(DisplaySwitchExecutor)
        .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(executor)!;

    private static void AssertFrame(DisplayChangedFrame frame, DisplaySwitchRequestFrame request, ulong firstFrameId, int width, int height)
    {
        Assert.Equal(request.SessionId, frame.SessionId);
        Assert.Equal(90, frame.Sequence);
        Assert.Equal(5678, frame.TimestampMs);
        Assert.Equal(request.Sequence, frame.RequestSequence);
        Assert.Equal(request.CatalogSequence, frame.CatalogSequence);
        Assert.Equal(request.DisplayId, frame.DisplayId);
        Assert.Equal(firstFrameId, frame.FirstFrameId);
        Assert.Equal(width, frame.Width);
        Assert.Equal(height, frame.Height);
    }

    private static void AssertBusy(Fixture f, DisplaySwitchExecutor executor, long sequence)
    {
        Status status = executor.TryExecute(f.Request(sequence), out Task<Result>? execution, out Receipt? receipt);
        if (execution is not null) _ = f.Track(execution);
        Assert.Equal(Status.Busy, status);
        Assert.Null(execution);
        Assert.Null(receipt);
    }

    private static void AssertRejected(Fixture f, DisplaySwitchExecutor executor, Receipt receipt, ReplyStatus expected)
    {
        int calls = 0;
        ReplyStatus status = executor.TrySendReply(receipt, 90, 5678, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        }, out Task<ReplyResult>? reply);
        if (reply is not null) f.Track(reply);
        Assert.Equal(expected, status);
        Assert.Null(reply);
        Assert.Equal(0, calls);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static T Settled<T>(Task<T> promise)
    {
        Assert.True(promise.IsCompletedSuccessfully);
        return promise.GetAwaiter().GetResult();
    }
    private static async Task Join(Task task) => await task.WaitAsync(Watchdog);
    private static async Task<T> Join<T>(Task<T> task) => await task.WaitAsync(Watchdog);
    private static async Task<Exception?> Observe(Task task)
    {
        try { await Join(task); return null; }
        catch (TimeoutException) { throw; }
        catch (Exception error) { return error; }
    }
    private static async Task Drain(Task task)
    {
        // 正文 watchdog 仅诊断；收尾必须完整等待原任务，不等待 Binding/FirstSend 等纯结果 promise。
        try { await task.ConfigureAwait(false); }
        catch (Exception) { /* 正文断言故障，清理继续 join 其余原根。 */ }
    }

    private sealed class Gate
    {
        private readonly TaskCompletionSource _release = Signal();
        internal TaskCompletionSource Entered { get; } = Signal();
        internal async Task HoldAsync(CancellationToken _)
        {
            Entered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
        }
        internal void Block()
        {
            Entered.TrySetResult();
            _release.Task.GetAwaiter().GetResult();
        }
        internal void Release() => _release.TrySetResult();
    }

    private sealed class Producer(EncodedFrame? frame = null) : IVideoFrameProducer
    {
        private readonly TaskCompletionSource _lifetime = Signal();
        private readonly TaskCompletionSource<EncodedFrame?> _next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;
        internal TaskCompletionSource NextReadEntered { get; } = Signal();
        internal int ReadCalls => Volatile.Read(ref _reads);
        public void Start() { }
        public ValueTask<EncodedFrame?> ReadNextAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _reads) == 1) return ValueTask.FromResult(frame);
            NextReadEntered.TrySetResult();
            return new(_next.Task);
        }
        internal void Release() => _next.TrySetResult(null);
        public Task StopAsync()
        {
            Release();
            _lifetime.TrySetResult();
            return _lifetime.Task;
        }
        public ValueTask DisposeAsync() => new(StopAsync());
        public Task Completion => _lifetime.Task;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly List<Task> _roots = [];
        private readonly List<Action> _releases = [];
        private readonly List<IDisposable> _afterJoin = [];
        private readonly List<EncodedFrame> _frames = [];
        private DisplaySwitchExecutor? _executor;
        private VideoFrameSender? _sender;
        private Fixture(bool blockVideo)
        {
            Video = new RecordingVideoStream { GateWriteAt = blockVideo ? 0 : int.MaxValue };
            byte[] token = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
            byte[] pin = Enumerable.Range(64, 32).Select(value => (byte)value).ToArray();
            byte[] nonce = Enumerable.Range(128, 16).Select(value => (byte)value).ToArray();
            IPAddress remote = IPAddress.Parse("192.168.10.20");
            Guid session = Guid.NewGuid();
            Registration = Registry.Register(session, Guid.NewGuid(), Guid.NewGuid(), "回执生命周期接缝测试",
                SessionPermission.ViewOnly, remote, 12345, token, new VideoAttachWindow(Clock, 15_000),
                pin, Control.Token, V2AgreementFixture.Create(session));
            ConnectionSecurityContext video = new(IPAddress.Parse("192.168.10.10"), remote, 23456, SslProtocols.Tls13, pin);
            // 真实登记/租约/发布/准入；没有真实 TLS、GDI、网络或双机依赖。
            byte[] transcript = new byte[83];
            Encoding.UTF8.GetBytes("LANREMOTE-VIDEO-V1\0").CopyTo(transcript, 0);
            Convert.FromHexString(session.ToString("N")).CopyTo(transcript, 19);
            nonce.CopyTo(transcript, 35);
            pin.CopyTo(transcript, 51);
            using HMACSHA256 hmac = new(token);
            Assert.Equal(VideoAttachStatus.Attached, Registry.TryAttachVideo(session, video, nonce,
                hmac.ComputeHash(transcript), default, out VideoAttachLease? lease));
            Lease = lease!;
            Source = new(session);
        }
        internal static async Task<Fixture> CreateAsync(bool blockVideo = false)
        {
            Fixture f = new(blockVideo);
            try
            {
                await Join(f.Track(f.Source.ReplaceAsync(new Producer())));
                f.Publication = f.Registry.PublishVideoSource(f.Lease, f.Source);
                f.Admission = f.Registration.CreateDisplaySwitchAdmission(f.Catalog);
                return f;
            }
            catch
            {
                await f.DisposeAsync();
                throw;
            }
        }
        internal SessionRegistry Registry { get; } = new();
        internal SessionRegistry.SessionRegistration Registration { get; }
        internal ReplaceableVideoFrameSource Source { get; }
        internal ManualDeadlineClock Clock { get; } = new();
        internal CancellationTokenSource Control { get; } = new();
        internal VideoAttachLease Lease { get; }
        internal RecordingVideoStream Video { get; }
        internal IDisposable Publication { get; private set; } = null!;
        internal Admission Admission { get; private set; } = null!;
        internal DisplayCatalogFrame Catalog { get; } = new(23, 44,
        [
            new DisplayInfo(new DisplayId(3), "主显示器", 0, 0, 1920, 1080, true),
            new DisplayInfo(new DisplayId(17), "左侧显示器", -1280, -200, 1280, 1024, false),
        ]);
        internal DisplaySwitchRequestFrame Request(long sequence) =>
            new(Registration.SessionId, sequence, 1234, Catalog.Sequence, new DisplayId(17));
        internal Producer Producer(int width = 640, int height = 360)
        {
            RecordingVideoOwner owner = new(32);
            VideoFrameTestData.Payload().CopyTo(owner.Bytes, 0);
            EncodedFrame frame = new(VideoCodec.Jpeg, width, height, 0x0123456789ABCDEFul, 1234, 60, owner, 5);
            _frames.Add(frame);
            Producer producer = new(frame);
            ReleaseBeforeStop(producer.Release);
            return producer;
        }
        internal Gate Gate()
        {
            Gate gate = new();
            ReleaseBeforeStop(gate.Release);
            return gate;
        }
        internal void ReleaseBeforeStop(Action release) => _releases.Add(release);
        internal void DisposeAfterJoin(IDisposable disposable)
        {
            lock (_afterJoin) _afterJoin.Add(disposable);
        }
        internal T Track<T>(T root) where T : Task
        {
            _roots.Add(root);
            return root;
        }
        internal DisplaySwitchExecutor Executor(Func<DisplayInfo, CancellationToken, Task<IVideoFrameProducer>> prepare, string mode = "reply") =>
            _executor = mode switch
            {
                "legacy" => Admission.CreateExecutor(prepare),
                "tracked" => Admission.CreateTrackedExecutor(prepare),
                _ => Admission.CreateReplyExecutor(prepare),
            };
        internal Task<Result> Execute(DisplaySwitchExecutor executor, long sequence, out Receipt receipt)
        {
            Status status = executor.TryExecute(Request(sequence), out Task<Result>? execution, out Receipt? accepted);
            if (execution is not null) Track(execution);
            Assert.Equal(Status.Accepted, status);
            receipt = Assert.IsType<Receipt>(accepted);
            return Assert.IsAssignableFrom<Task<Result>>(execution);
        }
        internal async Task<Receipt> Ready(DisplaySwitchExecutor executor, Producer producer, long sequence = 0)
        {
            Result result = await Join(Execute(executor, sequence, out Receipt receipt));
            Assert.True(result.Transferred && result.ReplacementCompleted);
            var send = Assert.IsType<DisplaySwitchExecutor.RequestSendReceipt>(result.SendReceipt);
            Assert.Same(send.Generation, Settled(receipt.Binding));
            StartSender();
            // sender 原写入完成后才会读取下一帧；阻塞该读取，避免 EOF 将来源封口。
            await Join(producer.NextReadEntered.Task);
            Assert.NotNull(Settled(send.FirstSend));
            Assert.False(Source.Termination.IsCompleted);
            return receipt;
        }
        internal void StartSender()
        {
            if (_sender is not null) return;
            _sender = new VideoFrameSender(70, Source);
            VideoFrameWriter writer = new(Video);
            Track(_sender.SendAsync(Registration.SessionId, writer, Source, Clock, TimeSpan.FromSeconds(10), Control.Token));
        }
        internal Task<ReplyResult> Send(DisplaySwitchExecutor executor, Receipt receipt,
            Func<DisplayChangedFrame, CancellationToken, Task> write)
        {
            ReplyStatus status = executor.TrySendReply(receipt, 90, 5678, write, out Task<ReplyResult>? reply);
            if (reply is not null) Track(reply);
            Assert.Equal(ReplyStatus.Accepted, status);
            return Assert.IsAssignableFrom<Task<ReplyResult>>(reply);
        }
        internal void Invalidate(string reason, DisplaySwitchExecutor executor)
        {
            switch (reason)
            {
                case "withdraw": Publication.Dispose(); break;
                case "unregister": Registration.Dispose(); break;
                case "control-cancel": Control.Cancel(); break;
                case "lease-complete": Lease.Complete(); break;
                case "admission-close": Admission.Dispose(); break;
                case "source-stop": Track(Source.DisposeAsync().AsTask()); break;
                case "source-replace": Track(Source.ReplaceAsync(Producer())); break;
                case "request-stop": executor.RequestStop(); break;
                default: throw new ArgumentOutOfRangeException(nameof(reason));
            }
        }
        public async ValueTask DisposeAsync()
        {
            // 先放全部可阻塞入口，再独立通知各 owner，最后直接完整 join 原 sender/source/executor/reply。
            foreach (Action release in _releases) release();
            Video.ReleaseWrite();
            Task closing = Source.DisposeAsync().AsTask();
            Control.Cancel();
            _executor?.RequestStop();
            Publication?.Dispose();
            Admission?.Dispose();
            Registration.Dispose();
            try
            {
                foreach (Task root in _roots) await Drain(root);
                await Drain(closing);
                await Drain(Source.Completion);
                if (_executor is not null) await Drain(_executor.Completion);
                lock (_afterJoin)
                    foreach (IDisposable disposable in _afterJoin) disposable.Dispose();
                foreach (EncodedFrame frame in _frames) frame.Dispose();
                Assert.Equal(0, Clock.TimerCount);
            }
            finally
            {
                Lease.Complete();
                Control.Dispose();
                if (_sender is null) Video.Dispose();
            }
        }
    }
}
