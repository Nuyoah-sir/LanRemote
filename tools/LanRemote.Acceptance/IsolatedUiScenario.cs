using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using LanRemote.Transport;
using LanRemote.Transport.Auth;

namespace LanRemote.Acceptance;

internal enum IsolatedUiCase
{
    ActiveHostStop,
    ServerProofMismatch,
}

/// <summary>仅限验收器的本机隔离运行；不读取设备配置、发现或持久化秘密。</summary>
internal static class IsolatedUiScenario
{
    internal static Task<AcceptanceOutcome> RunAsync(
        AcceptanceRun run,
        LocalApprovalInbox inbox,
        IsolatedUiCase scenario,
        Action<ControlClientApprovalPending> pending,
        Action<string> status,
        CancellationToken cancellationToken) =>
        RunAsync(run, inbox, scenario, pending, status, cancellationToken, new Options());

    // 只缩放本地对象保持，不改变任何认证窗口。null 用于核对普通客户端默认合同。
    internal sealed record Options
    {
        internal TimeSpan? ActiveHoldDuration { get; init; } = TimeSpan.FromSeconds(120);
        internal Action<Resources>? Initialized { get; init; }
        internal Func<Resources, CancellationToken, Task>? BeforeClientConnectAsync { get; init; }
        internal Action? BeforePeerReply { get; init; }
        internal Action<byte[]>? SecretAllocated { get; init; }
    }

    // 测试可观察实际所有者及释放后的缓冲；不可把这些引用交付 UI 或日志。
    internal sealed record Resources(
        TransportHost Host,
        ControlAuthContext AuthContext,
        ConnectionTarget Target,
        DeviceIdentity ClientIdentity,
        byte[] AccessKey,
        X509Certificate2 Certificate);

    internal static Task<AcceptanceOutcome> RunAsync(
        AcceptanceRun run,
        LocalApprovalInbox inbox,
        IsolatedUiCase scenario,
        Action<ControlClientApprovalPending> pending,
        Action<string> status,
        CancellationToken cancellationToken,
        Options options) => new Runner(run, inbox, scenario, pending, status, cancellationToken, options).RunAsync();

    private sealed class Runner(
        AcceptanceRun run,
        LocalApprovalInbox inbox,
        IsolatedUiCase scenario,
        Action<ControlClientApprovalPending> pending,
        Action<string> status,
        CancellationToken cancellationToken,
        Options options)
    {
        private static readonly TimeSpan CleanupBudget = TimeSpan.FromSeconds(5);
        private readonly object _handlerGate = new();
        private readonly List<Task> _handlers = new();
        private readonly HashSet<Exception> _reported = new();
        private readonly TaskCompletionSource<Exception> _handlerFault = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<PeerResult> _peerFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<Guid> _verified = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly HostSessionEvidence _evidence = new(4);
        private Resources? _resources;
        private HostRole.HostCounters? _counters;
        private Task _sampling = Task.CompletedTask;
        private Task<ClientRole.ScenarioOutcome>? _client;
        private bool _stopping;
        private AcceptanceOutcome _outcome = AcceptanceOutcome.PreconditionUnmet;
        private string _detail = "隔离场景尚未满足前置条件。";

        internal async Task<AcceptanceOutcome> RunAsync()
        {
            // 客户端不能链接 UI 取消：活动会话必须先由 Host 快照、Stop，再取消本地保持。
            using CancellationTokenSource clientStop = new();
            using CancellationTokenSource samplingStop = new();
            byte[]? key = null;
            X509Certificate2? certificate = null;
            TransportHost? host = null;
            TransportHostStopReport? firstStop = null;
            bool certificateDisposed = false;
            bool cleanupFault = false;
            run.Log.LineWritten += ObserveClientAuthentication;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (scenario is not (IsolatedUiCase.ActiveHostStop or IsolatedUiCase.ServerProofMismatch) ||
                    options.ActiveHoldDuration is { } hold && (hold <= TimeSpan.Zero || hold > TimeSpan.FromSeconds(120)))
                {
                    throw new ArgumentOutOfRangeException(nameof(options), "隔离场景或保持时长无效。");
                }
                run.WriteHeader(AcceptanceProfile.Timeouts);
                key = RandomNumberGenerator.GetBytes(AccessSecret.AccessKeyByteLength);
                certificate = CreateCertificate();
                Guid serverId = Guid.NewGuid();
                Guid clientId = Guid.NewGuid();
                string pin = Convert.ToHexString(SHA256.HashData(certificate.RawData));
                DeviceIdentity identity = new(clientId, clientId.ToString("N"), "本机隔离客户端", pin);
                int port;
                using (TcpListener reservation = new(IPAddress.Loopback, 0))
                {
                    reservation.Start();
                    port = ((IPEndPoint)reservation.LocalEndpoint).Port;
                }
                if (!ConnectionTarget.TryCreate(serverId, IPAddress.Loopback, port, pin, out ConnectionTarget? target))
                {
                    throw new InvalidOperationException("无法构造本机隔离目标。");
                }
                ControlAuthContext context = new()
                {
                    ServerDeviceId = serverId,
                    AccessSecretStore = new MemorySecretStore(key),
                    FailedAuthLimiter = new FailedAuthLimiter(),
                    PendingApprovalLimiter = new ConnectionAdmissionLimiter(3, 1),
                    ApprovalGate = inbox,
                    SessionRegistry = new SessionRegistry(),
                };
                _counters = new HostRole.HostCounters(context, _evidence);
                host = new TransportHost(new[] { IPAddress.Loopback }, new ExactLoopbackPolicy(), certificate,
                    HandleAsync, new TransportHostOptions
                    {
                        Port = port,
                        MaxConnections = 1,
                        MaxConnectionsPerAddress = 1,
                        Timeouts = AcceptanceProfile.Timeouts,
                        ShutdownTimeout = CleanupBudget,
                    });
                _resources = new Resources(host, context, target!, identity, key, certificate);
                options.Initialized?.Invoke(_resources);
                cancellationToken.ThrowIfCancellationRequested();
                TransportHostStartResult start = host.Start();
                if (!start.IsListening || start.Failures.Count != 0)
                {
                    // 临时端口释放与正式绑定间可能竞争；不能输出系统异常正文或继续连接其它服务。
                    throw new InvalidOperationException("本机随机端口绑定失败。");
                }
                run.Log.WriteLine($"[ISOLATED][START] scenario={scenario} serverDeviceId={serverId} " +
                    $"clientDeviceId={clientId} address=127.0.0.1 port={port} certificateSha256={pin}");
                Publish("本机隔离监听已启动，等待人工审批；尚未认证。");
                _sampling = SampleAsync(samplingStop.Token);
                // 测试可在同一真实 Host 上顺序连接；直接 await，钩子也由本轮拥有，不另起背景任务。
                if (options.BeforeClientConnectAsync is { } beforeClient)
                {
                    await beforeClient(_resources, cancellationToken).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                _client = RunClientAsync(clientStop.Token);
                ClientRole.ScenarioOutcome clientOutcome = await AwaitOwnedAsync(_client, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (scenario == IsolatedUiCase.ActiveHostStop)
                {
                    _outcome = clientOutcome.Outcome == AcceptanceOutcome.Pass
                        ? AcceptanceOutcome.PreconditionUnmet : clientOutcome.Outcome;
                    _detail = clientOutcome.Outcome == AcceptanceOutcome.Pass
                        ? "本地保持期限已到，未执行人工活动停机；本场景仍为 UNMET。" : clientOutcome.Detail;
                }
                else
                {
                    using CancellationTokenSource closeBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    closeBudget.CancelAfter(CleanupBudget);
                    PeerResult peer = await AwaitOwnedAsync(_peerFinished.Task, closeBudget.Token).ConfigureAwait(false);
                    using PeriodicTimer drain = new(TimeSpan.FromMilliseconds(10));
                    while (host.ActiveConnections != 0 || host.AdmittedConnections != 0)
                    {
                        await drain.WaitForNextTickAsync(closeBudget.Token).ConfigureAwait(false);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    bool rejectedProof = clientOutcome.Outcome == AcceptanceOutcome.Fail &&
                        clientOutcome.Fields.Any(pair => pair.Key == "rejection" && pair.Value == "client-server-proof-mismatch");
                    _outcome = peer.Injected ? AcceptanceOutcome.Fail : AcceptanceOutcome.PreconditionUnmet;
                    _detail = !peer.Injected
                        ? $"人工审批未批准或连接已离开，未发送故障证明；本场景未执行（{peer.Reason}）。"
                        : !peer.ClosedBeforeStop ? "坏证明已发送，但缺少主动停机前的客户端关闭证据。"
                        : rejectedProof ? clientOutcome.Detail : "坏证明已发送，但未获得预期的身份验证拒绝，不能判通过。";
                    Publish(!peer.Injected ? "人工审批未批准或已到期，未注入故障；结果为 UNMET。"
                        : rejectedProof && peer.ClosedBeforeStop
                            ? "客户端明确拒绝错误证明，并在 Host 停机前关闭；结果保持 FAIL。"
                            : "故障证明已发送，但拒绝码或关闭证据不符合预期；不能据此确认专项通过。");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _detail = "本机隔离运行被操作员停止；已先停止 Host 再回收客户端。";
            }
            catch (Exception error)
            {
                Fault(error);
                _detail = "本机隔离运行出现仪器故障，异常正文未输出。";
            }
            finally
            {
                lock (_handlerGate) { _stopping = true; }
                // 所有清理阶段独立执行。证书和 key 的生命周期覆盖初始化失败及端口竞争。
                await CleanupAsync(() =>
                {
                    if (scenario == IsolatedUiCase.ActiveHostStop && _resources is not null)
                    {
                        IReadOnlyList<ControlSessionSummary> snapshot = _evidence.BeginHostStop(_resources.AuthContext.SessionRegistry);
                        run.Log.WriteLine($"[HOST][STOP] authenticatedActiveBeforeStop={snapshot.Count}");
                        foreach (ControlSessionSummary session in snapshot)
                        {
                            run.Log.WriteLine($"[HOST][STOP] sessionId={session.SessionId} connectionId={session.ConnectionId} " +
                                "closeOrigin=host-forced-close naturalRelease=UNMET");
                        }
                    }
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
                await CleanupAsync(() => { inbox.Stop(); return Task.CompletedTask; }).ConfigureAwait(false);
                if (host is not null)
                {
                    await CleanupAsync(() =>
                    {
                        run.Log.WriteLine($"[ISOLATED][HOST-STOP] first=True clientTaskCompleted={_client is null || _client.IsCompleted}");
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
                    await CleanupAsync(async () =>
                    {
                        firstStop = await host.StopAsync(CleanupBudget).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                }
                await CleanupAsync(() =>
                {
                    clientStop.Cancel();
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
                await CleanupAsync(() => { samplingStop.Cancel(); return Task.CompletedTask; }).ConfigureAwait(false);
                if (_client is not null)
                {
                    await CleanupAsync(() => JoinAsync(_client, clientStop.Token)).ConfigureAwait(false);
                }
                Task[] handlers;
                lock (_handlerGate) { handlers = _handlers.ToArray(); }
                // 不以超时后 Transport registry 清表的 0 代替自己拥有的 handler join。
                await CleanupAsync(() => Task.WhenAll(handlers)).ConfigureAwait(false);
                await CleanupAsync(() => JoinAsync(_sampling, samplingStop.Token)).ConfigureAwait(false);
                if (_handlerFault.Task.IsCompletedSuccessfully) { Fault(_handlerFault.Task.Result); }
                if (host is not null) { await CleanupAsync(() => host.DisposeAsync().AsTask()).ConfigureAwait(false); }
                run.Log.LineWritten -= ObserveClientAuthentication;
                if (key is not null) { CryptographicOperations.ZeroMemory(key); }
                await CleanupAsync(() =>
                {
                    certificate?.Dispose();
                    certificateDisposed = true;
                    return Task.CompletedTask;
                }).ConfigureAwait(false);

                async Task CleanupAsync(Func<Task> work)
                {
                    try { await work().ConfigureAwait(false); }
                    catch (Exception error) { cleanupFault = true; Fault(error); }
                }
            }

            try
            {
                if (_resources is not null)
                {
                    HostRole.HostCounterSnapshot counts = _counters!.Snapshot();
                    ControlAuthContext context = _resources.AuthContext;
                    bool clean = !cleanupFault && firstStop?.AllFinished == true && counts.Active == 0 &&
                        context.SessionRegistry.ActiveSessionCount == 0 && context.PendingApprovalLimiter.GlobalInUse == 0 &&
                        inbox.PendingCount == 0 && host!.ActiveConnections == 0 && host.AdmittedConnections == 0;
                    if (scenario == IsolatedUiCase.ActiveHostStop)
                    {
                        run.Log.WriteLine(counts.Format());
                        run.Log.WriteLine(_evidence.FormatSummary());
                        run.Log.WriteLine($"[HOST][SUMMARY] firstStopAllFinishedWithinBudget={firstStop?.AllFinished.ToString() ?? "UNOBSERVED"} " +
                            $"firstStopUnfinishedConnections={firstStop?.UnfinishedConnections.ToString() ?? "UNOBSERVED"} " +
                            $"acceptLoopsFinished={firstStop?.AcceptLoopsFinished.ToString() ?? "UNOBSERVED"} " +
                            $"activeHandlers={counts.Active} activeRegistry={context.SessionRegistry.ActiveSessionCount} " +
                            $"pendingApprovals={context.PendingApprovalLimiter.GlobalInUse} handlerFaults={counts.HandlerFaults} cleanupFault={cleanupFault}");
                        _outcome = new[] { _outcome, _evidence.Evaluate(counts.PartitionOk, counts.HandlerFaults, clean) }.Combine();
                    }
                    else if (!clean) { _outcome = AcceptanceOutcome.HarnessError; }
                    run.Log.WriteLine($"[ISOLATED][CLEANUP] firstStopAllFinishedWithinBudget={firstStop?.AllFinished.ToString() ?? "UNOBSERVED"} " +
                        $"activeHandlers={counts.Active} activeRegistry={context.SessionRegistry.ActiveSessionCount} " +
                        $"pendingApprovals={context.PendingApprovalLimiter.GlobalInUse} inboxPending={inbox.PendingCount} " +
                        $"activeConnections={host!.ActiveConnections} admittedConnections={host.AdmittedConnections} " +
                        $"handlersJoined={_handlers.All(task => task.IsCompleted)} clientJoined={_client is null || _client.IsCompleted} " +
                        $"keyCleared={key!.All(value => value == 0)} certificateDisposed={certificateDisposed} cleanupFault={cleanupFault}");
                }
            }
            catch (Exception error) { Fault(error); }
            if (cancellationToken.IsCancellationRequested)
            {
                try { run.MarkOperatorAbort("本机隔离运行停止或关窗"); }
                catch (Exception error) { Fault(error); }
            }
            // 唯一结算点，位于全部子任务及秘密资源收尾之后；停止仍由 AcceptanceRun 判 INVALID_RUN。
            return run.Complete(_outcome, _detail);
        }

        private async Task<ClientRole.ScenarioOutcome> RunClientAsync(CancellationToken token)
        {
            Resources resources = _resources!;
            ClientRole.ScenarioOutcome outcome = await ClientRole.RunSuccessAsync(run, resources.ClientIdentity,
                resources.Target, resources.AccessKey, SessionPermission.Control,
                value =>
                {
                    if (!cancellationToken.IsCancellationRequested && !IsStopping) { pending(value); }
                }, token, scenario == IsolatedUiCase.ActiveHostStop ? options.ActiveHoldDuration : null).ConfigureAwait(false);
            // 分类日志属于客户端任务本身；owner 同时收到 handler 故障也不能跳过已取得的真实结果。
            // 操作员取消不发布迟到结果，更不能发布本地保持 PASS。
            if (!cancellationToken.IsCancellationRequested)
            {
                ClientRole.WriteOutcome(run, ClientRole.ScenarioSuccess, outcome);
            }
            return outcome;
        }

        private bool IsStopping { get { lock (_handlerGate) { return _stopping; } } }

        private void Publish(string text)
        {
            // 只调用前端提供的内存状态回调；不调用 Dispatcher，也不等待 UI。
            if (!cancellationToken.IsCancellationRequested && !IsStopping) { status(text); }
        }

        private void ObserveClientAuthentication(string line)
        {
            const string prefix = "[CLIENT][AUTH] serverProof=verified sessionId=";
            if (line.StartsWith(prefix, StringComparison.Ordinal) && line.Length >= prefix.Length + 36 &&
                Guid.TryParse(line.AsSpan(prefix.Length, 36), out Guid sessionId))
            {
                _verified.TrySetResult(sessionId);
            }
        }

        private async Task SampleAsync(CancellationToken token)
        {
            using PeriodicTimer timer = new(HostSessionEvidence.SamplePeriod);
            string? previous = null;
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (scenario != IsolatedUiCase.ActiveHostStop) { continue; }
                SessionRegistry registry = _resources!.AuthContext.SessionRegistry;
                _evidence.Sample(registry);
                IReadOnlyList<ControlSessionSummary> sessions = registry.Snapshot();
                bool verifiedActive = _client is { IsCompleted: false } && _verified.Task.IsCompletedSuccessfully && sessions.Any(session =>
                    session.SessionId == _verified.Task.Result && session.ClientDeviceId == _resources.ClientIdentity.DeviceId);
                string text = verifiedActive
                    ? $"已双向认证；Host registry={sessions.Count}，客户端保持中，请点击停止验证活动停机。"
                    : $"Host registry={sessions.Count}；尚未同时观测到客户端证明通过与 Host 活动登记。";
                if (text != previous) { Publish(text); previous = text; }
            }
        }

        private async Task<T> AwaitOwnedAsync<T>(Task<T> task, CancellationToken token)
        {
            await Task.WhenAny(task, _handlerFault.Task, _sampling).WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (_handlerFault.Task.IsCompletedSuccessfully) { throw _handlerFault.Task.Result; }
            if (_sampling.IsCompleted)
            {
                await _sampling.ConfigureAwait(false);
                throw new InvalidOperationException("隔离采样任务提前退出。");
            }
            return await task.ConfigureAwait(false);
        }

        private Task HandleAsync(AcceptedConnection connection, CancellationToken token)
        {
            lock (_handlerGate)
            {
                if (_stopping) { return Task.CompletedTask; }
                // Transport 的单连接配额只限制同时工作，不限制累计连接数。
                // 已完成 handler 的故障已转交 _handlerFault；仅保留仍需 join 的拥有权。
                _handlers.RemoveAll(static task => task.IsCompleted);
                Task handler = HandleCoreAsync(connection, token);
                _handlers.Add(handler);
                return handler;
            }
        }

        private async Task HandleCoreAsync(AcceptedConnection connection, CancellationToken token)
        {
            try
            {
                Require(connection.LocalAddress.Equals(IPAddress.Loopback) && connection.RemoteAddress.Equals(IPAddress.Loopback),
                    "隔离连接不是精确 IPv4 Loopback。");
                if (scenario == IsolatedUiCase.ActiveHostStop)
                {
                    await _counters!.HandleAsync(run, connection, AcceptanceProfile.Timeouts, token).ConfigureAwait(false);
                }
                else
                {
                    _peerFinished.TrySetResult(await RunPeerAsync(connection, token).ConfigureAwait(false));
                }
            }
            catch (Exception error) when ((token.IsCancellationRequested || cancellationToken.IsCancellationRequested) &&
                error is OperationCanceledException or IOException or ObjectDisposedException) { }
            catch (Exception error)
            {
                // TransportHost 会吞掉部分 handler 异常；非 faulted 信号确保 owner 必须显式处理。
                _handlerFault.TrySetResult(error);
            }
        }

        private async Task<PeerResult> RunPeerAsync(AcceptedConnection connection, CancellationToken hostToken)
        {
            Resources resources = _resources!;
            List<byte[]> buffers = new();
            byte[] Keep(byte[] bytes, bool secret = true)
            {
                buffers.Add(bytes);
                if (secret) { options.SecretAllocated?.Invoke(bytes); }
                return bytes;
            }
            using CancellationTokenSource readStop = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
            using CancellationTokenSource approvalStop = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
            Task<CloseResult>? read = null;
            Task<LocalApprovalDecision>? approval = null;
            bool injected = false;
            try
            {
                SslStream stream = connection.Stream;
                Require(stream.IsAuthenticated && stream.IsEncrypted && stream.LocalCertificate is not null, "隔离 TLS 尚未成立。");
                byte[] pin = Keep(SHA256.HashData(stream.LocalCertificate!.GetRawCertData()), secret: false);
                Require(pin.AsSpan().SequenceEqual(resources.Target.ExpectedCertSha256.Span) &&
                    pin.AsSpan().SequenceEqual(connection.Security.ServerCertificateSha256.Span), "隔离 TLS 指纹不匹配。");
                using (CancellationTokenSource helloWindow = CancellationTokenSource.CreateLinkedTokenSource(hostToken))
                {
                    helloWindow.CancelAfter(AcceptanceProfile.Timeouts.PreAuthEnvelopeTimeout);
                    byte[] hello = Keep(await ReadFrameAsync(stream, helloWindow.Token).ConfigureAwait(false), secret: false);
                    Require(HelloFrame.TryParse(hello, out _), "隔离 hello 非法。");
                }
                Guid sessionId = Guid.NewGuid();
                byte[] serverNonce = Keep(RandomNumberGenerator.GetBytes(32));
                byte[] transcript;
                byte[] clientNonce;
                using (CancellationTokenSource machine = CancellationTokenSource.CreateLinkedTokenSource(hostToken))
                {
                    machine.CancelAfter(AuthProtocol.AuthenticationWindowMilliseconds);
                    await SendFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        type = "auth_challenge", protocol = 1, sessionId = sessionId.ToString("D"),
                        serverDeviceId = resources.Target.DeviceId.ToString("D"), serverNonce = Convert.ToBase64String(serverNonce),
                        certSha256 = Convert.ToHexString(pin), expiresInMs = AuthProtocol.ChallengeExpiresInMs,
                    }), machine.Token).ConfigureAwait(false);
                    byte[] response = Keep(await ReadFrameAsync(stream, machine.Token).ConfigureAwait(false));
                    using JsonDocument document = JsonDocument.Parse(response);
                    JsonElement root = document.RootElement;
                    Require(root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).SequenceEqual(
                        new[] { "clientDeviceId", "clientName", "clientNonce", "clientProof", "requestedPermission", "type" }),
                        "隔离响应字段不符合协议。");
                    Require(root.GetProperty("type").GetString() == "auth_response" &&
                        root.GetProperty("clientDeviceId").GetString() == resources.ClientIdentity.DeviceId.ToString("D") &&
                        root.GetProperty("clientName").GetString() == resources.ClientIdentity.DeviceName &&
                        root.GetProperty("requestedPermission").GetString() == "control", "隔离响应身份或权限不匹配。");
                    string nonceText = root.GetProperty("clientNonce").GetString()!;
                    string proofText = root.GetProperty("clientProof").GetString()!;
                    clientNonce = Keep(Convert.FromBase64String(nonceText));
                    byte[] clientProof = Keep(Convert.FromBase64String(proofText));
                    Require(clientNonce.Length == 32 && clientProof.Length == 32 &&
                        Convert.ToBase64String(clientNonce) == nonceText && Convert.ToBase64String(clientProof) == proofText,
                        "隔离响应编码非法。");
                    // 独立构造 transcript，不调用产品的 proof/transcript 构造器来验证自身。
                    transcript = Keep(Encoding.UTF8.GetBytes(string.Join('\0',
                        "LANREMOTE-AUTH-V1", sessionId.ToString("D"), resources.Target.DeviceId.ToString("D"),
                        resources.ClientIdentity.DeviceId.ToString("D"), Convert.ToBase64String(serverNonce),
                        nonceText, Convert.ToHexString(pin), "control")));
                    using HMACSHA256 hmac = new(resources.AccessKey);
                    byte[] expected = Keep(hmac.ComputeHash(transcript));
                    Require(CryptographicOperations.FixedTimeEquals(expected, clientProof), "隔离客户端证明未通过。");
                    machine.Token.ThrowIfCancellationRequested();
                    await SendFrameAsync(stream, "{\"type\":\"approval_pending\"}"u8.ToArray(), machine.Token).ConfigureAwait(false);
                }

                TimeSpan approvalWindow = TimeSpan.FromMilliseconds(AuthProtocol.ApprovalWindowMilliseconds);
                long approvalStarted = Stopwatch.GetTimestamp();
                approvalStop.CancelAfter(approvalWindow);
                LocalApprovalRequest request = new(Guid.NewGuid(), connection.Security.ConnectionId, sessionId,
                    connection.RemoteAddress, connection.RemotePort, resources.ClientIdentity.DeviceId,
                    resources.ClientIdentity.DeviceName, SessionPermission.Control,
                    LocalApprovalRequest.ComputeShortCode(sessionId, clientNonce), DateTimeOffset.UtcNow + approvalWindow);
                approval = inbox.RequestApprovalAsync(request, approvalStop.Token).AsTask();
                // 同一读取跨越人工审批和终帧，既监测审批期间断连，又不取消读取来制造 EOF。
                read = ObserveCloseAsync(stream, Keep(new byte[1]), readStop.Token);
                await Task.WhenAny(approval, read).WaitAsync(approvalStop.Token).ConfigureAwait(false);
                approvalStop.Token.ThrowIfCancellationRequested();
                cancellationToken.ThrowIfCancellationRequested();
                if (read.IsCompleted)
                {
                    CloseResult earlyClose = await read.ConfigureAwait(false);
                    Require(earlyClose.Kind is "eof" or "rst", "审批期间收到意外载荷。");
                    run.Log.WriteLine("[ISOLATED][UNMET] reason=left-before-approval injected=False");
                    return new PeerResult(false, !IsStopping, "left-before-approval");
                }
                if (Stopwatch.GetElapsedTime(approvalStarted) >= approvalWindow)
                {
                    run.Log.WriteLine("[ISOLATED][UNMET] reason=approval-timeout injected=False");
                    return new PeerResult(false, false, "approval-timeout");
                }
                LocalApprovalDecision decision = await approval.ConfigureAwait(false);
                Require(decision.RequestId == request.RequestId, "隔离审批请求不匹配。");
                bool inject = decision.Outcome == LocalApprovalOutcome.Approved;
                options.BeforePeerReply?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                if (inject)
                {
                    string grant = decision.GrantedPermission switch
                    {
                        SessionPermission.ViewOnly => "view",
                        SessionPermission.Control => "control",
                        _ => throw new InvalidOperationException("隔离审批未授予合法权限。"),
                    };
                    byte[] hash = Keep(SHA256.HashData(transcript));
                    byte[] grantTranscript = Keep(Encoding.UTF8.GetBytes(string.Join('\0',
                        "server", "LANREMOTE-GRANT-V1", Convert.ToHexString(hash), grant)));
                    using HMACSHA256 hmac = new(resources.AccessKey);
                    byte[] proof = Keep(hmac.ComputeHash(grantTranscript));
                    proof[0] ^= 0x80;
                    options.SecretAllocated?.Invoke(proof);
                    byte[] token = Keep(RandomNumberGenerator.GetBytes(32));
                    approvalStop.Token.ThrowIfCancellationRequested();
                    Require(!read.IsCompleted && Stopwatch.GetElapsedTime(approvalStarted) < approvalWindow,
                        "隔离终帧发送前连接离开或审批过期。");
                    await SendFrameAsync(stream, Keep(JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        type = "auth_success", grantedPermission = grant, serverProof = Convert.ToBase64String(proof),
                        sessionToken = Convert.ToBase64String(token), videoAttachExpiresInMs = AuthProtocol.VideoAttachExpiresInMs,
                    })), approvalStop.Token).ConfigureAwait(false);
                    injected = true;
                    run.Log.WriteLine($"[ISOLATED][INJECT] grant={decision.GrantedPermission} flippedBits=1 registry=NOT_APPLICABLE");
                }
                else
                {
                    await SendFrameAsync(stream, "{\"type\":\"authentication_failed\"}"u8.ToArray(), approvalStop.Token).ConfigureAwait(false);
                    run.Log.WriteLine("[ISOLATED][DENIED] injected=False registry=NOT_APPLICABLE");
                }
                // 这里只缩放关闭观测预算，不改变机器认证或人工审批时限。
                CloseResult close;
                try { close = await read.WaitAsync(CleanupBudget, hostToken).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    hostToken.ThrowIfCancellationRequested();
                    cancellationToken.ThrowIfCancellationRequested();
                    run.Log.WriteLine($"[ISOLATED][PEER-CLOSE-MISSING] reason=timeout injected={injected}");
                    return new PeerResult(injected, false, "close-timeout");
                }
                hostToken.ThrowIfCancellationRequested();
                cancellationToken.ThrowIfCancellationRequested();
                bool beforeStop = !IsStopping;
                Require(beforeStop && close.Kind is "eof" or "rst", "未观察到主动 HostStop 前的自然关闭。");
                run.Log.WriteLine($"[ISOLATED][PEER-CLOSE] kind={close.Kind} " +
                    $"bytesAfterReply={close.Bytes?.ToString() ?? "UNKNOWN"} beforeHostStop={beforeStop}");
                return new PeerResult(inject, beforeStop);
            }
            catch (OperationCanceledException) when (approvalStop.IsCancellationRequested &&
                !hostToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !injected)
            {
                run.Log.WriteLine("[ISOLATED][UNMET] reason=approval-timeout injected=False");
                return new PeerResult(false, false, "approval-timeout");
            }
            finally
            {
                try
                {
                    approvalStop.Cancel();
                    if (approval is not null) { await JoinAsync(approval, approvalStop.Token).ConfigureAwait(false); }
                }
                finally
                {
                    try
                    {
                        readStop.Cancel();
                        if (read is not null) { await JoinAsync(read, readStop.Token).ConfigureAwait(false); }
                    }
                    finally { foreach (byte[] bytes in buffers) { CryptographicOperations.ZeroMemory(bytes); } }
                }
            }
        }

        private void Fault(Exception error)
        {
            _outcome = AcceptanceOutcome.HarnessError;
            if (_reported.Add(error))
            {
                try
                {
                    run.ReportBackgroundFault("本机隔离运行", new InvalidOperationException(
                        $"{error.GetType().Name}（异常正文未输出，避免秘密进入日志）"));
                }
                catch (Exception)
                {
                    // 故障已先登记；日志订阅者再抛错也不能阻断后续 Stop、join 和秘密清理。
                }
            }
        }
    }

    private sealed record PeerResult(bool Injected, bool ClosedBeforeStop, string Reason = "completed");
    private sealed record CloseResult(string Kind, int? Bytes);

    private static async Task<CloseResult> ObserveCloseAsync(SslStream stream, byte[] buffer, CancellationToken token)
    {
        try
        {
            int count = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return new CloseResult(count == 0 ? "eof" : "data", count);
        }
        catch (IOException error) when (IsConnectionReset(error))
        {
            token.ThrowIfCancellationRequested();
            // RST 没有读取返回值，UNKNOWN 不能替换成 0。其它 IOException 仍交 owner 判仪器故障。
            return new CloseResult("rst", null);
        }
    }

    internal static bool IsConnectionReset(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket && socket.SocketErrorCode == SocketError.ConnectionReset) { return true; }
        }
        return false;
    }

    private static Task<byte[]> ReadFrameAsync(SslStream stream, CancellationToken token) => new FrameReader(stream)
        .ReadFrameAsync(TransportConstants.MaxPreAuthMessageBytes, AcceptanceProfile.Timeouts.LengthPrefixTimeout,
            AcceptanceProfile.Timeouts.PayloadTimeout, token);

    private static async Task SendFrameAsync(SslStream stream, byte[] payload, CancellationToken token)
    {
        try
        {
            await FrameWriter.WriteFrameAsync(stream, payload, TransportConstants.MaxPreAuthMessageBytes,
                AcceptanceProfile.Timeouts.LengthPrefixTimeout, token).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    private static async Task JoinAsync(Task task, CancellationToken token)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested && task.IsCanceled) { }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private sealed class ExactLoopbackPolicy : ISubnetPolicy
    {
        public bool IsAllowedPeer(IPAddress localAddress, IPAddress remoteAddress) =>
            localAddress.Equals(IPAddress.Loopback) && remoteAddress.Equals(IPAddress.Loopback);
    }

    private sealed class MemorySecretStore(byte[] key) : IAccessSecretStore
    {
        public Task<AccessSecret> LoadOrCreateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AccessSecret(key.ToArray()));
        }

        public Task<AccessSecret> RegenerateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("隔离运行不轮换或持久化密钥。");
    }

    private static X509Certificate2 CreateCertificate()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new("CN=lanremote-isolated", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new(PeerCertificateValidator.ServerAuthEkuOid) }, false));
        using X509Certificate2 signed = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        byte[] passwordBytes = RandomNumberGenerator.GetBytes(16);
        byte[]? pfx = null;
        try
        {
            string password = Convert.ToHexString(passwordBytes);
            pfx = signed.Export(X509ContentType.Pfx, password);
            return X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.DefaultKeySet);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            if (pfx is not null) { CryptographicOperations.ZeroMemory(pfx); }
        }
    }
}
