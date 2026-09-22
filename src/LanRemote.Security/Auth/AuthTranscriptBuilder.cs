using System.Security.Cryptography;
using System.Text;
using LanRemote.Core.Models;

namespace LanRemote.Security.Auth;

/// <summary>
/// M4 认证 transcript 与 HMAC proof 的纯函数核心。
/// </summary>
/// <remarks>
/// <para>字节布局以 <c>docs/DECISIONS.md</c> 的 ADR-038 第 1 条为准（<c>\0</c> = 单字节 0x00，
/// 字段值编码：uuid 小写 <c>D</c> 格式串 / base64 canonical / uppercase hex / 枚举线上串）：</para>
/// <code>
/// ClientAuthTranscript =
///   "LANREMOTE-AUTH-V1\0" sessionId "\0" serverDeviceId "\0" clientDeviceId "\0"
///   serverNonce(base64) "\0" clientNonce(base64) "\0" certSha256(UPPER HEX) "\0"
///   requestedPermission                      （末尾字段，无尾随 \0）
///
/// clientProof = HMAC-SHA256(accessKeyBytes, ClientAuthTranscript)
///
/// ServerGrantTranscript =
///   "LANREMOTE-GRANT-V1\0" SHA256(ClientAuthTranscript).UPPER HEX "\0" grantedPermission
///
/// serverProof = HMAC-SHA256(accessKeyBytes, "server\0" || ServerGrantTranscript)
/// </code>
/// <para><b>输入全部是强类型值/字节</b>（<see cref="Guid"/> / <see cref="ReadOnlySpan{T}"/> /
/// 枚举），<b>不接收 string</b>：规范形式只由本类输出，从类型上消灭「对端串编码差异
/// （hex 大小写、非规范 base64、uuid 格式）进入 transcript」的整类问题。</para>
/// <para>本类是无状态纯函数；所有分支可被确定性单测穷举（黄金向量见
/// <c>scripts/reference/gen-auth-golden-vectors.py</c>，由独立 Python 实现生成）。</para>
/// </remarks>
public static class AuthTranscriptBuilder
{
    /// <summary>
    /// 构造客户端认证 transcript（<c>clientProof</c> 的 MAC 输入）。
    /// </summary>
    /// <param name="sessionId">会话 id（服务端 challenge 指定）。</param>
    /// <param name="serverDeviceId">服务端设备 id。</param>
    /// <param name="clientDeviceId">客户端设备 id。</param>
    /// <param name="serverNonce">服务端 nonce 原始字节，必须 32 字节。</param>
    /// <param name="clientNonce">客户端 nonce 原始字节，必须 32 字节。</param>
    /// <param name="certificateSha256">服务端证书 DER 的 SHA-256 摘要（32 字节）；
    /// 必须取自本连接冻结的安全上下文，而不是「再次去查一次」。</param>
    /// <param name="requestedPermission">请求的权限。</param>
    /// <returns>transcript 的 UTF-8 字节（内容为纯 ASCII）。</returns>
    /// <exception cref="ArgumentException">任一固定长度字段尺寸不符。</exception>
    public static byte[] BuildClientTranscript(
        Guid sessionId,
        Guid serverDeviceId,
        Guid clientDeviceId,
        ReadOnlySpan<byte> serverNonce,
        ReadOnlySpan<byte> clientNonce,
        ReadOnlySpan<byte> certificateSha256,
        SessionPermission requestedPermission)
    {
        RequireLength(serverNonce, AuthProtocol.NonceByteLength, nameof(serverNonce));
        RequireLength(clientNonce, AuthProtocol.NonceByteLength, nameof(clientNonce));
        RequireLength(
            certificateSha256,
            AuthProtocol.CertificateSha256ByteLength,
            nameof(certificateSha256));

        string transcript = string.Join(
            '\0',
            AuthProtocol.ClientTranscriptDomain,
            sessionId.ToString("D"),
            serverDeviceId.ToString("D"),
            clientDeviceId.ToString("D"),
            Convert.ToBase64String(serverNonce),
            Convert.ToBase64String(clientNonce),
            Convert.ToHexString(certificateSha256),
            AuthProtocol.EncodePermission(requestedPermission));

        return Encoding.UTF8.GetBytes(transcript);
    }

    /// <summary>
    /// 构造服务端授权（grant）transcript（<c>serverProof</c> 的 MAC 输入载体）。
    /// </summary>
    /// <param name="clientTranscript">客户端认证 transcript 的完整字节
    /// （即 <see cref="BuildClientTranscript"/> 的输出）。</param>
    /// <param name="grantedPermission">服务端<b>实际授予</b>的权限。</param>
    /// <returns>grant transcript 的 UTF-8 字节（内容为纯 ASCII）。</returns>
    /// <remarks>
    /// 客户端验证 <c>serverProof</c> 时用<b>自己那份</b> transcript 与收到的
    /// <c>grantedPermission</c> 重建本串：granted 被中途篡改，重算结果必然不同，
    /// 验证失败（ADR-038 第 1 条）。
    /// </remarks>
    public static byte[] BuildGrantTranscript(
        ReadOnlySpan<byte> clientTranscript,
        SessionPermission grantedPermission)
    {
        byte[] transcriptHash = SHA256.HashData(clientTranscript);

        string transcript = string.Join(
            '\0',
            AuthProtocol.GrantTranscriptDomain,
            Convert.ToHexString(transcriptHash),
            AuthProtocol.EncodePermission(grantedPermission));

        return Encoding.UTF8.GetBytes(transcript);
    }

    /// <summary>
    /// 计算 <c>clientProof</c>：<c>HMAC-SHA256(accessKeyBytes, ClientAuthTranscript)</c>。
    /// </summary>
    /// <param name="accessKeyBytes">访问密钥原始字节，必须 16 字节（128-bit）。</param>
    /// <param name="clientTranscript"><see cref="BuildClientTranscript"/> 的输出。</param>
    /// <returns>32 字节 proof。</returns>
    /// <exception cref="ArgumentException">访问密钥长度不是 16 字节。</exception>
    public static byte[] ComputeClientProof(
        ReadOnlySpan<byte> accessKeyBytes,
        ReadOnlySpan<byte> clientTranscript)
    {
        RequireAccessKeyLength(accessKeyBytes);
        return HMACSHA256.HashData(accessKeyBytes, clientTranscript);
    }

    /// <summary>
    /// 计算 <c>serverProof</c>：<c>HMAC-SHA256(accessKeyBytes, "server\0" || GrantTranscript)</c>。
    /// </summary>
    /// <param name="accessKeyBytes">访问密钥原始字节，必须 16 字节（128-bit）。</param>
    /// <param name="grantTranscript"><see cref="BuildGrantTranscript"/> 的输出。</param>
    /// <returns>32 字节 proof。</returns>
    /// <exception cref="ArgumentException">访问密钥长度不是 16 字节。</exception>
    public static byte[] ComputeServerProof(
        ReadOnlySpan<byte> accessKeyBytes,
        ReadOnlySpan<byte> grantTranscript)
    {
        RequireAccessKeyLength(accessKeyBytes);

        // MAC 输入 = "server\0" || GrantTranscript（ADR-038；前缀以规格 04 §9 为准）。
        byte[] prefix = Encoding.UTF8.GetBytes(AuthProtocol.ServerProofPrefix);
        byte[] message = new byte[prefix.Length + grantTranscript.Length];
        prefix.CopyTo(message.AsSpan());
        grantTranscript.CopyTo(message.AsSpan(prefix.Length));

        return HMACSHA256.HashData(accessKeyBytes, message);
    }

    private static void RequireLength(ReadOnlySpan<byte> value, int expectedLength, string parameterName)
    {
        if (value.Length != expectedLength)
        {
            throw new ArgumentException(
                $"长度必须为 {expectedLength} 字节，实际 {value.Length} 字节。",
                parameterName);
        }
    }

    private static void RequireAccessKeyLength(ReadOnlySpan<byte> accessKeyBytes)
    {
        if (accessKeyBytes.Length != AccessSecret.AccessKeyByteLength)
        {
            throw new ArgumentException(
                $"访问密钥必须为 {AccessSecret.AccessKeyByteLength} 字节，实际 {accessKeyBytes.Length} 字节。",
                nameof(accessKeyBytes));
        }
    }
}
