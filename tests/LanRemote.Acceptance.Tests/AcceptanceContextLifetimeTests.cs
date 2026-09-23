using Microsoft.Extensions.Logging;

namespace LanRemote.Acceptance.Tests;

[Collection(AcceptanceStageFiveCollection.Name)]
public sealed class AcceptanceContextLifetimeTests
{
    [Fact(Timeout = 20_000)]
    public async Task Stop_Cancels_Work_But_Drain_Waits_For_Explicit_Lease_Return()
    {
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(12));
        await using WorkScope scope = new();
        AcceptanceContext.WorkLease lease = Assert.IsType<AcceptanceContext.WorkLease>(scope.TryAcquire());
        TaskCompletionSource<CancellationToken> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> work = scope.Observe(lease.RunAsync(async token =>
        {
            using CancellationTokenRegistration registration = token.Register(() => cancelled.TrySetResult());
            started.TrySetResult(token);
            await release.Task.WaitAsync(guard.Token);
            return token.IsCancellationRequested;
        }));
        try
        {
            CancellationToken token = await started.Task.WaitAsync(guard.Token);
            Assert.True(token.CanBeCanceled);
            Assert.False(token.IsCancellationRequested);
            Assert.Null(scope.TryAcquire());
            Task draining = scope.Observe(scope.Context.StopWorkAsync());
            await cancelled.Task.WaitAsync(guard.Token);
            Assert.True(scope.Context.IsStopping);
            Assert.True(token.IsCancellationRequested);
            Assert.True(lease.IsStopRequested);
            Assert.Null(scope.TryAcquire());
            Assert.False(work.IsCompleted);
            Assert.False(draining.IsCompleted);
            Assert.False(lease.Completion.IsCompleted);

            release.SetResult();
            Assert.True(await work.WaitAsync(guard.Token));
            // 工作函数退出也不等于归还拥有权，Stop 必须继续等待 DisposeAsync。
            Assert.False(draining.IsCompleted);
            Assert.False(lease.Completion.IsCompleted);
            await lease.DisposeAsync().AsTask().WaitAsync(guard.Token);
            await draining.WaitAsync(guard.Token);
            Assert.True(lease.Completion.IsCompletedSuccessfully);
        }
        finally { release.TrySetResult(); }
    }

    [Fact(Timeout = 20_000)]
    public async Task Lease_Capacity_Recovers_After_Return_And_Context_Dispose_Is_Shared()
    {
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(12));
        await using WorkScope scope = new();
        AcceptanceContext.WorkLease first = Assert.IsType<AcceptanceContext.WorkLease>(scope.TryAcquire());
        Assert.Null(scope.TryAcquire());
        Assert.False(scope.Context.IsStopping);
        await first.DisposeAsync().AsTask().WaitAsync(guard.Token);
        await first.DisposeAsync().AsTask().WaitAsync(guard.Token);
        Assert.True(first.Completion.IsCompletedSuccessfully);

        AcceptanceContext.WorkLease second = Assert.IsType<AcceptanceContext.WorkLease>(scope.TryAcquire());
        Assert.NotSame(first, second);
        Assert.Null(scope.TryAcquire());
        Task firstDispose = scope.Observe(scope.Context.DisposeAsync().AsTask());
        Task secondDispose = scope.Observe(scope.Context.DisposeAsync().AsTask());
        Assert.Same(firstDispose, secondDispose);
        Assert.True(scope.Context.IsStopping);
        Assert.Null(scope.TryAcquire());
        Assert.False(firstDispose.IsCompleted);
        await second.RequestStop().WaitAsync(guard.Token);
        Assert.False(firstDispose.IsCompleted);
        await second.DisposeAsync().AsTask().WaitAsync(guard.Token);
        await firstDispose.WaitAsync(guard.Token);
        await secondDispose.WaitAsync(guard.Token);
        Assert.True(second.Completion.IsCompletedSuccessfully);
        Assert.Null(scope.TryAcquire());
    }

    [Fact(Timeout = 20_000)]
    public async Task Stop_And_Acquire_Race_Cannot_Lose_A_Lease_Or_Reopen_Work()
    {
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(12));
        for (int iteration = 0; iteration < 16; iteration++)
        {
            await using WorkScope scope = new();
            TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource acquireReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource stopReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<AcceptanceContext.WorkLease?> acquire = scope.Observe(Task.Run(async () =>
            {
                acquireReady.TrySetResult();
                await start.Task.WaitAsync(guard.Token);
                return scope.TryAcquire();
            }));
            Task stop = scope.Observe(Task.Run(async () =>
            {
                stopReady.TrySetResult();
                await start.Task.WaitAsync(guard.Token);
                Task draining = scope.Context.StopWorkAsync();
                stopped.TrySetResult();
                await draining;
            }));
            try
            {
                await Task.WhenAll(acquireReady.Task, stopReady.Task).WaitAsync(guard.Token);
                start.SetResult();
                AcceptanceContext.WorkLease? acquired = await acquire.WaitAsync(guard.Token);
                await stopped.Task.WaitAsync(guard.Token);
                Assert.True(scope.Context.IsStopping);
                Assert.Null(scope.TryAcquire());
                if (acquired is not null)
                {
                    Assert.True(acquired.IsStopRequested);
                    Assert.False(stop.IsCompleted);
                    Assert.False(acquired.Completion.IsCompleted);
                    await acquired.DisposeAsync().AsTask().WaitAsync(guard.Token);
                }
                await stop.WaitAsync(guard.Token);
                Assert.Null(scope.TryAcquire());
                await scope.Context.StopWorkAsync().WaitAsync(guard.Token);
            }
            finally { start.TrySetResult(); }
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task Stop_Request_Does_Not_Run_Blocking_Callback_Inline_And_Dispose_Joins_It()
    {
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(10));
        await using WorkScope scope = new();
        AcceptanceContext.WorkLease lease = Assert.IsType<AcceptanceContext.WorkLease>(scope.TryAcquire());
        using ManualResetEventSlim releaseCallback = new(false);
        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource returnWork = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> work = scope.Observe(lease.RunAsync(async token =>
        {
            using CancellationTokenRegistration registration = token.Register(() =>
            {
                entered.TrySetResult();
                releaseCallback.Wait(guard.Token);
            });
            ready.TrySetResult();
            await returnWork.Task.WaitAsync(guard.Token);
            return token.IsCancellationRequested;
        }));
        try
        {
            await ready.Task.WaitAsync(guard.Token);
            Task cancellation = scope.Observe(lease.RequestStop());
            await entered.Task.WaitAsync(guard.Token);
            Assert.False(cancellation.IsCompleted);
            Assert.Same(cancellation, lease.RequestStop());
            Task disposal = scope.Observe(lease.DisposeAsync().AsTask());
            Assert.False(disposal.IsCompleted);
            Assert.False(lease.Completion.IsCompleted);
            releaseCallback.Set();
            await cancellation.WaitAsync(guard.Token);
            await disposal.WaitAsync(guard.Token);
            returnWork.SetResult();
            Assert.True(await work.WaitAsync(guard.Token));
            await lease.DisposeAsync().AsTask().WaitAsync(guard.Token);
            Assert.True(lease.Completion.IsCompletedSuccessfully);
        }
        finally
        {
            releaseCallback.Set();
            returnWork.TrySetResult();
            // 先 join 才能释放回调所使用的 ManualResetEventSlim。
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(3));
            await work.WaitAsync(cleanup.Token);
        }
    }

    private sealed class WorkScope : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly List<AcceptanceContext.WorkLease> _leases = new();
        private readonly List<Task> _tasks = new();

        // 构造只装配对象；从不 InitializeAsync、启动 discovery、调用 vault 或访问密钥。
        internal AcceptanceContext Context { get; } = new(LogLevel.None);

        internal AcceptanceContext.WorkLease? TryAcquire()
        {
            lock (_gate)
            {
                AcceptanceContext.WorkLease? lease = Context.TryAcquireWork();
                if (lease is not null) { _leases.Add(lease); }
                return lease;
            }
        }

        internal Task Observe(Task task) { _tasks.Add(task); return task; }
        internal Task<T> Observe<T>(Task<T> task) { _tasks.Add(task); return task; }

        public async ValueTask DisposeAsync()
        {
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
            try
            {
                Task drain;
                AcceptanceContext.WorkLease[] leases;
                lock (_gate)
                {
                    // 先封入口再取 lease 快照，竞速任务之后只能取得 null。
                    drain = Context.StopWorkAsync();
                    leases = _leases.ToArray();
                }
                try
                {
                    await Task.WhenAll(leases.Select(lease => lease.DisposeAsync().AsTask())).WaitAsync(cleanup.Token);
                }
                finally
                {
                    await Task.WhenAll(_tasks.Append(drain)).WaitAsync(cleanup.Token);
                }
            }
            finally
            {
                await Context.DisposeAsync().AsTask().WaitAsync(cleanup.Token);
            }
            Assert.Null(Context.Identity);
            Assert.Null(Context.Certificate);
            Assert.Null(Context.Discovery);
        }
    }
}
