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

        bool finished = await registry.StopAllAsync(TimeSpan.FromSeconds(3));

        Assert.True(finished, "强制释放 socket 之后 handler 必须结束。");
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

        bool finished = await registry.StopAllAsync(TimeSpan.FromSeconds(3));

        Assert.True(finished);
        Assert.True(observed.Task.IsCompleted);
        Assert.Equal(0, registry.Count);
        await handler;
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
