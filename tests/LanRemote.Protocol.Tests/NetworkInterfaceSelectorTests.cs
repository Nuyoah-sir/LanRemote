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

    private static NetworkInterfaceSnapshot WiFiDirectNic() =>
        Nic(
            "hotspot",
            "本地连接* 2",
            "Microsoft Wi-Fi Direct Virtual Adapter #2",
            NetworkInterfaceType.Wireless80211,
            OperationalStatus.Up,
            ("192.168.137.1", "255.255.255.0")) with { InterfaceIndex = 13 };

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
        // 常见真实网卡以及满足窄豁免条件的系统 Wi-Fi Direct 接口均应保留。
        IReadOnlyList<NetworkBinding> bindings = NetworkInterfaceSelector.Select(
            new[]
            {
                Nic("1", "Ethernet", "Intel(R) Ethernet Connection I219-V", NetworkInterfaceType.Ethernet, OperationalStatus.Up, ("192.168.1.20", "255.255.255.0")),
                Nic("2", "Ethernet 2", "Realtek PCIe GbE Family Controller", NetworkInterfaceType.Ethernet, OperationalStatus.Up, ("192.168.1.21", "255.255.255.0")),
                Nic("3", "Wi-Fi", "Microsoft Wi-Fi Direct Virtual Adapter", NetworkInterfaceType.Wireless80211, OperationalStatus.Up, ("192.168.1.22", "255.255.255.0")),
            });

        Assert.Equal(3, bindings.Count);
        Assert.Contains(bindings, b => b.InterfaceId == "3");
    }

    [Fact]
    public void WindowsWiFiDirectHotspot_IsIncludedAlongsideEthernet()
    {
        NetworkInterfaceSnapshot hotspot = WiFiDirectNic();
        IReadOnlyList<NetworkBinding> bindings = NetworkInterfaceSelector.Select(
            new[]
            {
                Nic("ethernet", "Ethernet", "Realtek PCIe GbE", NetworkInterfaceType.Ethernet, OperationalStatus.Up, ("192.168.1.20", "255.255.255.0")),
                hotspot,
            });

        Assert.Equal(2, bindings.Count);
        Assert.Contains(bindings, b => b.InterfaceId == "ethernet");
        NetworkBinding binding = Assert.Single(bindings, b => b.InterfaceId == hotspot.Id);
        Assert.Equal("本地连接* 2", binding.InterfaceName);
        Assert.Equal(NetworkInterfaceType.Wireless80211, binding.InterfaceType);
        Assert.Equal(13, binding.InterfaceIndex);
        Assert.Equal("192.168.137.1", binding.Address.ToString());
        Assert.Equal("255.255.255.0", binding.SubnetMask.ToString());
        Assert.Equal("192.168.137.255", binding.DirectedBroadcast.ToString());
    }

    [Theory]
    [InlineData("本地连接* 2", "Microsoft Wi-Fi Direct Virtual Adapter")]
    [InlineData("我的热点", "Microsoft Wi-Fi Direct Virtual Adapter #1")]
    [InlineData("已重命名的无线接口", "Microsoft Wi-Fi Direct Virtual Adapter #2")]
    [InlineData("Virtual LAN", "Microsoft Wi-Fi Direct Virtual Adapter #12")]
    [InlineData("", "Microsoft Wi-Fi Direct Virtual Adapter #10")]
    public void WindowsWiFiDirectDescriptionAndRenamedInterface_AreIncluded(string name, string description)
    {
        NetworkInterfaceSnapshot nic = WiFiDirectNic() with { Name = name, Description = description };

        NetworkBinding binding = Assert.Single(NetworkInterfaceSelector.Select(new[] { nic }));

        Assert.Equal(name, binding.InterfaceName);
        Assert.Equal(13, binding.InterfaceIndex);
    }

    [Theory]
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter #")]
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter #0")]
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter #02")]
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter #-2")]
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter #+2")]
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter #2.0")]
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter #2e1")]
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter # 2")]
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter #2 ")]
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter #2\n")]
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter #2abc")]
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter #２")]
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter #2２")]
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter #2 #3")]
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter#2")]
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter  #2")]
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter ")]
    [InlineData("Other Microsoft Wi-Fi Direct Virtual Adapter #2")]
    [InlineData("microsoft Wi-Fi Direct Virtual Adapter #2")]
    [InlineData("Microsoft Wi-Fi Direct virtual Adapter #2")]
    public void NonExactWindowsWiFiDirectDescription_IsExcluded(string description)
    {
        NetworkInterfaceSnapshot nic = WiFiDirectNic() with { Description = description };

        Assert.Empty(NetworkInterfaceSelector.Select(new[] { nic }));
    }

    [Theory]
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter #2", "Intel Wireless")]
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter", "")]
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter #2", "Other Virtual Adapter")]
    [InlineData("本地连接* 2", "Other Virtual Adapter")]
    public void SpoofedNameOrHotspotSubnet_DoesNotExemptOtherAdapters(string name, string description)
    {
        NetworkInterfaceSnapshot nic = WiFiDirectNic() with { Name = name, Description = description };

        Assert.Empty(NetworkInterfaceSelector.Select(new[] { nic }));
    }

    [Theory]
    [InlineData(NetworkInterfaceType.Ethernet)]
    [InlineData(NetworkInterfaceType.FastEthernetT)]
    [InlineData(NetworkInterfaceType.FastEthernetFx)]
    [InlineData(NetworkInterfaceType.GigabitEthernet)]
    [InlineData(NetworkInterfaceType.Loopback)]
    [InlineData(NetworkInterfaceType.Tunnel)]
    [InlineData(NetworkInterfaceType.Ppp)]
    [InlineData(NetworkInterfaceType.Unknown)]
    [InlineData(NetworkInterfaceType.Wwanpp)]
    public void WindowsWiFiDirectDescriptionOnNonWirelessType_IsExcluded(NetworkInterfaceType type)
    {
        NetworkInterfaceSnapshot nic = WiFiDirectNic() with { InterfaceType = type };

        Assert.Empty(NetworkInterfaceSelector.Select(new[] { nic }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WindowsWiFiDirectWithoutValidIndex_IsExcluded(int interfaceIndex)
    {
        NetworkInterfaceSnapshot nic = WiFiDirectNic() with { InterfaceIndex = interfaceIndex };

        Assert.Empty(NetworkInterfaceSelector.Select(new[] { nic }));
    }

    [Fact]
    public void DownWindowsWiFiDirect_IsExcluded()
    {
        NetworkInterfaceSnapshot nic = WiFiDirectNic() with { OperationalStatus = OperationalStatus.Down };

        Assert.Empty(NetworkInterfaceSelector.Select(new[] { nic }));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("169.254.137.1")]
    [InlineData("172.32.137.1")]
    [InlineData("fe80::1")]
    public void WindowsWiFiDirectWithNonPrivateIpv4_IsExcluded(string address)
    {
        NetworkInterfaceSnapshot nic = WiFiDirectNic() with
        {
            UnicastAddresses = new[] { new Ipv4UnicastAddress(IPAddress.Parse(address), IPAddress.Parse("255.255.255.0")) },
        };

        Assert.Empty(NetworkInterfaceSelector.Select(new[] { nic }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    [InlineData("255.0.255.0")]
    [InlineData("ffff:ffff:ffff:ffff::")]
    public void WindowsWiFiDirectWithoutUsableMask_IsExcluded(string? mask)
    {
        NetworkInterfaceSnapshot nic = WiFiDirectNic() with
        {
            UnicastAddresses = new[] { new Ipv4UnicastAddress(IPAddress.Parse("192.168.137.1"), mask is null ? null : IPAddress.Parse(mask)) },
        };

        Assert.Empty(NetworkInterfaceSelector.Select(new[] { nic }));
    }

    [Theory]
    [InlineData("VPN")]
    [InlineData("Hyper-V")]
    [InlineData("vEthernet")]
    [InlineData("VMware")]
    [InlineData("VirtualBox")]
    [InlineData("WireGuard")]
    [InlineData("Wintun")]
    [InlineData("Tailscale")]
    [InlineData("ZeroTier")]
    [InlineData("TAP-Windows Adapter V9")]
    [InlineData("Tunnel")]
    public void WindowsWiFiDirectConflictingVirtualTokens_AreStillExcluded(string token)
    {
        NetworkInterfaceSnapshot nameConflict = WiFiDirectNic() with { Name = $"本地连接* 2 {token}" };
        NetworkInterfaceSnapshot descriptionConflict = WiFiDirectNic() with { Description = $"Microsoft Wi-Fi Direct Virtual Adapter #2 {token}" };

        Assert.Empty(NetworkInterfaceSelector.Select(new[] { nameConflict, descriptionConflict }));
    }

    [Fact]
    public void WindowsWiFiDirectWithMultipleAddresses_PreservesEligibleBindings()
    {
        NetworkInterfaceSnapshot nic = WiFiDirectNic() with
        {
            UnicastAddresses = new[]
            {
                new Ipv4UnicastAddress(IPAddress.Parse("192.168.137.1"), IPAddress.Parse("255.255.255.0")),
                new Ipv4UnicastAddress(IPAddress.Parse("10.1.2.3"), IPAddress.Parse("255.255.0.0")),
                new Ipv4UnicastAddress(IPAddress.Parse("172.16.17.18"), IPAddress.Parse("255.255.240.0")),
                new Ipv4UnicastAddress(IPAddress.Parse("8.8.8.8"), IPAddress.Parse("255.255.255.0")),
                new Ipv4UnicastAddress(IPAddress.Parse("192.168.137.2"), null),
            },
        };

        IReadOnlyList<NetworkBinding> bindings = NetworkInterfaceSelector.Select(new[] { nic });

        Assert.Equal(3, bindings.Count);
        Assert.All(bindings, binding =>
        {
            Assert.Equal(nic.Id, binding.InterfaceId);
            Assert.Equal(13, binding.InterfaceIndex);
        });
        Assert.Contains(bindings, b => b.Address.ToString() == "192.168.137.1" && b.SubnetMask.ToString() == "255.255.255.0" && b.DirectedBroadcast.ToString() == "192.168.137.255");
        Assert.Contains(bindings, b => b.Address.ToString() == "10.1.2.3" && b.SubnetMask.ToString() == "255.255.0.0" && b.DirectedBroadcast.ToString() == "10.1.255.255");
        Assert.Contains(bindings, b => b.Address.ToString() == "172.16.17.18" && b.SubnetMask.ToString() == "255.255.240.0" && b.DirectedBroadcast.ToString() == "172.16.31.255");
    }

    [Fact]
    public void VirtualFilterWithoutInterfaceMetadata_RemainsConservative()
    {
        NetworkInterfaceSnapshot nic = WiFiDirectNic();

        Assert.True(VirtualAdapterFilter.IsLikelyVirtual(nic.Name, nic.Description));
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
