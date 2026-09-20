using System.Security.Cryptography;
using LanRemote.Core.Models;
using LanRemote.Security.Certificates;
using LanRemote.Security.Identity;
using LanRemote.Security.Secrets;
using Xunit;

namespace LanRemote.Security.Tests;

/// <summary>
/// 本机身份（deviceGuid / 设备码 / 证书指纹）的稳定性测试。
/// </summary>
public sealed class DeviceIdentityTests : IDisposable
{
    private readonly TempSecretRoot _root = new();
    private readonly LogSink _logs = new();
    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (IDisposable disposable in _disposables)
        {
            disposable.Dispose();
        }

        _root.Dispose();
    }

    private DeviceIdentityService CreateIdentityService()
    {
        DpapiSecretVault vault = new(_root.Paths, new CapturingLogger<DpapiSecretVault>(_logs));
        DeviceCertificateService certificates =
            new(vault, new CapturingLogger<DeviceCertificateService>(_logs));
        _disposables.Add(certificates);

        return new DeviceIdentityService(
            vault,
            certificates,
            new CapturingLogger<DeviceIdentityService>(_logs));
    }

    [Fact]
    public async Task Identity_IsStableAcrossRestart()
    {
        DeviceIdentity first = await CreateIdentityService().GetOrCreateAsync();

        DeviceIdentity second = await CreateIdentityService().GetOrCreateAsync();

        Assert.Equal(first.DeviceId, second.DeviceId);
        Assert.Equal(first.DeviceCode, second.DeviceCode);
        Assert.Equal(first.CertificateSha256, second.CertificateSha256);
    }

    [Fact]
    public async Task Identity_DeviceNameIsMachineName()
    {
        DeviceIdentity identity = await CreateIdentityService().GetOrCreateAsync();

        Assert.Equal(Environment.MachineName, identity.DeviceName);
    }

    [Fact]
    public async Task Identity_DeviceCodeHasDisplayFormat()
    {
        DeviceIdentity identity = await CreateIdentityService().GetOrCreateAsync();

        Assert.Matches(@"^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$", identity.DeviceCode);
    }

    [Fact]
    public async Task Identity_FingerprintIsUpperHexSixtyFourChars()
    {
        DeviceIdentity identity = await CreateIdentityService().GetOrCreateAsync();

        Assert.Equal(64, identity.CertificateSha256.Length);
        Assert.All(identity.CertificateSha256, c => Assert.Contains(c, "0123456789ABCDEF"));
    }

    [Fact]
    public async Task Identity_DifferentRootsProduceDifferentDevices()
    {
        DeviceIdentity first = await CreateIdentityService().GetOrCreateAsync();

        using TempSecretRoot otherRoot = new();
        DpapiSecretVault otherVault = new(otherRoot.Paths, new CapturingLogger<DpapiSecretVault>(_logs));
        using DeviceCertificateService otherCertificates =
            new(otherVault, new CapturingLogger<DeviceCertificateService>(_logs));
        DeviceIdentityService otherIdentity = new(
            otherVault,
            otherCertificates,
            new CapturingLogger<DeviceIdentityService>(_logs));

        DeviceIdentity second = await otherIdentity.GetOrCreateAsync();

        Assert.NotEqual(first.DeviceId, second.DeviceId);
        Assert.NotEqual(first.DeviceCode, second.DeviceCode);
        Assert.NotEqual(first.CertificateSha256, second.CertificateSha256);
    }

    [Fact]
    public async Task RotatingAccessKeyDoesNotChangeIdentity()
    {
        DpapiSecretVault vault = new(_root.Paths, new CapturingLogger<DpapiSecretVault>(_logs));
        DeviceCertificateService certificates =
            new(vault, new CapturingLogger<DeviceCertificateService>(_logs));
        _disposables.Add(certificates);
        DeviceIdentityService identityService =
            new(vault, certificates, new CapturingLogger<DeviceIdentityService>(_logs));
        DpapiAccessSecretStore store = new(vault, new CapturingLogger<DpapiAccessSecretStore>(_logs));

        DeviceIdentity before = await identityService.GetOrCreateAsync();

        await store.RegenerateAsync();

        // 重新生成密钥只覆盖 bundle 的 AccessKey 字段，deviceGuid 与证书都原样保留。
        DeviceIdentity after = await identityService.GetOrCreateAsync();

        Assert.Equal(before.DeviceId, after.DeviceId);
        Assert.Equal(before.DeviceCode, after.DeviceCode);
        Assert.Equal(before.CertificateSha256, after.CertificateSha256);
    }

    [Fact]
    public async Task IdentityLogsNeverContainCertificatePrivateMaterial()
    {
        DeviceIdentity identity = await CreateIdentityService().GetOrCreateAsync();

        // 设备码允许出现在日志里（它不是秘密），但指纹之外的密钥材料不行。
        Assert.NotEmpty(_logs.Messages);
        Assert.All(_logs.Messages, m => Assert.DoesNotContain("BEGIN PRIVATE KEY", m));
        Assert.All(_logs.Messages, m => Assert.DoesNotContain("PRIVATE KEY", m));

        _ = identity;
    }
}
