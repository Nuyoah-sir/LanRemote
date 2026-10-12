using System.Buffers.Binary;
using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using LanRemote.Core.Models;

namespace LanRemote.Transport.Tests;

public sealed class DisplayCatalogFrameTests
{
    private const string ValidEntry = """{"id":0,"name":"Main","x":0,"y":0,"width":1920,"height":1080,"isPrimary":true}""";
    private const string ValidPayload = """{"type":"host_status","protocol":1,"seq":42,"timestampMs":1700000000000,"displays":[{"id":0,"name":"Main","x":0,"y":0,"width":1920,"height":1080,"isPrimary":true}]}""";
    private const string GoldenPayload = """{"type":"host_status","protocol":1,"seq":42,"timestampMs":1700000000000,"displays":[{"id":7,"name":"Left","x":-1280,"y":-120,"width":1280,"height":1024,"isPrimary":false},{"id":0,"name":"Main","x":0,"y":0,"width":1920,"height":1080,"isPrimary":true}]}""";
    private const string InvalidPayload = "host-status-invalid-payload";
    private const string InvalidLength = "host-status-invalid-length";
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(10);

    private static readonly (string Name, string Value)[] RootFields =
    [
        ("type", "\"host_status\""),
        ("protocol", "1"),
        ("seq", "42"),
        ("timestampMs", "1700000000000"),
        ("displays", "[" + ValidEntry + "]"),
    ];

    private static readonly (string Name, string Value)[] EntryFields =
    [
        ("id", "0"), ("name", "\"Main\""), ("x", "0"), ("y", "0"),
        ("width", "1920"), ("height", "1080"), ("isPrimary", "true"),
    ];

    [Fact]
    public void Constants_Match_The_Independent_Wire_Contract()
    {
        Assert.Equal("host_status", DisplayCatalogFrame.ExpectedType);
        Assert.Equal(1, DisplayCatalogFrame.ExpectedProtocol);
        Assert.Equal(256 * 1024, DisplayCatalogFrame.MaxPayloadByteLength);
        Assert.Equal(256, DisplayCatalogFrame.MaxDisplays);
        Assert.Equal(128, DisplayCatalogFrame.MaxNameLength);
    }

    [Fact]
    public void Parses_Independent_Literal_And_Preserves_Unsorted_Display_Ids()
    {
        DisplayCatalogFrame parsed = Parse(GoldenPayload);

        Assert.Equal(42L, parsed.Sequence);
        Assert.Equal(1700000000000L, parsed.TimestampMs);
        Assert.Equal(new[] { 7, 0 }, parsed.Displays.Select(display => display.Id.Value));
        Assert.Equal(new DisplayInfo(new DisplayId(7), "Left", -1280, -120, 1280, 1024, false), parsed.Displays[0]);
        Assert.Equal(MainDisplay(), parsed.Displays[1]);
    }

    [Fact]
    public void Serialize_Matches_Independent_Literal_Bytes_And_Field_Order()
    {
        var frame = new DisplayCatalogFrame(42, 1700000000000,
        [
            new DisplayInfo(new DisplayId(7), "Left", -1280, -120, 1280, 1024, false),
            MainDisplay(),
        ]);

        byte[] payload = frame.Serialize();

        Assert.Equal(Utf8(GoldenPayload), payload);
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;
        Assert.Equal(new[] { "type", "protocol", "seq", "timestampMs", "displays" },
            root.EnumerateObject().Select(property => property.Name));
        foreach (JsonElement display in root.GetProperty("displays").EnumerateArray())
        {
            Assert.Equal(new[] { "id", "name", "x", "y", "width", "height", "isPrimary" },
                display.EnumerateObject().Select(property => property.Name));
            Assert.Equal(JsonValueKind.Number, display.GetProperty("id").ValueKind);
        }
        foreach (string forbidden in new[] { "deviceId", "deviceKey", "Right", "Bottom", "Id", "selectedDisplayId", "sessionToken" })
            Assert.DoesNotContain("\"" + forbidden + "\"", Encoding.UTF8.GetString(payload), StringComparison.Ordinal);
    }

    [Fact]
    public void Accepts_Reordered_Fields_And_Legal_Escaped_Property_Names()
    {
        string entry = BuildObject(EntryFields.Reverse().Select(field =>
            (EscapeFirstCharacter(field.Name), field.Value)), namesAreJson: true);
        string root = BuildObject(RootFields.Reverse().Select(field =>
            (EscapeFirstCharacter(field.Name), field.Name == "displays" ? "[" + entry + "]" : field.Value)), namesAreJson: true);

        DisplayCatalogFrame parsed = Parse(root);

        Assert.Equal(MainDisplay(), Assert.Single(parsed.Displays));
        Assert.Equal(Utf8(ValidPayload), parsed.Serialize());
    }

    [Fact]
    public void Accepts_An_Escaped_Type_Value()
    {
        Assert.Equal(MainDisplay(), Assert.Single(Parse(ReplaceRoot("type", "\"host_\\u0073tatus\"")).Displays));
    }

    public static IEnumerable<object[]> InvalidFieldCases()
    {
        foreach (bool entry in new[] { false, true })
        {
            (string Name, string Value)[] fields = entry ? EntryFields : RootFields;
            for (int index = 0; index < fields.Length; index++)
            {
                string name = fields[index].Name;
                string Wrap(string json) => entry ? Catalog(json) : json;
                var without = fields.Where((_, current) => current != index);
                yield return [Wrap(BuildObject(without)), $"{(entry ? "entry" : "root")}.{name}:missing"];

                string[] wrongTypes = name switch
                {
                    "type" or "name" => ["null", "true", "0", "[]", "{}"],
                    "displays" => ["null", "true", "0", "\"value\"", "{}"],
                    "isPrimary" => ["null", "0", "1", "\"true\"", "[]", "{}"],
                    _ => ["null", "true", "false", "\"1\"", "[]", "{}"],
                };
                foreach (string wrongType in wrongTypes)
                    yield return [Wrap(BuildObject(fields.Select((field, current) =>
                        current == index ? (field.Name, wrongType) : field))), $"{name}:wrong-type:{wrongType}"];

                yield return [Wrap(BuildObject(fields.Append(fields[index]))), $"{name}:duplicate"];
                var escapedDuplicate = fields.Select(field => (JsonSerializer.Serialize(field.Name), field.Value))
                    .Append((EscapeFirstCharacter(name), fields[index].Value));
                yield return [Wrap(BuildObject(escapedDuplicate, namesAreJson: true)), $"{name}:escaped-duplicate"];

                string wrongCase = char.ToUpperInvariant(name[0]) + name[1..];
                yield return [Wrap(BuildObject(fields.Select((field, current) =>
                    current == index ? (wrongCase, field.Value) : field))), $"{name}:wrong-case"];
            }
        }
    }

    [Theory]
    [MemberData(nameof(InvalidFieldCases))]
    public void Rejects_Every_Missing_Null_WrongType_Duplicate_EscapedDuplicate_And_WrongCase_Field(
        string payload, string scenario)
    {
        AssertRejected(Utf8(payload), InvalidPayload, scenario);
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("deviceId")]
    [InlineData("deviceKey")]
    [InlineData("Right")]
    [InlineData("Bottom")]
    [InlineData("Id")]
    [InlineData("selectedDisplayId")]
    [InlineData("sessionToken")]
    public void Rejects_Unknown_Fields_At_Both_Levels(string name)
    {
        string root = BuildObject(RootFields.Append((name, "\"remote-private-value\"")));
        string entry = BuildObject(EntryFields.Append((name, "\"remote-private-value\"")));
        AssertRejected(Utf8(root), InvalidPayload);
        AssertRejected(Utf8(Catalog(entry)), InvalidPayload);
    }

    [Theory]
    [InlineData("\"Host_Status\"")]
    [InlineData("\"host_status \"")]
    [InlineData("\" host_status\"")]
    [InlineData("\"auth_success\"")]
    [InlineData("\"channel_hello\"")]
    [InlineData("\"\"")]
    public void Rejects_Other_Type_Values(string type)
    {
        AssertRejected(Utf8(ReplaceRoot("type", type)), InvalidPayload);
    }

    public static IEnumerable<object[]> InvalidIntegerCases()
    {
        string[] fields = ["protocol", "seq", "timestampMs", "id", "x", "y", "width", "height"];
        string[] invalidForms = ["1.0", "1e0", "1E0", "1E+0", "1e-0", "0.0", "-0.0", "01", "+1", "0x1", "NaN", "Infinity", "-Infinity", "1_0"];
        foreach (string field in fields)
        foreach (string form in invalidForms)
            yield return [field, form];

        foreach (string field in new[] { "protocol", "seq", "timestampMs" })
        foreach (string invalid in new[] { "-1", "-9223372036854775808", "9223372036854775808", "18446744073709551615" })
            yield return [field, invalid];

        foreach (string invalid in new[] { "0", "2", "2147483647" })
            yield return ["protocol", invalid];
        foreach (string invalid in new[] { "-1", "256", "2147483647", "2147483648", "-2147483649" })
            yield return ["id", invalid];
        foreach (string field in new[] { "x", "y", "width", "height" })
        foreach (string invalid in new[] { "2147483648", "-2147483649", "9223372036854775807", "-9223372036854775808" })
            yield return [field, invalid];
        foreach (string field in new[] { "width", "height" })
        foreach (string invalid in new[] { "0", "-1", "-2147483648" })
            yield return [field, invalid];
    }

    [Theory]
    [MemberData(nameof(InvalidIntegerCases))]
    public void Rejects_NonIntegral_Forms_And_OutOfRange_Numbers(string field, string value)
    {
        string payload = field is "protocol" or "seq" or "timestampMs"
            ? ReplaceRoot(field, value)
            : Catalog(ReplaceEntry(field, value));

        AssertRejected(Utf8(payload), InvalidPayload, field + ":" + value);
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(long.MaxValue, 0L)]
    [InlineData(0L, long.MaxValue)]
    [InlineData(long.MaxValue, long.MaxValue)]
    public void Accepts_Exact_Nonnegative_Int64_Boundaries(long sequence, long timestamp)
    {
        string payload = Catalog(ValidEntry, Number(sequence), Number(timestamp));
        DisplayCatalogFrame parsed = Parse(payload);
        var constructed = new DisplayCatalogFrame(sequence, timestamp, [MainDisplay()]);

        Assert.Equal(sequence, parsed.Sequence);
        Assert.Equal(timestamp, parsed.TimestampMs);
        Assert.Equal(Utf8(payload), constructed.Serialize());
        Assert.Equal(Utf8(payload), parsed.Serialize());
    }

    [Theory]
    [InlineData(-1L, 0L)]
    [InlineData(long.MinValue, 0L)]
    [InlineData(0L, -1L)]
    [InlineData(0L, long.MinValue)]
    public void Constructor_Rejects_Negative_Sequence_Or_Timestamp(long sequence, long timestamp)
    {
        Assert.ThrowsAny<ArgumentException>(() => new DisplayCatalogFrame(sequence, timestamp, [MainDisplay()]));
    }

    [Theory]
    [InlineData(0, 0, 1, 1)]
    [InlineData(int.MinValue, int.MinValue, int.MaxValue, int.MaxValue)]
    [InlineData(0, 0, int.MaxValue, int.MaxValue)]
    [InlineData(int.MaxValue - 1, int.MaxValue - 1, 1, 1)]
    [InlineData(-8192, -4096, 16384, 8192)]
    [InlineData(0, 0, 100000, 100000)]
    public void Accepts_Int32_Geometry_Boundaries_And_Metadata_Beyond_Pixel_Budgets(int x, int y, int width, int height)
    {
        DisplayInfo expected = MainDisplay() with { X = x, Y = y, Width = width, Height = height };
        string entry = BuildObject(EntryFields.Select(field => (field.Name, field.Name switch
        {
            "x" => Number(x), "y" => Number(y), "width" => Number(width), "height" => Number(height), _ => field.Value,
        })));

        Assert.Equal(expected, Assert.Single(Parse(Catalog(entry)).Displays));
        Assert.Equal(Utf8(Catalog(entry)), new DisplayCatalogFrame(42, 1700000000000, [expected]).Serialize());
    }

    [Theory]
    [InlineData(int.MaxValue, 0, 1, 1)]
    [InlineData(0, int.MaxValue, 1, 1)]
    [InlineData(1, 0, int.MaxValue, 1)]
    [InlineData(0, 1, 1, int.MaxValue)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue)]
    public void Rejects_Right_Or_Bottom_Overflow_Using_Wide_Arithmetic(int x, int y, int width, int height)
    {
        DisplayInfo display = MainDisplay() with { X = x, Y = y, Width = width, Height = height };
        string entry = BuildObject(EntryFields.Select(field => (field.Name, field.Name switch
        {
            "x" => Number(x), "y" => Number(y), "width" => Number(width), "height" => Number(height), _ => field.Value,
        })));

        AssertRejected(Utf8(Catalog(entry)), InvalidPayload);
        Assert.ThrowsAny<ArgumentException>(() => new DisplayCatalogFrame(0, 0, [display]));
    }

    public static IEnumerable<object[]> ValidNameCases()
    {
        yield return ["A"];
        yield return [new string('a', 127)];
        yield return [new string('a', 128)];
        yield return [new string('\u4e2d', 128)];
        yield return ["\U0001F600"];
        yield return [string.Concat(Enumerable.Repeat("\U0001F600", 64))];
        yield return [new string('a', 126) + "\U0001F600"];
        yield return [" Left "];
        yield return ["\"\\/<>&"];
        yield return ["e\u0301"];
        yield return ["\u4e2d\u6587\u663e\u793a\u5668"];
    }

    [Theory]
    [MemberData(nameof(ValidNameCases))]
    public void Accepts_Valid_Unicode_Names_By_Utf16_CodeUnit_Length(string name)
    {
        string payload = Catalog(ReplaceEntry("name", JsonSerializer.Serialize(name)));
        DisplayCatalogFrame parsed = Parse(payload);
        var constructed = new DisplayCatalogFrame(42, 1700000000000, [MainDisplay() with { Name = name }]);

        Assert.Equal(name, Assert.Single(parsed.Displays).Name);
        Assert.Equal(name, Assert.Single(Parse(constructed.Serialize()).Displays).Name);
    }

    [Theory]
    [InlineData("\u4e2d\u6587\u663e\u793a\u5668")]
    [InlineData("\U0001F600")]
    [InlineData("A\U0001F600B")]
    public void Accepts_Unescaped_Valid_Multibyte_Utf8_In_Names(string name)
    {
        Assert.Equal(name, Assert.Single(Parse(Catalog(ReplaceEntry("name", "\"" + name + "\""))).Displays).Name);
    }

    [Fact]
    public void Accepts_128_Characters_Encoded_As_768_Raw_Name_Bytes()
    {
        string escapedName = string.Concat(Enumerable.Repeat("\\u0061", 128));
        Assert.Equal(768, Utf8(escapedName).Length);

        DisplayCatalogFrame parsed = Parse(Catalog(ReplaceEntry("name", "\"" + escapedName + "\"")));

        Assert.Equal(new string('a', 128), Assert.Single(parsed.Displays).Name);
    }

    [Fact]
    public void Rejects_129_Characters_Encoded_As_774_Raw_Name_Bytes()
    {
        string escapedName = string.Concat(Enumerable.Repeat("\\u0061", 129));
        Assert.Equal(774, Utf8(escapedName).Length);

        AssertRejected(Utf8(Catalog(ReplaceEntry("name", "\"" + escapedName + "\""))), InvalidPayload);
    }

    [Theory]
    [InlineData("\"\\uD83D\\uDE00\"", "\U0001F600")]
    [InlineData("\"\\u4e2d\\u6587\"", "\u4e2d\u6587")]
    [InlineData("\"\\u0041\\u0022\\u005c\"", "A\"\\")]
    public void Accepts_Independent_Literal_Unicode_Escapes(string jsonName, string expected)
    {
        Assert.Equal(expected, Assert.Single(Parse(Catalog(ReplaceEntry("name", jsonName))).Displays).Name);
    }

    public static IEnumerable<object[]> InvalidNameCases()
    {
        yield return [string.Empty];
        yield return [" "];
        yield return [new string(' ', 128)];
        yield return ["\u00a0"];
        yield return ["\u3000\u2003"];
        yield return [new string('a', 129)];
        yield return [new string('\u4e2d', 129)];
        yield return [new string('a', 127) + "\U0001F600"];
        yield return [string.Concat(Enumerable.Repeat("\U0001F600", 65))];
        for (int codePoint = 0; codePoint <= 0x1f; codePoint++)
            yield return ["A" + (char)codePoint + "B"];
        for (int codePoint = 0x7f; codePoint <= 0x9f; codePoint++)
            yield return ["A" + (char)codePoint + "B"];
    }

    [Theory]
    [MemberData(nameof(InvalidNameCases))]
    public void Rejects_Empty_Whitespace_Overlong_And_All_C0_C1_Names(string name)
    {
        AssertRejected(Utf8(Catalog(ReplaceEntry("name", JsonSerializer.Serialize(name)))), InvalidPayload);
        Assert.ThrowsAny<ArgumentException>(() => new DisplayCatalogFrame(0, 0, [MainDisplay() with { Name = name }]));
    }

    [Theory]
    [InlineData("\"\\uD800\"")]
    [InlineData("\"\\uDC00\"")]
    [InlineData("\"\\uD800A\"")]
    [InlineData("\"A\\uDC00\"")]
    [InlineData("\"\\uDC00\\uD800\"")]
    [InlineData("\"\\uD800\\uD800\"")]
    [InlineData("\"\\uDC00\\uDC00\"")]
    [InlineData("\"\\uD83D\\uDE00\\uD800\"")]
    public void Rejects_Unpaired_Or_Misordered_Json_Surrogate_Escapes(string jsonName)
    {
        AssertRejected(Utf8(Catalog(ReplaceEntry("name", jsonName))), InvalidPayload);
    }

    [Theory]
    [InlineData(new int[] { 0xd800 })]
    [InlineData(new int[] { 0xdc00 })]
    [InlineData(new int[] { 0xd800, 0x41 })]
    [InlineData(new int[] { 0x41, 0xdc00 })]
    [InlineData(new int[] { 0xdc00, 0xd800 })]
    [InlineData(new int[] { 0xd800, 0xd800 })]
    [InlineData(new int[] { 0xdc00, 0xdc00 })]
    [InlineData(new int[] { 0xd83d, 0xde00, 0xd800 })]
    public void Constructor_Rejects_Invalid_Utf16_Without_Lossy_Encoding(int[] codeUnits)
    {
        // 在测试体内构造非法 UTF-16，避免测试发现器序列化参数时将其替换为 U+FFFD。
        string name = new(codeUnits.Select(value => (char)value).ToArray());
        Assert.ThrowsAny<ArgumentException>(() => new DisplayCatalogFrame(0, 0, [MainDisplay() with { Name = name }]));
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
    public void Rejects_Invalid_Utf8_Inside_An_Otherwise_Valid_Name(byte[] invalidUtf8)
    {
        byte[] payload = [.. Utf8(ValidPayload[..ValidPayload.IndexOf("Main", StringComparison.Ordinal)]),
            .. invalidUtf8,
            .. Utf8(ValidPayload[(ValidPayload.IndexOf("Main", StringComparison.Ordinal) + 4)..])];

        AssertRejected(payload, InvalidPayload);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(256)]
    public void Accepts_Display_Count_Boundaries_With_Unique_Unsorted_Ids(int count)
    {
        DisplayInfo[] displays = Displays(count);
        string payload = Catalog(string.Join(',', displays.Select(EntryJson)));

        DisplayCatalogFrame parsed = Parse(payload);
        var constructed = new DisplayCatalogFrame(42, 1700000000000, displays);

        Assert.Equal(count, parsed.Displays.Count);
        Assert.Equal(displays, parsed.Displays);
        Assert.Equal(Utf8(payload), constructed.Serialize());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(257)]
    public void Rejects_Empty_Or_Excessive_Display_Counts(int count)
    {
        DisplayInfo[] displays = Displays(count);
        string payload = Catalog(string.Join(',', displays.Select(EntryJson)));

        AssertRejected(Utf8(payload), InvalidPayload);
        Assert.ThrowsAny<ArgumentException>(() => new DisplayCatalogFrame(0, 0, displays));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("false")]
    [InlineData("1")]
    [InlineData("\"display\"")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void Rejects_Invalid_Array_Items_Alone_And_At_The_Final_Allowed_Position(string invalidEntry)
    {
        AssertRejected(Utf8(Catalog(invalidEntry)), InvalidPayload);
        string validPrefix = string.Join(',', Displays(255).Select(EntryJson));
        AssertRejected(Utf8(Catalog(validPrefix + "," + invalidEntry)), InvalidPayload);
    }

    [Theory]
    [InlineData("id", "256")]
    [InlineData("id", "0")]
    [InlineData("name", "\"\"")]
    [InlineData("width", "0")]
    [InlineData("height", "0")]
    [InlineData("x", "2147483647")]
    [InlineData("y", "2147483647")]
    [InlineData("isPrimary", "true")]
    public void Validates_All_Fields_Of_The_Final_Allowed_Display(string fieldName, string invalidValue)
    {
        string prefix = string.Join(',', Displays(255).Select(EntryJson));
        (string Name, string Value)[] finalFields = EntryFields.Select(field =>
            (field.Name, field.Name switch { "id" => "255", "isPrimary" => "false", _ => field.Value })).ToArray();
        Assert.Equal(256, Parse(Catalog(prefix + "," + BuildObject(finalFields))).Displays.Count);
        string invalidEntry = BuildObject(finalFields.Select(field =>
            (field.Name, field.Name == fieldName ? invalidValue : field.Value)));

        AssertRejected(Utf8(Catalog(prefix + "," + invalidEntry)), InvalidPayload);
    }

    [Fact]
    public void Serializes_256_Maximum_Length_Multibyte_Names_Within_The_Byte_Budget()
    {
        string name = new('\u4e2d', 128);
        DisplayInfo[] displays = Displays(256).Select(display => display with
        {
            Name = name, X = int.MinValue, Y = int.MinValue,
            Width = int.MaxValue, Height = int.MaxValue,
        }).ToArray();
        var frame = new DisplayCatalogFrame(long.MaxValue, long.MaxValue, displays);

        byte[] payload = frame.Serialize();

        Assert.InRange(payload.Length, 1, 256 * 1024);
        Assert.Equal(displays, Parse(payload).Displays);
    }

    [Fact]
    public void Rejects_A_257th_Item_After_A_Valid_Full_Catalog()
    {
        string prefix = string.Join(',', Displays(256).Select(EntryJson));
        Assert.Equal(256, Parse(Catalog(prefix)).Displays.Count);

        AssertRejected(Utf8(Catalog(prefix + "," + ValidEntry)), InvalidPayload);
        AssertRejected(Utf8(Catalog(prefix + ",null")), InvalidPayload);
        AssertRejected(Utf8(Catalog(prefix + "," + new string('[', 256) + "0" + new string(']', 256))), InvalidPayload);
    }

    [Fact]
    public void Rejects_Duplicate_Ids_Regardless_Of_Name_And_Geometry()
    {
        DisplayInfo first = MainDisplay();
        DisplayInfo duplicate = first with { Name = "Different", X = -1920, IsPrimary = false };
        AssertRejected(Utf8(Catalog(EntryJson(first) + "," + EntryJson(duplicate))), InvalidPayload);
        Assert.ThrowsAny<ArgumentException>(() => new DisplayCatalogFrame(0, 0, [first, duplicate]));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Requires_Exactly_One_Primary_Display(bool firstPrimary, bool secondPrimary)
    {
        DisplayInfo first = MainDisplay() with { IsPrimary = firstPrimary };
        DisplayInfo second = first with { Id = new DisplayId(255), IsPrimary = secondPrimary };
        AssertRejected(Utf8(Catalog(EntryJson(first) + "," + EntryJson(second))), InvalidPayload);
        Assert.ThrowsAny<ArgumentException>(() => new DisplayCatalogFrame(0, 0, [first, second]));
    }

    [Fact]
    public void Allows_Duplicate_Friendly_Names_And_Overlapping_Rectangles()
    {
        DisplayInfo first = MainDisplay();
        DisplayInfo second = first with { Id = new DisplayId(255), IsPrimary = false };
        string payload = Catalog(EntryJson(first) + "," + EntryJson(second));

        Assert.Equal(new[] { first, second }, Parse(payload).Displays);
        Assert.Equal(Utf8(payload), new DisplayCatalogFrame(42, 1700000000000, [first, second]).Serialize());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Constructor_Rejects_Invalid_Ids(int id)
    {
        Assert.ThrowsAny<ArgumentException>(() => new DisplayCatalogFrame(0, 0, [MainDisplay() with { Id = new DisplayId(id) }]));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    [InlineData(int.MinValue, 1)]
    [InlineData(1, int.MinValue)]
    public void Constructor_Rejects_Nonpositive_Dimensions(int width, int height)
    {
        Assert.ThrowsAny<ArgumentException>(() => new DisplayCatalogFrame(0, 0,
            [MainDisplay() with { Width = width, Height = height }]));
    }

    [Fact]
    public void Constructor_Rejects_Null_List_Null_Items_And_Null_Name()
    {
        Assert.ThrowsAny<ArgumentException>(() => new DisplayCatalogFrame(0, 0, null!));
        Assert.ThrowsAny<ArgumentException>(() => new DisplayCatalogFrame(0, 0, [null!]));
        Assert.ThrowsAny<ArgumentException>(() => new DisplayCatalogFrame(0, 0, [MainDisplay(), null!]));
        Assert.ThrowsAny<ArgumentException>(() => new DisplayCatalogFrame(0, 0, [MainDisplay() with { Name = null! }]));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("null")]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("\"host_status\"")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{")]
    [InlineData("not json")]
    public void Rejects_NonObject_Empty_And_Incomplete_Documents(string payload)
    {
        AssertRejected(Utf8(payload), payload.Length == 0 ? InvalidLength : InvalidPayload);
    }

    [Theory]
    [InlineData("\n\t ", "\r\n\t ")]
    [InlineData("", " ")]
    [InlineData(" ", "")]
    public void Allows_Only_Json_Whitespace_Around_The_Root(string prefix, string suffix)
    {
        Assert.Equal(MainDisplay(), Assert.Single(Parse(prefix + ValidPayload + suffix).Displays));
    }

    public static IEnumerable<object[]> InvalidJsonSyntaxCases()
    {
        yield return [ValidPayload + "{}"];
        yield return [ValidPayload + "[]"];
        yield return [ValidPayload + " null"];
        yield return [ValidPayload + " remote-private-value"];
        yield return [ValidPayload + ","];
        yield return ["/*comment*/" + ValidPayload];
        yield return [ValidPayload + "//comment"];
        yield return [ValidPayload.Replace("\"seq\":42", "\"seq\":/*comment*/42", StringComparison.Ordinal)];
        yield return [ValidPayload[..^1] + ",}"];
        yield return [Catalog(ValidEntry[..^1] + ",}")];
        yield return [Catalog(ValidEntry + ",")];
        yield return ["\u00a0" + ValidPayload];
        yield return [ValidPayload + "\0"];
        yield return ["\v" + ValidPayload];
        yield return [ValidPayload.Replace("\"Main\"", "\"Line\nBreak\"", StringComparison.Ordinal)];
        yield return [ValidPayload.Replace("\"Main\"", "\"bad\\q\"", StringComparison.Ordinal)];
        yield return [ValidPayload.Replace("\"Main\"", "\"bad\\u12\"", StringComparison.Ordinal)];
        yield return [ReplaceRoot("displays", new string('[', 256) + "0" + new string(']', 256))];
    }

    [Theory]
    [MemberData(nameof(InvalidJsonSyntaxCases))]
    public void Rejects_Trailing_Data_Comments_TrailingCommas_Invalid_Escapes_And_Excessive_Depth(string payload)
    {
        AssertRejected(Utf8(payload), InvalidPayload);
    }

    [Fact]
    public void Rejects_Utf8_Bom_Even_Before_An_Otherwise_Valid_Payload()
    {
        AssertRejected([0xef, 0xbb, 0xbf, .. Utf8(ValidPayload)], InvalidPayload);
    }

    [Theory]
    [InlineData(256 * 1024 - 1)]
    [InlineData(256 * 1024)]
    public void Accepts_Exact_Byte_Limit_And_One_Less_Including_Surrounding_Whitespace(int length)
    {
        byte[] payload = PadPayload(Utf8(ValidPayload), length);
        Assert.Equal(length, payload.Length);

        Assert.Equal(MainDisplay(), Assert.Single(Parse(payload).Displays));
    }

    [Fact]
    public void Rejects_One_Byte_Over_Limit_Even_When_All_Extra_Bytes_Are_Whitespace()
    {
        byte[] payload = PadPayload(Utf8(ValidPayload), 256 * 1024 + 1);
        Assert.Equal(256 * 1024 + 1, payload.Length);

        AssertRejected(payload, InvalidLength);
    }

    [Fact]
    public void Byte_Limit_Is_Not_A_Character_Limit_For_Multibyte_Names()
    {
        string payload = Catalog(ReplaceEntry("name", "\"" + new string('\u4e2d', 128) + "\""));
        byte[] bytes = Utf8(payload);
        Assert.True(bytes.Length > payload.Length);
        Assert.Equal(128, Assert.Single(Parse(PadPayload(bytes, 256 * 1024)).Displays).Name.Length);
        AssertRejected(PadPayload(bytes, 256 * 1024 + 1), InvalidLength);
    }

    [Fact]
    public void Parses_Only_The_Provided_Slice_Without_Reading_Or_Modifying_Outer_Bytes()
    {
        byte[] valid = PadPayload(Utf8(ValidPayload), 256 * 1024);
        byte[] outer = [0xff, .. valid, 0xfe];
        byte[] before = outer.ToArray();

        Assert.True(DisplayCatalogFrame.TryParse(outer.AsSpan(1, valid.Length), out DisplayCatalogFrame? frame, out string? rejection));
        Assert.NotNull(frame);
        Assert.Null(rejection);
        Assert.Equal(MainDisplay(), Assert.Single(frame.Displays));
        Assert.Equal(before, outer);
        AssertRejected(outer, InvalidLength);
        AssertRejected(outer.AsSpan(1, valid.Length + 1), InvalidLength);
    }

    [Fact]
    public void Parsing_Takes_Ownership_Of_Values_Not_The_Borrowed_Input_Buffer()
    {
        byte[] input = Utf8(GoldenPayload);
        byte[] before = input.ToArray();
        DisplayCatalogFrame parsed = Parse(input);
        Assert.Equal(before, input);

        Array.Fill(input, (byte)'x');

        Assert.Equal(Utf8(GoldenPayload), parsed.Serialize());
        Assert.Equal("Left", parsed.Displays[0].Name);
        Assert.Equal(7, parsed.Displays[0].Id.Value);
    }

    [Fact]
    public void Constructor_Copies_The_Source_Array_And_Does_Not_Expose_It()
    {
        DisplayInfo[] source = [MainDisplay()];
        var frame = new DisplayCatalogFrame(42, 1700000000000, source);
        Assert.NotSame(source, frame.Displays);
        Assert.Equal(MainDisplay(), source[0]);

        source[0] = MainDisplay() with { Id = new DisplayId(255), Name = "Changed" };

        Assert.Equal(MainDisplay(), Assert.Single(frame.Displays));
        Assert.Equal(Utf8(ValidPayload), frame.Serialize());
    }

    [Fact]
    public void Constructor_Copies_A_Mutable_List_And_Survives_Clear_And_Add()
    {
        var source = new List<DisplayInfo> { MainDisplay() };
        var frame = new DisplayCatalogFrame(42, 1700000000000, source);

        source.Clear();
        source.Add(MainDisplay() with { Name = "Changed" });

        Assert.Equal(MainDisplay(), Assert.Single(frame.Displays));
        Assert.Equal(Utf8(ValidPayload), frame.Serialize());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Displays_Cannot_Be_Mutated_Through_Array_Or_List_Casts(bool fromParser)
    {
        DisplayCatalogFrame frame = fromParser
            ? Parse(ValidPayload)
            : new DisplayCatalogFrame(42, 1700000000000, [MainDisplay()]);
        IReadOnlyList<DisplayInfo> displays = frame.Displays;
        Assert.False(displays is DisplayInfo[]);
        if (displays is IList<DisplayInfo> generic)
        {
            Assert.True(generic.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => generic[0] = MainDisplay() with { Name = "Changed" });
            Assert.Throws<NotSupportedException>(() => generic.Add(MainDisplay()));
            Assert.Throws<NotSupportedException>(() => generic.RemoveAt(0));
            Assert.Throws<NotSupportedException>(() => generic.Clear());
        }
        if (displays is IList nongeneric)
        {
            Assert.True(nongeneric.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => nongeneric[0] = MainDisplay() with { Name = "Changed" });
            Assert.Throws<NotSupportedException>(() => nongeneric.Add(MainDisplay()));
            Assert.Throws<NotSupportedException>(() => nongeneric.Clear());
        }
        Assert.Equal(Utf8(ValidPayload), frame.Serialize());
    }

    [Fact]
    public void Every_Serialize_Returns_An_Independent_Array()
    {
        DisplayCatalogFrame frame = Parse(ValidPayload);
        byte[] first = frame.Serialize();
        byte[] second = frame.Serialize();
        Assert.NotSame(first, second);
        Assert.Equal(Utf8(ValidPayload), first);
        Assert.Equal(Utf8(ValidPayload), second);

        Array.Clear(first);
        Assert.Equal(Utf8(ValidPayload), second);
        Array.Fill(second, (byte)'x');
        byte[] third = frame.Serialize();
        Assert.NotSame(first, third);
        Assert.NotSame(second, third);
        Assert.Equal(Utf8(ValidPayload), third);
        Assert.Equal(MainDisplay(), Assert.Single(frame.Displays));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(257)]
    [InlineData(int.MaxValue)]
    public void Constructor_Rejects_Invalid_Count_Before_Any_Index_Access_Or_Enumeration(int count)
    {
        var source = new IndexOnlyDisplays(count);

        Assert.ThrowsAny<ArgumentException>(() => new DisplayCatalogFrame(0, 0, source));

        Assert.Equal(0, source.IndexReads);
        Assert.Equal(0, source.EnumerationAttempts);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(256)]
    public void Constructor_Uses_A_Finite_Index_Copy_Not_Enumeration(int count)
    {
        DisplayInfo[] expected = Displays(count);
        var source = new IndexOnlyDisplays(expected);

        var frame = new DisplayCatalogFrame(0, 0, source);

        Assert.Equal(count, source.IndexReads);
        Assert.Equal(0, source.EnumerationAttempts);
        Assert.Equal(expected, frame.Displays);
    }

    [Fact]
    public void Constructor_Does_Not_Follow_A_List_That_Grows_During_Index_Copy()
    {
        DisplayInfo[] initial = Displays(2);
        var source = new GrowingDisplays(initial);
        DisplayCatalogFrame? frame = null;

        Assert.ThrowsAny<ArgumentException>(() => frame = new DisplayCatalogFrame(0, 0, source));

        Assert.Null(frame);
        Assert.Equal(initial.Length, source.IndexReads);
        Assert.Equal(0, source.EnumerationAttempts);
        Assert.True(source.Count > initial.Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(256)]
    public async Task Metadata_RoundTrips_Through_LengthPrefixed_MemoryStream(int count)
    {
        DisplayInfo[] displays = Displays(count);
        byte[] payload = new DisplayCatalogFrame(42, 1700000000000, displays).Serialize();
        using var stream = new MemoryStream();

        await FrameWriter.WriteFrameAsync(stream, payload, DisplayCatalogFrame.MaxPayloadByteLength, FrameTimeout);

        byte[] wire = stream.ToArray();
        Assert.Equal(payload.Length + 4, wire.Length);
        Assert.Equal((uint)payload.Length, BinaryPrimitives.ReadUInt32BigEndian(wire.AsSpan(0, 4)));
        Assert.Equal(payload, wire[4..]);
        stream.Position = 0;
        byte[] received = await new FrameReader(stream).ReadFrameAsync(
            DisplayCatalogFrame.MaxPayloadByteLength, FrameTimeout, FrameTimeout);
        Assert.Equal(payload, received);
        Assert.Equal(displays, Parse(received).Displays);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public async Task Metadata_Over_4KiB_Uses_Its_Local_Limit_Without_Changing_PreAuth_Limits()
    {
        byte[] payload = new DisplayCatalogFrame(0, 0, Displays(64)).Serialize();
        Assert.Equal(4 * 1024, TransportConstants.MaxPreAuthMessageBytes);
        Assert.Equal(1024 * 1024, TransportConstants.MaxControlMessageBytes);
        Assert.InRange(payload.Length, 4 * 1024 + 1, 256 * 1024);
        using var rejectedWrite = new MemoryStream();
        FrameProtocolException writeException = await Assert.ThrowsAsync<FrameProtocolException>(() =>
            FrameWriter.WriteFrameAsync(rejectedWrite, payload, TransportConstants.MaxPreAuthMessageBytes, FrameTimeout));
        Assert.Equal(FrameReader.RejectTooLarge, writeException.Reason);
        Assert.Equal(0L, rejectedWrite.Length);

        using var stream = new MemoryStream();
        await FrameWriter.WriteFrameAsync(stream, payload, DisplayCatalogFrame.MaxPayloadByteLength, FrameTimeout);
        stream.Position = 0;
        FrameProtocolException readException = await Assert.ThrowsAsync<FrameProtocolException>(() =>
            new FrameReader(stream).ReadFrameAsync(TransportConstants.MaxPreAuthMessageBytes, FrameTimeout, FrameTimeout));
        Assert.Equal(FrameReader.RejectTooLarge, readException.Reason);
        Assert.Equal(4L, stream.Position);

        stream.Position = 0;
        byte[] received = await new FrameReader(stream).ReadFrameAsync(
            DisplayCatalogFrame.MaxPayloadByteLength, FrameTimeout, FrameTimeout);
        Assert.Equal(64, Parse(received).Displays.Count);
    }

    private static DisplayInfo MainDisplay() => new(new DisplayId(0), "Main", 0, 0, 1920, 1080, true);

    private static DisplayInfo[] Displays(int count) => Enumerable.Range(0, count)
        .Select(index => new DisplayInfo(new DisplayId((count - index - 1) % 256), "Monitor", 0, 0, 1, 1, index == count - 1))
        .ToArray();

    private static string EntryJson(DisplayInfo display) => BuildObject(
    [
        ("id", Number(display.Id.Value)), ("name", JsonSerializer.Serialize(display.Name)),
        ("x", Number(display.X)), ("y", Number(display.Y)), ("width", Number(display.Width)),
        ("height", Number(display.Height)), ("isPrimary", display.IsPrimary ? "true" : "false"),
    ]);

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
    private static string Catalog(string entries, string sequence = "42", string timestamp = "1700000000000") =>
        "{\"type\":\"host_status\",\"protocol\":1,\"seq\":" + sequence + ",\"timestampMs\":" + timestamp + ",\"displays\":[" + entries + "]}";

    private static string BuildObject(IEnumerable<(string Name, string Value)> fields, bool namesAreJson = false) =>
        "{" + string.Join(',', fields.Select(field =>
            (namesAreJson ? field.Name : JsonSerializer.Serialize(field.Name)) + ":" + field.Value)) + "}";

    private static string ReplaceRoot(string name, string value) =>
        BuildObject(RootFields.Select(field => (field.Name, field.Name == name ? value : field.Value)));

    private static string ReplaceEntry(string name, string value) =>
        BuildObject(EntryFields.Select(field => (field.Name, field.Name == name ? value : field.Value)));

    private static string EscapeFirstCharacter(string name) =>
        "\"\\u" + ((int)name[0]).ToString("x4", CultureInfo.InvariantCulture) + name[1..] + "\"";

    private static DisplayCatalogFrame Parse(string payload) => Parse(Utf8(payload));

    private static DisplayCatalogFrame Parse(byte[] payload)
    {
        Assert.True(DisplayCatalogFrame.TryParse(payload, out DisplayCatalogFrame? parsed, out string? rejection), rejection);
        Assert.Null(rejection);
        Assert.NotNull(parsed);
        return parsed;
    }

    private static void AssertRejected(ReadOnlySpan<byte> payload, string expectedRejection, string? scenario = null)
    {
        Assert.False(DisplayCatalogFrame.TryParse(payload, out DisplayCatalogFrame? parsed, out string? rejection), scenario);
        Assert.Null(parsed);
        Assert.Equal(expectedRejection, rejection);
        Assert.DoesNotContain("remote-private-value", rejection!, StringComparison.Ordinal);
    }

    private static byte[] PadPayload(byte[] payload, int length)
    {
        Assert.True(payload.Length < length);
        byte[] padded = new byte[length];
        Array.Fill(padded, (byte)' ');
        payload.CopyTo(padded, 1);
        return padded;
    }

    private sealed class IndexOnlyDisplays : IReadOnlyList<DisplayInfo>
    {
        private readonly DisplayInfo[]? _items;
        public IndexOnlyDisplays(int count) => Count = count;
        public IndexOnlyDisplays(DisplayInfo[] items) { _items = items; Count = items.Length; }
        public int Count { get; }
        public int IndexReads { get; private set; }
        public int EnumerationAttempts { get; private set; }
        public DisplayInfo this[int index]
        {
            get
            {
                IndexReads++;
                if (_items is null)
                    throw new InvalidOperationException("超限 Count 不应触发索引访问。");
                return _items[index];
            }
        }
        public IEnumerator<DisplayInfo> GetEnumerator()
        {
            EnumerationAttempts++;
            throw new InvalidOperationException("目录快照必须按已验证的 Count 有限复制，不枚举源列表。");
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class GrowingDisplays : IReadOnlyList<DisplayInfo>
    {
        private readonly List<DisplayInfo> _items;
        public GrowingDisplays(DisplayInfo[] items) => _items = new List<DisplayInfo>(items);
        public int Count => _items.Count;
        public int IndexReads { get; private set; }
        public int EnumerationAttempts { get; private set; }
        public DisplayInfo this[int index]
        {
            get
            {
                IndexReads++;
                if (IndexReads > 2)
                    throw new InvalidOperationException("复制不得跟随增长后的 Count。");
                DisplayInfo item = _items[index];
                _items.Add(MainDisplay() with { Id = new DisplayId(100 + IndexReads), IsPrimary = false });
                return item;
            }
        }
        public IEnumerator<DisplayInfo> GetEnumerator()
        {
            EnumerationAttempts++;
            throw new InvalidOperationException("目录快照不得枚举增长的列表。");
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
