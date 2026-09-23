using System.Net;
using LanRemote.Core.Models;
using LanRemote.Transport;
using Microsoft.Extensions.Logging;

namespace LanRemote.Acceptance.Tests;

[Collection(AcceptanceStageFiveCollection.Name)]
public sealed class MainWindowRunStateTests
{
    [Fact(Timeout = 20_000)]
    public async Task Pending_Expires_From_Monotonic_Acceptance_Time_And_Reading_Cannot_Restart_Window()
    {
        await Task.Yield();
        using AcceptanceTestDirectory directory = new();
        using CancellationTokenSource cts = new();
        using MainWindow.RunState state = new(AcceptanceRun.Create(directory.Path, "state-test"), cts, false);
        long now = TimeProvider.System.GetTimestamp();
        ControlClientApprovalPending fresh = new(Guid.NewGuid(), "A1B2C3", now, TimeSpan.FromMinutes(1));
        state.PublishPending(fresh);
        Assert.Same(fresh, state.GetPending());
        Assert.Same(fresh, state.GetPending());
        Assert.Null(state.Inbox);

        // 只注入 pending 的接收时间，不修改系统 UTC，也不靠 sleep 猜过期。
        ControlClientApprovalPending expired = fresh with
        {
            AcceptedAtTimestamp = now - checked(TimeProvider.System.TimestampFrequency * 120),
        };
        state.PublishPending(expired);
        Assert.Null(state.GetPending());
        Assert.Null(state.GetPending());
        state.PublishPending(fresh with { ApprovalWindow = TimeSpan.Zero });
        Assert.Null(state.GetPending());
        ControlClientApprovalPending replacement = fresh with { SessionId = Guid.NewGuid() };
        state.PublishPending(replacement);
        Assert.Same(replacement, state.GetPending());
        state.ClearPending();
        Assert.Null(state.GetPending());
    }

    [Theory(Timeout = 20_000)]
    [InlineData("stop")]
    [InlineData("cancel")]
    [InlineData("dispose")]
    public async Task Stop_Clears_Context_Pending_And_Inbox_And_Rejects_Late_Publications(string action)
    {
        using AcceptanceTestDirectory directory = new();
        using CancellationTokenSource cts = new();
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(12));
        // 不实例化 MainWindow，不创建 Dispatcher，不初始化 context/vault。
        AcceptanceContext context = new(LogLevel.None);
        using MainWindow.RunState state = new(AcceptanceRun.Create(directory.Path, "state-test"), cts, true);
        LocalApprovalInbox inbox = Assert.IsType<LocalApprovalInbox>(state.Inbox);
        Task<LocalApprovalDecision>? decision = null;
        try
        {
            state.PublishContext(context);
            Assert.Same(context, state.GetHostContext());
            ControlClientApprovalPending pending = Pending();
            state.PublishPending(pending);
            Assert.Same(pending, state.GetPending());
            LocalApprovalRequest request = Request(Guid.NewGuid());
            decision = inbox.RequestApprovalAsync(request, CancellationToken.None).AsTask();
            LocalApprovalSnapshot snapshot = Assert.Single(inbox.GetSnapshot());
            Assert.False(decision.IsCompleted);

            switch (action)
            {
                case "stop": state.Stop(); break;
                case "cancel": cts.Cancel(); break;
                case "dispose": state.Dispose(); break;
            }

            Assert.True(state.IsStopped);
            Assert.Equal(action == "cancel", cts.IsCancellationRequested);
            Assert.Null(state.GetHostContext());
            Assert.Null(state.GetPending());
            Assert.Equal(0, inbox.PendingCount);
            Assert.Empty(inbox.GetSnapshot());
            Assert.Equal(LocalApprovalOutcome.Cancelled, (await decision.WaitAsync(guard.Token)).Outcome);
            Assert.False(inbox.TrySubmit(snapshot.RequestId, snapshot.Generation,
                LocalApprovalOutcome.Approved, SessionPermission.Control));
            state.PublishContext(context);
            state.PublishPending(pending);
            Assert.Null(state.GetHostContext());
            Assert.Null(state.GetPending());
            ValueTask<LocalApprovalDecision> unavailable = inbox.RequestApprovalAsync(Request(Guid.NewGuid()), CancellationToken.None);
            Assert.True(unavailable.IsCompletedSuccessfully);
            Assert.Equal(LocalApprovalOutcome.Unavailable, (await unavailable).Outcome);
            state.Stop();
            state.Dispose();
            Assert.True(state.IsStopped);
        }
        finally
        {
            state.Dispose();
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
            try
            {
                if (decision is not null) { await decision.WaitAsync(cleanup.Token); }
            }
            finally { await context.DisposeAsync().AsTask().WaitAsync(cleanup.Token); }
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task Old_Run_Cancellation_And_Snapshot_Cannot_Act_On_New_Run_With_Reused_Request_Id()
    {
        using AcceptanceTestDirectory directory = new();
        using CancellationTokenSource oldCts = new();
        using CancellationTokenSource newCts = new();
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(12));
        using MainWindow.RunState oldState = new(AcceptanceRun.Create(directory.Path, "old-state"), oldCts, true);
        using MainWindow.RunState newState = new(AcceptanceRun.Create(directory.Path, "new-state"), newCts, true);
        Assert.NotEqual(oldState.Generation, newState.Generation);
        Assert.NotEqual(Guid.Empty, oldState.Generation);
        Assert.NotSame(oldState.Inbox, newState.Inbox);
        LocalApprovalRequest request = Request(Guid.NewGuid());
        Task<LocalApprovalDecision> oldDecision = oldState.Inbox!.RequestApprovalAsync(request, CancellationToken.None).AsTask();
        Task<LocalApprovalDecision> newDecision = newState.Inbox!.RequestApprovalAsync(request, CancellationToken.None).AsTask();
        try
        {
            LocalApprovalSnapshot stale = Assert.Single(oldState.Inbox.GetSnapshot());
            LocalApprovalSnapshot current = Assert.Single(newState.Inbox.GetSnapshot());
            Assert.Equal(stale.RequestId, current.RequestId);
            Assert.NotEqual(stale.Generation, current.Generation);
            ControlClientApprovalPending oldPending = Pending();
            ControlClientApprovalPending newPending = Pending();
            oldState.PublishPending(oldPending);
            newState.PublishPending(newPending);
            oldCts.Cancel();
            Assert.Equal(LocalApprovalOutcome.Cancelled, (await oldDecision.WaitAsync(guard.Token)).Outcome);
            Assert.True(oldState.IsStopped);
            Assert.Null(oldState.GetPending());
            Assert.False(newState.IsStopped);
            Assert.False(newCts.IsCancellationRequested);
            Assert.Same(newPending, newState.GetPending());
            Assert.False(newState.Inbox.TrySubmit(stale.RequestId, stale.Generation,
                LocalApprovalOutcome.Approved, SessionPermission.Control));
            Assert.False(newState.Inbox.Cancel(stale.RequestId, stale.Generation));
            Assert.False(newDecision.IsCompleted);
            oldState.Run.Log.WriteLine("[CLIENT][AUTH] serverProof=verified old-run");
            Assert.Same(newPending, newState.GetPending());
            Assert.True(newState.Inbox.TrySubmit(current.RequestId, current.Generation,
                LocalApprovalOutcome.Approved, SessionPermission.ViewOnly));
            LocalApprovalDecision result = await newDecision.WaitAsync(guard.Token);
            Assert.Equal(LocalApprovalOutcome.Approved, result.Outcome);
            Assert.Equal(SessionPermission.ViewOnly, result.GrantedPermission);
        }
        finally
        {
            oldState.Stop();
            newState.Stop();
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
            await Task.WhenAll(oldDecision, newDecision).WaitAsync(cleanup.Token);
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task Stopping_Context_Is_No_Longer_Published_Without_Opening_A_Window()
    {
        using AcceptanceTestDirectory directory = new();
        using CancellationTokenSource cts = new();
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(12));
        AcceptanceContext context = new(LogLevel.None);
        using MainWindow.RunState state = new(AcceptanceRun.Create(directory.Path, "state-test"), cts, true);
        try
        {
            state.PublishContext(context);
            Assert.Same(context, state.GetHostContext());
            await context.StopWorkAsync().WaitAsync(guard.Token);
            Assert.True(context.IsStopping);
            Assert.Null(state.GetHostContext());
            Assert.False(state.IsStopped);
            Assert.Null(context.Identity);
            Assert.Null(context.Certificate);
            Assert.Null(context.Discovery);
        }
        finally
        {
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
            await context.DisposeAsync().AsTask().WaitAsync(cleanup.Token);
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task Log_Notifications_Clear_Pending_Report_Proof_Failure_And_Unsubscribe_On_Dispose()
    {
        await Task.Yield();
        using AcceptanceTestDirectory directory = new();
        using CancellationTokenSource cts = new();
        AcceptanceRun run = AcceptanceRun.Create(directory.Path, "state-test");
        using MainWindow.RunState state = new(run, cts, false);
        ControlClientApprovalPending pending = Pending();
        state.PublishPending(pending);
        run.Log.WriteLine("普通日志不能清理 pending");
        Assert.Same(pending, state.GetPending());
        run.Log.WriteLine("[CLIENT][AUTH] serverProof=verified session=test");
        Assert.Null(state.GetPending());
        Assert.Null(state.AuthenticationFailure);
        state.PublishPending(pending);
        run.Log.WriteLine("[CLIENT][RESULT] scenario=success clientOutcome=PASS 结果");
        Assert.Null(state.GetPending());
        state.PublishPending(pending);
        string failure = "[CLIENT][RESULT] scenario=success clientOutcome=FAIL // " +
            ControlClientAuthenticationException.ServerProofFailureMessage;
        run.Log.WriteLine(failure);
        Assert.Null(state.GetPending());
        Assert.Equal(ControlClientAuthenticationException.ServerProofFailureMessage, state.AuthenticationFailure);

        using CancellationTokenSource otherCts = new();
        AcceptanceRun otherRun = AcceptanceRun.Create(directory.Path, "disposed-state");
        using MainWindow.RunState disposed = new(otherRun, otherCts, false);
        disposed.Dispose();
        otherRun.Log.WriteLine(failure);
        Assert.Null(disposed.AuthenticationFailure);
        Assert.True(disposed.IsStopped);
    }

    [Fact(Timeout = 20_000)]
    public async Task Already_Cancelled_Token_Stops_State_During_Construction()
    {
        await Task.Yield();
        using AcceptanceTestDirectory directory = new();
        using CancellationTokenSource cts = new();
        cts.Cancel();
        using MainWindow.RunState state = new(AcceptanceRun.Create(directory.Path, "state-test"), cts, true);
        Assert.True(state.IsStopped);
        state.PublishPending(Pending());
        Assert.Null(state.GetPending());
        Assert.Null(state.GetHostContext());
        Assert.Empty(state.Inbox!.GetSnapshot());
    }

    private static ControlClientApprovalPending Pending() =>
        new(Guid.NewGuid(), "A1B2C3", TimeProvider.System.GetTimestamp(), TimeSpan.FromMinutes(1));

    private static LocalApprovalRequest Request(Guid requestId) => new(
        requestId, Guid.NewGuid(), Guid.NewGuid(), IPAddress.Loopback, 43210,
        Guid.NewGuid(), "状态测试客户端", SessionPermission.Control, "A1B2C3", DateTimeOffset.UtcNow.AddMinutes(1));
}
