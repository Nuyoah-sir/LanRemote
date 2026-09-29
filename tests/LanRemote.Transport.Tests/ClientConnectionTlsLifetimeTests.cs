using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LanRemote.Transport.Tests;

public sealed class ClientConnectionTlsLifetimeTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly byte[] Request = [0x01, 0x00, 0xFE, 0x55, 0x7F];
    private static readonly byte[] Response = [0xA1, 0x00, 0x80, 0x42, 0xFF, 0x10, 0xB2];

    public enum IoOverload
    {
        SynchronousArray,
        SynchronousSpan,
        AsynchronousArray,
        AsynchronousMemory,
    }

    [Theory]
    [InlineData(IoOverload.SynchronousArray)]
    [InlineData(IoOverload.SynchronousSpan)]
    [InlineData(IoOverload.AsynchronousArray)]
    [InlineData(IoOverload.AsynchronousMemory)]
    public async Task Adapter_Forwards_Bidirectional_Bytes_And_Flush_Over_Real_Tls(IoOverload overload)
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        await using LoopbackTlsPair pair = new(certificate);
        await pair.OpenAsync();
        using Stream adapter = pair.Client.CreateVideoStream();
        Assert.True(adapter.CanRead);
        Assert.True(adapter.CanWrite);
        Assert.False(adapter.CanSeek);

        Task serverExchange = pair.Own(ExchangeOnServerAsync(pair.ServerStream));
        // 同步重载也放在有 owner 的任务上，guard 失败后可由 finally 关 socket 解堵并 join。
        Task clientExchange = pair.Own(Task.Run(() => ExchangeOnClientAsync(adapter, overload)));
        await clientExchange.WaitAsync(Guard);
        await serverExchange.WaitAsync(Guard);

        Task<int> peerRead = pair.StartPeerRead();
        Assert.False(peerRead.IsCompleted);
        Task close = pair.Own(adapter.DisposeAsync().AsTask());
        await close.WaitAsync(Guard);
        Assert.Empty(pair.Client.CleanupErrors);
        await AssertNetworkEndedAsync(peerRead);
        await pair.FinishServerAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Adapter_Dispose_And_DisposeAsync_Use_The_Connection_Close_Task(bool asyncFirst)
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        await using LoopbackTlsPair pair = new(certificate);
        await pair.OpenAsync();
        using Stream adapter = pair.Client.CreateVideoStream();
        Task<int> peerRead = pair.StartPeerRead();
        Assert.False(peerRead.IsCompleted);

        Task asyncClose;
        if (asyncFirst)
        {
            asyncClose = pair.Own(adapter.DisposeAsync().AsTask());
        }
        else
        {
            adapter.Dispose();
            // 必须在显式调用连接关闭前断言，不能由后者补做请求而让测试误绿。
            Assert.True(pair.Client.IsCloseRequested);
            asyncClose = pair.Own(adapter.DisposeAsync().AsTask());
        }

        Assert.True(pair.Client.IsCloseRequested);
        Task sharedClose = pair.Own(pair.Client.CloseAsync());
        Assert.Same(sharedClose, asyncClose);
        adapter.Dispose();
        Assert.Same(sharedClose, pair.Own(adapter.DisposeAsync().AsTask()));
        Assert.Same(sharedClose, pair.Client.CloseAsync());
        await sharedClose.WaitAsync(Guard);
        Assert.Empty(pair.Client.CleanupErrors);
        Assert.False(adapter.CanRead);
        Assert.False(adapter.CanWrite);
        Assert.Throws<ObjectDisposedException>(() => pair.Client.CreateVideoStream());
        Assert.Throws<ObjectDisposedException>(() => adapter.Read(new byte[1], 0, 1));

        await AssertNetworkEndedAsync(peerRead);
        await pair.FinishServerAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Adapter_Active_Close_Unblocks_And_Joins_Local_And_Peer_Reads(
        bool useMemory, bool asyncDispose)
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        await using LoopbackTlsPair pair = new(certificate);
        await pair.OpenAsync();
        using Stream adapter = pair.Client.CreateVideoStream();
        byte[] buffer = new byte[1];
        // 无取消 token、无调度闸门：这里直接调用真实 SSL 读，检查的是原读任务尚未完成。
        Task<int> localRead = pair.Own(useMemory
            ? adapter.ReadAsync(buffer.AsMemory(), CancellationToken.None).AsTask()
            : adapter.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None), allowNetworkEnd: true);
        Task<int> peerRead = pair.StartPeerRead();
        Assert.False(localRead.IsCompleted);
        Assert.False(peerRead.IsCompleted);

        Task close;
        if (asyncDispose)
        {
            close = pair.Own(adapter.DisposeAsync().AsTask());
        }
        else
        {
            adapter.Dispose();
            Assert.True(pair.Client.IsCloseRequested);
            close = pair.Own(pair.Client.CloseAsync());
        }

        Assert.True(pair.Client.IsCloseRequested);
        Assert.Same(close, pair.Client.CloseAsync());
        await close.WaitAsync(Guard);
        Assert.Empty(pair.Client.CleanupErrors);
        // 关闭任务完成不等于读完成；两端都必须在夹具兜底关闭前逐一观察并 join。
        await AssertNetworkEndedAsync(localRead);
        await AssertNetworkEndedAsync(peerRead);
        await pair.FinishServerAsync();
    }

    [Fact]
    public async Task Public_Dispose_Returns_Only_After_Real_Transport_Close_And_Reads_Are_Joined()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        await using LoopbackTlsPair pair = new(certificate);
        await pair.OpenAsync();
        using Stream adapter = pair.Client.CreateVideoStream();
        SslStream originalSsl = pair.Client.Stream;
        Task<int> localRead = pair.Own(
            adapter.ReadAsync(new byte[1].AsMemory(), CancellationToken.None).AsTask(), allowNetworkEnd: true);
        Task<int> peerRead = pair.StartPeerRead();
        Assert.False(localRead.IsCompleted);
        Assert.False(peerRead.IsCompleted);

        Task<bool> dispose = pair.Own(Task.Run(() =>
        {
            pair.Client.Dispose();
            // 在同步 Dispose 返回的同一调用栈采样；不能先 await CloseAsync 再声称同步关闭。
            return pair.Client.CloseAsync().IsCompletedSuccessfully &&
                !originalSsl.CanRead && !originalSsl.CanWrite;
        }));
        Assert.True(await dispose.WaitAsync(Guard), "public Dispose 返回时必须已完成物理释放尝试。");
        Assert.True(pair.Client.IsCloseRequested);
        Assert.Empty(pair.Client.CleanupErrors);
        Assert.Null(pair.Client.LocalEndPoint);
        Assert.Throws<ObjectDisposedException>(() => pair.Client.Stream);
        pair.Client.Dispose();

        await AssertNetworkEndedAsync(localRead);
        await AssertNetworkEndedAsync(peerRead);
        await pair.FinishServerAsync();
    }

    private static async Task ExchangeOnServerAsync(SslStream stream)
    {
        byte[] received = new byte[Request.Length];
        await stream.ReadExactlyAsync(received.AsMemory(), CancellationToken.None);
        Assert.Equal(Request, received);
        await stream.WriteAsync(Response.AsMemory(), CancellationToken.None);
        await stream.FlushAsync(CancellationToken.None);
    }

    private static async Task ExchangeOnClientAsync(Stream adapter, IoOverload overload)
    {
        byte[] sendBuffer = new byte[Request.Length + 4];
        Array.Fill(sendBuffer, (byte)0xCC);
        Request.CopyTo(sendBuffer, 2);
        switch (overload)
        {
            case IoOverload.SynchronousArray:
                adapter.Write(sendBuffer, 2, Request.Length);
                adapter.Flush();
                break;
            case IoOverload.SynchronousSpan:
                adapter.Write(sendBuffer.AsSpan(2, Request.Length));
                adapter.Flush();
                break;
            case IoOverload.AsynchronousArray:
                await adapter.WriteAsync(sendBuffer, 2, Request.Length, CancellationToken.None);
                await adapter.FlushAsync(CancellationToken.None);
                break;
            case IoOverload.AsynchronousMemory:
                await adapter.WriteAsync(sendBuffer.AsMemory(2, Request.Length), CancellationToken.None);
                await adapter.FlushAsync(CancellationToken.None);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(overload));
        }

        byte[] received = new byte[Response.Length + 4];
        Array.Fill(received, (byte)0xCC);
        int total = 0;
        while (total < Response.Length)
        {
            int offset = 2 + total;
            int remaining = Response.Length - total;
            int read = overload switch
            {
                IoOverload.SynchronousArray => adapter.Read(received, offset, remaining),
                IoOverload.SynchronousSpan => adapter.Read(received.AsSpan(offset, remaining)),
                IoOverload.AsynchronousArray =>
                    await adapter.ReadAsync(received, offset, remaining, CancellationToken.None),
                IoOverload.AsynchronousMemory =>
                    await adapter.ReadAsync(received.AsMemory(offset, remaining), CancellationToken.None),
                _ => throw new ArgumentOutOfRangeException(nameof(overload)),
            };
            Assert.InRange(read, 1, remaining);
            total += read;
        }

        Assert.Equal(Response, received.AsSpan(2, Response.Length).ToArray());
        Assert.All(received.Take(2).Concat(received.TakeLast(2)), value => Assert.Equal((byte)0xCC, value));
    }

    private static async Task AssertNetworkEndedAsync(Task<int> originalRead)
    {
        try
        {
            Assert.Equal(0, await originalRead.WaitAsync(Guard));
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // 仅用于双方握手已检查成功后的读；允许 EOF/RST/本端释放，不接受握手失败或取消。
        }
        Assert.True(originalRead.IsCompleted);
        Assert.False(originalRead.IsCanceled);
    }

    // 只监听单条回环连接；不启动生产 Host，也不改变生产子网准入策略。
    private sealed class LoopbackTlsPair(X509Certificate2 certificate) : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly object _serverGate = new();
        private readonly TaskCompletionSource<Exception?> _handshake =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseServer =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<(Task Task, bool AllowNetworkEnd)> _operations = [];
        private Task? _serverTask;
        private Task<TlsConnection>? _connectTask;
        private TcpClient? _serverClient;
        private SslStream? _serverSsl;
        private SslStream? _clientSsl;

        internal TlsConnection Client { get; private set; } = null!;
        internal SslStream ServerStream => _serverSsl!;

        internal async Task OpenAsync()
        {
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Assert.True(ConnectionTarget.TryCreate(
                Guid.Parse("cafecafe-1234-4321-9876-0123456789ab"), IPAddress.Loopback, port,
                TestCertificateFactory.Fingerprint(certificate), out ConnectionTarget? target));

            // 保存原始任务，而不是保存 WaitAsync 的超时包装；迟到的成功连接也由 finally 接管。
            _serverTask = ServeAsync();
            _connectTask = new TlsClientConnector().ConnectAsync(target!, cancellationToken: _stop.Token);
            Exception? serverHandshakeError = await _handshake.Task.WaitAsync(Guard);
            Assert.Null(serverHandshakeError);
            Client = await _connectTask.WaitAsync(Guard);
            _clientSsl = Client.Stream;
            Assert.True(_clientSsl.IsAuthenticated);
            Assert.True(_clientSsl.IsEncrypted);
            Assert.True(ServerStream.IsAuthenticated);
            Assert.True(ServerStream.IsEncrypted);
            Assert.True(Client.Identity.PinsMatch);
            Assert.Equal(target!.ExpectedCertSha256.ToArray(), Client.Identity.PresentedCertSha256.ToArray());
            Assert.NotNull(_clientSsl.RemoteCertificate);
            Assert.Equal(SHA256.HashData(_clientSsl.RemoteCertificate.GetRawCertData()),
                Client.Identity.PresentedCertSha256.ToArray());
            Assert.Contains(Client.NegotiatedProtocol, new[] { SslProtocols.Tls12, SslProtocols.Tls13 });
            Assert.Equal(Client.NegotiatedProtocol, ServerStream.SslProtocol);
        }

        internal T Own<T>(T task, bool allowNetworkEnd = false) where T : Task
        {
            _operations.Add((task, allowNetworkEnd));
            return task;
        }

        internal Task<int> StartPeerRead() => Own(
            ServerStream.ReadAsync(new byte[1].AsMemory(), CancellationToken.None).AsTask(),
            allowNetworkEnd: true);

        internal async Task FinishServerAsync()
        {
            _releaseServer.TrySetResult();
            await _serverTask!.WaitAsync(Guard);
            Assert.Null(await _handshake.Task);
        }

        private async Task ServeAsync()
        {
            try
            {
                TcpClient accepted = await _listener.AcceptTcpClientAsync(_stop.Token);
                lock (_serverGate)
                {
                    // 先登记再检查取消；即使 teardown 与 accept 竞争，finally 也拥有这个 socket。
                    _serverClient = accepted;
                    _stop.Token.ThrowIfCancellationRequested();
                    _serverSsl = new SslStream(accepted.GetStream(), leaveInnerStreamOpen: false);
                }

                // 与 TransportHost.CreateServerOptions 一致；不安装任何接受所有证书的回调。
                SslServerAuthenticationOptions options = new()
                {
                    ServerCertificate = certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    AllowTlsResume = false,
                    AllowRenegotiation = false,
                    ClientCertificateRequired = false,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                };
                await _serverSsl.AuthenticateAsServerAsync(options, _stop.Token);
                _handshake.TrySetResult(null);
                // 测试先从对侧原始读观测结束，再开闸；服务端不会自行提前关闭来替客户端解堵。
                await _releaseServer.Task;
            }
            catch (Exception error)
            {
                _handshake.TrySetResult(error);
                throw;
            }
            finally
            {
                CloseServerTransport();
            }
        }

        private void CloseServerTransport()
        {
            lock (_serverGate)
            {
                try { _serverClient?.Dispose(); }
                finally { _serverSsl?.Dispose(); }
            }
        }

        public async ValueTask DisposeAsync()
        {
            List<Exception> failures = [];
            // await using 的 finally 总会开闸、取消建连并关闭两端；随后无超时地 join 原任务。
            // guard 只用于测试断言，绝不能因第二次 guard 超时而把任务留在后台。
            _releaseServer.TrySetResult();
            try
            {
                Attempt(_stop.Cancel);
                Attempt(_listener.Stop);
                Attempt(CloseServerTransport);

                TlsConnection? client = null;
                if (_connectTask is not null)
                {
                    try { client = await _connectTask; }
                    catch (Exception error) { failures.Add(error); }
                }
                if (client is not null)
                {
                    // 即使 OpenAsync 超时未交付连接，也必须释放迟到结果。
                    Attempt(() => _clientSsl ??= client.Stream);
                    Task close = client.CloseAsync();
                    // 独立于产品关闭实现的兜底，仅在断言结束/失败后使用，确保本端读也能 join。
                    Attempt(() => _clientSsl?.Dispose());
                    await JoinAsync(close, allowNetworkEnd: false);
                    failures.AddRange(client.CleanupErrors);
                }

                foreach ((Task task, bool allowNetworkEnd) in _operations)
                    await JoinAsync(task, allowNetworkEnd);
                if (_serverTask is not null)
                    await JoinAsync(_serverTask, allowNetworkEnd: false);
            }
            finally
            {
                _stop.Dispose();
            }
            if (failures.Count != 0)
                throw new AggregateException("真实 TLS 生命周期测试收尾失败（含服务端握手/原任务错误）。", failures);

            void Attempt(Action action)
            {
                try { action(); }
                catch (Exception error) { failures.Add(error); }
            }

            async Task JoinAsync(Task task, bool allowNetworkEnd)
            {
                try { await task; }
                catch (Exception error) when (allowNetworkEnd && (error is IOException or ObjectDisposedException))
                {
                    // 只有握手成功后明确登记的终止读可接受这两类错误，握手任务绝不走此过滤。
                }
                catch (Exception error) { failures.Add(error); }
            }
        }
    }
}
