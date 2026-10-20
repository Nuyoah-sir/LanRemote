using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Bridge = LanRemote.Transport.AuthenticatedControlSession.ClientDisplayPresentation;
using FrameStamp = LanRemote.Transport.AuthenticatedControlSession.FrameStamp;
using PresentationFrame = LanRemote.Transport.AuthenticatedControlSession.PresentationFrame;

namespace LanRemote.App;

/// <summary>内部 opt-in：真实读取、冻结解码和单个原 Dispatcher 操作共用父提交屏障。</summary>
internal sealed class FencedVideoPresenter
{
    private readonly Bridge _bridge;
    private readonly Image _image;
    private readonly CancellationTokenSource _stop;
    private readonly Task _reader;
    private readonly Task _decoder;
    private readonly Task _renderer;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PresentationFrame? _encoded;
    private DecodedFrame? _decoded;
    private ulong? _encodedHigh;
    private ulong? _decodedHigh;
    private bool _stopping;
    private bool _stopDisposed;
    private int _releaseFailed;
    private Task? _shutdown;
    private Task? _cancel;
    private Task<PresenterDrainEvidence>? _disposalEvidence;
    private object Gate => _bridge.Gate;

    internal FencedVideoPresenter(Bridge bridge, Image image)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _image = image ?? throw new ArgumentNullException(nameof(image));
        _stop = new();
        lock (Gate)
        {
            _reader = Task.Run(() => RunWorkerAsync(ReadFramesAsync));
            _decoder = Task.Run(() => RunWorkerAsync(DecodeFramesAsync));
            _renderer = Task.Run(() => RunWorkerAsync(RenderFramesAsync));
            Completion = Task.WhenAll(_reader, _decoder, _renderer);
        }
    }

    internal Task Completion { get; }

    private async Task RunWorkerAsync(Func<Task> body)
    {
        try
        {
            // 首次取得父 gate 后才允许外部操作，三个原任务和 Completion 此时均已发布。
            lock (Gate) if (_stopping) return;
            await body().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _ = RequestStop();
            // OCE 也保留原实例并成为 Faulted，避免联合等待吞掉取消分支的原始诊断。
            throw new AggregateException("屏障呈现 worker 失败。", error);
        }
        finally { lock (Gate) SignalUnderGate(); }
    }

    private async Task ReadFramesAsync()
    {
        while (true)
        {
            lock (Gate) if (_stopping) return;
            Task<PresentationFrame?> original = _bridge.ReadFrameAsync(_stop.Token);
            PresentationFrame? frame;
            try { frame = await original.ConfigureAwait(false); }
            catch (Exception error) { throw OriginalError(original, error); }
            if (frame is null) { _ = RequestStop(); return; }

            PresentationFrame? release = frame;
            Exception? handoffError = null;
            try
            {
                lock (Gate)
                {
                    if (!_stopping && _bridge.IsCurrentUnderGate(frame.Stamp) &&
                        (_encodedHigh is not { } last || frame.Stamp.FrameId > last))
                    {
                        release = _encoded;
                        _encoded = frame;
                        _encodedHigh = frame.Stamp.FrameId;
                        SignalUnderGate();
                    }
                }
            }
            catch (Exception error) { handoffError = error; _ = RequestStop(); throw; }
            finally
            {
                // 接纳前的帧或被摘旧帧均由此原 reader 覆盖同步释放；stale 不是 EOF。
                if (release is not null) DisposeFrame(release, handoffError);
            }
        }
    }

    private async Task DecodeFramesAsync()
    {
        while (true)
        {
            PresentationFrame? frame;
            Task changed, bridgeChanged;
            bool bridgeStopped;
            lock (Gate)
            {
                if (_stopping) return;
                bridgeStopped = _bridge.IsStoppedUnderGate;
                frame = bridgeStopped ? null : _encoded;
                if (frame is not null) _encoded = null;
                changed = _changed.Task;
                bridgeChanged = _bridge.ChangedUnderGate;
            }
            if (bridgeStopped) { _ = RequestStop(); return; }
            if (frame is null)
            {
                await Task.WhenAny(changed, bridgeChanged).ConfigureAwait(false);
                continue;
            }

            Exception? decodeError = null;
            try
            {
                lock (Gate)
                    if (_stopping || !_bridge.IsCurrentUnderGate(frame.Stamp)) continue;
                BitmapSource bitmap = RemoteVideoPresenter.DecodeJpeg(frame.Frame);
                lock (Gate)
                {
                    if (!_stopping && _bridge.IsCurrentUnderGate(frame.Stamp) &&
                        (_decodedHigh is not { } last || frame.Stamp.FrameId > last))
                    {
                        _decodedHigh = frame.Stamp.FrameId;
                        _decoded = new(frame.Stamp, bitmap);
                        SignalUnderGate();
                    }
                }
            }
            catch (Exception error)
            {
                decodeError = error;
                _ = RequestStop(); // 先关线，不能让随后阻塞的 owner.Dispose 推迟额外关闭。
                throw;
            }
            finally { DisposeFrame(frame, decodeError); }
        }
    }

    private async Task RenderFramesAsync()
    {
        while (true)
        {
            DecodedFrame? candidate;
            Task changed, bridgeChanged;
            bool bridgeStopped, canPresent;
            lock (Gate)
            {
                if (_stopping) return;
                bridgeStopped = _bridge.IsStoppedUnderGate;
                candidate = _decoded;
                if (candidate is not null && !_bridge.IsCurrentUnderGate(candidate.Stamp))
                {
                    _decoded = candidate = null;
                    SignalUnderGate();
                }
                changed = _changed.Task;
                bridgeChanged = _bridge.ChangedUnderGate;
                canPresent = candidate is not null && _bridge.CanPresentUnderGate(candidate.Stamp,
                    candidate.Bitmap.PixelWidth, candidate.Bitmap.PixelHeight);
            }
            if (bridgeStopped) { _ = RequestStop(); return; }
            if (!canPresent || candidate is null)
            {
                // ACK 先到或后到均重新检查；低于回执下界的帧不排 UI、不忙轮询。
                await Task.WhenAny(changed, bridgeChanged).ConfigureAwait(false);
                continue;
            }

            // 即使停止在登记前赢得 gate，此 worker 仍拥有并等待原操作，回调只会拒绝提交。
            DispatcherOperation operation = _image.Dispatcher.InvokeAsync(
                () => RenderCandidate(candidate), DispatcherPriority.Render);
            Task original = operation.Task;
            try { await original.ConfigureAwait(false); }
            catch (Exception error) { throw OriginalError(original, error); }
        }
    }

    private void RenderCandidate(DecodedFrame candidate)
    {
        lock (Gate)
        {
            if (_stopping || !ReferenceEquals(_decoded, candidate)) return;
            try
            {
                if (_bridge.IsCurrentUnderGate(candidate.Stamp))
                    _bridge.TryCommit(candidate.Stamp, candidate.Bitmap.PixelWidth, candidate.Bitmap.PixelHeight,
                        () => _image.Source = candidate.Bitmap);
            }
            finally
            {
                // Source setter 可重入停止；绝不能清掉其他候选或新 epoch 的槽。
                if (ReferenceEquals(_decoded, candidate))
                {
                    _decoded = null;
                    SignalUnderGate();
                }
            }
        }
    }

    internal Task StopAndDisposeAsync() => RequestStop();

    private Task RequestStop()
    {
        lock (Gate)
        {
            if (_shutdown is not null) return _shutdown;
            _stopping = true;
            _decoded = null;
            _bridge.RevokeUnderGate();
            SignalUnderGate();
            // 不执行取消、owner 释放或父 join；先发布唯一收尾任务。
            return _shutdown = Task.Run(StopCoreAsync);
        }
    }

    private async Task StopCoreAsync()
    {
        PresentationFrame? remaining;
        Task cancellation;
        lock (Gate)
        {
            remaining = _encoded;
            _encoded = null;
            cancellation = _cancel = Task.Run(() =>
            {
                lock (Gate) { } // 取消原任务的发布屏障。
                try { _stop.Cancel(); }
                catch (Exception error)
                {
                    MarkReleaseFailure();
                    throw new AggregateException("呈现器取消回调失败。", error);
                }
            });
        }

        List<Exception> errors = [];
        // bridge 只请求 Control/Video 两线独立关闭；其 parent join 仍属于外层。
        try { _bridge.RequestStop(); }
        catch (Exception error) { errors.Add(error); }
        try { if (remaining is not null) DisposeFrame(remaining, null); }
        catch (Exception error) { errors.Add(error); }

        // 不用取消令牌切断等待，也不把停止或 Abort 当成 UI 原任务已退出。
        Task joined = Task.WhenAll(Completion, cancellation);
        await joined.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (joined.Exception is { } aggregate) errors.AddRange(aggregate.InnerExceptions);
        try
        {
            _stop.Dispose();
            lock (Gate) _stopDisposed = true;
        }
        catch (Exception error) { MarkReleaseFailure(); errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("屏障呈现收尾失败。", errors);
    }

    internal Task<PresenterDrainEvidence> GetDisposalEvidenceAsync()
    {
        Task original = StopAndDisposeAsync();
        lock (Gate) return _disposalEvidence ??= ObserveDisposalAsync(original);
    }

    private async Task<PresenterDrainEvidence> ObserveDisposalAsync(Task original)
    {
        await original.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        lock (Gate)
        {
            bool finished = original.IsCompleted && Completion.IsCompleted &&
                _reader.IsCompleted && _decoder.IsCompleted && _renderer.IsCompleted &&
                _cancel is { IsCompleted: true };
            bool released = finished && _encoded is null && _decoded is null && _stopDisposed &&
                _cancel is { IsCompletedSuccessfully: true } && Volatile.Read(ref _releaseFailed) == 0;
            return new PresenterDrainEvidence(finished, released);
        }
    }

    private void DisposeFrame(PresentationFrame frame, Exception? previous)
    {
        try { frame.Frame.Dispose(); }
        catch (Exception error)
        {
            MarkReleaseFailure();
            throw previous is null
                ? new AggregateException("帧 owner 释放失败。", error)
                : new AggregateException("帧处理及 owner 释放失败。", previous, error);
        }
    }

    private void MarkReleaseFailure() => Interlocked.Exchange(ref _releaseFailed, 1);

    private void SignalUnderGate()
    {
        TaskCompletionSource previous = _changed;
        _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }

    private static Exception OriginalError(Task original, Exception caught)
    {
        AggregateException? container = original.Exception;
        // 只拆 Task 的单项包装；保留原异常身份、嵌套 Aggregate 和多错分支。
        return container is null ? caught : container.InnerExceptions.Count == 1 ? container.InnerExceptions[0] : container;
    }

    private sealed record DecodedFrame(FrameStamp Stamp, BitmapSource Bitmap);
}
