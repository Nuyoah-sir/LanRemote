using System.Net;
using LanRemote.Core.Identity;
using LanRemote.Core.Models;
using LanRemote.Discovery;
using LanRemote.Discovery.Protocol;
using Xunit;

namespace LanRemote.Protocol.Tests;

/// <summary>
/// 公告的严格校验、来源地址权威与自公告去重。
/// </summary>
public sealed class DiscoveryAnnouncementEvaluatorTests
{
    private const string ValidFingerprint = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    private readonly DiscoveryAnnouncementEvaluator _evaluator = new();

    private static readonly Guid LocalDeviceId = Guid.NewGuid();

    private static DiscoveryRuntimeSnapshot LocalSnapshot() =>
        new(
            LocalDeviceId,
            DeviceCode.Derive(LocalDeviceId),
            "LOCAL-PC",
            "0.1.0-m2",
            ValidFingerprint,
            AllowDiscovery: true,
            AllowViewing: true,
            AllowControl: true);

    private static DiscoveryAnnouncement ValidAnnouncement(Guid deviceId) => new()
    {
        DeviceId = deviceId,
        DeviceCode = DeviceCode.Derive(deviceId),
        DeviceName = "REMOTE-PC",
        AppVersion = "0.1.0-m2",
        TcpPort = DiscoveryConstants.ExpectedTransportPort,
        CertSha256 = ValidFingerprint,
        Capabilities = new List<string> { "view", "control" },
        Nonce = DiscoveryPacketCodec.NewNonce(),
    };

    private bool Evaluate(
        DiscoveryAnnouncement announcement,
        string sourceAddress,
        out DiscoveredDevice? device,
        out string? reason) =>
        _evaluator.TryEvaluate(
            announcement,
            IPAddress.Parse(sourceAddress),
            LocalSnapshot(),
            DateTimeOffset.UtcNow,
            out device,
            out reason);

    [Fact]
    public void ValidAnnouncement_IsAccepted()
    {
        Guid remoteId = Guid.NewGuid();

        Assert.True(Evaluate(ValidAnnouncement(remoteId), "192.168.1.55", out DiscoveredDevice? device, out _));

        Assert.NotNull(device);
        Assert.Equal(remoteId, device.DeviceId);
        Assert.Equal("REMOTE-PC", device.DeviceName);
        Assert.Equal(DiscoveryConstants.ExpectedTransportPort, device.Port);
    }

    [Fact]
    public void DeviceAddress_ComesFromUdpSourceNotPayload()
    {
        // §14 / §50：payload 里根本没有地址字段，结果必须等于 UDP source。
        Guid remoteId = Guid.NewGuid();

        Assert.True(Evaluate(ValidAnnouncement(remoteId), "192.168.1.55", out DiscoveredDevice? device, out _));

        Assert.Equal("192.168.1.55", device!.Address.ToString());
    }

    [Fact]
    public void DeviceCode_IsTakenFromDeviceIdNotFromPayload()
    {
        Guid remoteId = Guid.NewGuid();
        DiscoveryAnnouncement announcement = ValidAnnouncement(remoteId);
        announcement.DeviceCode = "ZZZZ-ZZZZ";

        Assert.False(Evaluate(announcement, "192.168.1.55", out _, out string? reason));
        Assert.Equal("deviceCode 与 deviceId 不一致", reason);
    }

    [Fact]
    public void LowercaseDeviceCode_IsAccepted()
    {
        Guid remoteId = Guid.NewGuid();
        DiscoveryAnnouncement announcement = ValidAnnouncement(remoteId);
        announcement.DeviceCode = announcement.DeviceCode.ToLowerInvariant();

        Assert.True(Evaluate(announcement, "192.168.1.55", out DiscoveredDevice? device, out _));
        Assert.Equal(DeviceCode.Derive(remoteId), device!.DeviceCode);
    }

    [Fact]
    public void SelfAnnouncement_IsDropped()
    {
        // §31 / §51：即使其余字段全部合法，本机自己的包绝不能进列表。
        DiscoveryAnnouncement announcement = ValidAnnouncement(LocalDeviceId);

        Assert.False(Evaluate(announcement, "192.168.1.55", out _, out string? reason));
        Assert.Equal("本机自公告", reason);
    }

    [Theory]
    [InlineData("LANREMOT")]
    [InlineData("lanremote")]
    [InlineData("OTHER")]
    [InlineData("")]
    public void WrongMagic_IsDropped(string magic)
    {
        DiscoveryAnnouncement announcement = ValidAnnouncement(Guid.NewGuid());
        announcement.Magic = magic;

        Assert.False(Evaluate(announcement, "192.168.1.55", out _, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(99)]
    public void WrongProtocol_IsDropped(int protocol)
    {
        DiscoveryAnnouncement announcement = ValidAnnouncement(Guid.NewGuid());
        announcement.Protocol = protocol;

        Assert.False(Evaluate(announcement, "192.168.1.55", out _, out _));
    }

    [Fact]
    public void WrongType_IsDropped()
    {
        DiscoveryAnnouncement announcement = ValidAnnouncement(Guid.NewGuid());
        announcement.Type = DiscoveryConstants.ProbeType;

        Assert.False(Evaluate(announcement, "192.168.1.55", out _, out _));
    }

    [Fact]
    public void EmptyDeviceId_IsDropped()
    {
        DiscoveryAnnouncement announcement = ValidAnnouncement(Guid.NewGuid());
        announcement.DeviceId = Guid.Empty;

        Assert.False(Evaluate(announcement, "192.168.1.55", out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NOT-A-CODE")]
    [InlineData("QPKE2CPC")]
    public void MalformedDeviceCode_IsDropped(string deviceCode)
    {
        DiscoveryAnnouncement announcement = ValidAnnouncement(Guid.NewGuid());
        announcement.DeviceCode = deviceCode;

        Assert.False(Evaluate(announcement, "192.168.1.55", out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyDeviceName_IsDropped(string name)
    {
        DiscoveryAnnouncement announcement = ValidAnnouncement(Guid.NewGuid());
        announcement.DeviceName = name;

        Assert.False(Evaluate(announcement, "192.168.1.55", out _, out _));
    }

    [Fact]
    public void OversizedDeviceName_IsDropped()
    {
        DiscoveryAnnouncement announcement = ValidAnnouncement(Guid.NewGuid());
        announcement.DeviceName = new string('N', DiscoveryConstants.MaxDeviceNameLength + 1);

        Assert.False(Evaluate(announcement, "192.168.1.55", out _, out string? reason));
        Assert.Equal("deviceName 过长", reason);
    }

    [Fact]
    public void MaxLengthDeviceName_IsAccepted()
    {
        DiscoveryAnnouncement announcement = ValidAnnouncement(Guid.NewGuid());
        announcement.DeviceName = new string('N', DiscoveryConstants.MaxDeviceNameLength);

        Assert.True(Evaluate(announcement, "192.168.1.55", out _, out _));
    }

    [Fact]
    public void DeviceNameWithControlCharacter_IsDropped()
    {
        DiscoveryAnnouncement announcement = ValidAnnouncement(Guid.NewGuid());
        announcement.DeviceName = "EVIL\u0007NAME";

        Assert.False(Evaluate(announcement, "192.168.1.55", out _, out string? reason));
        Assert.Equal("deviceName 含控制字符", reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(22)]
    [InlineData(80)]
    [InlineData(45872)]
    [InlineData(45874)]
    public void WrongTcpPort_IsDropped(int port)
    {
        DiscoveryAnnouncement announcement = ValidAnnouncement(Guid.NewGuid());
        announcement.TcpPort = port;

        Assert.False(Evaluate(announcement, "192.168.1.55", out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABC")]
    [InlineData("ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDE")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEG")]
    public void InvalidFingerprint_IsDropped(string fingerprint)
    {
        DiscoveryAnnouncement announcement = ValidAnnouncement(Guid.NewGuid());
        announcement.CertSha256 = fingerprint;

        Assert.False(Evaluate(announcement, "192.168.1.55", out _, out _));
    }

    [Fact]
    public void LowercaseFingerprint_IsNormalizedToUppercase()
    {
        DiscoveryAnnouncement announcement = ValidAnnouncement(Guid.NewGuid());
        announcement.CertSha256 = ValidFingerprint.ToLowerInvariant();

        Assert.True(Evaluate(announcement, "192.168.1.55", out DiscoveredDevice? device, out _));
        Assert.Equal(ValidFingerprint, device!.CertificateSha256);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64")]
    [InlineData("AAAA")]
    [InlineData("AAAAAAAAAAAAAAAAAAAA")]
    public void InvalidNonce_IsDropped(string nonce)
    {
        DiscoveryAnnouncement announcement = ValidAnnouncement(Guid.NewGuid());
        announcement.Nonce = nonce;

        Assert.False(Evaluate(announcement, "192.168.1.55", out _, out _));
    }

    [Fact]
    public void TooManyCapabilities_IsDropped()
    {
        DiscoveryAnnouncement announcement = ValidAnnouncement(Guid.NewGuid());
        announcement.Capabilities = Enumerable
            .Range(0, DiscoveryConstants.MaxCapabilities + 1)
            .Select(i => $"cap{i}")
            .ToList();

        Assert.False(Evaluate(announcement, "192.168.1.55", out _, out string? reason));
        Assert.Equal("capabilities 非法", reason);
    }

    [Fact]
    public void OversizedCapability_IsDropped()
    {
        DiscoveryAnnouncement announcement = ValidAnnouncement(Guid.NewGuid());
        announcement.Capabilities = new List<string>
        {
            new string('c', DiscoveryConstants.MaxCapabilityLength + 1),
        };

        Assert.False(Evaluate(announcement, "192.168.1.55", out _, out _));
    }

    [Fact]
    public void UnknownCapabilities_AreAllowed()
    {
        DiscoveryAnnouncement announcement = ValidAnnouncement(Guid.NewGuid());
        announcement.Capabilities = new List<string> { "view", "future-capability", "control" };

        Assert.True(Evaluate(announcement, "192.168.1.55", out DiscoveredDevice? device, out _));
        Assert.Contains("future-capability", device!.Capabilities);
    }

    [Fact]
    public void Capabilities_ValidDuplicates_AreDeduped()
    {
        // 合法项重复出现是允许的，只做去重；首尾普通空格允许 Trim。
        DiscoveryAnnouncement announcement = ValidAnnouncement(Guid.NewGuid());
        announcement.Capabilities = new List<string> { "view", "view", "control", " control " };

        Assert.True(Evaluate(announcement, "192.168.1.55", out DiscoveredDevice? device, out _));
        Assert.Equal(2, device!.Capabilities.Count);
        Assert.Contains("view", device.Capabilities);
        Assert.Contains("control", device.Capabilities);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\r")]
    public void Capabilities_WhitespaceEntry_IsRejected(string entry)
    {
        // 空白 capability 必须拒绝，而不是静默跳过：
        // 对端声称自己有一个「空白能力」本身就是畸形报文。
        DiscoveryAnnouncement announcement = ValidAnnouncement(Guid.NewGuid());
        announcement.Capabilities = new List<string> { "view", entry };

        Assert.False(Evaluate(announcement, "192.168.1.55", out _, out string? reason));
        Assert.Equal("capabilities 非法", reason);
    }

    [Fact]
    public void Capabilities_NullEntry_IsRejected()
    {
        DiscoveryAnnouncement announcement = ValidAnnouncement(Guid.NewGuid());
        announcement.Capabilities = new List<string> { "view", null! };

        Assert.False(Evaluate(announcement, "192.168.1.55", out _, out string? reason));
        Assert.Equal("capabilities 非法", reason);
    }

    [Theory]
    [InlineData("evil\nfake-log")]
    [InlineData("abc\rxyz")]
    [InlineData("abc\txyz")]
    [InlineData("view\n")]
    public void Capabilities_ControlCharacter_IsRejected(string entry)
    {
        // capability 会在「第一次发现设备」时进日志，
        // 允许 \n / \r 就等于让局域网报文可以伪造日志行。
        DiscoveryAnnouncement announcement = ValidAnnouncement(Guid.NewGuid());
        announcement.Capabilities = new List<string> { "view", entry };

        Assert.False(Evaluate(announcement, "192.168.1.55", out _, out string? reason));
        Assert.Equal("capabilities 非法", reason);
    }

    [Fact]
    public void Capabilities_RawCountAboveLimit_IsRejectedEvenWhenDuplicates()
    {
        // 关键：上限按「去重前的原始条数」判定。
        // 这 17 项全是 "view"，去重后只剩 1 项，但 raw.Count=17 已超限 → 必须拒绝，
        // 否则「最多 16 项」这条约束可以被重复项无限绕过。
        DiscoveryAnnouncement announcement = ValidAnnouncement(Guid.NewGuid());
        announcement.Capabilities =
            Enumerable.Repeat("view", DiscoveryConstants.MaxCapabilities + 1).ToList();

        Assert.False(Evaluate(announcement, "192.168.1.55", out _, out string? reason));
        Assert.Equal("capabilities 非法", reason);
    }

    [Fact]
    public void Capabilities_ExactlyMaxEntries_IsAccepted()
    {
        DiscoveryAnnouncement announcement = ValidAnnouncement(Guid.NewGuid());
        announcement.Capabilities = Enumerable
            .Range(0, DiscoveryConstants.MaxCapabilities)
            .Select(i => $"cap{i}")
            .ToList();

        Assert.True(Evaluate(announcement, "192.168.1.55", out DiscoveredDevice? device, out _));
        Assert.Equal(DiscoveryConstants.MaxCapabilities, device!.Capabilities.Count);
    }

    [Fact]
    public void EmptyCapabilities_IsAccepted()
    {
        DiscoveryAnnouncement announcement = ValidAnnouncement(Guid.NewGuid());
        announcement.Capabilities = new List<string>();

        Assert.True(Evaluate(announcement, "192.168.1.55", out DiscoveredDevice? device, out _));
        Assert.Empty(device!.Capabilities);
    }

    [Fact]
    public void NullAnnouncement_IsDropped()
    {
        Assert.False(_evaluator.TryEvaluate(
            null!,
            IPAddress.Parse("192.168.1.55"),
            LocalSnapshot(),
            DateTimeOffset.UtcNow,
            out _,
            out string? reason));

        Assert.Equal("空公告", reason);
    }
}
