using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using LanRemote.Transport;

namespace LanRemote.Acceptance.Tests;

[Collection(AcceptanceStageFiveCollection.Name)]
public sealed class AcceptanceTlsIntegrationTests
{
    [Theory(Timeout = 20_000)]
    [InlineData(false)]
    [InlineData(true)]
    public Task Approved_Downgrade_Requires_Observed_Hold_And_Natural_Release(bool hostForced) =>
        RunScenarioAsync(async scenario =>
        {
            Task<AuthenticatedControlSession> connecting = scenario.Connect();
            LocalApprovalSnapshot request = await scenario.AssertPendingAsync(connecting);
            Assert.True(scenario.Inbox.TrySubmit(request.RequestId, request.Generation,
                LocalApprovalOutcome.Approved, SessionPermission.ViewOnly));

            // Connect 返回意味着客户端已读完终帧并验证 serverProof，不以 Host 写帧代替。
            AuthenticatedControlSession client = await connecting.WaitAsync(scenario.Token);
            Assert.Equal(request.Request.SessionId, client.SessionId);
            Assert.Equal(request.Request.ShortCode, client.ShortCode);
            Assert.Equal(SessionPermission.ViewOnly, client.GrantedPermission);
            Assert.Equal(scenario.ServerDeviceId, client.Identity.DeviceId);
            Assert.True(client.Identity.PinsMatch);
            Assert.Equal(scenario.Target.ExpectedCertSha256.ToArray(), client.Identity.PresentedCertSha256.ToArray());

            scenario.BeginClientHold();
            ControlSessionSummary registered = await scenario.Registered.Task.WaitAsync(scenario.Token);
            Assert.Equal(client.SessionId, registered.SessionId);
            Assert.Equal(request.Request.ConnectionId, registered.ConnectionId);
            Assert.Equal(scenario.ClientDeviceId, registered.ClientDeviceId);
            Assert.Equal(SessionPermission.ViewOnly, registered.GrantedPermission);
            Assert.Equal(IPAddress.Loopback, registered.RemoteAddress);
            TimeSpan observedSpan = await scenario.Held.Task.WaitAsync(scenario.Token);
            Assert.True(observedSpan >= TimeSpan.FromSeconds(4));
            Assert.False(scenario.HandlerFinished.Task.IsCompleted);
            Assert.Equal(1, scenario.Context.SessionRegistry.ActiveSessionCount);
            Assert.Equal(1, scenario.Host.ActiveConnections);
            Assert.Equal(1, scenario.Counters.Snapshot().Active);
            Assert.Equal(0, scenario.Context.PendingApprovalLimiter.GlobalInUse);

            if (hostForced)
            {
                ControlSessionSummary stopping = Assert.Single(await scenario.StopHostAsync());
                Assert.Equal(client.SessionId, stopping.SessionId);
                // 直到真实 Host Stop 已完成仍由客户端持有会话，不能冒充客户端先释放。
                client.Dispose();
            }
            else
            {
                client.Dispose();
                await scenario.HandlerFinished.Task.WaitAsync(scenario.Token);
                Assert.Empty(await scenario.StopHostAsync());
            }

            await scenario.AssertCleanAsync(hostForced
                ? "authenticated-host-forced-close"
                : "authenticated-ended-unregistered");
            AcceptanceOutcome expected = hostForced ? AcceptanceOutcome.PreconditionUnmet : AcceptanceOutcome.Pass;
            Assert.Equal(expected, scenario.Evaluate());
            Assert.Equal(expected, scenario.Run.Complete(scenario.Evaluate(), "真实 TLS 会话证据"));

            string line = Assert.Single(scenario.Run.Log.ReadFrom(0, 100)
                .Where(value => value.StartsWith("[HOST][SESSION] ", StringComparison.Ordinal)));
            Assert.Contains($"sessionId={client.SessionId}", line);
            Assert.Contains("authenticated=True ", line);
            Assert.Contains("deregisteredAtRunEnd=True ", line);
            Assert.Contains($"hostForcedClose={hostForced} ", line);
            Assert.Contains($"evidence={expected.Code()} ", line);
            Assert.True(ReadMeasurement(line, "observedSpanMs") >= 4000,
                "不能靠保持计时冒充真实 registry 采样跨度。");
            Assert.True(ReadMeasurement(line, "observedSamples") >= 2);
            Assert.InRange(ReadMeasurement(line, "maxObservedGapMs"), 0, 500);
            Assert.InRange(ReadMeasurement(line, "endObservationGapMs"), 0, 500);
            Assert.Contains("observationInterrupted=False ", line);
        });

    [Theory(Timeout = 20_000)]
    [InlineData(false)]
    [InlineData(true)]
    public Task Wrong_Key_Or_Explicit_Denial_Never_Registers_And_Consumes_One_Terminal_Bucket(bool deny) =>
        RunScenarioAsync(async scenario =>
        {
            Task<AuthenticatedControlSession> connecting = scenario.Connect(wrongKey: !deny);
            if (deny)
            {
                LocalApprovalSnapshot request = await scenario.AssertPendingAsync(connecting);
                Assert.True(scenario.Inbox.TrySubmit(request.RequestId, request.Generation, LocalApprovalOutcome.Denied));
                Assert.False(scenario.Inbox.TrySubmit(request.RequestId, request.Generation,
                    LocalApprovalOutcome.Approved, SessionPermission.Control));
            }

            // 先观察客户端消费 authentication_failed；先停 Host 会把拒绝帧抢成 EOF。
            ControlClientAuthenticationException error = await Assert.ThrowsAsync<ControlClientAuthenticationException>(
                () => connecting.WaitAsync(scenario.Token));
            Assert.Equal("client-remote-authentication-failed", error.Rejection);
            Assert.Equal("远端拒绝了认证或审批请求。", error.DisplayMessage);
            await scenario.HandlerFinished.Task.WaitAsync(scenario.Token);
            Assert.Empty(await scenario.StopHostAsync());
            await scenario.AssertCleanAsync("auth-rejected:" + (deny
                ? ControlAuthSession.RejectApprovalDenied : ControlAuthSession.RejectProofMismatch));
            Assert.Equal(deny ? 1 : 0, scenario.PendingNotifications);
            Assert.Equal(deny, scenario.InboxObserved.Task.IsCompletedSuccessfully);
            Assert.False(scenario.Registered.Task.IsCompleted);
            Assert.Equal(0, scenario.MaximumRegistered);
            Assert.Equal(deny ? 0 : 1, scenario.Context.FailedAuthLimiter.CountRecentFailures(IPAddress.Loopback));
            Assert.Equal(AcceptanceOutcome.PreconditionUnmet, scenario.Evaluate());
            Assert.DoesNotContain("evidence=PASS ", scenario.Run.Log.All);
        });

    private static double ReadMeasurement(string line, string name)
    {
        string prefix = name + "=";
        string field = Assert.Single(line.Split(' ').Where(value => value.StartsWith(prefix, StringComparison.Ordinal)));
        return double.Parse(field[prefix.Length..], CultureInfo.InvariantCulture);
    }

    private static async Task RunScenarioAsync(Func<TlsScenario, Task> test)
    {
        using AcceptanceTestDirectory directory = new();
        using RandomSecretStore store = new();
        using X509Certificate2 certificate = CreateCertificate();
        await using TlsScenario scenario = new(directory.Path, store, certificate);
        scenario.Start();
        await test(scenario);
    }

    private static X509Certificate2 CreateCertificate()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new("CN=lanremote-acceptance-test", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new(PeerCertificateValidator.ServerAuthEkuOid) }, false));
        using X509Certificate2 selfSigned = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        byte[] pfx = selfSigned.Export(X509ContentType.Pfx, password);
        try
        {
            // Schannel 使用 PFX 往返后的私钥，不能直接使用 ephemeral 自签结果。
            return X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.DefaultKeySet);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    private sealed class RandomSecretStore : IAccessSecretStore, IDisposable
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(16);
        public byte[] CopyKey() => _key.ToArray();
        public Task<AccessSecret> LoadOrCreateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AccessSecret(CopyKey()));
        }
        public Task<AccessSecret> RegenerateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("测试不轮换密钥，也不访问真实存储。");
        public void Dispose() => CryptographicOperations.ZeroMemory(_key);
    }

    private sealed class LoopbackPolicy : ISubnetPolicy
    {
        public bool IsAllowedPeer(IPAddress localAddress, IPAddress remoteAddress) =>
            localAddress.Equals(IPAddress.Loopback) && remoteAddress.Equals(IPAddress.Loopback);
    }

    private sealed class TlsScenario : IAsyncDisposable
    {
        // 主流程最多 12 秒；finally 共享 5 秒预算，测试外层硬 guard 为 20 秒。
        private readonly CancellationTokenSource _operation = new(TimeSpan.FromSeconds(12));
        private readonly CancellationTokenSource _clientStop = new();
        private readonly CancellationTokenSource _samplingStop = new();
        private readonly RandomSecretStore _store;
        private readonly HostSessionEvidence _evidence = new(4);
        private readonly TransportTimeouts _timeouts = new(
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4));
        private readonly TaskCompletionSource<ControlClientApprovalPending> _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task<AuthenticatedControlSession>? _client;
        private Task _sampling = Task.CompletedTask;
        private Task<TransportHostStopReport>? _stop;
        private IReadOnlyList<ControlSessionSummary>? _stopSnapshot;
        private long _holdStarted;
        private int _entered;
        private int _pendingNotifications;
        private int _maximumRegistered;

        internal TlsScenario(string directory, RandomSecretStore store, X509Certificate2 certificate)
        {
            _store = store;
            Run = AcceptanceRun.Create(directory, "tls-test");
            Context = new ControlAuthContext
            {
                ServerDeviceId = ServerDeviceId,
                AccessSecretStore = store,
                FailedAuthLimiter = new FailedAuthLimiter(),
                PendingApprovalLimiter = new ConnectionAdmissionLimiter(3, 1),
                SessionRegistry = new SessionRegistry(),
                ApprovalGate = Inbox,
                Options = new ControlAuthOptions
                {
                    RequireLocalApproval = true,
                    MachineWindow = TimeSpan.FromSeconds(3),
                    ApprovalWindow = TimeSpan.FromSeconds(4),
                    FailureDelayMin = TimeSpan.Zero,
                    FailureDelayMax = TimeSpan.Zero,
                },
            };
            Counters = new HostRole.HostCounters(Context, _evidence);
            using TcpListener reservation = new(IPAddress.Loopback, 0);
            reservation.Start();
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            Assert.True(ConnectionTarget.TryCreate(ServerDeviceId, IPAddress.Loopback, port,
                Convert.ToHexString(SHA256.HashData(certificate.RawData)), out ConnectionTarget? target));
            Target = target!;
            Host = new TransportHost(new[] { IPAddress.Loopback }, new LoopbackPolicy(), certificate,
                HandleAsync, new TransportHostOptions
                {
                    Port = port, Timeouts = _timeouts, MaxConnections = 4,
                    ShutdownTimeout = TimeSpan.FromSeconds(1),
                });
        }

        internal Guid ServerDeviceId { get; } = Guid.NewGuid();
        internal Guid ClientDeviceId { get; } = Guid.NewGuid();
        internal AcceptanceRun Run { get; }
        internal LocalApprovalInbox Inbox { get; } = new();
        internal ControlAuthContext Context { get; }
        internal HostRole.HostCounters Counters { get; }
        internal TransportHost Host { get; }
        internal ConnectionTarget Target { get; }
        internal CancellationToken Token => _operation.Token;
        internal int PendingNotifications => Volatile.Read(ref _pendingNotifications);
        internal int MaximumRegistered => Volatile.Read(ref _maximumRegistered);
        internal TaskCompletionSource HandlerFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<LocalApprovalSnapshot> InboxObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<ControlSessionSummary> Registered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<TimeSpan> Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Start()
        {
            Assert.False(Run.Log.FileUnavailable);
            TransportHostStartResult start = Host.Start();
            Assert.True(start.IsListening);
            Assert.Empty(start.Failures);
            Assert.Equal(IPAddress.Loopback, Assert.Single(start.BoundAddresses));
            _sampling = SampleAsync();
        }

        private async Task HandleAsync(AcceptedConnection connection, CancellationToken cancellationToken)
        {
            Interlocked.Exchange(ref _entered, 1);
            try
            {
                Assert.True(connection.Stream.IsAuthenticated);
                Assert.True(connection.Stream.IsEncrypted);
                Assert.Equal(IPAddress.Loopback, connection.RemoteAddress);
                // 唯一真实处理链；这里不重写 pre-auth、认证、登记或结束桶。
                await Counters.HandleAsync(Run, connection, _timeouts, cancellationToken);
                HandlerFinished.TrySetResult();
            }
            catch (Exception error)
            {
                // Transport 会收住 handler 异常；独立信号保证断言仍被主测试/finally 观察。
                HandlerFinished.TrySetException(error);
                throw;
            }
        }

        internal Task<AuthenticatedControlSession> Connect(bool wrongKey = false)
        {
            Assert.Null(_client);
            _client = ConnectCoreAsync(wrongKey);
            return _client;
        }

        private async Task<AuthenticatedControlSession> ConnectCoreAsync(bool wrongKey)
        {
            byte[] key = _store.CopyKey();
            if (wrongKey) { key[0] ^= 0x80; }
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(Token, _clientStop.Token);
            try
            {
                return await new ControlClientConnector().ConnectAndAuthenticateAsync(
                    Target, ClientDeviceId, "阶段5真实 TLS 客户端", key, SessionPermission.Control,
                    new ControlClientAuthOptions
                    {
                        MachineWindow = TimeSpan.FromSeconds(3),
                        ApprovalWindow = TimeSpan.FromSeconds(4),
                        ApprovalPending = pending =>
                        {
                            Interlocked.Increment(ref _pendingNotifications);
                            _pending.TrySetResult(pending);
                        },
                    }, _timeouts, cancellationToken: linked.Token);
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }

        internal async Task<LocalApprovalSnapshot> AssertPendingAsync(Task<AuthenticatedControlSession> connecting)
        {
            ControlClientApprovalPending pending = await _pending.Task.WaitAsync(Token);
            LocalApprovalSnapshot snapshot = await InboxObserved.Task.WaitAsync(Token);
            Assert.Equal(snapshot.Request.SessionId, pending.SessionId);
            Assert.Equal(snapshot.Request.ShortCode, pending.ShortCode);
            Assert.Equal(SessionPermission.Control, snapshot.Request.RequestedPermission);
            Assert.Equal(ClientDeviceId, snapshot.Request.ClientDeviceId);
            Assert.Equal(1, PendingNotifications);
            Assert.False(connecting.IsCompleted);
            Assert.Equal(snapshot, Assert.Single(Inbox.GetSnapshot()));
            Assert.Equal(1, Context.PendingApprovalLimiter.GlobalInUse);
            Assert.Equal(0, Context.SessionRegistry.ActiveSessionCount);
            Assert.Equal(1, Counters.Snapshot().Active);
            return snapshot;
        }

        internal void BeginClientHold() => Interlocked.Exchange(ref _holdStarted, Stopwatch.GetTimestamp());

        private async Task SampleAsync()
        {
            long? firstPositive = null;
            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(100));
            while (await timer.WaitForNextTickAsync(_samplingStop.Token))
            {
                // 唯一采样入口，只读真实 registry；绝不注入样本或合成单调时间。
                _evidence.Sample(Context.SessionRegistry);
                IReadOnlyList<ControlSessionSummary> snapshot = Context.SessionRegistry.Snapshot();
                Interlocked.Exchange(ref _maximumRegistered, Math.Max(MaximumRegistered, snapshot.Count));
                foreach (LocalApprovalSnapshot approval in Inbox.GetSnapshot()) { InboxObserved.TrySetResult(approval); }
                if (snapshot.Count == 1)
                {
                    long now = Stopwatch.GetTimestamp();
                    firstPositive ??= now;
                    Registered.TrySetResult(snapshot[0]);
                    long holdStarted = Interlocked.Read(ref _holdStarted);
                    if (holdStarted != 0 && Stopwatch.GetElapsedTime(holdStarted, now) >= TimeSpan.FromSeconds(5))
                    {
                        Held.TrySetResult(Stopwatch.GetElapsedTime(firstPositive.Value, now));
                    }
                }
            }
        }

        private Task<TransportHostStopReport> StartStop()
        {
            if (_stop is null)
            {
                _stopSnapshot = _evidence.BeginHostStop(Context.SessionRegistry);
                Inbox.Stop();
                _stop = Host.StopAsync(TimeSpan.FromSeconds(1));
            }
            return _stop;
        }

        internal async Task<IReadOnlyList<ControlSessionSummary>> StopHostAsync()
        {
            TransportHostStopReport report = await StartStop().WaitAsync(Token);
            Assert.True(report.AllFinished);
            return _stopSnapshot!;
        }

        internal AcceptanceOutcome Evaluate()
        {
            HostRole.HostCounterSnapshot counts = Counters.Snapshot();
            bool clean = _stop?.IsCompletedSuccessfully == true && _stop.Result.AllFinished &&
                counts.Active == 0 && Host.ActiveConnections == 0 && Host.AdmittedConnections == 0 &&
                Context.PendingApprovalLimiter.GlobalInUse == 0 && Context.SessionRegistry.ActiveSessionCount == 0;
            return _evidence.Evaluate(counts.PartitionOk, counts.HandlerFaults, clean);
        }

        internal async Task AssertCleanAsync(string bucket)
        {
            await HandlerFinished.Task.WaitAsync(Token);
            await Counters.WaitForIdleAsync(TimeSpan.FromSeconds(1)).WaitAsync(Token);
            HostRole.HostCounterSnapshot counts = Counters.Snapshot();
            Assert.Equal(1L, counts.Handled);
            Assert.Equal(1L, counts.Sum);
            Assert.True(counts.PartitionOk);
            Assert.Equal(0L, counts.HandlerFaults);
            Assert.Equal(bucket + "=1", counts.Buckets);
            AssertCleanState();
        }

        private void AssertCleanState()
        {
            Assert.Equal(0, Counters.Snapshot().Active);
            Assert.Equal(0, Host.ActiveConnections);
            Assert.Equal(0, Host.AdmittedConnections);
            Assert.Equal(0, Context.PendingApprovalLimiter.GlobalInUse);
            Assert.Equal(0, Context.SessionRegistry.ActiveSessionCount);
            Assert.Empty(Context.SessionRegistry.Snapshot());
            Assert.Equal(0, Inbox.PendingCount);
            Assert.Empty(Inbox.GetSnapshot());
            Assert.Equal(0, _evidence.TrackedCount);
            Assert.False(Run.HasBackgroundFaults, Run.BackgroundFaultSummary);
            Assert.False(Run.Log.FileUnavailable);
        }

        public async ValueTask DisposeAsync()
        {
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
            try
            {
                try
                {
                    _clientStop.Cancel();
                    if (_client is not null)
                    {
                        try { (await _client.WaitAsync(cleanup.Token)).Dispose(); }
                        catch (Exception error) when (_client.IsCompleted &&
                            error is AuthenticationException or OperationCanceledException or IOException or SocketException)
                        {
                            // 仅观察客户端产品任务的预期拒绝/取消；不吞测试断言或未完成任务。
                        }
                    }
                }
                finally
                {
                    try
                    {
                        TransportHostStopReport report = await StartStop().WaitAsync(cleanup.Token);
                        Assert.True(report.AllFinished, "第一次停止报告不允许被 Dispose 的第二次停止洗白。");
                    }
                    finally
                    {
                        try
                        {
                            if (Volatile.Read(ref _entered) != 0)
                                await HandlerFinished.Task.WaitAsync(cleanup.Token);
                            await Counters.WaitForIdleAsync(TimeSpan.FromSeconds(1)).WaitAsync(cleanup.Token);
                        }
                        finally
                        {
                            await Host.DisposeAsync().AsTask().WaitAsync(cleanup.Token);
                        }
                    }
                }
            }
            finally
            {
                try
                {
                    _samplingStop.Cancel();
                    try { await _sampling.WaitAsync(cleanup.Token); }
                    catch (OperationCanceledException) when (_sampling.IsCanceled && _samplingStop.IsCancellationRequested) { }
                }
                finally
                {
                    Inbox.Dispose();
                    _operation.Dispose();
                    _clientStop.Dispose();
                    _samplingStop.Dispose();
                }
            }
            AssertCleanState();
        }
    }
}
