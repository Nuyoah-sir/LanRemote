using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace LanRemote.Transport.Tests;

/// <summary>
/// 证书指纹的解码 / 计算 / 定长比较。
/// </summary>
public sealed class CertificatePinTests
{
    private static X509Certificate2 CreateCertificate()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=LanRemote-Test", key, HashAlgorithmName.SHA256);

        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        return request.CreateSelfSigned(notBefore, notBefore.AddDays(1));
    }

    [Fact]
    public void TryDecode_AcceptsUppercaseHex()
    {
        string hex = new('A', CertificatePin.HexLength);

        Assert.True(CertificatePin.TryDecode(hex, out byte[] pin));
        Assert.Equal(32, pin.Length);
        Assert.All(pin, b => Assert.Equal(0xAA, b));
    }

    [Fact]
    public void TryDecode_AcceptsLowercaseHex()
    {
        string hex = "3f4fd8c5ce4c062fdca7ca1492bb9935ab11548f85b7a682a3a96992cc090d1f";

        Assert.True(CertificatePin.TryDecode(hex, out byte[] pin));
        Assert.Equal(32, pin.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("3F4F")]                                        // 太短
    [InlineData("3f4fd8c5ce4c062fdca7ca1492bb9935ab11548f85b7a682a3a96992cc090d1")]  // 63
    [InlineData("3f4fd8c5ce4c062fdca7ca1492bb9935ab11548f85b7a682a3a96992cc090d1fF")] // 65
    [InlineData("zz4fd8c5ce4c062fdca7ca1492bb9935ab11548f85b7a682a3a96992cc090d1f")] // 非十六进制
    [InlineData("3f-4d8c5ce4c062fdca7ca1492bb9935ab11548f85b7a682a3a96992cc090d1f")] // 带分隔符
    public void TryDecode_RejectsMalformed(string? hex)
    {
        // 失败时不能返回部分解码结果——失败路径的 out 必须是空数组。
        Assert.False(CertificatePin.TryDecode(hex, out byte[] pin));
        Assert.Empty(pin);
    }

    [Fact]
    public void Compute_EqualsSha256OfRawCertificateData()
    {
        using X509Certificate2 certificate = CreateCertificate();

        byte[] pin = CertificatePin.Compute(certificate);
        byte[] expected = SHA256.HashData(certificate.RawData);

        Assert.Equal(expected, pin);
        Assert.Equal(32, pin.Length);
    }

    [Fact]
    public void Compute_WorksOnBaseX509Certificate()
    {
        using X509Certificate2 certificate = CreateCertificate();

        // 回调里给的是 X509Certificate 基类（没有 RawData），Compute 必须能直接吃基类。
        X509Certificate baseCertificate = certificate;

        Assert.Equal(SHA256.HashData(certificate.RawData), CertificatePin.Compute(baseCertificate));
    }

    [Fact]
    public void Compute_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => CertificatePin.Compute(null!));
    }

    [Fact]
    public void Matches_ReturnsTrueForEqualPins()
    {
        byte[] pin = new byte[32];
        RandomNumberGenerator.Fill(pin);

        Assert.True(CertificatePin.Matches(pin, pin.ToArray()));
    }

    [Fact]
    public void Matches_ReturnsFalseForDifferentPins()
    {
        byte[] a = new byte[32];
        byte[] b = new byte[32];
        RandomNumberGenerator.Fill(a);
        RandomNumberGenerator.Fill(b);

        Assert.False(CertificatePin.Matches(a, b));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(0)]
    public void Matches_ReturnsFalseForWrongLength(int length)
    {
        byte[] pin = new byte[32];
        RandomNumberGenerator.Fill(pin);

        Assert.False(CertificatePin.Matches(pin, new byte[length]));
        Assert.False(CertificatePin.Matches(new byte[length], pin));
    }

    [Fact]
    public void Matches_ReturnsFalseForNull()
    {
        byte[] pin = new byte[32];

        Assert.False(CertificatePin.Matches(null, pin));
        Assert.False(CertificatePin.Matches(pin, null));
        Assert.False(CertificatePin.Matches(null, null));
    }
}
