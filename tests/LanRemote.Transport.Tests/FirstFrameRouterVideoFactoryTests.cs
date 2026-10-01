using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

public sealed partial class FirstFrameRouterBoundaryTests
{
    [Theory(Timeout = 30_000)]
    [InlineData("not-registered")]
    [InlineData("invalid-proof")]
    [InlineData("ack-write")]
    [InlineData("ack-flush")]
    public async Task Factory_Is_Never_Called_Before_Registration_Proof_And_Ack_Flush(string boundary)
    {
        ProbeProducer producer = new((_, _) => ValueTask.FromResult<EncodedFrame?>(null));
        ProbeFactory factory = new((_, _) => ValueTask.FromResult<IVideoFrameProducer>(producer));
        await using BoundaryScenario scenario = new(badProof: boundary == "invalid-proof",
            failWrite: boundary == "ack-write", failFlush: boundary == "ack-flush",
            producerFactory: factory);
        using var registration = boundary == "not-registered" ? null : scenario.Register();
        Task run = scenario.Start();
        if (boundary == "not-registered")
        {
            TimerNotice retry = await scenario.Clock.ReadFirstRetryAsync();
            Assert.Equal(0, factory.Calls);
            scenario.Clock.Manual.Advance(TimeSpan.FromMilliseconds(40), false);
            retry.Dispatch();
            retry = await scenario.Clock.ReadTimerAsync();
            Assert.Equal(0, factory.Calls);
            scenario.Clock.Manual.Advance(TimeSpan.FromMilliseconds(40), false);
            retry.Dispatch();
        }
        await run.WaitAsync(Guard);

        Assert.Equal(0, factory.Calls);
        Assert.Equal(0, producer.Starts);
        Assert.Equal(0, producer.Reads);
        Assert.Equal(0, scenario.Source.Calls);
        Assert.Equal(boundary == "not-registered" ? VideoAttachStatus.NotRegistered :
            boundary == "invalid-proof" ? VideoAttachStatus.InvalidProof : VideoAttachStatus.Attached,
            scenario.Router.AttachStatus);
        if (boundary is "ack-write" or "ack-flush")
            Assert.Equal(VideoAttachStatus.AlreadyAttached, scenario.ProbeAttach());
    }

    [Fact(Timeout = 30_000)]
    public async Task Factory_Is_Called_Once_Only_After_Ack_Write_And_Flush()
    {
        ProbeProducer producer = new((_, _) => ValueTask.FromResult<EncodedFrame?>(null));
        ProbeFactory factory = new((_, _) => ValueTask.FromResult<IVideoFrameProducer>(producer));
        await using BoundaryScenario scenario = new(holdWrite: true, holdFlush: true, producerFactory: factory);
        factory.ObserveFlush = () => scenario.Stream.FlushCompleted;
        using var registration = scenario.Register();
        Task run = scenario.Start();
        await scenario.Stream.WriteEntered.Task.WaitAsync(Guard);
        Assert.Equal(0, factory.Calls);
        Assert.False(scenario.Stream.FlushEntered.Task.IsCompleted);
        Assert.False(run.IsCompleted);
        scenario.Stream.ReleaseWrite();
        await scenario.Stream.FlushEntered.Task.WaitAsync(Guard);
        Assert.True(scenario.Stream.WriteCompleted);
        Assert.False(scenario.Stream.FlushCompleted);
        Assert.Equal(0, factory.Calls);
        Assert.False(run.IsCompleted);
        scenario.Stream.ReleaseFlush();
        await factory.Entered.Task.WaitAsync(Guard);
        await run.WaitAsync(Guard);

        AssertAck(scenario);
        Assert.Equal(SessionId, factory.SessionId);
        Assert.True(factory.SawCompletedFlush);
        Assert.Equal(1, factory.Calls);
        Assert.Equal(1, producer.Starts);
        Assert.Equal(1, producer.Reads);
        Assert.Equal(1, producer.Stops);
        Assert.Equal(1, producer.Disposals);
        Assert.Equal(0, scenario.Source.Calls);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revocation_During_Ack_Flush_Never_Creates_Producer(bool stopHost)
    {
        ProbeProducer producer = new((_, _) => ValueTask.FromResult<EncodedFrame?>(null));
        ProbeFactory factory = new((_, _) => ValueTask.FromResult<IVideoFrameProducer>(producer));
        await using BoundaryScenario scenario = new(holdFlush: true, producerFactory: factory);
        using var registration = scenario.Register();
        Task run = scenario.Start();
        await scenario.Stream.FlushEntered.Task.WaitAsync(Guard);
        if (stopHost) await scenario.HostStop.CancelAsync().WaitAsync(Guard);
        else registration.Dispose();
        await scenario.Stream.Closed.Task.WaitAsync(Guard);
        Assert.False(run.IsCompleted);
        Assert.Equal(0, factory.Calls);
        scenario.Stream.ReleaseFlush();
        if (stopHost)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Guard));
        else
            await run.WaitAsync(Guard);
        Assert.Equal(0, factory.Calls);
        Assert.Equal(0, producer.Starts);
        Assert.Equal(0, scenario.Source.Calls);
    }

    [Fact(Timeout = 30_000)]
    public async Task Factory_Producer_Sends_Actual_Encoded_Frame_And_Stops_After_Eof()
    {
        RecordingVideoOwner owner = new(32);
        VideoFrameTestData.Payload().CopyTo(owner.Bytes, 0);
        using EncodedFrame frame = VideoFrameTestData.Frame(owner);
        ProbeProducer producer = new((read, _) => ValueTask.FromResult<EncodedFrame?>(read == 1 ? frame : null));
        ProbeFactory factory = new((_, _) => ValueTask.FromResult<IVideoFrameProducer>(producer));
        await using BoundaryScenario scenario = new(producerFactory: factory);
        factory.ObserveFlush = () => scenario.Stream.FlushCompleted;
        using var registration = scenario.Register();
        await scenario.Start().WaitAsync(Guard);

        byte[][] writes = scenario.Stream.Writes.ToArray();
        Assert.Equal(3, writes.Length);
        Assert.True(VideoAttachAckFrame.TryParse(writes[0].AsSpan(4), out var ack, out string? rejection), rejection);
        Assert.Equal(SessionId, ack!.SessionId);
        Assert.Equal(VideoFrameTestData.Header(), writes[1]);
        Assert.Equal(VideoFrameTestData.Payload(), writes[2]);
        Assert.Equal(1, owner.DisposeCalls);
        Assert.Equal(1, factory.Calls);
        Assert.Equal(SessionId, factory.SessionId);
        Assert.True(factory.SawCompletedFlush);
        Assert.Equal(1, producer.Starts);
        Assert.Equal(2, producer.Reads);
        Assert.Equal(1, producer.Stops);
        Assert.Equal(1, producer.Disposals);
        Assert.Equal(0, scenario.Source.Calls);
        Assert.True(scenario.Stream.PeerReadCompleted);
    }

    [Fact(Timeout = 30_000)]
    public async Task No_Factory_Keeps_Original_Shared_Source_Path()
    {
        await using BoundaryScenario scenario = new();
        using var registration = scenario.Register();
        await scenario.Start().WaitAsync(Guard);
        AssertAck(scenario);
        Assert.Equal(1, scenario.Source.Calls);
        Assert.True(scenario.Source.SawCompletedFlush);
        Assert.Equal(SessionId, scenario.Source.SessionId);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Blocking_Synchronous_Factory_Prefix_Is_Joined_And_Late_Producer_Is_Stopped(bool stopHost)
    {
        using ManualResetEventSlim release = new(false);
        ProbeProducer producer = new((_, _) => ValueTask.FromResult<EncodedFrame?>(null));
        ProbeFactory factory = new((_, _) =>
        {
            release.Wait(); // 故意忽略取消：必须监督原始同步调用，而不是放弃后台工厂。
            return ValueTask.FromResult<IVideoFrameProducer>(producer);
        });
        BoundaryScenario scenario = new(producerFactory: factory);
        factory.ObserveFlush = () => scenario.Stream.FlushCompleted;
        using var registration = scenario.Register();
        Task run = Task.Run(scenario.Start);
        try
        {
            await factory.Entered.Task.WaitAsync(Guard);
            Assert.True(factory.SawCompletedFlush);
            Assert.Equal(SessionId, factory.SessionId);
            Assert.Equal(1, factory.Calls);
            Assert.False(run.IsCompleted);
            if (stopHost) await scenario.HostStop.CancelAsync().WaitAsync(Guard);
            else registration.Dispose();
            await scenario.Stream.Closed.Task.WaitAsync(Guard);
            Assert.False(run.IsCompleted);
            Assert.Equal(0, producer.Starts);
            Assert.Equal(0, producer.Stops);
            release.Set();
            if (stopHost)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Guard));
            else
                await run.WaitAsync(Guard);
            Assert.Equal(1, factory.Calls);
            Assert.Equal(0, producer.Starts);
            Assert.Equal(0, producer.Reads);
            Assert.Equal(1, producer.Stops);
            Assert.Equal(1, producer.Disposals);
            Assert.Equal(0, scenario.Source.Calls);
        }
        finally
        {
            release.Set();
            try
            {
                await run.WaitAsync(Guard);
            }
            catch (OperationCanceledException) when (stopHost) { }
            finally
            {
                await scenario.DisposeAsync();
            }
        }
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Synchronous_Start_Cannot_Block_Stop_After_Revocation_Or_Host_Cancellation(bool stopHost)
    {
        using ManualResetEventSlim releaseStart = new(false);
        ProbeProducer producer = new((_, _) => ValueTask.FromResult<EncodedFrame?>(null))
        {
            OnStart = () => releaseStart.Wait() // 不响应取消；真实同步 Start 保持在调用栈内。
        };
        ProbeFactory factory = new((_, _) => ValueTask.FromResult<IVideoFrameProducer>(producer));
        BoundaryScenario scenario = new(producerFactory: factory);
        using var registration = scenario.Register();
        Task run = scenario.Start();
        try
        {
            await producer.StartEntered.Task.WaitAsync(Guard);
            Assert.True(scenario.Stream.FlushCompleted);
            Assert.False(producer.StartExited.Task.IsCompleted);
            Assert.False(run.IsCompleted);
            if (stopHost) await scenario.HostStop.CancelAsync().WaitAsync(Guard);
            else registration.Dispose();
            await scenario.Stream.Closed.Task.WaitAsync(Guard);
            await scenario.Stream.JoinIOAsync();
            Assert.True(scenario.Stream.PeerReadCompleted);
            Assert.False(run.IsCompleted);
            Assert.Equal(0, producer.Reads);

            // 看门狗只在旧锁阻塞 Stop 时负责放闸并 join；最终由 Stop/Start
            // 重叠探针断言，而不是把等待超时当作预期结果。
            using CancellationTokenSource stopGuard = new();
            Task guard = Task.Delay(Guard, stopGuard.Token);
            await Task.WhenAny(producer.StopEntered.Task, guard);
            stopGuard.Cancel();
            bool stoppedBeforeRelease = producer.StopEntered.Task.IsCompleted;
            Assert.False(producer.StartExited.Task.IsCompleted);
            Assert.False(run.IsCompleted);
            releaseStart.Set();
            if (stopHost)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Guard));
            else
                await run.WaitAsync(Guard);

            Assert.True(stoppedBeforeRelease, "StopAsync 必须在同步 Start 闸门尚未放行时进入。");
            Assert.True(producer.StoppedDuringStart, "StopAsync 必须与正在执行的 Start 重叠。");
            Assert.True(producer.StartExited.Task.IsCompleted);
            AssertAck(scenario);
            Assert.Equal(1, factory.Calls);
            Assert.Equal(1, producer.Starts);
            Assert.Equal(0, producer.Reads);
            Assert.Equal(1, producer.Stops);
            Assert.Equal(1, producer.Disposals);
            Assert.Equal(0, scenario.Source.Calls);
        }
        finally
        {
            releaseStart.Set();
            try
            {
                await run.WaitAsync(Guard);
            }
            catch (OperationCanceledException) when (stopHost) { }
            finally
            {
                await scenario.DisposeAsync();
            }
        }
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Start_rejection_after_Stop_is_expected_only_with_original_stop_before_start_evidence(
        bool stopHost, bool reportEvidence)
    {
        using ManualResetEventSlim releaseStart = new(false);
        InvalidOperationException rejection = new("Start 被停止抢先拒绝，或者是普通 Start 错误。");
        ProbeProducer producer = reportEvidence
            ? new ProvenStartRejectionProducer(rejection, (_, _) => ValueTask.FromResult<EncodedFrame?>(null))
            {
                OnStart = () => releaseStart.Wait(), StartFailure = rejection
            }
            : new ProbeProducer((_, _) => ValueTask.FromResult<EncodedFrame?>(null))
            {
                OnStart = () => releaseStart.Wait(), StartFailure = rejection
            };
        ProbeFactory factory = new((_, _) => ValueTask.FromResult<IVideoFrameProducer>(producer));
        BoundaryScenario scenario = new(producerFactory: factory);
        using var registration = scenario.Register();
        Task run = scenario.Start();
        try
        {
            await producer.StartEntered.Task.WaitAsync(Guard);
            Assert.True(scenario.Stream.FlushCompleted);
            Assert.False(producer.StartExited.Task.IsCompleted);
            if (stopHost) await scenario.HostStop.CancelAsync().WaitAsync(Guard);
            else registration.Dispose();
            await scenario.Stream.Closed.Task.WaitAsync(Guard);
            await producer.StopEntered.Task.WaitAsync(Guard);
            await producer.Completion.WaitAsync(Guard);
            Assert.True(producer.StoppedDuringStart);
            Assert.False(producer.StartExited.Task.IsCompleted);
            Assert.False(run.IsCompleted);
            Assert.Equal(0, producer.Reads);
            if (reportEvidence)
            {
                IVideoFrameProducerStartRejection evidence = Assert.IsAssignableFrom<IVideoFrameProducerStartRejection>(producer);
                Assert.True(evidence.IsStopBeforeStartRejection(rejection));
                Assert.False(evidence.IsStopBeforeStartRejection(new InvalidOperationException(rejection.Message)));
            }
            else Assert.False(producer is IVideoFrameProducerStartRejection);

            releaseStart.Set();
            if (reportEvidence)
            {
                if (stopHost) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Guard));
                else await run.WaitAsync(Guard);
            }
            else
            {
                Exception? failure = await Record.ExceptionAsync(() => run.WaitAsync(Guard));
                Assert.Same(rejection, failure);
                Assert.True(run.IsFaulted);
                Assert.Same(rejection, Assert.Single(run.Exception!.InnerExceptions));
            }

            Assert.True(producer.StartExited.Task.IsCompleted);
            AssertAck(scenario);
            Assert.Equal(1, factory.Calls);
            Assert.Equal(1, producer.Starts);
            Assert.Equal(0, producer.Reads);
            Assert.Equal(1, producer.Stops);
            Assert.Equal(1, producer.Disposals);
            Assert.Equal(0, scenario.Source.Calls);
        }
        finally
        {
            releaseStart.Set();
            try
            {
                Exception? finished = await Record.ExceptionAsync(() => run.WaitAsync(Guard));
                if (reportEvidence)
                {
                    if (stopHost) Assert.IsAssignableFrom<OperationCanceledException>(finished);
                    else Assert.Null(finished);
                }
                else Assert.Same(rejection, finished);
            }
            finally
            {
                Exception? cleanup = await Record.ExceptionAsync(() => scenario.DisposeAsync().AsTask());
                if (cleanup is not null && (reportEvidence || !ReferenceEquals(cleanup, rejection))) throw cleanup;
            }
        }
    }

    [Theory(Timeout = 30_000)]
    [InlineData("io", false)]
    [InlineData("disposed", true)]
    [InlineData("cancelled", false)]
    [InlineData("nested-aggregate", true)]
    public async Task Throwing_Start_Rejection_Evidence_Keeps_Both_Original_Failures_After_Stop(
        string failureKind, bool stopHost)
    {
        using ManualResetEventSlim releaseStart = new(false);
        InvalidOperationException startFailure = new("Start 的独立原始故障。");
        IOException nestedFirst = new("探针内层 I/O 故障。");
        ObjectDisposedException nestedLast = new("探针内层资源故障。");
        AggregateException nestedSecond = new("探针第二层故障。", nestedLast);
        AggregateException probeTree = new("探针原始聚合故障。", nestedFirst, nestedSecond);
        Exception probeFailure = failureKind switch
        {
            "io" => new IOException("探针原始 I/O 故障。"),
            "disposed" => new ObjectDisposedException("evidence"),
            "cancelled" => new OperationCanceledException("探针原始取消故障。"),
            "nested-aggregate" => probeTree,
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind))
        };
        ThrowingStartRejectionEvidenceProducer producer = new(probeFailure,
            (_, _) => ValueTask.FromResult<EncodedFrame?>(null))
        {
            OnStart = () => releaseStart.Wait(), StartFailure = startFailure
        };
        ProbeFactory factory = new((_, _) => ValueTask.FromResult<IVideoFrameProducer>(producer));
        BoundaryScenario scenario = new(producerFactory: factory);
        using var registration = scenario.Register();
        Task run = scenario.Start();
        Exception? actual = null;
        try
        {
            await producer.StartEntered.Task.WaitAsync(Guard);
            Assert.True(scenario.Stream.FlushCompleted);
            Assert.False(producer.StartExited.Task.IsCompleted);
            if (stopHost) await scenario.HostStop.CancelAsync().WaitAsync(Guard);
            else registration.Dispose();
            await scenario.Stream.Closed.Task.WaitAsync(Guard);
            await scenario.Stream.JoinIOAsync();
            await producer.StopEntered.Task.WaitAsync(Guard);
            await producer.Completion.WaitAsync(Guard);
            Assert.True(producer.StoppedDuringStart);
            Assert.False(producer.StartExited.Task.IsCompleted);
            Assert.False(run.IsCompleted);
            Assert.Equal(1, scenario.Stream.DisposeCount);
            Assert.True(scenario.Stream.PeerReadCompleted);
            Assert.Equal(0, producer.Reads);

            releaseStart.Set();
            actual = await Record.ExceptionAsync(() => run.WaitAsync(Guard));
            AggregateException root = Assert.IsType<AggregateException>(actual);
            Assert.Collection(root.InnerExceptions,
                error => Assert.Same(startFailure, error),
                error => Assert.Same(probeFailure, error));
            if (failureKind == "nested-aggregate")
            {
                Assert.Collection(probeTree.InnerExceptions,
                    error => Assert.Same(nestedFirst, error),
                    error => Assert.Same(nestedSecond, error));
                Assert.Same(nestedLast, Assert.Single(nestedSecond.InnerExceptions));
            }
            Assert.True(run.IsFaulted);
            Assert.Same(root, Assert.Single(run.Exception!.InnerExceptions));
            Assert.Same(startFailure, producer.ObservedRejection);
            Assert.True(producer.SawCompletedStopBeforeProbe);
            Assert.Equal(1, producer.ProbeCalls);
            Assert.True(producer.StartExited.Task.IsCompleted);
            AssertAck(scenario);
            Assert.Equal(1, factory.Calls);
            Assert.Equal(1, producer.Starts);
            Assert.Equal(0, producer.Reads);
            Assert.Equal(1, producer.Stops);
            Assert.Equal(1, producer.Disposals);
            Assert.Equal(0, scenario.Source.Calls);
        }
        finally
        {
            releaseStart.Set();
            scenario.Stream.ReleaseAll();
            Exception? joined = null;
            try
            {
                joined = await Record.ExceptionAsync(() => run.WaitAsync(Guard));
                if (joined is TimeoutException) throw joined;
                if (actual is not null) Assert.Same(actual, joined);
            }
            finally
            {
                Exception? cleanup = await Record.ExceptionAsync(() => scenario.DisposeAsync().AsTask());
                if (cleanup is not null && !ReferenceEquals(cleanup, joined)) throw cleanup;
            }
        }
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_Precedes_Joining_Uncooperative_Read_And_Late_Frame_Cannot_Restart(bool stopHost)
    {
        RecordingVideoOwner owner = new(32);
        VideoFrameTestData.Payload().CopyTo(owner.Bytes, 0);
        using EncodedFrame frame = VideoFrameTestData.Frame(owner);
        TaskCompletionSource<EncodedFrame?> releaseRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ProbeProducer producer = new((_, _) => new ValueTask<EncodedFrame?>(releaseRead.Task));
        ProbeFactory factory = new((_, _) => ValueTask.FromResult<IVideoFrameProducer>(producer));
        BoundaryScenario scenario = new(producerFactory: factory);
        using var registration = scenario.Register();
        Task run = scenario.Start();
        try
        {
            await producer.ReadEntered.Task.WaitAsync(Guard);
            Assert.Equal(1, producer.Reads);
            if (stopHost) await scenario.HostStop.CancelAsync().WaitAsync(Guard);
            else registration.Dispose();
            await producer.StopEntered.Task.WaitAsync(Guard);
            await producer.Completion.WaitAsync(Guard);
            // Stop 已进入时路由已请求关闭；重复调用取得同一个物理 Close 任务并 join。
            await scenario.Connection.CloseAsync().WaitAsync(Guard);
            // 只 join SSL I/O（含 peer reader），不等待生产者仍挂起的 ReadNextAsync。
            await scenario.Stream.JoinIOAsync();
            Assert.True(scenario.Stream.PeerReadCompleted);
            Assert.False(run.IsCompleted);
            Assert.False(releaseRead.Task.IsCompleted);
            Assert.Equal(0, owner.DisposeCalls);
            Assert.Equal(1, factory.Calls);
            releaseRead.TrySetResult(frame);
            if (stopHost)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Guard));
            else
                await run.WaitAsync(Guard);
            Assert.Equal(1, owner.DisposeCalls);
            Assert.Single(scenario.Stream.Writes); // 迟到的帧不能写出，只有 ACK。
            Assert.Equal(1, factory.Calls);
            Assert.Equal(1, producer.Starts);
            Assert.Equal(1, producer.Reads);
            Assert.Equal(1, producer.Stops);
            Assert.Equal(1, producer.Disposals);
            Assert.Equal(0, scenario.Source.Calls);
        }
        finally
        {
            releaseRead.TrySetResult(frame);
            try
            {
                await run.WaitAsync(Guard);
            }
            catch (OperationCanceledException) when (stopHost) { }
            finally
            {
                await scenario.DisposeAsync();
            }
        }
    }

    [Theory(Timeout = 30_000)]
    [InlineData("stop-io")]
    [InlineData("stop-disposed")]
    [InlineData("stop-aggregate")]
    [InlineData("dispose-io")]
    [InlineData("dispose-disposed")]
    [InlineData("dispose-aggregate")]
    public async Task Producer_Stop_Or_Dispose_Fault_Is_Never_Suppressed_As_Network_End(string stage)
    {
        Exception failure = stage.EndsWith("io", StringComparison.Ordinal)
            ? new IOException("生产者原始 I/O 故障。")
            : stage.EndsWith("disposed", StringComparison.Ordinal)
                ? new ObjectDisposedException("producer")
                : new AggregateException("生产者原始聚合故障。",
                    new IOException("第一项。"), new ObjectDisposedException("第二项。"));
        ProbeProducer producer = new((_, _) => ValueTask.FromResult<EncodedFrame?>(null))
        {
            StopFailure = stage.StartsWith("stop", StringComparison.Ordinal) ? failure : null,
            DisposeFailure = stage.StartsWith("dispose", StringComparison.Ordinal) ? failure : null
        };
        ProbeFactory factory = new((_, _) => ValueTask.FromResult<IVideoFrameProducer>(producer));
        BoundaryScenario scenario = new(producerFactory: factory);
        using var registration = scenario.Register();
        try
        {
            Exception? actual = await Record.ExceptionAsync(() => scenario.Start().WaitAsync(Guard));
            Assert.Same(failure, actual);
            Assert.Equal(1, factory.Calls);
            Assert.Equal(1, producer.Starts);
            Assert.Equal(1, producer.Reads);
            Assert.Equal(1, producer.Stops);
            Assert.Equal(1, producer.Disposals);
            Assert.True(scenario.Stream.PeerReadCompleted);
            Assert.Equal(0, scenario.Source.Calls);
        }
        finally
        {
            // fixture 仍真实 join faulted run；只接纳已断言的同一个原始故障，不吞 Guard/清理故障。
            Exception? cleanup = await Record.ExceptionAsync(() => scenario.DisposeAsync().AsTask());
            if (cleanup is not null && !ReferenceEquals(cleanup, failure)) throw cleanup;
        }
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Start_And_Stop_Cleanup_Failures_Keep_Their_Unflattened_Original_Trees(bool failDispose)
    {
        IOException startLeaf = new("Start 原始 I/O 故障。");
        InvalidOperationException nestedStartLeaf = new("Start 内层故障。");
        AggregateException startNested = new("Start 内层树。", nestedStartLeaf);
        AggregateException startFailure = new("Start 原始树。", startLeaf, startNested);
        IOException stopFirst = new("Stop 第一项。");
        ObjectDisposedException stopSecond = new("Stop 第二项。");
        AggregateException stopFailure = new("Stop 原始双故障树。", stopFirst, stopSecond);
        InvalidOperationException disposeFailure = new("Dispose 独立故障。");
        ProbeProducer producer = new((_, _) => ValueTask.FromResult<EncodedFrame?>(null))
        {
            StartFailure = startFailure,
            StopFailure = stopFailure,
            DisposeFailure = failDispose ? disposeFailure : null
        };
        ProbeFactory factory = new((_, _) => ValueTask.FromResult<IVideoFrameProducer>(producer));
        BoundaryScenario scenario = new(producerFactory: factory);
        using var registration = scenario.Register();
        Task run = scenario.Start();
        Exception? actual = null;
        try
        {
            actual = await Record.ExceptionAsync(() => run.WaitAsync(Guard));
            AggregateException root = Assert.IsType<AggregateException>(actual);
            if (failDispose)
                Assert.Collection(root.InnerExceptions,
                    error => Assert.Same(startFailure, error),
                    error => Assert.Same(stopFailure, error),
                    error => Assert.Same(disposeFailure, error));
            else
                Assert.Collection(root.InnerExceptions,
                    error => Assert.Same(startFailure, error),
                    error => Assert.Same(stopFailure, error));
            Assert.Collection(startFailure.InnerExceptions,
                error => Assert.Same(startLeaf, error),
                error => Assert.Same(startNested, error));
            Assert.Same(nestedStartLeaf, Assert.Single(startNested.InnerExceptions));
            Assert.Collection(stopFailure.InnerExceptions,
                error => Assert.Same(stopFirst, error),
                error => Assert.Same(stopSecond, error));
            Assert.True(run.IsFaulted);
            Assert.Same(root, Assert.Single(run.Exception!.InnerExceptions));
            Assert.Equal(1, factory.Calls);
            Assert.Equal(1, producer.Starts);
            Assert.Equal(0, producer.Reads);
            Assert.Equal(1, producer.Stops);
            Assert.Equal(1, producer.Disposals);
            Assert.True(scenario.Stream.PeerReadCompleted);
            Assert.Equal(0, scenario.Source.Calls);
        }
        finally
        {
            Exception? cleanup = await Record.ExceptionAsync(() => scenario.DisposeAsync().AsTask());
            if (cleanup is not null && !ReferenceEquals(cleanup, actual)) throw cleanup;
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Distinct_Stop_And_Dispose_Tasks_Preserve_Both_References_To_The_Same_Failure()
    {
        IOException failure = new("两个原任务各自包含同一个故障实例。");
        Task stopTask = Task.FromException(failure);
        Task disposeTask = Task.FromException(failure);
        Assert.NotSame(stopTask, disposeTask);
        ProbeProducer producer = new((_, _) => ValueTask.FromResult<EncodedFrame?>(null))
        {
            StopTaskFactory = () => stopTask,
            DisposeTaskFactory = () => disposeTask
        };
        ProbeFactory factory = new((_, _) => ValueTask.FromResult<IVideoFrameProducer>(producer));
        BoundaryScenario scenario = new(producerFactory: factory);
        using var registration = scenario.Register();
        Task run = scenario.Start();
        Exception? actual = null;
        try
        {
            actual = await Record.ExceptionAsync(() => run.WaitAsync(Guard));
            AggregateException root = Assert.IsType<AggregateException>(actual);
            Assert.Collection(root.InnerExceptions,
                error => Assert.Same(failure, error),
                error => Assert.Same(failure, error));
            Assert.True(run.IsFaulted);
            Assert.Same(root, Assert.Single(run.Exception!.InnerExceptions));
            AssertAck(scenario);
            Assert.True(scenario.Stream.PeerReadCompleted);
            Assert.Equal(1, factory.Calls);
            Assert.Equal(1, producer.Starts);
            Assert.Equal(1, producer.Reads);
            Assert.Equal(1, producer.Stops);
            Assert.Equal(1, producer.Disposals);
            Assert.Equal(0, scenario.Source.Calls);
        }
        finally
        {
            Exception? joined = null;
            try
            {
                joined = await Record.ExceptionAsync(() => run.WaitAsync(Guard));
                if (joined is TimeoutException) throw joined;
                if (actual is not null) Assert.Same(actual, joined);
            }
            finally
            {
                Exception? cleanup = await Record.ExceptionAsync(() => scenario.DisposeAsync().AsTask());
                if (cleanup is not null && !ReferenceEquals(cleanup, joined)) throw cleanup;
            }
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Synchronous_Stop_Throw_And_Separate_Dispose_Task_Keep_The_Same_Failure_Twice()
    {
        IOException failure = new("Stop 同步抛出，Dispose 的任务再次抛出同一实例。");
        Task disposeTask = Task.FromException(failure);
        ProbeProducer producer = new((_, _) => ValueTask.FromResult<EncodedFrame?>(null))
        {
            StopTaskFactory = () => throw failure,
            DisposeTaskFactory = () => disposeTask
        };
        ProbeFactory factory = new((_, _) => ValueTask.FromResult<IVideoFrameProducer>(producer));
        BoundaryScenario scenario = new(producerFactory: factory);
        using var registration = scenario.Register();
        Task run = scenario.Start();
        Exception? actual = null;
        try
        {
            actual = await Record.ExceptionAsync(() => run.WaitAsync(Guard));
            AggregateException root = Assert.IsType<AggregateException>(actual);
            Assert.Collection(root.InnerExceptions,
                error => Assert.Same(failure, error),
                error => Assert.Same(failure, error));
            Assert.True(run.IsFaulted);
            Assert.Same(root, Assert.Single(run.Exception!.InnerExceptions));
            AssertAck(scenario);
            Assert.True(scenario.Stream.PeerReadCompleted);
            Assert.Equal(1, factory.Calls);
            Assert.Equal(1, producer.Starts);
            Assert.Equal(1, producer.Reads);
            Assert.Equal(1, producer.Stops);
            Assert.Equal(1, producer.Disposals);
            Assert.Equal(0, scenario.Source.Calls);
        }
        finally
        {
            Exception? joined = null;
            try
            {
                joined = await Record.ExceptionAsync(() => run.WaitAsync(Guard));
                if (joined is TimeoutException) throw joined;
                if (actual is not null) Assert.Same(actual, joined);
            }
            finally
            {
                Exception? cleanup = await Record.ExceptionAsync(() => scenario.DisposeAsync().AsTask());
                if (cleanup is not null && !ReferenceEquals(cleanup, joined)) throw cleanup;
            }
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Shared_Stop_And_Dispose_Task_Keeps_One_Unflattened_Multi_Fault_Tree()
    {
        IOException first = new("原任务第一及第三项是同一故障实例。");
        InvalidOperationException nestedFirst = new("原任务内层第一项。");
        ObjectDisposedException nestedSecond = new("原任务内层第二项。");
        AggregateException nested = new("原任务内层树。", nestedFirst, nestedSecond, nestedFirst);
        TaskCompletionSource faults = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(faults.TrySetException(new Exception[] { first, nested, first }));
        Task sharedTask = faults.Task;
        ProbeProducer producer = new((_, _) => ValueTask.FromResult<EncodedFrame?>(null))
        {
            StopTaskFactory = () => sharedTask,
            DisposeTaskFactory = () => sharedTask
        };
        ProbeFactory factory = new((_, _) => ValueTask.FromResult<IVideoFrameProducer>(producer));
        BoundaryScenario scenario = new(producerFactory: factory);
        using var registration = scenario.Register();
        Task run = scenario.Start();
        Exception? actual = null;
        try
        {
            actual = await Record.ExceptionAsync(() => run.WaitAsync(Guard));
            AggregateException root = Assert.IsType<AggregateException>(actual);
            Assert.Collection(root.InnerExceptions,
                error => Assert.Same(first, error),
                error => Assert.Same(nested, error),
                error => Assert.Same(first, error));
            Assert.Collection(nested.InnerExceptions,
                error => Assert.Same(nestedFirst, error),
                error => Assert.Same(nestedSecond, error),
                error => Assert.Same(nestedFirst, error));
            Assert.True(run.IsFaulted);
            Assert.Same(root, Assert.Single(run.Exception!.InnerExceptions));
            AssertAck(scenario);
            Assert.True(scenario.Stream.PeerReadCompleted);
            Assert.Equal(1, factory.Calls);
            Assert.Equal(1, producer.Starts);
            Assert.Equal(1, producer.Reads);
            Assert.Equal(1, producer.Stops);
            Assert.Equal(1, producer.Disposals);
            Assert.Equal(0, scenario.Source.Calls);
        }
        finally
        {
            Exception? joined = null;
            try
            {
                joined = await Record.ExceptionAsync(() => run.WaitAsync(Guard));
                if (joined is TimeoutException) throw joined;
                if (actual is not null) Assert.Same(actual, joined);
            }
            finally
            {
                Exception? cleanup = await Record.ExceptionAsync(() => scenario.DisposeAsync().AsTask());
                if (cleanup is not null && !ReferenceEquals(cleanup, joined)) throw cleanup;
            }
        }
    }

    private sealed class ProbeFactory(Func<Guid, CancellationToken, ValueTask<IVideoFrameProducer>> create)
        : IVideoFrameProducerFactory
    {
        private int _calls;
        internal TaskCompletionSource Entered { get; } = Signal();
        internal int Calls => Volatile.Read(ref _calls);
        internal Guid SessionId { get; private set; }
        internal Func<bool>? ObserveFlush { get; set; }
        internal bool SawCompletedFlush { get; private set; }

        public ValueTask<IVideoFrameProducer> CreateAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            SessionId = sessionId;
            SawCompletedFlush = ObserveFlush?.Invoke() ?? false;
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            return create(sessionId, cancellationToken);
        }
    }

    private sealed class ProvenStartRejectionProducer(
        InvalidOperationException rejection, Func<int, CancellationToken, ValueTask<EncodedFrame?>> read)
        : ProbeProducer(read), IVideoFrameProducerStartRejection
    {
        public bool IsStopBeforeStartRejection(InvalidOperationException error) =>
            StopEntered.Task.IsCompleted && ReferenceEquals(rejection, error);
    }

    private sealed class ThrowingStartRejectionEvidenceProducer(
        Exception failure, Func<int, CancellationToken, ValueTask<EncodedFrame?>> read)
        : ProbeProducer(read), IVideoFrameProducerStartRejection
    {
        private int _probeCalls;
        internal int ProbeCalls => Volatile.Read(ref _probeCalls);
        internal InvalidOperationException? ObservedRejection { get; private set; }
        internal bool SawCompletedStopBeforeProbe { get; private set; }

        public bool IsStopBeforeStartRejection(InvalidOperationException error)
        {
            ObservedRejection = error;
            SawCompletedStopBeforeProbe = StopEntered.Task.IsCompleted && Completion.IsCompleted;
            Interlocked.Increment(ref _probeCalls);
            throw failure;
        }
    }

    private class ProbeProducer(Func<int, CancellationToken, ValueTask<EncodedFrame?>> read)
        : IVideoFrameProducer
    {
        private readonly TaskCompletionSource _completion = Signal();
        private int _starts;
        private int _reads;
        private int _stops;
        private int _disposals;
        private int _startActive;
        private int _stoppedDuringStart;
        internal Action? OnStart { get; init; }
        internal Exception? StartFailure { get; init; }
        internal Exception? StopFailure { get; init; }
        internal Exception? DisposeFailure { get; init; }
        internal Func<Task>? StopTaskFactory { get; init; }
        internal Func<Task>? DisposeTaskFactory { get; init; }
        internal int Starts => Volatile.Read(ref _starts);
        internal int Reads => Volatile.Read(ref _reads);
        internal int Stops => Volatile.Read(ref _stops);
        internal int Disposals => Volatile.Read(ref _disposals);
        internal bool StoppedDuringStart => Volatile.Read(ref _stoppedDuringStart) != 0;
        internal TaskCompletionSource StartEntered { get; } = Signal();
        internal TaskCompletionSource StartExited { get; } = Signal();
        internal TaskCompletionSource ReadEntered { get; } = Signal();
        internal TaskCompletionSource StopEntered { get; } = Signal();
        public Task Completion => _completion.Task;

        public void Start()
        {
            Interlocked.Increment(ref _starts);
            Volatile.Write(ref _startActive, 1);
            StartEntered.TrySetResult();
            try
            {
                OnStart?.Invoke();
                if (StartFailure is { } error) throw error;
            }
            finally
            {
                Volatile.Write(ref _startActive, 0);
                StartExited.TrySetResult();
            }
        }
        public ValueTask<EncodedFrame?> ReadNextAsync(CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _reads);
            ReadEntered.TrySetResult();
            return read(call, cancellationToken);
        }

        public Task StopAsync()
        {
            if (Volatile.Read(ref _startActive) != 0)
                Volatile.Write(ref _stoppedDuringStart, 1);
            Interlocked.Increment(ref _stops);
            StopEntered.TrySetResult();
            if (StopFailure is { } error) _completion.TrySetException(error);
            else _completion.TrySetResult();
            return StopTaskFactory?.Invoke() ?? Completion;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposals);
            if (DisposeTaskFactory is { } taskFactory) return new ValueTask(taskFactory());
            // 独立故障用于检验异常清理边界；正常及 Stop 故障均共享原 Completion。
            return DisposeFailure is { } error
                ? new ValueTask(Task.FromException(error))
                : new ValueTask(Completion);
        }
    }
}
