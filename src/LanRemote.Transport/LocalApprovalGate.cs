using System.Net;
using System.Security.Cryptography;
using System.Text;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport;

/// <summary>
/// 本机审批的<b>显式终态</b>（评审 #37：不是 <see cref="bool"/>；
/// 「没有 handler」不得被映射成 Approved）。
/// </summary>
public enum LocalApprovalOutcome
{
    /// <summary>同意；<see cref="LocalApprovalDecision.GrantedPermission"/> 必填且 ≤ 请求权限（评审 #39）。</summary>
    Approved,

    /// <summary>操作者明确拒绝。</summary>
    Denied,

    /// <summary>审批窗口内没有决定（到点即拒，fail closed）。</summary>
    TimedOut,

    /// <summary>决定作废（例如请求已被取消、连接已断）。</summary>
    Cancelled,

    /// <summary>审批面不可用（fail closed；headless 默认实现<b>不得</b>自动批——评审 #46）。</summary>
    Unavailable,
}

/// <summary>
/// 一次审批请求的<b>不可变</b>快照（评审 #38：UI 只回「决定 + 请求 ID」，
/// 绝不回填 transcript / HMAC / token / nonce / 证书字段）。
/// </summary>
/// <param name="RequestId">每请求唯一 ID；审批<b>针对该 ID</b>（评审 #43：不存在「批准当前任意待批」）。</param>
/// <param name="ConnectionId">连接关联 id（<see cref="ConnectionSecurityContext.ConnectionId"/>）。</param>
/// <param name="SessionId">认证会话 id。</param>
/// <param name="RemoteAddress">来源 IP。</param>
/// <param name="RemotePort">来源端口。</param>
/// <param name="ClientDeviceId"><b>自称</b>设备号（discovery 数据永远是未认证的——评审 #41，
/// 展示时必须标注「自称」）。</param>
/// <param name="ClientName"><b>自称</b>显示名（同上）。</param>
/// <param name="RequestedPermission">请求的权限。</param>
/// <param name="ShortCode">短关联码（由已认证素材派生、两端可各自算出；仅作人工关联用——评审 #40）。</param>
/// <param name="ExpiresAt">绝对过期时刻（UI 展示剩余秒数用）。</param>
public sealed record LocalApprovalRequest(
    Guid RequestId,
    Guid ConnectionId,
    Guid SessionId,
    IPAddress RemoteAddress,
    int RemotePort,
    Guid ClientDeviceId,
    string ClientName,
    SessionPermission RequestedPermission,
    string ShortCode,
    DateTimeOffset ExpiresAt)
{
    /// <summary>短关联码的字符数（3 字节 → 6 个大写十六进制字符）。</summary>
    public const int ShortCodeLength = 6;

    /// <summary>
    /// 派生短关联码：<c>SHA256(域串 || sessionId || clientNonce)</c> 前 3 字节的大写十六进制。
    /// </summary>
    /// <param name="sessionId">认证会话 id（两端各知）。</param>
    /// <param name="clientNonce">客户端 nonce（两端各知）。</param>
    /// <returns>6 个大写十六进制字符。</returns>
    /// <remarks>
    /// <b>仅作人工关联</b>（评审 #40）：输入两端可各自算出、又不含任何秘密，
    /// 因此「两端可见」不需要额外传输——但它<b>不构成认证</b>，
    /// 任何展示都必须与「自称」字段同等对待。输入来自<b>已认证素材</b>（nonce 进了 proof），
    /// 不是可被旁路改写的路径。
    /// </remarks>
    public static string ComputeShortCode(Guid sessionId, ReadOnlySpan<byte> clientNonce)
    {
        byte[] prefix = Encoding.UTF8.GetBytes(AuthProtocol.ApprovalCodeDomain + "\0");
        byte[] input = new byte[prefix.Length + 16 + clientNonce.Length];
        prefix.CopyTo(input.AsSpan());
        sessionId.TryWriteBytes(input.AsSpan(prefix.Length, 16));
        clientNonce.CopyTo(input.AsSpan(prefix.Length + 16));

        byte[] digest = SHA256.HashData(input);
        return Convert.ToHexString(digest.AsSpan(0, ShortCodeLength / 2));
    }
}

/// <summary>
/// UI 的回应：决定 + 请求 ID（评审 #38）。
/// </summary>
/// <param name="RequestId">回指的请求 ID；与请求不匹配视为无效决定（fail closed）。</param>
/// <param name="Outcome">显式终态。</param>
/// <param name="GrantedPermission">仅 <see cref="LocalApprovalOutcome.Approved"/> 时有意义；
/// 必须 ≤ 请求权限（服务端自校验，评审 #39——UI 文本不得越权）。</param>
public sealed record LocalApprovalDecision(
    Guid RequestId,
    LocalApprovalOutcome Outcome,
    SessionPermission? GrantedPermission = null);

/// <summary>
/// 被控端本机审批面（M4 边界：库内真接口 + 测试替身；产品 WPF UI 不动——HANDOFF §18.3）。
/// </summary>
/// <remarks>
/// <para><b>实现契约</b>：请求不可变；决定只回「请求 ID + 终态」；sessionToken 由认证状态机
/// 在「决定 = Approved 且连接仍在」时自行生成并直接写往对端——审批面<b>永远看不到</b>
/// sessionToken / access key / proof / transcript。</para>
/// <para>取消令牌触发时应尽快返回（抛 <see cref="OperationCanceledException"/> 或给出终态均可）；
/// 窗口到点后的任何决定一律作废（首个终态胜，评审 #44）。</para>
/// <para><b>UI 不可用必须返回 <see cref="LocalApprovalOutcome.Unavailable"/></b>，
/// 不许静默当作同意（评审 #46）。</para>
/// </remarks>
public interface ILocalApprovalGate
{
    /// <summary>提交一次审批请求并等待决定。</summary>
    /// <param name="request">不可变请求快照。</param>
    /// <param name="cancellationToken">取消（窗口到点 / 连接已断 / 停机）。</param>
    /// <returns>决定（含回指请求 ID）。</returns>
    ValueTask<LocalApprovalDecision> RequestApprovalAsync(
        LocalApprovalRequest request,
        CancellationToken cancellationToken);
}
