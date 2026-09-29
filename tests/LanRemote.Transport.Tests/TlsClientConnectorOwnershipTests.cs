using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace LanRemote.Transport.Tests;

public sealed class TlsClientConnectorOwnershipTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    public static IEnumerable<object[]> ConnectFailures()
    {
        foreach (string kind in new[] { "ordinary", "canceled", "nested" })
        foreach (bool asynchronous in new[] { false, true })
        foreach (bool tcpThrows in new[] { false, true })
            yield return [kind, asynchronous, tcpThrows];
    }

    public static IEnumerable<object[]> AuthenticationFailures()
    {
        foreach (string kind in new[] { "ordinary", "canceled", "nested" })
        foreach (bool asynchronous in new[] { false, true })
        foreach (bool tcpThrows in new[] { false, true })
        foreach (bool sslThrows in new[] { false, true })
            yield return [kind, asynchronous, tcpThrows, sslThrows];
    }

    public static IEnumerable<object[]> CleanupGates()
    {
        foreach (string stage in new[] { "connect", "create", "auth" })
        foreach (bool tcpThrows in new[] { false, true })
        {
            yield return [stage, tcpThrows, false];
            if (stage == "auth") yield return [stage, tcpThrows, true];
        }
    }

    [Theory(Timeout = 60_000)]
    [MemberData(nameof(ConnectFailures))]
    public async Task ConnectFailure_SynchronousOrOriginalPendingTask_CleansOnlyTcpAndPreservesFailure(
        string kind, bool asynchronous, bool tcpThrows)
    {
        using CancellationTokenSource caller = new();
        using CancellationTokenSource operation = new();
        operation.Cancel();
        Exception primary = CreatePrimary(kind, operation.Token);
        Exception tcpError = CreateCleanupError("TCP", kind);
        OwnerFixture fixture = new(tcpError: tcpThrows ? tcpError : null);
        TaskCompletionSource pending = NewSignal();
        try
        {
            Task<TlsConnection> owner = fixture.Start(
                (_, _) => asynchronous ? pending.Task : throw primary,
                (_, _) => throw new InvalidOperationException("connect 失败后不应认证。"), caller.Token);
            Assert.Equal(1, fixture.ConnectCount);
            if (asynchronous)
            {
                Assert.False(pending.Task.IsCompleted);
                Assert.False(owner.IsCompleted);
                Assert.Equal(0, fixture.CreateCount);
                fixture.AssertNotDisposed();
                pending.SetException(primary);
            }

            Exception? observed = await ObserveAsync(owner).WaitAsync(Guard);
            AssertFailure(observed, primary, tcpThrows ? [tcpError] : []);
            Assert.Equal(0, fixture.CreateCount);
            Assert.Equal(0, fixture.AuthenticateCount);
            Assert.Null(fixture.Ssl);
            fixture.AssertDisposed(hasSsl: false);
            AssertOperationToken(primary, kind, operation.Token);
        }
        finally
        {
            pending.TrySetException(primary);
            await fixture.FinishAsync(pending.Task);
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateStreamThrows_CleansOnlyTcpAndPreservesOriginalPrimary(bool tcpThrows)
    {
        InvalidOperationException primary = new("测试创建 SSL 失败。");
        IOException tcpError = new("测试 TCP 清理失败。");
        OwnerFixture fixture = new(tcpError: tcpThrows ? tcpError : null);
        try
        {
            Task<TlsConnection> owner = fixture.Start(
                (_, _) => Task.CompletedTask,
                (_, _) => throw new InvalidOperationException("创建 SSL 失败后不应认证。"),
                CancellationToken.None, _ => throw primary);

            AssertFailure(await ObserveAsync(owner).WaitAsync(Guard), primary, tcpThrows ? [tcpError] : []);
            Assert.Equal(1, fixture.ConnectCount);
            Assert.Equal(1, fixture.CreateCount);
            Assert.Equal(0, fixture.AuthenticateCount);
            Assert.Null(fixture.Ssl);
            fixture.AssertDisposed(hasSsl: false);
        }
        finally
        {
            await fixture.FinishAsync();
        }
    }

    [Theory(Timeout = 60_000)]
    [MemberData(nameof(AuthenticationFailures))]
    public async Task AuthenticationFailure_FourCleanupCombinations_PreservesOriginalOrderedExceptionTree(
        string kind, bool asynchronous, bool tcpThrows, bool sslThrows)
    {
        using CancellationTokenSource caller = new();
        using CancellationTokenSource operation = new();
        operation.Cancel();
        Exception primary = CreatePrimary(kind, operation.Token);
        Exception tcpError = CreateCleanupError("TCP", kind);
        Exception sslError = CreateCleanupError("SSL", kind);
        Exception[] cleanup = ExpectedCleanup(tcpThrows, sslThrows, tcpError, sslError);
        OwnerFixture fixture = new(tcpThrows ? tcpError : null, sslThrows ? sslError : null);
        TaskCompletionSource<ConnectionIdentity> pending = NewIdentitySignal();
        try
        {
            Task<TlsConnection> owner = fixture.Start(
                (_, _) => Task.CompletedTask,
                (_, _) => asynchronous ? pending.Task : throw primary, caller.Token);
            Assert.Equal(1, fixture.AuthenticateCount);
            if (asynchronous)
            {
                Assert.False(pending.Task.IsCompleted);
                Assert.False(owner.IsCompleted);
                fixture.AssertNotDisposed();
                pending.SetException(primary);
            }

            Exception? observed = await ObserveAsync(owner).WaitAsync(Guard);
            AssertFailure(observed, primary, cleanup);
            Assert.Equal(1, fixture.ConnectCount);
            Assert.Equal(1, fixture.CreateCount);
            Assert.Equal(1, fixture.AuthenticateCount);
            fixture.AssertDisposed(hasSsl: true);
            AssertOperationToken(primary, kind, operation.Token);
        }
        finally
        {
            pending.TrySetException(primary);
            await fixture.FinishAsync(pending.Task);
        }
    }

    [Theory(Timeout = 60_000)]
    [MemberData(nameof(CleanupGates))]
    public async Task SynchronousPrimary_CleanupRunsOffCaller_WaitsForTcpThenIndependentSsl(
        string stage, bool tcpThrows, bool sslThrows)
    {
        using DisposeGate tcpGate = new();
        using DisposeGate sslGate = new();
        IOException primary = new("测试同步阶段失败。");
        IOException tcpError = new("测试 TCP 闸门后的失败。");
        AuthenticationException sslError = new("测试 SSL 闸门后的失败。");
        bool hasSsl = stage == "auth";
        OwnerFixture fixture = new(tcpThrows ? tcpError : null, sslThrows ? sslError : null,
            tcpGate.Block, sslGate.Block);
        OwnerCaller caller = new(() => fixture.Start(
            (_, _) => stage == "connect" ? throw primary : Task.CompletedTask,
            (_, _) => throw primary,
            CancellationToken.None, stage == "create" ? _ => throw primary : null));
        try
        {
            caller.Start();
            // 先观察实际进入的释放闸门；旧 SSL-first 行为直接断言变红，而非靠超时充当证据。
            Task first = await Task.WhenAny(tcpGate.Entered.Task, sslGate.Entered.Task).WaitAsync(Guard);
            Assert.Same(tcpGate.Entered.Task, first);
            Assert.Equal(1, fixture.Client.DisposeCount);
            Assert.Equal(0, fixture.Ssl?.DisposeCount ?? 0);
            Assert.True(fixture.Client.DisposedOnThreadPool, "TCP 清理不能在专用调用线程内执行。");
            Assert.NotEqual(caller.ThreadId, fixture.Client.DisposeThreadId);
            Task<TlsConnection> owner = await caller.Returned.Task.WaitAsync(Guard);
            Assert.False(owner.IsCompleted);

            tcpGate.Release();
            if (hasSsl)
            {
                await sslGate.Entered.Task.WaitAsync(Guard);
                Assert.Equal(1, fixture.Ssl!.DisposeCount);
                Assert.True(fixture.Ssl.DisposedOnThreadPool);
                Assert.NotEqual(caller.ThreadId, fixture.Ssl.DisposeThreadId);
                Assert.False(owner.IsCompleted);
                Assert.Equal(new[] { "tcp", "ssl" }, fixture.Order.ToArray());
            }
            sslGate.Release();
            AssertFailure(await ObserveAsync(owner).WaitAsync(Guard), primary,
                ExpectedCleanup(tcpThrows, sslThrows, tcpError, sslError));
            fixture.AssertDisposed(hasSsl);
        }
        finally
        {
            tcpGate.Release();
            sslGate.Release();
            try { await caller.JoinAsync(); }
            finally { await fixture.FinishAsync(); }
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreCanceledCaller_InvokesNoOperation_ButStillOwnsAndCleansClient(bool tcpThrows)
    {
        using CancellationTokenSource caller = new();
        caller.Cancel();
        IOException tcpError = new("测试预取消 TCP 清理失败。");
        OwnerFixture fixture = new(tcpError: tcpThrows ? tcpError : null);
        try
        {
            Task<TlsConnection> owner = fixture.Start(
                (_, _) => throw new InvalidOperationException("预取消不得调用 connect。"),
                (_, _) => throw new InvalidOperationException("预取消不得调用 authenticate。"), caller.Token);
            Exception? observed = await ObserveAsync(owner).WaitAsync(Guard);

            Assert.Equal(0, fixture.ConnectCount);
            Assert.Equal(0, fixture.CreateCount);
            Assert.Equal(0, fixture.AuthenticateCount);
            AssertCancellationFailure(observed, caller.Token, tcpThrows ? [tcpError] : []);
            fixture.AssertDisposed(hasSsl: false);
        }
        finally
        {
            await fixture.FinishAsync();
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectIgnoresCancellation_LateSuccessIsJoined_ButDoesNotCreateSsl(bool tcpThrows)
    {
        using CancellationTokenSource caller = new();
        IOException tcpError = new("测试 connect 迟到后的 TCP 清理失败。");
        TaskCompletionSource connect = NewSignal();
        OwnerFixture fixture = new(tcpError: tcpThrows ? tcpError : null);
        try
        {
            Task<TlsConnection> owner = fixture.Start(
                (_, _) => connect.Task,
                (_, _) => throw new InvalidOperationException("迟到 connect 成功后不得认证。"), caller.Token);
            Assert.Equal(1, fixture.ConnectCount);
            Assert.False(connect.Task.IsCompleted);
            caller.Cancel();
            Assert.False(connect.Task.IsCompleted);
            Assert.False(owner.IsCompleted);
            Assert.Equal(0, fixture.CreateCount);
            fixture.AssertNotDisposed();

            connect.SetResult();
            Exception? observed = await ObserveAsync(owner).WaitAsync(Guard);
            Assert.True(connect.Task.IsCompletedSuccessfully);
            Assert.Equal(0, fixture.CreateCount);
            Assert.Equal(0, fixture.AuthenticateCount);
            Assert.Null(fixture.Ssl);
            AssertCancellationFailure(observed, caller.Token, tcpThrows ? [tcpError] : []);
            fixture.AssertDisposed(hasSsl: false);
        }
        finally
        {
            connect.TrySetResult();
            await fixture.FinishAsync(connect.Task);
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AuthenticationIgnoresCancellation_LateSuccessIsJoined_ButNeverTransferred(
        bool tcpThrows, bool sslThrows)
    {
        using CancellationTokenSource caller = new();
        ConnectionIdentity identity = CreateIdentity();
        IOException tcpError = new("测试认证迟到后的 TCP 清理失败。");
        IOException sslError = new("测试认证迟到后的 SSL 清理失败。");
        TaskCompletionSource<ConnectionIdentity> authenticate = NewIdentitySignal();
        OwnerFixture fixture = new(tcpThrows ? tcpError : null, sslThrows ? sslError : null);
        try
        {
            Task<TlsConnection> owner = fixture.Start(
                (_, _) => Task.CompletedTask, (_, _) => authenticate.Task, caller.Token);
            Assert.Equal(1, fixture.AuthenticateCount);
            caller.Cancel();
            Assert.False(authenticate.Task.IsCompleted);
            Assert.False(owner.IsCompleted);
            fixture.AssertNotDisposed();

            authenticate.SetResult(identity);
            Exception? observed = await ObserveAsync(owner).WaitAsync(Guard);
            Assert.True(authenticate.Task.IsCompletedSuccessfully);
            Assert.False(owner.IsCompletedSuccessfully, "caller 已取消，不得移交 TlsConnection。");
            AssertCancellationFailure(observed, caller.Token,
                ExpectedCleanup(tcpThrows, sslThrows, tcpError, sslError));
            Assert.Equal(1, fixture.ConnectCount);
            Assert.Equal(1, fixture.CreateCount);
            Assert.Equal(1, fixture.AuthenticateCount);
            fixture.AssertDisposed(hasSsl: true);
        }
        finally
        {
            authenticate.TrySetResult(identity);
            await fixture.FinishAsync(authenticate.Task);
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PendingOperation_CallerCancelsThenOriginalFaults_DoesNotReplaceOriginalWithCancellation(
        bool authenticationStage, bool cleanupThrows)
    {
        using CancellationTokenSource caller = new();
        IOException primary = new("测试忽略取消后才报告的原始操作失败。");
        IOException tcpError = new("测试迟到失败的 TCP 清理错误。");
        IOException sslError = new("测试迟到失败的 SSL 清理错误。");
        TaskCompletionSource connect = NewSignal();
        TaskCompletionSource<ConnectionIdentity> authenticate = NewIdentitySignal();
        OwnerFixture fixture = new(cleanupThrows ? tcpError : null, cleanupThrows ? sslError : null);
        try
        {
            Task<TlsConnection> owner = fixture.Start(
                (_, _) => authenticationStage ? Task.CompletedTask : connect.Task,
                (_, _) => authenticate.Task, caller.Token);
            Task original = authenticationStage ? authenticate.Task : connect.Task;
            caller.Cancel();
            Assert.False(original.IsCompleted);
            Assert.False(owner.IsCompleted);
            fixture.AssertNotDisposed();
            if (authenticationStage) authenticate.SetException(primary);
            else connect.SetException(primary);

            Exception[] cleanup = cleanupThrows
                ? authenticationStage ? [tcpError, sslError] : [tcpError]
                : [];
            AssertFailure(await ObserveAsync(owner).WaitAsync(Guard), primary, cleanup);
            Assert.True(original.IsFaulted);
            fixture.AssertDisposed(authenticationStage);
            Assert.Equal(authenticationStage ? 1 : 0, fixture.CreateCount);
            Assert.Equal(authenticationStage ? 1 : 0, fixture.AuthenticateCount);
        }
        finally
        {
            connect.TrySetException(primary);
            authenticate.TrySetException(primary);
            await fixture.FinishAsync(connect.Task, authenticate.Task);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task UncanceledSuccess_AwaitsBothOriginalTasks_TransfersOriginalResources_ThenReclaimsThem()
    {
        using CancellationTokenSource caller = new();
        ConnectionIdentity identity = CreateIdentity();
        TaskCompletionSource connect = NewSignal();
        TaskCompletionSource<ConnectionIdentity> authenticate = NewIdentitySignal();
        OwnerFixture fixture = new();
        try
        {
            Task<TlsConnection> owner = fixture.Start(
                (_, _) => connect.Task, (_, _) => authenticate.Task, caller.Token);
            Assert.Equal(1, fixture.ConnectCount);
            Assert.Equal(0, fixture.CreateCount);
            Assert.Equal(0, fixture.AuthenticateCount);
            Assert.False(owner.IsCompleted);
            fixture.AssertNotDisposed();

            connect.SetResult();
            await fixture.AuthenticationEntered.Task.WaitAsync(Guard);
            Assert.True(connect.Task.IsCompletedSuccessfully);
            Assert.Equal(1, fixture.CreateCount);
            Assert.Equal(1, fixture.AuthenticateCount);
            Assert.False(authenticate.Task.IsCompleted);
            Assert.False(owner.IsCompleted);
            fixture.AssertNotDisposed();

            authenticate.SetResult(identity);
            TlsConnection connection = await owner.WaitAsync(Guard);
            Assert.True(authenticate.Task.IsCompletedSuccessfully);
            Assert.Same(identity, connection.Identity);
            Assert.Same(fixture.Ssl, connection.Stream);
            Assert.False(connection.IsCloseRequested);
            Assert.False(caller.IsCancellationRequested);
            fixture.AssertNotDisposed();
            Assert.Empty(fixture.Order);

            await connection.CloseAsync().WaitAsync(Guard);
            fixture.AssertDisposed(hasSsl: true);
            Assert.Empty(connection.CleanupErrors);
            connection.Dispose();
            fixture.AssertDisposed(hasSsl: true);
        }
        finally
        {
            connect.TrySetResult();
            authenticate.TrySetResult(identity);
            await fixture.FinishAsync(connect.Task, authenticate.Task);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task ConcurrentOwners_OneBlockedCleanupDoesNotBlockOrContaminateOtherDiagnostics()
    {
        using DisposeGate tcpGate = new();
        using DisposeGate sslGate = new();
        Exception[] primary = Enumerable.Range(0, 4)
            .Select(i => (Exception)new AuthenticationException($"测试第 {i} 个 owner 的主失败。"))
            .ToArray();
        Exception[] tcpErrors = Enumerable.Range(0, 4)
            .Select(i => (Exception)new IOException($"测试第 {i} 个 owner 的 TCP 错误。")).ToArray();
        Exception[] sslErrors = Enumerable.Range(0, 4)
            .Select(i => (Exception)new IOException($"测试第 {i} 个 owner 的 SSL 错误。")).ToArray();
        // 第一条双错误且被闸门持有，其余三条分别无错、仅 TCP 错、仅 SSL 错。
        bool[] tcpThrows = [true, false, true, false];
        bool[] sslThrows = [true, false, false, true];
        OwnerFixture[] fixtures = Enumerable.Range(0, 4).Select(i => new OwnerFixture(
            tcpThrows[i] ? tcpErrors[i] : null, sslThrows[i] ? sslErrors[i] : null,
            i == 0 ? tcpGate.Block : null, i == 0 ? sslGate.Block : null)).ToArray();
        TaskCompletionSource<ConnectionIdentity>[] pending = Enumerable.Range(0, 4)
            .Select(_ => NewIdentitySignal()).ToArray();
        try
        {
            Task<TlsConnection>[] owners = fixtures.Select((fixture, i) => fixture.Start(
                (_, _) => Task.CompletedTask, (_, _) => pending[i].Task, CancellationToken.None)).ToArray();
            Assert.All(fixtures, fixture => Assert.Equal(1, fixture.AuthenticateCount));
            Assert.All(owners, owner => Assert.False(owner.IsCompleted));
            pending[0].SetException(primary[0]);
            await Task.WhenAny(tcpGate.Entered.Task, sslGate.Entered.Task).WaitAsync(Guard);
            Assert.False(owners[0].IsCompleted);

            for (int i = 1; i < 4; i++) pending[i].SetException(primary[i]);
            Exception?[] observed = await Task.WhenAll(owners.Skip(1).Select(ObserveAsync)).WaitAsync(Guard);
            Assert.False(owners[0].IsCompleted);
            for (int i = 1; i < 4; i++)
            {
                AssertFailure(observed[i - 1], primary[i],
                    ExpectedCleanup(tcpThrows[i], sslThrows[i], tcpErrors[i], sslErrors[i]));
                fixtures[i].AssertDisposed(hasSsl: true);
            }

            tcpGate.Release();
            sslGate.Release();
            AssertFailure(await ObserveAsync(owners[0]).WaitAsync(Guard), primary[0], [tcpErrors[0], sslErrors[0]]);
            fixtures[0].AssertDisposed(hasSsl: true);
            // 再读已完成的原任务，不能被另一条调用追加诊断或换掉异常树。
            for (int i = 1; i < 4; i++)
                Assert.Same(observed[i - 1], await ObserveAsync(owners[i]).WaitAsync(Guard));
        }
        finally
        {
            tcpGate.Release();
            sslGate.Release();
            for (int i = 0; i < 4; i++) pending[i].TrySetException(primary[i]);
            await Task.WhenAll(fixtures.Select((fixture, i) => fixture.FinishAsync(pending[i].Task)))
                .WaitAsync(Guard + Guard);
        }
    }

    private static Exception CreatePrimary(string kind, CancellationToken token) => kind switch
    {
        "ordinary" => new AuthenticationException("测试原始主失败。"),
        "canceled" => new OperationCanceledException("测试操作自己的取消。", new IOException("原始取消内层。"), token),
        "nested" => new AggregateException("测试原始嵌套主失败。",
            new IOException("主失败第一项。"), new AggregateException(new AuthenticationException("主失败嵌套项。"))),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static Exception CreateCleanupError(string resource, string kind) => kind == "nested"
        ? new AggregateException($"测试 {resource} 原始清理聚合。", new IOException($"{resource} 第一项。"),
            new AggregateException(new InvalidOperationException($"{resource} 嵌套项。")))
        : new IOException($"测试 {resource} 原始清理失败。");

    private static Exception[] ExpectedCleanup(bool tcpThrows, bool sslThrows, Exception tcp, Exception ssl) =>
        (tcpThrows, sslThrows) switch
        {
            (true, true) => [tcp, ssl],
            (true, false) => [tcp],
            (false, true) => [ssl],
            _ => [],
        };

    private static void AssertFailure(Exception? observed, Exception primary, Exception[] cleanup)
    {
        if (cleanup.Length == 0)
        {
            Assert.Same(primary, observed);
            return;
        }

        AggregateException outer = Assert.IsType<AggregateException>(observed);
        Assert.Equal(2, outer.InnerExceptions.Count);
        Assert.Same(primary, outer.InnerExceptions[0]);
        AssertCleanup(outer.InnerExceptions[1], cleanup);
    }

    private static void AssertCleanup(Exception observed, Exception[] expected)
    {
        AggregateException cleanup = Assert.IsType<AggregateException>(observed);
        Assert.InRange(expected.Length, 1, 2);
        Assert.Equal(expected.Length, cleanup.InnerExceptions.Count);
        // 不 Flatten，也不按消息匹配；嵌套聚合仍必须是那个原始对象。
        for (int i = 0; i < expected.Length; i++) Assert.Same(expected[i], cleanup.InnerExceptions[i]);
    }

    private static void AssertCancellationFailure(Exception? observed, CancellationToken token, Exception[] cleanup)
    {
        Exception? primary = observed;
        if (cleanup.Length != 0)
        {
            AggregateException outer = Assert.IsType<AggregateException>(observed);
            Assert.Equal(2, outer.InnerExceptions.Count);
            primary = outer.InnerExceptions[0];
            AssertCleanup(outer.InnerExceptions[1], cleanup);
        }
        OperationCanceledException canceled = Assert.IsAssignableFrom<OperationCanceledException>(primary);
        Assert.Equal(token, canceled.CancellationToken);
    }

    private static void AssertOperationToken(Exception primary, string kind, CancellationToken expected)
    {
        if (kind == "canceled")
            Assert.Equal(expected, Assert.IsType<OperationCanceledException>(primary).CancellationToken);
    }

    private static async Task<Exception?> ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); return null; }
        catch (Exception error) { return error; }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<ConnectionIdentity> NewIdentitySignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // 仅供受控 auth 成功返回的独立夹具身份；不是证书、pinning 或实网安全性的证据。
    // 失败路径不创建身份，更不通过构造 TlsConnection 来代替 owner 的失败清理。
    private static ConnectionIdentity CreateIdentity()
    {
        byte[] pin = Convert.FromHexString("808182838485868788898A8B8C8D8E8F909192939495969798999A9B9C9D9E9F");
        Assert.True(ConnectionTarget.TryCreate(
            new Guid("11111111-2222-3333-4444-555555555555"), IPAddress.Loopback, 12345,
            Convert.ToHexString(pin), out ConnectionTarget? target));
        Assert.True(ConnectionIdentity.TryCreate(target!, pin, out ConnectionIdentity? identity));
        return identity!;
    }

    private sealed class OwnerFixture
    {
        private readonly Exception? _sslError;
        private readonly Action? _onSslDispose;
        private int _connectCount;
        private int _createCount;
        private int _authenticateCount;
        internal ProbeTcpClient Client { get; }
        internal ProbeSslStream? Ssl { get; private set; }
        internal Task<TlsConnection>? Owner { get; private set; }
        internal ConcurrentQueue<string> Order { get; } = new();
        internal TaskCompletionSource AuthenticationEntered { get; } = NewSignal();
        internal int ConnectCount => Volatile.Read(ref _connectCount);
        internal int CreateCount => Volatile.Read(ref _createCount);
        internal int AuthenticateCount => Volatile.Read(ref _authenticateCount);

        internal OwnerFixture(Exception? tcpError = null, Exception? sslError = null,
            Action? onTcpDispose = null, Action? onSslDispose = null)
        {
            _sslError = sslError;
            _onSslDispose = onSslDispose;
            Client = new ProbeTcpClient(() =>
            {
                Order.Enqueue("tcp");
                onTcpDispose?.Invoke();
                if (tcpError is not null) throw tcpError;
            });
        }

        internal Task<TlsConnection> Start(Func<TcpClient, CancellationToken, Task> connect,
            Func<SslStream, CancellationToken, Task<ConnectionIdentity>> authenticate,
            CancellationToken caller, Func<TcpClient, SslStream>? create = null)
        {
            return Owner = TlsClientConnector.ConnectOwnedAsync(Client,
                (client, token) =>
                {
                    Interlocked.Increment(ref _connectCount);
                    Assert.Same(Client, client);
                    Assert.Equal(caller, token);
                    return connect(client, token);
                },
                client =>
                {
                    Interlocked.Increment(ref _createCount);
                    Assert.Same(Client, client);
                    if (create is not null) return create(client);
                    return Ssl = new ProbeSslStream(() =>
                    {
                        Order.Enqueue("ssl");
                        _onSslDispose?.Invoke();
                        if (_sslError is not null) throw _sslError;
                    });
                },
                (ssl, token) =>
                {
                    Interlocked.Increment(ref _authenticateCount);
                    Assert.Same(Ssl, ssl);
                    Assert.Equal(caller, token);
                    AuthenticationEntered.TrySetResult();
                    return authenticate(ssl, token);
                }, caller);
        }

        internal void AssertNotDisposed()
        {
            Assert.Equal(0, Client.DisposeCount);
            Assert.Equal(0, Ssl?.DisposeCount ?? 0);
            Assert.Empty(Order);
        }

        internal void AssertDisposed(bool hasSsl)
        {
            Assert.Equal(1, Client.DisposeCount);
            Assert.Equal(hasSsl ? 1 : 0, Ssl?.DisposeCount ?? 0);
            Assert.Equal(hasSsl ? new[] { "tcp", "ssl" } : new[] { "tcp" }, Order.ToArray());
        }

        internal async Task FinishAsync(params Task[] originals)
        {
            try
            {
                Task[] tasks = Owner is null ? originals : [.. originals, Owner];
                // Guard 在观测包装之外：只能吞已完成任务的预期故障，不能吞 join 超时。
                await Task.WhenAll(tasks.Select(ObserveAsync)).WaitAsync(Guard + Guard);
                if (Owner?.IsCompletedSuccessfully == true)
                {
                    // 旧 helper 若错误地移交了连接，也只回收它实际返回的对象，不另造连接清理失败。
                    TlsConnection connection = await Owner;
                    await connection.CloseAsync().WaitAsync(Guard);
                }
            }
            finally
            {
                // 所有生产释放次数与错误断言均在此前完成；兜底绕过注入，不替生产补证据。
                try { Client.DisposeForCleanup(); }
                finally { Ssl?.DisposeForCleanup(); }
            }
        }
    }

    private sealed class DisposeGate : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        internal TaskCompletionSource Entered { get; } = NewSignal();
        internal void Block()
        {
            Entered.TrySetResult();
            if (!_release.Wait(Guard + Guard)) throw new TimeoutException("测试清理闸门未放行。");
        }
        internal void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
    }

    private sealed class OwnerCaller
    {
        private readonly Thread _thread;
        private bool _started;
        internal TaskCompletionSource<Task<TlsConnection>> Returned { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ThreadId => _thread.ManagedThreadId;

        internal OwnerCaller(Func<Task<TlsConnection>> invoke)
        {
            _thread = new Thread(() =>
            {
                try { Returned.TrySetResult(invoke()); }
                catch (Exception error) { Returned.TrySetException(error); }
            }) { IsBackground = true };
        }

        internal void Start()
        {
            _thread.Start();
            _started = true;
        }

        internal async Task JoinAsync()
        {
            if (!_started) return;
            try
            {
                Task<TlsConnection> original = await Returned.Task.WaitAsync(Guard);
                await ObserveAsync(original).WaitAsync(Guard);
            }
            finally
            {
                Assert.True(_thread.Join(Guard), "测试 owner 调用线程未结束。");
            }
        }
    }

    private sealed class ProbeTcpClient(Action onDispose) : TcpClient(AddressFamily.InterNetwork)
    {
        private int _disposeCount;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        internal int DisposeThreadId { get; private set; }
        internal bool DisposedOnThreadPool { get; private set; }
        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    DisposeThreadId = Environment.CurrentManagedThreadId;
                    DisposedOnThreadPool = Thread.CurrentThread.IsThreadPoolThread;
                    Interlocked.Increment(ref _disposeCount);
                    onDispose();
                }
            }
            finally { base.Dispose(disposing); }
        }
        internal void DisposeForCleanup() => base.Dispose(true);
    }

    private sealed class ProbeSslStream(Action onDispose) : SslStream(new MemoryStream(), leaveInnerStreamOpen: false)
    {
        private int _disposeCount;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        internal int DisposeThreadId { get; private set; }
        internal bool DisposedOnThreadPool { get; private set; }
        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    DisposeThreadId = Environment.CurrentManagedThreadId;
                    DisposedOnThreadPool = Thread.CurrentThread.IsThreadPoolThread;
                    Interlocked.Increment(ref _disposeCount);
                    onDispose();
                }
            }
            finally { base.Dispose(disposing); }
        }
        internal void DisposeForCleanup() => base.Dispose(true);
    }
}
