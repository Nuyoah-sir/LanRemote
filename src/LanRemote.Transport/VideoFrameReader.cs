using System.Buffers;
using LanRemote.Core.Models;

namespace LanRemote.Transport;

/// <summary>拥有单条输入流的视频读取器；完整验证头之后才租用 payload 内存。</summary>
/// <remarks>
/// 内部 plumbing，不是已认证入口，不提供认证或 JPEG 解码校验。调用方须独占该流的读取，
/// 不得并发调用。首次失败、帧首 EOF 或 Dispose 均永久终止实例并且只尝试一次流释放；不扫描 magic 重同步。
/// 这里只用 caller 取消，没有内置 deadline；未来由 Session 外层对整帧施加时限。
/// 构造后的最终取消检查及状态 CAS 决定交付：1→0 的成功 CAS 是提交点，若终止的 2 先写入则不可交付。
/// 最终检查先处理父取消，CAS 失败也优先报告父取消；提交之后的终止不追溯收回已交付帧。
/// 终止不等于 join 完成；调用方仍须 await 在途操作，不保证硬中断任意 Stream。
/// 释放失败可从 CleanupErrors 观察，StreamDisposeSucceeded 只表示底层 Dispose 成功返回，而非保证资源已关闭。
/// </remarks>
internal sealed class VideoFrameReader : IDisposable
{
    private readonly Stream _stream;
    private readonly Func<int, IMemoryOwner<byte>> _rent;
    private readonly byte[] _header = new byte[VideoFrameHeader.Size];
    private int _state; // 0：空闲，1：正在读取，2：永久终止（不代表流释放或在途操作已完成）。
    private int _streamDisposeSucceeded;
    private readonly object _cleanupLock = new();
    private Exception? _streamCleanupError;
    private Exception? _ownerCleanupError;

    /// <summary>仅表示禁止后续操作；不是流已关闭或在途操作已退出的证明。</summary>
    internal bool IsTerminated => Volatile.Read(ref _state) == 2;

    /// <summary>仅在底层 Stream.Dispose 正常返回后为 true；失败、尚未尝试或仍在释放时为 false。</summary>
    internal bool StreamDisposeSucceeded => Volatile.Read(ref _streamDisposeSucceeded) != 0;

    /// <summary>
    /// 当前已记录的清理错误的稳定只读快照，顺序为 stream、owner。
    /// 固定最多两项：流只释放一次，首个失败后最多有一个尚未交付的 owner；后续拒绝不累积错误。
    /// </summary>
    internal IReadOnlyList<Exception> CleanupErrors
    {
        get
        {
            lock (_cleanupLock)
            {
                List<Exception> snapshot = new(2);
                if (_streamCleanupError is not null)
                {
                    snapshot.Add(_streamCleanupError);
                }

                if (_ownerCleanupError is not null)
                {
                    snapshot.Add(_ownerCleanupError);
                }

                return snapshot.AsReadOnly();
            }
        }
    }

    internal VideoFrameReader(Stream stream, Func<int, IMemoryOwner<byte>>? rent = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _rent = rent ?? MemoryPool<byte>.Shared.Rent;
    }

    /// <summary>完整读一帧；仅帧首无数据 EOF 返回 null，截断头/体抛出不同消息的 EndOfStreamException。</summary>
    internal async Task<EncodedFrame?> ReadFrameAsync(CancellationToken cancellationToken = default)
    {
        IMemoryOwner<byte>? owner = null;
        EncodedFrame? uncommitted = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            int previous = Interlocked.CompareExchange(ref _state, 1, 0);
            ObjectDisposedException.ThrowIf(previous == 2, this);
            if (previous == 1)
            {
                throw new InvalidOperationException("视频读取器不允许重叠读取。");
            }

            if (!await ReadExactlyAsync(_header, isHeader: true, cancellationToken).ConfigureAwait(false))
            {
                Dispose();
                return null;
            }

            VideoFrameHeader header = VideoFrameHeader.Read(_header);
            cancellationToken.ThrowIfCancellationRequested();
            owner = _rent(header.PayloadLength);
            Memory<byte> payload = owner.Memory[..header.PayloadLength];
            await ReadExactlyAsync(payload, isHeader: false, cancellationToken).ConfigureAwait(false);

            uncommitted = new EncodedFrame(
                header.Codec, header.Width, header.Height, header.FrameId, header.TimestampUs,
                header.JpegQuality, owner, header.PayloadLength);
            owner = null; // 所有权仅转给本地未交付帧，不能把构造成功当作交付成功。

            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.CompareExchange(ref _state, 0, 1) != 1)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new ObjectDisposedException(nameof(VideoFrameReader));
            }

            // CAS 是交付提交点；此后既不访问用户内存，也不再清理已交付 owner。
            EncodedFrame delivered = uncommitted;
            uncommitted = null;
            return delivered;
        }
        catch
        {
            try
            {
                Dispose();
            }
            catch
            {
                // Dispose 已记录原始关闭错误；继续清理 owner，不替换主异常。
            }

            try
            {
                if (uncommitted is not null)
                {
                    uncommitted.Dispose();
                }
                else
                {
                    owner?.Dispose();
                }
            }
            catch (Exception cleanupError)
            {
                lock (_cleanupLock)
                {
                    _ownerCleanupError ??= cleanupError;
                }
            }

            // 不用 finally 中的 Dispose 或 AggregateException 覆盖原异常实例、类型和取消令牌。
            throw;
        }
    }

    private async Task<bool> ReadExactlyAsync(Memory<byte> buffer, bool isHeader, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _state) == 2, this);
            int read = await _stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _state) == 2, this);
            if (read == 0)
            {
                if (isHeader && offset == 0)
                {
                    return false;
                }

                string part = isHeader ? "video-header-truncated" : "video-payload-truncated";
                throw new EndOfStreamException($"{part}：读到 {offset}/{buffer.Length} 字节时遇到 EOF。");
            }

            offset += read;
        }

        return true;
    }

    /// <summary>
    /// 先永久终止，再且仅尝试一次流释放。直接调用可抛出原始关闭异常，同时记录诊断；不盲目重试。
    /// 不拥有注入的池/租用委托，不释放已交付帧，也不等待在途操作退出。
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
                lock (_cleanupLock)
                {
                    _streamCleanupError ??= cleanupError;
                }

                throw;
            }
        }
    }
}
