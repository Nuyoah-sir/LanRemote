using System.Buffers;
using System.Text.Json;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport;

/// <summary>新版 Control 的 request_display 纯请求载荷合同，不直接切换采集源。</summary>
/// <remarks>
/// sessionId 仅关联会话，catalogSequence 仅引用目录快照，displayId 仅为该目录的原始索引。
/// seq/timestampMs 不是授权、反重放、拓扑世代或帧坐标关联证明；后续会话 gate 另行判定。
/// 本类不触 stream/clock，不规定成功 ACK 或 frameId，也不改变现行 Control/LRVF。
/// </remarks>
internal sealed class DisplaySwitchRequestFrame
{
    internal const string ExpectedType = "request_display";
    internal const int ExpectedProtocol = 1;
    internal const int MaxPayloadByteLength = 4096;
    private static readonly string[] Fields = ["type", "protocol", "sessionId", "seq", "timestampMs", "catalogSequence", "displayId"];

    internal DisplaySwitchRequestFrame(Guid sessionId, long sequence, long timestampMs, long catalogSequence, DisplayId displayId)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("会话标识不可为空。", nameof(sessionId));
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        ArgumentOutOfRangeException.ThrowIfNegative(timestampMs);
        ArgumentOutOfRangeException.ThrowIfNegative(catalogSequence);
        if (displayId.Value is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(displayId));

        SessionId = sessionId;
        Sequence = sequence;
        TimestampMs = timestampMs;
        CatalogSequence = catalogSequence;
        DisplayId = displayId;
    }

    internal Guid SessionId { get; }
    internal long Sequence { get; }
    internal long TimestampMs { get; }
    internal long CatalogSequence { get; }
    internal DisplayId DisplayId { get; }

    /// <summary>只输出 UTF-8 JSON，不含长度前缀；每次返回独立数组。</summary>
    internal byte[] Serialize()
    {
        // 固定 ASCII 字段、规范 Guid 与有界整数保证输出远小于 4096 字节。
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("type", ExpectedType);
            writer.WriteNumber("protocol", ExpectedProtocol);
            writer.WriteString("sessionId", SessionId.ToString("D"));
            writer.WriteNumber("seq", Sequence);
            writer.WriteNumber("timestampMs", TimestampMs);
            writer.WriteNumber("catalogSequence", CatalogSequence);
            writer.WriteNumber("displayId", DisplayId.Value);
            writer.WriteEndObject();
        }
        return output.WrittenSpan.ToArray();
    }

    /// <summary>严格纯解析；先检查字节预算，再检查调用方上下文，拒绝码不包含对端数据。</summary>
    internal static bool TryParse(ReadOnlySpan<byte> utf8, Guid expectedSessionId,
        out DisplaySwitchRequestFrame? frame, out string? rejection)
    {
        frame = null;
        rejection = "request-display-invalid-length";
        if (utf8.Length is < 1 or > MaxPayloadByteLength) return false;
        rejection = "request-display-invalid-context";
        if (expectedSessionId == Guid.Empty) return false;
        rejection = "request-display-invalid-payload";
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
            long sequence = -1, timestamp = -1, catalogSequence = -1, displayId = -1;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                int field = ReadField(ref reader, ref seen);
                if (field < 0 || !reader.Read()) return false;
                switch (field)
                {
                    case 0:
                        if (reader.TokenType != JsonTokenType.String || !reader.ValueTextEquals(ExpectedType)) return false;
                        break;
                    case 1:
                        if (!ReadInteger(ref reader, out long protocol) || protocol != ExpectedProtocol) return false;
                        break;
                    case 2:
                        // 36 个 ASCII 字符各自最多占一个六字节 JSON 转义；分配字符串前先限长。
                        if (reader.TokenType != JsonTokenType.String || reader.ValueSpan.Length > 36 * 6) return false;
                        if (!CanonicalGuid.TryParse(reader.GetString(), out session) || session == Guid.Empty || session != expectedSessionId) return false;
                        break;
                    case 3:
                        if (!ReadInteger(ref reader, out sequence) || sequence < 0) return false;
                        break;
                    case 4:
                        if (!ReadInteger(ref reader, out timestamp) || timestamp < 0) return false;
                        break;
                    case 5:
                        if (!ReadInteger(ref reader, out catalogSequence) || catalogSequence < 0) return false;
                        break;
                    case 6:
                        if (!ReadInteger(ref reader, out displayId) || displayId is < 0 or > 255) return false;
                        break;
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject || seen != 127 || reader.Read()) return false;
            frame = new DisplaySwitchRequestFrame(session, sequence, timestamp, catalogSequence, new DisplayId((int)displayId));
            rejection = null;
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; } // GetString/ValueTextEquals 可能拒绝非法编码。
    }

    private static int ReadField(ref Utf8JsonReader reader, ref int seen)
    {
        if (reader.TokenType != JsonTokenType.PropertyName) return -1;
        for (int index = 0; index < Fields.Length; index++)
        {
            if (!reader.ValueTextEquals(Fields[index])) continue;
            int bit = 1 << index;
            if ((seen & bit) != 0) return -1;
            seen |= bit;
            return index;
        }
        return -1;
    }

    private static bool ReadInteger(ref Utf8JsonReader reader, out long value)
    {
        value = 0;
        if (reader.TokenType != JsonTokenType.Number) return false;
        foreach (byte character in reader.ValueSpan)
            if (character is (byte)'.' or (byte)'e' or (byte)'E') return false;
        return reader.TryGetInt64(out value);
    }
}
