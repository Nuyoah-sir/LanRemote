using System.Net;
using System.Net.NetworkInformation;
using LanRemote.Discovery.Networking;
using Xunit;

namespace LanRemote.Protocol.Tests;

/// <summary>
/// 网卡筛选测试（使用快照替身，不触碰真实 Windows 网卡）。
/// </summary>
public sealed class NetworkInterfaceSelectorTests
{
    private static NetworkInterfaceSnapshot Nic(
        string id,
        string name,
        string description,
        NetworkInterfaceType type,
        OperationalStatus status,
        params (string Address, string Mask)[] addresses) =>
        new(
            id,
            name,
            description,
            type,
            status,
            1,
            addresses
                .Select(a => new Ipv4UnicastAddress(IPAddress.Parse(a.Address), IPAddress.Parse(a.Mask)))
                .ToArray());

    private static NetworkInterfaceSnapshot NicWithoutMask(
        string id,
        string name,
        NetworkInterfaceType type,
        string address) =>
        new(
            id,
            name,
            name,
            type,
            OperationalStatus.Up,
            1,
            new[] { new Ipv4UnicastAddress(IPAddress.Parse(address), null) });

    [Fact]
    public void UpEthernetWithPrivateIpv4AndMask_IsIncluded()
    {
        IReadOnlyList<NetworkBinding> bindings = NetworkInterfaceSelector.Select(
            new[] { Nic("1", "Ethernet", "Realtek PCIe GbE", NetworkInterfaceType.Ethernet, OperationalStatus.Up, ("192.168.1.20", "255.255.255.0")) });

        NetworkBinding binding = Assert.Single(bindings);
        Assert.Equal("192.168.1.20", binding.Address.ToString());
        Assert.Equal("255.255.255.0", binding.SubnetMask.ToString());
        Assert.Equal("192.168.1.255", binding.DirectedBroadcast.ToString());
    }

    [Fact]
    public void DownEthernet_IsExcluded()
    {
        IReadOnlyList<NetworkBinding> bindings = NetworkInterfaceSelector.Select(
            new[] { Nic("1", "Ethernet", "Realtek", NetworkInterfaceType.Ethernet, OperationalStatus.Down, ("192.168.1.20", "255.255.255.0")) });

        Assert.Empty(bindings);
    }

    [Fact]
    public void UpWireless80211WithPrivateIpv4_IsIncluded()
    {
        IReadOnlyList<NetworkBinding> bindings = NetworkInterfaceSelector.Select(
            new[] { Nic("2", "Wi-Fi", "Intel Wireless", NetworkInterfaceType.Wireless80211, OperationalStatus.Up, ("10.0.0.5", "255.255.255.0")) });

        Assert.Single(bindings);
        Assert.Equal(NetworkInterfaceType.Wireless80211, bindings[0].InterfaceType);
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("169.254.7.7")]
    [InlineData("203.0.113.9")]
    public void NonPrivateAddress_IsExcluded(string address)
    {
        IReadOnlyList<NetworkBinding> bindings = NetworkInterfaceSelector.Select(
            new[] { Nic("1", "Ethernet", "Realtek", NetworkInterfaceType.Ethernet, OperationalStatus.Up, (address, "255.255.255.0")) });

        Assert.Empty(bindings);
    }

    [Theory]
    [InlineData(NetworkInterfaceType.Loopback)]
    [InlineData(NetworkInterfaceType.Tunnel)]
    [InlineData(NetworkInterfaceType.Ppp)]
    [InlineData(NetworkInterfaceType.Unknown)]
    [InlineData(NetworkInterfaceType.Wwanpp)]
    public void DisallowedInterfaceType_IsExcluded(NetworkInterfaceType type)
    {
        IReadOnlyList<NetworkBinding> bindings = NetworkInterfaceSelector.Select(
            new[] { Nic("1", "Some Adapter", "Some Adapter", type, OperationalStatus.Up, ("192.168.1.20", "255.255.255.0")) });

        Assert.Empty(bindings);
    }

    [Theory]
    [InlineData("Hyper-V Virtual Switch", "Microsoft Hyper-V Network Adapter")]
    [InlineData("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter")]
    [InlineData("VMware Network Adapter VMnet8", "VMware Virtual Ethernet Adapter")]
    [InlineData("VirtualBox Host-Only Ethernet Adapter", "VirtualBox")]
    [InlineData("WireGuard Tunnel", "WireGuard Adapter")]
    [InlineData("Wintun Userspace Tunnel", "Wintun")]
    [InlineData("Tailscale", "Tailscale Tunnel")]
    [InlineData("ZeroTier One [1234]", "ZeroTier Virtual Port")]
    [InlineData("TAP-Windows Adapter V9", "TAP-Windows Provider V9")]
    [InlineData("Ethernet 2", "Some VPN Client Adapter")]
    public void VirtualOrVpnAdapter_IsExcluded(string name, string description)
    {
        IReadOnlyList<NetworkBinding> bindings = NetworkInterfaceSelector.Select(
            new[] { Nic("1", name, description, NetworkInterfaceType.Ethernet, OperationalStatus.Up, ("192.168.1.20", "255.255.255.0")) });

        Assert.Empty(bindings);
    }

    [Fact]
    public void RealAdapterNamesAreNotFalsePositives()
    {
        // 这几个词在真实网卡名称/描述里很常见，绝不能误杀。
        IReadOnlyList<NetworkBinding> bindings = NetworkInterfaceSelector.Select(
            new[]
            {
                Nic("1", "Ethernet", "Intel(R) Ethernet Connection I219-V", NetworkInterfaceType.Ethernet, OperationalStatus.Up, ("192.168.1.20", "255.255.255.0")),
                Nic("2", "Ethernet 2", "Realtek PCIe GbE Family Controller", NetworkInterfaceType.Ethernet, OperationalStatus.Up, ("192.168.1.21", "255.255.255.0")),
                Nic("3", "Wi-Fi", "Microsoft Wi-Fi Direct Virtual Adapter", NetworkInterfaceType.Wireless80211, OperationalStatus.Up, ("192.168.1.22", "255.255.255.0")),
            });

        // 第三张卡的 description 含 "Virtual"，会被过滤掉（这是保守策略的预期结果）。
        Assert.Equal(2, bindings.Count);
    }

    [Fact]
    public void MissingMask_IsExcluded()
    {
        IReadOnlyList<NetworkBinding> bindings = NetworkInterfaceSelector.Select(
            new[] { NicWithoutMask("1", "Ethernet", NetworkInterfaceType.Ethernet, "192.168.1.20") });

        Assert.Empty(bindings);
    }

    [Fact]
    public void OneNicWithTwoValidPrivateAddresses_ProducesTwoBindings()
    {
        IReadOnlyList<NetworkBinding> bindings = NetworkInterfaceSelector.Select(
            new[]
            {
                Nic(
                    "1",
                    "Ethernet",
                    "Realtek",
                    NetworkInterfaceType.Ethernet,
                    OperationalStatus.Up,
                    ("192.168.1.20", "255.255.255.0"),
                    ("10.1.2.3", "255.255.0.0")),
            });

        Assert.Equal(2, bindings.Count);
        Assert.Contains(bindings, b => b.Address.ToString() == "192.168.1.20");
        Assert.Contains(bindings, b => b.Address.ToString() == "10.1.2.3");
        Assert.Equal("10.1.255.255", bindings.Single(b => b.Address.ToString() == "10.1.2.3").DirectedBroadcast.ToString());
    }

    [Fact]
    public void MixedEnvironment_OnlyEligibleBindingsSurvive()
    {
        IReadOnlyList<NetworkBinding> bindings = NetworkInterfaceSelector.Select(
            new[]
            {
                Nic("1", "Ethernet", "Realtek", NetworkInterfaceType.Ethernet, OperationalStatus.Up, ("192.168.1.20", "255.255.255.0")),
                Nic("2", "Wi-Fi", "Intel Wireless", NetworkInterfaceType.Wireless80211, OperationalStatus.Up, ("192.168.1.30", "255.255.255.0")),
                Nic("3", "Ethernet 3", "Realtek", NetworkInterfaceType.Ethernet, OperationalStatus.Down, ("192.168.1.40", "255.255.255.0")),
                Nic("4", "Loopback", "Software Loopback", NetworkInterfaceType.Loopback, OperationalStatus.Up, ("127.0.0.1", "255.0.0.0")),
                Nic("5", "vEthernet", "Hyper-V", NetworkInterfaceType.Ethernet, OperationalStatus.Up, ("172.16.0.9", "255.255.0.0")),
            });

        Assert.Equal(2, bindings.Count);
    }

    [Fact]
    public void SystemNetworkInterfaceSource_DoesNotThrow()
    {
        // 冒烟：真实 OS 枚举不应抛异常（结果取决于机器，只断言不抛）。
        IReadOnlyList<NetworkInterfaceSnapshot> snapshots = new SystemNetworkInterfaceSource().GetInterfaces();
        IReadOnlyList<NetworkBinding> bindings = NetworkInterfaceSelector.Select(snapshots);

        Assert.NotNull(snapshots);
        Assert.NotNull(bindings);
    }
}
