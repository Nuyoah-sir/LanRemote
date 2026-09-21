using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LanRemote.Transport;

/// <summary>
/// 证书指纹（SHA-256 over 证书 DER）的解码、计算与定长时间比较。
/// </summary>
/// <remarks>
/// <para>规格依据 <c>04_PROTOCOL_AND_SECURITY.md</c> 第 7 节：指纹 = SHA256(cert.RawData)，
/// 展示为 64 个大写十六进制字符；比较必须用
/// <see cref="CryptographicOperations.FixedTimeEquals"/>，不允许无条件放过。</para>
/// <para><b>安全约束</b>（外部红队评审 A 桶第 3 条）：
/// 安全判定必须是<b>字节对字节</b>，不能比较格式化后的 hex 字符串；
/// 期望指纹必须在建立 TCP 连接<b>之前</b>解码成 32 字节并冻结，
/// 畸形指纹（长度不对 / 非十六进制）在连接之前就要失败。</para>
/// <para><b>.NET 10 事实</b>：<c>RemoteCertificateValidationCallback</c> 的证书参数是
/// <see cref="X509Certificate"/>（基类），<b>没有</b> <c>RawData</c> 属性，只有
/// <c>GetRawCertData()</c>。所以 <see cref="Compute"/> 接受基类而不是
/// <see cref="X509Certificate2"/>——这样回调里传进来的对象可以直接使用。</para>
/// </remarks>
public static class CertificatePin
{
    /// <summary>指纹字节长度（SHA-256 = 32 字节）。</summary>
    public const int LengthBytes = 32;

    /// <summary>十六进制表示的长度（64 个字符）。</summary>
    public const int HexLength = LengthBytes * 2;

    /// <summary>
    /// 把十六进制指纹解码成恰好 32 字节。
    /// </summary>
    /// <param name="hex">64 个十六进制字符，大小写均可。</param>
    /// <param name="pin">成功时为 32 字节；<b>失败时恒为 <see cref="Array.Empty{T}"/></b>，
    /// 不会返回部分解码结果（与 <c>SecretEncoding.TryDecodeExact</c> 的约定一致）。</param>
    /// <returns>解码成功且长度恰好 32 字节则为 <see langword="true"/>。</returns>
    public static bool TryDecode(string? hex, out byte[] pin)
    {
        pin = Array.Empty<byte>();

        if (string.IsNullOrEmpty(hex) || hex.Length != HexLength)
        {
            return false;
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return false;
        }

        // Convert.FromHexString 在长度合法时必然给出 LengthBytes 字节；
        // 这里仍然显式校验一次，避免将来改长度常量时静默错位。
        if (decoded.Length != LengthBytes)
        {
            CryptographicOperations.ZeroMemory(decoded);
            return false;
        }

        pin = decoded;
        return true;
    }

    /// <summary>
    /// 计算证书 DER 的 SHA-256 指纹。
    /// </summary>
    /// <param name="certificate">出示的证书（回调里给的是 <see cref="X509Certificate"/> 基类）。</param>
    /// <returns>32 字节指纹。</returns>
    public static byte[] Compute(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        return SHA256.HashData(certificate.GetRawCertData());
    }

    /// <summary>
    /// 定长时间比较两个指纹。
    /// </summary>
    /// <param name="expected">连接前冻结的期望指纹。</param>
    /// <param name="presented">TLS 握手时实际出示证书算出的指纹。</param>
    /// <returns>两者都是 32 字节且内容相同则为 <see langword="true"/>；否则一律 <see langword="false"/>。</returns>
    /// <remarks>
    /// 长度不符时直接返回 <see langword="false"/>，不抛异常——校验回调里抛异常只会让错误信息更难读。
    /// <see cref="CryptographicOperations.FixedTimeEquals(byte[], byte[])"/> 本身对长度不同返回 false，
    /// 这里先做长度检查是为了表达「必须恰好 32 字节」这个契约。
    /// </remarks>
    public static bool Matches(byte[]? expected, byte[]? presented)
    {
        if (expected is null || presented is null)
        {
            return false;
        }

        return Matches(expected.AsSpan(), presented.AsSpan());
    }

    /// <summary>
    /// 定长时间比较两个指纹（不分配临时数组）。
    /// </summary>
    /// <param name="expected">连接前冻结的期望指纹。</param>
    /// <param name="presented">TLS 握手时实际出示证书算出的指纹。</param>
    /// <returns>两者都是 32 字节且内容相同则为 <see langword="true"/>；否则一律 <see langword="false"/>。</returns>
    public static bool Matches(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> presented)
    {
        if (expected.Length != LengthBytes || presented.Length != LengthBytes)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expected, presented);
    }
}
