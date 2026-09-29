namespace LanRemote.Transport;

/// <summary>已消费的视频资格；只通知撤销，不拥有流，也不能释放或恢复资格。</summary>
internal sealed class VideoAttachLease
{
    internal VideoAttachLease(Guid sessionId, Guid videoConnectionId, Task revoked)
    {
        SessionId = sessionId;
        VideoConnectionId = videoConnectionId;
        Revoked = revoked;
    }

    internal Guid SessionId { get; }
    internal Guid VideoConnectionId { get; }
    internal Task Revoked { get; }
}
