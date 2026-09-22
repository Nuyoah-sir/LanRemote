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
/// 活动而重置；审批窗口自请求提交给审批面起算（排队等待不计入——HANDOFF §18.4）。</para>
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

    /// <summary>审批窗口到点。</summary>
    public const string RejectApprovalTimeout = "auth-approval-timeout";

    /// <summary>审批期间连接断开（绝不因此发放 token——评审 #45）。</summary>
    public const string RejectApprovalDisconnected = "auth-approval-disconnected";

    /// <summary><c>auth_success</c> 没能送达对端（认证不算成立：不登记、不保持）。</summary>
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

            // ② 机器窗口：challenge 写出前起算的绝对 deadline，覆盖写、读与校验判定。
            byte[] responsePayload;
            using (CancellationTokenSource machine =
                   CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                machine.CancelAfter(Context.Options.MachineWindow);

                try
                {
                    await FrameWriter.WriteFrameAsync(
                        _stream,
                        _challenge.Serialize(),
                        TransportConstants.MaxPreAuthMessageBytes,
                        _timeouts.LengthPrefixTimeout,
                        machine.Token).ConfigureAwait(false);

                    FrameReader reader = new(_stream);
                    responsePayload = await reader.ReadFrameAsync(
                        TransportConstants.MaxPreAuthMessageBytes,
                        _timeouts.LengthPrefixTimeout,
                        _timeouts.PayloadTimeout,
                        machine.Token).ConfigureAwait(false);
                }
                catch (FrameProtocolException ex)
                {
                    // 长度违规（含 0 / 超限）：格式违规，不计数。
                    return await FailAsync(
                        $"{RejectFrameViolation}:{ex.Reason}", cancellationToken).ConfigureAwait(false);
                }
                catch (EndOfStreamException)
                {
                    return await FailAsync(RejectEof, cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    return await FailAsync(RejectEof, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // 机器窗口到点（写/读任一段）；停机取消在 when 处排除、向上抛。
                    return await FailAsync(RejectTimeout, cancellationToken).ConfigureAwait(false);
                }

                // 读恰好压线完成也算超时：「response 校验完成 ≤ 窗口」是硬语义。
                if (machine.IsCancellationRequested)
                {
                    return await FailAsync(RejectTimeout, cancellationToken).ConfigureAwait(false);
                }
            }

            // ③ 帧级严格解析（重复键 / 未映射成员 / 尾随数据 / base64 canonical / 尺寸全在帧里判）。
            if (!AuthResponseFrame.TryParse(
                    responsePayload, out AuthResponseFrame? response, out string? parseRejection)
                || response is null)
            {
                return await FailAsync(
                    $"{RejectFrameViolation}:{parseRejection}", cancellationToken).ConfigureAwait(false);
            }

            // ④ 读访问密钥（每会话一次；返回值是新副本，finally 里清零）。
            try
            {
                AccessSecret secret = await Context.AccessSecretStore
                    .LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
                accessKeyBytes = secret.AccessKeyBytes;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return await FailAsync(RejectKeyUnavailable, cancellationToken).ConfigureAwait(false);
            }

            // ⑤ 重算 + FixedTimeEquals。素材 = 本连接冻结的上下文 + challenge 时定稿的 nonce。
            byte[] clientTranscript = AuthTranscriptBuilder.BuildClientTranscript(
                SessionId,
                Context.ServerDeviceId,
                response.ClientDeviceId,
                _serverNonce,
                response.ClientNonce.Span,
                Security.ServerCertificateSha256.Span,
                response.RequestedPermission);

            byte[] expectedProof =
                AuthTranscriptBuilder.ComputeClientProof(accessKeyBytes, clientTranscript);

            if (!CryptographicOperations.FixedTimeEquals(expectedProof, response.ClientProof.Span))
            {
                // 唯一计入限流的失败 + 规格 04 §14 的随机延时（对猜密钥降速）。
                Context.FailedAuthLimiter.RecordFailure(Security.RemoteAddress);
                await Task.Delay(RandomFailureDelay(), cancellationToken).ConfigureAwait(false);
                return await FailAsync(RejectProofMismatch, cancellationToken).ConfigureAwait(false);
            }

            Context.FailedAuthLimiter.RecordSuccess(Security.RemoteAddress);

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
            catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException)
            {
                // 对端没收到 success = 认证没有成立：不登记、不保持。
                return await FailAsync(RejectSuccessNotDelivered, cancellationToken).ConfigureAwait(false);
            }

            // ⑧ 登记 + 保持（#45：决定已转移、success 已送达，才登记 session）。
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
            catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException)
            {
                return new ApprovalAttempt(RejectEof, null, null);
            }

            Guid requestId = Guid.NewGuid();
            LocalApprovalRequest request = new(
                requestId,
                Security.ConnectionId,
                SessionId,
                Security.RemoteAddress,
                Security.RemotePort,
                response.ClientDeviceId,
                response.ClientName,
                response.RequestedPermission,
                LocalApprovalRequest.ComputeShortCode(SessionId, response.ClientNonce.Span),
                Context.TimeProvider.GetUtcNow() + Context.Options.ApprovalWindow);

            Task<int> clientActivity = ReadOneByteAsync(_stream, cancellationToken);

            using CancellationTokenSource abort =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            Task<LocalApprovalDecision> decision = RequestApprovalSafeAsync(request, abort.Token);
            Task window = Task.Delay(Context.Options.ApprovalWindow, cancellationToken);

            Task<int>? handOff = null;
            try
            {
                Task winner = await Task.WhenAny(clientActivity, decision, window).ConfigureAwait(false);

                if (winner == clientActivity)
                {
                    // 审批期间断连 / 越界数据：终态先转移（#44），决定一律作废（#45）。
                    try
                    {
                        int bytesRead = await clientActivity.ConfigureAwait(false);
                        return new ApprovalAttempt(
                            bytesRead == 0 ? RejectApprovalDisconnected : RejectUnexpectedData, null, null);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        return new ApprovalAttempt(RejectApprovalDisconnected, null, null);
                    }
                }

                if (winner == window)
                {
                    return new ApprovalAttempt(RejectApprovalTimeout, null, null);
                }

                // 决定胜出：先消化它（gate 抛异常 = fail closed 当 Unavailable）。
                LocalApprovalDecision decided;
                try
                {
                    decided = await decision.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    return new ApprovalAttempt(RejectApprovalUnavailable, null, null);
                }

                // #45 加固：决定到手时客户端若已经走了，这个决定不作数（断连绝不发 token）。
                if (clientActivity.IsCompleted)
                {
                    try
                    {
                        int bytesRead = await clientActivity.ConfigureAwait(false);
                        return new ApprovalAttempt(
                            bytesRead == 0 ? RejectApprovalDisconnected : RejectUnexpectedData, null, null);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        return new ApprovalAttempt(RejectApprovalDisconnected, null, null);
                    }
                }

                // 决定校验（#37 / #38 / #39）。
                if (decided.RequestId != requestId)
                {
                    return new ApprovalAttempt(RejectApprovalInvalid, null, null);
                }

                switch (decided.Outcome)
                {
                    case LocalApprovalOutcome.Approved:
                        if (decided.GrantedPermission is not { } granted
                            || !IsGrantable(granted, response.RequestedPermission))
                        {
                            return new ApprovalAttempt(RejectApprovalInvalid, null, null);
                        }

                        handOff = clientActivity;
                        return new ApprovalAttempt(null, granted, handOff);

                    case LocalApprovalOutcome.Denied:
                        return new ApprovalAttempt(RejectApprovalDenied, null, null);

                    case LocalApprovalOutcome.TimedOut:
                        return new ApprovalAttempt(RejectApprovalTimeout, null, null);

                    case LocalApprovalOutcome.Cancelled:
                        return new ApprovalAttempt(RejectApprovalCancelled, null, null);

                    case LocalApprovalOutcome.Unavailable:
                        return new ApprovalAttempt(RejectApprovalUnavailable, null, null);

                    default:
                        return new ApprovalAttempt(RejectApprovalInvalid, null, null);
                }
            }
            finally
            {
                // 离开等待区：停掉 gate 侧（best effort），
                // 未被接管的两个任务一律「观察掉」，不让未观察异常漏出去。
                abort.Cancel();
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

        long minMs = (long)min.TotalMilliseconds;
        long maxMs = (long)max.TotalMilliseconds;
        return TimeSpan.FromMilliseconds(Random.Shared.NextInt64(minMs, maxMs + 1));
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

        if (options.FailureDelayMin < TimeSpan.Zero || options.FailureDelayMax < options.FailureDelayMin)
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
