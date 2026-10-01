namespace LanRemote.Core.Abstractions;

/// <summary>创建尚未启动、拥有会话专用依赖的视频帧生产者。</summary>
/// <remarks>工厂本身为借用对象，不由生产者释放；认证门禁由未来的 Transport 层负责。</remarks>
public interface IVideoFrameProducerFactory
{
    /// <summary>创建成功后由调用方负责停止及释放；失败或取消须先完成已取得资源的清理。</summary>
    /// <param name="sessionId">非空会话关联标识，不是凭证，也不表示已经认证。</param>
    /// <param name="cancellationToken">仅用于创建；已进入的原操作仍须等待，成功交付后不再控制生产者。</param>
    ValueTask<IVideoFrameProducer> CreateAsync(Guid sessionId, CancellationToken cancellationToken);
}
