using System.Text;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

/// <summary>
/// <see cref="AuthChallengeFrame"/> 的严格解析：M4 阶段 2 步骤 9 的验收面。
/// </summary>
/// <remarks>
/// <para>拒绝原因只用于本地日志、绝不下发给对端；本测试同时锁死这一点
/// （<c>Rejections_Are_Short_Codes_Only</c>）。</para>
/// <para>输入构造两种手法：① 基线字面量 <see cref="ValidChallenge"/>（规格样本形状，
/// 供接受路径与结构级负例使用）；② <see cref="Build"/>（原始 token 直落的变体构造器，
/// 供值级/类型级负例使用）。<see cref="Build_Helper_Matches_The_Handwritten_Literal"/>
/// 锁死两者同形，防止两处漂移。</para>
/// <para>字面量常量与 <c>scripts/reference/gen-auth-golden-vectors.py</c> 的向量 1 同源
/// （uuid 与 32 字节 <c>00..1F</c> 模式），便于与 transcript 测试互相印证。</para>
/// </remarks>
public sealed class AuthChallengeFrameTests
{
    private const string SessionId = "11111111-2222-3333-4444-555555555555";
    private const string ServerDeviceId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string ServerNonce = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private const string CertSha256 = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

    private static readonly byte[] Range32 = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    /// <summary>规格样本形状的合法 challenge（字段顺序照规格 04 §9）。</summary>
    private const string ValidChallenge =
        """{"type":"auth_challenge","protocol":1,"sessionId":"11111111-2222-3333-4444-555555555555","serverDeviceId":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","serverNonce":"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=","certSha256":"000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F","expiresInMs":15000}""";

    /// <summary>
    /// 用原始 token 拼 challenge JSON：token <b>不经任何编码</b>直接落入文本——
    /// 这样既能构造「值错了」（<c>sessionId:"not-a-uuid"</c>），也能构造
    /// 「JSON 类型错了」（<c>protocol:"1"</c>、<c>sessionId:null</c>）。默认参数即合法载荷。
    /// </summary>
    private static string Build(
        string type = "\"auth_challenge\"",
        string protocol = "1",
        string sessionId = "\"" + SessionId + "\"",
        string serverDeviceId = "\"" + ServerDeviceId + "\"",
        string serverNonce = "\"" + ServerNonce + "\"",
        string certSha256 = "\"" + CertSha256 + "\"",
        string expiresInMs = "15000")
        => $"{{\"type\":{type},\"protocol\":{protocol},\"sessionId\":{sessionId}," +
           $"\"serverDeviceId\":{serverDeviceId},\"serverNonce\":{serverNonce}," +
           $"\"certSha256\":{certSha256},\"expiresInMs\":{expiresInMs}}}";

    /// <summary>对基线字面量做精确补丁（结构级负例：重复字段、大小写、注释、尾逗号等）。</summary>
    private static string Patch(string from, string to) => ValidChallenge.Replace(from, to, StringComparison.Ordinal);

    private static byte[] Utf8(string json) => Encoding.UTF8.GetBytes(json);

    /// <summary>
    /// 造「N 字节 0xAA」的 canonical base64，用 BCL 编码器（独立于被测实现）。
    /// 起因：手抄长 base64 字面量出过一次错（45 字符的"31 字节"串——它根本不是合法 base64，
    /// 于是那条测试连长度判定都没碰到就被别的拒绝路径救了；变异验证当场抓住）。
    /// </summary>
    private static string B64(int byteCount) =>
        Convert.ToBase64String(Enumerable.Repeat((byte)0xAA, byteCount).ToArray());

    private static void Rejects(string json, string expected)
    {
        Assert.False(AuthChallengeFrame.TryParse(Utf8(json), out AuthChallengeFrame? frame, out string? rejection), $"本该被拒却通过了：{json}");
        Assert.Null(frame);
        Assert.Equal(expected, rejection);
    }

    /// <summary>构造器默认参数与手写字面量必须保持同形（防两处漂移）。</summary>
    [Fact]
    public void Build_Helper_Matches_The_Handwritten_Literal()
    {
        Assert.Equal(ValidChallenge, Build());
    }

    [Fact]
    public void Accepts_The_Spec_Shaped_Challenge()
    {
        Assert.True(AuthChallengeFrame.TryParse(Utf8(ValidChallenge), out AuthChallengeFrame? frame, out string? rejection));
        Assert.Null(rejection);
        Assert.NotNull(frame);

        Assert.Equal(new Guid(SessionId), frame!.SessionId);
        Assert.Equal(new Guid(ServerDeviceId), frame.ServerDeviceId);
        Assert.Equal(Range32, frame.ServerNonce.ToArray());
        Assert.Equal(Range32, frame.CertificateSha256.ToArray());
        Assert.Equal(15000, frame.ExpiresInMs);
    }

    [Fact]
    public void Allows_Surrounding_Whitespace()
    {
        Assert.True(AuthChallengeFrame.TryParse(Utf8("  " + ValidChallenge + "\r\n"), out _, out string? rejection));
        Assert.Null(rejection);
    }

    [Fact]
    public void Serialize_Round_Trips_Through_The_Same_Strict_Parser()
    {
        AuthChallengeFrame original = new(
            new Guid(SessionId), new Guid(ServerDeviceId), Range32, Range32, 15000);

        byte[] payload = original.Serialize();

        Assert.True(
            AuthChallengeFrame.TryParse(payload, out AuthChallengeFrame? parsed, out string? rejection),
            Encoding.UTF8.GetString(payload));
        Assert.Null(rejection);
        Assert.NotNull(parsed);

        Assert.Equal(original.SessionId, parsed!.SessionId);
        Assert.Equal(original.ServerDeviceId, parsed.ServerDeviceId);
        Assert.Equal(original.ServerNonce.ToArray(), parsed.ServerNonce.ToArray());
        Assert.Equal(original.CertificateSha256.ToArray(), parsed.CertificateSha256.ToArray());
        Assert.Equal(original.ExpiresInMs, parsed.ExpiresInMs);
    }

    [Fact]
    public void Serialize_Is_Byte_Stable()
    {
        AuthChallengeFrame frame = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Enumerable.Repeat((byte)0xAB, 32).ToArray(),
            Enumerable.Repeat((byte)0xCD, 32).ToArray(),
            1);

        Assert.Equal(frame.Serialize(), frame.Serialize());
    }

    /// <summary>
    /// 交叉类型：把其它四类帧（由各自实现序列化出来的真实字节）喂给 challenge 解析器，
    /// 必须全部以 wrong-type 拒绝——认证帧的「类型认领」互不串门。
    /// </summary>
    [Fact]
    public void Does_Not_Cross_Accept_Other_Frame_Payloads()
    {
        byte[][] others =
        [
            new AuthResponseFrame(
                new Guid("99999999-8888-7777-6666-555555555555"),
                "DESKTOP-B",
                Enumerable.Repeat((byte)0xAB, 32).ToArray(),
                SessionPermission.Control,
                Enumerable.Repeat((byte)0xCD, 32).ToArray()).Serialize(),
            new AuthSuccessFrame(
                SessionPermission.Control,
                Enumerable.Repeat((byte)0xAB, 32).ToArray(),
                Enumerable.Repeat((byte)0xCD, 32).ToArray(),
                15000).Serialize(),
            ApprovalPendingFrame.Serialize(),
            AuthenticationFailedFrame.Serialize(),
        ];

        foreach (byte[] payload in others)
        {
            Assert.False(AuthChallengeFrame.TryParse(payload, out AuthChallengeFrame? frame, out string? rejection));
            Assert.Null(frame);
            Assert.Equal(AuthChallengeFrame.RejectWrongType, rejection);
        }
    }

    [Theory]
    // ── 结构性非法 ──
    [InlineData("", AuthChallengeFrame.RejectMalformedJson)]
    [InlineData("   ", AuthChallengeFrame.RejectMalformedJson)]
    [InlineData("not json", AuthChallengeFrame.RejectMalformedJson)]
    [InlineData("{", AuthChallengeFrame.RejectMalformedJson)]
    [InlineData("[1,2,3]", AuthChallengeFrame.RejectMalformedJson)]
    [InlineData("\"auth_challenge\"", AuthChallengeFrame.RejectMalformedJson)]
    [InlineData("null", AuthChallengeFrame.RejectMalformedJson)]
    // UTF-8 BOM / 非法 UTF-8 字节（用 string 造不出，单独测试）
    public void Rejects_Structural_Garbage(string json, string expected)
    {
        Rejects(json, expected);
    }

    /// <summary>非法 UTF-8 与 BOM：不做替换字符兜底、BOM 不被"贴心地"剥掉。</summary>
    [Theory]
    [InlineData(new byte[] { 0x7B, 0x22, 0x74, 0x79, 0x70, 0x65, 0x22, 0x3A, 0x22, 0xFF, 0xFE, 0x22, 0x7D })]
    [InlineData(new byte[] { 0x80, 0x81, 0x82 })]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0x7B, 0x7D })]
    public void Rejects_Invalid_Utf8_And_Bom(byte[] payload)
    {
        Assert.False(AuthChallengeFrame.TryParse(payload, out AuthChallengeFrame? frame, out string? rejection));
        Assert.Null(frame);
        Assert.Equal(AuthChallengeFrame.RejectMalformedJson, rejection);
    }

    [Fact]
    public void Rejects_Comment_Nested_And_Trailing_Comma()
    {
        Rejects(Patch("{\"type\"", "{/*x*/\"type\""), AuthChallengeFrame.RejectMalformedJson);
        Rejects(Patch(",\"expiresInMs\":15000}", ",\"expiresInMs\":15000,}"), AuthChallengeFrame.RejectMalformedJson);
    }

    [Fact]
    public void Rejects_Unknown_Field()
    {
        Rejects(ValidChallenge[..^1] + ",\"extra\":1}", AuthChallengeFrame.RejectMalformedJson);
    }

    [Fact]
    public void Rejects_Duplicate_Fields()
    {
        Rejects(
            Patch("\"type\":\"auth_challenge\",", "\"type\":\"auth_challenge\",\"type\":\"auth_success\","),
            AuthChallengeFrame.RejectMalformedJson);
        Rejects(
            Patch(
                $"\"sessionId\":\"{SessionId}\",",
                $"\"sessionId\":\"{SessionId}\",\"sessionId\":\"99999999-8888-7777-6666-555555555555\","),
            AuthChallengeFrame.RejectMalformedJson);
    }

    [Fact]
    public void Rejects_Property_Name_Case_Variants()
    {
        Rejects(Patch("\"type\":", "\"Type\":"), AuthChallengeFrame.RejectMalformedJson);
        Rejects(Patch("\"sessionId\":", "\"SessionId\":"), AuthChallengeFrame.RejectMalformedJson);
        Rejects(Patch("\"expiresInMs\":", "\"expiresinms\":"), AuthChallengeFrame.RejectMalformedJson);
    }

    [Fact]
    public void Rejects_Deep_Nesting_In_Scalar_Field()
    {
        Rejects(Build(sessionId: "[[[[[[1]]]]]]"), AuthChallengeFrame.RejectMalformedJson);
    }

    [Fact]
    public void Rejects_Wrong_Json_Types()
    {
        Rejects(Build(protocol: "\"1\""), AuthChallengeFrame.RejectMalformedJson);
        Rejects(Build(protocol: "1.0"), AuthChallengeFrame.RejectMalformedJson);
        Rejects(Build(protocol: "true"), AuthChallengeFrame.RejectMalformedJson);
        Rejects(Build(sessionId: "[\"" + SessionId + "\"]"), AuthChallengeFrame.RejectMalformedJson);
        Rejects(Build(expiresInMs: "\"15000\""), AuthChallengeFrame.RejectMalformedJson);
        Rejects(Build(expiresInMs: "2147483648"), AuthChallengeFrame.RejectMalformedJson);
    }

    [Fact]
    public void Rejects_Trailing_Data()
    {
        Rejects(ValidChallenge + "{\"x\":1}", AuthChallengeFrame.RejectTrailingData);
        Rejects(ValidChallenge + " x", AuthChallengeFrame.RejectTrailingData);
        Rejects(ValidChallenge + ",", AuthChallengeFrame.RejectTrailingData);
    }

    [Fact]
    public void Rejects_Every_Missing_Field()
    {
        Rejects(ValidChallenge.Replace("\"type\":\"auth_challenge\",", "", StringComparison.Ordinal), AuthChallengeFrame.RejectMissingField);
        Rejects(ValidChallenge.Replace(",\"protocol\":1", "", StringComparison.Ordinal), AuthChallengeFrame.RejectMissingField);
        Rejects(ValidChallenge.Replace($",\"sessionId\":\"{SessionId}\"", "", StringComparison.Ordinal), AuthChallengeFrame.RejectMissingField);
        Rejects(ValidChallenge.Replace($",\"serverDeviceId\":\"{ServerDeviceId}\"", "", StringComparison.Ordinal), AuthChallengeFrame.RejectMissingField);
        Rejects(ValidChallenge.Replace($",\"serverNonce\":\"{ServerNonce}\"", "", StringComparison.Ordinal), AuthChallengeFrame.RejectMissingField);
        Rejects(ValidChallenge.Replace($",\"certSha256\":\"{CertSha256}\"", "", StringComparison.Ordinal), AuthChallengeFrame.RejectMissingField);
        Rejects(ValidChallenge.Replace(",\"expiresInMs\":15000", "", StringComparison.Ordinal), AuthChallengeFrame.RejectMissingField);
        Rejects("{}", AuthChallengeFrame.RejectMissingField);
    }

    [Fact]
    public void Rejects_Null_Values_As_Missing()
    {
        Rejects(Build(type: "null"), AuthChallengeFrame.RejectMissingField);
        Rejects(Build(protocol: "null"), AuthChallengeFrame.RejectMissingField);
        Rejects(Build(sessionId: "null"), AuthChallengeFrame.RejectMissingField);
        Rejects(Build(serverNonce: "null"), AuthChallengeFrame.RejectMissingField);
        Rejects(Build(expiresInMs: "null"), AuthChallengeFrame.RejectMissingField);
    }

    [Fact]
    public void Rejects_Wrong_Type_Value()
    {
        Rejects(Build(type: "\"auth_challenge \""), AuthChallengeFrame.RejectWrongType);
        Rejects(Build(type: "\"Auth_Challenge\""), AuthChallengeFrame.RejectWrongType);
        Rejects(Build(type: "\"auth_response\""), AuthChallengeFrame.RejectWrongType);
        Rejects(Build(type: "\"approval_pending\""), AuthChallengeFrame.RejectWrongType);
    }

    [Fact]
    public void Rejects_Wrong_Protocol()
    {
        Rejects(Build(protocol: "2"), AuthChallengeFrame.RejectWrongProtocol);
        Rejects(Build(protocol: "0"), AuthChallengeFrame.RejectWrongProtocol);
    }

    [Fact]
    public void Rejects_Bad_Session_Id()
    {
        // 大写 hex：TryParseExact("D") 容忍，canonical 判定拒绝
        Rejects(Build(sessionId: "\"0F8FAD5B-D9CB-469F-A165-70867728950E\""), AuthChallengeFrame.RejectBadSessionId);
        Rejects(Build(sessionId: "\"11111111-2222-3333-4444-55555555555\""), AuthChallengeFrame.RejectBadSessionId);
        Rejects(Build(sessionId: "\"11111111222233334444555555555555\""), AuthChallengeFrame.RejectBadSessionId);
        Rejects(Build(sessionId: "\"\""), AuthChallengeFrame.RejectBadSessionId);
    }

    [Fact]
    public void Rejects_Bad_Server_Device_Id()
    {
        Rejects(Build(serverDeviceId: "\"AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE\""), AuthChallengeFrame.RejectBadServerDeviceId);
        Rejects(Build(serverDeviceId: "\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeee\""), AuthChallengeFrame.RejectBadServerDeviceId);
    }

    [Fact]
    public void Rejects_Bad_Server_Nonce()
    {
        // 31 / 33 字节：都是合法 canonical base64，唯有长度判定能挡（两侧邻界）
        Rejects(Build(serverNonce: "\"" + B64(31) + "\""), AuthChallengeFrame.RejectBadServerNonce);
        Rejects(Build(serverNonce: "\"" + B64(33) + "\""), AuthChallengeFrame.RejectBadServerNonce);
        // 非规范（可宽松解码但 round-trip 不等）
        Rejects(Build(serverNonce: "\"AB==\""), AuthChallengeFrame.RejectBadServerNonce);
        // 字母表外 / 空串
        Rejects(Build(serverNonce: "\"not base64!!\""), AuthChallengeFrame.RejectBadServerNonce);
        Rejects(Build(serverNonce: "\"\""), AuthChallengeFrame.RejectBadServerNonce);
    }

    [Fact]
    public void Rejects_Bad_Cert_Sha256()
    {
        // 小写 hex
        Rejects(Build(certSha256: "\"000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f\""), AuthChallengeFrame.RejectBadCertSha256);
        // 31 字节
        Rejects(Build(certSha256: "\"000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E\""), AuthChallengeFrame.RejectBadCertSha256);
        // 非 hex / 空串
        Rejects(Build(certSha256: "\"zz\""), AuthChallengeFrame.RejectBadCertSha256);
        Rejects(Build(certSha256: "\"\""), AuthChallengeFrame.RejectBadCertSha256);
    }

    [Fact]
    public void Rejects_Non_Positive_Expires_In_Ms()
    {
        Rejects(Build(expiresInMs: "0"), AuthChallengeFrame.RejectBadExpiresInMs);
        Rejects(Build(expiresInMs: "-5"), AuthChallengeFrame.RejectBadExpiresInMs);
    }

    /// <summary>拒绝原因必须是短码：不含输入内容，方便直接进日志而不泄漏对端报文。</summary>
    [Fact]
    public void Rejections_Are_Short_Codes_Only()
    {
        foreach (string json in new[]
                 {
                     "not json",
                     Build(type: "\"auth_response\""),
                     Build(protocol: "9"),
                     Build(sessionId: "\"not-a-uuid\""),
                     Build(serverNonce: "\"\""),
                     ValidChallenge + " trailing",
                 })
        {
            Assert.False(AuthChallengeFrame.TryParse(Utf8(json), out _, out string? rejection));
            Assert.NotNull(rejection);
            Assert.DoesNotContain("auth_challenge", rejection!, StringComparison.Ordinal);
            Assert.DoesNotContain("not-a-uuid", rejection!, StringComparison.Ordinal);
            Assert.Equal(rejection, rejection!.Trim());
            Assert.InRange(rejection!.Length, 1, 48);
        }
    }

    /// <summary>构造器（服务端侧）fail loud：长度不对 / 非正值当场抛。</summary>
    [Fact]
    public void Constructor_Fails_Fast_On_Programmer_Error()
    {
        byte[] nonce = Enumerable.Repeat((byte)0xAB, 32).ToArray();
        byte[] cert = Enumerable.Repeat((byte)0xCD, 32).ToArray();

        Assert.Throws<ArgumentException>(() => new AuthChallengeFrame(Guid.NewGuid(), Guid.NewGuid(), nonce[..31], cert, 15000));
        Assert.Throws<ArgumentException>(() => new AuthChallengeFrame(Guid.NewGuid(), Guid.NewGuid(), nonce, cert[..31], 15000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AuthChallengeFrame(Guid.NewGuid(), Guid.NewGuid(), nonce, cert, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AuthChallengeFrame(Guid.NewGuid(), Guid.NewGuid(), nonce, cert, -1));

        // 31 / 32 / 1 是同一套判定的边界：32 必须通过
        _ = new AuthChallengeFrame(Guid.NewGuid(), Guid.NewGuid(), nonce, cert, 1);
    }

    /// <summary>构造时复制输入数组：构造后篡改调用方数组不影响帧。</summary>
    [Fact]
    public void Constructor_Copies_Input_Buffers()
    {
        byte[] nonce = Enumerable.Repeat((byte)0xAB, 32).ToArray();
        byte[] cert = Enumerable.Repeat((byte)0xCD, 32).ToArray();

        AuthChallengeFrame frame = new(Guid.NewGuid(), Guid.NewGuid(), nonce, cert, 15000);

        nonce[0] = 0x00;
        cert[0] = 0x00;

        Assert.Equal(0xAB, frame.ServerNonce.Span[0]);
        Assert.Equal(0xCD, frame.CertificateSha256.Span[0]);
    }
}
