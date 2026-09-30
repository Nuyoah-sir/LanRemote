using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using LanRemote.Core.Models;

namespace LanRemote.Transport.Tests;

public sealed class AuthenticatedControlSessionAttachBudgetTests
{
    private static readonly Guid SessionId = new("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private const long BudgetTicks = 15_000 * TimeSpan.TicksPerMillisecond;
    private const string TokenHex = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
    private const string NonceHex = "202122232425262728292A2B2C2D2E2F";
    private const string PinHex = "808182838485868788898A8B8C8D8E8F909192939495969798999A9B9C9D9E9F";
    // 复用既有独立 transcript/HMAC 黄金值，不以生产 proof 方法生成预期值。
    private const string ProofHex = "602656EADC023F939F0F922B6311A362C3AE9048A880FCF4381E552F178B7A3A";

    [Theory]
    [InlineData(0L)]
    [InlineData(-123_456L)]
    public void Constructor_AcceptsZeroAndNegativeOrigin_WithoutReadingClock(long successReceivedAt)
    {
        using SessionFixture fixture = new();
        ProbeClock clock = new(successReceivedAt + 2 * TimeSpan.TicksPerSecond);
        AuthenticatedControlSession session = fixture.CreateSession(15_000, successReceivedAt, clock);

        Assert.Equal(0, clock.TimestampReads);
        Assert.Equal(0, clock.FrequencyReads);
        clock.AssertNoTimersOrWallClock();
        Assert.Equal(TimeSpan.FromSeconds(13), session.GetRemainingAttachBudget());
        Assert.Equal(1, clock.TimestampReads);
        Assert.True(clock.FrequencyReads > 0);
        fixture.AssertLiveAndUntouched(session, clock);
    }

    [Theory]
    [InlineData(true, 15_000)]
    [InlineData(false, 0)]
    [InlineData(false, -1)]
    [InlineData(false, int.MinValue)]
    public void Constructor_RejectsInvalidClockOrHint_BeforeAllocatingTokenCopy(bool nullClock, int hint)
    {
        using SessionFixture fixture = new();
        ProbeClock clock = new(0);
        byte[] token = new byte[1024 * 1024];
        Array.Fill(token, (byte)0xA5);
        Action construct = () => fixture.CreateSession(hint, 0, nullClock ? null : clock, token);
        string parameter = nullClock ? "clock" : "videoAttachExpiresInMsHint";

        AssertRejection(Record.Exception(construct));
        // 预热后仅测当前线程的构造分配；大输入是 ToArray 复制哨兵，不是失败对象的秘密反射证据。
        long before = GC.GetAllocatedBytesForCurrentThread();
        Exception? error = Record.Exception(construct);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        AssertRejection(error);
        Assert.True(allocated < token.Length, $"拒绝构造分配了 {allocated} 字节，疑似先复制了 token。");
        Assert.True(token.AsSpan().IndexOfAnyExcept((byte)0xA5) < 0, "拒绝构造不应修改调用方 token。");
        Assert.Equal(0, clock.TimestampReads);
        Assert.Equal(0, clock.FrequencyReads);
        clock.AssertNoTimersOrWallClock();
        fixture.AssertNotClosingOrDoingIo();

        void AssertRejection(Exception? rejection)
        {
            ArgumentException argument = nullClock
                ? Assert.IsType<ArgumentNullException>(rejection)
                : Assert.IsType<ArgumentOutOfRangeException>(rejection);
            Assert.Equal(parameter, argument.ParamName);
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(15_000, 15_000)]
    [InlineData(15_001, 15_000)]
    [InlineData(int.MaxValue, 15_000)]
    public void Hint_IsCappedAt15000Milliseconds_WithoutOverflow(int hint, int expectedMilliseconds)
    {
        using SessionFixture fixture = new();
        ProbeClock clock = new(73);
        AuthenticatedControlSession session = fixture.CreateSession(hint, 73, clock);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), session.GetRemainingAttachBudget());
        Assert.Equal(1, clock.TimestampReads);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(1L)]
    public void Deadline_BMinusOneTick_B_And_BPlusOneTick_AreIndependentCases(long offset)
    {
        const long start = 987_654_321;
        using SessionFixture fixture = new();
        ProbeClock clock = new(start + BudgetTicks + offset);
        AuthenticatedControlSession session = fixture.CreateSession(15_000, start, clock);

        if (offset < 0)
            Assert.Equal(TimeSpan.FromTicks(1), session.GetRemainingAttachBudget());
        else
            Assert.Throws<TimeoutException>(() => session.GetRemainingAttachBudget());

        Assert.Equal(1, clock.TimestampReads);
        fixture.AssertLiveAndUntouched(session, clock);
    }

    [Theory]
    [InlineData(1024L, 256L, 7_500_000L)]
    [InlineData(3L, 1L, 6_666_667L)]
    public void ElapsedTime_UsesProviderFrequency_AndTimeSpanTickTruncation(
        long frequency, long elapsedTimestamp, long expectedRemainingTicks)
    {
        const long start = -2048;
        using SessionFixture fixture = new();
        ProbeClock clock = new(start + elapsedTimestamp, frequency);
        AuthenticatedControlSession session = fixture.CreateSession(1000, start, clock);

        // 256/1024 秒 = 250ms；1/3 秒在 TimeSpan 中截断到 3,333,333 ticks。
        Assert.Equal(TimeSpan.FromTicks(expectedRemainingTicks), session.GetRemainingAttachBudget());
        Assert.Equal(1, clock.TimestampReads);
        Assert.Equal(1, clock.FrequencyReads);
        fixture.AssertLiveAndUntouched(session, clock);
    }

    [Fact]
    public void NegativeElapsed_ThrowsTimeout_WithoutRevokingOrConsumingSession()
    {
        using SessionFixture fixture = new();
        ProbeClock clock = new(122);
        AuthenticatedControlSession session = fixture.CreateSession(15_000, 123, clock);

        Assert.Throws<TimeoutException>(() => session.GetRemainingAttachBudget());
        fixture.AssertLiveAndUntouched(session, clock);
        clock.Timestamp = 123;
        Assert.Equal(TimeSpan.FromMilliseconds(15_000), session.GetRemainingAttachBudget());
        Assert.Equal(2, clock.TimestampReads);
        fixture.AssertLiveAndUntouched(session, clock);
    }

    [Fact]
    public void RepeatedQueries_SampleOnceUnderGate_WithoutRenewalOrConsumption()
    {
        const long start = 42;
        using SessionFixture fixture = new();
        ProbeClock clock = new(start + 2 * TimeSpan.TicksPerSecond);
        AuthenticatedControlSession session = fixture.CreateSession(15_000, start, clock);
        object gate = GetGate(session);
        clock.OnTimestamp = () => Assert.True(Monitor.IsEntered(gate));
        clock.OnFrequency = () => Assert.True(Monitor.IsEntered(gate));

        Assert.Equal(TimeSpan.FromSeconds(13), session.GetRemainingAttachBudget());
        Assert.Equal(TimeSpan.FromSeconds(13), session.GetRemainingAttachBudget());
        Assert.Equal(2, clock.TimestampReads);
        fixture.AssertLiveAndUntouched(session, clock);

        clock.Timestamp = start + 3 * TimeSpan.TicksPerSecond;
        clock.OnTimestamp = () =>
        {
            Assert.True(Monitor.IsEntered(gate));
            // 返回已捕获的 now 后把下一次采样推进到到期，揭露一次查询重复取时。
            clock.Timestamp = start + BudgetTicks;
        };
        Assert.Equal(TimeSpan.FromSeconds(12), session.GetRemainingAttachBudget());
        Assert.Equal(3, clock.TimestampReads);
        Assert.Throws<TimeoutException>(() => session.GetRemainingAttachBudget());
        Assert.Throws<TimeoutException>(() => session.GetRemainingAttachBudget());
        Assert.Equal(5, clock.TimestampReads);
        Assert.Equal(5, clock.FrequencyReads);
        fixture.AssertLiveAndUntouched(session, clock);
    }

    [Theory]
    [InlineData("disposed")]
    [InlineData("revoked")]
    [InlineData("cancelled")]
    public void EntryPriority_IsDisposedOrRevoked_ThenCallerCancellation_ThenExpiry(string state)
    {
        using SessionFixture fixture = new();
        ProbeClock clock = new(BudgetTicks);
        AuthenticatedControlSession session = fixture.CreateSession(15_000, 0, clock);
        using CancellationTokenSource caller = new();
        caller.Cancel();
        if (state == "disposed") session.Dispose();
        if (state == "revoked") session.RevokeForOwnerCleanup();
        int tcpCloses = fixture.Client.DisposeCount;
        int sslCloses = fixture.Ssl.DisposeCount;
        bool closeRequested = fixture.Connection.IsCloseRequested;

        if (state == "cancelled")
        {
            OperationCanceledException error = Assert.Throws<OperationCanceledException>(
                () => session.GetRemainingAttachBudget(caller.Token));
            Assert.Equal(caller.Token, error.CancellationToken);
        }
        else
        {
            Assert.Throws<ObjectDisposedException>(() => session.GetRemainingAttachBudget(caller.Token));
        }
        Assert.Equal(0, clock.TimestampReads);
        Assert.Equal(0, clock.FrequencyReads);
        clock.AssertNoTimersOrWallClock();
        Assert.Equal(tcpCloses, fixture.Client.DisposeCount);
        Assert.Equal(sslCloses, fixture.Ssl.DisposeCount);
        Assert.Equal(closeRequested, fixture.Connection.IsCloseRequested);
        Assert.Equal(0, fixture.Ssl.IoCalls);
        if (state != "disposed") fixture.AssertNotClosingOrDoingIo();
        if (state == "cancelled")
        {
            Assert.Throws<TimeoutException>(() => session.GetRemainingAttachBudget());
            fixture.AssertLiveAndUntouched(session, clock);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueuedQuery_ChecksRevocationAndCancellationInsideTheSameGate(bool revoke)
    {
        using SessionFixture fixture = new();
        ProbeClock clock = new(0);
        AuthenticatedControlSession session = fixture.CreateSession(15_000, 0, clock);
        using CancellationTokenSource caller = new();
        using ManualResetEventSlim entered = new();
        object gate = GetGate(session);
        Exception? error = null;
        Thread worker = new(() =>
        {
            entered.Set();
            error = Record.Exception(() => session.GetRemainingAttachBudget(caller.Token));
        }) { IsBackground = true };
        bool started = false;
        Monitor.Enter(gate);
        try
        {
            worker.Start();
            started = true;
            Assert.True(entered.Wait(Guard), "查询线程未到达调用边界。");
            bool observedWaiting = false;
            Assert.True(SpinWait.SpinUntil(() =>
            {
                observedWaiting = (worker.ThreadState & ThreadState.WaitSleepJoin) != 0;
                return observedWaiting || !worker.IsAlive;
            }, Guard), "查询线程未在同一 gate 等待。");
            Assert.True(observedWaiting, "查询越过了受控 gate。");
            Assert.Equal(0, clock.TimestampReads);
            caller.Cancel();
            if (revoke) session.RevokeForOwnerCleanup();
        }
        finally
        {
            Monitor.Exit(gate);
            if (started) Assert.True(worker.Join(Guard), "释放 gate 后查询线程未结束。");
        }

        if (revoke) Assert.IsType<ObjectDisposedException>(error);
        else Assert.Equal(caller.Token, Assert.IsType<OperationCanceledException>(error).CancellationToken);
        Assert.Equal(0, clock.TimestampReads);
        Assert.Equal(0, clock.FrequencyReads);
        fixture.AssertNotClosingOrDoingIo();
        clock.AssertNoTimersOrWallClock();
        if (!revoke)
        {
            Assert.Equal(TimeSpan.FromSeconds(15), session.GetRemainingAttachBudget());
            Assert.Equal(1, clock.TimestampReads);
            fixture.AssertLiveAndUntouched(session, clock);
        }
    }

    [Theory]
    [InlineData("timestamp", false)]
    [InlineData("timestamp", true)]
    [InlineData("frequency", false)]
    [InlineData("frequency", true)]
    public void ReentrantClockCallbacks_RecheckRevocationThenCancellation_BeforeExpiry(string phase, bool revoke)
    {
        using SessionFixture fixture = new();
        ProbeClock clock = new(BudgetTicks);
        AuthenticatedControlSession session = fixture.CreateSession(15_000, 0, clock);
        byte[] ownedToken = TestOnlyControlSessionSecrets.GetOwnedToken(session);
        object gate = GetGate(session);
        using CancellationTokenSource caller = new();
        int callbacks = 0;
        Action changeState = () =>
        {
            callbacks++;
            Assert.True(Monitor.IsEntered(gate));
            caller.Cancel();
            if (revoke) session.RevokeForOwnerCleanup();
        };
        if (phase == "timestamp") clock.OnTimestamp = changeState;
        else clock.OnFrequency = changeState;

        if (revoke)
            Assert.Throws<ObjectDisposedException>(() => session.GetRemainingAttachBudget(caller.Token));
        else
        {
            OperationCanceledException error = Assert.Throws<OperationCanceledException>(
                () => session.GetRemainingAttachBudget(caller.Token));
            Assert.Equal(caller.Token, error.CancellationToken);
        }
        Assert.True(callbacks > 0);
        Assert.Equal(1, clock.TimestampReads);
        if (phase == "frequency") Assert.True(clock.FrequencyReads > 0);
        fixture.AssertNotClosingOrDoingIo();
        clock.AssertNoTimersOrWallClock();
        if (revoke)
        {
            Assert.Same(ownedToken, TestOnlyControlSessionSecrets.GetOwnedToken(session));
            Assert.All(ownedToken, value => Assert.Equal((byte)0, value));
        }
        else
        {
            fixture.AssertLiveAndUntouched(session, clock);
        }
    }

    [Fact]
    public void BudgetAndRequiredClockConstructor_RemainInternal_WithoutExpandingPublicSurface()
    {
        Type type = typeof(AuthenticatedControlSession);
        const BindingFlags declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        const BindingFlags all = declared | BindingFlags.Public | BindingFlags.NonPublic;
        MethodInfo? budget = type.GetMethod("GetRemainingAttachBudget", all);
        Assert.NotNull(budget);
        Assert.True(budget.IsAssembly);
        Assert.False(budget.IsStatic);
        Assert.Equal(typeof(TimeSpan), budget.ReturnType);
        ParameterInfo cancellation = Assert.Single(budget.GetParameters());
        Assert.Equal(typeof(CancellationToken), cancellation.ParameterType);
        Assert.Equal("cancellationToken", cancellation.Name);
        Assert.True(cancellation.IsOptional);
        Assert.True(cancellation.HasDefaultValue);

        ConstructorInfo constructor = Assert.Single(type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.True(constructor.IsAssembly);
        ParameterInfo[] parameters = constructor.GetParameters();
        Assert.Equal(new[] { typeof(TlsConnection), typeof(SessionPermission), typeof(Guid), typeof(string),
            typeof(ReadOnlySpan<byte>), typeof(int), typeof(long), typeof(TimeProvider) },
            parameters.Select(parameter => parameter.ParameterType).ToArray());
        Assert.Equal(new[] { "videoAttachExpiresInMsHint", "successReceivedAt", "clock" },
            parameters.TakeLast(3).Select(parameter => parameter.Name).ToArray());
        Assert.All(parameters.TakeLast(3), parameter =>
        {
            Assert.False(parameter.IsOptional);
            Assert.False(parameter.HasDefaultValue);
        });

        const BindingFlags publicDeclared = declared | BindingFlags.Public;
        Assert.Equal(new[] { "GrantedPermission", "Identity", "SessionId", "ShortCode" },
            type.GetProperties(publicDeclared).Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { "AttachVideoAsync", "Dispose", "DisposeAsync", "get_GrantedPermission", "get_Identity", "get_SessionId", "get_ShortCode" },
            type.GetMethods(publicDeclared).Select(method => method.Name)
                .OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Empty(type.GetFields(publicDeclared));
        Assert.Empty(type.GetConstructors(publicDeclared));
        Assert.Empty(type.GetEvents(publicDeclared));
        Assert.Null(type.GetProperty("SessionToken", all));
    }

    private static object GetGate(AuthenticatedControlSession session) =>
        typeof(AuthenticatedControlSession).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(session)!;

    private sealed class ProbeClock(long timestamp, long frequency = TimeSpan.TicksPerSecond) : TimeProvider
    {
        internal long Timestamp { get; set; } = timestamp;
        internal int TimestampReads { get; private set; }
        internal int FrequencyReads { get; private set; }
        internal int TimerCreations { get; private set; }
        internal int UtcReads { get; private set; }
        internal Action? OnTimestamp { get; set; }
        internal Action? OnFrequency { get; set; }

        public override long GetTimestamp()
        {
            TimestampReads++;
            long captured = Timestamp;
            OnTimestamp?.Invoke();
            return captured;
        }

        public override long TimestampFrequency
        {
            get
            {
                FrequencyReads++;
                OnFrequency?.Invoke();
                return frequency;
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            UtcReads++;
            throw new InvalidOperationException("预算不应读取墙钟。");
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            TimerCreations++;
            throw new InvalidOperationException("预算查询不应创建计时器。");
        }

        internal void AssertNoTimersOrWallClock()
        {
            Assert.Equal(0, TimerCreations);
            Assert.Equal(0, UtcReads);
        }
    }

    // 未连接的 TCP/SslStream 仅为单元测试夹具；不执行或声称真实 TLS 握手、视频附着或远端资格消费。
    private sealed class SessionFixture : IDisposable
    {
        internal ProbeTcpClient Client { get; } = new();
        internal ProbeSslStream Ssl { get; } = new();
        internal TlsConnection Connection { get; }
        private readonly byte[] _callerToken = Convert.FromHexString(TokenHex);
        private AuthenticatedControlSession? _session;
        private byte[]? _ownedToken;

        internal SessionFixture()
        {
            byte[] pin = Convert.FromHexString(PinHex);
            Assert.True(ConnectionTarget.TryCreate(new Guid("11111111-2222-3333-4444-555555555555"),
                IPAddress.Loopback, 12345, PinHex, out ConnectionTarget? target));
            Assert.True(ConnectionIdentity.TryCreate(target!, pin, out ConnectionIdentity? identity));
            Connection = new TlsConnection(identity!, Client, Ssl);
        }

        internal AuthenticatedControlSession CreateSession(int hint, long successReceivedAt, TimeProvider? clock,
            byte[]? token = null)
        {
            _session = new AuthenticatedControlSession(Connection, SessionPermission.Control,
                SessionId, "ABCDEF", token ?? _callerToken, hint, successReceivedAt, clock!);
            _ownedToken = TestOnlyControlSessionSecrets.GetOwnedToken(_session);
            return _session;
        }

        internal void AssertLiveAndUntouched(AuthenticatedControlSession session, ProbeClock clock)
        {
            Assert.NotNull(_ownedToken);
            Assert.NotSame(_callerToken, _ownedToken);
            Assert.Same(_ownedToken, TestOnlyControlSessionSecrets.GetOwnedToken(session));
            Assert.Equal(TokenHex, Convert.ToHexString(_ownedToken));
            Assert.Equal(TokenHex, Convert.ToHexString(_callerToken));
            Assert.Same(Ssl, session.Stream);
            int timestampReads = clock.TimestampReads;
            int frequencyReads = clock.FrequencyReads;
            Assert.Equal(ProofHex, Convert.ToHexString(session.CreateVideoAttachProof(
                Convert.FromHexString(NonceHex), Convert.FromHexString(PinHex))));
            // 即使预算已到期或上次查询被取消，原 proof 合同仍无本地 TTL，也不读取时钟。
            Assert.Equal(timestampReads, clock.TimestampReads);
            Assert.Equal(frequencyReads, clock.FrequencyReads);
            clock.AssertNoTimersOrWallClock();
            AssertNotClosingOrDoingIo();
        }

        internal void AssertNotClosingOrDoingIo()
        {
            Assert.False(Connection.IsCloseRequested);
            Assert.Equal(0, Client.DisposeCount);
            Assert.Equal(0, Ssl.DisposeCount);
            Assert.Equal(0, Ssl.IoCalls);
            Assert.Same(Ssl, Connection.Stream);
            Assert.Empty(Connection.CleanupErrors);
        }

        public void Dispose()
        {
            // 所有无关闭断言都在夹具清理前；撤销后由原 owner 统一释放未连接资源。
            try { _session?.RevokeForOwnerCleanup(); }
            finally { Connection.Dispose(); }
        }
    }

    private sealed class ProbeTcpClient : TcpClient
    {
        private int _disposeCount;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        protected override void Dispose(bool disposing)
        {
            if (disposing) Interlocked.Increment(ref _disposeCount);
            base.Dispose(disposing);
        }
    }

    private sealed class ProbeSslStream() : SslStream(new MemoryStream(), leaveInnerStreamOpen: false)
    {
        private int _disposeCount;
        private int _ioCalls;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        internal int IoCalls => Volatile.Read(ref _ioCalls);

        private Exception UnexpectedIo()
        {
            Interlocked.Increment(ref _ioCalls);
            return new InvalidOperationException("预算查询不应在控制 TLS 上执行 I/O。");
        }

        public override int Read(byte[] buffer, int offset, int count) => throw UnexpectedIo();
        public override int Read(Span<byte> buffer) => throw UnexpectedIo();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            throw UnexpectedIo();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw UnexpectedIo();
        public override void Write(byte[] buffer, int offset, int count) => throw UnexpectedIo();
        public override void Write(ReadOnlySpan<byte> buffer) => throw UnexpectedIo();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            throw UnexpectedIo();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw UnexpectedIo();
        public override void Flush() => throw UnexpectedIo();
        public override Task FlushAsync(CancellationToken cancellationToken) => throw UnexpectedIo();

        protected override void Dispose(bool disposing)
        {
            if (disposing) Interlocked.Increment(ref _disposeCount);
            base.Dispose(disposing);
        }
    }
}
