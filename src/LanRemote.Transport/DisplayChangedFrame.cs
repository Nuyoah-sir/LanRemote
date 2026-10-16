using System.Buffers;
using System.Text.Json;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport;

/// <summary>新版 Control 的 display_changed 成功回执纯载荷合同。</summary>
/// <remarks>
/// sessionId 仅关联会话；seq 是 Host 回执序号，与 requestSequence 独立。
/// catalogSequence/displayId/firstFrameId 与尺寸只描述关联结果，不构成授权、反重放或 ACK 可见性证明。
/// 本类不触 stream/clock，不判断实际首帧发送完成，也不包含失败回执分支。
/// </remarks>
internal sealed class DisplayChangedFrame
{
    internal const string ExpectedType = "display_changed";
    internal const int ExpectedProtocol = 1;
    internal const int MaxPayloadByteLength = 4096;
    private static readonly string[] Fields =
    [
        "type", "protocol", "sessionId", "seq", "timestampMs", "requestSequence",
        "catalogSequence", "displayId", "firstFrameId", "width", "height",
    ];

    internal DisplayChangedFrame(Guid sessionId, long sequence, long timestampMs, long requestSequence,
        long catalogSequence, DisplayId displayId, ulong firstFrameId, int width, int height)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("会话标识不可为空。", nameof(sessionId));
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        ArgumentOutOfRangeException.ThrowIfNegative(timestampMs);
        ArgumentOutOfRangeException.ThrowIfNegative(requestSequence);
        ArgumentOutOfRangeException.ThrowIfNegative(catalogSequence);
        if (displayId.Value is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(displayId));
        if (width is < 1 or > FrameLimits.MaxDimension) throw new ArgumentOutOfRangeException(nameof(width));
        if (height is < 1 or > FrameLimits.MaxDimension) throw new ArgumentOutOfRangeException(nameof(height));

        SessionId = sessionId;
        Sequence = sequence;
        TimestampMs = timestampMs;
        RequestSequence = requestSequence;
        CatalogSequence = catalogSequence;
        DisplayId = displayId;
        FirstFrameId = firstFrameId;
        Width = width;
        Height = height;
    }

    internal Guid SessionId { get; }
    internal long Sequence { get; }
    internal long TimestampMs { get; }
    internal long RequestSequence { get; }
    internal long CatalogSequence { get; }
    internal DisplayId DisplayId { get; }
    internal ulong FirstFrameId { get; }
    internal int Width { get; }
    internal int Height { get; }

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
            writer.WriteNumber("requestSequence", RequestSequence);
            writer.WriteNumber("catalogSequence", CatalogSequence);
            writer.WriteNumber("displayId", DisplayId.Value);
            writer.WriteNumber("firstFrameId", FirstFrameId);
            writer.WriteNumber("width", Width);
            writer.WriteNumber("height", Height);
            writer.WriteEndObject();
        }
        return output.WrittenSpan.ToArray();
    }

    /// <summary>严格纯解析；先检查字节预算，再检查调用方上下文，拒绝码不包含对端数据。</summary>
    internal static bool TryParse(ReadOnlySpan<byte> utf8, Guid expectedSessionId,
        out DisplayChangedFrame? frame, out string? rejection)
    {
        frame = null;
        rejection = "display-changed-invalid-length";
        if (utf8.Length is < 1 or > MaxPayloadByteLength) return false;
        rejection = "display-changed-invalid-context";
        if (expectedSessionId == Guid.Empty) return false;
        rejection = "display-changed-invalid-payload";
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
            long sequence = -1, timestamp = -1, requestSequence = -1, catalogSequence = -1;
            long displayId = -1, width = -1, height = -1;
            ulong firstFrameId = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                int field = ReadField(ref reader, ref seen);
                if (field < 0 || !reader.Read()) return false;
                switch (field)
                {
                    case 0:
                        if (reader.TokenType != JsonTokenType.String || reader.ValueSpan.Length > ExpectedType.Length * 6
                            || !reader.ValueTextEquals(ExpectedType)) return false;
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
                        if (!ReadInteger(ref reader, out requestSequence) || requestSequence < 0) return false;
                        break;
                    case 6:
                        if (!ReadInteger(ref reader, out catalogSequence) || catalogSequence < 0) return false;
                        break;
                    case 7:
                        if (!ReadInteger(ref reader, out displayId) || displayId is < 0 or > 255) return false;
                        break;
                    case 8:
                        if (!ReadUnsignedInteger(ref reader, out firstFrameId)) return false;
                        break;
                    case 9:
                        if (!ReadInteger(ref reader, out width) || width is < 1 or > FrameLimits.MaxDimension) return false;
                        break;
                    case 10:
                        if (!ReadInteger(ref reader, out height) || height is < 1 or > FrameLimits.MaxDimension) return false;
                        break;
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject || seen != (1 << Fields.Length) - 1 || reader.Read()) return false;
            frame = new DisplayChangedFrame(session, sequence, timestamp, requestSequence, catalogSequence,
                new DisplayId((int)displayId), firstFrameId, (int)width, (int)height);
            rejection = null;
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; } // GetString/ValueTextEquals 可能拒绝非法编码。
    }

    private static int ReadField(ref Utf8JsonReader reader, ref int seen)
    {
        // 最长字段名 requestSequence/catalogSequence 为 15 个 ASCII 字符。
        if (reader.TokenType != JsonTokenType.PropertyName || reader.ValueSpan.Length > 15 * 6) return -1;
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

    private static bool ReadUnsignedInteger(ref Utf8JsonReader reader, out ulong value)
    {
        value = 0;
        if (reader.TokenType != JsonTokenType.Number) return false;
        foreach (byte character in reader.ValueSpan)
            if (character is (byte)'.' or (byte)'e' or (byte)'E') return false;
        return reader.TryGetUInt64(out value);
    }
}
