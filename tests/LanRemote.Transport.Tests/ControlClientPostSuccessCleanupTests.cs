using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;
using Xunit.Abstractions;

namespace LanRemote.Transport.Tests;

// 只替换 TLS 获取和应用层 IO；认证、严格解析、MAC、会话构造和 owner 收尾均走产品入口。
public sealed class ControlClientPostSuccessCleanupTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MachineBudget = TimeSpan.FromSeconds(9);
    private static readonly TimeSpan ApprovalBudget = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan PayloadBudget = TimeSpan.FromMilliseconds(1100);
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static IEnumerable<object[]> FinalChecks()
    {
        foreach (bool pending in new[] { false, true })
        foreach (bool callerFinalCancellation in new[] { false, true })
        for (int cleanup = 0; cleanup < 4; cleanup++)
            yield return [pending, callerFinalCancellation, cleanup];
    }

    [Theory(Timeout = 120_000)]
    [MemberData(nameof(FinalChecks))]
    public async Task ConstructedSession_FinalTimeoutOrCallerCancel_PreservesPrimaryAndOwnsCleanupOnce(
        bool pending, bool callerFinalCancellation, int cleanup)
    {
        Fixture fixture = new(pending, callerFinalCancellation, cleanup, pausePayload: true);
        try
        {
            fixture.Start();
            Task<AuthenticatedControlSession> original = await fixture.Returned.Task.WaitAsync(Guard);
            await fixture.Ssl.PayloadEntered.Task.WaitAsync(Guard);
            CapturedSecrets secrets = new(original);
            fixture.ObserveParsedSuccess = secrets.CaptureParsedSuccess;
            Assert.NotSame(fixture.Key, secrets.Key);
            Assert.Equal(fixture.Key, secrets.Key);
            Assert.Contains(secrets.Transcript, value => value != 0);
            Assert.False(fixture.Connection.IsCloseRequested);
            fixture.Ssl.ReleasePayload();

            await fixture.TcpClose.Entered.Task.WaitAsync(Guard);
            fixture.AssertFinalPath();
            Assert.False(original.IsCompleted);
            Assert.False(fixture.SslClose.Entered.Task.IsCompleted);
            // 此处仍持有原数组，且 TCP 的释放闸尚未放行；不能等最终 finally 清完才断言顺序。
            bool keyZeroBeforeTcp = IsZero(secrets.Key);
            bool transcriptZeroBeforeTcp = IsZero(secrets.Transcript);
            bool parsedTokenZeroBeforeTcp = IsZero(Assert.IsType<byte[]>(secrets.ParsedToken));
            // 真正 new 出来的 session 与解析帧拥有不同的 token 原数组；测试此时没有补做 Dispose。
            output.WriteLine($"TCP闸前：真实构造事件={fixture.Constructions.Count}；" +
                $"session自有token清零={IsZero(ReadSessionToken(Assert.Single(fixture.Constructions)))}；" +
                $"测试Dispose次数={fixture.FallbackDisposeCalls}");
            fixture.AssertSessionRevoked(secrets.ParsedToken);
            Task sharedClose = fixture.Connection.CloseAsync();
            Assert.Same(sharedClose, fixture.Connection.CloseAsync());
            Assert.False(sharedClose.IsCompleted);
            fixture.TcpClose.Release();

            await fixture.SslClose.Entered.Task.WaitAsync(Guard);
            Assert.False(original.IsCompleted);
            Assert.False(sharedClose.IsCompleted);
            bool keyZeroBeforeSsl = IsZero(secrets.Key);
            fixture.SslClose.Release();
            Exception? observed = await ObserveAsync(original).WaitAsync(Guard);
            fixture.AssertClosed();
            Assert.True(sharedClose.IsCompletedSuccessfully);
            output.WriteLine($"路径={(pending ? "pending-success" : "direct-success")}; " +
                $"末检={(callerFinalCancellation ? "caller-cancel" : "constructed-timeout")}; cleanup={cleanup}; " +
                $"独立事件={string.Join(",", fixture.Clock.Events)}; " +
                $"TCP前 key/transcript/parsedToken={keyZeroBeforeTcp}/{transcriptZeroBeforeTcp}/{parsedTokenZeroBeforeTcp}; " +
                $"原Task={original.Status}; 实际错误={observed}");

            AssertOutcome(original, observed, fixture, callerFinalCancellation);
            Assert.True(keyZeroBeforeTcp, "私有 key 必须在开始网络清理前清零，不得被内部 session.Dispose 阻塞。");
            Assert.True(transcriptZeroBeforeTcp, "认证 transcript 原数组必须先于网络清理清零。");
            Assert.True(parsedTokenZeroBeforeTcp, "已解析 success 的 token 原数组必须先于网络清理清零。");
            Assert.True(keyZeroBeforeSsl);
            Assert.Same(observed, await ObserveAsync(original).WaitAsync(Guard));
            Assert.All(secrets.Key, value => Assert.Equal((byte)0, value));
            fixture.AssertCallerKeyUnchanged();
        }
        finally
        {
            await fixture.FinishAsync();
        }
    }

    [Theory(Timeout = 120_000)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FullySynchronousSuccess_FinalFailure_ReturnsOriginalTaskWhileBothClosesAreBlocked(
        bool pending, bool callerFinalCancellation)
    {
        Fixture fixture = new(pending, callerFinalCancellation, cleanup: 3, pausePayload: false);
        try
        {
            fixture.Start();
            await fixture.TcpClose.Entered.Task.WaitAsync(Guard);
            fixture.AssertFinalPath();
            fixture.AssertSessionRevoked();
            Assert.NotEqual(fixture.CallerThreadId, fixture.TcpClose.ThreadId);
            Assert.True(fixture.TcpClose.OnThreadPool);
            // 原入口在专用线程直接调用，没有 Task.Run(...).Unwrap() 代理冒充返回值。
            // 旧 session.Dispose 虽在 worker 释放 TCP，却同步卡住此调用线程，拿不到原 Task。
            output.WriteLine($"同步入口已到构造后末检；TCP释放线程={fixture.TcpClose.ThreadId}，" +
                $"调用线程={fixture.CallerThreadId}，TCP/SSL闸未放行，原Task是否已返回={fixture.Returned.Task.IsCompleted}");
            Task<AuthenticatedControlSession> original = await fixture.Returned.Task.WaitAsync(Guard);
            Assert.False(original.IsCompleted);
            Assert.False(fixture.SslClose.Entered.Task.IsCompleted);
            fixture.TcpClose.Release();
            await fixture.SslClose.Entered.Task.WaitAsync(Guard);
            Assert.False(original.IsCompleted);
            Assert.NotEqual(fixture.CallerThreadId, fixture.SslClose.ThreadId);
            Assert.True(fixture.SslClose.OnThreadPool);
            fixture.SslClose.Release();
            Exception? observed = await ObserveAsync(original).WaitAsync(Guard);
            fixture.AssertClosed();
            AssertOutcome(original, observed, fixture, callerFinalCancellation);
        }
        finally
        {
            await fixture.FinishAsync();
        }
    }

    [Theory(Timeout = 120_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongProofCalibration_StopsBeforeConstruction_AndKeepsExistingRejection(bool pending)
    {
        Fixture fixture = new(pending, callerFinalCancellation: false, cleanup: 0,
            pausePayload: true, wrongProof: true);
        try
        {
            fixture.Start();
            Task<AuthenticatedControlSession> original = await fixture.Returned.Task.WaitAsync(Guard);
            await fixture.Ssl.PayloadEntered.Task.WaitAsync(Guard);
            fixture.Ssl.ReleasePayload();
            await fixture.TcpClose.Entered.Task.WaitAsync(Guard);
            Assert.Empty(fixture.Constructions);
            Assert.Equal(new[] { "success-payload-disposed", "final-window-disposed" }, fixture.Clock.Events);
            Assert.False(fixture.Caller.IsCancellationRequested);
            Assert.Equal(0, fixture.FallbackDisposeCalls);
            fixture.TcpClose.Release();
            await fixture.SslClose.Entered.Task.WaitAsync(Guard);
            fixture.SslClose.Release();
            Exception? observed = await ObserveAsync(original).WaitAsync(Guard);
            ControlClientAuthenticationException rejection = Assert.IsType<ControlClientAuthenticationException>(observed);
            Assert.Equal("client-server-proof-mismatch", rejection.Rejection);
            Assert.Equal(ControlClientAuthenticationException.ServerProofFailureMessage, rejection.DisplayMessage);
            Assert.Null(rejection.InnerException);
            Assert.Equal(TaskStatus.Faulted, original.Status);
            fixture.AssertClosed();
            Assert.Empty(fixture.Constructions);
            Assert.Equal(0, fixture.Clock.Timestamp);
            output.WriteLine("错误 proof 校准：真实构造事件为0，单调时间未推进，拒绝语义未变。");
        }
        finally
        {
            await fixture.FinishAsync();
        }
    }

    [Theory(Timeout = 120_000)]
    [InlineData(false, 0)]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    [InlineData(true, 3)]
    public async Task ConstructionObserver_ThrowsOriginalError_RevokesUnreturnedSession(bool pending, int cleanup)
    {
        InvalidOperationException observerError = new("受控构造观察器故障。");
        Fixture fixture = new(pending, callerFinalCancellation: false, cleanup,
            pausePayload: true, observerError: observerError);
        try
        {
            fixture.Start();
            Task<AuthenticatedControlSession> original = await fixture.Returned.Task.WaitAsync(Guard);
            await fixture.Ssl.PayloadEntered.Task.WaitAsync(Guard);
            CapturedSecrets secrets = new(original);
            fixture.ObserveParsedSuccess = secrets.CaptureParsedSuccess;
            fixture.Ssl.ReleasePayload();
            await fixture.TcpClose.Entered.Task.WaitAsync(Guard);
            fixture.AssertSessionRevoked(secrets.ParsedToken);
            Assert.True(IsZero(secrets.Key));
            Assert.True(IsZero(secrets.Transcript));
            Assert.True(IsZero(Assert.IsType<byte[]>(secrets.ParsedToken)));
            Assert.Equal(new[] { "success-payload-disposed", "session-constructed", "final-window-disposed" },
                fixture.Clock.Events);
            Assert.Equal(0, fixture.Clock.Timestamp);
            Assert.False(fixture.Caller.IsCancellationRequested);
            Assert.False(original.IsCompleted);
            Assert.False(fixture.SslClose.Entered.Task.IsCompleted);
            output.WriteLine($"观察器同步抛错：真实构造事件={fixture.Constructions.Count}；" +
                $"TCP闸前session自有token已清零且访问拒绝；测试Dispose次数={fixture.FallbackDisposeCalls}");
            fixture.TcpClose.Release();
            await fixture.SslClose.Entered.Task.WaitAsync(Guard);
            Assert.False(original.IsCompleted);
            fixture.SslClose.Release();
            Exception? observed = await ObserveAsync(original).WaitAsync(Guard);
            if (cleanup == 0)
            {
                Assert.Same(observerError, observed);
            }
            else
            {
                AggregateException outer = Assert.IsType<AggregateException>(observed);
                Assert.Equal(2, outer.InnerExceptions.Count);
                Assert.Same(observerError, outer.InnerExceptions[0]);
                AggregateException close = Assert.IsType<AggregateException>(outer.InnerExceptions[1]);
                Assert.Equal(fixture.Errors.Length, close.InnerExceptions.Count);
                for (int i = 0; i < fixture.Errors.Length; i++)
                    Assert.Same(fixture.Errors[i], close.InnerExceptions[i]);
            }
            Assert.Equal(TaskStatus.Faulted, original.Status);
            fixture.AssertClosed();
            fixture.Ssl.AssertWireConsumed(pending);
        }
        finally
        {
            await fixture.FinishAsync();
        }
    }

    private static void AssertOutcome(Task original, Exception? observed, Fixture fixture, bool canceled)
    {
        Assert.NotNull(observed);
        Exception primary = observed;
        if (fixture.Errors.Length != 0)
        {
            Assert.Equal(TaskStatus.Faulted, original.Status);
            AggregateException outer = Assert.IsType<AggregateException>(observed);
            Assert.Equal(2, outer.InnerExceptions.Count);
            primary = outer.InnerExceptions[0];
            AggregateException cleanup = Assert.IsType<AggregateException>(outer.InnerExceptions[1]);
            Assert.Equal(fixture.Errors.Length, cleanup.InnerExceptions.Count);
            for (int i = 0; i < fixture.Errors.Length; i++)
                Assert.Same(fixture.Errors[i], cleanup.InnerExceptions[i]);
        }
        if (canceled)
        {
            OperationCanceledException error = Assert.IsAssignableFrom<OperationCanceledException>(primary);
            Assert.Equal(fixture.Caller.Token, error.CancellationToken);
            Assert.Null(error.InnerException);
            Assert.Equal(fixture.Errors.Length == 0 ? TaskStatus.Canceled : TaskStatus.Faulted, original.Status);
        }
        else
        {
            ControlClientAuthenticationException error = Assert.IsType<ControlClientAuthenticationException>(primary);
            Assert.Equal(fixture.Pending ? "client-approval-timeout" : "client-machine-timeout", error.Rejection);
            Assert.Null(error.InnerException);
            Assert.Equal(TaskStatus.Faulted, original.Status);
        }
    }

    private static byte[] ReadSessionToken(AuthenticatedControlSession session) =>
        Assert.IsType<byte[]>(typeof(AuthenticatedControlSession).GetField("_sessionToken", Fields)!.GetValue(session));

    private static bool IsZero(byte[] bytes) => bytes.All(value => value == 0);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<Exception?> ObserveAsync(Task original)
    {
        try { await original.ConfigureAwait(false); return null; }
        catch (Exception error) { return error; }
    }

    private static object ReadStateMachine(Task original, string method)
    {
        FieldInfo? field = null;
        for (Type? type = original.GetType(); type is not null; type = type.BaseType)
        {
            field = type.GetField("StateMachine", Fields | BindingFlags.DeclaredOnly);
            if (field is null) continue;
            Assert.Equal(typeof(AsyncTaskMethodBuilder<>), type.DeclaringType);
            break;
        }
        Assert.NotNull(field);
        object machine = Assert.IsAssignableFrom<object>(field.GetValue(original));
        Type expected = typeof(ControlClientConnector).GetMethod(method,
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        Assert.Equal(expected, machine.GetType());
        return machine;
    }

    private static T ReadLocal<T>(object machine, string name) => Assert.IsType<T>(
        Assert.Single(machine.GetType().GetFields(Fields),
            field => field.Name.StartsWith($"<{name}>5__", StringComparison.Ordinal)).GetValue(machine));

    private sealed class CapturedSecrets
    {
        private readonly Task _authentication;
        internal byte[] Key { get; }
        internal byte[] Transcript { get; }
        internal byte[]? ParsedToken { get; private set; }

        internal CapturedSecrets(Task<AuthenticatedControlSession> original)
        {
            Assert.False(original.IsCompleted);
            object owner = ReadStateMachine(original, nameof(ControlClientConnector.ConnectAndAuthenticateAsync));
            Key = ReadLocal<byte[]>(owner, "key");
            // 只沿原 owner 当前的强类型 awaiter 取正在等待的认证 Task，不扫描 continuation/堆。
            object awaiter = Assert.Single(owner.GetType().GetFields(Fields), field =>
                field.FieldType == typeof(ConfiguredTaskAwaitable<AuthenticatedControlSession>.ConfiguredTaskAwaiter)).GetValue(owner)!;
            _authentication = Assert.IsAssignableFrom<Task>(awaiter.GetType().GetField("m_task", Fields)!.GetValue(awaiter));
            Transcript = ReadLocal<byte[]>(ReadStateMachine(_authentication, "AuthenticateConnectedAsync"), "clientTranscript");
        }

        internal void CaptureParsedSuccess()
        {
            AuthSuccessFrame frame = ReadLocal<AuthSuccessFrame>(
                ReadStateMachine(_authentication, "AuthenticateConnectedAsync"), "success");
            Assert.True(MemoryMarshal.TryGetArray(frame.SessionToken, out ArraySegment<byte> segment));
            Assert.Equal(0, segment.Offset);
            ParsedToken = Assert.IsType<byte[]>(segment.Array);
            Assert.Equal(32, ParsedToken.Length);
            Assert.Contains(ParsedToken, value => value != 0);
        }

        // expectedProof、grantTranscript 仍是 Verify 的同步栈局部，不能取得原数组，
        // 不拿接收的 serverProof 或测试副本假证它们清零。session 则另由真实构造观察器捕获。
    }

    private sealed class SuccessClock(bool pending, bool callerFinalCancellation,
        bool expireAfterConstruction, Action cancel) : TimeProvider
    {
        private readonly ManualDeadlineClock _inner = new();
        private readonly List<ProbeTimer> _timers = new();
        private ProbeTimer? _successPayload;
        private ProbeTimer? _finalWindow;
        internal List<string> Events { get; } = new();
        internal bool PayloadDisposed { get; private set; }
        internal bool CanceledAfterVerify { get; private set; }
        internal int TimerCallbacks { get; private set; }
        internal long Timestamp => _inner.GetTimestamp();
        internal int ActiveTimers => _inner.TimerCount;
        internal TimeSpan FinalBudget => pending ? ApprovalBudget : MachineBudget;
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
        public override long GetTimestamp() => _inner.GetTimestamp();

        internal void SessionConstructed()
        {
            // 只由真实 new 后的实例观察器调用，与 GetRemaining/GetTimestamp 的次数完全独立。
            Events.Add("session-constructed");
            if (expireAfterConstruction)
            {
                _inner.Advance(FinalBudget, fireTimers: false);
                Events.Add("deadline-advanced");
            }
        }

        internal void SuccessPayloadStarted()
        {
            Assert.Null(_successPayload);
            _successPayload = _timers.Last(timer => !timer.Disposed && timer.Budget == PayloadBudget);
            // direct 的 machine/challenge 预算相同，后创建的是 challenge；pending 则是独立 approval。
            // 最终帧的 prefix timer 此时已释放，不能把它误作认证窗口。
            _finalWindow = _timers.Last(timer => !timer.Disposed && timer.Budget == FinalBudget);
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer inner = _inner.CreateTimer(value => { TimerCallbacks++; callback(value); }, state, dueTime, period);
            ProbeTimer timer = new(inner, dueTime, OnDisposed);
            _timers.Add(timer);
            return timer;
        }

        private void OnDisposed(ProbeTimer timer)
        {
            if (ReferenceEquals(timer, _successPayload))
            {
                Assert.False(PayloadDisposed);
                PayloadDisposed = true;
                Events.Add("success-payload-disposed");
            }
            if (ReferenceEquals(timer, _finalWindow))
            {
                Events.Add("final-window-disposed");
                if (callerFinalCancellation)
                {
                    // caller 场景没有到期/观察器抛错；此 using 的 Dispose 在 Verify 返回之后。
                    // 取消仅由该 timer 的独立释放事件触发，不从构造 hook 或取时次数触发。
                    CanceledAfterVerify = true;
                    cancel();
                    Events.Add("caller-canceled");
                }
            }
        }
    }

    private sealed class ProbeTimer(ITimer inner, TimeSpan budget, Action<ProbeTimer> disposed) : ITimer
    {
        internal TimeSpan Budget { get; } = budget;
        internal bool Disposed { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);
        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            inner.Dispose();
            disposed(this);
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class Fixture
    {
        private readonly ProbeTcp _tcp;
        private readonly byte[] _keyBefore;
        private readonly bool _cancel;
        private readonly Exception? _observerError;
        private bool _revocationAsserted;
        internal ConcurrentQueue<AuthenticatedControlSession> Constructions { get; } = new();
        private readonly ConcurrentQueue<byte[]> _constructedTokens = new();
        internal Action? ObserveParsedSuccess { get; set; }
        internal int FallbackDisposeCalls { get; private set; }
        private readonly TransportTimeouts _timeouts = new(
            connectTimeout: TimeSpan.FromSeconds(5), handshakeTimeout: TimeSpan.FromSeconds(5),
            lengthPrefixTimeout: TimeSpan.FromMilliseconds(1700), payloadTimeout: PayloadBudget,
            helloTimeout: TimeSpan.FromSeconds(2), preAuthEnvelopeTimeout: TimeSpan.FromSeconds(5));
        private Thread? _thread;
        private int _connectCalls;
        internal bool Pending { get; }
        internal byte[] Key { get; } = Convert.FromHexString("0F1E2D3C4B5A69788796A5B4C3D2E1F0");
        internal CancellationTokenSource Caller { get; } = new();
        internal SuccessClock Clock { get; }
        internal ScriptedSsl Ssl { get; }
        internal TlsConnection Connection { get; }
        internal ConnectionTarget Target { get; }
        internal CloseGate TcpClose { get; }
        internal CloseGate SslClose { get; }
        internal Exception[] Errors { get; }
        internal ConcurrentQueue<string> CloseOrder { get; } = new();
        internal TaskCompletionSource<Task<AuthenticatedControlSession>> Returned { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int CallerThreadId => _thread!.ManagedThreadId;
        internal TimeSpan FinalBudget => Clock.FinalBudget;

        internal Fixture(bool pending, bool callerFinalCancellation, int cleanup, bool pausePayload,
            bool wrongProof = false, Exception? observerError = null)
        {
            Pending = pending;
            _cancel = callerFinalCancellation;
            _observerError = observerError;
            _keyBefore = Key.ToArray();
            Clock = new(pending, callerFinalCancellation,
                expireAfterConstruction: !callerFinalCancellation && observerError is null, Caller.Cancel);
            byte[] pin = Convert.FromHexString("808182838485868788898A8B8C8D8E8F909192939495969798999A9B9C9D9E9F");
            Assert.True(ConnectionTarget.TryCreate(Guid.NewGuid(), IPAddress.Loopback, 12345,
                Convert.ToHexString(pin), out ConnectionTarget? target));
            Target = target!;
            Assert.True(ConnectionIdentity.TryCreate(Target, pin, out ConnectionIdentity? identity));
            IOException? tcpError = (cleanup & 1) != 0 ? new IOException("受控 TCP 清理故障。") : null;
            IOException? sslError = (cleanup & 2) != 0 ? new IOException("受控 SSL 清理故障。") : null;
            Errors = new[] { tcpError, sslError }.OfType<Exception>().ToArray();
            TcpClose = new("tcp", CloseOrder, tcpError);
            SslClose = new("ssl", CloseOrder, sslError);
            _tcp = new(TcpClose);
            Ssl = new(Target.DeviceId, pin, Key, pending, pausePayload, wrongProof, Clock, SslClose);
            Connection = new(identity!, _tcp, Ssl);
        }

        internal void Start()
        {
            ControlClientConnector connector = new((target, timeouts, clock, token) =>
            {
                Interlocked.Increment(ref _connectCalls);
                Assert.Same(Target, target);
                Assert.Same(_timeouts, timeouts);
                Assert.Same(Clock, clock);
                Assert.Equal(Caller.Token, token);
                return Task.FromResult(Connection);
            }, session =>
            {
                // 仅保存真实对象和解析帧观测；不调用 Dispose/Revoke，也不取消 caller。
                Constructions.Enqueue(session);
                byte[] originalToken = ReadSessionToken(session);
                _constructedTokens.Enqueue(originalToken);
                Assert.Contains(originalToken, value => value != 0);
                ObserveParsedSuccess?.Invoke();
                Clock.SessionConstructed();
                if (_observerError is not null) throw _observerError;
            });
            _thread = new Thread(() =>
            {
                try
                {
                    Returned.TrySetResult(connector.ConnectAndAuthenticateAsync(Target, Ssl.ClientId,
                        ScriptedSsl.ClientName, Key, SessionPermission.Control,
                        new ControlClientAuthOptions { MachineWindow = MachineBudget, ApprovalWindow = ApprovalBudget },
                        _timeouts, Clock, Caller.Token));
                }
                catch (Exception error) { Returned.TrySetException(error); }
            }) { IsBackground = true };
            _thread.Start();
        }

        internal void AssertFinalPath()
        {
            Assert.True(Clock.PayloadDisposed);
            Assert.Single(Constructions);
            Assert.Equal(_cancel
                ? new[] { "success-payload-disposed", "session-constructed", "final-window-disposed", "caller-canceled" }
                : new[] { "success-payload-disposed", "session-constructed", "deadline-advanced", "final-window-disposed" },
                Clock.Events);
            Assert.Equal(_cancel, Clock.CanceledAfterVerify);
            Assert.Equal(_cancel, Caller.IsCancellationRequested);
            Assert.Equal(_cancel ? 0 : FinalBudget.Ticks, Clock.Timestamp);
            Assert.Equal(0, Clock.TimerCallbacks);
            Ssl.AssertWireConsumed(Pending);
        }

        internal void AssertSessionRevoked(byte[]? parsedToken = null)
        {
            _revocationAsserted = true;
            Assert.Equal(0, FallbackDisposeCalls);
            AuthenticatedControlSession session = Assert.Single(Constructions);
            byte[] token = Assert.Single(_constructedTokens);
            Assert.Same(token, ReadSessionToken(session));
            Assert.Equal(32, token.Length);
            if (parsedToken is not null) Assert.NotSame(parsedToken, token);
            bool tokenZero = IsZero(token);
            Exception? streamError = Record.Exception(() => { _ = session.Stream; });
            byte[]? unexpectedProof = null;
            Exception? proofError = Record.Exception(() => unexpectedProof = session.CreateVideoAttachProof(
                new byte[16], Connection.Identity.PresentedCertSha256.Span));
            if (unexpectedProof is not null) CryptographicOperations.ZeroMemory(unexpectedProof);
            Assert.True(tokenZero,
                $"真实session自有token未在TCP闸前清零；Stream异常={streamError?.GetType().Name}；" +
                $"proof异常={proofError?.GetType().Name ?? "无"}；测试Dispose次数={FallbackDisposeCalls}");
            // 仅 TlsConnection 开始关闭也会拒绝 Stream，必须确认拒绝来自 session 本身。
            Assert.Equal(nameof(AuthenticatedControlSession), Assert.IsType<ObjectDisposedException>(streamError).ObjectName);
            Assert.IsType<ObjectDisposedException>(proofError);
        }

        internal void AssertClosed()
        {
            Assert.Equal(1, _connectCalls);
            Assert.True(Connection.IsCloseRequested);
            Assert.Equal(1, TcpClose.Calls);
            Assert.Equal(1, SslClose.Calls);
            Assert.Equal(new[] { "tcp", "ssl" }, CloseOrder.ToArray());
            Assert.Equal(Errors.Length, Connection.CleanupErrors.Count);
            for (int i = 0; i < Errors.Length; i++) Assert.Same(Errors[i], Connection.CleanupErrors[i]);
            Assert.Equal(0, Clock.TimerCallbacks);
            Assert.Equal(0, Clock.ActiveTimers);
            AssertCallerKeyUnchanged();
        }

        internal void AssertCallerKeyUnchanged() => Assert.Equal(_keyBefore, Key);

        internal async Task FinishAsync()
        {
            // 即使断言失败或原入口同步卡在 Dispose，也先放全部闸，再观察原 IO/原 Task 并 join 专用线程。
            Ssl.ReleasePayload();
            TcpClose.Release();
            SslClose.Release();
            try
            {
                await ObserveAsync(Ssl.PendingRead).WaitAsync(Guard);
                if (_thread is not null)
                {
                    Task<AuthenticatedControlSession> original = await Returned.Task.WaitAsync(Guard);
                    await ObserveAsync(original).WaitAsync(Guard);
                }
            }
            finally
            {
                try
                {
                    if (_thread is not null) Assert.True(_thread.Join(Guard), "原入口调用线程未结束。");
                    await Connection.CloseAsync().WaitAsync(Guard);
                    AssertCallerKeyUnchanged();
                }
                finally
                {
                    try
                    {
                        // 若在主断言前已有失败，也必须先尝试撤销断言，再兜底；这不是闸前顺序证据。
                        if (!_revocationAsserted && !Constructions.IsEmpty) AssertSessionRevoked();
                    }
                    finally
                    {
                        // 所有 gate 已释放，原 Task 已观察、入口线程已 join；此后才允许测试 Dispose。
                        // 变异时 Dispose 可能重报既有清理异常，仅回收，不将其当作生产撤销证据。
                        foreach (AuthenticatedControlSession session in Constructions)
                        {
                            FallbackDisposeCalls++;
                            _ = Record.Exception(session.Dispose);
                        }
                        try { _tcp.DisposeForCleanup(); }
                        finally
                        {
                            Ssl.DisposeForCleanup();
                            TcpClose.Dispose();
                            SslClose.Dispose();
                            Caller.Dispose();
                        }
                    }
                }
            }
        }
    }

    private sealed class CloseGate(string name, ConcurrentQueue<string> order, Exception? error) : IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        internal TaskCompletionSource Entered { get; } = Signal();
        internal int Calls { get; private set; }
        internal int ThreadId { get; private set; }
        internal bool OnThreadPool { get; private set; }
        internal void Run()
        {
            Calls++;
            ThreadId = Environment.CurrentManagedThreadId;
            OnThreadPool = Thread.CurrentThread.IsThreadPoolThread;
            order.Enqueue(name);
            Entered.TrySetResult();
            if (!_release.Wait(Guard + Guard + Guard)) throw new TimeoutException("测试释放闸未放行。");
            if (error is not null) throw error;
        }
        internal void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
    }

    private sealed class ProbeTcp(CloseGate close) : TcpClient(AddressFamily.InterNetwork)
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing) close.Run();
            else base.Dispose(false);
        }
        internal void DisposeForCleanup() => base.Dispose(true);
    }

    private sealed class ScriptedSsl : SslStream
    {
        internal const string ClientName = "构造后收尾测试客户端";
        private readonly Guid _serverId;
        private readonly Guid _sessionId = Guid.NewGuid();
        private readonly byte[] _serverNonce = RandomNumberGenerator.GetBytes(32);
        private readonly byte[] _pin;
        private readonly byte[] _oracleKey;
        private readonly bool _pending;
        private readonly bool _pausePayload;
        private readonly bool _wrongProof;
        private readonly SuccessClock _clock;
        private readonly CloseGate _close;
        private readonly Queue<(string Kind, byte[] Wire)> _input = new();
        private readonly List<string> _consumed = new();
        private readonly TaskCompletionSource _payloadRelease = Signal();
        private int _offset;
        private int _writes;
        private int _flushes;
        private int _reads;
        internal Guid ClientId { get; } = Guid.NewGuid();
        internal TaskCompletionSource PayloadEntered { get; } = Signal();
        internal Task PendingRead { get; private set; } = Task.CompletedTask;

        internal ScriptedSsl(Guid serverId, byte[] pin, byte[] key, bool pending, bool pausePayload,
            bool wrongProof, SuccessClock clock, CloseGate close) : base(new MemoryStream(), false)
        {
            _serverId = serverId;
            _pin = pin;
            _oracleKey = key.ToArray();
            _pending = pending;
            _pausePayload = pausePayload;
            _wrongProof = wrongProof;
            _clock = clock;
            _close = close;
            Enqueue("challenge", JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "auth_challenge", protocol = 1, sessionId = _sessionId.ToString("D"),
                serverDeviceId = _serverId.ToString("D"), serverNonce = Convert.ToBase64String(_serverNonce),
                certSha256 = Convert.ToHexString(pin), expiresInMs = 15_000,
            }));
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal((uint)(buffer.Length - 4), BinaryPrimitives.ReadUInt32BigEndian(buffer.Span));
            ReadOnlyMemory<byte> payload = buffer[4..];
            _writes++;
            if (_writes == 1)
            {
                Assert.True(HelloFrame.TryParse(payload.Span, out _));
            }
            else
            {
                Assert.Equal(2, _writes);
                byte[] proof = VerifyResponseAndComputeServerProof(payload);
                if (_wrongProof) proof[0] ^= 0x80;
                if (_pending) Enqueue("pending", "{\"type\":\"approval_pending\"}"u8.ToArray());
                Enqueue("success", JsonSerializer.SerializeToUtf8Bytes(new
                {
                    type = "auth_success", grantedPermission = "control", serverProof = Convert.ToBase64String(proof),
                    sessionToken = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
                    videoAttachExpiresInMs = 15_000,
                }));
            }
            return ValueTask.CompletedTask;
        }

        private byte[] VerifyResponseAndComputeServerProof(ReadOnlyMemory<byte> payload)
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            Assert.Equal(new[] { "clientDeviceId", "clientName", "clientNonce", "clientProof", "requestedPermission", "type" },
                root.EnumerateObject().Select(property => property.Name).Order().ToArray());
            Assert.Equal("auth_response", root.GetProperty("type").GetString());
            Assert.Equal(ClientId.ToString("D"), root.GetProperty("clientDeviceId").GetString());
            Assert.Equal(ClientName, root.GetProperty("clientName").GetString());
            Assert.Equal("control", root.GetProperty("requestedPermission").GetString());
            string nonce = root.GetProperty("clientNonce").GetString()!;
            byte[] nonceBytes = Convert.FromBase64String(nonce);
            Assert.Equal(32, nonceBytes.Length);
            Assert.Equal(Convert.ToBase64String(nonceBytes), nonce);
            // 独立 oracle：读取实际随机 nonce，NUL 分隔、无尾 NUL、小写 D UUID、大写 HEX。
            // 不调用生产 AuthTranscriptBuilder/Compute*Proof，也不硬编码客户端随机数。
            byte[] transcript = Encoding.UTF8.GetBytes(string.Join('\0', "LANREMOTE-AUTH-V1",
                _sessionId.ToString("D"), _serverId.ToString("D"), ClientId.ToString("D"),
                Convert.ToBase64String(_serverNonce), nonce, Convert.ToHexString(_pin), "control"));
            using HMACSHA256 hmac = new(_oracleKey);
            Assert.Equal(hmac.ComputeHash(transcript), Convert.FromBase64String(root.GetProperty("clientProof").GetString()!));
            byte[] grant = Encoding.UTF8.GetBytes(string.Join('\0', "server", "LANREMOTE-GRANT-V1",
                Convert.ToHexString(SHA256.HashData(transcript)), "control"));
            return hmac.ComputeHash(grant);
        }

        public override Task FlushAsync(CancellationToken cancellationToken) { _flushes++; return Task.CompletedTask; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _reads++;
            var frame = _input.Peek();
            if (frame.Kind == "success" && _offset == 4)
            {
                _clock.SuccessPayloadStarted();
                PayloadEntered.TrySetResult();
                if (_pausePayload)
                {
                    Task<int> read = ReadPayloadAsync(buffer);
                    PendingRead = read;
                    return new(read);
                }
            }
            return ValueTask.FromResult(CopyInput(buffer));
        }

        private async Task<int> ReadPayloadAsync(Memory<byte> buffer)
        {
            await _payloadRelease.Task.ConfigureAwait(false);
            return CopyInput(buffer);
        }

        private int CopyInput(Memory<byte> buffer)
        {
            var frame = _input.Peek();
            int count = Math.Min(buffer.Length, frame.Wire.Length - _offset);
            frame.Wire.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            if (_offset == frame.Wire.Length) { _input.Dequeue(); _consumed.Add(frame.Kind); _offset = 0; }
            return count;
        }

        private void Enqueue(string kind, byte[] payload)
        {
            byte[] wire = new byte[4 + payload.Length];
            BinaryPrimitives.WriteUInt32BigEndian(wire, (uint)payload.Length);
            payload.CopyTo(wire, 4);
            _input.Enqueue((kind, wire));
        }

        internal void AssertWireConsumed(bool pending)
        {
            Assert.Equal(2, _writes);
            Assert.Equal(2, _flushes);
            Assert.Equal(pending ? 6 : 4, _reads);
            Assert.Equal(pending ? new[] { "challenge", "pending", "success" } : new[] { "challenge", "success" }, _consumed);
            Assert.Empty(_input);
        }
        internal void ReleasePayload() => _payloadRelease.TrySetResult();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _close.Run();
            else base.Dispose(false);
        }
        internal void DisposeForCleanup() { CryptographicOperations.ZeroMemory(_oracleKey); base.Dispose(true); }
    }
}
