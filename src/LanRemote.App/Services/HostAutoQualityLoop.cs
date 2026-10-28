using System.Collections.Generic;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using LanRemote.Sessions;
using LanRemote.Transport;
using Microsoft.Extensions.Logging;

namespace LanRemote.App.Services;

/// <summary>Host 自动画质周期 worker：按会话读取 RTT/实际帧率，决策后创建候选并换代。</summary>
/// <remarks>
/// 每轮只做一次快照，逐个会话串行评估；换代在途或来源终态直接跳过，不排队。
/// 候选先由显式画质创建（尚未 Start），换代成功并接管后才写会话档位；换代异步失败时候选不归本循环。
/// 同步拒绝（来源已封口/已有换代在途）时本循环仍拥有候选，必须自己停止并释放，不遗弃后台根。
/// </remarks>
internal sealed class HostAutoQualityLoop
{
    private readonly object _gate = new();
    private readonly AutoQualityController _controller;
    private readonly ControlPingTelemetry _roundTrips;
    private readonly SessionQualityResolver _quality;
    private readonly Func<Guid, DisplayInfo, VideoQualitySettings, CancellationToken, ValueTask<IVideoFrameProducer>> _prepareCandidate;
    private readonly Func<IReadOnlyList<KeyValuePair<Guid, ReplaceableVideoFrameSource>>> _snapshot;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _interval;
    private readonly ILogger? _logger;
    private readonly CancellationTokenSource _stop = new();
    private Task? _completion;
    private bool _stopping;

    internal HostAutoQualityLoop(
        AutoQualityController controller,
        ControlPingTelemetry roundTrips,
        SessionQualityResolver quality,
        Func<Guid, DisplayInfo, VideoQualitySettings, CancellationToken, ValueTask<IVideoFrameProducer>> prepareCandidate,
        Func<IReadOnlyList<KeyValuePair<Guid, ReplaceableVideoFrameSource>>> snapshot,
        TimeProvider clock,
        TimeSpan interval,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(roundTrips);
        ArgumentNullException.ThrowIfNull(quality);
        ArgumentNullException.ThrowIfNull(prepareCandidate);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(clock);
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval), interval, "评估间隔必须为正。");
        _controller = controller;
        _roundTrips = roundTrips;
        _quality = quality;
        _prepareCandidate = prepareCandidate;
        _snapshot = snapshot;
        _clock = clock;
        _interval = interval;
        _logger = logger;
    }

    internal Task? Completion { get { lock (_gate) return _completion; } }

    internal Task Start()
    {
        lock (_gate)
        {
            if (_completion is not null) throw new InvalidOperationException("自动画质worker不能重复启动。");
            if (_stopping) return _completion = Task.CompletedTask;
            return _completion = Task.Run(RunAsync);
        }
    }

    internal void RequestStop()
    {
        bool cancel;
        lock (_gate)
        {
            cancel = !_stopping;
            _stopping = true;
        }
        if (cancel) _stop.Cancel();
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(_interval, _clock, _stop.Token).ConfigureAwait(false);
                _stop.Token.ThrowIfCancellationRequested();
                foreach ((Guid sessionId, ReplaceableVideoFrameSource source) in _snapshot())
                {
                    try { await EvaluateAsync(sessionId, source).ConfigureAwait(false); }
                    catch (Exception error) { _logger?.LogWarning(error, "会话 {Session} 的自动画质评估失败，本轮跳过。", sessionId); }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally { _stop.Dispose(); }
    }

    private async Task EvaluateAsync(Guid sessionId, ReplaceableVideoFrameSource source)
    {
        if (sessionId == Guid.Empty || source.Termination.IsCompleted || source.HasReplacementInFlight) return;
        if (source.ActiveProducer is not IVideoProducerTelemetry measured || measured.Display is not { } display) return;
        if (_quality.Resolve(sessionId) is not { } current) return;
        string? currentPreset = VideoQualityPresets.NameOf(current);
        TimeSpan? roundTrip = _roundTrips.TryGet(sessionId, out ControlPingTelemetry.SessionSnapshot? ping) ? ping!.LatestRtt : null;
        AutoQualityController.Decision decision = _controller.Evaluate(sessionId,
            new AutoQualityController.Observation(roundTrip, measured.ActualFps, currentPreset));
        if (decision.TargetPreset is not { } target) return;

        VideoQualitySettings settings = VideoQualityPresets.Resolve(target);
        IVideoFrameProducer candidate = await _prepareCandidate(sessionId, display, settings, _stop.Token).ConfigureAwait(false);
        bool handedOver = false;
        try
        {
            Task replacing = source.ReplaceAsync(candidate);
            handedOver = true;
            await replacing.ConfigureAwait(false);
        }
        catch (Exception) when (!handedOver)
        {
            // 同步拒绝时候选仍归本循环；换代一旦移交，异步失败由来源自己的退休槽负责。
            await candidate.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        _quality.SetPreset(sessionId, target);
    }
}
