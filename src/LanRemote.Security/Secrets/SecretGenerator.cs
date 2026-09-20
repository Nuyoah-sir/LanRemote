using System.Security.Cryptography;
using LanRemote.Core.Encoding;

namespace LanRemote.Security.Secrets;

/// <summary>
/// 秘密的现场生成。
/// </summary>
/// <remarks>
/// 硬约束：只允许 <see cref="RandomNumberGenerator"/>，**禁止 <see cref="Random"/>、固定密码、6 位 PIN**
/// （ADR-004、01_MASTER_PROMPT.md 第 7.2 节、06_DEV_STANDARDS.md 第 5 节）。
/// </remarks>
public static class SecretGenerator
{
    /// <summary>128-bit 熵，与 <c>AccessSecret.AccessKeyBitLength</c> 一致。</summary>
    public const int AccessKeyByteCount = 16;

    /// <summary>PFX 导出口令的随机字节数。</summary>
    public const int PfxPasswordByteCount = 32;

    /// <summary>生成新的随机设备标识。</summary>
    /// <returns>新的 GUID。</returns>
    public static Guid NewDeviceGuid() => Guid.NewGuid();

    /// <summary>生成 128-bit 随机访问密钥的原始字节。</summary>
    /// <returns>16 字节。</returns>
    public static byte[] NewAccessKeyBytes() => RandomNumberGenerator.GetBytes(AccessKeyByteCount);

    /// <summary>生成 128-bit 随机访问密钥，并返回可展示的 Base32 形式。</summary>
    /// <returns>26 个 Base32 字符，不含分隔符。</returns>
    /// <remarks>
    /// 原始字节在完成编码后立即清零；返回的是 <see cref="string"/>，
    /// 而 .NET 的 string 不可变，无法可靠清零——这一限制在 <c>HANDOFF.md</c> 中如实记录。
    /// </remarks>
    public static string NewAccessKey()
    {
        byte[] key = NewAccessKeyBytes();
        try
        {
            return CrockfordBase32.Encode(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>把 Base32 形式的访问密钥还原为原始字节。</summary>
    /// <param name="base32AccessKey">26 个 Base32 字符（允许含分隔符与空白）。</param>
    /// <param name="keyBytes">原始字节。</param>
    /// <returns>是否为合法的访问密钥编码。</returns>
    /// <remarks>长度必须严格等于 16 字节，否则说明密钥被截断或被篡改。</remarks>
    public static bool TryDecodeAccessKey(string? base32AccessKey, out byte[] keyBytes) =>
        CrockfordBase32.TryDecodeExact(base32AccessKey, AccessKeyByteCount, out keyBytes);

    /// <summary>生成用于导出 PFX 的随机口令。</summary>
    /// <returns>Base64 形式的口令。</returns>
    public static string NewPfxPassword() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(PfxPasswordByteCount));
}
