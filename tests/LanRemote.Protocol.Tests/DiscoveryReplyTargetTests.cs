using System.Net;
using LanRemote.Discovery;
using LanRemote.Discovery.Protocol;
using Xunit;

namespace LanRemote.Protocol.Tests;

/// <summary>
/// probe unicast 回应目标端点的回归测试。
/// </summary>
/// <remarks>
/// <para><b>本轮 M2.1 的阻断项</b>：发送 socket 绑定的是 <c>binding.Address:0</c>，
/// 所以 probe 的 UDP 源端口是随机临时端口。旧代码把 <c>remote</c> 整个当成回应目标，
/// 于是回应被送到 <c>192.168.1.20:53742</c> 这种没人监听的端口上，
/// 表现为「点了刷新，对方就是收不到、列表就是不出现」。</para>
/// <para>LanRemote 的 discovery receiver 永远监听 <see cref="DiscoveryConstants.Port"/>，
/// 因此回应必须是 <c>源地址 : 45872</c>。</para>
/// </remarks>
public sealed class DiscoveryReplyTargetTests
{
    [Fact]
    public void ProbeReply_AlwaysTargetsDiscoveryPort_NotSourcePort()
    {
        // 真实场景里的 probe 来源：地址是对方的私有 IPv4，端口是它的随机临时端口。
        IPEndPoint probeSource = new(IPAddress.Parse("192.168.1.20"), 53742);

        IPEndPoint replyTarget = DiscoveryReplyTarget.ForProbe(probeSource);

        Assert.Equal(IPAddress.Parse("192.168.1.20"), replyTarget.Address);
        Assert.Equal(DiscoveryConstants.Port, replyTarget.Port);

        // 明确断言「不是源端口」：这是本 bug 的核心，用 NotEqual 防止以后写回 remote。
        Assert.NotEqual(53742, replyTarget.Port);

        Assert.Equal(
            new IPEndPoint(IPAddress.Parse("192.168.1.20"), DiscoveryConstants.Port),
            replyTarget);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1024)]
    [InlineData(DiscoveryConstants.Port - 1)]
    [InlineData(DiscoveryConstants.Port + 1)]
    [InlineData(65535)]
    public void ProbeReply_IgnoresAnySourcePort(int sourcePort)
    {
        IPEndPoint probeSource = new(IPAddress.Parse("10.0.0.7"), sourcePort);

        IPEndPoint replyTarget = DiscoveryReplyTarget.ForProbe(probeSource);

        Assert.Equal(DiscoveryConstants.Port, replyTarget.Port);
        Assert.Equal(IPAddress.Parse("10.0.0.7"), replyTarget.Address);
    }

    [Fact]
    public void ProbeReply_KeepsSourceAddressOnly()
    {
        // 端口永远是常量；唯一来自报文的只有地址。
        IPEndPoint probeSource = new(IPAddress.Parse("172.16.5.9"), 40000);

        IPEndPoint replyTarget = DiscoveryReplyTarget.ForProbe(probeSource);

        Assert.Equal(probeSource.Address, replyTarget.Address);
        Assert.NotEqual(probeSource.Port, replyTarget.Port);
    }

    [Fact]
    public void ProbeReply_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => DiscoveryReplyTarget.ForProbe(null!));
    }

    [Fact]
    public void ProbeReply_RejectsNonIpv4()
    {
        IPEndPoint ipv6Source = new(IPAddress.IPv6Loopback, 53742);

        Assert.Throws<ArgumentException>(() => DiscoveryReplyTarget.ForProbe(ipv6Source));
    }
}
