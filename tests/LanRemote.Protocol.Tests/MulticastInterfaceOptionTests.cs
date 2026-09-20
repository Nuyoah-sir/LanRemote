using System.Net;
using System.Net.Sockets;
using LanRemote.Discovery.Networking;
using Xunit;

namespace LanRemote.Protocol.Tests;

/// <summary>
/// Windows <c>IP_MULTICAST_IF</c> 组播出口网卡选项值。
/// </summary>
/// <remarks>
/// <para>多网卡（Ethernet + Wi-Fi 各有私有 IPv4）时，<c>Bind(binding.Address, 0)</c>
/// 只决定 unicast 源地址，组播从哪张卡出去由 <c>IP_MULTICAST_IF</c> 决定。
/// 不显式设置就会全部走系统默认组播路由，另一张卡所在子网永远收不到 announce。</para>
/// <para>选项值形式已在本机 Windows + .NET 10 上<b>实测</b>确认（见 <c>MulticastInterfaceOption</c> 注释）：
/// 采用 4 字节网络序的接口 IPv4 地址；对地址做 <c>HostToNetworkOrder</c> 或直接传裸接口索引都会抛
/// <c>SocketException</c>。</para>
/// </remarks>
public sealed class MulticastInterfaceOptionTests
{
    [Fact]
    public void ForInterface_ReturnsNetworkOrderAddressBytes()
    {
        // IP_MULTICAST_IF 要求网络序 4 字节；IPAddress.GetAddressBytes 正是这个表示。
        byte[] value = MulticastInterfaceOption.ForInterface(IPAddress.Parse("192.168.1.20"));

        Assert.Equal(new byte[] { 192, 168, 1, 20 }, value);
    }

    [Fact]
    public void ForInterface_ProducesDistinctValuePerBinding()
    {
        // 「每个 sender → 自己的 binding」的前提：不同网卡必须产出不同的选项值。
        byte[] ethernet = MulticastInterfaceOption.ForInterface(IPAddress.Parse("192.168.1.20"));
        byte[] wifi = MulticastInterfaceOption.ForInterface(IPAddress.Parse("192.168.2.33"));

        Assert.Equal(4, ethernet.Length);
        Assert.Equal(4, wifi.Length);
        Assert.NotEqual(ethernet, wifi);
    }

    [Fact]
    public void ForInterface_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => MulticastInterfaceOption.ForInterface(null!));
    }

    [Fact]
    public void ForInterface_RejectsNonIpv4()
    {
        Assert.Throws<ArgumentException>(
            () => MulticastInterfaceOption.ForInterface(IPAddress.IPv6Loopback));
    }

    [Fact]
    public void MulticastInterface_CanBeAppliedToRealSocket()
    {
        // M2.1 的硬要求：至少在 Windows 上确认设置这个 socket option 不抛异常。
        // 本机唯一活跃网卡是 172.100.x.x，但 bind 到 loopback 验证选项本身是可行的。
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        socket.SetSocketOption(
            SocketOptionLevel.IP,
            SocketOptionName.MulticastInterface,
            MulticastInterfaceOption.ForInterface(IPAddress.Loopback));

        // 回读：.NET 把内核返回的 4 字节按小端读成 int，
        // 因此 BitConverter.GetBytes 在小端机器上直接就是网络序的地址字节。
        if (socket.GetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface)
                is int raw)
        {
            byte[] bytes = BitConverter.GetBytes(raw);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            Assert.Equal(IPAddress.Loopback, new IPAddress(bytes));
        }
    }
}
