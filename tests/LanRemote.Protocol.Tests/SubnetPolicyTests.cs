using System.Net;
using LanRemote.Discovery.Networking;
using Xunit;

namespace LanRemote.Protocol.Tests;

/// <summary>
/// <see cref="SubnetPolicy"/> 的数据驱动测试。
/// </summary>
/// <remarks>
/// 核心要求：必须按<b>真实掩码</b>比较网络号，
/// 禁止「都是 192.168.x.x 就是同网段」这类近似判断。
/// </remarks>
public sealed class SubnetPolicyTests
{
    private static SubnetPolicy CreatePolicy(params (string Address, string Mask)[] bindings) =>
        new(new FakeBindingProvider(bindings));

    [Theory]
    [InlineData("192.168.1.10", "255.255.255.0", "192.168.1.20", true)]
    [InlineData("192.168.1.10", "255.255.255.0", "192.168.2.20", false)]
    [InlineData("10.1.1.1", "255.0.0.0", "10.200.5.5", true)]
    [InlineData("172.16.1.1", "255.240.0.0", "172.31.200.1", true)]
    [InlineData("172.16.1.1", "255.240.0.0", "172.32.1.1", false)]
    [InlineData("192.168.1.130", "255.255.255.128", "192.168.1.200", true)]
    [InlineData("192.168.1.130", "255.255.255.128", "192.168.1.20", false)]
    [InlineData("192.168.10.5", "255.255.254.0", "192.168.11.200", true)]
    public void IsAllowedPeer_ComparesNetworkNumbersWithRealMask(
        string local,
        string mask,
        string remote,
        bool expected)
    {
        SubnetPolicy policy = CreatePolicy((local, mask));

        Assert.Equal(
            expected,
            policy.IsAllowedPeer(IPAddress.Parse(local), IPAddress.Parse(remote)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("169.254.1.1")]
    [InlineData("203.0.113.5")]
    public void IsAllowedPeer_RejectsNonPrivateRemote(string remote)
    {
        SubnetPolicy policy = CreatePolicy(("192.168.1.10", "255.255.255.0"));

        Assert.False(policy.IsAllowedPeer(IPAddress.Parse("192.168.1.10"), IPAddress.Parse(remote)));
    }

    [Fact]
    public void IsAllowedPeer_RejectsIpv6()
    {
        SubnetPolicy policy = CreatePolicy(("192.168.1.10", "255.255.255.0"));

        Assert.False(policy.IsAllowedPeer(IPAddress.Parse("192.168.1.10"), IPAddress.Parse("fe80::1")));
        Assert.False(policy.IsAllowedPeer(IPAddress.Parse("fe80::1"), IPAddress.Parse("192.168.1.20")));
    }

    [Fact]
    public void IsAllowedPeer_RejectsUnknownLocalAddress()
    {
        // local 地址不在任何本机绑定里 → 无法取到可信掩码 → 拒绝。
        SubnetPolicy policy = CreatePolicy(("192.168.1.10", "255.255.255.0"));

        Assert.False(policy.IsAllowedPeer(IPAddress.Parse("10.0.0.5"), IPAddress.Parse("10.0.0.6")));
    }

    [Fact]
    public void IsAllowedPeer_RejectsNulls()
    {
        SubnetPolicy policy = CreatePolicy(("192.168.1.10", "255.255.255.0"));

        Assert.False(policy.IsAllowedPeer(null!, IPAddress.Parse("192.168.1.20")));
        Assert.False(policy.IsAllowedPeer(IPAddress.Parse("192.168.1.10"), null!));
    }

    [Fact]
    public void IsAllowedPeer_BothPrivateIsNotEnough()
    {
        // 10.x 与 192.168.x 都是 RFC1918，但不是同一个网络。
        SubnetPolicy policy = CreatePolicy(("192.168.1.10", "255.255.255.0"));

        Assert.False(policy.IsAllowedPeer(IPAddress.Parse("192.168.1.10"), IPAddress.Parse("10.1.1.1")));
    }

    [Fact]
    public void IsAllowedPeer_UsesMaskOfTheMatchingBindingOnly()
    {
        // 多网卡：只有匹配 local 的那张卡的掩码参与判断。
        SubnetPolicy policy = CreatePolicy(
            ("192.168.1.10", "255.255.255.0"),
            ("10.1.1.1", "255.0.0.0"));

        Assert.True(policy.IsAllowedPeer(IPAddress.Parse("10.1.1.1"), IPAddress.Parse("10.200.5.5")));
        Assert.False(policy.IsAllowedPeer(IPAddress.Parse("192.168.1.10"), IPAddress.Parse("10.200.5.5")));
    }

    [Fact]
    public void SourceEndpointFilter_RequiresIpv4PrivateAndSameSubnet()
    {
        var bindings = new FakeBindingProvider(("192.168.1.10", "255.255.255.0")).GetBindings();

        Assert.True(SourceEndpointFilter.IsAcceptableSource(IPAddress.Parse("192.168.1.77"), bindings));
        Assert.False(SourceEndpointFilter.IsAcceptableSource(IPAddress.Parse("192.168.2.77"), bindings));
        Assert.False(SourceEndpointFilter.IsAcceptableSource(IPAddress.Parse("8.8.8.8"), bindings));
        Assert.False(SourceEndpointFilter.IsAcceptableSource(IPAddress.Parse("169.254.9.9"), bindings));
        Assert.False(SourceEndpointFilter.IsAcceptableSource(IPAddress.Parse("fe80::1"), bindings));
        Assert.False(SourceEndpointFilter.IsAcceptableSource(null, bindings));
    }
}
