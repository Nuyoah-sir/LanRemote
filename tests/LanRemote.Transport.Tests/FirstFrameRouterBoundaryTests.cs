using System.Buffers.Binary;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

/// <summary>
/// 调用真实 Router/Registry/关闭句柄的组件边界测试，不反射私有方法。
/// 受控 SSL 流不进行 TLS 握手；不覆盖真实 socket、Control 认证流程或异常聚合形态。
/// await using 的 finally 统一放行所有 I/O 闸门，再 join 路由、关闭及实际 I/O 任务。
/// </summary>
public sealed partial class FirstFrameRouterBoundaryTests
{
    [Fact(Timeout = 30_000)]
    public async Task NotRegistered_Retries_At_40_And_80_Milliseconds_And_Stops_After_Three_Attempts()
    {
        await using BoundaryScenario scenario = new();
        Task run = scenario.Start();
        TimerNotice firstRetry = await scenario.Clock.ReadFirstRetryAsync();
        Assert.Equal(1, scenario.Router.AttachAttempts);
        Assert.Equal(VideoAttachStatus.NotRegistered, scenario.Router.AttachStatus);
        Assert.Equal(0, firstRetry.StartedAt);

        scenario.Clock.Manual.Advance(TimeSpan.FromMilliseconds(39), false);
        Assert.False(run.IsCompleted);
        Assert.Equal(1, scenario.Router.AttachAttempts);
        scenario.Clock.Manual.Advance(TimeSpan.FromMilliseconds(1), false);
        firstRetry.Dispatch();

        TimerNotice secondRetry = await scenario.Clock.ReadTimerAsync();
        Assert.Equal(TimeSpan.FromMilliseconds(40), secondRetry.DueTime);
        Assert.Equal(TimeSpan.FromMilliseconds(40).Ticks, secondRetry.StartedAt);
        Assert.Equal(2, scenario.Router.AttachAttempts);
        scenario.Clock.Manual.Advance(TimeSpan.FromMilliseconds(39), false);
        Assert.Equal(2, scenario.Router.AttachAttempts);
        Assert.False(run.IsCompleted);
        scenario.Clock.Manual.Advance(TimeSpan.FromMilliseconds(1), false);
        secondRetry.Dispatch();

        await run.WaitAsync(Guard);
        Assert.Equal(3, scenario.Router.AttachAttempts);
        Assert.Equal(VideoAttachStatus.NotRegistered, scenario.Router.AttachStatus);
        Assert.Equal("video-attach-rejected", scenario.Router.Rejection);
        AssertNoOutput(scenario);
        Assert.Equal(0, scenario.Clock.Manual.TimerCount);
        Assert.Equal(1, scenario.Stream.DisposeCount);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(1)] // prefix
    [InlineData(2)] // payload
    public async Task Host_Cancellation_During_First_Hello_Read_Reports_Host_Token_Not_Deadline_Token(int heldRead)
    {
        await using BoundaryScenario scenario = new(holdRead: heldRead, cancelAwareRead: true);
        Task run = scenario.Start();
        await scenario.Stream.ReadEntered.Task.WaitAsync(Guard);
        CancellationToken hostToken = scenario.HostStop.Token;
        CancellationToken deadlineToken = scenario.Stream.HeldReadToken;
        Assert.True(deadlineToken.CanBeCanceled);
        Assert.NotEqual(hostToken, deadlineToken);
        Assert.False(hostToken.IsCancellationRequested);
        Assert.False(deadlineToken.IsCancellationRequested);
        Assert.False(run.IsCompleted);
        Assert.Equal(heldRead, scenario.Stream.ReadCalls);
        Assert.Equal(heldRead == 1 ? 0 : 4, scenario.Stream.BytesRead);

        // 不释放读 gate、不推进时钟：必须是 Host 取消令真实 ReadAsync 携带 deadline token 退出。
        await scenario.HostStop.CancelAsync().WaitAsync(Guard);
        OperationCanceledException readError = await scenario.Stream.ReadCancelled.Task.WaitAsync(Guard);
        Assert.Equal(deadlineToken, readError.CancellationToken);
        Assert.True(deadlineToken.IsCancellationRequested);
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => run.WaitAsync(Guard));

        Assert.True(run.IsCanceled);
        Assert.Equal(heldRead, scenario.Stream.ReadCalls);
        Assert.Equal(heldRead == 1 ? 0 : 4, scenario.Stream.BytesRead);
        Assert.Equal(0, scenario.Router.AttachAttempts);
        Assert.Null(scenario.Router.AttachStatus);
        Assert.Null(scenario.Router.Rejection);
        AssertNoOutput(scenario);
        Assert.Equal(0, scenario.Clock.Manual.GetTimestamp());
        Assert.Equal(0, scenario.Clock.Manual.TimerCount);
        Assert.Equal(1, scenario.Stream.DisposeCount);
        Assert.Equal(hostToken, error.CancellationToken);
        Assert.NotEqual(deadlineToken, error.CancellationToken);
    }

    [Fact(Timeout = 30_000)]
    public async Task Host_Cancellation_During_NotRegistered_Retry_Reports_Host_Token_Not_Deadline_Token()
    {
        await using BoundaryScenario scenario = new();
        Task run = scenario.Start();
        Assert.Equal(TimeSpan.FromSeconds(1), (await scenario.Clock.ReadTimerAsync()).DueTime);
        Assert.Equal(TimeSpan.FromSeconds(1), (await scenario.Clock.ReadTimerAsync()).DueTime);
        TimerNotice registrationDeadline = await scenario.Clock.ReadTimerAsync();
        Assert.Equal(TimeSpan.FromMilliseconds(100), registrationDeadline.DueTime);
        CancellationToken deadlineToken = Assert.IsType<AuthenticationDeadline>(registrationDeadline.State).Token;
        TimerNotice retry = await scenario.Clock.ReadTimerAsync();
        Assert.Equal(TimeSpan.FromMilliseconds(40), retry.DueTime);
        Assert.Equal(0, retry.StartedAt);
        Task delay = Assert.IsAssignableFrom<Task>(retry.State);
        Assert.False(delay.IsCompleted);
        Assert.False(run.IsCompleted);
        Assert.Equal(1, scenario.Router.AttachAttempts);
        Assert.Equal(VideoAttachStatus.NotRegistered, scenario.Router.AttachStatus);
        CancellationToken hostToken = scenario.HostStop.Token;
        Assert.True(deadlineToken.CanBeCanceled);
        Assert.NotEqual(hostToken, deadlineToken);
        Assert.False(hostToken.IsCancellationRequested);
        Assert.False(deadlineToken.IsCancellationRequested);

        // 已观测到真实重试 timer 和未完成的 Task.Delay；不派发 timer，不以看门狗取消充当预期错误。
        await scenario.HostStop.CancelAsync().WaitAsync(Guard);
        OperationCanceledException delayError = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => delay.WaitAsync(Guard));
        Assert.Equal(deadlineToken, delayError.CancellationToken);
        Assert.True(deadlineToken.IsCancellationRequested);
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => run.WaitAsync(Guard));

        Assert.True(run.IsCanceled);
        Assert.Equal(1, scenario.Router.AttachAttempts);
        Assert.Equal(VideoAttachStatus.NotRegistered, scenario.Router.AttachStatus);
        Assert.Null(scenario.Router.Rejection);
        AssertNoOutput(scenario);
        Assert.Equal(0, scenario.Clock.Manual.GetTimestamp());
        Assert.Equal(0, scenario.Clock.Manual.TimerCount);
        Assert.Equal(1, scenario.Stream.DisposeCount);
        Assert.Equal(hostToken, error.CancellationToken);
        Assert.NotEqual(deadlineToken, error.CancellationToken);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Registration_Between_Retries_Can_Attach_On_The_Second_Or_Third_Attempt(int winningAttempt)
    {
        await using BoundaryScenario scenario = new();
        Task run = scenario.Start();
        TimerNotice retry = await scenario.Clock.ReadFirstRetryAsync();
        if (winningAttempt == 3)
        {
            scenario.Clock.Manual.Advance(TimeSpan.FromMilliseconds(40), false);
            retry.Dispatch();
            retry = await scenario.Clock.ReadTimerAsync();
            Assert.Equal(TimeSpan.FromMilliseconds(40), retry.DueTime);
            Assert.Equal(2, scenario.Router.AttachAttempts);
        }

        using var registration = scenario.Register();
        scenario.Clock.Manual.Advance(TimeSpan.FromMilliseconds(40), false);
        retry.Dispatch();
        await run.WaitAsync(Guard);

        Assert.Equal(winningAttempt, scenario.Router.AttachAttempts);
        Assert.Equal(VideoAttachStatus.Attached, scenario.Router.AttachStatus);
        Assert.Null(scenario.Router.Rejection);
        AssertAck(scenario);
        Assert.Equal(1, scenario.Source.Calls);
        Assert.True(scenario.Source.SawCompletedFlush);
        Assert.Equal(1, scenario.Registry.ActiveSessionCount);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public async Task Registration_Wait_Ends_At_Exactly_100_Milliseconds_Even_Without_Deadline_Callback(
        long offsetTicks, bool accepted)
    {
        await using BoundaryScenario scenario = new();
        Task run = scenario.Start();
        TimerNotice retry = await scenario.Clock.ReadFirstRetryAsync();
        using var registration = scenario.Register();

        // 只交付已经迟到的 retry 通知，不派发 registration deadline；到点必须靠绝对取时拒绝。
        scenario.Clock.Manual.Advance(TimeSpan.FromMilliseconds(100) + TimeSpan.FromTicks(offsetTicks), false);
        retry.Dispatch();
        await run.WaitAsync(Guard);

        Assert.Equal(accepted ? 2 : 1, scenario.Router.AttachAttempts);
        Assert.Equal(accepted ? VideoAttachStatus.Attached : VideoAttachStatus.NotRegistered,
            scenario.Router.AttachStatus);
        if (accepted)
        {
            AssertAck(scenario);
            Assert.Equal(1, scenario.Source.Calls);
        }
        else
        {
            Assert.Equal("channel-timeout-or-revoked", scenario.Router.Rejection);
            AssertNoOutput(scenario);
            Assert.Equal(VideoAttachStatus.Attached, scenario.ProbeAttach());
        }
        Assert.Equal(0, scenario.Clock.Manual.TimerCount);
    }

    [Theory(Timeout = 30_000)]
    [InlineData("proof")]
    [InlineData("identity")]
    [InlineData("consumed")]
    [InlineData("expired")]
    [InlineData("control-cancelled")]
    public async Task Registered_Nonretryable_Status_Stops_After_One_Attempt(string reason)
    {
        await using BoundaryScenario scenario = new(badProof: reason == "proof");
        using CancellationTokenSource control = new();
        using var registration = scenario.Register(
            controlToken: control.Token,
            connectionId: reason == "identity" ? scenario.Connection.Security.ConnectionId : null,
            expiresInMs: reason == "expired" ? 50 : 15_000);
        if (reason == "consumed")
            Assert.Equal(VideoAttachStatus.Attached, scenario.ProbeAttach());
        if (reason == "expired")
            scenario.Clock.Manual.Advance(TimeSpan.FromMilliseconds(50), false);
        if (reason == "control-cancelled")
            control.Cancel();

        await scenario.Start().WaitAsync(Guard);
        VideoAttachStatus expected = reason switch
        {
            "proof" => VideoAttachStatus.InvalidProof,
            "identity" => VideoAttachStatus.IdentityMismatch,
            "consumed" => VideoAttachStatus.AlreadyAttached,
            "expired" => VideoAttachStatus.Expired,
            "control-cancelled" => VideoAttachStatus.Cancelled,
            _ => throw new InvalidOperationException()
        };
        Assert.Equal(expected, scenario.Router.AttachStatus);
        Assert.Equal(1, scenario.Router.AttachAttempts);
        Assert.Equal("video-attach-rejected", scenario.Router.Rejection);
        AssertNoOutput(scenario);
        Assert.Equal(0, scenario.Clock.Manual.TimerCount);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unavailable_After_Unregister_Or_Same_Id_ABA_Is_Not_Retried(bool replace)
    {
        await using BoundaryScenario scenario = new();
        WindowSampleClock windowClock = new(scenario.Clock.Manual);
        using var original = scenario.Register(windowClock: windowClock);
        SessionRegistry.SessionRegistration? replacement = null;
        int finalSample = windowClock.TimestampReads + 2;
        windowClock.AfterSample = read =>
        {
            if (read != finalSample) return;
            windowClock.AfterSample = null;
            original.Dispose();
            // 有意复用同一 token，使错误重试能够成功，而不是被另一个 InvalidProof 掩盖。
            if (replace) replacement = scenario.Register();
        };

        try
        {
            await scenario.Start().WaitAsync(Guard);
            Assert.Equal(VideoAttachStatus.Unavailable, scenario.Router.AttachStatus);
            Assert.Equal(1, scenario.Router.AttachAttempts);
            Assert.Equal("video-attach-rejected", scenario.Router.Rejection);
            AssertNoOutput(scenario);
            Assert.Equal(replace ? 1 : 0, scenario.Registry.ActiveSessionCount);
            Assert.Equal(replace ? VideoAttachStatus.Attached : VideoAttachStatus.NotRegistered,
                scenario.ProbeAttach());
        }
        finally
        {
            windowClock.AfterSample = null;
            replacement?.Dispose();
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Ack_Write_And_Flush_Must_Both_Complete_Before_First_Source_Call()
    {
        await using BoundaryScenario scenario = new(holdWrite: true, holdFlush: true);
        using var registration = scenario.Register();
        Task run = scenario.Start();

        await scenario.Stream.WriteEntered.Task.WaitAsync(Guard);
        Assert.Equal(VideoAttachStatus.Attached, scenario.Router.AttachStatus);
        Task childJoin = registration.VideoCompletion;
        Assert.False(childJoin.IsCompleted);
        Assert.Equal(0, scenario.Source.Calls);
        Assert.False(scenario.Stream.WriteCompleted);
        Assert.False(scenario.Stream.FlushEntered.Task.IsCompleted);
        Assert.False(run.IsCompleted);
        scenario.Stream.ReleaseWrite();

        await scenario.Stream.FlushEntered.Task.WaitAsync(Guard);
        Assert.True(scenario.Stream.WriteCompleted);
        Assert.False(scenario.Stream.FlushCompleted);
        Assert.Equal(0, scenario.Source.Calls);
        Assert.False(run.IsCompleted);
        scenario.Stream.ReleaseFlush();

        await run.WaitAsync(Guard);
        AssertAck(scenario);
        Assert.Equal(1, scenario.Source.Calls);
        Assert.True(scenario.Source.SawCompletedFlush);
        Assert.Equal(SessionId, scenario.Source.SessionId);
        Assert.Same(childJoin, registration.VideoCompletion);
        Assert.True(childJoin.IsCompletedSuccessfully);
        Assert.Equal(1, scenario.Stream.DisposeCount);
        Assert.True(scenario.Stream.PeerReadCompleted);
    }

    [Theory(Timeout = 30_000)]
    [InlineData("stage")]
    [InlineData("envelope")]
    [InlineData("elapsed")]
    [InlineData("cancel")]
    public async Task PostProof_Budget_Or_Host_Cancellation_Joins_Close_Before_Completing_Reservation(
        string boundary)
    {
        NeverCreateFactory factory = new();
        await using BoundaryScenario scenario = new(producerFactory: factory, holdDispose: true);
        WindowSampleClock windowClock = new(scenario.Clock.Manual);
        using var registration = scenario.Register(windowClock: windowClock);
        Task noChild = registration.VideoCompletion;
        int finalProofSample = windowClock.TimestampReads + 2;
        int afterAttachReads = -1;
        windowClock.AfterSample = read =>
        {
            if (read != finalProofSample) return;
            windowClock.AfterSample = null;
            afterAttachReads = scenario.Clock.TimestampReads;
            if (boundary == "stage")
                scenario.Clock.Manual.Advance(VideoSessionOptions.RegistrationWait, false);
        };
        scenario.Clock.AfterSample = read =>
        {
            if (afterAttachReads < 0 || read != afterAttachReads + (boundary == "elapsed" ? 2 : 1)) return;
            scenario.Clock.AfterSample = null;
            if (boundary == "cancel") scenario.HostStop.Cancel();
            else if (boundary == "envelope")
                scenario.Clock.Manual.Advance(NewTimeouts().PreAuthEnvelopeTimeout, false);
            else if (boundary == "elapsed")
                scenario.Clock.Manual.Advance(VideoSessionOptions.RegistrationWait, false);
        };

        try
        {
            Task run = scenario.Start();
            // 后检已消费资格，但只允许 CloseHandle 的原始 Dispose 进入，暂不让物理关闭完成。
            await scenario.Stream.DisposeEntered.Task.WaitAsync(Guard);
            Task close = scenario.Connection.CloseAsync();
            Assert.False(close.IsCompleted);
            Assert.False(scenario.Stream.Closed.Task.IsCompleted);
            Assert.False(run.IsCompleted);
            Assert.Equal(finalProofSample, windowClock.TimestampReads);
            Assert.Equal(VideoAttachStatus.Attached, scenario.Router.AttachStatus);
            Assert.Equal(1, scenario.Router.AttachAttempts);
            AssertNoOutput(scenario);
            Assert.Equal(0, factory.Calls);
            Assert.NotSame(noChild, registration.VideoCompletion);
            Assert.False(registration.VideoCompletion.IsCompleted);
            Assert.Equal(0, scenario.Stream.DisposeCount);
            Assert.Equal(2, scenario.Stream.ReadCalls);
            Assert.Equal(VideoAttachStatus.AlreadyAttached, scenario.ProbeAttach(freshNonce: true));

            scenario.Stream.ReleaseDispose();
            if (boundary == "cancel")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Guard));
            else
                await run.WaitAsync(Guard);

            Assert.True(close.IsCompletedSuccessfully);
            Assert.True(scenario.Stream.Closed.Task.IsCompletedSuccessfully);
            Assert.True(registration.VideoCompletion.IsCompletedSuccessfully);
            Assert.Equal(1, scenario.Stream.DisposeCount);
            Assert.Equal(VideoAttachStatus.AlreadyAttached, scenario.ProbeAttach(freshNonce: true));
        }
        finally
        {
            scenario.Stream.ReleaseDispose();
            windowClock.AfterSample = null;
            scenario.Clock.AfterSample = null;
        }
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ack_IO_Failure_Does_Not_Restore_Consumed_Eligibility(bool failFlush)
    {
        // 只断言 ACK 失败的资格边界，不依赖 Router 的异常聚合格式。
        NeverCreateFactory factory = new();
        await using BoundaryScenario scenario = new(failWrite: !failFlush, failFlush: failFlush,
            producerFactory: factory);
        using var registration = scenario.Register();
        Task noChild = registration.VideoCompletion;
        await scenario.Start().WaitAsync(Guard);

        Assert.Equal(VideoAttachStatus.Attached, scenario.Router.AttachStatus);
        Assert.NotSame(noChild, registration.VideoCompletion);
        Assert.True(registration.VideoCompletion.IsCompletedSuccessfully);
        Assert.Equal(1, scenario.Router.AttachAttempts);
        Assert.Equal(0, scenario.Source.Calls);
        Assert.Equal(0, factory.Calls);
        Assert.Equal(failFlush, scenario.Stream.WriteCompleted);
        Assert.False(scenario.Stream.FlushCompleted);
        Assert.Equal(VideoAttachStatus.AlreadyAttached, scenario.ProbeAttach());
        Assert.Equal(VideoAttachStatus.AlreadyAttached, scenario.ProbeAttach(freshNonce: true));
        Assert.Equal(1, scenario.Registry.ActiveSessionCount);
        Assert.Equal(1, scenario.Stream.DisposeCount);
        Assert.True(scenario.Stream.PeerReadCompleted);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Revocation_While_Ack_Is_Pending_Joins_IO_And_Never_Starts_Source(
        bool cancelHost, bool holdFlush)
    {
        await using BoundaryScenario scenario = new(holdWrite: !holdFlush, holdFlush: holdFlush);
        using var registration = scenario.Register();
        Task run = scenario.Start();
        await (holdFlush ? scenario.Stream.FlushEntered.Task : scenario.Stream.WriteEntered.Task).WaitAsync(Guard);
        Assert.Equal(VideoAttachStatus.Attached, scenario.Router.AttachStatus);
        Task childJoin = registration.VideoCompletion;
        Assert.False(childJoin.IsCompleted);

        if (cancelHost) await scenario.HostStop.CancelAsync();
        else registration.Dispose();
        await scenario.Stream.Closed.Task.WaitAsync(Guard);
        Assert.Equal(0, scenario.Source.Calls);
        Assert.False(run.IsCompleted);
        Assert.False(childJoin.IsCompleted);
        Assert.Equal(cancelHost ? VideoAttachStatus.AlreadyAttached : VideoAttachStatus.NotRegistered,
            scenario.ProbeAttach(freshNonce: true));

        // 模拟已取消后 I/O 才成功返回；不能把完成通知当作恢复资格或启动 source 的理由。
        scenario.Stream.ReleaseWrite();
        scenario.Stream.ReleaseFlush();
        if (cancelHost)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Guard));
        else
            await run.WaitAsync(Guard);

        Assert.Equal(0, scenario.Source.Calls);
        Assert.Equal(1, scenario.Router.AttachAttempts);
        Assert.Same(childJoin, registration.VideoCompletion);
        Assert.True(childJoin.IsCompletedSuccessfully);
        Assert.Equal(cancelHost ? VideoAttachStatus.AlreadyAttached : VideoAttachStatus.NotRegistered,
            scenario.ProbeAttach(freshNonce: true));
        Assert.True(scenario.Stream.PeerReadCompleted);
        Assert.Equal(1, scenario.Stream.DisposeCount);
    }

    [Theory(Timeout = 30_000)]
    [InlineData("prefix", -1, true)]
    [InlineData("prefix", 0, false)]
    [InlineData("prefix", 1, false)]
    [InlineData("payload", -1, true)]
    [InlineData("payload", 0, false)]
    [InlineData("payload", 1, false)]
    [InlineData("envelope", -1, true)]
    [InlineData("envelope", 0, false)]
    [InlineData("envelope", 1, false)]
    public async Task First_Hello_Requires_Strictly_Before_Stage_And_Envelope_Deadlines(
        string stage, long offsetTicks, bool accepted)
    {
        TransportTimeouts timeouts = NewTimeouts(
            prefixMs: stage == "payload" ? 500 : 100,
            payloadMs: stage == "prefix" ? 500 : 100,
            envelopeMs: stage == "envelope" ? 100 : 1000);
        await using BoundaryScenario scenario = new(timeouts: timeouts);
        using var registration = scenario.Register();
        scenario.Stream.BeforeReadCompletes = read =>
        {
            TimeSpan elapsed = (stage, read) switch
            {
                ("prefix", 1) => TimeSpan.FromMilliseconds(100) + TimeSpan.FromTicks(offsetTicks),
                ("payload", 1) => TimeSpan.FromMilliseconds(30),
                ("payload", 2) => TimeSpan.FromMilliseconds(100) + TimeSpan.FromTicks(offsetTicks),
                ("envelope", 1) => TimeSpan.FromMilliseconds(60),
                ("envelope", 2) => TimeSpan.FromMilliseconds(40) + TimeSpan.FromTicks(offsetTicks),
                _ => TimeSpan.Zero
            };
            scenario.Clock.Manual.Advance(elapsed, false);
        };

        await scenario.Start().WaitAsync(Guard);
        if (accepted)
        {
            Assert.Equal(VideoAttachStatus.Attached, scenario.Router.AttachStatus);
            AssertAck(scenario);
            Assert.Equal(1, scenario.Source.Calls);
        }
        else
        {
            Assert.Equal("channel-timeout-or-revoked", scenario.Router.Rejection);
            Assert.Null(scenario.Router.AttachStatus);
            Assert.Equal(0, scenario.Router.AttachAttempts);
            Assert.Equal(stage == "prefix" ? 1 : 2, scenario.Stream.ReadCalls);
            AssertNoOutput(scenario);
            Assert.Equal(VideoAttachStatus.Attached, scenario.ProbeAttach());
        }
        Assert.Equal(0, scenario.Clock.Manual.TimerCount);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(0u)]
    [InlineData(4097u)]
    [InlineData(0x80000000u)]
    [InlineData(uint.MaxValue)]
    public async Task Invalid_First_Length_Closes_Without_Reading_Or_Draining_Payload(uint length)
    {
        byte[] wire = new byte[20];
        BinaryPrimitives.WriteUInt32BigEndian(wire, length);
        await using BoundaryScenario scenario = new(wire: wire);
        await scenario.Start().WaitAsync(Guard);

        Assert.Equal("channel-closed-or-invalid", scenario.Router.Rejection);
        Assert.Equal(1, scenario.Stream.ReadCalls);
        Assert.Equal(4, scenario.Stream.BytesRead);
        Assert.Equal(0, scenario.Router.AttachAttempts);
        Assert.Null(scenario.Router.AttachStatus);
        AssertNoOutput(scenario);
        Assert.Equal(1, scenario.Stream.DisposeCount);
    }

    [Fact(Timeout = 30_000)]
    public async Task Reentrant_Run_Is_Rejected_Without_Closing_The_First_Runs_Stream()
    {
        await using BoundaryScenario scenario = new(holdRead: 1);
        using var registration = scenario.Register();
        Task first = scenario.Start();
        await scenario.Stream.ReadEntered.Task.WaitAsync(Guard);
        Assert.True(scenario.Connection.HasCloseAuthority);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scenario.Router.RunAsync(scenario.Connection, CancellationToken.None));
        Assert.False(first.IsCompleted);
        Assert.False(scenario.Stream.Closed.Task.IsCompleted);
        Assert.Equal(0, scenario.Stream.DisposeCount);
        Assert.Equal(1, scenario.Stream.ReadCalls);
        scenario.Stream.ReleaseRead();
        await first.WaitAsync(Guard);

        Assert.Equal(VideoAttachStatus.Attached, scenario.Router.AttachStatus);
        Assert.Equal(1, scenario.Source.Calls);
        AssertAck(scenario);
        int reads = scenario.Stream.ReadCalls;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scenario.Router.RunAsync(scenario.Connection, CancellationToken.None));
        Assert.Equal(reads, scenario.Stream.ReadCalls);
        Assert.Equal(1, scenario.Stream.DisposeCount);
    }

    [Fact(Timeout = 30_000)]
    public async Task Public_AcceptedConnection_Without_Close_Authority_Is_Rejected_Before_IO()
    {
        await using BoundaryScenario scenario = new();
        AcceptedConnection publicConnection = new(scenario.Connection.Security, scenario.Stream);
        Assert.False(publicConnection.HasCloseAuthority);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scenario.Router.RunAsync(publicConnection, CancellationToken.None));
        Assert.Equal(0, scenario.Stream.ReadCalls);
        Assert.Equal(0, scenario.Router.AttachAttempts);
        AssertNoOutput(scenario);
        Assert.False(scenario.Stream.Closed.Task.IsCompleted);
        Assert.Equal(0, scenario.Stream.DisposeCount);
    }

    private static void AssertNoOutput(BoundaryScenario scenario)
    {
        Assert.Empty(scenario.Stream.Writes);
        Assert.Equal(0, scenario.Source.Calls);
        Assert.False(scenario.Stream.FlushEntered.Task.IsCompleted);
    }

    private static void AssertAck(BoundaryScenario scenario)
    {
        byte[] wire = Assert.Single(scenario.Stream.Writes);
        Assert.Equal((uint)(wire.Length - 4), BinaryPrimitives.ReadUInt32BigEndian(wire));
        Assert.True(VideoAttachAckFrame.TryParse(wire.AsSpan(4), out var ack, out string? rejection), rejection);
        Assert.Equal(SessionId, ack!.SessionId);
        Assert.True(scenario.Stream.WriteCompleted);
        Assert.True(scenario.Stream.FlushCompleted);
    }
}
