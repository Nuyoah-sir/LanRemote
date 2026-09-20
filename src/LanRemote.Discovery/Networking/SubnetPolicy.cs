using System.Net;
using LanRemote.Core.Abstractions;

namespace LanRemote.Discovery.Networking;

/// <summary>
/// 同子网连接策略。
/// </summary>
/// <remarks>
/// <para>实现严格按 <c>04_PROTOCOL_AND_SECURITY.md</c> 第 4 节：</para>
/// <list type="number">
/// <item><description>local / remote 都必须是 IPv4；</description></item>
/// <item><description>remote 必须是 RFC1918；</description></item>
/// <item><description>按 <paramref name="localAddress"/> 找到<b>完全匹配</b>的本机绑定；找不到即拒绝；</description></item>
/// <item><description>用该绑定的<b>真实掩码</b>比较网络号。</description></item>
/// </list>
/// <para>注意：「两者都是 RFC1918」<b>不</b>等于同子网——10.x 与 192.168.x 都是私有地址但不在同一网络。</para>
/// </remarks>
public sealed class SubnetPolicy : ISubnetPolicy
{
    private readonly INetworkBindingProvider _bindings;

    /// <summary>构造策略。</summary>
    /// <param name="bindings">本机绑定提供者。</param>
    public SubnetPolicy(INetworkBindingProvider bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        _bindings = bindings;
    }

    /// <inheritdoc />
    public bool IsAllowedPeer(IPAddress localAddress, IPAddress remoteAddress)
    {
        if (localAddress is null || remoteAddress is null)
        {
            return false;
        }

        if (localAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || remoteAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        if (!PrivateIpv4.IsPrivate(remoteAddress))
        {
            return false;
        }

        NetworkBinding? binding = FindBinding(localAddress);
        if (binding is null)
        {
            return false;
        }

        return Ipv4Math.IsInSameNetwork(binding.Address, binding.SubnetMask, remoteAddress);
    }

    private NetworkBinding? FindBinding(IPAddress localAddress)
    {
        foreach (NetworkBinding binding in _bindings.GetBindings())
        {
            if (binding.Address.Equals(localAddress))
            {
                return binding;
            }
        }

        return null;
    }
}
