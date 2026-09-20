using System.IO;
using System.Text;
using LanRemote.Core.Encoding;
using LanRemote.Core.Models;
using LanRemote.Security.Secrets;
using Xunit;

namespace LanRemote.Security.Tests;

/// <summary>
/// DPAPI 访问密钥存储的持久化、轮换与隔离性测试。
/// </summary>
public sealed class DpapiAccessSecretStoreTests : IDisposable
{
    private readonly TempSecretRoot _root = new();
    private readonly LogSink _logs = new();

    public void Dispose() => _root.Dispose();

    private (DpapiSecretVault Vault, DpapiAccessSecretStore Store) CreateStore()
    {
        DpapiSecretVault vault = new(_root.Paths, new CapturingLogger<DpapiSecretVault>(_logs));
        DpapiAccessSecretStore store = new(vault, new CapturingLogger<DpapiAccessSecretStore>(_logs));
        return (vault, store);
    }

    [Fact]
    public async Task LoadOrCreateAsync_CreatesSecretsFile()
    {
        (_, DpapiAccessSecretStore store) = CreateStore();

        await store.LoadOrCreateAsync();

        Assert.True(File.Exists(_root.Paths.SecretsFilePath));
    }

    [Fact]
    public async Task LoadOrCreateAsync_Returns128BitKey()
    {
        (_, DpapiAccessSecretStore store) = CreateStore();

        AccessSecret secret = await store.LoadOrCreateAsync();

        Assert.Equal(128 / 8, secret.AccessKeyBytes.Length);
    }

    [Fact]
    public async Task LoadOrCreateAsync_IsStableAcrossRestart()
    {
        (_, DpapiAccessSecretStore firstBoot) = CreateStore();
        AccessSecret first = await firstBoot.LoadOrCreateAsync();

        // 模拟进程重启：全新的 vault 与 store，只共享磁盘上的 secrets.bin。
        (_, DpapiAccessSecretStore secondBoot) = CreateStore();
        AccessSecret second = await secondBoot.LoadOrCreateAsync();

        Assert.Equal(first.AccessKeyBytes, second.AccessKeyBytes);
    }

    [Fact]
    public async Task RegenerateAsync_ProducesDifferentKeyImmediately()
    {
        (_, DpapiAccessSecretStore store) = CreateStore();
        AccessSecret original = await store.LoadOrCreateAsync();

        AccessSecret rotated = await store.RegenerateAsync();

        Assert.NotEqual(original.AccessKeyBytes, rotated.AccessKeyBytes);
    }

    [Fact]
    public async Task RegenerateAsync_OldKeyIsNoLongerServable()
    {
        (_, DpapiAccessSecretStore store) = CreateStore();
        AccessSecret original = await store.LoadOrCreateAsync();

        await store.RegenerateAsync();

        // 旧密钥立即失效的实现方式：存储里只保留最新值，
        // 下一次读取（= 下一次认证比较）拿到的必然是新值，不存在 TTL 或缓存过渡。
        AccessSecret current = await store.LoadOrCreateAsync();

        Assert.NotEqual(original.AccessKeyBytes, current.AccessKeyBytes);
    }

    [Fact]
    public async Task RegenerateAsync_IsPersistedAcrossRestart()
    {
        (_, DpapiAccessSecretStore store) = CreateStore();
        AccessSecret rotated = await store.RegenerateAsync();

        (_, DpapiAccessSecretStore rebooted) = CreateStore();
        AccessSecret afterReboot = await rebooted.LoadOrCreateAsync();

        Assert.Equal(rotated.AccessKeyBytes, afterReboot.AccessKeyBytes);
    }

    [Fact]
    public async Task RepeatedRegeneration_ProducesUniqueKeys()
    {
        (_, DpapiAccessSecretStore store) = CreateStore();

        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int i = 0; i < 10; i++)
        {
            AccessSecret secret = await store.RegenerateAsync();
            Assert.True(seen.Add(Convert.ToHexString(secret.AccessKeyBytes)));
        }
    }

    [Fact]
    public async Task SecretsFile_DoesNotContainPlaintextKey()
    {
        (_, DpapiAccessSecretStore store) = CreateStore();
        AccessSecret secret = await store.LoadOrCreateAsync();

        string keyBase32 = CrockfordBase32.Encode(secret.AccessKeyBytes);
        byte[] fileBytes = await File.ReadAllBytesAsync(_root.Paths.SecretsFilePath);

        string raw = Encoding.Latin1.GetString(fileBytes);
        Assert.DoesNotContain(keyBase32, raw, StringComparison.OrdinalIgnoreCase);

        // 原始 16 字节也不应以明文出现在文件里。
        Assert.Equal(-1, IndexOfSequence(fileBytes, secret.AccessKeyBytes));
    }

    [Fact]
    public async Task SecretsFile_StartsWithExpectedHeader()
    {
        (_, DpapiAccessSecretStore store) = CreateStore();
        await store.LoadOrCreateAsync();

        byte[] fileBytes = await File.ReadAllBytesAsync(_root.Paths.SecretsFilePath);

        Assert.True(fileBytes.Length > SecretFile.HeaderBytes);
        Assert.Equal(SecretFile.Magic, Encoding.ASCII.GetString(fileBytes, 0, 4));
        Assert.Equal(SecretFile.CurrentVersion, fileBytes[4]);
    }

    [Fact]
    public async Task CorruptSecretsFile_DoesNotSilentlyRegenerate()
    {
        (_, DpapiAccessSecretStore store) = CreateStore();
        await store.LoadOrCreateAsync();

        // 用不属于 DPAPI 的垃圾覆盖文件。
        await File.WriteAllBytesAsync(_root.Paths.SecretsFilePath, "not a secret file"u8.ToArray());

        (_, DpapiAccessSecretStore rebooted) = CreateStore();

        // 静默重建会把「文件损坏」伪装成「首次运行」，这是不能接受的。
        await Assert.ThrowsAsync<InvalidDataException>(() => rebooted.LoadOrCreateAsync());
    }

    [Fact]
    public async Task SecretNeverAppearsInLogs()
    {
        (_, DpapiAccessSecretStore store) = CreateStore();

        AccessSecret secret = await store.LoadOrCreateAsync();
        string keyBase32 = CrockfordBase32.Encode(secret.AccessKeyBytes);
        string keyHex = Convert.ToHexString(secret.AccessKeyBytes);

        await store.RegenerateAsync();

        Assert.False(_logs.Contains(keyBase32));
        Assert.False(_logs.Contains(keyHex));

        // 反向验证：如果日志真的空空如也，这个断言就没意义了。
        Assert.NotEmpty(_logs.Messages);
    }

    private static int IndexOfSequence(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }
}
