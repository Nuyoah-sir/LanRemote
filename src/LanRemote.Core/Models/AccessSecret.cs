namespace LanRemote.Core.Models;

/// <summary>
/// 本设备的最小秘密集合。
/// </summary>
/// <param name="AccessKeyBytes">128-bit 随机访问密钥的原始字节。</param>
/// <remarks>
/// <para>硬约束（见 <c>01_MASTER_PROMPT.md</c> 第七节与 <c>06_DEV_STANDARDS.md</c> 第 5 节）：</para>
/// <list type="bullet">
/// <item><description>密钥必须使用 <see cref="System.Security.Cryptography.RandomNumberGenerator"/> 生成，禁止 <see cref="Random"/>；</description></item>
/// <item><description>绝不明文上网，只发送绑定 transcript 的 HMAC proof；</description></item>
/// <item><description>绝不写入日志、<c>config.json</c> 或控制台；</description></item>
/// <item><description>不得以 Base64 当加密，rest 状态由 DPAPI 保护。</description></item>
/// </list>
/// <para>M1 才实现存储与编解码；M0 仅定义模型。</para>
/// </remarks>
public sealed record AccessSecret(byte[] AccessKeyBytes)
{
    /// <summary>访问密钥的熵位数。</summary>
    public const int AccessKeyBitLength = 128;

    /// <summary>访问密钥的字节数。</summary>
    public const int AccessKeyByteLength = AccessKeyBitLength / 8;
}
