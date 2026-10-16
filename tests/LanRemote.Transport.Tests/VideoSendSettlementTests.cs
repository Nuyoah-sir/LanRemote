using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;
using Admission = LanRemote.Transport.SessionRegistry.SessionRegistration.DisplaySwitchAdmission;
using Result = LanRemote.Transport.DisplaySwitchExecutor.ExecutionResult;
using Status = LanRemote.Transport.SessionRegistry.SessionRegistration.DisplaySwitchAdmissionStatus;

namespace LanRemote.Transport.Tests;

public sealed class VideoSendSettlementTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(20);

    [Theory]
    [InlineData(1, "success")]
    [InlineData(2, "success")]
    [InlineData(3, "success")]
    [InlineData(1, "fault")]
    [InlineData(2, "fault")]
    [InlineData(3, "fault")]
    [InlineData(1, "cancel")]
    [InlineData(2, "cancel")]
    [InlineData(3, "cancel")]
    [InlineData(1, "deadline")]
    [InlineData(2, "deadline")]
    [InlineData(3, "deadline")]
    [InlineData(1, "closed")]
    [InlineData(2, "closed")]
    [InlineData(3, "closed")]
    public async Task Sealed_First_Send_Waits_For_Original_Io_After_Replacement_And_Source_Completion(int boundary, string outcome)
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        using CancellationTokenSource caller = new();
        ManualDeadlineClock clock = new();
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = Frame(owner, 640, 360);
        Producer old = Frames(frame), next = Frames();
        RecordingVideoStream resource = new();
        Gate gate = new();
        IOException failure = new("原发送 I/O 尾部失败。");
        HookStream stream = new(resource, async (operation, token) =>
        {
            if (operation != boundary) return;
            await gate.HoldAsync(token);
            if (outcome == "fault") throw failure;
        });
        using VideoFrameWriter writer = new(stream);
        VideoFrameSender sender = new(17, source);
        Task installing = source.ReplaceAsync(old, out var oldGeneration);
        Task<VideoFrameSender.SentFrame?> first = oldGeneration.FirstSend;
        Task? replacing = null, sending = null, closing = null;
        try
        {
            await Join(installing);
            sending = sender.SendAsync(session, writer, source, clock, Budget, caller.Token);
            await Join(gate.Entered.Task);
            Assert.False(first.IsCompleted);
            Assert.Null(sender.LastSuccessfulGeneration);
            Assert.Equal(boundary == 1 ? 40 : 45, resource.BytesWritten);
            Assert.Equal(boundary == 3 ? 1 : 0, resource.FlushCalls);

            replacing = source.ReplaceAsync(next, out var nextGeneration);
            await Join(replacing);
            Assert.False(first.IsCompleted);
            Assert.False(nextGeneration.FirstSend.IsCompleted);
            Assert.Equal(1, old.StopCalls);
            Assert.Equal(1, old.DisposeCalls);
            Assert.Equal(0, next.ReadCalls);

            closing = source.DisposeAsync().AsTask();
            Assert.Same(source.Completion, closing);
            await Join(closing);
            Assert.True(source.Completion.IsCompletedSuccessfully);
            Assert.Null(Settled(nextGeneration.FirstSend));
            Assert.False(first.IsCompleted);
            Assert.False(sending.IsCompleted);
            Assert.Equal(0, owner.DisposeCalls);

            if (outcome == "cancel") caller.Cancel();
            if (outcome == "deadline") clock.Advance(Budget);
            if (outcome == "closed") writer.Dispose();
            Assert.Equal(outcome is "cancel" or "deadline", gate.Token.IsCancellationRequested);
            Assert.False(first.IsCompleted);
            Assert.False(sending.IsCompleted);
            gate.Release();
            Exception? error = await Observe(sending);
            if (outcome == "success")
            {
                Assert.Null(error);
                Assert.Equal(new VideoFrameSender.SentFrame(17, 640, 360), Settled(first));
                Assert.Same(oldGeneration, sender.LastSuccessfulGeneration!.Generation);
                Assert.Same(Settled(first), sender.LastSuccessfulGeneration.First);
            }
            else
            {
                if (outcome == "fault") Assert.Same(failure, error);
                else if (outcome == "closed") Assert.IsType<ObjectDisposedException>(error);
                else Assert.IsAssignableFrom<OperationCanceledException>(error);
                Assert.Null(Settled(first));
                Assert.Null(sender.LastSuccessfulGeneration);
            }
            Assert.Same(first, oldGeneration.FirstSend);
            Assert.Null(Settled(nextGeneration.FirstSend));
            Assert.Equal(1, old.ReadCalls);
            Assert.Equal(0, next.ReadCalls);
            Assert.Equal(1, owner.DisposeCalls);
            Assert.Equal(1, resource.DisposeCalls);
            Assert.Equal(0, clock.TimerCount);
        }
        finally
        {
            gate.Release();
            await Finish(source, caller, installing, replacing, closing, sending);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Final_Monotonic_Budget_Check_Settles_Without_Timer_Dispatch(int offsetTicks)
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        ManualDeadlineClock clock = new();
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = Frame(owner, 800, 600);
        RecordingVideoStream resource = new();
        HookStream stream = new(resource, (operation, _) =>
        {
            if (operation == 3)
            {
                clock.AdvanceUtc(TimeSpan.FromDays(-365));
                clock.Advance(TimeSpan.FromTicks(Budget.Ticks + offsetTicks), fireTimers: false);
            }
            return Task.CompletedTask;
        });
        using VideoFrameWriter writer = new(stream);
        VideoFrameSender sender = new(8, source);
        Task installing = source.ReplaceAsync(Frames(frame), out var generation);
        Task? sending = null;
        try
        {
            await Join(installing);
            sending = sender.SendAsync(session, writer, source, clock, Budget, default);
            Exception? error = await Observe(sending);
            if (offsetTicks < 0)
            {
                Assert.Null(error);
                Assert.Equal(new VideoFrameSender.SentFrame(8, 800, 600), Settled(generation.FirstSend));
            }
            else
            {
                Assert.IsAssignableFrom<OperationCanceledException>(error);
                Assert.Null(sender.LastSuccessfulGeneration);
                Assert.False(generation.FirstSend.IsCompleted);
                await Join(source.DisposeAsync().AsTask());
                Assert.Null(Settled(generation.FirstSend));
            }
            Assert.Equal(45, resource.BytesWritten);
            Assert.Equal(1, resource.FlushCalls);
            Assert.All(resource.Tokens, token => Assert.False(token.IsCancellationRequested));
            Assert.Equal(1, owner.DisposeCalls);
            Assert.Equal(0, clock.TimerCount);
        }
        finally { await Finish(source, null, installing, sending); }
    }

    [Fact]
    public async Task Failed_Attempt_Without_Sealing_Does_Not_Publish_Null()
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = Frame(owner, 640, 360);
        IOException failure = new("未封口代际的发送失败。");
        RecordingVideoStream stream = new() { FailWriteAt = 0, WriteFailure = failure };
        using VideoFrameWriter writer = new(stream);
        VideoFrameSender sender = new(0, source);
        Task installing = source.ReplaceAsync(Frames(frame), out var generation);
        Task? sending = null;
        try
        {
            await Join(installing);
            sending = sender.SendAsync(session, writer, source, new ManualDeadlineClock(), Budget, default);
            Assert.Same(failure, await Observe(sending));
            Assert.False(generation.FirstSend.IsCompleted);
            Assert.False(source.Termination.IsCompleted);
            await Join(source.DisposeAsync().AsTask());
            Assert.Null(Settled(generation.FirstSend));
            Assert.Null(sender.LastSuccessfulGeneration);
        }
        finally { await Finish(source, null, installing, sending); }
    }

    [Fact]
    public async Task First_Send_Is_Stable_Across_More_Frames_And_Later_Generation_Snapshots()
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        RecordingVideoOwner a = new(32), b = new(32), c = new(32);
        using EncodedFrame first = Frame(a, 640, 360);
        using EncodedFrame second = Frame(b, 800, 600);
        using EncodedFrame third = Frame(c, 1920, 1080);
        Gate secondTail = new();
        ReadSlot nextRead = new(), eof = new();
        Producer old = Frames(first, second);
        Producer next = new((read, _) => (read == 0 ? nextRead : eof).Read());
        RecordingVideoStream resource = new();
        HookStream stream = new(resource, (operation, token) => operation == 6 ? secondTail.HoldAsync(token) : Task.CompletedTask);
        using VideoFrameWriter writer = new(stream);
        VideoFrameSender sender = new(20, source);
        Task installing = source.ReplaceAsync(old, out var oldGeneration);
        Task<VideoFrameSender.SentFrame?> oldFirst = oldGeneration.FirstSend;
        Task? replacing = null, sending = null;
        try
        {
            await Join(installing);
            sending = sender.SendAsync(session, writer, source, new ManualDeadlineClock(), Budget, default);
            await Join(secondTail.Entered.Task);
            var sent = Settled(oldFirst);
            var firstSnapshot = sender.LastSuccessfulGeneration!;
            Assert.Equal(new VideoFrameSender.SentFrame(20, 640, 360), sent);
            replacing = source.ReplaceAsync(next, out var nextGeneration);
            await Join(replacing);
            Assert.Same(sent, Settled(oldFirst));
            secondTail.Release();
            await Join(nextRead.Entered.Task);
            var oldSnapshot = sender.LastSuccessfulGeneration!;
            Assert.NotSame(firstSnapshot, oldSnapshot);
            Assert.Same(sent, oldSnapshot.First);
            Assert.Equal(new VideoFrameSender.SentFrame(21, 800, 600), oldSnapshot.Latest);
            Assert.Equal(new VideoFrameSender.SentFrame(20, 640, 360), firstSnapshot.Latest);
            nextRead.Result.SetResult(third);
            await Join(eof.Entered.Task);
            var current = sender.LastSuccessfulGeneration!;
            Assert.Same(nextGeneration, current.Generation);
            Assert.Equal(new VideoFrameSender.SentFrame(22, 1920, 1080), Settled(nextGeneration.FirstSend));
            Assert.Same(oldGeneration, oldSnapshot.Generation);
            Assert.Same(oldFirst, oldGeneration.FirstSend);
            Assert.Same(sent, Settled(oldFirst));
            eof.Result.SetResult(null);
            await Join(sending);
            Assert.Same(current, sender.LastSuccessfulGeneration);
            Assert.Same(sent, Settled(oldFirst));
            Assert.Equal(1, a.DisposeCalls);
            Assert.Equal(1, b.DisposeCalls);
            Assert.Equal(1, c.DisposeCalls);
        }
        finally
        {
            secondTail.Release();
            nextRead.Result.TrySetResult(null);
            eof.Result.TrySetResult(null);
            await Finish(source, null, installing, replacing, sending);
        }
    }

    [Theory]
    [InlineData("start")]
    [InlineData("read")]
    [InlineData("retirement")]
    public async Task Zero_Committed_Frame_Seals_Without_Joining_Start_Read_Or_Retirement(string blocked)
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        Gate start = new();
        ReadSlot read = new();
        TaskCompletionSource retirement = Signal();
        RecordingVideoOwner owner = new(32);
        using EncodedFrame late = Frame(owner, 320, 200);
        Producer producer = new((_, _) => read.Read())
        {
            StartAction = blocked == "start" ? start.Block : null,
            Retirement = blocked == "retirement" ? retirement.Task : null,
        };
        Task installing = source.ReplaceAsync(producer, out var generation);
        Task<EncodedFrame?>? reading = null;
        ReplaceableVideoFrameSource.ReadOrigin? origin = null;
        Task? closing = null;
        try
        {
            if (blocked == "start") await Join(start.Entered.Task);
            else await Join(installing);
            if (blocked == "read")
            {
                reading = source.ReadForSendAsync(session, default, out origin, settleSend: true).AsTask();
                await Join(read.Entered.Task);
                Assert.Null(origin.Generation);
            }
            Assert.False(generation.FirstSend.IsCompleted);
            closing = source.DisposeAsync().AsTask();
            Assert.Null(Settled(generation.FirstSend));
            Assert.False(closing.IsCompleted);
            Assert.False(source.Completion.IsCompleted);
            if (blocked == "start") Assert.False(installing.IsCompleted);
            if (blocked == "read") Assert.False(reading!.IsCompleted);
            start.Release();
            read.Result.TrySetResult(late);
            retirement.TrySetResult();
            await Join(closing);
            if (blocked == "start") Assert.IsType<InvalidOperationException>(await Observe(installing));
            if (blocked == "read")
            {
                Assert.Null(await Join(reading!));
                Assert.Null(origin!.Generation);
                Assert.Equal(1, owner.DisposeCalls);
            }
            Assert.Null(Settled(generation.FirstSend));
        }
        finally
        {
            start.Release();
            read.Result.TrySetResult(null);
            retirement.TrySetResult();
            await Finish(source, null, installing, reading, closing);
            origin?.CompleteSend(null);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Each_Committed_Read_Settles_Once_And_Cannot_Finish_Another_Pending_Attempt(bool lastSucceeds)
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        RecordingVideoOwner a = new(32), b = new(32);
        using EncodedFrame firstFrame = Frame(a, 640, 360);
        using EncodedFrame secondFrame = Frame(b, 800, 600);
        Task installing = source.ReplaceAsync(Frames(firstFrame, secondFrame), out var generation);
        Task<EncodedFrame?>? firstRead = null, secondRead = null;
        ReplaceableVideoFrameSource.ReadOrigin? first = null, second = null;
        VideoFrameSender.SentFrame success = new(31, 800, 600);
        try
        {
            await Join(installing);
            firstRead = source.ReadForSendAsync(session, default, out first, settleSend: true).AsTask();
            Assert.Same(firstFrame, await Join(firstRead));
            secondRead = source.ReadForSendAsync(session, default, out second, settleSend: true).AsTask();
            Assert.Same(secondFrame, await Join(secondRead));
            Assert.Same(generation, first.Generation);
            Assert.Same(generation, second.Generation);
            await Join(source.DisposeAsync().AsTask());
            Assert.False(generation.FirstSend.IsCompleted);
            first.CompleteSend(null);
            first.CompleteSend(null);
            first.CompleteSend(new(30, 640, 360));
            Assert.False(generation.FirstSend.IsCompleted);
            second.CompleteSend(lastSucceeds ? success : null);
            Assert.Same(lastSucceeds ? success : null, Settled(generation.FirstSend));
            second.CompleteSend(null);
            second.CompleteSend(new(99, 1920, 1080));
            Assert.Same(lastSucceeds ? success : null, Settled(generation.FirstSend));
            Assert.Equal(0, a.DisposeCalls);
            Assert.Equal(0, b.DisposeCalls);
        }
        finally
        {
            await Finish(source, null, installing, firstRead, secondRead);
            first?.CompleteSend(null);
            second?.CompleteSend(null);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_Read_And_Default_Origin_Do_Not_Register_Send_Attempts(bool withOrigin)
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = Frame(owner, 640, 360);
        Task installing = source.ReplaceAsync(Frames(frame), out var generation);
        Task<EncodedFrame?>? reading = null;
        ReplaceableVideoFrameSource.ReadOrigin? origin = null;
        try
        {
            await Join(installing);
            reading = (withOrigin
                ? source.ReadForSendAsync(session, default, out origin)
                : source.ReadNextAsync(session, default)).AsTask();
            Assert.Same(frame, await Join(reading));
            if (withOrigin) Assert.Same(generation, origin!.Generation);
            Assert.False(generation.FirstSend.IsCompleted);
            origin?.CompleteSend(new(1, 640, 360));
            Assert.False(generation.FirstSend.IsCompleted);
            await Join(source.DisposeAsync().AsTask());
            Assert.Null(Settled(generation.FirstSend));
            Assert.Equal(0, owner.DisposeCalls);
        }
        finally
        {
            await Finish(source, null, installing, reading);
            origin?.CompleteSend(null);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_Senders_Do_Not_Complete_FirstSend_With_A_Success(bool sequential)
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = Frame(owner, 640, 360);
        RecordingVideoStream stream = new();
        using VideoFrameWriter writer = new(stream);
        VideoFrameSender sender = sequential ? new(17) : new();
        Task installing = source.ReplaceAsync(Frames(frame), out var generation);
        Task? sending = null;
        try
        {
            await Join(installing);
            sending = sender.SendAsync(session, writer, source, new ManualDeadlineClock(), Budget, default);
            await Join(sending);
            Assert.Null(Settled(generation.FirstSend));
            Assert.Null(sender.LastSuccessfulGeneration);
            Assert.Equal(45, stream.BytesWritten);
            Assert.Equal(1, owner.DisposeCalls);
        }
        finally { await Finish(source, null, installing, sending); }
    }

    [Fact]
    public async Task Owner_Cleanup_Failure_Does_Not_Revoke_An_Already_Settled_Success()
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        IOException cleanup = new("首帧成功后的 owner 清理失败。");
        RecordingVideoOwner owner = new(32) { DisposeFailure = cleanup };
        using EncodedFrame frame = Frame(owner, 800, 600);
        using VideoFrameWriter writer = new(new RecordingVideoStream());
        VideoFrameSender sender = new(55, source);
        Task installing = source.ReplaceAsync(Frames(frame), out var generation);
        Task<VideoFrameSender.SentFrame?> first = generation.FirstSend;
        bool settledAtCleanup = false;
        owner.BeforeDispose = () => settledAtCleanup = first.IsCompletedSuccessfully;
        Task? sending = null;
        try
        {
            await Join(installing);
            sending = sender.SendAsync(session, writer, source, new ManualDeadlineClock(), Budget, default);
            Assert.Same(cleanup, await Observe(sending));
            Assert.True(settledAtCleanup);
            Assert.Equal(new VideoFrameSender.SentFrame(55, 800, 600), Settled(first));
            Assert.Same(Settled(first), sender.LastSuccessfulGeneration!.First);
            await Join(source.DisposeAsync().AsTask());
            Assert.Same(first, generation.FirstSend);
            Assert.Equal(new VideoFrameSender.SentFrame(55, 800, 600), Settled(first));
            Assert.Same(cleanup, Assert.Single(sender.CleanupErrors));
            Assert.Null(sender.SendFailure);
        }
        finally
        {
            await Finish(source, null, installing, sending);
            owner.DisposeFailure = null;
        }
    }

    [Fact]
    public async Task Tracked_Executor_Receipts_Bind_Exact_Requests_And_Generations_Without_A_Send_Barrier()
    {
        await using Fixture f = await Fixture.CreateAsync();
        RecordingVideoOwner a = new(32), b = new(32);
        using EncodedFrame firstFrame = Frame(a, 640, 360);
        using EncodedFrame secondFrame = Frame(b, 1920, 1080);
        Gate wire = new();
        ReadSlot nextRead = new(), eof = new();
        Producer firstProducer = Frames(firstFrame);
        Producer nextProducer = new((read, _) => (read == 0 ? nextRead : eof).Read());
        int prepared = 0;
        DisplaySwitchExecutor executor = f.Executor((target, _) =>
        {
            Assert.Same(f.Catalog.Displays[1], target);
            return Task.FromResult<IVideoFrameProducer>(Interlocked.Increment(ref prepared) == 1 ? firstProducer : nextProducer);
        });
        RecordingVideoStream resource = new();
        using VideoFrameWriter writer = new(new HookStream(resource,
            (operation, token) => operation == 3 ? wire.HoldAsync(token) : Task.CompletedTask));
        VideoFrameSender sender = new(70, f.Source);
        Task? sending = null;
        try
        {
            DisplaySwitchRequestFrame requestA = f.Request(0), requestB = f.Request(1);
            Task<Result> executionA = f.Execute(executor, requestA, out var acceptedA);
            Assert.Same(requestA, acceptedA.Request);
            Task<ReplaceableVideoFrameSource.GenerationIdentity?> bindingA = acceptedA.Binding;
            Result resultA = await Join(executionA);
            var receiptA = Assert.IsType<DisplaySwitchExecutor.RequestSendReceipt>(resultA.SendReceipt);
            Assert.Same(receiptA.Generation, Settled(bindingA));
            Assert.Same(bindingA, acceptedA.Binding);
            Assert.True(resultA.Transferred && resultA.ReplacementCompleted);
            Assert.Same(requestA, receiptA.Request);
            Assert.Same(receiptA.Generation.FirstSend, receiptA.FirstSend);
            Task<VideoFrameSender.SentFrame?> firstA = receiptA.FirstSend;
            Assert.False(firstA.IsCompleted);
            Assert.False(executor.HasActiveExecution);
            Assert.Equal(0, firstProducer.ReadCalls);

            sending = f.Track(sender.SendAsync(f.Registration.SessionId, writer, f.Source, f.Clock, Budget, f.Control.Token));
            await Join(wire.Entered.Task);
            Task<Result> executionB = f.Execute(executor, requestB, out var acceptedB);
            Assert.Same(requestB, acceptedB.Request);
            Task<ReplaceableVideoFrameSource.GenerationIdentity?> bindingB = acceptedB.Binding;
            Assert.NotSame(executionA, executionB);
            var resultB = await Join(executionB);
            var receiptB = Assert.IsType<DisplaySwitchExecutor.RequestSendReceipt>(resultB.SendReceipt);
            Assert.Same(receiptB.Generation, Settled(bindingB));
            Assert.Same(bindingB, acceptedB.Binding);
            Assert.Same(receiptA.Generation, Settled(bindingA));
            Assert.Same(requestB, receiptB.Request);
            Assert.NotSame(receiptA.Generation, receiptB.Generation);
            Assert.Same(receiptB.Generation.FirstSend, receiptB.FirstSend);
            Assert.False(firstA.IsCompleted);
            Assert.False(receiptB.FirstSend.IsCompleted);
            Assert.Equal(0, nextProducer.ReadCalls);
            Assert.Equal(1, firstProducer.StopCalls);
            Assert.Equal(1, firstProducer.DisposeCalls);
            Assert.False(executor.HasActiveExecution);

            Task completion = executor.Completion;
            executor.RequestStop();
            Assert.Same(completion, executor.DisposeAsync().AsTask());
            await Join(completion);
            Assert.False(sending.IsCompleted);
            Assert.False(firstA.IsCompleted);
            Assert.False(receiptB.FirstSend.IsCompleted);
            Assert.Equal(0, nextProducer.StopCalls);
            Assert.False(f.Source.Termination.IsCompleted);
            Assert.False(f.Source.Completion.IsCompleted);
            Assert.False(f.Lease.Completed.IsCompleted);
            Assert.False(f.Lease.Revoked.IsCompleted);
            Assert.Equal(1, f.Registry.ActiveSessionCount);
            Assert.Same(f.Lease.Completed, f.Registration.VideoCompletion);

            wire.Release();
            await Join(nextRead.Entered.Task);
            var oldSuccess = sender.LastSuccessfulGeneration!;
            Assert.Same(receiptA.Generation, oldSuccess.Generation);
            Assert.Equal(new VideoFrameSender.SentFrame(70, 640, 360), Settled(firstA));
            nextRead.Result.SetResult(secondFrame);
            await Join(eof.Entered.Task);
            var current = sender.LastSuccessfulGeneration!;
            Assert.Same(receiptB.Generation, current.Generation);
            Assert.Equal(new VideoFrameSender.SentFrame(71, 1920, 1080), Settled(receiptB.FirstSend));
            Assert.Same(firstA, receiptA.FirstSend);
            Assert.Same(Settled(firstA), oldSuccess.First);
            Assert.Same(requestA, receiptA.Request);
            eof.Result.SetResult(null);
            await Join(sending);
            Assert.Same(firstA, receiptA.Generation.FirstSend);
            Assert.Equal(2, prepared);
            Assert.Equal(0, f.Clock.TimerCount);
        }
        finally
        {
            wire.Release();
            nextRead.Result.TrySetResult(null);
            eof.Result.TrySetResult(null);
            await Finish(f.Source, f.Control, sending);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_Executor_Still_Returns_The_Original_Result_Without_A_Receipt(bool useThirdOut)
    {
        await using Fixture f = await Fixture.CreateAsync();
        Producer candidate = Frames();
        DisplaySwitchExecutor executor = f.Executor((_, _) => Task.FromResult<IVideoFrameProducer>(candidate), tracked: false);
        Task<Result> execution;
        if (useThirdOut)
        {
            Status status = executor.TryExecute(f.Request(0), out var original, out var receipt);
            if (original is not null) _ = f.Track(original);
            Assert.Equal(Status.Accepted, status);
            Assert.NotNull(original);
            Assert.Null(receipt);
            execution = original;
        }
        else execution = f.Execute(executor, f.Request(0));
        Result result = await Join(execution);
        Assert.Equal(new Result(true, true), result);
        Assert.Null(result.SendReceipt);
        Assert.Equal(0, candidate.ReadCalls);
        Assert.False(executor.HasActiveExecution);
        executor.RequestStop();
        await Join(executor.Completion);
        Assert.Equal(0, candidate.StopCalls);
        Assert.False(f.Source.Termination.IsCompleted);
        Assert.False(f.Lease.Completed.IsCompleted);
    }

    [Theory]
    [InlineData("withdraw")]
    [InlineData("control-cancel")]
    [InlineData("unregister")]
    [InlineData("lease-complete")]
    [InlineData("admission-close")]
    [InlineData("source-stop")]
    public async Task Invalid_Parent_Synchronously_Rejects_Without_An_Execution_Or_Receipt(string reason)
    {
        await using Fixture f = await Fixture.CreateAsync();
        int prepared = 0;
        DisplaySwitchExecutor executor = f.Executor((_, _) =>
        {
            Interlocked.Increment(ref prepared);
            return Task.FromResult<IVideoFrameProducer>(Frames());
        });
        f.Invalidate(reason);
        Status status = executor.TryExecute(f.Request(0), out Task<Result>? execution, out var receipt);
        if (execution is not null) _ = f.Track(execution);
        Assert.Equal(Status.Unavailable, status);
        Assert.Null(execution);
        Assert.Null(receipt);
        Assert.Equal(0, prepared);
        Assert.False(executor.HasActiveExecution);
    }

    [Theory]
    [InlineData("withdraw")]
    [InlineData("control-cancel")]
    [InlineData("unregister")]
    [InlineData("lease-complete")]
    [InlineData("admission-close")]
    [InlineData("source-stop")]
    public async Task Parent_Invalidation_During_Prepare_Cannot_Return_A_Success_Receipt(string reason)
    {
        await using Fixture f = await Fixture.CreateAsync();
        Producer candidate = Frames();
        TaskCompletionSource entered = Signal();
        TaskCompletionSource<IVideoFrameProducer> prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = f.Track(prepared.Task);
        DisplaySwitchExecutor executor = f.Executor((_, _) =>
        {
            entered.TrySetResult();
            return prepared.Task;
        });
        DisplaySwitchRequestFrame request = f.Request(0);
        Task<Result> execution = f.Execute(executor, request, out var receipt);
        Task<ReplaceableVideoFrameSource.GenerationIdentity?> binding = receipt.Binding;
        try
        {
            await Join(entered.Task);
            Assert.Same(request, receipt.Request);
            Assert.False(binding.IsCompleted);
            f.Invalidate(reason);
            prepared.SetResult(candidate);
            Assert.IsType<AggregateException>(await Observe(execution));
            Assert.False(execution.IsCompletedSuccessfully);
            Assert.Same(binding, receipt.Binding);
            Assert.Null(Settled(binding));
            Assert.IsType<AggregateException>(await Observe(executor.Completion));
            Assert.Equal(0, candidate.StartCalls);
            Assert.Equal(0, candidate.ReadCalls);
            Assert.Equal(1, candidate.StopCalls);
            Assert.Equal(1, candidate.DisposeCalls);
        }
        finally { prepared.TrySetResult(candidate); }
    }

    [Fact]
    public async Task Candidate_Start_Fault_Preserves_Transferred_Binding_And_Original_Error()
    {
        await using Fixture f = await Fixture.CreateAsync();
        IOException failure = new("候选 Start 失败。");
        Gate start = new();
        Producer candidate = Frames();
        candidate.StartAction = () => { start.Block(); throw failure; };
        DisplaySwitchExecutor executor = f.Executor((_, _) => Task.FromResult<IVideoFrameProducer>(candidate));
        DisplaySwitchRequestFrame request = f.Request(0);
        Task<Result> execution = f.Execute(executor, request, out var receipt);
        Task<ReplaceableVideoFrameSource.GenerationIdentity?> binding = receipt.Binding;
        try
        {
            await Join(start.Entered.Task);
            // 此属性取同一 registry gate，确保 Replace 的同步移交/Bind 临界区已经退出。
            Assert.True(executor.HasActiveExecution);
            var generation = Assert.IsType<ReplaceableVideoFrameSource.GenerationIdentity>(Settled(binding));
            Assert.Same(request, receipt.Request);
            Task<VideoFrameSender.SentFrame?> first = generation.FirstSend;
            Assert.False(first.IsCompleted);
            Assert.False(execution.IsCompleted);
            start.Release();
            var error = Assert.IsType<AggregateException>(await Observe(execution));
            Assert.Contains(failure, error.Flatten().InnerExceptions);
            Assert.False(execution.IsCompletedSuccessfully);
            Assert.IsType<AggregateException>(await Observe(executor.Completion));
            Assert.IsType<AggregateException>(await Observe(f.Source.Completion));
            Assert.Same(binding, receipt.Binding);
            Assert.Same(generation, Settled(binding));
            Assert.Same(first, generation.FirstSend);
            Assert.Null(Settled(first));
            Assert.Equal(1, candidate.StartCalls);
            Assert.Equal(0, candidate.ReadCalls);
            Assert.Equal(1, candidate.StopCalls);
            Assert.Equal(1, candidate.DisposeCalls);
            Assert.False(f.Lease.Completed.IsCompleted);
        }
        finally { start.Release(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prepare_Fault_Settles_Binding_Null_And_Preserves_Original_Error_Tree(bool asynchronous)
    {
        await using Fixture f = await Fixture.CreateAsync();
        IOException first = new("准备首个失败。");
        InvalidOperationException second = new("准备第二个失败。");
        AggregateException originalTree = new(first, second);
        TaskCompletionSource<IVideoFrameProducer> preparing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        preparing.SetException(originalTree);
        _ = f.Track(preparing.Task);
        DisplaySwitchExecutor executor = f.Executor((_, _) => asynchronous ? preparing.Task : throw first);
        DisplaySwitchRequestFrame request = f.Request(0);
        Task<Result> execution = f.Execute(executor, request, out var receipt);
        var binding = receipt.Binding;
        Assert.Same(request, receipt.Request);
        var error = Assert.IsType<AggregateException>(await Observe(execution));
        Assert.Same(asynchronous ? originalTree : first, Assert.Single(error.InnerExceptions));
        Assert.Same(binding, receipt.Binding);
        Assert.Null(Settled(binding));
        Assert.IsType<AggregateException>(await Observe(executor.Completion));
        Assert.False(f.Source.Termination.IsCompleted);
        Assert.False(f.Lease.Completed.IsCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_Before_Transfer_Settles_Binding_Before_Original_Candidate_Cleanup(bool cleanupFault)
    {
        await using Fixture f = await Fixture.CreateAsync();
        TaskCompletionSource entered = Signal(), retirement = Signal();
        TaskCompletionSource<IVideoFrameProducer> preparing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = f.Track(preparing.Task);
        Producer candidate = new((_, _) => ValueTask.FromResult<EncodedFrame?>(null)) { Retirement = retirement.Task };
        IOException failure = new("未移交候选清理失败。");
        DisplaySwitchExecutor executor = f.Executor((_, _) => { entered.TrySetResult(); return preparing.Task; });
        DisplaySwitchRequestFrame request = f.Request(0);
        Task<Result> execution = f.Execute(executor, request, out var receipt);
        var binding = receipt.Binding;
        try
        {
            await Join(entered.Task);
            Assert.Same(request, receipt.Request);
            Assert.False(binding.IsCompleted);
            executor.RequestStop();
            Assert.False(execution.IsCompleted);
            preparing.SetResult(candidate);
            await Join(candidate.StopEntered.Task);
            Assert.Null(Settled(binding));
            Assert.False(execution.IsCompleted);
            Assert.False(executor.Completion.IsCompleted);
            Assert.Equal(0, candidate.StartCalls);
            if (cleanupFault) retirement.SetException(failure);
            else retirement.SetResult();
            if (cleanupFault)
            {
                var error = Assert.IsType<AggregateException>(await Observe(execution));
                Assert.Same(failure, Assert.Single(error.Flatten().InnerExceptions));
                Assert.IsType<AggregateException>(await Observe(executor.Completion));
            }
            else
            {
                Result result = await Join(execution);
                Assert.Equal(new Result(false, false), result);
                Assert.Null(result.SendReceipt);
                await Join(executor.Completion);
            }
            Assert.Same(binding, receipt.Binding);
            Assert.Null(Settled(binding));
            Assert.Equal(1, candidate.StopCalls);
            Assert.Equal(1, candidate.DisposeCalls);
            Assert.False(f.Source.Termination.IsCompleted);
            Assert.False(f.Lease.Completed.IsCompleted);
        }
        finally
        {
            retirement.TrySetResult();
            preparing.TrySetResult(candidate);
        }
    }

    [Fact]
    public async Task Previous_Retirement_Fault_Still_Binds_Unstarted_Candidate_And_Seals_Its_FirstSend()
    {
        await using Fixture f = await Fixture.CreateAsync();
        IOException failure = new("旧代退役失败，候选不启动。");
        Producer previous = new((_, _) => ValueTask.FromResult<EncodedFrame?>(null))
        {
            Retirement = Task.FromException(failure),
        };
        await Join(f.Track(f.Source.ReplaceAsync(previous, out var previousGeneration)));
        Producer candidate = Frames();
        DisplaySwitchExecutor executor = f.Executor((_, _) => Task.FromResult<IVideoFrameProducer>(candidate));
        DisplaySwitchRequestFrame request = f.Request(0);
        Task<Result> execution = f.Execute(executor, request, out var receipt);
        var binding = receipt.Binding;
        var error = Assert.IsType<AggregateException>(await Observe(execution));
        Assert.Contains(failure, error.Flatten().InnerExceptions);
        var candidateGeneration = Assert.IsType<ReplaceableVideoFrameSource.GenerationIdentity>(Settled(binding));
        Assert.NotSame(previousGeneration, candidateGeneration);
        Assert.Same(request, receipt.Request);
        Assert.Same(binding, receipt.Binding);
        Assert.Null(Settled(candidateGeneration.FirstSend));
        Assert.Null(Settled(previousGeneration.FirstSend));
        Assert.Equal(0, candidate.StartCalls);
        Assert.Equal(0, candidate.ReadCalls);
        Assert.Equal(1, candidate.StopCalls);
        Assert.Equal(1, candidate.DisposeCalls);
        Assert.IsType<AggregateException>(await Observe(executor.Completion));
        Assert.IsType<AggregateException>(await Observe(f.Source.Completion));
        Assert.Same(candidateGeneration, Settled(binding));
    }

    private static EncodedFrame Frame(RecordingVideoOwner owner, int width, int height)
    {
        VideoFrameTestData.Payload().CopyTo(owner.Bytes, 0);
        return new(VideoCodec.Jpeg, width, height, 0x0123456789ABCDEFul, 0x1020304050607080L, 60, owner, 5);
    }

    private static Producer Frames(params EncodedFrame[] frames) => new((read, _) =>
        ValueTask.FromResult<EncodedFrame?>(read < frames.Length ? frames[read] : null));

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static T Settled<T>(Task<T> result)
    {
        // 原资源任务/受控边界已确定结算时点；漏结算必须立即断言失败，不能等结果 TCS 超时。
        Assert.True(result.IsCompletedSuccessfully);
        return result.GetAwaiter().GetResult();
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
        // 正文 watchdog 仅诊断；清理必须直接完整等待原 Task，而不是超时/取消代理。
        try { await task.ConfigureAwait(false); }
        catch (Exception) { /* 正文负责断言原任务的结算结果。 */ }
    }

    private static async Task Finish(ReplaceableVideoFrameSource source, CancellationTokenSource? caller, params Task?[] roots)
    {
        // 调用者先放行全部受控入口，再独立通知来源与 caller，最后逐个 join 原根。
        Task closing = source.DisposeAsync().AsTask();
        caller?.Cancel();
        foreach (Task? root in roots)
            if (root is not null) await Drain(root);
        await Drain(closing);
        await Drain(source.Completion);
    }

    private sealed class Gate
    {
        private readonly TaskCompletionSource _release = Signal();
        internal TaskCompletionSource Entered { get; } = Signal();
        internal CancellationToken Token { get; private set; }
        internal async Task HoldAsync(CancellationToken token)
        {
            Token = token;
            Entered.TrySetResult();
            // 不响应取消：测试原 I/O 尾部未返回时不能提前结算。
            await _release.Task.ConfigureAwait(false);
        }
        internal void Block()
        {
            Entered.TrySetResult();
            _release.Task.GetAwaiter().GetResult();
        }
        internal void Release() => _release.TrySetResult();
    }

    private sealed class ReadSlot
    {
        internal TaskCompletionSource Entered { get; } = Signal();
        internal TaskCompletionSource<EncodedFrame?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ValueTask<EncodedFrame?> Read()
        {
            Entered.TrySetResult();
            return new(Result.Task);
        }
    }

    private sealed class Producer(Func<int, CancellationToken, ValueTask<EncodedFrame?>> read) : IVideoFrameProducer
    {
        private readonly TaskCompletionSource _lifetime = Signal();
        private int _starts, _reads, _stops, _disposes;
        internal Action? StartAction { get; set; }
        internal Task? Retirement { get; init; }
        internal TaskCompletionSource StopEntered { get; } = Signal();
        internal int StartCalls => Volatile.Read(ref _starts);
        internal int ReadCalls => Volatile.Read(ref _reads);
        internal int StopCalls => Volatile.Read(ref _stops);
        internal int DisposeCalls => Volatile.Read(ref _disposes);
        public void Start()
        {
            Interlocked.Increment(ref _starts);
            StartAction?.Invoke();
        }
        public ValueTask<EncodedFrame?> ReadNextAsync(CancellationToken cancellationToken = default) =>
            read(Interlocked.Increment(ref _reads) - 1, cancellationToken);
        private Task Complete()
        {
            _lifetime.TrySetResult();
            return Retirement ?? _lifetime.Task;
        }
        public Task StopAsync()
        {
            Interlocked.Increment(ref _stops);
            StopEntered.TrySetResult();
            return Complete();
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposes);
            return new(Complete());
        }
        public Task Completion => Retirement ?? _lifetime.Task;
    }

    private sealed class HookStream(RecordingVideoStream inner, Func<int, CancellationToken, Task> afterOperation) : Stream
    {
        private int _operation;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            await afterOperation(++_operation, cancellationToken);
        }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await inner.FlushAsync(cancellationToken);
            await afterOperation(++_operation, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
        public override bool CanRead => false;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly List<Task> _roots = [];
        private DisplaySwitchExecutor? _executor;
        private Fixture()
        {
            byte[] token = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
            byte[] pin = Enumerable.Range(64, 32).Select(value => (byte)value).ToArray();
            byte[] nonce = Enumerable.Range(128, 16).Select(value => (byte)value).ToArray();
            IPAddress remote = IPAddress.Parse("192.168.10.20");
            Guid session = Guid.NewGuid();
            Registration = Registry.Register(session, Guid.NewGuid(), Guid.NewGuid(), "首帧结算接缝测试",
                SessionPermission.ViewOnly, remote, 12345, token, new VideoAttachWindow(Clock, 15_000),
                pin, Control.Token, V2AgreementFixture.Create(session));
            ConnectionSecurityContext video = new(IPAddress.Parse("192.168.10.10"), remote, 23456, SslProtocols.Tls13, pin);
            // 真实 registry/lease 准入证明；这里只覆盖协议接缝，不宣称进行了 TLS/GDI/双机验证。
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
        internal static async Task<Fixture> CreateAsync()
        {
            Fixture f = new();
            try
            {
                await Join(f.Track(f.Source.ReplaceAsync(Frames())));
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
        internal IDisposable Publication { get; private set; } = null!;
        internal Admission Admission { get; private set; } = null!;
        internal DisplayCatalogFrame Catalog { get; } = new(23, 44,
        [
            new DisplayInfo(new DisplayId(3), "主显示器", 0, 0, 1920, 1080, true),
            new DisplayInfo(new DisplayId(17), "左侧显示器", -1280, -200, 1280, 1024, false),
        ]);
        internal DisplaySwitchRequestFrame Request(long sequence) =>
            new(Registration.SessionId, sequence, 1234, Catalog.Sequence, new DisplayId(17));
        internal T Track<T>(T root) where T : Task
        {
            _roots.Add(root);
            return root;
        }
        internal DisplaySwitchExecutor Executor(Func<DisplayInfo, CancellationToken, Task<IVideoFrameProducer>> prepare, bool tracked = true) =>
            _executor = tracked ? Admission.CreateTrackedExecutor(prepare) : Admission.CreateExecutor(prepare);
        internal Task<Result> Execute(DisplaySwitchExecutor executor, DisplaySwitchRequestFrame request)
        {
            Status status = executor.TryExecute(request, out Task<Result>? execution);
            if (execution is not null) Track(execution);
            Assert.Equal(Status.Accepted, status);
            Assert.NotNull(execution);
            return execution;
        }
        internal Task<Result> Execute(DisplaySwitchExecutor executor, DisplaySwitchRequestFrame request,
            out DisplaySwitchExecutor.RequestExecutionReceipt receipt)
        {
            Status status = executor.TryExecute(request, out Task<Result>? execution, out var accepted);
            if (execution is not null) Track(execution);
            Assert.Equal(Status.Accepted, status);
            Assert.NotNull(execution);
            receipt = Assert.IsType<DisplaySwitchExecutor.RequestExecutionReceipt>(accepted);
            Assert.Same(request, receipt.Request);
            return execution;
        }
        internal void Invalidate(string reason)
        {
            switch (reason)
            {
                case "withdraw": Publication.Dispose(); break;
                case "control-cancel": Control.Cancel(); break;
                case "unregister": Registration.Dispose(); break;
                case "lease-complete": Lease.Complete(); break;
                case "admission-close": Admission.Dispose(); break;
                case "source-stop": Track(Source.DisposeAsync().AsTask()); break;
                default: throw new ArgumentOutOfRangeException(nameof(reason));
            }
        }
        public async ValueTask DisposeAsync()
        {
            // 各测试已先释放其 gate；来源独立终止，不能先 join 仍依赖来源的执行器。
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
                Assert.Equal(0, Clock.TimerCount);
            }
            finally
            {
                Lease.Complete();
                Control.Dispose();
            }
        }
    }
}
