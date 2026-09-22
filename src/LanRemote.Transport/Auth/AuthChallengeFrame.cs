using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanRemote.Transport.Auth;

/// <summary>
/// 服务端 → 客户端：<c>auth_challenge</c> 帧（M4 阶段 2 步骤 9）。
/// </summary>
/// <remarks>
/// <para>字段集以权威规格 <c>04_PROTOCOL_AND_SECURITY.md</c> §9 的样本为准：
/// <c>type</c> / <c>protocol</c> / <c>sessionId</c> / <c>serverDeviceId</c> /
/// <c>serverNonce</c>（base64 canonical，32 字节）/ <c>certSha256</c>（uppercase hex，32 字节）/
/// <c>expiresInMs</c>。</para>
/// <para><b>微决策（记录在案）</b>：HANDOFF §18.2 步骤 9 的字段清单是缩略表述（连 <c>type</c>
/// 都未列出）；<c>protocol</c> 字段照规格样本保留并校验 <c>== 1</c>，与 <see cref="HelloFrame"/>
/// 的既有策略一致（hello 同样把 protocol 钉死为 1）。</para>
/// <para><b>严格解析哲学与 <see cref="HelloFrame"/> 相同</b>：认证帧全部出现在未认证阶段，
/// 每一项宽松（重复字段 / 未知字段 / 大小写 / 注释 / 尾逗号 / 尾随内容 / 非法 UTF-8 兜底）
/// 都被显式拒绝，且逐项有测试。拒绝短码只用于本地日志，绝不下发对端。</para>
/// <para><b>expiresInMs 只做「正值」判定</b>：公开语义是 challenge 有效期提示（毫秒）。
/// 上界不在帧层发明（规格未完定），由消费方的绝对 deadline 兜底（阶段 3/4）。</para>
/// <para><b>值语义不在此层</b>：<c>sessionId</c> / <c>serverDeviceId</c> 是否与本机已知值对齐、
/// <c>certSha256</c> 是否等于本研究连接实际出示的证书，属于状态机（阶段 3/4）的判定；
/// 本层只保证「字段存在且是 canonical 形状」。</para>
/// </remarks>
public sealed class AuthChallengeFrame
{
    /// <summary>期望的 <c>type</c>。</summary>
    public const string ExpectedType = AuthProtocol.TypeAuthChallenge;

    /// <summary>不是合法 JSON / 编码非法 / 结构超深。</summary>
    public const string RejectMalformedJson = "challenge-malformed-json";

    /// <summary>JSON 之后还有多余内容。</summary>
    public const string RejectTrailingData = "challenge-trailing-data";

    /// <summary>缺字段（七个字段都是必填）。</summary>
    public const string RejectMissingField = "challenge-missing-field";

    /// <summary><c>type</c> 不对（大小写也必须完全一致）。</summary>
    public const string RejectWrongType = "challenge-wrong-type";

    /// <summary><c>protocol</c> 不对。</summary>
    public const string RejectWrongProtocol = "challenge-wrong-protocol";

    /// <summary><c>sessionId</c> 不是 canonical（小写 D 格式）uuid 串。</summary>
    public const string RejectBadSessionId = "challenge-bad-session-id";

    /// <summary><c>serverDeviceId</c> 不是 canonical uuid 串。</summary>
    public const string RejectBadServerDeviceId = "challenge-bad-server-device-id";

    /// <summary><c>serverNonce</c> 不是 canonical base64，或不是恰好 32 字节。</summary>
    public const string RejectBadServerNonce = "challenge-bad-server-nonce";

    /// <summary><c>certSha256</c> 不是 canonical（大写）hex，或不是恰好 32 字节。</summary>
    public const string RejectBadCertSha256 = "challenge-bad-cert-sha256";

    /// <summary><c>expiresInMs</c> 不是正数。</summary>
    public const string RejectBadExpiresInMs = "challenge-bad-expires-in-ms";

    private readonly byte[] _serverNonce;
    private readonly byte[] _certificateSha256;

    /// <summary>
    /// 构造一个已校验的 challenge（服务端侧用）。
    /// </summary>
    /// <param name="sessionId">本次认证会话 id。</param>
    /// <param name="serverDeviceId">服务端设备号。</param>
    /// <param name="serverNonce">32 字节随机数。</param>
    /// <param name="certificateSha256">本连接实际出示证书的 DER SHA-256（32 字节）。</param>
    /// <param name="expiresInMs">有效期提示（毫秒，必须为正）。</param>
    /// <exception cref="ArgumentException">nonce 或证书摘要长度不是 32 字节。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="expiresInMs"/> 不是正数。</exception>
    /// <remarks>输入是程序内部值，不是对端输入——不符立即抛，属编程错误（fail loud）。</remarks>
    public AuthChallengeFrame(
        Guid sessionId,
        Guid serverDeviceId,
        ReadOnlySpan<byte> serverNonce,
        ReadOnlySpan<byte> certificateSha256,
        int expiresInMs)
    {
        if (serverNonce.Length != AuthProtocol.NonceByteLength)
        {
            throw new ArgumentException(
                $"serverNonce 必须是 {AuthProtocol.NonceByteLength} 字节。", nameof(serverNonce));
        }

        if (certificateSha256.Length != AuthProtocol.CertificateSha256ByteLength)
        {
            throw new ArgumentException(
                $"certificateSha256 必须是 {AuthProtocol.CertificateSha256ByteLength} 字节。",
                nameof(certificateSha256));
        }

        if (expiresInMs < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresInMs), expiresInMs, "expiresInMs 必须为正数（毫秒）。");
        }

        SessionId = sessionId;
        ServerDeviceId = serverDeviceId;
        _serverNonce = serverNonce.ToArray();
        _certificateSha256 = certificateSha256.ToArray();
        ExpiresInMs = expiresInMs;
    }

    /// <summary>本次认证会话 id。</summary>
    public Guid SessionId { get; }

    /// <summary>服务端设备号（客户端用来核对「是不是我以为的那台机器」）。</summary>
    public Guid ServerDeviceId { get; }

    /// <summary>32 字节服务端随机数（transcript 输入）。</summary>
    public ReadOnlyMemory<byte> ServerNonce => _serverNonce;

    /// <summary>本连接实际出示证书的 DER SHA-256（32 字节，transcript 输入）。</summary>
    public ReadOnlyMemory<byte> CertificateSha256 => _certificateSha256;

    /// <summary>有效期提示（毫秒）。</summary>
    public int ExpiresInMs { get; }

    /// <summary>
    /// 序列化（服务端侧发送方向）。
    /// </summary>
    /// <returns>UTF-8 JSON，不带长度前缀。</returns>
    public byte[] Serialize()
    {
        return JsonSerializer.SerializeToUtf8Bytes(
            new ChallengePayload
            {
                Type = ExpectedType,
                Protocol = AuthProtocol.WireProtocolVersion,
                SessionId = SessionId.ToString("D"),
                ServerDeviceId = ServerDeviceId.ToString("D"),
                ServerNonce = Convert.ToBase64String(_serverNonce),
                CertSha256 = Convert.ToHexString(_certificateSha256),
                ExpiresInMs = ExpiresInMs,
            },
            AuthJson.StrictOptions);
    }

    /// <summary>
    /// 解析并校验一个 <c>auth_challenge</c> 载荷（客户端侧接收方向）。
    /// </summary>
    /// <param name="utf8">帧载荷（不含长度前缀）。</param>
    /// <param name="frame">成功时为已校验帧；失败为 <see langword="null"/>。</param>
    /// <param name="rejection">通过时为 <see langword="null"/>；否则是只用于本地日志的短码。</param>
    /// <returns>是否解析成功。</returns>
    public static bool TryParse(
        ReadOnlySpan<byte> utf8,
        out AuthChallengeFrame? frame,
        out string? rejection)
    {
        frame = null;

        if (!AuthJson.TryDeserializeStrict(
                utf8, out ChallengePayload? payload, out rejection,
                RejectMalformedJson, RejectTrailingData,
                ExpectedType, RejectWrongType))
        {
            return false;
        }

        // 顺序：先判 type（缺失 / 不对），再判其余字段存在性，最后逐字段判值。
        // 「跨类型帧」因此报 wrong-type 而不是 missing-field——拒绝码是本地日志语义，
        // 这样更精确（例如把一个完整的 auth_response 误喂给本解析器）。
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

        if (payload.Protocol is null
            || payload.SessionId is null
            || payload.ServerDeviceId is null
            || payload.ServerNonce is null
            || payload.CertSha256 is null
            || payload.ExpiresInMs is null)
        {
            rejection = RejectMissingField;
            return false;
        }

        if (payload.Protocol.Value != AuthProtocol.WireProtocolVersion)
        {
            rejection = RejectWrongProtocol;
            return false;
        }

        if (!CanonicalGuid.TryParse(payload.SessionId, out Guid sessionId))
        {
            rejection = RejectBadSessionId;
            return false;
        }

        if (!CanonicalGuid.TryParse(payload.ServerDeviceId, out Guid serverDeviceId))
        {
            rejection = RejectBadServerDeviceId;
            return false;
        }

        if (!CanonicalBase64.TryDecode(payload.ServerNonce, out byte[] serverNonce)
            || serverNonce.Length != AuthProtocol.NonceByteLength)
        {
            rejection = RejectBadServerNonce;
            return false;
        }

        if (!CanonicalHex.TryDecode(payload.CertSha256, out byte[] certificateSha256)
            || certificateSha256.Length != AuthProtocol.CertificateSha256ByteLength)
        {
            rejection = RejectBadCertSha256;
            return false;
        }

        if (payload.ExpiresInMs.Value < 1)
        {
            rejection = RejectBadExpiresInMs;
            return false;
        }

        frame = new AuthChallengeFrame(
            sessionId, serverDeviceId, serverNonce, certificateSha256, payload.ExpiresInMs.Value);
        return true;
    }

    /// <summary>
    /// challenge 的反序列化载体。字段全是可空，用来把「缺字段」和「值不对」分开报；
    /// 属性声明顺序与规格样本一致。
    /// </summary>
    private sealed class ChallengePayload
    {
        [JsonPropertyName(AuthProtocol.FieldType)]
        public string? Type { get; set; }

        [JsonPropertyName(AuthProtocol.FieldProtocol)]
        public int? Protocol { get; set; }

        [JsonPropertyName(AuthProtocol.FieldSessionId)]
        public string? SessionId { get; set; }

        [JsonPropertyName(AuthProtocol.FieldServerDeviceId)]
        public string? ServerDeviceId { get; set; }

        [JsonPropertyName(AuthProtocol.FieldServerNonce)]
        public string? ServerNonce { get; set; }

        [JsonPropertyName(AuthProtocol.FieldCertSha256)]
        public string? CertSha256 { get; set; }

        [JsonPropertyName(AuthProtocol.FieldExpiresInMs)]
        public int? ExpiresInMs { get; set; }
    }
}
