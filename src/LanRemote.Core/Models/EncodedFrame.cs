using System.Buffers;

namespace LanRemote.Core.Models;

/// <summary>
/// 视频编解码器标识，取值与 <c>04_PROTOCOL_AND_SECURITY.md</c> 第 13 节二进制头的 <c>codec</c> 字段一致。
/// </summary>
public enum VideoCodec : byte
{
    /// <summary>JPEG / MJPEG，v1 唯一实现。</summary>
    Jpeg = 1,
}

/// <summary>
/// 编码后的视频帧。
/// </summary>
/// <remarks>
/// 与 <see cref="CapturedFrame"/> 一样持有 <see cref="IMemoryOwner{T}"/> 所有权，必须释放。
/// 编码后的队列同样是有界队列（容量 1~2，DropOldest），不得无限堆积。
/// </remarks>
public sealed class EncodedFrame : IDisposable
{
    private IMemoryOwner<byte>? _owner;
    private readonly int _payloadLength;
    private bool _disposed;

    /// <summary>构造一个已编码帧，owner 的整块内存均为有效 payload。</summary>
    /// <param name="codec">编解码器。</param>
    /// <param name="width">编码后宽度。</param>
    /// <param name="height">编码后高度。</param>
    /// <param name="frameId">单调递增帧序号。</param>
    /// <param name="timestampUs">采集时刻（微秒）。</param>
    /// <param name="jpegQuality">实际使用的 JPEG 质量。</param>
    /// <param name="owner">payload 内存所有者，仅成功构造时转移所有权给本实例；构造失败时由调用方负责释放。</param>
    /// <exception cref="ArgumentException">尺寸或 payload 非法。</exception>
    public EncodedFrame(
        VideoCodec codec,
        int width,
        int height,
        ulong frameId,
        long timestampUs,
        byte jpegQuality,
        IMemoryOwner<byte> owner)
        : this(codec, width, height, frameId, timestampUs, jpegQuality, owner, payloadLength: null)
    {
    }

    /// <summary>构造一个已编码帧，仅使用 owner 内存开头的有效 payload。</summary>
    /// <param name="codec">编解码器。</param>
    /// <param name="width">编码后宽度。</param>
    /// <param name="height">编码后高度。</param>
    /// <param name="frameId">单调递增帧序号。</param>
    /// <param name="timestampUs">采集时刻（微秒）。</param>
    /// <param name="jpegQuality">实际使用的 JPEG 质量。</param>
    /// <param name="owner">payload 内存所有者，仅成功构造时转移所有权给本实例；构造失败时由调用方负责释放。</param>
    /// <param name="payloadLength">有效 payload 字节数，必须大于 0、不超过 32 MiB，且不超过 owner 的内存长度。</param>
    /// <exception cref="ArgumentException">尺寸或 payload 非法。</exception>
    public EncodedFrame(
        VideoCodec codec,
        int width,
        int height,
        ulong frameId,
        long timestampUs,
        byte jpegQuality,
        IMemoryOwner<byte> owner,
        int payloadLength)
        : this(codec, width, height, frameId, timestampUs, jpegQuality, owner, (int?)payloadLength)
    {
    }

    private EncodedFrame(
        VideoCodec codec,
        int width,
        int height,
        ulong frameId,
        long timestampUs,
        byte jpegQuality,
        IMemoryOwner<byte> owner,
        int? payloadLength)
    {
        if (width <= 0 || width > FrameLimits.MaxDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "width 必须位于 1..8192。");
        }

        if (height <= 0 || height > FrameLimits.MaxDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "height 必须位于 1..8192。");
        }

        ArgumentNullException.ThrowIfNull(owner);

        int capacity = owner.Memory.Length;
        int length = payloadLength ?? capacity;
        if (length <= 0 || length > FrameLimits.MaxPayloadBytes || length > capacity)
        {
            throw new ArgumentOutOfRangeException(
                payloadLength.HasValue ? nameof(payloadLength) : nameof(owner),
                length,
                $"payload 长度必须位于 1..{FrameLimits.MaxPayloadBytes}，且不得超过 owner 的内存长度。");
        }

        Codec = codec;
        Width = width;
        Height = height;
        FrameId = frameId;
        TimestampUs = timestampUs;
        JpegQuality = jpegQuality;
        _payloadLength = length;
        _owner = owner;
    }

    /// <summary>编解码器。</summary>
    public VideoCodec Codec { get; }

    /// <summary>编码后宽度。</summary>
    public int Width { get; }

    /// <summary>编码后高度。</summary>
    public int Height { get; }

    /// <summary>单调递增帧序号。</summary>
    public ulong FrameId { get; }

    /// <summary>采集时刻（微秒）。</summary>
    public long TimestampUs { get; }

    /// <summary>实际使用的 JPEG 质量。</summary>
    public byte JpegQuality { get; }

    /// <summary>JPEG 有效 payload 的只读视图，不包含底层内存的多余尾部。</summary>
    /// <exception cref="ObjectDisposedException">帧已释放。</exception>
    public ReadOnlyMemory<byte> Payload
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _owner!.Memory[.._payloadLength];
        }
    }

    /// <summary>有效 payload 字节数，释放后为 0。</summary>
    public int PayloadLength => _owner is null ? 0 : _payloadLength;

    /// <summary>归还底层 buffer。可重复调用。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Exchange(ref _owner, null)?.Dispose();
    }
}
