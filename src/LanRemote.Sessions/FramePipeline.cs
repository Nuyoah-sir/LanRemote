using LanRemote.Core;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;

namespace LanRemote.Sessions;

/// <summary>单次启动、双级 DropOldest 的视频生产管线；依赖需专用借出至 Completion 完成，不由管线释放。</summary>
public sealed class FramePipeline : IAsyncDisposable
{
    private const int Started = 1;
    private const int StopRequested = 2;
    private static readonly TimeSpan CaptureInterval = TimeSpan.FromMilliseconds(200);

    private readonly IScreenCaptureBackend _capture;
    private readonly IFrameEncoder _encoder;
    private readonly DisplayId _displayId;
    private readonly TimeProvider _timeProvider;
    private readonly Action? _afterCaptureLoopCreated;
    private readonly VideoQualitySettings _settings = new(5, 0.5, 60, false);
    private readonly BoundedFrameQueue<CapturedFrame> _rawQueue;
    private readonly BoundedFrameQueue<EncodedFrame> _encodedQueue;
    private readonly CancellationTokenSource _stopSource = new();
    private readonly CancellationToken _stopToken;
    private readonly TaskCompletionSource _stopSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _startPublished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _completion;

    private Task _captureLoop = Task.CompletedTask;
    private Task _encodeLoop = Task.CompletedTask;
    private Exception? _startError;
    private int _state;
    private int _reading;

    /// <summary>创建管线但不采集；两个队列的容量均只能为 1 或 2。</summary>
    public FramePipeline(
        IScreenCaptureBackend capture,
        IFrameEncoder encoder,
        DisplayId displayId,
        int rawCapacity = 2,
        int encodedCapacity = 2,
        TimeProvider? timeProvider = null)
        : this(capture, encoder, displayId, rawCapacity, encodedCapacity, timeProvider, null)
    {
    }

    internal FramePipeline(
        IScreenCaptureBackend capture,
        IFrameEncoder encoder,
        DisplayId displayId,
        int rawCapacity,
        int encodedCapacity,
        TimeProvider? timeProvider,
        Action? afterCaptureLoopCreated)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(encoder);
        _rawQueue = new BoundedFrameQueue<CapturedFrame>(rawCapacity);
        _encodedQueue = new BoundedFrameQueue<EncodedFrame>(encodedCapacity);
        _capture = capture;
        _encoder = encoder;
        _displayId = displayId;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _afterCaptureLoopCreated = afterCaptureLoopCreated;
        _stopToken = _stopSource.Token;
        _completion = Task.Run(CoordinateStopAsync);
    }

    /// <summary>稳定的完整收尾任务；任何非预期错误均以 AggregateException 保留完整错误树。</summary>
    public Task Completion => _completion;

    internal bool StopWonBeforeStart =>
        (Volatile.Read(ref _state) & (Started | StopRequested)) == StopRequested;

    /// <summary>仅能启动一次；已经请求停止的管线不能启动。</summary>
    public void Start()
    {
        if (Interlocked.CompareExchange(ref _state, Started, 0) != 0)
        {
            throw new InvalidOperationException("管线只能启动一次，且停止后不能启动。");
        }

        try
        {
            // 不传取消令牌，确保调度成功的循环及其清理一定执行；同步前缀也在后台。
            _captureLoop = Task.Run(CaptureLoopAsync);
            _afterCaptureLoopCreated?.Invoke();
            _encodeLoop = Task.Run(EncodeLoopAsync);
        }
        catch (Exception exception)
        {
            _startError = exception;
            RequestStop();
            throw;
        }
        finally
        {
            // 即使 Stop 与 Start 发布竞争，也必须等两个真实循环引用发布完毕。
            _startPublished.TrySetResult();
        }
    }

    /// <summary>
    /// 单消费者读取；重叠队列读取被拒绝。成功取得帧即交由调用方释放，不做后置取消检查。
    /// 停止时等待完整收尾，成功返回 null，失败报告 Completion 的错误。
    /// 取消令牌仅作用于队列读取；进入完整收尾等待后不再响应读取取消。
    /// 读槽在队列读取结束时释放，允许多个调用同时等待最终 EOF，不重复取帧。
    /// </summary>
    public ValueTask<EncodedFrame?> ReadNextAsync(CancellationToken cancellationToken = default)
    {
        if ((Volatile.Read(ref _state) & Started) == 0)
        {
            throw new InvalidOperationException("管线尚未启动，不能读取帧。");
        }

        if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
        {
            throw new InvalidOperationException("仅允许一个尚未完成的队列读取。");
        }

        return ReadCoreAsync(cancellationToken);
    }

    /// <summary>请求永久停止，返回与 Completion 相同的完整 join 任务；允许启动前停止。</summary>
    public Task StopAsync()
    {
        RequestStop();
        return _completion;
    }

    /// <summary>与 StopAsync 共用同一任务，不释放借用的采集后端及编码器。</summary>
    public ValueTask DisposeAsync() => new(StopAsync());

    private bool IsStopRequested => (Volatile.Read(ref _state) & StopRequested) != 0;

    private void RequestStop()
    {
        int previous = Interlocked.Or(ref _state, StopRequested);
        if ((previous & Started) == 0)
        {
            // Stop 赢过 Start 后，永远不会再有循环需要发布。
            _startPublished.TrySetResult();
        }

        _stopSignal.TrySetResult();
    }

    private async ValueTask<EncodedFrame?> ReadCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _encodedQueue.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (IsStopRequested)
        {
            // 仅转换队列自身的停止异常；调用方取消不停止管线。
        }
        finally
        {
            Volatile.Write(ref _reading, 0);
        }

        // 先释放读槽，且协调任务不 join Read，避免 Completion 与读操作互等。
        // 若队列已经移交帧，即使读 continuation 迟到，帧也仍由调用方负责。
        await _completion.ConfigureAwait(false);
        return null;
    }

    private async Task CaptureLoopAsync()
    {
        List<Exception> errors = [];
        CapturedFrame? frame = null;
        try
        {
            while (!IsStopRequested)
            {
                long startedAt = _timeProvider.GetTimestamp();
                if (IsStopRequested)
                {
                    break;
                }

                Task<CapturedFrame>? operation = null;
                try
                {
                    // AsTask 只调用一次，完整等待原 ValueTask；保留 Task 的全部 fault 根。
                    operation = _capture.CaptureAsync(_displayId, _stopToken).AsTask();
                    frame = await operation.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    RecordOperationError(errors, exception, operation);
                    break;
                }

                // 迟到结果先进入本地 ownership，再检查停止，由 finally 回收。
                if (IsStopRequested)
                {
                    break;
                }

                bool accepted = _rawQueue.TryWrite(frame, out Exception? disposalError);
                if (accepted)
                {
                    frame = null;
                }

                if (disposalError is not null)
                {
                    RecordError(errors, disposalError);
                    break;
                }

                if (!accepted || IsStopRequested)
                {
                    break;
                }

                // 每轮以本次实际启动时间为基准，超预算不补帧、不追赶旧截止点。
                TimeSpan remaining = CaptureInterval - _timeProvider.GetElapsedTime(startedAt);
                if (remaining > TimeSpan.Zero)
                {
                    Task? delay = null;
                    try
                    {
                        delay = Task.Delay(remaining, _timeProvider, _stopToken);
                        await delay.ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        RecordOperationError(errors, exception, delay);
                        break;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            // 计时器取时等非取消点的 OCE 也必须作为错误保留。
            RecordError(errors, exception);
        }
        finally
        {
            DisposeOwned(ref frame, errors);
        }

        if (errors.Count != 0)
        {
            throw new AggregateException("采集循环失败。", errors);
        }
    }

    private async Task EncodeLoopAsync()
    {
        List<Exception> errors = [];
        CapturedFrame? raw = null;
        EncodedFrame? encoded = null;
        try
        {
            while (!IsStopRequested)
            {
                try
                {
                    raw = await _rawQueue.ReadAsync(_stopToken).ConfigureAwait(false);
                }
                catch (ObjectDisposedException) when (IsStopRequested)
                {
                    break;
                }
                catch (OperationCanceledException exception) when (IsOwnCancellation(exception))
                {
                    break;
                }

                if (IsStopRequested)
                {
                    break;
                }

                Task<EncodedFrame>? operation = null;
                try
                {
                    // raw 必须一直活到原 EncodeAsync（含同步前缀及异步尾部）真实退出。
                    operation = _encoder.EncodeAsync(raw, _settings, _stopToken).AsTask();
                    encoded = await operation.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    RecordOperationError(errors, exception, operation);
                    break;
                }

                DisposeOwned(ref raw, errors);
                if (IsStopRequested)
                {
                    break;
                }

                bool accepted = _encodedQueue.TryWrite(encoded, out Exception? disposalError);
                if (accepted)
                {
                    encoded = null;
                }

                if (disposalError is not null)
                {
                    RecordError(errors, disposalError);
                    break;
                }

                if (!accepted)
                {
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            RecordError(errors, exception);
        }
        finally
        {
            DisposeOwned(ref raw, errors);
            DisposeOwned(ref encoded, errors);
        }

        if (errors.Count != 0)
        {
            throw new AggregateException("编码循环失败。", errors);
        }
    }

    private bool IsOwnCancellation(OperationCanceledException exception) =>
        exception.CancellationToken == _stopToken && _stopToken.IsCancellationRequested;

    private void RecordOperationError(List<Exception> errors, Exception exception, Task? operation)
    {
        if (operation is { IsFaulted: true })
        {
            // await 只抛其中一个根；已 Faulted 的任务（包括其中的 OCE）不是正常取消。
            // 不 Flatten，保留多 fault 与每个原始 AggregateException 对象。
            foreach (Exception root in operation.Exception!.InnerExceptions)
            {
                RecordError(errors, root);
            }
        }
        else if (exception is not OperationCanceledException canceled || !IsOwnCancellation(canceled))
        {
            RecordError(errors, exception);
        }
    }

    private void RecordError(List<Exception> errors, Exception exception)
    {
        errors.Add(exception);
        // 首错即停止；每个循环只剩有限清理阶段，不积累逐帧错误，也不 await 自己的 join。
        RequestStop();
    }

    private void DisposeOwned<T>(ref T? owner, List<Exception> errors) where T : class, IDisposable
    {
        T? owned = owner;
        owner = null;
        try
        {
            owned?.Dispose();
        }
        catch (Exception exception)
        {
            // 先清 owner 再尝试一次 Dispose；清理错不覆盖原错，也不把 cleanup OCE 当取消。
            RecordError(errors, exception);
        }
    }

    private static Task RunCleanup(Action cleanup) => Task.Run(() =>
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            // 包装 OCE，避免后台任务变成 Canceled 而遗失清理异常对象。
            throw new AggregateException("停止管线时清理失败。", exception);
        }
    });

    private async Task CoordinateStopAsync()
    {
        await _stopSignal.Task.ConfigureAwait(false);

        // 固定三个独立清理任务：一个阻塞的队列 disposer 或取消 callback 不阻止其他任务。
        Task rawStop = RunCleanup(_rawQueue.Stop);
        Task encodedStop = RunCleanup(_encodedQueue.Stop);
        Task cancel = RunCleanup(() => _stopSource.Cancel());

        await _startPublished.Task.ConfigureAwait(false);
        List<Exception> errors = [];
        if (_startError is not null)
        {
            errors.Add(_startError);
        }

        // 循环包含原操作、同步前缀、本地帧清理及 TryWrite 的锁外 drop disposer。
        Task joined = Task.WhenAll(_captureLoop, _encodeLoop, rawStop, encodedStop, cancel);
        try
        {
            await joined.ConfigureAwait(false);
        }
        catch
        {
            // 循环及清理任务只会成功或以 AggregateException fault，不会以 Canceled 退出。
            errors.AddRange(joined.Exception!.InnerExceptions);
        }

        try
        {
            // 必须在原操作和 Cancel 的全部 callback 都退出后才能释放 CTS。
            _stopSource.Dispose();
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        if (errors.Count != 0)
        {
            throw new AggregateException("帧管线停止时发生错误。", errors);
        }
    }
}
