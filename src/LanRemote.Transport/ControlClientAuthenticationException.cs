using System.Security.Authentication;

namespace LanRemote.Transport;

/// <summary>控制客户端的本地认证拒绝；不携带远端载荷、密钥、proof 或 token。</summary>
public sealed class ControlClientAuthenticationException : AuthenticationException
{
    /// <summary>serverProof 不符时固定使用的展示文案。</summary>
    public const string ServerProofFailureMessage =
        "远端身份验证失败，可能是错误密码或伪造设备广播";

    /// <summary>用本地拒绝短码和安全展示文案构造异常；两者均不得包含秘密或远端载荷。</summary>
    public ControlClientAuthenticationException(string rejection, string displayMessage)
        : base(displayMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rejection);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayMessage);
        Rejection = rejection;
        DisplayMessage = displayMessage;
    }

    /// <summary>仅供本地诊断的稳定短码，绝不发送给对端。</summary>
    public string Rejection { get; }

    /// <summary>可以交给 UI 的文案，与 <see cref="Exception.Message"/> 一致。</summary>
    public string DisplayMessage { get; }
}
