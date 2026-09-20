using LanRemote.Core.Models;
using Xunit;

namespace LanRemote.Security.Tests;

/// <summary>
/// 访问密钥强度不变量测试。
/// </summary>
/// <remarks>
/// ADR-004 明确禁止把 128-bit 随机 key 降级为 6 位或其他弱密码；
/// 04_PROTOCOL_AND_SECURITY.md 第 8 节要求 <c>RandomNumberGenerator.GetBytes(16)</c>。
/// 这组断言是防止后续施工偷懒降级的护栏。M1 实现生成逻辑后需补充
/// “regenerate 后必变 / 存储为 DPAPI 密文 / 不落日志”的测试。
/// </remarks>
public sealed class AccessKeyStrengthTests
{
    [Fact]
    public void AccessKeyIsExactly128Bit()
    {
        Assert.Equal(128, AccessSecret.AccessKeyBitLength);
        Assert.Equal(16, AccessSecret.AccessKeyByteLength);
    }

    [Fact]
    public void ByteLengthMatchesBitLength()
    {
        Assert.Equal(AccessSecret.AccessKeyBitLength / 8, AccessSecret.AccessKeyByteLength);
    }

    [Fact]
    public void KeyLengthIsLongEnoughToResistOnlineGuessing()
    {
        // 6 位数字密码空间约 10^6；128-bit 至少高出 30 个数量级。
        Assert.True(
            AccessSecret.AccessKeyBitLength >= 96,
            "低于 96 bit 不应被认为是安全的访问密钥。");
    }

    [Fact]
    public void AccessSecretExposesKeyMaterialAsBytes()
    {
        byte[] key = new byte[AccessSecret.AccessKeyByteLength];
        Random.Shared.NextBytes(key);

        AccessSecret secret = new(key);

        Assert.Same(key, secret.AccessKeyBytes);
        Assert.Equal(AccessSecret.AccessKeyByteLength, secret.AccessKeyBytes.Length);
    }
}
