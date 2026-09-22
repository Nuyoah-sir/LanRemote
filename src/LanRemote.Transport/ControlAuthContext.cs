using LanRemote.Core.Abstractions;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport;

/// <summary>
/// 服务端认证的参数（值旋钮；provisional 初值全部来自规格 04 与 ADR-038）。
/// </summary>
/// <remarks>
/// <para><b>为什么是选项对象</b>：这些值在测试里要能整体缩放（机器窗口 10 s → 几百 ms、
/// 失败延时 300–800 ms → 0），否则认证层的超时路径只能靠真实等待去测。</para>
/// <para><see cref="ChallengeExpiresInMs"/> 是给客户端的<b>提示</b>；
/// <see cref="MachineWindow"/> 是服务端执行的<b>绝对窗口</b>。两者不是同一个数——见
/// <c>AuthProtocol</c> 里两常量各自的张力说明。</para>
/// </remarks>
public sealed record ControlAuthOptions
{
    /// <summary>是否要求本机审批（默认开启；v1 = 每个新控制连接都要批，ADR-038 第 3 条）。</summary>
    public bool RequireLocalApproval { get; init; } = true;

    /// <summary>challenge 的 <c>expiresInMs</c> 提示值（毫秒）。</summary>
    public int ChallengeExpiresInMs { get; init; } = AuthProtocol.ChallengeExpiresInMs;

    /// <summary><c>auth_success</c> 的 <c>videoAttachExpiresInMs</c>（M5 用）。</summary>
    public int VideoAttachExpiresInMs { get; init; } = AuthProtocol.VideoAttachExpiresInMs;

    /// <summary>机器窗口：challenge 写出前起算、覆盖 response 读取与校验的<b>绝对</b> deadline。</summary>
    public TimeSpan MachineWindow { get; init; } =
        TimeSpan.FromMilliseconds(AuthProtocol.AuthenticationWindowMilliseconds);

    /// <summary>人类审批窗口（自请求提交给审批面起算）。</summary>
    public TimeSpan ApprovalWindow { get; init; } =
        TimeSpan.FromMilliseconds(AuthProtocol.ApprovalWindowMilliseconds);

    /// <summary>每次密码学失败的最小随机延时。</summary>
    public TimeSpan FailureDelayMin { get; init; } =
        TimeSpan.FromMilliseconds(AuthProtocol.FailureDelayMinMilliseconds);

    /// <summary>每次密码学失败的最大随机延时。</summary>
    public TimeSpan FailureDelayMax { get; init; } =
        TimeSpan.FromMilliseconds(AuthProtocol.FailureDelayMaxMilliseconds);
}

/// <summary>
/// 服务端认证会话的素材与守卫（按 Host 装配，跨连接共享；每个连接开出的
/// <see cref="ControlAuthSession"/> 读它、不改它）。
/// </summary>
/// <remarks>
/// <para><b>哪些是共享态</b>：<see cref="FailedAuthLimiter"/> /
/// <see cref="PendingApprovalLimiter"/> / <see cref="SessionRegistry"/> 都是
/// <b>跨连接</b>的host 级状态——每个认证会话持同一实例，正是「限流按 IP 累计」「待批全局有界」
/// 「会话表统一登记」的前提。忘记共享（每连接 new 一个）会把它们全部退化成摆设。</para>
/// <para><see cref="PendingApprovalLimiter"/> 复用 <see cref="ConnectionAdmissionLimiter"/>
/// 的机制（全局 + 每源有界租约，已被 B15 测试覆盖），但必须是<b>独立实例</b>、独立配额
/// （评审 A12：与连接准入分离；初值全局 3 / 每来源 1）。</para>
/// </remarks>
public sealed record ControlAuthContext
{
    /// <summary>本机设备号（进 challenge 的 <c>serverDeviceId</c>，同时是 transcript 素材）。</summary>
    public required Guid ServerDeviceId { get; init; }

    /// <summary>
    /// 访问密钥来源。每会话<b>每次</b>加载一次（<c>LoadOrCreateAsync</c>），用后清零；
    /// 实现的返回值必须是<b>新副本</b>（现有 DPAPI 实现每次解码出新数组，满足）。
    /// </summary>
    public required IAccessSecretStore AccessSecretStore { get; init; }

    /// <summary>失败限流（按 remote IP；只计「到达密码学校验且失败」，ADR-038 第 2 条）。</summary>
    public required FailedAuthLimiter FailedAuthLimiter { get; init; }

    /// <summary>待批配额（独立于连接准入；全局 3 / 每源 1，初值）。</summary>
    public required ConnectionAdmissionLimiter PendingApprovalLimiter { get; init; }

    /// <summary>本机审批面（库内真接口；产品 WPF UI 不动——M4 边界见 HANDOFF §18.3）。</summary>
    public required ILocalApprovalGate ApprovalGate { get; init; }

    /// <summary>已认证会话登记表（DoD：auth success 才能有 session）。</summary>
    public required SessionRegistry SessionRegistry { get; init; }

    /// <summary>时钟（审批请求的过期时刻等；限流器的时钟在自己的构造器参数里）。</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>值旋钮。</summary>
    public ControlAuthOptions Options { get; init; } = new();
}
