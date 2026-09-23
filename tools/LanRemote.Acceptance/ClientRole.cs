using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using LanRemote.Core.Models;
using LanRemote.Discovery;
using LanRemote.Transport;
using Microsoft.Extensions.Logging;

namespace LanRemote.Acceptance;

/// <summary>
/// 控制端角色：发现 → 冻结快照 → 双向认证 / 低层专项，并对<b>自己的观测</b>给出结论。
/// </summary>
/// <remarks>
/// <para><b>控制端不宣告 M4 里程碑完成</b>：success 只在高层连接器验证 serverProof 后成立，
/// 持有会话五秒仅证明本地对象保有，不证明远端 registry 持续在线或已注销。
/// 必须按 SessionId 交叉核对 Host 的实测采样与注销证据（见 <see cref="WriteCrossCheck"/>）。</para>
/// <para><b>判定必须键在「原因」上，不能键在「失败了」上</b>（评审第 4 条）。
/// <c>pin-mismatch</c> 场景原先把「任何不是 TCP 连不上的握手失败」都算通过，
/// 于是被同子网闸门拒绝、被准入限额拒绝、甚至对端根本不是 LanRemote，
/// 都会报 PASS——真正被测的那行 pinning 代码一行都没跑到。现在必须命中
/// <see cref="PeerCertificateValidator.RejectionPinMismatch"/> 这个短码才算通过。</para>
/// <para><b>不再用 TCP 探针</b>。本机实测（.NET 10.0.12，见 HANDOFF §15）：
/// TCP 层根本没连上时抛的是 <c>SocketException</c>（目标计算机积极拒绝）或
/// <c>IOException</c>（内层 <c>SocketException</c>），<b>永远不会是</b>
/// <c>AuthenticationException</c>。既然判据已经要求「必须是 AuthenticationException
/// 且原因是 pin-mismatch」，端口没开这一情形<b>已经被判据本身排除</b>，
/// 探针是多余的第二次连接——而它自己还会在被控端留下一条不着痕迹的连接，
/// 污染「连接条数」这个交叉核对的依据。</para>
/// </remarks>
internal static class ClientRole
{
    /// <summary>场景：完成双向认证，持有已验证会话五秒，再同步释放。</summary>
    public const string ScenarioSuccess = "success";

    /// <summary>场景：指纹差一位，握手必须被 pinning 拒绝。</summary>
    public const string ScenarioPinMismatch = "pin-mismatch";

    /// <summary>场景：连上不发 hello，服务端必须按绝对时限切断。</summary>
    public const string ScenarioTimeout = "timeout";

    /// <summary>场景：长度前缀一字节一字节地慢慢喂，绝对时限必须照切不误。</summary>
    public const string ScenarioSlowDribble = "slow-dribble";

    /// <summary>场景：跨子网，服务端必须在同子网闸门上拒掉（本次 lab 跑不出，见 §14.13）。</summary>
    public const string ScenarioCrossSubnet = "cross-subnet";

    private static readonly TimeSpan DiscoverBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SessionHoldDuration = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 全部已知的 pinning 拒绝短码。
    /// </summary>
    /// <remarks>
    /// 与产品常量<b>编译期绑定</b>：产品改了短码拼写，这里跟着改，不会静默失配。
    /// </remarks>
    private static readonly string[] KnownRejectionCodes =
    {
        PeerCertificateValidator.RejectionNoCertificate,
        PeerCertificateValidator.RejectionPinMismatch,
        PeerCertificateValidator.RejectionNotEcdsaP256,
        PeerCertificateValidator.RejectionIsCertificateAuthority,
        PeerCertificateValidator.RejectionMissingKeyUsage,
        PeerCertificateValidator.RejectionKeyUsageWithoutDigitalSignature,
        PeerCertificateValidator.RejectionMissingEnhancedKeyUsage,
        PeerCertificateValidator.RejectionEnhancedKeyUsageWithoutServerAuth,
        PeerCertificateValidator.RejectionNotYetValid,
        PeerCertificateValidator.RejectionExpired,
    };

    /// <summary>逐场景独立上下文；全部资源释放后才返回观测，调用方负责输出及整轮结算。</summary>
    private static async Task<ScenarioOutcome> RunAsync(
        AcceptanceRun run,
        string scenario,
        string? deviceCode,
        string? address,
        string? pinHex,
        int port,
        CancellationToken cancellationToken,
        ReadOnlyMemory<byte> accessKey,
        SessionPermission requestedPermission,
        Action<ControlClientApprovalPending>? approvalPending)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (scenario == ScenarioSuccess && ValidateSuccessConfiguration(
                accessKey.Length, requestedPermission, address, pinHex) is string unmet)
        {
            // 不加载本机密钥，不扩充 argv；无有效输入时也不启动发现或发起连接。
            return TargetResolution.Failed(unmet, AcceptanceOutcome.PreconditionUnmet).Failure!;
        }

        AcceptanceContext? context = null;
        ScenarioOutcome outcome;
        bool cleanupFault = false;
        try
        {
            context = new AcceptanceContext(LogLevel.Warning, run.Log.WriteLine);
            await context.InitializeAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // 每个上下文记录实际加载的身份，整轮 RUN/BUILD/ENV 头仍只写一次。
            run.WriteIdentity(context.Identity, context.Certificate, context.ListenAddresses());
            TargetResolution resolution = await ResolveTargetAsync(
                run, context, scenario, deviceCode, address, pinHex, port, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            outcome = resolution.Target is null
                ? resolution.Failure!
                : await ExecuteAsync(run, context, scenario, resolution.Target, cancellationToken,
                    accessKey, requestedPermission, approvalPending).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 交给 run owner 标记 operator abort；不能当作 TLS 拒绝或对端关闭。
            throw;
        }
        catch (Exception ex)
        {
            ReportFault(run, "Client 场景执行", ex);
            outcome = UnobservedFailure(AcceptanceOutcome.HarnessError, "控制端场景执行异常，连接观测不完整。");
        }
        finally
        {
            if (context is not null)
            {
                try
                {
                    await context.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    cleanupFault = true;
                    ReportFault(run, "Client Discovery/context 释放", ex);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return cleanupFault
            ? outcome with
            {
                Outcome = AcceptanceOutcome.HarnessError,
                Detail = outcome.Detail + " 资源清理失败，不能给 PASS。",
            }
            : outcome;
    }

    /// <summary>依次跑多个场景；资源清理、交叉核对后统一 Complete，GUI/headless 共用。</summary>
    /// <remarks>
    /// approvalPending 仅转发给 UI 的有界内存写入；不在回调中写同步日志或等待 Dispatcher。
    /// pending 不是认证成功；UI 在本方法成功、失败或取消收尾后负责清理等待状态。
    /// accessKey 由调用方保管和清零，客户端不日志、不持久化，也不从命令行取得密钥。
    /// </remarks>
    public static async Task<AcceptanceOutcome> RunAllAsync(
        AcceptanceRun run,
        IReadOnlyList<string> scenarios,
        string? deviceCode,
        string? address,
        string? pinHex,
        int port,
        CancellationToken cancellationToken,
        ReadOnlyMemory<byte> accessKey = default,
        SessionPermission requestedPermission = SessionPermission.Control,
        Action<ControlClientApprovalPending>? approvalPending = null)
    {
        List<ScenarioOutcome> outcomes = new();
        AcceptanceOutcome loopOutcome = AcceptanceOutcome.Pass;
        string? activeScenario = null;
        string detail = "控制端本地观测完成；M4 仍须配对 Host 的 registry 实测证据。";
        try
        {
            run.WriteHeader(AcceptanceProfile.Timeouts);
            foreach (string scenario in scenarios)
            {
                cancellationToken.ThrowIfCancellationRequested();
                activeScenario = scenario;
                run.Log.WriteLine(string.Empty);
                run.Log.WriteLine("---------------- 场景 " + scenario + " ----------------");
                run.Log.WriteLine($"[CLIENT] scenario    = {scenario}");

                ScenarioOutcome outcome = await RunAsync(
                    run, scenario, deviceCode, address, pinHex, port, cancellationToken,
                    accessKey, requestedPermission, approvalPending).ConfigureAwait(false);
                outcome = outcome with { Scenario = scenario };
                outcomes.Add(outcome);
                activeScenario = null;
                WriteOutcome(run, scenario, outcome);
                run.Log.WriteLine($"---------------- 场景 {scenario} 结束：{outcome.Outcome.Code()}（{outcome.Outcome.Describe()}）----------------");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            loopOutcome = AcceptanceOutcome.InvalidRun;
            detail = "控制端运行被操作员取消，未完成的观测不能作为通过证据。";
        }
        catch (Exception ex)
        {
            loopOutcome = AcceptanceOutcome.HarnessError;
            detail = "控制端场景循环或日志回调异常，观测不完整。";
            ReportFault(run, "Client 场景循环", ex);
        }

        try
        {
            if (activeScenario is not null)
            {
                ScenarioOutcome incomplete = UnobservedFailure(loopOutcome, detail) with { Scenario = activeScenario };
                outcomes.Add(incomplete);
                WriteOutcome(run, activeScenario, incomplete);
            }
            WriteCrossCheck(run, outcomes, scenarios);
        }
        catch (Exception ex)
        {
            loopOutcome = AcceptanceOutcome.HarnessError;
            detail += " 交叉核对清单输出失败。";
            ReportFault(run, "Client 证据汇总", ex);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            try
            {
                run.MarkOperatorAbort("控制端场景循环或资源收尾");
            }
            catch (Exception ex)
            {
                ReportFault(run, "Client 中止日志", ex);
            }
        }
        if (scenarios.Count == 0 && loopOutcome == AcceptanceOutcome.Pass)
        {
            loopOutcome = AcceptanceOutcome.PreconditionUnmet;
            detail = "没有请求任何客户端场景。";
        }
        detail += " 场景结果：" + (outcomes.Count == 0 ? "未完成任何场景"
            : string.Join("，", outcomes.Select(item => $"{item.Scenario}={item.Outcome.Code()}"))) + "。";
        AcceptanceOutcome combined = outcomes.Select(item => item.Outcome).Append(loopOutcome).Combine();
        return run.Complete(combined, detail);
    }

    internal static string? ValidateSuccessConfiguration(
        int accessKeyLength, SessionPermission requestedPermission, string? address, string? pinHex)
    {
        if (accessKeyLength != AccessSecret.AccessKeyByteLength)
        {
            return "success 需要 UI 提供有效的 16 字节访问密钥；缺失或长度无效，本次不连接。";
        }
        if (!string.IsNullOrWhiteSpace(address) && !string.IsNullOrWhiteSpace(pinHex))
        {
            return "success 不支持 --address/--pin 直连的占位 DeviceId；必须从发现记录冻结真实 DeviceId，不能关闭身份核对。";
        }
        if (requestedPermission is not (SessionPermission.Control or SessionPermission.ViewOnly))
        {
            return "success 请求权限必须是 Control 或 ViewOnly。";
        }
        return null;
    }

    private static ScenarioOutcome UnobservedFailure(AcceptanceOutcome outcome, string detail) =>
        new(outcome, detail, Fields(("connection", "UNOBSERVED")),
            "(连接阶段观测不完整，不能推断 Host 行数)", ReachedWire: false, ConnectionObservationUnknown: true);

    private static void ReportFault(AcceptanceRun run, string source, Exception exception)
    {
        try
        {
            // UI 回调可能接触密钥；不把任意异常正文、堆栈或 inner exception 传给日志。
            run.ReportBackgroundFault(source,
                new InvalidOperationException($"{exception.GetType().Name}（异常正文未输出，避免秘密进入日志）"));
        }
        catch (Exception)
        {
            // 故障已先记账；日志订阅方抛异常不能阻断其它资源释放及尾部结算。
        }
    }

    // -----------------------------------------------------------------------
    // 目标解析
    // -----------------------------------------------------------------------
    private static async Task<TargetResolution> ResolveTargetAsync(
        AcceptanceRun run,
        AcceptanceContext context,
        string scenario,
        string? deviceCode,
        string? address,
        string? pinHex,
        int port,
        CancellationToken cancellationToken)
    {
        // 直连模式：给了 --address + --pin 就不再走发现。
        //
        // 一开始这条只服务于 cross-subnet（那种场景本来也发现不到对端）。
        // 但它同时解决另一个问题：**验收器自己能不能被自检**。
        // 走发现的话，验四个场景需要一整套 lab（两台机器、私有网段、被控端在跑），
        // 于是新加的判据在真机跑之前<b>一次都没被看见红过</b>——
        // 这是「变异后仍然绿」最容易发生的场合。直连让一个环回上的假被控端
        // 就能把四个场景的判据全部走一遍。
        if (!string.IsNullOrWhiteSpace(address) && !string.IsNullOrWhiteSpace(pinHex))
        {
            if (!IPAddress.TryParse(address, out IPAddress? parsed) || parsed is null)
            {
                return TargetResolution.Failed(
                    $"--address「{address}」不是合法 IPv4 地址。", AcceptanceOutcome.PreconditionUnmet);
            }

            string effectivePin = pinHex;

            // pin-mismatch 在直连模式下**必须自己把指纹改成近失值**。
            //
            // 漏了这一步的后果实测过（2026-09-21，`--headless client --all`）：
            // 四个场景共用同一个 --pin，于是 pin-mismatch 拿真指纹去连，握手当然成功，
            // 场景必然 FAIL——失败信息还写着「本该在 TLS 阶段被拒绝，实际握手成功了」，
            // 看上去像产品出了问题，其实是验收器自己没做它该做的事。
            // 走发现的那条路径一直有这一步（RebuildWithWrongPin），直连是新加的路，
            // 漏改属于「同一件事有两个实现，只改了一个」。
            if (scenario == ScenarioPinMismatch)
            {
                effectivePin = FlipOneBit(pinHex);
                run.Log.WriteLine($"[CLIENT] expectedPin = {pinHex.ToUpperInvariant()}");
                run.Log.WriteLine($"[CLIENT] wrongPin    = {effectivePin.ToUpperInvariant()}");
                run.Log.WriteLine("[CLIENT] 改动幅度    = byte[16] ^ 0x01" +
                                  "——256 位里只差 1 位，用于证明判定是逐字节比较而不是别的近似条件");
            }

            if (!ConnectionTarget.TryCreate(
                    UnverifiedPeerId, parsed, port, effectivePin, out ConnectionTarget? direct) || direct is null)
            {
                return TargetResolution.Failed(
                    "--pin 不是 64 位十六进制指纹，无法构造直连快照。",
                    AcceptanceOutcome.PreconditionUnmet);
            }

            run.Log.WriteLine($"[CLIENT] 直连目标   = {parsed}:{port}（跳过发现，deviceId 未经验证）");
            run.Log.WriteLine($"[CLIENT][CORRELATE] peerHost={parsed}:{port} expectedPin={effectivePin.ToUpperInvariant()}");
            return new TargetResolution(direct, null);
        }

        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            return TargetResolution.Failed(
                "既没给对端设备码，也没给 --address/--pin，不知道要连谁。",
                AcceptanceOutcome.PreconditionUnmet);
        }

        await context.StartDiscoveryAsync(cancellationToken);

        DiscoveredDevice? peer = await WaitForPeerAsync(context, deviceCode, cancellationToken);
        if (peer is null)
        {
            return TargetResolution.Failed(
                $"发现预算内没看到设备码为 {deviceCode} 的对端——对端没在跑、不在同一子网、或被防火墙拦了。",
                AcceptanceOutcome.PreconditionUnmet);
        }

        run.Log.WriteLine($"[CLIENT] peer        = {peer.DeviceCode} {peer.Address}:{peer.Port}");
        run.Log.WriteLine($"[CLIENT][CORRELATE] peerDeviceId={peer.DeviceId} peerHost={peer.Address}:{peer.Port}");

        if (!ConnectionTarget.TryCreate(peer, out ConnectionTarget? frozen) || frozen is null)
        {
            return TargetResolution.Failed("对端的发现记录无法冻结成连接快照。",
                AcceptanceOutcome.PreconditionUnmet);
        }

        return new TargetResolution(
            scenario == ScenarioPinMismatch ? RebuildWithWrongPin(run, peer) : frozen,
            null);
    }

    // -----------------------------------------------------------------------
    // 执行
    // -----------------------------------------------------------------------
    private static async Task<ScenarioOutcome> ExecuteAsync(
        AcceptanceRun run,
        AcceptanceContext context,
        string scenario,
        ConnectionTarget target,
        CancellationToken cancellationToken,
        ReadOnlyMemory<byte> accessKey,
        SessionPermission requestedPermission,
        Action<ControlClientApprovalPending>? approvalPending)
    {
        // 高层自己建立且独占 TLS；必须先分流，不能先建低层连接再做第二次认证连接。
        if (scenario == ScenarioSuccess)
        {
            return await RunSuccessAsync(run, context, target, accessKey, requestedPermission,
                approvalPending, cancellationToken).ConfigureAwait(false);
        }

        TlsClientConnector connector = new();

        TlsConnection connection;
        try
        {
            connection = await connector.ConnectAsync(
                target, AcceptanceProfile.Timeouts, null, cancellationToken);
        }
        catch (Exception ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ClassifyHandshakeFailure(run, scenario, ex);
        }

        using (connection)
        {
            string presented = Convert.ToHexString(connection.Identity.PresentedCertSha256.Span);
            string expected = Convert.ToHexString(connection.Identity.ExpectedCertSha256.Span);

            run.Log.WriteLine($"[CLIENT] tls         = ok proto={connection.NegotiatedProtocol}");
            run.Log.WriteLine($"[CLIENT] expectedPin = {expected}");
            run.Log.WriteLine($"[CLIENT] presentedPin= {presented}");

            // 注意：握手成功 ⟹ 两个指纹必然一致——PeerCertificateValidator 的返回定义就是
            // 「pin 字节相等」。所以这一位<b>不是</b>独立断言，只作记录，
            // 不要把它当成「验证了 pinning」的证据（pinning 真正的证据是握手成功这件事本身）。
            run.Log.WriteLine($"[CLIENT] pinsMatch   = {connection.Identity.PinsMatch} (由握手成功蕴含)");

            // 4 元组：本端临时端口 + 对端 IP:端口。被控端会打 peer=<本端IP>:<同一个临时端口>。
            // 只有这两个数能唯一配对一条连接；presentedPin 两条场景下可能相同，靠它配不出来。
            run.Log.WriteLine($"[CLIENT][CORRELATE] local={connection.LocalEndPoint?.ToString() ?? "unavailable"} " +
                              $"peerHost={target.RemoteAddress}:{target.Port} presentedPin={presented}");

            Stopwatch clock = Stopwatch.StartNew();

            return scenario switch
            {
                ScenarioTimeout => await RunTimeoutAsync(run, connection, clock, cancellationToken),
                ScenarioSlowDribble => await RunSlowDribbleAsync(run, connection, clock, cancellationToken),
                _ => new ScenarioOutcome(
                    AcceptanceOutcome.Fail,
                    $"场景 {scenario} 本该在 TLS 阶段就被拒绝，实际握手成功了。",
                    Fields(("handshake", "succeeded")),
                    // 关键：pinning 一旦没拦住，被控端**一定会**留一行（控制端随后不发 hello 就关闭，
                    // 被控端读到 EOF 给出 rejection=pre-auth-eof）。所以这里没有可用的否定式期望——
                    // 这条失败必须由控制端自己定案，不能靠「被控端没看到行」来反证。
                    "(pinning 未生效：被控端会出现一行 TLS 后记录——本条失败由控制端定案，勿靠被控端无行反证)",
                    ReachedWire: true),
            };
        }
    }

    /// <summary><c>success</c>：只接受高层已验证会话，不读取内部 Stream/Token。</summary>
    private static async Task<ScenarioOutcome> RunSuccessAsync(
        AcceptanceRun run,
        AcceptanceContext context,
        ConnectionTarget target,
        ReadOnlyMemory<byte> accessKey,
        SessionPermission requestedPermission,
        Action<ControlClientApprovalPending>? approvalPending,
        CancellationToken cancellationToken)
    {
        if (target.DeviceId == UnverifiedPeerId)
        {
            return TargetResolution.Failed("success 的目标仍为占位 DeviceId，无法验证远端身份。",
                AcceptanceOutcome.PreconditionUnmet).Failure!;
        }

        ControlClientAuthOptions options = new()
        {
            // 认证窗口内只执行 UI 提供的有界内存更新，不包装同步日志或 Dispatcher 等待。
            ApprovalPending = approvalPending,
        };
        run.Log.WriteLine($"[CLIENT][AUTH] clientDeviceId={context.Identity.DeviceId} " +
            $"requestedPermission={requestedPermission} machineWindowMs={options.MachineWindow.TotalMilliseconds:0} " +
            $"approvalWindowMs={options.ApprovalWindow.TotalMilliseconds:0}");
        AuthenticatedControlSession session;
        try
        {
            session = await new ControlClientConnector().ConnectAndAuthenticateAsync(
                target, context.Identity.DeviceId, context.Identity.DeviceName, accessKey, requestedPermission,
                options, AcceptanceProfile.Timeouts, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ScenarioOutcome failure = ClassifyAuthenticationFailure(ex, cancellationToken);
            if (failure.Outcome == AcceptanceOutcome.HarnessError)
            {
                ReportFault(run, "Client 高层连接", ex);
            }
            return failure;
        }

        ScenarioOutcome outcome;
        using (session)
        {
            // 返回会话已经蕴含 serverProof、冻结身份及降权校验通过；pending 绝不能走到这里。
            cancellationToken.ThrowIfCancellationRequested();
            string expected = Convert.ToHexString(session.Identity.ExpectedCertSha256.Span);
            string presented = Convert.ToHexString(session.Identity.PresentedCertSha256.Span);
            run.Log.WriteLine($"[CLIENT][AUTH] serverProof=verified sessionId={session.SessionId} " +
                $"grant={session.GrantedPermission}");
            run.Log.WriteLine($"[CLIENT] expectedPin = {expected}");
            run.Log.WriteLine($"[CLIENT] presentedPin= {presented}");
            run.Log.WriteLine($"[CLIENT] pinsMatch   = {session.Identity.PinsMatch} (由高层认证成功蕴含)");
            // 高层身份不暴露本地端口，不能为凑四元组而伪造端点或再次建连。
            run.Log.WriteLine($"[CLIENT][CORRELATE] sessionId={session.SessionId} peerDeviceId={target.DeviceId} " +
                $"peerHost={target.RemoteAddress}:{target.Port} presentedPin={presented} grant={session.GrantedPermission}");
            run.Log.WriteLine("[CLIENT][HOLD] localObjectHoldTargetMs=5000 hostRegistry=UNOBSERVED");
            Stopwatch hold = Stopwatch.StartNew();
            TimeSpan remaining = SessionHoldDuration;
            do
            {
                await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
                remaining = SessionHoldDuration - hold.Elapsed;
            }
            while (remaining > TimeSpan.Zero);
            cancellationToken.ThrowIfCancellationRequested();
            long heldMs = hold.ElapsedMilliseconds;
            run.Log.WriteLine($"[CLIENT][HOLD] sessionId={session.SessionId} localObjectHeldMs={heldMs} " +
                "// 仅证明本地对象保有，不证明 Host registry 持续在线");
            outcome = new ScenarioOutcome(
                AcceptanceOutcome.Pass,
                "serverProof 已通过；本地持有已验证会话至少五秒后同步释放。Host 保持及注销仍须按 SessionId 实测核对。",
                Fields(("serverProof", "verified"), ("sessionId", session.SessionId.ToString()),
                    ("expectedPin", expected), ("presentedPin", presented),
                    ("grant", session.GrantedPermission.ToString()), ("localObjectHeldMs", heldMs.ToString()),
                    ("sessionDisposed", "True"), ("hostRegistry", "UNOBSERVED")),
                HostExpectation: $"sessionId={session.SessionId} authenticated=True evidence=PASS " +
                    "deregisteredAtRunEnd=True hostForcedClose=False terminal=authenticated-ended-unregistered",
                ReachedWire: true);
        }
        // using 的同步 Dispose 成功以后才返回 PASS；异常由外层记账，不能被观测结论盖住。
        run.Log.WriteLine($"[CLIENT][DISPOSE] sessionId={session.SessionId} synchronous=True completed=True");
        return outcome;
    }

    /// <summary>高层认证失败先于 TLS 异常分类；只输出产品固定展示文案和非秘密拒绝短码。</summary>
    internal static ScenarioOutcome ClassifyAuthenticationFailure(Exception exception, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 该异常继承 AuthenticationException，顺序颠倒会把错误密码误判成 TLS 失败。
        if (exception is ControlClientAuthenticationException auth)
        {
            return new ScenarioOutcome(AcceptanceOutcome.Fail, auth.DisplayMessage,
                Fields(("stage", "authentication"), ("rejection", auth.Rejection)),
                "(认证未通过；Host 的拒绝/会话证据需交叉核对，客户端未获得已验证会话)", ReachedWire: true);
        }
        if (exception is AuthenticationException)
        {
            string code = FindRejectionCode(exception.Message) ?? "(none)";
            return new ScenarioOutcome(AcceptanceOutcome.Fail, "success 在 TLS 身份验证阶段失败，未进入已验证会话。",
                Fields(("stage", "tls"), ("rejection", code)),
                "(TLS 失败；Host 可能有 pre-auth-eof 行，但不得出现已认证会话)",
                ReachedWire: true, TlsStageRejection: true);
        }
        if (HasSocketError(exception, SocketError.ConnectionRefused, SocketError.HostUnreachable,
                SocketError.NetworkUnreachable, SocketError.NetworkDown, SocketError.TimedOut,
                SocketError.AddressNotAvailable))
        {
            return new ScenarioOutcome(AcceptanceOutcome.PreconditionUnmet,
                "TCP 层未连接，无法进行双向认证。", Fields(("stage", "tcp")),
                "(对端不会看到这条连接)", ReachedWire: false);
        }
        if (exception is IOException or SocketException or TimeoutException or OperationCanceledException)
        {
            // TLS 连接超时也可能是非 caller 的 OCE；无确定阶段证据，不伪造已建连数量。
            return UnobservedFailure(AcceptanceOutcome.Fail, "连接中断或超过本地预算，未获得已验证会话。");
        }
        return UnobservedFailure(AcceptanceOutcome.HarnessError, "高层连接调用异常，未获得已验证会话。");
    }

    /// <summary><c>timeout</c>：一个字节都不发，期望服务端在绝对时限附近切断。</summary>
    private static async Task<ScenarioOutcome> RunTimeoutAsync(
        AcceptanceRun run,
        TlsConnection connection,
        Stopwatch clock,
        CancellationToken cancellationToken)
    {
        run.Log.WriteLine("[CLIENT] 故意不发 hello，等服务端按 pre-auth 绝对时限切断……");

        CloseObservation close = await WaitForPeerCloseAsync(connection, cancellationToken);
        long closedAt = clock.ElapsedMilliseconds;

        run.Log.WriteLine($"[CLIENT] peerClosed  = {close.Kind} t={closedAt} ms // {close.Detail}");

        bool withinWindow =
            closedAt >= (long)AcceptanceProfile.TimeoutCloseMin.TotalMilliseconds &&
            closedAt <= (long)AcceptanceProfile.TimeoutCloseMax.TotalMilliseconds;

        bool pass = close.Closed && withinWindow;

        List<string> problems = new();
        if (!close.Closed)
        {
            problems.Add($"读预算内对端一直没关（{close.Kind}）——绝对时限没生效？");
        }

        if (!withinWindow)
        {
            problems.Add($"对端在 {closedAt} ms 收尾，落在期望窗口 " +
                         $"[{AcceptanceProfile.TimeoutCloseMin.TotalMilliseconds:0}, " +
                         $"{AcceptanceProfile.TimeoutCloseMax.TotalMilliseconds:0}] ms 之外");
        }

        string detail = problems.Count == 0
            ? $"对端在 {closedAt} ms 切断（{close.Kind}），符合长度前缀绝对时限 " +
              $"{AcceptanceProfile.Timeouts.LengthPrefixTimeout.TotalMilliseconds:0} ms"
            : string.Join("；", problems);

        return new ScenarioOutcome(
            pass ? AcceptanceOutcome.Pass : AcceptanceOutcome.Fail,
            detail,
            Fields(
                ("peerClosed", close.Kind),
                ("closedAtMs", closedAt.ToString()),
                ("windowMinMs", AcceptanceProfile.TimeoutCloseMin.TotalMilliseconds.ToString("0")),
                ("windowMaxMs", AcceptanceProfile.TimeoutCloseMax.TotalMilliseconds.ToString("0"))),
            HostExpectation: "rejection=pre-auth-timeout",
            ReachedWire: true);
    }

    /// <summary>
    /// <c>slow-dribble</c>：把长度前缀<b>一字节一字节</b>地慢慢喂过去。
    /// </summary>
    /// <remarks>
    /// <para>这是「时限是<b>绝对</b>的」唯一的正面证据。原先只测「不发字节 → 被切」，
    /// 那条判据对一个<b>每收到一个字节就重置</b>的空闲超时同样成立——两者行为一致，
    /// 测了等于没测。</para>
    /// <para>绝对时限：3 个字节喂到第 5 秒就被切，收尾时刻贴着时限。
    /// 可重置的空闲时限：4 个字节在第 6 秒喂完，再等 10 秒 payload 时限才收尾
    /// （约 16 秒）——超出上界，红。</para>
    /// </remarks>
    private static async Task<ScenarioOutcome> RunSlowDribbleAsync(
        AcceptanceRun run,
        TlsConnection connection,
        Stopwatch clock,
        CancellationToken cancellationToken)
    {
        byte[] hello = HelloFrame.Serialize();
        byte[] prefix = new byte[TransportConstants.LengthPrefixBytes];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)hello.Length);

        run.Log.WriteLine($"[CLIENT] 慢滴长度前缀 {Convert.ToHexString(prefix)}" +
                          $"（hello 共 {hello.Length} 字节），每 {AcceptanceProfile.DribbleInterval.TotalSeconds:0} 秒发 1 字节");

        // 读和服务端并发跑：写字节的间隙里也要能立刻发现对端已经关了。
        Task<CloseObservation> closeTask = WaitForPeerCloseAsync(connection, cancellationToken);

        int sent = 0;
        long closedAt = -1;

        for (int index = 0; index < prefix.Length; index++)
        {
            if (closeTask.IsCompleted)
            {
                closedAt = clock.ElapsedMilliseconds;
                break;
            }

            try
            {
                await connection.Stream.WriteAsync(prefix.AsMemory(index, 1), cancellationToken);
                await connection.Stream.FlushAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 本机取消：不算对端收尾，交给结算逻辑判成 INVALID_RUN。
                break;
            }
            catch (Exception ex)
            {
                run.Log.WriteLine($"[CLIENT] 第 {index + 1} 字节写出失败：" +
                                  $"{ex.GetType().Name}: {ex.Message}");
                closedAt = clock.ElapsedMilliseconds;
                break;
            }

            sent++;
            run.Log.WriteLine($"[CLIENT] 已发 {sent}/{prefix.Length} 字节 t={clock.ElapsedMilliseconds} ms");

            if (index < prefix.Length - 1)
            {
                // WhenAny 不抛：延迟到点或对端先关，谁先到都行。
                await Task.WhenAny(
                    Task.Delay(AcceptanceProfile.DribbleInterval, cancellationToken),
                    closeTask);
            }
        }

        CloseObservation close = await closeTask;

        if (closedAt < 0)
        {
            closedAt = clock.ElapsedMilliseconds;
        }

        run.Log.WriteLine($"[CLIENT] peerClosed  = {close.Kind} sent={sent}/{prefix.Length} t={closedAt} ms" +
                          $" // {close.Detail}");

        bool prefixIncomplete = sent < prefix.Length;
        bool inWindow =
            closedAt >= (long)AcceptanceProfile.TimeoutCloseMin.TotalMilliseconds &&
            closedAt <= (long)AcceptanceProfile.DribbleCloseMax.TotalMilliseconds;

        bool pass = close.Closed && prefixIncomplete && inWindow;

        List<string> problems = new();
        if (!close.Closed)
        {
            problems.Add($"读预算内对端一直没关（{close.Kind}）——绝对时限没生效？");
        }

        if (!prefixIncomplete)
        {
            problems.Add($"{prefix.Length} 个前缀字节全部发完（sent={sent}，t={closedAt} ms）" +
                         "对端才收尾——时限被每字节的到达重置了，不是绝对时限");
        }

        if (!inWindow)
        {
            problems.Add($"对端在 {closedAt} ms 收尾，落在期望窗口 " +
                         $"[{AcceptanceProfile.TimeoutCloseMin.TotalMilliseconds:0}, " +
                         $"{AcceptanceProfile.DribbleCloseMax.TotalMilliseconds:0}] ms 之外");
        }

        string detail = problems.Count == 0
            ? $"只发了 {sent}/{prefix.Length} 字节、在 {closedAt} ms 被切" +
              "——长度前缀阶段用的是绝对时限，没有被逐字节重置"
            : string.Join("；", problems);

        return new ScenarioOutcome(
            pass ? AcceptanceOutcome.Pass : AcceptanceOutcome.Fail,
            detail,
            Fields(
                ("peerClosed", close.Kind),
                ("bytesSent", $"{sent}/{prefix.Length}"),
                ("closedAtMs", closedAt.ToString()),
                ("windowMinMs", AcceptanceProfile.TimeoutCloseMin.TotalMilliseconds.ToString("0")),
                ("windowMaxMs", AcceptanceProfile.DribbleCloseMax.TotalMilliseconds.ToString("0"))),
            HostExpectation: "rejection=pre-auth-timeout",
            ReachedWire: true);
    }

    // -----------------------------------------------------------------------
    // 握手失败的归类
    // -----------------------------------------------------------------------
    /// <summary>
    /// 把握手失败归到「环境没配好 / 符合预期 / 不符合预期」之一。
    /// </summary>
    /// <remarks>
    /// <b>绝不允许把「没测到」报成 PASS</b>。TCP 层没连上（对端没跑 host、地址写错、
    /// 防火墙拦掉）时同子网闸门与 pinning 一行都没跑到，那是前置条件不满足。
    /// </remarks>
    private static ScenarioOutcome ClassifyHandshakeFailure(
        AcceptanceRun run,
        string scenario,
        Exception exception)
    {
        string type = exception.GetType().FullName ?? exception.GetType().Name;
        run.Log.WriteLine($"[CLIENT] handshake failed: {type}: {exception.Message}");

        if (HasSocketError(
                exception,
                SocketError.ConnectionRefused,
                SocketError.HostUnreachable,
                SocketError.NetworkUnreachable,
                SocketError.NetworkDown,
                SocketError.TimedOut,
                SocketError.AddressNotAvailable))
        {
            return new ScenarioOutcome(
                AcceptanceOutcome.PreconditionUnmet,
                $"TCP 层就没连上（{type}）——对端 host 没跑、地址不对或被防火墙拦了，" +
                "同子网闸门与 pinning 都未被触及。",
                Fields(("handshake", type), ("stage", "tcp")),
                HostExpectation: "(对端不会看到这条连接)",
                ReachedWire: false);
        }

        string? code = FindRejectionCode(exception.Message);

        if (scenario == ScenarioPinMismatch)
        {
            // 只有「认证失败 + 原因短码恰是 pin-mismatch」才算通过。
            bool isAuthFailure = exception is AuthenticationException;
            bool pinRejected = code == PeerCertificateValidator.RejectionPinMismatch;
            bool pass = isAuthFailure && pinRejected;

            string detail = pass
                ? "握手被 pinning 按预期拒绝，原因短码 pin-mismatch"
                : $"被拒绝的原因不是 pin-mismatch（异常={type}，短码={code ?? "未识别"}）" +
                  "——真正被测的那行 pinning 判定很可能没跑到，不能算通过";

            return new ScenarioOutcome(
                pass ? AcceptanceOutcome.Pass : AcceptanceOutcome.Fail,
                detail,
                Fields(("handshake", type), ("rejection", code ?? "(none)")),
                // 这里**不能**写「无行」。曾经写的就是「无行：TLS 阶段即被拒，TransportHost 静默」，
                // 与下面 ④ 的区间（3..4）自相矛盾——按这行去核对的人会去找「零行」，找不到就判失败。
                // 实测事实：TLS 1.3 下服务端在收到客户端的 alert 之前就认为握手完成，
                // 于是照样进会话处理器、读到 EOF 给出 rejection=pre-auth-eof（本机环回实测）；
                // TLS 1.2 下服务端握手直接失败、才真的不留行。两种都正常，所以只给可证伪的约束。
                HostExpectation: "(TLS 阶段被拒；被控端可能留一行 rejection=pre-auth-eof（TLS 1.3），" +
                                 "也可能完全无行（TLS 1.2）——但绝不能是 returnedState=Authenticated)",
                ReachedWire: true,
                TlsStageRejection: true);
        }

        if (scenario == ScenarioCrossSubnet)
        {
            // 同子网闸门在 accept 之后才关连接，所以对端看到的是 EOF / RST 而不是拒连。
            // 「根本没连上」已经被上面的 SocketError 分支排除掉了。
            return new ScenarioOutcome(
                AcceptanceOutcome.Pass,
                $"连接在 TLS 之前被关掉（{type}，短码={code ?? "未识别"}）——" +
                "与同子网闸门（accept → 子网校验 → 准入 → TLS）的预期一致",
                Fields(("handshake", type), ("stage", "pre-tls")),
                HostExpectation: "(无行：被同子网闸门或准入限额拒掉，TransportHost 静默)",
                ReachedWire: true,
                TlsStageRejection: true);
        }

        return new ScenarioOutcome(
            AcceptanceOutcome.Fail,
            $"本该握手成功，却在 TLS 阶段失败（异常={type}，短码={code ?? "未识别"}）",
            Fields(("handshake", type), ("rejection", code ?? "(none)")),
            HostExpectation: "(TLS 未完成；按实际拒绝阶段核对，不能计为已认证会话)",
            ReachedWire: true,
            // 这里也必须标上。曾经漏标，于是交叉核对把「TLS 就没成功」的场景算成
            // 「必然进入会话处理器」，给出的下界比真相大——一个会让人去找不存在的行的错数。
            TlsStageRejection: true);
    }

    /// <summary>在异常链的消息里找 pinning 拒绝短码。</summary>
    /// <remarks>
    /// 短码来自 <see cref="PeerCertificateValidator"/> 的公开常量，且
    /// <c>TlsClientConnector</c> 会把它裹进 <c>AuthenticationException</c> 的消息里。
    /// 任何一个短码都不是另一个的子串，所以「命中即唯一」。
    /// </remarks>
    private static string? FindRejectionCode(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        foreach (string code in KnownRejectionCodes)
        {
            if (message.Contains(code, StringComparison.Ordinal))
            {
                return code;
            }
        }

        return null;
    }

    private static bool HasSocketError(Exception exception, params SocketError[] errors)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket && Array.IndexOf(errors, socket.SocketErrorCode) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    // -----------------------------------------------------------------------
    // 对端收尾观测
    // -----------------------------------------------------------------------
    private const string EofKind = "eof";

    /// <summary>
    /// 把「对端怎么收的尾」如实分开记，而不是压成一个布尔。
    /// </summary>
    /// <remarks>
    /// <para><b>本机实测（.NET 10.0.12，HANDOFF §15）：<c>ReadAsync</c> 返回 0 只说明
    /// 「TLS 记录层读到了有序结束」，<c>close_notify</c> 与裸 TCP FIN 的观测结果<b>相同</b>。
    /// 所以这里只能说「有序 EOF」，<b>不能</b>说「对端发了 close_notify」。</para>
    /// <para><b>取消必须先于通用异常捕获</b>：原先 <c>catch (Exception)</c> 会把
    /// <c>OperationCanceledException</c> 也归成 <c>reset</c>，而 <c>reset</c> 在
    /// 低层超时专项里会算「对端关闭了」——于是操作员一点中止，
    /// 场景就 PASS。这正是评审第 13 条担心的事，必须用独立的 <c>local-cancelled</c> 掐掉。</para>
    /// </remarks>
    private static async Task<CloseObservation> WaitForPeerCloseAsync(
        TlsConnection connection,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16];

        try
        {
            int read = await connection.Stream
                .ReadAsync(buffer.AsMemory(), cancellationToken)
                .AsTask()
                .WaitAsync(AcceptanceProfile.ReadBudget, cancellationToken);

            return read == 0
                ? new CloseObservation(true, EofKind, "读到有序结束（TLS 记录层 EOF）")
                : new CloseObservation(
                    false,
                    "unexpected-data",
                    $"未发完整 hello 就收到对端 {read} 字节——低层超时专项不应收到后续数据");
        }
        catch (TimeoutException)
        {
            return new CloseObservation(false, "still-open", "读预算内对端没有关闭连接");
        }
        catch (OperationCanceledException ex)
        {
            // 本机取消 ≠ 对端关闭。绝不能算作「对端收尾了」。
            return new CloseObservation(false, "local-cancelled",
                $"{ex.GetType().Name}（本机取消，不算对端收尾）");
        }
        catch (Exception ex)
        {
            string type = ex.GetType().FullName ?? ex.GetType().Name;
            return new CloseObservation(true, "reset", $"{type}: {ex.Message}");
        }
    }

    private readonly record struct CloseObservation(bool Closed, string Kind, string Detail);

    // -----------------------------------------------------------------------
    // 输出
    // -----------------------------------------------------------------------
    private static void WriteOutcome(AcceptanceRun run, string scenario, ScenarioOutcome outcome)
    {
        string fields = string.Join(" ", outcome.Fields.Select(pair => $"{pair.Key}={pair.Value}"));

        run.Log.WriteLine(
            $"[CLIENT][RESULT] scenario={scenario} clientOutcome={outcome.Outcome.Code()} {fields} " +
            $"hostEvidence={(outcome.ReachedWire || outcome.ConnectionObservationUnknown ? "REQUIRED" : "N/A")} " +
            $"hostExpect=\"{outcome.HostExpectation}\" // {outcome.Detail}");
    }

    /// <summary>
    /// 两机交叉核对清单。
    /// </summary>
    /// <remarks>
    /// <para><b>控制端不给里程碑结论</b>。它把自己观测到的东西换算成「被控端日志应当长什么样」，
    /// 交给人工/脚本去对；对不上就说明其中一侧的证据是假的。</para>
    /// <para><b>这里原本写错了一次，而且是被实测抓住的</b>（2026-09-21 环回变异测试）：
    /// 原以为 <c>pin-mismatch</c> 那条连接死在 TLS 阶段、服务端不会留行，于是断言
    /// 「被控端应为 connectionsEnteringSessionHandler=3（4 减 1）」。实测**是 4**——
    /// 客户端拒绝证书发的是 TLS alert，而 <b>TLS 1.3 下服务端此刻已经认为握手完成</b>，
    /// 于是它照样进了会话处理器，读到 EOF 后给出 <c>rejection=pre-auth-eof</c>。
    /// 这正是「从症状推断因果」的典型错误，所以现在只写<b>可证伪的约束</b>，
    /// 不再写一个我猜出来的数字。</para>
    /// </remarks>
    private static void WriteCrossCheck(
        AcceptanceRun run,
        IReadOnlyList<ScenarioOutcome> outcomes,
        IReadOnlyList<string> scenarios)
    {
        int issued = outcomes.Count(item => item.ReachedWire);
        int unknown = outcomes.Count(item => item.ConnectionObservationUnknown);
        int notIssued = outcomes.Count(item => !item.ReachedWire && !item.ConnectionObservationUnknown);
        int expectAuthenticated = outcomes.Count(
            item => item.Outcome == AcceptanceOutcome.Pass && item.Scenario == ScenarioSuccess);
        int expectPreAuthTimeout = outcomes.Count(
            item => item.Outcome == AcceptanceOutcome.Pass &&
                    item.Scenario is ScenarioTimeout or ScenarioSlowDribble);

        // 区间只依赖连接阶段，不依赖 PASS；TLS 1.3 下证书拒绝仍可能进入 Host handler。
        int lower = outcomes.Count(item => item.ReachedWire && !item.TlsStageRejection);
        int upper = lower + outcomes.Count(item => item.ReachedWire && item.TlsStageRejection &&
            item.Scenario != ScenarioCrossSubnet);
        List<string> lines = new()
        {
            $"场景 {scenarios.Count} 个：确定上过线 {issued} 个（不等于 TLS 成功），" +
            $"确定未建连 {notIssued} 个，连接阶段未知 {unknown} 个，未执行 {scenarios.Count - outcomes.Count} 个。",
            string.Empty,
            "被控端逐条核对（认证、registry 采样及终态不是同一条日志）：",
        };
        for (int index = 0; index < scenarios.Count; index++)
        {
            lines.Add(index < outcomes.Count
                ? $"  {scenarios[index],-14} -> {outcomes[index].HostExpectation}"
                : $"  {scenarios[index],-14} -> （未执行）");
        }

        string preAuthTimeoutWhy = string.Join(" + ",
            outcomes.Where(item => item.Outcome == AcceptanceOutcome.Pass &&
                                   item.Scenario is ScenarioTimeout or ScenarioSlowDribble)
                    .Select(item => item.Scenario));
        lines.Add(string.Empty);
        lines.Add("通过场景的 Host 证据要求（仅对本轮配对连接核对，不把客户端期望当成 Host 实测）：");
        lines.Add($"  ① {expectAuthenticated} 个 success 必须各按 SessionId 找到 [HOST][SESSION] authenticated=True，");
        lines.Add("     evidence=PASS、deregisteredAtRunEnd=True、hostForcedClose=False；");
        lines.Add("     [HOST][RESULT] terminal=authenticated-ended-unregistered，不能只看 pre-auth 成功。");
        lines.Add($"  ② rejection=pre-auth-timeout 应有 {expectPreAuthTimeout} 条" +
            (preAuthTimeoutWhy.Length > 0 ? $"（{preAuthTimeoutWhy}）" : string.Empty));
        lines.Add("  ③ 其余通过的低层拒绝专项不得出现已认证会话；失败场景按其实际阶段逐条核对。");
        lines.Add(unknown == 0
            ? $"  ④ [HOST][BUCKETS] sessionHandled 应落在 {lower}..{upper}（仅限本轮连接、无其它流量）。"
            : "  ④ 存在连接阶段未知的场景，不能给出整轮 sessionHandled 确定区间。");
        lines.Add("  ⑤ Host partitionOk=True、handlerFaults=0、cleanupFault=False、firstStopAllFinishedWithinBudget=True，");
        lines.Add("     activeHandlers=0、activeRegistry=0、pendingApprovals=0；最终为 0 不替代保持期间的正采样。");
        if (outcomes.Any(item => item.Outcome != AcceptanceOutcome.Pass) || outcomes.Count != scenarios.Count)
        {
            lines.Add("本轮有未通过或未执行场景：①②③ 只约束已通过场景；④ 按已知连接阶段计数，不能反推未知场景。");
        }

        int crossSubnetOnWire = outcomes.Count(item => item.ReachedWire && item.Scenario == ScenarioCrossSubnet);
        if (crossSubnetOnWire > 0)
        {
            lines.Add($"另有 {crossSubnetOnWire} 个跨子网场景预期不进入 Host handler（同子网闸门拒绝），仍须两端核对。");
        }
        lines.Add(string.Empty);
        lines.Add("配对方法（不能只靠计数相等）：");
        lines.Add("  success 使用 [CLIENT][CORRELATE] sessionId 配对 Host 的 AUTH-BEGIN/SESSION/RESULT，");
        lines.Add("  再用 Host connectionId 对齐 registry 实测；高层 session 不提供本地端口，不伪造四元组。");
        lines.Add("  五秒持有只证明客户端对象保有，Host 必须独立满足其保持跨度、采样间隔及非强制注销判据。");
        lines.Add("  低层专项仍用 local=<本机IP>:<临时端口> + peerHost=<对端IP>:<端口>，");
        lines.Add("  与 Host peer=<同一本机端点> 配对；local=unavailable 才退回时间顺序，并注明不确定性。");
        lines.Add(string.Empty);
        lines.Add("TLS 1.3 证书拒绝可能在 Host 留 rejection=pre-auth-eof；TLS 1.2 可能无行，均不得是已认证会话。");
        lines.Add("Host 同子网拒绝 / 准入拒绝 / TLS 失败不进入 handler，属于 UNOBSERVABLE，不能填 0。");
        run.WriteBlock("两机交叉核对（把这台和被控端的日志放一起看）", lines);
        run.Log.WriteLine("[VERDICT] M4 = PENDING-HOST-EVIDENCE // 客户端本地认证与对象保有不足以单端宣布里程碑完成");
    }

    private static IReadOnlyList<KeyValuePair<string, string>> Fields(params (string Key, string Value)[] pairs) =>
        pairs.Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value)).ToArray();

    // -----------------------------------------------------------------------
    // 辅助
    // -----------------------------------------------------------------------
    /// <summary>
    /// 把期望指纹改成「只差一位」的近失值。
    /// </summary>
    /// <remarks>
    /// <para>原先用的是 <c>000…001</c> 这种全零串。那不是一个有意义的近失样本：
    /// 它和真实指纹没有任何共同点，等价于「随便填了一个合法格式的串」，
    /// 无法说明判定是<b>逐字节比较</b>而不是别的什么（比如长度检查、字符集检查）。</para>
    /// <para>现在翻转真实指纹第 16 字节的最低位：256 位里只差 1 位，
    /// 若实现是「比较前 N 位」「忽略大小写」之类，这个样本照样能过——于是能被抓到。</para>
    /// </remarks>
    private static ConnectionTarget RebuildWithWrongPin(AcceptanceRun run, DiscoveredDevice peer)
    {
        string wrong = FlipOneBit(peer.CertificateSha256);

        run.Log.WriteLine($"[CLIENT] expectedPin = {Convert.ToHexString(Convert.FromHexString(peer.CertificateSha256))}");
        run.Log.WriteLine($"[CLIENT] wrongPin    = {wrong}");
        run.Log.WriteLine($"[CLIENT] 改动幅度    = byte[{FlippedByteIndex}] ^ 0x{FlippedByteMask:X2}" +
                          "——256 位里只差 1 位，用于证明判定是逐字节比较而不是别的近似条件");

        bool created = ConnectionTarget.TryCreate(
            peer.DeviceId, peer.Address, peer.Port, wrong, out ConnectionTarget? target);

        if (!created || target is null)
        {
            throw new InvalidOperationException("构造错误指纹的快照失败——这是测试器自身的问题。");
        }

        return target;
    }

    private const int FlippedByteIndex = 16;
    private const byte FlippedByteMask = 0x01;

    /// <summary>
    /// 把 256 位指纹翻转 1 位，得到一个「最小差异」的错误指纹。
    /// </summary>
    /// <remarks>
    /// <para>原先用的是 <c>000…001</c> 这种全零串。那不是一个有意义的近失样本：
    /// 它和真实指纹没有任何共同点，等价于「随便填了一个合法格式的串」，
    /// 无法说明判定是<b>逐字节比较</b>而不是别的什么（比如长度检查、字符集检查）。</para>
    /// <para>现在只翻 1 位：若实现是「比较前 N 位」「忽略大小写」之类，这个样本照样能过——于是能被抓到。</para>
    /// <para><b>必须是唯一实现</b>。发现路径与直连路径共用它——两条路径各写一份，
    /// 就会重演「只改了一个」那种漏（见 <see cref="ResolveTargetAsync"/> 里的实测记录）。</para>
    /// </remarks>
    private static string FlipOneBit(string pinHex)
    {
        byte[] real = Convert.FromHexString(pinHex);

        if (real.Length != CertificatePin.LengthBytes)
        {
            throw new InvalidOperationException(
                $"指纹长度异常：{real.Length} 字节，期望 {CertificatePin.LengthBytes}。");
        }

        real[FlippedByteIndex] ^= FlippedByteMask;
        return Convert.ToHexString(real);
    }

    private static async Task<DiscoveredDevice?> WaitForPeerAsync(
        AcceptanceContext context,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        string normalized = deviceCode.Replace("-", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();

        using CancellationTokenSource budget =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(DiscoverBudget);

        try
        {
            await foreach (DiscoveredDevice device in context.Discovery
                               .WatchAsync(budget.Token)
                               .WithCancellation(budget.Token))
            {
                string candidate = device.DeviceCode.Replace("-", string.Empty, StringComparison.Ordinal)
                    .ToUpperInvariant();

                if (string.Equals(candidate, normalized, StringComparison.Ordinal))
                {
                    return device;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 只有本地发现预算到期是 UNMET；调用方取消必须交给 run owner。
        }

        return null;
    }

    /// <summary>
    /// 低层直连专项的占位 DeviceId；success 必须使用发现冻结的真实身份，拒绝此占位值。
    /// </summary>
    private static readonly Guid UnverifiedPeerId =
        Guid.Parse("00000000-0000-0000-0000-000000000001");

    /// <summary>UI 依次要跑的场景。</summary>
    /// <remarks>
    /// <b>刻意不含 <see cref="ScenarioCrossSubnet"/></b>：它要求控制端到被控端监听地址的包
    /// 源 IP 落在另一个子网。这套 lab 是两机同挂 <c>172.100.166.x</c> + <c>192.168.1.x</c>，
    /// host 只听 RFC1918 绑定，Windows 会自动挑同子网源地址，做不出那个样本；
    /// <c>New-NetRoute</c> / <c>route add</c> 都不能指定源地址。见 HANDOFF §14.13。
    /// </remarks>
    public static readonly string[] MandatoryScenarios =
    {
        ScenarioSuccess,
        ScenarioPinMismatch,
        ScenarioTimeout,
        ScenarioSlowDribble,
    };

    /// <summary>命令行用法里列出的全部场景。</summary>
    public static string UsableScenarios() => string.Join(" | ", KnownScenarios);

    /// <summary>已实现的全部场景。</summary>
    public static readonly string[] KnownScenarios =
    {
        ScenarioSuccess,
        ScenarioPinMismatch,
        ScenarioTimeout,
        ScenarioSlowDribble,
        ScenarioCrossSubnet,
    };

    /// <summary>单场景执行的详细结果。</summary>
    /// <param name="Outcome">结局。</param>
    /// <param name="Detail">人读的说明。</param>
    /// <param name="Fields">机器可读字段。</param>
    /// <param name="HostExpectation">被控端日志里应当出现的对应行。</param>
    /// <param name="ReachedWire">是否真的把连接发出去过（否则谈不上被测）。</param>
    /// <param name="TlsStageRejection">是否死在 TLS 之前/之中（TLS 1.3 下仍可能进入会话处理器）。</param>
    /// <param name="ConnectionObservationUnknown">连接阶段观测是否缺失，不能按未建连或已建连计数。</param>
    internal sealed record ScenarioOutcome(
        AcceptanceOutcome Outcome,
        string Detail,
        IReadOnlyList<KeyValuePair<string, string>> Fields,
        string HostExpectation,
        bool ReachedWire,
        bool TlsStageRejection = false,
        bool ConnectionObservationUnknown = false)
    {
        /// <summary>场景名；由 <see cref="RunAllAsync"/> 回填。</summary>
        public string Scenario { get; init; } = string.Empty;
    }

    /// <summary>目标解析结果：要么有目标，要么带一个前置条件失败的结局。</summary>
    private readonly record struct TargetResolution(ConnectionTarget? Target, ScenarioOutcome? Failure)
    {
        public static TargetResolution Failed(string detail, AcceptanceOutcome outcome) =>
            new(null, new ScenarioOutcome(
                outcome, detail, Array.Empty<KeyValuePair<string, string>>(),
                HostExpectation: "(对端不会看到这条连接)", ReachedWire: false));
    }
}
