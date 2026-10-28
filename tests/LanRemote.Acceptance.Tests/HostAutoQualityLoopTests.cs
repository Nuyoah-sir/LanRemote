using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LanRemote.App.Services;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using LanRemote.Sessions;
using LanRemote.Transport;
using Xunit;

namespace LanRemote.Acceptance.Tests;

/// <summary>Host 自动画质周期 worker：只在有真实测量、当前档位与目标显示器时才换代，换代成功才写档位。</summary>
public sealed class HostAutoQualityLoopTests
{
    private static readonly Guid Session = new("8d4d2f5b-9c03-4e2f-9d2f-2b6f6a6d5c31");
    private static readonly DisplayInfo Display = new(new DisplayId(3), "Second", 0, 0, 1280, 720, true);

    [Fact(Timeout = 30_000)]
    public async Task Loop_skips_sources_without_measured_producer_or_display()
    {
        int prepared = 0;
        Guid plainSession = Guid.NewGuid(), noDisplaySession = Guid.NewGuid();
        ReplaceableVideoFrameSource plain = new(plainSession);
        await plain.ReplaceAsync(new PlainProducer());
        ReplaceableVideoFrameSource noDisplay = new(noDisplaySession);
        await noDisplay.ReplaceAsync(new MeasuredProducer(null));
        ControlPingTelemetry roundTrips = new();
        RegisterBadRtt(roundTrips, plainSession);
        RegisterBadRtt(roundTrips, noDisplaySession);
        HostAutoQualityLoop loop = CreateLoop(() => prepared++, new SessionQualityResolver(() => VideoQualitySettings.HighQuality),
            roundTrips, [KeyValuePair.Create(plainSession, plain), KeyValuePair.Create(noDisplaySession, noDisplay)]);
        _ = loop.Start();
        try
        {
            await WaitAsync(() => Volatile.Read(ref prepared) > 0, expect: false);
            Assert.Equal(0, prepared);
        }
        finally { await StopAsync(loop); }
        await plain.DisposeAsync();
        await noDisplay.DisposeAsync();
    }

    [Fact(Timeout = 30_000)]
    public async Task Terminated_source_is_never_replaced_even_with_bad_samples()
    {
        int prepared = 0;
        ControlPingTelemetry roundTrips = new();
        RegisterBadRtt(roundTrips, Session);
        ReplaceableVideoFrameSource source = new(Session);
        await source.ReplaceAsync(new MeasuredProducer(Display));
        await source.DisposeAsync();
        HostAutoQualityLoop loop = CreateLoop(() => prepared++, new SessionQualityResolver(() => VideoQualitySettings.HighQuality),
            roundTrips, [KeyValuePair.Create(Session, source)]);
        _ = loop.Start();
        try
        {
            await WaitAsync(() => Volatile.Read(ref prepared) > 0, expect: false);
            Assert.Equal(0, prepared);
        }
        finally { await StopAsync(loop); }
    }

    [Fact(Timeout = 30_000)]
    public async Task No_current_preset_is_never_managed()
    {
        int prepared = 0;
        ControlPingTelemetry roundTrips = new();
        RegisterBadRtt(roundTrips, Session);
        ReplaceableVideoFrameSource source = new(Session);
        await source.ReplaceAsync(new MeasuredProducer(Display));
        HostAutoQualityLoop loop = CreateLoop(() => prepared++, new SessionQualityResolver(() => null),
            roundTrips, [KeyValuePair.Create(Session, source)]);
        _ = loop.Start();
        try
        {
            await WaitAsync(() => Volatile.Read(ref prepared) > 0, expect: false);
            Assert.Equal(0, prepared);
        }
        finally { await StopAsync(loop); }
        await source.DisposeAsync();
    }

    [Fact(Timeout = 30_000)]
    public async Task Consecutive_bad_samples_prepare_balanced_candidate_and_write_preset()
    {
        int prepared = 0;
        DisplayInfo? usedDisplay = null;
        VideoQualitySettings? usedSettings = null;
        MeasuredProducer candidate = new(Display);
        ControlPingTelemetry roundTrips = new();
        RegisterBadRtt(roundTrips, Session);
        SessionQualityResolver quality = new(() => VideoQualitySettings.HighQuality);
        ReplaceableVideoFrameSource source = new(Session);
        MeasuredProducer initial = new(Display);
        await source.ReplaceAsync(initial);
        HostAutoQualityLoop loop = CreateLoop(() => Interlocked.Increment(ref prepared), quality,
            roundTrips, [KeyValuePair.Create(Session, source)], (_, display, settings, _) =>
            {
                usedDisplay = display;
                usedSettings = settings;
            }, candidate);
        _ = loop.Start();
        try
        {
            bool replaced = await WaitAsync(() => Volatile.Read(ref prepared) > 0, expect: true);
            Assert.True(replaced, "连续坏样本后未创建候选。");
            Assert.Equal(Display, usedDisplay);
            Assert.NotNull(usedSettings);
            Assert.Equal("balanced", VideoQualityPresets.NameOf(usedSettings!));
            VideoQualitySettings? now = quality.Resolve(Session);
            Assert.NotNull(now);
            Assert.Equal("balanced", VideoQualityPresets.NameOf(now!));
            Assert.Equal(1, initial.StopCalls);
            Assert.True(source.ActiveProducer is null || ReferenceEquals(source.ActiveProducer, candidate));
        }
        finally { await StopAsync(loop); }
        await source.DisposeAsync();
    }

    private static void RegisterBadRtt(ControlPingTelemetry roundTrips, Guid sessionId)
    {
        PingRttMeter meter = new(TimeProvider.System);
        roundTrips.Register(sessionId, meter);
        meter.Arm(0);
        Thread.Sleep(160);
        Assert.True(meter.Settle(0, out TimeSpan measured));
        Assert.True(measured >= TimeSpan.FromMilliseconds(120));
    }

    private static HostAutoQualityLoop CreateLoop(Action onPrepare, SessionQualityResolver quality,
        ControlPingTelemetry roundTrips, IReadOnlyList<KeyValuePair<Guid, ReplaceableVideoFrameSource>> snapshot,
        Action<Guid, DisplayInfo, VideoQualitySettings, CancellationToken>? observePrepare = null,
        IVideoFrameProducer? candidate = null)
    {
        IVideoFrameProducer prepared = candidate ?? new MeasuredProducer(Display);
        return new HostAutoQualityLoop(new AutoQualityController(), roundTrips, quality,
            (sessionId, display, settings, token) =>
            {
                onPrepare();
                observePrepare?.Invoke(sessionId, display, settings, token);
                return new ValueTask<IVideoFrameProducer>(prepared);
            },
            () => snapshot, TimeProvider.System, TimeSpan.FromMilliseconds(20));
    }

    private static async Task<bool> WaitAsync(Func<bool> predicate, bool expect)
    {
        long started = Environment.TickCount64;
        while (predicate() != expect && Environment.TickCount64 - started < 2_000) await Task.Delay(10);
        if (!expect) await Task.Delay(120); // 给可能被误触发的换代一次观察窗口。
        return predicate();
    }

    private static async Task StopAsync(HostAutoQualityLoop loop)
    {
        loop.RequestStop();
        if (loop.Completion is { } completion)
        {
            try { await completion; }
            catch { /* worker 内部已记录；停止路径不因评估错误失败。 */ }
        }
    }

    private sealed class PlainProducer : IVideoFrameProducer
    {
        public Task Completion { get; } = Task.CompletedTask;
        public void Start() { }
        public ValueTask<EncodedFrame?> ReadNextAsync(CancellationToken cancellationToken = default) => new((EncodedFrame?)null);
        public Task StopAsync() => Completion;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MeasuredProducer : IVideoFrameProducer, IVideoProducerTelemetry
    {
        private int _stopCalls, _startCalls;
        internal MeasuredProducer(DisplayInfo? display, double fps = 30, long frames = 10,
            VideoQualitySettings? quality = null)
        {
            Display = display;
            ActualFps = fps;
            EncodedFrames = frames;
            Quality = quality ?? VideoQualitySettings.Balanced;
        }
        internal int StopCalls => Volatile.Read(ref _stopCalls);
        internal int StartCalls => Volatile.Read(ref _startCalls);
        public Task Completion { get; } = Task.CompletedTask;
        public long EncodedFrames { get; }
        public double ActualFps { get; }
        public DisplayInfo? Display { get; }
        // 本片新增的只读档位接缝：桩固定为构造时给定值，不影响原有换代/跳过的断言。
        public VideoQualitySettings Quality { get; }
        public void Start() => Interlocked.Increment(ref _startCalls);
        public ValueTask<EncodedFrame?> ReadNextAsync(CancellationToken cancellationToken = default) => new((EncodedFrame?)null);
        public Task StopAsync()
        {
            Interlocked.Increment(ref _stopCalls);
            return Completion;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
