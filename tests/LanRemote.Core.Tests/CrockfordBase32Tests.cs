using System.Security.Cryptography;
using LanRemote.Core.Encoding;
using Xunit;

namespace LanRemote.Core.Tests;

/// <summary>
/// Crockford Base32 编解码测试。
/// </summary>
public sealed class CrockfordBase32Tests
{
    [Fact]
    public void Encode_EmptyInput_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, CrockfordBase32.Encode(Array.Empty<byte>()));
    }

    [Fact]
    public void Encode_SixteenBytes_ProducesTwentySixCharacters()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(16);

        string encoded = CrockfordBase32.Encode(bytes);

        // 128 bit / 5 = 25.6 → 26 个字符，正好对应 01_MASTER_PROMPT.md 第 7.2 节的「约 26 字符」。
        Assert.Equal(26, encoded.Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(100)]
    public void RoundTrip_PreservesBytes(int length)
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(length);

        string encoded = CrockfordBase32.Encode(bytes);

        Assert.True(CrockfordBase32.TryDecode(encoded, out byte[] decoded));
        Assert.Equal(bytes, decoded);
    }

    [Fact]
    public void RoundTrip_AllOnesBytes()
    {
        byte[] bytes = Enumerable.Repeat((byte)0xFF, 16).ToArray();

        string encoded = CrockfordBase32.Encode(bytes);

        Assert.True(CrockfordBase32.TryDecode(encoded, out byte[] decoded));
        Assert.Equal(bytes, decoded);
    }

    [Fact]
    public void Encode_OnlyUsesAlphabet()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);

        string encoded = CrockfordBase32.Encode(bytes);

        Assert.All(encoded, c => Assert.Contains(c, CrockfordBase32.Alphabet));
    }

    [Fact]
    public void Alphabet_ExcludesAmbiguousCharacters()
    {
        Assert.DoesNotContain('I', CrockfordBase32.Alphabet);
        Assert.DoesNotContain('L', CrockfordBase32.Alphabet);
        Assert.DoesNotContain('O', CrockfordBase32.Alphabet);
        Assert.DoesNotContain('U', CrockfordBase32.Alphabet);
    }

    [Fact]
    public void TryDecode_IgnoresSeparatorsAndWhitespace()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(16);
        string encoded = CrockfordBase32.Encode(bytes);
        string noisy = $" {encoded[..6]}-{encoded[6..]}\t";

        Assert.True(CrockfordBase32.TryDecode(noisy, out byte[] decoded));
        Assert.Equal(bytes, decoded);
    }

    [Fact]
    public void TryDecode_IsCaseInsensitive()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(16);
        string encoded = CrockfordBase32.Encode(bytes);

        Assert.True(CrockfordBase32.TryDecode(encoded.ToLowerInvariant(), out byte[] decoded));
        Assert.Equal(bytes, decoded);
    }

    [Theory]
    // 同一个字节 0xFF 的三种写法：规范形式 ZW，尾部填充位非 0 的 ZY / ZZ 必须被拒绝。
    [InlineData("ZY")]
    [InlineData("ZZ")]
    public void TryDecode_RejectsNonCanonicalTrailingBits(string nonCanonical)
    {
        Assert.False(CrockfordBase32.TryDecode(nonCanonical, out _));
    }

    [Fact]
    public void TryDecode_AcceptsCanonicalAllOnes()
    {
        Assert.True(CrockfordBase32.TryDecode("ZW", out byte[] decoded));
        Assert.Equal(new byte[] { 0xFF }, decoded);
    }

    [Theory]
    [InlineData("IZZZZZZZ")] // I 折算为 1
    [InlineData("LZZZZZZZ")] // L 折算为 1
    public void TryDecode_NormalizesIAndLToOne(string text)
    {
        Assert.True(CrockfordBase32.TryDecode(text, out byte[] decoded));
        Assert.Equal(CrockfordBase32.Decode("1ZZZZZZZ"), decoded);
    }

    [Fact]
    public void TryDecode_NormalizesOToZero()
    {
        Assert.True(CrockfordBase32.TryDecode("OZZZZZZZ", out byte[] decoded));
        Assert.Equal(CrockfordBase32.Decode("0ZZZZZZZ"), decoded);
    }

    [Theory]
    [InlineData("UZZZZZZZ")] // U 被 Crockford 保留，不属于合法字符
    [InlineData("A!ZZZZZZ")]
    [InlineData("A中ZZZZZZ")]
    public void TryDecode_RejectsInvalidCharacters(string text)
    {
        Assert.False(CrockfordBase32.TryDecode(text, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    public void TryDecode_RejectsEmptyOrBlankInput(string? text)
    {
        Assert.False(CrockfordBase32.TryDecode(text, out _));
    }

    [Fact]
    public void Decode_ThrowsOnMalformedInput()
    {
        Assert.Throws<FormatException>(() => CrockfordBase32.Decode("!!!"));
    }

    [Fact]
    public void TryDecodeExact_RejectsWrongLength()
    {
        byte[] sixteen = RandomNumberGenerator.GetBytes(16);
        string encoded = CrockfordBase32.Encode(sixteen);

        Assert.True(CrockfordBase32.TryDecodeExact(encoded, 16, out _));
        Assert.False(CrockfordBase32.TryDecodeExact(encoded, 15, out _));
        Assert.False(CrockfordBase32.TryDecodeExact(encoded, 32, out _));
    }

    [Fact]
    public void TryDecodeExact_MatchingLength_ReturnsDecodedBytes()
    {
        byte[] key = RandomNumberGenerator.GetBytes(16);
        string encoded = CrockfordBase32.Encode(key);

        Assert.True(CrockfordBase32.TryDecodeExact(encoded, 16, out byte[] decoded));
        Assert.Equal(key, decoded);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(17)]
    [InlineData(32)]
    public void TryDecodeExact_LengthMismatch_ReturnsEmptyInsteadOfPartialSecret(int actualLength)
    {
        // 一个「Base32 本身合法、但解码长度不是 16 字节」的输入。
        // M1.2 语义：失败时不得把部分解码出的字节留给调用方（M4 校验访问密钥时会用到）。
        string encoded = CrockfordBase32.Encode(RandomNumberGenerator.GetBytes(actualLength));

        Assert.False(CrockfordBase32.TryDecodeExact(encoded, 16, out byte[] bytes));
        Assert.Empty(bytes);
        Assert.Same(Array.Empty<byte>(), bytes);
    }

    [Fact]
    public void TryDecodeExact_MalformedInput_AlsoReturnsEmpty()
    {
        Assert.False(CrockfordBase32.TryDecodeExact("!!!", 16, out byte[] bytes));
        Assert.Empty(bytes);
    }

    [Fact]
    public void Group_InsertsSeparators()
    {
        // 从左往右每 groupSize 个字符插入一个 '-'，最后一组拿到剩下的部分。
        Assert.Equal("ABCD-EFGH", CrockfordBase32.Group("ABCDEFGH", 4));
        Assert.Equal("ABCDE-FGHIJ-K", CrockfordBase32.Group("ABCDEFGHIJK", 5));
    }

    [Fact]
    public void Group_ShortInputIsLeftUngrouped()
    {
        Assert.Equal("ABC", CrockfordBase32.Group("ABC", 4));
    }

    [Fact]
    public void Group_RejectsNonPositiveGroupSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CrockfordBase32.Group("ABCD", 0));
    }
}
