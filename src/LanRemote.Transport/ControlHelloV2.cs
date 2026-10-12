using System.Text.Json;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport;

/// <summary>独立 Control Hello2 纯载荷，不改现行 V1 首帧入口或共享版本常量。</summary>
/// <remarks>只在整份严格合同通过后分类版本。LegacyV1 不意味着认证或降级许可。</remarks>
internal static class ControlHelloV2
{
    internal const int Protocol = 2;
    internal const int MaxPayloadByteLength = WireV2Json.MaxPayloadByteLength;
    private static readonly string[] Fields = ["type", "channel", "protocol"];

    /// <summary>固定 UTF-8 JSON，不含长度前缀，每次返回独立数组。</summary>
    internal static byte[] Serialize() =>
        "{\"type\":\"channel_hello\",\"channel\":\"control\",\"protocol\":2}"u8.ToArray();

    internal static WireV2ParseStatus Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length is < 1 or > MaxPayloadByteLength) return WireV2ParseStatus.Invalid;
        var reader = WireV2Json.Reader(utf8);
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return WireV2ParseStatus.Invalid;
            int seen = 0, protocol = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                int field = WireV2Json.ReadField(ref reader, Fields, ref seen);
                if (field < 0 || !reader.Read()) return WireV2ParseStatus.Invalid;
                switch (field)
                {
                    case 0:
                        if (reader.TokenType != JsonTokenType.String || !reader.ValueTextEquals("channel_hello")) return WireV2ParseStatus.Invalid;
                        break;
                    case 1:
                        if (reader.TokenType != JsonTokenType.String || !reader.ValueTextEquals("control")) return WireV2ParseStatus.Invalid;
                        break;
                    case 2:
                        if (!WireV2Json.PositiveInteger(ref reader, out protocol)) return WireV2ParseStatus.Invalid;
                        break;
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject || seen != 7 || reader.Read()) return WireV2ParseStatus.Invalid;
            return protocol switch
            {
                1 => WireV2ParseStatus.LegacyV1,
                Protocol => WireV2ParseStatus.AcceptedV2,
                _ => WireV2ParseStatus.UnsupportedVersion,
            };
        }
        catch (JsonException) { return WireV2ParseStatus.Invalid; }
        catch (InvalidOperationException) { return WireV2ParseStatus.Invalid; }
    }
}
