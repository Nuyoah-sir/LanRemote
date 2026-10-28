using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using LanRemote.Sessions;
using Xunit;

namespace LanRemote.Acceptance.Tests;

/// <summary>工厂把本次实际选中的显示器随生产者交付；后续自动画质换代必须沿用当前显示器，不能悄悄回到主屏。</summary>
public sealed class VideoProducerDisplayTargetTests
{
    [Fact(Timeout = 30_000)]
    public async Task Explicit_target_and_primary_default_reach_the_producer()
    {
        DisplayInfo primary = new(new DisplayId(1), "Primary", 0, 0, 1920, 1080, true);
        DisplayInfo second = new(new DisplayId(7), "Second", 1920, 0, 1280, 1024, false);
        Dual dependency = new([second, primary]);
        FramePipelineVideoProducerFactory factory = new(() => dependency, () => dependency);

        FramePipelineVideoProducer explicitTarget = await Create(factory, second);
        try
        {
            Assert.Equal(second, explicitTarget.Display);
            Assert.Equal(0, explicitTarget.EncodedFrames);
            Assert.Equal(0d, explicitTarget.ActualFps);
        }
        finally
        {
            await explicitTarget.DisposeAsync();
        }

        FramePipelineVideoProducer primaryDefault = await Create(factory, null);
        try
        {
            Assert.Equal(primary, primaryDefault.Display);
        }
        finally
        {
            await primaryDefault.DisposeAsync();
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Unknown_target_is_rejected_before_any_dependency_is_created()
    {
        Dual dependency = new([new DisplayInfo(new DisplayId(1), "Primary", 0, 0, 1024, 768, true)]);
        int created = 0;
        FramePipelineVideoProducerFactory factory = new(
            () => { Interlocked.Increment(ref created); return dependency; },
            () => dependency);
        await Assert.ThrowsAsync<InvalidOperationException>(() => factory
            .CreateForDisplayAsync(Guid.NewGuid(), new DisplayInfo(new DisplayId(9), "Missing", 0, 0, 800, 600, false),
                CancellationToken.None)
            .AsTask());
        // 目标元数据不符时仍先枚举一次显示器；未找到目标绝不创建编码器或采集像素。
        Assert.Equal(1, created);
    }

    private static async Task<FramePipelineVideoProducer> Create(
        FramePipelineVideoProducerFactory factory, DisplayInfo? expected)
    {
        IVideoFrameProducer producer = expected is null
            ? await factory.CreateAsync(Guid.NewGuid(), CancellationToken.None)
            : await factory.CreateForDisplayAsync(Guid.NewGuid(), expected, CancellationToken.None);
        FramePipelineVideoProducer typed = Assert.IsType<FramePipelineVideoProducer>(producer);
        Assert.Same(typed.Completion, typed.Completion);
        return typed;
    }

    private sealed class Dual(IReadOnlyList<DisplayInfo> displays) : IScreenCaptureBackend, IFrameEncoder
    {
        public IReadOnlyList<DisplayInfo> GetDisplays() => displays;
        public ValueTask<CapturedFrame> CaptureAsync(DisplayId displayId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("本用例不启动生产，不允许采集。");
        public ValueTask<EncodedFrame> EncodeAsync(CapturedFrame frame, VideoQualitySettings settings,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("本用例不启动生产，不允许编码。");
    }
}
