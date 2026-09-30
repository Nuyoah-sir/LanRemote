using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

public sealed partial class ClientVideoAttachTlsTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly Guid ServerDeviceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid ClientDeviceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly byte[] AccessKey = Convert.FromHexString("0F1E2D3C4B5A69788796A5B4C3D2E1F0");
    private enum Reply { Router, Combined, Fragmented, Invalid, SecondAck, Hang }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task CompletedAsync(Task task)
    {
        // 只对不传播业务错误的观察任务加 Guard；即使原任务恰在 Guard 到期后结束，也不能冒充及时完成。
        await ObserveAsync().WaitAsync(Guard);

        async Task ObserveAsync() => await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private static async Task<Exception> ErrorAsync(Task task)
    {
        await CompletedAsync(task);
        Exception? error = await Record.ExceptionAsync(() => task);
        Assert.NotNull(error);
        return error;
    }

    private static async Task JoinedCancellationAsync(Task task)
    {
        await CompletedAsync(task);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(task.IsCanceled);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        long start = Stopwatch.GetTimestamp();
        while (!condition())
        {
            Assert.True(Stopwatch.GetElapsedTime(start) < Guard, "测试 Guard：连接登记/回收尚未达到预期。");
            await Task.Yield();
        }
    }

    private static string AckJson(Guid sessionId) =>
        $"{{\"type\":\"video_attach_ack\",\"channel\":\"video\",\"protocol\":1,\"sessionId\":\"{sessionId:D}\"}}";

    private static byte[] Prefix(uint length)
    {
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, length);
        return prefix;
    }

    private static byte[] Framed(string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        return [.. Prefix((uint)payload.Length), .. payload];
    }

    private sealed class ObservedConnection(AcceptedConnection connection, FirstFrameRouter router)
    {
        internal AcceptedConnection Connection { get; } = connection;
        internal FirstFrameRouter Router { get; } = router;
        internal Task Worker { get; set; } = Task.CompletedTask;
    }

    // 相比旧夹具这里只拥有一个首帧源、一个恶意响应脚本；所有原操作都由 await using 的 finally 收尾。
    private sealed class TlsScenario : IAsyncDisposable
    {
        private readonly Reply _reply;
        private readonly InvalidAck _invalid;
        private readonly int _splitAt;
        private readonly bool _rotateCertificate;
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Task> _clients = [];
        private readonly HashSet<Task> _assertedFailures = [];
        private readonly List<Task<EncodedFrame?>> _reads = [];
        private readonly ConcurrentQueue<ObservedConnection> _servers = new();
        private readonly TaskCompletionSource<ObservedConnection> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task<AuthenticatedControlSession>? _controlTask;
        private ObservedConnection _controlServer = null!;
        private int _handlerCount;

        internal TlsScenario(Reply reply = Reply.Router, InvalidAck invalid = default,
            int splitAt = 1, bool rejectSecond = false, bool rotateCertificate = false)
        {
            _reply = reply;
            _invalid = invalid;
            _splitAt = splitAt;
            _rotateCertificate = rotateCertificate;
            Policy = new LoopbackPolicy(rejectSecond);
            Context = new ControlAuthContext
            {
                ServerDeviceId = ServerDeviceId,
                AccessSecretStore = new FixedSecretStore(),
                FailedAuthLimiter = new FailedAuthLimiter(),
                PendingApprovalLimiter = new ConnectionAdmissionLimiter(3, 1),
                ApprovalGate = new ApproveGate(),
                SessionRegistry = new SessionRegistry(),
                Options = new ControlAuthOptions
                {
                    RequireLocalApproval = true,
                    FailureDelayMin = TimeSpan.Zero,
                    FailureDelayMax = TimeSpan.Zero,
                    VideoAttachExpiresInMs = 15_000,
                },
            };
        }

        internal X509Certificate2 Certificate { get; } = TestCertificateFactory.Create();
        internal TransportHost Host { get; private set; } = null!;
        internal ConnectionTarget Target { get; private set; } = null!;
        internal ControlAuthContext Context { get; }
        internal LoopbackPolicy Policy { get; }
        internal CertificateSwitchServer? CertificateSwitch { get; private set; }
        // 只注入客户端；真实 TLS 回调与 attach 使用同一个父 clock，服务端不依赖这个时钟。
        internal ManualDeadlineClock Clock { get; } = new(DateTimeOffset.UtcNow);
        internal SingleFrameSource Source { get; } = new();
        internal AuthenticatedControlSession Control { get; private set; } = null!;
        internal int HandlerCount => Volatile.Read(ref _handlerCount);
        internal TaskCompletionSource<ObservedConnection> Second { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource HelloVerified { get; } = Signal();
        internal TaskCompletionSource PartialAck { get; } = Signal();
        internal TaskCompletionSource ReleaseAck { get; } = Signal();
        internal TaskCompletionSource PeerClosed { get; } = Signal();

        internal async Task OpenControlAsync()
        {
            // await using 已取得夹具所有权后才创建监听器；中途断言/启动失败也会进入完整收尾。
            using TcpListener reservation = new(IPAddress.Loopback, 0);
            reservation.Start();
            int hostPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            Host = new TransportHost([IPAddress.Loopback], Policy, Certificate, HandleAsync,
                new TransportHostOptions { Port = hostPort, MaxConnectionsPerAddress = 2 });
            TransportHostStartResult started = Host.Start();
            Assert.True(started.IsListening);
            Assert.Empty(started.Failures);
            if (_rotateCertificate) CertificateSwitch = new CertificateSwitchServer(hostPort);
            Assert.True(ConnectionTarget.TryCreate(ServerDeviceId, IPAddress.Loopback, CertificateSwitch?.Port ?? hostPort,
                TestCertificateFactory.Fingerprint(Certificate), out ConnectionTarget? target));
            Target = target!;
            CertificateSwitch?.Start();
            _controlTask = Own(new ControlClientConnector().ConnectAndAuthenticateAsync(
                Target, ClientDeviceId, "客户端视频附加 TLS 测试", AccessKey, SessionPermission.Control,
                clock: Clock, cancellationToken: _stop.Token));
            Control = await _controlTask.WaitAsync(Guard);
            _controlServer = await _first.Task.WaitAsync(Guard);
            await WaitUntilAsync(() => Context.SessionRegistry.ActiveSessionCount == 1);
            Assert.Equal(Target.Port, Control.Identity.Port);
            Assert.Equal(ServerDeviceId, Control.Identity.DeviceId);
            Assert.Equal(Target.ExpectedCertSha256.ToArray(), Control.Identity.PresentedCertSha256.ToArray());
            AssertControlAlive();
        }

        internal Task<AuthenticatedControlSession.ClientVideoLifetime> Attach(CancellationToken token = default)
        {
            // 只有无连接委托的生产入口；不用 StartVideoLifetimeAsync 或伪造 TLS 身份代替。
            try { return Own(Control.AttachVideoCoreAsync(token)); }
            catch (Exception error)
            {
                // 统一观察同步的重复资格拒绝与异步 I/O 拒绝；测试本体仍按精确类型/短码断言。
                return Own(Task.FromException<AuthenticatedControlSession.ClientVideoLifetime>(error));
            }
        }

        internal Task<AuthenticatedControlSession.ClientVideoLifetime> AttachMonitored(CancellationToken token = default)
        {
            try
            {
                // 显式生产入口固定真实 TlsClientConnector；旧 Attach 不启用 monitor。
                Task<AuthenticatedControlSession.ClientVideoLifetime> attach =
                    Own(Control.AttachVideoWithControlMonitorCoreAsync(token));
                if (Control.ControlMonitorCompletion is { } monitor) Own(monitor);
                return attach;
            }
            catch (Exception error)
            {
                return Own(Task.FromException<AuthenticatedControlSession.ClientVideoLifetime>(error));
            }
        }

        internal Task CloseServerControlAsync() => Own(_controlServer.Connection.CloseAsync());

        internal Task SendServerControlByteAsync(byte value) => Own(
            _controlServer.Connection.Stream.WriteAsync(new byte[] { value }, CancellationToken.None).AsTask());

        internal async Task JoinServerConnectionsAsync()
        {
            // 这里只等待事实，不请求关闭，更不能对已经断开的 Control 调用 AssertControlAlive。
            foreach (ObservedConnection server in _servers)
                await server.Worker.WaitAsync(Guard);
            await WaitForConnectionCountAsync(0);
            Assert.Empty(Context.SessionRegistry.Snapshot());
        }

        internal T Own<T>(T task) where T : Task { _clients.Add(task); return task; }

        internal Task<EncodedFrame?> Read(AuthenticatedControlSession.ClientVideoLifetime child)
        {
            Task<EncodedFrame?> read = Own(child.ReadFrameAsync());
            _reads.Add(read);
            return read;
        }

        internal Task<Exception> FailureAsync(Task task)
        {
            _assertedFailures.Add(task);
            return ErrorAsync(task);
        }

        private Task HandleAsync(AcceptedConnection connection, CancellationToken ct)
        {
            int number = Interlocked.Increment(ref _handlerCount);
            ObservedConnection observed = new(connection, new FirstFrameRouter(Context, TransportTimeouts.Default, Source));
            observed.Worker = RunAsync();
            _servers.Enqueue(observed);
            (number == 1 ? _first : Second).TrySetResult(observed);
            return observed.Worker;

            async Task RunAsync()
            {
                Assert.True(connection.Stream.IsAuthenticated);
                Assert.True(connection.Stream.IsEncrypted);
                if (number == 1 || _reply == Reply.Router)
                    await observed.Router.RunAsync(connection, ct);
                else
                {
                    await VerifyHelloAsync(connection, ct);
                    HelloVerified.TrySetResult();
                    await SendReplyAsync(connection.Stream, ct);
                    try { Assert.Equal(0, await connection.Stream.ReadAsync(new byte[1], ct)); }
                    catch (IOException) { ct.ThrowIfCancellationRequested(); }
                    PeerClosed.TrySetResult();
                }
            }
        }

        private async Task VerifyHelloAsync(AcceptedConnection connection, CancellationToken ct)
        {
            byte[] prefix = new byte[4];
            await connection.Stream.ReadExactlyAsync(prefix, ct);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(prefix);
            Assert.InRange(length, 1u, 4096u);
            byte[] payload = new byte[(int)length];
            byte[] token = TestOnlyControlSessionSecrets.GetOwnedToken(Control).ToArray();
            try
            {
                await connection.Stream.ReadExactlyAsync(payload, ct);
                Assert.True(VideoHelloFrame.TryParse(payload, out _, out string? rejection), rejection);
                using JsonDocument document = JsonDocument.Parse(payload);
                JsonElement root = document.RootElement;
                Assert.Equal(new[] { "attachNonce", "attachProof", "channel", "protocol", "sessionId", "type" },
                    root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
                Assert.Equal("channel_hello", root.GetProperty("type").GetString());
                Assert.Equal("video", root.GetProperty("channel").GetString());
                Assert.Equal(1, root.GetProperty("protocol").GetInt32());
                Assert.Equal(Control.SessionId.ToString("D"), root.GetProperty("sessionId").GetString());
                byte[] nonce = Convert.FromBase64String(root.GetProperty("attachNonce").GetString()!);
                byte[] proof = Convert.FromBase64String(root.GetProperty("attachProof").GetString()!);
                Assert.Equal(16, nonce.Length);
                Assert.Equal(32, proof.Length);
                Assert.Equal(32, token.Length);
                Assert.NotNull(connection.Stream.LocalCertificate);
                byte[] pin = SHA256.HashData(connection.Stream.LocalCertificate.GetRawCertData());
                Assert.Equal(Control.Identity.PresentedCertSha256.ToArray(), pin);
                Assert.Equal(connection.Security.ServerCertificateSha256.ToArray(), pin);
                // 独立 oracle：域 19 + 网络序 UUID 16 + nonce 16 + 本次实际 TLS 证书 pin 32。
                byte[] transcript = [.. Encoding.UTF8.GetBytes("LANREMOTE-VIDEO-V1\0"),
                    .. Convert.FromHexString(Control.SessionId.ToString("N")), .. nonce, .. pin];
                Assert.Equal(83, transcript.Length);
                using HMACSHA256 hmac = new(token);
                Assert.True(CryptographicOperations.FixedTimeEquals(hmac.ComputeHash(transcript), proof),
                    "客户端 hello 的独立 HMAC 验证失败。");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(token);
                CryptographicOperations.ZeroMemory(payload);
            }
        }

        private async Task SendReplyAsync(SslStream stream, CancellationToken ct)
        {
            if (_reply == Reply.Hang) return;
            string json = AckJson(Control.SessionId);
            byte[] ack = Framed(json);
            byte[] wire = _reply switch
            {
                Reply.SecondAck => [.. ack, .. ack, .. VideoFrameTestData.Wire()],
                Reply.Invalid => _invalid switch
                {
                    InvalidAck.WrongSessionId => Framed(AckJson(Control.SessionId == ServerDeviceId ? ClientDeviceId : ServerDeviceId)),
                    InvalidAck.WrongType => Framed(json.Replace("video_attach_ack", "auth_success", StringComparison.Ordinal)),
                    InvalidAck.DuplicateField => Framed(json[..^1] + ",\"protocol\":1}"),
                    InvalidAck.ZeroLength => Prefix(0),
                    InvalidAck.OversizeLength => Prefix(4097),
                    InvalidAck.UnsignedLength => Prefix(uint.MaxValue),
                    InvalidAck.TruncatedPrefix => ack[..2],
                    InvalidAck.TruncatedPayload => ack[..^1],
                    _ => throw new InvalidOperationException(),
                },
                _ => [.. ack, .. VideoFrameTestData.Wire()],
            };
            if (_reply == Reply.Fragmented)
            {
                await stream.WriteAsync(wire.AsMemory(0, _splitAt), ct);
                await stream.FlushAsync(ct);
                PartialAck.TrySetResult();
                await ReleaseAck.Task.WaitAsync(ct);
                // 明确拆开长度前缀/JSON；这是多次应用写，不对 TLS record 或 TCP 分段作假设。
                for (int i = _splitAt; i < ack.Length; i++)
                    await stream.WriteAsync(wire.AsMemory(i, 1), ct);
                await stream.WriteAsync(wire.AsMemory(ack.Length), ct);
            }
            else
            {
                // Combined 与 SecondAck 各用一次 Write；不声称对应一个 TLS record。
                await stream.WriteAsync(wire, ct);
            }
            await stream.FlushAsync(ct);
            if (_reply == Reply.Invalid && _invalid is InvalidAck.TruncatedPrefix or InvalidAck.TruncatedPayload)
                await stream.ShutdownAsync();
        }

        internal async Task AssertDistinctConnectionsAsync()
        {
            ObservedConnection video = await Second.Task.WaitAsync(Guard);
            await WaitForConnectionCountAsync(2);
            ConnectionSecurityContext first = _controlServer.Connection.Security;
            ConnectionSecurityContext second = video.Connection.Security;
            Assert.NotEqual(first.ConnectionId, second.ConnectionId);
            Assert.NotEqual(first.RemotePort, second.RemotePort);
            Assert.Equal(IPAddress.Loopback, first.LocalAddress);
            Assert.Equal(IPAddress.Loopback, second.LocalAddress);
            Assert.Equal(IPAddress.Loopback, first.RemoteAddress);
            Assert.Equal(IPAddress.Loopback, second.RemoteAddress);
            Assert.Equal(first.ServerCertificateSha256.ToArray(), second.ServerCertificateSha256.ToArray());
            Assert.False(_controlServer.Worker.IsCompleted);
            Assert.False(video.Worker.IsCompleted);
        }

        internal void AssertControlAlive()
        {
            ControlSessionSummary session = Assert.Single(Context.SessionRegistry.Snapshot());
            Assert.Equal(Control.SessionId, session.SessionId);
            Assert.Equal(ClientDeviceId, session.ClientDeviceId);
            Assert.Equal(_controlServer.Connection.Security.ConnectionId, session.ConnectionId);
            Assert.Equal(SessionPermission.Control, session.GrantedPermission);
            Assert.False(_controlServer.Worker.IsCompleted);
            Assert.True(Control.Stream.CanRead);
            Assert.True(Control.Stream.CanWrite);
        }

        internal Task WaitForConnectionCountAsync(int count) => WaitUntilAsync(() =>
            Host.ActiveConnections == count && Host.AdmittedConnections == count);

        public async ValueTask DisposeAsync()
        {
            List<Exception> failures = [];
            ReleaseAck.TrySetResult();
            Source.Release.TrySetResult();
            await AttemptAsync(() => Own(_stop.CancelAsync()).WaitAsync(Guard));
            if (_controlTask is not null)
                await AttemptAsync(async () =>
                {
                    await CompletedAsync(_controlTask);
                    if (_controlTask.IsCompletedSuccessfully)
                        await Own(_controlTask.Result.CloseAndJoinAsync()).WaitAsync(Guard);
                    else
                        await JoinStoppedAsync(_controlTask);
                });
            if (CertificateSwitch is not null)
                await AttemptAsync(() => Own(CertificateSwitch.DisposeAsync().AsTask()));
            if (Host is not null)
            {
                await AttemptAsync(async () =>
                {
                    TransportHostStopReport report = await Own(Host.StopAsync(Guard)).WaitAsync(Guard + Guard);
                    Assert.True(report.AllFinished, "Host 未 join 全部原 accept/连接任务。");
                });
                await AttemptAsync(() => Own(Host.DisposeAsync().AsTask()).WaitAsync(Guard + Guard));
            }
            for (int i = 0; i < _clients.Count; i++)
            {
                Task task = _clients[i];
                await AttemptAsync(async () =>
                {
                    await CompletedAsync(task);
                    if (!_assertedFailures.Contains(task)) await JoinStoppedAsync(task);
                    else _ = task.Exception; // 负例已由测试本体精确断言，仍须确认原任务已完成。
                    // 初次 Guard 失败后可能迟到的 Control 也必须关闭；原关闭任务继续进入本循环。
                    if (ReferenceEquals(task, _controlTask) && _controlTask!.IsCompletedSuccessfully)
                        _ = Own(_controlTask.Result.CloseAndJoinAsync());
                });
            }
            foreach (Task<EncodedFrame?> read in _reads)
                if (read.IsCompletedSuccessfully) read.Result?.Dispose(); // 包括 Guard 之后迟到交付的帧。
            await AttemptAsync(Source.JoinAsync);
            foreach (ObservedConnection server in _servers)
                await AttemptAsync(async () =>
                {
                    await CompletedAsync(server.Worker);
                    try { await server.Worker; }
                    catch (OperationCanceledException) when (server.Worker.IsCanceled) { }
                });
            await AttemptAsync(() =>
            {
                if (Host is not null)
                {
                    Assert.Empty(Host.LifecycleErrors.Snapshot);
                    Assert.Equal(0, Host.ActiveConnections);
                    Assert.Equal(0, Host.AdmittedConnections);
                }
                Assert.Empty(Context.SessionRegistry.Snapshot());
                Assert.Equal(0, Clock.TimerCount);
                return Task.CompletedTask;
            });
            _stop.Dispose();
            Certificate.Dispose();
            if (failures.Count != 0) throw new AggregateException("客户端视频 TLS 测试收尾失败。", failures);

            async Task AttemptAsync(Func<Task> action)
            {
                try { await action(); }
                catch (Exception error) { failures.Add(error); }
            }
        }
    }

    private static async Task JoinStoppedAsync(Task task)
    {
        await CompletedAsync(task);
        try { await task; }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException or SocketException)
        {
            // 仅用于 finally 中已请求停止的原 I/O；业务负例不得调用此帮助方法来判定成功。
        }
    }

    private sealed class SingleFrameSource : IVideoFrameSource
    {
        internal Guid SessionId { get; private set; }
        internal RecordingVideoOwner? Owner { get; private set; }
        internal Task<EncodedFrame?>? Pending { get; private set; }
        internal TaskCompletionSource Waiting { get; } = Signal();
        internal TaskCompletionSource Release { get; } = Signal();

        public ValueTask<EncodedFrame?> ReadNextAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            SessionId = sessionId;
            if (Owner is null)
            {
                Owner = new RecordingVideoOwner(5);
                VideoFrameTestData.Payload().CopyTo(Owner.Bytes, 0);
                return ValueTask.FromResult<EncodedFrame?>(VideoFrameTestData.Frame(Owner));
            }
            Pending = WaitAsync(cancellationToken);
            Waiting.TrySetResult();
            return new ValueTask<EncodedFrame?>(Pending);
        }

        private async Task<EncodedFrame?> WaitAsync(CancellationToken ct)
        {
            await Release.Task.WaitAsync(ct);
            return null;
        }

        internal async Task JoinAsync()
        {
            if (Pending is not null) await JoinStoppedAsync(Pending);
            if (Owner is not null) Assert.Equal(1, Owner.DisposeCalls);
        }
    }

    private sealed class LoopbackPolicy(bool rejectSecond) : ISubnetPolicy
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        internal TaskCompletionSource Rejected { get; } = Signal();
        public bool IsAllowedPeer(IPAddress localAddress, IPAddress remoteAddress)
        {
            int number = Interlocked.Increment(ref _calls);
            bool allowed = IPAddress.IsLoopback(localAddress) && IPAddress.IsLoopback(remoteAddress)
                && (!rejectSecond || number == 1);
            if (!allowed) Rejected.TrySetResult();
            return allowed;
        }
    }

    private sealed class ApproveGate : ILocalApprovalGate
    {
        public ValueTask<LocalApprovalDecision> RequestApprovalAsync(LocalApprovalRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LocalApprovalDecision(request.RequestId, LocalApprovalOutcome.Approved, SessionPermission.Control));
    }

    private sealed class FixedSecretStore : IAccessSecretStore
    {
        public Task<AccessSecret> LoadOrCreateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccessSecret(AccessKey.ToArray()));
        public Task<AccessSecret> RegenerateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("测试不轮换访问密钥。");
    }

    // Host 证书冻结：首条 TCP 透明转发到真实 Host，第二条在同一目标端口用另一证书执行真实 TLS。
    // 不终止/伪造 Control TLS，也没有客户端放行证书回调；生产 connector 自己裁决第二次 pin。
    private sealed class CertificateSwitchServer : IAsyncDisposable
    {
        private readonly int _hostPort;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentQueue<TcpClient> _sockets = new();
        private readonly ConcurrentQueue<Task> _pumps = new();
        private Task? _worker;
        internal X509Certificate2 Certificate { get; } = TestCertificateFactory.Create();
        internal TaskCompletionSource SecondAccepted { get; } = Signal();
        internal int Port { get; }

        internal CertificateSwitchServer(int hostPort)
        {
            _hostPort = hostPort;
            try
            {
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            }
            catch
            {
                _listener.Stop();
                _stop.Dispose();
                Certificate.Dispose();
                throw;
            }
        }

        internal void Start() => _worker = RunAsync();

        private async Task RunAsync()
        {
            TcpClient control = await _listener.AcceptTcpClientAsync(_stop.Token);
            _sockets.Enqueue(control);
            TcpClient upstream = new();
            _sockets.Enqueue(upstream);
            await upstream.ConnectAsync(IPAddress.Loopback, _hostPort, _stop.Token);
            _pumps.Enqueue(control.GetStream().CopyToAsync(upstream.GetStream(), _stop.Token));
            _pumps.Enqueue(upstream.GetStream().CopyToAsync(control.GetStream(), _stop.Token));
            TcpClient video = await _listener.AcceptTcpClientAsync(_stop.Token);
            _sockets.Enqueue(video);
            SecondAccepted.TrySetResult();
            using SslStream ssl = new(video.GetStream(), leaveInnerStreamOpen: true);
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = Certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    AllowTlsResume = false,
                    AllowRenegotiation = false,
                    ClientCertificateRequired = false,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                }, _stop.Token);
                // 某些 TLS 版本服务端先完成握手；不得收到任何应用 hello 字节。
                Assert.Equal(0, await ssl.ReadAsync(new byte[1], _stop.Token));
            }
            catch (Exception error) when (error is AuthenticationException or IOException)
            {
                // 对端 pin 拒绝可体现为握手 alert 或读 EOF/RST；客户端测试必须额外断言 pin-mismatch。
            }
        }

        public async ValueTask DisposeAsync()
        {
            List<Exception> errors = [];
            await AttemptAsync(() => _stop.CancelAsync());
            await AttemptAsync(() => { _listener.Stop(); return Task.CompletedTask; });
            foreach (TcpClient socket in _sockets)
                await AttemptAsync(() => { socket.Dispose(); return Task.CompletedTask; });
            if (_worker is not null) await AttemptAsync(() => JoinStoppedAsync(_worker));
            // accept 可能在取消前成功、取消后才入队；先 join 原 accept worker，再收迟到 socket。
            foreach (TcpClient socket in _sockets)
                await AttemptAsync(() => { socket.Dispose(); return Task.CompletedTask; });
            foreach (Task pump in _pumps) await AttemptAsync(() => JoinStoppedAsync(pump));
            _stop.Dispose();
            Certificate.Dispose();
            if (errors.Count != 0) throw new AggregateException("换证 loopback 收尾失败。", errors);

            async Task AttemptAsync(Func<Task> action)
            {
                try { await action(); }
                catch (Exception error) { errors.Add(error); }
            }
        }
    }
}
