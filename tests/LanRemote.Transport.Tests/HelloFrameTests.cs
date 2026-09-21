using System.Text;

namespace LanRemote.Transport.Tests;

/// <summary>
/// <see cref="HelloFrame"/> 的严格解析：步骤 17～19 的验收面。
/// </summary>
/// <remarks>
/// <para>这一层是<b>认证之前</b>唯一被处理的输入，所以每一项宽松都必须被显式拒绝，
/// 且每一项都要有测试——「能反序列化成功」不算验收通过。</para>
/// <para>拒绝原因只用于本地日志、<b>绝不下发给对端</b>，本测试同时锁死这一点
/// （<c>Rejections_Are_Short_Codes_Only</c>）。</para>
/// </remarks>
public sealed class HelloFrameTests
{
    private const string ValidHello =
        """{"type":"channel_hello","channel":"control","protocol":1}""";

    [Fact]
    public void Accepts_The_Exact_Hello_And_Only_That()
    {
        Assert.True(HelloFrame.TryParse(Encoding.UTF8.GetBytes(ValidHello), out string? rejection));
        Assert.Null(rejection);
    }

    [Fact]
    public void Serialize_Round_Trips_Through_The_Same_Strict_Parser()
    {
        byte[] payload = HelloFrame.Serialize();

        Assert.True(HelloFrame.TryParse(payload, out string? rejection), Encoding.UTF8.GetString(payload));
        Assert.Null(rejection);
    }

    [Theory]
    // 结构性非法
    [InlineData("", HelloFrame.RejectMalformedJson)]
    [InlineData("   ", HelloFrame.RejectMalformedJson)]
    [InlineData("not json", HelloFrame.RejectMalformedJson)]
    [InlineData("{", HelloFrame.RejectMalformedJson)]
    [InlineData("[1,2,3]", HelloFrame.RejectMalformedJson)]
    [InlineData("\"channel_hello\"", HelloFrame.RejectMalformedJson)]
    [InlineData("null", HelloFrame.RejectMalformedJson)]
    // 注释与尾逗号：JSON 的两种"方言"，一律不接受
    [InlineData("""{"type":"channel_hello",/*x*/"channel":"control","protocol":1}""", HelloFrame.RejectMalformedJson)]
    [InlineData("""{"type":"channel_hello","channel":"control","protocol":1,}""", HelloFrame.RejectMalformedJson)]
    // 未知字段
    [InlineData("""{"type":"channel_hello","channel":"control","protocol":1,"extra":1}""", HelloFrame.RejectMalformedJson)]
    // 重复字段：默认的「后者覆盖」会让同一份报文有两种解释
    [InlineData("""{"type":"channel_hello","type":"nope","channel":"control","protocol":1}""", HelloFrame.RejectMalformedJson)]
    [InlineData("""{"type":"channel_hello","channel":"control","protocol":1,"protocol":2}""", HelloFrame.RejectMalformedJson)]
    // 属性名大小写不敏感也是解析面，必须关掉
    [InlineData("""{"Type":"channel_hello","channel":"control","protocol":1}""", HelloFrame.RejectMalformedJson)]
    [InlineData("""{"type":"channel_hello","Channel":"control","protocol":1}""", HelloFrame.RejectMalformedJson)]
    // protocol 的类型：字符串、"1"、小数都不行
    [InlineData("""{"type":"channel_hello","channel":"control","protocol":"1"}""", HelloFrame.RejectMalformedJson)]
    [InlineData("""{"type":"channel_hello","channel":"control","protocol":1.0}""", HelloFrame.RejectMalformedJson)]
    [InlineData("""{"type":"channel_hello","channel":"control","protocol":true}""", HelloFrame.RejectMalformedJson)]
    // 深层嵌套：标量字段里塞结构
    [InlineData("""{"type":[[[[[[1]]]]]],"channel":"control","protocol":1}""", HelloFrame.RejectMalformedJson)]
    // 尾随内容：一个 JSON 值后面不许再有东西
    [InlineData("""{"type":"channel_hello","channel":"control","protocol":1}{"x":1}""", HelloFrame.RejectTrailingData)]
    [InlineData("""{"type":"channel_hello","channel":"control","protocol":1} x""", HelloFrame.RejectTrailingData)]
    [InlineData("""{"type":"channel_hello","channel":"control","protocol":1},""", HelloFrame.RejectTrailingData)]
    // 缺字段
    [InlineData("""{"channel":"control","protocol":1}""", HelloFrame.RejectMissingField)]
    [InlineData("""{"type":"channel_hello","protocol":1}""", HelloFrame.RejectMissingField)]
    [InlineData("""{"type":"channel_hello","channel":"control"}""", HelloFrame.RejectMissingField)]
    [InlineData("""{"type":null,"channel":"control","protocol":1}""", HelloFrame.RejectMissingField)]
    [InlineData("""{"type":"channel_hello","channel":null,"protocol":1}""", HelloFrame.RejectMissingField)]
    [InlineData("""{"type":"channel_hello","channel":"control","protocol":null}""", HelloFrame.RejectMissingField)]
    [InlineData("{}", HelloFrame.RejectMissingField)]
    // 值不对（结构完全合法）
    [InlineData("""{"type":"auth_hello","channel":"control","protocol":1}""", HelloFrame.RejectWrongType)]
    [InlineData("""{"type":"Channel_Hello","channel":"control","protocol":1}""", HelloFrame.RejectWrongType)]
    [InlineData("""{"type":"channel_hello ","channel":"control","protocol":1}""", HelloFrame.RejectWrongType)]
    [InlineData("""{"type":"channel_hello","channel":"video","protocol":1}""", HelloFrame.RejectWrongChannel)]
    [InlineData("""{"type":"channel_hello","channel":"Control","protocol":1}""", HelloFrame.RejectWrongChannel)]
    [InlineData("""{"type":"channel_hello","channel":"control","protocol":2}""", HelloFrame.RejectWrongProtocol)]
    [InlineData("""{"type":"channel_hello","channel":"control","protocol":0}""", HelloFrame.RejectWrongProtocol)]
    public void Rejects_Everything_That_Is_Not_Exactly_The_Hello(string json, string expected)
    {
        Assert.False(
            HelloFrame.TryParse(Encoding.UTF8.GetBytes(json), out string? rejection),
            $"本该被拒却通过了：{json}");

        Assert.Equal(expected, rejection);
    }

    /// <remarks>
    /// ⚠️ 这里的第二组数据必须用<b>普通</b>字符串字面量：
    /// 原始字符串字面量 <c>"""..."""</c> 里的 <c>\n</c> <b>不是转义</b>，
    /// 喂进去的是字面反斜杠 + n，会被当成非法 JSON（我自己先踩了一次）。
    /// </remarks>
    [Theory]
    // 值前后有空白是合法 JSON，允许。
    [InlineData("""  {"type":"channel_hello","channel":"control","protocol":1}  """)]
    [InlineData("\n\t{\"type\":\"channel_hello\",\"channel\":\"control\",\"protocol\":1}\r\n")]
    public void Allows_Surrounding_Whitespace(string json)
    {
        Assert.True(HelloFrame.TryParse(Encoding.UTF8.GetBytes(json), out string? rejection));
        Assert.Null(rejection);
    }

    /// <summary>
    /// 非法 UTF-8 <b>不做替换字符兜底</b>：直接拒。
    /// </summary>
    /// <remarks>
    /// 用替换字符（U+FFFD）"修好"字节流，等于让同一份字节在不同实现下解析出不同结果——
    /// 这正是协议解析面要避免的东西。
    /// </remarks>
    [Theory]
    [InlineData(new byte[] { 0x7B, 0x22, 0x74, 0x79, 0x70, 0x65, 0x22, 0x3A, 0x22, 0xFF, 0xFE, 0x22, 0x7D })]
    [InlineData(new byte[] { 0x80, 0x81, 0x82 })]
    public void Rejects_Invalid_Utf8_Without_Replacement(byte[] payload)
    {
        Assert.False(HelloFrame.TryParse(payload, out string? rejection));
        Assert.Equal(HelloFrame.RejectMalformedJson, rejection);
    }

    /// <summary>
    /// UTF-8 BOM 不许被"贴心地"剥掉。
    /// </summary>
    [Fact]
    public void Rejects_Utf8_Bom()
    {
        byte[] withBom = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes(ValidHello))
            .ToArray();

        Assert.False(HelloFrame.TryParse(withBom, out string? rejection));
        Assert.Equal(HelloFrame.RejectMalformedJson, rejection);
    }

    /// <summary>
    /// 拒绝原因必须是短码：不含输入内容，方便直接进日志而不泄漏对端报文。
    /// </summary>
    [Fact]
    public void Rejections_Are_Short_Codes_Only()
    {
        foreach (string json in new[]
                 {
                     "not json",
                     """{"type":"channel_hello","channel":"video","protocol":1}""",
                     """{"type":"channel_hello","channel":"control","protocol":9}""",
                     """{"channel":"control","protocol":1}""",
                     ValidHello + " trailing",
                 })
        {
            Assert.False(HelloFrame.TryParse(Encoding.UTF8.GetBytes(json), out string? rejection));
            Assert.NotNull(rejection);
            Assert.DoesNotContain("channel_hello", rejection!, StringComparison.Ordinal);
            Assert.DoesNotContain("video", rejection!, StringComparison.Ordinal);
            Assert.Equal(rejection, rejection!.Trim());
            Assert.InRange(rejection!.Length, 1, 48);
        }
    }

    /// <summary>
    /// 序列化出来的东西必须是<b>确定的</b>：同样的字节，M4 才能做 canonical transcript。
    /// </summary>
    [Fact]
    public void Serialize_Is_Byte_Stable()
    {
        byte[] first = HelloFrame.Serialize();
        byte[] second = HelloFrame.Serialize();

        Assert.Equal(first, second);
        Assert.Equal(ValidHello, Encoding.UTF8.GetString(first));
    }
}
