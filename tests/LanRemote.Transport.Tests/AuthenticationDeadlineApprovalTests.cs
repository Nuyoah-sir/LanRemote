using LanRemote.Core.Models;
using TimerCaptureClock = LanRemote.Transport.Tests.AuthenticationDeadlineTests.TimerCaptureClock;

namespace LanRemote.Transport.Tests;

public sealed partial class ControlAuthSessionTests
{
    [Fact(Timeout = 120_000)]
    public async Task Approval_Early_Timer_Is_Not_Gate_Unavailable_And_Late_Approval_Is_Discarded()
    {
        ManualDeadlineClock manual = new();
        TimerCaptureClock clock = new(manual);
        TaskCompletionSource<LocalApprovalDecision> late = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> earlyCancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubApprovalGate gate = new((_, token) =>
        {
            manual.Advance(TimeSpan.FromMilliseconds(599.5), fireTimers: false);
            clock.FireEarlyCallback();
            // 不在 gate/取消回调内断言：生产路径会隔离这些异常。
            earlyCancellation.SetResult(token.IsCancellationRequested);
            return new ValueTask<LocalApprovalDecision>(late.Task.WaitAsync(token));
        });

        try
        {
            AuthScenario scenario = await RunScenarioAsync(
                (connection, probe, ct) => probe.SpeakAuthAsync(connection, ct), gate,
                options: FastOptions with { ApprovalWindow = TimeSpan.FromMilliseconds(600) },
                timeProvider: clock,
                midflight: async (_, probe) =>
                {
                    bool cancelledEarly = await earlyCancellation.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    // 旧实现已错误唤醒 gate，保持时钟冻结以确定性暴露 unavailable，而非被随后到期掩盖。
                    if (!cancelledEarly)
                    {
                        manual.Advance(TimeSpan.FromMilliseconds(1));
                    }
                    await probe.TerminalSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
                });

            Assert.False(scenario.Result.Completed);
            Assert.Equal(ControlAuthSession.RejectApprovalTimeout, scenario.Result.Rejection);
            Assert.False(await earlyCancellation.Task);
            Assert.True(scenario.Probe.SawApprovalPending);
            Assert.True(scenario.Probe.SawGenericFailure);
            Assert.Null(scenario.Probe.Success);
            Assert.Equal(0, scenario.Registry.ActiveSessionCount);
            Assert.Equal(0, scenario.Session.Context.PendingApprovalLimiter.GlobalInUse);

            LocalApprovalRequest request = Assert.IsType<LocalApprovalRequest>(gate.LastRequest);
            LocalApprovalDecision approved = new(
                request.RequestId, LocalApprovalOutcome.Approved, request.RequestedPermission);
            late.SetResult(approved);
            Assert.Same(approved, await late.Task);
            Assert.Equal(ControlAuthSession.RejectApprovalTimeout, scenario.Result.Rejection);
            Assert.Null(scenario.Probe.Success);
            Assert.Equal(0, scenario.Registry.ActiveSessionCount);
        }
        finally
        {
            late.TrySetCanceled();
        }
    }
}
