using System.Text.Json;

namespace LanRemote.Transport.Auth;

/// <summary>仅供独立 Hello2/Challenge2 使用的扁平 JSON 读取原语，不改变既有 AuthJson。</summary>
internal static class WireV2Json
{
    internal const int MaxPayloadByteLength = 4096;

    internal static Utf8JsonReader Reader(ReadOnlySpan<byte> utf8) => new(utf8, new JsonReaderOptions
    {
        MaxDepth = 1,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    });

    internal static int ReadField(ref Utf8JsonReader reader, string[] fields, ref int seen)
    {
        if (reader.TokenType != JsonTokenType.PropertyName) return -1;
        for (int i = 0; i < fields.Length; i++)
        {
            if (!reader.ValueTextEquals(fields[i])) continue;
            int bit = 1 << i;
            if ((seen & bit) != 0) return -1;
            seen |= bit;
            return i;
        }
        return -1;
    }

    // 版本与有效期都限定为正 Int32 十进制整数字面量，拒绝浮点、指数、符号和溢出。
    internal static bool PositiveInteger(ref Utf8JsonReader reader, out int value)
    {
        value = 0;
        if (reader.TokenType != JsonTokenType.Number) return false;
        foreach (byte digit in reader.ValueSpan)
            if (digit is < (byte)'0' or > (byte)'9') return false;
        return reader.TryGetInt32(out value) && value > 0;
    }

    // 先限制原始转义字节，再分配字符串；canonical 值均为 ASCII，每字符最多六字节转义。
    internal static string? BoundedString(ref Utf8JsonReader reader, int maximumCharacters)
    {
        if (reader.TokenType != JsonTokenType.String || reader.ValueSpan.Length > maximumCharacters * 6)
            return null;
        string? value = reader.GetString();
        return value?.Length <= maximumCharacters ? value : null;
    }
}
