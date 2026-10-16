using System.Runtime.ExceptionServices;
using LanRemote.Core.Models;

namespace LanRemote.Transport;

/// <summary>单次运行，串行接管、写出并释放帧；不预取，不拥有共享帧来源。</summary>
internal sealed class VideoFrameSender
{
    private readonly bool _useSequentialFrameIds;
    private ulong _nextFrameId;
    private bool _sequenceExhausted;
    private int _started;
    private Exception? _sourceFailure;
    private Exception? _sendFailure;
    private Exception? _frameCleanupError;
    private Exception? _writerCleanupError;
    private readonly ReplaceableVideoFrameSource? _trackedSource;
    private GenerationSendSnapshot? _lastSuccessfulGeneration;

    /// <summary>最多一代的首个与最新成功帧；纯元数据历史，不持有 payload/producer，不是接收或显示 ACK。</summary>
    internal sealed record GenerationSendSnapshot(
        Guid SessionId,
        ReplaceableVideoFrameSource.GenerationIdentity Generation,
        SentFrame First,
        SentFrame Latest);

    internal sealed record SentFrame(ulong FrameId, int Width, int Height);

    internal GenerationSendSnapshot? LastSuccessfulGeneration => Volatile.Read(ref _lastSuccessfulGeneration);

    /// <summary>默认逐帧透传来源编号，包括重复、倒序与完整 ulong 范围。</summary>
    internal VideoFrameSender() { }

    /// <summary>
    /// 显式启用本 sender 的写入尝试编号（允许起点 0）。分配不是完整发送或显示证明。
    /// MaxValue 只分配一次；之后允许来源 EOF，若又交付帧则释放该帧并失败，不回绕、不重试。
    /// </summary>
    internal VideoFrameSender(ulong firstFrameId)
    {
        _useSequentialFrameIds = true;
        _nextFrameId = firstFrameId;
    }

    /// <summary>显式绑定换代来源及串行 wire 编号；其他构造仍不启用成功记录。</summary>
    internal VideoFrameSender(ulong firstFrameId, ReplaceableVideoFrameSource source) : this(firstFrameId)
    {
        ArgumentNullException.ThrowIfNull(source);
        _trackedSource = source;
    }

    /// <summary>仅保留来源边界判定的首个真实故障，不受后续取消影响。</summary>
    internal Exception? SourceFailure => Volatile.Read(ref _sourceFailure);

    // 独立发送操作的错误槽；不含来源传播或 frame/writer 清理，避免按异常引用猜测归属。
    internal Exception? SendFailure => Volatile.Read(ref _sendFailure);

    /// <summary>稳定只读快照，最多保留 frame / writer 各自的首个清理错误。</summary>
    internal IReadOnlyList<Exception> CleanupErrors
    {
        get
        {
            Exception? frameError = Volatile.Read(ref _frameCleanupError);
            Exception? writerError = Volatile.Read(ref _writerCleanupError);
            return Array.AsReadOnly<Exception>(frameError is null
                ? writerError is null ? [] : [writerError]
                : writerError is null ? [frameError] : [frameError, writerError]);
        }
    }

    internal async Task SendAsync(
        Guid sessionId,
        VideoFrameWriter writer,
        IVideoFrameSource source,
        TimeProvider clock,
        TimeSpan frameWriteTimeout,
        CancellationToken cancellationToken)
    {
        // 拒绝重入不能释放正在被首次调用使用的 writer。
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("视频发送器只能运行一次。");
        }

        ArgumentNullException.ThrowIfNull(writer);
        ExceptionDispatchInfo? failure = null;
        try
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(clock);
            ArgumentOutOfRangeException.ThrowIfLessThan(frameWriteTimeout, TimeSpan.Zero);
            if (_trackedSource is not null &&
                (!ReferenceEquals(source, _trackedSource) || sessionId != _trackedSource.SessionId))
                throw new ArgumentException("成功记录必须使用绑定来源及其原会话。", nameof(source));

            while (failure is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 必须等待来源完成；同步前缀的 Task.Run 监督属于上层路由。
                EncodedFrame? frame;
                ReplaceableVideoFrameSource.ReadOrigin? origin = null;
                try
                {
                    frame = await (_trackedSource is null
                        ? source.ReadNextAsync(sessionId, cancellationToken)
                        : _trackedSource.ReadForSendAsync(sessionId, cancellationToken, out origin)).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException ||
                    !cancellationToken.IsCancellationRequested)
                {
                    // 在来源边界分类，不能在 Router 收尾取消后反推；IO/ODE 也不是网络断连。
                    Interlocked.CompareExchange(ref _sourceFailure, error, null);
                    throw;
                }
                AuthenticationDeadline? deadline = null;
                try
                {
                    // 先接管所有权再检查取消，迟到的帧也会进入 finally。
                    cancellationToken.ThrowIfCancellationRequested();
                    if (frame is null)
                    {
                        break;
                    }

                    long startedAt = clock.GetTimestamp();
                    TimeSpan remaining = CheckWriteBudget(
                        clock, startedAt, frameWriteTimeout, cancellationToken, cancellationToken);
                    deadline = new AuthenticationDeadline(clock, remaining, cancellationToken);
                    CheckWriteBudget(clock, startedAt, frameWriteTimeout, cancellationToken, deadline.Token);
                    ulong frameId = frame.FrameId;
                    if (_useSequentialFrameIds)
                    {
                        // 只在串行写入前分配；即使原写出部分失败，也不复用已分配编号。
                        frameId = ReserveFrameId();
                        await writer.WriteFrameWithIdAsync(frame, frameId, deadline.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        await writer.WriteFrameAsync(frame, deadline.Token).ConfigureAwait(false);
                    }
                    CheckWriteBudget(clock, startedAt, frameWriteTimeout, cancellationToken, deadline.Token);
                    if (_trackedSource is not null)
                    {
                        // writer 原任务包含 header/payload/flush 与终检；本 sender 末预算检查也通过后才记历史。
                        // 不能在这里读 source 当前代；Replace 可能已经完成，但在途帧仍属于旧代。
                        ReplaceableVideoFrameSource.GenerationIdentity generation = origin?.Generation
                            ?? throw new InvalidOperationException("成功写出的帧缺少所属代际。");
                        SentFrame sent = new(frameId, frame.Width, frame.Height);
                        GenerationSendSnapshot? previous = LastSuccessfulGeneration;
                        SentFrame first = ReferenceEquals(previous?.Generation, generation) ? previous!.First : sent;
                        Volatile.Write(ref _lastSuccessfulGeneration, new(sessionId, generation, first, sent));
                    }
                }
                catch (Exception error)
                {
                    Interlocked.CompareExchange(ref _sendFailure, error, null);
                    failure = ExceptionDispatchInfo.Capture(error);
                }
                finally
                {
                    try
                    {
                        deadline?.Dispose();
                    }
                    catch (Exception error)
                    {
                        Interlocked.CompareExchange(ref _sendFailure, error, null);
                        failure ??= ExceptionDispatchInfo.Capture(error);
                    }

                    // 不以取消或 timer 通知代替 join；只能在 WriteFrameAsync 结束后释放。
                    try
                    {
                        frame?.Dispose();
                    }
                    catch (Exception error)
                    {
                        Interlocked.CompareExchange(ref _frameCleanupError, error, null);
                        failure ??= ExceptionDispatchInfo.Capture(error);
                    }
                }
            }
        }
        catch (Exception error)
        {
            if (SourceFailure is null) Interlocked.CompareExchange(ref _sendFailure, error, null);
            failure ??= ExceptionDispatchInfo.Capture(error);
        }
        finally
        {
            Exception? writerError = null;
            try
            {
                // Host 适配器只请求关闭，物理 close 的 await 仍属于上层路由。
                writer.Dispose();
            }
            catch (Exception error)
            {
                writerError = error;
            }

            // 写出失败时 writer 可能已自行终止，后续 Dispose 不会再次抛出关闭错误。
            IReadOnlyList<Exception> writerErrors = writer.CleanupErrors;
            writerError ??= writerErrors.Count == 0 ? null : writerErrors[0];
            if (writerError is not null)
            {
                Interlocked.CompareExchange(ref _writerCleanupError, writerError, null);
                failure ??= ExceptionDispatchInfo.Capture(writerError);
            }
        }

        failure?.Throw();
    }

    private ulong ReserveFrameId()
    {
        if (_sequenceExhausted)
        {
            throw new InvalidOperationException("视频发送帧编号已耗尽。");
        }

        ulong frameId = _nextFrameId;
        if (frameId == ulong.MaxValue)
        {
            _sequenceExhausted = true;
        }
        else
        {
            _nextFrameId++;
        }
        return frameId;
    }

    private static TimeSpan CheckWriteBudget(
        TimeProvider clock,
        long startedAt,
        TimeSpan budget,
        CancellationToken cancellationToken,
        CancellationToken writeToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TimeSpan elapsed = clock.GetElapsedTime(startedAt);
        // 取时本身可同步阻塞或重入取消，返回后重新观察 caller。
        cancellationToken.ThrowIfCancellationRequested();
        if (elapsed >= budget)
        {
            throw new OperationCanceledException("视频整帧写出时限到期。", writeToken);
        }

        writeToken.ThrowIfCancellationRequested();
        return budget - elapsed;
    }
}
