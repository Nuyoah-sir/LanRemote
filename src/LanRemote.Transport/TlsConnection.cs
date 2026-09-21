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
