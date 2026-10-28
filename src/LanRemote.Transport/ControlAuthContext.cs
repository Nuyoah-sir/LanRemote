using System.Runtime.ExceptionServices;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
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
    // Host 共用；即使一个 store Task 无视取消，也不得由后续连接积累无限迟到任务。
    internal AuthenticationSecretLoader SecretLoader { get; } = new();

    // 未配置仍为原认证保持路径；显式来源由 Router 在认证后生命周期内启动。
    internal HostDisplayCatalogSendOptions? DisplayCatalogSend { get; init; }

    // 产品 Host 显式启用后才发送 ping；普通认证/旧夹具保持原有帧序列。
    internal ControlPingOptions? ControlPing { get; init; }

    /// <summary>返回装配单次只读显示器目录来源的副本；保留本 Host 的所有共享守卫。</summary>
    /// <remarks>
    /// 配置时不调用来源、时钟或 I/O。来源仅由已完成认证/审批/登记及秘密清理的连接调用，
    /// 每连接至多一次；不同连接可以并发调用，来源不得共享未同步的可变状态。
    /// budget 覆盖来源、校验、序列化、写出和预算清理；停止不取消或遗弃来源原任务，
    /// 不合作的同步调用或原任务可能使最终等待不结束。返回的数据仅为历史元数据，非采集或切屏授权。
    /// 来源必须交还代表全部所属工作的 ValueTask，不得隐匿后台操作；列表交还后不得再并发修改。
    /// </remarks>
    public ControlAuthContext WithDisplayCatalogSource(
        Func<ValueTask<IReadOnlyList<DisplayInfo>>> source, TimeSpan budget)
    {
        ArgumentNullException.ThrowIfNull(source);
        return this with
        {
            DisplayCatalogSend = new HostDisplayCatalogSendOptions(
                // 非 async 桥：source/AsTask 未交还原 Task 的同步异常仍由 sender 记录未知退出。
                () => new ValueTask<DisplayCatalogFrame>(ProjectCatalogAsync(source().AsTask())), budget),
        };
    }

    private static async Task<DisplayCatalogFrame> ProjectCatalogAsync(Task<IReadOnlyList<DisplayInfo>> original)
    {
        IReadOnlyList<DisplayInfo> displays;
        try { displays = await original.ConfigureAwait(false); }
        catch
        {
            // await 只抛一个异常；保留原 Task 的兄弟、嵌套和重复引用，不 Flatten/去重。
            if (original.Exception is { } tree)
                ExceptionDispatchInfo.Capture(tree.InnerExceptions.Count == 1 ? tree.InnerExceptions[0] : tree).Throw();
            throw;
        }
        // 当前每父连接仅一次发送，seq=0；UTC仅消息元数据，不作为单调预算、拓扑代或视频坐标映射。
        return new DisplayCatalogFrame(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), displays);
    }

    /// <summary>
    /// 在已装配目录来源的基础上启用自动切屏协调：同一会话写者发送目录与回执，
    /// 并按每个真实认证会话的身份准备目标显示器生产者。
    /// </summary>
    /// <remarks>
    /// 返回装配副本，不调用工厂、时钟或 I/O。prepare 收到的 sessionId 是本 Host 实际登记的认证会话身份，
    /// 由 Transport 在协调器内从登记表原样传入；调用方不得共享或伪造该标识。
    /// 目录仍为每连接单次发送，不增加动态刷新；与显式请求处理器互斥，只能装配一次。
    /// </remarks>
    internal ControlAuthContext WithSessionDisplayRequestPrepare(
        Func<Guid, DisplayInfo, CancellationToken, Task<IVideoFrameProducer>> prepare)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        HostDisplayCatalogSendOptions options = DisplayCatalogSend
            ?? throw new InvalidOperationException("必须先装配显示目录来源，才能启用切屏协调。");
        if (options.DisplayRequestHandler is not null)
            throw new ArgumentException("请求处理器与自动协调准备工厂不能同时配置。", nameof(prepare));
        if (options.DisplayRequestPrepare is not null || options.SessionDisplayRequestPrepare is not null)
            throw new InvalidOperationException("切屏协调准备工厂只能装配一次。");
        // 选项为普通类而非记录；同一 Source/Budget 显式重建，不共享可变态。
        HostDisplayCatalogSendOptions coordinated = new(options.Source, options.Budget)
        {
            UseSessionWriter = true,
            SessionDisplayRequestPrepare = prepare,
        };
        return this with { DisplayCatalogSend = coordinated };
    }

    /// <summary>在既有会话写者上启用 Host ping 与按会话 RTT；不新增写者或公开 API。</summary>
    internal ControlAuthContext WithControlPing(ControlPingTelemetry telemetry, TimeSpan interval, TimeSpan pendingTimeout)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        if (DisplayCatalogSend?.UseSessionWriter != true)
            throw new InvalidOperationException("ping 必须装配在既有会话写者与请求读者上。");
        if (ControlPing is not null) throw new InvalidOperationException("Control ping 只能装配一次。");
        return this with { ControlPing = new(telemetry, interval, pendingTimeout) };
    }

    /// <summary>观察每会话视频来源的发布与摘槽；仅供产品 Host 登记/注销，不改变来源生命周期。</summary>
    internal ControlAuthContext WithVideoSourceObserver(
        Action<Guid, ReplaceableVideoFrameSource> published, Action<Guid> withdrawn)
    {
        ArgumentNullException.ThrowIfNull(published);
        ArgumentNullException.ThrowIfNull(withdrawn);
        HostDisplayCatalogSendOptions options = DisplayCatalogSend
            ?? throw new InvalidOperationException("必须先装配显示目录来源，才能观察视频来源。");
        if (options.VideoSourcePublished is not null || options.VideoSourceWithdrawn is not null)
            throw new InvalidOperationException("视频来源观察只能装配一次。");
        HostDisplayCatalogSendOptions observed = new(options.Source, options.Budget)
        {
            UseSessionWriter = options.UseSessionWriter,
            DisplayRequestPrepare = options.DisplayRequestPrepare,
            SessionDisplayRequestPrepare = options.SessionDisplayRequestPrepare,
            DisplayRequestHandler = options.DisplayRequestHandler,
            VideoSourcePublished = published,
            VideoSourceWithdrawn = withdrawn,
        };
        return this with { DisplayCatalogSend = observed };
    }

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

    /// <summary>单调时间及 timer 驱动认证窗口；UTC 仅供审批显示。限流器使用自己的时钟。</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>值旋钮。</summary>
    public ControlAuthOptions Options { get; init; } = new();
}
