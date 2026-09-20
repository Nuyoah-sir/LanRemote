using System.Buffers;
using LanRemote.Core.Models;
using Xunit;

namespace LanRemote.Core.Tests;

/// <summary>
/// 帧模型的长度/范围校验测试。
/// </summary>
/// <remarks>
/// 08_TEST_PLAN.md 要求所有 parser 与 payload 必须有非法长度测试；
/// 这里覆盖在**构造入口**而非解析入口，确保上层拿到的帧一定是合法的。
/// </remarks>
public sealed class FrameModelTests
{
    private static IMemoryOwner<byte> Rent(int size) => MemoryPool<byte>.Shared.Rent(size);

    [Fact]
    public void CapturedFrame_AcceptsValidBgraFrame()
    {
        using IMemoryOwner<byte> owner = Rent(1920 * 4 * 1080);

        using CapturedFrame frame = new(
            new DisplayId(1),
            width: 1920,
            height: 1080,
            stride: 1920 * 4,
            FramePixelFormat.Bgra32,
            owner,
            timestampUs: 1234);

        Assert.Equal(1920, frame.Width);
        Assert.Equal(1080, frame.Height);
        Assert.Equal((long)1920 * 4 * 1080, frame.EstimatedBytes);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-1, 100)]
    [InlineData(8193, 100)]
    public void CapturedFrame_RejectsOutOfRangeWidth(int width, int height)
    {
        using IMemoryOwner<byte> owner = Rent(1024);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CapturedFrame(new DisplayId(0), width, height, 1024, FramePixelFormat.Bgra32, owner, 0));
    }

    [Theory]
    [InlineData(100, 0)]
    [InlineData(100, -1)]
    [InlineData(100, 8193)]
    public void CapturedFrame_RejectsOutOfRangeHeight(int width, int height)
    {
        using IMemoryOwner<byte> owner = Rent(1024);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CapturedFrame(new DisplayId(0), width, height, 1024, FramePixelFormat.Bgra32, owner, 0));
    }

    [Fact]
    public void CapturedFrame_RejectsStrideSmallerThanRowBytes()
    {
        using IMemoryOwner<byte> owner = Rent(1024);

        // 100 像素宽的 BGRA 行需要 400 字节，399 不够。
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CapturedFrame(new DisplayId(0), 100, 2, 399, FramePixelFormat.Bgra32, owner, 0));
    }

    [Fact]
    public void CapturedFrame_RejectsNullOwner()
    {
        Assert.Throws<ArgumentNullException>(
            () => new CapturedFrame(new DisplayId(0), 10, 10, 40, FramePixelFormat.Bgra32, null!, 0));
    }

    [Fact]
    public void CapturedFrame_AfterDispose_AccessingPixelsThrows()
    {
        using IMemoryOwner<byte> owner = Rent(64);
        CapturedFrame frame = new(new DisplayId(0), 4, 4, 16, FramePixelFormat.Bgra32, owner, 0);

        frame.Dispose();

        Assert.Throws<ObjectDisposedException>(() => frame.Pixels);
    }

    [Fact]
    public void CapturedFrame_DisposeIsIdempotent()
    {
        using IMemoryOwner<byte> owner = Rent(64);
        CapturedFrame frame = new(new DisplayId(0), 4, 4, 16, FramePixelFormat.Bgra32, owner, 0);

        frame.Dispose();
        frame.Dispose();

        Assert.Throws<ObjectDisposedException>(() => frame.Pixels);
    }

    [Fact]
    public void EncodedFrame_RejectsPayloadLargerThan32MiB()
    {
        using IMemoryOwner<byte> owner = Rent(FrameLimits.MaxPayloadBytes + 1);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new EncodedFrame(VideoCodec.Jpeg, 100, 100, 1, 0, 60, owner));
    }

    [Fact]
    public void EncodedFrame_AcceptsPayloadWithinLimit()
    {
        using IMemoryOwner<byte> owner = Rent(2048);

        using EncodedFrame frame = new(VideoCodec.Jpeg, 100, 100, 42, 999, 60, owner);

        Assert.Equal(VideoCodec.Jpeg, frame.Codec);
        Assert.Equal(42ul, frame.FrameId);
        Assert.Equal(999, frame.TimestampUs);
        Assert.True(frame.PayloadLength > 0);
    }

    [Fact]
    public void EncodedFrame_AfterDispose_AccessingPayloadThrows()
    {
        using IMemoryOwner<byte> owner = Rent(256);
        EncodedFrame frame = new(VideoCodec.Jpeg, 8, 8, 1, 0, 60, owner);

        frame.Dispose();

        Assert.Throws<ObjectDisposedException>(() => frame.Payload);
        Assert.Equal(0, frame.PayloadLength);
    }
}
