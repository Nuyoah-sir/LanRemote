using System.Buffers.Binary;
using LanRemote.Core.Models;

namespace LanRemote.Transport.Tests;

public sealed class VideoFrameHeaderTests
{
    [Fact]
    public void Reads_Independent_Manual_Golden_Vector()
    {
        byte[] bytes = VideoFrameTestData.Header();
        Assert.Equal(40, bytes.Length);
        Assert.Equal(40, VideoFrameHeader.Size);
        VideoFrameHeader header = VideoFrameHeader.Read(bytes);
        Assert.Equal(VideoCodec.Jpeg, header.Codec);
        Assert.Equal(0x0123456789ABCDEFul, header.FrameId);
        Assert.Equal(0x1020304050607080L, header.TimestampUs);
        Assert.Equal(1920, header.Width);
        Assert.Equal(1080, header.Height);
        Assert.Equal(60, header.JpegQuality);
        Assert.Equal(5, header.PayloadLength);
    }

    [Fact]
    public void Writes_Independent_Manual_Golden_Vector_And_Clears_Reserved_Bytes()
    {
        byte[] destination = Enumerable.Repeat((byte)0xCC, 40).ToArray();
        new VideoFrameHeader(VideoCodec.Jpeg, 0x0123456789ABCDEFul, 0x1020304050607080L,
            1920, 1080, 60, 5).Write(destination);
        Assert.Equal(VideoFrameTestData.Header(), destination);
    }

    public static IEnumerable<object[]> InvalidHeaders()
    {
        foreach (int offset in new[] { 0, 1, 2, 3 })
        {
            yield return ChangedByte(offset, 0, "video-magic");
        }

        foreach (byte value in new byte[] { 0, 2, 255 })
        {
            yield return ChangedByte(4, value, "video-version");
            yield return ChangedByte(5, value, "video-codec");
        }

        foreach (int offset in new[] { 6, 7 })
        {
            yield return ChangedByte(offset, 1, "video-flags");
            yield return ChangedByte(offset, 128, "video-flags");
        }

        foreach (int offset in new[] { 33, 34, 35 })
        {
            yield return ChangedByte(offset, 1, "video-reserved");
            yield return ChangedByte(offset, 255, "video-reserved");
        }

        foreach (byte quality in new byte[] { 0, 39, 86, 100, 255 })
        {
            yield return ChangedByte(32, quality, "video-quality");
        }

        foreach (uint value in new[] { 0u, 8193u, 0x7FFFFFFFu, 0x80000000u, uint.MaxValue })
        {
            yield return ChangedUInt32(24, value, "video-width");
            yield return ChangedUInt32(28, value, "video-height");
        }

        yield return ChangedUInt32(36, 0, FrameReader.RejectZeroLength);
        foreach (uint value in new[] { 33554433u, 0x7FFFFFFFu, 0x80000000u, uint.MaxValue })
        {
            yield return ChangedUInt32(36, value, FrameReader.RejectTooLarge);
        }

        foreach (ulong value in new[] { 0x8000000000000000ul, ulong.MaxValue })
        {
            byte[] bytes = VideoFrameTestData.Header();
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(16, 8), value);
            yield return [bytes, "video-timestamp"];
        }
    }

    private static object[] ChangedByte(int offset, byte value, string reason)
    {
        byte[] bytes = VideoFrameTestData.Header();
        bytes[offset] = value;
        return [bytes, reason];
    }

    private static object[] ChangedUInt32(int offset, uint value, string reason)
    {
        byte[] bytes = VideoFrameTestData.Header();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, 4), value);
        return [bytes, reason];
    }

    [Theory]
    [MemberData(nameof(InvalidHeaders))]
    public void Rejects_Each_Invalid_Field(byte[] bytes, string reason)
    {
        FrameProtocolException error = Assert.Throws<FrameProtocolException>(() => VideoFrameHeader.Read(bytes));
        Assert.Equal(reason, error.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(39)]
    [InlineData(41)]
    public void Rejects_Non_Exact_Header_Size(int size)
    {
        FrameProtocolException error = Assert.Throws<FrameProtocolException>(() => VideoFrameHeader.Read(new byte[size]));
        Assert.Equal("video-header-size", error.Reason);
        VideoFrameHeader valid = new(VideoCodec.Jpeg, 0, 0, 1, 1, 40, 1);
        Assert.Throws<ArgumentException>(() => valid.Write(new byte[size]));
    }

    [Theory]
    [InlineData(1u, 8192u, 40, 1u, 0ul, 0L)]
    [InlineData(8192u, 1u, 85, 33554432u, ulong.MaxValue, long.MaxValue)]
    [InlineData(8192u, 8192u, 60, 33554431u, 0x8000000000000000ul, 1L)]
    public void Accepts_Boundaries_Without_Signed_FrameId_Restriction(
        uint width, uint height, byte quality, uint length, ulong frameId, long timestamp)
    {
        byte[] bytes = VideoFrameTestData.Header();
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8, 8), frameId);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(16, 8), (ulong)timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(24, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(28, 4), height);
        bytes[32] = quality;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(36, 4), length);

        VideoFrameHeader expected = new(VideoCodec.Jpeg, frameId, timestamp, (int)width, (int)height, quality, (int)length);
        Assert.Equal(expected, VideoFrameHeader.Read(bytes));
        byte[] written = new byte[40];
        expected.Write(written);
        Assert.Equal(bytes, written);
    }

    [Theory]
    [InlineData(0, 0L, 1, 1, 40, 1, "video-codec")]
    [InlineData(2, 0L, 1, 1, 40, 1, "video-codec")]
    [InlineData(1, -1L, 1, 1, 40, 1, "video-timestamp")]
    [InlineData(1, long.MinValue, 1, 1, 40, 1, "video-timestamp")]
    [InlineData(1, 0L, -1, 1, 40, 1, "video-width")]
    [InlineData(1, 0L, 0, 1, 40, 1, "video-width")]
    [InlineData(1, 0L, 8193, 1, 40, 1, "video-width")]
    [InlineData(1, 0L, 1, -1, 40, 1, "video-height")]
    [InlineData(1, 0L, 1, 0, 40, 1, "video-height")]
    [InlineData(1, 0L, 1, 8193, 40, 1, "video-height")]
    [InlineData(1, 0L, 1, 1, 39, 1, "video-quality")]
    [InlineData(1, 0L, 1, 1, 86, 1, "video-quality")]
    [InlineData(1, 0L, 1, 1, 40, -1, FrameReader.RejectZeroLength)]
    [InlineData(1, 0L, 1, 1, 40, 0, FrameReader.RejectZeroLength)]
    [InlineData(1, 0L, 1, 1, 40, 33554433, FrameReader.RejectTooLarge)]
    public void Invalid_Model_Does_Not_Modify_Destination(
        byte codec, long timestamp, int width, int height, byte quality, int length, string reason)
    {
        byte[] destination = Enumerable.Repeat((byte)0xCC, 40).ToArray();
        VideoFrameHeader header = new((VideoCodec)codec, 0, timestamp, width, height, quality, length);
        FrameProtocolException error = Assert.Throws<FrameProtocolException>(() => header.Write(destination));
        Assert.Equal(reason, error.Reason);
        Assert.All(destination, b => Assert.Equal(0xCC, b));
    }

    [Fact]
    public void Stream_Codecs_And_Rent_Seam_Are_Not_Public_Entrypoints()
    {
        Assert.False(typeof(VideoFrameHeader).IsPublic);
        Assert.False(typeof(VideoFrameReader).IsPublic);
        Assert.False(typeof(VideoFrameWriter).IsPublic);
        Assert.Empty(typeof(VideoFrameReader).GetConstructors());
        Assert.Empty(typeof(VideoFrameWriter).GetConstructors());
    }
}
