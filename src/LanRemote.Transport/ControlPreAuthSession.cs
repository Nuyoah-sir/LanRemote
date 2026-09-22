namespace LanRemote.Transport;

/// <summary>
/// 控制通道连接在完成 TLS 之后的会话状态。
/// </summary>
/// <remarks>
/// <b>「TLS 握手完成」不等于「认证通过」。</b>本项目把这两件事用两个不同的状态表达，
/// 就是为了防止「反正已经加密了，那就先让它干点活吧」这种推理悄悄发生。
/// 一个枚举贯穿整条链（不另立平行枚举，ADR-037 第 4 条）；后续里程碑（Video Attach 等）
/// 继续扩展本枚举。
/// </remarks>
public enum ControlSessionState
{
    /// <summary>TLS 已完成，正在等唯一允许的首帧 <c>channel_hello</c>。此刻<b>什么都不能做</b>。</summary>
    AwaitingHello,

    /// <summary>
    /// hello 已通过：<b>已建立经过 pin 校验的加密通道，但对端身份<b>尚未</b>被认证</b>。
    /// 这是外部红队评审要求的显式状态（A 桶第 1 条）。
    /// 此状态下<b>允许的操作集合恰好一项</b>——「开始访问密钥认证」，
    /// 见 <see cref="ControlPreAuthSession.AllowedOperationsWhilePreAuthenticated"/>。
    /// </summary>
    PreAuthenticated,

    /// <summary>
    /// 认证进行中（由 <see cref="ControlAuthSession"/> 推进）：challenge 已发出（或即将发出），
    /// 正在等 response / 验证 / 审批。
    /// </summary>
    Authenticating,

    /// <summary>
    /// 认证成功（DoD「auth success 才能有 session」的对应状态）：会话已登记，
    /// 连接保持以承载会话；其生命周期 = 直到客户端断开或停机。
    /// </summary>
    Authenticated,

    /// <summary>已关闭（被拒绝或正常收尾）。终态。</summary>
    Closed,
}

/// <summary>
/// pre-auth 阶段的结局。
/// </summary>
/// <param name="Completed">是否成功走到 <see cref="ControlSessionState.PreAuthenticated"/>。</param>
/// <param name="State">结局状态（成功 = <see cref="ControlSessionState.PreAuthenticated"/>，其余为 <see cref="ControlSessionState.Closed"/>）。</param>
/// <param name="Rejection">失败时的短原因码，<b>只用于本地日志</b>。</param>
/// <param name="Handoff">成功时的<b>一次性交接对象</b>（ADR-037）；失败恒为 <see langword="null"/>。</param>
public sealed record ControlPreAuthResult(
    bool Completed,
    ControlSessionState State,
    string? Rejection,
    ControlPreAuthHandoff? Handoff = null);

/// <summary>
/// Host 侧的 pre-auth 会话：读且只读一帧，必须是 <c>channel_hello</c>，然后进入
/// <see cref="ControlSessionState.PreAuthenticated"/>。
/// </summary>
/// <remarks>
/// <para><b>步骤 20</b>：TLS + hello 完成 = 显式的 <see cref="ControlSessionState.PreAuthenticated"/>。
/// 在此之后，屏幕数据 / 输入能力 / session token / 权限判定 / 特权主机元数据一律拒绝。
/// 表达方式是 <see cref="AllowedOperationsWhilePreAuthenticated"/> 恰好一项——
/// 「开始访问密钥认证」（ADR-037 第 5 条；M3 的空集合版本已随 M4 落地有意识改写）。
/// 有测试盯着这个集合，谁加第二项谁红。</para>
/// <para><b>M3 的「成功即干净关闭」已被 M4 的显式交接取代</b>（ADR-037 第 1 条）：
/// 成功路径<b>不再</b>调用 <c>ShutdownAsync</c>——「活着的流 + 冻结的安全上下文」交给
/// <see cref="ControlPreAuthHandoff"/>，关闭时机随所有权一并移交。M3 的干净关闭是
/// 「没有后继」时的正确终态；M4 有了后继（认证），关闭时机就不该再由 pre-auth 决定。</para>
/// <para>本类<b>每次连接一个实例</b>，<see cref="RunAsync"/> 只允许调用一次——
/// 「第二个 hello」在协议上就该断开，这里用「不允许第二次 run」把它在类型层面堵掉。
/// 认证侧的对应物是 <see cref="ControlPreAuthHandoff.BeginAuthentication"/> 的 exactly-once。</para>
/// </remarks>
public sealed class ControlPreAuthSession
{
    /// <summary>在收到任何字节之前对端就关了。</summary>
    public const string RejectEof = "pre-auth-eof";

    /// <summary>pre-auth 帧超限或长度为 0（来自 <see cref="FrameProtocolException.Reason"/>）。</summary>
    public const string RejectFrameViolation = "pre-auth-frame";

    /// <summary>
    /// 对端在 pre-auth 阶段拖过了某一段的绝对时限。
    /// </summary>
    /// <remarks>
    /// 这不是「停机」：停机是<b>我们</b>主动取消，不该记成对端的错。
    /// 两者的异常类型都是 <see cref="OperationCanceledException"/>，靠
    /// <c>when (!cancellationToken.IsCancellationRequested)</c> 区分。
    /// </remarks>
    public const string RejectTimeout = "pre-auth-timeout";

    private int _started;

    /// <summary>当前状态。</summary>
    public ControlSessionState State { get; private set; } = ControlSessionState.AwaitingHello;

    /// <summary>
    /// <see cref="ControlSessionState.PreAuthenticated"/> 下允许的操作集合。
    /// </summary>
    /// <remarks>
    /// <b>M4 形态：恰好一项——<see cref="ControlPreAuthHandoff.OperationBeginAuthentication"/></b>
    /// （ADR-037 第 5 条）。这不是占位代码：它是「未认证连接不得换取任何能力」这条约束的
    /// <b>可执行形式</b>，并且有测试做<b>精确集合相等</b>（任何人加第二项即红）。
    /// M3 原文（空集合 + 测试名 <c>PreAuthenticated_Allows_Nothing_Before_M4</c>）随本批
    /// 有意识改写，改写理由即本条 ADR。
    /// </remarks>
    public static IReadOnlyCollection<string> AllowedOperationsWhilePreAuthenticated { get; } =
        new[] { ControlPreAuthHandoff.OperationBeginAuthentication };

    /// <summary>
    /// 跑完 pre-auth：读一帧 → 校验 → 成功则留在 <see cref="ControlSessionState.PreAuthenticated"/>
    /// 并交出 <see cref="ControlPreAuthHandoff"/>（ADR-037：不再由本方法关闭连接）。
    /// </summary>
    /// <param name="connection">已完成 TLS 的连接。</param>
    /// <param name="timeouts">时限预算。</param>
    /// <param name="cancellationToken">停机取消。</param>
    /// <returns>结局。</returns>
    /// <remarks>
    /// <b>停机取消会向上抛 <see cref="OperationCanceledException"/></b>，不包装成结果——
    /// 那不是「对端做错了什么」，不该混进拒绝原因里。
    /// </remarks>
    /// <exception cref="InvalidOperationException">同一个实例被 run 了第二次。</exception>
    public async Task<ControlPreAuthResult> RunAsync(
        AcceptedConnection connection,
        TransportTimeouts timeouts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(timeouts);

        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException(
                "一个连接的 pre-auth 只允许跑一次；第二个 hello 属于协议违规，连接应当已经关闭。");
        }

        try
        {
            FrameReader reader = new(connection.Stream);

            // pre-auth 外层信封：自进入本方法（= 进入 pre-auth）起算的硬上限，永不重置。
            // 「分段各自绝对」不蕴含「总量有界」——前缀 5 s 与 payload 10 s 顺序执行即可加和；
            // 信封把「对端可拖占的总时间」封顶（HANDOFF §17 教训 #25）。
            // 它只约束本方法内对端可控的等待（读取）；hello 读取完成后的收尾是本机
            // 非阻塞的 best-effort 写，不在信封的可中断范围内（见 TransportTimeouts 说明）。
            using CancellationTokenSource envelope =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            envelope.CancelAfter(timeouts.PreAuthEnvelopeTimeout);

            byte[] payload;
            try
            {
                payload = await reader.ReadFrameAsync(
                    TransportConstants.MaxPreAuthMessageBytes,
                    timeouts.LengthPrefixTimeout,
                    timeouts.PayloadTimeout,
                    envelope.Token).ConfigureAwait(false);
            }
            catch (FrameProtocolException ex)
            {
                // 长度违规：不 drain，直接关。
                return Fail($"{RejectFrameViolation}:{ex.Reason}");
            }
            catch (EndOfStreamException)
            {
                return Fail(RejectEof);
            }
            catch (IOException)
            {
                return Fail(RejectEof);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 两种来源（全都不含停机，停机在 when 处被排除、向上抛）：
                //   ① 某一段的绝对时限到点（前缀 / payload）；
                //   ② 外层信封到点——对端用「每段都不超时的顺序等待」加和拖时间时，由它兜底。
                // 对端视角同为「超时」；归因差异只体现在本地（当前统一记 pre-auth-timeout，
                // 若将来需要细分，信封与分段各持有独立 CTS，可在此处区分）。
                return Fail(RejectTimeout);
            }

            if (!HelloFrame.TryParse(payload, out string? rejection))
            {
                return Fail(rejection!);
            }

            State = ControlSessionState.PreAuthenticated;

            // ADR-037 第 1 条：成功路径不再关闭连接——「活着的流 + 冻结的安全上下文」交给
            // 一次性交接对象（线性所有权 + exactly-once）。关闭时机随所有权一并移交：
            // 认证成功 → 连接保持（承载 session）；认证失败/停机 → 由持有方与 Host 收尾。
            // 本方法此后不再触碰流；对端可能早走了这种「收尾写」也不复存在（这是有意的：
            // 交接对象不做 IO，只有拿它的 BeginAuthentication 才会再次用到流）。
            ControlPreAuthHandoff handoff = new(connection.Stream, connection.Security);

            return new ControlPreAuthResult(
                true, ControlSessionState.PreAuthenticated, null, handoff);
        }
        finally
        {
            // run 结束即本对象的终态：成功留在 PreAuthenticated（后续由交接对象/认证会话推进，
            // 本对象不再触碰连接），其余任何一条失败路径都归到 Closed。
            if (State != ControlSessionState.PreAuthenticated)
            {
                State = ControlSessionState.Closed;
            }
        }
    }

    private ControlPreAuthResult Fail(string rejection)
    {
        State = ControlSessionState.Closed;
        return new ControlPreAuthResult(false, ControlSessionState.Closed, rejection);
    }
}
