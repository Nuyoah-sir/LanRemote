using System.Diagnostics;
using System.Net;
using LanRemote.Transport;
using Microsoft.Extensions.Logging;

namespace LanRemote.Acceptance;

/// <summary>真实网卡上的 TLS、pre-auth、认证与会话保持验收；终态桶互斥，观测不足不冒充通过。</summary>
internal static class HostRole
{
    public const int RunUntilCancelled = 0;

    /// <summary>initialized 交付同一 context；stopping 在清理入口通知停用，两者均不等待 UI、不输出秘密。</summary>
    public static async Task<AcceptanceOutcome> RunAsync(
        AcceptanceRun run,
        int seconds,
        CancellationToken cancellationToken,
        LocalApprovalInbox? approvalInbox = null,
        Action<AcceptanceContext>? initialized = null,
        Action? stopping = null)
    {
        AcceptanceLog log = run.Log;
        AcceptanceContext? context = null;
        TransportHost? host = null;
        ControlAuthContext? authContext = null;
        HostCounters? counters = null;
        HostSessionEvidence? evidence = null;
        CancellationTokenSource? samplingStop = null;
        Task? sampling = null;
        TransportHostStopReport? stop = null;
        bool listening = false;
        bool cleanupFault = false;
        AcceptanceOutcome outcome = AcceptanceOutcome.PreconditionUnmet;
        string detail = "尚未完成有效认证观测。";
        string stage = "初始化";

        try
        {
            run.WriteHeader(AcceptanceProfile.Timeouts);
            context = new AcceptanceContext(LogLevel.Information, log.WriteLine);
            await context.InitializeAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            stage = "initialized 回调";
            // UI 只发布引用；附属读取须取得 context lease，回调失败走统一清理及 HARNESS_ERROR。
            initialized?.Invoke(context);
            stage = "启动";
            IReadOnlyList<IPAddress> addresses = context.ListenAddresses();
            run.WriteIdentity(context.Identity, context.Certificate, addresses);

            if (addresses.Count == 0)
            {
                detail = "本机没有合格的 RFC1918 网卡；请人工确认两机实验网段，本轮未修改网络。";
                log.WriteLine("[HOST] " + detail);
            }
            else
            {
                await context.StartDiscoveryAsync(cancellationToken).ConfigureAwait(false);
                TransportHostOptions options = new()
                {
                    Port = context.Config.TransportPort,
                    Timeouts = AcceptanceProfile.Timeouts,
                };
                // 全部按 Host 创建一次，绝不能在每条连接里重新创建限流器或 registry。
                authContext = new ControlAuthContext
                {
                    ServerDeviceId = context.Identity.DeviceId,
                    AccessSecretStore = context.AccessSecretStore,
                    FailedAuthLimiter = new FailedAuthLimiter(),
                    PendingApprovalLimiter = new ConnectionAdmissionLimiter(3, 1),
                    SessionRegistry = new SessionRegistry(),
                    ApprovalGate = approvalInbox is null ? new UnavailableApprovalGate() : approvalInbox,
                    Options = new ControlAuthOptions { RequireLocalApproval = true },
                };
                evidence = new HostSessionEvidence(options.MaxConnections);
                counters = new HostCounters(authContext, evidence);
                host = new TransportHost(addresses, context.SubnetPolicy, context.Certificate.Certificate,
                    (connection, token) => counters.HandleAsync(run, connection, options.Timeouts, token), options);
                TransportHostStartResult start = host.Start();
                foreach (TransportHostBindFailure failure in start.Failures)
                {
                    log.WriteLine($"[HOST] bind 失败 {failure.Address}: {failure.Error} {failure.Message}");
                }
                listening = start.IsListening;
                if (!listening)
                {
                    detail = "所有地址都 bind 失败。";
                }
                else
                {
                    log.WriteLine($"[HOST][CORRELATE] hostDeviceId={context.Identity.DeviceId} " +
                        $"hostDeviceCode={context.Identity.DeviceCode} " +
                        $"hostCertSha256={context.Certificate.Sha256FingerprintHex} " +
                        $"boundAddresses=\"{string.Join(",", start.BoundAddresses)}\" port={context.Config.TransportPort}");
                    log.WriteLine($"[HOST][AUTH] localApprovalRequired=True approvalGate=" +
                        $"{(approvalInbox is null ? "Unavailable" : "LocalApprovalInbox")} pendingQuota=3/1");
                    log.WriteLine("[HOST][CRITERIA] clientHoldMs=5000 samplePeriodMs=100 " +
                        "requiredPositiveSpanMs=4000 maxObservationGapMs=500 scope=acceptance-only");
                    log.WriteLine(seconds == RunUntilCancelled
                        ? "[HOST] 正在监听；操作员停止会作废本轮，定时结束仍活跃的会话只记强制关闭。"
                        : $"[HOST] 等待连接 {seconds} 秒……");

                    samplingStop = new CancellationTokenSource();
                    sampling = SampleRegistryAsync(evidence, authContext.SessionRegistry, samplingStop.Token);
                    using CancellationTokenSource window =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    if (seconds != RunUntilCancelled)
                    {
                        window.CancelAfter(TimeSpan.FromSeconds(seconds));
                    }
                    stage = "监听或 registry 采样";
                    Task wait = Task.Delay(Timeout.Infinite, window.Token);
                    Task finished = await Task.WhenAny(wait, sampling).ConfigureAwait(false);
                    if (finished == sampling)
                    {
                        // 不让采样器已经坏掉的 Host 继续无限监听，也取消尚未结束的监听等待。
                        window.Cancel();
                        await sampling.ConfigureAwait(false);
                        throw new InvalidOperationException("registry 采样器提前退出。");
                    }
                    try
                    {
                        await wait.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (window.IsCancellationRequested)
                    {
                        // 定时停机不是客户端自然释放；在清理入口统一标注。
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            detail = "被控端运行被操作员取消。";
        }
        catch (Exception ex)
        {
            outcome = AcceptanceOutcome.HarnessError;
            detail = $"Host {stage}失败（{ex.GetType().Name}）。";
            ReportFault(run, "Host " + stage, ex);
        }
        finally
        {
            // 先关闭附属工作入口；lease 的真实归还在 context 释放前等待，不以 UI/日志时序代替。
            Task ownedWork = context?.StopWorkAsync() ?? Task.CompletedTask;
            // 先标注停机重叠，再通知 UI 停用；两步都先于同步日志，不能让日志积压延后停用。
            IReadOnlyList<ControlSessionSummary>? activeAtStop = null;
            Exception? snapshotFault = null;
            if (host is not null)
            {
                try { activeAtStop = evidence!.BeginHostStop(authContext!.SessionRegistry); }
                catch (Exception ex) { snapshotFault = ex; cleanupFault = true; }
            }
            try
            {
                // 仅发布停用状态，不调用 Dispatcher；失败不妨碍其它资源收尾。
                stopping?.Invoke();
            }
            catch (Exception ex)
            {
                cleanupFault = true;
                ReportFault(run, "Host stopping 回调", ex);
            }
            try
            {
                approvalInbox?.Stop();
            }
            catch (Exception ex)
            {
                cleanupFault = true;
                ReportFault(run, "Host 审批收件箱停止", ex);
            }
            if (snapshotFault is not null) { ReportFault(run, "Host 停机快照", snapshotFault); }
            if (activeAtStop is not null)
            {
                try
                {
                    log.WriteLine($"[HOST][STOP] authenticatedActiveBeforeStop={activeAtStop.Count}");
                    foreach (ControlSessionSummary session in activeAtStop)
                    {
                        log.WriteLine($"[HOST][STOP] sessionId={session.SessionId} connectionId={session.ConnectionId} " +
                            "closeOrigin=host-forced-close naturalRelease=UNMET");
                    }
                }
                catch (Exception ex)
                {
                    cleanupFault = true;
                    ReportFault(run, "Host 停机快照日志", ex);
                }
            }
            if (host is not null)
            {
                try
                {
                    // 保留第一次报告：旧 registry 超预算后清表，Dispose 再停一次可能返回虚假的全完成。
                    stop = await host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    cleanupFault = true;
                    ReportFault(run, "Host StopAsync", ex);
                }
                finally
                {
                    try
                    {
                        await host.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        cleanupFault = true;
                        ReportFault(run, "Host DisposeAsync", ex);
                    }
                }
                try
                {
                    // 独立等待本角色 handler 的记账收尾，不以 Transport 清表后的 0 替代 join。
                    await counters!.WaitForIdleAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    cleanupFault = true;
                    ReportFault(run, "Host handler 收尾预算耗尽或失败（仍不能声称全部任务完成）", ex);
                }
            }
            if (samplingStop is not null)
            {
                try
                {
                    samplingStop.Cancel();
                    await sampling!.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (samplingStop.IsCancellationRequested && sampling!.IsCanceled)
                {
                    // 只接受本采样器的停机取消；真正 fault 仍按故障处理。
                }
                catch (Exception ex)
                {
                    cleanupFault = true;
                    ReportFault(run, "Host registry 采样器收尾", ex);
                }
                finally
                {
                    samplingStop.Dispose();
                }
            }
            if (context is not null)
            {
                try
                {
                    try { await ownedWork.ConfigureAwait(false); }
                    finally { await context.DisposeAsync().ConfigureAwait(false); }
                }
                catch (Exception ex)
                {
                    cleanupFault = true;
                    ReportFault(run, "Host Discovery/context 释放", ex);
                }
            }
        }

        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                run.MarkOperatorAbort("被控端停止监听或初始化取消");
            }
            if (counters is not null)
            {
                HostCounterSnapshot counts = counters.Snapshot();
                int registryRemaining = authContext!.SessionRegistry.ActiveSessionCount;
                int pendingRemaining = authContext.PendingApprovalLimiter.GlobalInUse;
                bool clean = stop?.AllFinished == true && counts.Active == 0 &&
                    registryRemaining == 0 && pendingRemaining == 0 && host!.ActiveConnections == 0;
                log.WriteLine(counts.Format());
                log.WriteLine(evidence!.FormatSummary());
                log.WriteLine($"[HOST][SUMMARY] firstStopAllFinishedWithinBudget={stop?.AllFinished.ToString() ?? "UNOBSERVED"} " +
                    $"firstStopUnfinishedConnections={stop?.UnfinishedConnections.ToString() ?? "UNOBSERVED"} " +
                    $"acceptLoopsFinished={stop?.AcceptLoopsFinished.ToString() ?? "UNOBSERVED"} " +
                    $"activeHandlers={counts.Active} activeRegistry={registryRemaining} pendingApprovals={pendingRemaining} " +
                    $"handlerFaults={counts.HandlerFaults} cleanupFault={cleanupFault}");
                if (listening && outcome != AcceptanceOutcome.HarnessError)
                {
                    outcome = evidence.Evaluate(counts.PartitionOk, counts.HandlerFaults, clean);
                    detail = $"Host 认证保持与注销证据={outcome.Code()}；sessionHandled={counts.Handled} " +
                        $"partitionOk={counts.PartitionOk} firstStopAllFinished={stop?.AllFinished} " +
                        $"activeHandlers={counts.Active}；拒绝桶单独核对，不逐条视为失败。";
                }
            }
            log.WriteLine("[HOST][UNOBSERVED] tlsStageRejections=UNOBSERVABLE " +
                "// TransportHost 的同子网拒绝/准入拒绝/TLS 失败不进入 handler，不能用 0 顶替。");
            log.WriteLine("[HOST][UNOBSERVED] localShutdownSent=UNOBSERVED peerCloseKind=UNOBSERVABLE " +
                "// 认证 Run 结束仅证明保持读已结束；EOF/RST/越界数据无法区分，不声称 close_notify 已观测。");
        }
        catch (Exception ex)
        {
            outcome = AcceptanceOutcome.HarnessError;
            detail = "Host 证据汇总或日志回调失败。";
            ReportFault(run, "Host 证据汇总", ex);
        }
        if (cleanupFault)
        {
            outcome = AcceptanceOutcome.HarnessError;
            detail += " 资源清理或后台收尾存在故障，不能给 PASS。";
        }
        // 唯一结算点在外层 finally 之后；Complete 由 run 优先处理 operator abort。
        return run.Complete(outcome, detail);
    }

    private static async Task SampleRegistryAsync(
        HostSessionEvidence evidence, SessionRegistry registry, CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(HostSessionEvidence.SamplePeriod);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            evidence.Sample(registry);
        }
    }

    private static void ReportFault(AcceptanceRun run, string source, Exception exception)
    {
        // initialized/UI 可能触及密钥；异常 Message/InnerException 也不透传到日志。
        try
        {
            run.ReportBackgroundFault(source,
                new InvalidOperationException($"{exception.GetType().Name}（异常正文未输出，避免秘密进入日志）"));
        }
        catch (Exception)
        {
            // ReportBackgroundFault 已先登记故障；日志订阅方再抛也不能阻断资源释放。
        }
    }

    private sealed class UnavailableApprovalGate : ILocalApprovalGate
    {
        public ValueTask<LocalApprovalDecision> RequestApprovalAsync(
            LocalApprovalRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LocalApprovalDecision(request.RequestId, LocalApprovalOutcome.Unavailable));
    }

    internal sealed class HostCounters(ControlAuthContext authContext, HostSessionEvidence evidence)
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, long> _buckets = new(StringComparer.Ordinal);
        private TaskCompletionSource _idle = CompletedSource();
        private long _handled;
        private long _handlerFaults;
        private int _active;

        internal async Task HandleAsync(AcceptanceRun run, AcceptedConnection connection,
            TransportTimeouts timeouts, CancellationToken cancellationToken)
        {
            long index;
            lock (_gate)
            {
                index = ++_handled;
                if (_active++ == 0) { _idle = new(TaskCreationOptions.RunContinuationsAsynchronously); }
            }
            long began = Stopwatch.GetTimestamp();
            Guid connectionId = connection.Security.ConnectionId;
            ControlAuthSession? auth = null;
            ControlAuthResult? result = null;
            string bucket = "handler-faulted";
            string? rejection = null;
            ControlSessionState state = ControlSessionState.AwaitingHello;
            Exception? fault = null;
            try
            {
                ControlPreAuthResult preauth = await new ControlPreAuthSession()
                    .RunAsync(connection, timeouts, cancellationToken).ConfigureAwait(false);
                state = preauth.State;
                if (!preauth.Completed)
                {
                    rejection = preauth.Rejection;
                    bucket = "preauth-rejected:" + Reason(rejection);
                }
                else
                {
                    ControlPreAuthHandoff handoff = preauth.Handoff ??
                        throw new InvalidOperationException("pre-auth 成功但缺少交接对象。");
                    auth = handoff.BeginAuthentication(authContext, timeouts);
                    evidence.Track(auth.SessionId, connectionId);
                    run.Log.WriteLine($"[HOST][AUTH-BEGIN] conn#{index} connectionId={connectionId} sessionId={auth.SessionId}");
                    // 不打开额外读者；所有流读取与保持完全交给 Transport 的唯一读者链。
                    result = await auth.RunAsync(cancellationToken).ConfigureAwait(false);
                    if (result.SessionId != auth.SessionId)
                    {
                        throw new InvalidOperationException("认证结果 SessionId 与交接会话不一致。");
                    }
                    state = result.State;
                    rejection = result.Rejection;
                    bucket = result.Completed ? "authenticated-ended" : "auth-rejected:" + Reason(rejection);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                bucket = auth is null ? "preauth-cancelled-by-stop" : "auth-cancelled-by-stop";
                rejection = "host-stop";
            }
            catch (Exception ex)
            {
                fault = ex;
            }
            finally
            {
                try
                {
                    if (auth is not null)
                    {
                        state = auth.State;
                        HostSessionObservation observation = evidence.End(auth.SessionId, connectionId,
                            result?.Completed == true || state == ControlSessionState.Authenticated,
                            cancellationToken, authContext.SessionRegistry);
                        if (observation.Authenticated)
                        {
                            bucket = observation.HostForcedClose ? "authenticated-host-forced-close"
                                : observation.DeregisteredAtEnd ? "authenticated-ended-unregistered"
                                : "authenticated-ended-still-registered";
                        }
                        run.Log.WriteLine(observation.Format());
                    }
                    if (fault is not null)
                    {
                        bucket = "handler-faulted";
                        ReportFault(run, $"Host handler conn#{index} connectionId={connectionId}", fault);
                    }
                    run.Log.WriteLine($"[HOST][RESULT] conn#{index} connectionId={connectionId} " +
                        $"sessionId={auth?.SessionId.ToString() ?? "-"} peer={connection.RemoteAddress}:{connection.RemotePort} " +
                        $"proto={connection.NegotiatedProtocol} terminal={bucket} rejection={rejection ?? "-"} " +
                        $"returnedState={state} elapsedMs={HostSessionEvidence.Milliseconds(Stopwatch.GetElapsedTime(began))} " +
                        "// Authenticated 返回不是在线状态；注销证据见 SESSION 行。");
                }
                catch (Exception ex)
                {
                    fault ??= ex;
                    ReportFault(run, $"Host handler 证据或日志 conn#{index}", ex);
                }
                finally
                {
                    // 只在这里入桶一次；日志或证据处理抛错也仍保持互斥划分。
                    lock (_gate)
                    {
                        if (fault is not null) { _handlerFaults++; bucket = "handler-faulted"; }
                        _buckets.TryGetValue(bucket, out long count);
                        _buckets[bucket] = count + 1;
                        if (--_active == 0) { _idle.TrySetResult(); }
                    }
                }
            }
        }

        internal HostCounterSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new HostCounterSnapshot(_handled, _active, _handlerFaults, _buckets.Values.Sum(),
                    string.Join(", ", _buckets.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => $"{pair.Key}={pair.Value}")));
            }
        }

        internal Task WaitForIdleAsync(TimeSpan budget)
        {
            lock (_gate) { return _idle.Task.WaitAsync(budget); }
        }

        private static string Reason(string? rejection) => rejection?.Split(':')[0] ?? "unknown";

        private static TaskCompletionSource CompletedSource()
        {
            TaskCompletionSource source = new(TaskCreationOptions.RunContinuationsAsynchronously);
            source.SetResult();
            return source;
        }
    }

    internal sealed record HostCounterSnapshot(long Handled, int Active, long HandlerFaults, long Sum, string Buckets)
    {
        internal bool PartitionOk => Active == 0 && Sum == Handled;
        internal string Format() => $"[HOST][BUCKETS] sessionHandled={Handled} active={Active} " +
            $"sum={Sum} partitionOk={PartitionOk} {{{Buckets}}}";
    }
}
