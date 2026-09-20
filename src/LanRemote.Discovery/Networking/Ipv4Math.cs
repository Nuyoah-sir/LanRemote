using System.Buffers.Binary;
using System.Net;

namespace LanRemote.Discovery.Networking;

/// <summary>
/// IPv4 地址的纯数学工具。
/// </summary>
/// <remarks>
/// 所有「同网段」判断都必须走这里的真实掩码运算。
/// <b>禁止</b>用字符串前缀、或用「前三段相同」这类近似判断（见 M2 要求第三节）。
/// </remarks>
public static class Ipv4Math
{
    /// <summary>把 IPv4 地址转成 32 位无符号整数（网络字节序 → 主机整数）。</summary>
    /// <param name="address">地址。</param>
    /// <param name="value">结果。</param>
    /// <returns>是否为合法 IPv4。</returns>
    public static bool TryToUInt32(IPAddress? address, out uint value)
    {
        value = 0;

        if (address is null || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[4];
        if (!address.TryWriteBytes(buffer, out int written) || written != 4)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32BigEndian(buffer);
        return true;
    }

    /// <summary>把 32 位无符号整数还原为 IPv4 地址。</summary>
    /// <param name="value">主机序整数。</param>
    /// <returns>IPv4 地址。</returns>
    public static IPAddress FromUInt32(uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        return new IPAddress(buffer);
    }

    /// <summary>
    /// 计算 directed broadcast：<c>address | ~mask</c>。
    /// </summary>
    /// <param name="address">本机 IPv4。</param>
    /// <param name="mask">子网掩码。</param>
    /// <param name="broadcast">定向广播地址。</param>
    /// <returns>是否计算成功。</returns>
    /// <remarks>
    /// 例：192.168.1.20 / 255.255.255.0 → 192.168.1.255。
    /// <b>不要</b>硬编码 <c>.255</c>——/23 的子网广播是 192.168.11.255 这类结果。
    /// </remarks>
    public static bool TryComputeDirectedBroadcast(IPAddress address, IPAddress? mask, out IPAddress? broadcast)
    {
        broadcast = null;

        if (!TryToUInt32(address, out uint addressValue) || !TryToUInt32(mask, out uint maskValue))
        {
            return false;
        }

        broadcast = FromUInt32(addressValue | ~maskValue);
        return true;
    }

    /// <summary>按真实掩码判断两个地址是否属于同一网络号。</summary>
    /// <param name="localAddress">本机地址。</param>
    /// <param name="mask">真实子网掩码。</param>
    /// <param name="remoteAddress">远端地址。</param>
    /// <returns>网络号是否相等。</returns>
    /// <remarks>必须支持 /8、/12、/16、/20、/23、/24、/25…… 不能假定 /24。</remarks>
    public static bool IsInSameNetwork(IPAddress localAddress, IPAddress mask, IPAddress remoteAddress)
    {
        if (!TryToUInt32(localAddress, out uint localValue)
            || !TryToUInt32(mask, out uint maskValue)
            || !TryToUInt32(remoteAddress, out uint remoteValue))
        {
            return false;
        }

        return (localValue & maskValue) == (remoteValue & maskValue);
    }

    /// <summary>掩码是否为「连续前缀掩码」，且不是 0.0.0.0 / 255.255.255.255。</summary>
    /// <param name="mask">掩码。</param>
    /// <returns>是否可用。</returns>
    /// <remarks>
    /// 0.0.0.0 表示「没有掩码」，255.255.255.255 表示「没有网络」，
    /// 两者都不能用于同网段判断。非连续掩码在真实 NIC 上不应出现，出现即说明数据可疑。
    /// </remarks>
    public static bool IsUsableMask(IPAddress mask)
    {
        if (!TryToUInt32(mask, out uint maskValue))
        {
            return false;
        }

        if (maskValue == 0u || maskValue == uint.MaxValue)
        {
            return false;
        }

        // 连续前缀掩码：~mask 必然形如 2^n - 1。
        uint inverted = ~maskValue;
        return (inverted & (inverted + 1)) == 0;
    }
}
