using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LanRemote.Core.Models;

namespace LanRemote.Transport.Tests;

public sealed class DisplaySwitchRequestFrameTests
{
    private const string SessionText = "01234567-89ab-cdef-0123-456789abcdef";
    private const string GoldenPayload = """{"type":"request_display","protocol":1,"sessionId":"01234567-89ab-cdef-0123-456789abcdef","seq":42,"timestampMs":1700000000000,"catalogSequence":17,"displayId":255}""";
    private const string InvalidPayload = "request-display-invalid-payload";
    private const string InvalidLength = "request-display-invalid-length";
    private const string InvalidContext = "request-display-invalid-context";
    private static readonly Guid Session = Guid.Parse(SessionText);
    private static readonly Guid OtherSession = Guid.Parse("fedcba98-7654-3210-fedc-ba9876543210");
    private static readonly (string Name, string Value)[] Fields =
    [
        ("type", "\"request_display\""), ("protocol", "1"), ("sessionId", "\"" + SessionText + "\""),
        ("seq", "42"), ("timestampMs", "1700000000000"), ("catalogSequence", "17"), ("displayId", "255"),
    ];

    [Fact]
    public void Constants_Match_The_Independent_Request_Contract()
    {
        Assert.Equal("request_display", DisplaySwitchRequestFrame.ExpectedType);
        Assert.Equal(1, DisplaySwitchRequestFrame.ExpectedProtocol);
        Assert.Equal(4096, DisplaySwitchRequestFrame.MaxPayloadByteLength);
    }

    [Fact]
    public void Parses_Independent_Literal_Without_An_Encoder_Roundtrip()
    {
        DisplaySwitchRequestFrame frame = Parse(GoldenPayload);
        Assert.Equal(Session, frame.SessionId);
        Assert.Equal(42L, frame.Sequence);
        Assert.Equal(1700000000000L, frame.TimestampMs);
        Assert.Equal(17L, frame.CatalogSequence);
        Assert.Equal(new DisplayId(255), frame.DisplayId);
    }

    [Fact]
    public void Serializer_Matches_Independent_Literal_And_Exact_Fields()
    {
        var frame = new DisplaySwitchRequestFrame(Session, 42, 1700000000000, 17, new DisplayId(255));
        byte[] payload = frame.Serialize();
        Assert.Equal(Utf8(GoldenPayload), payload);
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;
        Assert.Equal(new[] { "type", "protocol", "sessionId", "seq", "timestampMs", "catalogSequence", "displayId" },
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(SessionText, root.GetProperty("sessionId").GetString());
        foreach (string name in new[] { "protocol", "seq", "timestampMs", "catalogSequence", "displayId" })
            Assert.Equal(JsonValueKind.Number, root.GetProperty(name).ValueKind);
    }

    [Fact]
    public void Accepts_Reordered_Fields_Whitespace_And_Fully_Escaped_Names_Type_And_Guid()
    {
        string reordered = BuildObject(Fields.Reverse());
        Assert.Equal(Utf8(GoldenPayload), Parse(" \r\n\t" + reordered + " \r\n\t").Serialize());
        string escaped = BuildObject(Fields.Reverse().Select(field =>
            (Escape(field.Name), field.Name switch
            {
                "type" => Escape("request_display"),
                "sessionId" => Escape(SessionText),
                _ => field.Value,
            })), namesAreJson: true);
        Assert.Equal(Utf8(GoldenPayload), Parse(escaped).Serialize());
        Assert.Equal(Utf8(GoldenPayload), Parse(GoldenPayload.Replace("request_display", "request_\\u0064isplay", StringComparison.Ordinal)).Serialize());
    }

    [Theory]
    [InlineData(0L, 0L, 0L)]
    [InlineData(long.MaxValue, 0L, 0L)]
    [InlineData(0L, long.MaxValue, 0L)]
    [InlineData(0L, 0L, long.MaxValue)]
    [InlineData(long.MaxValue, long.MaxValue, 0L)]
    [InlineData(long.MaxValue, 0L, long.MaxValue)]
    [InlineData(0L, long.MaxValue, long.MaxValue)]
    [InlineData(long.MaxValue, long.MaxValue, long.MaxValue)]
    public void Accepts_All_Nonnegative_Int64_Boundary_Combinations(long sequence, long timestamp, long catalogSequence)
    {
        string payload = BuildObject(Fields.Select(field => (field.Name, field.Name switch
        {
            "seq" => Number(sequence), "timestampMs" => Number(timestamp),
            "catalogSequence" => Number(catalogSequence), _ => field.Value,
        })));
        DisplaySwitchRequestFrame parsed = Parse(payload);
        var constructed = new DisplaySwitchRequestFrame(Session, sequence, timestamp, catalogSequence, new DisplayId(255));
        Assert.Equal(sequence, parsed.Sequence);
        Assert.Equal(timestamp, parsed.TimestampMs);
        Assert.Equal(catalogSequence, parsed.CatalogSequence);
        Assert.Equal(Utf8(payload), parsed.Serialize());
        Assert.Equal(Utf8(payload), constructed.Serialize());
        Assert.InRange(constructed.Serialize().Length, 1, 4096);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(254)]
    [InlineData(255)]
    public void Accepts_DisplayId_Boundaries_And_Preserves_Raw_Index(int displayId)
    {
        string payload = Replace("displayId", Number(displayId));
        DisplaySwitchRequestFrame frame = Parse(payload);
        Assert.Equal(new DisplayId(displayId), frame.DisplayId);
        Assert.Equal(Utf8(payload), new DisplaySwitchRequestFrame(Session, 42, 1700000000000, 17, new DisplayId(displayId)).Serialize());
    }

    [Theory]
    [InlineData(-1L, 0L, 0L, "sequence")]
    [InlineData(long.MinValue, 0L, 0L, "sequence")]
    [InlineData(0L, -1L, 0L, "timestampMs")]
    [InlineData(0L, long.MinValue, 0L, "timestampMs")]
    [InlineData(0L, 0L, -1L, "catalogSequence")]
    [InlineData(0L, 0L, long.MinValue, "catalogSequence")]
    public void Constructor_Rejects_Each_Negative_Metadata_Value(long sequence, long timestamp, long catalogSequence, string parameter)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DisplaySwitchRequestFrame(Session, sequence, timestamp, catalogSequence, new DisplayId(0)));
        Assert.Equal(parameter, error.ParamName);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Constructor_Rejects_Invalid_DisplayIds(int id)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new DisplaySwitchRequestFrame(Session, 0, 0, 0, new DisplayId(id)));
        Assert.Equal("displayId", error.ParamName);
    }

    [Fact]
    public void Constructor_Rejects_Empty_Session()
    {
        var error = Assert.Throws<ArgumentException>(() => new DisplaySwitchRequestFrame(Guid.Empty, 0, 0, 0, new DisplayId(0)));
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

    [Fact]
    public void Rejects_Escaped_Duplicate_Of_Type_After_All_Required_Fields()
    {
        Reject(Utf8(GoldenPayload[..^1] + ",\"\\u0074ype\":\"request_display\"}"));
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("frameId")]
    [InlineData("ack")]
    [InlineData("success")]
    [InlineData("deviceId")]
    [InlineData("deviceKey")]
    [InlineData("sessionToken")]
    [InlineData("displays")]
    public void Rejects_Unknown_Fields_Without_Freezing_A_Success_Response(string name)
    {
        Reject(Utf8(BuildObject(Fields.Append((name, "\"remote-private-value\"")))));
    }

    [Theory]
    [InlineData("\"host_status\"")]
    [InlineData("\"display_catalog_accept\"")]
    [InlineData("\"channel_hello\"")]
    [InlineData("\"REQUEST_DISPLAY\"")]
    [InlineData("\"request_Display\"")]
    [InlineData("\"request_display \"")]
    [InlineData("\" request_display\"")]
    [InlineData("\"request_display\\u0000\"")]
    [InlineData("\"request_display\\n\"")]
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
        string[] names = ["protocol", "seq", "timestampMs", "catalogSequence", "displayId"];
        string[] invalidForms =
        [
            "1.0", "1e0", "1E0", "1E+0", "1e-0", "0.0", "-0.0", "01", "+1", "0x1", "NaN",
            "Infinity", "-Infinity", "1_0", "9223372036854775808", "-9223372036854775809",
            "18446744073709551615", "9999999999999999999999999999", "-1", "-9223372036854775808",
        ];
        foreach (string name in names)
        foreach (string form in invalidForms)
            yield return [name, form];
        foreach (string form in new[] { "0", "2", "9223372036854775807" })
            yield return ["protocol", form];
        foreach (string form in new[] { "256", "2147483647", "2147483648", "9223372036854775807" })
            yield return ["displayId", form];
    }

    [Theory]
    [MemberData(nameof(InvalidIntegerCases))]
    public void Rejects_Fraction_Exponent_Invalid_Syntax_Overflow_And_OutOfRange_Integers(string field, string value)
    {
        Reject(Utf8(Replace(field, value)), scenario: field + ":" + value);
    }

    [Fact]
    public void Metadata_And_Repeated_Parse_Are_Not_Replay_Or_Authorization_Gates()
    {
        string payload = Replace("catalogSequence", "9223372036854775807");
        DisplaySwitchRequestFrame first = Parse(payload);
        DisplaySwitchRequestFrame second = Parse(payload);
        Assert.NotSame(first, second);
        Assert.Equal(first.Serialize(), second.Serialize());
        Assert.Equal(long.MaxValue, second.CatalogSequence);
        Assert.Equal(42L, second.Sequence);
        Assert.Equal(0L, Parse(Replace("seq", "0")).Sequence);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000001")]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("ffffffff-ffff-ffff-ffff-ffffffffffff")]
    public void Accepts_Canonical_Guid_Boundaries_With_Matching_Context(string sessionText)
    {
        Guid session = Guid.Parse(sessionText);
        byte[] payload = Utf8(Replace("sessionId", "\"" + sessionText + "\""));
        Assert.True(DisplaySwitchRequestFrame.TryParse(payload, session, out var frame, out var rejection));
        Assert.Null(rejection);
        Assert.Equal(session, Assert.IsType<DisplaySwitchRequestFrame>(frame).SessionId);
        Assert.Equal(payload, new DisplaySwitchRequestFrame(session, 42, 1700000000000, 17, new DisplayId(255)).Serialize());
    }

    [Fact]
    public void Caller_Context_Must_Be_Nonempty_And_Match_The_Request()
    {
        byte[] payload = Utf8(GoldenPayload);
        Assert.False(DisplaySwitchRequestFrame.TryParse(payload, Guid.Empty, out var frame, out var rejection));
        Assert.Null(frame);
        Assert.Equal(InvalidContext, rejection);
        Assert.False(DisplaySwitchRequestFrame.TryParse(payload, OtherSession, out frame, out rejection));
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
        Assert.False(DisplaySwitchRequestFrame.TryParse(new byte[length], emptyContext ? Guid.Empty : Session, out var frame, out var rejection));
        Assert.Null(frame);
        Assert.Equal(expected, rejection);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("null")]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("\"request_display\"")]
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
        yield return [Replace("type", "\"request\ndisplay\"")];
        yield return [Replace("type", "\"bad\\q\"")];
        yield return [Replace("sessionId", "\"bad\\u12\"")];
        yield return [Replace("displayId", new string('[', 256) + "0" + new string(']', 256))];
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
        foreach (string part in new[] { "type", "request_display", SessionText })
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
    public void Rejects_Overlong_Guid_Before_Unbounded_String_Allocation()
    {
        Reject(Utf8(Replace("sessionId", "\"" + new string('a', 217) + "\"")));
        Reject(Utf8(Replace("sessionId", Escape(SessionText + "0"))));
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
        Assert.True(DisplaySwitchRequestFrame.TryParse(input.AsSpan(1, valid.Length), Session, out var frame, out var rejection));
        Assert.Null(rejection);
        Assert.Equal(original, input);
        Array.Fill(input, (byte)0);
        Assert.Equal(valid, Assert.IsType<DisplaySwitchRequestFrame>(frame).Serialize());
    }

    [Fact]
    public void Every_Serialize_Returns_A_Fresh_Independent_Array()
    {
        DisplaySwitchRequestFrame frame = Parse(GoldenPayload);
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
        Assert.True(DisplaySwitchRequestFrame.TryParse(Utf8(GoldenPayload), Session, out var frame, out var rejection));
        Assert.NotNull(frame);
        Assert.Null(rejection);
        byte[] invalid = Utf8(BuildObject(Fields.Append(("remote-private-field", "\"remote-private-value\""))));
        Assert.False(DisplaySwitchRequestFrame.TryParse(invalid, Session, out frame, out rejection));
        Assert.Null(frame);
        Assert.Equal(InvalidPayload, rejection);
        Assert.DoesNotContain("remote-private", rejection!, StringComparison.Ordinal);
        Assert.False(DisplaySwitchRequestFrame.TryParse([], Session, out frame, out rejection));
        Assert.Null(frame);
        Assert.Equal(InvalidLength, rejection);
    }

    [Fact]
    public void Contract_Is_Internal_Sealed_Immutable_And_Adds_No_Public_Api()
    {
        Type type = typeof(DisplaySwitchRequestFrame);
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
        Assert.Equal(new[] { "CatalogSequence", "DisplayId", "Sequence", "SessionId", "TimestampMs" },
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
        Assert.Equal(new[] { typeof(Guid), typeof(long), typeof(long), typeof(long), typeof(DisplayId) },
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
    private static DisplaySwitchRequestFrame Parse(string payload) => Parse(Utf8(payload));
    private static DisplaySwitchRequestFrame Parse(byte[] payload)
    {
        Assert.True(DisplaySwitchRequestFrame.TryParse(payload, Session, out var frame, out var rejection), rejection);
        Assert.Null(rejection);
        return Assert.IsType<DisplaySwitchRequestFrame>(frame);
    }
    private static void Reject(ReadOnlySpan<byte> payload, string expected = InvalidPayload, string? scenario = null)
    {
        Assert.False(DisplaySwitchRequestFrame.TryParse(payload, Session, out var frame, out var rejection), scenario);
        Assert.Null(frame);
        Assert.Equal(expected, rejection);
        Assert.DoesNotContain("remote-private", rejection!, StringComparison.Ordinal);
    }
}
