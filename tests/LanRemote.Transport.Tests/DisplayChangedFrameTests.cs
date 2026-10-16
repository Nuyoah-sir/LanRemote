using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LanRemote.Core.Models;

namespace LanRemote.Transport.Tests;

public sealed class DisplayChangedFrameTests
{
    private const string SessionText = "01234567-89ab-cdef-0123-456789abcdef";
    private const string GoldenPayload = """{"type":"display_changed","protocol":1,"sessionId":"01234567-89ab-cdef-0123-456789abcdef","seq":42,"timestampMs":1700000000000,"requestSequence":7009,"catalogSequence":17,"displayId":255,"firstFrameId":18446744073709551615,"width":1920,"height":1080}""";
    private const string InvalidPayload = "display-changed-invalid-payload";
    private const string InvalidLength = "display-changed-invalid-length";
    private const string InvalidContext = "display-changed-invalid-context";
    private static readonly Guid Session = Guid.Parse(SessionText);
    private static readonly Guid OtherSession = Guid.Parse("fedcba98-7654-3210-fedc-ba9876543210");
    private static readonly (string Name, string Value)[] Fields =
    [
        ("type", "\"display_changed\""), ("protocol", "1"), ("sessionId", "\"" + SessionText + "\""),
        ("seq", "42"), ("timestampMs", "1700000000000"), ("requestSequence", "7009"),
        ("catalogSequence", "17"), ("displayId", "255"), ("firstFrameId", "18446744073709551615"),
        ("width", "1920"), ("height", "1080"),
    ];

    [Fact]
    public void Constants_Match_The_Independent_Success_Contract()
    {
        Assert.Equal("display_changed", DisplayChangedFrame.ExpectedType);
        Assert.Equal(1, DisplayChangedFrame.ExpectedProtocol);
        Assert.Equal(4096, DisplayChangedFrame.MaxPayloadByteLength);
        Assert.Equal(8192, FrameLimits.MaxDimension);
    }

    [Fact]
    public void Parses_Independent_Literal_Without_An_Encoder_Roundtrip()
    {
        DisplayChangedFrame frame = Parse(GoldenPayload);
        Assert.Equal(Session, frame.SessionId);
        Assert.Equal(42L, frame.Sequence);
        Assert.Equal(1700000000000L, frame.TimestampMs);
        Assert.Equal(7009L, frame.RequestSequence);
        Assert.Equal(17L, frame.CatalogSequence);
        Assert.Equal(new DisplayId(255), frame.DisplayId);
        Assert.Equal(ulong.MaxValue, frame.FirstFrameId);
        Assert.Equal(1920, frame.Width);
        Assert.Equal(1080, frame.Height);
    }

    [Fact]
    public void Serializer_Matches_Independent_Literal_And_Exactly_Eleven_Fields()
    {
        var frame = new DisplayChangedFrame(Session, 42, 1700000000000, 7009, 17, new DisplayId(255), ulong.MaxValue, 1920, 1080);
        byte[] payload = frame.Serialize();
        Assert.Equal(Utf8(GoldenPayload), payload);
        Assert.Equal((byte)'{', payload[0]);
        Assert.Equal((byte)'}', payload[^1]);
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;
        Assert.Equal(new[] { "type", "protocol", "sessionId", "seq", "timestampMs", "requestSequence", "catalogSequence", "displayId", "firstFrameId", "width", "height" },
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(SessionText, root.GetProperty("sessionId").GetString());
        Assert.Equal("display_changed", root.GetProperty("type").GetString());
        foreach (string name in new[] { "protocol", "seq", "timestampMs", "requestSequence", "catalogSequence", "displayId", "firstFrameId", "width", "height" })
            Assert.Equal(JsonValueKind.Number, root.GetProperty(name).ValueKind);
        Assert.Equal(ulong.MaxValue, root.GetProperty("firstFrameId").GetUInt64());
        Assert.Equal(payload, Parse(payload).Serialize());
    }

    [Fact]
    public void Accepts_Reordered_Fields_Whitespace_And_Fully_Escaped_Names_Type_And_Guid()
    {
        string reordered = BuildObject(Fields.Reverse());
        Assert.Equal(Utf8(GoldenPayload), Parse(" \r\n\t" + reordered + " \r\n\t").Serialize());
        string escaped = BuildObject(Fields.Reverse().Select(field =>
            (Escape(field.Name), field.Name switch
            {
                "type" => Escape("display_changed"),
                "sessionId" => Escape(SessionText),
                _ => field.Value,
            })), namesAreJson: true);
        Assert.Equal(Utf8(GoldenPayload), Parse(escaped).Serialize());
        Assert.Equal(Utf8(GoldenPayload), Parse(GoldenPayload.Replace("display_changed", "display_\\u0063hanged", StringComparison.Ordinal)).Serialize());
    }

    public static IEnumerable<object[]> MetadataBoundaryCases()
    {
        foreach (long sequence in new[] { 0L, long.MaxValue })
        foreach (long timestamp in new[] { 0L, long.MaxValue })
        foreach (long requestSequence in new[] { 0L, long.MaxValue })
        foreach (long catalogSequence in new[] { 0L, long.MaxValue })
            yield return [sequence, timestamp, requestSequence, catalogSequence];
    }

    [Theory]
    [MemberData(nameof(MetadataBoundaryCases))]
    public void Accepts_All_Nonnegative_Int64_Boundary_Combinations(long sequence, long timestamp, long requestSequence, long catalogSequence)
    {
        string payload = BuildObject(Fields.Select(field => (field.Name, field.Name switch
        {
            "seq" => Number(sequence), "timestampMs" => Number(timestamp),
            "requestSequence" => Number(requestSequence), "catalogSequence" => Number(catalogSequence), _ => field.Value,
        })));
        DisplayChangedFrame parsed = Parse(payload);
        var constructed = new DisplayChangedFrame(Session, sequence, timestamp, requestSequence, catalogSequence, new DisplayId(255), ulong.MaxValue, 1920, 1080);
        Assert.Equal(sequence, parsed.Sequence);
        Assert.Equal(timestamp, parsed.TimestampMs);
        Assert.Equal(requestSequence, parsed.RequestSequence);
        Assert.Equal(catalogSequence, parsed.CatalogSequence);
        Assert.Equal(Utf8(payload), parsed.Serialize());
        Assert.Equal(Utf8(payload), constructed.Serialize());
        Assert.InRange(constructed.Serialize().Length, 1, 4096);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2147483647")]
    [InlineData("2147483648")]
    [InlineData("4294967295")]
    [InlineData("4294967296")]
    [InlineData("9007199254740991")]
    [InlineData("9007199254740992")]
    [InlineData("9007199254740993")]
    [InlineData("9223372036854775807")]
    [InlineData("9223372036854775808")]
    [InlineData("18446744073709551615")]
    public void Accepts_Full_UInt64_FrameIds_Without_Signed_Or_FloatingPoint_Narrowing(string number)
    {
        ulong expected = ulong.Parse(number, CultureInfo.InvariantCulture);
        string payload = Replace("firstFrameId", number);
        DisplayChangedFrame parsed = Parse(payload);
        var constructed = new DisplayChangedFrame(Session, 42, 1700000000000, 7009, 17, new DisplayId(255), expected, 1920, 1080);
        Assert.Equal(expected, parsed.FirstFrameId);
        Assert.Equal(expected, constructed.FirstFrameId);
        Assert.Equal(Utf8(payload), parsed.Serialize());
        Assert.Equal(Utf8(payload), constructed.Serialize());
        Assert.Equal(expected, Parse(constructed.Serialize()).FirstFrameId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(254)]
    [InlineData(255)]
    public void Accepts_DisplayId_Boundaries_And_Preserves_Raw_Index(int displayId)
    {
        string payload = Replace("displayId", Number(displayId));
        Assert.Equal(new DisplayId(displayId), Parse(payload).DisplayId);
        Assert.Equal(Utf8(payload), new DisplayChangedFrame(Session, 42, 1700000000000, 7009, 17, new DisplayId(displayId), ulong.MaxValue, 1920, 1080).Serialize());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 8191)]
    [InlineData(1, 8192)]
    [InlineData(8191, 1)]
    [InlineData(8191, 8191)]
    [InlineData(8191, 8192)]
    [InlineData(8192, 1)]
    [InlineData(8192, 8191)]
    [InlineData(8192, 8192)]
    public void Accepts_Independent_Dimension_Boundaries_Without_An_Extra_Pixel_Budget(int width, int height)
    {
        string payload = BuildObject(Fields.Select(field => (field.Name, field.Name switch
        {
            "width" => Number(width), "height" => Number(height), _ => field.Value,
        })));
        DisplayChangedFrame parsed = Parse(payload);
        Assert.Equal(width, parsed.Width);
        Assert.Equal(height, parsed.Height);
        Assert.Equal(Utf8(payload), parsed.Serialize());
        Assert.Equal(Utf8(payload), new DisplayChangedFrame(Session, 42, 1700000000000, 7009, 17, new DisplayId(255), ulong.MaxValue, width, height).Serialize());
    }

    [Theory]
    [InlineData(-1L, 0L, 0L, 0L, "sequence")]
    [InlineData(long.MinValue, 0L, 0L, 0L, "sequence")]
    [InlineData(0L, -1L, 0L, 0L, "timestampMs")]
    [InlineData(0L, long.MinValue, 0L, 0L, "timestampMs")]
    [InlineData(0L, 0L, -1L, 0L, "requestSequence")]
    [InlineData(0L, 0L, long.MinValue, 0L, "requestSequence")]
    [InlineData(0L, 0L, 0L, -1L, "catalogSequence")]
    [InlineData(0L, 0L, 0L, long.MinValue, "catalogSequence")]
    public void Constructor_Rejects_Each_Negative_Metadata_Value(long sequence, long timestamp, long requestSequence, long catalogSequence, string parameter)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DisplayChangedFrame(Session, sequence, timestamp, requestSequence, catalogSequence, new DisplayId(0), 0, 1, 1));
        Assert.Equal(parameter, error.ParamName);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Constructor_Rejects_Invalid_DisplayIds(int id)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new DisplayChangedFrame(Session, 0, 0, 0, 0, new DisplayId(id), 0, 1, 1));
        Assert.Equal("displayId", error.ParamName);
    }

    [Theory]
    [InlineData(0, 1, "width")]
    [InlineData(-1, 1, "width")]
    [InlineData(8193, 1, "width")]
    [InlineData(int.MinValue, 1, "width")]
    [InlineData(int.MaxValue, 1, "width")]
    [InlineData(1, 0, "height")]
    [InlineData(1, -1, "height")]
    [InlineData(1, 8193, "height")]
    [InlineData(1, int.MinValue, "height")]
    [InlineData(1, int.MaxValue, "height")]
    public void Constructor_Rejects_Each_Invalid_Dimension(int width, int height, string parameter)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new DisplayChangedFrame(Session, 0, 0, 0, 0, new DisplayId(0), 0, width, height));
        Assert.Equal(parameter, error.ParamName);
    }

    [Fact]
    public void Constructor_Rejects_Empty_Session()
    {
        var error = Assert.Throws<ArgumentException>(() => new DisplayChangedFrame(Guid.Empty, 0, 0, 0, 0, new DisplayId(0), 0, 1, 1));
        Assert.Equal("sessionId", error.ParamName);
    }

    public static IEnumerable<object[]> InvalidFieldCases()
    {
        foreach ((string name, string value) in Fields)
        {
            yield return [BuildObject(Fields.Where(field => field.Name != name)), name + ":missing"];
            string[] wrongTypes = name is "type" or "sessionId"
                ? ["null", "true", "false", "0", "[]", "{}"]
                : ["null", "true", "false", "\"1\"", "[]", "{}"];
            foreach (string wrongType in wrongTypes)
                yield return [Replace(name, wrongType), name + ":wrong-type:" + wrongType];
            yield return [BuildObject(Fields.Append((name, value))), name + ":duplicate"];
            var encodedFields = Fields.Select(field => (JsonSerializer.Serialize(field.Name), field.Value));
            yield return [BuildObject(encodedFields.Append((Escape(name), value)), namesAreJson: true), name + ":escaped-duplicate-last"];
            yield return [BuildObject(encodedFields.Prepend((Escape(name), value)), namesAreJson: true), name + ":escaped-duplicate-first"];
            yield return [BuildObject(Fields.Select(field => (Escape(field.Name), field.Value)).Append((JsonSerializer.Serialize(name), value)), namesAreJson: true), name + ":escaped-original-duplicate"];
            string wrongCase = char.ToUpperInvariant(name[0]) + name[1..];
            yield return [BuildObject(Fields.Select(field => (field.Name == name ? wrongCase : field.Name, field.Value))), name + ":wrong-case"];
        }
    }

    [Theory]
    [MemberData(nameof(InvalidFieldCases))]
    public void Rejects_Every_Missing_WrongType_Duplicate_EscapedDuplicate_And_WrongCase_Field(string payload, string scenario)
    {
        Reject(Utf8(payload), scenario: scenario);
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("frameId")]
    [InlineData("ack")]
    [InlineData("success")]
    [InlineData("error")]
    [InlineData("failure")]
    [InlineData("reason")]
    [InlineData("deviceKey")]
    [InlineData("sessionToken")]
    [InlineData("displays")]
    public void Rejects_Unknown_Fields_And_Their_Escaped_Aliases(string name)
    {
        Reject(Utf8(BuildObject(Fields.Append((name, "\"remote-private-value\"")))));
        var encodedFields = Fields.Select(field => (JsonSerializer.Serialize(field.Name), field.Value));
        Reject(Utf8(BuildObject(encodedFields.Append((Escape(name), "\"remote-private-value\"")), namesAreJson: true)));
    }

    [Theory]
    [InlineData("\"request_display\"")]
    [InlineData("\"display_change_failed\"")]
    [InlineData("\"display_catalog_accept\"")]
    [InlineData("\"channel_hello\"")]
    [InlineData("\"DISPLAY_CHANGED\"")]
    [InlineData("\"display_Changed\"")]
    [InlineData("\"display_changed \"")]
    [InlineData("\" display_changed\"")]
    [InlineData("\"display_changed\\u0000\"")]
    [InlineData("\"display_changed\\n\"")]
    [InlineData("\"\"")]
    public void Rejects_Nonmatching_Type_Strings(string type)
    {
        Reject(Utf8(Replace("type", type)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("01234567-89AB-CDEF-0123-456789ABCDEF")]
    [InlineData(" 01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef ")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    [InlineData("{01234567-89ab-cdef-0123-456789abcdef}")]
    [InlineData("(01234567-89ab-cdef-0123-456789abcdef)")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("01234567-89ab-cdef-0123-456789abcdeg")]
    [InlineData("fedcba98-7654-3210-fedc-ba9876543210")]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef\\u0000")]
    [InlineData("\\u0000")]
    public void Requires_Canonical_Nonempty_ContextMatching_Guid(string session)
    {
        Reject(Utf8(Replace("sessionId", "\"" + session + "\"")));
    }

    public static IEnumerable<object[]> InvalidIntegerCases()
    {
        string[] names = ["protocol", "seq", "timestampMs", "requestSequence", "catalogSequence", "displayId", "firstFrameId", "width", "height"];
        string[] invalidForms =
        [
            "1.0", "1e0", "1E0", "1E+0", "1e-0", "0.0", "-0.0", "01", "+1", "0x1", "NaN",
            "Infinity", "-Infinity", "1_0", "18446744073709551616", "-9223372036854775809",
            "9999999999999999999999999999", "-1", "-9223372036854775808",
        ];
        foreach (string name in names)
        foreach (string form in invalidForms)
            yield return [name, form];
        foreach (string name in names.Where(name => name != "firstFrameId"))
        foreach (string form in new[] { "9223372036854775808", "18446744073709551615" })
            yield return [name, form];
        foreach (string form in new[] { "0", "2", "9223372036854775807" })
            yield return ["protocol", form];
        foreach (string form in new[] { "256", "2147483647", "2147483648", "9223372036854775807" })
            yield return ["displayId", form];
        foreach (string name in new[] { "width", "height" })
        foreach (string form in new[] { "0", "8193", "65536", "2147483647", "2147483648", "9223372036854775807" })
            yield return [name, form];
    }

    [Theory]
    [MemberData(nameof(InvalidIntegerCases))]
    public void Rejects_Fraction_Exponent_Invalid_Syntax_Overflow_And_OutOfRange_Integers(string field, string value)
    {
        Reject(Utf8(Replace(field, value)), scenario: field + ":" + value);
    }

    [Theory]
    [InlineData(0L, long.MaxValue)]
    [InlineData(long.MaxValue, 0L)]
    [InlineData(17L, 17L)]
    public void Reply_Sequence_And_RequestSequence_Are_Independent_Metadata_Not_Replay_Or_Authorization_Gates(long sequence, long requestSequence)
    {
        string payload = BuildObject(Fields.Select(field => (field.Name, field.Name switch
        {
            "seq" => Number(sequence), "requestSequence" => Number(requestSequence), _ => field.Value,
        })));
        DisplayChangedFrame first = Parse(payload);
        DisplayChangedFrame second = Parse(payload);
        Assert.NotSame(first, second);
        Assert.Equal(sequence, first.Sequence);
        Assert.Equal(requestSequence, first.RequestSequence);
        Assert.Equal(first.Serialize(), second.Serialize());
        Assert.Equal(Utf8(payload), first.Serialize());
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000001")]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("ffffffff-ffff-ffff-ffff-ffffffffffff")]
    public void Accepts_Canonical_Guid_Boundaries_With_Matching_Context(string sessionText)
    {
        Guid session = Guid.Parse(sessionText);
        byte[] payload = Utf8(Replace("sessionId", "\"" + sessionText + "\""));
        Assert.True(DisplayChangedFrame.TryParse(payload, session, out var frame, out var rejection));
        Assert.Null(rejection);
        Assert.Equal(session, Assert.IsType<DisplayChangedFrame>(frame).SessionId);
        Assert.Equal(payload, new DisplayChangedFrame(session, 42, 1700000000000, 7009, 17, new DisplayId(255), ulong.MaxValue, 1920, 1080).Serialize());
    }

    [Fact]
    public void Caller_Context_Must_Be_Nonempty_And_Match_The_Reply()
    {
        byte[] payload = Utf8(GoldenPayload);
        Assert.False(DisplayChangedFrame.TryParse(payload, Guid.Empty, out var frame, out var rejection));
        Assert.Null(frame);
        Assert.Equal(InvalidContext, rejection);
        Assert.False(DisplayChangedFrame.TryParse(payload, OtherSession, out frame, out rejection));
        Assert.Null(frame);
        Assert.Equal(InvalidPayload, rejection);
    }

    [Theory]
    [InlineData(0, true, InvalidLength)]
    [InlineData(0, false, InvalidLength)]
    [InlineData(4097, true, InvalidLength)]
    [InlineData(4097, false, InvalidLength)]
    [InlineData(1, true, InvalidContext)]
    [InlineData(1, false, InvalidPayload)]
    public void Rejection_Priority_Is_Length_Then_Context_Then_Payload(int length, bool emptyContext, string expected)
    {
        Assert.False(DisplayChangedFrame.TryParse(new byte[length], emptyContext ? Guid.Empty : Session, out var frame, out var rejection));
        Assert.Null(frame);
        Assert.Equal(expected, rejection);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("null")]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("\"display_changed\"")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{")]
    [InlineData("not json")]
    public void Rejects_NonObject_Empty_And_Incomplete_Roots(string payload)
    {
        Reject(Utf8(payload), payload.Length == 0 ? InvalidLength : InvalidPayload);
    }

    public static IEnumerable<object[]> InvalidJsonSyntaxCases()
    {
        foreach (string suffix in new[] { "{}", "[]", " null", " remote-private-value", ",", "\0", "/*comment*/", "//comment", "\u00a0" })
            yield return [GoldenPayload + suffix];
        yield return ["/*comment*/" + GoldenPayload];
        yield return [GoldenPayload.Replace("\"seq\":42", "\"seq\":/*comment*/42", StringComparison.Ordinal)];
        yield return [GoldenPayload[..^1] + ",}"];
        yield return ["\v" + GoldenPayload];
        yield return [Replace("type", "\"display\nchanged\"")];
        yield return [Replace("type", "\"bad\\q\"")];
        yield return [Replace("sessionId", "\"bad\\u12\"")];
        yield return [Replace("firstFrameId", new string('[', 256) + "0" + new string(']', 256))];
        yield return [Replace("requestSequence", "{\"seq\":42}")];
    }

    [Theory]
    [MemberData(nameof(InvalidJsonSyntaxCases))]
    public void Rejects_Trailing_Data_Comments_TrailingCommas_Invalid_Escapes_And_Depth(string payload)
    {
        Reject(Utf8(payload));
    }

    [Fact]
    public void Rejects_Utf8_Bom_And_Every_Truncated_Prefix()
    {
        byte[] valid = Utf8(GoldenPayload);
        Reject([0xef, 0xbb, 0xbf, .. valid]);
        for (int length = 0; length < valid.Length; length++)
            Reject(valid.AsSpan(0, length), length == 0 ? InvalidLength : InvalidPayload);
    }

    [Theory]
    [InlineData(new byte[] { 0x80 })]
    [InlineData(new byte[] { 0xc0, 0xaf })]
    [InlineData(new byte[] { 0xe2, 0x82 })]
    [InlineData(new byte[] { 0xed, 0xa0, 0x80 })]
    [InlineData(new byte[] { 0xed, 0xb0, 0x80 })]
    [InlineData(new byte[] { 0xf0, 0x80, 0x80, 0xaf })]
    [InlineData(new byte[] { 0xf4, 0x90, 0x80, 0x80 })]
    [InlineData(new byte[] { 0xff, 0xfe })]
    public void Rejects_Invalid_Utf8_In_Property_Type_Session_And_Trailing_Bytes(byte[] invalidUtf8)
    {
        foreach (string part in new[] { "type", "display_changed", SessionText })
        {
            int offset = GoldenPayload.IndexOf(part, StringComparison.Ordinal);
            byte[] payload = [.. Utf8(GoldenPayload[..offset]), .. invalidUtf8, .. Utf8(GoldenPayload[(offset + 1)..])];
            Reject(payload);
        }
        Reject([.. Utf8(GoldenPayload), .. invalidUtf8]);
    }

    [Theory]
    [InlineData("\\uD800")]
    [InlineData("\\uDC00")]
    [InlineData("\\uD800A")]
    [InlineData("A\\uDC00")]
    [InlineData("\\uDC00\\uD800")]
    [InlineData("\\uD800\\uD800")]
    [InlineData("\\uDC00\\uDC00")]
    [InlineData("\\uD83D\\uDE00\\uD800")]
    public void Rejects_Unpaired_And_Misordered_Surrogates_In_All_String_Positions(string escaped)
    {
        Reject(Utf8(Replace("type", "\"" + escaped + "\"")));
        Reject(Utf8(Replace("sessionId", "\"" + escaped + "\"")));
        Reject(Utf8(GoldenPayload.Replace("\"type\"", "\"" + escaped + "\"", StringComparison.Ordinal)));
    }

    [Fact]
    public void Rejects_Overlong_Strings_Before_Unbounded_String_Allocation()
    {
        Reject(Utf8(Replace("sessionId", "\"" + new string('a', 217) + "\"")));
        Reject(Utf8(Replace("sessionId", Escape(SessionText + "0"))));
        Reject(Utf8(Replace("type", "\"" + new string('a', 91) + "\"")));
        Reject(Utf8(Replace("type", Escape("display_changed0"))));
        Reject(Utf8(BuildObject(Fields.Append((new string('a', 91), "0")))));
        Reject(Utf8(BuildObject(Fields.Append((new string('a', 3000), "0")))));
        Assert.Equal(Utf8(GoldenPayload), Parse(Replace("sessionId", Escape(SessionText))).Serialize());
    }

    [Theory]
    [InlineData(4095)]
    [InlineData(4096)]
    public void Accepts_Exact_Byte_Budget_And_One_Less_With_Whitespace(int length)
    {
        byte[] payload = Utf8(GoldenPayload.PadRight(length));
        Assert.Equal(length, payload.Length);
        Assert.Equal(Utf8(GoldenPayload), Parse(payload).Serialize());
    }

    [Fact]
    public void Rejects_One_Byte_Above_Budget_Even_When_Extra_Content_Is_Whitespace()
    {
        Reject(Utf8(GoldenPayload.PadRight(4097)), InvalidLength);
    }

    [Fact]
    public void Parser_Uses_Only_The_Provided_Slice_And_Does_Not_Retain_Or_Mutate_Input()
    {
        byte[] valid = Utf8(GoldenPayload);
        byte[] input = [0xff, .. valid, 0xfe];
        byte[] original = input.ToArray();
        Assert.True(DisplayChangedFrame.TryParse(input.AsSpan(1, valid.Length), Session, out var frame, out var rejection));
        Assert.Null(rejection);
        Assert.Equal(original, input);
        Array.Fill(input, (byte)0);
        Assert.Equal(valid, Assert.IsType<DisplayChangedFrame>(frame).Serialize());
    }

    [Fact]
    public void Every_Serialize_Returns_A_Fresh_Independent_Array()
    {
        DisplayChangedFrame frame = Parse(GoldenPayload);
        byte[] first = frame.Serialize();
        byte[] second = frame.Serialize();
        Assert.NotSame(first, second);
        Assert.Equal(Utf8(GoldenPayload), first);
        Assert.Equal(first, second);
        Array.Clear(first);
        Assert.Equal(Utf8(GoldenPayload), second);
        Array.Fill(second, (byte)'x');
        byte[] third = frame.Serialize();
        Assert.NotSame(first, third);
        Assert.NotSame(second, third);
        Assert.Equal(Utf8(GoldenPayload), third);
    }

    [Fact]
    public void Failure_Clears_Previous_Success_And_Diagnostics_Do_Not_Expose_Peer_Data()
    {
        Assert.True(DisplayChangedFrame.TryParse(Utf8(GoldenPayload), Session, out var frame, out var rejection));
        Assert.NotNull(frame);
        Assert.Null(rejection);
        byte[] invalid = Utf8(BuildObject(Fields.Append(("remote-private-field", "\"remote-private-value\""))));
        Assert.False(DisplayChangedFrame.TryParse(invalid, Session, out frame, out rejection));
        Assert.Null(frame);
        Assert.Equal(InvalidPayload, rejection);
        Assert.DoesNotContain("remote-private", rejection!, StringComparison.Ordinal);
        Assert.False(DisplayChangedFrame.TryParse([], Session, out frame, out rejection));
        Assert.Null(frame);
        Assert.Equal(InvalidLength, rejection);
        Assert.False(DisplayChangedFrame.TryParse(Utf8(GoldenPayload), Guid.Empty, out frame, out rejection));
        Assert.Null(frame);
        Assert.Equal(InvalidContext, rejection);
    }

    [Fact]
    public void Contract_Is_Internal_Sealed_Immutable_And_Adds_No_Public_Api()
    {
        Type type = typeof(DisplayChangedFrame);
        Assert.True(type.IsNotPublic);
        Assert.True(type.IsSealed);
        Assert.False(type.IsVisible);
        const BindingFlags publicDeclared = BindingFlags.Public | BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static;
        Assert.Empty(type.GetConstructors(publicDeclared));
        Assert.Empty(type.GetMethods(publicDeclared));
        Assert.Empty(type.GetProperties(publicDeclared));
        Assert.Empty(type.GetFields(publicDeclared));
        Assert.Empty(type.GetEvents(publicDeclared));
        const BindingFlags instanceMembers = BindingFlags.NonPublic | BindingFlags.DeclaredOnly | BindingFlags.Instance;
        PropertyInfo[] properties = type.GetProperties(instanceMembers);
        Assert.Equal(new[] { "CatalogSequence", "DisplayId", "FirstFrameId", "Height", "RequestSequence", "Sequence", "SessionId", "TimestampMs", "Width" },
            properties.Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        foreach (PropertyInfo property in properties)
        {
            Assert.True(property.GetMethod!.IsAssembly);
            Assert.Null(property.SetMethod);
        }
        foreach (FieldInfo field in type.GetFields(instanceMembers))
            Assert.True(field.IsInitOnly);
        ConstructorInfo constructor = Assert.Single(type.GetConstructors(instanceMembers));
        Assert.True(constructor.IsAssembly);
        Assert.Equal(new[] { typeof(Guid), typeof(long), typeof(long), typeof(long), typeof(long), typeof(DisplayId), typeof(ulong), typeof(int), typeof(int) },
            constructor.GetParameters().Select(parameter => parameter.ParameterType));
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Escape(string value) => "\"" + string.Concat(value.Select(character =>
        "\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture))) + "\"";
    private static string BuildObject(IEnumerable<(string Name, string Value)> fields, bool namesAreJson = false) =>
        "{" + string.Join(',', fields.Select(field => (namesAreJson ? field.Name : JsonSerializer.Serialize(field.Name)) + ":" + field.Value)) + "}";
    private static string Replace(string name, string value) => BuildObject(Fields.Select(field =>
        (field.Name, field.Name == name ? value : field.Value)));
    private static DisplayChangedFrame Parse(string payload) => Parse(Utf8(payload));
    private static DisplayChangedFrame Parse(byte[] payload)
    {
        Assert.True(DisplayChangedFrame.TryParse(payload, Session, out var frame, out var rejection), rejection);
        Assert.Null(rejection);
        return Assert.IsType<DisplayChangedFrame>(frame);
    }
    private static void Reject(ReadOnlySpan<byte> payload, string expected = InvalidPayload, string? scenario = null)
    {
        Assert.False(DisplayChangedFrame.TryParse(payload, Session, out var frame, out var rejection), scenario);
        Assert.Null(frame);
        Assert.Equal(expected, rejection);
        Assert.DoesNotContain("remote-private", rejection!, StringComparison.Ordinal);
    }
}
