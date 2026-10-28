using System.Collections.Generic;
using LanRemote.Core.Models;

namespace LanRemote.App.Services;

/// <summary>按会话累积 RTT 与实际帧率样本的自动画质决策核心；只给出目标预设名，不创建/换代生产者。</summary>
/// <remarks>
/// 只管理命名预设：手工档位或未知名一律不改档且清空计数。无有效样本（RTT 缺失、帧率未启动/非有限）既不算差也不算好，
/// 不虚构“低质量”证据，也不清零已有计数。升降档都要求连续样本达到阈值，切换后立即清零，避免抖动；
/// 本类不定时、不收发字节、不接触会话表，也不改变 <see cref="VideoQualityPresets"/> 的严格解析语义。
/// </remarks>
internal sealed class AutoQualityController
{
    /// <summary>一次观测：RTT 与实际帧率都来自真实测量；帧率是启动以来累计平均编码帧率，不是目标帧率。</summary>
    internal sealed record Observation(TimeSpan? RoundTrip, double ActualFps, string? CurrentPreset);

    internal sealed record Decision(string? TargetPreset, int GoodSamples, int BadSamples);

    internal static TimeSpan GoodRoundTrip { get; } = TimeSpan.FromMilliseconds(40);
    internal static TimeSpan BadRoundTrip { get; } = TimeSpan.FromMilliseconds(120);

    /// <summary>连续同向样本达到该次数才换档；滞回区间内的混合样本清零计数。</summary>
    internal const int RequiredSamples = 3;
    private const double DowngradeFpsRatio = 0.80;
    private const double UpgradeFpsRatio = 0.95;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, State> _states = new();

    internal Decision Evaluate(Guid sessionId, Observation observation)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("会话标识不可为空。", nameof(sessionId));
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.CurrentPreset is not { } current ||
            !VideoQualityPresets.TryResolve(current, out VideoQualitySettings? settings))
        {
            Reset(sessionId);
            return new(null, 0, 0);
        }

        if (!IsValidSample(observation)) return Snapshot(sessionId);

        VideoQualitySettings active = settings!;
        bool bad = observation.RoundTrip!.Value >= BadRoundTrip ||
                   observation.ActualFps < active.TargetFps * DowngradeFpsRatio;
        bool good = !bad && observation.RoundTrip.Value <= GoodRoundTrip &&
                    observation.ActualFps >= active.TargetFps * UpgradeFpsRatio;

        lock (_gate)
        {
            State state = _states.TryGetValue(sessionId, out State? existing) ? existing : _states[sessionId] = new();
            if (bad) { state.Bad++; state.Good = 0; }
            else if (good) { state.Good++; state.Bad = 0; }
            else { state.Good = 0; state.Bad = 0; }

            IReadOnlyList<string> names = VideoQualityPresets.Names;
            int index = IndexOf(names, current);
            string? target = null;
            if (state.Bad >= RequiredSamples && index > 0)
            {
                target = names[index - 1];
                state.Good = state.Bad = 0;
            }
            else if (state.Good >= RequiredSamples && index < names.Count - 1)
            {
                target = names[index + 1];
                state.Good = state.Bad = 0;
            }
            return new(target, state.Good, state.Bad);
        }
    }

    internal Decision Snapshot(Guid sessionId)
    {
        lock (_gate)
            return _states.TryGetValue(sessionId, out State? state)
                ? new(null, state.Good, state.Bad) : new(null, 0, 0);
    }

    internal void Reset(Guid sessionId)
    {
        lock (_gate) _states.Remove(sessionId);
    }

    internal void Clear()
    {
        lock (_gate) _states.Clear();
    }

    private static bool IsValidSample(Observation observation) =>
        observation.RoundTrip is { } rtt && rtt >= TimeSpan.Zero &&
        double.IsFinite(observation.ActualFps) && observation.ActualFps > 0;

    private static int IndexOf(IReadOnlyList<string> names, string current)
    {
        for (int index = 0; index < names.Count; index++)
            if (string.Equals(names[index], current, StringComparison.Ordinal)) return index;
        return -1;
    }

    private sealed class State
    {
        internal int Good;
        internal int Bad;
    }
}
