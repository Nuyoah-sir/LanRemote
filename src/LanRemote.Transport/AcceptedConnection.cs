using System.Net;
using System.Net.Security;
using System.Security.Authentication;

namespace LanRemote.Transport;

/// <summary>
/// Host 侧一条已通过「同子网 + 准入」并完成 TLS 握手的连接（ADR-037：安全上下文 + 流）。
/// </summary>
/// <param name="Security">本连接的安全事实快照（<see cref="ConnectionSecurityContext"/>）；冻结，不再变化。</param>
/// <param name="Stream">已认证的 TLS 流。</param>
/// <remarks>
/// <para><b>所有权在 Host</b>：<paramref name="Stream"/> 与底层 socket 由 Host 的连接处理路径释放，
/// 会话处理器（以及 pre-auth → auth 的交接链）都<b>不要</b>自己 <c>Dispose</c>
/// ——ADR-037 第 6 条：所有权转移不改变「Host 是 socket 最终拥有者」这一 M2 起的事实。</para>
/// <para>地址 / 端口 / TLS 版本经转发属性继续可用（既有消费点不破）；
/// 新代码应直接用 <see cref="Security"/>——它是认证 transcript 的唯一素材来源。</para>
/// <para>此刻的状态是「TLS 完成、还没认证」；进入会话层后由
/// <see cref="ControlPreAuthSession"/> 推进到 <see cref="ControlSessionState.PreAuthenticated"/>，
/// 在此之前不得凭它做任何授权判断。</para>
/// </remarks>
public sealed record AcceptedConnection(
    ConnectionSecurityContext Security,
    SslStream Stream)
{
    /// <summary>接受它的那个 listener 的本地地址（转发自 <see cref="Security"/>）。</summary>
    public IPAddress LocalAddress => Security.LocalAddress;

    /// <summary>对端 IPv4 地址（转发自 <see cref="Security"/>）。</summary>
    public IPAddress RemoteAddress => Security.RemoteAddress;

    /// <summary>对端端口（转发自 <see cref="Security"/>）。</summary>
    public int RemotePort => Security.RemotePort;

    /// <summary>协商出的 TLS 版本（转发自 <see cref="Security"/>）。</summary>
    public SslProtocols NegotiatedProtocol => Security.NegotiatedProtocol;
}
