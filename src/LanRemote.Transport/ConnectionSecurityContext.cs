using System.Net;
using System.Security.Authentication;

namespace LanRemote.Transport;

/// <summary>
/// 一条连接的<b>安全事实快照</b>（ADR-037 第 3 条）：接受连接后即构造，此后不可变。
/// </summary>
/// <remarks>
/// <para><b>为什么冻结而不是「需要时去查」</b>：认证期间<b>禁止</b>重读 discovery 缓存 /
/// config / 实时证书状态（评审 1.8）——「认证中的值」与「握手时的值」一旦允许不同，
/// 凭据中继类缺口就有了藏身处。需要哪个值，就在交接里带哪个值。</para>
/// <para><see cref="ServerCertificateSha256"/> 必须来自<b>本连接实际出示</b>的服务端证书
/// （transport 在 TLS 完成时算定）；challenge 的 <c>certSha256</c> 必须从它派生——
/// 保证「challenge 声称的指纹 = 本连接实际出示的证书」（评审 A11）。</para>
/// <para>不保存 <see cref="IPEndPoint"/> 实例：<c>IPEndPoint.Port</c> 可写，
/// 共享出去等于把可变状态交给调用方（与 <see cref="ConnectionIdentity"/> 同一约定）。</para>
/// </remarks>
public sealed class ConnectionSecurityContext
{
    private readonly byte[] _serverCertificateSha256;

    /// <summary>
    /// 构造快照。<see cref="ConnectionId"/> 由本类型生成（每连接一个，日志/会话关联用）。
    /// </summary>
    /// <param name="localAddress">接受它的那个 listener 的本地地址。</param>
    /// <param name="remoteAddress">对端 IPv4 地址。</param>
    /// <param name="remotePort">对端端口。</param>
    /// <param name="negotiatedProtocol">协商出的 TLS 版本。</param>
    /// <param name="serverCertificateSha256">本连接出示证书 DER 的 SHA-256（恰好 32 字节）。</param>
    /// <exception cref="ArgumentException">摘要长度不是 32 字节。</exception>
    public ConnectionSecurityContext(
        IPAddress localAddress,
        IPAddress remoteAddress,
        int remotePort,
        SslProtocols negotiatedProtocol,
        ReadOnlySpan<byte> serverCertificateSha256)
    {
        ArgumentNullException.ThrowIfNull(localAddress);
        ArgumentNullException.ThrowIfNull(remoteAddress);

        if (serverCertificateSha256.Length != CertificatePin.LengthBytes)
        {
            throw new ArgumentException(
                $"证书摘要必须恰好 {CertificatePin.LengthBytes} 字节。",
                nameof(serverCertificateSha256));
        }

        ConnectionId = Guid.NewGuid();
        LocalAddress = localAddress;
        RemoteAddress = remoteAddress;
        RemotePort = remotePort;
        NegotiatedProtocol = negotiatedProtocol;
        _serverCertificateSha256 = serverCertificateSha256.ToArray();
    }

    /// <summary>每连接唯一的关联 id（不是秘密；供日志与会话关联）。</summary>
    public Guid ConnectionId { get; }

    /// <summary>接受本连接的 listener 的本地地址。</summary>
    public IPAddress LocalAddress { get; }

    /// <summary>对端 IPv4 地址。</summary>
    public IPAddress RemoteAddress { get; }

    /// <summary>对端 TCP 端口。</summary>
    public int RemotePort { get; }

    /// <summary>协商出的 TLS 版本。</summary>
    public SslProtocols NegotiatedProtocol { get; }

    /// <summary>本连接出示证书 DER 的 SHA-256（32 字节；防御性拷贝，只读视图）。</summary>
    public ReadOnlyMemory<byte> ServerCertificateSha256 => _serverCertificateSha256;

    /// <summary>对端端点；每次调用返回新对象，不共享可变状态。</summary>
    public IPEndPoint RemoteEndPoint => new(RemoteAddress, RemotePort);
}
