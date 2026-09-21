using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LanRemote.Transport;

/// <summary>
/// 客户端对<b>服务端</b>证书的校验：精确 pinning + 本地不变量。
/// </summary>
/// <remarks>
/// <para><b>pinning 是安全边界，其余都是不变量检查</b>（外部红队评审 A 桶第 1、3 条）。
/// 自签名证书在 Windows 上必然触发 <c>SslPolicyErrors.RemoteCertificateChainErrors</c>，
/// 所以这里<b>刻意不要求</b> <c>sslPolicyErrors == SslPolicyErrors.None</c>——
/// 本项目不使用 CA / 不使用系统信任库（ADR-027），信任只来自用户带外拿到的访问密钥（M4），
/// pinning 在这里的作用是把「这一条连接确实连到了发现记录里那台设备」固定下来。</para>
/// <para>不变量检查（ECDSA P-256 / 非 CA / KU 含 digitalSignature / EKU 含 serverAuth /
/// 有效期）不是独立的第二道防线，而是防止「我们自己签发流程出错」被静默接受。
/// 它们失败时同样一律拒绝。</para>
/// <para><b>时间必须可注入</b>：有效期判定用 <see cref="TimeProvider"/>，
/// 测试里用假时钟制造过期证书，绝不允许去改系统时钟。</para>
/// </remarks>
public static class PeerCertificateValidator
{
    /// <summary>serverAuth 的 EKU OID（1.3.6.1.5.5.7.3.1）。</summary>
    public const string ServerAuthEkuOid = "1.3.6.1.5.5.7.3.1";

    /// <summary>ECDSA P-256 的密钥长度（位）。</summary>
    public const int RequiredEcKeySizeBits = 256;

    /// <summary>
    /// 校验对端证书。
    /// </summary>
    /// <param name="certificate">TLS 回调给出的证书（可能是 <see langword="null"/>）。</param>
    /// <param name="target">连接前冻结的目标快照，提供期望指纹。</param>
    /// <param name="clock">时间源；有效期判定使用它，便于测试注入。</param>
    /// <param name="presentedPin">实际出示证书的指纹。
    /// <b>无论成功失败都会尽力返回</b>（失败且拿不到时为 <see cref="Array.Empty{T}"/>），
    /// 便于本地日志留证。</param>
    /// <param name="rejection">拒绝原因短码；成功时为 <see langword="null"/>。
    /// <b>只用于本地日志，不要回传给对端</b>——把拒绝原因告诉未认证的对端等于免费给出指纹探测 oracle。</param>
    /// <returns>通过则为 <see langword="true"/>。</returns>
    public static bool TryValidate(
        X509Certificate? certificate,
        ConnectionTarget target,
        TimeProvider clock,
        out byte[] presentedPin,
        out string? rejection)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(clock);

        presentedPin = Array.Empty<byte>();
        rejection = null;

        if (certificate is null)
        {
            rejection = RejectionNoCertificate;
            return false;
        }

        // 第一道、也是唯一的安全边界：字节对字节的 pin 比较。
        // 先做它是因为后面的不变量检查成本更高，且证书都不对时它们没有意义。
        byte[] presented = CertificatePin.Compute(certificate);
        presentedPin = presented;

        if (!CertificatePin.Matches(target.ExpectedCertSha256.Span, presented))
        {
            rejection = RejectionPinMismatch;
            return false;
        }

        // pin 已经证明「这是发现记录里那张证书」，下面检查它是不是一张我们认可形状的证书。
        using X509Certificate2 detailed = new(certificate);

        if (!HasEcdsaP256Key(detailed))
        {
            rejection = RejectionNotEcdsaP256;
            return false;
        }

        if (IsCertificateAuthority(detailed))
        {
            rejection = RejectionIsCertificateAuthority;
            return false;
        }

        X509KeyUsageExtension? keyUsage = FindExtension<X509KeyUsageExtension>(detailed);
        if (keyUsage is null)
        {
            rejection = RejectionMissingKeyUsage;
            return false;
        }

        if (!keyUsage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature))
        {
            rejection = RejectionKeyUsageWithoutDigitalSignature;
            return false;
        }

        X509EnhancedKeyUsageExtension? enhancedKeyUsage = FindExtension<X509EnhancedKeyUsageExtension>(detailed);
        if (enhancedKeyUsage is null)
        {
            rejection = RejectionMissingEnhancedKeyUsage;
            return false;
        }

        bool hasServerAuth = false;
        foreach (Oid oid in enhancedKeyUsage.EnhancedKeyUsages)
        {
            if (oid.Value == ServerAuthEkuOid)
            {
                hasServerAuth = true;
                break;
            }
        }

        if (!hasServerAuth)
        {
            rejection = RejectionEnhancedKeyUsageWithoutServerAuth;
            return false;
        }

        DateTimeOffset now = clock.GetUtcNow();
        DateTimeOffset notBefore = new(detailed.NotBefore.ToUniversalTime());
        DateTimeOffset notAfter = new(detailed.NotAfter.ToUniversalTime());

        if (now < notBefore)
        {
            rejection = RejectionNotYetValid;
            return false;
        }

        if (now > notAfter)
        {
            rejection = RejectionExpired;
            return false;
        }

        return true;
    }

    /// <summary>对端没有出示证书。</summary>
    public const string RejectionNoCertificate = "no-certificate";

    /// <summary>指纹不匹配。</summary>
    public const string RejectionPinMismatch = "pin-mismatch";

    /// <summary>密钥不是 ECDSA P-256。</summary>
    public const string RejectionNotEcdsaP256 = "not-ecdsa-p256";

    /// <summary>这是一张 CA 证书。</summary>
    public const string RejectionIsCertificateAuthority = "is-ca";

    /// <summary>缺少 Key Usage 扩展。</summary>
    public const string RejectionMissingKeyUsage = "missing-key-usage";

    /// <summary>Key Usage 不含 digitalSignature。</summary>
    public const string RejectionKeyUsageWithoutDigitalSignature = "ku-without-digital-signature";

    /// <summary>缺少 Extended Key Usage 扩展。</summary>
    public const string RejectionMissingEnhancedKeyUsage = "missing-eku";

    /// <summary>Extended Key Usage 不含 serverAuth。</summary>
    public const string RejectionEnhancedKeyUsageWithoutServerAuth = "eku-without-server-auth";

    /// <summary>证书尚未生效。</summary>
    public const string RejectionNotYetValid = "not-yet-valid";

    /// <summary>证书已过期。</summary>
    public const string RejectionExpired = "expired";

    private static bool HasEcdsaP256Key(X509Certificate2 certificate)
    {
        using ECDsa? publicKey = certificate.PublicKey.GetECDsaPublicKey();
        if (publicKey is null)
        {
            return false;
        }

        return publicKey.KeySize == RequiredEcKeySizeBits;
    }

    /// <remarks>
    /// 缺少 Basic Constraints 扩展时按 RFC 5280 视为<b>非 CA</b>（end-entity 证书可以不带）。
    /// 我们的设备证书显式带了 <c>CA=false</c>，这里容忍缺失是为了不把「没写扩展」
    /// 和「写了 CA=true」混为一谈——真正要拦的是后者。
    /// </remarks>
    private static bool IsCertificateAuthority(X509Certificate2 certificate)
    {
        X509BasicConstraintsExtension? basicConstraints =
            FindExtension<X509BasicConstraintsExtension>(certificate);

        return basicConstraints is not null && basicConstraints.CertificateAuthority;
    }

    private static T? FindExtension<T>(X509Certificate2 certificate)
        where T : X509Extension
    {
        foreach (X509Extension extension in certificate.Extensions)
        {
            if (extension is T typed)
            {
                return typed;
            }
        }

        return null;
    }
}
