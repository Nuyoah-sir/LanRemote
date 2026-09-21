using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using LanRemote.Core.Models;
using LanRemote.Discovery;
using LanRemote.Transport;
using Microsoft.Extensions.Logging;

namespace LanRemote.Acceptance;

/// <summary>
/// 控制端角色：发现 → 冻结快照 → TLS → 按场景说话，并对<b>自己的观测</b>给出结论。
/// </summary>
/// <remarks>
/// <para><b>控制端不宣告里程碑结论</b>（外部评审第 2、3 条）。理由是被测对象的核心行为
/// ——「hello 被接受」——在 M3 里<b>对控制端不可观测</b>：M3 的终态就是关闭连接，
/// 之后没有任何回帧，所以「被接受」和「被拒绝后关闭」在控制端看起来完全一样。
/// 因此这里只输出 <c>clientOutcome=PASS-CLIENT</c> 与 <c>hostEvidence=REQUIRED</c>，
/// 由两机日志的交叉核对（见 <see cref="WriteCrossCheck"/>）来定案。</para>
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
    /// <summary>场景：一切正常，hello 应当被接受。</summary>
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

    /// <summary>
    /// 跑单个场景。返回的结局<b>已并入运行级因素</b>（中止 / 后台故障）。
    /// </summary>
    /// <returns>符合 <see cref="AcceptanceOutcome"/> 语义的结局；枚举值即退出码。</returns>
    /// <remarks>
    /// <b>不写 RUN/BUILD/ENV/ID 头</b>——那些是每轮一次的事实，由 <see cref="RunAllAsync"/>
    /// 在场景循环之前写。曾经写在这里，于是 <c>--all</c> 把同一段 20 行头部印了 4 遍：
    /// 同一个 <c>runId</c> 声称了四次「本轮开始」，读者会以为跑了四轮（本机实测所见）。
    /// </remarks>
    private static async Task<AcceptanceOutcome> RunAsync(
        AcceptanceRun run,
        string scenario,
        string? deviceCode,
        string? address,
        string? pinHex,
        int port,
        bool writeHeader,
        CancellationToken cancellationToken)
    {
        await using AcceptanceContext context = new(LogLevel.Warning, run.Log.WriteLine);
        await context.InitializeAsync(cancellationToken);

        if (writeHeader)
        {
            run.WriteHeader(AcceptanceProfile.Timeouts);
            run.WriteIdentity(context.Identity, context.Certificate, context.ListenAddresses());
        }

        run.Log.WriteLine($"[CLIENT] scenario    = {scenario}");

        TargetResolution resolution = await ResolveTargetAsync(
            run, context, scenario, deviceCode, address, pinHex, port, cancellationToken);

        if (resolution.Target is null)
        {
            ScenarioOutcome failure = resolution.Failure! with { Scenario = scenario };
            LastScenarioOutcome = failure;
            WriteOutcome(run, scenario, failure);

            // 同成功路径：不结算，交给调用方。前置条件不满足也是「场景结论」，不是结算。
            return failure.Outcome;
        }

        ScenarioOutcome outcome = await ExecuteAsync(run, scenario, resolution.Target, cancellationToken);
        outcome = outcome with { Scenario = scenario };
        LastScenarioOutcome = outcome;
        WriteOutcome(run, scenario, outcome);

        // 这里刻意<b>不</b>结算：调用方还要把多场景合并后再结算。
        // 结算必须发生在合并之后，否则「整轮被毒化」会被场景级的 PASS 盖掉。
        return outcome.Outcome;
    }

    /// <summary>
    /// 依次跑多个场景，打印交叉核对清单，返回<b>已结算</b>的整轮结局。
    /// </summary>
    public static async Task<AcceptanceOutcome> RunAllAsync(
        AcceptanceRun run,
        IReadOnlyList<string> scenarios,
        string? deviceCode,
        string? address,
        string? pinHex,
        int port,
        CancellationToken cancellationToken)
    {
        List<ScenarioOutcome> outcomes = new();

        foreach (string scenario in scenarios)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                run.MarkOperatorAbort("控制端场景循环");
                break;
            }

            run.Log.WriteLine(string.Empty);
            run.Log.WriteLine("---------------- 场景 " + scenario + " ----------------");

            // 逐场景单独建上下文：一条连接的失败不该污染下一条的场景状态，
            // 也保证每个场景的日志块边界清楚（评审第 8 条）。
            AcceptanceOutcome outcome = await RunAsync(
                run,
                scenario,
                deviceCode,
                address,
                pinHex,
                port,
                // 头部只由第一个场景写一次。用 outcomes.Count 而不是 bool 变量：
                // 前者是「已经跑完几个场景」的直接读数，不可能和循环进度失配。
                writeHeader: outcomes.Count == 0,
                cancellationToken);

            outcomes.Add(LastScenarioOutcome!);
            run.Log.WriteLine($"---------------- 场景 {scenario} 结束：{outcome.Code()}（{outcome.Describe()}）----------------");
        }

        AcceptanceOutcome combined = outcomes.Select(item => item.Outcome).Combine();
        WriteCrossCheck(run, outcomes, scenarios);

        return run.Settle(combined);
    }

    /// <summary>
    /// 最近一次 <see cref="RunAsync"/> 的详细结果，供 <see cref="RunAllAsync"/> 汇总。
    /// </summary>
    /// <remarks>
    /// 用字段而不是改签名，是为了让 <see cref="RunAsync"/> 保持「返回退出码」这个
    /// 与 headless / UI 都方便的接口。两个方法都不并发，这是安全的。
    /// </remarks>
    private static ScenarioOutcome? LastScenarioOutcome;

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
        string scenario,
        ConnectionTarget target,
        CancellationToken cancellationToken)
    {
        TlsClientConnector connector = new();

        TlsConnection connection;
        try
        {
            connection = await connector.ConnectAsync(
                target, AcceptanceProfile.Timeouts, null, cancellationToken);
        }
        catch (Exception ex)
        {
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
                ScenarioSuccess => await RunSuccessAsync(run, connection, clock, cancellationToken),
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

    /// <summary><c>success</c>：发合法 hello，期望被接受后连按干净关闭。</summary>
    private static async Task<ScenarioOutcome> RunSuccessAsync(
        AcceptanceRun run,
        TlsConnection connection,
        Stopwatch clock,
        CancellationToken cancellationToken)
    {
        await FrameWriter.WriteHelloAsync(
            connection.Stream, AcceptanceProfile.Timeouts.HelloTimeout, cancellationToken);

        run.Log.WriteLine($"[CLIENT] hello sent  = t={clock.ElapsedMilliseconds} ms");

        CloseObservation close = await WaitForPeerCloseAsync(connection, cancellationToken);
        long closedAt = clock.ElapsedMilliseconds;

        run.Log.WriteLine($"[CLIENT] peerClosed  = {close.Kind} t={closedAt} ms // {close.Detail}");

        // 「hello 被接受」对控制端不可观测，能观测的只有「对端没等时限就收尾了」。
        // 合法 hello 下服务端读到即走完流程（毫秒级）；若它没认这个 hello，
        // 收尾时刻必然贴着长度前缀时限。这就是这条断言的全部依据，别再往强里宣称。
        bool fastEnough = closedAt <= (long)AcceptanceProfile.SuccessCloseMax.TotalMilliseconds;
        bool cleanEof = close.Kind == EofKind;
        bool pass = close.Closed && cleanEof && fastEnough;

        // 逐条列出「哪里不符合」，而不是命中的第一个。
        // 实测发现两种失败会同时发生（对端不读 hello 时既不是有序 EOF、也更晚），
        // 只报第一条会让人以为只是收尾方式的问题。
        List<string> problems = new();
        if (!close.Closed)
        {
            problems.Add($"对端没有收尾（{close.Kind}）——{close.Detail}");
        }

        if (close.Closed && !cleanEof)
        {
            problems.Add($"收尾方式不是有序 EOF，而是 {close.Kind}" +
                         "（M3 终态应当发 close_notify 后关闭）");
        }

        if (!fastEnough)
        {
            problems.Add($"收尾时刻 {closedAt} ms 超过上界 " +
                         $"{AcceptanceProfile.SuccessCloseMax.TotalMilliseconds:0} ms" +
                         "——像是等到了长度前缀时限才关，hello 很可能根本没被接受");
        }

        string detail = problems.Count == 0
            ? $"已发 hello、对端在 {closedAt} ms 有序 EOF 收尾（早于时限 " +
              $"{AcceptanceProfile.Timeouts.LengthPrefixTimeout.TotalMilliseconds:0} ms）"
            : string.Join("；", problems);

        return new ScenarioOutcome(
            pass ? AcceptanceOutcome.Pass : AcceptanceOutcome.Fail,
            detail,
            Fields(
                ("peerClosed", close.Kind),
                ("closedAtMs", closedAt.ToString()),
                ("successCloseMaxMs", AcceptanceProfile.SuccessCloseMax.TotalMilliseconds.ToString("0")),
                ("cleanEof", cleanEof.ToString())),
            HostExpectation: "outcome=PreAuthenticated rejection=-",
            ReachedWire: true);
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
                                 "也可能完全无行（TLS 1.2）——但绝不能是 outcome=PreAuthenticated)",
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
            HostExpectation: "outcome=PreAuthenticated rejection=-",
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
    /// <c>success</c>／<c>timeout</c> 里都算「对端关闭了」——于是操作员一点中止，
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
                    $"hello 之后对端还发了 {read} 字节——M3 终态不该有后续数据");
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
            $"hostEvidence={(outcome.ReachedWire ? "REQUIRED" : "N/A")} " +
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

        // ①②③ 是「通过时应有的样子」，所以按 PASS 计数。
        int expectPreAuthenticated = outcomes.Count(
            item => item.Outcome == AcceptanceOutcome.Pass &&
                    item.Scenario == ScenarioSuccess);

        int expectPreAuthTimeout = outcomes.Count(
            item => item.Outcome == AcceptanceOutcome.Pass &&
                    item.Scenario is ScenarioTimeout or ScenarioSlowDribble);

        bool anyFailures = outcomes.Any(item => item.Outcome != AcceptanceOutcome.Pass);

        // ④ 的区间**只依赖「连接走到了哪一步」**，不依赖场景通过与否——
        // 否则场景一失败，区间自己就跟着漂，读者会以为区间是实测值。
        //
        // 每条上过线的连接对「被控端进入会话处理器的行数」的贡献：
        //   完成了 TLS（TlsStageRejection=false） → 必然 1 行；
        //   死在 TLS（TlsStageRejection=true）    → 0 或 1 行（TLS 1.3 幽灵行；TLS 1.2 下 0 行）；
        //   cross-subnet                          → 恒 0 行（死在同子网闸门，压根不到 TLS）。
        int definitelyEntersHandler = outcomes.Count(
            item => item.ReachedWire && !item.TlsStageRejection &&
                    item.Scenario != ScenarioCrossSubnet);

        int mayEnterHandler = outcomes.Count(
            item => item.ReachedWire && item.TlsStageRejection &&
                    item.Scenario != ScenarioCrossSubnet);

        int lower = definitelyEntersHandler;
        int upper = definitelyEntersHandler + mayEnterHandler;

        List<string> lines = new()
        {
            $"控制端本次共建立 TCP+TLS 连接：{issued} 条" +
            $"（场景 {scenarios.Count} 个，前置条件不满足的 {scenarios.Count - issued} 个没上过线）",
            string.Empty,
            "被控端逐条应当出现（可机器判定）：",
        };

        foreach (string scenario in scenarios)
        {
            ScenarioOutcome? item = outcomes.FirstOrDefault(candidate => candidate.Scenario == scenario);
            lines.Add(item is null
                ? $"  {scenario,-14} -> （未执行）"
                : $"  {scenario,-14} -> {item.HostExpectation}");
        }

        lines.Add(string.Empty);
        // 括号里的「为什么是这个数」必须跟着实际场景走。写死「timeout 与 slow-dribble 各一条」
        // 在只跑单个场景时会变成假说明——而假说明比没有说明更坏，它会被当成核对依据。
        string preAuthTimeoutWhy = string.Join(" + ",
            outcomes.Where(item => item.Outcome == AcceptanceOutcome.Pass &&
                                   item.Scenario is ScenarioTimeout or ScenarioSlowDribble)
                    .Select(item => item.Scenario));

        lines.Add("被控端汇总行必须同时满足下面四条：");
        lines.Add($"  ① outcome=PreAuthenticated 的行恰好 {expectPreAuthenticated} 条（只有 success 该走到这）");
        lines.Add($"  ② rejection=pre-auth-timeout 的行恰好 {expectPreAuthTimeout} 条" +
                  (preAuthTimeoutWhy.Length > 0 ? $"（{preAuthTimeoutWhy}）" : string.Empty));
        lines.Add("  ③ 其余任何一行都不得是 PreAuthenticated，也不得是 pre-auth-timeout");
        lines.Add($"  ④ connectionsEnteringSessionHandler 落在 {lower}..{upper}（区间原因见下）");
        lines.Add($"  ⑤ listenersStoppedCleanly=True 且 activeAtStop=0");

        if (anyFailures)
        {
            lines.Add(string.Empty);
            lines.Add("⚠ 本轮有场景未通过：①②③ 里的数字是「全都通过时**本该**是多少」，不是实测值。");
            lines.Add("  它们只能用来核对**通过了的**那些场景；未通过场景由上面逐条的 clientOutcome 定案，");
            lines.Add("  不要拿这几条去反推「到底跑没跑到」。");
        }

        // 跨子网的期望与「通过与否」无关：只要它上了线，就必须一行都不留——
        // 同子网闸门在 TLS 之前就关掉了连接，服务端静默。所以这里按「上过线」计数。
        int crossSubnetOnWire = outcomes.Count(
            item => item.ReachedWire && item.Scenario == ScenarioCrossSubnet);

        if (crossSubnetOnWire > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"另有 {crossSubnetOnWire} 个跨子网场景预期**一行都不留**" +
                      "（死在同子网闸门，TransportHost 静默）；这一条不因该场景 Pass/Fail 而变。");
        }

        lines.Add(string.Empty);
        lines.Add("配对方法（不要靠计数相等来配对）：本机日志里每个场景的 [CLIENT][CORRELATE] 行有");
        lines.Add("  local=<本机IP>:<临时端口>，被控端有 peer=<同一个端点>。用这对数字**唯一配对**一条连接。");
        lines.Add("  仅当 local 显示 unavailable（socket 已拆）时才退回按时间顺序配，并在记录里注明。");
        lines.Add(string.Empty);
        lines.Add("★ 区间 ④ 的取值取决于一个实测事实，不要猜：");
        lines.Add("  客户端拒绝服务端证书时发的是 TLS alert。TLS 1.3 下服务端在收到该 alert 之前");
        lines.Add("  就已经认为握手完成，于是这条连接照样进入会话处理器，读到 EOF 后给出");
        lines.Add("  rejection=pre-auth-eof（本机环回实测值）；TLS 1.2 下服务端握手会直接失败、不留行。");
        lines.Add("  两种都属于正常，但**绝不能**出现 outcome=PreAuthenticated。");
        lines.Add(string.Empty);
        lines.Add("被控端其余盲区：同子网拒绝 / 准入拒绝 / TLS 失败全部静默 return（HANDOFF §14.12），");
        lines.Add("本进程观测不到，日志里一律标 UNOBSERVABLE，不填 0。");

        run.WriteBlock("两机交叉核对（把这台和被控端的日志放一起看）", lines);

        run.Log.WriteLine(
            "[VERDICT] M3 = PENDING-HOST-EVIDENCE // 控制端的观测不足以单独判定里程碑通过，" +
            "必须与被控端日志的汇总行对齐");
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
        catch (OperationCanceledException)
        {
        }

        return null;
    }

    /// <summary>
    /// 直连场景（<c>--address/--pin</c>）里对端的 deviceId 未经验证，这里填一个占位值——
    /// 它不参与任何安全判定，pin 与同子网校验都在它之外。
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
    /// <param name="TlsStageRejection">是否死在 TLS 之前/之中（不会进被控端会话处理器）。</param>
    private sealed record ScenarioOutcome(
        AcceptanceOutcome Outcome,
        string Detail,
        IReadOnlyList<KeyValuePair<string, string>> Fields,
        string HostExpectation,
        bool ReachedWire,
        bool TlsStageRejection = false)
    {
        /// <summary>场景名；由 <see cref="WriteCrossCheck"/> 回填。</summary>
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
