using System.Text;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

/// <summary>
/// <see cref="AuthSuccessFrame"/> 的严格解析：M4 阶段 2 步骤 11 的验收面。
/// </summary>
/// <remarks>
/// <para>客户端必须先验 serverProof 再使用 sessionToken（规格 04 §9）；本层只保证
/// 「字段存在且是 canonical 形状」。验证语义测试在阶段 4。</para>
/// </remarks>
public sealed class AuthSuccessFrameTests
{
    private const string ServerProof = "qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqo=";
    private const string SessionToken = "zc3Nzc3Nzc3Nzc3Nzc3Nzc3Nzc3Nzc3Nzc3Nzc3Nzc0=";

    private static readonly byte[] ProofBytes = Enumerable.Repeat((byte)0xAA, 32).ToArray();
    private static readonly byte[] TokenBytes = Enumerable.Repeat((byte)0xCD, 32).ToArray();

    /// <summary>规格样本形状的合法 success（字段顺序照规格 04 §9）。</summary>
    private const string ValidSuccess =
        """{"type":"auth_success","grantedPermission":"control","serverProof":"qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqo=","sessionToken":"zc3Nzc3Nzc3Nzc3Nzc3Nzc3Nzc3Nzc3Nzc3Nzc3Nzc0=","videoAttachExpiresInMs":15000}""";

    private static string Build(
        string type = "\"auth_success\"",
        string grantedPermission = "\"control\"",
        string serverProof = "\"" + ServerProof + "\"",
        string sessionToken = "\"" + SessionToken + "\"",
        string videoAttachExpiresInMs = "15000")
        => $"{{\"type\":{type},\"grantedPermission\":{grantedPermission}," +
           $"\"serverProof\":{serverProof},\"sessionToken\":{sessionToken}," +
           $"\"videoAttachExpiresInMs\":{videoAttachExpiresInMs}}}";

    private static string Patch(string from, string to) => ValidSuccess.Replace(from, to, StringComparison.Ordinal);

    private static byte[] Utf8(string json) => Encoding.UTF8.GetBytes(json);

    /// <summary>造「N 字节 0xAA」的 canonical base64（BCL 编码器；防手抄长字面量出错，见 challenge 测试的同名注释）。</summary>
    private static string B64(int byteCount) =>
        Convert.ToBase64String(Enumerable.Repeat((byte)0xAA, byteCount).ToArray());

    private static void Rejects(string json, string expected)
    {
        Assert.False(AuthSuccessFrame.TryParse(Utf8(json), out AuthSuccessFrame? frame, out string? rejection), $"本该被拒却通过了：{json}");
        Assert.Null(frame);
        Assert.Equal(expected, rejection);
    }

    [Fact]
    public void Build_Helper_Matches_The_Handwritten_Literal()
    {
        Assert.Equal(ValidSuccess, Build());
    }

    [Fact]
    public void Accepts_The_Spec_Shaped_Success()
    {
        Assert.True(AuthSuccessFrame.TryParse(Utf8(ValidSuccess), out AuthSuccessFrame? frame, out string? rejection));
        Assert.Null(rejection);
        Assert.NotNull(frame);

        Assert.Equal(SessionPermission.Control, frame!.GrantedPermission);
        Assert.Equal(ProofBytes, frame.ServerProof.ToArray());
        Assert.Equal(TokenBytes, frame.SessionToken.ToArray());
        Assert.Equal(15000, frame.VideoAttachExpiresInMs);
    }

    [Fact]
    public void Accepts_View_Permission()
    {
        Assert.True(AuthSuccessFrame.TryParse(Utf8(Build(grantedPermission: "\"view\"")), out AuthSuccessFrame? frame, out _));
        Assert.Equal(SessionPermission.ViewOnly, frame!.GrantedPermission);
    }

    [Fact]
    public void Allows_Surrounding_Whitespace()
    {
        Assert.True(AuthSuccessFrame.TryParse(Utf8("  " + ValidSuccess + "  "), out _, out string? rejection));
        Assert.Null(rejection);
    }

    [Fact]
    public void Serialize_Round_Trips_Through_The_Same_Strict_Parser()
    {
        AuthSuccessFrame original = new(SessionPermission.Control, ProofBytes, TokenBytes, 15000);

        byte[] payload = original.Serialize();

        Assert.True(
            AuthSuccessFrame.TryParse(payload, out AuthSuccessFrame? parsed, out string? rejection),
            Encoding.UTF8.GetString(payload));
        Assert.Null(rejection);
        Assert.NotNull(parsed);

        Assert.Equal(original.GrantedPermission, parsed!.GrantedPermission);
        Assert.Equal(original.ServerProof.ToArray(), parsed.ServerProof.ToArray());
        Assert.Equal(original.SessionToken.ToArray(), parsed.SessionToken.ToArray());
        Assert.Equal(original.VideoAttachExpiresInMs, parsed.VideoAttachExpiresInMs);
    }

    [Fact]
    public void Serialize_Is_Byte_Stable()
    {
        AuthSuccessFrame frame = new(SessionPermission.ViewOnly, ProofBytes, TokenBytes, 1);

        Assert.Equal(frame.Serialize(), frame.Serialize());
    }

    /// <summary>交叉类型：其它帧（真实序列化字节）喂给 success 解析器 → 一律 wrong-type。</summary>
    [Fact]
    public void Does_Not_Cross_Accept_Other_Frame_Payloads()
    {
        byte[][] others =
        [
            new AuthChallengeFrame(Guid.NewGuid(), Guid.NewGuid(), ProofBytes, ProofBytes, 15000).Serialize(),
            new AuthResponseFrame(Guid.NewGuid(), "X", ProofBytes, SessionPermission.Control, ProofBytes).Serialize(),
            ApprovalPendingFrame.Serialize(),
            AuthenticationFailedFrame.Serialize(),
        ];

        foreach (byte[] payload in others)
        {
            Assert.False(AuthSuccessFrame.TryParse(payload, out AuthSuccessFrame? frame, out string? rejection));
            Assert.Null(frame);
            Assert.Equal(AuthSuccessFrame.RejectWrongType, rejection);
        }
    }

    [Theory]
    [InlineData("", AuthSuccessFrame.RejectMalformedJson)]
    [InlineData("   ", AuthSuccessFrame.RejectMalformedJson)]
    [InlineData("not json", AuthSuccessFrame.RejectMalformedJson)]
    [InlineData("{", AuthSuccessFrame.RejectMalformedJson)]
    [InlineData("[1,2,3]", AuthSuccessFrame.RejectMalformedJson)]
    [InlineData("\"auth_success\"", AuthSuccessFrame.RejectMalformedJson)]
    [InlineData("null", AuthSuccessFrame.RejectMalformedJson)]
    public void Rejects_Structural_Garbage(string json, string expected)
    {
        Rejects(json, expected);
    }

    [Theory]
    [InlineData(new byte[] { 0x7B, 0x22, 0x74, 0x79, 0x70, 0x65, 0x22, 0x3A, 0x22, 0xFF, 0xFE, 0x22, 0x7D })]
    [InlineData(new byte[] { 0x80, 0x81, 0x82 })]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0x7B, 0x7D })]
    public void Rejects_Invalid_Utf8_And_Bom(byte[] payload)
    {
        Assert.False(AuthSuccessFrame.TryParse(payload, out AuthSuccessFrame? frame, out string? rejection));
        Assert.Null(frame);
        Assert.Equal(AuthSuccessFrame.RejectMalformedJson, rejection);
    }

    [Fact]
    public void Rejects_Comment_Nested_And_Trailing_Comma()
    {
        Rejects(Patch("{\"type\"", "{/*x*/\"type\""), AuthSuccessFrame.RejectMalformedJson);
        Rejects(Patch(",\"videoAttachExpiresInMs\":15000}", ",\"videoAttachExpiresInMs\":15000,}"), AuthSuccessFrame.RejectMalformedJson);
    }

    [Fact]
    public void Rejects_Unknown_Field()
    {
        Rejects(ValidSuccess[..^1] + ",\"extra\":1}", AuthSuccessFrame.RejectMalformedJson);
    }

    [Fact]
    public void Rejects_Duplicate_Fields()
    {
        Rejects(
            Patch("\"type\":\"auth_success\",", "\"type\":\"auth_success\",\"type\":\"auth_challenge\","),
            AuthSuccessFrame.RejectMalformedJson);
        Rejects(
            Patch("\"grantedPermission\":\"control\",", "\"grantedPermission\":\"control\",\"grantedPermission\":\"view\","),
            AuthSuccessFrame.RejectMalformedJson);
    }

    [Fact]
    public void Rejects_Property_Name_Case_Variants()
    {
        Rejects(Patch("\"type\":", "\"Type\":"), AuthSuccessFrame.RejectMalformedJson);
        Rejects(Patch("\"sessionToken\":", "\"SessionToken\":"), AuthSuccessFrame.RejectMalformedJson);
        Rejects(Patch("\"grantedPermission\":", "\"grantedpermission\":"), AuthSuccessFrame.RejectMalformedJson);
    }

    [Fact]
    public void Rejects_Wrong_Json_Types()
    {
        Rejects(Build(videoAttachExpiresInMs: "\"15000\""), AuthSuccessFrame.RejectMalformedJson);
        Rejects(Build(videoAttachExpiresInMs: "15000.0"), AuthSuccessFrame.RejectMalformedJson);
        Rejects(Build(videoAttachExpiresInMs: "2147483648"), AuthSuccessFrame.RejectMalformedJson);
        Rejects(Build(serverProof: "5"), AuthSuccessFrame.RejectMalformedJson);
    }

    [Fact]
    public void Rejects_Trailing_Data()
    {
        Rejects(ValidSuccess + "{\"x\":1}", AuthSuccessFrame.RejectTrailingData);
        Rejects(ValidSuccess + " x", AuthSuccessFrame.RejectTrailingData);
        Rejects(ValidSuccess + ",", AuthSuccessFrame.RejectTrailingData);
    }

    [Fact]
    public void Rejects_Every_Missing_Field()
    {
        Rejects(ValidSuccess.Replace("\"type\":\"auth_success\",", "", StringComparison.Ordinal), AuthSuccessFrame.RejectMissingField);
        Rejects(ValidSuccess.Replace("\"grantedPermission\":\"control\",", "", StringComparison.Ordinal), AuthSuccessFrame.RejectMissingField);
        Rejects(ValidSuccess.Replace($",\"serverProof\":\"{ServerProof}\"", "", StringComparison.Ordinal), AuthSuccessFrame.RejectMissingField);
        Rejects(ValidSuccess.Replace($",\"sessionToken\":\"{SessionToken}\"", "", StringComparison.Ordinal), AuthSuccessFrame.RejectMissingField);
        Rejects(ValidSuccess.Replace(",\"videoAttachExpiresInMs\":15000", "", StringComparison.Ordinal), AuthSuccessFrame.RejectMissingField);
        Rejects("{}", AuthSuccessFrame.RejectMissingField);
    }

    [Fact]
    public void Rejects_Null_Values_As_Missing()
    {
        Rejects(Build(type: "null"), AuthSuccessFrame.RejectMissingField);
        Rejects(Build(grantedPermission: "null"), AuthSuccessFrame.RejectMissingField);
        Rejects(Build(serverProof: "null"), AuthSuccessFrame.RejectMissingField);
    }

    [Fact]
    public void Rejects_Wrong_Type_Value()
    {
        Rejects(Build(type: "\"auth_success \""), AuthSuccessFrame.RejectWrongType);
        Rejects(Build(type: "\"Auth_Success\""), AuthSuccessFrame.RejectWrongType);
        Rejects(Build(type: "\"auth_challenge\""), AuthSuccessFrame.RejectWrongType);
    }

    [Fact]
    public void Rejects_Bad_Granted_Permission()
    {
        Rejects(Build(grantedPermission: "\"Control\""), AuthSuccessFrame.RejectBadGrantedPermission);
        Rejects(Build(grantedPermission: "\"admin\""), AuthSuccessFrame.RejectBadGrantedPermission);
        Rejects(Build(grantedPermission: "\"\""), AuthSuccessFrame.RejectBadGrantedPermission);
        Rejects(Build(grantedPermission: "\"control \""), AuthSuccessFrame.RejectBadGrantedPermission);
    }

    [Fact]
    public void Rejects_Bad_Server_Proof()
    {
        // 31 / 33 字节：都是合法 canonical base64，唯有长度判定能挡（两侧邻界）
        Rejects(Build(serverProof: "\"" + B64(31) + "\""), AuthSuccessFrame.RejectBadServerProof);
        Rejects(Build(serverProof: "\"" + B64(33) + "\""), AuthSuccessFrame.RejectBadServerProof);
        Rejects(Build(serverProof: "\"AB==\""), AuthSuccessFrame.RejectBadServerProof);
        Rejects(Build(serverProof: "\"\""), AuthSuccessFrame.RejectBadServerProof);
    }

    [Fact]
    public void Rejects_Bad_Session_Token()
    {
        // 31 / 33 字节：都是合法 canonical base64，唯有长度判定能挡（两侧邻界）
        Rejects(Build(sessionToken: "\"" + B64(31) + "\""), AuthSuccessFrame.RejectBadSessionToken);
        Rejects(Build(sessionToken: "\"" + B64(33) + "\""), AuthSuccessFrame.RejectBadSessionToken);
        Rejects(Build(sessionToken: "\"AB==\""), AuthSuccessFrame.RejectBadSessionToken);
        Rejects(Build(sessionToken: "\"\""), AuthSuccessFrame.RejectBadSessionToken);
    }

    [Fact]
    public void Rejects_Non_Positive_Video_Attach_Expires_In_Ms()
    {
        Rejects(Build(videoAttachExpiresInMs: "0"), AuthSuccessFrame.RejectBadVideoAttachExpiresInMs);
        Rejects(Build(videoAttachExpiresInMs: "-1"), AuthSuccessFrame.RejectBadVideoAttachExpiresInMs);
    }

    /// <summary>拒绝原因必须是短码：不含输入内容。</summary>
    [Fact]
    public void Rejections_Are_Short_Codes_Only()
    {
        foreach (string json in new[]
                 {
                     "not json",
                     Build(type: "\"auth_challenge\""),
                     Build(grantedPermission: "\"admin\""),
                     Build(sessionToken: "\"\""),
                     ValidSuccess + " trailing",
                 })
        {
            Assert.False(AuthSuccessFrame.TryParse(Utf8(json), out _, out string? rejection));
            Assert.NotNull(rejection);
            Assert.DoesNotContain("auth_success", rejection!, StringComparison.Ordinal);
            Assert.DoesNotContain("admin", rejection!, StringComparison.Ordinal);
            Assert.Equal(rejection, rejection!.Trim());
            Assert.InRange(rejection!.Length, 1, 48);
        }
    }

    [Fact]
    public void Constructor_Fails_Fast_On_Programmer_Error()
    {
        Assert.Throws<ArgumentException>(() => new AuthSuccessFrame(SessionPermission.ViewOnly, ProofBytes[..31], TokenBytes, 15000));
        Assert.Throws<ArgumentException>(() => new AuthSuccessFrame(SessionPermission.ViewOnly, ProofBytes, TokenBytes[..31], 15000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AuthSuccessFrame(SessionPermission.ViewOnly, ProofBytes, TokenBytes, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AuthSuccessFrame(SessionPermission.ViewOnly, ProofBytes, TokenBytes, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AuthSuccessFrame((SessionPermission)7, ProofBytes, TokenBytes, 15000));

        _ = new AuthSuccessFrame(SessionPermission.Control, ProofBytes, TokenBytes, 1);
    }

    [Fact]
    public void Constructor_Copies_Input_Buffers()
    {
        byte[] proof = ProofBytes.ToArray();
        byte[] token = TokenBytes.ToArray();

        AuthSuccessFrame frame = new(SessionPermission.Control, proof, token, 15000);

        proof[0] = 0x00;
        token[0] = 0x00;

        Assert.Equal(0xAA, frame.ServerProof.Span[0]);
        Assert.Equal(0xCD, frame.SessionToken.Span[0]);
    }
}
