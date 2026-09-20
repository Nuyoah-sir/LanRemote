using System.Security.Cryptography;
using LanRemote.Core.Encoding;

namespace LanRemote.Core.Identity;

/// <summary>
/// 设备码派生与校验。
/// </summary>
/// <remarks>
/// <para>规则（01_MASTER_PROMPT.md 第 7.1 节）：</para>
/// <list type="bullet">
/// <item><description><c>设备码 = Base32(SHA256(deviceGuid) 的前 5 个字节)</c>，共 8 个字符；</description></item>
/// <item><description>5 字节 = 40 bit，正好编码为 8 个 Base32 字符，不需要 padding；</description></item>
/// <item><description>展示形如 <c>7K3M-P9QX</c>；</description></item>
/// <item><description>设备码<b>不是秘密</b>，只用于识别，不得用于任何认证判定。</description></item>
/// </list>
/// </remarks>
public static class DeviceCode
{
    /// <summary>派生所需的哈希字节数。</summary>
    public const int HashByteCount = 5;

    /// <summary>编码后的字符数（不含分隔符）。</summary>
    public const int EncodedLength = 8;

    /// <summary>展示时每组的字符数。</summary>
    public const int DisplayGroupSize = 4;

    /// <summary>由设备 GUID 派生不带分隔符的 8 字符设备码。</summary>
    /// <param name="deviceGuid">稳定设备标识。</param>
    /// <returns>8 个字符的大写 Base32 串。</returns>
    public static string DeriveRaw(Guid deviceGuid)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(deviceGuid.ToByteArray(), hash);

        return CrockfordBase32.Encode(hash[..HashByteCount]);
    }

    /// <summary>由设备 GUID 派生展示用设备码，形如 <c>7K3M-P9QX</c>。</summary>
    /// <param name="deviceGuid">稳定设备标识。</param>
    /// <returns>分组后的设备码。</returns>
    public static string Derive(Guid deviceGuid) =>
        CrockfordBase32.Group(DeriveRaw(deviceGuid), DisplayGroupSize);

    /// <summary>校验用户输入的设备码是否形如 <c>7K3M-P9QX</c>。</summary>
    /// <param name="deviceCode">待校验字符串，允许含/不含分隔符，大小写不敏感。</param>
    /// <returns>是否为合法设备码。</returns>
    public static bool IsWellFormed(string? deviceCode)
    {
        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            return false;
        }

        // 去掉分隔符与空白后必须正好剩下 8 个字母表字符。
        string normalized = deviceCode.Replace("-", string.Empty, StringComparison.Ordinal);

        return normalized.Length == EncodedLength
            && CrockfordBase32.TryDecode(normalized, out byte[] _);
    }
}
