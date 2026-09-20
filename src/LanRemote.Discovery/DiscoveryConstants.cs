namespace LanRemote.Discovery;

/// <summary>
/// 局域网发现的固定协议参数。
/// </summary>
/// <remarks>
/// 来源：<c>04_PROTOCOL_AND_SECURITY.md</c> 第 5 节与 <c>01_MASTER_PROMPT.md</c> 第六节。
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

    /// <summary>设备缓存过期时间（毫秒）；超过后标记离线/移除。</summary>
    public const int DeviceCacheTtlMs = 7000;

    /// <summary>发现报文魔数字符串。</summary>
    public const string Magic = "LANREMOTE";

    /// <summary>发现协议版本。</summary>
    public const int ProtocolVersion = 1;

    /// <summary>单个发现报文的长度上限（字节），超过即丢弃。</summary>
    public const int MaxAnnouncementBytes = 2048;
}
