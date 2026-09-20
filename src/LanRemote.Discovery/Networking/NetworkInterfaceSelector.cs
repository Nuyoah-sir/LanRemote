using System.Net;
using System.Net.NetworkInformation;

namespace LanRemote.Discovery.Networking;

/// <summary>
/// 合格网卡筛选。
/// </summary>
/// <remarks>
/// <para>规则（<c>04_PROTOCOL_AND_SECURITY.md</c> 第 3 节）：</para>
/// <list type="bullet">
/// <item><description><see cref="OperationalStatus.Up"/>；</description></item>
/// <item><description>类型属于允许的真实 Ethernet / Wi-Fi；</description></item>
/// <item><description>不是明显的虚拟 / VPN 适配器；</description></item>
/// <item><description>IPv4 地址属于 RFC1918；</description></item>
/// <item><description>有可用且连续的子网掩码。</description></item>
/// </list>
/// <para>一张 NIC 可能有多个合格 IPv4，每个都会形成一个独立的 <see cref="NetworkBinding"/>。</para>
/// </remarks>
public static class NetworkInterfaceSelector
{
    /// <summary>允许的物理网络类型。</summary>
    public static IReadOnlySet<NetworkInterfaceType> AllowedInterfaceTypes { get; } =
        new HashSet<NetworkInterfaceType>
        {
            NetworkInterfaceType.Ethernet,
            NetworkInterfaceType.FastEthernetT,
            NetworkInterfaceType.FastEthernetFx,
            NetworkInterfaceType.GigabitEthernet,
            NetworkInterfaceType.Wireless80211,
        };

    /// <summary>类型是否在允许清单内。</summary>
    /// <param name="interfaceType">网卡类型。</param>
    /// <returns>是否允许。</returns>
    /// <remarks>显式排除 Loopback / Tunnel / PPP / Wwanpp / Unknown 等。</remarks>
    public static bool IsAllowedInterfaceType(NetworkInterfaceType interfaceType) =>
        AllowedInterfaceTypes.Contains(interfaceType);

    /// <summary>
    /// 从网卡快照中筛出合格的 IPv4 绑定。
    /// </summary>
    /// <param name="interfaces">网卡快照。</param>
    /// <returns>绑定列表；无合格网卡时为空列表。</returns>
    public static IReadOnlyList<NetworkBinding> Select(IEnumerable<NetworkInterfaceSnapshot> interfaces)
    {
        ArgumentNullException.ThrowIfNull(interfaces);

        List<NetworkBinding> bindings = new();

        foreach (NetworkInterfaceSnapshot nic in interfaces)
        {
            if (nic is null)
            {
                continue;
            }

            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            if (!IsAllowedInterfaceType(nic.InterfaceType))
            {
                continue;
            }

            if (VirtualAdapterFilter.IsLikelyVirtual(nic.Name, nic.Description))
            {
                continue;
            }

            foreach (Ipv4UnicastAddress unicast in nic.UnicastAddresses)
            {
                if (unicast?.Address is null)
                {
                    continue;
                }

                IPAddress address = unicast.Address;
                if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    continue;
                }

                if (!PrivateIpv4.IsPrivate(address))
                {
                    continue;
                }

                if (unicast.Ipv4Mask is null || !Ipv4Math.IsUsableMask(unicast.Ipv4Mask))
                {
                    continue;
                }

                if (!Ipv4Math.TryComputeDirectedBroadcast(address, unicast.Ipv4Mask, out IPAddress? broadcast)
                    || broadcast is null)
                {
                    continue;
                }

                bindings.Add(new NetworkBinding(
                    nic.Id,
                    nic.Name,
                    nic.InterfaceType,
                    address,
                    unicast.Ipv4Mask,
                    broadcast,
                    nic.InterfaceIndex));
            }
        }

        return bindings;
    }
}
