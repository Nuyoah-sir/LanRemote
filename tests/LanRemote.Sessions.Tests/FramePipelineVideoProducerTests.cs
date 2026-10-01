using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using Xunit.Sdk;

namespace LanRemote.Sessions.Tests;

public sealed class FramePipelineVideoProducerTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(30);
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Start_stop_and_dispose_share_stable_completion_and_stop_before_start_is_permanent(bool start)
    {
        var f = new Fixture();
        try
        {
            IVideoFrameProducer producer = await f.Create();
            Task completion = producer.Completion;
            Assert.Same(completion, producer.Completion);
            Assert.False(completion.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => { _ = producer.ReadNextAsync(); });
            if (start)
            {
                producer.Start();
                Assert.Throws<InvalidOperationException>(producer.Start);
                await Success(f.Captures.At(0));
            }
            Task stop = producer.StopAsync();
            Assert.Same(completion, stop);
            Assert.Same(completion, producer.StopAsync());
            Assert.Same(completion, producer.DisposeAsync().AsTask());
            Assert.Throws<InvalidOperationException>(producer.Start);
            f.ReleaseCalls();
            await Success(stop);
            Assert.Equal(start ? 1 : 0, f.Captures.Count);
            Assert.Equal(0, f.Encodes.Count);
            if (start) Assert.Null(await Success(f.Read()));
            Assert.Equal(1, f.Capture.Cleanup.AsyncCalls);
            Assert.Equal(1, f.Encoder.Cleanup.AsyncCalls);
            Assert.Equal(0, f.Capture.Cleanup.SyncCalls + f.Encoder.Cleanup.SyncCalls);
            Assert.Same(completion, producer.Completion);
            f.AssertOwnersReleased();
        }
        finally { await f.Finish(); }
    }

    [Fact]
    public async Task Stop_before_Start_records_only_the_original_rejection_as_expected()
    {
        var f = new Fixture();
        try
        {
            IVideoFrameProducer producer = await f.Create();
            IVideoFrameProducerStartRejection evidence = Assert.IsAssignableFrom<IVideoFrameProducerStartRejection>(producer);
            Task completion = producer.Completion;
            Assert.Same(completion, producer.StopAsync());
            f.ReleaseCalls();
            await Success(completion);

            InvalidOperationException rejection = Assert.Throws<InvalidOperationException>(producer.Start);
            Assert.True(evidence.IsStopBeforeStartRejection(rejection));
            Assert.False(evidence.IsStopBeforeStartRejection(new InvalidOperationException(rejection.Message)));
            Assert.Equal(0, f.Captures.Count);
            Assert.Equal(1, f.Capture.Cleanup.AsyncCalls);
            Assert.Equal(1, f.Encoder.Cleanup.AsyncCalls);
            f.AssertOwnersReleased();
        }
        finally { await f.Finish(); }
    }

    [Fact]
    public async Task Start_before_Stop_never_marks_a_later_duplicate_Start_rejection_as_expected()
    {
        var f = new Fixture();
        try
        {
            IVideoFrameProducer producer = await f.Create();
            IVideoFrameProducerStartRejection evidence = Assert.IsAssignableFrom<IVideoFrameProducerStartRejection>(producer);
            producer.Start();
            await Success(f.Captures.At(0));
            Task completion = producer.StopAsync();
            f.ReleaseCalls();
            await Success(completion);

            InvalidOperationException rejection = Assert.Throws<InvalidOperationException>(producer.Start);
            Assert.False(evidence.IsStopBeforeStartRejection(rejection));
            Assert.False(evidence.IsStopBeforeStartRejection(new InvalidOperationException(rejection.Message)));
            Assert.Equal(1, f.Captures.Count);
            f.AssertOwnersReleased();
        }
        finally { await f.Finish(); }
    }

    [Fact]
    public async Task Canceled_caller_read_does_not_stop_production_and_successful_frame_belongs_to_caller()
    {
        var f = new Fixture();
        using var caller = new CancellationTokenSource();
        try
        {
            IVideoFrameProducer producer = await f.Create();
            producer.Start();
            Call<CapturedFrame> capture = await Success(f.Captures.At(0));
            Task<EncodedFrame?> canceled = f.Read(caller.Token);
            Assert.Throws<InvalidOperationException>(() => { _ = producer.ReadNextAsync(); });
            caller.Cancel();
            Assert.IsAssignableFrom<OperationCanceledException>(await Error(canceled));
            Assert.True(canceled.IsCanceled);
            Assert.False(capture.Token.IsCancellationRequested);
            Task<EncodedFrame?> next = f.Read();
            CapturedFrame raw = capture.Reply();
            Call<EncodedFrame> encode = await Success(f.Encodes.At(0));
            Assert.Same(raw, encode.Input);
            EncodedFrame expected = encode.Reply();
            EncodedFrame owned = Assert.IsType<EncodedFrame>(await Success(next));
            Assert.Same(expected, owned);
            Assert.Equal(1, capture.Owner!.DisposeCount);
            Assert.Equal(0, encode.Owner!.DisposeCount);
            Task stop = producer.StopAsync();
            f.ReleaseCalls();
            await Success(stop);
            Assert.Equal(4, owned.Payload.Length);
            Assert.Equal(0, encode.Owner.DisposeCount);
            owned.Dispose();
            Assert.Equal(1, encode.Owner.DisposeCount);
            Assert.Null(await Success(f.Read()));
            f.AssertOwnersReleased();
        }
        finally { caller.Cancel(); await f.Finish(); }
    }

    [Theory]
    [InlineData("capture", "prefix")]
    [InlineData("capture", "tail")]
    [InlineData("encode", "prefix")]
    [InlineData("encode", "tail")]
    public async Task Stop_joins_original_capture_or_encode_through_sync_prefix_and_async_tail(string stage, string mode)
    {
        var f = new Fixture { HeldStage = stage, HoldPrefix = mode == "prefix", HoldTail = true };
        try
        {
            IVideoFrameProducer producer = await f.Create();
            producer.Start();
            Call<CapturedFrame> first = await Success(f.Captures.At(0));
            CallBase held;
            if (stage == "capture") held = first;
            else
            {
                first.Reply();
                held = await Success(f.Encodes.At(0));
            }
            await Success(held.Prefix.Entered.Task);
            if (mode == "tail")
            {
                held.ReplyDefault();
                await Success(held.Tail.Entered.Task);
                await Success(held.InvocationExited.Task);
            }
            Task<EncodedFrame?> reader = f.Read();
            Task completion = producer.StopAsync();
            await Success(held.CancellationSeen.Task);
            Task observingPipeline = await RegisteredAwait(producer.Completion);
            Assert.Contains("<ObserveAsync>", StateName(observingPipeline));
            await AssertAwaiting(observingPipeline, f.Pipeline.Completion);
            Assert.False(completion.IsCompleted);
            Assert.Equal(0, f.Capture.Cleanup.AsyncCalls + f.Encoder.Cleanup.AsyncCalls);
            Assert.Throws<InvalidOperationException>(producer.Start);
            if (stage == "encode")
            {
                Assert.Equal(0, first.Owner!.DisposeCount);
                Assert.Equal(4, held.Input!.Pixels.Length);
            }
            if (mode == "prefix")
            {
                Assert.False(held.InvocationExited.Task.IsCompleted);
                held.Prefix.Open();
                held.ReplyDefault();
                await Success(held.Tail.Entered.Task);
                await Success(held.InvocationExited.Task);
            }
            Task worker = Assert.IsAssignableFrom<Task>(held.Worker);
            Task loop = await DiscoverLoop(worker, f.PublishedLoop(stage),
                stage == "capture" ? "CaptureLoopAsync" : "EncodeLoopAsync");
            await AssertAwaiting(loop, worker);
            Assert.True(Registered(loop, f.PublishedLoop(stage)));
            Assert.True(held.Owner is { DisposeCount: 0 });
            if (stage == "encode")
            {
                Assert.Equal(0, first.Owner!.DisposeCount);
                Assert.Equal(4, held.Input!.Pixels.Length);
            }
            Assert.False(completion.IsCompleted);
            f.ReleaseCalls(held);
            held.Tail.Open();
            await Success(worker);
            await Success(completion);
            Assert.Null(await Success(reader));
            Assert.Equal(1, held.Owner!.DisposeCount);
            if (stage == "encode") Assert.Equal(1, first.Owner!.DisposeCount);
            Assert.Equal(1, f.Capture.Cleanup.AsyncCalls);
            Assert.Equal(1, f.Encoder.Cleanup.AsyncCalls);
            f.AssertOwnersReleased();
        }
        finally { await f.Finish(); }
    }

    [Theory]
    [InlineData("prefix", false)]
    [InlineData("prefix", true)]
    [InlineData("tail", false)]
    [InlineData("tail", true)]
    [InlineData("sync", false)]
    [InlineData("sync", true)]
    public async Task Both_dependency_disposals_start_independently_and_EOF_joins_every_original(string mode, bool fail)
    {
        var f = new Fixture(mode == "sync");
        if (mode != "tail")
        {
            f.Capture.Cleanup.Prefix.Hold();
            f.Encoder.Cleanup.Prefix.Hold();
        }
        else
        {
            f.Capture.Cleanup.HoldTail();
            f.Encoder.Cleanup.HoldTail();
        }
        var captureOce = new OperationCanceledException("原清理 OCE 不是正常停止。");
        var nested = new AggregateException("原聚合节点。", new IOException("原叶。"));
        try
        {
            IVideoFrameProducer producer = await f.Create();
            producer.Start();
            Call<CapturedFrame> active = await Success(f.Captures.At(0));
            Task completion = producer.StopAsync();
            f.ReleaseCalls();
            await Success(active.CancellationSeen.Task);
            Task first = await Success(Task.WhenAny(f.Capture.Cleanup.Prefix.Entered.Task,
                f.Encoder.Cleanup.Prefix.Entered.Task));
            Cleanup entered = ReferenceEquals(first, f.Capture.Cleanup.Prefix.Entered.Task)
                ? f.Capture.Cleanup : f.Encoder.Cleanup;
            Assert.False(entered.Prefix.OnAdapterStack);
            Task dispose = await RegisteredAwait(completion);
            Assert.Contains("<DisposeDependenciesAsync>", StateName(dispose));
            Task observe = await RegisteredAwait(dispose);
            Assert.Contains("<ObserveAsync>", StateName(observe));
            Task join = await RegisteredAwait(observe);
            Assert.StartsWith("WhenAllPromise", join.GetType().Name);
            Assert.Equal(2, Assert.IsType<int>(RequiredField(join.GetType(), "_remainingToComplete").GetValue(join)));
            await Success(Task.WhenAll(f.Capture.Cleanup.Prefix.Entered.Task,
                f.Encoder.Cleanup.Prefix.Entered.Task));
            Assert.NotSame(f.Capture.Cleanup.Prefix.ExecutingTask, f.Encoder.Cleanup.Prefix.ExecutingTask);
            var workers = new List<Task>();
            if (mode == "tail")
            {
                // 就绪先接受任意真正登记的 Task，再单独检验其是否为原 DisposeAsync 任务。
                foreach (Cleanup cleanup in new[] { f.Capture.Cleanup, f.Encoder.Cleanup })
                {
                    Task worker = await DiscoverCleanup(cleanup.Original.Task, join);
                    await AssertAwaiting(worker, cleanup.Original.Task);
                    workers.Add(worker);
                }
            }
            using var readCancellation = new CancellationTokenSource();
            Task<EncodedFrame?> firstRead = f.Read(readCancellation.Token);
            await AssertAwaiting(firstRead, completion);
            readCancellation.Cancel();
            await AssertAwaiting(firstRead, completion);
            Task<EncodedFrame?> secondRead = f.Read();
            await AssertAwaiting(secondRead, completion);
            Assert.Equal(mode == "sync" ? 1 : 0, f.Capture.Cleanup.SyncCalls);
            Assert.Equal(mode == "sync" ? 1 : 0, f.Encoder.Cleanup.SyncCalls);
            Assert.Equal(mode == "sync" ? 0 : 1, f.Capture.Cleanup.AsyncCalls);
            Assert.Equal(mode == "sync" ? 0 : 1, f.Encoder.Cleanup.AsyncCalls);
            if (fail)
            {
                if (mode == "sync")
                {
                    f.Capture.Cleanup.SyncError = captureOce;
                    f.Encoder.Cleanup.SyncError = nested;
                }
                else
                {
                    f.Capture.Cleanup.Fail([captureOce]);
                    if (mode == "prefix") f.Encoder.Cleanup.Fail([nested, nested]);
                }
            }
            f.Capture.Cleanup.Release();
            await Observe(f.Capture.Cleanup.Prefix.ExecutingTask!);
            await Success(f.Capture.Cleanup.Prefix.Exited.Task);
            if (mode == "tail") await Observe(workers[0]);
            Assert.False(completion.IsCompleted);
            // tail 的原 DisposeAsync 仍必须未完成；验证第二路尚在等待后才注入其多根故障。
            if (fail && mode == "tail") f.Encoder.Cleanup.Fail([nested, nested]);
            f.Encoder.Cleanup.Release();
            await Success(f.Encoder.Cleanup.Prefix.Exited.Task);
            if (mode == "tail") await Observe(workers[1]);
            if (fail)
            {
                var failure = Assert.IsType<AggregateException>(await Error(completion));
                Assert.True(completion.IsFaulted);
                AssertSubtree(failure, [captureOce]);
                Exception[] encoderRoots = mode == "sync" ? [nested] : [nested, nested];
                AssertSubtree(failure, encoderRoots);
                Assert.Same(failure, await Error(firstRead));
                Assert.Same(failure, await Error(secondRead));
                Assert.Same(failure, await Error(f.Read()));
            }
            else
            {
                await Success(completion);
                Assert.Null(await Success(firstRead));
                Assert.Null(await Success(secondRead));
            }
            Assert.Same(completion, producer.StopAsync());
            Assert.Same(completion, producer.DisposeAsync().AsTask());
            Assert.Equal(mode == "sync" ? 2 : 0, f.Capture.Cleanup.SyncCalls + f.Encoder.Cleanup.SyncCalls);
            f.AssertOwnersReleased();
        }
        finally { await f.Finish(); }
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("encode")]
    public async Task Sync_cleanup_OCE_and_other_async_multiroot_failure_both_survive(string synchronous)
    {
        var f = new Fixture();
        var oce = new OperationCanceledException("DisposeAsync 同步前缀 OCE。");
        var nested = new AggregateException("原嵌套根。", new IOException("内叶。"));
        Cleanup sync = synchronous == "capture" ? f.Capture.Cleanup : f.Encoder.Cleanup;
        Cleanup other = synchronous == "capture" ? f.Encoder.Cleanup : f.Capture.Cleanup;
        sync.SyncError = oce;
        other.Fail([nested, nested]);
        try
        {
            IVideoFrameProducer producer = await f.Create();
            var failure = Assert.IsType<AggregateException>(await Error(producer.StopAsync()));
            Assert.True(producer.Completion.IsFaulted);
            AssertSubtree(failure, [oce]);
            AssertSubtree(failure, [nested, nested]);
            Assert.Equal(1, sync.AsyncCalls);
            Assert.Equal(1, other.AsyncCalls);
            Assert.Same(failure, await Error(producer.DisposeAsync().AsTask()));
        }
        finally { await f.Finish(); }
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("encode")]
    public async Task Production_fault_automatically_stops_and_retains_pipeline_and_both_cleanup_error_trees(string stage)
    {
        var f = new Fixture();
        var nested = new AggregateException("生产原嵌套根。", new IOException("叶。"));
        var oce = new OperationCanceledException("Faulted 原任务中的 OCE 不是正常取消。");
        Exception[] roots = [nested, oce, nested];
        var captureCleanup = new AggregateException("采集清理原节点。", new IOException("采集清理叶。"));
        var encoderCleanup = new AggregateException("编码清理原节点。", new IOException("编码清理叶。"));
        f.Capture.Cleanup.Fail([captureCleanup]);
        f.Encoder.Cleanup.Fail([encoderCleanup]);
        try
        {
            IVideoFrameProducer producer = await f.Create();
            producer.Start();
            Call<CapturedFrame> capture = await Success(f.Captures.At(0));
            CallBase operation;
            if (stage == "capture") operation = capture;
            else
            {
                capture.Reply();
                operation = await Success(f.Encodes.At(0));
            }
            Task<EncodedFrame?> read = f.Read();
            operation.Fail(roots);
            await Success(operation.CancellationSeen.Task);
            f.ReleaseCalls();
            // 无需再次调用 Stop/Dispose，生产故障本身必须驱动拥有依赖的适配器收尾。
            var failure = Assert.IsType<AggregateException>(await Error(producer.Completion));
            Assert.Equal(3, failure.InnerExceptions.Count);
            Assert.Same(Assert.Single(f.Pipeline.Completion.Exception!.InnerExceptions), failure.InnerExceptions[0]);
            AssertSubtree(failure, roots);
            AssertSubtree(failure, [captureCleanup]);
            AssertSubtree(failure, [encoderCleanup]);
            Assert.Equal(1, f.Capture.Cleanup.AsyncCalls);
            Assert.Equal(1, f.Encoder.Cleanup.AsyncCalls);
            Assert.Same(failure, await Error(read));
            Assert.Same(failure, await Error(f.Read()));
            Assert.Same(failure, await Error(producer.StopAsync()));
            f.AssertOwnersReleased();
        }
        finally { await f.Finish(); }
    }

    [Fact]
    public async Task Await_probe_detects_a_cancellation_proxy_by_identity_before_its_watchdog()
    {
        var original = Signal();
        using var cancellation = new CancellationTokenSource();
        Task proxy = original.Task.WaitAsync(cancellation.Token);
        Task observing = Probe(proxy);
        try
        {
            Task actual = await RegisteredAwait(observing);
            Assert.Same(proxy, actual);
            Exception? failure = Record.Exception(() => Assert.Same(original.Task, actual));
            Assert.IsAssignableFrom<XunitException>(failure);
            Assert.DoesNotContain("Guard", failure!.Message);
        }
        finally
        {
            original.TrySetResult();
            await Success(original.Task);
            await Success(observing);
        }
    }

    private static async Task Probe(Task task) => await task.ConfigureAwait(false);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Finished(Task original)
    {
        using var cancellation = new CancellationTokenSource();
        Task guard = Task.Delay(Watchdog, cancellation.Token);
        try { Assert.Same(original, await Task.WhenAny(original, guard)); }
        finally { cancellation.Cancel(); }
    }
    private static async Task Success(Task task) { await Finished(task); await task; }
    private static async Task<T> Success<T>(Task<T> task) { await Finished(task); return await task; }
    private static async Task<Exception> Error(Task task)
    {
        await Finished(task);
        Exception? error = await Record.ExceptionAsync(() => task);
        return Assert.IsAssignableFrom<Exception>(error);
    }
    private static async Task Observe(Task task)
    {
        await Finished(task);
        _ = await Record.ExceptionAsync(() => task);
        _ = task.Exception;
    }
    private static async Task JoinInvocation(Task original)
    {
        await Observe(original);
        if (original.IsCompletedSuccessfully && original.GetType().GetProperty("Result")?.GetValue(original) is Task child)
            await JoinInvocation(child);
    }
    private static IEnumerable<Exception> Tree(Exception error)
    {
        yield return error;
        if (error is AggregateException aggregate)
            foreach (Exception child in aggregate.InnerExceptions)
                foreach (Exception descendant in Tree(child)) yield return descendant;
    }
    private static void AssertSubtree(Exception tree, IReadOnlyList<Exception> expected)
    {
        Assert.Contains(Tree(tree).OfType<AggregateException>(), node =>
            node.InnerExceptions.Count == expected.Count &&
            node.InnerExceptions.Select((child, index) => ReferenceEquals(child, expected[index])).All(same => same));
    }

    private static FieldInfo? RuntimeField(Type type, string name)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
            if (current.GetField(name, Fields | BindingFlags.DeclaredOnly) is { } field) return field;
        return null;
    }
    private static FieldInfo RequiredField(Type type, string name) => RuntimeField(type, name)
        ?? throw new XunitException($"缺少真实任务字段 {type.Name}.{name}，不能跳过 await 身份检查。");
    private static object? Machine(Task task) => RuntimeField(task.GetType(), "StateMachine")?.GetValue(task);
    private static string StateName(Task task) => Machine(task)?.GetType().Name ?? string.Empty;
    private static IEnumerable<Task> StoredTasks(object value, int depth = 0)
    {
        if (depth > 4) yield break;
        foreach (FieldInfo field in value.GetType().GetFields(Fields))
        {
            object? child = field.GetValue(value);
            if (child is Task task) yield return task;
            else if (child is not null && field.FieldType.IsValueType && !field.FieldType.IsPrimitive && !field.FieldType.IsEnum)
                foreach (Task nested in StoredTasks(child, depth + 1)) yield return nested;
        }
    }
    private static Task[] Continuations(Task task)
    {
        object? value = RequiredField(typeof(Task), "m_continuationObject").GetValue(task);
        object[] items;
        if (value is IList list) { lock (list) items = list.Cast<object?>().OfType<object>().ToArray(); }
        else items = value is null ? [] : [value];
        return items.Select(item => item is Delegate action ? action.Target ?? item :
            RuntimeField(item.GetType(), "m_action")?.GetValue(item) is Delegate scheduled
                ? scheduled.Target ?? item : item).OfType<Task>().ToArray();
    }
    private static bool Registered(Task source, Task target) =>
        Continuations(source).Any(next => ReferenceEquals(next, target));
    private static Task? CurrentAwait(Task owner)
    {
        object? state = Machine(owner);
        if (state is null) return null;
        Type type = state.GetType();
        object builder = RequiredField(type, "<>t__builder").GetValue(state)!;
        Assert.Contains(StoredTasks(builder), task => ReferenceEquals(task, owner));
        if (Assert.IsType<int>(RequiredField(type, "<>1__state").GetValue(state)) < 0) return null;
        foreach (FieldInfo field in type.GetFields(Fields).Where(f => f.Name.StartsWith("<>u__", StringComparison.Ordinal)))
            if (field.GetValue(state) is { } awaiter)
                foreach (Task awaited in StoredTasks(awaiter))
                    if (!awaited.IsCompleted && Registered(awaited, owner)) return awaited;
        return null;
    }
    private static async Task<Task> RegisteredAwait(Task owner)
    {
        using var guard = new CancellationTokenSource(Watchdog);
        while (true)
        {
            Assert.False(owner.IsCompleted, "原状态机提前完成，不能伪装成仍在 join。");
            // 任何真实已登记 await 都可作为就绪；随后才单独核对原任务身份。
            if (CurrentAwait(owner) is { } actual) return actual;
            Assert.False(guard.IsCancellationRequested, "Guard：原状态机没有发布可观察的 await。");
            await Task.Yield();
        }
    }
    private static async Task AssertAwaiting(Task owner, Task expected)
    {
        Task actual = await RegisteredAwait(owner);
        Assert.Same(expected, actual);
        Assert.False(expected.IsCompleted);
    }
    private static async Task<Task> DiscoverLoop(Task original, Task published, string method)
    {
        using var guard = new CancellationTokenSource(Watchdog);
        while (true)
        {
            Assert.False(published.IsCompleted, "公开循环不能先于真实原操作完成。");
            var pending = new Queue<Task>();
            var seen = new HashSet<Task>(ReferenceEqualityComparer.Instance);
            pending.Enqueue(original);
            while (pending.TryDequeue(out Task? candidate))
            {
                if (!seen.Add(candidate)) continue;
                if (StateName(candidate).Contains("<" + method + ">", StringComparison.Ordinal)) return candidate;
                foreach (Task next in Continuations(candidate)) pending.Enqueue(next);
            }
            Assert.False(original.IsCompleted, "原任务在发现真实循环前提前退出。");
            Assert.False(guard.IsCancellationRequested, "Guard：无法发现原循环状态机。");
            await Task.Yield();
        }
    }
    private static async Task<Task> DiscoverCleanup(Task original, Task join)
    {
        using var guard = new CancellationTokenSource(Watchdog);
        while (true)
        {
            Assert.False(join.IsCompleted, "清理 join 不能先于原 DisposeAsync 完成。");
            var pending = new Queue<Task>();
            var seen = new HashSet<Task>(ReferenceEqualityComparer.Instance);
            pending.Enqueue(original);
            while (pending.TryDequeue(out Task? candidate))
            {
                if (!seen.Add(candidate)) continue;
                if (StateName(candidate).Contains("RunCleanup", StringComparison.Ordinal)) return candidate;
                foreach (Task next in Continuations(candidate)) pending.Enqueue(next);
            }
            Assert.False(original.IsCompleted, "原 DisposeAsync 在观察其 await 之前退出。");
            Assert.False(guard.IsCancellationRequested, "Guard：无法发现依赖清理状态机。");
            await Task.Yield();
        }
    }

    private sealed class Fixture
    {
        private readonly object _gate = new();
        private readonly List<Owner> _owners = [];
        private readonly List<(Task<EncodedFrame?> Task, CancellationTokenSource Cancellation)> _reads = [];
        private bool _releaseCalls;
        private CallBase? _held;
        internal string? HeldStage;
        internal bool HoldPrefix;
        internal bool HoldTail;
        internal Log<CapturedFrame> Captures { get; } = new();
        internal Log<EncodedFrame> Encodes { get; } = new();
        internal Capture Capture { get; }
        internal Encoder Encoder { get; }
        internal IVideoFrameProducer? Producer { get; private set; }
        internal FramePipeline Pipeline => Assert.IsType<FramePipeline>(
            RequiredField(Producer!.GetType(), "_pipeline").GetValue(Producer));
        internal Fixture(bool synchronous = false)
        {
            Capture = synchronous ? new SyncCapture(this) : new AsyncCapture(this);
            Encoder = synchronous ? new SyncEncoder(this) : new AsyncEncoder(this);
        }
        internal Owner NewOwner()
        {
            var owner = new Owner();
            lock (_gate) _owners.Add(owner);
            return owner;
        }
        internal async Task<IVideoFrameProducer> Create()
        {
            IVideoFrameProducerFactory factory = new FramePipelineVideoProducerFactory(
                () => Capture, () => Encoder, timeProvider: new OverBudgetClock());
            return Producer = await Success(factory.CreateAsync(Guid.NewGuid(), CancellationToken.None).AsTask());
        }
        internal Task PublishedLoop(string stage) => Assert.IsAssignableFrom<Task>(
            RequiredField(typeof(FramePipeline), stage == "capture" ? "_captureLoop" : "_encodeLoop").GetValue(Pipeline));
        internal Task<EncodedFrame?> Read(CancellationToken caller = default)
        {
            var cancellation = caller.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(caller) : new();
            try
            {
                Task<EncodedFrame?> read = Producer!.ReadNextAsync(cancellation.Token).AsTask();
                _reads.Add((read, cancellation));
                return read;
            }
            catch { cancellation.Dispose(); throw; }
        }
        internal void Added(CallBase call)
        {
            lock (_gate) if (_releaseCalls && !ReferenceEquals(_held, call)) call.Release();
        }
        internal void ReleaseCalls(CallBase? held = null)
        {
            lock (_gate) { _releaseCalls = true; _held = held; }
            foreach (CallBase call in Captures.Snapshot().Cast<CallBase>().Concat(Encodes.Snapshot()))
                if (!ReferenceEquals(call, held)) call.Release();
        }
        internal void AssertOwnersReleased()
        {
            lock (_gate) Assert.All(_owners, owner => Assert.Equal(1, owner.DisposeCount));
        }
        internal async Task Finish()
        {
            // 先放全部闸，再独立发起停止并 join 每个原操作；Guard 从不代替原操作完成。
            ReleaseCalls();
            foreach (CallBase call in Captures.Snapshot().Cast<CallBase>().Concat(Encodes.Snapshot())) call.Release();
            Capture.Cleanup.Release();
            Encoder.Cleanup.Release();
            Captures.Finish();
            Encodes.Finish();
            Task[] cancelReads = _reads.Select(read => read.Cancellation.CancelAsync()).ToArray();
            Task<Task>? request = Producer is null ? null : Task.Factory.StartNew(Producer.StopAsync,
                CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
            bool joined = false;
            try
            {
                if (request is not null) await Observe(request);
                if (Producer is not null)
                {
                    await Observe(Pipeline.Completion);
                    await Observe(Producer.Completion);
                    await Observe(PublishedLoop("capture"));
                    await Observe(PublishedLoop("encode"));
                }
                foreach (CallBase call in Captures.Snapshot().Cast<CallBase>().Concat(Encodes.Snapshot())) await call.Join();
                foreach (Cleanup cleanup in new[] { Capture.Cleanup, Encoder.Cleanup })
                {
                    if (cleanup.Prefix.Entered.Task.IsCompleted)
                    {
                        await Observe(cleanup.Prefix.Exited.Task);
                        if (cleanup.Prefix.ExecutingTask is { } invocation) await JoinInvocation(invocation);
                    }
                    await Observe(cleanup.Original.Task);
                }
                foreach (Task cancel in cancelReads) await Observe(cancel);
                foreach (var read in _reads)
                {
                    await Observe(read.Task);
                    if (read.Task.IsCompletedSuccessfully) (await read.Task)?.Dispose();
                }
                joined = true;
            }
            finally
            {
                // 未完成的原调用不能因诊断 Guard 超时就被释放注册或强制回收 owner。
                if (joined)
                {
                    foreach (CallBase call in Captures.Snapshot().Cast<CallBase>().Concat(Encodes.Snapshot())) call.Unregister();
                    foreach (var read in _reads) read.Cancellation.Dispose();
                    lock (_gate) foreach (Owner owner in _owners) if (owner.DisposeCount == 0) owner.Dispose();
                }
            }
        }
    }

    private class Capture(Fixture f) : IScreenCaptureBackend
    {
        internal Cleanup Cleanup { get; } = new();
        public IReadOnlyList<DisplayInfo> GetDisplays() => [new(new DisplayId(11), "内存显示器", 0, 0, 1, 1, true)];
        public ValueTask<CapturedFrame> CaptureAsync(DisplayId displayId, CancellationToken token)
        {
            int index = f.Captures.Count;
            if (index >= 2) throw new XunitException("有限 fake 不允许第三次采集。");
            var call = new Call<CapturedFrame>(f, token, null,
                f.HeldStage == "capture" && index == 0 && f.HoldPrefix,
                f.HeldStage == "capture" && index == 0 && f.HoldTail,
                owner => new CapturedFrame(displayId, 1, 1, 4, FramePixelFormat.Bgra32, owner, index + 1));
            f.Captures.Add(call);
            f.Added(call);
            return call.Invoke();
        }
    }
    private sealed class AsyncCapture(Fixture f) : Capture(f), IDisposable, IAsyncDisposable
    {
        public void Dispose() => Cleanup.Dispose();
        public ValueTask DisposeAsync() => Cleanup.DisposeAsync();
    }
    private sealed class SyncCapture(Fixture f) : Capture(f), IDisposable
    {
        public void Dispose() => Cleanup.Dispose();
    }
    private class Encoder(Fixture f) : IFrameEncoder
    {
        internal Cleanup Cleanup { get; } = new();
        public ValueTask<EncodedFrame> EncodeAsync(CapturedFrame frame, VideoQualitySettings settings, CancellationToken token)
        {
            int index = f.Encodes.Count;
            if (index >= 2) throw new XunitException("有限 fake 不允许第三次编码。");
            var call = new Call<EncodedFrame>(f, token, frame,
                f.HeldStage == "encode" && index == 0 && f.HoldPrefix,
                f.HeldStage == "encode" && index == 0 && f.HoldTail,
                owner => new EncodedFrame(VideoCodec.Jpeg, 1, 1, (ulong)index + 1, frame.TimestampUs, 60, owner));
            f.Encodes.Add(call);
            f.Added(call);
            return call.Invoke();
        }
    }
    private sealed class AsyncEncoder(Fixture f) : Encoder(f), IDisposable, IAsyncDisposable
    {
        public void Dispose() => Cleanup.Dispose();
        public ValueTask DisposeAsync() => Cleanup.DisposeAsync();
    }
    private sealed class SyncEncoder(Fixture f) : Encoder(f), IDisposable
    {
        public void Dispose() => Cleanup.Dispose();
    }
    private sealed class Cleanup
    {
        private bool _holdTail;
        private int _asyncCalls;
        private int _syncCalls;
        internal Gate Prefix { get; } = new();
        internal TaskCompletionSource Original { get; } = Signal();
        internal Exception? SyncError;
        internal int AsyncCalls => Volatile.Read(ref _asyncCalls);
        internal int SyncCalls => Volatile.Read(ref _syncCalls);
        internal void HoldTail() => _holdTail = true;
        internal void Fail(Exception[] roots) => Original.TrySetException(roots);
        internal ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _asyncCalls);
            Prefix.Block();
            if (SyncError is { } error) throw error;
            if (!_holdTail) Original.TrySetResult();
            return new ValueTask(Original.Task);
        }
        internal void Dispose()
        {
            Interlocked.Increment(ref _syncCalls);
            Prefix.Block();
            if (SyncError is { } error) throw error;
        }
        internal void Release() { Prefix.Open(); Original.TrySetResult(); }
    }
    private sealed class Gate
    {
        private readonly TaskCompletionSource _release = Signal();
        private bool _held;
        internal TaskCompletionSource Entered { get; } = Signal();
        internal TaskCompletionSource Exited { get; } = Signal();
        internal Task? ExecutingTask;
        internal bool OnAdapterStack;
        internal void Hold() => _held = true;
        internal void Open() => _release.TrySetResult();
        internal void Block()
        {
            ExecutingTask = typeof(Task).GetField("t_currentTask", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as Task;
            OnAdapterStack = new StackTrace().GetFrames().Any(frame =>
            {
                string name = frame.GetMethod()?.DeclaringType?.Name ?? "";
                return name.Contains("<CompleteAsync>", StringComparison.Ordinal)
                    || name.Contains("<DisposeDependenciesAsync>", StringComparison.Ordinal);
            });
            Entered.TrySetResult();
            try { if (_held) _release.Task.GetAwaiter().GetResult(); }
            finally { Exited.TrySetResult(); }
        }
        internal async Task WaitAsync()
        {
            Entered.TrySetResult();
            try { if (_held) await _release.Task.ConfigureAwait(false); }
            finally { Exited.TrySetResult(); }
        }
    }
    private abstract class CallBase
    {
        private readonly CancellationTokenRegistration _registration;
        internal Gate Prefix { get; } = new();
        internal Gate Tail { get; } = new();
        internal TaskCompletionSource InvocationExited { get; } = Signal();
        internal TaskCompletionSource CancellationSeen { get; } = Signal();
        internal CancellationToken Token { get; }
        internal CapturedFrame? Input { get; }
        internal Owner? Owner;
        internal Task? Worker;
        protected CallBase(CancellationToken token, CapturedFrame? input, bool prefix, bool tail)
        {
            Token = token;
            Input = input;
            if (prefix) Prefix.Hold();
            if (tail) Tail.Hold();
            _registration = token.Register(() => CancellationSeen.TrySetResult());
        }
        internal abstract void ReplyDefault();
        internal abstract void Fail(Exception[] roots);
        internal abstract void Release();
        internal abstract Task Join();
        internal void Unregister() => _registration.Dispose();
    }
    private sealed class Call<T>(Fixture fixture, CancellationToken token, CapturedFrame? input,
        bool prefix, bool tail, Func<Owner, T> create) : CallBase(token, input, prefix, tail) where T : class, IDisposable
    {
        private readonly object _gate = new();
        private readonly bool _tail = tail;
        private readonly TaskCompletionSource<T> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ValueTask<T> Invoke()
        {
            try
            {
                Prefix.Block();
                Task<T> worker = _tail ? RunTail() : _result.Task;
                Worker = worker;
                return new ValueTask<T>(worker);
            }
            finally { InvocationExited.TrySetResult(); }
        }
        private async Task<T> RunTail()
        {
            T result = await _result.Task.ConfigureAwait(false);
            await Tail.WaitAsync().ConfigureAwait(false);
            if (Input is not null) _ = Input.Pixels.Length;
            return result;
        }
        internal T Reply()
        {
            lock (_gate)
            {
                if (_result.Task.IsCompleted) return _result.Task.GetAwaiter().GetResult();
                Owner = fixture.NewOwner();
                T frame = create(Owner);
                _result.SetResult(frame);
                return frame;
            }
        }
        internal override void ReplyDefault() => Reply();
        internal override void Fail(Exception[] roots) { lock (_gate) _result.TrySetException(roots); }
        internal override void Release()
        {
            lock (_gate) if (!_result.Task.IsCompleted) Reply();
            Prefix.Open();
            Tail.Open();
        }
        internal override async Task Join()
        {
            await Observe(InvocationExited.Task);
            await Observe(_result.Task);
            if (Worker is { } worker) await Observe(worker);
            await Observe(Prefix.Exited.Task);
            if (_tail && _result.Task.IsCompletedSuccessfully) await Observe(Tail.Exited.Task);
        }
    }
    private sealed class Log<T> where T : class, IDisposable
    {
        private readonly object _gate = new();
        private readonly List<Call<T>> _calls = [];
        private readonly Dictionary<int, TaskCompletionSource<Call<T>>> _waiting = [];
        internal int Count { get { lock (_gate) return _calls.Count; } }
        internal Call<T>[] Snapshot() { lock (_gate) return _calls.ToArray(); }
        internal void Add(Call<T> call)
        {
            lock (_gate)
            {
                int index = _calls.Count;
                _calls.Add(call);
                if (_waiting.Remove(index, out var waiter)) waiter.TrySetResult(call);
            }
        }
        internal Task<Call<T>> At(int index)
        {
            lock (_gate)
            {
                if (index < _calls.Count) return Task.FromResult(_calls[index]);
                if (!_waiting.TryGetValue(index, out var waiter)) _waiting.Add(index, waiter =
                    new TaskCompletionSource<Call<T>>(TaskCreationOptions.RunContinuationsAsynchronously));
                return waiter.Task;
            }
        }
        internal void Finish()
        {
            lock (_gate)
            {
                foreach (TaskCompletionSource<Call<T>> waiting in _waiting.Values) waiting.TrySetCanceled();
                _waiting.Clear();
            }
        }
    }
    private sealed class Owner : IMemoryOwner<byte>
    {
        private readonly byte[] _bytes = [1, 2, 3, 4];
        private int _disposed;
        internal int DisposeCount => Volatile.Read(ref _disposed);
        public Memory<byte> Memory
        {
            get { ObjectDisposedException.ThrowIf(DisposeCount != 0, this); return _bytes; }
        }
        public void Dispose() => Interlocked.Increment(ref _disposed);
    }
    private sealed class OverBudgetClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Add(ref _ticks, TimeSpan.TicksPerSecond);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            throw new XunitException("受控时钟不会等待墙钟节拍。");
    }
}
