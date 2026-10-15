using System.Buffers.Binary;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

public sealed partial class FirstFrameRouterBoundaryTests
{
    [Fact(Timeout = 30_000)]
    public async Task Generation_Router_Publishes_Only_After_Ack_Initial_Start_And_Sender_Registration()
    {
        using ManualResetEventSlim startRelease = new();
        TaskCompletionSource<EncodedFrame?> read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ProbeProducer producer = new((_, _) => new(read.Task)) { OnStart = () => startRelease.Wait() };
        ProbeProducer rejected = new((_, _) => ValueTask.FromResult<EncodedFrame?>(null));
        ProbeFactory factory = new((_, _) => ValueTask.FromResult<IVideoFrameProducer>(producer));
        BoundaryScenario scenario = new(holdWrite: true, holdFlush: true, producerFactory: factory,
            enableProducerReplacement: true);
        using var registration = scenario.Register();
        Task run = scenario.Start();
        try
        {
            await scenario.Stream.WriteEntered.Task.WaitAsync(Guard);
            Assert.Equal(0, factory.Calls);
            Assert.Throws<InvalidOperationException>(() => { _ = registration.ReplaceVideoProducerAsync(rejected); });
            scenario.Stream.ReleaseWrite();
            await scenario.Stream.FlushEntered.Task.WaitAsync(Guard);
            Assert.Equal(0, factory.Calls);
            Assert.Throws<InvalidOperationException>(() => { _ = registration.ReplaceVideoProducerAsync(rejected); });
            scenario.Stream.ReleaseFlush();
            await producer.StartEntered.Task.WaitAsync(Guard);
            Assert.True(scenario.Stream.FlushCompleted);
            Assert.Equal(1, factory.Calls);
            Assert.Equal(0, producer.Reads);
            Assert.Throws<InvalidOperationException>(() => { _ = registration.ReplaceVideoProducerAsync(rejected); });
            Assert.Equal(0, rejected.Starts);
            Assert.Equal(0, rejected.Stops);
            startRelease.Set();
            await producer.ReadEntered.Task.WaitAsync(Guard);
            Task replacement = registration.ReplaceVideoProducerAsync(rejected);
            await producer.StopEntered.Task.WaitAsync(Guard);
            Assert.False(replacement.IsCompleted);
            read.TrySetResult(null);
            await replacement.WaitAsync(Guard);
            await run.WaitAsync(Guard);
            Assert.Equal(1, rejected.Starts);
            Assert.Equal(1, rejected.Reads);
            Assert.Equal(1, producer.Stops);
            Assert.Equal(1, producer.Disposals);
            Assert.Equal(1, rejected.Stops);
            Assert.Equal(1, rejected.Disposals);
            Assert.Equal(0, scenario.Source.Calls);
            Assert.True(registration.VideoCompletion.IsCompletedSuccessfully);
        }
        finally
        {
            startRelease.Set();
            read.TrySetResult(null);
            await FinishGenerationAsync(scenario, run);
            if (rejected.Stops == 0) { await rejected.StopAsync(); await rejected.DisposeAsync(); }
        }
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Generation_Router_Uses_One_Stream_And_Optional_Sender_Sequence_Across_Replacement(bool sequential)
    {
        RecordingVideoOwner firstOwner = new(32);
        RecordingVideoOwner secondOwner = new(32);
        VideoFrameTestData.Payload().CopyTo(firstOwner.Bytes, 0);
        VideoFrameTestData.Payload().CopyTo(secondOwner.Bytes, 0);
        using EncodedFrame first = VideoFrameTestData.Frame(firstOwner);
        using EncodedFrame second = VideoFrameTestData.Frame(secondOwner);
        TaskCompletionSource oldWaiting = Signal();
        TaskCompletionSource<EncodedFrame?> oldRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ProbeProducer producer = new((call, _) =>
        {
            if (call == 1) return ValueTask.FromResult<EncodedFrame?>(first);
            oldWaiting.TrySetResult();
            return new(oldRead.Task);
        });
        ProbeProducer next = new((call, _) => ValueTask.FromResult<EncodedFrame?>(call == 1 ? second : null));
        ProbeFactory factory = new((_, _) => ValueTask.FromResult<IVideoFrameProducer>(producer));
        BoundaryScenario scenario = new(producerFactory: factory, enableProducerReplacement: true,
            firstFrameId: sequential ? 70UL : null);
        using var registration = scenario.Register();
        Task run = scenario.Start();
        try
        {
            await oldWaiting.Task.WaitAsync(Guard);
            Task replace = registration.ReplaceVideoProducerAsync(next);
            await producer.StopEntered.Task.WaitAsync(Guard);
            Assert.Equal(0, next.Starts);
            oldRead.TrySetResult(null);
            await replace.WaitAsync(Guard);
            await run.WaitAsync(Guard);
            byte[][] writes = scenario.Stream.Writes.ToArray();
            Assert.Equal(5, writes.Length);
            Assert.True(VideoAttachAckFrame.TryParse(writes[0].AsSpan(4), out _, out _));
            Assert.Equal(sequential ? 70UL : first.FrameId, BinaryPrimitives.ReadUInt64BigEndian(writes[1].AsSpan(8)));
            Assert.Equal(sequential ? 71UL : second.FrameId, BinaryPrimitives.ReadUInt64BigEndian(writes[3].AsSpan(8)));
            Assert.Equal(VideoFrameTestData.Payload(), writes[2]);
            Assert.Equal(VideoFrameTestData.Payload(), writes[4]);
            Assert.Equal(1, factory.Calls);
            Assert.Equal(1, firstOwner.DisposeCalls);
            Assert.Equal(1, secondOwner.DisposeCalls);
            Assert.Equal(1, producer.Starts);
            Assert.Equal(1, next.Starts);
            Assert.Equal(1, producer.Stops);
            Assert.Equal(1, next.Stops);
        }
        finally
        {
            oldRead.TrySetResult(null);
            await FinishGenerationAsync(scenario, run);
            if (next.Stops == 0) { await next.StopAsync(); await next.DisposeAsync(); }
        }
    }

    [Theory(Timeout = 30_000)]
    [InlineData("start", "host")]
    [InlineData("start", "revoke")]
    [InlineData("start", "peer")]
    [InlineData("read", "host")]
    [InlineData("read", "revoke")]
    [InlineData("read", "peer")]
    [InlineData("read-prefix", "host")]
    [InlineData("stop", "host")]
    [InlineData("stop", "revoke")]
    [InlineData("stop", "peer")]
    [InlineData("completion", "host")]
    [InlineData("completion", "revoke")]
    [InlineData("completion", "peer")]
    public async Task Generation_Router_Withdraws_Before_Close_And_Joins_Blocked_Original_Operations(
        string boundary, string ending)
    {
        using ManualResetEventSlim releasePrefix = new();
        TaskCompletionSource releaseCompletion = Signal();
        TaskCompletionSource<EncodedFrame?> releaseRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancelEntered = Signal();
        TaskCompletionSource disposeEntered = Signal();
        CancellationTokenRegistration cancellation = default;
        SessionRegistry.SessionRegistration? registration = null;
        ProbeProducer rejected = new((_, _) => ValueTask.FromResult<EncodedFrame?>(null));
        System.Collections.Concurrent.ConcurrentQueue<Task> unintended = new();
        Exception? cancellationRejection = null;
        Exception? closeRejection = null;
        Exception? ProbeRejection()
        {
            try { unintended.Enqueue(registration!.ReplaceVideoProducerAsync(rejected)); return null; }
            catch (Exception error) { return error; }
        }
        ProbeProducer producer = new((_, token) =>
        {
            cancellation = token.Register(() =>
            {
                cancellationRejection = ProbeRejection();
                cancelEntered.TrySetResult();
            });
            if (boundary == "read-prefix") releasePrefix.Wait();
            return new(releaseRead.Task);
        })
        {
            OnStart = () => { if (boundary == "start") releasePrefix.Wait(); },
            StopTaskFactory = () =>
            {
                if (boundary == "stop") releasePrefix.Wait();
                return Task.CompletedTask;
            },
            DisposeTaskFactory = () => { disposeEntered.TrySetResult(); return Task.CompletedTask; },
            CompletionTaskFactory = boundary == "completion" ? () => releaseCompletion.Task : null
        };
        ProbeFactory factory = new((_, _) => ValueTask.FromResult<IVideoFrameProducer>(producer));
        BoundaryScenario scenario = new(producerFactory: factory, enableProducerReplacement: true);
        registration = scenario.Register();
        int rejectedBeforeClose = 0;
        scenario.Stream.BeforeDispose = () =>
        {
            closeRejection = ProbeRejection();
            Interlocked.Increment(ref rejectedBeforeClose);
        };
        Task run = scenario.Start();
        try
        {
            await (boundary == "start" ? producer.StartEntered.Task : producer.ReadEntered.Task).WaitAsync(Guard);
            if (ending == "host") await scenario.HostStop.CancelAsync().WaitAsync(Guard);
            else if (ending == "revoke") registration.Dispose();
            else scenario.Stream.EndPeer();
            await scenario.Stream.Closed.Task.WaitAsync(Guard);
            await producer.StopEntered.Task.WaitAsync(Guard);
            await disposeEntered.Task.WaitAsync(Guard);
            Assert.Equal(1, Volatile.Read(ref rejectedBeforeClose));
            Assert.IsType<InvalidOperationException>(closeRejection);
            Assert.False(run.IsCompleted);
            Assert.False(registration.VideoCompletion.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => { _ = registration.ReplaceVideoProducerAsync(rejected); });
            if (boundary != "start")
            {
                await cancelEntered.Task.WaitAsync(Guard);
                Assert.IsType<InvalidOperationException>(cancellationRejection);
            }
            if (boundary is "stop" or "completion")
            {
                releaseRead.TrySetResult(null);
                Assert.False(run.IsCompleted);
            }
            releasePrefix.Set();
            releaseRead.TrySetResult(null);
            releaseCompletion.TrySetResult();
            if (ending == "host") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Guard));
            else await run.WaitAsync(Guard);
            Assert.Equal(1, producer.Starts);
            Assert.Equal(boundary == "start" ? 0 : 1, producer.Reads);
            Assert.Equal(1, producer.Stops);
            Assert.Equal(1, producer.Disposals);
            Assert.Equal(0, rejected.Starts);
            Assert.Equal(0, rejected.Stops);
            Assert.True(registration.VideoCompletion.IsCompletedSuccessfully);
        }
        finally
        {
            releasePrefix.Set();
            releaseRead.TrySetResult(null);
            releaseCompletion.TrySetResult();
            await FinishGenerationAsync(scenario, run);
            cancellation.Dispose();
            foreach (Task operation in unintended)
                Assert.IsNotType<TimeoutException>(await Record.ExceptionAsync(() => operation.WaitAsync(Guard)));
            if (rejected.Stops == 0) { await rejected.StopAsync(); await rejected.DisposeAsync(); }
            registration.Dispose();
        }
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false, "host")]
    [InlineData(true, "host")]
    [InlineData(false, "revoke")]
    [InlineData(true, "peer")]
    public async Task Generation_Router_Joins_Late_Factory_And_All_Unaccepted_Candidate_Tasks(bool synchronous, string ending)
    {
        using ManualResetEventSlim factoryRelease = new();
        TaskCompletionSource<IVideoFrameProducer> creation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource stop = Signal();
        TaskCompletionSource dispose = Signal();
        TaskCompletionSource completion = Signal();
        TaskCompletionSource disposeEntered = Signal();
        TaskCompletionSource completionEntered = Signal();
        ProbeProducer producer = new((_, _) => ValueTask.FromResult<EncodedFrame?>(null))
        {
            StopTaskFactory = () => stop.Task,
            DisposeTaskFactory = () => { disposeEntered.TrySetResult(); return dispose.Task; },
            CompletionTaskFactory = () => { completionEntered.TrySetResult(); return completion.Task; }
        };
        ProbeFactory factory = new((_, _) =>
        {
            if (synchronous) factoryRelease.Wait();
            return new(creation.Task);
        });
        BoundaryScenario scenario = new(producerFactory: factory, enableProducerReplacement: true);
        using var registration = scenario.Register();
        Task run = scenario.Start();
        try
        {
            await factory.Entered.Task.WaitAsync(Guard);
            if (ending == "host") await scenario.HostStop.CancelAsync().WaitAsync(Guard);
            else if (ending == "revoke") registration.Dispose();
            else scenario.Stream.EndPeer();
            await scenario.Stream.Closed.Task.WaitAsync(Guard);
            Assert.False(run.IsCompleted);
            Assert.False(registration.VideoCompletion.IsCompleted);
            Assert.Equal(0, producer.Stops);
            factoryRelease.Set();
            creation.TrySetResult(producer);
            await producer.StopEntered.Task.WaitAsync(Guard);
            await disposeEntered.Task.WaitAsync(Guard);
            await completionEntered.Task.WaitAsync(Guard);
            Assert.Equal(0, producer.Starts);
            Assert.False(run.IsCompleted);
            stop.TrySetResult();
            dispose.TrySetResult();
            Assert.False(run.IsCompleted);
            Assert.False(registration.VideoCompletion.IsCompleted);
            completion.TrySetResult();
            if (ending == "host") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Guard));
            else await run.WaitAsync(Guard);
            Assert.Equal(0, producer.Starts);
            Assert.Equal(0, producer.Reads);
            Assert.Equal(1, producer.Stops);
            Assert.Equal(1, producer.Disposals);
            Assert.True(registration.VideoCompletion.IsCompletedSuccessfully);
        }
        finally
        {
            factoryRelease.Set();
            creation.TrySetResult(producer);
            stop.TrySetResult();
            dispose.TrySetResult();
            completion.TrySetResult();
            await FinishGenerationAsync(scenario, run);
        }
    }

    [Theory(Timeout = 30_000)]
    [InlineData("eof")]
    [InlineData("read")]
    [InlineData("start")]
    public async Task Generation_Router_Uses_Early_Source_Termination_Before_Blocked_Retirement(string terminal)
    {
        using ManualResetEventSlim releaseStop = new();
        TaskCompletionSource completion = Signal();
        IOException failure = new("来源原始故障。");
        ProbeProducer producer = new((_, _) => terminal == "read"
            ? ValueTask.FromException<EncodedFrame?>(failure) : ValueTask.FromResult<EncodedFrame?>(null))
        {
            StartFailure = terminal == "start" ? failure : null,
            StopTaskFactory = () => { releaseStop.Wait(); return Task.CompletedTask; },
            DisposeTaskFactory = () => Task.CompletedTask,
            CompletionTaskFactory = () => completion.Task
        };
        ProbeFactory factory = new((_, _) => ValueTask.FromResult<IVideoFrameProducer>(producer));
        BoundaryScenario scenario = new(producerFactory: factory, enableProducerReplacement: true);
        using var registration = scenario.Register();
        Task run = scenario.Start();
        try
        {
            await producer.StopEntered.Task.WaitAsync(Guard);
            await scenario.Stream.Closed.Task.WaitAsync(Guard);
            Assert.False(run.IsCompleted);
            Assert.False(registration.VideoCompletion.IsCompleted);
            releaseStop.Set();
            Assert.False(run.IsCompleted);
            completion.TrySetResult();
            Exception? actual = await Record.ExceptionAsync(() => run.WaitAsync(Guard));
            if (terminal == "eof") Assert.Null(actual);
            else Assert.Same(failure, Assert.Single(Assert.IsType<AggregateException>(actual).InnerExceptions));
            Assert.True(registration.VideoCompletion.IsCompletedSuccessfully);
        }
        finally
        {
            releaseStop.Set();
            completion.TrySetResult();
            await FinishGenerationAsync(scenario, run);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Generation_Router_Records_Replacement_And_Sender_Source_Failure_Only_Once_With_Original_Tree()
    {
        TaskCompletionSource<EncodedFrame?> read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IOException repeated = new("原读取多根中的第一与第三项。");
        AggregateException nested = new("原嵌套树。", new ObjectDisposedException("nested"));
        ProbeProducer old = new((_, _) => new(read.Task));
        ProbeProducer next = new((_, _) => ValueTask.FromResult<EncodedFrame?>(null));
        ProbeFactory factory = new((_, _) => ValueTask.FromResult<IVideoFrameProducer>(old));
        BoundaryScenario scenario = new(producerFactory: factory, enableProducerReplacement: true);
        using var registration = scenario.Register();
        Task run = scenario.Start();
        Task? replace = null;
        try
        {
            await old.ReadEntered.Task.WaitAsync(Guard);
            replace = registration.ReplaceVideoProducerAsync(next);
            await old.StopEntered.Task.WaitAsync(Guard);
            read.TrySetException([repeated, nested, repeated]);
            AggregateException replaceFailure = Assert.IsType<AggregateException>(await Record.ExceptionAsync(() => replace.WaitAsync(Guard)));
            AggregateException actual = Assert.IsType<AggregateException>(await Record.ExceptionAsync(() => run.WaitAsync(Guard)));
            AggregateException original = Assert.IsType<AggregateException>(Assert.Single(actual.InnerExceptions));
            Assert.Collection(original.InnerExceptions,
                error => Assert.Same(repeated, error), error => Assert.Same(nested, error), error => Assert.Same(repeated, error));
            AggregateException replacementOriginal = Assert.IsType<AggregateException>(Assert.Single(replaceFailure.InnerExceptions));
            Assert.Equal(original.InnerExceptions, replacementOriginal.InnerExceptions);
            Assert.Equal(0, next.Starts);
            Assert.Equal(1, next.Stops);
            Assert.Equal(1, next.Disposals);
        }
        finally
        {
            read.TrySetResult(null);
            await FinishGenerationAsync(scenario, run);
            if (replace is not null)
            {
                Exception? joined = await Record.ExceptionAsync(() => replace.WaitAsync(Guard));
                Assert.IsNotType<TimeoutException>(joined);
            }
            if (next.Stops == 0) { await next.StopAsync(); await next.DisposeAsync(); }
        }
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Generation_Router_Preserves_Independent_Retirement_Errors_Even_With_Same_Exception(bool sharedTask)
    {
        AggregateException failure = new("各原操作共用的错误树。", new IOException("第一项。"),
            new AggregateException("嵌套。", new ObjectDisposedException("第二项")));
        Task stop = Task.FromException(failure);
        Task dispose = sharedTask ? stop : Task.FromException(failure);
        Task completion = sharedTask ? stop : Task.FromException(failure);
        ProbeProducer producer = new((_, _) => ValueTask.FromResult<EncodedFrame?>(null))
        {
            StopTaskFactory = () => stop,
            DisposeTaskFactory = () => dispose,
            CompletionTaskFactory = () => completion
        };
        ProbeFactory factory = new((_, _) => ValueTask.FromResult<IVideoFrameProducer>(producer));
        BoundaryScenario scenario = new(producerFactory: factory, enableProducerReplacement: true);
        using var registration = scenario.Register();
        Task run = scenario.Start();
        try
        {
            AggregateException actual = Assert.IsType<AggregateException>(await Record.ExceptionAsync(() => run.WaitAsync(Guard)));
            Assert.Equal(sharedTask ? 1 : 3, actual.InnerExceptions.Count);
            Assert.All(actual.InnerExceptions, error => Assert.Same(failure, error));
            Assert.IsType<AggregateException>(failure.InnerExceptions[1]);
        }
        finally { await FinishGenerationAsync(scenario, run); }
    }

    [Fact(Timeout = 30_000)]
    public async Task Generation_Router_Joins_Close_And_Blocked_Cancellation_After_Withdrawing()
    {
        using ManualResetEventSlim cancelRelease = new();
        TaskCompletionSource cancelEntered = Signal();
        TaskCompletionSource<EncodedFrame?> read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration callback = default;
        ProbeProducer producer = new((_, token) =>
        {
            callback = token.Register(() => { cancelEntered.TrySetResult(); cancelRelease.Wait(); });
            return new(read.Task);
        });
        ProbeProducer rejected = new((_, _) => ValueTask.FromResult<EncodedFrame?>(null));
        ProbeFactory factory = new((_, _) => ValueTask.FromResult<IVideoFrameProducer>(producer));
        BoundaryScenario scenario = new(producerFactory: factory, holdDispose: true, enableProducerReplacement: true);
        using var registration = scenario.Register();
        Task run = scenario.Start();
        try
        {
            await producer.ReadEntered.Task.WaitAsync(Guard);
            await scenario.HostStop.CancelAsync().WaitAsync(Guard);
            await scenario.Stream.DisposeEntered.Task.WaitAsync(Guard);
            await cancelEntered.Task.WaitAsync(Guard);
            await producer.StopEntered.Task.WaitAsync(Guard);
            Assert.Throws<InvalidOperationException>(() => { _ = registration.ReplaceVideoProducerAsync(rejected); });
            read.TrySetResult(null);
            Assert.False(run.IsCompleted);
            Assert.False(registration.VideoCompletion.IsCompleted);
            cancelRelease.Set();
            Assert.False(run.IsCompleted);
            scenario.Stream.ReleaseDispose();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Guard));
            Assert.True(registration.VideoCompletion.IsCompletedSuccessfully);
        }
        finally
        {
            cancelRelease.Set();
            read.TrySetResult(null);
            scenario.Stream.ReleaseDispose();
            await FinishGenerationAsync(scenario, run);
            callback.Dispose();
            await rejected.StopAsync();
            await rejected.DisposeAsync();
        }
    }

    [Theory(Timeout = 30_000)]
    [InlineData("proof")]
    [InlineData("ack-write")]
    [InlineData("ack-flush")]
    public async Task Generation_Router_Never_Creates_Or_Publishes_After_Attach_Or_Ack_Failure(string boundary)
    {
        NeverCreateFactory factory = new();
        ProbeProducer candidate = new((_, _) => ValueTask.FromResult<EncodedFrame?>(null));
        await using BoundaryScenario scenario = new(badProof: boundary == "proof",
            failWrite: boundary == "ack-write", failFlush: boundary == "ack-flush",
            producerFactory: factory, enableProducerReplacement: true);
        using var registration = scenario.Register();
        await scenario.Start().WaitAsync(Guard);
        Assert.Equal(0, factory.Calls);
        Assert.Throws<InvalidOperationException>(() => { _ = registration.ReplaceVideoProducerAsync(candidate); });
        Assert.Equal(0, candidate.Starts);
        Assert.Equal(0, candidate.Stops);
        await candidate.StopAsync();
        await candidate.DisposeAsync();
    }

    [Fact(Timeout = 30_000)]
    public async Task Generation_Router_Keeps_Send_And_Frame_Cleanup_As_Independent_Same_Instance_Errors()
    {
        InvalidOperationException failure = new("写帧和 owner 清理分别失败但共用实例。");
        RecordingVideoOwner owner = new(32);
        EncodedFrame frame = VideoFrameTestData.Frame(owner);
        owner.ThrowOnMemoryRead = owner.MemoryReads + 1;
        owner.MemoryFailure = failure;
        owner.DisposeFailure = failure;
        ProbeProducer producer = new((_, _) => ValueTask.FromResult<EncodedFrame?>(frame));
        ProbeFactory factory = new((_, _) => ValueTask.FromResult<IVideoFrameProducer>(producer));
        BoundaryScenario scenario = new(producerFactory: factory, enableProducerReplacement: true);
        using var registration = scenario.Register();
        Task run = scenario.Start();
        try
        {
            AggregateException actual = Assert.IsType<AggregateException>(await Record.ExceptionAsync(() => run.WaitAsync(Guard)));
            Assert.Collection(actual.InnerExceptions,
                error => Assert.Same(failure, error), error => Assert.Same(failure, error));
            Assert.Equal(1, owner.DisposeCalls);
            Assert.Equal(1, producer.Reads);
        }
        finally
        {
            await FinishGenerationAsync(scenario, run);
            owner.DisposeFailure = null;
            frame.Dispose();
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Generation_Router_Host_Stop_Still_Closes_During_Post_Attach_Clock_Prefix()
    {
        using ManualResetEventSlim releaseClock = new();
        TaskCompletionSource entered = Signal();
        NeverCreateFactory factory = new();
        BoundaryScenario scenario = new(producerFactory: factory, enableProducerReplacement: true);
        using var registration = scenario.Register();
        int blocked = 0;
        scenario.Clock.AfterSample = _ =>
        {
            if (scenario.Router.AttachStatus == VideoAttachStatus.Attached && Interlocked.Exchange(ref blocked, 1) == 0)
            {
                entered.TrySetResult();
                releaseClock.Wait();
            }
        };
        Task run = Task.Run(scenario.Start);
        try
        {
            await entered.Task.WaitAsync(Guard);
            Assert.False(registration.VideoCompletion.IsCompleted);
            await scenario.HostStop.CancelAsync().WaitAsync(Guard);
            await scenario.Stream.Closed.Task.WaitAsync(Guard);
            Assert.False(run.IsCompleted);
            Assert.Equal(0, factory.Calls);
            releaseClock.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Guard));
            Assert.True(registration.VideoCompletion.IsCompletedSuccessfully);
            Assert.Empty(scenario.Stream.Writes);
        }
        finally
        {
            releaseClock.Set();
            await FinishGenerationAsync(scenario, run);
        }
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(ulong.MaxValue)]
    public void Generation_Router_Rejects_Sequence_Without_Explicit_Replacement(ulong firstFrameId)
    {
        UnexpectedControlServices control = new();
        ControlAuthContext context = new()
        {
            ServerDeviceId = Guid.NewGuid(),
            AccessSecretStore = control,
            FailedAuthLimiter = new FailedAuthLimiter(),
            PendingApprovalLimiter = new ConnectionAdmissionLimiter(3, 1),
            ApprovalGate = control,
            SessionRegistry = new SessionRegistry(),
            TimeProvider = TimeProvider.System
        };
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
        {
            _ = new FirstFrameRouter(context, NewTimeouts(), new NeverCreateFactory(), firstFrameId: firstFrameId);
        });
        Assert.Equal("firstFrameId", error.ParamName);
    }

    [Fact(Timeout = 30_000)]
    public async Task Generation_Router_Writer_Closes_Only_After_Withdrawing_While_Sender_Cleanup_Is_Blocked()
    {
        using ManualResetEventSlim releaseFrameCleanup = new();
        TaskCompletionSource cleanupEntered = Signal();
        TaskCompletionSource closeObserved = Signal();
        TaskCompletionSource readReady = Signal();
        TaskCompletionSource cancellationEntered = Signal();
        TaskCompletionSource<EncodedFrame?> read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration cancellation = default;
        RecordingVideoOwner firstOwner = new(32);
        VideoFrameTestData.Payload().CopyTo(firstOwner.Bytes, 0);
        using EncodedFrame firstFrame = VideoFrameTestData.Frame(firstOwner);
        InvalidOperationException failure = new("写帧原始故障。");
        RecordingVideoOwner owner = new(32);
        EncodedFrame frame = VideoFrameTestData.Frame(owner);
        owner.ThrowOnMemoryRead = owner.MemoryReads + 1;
        owner.MemoryFailure = failure;
        owner.BeforeDispose = () => { cleanupEntered.TrySetResult(); releaseFrameCleanup.Wait(); };
        ProbeProducer producer = new((call, token) =>
        {
            if (call == 1) return ValueTask.FromResult<EncodedFrame?>(firstFrame);
            cancellation = token.Register(() => cancellationEntered.TrySetResult());
            readReady.TrySetResult();
            return new(read.Task);
        });
        ProbeProducer candidate = new((_, _) => ValueTask.FromResult<EncodedFrame?>(null));
        ProbeFactory factory = new((_, _) => ValueTask.FromResult<IVideoFrameProducer>(producer));
        BoundaryScenario scenario = new(producerFactory: factory, holdDispose: true, enableProducerReplacement: true);
        using var registration = scenario.Register();
        Exception? rejected = null;
        Task? unintendedReplacement = null;
        scenario.Stream.BeforeDispose = () =>
        {
            try { unintendedReplacement = registration.ReplaceVideoProducerAsync(candidate); }
            catch (Exception error) { rejected = error; }
            closeObserved.TrySetResult();
        };
        Task run = scenario.Start();
        Task notifications = Task.WhenAll(producer.StopEntered.Task, cancellationEntered.Task);
        try
        {
            await readReady.Task.WaitAsync(Guard);
            Assert.Equal(2, producer.Reads);
            Assert.Equal(3, scenario.Stream.Writes.Count);
            Assert.Equal(1, firstOwner.DisposeCalls);
            read.TrySetResult(frame);
            await cleanupEntered.Task.WaitAsync(Guard);
            await closeObserved.Task.WaitAsync(Guard);
            await scenario.Stream.DisposeEntered.Task.WaitAsync(Guard);
            Assert.IsType<InvalidOperationException>(rejected);
            Assert.Null(unintendedReplacement);
            using CancellationTokenSource notificationGuard = new();
            Task watchdog = Task.Delay(Guard, notificationGuard.Token);
            await Task.WhenAny(notifications, watchdog);
            notificationGuard.Cancel();
            await Record.ExceptionAsync(() => watchdog);
            Assert.True(notifications.IsCompletedSuccessfully, "sender 与 close 都被闸门阻塞时仍须独立通知停止和取消。");
            Assert.False(releaseFrameCleanup.IsSet);
            Assert.False(scenario.Stream.Closed.Task.IsCompleted);
            Assert.False(run.IsCompleted);
            Assert.False(registration.VideoCompletion.IsCompleted);
            Assert.Equal(0, candidate.Starts);
            releaseFrameCleanup.Set();
            scenario.Stream.ReleaseDispose();
            Assert.Same(failure, await Record.ExceptionAsync(() => run.WaitAsync(Guard)));
            Assert.Equal(1, owner.DisposeCalls);
        }
        finally
        {
            read.TrySetResult(null);
            releaseFrameCleanup.Set();
            scenario.Stream.ReleaseDispose();
            await FinishGenerationAsync(scenario, run);
            cancellation.Dispose();
            if (unintendedReplacement is not null)
                Assert.IsNotType<TimeoutException>(await Record.ExceptionAsync(() => unintendedReplacement.WaitAsync(Guard)));
            if (candidate.Stops == 0) { await candidate.StopAsync(); await candidate.DisposeAsync(); }
            frame.Dispose();
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Generation_Router_Registers_Peer_Worker_Before_Blocking_Stream_Prefix_And_Joins_It()
    {
        using ManualResetEventSlim releasePeer = new();
        TaskCompletionSource peerEntered = Signal();
        TaskCompletionSource<EncodedFrame?> read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ProbeProducer producer = new((_, _) => new(read.Task));
        ProbeFactory factory = new((_, _) => ValueTask.FromResult<IVideoFrameProducer>(producer));
        BoundaryScenario scenario = new(producerFactory: factory, enableProducerReplacement: true);
        using var registration = scenario.Register();
        scenario.Stream.BeforeRead = call =>
        {
            if (call == 3) { peerEntered.TrySetResult(); releasePeer.Wait(); }
        };
        Task run = scenario.Start();
        try
        {
            await peerEntered.Task.WaitAsync(Guard);
            await producer.ReadEntered.Task.WaitAsync(Guard);
            await scenario.HostStop.CancelAsync().WaitAsync(Guard);
            await scenario.Stream.Closed.Task.WaitAsync(Guard);
            await producer.StopEntered.Task.WaitAsync(Guard);
            read.TrySetResult(null);
            Assert.False(run.IsCompleted);
            Assert.False(registration.VideoCompletion.IsCompleted);
            releasePeer.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Guard));
            Assert.True(scenario.Stream.PeerReadCompleted);
            Assert.True(registration.VideoCompletion.IsCompletedSuccessfully);
        }
        finally
        {
            releasePeer.Set();
            read.TrySetResult(null);
            await FinishGenerationAsync(scenario, run);
        }
    }

    private static async Task FinishGenerationAsync(BoundaryScenario scenario, Task run)
    {
        scenario.Stream.ReleaseAll();
        await scenario.HostStop.CancelAsync().WaitAsync(Guard);
        Exception? joined = await Record.ExceptionAsync(() => run.WaitAsync(Guard));
        Assert.IsNotType<TimeoutException>(joined);
        Exception? cleanup = await Record.ExceptionAsync(() => scenario.DisposeAsync().AsTask());
        if (cleanup is not null && !ReferenceEquals(cleanup, joined)) throw cleanup;
    }
}
