using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using LanRemote.Core.Abstractions;
using Xunit.Abstractions;

namespace LanRemote.Transport.Tests;

/// <summary>
/// <b>M3 步骤 15 的实测</b>：在真实 <c>SslStream</c> 上量「卡死的连接要多久才被切断」，
/// 以及取消时抛出的<b>实际</b>异常类型。
/// </summary>
/// <remarks>
/// <para>为什么必须真跑：取消延迟和异常类型都是<b>本机 .NET 10 / Schannel 的行为</b>，
/// 不能靠推理、更不能问模型。用 <c>MemoryStream</c> 替身量出来的类型
/// （<c>TaskCanceledException</c>，见 <see cref="FrameReaderTests"/>）是替身自己的，
/// 与 <c>SslStream</c> 无关。</para>
/// <para>四个场景：握手卡死 / 半个长度前缀 / 半个 payload / pre-auth 空闲。
/// 每个场景除了量时间，还要断言<b>处理器确实结束了</b>且<b>准入名额归还</b>——
/// 只量时间不查名额，等于漏掉「连接泄漏」这个真正的失败模式。</para>
/// <para>客户端在这几个用例里扮演攻击者：<b>故意不说话</b>。
/// 因此客户端必须一直挂着，等服务端量完再断开，否则服务端看到的是 EOF 而不是超时，
/// 测出来的就不是我们要的东西。</para>
/// <para><b>实测结果（2026-09-21，本机 Win11 25H2 / 26200，回环，真实 SslStream）</b>：</para>
/// <list type="table">
/// <listheader><term>场景</term><description>时限 → 实测 / 异常类型</description></listheader>
/// <item><term>pre-auth 空闲</term><description>400 ms → <b>413 ms</b>，<c>System.OperationCanceledException</c></description></item>
/// <item><term>半截长度前缀</term><description>400 ms → <b>400 ms</b>，<c>System.OperationCanceledException</c></description></item>
/// <item><term>半截 payload</term><description>600 ms → <b>608 ms</b>，<c>System.OperationCanceledException</c></description></item>
/// <item><term>握手卡死 ×3</term><description>400 ms → <b>436 ms</b> 名额全部归还，<b>未</b>调用 <c>StopAsync</c></description></item>
/// </list>
/// <para>两点结论：
/// ① deadline 的执行开销在 <b>0–36 ms</b> 量级，可以放心用几百毫秒级的时限；
/// ② <c>SslStream</c> 上的取消抛出的是<b>基类</b> <c>OperationCanceledException</c>，
/// 与测试替身里的 <c>TaskCanceledException</c>（见 <see cref="FrameReaderTests"/>）<b>不是同一个类型</b>——
/// 所以断言只要求「是取消」，不锁死具体子类。</para>
/// <para><b>本步骤只量了「时限执行得准不准」，没有量「这几个数值本身合不合适」。</b>
/// 五个数值仍需第二轮外部评审，不在本机实测范围内。</para>
/// </remarks>
public sealed class PreAuthDeadlineTests(ITestOutputHelper output)
{
    private static readonly Guid DeviceId = Guid.Parse("99999999-8888-7777-6666-555555555555");

    // 上限给得比较松：CI 机器抖动时，宁可断言「没有早退」+「没有挂死」，
    // 也不要卡死在某个精确到毫秒的数字上。真正关心的是量级。
    private static readonly TimeSpan PrefixDeadline = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan PayloadDeadline = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan HandshakeDeadline = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 场景 1：握完手一个字节都不发 —— 应当卡在<b>长度前缀</b>那一段被切断。
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Idle_Client_Is_Cut_Off_At_The_Length_Prefix_Deadline()
    {
        HandlerOutcome outcome = await RunServerSideAsync(
            PrefixDeadline,
            PayloadDeadline,
            client: async (connection, stall) =>
            {
                // 什么都不写，等服务端自己超时。
                await Task.Delay(Timeout.Infinite, stall);
                await connection.Stream.FlushAsync();
            });

        AssertCutOffAt(outcome, PrefixDeadline, "pre-auth 空闲");
    }

    /// <summary>
    /// 场景 2：只发半个长度前缀（2 字节）就沉默。
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Half_Length_Prefix_Is_Cut_Off_At_The_Same_Deadline()
    {
        HandlerOutcome outcome = await RunServerSideAsync(
            PrefixDeadline,
            PayloadDeadline,
            client: async (connection, stall) =>
            {
                await connection.Stream.WriteAsync(new byte[] { 0x00, 0x00 });
                await connection.Stream.FlushAsync();
                await Task.Delay(Timeout.Infinite, stall);
            });

        AssertCutOffAt(outcome, PrefixDeadline, "半截长度前缀");
    }

    /// <summary>
    /// 场景 3：长度前缀声称有 64 字节，但只发 10 个 ——
    /// 应当卡在<b>payload</b> 那一段被切断，且用的是 payload 自己的预算。
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Half_Payload_Is_Cut_Off_At_The_Payload_Deadline()
    {
        HandlerOutcome outcome = await RunServerSideAsync(
            PrefixDeadline,
            PayloadDeadline,
            client: async (connection, stall) =>
            {
                byte[] frame = new byte[4 + 10];
                frame[3] = 64; // 大端：声称 64 字节 payload
                await connection.Stream.WriteAsync(frame);
                await connection.Stream.FlushAsync();
                await Task.Delay(Timeout.Infinite, stall);
            });

        AssertCutOffAt(outcome, PayloadDeadline, "半截 payload");
    }

    /// <summary>
    /// 场景 4：TCP 连上但永远不开始 TLS —— Host 必须在<b>握手</b>时限上放手并归还名额，
    /// <b>不需要</b>等到停机。
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Stalled_Handshake_Is_Cut_Off_Without_Shutdown_And_Releases_The_Slot()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        int port = GetFreePort();

        TransportHostOptions options = new()
        {
            Port = port,
            MaxConnections = 4,
            MaxConnectionsPerAddress = 4,
            Timeouts = BuildTimeouts(HandshakeDeadline, PrefixDeadline, PayloadDeadline),
        };

        TransportHost host = new(
            new[] { IPAddress.Loopback },
            new StubSubnetPolicy(allow: true),
            certificate,
            (_, _) => Task.CompletedTask,
            options);

        await using (host)
        {
            TransportHostStartResult start = host.Start();
            Assert.True(start.IsListening);

            List<TcpClient> raw = new();
            Stopwatch stopwatch = Stopwatch.StartNew();

            for (int i = 0; i < 3; i++)
            {
                TcpClient client = new();
                await client.ConnectAsync(IPAddress.Loopback, port);
                raw.Add(client);
            }

            Assert.True(await WaitUntilAsync(() => host.AdmittedConnections == 3));

            // 不调用 StopAsync：只靠握手时限自己把连接收掉。
            Assert.True(
                await WaitUntilAsync(() => host.AdmittedConnections == 0),
                "握手卡死的连接应当在握手时限内被放手，而不是等到停机。");

            stopwatch.Stop();

            output.WriteLine(
                $"[步骤15实测] 三条卡在握手的连接：时限 {HandshakeDeadline.TotalMilliseconds:F0} ms，" +
                $"从连上到名额全部归还耗时 {stopwatch.Elapsed.TotalMilliseconds:F0} ms（未调用 StopAsync）。");

            foreach (TcpClient client in raw)
            {
                client.Dispose();
            }

            Assert.True(
                await WaitUntilAsync(() => host.ActiveConnections == 0),
                "准入归还了但登记表没清，就是连接泄漏。");

            Assert.True(
                stopwatch.Elapsed < HandshakeDeadline + Slack,
                $"实测耗时 {stopwatch.Elapsed}，不像是被握手时限（{HandshakeDeadline}）切断的。");
        }
    }

    /// <summary>
    /// 跑一次「客户端 TLS 连上 → 按脚本使坏 → 服务端量结果」。
    /// </summary>
    /// <param name="prefixDeadline">服务端长度前缀时限。</param>
    /// <param name="payloadDeadline">服务端 payload 时限。</param>
    /// <param name="client">
    /// 客户端脚本；<c>stall</c> 被取消时才允许它收摊——
    /// 保证服务端看到的是<b>超时</b>而不是 EOF。
    /// </param>
    private static async Task<HandlerOutcome> RunServerSideAsync(
        TimeSpan prefixDeadline,
        TimeSpan payloadDeadline,
        Func<TlsConnection, CancellationToken, Task> client)
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        int port = GetFreePort();

        TaskCompletionSource<HandlerOutcome> outcome =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        using CancellationTokenSource stall = new();

        TransportHostOptions options = new()
        {
            Port = port,
            Timeouts = BuildTimeouts(HandshakeDeadline, prefixDeadline, payloadDeadline),
        };

        TransportHost host = new(
            new[] { IPAddress.Loopback },
            new StubSubnetPolicy(allow: true),
            certificate,
            async (connection, cancellationToken) =>
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                try
                {
                    FrameReader reader = new(connection.Stream);
                    byte[] frame = await reader.ReadFrameAsync(
                        TransportConstants.MaxPreAuthMessageBytes,
                        options.Timeouts.LengthPrefixTimeout,
                        options.Timeouts.PayloadTimeout,
                        cancellationToken);

                    stopwatch.Stop();
                    outcome.TrySetResult(HandlerOutcome.Completed(frame.Length, stopwatch.Elapsed));
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    outcome.TrySetResult(HandlerOutcome.Failed(ex, stopwatch.Elapsed));
                }
            },
            options);

        await using (host)
        {
            TransportHostStartResult start = host.Start();
            Assert.True(start.IsListening);

            ConnectionTarget target = CreateTarget(
                port,
                TestCertificateFactory.Fingerprint(certificate));

            TlsConnection connection = await new TlsClientConnector().ConnectAsync(target);

            Task clientTask = Task.Run(() => client(connection, stall.Token));

            Task<HandlerOutcome> outcomeTask = outcome.Task;
            Task finished = await Task.WhenAny(outcomeTask, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.Same(outcomeTask, finished);

            HandlerOutcome result = await outcomeTask;

            // 服务端量完了，客户端可以收摊。
            stall.Cancel();
            connection.Dispose();

            try
            {
                await clientTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // 攻击者脚本被唤醒后可能抛取消、也可能抛 IO——都不影响结论。
            }

            // 名额与登记表必须回到 0，否则就是连接泄漏。
            Assert.True(await WaitUntilAsync(() => host.ActiveConnections == 0));
            Assert.True(await WaitUntilAsync(() => host.AdmittedConnections == 0));

            return result;
        }
    }

    private void AssertCutOffAt(HandlerOutcome outcome, TimeSpan deadline, string scenario)
    {
        // 步骤 15 的交付物是「量出来的数」，不是断言本身：
        // HANDOFF 里记录的异常类型与耗时必须来自这行输出。
        output.WriteLine(
            $"[步骤15实测] {scenario}：时限 {deadline.TotalMilliseconds:F0} ms，" +
            $"实测耗时 {outcome.Elapsed.TotalMilliseconds:F0} ms，" +
            $"异常 {outcome.ErrorType ?? "无"}");

        Assert.False(
            outcome.Succeeded,
            $"「{scenario}」本该超时，却读到了 {outcome.Bytes} 字节。");

        // 断言消息里带上实测类型：一旦 SslStream 抛的不是取消，失败信息直接给出真相。
        Assert.True(
            outcome.Error is OperationCanceledException,
            $"「{scenario}」实测异常类型是 {outcome.ErrorType}：{outcome.Error?.Message}");

        Assert.True(
            outcome.Elapsed >= deadline * 0.8,
            $"「{scenario}」过早返回：{outcome.Elapsed}，时限是 {deadline}。");

        Assert.True(
            outcome.Elapsed < deadline + Slack,
            $"「{scenario}」耗时 {outcome.Elapsed}，超过时限 {deadline} 太多。");
    }

    private static TransportTimeouts BuildTimeouts(
        TimeSpan handshake,
        TimeSpan prefix,
        TimeSpan payload) =>
        new(
            connectTimeout: TimeSpan.FromSeconds(3),
            handshakeTimeout: handshake,
            lengthPrefixTimeout: prefix,
            payloadTimeout: payload,
            helloTimeout: TimeSpan.FromSeconds(2),
            // 信封给得远高于本文件的场景时限（400–600 ms），不参与这些用例的判定；
            // 信封自身的专项测试在 ControlPreAuthSessionTests（缩放值）。
            preAuthEnvelopeTimeout: TimeSpan.FromSeconds(10));

    private static ConnectionTarget CreateTarget(int port, string pinHex)
    {
        bool created = ConnectionTarget.TryCreate(
            DeviceId,
            IPAddress.Loopback,
            port,
            pinHex,
            out ConnectionTarget? target);

        Assert.True(created, "测试前置条件：目标快照应能创建。");
        return target!;
    }

    private static int GetFreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 400; i++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }

    /// <summary>服务端会话处理器量到的结局。</summary>
    private sealed record HandlerOutcome(
        bool Succeeded,
        int Bytes,
        TimeSpan Elapsed,
        Exception? Error,
        string? ErrorType)
    {
        public static HandlerOutcome Completed(int bytes, TimeSpan elapsed) =>
            new(true, bytes, elapsed, null, null);

        public static HandlerOutcome Failed(Exception error, TimeSpan elapsed) =>
            new(false, 0, elapsed, error, error.GetType().FullName);
    }

    private sealed class StubSubnetPolicy : ISubnetPolicy
    {
        private readonly bool _allow;

        public StubSubnetPolicy(bool allow)
        {
            _allow = allow;
        }

        public bool IsAllowedPeer(IPAddress localAddress, IPAddress remoteAddress) => _allow;
    }
}
