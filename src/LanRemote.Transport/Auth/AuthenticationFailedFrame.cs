using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanRemote.Transport.Auth;

/// <summary>
/// 服务端 → 客户端：<c>authentication_failed</c> 帧——认证失败的<b>唯一</b>对外形式，
/// 不区分「密码错 / 设备码错 / 会话过期」等任何细节。
/// </summary>
/// <remarks>
/// <para><b>计划外补充（记录在案）</b>：HANDOFF §18.2 阶段 2 步骤 9–11 只列了四类帧；
/// 本帧为阶段 3（服务端发送）与阶段 4（客户端解析）的公共前置件，且与
/// <see cref="ApprovalPendingFrame"/> 同构（唯一字段 <c>type</c>）——提前落地可避免
/// 阶段 4 手写第二套 JSON 解析面，认证帧的严格解析始终只有一条路径。</para>
/// <para><b>语义为空是有意的</b>：规格 04 §9「不区分细节给远端」。本帧<b>永不</b>携带
/// 原因短码；拒绝原因只留在服务端本地日志（阶段 3 的 limiter 也不打日志）。</para>
/// <para><b>严格解析哲学与 <see cref="HelloFrame"/> 相同</b>：重复字段 / 未知字段 / 多余
/// 尾随内容 / 大小写变体全部拒绝。</para>
/// </remarks>
public static class AuthenticationFailedFrame
{
    /// <summary>期望的 <c>type</c>。</summary>
    public const string ExpectedType = AuthProtocol.TypeAuthenticationFailed;

    /// <summary>不是合法 JSON / 编码非法 / 结构超深。</summary>
    public const string RejectMalformedJson = "auth-failed-malformed-json";

    /// <summary>JSON 之后还有多余内容。</summary>
    public const string RejectTrailingData = "auth-failed-trailing-data";

    /// <summary>缺字段（<c>type</c> 必填）。</summary>
    public const string RejectMissingField = "auth-failed-missing-field";

    /// <summary><c>type</c> 不对（大小写也必须完全一致）。</summary>
    public const string RejectWrongType = "auth-failed-wrong-type";

    /// <summary>
    /// 解析并校验一个 <c>authentication_failed</c> 载荷。
    /// </summary>
    /// <param name="utf8">帧载荷（不含长度前缀）。</param>
    /// <param name="rejection">通过时为 <see langword="null"/>；否则是只用于本地日志的短码。</param>
    /// <returns>是否是<b>且仅是</b> <c>{type:"authentication_failed"}</c>。</returns>
    public static bool TryParse(ReadOnlySpan<byte> utf8, out string? rejection)
    {
        rejection = null;

        if (!AuthJson.TryDeserializeStrict(
                utf8, out AuthenticationFailedPayload? payload, out rejection,
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
    /// 序列化一个合法的 <c>authentication_failed</c> 帧载荷（服务端侧用；无内容变体）。
    /// </summary>
    /// <returns>UTF-8 JSON，不带长度前缀。</returns>
    public static byte[] Serialize()
    {
        return JsonSerializer.SerializeToUtf8Bytes(
            new AuthenticationFailedPayload { Type = ExpectedType },
            AuthJson.StrictOptions);
    }

    /// <summary>authentication_failed 的反序列化载体（只有 type；可空用于区分「缺字段」）。</summary>
    private sealed class AuthenticationFailedPayload
    {
        [JsonPropertyName(AuthProtocol.FieldType)]
        public string? Type { get; set; }
    }
}
