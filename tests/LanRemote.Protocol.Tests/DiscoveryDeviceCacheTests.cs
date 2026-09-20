using System.Net;
using LanRemote.Core.Models;
using LanRemote.Discovery;
using Xunit;

namespace LanRemote.Protocol.Tests;

/// <summary>
/// 设备缓存：upsert 语义、TTL 与容量上限。
/// </summary>
/// <remarks>时间一律由测试以 <c>now</c> 传入，不做真的 sleep。</remarks>
public sealed class DiscoveryDeviceCacheTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static DiscoveredDevice Device(Guid id, string address, DateTimeOffset lastSeen) =>
        new(
            id,
            "AAAA-BBBB",
            "REMOTE",
            IPAddress.Parse(address),
            DiscoveryConstants.ExpectedTransportPort,
            new string('A', 64),
            lastSeen,
            new HashSet<string> { "view" });

    [Fact]
    public void FirstAnnounce_AddsDevice()
    {
        DiscoveryDeviceCache cache = new();
        Guid id = Guid.NewGuid();

        bool isNew = cache.Upsert(Device(id, "192.168.1.50", T0), T0);

        Assert.True(isNew);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void SameDeviceId_UpdatesInsteadOfAdding()
    {
        DiscoveryDeviceCache cache = new();
        Guid id = Guid.NewGuid();

        cache.Upsert(Device(id, "192.168.1.50", T0), T0);
        bool isNew = cache.Upsert(Device(id, "192.168.1.50", T0.AddSeconds(2)), T0.AddSeconds(2));

        Assert.False(isNew);
        Assert.Equal(1, cache.Count);
        Assert.Equal(T0.AddSeconds(2), cache.Snapshot()[0].LastSeen);
    }

    [Fact]
    public void SameDeviceIdWithNewAddress_KeepsSingleEntry()
    {
        DiscoveryDeviceCache cache = new();
        Guid id = Guid.NewGuid();

        cache.Upsert(Device(id, "192.168.1.50", T0), T0);
        cache.Upsert(Device(id, "192.168.1.77", T0.AddSeconds(1)), T0.AddSeconds(1));

        // v1 采用最新的有效 endpoint，不生成第二条 UI 设备。
        Assert.Equal(1, cache.Count);
        Assert.Equal("192.168.1.77", cache.Snapshot()[0].Address.ToString());
    }

    [Fact]
    public void DifferentDeviceIds_AreSeparateEntries()
    {
        DiscoveryDeviceCache cache = new();

        cache.Upsert(Device(Guid.NewGuid(), "192.168.1.50", T0), T0);
        cache.Upsert(Device(Guid.NewGuid(), "192.168.1.51", T0), T0);

        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void ExpiredDevices_AreRemoved()
    {
        DiscoveryDeviceCache cache = new();
        Guid id = Guid.NewGuid();
        cache.Upsert(Device(id, "192.168.1.50", T0), T0);

        // 缓存 TTL 7 秒：等于 8 秒后应当被清理。
        int removed = cache.RemoveExpired(T0.AddSeconds(8), out IReadOnlyList<DiscoveredDevice> expired);

        Assert.Equal(1, removed);
        Assert.Single(expired);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void NonExpiredDevices_AreKept()
    {
        DiscoveryDeviceCache cache = new();
        cache.Upsert(Device(Guid.NewGuid(), "192.168.1.50", T0), T0);

        int removed = cache.RemoveExpired(T0.AddSeconds(5), out _);

        Assert.Equal(0, removed);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void CapacityIsBounded_AndDoesNotGrowForever()
    {
        DiscoveryDeviceCache cache = new(maxDevices: 8);

        for (int i = 0; i < 100; i++)
        {
            cache.Upsert(
                Device(Guid.NewGuid(), $"192.168.1.{i % 254 + 1}", T0.AddSeconds(i)),
                T0.AddSeconds(i));
        }

        Assert.True(cache.Count <= 8, $"缓存条目 {cache.Count} 超过上限 8。");
    }

    [Fact]
    public void AtCapacity_ExpiredEntriesArePurgedFirst()
    {
        DiscoveryDeviceCache cache = new(maxDevices: 2);

        Guid old = Guid.NewGuid();
        Guid kept = Guid.NewGuid();

        cache.Upsert(Device(old, "192.168.1.50", T0), T0);

        // kept 必须仍然在线：相对下面的插入时刻不能过期（TTL 7 秒）。
        DateTimeOffset keptTime = T0.AddSeconds(9);
        cache.Upsert(Device(kept, "192.168.1.51", keptTime), keptTime);

        // 满了；新设备进来时先清掉已过期的 old，而不是淘汰 still-online 的 kept。
        DateTimeOffset later = T0.AddSeconds(10);
        cache.Upsert(Device(Guid.NewGuid(), "192.168.1.52", later), later);

        Assert.Equal(2, cache.Count);
        Assert.Contains(cache.Snapshot(), d => d.DeviceId == kept);
        Assert.DoesNotContain(cache.Snapshot(), d => d.DeviceId == old);
    }

    [Fact]
    public void AtCapacity_WithNoExpired_EvictsOldest()
    {
        DiscoveryDeviceCache cache = new(maxDevices: 2);

        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        Guid third = Guid.NewGuid();

        DateTimeOffset t = T0;
        cache.Upsert(Device(first, "192.168.1.50", t), t);
        cache.Upsert(Device(second, "192.168.1.51", t.AddMilliseconds(500)), t.AddMilliseconds(500));
        cache.Upsert(Device(third, "192.168.1.52", t.AddSeconds(1)), t.AddSeconds(1));

        Assert.Equal(2, cache.Count);
        Assert.DoesNotContain(cache.Snapshot(), d => d.DeviceId == first);
    }

    [Fact]
    public void DefaultCapacityUsesProtocolConstant()
    {
        DiscoveryDeviceCache cache = new();

        Assert.Equal(DiscoveryConstants.MaxCachedDevices, 256);
        Assert.Equal(0, cache.Count);
    }
}
