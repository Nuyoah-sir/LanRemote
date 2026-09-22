using System.Text;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

/// <summary>
/// <see cref="ApprovalPendingFrame"/> 的严格解析：纯标志帧（唯一字段 <c>type</c>），
/// 与 <see cref="HelloFrame"/> 同构。
/// </summary>
public sealed class ApprovalPendingFrameTests
{
    private const string ValidApprovalPending = """{"type":"approval_pending"}""";

    private static byte[] Utf8(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public void Accepts_The_Exact_Frame_And_Only_That()
    {
        Assert.True(ApprovalPendingFrame.TryParse(Utf8(ValidApprovalPending), out string? rejection));
        Assert.Null(rejection);
    }

    [Fact]
    public void Allows_Surrounding_Whitespace()
    {
        Assert.True(ApprovalPendingFrame.TryParse(Utf8("\n\t" + ValidApprovalPending + "\r\n"), out string? rejection));
        Assert.Null(rejection);
    }

    [Fact]
    public void Serialize_Is_Byte_Stable_And_Exact()
    {
        byte[] first = ApprovalPendingFrame.Serialize();
        byte[] second = ApprovalPendingFrame.Serialize();

        Assert.Equal(first, second);
        Assert.Equal(ValidApprovalPending, Encoding.UTF8.GetString(first));
    }

    [Fact]
    public void Serialize_Round_Trips_Through_The_Same_Strict_Parser()
    {
        byte[] payload = ApprovalPendingFrame.Serialize();

        Assert.True(ApprovalPendingFrame.TryParse(payload, out string? rejection), Encoding.UTF8.GetString(payload));
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
            AuthenticationFailedFrame.Serialize(),
        ];

        foreach (byte[] payload in others)
        {
            Assert.False(ApprovalPendingFrame.TryParse(payload, out string? rejection));
            Assert.Equal(ApprovalPendingFrame.RejectWrongType, rejection);
        }
    }

    [Theory]
    [InlineData("", ApprovalPendingFrame.RejectMalformedJson)]
    [InlineData("   ", ApprovalPendingFrame.RejectMalformedJson)]
    [InlineData("not json", ApprovalPendingFrame.RejectMalformedJson)]
    [InlineData("{", ApprovalPendingFrame.RejectMalformedJson)]
    [InlineData("[1,2,3]", ApprovalPendingFrame.RejectMalformedJson)]
    [InlineData("\"approval_pending\"", ApprovalPendingFrame.RejectMalformedJson)]
    [InlineData("null", ApprovalPendingFrame.RejectMalformedJson)]
    // 注释与尾逗号
    [InlineData("""{"type":"approval_pending",/*x*/}""", ApprovalPendingFrame.RejectMalformedJson)]
    [InlineData("""{"type":"approval_pending",}""", ApprovalPendingFrame.RejectMalformedJson)]
    // 未知字段（本帧没有第二个合法字段）
    [InlineData("""{"type":"approval_pending","extra":1}""", ApprovalPendingFrame.RejectMalformedJson)]
    // 重复字段
    [InlineData("""{"type":"approval_pending","type":"auth_success"}""", ApprovalPendingFrame.RejectMalformedJson)]
    // 属性名大小写
    [InlineData("""{"Type":"approval_pending"}""", ApprovalPendingFrame.RejectMalformedJson)]
    // type 的 JSON 类型错了
    [InlineData("""{"type":1}""", ApprovalPendingFrame.RejectMalformedJson)]
    [InlineData("""{"type":["approval_pending"]}""", ApprovalPendingFrame.RejectMalformedJson)]
    // 尾随内容
    [InlineData("""{"type":"approval_pending"}{"x":1}""", ApprovalPendingFrame.RejectTrailingData)]
    [InlineData("""{"type":"approval_pending"} x""", ApprovalPendingFrame.RejectTrailingData)]
    [InlineData("""{"type":"approval_pending"},""", ApprovalPendingFrame.RejectTrailingData)]
    // 缺字段
    [InlineData("{}", ApprovalPendingFrame.RejectMissingField)]
    [InlineData("""{"type":null}""", ApprovalPendingFrame.RejectMissingField)]
    // 值不对
    [InlineData("""{"type":"approval_pending "}""", ApprovalPendingFrame.RejectWrongType)]
    [InlineData("""{"type":"Approval_Pending"}""", ApprovalPendingFrame.RejectWrongType)]
    [InlineData("""{"type":"auth_success"}""", ApprovalPendingFrame.RejectWrongType)]
    // M3 的 hello 帧必须被精确拒绝（联调时最容易发错的帧）
    [InlineData("""{"type":"channel_hello","channel":"control","protocol":1}""", ApprovalPendingFrame.RejectWrongType)]
    public void Rejects_Everything_That_Is_Not_Exactly_The_Frame(string json, string expected)
    {
        Assert.False(ApprovalPendingFrame.TryParse(Utf8(json), out string? rejection), $"本该被拒却通过了：{json}");
        Assert.Equal(expected, rejection);
    }

    [Theory]
    [InlineData(new byte[] { 0x7B, 0x22, 0x74, 0x79, 0x70, 0x65, 0x22, 0x3A, 0x22, 0xFF, 0xFE, 0x22, 0x7D })]
    [InlineData(new byte[] { 0x80, 0x81, 0x82 })]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0x7B, 0x7D })]
    public void Rejects_Invalid_Utf8_And_Bom(byte[] payload)
    {
        Assert.False(ApprovalPendingFrame.TryParse(payload, out string? rejection));
        Assert.Equal(ApprovalPendingFrame.RejectMalformedJson, rejection);
    }

    /// <summary>拒绝原因必须是短码：不含输入内容。</summary>
    [Fact]
    public void Rejections_Are_Short_Codes_Only()
    {
        foreach (string json in new[]
                 {
                     "not json",
                     """{"type":"auth_success"}""",
                     """{"type":"approval_pending","extra":1}""",
                     "{}",
                     ValidApprovalPending + " trailing",
                 })
        {
            Assert.False(ApprovalPendingFrame.TryParse(Utf8(json), out string? rejection));
            Assert.NotNull(rejection);
            Assert.DoesNotContain("approval_pending", rejection!, StringComparison.Ordinal);
            Assert.Equal(rejection, rejection!.Trim());
            Assert.InRange(rejection!.Length, 1, 48);
        }
    }
}
