using System.Buffers.Binary;
using LanRemote.Core.Models;

namespace LanRemote.Transport;

/// <summary>LRVF v1 的固定 40 字节视频头，不包含 Control 长度前缀。</summary>
/// <remarks>仅做 wire/model 编解码；不提供认证、授权或 JPEG 解码校验。</remarks>
internal readonly record struct VideoFrameHeader(
    VideoCodec Codec,
    ulong FrameId,
    long TimestampUs,
    int Width,
    int Height,
    byte JpegQuality,
    int PayloadLength)
{
    internal const int Size = 40;

    internal static VideoFrameHeader Read(ReadOnlySpan<byte> source)
    {
        if (source.Length != Size)
        {
            throw new FrameProtocolException("video-header-size");
        }

        if (!source[..4].SequenceEqual("LRVF"u8))
        {
            throw new FrameProtocolException("video-magic");
        }

        if (source[4] != 1)
        {
            throw new FrameProtocolException("video-version");
        }

        if (BinaryPrimitives.ReadUInt16BigEndian(source[6..8]) != 0)
        {
            throw new FrameProtocolException("video-flags");
        }

        if (source[33] != 0 || source[34] != 0 || source[35] != 0)
        {
            throw new FrameProtocolException("video-reserved");
        }

        ulong timestamp = BinaryPrimitives.ReadUInt64BigEndian(source[16..24]);
        uint width = BinaryPrimitives.ReadUInt32BigEndian(source[24..28]);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(source[28..32]);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(source[36..40]);
        Validate(source[5], timestamp, width, height, source[32], length);

        // wire 的无符号范围全部验证后才转换；frameId 不施加排序或去重规则。
        return new VideoFrameHeader(
            (VideoCodec)source[5], BinaryPrimitives.ReadUInt64BigEndian(source[8..16]),
            (long)timestamp, (int)width, (int)height, source[32], (int)length);
    }

    internal static VideoFrameHeader FromFrame(EncodedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        VideoFrameHeader header = new(
            frame.Codec, frame.FrameId, frame.TimestampUs, frame.Width, frame.Height,
            frame.JpegQuality, frame.PayloadLength);
        header.ValidateModel();
        return header;
    }

    internal void Write(Span<byte> destination)
    {
        ValidateModel();
        if (destination.Length != Size)
        {
            throw new ArgumentException("视频头目标必须恰好为 40 字节。", nameof(destination));
        }

        destination.Clear();
        "LRVF"u8.CopyTo(destination);
        destination[4] = 1;
        destination[5] = (byte)Codec;
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..16], FrameId);
        BinaryPrimitives.WriteUInt64BigEndian(destination[16..24], (ulong)TimestampUs);
        BinaryPrimitives.WriteUInt32BigEndian(destination[24..28], (uint)Width);
        BinaryPrimitives.WriteUInt32BigEndian(destination[28..32], (uint)Height);
        destination[32] = JpegQuality;
        BinaryPrimitives.WriteUInt32BigEndian(destination[36..40], (uint)PayloadLength);
    }

    private void ValidateModel()
    {
        if (TimestampUs < 0)
        {
            throw new FrameProtocolException("video-timestamp");
        }

        if (Width <= 0 || Height <= 0)
        {
            throw new FrameProtocolException(Width <= 0 ? "video-width" : "video-height");
        }

        if (PayloadLength <= 0)
        {
            throw new FrameProtocolException(FrameReader.RejectZeroLength);
        }

        Validate((byte)Codec, (ulong)TimestampUs, (uint)Width, (uint)Height, JpegQuality, (uint)PayloadLength);
    }

    private static void Validate(byte codec, ulong timestamp, uint width, uint height, byte quality, uint length)
    {
        if (codec != (byte)VideoCodec.Jpeg)
        {
            throw new FrameProtocolException("video-codec");
        }

        if (timestamp > long.MaxValue)
        {
            throw new FrameProtocolException("video-timestamp");
        }

        if (width == 0 || width > FrameLimits.MaxDimension)
        {
            throw new FrameProtocolException("video-width");
        }

        if (height == 0 || height > FrameLimits.MaxDimension)
        {
            throw new FrameProtocolException("video-height");
        }

        if (quality < VideoQualitySettings.MinJpegQuality || quality > VideoQualitySettings.MaxJpegQuality)
        {
            throw new FrameProtocolException("video-quality");
        }

        if (!FrameReader.TryValidateLength(length, FrameLimits.MaxPayloadBytes, out _, out string? rejection))
        {
            throw new FrameProtocolException(rejection!);
        }
    }
}
