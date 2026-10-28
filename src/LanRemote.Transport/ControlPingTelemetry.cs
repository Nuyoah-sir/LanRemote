namespace LanRemote.Transport;

/// <summary>按真实认证会话登记 ping RTT 的只读遥测入口；不包含 token、密钥或对端秘密。</summary>
/// <remarks>
/// 每个控制会话至多登记一个计量器，注销必须传入同一实例，避免旧连接收尾误删新连接。
/// 快照是历史测量，不是连接存活、授权、显示或网络质量证明；后续控制器仍须复核会话身份。
/// </remarks>
internal sealed class ControlPingTelemetry
{
    internal sealed record SessionSnapshot(TimeSpan? LatestRtt, int PendingCount, long SampleCount);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, PingRttMeter> _bySession = new();

    internal int Count { get { lock (_gate) return _bySession.Count; } }

    internal void Register(Guid sessionId, PingRttMeter meter)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("会话标识不可为空。", nameof(sessionId));
        ArgumentNullException.ThrowIfNull(meter);
        lock (_gate) _bySession.Add(sessionId, meter);
    }

    internal void Unregister(Guid sessionId, PingRttMeter meter)
    {
        ArgumentNullException.ThrowIfNull(meter);
        lock (_gate)
        {
            if (_bySession.TryGetValue(sessionId, out PingRttMeter? current) && ReferenceEquals(current, meter))
                _bySession.Remove(sessionId);
        }
    }

    internal bool TryGet(Guid sessionId, out SessionSnapshot? snapshot)
    {
        lock (_gate)
        {
            if (!_bySession.TryGetValue(sessionId, out PingRttMeter? meter))
            {
                snapshot = null;
                return false;
            }
            snapshot = new(meter.LatestRtt, meter.PendingCount, meter.SampleCount);
            return true;
        }
    }
}

/// <summary>产品 Host 显式启用的 ping 配置；构造不取时、不触流、不启动 worker。</summary>
internal sealed class ControlPingOptions
{
    internal ControlPingOptions(ControlPingTelemetry telemetry, TimeSpan interval, TimeSpan pendingTimeout)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval), interval, "ping 间隔必须为正。");
        if (pendingTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pendingTimeout), pendingTimeout, "待确认清理时限必须为正。");
        Telemetry = telemetry;
        Interval = interval;
        PendingTimeout = pendingTimeout;
    }

    internal ControlPingTelemetry Telemetry { get; }
    internal TimeSpan Interval { get; }
    internal TimeSpan PendingTimeout { get; }
}
