namespace LanRemote.Transport;

/// <summary>
/// 控制通道连接在完成 TLS 之后的会话状态。
/// </summary>
/// <remarks>
/// <b>「TLS 握手完成」不等于「认证通过」。</b>本项目把这两件事用两个不同的状态表达，
/// 就是为了防止「反正已经加密了，那就先让它干点活吧」这种推理悄悄发生。
/// </remarks>
public enum ControlSessionState
{
    /// <summary>TLS 已完成，正在等唯一允许的首帧 <c>channel_hello</c>。此刻<b>什么都不能做</b>。</summary>
    AwaitingHello,

    /// <summary>
    /// hello 已通过：<b>已建立经过 pin 校验的加密通道，但对端身份<b>尚未</b>被认证</b>。
    /// 这是外部红队评审要求的显式状态（A 桶第 1 条）。
    /// M4 落地前，此状态下<b>允许的操作集合为空</b>——见
    /// <see cref="ControlPreAuthSession.AllowedOperationsWhilePreAuthenticated"/>。
    /// </summary>
    PreAuthenticated,

    /// <summary>已关闭（被拒绝或正常收尾）。终态。</summary>
    Closed,
}

/// <summary>
/// pre-auth 阶段的结局。
/// </summary>
/// <param name="Completed">是否成功走到 <see cref="ControlSessionState.PreAuthenticated"/>。</param>
/// <param name="State">终态。</param>
/// <param name="Rejection">失败时的短原因码，<b>只用于本地日志</b>。</param>
public sealed record ControlPreAuthResult(
    bool Completed,
    ControlSessionState State,
    string? Rejection);

/// <summary>
/// Host 侧的 pre-auth 会话：读且只读一帧，必须是 <c>channel_hello</c>，然后进入
/// <see cref="ControlSessionState.PreAuthenticated"/>。
/// </summary>
/// <remarks>
/// <para><b>步骤 20</b>：TLS + hello 完成 = 显式的 <see cref="ControlSessionState.PreAuthenticated"/>。
/// 在此之后，屏幕数据 / 输入能力 / session token / 权限判定 / 特权主机元数据一律拒绝。
/// M3 的表达方式就是 <see cref="AllowedOperationsWhilePreAuthenticated"/> 为空集合——
/// 有测试盯着它，M4 之前谁往里加东西谁红。</para>
/// <para><b>步骤 21</b>：M3 单独成里程碑的终态是<b>干净关闭</b>（发 close_notify 后释放），
/// 而不是把未认证 socket 无限期挂着等还不存在的 M4 代码。
/// 没有启用「短绝对占位 deadline 后进入 AwaitingAuthentication」那条备选——
/// M3 里没有任何东西值得为它继续留着连接。</para>
/// <para>本类<b>每次连接一个实例</b>，<see cref="RunAsync"/> 只允许调用一次——
/// 「第二个 hello」在协议上就该断开，这里用「不允许第二次 run」把它在类型层面堵掉。</para>
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
    /// <b>M3 必须为空。</b>M4 落地时把「开始访问密钥认证」这一项加进来。
    /// 这不是占位代码：它是「未认证连接不得换取任何能力」这条约束的<b>可执行形式</b>，
    /// 并且有测试（<c>PreAuthenticated_Allows_Nothing_Before_M4</c>）盯着。
    /// </remarks>
    public static IReadOnlyCollection<string> AllowedOperationsWhilePreAuthenticated { get; } =
        Array.Empty<string>();

    /// <summary>
    /// 跑完 pre-auth：读一帧 → 校验 → 成功则干净关闭并返回。
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

            byte[] payload;
            try
            {
                payload = await reader.ReadFrameAsync(
                    TransportConstants.MaxPreAuthMessageBytes,
                    timeouts.LengthPrefixTimeout,
                    timeouts.PayloadTimeout,
                    cancellationToken).ConfigureAwait(false);
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
                // 阶段时限到点（不是停机）：这是「对端在拖」，必须留下原因。
                // 少了这一条，超时会一路飞出 RunAsync，结局永远产生不出来——
                // 做变异验证时就是靠它「30 秒都没结果」才被发现的。
                return Fail(RejectTimeout);
            }

            if (!HelloFrame.TryParse(payload, out string? rejection))
            {
                return Fail(rejection!);
            }

            State = ControlSessionState.PreAuthenticated;

            // 步骤 21：M3 的终态是干净关闭——把 close_notify 发出去，别让对端靠 RST 猜。
            // 对端可能早就走了，所以这里best-effort。
            try
            {
                await connection.Stream.ShutdownAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 对端已断开 / socket 已被释放：都不影响「我们这一侧已经干净收尾」。
            }

            return new ControlPreAuthResult(true, ControlSessionState.PreAuthenticated, null);
        }
        finally
        {
            // run 结束即终态：成功留在 PreAuthenticated（连接随后由 Host 干净关闭），
            // 其余任何一条失败路径都归到 Closed。
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
