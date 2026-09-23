using System.Reflection;
using LanRemote.Core.Models;

namespace LanRemote.Transport.Tests;

public sealed partial class ControlClientConnectorTests
{
    [Fact(Timeout = 30_000)]
    public async Task Pending_Notification_Precedes_Decision_And_Matches_Real_Server_Request()
    {
        DeferredApprovalGate gate = new();
        ControlAuthContext context = CreateContext(new RecordingApprovalGate(SessionPermission.Control), true)
            with { ApprovalGate = gate };
        TaskCompletionSource<ControlClientApprovalPending> notified = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        await using TlsScenario scenario = new(async (connection, ct) =>
        {
            ControlAuthSession auth = await BeginRealAuthenticationAsync(connection, context, ct);
            ControlAuthResult result = await auth.RunAsync(ct);
            Assert.True(result.Completed, result.Rejection);
        });
        scenario.Start();
        Task<AuthenticatedControlSession> client = scenario.ConnectAsync(options: ClientOptions with
        {
            ApprovalPending = pending =>
            {
                Interlocked.Increment(ref calls);
                notified.TrySetResult(pending);
            },
        });
        ControlClientApprovalPending pending = await notified.Task.WaitAsync(Guard);
        LocalApprovalRequest request = await gate.Request.Task.WaitAsync(Guard);
        Assert.False(client.IsCompleted);
        Assert.Equal(0, context.SessionRegistry.ActiveSessionCount);
        Assert.Equal(request.SessionId, pending.SessionId);
        Assert.Equal(request.ShortCode, pending.ShortCode);
        Assert.Matches("^[0-9A-F]{6}$", pending.ShortCode);
        Assert.Equal(ClientOptions.ApprovalWindow, pending.ApprovalWindow);
        gate.Decision.SetResult(new(request.RequestId, LocalApprovalOutcome.Approved, SessionPermission.ViewOnly));
        using AuthenticatedControlSession session = await client.WaitAsync(Guard);
        Assert.Equal(pending.SessionId, session.SessionId);
        Assert.Equal(pending.ShortCode, session.ShortCode);
        Assert.Equal(SessionPermission.ViewOnly, session.GrantedPermission);
        Assert.Equal(1, calls);
        session.Dispose();
        await scenario.AssertServerFinishedAsync();
    }

    [Theory(Timeout = 30_000)]
    [InlineData("direct-success")]
    [InlineData("early-pending")]
    [InlineData("malformed-pending")]
    [InlineData("remote-failure")]
    public async Task No_Notification_Without_First_Valid_Post_Response_Pending(string frame)
    {
        int calls = 0;
        await using TlsScenario scenario = TlsScenario.Scripted(async (peer, ct) =>
        {
            if (frame == "early-pending")
            {
                await peer.SendPendingAsync(ct);
            }
            else
            {
                await peer.SendChallengeAsync(ct);
                byte[] transcript = await peer.ReadAndVerifyResponseAsync(SessionPermission.Control, ct);
                await peer.SendAsync(frame switch
                {
                    "direct-success" => peer.Success(transcript, SessionPermission.Control),
                    "malformed-pending" => "{\"type\":\"approval_pending\",\"extra\":true}"u8.ToArray(),
                    _ => "{\"type\":\"authentication_failed\"}"u8.ToArray(),
                }, ct);
            }
            await peer.AssertClosedWithoutDataAsync(ct);
        });
        scenario.Start();
        Task<AuthenticatedControlSession> client = scenario.ConnectAsync(options: ClientOptions with
        {
            ApprovalPending = _ => Interlocked.Increment(ref calls),
        });
        if (frame == "direct-success")
        {
            using AuthenticatedControlSession session = await client.WaitAsync(Guard);
            session.Dispose();
        }
        else
        {
            await Assert.ThrowsAsync<ControlClientAuthenticationException>(() => client.WaitAsync(Guard));
        }
        await scenario.AssertServerFinishedAsync();
        Assert.Equal(0, calls);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repeated_Pending_Or_Bad_Proof_Cannot_Become_Authenticated_Through_Notification(bool repeated)
    {
        int calls = 0;
        await using TlsScenario scenario = TlsScenario.Scripted(async (peer, ct) =>
        {
            await peer.SendChallengeAsync(ct);
            _ = await peer.ReadAndVerifyResponseAsync(SessionPermission.Control, ct);
            await peer.SendPendingAsync(ct);
            if (repeated)
                await peer.SendPendingAsync(ct);
            else
                await peer.SendAsync(peer.SuccessWithProof(SessionPermission.Control, new byte[32]), ct);
            await peer.AssertClosedWithoutDataAsync(ct);
        });
        scenario.Start();
        await AssertRejectedAsync(scenario, scenario.ConnectAsync(options: ClientOptions with
        {
            ApprovalPending = _ => Interlocked.Increment(ref calls),
        }), repeated ? "client-repeated-pending" : "client-server-proof-mismatch",
            repeated ? "远端重复发送审批等待帧。" : ProofFailureMessage);
        Assert.Equal(1, calls);
    }

    [Theory(Timeout = 30_000)]
    [InlineData("throw")]
    [InlineData("cancel")]
    [InlineData("cancel-throw")]
    [InlineData("expire")]
    [InlineData("expire-throw")]
    public async Task Notification_Rechecks_Cancel_And_Deadline_And_Sanitizes_Local_Fault(string behavior)
    {
        ClientDeadlineClock clock = new();
        using CancellationTokenSource caller = new();
        int calls = 0;
        const string sensitiveSentinel = "not-a-real-secret-do-not-propagate";
        await using TlsScenario scenario = TlsScenario.Scripted(async (peer, ct) =>
        {
            await peer.SendChallengeAsync(ct);
            _ = await peer.ReadAndVerifyResponseAsync(SessionPermission.Control, ct);
            await peer.SendPendingAsync(ct);
            // 不发 success 也不先断线；客户端必须因通知的取消/到期/故障主动关闭。
            await peer.AssertClosedWithoutDataAsync(ct);
        });
        scenario.Start();
        Task<AuthenticatedControlSession> client = scenario.ConnectAsync(
            options: ClientOptions with
            {
                ApprovalPending = pending =>
                {
                    Interlocked.Increment(ref calls);
                    Assert.Equal(clock.MonotonicTimestamp, pending.AcceptedAtTimestamp);
                    if (behavior.StartsWith("cancel", StringComparison.Ordinal))
                        caller.Cancel();
                    if (behavior.StartsWith("expire", StringComparison.Ordinal))
                        clock.Advance(pending.ApprovalWindow, fireTimers: false);
                    if (behavior.EndsWith("throw", StringComparison.Ordinal))
                        throw new InvalidOperationException(sensitiveSentinel);
                },
            }, clock: clock, cancellationToken: caller.Token);
        if (behavior.StartsWith("cancel", StringComparison.Ordinal))
        {
            OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.WaitAsync(Guard));
            Assert.Null(error.InnerException);
            Assert.DoesNotContain(sensitiveSentinel, error.ToString());
            await scenario.AssertServerFinishedAsync();
        }
        else
        {
            bool expires = behavior.StartsWith("expire", StringComparison.Ordinal);
            await AssertRejectedAsync(scenario, client,
                expires ? "client-approval-timeout" : "client-approval-notification-failed",
                expires ? "等待远端审批超时，请重试。" : "无法显示远端审批等待状态，请重试。");
            Assert.DoesNotContain(sensitiveSentinel, client.Exception!.ToString());
        }
        Assert.Equal(1, calls);
        Assert.Equal(0, clock.TimerCallbacks);
        Assert.Equal(0, clock.ActiveTimerCount);
    }

    [Fact(Timeout = 30_000)]
    public async Task Notification_Elapsed_Time_Is_Not_Added_Back_To_Approval_Window()
    {
        ClientDeadlineClock clock = new();
        TimeSpan budget = TimeSpan.FromSeconds(3);
        TimeSpan callbackElapsed = TimeSpan.FromSeconds(1);
        await using TlsScenario scenario = TlsScenario.Scripted(async (peer, ct) =>
        {
            await peer.SendChallengeAsync(ct);
            _ = await peer.ReadAndVerifyResponseAsync(SessionPermission.Control, ct);
            await peer.SendPendingAsync(ct);
            ClientTimer approval = await clock.WaitForTimerAsync(budget, cancellationToken: ct);
            ClientTimer prefix = await clock.WaitForTimerAsync(budget - callbackElapsed, after: approval, cancellationToken: ct);
            Assert.Equal(callbackElapsed.Ticks, prefix.StartedAt - approval.StartedAt);
            clock.Advance(budget - callbackElapsed, fireTimers: true);
            await peer.AssertClosedWithoutDataAsync(ct);
        });
        scenario.Start();
        await AssertRejectedAsync(scenario, scenario.ConnectAsync(options: ClientOptions with
        {
            ApprovalWindow = budget,
            ApprovalPending = _ => clock.Advance(callbackElapsed, fireTimers: false),
        }, clock: clock), "client-approval-timeout", "等待远端审批超时，请重试。");
        Assert.Equal(budget.Ticks, clock.MonotonicTimestamp);
        Assert.True(clock.TimerCallbacks > 0);
        Assert.Equal(0, clock.ActiveTimerCount);
    }

    [Fact]
    public void Pending_Notification_Public_Properties_Are_Only_Nonsecret_Correlation_And_Timing()
    {
        Assert.Equal(new[] { "AcceptedAtTimestamp", "ApprovalWindow", "SessionId", "ShortCode" },
            typeof(ControlClientApprovalPending).GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => property.Name).Order().ToArray());
    }

    private sealed class DeferredApprovalGate : ILocalApprovalGate
    {
        public TaskCompletionSource<LocalApprovalRequest> Request { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<LocalApprovalDecision> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<LocalApprovalDecision> RequestApprovalAsync(LocalApprovalRequest request, CancellationToken cancellationToken)
        {
            Request.TrySetResult(request);
            return new(Decision.Task.WaitAsync(cancellationToken));
        }
    }
}
