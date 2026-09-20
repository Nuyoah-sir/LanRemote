using System.Net;
using System.Text;
using LanRemote.Discovery;
using LanRemote.Discovery.Protocol;
using LanRemote.Transport;
using Xunit;

namespace LanRemote.Protocol.Tests;

/// <summary>
/// 发现报文的编解码与畸形输入防御。
/// </summary>
public sealed class DiscoveryPacketCodecTests
{
    private static DiscoveryAnnouncement ValidAnnouncement() => new()
    {
        DeviceId = Guid.NewGuid(),
        DeviceCode = "QPKE-2CPC",
        DeviceName = "DESKTOP-B",
        AppVersion = "0.1.0-m2",
        TcpPort = DiscoveryConstants.ExpectedTransportPort,
        CertSha256 = new string('A', 64),
        Capabilities = new List<string> { "view", "control" },
        Nonce = DiscoveryPacketCodec.NewNonce(),
    };

    [Fact]
    public void Announcement_RoundTrip()
    {
        DiscoveryAnnouncement original = ValidAnnouncement();

        byte[] payload = DiscoveryPacketCodec.EncodeAnnouncement(original);

        Assert.True(DiscoveryPacketCodec.TryDecodeAnnouncement(payload, out DiscoveryAnnouncement? parsed));
        Assert.NotNull(parsed);

        Assert.Equal(original.DeviceId, parsed.DeviceId);
        Assert.Equal(original.DeviceCode, parsed.DeviceCode);
        Assert.Equal(original.DeviceName, parsed.DeviceName);
        Assert.Equal(original.AppVersion, parsed.AppVersion);
        Assert.Equal(original.TcpPort, parsed.TcpPort);
        Assert.Equal(original.CertSha256, parsed.CertSha256);
        Assert.Equal(original.Nonce, parsed.Nonce);
        Assert.Equal(original.Capabilities, parsed.Capabilities);
    }

    [Fact]
    public void Announcement_UsesCamelCaseKeys()
    {
        byte[] payload = DiscoveryPacketCodec.EncodeAnnouncement(ValidAnnouncement());
        string json = Encoding.UTF8.GetString(payload);

        Assert.Contains("\"magic\"", json);
        Assert.Contains("\"deviceId\"", json);
        Assert.Contains("\"deviceCode\"", json);
        Assert.Contains("\"certSha256\"", json);
        Assert.Contains("\"tcpPort\"", json);
        Assert.DoesNotContain("\"DeviceId\"", json);
    }

    [Fact]
    public void Announcement_IsWithinProtocolSizeLimit()
    {
        byte[] payload = DiscoveryPacketCodec.EncodeAnnouncement(ValidAnnouncement());

        Assert.True(payload.Length <= DiscoveryConstants.MaxAnnouncementBytes);
    }

    [Fact]
    public void Probe_RoundTrip()
    {
        DiscoveryProbe original = new() { Nonce = DiscoveryPacketCodec.NewNonce() };

        byte[] payload = DiscoveryPacketCodec.EncodeProbe(original);

        Assert.True(DiscoveryPacketCodec.TryDecodeProbe(payload, out DiscoveryProbe? parsed));
        Assert.NotNull(parsed);
        Assert.Equal(DiscoveryConstants.ProbeType, parsed.Type);
        Assert.Equal(original.Nonce, parsed.Nonce);
    }

    [Fact]
    public void PeekType_DistinguishesAnnounceAndProbe()
    {
        Assert.True(DiscoveryPacketCodec.TryPeekType(
            DiscoveryPacketCodec.EncodeAnnouncement(ValidAnnouncement()), out string? announceType));
        Assert.Equal(DiscoveryConstants.AnnounceType, announceType);

        Assert.True(DiscoveryPacketCodec.TryPeekType(
            DiscoveryPacketCodec.EncodeProbe(new DiscoveryProbe { Nonce = DiscoveryPacketCodec.NewNonce() }),
            out string? probeType));
        Assert.Equal(DiscoveryConstants.ProbeType, probeType);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void EmptyOrTinyPayload_IsRejected(int length)
    {
        byte[] payload = new byte[length];

        Assert.False(DiscoveryPacketCodec.TryDecodeAnnouncement(payload, out _));
        Assert.False(DiscoveryPacketCodec.TryDecodeProbe(payload, out _));
        Assert.False(DiscoveryPacketCodec.TryPeekType(payload, out _));
    }

    [Fact]
    public void OversizedPayload_IsRejected()
    {
        byte[] payload = new byte[DiscoveryConstants.MaxAnnouncementBytes + 1];
        Array.Fill(payload, (byte)'A');

        Assert.False(DiscoveryPacketCodec.TryDecodeAnnouncement(payload, out _));
        Assert.False(DiscoveryPacketCodec.TryDecodeProbe(payload, out _));
    }

    [Fact]
    public void RandomBytes_AreRejected()
    {
        byte[] payload = new byte[256];
        Random.Shared.NextBytes(payload);

        Assert.False(DiscoveryPacketCodec.TryDecodeAnnouncement(payload, out _));
        Assert.False(DiscoveryPacketCodec.TryPeekType(payload, out _));
    }

    [Fact]
    public void InvalidUtf8_IsRejected()
    {
        // 0xC3 0x28 不是合法 UTF-8 序列。
        byte[] payload = new byte[] { 0x7B, 0x22, 0x74, 0x79, 0x70, 0x65, 0x22, 0x3A, 0xC3, 0x28, 0x7D };

        Assert.False(DiscoveryPacketCodec.TryDecodeAnnouncement(payload, out _));
        Assert.False(DiscoveryPacketCodec.TryPeekType(payload, out _));
    }

    [Fact]
    public void TruncatedJson_IsRejected()
    {
        byte[] payload = Encoding.UTF8.GetBytes("{\"magic\":\"LANREMOTE\",\"type\":");

        Assert.False(DiscoveryPacketCodec.TryDecodeAnnouncement(payload, out _));
        Assert.False(DiscoveryPacketCodec.TryPeekType(payload, out _));
    }

    [Fact]
    public void EmptyJsonObject_HasNoType()
    {
        byte[] payload = Encoding.UTF8.GetBytes("{}");

        // 形态上能解析，但读不出 type → 应当被丢弃，绝不进入业务处理。
        Assert.False(DiscoveryPacketCodec.TryPeekType(payload, out _));
    }

    [Fact]
    public void NonObjectJson_IsRejected()
    {
        byte[] payload = Encoding.UTF8.GetBytes("[1,2,3]");

        Assert.False(DiscoveryPacketCodec.TryPeekType(payload, out _));
    }

    [Fact]
    public void NewNonce_IsTwelveBytesOfBase64()
    {
        string nonce = DiscoveryPacketCodec.NewNonce();

        byte[] decoded = Convert.FromBase64String(nonce);
        Assert.Equal(DiscoveryConstants.NonceByteCount, decoded.Length);
        Assert.True(DiscoveryPacketCodec.IsWellFormedNonce(nonce));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("not base64!!!", false)]
    [InlineData("AAAA", false)]
    [InlineData(null, false)]
    public void IsWellFormedNonce_RejectsBadInput(string? nonce, bool expected) =>
        Assert.Equal(expected, DiscoveryPacketCodec.IsWellFormedNonce(nonce));

    [Fact]
    public void NoncesAreUnique()
    {
        HashSet<string> nonces = new(StringComparer.Ordinal);

        for (int i = 0; i < 200; i++)
        {
            Assert.True(nonces.Add(DiscoveryPacketCodec.NewNonce()));
        }
    }

    [Fact]
    public void PortsDoNotDrift()
    {
        // §9 要求的护栏：UDP announcement 声明的端口必须等于未来的 TLS 端口。
        Assert.Equal(TransportConstants.Port, DiscoveryConstants.ExpectedTransportPort);
        Assert.Equal(45872, DiscoveryConstants.Port);
        Assert.Equal(45873, DiscoveryConstants.ExpectedTransportPort);
    }

    [Fact]
    public void ReceiverBufferIsOneByteLargerThanProtocolLimit()
    {
        Assert.Equal(
            DiscoveryConstants.MaxAnnouncementBytes + 1,
            DiscoveryConstants.ReceiverBufferBytes);
    }

    [Fact]
    public void MulticastConfigurationIsUnchanged()
    {
        Assert.Equal("239.255.77.77", DiscoveryConstants.MulticastGroupAddress);
        Assert.Equal(1, DiscoveryConstants.MulticastTimeToLive);
        Assert.Equal(2000, DiscoveryConstants.AnnounceIntervalMs);
        Assert.Equal(7000, DiscoveryConstants.DeviceCacheTtlMs);
        Assert.Equal("LANREMOTE", DiscoveryConstants.Magic);
        Assert.Equal(1, DiscoveryConstants.ProtocolVersion);
        Assert.Equal(2048, DiscoveryConstants.MaxAnnouncementBytes);
        Assert.Equal("239.255.77.77", IPAddress.Parse(DiscoveryConstants.MulticastGroupAddress).ToString());
    }
}
