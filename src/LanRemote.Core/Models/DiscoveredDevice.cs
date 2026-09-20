using System.Net;

namespace LanRemote.Core.Models;

/// <summary>
/// 局域网中发现的一台远端设备。
/// </summary>
/// <param name="DeviceId">远端稳定内部标识。</param>
/// <param name="DeviceCode">远端人类可读短码。</param>
/// <param name="DeviceName">远端显示名。</param>
/// <param name="Address">远端 IPv4 地址。</param>
/// <param name="Port">远端 TLS/TCP 端口。</param>
/// <param name="CertificateSha256">远端证书指纹，用于 TLS pinning。</param>
/// <param name="LastSeen">最后一次收到公告的时间。</param>
/// <param name="Capabilities">远端声明的能力集合，如 <c>view</c>/<c>control</c>/<c>multi-monitor</c>。</param>
/// <remarks>
/// 发现包不是认证依据：<see cref="CertificateSha256"/> 只能用于 pinning，
/// 远端是否真的持有访问密钥必须靠后续 server proof 验证（见 <c>04_PROTOCOL_AND_SECURITY.md</c> 第 9 节）。
/// </remarks>
public sealed record DiscoveredDevice(
    Guid DeviceId,
    string DeviceCode,
    string DeviceName,
    IPAddress Address,
    int Port,
    string CertificateSha256,
    DateTimeOffset LastSeen,
    IReadOnlySet<string> Capabilities);
