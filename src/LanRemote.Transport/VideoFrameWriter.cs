using LanRemote.Core.Models;

namespace LanRemote.Transport;

/// <summary>拥有单条输出流的视频写出器；直接写 40 字节头及有效 payload，不加 Control 长度前缀。</summary>
/// <remarks>
/// 内部 plumbing，不是已认证入口，不提供认证或 JPEG 解码校验。调用方须独占该流的写入，
/// 不得并发调用；整帧写出完成前不得替换、修改或释放输入帧。写出器始终不拥有输入帧。
/// 首次失败（包括模型错误、取消）或 Dispose 均永久终止并且只尝试一次流释放，不重试半帧。
/// 这里只用 caller 取消，没有内置 deadline；未来由 Session 外层对整帧施加时限。
/// 终止不等于 join 完成；调用方仍须 await 在途操作，不保证硬中断任意 Stream。
/// CleanupErrors 暴露关闭错误，StreamDisposeSucceeded 仅表示底层 Dispose 成功返回，不保证资源实际已关闭。
/// </remarks>
internal sealed class VideoFrameWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly byte[] _header = new byte[VideoFrameHeader.Size];
    private int _state; // 0：空闲，1：正在写入，2：永久终止（不代表流释放或在途操作已完成）。
    private int _streamDisposeSucceeded;
    private Exception? _streamCleanupError;

    /// <summary>仅表示禁止后续操作，不是 join 完成或底层流已关闭的证明。</summary>
    internal bool IsTerminated => Volatile.Read(ref _state) == 2;

    /// <summary>底层 Stream.Dispose 正常返回后才为 true；失败、尚未尝试或仍在释放时为 false。</summary>
    internal bool StreamDisposeSucceeded => Volatile.Read(ref _streamDisposeSucceeded) != 0;

    /// <summary>首次流释放错误的稳定只读快照，固定最多一项；不拥有调用方帧，后续拒绝不累积错误。</summary>
    internal IReadOnlyList<Exception> CleanupErrors
    {
        get
        {
            Exception? error = Volatile.Read(ref _streamCleanupError);
            return Array.AsReadOnly<Exception>(error is null ? [] : [error]);
        }
    }

    internal VideoFrameWriter(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
    }

    internal async Task WriteFrameAsync(EncodedFrame frame, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            int previous = Interlocked.CompareExchange(ref _state, 1, 0);
            ObjectDisposedException.ThrowIf(previous == 2, this);
            if (previous == 1)
            {
                throw new InvalidOperationException("视频写出器不允许重叠写入。");
            }

            VideoFrameHeader header = VideoFrameHeader.FromFrame(frame);
            ReadOnlyMemory<byte> payload = frame.Payload;
            if (payload.Span.Length != header.PayloadLength)
            {
                throw new FrameProtocolException("video-payload-length-mismatch");
            }

            header.Write(_header);
            CheckActive(cancellationToken);
            await _stream.WriteAsync(_header, cancellationToken).ConfigureAwait(false);
            CheckActive(cancellationToken);
            // WriteAsync 的契约是写完整块才完成；仅传有效切片，不接触池尾部。
            await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            CheckActive(cancellationToken);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            CheckActive(cancellationToken);
            if (Interlocked.CompareExchange(ref _state, 0, 1) != 1)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new ObjectDisposedException(nameof(VideoFrameWriter));
            }
        }
        catch
        {
            try
            {
                Dispose();
            }
            catch
            {
                // Dispose 已发布关闭错误快照，仍保留主异常的原实例、类型和取消令牌。
            }

            throw;
        }
    }

    private void CheckActive(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _state) == 2, this);
    }

    /// <summary>
    /// 先永久终止，再且仅尝试一次流释放；直接调用可抛原始关闭异常，同时发布诊断，不盲目重试。
    /// 不释放调用方帧，不等待在途操作退出。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _state, 2) != 2)
        {
            try
            {
                _stream.Dispose();
                Volatile.Write(ref _streamDisposeSucceeded, 1);
            }
            catch (Exception cleanupError)
            {
                Interlocked.CompareExchange(ref _streamCleanupError, cleanupError, null);
                throw;
            }
        }
    }
}
