using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LanRemote.Core.Identity;
using LanRemote.Security.Secrets;
using Microsoft.Extensions.Logging;

namespace LanRemote.Security.Certificates;

/// <summary>
/// 本设备 TLS 身份（自签名 ECDSA P-256 证书 + 私钥 + 指纹）。
/// </summary>
/// <param name="Certificate">带私钥的证书。</param>
/// <param name="Sha256FingerprintHex">证书 DER 的 SHA-256 指纹，64 个大写十六进制字符。</param>
public sealed record DeviceCertificate(X509Certificate2 Certificate, string Sha256FingerprintHex);

/// <summary>
/// 设备证书的创建、持久化与加载。
/// </summary>
/// <remarks>
/// <para>规格依据（04_PROTOCOL_AND_SECURITY.md 第 7 节）：</para>
/// <list type="bullet">
/// <item><description>ECDSA P-256、自签名、有效期 5 年；</description></item>
/// <item><description>Subject 只含本机软件标识（设备码），不含机器名、用户名等敏感信息；</description></item>
/// <item><description>私钥由 DPAPI 保护后存放在 <c>secrets.bin</c>，禁止写入 <c>config.json</c>；</description></item>
/// <item><description>指纹来自证书 DER，重新加载后保持不变——这是 M3 证书 pinning 的基础。</description></item>
/// </list>
/// <para>导出 PFX 时使用随机口令；口令与 PFX 一起被封在同一个 DPAPI 信封中，
/// 它不提供额外机密性，只是 PFX 导出 API 的强制要求。</para>
/// <para><b>私钥导入策略（M1.1，ADR-018）</b>：使用 <see cref="X509KeyStorageFlags.EphemeralKeySet"/>。
/// 磁盘上唯一的持久化副本是 DPAPI 保护的 <c>secrets.bin</c>；进程运行期间私钥只存在于内存，
/// 不额外写入 Windows CNG 密钥容器，也不标记为可导出。</para>
/// </remarks>
public sealed class DeviceCertificateService : IDisposable
{
    /// <summary>证书有效期。</summary>
    public const int ValidityYears = 5;

    /// <summary>
    /// 私钥只在内存中存活。
    /// </summary>
    /// <remarks>
    /// 刻意<b>不</b>使用 <c>PersistKeySet</c>（会把私钥写进用户的 CNG 密钥容器，
    /// 等于在 DPAPI 文件之外多留一份持久化副本），也<b>不</b>使用 <c>Exportable</c>。
    /// M3 必须补一条真实 SslStream 握手集成测试来验证这个策略满足 TLS 服务端要求；
    /// 在拿到那个结果之前，不得凭猜测改回 PersistKeySet（ADR-016 已被标记 superseded）。
    /// </remarks>
    public const X509KeyStorageFlags ImportFlags = X509KeyStorageFlags.EphemeralKeySet;

    private readonly DpapiSecretVault _vault;
    private readonly ILogger<DeviceCertificateService>? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private DeviceCertificate? _cached;
    private bool _disposed;

    /// <summary>构造服务。</summary>
    /// <param name="vault">秘密仓库。</param>
    /// <param name="logger">日志器，可为空。</param>
    public DeviceCertificateService(DpapiSecretVault vault, ILogger<DeviceCertificateService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(vault);
        _vault = vault;
        _logger = logger;
    }

    /// <summary>
    /// 取得设备证书；只有「证书确实不存在」时才会创建并写盘。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>带私钥的证书与指纹。</returns>
    /// <remarks>
    /// <para><b>M1.2 修复</b>：旧实现无条件走 <see cref="DpapiSecretVault.UpdateAsync{TResult}"/>，
    /// 即便证书早已存在，也会再做一次 DPAPI 加密与文件写入。这意味着「只是想读一下身份」
    /// 也会触发一次写操作——在只读/受限环境下会无谓失败，也平白多一次密钥材料的搬运。</para>
    /// <para>现在分两阶段：</para>
    /// <list type="number">
    /// <item><description><b>只读</b>判断 bundle 中证书字段的状态；</description></item>
    /// <item><description>只有在「两个字段都为空」时才进入写路径，且写路径内部<b>再次</b>检查状态以防竞态。</description></item>
    /// </list>
    /// </remarks>
    public async Task<DeviceCertificate> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null)
            {
                return _cached;
            }

            // 第一阶段：只读，不写文件。
            CertificateSnapshot snapshot = await _vault.ReadAsync(SnapshotCertificate, cancellationToken)
                .ConfigureAwait(false);

            switch (snapshot.State)
            {
                case CertificateBundleState.Present:
                    _cached = snapshot.Certificate
                        ?? throw new InvalidOperationException("证书状态判定与加载结果不一致。");

                    _logger?.LogDebug(
                        "已从 secrets.bin 加载设备证书（未重写文件）。指纹前缀={FingerprintPrefix}",
                        _cached.Sha256FingerprintHex[..8]);
                    return _cached;

                case CertificateBundleState.Corrupt:
                    throw new InvalidDataException(CorruptStateMessage);

                case CertificateBundleState.Absent:
                default:
                    break;
            }

            // 第二阶段：确认不存在，才真正创建并写盘。
            _cached = await _vault.UpdateAsync(CreateOrLoadCore, cancellationToken).ConfigureAwait(false);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>释放持有的证书。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cached?.Certificate.Dispose();
        _cached = null;
        _gate.Dispose();
    }

    private const string CorruptStateMessage =
        "secrets.bin 中的设备证书状态损坏：certificatePfx 与 certificatePfxPassword 必须同时存在或同时缺失。" +
        "已拒绝自动生成新证书，以免静默改变证书指纹。";

    /// <summary>
    /// 只读地取出 bundle 里的证书状态；证书存在时顺带载入（不写盘）。
    /// </summary>
    /// <param name="bundle">bundle 副本。</param>
    /// <returns>状态快照。</returns>
    private static CertificateSnapshot SnapshotCertificate(SecretBundle bundle)
    {
        bool hasPfx = !string.IsNullOrEmpty(bundle.CertificatePfx);
        bool hasPassword = !string.IsNullOrEmpty(bundle.CertificatePfxPassword);

        if (hasPfx && hasPassword)
        {
            X509Certificate2 certificate = ImportFromPfx(bundle.CertificatePfx!, bundle.CertificatePfxPassword!);
            return new CertificateSnapshot(
                CertificateBundleState.Present,
                new DeviceCertificate(certificate, DataProtection.ComputeCertificateFingerprint(certificate)));
        }

        // 恰好只有一个字段存在 = 状态损坏。
        return new CertificateSnapshot(
            hasPfx || hasPassword
                ? CertificateBundleState.Corrupt
                : CertificateBundleState.Absent,
            null);
    }

    /// <summary>
    /// 写路径：只在确认证书确实不存在时才签发；否则走「已经存在」分支。
    /// </summary>
    /// <param name="bundle">working copy。</param>
    /// <returns>证书与指纹。</returns>
    /// <remarks>
    /// 这里的重复检查不是冗余：只读阶段与写阶段之间隔着一次锁的进出，
    /// 理论上可能有另一个调用方抢先创建了证书。这里必须同样 fail closed。
    /// </remarks>
    private DeviceCertificate CreateOrLoadCore(SecretBundle bundle)
    {
        bool hasPfx = !string.IsNullOrEmpty(bundle.CertificatePfx);
        bool hasPassword = !string.IsNullOrEmpty(bundle.CertificatePfxPassword);

        if (hasPfx && hasPassword)
        {
            // 竞态兜底：别人已经写好了，直接用那一份，绝不覆盖。
            X509Certificate2 existing = ImportFromPfx(bundle.CertificatePfx!, bundle.CertificatePfxPassword!);
            return new DeviceCertificate(
                existing,
                DataProtection.ComputeCertificateFingerprint(existing));
        }

        if (hasPfx || hasPassword)
        {
            throw new InvalidDataException(CorruptStateMessage);
        }

        return CreateAndStoreCore(bundle);
    }

    private DeviceCertificate CreateAndStoreCore(SecretBundle bundle)
    {
        string deviceCode = DeviceCode.DeriveRaw(bundle.DeviceGuid);
        string password = SecretGenerator.NewPfxPassword();

        using X509Certificate2 freshlyCreated = CreateSelfSignedCertificate(deviceCode);

        // PFX 里含私钥，转成 Base64 之后立即清零中间缓冲。
        byte[] pfx = freshlyCreated.Export(X509ContentType.Pfx, password);
        try
        {
            bundle.CertificatePfx = Convert.ToBase64String(pfx);
            bundle.CertificatePfxPassword = password;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }

        // 立即从 PFX 重新导入，确保「首次创建」与「后续加载」走完全相同的路径，
        // 也顺便当场校验我们写进去的 PFX 是可用的。
        X509Certificate2 certificate = ImportFromPfx(bundle.CertificatePfx, bundle.CertificatePfxPassword!);
        string fingerprint = DataProtection.ComputeCertificateFingerprint(certificate);

        _logger?.LogInformation(
            "已生成自签名 ECDSA P-256 设备证书，有效期={ValidityYears} 年。指纹前缀={FingerprintPrefix}",
            ValidityYears,
            fingerprint[..8]);

        return new DeviceCertificate(certificate, fingerprint);
    }

    private static X509Certificate2 CreateSelfSignedCertificate(string deviceCode)
    {
        using ECDsa privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        CertificateRequest request = new($"CN=LanRemote-{deviceCode}", privateKey, HashAlgorithmName.SHA256);

        // 不是 CA，只做服务器身份认证。
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));

        // M1.3：Key Usage 只允许 digitalSignature。
        //
        // RFC 5480 对 id-ecPublicKey 的 end-entity 证书允许的 Key Usage 是
        // digitalSignature / nonRepudiation / keyAgreement；
        // keyEncipherment 属于 RSA 密钥传输语义，不属于 EC certificate profile——
        // 之前错误地把它写进 ECDSA 证书，属于 profile 违规（严格校验方可能直接拒绝）。
        //
        // LanRemote 的 TLS 服务端身份使用 ECDSA 签名，因此只需要 digitalSignature；
        // 刻意不使用 keyAgreement（那会要求 ECDH 语义，本项目不做）。
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));

        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                critical: false));

        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        DateTimeOffset notAfter = notBefore.AddYears(ValidityYears);

        return request.CreateSelfSigned(notBefore, notAfter);
    }

    private static X509Certificate2 ImportFromPfx(string base64Pfx, string password)
    {
        // 使用 .NET 9+ 的 X509CertificateLoader：旧的 X509Certificate2(byte[], ...) 构造已标记过时。
        byte[] pfx = Convert.FromBase64String(base64Pfx);
        try
        {
            X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12(pfx, password, ImportFlags);

            if (!certificate.HasPrivateKey)
            {
                certificate.Dispose();
                throw new InvalidDataException("从 secrets.bin 还原的证书缺少私钥。");
            }

            return certificate;
        }
        finally
        {
            // PFX 字节里含私钥，成功或失败都要清零。
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    /// <summary>
    /// bundle 中证书字段的三种状态。
    /// </summary>
    private enum CertificateBundleState
    {
        /// <summary>两个字段都为空：尚未签发过证书。</summary>
        Absent = 0,

        /// <summary>两个字段都有：可以加载。</summary>
        Present = 1,

        /// <summary>恰好只有一个：状态损坏，必须 fail closed。</summary>
        Corrupt = 2,
    }

    /// <summary>
    /// 只读阶段的状态快照。
    /// </summary>
    /// <param name="State">状态。</param>
    /// <param name="Certificate">状态为 <see cref="CertificateBundleState.Present"/> 时的证书，否则为 <see langword="null"/>。</param>
    private readonly record struct CertificateSnapshot(
        CertificateBundleState State,
        DeviceCertificate? Certificate);
}
