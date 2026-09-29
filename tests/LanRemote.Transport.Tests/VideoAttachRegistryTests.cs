using System.Collections;
using System.Net;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using LanRemote.Core.Models;

namespace LanRemote.Transport.Tests;

public sealed class VideoAttachRegistryTests
{
    private static readonly Guid SessionId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly IPAddress RemoteAddress = IPAddress.Parse("192.168.10.20");
    private static readonly IPAddress LocalAddress = IPAddress.Parse("192.168.10.10");

    [Fact]
    public void First_Valid_Proof_Attaches_Only_The_Requested_Video_Connection()
    {
        ManualDeadlineClock clock = new();
        SessionRegistry registry = new();
        byte[] token = Token();
        byte[] pin = Pin();
        byte[] nonce = Nonce();
        ConnectionSecurityContext video = Security(pin);
        using var registration = Register(registry, new VideoAttachWindow(clock, 15_000), token, pin);

        byte[] proof = IndependentProof(token, SessionId, nonce, pin);
        // Python uuid.UUID.bytes + hmac/hashlib 独立计算的黄金值。
        Assert.Equal(Convert.FromHexString("ade67adfb898996be504dec92b6ba6bb449772e41e67d5f36210d666f77022d9"), proof);
        Assert.Equal(VideoAttachStatus.Attached, registry.TryAttachVideo(
            SessionId, video, nonce, proof, default, out var lease));

        Assert.NotNull(lease);
        Assert.Equal(SessionId, lease.SessionId);
        Assert.Equal(video.ConnectionId, lease.VideoConnectionId);
        Assert.False(lease.Revoked.IsCompleted);
        Assert.Equal(1, registry.ActiveSessionCount);
        Assert.Equal(SessionId, Assert.Single(registry.Snapshot()).SessionId);
        Assert.Equal(0, clock.TimerCount);
    }

    [Fact]
    public void Wrong_Proof_And_Replayed_Proof_With_A_Different_Nonce_Do_Not_Consume_Eligibility()
    {
        ManualDeadlineClock clock = new();
        SessionRegistry registry = new();
        byte[] token = Token();
        byte[] pin = Pin();
        byte[] nonce = Nonce();
        byte[] otherNonce = Nonce();
        otherNonce[0] ^= 1;
        byte[] proof = IndependentProof(token, SessionId, nonce, pin);
        byte[] wrongProof = (byte[])proof.Clone();
        wrongProof[^1] ^= 1;
        ConnectionSecurityContext video = Security(pin);
        using var registration = Register(registry, new VideoAttachWindow(clock, 15_000), token, pin);

        Assert.Equal(VideoAttachStatus.InvalidProof,
            registry.TryAttachVideo(SessionId, video, nonce, wrongProof, default, out var wrongLease));
        Assert.Null(wrongLease);
        Assert.Equal(VideoAttachStatus.InvalidProof,
            registry.TryAttachVideo(SessionId, video, otherNonce, proof, default, out var replayLease));
        Assert.Null(replayLease);
        Assert.Equal(VideoAttachStatus.Attached,
            registry.TryAttachVideo(SessionId, video, nonce, proof, default, out var lease));
        Assert.NotNull(lease);
    }

    [Fact]
    public void Duplicate_And_Fresh_Nonce_Proofs_Cannot_Restore_Consumed_Eligibility()
    {
        ManualDeadlineClock clock = new();
        SessionRegistry registry = new();
        byte[] token = Token();
        byte[] pin = Pin();
        byte[] nonce = Nonce();
        byte[] proof = IndependentProof(token, SessionId, nonce, pin);
        ConnectionSecurityContext video = Security(pin);
        using var registration = Register(registry, new VideoAttachWindow(clock, 15_000), token, pin);
        using CancellationTokenSource caller = new();
        Assert.Equal(VideoAttachStatus.Attached,
            registry.TryAttachVideo(SessionId, video, nonce, proof, caller.Token, out var lease));
        caller.Cancel();

        Assert.Equal(VideoAttachStatus.AlreadyAttached,
            registry.TryAttachVideo(SessionId, video, nonce, proof, default, out var duplicate));
        Assert.Null(duplicate);
        nonce[0] ^= 1;
        Assert.Equal(VideoAttachStatus.AlreadyAttached, registry.TryAttachVideo(
            SessionId, Security(pin), nonce, IndependentProof(token, SessionId, nonce, pin), default, out var fresh));
        Assert.Null(fresh);
        Assert.False(lease!.Revoked.IsCompleted);
    }

    [Fact]
    public async Task Concurrent_Valid_Proofs_Have_Exactly_One_Winner()
    {
        ManualDeadlineClock clock = new();
        SessionRegistry registry = new();
        byte[] token = Token();
        byte[] pin = Pin();
        using var registration = Register(registry, new VideoAttachWindow(clock, 15_000), token, pin);
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var contenders = Enumerable.Range(0, 32).Select(async index =>
        {
            byte[] nonce = Nonce();
            nonce[0] = (byte)index;
            byte[] proof = IndependentProof(token, SessionId, nonce, pin);
            ConnectionSecurityContext video = Security(pin);
            await start.Task;
            VideoAttachStatus status = registry.TryAttachVideo(
                SessionId, video, nonce, proof, default, out var lease);
            return (Status: status, Lease: lease, video.ConnectionId);
        }).ToArray();

        start.SetResult();
        var results = await Task.WhenAll(contenders).WaitAsync(TimeSpan.FromSeconds(10));
        var winner = Assert.Single(results, result => result.Status == VideoAttachStatus.Attached);
        Assert.NotNull(winner.Lease);
        Assert.Equal(winner.ConnectionId, winner.Lease.VideoConnectionId);
        Assert.All(results.Where(result => result.Status != VideoAttachStatus.Attached), result =>
        {
            Assert.Equal(VideoAttachStatus.AlreadyAttached, result.Status);
            Assert.Null(result.Lease);
        });
    }

    [Theory]
    [InlineData("address")]
    [InlineData("pin")]
    [InlineData("connection")]
    [InlineData("ipv6")]
    public void Frozen_Tls_Identity_Must_Match_Control_Without_Consuming_On_Mismatch(string mismatch)
    {
        ManualDeadlineClock clock = new();
        SessionRegistry registry = new();
        byte[] token = Token();
        byte[] pin = Pin();
        byte[] nonce = Nonce();
        ConnectionSecurityContext control = Security(pin);
        byte[] wrongPin = Pin();
        wrongPin[0] ^= 1;
        ConnectionSecurityContext video = mismatch switch
        {
            "address" => Security(pin, IPAddress.Parse("192.168.10.21")),
            "pin" => Security(wrongPin),
            "connection" => control,
            "ipv6" => Security(pin, RemoteAddress.MapToIPv6()),
            _ => throw new InvalidOperationException()
        };
        using var registration = Register(registry, new VideoAttachWindow(clock, 15_000), token, pin,
            connectionId: control.ConnectionId);
        byte[] proof = IndependentProof(token, SessionId, nonce, video.ServerCertificateSha256.ToArray());

        Assert.Equal(VideoAttachStatus.IdentityMismatch,
            registry.TryAttachVideo(SessionId, video, nonce, proof, default, out var rejected));
        Assert.Null(rejected);
        Assert.Equal(VideoAttachStatus.Attached, registry.TryAttachVideo(
            SessionId, Security(pin), nonce, IndependentProof(token, SessionId, nonce, pin), default, out var lease));
        Assert.NotNull(lease);
    }

    [Theory]
    [InlineData(0, 32)]
    [InlineData(15, 32)]
    [InlineData(17, 32)]
    [InlineData(16, 0)]
    [InlineData(16, 31)]
    [InlineData(16, 33)]
    public void Invalid_Attach_Lengths_Do_Not_Consume_Eligibility(int nonceLength, int proofLength)
    {
        ManualDeadlineClock clock = new();
        SessionRegistry registry = new();
        byte[] token = Token();
        byte[] pin = Pin();
        byte[] nonce = Nonce();
        ConnectionSecurityContext video = Security(pin);
        using var registration = Register(registry, new VideoAttachWindow(clock, 15_000), token, pin);

        Assert.Equal(VideoAttachStatus.InvalidInput, registry.TryAttachVideo(
            SessionId, video, new byte[nonceLength], new byte[proofLength], default, out var rejected));
        Assert.Null(rejected);
        Assert.Equal(VideoAttachStatus.Attached, registry.TryAttachVideo(
            SessionId, video, nonce, IndependentProof(token, SessionId, nonce, pin), default, out var lease));
        Assert.NotNull(lease);
    }

    [Fact]
    public void Missing_Entry_Fails_Closed_And_Null_Security_Is_Invalid()
    {
        ManualDeadlineClock clock = new();
        SessionRegistry registry = new();
        byte[] token = Token();
        byte[] pin = Pin();
        byte[] nonce = Nonce();
        byte[] proof = IndependentProof(token, SessionId, nonce, pin);
        ConnectionSecurityContext video = Security(pin);
        Assert.Equal(VideoAttachStatus.NotRegistered,
            registry.TryAttachVideo(SessionId, video, nonce, proof, default, out var missing));
        Assert.Null(missing);

        using var registration = Register(registry, new VideoAttachWindow(clock, 15_000), token, pin);
        Assert.Equal(VideoAttachStatus.InvalidInput,
            registry.TryAttachVideo(SessionId, null!, nonce, proof, default, out var invalid));
        Assert.Null(invalid);
        Assert.Equal(VideoAttachStatus.Attached,
            registry.TryAttachVideo(SessionId, video, nonce, proof, default, out _));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(400)]
    [InlineData(15_000)]
    [InlineData(20_000)]
    [InlineData(int.MaxValue)]
    public void Window_Uses_The_Original_Nonzero_Start_And_Capped_Budget(int expiresInMs)
    {
        ManualDeadlineClock clock = new();
        clock.Advance(TimeSpan.FromDays(3));
        VideoAttachWindow window = new(clock, expiresInMs);
        Assert.False(window.IsExpired);
        clock.Advance(TimeSpan.FromMilliseconds(Math.Min(expiresInMs, 15_000)) - TimeSpan.FromTicks(1), false);
        Assert.False(window.IsExpired);
        clock.Advance(TimeSpan.FromTicks(1), false);
        Assert.True(window.IsExpired);
        Assert.Equal(0, clock.TimerCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Window_Rejects_Nonpositive_Budgets(int expiresInMs)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VideoAttachWindow(new ManualDeadlineClock(), expiresInMs));
    }

    [Fact]
    public void Window_Rejects_Null_Clock_And_Negative_Elapsed_Time()
    {
        Assert.Throws<ArgumentNullException>(() => new VideoAttachWindow(null!, 1));
        ManualDeadlineClock manual = new();
        SampleClock clock = new(manual);
        VideoAttachWindow window = new(clock, 15_000);
        clock.TimestampAdjustment = -1;
        Assert.True(window.IsExpired);

        SessionRegistry registry = new();
        byte[] token = Token();
        byte[] pin = Pin();
        byte[] nonce = Nonce();
        using var registration = Register(registry, window, token, pin);
        Assert.Equal(VideoAttachStatus.Expired, registry.TryAttachVideo(
            SessionId, Security(pin), nonce, IndependentProof(token, SessionId, nonce, pin), default, out var lease));
        Assert.Null(lease);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Register_Does_Not_Reset_The_PreWrite_Window_Even_When_Already_Expired(bool expiredBeforeRegister)
    {
        ManualDeadlineClock clock = new();
        SessionRegistry registry = new();
        byte[] token = Token();
        byte[] pin = Pin();
        byte[] nonce = Nonce();
        VideoAttachWindow preWriteWindow = new(clock, 15_000);
        // Registry 层模拟 write/flush 和写后调度等待；不声称覆盖 ControlAuthSession 接线。
        clock.Advance(TimeSpan.FromSeconds(9), false);
        clock.Advance(TimeSpan.FromSeconds(expiredBeforeRegister ? 6 : 5), false);
        using var registration = Register(registry, preWriteWindow, token, pin);
        Assert.Equal(1, registry.ActiveSessionCount);
        if (!expiredBeforeRegister)
        {
            clock.Advance(TimeSpan.FromSeconds(1), false);
        }

        Assert.Equal(VideoAttachStatus.Expired, registry.TryAttachVideo(
            SessionId, Security(pin), nonce, IndependentProof(token, SessionId, nonce, pin), default, out var lease));
        Assert.Null(lease);
        Assert.Equal(1, registry.ActiveSessionCount);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void Attach_Requires_Strictly_Before_The_Exact_Deadline(long offsetTicks, bool accepted)
    {
        ManualDeadlineClock clock = new();
        SessionRegistry registry = new();
        byte[] token = Token();
        byte[] pin = Pin();
        byte[] nonce = Nonce();
        using var registration = Register(registry, new VideoAttachWindow(clock, 100), token, pin);
        clock.Advance(TimeSpan.FromMilliseconds(100) + TimeSpan.FromTicks(offsetTicks), false);

        Assert.Equal(accepted ? VideoAttachStatus.Attached : VideoAttachStatus.Expired, registry.TryAttachVideo(
            SessionId, Security(pin), nonce, IndependentProof(token, SessionId, nonce, pin), default, out var lease));
        Assert.Equal(accepted, lease is not null);
    }

    [Theory]
    [InlineData(-365)]
    [InlineData(365)]
    public void Utc_Drift_Does_Not_Expire_An_Eligible_Window(int days)
    {
        ManualDeadlineClock clock = new();
        SessionRegistry registry = new();
        byte[] token = Token();
        byte[] pin = Pin();
        byte[] nonce = Nonce();
        using var registration = Register(registry, new VideoAttachWindow(clock, 15_000), token, pin);
        clock.Advance(TimeSpan.FromSeconds(14), false);
        clock.AdvanceUtc(TimeSpan.FromDays(days));
        Assert.Equal(VideoAttachStatus.Attached, registry.TryAttachVideo(
            SessionId, Security(pin), nonce, IndependentProof(token, SessionId, nonce, pin), default, out _));
    }

    [Fact]
    public void Delayed_Timer_Dispatch_And_Utc_Rollback_Cannot_Extend_The_Window()
    {
        ManualDeadlineClock clock = new();
        SessionRegistry registry = new();
        byte[] token = Token();
        byte[] pin = Pin();
        byte[] nonce = Nonce();
        byte[] proof = IndependentProof(token, SessionId, nonce, pin);
        ConnectionSecurityContext video = Security(pin);
        using var registration = Register(registry, new VideoAttachWindow(clock, 15_000), token, pin);
        int dispatched = 0;
        using ITimer unrelatedTimer = clock.CreateTimer(_ => dispatched++, null,
            TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan);
        Assert.Equal(1, clock.TimerCount);
        clock.Advance(TimeSpan.FromSeconds(15), false);
        clock.AdvanceUtc(TimeSpan.FromDays(-365));

        Assert.Equal(0, dispatched);
        Assert.Equal(VideoAttachStatus.Expired,
            registry.TryAttachVideo(SessionId, video, nonce, proof, default, out var lease));
        Assert.Null(lease);
        clock.FireTimers();
        Assert.Equal(1, dispatched);
        Assert.Equal(VideoAttachStatus.Expired,
            registry.TryAttachVideo(SessionId, video, nonce, proof, default, out lease));
        Assert.Null(lease);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Final_Check_After_Mac_Rejects_Expiration_Even_For_Wrong_Proof(bool correctProof)
    {
        ManualDeadlineClock manual = new();
        SampleClock clock = new(manual);
        SessionRegistry registry = new();
        byte[] token = Token();
        byte[] pin = Pin();
        byte[] nonce = Nonce();
        byte[] proof = IndependentProof(token, SessionId, nonce, pin);
        if (!correctProof) proof[0] ^= 1;
        using var registration = Register(registry, new VideoAttachWindow(clock, 15_000), token, pin);
        int beforeMacSample = clock.TimestampReads + 1;
        clock.AfterSample = read =>
        {
            // 返回仍有效的 MAC 前样本，但让时间已到点；删除 MAC 后检查也不能删除这个到期事件。
            if (read == beforeMacSample) manual.Advance(TimeSpan.FromSeconds(15), false);
        };

        Assert.Equal(VideoAttachStatus.Expired, registry.TryAttachVideo(
            SessionId, Security(pin), nonce, proof, default, out var lease));
        Assert.Null(lease);
        Assert.True(clock.TimestampReads > beforeMacSample);
    }

    [Theory]
    [InlineData(1, false, false)]
    [InlineData(1, true, true)]
    [InlineData(2, false, false)]
    [InlineData(2, false, true)]
    [InlineData(2, true, false)]
    [InlineData(2, true, true)]
    public void Timestamp_Callback_Cancellation_Wins_Over_Expiration_And_Proof(
        int sampleOffset, bool cancelControl, bool correctProof)
    {
        ManualDeadlineClock manual = new();
        SampleClock clock = new(manual);
        SessionRegistry registry = new();
        byte[] token = Token();
        byte[] pin = Pin();
        byte[] nonce = Nonce();
        byte[] proof = IndependentProof(token, SessionId, nonce, pin);
        if (!correctProof) proof[0] ^= 1;
        using CancellationTokenSource control = new();
        using CancellationTokenSource caller = new();
        using var registration = Register(registry, new VideoAttachWindow(clock, 15_000), token, pin, control.Token);
        int cancelSample = clock.TimestampReads + sampleOffset;
        clock.AfterSample = read =>
        {
            if (read != cancelSample) return;
            manual.Advance(TimeSpan.FromSeconds(15), false);
            (cancelControl ? control : caller).Cancel();
        };

        Assert.Equal(VideoAttachStatus.Cancelled, registry.TryAttachVideo(
            SessionId, Security(pin), nonce, proof, caller.Token, out var lease));
        Assert.Null(lease);
        Assert.Equal(1, registry.ActiveSessionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Final_Sample_Cancellation_Has_Priority_Over_Reentrant_Unregister(bool cancelControl)
    {
        ManualDeadlineClock manual = new();
        SampleClock clock = new(manual);
        SessionRegistry registry = new();
        byte[] token = Token();
        byte[] pin = Pin();
        byte[] nonce = Nonce();
        using CancellationTokenSource control = new();
        using CancellationTokenSource caller = new();
        using var registration = Register(registry, new VideoAttachWindow(clock, 15_000), token, pin, control.Token);
        int finalSample = clock.TimestampReads + 2;
        clock.AfterSample = read =>
        {
            if (read != finalSample) return;
            (cancelControl ? control : caller).Cancel();
            registration.Dispose();
        };

        Assert.Equal(VideoAttachStatus.Cancelled, registry.TryAttachVideo(
            SessionId, Security(pin), nonce, IndependentProof(token, SessionId, nonce, pin), caller.Token, out var lease));
        Assert.Null(lease);
        Assert.Equal(0, registry.ActiveSessionCount);
    }

    [Fact]
    public void Caller_Cancellation_Has_Priority_Over_Missing_Invalid_Expired_And_Consumed_States()
    {
        ManualDeadlineClock clock = new();
        SessionRegistry registry = new();
        byte[] token = Token();
        byte[] pin = Pin();
        byte[] nonce = Nonce();
        byte[] proof = IndependentProof(token, SessionId, nonce, pin);
        ConnectionSecurityContext video = Security(pin);
        using CancellationTokenSource caller = new();
        caller.Cancel();
        Assert.Equal(VideoAttachStatus.Cancelled,
            registry.TryAttachVideo(SessionId, video, nonce, proof, caller.Token, out var missing));
        Assert.Null(missing);
        using var registration = Register(registry, new VideoAttachWindow(clock, 15_000), token, pin);
        Assert.Equal(VideoAttachStatus.Cancelled,
            registry.TryAttachVideo(SessionId, null!, [], [], caller.Token, out var invalid));
        Assert.Null(invalid);
        Assert.Equal(VideoAttachStatus.Attached,
            registry.TryAttachVideo(SessionId, video, nonce, proof, default, out _));
        Assert.Equal(VideoAttachStatus.Cancelled,
            registry.TryAttachVideo(SessionId, video, nonce, proof, caller.Token, out var consumed));
        Assert.Null(consumed);
        clock.Advance(TimeSpan.FromSeconds(15), false);
        Assert.Equal(VideoAttachStatus.Cancelled,
            registry.TryAttachVideo(SessionId, video, nonce, proof, caller.Token, out var expired));
        Assert.Null(expired);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cancelled_Control_Is_Unavailable_For_Attach_Before_Unregister(bool cancelledBeforeRegister)
    {
        ManualDeadlineClock clock = new();
        SessionRegistry registry = new();
        byte[] token = Token();
        byte[] pin = Pin();
        byte[] nonce = Nonce();
        using CancellationTokenSource control = new();
        if (cancelledBeforeRegister) control.Cancel();
        using var registration = Register(registry, new VideoAttachWindow(clock, 15_000), token, pin, control.Token);
        if (!cancelledBeforeRegister) control.Cancel();

        Assert.Equal(1, registry.ActiveSessionCount);
        Assert.Equal(VideoAttachStatus.Cancelled, registry.TryAttachVideo(
            SessionId, Security(pin), nonce, IndependentProof(token, SessionId, nonce, pin), default, out var lease));
        Assert.Null(lease);
        Assert.Equal(VideoAttachStatus.Cancelled,
            registry.TryAttachVideo(SessionId, null!, [], [], default, out lease));
        Assert.Null(lease);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Timestamp_Reentrant_Attach_Has_Only_One_Winner(int sampleOffset)
    {
        ManualDeadlineClock manual = new();
        SampleClock clock = new(manual);
        SessionRegistry registry = new();
        byte[] token = Token();
        byte[] pin = Pin();
        byte[] nonce = Nonce();
        byte[] proof = IndependentProof(token, SessionId, nonce, pin);
        ConnectionSecurityContext innerVideo = Security(pin);
        using var registration = Register(registry, new VideoAttachWindow(clock, 15_000), token, pin);
        VideoAttachLease? innerLease = null;
        int reentrantSample = clock.TimestampReads + sampleOffset;
        clock.AfterSample = read =>
        {
            if (read != reentrantSample) return;
            clock.AfterSample = null;
            Assert.Equal(VideoAttachStatus.Attached,
                registry.TryAttachVideo(SessionId, innerVideo, nonce, proof, default, out innerLease));
        };

        Assert.Equal(VideoAttachStatus.AlreadyAttached,
            registry.TryAttachVideo(SessionId, Security(pin), nonce, proof, default, out var outerLease));
        Assert.Null(outerLease);
        Assert.NotNull(innerLease);
        Assert.Equal(innerVideo.ConnectionId, innerLease.VideoConnectionId);
        registration.Dispose();
        Assert.True(innerLease.Revoked.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Final_Sample_Unregister_Or_Same_Id_Replacement_Invalidates_The_Original_Entry(bool replace)
    {
        ManualDeadlineClock manual = new();
        SampleClock clock = new(manual);
        SessionRegistry registry = new();
        byte[] token = Token();
        byte[] replacementToken = Token();
        replacementToken[0] ^= 1;
        byte[] pin = Pin();
        byte[] nonce = Nonce();
        byte[] proof = IndependentProof(token, SessionId, nonce, pin);
        // 初次查找缺项与取时后原 entry 失效是两类状态，注销和同 ID 替换（ABA）都必须区分。
        Assert.Equal(VideoAttachStatus.NotRegistered,
            registry.TryAttachVideo(SessionId, Security(pin), nonce, proof, default, out var missingLease));
        Assert.Null(missingLease);
        using var original = Register(registry, new VideoAttachWindow(clock, 15_000), token, pin);
        byte[] ownedToken = OwnedToken(registry);
        SessionRegistry.SessionRegistration? replacement = null;
        int finalSample = clock.TimestampReads + 2;
        clock.AfterSample = read =>
        {
            if (read != finalSample) return;
            clock.AfterSample = null;
            original.Dispose();
            if (replace)
            {
                replacement = Register(registry, new VideoAttachWindow(manual, 15_000), replacementToken, pin);
            }
        };

        try
        {
            // 已观察的原 entry 在最终取时中失效；即使此时缺项，也不能返回可重试的 NotRegistered。
            Assert.Equal(VideoAttachStatus.Unavailable,
                registry.TryAttachVideo(SessionId, Security(pin), nonce, proof, default, out var staleLease));
            Assert.Null(staleLease);
            Assert.All(ownedToken, value => Assert.Equal((byte)0, value));
            original.Dispose();
            Assert.Equal(replace ? 1 : 0, registry.ActiveSessionCount);
            if (replace)
            {
                Assert.Equal(VideoAttachStatus.InvalidProof,
                    registry.TryAttachVideo(SessionId, Security(pin), nonce, proof, default, out var replay));
                Assert.Null(replay);
                Assert.Equal(VideoAttachStatus.Attached, registry.TryAttachVideo(SessionId, Security(pin), nonce,
                    IndependentProof(replacementToken, SessionId, nonce, pin), default, out var currentLease));
                Assert.NotNull(currentLease);
                Assert.False(currentLease.Revoked.IsCompleted);
                replacement!.Dispose();
                Assert.True(currentLease.Revoked.IsCompletedSuccessfully);
            }

            // 注销完成后发起的独立调用首次查找缺项，应与上面原 entry 失效的结果严格区分。
            Assert.Equal(0, registry.ActiveSessionCount);
            Assert.Equal(VideoAttachStatus.NotRegistered,
                registry.TryAttachVideo(SessionId, Security(pin), nonce, proof, default, out var unregisteredLease));
            Assert.Null(unregisteredLease);
        }
        finally
        {
            replacement?.Dispose();
        }
    }

    [Fact]
    public async Task Unregister_Zeros_Owned_Token_And_Revokes_Asynchronously_Without_Cancelling_Control()
    {
        ManualDeadlineClock clock = new();
        SessionRegistry registry = new();
        byte[] token = Token();
        byte[] originalToken = (byte[])token.Clone();
        byte[] pin = Pin();
        byte[] nonce = Nonce();
        using CancellationTokenSource control = new();
        int cancellations = 0;
        using var cancellationRegistration = control.Token.Register(() => Interlocked.Increment(ref cancellations));
        using var registration = Register(registry, new VideoAttachWindow(clock, 15_000), token, pin, control.Token);
        byte[] ownedToken = OwnedToken(registry);
        Assert.NotSame(token, ownedToken);
        Assert.Equal(originalToken, ownedToken);
        Assert.Equal(VideoAttachStatus.Attached, registry.TryAttachVideo(
            SessionId, Security(pin), nonce, IndependentProof(token, SessionId, nonce, pin), default, out var lease));
        Assert.NotNull(lease);
        Assert.True(lease.Revoked.CreationOptions.HasFlag(TaskCreationOptions.RunContinuationsAsynchronously));
        int disposingThread = 0;
        Task continuation = lease.Revoked.ContinueWith(_ =>
        {
            Assert.NotEqual(disposingThread, Environment.CurrentManagedThreadId);
            Assert.Equal(0, registry.ActiveSessionCount);
            Assert.All(ownedToken, value => Assert.Equal((byte)0, value));
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        Exception? disposeError = null;
        Thread worker = new(() =>
        {
            disposingThread = Environment.CurrentManagedThreadId;
            disposeError = Record.Exception(registration.Dispose);
        }) { IsBackground = true };
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(disposeError);
        await continuation.WaitAsync(TimeSpan.FromSeconds(10));
        registration.Dispose();
        Assert.True(lease.Revoked.IsCompletedSuccessfully);
        Assert.Equal(originalToken, token);
        Assert.False(control.IsCancellationRequested);
        Assert.Equal(0, cancellations);
    }

    [Fact]
    public void Expiration_Does_Not_Revoke_An_Already_Attached_Lease_And_Same_Id_Reuse_Is_Independent()
    {
        ManualDeadlineClock clock = new();
        SessionRegistry registry = new();
        byte[] token = Token();
        byte[] pin = Pin();
        byte[] nonce = Nonce();
        byte[] proof = IndependentProof(token, SessionId, nonce, pin);
        using var original = Register(registry, new VideoAttachWindow(clock, 15_000), token, pin);
        Assert.Equal(VideoAttachStatus.Attached,
            registry.TryAttachVideo(SessionId, Security(pin), nonce, proof, default, out var oldLease));
        Assert.NotNull(oldLease);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.False(oldLease.Revoked.IsCompleted);
        Assert.Equal(0, clock.TimerCount);
        original.Dispose();
        Assert.True(oldLease.Revoked.IsCompletedSuccessfully);
        Assert.Equal(VideoAttachStatus.NotRegistered,
            registry.TryAttachVideo(SessionId, Security(pin), nonce, proof, default, out _));

        using var replacement = Register(registry, new VideoAttachWindow(clock, 15_000), token, pin);
        original.Dispose();
        Assert.Equal(1, registry.ActiveSessionCount);
        Assert.Equal(VideoAttachStatus.Attached,
            registry.TryAttachVideo(SessionId, Security(pin), nonce, proof, default, out var newLease));
        Assert.NotNull(newLease);
        Assert.NotSame(oldLease.Revoked, newLease.Revoked);
        Assert.False(newLease.Revoked.IsCompleted);
        replacement.Dispose();
        Assert.True(newLease.Revoked.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(0, 32)]
    [InlineData(31, 32)]
    [InlineData(33, 32)]
    [InlineData(32, 0)]
    [InlineData(32, 31)]
    [InlineData(32, 33)]
    public void Register_Requires_Exactly_32_Byte_Token_And_Pin_Without_Clearing_Input(int tokenLength, int pinLength)
    {
        SessionRegistry registry = new();
        byte[] token = Enumerable.Repeat((byte)0xA5, tokenLength).ToArray();
        byte[] pin = Enumerable.Repeat((byte)0x5A, pinLength).ToArray();
        byte[] originalToken = (byte[])token.Clone();
        byte[] originalPin = (byte[])pin.Clone();
        VideoAttachWindow window = new(new ManualDeadlineClock(), 15_000);

        Assert.Throws<ArgumentException>(() => Register(registry, window, token, pin));
        Assert.Equal(0, registry.ActiveSessionCount);
        Assert.Equal(originalToken, token);
        Assert.Equal(originalPin, pin);
    }

    [Fact]
    public void Register_Rejects_Null_Window_Without_Clearing_Input()
    {
        SessionRegistry registry = new();
        byte[] token = Token();
        byte[] pin = Pin();
        Assert.Throws<ArgumentNullException>(() => Register(registry, null!, token, pin));
        Assert.Equal(Token(), token);
        Assert.Equal(Pin(), pin);
        Assert.Equal(0, registry.ActiveSessionCount);
    }

    [Fact]
    public void Registration_Conflict_Does_Not_Clear_Caller_Or_Existing_Entry()
    {
        ManualDeadlineClock clock = new();
        SessionRegistry registry = new();
        byte[] token = Token();
        byte[] pin = Pin();
        byte[] nonce = Nonce();
        using var registration = Register(registry, new VideoAttachWindow(clock, 15_000), token, pin);
        byte[] ownedToken = OwnedToken(registry);
        byte[] conflictingToken = Enumerable.Repeat((byte)0xA5, 32).ToArray();
        byte[] conflictingPin = Enumerable.Repeat((byte)0x5A, 32).ToArray();
        byte[] conflictTokenBefore = (byte[])conflictingToken.Clone();
        byte[] conflictPinBefore = (byte[])conflictingPin.Clone();

        Assert.Throws<ArgumentException>(() => Register(
            registry, new VideoAttachWindow(clock, 15_000), conflictingToken, conflictingPin));

        Assert.Equal(1, registry.ActiveSessionCount);
        Assert.Equal(Token(), ownedToken);
        Assert.Equal(Token(), token);
        Assert.Equal(Pin(), pin);
        Assert.Equal(conflictTokenBefore, conflictingToken);
        Assert.Equal(conflictPinBefore, conflictingPin);
        Assert.Equal(VideoAttachStatus.Attached, registry.TryAttachVideo(
            SessionId, Security(pin), nonce, IndependentProof(token, SessionId, nonce, pin), default, out _));
        registration.Dispose();
        Assert.All(ownedToken, value => Assert.Equal((byte)0, value));
        Assert.Equal(conflictTokenBefore, conflictingToken);
    }

    [Fact]
    public void Registration_Defensively_Copies_Token_And_Control_Pin()
    {
        ManualDeadlineClock clock = new();
        SessionRegistry registry = new();
        byte[] token = Token();
        byte[] pin = Pin();
        byte[] nonce = Nonce();
        byte[] originalPin = (byte[])pin.Clone();
        byte[] proof = IndependentProof(token, SessionId, nonce, pin);
        using var registration = Register(registry, new VideoAttachWindow(clock, 15_000), token, pin);
        byte[] ownedToken = OwnedToken(registry);
        Array.Fill(token, (byte)0xA5);
        Array.Fill(pin, (byte)0x5A);

        Assert.Equal(VideoAttachStatus.Attached,
            registry.TryAttachVideo(SessionId, Security(originalPin), nonce, proof, default, out _));
        registration.Dispose();
        Assert.All(ownedToken, value => Assert.Equal((byte)0, value));
        Assert.All(token, value => Assert.Equal((byte)0xA5, value));
        Assert.All(pin, value => Assert.Equal((byte)0x5A, value));
    }

    [Fact]
    public void Registry_And_Lease_Expose_No_Token_Bypass_Or_Eligibility_Reset()
    {
        const BindingFlags declared = BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        MethodInfo[] methods = typeof(SessionRegistry).GetMethods(declared);
        Assert.DoesNotContain(methods, method => method.Name == "TryGetSessionToken");
        Assert.Equal(new[] { "Snapshot", "get_ActiveSessionCount" },
            methods.Where(method => method.IsPublic).Select(method => method.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { "Register", "TryAttachVideo" },
            methods.Where(method => method.IsAssembly).Select(method => method.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        MethodInfo register = Assert.Single(methods, method => method.Name == "Register");
        ParameterInfo[] parameters = register.GetParameters();
        Assert.Equal(11, parameters.Length);
        Assert.Equal(typeof(ReadOnlySpan<byte>), parameters[7].ParameterType);
        Assert.Equal(typeof(VideoAttachWindow), parameters[8].ParameterType);
        Assert.Equal(typeof(ReadOnlySpan<byte>), parameters[9].ParameterType);
        Assert.Equal(typeof(CancellationToken), parameters[10].ParameterType);
        Assert.False(typeof(VideoAttachWindow).IsVisible);
        Assert.False(typeof(VideoAttachStatus).IsVisible);
        Assert.False(typeof(VideoAttachLease).IsVisible);
        Assert.True(typeof(VideoAttachLease).IsSealed);
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(VideoAttachLease)));
        Assert.False(typeof(IAsyncDisposable).IsAssignableFrom(typeof(VideoAttachLease)));
        PropertyInfo[] properties = typeof(VideoAttachLease).GetProperties(declared);
        Assert.Equal(new[] { "Revoked", "SessionId", "VideoConnectionId" },
            properties.Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.All(properties, property =>
        {
            Assert.Null(property.SetMethod);
            Assert.True(property.GetMethod!.IsAssembly);
        });
        Assert.Equal(typeof(Task), properties.Single(property => property.Name == "Revoked").PropertyType);
        Assert.All(typeof(VideoAttachLease).GetMethods(declared), method => Assert.True(method.IsSpecialName));
    }

    private static SessionRegistry.SessionRegistration Register(
        SessionRegistry registry,
        VideoAttachWindow window,
        byte[] token,
        byte[] pin,
        CancellationToken controlCancellationToken = default,
        Guid? connectionId = null) => registry.Register(
            SessionId, connectionId ?? Guid.NewGuid(), Guid.NewGuid(), "video-registry-test",
            SessionPermission.ViewOnly, RemoteAddress, 12345, token, window, pin, controlCancellationToken);

    private static ConnectionSecurityContext Security(byte[] pin, IPAddress? remoteAddress = null) =>
        new(LocalAddress, remoteAddress ?? RemoteAddress, 23456, SslProtocols.Tls13, pin);

    private static byte[] Token() => Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
    private static byte[] Pin() => Enumerable.Range(64, 32).Select(value => (byte)value).ToArray();
    private static byte[] Nonce() => Enumerable.Range(128, 16).Select(value => (byte)value).ToArray();

    // 独立拼装全部 83 字节，UUID 由规范十六进制解码为 network order，不调用生产 proof/transcript helper。
    private static byte[] IndependentProof(byte[] token, Guid sessionId, byte[] nonce, byte[] pin)
    {
        byte[] transcript = new byte[83];
        Encoding.UTF8.GetBytes("LANREMOTE-VIDEO-V1\0").CopyTo(transcript, 0);
        Convert.FromHexString(sessionId.ToString("N")).CopyTo(transcript, 19);
        nonce.CopyTo(transcript, 35);
        pin.CopyTo(transcript, 51);
        using HMACSHA256 hmac = new(token);
        return hmac.ComputeHash(transcript);
    }

    // 仅为证明登记表自有数组的清零而取得引用；没有新增任何生产测试入口。
    private static byte[] OwnedToken(SessionRegistry registry)
    {
        var sessions = (IDictionary)typeof(SessionRegistry)
            .GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registry)!;
        object entry = sessions[SessionId]!;
        return (byte[])entry.GetType().GetProperty("SessionToken")!.GetValue(entry)!;
    }

    private sealed class SampleClock(ManualDeadlineClock inner) : TimeProvider
    {
        public int TimestampReads { get; private set; }
        public long TimestampAdjustment { get; set; }
        public Action<int>? AfterSample { get; set; }
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long GetTimestamp()
        {
            long sample = inner.GetTimestamp() + TimestampAdjustment;
            int read = ++TimestampReads;
            AfterSample?.Invoke(read);
            return sample;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            throw new InvalidOperationException("视频附着窗口不应创建 timer。");
    }
}
