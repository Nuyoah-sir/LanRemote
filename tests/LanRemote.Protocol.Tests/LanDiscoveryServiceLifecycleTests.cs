using System.Threading.Tasks;
using LanRemote.Discovery;
using LanRemote.Discovery.Networking;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LanRemote.Protocol.Tests;

/// <summary>
/// <see cref="LanDiscoveryService"/> 的生命周期。
/// </summary>
/// <remarks>
/// 只覆盖「不需要真实网卡」的路径：
/// 本机唯一的活跃网卡是 172.100.x.x（公网段，非 RFC1918），
/// 因此无法在本机做真实的组播/广播收发测试——这一点已记入 HANDOFF。
/// </remarks>
public sealed class LanDiscoveryServiceLifecycleTests
{
    private static LanDiscoveryService CreateService(params (string Address, string Mask)[] bindings)
    {
        INetworkBindingProvider provider = new FakeBindingProvider(bindings);

        return new LanDiscoveryService(
            provider,
            new DiscoveryRuntimeState(),
            NullLogger<LanDiscoveryService>.Instance);
    }

    private static LanDiscoveryService CreateService(Func<CancellationToken, Task> additionalLoop) =>
        new(
            new FakeBindingProvider(),
            new DiscoveryRuntimeState(),
            NullLogger<LanDiscoveryService>.Instance,
            additionalLoop);

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task StartAsync_WithNoEligibleBindings_DoesNotThrow()
    {
        LanDiscoveryService service = CreateService();

        await service.StartAsync();

        Assert.Empty(service.CurrentBindings);

        await service.StopAsync();
    }

    [Fact]
    public async Task StartAsync_IsIdempotent()
    {
        LanDiscoveryService service = CreateService();

        await service.StartAsync();
        await service.StartAsync();

        await service.StopAsync();
    }

    [Fact]
    public async Task StopAsync_WithoutStart_DoesNotThrow()
    {
        LanDiscoveryService service = CreateService();

        await service.StopAsync();
        await service.StopAsync();
        await service.JoinAndDisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync());
    }

    [Fact]
    public async Task StopAsync_IsIdempotent()
    {
        LanDiscoveryService service = CreateService();

        await service.StartAsync();
        await service.StopAsync();
        await service.StopAsync();
    }

    [Fact]
    public async Task StopAsync_TimeoutKeepsOriginalLoopAliveUntilStrictJoin()
    {
        TaskCompletionSource<bool> entered = NewSignal();
        TaskCompletionSource<bool> release = NewSignal();
        CancellationToken loopToken = default;
        LanDiscoveryService service = CreateService(async token =>
        {
            loopToken = token;
            entered.TrySetResult(true);
            await release.Task;
        });

        await service.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task watcher = Task.Run(async () =>
        {
            await foreach (var _ in service.WatchAsync(CancellationToken.None))
            {
            }
        });

        Task boundedStop = service.StopAsync();
        Task join = service.JoinAndDisposeAsync();
        try
        {
            Assert.Same(join, service.JoinAndDisposeAsync());
            Assert.False(join.IsCompleted);
            await boundedStop.WaitAsync(TimeSpan.FromSeconds(15));
            await watcher.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(loopToken.IsCancellationRequested);
            Assert.True(loopToken.WaitHandle.WaitOne(0));
            Assert.False(join.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync());
        }
        finally
        {
            release.TrySetResult(true);
            await join.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Throws<ObjectDisposedException>(() => { _ = loopToken.WaitHandle; });
    }

    [Fact]
    public async Task JoinAndDisposeAsync_WaitsForInFlightInitialProbe()
    {
        TaskCompletionSource<bool> probeEntered = NewSignal();
        TaskCompletionSource<bool> releaseProbe = NewSignal();
        CancellationToken probeToken = default;
        LanDiscoveryService service = new(
            new FakeBindingProvider(),
            new DiscoveryRuntimeState(),
            NullLogger<LanDiscoveryService>.Instance,
            _ => Task.CompletedTask,
            async token =>
            {
                probeToken = token;
                probeEntered.TrySetResult(true);
                await releaseProbe.Task;
            });

        Task start = service.StartAsync();
        await probeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task join = service.JoinAndDisposeAsync();
        try
        {
            Assert.False(join.IsCompleted);
            Assert.True(probeToken.IsCancellationRequested);
            Assert.True(probeToken.WaitHandle.WaitOne(0));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync());
        }
        finally
        {
            releaseProbe.TrySetResult(true);
            await start.WaitAsync(TimeSpan.FromSeconds(5));
            await join.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Throws<ObjectDisposedException>(() => { _ = probeToken.WaitHandle; });
    }

    [Fact]
    public async Task StopAsync_CallerCancellationDoesNotAbandonCancellationCallbackOrLoop()
    {
        TaskCompletionSource<bool> entered = NewSignal();
        TaskCompletionSource<bool> callbackEntered = NewSignal();
        TaskCompletionSource<bool> releaseCallback = NewSignal();
        TaskCompletionSource<bool> releaseLoop = NewSignal();
        CancellationToken loopToken = default;
        LanDiscoveryService service = CreateService(async token =>
        {
            loopToken = token;
            using CancellationTokenRegistration registration = token.Register(() =>
            {
                callbackEntered.TrySetResult(true);
                releaseCallback.Task.GetAwaiter().GetResult();
            });
            entered.TrySetResult(true);
            await releaseLoop.Task;
        });

        await service.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using CancellationTokenSource callerCancellation = new();
        Task boundedStop = Task.Run(() => service.StopAsync(callerCancellation.Token));
        try
        {
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task join = service.JoinAndDisposeAsync();
            callerCancellation.Cancel();
            await boundedStop.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(loopToken.WaitHandle.WaitOne(0));
            Assert.False(join.IsCompleted);

            releaseLoop.TrySetResult(true);
            Assert.False(join.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync());
        }
        finally
        {
            releaseCallback.TrySetResult(true);
            releaseLoop.TrySetResult(true);
            await service.JoinAndDisposeAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Throws<ObjectDisposedException>(() => { _ = loopToken.WaitHandle; });
    }

    [Fact]
    public async Task JoinAndDisposeAsync_CollectsCallbackAndLoopFailuresBeforeDisposal()
    {
        TaskCompletionSource<bool> entered = NewSignal();
        TaskCompletionSource<bool> release = NewSignal();
        CancellationToken loopToken = default;
        LanDiscoveryService service = CreateService(async token =>
        {
            loopToken = token;
            using CancellationTokenRegistration registration = token.Register(
                () => throw new InvalidOperationException("取消回调失败"));
            entered.TrySetResult(true);
            await release.Task;
            throw new ApplicationException("后台循环失败");
        });

        await service.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using CancellationTokenSource callerCancellation = new();
        Task boundedStop = service.StopAsync(callerCancellation.Token);
        Task join = service.JoinAndDisposeAsync();
        try
        {
            callerCancellation.Cancel();
            await boundedStop.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(join.IsCompleted);
            Assert.True(loopToken.WaitHandle.WaitOne(0));
        }
        finally
        {
            release.TrySetResult(true);
        }

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(
            () => join.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains(failure.InnerExceptions,
            ex => ex is InvalidOperationException { Message: "取消回调失败" });
        Assert.Contains(failure.InnerExceptions,
            ex => ex is ApplicationException { Message: "后台循环失败" });
        Assert.Throws<ObjectDisposedException>(() => { _ = loopToken.WaitHandle; });
    }

    [Fact]
    public async Task ProbeAsync_WithNoBindings_DoesNotThrow()
    {
        LanDiscoveryService service = CreateService();

        await service.StartAsync();

        // 扫描与「允许被发现」无关；即使没有合格网卡也只是空操作。
        await service.ProbeAsync();

        await service.StopAsync();
    }

    [Fact]
    public async Task WatchAsync_CompletesAfterStop()
    {
        LanDiscoveryService service = CreateService();

        await service.StartAsync();
        await service.StopAsync();

        // channel 被 complete 之后，消费者应当自然结束，而不是永久挂起。
        int count = 0;
        await foreach (var _ in service.WatchAsync(CancellationToken.None))
        {
            count++;
        }

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task DroppedPackets_AreCountedForUntrustedSources()
    {
        // 没有合格绑定时，来源过滤会拒绝一切；这里验证计数器确实工作。
        LanDiscoveryService service = CreateService();

        await service.StartAsync();

        Assert.Equal(0, service.DroppedPacketCount);

        await service.StopAsync();
    }
}
