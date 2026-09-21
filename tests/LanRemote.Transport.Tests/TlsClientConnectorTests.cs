using System.Diagnostics;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using LanRemote.Core.Models;
using LanRemote.Discovery;

namespace LanRemote.Transport.Tests;

/// <summary>
/// <see cref="TlsClientConnector"/> 的真实回环握手行为。
/// </summary>
/// <remarks>
/// <para>这里的每个用例都跑<b>真实</b> TLS——pinning 与证书形状只有在握手里才有意义。</para>
/// <para>服务端的异常由 <see cref="TestTlsServer"/> 收集：客户端只能看到
/// <c>IOException: unexpected EOF</c> 之类的表层信息，真实原因在对端。</para>
/// </remarks>
public sealed class TlsClientConnectorTests
{
    private static readonly Guid DeviceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact(Timeout = 60_000)]
    public async Task Connect_Succeeds_And_Captures_Presented_Pin()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        using TestTlsServer server = new(certificate);
        TlsClientConnector connector = new();

        ConnectionTarget target = CreateTarget(
            server.Port,
            TestCertificateFactory.Fingerprint(certificate));

        using TlsConnection connection = await connector.ConnectAsync(target);

        Assert.True(connection.Identity.PinsMatch, "期望指纹与实际指纹应一致。");
        Assert.Equal(
            TestCertificateFactory.Fingerprint(certificate),
            TestCertificateFactory.ToHex(connection.Identity.PresentedCertSha256.Span));
        Assert.Equal(DeviceId, connection.Identity.DeviceId);
        Assert.Contains(
            connection.NegotiatedProtocol,
            new[] { SslProtocols.Tls12, SslProtocols.Tls13 });
        Assert.Empty(server.Failures);
    }

    [Fact(Timeout = 60_000)]
    public async Task Connect_Rejects_Pin_Mismatch()
    {
        using X509Certificate2 serverCertificate = TestCertificateFactory.Create();
        using X509Certificate2 otherCertificate = TestCertificateFactory.Create();
        using TestTlsServer server = new(serverCertificate);
        TlsClientConnector connector = new();

        // 期望的是另一张证书的指纹：pin 必然不符。
        ConnectionTarget target = CreateTarget(
            server.Port,
            TestCertificateFactory.Fingerprint(otherCertificate));

        AuthenticationException exception = await Assert.ThrowsAsync<AuthenticationException>(
            () => connector.ConnectAsync(target));

        Assert.Contains(PeerCertificateValidator.RejectionPinMismatch, exception.Message);
    }

    [Fact(Timeout = 60_000)]
    public async Task Connect_Rejects_Expired_Certificate_Using_Injected_Clock()
    {
        DateTimeOffset realNow = DateTimeOffset.UtcNow;

        // 证书按真实时间仍然有效：这样服务端 Schannel 不会因过期自行拒绝，
        // 被拦下的原因就只剩「我们的校验器认为它过期」，结论不会被混淆。
        using X509Certificate2 certificate = TestCertificateFactory.Create(new TestCertificateOptions
        {
            NotBefore = realNow.AddMinutes(-5),
            NotAfter = realNow.AddYears(1),
        });

        using TestTlsServer server = new(certificate);
        TlsClientConnector connector = new();

        ConnectionTarget target = CreateTarget(
            server.Port,
            TestCertificateFactory.Fingerprint(certificate));

        FakeClock clock = new(realNow.AddYears(2));

        AuthenticationException exception = await Assert.ThrowsAsync<AuthenticationException>(
            () => connector.ConnectAsync(target, clock: clock));

        Assert.Contains(PeerCertificateValidator.RejectionExpired, exception.Message);
    }

    [Fact(Timeout = 60_000)]
    public async Task Handshake_Deadline_Fires_Even_When_Peer_Keeps_Silence()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        using TestTlsServer server = new(certificate)
        {
            // accept 之后一直不握手：客户端应当卡在握手里，直到自己的绝对 deadline 到了被切断。
            HandshakeGate = new ManualResetEventSlim(false),
        };

        TlsClientConnector connector = new();
        ConnectionTarget target = CreateTarget(
            server.Port,
            TestCertificateFactory.Fingerprint(certificate));

        TransportTimeouts budget = new(
            connectTimeout: TimeSpan.FromSeconds(3),
            handshakeTimeout: TimeSpan.FromMilliseconds(400),
            lengthPrefixTimeout: TimeSpan.FromSeconds(5),
            payloadTimeout: TimeSpan.FromSeconds(10),
            helloTimeout: TimeSpan.FromSeconds(5),
            preAuthEnvelopeTimeout: TimeSpan.FromSeconds(8));

        Stopwatch stopwatch = Stopwatch.StartNew();
        Exception? caught = null;
        TlsConnection? connection = null;
        try
        {
            connection = await connector.ConnectAsync(target, budget);
        }
        catch (Exception ex)
        {
            caught = ex;
        }
        finally
        {
            connection?.Dispose();
        }

        stopwatch.Stop();

        Assert.Null(connection);
        Assert.NotNull(caught);

        // 绝对 deadline：不早于设定值，也不该远超（说明没有别的更慢的路径在兜底）。
        Assert.True(
            stopwatch.Elapsed >= TimeSpan.FromMilliseconds(380),
            $"过早返回（{stopwatch.Elapsed}），时限没有生效。");
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"远超设定时限（{stopwatch.Elapsed}），可能有第二条更慢的超时路径。");

        // 实测到的异常类型（M3 步骤 15 要求如实记录，不预填）：
        Assert.Equal("System.OperationCanceledException", caught!.GetType().FullName);
    }

    /// <summary>
    /// M3 步骤 5 的验收：握手<b>进行中</b>把 discovery 缓存的指纹改掉，
    /// 这次连接仍必须按点击那一刻冻结的指纹校验。
    /// </summary>
    /// <remarks>
    /// 服务端闸门保证「改缓存」发生在 TCP 已连上、TLS 还没完成之间。
    /// 如果将来有人把 <see cref="TlsClientConnector.ConnectAsync"/> 改成接受缓存 / 设备对象
    /// 并在回调里重新取指纹，这个用例会失败。
    /// </remarks>
    [Fact(Timeout = 60_000)]
    public async Task Frozen_Pin_Survives_Discovery_Cache_Mutation_Mid_Handshake()
    {
        using X509Certificate2 serverCertificate = TestCertificateFactory.Create();
        using X509Certificate2 impostorCertificate = TestCertificateFactory.Create();

        string pinA = TestCertificateFactory.Fingerprint(serverCertificate);
        string pinB = TestCertificateFactory.Fingerprint(impostorCertificate);
        Assert.NotEqual(pinA, pinB);

        using TestTlsServer server = new(serverCertificate)
        {
            HandshakeGate = new ManualResetEventSlim(false),
        };

        DiscoveryDeviceCache cache = new();
        cache.Upsert(CreateDevice(server.Port, pinA), DateTimeOffset.UtcNow);

        // 用户点击连接的那一刻冻结快照。
        bool frozen = ConnectionTarget.TryCreate(
            cache.Snapshot().Single(),
            out ConnectionTarget? target);
        Assert.True(frozen);

        TlsClientConnector connector = new();
        Task<TlsConnection> connect = connector.ConnectAsync(target!);

        // 客户端已经连上、正卡在握手里。
        Assert.True(
            server.Accepted.Wait(TimeSpan.FromSeconds(10)),
            "服务端没有 accept，无法构造「握手进行中」的时刻。");

        // 握手进行中：伪造的广播把缓存里的指纹换成了 B。
        cache.Upsert(CreateDevice(server.Port, pinB), DateTimeOffset.UtcNow);
        Assert.Equal(pinB, cache.Snapshot().Single().CertificateSha256);

        server.HandshakeGate!.Set();

        using TlsConnection connection = await connect;

        Assert.True(connection.Identity.PinsMatch);
        Assert.Equal(pinA, TestCertificateFactory.ToHex(connection.Identity.PresentedCertSha256.Span));

        // 对照组：从被污染的缓存里重新冻结，必须失败。
        Assert.True(ConnectionTarget.TryCreate(cache.Snapshot().Single(), out ConnectionTarget? poisoned));
        await Assert.ThrowsAsync<AuthenticationException>(
            () => connector.ConnectAsync(poisoned!));
    }

    /// <summary>
    /// <c>LocalEndPoint</c> 必须与对端 accept 到的远端端点**完全一致**，且释放后不再抛异常。
    /// </summary>
    /// <remarks>
    /// <para>这个属性不参与任何判定，只是两机验收的配对键。但「不参与判定」不等于「可以不做对侧断言」：
    /// 如果只断言它非空，那么把它实现成 <c>返回任意本地端口</c> 测试照样绿——
    /// 而错的配对键会让两份日志配错行，比没有配对键更坏。</para>
    /// <para>所以这里断言的是**同一性**：客户端自己说的本地端点 == 服务端 accept 时看到的远端端点。</para>
    /// </remarks>
    [Fact(Timeout = 60_000)]
    public async Task Connect_Reports_Local_EndPoint_Matching_Server_Observation()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        using TestTlsServer server = new(certificate);
        TlsClientConnector connector = new();

        ConnectionTarget target = CreateTarget(
            server.Port,
            TestCertificateFactory.Fingerprint(certificate));

        TlsConnection connection = await connector.ConnectAsync(target);

        IPEndPoint? local = connection.LocalEndPoint;
        Assert.NotNull(local);

        // 回环上客户端与「服务端看到的远端」是同一台机器：地址必须是 IPv4 回环，端口必须一致。
        Assert.Equal(IPAddress.Loopback, local!.Address);
        Assert.NotEqual(0, local.Port);

        IPEndPoint observed = Assert.Single(server.AcceptedRemoteEndPoints);
        Assert.Equal(observed.Port, local.Port);
        Assert.Equal(observed.Address, local.Address);

        // 释放之后必须「拿不到」而不是「抛异常」——验收器在 using 块外才打印日志时不能炸。
        connection.Dispose();
        Assert.Null(connection.LocalEndPoint);
        Assert.Null(connection.LocalEndPoint); // 幂等，第二次调用行为一致
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

    private static DiscoveredDevice CreateDevice(int port, string pinHex)
    {
        return new DiscoveredDevice(
            DeviceId,
            "ABCD-EFGH",
            "test-device",
            IPAddress.Loopback,
            port,
            pinHex,
            DateTimeOffset.UtcNow,
            new HashSet<string> { "view" });
    }
}
