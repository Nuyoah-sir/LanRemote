using System.Net;
using LanRemote.Core.Models;

namespace LanRemote.Transport;

/// <summary>
/// 一次连接尝试的<b>不可变</b>目标快照。
/// </summary>
/// <remarks>
/// <para><b>为什么必须冻结</b>（外部红队评审 A 桶第 2 条，TOCTOU）：
/// discovery 缓存是 last-write-wins 的，且它的数据来自<b>未认证</b>的 UDP 广播。
/// 如果在 <c>ConnectAsync</c> 执行期间还去读实时缓存，那么"点击之后、握手之前"这段时间里
/// 缓存一旦被更新（或被伪造的广播更新），这次连接的期望指纹或目标地址就会被偷偷换掉。
/// 因此用户点击连接的那一刻必须冻结这一份四元组，整个 TCP/TLS 尝试只认它。</para>
/// <para>本类型只做<b>结构校验</b>：设备号非空、地址是 IPv4、端口在合法区间、指纹能解成 32 字节。
/// RFC1918 与同子网不在本类型校验——discovery 侧已按 RFC1918 过滤，服务端 accept 时还会再按
/// 真实掩码校验一次（<c>ISubnetPolicy</c>）。职责不重复，避免两处规则不一致。</para>
/// </remarks>
public sealed class ConnectionTarget
{
    private readonly byte[] _expectedCertSha256;

    private ConnectionTarget(Guid deviceId, IPAddress remoteAddress, int port, byte[] expectedCertSha256)
    {
        DeviceId = deviceId;
        RemoteAddress = remoteAddress;
        Port = port;
        _expectedCertSha256 = expectedCertSha256;
    }

    /// <summary>目标设备号。</summary>
    public Guid DeviceId { get; }

    /// <summary>目标 IPv4 地址（取自 UDP source endpoint，不是报文内容）。</summary>
    public IPAddress RemoteAddress { get; }

    /// <summary>目标 TCP 端口。</summary>
    public int Port { get; }

    /// <summary>期望的证书指纹，恰好 32 字节；只读视图，外部无法改写内部数组。</summary>
    public ReadOnlyMemory<byte> ExpectedCertSha256 => _expectedCertSha256;

    /// <summary>
    /// 从 discovery 结果创建快照。
    /// </summary>
    /// <param name="device">发现到的设备（地址来自 UDP source）。</param>
    /// <param name="target">成功时为不可变快照；失败为 <see langword="null"/>。</param>
    /// <returns>校验通过则为 <see langword="true"/>。</returns>
    public static bool TryCreate(DiscoveredDevice device, out ConnectionTarget? target)
    {
        ArgumentNullException.ThrowIfNull(device);

        return TryCreate(
            device.DeviceId,
            device.Address,
            device.Port,
            device.CertificateSha256,
            out target);
    }

    /// <summary>
    /// 创建快照。
    /// </summary>
    /// <param name="deviceId">目标设备号。</param>
    /// <param name="remoteAddress">目标 IPv4 地址。</param>
    /// <param name="port">目标 TCP 端口（1..65535）。</param>
    /// <param name="certificateSha256Hex">期望指纹，64 个十六进制字符。</param>
    /// <param name="target">成功时为不可变快照；失败为 <see langword="null"/>。</param>
    /// <returns>全部校验通过则为 <see langword="true"/>。</returns>
    public static bool TryCreate(
        Guid deviceId,
        IPAddress? remoteAddress,
        int port,
        string? certificateSha256Hex,
        out ConnectionTarget? target)
    {
        target = null;

        if (deviceId == Guid.Empty || remoteAddress is null)
        {
            return false;
        }

        // 只接受 IPv4：ADR-006 把「同一局域网」定义为同一 IPv4 子网，v1 不处理 IPv6。
        if (remoteAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        if (port is < 1 or > 65535)
        {
            return false;
        }

        if (!CertificatePin.TryDecode(certificateSha256Hex, out byte[] pin))
        {
            return false;
        }

        target = new ConnectionTarget(deviceId, remoteAddress, port, pin);
        return true;
    }
}
