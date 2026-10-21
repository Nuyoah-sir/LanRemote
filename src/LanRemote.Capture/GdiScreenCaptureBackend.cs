using System.Diagnostics;
using System.Runtime.ExceptionServices;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;

namespace LanRemote.Capture;

/// <summary>默认使用 GDI 采集当前主显示设备的物理像素；多屏采集仅由内部工厂显式启用。</summary>
/// <remarks>
/// 实例同一时刻只接受一次采集；忙时拒绝而不排队。取消不会提前结束仍在执行的 native 调用。
/// 多屏路径核对采前采后完整稳定观察，不承诺原子快照、检测 ABA 或未来设备身份。
/// 负 X/Y 仅作为元数据；源 DC 使用所选设备名称，BitBlt 使用设备本地坐标 (0,0)。
/// 不改变进程 DPI awareness、网络、权限或桌面；此实现不代表已验证混合 DPI 环境。
/// </remarks>
public sealed class GdiScreenCaptureBackend : IScreenCaptureBackend
{
    private readonly IGdiNative _native;
    private readonly GdiDisplayCatalog? _multiCatalog;
    private readonly object _captureGate = new();
    private Task<CapturedFrame>? _operation;
    private Task? _originalWorker;

    // 只读观察最近发布的原 Task.Run 任务；发布可能早于 await 登记，不能单凭此属性证明等待关系。
    internal Task? OriginalWorker => Volatile.Read(ref _originalWorker);

    /// <summary>创建仅采集当前主显示器的 GDI 后端。</summary>
    public GdiScreenCaptureBackend() : this(new GdiNative())
    {
    }

    internal GdiScreenCaptureBackend(IGdiNative native) : this(native, false)
    {
    }

    private GdiScreenCaptureBackend(IGdiNative native, bool multi)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
        _multiCatalog = multi ? new GdiDisplayCatalog(native) : null;
    }

    /// <summary>内部显式创建多屏 GDI 后端；构造不枚举设备或执行 native I/O。</summary>
    internal static GdiScreenCaptureBackend CreateMultiDisplay() => new(new GdiNative(), true);

    /// <summary>内部显式创建使用指定 native 的多屏后端；构造仅检查空值并保存依赖。</summary>
    internal static GdiScreenCaptureBackend CreateMultiDisplay(IGdiNative native) => new(native, true);

    /// <summary>默认返回当前主显示器；内部显式多屏实例返回稳定观察到的附着非镜像设备。</summary>
    /// <remarks>标识取实际 EnumDisplayDevicesW 设备枚举索引，不把过滤后的列表位置当成设备标识。</remarks>
    /// <returns>默认仅包含主屏；多屏列表只读，列出的设备仍须在采集时检查像素预算。</returns>
    public IReadOnlyList<DisplayInfo> GetDisplays() => _multiCatalog?.GetDisplays() ?? new[] { ReadPrimaryDisplay().Info };

    /// <summary>在线程池中采集一帧 top-down BGRA32，alpha 固定为 255。</summary>
    /// <param name="displayId">默认仅接受当前主屏索引；内部多屏实例接受本次稳定观察中的设备原始枚举索引。</param>
    /// <param name="cancellationToken">取消令牌；操作结束及资源清理后才报告取消。</param>
    /// <returns>调用方负责释放的帧。</returns>
    /// <exception cref="NotSupportedException">默认路径没有匹配的主屏，或多屏目标缺失、镜像、脱离；不回退其他设备。</exception>
    /// <exception cref="InvalidOperationException">实例正忙、显示配置变化或 GDI 清理失败。</exception>
    /// <exception cref="AggregateException">原始操作和清理、或多个清理步骤同时失败。</exception>
    public ValueTask<CapturedFrame> CaptureAsync(DisplayId displayId, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<CapturedFrame>(cancellationToken);
        }

        // 只尝试取得发布锁，不排队。单飞资格由公开 operation 的完成状态决定，
        // 不在 worker 的 finally 或 operation 的 SetResult/SetException 之前提前释放。
        if (!Monitor.TryEnter(_captureGate))
        {
            return ValueTask.FromException<CapturedFrame>(new InvalidOperationException("此 GDI 实例已有采集正在执行，不接受排队。"));
        }

        try
        {
            if (_operation is { IsCompleted: false })
            {
                return ValueTask.FromException<CapturedFrame>(new InvalidOperationException("此 GDI 实例已有采集正在执行，不接受排队。"));
            }

            Volatile.Write(ref _originalWorker, null);
            _operation = CaptureOperationAsync(displayId, cancellationToken);
            return new ValueTask<CapturedFrame>(_operation);
        }
        finally
        {
            Monitor.Exit(_captureGate);
        }
    }

    private async Task<CapturedFrame> CaptureOperationAsync(DisplayId displayId, CancellationToken cancellationToken)
    {
        // 不给 Task.Run 传取消令牌，不使用 WaitAsync 或 TCS 替代原任务。
        // 等待原核心完成清理后再传播取消，async builder 将其表示为 Canceled。
        Task<CapturedFrame> worker = Task.Run(() => CaptureCore(displayId, cancellationToken));
        Volatile.Write(ref _originalWorker, worker);
        return await worker.ConfigureAwait(false);
    }

    private CapturedFrame CaptureCore(DisplayId displayId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<GdiDisplayCatalog.DisplayObservation>? snapshot = _multiCatalog?.ReadStableSnapshot();
        GdiPrimaryDisplay display;
        if (snapshot is null)
        {
            display = ReadPrimaryDisplay();
            if (display.Info.Id != displayId)
            {
                throw new NotSupportedException("GDI 后端只支持当前主显示设备的标识。");
            }
        }
        else
        {
            GdiDisplayCatalog.DisplayObservation observation = snapshot.SingleOrDefault(item => item.Info.Id == displayId)
                ?? throw new NotSupportedException("指定标识不是当前桌面的附着非镜像显示设备；不回退其他设备。");
            if (observation.Info.Width is < 1 or > FrameLimits.MaxDimension ||
                observation.Info.Height is < 1 or > FrameLimits.MaxDimension ||
                (long)observation.Info.Width * observation.Info.Height > FrameLimits.MaxDecodedPixels)
            {
                throw new ArgumentOutOfRangeException(nameof(displayId),
                    "所选屏幕尺寸超过M5宽高或像素预算；未分配GDI像素，不静默缩小实屏。");
            }

            display = new GdiPrimaryDisplay(observation.Info, observation.DeviceName, observation.DeviceId,
                observation.DeviceKey, observation.Orientation, observation.FixedOutput, observation.BitsPerPixel,
                observation.Frequency, observation.Flags);
        }

        int stride = checked(display.Info.Width * 4);
        int length = checked(stride * display.Info.Height);
        nint source = 0;
        nint memory = 0;
        nint bitmap = 0;
        nint previous = 0;
        bool selected = false;
        PooledByteOwner? owner = null;
        long timestampUs = 0;
        var errors = new List<Exception>();

        try
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                source = _native.CreateSourceDc(display.DeviceName);
                memory = _native.CreateMemoryDc(source);
                GdiBitmapInfo info = GdiBitmapInfo.Create(display.Info.Width, display.Info.Height, length);
                bitmap = _native.CreateDibSection(memory, ref info, out nint bits);
                if (bits == 0)
                {
                    throw new InvalidOperationException("CreateDIBSection 没有返回像素地址。");
                }

                previous = _native.SelectObject(memory, bitmap);
                selected = true;
                cancellationToken.ThrowIfCancellationRequested();
                _native.BitBlt(memory, display.Info.Width, display.Info.Height, source, GdiNative.CopyOperation);
                _native.Flush();
                timestampUs = Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp()).Ticks / 10;
                cancellationToken.ThrowIfCancellationRequested();
                owner = PooledByteOwner.Rent(length);
                _native.CopyPixels(bits, owner.Array, length);
                Span<byte> pixels = owner.Memory.Span;
                for (int alpha = 3; alpha < length; alpha += 4)
                {
                    pixels[alpha] = 255;
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
            finally
            {
                bool mayDeleteBitmap = true;
                if (selected && !Cleanup(() => _native.SelectObject(memory, previous), "恢复借用的旧选入对象", errors))
                {
                    // 恢复失败时只能先删除 DC 解除选择。每一步至多一次，不删除可能仍选入的位图。
                    mayDeleteBitmap = Cleanup(() => _native.DeleteDc(memory),
                        "恢复失败后删除内存 DC（失败则保留可能仍选入的位图）", errors);
                    memory = 0;
                }

                if (bitmap != 0 && mayDeleteBitmap)
                {
                    Cleanup(() => _native.DeleteObject(bitmap), "删除 DIB 位图", errors);
                }

                if (memory != 0)
                {
                    Cleanup(() => _native.DeleteDc(memory), "删除内存 DC", errors);
                }

                if (source != 0)
                {
                    Cleanup(() => _native.DeleteDc(source), "删除源 DC", errors);
                }
            }

            if (errors.Count == 1)
            {
                ExceptionDispatchInfo.Capture(errors[0]).Throw();
            }

            if (errors.Count > 1)
            {
                throw new AggregateException("GDI 采集或清理失败；未交付帧。", errors);
            }

            cancellationToken.ThrowIfCancellationRequested();
            // 成功交付前 native 资源必须全部清理，并重新核对设备身份和物理几何。
            if (_multiCatalog is null)
            {
                if (display != ReadPrimaryDisplay())
                {
                    throw new InvalidOperationException("采集期间主显示设备或物理模式发生变化；未交付帧。");
                }
            }
            else if (!snapshot!.SequenceEqual(_multiCatalog.ReadStableSnapshot()))
            {
                throw new InvalidOperationException("采集期间附着显示设备或物理模式发生变化；未交付帧。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var frame = new CapturedFrame(display.Info.Id, display.Info.Width, display.Info.Height,
                stride, FramePixelFormat.Bgra32, owner!, timestampUs);
            owner = null;
            return frame;
        }
        finally
        {
            owner?.Dispose();
        }
    }

    private GdiPrimaryDisplay ReadPrimaryDisplay()
    {
        for (uint index = 0; _native.EnumDisplayDevices(index, out GdiDisplayDevice device); index = checked(index + 1))
        {
            const uint requiredFlags = GdiNative.AttachedToDesktop | GdiNative.PrimaryDevice;
            if ((device.StateFlags & requiredFlags) != requiredFlags)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(device.DeviceName))
            {
                throw new InvalidOperationException("主显示设备名称为空。");
            }

            GdiDevMode mode = _native.GetDisplayMode(device.DeviceName);
            if (mode.PelsWidth is < 1 or > 8192 || mode.PelsHeight is < 1 or > 8192 ||
                (long)mode.PelsWidth * mode.PelsHeight > FrameLimits.MaxDecodedPixels)
            {
                throw new ArgumentOutOfRangeException(nameof(mode),
                    "主屏尺寸超过M5宽高或像素预算；未分配GDI像素，不静默缩小实屏。");
            }

            var info = new DisplayInfo(new DisplayId(checked((int)index)),
                string.IsNullOrWhiteSpace(device.DeviceString) ? device.DeviceName : device.DeviceString,
                mode.PositionX, mode.PositionY, checked((int)mode.PelsWidth), checked((int)mode.PelsHeight), true);
            return new GdiPrimaryDisplay(info, device.DeviceName, device.DeviceId, device.DeviceKey,
                mode.DisplayOrientation, mode.DisplayFixedOutput, mode.BitsPerPel,
                mode.DisplayFrequency, mode.DisplayFlags);
        }

        throw new NotSupportedException("没有附着到当前桌面的主显示设备。");
    }

    private static bool Cleanup(Action action, string operation, List<Exception> errors)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception error)
        {
            errors.Add(new InvalidOperationException($"GDI 清理失败：{operation}。", error));
            return false;
        }
    }

    private sealed record GdiPrimaryDisplay(DisplayInfo Info, string DeviceName, string DeviceId,
        string DeviceKey, uint Orientation, uint FixedOutput, uint BitsPerPixel, uint Frequency, uint Flags);
}
