using System.Text;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

/// <summary>
/// <see cref="AuthenticationFailedFrame"/> 的严格解析：纯标志帧；语义为空是有意的
/// （规格 04 §9「不区分细节给远端」）。
/// </summary>
public sealed class AuthenticationFailedFrameTests
{
    private const string ValidFailed = """{"type":"authentication_failed"}""";

    private static byte[] Utf8(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public void Accepts_The_Exact_Frame_And_Only_That()
    {
        Assert.True(AuthenticationFailedFrame.TryParse(Utf8(ValidFailed), out string? rejection));
        Assert.Null(rejection);
    }

    [Fact]
    public void Allows_Surrounding_Whitespace()
    {
        Assert.True(AuthenticationFailedFrame.TryParse(Utf8(" " + ValidFailed + "\t"), out string? rejection));
        Assert.Null(rejection);
    }

    [Fact]
    public void Serialize_Is_Byte_Stable_And_Exact()
    {
        byte[] first = AuthenticationFailedFrame.Serialize();
        byte[] second = AuthenticationFailedFrame.Serialize();

        Assert.Equal(first, second);
        Assert.Equal(ValidFailed, Encoding.UTF8.GetString(first));
    }

    [Fact]
    public void Serialize_Round_Trips_Through_The_Same_Strict_Parser()
    {
        byte[] payload = AuthenticationFailedFrame.Serialize();

        Assert.True(AuthenticationFailedFrame.TryParse(payload, out string? rejection), Encoding.UTF8.GetString(payload));
        Assert.Null(rejection);
    }

    /// <summary>交叉类型：其它帧（真实序列化字节）喂给本解析器 → 一律 wrong-type。</summary>
    [Fact]
    public void Does_Not_Cross_Accept_Other_Frame_Payloads()
    {
        byte[][] others =
        [
            new AuthChallengeFrame(Guid.NewGuid(), Guid.NewGuid(), new byte[32], new byte[32], 15000).Serialize(),
            new AuthResponseFrame(Guid.NewGuid(), "X", new byte[32], SessionPermission.Control, new byte[32]).Serialize(),
            new AuthSuccessFrame(SessionPermission.Control, new byte[32], new byte[32], 15000).Serialize(),
            ApprovalPendingFrame.Serialize(),
        ];

        foreach (byte[] payload in others)
        {
            Assert.False(AuthenticationFailedFrame.TryParse(payload, out string? rejection));
            Assert.Equal(AuthenticationFailedFrame.RejectWrongType, rejection);
        }
    }

    [Theory]
    [InlineData("", AuthenticationFailedFrame.RejectMalformedJson)]
    [InlineData("   ", AuthenticationFailedFrame.RejectMalformedJson)]
    [InlineData("not json", AuthenticationFailedFrame.RejectMalformedJson)]
    [InlineData("{", AuthenticationFailedFrame.RejectMalformedJson)]
    [InlineData("[1,2,3]", AuthenticationFailedFrame.RejectMalformedJson)]
    [InlineData("\"authentication_failed\"", AuthenticationFailedFrame.RejectMalformedJson)]
    [InlineData("null", AuthenticationFailedFrame.RejectMalformedJson)]
    // 注释与尾逗号
    [InlineData("""{"type":"authentication_failed",/*x*/}""", AuthenticationFailedFrame.RejectMalformedJson)]
    [InlineData("""{"type":"authentication_failed",}""", AuthenticationFailedFrame.RejectMalformedJson)]
    // 未知字段（原因码绝不允许出现在线上）
    [InlineData("""{"type":"authentication_failed","reason":"wrong-key"}""", AuthenticationFailedFrame.RejectMalformedJson)]
    [InlineData("""{"type":"authentication_failed","code":7}""", AuthenticationFailedFrame.RejectMalformedJson)]
    // 重复字段
    [InlineData("""{"type":"authentication_failed","type":"auth_success"}""", AuthenticationFailedFrame.RejectMalformedJson)]
    // 属性名大小写
    [InlineData("""{"Type":"authentication_failed"}""", AuthenticationFailedFrame.RejectMalformedJson)]
    // type 的 JSON 类型错了
    [InlineData("""{"type":1}""", AuthenticationFailedFrame.RejectMalformedJson)]
    // 尾随内容
    [InlineData("""{"type":"authentication_failed"}{"x":1}""", AuthenticationFailedFrame.RejectTrailingData)]
    [InlineData("""{"type":"authentication_failed"} x""", AuthenticationFailedFrame.RejectTrailingData)]
    // 缺字段
    [InlineData("{}", AuthenticationFailedFrame.RejectMissingField)]
    [InlineData("""{"type":null}""", AuthenticationFailedFrame.RejectMissingField)]
    // 值不对
    [InlineData("""{"type":"authentication_failed "}""", AuthenticationFailedFrame.RejectWrongType)]
    [InlineData("""{"type":"Authentication_Failed"}""", AuthenticationFailedFrame.RejectWrongType)]
    [InlineData("""{"type":"approval_pending"}""", AuthenticationFailedFrame.RejectWrongType)]
    [InlineData("""{"type":"channel_hello","channel":"control","protocol":1}""", AuthenticationFailedFrame.RejectWrongType)]
    public void Rejects_Everything_That_Is_Not_Exactly_The_Frame(string json, string expected)
    {
        Assert.False(AuthenticationFailedFrame.TryParse(Utf8(json), out string? rejection), $"本该被拒却通过了：{json}");
        Assert.Equal(expected, rejection);
    }

    [Theory]
    [InlineData(new byte[] { 0x7B, 0x22, 0x74, 0x79, 0x70, 0x65, 0x22, 0x3A, 0x22, 0xFF, 0xFE, 0x22, 0x7D })]
    [InlineData(new byte[] { 0x80, 0x81, 0x82 })]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0x7B, 0x7D })]
    public void Rejects_Invalid_Utf8_And_Bom(byte[] payload)
    {
        Assert.False(AuthenticationFailedFrame.TryParse(payload, out string? rejection));
        Assert.Equal(AuthenticationFailedFrame.RejectMalformedJson, rejection);
    }

    /// <summary>拒绝原因必须是短码：不含输入内容。</summary>
    [Fact]
    public void Rejections_Are_Short_Codes_Only()
    {
        foreach (string json in new[]
                 {
                     "not json",
                     """{"type":"approval_pending"}""",
                     """{"type":"authentication_failed","reason":"wrong-key"}""",
                     "{}",
                     ValidFailed + " trailing",
                 })
        {
            Assert.False(AuthenticationFailedFrame.TryParse(Utf8(json), out string? rejection));
            Assert.NotNull(rejection);
            Assert.DoesNotContain("authentication_failed", rejection!, StringComparison.Ordinal);
            Assert.DoesNotContain("wrong-key", rejection!, StringComparison.Ordinal);
            Assert.Equal(rejection, rejection!.Trim());
            Assert.InRange(rejection!.Length, 1, 48);
        }
    }
}
