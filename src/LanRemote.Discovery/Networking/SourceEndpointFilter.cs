using System.Net;

namespace LanRemote.Discovery.Networking;

/// <summary>
/// UDP 报文来源地址的准入判断。
/// </summary>
/// <remarks>
/// <b>安全顺序要求</b>：来源地址必须先在这里通过，才允许进入 JSON parser。
/// 不能把公网 / 跨子网的垃圾先喂给 <c>JsonSerializer</c>。
/// </remarks>
public static class SourceEndpointFilter
{
    /// <summary>判断 UDP 来源地址是否可以继续处理。</summary>
    /// <param name="remoteAddress">来源 IPv4。</param>
    /// <param name="bindings">本机合格绑定。</param>
    /// <returns>是否接受。</returns>
    /// <remarks>
    /// 通过与<b>任意一个</b>合格绑定同子网即可接受；
    /// 多网卡机器（有线 + Wi-Fi 在网段 A/B）应当都能工作。
    /// </remarks>
    public static bool IsAcceptableSource(IPAddress? remoteAddress, IReadOnlyList<NetworkBinding> bindings)
    {
        if (remoteAddress is null)
        {
            return false;
        }

        if (remoteAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        if (!PrivateIpv4.IsPrivate(remoteAddress))
        {
            return false;
        }

        foreach (NetworkBinding binding in bindings)
        {
            if (Ipv4Math.IsInSameNetwork(binding.Address, binding.SubnetMask, remoteAddress))
            {
                return true;
            }
        }

        return false;
    }
}
