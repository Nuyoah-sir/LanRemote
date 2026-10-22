// 只验证每会话画质底座：画质按会话传入、只作用于本会话管线、在任何依赖创建之前校验；
// 不建立真实网络、GDI 或 TLS，不代表产品侧已按会话分配画质。

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using Xunit;

namespace LanRemote.Sessions.Tests;

public sealed class FramePipelineSessionQualityTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    private static DisplayInfo Primary =>
        new(new DisplayId(0), "主屏", 0, 0, 1920, 1080, true);

    private static DisplayInfo Secondary =>
        new(new DisplayId(9), "副屏", 1920, 0, 1280, 720, false);

    [Fact(Timeout = 30_000)]
    public async Task Session_quality_reaches_its_own_pipeline_and_never_mutates_factory_defaults()
    {
        Rig rig = new();
        VideoQualitySettings factoryDefault = VideoQualitySettings.HighQuality;
        FramePipelineVideoProducerFactory factory = rig.Factory(settings: factoryDefault);
        List<IVideoFrameProducer> created = [];
        try
        {
            IVideoFrameProducer perSession = await factory.CreateWithSessionQualityAsync(
                Guid.NewGuid(), VideoQualitySettings.LowLatency, CancellationToken.None);
            created.Add(perSession);
            Assert.Equal(VideoQualitySettings.LowLatency, PipelineSettings(perSession));

            // 同一工厂后续不指定画质的会话仍用工厂档位；按会话画质不改写工厂默认。
            IVideoFrameProducer plain = await factory.CreateAsync(Guid.NewGuid(), CancellationToken.None);
            created.Add(plain);
            Assert.Equal(factoryDefault, PipelineSettings(plain));
            Assert.Equal(2, rig.CaptureCreates);
            Assert.Equal(2, rig.EncoderCreates);
        }
        finally { await DrainAsync(created); }
    }

    [Fact(Timeout = 30_000)]
    public async Task Two_sessions_hold_independent_quality_and_encoding_uses_each_own_settings()
    {
        Rig rig = new();
        FramePipelineVideoProducerFactory factory = rig.Factory();
        List<IVideoFrameProducer> created = [];
        try
        {
            IVideoFrameProducer first = await factory.CreateWithSessionQualityAsync(
                Guid.NewGuid(), VideoQualitySettings.LowLatency, CancellationToken.None);
            IVideoFrameProducer second = await factory.CreateWithSessionQualityAsync(
                Guid.NewGuid(), VideoQualitySettings.HighQuality, CancellationToken.None);
            created.Add(first);
            created.Add(second);
            Assert.Equal(VideoQualitySettings.LowLatency, PipelineSettings(first));
            Assert.Equal(VideoQualitySettings.HighQuality, PipelineSettings(second));

            // 真实跑到编码器：两会话各按自己的画质编码，互不被对方覆盖。
            first.Start();
            second.Start();
            EncodedFrame? firstFrame = await ReadAsync(first);
            EncodedFrame? secondFrame = await ReadAsync(second);
            Assert.NotNull(firstFrame);
            Assert.NotNull(secondFrame);
            Assert.Contains(VideoQualitySettings.LowLatency, rig.EncodedQualities);
            Assert.Contains(VideoQualitySettings.HighQuality, rig.EncodedQualities);
            Assert.NotEqual(PipelineSettings(first), PipelineSettings(second));
            firstFrame!.Dispose();
            secondFrame!.Dispose();
        }
        finally { await DrainAsync(created); }
    }

    [Theory(Timeout = 30_000)]
    [InlineData(7, 0.75, 60, false)]
    [InlineData(30, 0.90, 60, false)]
    [InlineData(20, 0.75, 90, false)]
    [InlineData(20, 0.75, 30, false)]
    [InlineData(20, 0.75, 60, true)]
    public async Task Invalid_session_quality_is_rejected_before_any_dependency_is_created(
        int fps, double scale, int quality, bool auto)
    {
        Rig rig = new();
        FramePipelineVideoProducerFactory factory = rig.Factory();
        VideoQualitySettings bad = new(fps, scale, quality, auto);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => factory.CreateWithSessionQualityAsync(Guid.NewGuid(), bad, CancellationToken.None).AsTask());
        // 拒绝发生在委托、显示器枚举与像素预算之前：不创建采集后端或编码器，也没有待清理依赖。
        Assert.Equal(0, rig.CaptureCreates);
        Assert.Equal(0, rig.EncoderCreates);
        Assert.Equal(0, rig.DisplayCalls);
    }

    [Fact(Timeout = 30_000)]
    public async Task Empty_session_id_is_rejected_before_quality_without_creating_dependencies()
    {
        Rig rig = new();
        FramePipelineVideoProducerFactory factory = rig.Factory();
        await Assert.ThrowsAsync<ArgumentException>(
            () => factory.CreateWithSessionQualityAsync(Guid.Empty, VideoQualitySettings.Balanced, CancellationToken.None).AsTask());
        Assert.Equal(0, rig.CaptureCreates);
        Assert.Equal(0, rig.EncoderCreates);
    }

    [Fact(Timeout = 30_000)]
    public async Task Null_session_quality_is_rejected_without_creating_dependencies()
    {
        Rig rig = new();
        FramePipelineVideoProducerFactory factory = rig.Factory();
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => factory.CreateWithSessionQualityAsync(Guid.NewGuid(), null!, CancellationToken.None).AsTask());
        Assert.Equal(0, rig.CaptureCreates);
        Assert.Equal(0, rig.EncoderCreates);
    }

    [Fact(Timeout = 30_000)]
    public async Task Fixed_display_target_with_session_quality_uses_both()
    {
        Rig rig = new([Primary, Secondary]);
        FramePipelineVideoProducerFactory factory = rig.Factory();
        List<IVideoFrameProducer> created = [];
        try
        {
            IVideoFrameProducer producer = await factory.CreateForDisplayWithSessionQualityAsync(
                Guid.NewGuid(), Secondary, VideoQualitySettings.HighQuality, CancellationToken.None);
            created.Add(producer);
            Assert.Equal(VideoQualitySettings.HighQuality, PipelineSettings(producer));
            Assert.Equal(new DisplayId(9), PipelineDisplayId(producer));
        }
        finally { await DrainAsync(created); }
    }

    [Fact(Timeout = 30_000)]
    public async Task Fixed_display_target_with_invalid_quality_is_rejected_before_any_dependency()
    {
        Rig rig = new([Primary, Secondary]);
        FramePipelineVideoProducerFactory factory = rig.Factory();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => factory.CreateForDisplayWithSessionQualityAsync(Guid.NewGuid(), Secondary,
                new VideoQualitySettings(20, 0.75, 60, true), CancellationToken.None).AsTask());
        Assert.Equal(0, rig.CaptureCreates);
        Assert.Equal(0, rig.EncoderCreates);
        Assert.Equal(0, rig.DisplayCalls);
    }

    private static async Task<EncodedFrame?> ReadAsync(IVideoFrameProducer producer)
    {
        using CancellationTokenSource cancel = new();
        Task<EncodedFrame?> read = producer.ReadNextAsync(cancel.Token).AsTask();
        Task done = await Task.WhenAny(read, Task.Delay(Guard)).ConfigureAwait(false);
        Assert.Same(read, done);
        return await read.ConfigureAwait(false);
    }

    // 反射只用于读取管线当前持有的画质/目标；所有验证仍经公开工厂与 IVideoFrameProducer 发起。
    private static VideoQualitySettings PipelineSettings(IVideoFrameProducer producer)
    {
        object pipeline = FramePipeline(producer);
        return (VideoQualitySettings)RequiredField(pipeline.GetType(), "_settings").GetValue(pipeline)!;
    }

    private static DisplayId PipelineDisplayId(IVideoFrameProducer producer)
    {
        object pipeline = FramePipeline(producer);
        return (DisplayId)RequiredField(pipeline.GetType(), "_displayId").GetValue(pipeline)!;
    }

    private static object FramePipeline(IVideoFrameProducer producer) =>
        RequiredField(producer.GetType(), "_pipeline").GetValue(producer)!;

    private static FieldInfo RequiredField(Type owner, string name) =>
        owner.GetField(name, Hidden) ?? throw new InvalidOperationException("缺少字段: " + name);

    private static async Task DrainAsync(List<IVideoFrameProducer> producers)
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

    private sealed class Rig(DisplayInfo[]? displays = null)
    {
        private readonly DisplayInfo[] _displays = displays ?? [Primary];
        internal List<VideoQualitySettings> EncodedQualities { get; } = [];
        internal int CaptureCreates;
        internal int EncoderCreates;
        internal int DisplayCalls;
        internal int CaptureCalls;

        internal FramePipelineVideoProducerFactory Factory(VideoQualitySettings? settings = null) =>
            new(() =>
                {
                    Interlocked.Increment(ref CaptureCreates);
                    return new FakeCapture(this, _displays);
                },
                () =>
                {
                    Interlocked.Increment(ref EncoderCreates);
                    return new FakeEncoder(this);
                }, settings: settings);

        internal void NoteDisplay() => Interlocked.Increment(ref DisplayCalls);
        internal void NoteCapture() => Interlocked.Increment(ref CaptureCalls);

        internal void NoteQuality(VideoQualitySettings settings)
        {
            lock (EncodedQualities) EncodedQualities.Add(settings);
        }
    }

    private sealed class FakeCapture(Rig rig, DisplayInfo[] displays) : IScreenCaptureBackend, IDisposable
    {
        public IReadOnlyList<DisplayInfo> GetDisplays()
        {
            rig.NoteDisplay();
            return displays;
        }

        public ValueTask<CapturedFrame> CaptureAsync(DisplayId displayId, CancellationToken cancellationToken)
        {
            rig.NoteCapture();
            IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(2 * 2 * 4);
            return ValueTask.FromResult(new CapturedFrame(displayId, 2, 2, 2 * 4,
                FramePixelFormat.Bgra32, owner, 1L));
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeEncoder(Rig rig) : IFrameEncoder, IDisposable
    {
        public ValueTask<EncodedFrame> EncodeAsync(CapturedFrame frame, VideoQualitySettings settings,
            CancellationToken cancellationToken)
        {
            rig.NoteQuality(settings);
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
