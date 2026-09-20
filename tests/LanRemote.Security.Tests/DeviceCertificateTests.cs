using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LanRemote.Security.Certificates;
using LanRemote.Security.Secrets;
using Xunit;

namespace LanRemote.Security.Tests;

/// <summary>
/// 自签名 ECDSA P-256 设备证书的创建、持久化与重新加载。
/// </summary>
public sealed class DeviceCertificateTests : IDisposable
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

    private (DpapiSecretVault Vault, DeviceCertificateService Certificates) CreateServices()
    {
        DpapiSecretVault vault = new(_root.Paths, new CapturingLogger<DpapiSecretVault>(_logs));
        DeviceCertificateService certificates =
            new(vault, new CapturingLogger<DeviceCertificateService>(_logs));
        _disposables.Add(certificates);
        return (vault, certificates);
    }

    [Fact]
    public async Task GetOrCreateAsync_ReturnsCertificateWithPrivateKey()
    {
        (_, DeviceCertificateService certificates) = CreateServices();

        DeviceCertificate certificate = await certificates.GetOrCreateAsync();

        Assert.True(certificate.Certificate.HasPrivateKey);
    }

    [Fact]
    public async Task GetOrCreateAsync_UsesEcdsaP256()
    {
        (_, DeviceCertificateService certificates) = CreateServices();

        DeviceCertificate certificate = await certificates.GetOrCreateAsync();

        // M1.1 / ADR-018：私钥以 EphemeralKeySet 载入，测试不再依赖私钥导出。
        // 曲线信息本身是公开的，用公钥即可确认。
        using ECDsa? publicKey = certificate.Certificate.GetECDsaPublicKey();
        Assert.NotNull(publicKey);

        ECParameters parameters = publicKey.ExportParameters(includePrivateParameters: false);
        Assert.Equal(ECCurve.NamedCurves.nistP256.Oid.Value, parameters.Curve.Oid.Value);
        Assert.Equal(256, publicKey.KeySize);
    }

    [Fact]
    public void ImportFlags_UsesEphemeralKeySetOnly()
    {
        // 防止以后「为了让测试好过」把 PersistKeySet / Exportable 加回来（ADR-018）。
        Assert.Equal(X509KeyStorageFlags.EphemeralKeySet, DeviceCertificateService.ImportFlags);
        Assert.False(DeviceCertificateService.ImportFlags.HasFlag(X509KeyStorageFlags.PersistKeySet));
        Assert.False(DeviceCertificateService.ImportFlags.HasFlag(X509KeyStorageFlags.Exportable));
        Assert.False(DeviceCertificateService.ImportFlags.HasFlag(X509KeyStorageFlags.MachineKeySet));
    }

    [Fact]
    public async Task Fingerprint_IsSixtyFourUppercaseHexCharacters()
    {
        (_, DeviceCertificateService certificates) = CreateServices();

        DeviceCertificate certificate = await certificates.GetOrCreateAsync();

        Assert.Equal(64, certificate.Sha256FingerprintHex.Length);
        Assert.Equal(
            certificate.Sha256FingerprintHex.ToUpperInvariant(),
            certificate.Sha256FingerprintHex);
    }

    [Fact]
    public async Task Fingerprint_MatchesCertificateDerHash()
    {
        (_, DeviceCertificateService certificates) = CreateServices();

        DeviceCertificate certificate = await certificates.GetOrCreateAsync();

        string expected = Convert.ToHexString(SHA256.HashData(certificate.Certificate.RawData));
        Assert.Equal(expected, certificate.Sha256FingerprintHex);
    }

    [Fact]
    public async Task Fingerprint_IsStableAcrossRestart()
    {
        (_, DeviceCertificateService firstBoot) = CreateServices();
        DeviceCertificate first = await firstBoot.GetOrCreateAsync();

        // 模拟重启：新的 vault 与新的证书服务，只共享磁盘状态。
        (_, DeviceCertificateService secondBoot) = CreateServices();
        DeviceCertificate second = await secondBoot.GetOrCreateAsync();

        Assert.Equal(first.Sha256FingerprintHex, second.Sha256FingerprintHex);
    }

    [Fact]
    public async Task PrivateKeyCanBeReloadedAfterRestart()
    {
        (_, DeviceCertificateService firstBoot) = CreateServices();
        await firstBoot.GetOrCreateAsync();

        (_, DeviceCertificateService secondBoot) = CreateServices();
        DeviceCertificate reloaded = await secondBoot.GetOrCreateAsync();

        Assert.True(reloaded.Certificate.HasPrivateKey);

        using ECDsa? privateKey = reloaded.Certificate.GetECDsaPrivateKey();
        Assert.NotNull(privateKey);
    }

    [Fact]
    public async Task ReloadedCertificateCanSign()
    {
        (_, DeviceCertificateService firstBoot) = CreateServices();
        await firstBoot.GetOrCreateAsync();

        (_, DeviceCertificateService secondBoot) = CreateServices();
        DeviceCertificate reloaded = await secondBoot.GetOrCreateAsync();

        using ECDsa? privateKey = reloaded.Certificate.GetECDsaPrivateKey();
        Assert.NotNull(privateKey);

        byte[] signature = privateKey.SignData("lanremote"u8.ToArray(), HashAlgorithmName.SHA256);
        using ECDsa? publicKey = reloaded.Certificate.GetECDsaPublicKey();
        Assert.NotNull(publicKey);
        Assert.True(publicKey.VerifyData("lanremote"u8.ToArray(), signature, HashAlgorithmName.SHA256));
    }

    [Fact]
    public async Task SubjectContainsOnlySoftwareIdentifier()
    {
        (DpapiSecretVault vault, DeviceCertificateService certificates) = CreateServices();

        DeviceCertificate certificate = await certificates.GetOrCreateAsync();

        string subject = certificate.Certificate.Subject;
        Assert.StartsWith("CN=LanRemote-", subject, StringComparison.Ordinal);

        // Subject 不能泄漏机器名、用户名等身份信息（04_PROTOCOL_AND_SECURITY.md 第 7 节）。
        Assert.DoesNotContain(Environment.MachineName, subject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, subject, StringComparison.OrdinalIgnoreCase);

        Guid deviceGuid = await vault.ReadAsync(bundle => bundle.DeviceGuid);
        _ = deviceGuid; // 设备码本身不是秘密，Subject 里含它是允许的。
    }

    [Fact]
    public async Task CertificateIsValidForFiveYearsAndIsNotCa()
    {
        (_, DeviceCertificateService certificates) = CreateServices();

        DeviceCertificate certificate = await certificates.GetOrCreateAsync();
        X509Certificate2 x509 = certificate.Certificate;

        // X509Certificate2 的 NotBefore/NotAfter 返回本地时间，直接与 DateTime.UtcNow
        // 比较会因为时区偏移（本机 UTC+8）而误判，必须统一换算到 UTC。
        DateTime notBeforeUtc = x509.NotBefore.ToUniversalTime();
        DateTime notAfterUtc = x509.NotAfter.ToUniversalTime();

        double years = (notAfterUtc - notBeforeUtc).TotalDays / 365.25;
        Assert.Equal(DeviceCertificateService.ValidityYears, (int)Math.Round(years));
        Assert.True(notBeforeUtc <= DateTime.UtcNow.AddMinutes(1));
        Assert.True(notAfterUtc > DateTime.UtcNow.AddYears(4));

        X509BasicConstraintsExtension? constraints = x509.Extensions
            .OfType<X509BasicConstraintsExtension>()
            .FirstOrDefault();
        Assert.NotNull(constraints);
        Assert.False(constraints.CertificateAuthority);
    }

    [Fact]
    public async Task CertificateDeclaresServerAuthenticationUsage()
    {
        (_, DeviceCertificateService certificates) = CreateServices();

        DeviceCertificate certificate = await certificates.GetOrCreateAsync();

        X509EnhancedKeyUsageExtension? eku = certificate.Certificate.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .FirstOrDefault();

        Assert.NotNull(eku);

        // 1.3.6.1.5.5.7.3.1 = id-kp-serverAuth，M3 做 TLS 服务端时需要。
        Assert.Contains(
            "1.3.6.1.5.5.7.3.1",
            eku.EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value).ToArray());
    }

    [Fact]
    public async Task CertificateKeyUsage_IsDigitalSignatureOnly()
    {
        (_, DeviceCertificateService certificates) = CreateServices();

        DeviceCertificate certificate = await certificates.GetOrCreateAsync();

        X509KeyUsageExtension? keyUsage = certificate.Certificate.Extensions
            .OfType<X509KeyUsageExtension>()
            .FirstOrDefault();

        Assert.NotNull(keyUsage);
        Assert.True(keyUsage.Critical);

        Assert.True(keyUsage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature));

        // RFC 5480：keyEncipherment 不属于 id-ecPublicKey 的 EC certificate profile。
        Assert.False(keyUsage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyEncipherment));

        // 本项目用 ECDSA 签名做 TLS 服务端身份，不做 ECDH 密钥协商。
        Assert.False(keyUsage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyAgreement));

        // 最终 Flags 必须恰好等于 digitalSignature，不多不少。
        Assert.Equal(X509KeyUsageFlags.DigitalSignature, keyUsage.KeyUsages);
    }

    [Fact]
    public async Task CertificateProfile_MatchesEcdsaServerIdentity()
    {
        (_, DeviceCertificateService certificates) = CreateServices();

        DeviceCertificate certificate = await certificates.GetOrCreateAsync();
        X509Certificate2 x509 = certificate.Certificate;

        // 1) ECDSA P-256（只看公钥，私钥以 EphemeralKeySet 载入不宜导出）
        using ECDsa? publicKey = x509.GetECDsaPublicKey();
        Assert.NotNull(publicKey);
        Assert.Equal(
            ECCurve.NamedCurves.nistP256.Oid.Value,
            publicKey.ExportParameters(includePrivateParameters: false).Curve.Oid.Value);
        Assert.Equal(256, publicKey.KeySize);

        // 2) CA=false
        X509BasicConstraintsExtension? constraints = x509.Extensions
            .OfType<X509BasicConstraintsExtension>()
            .FirstOrDefault();
        Assert.NotNull(constraints);
        Assert.False(constraints.CertificateAuthority);

        // 3) KeyUsage = digitalSignature only
        X509KeyUsageExtension? keyUsage = x509.Extensions
            .OfType<X509KeyUsageExtension>()
            .FirstOrDefault();
        Assert.NotNull(keyUsage);
        Assert.Equal(X509KeyUsageFlags.DigitalSignature, keyUsage.KeyUsages);

        // 4) EKU 含 serverAuth
        X509EnhancedKeyUsageExtension? eku = x509.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .FirstOrDefault();
        Assert.NotNull(eku);
        Assert.Contains(
            "1.3.6.1.5.5.7.3.1",
            eku.EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value).ToArray());

        // 5) 公钥算法必须是 id-ecPublicKey（1.2.840.10045.2.1），而不是 RSA
        Assert.Equal("1.2.840.10045.2.1", x509.PublicKey.Oid.Value);
    }

    [Fact]
    public async Task KeyUsageSurvivesReloadFromSecretsBin()
    {
        (_, DeviceCertificateService firstBoot) = CreateServices();
        await firstBoot.GetOrCreateAsync();

        // 冷启动：从 PFX 重新加载后，KeyUsage 必须仍然是 digitalSignature only。
        (_, DeviceCertificateService secondBoot) = CreateServices();
        DeviceCertificate reloaded = await secondBoot.GetOrCreateAsync();

        X509KeyUsageExtension? keyUsage = reloaded.Certificate.Extensions
            .OfType<X509KeyUsageExtension>()
            .FirstOrDefault();

        Assert.NotNull(keyUsage);
        Assert.Equal(X509KeyUsageFlags.DigitalSignature, keyUsage.KeyUsages);
    }

    [Fact]
    public async Task CertificateIsPersistedAndSecretsBinHasNoPlainReadablePfx()
    {
        (_, DeviceCertificateService certificates) = CreateServices();
        await certificates.GetOrCreateAsync();

        byte[] fileBytes = await File.ReadAllBytesAsync(_root.Paths.SecretsFilePath);
        string text = System.Text.Encoding.Latin1.GetString(fileBytes);

        Assert.DoesNotContain("certificatePfx", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CertificateState_PfxWithoutPassword_IsRejected()
    {
        (DpapiSecretVault vault, _) = CreateServices();

        // 制造「只有 PFX、没有口令」的损坏状态。
        await vault.UpdateAsync(
            bundle =>
            {
                bundle.CertificatePfx = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });
                bundle.CertificatePfxPassword = null;
                return true;
            });

        // 换一个新服务从磁盘读，必须显式失败，而不是「顺手」重新签发一张证书。
        (_, DeviceCertificateService certificates) = CreateServices();

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => certificates.GetOrCreateAsync());

        Assert.Contains("证书状态损坏", exception.Message);
    }

    [Fact]
    public async Task CertificateState_PasswordWithoutPfx_IsRejected()
    {
        (DpapiSecretVault vault, _) = CreateServices();

        await vault.UpdateAsync(
            bundle =>
            {
                bundle.CertificatePfx = null;
                bundle.CertificatePfxPassword = SecretGenerator.NewPfxPassword();
                return true;
            });

        (_, DeviceCertificateService certificates) = CreateServices();

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => certificates.GetOrCreateAsync());

        Assert.Contains("证书状态损坏", exception.Message);
    }

    [Fact]
    public async Task ExistingCertificate_LoadDoesNotRewriteSecretsFile()
    {
        (_, DeviceCertificateService firstBoot) = CreateServices();
        DeviceCertificate first = await firstBoot.GetOrCreateAsync();

        byte[] fileAfterCreation = await File.ReadAllBytesAsync(_root.Paths.SecretsFilePath);

        // 模拟冷启动：全新的 vault + 全新的证书服务，只读磁盘。
        (_, DeviceCertificateService secondBoot) = CreateServices();
        DeviceCertificate second = await secondBoot.GetOrCreateAsync();

        byte[] fileAfterSecondBoot = await File.ReadAllBytesAsync(_root.Paths.SecretsFilePath);

        // 指纹必须一致，且 secrets.bin 必须 byte-for-byte 未被重写。
        Assert.Equal(first.Sha256FingerprintHex, second.Sha256FingerprintHex);
        Assert.Equal(fileAfterCreation, fileAfterSecondBoot);
    }

    [Fact]
    public async Task CertificateState_PartialStateDoesNotSilentlyReissue()
    {
        (DpapiSecretVault vault, DeviceCertificateService first) = CreateServices();

        // 1) 首次生成证书，记录指纹 A。
        string fingerprintA = (await first.GetOrCreateAsync()).Sha256FingerprintHex;

        // 2) 只读取出「原来的」口令（绝不写日志）。
        string originalPassword = await vault.ReadAsync(bundle => bundle.CertificatePfxPassword ?? string.Empty);
        Assert.False(string.IsNullOrEmpty(originalPassword));

        // 3) 把口令置空，制造 partial state。
        await vault.UpdateAsync(
            bundle =>
            {
                bundle.CertificatePfxPassword = null;
                return true;
            });

        // 4) 新服务加载必须显式失败，而不是悄悄重签一张。
        (_, DeviceCertificateService broken) = CreateServices();
        await Assert.ThrowsAsync<InvalidDataException>(() => broken.GetOrCreateAsync());

        // 5) 把「原来的」口令恢复回去。
        await vault.UpdateAsync(
            bundle =>
            {
                bundle.CertificatePfxPassword = originalPassword;
                return true;
            });

        // 6) 新服务再加载，必须还是同一张证书 —— 这才是「没有被悄悄替换」的真正证明。
        (_, DeviceCertificateService recovered) = CreateServices();
        DeviceCertificate afterRecovery = await recovered.GetOrCreateAsync();

        Assert.Equal(fingerprintA, afterRecovery.Sha256FingerprintHex);

        // 口令属于秘密，不得出现在日志里。
        Assert.False(_logs.Contains(originalPassword));
    }
}
