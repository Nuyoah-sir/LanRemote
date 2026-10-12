using System.Buffers;
using System.Text.Json;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport;

/// <summary>新版专用目录 Offer/Accept 的纯载荷合同，尚未接入真实 Control。</summary>
/// <remarks>
/// applicationProfile=2 是新版应用合同，catalogSchema=1 是目录载荷版式；都不是 TLS/LRVF 版本。
/// B 政策只支持新版双方，不接受旧 profile 或静默降级；现行 hello/auth/monitor 不由此类改动。
/// Offer 仅 Host→Client，Accept 仅 Client→Host；调用方选择正确解析入口并提供预期 challenge 会话 id。
/// V2 约定在 proof 前收集再绑定认证；该 id 此时不代表已认证，本类仍不实现 live 握手。
/// SessionId 相等只是载荷关联，不证明身份、双方确认、反重放、权限或未来交付资格。
/// 未来 live 必须一会话仅一次握手、无同会话重新协商/重试，另过时限、唯一读写者及父会话 gate。
/// 本类没有状态机、不生成随机数、不触 stream/clock，不把解析结果作为可兑换的授权对象。
/// </remarks>
internal sealed class DisplayCatalogAgreementFrame
{
    internal const int ApplicationProfile = 2;
    internal const int CatalogSchema = 1;
    internal const int MaxPayloadByteLength = 4096;
    internal const string OfferType = "display_catalog_offer";
    internal const string AcceptType = "display_catalog_accept";
    private static readonly string[] Fields = ["type", "applicationProfile", "catalogSchema", "sessionId"];

    private DisplayCatalogAgreementFrame(Guid sessionId, bool isOffer)
    {
        SessionId = sessionId;
        IsOffer = isOffer;
    }

    internal Guid SessionId { get; }
    internal bool IsOffer { get; }

    /// <summary>仅序列化 UTF-8 JSON，不包含长度前缀；每次返回新数组。</summary>
    internal static byte[] SerializeOffer(Guid sessionId) => Serialize(sessionId, true);
    internal static byte[] SerializeAccept(Guid sessionId) => Serialize(sessionId, false);
    private static byte[] Serialize(Guid sessionId, bool offer)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("会话标识不可为空。", nameof(sessionId));
        // 所有字段为固定ASCII或36字符canonical Guid，输出有界且远小于4096字节。
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("type", offer ? OfferType : AcceptType);
            writer.WriteNumber("applicationProfile", ApplicationProfile);
            writer.WriteNumber("catalogSchema", CatalogSchema);
            writer.WriteString("sessionId", sessionId.ToString("D"));
            writer.WriteEndObject();
        }
        return output.WrittenSpan.ToArray();
    }

    internal static bool TryParseOffer(ReadOnlySpan<byte> utf8, Guid expectedSessionId,
        out DisplayCatalogAgreementFrame? frame, out string? rejection) => Parse(utf8, expectedSessionId, true, out frame, out rejection);
    internal static bool TryParseAccept(ReadOnlySpan<byte> utf8, Guid expectedSessionId,
        out DisplayCatalogAgreementFrame? frame, out string? rejection) => Parse(utf8, expectedSessionId, false, out frame, out rejection);

    private static bool Parse(ReadOnlySpan<byte> utf8, Guid expectedSessionId, bool offer,
        out DisplayCatalogAgreementFrame? frame, out string? rejection)
    {
        frame = null;
        rejection = "catalog-agreement-invalid-length";
        if (utf8.Length is < 1 or > MaxPayloadByteLength) return false;
        rejection = "catalog-agreement-invalid-context";
        if (expectedSessionId == Guid.Empty) return false;
        rejection = "catalog-agreement-invalid-payload"; // 只用于本地诊断；绝不含对端内容。
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            MaxDepth = 1,
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
        });
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
            int seen = 0;
            Guid session = Guid.Empty;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                int field = ReadField(ref reader, ref seen);
                if (field < 0 || !reader.Read()) return false;
                switch (field)
                {
                    case 0:
                        if (reader.TokenType != JsonTokenType.String || !reader.ValueTextEquals(offer ? OfferType : AcceptType)) return false;
                        break;
                    case 1:
                        if (!IsExactInteger(ref reader, ApplicationProfile)) return false;
                        break;
                    case 2:
                        if (!IsExactInteger(ref reader, CatalogSchema)) return false;
                        break;
                    case 3:
                        // 36个ASCII字符最坏每个写成一个六字节JSON转义；先限长再分配字符串。
                        if (reader.TokenType != JsonTokenType.String || reader.ValueSpan.Length > 36 * 6) return false;
                        if (!CanonicalGuid.TryParse(reader.GetString(), out session) || session == Guid.Empty || session != expectedSessionId) return false;
                        break;
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject || seen != 15 || reader.Read()) return false;
            frame = new DisplayCatalogAgreementFrame(session, offer);
            rejection = null;
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; } // GetString/ValueTextEquals的非法编码失败。
    }

    private static int ReadField(ref Utf8JsonReader reader, ref int seen)
    {
        if (reader.TokenType != JsonTokenType.PropertyName) return -1;
        for (int i = 0; i < Fields.Length; i++)
        {
            if (!reader.ValueTextEquals(Fields[i])) continue;
            int bit = 1 << i;
            if ((seen & bit) != 0) return -1;
            seen |= bit;
            return i;
        }
        return -1;
    }

    private static bool IsExactInteger(ref Utf8JsonReader reader, int expected)
    {
        if (reader.TokenType != JsonTokenType.Number) return false;
        foreach (byte value in reader.ValueSpan)
            if (value is (byte)'.' or (byte)'e' or (byte)'E') return false;
        return reader.TryGetInt32(out int actual) && actual == expected;
    }
}
