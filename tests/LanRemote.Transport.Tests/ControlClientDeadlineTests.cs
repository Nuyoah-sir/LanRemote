using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

public sealed partial class ControlClientConnectorTests
{
    [Theory(Timeout = 30_000)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Success_Mac_Final_Check_Rejects_Expired_Window_Without_Timer_Dispatch(
        bool pending, bool wrongProof)
    {
        ClientDeadlineClock clock = new();
        TimeSpan approvalBudget = TimeSpan.FromSeconds(3);
        TimeSpan prefixBudget = TimeSpan.FromMilliseconds(1700);
        TimeSpan payloadBudget = TimeSpan.FromMilliseconds(1100);
        TimeSpan finalBudget = pending ? approvalBudget : ClientOptions.MachineWindow;
        List<SuccessClockCheckpoint> checkpoints = new();
        bool successPayloadDisposed = false;
        await using TlsScenario scenario = TlsScenario.Scripted(async (peer, ct) =>
        {
            await peer.SendChallengeAsync(ct);
            byte[] transcript = await peer.ReadAndVerifyResponseAsync(SessionPermission.Control, ct);
            if (pending)
            {
                await peer.SendPendingAsync(ct);
                ClientTimer approval = await clock.WaitForTimerAsync(approvalBudget, cancellationToken: ct);
                _ = await clock.WaitForTimerAsync(approvalBudget, after: approval, cancellationToken: ct);
            }
            else
            {
                ClientTimer challengePrefix = await clock.WaitForTimerAsync(prefixBudget, cancellationToken: ct);
                ClientTimer offerPrefix = await clock.WaitForTimerAsync(prefixBudget, after: challengePrefix, cancellationToken: ct);
                _ = await clock.WaitForTimerAsync(prefixBudget, after: offerPrefix, cancellationToken: ct);
            }

            // 已进入最终回复的前缀等待，之前的 challenge/pending payload timer 均已释放。
            // 仅当这次 success 的 payload timer 释放后才计数：ReadFrameAsync 此时已收齐
            // payload，且已通过窗口与分段时限检查；不能用服务端 WriteAsync 完成替代收齐证据。
            clock.TimerDisposed = timer =>
            {
                if (timer.Budget != payloadBudget)
                {
                    return;
                }

                Assert.False(successPayloadDisposed, "success payload 的观察边界只能进入一次。");
                successPayloadDisposed = true;
                clock.TimestampObserved = sampled =>
                {
                    // 从明确的 payload 释放事件起，现有同步路径依次为：ReadReplyAsync
                    // 解析后检查、VerifyAndCreateSession 的权限/MAC 前检查、MAC 比较后检查。
                    // 使用具名阶段而非全连接 GetTimestamp 魔数；产品若插入新的取时点，
                    // 此白盒序列必须随之复核，或由主代理提供 internal 阶段 observer。
                    SuccessClockCheckpoint checkpoint = (SuccessClockCheckpoint)(checkpoints.Count + 1);
                    checkpoints.Add(checkpoint);
                    if (checkpoint == SuccessClockCheckpoint.PermissionCheckedBeforeMac)
                    {
                        // 本次权限检查仍拿到旧采样值；此后单调时间已到期，即使删掉
                        // MAC 后检查，也不能连测试的到期事件一起删掉。timer 不派发。
                        clock.Advance(finalBudget, fireTimers: false);
                    }
                    return sampled;
                };
            };

            byte[] proof = ScriptedPeer.ServerProof(transcript, SessionPermission.Control);
            if (wrongProof)
            {
                proof[0] ^= 0x80;
            }
            await peer.SendAsync(peer.SuccessWithProof(SessionPermission.Control, proof), ct);
            await peer.AssertClosedWithoutDataAsync(ct);
        });
        scenario.Start();
        await AssertRejectedAsync(scenario, scenario.ConnectAsync(
                options: ClientOptions with { ApprovalWindow = approvalBudget },
                timeouts: Timeouts(prefixMs: 1700, payloadMs: 1100), clock: clock),
            pending ? "client-approval-timeout" : "client-machine-timeout",
            pending ? "等待远端审批超时，请重试。" : "远端认证等待超时，请重试。");

        // 错误 proof 也必须先报告超时，避免把仅会话构造后的检查误当成 MAC 后检查。
        Assert.True(successPayloadDisposed);
        Assert.Equal(new[]
        {
            SuccessClockCheckpoint.SuccessParsed,
            SuccessClockCheckpoint.PermissionCheckedBeforeMac,
            SuccessClockCheckpoint.MacCompared,
        }, checkpoints);
        Assert.Equal(finalBudget.Ticks, clock.MonotonicTimestamp);
        Assert.Equal(0, clock.TimerCallbacks);
        Assert.Equal(0, clock.ActiveTimerCount);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(400, 15_000)]
    [InlineData(5_000, 800)]
    public async Task Late_Offer_Cannot_Renew_Parent_Or_Challenge_Window_Without_Timer_Dispatch(
        int machineMs, int challengeMs)
    {
        ClientDeadlineClock clock = new();
        TimeSpan prefixBudget = TimeSpan.FromMilliseconds(170);
        TimeSpan payloadBudget = TimeSpan.FromMilliseconds(110);
        TimeSpan deadline = TimeSpan.FromMilliseconds(Math.Min(machineMs, challengeMs));
        await using TlsScenario scenario = TlsScenario.Scripted(async (peer, ct) =>
        {
            await peer.SendAsync(peer.Challenge(expiresMs: challengeMs), ct);
            ClientTimer challengePrefix = await clock.WaitForTimerAsync(prefixBudget, cancellationToken: ct);
            ClientTimer offerPrefix = await clock.WaitForTimerAsync(prefixBudget, after: challengePrefix, cancellationToken: ct);
            byte[] offer = peer.Offer();
            byte[] prefix = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)offer.Length);
            await peer.Stream.WriteAsync(prefix, ct);
            _ = await clock.WaitForTimerAsync(payloadBudget, after: offerPrefix, cancellationToken: ct);
            clock.Advance(deadline, fireTimers: false);
            await peer.Stream.WriteAsync(offer, ct);
            await peer.AssertClosedWithoutDataAsync(ct);
        });
        scenario.Start();
        await AssertRejectedAsync(scenario, scenario.ConnectAsync(
            options: ClientOptions with { MachineWindow = TimeSpan.FromMilliseconds(machineMs) },
            timeouts: Timeouts(prefixMs: 170, payloadMs: 110), clock: clock),
            "client-machine-timeout", "远端认证等待超时，请重试。");
        Assert.Equal(deadline.Ticks, clock.MonotonicTimestamp);
        Assert.Equal(0, clock.TimerCallbacks);
        Assert.Equal(0, clock.ActiveTimerCount);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(700, 15_000, false)]
    [InlineData(700, 15_000, true)]
    [InlineData(5_000, 800, false)]
    [InlineData(5_000, 800, true)]
    public async Task Offer_Parse_Final_Check_Rejects_Expired_Original_Window_After_Timely_Payload(
        int machineMs, int challengeMs, bool invalidOffer)
    {
        CatalogDeadlineScenario scenario = new("offer", machineMs, challengeMs, invalidOffer);
        try
        {
            Task<AuthenticatedControlSession> client = scenario.Start();
            await scenario.AssertRejectedAsync(client);
            // payload 的读后窗口/分段检查已通过并释放 timer，才推进时间；不是迟到 payload。
            Assert.True(scenario.OfferPayloadDisposed);
            Assert.Equal(TimeSpan.FromMilliseconds(280).Ticks, scenario.OfferPayloadDisposedAt);
            Assert.Equal(1, scenario.NegotiationChecks);
            Assert.Equal(new[] { "channel_hello" }, scenario.Stream.WrittenTypes);
            Assert.Equal(1, scenario.Stream.FlushCalls);
            // 无效 Offer 校准：只删除解析后检查必须暴露 catalog 错误，不能由写前末检救活。
        }
        finally
        {
            await scenario.FinishAsync();
        }
    }

    [Theory(Timeout = 30_000)]
    [InlineData(700, 15_000, "write")]
    [InlineData(700, 15_000, "flush")]
    [InlineData(5_000, 800, "write")]
    [InlineData(5_000, 800, "flush")]
    public async Task Accept_Write_Or_Flush_Completion_Rejects_Expired_Original_Window_Without_Timer_Dispatch(
        int machineMs, int challengeMs, string boundary)
    {
        CatalogDeadlineScenario scenario = new(boundary, machineMs, challengeMs);
        try
        {
            Task<AuthenticatedControlSession> client = scenario.Start();
            await scenario.Stream.BoundaryEntered.Task.WaitAsync(Guard);
            Assert.False(client.IsCompleted);
            Assert.False(scenario.Stream.OriginalBoundary.IsCompleted);
            Assert.Equal(2, scenario.NegotiationChecks);
            Assert.Equal(TimeSpan.FromMilliseconds(280).Ticks, scenario.Clock.MonotonicTimestamp);
            Assert.False(scenario.Connection.IsCloseRequested);
            Assert.Equal(boundary == "write" ? 1 : 2, scenario.Stream.FlushCalls);
            Assert.False(scenario.Stream.AcceptToken.IsCancellationRequested);

            // 原写/flush Task 尚未完成；先把客观单调时间推进到原截止，再放行实际 IO。
            scenario.ExpireWithoutTimerDispatch();
            Assert.False(scenario.Stream.AcceptToken.IsCancellationRequested);
            Assert.False(scenario.Stream.OriginalBoundary.IsCompleted);
            scenario.Stream.ReleaseBoundary();
            await scenario.AssertRejectedAsync(client);
            Assert.True(scenario.Stream.OriginalBoundary.IsCompletedSuccessfully);
            Assert.Equal(scenario.DeadlineAt, scenario.Stream.BoundaryCompletedAt);
            Assert.Equal(3, scenario.NegotiationChecks);
            Assert.Equal(new[] { "channel_hello", "display_catalog_accept" }, scenario.Stream.WrittenTypes);
            Assert.Equal(2, scenario.Stream.FlushCalls);
            // 自然时间证据不宣称独立覆盖该检查：删 Accept 后检查仍会被 binding 后检查拒绝。
        }
        finally
        {
            await scenario.FinishAsync();
        }
    }

    [Theory(Timeout = 30_000)]
    [InlineData(700, 15_000, "binding")]
    [InlineData(5_000, 800, "binding")]
    [InlineData(700, 15_000, "write")]
    [InlineData(5_000, 800, "write")]
    [InlineData(700, 15_000, "flush")]
    [InlineData(5_000, 800, "flush")]
    public async Task Isolated_Catalog_Negotiation_Rejects_Expired_Original_Window_Without_Timer_Dispatch(
        int machineMs, int challengeMs, string boundary)
    {
        CatalogDeadlineScenario scenario = new(boundary, machineMs, challengeMs, isolated: true);
        try
        {
            Task<DisplayCatalogAgreementBinding> negotiation = scenario.StartIsolatedNegotiation();
            if (boundary != "binding")
            {
                await scenario.Stream.BoundaryEntered.Task.WaitAsync(Guard);
                Assert.False(negotiation.IsCompleted);
                Assert.False(scenario.Stream.OriginalBoundary.IsCompleted);
                Assert.Equal(2, scenario.NegotiationChecks);
                Assert.Equal(TimeSpan.FromMilliseconds(280).Ticks, scenario.Clock.MonotonicTimestamp);
                Assert.Equal(boundary == "write" ? 0 : 1, scenario.Stream.FlushCalls);
                scenario.ExpireWithoutTimerDispatch();
                Assert.False(scenario.Stream.AcceptToken.IsCancellationRequested);
                Assert.False(scenario.Stream.OriginalBoundary.IsCompleted);
                scenario.Stream.ReleaseBoundary();
            }
            ControlClientAuthenticationException error = await Assert.ThrowsAsync<ControlClientAuthenticationException>(
                () => negotiation.WaitAsync(Guard));
            Assert.Equal("client-machine-timeout", error.Rejection);
            Assert.Equal("远端认证等待超时，请重试。", error.DisplayMessage);
            Assert.Null(error.InnerException);
            Assert.Equal(TaskStatus.Faulted, negotiation.Status);
            Assert.Equal(scenario.DeadlineAt, scenario.Clock.MonotonicTimestamp);
            if (boundary == "binding")
            {
                Assert.Equal(4, scenario.NegotiationChecks);
                Assert.Equal(scenario.DeadlineAt, scenario.BindingCheckSample);
                Assert.Equal(TimeSpan.FromMilliseconds(280).Ticks, scenario.AcceptCheckSample);
                Assert.True(scenario.ExpiredBeforeBindingCheck);
            }
            else
            {
                Assert.Equal(3, scenario.NegotiationChecks);
                Assert.True(scenario.Stream.OriginalBoundary.IsCompletedSuccessfully);
                Assert.Equal(scenario.DeadlineAt, scenario.Stream.BoundaryCompletedAt);
            }
            Assert.True(scenario.OfferPayloadDisposed);
            Assert.Equal(new[] { "display_catalog_accept" }, scenario.Stream.WrittenTypes);
            Assert.Equal(1, scenario.Stream.FlushCalls);
            Assert.False(scenario.Stream.AcceptToken.IsCancellationRequested);
            Assert.False(scenario.CallerCancellationRequested);
            Assert.All(scenario.Stream.IoTokens, token => Assert.False(token.IsCancellationRequested));
            Assert.Equal(0, scenario.Clock.TimerCallbacks);
            // 反射直接调用既有协商边界，不声称覆盖完整认证或测得 binding 同步构造耗时。
            // binding到期发生在第3次有效采样之后；单删第4次末检仍保留到期事件并返回对象。
            // write/flush单删Accept后检查仍有binding检查；双删才应以过期返回对象触红。
        }
        finally
        {
            await scenario.FinishAsync();
        }
    }

    [Theory(Timeout = 30_000)]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task Caller_Key_Is_Copied_Before_First_Await_And_Never_Cleared(string outcome)
    {
        ClientDeadlineClock clock = new();
        TimeSpan approvalBudget = TimeSpan.FromSeconds(3);
        byte[] callerKey = GoodKey.ToArray();
        byte[] changedKey = Enumerable.Repeat((byte)0xA5, callerKey.Length).ToArray();
        TaskCompletionSource keyChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using TlsScenario scenario = TlsScenario.Scripted(async (peer, ct) =>
        {
            // 即使本机 TLS 很快，也不允许客户端在调用方改写数组前收到 challenge。
            await keyChanged.Task.WaitAsync(Guard, ct);
            await peer.SendChallengeAsync(ct);
            // 独立 TLS oracle 始终使用 GoodKey，不读取调用方改写后的数组。
            byte[] transcript = await peer.ReadAndVerifyResponseAsync(SessionPermission.Control, ct);
            if (outcome == "cancel")
            {
                await peer.SendPendingAsync(ct);
            }
            else
            {
                byte[] proof = ScriptedPeer.ServerProof(transcript, SessionPermission.Control);
                if (outcome == "failure")
                {
                    proof[0] ^= 0x80;
                }
                await peer.SendAsync(peer.SuccessWithProof(SessionPermission.Control, proof), ct);
            }
            await peer.AssertClosedWithoutDataAsync(ct);
        });
        scenario.Start();
        using CancellationTokenSource caller = new();
        Task<AuthenticatedControlSession> clientTask = scenario.ConnectAsync(
            key: callerKey, options: ClientOptions with { ApprovalWindow = approvalBudget },
            cancellationToken: caller.Token, clock: clock);
        // 这里不得插入 await：必须在 ConnectAsync 返回任务后立即覆盖调用方输入。
        changedKey.CopyTo(callerKey, 0);
        keyChanged.SetResult();

        if (outcome == "success")
        {
            using AuthenticatedControlSession session = await clientTask.WaitAsync(Guard);
            Assert.Contains(TestOnlyControlSessionSecrets.GetOwnedToken(session), value => value != 0);
            Assert.Equal(changedKey, callerKey);
            session.Dispose();
            await scenario.AssertServerFinishedAsync();
        }
        else if (outcome == "failure")
        {
            await AssertRejectedAsync(scenario, clientTask, "client-server-proof-mismatch", ProofFailureMessage);
        }
        else
        {
            _ = await clock.WaitForTimerAsync(approvalBudget);
            Assert.False(clientTask.IsCompleted);
            caller.Cancel();
            OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => clientTask.WaitAsync(Guard));
            Assert.True(error.CancellationToken.IsCancellationRequested);
            Assert.True(clientTask.IsCanceled);
            await scenario.AssertServerFinishedAsync();
        }

        Assert.Equal(changedKey, callerKey);
        Assert.Equal(0, clock.ActiveTimerCount);
    }

    [Fact(Timeout = 30_000)]
    public async Task Approval_Long_Prefix_Wait_Does_Not_Expand_100ms_Partial_Payload_Budget()
    {
        ClientDeadlineClock clock = new();
        TimeSpan approvalBudget = TimeSpan.FromSeconds(3);
        TimeSpan prefixWait = TimeSpan.FromMilliseconds(600);
        TimeSpan payloadBudget = TimeSpan.FromMilliseconds(100);
        TaskCompletionSource<ClientTimer> payloadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using TlsScenario scenario = TlsScenario.Scripted(async (peer, ct) =>
        {
            await peer.SendChallengeAsync(ct);
            byte[] transcript = await peer.ReadAndVerifyResponseAsync(SessionPermission.Control, ct);
            await peer.SendPendingAsync(ct);
            ClientTimer approval = await clock.WaitForTimerAsync(approvalBudget, cancellationToken: ct);
            ClientTimer prefix = await clock.WaitForTimerAsync(approvalBudget, after: approval, cancellationToken: ct);
            clock.Advance(prefixWait, fireTimers: false);

            byte[] payload = peer.Success(transcript, SessionPermission.Control);
            byte[] partialFrame = new byte[TransportConstants.LengthPrefixBytes + 1];
            BinaryPrimitives.WriteUInt32BigEndian(partialFrame, (uint)payload.Length);
            partialFrame[^1] = payload[0];
            await peer.Stream.WriteAsync(partialFrame, ct);
            ClientTimer timer = await clock.WaitForTimerAsync(payloadBudget, after: prefix, cancellationToken: ct);
            Assert.Equal(prefixWait.Ticks, timer.StartedAt - approval.StartedAt);
            payloadStarted.SetResult(timer);
            // 不再发送剩余 payload，也不主动断线；由测试推进客户端同一时钟上的分段截止。
            await peer.AssertClosedWithoutDataAsync(ct);
        });
        scenario.Start();
        Task<AuthenticatedControlSession> client = scenario.ConnectAsync(
            options: ClientOptions with { ApprovalWindow = approvalBudget },
            timeouts: Timeouts(prefixMs: 100, payloadMs: 100), clock: clock);
        ClientTimer payloadTimer = await payloadStarted.Task.WaitAsync(Guard);
        clock.Advance(payloadBudget - TimeSpan.FromTicks(1), fireTimers: false);
        Assert.False(client.IsCompleted);
        Assert.Equal(0, clock.TimerCallbacks);
        clock.Advance(TimeSpan.FromTicks(1), fireTimers: true);
        await AssertRejectedAsync(scenario, client, "client-frame-timeout", "远端认证等待超时，请重试。");
        Assert.Equal(payloadBudget, payloadTimer.Budget);
        Assert.True(payloadTimer.IsDisposed);
        Assert.Equal((prefixWait + payloadBudget).Ticks, clock.MonotonicTimestamp);
        Assert.Equal(1, clock.TimerCallbacks);
        Assert.Equal(0, clock.ActiveTimerCount);
    }

    [Fact(Timeout = 30_000)]
    public async Task Approval_Fragmented_Prefix_And_Payload_Cannot_Renew_Outer_Deadline()
    {
        ClientDeadlineClock clock = new();
        TimeSpan approvalBudget = TimeSpan.FromSeconds(3);
        TimeSpan prefixFragmentInterval = TimeSpan.FromMilliseconds(600);
        TimeSpan payloadFragmentInterval = TimeSpan.FromMilliseconds(200);
        await using TlsScenario scenario = TlsScenario.Scripted(async (peer, ct) =>
        {
            await peer.SendChallengeAsync(ct);
            byte[] transcript = await peer.ReadAndVerifyResponseAsync(SessionPermission.Control, ct);
            await peer.SendPendingAsync(ct);
            ClientTimer approval = await clock.WaitForTimerAsync(approvalBudget, cancellationToken: ct);
            ClientTimer prefixTimer = await clock.WaitForTimerAsync(approvalBudget, after: approval, cancellationToken: ct);
            byte[] payload = peer.Success(transcript, SessionPermission.Control);
            byte[] prefix = new byte[TransportConstants.LengthPrefixBytes];
            BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)payload.Length);
            for (int i = 0; i < prefix.Length; i++)
            {
                clock.Advance(prefixFragmentInterval, fireTimers: false);
                await peer.Stream.WriteAsync(prefix.AsMemory(i, 1), ct);
            }

            TimeSpan prefixElapsed = prefixFragmentInterval * prefix.Length;
            TimeSpan remaining = approvalBudget - prefixElapsed;
            ClientTimer payloadTimer = await clock.WaitForTimerAsync(remaining, after: prefixTimer, cancellationToken: ct);
            // payload timer 创建证明完整前缀已被客户端消费；没有把服务端写出等同于收到。
            Assert.Equal(prefixElapsed.Ticks, payloadTimer.StartedAt - approval.StartedAt);
            Assert.Equal(remaining, payloadTimer.Budget);
            int fragmentBytes = payload.Length / 3;
            clock.Advance(payloadFragmentInterval, fireTimers: false);
            await peer.Stream.WriteAsync(payload.AsMemory(0, fragmentBytes), ct);
            clock.Advance(payloadFragmentInterval, fireTimers: false);
            await peer.Stream.WriteAsync(payload.AsMemory(fragmentBytes, fragmentBytes), ct);
            // 保留最后一段不发；推进到原始审批起点 + 3 秒，不能从最近字节重新计时。
            clock.Advance(remaining - payloadFragmentInterval * 2, fireTimers: true);
            await peer.AssertClosedWithoutDataAsync(ct);
        });
        scenario.Start();
        await AssertRejectedAsync(scenario, scenario.ConnectAsync(
                options: ClientOptions with { ApprovalWindow = approvalBudget }, clock: clock),
            "client-approval-timeout", "等待远端审批超时，请重试。");
        Assert.Equal(approvalBudget.Ticks, clock.MonotonicTimestamp);
        Assert.True(clock.TimerCallbacks > 0);
        Assert.Equal(0, clock.ActiveTimerCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClearSessionToken_Clears_Owned_Buffer_Without_Changing_Source_Arrays(bool parse)
    {
        byte[] sourceToken = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        byte[] sourceProof = Enumerable.Range(33, 32).Select(value => (byte)value).ToArray();
        byte[] expectedToken = sourceToken.ToArray();
        byte[] expectedProof = sourceProof.ToArray();
        AuthSuccessFrame frame = new(SessionPermission.Control, sourceProof, sourceToken, 15_000);
        byte[] sourcePayload = frame.Serialize();
        byte[] expectedPayload = sourcePayload.ToArray();
        if (parse)
        {
            frame.ClearSessionToken();
            Assert.True(AuthSuccessFrame.TryParse(sourcePayload, out AuthSuccessFrame? parsed, out string? rejection), rejection);
            frame = Assert.IsType<AuthSuccessFrame>(parsed);
        }
        ReadOnlyMemory<byte> ownedToken = frame.SessionToken;
        Assert.Equal(expectedToken, ownedToken.ToArray());
        frame.ClearSessionToken();
        frame.ClearSessionToken();
        // 只断言可持有的 byte[]/Memory；不声称能观察或清零 JSON/Base64 的堆字符串。
        Assert.All(ownedToken.ToArray(), value => Assert.Equal((byte)0, value));
        Assert.Equal(expectedToken, sourceToken);
        Assert.Equal(expectedProof, sourceProof);
        Assert.Equal(expectedProof, frame.ServerProof.ToArray());
        Assert.Equal(expectedPayload, sourcePayload);
    }

    [Fact(Timeout = 30_000)]
    public async Task Successful_Session_Token_Remains_Independent_After_Frame_Token_Is_Cleared()
    {
        TaskCompletionSource<(AuthSuccessFrame Frame, byte[] SourceToken)> sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using TlsScenario scenario = TlsScenario.Scripted(async (peer, ct) =>
        {
            await peer.SendChallengeAsync(ct);
            byte[] transcript = await peer.ReadAndVerifyResponseAsync(SessionPermission.Control, ct);
            AuthSuccessFrame frame = new(SessionPermission.Control,
                ScriptedPeer.ServerProof(transcript, SessionPermission.Control), peer.Token, 15_000);
            await peer.SendAsync(frame.Serialize(), ct);
            frame.ClearSessionToken();
            sent.SetResult((frame, peer.Token));
            await peer.AssertClosedWithoutDataAsync(ct);
        });
        scenario.Start();
        using AuthenticatedControlSession session = await scenario.ConnectAsync().WaitAsync(Guard);
        var (frame, sourceToken) = await sent.Task.WaitAsync(Guard);
        byte[] expectedToken = sourceToken.ToArray();
        Assert.Contains(expectedToken, value => value != 0);
        Assert.All(frame.SessionToken.ToArray(), value => Assert.Equal((byte)0, value));
        byte[] sessionToken = TestOnlyControlSessionSecrets.GetOwnedToken(session);
        // 客户端返回前自己的 parsed success 也已在 finally 清理；此处仍须保有独立 token。
        Assert.Equal(expectedToken, sessionToken.ToArray());
        frame.ClearSessionToken();
        Assert.Equal(expectedToken, sessionToken.ToArray());
        Assert.Equal(expectedToken, sourceToken);
        session.Dispose();
        Assert.All(sessionToken.ToArray(), value => Assert.Equal((byte)0, value));
        Assert.Equal(expectedToken, sourceToken);
        await scenario.AssertServerFinishedAsync();
    }

    // 公开入口与孤立协商共用夹具；后者打开既有私有协商边界隔离外层末检。
    private sealed class CatalogDeadlineScenario
    {
        private static readonly Type NegotiationStateMachine = typeof(ControlClientConnector)
            .GetMethod("NegotiateCatalogAsync", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        private readonly string _boundary;
        private readonly bool _isolated;
        private readonly TcpClient _tcp = new(AddressFamily.InterNetwork);
        private readonly CancellationTokenSource _caller = new();
        private readonly ConnectionTarget _target;
        private readonly ControlClientAuthOptions _options;
        private readonly byte[] _key = GoodKey.ToArray();
        private readonly List<AuthenticatedControlSession> _constructed = new();
        private Task<AuthenticatedControlSession>? _client;
        private AuthenticatedControlSession? _returned;
        private Task<DisplayCatalogAgreementBinding>? _negotiation;
        private IDisposable? _bindingParent;
        private IDisposable? _bindingWindow;
        private readonly int _challengeMs;
        internal ClientDeadlineClock Clock { get; } = new();
        internal CatalogDeadlineStream Stream { get; }
        internal TlsConnection Connection { get; }
        internal bool OfferPayloadDisposed { get; private set; }
        internal long OfferPayloadDisposedAt { get; private set; }
        internal int NegotiationChecks { get; private set; }
        internal long BindingCheckSample { get; private set; }
        internal long AcceptCheckSample { get; private set; }
        internal bool ExpiredBeforeBindingCheck { get; private set; }
        internal long DeadlineAt { get; }
        internal bool CallerCancellationRequested => _caller.IsCancellationRequested;

        internal CatalogDeadlineScenario(string boundary, int machineMs, int challengeMs,
            bool invalidOffer = false, bool isolated = false)
        {
            _boundary = boundary;
            _isolated = isolated;
            _challengeMs = challengeMs;
            _options = ClientOptions with { MachineWindow = TimeSpan.FromMilliseconds(machineMs) };
            // hello 原起点是0，challenge完整载荷在200ms收齐；子窗口不能偷换回父起点或协商结束。
            DeadlineAt = TimeSpan.FromMilliseconds(Math.Min(machineMs, 200 + challengeMs)).Ticks;
            byte[] pin = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
            Assert.True(ConnectionTarget.TryCreate(ServerDeviceId, IPAddress.Loopback, 12345,
                Convert.ToHexString(pin), out ConnectionTarget? target));
            _target = target!;
            Assert.True(ConnectionIdentity.TryCreate(_target, pin, out ConnectionIdentity? identity));
            Stream = new(Clock, boundary, pin, challengeMs, invalidOffer, isolated);
            Connection = new(identity!, _tcp, Stream);
            Clock.TimerDisposed = timer =>
            {
                if (!Stream.OfferPayloadCompleted || timer.Budget != TimeSpan.FromMilliseconds(450)) return;
                Assert.False(OfferPayloadDisposed);
                OfferPayloadDisposed = true;
                OfferPayloadDisposedAt = Clock.MonotonicTimestamp;
                Assert.Equal(TimeSpan.FromMilliseconds(280).Ticks, OfferPayloadDisposedAt);
                if (_boundary == "offer") ExpireWithoutTimerDispatch();
            };
            Clock.TimestampSampling = () =>
            {
                if (!OfferPayloadDisposed) return;
                // 取最近的客户端 async 状态机，排除 ReadBoundedFrameAsync 内部和调用方的兜底。
                // 不依赖源码行号/IL偏移；这里只定位协商方法的4个直属 GetRemaining 调用。
                Type? nearest = new StackTrace().GetFrames()
                    .Select(frame => frame.GetMethod()?.DeclaringType)
                    .FirstOrDefault(type => type?.DeclaringType == typeof(ControlClientConnector)
                        && type.Name.StartsWith("<", StringComparison.Ordinal));
                if (nearest != NegotiationStateMachine) return;
                NegotiationChecks++;
                if (_boundary == "binding" && NegotiationChecks == 4)
                {
                    Assert.True(ExpiredBeforeBindingCheck);
                    Assert.Equal(DeadlineAt, Clock.MonotonicTimestamp);
                    BindingCheckSample = Clock.MonotonicTimestamp;
                }
            };
            Clock.TimestampObserved = sampled =>
            {
                if (_boundary == "binding" && NegotiationChecks == 3 && !ExpiredBeforeBindingCheck)
                {
                    // 第3次检查在线性化采样时仍有效；推进事件独立于待测的第4次检查。
                    AcceptCheckSample = sampled;
                    ExpireWithoutTimerDispatch();
                    ExpiredBeforeBindingCheck = true;
                }
                return sampled;
            };
        }

        internal Task<AuthenticatedControlSession> Start()
        {
            Assert.Null(_client);
            ControlClientConnector connector = new((target, _, clock, token) =>
            {
                Assert.Same(_target, target);
                Assert.Same(Clock, clock);
                Assert.Equal(_caller.Token, token);
                return Task.FromResult(Connection);
            }, _constructed.Add);
            _client = connector.ConnectAndAuthenticateAsync(_target, ClientDeviceId, ClientName, _key,
                SessionPermission.Control, _options, Timeouts(prefixMs: 170, payloadMs: 450), Clock, _caller.Token);
            return _client;
        }

        internal Task<DisplayCatalogAgreementBinding> StartIsolatedNegotiation()
        {
            Assert.True(_isolated);
            Assert.Null(_negotiation);
            Type windowType = typeof(ControlClientConnector).GetNestedType("ClientAuthWindow", BindingFlags.NonPublic)!;
            // 白盒固定父起点0和已收齐challenge起点200ms；不重新实现GetRemaining或绑定构造。
            _bindingParent = (IDisposable)Activator.CreateInstance(windowType,
                Clock, _options.MachineWindow, _caller.Token, "client-machine-timeout", (long?)0, null)!;
            Clock.Advance(TimeSpan.FromMilliseconds(200), fireTimers: false);
            _bindingWindow = (IDisposable)Activator.CreateInstance(windowType,
                Clock, TimeSpan.FromMilliseconds(_challengeMs), _caller.Token, "client-machine-timeout",
                (long?)Clock.MonotonicTimestamp, _bindingParent)!;
            MethodInfo negotiate = typeof(ControlClientConnector)
                .GetMethod("NegotiateCatalogAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
            _negotiation = (Task<DisplayCatalogAgreementBinding>)negotiate.Invoke(null,
                [Connection, new FrameReader(Stream), Stream.SessionId, _bindingWindow,
                    Timeouts(prefixMs: 170, payloadMs: 450)])!;
            return _negotiation;
        }

        internal void ExpireWithoutTimerDispatch()
        {
            Assert.True(Clock.MonotonicTimestamp < DeadlineAt);
            Clock.Advance(TimeSpan.FromTicks(DeadlineAt - Clock.MonotonicTimestamp), fireTimers: false);
            Assert.Equal(0, Clock.TimerCallbacks);
        }

        internal async Task AssertRejectedAsync(Task<AuthenticatedControlSession> client)
        {
            Assert.Same(_client, client);
            ControlClientAuthenticationException error = await Assert.ThrowsAsync<ControlClientAuthenticationException>(async () =>
            {
                _returned = await client.WaitAsync(Guard);
            });
            Assert.Equal("client-machine-timeout", error.Rejection);
            Assert.Equal("远端认证等待超时，请重试。", error.DisplayMessage);
            Assert.Equal(error.DisplayMessage, error.Message);
            Assert.Null(error.InnerException);
            Assert.Equal(TaskStatus.Faulted, client.Status);
            Assert.Null(_returned);
            Assert.Empty(_constructed);
            Assert.DoesNotContain("auth_response", Stream.WrittenTypes);
            Assert.True(Stream.OfferPayloadCompleted);
            Assert.True(OfferPayloadDisposed);
            Assert.Equal(DeadlineAt, Clock.MonotonicTimestamp);
            Assert.False(_caller.IsCancellationRequested);
            Assert.All(Stream.IoTokens, token => Assert.False(token.IsCancellationRequested));
            Assert.Equal(0, Clock.TimerCallbacks);
            Assert.Equal(0, Clock.ActiveTimerCount);
            Assert.True(Connection.IsCloseRequested);
            Assert.Equal(1, Stream.DisposeCalls);
            Assert.Empty(Connection.CleanupErrors);
            Assert.Equal(GoodKey, _key);
        }

        internal async Task FinishAsync()
        {
            // 断言失败也先放行原IO任务，再观察公开入口任务，最后关闭并join所有可能交付的会话。
            Stream.ReleaseBoundary();
            _caller.Cancel();
            Clock.TimestampSampling = null;
            Clock.TimestampObserved = null;
            Clock.TimerDisposed = null;
            try
            {
                await ObserveCatalogTaskAsync(Stream.OriginalBoundary).WaitAsync(Guard);
                if (_client is not null) await ObserveCatalogTaskAsync(_client).WaitAsync(Guard);
                if (_negotiation is not null) await ObserveCatalogTaskAsync(_negotiation).WaitAsync(Guard);
            }
            finally
            {
                _bindingWindow?.Dispose();
                _bindingParent?.Dispose();
                try
                {
                    foreach (AuthenticatedControlSession session in _constructed)
                        await session.CloseAndJoinAsync().WaitAsync(Guard);
                    if (_client is { IsCompletedSuccessfully: true })
                        await _client.Result.CloseAndJoinAsync().WaitAsync(Guard);
                }
                finally
                {
                    try
                    {
                        await Connection.CloseAsync().WaitAsync(Guard);
                        Assert.Equal(0, Clock.ActiveTimerCount);
                    }
                    finally { _tcp.Dispose(); _caller.Dispose(); }
                }
            }
        }

        private static async Task ObserveCatalogTaskAsync(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception) { /* 只观察生产/原IO故障；WaitAsync 的 Guard 超时由调用方保留。 */ }
        }
    }

    private sealed class CatalogDeadlineStream : SslStream
    {
        private readonly ClientDeadlineClock _clock;
        private readonly string _boundary;
        private readonly Guid _sessionId = Guid.NewGuid();
        internal Guid SessionId => _sessionId;
        private readonly Queue<(string Kind, byte[] Wire)> _input = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _offset;
        internal TaskCompletionSource BoundaryEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task OriginalBoundary { get; private set; } = Task.CompletedTask;
        internal List<string> WrittenTypes { get; } = new();
        internal List<CancellationToken> IoTokens { get; } = new();
        internal CancellationToken AcceptToken { get; private set; }
        internal bool OfferPayloadCompleted { get; private set; }
        internal long BoundaryCompletedAt { get; private set; }
        internal int FlushCalls { get; private set; }
        internal int DisposeCalls { get; private set; }

        internal CatalogDeadlineStream(ClientDeadlineClock clock, string boundary, byte[] pin,
            int challengeMs, bool invalidOffer, bool isolated) : base(new MemoryStream(), leaveInnerStreamOpen: false)
        {
            _clock = clock;
            _boundary = boundary;
            if (!isolated)
                Enqueue("challenge", new AuthChallengeV2(_sessionId, ServerDeviceId, new byte[32], pin, challengeMs).Serialize());
            Enqueue("offer", invalidOffer ? "{"u8.ToArray() : DisplayCatalogAgreementFrame.SerializeOffer(_sessionId));
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IoTokens.Add(cancellationToken);
            if (_input.Count == 0) return ValueTask.FromResult(0);
            var frame = _input.Peek();
            int count = Math.Min(buffer.Length, frame.Wire.Length - _offset);
            frame.Wire.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            if (_offset == frame.Wire.Length)
            {
                _clock.Advance(TimeSpan.FromMilliseconds(frame.Kind == "challenge" ? 200 : 80), fireTimers: false);
                if (frame.Kind == "offer") OfferPayloadCompleted = true;
                _input.Dequeue();
                _offset = 0;
            }
            return ValueTask.FromResult(count);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IoTokens.Add(cancellationToken);
            Assert.Equal((uint)(buffer.Length - 4), BinaryPrimitives.ReadUInt32BigEndian(buffer.Span));
            using JsonDocument document = JsonDocument.Parse(buffer[4..]);
            string type = document.RootElement.GetProperty("type").GetString()!;
            WrittenTypes.Add(type);
            if (type == "display_catalog_accept")
            {
                Assert.True(OfferPayloadCompleted);
                Assert.True(DisplayCatalogAgreementFrame.TryParseAccept(buffer.Span[4..], _sessionId, out _, out _));
                AcceptToken = cancellationToken;
                if (_boundary == "write") return new ValueTask(PauseBoundary());
            }
            return ValueTask.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IoTokens.Add(cancellationToken);
            FlushCalls++;
            if (WrittenTypes[^1] == "display_catalog_accept" && _boundary == "flush")
            {
                Assert.Equal(AcceptToken, cancellationToken);
                return PauseBoundary();
            }
            return Task.CompletedTask;
        }

        private Task PauseBoundary()
        {
            Assert.False(BoundaryEntered.Task.IsCompleted);
            OriginalBoundary = CompleteBoundaryAsync();
            BoundaryEntered.TrySetResult();
            return OriginalBoundary;
        }

        private async Task CompleteBoundaryAsync()
        {
            await _release.Task.ConfigureAwait(false);
            BoundaryCompletedAt = _clock.MonotonicTimestamp;
        }

        private void Enqueue(string kind, byte[] payload)
        {
            byte[] wire = new byte[4 + payload.Length];
            BinaryPrimitives.WriteUInt32BigEndian(wire, (uint)payload.Length);
            payload.CopyTo(wire, 4);
            _input.Enqueue((kind, wire));
        }

        internal void ReleaseBoundary() => _release.TrySetResult();
        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeCalls++;
            base.Dispose(disposing);
        }
    }

    private enum SuccessClockCheckpoint
    {
        SuccessParsed = 1,
        PermissionCheckedBeforeMac,
        MacCompared,
    }

    // 真实 TLS 校验证书仍需当前 UTC；单调时间单独冻结，只有测试显式推进。
    // 所有 timer 都委托给已有 ManualDeadlineClock，另记录预算和创建/释放事件。
    private sealed class ClientDeadlineClock : TimeProvider
    {
        private readonly ManualDeadlineClock _inner = new(DateTimeOffset.UtcNow);
        private readonly object _gate = new();
        private readonly List<ClientTimer> _timers = new();
        private TaskCompletionSource _timerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action<ClientTimer>? _timerDisposed;
        private Func<long, long>? _timestampObserved;
        private Action? _timestampSampling;
        private int _timerCallbacks;

        public Action<ClientTimer>? TimerDisposed { set => Volatile.Write(ref _timerDisposed, value); }
        public Func<long, long>? TimestampObserved { set => Volatile.Write(ref _timestampObserved, value); }
        public Action? TimestampSampling { set => Volatile.Write(ref _timestampSampling, value); }
        public int TimerCallbacks => Volatile.Read(ref _timerCallbacks);
        public int ActiveTimerCount => _inner.TimerCount;
        public long MonotonicTimestamp => _inner.GetTimestamp();
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
        public override long GetTimestamp()
        {
            Volatile.Read(ref _timestampSampling)?.Invoke();
            long sampled = _inner.GetTimestamp();
            return Volatile.Read(ref _timestampObserved)?.Invoke(sampled) ?? sampled;
        }
        public void Advance(TimeSpan elapsed, bool fireTimers) => _inner.Advance(elapsed, fireTimers);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            long startedAt = _inner.GetTimestamp();
            ITimer inner = _inner.CreateTimer(callbackState =>
            {
                Interlocked.Increment(ref _timerCallbacks);
                callback(callbackState);
            }, state, dueTime, period);
            ClientTimer timer = new(inner, dueTime, startedAt,
                disposed => Volatile.Read(ref _timerDisposed)?.Invoke(disposed));
            lock (_gate)
            {
                _timers.Add(timer);
                TaskCompletionSource signal = _timerCreated;
                _timerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
                signal.TrySetResult();
            }
            return timer;
        }

        public async Task<ClientTimer> WaitForTimerAsync(
            TimeSpan budget, ClientTimer? after = null, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    int start = after is null ? 0 : _timers.IndexOf(after) + 1;
                    ClientTimer? timer = _timers.Skip(start).FirstOrDefault(candidate => candidate.Budget == budget);
                    if (timer is not null)
                    {
                        return timer;
                    }
                    changed = _timerCreated.Task;
                }
                await changed.WaitAsync(Guard, cancellationToken);
            }
        }
    }

    private sealed class ClientTimer(
        ITimer inner, TimeSpan budget, long startedAt, Action<ClientTimer> disposed) : ITimer
    {
        private int _disposed;
        public TimeSpan Budget { get; } = budget;
        public long StartedAt { get; } = startedAt;
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                inner.Dispose();
                disposed(this);
            }
        }
        public ValueTask DisposeAsync()
        {
            Dispose();
            return inner.DisposeAsync();
        }
    }
}
