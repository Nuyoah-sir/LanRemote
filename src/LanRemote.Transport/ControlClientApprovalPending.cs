namespace LanRemote.Transport;

/// <summary>
/// 首次接受合法 pending 的不可变、非秘密快照。仅用于显示等待状态与人工核对短码；
/// 此时尚未验证 serverProof，不能据此发送输入、标为远端身份已验证或授予权限。
/// </summary>
/// <param name="SessionId">认证会话关联 ID，不是远端审批 RequestId。</param>
/// <param name="ShortCode">六位大写 HEX 人工关联短码，可能碰撞，不是凭据。</param>
/// <param name="AcceptedAtTimestamp">接受 pending 时使用连接 TimeProvider 的单调时间戳；不是 UTC。</param>
/// <param name="ApprovalWindow">原始本地审批预算；显示排队不重置此窗口，最终截止仍由认证状态机裁决。</param>
public sealed record ControlClientApprovalPending(
    Guid SessionId,
    string ShortCode,
    long AcceptedAtTimestamp,
    TimeSpan ApprovalWindow);
