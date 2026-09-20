using System.Security.Cryptography;
using System.Text;
using LanRemote.Security.Secrets;
using Xunit;

namespace LanRemote.Security.Tests;

/// <summary>
/// <c>secrets.bin</c> 二进制格式的严格性测试。
/// </summary>
/// <remarks>
/// 05 / M1.1：长度必须严格相等、版本必须匹配，任何拼接或篡改都必须被拒绝，
/// 而不是「尽力读出来」。
/// </remarks>
public sealed class SecretFileFormatTests : IDisposable
{
    private readonly TempSecretRoot _root = new();
    private readonly LogSink _logs = new();

    public void Dispose() => _root.Dispose();

    private async Task<byte[]> CreateValidFileAsync()
    {
        DpapiSecretVault vault = new(_root.Paths, new CapturingLogger<DpapiSecretVault>(_logs));
        await vault.ReadAsync(bundle => bundle.DeviceGuid);

        return await File.ReadAllBytesAsync(_root.Paths.SecretsFilePath);
    }

    [Fact]
    public async Task AppendedTrailingBytes_AreRejected()
    {
        byte[] valid = await CreateValidFileAsync();

        byte[] tampered = valid.Concat(new byte[] { 0x00, 0x01, 0x02 }).ToArray();
        await File.WriteAllBytesAsync(_root.Paths.SecretsFilePath, tampered);

        DpapiSecretVault vault = new(_root.Paths, new CapturingLogger<DpapiSecretVault>(_logs));

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => vault.ReadAsync(bundle => bundle.DeviceGuid));

        Assert.Contains("长度与声明不一致", exception.Message);
    }

    [Fact]
    public async Task TruncatedFile_IsRejected()
    {
        byte[] valid = await CreateValidFileAsync();

        byte[] truncated = valid[..(valid.Length - 4)];
        await File.WriteAllBytesAsync(_root.Paths.SecretsFilePath, truncated);

        DpapiSecretVault vault = new(_root.Paths, new CapturingLogger<DpapiSecretVault>(_logs));

        await Assert.ThrowsAsync<InvalidDataException>(() => vault.ReadAsync(bundle => bundle.DeviceGuid));
    }

    [Fact]
    public async Task BundleVersionMismatchInsidePayload_IsRejected()
    {
        // 手工构造一个「头部版本正确、payload 内 bundle.Version 却是未来版本」的文件。
        string json = """
            {"version":99,"deviceGuid":"11111111-2222-3333-4444-555555555555","accessKey":"ABCDEFGHIJKLMNOPQRSTUVWXYZ0"}
            """;

        byte[] plaintext = Encoding.UTF8.GetBytes(json);
        byte[] payload = DataProtection.Protect(plaintext);

        byte[] file = new byte[SecretFile.HeaderBytes + payload.Length];
        Encoding.ASCII.GetBytes(SecretFile.Magic).CopyTo(file, 0);
        file[4] = SecretFile.CurrentVersion;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(5, 4), (uint)payload.Length);
        payload.CopyTo(file, SecretFile.HeaderBytes);

        await File.WriteAllBytesAsync(_root.Paths.SecretsFilePath, file);

        DpapiSecretVault vault = new(_root.Paths, new CapturingLogger<DpapiSecretVault>(_logs));

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => vault.ReadAsync(bundle => bundle.DeviceGuid));

        Assert.Contains("版本", exception.Message);
    }

    [Fact]
    public async Task WrongHeaderVersion_IsRejected()
    {
        byte[] valid = await CreateValidFileAsync();

        valid[4] = 9;
        await File.WriteAllBytesAsync(_root.Paths.SecretsFilePath, valid);

        DpapiSecretVault vault = new(_root.Paths, new CapturingLogger<DpapiSecretVault>(_logs));

        await Assert.ThrowsAsync<InvalidDataException>(() => vault.ReadAsync(bundle => bundle.DeviceGuid));
    }

    [Fact]
    public void PackProtected_SetsCurrentVersion()
    {
        SecretBundle bundle = new() { Version = 12345 };

        byte[] packed = SecretFile.PackProtected(bundle);

        Assert.Equal(SecretFile.CurrentVersion, bundle.Version);
        Assert.Equal(SecretFile.CurrentVersion, packed[4]);
    }
}
