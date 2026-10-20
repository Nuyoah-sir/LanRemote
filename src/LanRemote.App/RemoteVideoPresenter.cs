using System.IO;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LanRemote.Core;
using LanRemote.Core.Models;
using LanRemote.Transport;

namespace LanRemote.App;

/// <summary>有界拥有型压缩帧队列、后台 JPEG 解码、UI 单槽合并呈现。</summary>
public sealed class RemoteVideoPresenter
{
    private readonly AuthenticatedVideoSession _video;
    private readonly Image _image;
    private readonly FencedVideoPresenter? _fenced;
    private readonly BoundedFrameQueue<EncodedFrame> _queue = new(1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly Task _reader;
    private readonly Task _decoder;
    private BitmapSource? _latest;
    private bool _dispatchQueued;
    private int _generation;
    private Task? _shutdown;
    private Task<PresenterDrainEvidence>? _disposalEvidence;
    private Task? _cancellation;
    private Exception? _readCancellation;
    private int _releaseFailed;
    private int _queueStopSucceeded;
    private int _stopDisposeSucceeded;

    public RemoteVideoPresenter(AuthenticatedVideoSession video, Image image)
    {
        _video = video ?? throw new ArgumentNullException(nameof(video));
        _image = image ?? throw new ArgumentNullException(nameof(image));
        _reader = Task.Run(ReadFramesAsync);
        _decoder = Task.Run(DecodeFramesAsync);
        Completion = Task.WhenAll(_reader, _decoder);
    }

    internal RemoteVideoPresenter(AuthenticatedVideoSession video, Image image, AuthenticatedControlSession control)
    {
        // 此路径不启动旧管线；连参数验证或绑定失败也不遗留其初始化资源。
        _queue.Dispose();
        _stop.Dispose();
        _video = video ?? throw new ArgumentNullException(nameof(video));
        _image = image ?? throw new ArgumentNullException(nameof(image));
        ArgumentNullException.ThrowIfNull(control);
        AuthenticatedControlSession.ClientDisplayPresentation binding = control.BindDisplayPresentation(video);
        _fenced = new(binding, image);
        _reader = _fenced.Completion;
        _decoder = Task.CompletedTask;
        Completion = _fenced.Completion;
    }

    /// <summary>所有原 worker 的联合完成；会话所有者必须等待。</summary>
    public Task Completion { get; }

    private async Task ReadFramesAsync()
    {
        Exception? readError = null;
        try
        {
            while (true)
            {
                EncodedFrame? frame;
                try { frame = await _video.ReadFrameAsync(_stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException error)
                {
                    // async Task 会把 OCE 变成 Canceled；另存原实例供 StopCore 报告。
                    Volatile.Write(ref _readCancellation, error);
                    throw;
                }
                if (frame is null) return;
                Exception? enqueueError = null;
                try
                {
                    bool accepted = _queue.TryWrite(frame, out Exception? disposalError);
                    if (accepted) frame = null;
                    if (disposalError is not null)
                    {
                        MarkReleaseFailure();
                        if (disposalError is OperationCanceledException)
                            throw new AggregateException("丢帧 owner 释放失败。", disposalError);
                        throw disposalError;
                    }
                    if (!accepted) return;
                }
                catch (Exception error)
                {
                    enqueueError = error;
                    throw;
                }
                finally
                {
                    if (frame is not null) DisposeFrame(frame, enqueueError);
                }
            }
        }
        catch (Exception error)
        {
            readError = error;
            try { _stop.Cancel(); }
            catch (Exception cancellationError)
            {
                MarkReleaseFailure();
                readError = new AggregateException("读取和取消回调均出现错误。", error, cancellationError);
                throw readError;
            }
            throw;
        }
        finally
        {
            try { _queue.Stop(); }
            catch (Exception queueError)
            {
                MarkReleaseFailure();
                if (readError is not null)
                    throw new AggregateException("读取和队列释放均出现错误。", readError, queueError);
                throw;
            }
        }
    }

    private async Task DecodeFramesAsync()
    {
        try
        {
            while (true)
            {
                EncodedFrame frame;
                try
                {
                    frame = await _queue.ReadAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    return; // reader 到 EOF，或本地停止后队列已经排空。
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    return;
                }

                Exception? decodeError = null;
                try
                {
                    BitmapSource bitmap = DecodeJpeg(frame);
                    PostLatest(bitmap);
                }
                catch (Exception error)
                {
                    decodeError = error;
                    throw;
                }
                finally
                {
                    DisposeFrame(frame, decodeError);
                }
            }
        }
        catch (Exception error)
        {
            try { _stop.Cancel(); } // 解码失败必须唤醒仍在等待网络帧的 reader。
            catch (Exception cancellationError)
            {
                MarkReleaseFailure();
                throw new AggregateException("解码和取消回调均出现错误。", error, cancellationError);
            }
            throw;
        }
    }

    private void DisposeFrame(EncodedFrame frame, Exception? previousError)
    {
        try { frame.Dispose(); }
        catch (Exception disposeError)
        {
            MarkReleaseFailure();
            if (previousError is not null)
                throw new AggregateException("帧处理和 owner 释放均出现错误。", previousError, disposeError);
            if (disposeError is OperationCanceledException)
                throw new AggregateException("帧 owner 释放失败。", disposeError);
            throw;
        }
    }

    private void MarkReleaseFailure() => Interlocked.Exchange(ref _releaseFailed, 1);

    internal static BitmapSource DecodeJpeg(EncodedFrame frame)
    {
        if (frame.Codec != VideoCodec.Jpeg || !DimensionsAllowed(frame.Width, frame.Height))
        {
            throw new InvalidDataException("远端视频帧类型或尺寸超出允许范围。");
        }

        // 唯一快照先经字节级边界与 SOF 预检，再交给 WPF 解码器。
        byte[] payload = frame.Payload.ToArray();
        ValidateBaselineJpeg(payload, frame.Width, frame.Height);

        using MemoryStream stream = new(payload, writable: false);
        BitmapImage bitmap = new();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        if (!DimensionsAllowed(bitmap.PixelWidth, bitmap.PixelHeight)
            || bitmap.PixelWidth != frame.Width || bitmap.PixelHeight != frame.Height)
            throw new InvalidDataException("远端 JPEG 解码尺寸与元数据不一致。");
        bitmap.Freeze();
        return bitmap;
    }

    private static void ValidateBaselineJpeg(byte[] jpeg, int expectedWidth, int expectedHeight)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            throw new InvalidDataException("远端视频不是 JPEG。");

        int offset = 2;
        bool sawFrame = false;
        bool sawScan = false;
        bool inScan = false;
        while (offset < jpeg.Length)
        {
            if (jpeg[offset++] != 0xFF)
            {
                if (inScan) continue;
                throw new InvalidDataException("远端 JPEG 标记或段长度非法。");
            }
            while (offset < jpeg.Length && jpeg[offset] == 0xFF) offset++;
            if (offset == jpeg.Length)
                throw new InvalidDataException("远端 JPEG 标记或段长度非法。");
            byte marker = jpeg[offset++];
            if (inScan && marker == 0x00) continue; // 熵编码中的 FF 00 是字节填充。
            if (inScan && marker is >= 0xD0 and <= 0xD7) continue;
            inScan = false;

            if (marker == 0xD9)
            {
                if (!sawScan || offset != jpeg.Length)
                    throw new InvalidDataException("远端 JPEG 扫描数据或结束标记非法。");
                return;
            }
            if (marker is 0x00 or 0x01 or 0xD8 || marker is >= 0xD0 and <= 0xD7)
                throw new InvalidDataException("远端 JPEG 标记不受支持。");
            if (jpeg.Length - offset < 2)
                throw new InvalidDataException("远端 JPEG 标记或段长度非法。");
            int segmentLength = (jpeg[offset] << 8) | jpeg[offset + 1];
            if (segmentLength < 2 || segmentLength > jpeg.Length - offset)
                throw new InvalidDataException("远端 JPEG 标记或段长度非法。");
            int data = offset + 2;

            switch (marker)
            {
                case 0xC0: // 只接受 8 位 baseline SOF0；不将其他 JPEG 变体送入 WPF。
                    if (sawFrame || sawScan || segmentLength < 8)
                        throw new InvalidDataException("远端 JPEG 帧头重复或顺序错误。");
                    int components = jpeg[data + 5];
                    if (jpeg[data] != 8 || components is < 1 or > 4 || segmentLength != 8 + 3 * components)
                        throw new InvalidDataException("远端 JPEG 帧头非法。");
                    int height = (jpeg[data + 1] << 8) | jpeg[data + 2];
                    int width = (jpeg[data + 3] << 8) | jpeg[data + 4];
                    if (!DimensionsAllowed(width, height) || width != expectedWidth || height != expectedHeight)
                        throw new InvalidDataException("远端 JPEG 实际尺寸与帧头不符或超出像素预算。");
                    sawFrame = true;
                    break;
                case 0xDA: // SOS 必须在唯一 SOF0 之后；允许 baseline 的多扫描结构。
                    if (!sawFrame || segmentLength < 6)
                        throw new InvalidDataException("远端 JPEG 扫描头顺序错误。");
                    int scanComponents = jpeg[data];
                    if (scanComponents is < 1 or > 4 || segmentLength != 6 + 2 * scanComponents)
                        throw new InvalidDataException("远端 JPEG 扫描头非法。");
                    sawScan = true;
                    inScan = true;
                    break;
                case 0xDD when segmentLength != 4:
                    throw new InvalidDataException("远端 JPEG 重启间隔段非法。");
                case 0xC4: // DHT
                case 0xDB: // DQT
                case 0xDD: // DRI
                case 0xFE: // COM
                case >= 0xE0 and <= 0xEF: // APPn
                    break;
                default: // 其他 SOF、算术/分层/差分 JPEG 与未知标记均不交给 WPF。
                    throw new InvalidDataException("远端 JPEG 格式不受支持。");
            }
            offset += segmentLength;
        }
        throw new InvalidDataException("远端 JPEG 缺少扫描数据或结束标记。");
    }

    private static bool DimensionsAllowed(int width, int height) =>
        width is > 0 and <= FrameLimits.MaxDimension
        && height is > 0 and <= FrameLimits.MaxDimension
        && (long)width * height <= FrameLimits.MaxDecodedPixels;

    private void PostLatest(BitmapSource bitmap)
    {
        int generation;
        lock (_gate)
        {
            if (_shutdown is not null) return;
            _latest = bitmap;
            if (_dispatchQueued) return;
            _dispatchQueued = true;
            generation = _generation;
        }

        try
        {
            _image.Dispatcher.BeginInvoke(new Action(() => RenderLatest(generation)), DispatcherPriority.Render);
        }
        catch (InvalidOperationException)
        {
            lock (_gate) _dispatchQueued = false;
            throw;
        }
    }

    private void RenderLatest(int generation)
    {
        lock (_gate)
        {
            if (_generation != generation || _shutdown is not null) return;
            BitmapSource? latest = _latest;
            _latest = null;
            _dispatchQueued = false;
            // 与代次终止互斥；不能在检查后、写入 Source 前让关窗赢得代次。
            if (latest is not null) _image.Source = latest;
        }
    }

    /// <summary>拒绝旧代次 UI 回调，并完整等待原 reader/decode；网络连接由 service 同时释放。</summary>
    public Task StopAndDisposeAsync()
    {
        if (_fenced is not null) return _fenced.StopAndDisposeAsync();
        lock (_gate)
        {
            if (_shutdown is not null) return _shutdown;
            ++_generation;
            _latest = null;
            // 不在 UI gate 内运行 CancelAsync 回调或释放帧；先发布稳定的原收尾任务。
            return _shutdown = Task.Run(StopCoreAsync);
        }
    }

    /// <summary>等待同一原收尾任务，再只依据独占根成功释放取得证据，不消费原异常。</summary>
    internal Task<PresenterDrainEvidence> GetDisposalEvidenceAsync()
    {
        if (_fenced is not null) return _fenced.GetDisposalEvidenceAsync();
        Task original = StopAndDisposeAsync();
        lock (_gate)
            return _disposalEvidence ??= ObserveDisposalAsync(original);
    }

    private async Task<PresenterDrainEvidence> ObserveDisposalAsync(Task original)
    {
        await original.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        lock (_gate)
        {
            bool finished = original.IsCompleted
                && _reader.IsCompleted && _decoder.IsCompleted && Completion.IsCompleted
                && _cancellation is { IsCompleted: true };
            bool released = finished
                && _cancellation is { IsCompletedSuccessfully: true }
                && Volatile.Read(ref _queueStopSucceeded) != 0
                && Volatile.Read(ref _stopDisposeSucceeded) != 0
                && Volatile.Read(ref _releaseFailed) == 0;
            return new PresenterDrainEvidence(finished, released);
        }
    }

    private async Task StopCoreAsync()
    {
        Task cancellation = _stop.CancelAsync();
        Volatile.Write(ref _cancellation, cancellation);
        Exception? queueError = null;
        try
        {
            _queue.Stop();
            Volatile.Write(ref _queueStopSucceeded, 1);
        }
        catch (Exception error)
        {
            MarkReleaseFailure();
            queueError = error;
        }

        Task joined = Task.WhenAll(cancellation, Completion);
        try { await joined.ConfigureAwait(false); }
        catch { /* 完整等待后从联合任务保留全部原始错误。 */ }
        Exception? disposeError = null;
        try
        {
            _stop.Dispose();
            Volatile.Write(ref _stopDisposeSucceeded, 1);
        }
        catch (Exception error)
        {
            MarkReleaseFailure();
            disposeError = error;
        }

        Exception? readCancellation = Volatile.Read(ref _readCancellation);
        if (queueError is not null || disposeError is not null || joined.IsFaulted || joined.IsCanceled
            || readCancellation is not null)
        {
            List<Exception> errors = [];
            if (queueError is not null) errors.Add(queueError);
            if (joined.Exception is { } aggregate) errors.AddRange(aggregate.InnerExceptions);
            if (readCancellation is not null && !errors.Any(error => ContainsException(error, readCancellation)))
                errors.Add(readCancellation);
            else if (joined.IsCanceled && readCancellation is null)
                errors.Add(new OperationCanceledException("帧处理收尾已取消。"));
            if (disposeError is not null) errors.Add(disposeError);
            throw new AggregateException("帧处理收尾出现错误。", errors);
        }
    }

    private static bool ContainsException(Exception root, Exception candidate) =>
        ReferenceEquals(root, candidate)
        || (root is AggregateException aggregate
            && aggregate.InnerExceptions.Any(error => ContainsException(error, candidate)))
        || (root.InnerException is { } inner && ContainsException(inner, candidate));
}

/// <summary>仅证明呈现器原 worker/取消回调已退出且其独占帧与取消源释放正常返回。</summary>
internal sealed class PresenterDrainEvidence(bool originalOperationsFinished, bool allOwnedRootsReleased)
{
    internal bool OriginalOperationsFinished { get; } = originalOperationsFinished;
    internal bool AllOwnedRootsReleased { get; } = allOwnedRootsReleased;
}
