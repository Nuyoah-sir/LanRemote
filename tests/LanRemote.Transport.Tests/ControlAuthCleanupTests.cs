using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Text.Json;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

public sealed partial class ControlAuthSessionTests
{
    // 真实回环 TLS：store 只取消自己的任务，调用方和机器窗口都没有取消。
    [Fact(Timeout = 120_000)]
    public async Task Key_Store_Self_Cancellation_Is_Key_Unavailable_On_Real_Tls()
    {
        ManualDeadlineClock clock = new();
        using CancellationTokenSource caller = new();
        using CancellationTokenSource storeCancellation = new();
        CancellationToken machineToken = default;
        ScriptedDeadlineStore store = new(token =>
        {
            machineToken = token;
            Assert.False(token.IsCancellationRequested);
            Assert.False(caller.IsCancellationRequested);
            storeCancellation.Cancel();
            return Task.FromCanceled<AccessSecret>(storeCancellation.Token);
        });
        StubApprovalGate gate = StubApprovalGate.Approve();

        AuthScenario scenario = await RunScenarioAsync(
            (connection, probe, ct) => probe.SpeakAuthAsync(connection, ct), gate,
            secretStore: store, timeProvider: clock, authCancellation: caller.Token,
            midflight: WaitForTerminalAsync);

        Assert.Equal(1, store.Calls);
        Assert.True(storeCancellation.IsCancellationRequested);
        Assert.True(machineToken.CanBeCanceled);
        Assert.False(machineToken.IsCancellationRequested);
        Assert.False(caller.IsCancellationRequested);
        Assert.Equal(0L, clock.GetTimestamp());
        Assert.False(scenario.Result.Completed);
        Assert.Equal(ControlAuthSession.RejectKeyUnavailable, scenario.Result.Rejection);
        Assert.Equal(ControlSessionState.Closed, scenario.Session.State);
        Assert.Equal(new[] { "auth_challenge", "authentication_failed" }, scenario.Probe.Frames);
        Assert.True(scenario.Probe.SawGenericFailure);
        Assert.Null(scenario.Probe.Success);
        Assert.Equal(0, gate.RequestCount);
        Assert.Equal(0, scenario.Registry.ActiveSessionCount);
        Assert.Equal(0, scenario.Session.Context.PendingApprovalLimiter.GlobalInUse);
        Assert.Equal(0, scenario.Limiter.CountRecentFailures(IPAddress.Loopback));
    }

    // 替身 IO 状态机单测，不是真实 TLS：取消后仍挂起同一笔读，排除读取消抢先成为终态。
    [Fact(Timeout = 120_000)]
    public async Task Gate_Fault_After_First_Stop_Cancellation_Check_Still_Propagates_Caller_Cancellation()
    {
        ManualDeadlineClock manual = new();
        BoundaryObservationClock clock = new(manual);
        using CancellationTokenSource caller = new();
        using CleanupIoSslStream stream = new();
        Task<LocalApprovalDecision> gateFault = Task.FromException<LocalApprovalDecision>(
            new InvalidOperationException("受控 gate 故障。"));
        StubApprovalGate gate = new((_, _) =>
        {
            Assert.False(caller.IsCancellationRequested);
            Assert.Equal(1, stream.PendingReadCalls);
            Assert.False(stream.PendingRead.IsCompleted);
            // gate 返回后，首次 ApprovalStopAsync 先检查 caller，再读取 IsExpired 的时间戳。
            clock.AfterNextTimestamp = caller.Cancel;
            return new ValueTask<LocalApprovalDecision>(gateFault);
        });
        ControlAuthContext context = NewContext(gate) with { TimeProvider = clock };
        ConnectionSecurityContext security = new(
            IPAddress.Loopback, IPAddress.Loopback, 0, SslProtocols.None,
            new byte[CertificatePin.LengthBytes]);
        ControlAuthSession session = new(stream, security, context, BuildAuthTimeouts());

        OperationCanceledException cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.RunAsync(caller.Token));

        Assert.Equal(caller.Token, cancelled.CancellationToken);
        Assert.True(caller.IsCancellationRequested);
        Assert.Equal(0L, manual.GetTimestamp());
        Assert.Equal(1, gate.RequestCount);
        Assert.True(gateFault.IsFaulted);
        Assert.False(stream.PendingRead.IsCompleted);
        Assert.Equal(new[] { "auth_challenge", "approval_pending" }, stream.WriteAttempts);
        Assert.DoesNotContain("auth_success", stream.AcceptedFrames);
        Assert.Equal(ControlSessionState.Closed, session.State);
        Assert.Equal(0, context.SessionRegistry.ActiveSessionCount);
        Assert.Equal(0, context.PendingApprovalLimiter.GlobalInUse);
    }

    // 判定器单测：输入任务在调用之前已经完成，不把它冒充为 TLS 上 EOF/字节/故障的竞速。
    // 优先级为 caller 取消 > 单调 deadline 到期 > 已完成客户端活动；到期 timer 故意不派发。
    [Theory]
    [InlineData(0, false, false, ControlAuthSession.RejectApprovalDisconnected)]
    [InlineData(1, false, false, ControlAuthSession.RejectUnexpectedData)]
    [InlineData(-1, false, false, ControlAuthSession.RejectApprovalDisconnected)]
    [InlineData(0, true, false, ControlAuthSession.RejectApprovalTimeout)]
    [InlineData(1, true, false, ControlAuthSession.RejectApprovalTimeout)]
    [InlineData(-1, true, false, ControlAuthSession.RejectApprovalTimeout)]
    [InlineData(0, false, true, null)]
    [InlineData(1, false, true, null)]
    [InlineData(-1, false, true, null)]
    [InlineData(0, true, true, null)]
    [InlineData(1, true, true, null)]
    [InlineData(-1, true, true, null)]
    public async Task Approval_Stop_Completed_Activity_Has_Deterministic_Priority(
        int activity, bool expired, bool callerCancelled, string? expected)
    {
        ManualDeadlineClock clock = new();
        using CancellationTokenSource caller = new();
        using AuthenticationDeadline deadline = new(clock, FastOptions.ApprovalWindow, caller.Token);
        Task<int> clientActivity = activity < 0
            ? Task.FromException<int>(new IOException("受控客户端读故障。"))
            : Task.FromResult(activity);
        if (expired)
        {
            clock.Advance(FastOptions.ApprovalWindow, fireTimers: false);
        }
        if (callerCancelled)
        {
            caller.Cancel();
        }

        try
        {
            Assert.True(clientActivity.IsCompleted);
            Assert.Equal(expired, deadline.IsExpired);
            Assert.Equal(callerCancelled, deadline.Token.IsCancellationRequested);
            if (callerCancelled)
            {
                OperationCanceledException cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => ControlAuthSession.ApprovalStopAsync(deadline, clientActivity, caller.Token));
                Assert.Equal(caller.Token, cancelled.CancellationToken);
            }
            else
            {
                Assert.Equal(expected, await ControlAuthSession.ApprovalStopAsync(
                    deadline, clientActivity, caller.Token));
            }
        }
        finally
        {
            // 高优先级分支无须消费活动任务；这里只清理测试拥有的输入，不验证生产清理观察器。
            if (clientActivity.IsFaulted)
            {
                _ = clientActivity.Exception;
            }
        }
    }

    // 真实回环 TLS：gate 直接交回不合作的任务，不用 WaitOn/WaitAsync(token) 包装。
    [Fact(Timeout = 120_000)]
    public async Task Noncooperative_Gate_Completes_Late_Without_Reversing_Real_Tls_Timeout()
    {
        ManualDeadlineClock clock = new();
        TaskCompletionSource<LocalApprovalDecision> late = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<CancellationToken> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubApprovalGate gate = new((_, token) =>
        {
            entered.TrySetResult(token);
            return new ValueTask<LocalApprovalDecision>(late.Task);
        });
        SessionRegistry registry = new();
        ConnectionAdmissionLimiter pending = new(3, 1);
        AuthClientProbe probe = new();

        try
        {
            AuthScenario scenario = await RunScenarioAsync(
                (connection, p, ct) => p.SpeakAuthAsync(connection, ct), gate,
                registry: registry, pendingLimiter: pending, probe: probe, timeProvider: clock,
                midflight: async (_, p) =>
                {
                    CancellationToken gateToken = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.False(gateToken.IsCancellationRequested);
                    Assert.False(late.Task.IsCompleted);
                    Assert.Equal(1, pending.GlobalInUse);
                    clock.Advance(FastOptions.ApprovalWindow);
                    await p.TerminalSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));

                    // 在夹具拆 Host 之前已有失败帧且租约归零；不是只检查拆除后的 registry。
                    Assert.True(gateToken.IsCancellationRequested);
                    Assert.False(late.Task.IsCompleted);
                    Assert.True(p.SawGenericFailure);
                    Assert.Null(p.Success);
                    Assert.DoesNotContain("auth_success", p.Frames);
                    Assert.Equal(0, pending.GlobalInUse);
                    Assert.Equal(0, registry.ActiveSessionCount);
                });

            Assert.False(late.Task.IsCompleted);
            Assert.False(scenario.Result.Completed);
            Assert.Equal(ControlAuthSession.RejectApprovalTimeout, scenario.Result.Rejection);
            Assert.Equal(ControlSessionState.Closed, scenario.Session.State);
            Assert.Equal(0, pending.GlobalInUse);
            Assert.Equal(new[] { "auth_challenge", "approval_pending", "authentication_failed" }, probe.Frames);

            LocalApprovalRequest request = Assert.IsType<LocalApprovalRequest>(gate.LastRequest);
            LocalApprovalDecision approved = new(
                request.RequestId, LocalApprovalOutcome.Approved, request.RequestedPermission);
            late.SetResult(approved);
            Assert.Same(approved, await late.Task.WaitAsync(TimeSpan.FromSeconds(5)));

            // 同步到 gate 提供的任务完成，不用 Sleep 猜调度；认证 RunAsync 此前已经返回失败。
            // 这不声称能直接观察私有 decision 包装任务的完成状态。
            Assert.False(scenario.Result.Completed);
            Assert.Equal(ControlAuthSession.RejectApprovalTimeout, scenario.Result.Rejection);
            Assert.Equal(ControlSessionState.Closed, scenario.Session.State);
            Assert.Null(probe.Success);
            Assert.DoesNotContain("auth_success", probe.Frames);
            Assert.Equal(0, registry.ActiveSessionCount);
            Assert.Equal(0, pending.GlobalInUse);
            Assert.Equal(1, gate.RequestCount);
        }
        finally
        {
            late.TrySetCanceled(); // 断言失败时也不留下永远挂起的测试任务。
        }
    }

    // 受控 SslStream 派生类跳过 TLS 握手/加密，只验证本地 IO 取消的状态机映射。
    [Theory(Timeout = 120_000)]
    [InlineData(2, ControlAuthSession.RejectApprovalPendingNotDelivered)]
    [InlineData(3, ControlAuthSession.RejectSuccessNotDelivered)]
    public async Task Local_Write_Cancellation_Fails_Closed_With_Controlled_Io(
        int cancelOnWrite, string expected)
    {
        using CancellationTokenSource caller = new();
        using CleanupIoSslStream stream = new(cancelOnWrite);
        StubApprovalGate gate = StubApprovalGate.Approve();
        ControlAuthContext context = NewContext(gate) with { TimeProvider = new ManualDeadlineClock() };
        ConnectionSecurityContext security = new(
            IPAddress.Loopback, IPAddress.Loopback, 0, SslProtocols.None,
            new byte[CertificatePin.LengthBytes]);
        ControlAuthSession session = new(stream, security, context, BuildAuthTimeouts());

        ControlAuthResult result = await session.RunAsync(caller.Token);

        Assert.False(caller.IsCancellationRequested);
        Assert.False(result.Completed);
        Assert.Equal(expected, result.Rejection);
        Assert.Equal(ControlSessionState.Closed, result.State);
        Assert.Equal(ControlSessionState.Closed, session.State);
        Assert.Equal(session.SessionId, result.SessionId);
        Assert.Equal(0, context.SessionRegistry.ActiveSessionCount);
        Assert.False(context.SessionRegistry.TryGetSessionToken(session.SessionId, out _));
        Assert.Equal(0, context.PendingApprovalLimiter.GlobalInUse);
        Assert.Equal(0, context.FailedAuthLimiter.CountRecentFailures(IPAddress.Loopback));
        Assert.Equal(cancelOnWrite == 2 ? 0 : 1, gate.RequestCount);
        Assert.Equal(2, stream.ResponseReadCalls); // response 的长度前缀与载荷，不是审批期一字节读。
        Assert.True(stream.ResponseFullyRead);
        Assert.Equal(cancelOnWrite == 2 ? 0 : 1, stream.PendingReadCalls);
        Assert.False(stream.PendingRead.IsCompleted);
        Assert.Equal(1, stream.InjectedWriteCancellations);
        Assert.Equal(cancelOnWrite + 1, stream.WriteAttempts.Count);
        Assert.Equal("authentication_failed", stream.AcceptedFrames.Last());
        Assert.DoesNotContain("auth_success", stream.AcceptedFrames);
        Assert.Equal(cancelOnWrite == 2
            ? new[] { "auth_challenge", "approval_pending", "authentication_failed" }
            : new[] { "auth_challenge", "approval_pending", "auth_success", "authentication_failed" },
            stream.WriteAttempts);

        // pending 写失败尚未启动活动读；success 写失败留下的同一笔读由替身 Dispose 以 EOF 收尾。
        // 生产代码未暴露私有包装读任务/异常观察通知：这里不伪造 pending-read fault 已被观察的断言，
        // 也不以测试自行 await 底层 Task 或 GC/UnobservedTaskException 来替代该缺失证据。
    }

    private sealed class CleanupIoSslStream : SslStream
    {
        private readonly int _cancelOnWrite;
        private readonly TaskCompletionSource<int> _pendingRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[]? _response;
        private int _responseOffset;

        public CleanupIoSslStream(int cancelOnWrite = 0) : base(new MemoryStream()) =>
            _cancelOnWrite = cancelOnWrite;

        public List<string> WriteAttempts { get; } = new();
        public List<string> AcceptedFrames { get; } = new();
        public int InjectedWriteCancellations { get; private set; }
        public int ResponseReadCalls { get; private set; }
        public int PendingReadCalls { get; private set; }
        public bool ResponseFullyRead => _response is not null && _responseOffset == _response.Length;
        public Task<int> PendingRead => _pendingRead.Task;

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(buffer.Length > TransportConstants.LengthPrefixBytes);
            ReadOnlyMemory<byte> payload = buffer[TransportConstants.LengthPrefixBytes..];
            Assert.Equal((uint)payload.Length, BinaryPrimitives.ReadUInt32BigEndian(buffer.Span));
            using JsonDocument document = JsonDocument.Parse(payload);
            string type = document.RootElement.GetProperty("type").GetString()!;
            WriteAttempts.Add(type);

            if (WriteAttempts.Count == 1)
            {
                Assert.Equal("auth_challenge", type);
                Assert.True(AuthChallengeFrame.TryParse(payload.Span, out AuthChallengeFrame? challenge, out _));
                byte[] nonce = new byte[AuthProtocol.NonceByteLength];
                byte[] transcript = AuthTranscriptBuilder.BuildClientTranscript(
                    challenge!.SessionId, challenge.ServerDeviceId, TestClientDeviceId,
                    challenge.ServerNonce.Span, nonce, challenge.CertificateSha256.Span,
                    SessionPermission.ViewOnly);
                byte[] proof = AuthTranscriptBuilder.ComputeClientProof(GoodKey, transcript);
                byte[] response = new AuthResponseFrame(
                    TestClientDeviceId, TestClientName, nonce, SessionPermission.ViewOnly, proof).Serialize();
                _response = new byte[TransportConstants.LengthPrefixBytes + response.Length];
                BinaryPrimitives.WriteUInt32BigEndian(_response, (uint)response.Length);
                response.CopyTo(_response.AsSpan(TransportConstants.LengthPrefixBytes));
            }

            // 只在精确的第二/三次写抛一次；随后 generic failure 帧不能再次触发注入。
            if (WriteAttempts.Count == _cancelOnWrite)
            {
                Assert.Equal(_cancelOnWrite == 2 ? "approval_pending" : "auth_success", type);
                InjectedWriteCancellations++;
                throw new OperationCanceledException("替身 IO 的本地写出取消，非调用方取消。");
            }

            AcceptedFrames.Add(type);
            return ValueTask.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.NotNull(_response);
            if (_responseOffset < _response!.Length)
            {
                int count = Math.Min(buffer.Length, _response.Length - _responseOffset);
                _response.AsMemory(_responseOffset, count).CopyTo(buffer);
                _responseOffset += count;
                ResponseReadCalls++;
                return ValueTask.FromResult(count);
            }

            // 必须先消费完整 response，才允许启动且仅启动一次审批期的单字节读。
            Assert.Equal(1, buffer.Length);
            PendingReadCalls++;
            Assert.Equal(1, PendingReadCalls);
            // 故意不注册取消，供边界竞态用例确保首次 Stop 后仍需消费 gate 故障并复核 caller。
            return new ValueTask<int>(_pendingRead.Task);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _pendingRead.TrySetResult(0);
            }
            base.Dispose(disposing);
        }
    }
}
