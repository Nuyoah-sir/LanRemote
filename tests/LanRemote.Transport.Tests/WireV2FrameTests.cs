using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

public sealed class WireV2FrameTests
{
    private static readonly Guid Session = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
    private static readonly Guid Device = Guid.Parse("fedcba98-7654-3210-fedc-ba9876543210");
    private static byte[] Nonce() => Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static byte[] Pin() => Enumerable.Range(160, 32).Select(i => (byte)i).ToArray();
    private static string Hello(int protocol = 2) =>
        "{\"type\":\"channel_hello\",\"channel\":\"control\",\"protocol\":" + protocol.ToString(CultureInfo.InvariantCulture) + "}";
    private static string Challenge(int protocol = 2) =>
        "{\"type\":\"auth_challenge\",\"protocol\":" + protocol.ToString(CultureInfo.InvariantCulture) +
        ",\"sessionId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"serverDeviceId\":\"fedcba98-7654-3210-fedc-ba9876543210\"," +
        "\"serverNonce\":\"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=\"," +
        "\"certSha256\":\"A0A1A2A3A4A5A6A7A8A9AAABACADAEAFB0B1B2B3B4B5B6B7B8B9BABBBCBDBEBF\",\"expiresInMs\":15000}";
    private static string Wire(bool challenge, int protocol = 2) => challenge ? Challenge(protocol) : Hello(protocol);
    private static WireV2ParseStatus Parse(byte[] bytes, bool challenge)
    {
        if (!challenge) return ControlHelloV2.Parse(bytes);
        var status = AuthChallengeV2.Parse(bytes, out var frame);
        if (status == WireV2ParseStatus.AcceptedV2) Assert.NotNull(frame);
        else Assert.Null(frame);
        return status;
    }
    private static void Reject(string json, bool challenge) =>
        Assert.Equal(WireV2ParseStatus.Invalid, Parse(Encoding.UTF8.GetBytes(json), challenge));
    private static string Replace(string json, string field, string raw)
    {
        using var doc = JsonDocument.Parse(json);
        return "{" + string.Join(",", doc.RootElement.EnumerateObject().Select(p =>
            "\"" + p.Name + "\":" + (p.Name == field ? raw : p.Value.GetRawText()))) + "}";
    }
    private static string Quoted(string text) => JsonSerializer.Serialize(text);

    [Fact]
    public void Constants_are_independent_of_live_V1()
    {
        Assert.Equal(2, ControlHelloV2.Protocol);
        Assert.Equal(2, AuthChallengeV2.Protocol);
        Assert.Equal(4096, ControlHelloV2.MaxPayloadByteLength);
        Assert.Equal(4096, AuthChallengeV2.MaxPayloadByteLength);
        Assert.Equal(1, HelloFrame.ExpectedProtocol);
        Assert.Equal(1, AuthProtocol.WireProtocolVersion);
        Assert.Equal(WireV2ParseStatus.Invalid, default);
    }

    [Theory]
    [InlineData(false, 1, 2)]
    [InlineData(false, 2, 3)]
    [InlineData(false, 3, 1)]
    [InlineData(false, int.MaxValue, 1)]
    [InlineData(true, 1, 2)]
    [InlineData(true, 2, 3)]
    [InlineData(true, 3, 1)]
    [InlineData(true, int.MaxValue, 1)]
    public void Complete_contract_classifies_version_only_at_end(bool challenge, int version, int expected)
    {
        var status = Parse(Encoding.UTF8.GetBytes(Wire(challenge, version)), challenge);
        Assert.Equal((WireV2ParseStatus)expected, status);
    }

    [Fact]
    public void Serializers_match_independent_literals_and_return_copies()
    {
        byte[] h = ControlHelloV2.Serialize(), h2 = ControlHelloV2.Serialize();
        Assert.Equal(Encoding.UTF8.GetBytes(Hello()), h);
        Assert.Equal(h, h2);
        Assert.NotSame(h, h2);
        h[0] = 0;
        Assert.Equal(WireV2ParseStatus.AcceptedV2, ControlHelloV2.Parse(h2));
        var frame = new AuthChallengeV2(Session, Device, Nonce(), Pin(), 15000);
        byte[] a = frame.Serialize(), b = frame.Serialize();
        Assert.Equal(Encoding.UTF8.GetBytes(Challenge()), a);
        Assert.Equal(a, b);
        Assert.NotSame(a, b);
        a[0] = 0;
        Assert.Equal(WireV2ParseStatus.AcceptedV2, AuthChallengeV2.Parse(b, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(Session, parsed.SessionId);
        Assert.Equal(Device, parsed.ServerDeviceId);
        Assert.Equal(15000, parsed.ExpiresInMs);
        Assert.Equal(Nonce(), parsed.CopyServerNonce());
        Assert.Equal(Pin(), parsed.CopyCertificateSha256());
    }

    [Fact]
    public void Input_and_exported_arrays_cannot_mutate_private_state()
    {
        byte[] nonce = Nonce(), pin = Pin();
        var frame = new AuthChallengeV2(Session, Device, nonce, pin, 15000);
        nonce[0] ^= 255;
        pin[0] ^= 255;
        byte[] exportedNonce = frame.CopyServerNonce(), exportedPin = frame.CopyCertificateSha256();
        Assert.Equal(Nonce(), exportedNonce);
        Assert.Equal(Pin(), exportedPin);
        exportedNonce[0] ^= 255;
        exportedPin[0] ^= 255;
        Assert.Equal(Encoding.UTF8.GetBytes(Challenge()), frame.Serialize());
        byte[] raw = Encoding.UTF8.GetBytes(Challenge());
        Assert.Equal(WireV2ParseStatus.AcceptedV2, AuthChallengeV2.Parse(raw, out var parsed));
        Assert.NotNull(parsed);
        Array.Fill(raw, (byte)0);
        parsed.CopyServerNonce()[0] ^= 255;
        parsed.CopyCertificateSha256()[0] ^= 255;
        Assert.Equal(Encoding.UTF8.GetBytes(Challenge()), parsed.Serialize());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void Constructor_rejects_wrong_nonce_and_pin_sizes(int length)
    {
        Assert.Throws<ArgumentException>(() => new AuthChallengeV2(Session, Device, new byte[length], Pin(), 1));
        Assert.Throws<ArgumentException>(() => new AuthChallengeV2(Session, Device, Nonce(), new byte[length], 1));
    }

    [Fact]
    public void Constructor_rejects_empty_identities_and_nonpositive_expiry()
    {
        Assert.Throws<ArgumentException>(() => new AuthChallengeV2(Guid.Empty, Device, Nonce(), Pin(), 1));
        Assert.Throws<ArgumentException>(() => new AuthChallengeV2(Session, Guid.Empty, Nonce(), Pin(), 1));
        foreach (int value in new[] { 0, -1, int.MinValue })
            Assert.Throws<ArgumentOutOfRangeException>(() => new AuthChallengeV2(Session, Device, Nonce(), Pin(), value));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void Positive_expiry_extremes_are_only_hints(int expiry)
    {
        var frame = new AuthChallengeV2(Session, Device, Nonce(), Pin(), expiry);
        Assert.Equal(WireV2ParseStatus.AcceptedV2, AuthChallengeV2.Parse(frame.Serialize(), out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(expiry, parsed.ExpiresInMs);
        foreach (int version in new[] { 1, 2, 3 })
            Assert.NotEqual(WireV2ParseStatus.Invalid, Parse(Encoding.UTF8.GetBytes(
                Replace(Challenge(version), "expiresInMs", expiry.ToString(CultureInfo.InvariantCulture))), true));
    }

    [Theory]
    [InlineData("sessionId")]
    [InlineData("serverDeviceId")]
    public void Empty_guid_is_only_legacy_evidence_never_a_V2_object(string field)
    {
        string old = Replace(Challenge(1), field, Quoted(Guid.Empty.ToString("D")));
        Assert.True(AuthChallengeFrame.TryParse(Encoding.UTF8.GetBytes(old), out _, out _));
        Assert.Equal(WireV2ParseStatus.LegacyV1, AuthChallengeV2.Parse(Encoding.UTF8.GetBytes(old), out var frame));
        Assert.Null(frame);
        foreach (int version in new[] { 2, 3 })
            Reject(Replace(Challenge(version), field, Quoted(Guid.Empty.ToString("D"))), true);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0")]
    [InlineData("-1")]
    [InlineData("1.0")]
    [InlineData("2.0")]
    [InlineData("1e0")]
    [InlineData("2E0")]
    [InlineData("1e1")]
    [InlineData("\"1\"")]
    [InlineData("\"2\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("01")]
    [InlineData("+1")]
    [InlineData("2147483648")]
    [InlineData("999999999999999999999999999999999999999")]
    public void Version_and_expiry_require_positive_Int32_literals(string raw)
    {
        foreach (bool challenge in new[] { false, true })
            Reject(Replace(Wire(challenge), "protocol", raw), challenge);
        foreach (int version in new[] { 1, 2, 3 })
            Reject(Replace(Challenge(version), "expiresInMs", raw), true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_field_is_required_unique_and_correctly_typed_before_version_classification(bool challenge)
    {
        foreach (int version in new[] { 1, 2, 3 })
        {
            string wire = Wire(challenge, version);
            using var doc = JsonDocument.Parse(wire);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                string missing = "{" + string.Join(",", doc.RootElement.EnumerateObject().Where(x => x.Name != p.Name)
                    .Select(x => "\"" + x.Name + "\":" + x.Value.GetRawText())) + "}";
                Reject(missing, challenge);
                foreach (string key in new[] { p.Name, "\\u" + ((int)p.Name[0]).ToString("x4", CultureInfo.InvariantCulture) + p.Name[1..] })
                {
                    Reject(wire[..^1] + ",\"" + key + "\":" + p.Value.GetRawText() + "}", challenge);
                    Reject("{\"" + key + "\":null," + wire[1..], challenge);
                }
                foreach (string raw in new[] { "null", "true", "[]", "{}" })
                    Reject(Replace(wire, p.Name, raw), challenge);
                if (p.Value.ValueKind == JsonValueKind.String) Reject(Replace(wire, p.Name, "12"), challenge);
                string wrongCase = char.ToUpperInvariant(p.Name[0]) + p.Name[1..];
                Reject(wire.Replace("\"" + p.Name + "\":", "\"" + wrongCase + "\":"), challenge);
            }
        }
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("capabilities")]
    [InlineData("displays")]
    [InlineData("Type")]
    [InlineData("Protocol")]
    [InlineData("clientProof")]
    public void Unknown_fields_are_invalid_even_after_a_legacy_version(string field)
    {
        foreach (bool challenge in new[] { false, true })
            foreach (int version in new[] { 1, 2, 3 })
                Reject(Wire(challenge, version)[..^1] + ",\"" + field + "\":0}", challenge);
    }

    [Theory]
    [InlineData("auth_success")]
    [InlineData("authentication_failed")]
    [InlineData("approval_pending")]
    [InlineData("display_catalog_offer")]
    [InlineData("Channel_Hello")]
    [InlineData("AUTH_CHALLENGE")]
    [InlineData("auth_challenge ")]
    [InlineData("")]
    public void Wrong_message_type_is_not_version_evidence(string type)
    {
        foreach (bool challenge in new[] { false, true })
            foreach (int version in new[] { 1, 2, 3 })
                Reject(Replace(Wire(challenge, version), "type", Quoted(type)), challenge);
    }

    [Theory]
    [InlineData("video")]
    [InlineData("Control")]
    [InlineData("control ")]
    [InlineData("")]
    public void Noncontrol_channel_is_not_legacy_control(string channel)
    {
        foreach (int version in new[] { 1, 2, 3 }) Reject(Replace(Hello(version), "channel", Quoted(channel)), false);
    }

    [Theory]
    [InlineData("")]
    [InlineData("01234567-89AB-CDEF-0123-456789ABCDEF")]
    [InlineData(" 01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef ")]
    [InlineData("{01234567-89ab-cdef-0123-456789abcdef}")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    [InlineData("01234567-89ab-cdef-0123-456789abcdeg")]
    [InlineData("\u0000")]
    public void Guid_fields_are_canonical_before_legacy_classification(string value)
    {
        foreach (int version in new[] { 1, 2, 3 })
            foreach (string field in new[] { "sessionId", "serverDeviceId" })
                Reject(Replace(Challenge(version), field, Quoted(value)), true);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(128)]
    public void Nonce_and_pin_decoded_sizes_are_exact(int length)
    {
        foreach (int version in new[] { 1, 2, 3 })
        {
            Reject(Replace(Challenge(version), "serverNonce", Quoted(Convert.ToBase64String(new byte[length]))), true);
            Reject(Replace(Challenge(version), "certSha256", Quoted(Convert.ToHexString(new byte[length]))), true);
        }
    }

    [Fact]
    public void Nonce_and_pin_canonical_encodings_are_enforced()
    {
        string nonce = Convert.ToBase64String(Nonce());
        string pin = Convert.ToHexString(Pin());
        foreach (int version in new[] { 1, 2, 3 })
        {
            foreach (string bad in new[] { nonce.TrimEnd('='), nonce + "=", nonce + " ", " " + nonce,
                nonce[..^2] + "9=", new string('-', 43) + "=", new string('_', 43) + "=", "!" + nonce[1..] })
                Reject(Replace(Challenge(version), "serverNonce", Quoted(bad)), true);
            foreach (string bad in new[] { pin.ToLowerInvariant(), pin[..^1], " " + pin, pin + " ", "G" + pin[1..] })
                Reject(Replace(Challenge(version), "certSha256", Quoted(bad)), true);
        }
        // 合法的 + /、零和全 FF 值只证明形状，不声称随机性。
        foreach (byte fill in new byte[] { 0, 251, 255 })
        {
            var frame = new AuthChallengeV2(Session, Device, Enumerable.Repeat(fill, 32).ToArray(), new byte[32], 1);
            Assert.Equal(WireV2ParseStatus.AcceptedV2, AuthChallengeV2.Parse(frame.Serialize(), out _));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reordered_fields_whitespace_and_equivalent_escapes_preserve_semantics(bool challenge)
    {
        foreach (int version in new[] { 1, 2, 3 })
        {
            string wire = Wire(challenge, version);
            var expected = Parse(Encoding.UTF8.GetBytes(wire), challenge);
            using var doc = JsonDocument.Parse(wire);
            string escaped = "{ " + string.Join(" , ", doc.RootElement.EnumerateObject().Reverse().Select(p =>
                "\"" + Escape(p.Name) + "\" : " + (p.Value.ValueKind == JsonValueKind.String
                    ? "\"" + Escape(p.Value.GetString()!) + "\"" : p.Value.GetRawText()))) + " }";
            Assert.Equal(expected, Parse(Encoding.UTF8.GetBytes("\r\n\t " + escaped + " \t\r\n"), challenge));
        }
    }
    private static string Escape(string text) => string.Concat(text.Select(c => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Payload_limit_includes_whitespace_and_is_checked_before_version_classification(bool challenge)
    {
        foreach (int version in new[] { 1, 2, 3 })
        {
            string wire = Wire(challenge, version);
            var expected = Parse(Encoding.UTF8.GetBytes(wire), challenge);
            foreach (int length in new[] { 4095, 4096 })
                Assert.Equal(expected, Parse(Encoding.UTF8.GetBytes(wire.PadRight(length)), challenge));
            Reject(wire.PadRight(4097), challenge);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("2")]
    [InlineData("\"channel_hello\"")]
    [InlineData("{\"protocol\":1}")]
    [InlineData("{\"protocol\":2}")]
    public void Incomplete_or_nonobject_payload_is_invalid(string json)
    {
        Reject(json, false);
        Reject(json, true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Truncation_trailing_values_comments_BOM_and_nested_data_fail_closed(bool challenge)
    {
        foreach (int version in new[] { 1, 2, 3 })
        {
            string wire = Wire(challenge, version);
            for (int size = 0; size < wire.Length; size++) Reject(wire[..size], challenge);
            foreach (string suffix in new[] { "{}", "[]", "null", "x", ",", "\0", "//comment", "/*comment*/" })
                Reject(wire + suffix, challenge);
            Reject("\uFEFF" + wire, challenge);
            Reject(wire[..^1] + ",}", challenge);
            Reject("{/*comment*/" + wire[1..], challenge);
            Reject("[" + wire + "]", challenge);
            Reject(Replace(wire, "type", "{\"type\":\"auth_challenge\"}"), challenge);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Invalid_UTF8_and_lone_surrogates_never_throw_or_classify_as_legacy(bool challenge)
    {
        foreach (int version in new[] { 1, 2, 3 })
        {
            string wire = Wire(challenge, version);
            foreach (string field in challenge ? new[] { "type", "sessionId", "serverDeviceId", "serverNonce", "certSha256" } : new[] { "type", "channel" })
            {
                Reject(Replace(wire, field, "\"\\uD800\""), challenge);
                Reject(Replace(wire, field, "\"\\uDC00\""), challenge);
                byte[] bytes = Encoding.UTF8.GetBytes(Replace(wire, field, "\"MARKER\""));
                int at = Encoding.UTF8.GetString(bytes).IndexOf("MARKER", StringComparison.Ordinal);
                bytes[at] = 0xFF;
                Assert.Equal(WireV2ParseStatus.Invalid, Parse(bytes, challenge));
            }
            byte[] key = Encoding.UTF8.GetBytes(wire);
            key[2] = 0xFF;
            Assert.Equal(WireV2ParseStatus.Invalid, Parse(key, challenge));
        }
    }

    [Fact]
    public void Strict_legacy_evidence_never_yields_V2_and_V1_still_rejects_V2()
    {
        Assert.True(HelloFrame.TryParse(Encoding.UTF8.GetBytes(Hello(1)), out _));
        Assert.Equal(WireV2ParseStatus.LegacyV1, ControlHelloV2.Parse(HelloFrame.Serialize()));
        Assert.False(HelloFrame.TryParse(ControlHelloV2.Serialize(), out _));
        var legacy = new AuthChallengeFrame(Session, Device, Nonce(), Pin(), 15000);
        Assert.True(AuthChallengeFrame.TryParse(legacy.Serialize(), out _, out _));
        Assert.Equal(WireV2ParseStatus.LegacyV1, AuthChallengeV2.Parse(legacy.Serialize(), out var frame));
        Assert.Null(frame);
        Assert.False(AuthChallengeFrame.TryParse(new AuthChallengeV2(Session, Device, Nonce(), Pin(), 15000).Serialize(), out _, out _));
    }

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("ar-SA")]
    public void Wire_is_culture_invariant(string name)
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            Assert.Equal(Encoding.UTF8.GetBytes(Hello()), ControlHelloV2.Serialize());
            Assert.Equal(Encoding.UTF8.GetBytes(Challenge()), new AuthChallengeV2(Session, Device, Nonce(), Pin(), 15000).Serialize());
            Assert.Equal(WireV2ParseStatus.AcceptedV2, Parse(Encoding.UTF8.GetBytes(Challenge()), true));
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void API_is_internal_has_no_writable_properties_or_memory_views()
    {
        foreach (Type type in new[] { typeof(ControlHelloV2), typeof(AuthChallengeV2), typeof(WireV2ParseStatus), typeof(WireV2Json) })
            Assert.False(type.IsPublic);
        foreach (var property in typeof(AuthChallengeV2).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            Assert.Null(property.SetMethod);
            Assert.True(property.PropertyType == typeof(Guid) || property.PropertyType == typeof(int));
        }
        Assert.All(typeof(AuthChallengeV2).GetFields(BindingFlags.Instance | BindingFlags.NonPublic), field =>
        {
            Assert.True(field.IsPrivate);
            Assert.True(field.IsInitOnly);
        });
    }
}
