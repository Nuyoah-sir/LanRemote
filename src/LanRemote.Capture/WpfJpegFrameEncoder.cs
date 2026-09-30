using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;

namespace LanRemote.Capture;

/// <summary>在线程池上缩放并编码 JPEG；单实例最多一个在途编码，不建立等待队列。</summary>
/// <remarks>
/// 借用输入帧，调用方必须等待原 EncodeAsync 完成再释放输入。取消不能硬中断 WIC 的同步 Save；
/// 原操作完成并收回未交付 buffer 后才报告取消。已交付帧由调用方释放。
/// TargetFps 只校验不调度；帧率控制由上层有界管线负责。
/// </remarks>
public sealed class WpfJpegFrameEncoder : IFrameEncoder
{
    private readonly Action<BitmapSource, int, Stream> _save;
    private readonly int _payloadLimit;
    private ulong _nextFrameId;
    private bool _sequenceExhausted;
    private readonly object _encodeGate = new();
    private Task<EncodedFrame>? _operation;
    private Task? _originalWorker;

    // 只读观察最近发布的原 Task.Run 任务；发布可能早于 await 登记，不能单凭此属性证明等待关系。
    internal Task? OriginalWorker => Volatile.Read(ref _originalWorker);

    /// <summary>创建 JPEG 编码器，帧序号从 1 开始且永不回绕。</summary>
    public WpfJpegFrameEncoder() : this(SaveJpeg, FrameLimits.MaxPayloadBytes, 1) { }

    internal WpfJpegFrameEncoder(Action<BitmapSource, int, Stream> save,
        int payloadLimit = FrameLimits.MaxPayloadBytes, ulong firstFrameId = 1)
    {
        ArgumentNullException.ThrowIfNull(save);
        if (payloadLimit is <= 0 or > FrameLimits.MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(payloadLimit));
        _save = save;
        _payloadLimit = payloadLimit;
        _nextFrameId = firstFrameId;
    }

    /// <summary>编码一帧；校验设置、按 floor 缩放到至少 1×1，输出实际 JPEG 长度。</summary>
    /// <param name="frame">借用的 BGRA32 或 BGR24 帧；不得并发修改或提前释放。</param>
    /// <param name="settings">合法 FPS/缩放档位及 40..85 JPEG 质量。</param>
    /// <param name="cancellationToken">请求取消；不提前结束仍在执行的编码或资源回收。</param>
    /// <returns>持有独立 payload owner 的编码帧。</returns>
    public ValueTask<EncodedFrame> EncodeAsync(CapturedFrame frame,
        VideoQualitySettings settings, CancellationToken cancellationToken)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(frame);
            ArgumentNullException.ThrowIfNull(settings);
            if (cancellationToken.IsCancellationRequested)
                return ValueTask.FromCanceled<EncodedFrame>(cancellationToken);
            Validate(frame, settings);
        }
        catch (Exception error)
        {
            // 保留原 async 入口的异常 ValueTask 行为，不改成同步抛出校验错误。
            return ValueTask.FromException<EncodedFrame>(error);
        }

        // 只尝试取得发布锁，不排队；资格由公开 operation 完成状态决定，不在其 finally 中提前释放。
        if (!Monitor.TryEnter(_encodeGate))
            return ValueTask.FromException<EncodedFrame>(new InvalidOperationException("此 JPEG 实例已有编码正在执行，不接受排队。"));
        try
        {
            if (_operation is { IsCompleted: false })
                return ValueTask.FromException<EncodedFrame>(new InvalidOperationException("此 JPEG 实例已有编码正在执行，不接受排队。"));

            Volatile.Write(ref _originalWorker, null);
            _operation = EncodeOperationAsync(frame, settings, cancellationToken);
            return new ValueTask<EncodedFrame>(_operation);
        }
        finally
        {
            Monitor.Exit(_encodeGate);
        }
    }

    private async Task<EncodedFrame> EncodeOperationAsync(CapturedFrame frame,
        VideoQualitySettings settings, CancellationToken cancellationToken)
    {
        // 不给 Task.Run 传取消令牌，不使用 WaitAsync 或 TCS 替代原任务；清理完成后才传播取消。
        Task<EncodedFrame> worker = Task.Run(() => EncodeCore(frame, settings, cancellationToken));
        Volatile.Write(ref _originalWorker, worker);
        return await worker.ConfigureAwait(false);
    }

    private EncodedFrame EncodeCore(CapturedFrame frame, VideoQualitySettings settings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_sequenceExhausted)
            throw new InvalidOperationException("JPEG 帧序号已耗尽，不允许回绕。");
        ReadOnlySpan<byte> source = frame.Pixels.Span;
        int sourceLength = checked(frame.Stride * frame.Height);
        if (source.Length < sourceLength)
            throw new ArgumentException("像素 owner 小于 stride × height。", nameof(frame));
        int width = Math.Max(1, (int)Math.Floor(frame.Width * settings.Scale));
        int height = Math.Max(1, (int)Math.Floor(frame.Height * settings.Scale));
        int stride = checked((width * 3 + 3) & ~3);
        using var pixels = PooledByteOwner.Rent(checked(stride * height));
        pixels.Memory.Span.Clear();
        int sourcePixelBytes = frame.PixelFormat == FramePixelFormat.Bgra32 ? 4 : 3;
        // 确定性最近邻：floor(目标坐标 × 源尺寸 / 目标尺寸)，尊重源 stride，忽略 alpha。
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int sourceRow = checked((int)((long)y * frame.Height / height) * frame.Stride);
            for (int x = 0; x < width; x++)
            {
                int sourceOffset = sourceRow + (int)((long)x * frame.Width / width) * sourcePixelBytes;
                source.Slice(sourceOffset, 3).CopyTo(pixels.Memory.Span.Slice(y * stride + x * 3, 3));
            }
        }

        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24,
            null, pixels.Array, stride);
        bitmap.Freeze();
        using var staging = PooledByteOwner.Rent(_payloadLimit);
        // 数组支持的流不可扩展，并锁存 WIC 可能吞掉的底层错误；非空长度不证明 JPEG 完整。
        using var output = new JpegOutputStream(staging.Array, _payloadLimit);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _save(bitmap, settings.JpegQuality, output);
        }
        catch (Exception saveError)
        {
            if (output.Failure is { } failure && !ReferenceEquals(saveError, failure))
                throw new AggregateException("JPEG Save 与底层输出均失败。", saveError, failure);
            throw;
        }
        output.ThrowIfFailed();
        cancellationToken.ThrowIfCancellationRequested();
        int payloadLength = checked((int)output.Length);
        if (payloadLength is <= 0 || payloadLength > _payloadLimit)
            throw new InvalidDataException("JPEG 编码结果为空或超过 payload 上限。");

        PooledByteOwner? payload = PooledByteOwner.Rent(payloadLength);
        try
        {
            staging.Memory.Span[..payloadLength].CopyTo(payload.Memory.Span);
            cancellationToken.ThrowIfCancellationRequested();
            var encoded = new EncodedFrame(VideoCodec.Jpeg, width, height, _nextFrameId,
                frame.TimestampUs, checked((byte)settings.JpegQuality), payload, payloadLength);
            payload = null;
            if (_nextFrameId == ulong.MaxValue)
                _sequenceExhausted = true;
            else
                _nextFrameId++;
            // 唯一交付点之后不重新检查 caller 取消；序号只在成功交付时消耗。
            return encoded;
        }
        finally
        {
            payload?.Dispose();
        }
    }

    private static void Validate(CapturedFrame frame, VideoQualitySettings settings)
    {
        if (frame.PixelFormat is not (FramePixelFormat.Bgra32 or FramePixelFormat.Bgr24))
            throw new ArgumentOutOfRangeException(nameof(frame), "不支持的像素格式。");
        if (frame.TimestampUs < 0)
            throw new ArgumentOutOfRangeException(nameof(frame), "采集时间必须为非负单调微秒。");
        if (settings.TargetFps is not (5 or 10 or 15 or 20 or 30) ||
            settings.Scale is not (0.50 or 0.67 or 0.75 or 1.00) ||
            settings.JpegQuality is < VideoQualitySettings.MinJpegQuality or > VideoQualitySettings.MaxJpegQuality)
            throw new ArgumentOutOfRangeException(nameof(settings), "视频设置不属于支持的档位。");
        int minimumStride = checked(frame.Width * (frame.PixelFormat == FramePixelFormat.Bgra32 ? 4 : 3));
        if (frame.Stride < minimumStride || checked((long)frame.Stride * frame.Height) > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(frame), "stride 或像素长度非法。");
    }

    internal static void SaveJpeg(BitmapSource bitmap, int quality, Stream output)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(output);
    }
}
