using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

/// <summary>
/// 步骤 13～17 的验收：显式交接（ADR-037）与认证状态机（ADR-038）的端到端行为。
/// </summary>
/// <remarks>
/// <para>走的是<b>真实回环 TLS + 真实 <see cref="TransportHost"/></b>：认证的判据全在
/// 「两端各自算出什么、谁先说什么」上，替身流很容易测出一个只在替身里成立的行为。</para>
/// <para>客户端侧在本层是<b>测试脚本</b>（真实产品客户端在 M4 阶段 4）：它独立使用
/// <see cref="AuthTranscriptBuilder"/> 重算 proof（不经过服务端任何路径），成功路径上
/// 还按客户端语义验证 serverProof——两套独立算式交叉印证，而不是自己验自己。</para>
/// <para><b>限流计数的判据</b>（ADR-038 第 2 条）只在这条唯一入口被观测：
/// 密码学校验失败 = +1；格式违规 / 超时 / 断连 / 审批结果 = 不计。</para>
/// </remarks>
public sealed class ControlAuthSessionTests
{
    private static readonly Guid ServerDeviceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid TestClientDeviceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    /// <summary>测试已知的正确访问密钥（128-bit；产品路径由 DPAPI 存储提供）。</summary>
    private static readonly byte[] GoodKey = Convert.FromHexString("0F1E2D3C4B5A69788796A5B4C3D2E1F0");

    /// <summary>「攻击者」用的错误密钥——形态相同，值不同。</summary>
    private static readonly byte[] AttackKey = Convert.FromHexString("F0E1D2C3B4A5968778695A4B3C2D1E0F");

    private const string TestClientName = "AUTH-TEST-CLIENT";

    /// <summary>帧读写预算（测试内足够宽松；被验的是状态机自己的窗口，不是分段时限）。</summary>
    private static readonly TimeSpan FrameDeadline = TimeSpan.FromSeconds(10);

    /// <summary>默认缩放选项：机器/审批窗口 5 s、失败延时 0（延时专项见 <see cref="Failed_Proof_Delay_Is_Bounded_By_The_Configured_Range"/>）。</summary>
    private static ControlAuthOptions FastOptions => new()
    {
        MachineWindow = TimeSpan.FromSeconds(5),
        ApprovalWindow = TimeSpan.FromSeconds(5),
        FailureDelayMin = TimeSpan.Zero,
        FailureDelayMax = TimeSpan.Zero,
    };

    // ═══════════════════════════ 交接与 exactly-once（ADR-037 第 2 条） ═══════════════════════════

    /// <summary>同一连接的 <c>BeginAuthentication</c> 只允许一次；第二次立即抛，绝不产出第二份 challenge。</summary>
    [Fact]
    public void BeginAuthentication_Is_Exactly_Once_Per_Handoff()
    {
        (ControlPreAuthHandoff handoff, _) = CreateDetachedHandoff();

        ControlAuthSession first = handoff.BeginAuthentication(NewContext(StubApprovalGate.Approve()), BuildAuthTimeouts());
        Assert.NotNull(first);
        Assert.Equal(ControlSessionState.Authenticating, first.State);

        InvalidOperationException guard = Assert.Throws<InvalidOperationException>(
            () => handoff.BeginAuthentication(NewContext(StubApprovalGate.Approve()), BuildAuthTimeouts()));
        Assert.Contains("只允许", guard.Message, StringComparison.Ordinal);
    }

    /// <summary>同一认证会话的 <c>RunAsync</c> 只允许一次（BeginAuthentication exactly-once 的下游一半）。</summary>
    [Fact]
    public async Task Auth_Session_Cannot_Be_Run_Twice()
    {
        (ControlPreAuthHandoff handoff, _) = CreateDetachedHandoff();
        ControlAuthSession session = handoff.BeginAuthentication(NewContext(StubApprovalGate.Approve()), BuildAuthTimeouts());

        // 第一次会抛（detached 的 SslStream 从未认证过，写 challenge 就不允许）——本用例不关心它抛什么，
        // 关心的是：不管第一次怎么收场，这个会话都不允许再跑第二次。
        await Assert.ThrowsAnyAsync<Exception>(
            () => session.RunAsync(CancellationToken.None));

        InvalidOperationException guard = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.RunAsync(CancellationToken.None));
        Assert.Contains("只允许跑一次", guard.Message, StringComparison.Ordinal);
    }

    // ═══════════════════════════ 成功路径（全链） ═══════════════════════════

    /// <summary>
    /// 正确密钥 + 审批通过 → 认证成立：success 帧可被客户端独立验证（serverProof）、
    /// 会话登记可见且 token 与下发的完全一致、连接保持到客户端断开为止。
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Correct_Key_With_Approval_Reaches_Authenticated_And_Holds_The_Connection()
    {
        StubApprovalGate gate = StubApprovalGate.Approve();
        SessionRegistry registry = new();
        AuthClientProbe probe = new()
        {
            SignPermission = SessionPermission.Control,
            SendPermission = SessionPermission.Control,
        };

        Task<AuthScenario> scenarioTask = RunScenarioAsync(
            (connection, p, ct) => p.SpeakAuthAsync(connection, ct),
            gate,
            registry: registry,
            probe: probe,
            midflight: async (connection, p) =>
            {
                await p.TerminalSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(p.Success is not null, $"没收到 auth_success（帧序列：{string.Join(",", p.Frames)}）。");
                Assert.True(p.ServerProofValid == true, "客户端按 grant transcript 重算 serverProof 验证失败。");
                Assert.True(p.SawApprovalPending);
                Assert.Equal(
                    new[] { "auth_challenge", "approval_pending", "auth_success" },
                    p.Frames);

                // 登记表：认证成立的另一半证据（DoD「auth success 才能有 session」的正例）。
                Assert.True(await WaitUntilAsync(() => registry.ActiveSessionCount == 1));
                ControlSessionSummary summary = Assert.Single(registry.Snapshot());
                Assert.Equal(p.Challenge!.SessionId, summary.SessionId);
                Assert.Equal(TestClientDeviceId, summary.ClientDeviceId);
                Assert.Equal(TestClientName, summary.ClientName);
                Assert.Equal(SessionPermission.Control, summary.GrantedPermission);
                Assert.Equal(IPAddress.Loopback, summary.RemoteAddress);

                // token 生命周期：登记中的 token 与下发的逐字节相同（内存拷贝，外部不可见）。
                Assert.True(registry.TryGetSessionToken(p.Challenge.SessionId, out ReadOnlyMemory<byte> token));
                Assert.True(CryptographicOperations.FixedTimeEquals(
                    token.Span, p.Success!.SessionToken.Span));

                // 审批请求的内容（评审 #40/#41 的素材面）。
                LocalApprovalRequest request = Assert.IsType<LocalApprovalRequest>(gate.LastRequest);
                Assert.Equal(TestClientDeviceId, request.ClientDeviceId);
                Assert.Equal(TestClientName, request.ClientName);
                Assert.Equal(SessionPermission.Control, request.RequestedPermission);
                Assert.Equal(
                    LocalApprovalRequest.ComputeShortCode(p.Challenge.SessionId, p.ClientNonce),
                    request.ShortCode);
                Assert.Equal(LocalApprovalRequest.ShortCodeLength, request.ShortCode.Length);

                // 成功之后连接必须保持（挂住读 = 对端没有 EOF）；这笔读在 dispose 后自然收场。
                Task<int> watch = connection.Stream.ReadAsync(new byte[1]).AsTask();
                await Assert.ThrowsAsync<TimeoutException>(
                    () => watch.WaitAsync(TimeSpan.FromMilliseconds(300)));
                p.PendingWatch = watch;
            });

        AuthScenario scenario = await scenarioTask;

        Assert.True(scenario.Result.Completed, scenario.Result.Rejection);
        Assert.Equal(ControlSessionState.Authenticated, scenario.Result.State);
        Assert.Null(scenario.Result.Rejection);
        Assert.Equal(scenario.Probe.Challenge!.SessionId, scenario.Result.SessionId);

        // 断开之后：会话注销、token 不再可取；失败计数为零（成功清空）。
        Assert.True(await WaitUntilAsync(() => registry.ActiveSessionCount == 0));
        Assert.False(registry.TryGetSessionToken(scenario.Result.SessionId, out _));
        Assert.Equal(0, scenario.Limiter.CountRecentFailures(IPAddress.Loopback));
    }

    // ═══════════════════════════ 失败路径：密码学与协议面 ═══════════════════════════

    /// <summary>
    /// 错误密钥：唯一计入限流的失败。对端只看到 generic authentication_failed（不区分原因）。
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Wrong_Key_Is_Rejected_Generically_And_Counts_Exactly_Once()
    {
        AuthClientProbe probe = new() { AccessKey = AttackKey };
        AuthScenario scenario = await RunScenarioAsync(
            (connection, p, ct) => p.SpeakAuthAsync(connection, ct),
            StubApprovalGate.Approve(),
            probe: probe);

        Assert.False(scenario.Result.Completed);
        Assert.Equal(ControlAuthSession.RejectProofMismatch, scenario.Result.Rejection);
        Assert.True(probe.SawGenericFailure, "对端应当收到 generic 失败帧。");
        Assert.DoesNotContain("auth_success", probe.Frames);
        Assert.Equal(0, scenario.Registry.ActiveSessionCount);
        Assert.Equal(1, scenario.Limiter.CountRecentFailures(IPAddress.Loopback));
    }

    /// <summary>
    /// proof 重算把字段全部隐式绑死——篡改签名内容（这里：签名用 control、报文却声称 view）
    /// 必然 MAC 失败。这条钉的是「没有独立对齐校验、对齐由重算承担」的设计（ADR-037 评审 A11 面）。
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Tampered_Permission_In_Response_Is_Rejected()
    {
        AuthClientProbe probe = new()
        {
            SignPermission = SessionPermission.Control,  // 签名覆盖的 transcript 用 control
            SendPermission = SessionPermission.ViewOnly, // 报文里改成 view（两者不一致 = 篡改）
        };

        AuthScenario scenario = await RunScenarioAsync(
            (connection, p, ct) => p.SpeakAuthAsync(connection, ct),
            StubApprovalGate.Approve(),
            probe: probe);

        Assert.False(scenario.Result.Completed);
        Assert.Equal(ControlAuthSession.RejectProofMismatch, scenario.Result.Rejection);
        Assert.Equal(1, scenario.Limiter.CountRecentFailures(IPAddress.Loopback));
    }

    /// <summary>
    /// 证书指纹同理：proof 若绑定的是「别的证书」的摘要，重算必败——
    /// 「challenge 里的 certSha256 = 本连接实际出示的证书」这条链条由此可证伪。
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Tampered_Certificate_Fingerprint_In_Proof_Is_Rejected()
    {
        using X509Certificate2 otherCertificate = TestCertificateFactory.Create(
            new TestCertificateOptions { NotBefore = DateTimeOffset.UtcNow.AddMinutes(-1) });

        AuthClientProbe probe = new()
        {
            SignedCertSha256 = SHA256.HashData(otherCertificate.RawData),
        };

        AuthScenario scenario = await RunScenarioAsync(
            (connection, p, ct) => p.SpeakAuthAsync(connection, ct),
            StubApprovalGate.Approve(),
            probe: probe);

        Assert.False(scenario.Result.Completed);
        Assert.Equal(ControlAuthSession.RejectProofMismatch, scenario.Result.Rejection);
        Assert.Equal(1, scenario.Limiter.CountRecentFailures(IPAddress.Loopback));
    }

    /// <summary>帧格式违规（长度 0）：断开 + generic 失败，但<b>不计</b>入限流（防垃圾帧刷封禁）。</summary>
    [Fact(Timeout = 120_000)]
    public async Task Frame_Violation_Sends_Generic_Failure_And_Does_Not_Count()
    {
        AuthScenario scenario = await RunScenarioAsync(
            async (connection, p, ct) =>
            {
                await FrameWriter.WriteHelloAsync(connection.Stream, FrameDeadline, ct);
                await p.ReadChallengeAsync(connection.Stream, ct);

                // 长度前缀全 0 = 声称 0 字节 payload（协议违规）。
                byte[] zeroPrefix = new byte[TransportConstants.LengthPrefixBytes];
                await connection.Stream.WriteAsync(zeroPrefix, ct);
                await connection.Stream.FlushAsync(ct);

                byte[] payload = await ReadFramePayloadAsync(connection.Stream, ct);
                p.SawGenericFailure = AuthenticationFailedFrame.TryParse(payload, out _);
                p.TerminalSeen.TrySetResult();
            },
            StubApprovalGate.Approve());

        Assert.False(scenario.Result.Completed);
        Assert.Equal(
            $"{ControlAuthSession.RejectFrameViolation}:{FrameReader.RejectZeroLength}",
            scenario.Result.Rejection);
        Assert.True(scenario.Probe.SawGenericFailure);
        Assert.Equal(0, scenario.Limiter.CountRecentFailures(IPAddress.Loopback));
        Assert.Equal(0, scenario.Registry.ActiveSessionCount);
    }

    /// <summary>response 之前断开：EOF 收场、不计失败——不是「猜错了密钥」，而是对端走了。</summary>
    [Fact(Timeout = 120_000)]
    public async Task Disconnect_Before_Response_Ends_As_Eof_And_Does_Not_Count()
    {
        AuthScenario scenario = await RunScenarioAsync(
            async (connection, p, ct) =>
            {
                await FrameWriter.WriteHelloAsync(connection.Stream, FrameDeadline, ct);
                await p.ReadChallengeAsync(connection.Stream, ct);
                connection.Dispose();
                p.TerminalSeen.TrySetResult();
            },
            StubApprovalGate.Approve());

        Assert.False(scenario.Result.Completed);
        Assert.Equal(ControlAuthSession.RejectEof, scenario.Result.Rejection);
        Assert.Equal(0, scenario.Limiter.CountRecentFailures(IPAddress.Loopback));
    }

    /// <summary>访问密钥加载失败必须 fail closed（不放过、不崩溃）。</summary>
    [Fact(Timeout = 120_000)]
    public async Task Key_Store_Failure_Fails_Closed()
    {
        AuthScenario scenario = await RunScenarioAsync(
            (connection, p, ct) => p.SpeakAuthAsync(connection, ct),
            StubApprovalGate.Approve(),
            secretStore: new FailingAccessSecretStore());

        Assert.False(scenario.Result.Completed);
        Assert.Equal(ControlAuthSession.RejectKeyUnavailable, scenario.Result.Rejection);
        Assert.Equal(0, scenario.Registry.ActiveSessionCount);
        Assert.Equal(0, scenario.Limiter.CountRecentFailures(IPAddress.Loopback));
    }

    // ═══════════════════════════ 失败路径：时限与限流 ═══════════════════════════

    /// <summary>
    /// 机器窗口：自 challenge 写出前起算的<b>绝对</b> deadline。客户端拿到 challenge 后沉默，
    /// 必须贴着窗口（约 700 ms）被切，而不是分段时限（5 s）。
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Machine_Window_Cuts_A_Stalling_Client_And_Does_Not_Count()
    {
        ControlAuthOptions options = FastOptions with { MachineWindow = TimeSpan.FromMilliseconds(700) };

        AuthScenario scenario = await RunScenarioAsync(
            async (connection, p, ct) =>
            {
                await FrameWriter.WriteHelloAsync(connection.Stream, FrameDeadline, ct);
                await p.ReadChallengeAsync(connection.Stream, ct);
                await Task.Delay(Timeout.Infinite, ct);
            },
            StubApprovalGate.Approve(),
            options: options);

        Assert.False(scenario.Result.Completed);
        Assert.Equal(ControlAuthSession.RejectTimeout, scenario.Result.Rejection);
        Assert.True(
            scenario.Elapsed >= TimeSpan.FromMilliseconds(400),
            $"过早返回（{scenario.Elapsed}），不像是窗口（700 ms）在起作用。");
        Assert.True(
            scenario.Elapsed < TimeSpan.FromSeconds(2),
            $"耗时 {scenario.Elapsed}——窗口没生效，贴到了 5 s 的分段时限。");
        Assert.Equal(0, scenario.Limiter.CountRecentFailures(IPAddress.Loopback));
    }

    /// <summary>被罚 IP 连 challenge 都不发：收到的第一帧只能是 generic 失败（绝无 challenge）。</summary>
    [Fact(Timeout = 120_000)]
    public async Task Throttled_Source_Never_Receives_A_Challenge()
    {
        FakeClock clock = new(DateTimeOffset.UtcNow);
        FailedAuthLimiter limiter = new(clock);
        for (int i = 0; i < limiter.MaxFailures; i++)
        {
            limiter.RecordFailure(IPAddress.Loopback);
        }

        Assert.True(limiter.IsBlocked(IPAddress.Loopback, out _));

        AuthScenario scenario = await RunScenarioAsync(
            async (connection, p, ct) =>
            {
                await FrameWriter.WriteHelloAsync(connection.Stream, FrameDeadline, ct);
                try
                {
                    byte[] payload = await ReadFramePayloadAsync(connection.Stream, ct);
                    if (AuthChallengeFrame.TryParse(payload, out AuthChallengeFrame? challenge, out _))
                    {
                        // 能做到这一步就已经是 bug——记录下来，让断言变红。
                        p.RecordChallenge(challenge!, "auth_challenge");
                    }

                    p.SawGenericFailure = AuthenticationFailedFrame.TryParse(payload, out _);
                }
                catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException)
                {
                    // 失败帧可能先于 FIN 到达，也可能被收线抢掉——本用例的硬判据在服务端。
                }

                p.TerminalSeen.TrySetResult();
            },
            StubApprovalGate.Approve(),
            limiter: limiter);

        Assert.False(scenario.Result.Completed);
        Assert.Equal(ControlAuthSession.RejectThrottled, scenario.Result.Rejection);
        Assert.Null(scenario.Probe.Challenge);
        Assert.True(scenario.Probe.SawGenericFailure, "被限流的连接也应当收到 generic 失败帧。");

        // 被限流拒绝本身不计数：计数保持原值（5），封禁不因拒绝而延长。
        Assert.Equal(limiter.MaxFailures, limiter.CountRecentFailures(IPAddress.Loopback));
        Assert.Equal(0, scenario.Registry.ActiveSessionCount);
    }

    // ═══════════════════════════ 失败路径：审批面 ═══════════════════════════

    /// <summary>
    /// 审批的每种终态都 fail closed，且互相可分辨（本地短码）；对端一律 generic。
    /// 「gate 抛异常」也归 Unavailable——绝不静默通过。
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData("denied", ControlAuthSession.RejectApprovalDenied)]
    [InlineData("cancelled", ControlAuthSession.RejectApprovalCancelled)]
    [InlineData("unavailable", ControlAuthSession.RejectApprovalUnavailable)]
    [InlineData("timedout", ControlAuthSession.RejectApprovalTimeout)]
    [InlineData("raising", ControlAuthSession.RejectApprovalUnavailable)]
    public async Task Approval_Terminal_States_All_Fail_Closed(string kind, string expected)
    {
        StubApprovalGate gate = kind switch
        {
            "denied" => StubApprovalGate.WithOutcome(LocalApprovalOutcome.Denied),
            "cancelled" => StubApprovalGate.WithOutcome(LocalApprovalOutcome.Cancelled),
            "unavailable" => StubApprovalGate.WithOutcome(LocalApprovalOutcome.Unavailable),
            "timedout" => StubApprovalGate.WithOutcome(LocalApprovalOutcome.TimedOut),
            "raising" => StubApprovalGate.Raising(new InvalidOperationException("审批面内部错误")),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

        AuthScenario scenario = await RunScenarioAsync(
            (connection, p, ct) => p.SpeakAuthAsync(connection, ct),
            gate);

        Assert.False(scenario.Result.Completed);
        Assert.Equal(expected, scenario.Result.Rejection);
        Assert.True(scenario.Probe.SawApprovalPending, "审批请求已受理（approval_pending 必达）。");
        Assert.Equal(0, scenario.Registry.ActiveSessionCount);

        // 审批类失败不计入限流（它们不是「猜密钥」）。
        Assert.Equal(0, scenario.Limiter.CountRecentFailures(IPAddress.Loopback));
    }

    /// <summary>审批决定回指的请求 ID 不匹配：无效决定，fail closed。</summary>
    [Fact(Timeout = 120_000)]
    public async Task Wrong_Request_Id_In_Decision_Is_Rejected()
    {
        AuthScenario scenario = await RunScenarioAsync(
            (connection, p, ct) => p.SpeakAuthAsync(connection, ct),
            StubApprovalGate.WrongRequestId());

        Assert.False(scenario.Result.Completed);
        Assert.Equal(ControlAuthSession.RejectApprovalInvalid, scenario.Result.Rejection);
    }

    /// <summary>越权授予（请求 view、批 control）：服务端自校验拒绝（评审 #39）。</summary>
    [Fact(Timeout = 120_000)]
    public async Task Over_Grant_In_Decision_Is_Rejected()
    {
        AuthClientProbe probe = new()
        {
            SignPermission = SessionPermission.ViewOnly,
            SendPermission = SessionPermission.ViewOnly,
        };

        AuthScenario scenario = await RunScenarioAsync(
            (connection, p, ct) => p.SpeakAuthAsync(connection, ct),
            StubApprovalGate.Approve(SessionPermission.Control), // 试图给得比请求多
            probe: probe);

        Assert.False(scenario.Result.Completed);
        Assert.Equal(ControlAuthSession.RejectApprovalInvalid, scenario.Result.Rejection);
    }

    /// <summary>降级授予（请求 control、批 view）：合法——granted ≤ requested（评审 #39）。</summary>
    [Fact(Timeout = 120_000)]
    public async Task Downgrade_Control_To_ViewOnly_Is_Allowed()
    {
        AuthClientProbe probe = new()
        {
            SignPermission = SessionPermission.Control,
            SendPermission = SessionPermission.Control,
        };

        AuthScenario scenario = await RunScenarioAsync(
            (connection, p, ct) => p.SpeakAuthAsync(connection, ct),
            StubApprovalGate.Approve(SessionPermission.ViewOnly),
            probe: probe,
            midflight: async (_, p) =>
            {
                await p.TerminalSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(p.Success is not null, $"帧序列：{string.Join(",", p.Frames)}");
                Assert.Equal(SessionPermission.ViewOnly, p.Success!.GrantedPermission);
                Assert.True(p.ServerProofValid == true);
            });

        Assert.True(scenario.Result.Completed, scenario.Result.Rejection);
        Assert.Equal(1, scenario.Gate.RequestCount);
    }

    /// <summary>
    /// 审批窗口到点即拒；窗口之后到达的决定一律作废（评审 #44）——
    /// 迟到的 Approved 不得产生任何登记。
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Approval_Window_Cuts_And_Late_Approval_Is_Discarded()
    {
        TaskCompletionSource<LocalApprovalDecision> late = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubApprovalGate gate = StubApprovalGate.WaitOn(late);

        ControlAuthOptions options = FastOptions with { ApprovalWindow = TimeSpan.FromMilliseconds(600) };

        AuthScenario scenario = await RunScenarioAsync(
            (connection, p, ct) => p.SpeakAuthAsync(connection, ct),
            gate,
            options: options);

        Assert.False(scenario.Result.Completed);
        Assert.Equal(ControlAuthSession.RejectApprovalTimeout, scenario.Result.Rejection);
        Assert.True(
            scenario.Elapsed >= TimeSpan.FromMilliseconds(400),
            $"过早返回（{scenario.Elapsed}），不像是审批窗口（600 ms）在起作用。");
        Assert.True(scenario.Elapsed < TimeSpan.FromSeconds(5), $"耗时 {scenario.Elapsed}。");

        // 迟到的批准：窗口已关。放它进来，不得产生任何登记。
        LocalApprovalRequest request = Assert.IsType<LocalApprovalRequest>(gate.LastRequest);
        late.TrySetResult(new LocalApprovalDecision(
            request.RequestId, LocalApprovalOutcome.Approved, request.RequestedPermission));
        await Task.Delay(250);

        Assert.Equal(0, scenario.Registry.ActiveSessionCount);
    }

    /// <summary>
    /// 审批期间断连：决定即使随后到达也作废，绝不给已离开的连接发 token（评审 #45）。
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Approval_Disconnect_Discards_A_Later_Decision()
    {
        TaskCompletionSource<LocalApprovalDecision> late = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubApprovalGate gate = StubApprovalGate.WaitOn(late);

        AuthScenario scenario = await RunScenarioAsync(
            async (connection, p, ct) =>
            {
                await FrameWriter.WriteHelloAsync(connection.Stream, FrameDeadline, ct);
                await p.ReadChallengeAsync(connection.Stream, ct);
                await p.SendResponseAsync(connection.Stream, ct);

                byte[] payload = await ReadFramePayloadAsync(connection.Stream, ct);
                Assert.True(ApprovalPendingFrame.TryParse(payload, out _), "先收到 approval_pending。");
                p.SawApprovalPending = true;

                // 审批期间走人：服务端应以「断连」收场。
                connection.Dispose();

                // 连上之后 150 ms 再放一个迟到的批准进来——它不得改变结局。
                // （刻意不绑 stall 令牌：harness 在服务端出结局后就会取消它，绑了会把这笔「迟到决定」吞掉。）
                await Task.Delay(150);
                if (gate.LastRequest is { } request)
                {
                    late.TrySetResult(new LocalApprovalDecision(
                        request.RequestId, LocalApprovalOutcome.Approved, request.RequestedPermission));
                }

                p.TerminalSeen.TrySetResult();
            },
            gate);

        Assert.False(scenario.Result.Completed);
        Assert.Equal(ControlAuthSession.RejectApprovalDisconnected, scenario.Result.Rejection);
        Assert.True(scenario.Probe.SawApprovalPending);
        Assert.Equal(0, scenario.Registry.ActiveSessionCount);
    }

    /// <summary>
    /// 待批配额（独立于连接准入；每源 1）：同源第二条连接在配额处被直接切掉，
    /// 连 approval_pending 都不会收到；第一条不受影响。
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Approval_Quota_Cuts_The_Second_Pending_From_The_Same_Source()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        int port = GetFreePort();

        SessionRegistry registry = new();
        FailedAuthLimiter limiter = new();
        ConnectionAdmissionLimiter pendingLimiter = new(globalLimit: 3, perAddressLimit: 1);

        TaskCompletionSource<LocalApprovalDecision> firstDecision =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubApprovalGate gate = StubApprovalGate.WaitOn(firstDecision);

        ControlAuthContext context = NewContext(gate, registry, pendingLimiter, limiter, FastOptions, null);

        TaskCompletionSource<ControlAuthResult> outcomeA = NewOutcome();
        TaskCompletionSource<ControlAuthResult> outcomeB = NewOutcome();

        int connectionIndex = 0;

        TransportHost host = new(
            new[] { IPAddress.Loopback },
            new StubSubnetPolicy(allow: true),
            certificate,
            async (connection, cancellationToken) =>
            {
                int index = Interlocked.Increment(ref connectionIndex);
                TaskCompletionSource<ControlAuthResult> outcome = index == 1 ? outcomeA : outcomeB;

                ControlPreAuthSession preAuth = new();
                ControlPreAuthResult pre = await preAuth.RunAsync(connection, BuildAuthTimeouts(), cancellationToken);
                if (!pre.Completed || pre.Handoff is null)
                {
                    return;
                }

                try
                {
                    ControlAuthSession session = pre.Handoff.BeginAuthentication(context, BuildAuthTimeouts());
                    outcome.TrySetResult(await session.RunAsync(cancellationToken));
                }
                catch (Exception ex)
                {
                    outcome.TrySetException(ex);
                }
            },
            new TransportHostOptions { Port = port, Timeouts = BuildAuthTimeouts() });

        await using (host)
        {
            TransportHostStartResult start = host.Start();
            Assert.True(start.IsListening);

            using CancellationTokenSource stall = new();

            // 连接 A：一路走到待批（gate 被调用 = 租约已拿到、approval_pending 已写出）。
            TlsConnection connectionA = await ConnectAsync(certificate, port);
            AuthClientProbe probeA = new();
            Task clientA = Task.Run(() => probeA.SpeakAuthAsync(connectionA, stall.Token));

            LocalApprovalRequest firstRequest = await gate.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, pendingLimiter.GlobalInUse);

            // 连接 B：同源第二条——配额应当在此把它切掉。
            TlsConnection connectionB = await ConnectAsync(certificate, port);
            AuthClientProbe probeB = new();
            Task clientB = Task.Run(() => probeB.SpeakAuthAsync(connectionB, stall.Token));

            ControlAuthResult resultB = await outcomeB.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(resultB.Completed);
            Assert.Equal(ControlAuthSession.RejectApprovalQuota, resultB.Rejection);
            Assert.False(probeB.SawApprovalPending, "配额被拒的连接不该收到 approval_pending。");
            Assert.DoesNotContain("auth_success", probeB.Frames);

            // 释放 A：迟到的份额不受影响——A 正常成功。
            firstDecision.TrySetResult(new LocalApprovalDecision(
                firstRequest.RequestId, LocalApprovalOutcome.Approved, firstRequest.RequestedPermission));

            await probeA.TerminalSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(probeA.Success is not null, $"帧序列：{string.Join(",", probeA.Frames)}");
            Assert.True(await WaitUntilAsync(() => pendingLimiter.GlobalInUse == 0));

            stall.Cancel();
            connectionA.Dispose();
            connectionB.Dispose();

            ControlAuthResult resultA = await outcomeA.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(resultA.Completed, resultA.Rejection);
            Assert.Equal(0, registry.ActiveSessionCount);
            Assert.Equal(0, pendingLimiter.GlobalInUse);

            try
            {
                await Task.WhenAll(clientA, clientB).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
            }

            Assert.True(await WaitUntilAsync(() => host.ActiveConnections == 0));
            Assert.True(await WaitUntilAsync(() => host.AdmittedConnections == 0));
        }
    }

    // ═══════════════════════════ 数值与 DoD 负例 ═══════════════════════════

    /// <summary>
    /// 失败随机延时在配置区间内（默认 300–800 ms；这里缩放到 200–400 ms 验证边界与耗时）。
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Failed_Proof_Delay_Is_Bounded_By_The_Configured_Range()
    {
        ControlAuthOptions options = FastOptions with
        {
            FailureDelayMin = TimeSpan.FromMilliseconds(200),
            FailureDelayMax = TimeSpan.FromMilliseconds(400),
        };

        AuthClientProbe probe = new() { AccessKey = AttackKey };
        AuthScenario scenario = await RunScenarioAsync(
            (connection, p, ct) => p.SpeakAuthAsync(connection, ct),
            StubApprovalGate.Approve(),
            options: options,
            probe: probe);

        Assert.False(scenario.Result.Completed);
        Assert.Equal(ControlAuthSession.RejectProofMismatch, scenario.Result.Rejection);
        Assert.True(
            scenario.Elapsed >= TimeSpan.FromMilliseconds(150),
            $"耗时 {scenario.Elapsed}——低于延时下限，延时没有生效。");
        Assert.True(
            scenario.Elapsed < TimeSpan.FromSeconds(2),
            $"耗时 {scenario.Elapsed}——远高于延时上限（400 ms），区间没被遵守。");
    }

    /// <summary>DoD 负例：会话登记表的公开面上没有任何注册入口，也没有 token 读取面。</summary>
    [Fact]
    public void SessionRegistry_Public_Surface_Exposes_No_Registration_Or_Token_Access()
    {
        MethodInfo[] methods = typeof(SessionRegistry).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

        Assert.DoesNotContain(methods, m => m.Name is "Register" or "Unregister" or "TryGetSessionToken");

        PropertyInfo[] summaryProperties = typeof(ControlSessionSummary).GetProperties();
        Assert.DoesNotContain(
            summaryProperties,
            p => p.Name.Contains("Token", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Proof", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Key", StringComparison.OrdinalIgnoreCase));

        Type? registration = typeof(SessionRegistry).GetNestedType(
            "SessionRegistration", BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(registration);
        Assert.False(registration!.IsPublic, "注销句柄不应出现在公开面。");
    }

    /// <summary>
    /// DoD 负例：认证链上的每一帧恰好只有白名单字段——多一个字段就是给对端扩口子，
    /// 必须把这里改红才可能发生（序列化是唯一出处）。
    /// </summary>
    [Fact]
    public void Auth_Frames_Carry_Exactly_The_Allowlisted_Wire_Fields()
    {
        byte[] nonce = new byte[AuthProtocol.NonceByteLength];
        byte[] proof = new byte[AuthProtocol.ProofByteLength];
        byte[] token = new byte[AuthProtocol.SessionTokenByteLength];

        AssertJsonKeySet(
            new AuthChallengeFrame(Guid.NewGuid(), Guid.NewGuid(), nonce, nonce, 15_000).Serialize(),
            "type", "protocol", "sessionId", "serverDeviceId",
            "serverNonce", "certSha256", "expiresInMs");

        AssertJsonKeySet(
            new AuthResponseFrame(
                Guid.NewGuid(), "client", nonce, SessionPermission.ViewOnly, proof).Serialize(),
            "type", "clientDeviceId", "clientName",
            "clientNonce", "requestedPermission", "clientProof");

        AssertJsonKeySet(
            new AuthSuccessFrame(SessionPermission.ViewOnly, proof, token, 15_000).Serialize(),
            "type", "grantedPermission", "serverProof", "sessionToken", "videoAttachExpiresInMs");

        AssertJsonKeySet(ApprovalPendingFrame.Serialize(), "type");
        AssertJsonKeySet(AuthenticationFailedFrame.Serialize(), "type");
    }

    // ═══════════════════════════ 场景夹具 ═══════════════════════════

    /// <summary>
    /// 起一个真实 Host（pre-auth → auth 全链），客户端按脚本说话。
    /// </summary>
    /// <param name="clientScript">客户端脚本（真实回环 TLS 连接 + 探针 + 静默令牌）。</param>
    /// <param name="gate">审批面替身。</param>
    /// <param name="midflight">可选：客户端到达终帧后、断开之前执行的验证（成功路径用）。</param>
    /// <param name="options">认证选项（默认 <see cref="FastOptions"/>）。</param>
    /// <param name="limiter">失败限流器（默认新实例）。</param>
    /// <param name="pendingLimiter">待批配额（默认全局 3 / 每源 1）。</param>
    /// <param name="registry">会话登记表（默认新实例）。</param>
    /// <param name="secretStore">密钥来源（默认固定 GoodKey）。</param>
    /// <param name="probe">客户端探针（默认新建；篡改类用例注入预配置的探针）。</param>
    private async Task<AuthScenario> RunScenarioAsync(
        Func<TlsConnection, AuthClientProbe, CancellationToken, Task> clientScript,
        StubApprovalGate gate,
        Func<TlsConnection, AuthClientProbe, Task>? midflight = null,
        ControlAuthOptions? options = null,
        FailedAuthLimiter? limiter = null,
        ConnectionAdmissionLimiter? pendingLimiter = null,
        SessionRegistry? registry = null,
        IAccessSecretStore? secretStore = null,
        AuthClientProbe? probe = null)
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        int port = GetFreePort();

        SessionRegistry effectiveRegistry = registry ?? new SessionRegistry();
        FailedAuthLimiter effectiveLimiter = limiter ?? new FailedAuthLimiter();
        AuthClientProbe effectiveProbe = probe ?? new AuthClientProbe();

        ControlAuthContext context = NewContext(
            gate, effectiveRegistry, pendingLimiter, effectiveLimiter, options, secretStore);

        TaskCompletionSource<(ControlAuthResult Result, TimeSpan Elapsed, string? Fault)> outcome =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        ControlAuthSession?[] sessionBox = new ControlAuthSession?[1];

        TransportHost host = new(
            new[] { IPAddress.Loopback },
            new StubSubnetPolicy(allow: true),
            certificate,
            async (connection, cancellationToken) =>
            {
                ControlPreAuthSession preAuth = new();
                ControlPreAuthResult pre = await preAuth.RunAsync(
                    connection, BuildAuthTimeouts(), cancellationToken);
                if (!pre.Completed || pre.Handoff is null)
                {
                    outcome.TrySetResult((null!, TimeSpan.Zero, $"preauth:{pre.Rejection}"));
                    return;
                }

                ControlAuthSession session = pre.Handoff.BeginAuthentication(context, BuildAuthTimeouts());
                sessionBox[0] = session;

                Stopwatch clock = Stopwatch.StartNew();
                try
                {
                    ControlAuthResult result = await session.RunAsync(cancellationToken);
                    clock.Stop();
                    outcome.TrySetResult((result, clock.Elapsed, null));
                }
                catch (Exception ex)
                {
                    clock.Stop();
                    outcome.TrySetResult((null!, clock.Elapsed, $"{ex.GetType().Name}: {ex.Message}"));
                }
            },
            new TransportHostOptions { Port = port, Timeouts = BuildAuthTimeouts() });

        await using (host)
        {
            TransportHostStartResult start = host.Start();
            Assert.True(start.IsListening);

            TlsConnection connection = await ConnectAsync(certificate, port);

            using CancellationTokenSource stall = new();
            Task clientTask = Task.Run(() => clientScript(connection, effectiveProbe, stall.Token));

            if (midflight is not null)
            {
                await midflight(connection, effectiveProbe);
            }
            else
            {
                Task finished = await Task.WhenAny(outcome.Task, Task.Delay(TimeSpan.FromSeconds(20)));
                Assert.True(finished == outcome.Task, "服务端在 20 秒内没有给出结局。");
            }

            // 先给客户端脚本一个自然收场的机会，再拆连接。
            // 失败场景里「服务端写完终帧并收线」与「夹具拆连接」曾构成竞速：直接 cancel/dispose
            // 会把客户端从挂起的读里打断（OCE 不是脚本的预期异常）、并把已达内核缓冲的终帧一起丢掉——
            // 表现为偶发的「收不到 generic 失败帧 / approval_pending」。服务端收线后正文先于 FIN 到达，
            // 读类脚本会在毫秒级自行结束；静止类脚本（Delay）由下面的 cancel 兜底。
            await Task.WhenAny(clientTask, Task.Delay(TimeSpan.FromSeconds(3)));

            stall.Cancel();
            connection.Dispose();

            if (effectiveProbe.PendingWatch is { } pendingWatch)
            {
                try
                {
                    await pendingWatch.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception)
                {
                    // 挂起的读随连接释放收场（IOException/ObjectDisposed）——断言已在 midflight 做完。
                }
            }

            (ControlAuthResult result, TimeSpan elapsed, string? fault) =
                await outcome.Task.WaitAsync(TimeSpan.FromSeconds(15));

            try
            {
                await clientTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                effectiveProbe.ClientFault = ex;

                // 客户端脚本里的断言失败必须冒出来，不能被「客户端随服务端关线收场」吞掉。
                if (ex is Xunit.Sdk.XunitException)
                {
                    throw;
                }
            }

            Assert.True(await WaitUntilAsync(() => host.ActiveConnections == 0));
            Assert.True(await WaitUntilAsync(() => host.AdmittedConnections == 0));

            Assert.True(fault is null, fault);
            ControlAuthSession session = sessionBox[0]!;
            return new AuthScenario(
                result, elapsed, fault, session, effectiveRegistry, effectiveLimiter, gate, effectiveProbe);
        }
    }

    private static ControlAuthContext NewContext(
        ILocalApprovalGate gate,
        SessionRegistry? registry = null,
        ConnectionAdmissionLimiter? pendingLimiter = null,
        FailedAuthLimiter? limiter = null,
        ControlAuthOptions? options = null,
        IAccessSecretStore? secretStore = null) => new()
        {
            ServerDeviceId = ServerDeviceId,
            AccessSecretStore = secretStore ?? new FixedAccessSecretStore(GoodKey),
            FailedAuthLimiter = limiter ?? new FailedAuthLimiter(),
            PendingApprovalLimiter = pendingLimiter ?? new ConnectionAdmissionLimiter(3, 1),
            ApprovalGate = gate,
            SessionRegistry = registry ?? new SessionRegistry(),
            Options = options ?? FastOptions,
        };

    private static (ControlPreAuthHandoff Handoff, ConnectionSecurityContext Security) CreateDetachedHandoff()
    {
        ConnectionSecurityContext security = new(
            IPAddress.Loopback,
            IPAddress.Loopback,
            0,
            SslProtocols.None,
            new byte[CertificatePin.LengthBytes]);

        SslStream detached = new(new MemoryStream());
        return (new ControlPreAuthHandoff(detached, security), security);
    }

    private static TransportTimeouts BuildAuthTimeouts() => new(
        connectTimeout: TimeSpan.FromSeconds(3),
        handshakeTimeout: TimeSpan.FromSeconds(3),
        lengthPrefixTimeout: TimeSpan.FromSeconds(5),
        payloadTimeout: TimeSpan.FromSeconds(5),
        helloTimeout: TimeSpan.FromSeconds(2),
        preAuthEnvelopeTimeout: TimeSpan.FromSeconds(10));

    private static async Task<TlsConnection> ConnectAsync(X509Certificate2 certificate, int port)
    {
        bool created = ConnectionTarget.TryCreate(
            ServerDeviceId,
            IPAddress.Loopback,
            port,
            TestCertificateFactory.Fingerprint(certificate),
            out ConnectionTarget? target);
        Assert.True(created);

        return await new TlsClientConnector().ConnectAsync(target!);
    }

    private static async Task<byte[]> ReadFramePayloadAsync(Stream stream, CancellationToken cancellationToken)
    {
        FrameReader reader = new(stream);
        return await reader.ReadFrameAsync(
            TransportConstants.MaxPreAuthMessageBytes,
            FrameDeadline,
            FrameDeadline,
            cancellationToken);
    }

    private static void AssertJsonKeySet(byte[] utf8, params string[] expected)
    {
        using JsonDocument document = JsonDocument.Parse(utf8);
        string[] actual = document.RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            expected.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            actual);
    }

    private static TaskCompletionSource<ControlAuthResult> NewOutcome() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static int GetFreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 400; i++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }

    // ═══════════════════════════ 测试替身 ═══════════════════════════

    private sealed record AuthScenario(
        ControlAuthResult Result,
        TimeSpan Elapsed,
        string? Fault,
        ControlAuthSession Session,
        SessionRegistry Registry,
        FailedAuthLimiter Limiter,
        StubApprovalGate Gate,
        AuthClientProbe Probe);

    /// <summary>
    /// 客户端探针：独立算出 proof / 验证 serverProof / 记录收到的帧序列。
    /// </summary>
    /// <remarks>
    /// 正常值可用可篡改（<see cref="SignPermission"/> vs <see cref="SendPermission"/>、
    /// <see cref="SignedCertSha256"/>）——篡改类用例靠这些旋钮构造「签名内容 ≠ 报文内容」。
    /// </remarks>
    private sealed class AuthClientProbe
    {
        /// <summary>本客户端用来算 proof 的密钥。</summary>
        public byte[] AccessKey { get; set; } = GoodKey;

        /// <summary>签名时写进 transcript 的权限。</summary>
        public SessionPermission SignPermission { get; set; } = SessionPermission.ViewOnly;

        /// <summary>报文里实际声称的权限（正常时应等于 <see cref="SignPermission"/>）。</summary>
        public SessionPermission SendPermission { get; set; } = SessionPermission.ViewOnly;

        /// <summary>签名时用的证书指纹；<see langword="null"/> = 用 challenge 里的（正常路径）。</summary>
        public byte[]? SignedCertSha256 { get; set; }

        public Guid ClientDeviceId { get; set; } = TestClientDeviceId;

        public byte[] ClientNonce { get; } = RandomNumberGenerator.GetBytes(AuthProtocol.NonceByteLength);

        public TaskCompletionSource TerminalSeen { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AuthChallengeFrame? Challenge { get; private set; }

        public AuthSuccessFrame? Success { get; private set; }

        /// <summary>客户端按 grant transcript 重算 serverProof 的验证结果。</summary>
        public bool? ServerProofValid { get; private set; }

        public bool SawApprovalPending { get; set; }

        public bool SawGenericFailure { get; set; }

        public List<string> Frames { get; } = new();

        /// <summary>「保持连接」探针挂起的那笔读（harness 在断开后负责收场）。</summary>
        public Task<int>? PendingWatch { get; set; }

        /// <summary>客户端脚本的异常（非断言类；断言类会被 harness 直接抛出）。</summary>
        public Exception? ClientFault { get; set; }

        /// <summary>标准客户端脚本：hello → challenge → response → （pending）→ 终帧。</summary>
        public async Task SpeakAuthAsync(TlsConnection connection, CancellationToken cancellationToken)
        {
            try
            {
                await FrameWriter.WriteHelloAsync(connection.Stream, FrameDeadline, cancellationToken);
                await ReadChallengeAsync(connection.Stream, cancellationToken);
                await SendResponseAsync(connection.Stream, cancellationToken);

                for (int i = 0; i < 3; i++)
                {
                    byte[] payload = await ReadFramePayloadAsync(connection.Stream, cancellationToken);

                    if (ApprovalPendingFrame.TryParse(payload, out _))
                    {
                        SawApprovalPending = true;
                        Frames.Add("approval_pending");
                        continue;
                    }

                    if (AuthSuccessFrame.TryParse(payload, out AuthSuccessFrame? success, out _)
                        && success is not null)
                    {
                        Success = success;
                        ServerProofValid = VerifyServerProof(success);
                        Frames.Add("auth_success");
                        break;
                    }

                    if (AuthenticationFailedFrame.TryParse(payload, out _))
                    {
                        SawGenericFailure = true;
                        Frames.Add("authentication_failed");
                        break;
                    }

                    Frames.Add("unexpected-frame");
                    break;
                }
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException)
            {
                Frames.Add("disconnected");
            }
            finally
            {
                TerminalSeen.TrySetResult();
            }
        }

        public async Task<AuthChallengeFrame> ReadChallengeAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] payload = await ReadFramePayloadAsync(stream, cancellationToken);
            bool parsed = AuthChallengeFrame.TryParse(payload, out AuthChallengeFrame? challenge, out string? rejection);
            Assert.True(parsed, $"challenge 解析失败：{rejection}");
            RecordChallenge(challenge!, "auth_challenge");
            return challenge!;
        }

        public void RecordChallenge(AuthChallengeFrame challenge, string label)
        {
            Challenge = challenge;
            Frames.Add(label);
        }

        public async Task SendResponseAsync(Stream stream, CancellationToken cancellationToken)
        {
            AuthChallengeFrame challenge = Challenge
                ?? throw new InvalidOperationException("先读 challenge 再发 response。");

            byte[] certificateSha256 = SignedCertSha256 ?? challenge.CertificateSha256.ToArray();
            byte[] transcript = AuthTranscriptBuilder.BuildClientTranscript(
                challenge.SessionId,
                challenge.ServerDeviceId,
                ClientDeviceId,
                challenge.ServerNonce.Span,
                ClientNonce,
                certificateSha256,
                SignPermission);
            byte[] proof = AuthTranscriptBuilder.ComputeClientProof(AccessKey, transcript);

            AuthResponseFrame response = new(
                ClientDeviceId, TestClientName, ClientNonce, SendPermission, proof);

            await FrameWriter.WriteFrameAsync(
                stream,
                response.Serialize(),
                TransportConstants.MaxPreAuthMessageBytes,
                FrameDeadline,
                cancellationToken);
        }

        private bool VerifyServerProof(AuthSuccessFrame success)
        {
            AuthChallengeFrame challenge = Challenge
                ?? throw new InvalidOperationException("没有 challenge 就无法验证 serverProof。");

            // 客户端语义（规格 04 §9）：用自己那份 transcript + 收到的 grantedPermission 重建 grant 档。
            byte[] transcript = AuthTranscriptBuilder.BuildClientTranscript(
                challenge.SessionId,
                challenge.ServerDeviceId,
                ClientDeviceId,
                challenge.ServerNonce.Span,
                ClientNonce,
                challenge.CertificateSha256.Span,
                SignPermission);
            byte[] grant = AuthTranscriptBuilder.BuildGrantTranscript(
                transcript, success.GrantedPermission);
            byte[] expected = AuthTranscriptBuilder.ComputeServerProof(AccessKey, grant);

            return CryptographicOperations.FixedTimeEquals(expected, success.ServerProof.Span);
        }
    }

    /// <summary>审批面替身：可编程行为 + 记录请求（评审 #37/#38 的显式终态面）。</summary>
    private sealed class StubApprovalGate : ILocalApprovalGate
    {
        private readonly Func<LocalApprovalRequest, CancellationToken, ValueTask<LocalApprovalDecision>> _handler;

        public StubApprovalGate(
            Func<LocalApprovalRequest, CancellationToken, ValueTask<LocalApprovalDecision>> handler)
        {
            _handler = handler;
        }

        public LocalApprovalRequest? LastRequest { get; private set; }

        public int RequestCount { get; private set; }

        public TaskCompletionSource<LocalApprovalRequest> FirstRequest { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<LocalApprovalDecision> RequestApprovalAsync(
            LocalApprovalRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            RequestCount++;
            FirstRequest.TrySetResult(request);
            return _handler(request, cancellationToken);
        }

        public static StubApprovalGate Approve(SessionPermission? granted = null) =>
            new((request, _) => ValueTask.FromResult(new LocalApprovalDecision(
                request.RequestId,
                LocalApprovalOutcome.Approved,
                granted ?? request.RequestedPermission)));

        public static StubApprovalGate WithOutcome(LocalApprovalOutcome outcome) =>
            new((request, _) => ValueTask.FromResult(
                new LocalApprovalDecision(request.RequestId, outcome)));

        public static StubApprovalGate WrongRequestId() =>
            new((request, _) => ValueTask.FromResult(new LocalApprovalDecision(
                Guid.NewGuid(), LocalApprovalOutcome.Approved, request.RequestedPermission)));

        public static StubApprovalGate WaitOn(TaskCompletionSource<LocalApprovalDecision> decision) =>
            new(async (_, cancellationToken) => await decision.Task.WaitAsync(cancellationToken));

        public static StubApprovalGate Raising(Exception exception) =>
            new((_, _) => throw exception);
    }

    /// <summary>固定密钥来源（每次返回新副本——接口契约要求）。</summary>
    private sealed class FixedAccessSecretStore : IAccessSecretStore
    {
        private readonly byte[] _key;

        public FixedAccessSecretStore(byte[] key)
        {
            _key = key;
        }

        public Task<AccessSecret> LoadOrCreateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccessSecret(_key.ToArray()));

        public Task<AccessSecret> RegenerateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("测试不轮换密钥。");
    }

    /// <summary>永远失败的密钥来源（fail-closed 用例）。</summary>
    private sealed class FailingAccessSecretStore : IAccessSecretStore
    {
        public Task<AccessSecret> LoadOrCreateAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("DPAPI 不可用（测试替身）。");

        public Task<AccessSecret> RegenerateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("测试不轮换密钥。");
    }

    private sealed class StubSubnetPolicy : ISubnetPolicy
    {
        private readonly bool _allow;

        public StubSubnetPolicy(bool allow)
        {
            _allow = allow;
        }

        public bool IsAllowedPeer(IPAddress localAddress, IPAddress remoteAddress) => _allow;
    }
}
