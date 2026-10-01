using LanRemote.Core.Models;

namespace LanRemote.Core.Abstractions;

/// <summary>单会话、单次启动的视频帧生产者，拥有其专用生产依赖。</summary>
/// <remarks>
/// DisposeAsync 与 StopAsync 共用同一个完整停止任务。依赖及其回调不得调用并等待本生产者的
/// Completion、StopAsync 或 DisposeAsync（包括同步等待），否则会自 join。
/// </remarks>
public interface IVideoFrameProducer : IAsyncDisposable
{
    /// <summary>稳定的完整停止任务，包含生产退出及所有专用依赖清理；失败保留完整聚合错误树。</summary>
    Task Completion { get; }

    /// <summary>仅启动一次；重复启动或停止后启动均被拒绝。</summary>
    void Start();

    /// <summary>
    /// 单消费者读取，拒绝重叠的队列读取。成功取得帧即交由调用方释放，不做后置取消检查。
    /// EOF 或生产故障须等待完整停止，随后返回 null 或抛出完整错误。
    /// </summary>
    /// <param name="cancellationToken">仅取消本次队列读取，不停止生产；进入收尾等待后不再响应此取消。</param>
    ValueTask<EncodedFrame?> ReadNextAsync(CancellationToken cancellationToken = default);

    /// <summary>请求永久停止，始终返回 Completion；允许启动前停止。</summary>
    Task StopAsync();
}
