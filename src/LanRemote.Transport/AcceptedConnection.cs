using System.Net;
using System.Net.Security;
using System.Security.Authentication;

namespace LanRemote.Transport;

/// <summary>
/// Host 侧一条已通过「同子网 + 准入」并完成 TLS 握手的连接。
/// </summary>
/// <param name="LocalAddress">接受它的那个 listener 的本地地址（决定用哪个子网掩码校验）。</param>
/// <param name="RemoteAddress">对端 IPv4 地址。</param>
/// <param name="RemotePort">对端端口。</param>
/// <param name="Stream">已认证的 TLS 流。</param>
/// <param name="NegotiatedProtocol">协商出的 TLS 版本。</param>
/// <remarks>
/// <para><b>所有权在 Host</b>：<paramref name="Stream"/> 与底层 socket 由 Host 的连接处理路径释放，
/// 会话处理器不要自己 <c>Dispose</c>（重复释放是安全的，但没必要）。</para>
/// <para>此刻的状态是「TLS 完成、还没认证」。M3 阶段 4 会把它推进到显式的
/// <c>PreAuthenticated</c>，在此之前不得凭它做任何授权判断。</para>
/// </remarks>
public sealed record AcceptedConnection(
    IPAddress LocalAddress,
    IPAddress RemoteAddress,
    int RemotePort,
    SslStream Stream,
    SslProtocols NegotiatedProtocol);
