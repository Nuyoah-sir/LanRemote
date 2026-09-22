using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanRemote.Transport.Auth;

/// <summary>
/// 服务端 → 客户端：<c>approval_pending</c> 帧（M4 阶段 2 步骤 11）——「已受理，等被控端
/// 本机审批」。语义为空：唯一的字段是 <c>type</c>（与 <see cref="HelloFrame"/> 同为纯标志帧）。
/// </summary>
/// <remarks>
/// <para>规格 04 §9 的样本只有 <c>{"type":"approval_pending"}</c>；审批的展示素材
/// （最小集 + 短关联码）留在被控端本机 UI（阶段 3），不在此帧携带——不给「审批界面」
/// 发明协议字段。</para>
/// <para><b>严格解析哲学与 <see cref="HelloFrame"/> 相同</b>：重复字段 / 未知字段 / 多余
/// 尾随内容 / 大小写变体全部拒绝。拒绝短码只用于本地日志，绝不下发对端。</para>
/// </remarks>
public static class ApprovalPendingFrame
{
    /// <summary>期望的 <c>type</c>。</summary>
    public const string ExpectedType = AuthProtocol.TypeApprovalPending;

    /// <summary>不是合法 JSON / 编码非法 / 结构超深。</summary>
    public const string RejectMalformedJson = "approval-pending-malformed-json";

    /// <summary>JSON 之后还有多余内容。</summary>
    public const string RejectTrailingData = "approval-pending-trailing-data";

    /// <summary>缺字段（<c>type</c> 必填）。</summary>
    public const string RejectMissingField = "approval-pending-missing-field";

    /// <summary><c>type</c> 不对（大小写也必须完全一致）。</summary>
    public const string RejectWrongType = "approval-pending-wrong-type";

    /// <summary>
    /// 解析并校验一个 <c>approval_pending</c> 载荷。
    /// </summary>
    /// <param name="utf8">帧载荷（不含长度前缀）。</param>
    /// <param name="rejection">通过时为 <see langword="null"/>；否则是只用于本地日志的短码。</param>
    /// <returns>是否是<b>且仅是</b> <c>{type:"approval_pending"}</c>。</returns>
    public static bool TryParse(ReadOnlySpan<byte> utf8, out string? rejection)
    {
        rejection = null;

        if (!AuthJson.TryDeserializeStrict(
                utf8, out ApprovalPendingPayload? payload, out rejection,
                RejectMalformedJson, RejectTrailingData,
                ExpectedType, RejectWrongType))
        {
            return false;
        }

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

        return true;
    }

    /// <summary>
    /// 序列化一个合法的 <c>approval_pending</c> 帧载荷（服务端侧用）。
    /// </summary>
    /// <returns>UTF-8 JSON，不带长度前缀。</returns>
    public static byte[] Serialize()
    {
        return JsonSerializer.SerializeToUtf8Bytes(
            new ApprovalPendingPayload { Type = ExpectedType },
            AuthJson.StrictOptions);
    }

    /// <summary>approval_pending 的反序列化载体（只有 type；可空用于区分「缺字段」）。</summary>
    private sealed class ApprovalPendingPayload
    {
        [JsonPropertyName(AuthProtocol.FieldType)]
        public string? Type { get; set; }
    }
}
