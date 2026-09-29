using System.Reflection;
using System.Text;
using System.Text.Json;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

public sealed class VideoAttachAckFrameTests
{
    // 独立 wire 字面量，不用生产 Serialize 生成解析期望。
    private const string SessionId = "00112233-4455-6677-8899-aabbccddeeff";
    private const string ValidAck = """{"type":"video_attach_ack","channel":"video","protocol":1,"sessionId":"00112233-4455-6677-8899-aabbccddeeff"}""";
    private static readonly string[] Fields = ["type", "channel", "protocol", "sessionId"];
    private static readonly string[] Values = ["\"video_attach_ack\"", "\"video\"", "1", "\"" + SessionId + "\""];

    private static string Build(string field, string rawValue) => "{" + string.Join(",",
        Fields.Select((name, i) => $"\"{name}\":{(name == field ? rawValue : Values[i])}")) + "}";

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    private static void Rejects(string json, string expected) => Rejects(Encoding.UTF8.GetBytes(json), expected);

    private static void Rejects(byte[] payload, string expected)
    {
        Assert.False(VideoAttachAckFrame.TryParse(payload, out VideoAttachAckFrame? frame, out string? rejection));
        Assert.Null(frame);
        Assert.Equal(expected, rejection);
        Assert.Matches("^video-attach-ack-[a-z-]+$", rejection!);
        Assert.InRange(rejection!.Length, 1, 48);
    }

    [Fact]
    public void Test_Builder_Matches_Independent_Literal()
    {
        Assert.Equal(ValidAck, Build("sessionId", Quote(SessionId)));
    }

    [Fact]
    public void Parses_Independent_Ack_Literal()
    {
        Assert.True(VideoAttachAckFrame.TryParse(Encoding.UTF8.GetBytes(ValidAck), out VideoAttachAckFrame? frame, out string? rejection));
        Assert.Null(rejection);
        Assert.NotNull(frame);
        Assert.Equal(new Guid(SessionId), frame.SessionId);
    }

    [Fact]
    public void Serialize_Produces_Exactly_The_Four_Field_Wire_Sample_And_Round_Trips()
    {
        VideoAttachAckFrame frame = new(new Guid("00112233-4455-6677-8899-AABBCCDDEEFF"));
        byte[] payload = frame.Serialize();
        Assert.Equal(Encoding.UTF8.GetBytes(ValidAck), payload);
        Assert.True(VideoAttachAckFrame.TryParse(payload, out VideoAttachAckFrame? parsed, out string? rejection));
        Assert.Null(rejection);
        Assert.NotNull(parsed);
        Assert.Equal(frame.SessionId, parsed.SessionId);
        Assert.Equal(payload, parsed.Serialize());
    }

    [Fact]
    public void Guid_Empty_Is_Allowed_In_Constructor_And_Parser()
    {
        const string emptyAck = """{"type":"video_attach_ack","channel":"video","protocol":1,"sessionId":"00000000-0000-0000-0000-000000000000"}""";
        Assert.Equal(Encoding.UTF8.GetBytes(emptyAck), new VideoAttachAckFrame(Guid.Empty).Serialize());
        Assert.True(VideoAttachAckFrame.TryParse(Encoding.UTF8.GetBytes(emptyAck), out VideoAttachAckFrame? frame, out string? rejection));
        Assert.Null(rejection);
        Assert.NotNull(frame);
        Assert.Equal(Guid.Empty, frame.SessionId);
    }

    [Fact]
    public void Accepts_Whitespace_Reordered_Fields_And_Single_Escaped_Names()
    {
        string reordered = " \t\r\n{" + string.Join(",", Fields.Select((field, i) => $"\"{field}\":{Values[i]}").Reverse()) + "}\r\n";
        string escaped = ValidAck.Replace("\"type\"", "\"t\\u0079pe\"", StringComparison.Ordinal)
            .Replace("video_attach_ack", "video_attach_ac\\u006b", StringComparison.Ordinal);
        foreach (string json in new[] { reordered, escaped })
        {
            Assert.True(VideoAttachAckFrame.TryParse(Encoding.UTF8.GetBytes(json), out VideoAttachAckFrame? frame, out string? rejection));
            Assert.Null(rejection);
            Assert.NotNull(frame);
            Assert.Equal(new Guid(SessionId), frame.SessionId);
            Assert.Equal(Encoding.UTF8.GetBytes(ValidAck), frame.Serialize());
        }
    }

    [Theory]
    [InlineData(4095)]
    [InlineData(4096)]
    public void Payload_Limit_Is_Inclusive_And_Surrounding_Whitespace_Counts(int length)
    {
        byte[] payload = Encoding.UTF8.GetBytes(ValidAck + new string(' ', length - Encoding.UTF8.GetByteCount(ValidAck)));
        Assert.Equal(length, payload.Length);
        Assert.True(VideoAttachAckFrame.TryParse(payload, out VideoAttachAckFrame? frame, out string? rejection));
        Assert.Null(rejection);
        Assert.NotNull(frame);
        Assert.Equal(new Guid(SessionId), frame.SessionId);
    }

    [Theory]
    [InlineData(4097)]
    [InlineData(8192)]
    public void Oversized_Payload_Is_Rejected_Before_Json_Parsing(int length)
    {
        Rejects(ValidAck + new string(' ', length - ValidAck.Length), VideoAttachAckFrame.RejectPayloadTooLarge);
        Rejects(new byte[length], VideoAttachAckFrame.RejectPayloadTooLarge);
    }

    [Fact]
    public void Payload_Limit_Counts_Utf8_Bytes_Not_Characters()
    {
        string json = Build("sessionId", "\"" + new string('\u00e9', 2100) + "\"");
        Assert.True(json.Length < 4096);
        Assert.True(Encoding.UTF8.GetByteCount(json) > 4096);
        Rejects(json, VideoAttachAckFrame.RejectPayloadTooLarge);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("[1,2,3]")]
    [InlineData("\"video_attach_ack\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("1")]
    public void Rejects_Non_Object_And_Malformed_Json(string json)
    {
        Rejects(json, VideoAttachAckFrame.RejectMalformedJson);
    }

    [Theory]
    [InlineData(" {}")]
    [InlineData(" []")]
    [InlineData(" null")]
    [InlineData(" true")]
    [InlineData(" 1")]
    [InlineData(" x")]
    [InlineData(",")]
    [InlineData("\0")]
    [InlineData(" /* comment */")]
    [InlineData("\u00a0")]
    public void Rejects_Trailing_Values_Or_Non_Json_Whitespace(string suffix)
    {
        Rejects(ValidAck + suffix, VideoAttachAckFrame.RejectTrailingData);
    }

    [Fact]
    public void Rejects_Comments_Trailing_Comma_And_Deep_Nesting()
    {
        Rejects(ValidAck.Insert(1, "/* comment */"), VideoAttachAckFrame.RejectMalformedJson);
        Rejects(ValidAck.Insert(1, "// comment\n"), VideoAttachAckFrame.RejectMalformedJson);
        Rejects(ValidAck[..^1] + ",}", VideoAttachAckFrame.RejectMalformedJson);
        Rejects(Build("sessionId", "[[[[[[1]]]]]]"), VideoAttachAckFrame.RejectMalformedJson);
        Rejects("{}", VideoAttachAckFrame.RejectMissingField);
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("attachNonce")]
    [InlineData("attachProof")]
    [InlineData("sessionToken")]
    [InlineData("accepted")]
    public void Rejects_Unknown_Fields(string field)
    {
        Rejects(ValidAck[..^1] + $",\"{field}\":null}}", VideoAttachAckFrame.RejectMalformedJson);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("channel")]
    [InlineData("protocol")]
    [InlineData("sessionId")]
    public void Rejects_Each_Missing_Or_Null_Field(string field)
    {
        string missing = "{" + string.Join(",", Fields.Select((name, i) => (name, i))
            .Where(item => item.name != field).Select(item => $"\"{item.name}\":{Values[item.i]}")) + "}";
        Rejects(missing, VideoAttachAckFrame.RejectMissingField);
        Rejects(Build(field, "null"), VideoAttachAckFrame.RejectMissingField);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("channel")]
    [InlineData("protocol")]
    [InlineData("sessionId")]
    public void Rejects_Wrong_Json_Types_For_Every_Field(string field)
    {
        foreach (string value in new[] { "true", "false", "[]", "{}", field == "protocol" ? "\"1\"" : "1" })
        {
            Rejects(Build(field, value), VideoAttachAckFrame.RejectMalformedJson);
        }
    }

    [Theory]
    [InlineData("type")]
    [InlineData("channel")]
    [InlineData("protocol")]
    [InlineData("sessionId")]
    public void Rejects_Each_Duplicate_Including_Escaped_Equivalent_Names(string field)
    {
        string value = Values[Array.IndexOf(Fields, field)];
        string escaped = "\\u" + ((int)field[0]).ToString("x4") + field[1..];
        Rejects(ValidAck[..^1] + $",\"{field}\":{value}}}", VideoAttachAckFrame.RejectMalformedJson);
        Rejects(ValidAck[..^1] + $",\"{escaped}\":{value}}}", VideoAttachAckFrame.RejectMalformedJson);
        Rejects($"{{\"{escaped}\":{value}," + ValidAck[1..], VideoAttachAckFrame.RejectMalformedJson);
        // 相同值和冲突值都不允许后者覆盖前者。
        Rejects(ValidAck[..^1] + $",\"{escaped}\":null}}", VideoAttachAckFrame.RejectMalformedJson);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("channel")]
    [InlineData("protocol")]
    [InlineData("sessionId")]
    public void Rejects_Property_Name_Case_Variants(string field)
    {
        string changed = char.ToUpperInvariant(field[0]) + field[1..];
        Rejects(ValidAck.Replace($"\"{field}\":", $"\"{changed}\":", StringComparison.Ordinal), VideoAttachAckFrame.RejectMalformedJson);
    }

    [Theory]
    [InlineData("0", VideoAttachAckFrame.RejectWrongProtocol)]
    [InlineData("2", VideoAttachAckFrame.RejectWrongProtocol)]
    [InlineData("-1", VideoAttachAckFrame.RejectWrongProtocol)]
    [InlineData("\"1\"", VideoAttachAckFrame.RejectMalformedJson)]
    [InlineData("1.0", VideoAttachAckFrame.RejectMalformedJson)]
    [InlineData("1e0", VideoAttachAckFrame.RejectMalformedJson)]
    [InlineData("1E+0", VideoAttachAckFrame.RejectMalformedJson)]
    [InlineData("0.1e1", VideoAttachAckFrame.RejectMalformedJson)]
    [InlineData("2147483648", VideoAttachAckFrame.RejectMalformedJson)]
    [InlineData("01", VideoAttachAckFrame.RejectMalformedJson)]
    [InlineData("+1", VideoAttachAckFrame.RejectMalformedJson)]
    [InlineData("NaN", VideoAttachAckFrame.RejectMalformedJson)]
    public void Protocol_Must_Be_Exactly_Integer_One(string value, string expected)
    {
        Rejects(Build("protocol", value), expected);
    }

    [Theory]
    [InlineData("type", "Video_attach_ack", VideoAttachAckFrame.RejectWrongType)]
    [InlineData("type", "video_attach_ack ", VideoAttachAckFrame.RejectWrongType)]
    [InlineData("type", "channel_hello", VideoAttachAckFrame.RejectWrongType)]
    [InlineData("type", "auth_success", VideoAttachAckFrame.RejectWrongType)]
    [InlineData("type", "", VideoAttachAckFrame.RejectWrongType)]
    [InlineData("channel", "Video", VideoAttachAckFrame.RejectWrongChannel)]
    [InlineData("channel", "video ", VideoAttachAckFrame.RejectWrongChannel)]
    [InlineData("channel", "control", VideoAttachAckFrame.RejectWrongChannel)]
    [InlineData("channel", "", VideoAttachAckFrame.RejectWrongChannel)]
    public void Type_And_Channel_Are_Exact_Ordinal_Values(string field, string value, string expected)
    {
        Rejects(Build(field, Quote(value)), expected);
    }

    [Theory]
    [InlineData("00112233-4455-6677-8899-AABBCCDDEEFF")]
    [InlineData("00112233445566778899aabbccddeeff")]
    [InlineData("{00112233-4455-6677-8899-aabbccddeeff}")]
    [InlineData("(00112233-4455-6677-8899-aabbccddeeff)")]
    [InlineData(" 00112233-4455-6677-8899-aabbccddeeff")]
    [InlineData("00112233-4455-6677-8899-aabbccddeeff ")]
    [InlineData("00112233-4455-6677-8899-aabbccddeef")]
    [InlineData("00112233-4455-6677-8899-aabbccddeefg")]
    [InlineData("not-a-guid")]
    [InlineData("")]
    public void Session_Id_Must_Be_Canonical_Lowercase_D(string value)
    {
        Rejects(Build("sessionId", Quote(value)), VideoAttachAckFrame.RejectBadSessionId);
    }

    [Theory]
    [InlineData("FF")]
    [InlineData("80")]
    [InlineData("C080")]
    [InlineData("EDA080")]
    [InlineData("F4908080")]
    [InlineData("F09F")]
    public void Invalid_Utf8_Is_Rejected_In_Values_And_Property_Names(string hex)
    {
        byte[] invalid = Convert.FromHexString(hex);
        foreach (string field in Fields.Where(field => field != "protocol"))
        {
            string[] parts = Build(field, "\"marker\"").Split("marker", StringSplitOptions.None);
            Rejects([.. Encoding.UTF8.GetBytes(parts[0]), .. invalid, .. Encoding.UTF8.GetBytes(parts[1])],
                VideoAttachAckFrame.RejectMalformedJson);
        }

        Rejects([.. "{\""u8.ToArray(), .. invalid, .. "\":1}"u8.ToArray()], VideoAttachAckFrame.RejectMalformedJson);
    }

    [Fact]
    public void Rejects_Bom_And_Unpaired_Surrogate_Escapes()
    {
        Rejects([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(ValidAck)], VideoAttachAckFrame.RejectMalformedJson);
        foreach (string field in Fields.Where(field => field != "protocol"))
        {
            foreach (string raw in new[] { "\"\\uD800\"", "\"\\uDC00\"" })
            {
                Rejects(Build(field, raw), VideoAttachAckFrame.RejectMalformedJson);
            }
        }
    }

    [Fact]
    public void Serialize_Returns_Independent_Arrays()
    {
        VideoAttachAckFrame frame = new(new Guid(SessionId));
        byte[] first = frame.Serialize();
        byte[] second = frame.Serialize();
        Assert.NotSame(first, second);
        Array.Clear(first);
        Assert.Equal(Encoding.UTF8.GetBytes(ValidAck), second);
        Assert.Equal(second, frame.Serialize());
    }

    [Fact]
    public void Parser_Uses_Only_The_Slice_And_Does_Not_Retain_The_Input_Buffer()
    {
        byte[] wire = Encoding.UTF8.GetBytes(ValidAck);
        byte[] buffer = [0xFF, .. wire, 0xFF];
        Assert.True(VideoAttachAckFrame.TryParse(buffer.AsSpan(1, wire.Length), out VideoAttachAckFrame? frame, out string? rejection));
        Assert.Null(rejection);
        Assert.NotNull(frame);
        Assert.Equal(wire, buffer[1..^1]);
        Array.Clear(buffer);
        Assert.Equal(new Guid(SessionId), frame.SessionId);
        Assert.Equal(wire, frame.Serialize());
    }

    [Fact]
    public void Ack_Is_Sealed_And_Not_Publicly_Accessible()
    {
        Type type = typeof(VideoAttachAckFrame);
        Assert.True(type.IsSealed);
        Assert.True(type.IsNotPublic);
        Assert.False(type.IsVisible);
        Assert.DoesNotContain(type, type.Assembly.GetExportedTypes());
        Assert.Empty(type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly));
    }
}
