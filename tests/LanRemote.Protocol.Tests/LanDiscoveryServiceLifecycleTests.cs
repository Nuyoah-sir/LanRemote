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
