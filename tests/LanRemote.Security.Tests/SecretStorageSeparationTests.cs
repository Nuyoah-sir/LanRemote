using System.IO;
using LanRemote.Core.Configuration;
using LanRemote.Core.Encoding;
using LanRemote.Core.Models;
using LanRemote.Security.Secrets;
using Xunit;

namespace LanRemote.Security.Tests;

/// <summary>
/// secret 与 config 的职责分离。
/// </summary>
/// <remarks>
/// 02_PRODUCT_SPEC.md 第 9 节：秘密只能写 <c>secrets.bin</c>，禁止写 <c>config.json</c>。
/// 这组用例把「分离」变成可执行断言，而不是靠人记住。
/// </remarks>
public sealed class SecretStorageSeparationTests : IDisposable
{
    private readonly TempSecretRoot _root = new();
    private readonly LogSink _logs = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public async Task SecretIsWrittenToSecretsBin_AndNowhereElse()
    {
        DpapiSecretVault vault = new(_root.Paths, new CapturingLogger<DpapiSecretVault>(_logs));
        DpapiAccessSecretStore store = new(vault, new CapturingLogger<DpapiAccessSecretStore>(_logs));
        AppConfigStore configStore = new(_root.Paths);

        await store.LoadOrCreateAsync();
        await configStore.SaveAsync(new AppConfig());

        Assert.True(File.Exists(_root.Paths.SecretsFilePath));
        Assert.True(File.Exists(_root.Paths.ConfigFilePath));

        string[] filesInRoot = Directory.GetFiles(_root.Paths.RootDirectory);
        Assert.Contains(_root.Paths.SecretsFilePath, filesInRoot);
        Assert.Contains(_root.Paths.ConfigFilePath, filesInRoot);
    }

    [Fact]
    public async Task ConfigJsonNeverContainsSecretMaterial()
    {
        DpapiSecretVault vault = new(_root.Paths, new CapturingLogger<DpapiSecretVault>(_logs));
        DpapiAccessSecretStore store = new(vault, new CapturingLogger<DpapiAccessSecretStore>(_logs));
        AppConfigStore configStore = new(_root.Paths);

        AccessSecret secret = await store.LoadOrCreateAsync();
        await configStore.SaveAsync(new AppConfig());

        string configText = await File.ReadAllTextAsync(_root.Paths.ConfigFilePath);
        string keyBase32 = CrockfordBase32.Encode(secret.AccessKeyBytes);

        Assert.DoesNotContain(keyBase32, configText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToHexString(secret.AccessKeyBytes), configText, StringComparison.OrdinalIgnoreCase);

        // 连「字段名」都不应该出现：如果出现说明有人把 secret 建模进了配置类型。
        Assert.DoesNotContain("accessKey", configText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deviceGuid", configText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("certificate", configText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeviceGuidIsStoredInSecretsBinNotConfigJson()
    {
        DpapiSecretVault vault = new(_root.Paths, new CapturingLogger<DpapiSecretVault>(_logs));
        AppConfigStore configStore = new(_root.Paths);

        Guid deviceGuid = await vault.ReadAsync(bundle => bundle.DeviceGuid);
        await configStore.SaveAsync(new AppConfig());

        string configText = await File.ReadAllTextAsync(_root.Paths.ConfigFilePath);
        Assert.DoesNotContain(deviceGuid.ToString(), configText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SecretsBinIsProtectedByDpapi()
    {
        DpapiSecretVault vault = new(_root.Paths, new CapturingLogger<DpapiSecretVault>(_logs));
        await vault.ReadAsync(bundle => bundle.DeviceGuid);

        byte[] fileBytes = await File.ReadAllBytesAsync(_root.Paths.SecretsFilePath);
        string text = System.Text.Encoding.Latin1.GetString(fileBytes, SecretFile.HeaderBytes, fileBytes.Length - SecretFile.HeaderBytes);

        // DPAPI 密文不应该是可读 JSON。
        Assert.DoesNotContain("deviceGuid", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accessKey", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SecretFileIsRejectedWhenMagicIsWrong()
    {
        byte[] tampered = "XXXX"u8.ToArray().Concat(new byte[] { 1, 0, 0, 0, 5, 0, 0, 0, 0 }).ToArray();
        await File.WriteAllBytesAsync(_root.Paths.SecretsFilePath, tampered);

        DpapiSecretVault vault = new(_root.Paths, new CapturingLogger<DpapiSecretVault>(_logs));

        await Assert.ThrowsAsync<InvalidDataException>(() => vault.ReadAsync(bundle => bundle.DeviceGuid));
    }
}
