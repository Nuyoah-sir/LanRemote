using System.Net;
using System.Net.Sockets;

namespace LanRemote.Discovery.Networking;

/// <summary>
/// Windows <c>IP_MULTICAST_IF</c> 选项值的构造。
/// </summary>
/// <remarks>
/// <para><b>为什么要显式设置</b>：LanRemote 明确支持多网卡（Ethernet + Wi-Fi 同时有私有 IPv4）。
/// 每个发送 socket 虽然 <c>Bind(binding.Address, 0)</c>，但 bind 只决定 unicast 源地址，
/// <b>不会</b>决定组播从哪张网卡出去——那是 <c>IP_MULTICAST_IF</c> 的职责。
/// 不设置就会退化成「系统默认组播路由」，在有线上网 + 无线并存的机器上会把所有组播
/// 错误地全部送到默认那一张卡，另一张卡所在子网永远收不到 announce。</para>
/// <para><b>取值形式（已在本机 Windows 上实测验证，不是猜测）</b>：
/// <c>IP_MULTICAST_IF</c> 接受 4 字节网络序的 <b>接口 IPv4 地址</b>（也接受 <c>0.x.x.x</c> 形式的接口索引）。
/// 这里采用地址形式：<see cref="IPAddress.GetAddressBytes"/> 返回的就是网络序 4 字节，
/// 与 Winsock 要求完全一致，不需要任何额外字节序转换。</para>
/// <para>实测（本机 .NET 10 / Windows，设置后立刻 <c>GetSocketOption</c> 回读）：
/// 地址形式 → OK，回读等于原地址；接口索引形式 <c>IPAddress.HostToNetworkOrder(index)</c> → OK；
/// 而对地址做 <c>HostToNetworkOrder</c> 或直接传裸索引都会抛
/// <c>SocketException: 在其上下文中，该请求的地址无效</c>。因此这里<b>不做</b>字节序翻转。</para>
/// </remarks>
internal static class MulticastInterfaceOption
{
    /// <summary>
    /// 构造某个网卡绑定的 <c>IP_MULTICAST_IF</c> 选项值。
    /// </summary>
    /// <param name="interfaceAddress">该网卡的本机 IPv4 地址。</param>
    /// <returns>可直接传给 <c>SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, ...)</c> 的 4 字节值。</returns>
    internal static byte[] ForInterface(IPAddress interfaceAddress)
    {
        ArgumentNullException.ThrowIfNull(interfaceAddress);

        if (interfaceAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException(
                "组播出口网卡地址必须是 IPv4。",
                nameof(interfaceAddress));
        }

        // GetAddressBytes 已经是网络字节序，正是 IP_MULTICAST_IF 要求的表示。
        return interfaceAddress.GetAddressBytes();
    }
}
