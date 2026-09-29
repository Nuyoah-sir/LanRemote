using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

public sealed partial class VideoAttachTlsTests
{
    private static readonly Guid ServerDeviceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid ClientDeviceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly byte[] AccessKey = Convert.FromHexString("0F1E2D3C4B5A69788796A5B4C3D2E1F0");
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly TransportTimeouts Timeouts = new(
        connectTimeout: TimeSpan.FromSeconds(3),
        handshakeTimeout: TimeSpan.FromSeconds(3),
        lengthPrefixTimeout: TimeSpan.FromSeconds(5),
        payloadTimeout: TimeSpan.FromSeconds(5),
        helloTimeout: TimeSpan.FromSeconds(2),
        preAuthEnvelopeTimeout: TimeSpan.FromSeconds(10));

    private static async Task SendHelloAsync(
        AuthenticatedControlSession control, VideoPeer video, bool corruptProof = false, bool useWrongPin = false)
    {
        Assert.True(video.Client.Stream.IsAuthenticated);
        Assert.NotNull(video.Client.Stream.RemoteCertificate);
        // 从第二条真实 TLS 出示的证书独立取 pin，而非信任 hello 或目标配置里的字段。
        byte[] actualPin = SHA256.HashData(video.Client.Stream.RemoteCertificate.GetRawCertData());
        Assert.Equal(video.Client.Identity.PresentedCertSha256.ToArray(), actualPin);
        Assert.Equal(control.Identity.PresentedCertSha256.ToArray(), actualPin);
        byte[] proofPin = actualPin.ToArray();
        if (useWrongPin) proofPin[0] ^= 0x80;
        byte[] nonce = RandomNumberGenerator.GetBytes(16);
        byte[] domain = Encoding.UTF8.GetBytes("LANREMOTE-VIDEO-V1\0");
        byte[] networkUuid = Convert.FromHexString(control.SessionId.ToString("N"));
        Assert.Equal(19, domain.Length);
        Assert.Equal(16, networkUuid.Length);
        Assert.Equal(32, actualPin.Length);
        byte[] transcript = new byte[83];
        domain.CopyTo(transcript, 0);
        networkUuid.CopyTo(transcript, 19);
        nonce.CopyTo(transcript, 35);
        proofPin.CopyTo(transcript, 51);
        byte[] token = TestOnlyControlSessionSecrets.GetOwnedToken(control).ToArray();
        byte[] proof;
        try
        {
            Assert.Equal(32, token.Length);
            using HMACSHA256 hmac = new(token);
            proof = hmac.ComputeHash(transcript);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(token);
        }
        if (corruptProof) proof[0] ^= 0x80;
        byte[] payload = new VideoHelloFrame(control.SessionId, nonce, proof).Serialize();
        Assert.True(VideoHelloFrame.TryParse(payload, out _, out string? rejection), rejection);
        using CancellationTokenSource guard = new(Guard);
        await FrameWriter.WriteFrameAsync(video.Client.Stream, payload,
            TransportConstants.MaxPreAuthMessageBytes, guard.Token);
    }

    private static async Task ReadAckAsync(VideoPeer video, Guid sessionId)
    {
        using CancellationTokenSource guard = new(Guard);
        byte[] prefix = new byte[4];
        await video.Client.Stream.ReadExactlyAsync(prefix, guard.Token);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(prefix);
        // 若服务端错误地先发 LRVF，这里的长度立即失败，不会按它分配巨大的缓冲区。
        Assert.InRange(length, 1u, 4096u);
        byte[] payload = new byte[checked((int)length)];
        await video.Client.Stream.ReadExactlyAsync(payload, guard.Token);
        Assert.True(VideoAttachAckFrame.TryParse(payload, out VideoAttachAckFrame? ack, out string? rejection), rejection);
        Assert.NotNull(ack);
        Assert.Equal(sessionId, ack.SessionId);
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;
        Assert.Equal(new[] { "channel", "protocol", "sessionId", "type" },
            root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("video_attach_ack", root.GetProperty("type").GetString());
        Assert.Equal("video", root.GetProperty("channel").GetString());
        Assert.Equal(1, root.GetProperty("protocol").GetInt32());
        Assert.Equal(sessionId.ToString("D"), root.GetProperty("sessionId").GetString());
    }

    private static async Task ReadGoldenFrameAsync(VideoPeer video)
    {
        // 不使用 VideoFrameReader/Writer roundtrip 作 oracle；逐字节对照手写 LRVF 黄金向量。
        byte[] expected = VideoFrameTestData.Wire();
        byte[] received = new byte[expected.Length];
        using CancellationTokenSource guard = new(Guard);
        await video.Client.Stream.ReadExactlyAsync(received, guard.Token);
        Assert.Equal(expected, received);
    }

    private static async Task AssertClosedWithoutDataAsync(Stream stream)
    {
        using CancellationTokenSource guard = new(Guard);
        try
        {
            Assert.Equal(0, await stream.ReadAsync(new byte[1], guard.Token));
        }
        catch (IOException)
        {
            // 只在已成功握手的连接上调用；socket 先关时允许 TLS 无 close_notify 的 EOF/RST。
            guard.Token.ThrowIfCancellationRequested();
        }
    }

    private static async Task AssertRejectedAsync(VideoPeer video, VideoAttachStatus status)
    {
        Assert.Null(await video.Server.Finished.Task.WaitAsync(Guard));
        Assert.Equal(status, video.Server.Router.AttachStatus);
        Assert.Equal(1, video.Server.Router.AttachAttempts);
        Assert.Equal("video-attach-rejected", video.Server.Router.Rejection);
        // 拒绝路径连一个 ACK/视频字节都不能泄漏。
        await AssertClosedWithoutDataAsync(video.Client.Stream);
    }

    private static async Task AssertSourceCancelledAsync(TestFrameSource source, Task<EncodedFrame?> task)
    {
        await source.CancellationObserved.Task.WaitAsync(Guard);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(Guard));
        Assert.True(task.IsCanceled);
        Assert.True(source.LifetimeToken.IsCancellationRequested);
    }

    private static IEnumerable<Exception> LeafErrors(Exception error) => error is AggregateException aggregate
        ? aggregate.Flatten().InnerExceptions : new[] { error };

    private static async Task WaitForConditionAsync(Func<bool> condition)
    {
        // 连接登记计数没有事件：仅在 TCS/网络步骤之后观察它，使用真实单调 guard，不睡眠猜时序。
        long started = Stopwatch.GetTimestamp();
        while (!condition())
        {
            Assert.True(Stopwatch.GetElapsedTime(started) < Guard, "等待服务端登记/回收计数超时。");
            await Task.Yield();
        }
    }

    private sealed record VideoPeer(TlsConnection Client, ObservedConnection Server);

    private sealed class ObservedConnection(
        AcceptedConnection connection, FirstFrameRouter router, CancellationToken handlerToken)
    {
        internal AcceptedConnection Connection { get; } = connection;
        internal FirstFrameRouter Router { get; } = router;
        internal CancellationToken HandlerToken { get; } = handlerToken;
        internal ControlPreAuthResult? LegacyResult { get; set; }
        internal TaskCompletionSource<Exception?> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // 每个用例的 await using 都生成 finally：所有连接任务（包括迟到结果）、源任务和 Host 均由此回收。
    private sealed class TlsScenario : IAsyncDisposable
    {
        private readonly X509Certificate2 _certificate = TestCertificateFactory.Create();
        private readonly CancellationTokenSource _clientsStop = new();
        private readonly Channel<ObservedConnection> _arrivals = Channel.CreateUnbounded<ObservedConnection>();
        private readonly ConcurrentQueue<ObservedConnection> _servers = new();
        private readonly List<Task<TlsConnection>> _videoClients = [];
        private readonly ApproveGate _approval = new();
        private readonly bool _legacyVideo;
        private Task<AuthenticatedControlSession>? _controlClient;
        private int _connectionNumber;

        internal TlsScenario(
            int maxConnectionsPerAddress = 2, bool ignoreCancellation = false,
            bool secondFrame = false, Exception? cancellationFailure = null, bool legacyVideo = false,
            OperationCanceledException? sourceFailure = null)
        {
            _legacyVideo = legacyVideo;
            Source = new TestFrameSource(ignoreCancellation, secondFrame, cancellationFailure, sourceFailure);
            Context = new ControlAuthContext
            {
                ServerDeviceId = ServerDeviceId,
                AccessSecretStore = new FixedSecretStore(),
                FailedAuthLimiter = new FailedAuthLimiter(),
                PendingApprovalLimiter = new ConnectionAdmissionLimiter(3, 1),
                ApprovalGate = _approval,
                SessionRegistry = new SessionRegistry(),
                TimeProvider = Clock,
                Options = new ControlAuthOptions
                {
                    RequireLocalApproval = true,
                    MachineWindow = TimeSpan.FromSeconds(5),
                    ApprovalWindow = TimeSpan.FromSeconds(5),
                    FailureDelayMin = TimeSpan.Zero,
                    FailureDelayMax = TimeSpan.Zero,
                    VideoAttachExpiresInMs = 15_000,
                },
            };
            try
            {
                using TcpListener reservation = new(IPAddress.Loopback, 0);
                reservation.Start();
                int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
                reservation.Stop();
                Assert.True(ConnectionTarget.TryCreate(ServerDeviceId, IPAddress.Loopback, port,
                    TestCertificateFactory.Fingerprint(_certificate), out ConnectionTarget? target));
                Target = target!;
                Host = new TransportHost(new[] { IPAddress.Loopback }, new LoopbackOnlyPolicy(), _certificate,
                    HandleAsync, new TransportHostOptions
                    {
                        Port = port,
                        Timeouts = Timeouts,
                        MaxConnectionsPerAddress = maxConnectionsPerAddress,
                    });
            }
            catch
            {
                _clientsStop.Dispose();
                _certificate.Dispose();
                throw;
            }
        }

        internal TransportHost Host { get; }
        internal ConnectionTarget Target { get; }
        internal ControlAuthContext Context { get; }
        // 只注入服务端，不改变 TLS 客户端的真实证书有效期校验或网络等待 guard。
        internal ManualDeadlineClock Clock { get; } = new(DateTimeOffset.UtcNow);
        internal TestFrameSource Source { get; }
        internal ObservedConnection ControlServer { get; private set; } = null!;

        internal void Start()
        {
            TransportHostStartResult result = Host.Start();
            Assert.True(result.IsListening);
            Assert.Empty(result.Failures);
        }

        private async Task HandleAsync(AcceptedConnection connection, CancellationToken ct)
        {
            int number = Interlocked.Increment(ref _connectionNumber);
            ObservedConnection observed = new(connection, new FirstFrameRouter(Context, Timeouts, Source), ct);
            _servers.Enqueue(observed);
            _arrivals.Writer.TryWrite(observed);
            Exception? failure = null;
            try
            {
                Assert.True(connection.Stream.IsAuthenticated);
                Assert.True(connection.Stream.IsEncrypted);
                Assert.True(connection.HasCloseAuthority);
                if (_legacyVideo && number > 1)
                    observed.LegacyResult = await new ControlPreAuthSession().RunAsync(connection, Timeouts, ct);
                else
                    await observed.Router.RunAsync(connection, ct);
            }
            catch (OperationCanceledException error) when (ct.IsCancellationRequested &&
                !ReferenceEquals(error, Source.SourceFailure))
            {
                throw;
            }
            catch (Exception error)
            {
                failure = error;
                // 转交主测试，但仍重新抛给 Host；不能吞掉 handler 断言或取消回调故障。
                throw;
            }
            finally
            {
                observed.Finished.TrySetResult(failure);
            }
        }

        internal async Task<AuthenticatedControlSession> OpenControlAsync()
        {
            Assert.Null(_controlClient);
            _controlClient = ConnectControlAsync();
            AuthenticatedControlSession control = await _controlClient.WaitAsync(Guard);
            ControlServer = await NextServerAsync();
            // auth_success 客户端已收到与服务端登记之间有调度间隙；不能等待 holding 的 RunAsync 返回。
            await WaitForConditionAsync(() => Context.SessionRegistry.ActiveSessionCount == 1);
            AssertControlAlive(control);
            Assert.Equal(1, _approval.RequestCount);
            Assert.Equal(control.SessionId, Assert.IsType<LocalApprovalRequest>(_approval.Request).SessionId);
            Assert.Equal(32, TestOnlyControlSessionSecrets.GetOwnedToken(control).Length);
            Assert.True(control.Identity.PinsMatch);
            Assert.Equal(Target.ExpectedCertSha256.ToArray(), control.Identity.PresentedCertSha256.ToArray());
            return control;
        }

        private async Task<AuthenticatedControlSession> ConnectControlAsync()
        {
            using CancellationTokenSource guard = CancellationTokenSource.CreateLinkedTokenSource(_clientsStop.Token);
            guard.CancelAfter(Guard);
            return await new ControlClientConnector().ConnectAndAuthenticateAsync(
                Target, ClientDeviceId, "双 TLS 视频测试客户端", AccessKey, SessionPermission.Control,
                new ControlClientAuthOptions
                {
                    MachineWindow = TimeSpan.FromSeconds(5),
                    ApprovalWindow = TimeSpan.FromSeconds(5),
                }, Timeouts, cancellationToken: guard.Token);
        }

        internal async Task<VideoPeer> OpenVideoAsync()
        {
            Task<TlsConnection> connecting = ConnectVideoAsync();
            _videoClients.Add(connecting);
            TlsConnection client = await connecting.WaitAsync(Guard);
            ObservedConnection server = await NextServerAsync();
            Assert.NotNull(client.LocalEndPoint);
            Assert.Equal(client.LocalEndPoint.Port, server.Connection.Security.RemotePort);
            Assert.True(client.Identity.PinsMatch);
            Assert.Equal(Target.ExpectedCertSha256.ToArray(), client.Identity.PresentedCertSha256.ToArray());
            Assert.True(client.Stream.IsAuthenticated);
            Assert.True(client.Stream.IsEncrypted);
            Assert.Equal(client.NegotiatedProtocol, server.Connection.Stream.SslProtocol);
            Assert.NotNull(server.Connection.Stream.LocalCertificate);
            Assert.Equal(client.Identity.PresentedCertSha256.ToArray(),
                SHA256.HashData(server.Connection.Stream.LocalCertificate.GetRawCertData()));
            return new VideoPeer(client, server);
        }

        private async Task<TlsConnection> ConnectVideoAsync()
        {
            using CancellationTokenSource guard = CancellationTokenSource.CreateLinkedTokenSource(_clientsStop.Token);
            guard.CancelAfter(Guard);
            // 生产连接器验证冻结目标的真实 pin；没有无条件放行证书的回调。
            return await new TlsClientConnector().ConnectAsync(Target, Timeouts, cancellationToken: guard.Token);
        }

        private async Task<ObservedConnection> NextServerAsync()
        {
            using CancellationTokenSource guard = new(Guard);
            return await _arrivals.Reader.ReadAsync(guard.Token);
        }

        internal async Task<VideoPeer> AttachAsync(AuthenticatedControlSession control)
        {
            VideoPeer video = await OpenVideoAsync();
            await AssertDistinctLiveConnectionsAsync(video);
            await SendHelloAsync(control, video);
            await ReadAckAsync(video, control.SessionId);
            await ReadGoldenFrameAsync(video);
            Task<EncodedFrame?> pending = await Source.Blocked.Task.WaitAsync(Guard);
            Assert.False(pending.IsCompleted);
            Assert.Equal(VideoAttachStatus.Attached, video.Server.Router.AttachStatus);
            Assert.Equal(2, Source.ReadCount);
            return video;
        }

        internal async Task AssertDistinctLiveConnectionsAsync(VideoPeer video)
        {
            await WaitForConditionAsync(() => Host.ActiveConnections >= 2);
            Assert.True(Host.AdmittedConnections >= 2);
            Assert.False(ControlServer.Finished.Task.IsCompleted);
            Assert.False(video.Server.Finished.Task.IsCompleted);
            ConnectionSecurityContext control = ControlServer.Connection.Security;
            ConnectionSecurityContext second = video.Server.Connection.Security;
            Assert.NotEqual(control.ConnectionId, second.ConnectionId);
            Assert.NotEqual(control.RemotePort, second.RemotePort);
            Assert.Equal(IPAddress.Loopback, control.LocalAddress);
            Assert.Equal(IPAddress.Loopback, second.LocalAddress);
            Assert.Equal(IPAddress.Loopback, control.RemoteAddress);
            Assert.Equal(IPAddress.Loopback, second.RemoteAddress);
            Assert.Equal(control.ServerCertificateSha256.ToArray(), second.ServerCertificateSha256.ToArray());
        }

        internal void AssertControlAlive(AuthenticatedControlSession control)
        {
            ControlSessionSummary summary = Assert.Single(Context.SessionRegistry.Snapshot());
            Assert.Equal(control.SessionId, summary.SessionId);
            Assert.Equal(ControlServer.Connection.Security.ConnectionId, summary.ConnectionId);
            Assert.Equal(ClientDeviceId, summary.ClientDeviceId);
            Assert.Equal(SessionPermission.Control, summary.GrantedPermission);
            Assert.Equal(SessionPermission.Control, control.GrantedPermission);
            Assert.False(ControlServer.Finished.Task.IsCompleted);
        }

        internal Task WaitForConnectionCountAsync(int count) => WaitForConditionAsync(() =>
            Host.ActiveConnections == count && Host.AdmittedConnections == count);

        internal async Task AssertStoppedAsync()
        {
            TransportHostStopReport report = await Host.StopAsync(Guard).WaitAsync(Guard + Guard);
            Assert.True(report.AllFinished, $"停机后尚有 {report.UnfinishedConnections} 条连接未结束。");
            Assert.Equal(0, Host.ActiveConnections);
            Assert.Equal(0, Host.AdmittedConnections);
            Assert.Empty(Context.SessionRegistry.Snapshot());
        }

        public async ValueTask DisposeAsync()
        {
            List<Exception> failures = [];
            // 先解除所有源闸门，确保异常路径也不会因无视取消的源卡住 Stop/join。
            Source.ReleaseAll();
            await AttemptAsync(() => _clientsStop.CancelAsync());
            foreach (Task<TlsConnection> task in _videoClients)
            {
                await AttemptAsync(async () =>
                {
                    try { (await task.WaitAsync(Guard)).Dispose(); }
                    catch (Exception error) when (task.IsCompleted && !task.IsCompletedSuccessfully &&
                        error is AuthenticationException or OperationCanceledException or IOException or SocketException)
                    {
                        // 建连失败已由调用点报告；这里不丢弃迟到成功的连接，也不吞 Timeout/断言。
                    }
                });
            }
            if (_controlClient is not null)
            {
                await AttemptAsync(async () =>
                {
                    try { (await _controlClient.WaitAsync(Guard)).Dispose(); }
                    catch (Exception error) when (_controlClient.IsCompleted && !_controlClient.IsCompletedSuccessfully &&
                        error is AuthenticationException or OperationCanceledException or IOException or SocketException)
                    {
                    }
                });
            }
            await AttemptAsync(AssertStoppedAsync);
            // 等真实的 ReadNextAsync 返回任务，不以 CancellationObserved/Blocked 信号代替 join。
            await AttemptAsync(Source.JoinAsync);
            foreach (ObservedConnection server in _servers)
            {
                await AttemptAsync(async () =>
                {
                    Exception? error = await server.Finished.Task.WaitAsync(Guard);
                    if (error is not null) AssertExpectedFailure(error);
                });
            }
            await AttemptAsync(() =>
            {
                foreach (HostLifecycleError diagnostic in Host.LifecycleErrors.Snapshot)
                {
                    Assert.Equal(HostLifecycleErrorKind.Handler, diagnostic.Kind);
                    AssertExpectedFailure(diagnostic.Error);
                }
                return Task.CompletedTask;
            });
            await AttemptAsync(() => Host.DisposeAsync().AsTask().WaitAsync(Guard + Guard));
            _clientsStop.Dispose();
            _certificate.Dispose();
            if (failures.Count != 0) throw new AggregateException("双 TLS 测试收尾失败。", failures);

            async Task AttemptAsync(Func<Task> cleanup)
            {
                try { await cleanup(); }
                catch (Exception error) { failures.Add(error); }
            }

            void AssertExpectedFailure(Exception error)
            {
                Exception? expected = Source.SourceFailure ?? Source.CancellationFailure;
                Assert.NotNull(expected);
                Assert.Same(expected, Assert.Single(LeafErrors(error)));
            }
        }
    }

    private sealed class TestFrameSource(
        bool ignoreCancellation, bool secondFrame, Exception? cancellationFailure, OperationCanceledException? sourceFailure)
        : IVideoFrameSource
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _nextFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseSourceFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseCancellationCallback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<Task<EncodedFrame?>> _reads = new();
        private CancellationTokenRegistration _sourceFailureRegistration;
        private int _readCount;
        private int _cleanup;

        internal int ReadCount => Volatile.Read(ref _readCount);
        internal Exception? CancellationFailure { get; } = cancellationFailure;
        internal OperationCanceledException? SourceFailure { get; } = sourceFailure;
        internal bool? LifetimeCancelledAtSourceFailure { get; private set; }
        internal TaskCompletionSource CancellationCallbackEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken LifetimeToken { get; private set; }
        internal ConcurrentQueue<Guid> SessionIds { get; } = new();
        internal ConcurrentQueue<RecordingVideoOwner> Owners { get; } = new();
        internal TaskCompletionSource<Task<EncodedFrame?>> Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<Task<EncodedFrame?>> WaitingAgain { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<EncodedFrame?> ReadNextAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            SessionIds.Enqueue(sessionId);
            LifetimeToken = cancellationToken;
            int read = Interlocked.Increment(ref _readCount);
            Task<EncodedFrame?> task = read == 1
                ? Task.FromResult<EncodedFrame?>(CreateFrame())
                : WaitForFrameAsync(read, cancellationToken);
            _reads.Enqueue(task);
            if (read == 2) Blocked.TrySetResult(task);
            if (read == 3) WaitingAgain.TrySetResult(task);
            return new ValueTask<EncodedFrame?>(task);
        }

        private async Task<EncodedFrame?> WaitForFrameAsync(int read, CancellationToken ct)
        {
            if (SourceFailure is not null)
            {
                // 必须跨源故障保留注册；回调只卡住 Router 的 CancelAsync join，正常返回且不注入异常。
                _sourceFailureRegistration = ct.Register(() =>
                {
                    CancellationCallbackEntered.TrySetResult();
                    _releaseCancellationCallback.Task.GetAwaiter().GetResult();
                });
                await _releaseSourceFailure.Task;
                LifetimeCancelledAtSourceFailure = ct.IsCancellationRequested;
                throw SourceFailure;
            }

            using CancellationTokenRegistration registration = ct.Register(() =>
            {
                CancellationObserved.TrySetResult();
                if (CancellationFailure is not null) throw CancellationFailure;
            });
            if (ignoreCancellation)
            {
                await _release.Task;
                return null;
            }

            // 由同一个回调发信号并注入故障，避免独立 WaitAsync(ct) 回调先移除故障回调的竞态。
            if (secondFrame && read == 2)
                await Task.WhenAny(_nextFrame.Task, _release.Task, CancellationObserved.Task);
            else
                await Task.WhenAny(_release.Task, CancellationObserved.Task);
            ct.ThrowIfCancellationRequested();
            return secondFrame && read == 2 && Volatile.Read(ref _cleanup) == 0 ? CreateFrame() : null;
        }

        private EncodedFrame CreateFrame()
        {
            // Jpeg 只是线上 codec 标签；这五个不透明字节不声称能被 JPEG 解码器接受。
            RecordingVideoOwner owner = new(5);
            VideoFrameTestData.Payload().CopyTo(owner.Bytes, 0);
            Owners.Enqueue(owner);
            return VideoFrameTestData.Frame(owner);
        }

        internal void ReleaseNextFrame() => _nextFrame.TrySetResult();
        internal void ReleaseSourceFailure() => _releaseSourceFailure.TrySetResult();

        internal void ReleaseAll()
        {
            Volatile.Write(ref _cleanup, 1);
            _release.TrySetResult();
            _nextFrame.TrySetResult();
            _releaseSourceFailure.TrySetResult();
            _releaseCancellationCallback.TrySetResult();
        }

        internal async Task JoinAsync()
        {
            await _sourceFailureRegistration.DisposeAsync().AsTask().WaitAsync(Guard);
            foreach (Task<EncodedFrame?> read in _reads)
            {
                try { await read.WaitAsync(Guard); }
                catch (OperationCanceledException) when (read.IsCanceled) { }
            }
            Assert.All(Owners, owner => Assert.Equal(1, owner.DisposeCalls));
        }
    }

    private sealed class ApproveGate : ILocalApprovalGate
    {
        internal int RequestCount { get; private set; }
        internal LocalApprovalRequest? Request { get; private set; }

        public ValueTask<LocalApprovalDecision> RequestApprovalAsync(LocalApprovalRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            RequestCount++;
            return ValueTask.FromResult(new LocalApprovalDecision(
                request.RequestId, LocalApprovalOutcome.Approved, SessionPermission.Control));
        }
    }

    private sealed class FixedSecretStore : IAccessSecretStore
    {
        public Task<AccessSecret> LoadOrCreateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccessSecret(AccessKey.ToArray()));

        public Task<AccessSecret> RegenerateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("测试不轮换密钥。");
    }

    private sealed class LoopbackOnlyPolicy : ISubnetPolicy
    {
        public bool IsAllowedPeer(IPAddress localAddress, IPAddress remoteAddress) =>
            IPAddress.IsLoopback(localAddress) && IPAddress.IsLoopback(remoteAddress);
    }
}
