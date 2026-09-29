using System.Text;
using System.Text.Json;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

public sealed class VideoHelloFrameTests
{
    // 来自独立 Python 向量的原始 wire 样本；不由生产 Serialize 生成解析期望。
    private const string SessionId = "00112233-4455-6677-8899-aabbccddeeff";
    private const string Nonce = "ICEiIyQlJicoKSorLC0uLw==";
    private const string Proof = "YCZW6twCP5OfD5IrYxGjYsOukEiogPz0OB5VLxeLejo=";
    private const string ValidHello = """{"type":"channel_hello","channel":"video","protocol":1,"sessionId":"00112233-4455-6677-8899-aabbccddeeff","attachNonce":"ICEiIyQlJicoKSorLC0uLw==","attachProof":"YCZW6twCP5OfD5IrYxGjYsOukEiogPz0OB5VLxeLejo="}""";
    private static readonly string[] Fields = ["type", "channel", "protocol", "sessionId", "attachNonce", "attachProof"];
    private static readonly string[] Values = ["\"channel_hello\"", "\"video\"", "1", "\"" + SessionId + "\"", "\"" + Nonce + "\"", "\"" + Proof + "\""];

    private static string Build(string field, string rawValue) => "{" + string.Join(",",
        Fields.Select((name, i) => $"\"{name}\":{(name == field ? rawValue : Values[i])}")) + "}";

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    private static void Rejects(string json, string expected) => Rejects(Encoding.UTF8.GetBytes(json), expected);

    private static void Rejects(byte[] payload, string expected)
    {
        Assert.False(VideoHelloFrame.TryParse(payload, out VideoHelloFrame? frame, out string? rejection));
        Assert.Null(frame);
        Assert.Equal(expected, rejection);
        Assert.Matches("^video-hello-[a-z-]+$", rejection!);
        Assert.InRange(rejection!.Length, 1, 48);
    }

    [Fact]
    public void Test_Builder_Matches_Independent_Literal()
    {
        Assert.Equal(ValidHello, Build("attachNonce", Quote(Nonce)));
    }

    [Fact]
    public void Parses_Independent_Python_Hello()
    {
        Assert.True(VideoHelloFrame.TryParse(Encoding.UTF8.GetBytes(ValidHello), out VideoHelloFrame? frame, out string? rejection));
        Assert.Null(rejection);
        Assert.NotNull(frame);
        Assert.Equal(new Guid(SessionId), frame.SessionId);
        Assert.Equal(Convert.FromHexString("202122232425262728292A2B2C2D2E2F"), frame.AttachNonce.ToArray());
        Assert.Equal(Convert.FromHexString("602656EADC023F939F0F922B6311A362C3AE9048A880FCF4381E552F178B7A3A"), frame.AttachProof.ToArray());
    }

    [Fact]
    public void Serialize_Produces_Exactly_The_Six_Field_Wire_Sample_And_Round_Trips()
    {
        VideoHelloFrame frame = new(new Guid(SessionId), Convert.FromBase64String(Nonce), Convert.FromBase64String(Proof));
        byte[] payload = frame.Serialize();
        Assert.Equal(Encoding.UTF8.GetBytes(ValidHello), payload);
        Assert.True(VideoHelloFrame.TryParse(payload, out VideoHelloFrame? parsed, out string? rejection));
        Assert.Null(rejection);
        Assert.NotNull(parsed);
        Assert.Equal(frame.SessionId, parsed.SessionId);
        Assert.Equal(frame.AttachNonce.ToArray(), parsed.AttachNonce.ToArray());
        Assert.Equal(frame.AttachProof.ToArray(), parsed.AttachProof.ToArray());
        Assert.Equal(payload, parsed.Serialize());
    }

    [Fact]
    public void Guid_Empty_Is_Allowed_In_Constructor_And_Parser()
    {
        VideoHelloFrame original = new(Guid.Empty, new byte[16], new byte[32]);
        Assert.True(VideoHelloFrame.TryParse(original.Serialize(), out VideoHelloFrame? parsed, out string? rejection));
        Assert.Null(rejection);
        Assert.NotNull(parsed);
        Assert.Equal(Guid.Empty, parsed.SessionId);
        Assert.Equal(new byte[16], parsed.AttachNonce.ToArray());
        Assert.Equal(new byte[32], parsed.AttachProof.ToArray());
    }

    [Fact]
    public void Accepts_Whitespace_Reordered_Fields_And_Single_Escaped_Names()
    {
        string reordered = " \t\r\n{" + string.Join(",", Fields.Select((field, i) => $"\"{field}\":{Values[i]}").Reverse()) + "}\r\n";
        string escaped = ValidHello.Replace("\"type\"", "\"t\\u0079pe\"", StringComparison.Ordinal)
            .Replace("channel_hello", "channel_hell\\u006f", StringComparison.Ordinal);
        foreach (string json in new[] { reordered, escaped })
        {
            Assert.True(VideoHelloFrame.TryParse(Encoding.UTF8.GetBytes(json), out VideoHelloFrame? frame, out string? rejection));
            Assert.Null(rejection);
            Assert.NotNull(frame);
            Assert.Equal(Encoding.UTF8.GetBytes(ValidHello), frame.Serialize());
        }
    }

    [Fact]
    public void Accepts_Standard_Base64_Plus_And_Slash()
    {
        const string hello = """{"type":"channel_hello","channel":"video","protocol":1,"sessionId":"0f8fad5b-d9cb-469f-a165-70867728950e","attachNonce":"8PHy8/T19vf4+fr7/P3+/w==","attachProof":"xr5/MHIeO4TpXZlp7sfk/Hj5UXfKk3Io/69p6VCKNFw="}""";
        Assert.True(VideoHelloFrame.TryParse(Encoding.UTF8.GetBytes(hello), out VideoHelloFrame? frame, out string? rejection));
        Assert.Null(rejection);
        Assert.NotNull(frame);
        Assert.Equal(Convert.FromHexString("F0F1F2F3F4F5F6F7F8F9FAFBFCFDFEFF"), frame.AttachNonce.ToArray());
        Assert.Equal(Convert.FromHexString("C6BE7F30721E3B84E95D9969EEC7E4FC78F95177CA937228FFAF69E9508A345C"), frame.AttachProof.ToArray());
        Assert.True(VideoHelloFrame.TryParse(frame.Serialize(), out VideoHelloFrame? parsed, out rejection));
        Assert.Null(rejection);
        Assert.NotNull(parsed);
        Assert.Equal(frame.AttachNonce.ToArray(), parsed.AttachNonce.ToArray());
        Assert.Equal(frame.AttachProof.ToArray(), parsed.AttachProof.ToArray());
    }

    [Theory]
    [InlineData(4095)]
    [InlineData(4096)]
    public void Payload_Limit_Is_Inclusive_And_Surrounding_Whitespace_Counts(int length)
    {
        byte[] payload = Encoding.UTF8.GetBytes(ValidHello + new string(' ', length - Encoding.UTF8.GetByteCount(ValidHello)));
        Assert.Equal(length, payload.Length);
        Assert.True(VideoHelloFrame.TryParse(payload, out _, out string? rejection));
        Assert.Null(rejection);
    }

    [Theory]
    [InlineData(4097)]
    [InlineData(8192)]
    public void Oversized_Payload_Is_Rejected_Before_Json_Parsing(int length)
    {
        Rejects(ValidHello + new string(' ', length - ValidHello.Length), VideoHelloFrame.RejectPayloadTooLarge);
        Rejects(new byte[length], VideoHelloFrame.RejectPayloadTooLarge);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("[1,2,3]")]
    [InlineData("\"channel_hello\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("1")]
    public void Rejects_Non_Object_And_Malformed_Json(string json)
    {
        Rejects(json, VideoHelloFrame.RejectMalformedJson);
    }

    [Theory]
    [InlineData(" {}")]
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
        Rejects(ValidHello + suffix, VideoHelloFrame.RejectTrailingData);
    }

    [Fact]
    public void Rejects_Comments_Trailing_Comma_Unknown_Fields_And_Deep_Nesting()
    {
        Rejects(ValidHello.Insert(1, "/* comment */"), VideoHelloFrame.RejectMalformedJson);
        Rejects(ValidHello.Insert(1, "// comment\n"), VideoHelloFrame.RejectMalformedJson);
        Rejects(ValidHello[..^1] + ",}", VideoHelloFrame.RejectMalformedJson);
        Rejects(ValidHello[..^1] + ",\"extra\":1}", VideoHelloFrame.RejectMalformedJson);
        Rejects(ValidHello[..^1] + ",\"sessionToken\":\"secret-marker\"}", VideoHelloFrame.RejectMalformedJson);
        Rejects(Build("attachProof", "[[[[[[1]]]]]]"), VideoHelloFrame.RejectMalformedJson);
        Rejects("{}", VideoHelloFrame.RejectMissingField);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("channel")]
    [InlineData("protocol")]
    [InlineData("sessionId")]
    [InlineData("attachNonce")]
    [InlineData("attachProof")]
    public void Rejects_Each_Missing_Or_Null_Field(string field)
    {
        string missing = "{" + string.Join(",", Fields.Select((name, i) => (name, i))
            .Where(item => item.name != field).Select(item => $"\"{item.name}\":{Values[item.i]}")) + "}";
        Rejects(missing, VideoHelloFrame.RejectMissingField);
        Rejects(Build(field, "null"), VideoHelloFrame.RejectMissingField);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("channel")]
    [InlineData("protocol")]
    [InlineData("sessionId")]
    [InlineData("attachNonce")]
    [InlineData("attachProof")]
    public void Rejects_Wrong_Json_Types_For_Every_Field(string field)
    {
        foreach (string value in new[] { "true", "[]", "{}", field == "protocol" ? "\"1\"" : "1" })
        {
            Rejects(Build(field, value), VideoHelloFrame.RejectMalformedJson);
        }
    }

    [Theory]
    [InlineData("type")]
    [InlineData("channel")]
    [InlineData("protocol")]
    [InlineData("sessionId")]
    [InlineData("attachNonce")]
    [InlineData("attachProof")]
    public void Rejects_Each_Duplicate_Including_Escaped_Equivalent_Names(string field)
    {
        string value = Values[Array.IndexOf(Fields, field)];
        string escaped = "\\u" + ((int)field[0]).ToString("x4") + field[1..];
        Rejects(ValidHello[..^1] + $",\"{field}\":{value}}}", VideoHelloFrame.RejectMalformedJson);
        Rejects(ValidHello[..^1] + $",\"{escaped}\":{value}}}", VideoHelloFrame.RejectMalformedJson);
        Rejects($"{{\"{escaped}\":{value}," + ValidHello[1..], VideoHelloFrame.RejectMalformedJson);
        // 相同值和冲突值都不允许后者覆盖前者。
        Rejects(ValidHello[..^1] + $",\"{escaped}\":null}}", VideoHelloFrame.RejectMalformedJson);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("channel")]
    [InlineData("protocol")]
    [InlineData("sessionId")]
    [InlineData("attachNonce")]
    [InlineData("attachProof")]
    public void Rejects_Property_Name_Case_Variants(string field)
    {
        string changed = char.ToUpperInvariant(field[0]) + field[1..];
        Rejects(ValidHello.Replace($"\"{field}\":", $"\"{changed}\":", StringComparison.Ordinal), VideoHelloFrame.RejectMalformedJson);
    }

    [Theory]
    [InlineData("0", VideoHelloFrame.RejectWrongProtocol)]
    [InlineData("2", VideoHelloFrame.RejectWrongProtocol)]
    [InlineData("-1", VideoHelloFrame.RejectWrongProtocol)]
    [InlineData("\"1\"", VideoHelloFrame.RejectMalformedJson)]
    [InlineData("1.0", VideoHelloFrame.RejectMalformedJson)]
    [InlineData("1e0", VideoHelloFrame.RejectMalformedJson)]
    [InlineData("1E+0", VideoHelloFrame.RejectMalformedJson)]
    [InlineData("0.1e1", VideoHelloFrame.RejectMalformedJson)]
    [InlineData("2147483648", VideoHelloFrame.RejectMalformedJson)]
    [InlineData("01", VideoHelloFrame.RejectMalformedJson)]
    [InlineData("+1", VideoHelloFrame.RejectMalformedJson)]
    [InlineData("NaN", VideoHelloFrame.RejectMalformedJson)]
    public void Protocol_Must_Be_Exactly_Integer_One(string value, string expected)
    {
        Rejects(Build("protocol", value), expected);
    }

    [Theory]
    [InlineData("type", "Channel_hello", VideoHelloFrame.RejectWrongType)]
    [InlineData("type", "channel_hello ", VideoHelloFrame.RejectWrongType)]
    [InlineData("type", "auth_success", VideoHelloFrame.RejectWrongType)]
    [InlineData("type", "", VideoHelloFrame.RejectWrongType)]
    [InlineData("channel", "Video", VideoHelloFrame.RejectWrongChannel)]
    [InlineData("channel", "video ", VideoHelloFrame.RejectWrongChannel)]
    [InlineData("channel", "control", VideoHelloFrame.RejectWrongChannel)]
    [InlineData("channel", "", VideoHelloFrame.RejectWrongChannel)]
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
    [InlineData("not-a-guid")]
    [InlineData("")]
    public void Session_Id_Must_Be_Canonical_Lowercase_D(string value)
    {
        Rejects(Build("sessionId", Quote(value)), VideoHelloFrame.RejectBadSessionId);
    }

    [Theory]
    [InlineData("attachNonce", 0)]
    [InlineData("attachNonce", 15)]
    [InlineData("attachNonce", 17)]
    [InlineData("attachNonce", 32)]
    [InlineData("attachProof", 0)]
    [InlineData("attachProof", 16)]
    [InlineData("attachProof", 31)]
    [InlineData("attachProof", 33)]
    public void Canonical_Base64_With_Wrong_Decoded_Length_Is_Rejected(string field, int length)
    {
        string value = Convert.ToBase64String(new byte[length]);
        Rejects(Build(field, Quote(value)), field == "attachNonce" ? VideoHelloFrame.RejectBadAttachNonce : VideoHelloFrame.RejectBadAttachProof);
    }

    [Theory]
    [InlineData("attachNonce", 16)]
    [InlineData("attachProof", 32)]
    public void Rejects_Noncanonical_Base64_Even_When_Decodable_To_The_Right_Length(string field, int length)
    {
        string canonical = Convert.ToBase64String(new byte[length]);
        int lastDataIndex = canonical.IndexOf('=') - 1;
        string badPadBits = canonical[..lastDataIndex] + "B" + canonical[(lastDataIndex + 1)..];
        Assert.Equal(new byte[length], Convert.FromBase64String(badPadBits));
        string expected = field == "attachNonce" ? VideoHelloFrame.RejectBadAttachNonce : VideoHelloFrame.RejectBadAttachProof;
        foreach (string value in new[] { badPadBits, " " + canonical, canonical + " ", canonical.Insert(4, "\r\n\t"), canonical.TrimEnd('='), canonical + "=", "!" + canonical[1..] })
        {
            Rejects(Build(field, Quote(value)), expected);
        }

        string standard = Convert.ToBase64String(Enumerable.Repeat((byte)0xFB, length).ToArray());
        Assert.Contains("+", standard, StringComparison.Ordinal);
        Rejects(Build(field, Quote(standard.Replace('+', '-'))), expected);
        standard = Convert.ToBase64String(Enumerable.Repeat((byte)0xFF, length).ToArray());
        Assert.Contains("/", standard, StringComparison.Ordinal);
        Rejects(Build(field, Quote(standard.Replace('/', '_'))), expected);
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
            byte[] prefix = Encoding.UTF8.GetBytes(Build(field, "\"marker\"").Split("marker", StringSplitOptions.None)[0]);
            byte[] suffix = Encoding.UTF8.GetBytes(Build(field, "\"marker\"").Split("marker", StringSplitOptions.None)[1]);
            Rejects([.. prefix, .. invalid, .. suffix], VideoHelloFrame.RejectMalformedJson);
        }

        Rejects([.. "{\""u8.ToArray(), .. invalid, .. "\":1}"u8.ToArray()], VideoHelloFrame.RejectMalformedJson);
    }

    [Fact]
    public void Rejects_Bom_And_Unpaired_Surrogate_Escapes()
    {
        Rejects([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(ValidHello)], VideoHelloFrame.RejectMalformedJson);
        foreach (string raw in new[] { "\"\\uD800\"", "\"\\uDC00\"" })
        {
            Rejects(Build("type", raw), VideoHelloFrame.RejectMalformedJson);
            Rejects(Build("attachNonce", raw), VideoHelloFrame.RejectMalformedJson);
            Rejects(Build("attachProof", raw), VideoHelloFrame.RejectMalformedJson);
        }
    }

    [Theory]
    [InlineData(0, 32, "attachNonce")]
    [InlineData(15, 32, "attachNonce")]
    [InlineData(17, 32, "attachNonce")]
    [InlineData(32, 32, "attachNonce")]
    [InlineData(16, 0, "attachProof")]
    [InlineData(16, 31, "attachProof")]
    [InlineData(16, 33, "attachProof")]
    public void Constructor_Rejects_Invalid_Lengths(int nonceLength, int proofLength, string parameter)
    {
        Assert.Equal(parameter, Assert.Throws<ArgumentException>(
            () => new VideoHelloFrame(Guid.Empty, new byte[nonceLength], new byte[proofLength])).ParamName);
    }

    [Fact]
    public void Constructor_Copies_Input_Slices_And_Serialize_Returns_Independent_Arrays()
    {
        byte[] nonce = [0xFF, .. Convert.FromBase64String(Nonce), 0xFF];
        byte[] proof = [0xFF, .. Convert.FromBase64String(Proof), 0xFF];
        byte[] nonceBefore = nonce.ToArray();
        byte[] proofBefore = proof.ToArray();
        VideoHelloFrame frame = new(new Guid(SessionId), nonce.AsSpan(1, 16), proof.AsSpan(1, 32));
        Assert.Equal(nonceBefore, nonce);
        Assert.Equal(proofBefore, proof);
        Array.Clear(nonce);
        Array.Clear(proof);
        Assert.Equal(Convert.FromBase64String(Nonce), frame.AttachNonce.ToArray());
        Assert.Equal(Convert.FromBase64String(Proof), frame.AttachProof.ToArray());
        byte[] first = frame.Serialize();
        byte[] second = frame.Serialize();
        Assert.NotSame(first, second);
        Array.Clear(first);
        Assert.Equal(Encoding.UTF8.GetBytes(ValidHello), second);
        Assert.Equal(second, frame.Serialize());
    }

    [Fact]
    public void Parser_Uses_Only_The_Slice_And_Does_Not_Retain_The_Input_Buffer()
    {
        byte[] wire = Encoding.UTF8.GetBytes(ValidHello);
        byte[] buffer = [0xFF, .. wire, 0xFF];
        Assert.True(VideoHelloFrame.TryParse(buffer.AsSpan(1, wire.Length), out VideoHelloFrame? frame, out string? rejection));
        Assert.Null(rejection);
        Assert.NotNull(frame);
        Assert.Equal(wire, buffer[1..^1]);
        Array.Clear(buffer);
        Assert.Equal(wire, frame.Serialize());
        Assert.Equal(Convert.FromBase64String(Nonce), frame.AttachNonce.ToArray());
        Assert.Equal(Convert.FromBase64String(Proof), frame.AttachProof.ToArray());
    }

    [Fact]
    public void Existing_Control_Hello_And_Video_Hello_Do_Not_Cross_Accept()
    {
        Rejects(HelloFrame.Serialize(), VideoHelloFrame.RejectMissingField);
        Assert.False(HelloFrame.TryParse(Encoding.UTF8.GetBytes(ValidHello), out _));
    }
}
