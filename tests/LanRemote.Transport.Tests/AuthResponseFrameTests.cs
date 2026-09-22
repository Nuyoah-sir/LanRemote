using System.Text;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

/// <summary>
/// <see cref="AuthResponseFrame"/> 的严格解析：M4 阶段 2 步骤 10 的验收面。
/// </summary>
/// <remarks>
/// <para>clientName 的判定是本地显示面硬化（非规格常量）：非空 / ≤64 / 无控制字符 /
/// UTF-16 良构。名字最终会出现在被控端本机审批面上。</para>
/// </remarks>
public sealed class AuthResponseFrameTests
{
    private const string ClientDeviceId = "99999999-8888-7777-6666-555555555555";
    private const string ClientNonce = "ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8=";
    private const string ClientProof = "qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqo=";

    private static readonly byte[] NonceBytes = Enumerable.Range(0x20, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] ProofBytes = Enumerable.Repeat((byte)0xAA, 32).ToArray();

    /// <summary>规格样本形状的合法 response（字段顺序照规格 04 §9）。</summary>
    private const string ValidResponse =
        """{"type":"auth_response","clientDeviceId":"99999999-8888-7777-6666-555555555555","clientName":"DESKTOP-B","clientNonce":"ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8=","requestedPermission":"control","clientProof":"qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqo="}""";

    private static string Build(
        string type = "\"auth_response\"",
        string clientDeviceId = "\"" + ClientDeviceId + "\"",
        string clientName = "\"DESKTOP-B\"",
        string clientNonce = "\"" + ClientNonce + "\"",
        string requestedPermission = "\"control\"",
        string clientProof = "\"" + ClientProof + "\"")
        => $"{{\"type\":{type},\"clientDeviceId\":{clientDeviceId},\"clientName\":{clientName}," +
           $"\"clientNonce\":{clientNonce},\"requestedPermission\":{requestedPermission}," +
           $"\"clientProof\":{clientProof}}}";

    private static string Patch(string from, string to) => ValidResponse.Replace(from, to, StringComparison.Ordinal);

    private static byte[] Utf8(string json) => Encoding.UTF8.GetBytes(json);

    /// <summary>造「N 字节 0xAA」的 canonical base64（BCL 编码器；防手抄长字面量出错，见 challenge 测试的同名注释）。</summary>
    private static string B64(int byteCount) =>
        Convert.ToBase64String(Enumerable.Repeat((byte)0xAA, byteCount).ToArray());

    private static void Rejects(string json, string expected)
    {
        Assert.False(AuthResponseFrame.TryParse(Utf8(json), out AuthResponseFrame? frame, out string? rejection), $"本该被拒却通过了：{json}");
        Assert.Null(frame);
        Assert.Equal(expected, rejection);
    }

    [Fact]
    public void Build_Helper_Matches_The_Handwritten_Literal()
    {
        Assert.Equal(ValidResponse, Build());
    }

    [Fact]
    public void Accepts_The_Spec_Shaped_Response()
    {
        Assert.True(AuthResponseFrame.TryParse(Utf8(ValidResponse), out AuthResponseFrame? frame, out string? rejection));
        Assert.Null(rejection);
        Assert.NotNull(frame);

        Assert.Equal(new Guid(ClientDeviceId), frame!.ClientDeviceId);
        Assert.Equal("DESKTOP-B", frame.ClientName);
        Assert.Equal(NonceBytes, frame.ClientNonce.ToArray());
        Assert.Equal(SessionPermission.Control, frame.RequestedPermission);
        Assert.Equal(ProofBytes, frame.ClientProof.ToArray());
    }

    [Fact]
    public void Accepts_View_Permission()
    {
        Assert.True(AuthResponseFrame.TryParse(Utf8(Build(requestedPermission: "\"view\"")), out AuthResponseFrame? frame, out _));
        Assert.Equal(SessionPermission.ViewOnly, frame!.RequestedPermission);
    }

    [Fact]
    public void Allows_Surrounding_Whitespace()
    {
        Assert.True(AuthResponseFrame.TryParse(Utf8("\t" + ValidResponse + "\n"), out _, out string? rejection));
        Assert.Null(rejection);
    }

    [Fact]
    public void Serialize_Round_Trips_Through_The_Same_Strict_Parser()
    {
        AuthResponseFrame original = new(
            new Guid(ClientDeviceId), "DESKTOP-B", NonceBytes, SessionPermission.Control, ProofBytes);

        byte[] payload = original.Serialize();

        Assert.True(
            AuthResponseFrame.TryParse(payload, out AuthResponseFrame? parsed, out string? rejection),
            Encoding.UTF8.GetString(payload));
        Assert.Null(rejection);
        Assert.NotNull(parsed);

        Assert.Equal(original.ClientDeviceId, parsed!.ClientDeviceId);
        Assert.Equal(original.ClientName, parsed.ClientName);
        Assert.Equal(original.ClientNonce.ToArray(), parsed.ClientNonce.ToArray());
        Assert.Equal(original.RequestedPermission, parsed.RequestedPermission);
        Assert.Equal(original.ClientProof.ToArray(), parsed.ClientProof.ToArray());
    }

    [Fact]
    public void Serialize_Is_Byte_Stable()
    {
        AuthResponseFrame frame = new(
            Guid.NewGuid(), "X", NonceBytes, SessionPermission.ViewOnly, ProofBytes);

        Assert.Equal(frame.Serialize(), frame.Serialize());
    }

    /// <summary>交叉类型：其它帧（真实序列化字节）喂给 response 解析器 → 一律 wrong-type。</summary>
    [Fact]
    public void Does_Not_Cross_Accept_Other_Frame_Payloads()
    {
        byte[][] others =
        [
            new AuthChallengeFrame(Guid.NewGuid(), Guid.NewGuid(), NonceBytes, ProofBytes, 15000).Serialize(),
            new AuthSuccessFrame(SessionPermission.Control, ProofBytes, ProofBytes, 15000).Serialize(),
            ApprovalPendingFrame.Serialize(),
            AuthenticationFailedFrame.Serialize(),
        ];

        foreach (byte[] payload in others)
        {
            Assert.False(AuthResponseFrame.TryParse(payload, out AuthResponseFrame? frame, out string? rejection));
            Assert.Null(frame);
            Assert.Equal(AuthResponseFrame.RejectWrongType, rejection);
        }
    }

    [Theory]
    [InlineData("", AuthResponseFrame.RejectMalformedJson)]
    [InlineData("   ", AuthResponseFrame.RejectMalformedJson)]
    [InlineData("not json", AuthResponseFrame.RejectMalformedJson)]
    [InlineData("{", AuthResponseFrame.RejectMalformedJson)]
    [InlineData("[1,2,3]", AuthResponseFrame.RejectMalformedJson)]
    [InlineData("\"auth_response\"", AuthResponseFrame.RejectMalformedJson)]
    [InlineData("null", AuthResponseFrame.RejectMalformedJson)]
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
        Assert.False(AuthResponseFrame.TryParse(payload, out AuthResponseFrame? frame, out string? rejection));
        Assert.Null(frame);
        Assert.Equal(AuthResponseFrame.RejectMalformedJson, rejection);
    }

    [Fact]
    public void Rejects_Comment_Nested_And_Trailing_Comma()
    {
        Rejects(Patch("{\"type\"", "{/*x*/\"type\""), AuthResponseFrame.RejectMalformedJson);
        Rejects(Patch(",\"clientProof\":\"" + ClientProof + "\"}", ",\"clientProof\":\"" + ClientProof + "\",}"), AuthResponseFrame.RejectMalformedJson);
    }

    [Fact]
    public void Rejects_Unknown_Field()
    {
        Rejects(ValidResponse[..^1] + ",\"extra\":1}", AuthResponseFrame.RejectMalformedJson);
    }

    [Fact]
    public void Rejects_Duplicate_Fields()
    {
        Rejects(
            Patch("\"type\":\"auth_response\",", "\"type\":\"auth_response\",\"type\":\"auth_challenge\","),
            AuthResponseFrame.RejectMalformedJson);
        Rejects(
            Patch("\"clientName\":\"DESKTOP-B\",", "\"clientName\":\"DESKTOP-B\",\"clientName\":\"X\","),
            AuthResponseFrame.RejectMalformedJson);
    }

    [Fact]
    public void Rejects_Property_Name_Case_Variants()
    {
        Rejects(Patch("\"type\":", "\"Type\":"), AuthResponseFrame.RejectMalformedJson);
        Rejects(Patch("\"clientName\":", "\"ClientName\":"), AuthResponseFrame.RejectMalformedJson);
        Rejects(Patch("\"requestedPermission\":", "\"requestedpermission\":"), AuthResponseFrame.RejectMalformedJson);
    }

    [Fact]
    public void Rejects_Wrong_Json_Types()
    {
        Rejects(Build(clientName: "123"), AuthResponseFrame.RejectMalformedJson);
        Rejects(Build(clientName: "[\"DESKTOP-B\"]"), AuthResponseFrame.RejectMalformedJson);
        Rejects(Build(clientName: "[[[[[[1]]]]]]"), AuthResponseFrame.RejectMalformedJson);
        Rejects(Build(clientNonce: "5"), AuthResponseFrame.RejectMalformedJson);
        Rejects(Build(requestedPermission: "1"), AuthResponseFrame.RejectMalformedJson);
    }

    [Fact]
    public void Rejects_Trailing_Data()
    {
        Rejects(ValidResponse + "{\"x\":1}", AuthResponseFrame.RejectTrailingData);
        Rejects(ValidResponse + " x", AuthResponseFrame.RejectTrailingData);
        Rejects(ValidResponse + ",", AuthResponseFrame.RejectTrailingData);
    }

    [Fact]
    public void Rejects_Every_Missing_Field()
    {
        Rejects(ValidResponse.Replace("\"type\":\"auth_response\",", "", StringComparison.Ordinal), AuthResponseFrame.RejectMissingField);
        Rejects(ValidResponse.Replace($",\"clientDeviceId\":\"{ClientDeviceId}\"", "", StringComparison.Ordinal), AuthResponseFrame.RejectMissingField);
        Rejects(ValidResponse.Replace(",\"clientName\":\"DESKTOP-B\"", "", StringComparison.Ordinal), AuthResponseFrame.RejectMissingField);
        Rejects(ValidResponse.Replace($",\"clientNonce\":\"{ClientNonce}\"", "", StringComparison.Ordinal), AuthResponseFrame.RejectMissingField);
        Rejects(ValidResponse.Replace(",\"requestedPermission\":\"control\"", "", StringComparison.Ordinal), AuthResponseFrame.RejectMissingField);
        Rejects(ValidResponse.Replace($",\"clientProof\":\"{ClientProof}\"", "", StringComparison.Ordinal), AuthResponseFrame.RejectMissingField);
        Rejects("{}", AuthResponseFrame.RejectMissingField);
    }

    [Fact]
    public void Rejects_Null_Values_As_Missing()
    {
        Rejects(Build(type: "null"), AuthResponseFrame.RejectMissingField);
        Rejects(Build(clientName: "null"), AuthResponseFrame.RejectMissingField);
        Rejects(Build(clientProof: "null"), AuthResponseFrame.RejectMissingField);
    }

    [Fact]
    public void Rejects_Wrong_Type_Value()
    {
        Rejects(Build(type: "\"auth_response \""), AuthResponseFrame.RejectWrongType);
        Rejects(Build(type: "\"Auth_Response\""), AuthResponseFrame.RejectWrongType);
        Rejects(Build(type: "\"auth_challenge\""), AuthResponseFrame.RejectWrongType);
        // M3 的 hello 帧也必须被精确拒绝（联调时最容易发错的帧）
        Rejects("""{"type":"channel_hello","channel":"control","protocol":1}""", AuthResponseFrame.RejectWrongType);
    }

    [Fact]
    public void Rejects_Bad_Client_Device_Id()
    {
        // 大写 hex：TryParseExact("D") 容忍，canonical 判定拒绝
        Rejects(Build(clientDeviceId: "\"0F8FAD5B-D9CB-469F-A165-70867728950E\""), AuthResponseFrame.RejectBadClientDeviceId);
        Rejects(Build(clientDeviceId: "\"99999999888877776666555555555555\""), AuthResponseFrame.RejectBadClientDeviceId);
        Rejects(Build(clientDeviceId: "\"\""), AuthResponseFrame.RejectBadClientDeviceId);
    }

    [Fact]
    public void Rejects_Bad_Client_Name()
    {
        Rejects(Build(clientName: "\"\""), AuthResponseFrame.RejectBadClientName);
        Rejects(Build(clientName: "\"" + new string('A', 65) + "\""), AuthResponseFrame.RejectBadClientName);
        // 换行经 JSON 转义进入（原始字符串里的 \n 是字面反斜杠+n，正好构成 JSON 转义序列）
        Rejects(Build(clientName: "\"A\\nB\""), AuthResponseFrame.RejectBadClientName);
        Rejects(Build(clientName: "\"A\\u0000B\""), AuthResponseFrame.RejectBadClientName);
        // 孤立代理码元：System.Text.Json 在解码 `\uD800` 时直接抛 JsonException（实测 2026-09-22）
        // → 报 malformed 而非 bad-name。本层判定仍保留代理对检查，用于构造器路径（见下）。
        Rejects(Build(clientName: "\"A\\uD800B\""), AuthResponseFrame.RejectMalformedJson);
    }

    [Fact]
    public void Accepts_Boundary_And_Unicode_Client_Names()
    {
        // 恰好 64 字符：上限之内
        Assert.True(AuthResponseFrame.TryParse(Utf8(Build(clientName: "\"" + new string('A', 64) + "\"")), out _, out _));
        // BMP Unicode
        Assert.True(AuthResponseFrame.TryParse(Utf8(Build(clientName: "\"办公室电脑\"")), out _, out _));
        // 非 BMP（代理对，UTF-16 良构）
        Assert.True(AuthResponseFrame.TryParse(Utf8(Build(clientName: "\"PC \U0001F5A5\"")), out _, out _));
    }

    [Fact]
    public void Rejects_Bad_Client_Nonce()
    {
        Rejects(Build(clientNonce: "\"" + B64(31) + "\""), AuthResponseFrame.RejectBadClientNonce);
        Rejects(Build(clientNonce: "\"" + B64(33) + "\""), AuthResponseFrame.RejectBadClientNonce);
        Rejects(Build(clientNonce: "\"AB==\""), AuthResponseFrame.RejectBadClientNonce);
        Rejects(Build(clientNonce: "\"\""), AuthResponseFrame.RejectBadClientNonce);
    }

    [Fact]
    public void Rejects_Bad_Requested_Permission()
    {
        Rejects(Build(requestedPermission: "\"Control\""), AuthResponseFrame.RejectBadRequestedPermission);
        Rejects(Build(requestedPermission: "\"admin\""), AuthResponseFrame.RejectBadRequestedPermission);
        Rejects(Build(requestedPermission: "\"\""), AuthResponseFrame.RejectBadRequestedPermission);
        Rejects(Build(requestedPermission: "\"view \""), AuthResponseFrame.RejectBadRequestedPermission);
        Rejects(Build(requestedPermission: "\"VIEW\""), AuthResponseFrame.RejectBadRequestedPermission);
    }

    [Fact]
    public void Rejects_Bad_Client_Proof()
    {
        Rejects(Build(clientProof: "\"" + B64(31) + "\""), AuthResponseFrame.RejectBadClientProof);
        Rejects(Build(clientProof: "\"" + B64(33) + "\""), AuthResponseFrame.RejectBadClientProof);
        Rejects(Build(clientProof: "\"AB==\""), AuthResponseFrame.RejectBadClientProof);
        Rejects(Build(clientProof: "\"\""), AuthResponseFrame.RejectBadClientProof);
    }

    /// <summary>拒绝原因必须是短码：不含输入内容。</summary>
    [Fact]
    public void Rejections_Are_Short_Codes_Only()
    {
        foreach (string json in new[]
                 {
                     "not json",
                     Build(type: "\"auth_challenge\""),
                     Build(clientName: "\"\""),
                     Build(clientNonce: "\"\""),
                     ValidResponse + " trailing",
                 })
        {
            Assert.False(AuthResponseFrame.TryParse(Utf8(json), out _, out string? rejection));
            Assert.NotNull(rejection);
            Assert.DoesNotContain("auth_response", rejection!, StringComparison.Ordinal);
            Assert.DoesNotContain("DESKTOP-B", rejection!, StringComparison.Ordinal);
            Assert.Equal(rejection, rejection!.Trim());
            Assert.InRange(rejection!.Length, 1, 48);
        }
    }

    [Fact]
    public void Constructor_Fails_Fast_On_Programmer_Error()
    {
        Assert.Throws<ArgumentNullException>(() => new AuthResponseFrame(Guid.NewGuid(), null!, NonceBytes, SessionPermission.ViewOnly, ProofBytes));
        Assert.Throws<ArgumentException>(() => new AuthResponseFrame(Guid.NewGuid(), "", NonceBytes, SessionPermission.ViewOnly, ProofBytes));
        Assert.Throws<ArgumentException>(() => new AuthResponseFrame(Guid.NewGuid(), new string('A', 65), NonceBytes, SessionPermission.ViewOnly, ProofBytes));
        Assert.Throws<ArgumentException>(() => new AuthResponseFrame(Guid.NewGuid(), "A\nB", NonceBytes, SessionPermission.ViewOnly, ProofBytes));
        // 孤立代理（C# 字符串级不良构；wire 侧根本到不了这里——S.T.J 解码时就抛）
        Assert.Throws<ArgumentException>(() => new AuthResponseFrame(Guid.NewGuid(), "A\uD800B", NonceBytes, SessionPermission.ViewOnly, ProofBytes));
        Assert.Throws<ArgumentException>(() => new AuthResponseFrame(Guid.NewGuid(), "X", NonceBytes[..31], SessionPermission.ViewOnly, ProofBytes));
        Assert.Throws<ArgumentException>(() => new AuthResponseFrame(Guid.NewGuid(), "X", NonceBytes, SessionPermission.ViewOnly, ProofBytes[..31]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AuthResponseFrame(Guid.NewGuid(), "X", NonceBytes, (SessionPermission)7, ProofBytes));

        _ = new AuthResponseFrame(Guid.NewGuid(), new string('A', 64), NonceBytes, SessionPermission.Control, ProofBytes);
    }

    [Fact]
    public void Constructor_Copies_Input_Buffers()
    {
        byte[] nonce = NonceBytes.ToArray();
        byte[] proof = ProofBytes.ToArray();

        AuthResponseFrame frame = new(Guid.NewGuid(), "X", nonce, SessionPermission.Control, proof);

        nonce[0] = 0x00;
        proof[0] = 0x00;

        Assert.Equal(0x20, frame.ClientNonce.Span[0]);
        Assert.Equal(0xAA, frame.ClientProof.Span[0]);
    }
}
