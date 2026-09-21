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
                helloTimeout: TimeSpan.FromSeconds(2));

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
                helloTimeout: TimeSpan.FromSeconds(2));

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
                helloTimeout: TimeSpan.FromSeconds(2));

            // 第二条：TCP 会被内核收下（backlog 队列），但应用层不握手——没有名额。
            await Assert.ThrowsAnyAsync<Exception>(
                () => connector.ConnectAsync(target, shortBudget));

            Assert.Equal(1, host.AdmittedConnections);
            Assert.Equal(1, host.ActiveConnections);

            release.SetResult();
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
                helloTimeout: TimeSpan.FromSeconds(5)),
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

        bool allFinished = await host.StopAsync(TimeSpan.FromSeconds(5));

        Assert.True(allFinished, "停机会先取消、再强制释放 socket，handler 必须全部结束。");
        Assert.Equal(0, host.ActiveConnections);
        Assert.Equal(0, host.AdmittedConnections);

        foreach (TcpClient client in rawClients)
        {
            client.Dispose();
        }

        await host.DisposeAsync();
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
