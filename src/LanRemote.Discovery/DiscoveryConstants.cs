namespace LanRemote.Discovery;

/// <summary>
/// 局域网发现的固定协议参数。
/// </summary>
/// <remarks>
/// 来源：<c>04_PROTOCOL_AND_SECURITY.md</c> 第 2、5 节与 <c>01_MASTER_PROMPT.md</c> 第六节。
/// 协议 v1 固定端口，减少施工复杂度；将来如需改端口必须同步更新文档与集成测试。
/// </remarks>
public static class DiscoveryConstants
{
    /// <summary>UDP 发现端口。</summary>
    public const int Port = 45872;

    /// <summary>IPv4 组播发现地址。</summary>
    public const string MulticastGroupAddress = "239.255.77.77";

    /// <summary>组播 TTL，必须为 1，防止跨越路由。</summary>
    public const int MulticastTimeToLive = 1;

    /// <summary>announce 广播周期（毫秒）。</summary>
    public const int AnnounceIntervalMs = 2000;

    /// <summary>设备缓存过期时间（毫秒）；超过后从缓存移除。</summary>
    public const int DeviceCacheTtlMs = 7000;

    /// <summary>缓存清理循环周期（毫秒）。</summary>
    public const int CacheCleanupIntervalMs = 1000;

    /// <summary>发现报文魔数字符串。</summary>
    public const string Magic = "LANREMOTE";

    /// <summary>发现协议版本。</summary>
    public const int ProtocolVersion = 1;

    /// <summary>单个发现报文的长度上限（字节），超过即丢弃。</summary>
    public const int MaxAnnouncementBytes = 2048;

    /// <summary>receiver 缓冲区大小：上限 + 1，用「超出 1 字节」来识别被放大的报文。</summary>
    public const int ReceiverBufferBytes = MaxAnnouncementBytes + 1;

    /// <summary>announce 报文的 type 值。</summary>
    public const string AnnounceType = "announce";

    /// <summary>probe 报文的 type 值。</summary>
    public const string ProbeType = "probe";

    /// <summary>
    /// 报文中声明的 TLS/TCP 端口的期望值。
    /// </summary>
    /// <remarks>
    /// 存在意义：让 <c>LanRemote.Protocol.Tests</c> 能断言
    /// <c>TransportConstants.Port == DiscoveryConstants.ExpectedTransportPort</c>，
    /// 这样 UDP announcement 与未来 TLS 端口不会悄悄漂移。
    /// </remarks>
    public const int ExpectedTransportPort = 45873;

    /// <summary>nonce 字节数。</summary>
    public const int NonceByteCount = 12;

    /// <summary>设备名最大字符数。</summary>
    public const int MaxDeviceNameLength = 128;

    /// <summary>capabilities 最大条目数。</summary>
    public const int MaxCapabilities = 16;

    /// <summary>单个 capability 最大字符数。</summary>
    public const int MaxCapabilityLength = 32;

    /// <summary>设备缓存最大条目数。局域网报文是不可信输入，绝不能无界。</summary>
    public const int MaxCachedDevices = 256;

    /// <summary>WatchAsync 更新队列容量；FullMode = DropOldest。</summary>
    public const int UpdateChannelCapacity = 512;

    /// <summary>capability：可被查看。</summary>
    public const string CapabilityView = "view";

    /// <summary>capability：可被控制。</summary>
    public const string CapabilityControl = "control";
}
