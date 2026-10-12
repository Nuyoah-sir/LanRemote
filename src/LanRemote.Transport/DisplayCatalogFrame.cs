using System.Buffers;
using System.Text.Json;
using LanRemote.Core.Models;

namespace LanRemote.Transport;

/// <summary>M6 独立 host_status 显示目录载荷；未接认证会话、网络读取或远端切屏。</summary>
/// <remarks>
/// 仅传本次目录的原始索引和几何，不能作为持久身份或采集授权。
/// seq/timestampMs 是消息元数据，不是拓扑世代、反重放或帧坐标关联证明。
/// 不发送 native DeviceId/DeviceKey，不改变现有 Control 单读槽或 LRVF v1。
/// </remarks>
internal sealed class DisplayCatalogFrame
{
    internal const string ExpectedType = "host_status";
    internal const int ExpectedProtocol = 1;
    internal const int MaxPayloadByteLength = 256 * 1024;
    internal const int MaxDisplays = 256;
    internal const int MaxNameLength = 128;
    private const int MaxEncodedNameBytes = MaxNameLength * 6;
    private const string InvalidPayload = "host-status-invalid-payload";
    private const string InvalidLength = "host-status-invalid-length";
    private static readonly string[] RootFields = ["type", "protocol", "seq", "timestampMs", "displays"];
    private static readonly string[] DisplayFields = ["id", "name", "x", "y", "width", "height", "isPrimary"];

    /// <summary>拷贝有界列表；元数据允许超出像素采集预算，但不分配像素。</summary>
    internal DisplayCatalogFrame(long sequence, long timestampMs, IReadOnlyList<DisplayInfo> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        ArgumentOutOfRangeException.ThrowIfNegative(timestampMs);
        int count = displays.Count;
        if (count is < 1 or > MaxDisplays)
            throw new ArgumentOutOfRangeException(nameof(displays));

        var snapshot = new DisplayInfo[count];
        Span<bool> ids = stackalloc bool[MaxDisplays];
        ids.Clear();
        int primaries = 0;
        for (int index = 0; index < count; index++)
        {
            DisplayInfo item = displays[index];
            if (!IsValid(item) || ids[item.Id.Value])
                throw new ArgumentException("显示目录包含无效或重复条目。", nameof(displays));
            ids[item.Id.Value] = true;
            if (item.IsPrimary) primaries++;
            snapshot[index] = item; // sealed immutable record and immutable string; never retains caller's list.
        }
        if (primaries != 1 || displays.Count != count)
            throw new ArgumentException("显示目录必须稳定且恰好含一个主屏。", nameof(displays));

        Sequence = sequence;
        TimestampMs = timestampMs;
        Displays = Array.AsReadOnly(snapshot);
    }

    internal long Sequence { get; }
    internal long TimestampMs { get; }
    internal IReadOnlyList<DisplayInfo> Displays { get; }

    /// <summary>只输出 UTF-8 JSON；长度前缀由现有帧封装处理，不直接写任何流。</summary>
    internal byte[] Serialize()
    {
        // 字段/条目/名称上限先验限定输出；默认转义每 UTF-16 单元最多6字节，最坏目录小于256KiB。
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", ExpectedType);
            writer.WriteNumber("protocol", ExpectedProtocol);
            writer.WriteNumber("seq", Sequence);
            writer.WriteNumber("timestampMs", TimestampMs);
            writer.WriteStartArray("displays");
            foreach (DisplayInfo item in Displays)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", item.Id.Value);
                writer.WriteString("name", item.Name);
                writer.WriteNumber("x", item.X);
                writer.WriteNumber("y", item.Y);
                writer.WriteNumber("width", item.Width);
                writer.WriteNumber("height", item.Height);
                writer.WriteBoolean("isPrimary", item.IsPrimary);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        if (buffer.WrittenCount > MaxPayloadByteLength)
            throw new InvalidOperationException("显示目录输出超过载荷预算。");
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>纯解析；拒绝码不含对端数据。长度限制在解析/条目分配前检查。</summary>
    internal static bool TryParse(ReadOnlySpan<byte> utf8, out DisplayCatalogFrame? frame, out string? rejection)
    {
        frame = null;
        rejection = InvalidLength;
        if (utf8.Length is < 1 or > MaxPayloadByteLength) return false;
        rejection = InvalidPayload;
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            MaxDepth = 3,
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
        });
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
            int seen = 0;
            long sequence = -1, timestamp = -1;
            List<DisplayInfo>? displays = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                int field = ReadField(ref reader, RootFields, ref seen);
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
                        if (!ReadInteger(ref reader, out sequence) || sequence < 0) return false;
                        break;
                    case 3:
                        if (!ReadInteger(ref reader, out timestamp) || timestamp < 0) return false;
                        break;
                    case 4:
                        if (!ReadDisplays(ref reader, out displays)) return false;
                        break;
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject || seen != 31 || reader.Read()) return false;
            frame = new DisplayCatalogFrame(sequence, timestamp, displays!);
            rejection = null;
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; } // GetString may reject invalid UTF-8 this way.
        catch (ArgumentException) { return false; } // Final semantic validation; never publishes partial data.
    }

    private static bool ReadDisplays(ref Utf8JsonReader reader, out List<DisplayInfo>? displays)
    {
        displays = null;
        if (reader.TokenType != JsonTokenType.StartArray) return false;
        var items = new List<DisplayInfo>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (items.Count >= MaxDisplays || !ReadDisplay(ref reader, out DisplayInfo? item)) return false;
            items.Add(item!);
        }
        if (reader.TokenType != JsonTokenType.EndArray || items.Count == 0) return false;
        displays = items;
        return true;
    }

    private static bool ReadDisplay(ref Utf8JsonReader reader, out DisplayInfo? display)
    {
        display = null;
        if (reader.TokenType != JsonTokenType.StartObject) return false;
        int seen = 0;
        Span<int> values = stackalloc int[7];
        values.Clear();
        string? name = null;
        bool primary = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            int field = ReadField(ref reader, DisplayFields, ref seen);
            if (field < 0 || !reader.Read()) return false;
            if (field == 1)
            {
                if (reader.TokenType != JsonTokenType.String || reader.ValueSpan.Length > MaxEncodedNameBytes) return false;
                name = reader.GetString();
                if (!ValidName(name)) return false;
            }
            else if (field == 6)
            {
                if (reader.TokenType is not (JsonTokenType.True or JsonTokenType.False)) return false;
                primary = reader.GetBoolean();
            }
            else
            {
                if (!ReadInteger(ref reader, out long value) || value is < int.MinValue or > int.MaxValue) return false;
                values[field] = (int)value;
            }
        }
        if (reader.TokenType != JsonTokenType.EndObject || seen != 127) return false;
        display = new DisplayInfo(new DisplayId(values[0]), name!, values[2], values[3], values[4], values[5], primary);
        return IsValid(display);
    }

    private static int ReadField(ref Utf8JsonReader reader, string[] fields, ref int seen)
    {
        if (reader.TokenType != JsonTokenType.PropertyName) return -1;
        for (int index = 0; index < fields.Length; index++)
        {
            if (!reader.ValueTextEquals(fields[index])) continue;
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

    private static bool IsValid(DisplayInfo? item) => item is not null &&
        item.Id.Value is >= 0 and < MaxDisplays && ValidName(item.Name) &&
        item.Width > 0 && item.Height > 0 &&
        (long)item.X + item.Width <= int.MaxValue && (long)item.Y + item.Height <= int.MaxValue;

    private static bool ValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength) return false;
        for (int index = 0; index < name.Length; index++)
        {
            char character = name[index];
            if (char.IsControl(character) || char.IsLowSurrogate(character)) return false;
            if (char.IsHighSurrogate(character) &&
                (++index >= name.Length || !char.IsLowSurrogate(name[index]))) return false;
        }
        return true;
    }
}
