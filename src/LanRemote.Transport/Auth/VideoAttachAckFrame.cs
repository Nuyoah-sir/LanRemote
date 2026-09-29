using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanRemote.Transport.Auth;

internal sealed class VideoAttachAckFrame
{
    internal const string ExpectedType = "video_attach_ack";
    internal const string ExpectedChannel = "video";
    internal const int ExpectedProtocol = 1;
    internal const int MaxPayloadByteLength = 4096;

    // 拒绝短码仅供本地使用，不携带原始载荷。
    internal const string RejectPayloadTooLarge = "video-attach-ack-payload-too-large";
    internal const string RejectMalformedJson = "video-attach-ack-malformed-json";
    internal const string RejectTrailingData = "video-attach-ack-trailing-data";
    internal const string RejectMissingField = "video-attach-ack-missing-field";
    internal const string RejectWrongType = "video-attach-ack-wrong-type";
    internal const string RejectWrongChannel = "video-attach-ack-wrong-channel";
    internal const string RejectWrongProtocol = "video-attach-ack-wrong-protocol";
    internal const string RejectBadSessionId = "video-attach-ack-bad-session-id";

    internal VideoAttachAckFrame(Guid sessionId)
    {
        SessionId = sessionId;
    }

    internal Guid SessionId { get; }

    // 返回 UTF-8 JSON 载荷，不含长度前缀。
    internal byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(
        new VideoAttachAckPayload
        {
            Type = ExpectedType,
            Channel = ExpectedChannel,
            Protocol = ExpectedProtocol,
            SessionId = SessionId.ToString("D"),
        },
        AuthJson.StrictOptions);

    internal static bool TryParse(
        ReadOnlySpan<byte> utf8,
        [NotNullWhen(true)] out VideoAttachAckFrame? frame,
        out string? rejection)
    {
        frame = null;
        if (utf8.Length > MaxPayloadByteLength)
        {
            rejection = RejectPayloadTooLarge;
            return false;
        }

        if (!AuthJson.TryDeserializeStrict(
                utf8, out VideoAttachAckPayload? payload, out rejection,
                RejectMalformedJson, RejectTrailingData,
                ExpectedType, RejectWrongType))
        {
            return false;
        }

        if (payload.Type is null || payload.Channel is null || payload.Protocol is null
            || payload.SessionId is null)
        {
            rejection = RejectMissingField;
            return false;
        }

        if (!string.Equals(payload.Type, ExpectedType, StringComparison.Ordinal))
        {
            rejection = RejectWrongType;
            return false;
        }

        if (!string.Equals(payload.Channel, ExpectedChannel, StringComparison.Ordinal))
        {
            rejection = RejectWrongChannel;
            return false;
        }

        if (payload.Protocol.Value != ExpectedProtocol)
        {
            rejection = RejectWrongProtocol;
            return false;
        }

        if (!CanonicalGuid.TryParse(payload.SessionId, out Guid sessionId))
        {
            rejection = RejectBadSessionId;
            return false;
        }

        frame = new VideoAttachAckFrame(sessionId);
        return true;
    }

    private sealed class VideoAttachAckPayload
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("channel")]
        public string? Channel { get; set; }

        [JsonPropertyName("protocol")]
        public int? Protocol { get; set; }

        [JsonPropertyName("sessionId")]
        public string? SessionId { get; set; }
    }
}
