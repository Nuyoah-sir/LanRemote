using System.Diagnostics;
using LanRemote.Transport;

namespace LanRemote.Acceptance;

/// <summary>只保存正在运行的认证会话；结束即移除，历史仅累计计数，不保存秘密。</summary>
internal sealed class HostSessionEvidence
{
    // 两机验收配置，不改变产品认证窗口或保持行为；客户端应自行持有 5 秒。
    internal static readonly TimeSpan SamplePeriod = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan RequiredObservedSpan = TimeSpan.FromSeconds(4);
    internal static readonly TimeSpan AllowedObservationGap = TimeSpan.FromMilliseconds(500);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Observation> _active = new();
    private readonly int _capacity;
    private readonly long _origin = Stopwatch.GetTimestamp();
    private bool _stopping;
    private long _samples;
    private TimeSpan? _firstSample;
    private TimeSpan? _lastSample;
    private TimeSpan _maxSampleGap;
    private long _trackingDropped;
    private long _authenticatedEnded;
    private long _qualified;
    private long _unmet;
    private long _forced;
    private long _violations;

    internal HostSessionEvidence(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    internal int TrackedCount
    {
        get { lock (_gate) { return _active.Count; } }
    }

    internal bool Track(Guid sessionId, Guid connectionId)
    {
        lock (_gate)
        {
            if (_active.ContainsKey(sessionId))
            {
                throw new InvalidOperationException("同一认证会话被重复跟踪。");
            }
            if (_active.Count >= _capacity)
            {
                _trackingDropped++;
                return false;
            }
            _active.Add(sessionId, new Observation(connectionId));
            return true;
        }
    }

    internal void Sample(SessionRegistry registry)
    {
        // 取快照与处理必须在同一锁内，避免晚处理的旧快照跨越 End，复活已结束会话。
        lock (_gate)
        {
            Sample(registry.Snapshot(), Stopwatch.GetElapsedTime(_origin));
        }
    }

    /// <summary>测试只注入实际样本及单调时刻；不按预期周期补点，不使用 RegisteredAt 推算。</summary>
    internal void Sample(IReadOnlyList<ControlSessionSummary> snapshot, TimeSpan monotonicTime)
    {
        lock (_gate)
        {
            if (monotonicTime < TimeSpan.Zero || (_lastSample is { } last && monotonicTime < last))
            {
                throw new ArgumentOutOfRangeException(nameof(monotonicTime), "采样时刻必须单调。");
            }
            if (_lastSample is { } previous)
            {
                _maxSampleGap = Max(_maxSampleGap, monotonicTime - previous);
            }
            _samples++;
            _firstSample ??= monotonicTime;
            _lastSample = monotonicTime;

            foreach ((Guid sessionId, Observation observation) in _active)
            {
                ControlSessionSummary? present = snapshot.FirstOrDefault(item => item.SessionId == sessionId);
                if (present is null)
                {
                    observation.AbsentAfterPositive |= observation.Count > 0;
                    continue;
                }
                if (present.ConnectionId != observation.ConnectionId)
                {
                    observation.ConnectionMismatch = true;
                    continue;
                }
                observation.Interrupted |= observation.AbsentAfterPositive;
                if (observation.Last is { } lastPositive)
                {
                    observation.MaxGap = Max(observation.MaxGap, monotonicTime - lastPositive);
                }
                observation.Count++;
                observation.First ??= monotonicTime;
                observation.Last = monotonicTime;
            }
        }
    }

    internal IReadOnlyList<ControlSessionSummary> BeginHostStop(SessionRegistry registry)
    {
        lock (_gate)
        {
            _stopping = true;
            // 停机快照只说明谁将被强制关闭，绝不充当定时正向样本。
            return registry.Snapshot();
        }
    }

    internal HostSessionObservation End(
        Guid sessionId, Guid connectionId, bool authenticated, CancellationToken hostCancellation, SessionRegistry registry)
    {
        lock (_gate)
        {
            IReadOnlyList<ControlSessionSummary> snapshot = registry.Snapshot();
            TimeSpan now = Stopwatch.GetElapsedTime(_origin);
            // 不能在等待采样锁之前缓存取消 bool；等待期间发生的停机也属于结束重叠。
            return End(sessionId, connectionId, authenticated, hostCancellation.IsCancellationRequested, snapshot, now);
        }
    }

    /// <summary>认证 Run 已退出之后的注销检查；最终为空不能倒推出曾经登记或保持。</summary>
    internal HostSessionObservation End(
        Guid sessionId, Guid connectionId, bool authenticated, bool cancelledByHost,
        IReadOnlyList<ControlSessionSummary> snapshot, TimeSpan monotonicTime)
    {
        lock (_gate)
        {
            if (monotonicTime < TimeSpan.Zero || (_lastSample is { } last && monotonicTime < last))
            {
                throw new ArgumentOutOfRangeException(nameof(monotonicTime), "结束时刻必须单调。");
            }
            bool tracked = _active.Remove(sessionId, out Observation? observation);
            long count = observation?.Count ?? 0;
            bool deregistered = !snapshot.Any(item => item.SessionId == sessionId);
            bool hostForced = authenticated && (_stopping || cancelledByHost);
            TimeSpan? span = count > 0 ? observation!.Last - observation.First : null;
            TimeSpan? endGap = count > 0 ? monotonicTime - observation!.Last : null;
            bool mismatch = observation is not null &&
                (observation.ConnectionId != connectionId || observation.ConnectionMismatch);
            bool interrupted = observation?.Interrupted ?? false;

            AcceptanceOutcome outcome;
            string detail;
            if (!deregistered || mismatch || (!authenticated && count > 0))
            {
                outcome = AcceptanceOutcome.Fail;
                detail = "registry 注销或连接身份与认证终态矛盾";
                _violations++;
            }
            else if (!authenticated)
            {
                outcome = AcceptanceOutcome.PreconditionUnmet;
                detail = "未成功认证；拒绝原因另记终态桶";
            }
            else if (hostForced)
            {
                outcome = AcceptanceOutcome.PreconditionUnmet;
                detail = "host 停机强制关闭或与结束重叠；不作自然释放证据";
            }
            else if (!tracked || count < 2 || span < RequiredObservedSpan ||
                     observation!.MaxGap > AllowedObservationGap || endGap > AllowedObservationGap || interrupted)
            {
                outcome = AcceptanceOutcome.PreconditionUnmet;
                detail = "正向观测不足或有采样缺口；不能证明持续保持";
            }
            else
            {
                outcome = AcceptanceOutcome.Pass;
                detail = "实际正向样本覆盖保持窗口，Run 结束后已注销，未由 host 停机关闭";
            }

            if (authenticated)
            {
                _authenticatedEnded++;
                if (outcome == AcceptanceOutcome.Pass) { _qualified++; }
                if (outcome == AcceptanceOutcome.PreconditionUnmet) { _unmet++; }
                if (hostForced) { _forced++; }
            }
            return new HostSessionObservation(sessionId, connectionId, authenticated, tracked, count,
                observation?.First, observation?.Last, span, count > 1 ? observation!.MaxGap : null,
                endGap, interrupted, deregistered, hostForced, outcome, detail);
        }
    }

    internal AcceptanceOutcome Evaluate(bool partitionOk, long handlerFaults, bool cleanShutdown)
    {
        lock (_gate)
        {
            if (!partitionOk || handlerFaults > 0) { return AcceptanceOutcome.HarnessError; }
            if (!cleanShutdown || _violations > 0) { return AcceptanceOutcome.Fail; }
            if (_authenticatedEnded == 0 || _qualified == 0 || _unmet > 0 ||
                _trackingDropped > 0 || _active.Count > 0)
            {
                return AcceptanceOutcome.PreconditionUnmet;
            }
            return AcceptanceOutcome.Pass;
        }
    }

    internal string FormatSummary()
    {
        lock (_gate)
        {
            return $"[HOST][EVIDENCE] registrySamples={_samples} " +
                   $"sampleSpanMs={Milliseconds(_lastSample - _firstSample)} " +
                   $"maxSampleGapMs={(_samples > 1 ? Milliseconds(_maxSampleGap) : "UNOBSERVED")} " +
                   $"trackedActive={_active.Count} trackingCapacity={_capacity} trackingDropped={_trackingDropped} " +
                   $"authenticatedEnded={_authenticatedEnded} qualifiedNaturalRelease={_qualified} " +
                   $"holdUnmet={_unmet} hostForcedClose={_forced} registryViolations={_violations}";
        }
    }

    internal static string Milliseconds(TimeSpan? value) => value is { } duration
        ? duration.TotalMilliseconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
        : "UNOBSERVED";

    private static TimeSpan Max(TimeSpan first, TimeSpan second) => first >= second ? first : second;

    private sealed class Observation(Guid connectionId)
    {
        internal Guid ConnectionId { get; } = connectionId;
        internal long Count;
        internal TimeSpan? First;
        internal TimeSpan? Last;
        internal TimeSpan MaxGap;
        internal bool AbsentAfterPositive;
        internal bool Interrupted;
        internal bool ConnectionMismatch;
    }
}

internal sealed record HostSessionObservation(
    Guid SessionId,
    Guid ConnectionId,
    bool Authenticated,
    bool Tracked,
    long ObservedSamples,
    TimeSpan? FirstObserved,
    TimeSpan? LastObserved,
    TimeSpan? ObservedSpan,
    TimeSpan? MaxObservedGap,
    TimeSpan? EndObservationGap,
    bool ObservationInterrupted,
    bool DeregisteredAtEnd,
    bool HostForcedClose,
    AcceptanceOutcome Outcome,
    string Detail)
{
    internal string Format() =>
        $"[HOST][SESSION] sessionId={SessionId} connectionId={ConnectionId} authenticated={Authenticated} " +
        $"tracked={Tracked} observedSamples={ObservedSamples} " +
        $"firstObservedMs={HostSessionEvidence.Milliseconds(FirstObserved)} " +
        $"lastObservedMs={HostSessionEvidence.Milliseconds(LastObserved)} " +
        $"observedSpanMs={HostSessionEvidence.Milliseconds(ObservedSpan)} " +
        $"maxObservedGapMs={HostSessionEvidence.Milliseconds(MaxObservedGap)} " +
        $"endObservationGapMs={HostSessionEvidence.Milliseconds(EndObservationGap)} " +
        $"observationInterrupted={ObservationInterrupted} deregisteredAtRunEnd={DeregisteredAtEnd} " +
        $"hostForcedClose={HostForcedClose} evidence={Outcome.Code()} detail={Detail}";
}
