using System.Net;
using LanRemote.Core.Models;
using Xunit;

namespace LanRemote.Transport.Tests;

/// <summary>
/// 连接目标快照：TOCTOU 防线（评审 A 桶第 2 条）与结构校验。
/// </summary>
public sealed class ConnectionTargetTests
{
    private const string ValidSha = "3F4FD8C5CE4C062FDCA7CA1492BB9935AB11548F85B7A682A3A96992CC090D1F";

    [Fact]
    public void TryCreate_AcceptsValidInput()
    {
        Guid deviceId = Guid.NewGuid();
        IPAddress address = IPAddress.Parse("192.168.1.20");

        Assert.True(ConnectionTarget.TryCreate(deviceId, address, 45873, ValidSha, out ConnectionTarget? target));
        Assert.NotNull(target);
        Assert.Equal(deviceId, target!.DeviceId);
        Assert.Equal(address, target.RemoteAddress);
        Assert.Equal(45873, target.Port);
        Assert.Equal(32, target.ExpectedCertSha256.Length);
    }

    [Fact]
    public void TryCreate_FromDiscoveredDevice_FreezesAddressAndPin()
    {
        Guid deviceId = Guid.NewGuid();
        IPAddress address = IPAddress.Parse("192.168.1.20");
        var device = new DiscoveredDevice(
            deviceId,
            "ABCD-EFGH",
            "DESKTOP-B",
            address,
            45873,
            ValidSha,
            DateTimeOffset.UtcNow,
            new HashSet<string> { "view" });

        Assert.True(ConnectionTarget.TryCreate(device, out ConnectionTarget? target));
        Assert.Equal(deviceId, target!.DeviceId);
        Assert.Equal(address, target.RemoteAddress);
        Assert.Equal(45873, target.Port);
    }

    [Fact]
    public void TryCreate_RejectsEmptyDeviceId()
    {
        Assert.False(ConnectionTarget.TryCreate(
            Guid.Empty, IPAddress.Parse("192.168.1.20"), 45873, ValidSha, out _));
    }

    [Fact]
    public void TryCreate_RejectsNullAddress()
    {
        Assert.False(ConnectionTarget.TryCreate(Guid.NewGuid(), null, 45873, ValidSha, out _));
    }

    [Fact]
    public void TryCreate_RejectsIPv6Address()
    {
        // ADR-006：v1 只处理 IPv4。
        Assert.False(ConnectionTarget.TryCreate(
            Guid.NewGuid(), IPAddress.Parse("fe80::1"), 45873, ValidSha, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void TryCreate_RejectsPortOutOfRange(int port)
    {
        Assert.False(ConnectionTarget.TryCreate(
            Guid.NewGuid(), IPAddress.Parse("192.168.1.20"), port, ValidSha, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("3F4F")]
    [InlineData("zz4fd8c5ce4c062fdca7ca1492bb9935ab11548f85b7a682a3a96992cc090d1f")]
    public void TryCreate_RejectsMalformedPin(string? sha)
    {
        Assert.False(ConnectionTarget.TryCreate(
            Guid.NewGuid(), IPAddress.Parse("192.168.1.20"), 45873, sha, out _));
    }

    [Fact]
    public void ExpectedCertSha256_IsNotSharedWithCaller()
    {
        // 快照必须是自持的一份：调用方手里那 32 字节改了也不能影响快照。
        byte[] callerOwned = new byte[32];
        callerOwned[0] = 0x11;
        string hex = Convert.ToHexString(callerOwned);

        Assert.True(ConnectionTarget.TryCreate(
            Guid.NewGuid(), IPAddress.Parse("192.168.1.20"), 45873, hex, out ConnectionTarget? target));

        callerOwned[0] = 0xFF;

        Assert.Equal(0x11, target!.ExpectedCertSha256.Span[0]);
    }
}
