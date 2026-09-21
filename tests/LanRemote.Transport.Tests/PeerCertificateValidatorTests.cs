using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace LanRemote.Transport.Tests;

/// <summary>
/// <see cref="PeerCertificateValidator"/> 的行为。
/// </summary>
/// <remarks>
/// <para>覆盖 M3 步骤 13：pinning 之外的证书形状不变量。
/// 这些不是独立的第二道防线，而是防止「我们自己的签发流程出错」被静默接受。</para>
/// <para>有效期用 <see cref="FakeClock"/> 注入，不改系统时钟。</para>
/// </remarks>
public sealed class PeerCertificateValidatorTests
{
    private static readonly Guid DeviceId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Accepts_Certificate_With_Matching_Pin_And_Valid_Shape()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        string pin = TestCertificateFactory.Fingerprint(certificate);

        (bool ok, byte[] presented, string? rejection) = Validate(certificate, pin, new FakeClock(DateTimeOffset.UtcNow));

        Assert.True(ok, rejection);
        Assert.Null(rejection);
        Assert.Equal(pin, TestCertificateFactory.ToHex(presented));
    }

    [Fact]
    public void Rejects_Null_Certificate()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        ConnectionTarget target = CreateTarget(TestCertificateFactory.Fingerprint(certificate));

        bool ok = PeerCertificateValidator.TryValidate(
            certificate: null,
            target: target,
            clock: TimeProvider.System,
            presentedPin: out byte[] presented,
            rejection: out string? rejection);

        Assert.False(ok);
        Assert.Equal(PeerCertificateValidator.RejectionNoCertificate, rejection);
        Assert.Empty(presented);
    }

    [Fact]
    public void Rejects_Pin_Mismatch()
    {
        using X509Certificate2 presented = TestCertificateFactory.Create();
        using X509Certificate2 expected = TestCertificateFactory.Create();

        (bool ok, byte[] pin, string? rejection) =
            Validate(presented, TestCertificateFactory.Fingerprint(expected), new FakeClock(DateTimeOffset.UtcNow));

        Assert.False(ok);
        Assert.Equal(PeerCertificateValidator.RejectionPinMismatch, rejection);

        // 即便拒绝，也要把实际指纹交出来给日志（ADR-028 的 presentedPin 留证）。
        Assert.Equal(TestCertificateFactory.Fingerprint(presented), TestCertificateFactory.ToHex(pin));
    }

    [Fact]
    public void Rejects_Rsa_Key()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create(
            new TestCertificateOptions { UseEcdsa = false });

        (bool ok, _, string? rejection) =
            Validate(certificate, TestCertificateFactory.Fingerprint(certificate), new FakeClock(DateTimeOffset.UtcNow));

        Assert.False(ok);
        Assert.Equal(PeerCertificateValidator.RejectionNotEcdsaP256, rejection);
    }

    [Fact]
    public void Rejects_Ca_Certificate()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create(
            new TestCertificateOptions { IsCertificateAuthority = true });

        (bool ok, _, string? rejection) =
            Validate(certificate, TestCertificateFactory.Fingerprint(certificate), new FakeClock(DateTimeOffset.UtcNow));

        Assert.False(ok);
        Assert.Equal(PeerCertificateValidator.RejectionIsCertificateAuthority, rejection);
    }

    [Fact]
    public void Rejects_Missing_Key_Usage_Extension()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create(
            new TestCertificateOptions { KeyUsage = null });

        (bool ok, _, string? rejection) =
            Validate(certificate, TestCertificateFactory.Fingerprint(certificate), new FakeClock(DateTimeOffset.UtcNow));

        Assert.False(ok);
        Assert.Equal(PeerCertificateValidator.RejectionMissingKeyUsage, rejection);
    }

    [Fact]
    public void Rejects_Key_Usage_Without_Digital_Signature()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create(
            new TestCertificateOptions { KeyUsage = X509KeyUsageFlags.KeyEncipherment });

        (bool ok, _, string? rejection) =
            Validate(certificate, TestCertificateFactory.Fingerprint(certificate), new FakeClock(DateTimeOffset.UtcNow));

        Assert.False(ok);
        Assert.Equal(PeerCertificateValidator.RejectionKeyUsageWithoutDigitalSignature, rejection);
    }

    [Fact]
    public void Rejects_Missing_Enhanced_Key_Usage_Extension()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create(
            new TestCertificateOptions { EnhancedKeyUsages = null });

        (bool ok, _, string? rejection) =
            Validate(certificate, TestCertificateFactory.Fingerprint(certificate), new FakeClock(DateTimeOffset.UtcNow));

        Assert.False(ok);
        Assert.Equal(PeerCertificateValidator.RejectionMissingEnhancedKeyUsage, rejection);
    }

    [Fact]
    public void Rejects_Enhanced_Key_Usage_Without_Server_Auth()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create(
            new TestCertificateOptions { EnhancedKeyUsages = new[] { "1.3.6.1.5.5.7.3.2" } });

        (bool ok, _, string? rejection) =
            Validate(certificate, TestCertificateFactory.Fingerprint(certificate), new FakeClock(DateTimeOffset.UtcNow));

        Assert.False(ok);
        Assert.Equal(PeerCertificateValidator.RejectionEnhancedKeyUsageWithoutServerAuth, rejection);
    }

    [Fact]
    public void Rejects_Expired_Certificate_Using_Injected_Clock()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 certificate = TestCertificateFactory.Create(new TestCertificateOptions
        {
            NotBefore = now.AddDays(-30),
            NotAfter = now.AddDays(-1),
        });

        (bool ok, _, string? rejection) =
            Validate(certificate, TestCertificateFactory.Fingerprint(certificate), new FakeClock(now));

        Assert.False(ok);
        Assert.Equal(PeerCertificateValidator.RejectionExpired, rejection);
    }

    [Fact]
    public void Rejects_Not_Yet_Valid_Certificate_Using_Injected_Clock()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 certificate = TestCertificateFactory.Create(new TestCertificateOptions
        {
            NotBefore = now.AddDays(1),
            NotAfter = now.AddDays(30),
        });

        (bool ok, _, string? rejection) =
            Validate(certificate, TestCertificateFactory.Fingerprint(certificate), new FakeClock(now));

        Assert.False(ok);
        Assert.Equal(PeerCertificateValidator.RejectionNotYetValid, rejection);
    }

    [Fact]
    public void Accepts_Certificate_That_Expires_One_Second_Later()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-21T00:00:00Z");
        using X509Certificate2 certificate = TestCertificateFactory.Create(new TestCertificateOptions
        {
            NotBefore = now.AddDays(-1),
            NotAfter = now.AddSeconds(1),
        });

        (bool ok, _, string? rejection) =
            Validate(certificate, TestCertificateFactory.Fingerprint(certificate), new FakeClock(now));

        Assert.True(ok, rejection);
    }

    private static ConnectionTarget CreateTarget(string expectedPinHex)
    {
        bool created = ConnectionTarget.TryCreate(
            DeviceId,
            IPAddress.Loopback,
            TransportConstants.Port,
            expectedPinHex,
            out ConnectionTarget? target);

        Assert.True(created, "测试前置条件：目标快照应能创建。");
        return target!;
    }

    private static (bool Ok, byte[] Presented, string? Rejection) Validate(
        X509Certificate2 certificate,
        string expectedPinHex,
        TimeProvider clock)
    {
        ConnectionTarget target = CreateTarget(expectedPinHex);

        bool ok = PeerCertificateValidator.TryValidate(
            certificate,
            target,
            clock,
            out byte[] presented,
            out string? rejection);

        return (ok, presented, rejection);
    }
}
