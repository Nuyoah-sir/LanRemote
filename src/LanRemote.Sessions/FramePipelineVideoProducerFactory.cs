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

    /// <summary>只验证并保存参数，不调用委托；队列容量均只能为 1 或 2。</summary>
    public FramePipelineVideoProducerFactory(
        Func<IScreenCaptureBackend> createCapture,
        Func<IFrameEncoder> createEncoder,
        int rawCapacity = 2,
        int encodedCapacity = 2,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(createCapture);
        ArgumentNullException.ThrowIfNull(createEncoder);
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
    }

    /// <inheritdoc />
    public ValueTask<IVideoFrameProducer> CreateAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("会话关联标识不能为空。", nameof(sessionId));
        }

        // 包括委托和显示器枚举的全部同步前缀都在后台；不传调度取消，也不以取消代理替代原任务。
        return new ValueTask<IVideoFrameProducer>(Task.Run(() => CreateCoreAsync(sessionId, cancellationToken)));
    }

    private async Task<IVideoFrameProducer> CreateCoreAsync(Guid sessionId, CancellationToken cancellationToken)
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
            DisplayInfo primary = displays.Single(display => display.IsPrimary);
            cancellationToken.ThrowIfCancellationRequested();

            encoder = _createEncoder() ?? throw new InvalidOperationException("编码器工厂返回了 null。");
            cancellationToken.ThrowIfCancellationRequested();
            pipeline = new FramePipeline(capture, encoder, primary.Id, _rawCapacity, _encodedCapacity, _timeProvider);
            cancellationToken.ThrowIfCancellationRequested();

            // 最后一次取消检查前一直由局部变量拥有；成功构造 adapter 后一次性交付，不再后置取消。
            var producer = new FramePipelineVideoProducer(sessionId, pipeline, capture, encoder);
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
}
