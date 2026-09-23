using System.Net;
using LanRemote.Core.Models;
using LanRemote.Transport;

namespace LanRemote.Acceptance.Tests;

public sealed class HostSessionEvidenceTests
{
    [Fact]
    public void Empty_Registry_Without_Authentication_Is_Unmet()
    {
        HostSessionEvidence evidence = new(8);
        evidence.Sample([], Ms(0));
        evidence.Sample([], Ms(5000));

        Assert.Equal(AcceptanceOutcome.PreconditionUnmet, Evaluate(evidence));
        Assert.Equal(0, evidence.TrackedCount);
    }

    [Fact]
    public void Successful_Run_And_Final_Zero_Do_Not_Invent_Positive_Samples()
    {
        HostSessionEvidence evidence = new(8);
        ControlSessionSummary session = Session();
        evidence.Track(session.SessionId, session.ConnectionId);
        evidence.Sample([], Ms(0));
        evidence.Sample([], Ms(5000));

        HostSessionObservation ended = End(evidence, session, 5000);

        Assert.True(ended.Authenticated);
        Assert.True(ended.DeregisteredAtEnd);
        Assert.Equal(0L, ended.ObservedSamples);
        Assert.Null(ended.FirstObserved);
        Assert.Null(ended.LastObserved);
        Assert.Null(ended.ObservedSpan);
        Assert.Null(ended.MaxObservedGap);
        Assert.Equal(AcceptanceOutcome.PreconditionUnmet, ended.Outcome);
        Assert.Equal(AcceptanceOutcome.PreconditionUnmet, Evaluate(evidence));
        Assert.Contains("observedSpanMs=UNOBSERVED", ended.Format());
    }

    [Theory]
    [InlineData(100, 4100, AcceptanceOutcome.Pass)]
    [InlineData(500, 4500, AcceptanceOutcome.Pass)]
    [InlineData(500, 4501, AcceptanceOutcome.PreconditionUnmet)]
    public void Actual_Four_Second_Span_And_Gap_Boundaries_Determine_Hold_Evidence(
        int stepMs, int endMs, AcceptanceOutcome expected)
    {
        HostSessionEvidence evidence = new(8);
        ControlSessionSummary session = Session();
        evidence.Track(session.SessionId, session.ConnectionId);
        for (int ms = 0; ms <= 4000; ms += stepMs)
        {
            evidence.Sample([session], Ms(ms));
        }

        HostSessionObservation ended = End(evidence, session, endMs);

        Assert.Equal(4000 / stepMs + 1L, ended.ObservedSamples);
        Assert.Equal(Ms(4000), ended.ObservedSpan);
        Assert.Equal(Ms(stepMs), ended.MaxObservedGap);
        Assert.Equal(Ms(endMs - 4000), ended.EndObservationGap);
        Assert.Equal(expected, ended.Outcome);
        Assert.Equal(expected, Evaluate(evidence));
        Assert.Equal(0, evidence.TrackedCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Sparse_Samples_Never_Get_Filled_From_RegisteredAt_Or_Expected_Timer_Ticks(int count)
    {
        HostSessionEvidence evidence = new(8);
        ControlSessionSummary session = Session() with { RegisteredAt = DateTimeOffset.UnixEpoch };
        evidence.Track(session.SessionId, session.ConnectionId);
        evidence.Sample([session], Ms(0));
        if (count == 2) { evidence.Sample([session], Ms(4000)); }

        HostSessionObservation ended = End(evidence, session, 4100);

        Assert.Equal((long)count, ended.ObservedSamples);
        Assert.Equal(count == 1 ? Ms(0) : Ms(4000), ended.ObservedSpan);
        if (count == 1) { Assert.Null(ended.MaxObservedGap); }
        else { Assert.Equal(Ms(4000), ended.MaxObservedGap); }
        Assert.Equal(AcceptanceOutcome.PreconditionUnmet, ended.Outcome);
    }

    [Fact]
    public void Gap_Of_501_Milliseconds_Is_Unmet_Even_With_Four_Second_Span()
    {
        HostSessionEvidence evidence = new(8);
        ControlSessionSummary session = Session();
        evidence.Track(session.SessionId, session.ConnectionId);
        int[] times = [0, 500, 1001, 1500, 2000, 2500, 3000, 3500, 4000];
        foreach (int ms in times) { evidence.Sample([session], Ms(ms)); }

        HostSessionObservation ended = End(evidence, session, 4100);

        Assert.Equal(Ms(4000), ended.ObservedSpan);
        Assert.Equal(Ms(501), ended.MaxObservedGap);
        Assert.Equal(AcceptanceOutcome.PreconditionUnmet, ended.Outcome);
    }

    [Fact]
    public void Three_Point_Nine_Nine_Nine_Seconds_Is_Not_Four_Seconds()
    {
        HostSessionEvidence evidence = new(8);
        ControlSessionSummary session = Session();
        evidence.Track(session.SessionId, session.ConnectionId);
        for (int ms = 0; ms <= 3500; ms += 500) { evidence.Sample([session], Ms(ms)); }
        evidence.Sample([session], Ms(3999));

        HostSessionObservation ended = End(evidence, session, 4100);

        Assert.Equal(Ms(3999), ended.ObservedSpan);
        Assert.Equal(AcceptanceOutcome.PreconditionUnmet, ended.Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Host_Stop_Or_Cancellation_Cannot_Pass_As_Natural_Release(bool cancellationOnly)
    {
        HostSessionEvidence evidence = new(8);
        ControlSessionSummary session = Session();
        SampleFullHold(evidence, session);
        if (!cancellationOnly)
        {
            // 真实 registry 可以已经为空：停机与 Run 返回重叠仍不能倒写自然释放。
            evidence.BeginHostStop(new SessionRegistry());
        }

        HostSessionObservation ended = End(evidence, session, 4100, cancelledByHost: cancellationOnly);

        Assert.True(ended.HostForcedClose);
        Assert.True(ended.DeregisteredAtEnd);
        Assert.Equal(AcceptanceOutcome.PreconditionUnmet, ended.Outcome);
        Assert.Equal(AcceptanceOutcome.PreconditionUnmet, Evaluate(evidence));
    }

    [Fact]
    public void Rejected_Attempts_Are_Separate_From_Qualified_Authenticated_Evidence()
    {
        HostSessionEvidence evidence = new(8);
        ControlSessionSummary rejected = Session();
        evidence.Track(rejected.SessionId, rejected.ConnectionId);
        HostSessionObservation rejection = End(evidence, rejected, 0, authenticated: false);
        Assert.False(rejection.Authenticated);
        Assert.True(rejection.DeregisteredAtEnd);
        Assert.Equal(AcceptanceOutcome.PreconditionUnmet, Evaluate(evidence));

        ControlSessionSummary successful = Session();
        SampleFullHold(evidence, successful);
        Assert.Equal(AcceptanceOutcome.Pass, End(evidence, successful, 4100).Outcome);
        Assert.Equal(AcceptanceOutcome.Pass, Evaluate(evidence));
    }

    [Fact]
    public void Registry_Still_Containing_Session_At_Run_End_Is_Fail()
    {
        HostSessionEvidence evidence = new(8);
        ControlSessionSummary session = Session();
        SampleFullHold(evidence, session);

        HostSessionObservation ended = evidence.End(session.SessionId, session.ConnectionId,
            true, false, [session], Ms(4100));

        Assert.False(ended.DeregisteredAtEnd);
        Assert.Equal(AcceptanceOutcome.Fail, ended.Outcome);
        Assert.Equal(AcceptanceOutcome.Fail, Evaluate(evidence));
    }

    [Fact]
    public void Connection_Mismatch_Is_Not_An_Observed_Positive_Sample()
    {
        HostSessionEvidence evidence = new(8);
        ControlSessionSummary session = Session();
        evidence.Track(session.SessionId, session.ConnectionId);
        evidence.Sample([session with { ConnectionId = Guid.NewGuid() }], Ms(0));

        HostSessionObservation ended = End(evidence, session, 0);

        Assert.Equal(0L, ended.ObservedSamples);
        Assert.Equal(AcceptanceOutcome.Fail, ended.Outcome);
        Assert.Equal(AcceptanceOutcome.Fail, Evaluate(evidence));
    }

    [Fact]
    public void Observed_Absence_Between_Positives_Breaks_Continuity()
    {
        HostSessionEvidence evidence = new(8);
        ControlSessionSummary session = Session();
        evidence.Track(session.SessionId, session.ConnectionId);
        for (int ms = 0; ms <= 4000; ms += 100)
        {
            evidence.Sample(ms == 2000 ? [] : [session], Ms(ms));
        }

        HostSessionObservation ended = End(evidence, session, 4100);

        Assert.Equal(40L, ended.ObservedSamples);
        Assert.Equal(Ms(4000), ended.ObservedSpan);
        Assert.Equal(Ms(200), ended.MaxObservedGap);
        Assert.True(ended.ObservationInterrupted);
        Assert.Equal(AcceptanceOutcome.PreconditionUnmet, ended.Outcome);
    }

    [Fact]
    public void Tracking_Is_Bounded_And_End_Releases_The_Slot_Without_Resurrecting_History()
    {
        HostSessionEvidence evidence = new(1);
        ControlSessionSummary first = Session();
        ControlSessionSummary overflow = Session();
        Assert.True(evidence.Track(first.SessionId, first.ConnectionId));
        Assert.False(evidence.Track(overflow.SessionId, overflow.ConnectionId));
        evidence.Sample([first, overflow], Ms(0));
        Assert.Equal(1, evidence.TrackedCount);
        HostSessionObservation dropped = End(evidence, overflow, 0);
        Assert.False(dropped.Tracked);
        Assert.Equal(0L, dropped.ObservedSamples);
        Assert.Equal(AcceptanceOutcome.PreconditionUnmet, dropped.Outcome);
        End(evidence, first, 0);
        Assert.Equal(0, evidence.TrackedCount);

        // 即使外部注入结束后的旧快照，也不创建新的跟踪项。
        evidence.Sample([first], Ms(100));
        Assert.Equal(0, evidence.TrackedCount);
        Assert.True(evidence.Track(overflow.SessionId, overflow.ConnectionId));
        Assert.Equal(1, evidence.TrackedCount);
    }

    [Fact]
    public void Different_SessionIds_Do_Not_Combine_Their_Observed_Spans()
    {
        HostSessionEvidence evidence = new(1);
        for (int i = 0; i < 2; i++)
        {
            ControlSessionSummary session = Session();
            evidence.Track(session.SessionId, session.ConnectionId);
            for (int ms = i * 2500; ms <= i * 2500 + 2000; ms += 100)
            {
                evidence.Sample([session], Ms(ms));
            }
            Assert.Equal(AcceptanceOutcome.PreconditionUnmet, End(evidence, session, i * 2500 + 2100).Outcome);
        }
        Assert.Equal(AcceptanceOutcome.PreconditionUnmet, Evaluate(evidence));
    }

    [Theory]
    [InlineData(false, 0, true, AcceptanceOutcome.HarnessError)]
    [InlineData(true, 1, true, AcceptanceOutcome.HarnessError)]
    [InlineData(true, 0, false, AcceptanceOutcome.Fail)]
    [InlineData(true, 0, true, AcceptanceOutcome.Pass)]
    public void Partition_Handler_Faults_And_First_Stop_Report_Actually_Control_Outcome(
        bool partitionOk, long handlerFaults, bool cleanShutdown, AcceptanceOutcome expected)
    {
        HostSessionEvidence evidence = new(8);
        ControlSessionSummary session = Session();
        SampleFullHold(evidence, session);
        Assert.Equal(AcceptanceOutcome.Pass, End(evidence, session, 4100).Outcome);

        Assert.Equal(expected, evidence.Evaluate(partitionOk, handlerFaults, cleanShutdown));
    }

    [Fact]
    public void Sampling_Rejects_Nonmonotonic_Time_Without_Removing_Active_Tracking()
    {
        HostSessionEvidence evidence = new(8);
        ControlSessionSummary session = Session();
        evidence.Track(session.SessionId, session.ConnectionId);
        evidence.Sample([session], Ms(100));

        Assert.Throws<ArgumentOutOfRangeException>(() => evidence.Sample([session], Ms(99)));
        Assert.Throws<ArgumentOutOfRangeException>(() => End(evidence, session, 99));
        Assert.Equal(1, evidence.TrackedCount);
    }

    private static void SampleFullHold(HostSessionEvidence evidence, ControlSessionSummary session)
    {
        Assert.True(evidence.Track(session.SessionId, session.ConnectionId));
        for (int ms = 0; ms <= 4000; ms += 100) { evidence.Sample([session], Ms(ms)); }
    }

    private static HostSessionObservation End(HostSessionEvidence evidence, ControlSessionSummary session,
        int endMs, bool authenticated = true, bool cancelledByHost = false) =>
        evidence.End(session.SessionId, session.ConnectionId, authenticated, cancelledByHost, [], Ms(endMs));

    private static AcceptanceOutcome Evaluate(HostSessionEvidence evidence) => evidence.Evaluate(true, 0, true);

    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    private static ControlSessionSummary Session() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        "证据分类测试", SessionPermission.ViewOnly, IPAddress.Loopback, 45678, DateTimeOffset.UtcNow);
}
