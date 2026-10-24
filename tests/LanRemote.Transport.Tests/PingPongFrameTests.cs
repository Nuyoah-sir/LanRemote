// 只验证 ping/pong 帧合同：序列化与严格解析、会话关联、echoedSeq 回显；
// 不建立连接、不测时钟、不收发字节，不代表已接 ping loop 或往返时延已用于画质调控。

using System;
using System.Text;
using System.Text.Json;
using Xunit;

namespace LanRemote.Transport.Tests;

public sealed class PingPongFrameTests
{
    private static readonly Guid Session = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Ping_roundtrips_through_strict_serialization()
    {
        PingFrame ping = new(Session, 7, 1234);

        Assert.True(PingFrame.TryParse(ping.Serialize(), Session, out PingFrame? parsed, out string? rejection));
        Assert.Null(rejection);
        Assert.Equal(Session, parsed!.SessionId);
        Assert.Equal(7, parsed.Sequence);
        Assert.Equal(1234, parsed.TimestampMs);
    }

    [Fact]
    public void Pong_roundtrips_and_echoes_the_ping_sequence()
    {
        PongFrame pong = new(Session, 9, 4321, 7);

        Assert.True(PongFrame.TryParse(pong.Serialize(), Session, out PongFrame? parsed, out string? rejection));
        Assert.Null(rejection);
        Assert.Equal(9, parsed!.Sequence);
        Assert.Equal(4321, parsed.TimestampMs);
        Assert.Equal(7, parsed.EchoedSequence);
    }

    [Fact]
    public void Ping_and_pong_are_distinct_types()
    {
        byte[] ping = new PingFrame(Session, 1, 0).Serialize();
        byte[] pong = new PongFrame(Session, 1, 0, 1).Serialize();

        Assert.False(PongFrame.TryParse(ping, Session, out _, out string? pongRejection));
        Assert.Equal("pong-invalid-payload", pongRejection);
        Assert.False(PingFrame.TryParse(pong, Session, out _, out string? pingRejection));
        Assert.Equal("ping-invalid-payload", pingRejection);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4097)]
    public void Oversized_or_empty_payload_is_rejected_before_parsing(int length)
    {
        byte[] payload = length == 0 ? [] : new byte[length];

        Assert.False(PingFrame.TryParse(payload, Session, out _, out string? rejection));
        Assert.Equal("ping-invalid-length", rejection);
        Assert.False(PongFrame.TryParse(payload, Session, out _, out string? pongRejection));
        Assert.Equal("pong-invalid-length", pongRejection);
    }

    [Fact]
    public void Empty_expected_session_is_rejected_as_context_not_payload()
    {
        byte[] ping = new PingFrame(Session, 1, 0).Serialize();

        Assert.False(PingFrame.TryParse(ping, Guid.Empty, out _, out string? rejection));
        Assert.Equal("ping-invalid-context", rejection);
    }

    [Fact]
    public void Mismatched_session_is_rejected()
    {
        byte[] ping = new PingFrame(Session, 1, 0).Serialize();
        byte[] pong = new PongFrame(Session, 1, 0, 1).Serialize();
        Guid other = Guid.Parse("99999999-8888-7777-6666-555555555555");

        Assert.False(PingFrame.TryParse(ping, other, out _, out _));
        Assert.False(PongFrame.TryParse(pong, other, out _, out _));
    }

    [Fact]
    public void Payload_missing_echoed_seq_is_rejected()
    {
        byte[] payload = Encoding.UTF8.GetBytes(
            "{\"type\":\"pong\",\"protocol\":1,\"sessionId\":\"" + Session.ToString("D") +
            "\",\"seq\":1,\"timestampMs\":0}");

        Assert.False(PongFrame.TryParse(payload, Session, out _, out string? rejection));
        Assert.Equal("pong-invalid-payload", rejection);
    }

    [Fact]
    public void Duplicate_field_is_rejected()
    {
        byte[] payload = Encoding.UTF8.GetBytes(
            "{\"type\":\"ping\",\"protocol\":1,\"sessionId\":\"" + Session.ToString("D") +
            "\",\"seq\":1,\"seq\":1,\"timestampMs\":0}");

        Assert.False(PingFrame.TryParse(payload, Session, out _, out string? rejection));
        Assert.Equal("ping-invalid-payload", rejection);
    }

    [Fact]
    public void Negative_or_fractional_numbers_are_rejected()
    {
        byte[] negative = Encoding.UTF8.GetBytes(
            "{\"type\":\"ping\",\"protocol\":1,\"sessionId\":\"" + Session.ToString("D") +
            "\",\"seq\":-1,\"timestampMs\":0}");
        byte[] fractional = Encoding.UTF8.GetBytes(
            "{\"type\":\"pong\",\"protocol\":1,\"sessionId\":\"" + Session.ToString("D") +
            "\",\"seq\":1,\"timestampMs\":0,\"echoedSeq\":1.5}");

        Assert.False(PingFrame.TryParse(negative, Session, out _, out _));
        Assert.False(PongFrame.TryParse(fractional, Session, out _, out _));
    }

    [Fact]
    public void Malformed_json_is_rejected_without_throwing()
    {
        byte[] malformed = Encoding.UTF8.GetBytes("{\"type\":\"ping\"");

        Assert.False(PingFrame.TryParse(malformed, Session, out _, out string? rejection));
        Assert.Equal("ping-invalid-payload", rejection);
    }

    [Fact]
    public void Serialized_payload_stays_far_below_the_budget()
    {
        byte[] pong = new PongFrame(Session, long.MaxValue, long.MaxValue, long.MaxValue).Serialize();

        Assert.True(pong.Length < PingFrame.MaxPayloadByteLength);
        using JsonDocument document = JsonDocument.Parse(pong);
        Assert.Equal("pong", document.RootElement.GetProperty("type").GetString());
    }
}
