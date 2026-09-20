using System.Net;

namespace LanRemote.Discovery.Networking;

/// <summary>
/// RFC1918 私有 IPv4 判定。
/// </summary>
/// <remarks>
/// LanRemote 只承认这三个私网段：
/// <list type="bullet">
/// <item><description>10.0.0.0/8</description></item>
/// <item><description>172.16.0.0/12</description></item>
/// <item><description>192.168.0.0/16</description></item>
/// </list>
/// 127.0.0.0/8（loopback）与 169.254.0.0/16（link-local）虽然「不是公网」，
/// 但也不属于 RFC1918，因此一律返回 false。
/// </remarks>
public static class PrivateIpv4
{
    private const uint TenNetwork = 0x0A000000u;      // 10.0.0.0
    private const uint TenMask = 0xFF000000u;         // /8

    private const uint OneSevenTwoNetwork = 0xAC100000u; // 172.16.0.0
    private const uint OneSevenTwoMask = 0xFFF00000u;    // /12

    private const uint OneNineTwoNetwork = 0xC0A80000u; // 192.168.0.0
    private const uint OneNineTwoMask = 0xFFFF0000u;    // /16

    private const uint LoopbackNetwork = 0x7F000000u;   // 127.0.0.0
    private const uint LoopbackMask = 0xFF000000u;      // /8

    private const uint LinkLocalNetwork = 0xA9FE0000u;  // 169.254.0.0
    private const uint LinkLocalMask = 0xFFFF0000u;     // /16

    /// <summary>是否为 RFC1918 私有 IPv4。</summary>
    /// <param name="address">地址。</param>
    /// <returns>是否属于 10/8、172.16/12 或 192.168/16。</returns>
    public static bool IsPrivate(IPAddress? address)
    {
        if (!Ipv4Math.TryToUInt32(address, out uint value))
        {
            return false;
        }

        return (value & TenMask) == TenNetwork
            || (value & OneSevenTwoMask) == OneSevenTwoNetwork
            || (value & OneNineTwoMask) == OneNineTwoNetwork;
    }

    /// <summary>是否为 loopback（127.0.0.0/8）。</summary>
    /// <param name="address">地址。</param>
    /// <returns>是否 loopback。</returns>
    public static bool IsLoopback(IPAddress? address) =>
        Ipv4Math.TryToUInt32(address, out uint value) && (value & LoopbackMask) == LoopbackNetwork;

    /// <summary>是否为 link-local（169.254.0.0/16）。</summary>
    /// <param name="address">地址。</param>
    /// <returns>是否 link-local。</returns>
    public static bool IsLinkLocal(IPAddress? address) =>
        Ipv4Math.TryToUInt32(address, out uint value) && (value & LinkLocalMask) == LinkLocalNetwork;
}
