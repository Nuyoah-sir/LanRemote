using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace LanRemote.Transport;

/// <summary>
/// 一条已建立 TLS 的连接：身份上下文 + 可用的 <see cref="SslStream"/>。
/// </summary>
/// <remarks>
/// <para>身份上下文（<see cref="ConnectionIdentity"/>）在握手成功时就已经固定，
/// 之后不可变——M4 的 canonical transcript 要用它绑定 <c>presentedPin</c>（ADR-028）。</para>
/// <para>释放顺序：先关 <see cref="SslStream"/>（<c>leaveInnerStreamOpen: false</c>），
/// 再释放 <see cref="TcpClient"/>。调用方必须释放，否则 socket 泄漏。</para>
/// </remarks>
public sealed class TlsConnection : IDisposable
{
    private readonly TcpClient _client;
    private readonly SslStream _stream;
    private bool _disposed;

    internal TlsConnection(ConnectionIdentity identity, TcpClient client, SslStream stream)
    {
        Identity = identity;
        _client = client;
        _stream = stream;
    }

    /// <summary>本连接的不可变身份上下文（含 <c>presentedPin</c>）。</summary>
    public ConnectionIdentity Identity { get; }

    /// <summary>已认证的应用层流。</summary>
    public SslStream Stream
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _stream;
        }
    }

    /// <summary>协商出的 TLS 协议版本。</summary>
    public SslProtocols NegotiatedProtocol => Stream.SslProtocol;

    /// <summary>本端 TCP 端点（<c>本地IP:临时端口</c>）；socket 已释放或拿不到时为 <c>null</c>。</summary>
    /// <remarks>
    /// <para><b>只读观测量，不参与任何判定</b>。它的用途单一：两机验收时把控制端的一条连接
    /// 与被控端的一条 <c>peer=…</c> 记录**唯一配对**。没有它，就只能靠「聚合计数相等」
    /// 去猜「被计入的就是这几个场景」，而聚合数相等不构成配对证明
    /// （见 <c>docs/M3_ACCEPTANCE_UI_REVIEW_TRIAGE.md</c> §1.2）。</para>
    /// <para>放在这里而不是让验收器自己连 socket：验收器必须走产品路径，
    /// 自己连一条 TCP 再交给 <c>SslStream</c> 就等于把被测对象换掉了。</para>
    /// </remarks>
    public IPEndPoint? LocalEndPoint
    {
        get
        {
            if (_disposed)
            {
                return null;
            }

            try
            {
                return _client.Client.LocalEndPoint as IPEndPoint;
            }
            catch (SocketException)
            {
                // socket 已被对端/内核拆掉。这是观测，不是判定，拿不到就不给。
                return null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        }
    }

    /// <summary>释放底层 socket 与 TLS 流。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
        _client.Dispose();
    }
}
