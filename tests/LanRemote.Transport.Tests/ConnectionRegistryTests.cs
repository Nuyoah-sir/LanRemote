using System.Diagnostics;

namespace LanRemote.Transport.Tests;

/// <summary>
/// <see cref="ConnectionRegistry"/> 的行为。
/// </summary>
/// <remarks>
/// 覆盖评审 A-18：连接任务有界、可观测、停机可 join。
/// 重点是「停机时 handler 卡住」这一支——优雅取消不够，必须还有强制释放兜底。
/// </remarks>
public sealed class ConnectionRegistryTests
{
    [Fact(Timeout = 30_000)]
    public async Task Register_And_Complete_Tracks_Count()
    {
        ConnectionRegistry registry = new();

        ConnectionRegistration? first = registry.TryRegister(new NoopResource());
        ConnectionRegistration? second = registry.TryRegister(new NoopResource());

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(2, registry.Count);

        first.Dispose();
        Assert.Equal(1, registry.Count);

        second.Dispose();
        Assert.Equal(0, registry.Count);
    }

    [Fact(Timeout = 30_000)]
    public async Task Disposal_Is_Idempotent()
    {
        ConnectionRegistry registry = new();

        ConnectionRegistration? registration = registry.TryRegister(new NoopResource());
        Assert.NotNull(registration);

        registration.Dispose();
        registration.Dispose();

        Assert.Equal(0, registry.Count);
    }

    [Fact(Timeout = 30_000)]
    public async Task Stop_Marks_Stopping_And_Refuses_New_Registrations()
    {
        ConnectionRegistry registry = new();

        await registry.StopAllAsync(TimeSpan.FromSeconds(1));

        Assert.True(registry.IsStopping);
        Assert.Null(registry.TryRegister(new NoopResource()));
    }

    [Fact(Timeout = 30_000)]
    public async Task Stop_Cancels_Ignored_Cancellation_And_Still_Joins()
    {
        ConnectionRegistry registry = new();

        // 一个"根本不看取消令牌"的 handler：用来证明光靠 Cancel() 不够。
        StubbornResource stubborn = new();
        ConnectionRegistration? registration = registry.TryRegister(stubborn);
        Assert.NotNull(registration);

        Task handler = Task.Run(async () =>
        {
            await stubborn.NeverEndsUntilDisposed;
            registration.Dispose();
        });

        // 等一下确认它真的没被 Cancel 唤醒。
        await Task.Delay(100);
        Assert.False(stubborn.NeverEndsUntilDisposed.IsCompleted);

        ConnectionStopReport stop = await registry.StopAllAsync(TimeSpan.FromSeconds(3));

        Assert.True(stop.AllFinished, "强制释放 socket 之后 handler 必须结束。");
        Assert.Equal(1, stop.Total);
        Assert.Equal(0, stop.Unfinished);
        Assert.Equal(0, registry.Count);
        Assert.True(handler.IsCompleted);
        Assert.True(stubborn.Disposed);
    }

    [Fact(Timeout = 30_000)]
    public async Task Stop_Respects_Cancellation_For_Cooperative_Handlers()
    {
        ConnectionRegistry registry = new();

        ConnectionRegistration? registration = registry.TryRegister(new NoopResource());
        Assert.NotNull(registration);

        TaskCompletionSource observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task handler = Task.Run(async () =>
        {
            using CancellationTokenRegistration _ =
                registration.Cancellation.Register(() => observed.TrySetResult());
            await observed.Task;
            registration.Dispose();
        });

        ConnectionStopReport stop = await registry.StopAllAsync(TimeSpan.FromSeconds(3));

        Assert.True(stop.AllFinished);
        Assert.True(observed.Task.IsCompleted);
        Assert.Equal(0, registry.Count);
        await handler;
    }

    /// <summary>
    /// <b>M3.1 B18</b>：停机报告必须能<b>数出</b>「几条没完成」——混装场景给出 2/1，
    /// 而不是笼统的一个是非值。
    /// </summary>
    /// <remarks>
    /// 一条配合取消正常收尾、一条连强制释放都不理会。预算执行也要对：
    /// 耗时与预算同量级——不是提前放弃（那样会虚报未完成），也不是无限等待。
    /// </remarks>
    [Fact(Timeout = 30_000)]
    public async Task Stop_Counts_The_Connections_That_Did_Not_Finish()
    {
        ConnectionRegistry registry = new();

        ConnectionRegistration? cooperative = registry.TryRegister(new NoopResource());
        ConnectionRegistration? stuck = registry.TryRegister(new NoopResource());
        Assert.NotNull(cooperative);
        Assert.NotNull(stuck);

        TaskCompletionSource observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task handler = Task.Run(async () =>
        {
            using CancellationTokenRegistration _ =
                cooperative.Cancellation.Register(() => observed.TrySetResult());
            await observed.Task;
            cooperative.Dispose();
        });

        Stopwatch clock = Stopwatch.StartNew();
        ConnectionStopReport stop = await registry.StopAllAsync(TimeSpan.FromMilliseconds(400));
        clock.Stop();

        Assert.Equal(2, stop.Total);

        // 关键判据：能数出「1 条没完成」。
        Assert.Equal(1, stop.Unfinished);
        Assert.False(stop.AllFinished);

        Assert.True(
            clock.Elapsed >= TimeSpan.FromMilliseconds(250),
            $"耗时 {clock.Elapsed}——像是没等完预算就报了未完成。");
        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(3),
            $"耗时 {clock.Elapsed}——超出 400 ms 预算太多。");

        await handler;

        // 迟到的结束也要能安全处理：报告已经给出结论，之后这条连接才收尾。
        stuck.Dispose();
        Assert.Equal(0, registry.Count);
    }

    /// <summary>
    /// <b>M3.1 B18</b>：空表的停机报告是干净的成功（0/0），并且不虚耗预算。
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task Stop_On_Empty_Registry_Reports_Clean_Success()
    {
        ConnectionRegistry registry = new();

        Stopwatch clock = Stopwatch.StartNew();
        ConnectionStopReport stop = await registry.StopAllAsync(TimeSpan.FromSeconds(1));
        clock.Stop();

        Assert.Equal(0, stop.Total);
        Assert.Equal(0, stop.Unfinished);
        Assert.True(stop.AllFinished);
        Assert.True(registry.IsStopping);

        // 空表没有等待对象：不该为了「等预算」而空转。
        Assert.True(
            clock.Elapsed < TimeSpan.FromMilliseconds(500),
            $"空表停机耗时 {clock.Elapsed}，不该等预算。");
    }

    [Fact(Timeout = 30_000)]
    public async Task Stop_Retains_Unfinished_Registration_Across_Repeated_Timeouts()
    {
        ConnectionRegistry registry = new();
        ConnectionRegistration? registration = registry.TryRegister(new NoopResource());
        Assert.NotNull(registration);

        try
        {
            ConnectionStopReport first = await registry.StopAllAsync(TimeSpan.FromMilliseconds(80));
            int countAfterFirst = registry.Count;
            ConnectionStopReport second = await registry.StopAllAsync(TimeSpan.FromMilliseconds(80));
            int countAfterSecond = registry.Count;

            Assert.Equal(1, first.Total);
            Assert.Equal(1, first.Unfinished);
            Assert.False(first.AllFinished);
            Assert.Equal(1, countAfterFirst);
            Assert.Equal(1, second.Total);
            Assert.Equal(1, second.Unfinished);
            Assert.False(second.AllFinished);
            Assert.Equal(1, countAfterSecond);
        }
        finally
        {
            registration.Dispose();
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Stop_Preserves_Cancellation_Until_Registration_Is_Disposed()
    {
        ConnectionRegistry registry = new();
        ConnectionRegistration? registration = registry.TryRegister(new NoopResource());
        Assert.NotNull(registration);

        try
        {
            CancellationToken original = registration.Cancellation;
            await registry.StopAllAsync(TimeSpan.FromMilliseconds(80));

            CancellationToken current = default;
            Assert.Null(Record.Exception(() => { current = registration.Cancellation; }));
            Assert.Equal(original, current);
            Assert.True(current.IsCancellationRequested);

            int callbackCount = 0;
            using CancellationTokenRegistration first =
                original.Register(() => Interlocked.Increment(ref callbackCount));
            using CancellationTokenRegistration second =
                current.Register(() => Interlocked.Increment(ref callbackCount));
            Assert.Equal(2, Volatile.Read(ref callbackCount));
        }
        finally
        {
            registration.Dispose();
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Concurrent_And_Repeated_Stops_Force_Dispose_Unfinished_Resource_Only_Once()
    {
        ConnectionRegistry registry = new();
        CountingResource resource = new();
        ConnectionRegistration? registration = registry.TryRegister(resource);
        Assert.NotNull(registration);

        TaskCompletionSource cancellationEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseCancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<Task<ConnectionStopReport>> stops = new();
        using CancellationTokenRegistration cancellation = registration.Cancellation.Register(() =>
        {
            cancellationEntered.TrySetResult();
            releaseCancellation.Task.GetAwaiter().GetResult();
        });

        try
        {
            stops.Add(Task.Run(() => registry.StopAllAsync(TimeSpan.FromMilliseconds(80))));
            await cancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // 第一轮停在取消回调中，保证第二轮也取得同一条未完成连接的快照。
            stops.Add(registry.StopAllAsync(TimeSpan.FromMilliseconds(80)));
            releaseCancellation.TrySetResult();
            await Task.WhenAll(stops).WaitAsync(TimeSpan.FromSeconds(5));

            stops.Add(registry.StopAllAsync(TimeSpan.FromMilliseconds(80)));
            ConnectionStopReport[] reports = await Task.WhenAll(stops).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, resource.DisposeCount);
            Assert.All(reports, report =>
            {
                Assert.Equal(1, report.Total);
                Assert.Equal(1, report.Unfinished);
                Assert.False(report.AllFinished);
            });
        }
        finally
        {
            releaseCancellation.TrySetResult();
            registration.Dispose();
            await Task.WhenAll(stops).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Stop_Does_Not_Force_Dispose_Resource_Already_Released_By_Cooperative_Handler()
    {
        ConnectionRegistry registry = new();
        CountingResource resource = new();
        ConnectionRegistration? registration = registry.TryRegister(resource);
        Assert.NotNull(registration);

        TaskCompletionSource cancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task handler = Task.Run(async () =>
        {
            try
            {
                await cancellationObserved.Task;
                resource.Dispose();
            }
            finally
            {
                registration.Dispose();
            }
        });
        using CancellationTokenRegistration cancellation = registration.Cancellation.Register(() =>
        {
            cancellationObserved.TrySetResult();
            // 取消阶段等到 handler 收尾，避免与短停机预算竞争。
            handler.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        });

        try
        {
            ConnectionStopReport report = await registry.StopAllAsync(TimeSpan.FromMilliseconds(80));

            Assert.Equal(1, report.Total);
            Assert.Equal(0, report.Unfinished);
            Assert.True(report.AllFinished);
            Assert.Equal(1, resource.DisposeCount);
            Assert.Equal(0, registry.Count);
        }
        finally
        {
            cancellationObserved.TrySetResult();
            await handler.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Completion_Is_Published_Before_Registry_Removes_Entry()
    {
        ConnectionRegistry registry = new();
        using ConnectionRegistration registration = registry.TryRegister(new NoopResource())!;
        Assert.NotNull(registration);
        // 测试专用反射持有真实完成通知；停在 Dispose 内部 Remove 返回后的接缝，
        // 不用概率调度撞击仅一条语句的窗口，也不新增生产测试回调。
        var field = typeof(ConnectionRegistration).GetField("_completion",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        TaskCompletionSource completion = Assert.IsType<TaskCompletionSource>(field.GetValue(registration));

        registry.Remove(registration.Id);
        Assert.Equal(0, registry.Count);
        Assert.True(completion.Task.IsCompletedSuccessfully,
            "表项已经不可见时，对应租约完成通知必须先发布。");
        ConnectionStopReport stop = await registry.StopAllAsync(TimeSpan.FromMilliseconds(80));
        Assert.True(stop.AllFinished);
    }

    [Fact(Timeout = 30_000)]
    public async Task Repeated_Stop_Does_Not_Report_Finished_While_Forced_Dispose_Is_Running()
    {
        ConnectionRegistry registry = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConnectionRegistration? registration = null;
        ActionResource resource = new(() =>
        {
            registration!.Dispose();
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        });
        registration = registry.TryRegister(resource);
        Assert.NotNull(registration);
        Task<ConnectionStopReport> first = Task.Run(() => registry.StopAllAsync(TimeSpan.FromMilliseconds(80)));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            ConnectionStopReport second = await registry.StopAllAsync(TimeSpan.FromMilliseconds(80));
            Assert.False(first.IsCompleted);
            Assert.Equal(1, second.Total);
            Assert.Equal(1, second.Unfinished);
            Assert.Equal(1, registry.Count);
        }
        finally
        {
            release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            registration.Dispose();
        }

        Assert.Equal(0, registry.Count);
        Assert.True((await registry.StopAllAsync(TimeSpan.FromMilliseconds(80))).AllFinished);
    }

    private sealed class ActionResource(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    private sealed class CountingResource : IDisposable
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
        }
    }

    private sealed class NoopResource : IDisposable
    {
        public void Dispose()
        {
        }
    }

    /// <summary>
    /// 模拟「卡在不可中断读里」的连接：只有 Dispose 才能让它结束。
    /// </summary>
    private sealed class StubbornResource : IDisposable
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task NeverEndsUntilDisposed => _release.Task;

        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
            _release.TrySetResult();
        }
    }
}
