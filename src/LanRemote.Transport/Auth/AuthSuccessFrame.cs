using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LanRemote.Core.Models;

namespace LanRemote.Transport.Auth;

/// <summary>
/// 服务端 → 客户端：<c>auth_success</c> 帧（M4 阶段 2 步骤 11）。
/// </summary>
/// <remarks>
/// <para>字段集以权威规格 <c>04_PROTOCOL_AND_SECURITY.md</c> §9 的样本为准：
/// <c>type</c> / <c>grantedPermission</c>（<c>view</c> | <c>control</c>）/
/// <c>serverProof</c>（base64 canonical，32 字节）/ <c>sessionToken</c>（base64 canonical，
/// 32 字节）/ <c>videoAttachExpiresInMs</c>（M5 视频重连窗口，毫秒）。</para>
/// <para><b>客户端必须先验 serverProof 再使用 sessionToken</b>（规格 04 §9：验证失败立即断开、
/// UI 显示「远端身份验证失败」、不发送输入）。验证逻辑（重建 grant 档 + HMAC +
/// FixedTimeEquals）在阶段 4；本帧只负责「字段存在且是 canonical 形状」。</para>
/// <para><b>videoAttachExpiresInMs 只做「正值」判定</b>：与 challenge 的 expiresInMs 同理——
/// 上界不在帧层发明。</para>
/// </remarks>
public sealed class AuthSuccessFrame
{
    /// <summary>期望的 <c>type</c>。</summary>
    public const string ExpectedType = AuthProtocol.TypeAuthSuccess;

    /// <summary>不是合法 JSON / 编码非法 / 结构超深。</summary>
    public const string RejectMalformedJson = "success-malformed-json";

    /// <summary>JSON 之后还有多余内容。</summary>
    public const string RejectTrailingData = "success-trailing-data";

    /// <summary>缺字段（五个字段都是必填）。</summary>
    public const string RejectMissingField = "success-missing-field";

    /// <summary><c>type</c> 不对（大小写也必须完全一致）。</summary>
    public const string RejectWrongType = "success-wrong-type";

    /// <summary><c>grantedPermission</c> 不是 <c>view</c> / <c>control</c>。</summary>
    public const string RejectBadGrantedPermission = "success-bad-granted-permission";

    /// <summary><c>serverProof</c> 不是 canonical base64，或不是恰好 32 字节。</summary>
    public const string RejectBadServerProof = "success-bad-server-proof";

    /// <summary><c>sessionToken</c> 不是 canonical base64，或不是恰好 32 字节。</summary>
    public const string RejectBadSessionToken = "success-bad-session-token";

    /// <summary><c>videoAttachExpiresInMs</c> 不是正数。</summary>
    public const string RejectBadVideoAttachExpiresInMs = "success-bad-video-attach-expires-in-ms";

    private readonly byte[] _serverProof;
    private readonly byte[] _sessionToken;

    /// <summary>
    /// 构造一个已校验的 success（服务端侧用）。
    /// </summary>
    /// <param name="grantedPermission">实际授予的权限。</param>
    /// <param name="serverProof">32 字节 HMAC-SHA256 证明（绑 grant transcript）。</param>
    /// <param name="sessionToken">32 字节会话令牌。</param>
    /// <param name="videoAttachExpiresInMs">视频重连窗口（毫秒，必须为正）。</param>
    /// <exception cref="ArgumentException">proof 或 token 长度不是 32 字节。</exception>
    /// <exception cref="ArgumentOutOfRangeException">窗口不是正数，或权限枚举未定义。</exception>
    public AuthSuccessFrame(
        SessionPermission grantedPermission,
        ReadOnlySpan<byte> serverProof,
        ReadOnlySpan<byte> sessionToken,
        int videoAttachExpiresInMs)
    {
        if (serverProof.Length != AuthProtocol.ProofByteLength)
        {
            throw new ArgumentException(
                $"serverProof 必须是 {AuthProtocol.ProofByteLength} 字节。", nameof(serverProof));
        }

        if (sessionToken.Length != AuthProtocol.SessionTokenByteLength)
        {
            throw new ArgumentException(
                $"sessionToken 必须是 {AuthProtocol.SessionTokenByteLength} 字节。", nameof(sessionToken));
        }

        if (videoAttachExpiresInMs < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(videoAttachExpiresInMs),
                videoAttachExpiresInMs,
                "videoAttachExpiresInMs 必须为正数（毫秒）。");
        }

        // 未定义枚举值立刻抛：与 Serialize 的编码共用同一判定，fail fast 不拖到序列化。
        _ = AuthProtocol.EncodePermission(grantedPermission);

        GrantedPermission = grantedPermission;
        _serverProof = serverProof.ToArray();
        _sessionToken = sessionToken.ToArray();
        VideoAttachExpiresInMs = videoAttachExpiresInMs;
    }

    /// <summary>实际授予的权限。</summary>
    public SessionPermission GrantedPermission { get; }

    /// <summary>32 字节 HMAC-SHA256 serverProof（绑 grant transcript）。</summary>
    public ReadOnlyMemory<byte> ServerProof => _serverProof;

    /// <summary>32 字节会话令牌（只存在内存；断开即废弃；不写日志、不持久化）。</summary>
    public ReadOnlyMemory<byte> SessionToken => _sessionToken;

    /// <summary>客户端验证失败或复制入会话后清理帧内 token，不扩展公开 API。</summary>
    internal void ClearSessionToken() => CryptographicOperations.ZeroMemory(_sessionToken);

    /// <summary>视频重连窗口（毫秒）。</summary>
    public int VideoAttachExpiresInMs { get; }

    /// <summary>
    /// 序列化（服务端侧发送方向）。
    /// </summary>
    /// <returns>UTF-8 JSON，不带长度前缀。</returns>
    public byte[] Serialize()
    {
        return JsonSerializer.SerializeToUtf8Bytes(
            new SuccessPayload
            {
                Type = ExpectedType,
                GrantedPermission = AuthProtocol.EncodePermission(GrantedPermission),
                ServerProof = Convert.ToBase64String(_serverProof),
                SessionToken = Convert.ToBase64String(_sessionToken),
                VideoAttachExpiresInMs = VideoAttachExpiresInMs,
            },
            AuthJson.StrictOptions);
    }

    /// <summary>
    /// 解析并校验一个 <c>auth_success</c> 载荷（客户端侧接收方向）。
    /// </summary>
    /// <param name="utf8">帧载荷（不含长度前缀）。</param>
    /// <param name="frame">成功时为已校验帧；失败为 <see langword="null"/>。</param>
    /// <param name="rejection">通过时为 <see langword="null"/>；否则是只用于本地日志的短码。</param>
    /// <returns>是否解析成功。</returns>
    public static bool TryParse(
        ReadOnlySpan<byte> utf8,
        out AuthSuccessFrame? frame,
        out string? rejection)
    {
        frame = null;

        if (!AuthJson.TryDeserializeStrict(
                utf8, out SuccessPayload? payload, out rejection,
                RejectMalformedJson, RejectTrailingData,
                ExpectedType, RejectWrongType))
        {
            return false;
        }

        // 顺序：先判 type（缺失 / 不对），再判其余字段存在性，最后逐字段判值（同 challenge）。
        if (payload.Type is null)
        {
            rejection = RejectMissingField;
            return false;
        }

        if (!string.Equals(payload.Type, ExpectedType, StringComparison.Ordinal))
        {
            rejection = RejectWrongType;
            return false;
        }

        if (payload.GrantedPermission is null
            || payload.ServerProof is null
            || payload.SessionToken is null
            || payload.VideoAttachExpiresInMs is null)
        {
            rejection = RejectMissingField;
            return false;
        }

        if (!AuthProtocol.TryDecodePermission(payload.GrantedPermission, out SessionPermission granted))
        {
            rejection = RejectBadGrantedPermission;
            return false;
        }

        bool validProof = CanonicalBase64.TryDecode(payload.ServerProof, out byte[] serverProof);
        // token 不经过通用解码器的堆临时副本；仍以 round-trip 相等判定 canonical。
        Span<byte> sessionToken = stackalloc byte[AuthProtocol.SessionTokenByteLength];
        Span<char> canonicalToken = stackalloc char[((AuthProtocol.SessionTokenByteLength + 2) / 3) * 4];
        try
        {
            if (!validProof || serverProof.Length != AuthProtocol.ProofByteLength)
            {
                rejection = RejectBadServerProof;
                return false;
            }

            if (!Convert.TryFromBase64String(payload.SessionToken, sessionToken, out int written)
                || written != AuthProtocol.SessionTokenByteLength
                || !Convert.TryToBase64Chars(sessionToken, canonicalToken, out int encoded)
                || !payload.SessionToken.AsSpan().SequenceEqual(canonicalToken[..encoded]))
            {
                rejection = RejectBadSessionToken;
                return false;
            }

            if (payload.VideoAttachExpiresInMs.Value < 1)
            {
                rejection = RejectBadVideoAttachExpiresInMs;
                return false;
            }

            frame = new AuthSuccessFrame(
                granted, serverProof, sessionToken, payload.VideoAttachExpiresInMs.Value);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sessionToken);
            canonicalToken.Clear();
            CryptographicOperations.ZeroMemory(serverProof);
        }
    }

    /// <summary>
    /// success 的反序列化载体。字段全是可空，用来把「缺字段」和「值不对」分开报；
    /// 属性声明顺序与规格样本一致。
    /// </summary>
    private sealed class SuccessPayload
    {
        [JsonPropertyName(AuthProtocol.FieldType)]
        public string? Type { get; set; }

        [JsonPropertyName(AuthProtocol.FieldGrantedPermission)]
        public string? GrantedPermission { get; set; }

        [JsonPropertyName(AuthProtocol.FieldServerProof)]
        public string? ServerProof { get; set; }

        [JsonPropertyName(AuthProtocol.FieldSessionToken)]
        public string? SessionToken { get; set; }

        [JsonPropertyName(AuthProtocol.FieldVideoAttachExpiresInMs)]
        public int? VideoAttachExpiresInMs { get; set; }
    }
}
