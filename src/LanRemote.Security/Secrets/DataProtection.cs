using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace LanRemote.Security.Secrets;

/// <summary>
/// Windows DPAPI 封装。
/// </summary>
/// <remarks>
/// <para>使用 <c>ProtectedData</c> + <see cref="DataProtectionScope.CurrentUser</see>：
/// 密文只对当前 Windows 用户、当前机器可解。换个用户或换台机器都无法还原。</para>
/// <para>这是规格唯一允许的存储加密手段（01_MASTER_PROMPT.md 第 3.4 节密码学白名单）。
/// 禁止用 Base64、XOR、自写 AES 之类的替代方案。</para>
/// </remarks>
public static class DataProtection
{
    /// <summary>保护（加密）一段字节。</summary>
    /// <param name="plaintext">明文。</param>
    /// <returns>密文。</returns>
    public static byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        // optionalEntropy 传空：DPAPI 的 CurrentUser 作用域已绑定到「当前用户 + 当前机器的用户配置」，
        // 换用户或换机器都解不开。刻意不追加自定义 entropy，减少一层可丢失的隐式状态。
        return ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);
    }

    /// <summary>解保护（解密）一段字节。</summary>
    /// <param name="plaintext">明文。</param>
    /// <returns>密文。</returns>
    public static byte[] Unprotect(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);

        return ProtectedData.Unprotect(ciphertext, optionalEntropy: null, DataProtectionScope.CurrentUser);
    }

    /// <summary>
    /// 对缓冲区中的一段范围做 DPAPI 解密，避免多复制一次秘密明文。
    /// </summary>
    /// <param name="buffer">文件缓冲。</param>
    /// <param name="offset">密文起始偏移。</param>
    /// <param name="count">密文字节数。</param>
    /// <returns>明文。</returns>
    public static byte[] Unprotect(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "偏移与长度超出缓冲区范围。");
        }

        if (count == buffer.Length)
        {
            return Unprotect(buffer);
        }

        byte[] slice = new byte[count];
        Array.Copy(buffer, offset, slice, 0, count);

        return Unprotect(slice);
    }

    /// <summary>
    /// 计算 SHA-256 指纹（大写十六进制）。
    /// </summary>
    /// <param name="certificate">证书。</param>
    /// <returns>64 个大写的十六进制字符。</returns>
    /// <remarks>
    /// 指纹对象是证书的 DER 编码（<see cref="X509Certificate2.RawData"/>）。
    /// 从 PFX 重新导入后 RawData 不变，因此指纹在重启后保持稳定。
    /// 比较时必须使用常量时间比较（见 <c>06_DEV_STANDARDS.md</c> 第 5 节）。
    /// </remarks>
    public static string ComputeCertificateFingerprint(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        byte[] hash = SHA256.HashData(certificate.RawData);
        return Convert.ToHexString(hash);
    }

    /// <summary>把字节渲染为用于显示的十六进制（大写，每两字符一组便于阅读时为 false）。</summary>
    /// <param name="bytes">字节。</param>
    /// <returns>大写十六进制串。</returns>
    public static string ToUpperHex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);

    /// <summary>按 UTF-8 计算字节长度，避免在日志里意外出现 NaN 之类的误判。</summary>
    /// <param name="value">字符串。</param>
    /// <returns>字节数。</returns>
    public static int Utf8ByteCount(string value) => Encoding.UTF8.GetByteCount(value ?? string.Empty);
}
