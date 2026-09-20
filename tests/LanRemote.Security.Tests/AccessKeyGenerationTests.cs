using System.IO;
using System.Security.Cryptography;
using LanRemote.Core.Encoding;
using LanRemote.Core.Models;
using LanRemote.Security.Secrets;
using Xunit;

namespace LanRemote.Security.Tests;

/// <summary>
/// 128-bit 访问密钥的生成质量与 fmt。
/// </summary>
public sealed class AccessKeyGenerationTests
{
    [Fact]
    public void NewAccessKeyBytes_IsSixteenBytes()
    {
        byte[] key = SecretGenerator.NewAccessKeyBytes();

        Assert.Equal(16, key.Length);
        Assert.Equal(AccessSecret.AccessKeyByteLength, key.Length);
    }

    [Fact]
    public void NewAccessKey_EncodesToTwentySixBase32Characters()
    {
        string encoded = SecretGenerator.NewAccessKey();

        Assert.Equal(26, encoded.Length);
        Assert.All(encoded, c => Assert.Contains(c, CrockfordBase32.Alphabet));
    }

    [Fact]
    public void GeneratedKeysAreUnique()
    {
        // 如果有人换成固定密码或 Random()，这里会立刻挂掉。
        HashSet<string> keys = new(StringComparer.Ordinal);
        for (int i = 0; i < 200; i++)
        {
            Assert.True(keys.Add(SecretGenerator.NewAccessKey()));
        }
    }

    [Fact]
    public void GeneratedKeysHaveNoTrivialPattern()
    {
        for (int i = 0; i < 50; i++)
        {
            byte[] key = SecretGenerator.NewAccessKeyBytes();

            // 排除「全零」「全 FF」「严格递增」这类明显非随机的形态。
            Assert.False(key.All(b => b == 0x00));
            Assert.False(key.All(b => b == 0xFF));
            Assert.False(Enumerable.Range(0, key.Length).All(i => key[i] == (byte)i));
        }
    }

    [Fact]
    public void TryDecodeAccessKey_RoundTripsGeneratedKey()
    {
        byte[] key = SecretGenerator.NewAccessKeyBytes();
        string encoded = CrockfordBase32.Encode(key);

        Assert.True(SecretGenerator.TryDecodeAccessKey(encoded, out byte[] decoded));
        Assert.Equal(key, decoded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ZZZZ")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ0")]
    public void TryDecodeAccessKey_RejectsMalformedInput(string malformed)
    {
        Assert.False(SecretGenerator.TryDecodeAccessKey(malformed, out byte[] decoded));
        Assert.Empty(decoded);
    }
}
