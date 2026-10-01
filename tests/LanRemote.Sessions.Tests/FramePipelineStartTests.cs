using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using Xunit;
using Xunit.Sdk;

namespace LanRemote.Sessions.Tests;

public sealed class FramePipelineStartTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(30);
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public async Task Stop_wins_before_Start_and_no_loop_or_observer_is_invoked()
    {
        var f = new Fixture();
        try
        {
            await f.ObserveCoordinator();
            Task capturePlaceholder = f.CaptureLoop;
            Task encodePlaceholder = f.EncodeLoop;
            Task completion = f.Pipeline.Completion;
            Assert.Same(completion, await Success(f.RequestStop()));
            Assert.True(f.StartPublished.IsCompletedSuccessfully);
            Assert.IsType<InvalidOperationException>(await Error(f.Start()));
            await Success(completion);
            Assert.Equal(0, f.HookCalls);
            Assert.Equal(0, f.Capture.Calls);
            Assert.Same(capturePlaceholder, f.CaptureLoop);
            Assert.Same(encodePlaceholder, f.EncodeLoop);
            Assert.Same(completion, await Success(f.RequestStop(dispose: true)));
            f.AssertBorrowedDependencies();
        }
        finally { await f.Finish(); }
    }

    [Fact]
    public async Task Start_wins_CAS_and_Stop_waits_for_publication_then_joins_the_real_capture_loop()
    {
        var f = new Fixture();
        try
        {
            await f.ObserveCoordinator();
            Task encodePlaceholder = f.EncodeLoop;
            Task starting = f.Start();
            await f.ObserveHeldCapture();
            Assert.Same(encodePlaceholder, f.EncodeLoop);
            Assert.False(f.StartPublished.IsCompleted);

            Task completion = f.Pipeline.Completion;
            Assert.Same(completion, await Success(f.RequestStop()));
            await f.AssertWaitingForPublication();
            await Success(f.Capture.CancellationSeen.Task);
            Assert.False(starting.IsCompleted);
            Assert.False(f.Capture.Operation.Task.IsCompleted);
            Assert.Equal(0, f.Owner.DisposeCount);

            f.Publication.Open();
            await Success(starting);
            Assert.True(f.StartPublished.IsCompletedSuccessfully);
            Assert.NotSame(encodePlaceholder, f.EncodeLoop);
            await f.AssertCaptureJoined();
            await Success(f.EncodeLoop);

            // 原操作迟到返回后，真实循环仍须等待本地帧的 Dispose，不能只 join 原操作。
            f.Capture.Release();
            await Success(f.Owner.Disposal.Entered.Task);
            await f.AssertCaptureJoined();
            Assert.False(completion.IsCompleted);
            f.Owner.Disposal.Open();
            await Success(completion);
            await Success(f.CaptureLoop);
            Assert.Equal(1, f.HookCalls);
            Assert.Equal(1, f.Capture.Calls);
            Assert.Equal(1, f.Owner.DisposeCount);
            f.AssertBorrowedDependencies();
        }
        finally { await f.Finish(); }
    }

    [Theory]
    [InlineData("single")]
    [InlineData("nested")]
    [InlineData("oce")]
    public async Task Observer_failure_publishes_and_joins_started_capture_without_replacing_original_error(string shape)
    {
        var leaf = new IOException("观察回调的原始错误。");
        Exception original = shape switch
        {
            "nested" => new AggregateException("必须保留原嵌套节点。", new AggregateException(leaf), leaf),
            "oce" => new OperationCanceledException("观察回调 OCE 也必须保留为启动错误。"),
            _ => leaf
        };
        var f = new Fixture(original);
        try
        {
            await f.ObserveCoordinator();
            Task encodePlaceholder = f.EncodeLoop;
            Task starting = f.Start();
            await f.ObserveHeldCapture();
            f.Publication.Open();
            Assert.Same(original, await Error(starting));
            // 不先手动 Stop，直接验证 catch 请求停止、finally 发布；缺失它们是契约失败。
            Assert.True(f.StopSignal.IsCompletedSuccessfully);
            Assert.True(f.StartPublished.IsCompletedSuccessfully);
            Assert.Same(encodePlaceholder, f.EncodeLoop);
            await Success(f.Capture.CancellationSeen.Task);
            await f.AssertCaptureJoined();
            Assert.False(f.Capture.Operation.Task.IsCompleted);
            Assert.Equal(0, f.Owner.DisposeCount);

            f.Capture.Release();
            await Success(f.Owner.Disposal.Entered.Task);
            await f.AssertCaptureJoined();
            f.Owner.Disposal.Open();
            var failure = Assert.IsType<AggregateException>(await Error(f.Pipeline.Completion));
            Assert.Same(original, Assert.Single(failure.InnerExceptions));
            Assert.Same(failure, Assert.Single(f.Pipeline.Completion.Exception!.InnerExceptions));
            Assert.True(f.Pipeline.Completion.IsFaulted);
            Assert.Same(failure, await Error(await Success(f.RequestStop(dispose: true))));
            await Success(f.CaptureLoop);
            Assert.Equal(1, f.Owner.DisposeCount);
            Assert.Equal(1, f.HookCalls);
            Assert.Equal(1, f.Capture.Calls);
            f.AssertBorrowedDependencies();
        }
        finally { await f.Finish(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Second_Start_is_rejected_inside_the_first_publication_window_even_when_Stop_competes(bool stopFirst)
    {
        var f = new Fixture();
        try
        {
            await f.ObserveCoordinator();
            Task first = f.Start();
            await f.ObserveHeldCapture();
            Task captureLoop = f.CaptureLoop;
            Task encodePlaceholder = f.EncodeLoop;
            if (stopFirst)
            {
                Assert.Same(f.Pipeline.Completion, await Success(f.RequestStop()));
                await f.AssertWaitingForPublication();
            }

            Task second = f.Start();
            // 若错误地允许第二次启动，观察回调的重入也能使探针就绪，不等它被闸门卡到 Guard。
            await Success(Task.WhenAny(second, f.HookReentered.Task));
            Assert.Equal(1, f.HookCalls);
            Assert.IsType<InvalidOperationException>(await Error(second));
            Assert.False(first.IsCompleted);
            Assert.Same(captureLoop, f.CaptureLoop);
            Assert.Same(encodePlaceholder, f.EncodeLoop);
            Assert.Same(f.Pipeline.Completion, await Success(f.RequestStop()));
            await f.AssertWaitingForPublication();
            Assert.Same(f.Pipeline.Completion, await Success(f.RequestStop(dispose: true)));
            await f.AssertWaitingForPublication();

            f.Publication.Open();
            await Success(first);
            await f.AssertCaptureJoined();
            f.Capture.Release();
            await Success(f.Owner.Disposal.Entered.Task);
            await f.AssertCaptureJoined();
            f.Owner.Disposal.Open();
            await Success(f.Pipeline.Completion);
            Assert.Equal(1, f.HookCalls);
            Assert.Equal(1, f.Capture.Calls);
            Assert.Equal(1, f.Owner.DisposeCount);
            f.AssertBorrowedDependencies();
        }
        finally { await f.Finish(); }
    }

    [Fact]
    public async Task Await_probe_returns_an_unexpected_task_before_the_identity_assertion()
    {
        var f = new Fixture();
        try
        {
            await f.ObserveCoordinator();
            // 尚未 Stop 时真实 await 是 stopSignal。探针不能把 startPublished 的正确身份当就绪条件。
            Task actual = await f.CoordinatorAwait();
            Assert.Same(f.StopSignal, actual);
            Exception? failure = Record.Exception(() => Assert.Same(f.StartPublished, actual));
            Assert.IsAssignableFrom<XunitException>(failure);
            Assert.DoesNotContain("Guard", failure!.Message);
        }
        finally { await f.Finish(); }
    }

    private sealed class Fixture
    {
        private readonly List<Task> _starts = [];
        private readonly List<Task> _requests = [];
        private readonly Encoder _encoder = new();
        private Task? _coordinator;
        private Task? _captureMachine;
        private Task? _publishedCapture;
        private int _hookCalls;
        internal Gate Publication { get; } = new();
        internal TaskCompletionSource HookReentered { get; } = Signal();
        internal CountingOwner Owner { get; } = new();
        internal CaptureBackend Capture { get; }
        internal FramePipeline Pipeline { get; }
        internal int HookCalls => Volatile.Read(ref _hookCalls);
        internal Task CaptureLoop => Field<Task>(Pipeline, "_captureLoop");
        internal Task EncodeLoop => Field<Task>(Pipeline, "_encodeLoop");
        internal Task StartPublished => Field<TaskCompletionSource>(Pipeline, "_startPublished").Task;
        internal Task StopSignal => Field<TaskCompletionSource>(Pipeline, "_stopSignal").Task;

        internal Fixture(Exception? observerFailure = null)
        {
            Capture = new CaptureBackend(Owner);
            Pipeline = new FramePipeline(Capture, _encoder, new DisplayId(7), 2, 2, null, () =>
            {
                if (Interlocked.Increment(ref _hookCalls) > 1) HookReentered.TrySetResult();
                Publication.Block();
                if (observerFailure is not null) throw observerFailure;
            });
        }

        internal Task Start()
        {
            // 保存执行真实同步 Start 的任务，不拿 Completion 或另造的完成信号代替它。
            Task task = Task.Factory.StartNew(Pipeline.Start, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            _starts.Add(task);
            return task;
        }

        internal Task<Task> RequestStop(bool dispose = false)
        {
            Task<Task> task = Task.Factory.StartNew(
                () => dispose ? Pipeline.DisposeAsync().AsTask() : Pipeline.StopAsync(),
                CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
            _requests.Add(task);
            return task;
        }

        internal async Task ObserveCoordinator()
        {
            _coordinator = await DiscoverMachine(StopSignal, Pipeline.Completion, Pipeline, "CoordinateStopAsync");
            await AssertForwarded(_coordinator, Pipeline.Completion);
        }

        internal async Task ObserveHeldCapture()
        {
            await Success(Publication.Entered.Task);
            await Success(Capture.Entered.Task);
            _publishedCapture = CaptureLoop;
            _captureMachine = await DiscoverMachine(Capture.Operation.Task, _publishedCapture, Pipeline, "CaptureLoopAsync");
            Task actual = await RegisteredAwait(_captureMachine, Pipeline, "CaptureLoopAsync", _publishedCapture);
            Assert.Same(Capture.Operation.Task, actual);
            await AssertForwarded(_captureMachine, _publishedCapture);
        }

        internal Task<Task> CoordinatorAwait() => RegisteredAwait(
            _coordinator ?? throw new XunitException("必须先观察真实协调器。"), Pipeline, "CoordinateStopAsync", Pipeline.Completion);

        internal async Task AssertWaitingForPublication()
        {
            // 任意实际 awaitedTask 就绪后才比较身份；遗漏发布等待会直接暴露 WhenAll/提前完成。
            Task awaitedTask = await CoordinatorAwait();
            Assert.Same(StartPublished, awaitedTask);
            Assert.False(StartPublished.IsCompleted);
            Assert.False(Publication.IsOpen);
            Assert.False(Pipeline.Completion.IsCompleted);
            Assert.True(IsRegistered(_coordinator!, Pipeline.Completion));
        }

        internal async Task AssertCaptureJoined()
        {
            Task join = await CoordinatorAwait();
            Assert.StartsWith("WhenAllPromise", join.GetType().Name);
            Task machine = Assert.IsAssignableFrom<Task>(_captureMachine);
            Task published = Assert.IsAssignableFrom<Task>(_publishedCapture);
            Assert.Same(published, CaptureLoop);
            Assert.False(machine.IsCompleted);
            Assert.False(published.IsCompleted);
            Assert.True(IsRegistered(machine, published), "真实采集状态机必须仍向已发布的循环任务报告完成。");
            Assert.True(IsRegistered(published, join), "Stop 必须 join 实际已启动的采集循环，而非初始占位任务。");
            Assert.True(IsRegistered(_coordinator!, Pipeline.Completion), "Completion 必须承接真实协调状态机。");
        }

        internal void AssertBorrowedDependencies()
        {
            Assert.Equal(0, Capture.DisposeCount);
            Assert.Equal(0, _encoder.DisposeCount);
            Assert.Equal(0, _encoder.Calls);
        }

        internal async Task Finish()
        {
            // 所有闸先放开，再等待任何任务；失败的测试也不能留下阻塞的 Start/原操作/disposer。
            Publication.Open();
            Owner.Disposal.Open();
            Capture.Release();
            _ = RequestStop();
            try
            {
                await Task.WhenAll(_starts.Concat(_requests).Select(Observe));
            }
            finally
            {
                try
                {
                    // Start 全部真实退出后才读取最终循环引用；同时独立 join 早先发现的原状态机。
                    // 即使有缺陷的 Completion 提前完成，也不能替代这些真实任务的回收。
                    Task?[] tasks = [CaptureLoop, EncodeLoop, _publishedCapture, _captureMachine,
                        _coordinator, Pipeline.Completion, Capture.Calls == 0 ? null : Capture.Operation.Task];
                    await Task.WhenAll(tasks.OfType<Task>().Distinct().Select(Observe));
                }
                finally { Capture.Unregister(); }
            }
        }
    }

    private sealed class CaptureBackend(CountingOwner owner) : IScreenCaptureBackend, IDisposable, IAsyncDisposable
    {
        private readonly object _gate = new();
        private CancellationTokenRegistration _registration;
        private CapturedFrame? _frame;
        private bool _released;
        private int _calls;
        private int _disposeCount;
        internal TaskCompletionSource<CapturedFrame> Operation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Entered { get; } = Signal();
        internal TaskCompletionSource CancellationSeen { get; } = Signal();
        internal int Calls => Volatile.Read(ref _calls);
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        public IReadOnlyList<DisplayInfo> GetDisplays() => Array.Empty<DisplayInfo>();

        public ValueTask<CapturedFrame> CaptureAsync(DisplayId displayId, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (Interlocked.Increment(ref _calls) != 1) throw new XunitException("发布窗测试不允许第二次采集。");
                _frame = new CapturedFrame(displayId, 1, 1, 4, FramePixelFormat.Bgra32, owner, 1);
                _registration = cancellationToken.Register(() => CancellationSeen.TrySetResult());
                if (_released) Operation.TrySetResult(_frame);
                Entered.TrySetResult();
                // 此原任务故意忽略取消，只有测试放闸才交出帧，不能由取消代理代替它。
                return new ValueTask<CapturedFrame>(Operation.Task);
            }
        }

        internal void Release()
        {
            lock (_gate)
            {
                _released = true;
                if (_frame is not null) Operation.TrySetResult(_frame);
            }
        }

        internal void Unregister() => _registration.Dispose();
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class Encoder : IFrameEncoder, IDisposable, IAsyncDisposable
    {
        private int _calls;
        private int _disposeCount;
        internal int Calls => Volatile.Read(ref _calls);
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        public ValueTask<EncodedFrame> EncodeAsync(CapturedFrame frame, VideoQualitySettings settings, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            throw new XunitException("Stop 已请求且原采集未返回时不应编码。");
        }
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class CountingOwner : IMemoryOwner<byte>
    {
        private readonly byte[] _bytes = [1, 2, 3, 4];
        private int _disposeCount;
        internal Gate Disposal { get; } = new();
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        public Memory<byte> Memory
        {
            get { ObjectDisposedException.ThrowIf(DisposeCount != 0, this); return _bytes; }
        }
        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            Disposal.Block();
        }
    }

    private sealed class Gate
    {
        private readonly TaskCompletionSource _release = Signal();
        internal TaskCompletionSource Entered { get; } = Signal();
        internal bool IsOpen => _release.Task.IsCompleted;
        internal void Open() => _release.TrySetResult();
        internal void Block()
        {
            Entered.TrySetResult();
            // 闸内不制造超时异常，以免被误认成产品错误；测试 finally 无条件放闸并 join 原任务。
            _release.Task.GetAwaiter().GetResult();
        }
    }

    // Guard 仅限制基础设施等待，不是业务异常或变异 kill；只从已经完成的原任务读取错误。
    private static async Task Finished(Task original)
    {
        using var cancellation = new CancellationTokenSource();
        Task guard = Task.Delay(Watchdog, cancellation.Token);
        try
        {
            Task winner = await Task.WhenAny(original, guard);
            Assert.True(ReferenceEquals(original, winner), "Guard：原任务未退出，不能计为契约断言或变异 kill。");
        }
        finally { cancellation.Cancel(); }
    }

    private static async Task Success(Task original) { await Finished(original); await original; }
    private static async Task<T> Success<T>(Task<T> original) { await Finished(original); return await original; }
    private static async Task<Exception> Error(Task original)
    {
        await Finished(original);
        Exception? error = await Record.ExceptionAsync(() => original);
        Assert.NotNull(error);
        return error;
    }
    private static async Task Observe(Task original)
    {
        await Finished(original);
        _ = await Record.ExceptionAsync(() => original);
        _ = original.Exception;
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // 私有反射只读真实状态机与 continuation；不写字段、不调用私有生产方法、不伪造发布任务。
    private static FieldInfo? RuntimeField(Type type, string name)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
            if (current.GetField(name, Fields | BindingFlags.DeclaredOnly) is { } field) return field;
        return null;
    }
    private static FieldInfo RequiredField(Type type, string name) => RuntimeField(type, name)
        ?? throw new XunitException($"运行时缺少 {type.Name}.{name}，不能跳过真实等待链验证。");
    private static T Field<T>(object instance, string name) =>
        Assert.IsAssignableFrom<T>(RequiredField(instance.GetType(), name).GetValue(instance));

    private static object[] Continuations(Task task)
    {
        object? value = RequiredField(typeof(Task), "m_continuationObject").GetValue(task);
        if (value is IList list)
        {
            lock (list) return list.Cast<object?>().OfType<object>().ToArray();
        }
        return value is null ? [] : [value];
    }
    private static object ContinuationTarget(object continuation)
    {
        if (continuation is Delegate action) return action.Target ?? continuation;
        if (RuntimeField(continuation.GetType(), "m_action")?.GetValue(continuation) is Delegate scheduled)
            return scheduled.Target ?? continuation;
        return continuation;
    }
    private static bool IsRegistered(Task antecedent, Task continuation) => Continuations(antecedent)
        .Any(item => ReferenceEquals(ContinuationTarget(item), continuation));

    private static Type MachineType(string method) => typeof(FramePipeline).GetMethod(method, Fields)?
        .GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
        ?? throw new XunitException($"缺少 {method} 的真实状态机元数据。");
    private static object? StateMachine(Task task) => RuntimeField(task.GetType(), "StateMachine")?.GetValue(task);

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

    private static Task? FindRegisteredAwait(Task owner, FramePipeline pipeline, Type type)
    {
        object? state = StateMachine(owner);
        if (state is null) return null;
        Assert.IsType(type, state);
        Assert.Same(pipeline, RequiredField(type, "<>4__this").GetValue(state));
        object builder = RequiredField(type, "<>t__builder").GetValue(state)!;
        Assert.Contains(StoredTasks(builder), task => ReferenceEquals(owner, task));
        int number = Assert.IsType<int>(RequiredField(type, "<>1__state").GetValue(state));
        if (number < 0) return null;
        // 只扫描 awaiter 的任务槽；不把恰好保存于局部变量的 task 当作当前 await。
        foreach (FieldInfo field in type.GetFields(Fields).Where(field => field.Name.StartsWith("<>u__", StringComparison.Ordinal)))
        {
            if (field.GetValue(state) is not { } awaiter) continue;
            foreach (Task awaited in StoredTasks(awaiter))
                if (!awaited.IsCompleted && IsRegistered(awaited, owner)) return awaited;
        }
        return null;
    }

    private static async Task<Task> RegisteredAwait(Task owner, FramePipeline pipeline, string method, Task published)
    {
        Type type = MachineType(method);
        using var cancellation = new CancellationTokenSource();
        Task guard = Task.Delay(Watchdog, cancellation.Token);
        try
        {
            while (true)
            {
                Assert.False(owner.IsCompleted, $"{method} 提前退出，不能冒充完整等待。");
                Assert.False(published.IsCompleted, "发布任务提前完成，不能等待断开的 await 登记。");
                Task? actual = FindRegisteredAwait(owner, pipeline, type);
                if (actual is not null)
                {
                    Assert.False(owner.IsCompleted, $"{method} 在观察期间提前退出。");
                    Assert.False(published.IsCompleted, "发布任务提前完成，不能接受残留 await 登记。");
                    return actual;
                }
                if (guard.IsCompleted) throw new XunitException($"Guard：{method} 的实际 await 尚不可观察，不计为 kill。");
                await Task.Yield();
            }
        }
        finally { cancellation.Cancel(); }
    }

    private static async Task AssertForwarded(Task machine, Task published)
    {
        using var cancellation = new CancellationTokenSource();
        Task guard = Task.Delay(Watchdog, cancellation.Token);
        try
        {
            while (true)
            {
                Assert.False(published.IsCompleted, "发布任务提前完成，原状态机的解包关系已经断开。");
                Assert.False(machine.IsCompleted, "原状态机在建立解包关系前已退出。");
                if (Continuations(machine).Length != 0) break;
                if (guard.IsCompleted) throw new XunitException("Guard：原状态机的外层任务尚未登记，不计为 kill。");
                await Task.Yield();
            }
            Assert.False(published.IsCompleted, "发布任务提前完成，不能接受旧 continuation。");
            Assert.True(IsRegistered(machine, published), "发布任务没有承接真实状态机。");
        }
        finally { cancellation.Cancel(); }
    }

    private static async Task<Task> DiscoverMachine(Task anchor, Task published, FramePipeline pipeline, string method)
    {
        Type type = MachineType(method);
        using var cancellation = new CancellationTokenSource();
        Task guard = Task.Delay(Watchdog, cancellation.Token);
        try
        {
            while (true)
            {
                Assert.False(published.IsCompleted, "发布任务提前完成，原状态机的等待关系已经断开。");
                var pending = new Queue<Task>();
                var seen = new HashSet<Task>(ReferenceEqualityComparer.Instance);
                pending.Enqueue(anchor);
                while (pending.TryDequeue(out Task? candidate))
                {
                    if (!seen.Add(candidate)) continue;
                    if (StateMachine(candidate) is { } state && state.GetType() == type)
                    {
                        await RegisteredAwait(candidate, pipeline, method, published);
                        return candidate;
                    }
                    foreach (object continuation in Continuations(candidate))
                        if (ContinuationTarget(continuation) is Task next) pending.Enqueue(next);
                }
                Assert.False(anchor.IsCompleted, "用于发现状态机的受控原任务已经退出。");
                Assert.False(published.IsCompleted, "发布任务提前完成，不能用 Guard 代替契约断言。");
                if (guard.IsCompleted) throw new XunitException($"Guard：无法从 continuation 图找到 {method}，不计为 kill。");
                await Task.Yield();
            }
        }
        finally { cancellation.Cancel(); }
    }
}
