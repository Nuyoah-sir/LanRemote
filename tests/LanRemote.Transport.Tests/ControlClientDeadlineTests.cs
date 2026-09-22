using System.Buffers.Binary;
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
                _ = await clock.WaitForTimerAsync(prefixBudget, after: challengePrefix, cancellationToken: ct);
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
            Assert.Contains(session.SessionToken.ToArray(), value => value != 0);
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
            // 不再发送剩余 payload，也不主动断线。让真实 FrameReader 的 100ms 分段
            // CancelAfter 结束读取；它是被测超时，不是用于猜测协议阶段的固定 Sleep。
            await peer.AssertClosedWithoutDataAsync(ct);
        });
        scenario.Start();
        Task<AuthenticatedControlSession> client = scenario.ConnectAsync(
            options: ClientOptions with { ApprovalWindow = approvalBudget },
            timeouts: Timeouts(prefixMs: 100, payloadMs: 100), clock: clock);
        ClientTimer payloadTimer = await payloadStarted.Task.WaitAsync(Guard);
        await AssertRejectedAsync(scenario, client, "client-frame-timeout", "远端认证等待超时，请重试。");
        Assert.Equal(payloadBudget, payloadTimer.Budget);
        Assert.True(payloadTimer.IsDisposed);
        Assert.Equal(prefixWait.Ticks, clock.MonotonicTimestamp);
        Assert.Equal(0, clock.TimerCallbacks);
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
        ReadOnlyMemory<byte> sessionToken = session.SessionToken;
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
        private int _timerCallbacks;

        public Action<ClientTimer>? TimerDisposed { set => Volatile.Write(ref _timerDisposed, value); }
        public Func<long, long>? TimestampObserved { set => Volatile.Write(ref _timestampObserved, value); }
        public int TimerCallbacks => Volatile.Read(ref _timerCallbacks);
        public int ActiveTimerCount => _inner.TimerCount;
        public long MonotonicTimestamp => _inner.GetTimestamp();
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
        public override long GetTimestamp()
        {
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
