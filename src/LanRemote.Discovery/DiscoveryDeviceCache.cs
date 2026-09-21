using LanRemote.Core.Models;

namespace LanRemote.Discovery;

/// <summary>
/// 发现到的设备缓存。
/// </summary>
/// <remarks>
/// <para>Key 是 <see cref="DiscoveredDevice.DeviceId"/>：一台物理设备可能有多个 NIC，
/// 但 UI 里只能出现一条。同一 DeviceId 换了 IP 时 v1 采用最新的有效 endpoint。</para>
/// <para><b>身份冲突（ADR-027；M4 阶段 0 落地）</b>：同一 DeviceId 在 TTL 窗口内出现
/// <b>不同</b>的 <see cref="DiscoveredDevice.CertificateSha256"/> 时，新的观察只被记录为
/// <b>冲突观察</b>、<b>不覆盖主条目</b>（即「不二选一地挑一个指纹」）；
/// 查询方以 <see cref="IsIdentityConflicted"/> 判定是否禁用该设备的连接入口。</para>
/// <para><b>良性场景不得误杀</b>：同 deviceId + 同指纹 + 不同 IP/端口是同一台机器的多网卡正常广播，
/// 照常 last-write-wins 更新。指纹比较不区分大小写（生产路径已由 evaluator
/// 规范化为 64 字符大写十六进制）。</para>
/// <para><b>必须有上限</b>：局域网报文是不可信输入，不能让伪造的大量 DeviceId 或
/// 大量伪造指纹把内存塞满——每条目的冲突指纹记录同样有界。</para>
/// <para>时间一律由调用方以 <c>now</c> 传入，保证单元测试无需真的 sleep。</para>
/// </remarks>
public sealed class DiscoveryDeviceCache
{
    /// <summary>
    /// 每条目最多记录的不同冲突指纹数。超出时先清自己的过期项，仍满则淘汰一条最旧记录
    /// （时刻并列时淘汰任意一条）。
    /// </summary>
    /// <remarks>
    /// 上限的意义是<b>内存有界</b>而不是「只容忍 8 个伪造指纹」——持续推送新指纹的攻击者
    /// 会让冲突标记持续存在（预期行为）；攻击停止后全部记录随 TTL 过期。
    /// </remarks>
    internal const int MaxConflictingFingerprintsPerDevice = 8;

    private readonly Dictionary<Guid, Entry> _devices = new();
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
    /// <returns>设备列表。冲突状态不在快照里——它用 <see cref="IsIdentityConflicted"/> 查询。</returns>
    public IReadOnlyList<DiscoveredDevice> Snapshot()
    {
        lock (_gate)
        {
            return _devices.Values.Select(entry => entry.Device).ToArray();
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
    /// <remarks>
    /// 指纹与主条目<b>不同</b>时走冲突路径：只追加冲突观察、不覆盖主条目，返回值也是
    /// <see langword="false"/>（设备已经在上一次观察里存在过）。
    /// </remarks>
    public bool Upsert(DiscoveredDevice device, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(device);

        lock (_gate)
        {
            if (_devices.TryGetValue(device.DeviceId, out Entry? existing))
            {
                if (string.Equals(
                        existing.Device.CertificateSha256,
                        device.CertificateSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    // 良性更新（ADR-027 第 3 条）：同指纹换 IP/端口照旧采用最新 endpoint。
                    existing.Device = device;
                }
                else
                {
                    RecordConflictCore(existing, device.CertificateSha256, now);
                }

                return false;
            }

            if (_devices.Count >= _maxDevices)
            {
                // 先清过期的；仍然满则淘汰最久未见的那台。
                RemoveExpiredCore(now, out _);

                if (_devices.Count >= _maxDevices)
                {
                    EvictOldestCore();
                }
            }

            _devices[device.DeviceId] = new Entry(device);
            return true;
        }
    }

    /// <summary>
    /// 指定设备当前是否处于身份冲突状态（同 deviceId 观察到过不同指纹）。
    /// </summary>
    /// <param name="deviceId">设备标识。</param>
    /// <param name="now">当前时间，用于过期判定。</param>
    /// <returns>存在未过期的冲突观察则为 <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>连接入口的禁用判定以此为准</b>。查询会顺手清理该条目的过期冲突观察——
    /// 「所有冲突观察都过期后，冲突可清除」（ADR-027 验证 ③）。
    /// 条目不存在（含主条目整体过期）时返回 <see langword="false"/>。
    /// </remarks>
    public bool IsIdentityConflicted(Guid deviceId, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(deviceId, out Entry? entry))
            {
                return false;
            }

            PruneConflictsCore(entry, now);
            return entry.Conflicts.Count > 0;
        }
    }

    /// <summary>
    /// 指定设备当前记录的未过期冲突指纹数。
    /// </summary>
    /// <param name="deviceId">设备标识。</param>
    /// <param name="now">当前时间，用于过期判定。</param>
    /// <returns>冲突指纹数（上限 <see cref="MaxConflictingFingerprintsPerDevice"/>）。</returns>
    /// <remarks>诊断口：给日志与将来的 UI 呈现用，也让「记录有界」成为可断言的事实。</remarks>
    public int ConflictingFingerprintCount(Guid deviceId, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(deviceId, out Entry? entry))
            {
                return 0;
            }

            PruneConflictsCore(entry, now);
            return entry.Conflicts.Count;
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
        foreach (KeyValuePair<Guid, Entry> pair in _devices)
        {
            if (pair.Value.Device.LastSeen <= cutoff)
            {
                expired.Add(pair.Value.Device);
            }
            else
            {
                // 主条目还活着：顺手回收它自己的过期冲突记录（保持长期的周期性清理语义）。
                PruneConflictsCore(pair.Value, now);
            }
        }

        foreach (DiscoveredDevice device in expired)
        {
            _devices.Remove(device.DeviceId);
        }

        removed = expired;
        return expired.Count;
    }

    private void RecordConflictCore(Entry entry, string fingerprint, DateTimeOffset now)
    {
        if (!entry.Conflicts.ContainsKey(fingerprint)
            && entry.Conflicts.Count >= MaxConflictingFingerprintsPerDevice)
        {
            PruneConflictsCore(entry, now);

            if (entry.Conflicts.Count >= MaxConflictingFingerprintsPerDevice)
            {
                EvictOldestConflictCore(entry);
            }
        }

        entry.Conflicts[fingerprint] = now;
    }

    private void PruneConflictsCore(Entry entry, DateTimeOffset now)
    {
        if (entry.Conflicts.Count == 0)
        {
            return;
        }

        DateTimeOffset cutoff = now - _ttl;

        List<string>? expired = null;
        foreach (KeyValuePair<string, DateTimeOffset> pair in entry.Conflicts)
        {
            if (pair.Value <= cutoff)
            {
                (expired ??= new List<string>()).Add(pair.Key);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (string key in expired)
        {
            entry.Conflicts.Remove(key);
        }
    }

    private static void EvictOldestConflictCore(Entry entry)
    {
        string? oldestKey = null;
        DateTimeOffset oldest = DateTimeOffset.MaxValue;

        foreach (KeyValuePair<string, DateTimeOffset> pair in entry.Conflicts)
        {
            if (pair.Value < oldest)
            {
                oldest = pair.Value;
                oldestKey = pair.Key;
            }
        }

        if (oldestKey is not null)
        {
            entry.Conflicts.Remove(oldestKey);
        }
    }

    private void EvictOldestCore()
    {
        Guid? oldestId = null;
        DateTimeOffset oldest = DateTimeOffset.MaxValue;

        foreach (KeyValuePair<Guid, Entry> pair in _devices)
        {
            if (pair.Value.Device.LastSeen < oldest)
            {
                oldest = pair.Value.Device.LastSeen;
                oldestId = pair.Key;
            }
        }

        if (oldestId is not null)
        {
            _devices.Remove(oldestId.Value);
        }
    }

    /// <summary>
    /// 一条设备条目的内部形态：主条目 + 冲突观察集。
    /// </summary>
    /// <remarks>
    /// 冲突集只在「观察到与主条目不同的指纹」时增长；主条目<b>永不</b>被冲突观察覆盖。
    /// 主条目整体过期时随条目一起消失（TTL 语义；见 ADR-027 阶段 0 定案第 10 条）。
    /// </remarks>
    private sealed class Entry
    {
        public Entry(DiscoveredDevice device)
        {
            Device = device;
        }

        /// <summary>主条目（最后一个「指纹一致」的观察）。</summary>
        public DiscoveredDevice Device { get; set; }

        /// <summary>冲突指纹 → 最近观察时刻。key 比较忽略大小写。</summary>
        public Dictionary<string, DateTimeOffset> Conflicts { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}
