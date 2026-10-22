// 只验证每会话画质接线：工厂按会话解析回调与 App 会话画质解析器的快照/独立/清理；
// 不建立真实网络、GDI 或 TLS，不代表产品侧已由 UI 或协议分配画质。

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LanRemote.App.Services;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using LanRemote.Sessions;
using Xunit;

namespace LanRemote.Acceptance.Tests;

public sealed class SessionQualityCallbackTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    private static DisplayInfo Primary => new(new DisplayId(0), "主屏", 0, 0, 1920, 1080, true);

    [Fact(Timeout = 30_000)]
    public async Task Callback_quality_applies_only_to_its_own_session_and_null_falls_back_to_factory()
    {
        Rig rig = new();
        VideoQualitySettings perSession = VideoQualitySettings.LowLatency;
        VideoQualitySettings factoryDefault = VideoQualitySettings.HighQuality;
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        FramePipelineVideoProducerFactory factory = rig.Factory(factoryDefault,
            session => session == first ? perSession : null);
        List<IVideoFrameProducer> created = [];
        try
        {
            IVideoFrameProducer firstProducer = await factory.CreateAsync(first, CancellationToken.None);
            IVideoFrameProducer secondProducer = await factory.CreateAsync(second, CancellationToken.None);
            created.Add(firstProducer);
            created.Add(secondProducer);
            Assert.Equal(perSession, PipelineSettings(firstProducer));
            Assert.Equal(factoryDefault, PipelineSettings(secondProducer));
        }
        finally { await rig.Drain(created); }
    }

    [Theory(Timeout = 30_000)]
    [InlineData(7, 0.75, 60, false)]
    [InlineData(20, 0.75, 60, true)]
    public async Task Invalid_callback_quality_is_rejected_before_any_dependency(
        int fps, double scale, int quality, bool auto)
    {
        Rig rig = new();
        FramePipelineVideoProducerFactory factory = rig.Factory(VideoQualitySettings.Balanced,
            _ => new VideoQualitySettings(fps, scale, quality, auto));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => factory.CreateAsync(Guid.NewGuid(), CancellationToken.None).AsTask());
        Assert.Equal(0, rig.CaptureCreates);
        Assert.Equal(0, rig.EncoderCreates);
    }

    [Fact(Timeout = 30_000)]
    public async Task Callback_failure_propagates_without_creating_dependencies()
    {
        Rig rig = new();
        FramePipelineVideoProducerFactory factory = rig.Factory(VideoQualitySettings.Balanced,
            _ => throw new InvalidOperationException("会话画质不可用"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.CreateAsync(Guid.NewGuid(), CancellationToken.None).AsTask());
        Assert.Equal(0, rig.CaptureCreates);
        Assert.Equal(0, rig.EncoderCreates);
    }

    [Fact(Timeout = 30_000)]
    public async Task Without_callback_the_factory_default_is_unchanged()
    {
        Rig rig = new();
        FramePipelineVideoProducerFactory factory = rig.Factory(VideoQualitySettings.LowLatency);
        List<IVideoFrameProducer> created = [];
        try
        {
            IVideoFrameProducer producer = await factory.CreateAsync(Guid.NewGuid(), CancellationToken.None);
            created.Add(producer);
            Assert.Equal(VideoQualitySettings.LowLatency, PipelineSettings(producer));
        }
        finally { await rig.Drain(created); }
    }

    private static VideoQualitySettings PipelineSettings(IVideoFrameProducer producer)
    {
        object pipeline = RequiredField(producer.GetType(), "_pipeline").GetValue(producer)!;
        return (VideoQualitySettings)RequiredField(pipeline.GetType(), "_settings").GetValue(pipeline)!;
    }

    private static FieldInfo RequiredField(Type owner, string name) =>
        owner.GetField(name, Hidden) ?? throw new InvalidOperationException("缺少字段: " + name);

    private sealed class Rig
    {
        internal int CaptureCreates;
        internal int EncoderCreates;

        internal FramePipelineVideoProducerFactory Factory(VideoQualitySettings? settings,
            Func<Guid, VideoQualitySettings?>? sessionQuality = null) =>
            new(
                () =>
                {
                    Interlocked.Increment(ref CaptureCreates);
                    return new FakeCapture();
                },
                () =>
                {
                    Interlocked.Increment(ref EncoderCreates);
                    return new FakeEncoder();
                }, settings: settings, sessionQuality: sessionQuality);

        internal async Task Drain(List<IVideoFrameProducer> producers)
        {
            foreach (IVideoFrameProducer producer in producers)
            {
                try
                {
                    Task stop = producer.StopAsync();
                    await Task.WhenAny(stop, Task.Delay(Guard)).ConfigureAwait(false);
                    await producer.DisposeAsync().ConfigureAwait(false);
                    await Task.WhenAny(producer.Completion, Task.Delay(Guard)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 清场只负责不泄漏 fixture 资源；断言失败已在用例内先发生。
                }
            }
        }
    }

    private sealed class FakeCapture : IScreenCaptureBackend, IDisposable
    {
        public IReadOnlyList<DisplayInfo> GetDisplays() => [Primary];

        public ValueTask<CapturedFrame> CaptureAsync(DisplayId displayId, CancellationToken cancellationToken)
        {
            IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(2 * 2 * 4);
            return ValueTask.FromResult(new CapturedFrame(displayId, 2, 2, 2 * 4,
                FramePixelFormat.Bgra32, owner, 1L));
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeEncoder : IFrameEncoder, IDisposable
    {
        public ValueTask<EncodedFrame> EncodeAsync(CapturedFrame frame, VideoQualitySettings settings,
            CancellationToken cancellationToken)
        {
            IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(16);
            owner.Memory.Span[..16].Fill((byte)1);
            return ValueTask.FromResult(new EncodedFrame(VideoCodec.Jpeg, frame.Width, frame.Height, 1UL, 2L,
                (byte)settings.JpegQuality, owner, 16));
        }

        public void Dispose()
        {
        }
    }
}

public sealed class SessionQualityResolverTests
{
    [Fact]
    public void First_resolve_snapshots_current_and_later_host_change_does_not_affect_it()
    {
        VideoQualitySettings? current = VideoQualitySettings.Balanced;
        SessionQualityResolver resolver = new(() => current);
        Guid session = Guid.NewGuid();

        Assert.Equal(VideoQualitySettings.Balanced, resolver.Resolve(session));
        current = VideoQualitySettings.HighQuality;
        // 已快照的会话不受主机档位后续变化影响。
        Assert.Equal(VideoQualitySettings.Balanced, resolver.Resolve(session));
    }

    [Fact]
    public void Two_sessions_hold_independent_snapshots()
    {
        VideoQualitySettings? current = VideoQualitySettings.Balanced;
        SessionQualityResolver resolver = new(() => current);
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();

        Assert.Equal(VideoQualitySettings.Balanced, resolver.Resolve(first));
        current = VideoQualitySettings.LowLatency;
        Assert.Equal(VideoQualitySettings.LowLatency, resolver.Resolve(second));
        Assert.Equal(VideoQualitySettings.Balanced, resolver.Resolve(first));
        Assert.Equal(2, resolver.Count);
    }

    [Fact]
    public void Null_current_falls_back_without_recording_a_session_entry()
    {
        SessionQualityResolver resolver = new(() => null);
        Assert.Null(resolver.Resolve(Guid.NewGuid()));
        Assert.Equal(0, resolver.Count);
    }

    [Fact]
    public void Set_overrides_the_session_and_rejects_invalid_or_empty_input()
    {
        VideoQualitySettings? current = VideoQualitySettings.Balanced;
        SessionQualityResolver resolver = new(() => current);
        Guid session = Guid.NewGuid();

        resolver.Set(session, VideoQualitySettings.HighQuality);
        Assert.Equal(VideoQualitySettings.HighQuality, resolver.Resolve(session));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => resolver.Set(session, new VideoQualitySettings(20, 0.75, 60, true)));
        // 非法设定不得改写该会话已持有的合法档位。
        Assert.Equal(VideoQualitySettings.HighQuality, resolver.Resolve(session));
        Assert.Throws<ArgumentNullException>(() => resolver.Set(session, null!));
        Assert.Throws<ArgumentException>(() => resolver.Set(Guid.Empty, VideoQualitySettings.Balanced));
        Assert.Throws<ArgumentException>(() => resolver.Resolve(Guid.Empty));
    }

    [Fact]
    public void Clear_drops_every_session_entry()
    {
        VideoQualitySettings? current = VideoQualitySettings.Balanced;
        SessionQualityResolver resolver = new(() => current);
        Guid session = Guid.NewGuid();
        Assert.Equal(VideoQualitySettings.Balanced, resolver.Resolve(session));

        current = VideoQualitySettings.LowLatency;
        resolver.Clear();
        Assert.Equal(0, resolver.Count);
        Assert.Equal(VideoQualitySettings.LowLatency, resolver.Resolve(session));
    }
}
