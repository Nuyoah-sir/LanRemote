using LanRemote.Core.Models;

namespace LanRemote.Discovery;

/// <summary>
/// 发现到的设备缓存。
/// </summary>
/// <remarks>
/// <para>Key 是 <see cref="DiscoveredDevice.DeviceId"/>：一台物理设备可能有多个 NIC，
/// 但 UI 里只能出现一条。同一 DeviceId 换了 IP 时 v1 采用最新的有效 endpoint。</para>
/// <para><b>必须有上限</b>：局域网报文是不可信输入，不能让伪造的大量 DeviceId 把内存塞满。</para>
/// <para>时间一律由调用方以 <c>now</c> 传入，保证单元测试无需真的 sleep。</para>
/// </remarks>
public sealed class DiscoveryDeviceCache
{
    private readonly Dictionary<Guid, DiscoveredDevice> _devices = new();
    private readonly object _gate = new();

    private readonly int _maxDevices;
    private readonly TimeSpan _ttl;

    /// <summary>构造缓存。</summary>
    /// <param name="maxDevices">最大条目数。</param>
    /// <param name="ttl">过期时间；为空则用 <see cref="DiscoveryConstants.DeviceCacheTtlMs"/>。</param>
    public DiscoveryDeviceCache(
        int maxDevices = DiscoveryConstants.MaxCachedDevices,
        TimeSpan? ttl = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDevices);

        _maxDevices = maxDevices;
        _ttl = ttl ?? TimeSpan.FromMilliseconds(DiscoveryConstants.DeviceCacheTtlMs);
    }

    /// <summary>当前条目数。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _devices.Count;
            }
        }
    }

    /// <summary>取全部设备的快照。</summary>
    /// <returns>设备列表。</returns>
    public IReadOnlyList<DiscoveredDevice> Snapshot()
    {
        lock (_gate)
        {
            return _devices.Values.ToArray();
        }
    }

    /// <summary>是否存在指定设备。</summary>
    /// <param name="deviceId">设备标识。</param>
    /// <returns>是否存在且未过期。</returns>
    public bool Contains(Guid deviceId)
    {
        lock (_gate)
        {
            return _devices.ContainsKey(deviceId);
        }
    }

    /// <summary>
    /// 插入或更新。
    /// </summary>
    /// <param name="device">设备条目（LastSeen 已由调用方设置）。</param>
    /// <param name="now">当前时间，用于过期判定。</param>
    /// <returns>是否为<b>新</b>设备（首次进入缓存）。</returns>
    public bool Upsert(DiscoveredDevice device, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(device);

        lock (_gate)
        {
            bool isNew = !_devices.ContainsKey(device.DeviceId);

            if (isNew && _devices.Count >= _maxDevices)
            {
                // 先清过期的；仍然满则淘汰最久未见的那台。
                RemoveExpiredCore(now, out _);

                if (_devices.Count >= _maxDevices)
                {
                    EvictOldestCore();
                }
            }

            _devices[device.DeviceId] = device;
            return isNew;
        }
    }

    /// <summary>移除所有过期条目。</summary>
    /// <param name="now">当前时间。</param>
    /// <param name="removed">被移除的条目。</param>
    /// <returns>移除数量。</returns>
    public int RemoveExpired(DateTimeOffset now, out IReadOnlyList<DiscoveredDevice> removed)
    {
        lock (_gate)
        {
            return RemoveExpiredCore(now, out removed);
        }
    }

    private int RemoveExpiredCore(DateTimeOffset now, out IReadOnlyList<DiscoveredDevice> removed)
    {
        DateTimeOffset cutoff = now - _ttl;

        List<DiscoveredDevice> expired = new();
        foreach (KeyValuePair<Guid, DiscoveredDevice> pair in _devices)
        {
            if (pair.Value.LastSeen <= cutoff)
            {
                expired.Add(pair.Value);
            }
        }

        foreach (DiscoveredDevice device in expired)
        {
            _devices.Remove(device.DeviceId);
        }

        removed = expired;
        return expired.Count;
    }

    private void EvictOldestCore()
    {
        Guid? oldestId = null;
        DateTimeOffset oldest = DateTimeOffset.MaxValue;

        foreach (KeyValuePair<Guid, DiscoveredDevice> pair in _devices)
        {
            if (pair.Value.LastSeen < oldest)
            {
                oldest = pair.Value.LastSeen;
                oldestId = pair.Key;
            }
        }

        if (oldestId is not null)
        {
            _devices.Remove(oldestId.Value);
        }
    }
}
