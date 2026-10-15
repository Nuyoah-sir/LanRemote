namespace LanRemote.Transport;

/// <summary>已消费的视频资格；通知撤销及子连接收尾，不拥有流，也不能释放或恢复资格。</summary>
internal sealed class VideoAttachLease
{
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate;

    internal VideoAttachLease(Guid sessionId, Guid videoConnectionId, Task revoked, object? gate = null)
    {
        _gate = gate ?? new object();
        SessionId = sessionId;
        VideoConnectionId = videoConnectionId;
        Revoked = revoked;
    }

    internal Guid SessionId { get; }
    internal Guid VideoConnectionId { get; }
    internal Task Revoked { get; }
    internal Task Completed => _completed.Task;

    internal void Complete()
    {
        // 真实租约与父登记表共用 gate：完成与换代接纳不能各自通过末检。
        lock (_gate) _completed.TrySetResult();
    }
}
