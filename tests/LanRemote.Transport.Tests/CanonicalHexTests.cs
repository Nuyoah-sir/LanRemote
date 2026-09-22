using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

/// <summary>
/// <see cref="CanonicalHex"/>：canonical hex（仅大写 <c>[0-9A-F]</c>、偶数长度）的定义与测试。
/// </summary>
/// <remarks>
/// <para><c>Convert.FromHexString</c> 容忍小写与大小写混写——round-trip 把它们全部收窄到
/// 「仅大写」这一种 wire 表示。transcript 用 uppercase hex，wire 上不允许第二种形态存在。</para>
/// </remarks>
public sealed class CanonicalHexTests
{
    /// <summary>32 字节 <c>00..1F</c> 的 uppercase hex。</summary>
    private const string Range32Hex = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

    [Fact]
    public void Decodes_Uppercase_Literal()
    {
        Assert.True(CanonicalHex.TryDecode(Range32Hex, out byte[] bytes));
        Assert.Equal(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(), bytes);
    }

    [Fact]
    public void Decodes_Mixed_Digits_And_Letters()
    {
        Assert.True(CanonicalHex.TryDecode("0A5F", out byte[] bytes));
        Assert.Equal(new byte[] { 0x0A, 0x5F }, bytes);
    }

    /// <summary>
    /// 往返扫掠：<see cref="Convert.ToHexString(byte[])"/> 的产物必被接受（长度 1..64）。
    /// </summary>
    [Fact]
    public void Accepts_Every_ToHexString_Output_For_Lengths_1_To_64()
    {
        for (int length = 1; length <= 64; length++)
        {
            byte[] original = Enumerable.Range(0, length)
                .Select(i => (byte)(i * 11 + 5))
                .ToArray();

            string encoded = Convert.ToHexString(original);

            Assert.True(CanonicalHex.TryDecode(encoded, out byte[] decoded), $"长度 {length} 被误拒");
            Assert.Equal(original, decoded);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    // 小写 / 混写：Convert.FromHexString 全都容忍，round-trip 全部拒绝
    [InlineData("0a5f")]
    [InlineData("0A5f")]
    [InlineData("ab")]
    // 奇数长度
    [InlineData("A")]
    [InlineData("ABC")]
    [InlineData(Range32Hex + "A")]
    // 字母表外
    [InlineData("GG")]
    [InlineData("0x00")]
    [InlineData("00 01")]
    [InlineData("00\t01")]
    [InlineData("00,01")]
    [InlineData("＋＋")]
    // Unicode 不因编码器崩溃，直接拒绝
    [InlineData("\uD800")]
    public void Rejects_Everything_Not_Canonical(string? value)
    {
        Assert.False(CanonicalHex.TryDecode(value, out byte[] bytes));
        Assert.Empty(bytes);
    }

    /// <summary>失败时 out 恒为空数组，不返回部分解码结果。</summary>
    [Fact]
    public void Failure_Leaves_Empty_Bytes()
    {
        Assert.False(CanonicalHex.TryDecode("0a5f", out byte[] bytes));
        Assert.Same(Array.Empty<byte>(), bytes);
    }

    /// <summary>
    /// 本类<b>只判形状、不判语义长度</b>：31 字节的合法大写 hex 在形状上成立，
    /// 「必须恰好 32 字节」由帧层（如 <c>AuthChallengeFrame</c> 的 certSha256）判定。
    /// 把这条写死，防止将来有人把长度策略挪进本类而不自知。
    /// </summary>
    [Fact]
    public void Does_Not_Enforce_Semantic_Length()
    {
        const string ThirtyOneBytes = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E";

        Assert.True(CanonicalHex.TryDecode(ThirtyOneBytes, out byte[] bytes));
        Assert.Equal(31, bytes.Length);
    }
}
