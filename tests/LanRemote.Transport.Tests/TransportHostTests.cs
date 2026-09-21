using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using LanRemote.Core.Abstractions;
using LanRemote.Discovery.Networking;

namespace LanRemote.Transport.Tests;

/// <summary>
/// <see cref="TransportHost"/> 的真实回环行为：accept → 同子网 → 准入 → TLS 的顺序与停机收尾。
/// </summary>
/// <remarks>
/// <para>本机没有合格的 RFC1918 网卡（唯一活跃地址 <c>172.100.166.220</c> 也不是私网），
/// 所以 TCP 层用回环地址。<c>ISubnetPolicy</c> 是可替换的接口，因此「放行回环」用测试替身表达，
/// 而「真实策略会拒绝回环（因为 127/8 不是 RFC1918）」单独作为一个用例保留——
/// 它反过来证明这道闸门真的在生效，不是摆设。</para>
/// </remarks>
public sealed class TransportHostTests
{
    private static readonly Guid DeviceId = Guid.Parse("99999999-8888-7777-6666-555555555555");

    [Fact(Timeout = 60_000)]
    public async Task Accepts_Tls_Connection_And_Releases_Admission_When_It_Ends()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        int port = GetFreePort();

        TaskCompletionSource<AcceptedConnection> accepted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource handlerDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TransportHostOptions options = new() { Port = port };
        TransportHost host = new(
            new[] { IPAddress.Loopback },
            new StubSubnetPolicy(allow: true),
            certificate,
            async (connection, _) =>
            {
                accepted.TrySetResult(connection);
                await release.Task;
                handlerDone.TrySetResult();
            },
            options);

        await using (host)
        {
            TransportHostStartResult start = host.Start();
            Assert.True(start.IsListening);
            Assert.Empty(start.Failures);

            ConnectionTarget target = CreateTarget(port, TestCertificateFactory.Fingerprint(certificate));
            using TlsConnection connection = await new TlsClientConnector().ConnectAsync(target);

            AcceptedConnection serverSide = await WaitResultAsync(accepted);
            Assert.Equal(IPAddress.Loopback, serverSide.RemoteAddress);
            Assert.Contains(
                serverSide.NegotiatedProtocol,
                new[] { SslProtocols.Tls12, SslProtocols.Tls13 });

            Assert.Equal(1, host.ActiveConnections);
            Assert.Equal(1, host.AdmittedConnections);

            // 会话处理器结束时，名额必须归还——否则第二个连接永远进不来。
            release.SetResult();
            await WaitAsync(handlerDone.Task);

            Assert.True(await WaitUntilAsync(() => host.ActiveConnections == 0));
            Assert.Equal(0, host.AdmittedConnections);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Rejects_Peer_That_Fails_Subnet_Check_Before_Any_Tls()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        int port = GetFreePort();

        TaskCompletionSource<AcceptedConnection> accepted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        TransportHost host = new(
            new[] { IPAddress.Loopback },
            new StubSubnetPolicy(allow: false),
            certificate,
            (connection, _) =>
            {
                accepted.TrySetResult(connection);
                return Task.CompletedTask;
            },
            new TransportHostOptions { Port = port });

        await using (host)
        {
            host.Start();

            ConnectionTarget target = CreateTarget(port, TestCertificateFactory.Fingerprint(certificate));
            TransportTimeouts shortBudget = new(
                connectTimeout: TimeSpan.FromSeconds(2),
                handshakeTimeout: TimeSpan.FromMilliseconds(500),
                lengthPrefixTimeout: TimeSpan.FromSeconds(2),
                payloadTimeout: TimeSpan.FromSeconds(2),
                helloTimeout: TimeSpan.FromSeconds(2),
                preAuthEnvelopeTimeout: TimeSpan.FromSeconds(8));

            await Assert.ThrowsAnyAsync<Exception>(
                () => new TlsClientConnector().ConnectAsync(target, shortBudget));

            Assert.False(accepted.Task.IsCompleted, "未通过同子网校验的连接不该走到会话处理器。");
            Assert.Equal(0, host.ActiveConnections);

            // 关键：被拒的连接根本没占用名额——准入在 TLS 之前，而子网校验在准入之前。
            Assert.Equal(0, host.AdmittedConnections);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Real_SubnetPolicy_Rejects_Loopback_Peer_Because_It_Is_Not_Rfc1918()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        int port = GetFreePort();

        // 真实策略 + 一张「本地地址是 127.0.0.1/24」的绑定：
        // 网络号算得出来，但 127/8 不是 RFC1918，所以必须被拒。
        NetworkBinding loopbackBinding = new(
            "loopback",
            "Loopback",
            System.Net.NetworkInformation.NetworkInterfaceType.Loopback,
            IPAddress.Loopback,
            IPAddress.Parse("255.255.255.0"),
            IPAddress.Parse("127.0.0.255"),
            1);

        SubnetPolicy policy = new(new StubBindingProvider(loopbackBinding));

        TaskCompletionSource<AcceptedConnection> accepted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        TransportHost host = new(
            new[] { IPAddress.Loopback },
            policy,
            certificate,
            (connection, _) =>
            {
                accepted.TrySetResult(connection);
                return Task.CompletedTask;
            },
            new TransportHostOptions { Port = port });

        await using (host)
        {
            host.Start();

            ConnectionTarget target = CreateTarget(port, TestCertificateFactory.Fingerprint(certificate));
            TransportTimeouts shortBudget = new(
                connectTimeout: TimeSpan.FromSeconds(2),
                handshakeTimeout: TimeSpan.FromMilliseconds(500),
                lengthPrefixTimeout: TimeSpan.FromSeconds(2),
                payloadTimeout: TimeSpan.FromSeconds(2),
                helloTimeout: TimeSpan.FromSeconds(2),
                preAuthEnvelopeTimeout: TimeSpan.FromSeconds(8));

            await Assert.ThrowsAnyAsync<Exception>(
                () => new TlsClientConnector().ConnectAsync(target, shortBudget));

            Assert.False(accepted.Task.IsCompleted);
            Assert.Equal(0, host.AdmittedConnections);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Admission_Limit_Refuses_The_Extra_Connection()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        int port = GetFreePort();

        TaskCompletionSource firstAccepted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TransportHostOptions options = new()
        {
            Port = port,
            MaxConnections = 1,
            MaxConnectionsPerAddress = 1,
        };

        TransportHost host = new(
            new[] { IPAddress.Loopback },
            new StubSubnetPolicy(allow: true),
            certificate,
            async (_, _) =>
            {
                firstAccepted.TrySetResult();
                await release.Task;
            },
            options);

        await using (host)
        {
            host.Start();

            ConnectionTarget target = CreateTarget(port, TestCertificateFactory.Fingerprint(certificate));
            TlsClientConnector connector = new();

            using TlsConnection first = await connector.ConnectAsync(target);
            await WaitAsync(firstAccepted.Task);
            Assert.Equal(1, host.AdmittedConnections);

            TransportTimeouts shortBudget = new(
                connectTimeout: TimeSpan.FromSeconds(2),
                handshakeTimeout: TimeSpan.FromMilliseconds(500),
                lengthPrefixTimeout: TimeSpan.FromSeconds(2),
                payloadTimeout: TimeSpan.FromSeconds(2),
                helloTimeout: TimeSpan.FromSeconds(2),
                preAuthEnvelopeTimeout: TimeSpan.FromSeconds(8));

            // 第二条：TCP 会被内核收下（backlog 队列），但应用层不握手——没有名额。
            await Assert.ThrowsAnyAsync<Exception>(
                () => connector.ConnectAsync(target, shortBudget));

            Assert.Equal(1, host.AdmittedConnections);
            Assert.Equal(1, host.ActiveConnections);

            release.SetResult();
        }
    }

    /// <summary>
    /// <b>M3.1 B15</b>：准入限额在<b>所有 listener 之间共享</b>——一个 listener 收下的连接，
    /// 会让另一个 listener 上的新连接被拒。
    /// </summary>
    /// <remarks>
    /// <para>设计要点：拒绝原因必须<b>无歧义</b>地指向全局闸门。办法是让被拒连接走一个
    /// <b>不同的源地址桶</b>（每源限额为空）——这样「每源限额」不可能是拒绝原因，
    /// 断言组里连桶的读数一并核验。</para>
    /// <para>实测事实（2026-09-21，本机）：出站 socket 显式绑定源 <c>127.0.0.2</c> 可用，
    /// 服务端看到的 remote 就是 <c>127.0.0.2</c>；不绑定时连 <c>127.0.0.2</c> 的源是 <c>127.0.0.1</c>。
    /// 见 <see cref="ConnectRawAsync"/>。</para>
    /// <list type="number">
    /// <item><description>A 从 listener1（<c>127.0.0.1</c>）进、源显式为 <c>127.0.0.2</c>，
    /// 只连 TCP 不说话，卡在握手中占住全局唯一名额（稳定可观测）；</description></item>
    /// <item><description>B 打 listener2（<c>127.0.0.2</c>）、源 <c>127.0.0.1</c>——它自己的每源桶是空的，
    /// 被拒就只能来自全局闸门；</description></item>
    /// <item><description>释放 A 后，C 走完整 TLS 打 listener2 成功——
    /// 排除「listener2 根本不可用」这个替代解释。</description></item>
    /// </list>
    /// </remarks>
    [Fact(Timeout = 60_000)]
    public async Task Admission_Limit_Is_Shared_Across_Listeners()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        int port = GetFreePort();

        IPAddress secondAddress = IPAddress.Parse("127.0.0.2");

        int handlerCalls = 0;

        TransportHostOptions options = new()
        {
            Port = port,
            MaxConnections = 1,
            MaxConnectionsPerAddress = 1,
        };

        TransportHost host = new(
            new[] { IPAddress.Loopback, secondAddress },
            new StubSubnetPolicy(allow: true),
            certificate,
            (_, _) =>
            {
                Interlocked.Increment(ref handlerCalls);
                return Task.CompletedTask;
            },
            options);

        await using (host)
        {
            TransportHostStartResult start = host.Start();
            Assert.True(start.IsListening);
            Assert.Equal(2, start.BoundAddresses.Count);
            Assert.Empty(start.Failures);

            // A：源显式 127.0.0.2 → listener1（127.0.0.1）。只连 TCP、不说话——卡在握手占住名额。
            using (TcpClient first = await ConnectRawAsync(secondAddress, IPAddress.Loopback, port))
            {
                Assert.True(
                    await WaitUntilAsync(() => host.AdmittedConnections == 1),
                    "A 应当在握手之前就占住名额。");

                Assert.Equal(1, host.Limiter.InUseFor(secondAddress));

                // B：源 127.0.0.1 → listener2（127.0.0.2）。它自己的每源桶是空的。
                using TcpClient second = await ConnectRawAsync(IPAddress.Loopback, secondAddress, port);

                Task<int> pending = second.GetStream().ReadAsync(new byte[1]).AsTask();
                Task finished = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(5)));

                Assert.True(
                    finished == pending,
                    "被拒连接没有被关闭——要么被错误地接受了，要么根本没被处理。");

                bool refused;
                try
                {
                    int read = await pending;
                    refused = read == 0;
                }
                catch (IOException)
                {
                    // 对端拆连接也算「被关闭」。
                    refused = true;
                }

                Assert.True(refused, "被拒连接必须被服务端关闭（EOF 或连接重置）。");

                // 拒绝原因的唯一性证据：全局已满（1/1），而 B 自己的桶是空的（0/1）。
                Assert.Equal(1, host.Limiter.GlobalInUse);
                Assert.Equal(0, host.Limiter.InUseFor(IPAddress.Loopback));
                Assert.Equal(1, host.ActiveConnections);
                Assert.Equal(0, Volatile.Read(ref handlerCalls));
            }

            // 释放 A（原始连接被拆）→ 名额必须归还。
            Assert.True(await WaitUntilAsync(() => host.AdmittedConnections == 0));
            Assert.True(await WaitUntilAsync(() => host.ActiveConnections == 0));

            // 对照组：完整 TLS 打 listener2，成功。
            ConnectionTarget target = CreateTarget(
                secondAddress, port, TestCertificateFactory.Fingerprint(certificate));
            using TlsConnection control = await new TlsClientConnector().ConnectAsync(target);

            Assert.True(control.Stream.IsAuthenticated);
            Assert.True(await WaitUntilAsync(() => Volatile.Read(ref handlerCalls) == 1));
            Assert.True(await WaitUntilAsync(() => host.ActiveConnections == 0));
        }
    }

    /// <summary>
    /// <b>M3.1 B20</b>：握手<b>失败</b>的两条异常分支（协议错误 / 连接被重置）都必须归还名额。
    /// </summary>
    /// <remarks>
    /// <para>与既有用例的分工：<c>PreAuthDeadlineTests.Stalled_Handshake…</c> 覆盖<b>时限到点</b>分支；
    /// 本用例覆盖<b>失败异常</b>分支，并断言失败是<b>快速</b>发生的（远早于 10 s 握手时限）——
    /// 否则「失败路径」与「时限路径」就分不开。</para>
    /// <para>每轮都先让连接卡在握手（名额占用是稳定可观测状态），再分别把两条分支走一遍；
    /// 最后用「限额=1 时完整 TLS 仍能成功」做对照。</para>
    /// </remarks>
    [Fact(Timeout = 60_000)]
    public async Task Admission_Is_Returned_When_The_Tls_Handshake_Fails()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        int port = GetFreePort();

        int handlerCalls = 0;

        TransportHostOptions options = new()
        {
            Port = port,
            MaxConnections = 1,
            MaxConnectionsPerAddress = 1,
            Timeouts = new TransportTimeouts(
                connectTimeout: TimeSpan.FromSeconds(2),
                // 给到 10 s：每轮失败都必须远早于它。
                handshakeTimeout: TimeSpan.FromSeconds(10),
                lengthPrefixTimeout: TimeSpan.FromSeconds(5),
                payloadTimeout: TimeSpan.FromSeconds(5),
                helloTimeout: TimeSpan.FromSeconds(2),
                preAuthEnvelopeTimeout: TimeSpan.FromSeconds(8)),
        };

        TransportHost host = new(
            new[] { IPAddress.Loopback },
            new StubSubnetPolicy(allow: true),
            certificate,
            (_, _) =>
            {
                Interlocked.Increment(ref handlerCalls);
                return Task.CompletedTask;
            },
            options);

        await using (host)
        {
            host.Start();

            // 第一轮：畸形握手记录——快速失败（AuthenticationException 分支）。
            using (TcpClient broken = await ConnectRawAsync(IPAddress.Loopback, IPAddress.Loopback, port))
            {
                Assert.True(await WaitUntilAsync(() => host.AdmittedConnections == 1));

                Stopwatch clock = Stopwatch.StartNew();

                byte[] malformed = new byte[]
                {
                    0x16, 0x03, 0x01, 0x00, 0x08, // handshake 记录头，声明 8 字节体
                    0xAB, 0xAB, 0xAB, 0xAB, 0xAB, 0xAB, 0xAB, 0xAB, // 体不是合法的握手消息
                };

                await broken.GetStream().WriteAsync(malformed);
                await broken.GetStream().FlushAsync();

                Assert.True(
                    await WaitUntilAsync(() => host.AdmittedConnections == 0),
                    "握手失败后名额必须归还。");

                clock.Stop();

                Assert.True(
                    clock.Elapsed < TimeSpan.FromSeconds(5),
                    $"归还耗时 {clock.Elapsed}——像是等到了 10 s 的握手时限，而不是失败路径。");
            }

            Assert.True(await WaitUntilAsync(() => host.ActiveConnections == 0));

            // 第二轮：卡住后 RST——快速失败（IOException 分支）。
            using (TcpClient reset = await ConnectRawAsync(IPAddress.Loopback, IPAddress.Loopback, port))
            {
                Assert.True(await WaitUntilAsync(() => host.AdmittedConnections == 1));

                reset.Client.LingerState = new LingerOption(enable: true, seconds: 0);
                reset.Dispose();

                Assert.True(
                    await WaitUntilAsync(() => host.AdmittedConnections == 0),
                    "连接被重置后名额必须归还。");
            }

            Assert.True(await WaitUntilAsync(() => host.ActiveConnections == 0));

            // 对照组：限额=1 的世界里完整 TLS 仍能成功——名额真的回来了、Host 没被弄坏。
            ConnectionTarget target = CreateTarget(port, TestCertificateFactory.Fingerprint(certificate));
            using TlsConnection control = await new TlsClientConnector().ConnectAsync(target);

            Assert.True(control.Stream.IsAuthenticated);
            Assert.True(await WaitUntilAsync(() => Volatile.Read(ref handlerCalls) == 1));
        }
    }

    /// <summary>
    /// <b>M3.1 B20</b>：会话处理器抛异常后，名额与登记项都必须归还，且 Host 仍可用。
    /// </summary>
    /// <remarks>
    /// 处理器异常不会被 <see cref="TransportHost"/> 的任何 <c>catch</c> 接住（那是会话层的职责），
    /// 它会顺着 <c>HandleAsync</c> 逃逸；本用例要求 <c>finally</c> 先把资源归还。
    /// 证伪方式：限额=1 时第二条连接仍能成功——若名额泄漏，第二条会被拒。
    /// （处理器异常的<b>上报</b>不在 M3.1 范围：那是 M9 诊断里程碑的活。）
    /// </remarks>
    [Fact(Timeout = 60_000)]
    public async Task Admission_Is_Returned_When_The_Session_Handler_Throws()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        int port = GetFreePort();

        int handlerCalls = 0;
        TaskCompletionSource firstHandled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TransportHostOptions options = new()
        {
            Port = port,
            MaxConnections = 1,
            MaxConnectionsPerAddress = 1,
        };

        TransportHost host = new(
            new[] { IPAddress.Loopback },
            new StubSubnetPolicy(allow: true),
            certificate,
            (_, _) =>
            {
                if (Interlocked.Increment(ref handlerCalls) == 1)
                {
                    firstHandled.TrySetResult();
                    throw new InvalidOperationException("测试替身：会话处理器故意抛异常。");
                }

                return Task.CompletedTask;
            },
            options);

        await using (host)
        {
            host.Start();

            ConnectionTarget target = CreateTarget(port, TestCertificateFactory.Fingerprint(certificate));

            using (TlsConnection first = await new TlsClientConnector().ConnectAsync(target))
            {
                await WaitAsync(firstHandled.Task);
            }

            Assert.True(
                await WaitUntilAsync(() => host.ActiveConnections == 0),
                "处理器抛异常后登记项必须清掉。");
            Assert.True(
                await WaitUntilAsync(() => host.AdmittedConnections == 0),
                "处理器抛异常后名额必须归还。");

            // 对照：限额=1 的世界里第二条还能成功——证明①名额真的回来了、②Host 没被异常弄坏。
            using TlsConnection second = await new TlsClientConnector().ConnectAsync(target);

            Assert.True(second.Stream.IsAuthenticated);
            Assert.True(await WaitUntilAsync(() => Volatile.Read(ref handlerCalls) == 2));
            Assert.True(await WaitUntilAsync(() => host.ActiveConnections == 0));
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Stop_Cancels_Connections_Stuck_In_Handshake_And_Releases_Admission()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        int port = GetFreePort();

        TransportHostOptions options = new()
        {
            Port = port,
            MaxConnections = 4,
            MaxConnectionsPerAddress = 4,
            Timeouts = new TransportTimeouts(
                connectTimeout: TimeSpan.FromSeconds(2),
                handshakeTimeout: TimeSpan.FromSeconds(60),
                lengthPrefixTimeout: TimeSpan.FromSeconds(5),
                payloadTimeout: TimeSpan.FromSeconds(5),
                helloTimeout: TimeSpan.FromSeconds(5),
                preAuthEnvelopeTimeout: TimeSpan.FromSeconds(8)),
        };

        TransportHost host = new(
            new[] { IPAddress.Loopback },
            new StubSubnetPolicy(allow: true),
            certificate,
            (_, _) => Task.CompletedTask,
            options);

        host.Start();

        // 连上 TCP 但永不做 TLS：handler 会一直卡在 AuthenticateAsServerAsync。
        var rawClients = new List<TcpClient>();
        for (int i = 0; i < 3; i++)
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            rawClients.Add(client);
        }

        Assert.True(await WaitUntilAsync(() => host.ActiveConnections == 3));
        Assert.Equal(3, host.AdmittedConnections);

        TransportHostStopReport stop = await host.StopAsync(TimeSpan.FromSeconds(5));

        Assert.True(stop.AllFinished, "停机会先取消、再强制释放 socket，handler 必须全部结束。");
        Assert.True(stop.AcceptLoopsFinished, "accept 循环应当先于连接结束。");
        Assert.Equal(0, stop.UnfinishedConnections);
        Assert.Equal(0, host.ActiveConnections);
        Assert.Equal(0, host.AdmittedConnections);

        foreach (TcpClient client in rawClients)
        {
            client.Dispose();
        }

        await host.DisposeAsync();
    }

    /// <summary>
    /// <b>M3.1 B18</b>：handler 不可打断时，停机报告必须给出
    /// 「accept 循环停了、但还有 1 条连接没结束」——两个维度可区分，且在预算内返回。
    /// </summary>
    /// <remarks>
    /// 既有用例 <c>Stop_Cancels_Connections_Stuck_In_Handshake…</c> 覆盖的是 <b>true 路径</b>
    /// （取消 + 强制释放总能收掉 handler）。这里反过来：handler 不听取消令牌、
    /// 也不受 socket 被砸的影响（它在等一个测试自己控制的 TCS），
    /// 于是 <c>UnfinishedConnections</c> 必须是 1——报告不允许把「没干净」吞成「干净」。
    /// </remarks>
    [Fact(Timeout = 60_000)]
    public async Task Stop_Reports_The_Connection_That_Survived_The_Shutdown_Budget()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        int port = GetFreePort();

        TaskCompletionSource releaseHandler = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TransportHostOptions options = new()
        {
            Port = port,
            Timeouts = new TransportTimeouts(
                connectTimeout: TimeSpan.FromSeconds(2),
                handshakeTimeout: TimeSpan.FromSeconds(2),
                lengthPrefixTimeout: TimeSpan.FromSeconds(2),
                payloadTimeout: TimeSpan.FromSeconds(2),
                helloTimeout: TimeSpan.FromSeconds(2),
                preAuthEnvelopeTimeout: TimeSpan.FromSeconds(8)),
        };

        TransportHost host = new(
            new[] { IPAddress.Loopback },
            new StubSubnetPolicy(allow: true),
            certificate,
            async (_, _) =>
            {
                // 不看取消令牌、不等 socket 事件：停机叫不醒它。
                await releaseHandler.Task;
            },
            options);

        host.Start();

        try
        {
            ConnectionTarget target = CreateTarget(port, TestCertificateFactory.Fingerprint(certificate));
            using TlsConnection connection = await new TlsClientConnector().ConnectAsync(target);

            Assert.True(await WaitUntilAsync(() => host.ActiveConnections == 1));

            Stopwatch clock = Stopwatch.StartNew();
            TransportHostStopReport stop = await host.StopAsync(TimeSpan.FromMilliseconds(400));
            clock.Stop();

            // 两个维度必须可区分：accept 循环停了（true），连接没结束（1 条）。
            Assert.True(stop.AcceptLoopsFinished, "accept 循环应当先于连接收尾。");
            Assert.Equal(1, stop.UnfinishedConnections);
            Assert.False(stop.AllFinished, "handler 不可打断时，报告不允许把「没干净」说成「干净」。");

            Assert.True(
                clock.Elapsed < TimeSpan.FromSeconds(3),
                $"停机耗时 {clock.Elapsed}——必须在预算（400 ms）量级内返回，而不是无限等。");
        }
        finally
        {
            // 放掉卡住的 handler，别把「永不结束的任务」留在测试进程里。
            releaseHandler.TrySetResult();
            await host.DisposeAsync();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Start_Degrades_Per_Address_Instead_Of_Failing_Whole_Host()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        int port = GetFreePort();

        TransportHost host = new(
            new[] { IPAddress.Loopback, IPAddress.Loopback },
            new StubSubnetPolicy(allow: true),
            certificate,
            (_, _) => Task.CompletedTask,
            new TransportHostOptions { Port = port });

        await using (host)
        {
            TransportHostStartResult result = host.Start();

            // ADR-031：一个地址失败，其它地址照常监听。
            Assert.Single(result.BoundAddresses);
            Assert.Single(result.Failures);
            Assert.Equal(SocketError.AddressAlreadyInUse, result.Failures[0].Error);
            Assert.True(result.IsListening);
        }
    }

    /// <summary>
    /// 已有一个更宽、且未开 exclusive 的 bind 时，我们的具体地址 bind 会<b>成功</b>。
    /// </summary>
    /// <remarks>
    /// <para><b>这条是被一次失败逼出来改写</b>：我原先断言这种情况会以
    /// <see cref="SocketError.AccessDenied"/> 失败，实测并非如此——开不开
    /// <c>ExclusiveAddressUse</c> 都<b>会</b>成功（完整 2×2 矩阵见
    /// <see cref="MultiAddressListenTests"/>）。所以这里如实记录真实行为。</para>
    /// <para>这不是"端口被抢"：实测连到具体地址的连接会交给<b>更具体</b>的那个 listener
    /// （<c>MultiAddressListenTests.Connection_To_Specific_Address_Goes_To_The_More_Specific_Listener</c>），
    /// 流量不会跑到更宽的那个 socket 去。</para>
    /// <para>残余风险如实记录：我们<b>无法</b>从 bind 结果上发现"有人先占了更宽的地址"。
    /// 真正会被挡住的是「同地址同端口」（<see cref="SocketError.AddressAlreadyInUse"/>），
    /// 已在 <c>Start_Degrades_Per_Address_Instead_Of_Failing_Whole_Host</c> 里覆盖。</para>
    /// </remarks>
    [Fact(Timeout = 60_000)]
    public async Task Start_Succeeds_When_Another_Process_Holds_A_Wider_Non_Exclusive_Bind()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        int port = GetFreePort();

        // 刻意不开 ExclusiveAddressUse：模拟外部进程用默认设置的样子。
        var wildcard = new TcpListener(IPAddress.Any, port) { ExclusiveAddressUse = false };
        wildcard.Start();

        try
        {
            TransportHost host = new(
                new[] { IPAddress.Loopback },
                new StubSubnetPolicy(allow: true),
                certificate,
                (_, _) => Task.CompletedTask,
                new TransportHostOptions { Port = port });

            await using (host)
            {
                TransportHostStartResult result = host.Start();

                Assert.True(result.IsListening);
                Assert.Single(result.BoundAddresses);
                Assert.Empty(result.Failures);
            }
        }
        finally
        {
            wildcard.Stop();
        }
    }

    /// <summary>
    /// 同子网校验拿到的<b>本地地址</b>必须是「接受它的那个 listener 的地址」（triage A-6），
    /// 不是别的什么地址——掩码要靠它去查绑定，拿错了整道闸门就白设。
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Subnet_Check_Receives_The_Accepting_Listener_Address()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        int port = GetFreePort();

        RecordingSubnetPolicy policy = new(allow: true);

        TransportHost host = new(
            new[] { IPAddress.Loopback },
            policy,
            certificate,
            (_, _) => Task.CompletedTask,
            new TransportHostOptions { Port = port });

        await using (host)
        {
            host.Start();

            ConnectionTarget target = CreateTarget(port, TestCertificateFactory.Fingerprint(certificate));
            using TlsConnection connection = await new TlsClientConnector().ConnectAsync(target);

            Assert.True(await WaitUntilAsync(() => policy.LastLocal is not null));

            Assert.Equal(IPAddress.Loopback, policy.LastLocal);
            Assert.Equal(IPAddress.Loopback, policy.LastRemote);
        }
    }

    private static ConnectionTarget CreateTarget(int port, string pinHex) =>
        CreateTarget(IPAddress.Loopback, port, pinHex);

    private static ConnectionTarget CreateTarget(IPAddress address, int port, string pinHex)
    {
        bool created = ConnectionTarget.TryCreate(
            DeviceId,
            address,
            port,
            pinHex,
            out ConnectionTarget? target);

        Assert.True(created, "测试前置条件：目标快照应能创建。");
        return target!;
    }

    /// <summary>
    /// 连一条原始 TCP 连接（不做 TLS），源地址显式绑定——用来把「每源限额桶」钉死。
    /// </summary>
    /// <remarks>
    /// 实测事实（2026-09-21，本机）：出站 socket 可以绑定到回环别名（如 <c>127.0.0.2</c>），
    /// 且服务端看到的 remote 就是绑定的那个地址。这是 B15 用例里
    /// 「A 与 B 走不同每源桶」得以成立的前提。
    /// </remarks>
    private static async Task<TcpClient> ConnectRawAsync(
        IPAddress source, IPAddress destination, int port)
    {
        TcpClient client = new(AddressFamily.InterNetwork);
        try
        {
            client.Client.Bind(new IPEndPoint(source, 0));
            await client.ConnectAsync(destination, port);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task WaitAsync(Task task)
    {
        Task completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(task, completed);
        await task;
    }

    private static async Task<T> WaitResultAsync<T>(TaskCompletionSource<T> source)
    {
        Task<T> task = source.Task;
        Task completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(task, completed);
        return await task;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200; i++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
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

    private sealed class RecordingSubnetPolicy(bool allow) : ISubnetPolicy
    {
        public IPAddress? LastLocal { get; private set; }

        public IPAddress? LastRemote { get; private set; }

        public bool IsAllowedPeer(IPAddress localAddress, IPAddress remoteAddress)
        {
            LastLocal = localAddress;
            LastRemote = remoteAddress;
            return allow;
        }
    }

    private sealed class StubBindingProvider : INetworkBindingProvider
    {
        private readonly IReadOnlyList<NetworkBinding> _bindings;

        public StubBindingProvider(params NetworkBinding[] bindings)
        {
            _bindings = bindings;
        }

        public IReadOnlyList<NetworkBinding> GetBindings() => _bindings;

        public void Refresh()
        {
        }
    }
}
