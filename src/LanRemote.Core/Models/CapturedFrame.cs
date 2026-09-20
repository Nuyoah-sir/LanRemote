using System.Buffers;

namespace LanRemote.Core.Models;

/// <summary>
/// 原始帧像素格式。
/// </summary>
public enum FramePixelFormat
{
    /// <summary>每像素 4 字节 BGRA，GDI DIBSection 的常见形态。</summary>
    Bgra32 = 0,

    /// <summary>每像素 3 字节 BGR。</summary>
    Bgr24 = 1,
}

/// <summary>
/// 一次屏幕采集得到的原始帧。
/// </summary>
/// <remarks>
/// <para>像素内存来自 <see cref="MemoryPool{T}"/>，通过 <see cref="IMemoryOwner{T}"/> 持有所有权；
/// 类型实现 <see cref="IDisposable"/>，必须归还 buffer，否则 <c>03_ARCHITECTURE.md</c> 第 6 节的
/// 「不允许每帧泄漏」要求会被破坏。</para>
/// <para>它可以在管线中被移动但不能长期驻留：视频管线容量只有 1~2 帧，超出者按 DropOldest 丢弃。</para>
/// </remarks>
public sealed class CapturedFrame : IDisposable
{
    private IMemoryOwner<byte>? _owner;
    private bool _disposed;

    /// <summary>构造一帧。</summary>
    /// <param name="displayId">来源显示器。</param>
    /// <param name="width">图像宽度，必须大于 0 且不超过 8192。</param>
    /// <param name="height">图像高度，必须大于 0 且不超过 8192。</param>
    /// <param name="stride">单行字节数，必须不小于 width 乘以每像素字节数。</param>
    /// <param name="pixelFormat">像素格式。</param>
    /// <param name="owner">像素内存所有者，所有权转移给本实例。</param>
    /// <param name="timestampUs">捕获时刻（微秒，单调时钟）。</param>
    /// <exception cref="ArgumentException">尺寸或 stride 非法。</exception>
    public CapturedFrame(
        DisplayId displayId,
        int width,
        int height,
        int stride,
        FramePixelFormat pixelFormat,
        IMemoryOwner<byte> owner,
        long timestampUs)
    {
        if (width <= 0 || width > FrameLimits.MaxDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "width 必须位于 1..8192。");
        }

        if (height <= 0 || height > FrameLimits.MaxDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "height 必须位于 1..8192。");
        }

        int bytesPerPixel = pixelFormat == FramePixelFormat.Bgra32 ? 4 : 3;
        if (stride < width * bytesPerPixel)
        {
            throw new ArgumentOutOfRangeException(nameof(stride), stride, "stride 小于一行所需最小字节数。");
        }

        ArgumentNullException.ThrowIfNull(owner);

        DisplayId = displayId;
        Width = width;
        Height = height;
        Stride = stride;
        PixelFormat = pixelFormat;
        TimestampUs = timestampUs;
        _owner = owner;
    }

    /// <summary>来源显示器。</summary>
    public DisplayId DisplayId { get; }

    /// <summary>图像宽度。</summary>
    public int Width { get; }

    /// <summary>图像高度。</summary>
    public int Height { get; }

    /// <summary>单行字节数。</summary>
    public int Stride { get; }

    /// <summary>像素格式。</summary>
    public FramePixelFormat PixelFormat { get; }

    /// <summary>捕获时刻（微秒）。</summary>
    public long TimestampUs { get; }

    /// <summary>像素只读内存视图。</summary>
    /// <exception cref="ObjectDisposedException">帧已释放。</exception>
    public ReadOnlyMemory<byte> Pixels
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _owner!.Memory;
        }
    }

    /// <summary>像素大小估算（字节）。</summary>
    public long EstimatedBytes => (long)Stride * Height;

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

/// <summary>
/// 协议层共享的尺寸与负载上限（对齐 <c>04_PROTOCOL_AND_SECURITY.md</c> 第 13 节）。
/// </summary>
public static class FrameLimits
{
    /// <summary>宽/高上限。</summary>
    public const int MaxDimension = 8192;

    /// <summary>单帧 payload 上限，32 MiB。</summary>
    public const int MaxPayloadBytes = 32 * 1024 * 1024;
}
