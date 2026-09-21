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

    private static DiscoveredDevice Device(
        Guid id,
        string address,
        DateTimeOffset lastSeen,
        string? fingerprint = null) =>
        new(
            id,
            "AAAA-BBBB",
            "REMOTE",
            IPAddress.Parse(address),
            DiscoveryConstants.ExpectedTransportPort,
            fingerprint ?? new string('A', 64),
            lastSeen,
            new HashSet<string> { "view" });

    /// <summary>造一个「64 字符、内容为 c」的指纹；只用来区分不同观察，不要求是合法 hex。</summary>
    private static string Fingerprint(char c) => new(c, 64);

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

    // ─────────────────────────────────────────────────────────────────────
    // 身份冲突语义（ADR-027 + M4 阶段 0 定案）。三条 ADR 验证用例 + 边界。
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// ADR-027 验证 ①：同 deviceId + 同指纹 + 不同地址 = 良性多网卡，不判冲突。
    /// </summary>
    [Fact]
    public void SameFingerprint_DifferentAddress_IsNotAConflict()
    {
        DiscoveryDeviceCache cache = new();
        Guid id = Guid.NewGuid();

        cache.Upsert(Device(id, "192.168.1.50", T0, Fingerprint('X')), T0);
        cache.Upsert(Device(id, "192.168.1.77", T0.AddSeconds(1), Fingerprint('X')), T0.AddSeconds(1));

        Assert.False(cache.IsIdentityConflicted(id, T0.AddSeconds(1)));
        Assert.Equal("192.168.1.77", cache.Snapshot()[0].Address.ToString());
    }

    /// <summary>
    /// 指纹比较必须忽略大小写（hex 大小写不改变字节内容；缓存对直接 API 调用方稳健）。
    /// </summary>
    [Fact]
    public void SameFingerprint_DifferentHexCase_IsNotAConflict()
    {
        DiscoveryDeviceCache cache = new();
        Guid id = Guid.NewGuid();

        cache.Upsert(Device(id, "192.168.1.50", T0, Fingerprint('a')), T0);
        cache.Upsert(Device(id, "192.168.1.51", T0.AddSeconds(1), Fingerprint('A')), T0.AddSeconds(1));

        Assert.False(cache.IsIdentityConflicted(id, T0.AddSeconds(1)));
    }

    /// <summary>
    /// ADR-027 验证 ②：同 deviceId 不同指纹 → 标记冲突，且<b>不覆盖主条目</b>（不二选一）。
    /// </summary>
    [Fact]
    public void DifferentFingerprint_MarksConflict_AndKeepsTheFirstEntry()
    {
        DiscoveryDeviceCache cache = new();
        Guid id = Guid.NewGuid();

        cache.Upsert(Device(id, "192.168.1.50", T0, Fingerprint('X')), T0);
        bool isNew = cache.Upsert(Device(id, "192.168.1.77", T0.AddSeconds(1), Fingerprint('Y')), T0.AddSeconds(1));

        // 冲突观察不是「新设备」，也不得改写主条目：地址与指纹保持第一次的事实。
        Assert.False(isNew);
        Assert.Equal(1, cache.Count);
        Assert.True(cache.IsIdentityConflicted(id, T0.AddSeconds(1)));
        Assert.Equal("192.168.1.50", cache.Snapshot()[0].Address.ToString());
        Assert.Equal(Fingerprint('X'), cache.Snapshot()[0].CertificateSha256);
    }

    /// <summary>
    /// ADR-027 验证 ③：冲突指纹的观察全部过期后 → 冲突可清除（主条目仍在时）。
    /// </summary>
    [Fact]
    public void Conflict_Clears_AfterConflictingObservation_Expires()
    {
        DiscoveryDeviceCache cache = new();
        Guid id = Guid.NewGuid();

        // 时间轴（TTL = 7 s）：X@T0；Y@T0+1（冲突）；X@T0+2（刷新主条目，让它活过下面检查点）。
        cache.Upsert(Device(id, "192.168.1.50", T0, Fingerprint('X')), T0);
        cache.Upsert(Device(id, "192.168.1.77", T0.AddSeconds(1), Fingerprint('Y')), T0.AddSeconds(1));
        cache.Upsert(Device(id, "192.168.1.50", T0.AddSeconds(2), Fingerprint('X')), T0.AddSeconds(2));

        // T0+3：Y 未过期（过期点 T0+8）→ 冲突仍然在。
        Assert.True(cache.IsIdentityConflicted(id, T0.AddSeconds(3)));

        // T0+8：cutoff = T0+1 → Y@T0+1 过期（<= cutoff）；主条目 X@T0+2 仍在（> cutoff）。
        Assert.False(cache.IsIdentityConflicted(id, T0.AddSeconds(8)));
        Assert.Equal(1, cache.Count);
    }

    /// <summary>
    /// 对照：冲突指纹被持续刷新时，冲突不会自行消失。
    /// </summary>
    [Fact]
    public void Conflict_Persists_WhileConflictingObservation_IsRefreshed()
    {
        DiscoveryDeviceCache cache = new();
        Guid id = Guid.NewGuid();

        cache.Upsert(Device(id, "192.168.1.50", T0, Fingerprint('X')), T0);

        // Y 每 2 秒广播一次、持续 20 秒：全程冲突。
        for (int i = 1; i <= 10; i++)
        {
            DateTimeOffset at = T0.AddSeconds(i * 2);
            cache.Upsert(Device(id, "192.168.1.77", at, Fingerprint('Y')), at);
            Assert.True(cache.IsIdentityConflicted(id, at), $"第 {i} 次观察后冲突应当仍在。");
        }
    }

    /// <summary>
    /// 显式边界（ADR-027 定案第 10 条）：主条目整体过期 → 整条移除（含冲突记录）；
    /// 之后的观察从头开始（TTL 语义，v1 不做跨 TTL 的身份记忆）。
    /// </summary>
    [Fact]
    public void MainEntryExpiry_RemovesWholeEntry_AndLaterObservationStartsFresh()
    {
        DiscoveryDeviceCache cache = new();
        Guid id = Guid.NewGuid();

        cache.Upsert(Device(id, "192.168.1.50", T0, Fingerprint('X')), T0);
        cache.Upsert(Device(id, "192.168.1.77", T0.AddSeconds(1), Fingerprint('Y')), T0.AddSeconds(1));

        // X 不再出现；T0+8 时 X@T0 与 Y@T0+1 双双过期（cutoff = T0+1）。
        int removed = cache.RemoveExpired(T0.AddSeconds(8), out IReadOnlyList<DiscoveredDevice> expired);

        Assert.Equal(1, removed);
        Assert.Single(expired);
        Assert.Equal(0, cache.Count);
        Assert.False(cache.IsIdentityConflicted(id, T0.AddSeconds(8)));

        // 之后 Y 的观察是全新条目：无冲突（边界行为如实测出，不假装它不存在）。
        bool isNew = cache.Upsert(Device(id, "192.168.1.77", T0.AddSeconds(8.5), Fingerprint('Y')), T0.AddSeconds(8.5));

        Assert.True(isNew);
        Assert.False(cache.IsIdentityConflicted(id, T0.AddSeconds(8.5)));
    }

    /// <summary>
    /// 冲突记录有界：攻击者推大量伪造指纹也不得让内存无界增长。
    /// </summary>
    [Fact]
    public void ConflictingFingerprints_AreCapped_AndEntryStaysConflicted()
    {
        DiscoveryDeviceCache cache = new();
        Guid id = Guid.NewGuid();

        cache.Upsert(Device(id, "192.168.1.50", T0, Fingerprint('X')), T0);

        // 同一时刻推 12 个不同指纹（TTL 内无人过期，必然触发淘汰路径）。
        for (int i = 0; i < 12; i++)
        {
            cache.Upsert(Device(id, "192.168.1.77", T0.AddSeconds(1), Fingerprint((char)('A' + i))), T0.AddSeconds(1));
        }

        Assert.True(cache.IsIdentityConflicted(id, T0.AddSeconds(1)));
        Assert.Equal(
            DiscoveryDeviceCache.MaxConflictingFingerprintsPerDevice,
            cache.ConflictingFingerprintCount(id, T0.AddSeconds(1)));
    }

    /// <summary>不存在的设备不是「冲突设备」——冲突查询不得凭空造出状态。</summary>
    [Fact]
    public void UnknownDevice_IsNotConflicted()
    {
        DiscoveryDeviceCache cache = new();

        Assert.False(cache.IsIdentityConflicted(Guid.NewGuid(), T0));
        Assert.Equal(0, cache.ConflictingFingerprintCount(Guid.NewGuid(), T0));
    }
}
