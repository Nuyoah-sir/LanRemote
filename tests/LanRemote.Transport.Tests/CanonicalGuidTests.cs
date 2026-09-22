using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

/// <summary>
/// <see cref="CanonicalGuid"/>：canonical uuid 串（小写 <c>D</c> 格式）的定义与测试。
/// </summary>
/// <remarks>
/// <para><see cref="Guid.TryParseExact(string, string, out Guid)"/> 的 <c>"D"</c> 格式容忍大写 hex，
/// round-trip 把它收窄到「小写」这一种 wire 表示。transcript 无论如何都会以
/// <c>ToString("D")</c> 重新格式化，因此本判定的价值在「wire 表示唯一化 + 早拒非规范输入」。</para>
/// <para>值语义（是否 <see cref="Guid.Empty"/>、是否与已知设备号一致）<b>不在本层</b>判定——
/// 那属于认证状态机。</para>
/// </remarks>
public sealed class CanonicalGuidTests
{
    [Theory]
    [InlineData("11111111-2222-3333-4444-555555555555")]
    [InlineData("0f8fad5b-d9cb-469f-a165-70867728950e")]
    // 全零 uuid 在形状上是 canonical；「空值是否合法」由消费方判定（此处显式记录事实）
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Accepts_Lowercase_D_Format(string value)
    {
        Assert.True(CanonicalGuid.TryParse(value, out Guid guid));
        Assert.Equal(value, guid.ToString("D"));
    }

    [Fact]
    public void Parses_The_Expected_Value()
    {
        Assert.True(CanonicalGuid.TryParse("11111111-2222-3333-4444-555555555555", out Guid guid));
        Assert.Equal(new Guid("11111111-2222-3333-4444-555555555555"), guid);
    }

    /// <summary>
    /// 往返扫掠：<c>ToString("D")</c> 的产物必被接受；
    /// 其余五种标准格式（B / N / P / X）必被拒——wire 上只允许一种形态。
    /// </summary>
    [Fact]
    public void Accepts_Only_D_Format_Among_Standard_Formats()
    {
        for (int i = 0; i < 16; i++)
        {
            Guid guid = Guid.NewGuid();

            Assert.True(CanonicalGuid.TryParse(guid.ToString("D"), out Guid parsed));
            Assert.Equal(guid, parsed);

            foreach (string format in new[] { "B", "N", "P", "X" })
            {
                Assert.False(
                    CanonicalGuid.TryParse(guid.ToString(format), out _),
                    $"格式 {format} 被误接受：{guid.ToString(format)}");
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    // 大写：TryParseExact("D") 容忍，round-trip 拒绝
    [InlineData("0F8FAD5B-D9CB-469F-A165-70867728950E")]
    // 括号 / 无连字符 / 其它格式
    [InlineData("{11111111-2222-3333-4444-555555555555}")]
    [InlineData("11111111222233334444555555555555")]
    [InlineData("(11111111-2222-3333-4444-555555555555)")]
    // 空白
    [InlineData("11111111-2222-3333-4444-555555555555 ")]
    [InlineData(" 11111111-2222-3333-4444-555555555555")]
    // 段长不对 / 非 hex
    [InlineData("1111-2222-3333-4444-555555555555")]
    [InlineData("11111111-2222-3333-4444-55555555555")]
    [InlineData("11111111-2222-3333-4444-5555555555555")]
    [InlineData("gggggggg-2222-3333-4444-555555555555")]
    // 全角 / 代理码元：不崩溃，直接拒绝
    [InlineData("１１１１１１１１-２２２２-３３３３-４４４４-５５５５５５５５５５５５")]
    [InlineData("\uD800")]
    public void Rejects_Everything_Not_Canonical(string? value)
    {
        Assert.False(CanonicalGuid.TryParse(value, out Guid guid));
        Assert.Equal(Guid.Empty, guid);
    }
}
