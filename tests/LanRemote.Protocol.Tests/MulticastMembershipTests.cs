using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LanRemote.Discovery;
using LanRemote.Discovery.Networking;
using Microsoft.Extensions.Logging;

namespace LanRemote.Protocol.Tests;

public sealed class MulticastMembershipTests
{
    private static NetworkBinding Binding(string id, int index, string address) => new(
        id, "test-interface", NetworkInterfaceType.Wireless80211,
        IPAddress.Parse(address), IPAddress.Parse("255.255.255.0"),
        IPAddress.Parse("192.168.137.255"), index);

    [Fact]
    public void SameInterfaceWithTwoAddresses_JoinsOnce_WithoutDroppingAddressBindings()
    {
        NetworkBinding[] bindings = [Binding("wifi", 23, "192.168.137.141"), Binding("wifi", 23, "192.168.1.20")];
        List<NetworkBinding> calls = [];
        MulticastMembership.JoinUniqueInterfaces(bindings, calls.Add);
        Assert.Same(bindings[0], Assert.Single(calls));
        Assert.Equal(2, bindings.Length);
        Assert.Equal("192.168.1.20", bindings[1].Address.ToString());
        Assert.True(SourceEndpointFilter.IsAcceptableSource(IPAddress.Parse("192.168.1.21"), bindings));
    }

    [Fact]
    public void SeparateInterfaces_EachJoinOnce_InFirstSeenOrder()
    {
        NetworkBinding[] bindings = [Binding("wifi", 23, "192.168.137.141"),
            Binding("ethernet", 6, "10.1.1.2"), Binding("WIFI", 23, "192.168.1.20")];
        List<NetworkBinding> calls = [];
        MulticastMembership.JoinUniqueInterfaces(bindings, calls.Add);
        Assert.Equal(new[] { 23, 6 }, calls.Select(binding => binding.InterfaceIndex));
    }

    [Theory]
    [InlineData("wifi", 0)]
    [InlineData("wifi", -1)]
    [InlineData("", 23)]
    [InlineData(" ", 23)]
    public void MissingInterfaceIdentity_FailsBeforeAnyJoin(string id, int index)
    {
        int calls = 0;
        Assert.Throws<InvalidOperationException>(() => MulticastMembership.JoinUniqueInterfaces(
            [Binding("valid", 1, "10.1.1.1"), Binding(id, index, "10.2.2.2")], _ => calls++));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("second", 23)]
    [InlineData("first", 24)]
    public void ConflictingIdentity_FailsBeforeAnyJoin(string secondId, int secondIndex)
    {
        int calls = 0;
        Assert.Throws<InvalidOperationException>(() => MulticastMembership.JoinUniqueInterfaces(
            [Binding("first", 23, "10.1.1.1"), Binding(secondId, secondIndex, "10.2.2.2")], _ => calls++));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void JoinFailure_IsNotSwallowed_AndLaterInterfacesAreNotJoined()
    {
        SocketException expected = new(10022);
        int calls = 0;
        SocketException actual = Assert.Throws<SocketException>(() => MulticastMembership.JoinUniqueInterfaces(
            [Binding("a", 1, "10.1.1.1"), Binding("b", 2, "10.2.2.2")], _ =>
            {
                calls++;
                throw expected;
            }));
        Assert.Same(expected, actual);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void EmptyBindings_DoNotJoin()
    {
        MulticastMembership.JoinUniqueInterfaces([], _ => throw new InvalidOperationException("unexpected join"));
    }

    [Fact]
    public void RealReceiver_DuplicateLoopbackBinding_UsesOneMembership()
    {
        // 只建回环 socket，不发广播、不改网卡。绕过生产筛选仅为了测试同一个生产 receiver 工厂。
        NetworkInterface loopback = NetworkInterface.GetAllNetworkInterfaces().First(nic =>
            nic.NetworkInterfaceType == NetworkInterfaceType.Loopback && nic.Supports(NetworkInterfaceComponent.IPv4));
        NetworkBinding binding = Binding(loopback.Id, loopback.GetIPProperties().GetIPv4Properties()!.Index, "127.0.0.1");
        RecordingLogger logger = new();
        IPAddress group = IPAddress.Parse(DiscoveryConstants.MulticastGroupAddress);
        using Socket receiver = LanDiscoveryService.CreateReceiver([binding, binding], group, 0, logger);
        Assert.True(receiver.IsBound);
        Assert.Single(logger.Messages, message => message.Contains("加入成功", StringComparison.Ordinal));
        // 再次对同一 socket 加同接口同组必须失败；这里是真实 OS 调用，不是故障注入。
        Assert.Throws<SocketException>(() => receiver.SetSocketOption(SocketOptionLevel.IP,
            SocketOptionName.AddMembership, new MulticastOption(group, binding.Address)));
    }

    [Fact]
    public void RealReceiver_MembershipFailure_LogsInterface_AndReleasesPort()
    {
        using Socket reservation = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)reservation.LocalEndPoint!).Port;
        reservation.Dispose();
        RecordingLogger logger = new();
        // 非组播地址会使真实 AddMembership 失败；不依赖机器有没有某个私有地址。
        Assert.Throws<SocketException>(() => LanDiscoveryService.CreateReceiver(
            [Binding("loop", 1, "127.0.0.1")], IPAddress.Loopback, port, logger));
        Assert.Single(logger.Messages, message => message.Contains("加入失败", StringComparison.Ordinal)
            && message.Contains("ifIndex=1", StringComparison.Ordinal)
            && message.Contains("address=127.0.0.1", StringComparison.Ordinal)
            && message.Contains("nativeError=", StringComparison.Ordinal));
        using Socket replacement = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        replacement.ExclusiveAddressUse = true;
        replacement.Bind(new IPEndPoint(IPAddress.Any, port));
        Assert.True(replacement.IsBound);
    }

    private sealed class RecordingLogger : ILogger
    {
        internal List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
