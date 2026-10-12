using System.Buffers;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;

namespace LanRemote.Transport.Auth;

/// <summary>独立 Challenge2 纯载荷，只有 AcceptedV2 返回对象，绝不从 V1 转换或降级。</summary>
/// <remarks>
/// 版本声明不证明真实软件版本；nonce/pin 形状不证明随机性或与当前 TLS 证书相符。
/// 本类不接触流、时钟、密钥或审批，不授予权限。有效期提示仅供未来消费方收窄绝对预算。
/// </remarks>
internal sealed class AuthChallengeV2
{
    internal const int Protocol = 2;
    internal const int MaxPayloadByteLength = WireV2Json.MaxPayloadByteLength;
    private static readonly string[] Fields =
        ["type", "protocol", "sessionId", "serverDeviceId", "serverNonce", "certSha256", "expiresInMs"];
    private readonly byte[] _serverNonce;
    private readonly byte[] _certificateSha256;

    internal AuthChallengeV2(Guid sessionId, Guid serverDeviceId, ReadOnlySpan<byte> serverNonce,
        ReadOnlySpan<byte> certificateSha256, int expiresInMs)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("会话标识不可为空。", nameof(sessionId));
        if (serverDeviceId == Guid.Empty) throw new ArgumentException("设备标识不可为空。", nameof(serverDeviceId));
        if (serverNonce.Length != 32) throw new ArgumentException("nonce 必须为32字节。", nameof(serverNonce));
        if (certificateSha256.Length != 32) throw new ArgumentException("证书摘要必须为32字节。", nameof(certificateSha256));
        if (expiresInMs < 1) throw new ArgumentOutOfRangeException(nameof(expiresInMs));
        SessionId = sessionId;
        ServerDeviceId = serverDeviceId;
        _serverNonce = serverNonce.ToArray();
        _certificateSha256 = certificateSha256.ToArray();
        ExpiresInMs = expiresInMs;
    }

    internal Guid SessionId { get; }
    internal Guid ServerDeviceId { get; }
    internal int ExpiresInMs { get; }
    internal byte[] CopyServerNonce() => _serverNonce.ToArray();
    internal byte[] CopyCertificateSha256() => _certificateSha256.ToArray();

    /// <summary>新数组，仅 UTF-8 JSON，不带长度前缀。</summary>
    internal byte[] Serialize()
    {
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "auth_challenge");
            writer.WriteNumber("protocol", Protocol);
            writer.WriteString("sessionId", SessionId.ToString("D"));
            writer.WriteString("serverDeviceId", ServerDeviceId.ToString("D"));
            writer.WriteString("serverNonce", Convert.ToBase64String(_serverNonce));
            writer.WriteString("certSha256", Convert.ToHexString(_certificateSha256));
            writer.WriteNumber("expiresInMs", ExpiresInMs);
            writer.WriteEndObject();
        }
        return output.WrittenSpan.ToArray();
    }

    internal static WireV2ParseStatus Parse(ReadOnlySpan<byte> utf8, out AuthChallengeV2? frame) =>
        ParseCore(utf8, null, out frame, out _);

    /// <summary>
    /// 未来客户端的身份绑定消费入口；只有完整且匹配本连接的 V2 返回载荷对象。
    /// 不执行 I/O、计时、proof 或审批，不创建认证会话；live 调用方仍须在消费前后检查原 deadline。
    /// TLS pin 失败保持原异常类别；完整载荷的设备和实际 TLS pin 检查优先于版本提示。
    /// </summary>
    internal static AuthChallengeV2 ValidateForConnection(ReadOnlySpan<byte> utf8, ConnectionIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!identity.PinsMatch)
            throw new AuthenticationException("TLS 连接的实际证书指纹与冻结目标不一致。");

        var status = ParseCore(utf8, identity, out var frame, out string? identityRejection);
        if (identityRejection == "client-server-device-mismatch")
            throw new ControlClientAuthenticationException(identityRejection, "远端设备身份与连接目标不一致。");
        if (identityRejection == "client-challenge-pin-mismatch")
            throw new ControlClientAuthenticationException(identityRejection, "远端认证证书与实际 TLS 连接不一致。");
        return status switch
        {
            WireV2ParseStatus.AcceptedV2 => frame!,
            WireV2ParseStatus.LegacyV1 => throw new ControlClientAuthenticationException(
                "client-upgrade-required", "远端声明使用旧版协议；仅支持新版双方连接，请将两端升级后重试。"),
            WireV2ParseStatus.UnsupportedVersion => throw new ControlClientAuthenticationException(
                "client-protocol-unsupported", "双方协议版本不兼容，请将两端升级至兼容版本后重试。"),
            _ => throw new ControlClientAuthenticationException(
                "client-challenge-invalid", "远端认证 challenge 无效或顺序错误。"),
        };
    }

    private static WireV2ParseStatus ParseCore(ReadOnlySpan<byte> utf8, ConnectionIdentity? identity,
        out AuthChallengeV2? frame, out string? identityRejection)
    {
        frame = null;
        identityRejection = null;
        if (utf8.Length is < 1 or > MaxPayloadByteLength) return WireV2ParseStatus.Invalid;
        var reader = WireV2Json.Reader(utf8);
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return WireV2ParseStatus.Invalid;
            int seen = 0, protocol = 0, expires = 0;
            Guid session = Guid.Empty, device = Guid.Empty;
            byte[] nonce = [], pin = [];
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                int field = WireV2Json.ReadField(ref reader, Fields, ref seen);
                if (field < 0 || !reader.Read()) return WireV2ParseStatus.Invalid;
                switch (field)
                {
                    case 0:
                        if (reader.TokenType != JsonTokenType.String || !reader.ValueTextEquals("auth_challenge")) return WireV2ParseStatus.Invalid;
                        break;
                    case 1:
                        if (!WireV2Json.PositiveInteger(ref reader, out protocol)) return WireV2ParseStatus.Invalid;
                        break;
                    case 2:
                        if (!CanonicalGuid.TryParse(WireV2Json.BoundedString(ref reader, 36), out session)) return WireV2ParseStatus.Invalid;
                        break;
                    case 3:
                        if (!CanonicalGuid.TryParse(WireV2Json.BoundedString(ref reader, 36), out device)) return WireV2ParseStatus.Invalid;
                        break;
                    case 4:
                        if (!CanonicalBase64.TryDecode(WireV2Json.BoundedString(ref reader, 44), out nonce) || nonce.Length != 32) return WireV2ParseStatus.Invalid;
                        break;
                    case 5:
                        if (!CanonicalHex.TryDecode(WireV2Json.BoundedString(ref reader, 64), out pin) || pin.Length != 32) return WireV2ParseStatus.Invalid;
                        break;
                    case 6:
                        if (!WireV2Json.PositiveInteger(ref reader, out expires)) return WireV2ParseStatus.Invalid;
                        break;
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject || seen != 127 || reader.Read()) return WireV2ParseStatus.Invalid;
            // V1 的 canonical 零 Guid 属于其历史值域；其余版本先完成非空语义校验。
            if (protocol != 1 && (session == Guid.Empty || device == Guid.Empty)) return WireV2ParseStatus.Invalid;
            // 只在整个已知载荷有效后消费身份字段；不创建任何 V1/未知版本对象。
            if (identity is not null)
            {
                if (device != identity.DeviceId)
                {
                    identityRejection = "client-server-device-mismatch";
                    return WireV2ParseStatus.Invalid;
                }
                if (!CryptographicOperations.FixedTimeEquals(pin, identity.PresentedCertSha256.Span))
                {
                    identityRejection = "client-challenge-pin-mismatch";
                    return WireV2ParseStatus.Invalid;
                }
            }
            if (protocol == 1) return WireV2ParseStatus.LegacyV1;
            if (protocol != Protocol) return WireV2ParseStatus.UnsupportedVersion;
            frame = new AuthChallengeV2(session, device, nonce, pin, expires);
            return WireV2ParseStatus.AcceptedV2;
        }
        catch (JsonException) { return WireV2ParseStatus.Invalid; }
        catch (InvalidOperationException) { return WireV2ParseStatus.Invalid; }
    }
}
