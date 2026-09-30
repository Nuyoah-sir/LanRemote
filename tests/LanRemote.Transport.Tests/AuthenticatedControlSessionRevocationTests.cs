using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using LanRemote.Core.Models;

namespace LanRemote.Transport.Tests;

public sealed class AuthenticatedControlSessionRevocationTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly Guid SessionId = new("00112233-4455-6677-8899-aabbccddeeff");
    private const string TokenHex = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
    private const string NonceHex = "202122232425262728292A2B2C2D2E2F";
    private const string PinHex = "808182838485868788898A8B8C8D8E8F909192939495969798999A9B9C9D9E9F";
    // 与已有独立 transcript/HMAC 黄金向量相同，不调用生产 proof helper 生成预期值。
    private const string ProofHex = "602656EADC023F939F0F922B6311A362C3AE9048A880FCF4381E552F178B7A3A";

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revoke_ClearsOriginalToken_RejectsUse_AndLeavesAllClosingToOriginalOwner(bool cancelledProofCaller)
    {
        await using SessionFixture fixture = new();
        using CancellationTokenSource caller = new();
        if (cancelledProofCaller) caller.Cancel();
        TlsConnection owner = fixture.Connection;
        Assert.Same(fixture.Ssl, fixture.Session.Stream);
        Assert.Equal(ProofHex, Convert.ToHexString(fixture.Session.CreateVideoAttachProof(
            Convert.FromHexString(NonceHex), Convert.FromHexString(PinHex))));
        fixture.AssertNotClosing();

        fixture.Session.RevokeForOwnerCleanup();

        fixture.AssertRevoked(caller.Token);
        fixture.AssertNotClosing();
        for (int i = 0; i < 4; i++)
        {
            fixture.Session.RevokeForOwnerCleanup();
            fixture.Session.Dispose();
            fixture.AssertRevoked(caller.Token);
            fixture.AssertNotClosing();
        }

        // 原 owner 在构造会话前已持有连接；撤销入口不返回连接，也不能替 owner 请求关闭。
        Task close = owner.CloseAsync();
        await close.WaitAsync(Guard);
        Assert.True(close.IsCompletedSuccessfully);
        fixture.AssertClosedOnce();
        Assert.Empty(owner.CleanupErrors);
        fixture.Session.RevokeForOwnerCleanup();
        fixture.Session.Dispose();
        Assert.Same(close, owner.CloseAsync());
        fixture.AssertClosedOnce();
        fixture.AssertRevoked(caller.Token);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Revoke_DoesNotRunFailingDisposers_AndOwnerCloseRetainsOriginalCleanupErrors(
        bool tcpThrows, bool sslThrows)
    {
        Exception tcpError = new InvalidOperationException("测试 TCP 原始释放错误");
        Exception sslError = new IOException("测试 SSL 原始释放错误");
        await using SessionFixture fixture = new(
            () => { if (tcpThrows) throw tcpError; },
            () => { if (sslThrows) throw sslError; });
        TlsConnection owner = fixture.Connection;

        fixture.Session.RevokeForOwnerCleanup();
        fixture.Session.RevokeForOwnerCleanup();
        fixture.Session.Dispose();
        fixture.AssertRevoked();
        fixture.AssertNotClosing();

        Task close = owner.CloseAsync();
        await close.WaitAsync(Guard);
        Assert.True(close.IsCompletedSuccessfully);
        fixture.AssertClosedOnce();
        Exception[] expected = (tcpThrows, sslThrows) switch
        {
            (true, true) => [tcpError, sslError],
            (true, false) => [tcpError],
            _ => [sslError],
        };
        AssertErrors(expected, owner.CleanupErrors);

        // 后续 session 操作仍无权关闭资源或消费 owner 的原始错误。
        fixture.Session.RevokeForOwnerCleanup();
        fixture.Session.Dispose();
        Assert.Same(close, owner.CloseAsync());
        fixture.AssertClosedOnce();
        AssertErrors(expected, owner.CleanupErrors);
    }

    [Fact(Timeout = 60_000)]
    public async Task RevokeWinsGate_QueuedPublicDisposeDoesNotClose_OriginalOwnerStillClosesOnce()
    {
        await using SessionFixture fixture = new();
        OperationThread dispose = new(fixture.Session.Dispose);
        object gate = GetGate(fixture.Session);
        Monitor.Enter(gate);
        try
        {
            dispose.Start();
            dispose.AssertWaiting();
            fixture.AssertOriginalTokenUnchanged();
            // 同线程重入锁，让撤销确定地先取得连接；public Dispose 已排在同一锁边界。
            fixture.Session.RevokeForOwnerCleanup();
            fixture.AssertRevoked();
            fixture.AssertNotClosing();
        }
        finally
        {
            Monitor.Exit(gate);
            dispose.Join();
        }

        Assert.Null(await dispose.Result.Task.WaitAsync(Guard));
        fixture.AssertRevoked();
        fixture.AssertNotClosing();
        fixture.Session.Dispose();
        fixture.Session.RevokeForOwnerCleanup();
        fixture.AssertNotClosing();
        await fixture.Connection.CloseAsync().WaitAsync(Guard);
        fixture.AssertClosedOnce();
        Assert.Empty(fixture.Connection.CleanupErrors);
    }

    [Theory(Timeout = 60_000)]
    [InlineData("tcp")]
    [InlineData("ssl")]
    public async Task PublicDisposeAlreadyClosing_RevokeFinishesBeforeBlockedCloseIsReleased(string blockedResource)
    {
        using DisposeGate closeGate = new();
        await using SessionFixture fixture = new(
            () => { if (blockedResource == "tcp") closeGate.Block(); },
            () => { if (blockedResource == "ssl") closeGate.Block(); });
        OperationThread dispose = new(fixture.Session.Dispose);
        OperationThread revoke = new(() =>
        {
            fixture.Session.RevokeForOwnerCleanup();
            fixture.Session.RevokeForOwnerCleanup();
            fixture.Session.Dispose();
        });
        try
        {
            dispose.Start();
            await closeGate.Entered.Task.WaitAsync(Guard);
            Assert.True(fixture.Connection.IsCloseRequested);
            fixture.AssertOriginalTokenCleared();
            Assert.Equal(1, fixture.Client.DisposeCount);
            Assert.Equal(blockedResource == "tcp" ? 0 : 1, fixture.Ssl.DisposeCount);
            Task close = fixture.Connection.CloseAsync();
            dispose.AssertWaiting();

            revoke.Start();
            // 正向等待撤销返回，闸门仍关闭；Guard 只是失败保护，不是性能阈值或睡眠采样。
            Assert.Null(await revoke.Result.Task.WaitAsync(Guard));
            revoke.Join();
            Assert.False(closeGate.IsReleased);
            Assert.False(close.IsCompleted);
            Assert.False(dispose.Result.Task.IsCompleted);
            fixture.AssertRevoked();
            Assert.Equal(1, fixture.Client.DisposeCount);
            Assert.Equal(blockedResource == "tcp" ? 0 : 1, fixture.Ssl.DisposeCount);

            closeGate.Release();
            Assert.Null(await dispose.Result.Task.WaitAsync(Guard));
            await close.WaitAsync(Guard);
            fixture.AssertClosedOnce();
            fixture.AssertRevoked();
            Assert.Empty(fixture.Connection.CleanupErrors);
            fixture.Session.Dispose();
            fixture.Session.RevokeForOwnerCleanup();
            Assert.Same(close, fixture.Connection.CloseAsync());
            fixture.AssertClosedOnce();
        }
        finally
        {
            closeGate.Release();
            // 即使一个 join 失败，也不能跳过另一个调用线程；夹具随后 join 原连接关闭任务。
            try { dispose.Join(); }
            finally { revoke.Join(); }
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GateBoundary_SerializesProofAndRevoke_WithoutUsingPartiallyClearedToken(bool proofFirst)
    {
        await using SessionFixture fixture = new();
        byte[] nonce = Convert.FromHexString(NonceHex);
        byte[] pin = Convert.FromHexString(PinHex);
        byte[]? proof = null;
        OperationThread queued = new(() =>
        {
            if (proofFirst) fixture.Session.RevokeForOwnerCleanup();
            else proof = fixture.Session.CreateVideoAttachProof(nonce, pin);
        });
        object gate = GetGate(fixture.Session);
        // 仅控制入口锁边界；没有暂停 MAC 中途执行，也不反射调用私有撤销实现。
        Monitor.Enter(gate);
        try
        {
            queued.Start();
            queued.AssertWaiting();
            fixture.AssertOriginalTokenUnchanged();
            fixture.AssertNotClosing();
            if (proofFirst)
            {
                proof = fixture.Session.CreateVideoAttachProof(nonce, pin);
                Assert.Equal(ProofHex, Convert.ToHexString(proof));
                fixture.AssertOriginalTokenUnchanged();
                Assert.False(queued.Result.Task.IsCompleted);
            }
            else
            {
                fixture.Session.RevokeForOwnerCleanup();
                fixture.AssertRevoked();
                Assert.Null(proof);
                Assert.False(queued.Result.Task.IsCompleted);
            }
        }
        finally
        {
            Monitor.Exit(gate);
            queued.Join();
        }

        Exception? queuedError = await queued.Result.Task.WaitAsync(Guard);
        if (proofFirst)
        {
            Assert.Null(queuedError);
            Assert.NotNull(proof);
            Assert.Equal(ProofHex, Convert.ToHexString(proof));
        }
        else
        {
            Assert.IsType<ObjectDisposedException>(queuedError);
            Assert.Null(proof);
        }
        fixture.AssertRevoked();
        fixture.AssertNotClosing();
        Assert.Equal(NonceHex, Convert.ToHexString(nonce));
        Assert.Equal(PinHex, Convert.ToHexString(pin));
        fixture.Session.Dispose();
        fixture.AssertNotClosing();
        await fixture.Connection.CloseAsync().WaitAsync(Guard);
        fixture.AssertClosedOnce();
        Assert.Empty(fixture.Connection.CleanupErrors);
    }

    [Fact]
    public void RevocationRemainsInternalVoid_AndPublicSurfaceDoesNotExposeTokenOrStream()
    {
        Type type = typeof(AuthenticatedControlSession);
        const BindingFlags declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        MethodInfo? revoke = type.GetMethod(nameof(AuthenticatedControlSession.RevokeForOwnerCleanup),
            declared | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(revoke);
        Assert.True(revoke.IsAssembly);
        Assert.False(revoke.IsStatic);
        Assert.Equal(typeof(void), revoke.ReturnType);
        Assert.Empty(revoke.GetParameters());
        MethodInfo? proof = type.GetMethod("CreateVideoAttachProof", declared | BindingFlags.NonPublic);
        Assert.NotNull(proof);
        Assert.True(proof.IsAssembly);
        PropertyInfo? stream = type.GetProperty("Stream", declared | BindingFlags.NonPublic);
        Assert.NotNull(stream);
        Assert.True(stream.GetGetMethod(nonPublic: true)!.IsAssembly);

        const BindingFlags publicDeclared = declared | BindingFlags.Public;
        string[] properties = ["GrantedPermission", "Identity", "SessionId", "ShortCode"];
        Assert.Equal(properties, type.GetProperties(publicDeclared)
            .Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        string[] methods = ["AttachVideoAsync", "Dispose", "DisposeAsync", "get_GrantedPermission", "get_Identity", "get_SessionId", "get_ShortCode"];
        Assert.Equal(methods, type.GetMethods(publicDeclared)
            .Select(method => method.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(type.GetMethods(publicDeclared), method =>
            typeof(Stream).IsAssignableFrom(method.ReturnType) || method.ReturnType == typeof(byte[])
            || method.ReturnType == typeof(Memory<byte>) || method.ReturnType == typeof(ReadOnlyMemory<byte>)
            || method.ReturnType == typeof(Span<byte>) || method.ReturnType == typeof(ReadOnlySpan<byte>));
        Assert.Empty(type.GetFields(publicDeclared));
        Assert.Empty(type.GetConstructors(publicDeclared));
        Assert.Empty(type.GetEvents(publicDeclared));
        Assert.Null(type.GetProperty("SessionToken", declared | BindingFlags.Public | BindingFlags.NonPublic));
    }

    // 只反射读取原数组和锁；不复制秘密后将副本充作清零证据，不调用私有方法。
    private static byte[] GetOwnedToken(AuthenticatedControlSession session) =>
        Assert.IsType<byte[]>(typeof(AuthenticatedControlSession)
            .GetField("_sessionToken", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session));

    private static object GetGate(AuthenticatedControlSession session) =>
        typeof(AuthenticatedControlSession)
            .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;

    private static void AssertErrors(IReadOnlyList<Exception> expected, IReadOnlyList<Exception> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++) Assert.Same(expected[i], actual[i]);
    }

    // 未连接的本地资源，只验证会话/连接的所有权合同，不声称执行真实 TCP/TLS 握手。
    private sealed class SessionFixture : IAsyncDisposable
    {
        internal ProbeTcpClient Client { get; }
        internal ProbeSslStream Ssl { get; }
        internal TlsConnection Connection { get; }
        internal AuthenticatedControlSession Session { get; }
        private byte[] CallerToken { get; } = Convert.FromHexString(TokenHex);
        private byte[] OwnedToken { get; }

        internal SessionFixture(Action? onTcpDispose = null, Action? onSslDispose = null)
        {
            byte[] pin = Convert.FromHexString(PinHex);
            Assert.True(ConnectionTarget.TryCreate(
                new Guid("11111111-2222-3333-4444-555555555555"), IPAddress.Loopback, 12345,
                Convert.ToHexString(pin), out ConnectionTarget? target));
            Assert.True(ConnectionIdentity.TryCreate(target!, pin, out ConnectionIdentity? identity));
            Client = new ProbeTcpClient(onTcpDispose);
            Ssl = new ProbeSslStream(onSslDispose);
            Connection = new TlsConnection(identity!, Client, Ssl);
            Session = new AuthenticatedControlSession(Connection, SessionPermission.Control,
                SessionId, "ABCDEF", CallerToken, 15_000, TimeProvider.System.GetTimestamp(), TimeProvider.System);
            OwnedToken = GetOwnedToken(Session);
            Assert.NotSame(CallerToken, OwnedToken);
            AssertOriginalTokenUnchanged();
        }

        internal void AssertOriginalTokenUnchanged()
        {
            Assert.Same(OwnedToken, GetOwnedToken(Session));
            Assert.Equal(TokenHex, Convert.ToHexString(OwnedToken));
            Assert.Equal(TokenHex, Convert.ToHexString(CallerToken));
        }

        internal void AssertOriginalTokenCleared()
        {
            Assert.Same(OwnedToken, GetOwnedToken(Session));
            Assert.All(OwnedToken, value => Assert.Equal((byte)0, value));
            Assert.Equal(TokenHex, Convert.ToHexString(CallerToken));
        }

        internal void AssertRevoked(CancellationToken cancellationToken = default)
        {
            AssertOriginalTokenCleared();
            Assert.Throws<ObjectDisposedException>(() => { _ = Session.Stream; });
            Assert.Throws<ObjectDisposedException>(() => Session.CreateVideoAttachProof(
                Convert.FromHexString(NonceHex), Convert.FromHexString(PinHex), cancellationToken));
        }

        internal void AssertNotClosing()
        {
            Assert.False(Connection.IsCloseRequested);
            Assert.Equal(0, Client.DisposeCount);
            Assert.Equal(0, Ssl.DisposeCount);
            Assert.Same(Ssl, Connection.Stream);
            Assert.Empty(Connection.CleanupErrors);
        }

        internal void AssertClosedOnce()
        {
            Assert.True(Connection.IsCloseRequested);
            Assert.Equal(1, Client.DisposeCount);
            Assert.Equal(1, Ssl.DisposeCount);
        }

        public async ValueTask DisposeAsync()
        {
            try { await Connection.CloseAsync().WaitAsync(Guard); }
            finally
            {
                try { Session.RevokeForOwnerCleanup(); }
                finally
                {
                    // 所有次数/清零断言均在兜底清理之前，基类直调不经过探针计数或故障注入。
                    try { Ssl.DisposeForCleanup(); }
                    finally { Client.DisposeForCleanup(); }
                }
            }
        }
    }

    private sealed class OperationThread
    {
        private readonly Thread _thread;
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _started;
        internal TaskCompletionSource<Exception?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal OperationThread(Action action)
        {
            _thread = new Thread(() =>
            {
                _entered.TrySetResult();
                Result.TrySetResult(Record.Exception(action));
            }) { IsBackground = true };
        }

        internal void Start()
        {
            _thread.Start();
            _started = true;
        }

        internal void AssertWaiting()
        {
            Assert.True(_entered.Task.Wait(Guard), "调用线程未到达入口边界。");
            bool observedWaiting = false;
            Assert.True(SpinWait.SpinUntil(() =>
            {
                observedWaiting = (_thread.ThreadState & ThreadState.WaitSleepJoin) != 0;
                return observedWaiting || Result.Task.IsCompleted;
            }, Guard), "调用线程未到达受控等待点。");
            // 保存一次观测，避免再次读取 ThreadState 将短暂唤醒误判成已越过闸门。
            Assert.True(observedWaiting, "调用在线程锁或关闭闸门放行前结束。");
            Assert.False(Result.Task.IsCompleted);
        }

        internal void Join()
        {
            if (_started) Assert.True(_thread.Join(Guard), "释放闸门后调用线程未结束。");
        }
    }

    private sealed class DisposeGate : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool IsReleased => _release.IsSet;

        internal void Block()
        {
            Entered.TrySetResult();
            if (!_release.Wait(Guard + Guard + Guard)) throw new TimeoutException("测试关闭闸门未放行。");
        }

        internal void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
    }

    private sealed class ProbeTcpClient(Action? onDispose) : TcpClient(AddressFamily.InterNetwork)
    {
        private int _disposeCount;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    Interlocked.Increment(ref _disposeCount);
                    onDispose?.Invoke();
                }
            }
            finally { base.Dispose(disposing); }
        }

        internal void DisposeForCleanup() => base.Dispose(true);
    }

    private sealed class ProbeSslStream(Action? onDispose) : SslStream(new MemoryStream(), leaveInnerStreamOpen: false)
    {
        private int _disposeCount;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    Interlocked.Increment(ref _disposeCount);
                    onDispose?.Invoke();
                }
            }
            finally { base.Dispose(disposing); }
        }

        internal void DisposeForCleanup() => base.Dispose(true);
    }
}
