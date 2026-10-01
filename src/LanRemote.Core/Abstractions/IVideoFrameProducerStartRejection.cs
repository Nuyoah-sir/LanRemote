namespace LanRemote.Core.Abstractions;

/// <summary>可选的启动拒绝证明；仅识别停止先于启动的原始异常实例。</summary>
public interface IVideoFrameProducerStartRejection
{
    /// <summary>仅当此异常来自生产者自身的停止抢先启动时为 true；不表示停止或清理成功。</summary>
    bool IsStopBeforeStartRejection(InvalidOperationException error);
}
