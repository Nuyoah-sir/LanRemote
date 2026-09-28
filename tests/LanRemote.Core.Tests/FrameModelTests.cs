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

    [Theory]
    [InlineData(0)]
    [InlineData(FrameLimits.MaxPayloadBytes + 1)]
    public void EncodedFrame_RejectsInvalidLegacyPayloadWithoutDisposingOwner(int capacity)
    {
        CountingMemoryOwner owner = new(capacity);

        using (owner)
        {
            ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
                () => new EncodedFrame(VideoCodec.Jpeg, 100, 100, 1, 0, 60, owner));

            Assert.Equal("owner", exception.ParamName);
            Assert.Equal(0, owner.DisposeCalls);
            Assert.Equal(capacity, owner.Memory.Length);
        }

        Assert.Equal(1, owner.DisposeCalls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2048)]
    [InlineData(FrameLimits.MaxPayloadBytes)]
    public void EncodedFrame_AcceptsPayloadWithinLimit(int capacity)
    {
        CountingMemoryOwner owner = new(capacity);

        using (EncodedFrame frame = new(VideoCodec.Jpeg, 100, 100, 42, 999, 60, owner))
        {
            Assert.Equal(VideoCodec.Jpeg, frame.Codec);
            Assert.Equal(100, frame.Width);
            Assert.Equal(100, frame.Height);
            Assert.Equal(42ul, frame.FrameId);
            Assert.Equal(999, frame.TimestampUs);
            Assert.Equal((byte)60, frame.JpegQuality);
            Assert.Equal(capacity, frame.PayloadLength);
            Assert.Equal((ReadOnlyMemory<byte>)owner.Memory, frame.Payload);
            Assert.Equal(0, owner.DisposeCalls);
        }

        Assert.Equal(1, owner.DisposeCalls);
    }

    [Fact]
    public void EncodedFrame_ExplicitLength_ExposesOnlyValidPayloadWithoutCopying()
    {
        CountingMemoryOwner owner = new(16);
        byte[] expected = [0xff, 0xd8, 0xff, 0xd9];
        owner.Memory.Span.Fill(0xcc);
        expected.AsSpan().CopyTo(owner.Memory.Span);

        using (EncodedFrame frame = new(VideoCodec.Jpeg, 8, 8, 1, 0, 60, owner, payloadLength: 4))
        {
            Assert.Equal(4, frame.PayloadLength);
            Assert.Equal(4, frame.Payload.Length);
            Assert.Equal(expected, frame.Payload.ToArray());
            Assert.True(frame.Payload.Equals((ReadOnlyMemory<byte>)owner.Memory[..4]));

            owner.Memory.Span[4..].Fill(0xaa);

            Assert.Equal(expected, frame.Payload.ToArray());
            Assert.Equal(0, owner.DisposeCalls);
        }

        Assert.Equal(1, owner.DisposeCalls);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(16, 16)]
    [InlineData(FrameLimits.MaxPayloadBytes + 1, 4)]
    [InlineData(FrameLimits.MaxPayloadBytes, FrameLimits.MaxPayloadBytes)]
    [InlineData(FrameLimits.MaxPayloadBytes + 1, FrameLimits.MaxPayloadBytes)]
    public void EncodedFrame_ExplicitLength_AcceptsValidLengthRegardlessOfOwnerCapacity(int capacity, int payloadLength)
    {
        CountingMemoryOwner owner = new(capacity);

        using (EncodedFrame frame = new(VideoCodec.Jpeg, 8, 8, 1, 0, 60, owner, payloadLength))
        {
            Assert.Equal(capacity, owner.Memory.Length);
            Assert.Equal(payloadLength, frame.PayloadLength);
            Assert.Equal(payloadLength, frame.Payload.Length);
            Assert.Equal((ReadOnlyMemory<byte>)owner.Memory[..payloadLength], frame.Payload);
        }

        Assert.Equal(1, owner.DisposeCalls);
    }

    [Theory]
    [InlineData(16, 0)]
    [InlineData(16, -1)]
    [InlineData(0, 1)]
    [InlineData(16, 17)]
    [InlineData(FrameLimits.MaxPayloadBytes + 1, FrameLimits.MaxPayloadBytes + 1)]
    [InlineData(16, int.MaxValue)]
    public void EncodedFrame_ExplicitLength_RejectsInvalidLengthWithoutDisposingOwner(int capacity, int payloadLength)
    {
        CountingMemoryOwner owner = new(capacity);

        using (owner)
        {
            ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
                () => new EncodedFrame(VideoCodec.Jpeg, 8, 8, 1, 0, 60, owner, payloadLength));

            Assert.Equal("payloadLength", exception.ParamName);
            Assert.Equal(payloadLength, exception.ActualValue);
            Assert.Equal(0, owner.DisposeCalls);
            Assert.Equal(capacity, owner.Memory.Length);
        }

        Assert.Equal(1, owner.DisposeCalls);
    }

    [Theory]
    [InlineData(0, 8, false, "width")]
    [InlineData(8, 0, false, "height")]
    [InlineData(FrameLimits.MaxDimension + 1, 8, false, "width")]
    [InlineData(8, FrameLimits.MaxDimension + 1, false, "height")]
    [InlineData(0, 8, true, "width")]
    [InlineData(8, 0, true, "height")]
    [InlineData(FrameLimits.MaxDimension + 1, 8, true, "width")]
    [InlineData(8, FrameLimits.MaxDimension + 1, true, "height")]
    public void EncodedFrame_RejectsInvalidDimensionsWithoutDisposingOwner(
        int width, int height, bool explicitLength, string parameterName)
    {
        CountingMemoryOwner owner = new(16);

        using (owner)
        {
            ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
                explicitLength
                    ? new EncodedFrame(VideoCodec.Jpeg, width, height, 1, 0, 60, owner, payloadLength: 4)
                    : new EncodedFrame(VideoCodec.Jpeg, width, height, 1, 0, 60, owner));

            Assert.Equal(parameterName, exception.ParamName);
            Assert.Equal(0, owner.DisposeCalls);
            Assert.Equal(16, owner.Memory.Length);
        }

        Assert.Equal(1, owner.DisposeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EncodedFrame_RejectsNullOwner(bool explicitLength)
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() =>
            explicitLength
                ? new EncodedFrame(VideoCodec.Jpeg, 8, 8, 1, 0, 60, null!, payloadLength: 4)
                : new EncodedFrame(VideoCodec.Jpeg, 8, 8, 1, 0, 60, null!));

        Assert.Equal("owner", exception.ParamName);
    }

    [Theory]
    [InlineData((byte)0, long.MinValue, (byte)0)]
    [InlineData((byte)255, -1L, (byte)255)]
    [InlineData((byte)1, 999L, (byte)60)]
    public void EncodedFrame_ExplicitLength_PreservesMetadata(byte codec, long timestampUs, byte jpegQuality)
    {
        CountingMemoryOwner owner = new(16);
        using EncodedFrame frame = new((VideoCodec)codec, 8, 8, 42, timestampUs, jpegQuality, owner, payloadLength: 4);

        Assert.Equal((VideoCodec)codec, frame.Codec);
        Assert.Equal(8, frame.Width);
        Assert.Equal(8, frame.Height);
        Assert.Equal(42ul, frame.FrameId);
        Assert.Equal(timestampUs, frame.TimestampUs);
        Assert.Equal(jpegQuality, frame.JpegQuality);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EncodedFrame_AfterDispose_AccessingPayloadThrows(bool explicitLength)
    {
        CountingMemoryOwner owner = new(256);
        using EncodedFrame frame = explicitLength
            ? new(VideoCodec.Jpeg, 8, 8, 1, 0, 60, owner, payloadLength: 4)
            : new(VideoCodec.Jpeg, 8, 8, 1, 0, 60, owner);

        Assert.Equal(explicitLength ? 4 : 256, frame.PayloadLength);

        frame.Dispose();
        frame.Dispose();

        Assert.Equal(1, owner.DisposeCalls);
        Assert.Throws<ObjectDisposedException>(() => frame.Payload);
        Assert.Equal(0, frame.PayloadLength);
    }

    private sealed class CountingMemoryOwner(int capacity) : IMemoryOwner<byte>
    {
        private readonly byte[] _bytes = new byte[capacity];
        public int DisposeCalls { get; private set; }

        public Memory<byte> Memory
        {
            get
            {
                ObjectDisposedException.ThrowIf(DisposeCalls != 0, this);
                return _bytes;
            }
        }

        public void Dispose() => DisposeCalls++;
    }
}
