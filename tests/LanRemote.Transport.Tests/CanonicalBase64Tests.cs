using System.Text;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

/// <summary>
/// <see cref="CanonicalBase64"/>：M4 阶段 2 步骤 12——base64 canonical 的定义与测试
/// （填充 / 字母表 / 长度校验；两端必须字节一致）。
/// </summary>
/// <remarks>
/// <para>判定式是 round-trip 逐字符相等（解码后重编码必须与输入完全一致）。
/// 因此本组测试的期望值分两类：① 明确的 wire 字面量（含 padding 的两种形态与无 padding 形态，
/// 字面量由 <c>scripts/reference/gen-auth-golden-vectors.py</c> 的独立编码路径核对过）；
/// ② 「凡 <see cref="Convert.ToBase64String(byte[])"/> 的产物必被接受」的往返扫掠
/// （长度 1..96 覆盖三种 padding 余数）。</para>
/// <para>「非规范尾部位」的经典案例：<c>"AB=="</c> 解码同样是字节 <c>00</c>，
/// 但重编码为 <c>"AA=="</c>——两种 wire 形态映射同一字节，正是 canonical 要消灭的东西。</para>
/// </remarks>
public sealed class CanonicalBase64Tests
{
    /// <summary>32 字节 <c>00..1F</c> 的 canonical base64（含 1 个 padding）。</summary>
    private const string Nonce32B64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    /// <summary>30 字节 <c>00..1D</c> 的 canonical base64（长度恰为 3 的倍数：无 padding）。</summary>
    private const string ThirtyByteNoPadding = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwd";

    /// <summary>31 字节 <c>00..1E</c> 的 canonical base64（含 2 个 padding）。</summary>
    private const string ThirtyOneByteTwoPadding = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHg==";

    private static byte[] Range(int count) =>
        Enumerable.Range(0, count).Select(i => (byte)i).ToArray();

    [Fact]
    public void Decodes_The_32_Byte_Nonce_Literal()
    {
        Assert.True(CanonicalBase64.TryDecode(Nonce32B64, out byte[] bytes));
        Assert.Equal(Range(32), bytes);
    }

    [Fact]
    public void Decodes_One_Byte_With_Two_Padding_Chars()
    {
        Assert.True(CanonicalBase64.TryDecode("AA==", out byte[] bytes));
        Assert.Equal(new byte[] { 0x00 }, bytes);
    }

    /// <summary>
    /// 标准字母表含 <c>+</c>（62）与 <c>/</c>（63）：它们是合法字符，不是「被允许的宽松」。
    /// （写测试时我先入为主把 <c>"AA++"</c> 当成了非规范尾部位，被实测纠正。）
    /// </summary>
    [Fact]
    public void Accepts_Plus_And_Slash_From_Standard_Alphabet()
    {
        Assert.True(CanonicalBase64.TryDecode("AA++", out byte[] plus));
        Assert.Equal(new byte[] { 0x00, 0x0F, 0xBE }, plus);

        Assert.True(CanonicalBase64.TryDecode("AA//", out byte[] slash));
        Assert.Equal(new byte[] { 0x00, 0x0F, 0xFF }, slash);
    }

    [Fact]
    public void Decodes_Thirty_Byte_Without_Padding()
    {
        Assert.True(CanonicalBase64.TryDecode(ThirtyByteNoPadding, out byte[] bytes));
        Assert.Equal(Range(30), bytes);
    }

    [Fact]
    public void Decodes_ThirtyOne_Byte_With_Two_Padding()
    {
        Assert.True(CanonicalBase64.TryDecode(ThirtyOneByteTwoPadding, out byte[] bytes));
        Assert.Equal(Range(31), bytes);
    }

    /// <summary>
    /// 往返扫掠：<see cref="Convert.ToBase64String(byte[])"/> 的产物是 canonical 的参照物，
    /// 长度 1..96 覆盖 padding 余数 0 / 1 / 2 的全部形态。
    /// </summary>
    [Fact]
    public void Accepts_Every_ToBase64String_Output_For_Lengths_1_To_96()
    {
        for (int length = 1; length <= 96; length++)
        {
            byte[] original = Enumerable.Range(0, length)
                .Select(i => (byte)(i * 7 + 3))
                .ToArray();

            string encoded = Convert.ToBase64String(original);

            Assert.True(CanonicalBase64.TryDecode(encoded, out byte[] decoded), $"长度 {length} 被误拒");
            Assert.Equal(original, decoded);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    // 长度不是 4 的倍数 / 残缺
    [InlineData("A")]
    [InlineData("AA")]
    [InlineData("AAA")]
    [InlineData("AA=")]
    [InlineData("AA===")]
    [InlineData("=AAA")]
    // 非规范尾部位：解码同字节、编码不同形
    [InlineData("AB==")]
    [InlineData("AAB=")]
    // 拼接两组 padding：整体能被宽松解码，但不是单一 canonical 串
    [InlineData("AA==AA==")]
    // 空白：Convert 会静默忽略，round-trip 收窄
    [InlineData(" AA==")]
    [InlineData("AA== ")]
    [InlineData("AA\t==")]
    [InlineData("AA\n==")]
    [InlineData("qqqqqqqqqqqqqqqqqqqq qqqqqqqqqqqqqqqqqqqqqqo=")]
    // 字母表外（含 URL-safe 的 - 与 _：标准 base64 不存在这两种字符）
    [InlineData("-_-_")]
    [InlineData("AA--")]
    [InlineData("AA__")]
    [InlineData("AA 0")]
    public void Rejects_Everything_Not_Canonical(string? value)
    {
        Assert.False(CanonicalBase64.TryDecode(value, out byte[] bytes));
        Assert.Empty(bytes);
    }

    /// <summary>
    /// 失败时 out 恒为空数组：不返回部分解码结果（与 <c>CertificatePin.TryDecode</c> 的约定一致）。
    /// </summary>
    [Fact]
    public void Failure_Leaves_Empty_Bytes()
    {
        Assert.False(CanonicalBase64.TryDecode("AB==", out byte[] bytes));
        Assert.Same(Array.Empty<byte>(), bytes);
    }

    /// <summary>非法 UTF-16 / 代理码元不因编码器崩溃，一律返回 false。</summary>
    [Fact]
    public void Rejects_Lone_Surrogate_Without_Throwing()
    {
        Assert.False(CanonicalBase64.TryDecode("\uD800", out _));
        Assert.False(CanonicalBase64.TryDecode("AA\uD800==", out _));
    }

    /// <summary>BOM 不是空白，作为普通字符被拒。</summary>
    [Fact]
    public void Rejects_Bom_As_Ordinary_Character()
    {
        Assert.False(CanonicalBase64.TryDecode("\uFEFF" + "AA==", out _));
    }
}
