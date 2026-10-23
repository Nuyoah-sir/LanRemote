// 只验证实际帧率口径：计数与帧率来自真实编码并进入编码队列的帧，用 Stopwatch 测量；
// 不建立真实网络、GDI 或 TLS，不代表显示帧率、目标帧率或画质调控已接线。

using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using Xunit;

namespace LanRemote.Sessions.Tests;

public sealed class FramePipelineActualFpsTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(20);

    private static DisplayInfo Primary => new(new DisplayId(0), "主屏", 0, 0, 1920, 1080, true);

    [Fact(Timeout = 30_000)]
    public async Task Not_started_pipeline_reports_zero_frames_and_zero_rate()
    {
        FramePipelineVideoProducerFactory factory = NewFactory(4);
        FramePipelineVideoProducer producer = await CreateAsync(factory);
        try
        {
            Assert.Equal(0, producer.EncodedFrames);
            Assert.Equal(0d, producer.ActualFps);
            // 未启动不因任何读取而虚报帧率或清空历史。
            Assert.Equal(0d, producer.ActualFps);
        }
        finally { await StopAsync(producer); }
    }

    [Fact(Timeout = 30_000)]
    public async Task Started_without_encoded_frame_still_reports_zero_rate()
    {
        FramePipelineVideoProducerFactory factory = NewFactory(0);
        FramePipelineVideoProducer producer = await CreateAsync(factory);
        try
        {
            producer.Start();
            await Task.Delay(100);
            Assert.Equal(0, producer.EncodedFrames);
            Assert.Equal(0d, producer.ActualFps);
        }
        finally { await StopAsync(producer); }
    }

    [Fact(Timeout = 30_000)]
    public async Task Actual_rate_is_positive_and_survives_repeated_reads()
    {
        FramePipelineVideoProducerFactory factory = NewFactory(4);
        FramePipelineVideoProducer producer = await CreateAsync(factory);
        try
        {
            producer.Start();
            await WaitFramesAsync(producer, 4);
            Assert.Equal(4, producer.EncodedFrames);

            double first = producer.ActualFps;
            Assert.True(first > 0d, "真实编码帧后实际帧率必须大于 0。");
            double second = producer.ActualFps;
            // 读取不得清空历史计数导致帧率归零。
            Assert.True(second > 0d, "读取实际帧率不得清空历史计数。");
            Assert.Equal(4, producer.EncodedFrames);
        }
        finally { await StopAsync(producer); }
    }

    [Fact(Timeout = 30_000)]
    public async Task Counters_are_per_pipeline_and_not_shared_across_sessions()
    {
        FramePipelineVideoProducerFactory factory = NewFactory(4);
        FramePipelineVideoProducer first = await CreateAsync(factory);
        FramePipelineVideoProducer second = await CreateAsync(factory);
        try
        {
            first.Start();
            await WaitFramesAsync(first, 4);

            Assert.Equal(4, first.EncodedFrames);
            Assert.True(first.ActualFps > 0d, "已编码帧的管线实际帧率必须大于 0。");

            // 同一工厂的另一条未启动管线不得共享计数或帧率，也不得被前者清空。
            Assert.Equal(0, second.EncodedFrames);
            Assert.Equal(0d, second.ActualFps);
        }
        finally
        {
            await StopAsync(first);
            await StopAsync(second);
        }
    }

    private static async Task<FramePipelineVideoProducer> CreateAsync(FramePipelineVideoProducerFactory factory) =>
        Assert.IsType<FramePipelineVideoProducer>(await factory.CreateAsync(Guid.NewGuid(), CancellationToken.None));

    // 有界等待本身不做断言：计数口径由用例内的 Assert.Equal 判定，避免变异被等待辅助而非被测断言杀死。
    private static async Task WaitFramesAsync(FramePipelineVideoProducer producer, long count)
    {
        long started = Environment.TickCount64;
        while (producer.EncodedFrames < count && Environment.TickCount64 - started < 10_000)
        {
            await Task.Delay(Poll);
        }
    }

    private static async Task StopAsync(FramePipelineVideoProducer producer)
    {
        try
        {
            Task stop = producer.StopAsync();
            await Task.WhenAny(stop, Task.Delay(Watchdog));
            await producer.DisposeAsync();
            await Task.WhenAny(producer.Completion, Task.Delay(Watchdog));
        }
        catch (Exception)
        {
            // 清场只负责不泄漏 fixture 资源；断言失败已在用例内先发生。
        }
    }

    private static FramePipelineVideoProducerFactory NewFactory(int frames) =>
        new(() => new LimitedCapture(frames), () => new StubEncoder(),
            settings: new VideoQualitySettings(30, 0.5, 60, false));

    private sealed class LimitedCapture(int frames) : IScreenCaptureBackend, IDisposable
    {
        private int _calls;

        public IReadOnlyList<DisplayInfo> GetDisplays() => [Primary];

        public ValueTask<CapturedFrame> CaptureAsync(DisplayId displayId, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) > frames)
            {
                // 超出限定帧数后挂起，直到停止取消，避免计数继续增长。
                Task<CapturedFrame> hold = new TaskCompletionSource<CapturedFrame>(
                    TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(cancellationToken);
                return new ValueTask<CapturedFrame>(hold);
            }

            IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(2 * 2 * 4);
            return ValueTask.FromResult(new CapturedFrame(displayId, 2, 2, 2 * 4, FramePixelFormat.Bgra32, owner, 1L));
        }

        public void Dispose()
        {
        }
    }

    private sealed class StubEncoder : IFrameEncoder, IDisposable
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
