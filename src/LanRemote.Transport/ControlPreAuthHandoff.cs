using System.Net.Security;

namespace LanRemote.Transport;

/// <summary>
/// pre-auth → auth 的<b>一次性交接对象</b>（ADR-037 第 2 条）：线性所有权 + exactly-once。
/// </summary>
/// <remarks>
/// <para><b>它防的两个具体失败模式</b>：①「pre-auth 会话已经交出去了，有人还拿着旧引用读流」
/// （两个读者同时消费一条流——ADR-038 的双 proof 设计对消费顺序极敏感）；
/// ②「同一连接开两次认证」（第二份 challenge/response 重放面）。</para>
/// <para><b>流所有权链</b>（ADR-037 第 6 条）：Host（socket 生到死）→ pre-auth 会话（读 hello 止）→
/// 本对象 → auth 会话（challenge/response/success 的读写 → 成功保持 / 失败关闭）。
/// 本对象与 auth 会话都<b>不</b> Dispose 流；最终释放永远在
/// <c>TransportHost.HandleAsync</c> 的 <c>finally</c>。</para>
/// <para>本对象不暴露 <c>SslStream</c>：拿不到流，就没有「两个读者」；要用流，
/// 只能通过一次性的 <see cref="BeginAuthentication"/> 把它交给认证会话。</para>
/// </remarks>
public sealed class ControlPreAuthHandoff
{
    /// <summary>
    /// <see cref="ControlPreAuthSession.AllowedOperationsWhilePreAuthenticated"/> 的 M4 项——
    /// 「开始访问密钥认证」这一项就是本类的 <see cref="BeginAuthentication"/>。
    /// </summary>
    public const string OperationBeginAuthentication = "begin-authentication";

    private readonly SslStream _stream;
    private int _begun;

    internal ControlPreAuthHandoff(SslStream stream, ConnectionSecurityContext security)
    {
        _stream = stream;
        Security = security;
    }

    /// <summary>冻结的安全上下文（ADR-037 第 3 条；认证 transcript 的唯一素材来源）。</summary>
    public ConnectionSecurityContext Security { get; }

    /// <summary>
    /// 开始认证：返回本连接<b>唯一</b>的 <see cref="ControlAuthSession"/>（每连接一个实例）。
    /// </summary>
    /// <param name="context">服务端认证素材与守卫（身份 / 密钥来源 / 限流 / 审批 / 登记表 / 参数）。</param>
    /// <param name="timeouts">时限预算（帧读写的段预算；机器窗口见 <see cref="ControlAuthOptions"/>）。</param>
    /// <returns>认证会话；其 <c>RunAsync</c> 同样只允许一次。</returns>
    /// <exception cref="InvalidOperationException">第二次调用（exactly-once；与 <c>PreAuthSession.RunAsync</c> 的每实例一次同风格）。</exception>
    public ControlAuthSession BeginAuthentication(ControlAuthContext context, TransportTimeouts timeouts)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(timeouts);

        if (Interlocked.Exchange(ref _begun, 1) != 0)
        {
            throw new InvalidOperationException(
                "同一连接的认证只允许开始一次；第二次 BeginAuthentication 属于协议违规面（ADR-037 第 2 条）。");
        }

        return new ControlAuthSession(_stream, Security, context, timeouts);
    }
}
