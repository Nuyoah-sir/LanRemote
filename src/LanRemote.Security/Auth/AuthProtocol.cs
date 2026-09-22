using LanRemote.Core.Models;

namespace LanRemote.Security.Auth;

/// <summary>
/// M4 认证协议的常量与规范编码——协议词汇表的单一事实源。
/// </summary>
/// <remarks>
/// <para><b>字节布局以 <c>docs/DECISIONS.md</c> 的 ADR-038 第 1 条为准</b>；
/// 本类中的域串、字段名、尺寸与时限同时被确定性单测逐字锁定，
/// 防止「改了一处、忘了另一处」的静默漂移（ADR-038：「精确字节串同时固化于
/// <c>AuthProtocol</c> 常量 + 确定性单测，两处不得漂移」）。</para>
/// <para><b>参与 transcript 的串</b>（两个域前缀、权限词、<c>server\0</c> 前缀）改动即破坏
/// 协议兼容性，属 breaking change，必须同步修订 ADR-038、黄金向量生成脚本与测试。</para>
/// <para>时限为 <b>provisional</b> 初值（ADR-038 第 4 条），待数值实验（HANDOFF §18.4 C）
/// 定案后回写。</para>
/// </remarks>
public static class AuthProtocol
{
    // ─────────────────────────── 域分隔串（transcript 首字段） ───────────────────────────

    /// <summary>客户端认证 transcript 的域前缀（其后紧跟一个 <c>\0</c> 分隔符）。</summary>
    public const string ClientTranscriptDomain = "LANREMOTE-AUTH-V1";

    /// <summary>服务端授权（grant）transcript 的域前缀——与客户端档不同域，防跨档重放。</summary>
    public const string GrantTranscriptDomain = "LANREMOTE-GRANT-V1";

    /// <summary>
    /// <c>serverProof</c> 的 MAC 域前缀：ASCII <c>server</c> 加单个 <c>\0</c> 字节。
    /// </summary>
    /// <remarks>以规格 04 §9 为准；「<c>server|</c>」写法作废（ADR-038 第 1 条）。</remarks>
    public const string ServerProofPrefix = "server\0";

    // ─────────────────────────── 帧类型（§9 认证段） ───────────────────────────

    /// <summary>服务端 → 客户端：认证挑战。</summary>
    public const string TypeAuthChallenge = "auth_challenge";

    /// <summary>客户端 → 服务端：认证响应。</summary>
    public const string TypeAuthResponse = "auth_response";

    /// <summary>服务端 → 客户端：认证成功（携带 serverProof 与 sessionToken）。</summary>
    public const string TypeAuthSuccess = "auth_success";

    /// <summary>服务端 → 客户端：等待本机审批。</summary>
    public const string TypeApprovalPending = "approval_pending";

    /// <summary>服务端 → 客户端：认证失败的<b>唯一</b>对外形式（不区分细节）。</summary>
    public const string TypeAuthenticationFailed = "authentication_failed";

    // ─────────────────────────── JSON 字段名（§9 认证段冻结词表） ───────────────────────────

    /// <summary>字段名：<c>type</c>。</summary>
    public const string FieldType = "type";

    /// <summary>字段名：<c>protocol</c>。</summary>
    public const string FieldProtocol = "protocol";

    /// <summary>字段名：<c>sessionId</c>（uuid 串）。</summary>
    public const string FieldSessionId = "sessionId";

    /// <summary>字段名：<c>serverDeviceId</c>（uuid 串）。</summary>
    public const string FieldServerDeviceId = "serverDeviceId";

    /// <summary>字段名：<c>clientDeviceId</c>（uuid 串）。</summary>
    public const string FieldClientDeviceId = "clientDeviceId";

    /// <summary>字段名：<c>clientName</c>（人类可读，显示用）。</summary>
    public const string FieldClientName = "clientName";

    /// <summary>字段名：<c>serverNonce</c>（base64 canonical，32 字节）。</summary>
    public const string FieldServerNonce = "serverNonce";

    /// <summary>字段名：<c>clientNonce</c>（base64 canonical，32 字节）。</summary>
    public const string FieldClientNonce = "clientNonce";

    /// <summary>字段名：<c>certSha256</c>（uppercase hex，32 字节 DER 摘要）。</summary>
    public const string FieldCertSha256 = "certSha256";

    /// <summary>字段名：<c>requestedPermission</c>（<c>view</c> 或 <c>control</c>）。</summary>
    public const string FieldRequestedPermission = "requestedPermission";

    /// <summary>字段名：<c>clientProof</c>（base64 canonical，32 字节）。</summary>
    public const string FieldClientProof = "clientProof";

    /// <summary>字段名：<c>grantedPermission</c>（<c>view</c> 或 <c>control</c>）。</summary>
    public const string FieldGrantedPermission = "grantedPermission";

    /// <summary>字段名：<c>serverProof</c>（base64 canonical，32 字节）。</summary>
    public const string FieldServerProof = "serverProof";

    /// <summary>字段名：<c>sessionToken</c>（base64 canonical，32 字节随机）。</summary>
    public const string FieldSessionToken = "sessionToken";

    /// <summary>字段名：<c>videoAttachExpiresInMs</c>（M5 视频重连窗口）。</summary>
    public const string FieldVideoAttachExpiresInMs = "videoAttachExpiresInMs";

    /// <summary>字段名：<c>expiresInMs</c>（challenge 有效期提示）。</summary>
    public const string FieldExpiresInMs = "expiresInMs";

    // ─────────────────────────── 尺寸（字节） ───────────────────────────

    /// <summary>认证帧的 <c>protocol</c> 字段值（与 hello 帧的版本号一致）。</summary>
    public const int WireProtocolVersion = 1;

    /// <summary>nonce 的字节数（<c>serverNonce</c> / <c>clientNonce</c>）。</summary>
    public const int NonceByteLength = 32;

    /// <summary>HMAC-SHA256 proof 的字节数（<c>clientProof</c> / <c>serverProof</c>）。</summary>
    public const int ProofByteLength = 32;

    /// <summary>会话令牌的字节数（认证成功后由服务端生成）。</summary>
    public const int SessionTokenByteLength = 32;

    /// <summary>证书 DER 的 SHA-256 摘要字节数（<c>certSha256</c>）。</summary>
    public const int CertificateSha256ByteLength = 32;

    // ─────────────────────────── 时限（毫秒；provisional，ADR-038 第 4 条） ───────────────────────────

    /// <summary>机器认证窗口的初值：challenge 发出至 response 校验完成的绝对上限。</summary>
    public const int AuthenticationWindowMilliseconds = 10_000;

    /// <summary>人类审批窗口的初值（自获批面受理起算）。</summary>
    public const int ApprovalWindowMilliseconds = 60_000;

    // ─────────────────────────── 权限枚举 ↔ 线上串 ───────────────────────────

    /// <summary>线上串：仅查看（对应 <see cref="SessionPermission.ViewOnly"/>）。</summary>
    public const string PermissionView = "view";

    /// <summary>线上串：查看并控制（对应 <see cref="SessionPermission.Control"/>）。</summary>
    public const string PermissionControl = "control";

    /// <summary>
    /// 把权限枚举编码为线上串（参与 transcript 字节串）。
    /// </summary>
    /// <param name="permission">权限枚举。</param>
    /// <returns><c>view</c> 或 <c>control</c>。</returns>
    /// <exception cref="ArgumentOutOfRangeException">枚举值未定义。</exception>
    /// <remarks>
    /// 未定义值必须抛异常而不是落到某个默认串：将来扩展枚举时，
    /// 若忘记同步此映射，宁可当场失败，也不能把权限语义静默错编码进 transcript。
    /// </remarks>
    public static string EncodePermission(SessionPermission permission) => permission switch
    {
        SessionPermission.ViewOnly => PermissionView,
        SessionPermission.Control => PermissionControl,
        _ => throw new ArgumentOutOfRangeException(
            nameof(permission),
            permission,
            "未定义的 SessionPermission 值，无法编码为线上串。"),
    };
}
