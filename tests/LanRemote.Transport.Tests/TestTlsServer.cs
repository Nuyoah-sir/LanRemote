using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace LanRemote.Transport.Tests;

/// <summary>
/// 测试用 TLS 服务端（只跑在回环地址上）。
/// </summary>
/// <remarks>
/// <para>只做握手，不做应用层协议——M3 阶段 1 只需要一条真实的 TLS 连接。</para>
/// <para><b>握手闸门</b>（<see cref="HandshakeGate"/>）是给 TOCTOU 与超时测试用的：
/// accept 之后、<c>AuthenticateAsServerAsync</c> 之前会等它，
/// 这样测试就能精确控制「客户端正卡在握手中间」这一时刻。</para>
/// <para><b>服务端异常必须被收集</b>：客户端只能看到 <c>IOException: unexpected EOF</c>，
/// 真实原因在服务端。不收集就等于把失败原因丢掉（<c>HANDOFF.md</c> 第 16 节）。</para>
/// </remarks>
internal sealed class TestTlsServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _certificate;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<Exception> _failures = new();
    private readonly ConcurrentQueue<IPEndPoint> _remoteEndPoints = new();
    private readonly ConcurrentQueue<Task> _sessions = new();
    private readonly ManualResetEventSlim _accepted = new(false);
    private bool _disposed;

    public TestTlsServer(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        _certificate = certificate;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        AcceptLoop = Task.Run(RunAcceptLoopAsync);
    }

    /// <summary>监听端口（回环）。</summary>
    public int Port { get; }

    /// <summary>accept 循环任务。</summary>
    public Task AcceptLoop { get; }

    /// <summary>至少 accept 了一个连接。</summary>
    public ManualResetEventSlim Accepted => _accepted;

    /// <summary>握手前的闸门；不设则不等。</summary>
    public ManualResetEventSlim? HandshakeGate { get; set; }

    /// <summary>服务端侧捕获到的异常。</summary>
    public IReadOnlyList<Exception> Failures => _failures.ToArray();

    /// <summary>服务端 accept 到的远端端点（即客户端的 <c>本地IP:临时端口</c>）。</summary>
    /// <remarks>
    /// 存在的唯一理由：让「客户端 <c>LocalEndPoint</c>」这个观测量有**对侧**可断言。
    /// 没有对侧，那个属性写成 <c>return null</c> 测试也照样绿——
    /// 这正是本项目说的「删一行不会变红的开关」。
    /// </remarks>
    public IReadOnlyList<IPEndPoint> AcceptedRemoteEndPoints => _remoteEndPoints.ToArray();

    private async Task RunAcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                _accepted.Set();

                if (client.Client.RemoteEndPoint is IPEndPoint remote)
                {
                    _remoteEndPoints.Enqueue(remote);
                }

                _sessions.Enqueue(Task.Run(() => ServeOneAsync(client)));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private async Task ServeOneAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                ManualResetEventSlim? gate = HandshakeGate;
                if (gate is not null)
                {
                    gate.Wait(TimeSpan.FromSeconds(30));
                }

                var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    AllowTlsResume = false,
                    AllowRenegotiation = false,
                    ClientCertificateRequired = false,
                }).ConfigureAwait(false);

                // 握完手后等客户端断开再收尾：立刻关闭会和「客户端还在发 Finished」撞上，
                // 制造出与被测行为无关的偶发失败。
                byte[] buffer = new byte[1];
                await stream.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 测试收尾时的正常中断，不算失败。
        }
        catch (Exception ex)
        {
            _failures.Enqueue(ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 闸门必须放行，否则 session 会卡满 30 秒。
        HandshakeGate?.Set();

        _stop.Cancel();
        _listener.Stop();

        try
        {
            AcceptLoop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        while (_sessions.TryDequeue(out Task? session))
        {
            try
            {
                session.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }
        }

        _stop.Dispose();
        _accepted.Dispose();
    }
}
