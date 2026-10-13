using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LanRemote.Core.Models;

namespace LanRemote.Transport.Auth;

/// <summary>新版 Control 认证消费的强类型 V2 字节核；不改变独立 Video 协议版本。</summary>
/// <remarks>
/// 固定16个ASCII字段以15个NUL分隔且无尾NUL：AUTH-V2域、既有7字段、Offer四字段、Accept四字段。
/// 输入必须来自同一连接冻结的身份、nonce和证书摘要；本类不能证明来源或真实收发。
/// 不接受外部 transcript 字节构造 grant/proof，导出均为副本。清理前可重复使用，不是授权令牌。
/// Clear 与导出/计算同锁，清理为不可逆终态；已导出副本仍由调用者负责清理。
/// 私有 transcript 不含访问密钥或 proof；托管字符串和框架内部副本不承诺清零。
/// </remarks>
internal sealed class AuthTranscriptV2
{
    internal const string ClientDomain = "LANREMOTE-AUTH-V2";
    internal const string GrantDomain = "LANREMOTE-GRANT-V2";
    private readonly byte[] _client;
    private readonly SessionPermission _requestedPermission;
    private readonly object _sync = new();
    private bool _cleared;

    private AuthTranscriptV2(byte[] client, SessionPermission requestedPermission)
    {
        _client = client;
        _requestedPermission = requestedPermission;
    }

    internal static AuthTranscriptV2 CreateClient(Guid sessionId, Guid serverDeviceId, Guid clientDeviceId,
        ReadOnlySpan<byte> serverNonce, ReadOnlySpan<byte> clientNonce, ReadOnlySpan<byte> certificateSha256,
        SessionPermission requestedPermission, DisplayCatalogAgreementBinding agreement)
    {
        ArgumentNullException.ThrowIfNull(agreement);
        RequireNonempty(sessionId, nameof(sessionId));
        RequireNonempty(serverDeviceId, nameof(serverDeviceId));
        RequireNonempty(clientDeviceId, nameof(clientDeviceId));
        if (agreement.SessionId != sessionId)
            throw new ArgumentException("约定与认证会话不匹配。", nameof(agreement));
        RequireLength(serverNonce, AuthProtocol.NonceByteLength, nameof(serverNonce));
        RequireLength(clientNonce, AuthProtocol.NonceByteLength, nameof(clientNonce));
        RequireLength(certificateSha256, AuthProtocol.CertificateSha256ByteLength, nameof(certificateSha256));
        string permission = AuthProtocol.EncodePermission(requestedPermission);
        string session = sessionId.ToString("D");
        string profile = DisplayCatalogAgreementFrame.ApplicationProfile.ToString(CultureInfo.InvariantCulture);
        string schema = DisplayCatalogAgreementFrame.CatalogSchema.ToString(CultureInfo.InvariantCulture);
        string transcript = string.Join('\0', ClientDomain, session, serverDeviceId.ToString("D"),
            clientDeviceId.ToString("D"), Convert.ToBase64String(serverNonce), Convert.ToBase64String(clientNonce),
            Convert.ToHexString(certificateSha256), permission,
            DisplayCatalogAgreementFrame.OfferType, profile, schema, session,
            DisplayCatalogAgreementFrame.AcceptType, profile, schema, session);
        return new AuthTranscriptV2(Encoding.UTF8.GetBytes(transcript), requestedPermission);
    }

    internal byte[] ExportClientTranscript()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_cleared, this);
            return _client.ToArray();
        }
    }

    internal void Clear()
    {
        lock (_sync)
        {
            if (_cleared) return;
            CryptographicOperations.ZeroMemory(_client);
            _cleared = true;
        }
    }

    internal byte[] ExportGrantTranscript(SessionPermission grantedPermission)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_cleared, this);
            return ExportGrantTranscriptCore(grantedPermission);
        }
    }

    // 调用方持有 _sync；不得单独用于绕过清理终态。
    private byte[] ExportGrantTranscriptCore(SessionPermission grantedPermission)
    {
        string permission = AuthProtocol.EncodePermission(grantedPermission);
        if (_requestedPermission == SessionPermission.ViewOnly && grantedPermission == SessionPermission.Control)
            throw new ArgumentException("授予权限不得超出请求权限。", nameof(grantedPermission));
        return Encoding.UTF8.GetBytes(string.Join('\0', GrantDomain,
            Convert.ToHexString(SHA256.HashData(_client)), permission));
    }

    internal byte[] ComputeClientProof(ReadOnlySpan<byte> accessKeyBytes)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_cleared, this);
            RequireLength(accessKeyBytes, AccessSecret.AccessKeyByteLength, nameof(accessKeyBytes));
            return HMACSHA256.HashData(accessKeyBytes, _client);
        }
    }

    internal byte[] ComputeServerProof(ReadOnlySpan<byte> accessKeyBytes, SessionPermission grantedPermission)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_cleared, this);
            RequireLength(accessKeyBytes, AccessSecret.AccessKeyByteLength, nameof(accessKeyBytes));
            byte[] grant = ExportGrantTranscriptCore(grantedPermission);
            byte[]? message = null;
            try
            {
                message = new byte["server\0"u8.Length + grant.Length];
                "server\0"u8.CopyTo(message);
                grant.CopyTo(message.AsSpan("server\0"u8.Length));
                return HMACSHA256.HashData(accessKeyBytes, message);
            }
            finally
            {
                if (message is not null) CryptographicOperations.ZeroMemory(message);
                CryptographicOperations.ZeroMemory(grant);
            }
        }
    }

    private static void RequireNonempty(Guid value, string name)
    {
        if (value == Guid.Empty) throw new ArgumentException("标识不可为空。", name);
    }

    private static void RequireLength(ReadOnlySpan<byte> value, int expected, string name)
    {
        if (value.Length != expected) throw new ArgumentException($"长度必须为 {expected} 字节。", name);
    }
}
