using System;
using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using Xunit;
using Xunit.Sdk;

namespace LanRemote.Sessions.Tests;

public sealed class FramePipelineVideoProducerFactoryTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(30);
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    public async Task Constructor_is_idle_and_successful_creation_does_not_start_or_capture(int raw, int encoded)
    {
        var f = new Fixture();
        try
        {
            IVideoFrameProducerFactory factory = f.Factory(raw, encoded);
            f.AssertCalls(0, 0, 0);
            Assert.Equal(0, f.Clock.TimestampCalls);
            IVideoFrameProducer producer = await f.Create(factory);
            f.AssertCalls(1, 1, 1);
            Assert.Equal(0, f.Capture.CaptureCount);
            Assert.Equal(0, f.Encoder.EncodeCount);
            Assert.Equal(0, f.Clock.TimestampCalls);
            Assert.Throws<InvalidOperationException>(() => { _ = f.Read(producer); });
            Task completion = producer.Completion;
            Assert.Same(completion, producer.Completion);
            Assert.Same(completion, producer.StopAsync());
            Assert.Same(completion, producer.DisposeAsync().AsTask());
            await Success(completion);
            Assert.Throws<InvalidOperationException>(producer.Start);
            f.AssertDisposed(1, 0);
            f.AssertBorrowed();
        }
        finally { await f.Finish(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Constructor_rejects_null_delegates_without_invoking_either(bool capture)
    {
        var f = new Fixture();
        try
        {
            var error = Assert.Throws<ArgumentNullException>(() => new FramePipelineVideoProducerFactory(
                capture ? null! : f.Target.CreateCapture, capture ? f.Target.CreateEncoder : null!));
            Assert.Equal(capture ? "createCapture" : "createEncoder", error.ParamName);
            f.AssertCalls(0, 0, 0);
            f.AssertDisposed(0, 0);
        }
        finally { await f.Finish(); }
    }

    [Theory]
    [InlineData(true, int.MinValue)]
    [InlineData(true, -1)]
    [InlineData(true, 0)]
    [InlineData(true, 3)]
    [InlineData(true, int.MaxValue)]
    [InlineData(false, int.MinValue)]
    [InlineData(false, -1)]
    [InlineData(false, 0)]
    [InlineData(false, 3)]
    [InlineData(false, int.MaxValue)]
    public async Task Constructor_rejects_invalid_capacity_without_acquiring_resources(bool raw, int capacity)
    {
        var f = new Fixture();
        try
        {
            var error = Assert.Throws<ArgumentOutOfRangeException>(() => f.Factory(raw ? capacity : 2, raw ? 2 : capacity));
            Assert.Equal(raw ? "rawCapacity" : "encodedCapacity", error.ParamName);
            Assert.Equal(capacity, error.ActualValue);
            f.AssertCalls(0, 0, 0);
            f.AssertDisposed(0, 0);
        }
        finally { await f.Finish(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_session_id_is_rejected_synchronously_even_when_creation_is_canceled(bool canceled)
    {
        var f = new Fixture();
        try
        {
            if (canceled) f.CreationCancellation.Cancel();
            IVideoFrameProducerFactory factory = f.Factory();
            var error = Assert.Throws<ArgumentException>(() => { _ = factory.CreateAsync(Guid.Empty, f.CreationCancellation.Token); });
            Assert.Equal("sessionId", error.ParamName);
            f.AssertCalls(0, 0, 0);
            f.AssertDisposed(0, 0);
        }
        finally { await f.Finish(); }
    }

    [Fact]
    public async Task Pre_canceled_creation_acquires_nothing_and_preserves_creation_token()
    {
        var f = new Fixture();
        try
        {
            f.CreationCancellation.Cancel();
            Task<IVideoFrameProducer> creation = await f.Begin(f.Factory()).Published();
            var error = Assert.IsAssignableFrom<OperationCanceledException>(await Error(creation));
            Assert.Equal(f.CreationCancellation.Token, error.CancellationToken);
            Assert.True(creation.IsCanceled);
            f.AssertCalls(0, 0, 0);
            f.AssertDisposed(0, 0);
        }
        finally { await f.Finish(); }
    }

    [Theory]
    [InlineData("capture", false)]
    [InlineData("displays", false)]
    [InlineData("encoder", false)]
    [InlineData("capture", true)]
    [InlineData("displays", true)]
    [InlineData("encoder", true)]
    public async Task Creation_synchronous_prefix_runs_in_background_and_cancellation_joins_original_call(string stage, bool cancel)
    {
        var f = new Fixture();
        Gate held = f.Stage(stage);
        held.Hold();
        try
        {
            Request request = f.Begin(f.Factory());
            await Success(held.Entered.Task);
            Assert.NotEqual(request.CallerThread, held.ThreadId);
            Assert.NotSame(request.Outer, held.ExecutingTask);
            Task<IVideoFrameProducer> creation = await request.Published();
            Task originalInvocation = Assert.IsAssignableFrom<Task>(held.ExecutingTask);
            await AssertForwarded(originalInvocation, creation);
            if (cancel)
            {
                f.CreationCancellation.Cancel();
                // 闸门仍闭合，必须是原 Task.Run 调用的解包链，而不是提前取消的代理。
                await AssertForwarded(originalInvocation, creation);
                Assert.Equal(0, f.Capture.Cleanup.TotalCalls);
                Assert.Equal(0, f.Encoder.Cleanup.TotalCalls);
            }
            held.Open();
            if (cancel)
            {
                var error = Assert.IsAssignableFrom<OperationCanceledException>(await Error(creation));
                Assert.Equal(f.CreationCancellation.Token, error.CancellationToken);
                Assert.True(creation.IsCanceled);
                f.AssertCalls(1, stage == "capture" ? 0 : 1, stage == "encoder" ? 1 : 0);
                Assert.Equal(1, f.Capture.Cleanup.AsyncCalls);
                Assert.Equal(stage == "encoder" ? 1 : 0, f.Encoder.Cleanup.AsyncCalls);
            }
            else
            {
                IVideoFrameProducer producer = await Success(creation);
                await Success(producer.StopAsync());
                f.AssertDisposed(1, 0);
                f.AssertCalls(1, 1, 1);
            }
            // 独立 await 原执行任务及其真正返回的异步子任务，不能只检查 IsCompleted。
            await f.JoinInvocation(originalInvocation);
            Assert.Equal(0, f.Capture.CaptureCount);
            Assert.Equal(0, f.Encoder.EncodeCount);
        }
        finally { held.Open(); await f.Finish(); }
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("displays")]
    [InlineData("encoder")]
    public async Task Late_original_call_failure_wins_over_creation_cancellation_and_rolls_back(string stage)
    {
        var f = new Fixture();
        Gate held = f.Stage(stage);
        held.Hold();
        var original = new AggregateException("原调用的嵌套异常。", new IOException("原叶。"));
        f.FailStage(stage, original);
        try
        {
            Request request = f.Begin(f.Factory());
            await Success(held.Entered.Task);
            Assert.NotEqual(request.CallerThread, held.ThreadId);
            Task<IVideoFrameProducer> creation = await request.Published();
            f.CreationCancellation.Cancel();
            await AssertForwarded(Assert.IsAssignableFrom<Task>(held.ExecutingTask), creation);
            held.Open();
            Assert.Same(original, await Error(creation));
            Assert.True(creation.IsFaulted);
            Assert.Equal(stage == "capture" ? 0 : 1, f.Capture.Cleanup.AsyncCalls);
            Assert.Equal(0, f.Encoder.Cleanup.TotalCalls);
            f.AssertCalls(1, stage == "capture" ? 0 : 1, stage == "encoder" ? 1 : 0);
            await f.JoinInvocation(held.ExecutingTask!);
        }
        finally { held.Open(); await f.Finish(); }
    }

    [Theory]
    [InlineData("capture-throw")]
    [InlineData("capture-null")]
    [InlineData("displays-throw")]
    [InlineData("displays-null")]
    [InlineData("empty")]
    [InlineData("no-primary")]
    [InlineData("multiple-primary")]
    [InlineData("duplicate-primary-reference")]
    [InlineData("enumerate-before")]
    [InlineData("enumerate-after")]
    [InlineData("encoder-throw")]
    [InlineData("encoder-null")]
    public async Task Every_reachable_local_creation_failure_rolls_back_only_acquired_dependencies(string site)
    {
        var f = new Fixture();
        var original = new IOException("创建原错误。" + site);
        switch (site)
        {
            case "capture-throw": f.FailStage("capture", original); break;
            case "capture-null": f.Target.CaptureResult = () => null!; break;
            case "displays-throw": f.FailStage("displays", original); break;
            case "displays-null": f.Capture.Displays = () => null!; break;
            case "empty": f.Capture.Displays = () => []; break;
            case "no-primary": f.Capture.Displays = () => [Display(41, false), Display(73, false)]; break;
            case "multiple-primary": f.Capture.Displays = () => [Display(41, true), Display(73, true)]; break;
            case "duplicate-primary-reference":
                DisplayInfo primary = Display(41, true);
                f.Capture.Displays = () => [primary, primary];
                break;
            case "enumerate-before": f.Capture.Displays = () => new DisplayList(() => throw original, false); break;
            case "enumerate-after": f.Capture.Displays = () => new DisplayList(() => throw original, true); break;
            case "encoder-throw": f.FailStage("encoder", original); break;
            case "encoder-null": f.Target.EncoderResult = () => null!; break;
        }
        try
        {
            Task<IVideoFrameProducer> creation = await f.Begin(f.Factory()).Published();
            Exception error = await Error(creation);
            if (site.EndsWith("throw", StringComparison.Ordinal) || site.StartsWith("enumerate", StringComparison.Ordinal))
                Assert.Same(original, error);
            else Assert.IsType<InvalidOperationException>(error);
            Assert.True(creation.IsFaulted);
            bool acquired = !site.StartsWith("capture", StringComparison.Ordinal);
            f.AssertCalls(1, acquired ? 1 : 0, site.StartsWith("encoder", StringComparison.Ordinal) ? 1 : 0);
            Assert.Equal(acquired ? 1 : 0, f.Capture.Cleanup.AsyncCalls);
            Assert.Equal(0, f.Capture.Cleanup.SyncCalls);
            Assert.Equal(0, f.Encoder.Cleanup.TotalCalls);
            Assert.Equal(0, f.Capture.CaptureCount);
            Assert.Equal(0, f.Encoder.EncodeCount);
            f.AssertBorrowed();
        }
        finally { await f.Finish(); }
    }

    [Fact]
    public async Task Cancellation_during_primary_selection_rolls_back_before_encoder_creation()
    {
        var f = new Fixture();
        f.Capture.Displays = () => new DisplayList(f.CreationCancellation.Cancel, true);
        try
        {
            Task<IVideoFrameProducer> creation = await f.Begin(f.Factory()).Published();
            var error = Assert.IsAssignableFrom<OperationCanceledException>(await Error(creation));
            Assert.Equal(f.CreationCancellation.Token, error.CancellationToken);
            f.AssertCalls(1, 1, 0);
            Assert.Equal(1, f.Capture.Cleanup.AsyncCalls);
            Assert.Equal(0, f.Encoder.Cleanup.TotalCalls);
        }
        finally { await f.Finish(); }
    }

    [Theory]
    [InlineData("displays")]
    [InlineData("selection")]
    [InlineData("encoder")]
    public async Task Rollback_keeps_original_exception_and_all_nested_multitask_cleanup_roots_by_reference(string site)
    {
        var f = new Fixture();
        var original = new AggregateException("原创建异常节点。", new IOException("原创建叶。"));
        Exception[] cleanup = Roots("multiple");
        f.Capture.Cleanup.FailTask(cleanup);
        if (site == "selection") f.Capture.Displays = () => new DisplayList(() => throw original, true);
        else f.FailStage(site, original);
        try
        {
            Task<IVideoFrameProducer> creation = await f.Begin(f.Factory()).Published();
            var failure = Assert.IsType<AggregateException>(await Error(creation));
            Assert.Equal(2, failure.InnerExceptions.Count);
            Assert.Same(original, failure.InnerExceptions[0]);
            Children(Assert.IsType<AggregateException>(failure.InnerExceptions[1]), cleanup);
            Children(f.Capture.Cleanup.Original.Task.Exception!, cleanup);
            Assert.Equal(1, f.Capture.Cleanup.AsyncCalls);
            Assert.Equal(0, f.Encoder.Cleanup.TotalCalls);
        }
        finally { await f.Finish(); }
    }

    [Theory]
    [InlineData(false, "single")]
    [InlineData(false, "nested")]
    [InlineData(false, "multiple")]
    [InlineData(false, "faulted-oce")]
    [InlineData(false, "sync-oce")]
    [InlineData(false, "canceled")]
    [InlineData(true, "single")]
    [InlineData(true, "nested")]
    [InlineData(true, "multiple")]
    [InlineData(true, "faulted-oce")]
    [InlineData(true, "sync-oce")]
    [InlineData(true, "canceled")]
    public async Task Both_cleanup_failures_are_retained_without_flattening_deduplication_or_cancellation_disguise(bool rollback, string shape)
    {
        var f = new Fixture();
        Exception[] captureRoots = Roots(shape);
        Exception[] encoderRoots = Roots(shape);
        using var foreign = new CancellationTokenSource();
        foreign.Cancel();
        f.Capture.Cleanup.SetFailure(shape, captureRoots, foreign.Token);
        f.Encoder.Cleanup.SetFailure(shape, encoderRoots, foreign.Token);
        if (rollback) f.Target.AfterEncoder = f.CreationCancellation.Cancel;
        try
        {
            Task<IVideoFrameProducer> creation = await f.Begin(f.Factory()).Published();
            IVideoFrameProducer? producer = rollback ? null : await Success(creation);
            Task operation = producer is null ? creation : producer.StopAsync();
            var failure = Assert.IsType<AggregateException>(await Error(operation));
            Assert.True(operation.IsFaulted);
            Assert.Equal(rollback ? 3 : 2, failure.InnerExceptions.Count);
            if (rollback)
            {
                var original = Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerExceptions[0]);
                Assert.Equal(f.CreationCancellation.Token, original.CancellationToken);
            }
            var branches = failure.InnerExceptions.Skip(rollback ? 1 : 0).Select(Assert.IsType<AggregateException>).ToList();
            foreach ((Cleanup source, Exception[] expected) in new[] { (f.Capture.Cleanup, captureRoots), (f.Encoder.Cleanup, encoderRoots) })
            {
                if (shape == "canceled")
                {
                    // Canceled Task 不保证不同 await 创建的 TaskCanceledException 是同一个实例。
                    var branch = branches[0];
                    var canceled = Assert.IsAssignableFrom<OperationCanceledException>(Assert.Single(branch.InnerExceptions));
                    Assert.Equal(foreign.Token, canceled.CancellationToken);
                    Assert.True(source.Original.Task.IsCanceled);
                    branches.RemoveAt(0);
                }
                else
                {
                    AggregateException branch = Assert.Single(branches, node => SameChildren(node, expected));
                    Children(branch, expected);
                    branches.Remove(branch);
                    if (shape != "sync-oce") Children(source.Original.Task.Exception!, expected);
                }
            }
            Assert.Empty(branches);
            f.AssertDisposed(1, 0);
            if (producer is not null)
            {
                Assert.Same(operation, producer.Completion);
                Assert.Same(operation, producer.StopAsync());
                Assert.Same(operation, producer.DisposeAsync().AsTask());
                Assert.Same(failure, await Error(producer.DisposeAsync().AsTask()));
            }
        }
        finally { await f.Finish(); }
    }

    [Theory]
    [InlineData(false, false, "none")]
    [InlineData(false, false, "sync")]
    [InlineData(false, false, "async")]
    [InlineData(false, false, "dual")]
    [InlineData(false, true, "none")]
    [InlineData(false, true, "sync")]
    [InlineData(false, true, "async")]
    [InlineData(false, true, "dual")]
    [InlineData(true, false, "none")]
    [InlineData(true, false, "sync")]
    [InlineData(true, false, "async")]
    [InlineData(true, false, "dual")]
    [InlineData(true, true, "none")]
    [InlineData(true, true, "sync")]
    [InlineData(true, true, "async")]
    [InlineData(true, true, "dual")]
    public async Task Ownership_prefers_async_and_deduplicates_only_shared_reference_not_value_equality(bool rollback, bool shared, string kind)
    {
        var f = new Fixture(kind, shared);
        if (rollback) f.Target.AfterEncoder = f.CreationCancellation.Cancel;
        try
        {
            Task<IVideoFrameProducer> creation = await f.Begin(f.Factory()).Published();
            if (rollback) Assert.IsAssignableFrom<OperationCanceledException>(await Error(creation));
            else
            {
                IVideoFrameProducer producer = await Success(creation);
                await Success(producer.StopAsync());
                await Success(producer.DisposeAsync().AsTask());
                await Success(producer.StopAsync());
            }
            if (shared) Assert.Same(f.Capture, f.Encoder);
            else
            {
                Assert.NotSame(f.Capture, f.Encoder);
                Assert.True(f.Capture.Equals(f.Encoder));
            }
            foreach (Dependency resource in f.Resources)
            {
                Assert.Equal(kind is "async" or "dual" ? 1 : 0, resource.Cleanup.AsyncCalls);
                Assert.Equal(kind == "sync" ? 1 : 0, resource.Cleanup.SyncCalls);
            }
            f.AssertBorrowed();
        }
        finally { await f.Finish(); }
    }

    [Theory]
    [InlineData(false, "capture", "sync")]
    [InlineData(false, "encoder", "sync")]
    [InlineData(false, "capture", "prefix")]
    [InlineData(false, "encoder", "prefix")]
    [InlineData(false, "capture", "tail")]
    [InlineData(false, "encoder", "tail")]
    [InlineData(true, "capture", "sync")]
    [InlineData(true, "encoder", "sync")]
    [InlineData(true, "capture", "prefix")]
    [InlineData(true, "encoder", "prefix")]
    [InlineData(true, "capture", "tail")]
    [InlineData(true, "encoder", "tail")]
    public async Task Blocking_cleanup_cannot_prevent_other_dependency_cleanup_and_original_tasks_are_fully_joined(bool rollback, string heldName, string mode)
    {
        var f = new Fixture(mode == "sync" ? "sync" : "dual");
        Cleanup held = (heldName == "capture" ? f.Capture : f.Encoder).Cleanup;
        Cleanup other = (heldName == "capture" ? f.Encoder : f.Capture).Cleanup;
        foreach (Dependency dependency in f.Resources)
        {
            if (mode == "tail") dependency.Cleanup.HoldTail();
            else dependency.Cleanup.Prefix.Hold();
        }
        if (rollback) f.Target.AfterEncoder = f.CreationCancellation.Cancel;
        try
        {
            Task<IVideoFrameProducer> creation = await f.Begin(f.Factory()).Published();
            IVideoFrameProducer? producer = rollback ? null : await Success(creation);
            Task operation;
            if (producer is null) operation = creation;
            else
            {
                // 即使 Stop 被错误改成同步清理，测试线程仍可在 finally 放闸。
                Task<Task> request = f.RunRequest(producer.StopAsync);
                await Success(Task.WhenAny(held.Prefix.Entered.Task, other.Prefix.Entered.Task));
                Cleanup first = held.Prefix.Entered.Task.IsCompleted ? held : other;
                Assert.False(first.Prefix.OnOwnerStack, "依赖清理不能占住创建或停止协调状态机的同步栈。");
                Assert.NotSame(request, first.Prefix.ExecutingTask);
                operation = await Success(request);
                Assert.Same(producer.Completion, operation);
            }
            Task firstEntered = await Success(Task.WhenAny(held.Prefix.Entered.Task, other.Prefix.Entered.Task));
            Cleanup entered = ReferenceEquals(firstEntered, held.Prefix.Entered.Task) ? held : other;
            Assert.False(entered.Prefix.OnOwnerStack, "同步前缀必须独立调度，不能串行阻塞另一个依赖。");
            Task anchor = mode == "tail" ? entered.Original.Task : Assert.IsAssignableFrom<Task>(entered.Prefix.ExecutingTask);
            Task disposing = await FindMachine(anchor, operation, "DisposeDependenciesAsync");
            f.Track(disposing);
            Task awaited = await RegisteredAwait(disposing);
            Task join = StateName(awaited).Contains("<ObserveAsync>", StringComparison.Ordinal)
                ? await RegisteredAwait(awaited) : awaited;
            Assert.StartsWith("WhenAllPromise", join.GetType().Name);
            Assert.Equal(2, Assert.IsType<int>(RequiredField(join.GetType(), "_remainingToComplete").GetValue(join)));
            await Success(Task.WhenAll(held.Prefix.Entered.Task, other.Prefix.Entered.Task));
            Assert.NotSame(held.Prefix.ExecutingTask, other.Prefix.ExecutingTask);
            foreach (Cleanup cleanup in new[] { held, other })
            {
                Task input = mode == "tail"
                    ? await CleanupWorker(cleanup, f) : Assert.IsAssignableFrom<Task>(cleanup.Prefix.ExecutingTask);
                await AssertChainToJoin(input, join);
                if (mode == "tail")
                {
                    // 先观察任意实际 await，再比较身份；WaitAsync 代理也会就绪并立即判错。
                    Task actual = await RegisteredAwait(input);
                    Assert.Same(cleanup.Original.Task, actual);
                }
            }
            other.Release();
            await f.JoinInvocation(other.Prefix.ExecutingTask!);
            Task stillAwaited = await RegisteredAwait(disposing);
            Assert.Same(awaited, stillAwaited);
            if (mode == "tail")
            {
                Task worker = await CleanupWorker(held, f);
                Assert.Same(held.Original.Task, await RegisteredAwait(worker));
                await AssertChainToJoin(worker, join);
            }
            else await AssertChainToJoin(held.Prefix.ExecutingTask!, join);
            held.Release();
            if (rollback)
            {
                var canceled = Assert.IsAssignableFrom<OperationCanceledException>(await Error(operation));
                Assert.Equal(f.CreationCancellation.Token, canceled.CancellationToken);
            }
            else await Success(operation);
            await f.JoinInvocation(held.Prefix.ExecutingTask!);
            await Observe(held.Original.Task);
            await Observe(other.Original.Task);
            f.AssertDisposed(mode == "sync" ? 0 : 1, mode == "sync" ? 1 : 0);
        }
        finally { held.Release(); other.Release(); await f.Finish(); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Unique_primary_id_not_enumeration_position_reaches_pipeline_and_creation_token_is_no_longer_owned(int primaryIndex)
    {
        var f = new Fixture();
        DisplayInfo[] displays = new[] { 902, 41, 703 }.Select((id, index) => Display(id, index == primaryIndex)).ToArray();
        f.Capture.Displays = () => displays;
        try
        {
            IVideoFrameProducer producer = await f.Create(f.Factory());
            Assert.Equal(0, f.Capture.CaptureCount);
            Assert.Equal(0, f.Encoder.EncodeCount);
            f.CreationCancellation.Cancel();
            producer.Start();
            Assert.Throws<InvalidOperationException>(producer.Start);
            FrameCall<CapturedFrame> capture = f.Capture.Captures[0];
            await Success(capture.Entered.Task);
            Assert.Equal(displays[primaryIndex].Id, f.Capture.LastDisplay);
            Assert.False(capture.Token.IsCancellationRequested);
            CapturedFrame raw = capture.Reply();
            FrameCall<EncodedFrame> encode = f.Encoder.Encodes[0];
            await Success(encode.Entered.Task);
            Assert.Same(raw, f.Encoder.LastInput);
            Assert.Equal(displays[primaryIndex].Id, f.Encoder.LastInput!.DisplayId);
            EncodedFrame expected = encode.Reply();
            using (EncodedFrame? actual = await Success(f.Read(producer))) Assert.Same(expected, actual);
            Task completion = producer.StopAsync();
            f.ReleaseFrames();
            await Success(completion);
            Assert.Null(await Success(f.Read(producer)));
            f.AssertDisposed(1, 0);
            f.AssertOwnersReleased();
            Assert.True(f.Clock.TimestampCalls > 0);
            f.AssertBorrowed();
        }
        finally { await f.Finish(); }
    }

    [Fact]
    public async Task Two_sessions_from_one_factory_have_independent_producers_and_dedicated_dependencies()
    {
        var f = new Fixture();
        Dependency secondCapture = f.NewDependency("dual");
        Dependency secondEncoder = f.NewDependency("dual");
        int captures = 0;
        int encoders = 0;
        f.Target.CaptureResult = () => Interlocked.Increment(ref captures) == 1 ? f.Capture : secondCapture;
        f.Target.EncoderResult = () => Interlocked.Increment(ref encoders) == 1 ? f.Encoder : secondEncoder;
        try
        {
            IVideoFrameProducerFactory factory = f.Factory();
            IVideoFrameProducer first = await f.Create(factory);
            IVideoFrameProducer second = await f.Create(factory);
            Assert.NotSame(first, second);
            Assert.NotSame(first.Completion, second.Completion);
            Assert.NotSame(f.Capture, secondCapture);
            Assert.NotSame(f.Encoder, secondEncoder);
            await Success(first.StopAsync());
            Assert.Equal(1, f.Capture.Cleanup.AsyncCalls);
            Assert.Equal(1, f.Encoder.Cleanup.AsyncCalls);
            Assert.Equal(0, secondCapture.Cleanup.TotalCalls);
            Assert.Equal(0, secondEncoder.Cleanup.TotalCalls);
            // 第一会话已完全停止后，第二会话仍实际完成一轮生产，而非只看 Completion 快照。
            second.Start();
            await Success(secondCapture.Captures[0].Entered.Task);
            secondCapture.Captures[0].Reply();
            await Success(secondEncoder.Encodes[0].Entered.Task);
            EncodedFrame expected = secondEncoder.Encodes[0].Reply();
            using (EncodedFrame? actual = await Success(f.Read(second))) Assert.Same(expected, actual);
            Task stop = second.StopAsync();
            f.ReleaseFrames();
            await Success(stop);
            Assert.Null(await Success(f.Read(second)));
            Assert.All(f.Resources, dependency => Assert.Equal(1, dependency.Cleanup.AsyncCalls));
            Assert.All(f.Resources, dependency => Assert.Equal(0, dependency.Cleanup.SyncCalls));
            Assert.Equal(2, captures);
            Assert.Equal(2, encoders);
            f.AssertOwnersReleased();
            f.AssertBorrowed();
        }
        finally { await f.Finish(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Producer_EOF_waits_for_all_dependency_cleanup_and_reports_the_same_complete_failure(bool fail)
    {
        var f = new Fixture();
        f.Capture.Cleanup.HoldTail();
        f.Encoder.Cleanup.HoldTail();
        var cleanupError = new IOException("停止阶段的原清理错误。");
        try
        {
            IVideoFrameProducer producer = await f.Create(f.Factory());
            producer.Start();
            await Success(f.Capture.Captures[0].Entered.Task);
            Task completion = producer.StopAsync();
            f.ReleaseFrames();
            await Success(Task.WhenAll(f.Capture.Cleanup.Prefix.Entered.Task, f.Encoder.Cleanup.Prefix.Entered.Task));
            Task<EncodedFrame?> read = f.Read(producer);
            Assert.Same(completion, await RegisteredAwait(read));
            f.CancelRead(read);
            Assert.Same(completion, await RegisteredAwait(read));
            Task<EncodedFrame?> secondRead = f.Read(producer);
            Assert.Same(completion, await RegisteredAwait(secondRead));
            foreach (Dependency dependency in f.Resources)
            {
                Task worker = await CleanupWorker(dependency.Cleanup, f);
                Assert.Same(dependency.Cleanup.Original.Task, await RegisteredAwait(worker));
            }
            if (fail) f.Encoder.Cleanup.FailTask([cleanupError]);
            f.Capture.Cleanup.Release();
            await f.JoinInvocation(f.Capture.Cleanup.Prefix.ExecutingTask!);
            if (!fail) Assert.Same(completion, await RegisteredAwait(read));
            f.Encoder.Cleanup.Release();
            if (fail)
            {
                var failure = Assert.IsType<AggregateException>(await Error(completion));
                Children(Assert.IsType<AggregateException>(Assert.Single(failure.InnerExceptions)), [cleanupError]);
                Assert.Same(failure, await Error(read));
                Assert.Same(failure, await Error(secondRead));
                Assert.Same(failure, await Error(f.Read(producer)));
            }
            else
            {
                await Success(completion);
                Assert.Null(await Success(read));
                Assert.Null(await Success(secondRead));
            }
            Assert.Same(completion, producer.DisposeAsync().AsTask());
            f.AssertDisposed(1, 0);
            f.AssertOwnersReleased();
        }
        finally { await f.Finish(); }
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("encoder")]
    public async Task Production_failure_automatically_releases_dependencies_and_reader_keeps_all_original_error_trees(string stage)
    {
        var f = new Fixture();
        Exception[] productionRoots = Roots("multiple");
        Exception[] captureCleanup = Roots("multiple");
        Exception[] encoderCleanup = Roots("multiple");
        f.Capture.Cleanup.FailTask(captureCleanup);
        f.Encoder.Cleanup.FailTask(encoderCleanup);
        try
        {
            IVideoFrameProducer producer = await f.Create(f.Factory());
            producer.Start();
            await Success(f.Capture.Captures[0].Entered.Task);
            if (stage == "encoder")
            {
                f.Capture.Captures[0].Reply();
                await Success(f.Encoder.Encodes[0].Entered.Task);
            }
            Task<EncodedFrame?> read = f.Read(producer);
            if (stage == "capture") f.Capture.Captures[0].Fail(productionRoots);
            else f.Encoder.Encodes[0].Fail(productionRoots);
            await Success(stage == "capture" ? f.Capture.Captures[0].CancellationSeen.Task : f.Encoder.Encodes[0].CancellationSeen.Task);
            f.ReleaseFrames();
            // 不调用 Stop/Dispose 触发清理，生产故障本身必须使完整 Completion 收尾。
            var failure = Assert.IsType<AggregateException>(await Error(producer.Completion));
            Assert.Equal(3, failure.InnerExceptions.Count);
            FramePipeline pipeline = Assert.IsType<FramePipeline>(RequiredField(producer.GetType(), "_pipeline").GetValue(producer));
            Assert.Same(Assert.Single(pipeline.Completion.Exception!.InnerExceptions), failure.InnerExceptions[0]);
            Assert.Contains(ErrorTree(failure.InnerExceptions[0]).OfType<AggregateException>(), node => SameChildren(node, productionRoots));
            Assert.Contains(failure.InnerExceptions.OfType<AggregateException>(), node => SameChildren(node, captureCleanup));
            Assert.Contains(failure.InnerExceptions.OfType<AggregateException>(), node => SameChildren(node, encoderCleanup));
            Assert.Same(failure, await Error(read));
            Assert.Same(failure, await Error(f.Read(producer)));
            f.AssertDisposed(1, 0);
            f.AssertOwnersReleased();
        }
        finally { await f.Finish(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Synchronous_dispose_failures_preserve_both_original_instances_and_do_not_skip_the_other_dependency(bool rollback)
    {
        var f = new Fixture("sync");
        var captureFailure = new OperationCanceledException("同步 Dispose 的原 OCE。");
        var encoderFailure = new AggregateException("同步 Dispose 的原聚合节点。", new IOException("内叶。"));
        f.Capture.Cleanup.SetFailure("sync-oce", [captureFailure], default);
        f.Encoder.Cleanup.SetFailure("sync-oce", [encoderFailure], default);
        if (rollback) f.Target.AfterEncoder = f.CreationCancellation.Cancel;
        try
        {
            Task<IVideoFrameProducer> creation = await f.Begin(f.Factory()).Published();
            Task operation = rollback ? creation : (await Success(creation)).StopAsync();
            var failure = Assert.IsType<AggregateException>(await Error(operation));
            Assert.Equal(rollback ? 3 : 2, failure.InnerExceptions.Count);
            var branches = failure.InnerExceptions.Skip(rollback ? 1 : 0).Select(Assert.IsType<AggregateException>).ToArray();
            Assert.Contains(branches, branch => SameChildren(branch, [captureFailure]));
            Assert.Contains(branches, branch => SameChildren(branch, [encoderFailure]));
            Assert.True(operation.IsFaulted);
            f.AssertDisposed(0, 1);
        }
        finally { await f.Finish(); }
    }

    [Theory]
    [InlineData("completed-publication")]
    [InlineData("proxy-await")]
    public async Task Await_probes_reject_broken_identity_without_using_the_watchdog_as_a_kill(string shape)
    {
        var original = Signal();
        using var cancellation = new CancellationTokenSource();
        Task? worker = null;
        try
        {
            if (shape == "completed-publication")
            {
                Exception failure = await Error(AssertForwarded(original.Task, Task.CompletedTask));
                Assert.IsAssignableFrom<XunitException>(failure);
                Assert.DoesNotContain("Watchdog", failure.Message);
            }
            else
            {
                Task proxy = original.Task.WaitAsync(cancellation.Token);
                worker = AwaitProbe(proxy);
                Task actual = await RegisteredAwait(worker);
                Assert.Same(proxy, actual);
                Exception? failure = Record.Exception(() => Assert.Same(original.Task, actual));
                Assert.IsAssignableFrom<XunitException>(failure);
                Assert.DoesNotContain("Watchdog", failure!.Message);
            }
        }
        finally
        {
            original.TrySetResult();
            await original.Task;
            if (worker is not null) await worker;
        }
    }

    private static async Task AwaitProbe(Task operation) => await operation.ConfigureAwait(false);
    private static DisplayInfo Display(int id, bool primary) => new(new DisplayId(id), "内存显示器", -100, -50, 1, 1, primary);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Exception[] Roots(string shape)
    {
        var leaf = new IOException("原清理叶。");
        var inner = new AggregateException("原内层节点。", leaf);
        var branch = new AggregateException("原外层节点。", inner, new InvalidOperationException("兄弟叶。"));
        return shape switch
        {
            "nested" => [branch],
            "multiple" => [branch, leaf, branch],
            "faulted-oce" or "sync-oce" => [new OperationCanceledException("清理 OCE 不是成功取消。")],
            _ => [leaf]
        };
    }

    private static IEnumerable<Exception> ErrorTree(Exception error)
    {
        yield return error;
        if (error is AggregateException aggregate)
            foreach (Exception child in aggregate.InnerExceptions)
                foreach (Exception descendant in ErrorTree(child)) yield return descendant;
    }

    private static bool SameChildren(AggregateException error, IReadOnlyList<Exception> expected) =>
        error.InnerExceptions.Count == expected.Count && error.InnerExceptions.Select((child, i) => ReferenceEquals(child, expected[i])).All(same => same);
    private static void Children(AggregateException error, IReadOnlyList<Exception> expected)
    {
        Assert.Equal(expected.Count, error.InnerExceptions.Count);
        for (int i = 0; i < expected.Count; i++) Assert.Same(expected[i], error.InnerExceptions[i]);
    }

    // Watchdog 仅诊断测试基础设施失去进展；业务异常只读取原任务，finally 不用超时代理代替 join。
    private static async Task Finished(Task original)
    {
        using var cancellation = new CancellationTokenSource();
        Task watchdog = Task.Delay(Watchdog, cancellation.Token);
        try { Assert.Same(original, await Task.WhenAny(original, watchdog)); }
        finally { cancellation.Cancel(); }
    }
    private static async Task Success(Task task) { await Finished(task); await task; }
    private static async Task<T> Success<T>(Task<T> task) { await Finished(task); return await task; }
    private static async Task<Exception> Error(Task task)
    {
        await Finished(task);
        Exception? error = await Record.ExceptionAsync(() => task);
        Assert.NotNull(error);
        return error;
    }
    private static async Task Observe(Task task)
    {
        await Finished(task);
        _ = await Record.ExceptionAsync(() => task);
        _ = task.Exception;
    }
    private static async Task Join(Task task)
    {
        _ = await Record.ExceptionAsync(() => task);
        _ = task.Exception;
    }

    private static FieldInfo? RuntimeField(Type type, string name)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
            if (current.GetField(name, Fields | BindingFlags.DeclaredOnly) is { } field) return field;
        return null;
    }
    private static FieldInfo RequiredField(Type type, string name) => RuntimeField(type, name)
        ?? throw new XunitException($"运行时缺少 {type.Name}.{name}，必须适配真实任务探针，不能跳过断言。");
    private static object? Machine(Task task) => RuntimeField(task.GetType(), "StateMachine")?.GetValue(task);
    private static string StateName(Task task) => Machine(task)?.GetType().Name ?? string.Empty;
    private static object? Result(Task task) => task.IsCompletedSuccessfully ? task.GetType().GetProperty("Result")?.GetValue(task) : null;
    private static object[] Continuations(Task task)
    {
        object? value = RequiredField(typeof(Task), "m_continuationObject").GetValue(task);
        if (value is IList list) { lock (list) return list.Cast<object?>().OfType<object>().ToArray(); }
        return value is null ? [] : [value];
    }
    private static object Target(object continuation)
    {
        if (continuation is Delegate action) return action.Target ?? continuation;
        if (RuntimeField(continuation.GetType(), "m_action")?.GetValue(continuation) is Delegate scheduled)
            return scheduled.Target ?? continuation;
        return continuation;
    }
    private static Task[] Next(Task task) => Continuations(task).Select(Target).OfType<Task>().ToArray();
    private static bool Registered(Task source, Task target) => Next(source).Any(item => ReferenceEquals(item, target));
    private static IEnumerable<Task> StoredTasks(object value, int depth = 0)
    {
        if (depth > 5) yield break;
        foreach (FieldInfo field in value.GetType().GetFields(Fields))
        {
            object? child = field.GetValue(value);
            if (child is Task task) yield return task;
            else if (child is not null && field.FieldType.IsValueType && !field.FieldType.IsPrimitive && !field.FieldType.IsEnum)
                foreach (Task nested in StoredTasks(child, depth + 1)) yield return nested;
        }
    }
    private static Task? FindAwait(Task owner)
    {
        object? state = Machine(owner);
        if (state is null) return null;
        Type type = state.GetType();
        object builder = RequiredField(type, "<>t__builder").GetValue(state)!;
        Assert.Contains(StoredTasks(builder), item => ReferenceEquals(owner, item));
        if (Assert.IsType<int>(RequiredField(type, "<>1__state").GetValue(state)) < 0) return null;
        foreach (FieldInfo field in type.GetFields(Fields).Where(field => field.Name.StartsWith("<>u__", StringComparison.Ordinal)))
            if (field.GetValue(state) is { } awaiter)
                foreach (Task awaited in StoredTasks(awaiter))
                    if (!awaited.IsCompleted && Registered(awaited, owner)) return awaited;
        return null;
    }
    private static async Task<Task> RegisteredAwait(Task owner)
    {
        using var cancellation = new CancellationTokenSource(Watchdog);
        while (true)
        {
            Assert.False(owner.IsCompleted, "原状态机提前完成，不能用残留 await 冒充完整 join。");
            Task? actual = FindAwait(owner);
            if (actual is not null)
            {
                Assert.False(owner.IsCompleted, "观察 await 时原状态机提前退出。");
                return actual;
            }
            Assert.False(cancellation.IsCancellationRequested, "Watchdog：运行时没有发布可观察的真实 await。");
            await Task.Yield();
        }
    }
    private static async Task AssertForwarded(Task original, Task published)
    {
        using var cancellation = new CancellationTokenSource(Watchdog);
        while (true)
        {
            Assert.False(published.IsCompleted, "发布任务提前完成，原调用尚未退出。");
            Assert.False(original.IsCompleted, "受控原调用不应提前退出。");
            if (Continuations(original).Length != 0) break;
            Assert.False(cancellation.IsCancellationRequested, "Watchdog：原调用没有发布 continuation。");
            await Task.Yield();
        }
        Assert.True(Registered(original, published), "公开创建任务必须承接真实原调用，而不是取消代理。");
    }
    private static async Task<Task> FindMachine(Task anchor, Task published, string method)
    {
        using var cancellation = new CancellationTokenSource(Watchdog);
        while (true)
        {
            Assert.False(published.IsCompleted, "公开任务提前完成，清理的原操作仍未退出。");
            var pending = new Queue<Task>();
            var seen = new HashSet<Task>(ReferenceEqualityComparer.Instance);
            pending.Enqueue(anchor);
            while (pending.TryDequeue(out Task? current))
            {
                if (!seen.Add(current)) continue;
                if (StateName(current).Contains("<" + method + ">", StringComparison.Ordinal))
                {
                    _ = await RegisteredAwait(current);
                    return current;
                }
                foreach (Task next in Next(current)) pending.Enqueue(next);
            }
            Assert.False(anchor.IsCompleted, "发现任务链所用的受控原操作已提前退出。");
            Assert.False(cancellation.IsCancellationRequested, "Watchdog：无法发现真实清理协调状态机。");
            await Task.Yield();
        }
    }
    private static async Task AssertChainToJoin(Task original, Task join)
    {
        using var cancellation = new CancellationTokenSource(Watchdog);
        Task current = original;
        var seen = new HashSet<Task>(ReferenceEqualityComparer.Instance);
        while (!ReferenceEquals(current, join))
        {
            Assert.True(seen.Add(current), "清理任务的 join 链不应形成循环。");
            while (Continuations(current).Length == 0)
            {
                Assert.False(current.IsCompleted, "原清理在建立 join 链前提前退出。");
                Assert.False(cancellation.IsCancellationRequested, "Watchdog：清理任务未登记 join。");
                await Task.Yield();
            }
            Task[] next = Next(current);
            _ = Assert.Single(next);
            current = next[0];
            if (!ReferenceEquals(current, join)) Assert.Contains("UnwrapPromise", current.GetType().Name);
        }
    }
    private static async Task<Task> CleanupWorker(Cleanup cleanup, Fixture f)
    {
        Task invocation = Assert.IsAssignableFrom<Task>(cleanup.Prefix.ExecutingTask);
        await Success(invocation);
        Task worker = Assert.IsAssignableFrom<Task>(Result(invocation));
        f.Track(worker);
        Assert.NotNull(Machine(worker));
        return worker;
    }

    private sealed class Fixture
    {
        private readonly List<Gate> _gates = [];
        private readonly List<Request> _requests = [];
        private readonly List<Task> _tracked = [];
        private readonly List<(Task<EncodedFrame?> Task, CancellationTokenSource Cancellation)> _reads = [];
        private readonly ConcurrentBag<Owner> _owners = [];
        private readonly HashSet<IVideoFrameProducer> _producers = new(ReferenceEqualityComparer.Instance);
        internal List<Dependency> Resources { get; } = [];
        internal CancellationTokenSource CreationCancellation { get; } = new();
        internal CancellationTokenSource ReadCancellation { get; } = new();
        internal BorrowedClock Clock { get; } = new();
        internal BorrowedTarget Target { get; }
        internal Dependency Capture { get; }
        internal Dependency Encoder { get; }

        internal Fixture(string kind = "dual", bool shared = false)
        {
            Capture = NewDependency(kind);
            Encoder = shared ? Capture : NewDependency(kind);
            Target = new BorrowedTarget(this, Capture, Encoder);
        }
        internal Gate NewGate() { var gate = new Gate(); _gates.Add(gate); return gate; }
        internal Dependency NewDependency(string kind)
        {
            Dependency dependency = kind switch
            {
                "sync" => new SyncDependency(this),
                "async" => new AsyncDependency(this),
                "dual" => new DualDependency(this),
                _ => new Dependency(this)
            };
            Resources.Add(dependency);
            return dependency;
        }
        internal IVideoFrameProducerFactory Factory(int raw = 2, int encoded = 2) =>
            new FramePipelineVideoProducerFactory(Target.CreateCapture, Target.CreateEncoder, raw, encoded, Clock);
        internal Gate Stage(string stage) => stage switch
        {
            "capture" => Target.CaptureGate,
            "displays" => Capture.DisplayGate,
            _ => Target.EncoderGate
        };
        internal void FailStage(string stage, Exception error)
        {
            switch (stage)
            {
                case "capture": Target.CaptureResult = () => throw error; break;
                case "displays": Capture.Displays = () => throw error; break;
                case "encoder": Target.EncoderResult = () => throw error; break;
            }
        }
        internal Request Begin(IVideoFrameProducerFactory factory)
        {
            var request = new Request(factory, CreationCancellation.Token);
            _requests.Add(request);
            return request;
        }
        internal async Task<IVideoFrameProducer> Create(IVideoFrameProducerFactory factory)
        {
            IVideoFrameProducer producer = await Success(await Begin(factory).Published());
            _producers.Add(producer);
            return producer;
        }
        internal Task<Task> RunRequest(Func<Task> action)
        {
            Task<Task> task = Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Track(task);
            return task;
        }
        internal void Track(Task task) => _tracked.Add(task);
        internal Owner NewOwner() { var owner = new Owner(); _owners.Add(owner); return owner; }
        internal Task<EncodedFrame?> Read(IVideoFrameProducer producer)
        {
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ReadCancellation.Token);
            try
            {
                Task<EncodedFrame?> task = producer.ReadNextAsync(cancellation.Token).AsTask();
                _reads.Add((task, cancellation));
                return task;
            }
            catch { cancellation.Dispose(); throw; }
        }
        internal void CancelRead(Task<EncodedFrame?> task) =>
            _reads.Single(read => ReferenceEquals(read.Task, task)).Cancellation.Cancel();
        internal void AssertCalls(int capture, int displays, int encoder)
        {
            Assert.Equal(capture, Target.CaptureGate.Count);
            Assert.Equal(displays, Capture.DisplayGate.Count);
            Assert.Equal(encoder, Target.EncoderGate.Count);
        }
        internal void AssertDisposed(int asyncCalls, int syncCalls)
        {
            Assert.All(Resources, dependency => Assert.Equal(asyncCalls, dependency.Cleanup.AsyncCalls));
            Assert.All(Resources, dependency => Assert.Equal(syncCalls, dependency.Cleanup.SyncCalls));
        }
        internal void AssertBorrowed()
        {
            Assert.Equal(0, Target.DisposeCalls);
            Assert.Equal(0, Clock.DisposeCalls);
        }
        internal void AssertOwnersReleased() => Assert.All(_owners, owner => Assert.Equal(1, owner.DisposeCount));
        internal void ReleaseFrames()
        {
            foreach (Dependency resource in Resources)
            {
                foreach (FrameCall<CapturedFrame> call in resource.Captures) call.Release();
                foreach (FrameCall<EncodedFrame> call in resource.Encodes) call.Release();
            }
        }
        internal async Task JoinInvocation(Task invocation)
        {
            await Join(invocation);
            if (Result(invocation) is Task child) await JoinInvocation(child);
        }
        private async Task Collect(Task task, HashSet<Task> seen)
        {
            if (!seen.Add(task)) return;
            await Join(task);
            if (Result(task) is Task child) await Collect(child, seen);
            else if (Result(task) is IVideoFrameProducer producer) _producers.Add(producer);
        }
        internal async Task Finish()
        {
            // 先放所有闸并取消每个测试读取；不依赖被测 Completion 正确实现来释放 fixture。
            foreach (Gate gate in _gates) gate.Open();
            foreach (Dependency resource in Resources) resource.Cleanup.Release();
            ReleaseFrames();
            Task cancelReads = ReadCancellation.CancelAsync();
            Task cancelCreation = CreationCancellation.CancelAsync();
            try
            {
                var seen = new HashSet<Task>(ReferenceEqualityComparer.Instance);
                foreach (Request request in _requests) await Collect(request.Outer, seen);
                // 原同步调用的真实执行 Task 也必须独立 join，以覆盖公开任务提前返回的错误实现。
                foreach (Gate gate in _gates)
                    foreach (Task invocation in gate.Invocations) await Collect(invocation, seen);
                foreach (IVideoFrameProducer producer in _producers)
                {
                    Task stop = producer.StopAsync();
                    FramePipeline pipeline = Assert.IsType<FramePipeline>(RequiredField(producer.GetType(), "_pipeline").GetValue(producer));
                    // 仅用于最终清场；测试断言始终通过公开工厂和 IVideoFrameProducer 发起。
                    await Join(pipeline.StopAsync());
                    await Join(stop);
                    await Join(producer.Completion);
                    await Join(Assert.IsAssignableFrom<Task>(RequiredField(typeof(FramePipeline), "_captureLoop").GetValue(pipeline)));
                    await Join(Assert.IsAssignableFrom<Task>(RequiredField(typeof(FramePipeline), "_encodeLoop").GetValue(pipeline)));
                }
                foreach (Task task in _tracked) await Collect(task, seen);
                foreach (var read in _reads)
                {
                    await Join(read.Task);
                    if (read.Task.IsCompletedSuccessfully) (await read.Task)?.Dispose();
                }
            }
            finally
            {
                // 清理阶段新进入的 disposer/有限帧调用也要真实 await，不能只看进入信号的瞬时状态。
                try
                {
                    foreach (Gate gate in _gates)
                        foreach (Task invocation in gate.Invocations) await JoinInvocation(invocation);
                    foreach (Dependency resource in Resources)
                    {
                        await Join(resource.Cleanup.Original.Task);
                        foreach (FrameCall<CapturedFrame> call in resource.Captures) await call.JoinIfInvoked();
                        foreach (FrameCall<EncodedFrame> call in resource.Encodes) await call.JoinIfInvoked();
                    }
                    await Join(cancelReads);
                    await Join(cancelCreation);
                }
                finally
                {
                    foreach (var read in _reads) read.Cancellation.Dispose();
                    foreach (Dependency resource in Resources) resource.Unregister();
                    // 断言失败时回收尚未移交的测试 owner；正常路径已在此之前断言精确释放次数。
                    foreach (Owner owner in _owners) if (owner.DisposeCount == 0) owner.Dispose();
                    ReadCancellation.Dispose();
                    CreationCancellation.Dispose();
                }
            }
        }
    }

    private sealed class Request
    {
        internal int CallerThread;
        internal Task<Task<IVideoFrameProducer>> Outer { get; }
        internal Request(IVideoFrameProducerFactory factory, CancellationToken token)
        {
            Outer = Task.Factory.StartNew(() =>
            {
                CallerThread = Environment.CurrentManagedThreadId;
                return factory.CreateAsync(Guid.NewGuid(), token).AsTask();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        internal Task<Task<IVideoFrameProducer>> Published() => Success(Outer);
    }

    private sealed class BorrowedTarget : IDisposable, IAsyncDisposable
    {
        internal Gate CaptureGate { get; }
        internal Gate EncoderGate { get; }
        internal Func<IScreenCaptureBackend> CaptureResult;
        internal Func<IFrameEncoder> EncoderResult;
        internal Action? AfterEncoder;
        internal int DisposeCalls;
        internal BorrowedTarget(Fixture fixture, Dependency capture, Dependency encoder)
        {
            CaptureGate = fixture.NewGate();
            EncoderGate = fixture.NewGate();
            CaptureResult = () => capture;
            EncoderResult = () => encoder;
        }
        internal IScreenCaptureBackend CreateCapture() => CaptureGate.Invoke(CaptureResult);
        internal IFrameEncoder CreateEncoder() => EncoderGate.Invoke(() =>
        {
            IFrameEncoder result = EncoderResult();
            AfterEncoder?.Invoke();
            return result;
        });
        public void Dispose() => Interlocked.Increment(ref DisposeCalls);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private class Dependency : IScreenCaptureBackend, IFrameEncoder
    {
        private readonly Fixture _fixture;
        private int _captures;
        private int _encodes;
        internal Cleanup Cleanup { get; }
        internal Gate DisplayGate { get; }
        internal Func<IReadOnlyList<DisplayInfo>> Displays = () => [Display(91, false), Display(73, true)];
        internal FrameCall<CapturedFrame>[] Captures { get; }
        internal FrameCall<EncodedFrame>[] Encodes { get; }
        internal DisplayId LastDisplay;
        internal CapturedFrame? LastInput;
        internal int CaptureCount => Volatile.Read(ref _captures);
        internal int EncodeCount => Volatile.Read(ref _encodes);
        internal Dependency(Fixture fixture)
        {
            _fixture = fixture;
            Cleanup = new Cleanup(fixture.NewGate());
            DisplayGate = fixture.NewGate();
            Captures = Enumerable.Range(0, 2).Select(index => new FrameCall<CapturedFrame>(() =>
                new CapturedFrame(LastDisplay, 1, 1, 4, FramePixelFormat.Bgra32, _fixture.NewOwner(), index + 1))).ToArray();
            Encodes = Enumerable.Range(0, 2).Select(index => new FrameCall<EncodedFrame>(() =>
                new EncodedFrame(VideoCodec.Jpeg, 1, 1, (ulong)index + 1, LastInput!.TimestampUs, 60, _fixture.NewOwner()))).ToArray();
        }
        public IReadOnlyList<DisplayInfo> GetDisplays() => DisplayGate.Invoke(Displays);
        public ValueTask<CapturedFrame> CaptureAsync(DisplayId displayId, CancellationToken cancellationToken)
        {
            LastDisplay = displayId;
            int index = Interlocked.Increment(ref _captures) - 1;
            if (index >= Captures.Length) throw new XunitException("有限 fake 不允许第三次采集。");
            return Captures[index].Invoke(cancellationToken);
        }
        public ValueTask<EncodedFrame> EncodeAsync(CapturedFrame frame, VideoQualitySettings settings, CancellationToken cancellationToken)
        {
            LastInput = frame;
            _ = frame.Pixels.Length;
            int index = Interlocked.Increment(ref _encodes) - 1;
            if (index >= Encodes.Length) throw new XunitException("有限 fake 不允许第三次编码。");
            return Encodes[index].Invoke(cancellationToken);
        }
        internal void Unregister()
        {
            foreach (FrameCall<CapturedFrame> call in Captures) call.Unregister();
            foreach (FrameCall<EncodedFrame> call in Encodes) call.Unregister();
        }
        public override bool Equals(object? obj) => obj is Dependency;
        public override int GetHashCode() => 1;
    }
    private sealed class SyncDependency(Fixture fixture) : Dependency(fixture), IDisposable
    {
        public void Dispose() => Cleanup.Dispose();
    }
    private sealed class AsyncDependency(Fixture fixture) : Dependency(fixture), IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Cleanup.DisposeAsync();
    }
    private sealed class DualDependency(Fixture fixture) : Dependency(fixture), IDisposable, IAsyncDisposable
    {
        public void Dispose() => Cleanup.Dispose();
        public ValueTask DisposeAsync() => Cleanup.DisposeAsync();
    }

    private sealed class Cleanup(Gate prefix)
    {
        private int _asyncCalls;
        private int _syncCalls;
        private bool _tail;
        private Exception? _synchronousFailure;
        internal Gate Prefix { get; } = prefix;
        internal TaskCompletionSource Original { get; } = Signal();
        internal int AsyncCalls => Volatile.Read(ref _asyncCalls);
        internal int SyncCalls => Volatile.Read(ref _syncCalls);
        internal int TotalCalls => AsyncCalls + SyncCalls;
        internal void HoldTail() => _tail = true;
        internal void FailTask(Exception[] errors) => Original.TrySetException(errors);
        internal void SetFailure(string shape, Exception[] roots, CancellationToken token)
        {
            if (shape == "sync-oce") _synchronousFailure = roots[0];
            else if (shape == "canceled") Original.TrySetCanceled(token);
            else FailTask(roots);
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _asyncCalls);
            return Prefix.Invoke(() =>
            {
                if (_synchronousFailure is { } error) throw error;
                if (!_tail) Original.TrySetResult();
                return new ValueTask(Original.Task);
            });
        }
        public void Dispose()
        {
            Interlocked.Increment(ref _syncCalls);
            Prefix.Invoke(() =>
            {
                if (_synchronousFailure is { } error) throw error;
                return 0;
            });
        }
        internal void Release() { Prefix.Open(); Original.TrySetResult(); }
    }

    private sealed class Gate
    {
        private readonly TaskCompletionSource _release = Signal();
        private int _held;
        private int _count;
        internal TaskCompletionSource Entered { get; } = Signal();
        internal ConcurrentBag<Task> Invocations { get; } = [];
        internal Task? ExecutingTask;
        internal int ThreadId;
        internal bool OnOwnerStack;
        internal int Count => Volatile.Read(ref _count);
        internal void Hold() => Volatile.Write(ref _held, 1);
        internal void Open() => _release.TrySetResult();
        internal T Invoke<T>(Func<T> action)
        {
            Task? current = typeof(Task).GetField("t_currentTask", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as Task;
            if (current is not null) Invocations.Add(current);
            ExecutingTask = current;
            ThreadId = Environment.CurrentManagedThreadId;
            OnOwnerStack = new StackTrace().GetFrames().Any(frame =>
            {
                string name = frame.GetMethod()?.DeclaringType?.Name ?? string.Empty;
                return name.Contains("<DisposeDependenciesAsync>", StringComparison.Ordinal)
                    || name.Contains("<CompleteAsync>", StringComparison.Ordinal)
                    || name.Contains("<CreateCoreAsync>", StringComparison.Ordinal);
            });
            Interlocked.Increment(ref _count);
            Entered.TrySetResult();
            // 真实同步阻塞没有 timeout；只有测试 finally 可以放闸。
            if (Volatile.Read(ref _held) != 0) _release.Task.GetAwaiter().GetResult();
            return action();
        }
    }

    private sealed class FrameCall<T>(Func<T> create) where T : class, IDisposable
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource<T> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _registration;
        private bool _invoked;
        private bool _release;
        private T? _frame;
        internal CancellationToken Token;
        internal TaskCompletionSource Entered { get; } = Signal();
        internal TaskCompletionSource CancellationSeen { get; } = Signal();
        internal ValueTask<T> Invoke(CancellationToken token)
        {
            lock (_gate)
            {
                _invoked = true;
                Token = token;
                _registration = token.Register(() => CancellationSeen.TrySetResult());
                Entered.TrySetResult();
                if (_release && !_result.Task.IsCompleted) Reply();
                return new ValueTask<T>(_result.Task);
            }
        }
        internal void Fail(Exception[] errors) { lock (_gate) _result.TrySetException(errors); }
        internal T Reply()
        {
            lock (_gate)
            {
                if (_result.Task.IsCompleted && _frame is null) throw new XunitException("失败原调用不能再产生未移交的帧。");
                _frame ??= create();
                _result.TrySetResult(_frame);
                return _frame;
            }
        }
        internal void Release()
        {
            lock (_gate)
            {
                _release = true;
                if (_invoked && !_result.Task.IsCompleted) Reply();
            }
        }
        internal async Task JoinIfInvoked()
        {
            bool invoked;
            lock (_gate) invoked = _invoked;
            if (invoked) await Join(_result.Task);
        }
        internal void Unregister() => _registration.Dispose();
    }

    private sealed class Owner : IMemoryOwner<byte>
    {
        private readonly byte[] _bytes = [1, 2, 3, 4];
        private int _disposes;
        internal int DisposeCount => Volatile.Read(ref _disposes);
        public Memory<byte> Memory
        {
            get { ObjectDisposedException.ThrowIf(DisposeCount != 0, this); return _bytes; }
        }
        public void Dispose() => Interlocked.Increment(ref _disposes);
    }

    private sealed class BorrowedClock : TimeProvider, IDisposable, IAsyncDisposable
    {
        private long _ticks;
        internal int TimestampCalls;
        internal int DisposeCalls;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp()
        {
            Interlocked.Increment(ref TimestampCalls);
            return Interlocked.Add(ref _ticks, TimeSpan.TicksPerSecond);
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            throw new XunitException("内存测试时钟总是超预算，不应创建实际节拍计时器。");
        public void Dispose() => Interlocked.Increment(ref DisposeCalls);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class DisplayList(Action action, bool yieldPrimary) : IReadOnlyList<DisplayInfo>
    {
        public int Count => yieldPrimary ? 1 : 0;
        public DisplayInfo this[int index] => index == 0 && yieldPrimary ? Display(73, true) : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<DisplayInfo> GetEnumerator()
        {
            if (yieldPrimary) yield return Display(73, true);
            action();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
