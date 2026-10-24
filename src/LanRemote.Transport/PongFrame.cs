using System.Buffers;
using System.Text.Json;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport;

/// <summary>新版 Control 的 pong 纯回执载荷合同；echoedSeq 回显被响应的 ping 的 seq。</summary>
/// <remarks>
/// pong 自己的 seq 与 echoedSeq 是两回事：前者是本消息的序号，后者才用于结算往返时延。
/// sessionId 仅关联会话，seq/timestampMs 不是授权、反重放、拓扑世代或帧坐标关联证明。
/// 本类不触 stream/clock，不改变现行 Control/LRVF。
/// </remarks>
internal sealed class PongFrame
{
    internal const string ExpectedType = "pong";
    internal const int ExpectedProtocol = 1;
    internal const int MaxPayloadByteLength = 4096;
    private static readonly string[] Fields = ["type", "protocol", "sessionId", "seq", "timestampMs", "echoedSeq"];

    internal PongFrame(Guid sessionId, long sequence, long timestampMs, long echoedSequence)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("会话标识不可为空。", nameof(sessionId));
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        ArgumentOutOfRangeException.ThrowIfNegative(timestampMs);
        ArgumentOutOfRangeException.ThrowIfNegative(echoedSequence);

        SessionId = sessionId;
        Sequence = sequence;
        TimestampMs = timestampMs;
        EchoedSequence = echoedSequence;
    }

    internal Guid SessionId { get; }
    internal long Sequence { get; }
    internal long TimestampMs { get; }
    internal long EchoedSequence { get; }

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
            writer.WriteNumber("echoedSeq", EchoedSequence);
            writer.WriteEndObject();
        }
        return output.WrittenSpan.ToArray();
    }

    /// <summary>严格纯解析；先检查字节预算，再检查调用方上下文，拒绝码不包含对端数据。</summary>
    internal static bool TryParse(ReadOnlySpan<byte> utf8, Guid expectedSessionId,
        out PongFrame? frame, out string? rejection)
    {
        frame = null;
        rejection = "pong-invalid-length";
        if (utf8.Length is < 1 or > MaxPayloadByteLength) return false;
        rejection = "pong-invalid-context";
        if (expectedSessionId == Guid.Empty) return false;
        rejection = "pong-invalid-payload";
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
            long sequence = -1, timestamp = -1, echoedSequence = -1;
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
                        if (!ReadInteger(ref reader, out echoedSequence) || echoedSequence < 0) return false;
                        break;
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject || seen != 63 || reader.Read()) return false;
            frame = new PongFrame(session, sequence, timestamp, echoedSequence);
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
