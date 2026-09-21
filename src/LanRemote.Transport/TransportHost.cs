using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using LanRemote.Core.Abstractions;

namespace LanRemote.Transport;

/// <summary>
/// 被控端监听 Host：每个本地地址一个 listener，accept → 同子网 → 准入 → TLS。
/// </summary>
/// <remarks>
/// <para><b>顺序不可调换</b>（评审 A-6 / A-7）：
/// <c>accept</c> → <b>同子网校验</b> → <b>准入限额</b> → <b>TLS 握手</b>。
/// 把 TLS 提到最前面等于让任意远端都能逼我们做握手；把子网校验放到 TLS 之后
/// 等于先给了对方一次握手的机会再决定要不要认识它。</para>
/// <para><b>ADR-031：启动按网卡降级</b>。某个地址 bind 失败只记进
/// <see cref="TransportHostStartResult.Failures"/>，不影响其它地址。</para>
/// <para><b>关于 <c>ExclusiveAddressUse</c>：不要过度声称</b>。完整实测矩阵见
/// <c>MultiAddressListenTests</c>（本机 Win11 25H2 / 26200）：</para>
/// <list type="bullet">
/// <item><description>同端口 + 两个不同具体地址：开不开都<b>可以</b> bind（本类型靠这条工作）；</description></item>
/// <item><description>同地址同端口第二次：开不开都<b>被拒</b>（<see cref="SocketError.AddressAlreadyInUse"/>）；</description></item>
/// <item><description>别人先占更宽地址（<c>0.0.0.0</c>）且未开 exclusive：我们<b>仍会 bind 成功</b>——
/// <b>开 exclusive 也发现不了这种情况</b>；不过实测连到具体地址的连接会交给<b>更具体</b>的 listener，
/// 流量不会被更宽的 socket 截走；</description></item>
/// <item><description>带 <c>SO_REUSEADDR</c> 的后来者抢端口：开不开都<b>被拒</b>
/// （<see cref="SocketError.AccessDenied"/>）。</description></item>
/// </list>
/// <para>结论：<b>在本机可观测范围内它没有带来差别</b>。仍然保留 <c>true</c> 只作为
/// 对「<c>SO_REUSEADDR</c> 语义更宽松的旧版 Windows」的防御，
/// 不要把它当成「能发现端口已被占用」的手段——真正的冲突信号是
/// <see cref="SocketError.AddressAlreadyInUse"/>，它已记进
/// <see cref="TransportHostStartResult.Failures"/>。</para>
/// </remarks>
public sealed class TransportHost : IAsyncDisposable
{
    private readonly IReadOnlyList<IPAddress> _localAddresses;
    private readonly ISubnetPolicy _subnetPolicy;
    private readonly X509Certificate2 _serverCertificate;
    private readonly Func<AcceptedConnection, CancellationToken, Task> _sessionHandler;
    private readonly TransportHostOptions _options;
    private readonly ConnectionAdmissionLimiter _limiter;
    private readonly ConnectionRegistry _registry = new();
    private readonly List<TcpListener> _listeners = new();
    private readonly List<Task> _acceptLoops = new();
    private readonly CancellationTokenSource _stop = new();

    private int _started;
    private bool _disposed;

    /// <summary>构造 Host。</summary>
    /// <param name="localAddresses">要监听的本地 IPv4 地址（每张合格网卡一个）。</param>
    /// <param name="subnetPolicy">同子网策略；用<b>接受连接的那个本地地址</b>去校验。</param>
    /// <param name="serverCertificate">服务端证书（带私钥）。</param>
    /// <param name="sessionHandler">TLS 完成后的会话处理器；由它决定应用层做什么。</param>
    /// <param name="options">可调参数。</param>
    public TransportHost(
        IReadOnlyList<IPAddress> localAddresses,
        ISubnetPolicy subnetPolicy,
        X509Certificate2 serverCertificate,
        Func<AcceptedConnection, CancellationToken, Task> sessionHandler,
        TransportHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(localAddresses);
        ArgumentNullException.ThrowIfNull(subnetPolicy);
        ArgumentNullException.ThrowIfNull(serverCertificate);
        ArgumentNullException.ThrowIfNull(sessionHandler);

        _localAddresses = localAddresses;
        _subnetPolicy = subnetPolicy;
        _serverCertificate = serverCertificate;
        _sessionHandler = sessionHandler;
        _options = options ?? new TransportHostOptions();

        _limiter = new ConnectionAdmissionLimiter(
            _options.MaxConnections,
            _options.MaxConnectionsPerAddress);
    }

    /// <summary>是否正在运行。</summary>
    public bool IsRunning => Volatile.Read(ref _started) == 1 && !_stop.IsCancellationRequested;

    /// <summary>登记表中的活动连接数。</summary>
    public int ActiveConnections => _registry.Count;

    /// <summary>准入限额当前占用数（应当总是 ≤ <see cref="ActiveConnections"/> 的来源侧）。</summary>
    public int AdmittedConnections => _limiter.GlobalInUse;

    /// <summary>准入限额器（测试用）。</summary>
    public ConnectionAdmissionLimiter Limiter => _limiter;

    /// <summary>
    /// 启动所有 listener 并开始 accept。
    /// </summary>
    /// <returns>启动结果（哪些在听、哪些失败）。</returns>
    /// <remarks>按网卡降级（ADR-031）；一个都没听上<b>不抛异常</b>，由调用方决定如何呈现。</remarks>
    public TransportHostStartResult Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("TransportHost 只能启动一次。");
        }

        List<IPAddress> bound = new();
        List<TransportHostBindFailure> failures = new();

        foreach (IPAddress address in _localAddresses)
        {
            if (address is null)
            {
                continue;
            }

            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                failures.Add(new TransportHostBindFailure(address, null, "不是 IPv4 地址。"));
                continue;
            }

            TcpListener listener = new(address, _options.Port)
            {
                // 见类型说明：不开就会与更宽的 bind 静默共享端口。
                ExclusiveAddressUse = true,
            };

            try
            {
                listener.Start(_options.ListenBacklog);
            }
            catch (SocketException ex)
            {
                failures.Add(new TransportHostBindFailure(address, ex.SocketErrorCode, ex.Message));
                continue;
            }

            _listeners.Add(listener);
            bound.Add(address);
            _acceptLoops.Add(Task.Run(() => AcceptLoopAsync(listener, address)));
        }

        return new TransportHostStartResult(bound, failures);
    }

    /// <summary>
    /// 停机：停止 accept、取消全部连接、在预算内 join。
    /// </summary>
    /// <param name="timeout">停机预算；为空则用 <see cref="TransportHostOptions.ShutdownTimeout"/>。</param>
    /// <returns>所有连接是否都在预算内干净结束。</returns>
    public async Task<bool> StopAsync(TimeSpan? timeout = null)
    {
        TimeSpan budget = timeout ?? _options.ShutdownTimeout;
        if (budget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), budget, "停机预算必须为正。");
        }

        _stop.Cancel();

        foreach (TcpListener listener in _listeners)
        {
            try
            {
                listener.Stop();
            }
            catch (SocketException)
            {
            }
        }

        // accept 循环只会因为 listener.Stop() 抛 SocketException 或令牌被取消而退出，
        // 给它总预算的四分之一足够；剩下的留给连接收尾。
        TimeSpan acceptBudget = TimeSpan.FromTicks(budget.Ticks / 4);
        if (_acceptLoops.Count > 0)
        {
            try
            {
                await Task.WhenAll(_acceptLoops).WaitAsync(acceptBudget).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }

        bool allFinished = await _registry.StopAllAsync(budget - acceptBudget).ConfigureAwait(false);

        foreach (TcpListener listener in _listeners)
        {
            try
            {
                listener.Dispose();
            }
            catch (SocketException)
            {
            }
        }

        return allFinished;
    }

    /// <summary>释放 Host。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync(TcpListener listener, IPAddress localAddress)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                // listener.Stop() 会打断 pending accept，这是停机的正常路径。
                break;
            }

            _ = Task.Run(() => HandleAsync(client, localAddress));
        }
    }

    private async Task HandleAsync(TcpClient client, IPAddress listenerAddress)
    {
        AdmissionLease? lease = null;
        ConnectionRegistration? registration = null;
        ConnectionCloser? closer = null;
        SslStream? stream = null;

        try
        {
            if (client.Client.RemoteEndPoint is not IPEndPoint remoteEndPoint
                || client.Client.LocalEndPoint is not IPEndPoint localEndPoint)
            {
                return;
            }

            // ① 同子网校验：用**接受它的那个**本地地址去查绑定与掩码。
            if (!_subnetPolicy.IsAllowedPeer(localEndPoint.Address, remoteEndPoint.Address))
            {
                return;
            }

            // ② 准入限额：在 TLS 之前占用。
            if (!_limiter.TryAcquire(remoteEndPoint.Address, out lease) || lease is null)
            {
                return;
            }

            // ③ 登记表：停机时要能取消并 join。
            closer = new ConnectionCloser(client);
            registration = _registry.TryRegister(closer);
            if (registration is null)
            {
                return;
            }

            // ④ 才是 TLS。
            stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            closer.Attach(stream);

            using (CancellationTokenSource handshakeCts =
                   CancellationTokenSource.CreateLinkedTokenSource(registration.Cancellation))
            {
                handshakeCts.CancelAfter(_options.Timeouts.HandshakeTimeout);
                await stream.AuthenticateAsServerAsync(CreateServerOptions(), handshakeCts.Token)
                    .ConfigureAwait(false);
            }

            AcceptedConnection accepted = new(
                localEndPoint.Address,
                remoteEndPoint.Address,
                remoteEndPoint.Port,
                stream,
                stream.SslProtocol);

            await _sessionHandler(accepted, registration.Cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 停机取消，正常路径。
        }
        catch (AuthenticationException)
        {
            // 对端握手失败：没有可上报的通道，也不该上报细节。
        }
        catch (IOException)
        {
            // 对端断开发生在任何阶段都可能；不必记录。
        }
        catch (ObjectDisposedException)
        {
            // 停机时 socket 已被强制释放。
        }
        finally
        {
            stream?.Dispose();
            client.Dispose();
            lease?.Dispose();
            registration?.Dispose();
        }
    }

    private SslServerAuthenticationOptions CreateServerOptions()
    {
        return new SslServerAuthenticationOptions
        {
            ServerCertificate = _serverCertificate,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            AllowTlsResume = false,
            AllowRenegotiation = false,
            ClientCertificateRequired = false,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        };
    }

    /// <summary>
    /// 停机超时时用来打断阻塞读的资源句柄。
    /// </summary>
    /// <remarks>
    /// 取消只是「请求」：handler 可能卡在不可中断的读里。
    /// 登记表在超时后释放它，socket 一关，阻塞的读就会抛，handler 才真的结束。
    /// </remarks>
    private sealed class ConnectionCloser : IDisposable
    {
        private readonly TcpClient _client;
        private volatile SslStream? _stream;

        public ConnectionCloser(TcpClient client)
        {
            _client = client;
        }

        public void Attach(SslStream stream)
        {
            _stream = stream;
        }

        public void Dispose()
        {
            _stream?.Dispose();
            _client.Dispose();
        }
    }
}
