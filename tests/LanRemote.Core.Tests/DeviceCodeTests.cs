using LanRemote.Core.Identity;
using Xunit;

namespace LanRemote.Core.Tests;

/// <summary>
/// 设备码派生与格式测试。
/// </summary>
/// <remarks>
/// 对应 01_MASTER_PROMPT.md 第 7.1 节：deviceGuid → SHA-256 → 前 5 字节 → 8 个 Base32 字符 → <c>7K3M-P9QX</c>。
/// </remarks>
public sealed class DeviceCodeTests
{
    [Fact]
    public void Derive_IsDeterministic()
    {
        Guid guid = Guid.NewGuid();

        string first = DeviceCode.Derive(guid);
        string second = DeviceCode.Derive(guid);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Derive_HasFormattedShape()
    {
        Guid guid = Guid.NewGuid();

        string code = DeviceCode.Derive(guid);

        Assert.Equal(DeviceCode.EncodedLength + 1, code.Length);
        Assert.Equal('-', code[4]);
    }

    [Fact]
    public void DeriveRaw_HasEightCharactersAndNoSeparator()
    {
        Guid guid = Guid.NewGuid();

        string raw = DeviceCode.DeriveRaw(guid);

        Assert.Equal(DeviceCode.EncodedLength, raw.Length);
        Assert.DoesNotContain('-', raw);
    }

    [Fact]
    public void Derive_DifferentGuidsProduceDifferentCodes()
    {
        HashSet<string> codes = new(StringComparer.Ordinal);

        // 设备码空间为 40 bit；500 个样本的碰撞概率约 10^-7，足以当作确定性断言。
        for (int i = 0; i < 500; i++)
        {
            Assert.True(codes.Add(DeviceCode.Derive(Guid.NewGuid())));
        }
    }

    [Fact]
    public void Derive_UsesOnlyBase32Alphabet()
    {
        foreach (Guid guid in Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()))
        {
            string raw = DeviceCode.DeriveRaw(guid);
            Assert.All(raw, c => Assert.Contains(c, Encoding.CrockfordBase32.Alphabet));
        }
    }

    [Fact]
    public void IsWellFormed_AcceptsDerivedCodeInAnyForm()
    {
        string code = DeviceCode.Derive(Guid.NewGuid());

        Assert.True(DeviceCode.IsWellFormed(code));
        Assert.True(DeviceCode.IsWellFormed(code.Replace("-", string.Empty, StringComparison.Ordinal)));
        Assert.True(DeviceCode.IsWellFormed(code.ToLowerInvariant()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("7K3M")]
    [InlineData("7K3M-P9QX-EXTRA")]
    [InlineData("7K3M-P9QU")] // U 不是合法字符
    public void IsWellFormed_RejectsBadInput(string? input)
    {
        Assert.False(DeviceCode.IsWellFormed(input));
    }
}
