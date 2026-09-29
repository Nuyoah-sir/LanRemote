using LanRemote.Core.Models;

namespace LanRemote.Transport;

/// <summary>按会话提供帧；同一来源可由多个会话并发调用，不接触传输流或认证凭据。</summary>
internal interface IVideoFrameSource
{
    /// <summary>
    /// null 表示该会话 EOF；非 null 将帧所有权移交调用方，包括取消后才返回的帧。
    /// 同步前缀和异步操作均须由上层路由监督并等待完成，不得超时丢弃取帧任务。
    /// </summary>
    ValueTask<EncodedFrame?> ReadNextAsync(Guid sessionId, CancellationToken cancellationToken);
}
