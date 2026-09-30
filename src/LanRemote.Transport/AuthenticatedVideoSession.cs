using LanRemote.Core.Models;

namespace LanRemote.Transport;

/// <summary>已通过第二条 TLS 身份和附着 ACK 校验的视频会话；不暴露连接或认证凭据。</summary>
/// <remarks>每次只能有一个读取。交付的帧归调用方所有，必须单独释放。</remarks>
public sealed class AuthenticatedVideoSession : IAsyncDisposable
{
    private readonly AuthenticatedControlSession.ClientVideoLifetime _lifetime;

    internal AuthenticatedVideoSession(AuthenticatedControlSession.ClientVideoLifetime lifetime) =>
        _lifetime = lifetime;

    /// <summary>读取下一帧；有序 EOF 返回 null。返回底座原任务，不对已提交的帧追加取消裁决。</summary>
    public Task<EncodedFrame?> ReadFrameAsync(CancellationToken cancellationToken = default) =>
        _lifetime.ReadFrameAsync(cancellationToken);

    /// <summary>
    /// 停止并排空视频原操作和资源，不关闭 Control。重复调用观察同一任务及结果。
    /// 所有已保存错误均在排空后以 AggregateException 报告，包括主动关闭期间的 I/O 错误。
    /// 不可从本会话跟踪的操作或回调内等待释放；已交付帧不由此方法回收。
    /// </summary>
    public ValueTask DisposeAsync() => new(_lifetime.GetPublicDisposeTask());
}
