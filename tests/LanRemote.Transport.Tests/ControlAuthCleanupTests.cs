using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography;
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
    [Theory(Timeout = 120_000)]
    [InlineData("eof")]
    [InlineData("data")]
    [InlineData("cancelled")]
    [InlineData("io")]
    [InlineData("disposed")]
    [InlineData("unexpected")]
    public async Task Gate_Fault_After_First_Stop_Cancellation_Check_Still_Propagates_Caller_Cancellation(
        string readOutcome)
    {
        ManualDeadlineClock manual = new();
        BoundaryObservationClock clock = new(manual);
        using CancellationTokenSource caller = new();
        Exception? readFailure = readOutcome switch
        {
            "cancelled" => new OperationCanceledException(new CancellationToken(true)),
            "io" => new IOException("受控审批读 IO 故障。"),
            "disposed" => new ObjectDisposedException(nameof(CleanupIoSslStream)),
            "unexpected" => new InvalidOperationException("受控审批读意外故障。"),
            _ => null,
        };
        using CleanupIoSslStream stream = new()
        {
            ReadResultOnDispose = readOutcome == "data" ? 1 : 0,
            ReadExceptionOnDispose = readFailure,
        };
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

        // RunAsync 已抛出；join 必须观察原 ReadOneByteAsync 的包装任务，不能只观察替身底层 TCS。
        Task<int> approvalRead = ReadPendingApprovalTask(session);
        Assert.NotSame(stream.PendingRead, approvalRead);
        Assert.False(approvalRead.IsCompleted);
        Task join = session.JoinPendingReadAsync();
        try
        {
            Assert.False(join.IsCompleted);
            Assert.Equal(1, stream.PendingReadCalls);
            stream.Dispose();
            if (readOutcome == "unexpected")
            {
                Assert.Same(readFailure, await Assert.ThrowsAsync<InvalidOperationException>(
                    () => join.WaitAsync(FrameDeadline)));
                // 已完成的故障也必须 await，不能以 IsCompleted 为由提前返回。
                Assert.Same(readFailure, await Assert.ThrowsAsync<InvalidOperationException>(
                    session.JoinPendingReadAsync));
            }
            else
            {
                await join.WaitAsync(FrameDeadline);
                Assert.True(join.IsCompletedSuccessfully);
                await session.JoinPendingReadAsync().WaitAsync(FrameDeadline);
            }
            Assert.True(approvalRead.IsCompleted);
            Assert.Equal(readOutcome == "cancelled", approvalRead.IsCanceled);
            Assert.Equal(readOutcome is "io" or "disposed" or "unexpected", approvalRead.IsFaulted);
            Assert.Equal(readFailure is null, approvalRead.IsCompletedSuccessfully);
            Assert.Same(approvalRead, ReadPendingApprovalTask(session));
            Assert.Equal(1, stream.PendingReadCalls);
        }
        finally
        {
            stream.Dispose();
            try
            {
                await join.WaitAsync(FrameDeadline);
            }
            catch (InvalidOperationException ex) when (ReferenceEquals(ex, readFailure))
            {
            }
        }
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
        // 合成第二视频上下文：仍是受控 IO 单测，不建立额外 TLS 连接。
        ConnectionSecurityContext syntheticVideo = new(
            security.LocalAddress, security.RemoteAddress, 0, SslProtocols.None,
            security.ServerCertificateSha256.Span);
        Assert.NotEqual(security.ConnectionId, syntheticVideo.ConnectionId);
        Assert.Equal(VideoAttachStatus.NotRegistered, context.SessionRegistry.TryAttachVideo(
            session.SessionId, syntheticVideo, new byte[16], new byte[32], default, out VideoAttachLease? lease));
        Assert.Null(lease);
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

        // pending 写失败尚未启动活动读，join 必须立即完成且不能因此新开读者。
        Task join = session.JoinPendingReadAsync();
        Task ownedJoin = session.JoinOwnedOperationsAsync();
        try
        {
            Assert.Equal(cancelOnWrite == 2, ownedJoin.IsCompletedSuccessfully);
            Assert.Equal(cancelOnWrite == 2, join.IsCompletedSuccessfully);
            if (cancelOnWrite == 3)
            {
                Assert.False(join.IsCompleted);
                Assert.False(ownedJoin.IsCompleted);
            }
            Assert.Equal(cancelOnWrite == 2 ? 0 : 1, stream.PendingReadCalls);
        }
        finally
        {
            stream.Dispose();
            await join.WaitAsync(FrameDeadline);
            await ownedJoin.WaitAsync(FrameDeadline);
        }
        Assert.True(join.IsCompletedSuccessfully);
        Assert.True(ownedJoin.IsCompletedSuccessfully);
        Assert.Equal(cancelOnWrite == 2 ? 0 : 1, stream.PendingReadCalls);
    }

    // 受控 IO 接线测试，不建立 TLS：write 消耗 6 秒、flush 消耗 4 秒，登记不能重置写前的窗口。
    // 每行都是新会话，避免先附着成功消费资格后再测截止，混淆 Expired 与 AlreadyAttached。
    [Theory(Timeout = 120_000)]
    [InlineData(15_000, -1, true)]
    [InlineData(15_000, 0, false)]
    [InlineData(int.MaxValue, -1, true)]
    [InlineData(int.MaxValue, 0, false)]
    public async Task Video_Attach_Window_Starts_Before_Success_Write_And_Is_Capped_With_Controlled_Io(
        int hintMs, int deadlineOffsetTicks, bool canAttach)
    {
        ManualDeadlineClock clock = new();
        clock.Advance(TimeSpan.FromSeconds(23), fireTimers: false);
        byte[] returnedKey = GoodKey.ToArray();
        ScriptedDeadlineStore store = new(_ => Task.FromResult(new AccessSecret(returnedKey)));
        StubApprovalGate gate = StubApprovalGate.Approve();
        ControlAuthContext context = NewContext(gate, options: FastOptions with
        {
            RequireLocalApproval = false,
            VideoAttachExpiresInMs = hintMs,
        }, secretStore: store) with { TimeProvider = clock };
        ConnectionSecurityContext security = new(
            IPAddress.Loopback, IPAddress.Loopback, 0, SslProtocols.None,
            new byte[CertificatePin.LengthBytes]);
        ConnectionSecurityContext video = new(
            security.LocalAddress, security.RemoteAddress, 0, SslProtocols.None,
            security.ServerCertificateSha256.Span);
        using CleanupIoSslStream stream = new();
        ControlAuthSession session = new(stream, security, context, BuildAuthTimeouts());
        byte[] nonce = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        byte[]? wireToken = null;
        byte[]? proof = null;
        byte[]? registryToken = null;
        List<string> callbacks = new();

        void AssertNotRegistered()
        {
            Assert.Equal(0, context.SessionRegistry.ActiveSessionCount);
            Assert.Empty(context.SessionRegistry.Snapshot());
            Assert.Equal(VideoAttachStatus.NotRegistered, context.SessionRegistry.TryAttachVideo(
                session.SessionId, video, nonce, Assert.IsType<byte[]>(proof), default, out var premature));
            Assert.Null(premature);
        }

        stream.OnSuccessWrite = () =>
        {
            AuthSuccessFrame success = Assert.IsType<AuthSuccessFrame>(stream.SuccessAttempt);
            Assert.Equal(hintMs, success.VideoAttachExpiresInMs); // wire hint 不必被改写，实际窗口仍封顶。
            wireToken = success.SessionToken.ToArray();
            proof = IndependentVideoAttachProof(wireToken, session.SessionId, nonce,
                video.ServerCertificateSha256.ToArray());
            AssertNotRegistered();
            Assert.All(returnedKey, value => Assert.Equal((byte)0, value));
            Assert.Equal(TimeSpan.FromSeconds(23).Ticks, clock.GetTimestamp());
            clock.Advance(TimeSpan.FromSeconds(6), fireTimers: false);
            callbacks.Add("write");
        };
        stream.OnSuccessFlush = () =>
        {
            AssertNotRegistered();
            Assert.Equal(TimeSpan.FromSeconds(29).Ticks, clock.GetTimestamp());
            clock.Advance(TimeSpan.FromSeconds(4), fireTimers: false);
            callbacks.Add("flush");
        };

        Task<ControlAuthResult> run = session.RunAsync(CancellationToken.None);
        try
        {
            // 禁用审批，所以这一 TCS 对应登记、局部清理之后才启动的 holding 读，而不是 pending 读。
            await Task.WhenAny(stream.SingleReadStarted.Task, run).WaitAsync(FrameDeadline);
            Assert.True(stream.SingleReadStarted.Task.IsCompletedSuccessfully);
            Assert.Equal(new[] { "write", "flush" }, callbacks);
            Assert.Equal(new[] { "auth_challenge", "auth_success" }, stream.AcceptedFrames);
            Assert.Equal(0, gate.RequestCount);
            Assert.Equal(1, store.Calls);
            Assert.Equal(2, stream.ResponseReadCalls);
            Assert.True(stream.ResponseFullyRead);
            Assert.Equal(1, stream.PendingReadCalls);
            Assert.False(stream.PendingRead.IsCompleted);
            Assert.False(run.IsCompleted);
            Assert.Equal(ControlSessionState.Authenticated, session.State);
            Assert.Equal(session.SessionId, Assert.Single(context.SessionRegistry.Snapshot()).SessionId);
            Assert.Equal(1, context.SessionRegistry.ActiveSessionCount);
            Assert.NotEqual(security.ConnectionId, video.ConnectionId);
            Assert.All(returnedKey, value => Assert.Equal((byte)0, value));
            registryToken = ReadRegistryTokenArray(context.SessionRegistry, session.SessionId);
            Assert.Equal(Assert.IsType<byte[]>(wireToken), registryToken);
            Assert.NotSame(wireToken, registryToken);
            Assert.Contains(registryToken, value => value != 0);

            // 写前时刻 + 15 秒 - 1 tick 可附着；恰好 + 15 秒拒绝。不能从登记时刻再给 15 秒。
            clock.Advance(TimeSpan.FromSeconds(5) + TimeSpan.FromTicks(deadlineOffsetTicks), fireTimers: false);
            Assert.Equal(TimeSpan.FromSeconds(38).Ticks + deadlineOffsetTicks, clock.GetTimestamp());
            Assert.Equal(canAttach ? VideoAttachStatus.Attached : VideoAttachStatus.Expired,
                context.SessionRegistry.TryAttachVideo(session.SessionId, video, nonce,
                    Assert.IsType<byte[]>(proof), default, out var lease));
            Assert.Equal(canAttach, lease is not null);
            if (lease is not null)
            {
                Assert.Equal(session.SessionId, lease.SessionId);
                Assert.Equal(video.ConnectionId, lease.VideoConnectionId);
                Assert.False(lease.Revoked.IsCompleted);
            }
            Assert.Equal(1, context.SessionRegistry.ActiveSessionCount);
            Assert.False(run.IsCompleted);
            Assert.False(stream.PendingRead.IsCompleted);
            Assert.Equal(1, stream.PendingReadCalls);
        }
        finally
        {
            // 断言失败也让唯一读以 EOF 结束，并等待认证任务完成注销；不靠取消一个不合作的读。
            stream.Dispose();
            await run.WaitAsync(FrameDeadline);
        }

        Assert.True((await run).Completed);
        Assert.Equal(0, context.SessionRegistry.ActiveSessionCount);
        Assert.Equal(0, context.PendingApprovalLimiter.GlobalInUse);
        Assert.Equal(0, context.FailedAuthLimiter.CountRecentFailures(IPAddress.Loopback));
        Assert.Equal(0, await stream.PendingRead);
        Assert.All(Assert.IsType<byte[]>(registryToken), value => Assert.Equal((byte)0, value));
        // 只观察 store 原数组与 registry 原数组；不从接收端副本推断 success/sessionToken/payload 局部清零。
    }

    // 受控 IO 接线测试：即便 success 写出期间视频资格已过期，Control 仍登记且复用唯一审批读。
    [Theory(Timeout = 120_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_Video_Window_Still_Registers_Control_And_Reuses_Pending_Read_With_Controlled_Io(
        bool expireDuringFlush)
    {
        ManualDeadlineClock clock = new();
        using CleanupIoSslStream stream = new();
        Task<int>? approvalRead = null;
        StubApprovalGate gate = new((request, _) =>
        {
            approvalRead = stream.PendingRead;
            Assert.True(stream.SingleReadStarted.Task.IsCompletedSuccessfully);
            Assert.Equal(1, stream.PendingReadCalls);
            Assert.False(approvalRead.IsCompleted);
            return ValueTask.FromResult(new LocalApprovalDecision(
                request.RequestId, LocalApprovalOutcome.Approved, request.RequestedPermission));
        });
        ControlAuthContext context = NewContext(gate, options: FastOptions with
        {
            VideoAttachExpiresInMs = 15_000,
        }) with { TimeProvider = clock };
        ConnectionSecurityContext security = new(
            IPAddress.Loopback, IPAddress.Loopback, 0, SslProtocols.None,
            new byte[CertificatePin.LengthBytes]);
        ConnectionSecurityContext video = new(
            security.LocalAddress, security.RemoteAddress, 0, SslProtocols.None,
            security.ServerCertificateSha256.Span);
        ControlAuthSession session = new(stream, security, context, BuildAuthTimeouts());
        byte[] nonce = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        byte[]? proof = null;
        List<string> callbacks = new();

        void AssertNotRegistered()
        {
            Assert.Equal(0, context.SessionRegistry.ActiveSessionCount);
            Assert.Equal(VideoAttachStatus.NotRegistered, context.SessionRegistry.TryAttachVideo(
                session.SessionId, video, nonce, Assert.IsType<byte[]>(proof), default, out var premature));
            Assert.Null(premature);
            Assert.Equal(1, stream.PendingReadCalls);
            Assert.False(stream.PendingRead.IsCompleted);
        }

        stream.OnSuccessWrite = () =>
        {
            AuthSuccessFrame success = Assert.IsType<AuthSuccessFrame>(stream.SuccessAttempt);
            proof = IndependentVideoAttachProof(success.SessionToken.ToArray(), session.SessionId, nonce,
                video.ServerCertificateSha256.ToArray());
            AssertNotRegistered();
            if (!expireDuringFlush)
            {
                clock.Advance(TimeSpan.FromSeconds(16), fireTimers: false);
            }
            callbacks.Add("write");
        };
        stream.OnSuccessFlush = () =>
        {
            AssertNotRegistered();
            if (expireDuringFlush)
            {
                clock.Advance(TimeSpan.FromSeconds(16), fireTimers: false);
            }
            callbacks.Add("flush");
        };

        Task<ControlAuthResult> run = session.RunAsync(CancellationToken.None);
        try
        {
            await Task.WhenAny(stream.SingleReadStarted.Task, run).WaitAsync(FrameDeadline);
            // 本夹具 response/store/gate/write/flush 都同步完成；这里不是把 pending 的 TCS 冒称 holding 通知。
            // RunAsync 返回时仍挂起、登记已可见且没有第二次读，证明审批读已交给 holding 复用。
            Assert.Equal(new[] { "write", "flush" }, callbacks);
            Assert.Equal(TimeSpan.FromSeconds(16).Ticks, clock.GetTimestamp());
            Assert.Equal(1, gate.RequestCount);
            Assert.Equal(new[] { "auth_challenge", "approval_pending", "auth_success" }, stream.AcceptedFrames);
            Assert.Equal(ControlSessionState.Authenticated, session.State);
            Assert.Equal(1, context.SessionRegistry.ActiveSessionCount);
            Assert.Equal(session.SessionId, Assert.Single(context.SessionRegistry.Snapshot()).SessionId);
            Assert.Equal(0, context.PendingApprovalLimiter.GlobalInUse);
            Assert.Equal(2, stream.ResponseReadCalls);
            Assert.True(stream.ResponseFullyRead);
            Assert.Same(approvalRead, stream.PendingRead);
            Assert.Equal(1, stream.PendingReadCalls);
            Assert.False(stream.PendingRead.IsCompleted);
            Assert.False(run.IsCompleted);
            Assert.NotEqual(security.ConnectionId, video.ConnectionId);
            Assert.Equal(VideoAttachStatus.Expired, context.SessionRegistry.TryAttachVideo(
                session.SessionId, video, nonce, Assert.IsType<byte[]>(proof), default, out var rejected));
            Assert.Null(rejected);
            Assert.Equal(1, context.SessionRegistry.ActiveSessionCount);
            Assert.False(run.IsCompleted);
            Assert.Equal(1, stream.PendingReadCalls);

            Task<int> pendingRead = ReadPendingApprovalTask(session);
            Assert.NotSame(approvalRead, pendingRead);
            Assert.False(pendingRead.IsCompleted);
            Task join = session.JoinPendingReadAsync();
            Assert.False(join.IsCompleted);
            Assert.Equal(1, stream.PendingReadCalls);
            stream.Dispose();
            Assert.True((await run.WaitAsync(FrameDeadline)).Completed);
            await join.WaitAsync(FrameDeadline);
            Assert.True(join.IsCompletedSuccessfully);
            Assert.True(pendingRead.IsCompletedSuccessfully);
            Assert.Same(pendingRead, ReadPendingApprovalTask(session));
            Assert.Equal(1, stream.PendingReadCalls);
        }
        finally
        {
            stream.Dispose();
            await run.WaitAsync(FrameDeadline);
            await session.JoinPendingReadAsync().WaitAsync(FrameDeadline);
        }

        Assert.True((await run).Completed);
        Assert.Equal(0, await stream.PendingRead);
        Assert.Equal(0, context.SessionRegistry.ActiveSessionCount);
        Assert.Equal(0, context.FailedAuthLimiter.CountRecentFailures(IPAddress.Loopback));
    }

    // 受控 write/flush 故障，不声称真实 TLS 传输失败或证明对端是否收到 success。
    [Theory(Timeout = 120_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Success_Write_Or_Flush_Failure_Never_Registers_With_Controlled_Io(bool failDuringFlush)
    {
        using CleanupIoSslStream stream = new();
        StubApprovalGate gate = StubApprovalGate.Approve();
        ControlAuthContext context = NewContext(gate) with { TimeProvider = new ManualDeadlineClock() };
        ConnectionSecurityContext security = new(
            IPAddress.Loopback, IPAddress.Loopback, 0, SslProtocols.None,
            new byte[CertificatePin.LengthBytes]);
        ConnectionSecurityContext video = new(
            security.LocalAddress, security.RemoteAddress, 0, SslProtocols.None,
            security.ServerCertificateSha256.Span);
        ControlAuthSession session = new(stream, security, context, BuildAuthTimeouts());
        byte[] nonce = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        byte[]? proof = null;
        List<string> callbacks = new();

        void AssertNotRegistered()
        {
            Assert.Equal(0, context.SessionRegistry.ActiveSessionCount);
            Assert.Empty(context.SessionRegistry.Snapshot());
            Assert.Equal(VideoAttachStatus.NotRegistered, context.SessionRegistry.TryAttachVideo(
                session.SessionId, video, nonce, Assert.IsType<byte[]>(proof), default, out var rejected));
            Assert.Null(rejected);
        }

        stream.OnSuccessWrite = () =>
        {
            AuthSuccessFrame success = Assert.IsType<AuthSuccessFrame>(stream.SuccessAttempt);
            proof = IndependentVideoAttachProof(success.SessionToken.ToArray(), session.SessionId, nonce,
                video.ServerCertificateSha256.ToArray());
            AssertNotRegistered();
            callbacks.Add("write");
            if (!failDuringFlush)
            {
                throw new IOException("受控 success 写出失败。");
            }
        };
        stream.OnSuccessFlush = () =>
        {
            AssertNotRegistered();
            callbacks.Add("flush");
            throw new IOException("受控 success flush 失败。");
        };

        Task<ControlAuthResult> run = session.RunAsync(CancellationToken.None);
        try
        {
            ControlAuthResult result = await run.WaitAsync(FrameDeadline);
            Assert.False(result.Completed);
            Assert.Equal(ControlAuthSession.RejectSuccessNotDelivered, result.Rejection);
            Assert.Equal(ControlSessionState.Closed, result.State);
            Assert.Equal(ControlSessionState.Closed, session.State);
            Assert.Equal(failDuringFlush ? new[] { "write", "flush" } : new[] { "write" }, callbacks);
            Assert.Equal(new[] { "auth_challenge", "approval_pending", "auth_success", "authentication_failed" },
                stream.WriteAttempts);
            Assert.Equal(failDuringFlush
                ? new[] { "auth_challenge", "approval_pending", "auth_success", "authentication_failed" }
                : new[] { "auth_challenge", "approval_pending", "authentication_failed" }, stream.AcceptedFrames);
            AssertNotRegistered();
            Assert.Equal(1, gate.RequestCount);
            Assert.Equal(0, context.PendingApprovalLimiter.GlobalInUse);
            Assert.Equal(0, context.FailedAuthLimiter.CountRecentFailures(IPAddress.Loopback));
            Assert.True(stream.SingleReadStarted.Task.IsCompletedSuccessfully);
            Assert.Equal(1, stream.PendingReadCalls);
            Assert.False(stream.PendingRead.IsCompleted);
        }
        finally
        {
            stream.Dispose();
            await run.WaitAsync(FrameDeadline);
        }

        Assert.Equal(0, await stream.PendingRead);
        Assert.Equal(0, context.SessionRegistry.ActiveSessionCount);
    }

    // 受控 IO 红测：failure 写仍挂起时，必须已经擦除 RunAsync 自己拥有的三份秘密。
    // 先真正异步挂起 success 写，等 RunAsync 返回 Task 后才只读反射；不使用 wire 解析副本。
    [Theory(Timeout = 120_000)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Success_Locals_Are_Cleared_Before_Blocked_Failure_Write_With_Controlled_Io(
        bool failDuringFlush, bool localCancellation)
    {
        using CancellationTokenSource caller = new();
        using CleanupIoSslStream stream = new();
        TaskCompletionSource successWriteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resumeSuccessWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource failureWriteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resumeFailureWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception injectedFailure = localCancellation
            ? new OperationCanceledException("受控 success 本地取消，非调用方取消。")
            : new IOException("受控 success IO 故障。");
        int successFlushCalls = 0;
        stream.OnWriteAsync = async type =>
        {
            if (type == "auth_success")
            {
                successWriteEntered.TrySetResult();
                await resumeSuccessWrite.Task.ConfigureAwait(false);
            }
            else if (type == "authentication_failed")
            {
                failureWriteEntered.TrySetResult();
                await resumeFailureWrite.Task.ConfigureAwait(false);
            }
        };
        stream.OnSuccessFlush = () =>
        {
            successFlushCalls++;
            throw injectedFailure;
        };
        StubApprovalGate gate = StubApprovalGate.Approve();
        ControlAuthContext context = NewContext(gate, options: FastOptions with
        {
            RequireLocalApproval = false,
        }) with { TimeProvider = new ManualDeadlineClock() };
        ConnectionSecurityContext security = new(
            IPAddress.Loopback, IPAddress.Loopback, 0, SslProtocols.None,
            new byte[CertificatePin.LengthBytes]);
        ControlAuthSession session = new(stream, security, context, BuildPausedSuccessTimeouts());

        Task<ControlAuthResult> run = session.RunAsync(caller.Token);
        try
        {
            await Task.WhenAny(successWriteEntered.Task, run).WaitAsync(FrameDeadline);
            Assert.True(successWriteEntered.Task.IsCompletedSuccessfully);
            Assert.False(resumeSuccessWrite.Task.IsCompleted);
            Assert.False(run.IsCompleted);
            var (sessionToken, successPayload, success) = ReadSuspendedSuccessLocals(run);
            ReadOnlyMemory<byte> originalSuccessToken = success.SessionToken;
            Assert.Equal(0, context.SessionRegistry.ActiveSessionCount);
            Assert.Equal(0, stream.PendingReadCalls);

            if (failDuringFlush)
            {
                resumeSuccessWrite.SetResult();
            }
            else
            {
                // 异常由异步 WriteAsync 内部的 await 接收，绝不在 RunAsync 返回之前同步抛出。
                resumeSuccessWrite.SetException(injectedFailure);
            }
            await Task.WhenAny(failureWriteEntered.Task, run).WaitAsync(FrameDeadline);
            Assert.True(failureWriteEntered.Task.IsCompletedSuccessfully);
            Assert.False(resumeFailureWrite.Task.IsCompleted);
            Assert.False(run.IsCompleted);
            Assert.False(caller.IsCancellationRequested);
            Assert.Equal(failDuringFlush ? 1 : 0, successFlushCalls);
            Assert.Equal(new[] { "auth_challenge", "auth_success", "authentication_failed" }, stream.WriteAttempts);
            Assert.DoesNotContain("authentication_failed", stream.AcceptedFrames);
            Assert.Equal(ControlSessionState.Closed, session.State);
            Assert.Equal(0, context.SessionRegistry.ActiveSessionCount);
            Assert.Equal(0, gate.RequestCount);
            Assert.Equal(0, stream.PendingReadCalls);

            // 当前 bug 在这里红：catch 的 return await FailAsync 尚未退出，外层 finally 还没执行。
            // 同时检查全部三份原引用；不能等 failure 写结束/RunAsync 返回后才检查。
            AssertSuccessLocalsCleared(sessionToken, successPayload, originalSuccessToken);
        }
        finally
        {
            // 红测断言/反射失败也放行两笔写、以 EOF 解除读，并等待会话收尾；不留下挂起任务。
            resumeSuccessWrite.TrySetResult();
            resumeFailureWrite.TrySetResult();
            successWriteEntered.TrySetCanceled();
            failureWriteEntered.TrySetCanceled();
            stream.Dispose();
            await run.WaitAsync(FrameDeadline);
        }

        ControlAuthResult result = await run;
        Assert.False(result.Completed);
        Assert.Equal(ControlAuthSession.RejectSuccessNotDelivered, result.Rejection);
        Assert.Equal(ControlSessionState.Closed, result.State);
        Assert.Equal("authentication_failed", stream.AcceptedFrames.Last());
        Assert.Equal(0, context.SessionRegistry.ActiveSessionCount);
        Assert.Equal(0, context.PendingApprovalLimiter.GlobalInUse);
        Assert.Equal(0, context.FailedAuthLimiter.CountRecentFailures(IPAddress.Loopback));
    }

    // 与原内存清零测试相邻：失败 Run 可以先返回，只有 Dispose 原流才能解除审批期的同一笔读。
    [Theory(Timeout = 120_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_Run_Joins_Approval_Read_Only_After_Stream_Disposal_With_Controlled_Io(
        bool denyApproval)
    {
        using CleanupIoSslStream stream = new();
        StubApprovalGate gate = new((request, _) => ValueTask.FromResult(new LocalApprovalDecision(
            request.RequestId,
            denyApproval ? LocalApprovalOutcome.Denied : LocalApprovalOutcome.Approved,
            denyApproval ? null : request.RequestedPermission)));
        ControlAuthContext context = NewContext(gate) with { TimeProvider = new ManualDeadlineClock() };
        ConnectionSecurityContext security = new(
            IPAddress.Loopback, IPAddress.Loopback, 0, SslProtocols.None,
            new byte[CertificatePin.LengthBytes]);
        ControlAuthSession session = new(stream, security, context, BuildAuthTimeouts());
        stream.OnSuccessWrite = () => throw new IOException("受控 success 写出失败。");

        Task<ControlAuthResult> run = session.RunAsync(CancellationToken.None);
        try
        {
            ControlAuthResult result = await run.WaitAsync(FrameDeadline);
            Assert.False(result.Completed);
            Assert.Equal(denyApproval ? ControlAuthSession.RejectApprovalDenied
                : ControlAuthSession.RejectSuccessNotDelivered, result.Rejection);
            Assert.Equal(ControlSessionState.Closed, session.State);
            Assert.Equal(0, context.SessionRegistry.ActiveSessionCount);
            Assert.Equal(0, context.PendingApprovalLimiter.GlobalInUse);
            Assert.Equal(1, gate.RequestCount);
            Assert.Equal(2, stream.ResponseReadCalls);
            Assert.True(stream.ResponseFullyRead);
            Assert.Equal(denyApproval
                ? new[] { "auth_challenge", "approval_pending", "authentication_failed" }
                : new[] { "auth_challenge", "approval_pending", "auth_success", "authentication_failed" },
                stream.WriteAttempts);
            Assert.Equal(new[] { "auth_challenge", "approval_pending", "authentication_failed" },
                stream.AcceptedFrames);

            Task<int> approvalRead = ReadPendingApprovalTask(session);
            Assert.NotSame(stream.PendingRead, approvalRead);
            Assert.False(approvalRead.IsCompleted);
            Assert.False(stream.PendingRead.IsCompleted);
            Task join = session.JoinPendingReadAsync();
            Assert.False(join.IsCompleted);
            Assert.Equal(1, stream.PendingReadCalls);

            stream.Dispose();
            await join.WaitAsync(FrameDeadline);
            Assert.True(join.IsCompletedSuccessfully);
            Assert.True(approvalRead.IsCompletedSuccessfully);
            Assert.Same(approvalRead, ReadPendingApprovalTask(session));
            Assert.Equal(1, stream.PendingReadCalls);
        }
        finally
        {
            stream.Dispose();
            await run.WaitAsync(FrameDeadline);
            await session.JoinPendingReadAsync().WaitAsync(FrameDeadline);
        }
    }

    // 受控 gate 忽略取消；Run 已退出且原读已结束，也必须等原 decision 包装任务真正完成。
    [Theory(Timeout = 120_000)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task JoinOwnedOperations_Waits_For_Late_Gate_After_Run_Timeout_Or_Cancellation_With_Controlled_Io(
        bool cancelRun, bool cancelGate)
    {
        ManualDeadlineClock clock = new();
        using CancellationTokenSource caller = new();
        using CleanupIoSslStream stream = new();
        TaskCompletionSource<LocalApprovalDecision> late = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<CancellationToken> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubApprovalGate gate = new((_, token) =>
        {
            entered.TrySetResult(token);
            return new ValueTask<LocalApprovalDecision>(late.Task);
        });
        ControlAuthContext context = NewContext(gate) with { TimeProvider = clock };
        ConnectionSecurityContext security = new(
            IPAddress.Loopback, IPAddress.Loopback, 0, SslProtocols.None,
            new byte[CertificatePin.LengthBytes]);
        ControlAuthSession session = new(stream, security, context, BuildAuthTimeouts());

        Task<ControlAuthResult> run = session.RunAsync(caller.Token);
        try
        {
            CancellationToken gateToken = await entered.Task.WaitAsync(FrameDeadline);
            Assert.False(run.IsCompleted);
            if (cancelRun)
            {
                caller.Cancel();
                OperationCanceledException cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => run.WaitAsync(FrameDeadline));
                Assert.Equal(caller.Token, cancelled.CancellationToken);
            }
            else
            {
                clock.Advance(FastOptions.ApprovalWindow);
                ControlAuthResult result = await run.WaitAsync(FrameDeadline);
                Assert.False(result.Completed);
                Assert.Equal(ControlAuthSession.RejectApprovalTimeout, result.Rejection);
            }
            Assert.True(gateToken.IsCancellationRequested);
            Assert.Equal(ControlSessionState.Closed, session.State);
            Assert.Equal(0, context.PendingApprovalLimiter.GlobalInUse);
            Assert.False(stream.PendingRead.IsCompleted);
            Task<int> pendingRead = ReadPendingApprovalTask(session);
            Task<LocalApprovalDecision> decision = ReadApprovalDecisionTask(session);
            Assert.NotSame(late.Task, decision);

            stream.Dispose();
            await session.JoinPendingReadAsync().WaitAsync(FrameDeadline);
            Assert.True(pendingRead.IsCompletedSuccessfully);
            Assert.False(decision.IsCompleted);
            Task join = session.JoinOwnedOperationsAsync();
            Assert.False(join.IsCompleted);
            Assert.False(late.Task.IsCompleted);

            if (cancelGate)
            {
                late.SetCanceled(gateToken);
            }
            else
            {
                LocalApprovalRequest request = Assert.IsType<LocalApprovalRequest>(gate.LastRequest);
                late.SetResult(new LocalApprovalDecision(
                    request.RequestId, LocalApprovalOutcome.Approved, request.RequestedPermission));
            }
            await join.WaitAsync(FrameDeadline);
            Assert.True(join.IsCompletedSuccessfully);
            Assert.Equal(cancelGate, decision.IsCanceled);
            Assert.Equal(!cancelGate, decision.IsCompletedSuccessfully);
            Assert.Same(decision, ReadApprovalDecisionTask(session));
            Assert.Same(pendingRead, ReadPendingApprovalTask(session));
            Assert.Equal(1, stream.PendingReadCalls);
            Assert.Equal(1, gate.RequestCount);
            Assert.Equal(ControlSessionState.Closed, session.State);
            Assert.Equal(0, context.SessionRegistry.ActiveSessionCount);
            Assert.DoesNotContain("auth_success", stream.AcceptedFrames);
        }
        finally
        {
            late.TrySetCanceled();
            stream.Dispose();
            try
            {
                await run.WaitAsync(FrameDeadline);
            }
            catch (OperationCanceledException) when (caller.IsCancellationRequested)
            {
            }
            await session.JoinOwnedOperationsAsync().WaitAsync(FrameDeadline);
        }
    }

    // 两个完成顺序都覆盖：首项故障不能短路 join，且 WhenAll 必须保留两项意外故障。
    [Theory(Timeout = 120_000)]
    [InlineData(false, "io")]
    [InlineData(false, "disposed")]
    [InlineData(false, "unexpected")]
    [InlineData(true, "io")]
    [InlineData(true, "disposed")]
    [InlineData(true, "unexpected")]
    public async Task JoinOwnedOperations_Waits_For_Both_Operations_And_Preserves_Faults_With_Controlled_Io(
        bool gateFaultFirst, string gateFault)
    {
        InvalidOperationException readFailure = new("受控审批读意外故障。");
        Exception gateFailure = gateFault switch
        {
            "io" => new IOException("受控 gate IO 故障，不能当作读断连吞掉。"),
            "disposed" => new ObjectDisposedException("受控 gate"),
            _ => new InvalidOperationException("受控 gate 意外故障。"),
        };
        using CleanupIoSslStream stream = new() { ReadExceptionOnDispose = readFailure };
        TaskCompletionSource<LocalApprovalDecision> late = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubApprovalGate gate = new((_, _) =>
        {
            entered.TrySetResult();
            return new ValueTask<LocalApprovalDecision>(late.Task);
        });
        ControlAuthContext context = NewContext(gate) with { TimeProvider = new ManualDeadlineClock() };
        ConnectionSecurityContext security = new(
            IPAddress.Loopback, IPAddress.Loopback, 0, SslProtocols.None,
            new byte[CertificatePin.LengthBytes]);
        ControlAuthSession session = new(stream, security, context, BuildAuthTimeouts());

        Task<ControlAuthResult> run = session.RunAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(FrameDeadline);
            if (gateFaultFirst)
            {
                late.SetException(gateFailure);
            }
            else
            {
                stream.Dispose();
            }
            ControlAuthResult result = await run.WaitAsync(FrameDeadline);
            Assert.False(result.Completed);
            Assert.Equal(gateFaultFirst ? ControlAuthSession.RejectApprovalUnavailable
                : ControlAuthSession.RejectApprovalDisconnected, result.Rejection);
            Task<int> pendingRead = ReadPendingApprovalTask(session);
            Task<LocalApprovalDecision> decision = ReadApprovalDecisionTask(session);
            Assert.NotSame(late.Task, decision);
            Assert.Equal(gateFaultFirst, decision.IsFaulted);
            Assert.Equal(!gateFaultFirst, pendingRead.IsFaulted);
            Task join = session.JoinOwnedOperationsAsync();
            Assert.False(join.IsCompleted);

            if (gateFaultFirst)
            {
                Assert.False(pendingRead.IsCompleted);
                stream.Dispose();
            }
            else
            {
                Assert.False(decision.IsCompleted);
                late.SetException(gateFailure);
            }
            await Assert.ThrowsAnyAsync<Exception>(() => join.WaitAsync(FrameDeadline));
            Assert.True(join.IsFaulted);
            var failures = Assert.IsType<AggregateException>(join.Exception).Flatten().InnerExceptions;
            Assert.Equal(2, failures.Count);
            Assert.Contains(readFailure, failures);
            Assert.Contains(gateFailure, failures);
            Assert.True(pendingRead.IsFaulted);
            Assert.True(decision.IsFaulted);
            Assert.Same(pendingRead, ReadPendingApprovalTask(session));
            Assert.Same(decision, ReadApprovalDecisionTask(session));
            Assert.Equal(1, stream.PendingReadCalls);
            Assert.Equal(1, gate.RequestCount);
            Assert.Equal(ControlSessionState.Closed, session.State);
            Assert.Equal(0, context.PendingApprovalLimiter.GlobalInUse);
            Assert.Equal(0, context.SessionRegistry.ActiveSessionCount);
            Assert.DoesNotContain("auth_success", stream.AcceptedFrames);
        }
        finally
        {
            late.TrySetCanceled();
            stream.Dispose();
            await run.WaitAsync(FrameDeadline);
            Task cleanup = session.JoinOwnedOperationsAsync();
            try
            {
                await cleanup.WaitAsync(FrameDeadline);
            }
            catch (Exception ex) when (ReferenceEquals(ex, readFailure) || ReferenceEquals(ex, gateFailure))
            {
                _ = cleanup.Exception;
            }
        }
    }

    // 成功对照：相同异步暂停点借到原引用，恢复后在 holding（而非断连后）检查清理。
    [Fact(Timeout = 120_000)]
    public async Task Success_Locals_Are_Cleared_While_Holding_And_Registry_Token_Remains_Usable_With_Controlled_Io()
    {
        using CleanupIoSslStream stream = new();
        TaskCompletionSource successWriteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resumeSuccessWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        stream.OnWriteAsync = async type =>
        {
            if (type == "auth_success")
            {
                successWriteEntered.TrySetResult();
                await resumeSuccessWrite.Task.ConfigureAwait(false);
            }
        };
        StubApprovalGate gate = StubApprovalGate.Approve();
        ControlAuthContext context = NewContext(gate, options: FastOptions with
        {
            RequireLocalApproval = false,
        }) with { TimeProvider = new ManualDeadlineClock() };
        ConnectionSecurityContext security = new(
            IPAddress.Loopback, IPAddress.Loopback, 0, SslProtocols.None,
            new byte[CertificatePin.LengthBytes]);
        ConnectionSecurityContext video = new(
            security.LocalAddress, security.RemoteAddress, 0, SslProtocols.None,
            security.ServerCertificateSha256.Span);
        ControlAuthSession session = new(stream, security, context, BuildPausedSuccessTimeouts());
        byte[]? registryToken = null;
        VideoAttachLease? videoLease = null;

        Task<ControlAuthResult> run = session.RunAsync(CancellationToken.None);
        try
        {
            await Task.WhenAny(successWriteEntered.Task, run).WaitAsync(FrameDeadline);
            Assert.True(successWriteEntered.Task.IsCompletedSuccessfully);
            Assert.False(resumeSuccessWrite.Task.IsCompleted);
            Assert.False(run.IsCompleted);
            var (sessionToken, successPayload, success) = ReadSuspendedSuccessLocals(run);
            ReadOnlyMemory<byte> originalSuccessToken = success.SessionToken;
            Assert.Equal(0, context.SessionRegistry.ActiveSessionCount);
            Assert.False(stream.SingleReadStarted.Task.IsCompleted);
            byte[] nonce = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
            // proof 在清理前由真正的 sessionToken 生成，不从 wire 副本借 token。
            byte[] proof = IndependentVideoAttachProof(sessionToken, session.SessionId, nonce,
                video.ServerCertificateSha256.ToArray());

            resumeSuccessWrite.SetResult();
            // 无审批，因此此信号严格晚于登记及局部 finally，是 holding 的唯一读。
            await Task.WhenAny(stream.SingleReadStarted.Task, run).WaitAsync(FrameDeadline);
            Assert.True(stream.SingleReadStarted.Task.IsCompletedSuccessfully);
            Assert.False(run.IsCompleted);
            Assert.False(stream.PendingRead.IsCompleted);
            Assert.Equal(1, stream.PendingReadCalls);
            Assert.Equal(0, gate.RequestCount);
            Assert.Equal(new[] { "auth_challenge", "auth_success" }, stream.AcceptedFrames);
            Assert.Equal(ControlSessionState.Authenticated, session.State);
            Assert.Equal(1, context.SessionRegistry.ActiveSessionCount);
            AssertSuccessLocalsCleared(sessionToken, successPayload, originalSuccessToken);
            Assert.True(originalSuccessToken.Equals(success.SessionToken));

            registryToken = ReadRegistryTokenArray(context.SessionRegistry, session.SessionId);
            Assert.NotSame(sessionToken, registryToken);
            Assert.False(originalSuccessToken.Equals((ReadOnlyMemory<byte>)registryToken));
            Assert.Contains(registryToken, value => value != 0);
            Assert.Equal(VideoAttachStatus.Attached, context.SessionRegistry.TryAttachVideo(
                session.SessionId, video, nonce, proof, default, out videoLease));
            Assert.NotNull(videoLease);
            Assert.Equal(session.SessionId, videoLease.SessionId);
            Assert.Equal(video.ConnectionId, videoLease.VideoConnectionId);
            Assert.False(videoLease.Revoked.IsCompleted);
            Assert.Contains(registryToken, value => value != 0);
            Assert.False(run.IsCompleted);
        }
        finally
        {
            resumeSuccessWrite.TrySetResult();
            successWriteEntered.TrySetCanceled();
            stream.Dispose();
            await run.WaitAsync(FrameDeadline);
        }

        Assert.True((await run).Completed);
        Assert.Equal(0, await stream.PendingRead);
        Assert.Equal(0, context.SessionRegistry.ActiveSessionCount);
        Assert.Equal(0, context.PendingApprovalLimiter.GlobalInUse);
        Assert.All(Assert.IsType<byte[]>(registryToken), value => Assert.Equal((byte)0, value));
        Assert.True(Assert.IsType<VideoAttachLease>(videoLease).Revoked.IsCompleted);
    }

    // 只读保存的真实包装任务；不以底层 TCS 的完成或自行 await 它来冒充 join 的观察证据。
    private static Task<int> ReadPendingApprovalTask(ControlAuthSession session) =>
        Assert.IsAssignableFrom<Task<int>>(typeof(ControlAuthSession)
            .GetField("_pendingRead", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session));

    private static Task<LocalApprovalDecision> ReadApprovalDecisionTask(ControlAuthSession session) =>
        Assert.IsAssignableFrom<Task<LocalApprovalDecision>>(typeof(ControlAuthSession)
            .GetField("_approvalDecision", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session));

    // 写时限放到测试总看门狗之外，避免默认 5 秒 timer 抢先影响受控 write/flush 的因果顺序。
    private static TransportTimeouts BuildPausedSuccessTimeouts() => new(
        connectTimeout: FrameDeadline,
        handshakeTimeout: FrameDeadline,
        lengthPrefixTimeout: TransportTimeouts.Maximum,
        payloadTimeout: FrameDeadline,
        helloTimeout: FrameDeadline,
        preAuthEnvelopeTimeout: FrameDeadline);

    private static (byte[] SessionToken, byte[] SuccessPayload, AuthSuccessFrame Success)
        ReadSuspendedSuccessLocals(Task<ControlAuthResult> run)
    {
        Assert.False(run.IsCompleted);
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public
            | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        FieldInfo? stateMachineField = null;
        // 只沿返回 Task 的继承链找这个指定字段，不遍历 Task 的私有 continuation/其他对象图。
        for (Type? type = run.GetType(); type is not null; type = type.BaseType)
        {
            stateMachineField = type.GetField("StateMachine", fields);
            if (stateMachineField is not null)
            {
                Assert.True(type.Name == "AsyncStateMachineBox`1"
                    && type.DeclaringType == typeof(AsyncTaskMethodBuilder<>),
                    $"不支持的 StateMachine 字段声明类型：{type}；Task 实际类型：{run.GetType()}。");
                break;
            }
        }
        Assert.True(stateMachineField is not null,
            $"Task 实际类型 {run.GetType()} 的继承链没有 AsyncStateMachineBox.StateMachine；不能验证原秘密。");
        object? stateMachine = stateMachineField.GetValue(run);
        Assert.NotNull(stateMachine);
        // Debug 的 class / Release 的 struct 均由方法元数据确定实际类型，不硬编码 d__ 序号。
        // struct 的 GetValue 仅装箱状态机；下面取出的 byte[]/AuthSuccessFrame 仍是生产原引用。
        Type? expectedStateMachine = typeof(ControlAuthSession).GetMethod(nameof(ControlAuthSession.RunAsync))!
            .GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
        Assert.True(expectedStateMachine is not null && stateMachine.GetType() == expectedStateMachine,
            $"RunAsync 状态机类型不匹配：预期 {expectedStateMachine}，实际 {stateMachine.GetType()}；Task：{run.GetType()}。");

        object? ReadLocal(string name)
        {
            FieldInfo[] matches = stateMachine.GetType().GetFields(fields)
                .Where(field => field.Name.StartsWith($"<{name}>5__", StringComparison.Ordinal)).ToArray();
            Assert.True(matches.Length == 1,
                $"状态机 {stateMachine.GetType()} 的 <{name}>5__* 字段数量为 {matches.Length}；不能验证原秘密。");
            return matches[0].GetValue(stateMachine);
        }

        byte[] sessionToken = Assert.IsType<byte[]>(ReadLocal("sessionToken"));
        byte[] successPayload = Assert.IsType<byte[]>(ReadLocal("successPayload"));
        AuthSuccessFrame success = Assert.IsType<AuthSuccessFrame>(ReadLocal("success"));
        Assert.Equal(AuthProtocol.SessionTokenByteLength, sessionToken.Length);
        Assert.Equal(AuthProtocol.SessionTokenByteLength, success.SessionToken.Length);
        Assert.Contains(sessionToken, value => value != 0);
        Assert.Contains(successPayload, value => value != 0);
        Assert.True(success.SessionToken.Span.IndexOfAnyExcept((byte)0) >= 0);
        Assert.True(success.SessionToken.Span.SequenceEqual(sessionToken));
        Assert.False(success.SessionToken.Equals((ReadOnlyMemory<byte>)sessionToken));
        Assert.NotSame(sessionToken, successPayload);
        return (sessionToken, successPayload, success);
    }

    private static void AssertSuccessLocalsCleared(
        byte[] sessionToken, byte[] successPayload, ReadOnlyMemory<byte> originalSuccessToken)
    {
        // ReadOnlyMemory 只是原数组视图；Assert.All 同时报告三处清理缺失，不输出秘密内容。
        Assert.All(new (string Name, ReadOnlyMemory<byte> Bytes)[]
        {
            ("sessionToken", sessionToken),
            ("success.SessionToken 原视图", originalSuccessToken),
            ("successPayload", successPayload),
        }, secret => Assert.True(secret.Bytes.Span.IndexOfAnyExcept((byte)0) < 0,
            $"{secret.Name} 原内存尚未清零。"));
    }

    // 独立构造域、UUID 网络序、nonce 与 pin，不调用生产视频 proof/transcript helper。
    private static byte[] IndependentVideoAttachProof(byte[] token, Guid sessionId, byte[] nonce, byte[] pin)
    {
        byte[] transcript = new byte[83];
        byte[] domain = System.Text.Encoding.UTF8.GetBytes("LANREMOTE-VIDEO-V1\0");
        Assert.Equal(19, domain.Length);
        domain.CopyTo(transcript, 0);
        Convert.FromHexString(sessionId.ToString("N")).CopyTo(transcript, 19);
        nonce.CopyTo(transcript, 35);
        pin.CopyTo(transcript, 51);
        using HMACSHA256 hmac = new(token);
        return hmac.ComputeHash(transcript);
    }

    // 仅在受控 holding 且注销尚未开始时借用 registry 原数组；不以它代替 RunAsync 局部秘密。
    private static byte[] ReadRegistryTokenArray(SessionRegistry registry, Guid sessionId)
    {
        var entries = (System.Collections.IDictionary)typeof(SessionRegistry)
            .GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registry)!;
        object entry = entries[sessionId]!;
        return (byte[])entry.GetType().GetProperty("SessionToken")!.GetValue(entry)!;
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
        public int ReadResultOnDispose { get; init; }
        public Exception? ReadExceptionOnDispose { get; init; }
        public Action? OnSuccessWrite { get; set; }
        public Action? OnSuccessFlush { get; set; }
        public Func<string, Task>? OnWriteAsync { get; set; }
        // 从 FrameWriter 的帧副本解析出的测试自有值，绝不是 RunAsync 的 success 或 successPayload。
        public AuthSuccessFrame? SuccessAttempt { get; private set; }
        // 有审批时通知 pending 启动；无审批时才是 holding 启动。Dispose 默认以 EOF 结束此唯一读。
        public TaskCompletionSource SingleReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
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

            if (type == "auth_success" && OnSuccessWrite is not null)
            {
                Assert.True(AuthSuccessFrame.TryParse(payload.Span, out var success, out _));
                SuccessAttempt = Assert.IsType<AuthSuccessFrame>(success);
                OnSuccessWrite();
            }

            // 旧用例未设置 hook 时仍同步完成；新用例的挂起/异常必须由此 await 传播。
            if (OnWriteAsync is not null)
            {
                await OnWriteAsync(type).ConfigureAwait(false);
            }
            AcceptedFrames.Add(type);
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (WriteAttempts.Count > 0 && WriteAttempts[^1] == "auth_success")
            {
                OnSuccessFlush?.Invoke();
            }
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

            // 必须先消费完整 response，才允许启动且仅启动一次 pending/holding 单字节读。
            Assert.Equal(1, buffer.Length);
            PendingReadCalls++;
            Assert.Equal(1, PendingReadCalls);
            SingleReadStarted.TrySetResult();
            // 故意不注册取消，供边界竞态用例确保首次 Stop 后仍需消费 gate 故障并复核 caller。
            return new ValueTask<int>(_pendingRead.Task);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (ReadExceptionOnDispose is OperationCanceledException cancelled)
                {
                    _pendingRead.TrySetCanceled(cancelled.CancellationToken);
                }
                else if (ReadExceptionOnDispose is { } failure)
                {
                    _pendingRead.TrySetException(failure);
                }
                else
                {
                    _pendingRead.TrySetResult(ReadResultOnDispose);
                }
            }
            base.Dispose(disposing);
        }
    }
}
