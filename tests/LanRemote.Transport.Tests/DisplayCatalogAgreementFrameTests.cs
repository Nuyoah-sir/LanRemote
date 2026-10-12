using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LanRemote.Transport.Tests;

public sealed class DisplayCatalogAgreementFrameTests
{
    private static readonly Guid Session = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
    private static readonly Guid OtherSession = Guid.Parse("fedcba98-7654-3210-fedc-ba9876543210");
    private const string Invalid = "catalog-agreement-invalid-payload";

    private static string Wire(bool offer = true) =>
        "{\"type\":\"display_catalog_" + (offer ? "offer" : "accept") +
        "\",\"applicationProfile\":2,\"catalogSchema\":1,\"sessionId\":\"01234567-89ab-cdef-0123-456789abcdef\"}";

    private static bool Parse(byte[] bytes, bool offer, Guid expected,
        out DisplayCatalogAgreementFrame? frame, out string? error) => offer
        ? DisplayCatalogAgreementFrame.TryParseOffer(bytes, expected, out frame, out error)
        : DisplayCatalogAgreementFrame.TryParseAccept(bytes, expected, out frame, out error);

    private static void Accept(string json, bool offer = true)
    {
        Assert.True(Parse(Encoding.UTF8.GetBytes(json), offer, Session, out var frame, out var error));
        Assert.Null(error);
        Assert.NotNull(frame);
        Assert.Equal(Session, frame.SessionId);
        Assert.Equal(offer, frame.IsOffer);
    }

    private static void Reject(byte[] bytes, bool offer = true, string error = Invalid)
    {
        Assert.False(Parse(bytes, offer, Session, out var frame, out var actual));
        Assert.Null(frame);
        Assert.Equal(error, actual);
    }

    [Fact]
    public void Dedicated_contract_constants_are_not_legacy_hello_or_tls_versions()
    {
        Assert.Equal(2, DisplayCatalogAgreementFrame.ApplicationProfile);
        Assert.Equal(1, DisplayCatalogAgreementFrame.CatalogSchema);
        Assert.Equal(4096, DisplayCatalogAgreementFrame.MaxPayloadByteLength);
        Assert.Equal("display_catalog_offer", DisplayCatalogAgreementFrame.OfferType);
        Assert.Equal("display_catalog_accept", DisplayCatalogAgreementFrame.AcceptType);
        Assert.Equal(1, HelloFrame.ExpectedProtocol);
        Assert.Equal(1, DisplayCatalogFrame.ExpectedProtocol);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Literal_contract_parses_in_correct_direction(bool offer) => Accept(Wire(offer), offer);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Serializer_matches_independent_literal_and_returns_fresh_arrays(bool offer)
    {
        byte[] first = offer ? DisplayCatalogAgreementFrame.SerializeOffer(Session) : DisplayCatalogAgreementFrame.SerializeAccept(Session);
        byte[] second = offer ? DisplayCatalogAgreementFrame.SerializeOffer(Session) : DisplayCatalogAgreementFrame.SerializeAccept(Session);
        Assert.Equal(Encoding.UTF8.GetBytes(Wire(offer)), first);
        Assert.Equal(first, second);
        Assert.NotSame(first, second);
        first[0] = 0;
        Accept(Encoding.UTF8.GetString(second), offer);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Roundtrip_canonical_nonempty_guid_extremes(bool offer)
    {
        foreach (Guid id in new[] { Session, Guid.Parse("00000000-0000-0000-0000-000000000001"), Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff") })
        {
            byte[] bytes = offer ? DisplayCatalogAgreementFrame.SerializeOffer(id) : DisplayCatalogAgreementFrame.SerializeAccept(id);
            Assert.True(Parse(bytes, offer, id, out var frame, out var error));
            Assert.Null(error);
            Assert.Equal(id, Assert.IsType<DisplayCatalogAgreementFrame>(frame).SessionId);
            using var doc = JsonDocument.Parse(bytes);
            Assert.Equal(id.ToString("D"), doc.RootElement.GetProperty("sessionId").GetString());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Serializer_rejects_empty_session(bool offer)
    {
        Assert.Throws<ArgumentException>(() => offer
            ? DisplayCatalogAgreementFrame.SerializeOffer(Guid.Empty)
            : DisplayCatalogAgreementFrame.SerializeAccept(Guid.Empty));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Caller_context_must_be_nonempty_and_match(bool offer)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Wire(offer));
        Assert.False(Parse(bytes, offer, Guid.Empty, out var empty, out var emptyError));
        Assert.Null(empty);
        Assert.Equal("catalog-agreement-invalid-context", emptyError);
        Assert.False(Parse(bytes, offer, OtherSession, out var other, out var otherError));
        Assert.Null(other);
        Assert.Equal(Invalid, otherError);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Wrong_direction_is_not_accepted(bool offer) => Reject(Encoding.UTF8.GetBytes(Wire(!offer)), offer);

    [Theory]
    [InlineData("applicationProfile", "1")]
    [InlineData("applicationProfile", "3")]
    [InlineData("applicationProfile", "0")]
    [InlineData("applicationProfile", "-2")]
    [InlineData("applicationProfile", "2.0")]
    [InlineData("applicationProfile", "2e0")]
    [InlineData("applicationProfile", "2E0")]
    [InlineData("applicationProfile", "\"2\"")]
    [InlineData("applicationProfile", "null")]
    [InlineData("applicationProfile", "true")]
    [InlineData("applicationProfile", "[]")]
    [InlineData("applicationProfile", "{}")]
    [InlineData("applicationProfile", "9223372036854775808")]
    [InlineData("applicationProfile", "02")]
    [InlineData("applicationProfile", "+2")]
    [InlineData("catalogSchema", "0")]
    [InlineData("catalogSchema", "2")]
    [InlineData("catalogSchema", "-1")]
    [InlineData("catalogSchema", "1.0")]
    [InlineData("catalogSchema", "1e0")]
    [InlineData("catalogSchema", "\"1\"")]
    [InlineData("catalogSchema", "null")]
    [InlineData("catalogSchema", "false")]
    [InlineData("catalogSchema", "[1]")]
    [InlineData("catalogSchema", "{\"n\":1}")]
    [InlineData("catalogSchema", "9999999999999999999999999999999")]
    public void Profile_and_schema_are_exact_integer_contracts(string field, string value)
    {
        foreach (bool offer in new[] { true, false })
            Reject(Encoding.UTF8.GetBytes(Wire(offer).Replace("\"" + field + "\":" + (field == "applicationProfile" ? "2" : "1"), "\"" + field + "\":" + value)), offer);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("applicationProfile")]
    [InlineData("catalogSchema")]
    [InlineData("sessionId")]
    public void Every_field_is_required_and_duplicates_even_escaped_are_rejected(string field)
    {
        foreach (bool offer in new[] { true, false })
        {
            using var doc = JsonDocument.Parse(Wire(offer));
            var fields = doc.RootElement.EnumerateObject().ToArray();
            string missing = "{" + string.Join(",", fields.Where(p => p.Name != field).Select(p => "\"" + p.Name + "\":" + p.Value.GetRawText())) + "}";
            Reject(Encoding.UTF8.GetBytes(missing), offer);
            string value = doc.RootElement.GetProperty(field).GetRawText();
            foreach (string key in new[] { field, "\\u" + ((int)field[0]).ToString("x4", CultureInfo.InvariantCulture) + field[1..] })
            {
                Reject(Encoding.UTF8.GetBytes(Wire(offer)[..^1] + ",\"" + key + "\":" + value + "}"), offer);
                Reject(Encoding.UTF8.GetBytes("{\"" + key + "\":null," + Wire(offer)[1..]), offer);
            }
        }
    }

    [Theory]
    [InlineData("Type")]
    [InlineData("ApplicationProfile")]
    [InlineData("CatalogSchema")]
    [InlineData("SessionId")]
    [InlineData("protocol")]
    [InlineData("capabilities")]
    [InlineData("displays")]
    [InlineData("extra")]
    public void Unknown_or_case_changed_fields_fail_closed(string key)
    {
        foreach (bool offer in new[] { true, false })
            Reject(Encoding.UTF8.GetBytes(Wire(offer)[..^1] + ",\"" + key + "\":0}"), offer);
    }

    [Theory]
    [InlineData("")]
    [InlineData("01234567-89AB-CDEF-0123-456789ABCDEF")]
    [InlineData(" 01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef ")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    [InlineData("{01234567-89ab-cdef-0123-456789abcdef}")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("01234567-89ab-cdef-0123-456789abcdeg")]
    [InlineData("fedcba98-7654-3210-fedc-ba9876543210")]
    [InlineData("\\uD800")]
    [InlineData("\\u0000")]
    public void Session_is_canonical_nonempty_and_matches_context(string session)
    {
        foreach (bool offer in new[] { true, false })
            Reject(Encoding.UTF8.GetBytes(Wire(offer).Replace(Session.ToString("D"), session)), offer);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void Session_must_be_string(string token) => Reject(Encoding.UTF8.GetBytes(Wire().Replace("\"" + Session.ToString("D") + "\"", token)));

    [Theory]
    [InlineData("\"host_status\"")]
    [InlineData("\"channel_hello\"")]
    [InlineData("\"DISPLAY_CATALOG_OFFER\"")]
    [InlineData("\" display_catalog_offer\"")]
    [InlineData("\"display_catalog_offer \"")]
    [InlineData("\"display_catalog_offer\\u0000\"")]
    [InlineData("\"\\uD800\"")]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void Wrong_type_is_rejected(string token) => Reject(Encoding.UTF8.GetBytes(Wire().Replace("\"display_catalog_offer\"", token)));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("true")]
    [InlineData("0")]
    [InlineData("\"hello\"")]
    [InlineData("{\"outer\":{\"inner\":{\"deep\":[]}}}")]
    public void Invalid_roots_fail_without_partial_result(string input) => Reject(Encoding.UTF8.GetBytes(input), error: input.Length == 0 ? "catalog-agreement-invalid-length" : Invalid);

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("x")]
    [InlineData("\0")]
    [InlineData("/*comment*/")]
    [InlineData("//comment")]
    [InlineData("\u00a0")]
    public void Trailing_value_comment_or_non_json_space_is_rejected(string suffix) => Reject(Encoding.UTF8.GetBytes(Wire() + suffix));

    [Fact]
    public void Comments_trailing_comma_BOM_and_all_truncated_prefixes_are_rejected()
    {
        byte[] valid = Encoding.UTF8.GetBytes(Wire());
        Reject(Encoding.UTF8.GetBytes("/*comment*/" + Wire()));
        Reject(Encoding.UTF8.GetBytes(Wire().Replace(",\"applicationProfile\"", ",/*comment*/\"applicationProfile\"")));
        Reject(Encoding.UTF8.GetBytes(Wire()[..^1] + ",}"));
        Reject([0xef, 0xbb, 0xbf, ..valid]);
        for (int i = 0; i < valid.Length; i++)
            Reject(valid[..i], error: i == 0 ? "catalog-agreement-invalid-length" : Invalid);
    }

    [Theory]
    [InlineData(0x80)]
    [InlineData(0xc0)]
    [InlineData(0xff)]
    public void Invalid_utf8_in_known_property_or_values_is_rejected(int invalidByte)
    {
        foreach (string text in new[] { Wire().Replace("type", "ty?e"), Wire().Replace("display_catalog_offer", "display_?atalog_offer"), Wire().Replace("01234567", "012?4567") })
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            bytes[Array.IndexOf(bytes, (byte)'?')] = (byte)invalidByte;
            Reject(bytes);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Exact_byte_limit_accepts_json_whitespace_padding_and_rejects_one_more(bool offer)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Wire(offer).PadRight(4096));
        Assert.True(Parse(bytes, offer, Session, out var frame, out var error));
        Assert.NotNull(frame);
        Assert.Null(error);
        Reject([..bytes, (byte)' '], offer, "catalog-agreement-invalid-length");
        Reject(new byte[4097], offer, "catalog-agreement-invalid-length");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Property_order_whitespace_and_valid_json_escapes_are_accepted(bool offer)
    {
        Accept(" \r\n\t" + Wire(offer) + " \r\n\t", offer);
        using var doc = JsonDocument.Parse(Wire(offer));
        Accept("{" + string.Join(",", doc.RootElement.EnumerateObject().Reverse().Select(p => "\"" + p.Name + "\":" + p.Value.GetRawText())) + "}", offer);
        Accept(Wire(offer).Replace("type", "\\u0074ype").Replace("display_catalog", "\\u0064isplay_catalog").Replace("01234567", "\\u00301234567"), offer);
        Accept(Wire(offer).Replace(Session.ToString("D"), string.Concat(Session.ToString("D").Select(c => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture)))), offer);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Parser_does_not_retain_or_change_borrowed_input(bool offer)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Wire(offer));
        byte[] copy = bytes.ToArray();
        Assert.True(Parse(bytes, offer, Session, out var frame, out _));
        Assert.Equal(copy, bytes);
        Array.Fill(bytes, (byte)0);
        Assert.Equal(Session, Assert.IsType<DisplayCatalogAgreementFrame>(frame).SessionId);
        Assert.Equal(offer, frame.IsOffer);
    }

    [Fact]
    public void Failure_never_leaks_peer_text_or_retains_previous_success()
    {
        Assert.True(Parse(Encoding.UTF8.GetBytes(Wire()), true, Session, out var frame, out var error));
        Assert.False(DisplayCatalogAgreementFrame.TryParseOffer(Encoding.UTF8.GetBytes(Wire()[..^1] + ",\"secret_marker\":\"private-value\"}"), Session, out frame, out error));
        Assert.Null(frame);
        Assert.Equal(Invalid, error);
    }

    [Fact]
    public void Empty_input_takes_length_rejection_even_with_empty_context()
    {
        Assert.False(DisplayCatalogAgreementFrame.TryParseOffer([], Guid.Empty, out var frame, out var error));
        Assert.Null(frame);
        Assert.Equal("catalog-agreement-invalid-length", error);
    }

    [Fact]
    public void Codec_is_repeatable_not_a_session_grant_or_replay_gate()
    {
        byte[] bytes = DisplayCatalogAgreementFrame.SerializeAccept(Session);
        Assert.True(DisplayCatalogAgreementFrame.TryParseAccept(bytes, Session, out var first, out _));
        Assert.True(DisplayCatalogAgreementFrame.TryParseAccept(bytes, Session, out var second, out _));
        Assert.NotSame(first, second);
        Assert.Equal(Session, Assert.IsType<DisplayCatalogAgreementFrame>(second).SessionId);
    }
}
