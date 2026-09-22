using System.Net.Security;
using System.Security.Cryptography;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport;

/// <summary>
/// 认证会话的结局。
/// </summary>
/// <param name="Completed">是否认证成功（<see cref="ControlSessionState.Authenticated"/> 且会话已登记）。</param>
/// <param name="State">结局状态（成功 = <see cref="ControlSessionState.Authenticated"/>，其余 = <see cref="ControlSessionState.Closed"/>）。</param>
/// <param name="Rejection">失败短原因，<b>只用于本地日志</b>（对外一律 generic <c>authentication_failed</c>）。</param>
/// <param name="SessionId">本会话 id（成败都有，供日志关联）。</param>
public sealed record ControlAuthResult(
    bool Completed,
    ControlSessionState State,
    string? Rejection,
    Guid SessionId);

/// <summary>
/// Host 侧的认证会话（M4 阶段 3，步骤 13–17）：challenge → response 验证 → 本机审批 →
/// <c>auth_success</c> + 会话登记；认证成功后保持连接，直到客户端断开或停机。
/// </summary>
/// <remarks>
/// <para><b>验证链</b>（规格 04 §9）：机器窗口（绝对 deadline，写/读/校验全程）→ 帧级严格解析
/// （格式违规不计限流）→ 限流前置（被罚 IP 连 challenge 都不发）→ 载荷重算 +
/// <see cref="CryptographicOperations.FixedTimeEquals"/>（唯一计入限流的失败）。
/// 「session/device 对齐」不靠独立字段比对——proof 重算把 sessionId / serverDeviceId /
/// 两端 nonce / 本连接证书指纹全部隐式绑死（跨会话、跨连接的重放必然 MAC 失败）。</para>
/// <para><b>一切失败对外的形式都相同</b>：<see cref="AuthenticationFailedFrame"/>——不区分
/// 「密码错 / 设备码错 / 审批拒 / 被限流」；细节只进本对象的结果短码（本地）。</para>
/// <para><b>绝对 deadline 而非可重置空闲时限</b>：机器窗口自 challenge 写出前起算、永不因对端
/// 活动而重置；审批窗口在调用审批面前起算，含同步前缀、UI 调度及人类等待。
/// 两者均按单调时间裁决，到点即拒；取消只负责唤醒，不承诺硬中断同步本机代码。</para>
/// <para><b>本类每次连接一个实例</b>（由 <see cref="ControlPreAuthHandoff.BeginAuthentication"/>
/// 产出），<see cref="RunAsync"/> 只允许一次。信号处理：只有<b>停机取消</b>会向上抛
/// <see cref="OperationCanceledException"/>（与 pre-auth 同规），其余取消都归为对端可见的拒绝。</para>
/// <para><b>本类不 Dispose 流</b>：最终释放永远在 <c>TransportHost.HandleAsync</c> 的
/// <c>finally</c>（ADR-037 第 6 条）。</para>
/// </remarks>
public sealed class ControlAuthSession
{
    // ─────────────────────────── 本地拒绝短码（绝不下发对端） ───────────────────────────

    /// <summary>该 IP 处于失败封禁中（未发出 challenge）。</summary>
    public const string RejectThrottled = "auth-throttled";

    /// <summary>对端已断开（EOF / RST）。</summary>
    public const string RejectEof = "auth-eof";

    /// <summary>帧超限 / 长度为 0 / 解析违规（附解析器短码；<b>不计</b>入限流）。</summary>
    public const string RejectFrameViolation = "auth-frame";

    /// <summary>机器窗口到点（challenge 写出后对端拖过绝对 deadline）。</summary>
    public const string RejectTimeout = "auth-timeout";

    /// <summary>密码学校验失败（重算 + FixedTimeEquals；<b>唯一</b>计入限流的失败）。</summary>
    public const string RejectProofMismatch = "auth-proof-mismatch";

    /// <summary>访问密钥加载失败（fail closed）。</summary>
    public const string RejectKeyUnavailable = "auth-key-unavailable";

    /// <summary>审批期间收到越界数据（M4 的 response 之后对端应保持沉默）。</summary>
    public const string RejectUnexpectedData = "auth-unexpected-data";

    /// <summary>待批配额已满（直接终止；不产生 UI 提示——评审 1.11）。</summary>
    public const string RejectApprovalQuota = "auth-approval-quota";

    /// <summary>操作者拒绝。</summary>
    public const string RejectApprovalDenied = "auth-approval-denied";

    /// <summary>决定作废（Cancelled）。</summary>
    public const string RejectApprovalCancelled = "auth-approval-cancelled";

    /// <summary>审批面不可用（fail closed）。</summary>
    public const string RejectApprovalUnavailable = "auth-approval-unavailable";

    /// <summary>决定无效（请求 ID 不回指 / Approved 缺权限 / 越权授予 / 未知终态）。</summary>
    public const string RejectApprovalInvalid = "auth-approval-invalid";

    /// <summary>审批 pending 帧本地写出超时（尚未提交审批请求）。</summary>
    public const string RejectApprovalPendingNotDelivered = "auth-approval-pending-not-delivered";

    /// <summary>审批窗口到点。</summary>
    public const string RejectApprovalTimeout = "auth-approval-timeout";

    /// <summary>审批期间连接断开（绝不因此发放 token——评审 #45）。</summary>
    public const string RejectApprovalDisconnected = "auth-approval-disconnected";

    /// <summary><c>auth_success</c> 本地写出失败（不登记、不保持；写成功亦不证明对端已接收）。</summary>
    public const string RejectSuccessNotDelivered = "auth-success-not-delivered";

    private readonly SslStream _stream;
    private readonly TransportTimeouts _timeouts;
    private readonly byte[] _serverNonce;
    private readonly AuthChallengeFrame _challenge;
    private int _started;

    internal ControlAuthSession(
        SslStream stream,
        ConnectionSecurityContext security,
        ControlAuthContext context,
        TransportTimeouts timeouts)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(security);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(timeouts);
        ArgumentNullException.ThrowIfNull(context.AccessSecretStore);
        ArgumentNullException.ThrowIfNull(context.FailedAuthLimiter);
        ArgumentNullException.ThrowIfNull(context.PendingApprovalLimiter);
        ArgumentNullException.ThrowIfNull(context.ApprovalGate);
        ArgumentNullException.ThrowIfNull(context.SessionRegistry);
        ArgumentNullException.ThrowIfNull(context.TimeProvider);

        ValidateOptions(context.Options);

        _stream = stream;
        Security = security;
        Context = context;
        _timeouts = timeouts;

        SessionId = Guid.NewGuid();
        _serverNonce = RandomNumberGenerator.GetBytes(AuthProtocol.NonceByteLength);

        // challenge 在构造期定稿：sessionId / nonce 此后不再变化（重放面 = 0）。
        // certSha256 只从本连接冻结的上下文派生（评审 A11）——绝不"再去查一次"。
        _challenge = new AuthChallengeFrame(
            SessionId,
            context.ServerDeviceId,
            _serverNonce,
            security.ServerCertificateSha256.Span,
            context.Options.ChallengeExpiresInMs);
    }

    /// <summary>本会话 id（进 challenge；transcript 首字段之一）。</summary>
    public Guid SessionId { get; }

    /// <summary>本连接冻结的安全上下文。</summary>
    public ConnectionSecurityContext Security { get; }

    /// <summary>认证素材与守卫。</summary>
    public ControlAuthContext Context { get; }

    /// <summary>当前状态：起始 <see cref="ControlSessionState.Authenticating"/>；
    /// 成功 → <see cref="ControlSessionState.Authenticated"/>；失败 → <see cref="ControlSessionState.Closed"/>。</summary>
    public ControlSessionState State { get; private set; } = ControlSessionState.Authenticating;

    /// <summary>
    /// 跑完整条认证链。
    /// </summary>
    /// <param name="cancellationToken">停机取消。</param>
    /// <returns>结局（认证成功时 <see cref="ControlAuthResult.Completed"/> = true）。</returns>
    /// <remarks>
    /// <b>停机取消会向上抛 <see cref="OperationCanceledException"/></b>，不包装成结果
    /// （与 <see cref="ControlPreAuthSession.RunAsync"/> 同规）：那不是对端做错了什么。
    /// 认证成功后本方法继续挂住连接直到对端断开——「会话的宿主 = 连接」（规格 04 §10）。
    /// </remarks>
    /// <exception cref="InvalidOperationException">同一个实例被 run 了第二次。</exception>
    public async Task<ControlAuthResult> RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException(
                "一个连接的认证只允许跑一次（BeginAuthentication 的 exactly-once 的下游一半）。");
        }

        byte[]? accessKeyBytes = null;
        try
        {
            // ① 限流前置：被罚 IP 连 challenge 都不发（ADR-038 第 2 条的计数口径在别处——
            //    这里只读、不写；被拒本身不计任何数）。
            if (Context.FailedAuthLimiter.IsBlocked(Security.RemoteAddress, out _))
            {
                return await FailAsync(RejectThrottled, cancellationToken).ConfigureAwait(false);
            }

            // ② 安全接受窗口覆盖至 proof verdict；timer 只负责唤醒，单调后置检查负责裁决。
            AuthResponseFrame response;
            byte[] clientTranscript;
            bool proofMatches;
            using (AuthenticationDeadline machine = new(
                Context.TimeProvider, Context.Options.MachineWindow, cancellationToken))
            {
                try
                {
                    CheckDeadline(machine, cancellationToken);
                    await FrameWriter.WriteFrameAsync(
                        _stream,
                        _challenge.Serialize(),
                        TransportConstants.MaxPreAuthMessageBytes,
                        _timeouts.LengthPrefixTimeout,
                        machine.Token).ConfigureAwait(false);

                    FrameReader reader = new(_stream);
                    byte[] responsePayload = await reader.ReadFrameAsync(
                        TransportConstants.MaxPreAuthMessageBytes,
                        _timeouts.LengthPrefixTimeout,
                        _timeouts.PayloadTimeout,
                        machine.Token).ConfigureAwait(false);
                    CheckDeadline(machine, cancellationToken);

                    // ③ 严格解析；先检查时间再提交格式结论，垃圾帧不触碰密钥。
                    bool parsed = AuthResponseFrame.TryParse(
                        responsePayload, out AuthResponseFrame? candidate, out string? parseRejection);
                    CheckDeadline(machine, cancellationToken);
                    if (!parsed || candidate is null)
                    {
                        return await FailAsync(
                            $"{RejectFrameViolation}:{parseRejection}", cancellationToken).ConfigureAwait(false);
                    }
                    response = candidate;

                    // ④ Host 共用 loader：取消后最多遗留一项实际 store 工作，迟到成功值有清零所有者。
                    try
                    {
                        AccessSecret secret = await Context.SecretLoader.LoadAsync(
                            Context.AccessSecretStore, machine.Token).ConfigureAwait(false);
                        accessKeyBytes = secret.AccessKeyBytes;
                    }
                    catch (Exception)
                    {
                        CheckDeadline(machine, cancellationToken);
                        return await FailAsync(RejectKeyUnavailable, cancellationToken).ConfigureAwait(false);
                    }
                    CheckDeadline(machine, cancellationToken);

                    // ⑤ 同步 HMAC/比较也在窗口内：最终检查之前不得写 limiter 或进入审批。
                    clientTranscript = AuthTranscriptBuilder.BuildClientTranscript(
                        SessionId,
                        Context.ServerDeviceId,
                        response.ClientDeviceId,
                        _serverNonce,
                        response.ClientNonce.Span,
                        Security.ServerCertificateSha256.Span,
                        response.RequestedPermission);
                    byte[] expectedProof =
                        AuthTranscriptBuilder.ComputeClientProof(accessKeyBytes, clientTranscript);
                    try
                    {
                        proofMatches = CryptographicOperations.FixedTimeEquals(
                            expectedProof, response.ClientProof.Span);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(expectedProof);
                    }

                    // proof verdict 的线性化点：到点即超时，优先于正确或错误的 MAC 结果。
                    CheckDeadline(machine, cancellationToken);
                    if (proofMatches)
                    {
                        Context.FailedAuthLimiter.RecordSuccess(Security.RemoteAddress);
                    }
                    else
                    {
                        Context.FailedAuthLimiter.RecordFailure(Security.RemoteAddress);
                    }
                }
                catch (FrameProtocolException ex)
                {
                    return await FailAsync(
                        $"{RejectFrameViolation}:{ex.Reason}", cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    return await FailAsync(RejectEof, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return await FailAsync(RejectTimeout, cancellationToken).ConfigureAwait(false);
                }
            }

            if (!proofMatches)
            {
                // 已及时确定的错误 proof；延时与失败帧属于收尾，不延长安全接受窗口。
                await Task.Delay(RandomFailureDelay(), cancellationToken).ConfigureAwait(false);
                return await FailAsync(RejectProofMismatch, cancellationToken).ConfigureAwait(false);
            }

            // ⑥ 本机审批（v1：每个新控制连接都要批；ADR-038 第 3 条）。
            SessionPermission grantedPermission = response.RequestedPermission;
            Task<int>? pendingRead = null;
            if (Context.Options.RequireLocalApproval)
            {
                ApprovalAttempt attempt = await RunApprovalAsync(response, cancellationToken)
                    .ConfigureAwait(false);
                if (attempt.Rejection is not null)
                {
                    return await FailAsync(attempt.Rejection, cancellationToken).ConfigureAwait(false);
                }

                grantedPermission = attempt.Granted!.Value;
                pendingRead = attempt.ReadTask;
                // success 写出/登记若中途退出，Host 关流仍可能使挂起读 fault；观察不另开读者。
                if (pendingRead is not null)
                {
                    ObserveQuietly(pendingRead);
                }
            }

            // ⑦ serverProof（绑 grant 档）+ sessionToken + auth_success。
            //    DoD「raw key 绝不上网」：这一帧里只有 proof / token，没有 key。
            byte[] grantTranscript =
                AuthTranscriptBuilder.BuildGrantTranscript(clientTranscript, grantedPermission);
            byte[] serverProof = AuthTranscriptBuilder.ComputeServerProof(accessKeyBytes, grantTranscript);
            byte[] sessionToken = RandomNumberGenerator.GetBytes(AuthProtocol.SessionTokenByteLength);

            AuthSuccessFrame success = new(
                grantedPermission, serverProof, sessionToken, Context.Options.VideoAttachExpiresInMs);

            try
            {
                await FrameWriter.WriteFrameAsync(
                    _stream,
                    success.Serialize(),
                    TransportConstants.MaxPreAuthMessageBytes,
                    _timeouts.LengthPrefixTimeout,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return await FailAsync(RejectSuccessNotDelivered, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException)
            {
                // 本地写出失败：不登记、不保持。写出成功也不等于对端已经接收/接受。
                return await FailAsync(RejectSuccessNotDelivered, cancellationToken).ConfigureAwait(false);
            }

            // ⑧ 登记 + 保持（#45：决定已转移、success 本地写出成功，才登记 session）。
            State = ControlSessionState.Authenticated;

            using (SessionRegistry.SessionRegistration registration = Context.SessionRegistry.Register(
                SessionId,
                Security.ConnectionId,
                response.ClientDeviceId,
                response.ClientName,
                grantedPermission,
                Security.RemoteAddress,
                Security.RemotePort,
                sessionToken))
            {
                await HoldUntilDisconnectAsync(pendingRead, _stream, cancellationToken).ConfigureAwait(false);
            }

            return new ControlAuthResult(true, ControlSessionState.Authenticated, null, SessionId);
        }
        finally
        {
            if (accessKeyBytes is not null)
            {
                CryptographicOperations.ZeroMemory(accessKeyBytes);
            }

            if (State != ControlSessionState.Authenticated)
            {
                State = ControlSessionState.Closed;
            }
        }
    }

    /// <summary>
    /// 审批阶段：配额 → <c>approval_pending</c> → 三路竞速（决定 / 客户端活动 / 窗口到点）。
    /// </summary>
    /// <remarks>
    /// 「客户端活动」是一路 1 字节读：它同时承担两个职责——审批期间侦测断连（评审 #45：
    /// 断连绝不发放 token），以及成功后的保持（把这次读原样交给调用方复用为「挂住连接」的读，
    /// 全程只有一个读者在消费这条流）。
    /// </remarks>
    private async Task<ApprovalAttempt> RunApprovalAsync(
        AuthResponseFrame response,
        CancellationToken cancellationToken)
    {
        // 配额：独立于连接准入（评审 1.11；初值全局 3 / 每源 1）。超限 = 直接终止、不产生 UI 提示。
        if (!Context.PendingApprovalLimiter.TryAcquire(Security.RemoteAddress, out AdmissionLease? lease)
            || lease is null)
        {
            return new ApprovalAttempt(RejectApprovalQuota, null, null);
        }

        using (lease)
        {
            // approval_pending：告诉对端「已受理，等审批」（规格 04 §9 的空标志帧）。
            try
            {
                await FrameWriter.WriteFrameAsync(
                    _stream,
                    ApprovalPendingFrame.Serialize(),
                    TransportConstants.MaxPreAuthMessageBytes,
                    _timeouts.LengthPrefixTimeout,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new ApprovalAttempt(RejectApprovalPendingNotDelivered, null, null);
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException)
            {
                return new ApprovalAttempt(RejectEof, null, null);
            }

            Guid requestId = Guid.NewGuid();
            string shortCode = LocalApprovalRequest.ComputeShortCode(SessionId, response.ClientNonce.Span);
            Task<int> clientActivity = ReadOneByteAsync(_stream, cancellationToken);

            // 调用 gate 前起算；包含同步前缀与 UI 调度。UTC 仅供显示，不参与接受判据。
            using AuthenticationDeadline approval = new(
                Context.TimeProvider, Context.Options.ApprovalWindow, cancellationToken);
            LocalApprovalRequest request = new(
                requestId,
                Security.ConnectionId,
                SessionId,
                Security.RemoteAddress,
                Security.RemotePort,
                response.ClientDeviceId,
                response.ClientName,
                response.RequestedPermission,
                shortCode,
                Context.TimeProvider.GetUtcNow() + approval.Remaining);
            Task<LocalApprovalDecision> decision = RequestApprovalSafeAsync(request, approval.Token);
            Task window = Task.Delay(Timeout.InfiniteTimeSpan, approval.Token);

            Task<int>? handOff = null;
            try
            {
                // WhenAny 只唤醒；多个 ready 的任务不能由数组顺序决定授权或本地短码。
                await Task.WhenAny(clientActivity, decision, window).ConfigureAwait(false);
                string? stop = await ApprovalStopAsync(
                    approval, clientActivity, cancellationToken).ConfigureAwait(false);
                if (stop is not null)
                {
                    return new ApprovalAttempt(stop, null, null);
                }

                LocalApprovalDecision? decided;
                try
                {
                    decided = await decision.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // gate 自身出错/取消，不应胜过已经发生的窗口或断连。
                    stop = await ApprovalStopAsync(
                        approval, clientActivity, cancellationToken).ConfigureAwait(false);
                    return new ApprovalAttempt(stop ?? RejectApprovalUnavailable, null, null);
                }

                string? rejection = decided is null || decided.RequestId != requestId
                    ? RejectApprovalInvalid
                    : decided.Outcome switch
                    {
                        LocalApprovalOutcome.Approved =>
                            decided.GrantedPermission is { } granted
                            && IsGrantable(granted, response.RequestedPermission)
                                ? null : RejectApprovalInvalid,
                        LocalApprovalOutcome.Denied => RejectApprovalDenied,
                        LocalApprovalOutcome.TimedOut => RejectApprovalTimeout,
                        LocalApprovalOutcome.Cancelled => RejectApprovalCancelled,
                        LocalApprovalOutcome.Unavailable => RejectApprovalUnavailable,
                        _ => RejectApprovalInvalid,
                    };

                // 用户拍板：按状态机接受时刻，而非 UI 点击时间。取消 > 到期 > 已观测活动 > 决定。
                stop = await ApprovalStopAsync(
                    approval, clientActivity, cancellationToken).ConfigureAwait(false);
                if (stop is not null || rejection is not null)
                {
                    return new ApprovalAttempt(stop ?? rejection, null, null);
                }

                handOff = clientActivity;
                return new ApprovalAttempt(null, decided!.GrantedPermission, handOff);
            }
            finally
            {
                // 终态已经决定；取消只是合作式清理，不能使迟到决定反转结果。
                // gate 的回调须快速非阻塞；回调抛错不得掩盖既定的安全结局。
                try
                {
                    approval.Cancel();
                }
                catch (AggregateException)
                {
                }
                ObserveQuietly(decision);
                if (handOff is null)
                {
                    ObserveQuietly(clientActivity);
                }
            }
        }
    }

    /// <summary>
    /// 认证成功后的保持：等对端断开（单个 1 字节读；<c>pendingRead</c> 为审批期已挂上的同一读）。
    /// </summary>
    /// <remarks>
    /// M4 尚无 control 消息消费方，行为定义到「保持直到客户端断开或停机」为止：
    /// EOF / RST / 越界数据都结束会话（数据在此阶段没有消费方，不静默丢弃也不假装理解）。
    /// </remarks>
    private static async Task HoldUntilDisconnectAsync(
        Task<int>? pendingRead,
        Stream stream,
        CancellationToken cancellationToken)
    {
        Task<int> read = pendingRead ?? ReadOneByteAsync(stream, cancellationToken);

        try
        {
            // 0 = 有序 EOF（对端正常关闭）；>0 = 越界数据（M4 无消费方）——两者都结束会话。
            _ = await read.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // RST / 流被释放：会话随连接结束，正常收尾。
        }
    }

    /// <summary>读 1 字节（断连侦测 / 保持读；异常也捕获进返回的 Task，不在调用点同步抛）。</summary>
    private static Task<int> ReadOneByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        return ReadAsync();

        async Task<int> ReadAsync()
        {
            byte[] buffer = new byte[1];
            return await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>失败收尾：尽量礼貌地发一帧 generic <c>authentication_failed</c>，然后返回结局。</summary>
    private async Task<ControlAuthResult> FailAsync(string rejection, CancellationToken cancellationToken)
    {
        State = ControlSessionState.Closed;
        await SendFailureAsync(cancellationToken).ConfigureAwait(false);
        return new ControlAuthResult(false, ControlSessionState.Closed, rejection, SessionId);
    }

    /// <summary>
    /// best-effort 发送 generic 失败帧。
    /// </summary>
    /// <remarks>
    /// 失败帧是礼数不是义务：对端可能早走了，发不出去不改变「已拒绝」的事实。
    /// 但<b>停机取消照常向上抛</b>（与 pre-auth 的「不包装成结果」一致）——
    /// 其余异常（含分段时限的取消）一律吞掉。
    /// </remarks>
    private async Task SendFailureAsync(CancellationToken cancellationToken)
    {
        try
        {
            await FrameWriter.WriteFrameAsync(
                _stream,
                AuthenticationFailedFrame.Serialize(),
                TransportConstants.MaxPreAuthMessageBytes,
                _timeouts.LengthPrefixTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
        }
    }

    /// <summary>调用 gate 并把「gate 同步抛」也捕获进 Task。</summary>
    private async Task<LocalApprovalDecision> RequestApprovalSafeAsync(
        LocalApprovalRequest request,
        CancellationToken cancellationToken)
    {
        return await Context.ApprovalGate
            .RequestApprovalAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>「观察掉」一个任务的异常（不等待其完成，不让未观察异常漏出去）。</summary>
    private static void ObserveQuietly(Task task)
    {
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private static void CheckDeadline(AuthenticationDeadline deadline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (deadline.IsExpired)
        {
            throw new OperationCanceledException("认证安全接受窗口到期。", deadline.Token);
        }
        deadline.Token.ThrowIfCancellationRequested();
    }

    internal static async Task<string?> ApprovalStopAsync(
        AuthenticationDeadline deadline, Task<int> clientActivity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (deadline.IsExpired)
        {
            return RejectApprovalTimeout;
        }
        if (!clientActivity.IsCompleted)
        {
            return null;
        }
        try
        {
            int bytes = await clientActivity.ConfigureAwait(false);
            return bytes == 0 ? RejectApprovalDisconnected : RejectUnexpectedData;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return RejectApprovalDisconnected;
        }
    }

    /// <summary>授予权限的合法性：<c>granted ≤ requested</c>（评审 #39；不依赖枚举序数）。</summary>
    private static bool IsGrantable(SessionPermission granted, SessionPermission requested) => granted switch
    {
        // 降级（control → view）永远可授予；view 本来就是下限。
        SessionPermission.ViewOnly => true,
        // 升级只在客户端本来就请求了 control 时允许。
        SessionPermission.Control => requested == SessionPermission.Control,
        // 未定义枚举值：不授予。
        _ => false,
    };

    /// <summary>规格 04 §14 的随机失败延时（300~800 ms；测试经选项缩放到 0）。</summary>
    private TimeSpan RandomFailureDelay()
    {
        TimeSpan min = Context.Options.FailureDelayMin;
        TimeSpan max = Context.Options.FailureDelayMax;

        if (max <= TimeSpan.Zero || max <= min)
        {
            return min > TimeSpan.Zero ? min : TimeSpan.Zero;
        }

        int minMs = (int)min.TotalMilliseconds;
        int maxMs = (int)max.TotalMilliseconds;
        return TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32(minMs, maxMs + 1));
    }

    private static void ValidateOptions(ControlAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        ArgumentOutOfRangeException.ThrowIfLessThan(options.ChallengeExpiresInMs, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.VideoAttachExpiresInMs, 1);

        if (options.MachineWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MachineWindow, "机器窗口必须为正。");
        }

        if (options.ApprovalWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ApprovalWindow, "审批窗口必须为正。");
        }

        if (options.FailureDelayMin < TimeSpan.Zero || options.FailureDelayMax < options.FailureDelayMin
            || options.FailureDelayMax.TotalMilliseconds >= int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.FailureDelayMax, "失败延时范围必须非负且上限 ≥ 下限。");
        }
    }

    /// <summary>审批阶段的中间结论：拒绝码 / 授予权限 / 交给保持阶段的读，三选一或组合。</summary>
    private sealed record ApprovalAttempt(
        string? Rejection,
        SessionPermission? Granted,
        Task<int>? ReadTask);
}
