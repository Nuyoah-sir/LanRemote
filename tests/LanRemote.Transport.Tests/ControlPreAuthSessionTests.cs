using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using LanRemote.Core.Abstractions;

namespace LanRemote.Transport.Tests;

/// <summary>
/// 步骤 19～21 的验收：首帧只能是 hello / 显式的 <see cref="ControlSessionState.PreAuthenticated"/> /
/// M3 的终态是干净关闭。
/// </summary>
/// <remarks>
/// 走的是<b>真实回环 TLS</b>（不是替身）：pre-auth 这一层的失败模式几乎全在
/// 「对端什么时候说什么」上，用替身很容易测出一个只在替身里成立的行为。
/// </remarks>
public sealed class ControlPreAuthSessionTests
{
    private static readonly Guid DeviceId = Guid.Parse("99999999-8888-7777-6666-555555555555");

    private static readonly TimeSpan PrefixDeadline = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan PayloadDeadline = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// 步骤 20 的可执行形式：M3 里 PreAuthenticated 状态<b>不允许任何操作</b>。
    /// </summary>
    /// <remarks>
    /// 这条测试一旦变红，说明有人在 M4 落地前给未认证连接开了口子——**不要改这条测试**，
    /// 要么撤回那处改动，要么它确实属于 M4 且已经连带落地了认证。
    /// </remarks>
    [Fact]
    public void PreAuthenticated_Allows_Nothing_Before_M4()
    {
        Assert.Empty(ControlPreAuthSession.AllowedOperationsWhilePreAuthenticated);
    }

    /// <summary>
    /// 走完全程：客户端发一个合法 hello，服务端进入 PreAuthenticated 并干净关闭。
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Valid_Hello_Reaches_PreAuthenticated_And_Closes()
    {
        (ControlPreAuthResult result, ControlPreAuthSession session) = await RunAgainstHostAsync(
            async (connection, _) =>
                await FrameWriter.WriteHelloAsync(connection.Stream, PrefixDeadline));

        Assert.True(result.Completed, result.Rejection);
        Assert.Null(result.Rejection);
        Assert.Equal(ControlSessionState.PreAuthenticated, result.State);

        // 状态不是"猜出来的"：会话对象自己也停在这个状态上。
        Assert.Equal(ControlSessionState.PreAuthenticated, session.State);
    }

    /// <summary>
    /// 步骤 17：pre-auth 阶段声称 64 KiB —— 必须断开，且不分配、不读取该 payload。
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Pre_Auth_Frame_Larger_Than_4KiB_Is_Rejected()
    {
        (ControlPreAuthResult result, ControlPreAuthSession session) = await RunAgainstHostAsync(
            async (connection, _) =>
            {
                byte[] prefix = new byte[TransportConstants.LengthPrefixBytes];
                BinaryPrimitives.WriteUInt32BigEndian(prefix, 64 * 1024);
                await connection.Stream.WriteAsync(prefix);
                await connection.Stream.FlushAsync();
            });

        Assert.False(result.Completed);
        Assert.Equal(
            $"{ControlPreAuthSession.RejectFrameViolation}:{FrameReader.RejectTooLarge}",
            result.Rejection);
        Assert.Equal(ControlSessionState.Closed, session.State);
    }

    /// <summary>
    /// 步骤 19：结构合法但不是 hello —— 断开，并把解析器的短原因带出来。
    /// </summary>
    [Theory(Timeout = 60_000)]
    [InlineData(HelloFrame.RejectWrongChannel, """{"type":"channel_hello","channel":"video","protocol":1}""")]
    [InlineData(HelloFrame.RejectWrongProtocol, """{"type":"channel_hello","channel":"control","protocol":2}""")]
    [InlineData(HelloFrame.RejectWrongType, """{"type":"auth_hello","channel":"control","protocol":1}""")]
    [InlineData(HelloFrame.RejectMissingField, """{"channel":"control","protocol":1}""")]
    [InlineData(HelloFrame.RejectMalformedJson, "not json at all")]
    public async Task First_Frame_Must_Be_Exactly_The_Hello(string expected, string json)
    {
        (ControlPreAuthResult result, ControlPreAuthSession session) = await RunAgainstHostAsync(
            async (connection, _) =>
                await FrameWriter.WriteFrameAsync(
                    connection.Stream,
                    Encoding.UTF8.GetBytes(json),
                    TransportConstants.MaxPreAuthMessageBytes,
                    PrefixDeadline));

        Assert.False(result.Completed, $"本该被拒却通过了：{json}");
        Assert.Equal(expected, result.Rejection);
        Assert.Equal(ControlSessionState.Closed, session.State);
    }

    /// <summary>
    /// 对端握完手立刻断开：这是 EOF 或连接重置，不是「拒绝原因」，
    /// 但同样必须收尾成 <see cref="ControlSessionState.Closed"/>。
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Immediate_Disconnect_Ends_In_Closed()
    {
        (ControlPreAuthResult result, ControlPreAuthSession session) = await RunAgainstHostAsync(
            (connection, _) =>
            {
                connection.Dispose();
                return Task.CompletedTask;
            });

        Assert.False(result.Completed);
        Assert.Equal(ControlSessionState.Closed, session.State);
    }

    /// <summary>
    /// 握完手就沉默：被自己的长度前缀时限切断，而且必须留下原因。
    /// </summary>
    /// <remarks>
    /// <b>这条是变异验证逼出来的</b>：原先阶段超时会一路飞出 <c>RunAsync</c>，
    /// 结局永远产生不出来（表现是「30 秒都没结果」，而不是一条断言失败）。
    /// 现在它与「停机取消」用 <c>when (!ct.IsCancellationRequested)</c> 区分开。
    /// </remarks>
    [Fact(Timeout = 60_000)]
    public async Task Idle_Client_Is_Rejected_With_A_Timeout_Reason()
    {
        (ControlPreAuthResult result, ControlPreAuthSession session) = await RunAgainstHostAsync(
            async (_, stall) => await Task.Delay(Timeout.Infinite, stall));

        Assert.False(result.Completed);
        Assert.Equal(ControlPreAuthSession.RejectTimeout, result.Rejection);
        Assert.Equal(ControlSessionState.Closed, session.State);
    }

    /// <summary>
    /// 「第二个 hello」在协议上就该断开 —— 这里用「同一会话不允许 run 第二次」在类型层面堵掉。
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task A_Session_Cannot_Be_Run_Twice()
    {
        ControlPreAuthSession session = new();
        AcceptedConnection connection = CreateDetachedConnection();

        // 第一次会抛（detached 的 SslStream 从未认证过，连读都不允许）——本用例不关心它抛什么，
        // 关心的是：不管第一次怎么收场，这个会话<b>都不允许再跑第二次</b>。
        await Assert.ThrowsAnyAsync<Exception>(
            () => session.RunAsync(connection, BuildTimeouts(), CancellationToken.None));

        // 用消息把「守卫抛的」和「流又抛的」区分开，否则这条用例是空断言。
        InvalidOperationException guard = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.RunAsync(connection, BuildTimeouts(), CancellationToken.None));

        Assert.Contains("只允许跑一次", guard.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 起一个真实 Host，把 <see cref="ControlPreAuthSession"/> 当会话处理器，
    /// 客户端按脚本说话，把服务端结局取回来。
    /// </summary>
    private static async Task<(ControlPreAuthResult Result, ControlPreAuthSession Session)>
        RunAgainstHostAsync(Func<TlsConnection, CancellationToken, Task> client)
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        int port = GetFreePort();

        ControlPreAuthSession session = new();
        TaskCompletionSource<ControlPreAuthResult> outcome =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        TransportTimeouts timeouts = BuildTimeouts();

        // 客户端脚本必须在服务端量完之后才收摊，否则服务端看到的是 EOF 而不是它自己的行为。
        using CancellationTokenSource stall = new();

        TransportHost host = new(
            new[] { IPAddress.Loopback },
            new StubSubnetPolicy(allow: true),
            certificate,
            async (connection, cancellationToken) =>
            {
                ControlPreAuthResult result =
                    await session.RunAsync(connection, timeouts, cancellationToken);
                outcome.TrySetResult(result);
            },
            new TransportHostOptions { Port = port, Timeouts = timeouts });

        await using (host)
        {
            TransportHostStartResult start = host.Start();
            Assert.True(start.IsListening);

            bool created = ConnectionTarget.TryCreate(
                DeviceId,
                IPAddress.Loopback,
                port,
                TestCertificateFactory.Fingerprint(certificate),
                out ConnectionTarget? target);
            Assert.True(created);

            TlsConnection connection = await new TlsClientConnector().ConnectAsync(target!);

            Task clientTask = Task.Run(() => client(connection, stall.Token));

            Task<ControlPreAuthResult> outcomeTask = outcome.Task;
            Task finished = await Task.WhenAny(outcomeTask, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.Same(outcomeTask, finished);

            ControlPreAuthResult result = await outcomeTask;

            stall.Cancel();
            connection.Dispose();

            try
            {
                await clientTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // 攻击者脚本被唤醒后可能抛——不影响服务端结局。
            }

            Assert.True(await WaitUntilAsync(() => host.ActiveConnections == 0));
            Assert.True(await WaitUntilAsync(() => host.AdmittedConnections == 0));

            return (result, session);
        }
    }

    private static AcceptedConnection CreateDetachedConnection()
    {
        // 一个不与任何 socket 相连的流：读它会立刻 EOF。
        return new AcceptedConnection(
            IPAddress.Loopback,
            IPAddress.Loopback,
            0,
            new System.Net.Security.SslStream(new MemoryStream(Array.Empty<byte>())),
            System.Security.Authentication.SslProtocols.None);
    }

    private static TransportTimeouts BuildTimeouts() =>
        new(
            connectTimeout: TimeSpan.FromSeconds(3),
            handshakeTimeout: TimeSpan.FromSeconds(3),
            lengthPrefixTimeout: PrefixDeadline,
            payloadTimeout: PayloadDeadline,
            helloTimeout: TimeSpan.FromSeconds(2));

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
