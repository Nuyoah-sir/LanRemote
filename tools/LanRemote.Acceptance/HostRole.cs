using System.Net;
using LanRemote.Transport;
using Microsoft.Extensions.Logging;

namespace LanRemote.Acceptance;

/// <summary>
/// 被控端角色：真实网卡上起 TLS Host，对每条连接跑完整的 pre-auth 会话。
/// </summary>
/// <remarks>
/// <para><b>计数必须构成划分</b>（外部评审第 6 条）。原先的
/// <c>accepted / preAuthenticated / rejected</c> 三个数语义重叠：
/// <c>accepted</c> 是「进了会话处理器」、<c>preAuthenticated</c> 是它的子集、
/// <c>rejected</c> 是它的另一个子集，三个数放一起既不相等也不相加，
/// 无法回答「有没有连接没被记账」——而「没被记账」恰恰是最需要发现的情况。
/// 现在改成：进入会话处理器的总数 = 各<b>互斥</b>终结态桶之和，且停止时活动数必须为 0。</para>
/// <para><b>被控端有真实的可观测盲区，必须写出来而不是拿 0 冒充</b>（评审第 7 条）。
/// <see cref="TransportHost"/> 对「同子网拒绝 / 准入拒绝 / TLS 失败」都是静默
/// <c>return</c>（HANDOFF §14.12），所以这些连接在本次验收里<b>根本不会留下记录</b>。
/// 这里一律标 <c>UNOBSERVABLE</c>，不填 0——填 0 等于声称「一条都没有」，
/// 那是伪造证据。</para>
/// </remarks>
internal static class HostRole
{
    /// <summary>没有显式时长就一直监听，直到取消令牌被触发。</summary>
    public const int RunUntilCancelled = 0;

    /// <summary>
    /// 跑被控端角色。
    /// </summary>
    /// <param name="run">本次运行上下文（Run ID、日志、结算）。</param>
    /// <param name="seconds">监听多少秒后自行停机；<see cref="RunUntilCancelled"/> 表示一直等。</param>
    /// <param name="cancellationToken">停止信号（UI 的「停止监听」）。</param>
    /// <returns>整轮结局；枚举值即退出码。</returns>
    public static async Task<AcceptanceOutcome> RunAsync(
        AcceptanceRun run,
        int seconds,
        CancellationToken cancellationToken)
    {
        AcceptanceLog log = run.Log;

        await using AcceptanceContext context = new(LogLevel.Information, log.WriteLine);
        await context.InitializeAsync(cancellationToken);

        run.WriteHeader(AcceptanceProfile.Timeouts);
        run.WriteIdentity(context.Identity, context.Certificate, context.ListenAddresses());

        IReadOnlyList<IPAddress> addresses = context.ListenAddresses();

        if (addresses.Count == 0)
        {
            const string reason =
                "本机没有合格的 RFC1918 网卡——先用 set-lab-ip.ps1（管理员）把两台机器放进同一网段。";

            log.WriteLine("[HOST] 没有任何可监听地址。");
            return Finish(run, AcceptanceOutcome.PreconditionUnmet, reason);
        }

        await context.StartDiscoveryAsync(cancellationToken);

        TransportTimeouts timeouts = AcceptanceProfile.Timeouts;
        HostCounters counters = new();

        TransportHost host = new(
            addresses,
            context.SubnetPolicy,
            context.Certificate.Certificate,
            (connection, token) => counters.HandleAsync(run, connection, timeouts, token),
            new TransportHostOptions
            {
                Port = context.Config.TransportPort,
                Timeouts = timeouts,
            });

        await using (host)
        {
            TransportHostStartResult start = host.Start();

            if (!start.IsListening)
            {
                log.WriteLine("[HOST] 一个 listener 都没起来。");

                foreach (TransportHostBindFailure failure in start.Failures)
                {
                    log.WriteLine($"[HOST] bind 失败 {failure.Address}: {failure.Error} {failure.Message}");
                }

                return Finish(run, AcceptanceOutcome.PreconditionUnmet, "所有地址都 bind 失败。");
            }

            log.WriteLine($"[HOST] boundAddresses = {string.Join(", ", start.BoundAddresses)}");
            log.WriteLine($"[HOST] port           = {context.Config.TransportPort}");

            // 被控端可被核对的关键字段放在一行里，便于和客户端日志对上。
            log.WriteLine($"[HOST][CORRELATE] hostDeviceId={context.Identity.DeviceId} " +
                          $"hostDeviceCode={context.Identity.DeviceCode} " +
                          $"hostCertSha256={context.Certificate.Sha256FingerprintHex} " +
                          $"boundAddresses=\"{string.Join(",", start.BoundAddresses)}\" " +
                          $"port={context.Config.TransportPort}");

            log.WriteLine(seconds == RunUntilCancelled
                ? "[HOST] 正在监听，点「停止监听」结束……"
                : $"[HOST] 等待连接 {seconds} 秒……");

            using CancellationTokenSource window =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            if (seconds != RunUntilCancelled)
            {
                window.CancelAfter(TimeSpan.FromSeconds(seconds));
            }

            try
            {
                await Task.Delay(Timeout.Infinite, window.Token);
            }
            catch (OperationCanceledException)
            {
                // 正常停机路径。
            }

            if (cancellationToken.IsCancellationRequested)
            {
                run.MarkOperatorAbort("被控端「停止监听」");
            }

            log.WriteLine("[HOST] 正在停机……");

            TransportHostStopReport stop = await host.StopAsync(TimeSpan.FromSeconds(5));
            int activeAtStop = host.ActiveConnections;

            log.WriteLine(counters.FormatBuckets());
            log.WriteLine(
                $"[HOST][SUMMARY] connectionsEnteringSessionHandler={counters.SessionHandled} " +
                $"listenersStoppedCleanly={stop.AllFinished} " +
                $"unfinishedConnections={stop.UnfinishedConnections} " +
                $"acceptLoopsFinished={stop.AcceptLoopsFinished} " +
                $"activeAtStop={activeAtStop} handlerFaults={counters.HandlerFaults}");

            // 这两行的存在本身就是证据的一部分：它们说的是「我们测不到什么」。
            log.WriteLine(
                "[HOST][UNOBSERVED] tlsStageRejections=UNOBSERVABLE " +
                "// TransportHost 对同子网拒绝/准入拒绝/TLS 失败全部静默 return（HANDOFF §14.12）" +
                "——本次验收里这些连接不留任何记录，不能用 0 顶替");
            log.WriteLine(
                "[HOST][UNOBSERVED] localShutdownSent=UNOBSERVED " +
                "// M4 起 ControlPreAuthSession 成功路径不再关闭连接（ADR-037 显式交接）；" +
                "本角色未消费交接对象，关闭由 TransportHost 释放流完成，close_notify 与否本进程未观测");

            using CancellationTokenSource stopBudget = new(TimeSpan.FromSeconds(3));
            await context.Discovery.StopAsync(stopBudget.Token);

            log.WriteLine("[HOST] 已停机。把上面整段日志拷走，它就是被控端的证据。");

            if (!stop.AllFinished || activeAtStop != 0 || counters.HandlerFaults > 0)
            {
                return Finish(
                    run,
                    AcceptanceOutcome.Fail,
                    $"停机不干净：listenersStoppedCleanly={stop.AllFinished} " +
                    $"unfinishedConnections={stop.UnfinishedConnections} " +
                    $"acceptLoopsFinished={stop.AcceptLoopsFinished} " +
                    $"activeAtStop={activeAtStop} handlerFaults={counters.HandlerFaults}");
            }

            return Finish(
                run,
                AcceptanceOutcome.Pass,
                $"会话处理器共处理 {counters.SessionHandled} 条连接，停机时活动连接 0 条。");
        }
    }

    /// <summary>结算并写尾，返回<b>已结算</b>的结局。</summary>
    /// <remarks>
    /// 刻意返回 <see cref="AcceptanceOutcome"/> 而不是退出码：被控端这一侧
    /// 退出码由 <see cref="HeadlessRunner"/> 统一负责，角色实现只回答「结局是什么」。
    /// 两处各自算一遍退出码迟早会长歪。
    /// </remarks>
    private static AcceptanceOutcome Finish(AcceptanceRun run, AcceptanceOutcome outcome, string detail)
    {
        AcceptanceOutcome settled = run.Settle(outcome);
        AcceptanceRun.Finish(run, settled, detail);
        return settled;
    }

    /// <summary>
    /// 逐条连接的记账。
    /// </summary>
    /// <remarks>
    /// <para><b>会话处理器里必须自己捕获一切</b>：<see cref="TransportHost"/> 用
    /// <c>_ = Task.Run(() =&gt; HandleAsync(...))</c> 起连接处理，它的异常
    /// <b>无人观察</b>。本机实测（HANDOFF §15）确认未观察的 faulted Task
    /// <b>不会</b>终止进程，所以处理器里漏出来的异常会<b>完全静默</b>——
    /// 连接凭空消失，日志上一个字都没有。这里全部兜住并记账。</para>
    /// <para>终结态桶互斥：一个连接只进一个桶。</para>
    /// </remarks>
    private sealed class HostCounters
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, int> _buckets = new(StringComparer.Ordinal);
        private int _sessionHandled;
        private int _handlerFaults;
        private int _active;

        /// <summary>进入会话处理器的连接总数。</summary>
        public int SessionHandled => Volatile.Read(ref _sessionHandled);

        /// <summary>会话处理器自身抛异常的连接数。</summary>
        public int HandlerFaults => Volatile.Read(ref _handlerFaults);

        /// <summary>当前仍在处理器里的连接数。</summary>
        public int Active => Volatile.Read(ref _active);

        public async Task HandleAsync(
            AcceptanceRun run,
            AcceptedConnection connection,
            TransportTimeouts timeouts,
            CancellationToken cancellationToken)
        {
            int index = Interlocked.Increment(ref _sessionHandled);
            Interlocked.Increment(ref _active);

            string peer = $"{connection.RemoteAddress}:{connection.RemotePort}";
            DateTimeOffset began = DateTimeOffset.UtcNow;

            try
            {
                ControlPreAuthSession session = new();
                ControlPreAuthResult result =
                    await session.RunAsync(connection, timeouts, cancellationToken);

                long elapsed = (long)(DateTimeOffset.UtcNow - began).TotalMilliseconds;
                string bucket = result.Completed
                    ? "preauthenticated"
                    : $"rejected:{result.Rejection ?? "unknown"}";

                Record(bucket);

                run.Log.WriteLine(
                    $"[HOST][RESULT] conn#{index} peer={peer} " +
                    $"proto={connection.NegotiatedProtocol} " +
                    $"outcome={(result.Completed ? "PreAuthenticated" : "Rejected")} " +
                    $"rejection={result.Rejection ?? "-"} " +
                    $"state={result.State} elapsedMs={elapsed}");
            }
            catch (OperationCanceledException)
            {
                // 停机取消：不是被测行为，单独成桶，别混进 rejected。
                Record("cancelled-by-stop");
                run.Log.WriteLine($"[HOST][RESULT] conn#{index} peer={peer} outcome=Cancelled rejection=host-stop");
            }
            catch (Exception ex)
            {
                // 见类型说明：漏出去就彻底消失。
                Interlocked.Increment(ref _handlerFaults);
                Record("handler-faulted:" + ex.GetType().Name);
                run.ReportBackgroundFault($"HostRole 会话处理器 conn#{index} ({peer})", ex);
                run.Log.WriteLine(
                    $"[HOST][RESULT] conn#{index} peer={peer} " +
                    $"outcome=HarnessError rejection=handler-faulted " +
                    $"exception={ex.GetType().Name}");
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        /// <summary>把桶格式化成一行，并附上总和校验。</summary>
        public string FormatBuckets()
        {
            string body;
            int sum;

            lock (_gate)
            {
                sum = _buckets.Values.Sum();
                body = string.Join(
                    ", ",
                    _buckets.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => $"{pair.Key}={pair.Value}"));
            }

            bool partitionOk = sum == SessionHandled;

            return $"[HOST][BUCKETS] sessionHandled={SessionHandled} active={Active} " +
                   $"sum={sum} partitionOk={partitionOk} {{{body}}}";
        }

        private void Record(string bucket)
        {
            lock (_gate)
            {
                _buckets.TryGetValue(bucket, out int current);
                _buckets[bucket] = current + 1;
            }
        }
    }
}
