using System.Text;

namespace LanRemote.Core.Encoding;

/// <summary>
/// Crockford Base32 编解码器。
/// </summary>
/// <remarks>
/// <para>场景：设备码与访问密钥的人类可读/可口头传达表示（01_MASTER_PROMPT.md 第 7.1 / 7.2 节）。</para>
/// <para>字母表为 <c>0123456789ABCDEFGHJKMNPQRSTVWXYZ</c>，刻意剔除 I、L、O、U：
/// I/L 与数字 1 混淆、O 与数字 0 混淆、U 用于避免偶然拼出不雅词。</para>
/// <para>解码时把 I、L 归一化为 1，O 归一化为 0，忽略 <c>-</c> 与空白，且大小写不敏感。
/// 参见 04_PROTOCOL_AND_SECURITY.md 第 8 节「输入解析时忽略空格和 <c>-</c>」。</para>
/// <para>实现不引入 padding：调用方通过期望字节长度来保证语义，避免 Base32 的 <c>=</c> 歧义。</para>
/// </remarks>
public static class CrockfordBase32
{
    /// <summary>Crockford Base32 字母表，按值索引。</summary>
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>把字节序列编码为 Base32 字符串（不含分隔符与 padding）。</summary>
    /// <param name="bytes">待编码字节。</param>
    /// <returns>仅含字母表字符的字符串；空输入返回空字符串。</returns>
    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        int characterCount = ((bytes.Length * 8) + 4) / 5;
        Span<char> buffer = characterCount <= 64
            ? stackalloc char[characterCount]
            : new char[characterCount];

        int written = 0;
        int accumulator = 0;
        int accumulatedBits = 0;

        foreach (byte b in bytes)
        {
            accumulator = (accumulator << 8) | b;
            accumulatedBits += 8;

            while (accumulatedBits >= 5)
            {
                accumulatedBits -= 5;
                buffer[written++] = Alphabet[(accumulator >> accumulatedBits) & 0x1F];
            }
        }

        if (accumulatedBits > 0)
        {
            // 末尾不足 5 位时左移补齐，高位用 0 填充。
            buffer[written++] = Alphabet[(accumulator << (5 - accumulatedBits)) & 0x1F];
        }

        return new string(buffer);
    }

    /// <summary>
    /// 尝试解码。
    /// </summary>
    /// <param name="text">用户输入，允许含 <c>-</c> 与空白，大小写不敏感。</param>
    /// <param name="bytes">解码结果；失败时为 <see cref="Array.Empty{T}"/>。</param>
    /// <returns>是否为合法且规范的编码串。</returns>
    public static bool TryDecode(string? text, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        StringBuilder normalizedBuilder = new(text.Length);
        foreach (char c in text)
        {
            if (c is '-' or ' ' or '\t' or '\r' or '\n')
            {
                continue;
            }

            // Crockford 规定：解码时把易混字符折算回去。
            normalizedBuilder.Append(char.ToUpperInvariant(c) switch
            {
                'I' or 'L' => '1',
                'O' => '0',
                char other => other,
            });
        }

        string normalized = normalizedBuilder.ToString();
        if (normalized.Length == 0)
        {
            return false;
        }

        foreach (char c in normalized)
        {
            if (Alphabet.IndexOf(c) < 0)
            {
                return false;
            }
        }

        int totalBits = normalized.Length * 5;
        int byteCount = totalBits / 8;
        if (byteCount == 0)
        {
            return false;
        }

        byte[] result = new byte[byteCount];
        int written = 0;
        int accumulator = 0;
        int accumulatedBits = 0;

        foreach (char c in normalized)
        {
            accumulator = (accumulator << 5) | Alphabet.IndexOf(c);
            accumulatedBits += 5;

            if (accumulatedBits >= 8)
            {
                accumulatedBits -= 8;
                result[written++] = (byte)(accumulator >> accumulatedBits);
            }
        }

        if (written != byteCount)
        {
            return false;
        }

        if (accumulatedBits > 0)
        {
            // 规范形式要求尾部填充位必须为 0，否则说明同一个字节序列有其他编码写法。
            int leftoverMask = (1 << accumulatedBits) - 1;
            if ((accumulator & leftoverMask) != 0)
            {
                return false;
            }
        }

        bytes = result;
        return true;
    }

    /// <summary>解码，失败抛出 <see cref="FormatException"/>。</summary>
    /// <param name="text">用户输入。</param>
    /// <returns>解码后的字节。</returns>
    /// <exception cref="FormatException">输入不是合法的 Base32。</exception>
    public static byte[] Decode(string text)
    {
        if (!TryDecode(text, out byte[] bytes))
        {
            throw new FormatException("不是合法的 Crockford Base32 字符串。");
        }

        return bytes;
    }

    /// <summary>解码并要求结果长度严格等于期望值。</summary>
    /// <param name="text">用户输入。</param>
    /// <param name="expectedByteLength">期望字节数。</param>
    /// <param name="bytes">解码结果。</param>
    /// <returns>是否合法且长度匹配。</returns>
    public static bool TryDecodeExact(string? text, int expectedByteLength, out byte[] bytes)
    {
        if (!TryDecode(text, out bytes))
        {
            return false;
        }

        return bytes.Length == expectedByteLength;
    }

    /// <summary>按固定宽度插入分隔符以便人工阅读。</summary>
    /// <param name="encoded">不含分隔符的编码串。</param>
    /// <param name="groupSize">每组字符数，必须大于 0。</param>
    /// <returns>分组后的字符串。</returns>
    public static string Group(string encoded, int groupSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(groupSize);

        if (encoded.Length <= groupSize)
        {
            return encoded;
        }

        StringBuilder builder = new(encoded.Length + (encoded.Length / groupSize));
        for (int i = 0; i < encoded.Length; i++)
        {
            if (i > 0 && i % groupSize == 0)
            {
                builder.Append('-');
            }

            builder.Append(encoded[i]);
        }

        return builder.ToString();
    }
}
