using System.Net;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

public sealed partial class ControlAuthSessionTests
{
    [Fact(Timeout = 120_000)]
    public async Task Missing_Accept_On_Real_Tls_Uses_Original_Machine_Deadline()
    {
        ScriptedDeadlineStore store = new(_ => Task.FromResult(new AccessSecret(GoodKey.ToArray())));
        AuthScenario scenario = await RunScenarioAsync(async (connection, p, ct) =>
        {
            await WriteHelloV2Async(connection.Stream, FrameDeadline, ct);
            await p.ReadChallengeAsync(connection.Stream, ct, sendAccept: false);
            byte[] terminal = await ReadFramePayloadAsync(connection.Stream, ct);
            Assert.True(AuthenticationFailedFrame.TryParse(terminal, out _));
            p.Frames.Add("authentication_failed");
            p.TerminalSeen.TrySetResult();
        }, StubApprovalGate.Approve(), secretStore: store,
            options: FastOptions with { MachineWindow = TimeSpan.FromMilliseconds(700) });

        Assert.False(scenario.Result.Completed);
        Assert.Equal(ControlAuthSession.RejectTimeout, scenario.Result.Rejection);
        Assert.InRange(scenario.Elapsed, TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { "auth_challenge", "display_catalog_offer", "authentication_failed" }, scenario.Probe.Frames);
        Assert.Equal(0, store.Calls);
        Assert.Equal(0, scenario.Gate.RequestCount);
        Assert.Equal(0, scenario.Registry.ActiveSessionCount);
        Assert.Equal(0, scenario.Limiter.CountRecentFailures(IPAddress.Loopback));
    }

    // 受控 IO，不是 TLS；每次只在指定边界推进单调时钟，timer 故意不触发。
    [Theory(Timeout = 120_000)]
    [InlineData("offer-write", 0, 0)]
    [InlineData("accept-read", 2, 0)]
    [InlineData("accept-parse-final", 2, 0)]
    [InlineData("cumulative-proof", 4, 1)]
    public async Task V2_Agreement_Shares_Machine_Budget_And_Checks_Final_Boundaries_With_Controlled_Io(
        string boundary, int expectedReads, int expectedKeyLoads)
    {
        ManualDeadlineClock manual = new();
        BoundaryObservationClock clock = new(manual);
        using CleanupIoSslStream stream = new();
        stream.OnWriteAsync = type =>
        {
            if (type == "display_catalog_offer")
            {
                if (boundary == "offer-write")
                    manual.Advance(FastOptions.MachineWindow, fireTimers: false);
                else if (boundary == "cumulative-proof")
                    manual.Advance(TimeSpan.FromSeconds(3), fireTimers: false);
            }
            return Task.CompletedTask;
        };
        stream.OnHandshakeReadCompleted = count =>
        {
            if (count != 2) return; // Accept payload 已读完，但调用方尚未做返回后检查。
            if (boundary == "accept-read")
                manual.Advance(FastOptions.MachineWindow, fireTimers: false);
            else if (boundary == "accept-parse-final")
                clock.AfterNextTimestamp = () => manual.Advance(FastOptions.MachineWindow, fireTimers: false);
            else if (boundary == "cumulative-proof")
                manual.Advance(TimeSpan.FromSeconds(1), fireTimers: false);
        };
        byte[] returnedKey = GoodKey.ToArray();
        ScriptedDeadlineStore store = new(_ =>
        {
            manual.Advance(TimeSpan.FromSeconds(1), fireTimers: false);
            return Task.FromResult(new AccessSecret(returnedKey));
        });
        StubApprovalGate gate = StubApprovalGate.WithOutcome(LocalApprovalOutcome.Denied);
        FailedAuthLimiter limiter = new();
        limiter.RecordFailure(IPAddress.Loopback);
        ControlAuthContext context = NewContext(gate, secretStore: store, limiter: limiter) with { TimeProvider = clock };
        ConnectionSecurityContext security = new(IPAddress.Loopback, IPAddress.Loopback, 0,
            System.Security.Authentication.SslProtocols.None, new byte[CertificatePin.LengthBytes]);
        ControlAuthSession session = new(stream, security, context, BuildAuthTimeouts());
        ControlAuthResult result = await session.RunAsync(CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Equal(ControlAuthSession.RejectTimeout, result.Rejection);
        Assert.Equal(new[] { "auth_challenge", "display_catalog_offer", "authentication_failed" }, stream.AcceptedFrames);
        Assert.Equal(expectedReads, stream.HandshakeReadCalls);
        Assert.Equal(expectedKeyLoads, store.Calls);
        Assert.Equal(0, stream.PendingReadCalls);
        Assert.Equal(0, gate.RequestCount);
        Assert.Equal(0, context.SessionRegistry.ActiveSessionCount);
        Assert.Equal(1, limiter.CountRecentFailures(IPAddress.Loopback));
        if (expectedKeyLoads == 1) Assert.All(returnedKey, value => Assert.Equal((byte)0, value));
        await session.JoinOwnedOperationsAsync().WaitAsync(FrameDeadline);
    }

    // 真实 TLS + TransportHost；只在明确的本机依赖边界推进单调时间，不依赖 timer 恰好调度。
    [Theory(Timeout = 120_000)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Machine_Expired_Key_Result_Never_Commits_Proof_Or_Limiter(
        bool wrongProof, bool expireAfterKeyCheck)
    {
        ManualDeadlineClock manual = new();
        BoundaryObservationClock clock = new(manual);
        FailedAuthLimiter limiter = new();
        limiter.RecordFailure(IPAddress.Loopback); // 检测错误清空与错误递增两种副作用。
        byte[] returnedKey = GoodKey.ToArray();
        ScriptedDeadlineStore store = new(_ =>
        {
            if (expireAfterKeyCheck)
            {
                // 密钥后检查取到及时值；紧接着跨过截止，只有 MAC 后 final check 能挡住。
                clock.AfterNextTimestamp = () => manual.Advance(FastOptions.MachineWindow, fireTimers: false);
            }
            else
            {
                manual.Advance(FastOptions.MachineWindow, fireTimers: false);
            }
            return Task.FromResult(new AccessSecret(returnedKey));
        });
        AuthClientProbe probe = new() { AccessKey = wrongProof ? AttackKey : GoodKey };
        StubApprovalGate gate = StubApprovalGate.Approve();
        AuthScenario scenario = await RunScenarioAsync(
            (c, p, ct) => p.SpeakAuthAsync(c, ct), gate,
            secretStore: store, probe: probe, limiter: limiter, timeProvider: clock,
            midflight: WaitForTerminalAsync);

        Assert.Equal(1, store.Calls);
        Assert.False(scenario.Result.Completed);
        Assert.Equal(ControlAuthSession.RejectTimeout, scenario.Result.Rejection);
        Assert.True(probe.SawGenericFailure);
        Assert.Null(probe.Success);
        Assert.False(probe.SawApprovalPending);
        Assert.Equal(0, gate.RequestCount);
        Assert.Equal(1, limiter.CountRecentFailures(IPAddress.Loopback));
        Assert.All(returnedKey, b => Assert.Equal((byte)0, b));
        Assert.Equal(0, scenario.Registry.ActiveSessionCount);
    }

    [Fact(Timeout = 120_000)]
    public async Task Machine_Deadline_Cancels_Cooperative_Key_Load_On_Real_Tls()
    {
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedDeadlineStore store = new(async ct =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("无限等待不得自然完成。");
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
        });
        StubApprovalGate gate = StubApprovalGate.Approve();
        AuthScenario scenario = await RunScenarioAsync(
            (c, p, ct) => p.SpeakAuthAsync(c, ct), gate,
            options: FastOptions with { MachineWindow = TimeSpan.FromMilliseconds(700) },
            secretStore: store, midflight: WaitForTerminalAsync);

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, store.Calls);
        Assert.Equal(ControlAuthSession.RejectTimeout, scenario.Result.Rejection);
        Assert.True(scenario.Probe.SawGenericFailure);
        Assert.False(scenario.Result.Completed);
        Assert.Equal(0, gate.RequestCount);
        Assert.Equal(0, scenario.Limiter.CountRecentFailures(IPAddress.Loopback));
    }

    [Fact(Timeout = 120_000)]
    public async Task Machine_Noncooperative_Key_Task_Returns_Timeout_Then_Clears_Late_Success()
    {
        TaskCompletionSource<AccessSecret> late = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedDeadlineStore store = new(_ => late.Task);
        AuthScenario scenario = await RunScenarioAsync(
            (c, p, ct) => p.SpeakAuthAsync(c, ct), StubApprovalGate.Approve(),
            options: FastOptions with { MachineWindow = TimeSpan.FromMilliseconds(700) },
            secretStore: store, midflight: WaitForTerminalAsync);

        Assert.Equal(1, store.Calls);
        Assert.False(late.Task.IsCompleted);
        Assert.Equal(ControlAuthSession.RejectTimeout, scenario.Result.Rejection);
        Assert.True(scenario.Probe.SawGenericFailure);
        byte[] bytes = GoodKey.ToArray();
        late.SetResult(new AccessSecret(bytes));
        // 同一个 context 的下一次准入完成证明迟到 owner 已清零且归还名额，不用靠睡眠猜。
        AccessSecret next = await scenario.Session.Context.SecretLoader.LoadAsync(
            new FixedAccessSecretStore(GoodKey), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(bytes, b => Assert.Equal((byte)0, b));
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(next.AccessKeyBytes);
        Assert.Equal(0, scenario.Registry.ActiveSessionCount);
        Assert.Equal(0, scenario.Gate.RequestCount);
    }

    [Fact(Timeout = 120_000)]
    public async Task Approval_Synchronous_Prefix_Is_Inside_Real_Time_Window()
    {
        StubApprovalGate gate = new((request, _) =>
        {
            Thread.Sleep(1800); // 旧反例同类：违反快速返回契约，但迟到成功仍必须拒绝。
            return ValueTask.FromResult(new LocalApprovalDecision(
                request.RequestId, LocalApprovalOutcome.Approved, request.RequestedPermission));
        });
        AuthScenario scenario = await RunScenarioAsync(
            (c, p, ct) => p.SpeakAuthAsync(c, ct), gate,
            options: FastOptions with { ApprovalWindow = TimeSpan.FromMilliseconds(600) },
            midflight: WaitForTerminalAsync);

        Assert.False(scenario.Result.Completed);
        Assert.Equal(ControlAuthSession.RejectApprovalTimeout, scenario.Result.Rejection);
        Assert.True(scenario.Probe.SawApprovalPending);
        Assert.True(scenario.Probe.SawGenericFailure);
        Assert.Null(scenario.Probe.Success);
        Assert.Equal(0, scenario.Registry.ActiveSessionCount);
    }

    [Theory(Timeout = 120_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Approval_Acceptance_Uses_Monotonic_Time_Not_Timer_Or_Click(bool expireAtFinalCheck)
    {
        ManualDeadlineClock manual = new();
        BoundaryObservationClock clock = new(manual);
        StubApprovalGate gate = new((request, _) =>
        {
            if (expireAtFinalCheck)
            {
                // 第一次终态复核还及时，决定校验与最终接受之间跨过截止。
                clock.AfterNextTimestamp = () => manual.Advance(FastOptions.ApprovalWindow, fireTimers: false);
            }
            else
            {
                manual.Advance(FastOptions.ApprovalWindow, fireTimers: false);
            }
            return ValueTask.FromResult(new LocalApprovalDecision(
                request.RequestId, LocalApprovalOutcome.Approved, request.RequestedPermission));
        });
        AuthScenario scenario = await RunScenarioAsync(
            (c, p, ct) => p.SpeakAuthAsync(c, ct), gate,
            timeProvider: clock, midflight: WaitForTerminalAsync);

        Assert.Equal(ControlAuthSession.RejectApprovalTimeout, scenario.Result.Rejection);
        Assert.False(scenario.Result.Completed);
        Assert.True(scenario.Probe.SawGenericFailure);
        Assert.Null(scenario.Probe.Success);
        Assert.Equal(0, scenario.Registry.ActiveSessionCount);
    }

    [Theory(Timeout = 120_000)]
    [InlineData(-365)]
    [InlineData(365)]
    public async Task Approval_Utc_Jump_Does_Not_Expire_A_Timely_Approval(int days)
    {
        ManualDeadlineClock clock = new();
        StubApprovalGate gate = new((request, _) =>
        {
            clock.AdvanceUtc(TimeSpan.FromDays(days));
            clock.Advance(FastOptions.ApprovalWindow - TimeSpan.FromTicks(1), fireTimers: false);
            return ValueTask.FromResult(new LocalApprovalDecision(
                request.RequestId, LocalApprovalOutcome.Approved, request.RequestedPermission));
        });
        AuthScenario scenario = await RunScenarioAsync(
            (c, p, ct) => p.SpeakAuthAsync(c, ct), gate,
            timeProvider: clock, midflight: WaitForTerminalAsync);

        Assert.True(scenario.Result.Completed, scenario.Result.Rejection);
        Assert.True(scenario.Probe.ServerProofValid);
    }

    [Fact(Timeout = 120_000)]
    public async Task Approval_Cancellation_Wins_Over_Expired_And_Ready_Decision()
    {
        ManualDeadlineClock clock = new();
        using CancellationTokenSource caller = new();
        SessionRegistry registry = new();
        AuthClientProbe probe = new();
        StubApprovalGate gate = new((request, _) =>
        {
            clock.Advance(FastOptions.ApprovalWindow, fireTimers: false);
            caller.Cancel();
            return ValueTask.FromResult(new LocalApprovalDecision(
                request.RequestId, LocalApprovalOutcome.Approved, request.RequestedPermission));
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunScenarioAsync(
            (c, p, ct) => p.SpeakAuthAsync(c, ct), gate,
            timeProvider: clock, authCancellation: caller.Token, registry: registry, probe: probe));

        Assert.Equal(1, gate.RequestCount);
        Assert.Null(probe.Success);
        Assert.Equal(0, registry.ActiveSessionCount);
    }

    [Fact(Timeout = 120_000)]
    public async Task Approval_Unexpected_Byte_Rejects_Before_A_Late_Decision()
    {
        TaskCompletionSource<LocalApprovalDecision> decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubApprovalGate gate = StubApprovalGate.WaitOn(decision);
        AuthScenario scenario = await RunScenarioAsync(async (connection, probe, ct) =>
        {
            await WriteHelloV2Async(connection.Stream, FrameDeadline, ct);
            await probe.ReadChallengeAsync(connection.Stream, ct);
            await probe.SendResponseAsync(connection.Stream, ct);
            byte[] pending = await ReadFramePayloadAsync(connection.Stream, ct);
            Assert.True(ApprovalPendingFrame.TryParse(pending, out _));
            await connection.Stream.WriteAsync(new byte[] { 0x42 }, ct);
            byte[] terminal = await ReadFramePayloadAsync(connection.Stream, ct);
            probe.SawGenericFailure = AuthenticationFailedFrame.TryParse(terminal, out _);
            LocalApprovalRequest request = await gate.FirstRequest.Task;
            decision.TrySetResult(new LocalApprovalDecision(
                request.RequestId, LocalApprovalOutcome.Approved, request.RequestedPermission));
            probe.TerminalSeen.TrySetResult();
        }, gate, midflight: WaitForTerminalAsync);

        Assert.Equal(ControlAuthSession.RejectUnexpectedData, scenario.Result.Rejection);
        Assert.True(scenario.Probe.SawGenericFailure);
        Assert.Null(scenario.Probe.Success);
        Assert.Equal(0, scenario.Registry.ActiveSessionCount);
        Assert.Equal(0, scenario.Session.Context.PendingApprovalLimiter.GlobalInUse);
    }

    [Fact]
    public void Failure_Jitter_Rejects_Unsupported_Integer_Millisecond_Range()
    {
        (ControlPreAuthHandoff handoff, _) = CreateDetachedHandoff();
        Assert.Throws<ArgumentOutOfRangeException>(() => handoff.BeginAuthentication(
            NewContext(StubApprovalGate.Approve(), options: FastOptions with
            {
                FailureDelayMax = TimeSpan.FromMilliseconds(int.MaxValue),
            }), BuildAuthTimeouts()));
    }

    private static async Task WaitForTerminalAsync(TlsConnection _, AuthClientProbe probe) =>
        await probe.TerminalSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));

    private sealed class ScriptedDeadlineStore(Func<CancellationToken, Task<AccessSecret>> handler) : IAccessSecretStore
    {
        public int Calls { get; private set; }
        public Task<AccessSecret> LoadOrCreateAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return handler(cancellationToken);
        }
        public Task<AccessSecret> RegenerateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class BoundaryObservationClock(ManualDeadlineClock inner) : TimeProvider
    {
        private Action? _afterNextTimestamp;
        public Action? AfterNextTimestamp { set => Interlocked.Exchange(ref _afterNextTimestamp, value); }
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
        public override long GetTimestamp()
        {
            long value = inner.GetTimestamp();
            Interlocked.Exchange(ref _afterNextTimestamp, null)?.Invoke();
            return value;
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            inner.CreateTimer(callback, state, dueTime, period);
    }
}
