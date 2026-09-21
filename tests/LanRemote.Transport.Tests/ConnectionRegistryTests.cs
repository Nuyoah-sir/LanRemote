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
