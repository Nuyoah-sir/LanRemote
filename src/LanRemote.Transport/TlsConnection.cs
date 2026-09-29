using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Security.Authentication;

namespace LanRemote.Transport;

/// <summary>
/// 一条已建立 TLS 的连接：身份上下文 + 可用的 <see cref="SslStream"/>。
/// </summary>
/// <remarks>
/// <para>身份上下文（<see cref="ConnectionIdentity"/>）在握手成功时就已经固定，
/// 之后不可变——M4 的 canonical transcript 要用它绑定 <c>presentedPin</c>（ADR-028）。</para>
/// <para>关闭由一个共享任务执行：先释放 TCP 解堵，再独立尝试释放 SSL。
/// public Dispose 同步等待这两项尝试；内部 CloseAsync 不阻塞调用线程。
/// 关闭完成不保证释放成功，也不代替调用方对原读写任务的 join。</para>
/// </remarks>
public sealed class TlsConnection : IDisposable
{
    private readonly TcpClient _client;
    private readonly SslStream _stream;
    private readonly object _closeGate = new();
    private Task? _closeTask;
    private int _closeRequested;
    private int _disposeErrorObserver;
    private Exception? _clientError;
    private Exception? _streamError;

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
            ObjectDisposedException.ThrowIf(IsCloseRequested, this);
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
            if (IsCloseRequested)
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

    internal bool IsCloseRequested => Volatile.Read(ref _closeRequested) != 0;

    /// <summary>每次返回同一任务；完成仅表示两项释放尝试结束，错误见 CleanupErrors。</summary>
    internal Task CloseAsync()
    {
        lock (_closeGate)
        {
            Volatile.Write(ref _closeRequested, 1);
            // 不在请求线程执行可能阻塞的 Dispose；也不在 gate 内等待 worker。
            return _closeTask ??= Task.Run(CloseCore);
        }
    }

    /// <summary>至多两个原始异常的独立只读快照，顺序为 TCP、SSL。</summary>
    internal IReadOnlyList<Exception> CleanupErrors
    {
        get
        {
            List<Exception> errors = new(2);
            if (Volatile.Read(ref _clientError) is { } client) errors.Add(client);
            if (Volatile.Read(ref _streamError) is { } stream) errors.Add(stream);
            return errors.AsReadOnly();
        }
    }

    internal Stream CreateVideoStream()
    {
        ObjectDisposedException.ThrowIf(IsCloseRequested, this);
        return new ClientOwnedVideoStream(this);
    }

    /// <summary>
    /// 同步等待共享关闭。首个 public Dispose 调用者观察并抛出清理错误；
    /// 后续调用仍等待，但不重放错误（保留重复 Dispose 的无错误行为）。
    /// 所有错误始终可从内部 CleanupErrors 读取。
    /// </summary>
    public void Dispose()
    {
        bool reportErrors = Interlocked.Exchange(ref _disposeErrorObserver, 1) == 0;
        CloseAsync().GetAwaiter().GetResult();
        if (!reportErrors) return;

        IReadOnlyList<Exception> errors = CleanupErrors;
        if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1) throw new AggregateException("客户端连接释放发生多个错误。", errors);
    }

    private void CloseCore()
    {
        try
        {
            _client.Dispose();
        }
        catch (Exception error)
        {
            Volatile.Write(ref _clientError, error);
        }

        try
        {
            _stream.Dispose();
        }
        catch (Exception error)
        {
            Volatile.Write(ref _streamError, error);
        }
    }
}
