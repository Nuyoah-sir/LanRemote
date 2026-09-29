using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanRemote.Transport.Auth;

/// <summary>ADR-048 视频首帧的内部纯解析器；不改变 Control 入口的放行规则。</summary>
internal sealed class VideoHelloFrame
{
    internal const string ExpectedType = "channel_hello";
    internal const string ExpectedChannel = "video";
    internal const int ExpectedProtocol = 1;
    internal const int MaxPayloadByteLength = 4096;

    // 拒绝短码仅供本地使用，不携带原始载荷或证明。
    internal const string RejectPayloadTooLarge = "video-hello-payload-too-large";
    internal const string RejectMalformedJson = "video-hello-malformed-json";
    internal const string RejectTrailingData = "video-hello-trailing-data";
    internal const string RejectMissingField = "video-hello-missing-field";
    internal const string RejectWrongType = "video-hello-wrong-type";
    internal const string RejectWrongChannel = "video-hello-wrong-channel";
    internal const string RejectWrongProtocol = "video-hello-wrong-protocol";
    internal const string RejectBadSessionId = "video-hello-bad-session-id";
    internal const string RejectBadAttachNonce = "video-hello-bad-attach-nonce";
    internal const string RejectBadAttachProof = "video-hello-bad-attach-proof";

    private readonly byte[] _attachNonce;
    private readonly byte[] _attachProof;

    internal VideoHelloFrame(Guid sessionId, ReadOnlySpan<byte> attachNonce, ReadOnlySpan<byte> attachProof)
    {
        if (attachNonce.Length != VideoAttachProof.NonceByteLength)
        {
            throw new ArgumentException("attachNonce 必须是 16 字节。", nameof(attachNonce));
        }

        if (attachProof.Length != VideoAttachProof.ProofByteLength)
        {
            throw new ArgumentException("attachProof 必须是 32 字节。", nameof(attachProof));
        }

        SessionId = sessionId;
        _attachNonce = attachNonce.ToArray();
        _attachProof = attachProof.ToArray();
    }

    internal Guid SessionId { get; }
    internal ReadOnlyMemory<byte> AttachNonce => _attachNonce;
    internal ReadOnlyMemory<byte> AttachProof => _attachProof;

    /// <summary>返回 UTF-8 JSON 载荷，不含长度前缀。</summary>
    internal byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(
        new VideoHelloPayload
        {
            Type = ExpectedType,
            Channel = ExpectedChannel,
            Protocol = ExpectedProtocol,
            SessionId = SessionId.ToString("D"),
            AttachNonce = Convert.ToBase64String(_attachNonce),
            AttachProof = Convert.ToBase64String(_attachProof),
        },
        AuthJson.StrictOptions);

    internal static bool TryParse(
        ReadOnlySpan<byte> utf8,
        [NotNullWhen(true)] out VideoHelloFrame? frame,
        out string? rejection)
    {
        frame = null;
        if (utf8.Length > MaxPayloadByteLength)
        {
            rejection = RejectPayloadTooLarge;
            return false;
        }

        if (!AuthJson.TryDeserializeStrict(
                utf8, out VideoHelloPayload? payload, out rejection,
                RejectMalformedJson, RejectTrailingData,
                ExpectedType, RejectWrongType))
        {
            return false;
        }

        if (payload.Type is null || payload.Channel is null || payload.Protocol is null
            || payload.SessionId is null || payload.AttachNonce is null || payload.AttachProof is null)
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

        if (!CanonicalBase64.TryDecode(payload.AttachNonce, out byte[] attachNonce)
            || attachNonce.Length != VideoAttachProof.NonceByteLength)
        {
            rejection = RejectBadAttachNonce;
            return false;
        }

        if (!CanonicalBase64.TryDecode(payload.AttachProof, out byte[] attachProof)
            || attachProof.Length != VideoAttachProof.ProofByteLength)
        {
            rejection = RejectBadAttachProof;
            return false;
        }

        frame = new VideoHelloFrame(sessionId, attachNonce, attachProof);
        return true;
    }

    private sealed class VideoHelloPayload
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("channel")]
        public string? Channel { get; set; }

        [JsonPropertyName("protocol")]
        public int? Protocol { get; set; }

        [JsonPropertyName("sessionId")]
        public string? SessionId { get; set; }

        [JsonPropertyName("attachNonce")]
        public string? AttachNonce { get; set; }

        [JsonPropertyName("attachProof")]
        public string? AttachProof { get; set; }
    }
}
