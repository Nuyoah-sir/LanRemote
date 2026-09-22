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
