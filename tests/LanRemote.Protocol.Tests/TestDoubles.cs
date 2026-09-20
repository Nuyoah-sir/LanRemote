using System.Net;
using LanRemote.Discovery.Networking;

namespace LanRemote.Protocol.Tests;

/// <summary>
/// 测试替身：可控的本机网卡绑定。
/// </summary>
/// <remarks>让 <c>SubnetPolicy</c> 与 <c>SourceEndpointFilter</c> 的测试不依赖真实网卡。</remarks>
internal sealed class FakeBindingProvider : INetworkBindingProvider
{
    private readonly IReadOnlyList<NetworkBinding> _bindings;

    public FakeBindingProvider(params (string Address, string Mask)[] entries)
    {
        List<NetworkBinding> bindings = new();

        int index = 0;
        foreach ((string address, string mask) in entries)
        {
            IPAddress addressValue = IPAddress.Parse(address);
            IPAddress maskValue = IPAddress.Parse(mask);
            Ipv4Math.TryComputeDirectedBroadcast(addressValue, maskValue, out IPAddress? broadcast);

            bindings.Add(new NetworkBinding(
                $"fake-{index}",
                $"Fake NIC {index}",
                System.Net.NetworkInformation.NetworkInterfaceType.Ethernet,
                addressValue,
                maskValue,
                broadcast ?? IPAddress.Any,
                index));

            index++;
        }

        _bindings = bindings;
    }

    public IReadOnlyList<NetworkBinding> GetBindings() => _bindings;

    public void Refresh()
    {
        // 固定替身不需要重新枚举。
    }
}
