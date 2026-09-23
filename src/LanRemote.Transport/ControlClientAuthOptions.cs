using LanRemote.Transport.Auth;

namespace LanRemote.Transport;

/// <summary>控制客户端的本地认证窗口；远端提示只能收窄机器窗口。</summary>
public sealed record ControlClientAuthOptions
{
    /// <summary>hello 写完后起算，覆盖完整 success 验证或第一次合法 pending 的接受。</summary>
    public TimeSpan MachineWindow { get; init; } =
        TimeSpan.FromMilliseconds(AuthProtocol.AuthenticationWindowMilliseconds);

    /// <summary>接受 pending 后起算的本地等待窗口，不代表远端审批受理时刻。</summary>
    public TimeSpan ApprovalWindow { get; init; } =
        TimeSpan.FromMilliseconds(AuthProtocol.ApprovalWindowMilliseconds);

    /// <summary>首个合法 pending 被接受后的一次非秘密通知；未验证 serverProof，不表示已认证。</summary>
    /// <remarks>
    /// 在认证执行线程同步调用，必须快速返回（只更新有界内存状态，不等待 Dispatcher、磁盘或用户）。
    /// 耗时计入原审批窗口，不续期；回调前后复核取消与单调截止。抛异常则以固定本地错误关闭连接，
    /// 不传播回调消息或 inner exception。普通取消无法硬中断永不返回的本机回调。
    /// 无 pending 的直接 success、错误帧及重复 pending 不触发新通知。
    /// </remarks>
    public Action<ControlClientApprovalPending>? ApprovalPending { get; init; }

    internal void Validate()
    {
        ValidateWindow(MachineWindow, nameof(MachineWindow));
        ValidateWindow(ApprovalWindow, nameof(ApprovalWindow));
    }

    private static void ValidateWindow(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero || value > TransportTimeouts.Maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName, value, "认证窗口必须为正数且不超过一天。");
        }
    }
}
