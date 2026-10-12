using System.Net;
using System.Reflection;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

public sealed class ChallengeV2AdmissionTests
{
    private static readonly Guid Session = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
    private static readonly Guid Device = Guid.Parse("fedcba98-7654-3210-fedc-ba9876543210");
    private static readonly Guid Other = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static byte[] Pin => Enumerable.Range(160, 32).Select(i => (byte)i).ToArray();
    private delegate AuthChallengeV2 Admission(ReadOnlySpan<byte> payload, ConnectionIdentity identity);

    // Reflection permits the same test bytes to run red against the prior implementation;
    // no stub, production fallback or non-compiling red build is needed.
    private static Admission Entry()
    {
        var method = typeof(AuthChallengeV2).GetMethod("ValidateForConnection", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.True(method.IsAssembly);
        return method.CreateDelegate<Admission>();
    }

    private static ConnectionIdentity Identity(bool matchingPin = true)
    {
        Assert.True(ConnectionTarget.TryCreate(Device, IPAddress.Loopback, 45873, Convert.ToHexString(Pin), out var target));
        byte[] actual = Pin;
        if (!matchingPin) actual[0] ^= 255;
        Assert.True(ConnectionIdentity.TryCreate(target!, actual, out var identity));
        return identity!;
    }

    private static byte[] Payload(int version = 2, Guid? device = null, byte[]? pin = null, Guid? session = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "auth_challenge", protocol = version, sessionId = (session ?? Session).ToString("D"),
            serverDeviceId = (device ?? Device).ToString("D"),
            serverNonce = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()),
            certSha256 = Convert.ToHexString(pin ?? Pin), expiresInMs = 15000,
        });

    private static void Reject(byte[] bytes, string reason, ConnectionIdentity? identity = null)
    {
        var entry = Entry();
        var error = Assert.Throws<ControlClientAuthenticationException>(() => entry(bytes, identity ?? Identity()));
        Assert.Equal(reason, error.Rejection);
        Assert.DoesNotContain(Convert.ToHexString(Pin), error.Message);
        Assert.DoesNotContain(Device.ToString("D"), error.Message);
    }

    [Fact]
    public void Matching_V2_returns_independent_challenge_not_session_authority()
    {
        byte[] bytes = Payload();
        var entry = Entry();
        var a = entry(bytes, Identity());
        var b = entry(bytes, Identity());
        Assert.NotSame(a, b);
        Assert.Equal(Session, a.SessionId);
        Assert.Equal(Device, a.ServerDeviceId);
        Assert.Equal(15000, a.ExpiresInMs);
        Array.Clear(bytes);
        a.CopyCertificateSha256()[0] ^= 255;
        a.CopyServerNonce()[0] ^= 255;
        Assert.Equal(Pin, a.CopyCertificateSha256());
        Assert.Equal(Pin, b.CopyCertificateSha256());
        Assert.Equal(Enumerable.Range(0, 32).Select(i => (byte)i), a.CopyServerNonce());
    }

    [Theory]
    [InlineData(1, "client-upgrade-required")]
    [InlineData(3, "client-protocol-unsupported")]
    [InlineData(int.MaxValue, "client-protocol-unsupported")]
    public void Only_matching_V2_can_be_admitted(int version, string reason) => Reject(Payload(version), reason);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Device_mismatch_precedes_version_and_payload_pin_mismatch(int version) =>
        Reject(Payload(version, Other, new byte[32]), "client-server-device-mismatch");

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Actual_TLS_pin_mismatch_precedes_version(int version) =>
        Reject(Payload(version, pin: new byte[32]), "client-challenge-pin-mismatch");

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(0)]
    public void TLS_identity_pin_failure_is_not_relabelled_as_protocol_or_upgrade(int version)
    {
        var entry = Entry();
        var identity = Identity(false);
        // Matching the payload to the presented pin cannot rescue a TLS pin mismatch.
        byte[] bytes = version == 0 ? [] : Payload(version, Other, identity.PresentedCertSha256.ToArray());
        Assert.Throws<AuthenticationException>(() => entry(bytes, identity));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Entire_frame_must_be_valid_before_identity_or_version_classification(int version)
    {
        string json = Encoding.UTF8.GetString(Payload(version, Other, new byte[32]));
        foreach (string bad in new[]
        {
            json[..^1], json + "{}", json[..^1] + ",\"extra\":0}",
            json[..^1] + ",\"protoc\\u006fl\":1}",
            json.Replace("15000", "1e0"), json.Replace("15000", "null"),
            json.Replace("auth_challenge", "authentication_failed"), json.PadRight(4097),
            json.Replace("serverDeviceId", "serverDeviceXx"),
        }) Reject(Encoding.UTF8.GetBytes(bad), "client-challenge-invalid");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Truncation_at_every_byte_is_never_upgrade_evidence(int version)
    {
        byte[] bytes = Payload(version);
        for (int i = 0; i < bytes.Length; i++) Reject(bytes[..i], "client-challenge-invalid");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Invalid_UTF8_lone_surrogates_and_BOM_cannot_announce_legacy(int version)
    {
        byte[] bytes = Payload(version);
        bytes[2] = 0xFF;
        Reject(bytes, "client-challenge-invalid");
        Reject(Encoding.UTF8.GetBytes("\uFEFF" + Encoding.UTF8.GetString(Payload(version))), "client-challenge-invalid");
        Reject(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Payload(version)).Replace(Device.ToString("D"), "\\uD800")), "client-challenge-invalid");
    }

    [Fact]
    public void Legacy_zero_guid_contract_is_preserved_but_identity_still_precedes_upgrade()
    {
        byte[] both = Payload(1, Guid.Empty, session: Guid.Empty);
        Assert.Equal(WireV2ParseStatus.LegacyV1, AuthChallengeV2.Parse(both, out var frame));
        Assert.Null(frame);
        Reject(both, "client-server-device-mismatch");
        Reject(Payload(1, session: Guid.Empty), "client-upgrade-required");
        Reject(Payload(1, pin: new byte[32], session: Guid.Empty), "client-challenge-pin-mismatch");
        Reject(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(both) + "{}"), "client-challenge-invalid");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Nonlegacy_zero_guid_remains_invalid_not_version_or_identity_evidence(int version)
    {
        Reject(Payload(version, Guid.Empty), "client-challenge-invalid");
        Reject(Payload(version, session: Guid.Empty), "client-challenge-invalid");
        Reject(Payload(version, Guid.Empty, session: Guid.Empty), "client-challenge-invalid");
    }

    [Theory]
    [InlineData(1, "client-upgrade-required")]
    [InlineData(3, "client-protocol-unsupported")]
    public void Reordering_and_equivalent_escapes_do_not_bypass_bound_admission(int version, string reason)
    {
        using var doc = JsonDocument.Parse(Payload(version));
        string json = "{" + string.Join(",", doc.RootElement.EnumerateObject().Reverse().Select(p =>
            JsonSerializer.Serialize(p.Name) + ":" + p.Value.GetRawText())) + "}";
        json = json.Replace("serverDeviceId", "server\\u0044eviceId");
        Reject(Encoding.UTF8.GetBytes(" \r\n" + json + "\t"), reason);
    }

    [Theory]
    [InlineData(4095)]
    [InlineData(4096)]
    public void Exact_bounded_valid_V2_is_accepted(int length)
    {
        var entry = Entry();
        byte[] bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Payload()).PadRight(length));
        Assert.Equal(Session, entry(bytes, Identity()).SessionId);
    }

    [Fact]
    public void Input_null_identity_is_programming_error_and_live_wire_is_not_switched()
    {
        var entry = Entry();
        Assert.Throws<ArgumentNullException>(() => entry(Payload(), null!));
        Assert.Equal(1, AuthProtocol.WireProtocolVersion);
        Assert.Equal(1, HelloFrame.ExpectedProtocol);
    }
}
