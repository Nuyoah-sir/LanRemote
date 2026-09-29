using System.Runtime.ExceptionServices;
using LanRemote.Core.Models;

namespace LanRemote.Transport;

/// <summary>单次运行，串行接管、写出并释放帧；不预取，不拥有共享帧来源。</summary>
internal sealed class VideoFrameSender
{
    private int _started;
    private Exception? _sourceFailure;
    private Exception? _frameCleanupError;
    private Exception? _writerCleanupError;

    /// <summary>仅保留来源边界判定的首个真实故障，不受后续取消影响。</summary>
    internal Exception? SourceFailure => Volatile.Read(ref _sourceFailure);

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

            while (failure is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 必须等待来源完成；同步前缀的 Task.Run 监督属于上层路由。
                EncodedFrame? frame;
                try
                {
                    frame = await source.ReadNextAsync(sessionId, cancellationToken).ConfigureAwait(false);
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
                    await writer.WriteFrameAsync(frame, deadline.Token).ConfigureAwait(false);
                    CheckWriteBudget(clock, startedAt, frameWriteTimeout, cancellationToken, deadline.Token);
                }
                catch (Exception error)
                {
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
