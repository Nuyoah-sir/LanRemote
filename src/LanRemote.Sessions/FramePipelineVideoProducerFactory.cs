using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;

namespace LanRemote.Sessions;

/// <summary>为每个会话创建使用唯一主显示器、尚未启动的帧管线生产者。</summary>
/// <remarks>
/// 两个委托必须返回当前会话专用的非空依赖，不得跨会话共享或由外部并发使用、释放。
/// 同一对象可以同时充当采集后端和编码器，只清理一次。委托及其目标、TimeProvider 和工厂本身均为借用，
/// 不由生产者释放。依赖及回调不得等待本生产者的 Completion、StopAsync 或 DisposeAsync，避免自 join。
/// 会话标识仅用于关联；本工厂不验证认证，未认证门禁属于未来的 Transport 层。
/// </remarks>
public sealed class FramePipelineVideoProducerFactory : IVideoFrameProducerFactory
{
    private readonly Func<IScreenCaptureBackend> _createCapture;
    private readonly Func<IFrameEncoder> _createEncoder;
    private readonly int _rawCapacity;
    private readonly int _encodedCapacity;
    private readonly TimeProvider? _timeProvider;
    private readonly VideoQualitySettings? _settings;
    private readonly Func<Guid, VideoQualitySettings?>? _sessionQuality;

    /// <summary>只验证并保存参数，不调用委托；队列容量均只能为 1 或 2。</summary>
    /// <param name="sessionQuality">
    /// 可选的按会话画质解析回调：返回非空时该会话使用返回值，返回 null 时回退 <paramref name="settings"/>；
    /// 回调在校验与任何依赖创建之前调用，抛出或返回非法档位都同步拒绝，不创建采集后端或编码器。
    /// </param>
    public FramePipelineVideoProducerFactory(
        Func<IScreenCaptureBackend> createCapture,
        Func<IFrameEncoder> createEncoder,
        int rawCapacity = 2,
        int encodedCapacity = 2,
        TimeProvider? timeProvider = null,
        VideoQualitySettings? settings = null,
        Func<Guid, VideoQualitySettings?>? sessionQuality = null)
    {
        ArgumentNullException.ThrowIfNull(createCapture);
        ArgumentNullException.ThrowIfNull(createEncoder);
        if (settings is not null) FramePipeline.ValidateSettings(settings);
        if (rawCapacity is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(rawCapacity), rawCapacity, "容量只能为 1 或 2。");
        }

        if (encodedCapacity is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(encodedCapacity), encodedCapacity, "容量只能为 1 或 2。");
        }

        _createCapture = createCapture;
        _createEncoder = createEncoder;
        _rawCapacity = rawCapacity;
        _encodedCapacity = encodedCapacity;
        _timeProvider = timeProvider;
        _settings = settings;
        _sessionQuality = sessionQuality;
    }

    /// <inheritdoc />
    public ValueTask<IVideoFrameProducer> CreateAsync(Guid sessionId, CancellationToken cancellationToken) =>
        ScheduleCreate(sessionId, null, ResolveSessionQuality(sessionId), cancellationToken);

    /// <summary>按该会话自己的画质准备尚未启动的生产者；不代表认证。</summary>
    /// <remarks>
    /// 画质只作用于本会话管线，既不改动工厂默认档位，也不影响其他会话已持有的设置；
    /// 非法档位在创建采集后端、编码器或原始帧之前同步拒绝，不产生需要清理的依赖。
    /// 与既有两个准备入口同名不同义会破坏其形态契约，因此按会话画质另立独立入口。
    /// </remarks>
    internal ValueTask<IVideoFrameProducer> CreateWithSessionQualityAsync(
        Guid sessionId, VideoQualitySettings quality, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(quality);
        return ScheduleCreate(sessionId, null, quality, cancellationToken);
    }

    /// <summary>为固定目标准备尚未启动的生产者；不代表认证或运行中切屏能力。</summary>
    internal ValueTask<IVideoFrameProducer> CreateForDisplayAsync(
        Guid sessionId, DisplayInfo expectedDisplay, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedDisplay);
        return ScheduleCreate(sessionId, expectedDisplay, ResolveSessionQuality(sessionId), cancellationToken);
    }

    /// <summary>按该会话自己的画质为固定目标准备尚未启动的生产者；不代表认证或运行中切屏能力。</summary>
    /// <remarks>画质与目标同属本会话的一次准备：非法档位同样在任何依赖创建之前同步拒绝。</remarks>
    internal ValueTask<IVideoFrameProducer> CreateForDisplayWithSessionQualityAsync(
        Guid sessionId, DisplayInfo expectedDisplay, VideoQualitySettings quality, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedDisplay);
        ArgumentNullException.ThrowIfNull(quality);
        return ScheduleCreate(sessionId, expectedDisplay, quality, cancellationToken);
    }

    // 按会话解析先于空会话标识之外的任何工作：回调返回 null 表示本会话没有独立档位、回退工厂默认。
    private VideoQualitySettings? ResolveSessionQuality(Guid sessionId) => _sessionQuality?.Invoke(sessionId);

    private ValueTask<IVideoFrameProducer> ScheduleCreate(
        Guid sessionId, DisplayInfo? expectedDisplay, VideoQualitySettings? quality, CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("会话关联标识不能为空。", nameof(sessionId));
        }

        // 会话画质在任何委托、显示器枚举或像素预算之前校验：非法档位不得创建采集后端或编码器。
        if (quality is not null) FramePipeline.ValidateSettings(quality);

        // 包括委托和显示器枚举的全部同步前缀都在后台；不传调度取消，也不以取消代理替代原任务。
        return new ValueTask<IVideoFrameProducer>(Task.Run(() => CreateCoreAsync(sessionId, expectedDisplay, quality, cancellationToken)));
    }

    private async Task<IVideoFrameProducer> CreateCoreAsync(
        Guid sessionId, DisplayInfo? expectedDisplay, VideoQualitySettings? quality, CancellationToken cancellationToken)
    {
        IScreenCaptureBackend? capture = null;
        IFrameEncoder? encoder = null;
        FramePipeline? pipeline = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            capture = _createCapture() ?? throw new InvalidOperationException("采集后端工厂返回了 null。");
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<DisplayInfo> displays = capture.GetDisplays()
                ?? throw new InvalidOperationException("采集后端返回了 null 显示器列表。");
            cancellationToken.ThrowIfCancellationRequested();
            DisplayInfo selected = expectedDisplay is null
                ? displays.Single(display => display.IsPrimary)
                : SelectExpectedDisplay(displays, expectedDisplay);
            cancellationToken.ThrowIfCancellationRequested();
            if (selected.Width is < 1 or > FrameLimits.MaxDimension ||
                selected.Height is < 1 or > FrameLimits.MaxDimension ||
                (long)selected.Width * selected.Height > FrameLimits.MaxDecodedPixels)
            {
                throw new ArgumentOutOfRangeException(nameof(selected),
                    "目标显示器超过M5单帧像素预算；不创建编码器或采集大型原始帧。");
            }

            encoder = _createEncoder() ?? throw new InvalidOperationException("编码器工厂返回了 null。");
            cancellationToken.ThrowIfCancellationRequested();
            pipeline = new FramePipeline(capture, encoder, selected.Id, _rawCapacity, _encodedCapacity, _timeProvider,
                quality ?? _settings);
            cancellationToken.ThrowIfCancellationRequested();

            // 最后一次取消检查前一直由局部变量拥有；成功构造 adapter 后一次性交付，不再后置取消。
            // 目标随生产者一并交付：后续自动画质换代必须沿用当前显示器，不能悄悄回到主屏。
            var producer = new FramePipelineVideoProducer(sessionId, pipeline, capture, encoder) { Display = selected };
            pipeline = null;
            capture = null;
            encoder = null;
            return producer;
        }
        catch (Exception exception)
        {
            List<Exception> cleanupErrors = [];
            if (pipeline is not null)
            {
                await FramePipelineVideoProducerCleanup.ObserveAsync(pipeline.StopAsync(), cleanupErrors).ConfigureAwait(false);
            }

            await FramePipelineVideoProducerCleanup.DisposeDependenciesAsync(capture, encoder, cleanupErrors).ConfigureAwait(false);
            if (cleanupErrors.Count == 0)
            {
                // 原异常及堆栈不变；即使创建取消，也已等待迟到委托并清理其返回的依赖。
                throw;
            }

            cleanupErrors.Insert(0, exception);
            throw new AggregateException($"创建会话 {sessionId} 的视频生产者失败，且清理发生错误。", cleanupErrors);
        }
    }

    private static DisplayInfo SelectExpectedDisplay(IReadOnlyList<DisplayInfo> displays, DisplayInfo expectedDisplay)
    {
        DisplayInfo? selected = null;
        bool duplicate = false;
        foreach (DisplayInfo display in displays)
        {
            if (display.Id != expectedDisplay.Id) continue;
            if (selected is not null) duplicate = true;
            else selected = display;
        }

        if (selected is null) throw new InvalidOperationException("未找到目标显示器。");
        if (duplicate) throw new InvalidOperationException("目标显示器标识存在重复。");
        // 仅核对本次枚举与期望元数据；不保证跨调用硬件身份或拓扑世代未变化。
        if (selected != expectedDisplay) throw new InvalidOperationException("目标显示器元数据已变化。");
        return selected;
    }
}
