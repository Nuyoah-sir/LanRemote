namespace LanRemote.Discovery.Protocol;

/// <summary>
/// 发现公告。
/// </summary>
/// <remarks>
/// <para>对应 <c>04_PROTOCOL_AND_SECURITY.md</c> 第 5.1 节的 JSON。</para>
/// <para><b>公告不是认证依据</b>：任何人都可以伪造。真正的认证在 M3/M4。</para>
/// <para><b>本类型刻意不包含任何网络地址字段</b>：远端地址一律取自 UDP 报文的 source endpoint，
/// 绝不能相信 payload 里声明的地址。</para>
/// </remarks>
public sealed class DiscoveryAnnouncement
{
    /// <summary>魔数。</summary>
    public string Magic { get; set; } = DiscoveryConstants.Magic;

    /// <summary>协议版本。</summary>
    public int Protocol { get; set; } = DiscoveryConstants.ProtocolVersion;

    /// <summary>报文类型。</summary>
    public string Type { get; set; } = DiscoveryConstants.AnnounceType;

    /// <summary>远端稳定内部标识。</summary>
    public Guid DeviceId { get; set; }

    /// <summary>远端的派生设备码。</summary>
    public string DeviceCode { get; set; } = string.Empty;

    /// <summary>远端显示名。</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>远端应用版本。</summary>
    public string AppVersion { get; set; } = string.Empty;

    /// <summary>远端 TLS/TCP 端口。</summary>
    public int TcpPort { get; set; } = DiscoveryConstants.ExpectedTransportPort;

    /// <summary>远端证书 SHA-256 指纹（大写 hex）。</summary>
    public string CertSha256 { get; set; } = string.Empty;

    /// <summary>远端声明的能力。</summary>
    public List<string> Capabilities { get; set; } = new();

    /// <summary>随机 nonce（Base64，12 字节）。</summary>
    public string Nonce { get; set; } = string.Empty;
}

/// <summary>
/// 发现探测。
/// </summary>
/// <remarks>
/// 对应 <c>04_PROTOCOL_AND_SECURITY.md</c> 第 5.2 节。
/// probe 只表示「同网段有人在找 LanRemote 主机」，
/// <b>不得</b>携带 DeviceId、访问密钥、证书或任何身份信息。
/// </remarks>
public sealed class DiscoveryProbe
{
    /// <summary>魔数。</summary>
    public string Magic { get; set; } = DiscoveryConstants.Magic;

    /// <summary>协议版本。</summary>
    public int Protocol { get; set; } = DiscoveryConstants.ProtocolVersion;

    /// <summary>报文类型。</summary>
    public string Type { get; set; } = DiscoveryConstants.ProbeType;

    /// <summary>随机 nonce（Base64，12 字节）。</summary>
    public string Nonce { get; set; } = string.Empty;
}
