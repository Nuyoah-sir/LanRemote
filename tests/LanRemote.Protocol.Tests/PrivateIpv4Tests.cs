using System.Net;
using LanRemote.Discovery.Networking;
using Xunit;

namespace LanRemote.Protocol.Tests;

/// <summary>
/// RFC1918 边界与 IPv4 数学。
/// </summary>
public sealed class PrivateIpv4Tests
{
    [Theory]
    // 10.0.0.0/8
    [InlineData("10.0.0.0", true)]
    [InlineData("10.255.255.255", true)]
    [InlineData("9.255.255.255", false)]
    [InlineData("11.0.0.0", false)]
    // 172.16.0.0/12
    [InlineData("172.16.0.0", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.15.255.255", false)]
    [InlineData("172.32.0.0", false)]
    // 192.168.0.0/16
    [InlineData("192.168.0.0", true)]
    [InlineData("192.168.255.255", true)]
    [InlineData("192.167.255.255", false)]
    [InlineData("192.169.0.0", false)]
    // 非 RFC1918 的“非公网”地址也必须 false
    [InlineData("169.254.1.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("255.255.255.255", false)]
    public void IsPrivate_MatchesRfc1918Boundaries(string address, bool expected)
    {
        Assert.Equal(expected, PrivateIpv4.IsPrivate(IPAddress.Parse(address)));
    }

    [Fact]
    public void IsPrivate_RejectsIpv6AndNull()
    {
        Assert.False(PrivateIpv4.IsPrivate(IPAddress.Parse("2001:db8::1")));
        Assert.False(PrivateIpv4.IsPrivate(null));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.255.255.254", true)]
    [InlineData("128.0.0.1", false)]
    public void IsLoopback_DetectsLoopbackRange(string address, bool expected) =>
        Assert.Equal(expected, PrivateIpv4.IsLoopback(IPAddress.Parse(address)));

    [Theory]
    [InlineData("169.254.1.1", true)]
    [InlineData("169.253.255.255", false)]
    [InlineData("169.255.0.1", false)]
    public void IsLinkLocal_DetectsLinkLocalRange(string address, bool expected) =>
        Assert.Equal(expected, PrivateIpv4.IsLinkLocal(IPAddress.Parse(address)));

    [Theory]
    // 192.168.1.20 / 255.255.255.0 → 192.168.1.255
    [InlineData("192.168.1.20", "255.255.255.0", "192.168.1.255")]
    // 192.168.10.20 / 255.255.254.0 → 192.168.11.255
    [InlineData("192.168.10.20", "255.255.254.0", "192.168.11.255")]
    // 10.1.2.3 / 255.0.0.0 → 10.255.255.255
    [InlineData("10.1.2.3", "255.0.0.0", "10.255.255.255")]
    // /25 的广播不是 .255
    [InlineData("192.168.1.130", "255.255.255.128", "192.168.1.255")]
    [InlineData("192.168.1.20", "255.255.255.128", "192.168.1.127")]
    public void DirectedBroadcast_IsComputedFromRealMask(string address, string mask, string expected)
    {
        Assert.True(Ipv4Math.TryComputeDirectedBroadcast(
            IPAddress.Parse(address),
            IPAddress.Parse(mask),
            out IPAddress? broadcast));

        Assert.Equal(expected, broadcast!.ToString());
    }

    [Fact]
    public void DirectedBroadcast_RejectsMissingMask()
    {
        Assert.False(Ipv4Math.TryComputeDirectedBroadcast(
            IPAddress.Parse("192.168.1.20"),
            null,
            out IPAddress? broadcast));

        Assert.Null(broadcast);
    }

    [Theory]
    [InlineData("255.255.255.0", true)]
    [InlineData("255.255.254.0", true)]
    [InlineData("255.0.0.0", true)]
    [InlineData("255.255.255.128", true)]
    [InlineData("255.255.255.252", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("255.255.255.255", false)]
    // 非连续掩码：不应当被接受
    [InlineData("255.0.255.0", false)]
    public void IsUsableMask_AcceptsOnlyContiguousPrefixMasks(string mask, bool expected) =>
        Assert.Equal(expected, Ipv4Math.IsUsableMask(IPAddress.Parse(mask)));

    [Theory]
    // /23 的关键用例：不能假定 /24
    [InlineData("192.168.10.5", "255.255.254.0", "192.168.11.200", true)]
    [InlineData("192.168.10.5", "255.255.254.0", "192.168.12.1", false)]
    // /25
    [InlineData("192.168.1.130", "255.255.255.128", "192.168.1.200", true)]
    [InlineData("192.168.1.130", "255.255.255.128", "192.168.1.20", false)]
    // /8
    [InlineData("10.1.1.1", "255.0.0.0", "10.200.5.5", true)]
    [InlineData("10.1.1.1", "255.0.0.0", "11.1.1.1", false)]
    public void IsInSameNetwork_UsesRealMask(string local, string mask, string remote, bool expected) =>
        Assert.Equal(
            expected,
            Ipv4Math.IsInSameNetwork(IPAddress.Parse(local), IPAddress.Parse(mask), IPAddress.Parse(remote)));
}
