using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;
using Xunit;

namespace LanRemote.Transport.Tests;

public sealed class AuthTranscriptV2Tests
{
    private static readonly Guid Session = new("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Server = new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid Client = new("99999999-8888-7777-6666-555555555555");
    private static readonly Guid Other = new("01234567-89ab-cdef-0123-456789abcdef");
    private static byte[] Bytes(int start, int count) => Enumerable.Range(start, count).Select(i => (byte)i).ToArray();
    private static byte[] Pin() => Enumerable.Repeat((byte)0xaa, 32).ToArray();

    // 独立规范字面量；不是调用被测 serializer 构造期望。
    private static string Literal(Guid session, bool offer) =>
        "{\"type\":\"display_catalog_" + (offer ? "offer" : "accept") +
        "\",\"applicationProfile\":2,\"catalogSchema\":1,\"sessionId\":\"" + session.ToString("D") + "\"}";

    private static DisplayCatalogAgreementFrame Parse(Guid session, bool offer, string? text = null)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text ?? Literal(session, offer));
        DisplayCatalogAgreementFrame? result;
        string? rejection;
        bool parsed = offer
            ? DisplayCatalogAgreementFrame.TryParseOffer(bytes, session, out result, out rejection)
            : DisplayCatalogAgreementFrame.TryParseAccept(bytes, session, out result, out rejection);
        Assert.True(parsed, rejection);
        return Assert.IsType<DisplayCatalogAgreementFrame>(result);
    }

    private static DisplayCatalogAgreementBinding Binding(Guid? session = null) =>
        DisplayCatalogAgreementBinding.Create(session ?? Session, Parse(session ?? Session, true), Parse(session ?? Session, false));

    private static AuthTranscriptV2 Create(SessionPermission requested = SessionPermission.Control) =>
        AuthTranscriptV2.CreateClient(Session, Server, Client, Bytes(0, 32), Bytes(32, 32), Pin(), requested, Binding());

    private static string[] ExpectedFields() =>
    [
        "LANREMOTE-AUTH-V2", "11111111-2222-3333-4444-555555555555", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
        "99999999-8888-7777-6666-555555555555", "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=",
        "ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8=", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "control",
        "display_catalog_offer", "2", "1", "11111111-2222-3333-4444-555555555555",
        "display_catalog_accept", "2", "1", "11111111-2222-3333-4444-555555555555",
    ];

    // 独立 Python 实际输出，见 scripts/reference/gen-auth-v2-golden-vectors.py。
    [Theory]
    [InlineData(false, false, "7DE729D391FCE96B86997E544C7E4731A60BC5FA8115A0DC30901CC01AB65807", "D4520FBA3C6D3005D568BAD6C24B4287F9736D51F69B2F1199EE14FFACB7F3E9", "66EA6C3EEA9B8D18DC3F0548674029E8C77AC16137EAA2DF327CBC682E8DA09F")]
    [InlineData(false, true, "7DE729D391FCE96B86997E544C7E4731A60BC5FA8115A0DC30901CC01AB65807", "D4520FBA3C6D3005D568BAD6C24B4287F9736D51F69B2F1199EE14FFACB7F3E9", "37E7B23436BB21CD5B4A55E167F15134399F1A8DDED5D80FB6D365801AF6FC9E")]
    [InlineData(true, true, "CC6C8F43460C5C300DE725D194F32A86D413DB6F5DE007BE0966D9548E0551C6", "6A8AE64C58BE71B44750986D8F1A99B8C9B427220E16E31A81D1C19E0F4343E1", "BEC66287348D608F09248655DEDEBEC032254C9F05305D92B1B55388F8932D58")]
    public void Python_golden_vectors_match(bool second, bool grantView, string hash, string clientProof, string serverProof)
    {
        Guid session = second ? new Guid("0f8fad5b-d9cb-469f-a165-70867728950e") : Session;
        AuthTranscriptV2 value = second ? AuthTranscriptV2.CreateClient(session,
            new Guid("7c9e6679-7425-40de-944b-e07fc1f90ae7"), new Guid("3f2504e0-4f89-41d3-9a0c-0305e82c3301"),
            SHA256.HashData("server-nonce"u8), SHA256.HashData("client-nonce"u8), SHA256.HashData("cert-der"u8),
            SessionPermission.ViewOnly, Binding(session)) : Create();
        byte[] key = second ? Enumerable.Range(0, 16).Select(i => (byte)(255 - i)).ToArray() : Bytes(0, 16);
        SessionPermission granted = grantView ? SessionPermission.ViewOnly : SessionPermission.Control;
        Assert.Equal(second ? 415 : 418, value.ExportClientTranscript().Length);
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(value.ExportClientTranscript())));
        Assert.Equal(clientProof, Convert.ToHexString(value.ComputeClientProof(key)));
        Assert.Equal(serverProof, Convert.ToHexString(value.ComputeServerProof(key, granted)));
        byte[] grant = Encoding.ASCII.GetBytes("LANREMOTE-GRANT-V2\0" + hash + "\0" + (grantView ? "view" : "control"));
        Assert.Equal(grant, value.ExportGrantTranscript(granted));
    }

    [Fact]
    public void Sixteen_fields_have_exact_order_and_no_trailing_nul()
    {
        byte[] actual = Create().ExportClientTranscript();
        Assert.Equal(Encoding.ASCII.GetBytes(string.Join('\0', ExpectedFields())), actual);
        Assert.Equal(15, actual.Count(b => b == 0));
        Assert.NotEqual((byte)0, actual[^1]);
        Assert.All(actual, b => Assert.InRange(b, (byte)0, (byte)127));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)]
    [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(15)]
    public void Every_canonical_field_is_covered_by_client_mac(int index)
    {
        string[] expected = ExpectedFields();
        expected[index] += "x";
        byte[] changedMac = HMACSHA256.HashData(Bytes(0, 16), Encoding.ASCII.GetBytes(string.Join('\0', expected)));
        Assert.False(CryptographicOperations.FixedTimeEquals(Create().ComputeClientProof(Bytes(0, 16)), changedMac));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, true)] [InlineData(false, true)]
    public void Binding_rejects_wrong_direction(bool offerDirection, bool acceptDirection) =>
        Assert.Throws<ArgumentException>(() => DisplayCatalogAgreementBinding.Create(Session,
            Parse(Session, offerDirection), Parse(Session, acceptDirection)));

    [Theory]
    [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void Binding_rejects_either_wrong_session(bool wrongOffer, bool wrongAccept) =>
        Assert.Throws<ArgumentException>(() => DisplayCatalogAgreementBinding.Create(Session,
            Parse(wrongOffer ? Other : Session, true), Parse(wrongAccept ? Other : Session, false)));

    [Fact]
    public void Binding_rejects_empty_expected_session() =>
        Assert.Throws<ArgumentException>(() => DisplayCatalogAgreementBinding.Create(Guid.Empty, Parse(Session, true), Parse(Session, false)));

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Binding_rejects_null_frames(bool offer) =>
        Assert.Throws<ArgumentNullException>(() => DisplayCatalogAgreementBinding.Create(Session,
            offer ? null! : Parse(Session, true), offer ? Parse(Session, false) : null!));

    [Fact]
    public void Binding_is_repeatable_value_not_once_only_authorization()
    {
        DisplayCatalogAgreementFrame offer = Parse(Session, true), accept = Parse(Session, false);
        DisplayCatalogAgreementBinding a = DisplayCatalogAgreementBinding.Create(Session, offer, accept);
        DisplayCatalogAgreementBinding b = DisplayCatalogAgreementBinding.Create(Session, offer, accept);
        Assert.Equal(Session, a.SessionId);
        Assert.Equal(a.SessionId, b.SessionId);
        Assert.NotSame(a, b);
    }

    [Fact]
    public void Local_auth_context_must_match_binding() => Assert.Throws<ArgumentException>(() =>
        AuthTranscriptV2.CreateClient(Other, Server, Client, Bytes(0, 32), Bytes(32, 32), Pin(), SessionPermission.Control, Binding()));

    [Fact]
    public void Null_binding_is_rejected() => Assert.Throws<ArgumentNullException>(() =>
        AuthTranscriptV2.CreateClient(Session, Server, Client, Bytes(0, 32), Bytes(32, 32), Pin(), SessionPermission.Control, null!));

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void Empty_identity_is_rejected(int field) => Assert.Throws<ArgumentException>(() =>
        AuthTranscriptV2.CreateClient(field == 0 ? Guid.Empty : Session, field == 1 ? Guid.Empty : Server,
            field == 2 ? Guid.Empty : Client, Bytes(0, 32), Bytes(32, 32), Pin(), SessionPermission.Control, Binding()));

    [Theory]
    [InlineData(0, 0)] [InlineData(0, 31)] [InlineData(0, 33)]
    [InlineData(1, 0)] [InlineData(1, 31)] [InlineData(1, 33)]
    [InlineData(2, 0)] [InlineData(2, 31)] [InlineData(2, 33)]
    public void Fixed_byte_fields_reject_wrong_lengths(int field, int length) => Assert.Throws<ArgumentException>(() =>
        AuthTranscriptV2.CreateClient(Session, Server, Client,
            field == 0 ? new byte[length] : Bytes(0, 32), field == 1 ? new byte[length] : Bytes(32, 32),
            field == 2 ? new byte[length] : Pin(), SessionPermission.Control, Binding()));

    [Theory]
    [InlineData(0)] [InlineData(15)] [InlineData(17)]
    public void Both_proofs_reject_wrong_key_lengths(int length)
    {
        AuthTranscriptV2 value = Create();
        Assert.Throws<ArgumentException>(() => value.ComputeClientProof(new byte[length]));
        Assert.Throws<ArgumentException>(() => value.ComputeServerProof(new byte[length], SessionPermission.ViewOnly));
    }

    [Theory]
    [InlineData(-1)] [InlineData(2)] [InlineData(255)]
    public void Unknown_permission_is_rejected(int permission)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create((SessionPermission)permission));
        AuthTranscriptV2 value = Create();
        Assert.Throws<ArgumentOutOfRangeException>(() => value.ExportGrantTranscript((SessionPermission)permission));
        Assert.Throws<ArgumentOutOfRangeException>(() => value.ComputeServerProof(Bytes(0, 16), (SessionPermission)permission));
    }

    [Fact]
    public void View_request_cannot_be_granted_control()
    {
        AuthTranscriptV2 value = Create(SessionPermission.ViewOnly);
        Assert.Throws<ArgumentException>(() => value.ExportGrantTranscript(SessionPermission.Control));
        Assert.Throws<ArgumentException>(() => value.ComputeServerProof(Bytes(0, 16), SessionPermission.Control));
    }

    [Fact]
    public void Grant_hashes_entire_client_and_binds_actual_permission_and_server_prefix()
    {
        AuthTranscriptV2 value = Create();
        byte[] key = Bytes(0, 16);
        foreach (SessionPermission granted in new[] { SessionPermission.Control, SessionPermission.ViewOnly })
        {
            string permission = granted == SessionPermission.Control ? "control" : "view";
            byte[] expected = Encoding.ASCII.GetBytes("LANREMOTE-GRANT-V2\0" +
                Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(string.Join('\0', ExpectedFields())))) + "\0" + permission);
            Assert.Equal(expected, value.ExportGrantTranscript(granted));
            byte[] prefixed = Encoding.ASCII.GetBytes("server\0").Concat(expected).ToArray();
            Assert.Equal(HMACSHA256.HashData(key, prefixed), value.ComputeServerProof(key, granted));
            Assert.False(CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, expected), value.ComputeServerProof(key, granted)));
        }
        Assert.False(CryptographicOperations.FixedTimeEquals(value.ComputeServerProof(key, SessionPermission.Control),
            value.ComputeServerProof(key, SessionPermission.ViewOnly)));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void Changing_each_auth_input_changes_both_proofs(int field)
    {
        Guid session = field == 0 ? Other : Session;
        AuthTranscriptV2 changed = AuthTranscriptV2.CreateClient(session, field == 1 ? Other : Server, field == 2 ? Other : Client,
            field == 3 ? Bytes(1, 32) : Bytes(0, 32), field == 4 ? Bytes(33, 32) : Bytes(32, 32),
            field == 5 ? new byte[32] : Pin(), field == 6 ? SessionPermission.ViewOnly : SessionPermission.Control, Binding(session));
        AuthTranscriptV2 original = Create();
        Assert.False(CryptographicOperations.FixedTimeEquals(original.ComputeClientProof(Bytes(0, 16)), changed.ComputeClientProof(Bytes(0, 16))));
        Assert.False(CryptographicOperations.FixedTimeEquals(original.ComputeServerProof(Bytes(0, 16), SessionPermission.ViewOnly),
            changed.ComputeServerProof(Bytes(0, 16), SessionPermission.ViewOnly)));
    }

    [Fact]
    public void Caller_mutation_and_export_mutation_cannot_change_private_transcript()
    {
        byte[] sn = Bytes(0, 32), cn = Bytes(32, 32), pin = Pin(), key = Bytes(0, 16);
        AuthTranscriptV2 value = AuthTranscriptV2.CreateClient(Session, Server, Client, sn, cn, pin, SessionPermission.Control, Binding());
        byte[] client = value.ExportClientTranscript(), grant = value.ExportGrantTranscript(SessionPermission.ViewOnly);
        byte[] cp = value.ComputeClientProof(key), sp = value.ComputeServerProof(key, SessionPermission.ViewOnly);
        Assert.Equal(Bytes(0, 32), sn);
        Assert.Equal(Bytes(32, 32), cn);
        Assert.Equal(Pin(), pin);
        Assert.Equal(Bytes(0, 16), key);
        Array.Fill(sn, (byte)255); Array.Fill(cn, (byte)255); Array.Fill(pin, (byte)255);
        Array.Fill(client, (byte)255); Array.Fill(grant, (byte)255);
        Assert.Equal(cp, value.ComputeClientProof(key));
        Assert.Equal(sp, value.ComputeServerProof(key, SessionPermission.ViewOnly));
        Array.Fill(cp, (byte)255); Array.Fill(sp, (byte)255);
        Assert.Equal(Create().ComputeClientProof(key), value.ComputeClientProof(key));
        Assert.Equal(Create().ComputeServerProof(key, SessionPermission.ViewOnly), value.ComputeServerProof(key, SessionPermission.ViewOnly));
        Assert.NotSame(value.ExportClientTranscript(), value.ExportClientTranscript());
        Assert.NotSame(value.ExportGrantTranscript(SessionPermission.ViewOnly), value.ExportGrantTranscript(SessionPermission.ViewOnly));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Semantic_json_variants_produce_same_binding(bool offer)
    {
        string text = " { \"sessionId\" : \"" + Session + "\", \"catalogSchema\" : 1, \"applicationProfile\" : 2, \"type\" : \"display_catalog_" +
            (offer ? "o\\u0066fer" : "a\\u0063cept") + "\" } \r\n";
        DisplayCatalogAgreementBinding binding = DisplayCatalogAgreementBinding.Create(Session,
            Parse(Session, true, offer ? text : null), Parse(Session, false, offer ? null : text));
        AuthTranscriptV2 value = AuthTranscriptV2.CreateClient(Session, Server, Client, Bytes(0, 32), Bytes(32, 32), Pin(), SessionPermission.Control, binding);
        Assert.Equal(Create().ExportClientTranscript(), value.ExportClientTranscript());
    }

    [Theory]
    [InlineData(true, "applicationProfile", "1")] [InlineData(false, "applicationProfile", "1")]
    [InlineData(true, "applicationProfile", "3")] [InlineData(false, "applicationProfile", "3")]
    [InlineData(true, "catalogSchema", "2")] [InlineData(false, "catalogSchema", "2")]
    [InlineData(true, "catalogSchema", "0")] [InlineData(false, "catalogSchema", "0")]
    public void Noncanonical_contract_cannot_enter_binding(bool offer, string field, string value)
    {
        string original = field == "applicationProfile" ? "2" : "1";
        byte[] bytes = Encoding.UTF8.GetBytes(Literal(Session, offer).Replace("\"" + field + "\":" + original, "\"" + field + "\":" + value, StringComparison.Ordinal));
        bool ok = offer ? DisplayCatalogAgreementFrame.TryParseOffer(bytes, Session, out _, out _)
                        : DisplayCatalogAgreementFrame.TryParseAccept(bytes, Session, out _, out _);
        Assert.False(ok);
    }

    [Fact]
    public void V1_remains_unchanged_and_cannot_match_v2_proofs()
    {
        Assert.Equal("LANREMOTE-AUTH-V1", AuthProtocol.ClientTranscriptDomain);
        Assert.Equal("LANREMOTE-GRANT-V1", AuthProtocol.GrantTranscriptDomain);
        Assert.Equal(1, AuthProtocol.WireProtocolVersion);
        byte[] ct = AuthTranscriptBuilder.BuildClientTranscript(Session, Server, Client, Bytes(0, 32), Bytes(32, 32), Pin(), SessionPermission.Control);
        Assert.Equal("9C8DDCED05BDC9A75AE14254B82AFB9B8249B6740A8E1C9BBE1D114265783B69", Convert.ToHexString(AuthTranscriptBuilder.ComputeClientProof(Bytes(0, 16), ct)));
        Assert.False(CryptographicOperations.FixedTimeEquals(AuthTranscriptBuilder.ComputeClientProof(Bytes(0, 16), ct), Create().ComputeClientProof(Bytes(0, 16))));
        byte[] gt = AuthTranscriptBuilder.BuildGrantTranscript(ct, SessionPermission.Control);
        Assert.False(CryptographicOperations.FixedTimeEquals(AuthTranscriptBuilder.ComputeServerProof(Bytes(0, 16), gt), Create().ComputeServerProof(Bytes(0, 16), SessionPermission.Control)));
    }

    [Theory]
    [InlineData("tr-TR")] [InlineData("ar-SA")] [InlineData("zh-CN")]
    public void Canonical_bytes_are_culture_independent(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(Encoding.ASCII.GetBytes(string.Join('\0', ExpectedFields())), Create().ExportClientTranscript());
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Api_has_no_raw_transcript_import_or_public_constructor()
    {
        foreach (Type type in new[] { typeof(AuthTranscriptV2), typeof(DisplayCatalogAgreementBinding) })
        {
            Assert.True(type.IsSealed);
            Assert.DoesNotContain(type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), c => !c.IsPrivate);
            Assert.All(type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), field => Assert.True(field.IsInitOnly));
        }
        MethodInfo factory = Assert.Single(typeof(AuthTranscriptV2).GetMethods(BindingFlags.Static | BindingFlags.NonPublic),
            m => m.ReturnType == typeof(AuthTranscriptV2));
        Assert.Equal("CreateClient", factory.Name);
        Assert.Equal(typeof(DisplayCatalogAgreementBinding), factory.GetParameters()[^1].ParameterType);
        Assert.All(typeof(AuthTranscriptV2).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(m => m.Name.StartsWith("Compute", StringComparison.Ordinal)), m =>
            Assert.Equal("accessKeyBytes", Assert.Single(m.GetParameters(), p => p.ParameterType == typeof(ReadOnlySpan<byte>)).Name));
    }
}
