using System.Text.Json;
using System.Text.Json.Serialization;
using LanRemote.Core.Models;

namespace LanRemote.Transport.Auth;

/// <summary>
/// 客户端 → 服务端：<c>auth_response</c> 帧（M4 阶段 2 步骤 10）。
/// </summary>
/// <remarks>
/// <para>字段集以权威规格 <c>04_PROTOCOL_AND_SECURITY.md</c> §9 的样本为准：
/// <c>type</c> / <c>clientDeviceId</c> / <c>clientName</c> / <c>clientNonce</c>（base64 canonical，
/// 32 字节）/ <c>requestedPermission</c>（<c>view</c> | <c>control</c>）/ <c>clientProof</c>
/// （base64 canonical，32 字节）。</para>
/// <para><b>clientName 判定（本地显示面硬化，非规格常量）</b>：非空、长度 ≤
/// <see cref="ClientNameMaxLength"/>、无控制字符、UTF-16 良构。理由是这个名字最终会出现在
/// 被控端<b>本机审批面</b>上——把「显示面的输入」约束成无聊的形态，比事后在 UI 里补救便宜。
/// 本机名（NetBIOS，≤15 字符）远在上限之内。</para>
/// <para><b>严格解析哲学与 <see cref="HelloFrame"/> 相同</b>：拒绝短码只用于本地日志，
/// 绝不下发对端（服务端对该帧的所有失败统一以 <c>authentication_failed</c> 回应，见阶段 3）。</para>
/// </remarks>
public sealed class AuthResponseFrame
{
    /// <summary>期望的 <c>type</c>。</summary>
    public const string ExpectedType = AuthProtocol.TypeAuthResponse;

    /// <summary><c>clientName</c> 的最大长度（UTF-16 代码单元；显示面策略，非协议常量）。</summary>
    public const int ClientNameMaxLength = 64;

    /// <summary>不是合法 JSON / 编码非法 / 结构超深。</summary>
    public const string RejectMalformedJson = "response-malformed-json";

    /// <summary>JSON 之后还有多余内容。</summary>
    public const string RejectTrailingData = "response-trailing-data";

    /// <summary>缺字段（六个字段都是必填）。</summary>
    public const string RejectMissingField = "response-missing-field";

    /// <summary><c>type</c> 不对（大小写也必须完全一致）。</summary>
    public const string RejectWrongType = "response-wrong-type";

    /// <summary><c>clientDeviceId</c> 不是 canonical uuid 串。</summary>
    public const string RejectBadClientDeviceId = "response-bad-client-device-id";

    /// <summary><c>clientName</c> 为空 / 超长 / 含控制字符 / UTF-16 不良构。</summary>
    public const string RejectBadClientName = "response-bad-client-name";

    /// <summary><c>clientNonce</c> 不是 canonical base64，或不是恰好 32 字节。</summary>
    public const string RejectBadClientNonce = "response-bad-client-nonce";

    /// <summary><c>requestedPermission</c> 不是 <c>view</c> / <c>control</c>。</summary>
    public const string RejectBadRequestedPermission = "response-bad-requested-permission";

    /// <summary><c>clientProof</c> 不是 canonical base64，或不是恰好 32 字节。</summary>
    public const string RejectBadClientProof = "response-bad-client-proof";

    private readonly byte[] _clientNonce;
    private readonly byte[] _clientProof;

    /// <summary>
    /// 构造一个已校验的 response（客户端侧用）。
    /// </summary>
    /// <param name="clientDeviceId">客户端设备号。</param>
    /// <param name="clientName">客户端显示名（判定规则见类型备注）。</param>
    /// <param name="clientNonce">32 字节客户端随机数。</param>
    /// <param name="requestedPermission">请求的权限。</param>
    /// <param name="clientProof">32 字节 HMAC-SHA256 证明。</param>
    /// <exception cref="ArgumentException">名称为空 / 超长 / 含控制字符 / 不良构；nonce 或 proof 长度不对。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="requestedPermission"/> 是未定义的枚举值。</exception>
    public AuthResponseFrame(
        Guid clientDeviceId,
        string clientName,
        ReadOnlySpan<byte> clientNonce,
        SessionPermission requestedPermission,
        ReadOnlySpan<byte> clientProof)
    {
        ArgumentNullException.ThrowIfNull(clientName);

        if (!IsAcceptableClientName(clientName))
        {
            throw new ArgumentException(
                "clientName 必须非空、长度不超过上限、无控制字符且 UTF-16 良构。", nameof(clientName));
        }

        if (clientNonce.Length != AuthProtocol.NonceByteLength)
        {
            throw new ArgumentException(
                $"clientNonce 必须是 {AuthProtocol.NonceByteLength} 字节。", nameof(clientNonce));
        }

        if (clientProof.Length != AuthProtocol.ProofByteLength)
        {
            throw new ArgumentException(
                $"clientProof 必须是 {AuthProtocol.ProofByteLength} 字节。", nameof(clientProof));
        }

        // 未定义枚举值立刻抛：与 Serialize 的编码共用同一判定，fail fast 不拖到序列化。
        _ = AuthProtocol.EncodePermission(requestedPermission);

        ClientDeviceId = clientDeviceId;
        ClientName = clientName;
        _clientNonce = clientNonce.ToArray();
        _clientProof = clientProof.ToArray();
        RequestedPermission = requestedPermission;
    }

    /// <summary>客户端设备号。</summary>
    public Guid ClientDeviceId { get; }

    /// <summary>客户端显示名（原样保留，不做 trim / 归一化）。</summary>
    public string ClientName { get; }

    /// <summary>32 字节客户端随机数（transcript 输入）。</summary>
    public ReadOnlyMemory<byte> ClientNonce => _clientNonce;

    /// <summary>请求的权限（transcript 输入）。</summary>
    public SessionPermission RequestedPermission { get; }

    /// <summary>32 字节 HMAC-SHA256 clientProof。</summary>
    public ReadOnlyMemory<byte> ClientProof => _clientProof;

    /// <summary>
    /// 序列化（客户端侧发送方向）。
    /// </summary>
    /// <returns>UTF-8 JSON，不带长度前缀。</returns>
    public byte[] Serialize()
    {
        return JsonSerializer.SerializeToUtf8Bytes(
            new ResponsePayload
            {
                Type = ExpectedType,
                ClientDeviceId = ClientDeviceId.ToString("D"),
                ClientName = ClientName,
                ClientNonce = Convert.ToBase64String(_clientNonce),
                RequestedPermission = AuthProtocol.EncodePermission(RequestedPermission),
                ClientProof = Convert.ToBase64String(_clientProof),
            },
            AuthJson.StrictOptions);
    }

    /// <summary>
    /// 解析并校验一个 <c>auth_response</c> 载荷（服务端侧接收方向）。
    /// </summary>
    /// <param name="utf8">帧载荷（不含长度前缀）。</param>
    /// <param name="frame">成功时为已校验帧；失败为 <see langword="null"/>。</param>
    /// <param name="rejection">通过时为 <see langword="null"/>；否则是只用于本地日志的短码。</param>
    /// <returns>是否解析成功。</returns>
    public static bool TryParse(
        ReadOnlySpan<byte> utf8,
        out AuthResponseFrame? frame,
        out string? rejection)
    {
        frame = null;

        if (!AuthJson.TryDeserializeStrict(
                utf8, out ResponsePayload? payload, out rejection,
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

        if (payload.ClientDeviceId is null
            || payload.ClientName is null
            || payload.ClientNonce is null
            || payload.RequestedPermission is null
            || payload.ClientProof is null)
        {
            rejection = RejectMissingField;
            return false;
        }

        if (!CanonicalGuid.TryParse(payload.ClientDeviceId, out Guid clientDeviceId))
        {
            rejection = RejectBadClientDeviceId;
            return false;
        }

        if (!IsAcceptableClientName(payload.ClientName))
        {
            rejection = RejectBadClientName;
            return false;
        }

        if (!CanonicalBase64.TryDecode(payload.ClientNonce, out byte[] clientNonce)
            || clientNonce.Length != AuthProtocol.NonceByteLength)
        {
            rejection = RejectBadClientNonce;
            return false;
        }

        if (!AuthProtocol.TryDecodePermission(payload.RequestedPermission, out SessionPermission requested))
        {
            rejection = RejectBadRequestedPermission;
            return false;
        }

        if (!CanonicalBase64.TryDecode(payload.ClientProof, out byte[] clientProof)
            || clientProof.Length != AuthProtocol.ProofByteLength)
        {
            rejection = RejectBadClientProof;
            return false;
        }

        frame = new AuthResponseFrame(
            clientDeviceId, payload.ClientName, clientNonce, requested, clientProof);
        return true;
    }

    /// <summary>
    /// clientName 的判定（构造与解析共用，保证两条路径规则不分叉）：
    /// 非空、长度 ≤ <see cref="ClientNameMaxLength"/>、无控制字符、UTF-16 良构。
    /// </summary>
    /// <remarks>
    /// wire 侧的孤立代理码元（如 <c>\uD800</c> 转义）在 System.Text.Json 解码时就抛
    /// JsonException（实测 2026-09-22）→ 走 malformed；此处的代理对判定主要保护
    /// 构造器路径（.NET 字符串可以直接携带孤立代理，而 JSON 字节流不行）。
    /// </remarks>
    private static bool IsAcceptableClientName(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > ClientNameMaxLength)
        {
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];

            if (char.IsControl(c))
            {
                return false;
            }

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return false;
                }

                i++; // 合法代理对，跳过低位
            }
            else if (char.IsLowSurrogate(c))
            {
                return false; // 无前置高位的孤立低位代理
            }
        }

        return true;
    }

    /// <summary>
    /// response 的反序列化载体。字段全是可空，用来把「缺字段」和「值不对」分开报；
    /// 属性声明顺序与规格样本一致。
    /// </summary>
    private sealed class ResponsePayload
    {
        [JsonPropertyName(AuthProtocol.FieldType)]
        public string? Type { get; set; }

        [JsonPropertyName(AuthProtocol.FieldClientDeviceId)]
        public string? ClientDeviceId { get; set; }

        [JsonPropertyName(AuthProtocol.FieldClientName)]
        public string? ClientName { get; set; }

        [JsonPropertyName(AuthProtocol.FieldClientNonce)]
        public string? ClientNonce { get; set; }

        [JsonPropertyName(AuthProtocol.FieldRequestedPermission)]
        public string? RequestedPermission { get; set; }

        [JsonPropertyName(AuthProtocol.FieldClientProof)]
        public string? ClientProof { get; set; }
    }
}
