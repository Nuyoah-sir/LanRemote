using System.Text;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;
using Xunit;

namespace LanRemote.Transport.Tests;

/// <summary>
/// M4 认证协议词汇表（<see cref="AuthProtocol"/>）的字面量锁定。
/// </summary>
/// <remarks>
/// <para>这些断言看似「把常量抄一遍」，实为目的明确：这组串与数字是<b>协议兼容性合同</b>——
/// 改动它们会静默破坏与既有对端（以及黄金向量）的互通。锁定测试让「无意改写」变成「测试变红」，
/// 强制改动者先去修订 ADR-038 与参考脚本（ADR-038：「两处不得漂移」）。</para>
/// <para>测试中的期望值一律是<b>手写字面量</b>，不得引用被测常量本身（否则是套套逻辑）。</para>
/// </remarks>
public sealed class AuthProtocolTests
{
    [Fact]
    public void TranscriptDomains_AreExactLiterals()
    {
        Assert.Equal("LANREMOTE-AUTH-V1", AuthProtocol.ClientTranscriptDomain);
        Assert.Equal("LANREMOTE-GRANT-V1", AuthProtocol.GrantTranscriptDomain);
    }

    [Fact]
    public void ServerProofPrefix_IsAsciiServerPlusSingleNulByte()
    {
        // 精确到字节：6 个 ASCII 字符 + 1 个 NUL，共 7 字节；不是 "server|"、不是双 NUL。
        Assert.Equal(
            new byte[] { 0x73, 0x65, 0x72, 0x76, 0x65, 0x72, 0x00 },
            Encoding.ASCII.GetBytes(AuthProtocol.ServerProofPrefix));
    }

    [Fact]
    public void FrameTypes_AreExactLiterals()
    {
        Assert.Equal("auth_challenge", AuthProtocol.TypeAuthChallenge);
        Assert.Equal("auth_response", AuthProtocol.TypeAuthResponse);
        Assert.Equal("auth_success", AuthProtocol.TypeAuthSuccess);
        Assert.Equal("approval_pending", AuthProtocol.TypeApprovalPending);
        Assert.Equal("authentication_failed", AuthProtocol.TypeAuthenticationFailed);
    }

    [Fact]
    public void FieldNames_AreExactFrozenVocabulary()
    {
        // 顺序与 AuthProtocol 中的声明顺序对齐（便于逐项对读）。
        string[] expected =
        [
            "type",
            "protocol",
            "sessionId",
            "serverDeviceId",
            "clientDeviceId",
            "clientName",
            "serverNonce",
            "clientNonce",
            "certSha256",
            "requestedPermission",
            "clientProof",
            "grantedPermission",
            "serverProof",
            "sessionToken",
            "videoAttachExpiresInMs",
            "expiresInMs",
        ];

        string[] actual =
        [
            AuthProtocol.FieldType,
            AuthProtocol.FieldProtocol,
            AuthProtocol.FieldSessionId,
            AuthProtocol.FieldServerDeviceId,
            AuthProtocol.FieldClientDeviceId,
            AuthProtocol.FieldClientName,
            AuthProtocol.FieldServerNonce,
            AuthProtocol.FieldClientNonce,
            AuthProtocol.FieldCertSha256,
            AuthProtocol.FieldRequestedPermission,
            AuthProtocol.FieldClientProof,
            AuthProtocol.FieldGrantedPermission,
            AuthProtocol.FieldServerProof,
            AuthProtocol.FieldSessionToken,
            AuthProtocol.FieldVideoAttachExpiresInMs,
            AuthProtocol.FieldExpiresInMs,
        ];

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Sizes_AreExact()
    {
        Assert.Equal(1, AuthProtocol.WireProtocolVersion);
        Assert.Equal(32, AuthProtocol.NonceByteLength);
        Assert.Equal(32, AuthProtocol.ProofByteLength);
        Assert.Equal(32, AuthProtocol.SessionTokenByteLength);
        Assert.Equal(32, AuthProtocol.CertificateSha256ByteLength);
    }

    [Fact]
    public void Timeouts_AreProvisionalValues()
    {
        // provisional（ADR-038 第 4 条）：数值实验定案后随 ADR 回写，届时同步改此测试。
        Assert.Equal(10_000, AuthProtocol.AuthenticationWindowMilliseconds);
        Assert.Equal(60_000, AuthProtocol.ApprovalWindowMilliseconds);
    }

    [Fact]
    public void EncodePermission_MapsViewAndControl()
    {
        Assert.Equal("view", AuthProtocol.EncodePermission(SessionPermission.ViewOnly));
        Assert.Equal("control", AuthProtocol.EncodePermission(SessionPermission.Control));
    }

    [Fact]
    public void EncodePermission_RejectsUndefinedEnumValue()
    {
        // 将来扩展 SessionPermission 时，若忘记同步映射，必须当场失败而不是静默编码。
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AuthProtocol.EncodePermission((SessionPermission)2));
    }
}
