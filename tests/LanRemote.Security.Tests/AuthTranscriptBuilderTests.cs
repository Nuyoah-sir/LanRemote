using System.Security.Cryptography;
using System.Text;
using LanRemote.Core.Models;
using LanRemote.Security.Auth;
using Xunit;

namespace LanRemote.Security.Tests;

/// <summary>
/// M4 认证 transcript 与 HMAC proof 的确定性测试（纯函数核心，ADR-038 第 1 条）。
/// </summary>
/// <remarks>
/// <para><b>黄金向量由独立 Python 参考实现生成</b>（<c>scripts/reference/gen-auth-golden-vectors.py</c>，
/// 只用标准库 hmac/hashlib/base64/uuid，与 C# 侧零共享代码）。期望值绝不取自被测实现本身
/// （ADR-034 纪律：期望值只能由客观事实派生）。</para>
/// <para><b>与实现的映射关系</b>：本类同时锁定「字节布局」（transcript 逐字节）与
/// 「用途绑定」（proof 随任一字段变化而变化、granted 篡改必致 serverProof 验证失败）。</para>
/// </remarks>
public sealed class AuthTranscriptBuilderTests
{
    // ================== 黄金向量常量（由 gen-auth-golden-vectors.py 生成，勿手改） ==================
    // NUL 以 \u0000 转义写出（C# 中 \0 后跟数字会被解析为八进制转义，必须避免）。

    // Vector 1（control/control）
    private const string Vector1TranscriptUtf8 = "LANREMOTE-AUTH-V1\u000011111111-2222-3333-4444-555555555555\u0000aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\u000099999999-8888-7777-6666-555555555555\u0000AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=\u0000ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8=\u0000AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\u0000control";
    private const string Vector1ClientProofHex = "9C8DDCED05BDC9A75AE14254B82AFB9B8249B6740A8E1C9BBE1D114265783B69";
    private const string Vector1GrantTranscriptUtf8 = "LANREMOTE-GRANT-V1\u00001B37B484267D5CC1EB2555EA7FFDECD61B5A36620754026185F4EDF723F33AF6\u0000control";
    private const string Vector1ServerProofHex = "D102172FA3A2642639F032F03A253912D7803E5FB8EC80A365BAFA393E6B4A7D";

    // Vector 1b：同 Vector 1 transcript、granted 篡改为 view 时的 serverProof（必须与 1 不同）
    private const string Vector1bTamperedGrantServerProofHex = "C51A020223EF4F9B16643AD872015797B9948A8F0D5E8A1A2BE75D5691D6F86D";

    // Vector 2（view/view；nonce 与证书摘要 = SHA256("server-nonce"/"client-nonce"/"cert-der")）
    private const string Vector2TranscriptUtf8 = "LANREMOTE-AUTH-V1\u00000f8fad5b-d9cb-469f-a165-70867728950e\u00007c9e6679-7425-40de-944b-e07fc1f90ae7\u00003f2504e0-4f89-41d3-9a0c-0305e82c3301\u0000CJGZLdfsZh3Cv1vkB6zyH9VX2vFPdivgEsqqBMk3PrE=\u0000o1UlvctH+HYJnxx0s+Gas+pMinv2/S6rajw1vmAlUcY=\u00003418E74EBCA9BFB7638F4B6468612783AFE4C4B0BFE65B83A83D8715426C064D\u0000view";
    private const string Vector2ClientProofHex = "C88B2E03E9AAF99632CDEE772A6131B1BB3B34412B47342F9429F75D18989DAE";
    private const string Vector2GrantTranscriptUtf8 = "LANREMOTE-GRANT-V1\u000038D96CF564D3BD7F5234CD2A24258F63E99F2CC5B1FB17434EACB481793F2D4E\u0000view";
    private const string Vector2ServerProofHex = "4F110CC958505E24EB9B24ABED88E6CB8015120545F7CFF9B9C539116A99A9B6";

    // Vector 3（requested=view、granted=control；除权限字段外与 Vector 1 同输入）
    private const string Vector3TranscriptUtf8 = "LANREMOTE-AUTH-V1\u000011111111-2222-3333-4444-555555555555\u0000aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\u000099999999-8888-7777-6666-555555555555\u0000AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=\u0000ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8=\u0000AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\u0000view";
    private const string Vector3ClientProofHex = "EE7BF44416F02388A9D68D73A464E5C618E5C030877A79BAC3022B205EEE6E47";
    private const string Vector3GrantTranscriptUtf8 = "LANREMOTE-GRANT-V1\u000093C3A57CE9BA1C86B3642E0D78B2270A63A17527522D6AFC37A7C7EB1F35E051\u0000control";
    private const string Vector3ServerProofHex = "CA358DA1CF68F27B852393C0DEE0A71BD4ED018159C86AD134760147874CB710";

    // ==================================== 输入构造 ====================================

    private static readonly Guid Vector1SessionId = new("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Vector1ServerDeviceId = new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid Vector1ClientDeviceId = new("99999999-8888-7777-6666-555555555555");

    private static readonly Guid Vector2SessionId = new("0f8fad5b-d9cb-469f-a165-70867728950e");
    private static readonly Guid Vector2ServerDeviceId = new("7c9e6679-7425-40de-944b-e07fc1f90ae7");
    private static readonly Guid Vector2ClientDeviceId = new("3f2504e0-4f89-41d3-9a0c-0305e82c3301");

    /// <summary>连续递增字节序列：[start, start+1, …, start+count-1]。</summary>
    private static byte[] Bytes(int start, int count) =>
        Enumerable.Range(start, count).Select(i => (byte)i).ToArray();

    /// <summary>16 字节密钥 0x00..0x0F（Vector 1 / 3）。</summary>
    private static byte[] Vector1Key() => Bytes(0x00, 16);

    /// <summary>16 字节密钥 0xFF..0xF0（Vector 2）。</summary>
    private static byte[] Vector2Key() => Enumerable.Range(0, 16).Select(i => (byte)(0xFF - i)).ToArray();

    /// <summary>32 字节 0xAA（Vector 1 / 3 的证书摘要）。</summary>
    private static byte[] CertA() => Enumerable.Repeat((byte)0xAA, 32).ToArray();

    private static byte[] BuildVector1Transcript() => AuthTranscriptBuilder.BuildClientTranscript(
        Vector1SessionId,
        Vector1ServerDeviceId,
        Vector1ClientDeviceId,
        Bytes(0x00, 32),
        Bytes(0x20, 32),
        CertA(),
        SessionPermission.Control);

    // ==================================== 黄金向量 ====================================

    [Fact]
    public void Vector1_ClientTranscript_MatchesGoldenBytes()
    {
        byte[] actual = BuildVector1Transcript();

        Assert.Equal(Encoding.UTF8.GetBytes(Vector1TranscriptUtf8), actual);
    }

    [Fact]
    public void Vector1_ClientProof_MatchesGoldenHex()
    {
        byte[] proof = AuthTranscriptBuilder.ComputeClientProof(Vector1Key(), BuildVector1Transcript());

        Assert.Equal(Vector1ClientProofHex, Convert.ToHexString(proof));
    }

    [Fact]
    public void Vector1_GrantTranscript_MatchesGoldenBytes()
    {
        byte[] grantTranscript = AuthTranscriptBuilder.BuildGrantTranscript(
            BuildVector1Transcript(),
            SessionPermission.Control);

        Assert.Equal(Encoding.UTF8.GetBytes(Vector1GrantTranscriptUtf8), grantTranscript);
    }

    [Fact]
    public void Vector1_ServerProof_MatchesGoldenHex()
    {
        byte[] grantTranscript = AuthTranscriptBuilder.BuildGrantTranscript(
            BuildVector1Transcript(),
            SessionPermission.Control);

        byte[] proof = AuthTranscriptBuilder.ComputeServerProof(Vector1Key(), grantTranscript);

        Assert.Equal(Vector1ServerProofHex, Convert.ToHexString(proof));
    }

    [Fact]
    public void Vector2_FullChain_MatchesGoldenVector()
    {
        byte[] transcript = AuthTranscriptBuilder.BuildClientTranscript(
            Vector2SessionId,
            Vector2ServerDeviceId,
            Vector2ClientDeviceId,
            SHA256.HashData(Encoding.UTF8.GetBytes("server-nonce")),
            SHA256.HashData(Encoding.UTF8.GetBytes("client-nonce")),
            SHA256.HashData(Encoding.UTF8.GetBytes("cert-der")),
            SessionPermission.ViewOnly);

        Assert.Equal(Encoding.UTF8.GetBytes(Vector2TranscriptUtf8), transcript);
        Assert.Equal(
            Vector2ClientProofHex,
            Convert.ToHexString(AuthTranscriptBuilder.ComputeClientProof(Vector2Key(), transcript)));

        byte[] grantTranscript = AuthTranscriptBuilder.BuildGrantTranscript(
            transcript,
            SessionPermission.ViewOnly);

        Assert.Equal(Encoding.UTF8.GetBytes(Vector2GrantTranscriptUtf8), grantTranscript);
        Assert.Equal(
            Vector2ServerProofHex,
            Convert.ToHexString(AuthTranscriptBuilder.ComputeServerProof(Vector2Key(), grantTranscript)));
    }

    [Fact]
    public void Vector3_RequestedViewGrantedControl_MatchesGoldenVector()
    {
        // 与 Vector 1 仅权限字段不同：requested 进 client 档、granted 进 grant 档，互不混淆。
        byte[] transcript = AuthTranscriptBuilder.BuildClientTranscript(
            Vector1SessionId,
            Vector1ServerDeviceId,
            Vector1ClientDeviceId,
            Bytes(0x00, 32),
            Bytes(0x20, 32),
            CertA(),
            SessionPermission.ViewOnly);

        Assert.Equal(Encoding.UTF8.GetBytes(Vector3TranscriptUtf8), transcript);
        Assert.Equal(
            Vector3ClientProofHex,
            Convert.ToHexString(AuthTranscriptBuilder.ComputeClientProof(Vector1Key(), transcript)));

        byte[] grantTranscript = AuthTranscriptBuilder.BuildGrantTranscript(
            transcript,
            SessionPermission.Control);

        Assert.Equal(Encoding.UTF8.GetBytes(Vector3GrantTranscriptUtf8), grantTranscript);
        Assert.Equal(
            Vector3ServerProofHex,
            Convert.ToHexString(AuthTranscriptBuilder.ComputeServerProof(Vector1Key(), grantTranscript)));
    }

    // ==================================== 结构与边界 ====================================

    [Fact]
    public void SameInput_BuildTwice_ProducesByteIdenticalResults()
    {
        Assert.Equal(BuildVector1Transcript(), BuildVector1Transcript());

        byte[] firstProof = AuthTranscriptBuilder.ComputeClientProof(Vector1Key(), BuildVector1Transcript());
        byte[] secondProof = AuthTranscriptBuilder.ComputeClientProof(Vector1Key(), BuildVector1Transcript());

        Assert.Equal(firstProof, secondProof);
    }

    [Fact]
    public void ClientTranscript_HasExactlyEightNulSeparatedFields()
    {
        string[] fields = Encoding.UTF8.GetString(BuildVector1Transcript()).Split('\0');

        Assert.Equal(8, fields.Length);
        Assert.Equal(AuthProtocol.ClientTranscriptDomain, fields[0]);
        Assert.Equal("11111111-2222-3333-4444-555555555555", fields[1]);
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", fields[2]);
        Assert.Equal("99999999-8888-7777-6666-555555555555", fields[3]);
        Assert.Equal("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=", fields[4]);
        Assert.Equal("ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8=", fields[5]);
        Assert.Equal(new string('A', 64), fields[6]);
        Assert.Equal("control", fields[7]);
    }

    [Fact]
    public void GrantTranscript_HasExactlyThreeNulSeparatedFields()
    {
        byte[] grantTranscript = AuthTranscriptBuilder.BuildGrantTranscript(
            BuildVector1Transcript(),
            SessionPermission.Control);

        string[] fields = Encoding.UTF8.GetString(grantTranscript).Split('\0');

        Assert.Equal(3, fields.Length);
        Assert.Equal(AuthProtocol.GrantTranscriptDomain, fields[0]);
        Assert.Equal(64, fields[1].Length); // SHA-256 的 hex
        Assert.Equal("control", fields[2]);
    }

    [Fact]
    public void Transcripts_StartWithTheirOwnDomains()
    {
        // 域分隔是「两档不得互相冒充」的第一道结构防线。
        Assert.StartsWith("LANREMOTE-AUTH-V1\u0000", Vector1TranscriptUtf8, StringComparison.Ordinal);
        Assert.StartsWith("LANREMOTE-GRANT-V1\u0000", Vector1GrantTranscriptUtf8, StringComparison.Ordinal);
        Assert.NotEqual(AuthProtocol.ClientTranscriptDomain, AuthProtocol.GrantTranscriptDomain);
    }

    // ==================================== 字段敏感性（改任一字段必改 proof） ====================================

    [Fact]
    public void WrongAccessKey_ChangesClientProof()
    {
        byte[] wrongKey = Vector1Key();
        wrongKey[0] ^= 0x01;

        string actual = Convert.ToHexString(
            AuthTranscriptBuilder.ComputeClientProof(wrongKey, BuildVector1Transcript()));

        Assert.NotEqual(Vector1ClientProofHex, actual);
    }

    [Fact]
    public void ModifiedCertificateFingerprint_ChangesTranscriptAndClientProof()
    {
        byte[] modifiedCert = CertA();
        modifiedCert[0] ^= 0x01;

        byte[] modifiedTranscript = AuthTranscriptBuilder.BuildClientTranscript(
            Vector1SessionId,
            Vector1ServerDeviceId,
            Vector1ClientDeviceId,
            Bytes(0x00, 32),
            Bytes(0x20, 32),
            modifiedCert,
            SessionPermission.Control);

        Assert.NotEqual(BuildVector1Transcript(), modifiedTranscript);
        Assert.NotEqual(
            Vector1ClientProofHex,
            Convert.ToHexString(AuthTranscriptBuilder.ComputeClientProof(Vector1Key(), modifiedTranscript)));
    }

    [Fact]
    public void ModifiedRequestedPermission_ChangesTranscriptAndClientProof()
    {
        // control（Vector 1）与 view（Vector 3）在同输入下的 transcript 与 clientProof 必不同。
        Assert.NotEqual(Vector1ClientProofHex, Vector3ClientProofHex);
        Assert.NotEqual(Vector1TranscriptUtf8, Vector3TranscriptUtf8);
    }

    [Fact]
    public void ModifiedClientNonce_ChangesClientProof()
    {
        byte[] modifiedNonce = Bytes(0x20, 32);
        modifiedNonce[0] ^= 0x01;

        byte[] modifiedTranscript = AuthTranscriptBuilder.BuildClientTranscript(
            Vector1SessionId,
            Vector1ServerDeviceId,
            Vector1ClientDeviceId,
            Bytes(0x00, 32),
            modifiedNonce,
            CertA(),
            SessionPermission.Control);

        Assert.NotEqual(
            Vector1ClientProofHex,
            Convert.ToHexString(AuthTranscriptBuilder.ComputeClientProof(Vector1Key(), modifiedTranscript)));
    }

    [Fact]
    public void ModifiedSessionId_ChangesClientProof()
    {
        byte[] modifiedTranscript = AuthTranscriptBuilder.BuildClientTranscript(
            new Guid("11111111-2222-3333-4444-555555555556"), // 末位 5→6
            Vector1ServerDeviceId,
            Vector1ClientDeviceId,
            Bytes(0x00, 32),
            Bytes(0x20, 32),
            CertA(),
            SessionPermission.Control);

        Assert.NotEqual(
            Vector1ClientProofHex,
            Convert.ToHexString(AuthTranscriptBuilder.ComputeClientProof(Vector1Key(), modifiedTranscript)));
    }

    [Fact]
    public void TamperedGrantedPermission_FailsServerProofVerification()
    {
        byte[] transcript = BuildVector1Transcript();

        // 服务端如实按 granted=control 出具 serverProof（= 黄金向量 1）。
        byte[] genuine = AuthTranscriptBuilder.ComputeServerProof(
            Vector1Key(),
            AuthTranscriptBuilder.BuildGrantTranscript(transcript, SessionPermission.Control));
        Assert.Equal(Vector1ServerProofHex, Convert.ToHexString(genuine));

        // 客户端收到被篡改为 granted=view 的帧：用自己 transcript + 收到的 granted 重建，必得另一值。
        byte[] rebuilt = AuthTranscriptBuilder.ComputeServerProof(
            Vector1Key(),
            AuthTranscriptBuilder.BuildGrantTranscript(transcript, SessionPermission.ViewOnly));

        Assert.Equal(Vector1bTamperedGrantServerProofHex, Convert.ToHexString(rebuilt));
        Assert.NotEqual(Convert.ToHexString(genuine), Convert.ToHexString(rebuilt));
    }

    [Fact]
    public void ServerProof_DependsOnServerPrefix()
    {
        // 前缀不是装饰：不带 "server\0" 的 HMAC 与协议值必然不同（防「前缀被无声去掉」）。
        byte[] grantTranscript = AuthTranscriptBuilder.BuildGrantTranscript(
            BuildVector1Transcript(),
            SessionPermission.Control);

        string withoutPrefix = Convert.ToHexString(HMACSHA256.HashData(Vector1Key(), grantTranscript));
        string actual = Convert.ToHexString(
            AuthTranscriptBuilder.ComputeServerProof(Vector1Key(), grantTranscript));

        // ① 黄金值确非「无前缀」错误实现的输出（重生成黄金值时的防伪）。
        Assert.NotEqual(Vector1ServerProofHex, withoutPrefix);
        // ② 被测实现的实际输出 ≠ 无前缀 HMAC —— 这条才直接约束实现。
        Assert.NotEqual(withoutPrefix, actual);
    }

    // ==================================== 参数校验（fail fast） ====================================

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void BuildClientTranscript_RejectsWrongServerNonceLength(int length)
    {
        Assert.Throws<ArgumentException>(() => AuthTranscriptBuilder.BuildClientTranscript(
            Vector1SessionId,
            Vector1ServerDeviceId,
            Vector1ClientDeviceId,
            Bytes(0x00, length),
            Bytes(0x20, 32),
            CertA(),
            SessionPermission.Control));
    }

    [Fact]
    public void BuildClientTranscript_RejectsWrongClientNonceLength()
    {
        Assert.Throws<ArgumentException>(() => AuthTranscriptBuilder.BuildClientTranscript(
            Vector1SessionId,
            Vector1ServerDeviceId,
            Vector1ClientDeviceId,
            Bytes(0x00, 32),
            Bytes(0x20, 31),
            CertA(),
            SessionPermission.Control));
    }

    [Fact]
    public void BuildClientTranscript_RejectsWrongCertificateHashLength()
    {
        Assert.Throws<ArgumentException>(() => AuthTranscriptBuilder.BuildClientTranscript(
            Vector1SessionId,
            Vector1ServerDeviceId,
            Vector1ClientDeviceId,
            Bytes(0x00, 32),
            Bytes(0x20, 32),
            Bytes(0xAA, 16),
            SessionPermission.Control));
    }

    [Theory]
    [InlineData(15)]
    [InlineData(17)]
    [InlineData(0)]
    public void ComputeClientProof_RejectsWrongKeyLength(int keyLength)
    {
        Assert.Throws<ArgumentException>(() => AuthTranscriptBuilder.ComputeClientProof(
            Bytes(0x00, keyLength),
            BuildVector1Transcript()));
    }

    [Fact]
    public void ComputeServerProof_RejectsWrongKeyLength()
    {
        byte[] grantTranscript = AuthTranscriptBuilder.BuildGrantTranscript(
            BuildVector1Transcript(),
            SessionPermission.Control);

        Assert.Throws<ArgumentException>(() => AuthTranscriptBuilder.ComputeServerProof(
            Bytes(0x00, 32),
            grantTranscript));
    }
}
