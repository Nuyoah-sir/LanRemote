using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LanRemote.Transport.Tests;

/// <summary>
/// 证书外观选项：用来造出「形状不对」的证书，验证不变量检查真的会拒绝。
/// </summary>
internal sealed class TestCertificateOptions
{
    /// <summary>用 ECDSA P-256；为 <see langword="false"/> 时用 RSA 2048。</summary>
    public bool UseEcdsa { get; init; } = true;

    /// <summary>是否加 Basic Constraints 扩展。</summary>
    public bool IncludeBasicConstraints { get; init; } = true;

    /// <summary>Basic Constraints 里的 CA 标记。</summary>
    public bool IsCertificateAuthority { get; init; }

    /// <summary>Key Usage；为 <see langword="null"/> 时不加该扩展。</summary>
    public X509KeyUsageFlags? KeyUsage { get; init; } = X509KeyUsageFlags.DigitalSignature;

    /// <summary>Extended Key Usage；为 <see langword="null"/> 时不加该扩展。</summary>
    public string[]? EnhancedKeyUsages { get; init; } = new[] { PeerCertificateValidator.ServerAuthEkuOid };

    /// <summary>生效时间。</summary>
    public DateTimeOffset NotBefore { get; init; } = DateTimeOffset.UtcNow.AddMinutes(-5);

    /// <summary>失效时间。</summary>
    public DateTimeOffset NotAfter { get; init; } = DateTimeOffset.UtcNow.AddYears(1);
}

/// <summary>
/// 测试用证书工厂。
/// </summary>
/// <remarks>
/// <para>刻意不走 DPAPI / <c>secrets.bin</c>：本文件要的是「可控形状的证书」，
/// 不是「本机设备身份」。但<b>私钥的落地方式必须和生产一致</b>，见 <see cref="Create"/>。</para>
/// </remarks>
internal static class TestCertificateFactory
{
    /// <summary>造一张证书。</summary>
    /// <param name="options">外观选项。</param>
    /// <returns>带私钥的证书；调用方负责释放。</returns>
    /// <remarks>
    /// <para><b>必须走 PFX 往返，不能直接返回 <c>CreateSelfSigned</c> 的结果</b>。
    /// 2026-09-21 本机实测（<c>TestTlsServer</c> + 真实 <c>SslStream</c>）：
    /// <c>CertificateRequest.CreateSelfSigned</c> 直接产出的证书，其私钥是 <b>ephemeral</b> 的，
    /// 用作 TLS 服务端时在 <c>AcquireCredentialsHandle</c> 处失败：
    /// <c>AuthenticationException: Authentication failed because the platform does not support ephemeral keys.</c>
    /// （inner <c>Win32Exception 0x8009030E</c>）——与 ADR-029 记录的 <c>EphemeralKeySet</c> 失败<b>同一个错</b>。</para>
    /// <para>这反过来把 ADR-029 的结论收紧了一层：Schannel 拒绝的是<b>密钥本身的 ephemeral 属性</b>，
    /// 不只是「导入 flag 叫 EphemeralKeySet」。生产代码（<c>DeviceCertificateService</c>）
    /// 之所以可用，正是因为它签完立刻导出 PFX、再用 <c>DefaultKeySet</c> 导入。</para>
    /// <para>所以本方法刻意复刻同一条路径：导出随机口令 PFX →
    /// <c>X509CertificateLoader.LoadPkcs12(..., DefaultKeySet)</c>。
    /// 附带好处是测试证书的密钥形态与真实设备证书完全一致。</para>
    /// </remarks>
    public static X509Certificate2 Create(TestCertificateOptions? options = null)
    {
        TestCertificateOptions effective = options ?? new TestCertificateOptions();

        CertificateRequest request;
        IDisposable? keyToRelease = null;

        try
        {
            if (effective.UseEcdsa)
            {
                ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                keyToRelease = ecdsa;
                request = new CertificateRequest("CN=lanremote-test", ecdsa, HashAlgorithmName.SHA256);
            }
            else
            {
                RSA rsa = RSA.Create(2048);
                keyToRelease = rsa;
                request = new CertificateRequest(
                    "CN=lanremote-test",
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
            }

            if (effective.IncludeBasicConstraints)
            {
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
                    effective.IsCertificateAuthority,
                    hasPathLengthConstraint: false,
                    pathLengthConstraint: 0,
                    critical: true));
            }

            if (effective.KeyUsage is { } keyUsage)
            {
                request.CertificateExtensions.Add(new X509KeyUsageExtension(keyUsage, critical: true));
            }

            if (effective.EnhancedKeyUsages is { } ekus)
            {
                OidCollection oids = new();
                foreach (string oid in ekus)
                {
                    oids.Add(new Oid(oid));
                }

                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(oids, critical: false));
            }

            using X509Certificate2 selfSigned =
                request.CreateSelfSigned(effective.NotBefore, effective.NotAfter);

            return ReimportWithDefaultKeySet(selfSigned);
        }
        finally
        {
            keyToRelease?.Dispose();
        }
    }

    /// <summary>
    /// 复刻生产路径：导出 PFX 后用 <c>DefaultKeySet</c> 重新导入。
    /// </summary>
    private static X509Certificate2 ReimportWithDefaultKeySet(X509Certificate2 selfSigned)
    {
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        byte[] pfx = selfSigned.Export(X509ContentType.Pfx, password);
        try
        {
            // 与 DeviceCertificateService.ImportFlags 保持一致（ADR-029）。
            // 这里不直接引用该常量是因为 LanRemote.Security 的 TFM 是 net10.0-windows，
            // 本测试工程是 net10.0，无法跨 TFM 引用。
            const X509KeyStorageFlags importFlags = X509KeyStorageFlags.DefaultKeySet;

            X509Certificate2 loaded = X509CertificateLoader.LoadPkcs12(pfx, password, importFlags);

            if (!loaded.HasPrivateKey)
            {
                loaded.Dispose();
                throw new InvalidOperationException("PFX 往返后重新导入的证书缺少私钥。");
            }

            return loaded;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    /// <summary>
    /// 直接返回 <c>CreateSelfSigned</c> 的结果，<b>不做 PFX 往返</b>。
    /// </summary>
    /// <returns>带 ephemeral 私钥的证书；<b>不能</b>用作 TLS 服务端。</returns>
    /// <remarks>
    /// 只给守护测试用（<c>SelfSignedKeyEphemeralTests</c>），它记录「为什么 <see cref="Create"/> 必须往返一次」。
    /// 生产与测试都不该拿它去做服务端凭据。
    /// </remarks>
    public static X509Certificate2 CreateWithoutPfxRoundtrip()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        CertificateRequest request =
            new("CN=lanremote-ephemeral", key, HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid(PeerCertificateValidator.ServerAuthEkuOid) },
                critical: false));

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(1));
    }

    /// <summary>算证书指纹（64 个大写十六进制字符），与 <c>DataProtection.ComputeCertificateFingerprint</c> 同口径。</summary>
    public static string Fingerprint(X509Certificate2 certificate)
    {
        return Convert.ToHexString(SHA256.HashData(certificate.RawData));
    }

    /// <summary>把指纹字节还原成 hex，便于断言。</summary>
    public static string ToHex(ReadOnlySpan<byte> pin)
    {
        return Convert.ToHexString(pin);
    }
}
