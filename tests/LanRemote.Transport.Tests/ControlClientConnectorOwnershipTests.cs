using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

// 受控应用层 IO，不做 TLS 握手；始终调用公开 Control 入口，不伪造认证成功会话。
public sealed class ControlClientConnectorOwnershipTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private const string RemoteRejection = "client-remote-authentication-failed";

    public static IEnumerable<object[]> FramedFailures()
    {
        foreach (string wire in new[] { "rejected", "reply-rejected", "malformed", "zero-length", "truncated" })
        foreach (bool asynchronous in new[] { false, true })
        for (int cleanup = 0; cleanup < 4; cleanup++)
            yield return [wire, asynchronous, cleanup];
    }

    public static IEnumerable<object[]> InjectedFailures()
    {
        foreach (string stage in new[] { "read", "write", "flush" })
        foreach (string kind in new[] { "ordinary", "nested", "wrapped" })
        foreach (bool asynchronous in new[] { false, true })
        for (int cleanup = 0; cleanup < 4; cleanup++)
            yield return [stage, kind, asynchronous, cleanup];
    }

    public static IEnumerable<object[]> IoFailures()
    {
        foreach (string stage in new[] { "write", "flush" })
        foreach (bool asynchronous in new[] { false, true })
        for (int cleanup = 0; cleanup < 4; cleanup++)
            yield return [stage, asynchronous, cleanup];
    }

    public static IEnumerable<object[]> CancellationFailures()
    {
        foreach (string stage in new[] { "write", "flush" })
        foreach (string kind in new[] { "ordinary", "nested", "oce-nested" })
        foreach (bool asynchronous in new[] { false, true })
        for (int cleanup = 0; cleanup < 4; cleanup++)
            yield return [stage, kind, asynchronous, cleanup];
    }

    public static IEnumerable<object[]> ClosingGates()
    {
        foreach (bool lateCancellation in new[] { false, true })
        for (int cleanup = 0; cleanup < 4; cleanup++)
            yield return [lateCancellation, cleanup];
    }

    [Theory(Timeout = 120_000)]
    [MemberData(nameof(FramedFailures))]
    public async Task FramedAuthenticationFailure_FourCleanupCombinations_PreservesRealRejection(
        string wire, bool asynchronous, int cleanup)
    {
        OwnerFixture fixture = new(wire, "read", asynchronous, cleanup);
        try
        {
            fixture.Start();
            Task<AuthenticatedControlSession> original = await fixture.Returned.Task.WaitAsync(Guard);
            if (asynchronous)
            {
                await fixture.Ssl.BoundaryEntered.Task.WaitAsync(Guard);
                Assert.False(original.IsCompleted);
                fixture.AssertNotClosed();
                fixture.Ssl.ReleaseBoundary();
            }

            Exception? observed = await ObserveAsync(original).WaitAsync(Guard);
            fixture.AssertWireConsumed();
            fixture.AssertClosed();
            AssertRealRejection(original, observed, RejectionFor(wire), fixture.Errors);
            Assert.Same(observed, await ObserveAsync(original).WaitAsync(Guard));
        }
        finally
        {
            await fixture.FinishAsync();
        }
    }

    [Theory(Timeout = 120_000)]
    [MemberData(nameof(InjectedFailures))]
    public async Task UntranslatedIoBoundary_SyncThrowOrPendingFailure_PreservesOriginalTreeExactlyOnce(
        string stage, string kind, bool asynchronous, int cleanup)
    {
        Exception primary = CreatePrimary(kind, CancellationToken.None);
        OwnerFixture fixture = new("rejected", stage, asynchronous, cleanup, primary,
            cleanupKind: kind);
        try
        {
            fixture.Start();
            Task<AuthenticatedControlSession> original = await fixture.Returned.Task.WaitAsync(Guard);
            if (asynchronous)
            {
                await fixture.Ssl.BoundaryEntered.Task.WaitAsync(Guard);
                Assert.False(original.IsCompleted);
                Assert.False(fixture.Ssl.PendingBoundary.IsCompleted);
                fixture.AssertNotClosed();
                fixture.Ssl.ReleaseBoundary();
            }

            Exception? observed = await ObserveAsync(original).WaitAsync(Guard);
            fixture.AssertBoundaryReached(stage);
            fixture.AssertClosed();
            AssertOriginalFailure(original, observed, primary, fixture.Errors);
            Assert.Same(observed, await ObserveAsync(original).WaitAsync(Guard));
        }
        finally
        {
            await fixture.FinishAsync();
        }
    }

    [Theory(Timeout = 120_000)]
    [MemberData(nameof(IoFailures))]
    public async Task RealHelloWriteOrFlushIOException_IsTranslatedBeforeCombiningCleanup(
        string stage, bool asynchronous, int cleanup)
    {
        // IO 有意走真实翻译；不能把原 IOException 的实例保持当成这里的契约。
        IOException io = new("受控 hello IO 故障，不得直接交给调用方。");
        OwnerFixture fixture = new("rejected", stage, asynchronous, cleanup, io);
        try
        {
            fixture.Start();
            Task<AuthenticatedControlSession> original = await fixture.Returned.Task.WaitAsync(Guard);
            if (asynchronous)
            {
                await fixture.Ssl.BoundaryEntered.Task.WaitAsync(Guard);
                Assert.False(original.IsCompleted);
                fixture.Ssl.ReleaseBoundary();
            }
            Exception? observed = await ObserveAsync(original).WaitAsync(Guard);
            fixture.AssertBoundaryReached(stage);
            fixture.AssertClosed();
            AssertRealRejection(original, observed, "client-connection-closed", fixture.Errors);
        }
        finally
        {
            await fixture.FinishAsync();
        }
    }

    [Theory(Timeout = 120_000)]
    [MemberData(nameof(CancellationFailures))]
    public async Task CallerCancelsBeforePrimary_CleanupKeepsCatchEntryPrimary_WithoutCleanupKeepsCancellationPolicy(
        string stage, string kind, bool asynchronous, int cleanup)
    {
        using CancellationTokenSource caller = new();
        Exception primary = CreatePrimary(kind, caller.Token);
        OwnerFixture fixture = new("rejected", stage, asynchronous, cleanup, primary,
            caller.Token, cleanupKind: "canceled");
        // 同步边界先触发 caller，再直接 throw；异步边界由测试在原 Task 挂起时先取消再交付故障。
        if (!asynchronous) fixture.Ssl.BeforeSynchronousFailure = caller.Cancel;
        try
        {
            fixture.Start();
            Task<AuthenticatedControlSession> original = await fixture.Returned.Task.WaitAsync(Guard);
            if (asynchronous)
            {
                await fixture.Ssl.BoundaryEntered.Task.WaitAsync(Guard);
                Assert.False(caller.IsCancellationRequested);
                Assert.False(original.IsCompleted);
                caller.Cancel();
                Assert.False(fixture.Ssl.PendingBoundary.IsCompleted);
                Assert.False(original.IsCompleted);
                fixture.AssertNotClosed();
                fixture.Ssl.ReleaseBoundary();
            }

            Exception? observed = await ObserveAsync(original).WaitAsync(Guard);
            Assert.True(caller.IsCancellationRequested);
            fixture.AssertBoundaryReached(stage);
            fixture.AssertClosed();
            if (cleanup == 0 && kind == "ordinary")
            {
                OperationCanceledException canceled = Assert.IsType<OperationCanceledException>(observed);
                Assert.Equal(caller.Token, canceled.CancellationToken);
                Assert.Null(canceled.InnerException);
                Assert.Equal(TaskStatus.Canceled, original.Status);
            }
            else
            {
                // 写/flush 没有读路径的 GetRemaining 再翻译；这里确实是到达外层 catch 的原异常。
                // 原 OCE 包装聚合、且没有新增清理错时允许 async builder 的 Canceled 状态。
                AssertOriginalFailure(original, observed, primary, fixture.Errors);
            }
        }
        finally
        {
            await fixture.FinishAsync();
        }
    }

    [Theory(Timeout = 120_000)]
    [MemberData(nameof(ClosingGates))]
    public async Task OriginalTaskWaitsForBothCloseGates_KeyAlreadyZero_LateCancellationCannotRewritePrimary(
        bool lateCancellation, int cleanup)
    {
        using CancellationTokenSource caller = new();
        OwnerFixture fixture = new("rejected", "read", true, cleanup,
            caller: caller.Token, blockClose: true);
        try
        {
            fixture.Start();
            Task<AuthenticatedControlSession> original = await fixture.Returned.Task.WaitAsync(Guard);
            await fixture.Ssl.BoundaryEntered.Task.WaitAsync(Guard);
            byte[] privateKey = ReadSuspendedPrivateKey(original);
            Assert.NotSame(fixture.Key, privateKey);
            Assert.Equal(fixture.Key, privateKey);
            Assert.Contains(privateKey, value => value != 0);
            fixture.Ssl.ReleaseBoundary();

            await fixture.TcpClose.Entered.Task.WaitAsync(Guard);
            fixture.AssertWireConsumed();
            Assert.False(original.IsCompleted);
            Assert.False(fixture.SslClose.Entered.Task.IsCompleted);
            Assert.Equal(1, fixture.TcpClose.Calls);
            Assert.Equal(0, fixture.SslClose.Calls);
            Assert.All(privateKey, value => Assert.Equal((byte)0, value));
            fixture.AssertCallerKeyUnchanged();
            Assert.False(caller.IsCancellationRequested);
            if (lateCancellation) caller.Cancel();
            Assert.False(original.IsCompleted);

            fixture.TcpClose.Release();
            await fixture.SslClose.Entered.Task.WaitAsync(Guard);
            Assert.False(original.IsCompleted);
            Assert.All(privateKey, value => Assert.Equal((byte)0, value));
            Assert.Equal(new[] { "tcp", "ssl" }, fixture.CloseOrder.ToArray());
            fixture.AssertCloseOffCallingThread();
            fixture.SslClose.Release();

            Exception? observed = await ObserveAsync(original).WaitAsync(Guard);
            fixture.AssertClosed();
            Assert.Equal(lateCancellation, caller.IsCancellationRequested);
            // 零清理错也必须保留进入外层 catch 时的未取消快照，不能事后改成 OCE。
            AssertRealRejection(original, observed, RemoteRejection, fixture.Errors);
        }
        finally
        {
            await fixture.FinishAsync();
        }
    }

    [Theory(Timeout = 120_000)]
    [InlineData(0)]
    [InlineData(3)]
    public async Task FullySynchronousAuthenticationRejection_ReturnsOriginalTaskWithoutBlockingDedicatedCaller(
        int cleanup)
    {
        OwnerFixture fixture = new("rejected", "read", false, cleanup, blockClose: true);
        try
        {
            fixture.Start();
            await fixture.TcpClose.Entered.Task.WaitAsync(Guard);
            fixture.AssertWireConsumed();
            Assert.False(fixture.SslClose.Entered.Task.IsCompleted);
            Assert.NotEqual(fixture.CallerThreadId, fixture.TcpClose.ThreadId);
            // 旧 finally 的 Dispose 在专用调用线程等关闭；不是用 Task.Run 解包代理假装已返回。
            Task<AuthenticatedControlSession> original = await fixture.Returned.Task.WaitAsync(Guard);
            Assert.False(original.IsCompleted);
            fixture.TcpClose.Release();
            await fixture.SslClose.Entered.Task.WaitAsync(Guard);
            Assert.False(original.IsCompleted);
            fixture.AssertCloseOffCallingThread();
            fixture.SslClose.Release();
            Exception? observed = await ObserveAsync(original).WaitAsync(Guard);
            fixture.AssertClosed();
            AssertRealRejection(original, observed, RemoteRejection, fixture.Errors);
        }
        finally
        {
            await fixture.FinishAsync();
        }
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task PublicDisposeAlreadyReportedCleanup_OwnerStillConsumesStableDiagnosticsOnce(int cleanup)
    {
        OwnerFixture fixture = new("rejected", "read", true, cleanup, pauseAtPayload: true);
        Task? publicDispose = null;
        try
        {
            fixture.Start();
            Task<AuthenticatedControlSession> original = await fixture.Returned.Task.WaitAsync(Guard);
            await fixture.Ssl.BoundaryEntered.Task.WaitAsync(Guard);
            Assert.Equal(TransportConstants.LengthPrefixBytes, fixture.Ssl.BytesRead);
            Assert.False(original.IsCompleted);
            // 已经发起的最后一笔 payload 读不合作，关闭后仍交付帧；不再从已关闭连接取 Stream。
            publicDispose = Task.Run(fixture.Connection.Dispose);
            Exception? reported = await ObserveAsync(publicDispose).WaitAsync(Guard);
            if (fixture.Errors.Length == 1) Assert.Same(fixture.Errors[0], reported);
            else AssertCleanupGroup(reported, fixture.Errors);
            IReadOnlyList<Exception> snapshot = fixture.Connection.CleanupErrors;
            AssertDiagnosticSnapshot(snapshot, fixture.Errors);
            Assert.Null(Record.Exception(fixture.Connection.Dispose));
            Assert.False(original.IsCompleted);
            fixture.AssertClosed();

            fixture.Ssl.ReleaseBoundary();
            Exception? observed = await ObserveAsync(original).WaitAsync(Guard);
            fixture.AssertWireConsumed();
            fixture.AssertClosed();
            AssertDiagnosticSnapshot(snapshot, fixture.Errors);
            AssertDiagnosticSnapshot(fixture.Connection.CleanupErrors, fixture.Errors);
            AssertRealRejection(original, observed, RemoteRejection, fixture.Errors);
            Assert.Same(observed, await ObserveAsync(original).WaitAsync(Guard));
            Assert.Null(Record.Exception(fixture.Connection.Dispose));
            fixture.AssertClosed();
        }
        finally
        {
            fixture.ReleaseAll();
            try
            {
                if (publicDispose is not null) await ObserveAsync(publicDispose).WaitAsync(Guard);
            }
            finally
            {
                await fixture.FinishAsync();
            }
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task ConcurrentCallsOnSameConnector_IsolateCleanupGatesOriginalTasksAndDiagnostics()
    {
        // 第一条双错误且阻塞，其余三条分别无错、仅 TCP 错、仅 SSL 错。
        int[] masks = [3, 0, 1, 2];
        OwnerFixture[] fixtures = masks.Select((mask, i) => new OwnerFixture(
            i == 3 ? "malformed" : "rejected", "read", true, mask, blockClose: i == 0)).ToArray();
        Dictionary<Guid, OwnerFixture> byTarget = fixtures.ToDictionary(f => f.Target.DeviceId);
        ControlClientConnector connector = new((target, budget, clock, token) =>
            byTarget[target.DeviceId].ConnectTls(target, budget, clock, token));
        try
        {
            foreach (OwnerFixture fixture in fixtures) fixture.Start(connector);
            Task<AuthenticatedControlSession>[] originals = await Task.WhenAll(
                fixtures.Select(f => f.Returned.Task)).WaitAsync(Guard);
            await Task.WhenAll(fixtures.Select(f => f.Ssl.BoundaryEntered.Task)).WaitAsync(Guard);
            byte[][] privateKeys = originals.Select(ReadSuspendedPrivateKey).ToArray();
            Assert.Equal(4, privateKeys.Distinct(ReferenceEqualityComparer.Instance).Count());
            fixtures[0].Ssl.ReleaseBoundary();
            await fixtures[0].TcpClose.Entered.Task.WaitAsync(Guard);
            Assert.False(originals[0].IsCompleted);
            for (int i = 1; i < fixtures.Length; i++) fixtures[i].Ssl.ReleaseBoundary();
            Exception?[] observed = await Task.WhenAll(originals.Skip(1).Select(ObserveAsync)).WaitAsync(Guard);
            Assert.False(originals[0].IsCompleted);
            Assert.All(privateKeys, key => Assert.All(key, value => Assert.Equal((byte)0, value)));
            for (int i = 1; i < fixtures.Length; i++)
            {
                fixtures[i].AssertWireConsumed();
                fixtures[i].AssertClosed();
            }

            fixtures[0].TcpClose.Release();
            await fixtures[0].SslClose.Entered.Task.WaitAsync(Guard);
            Assert.False(originals[0].IsCompleted);
            fixtures[0].SslClose.Release();
            Exception? first = await ObserveAsync(originals[0]).WaitAsync(Guard);
            fixtures[0].AssertClosed();
            // 用 Assert.All 保留所有独立调用的诊断差异，不因第一条错误提前漏掉后面的核对。
            Assert.All(Enumerable.Range(0, fixtures.Length), i =>
                AssertRealRejection(originals[i], i == 0 ? first : observed[i - 1],
                    i == 3 ? AuthChallengeFrame.RejectMalformedJson : RemoteRejection, fixtures[i].Errors));
            for (int i = 1; i < fixtures.Length; i++)
                Assert.Same(observed[i - 1], await ObserveAsync(originals[i]).WaitAsync(Guard));
        }
        finally
        {
            foreach (OwnerFixture fixture in fixtures) fixture.ReleaseAll();
            await Task.WhenAll(fixtures.Select(f => f.FinishAsync())).WaitAsync(Guard + Guard);
        }
    }

    private static string RejectionFor(string wire) => wire switch
    {
        "rejected" or "reply-rejected" => RemoteRejection,
        "malformed" => AuthChallengeFrame.RejectMalformedJson,
        "zero-length" => FrameReader.RejectZeroLength,
        "truncated" => "client-connection-closed",
        _ => throw new ArgumentOutOfRangeException(nameof(wire)),
    };

    private static Exception CreatePrimary(string kind, CancellationToken caller)
    {
        AggregateException nested = new("受控原始聚合主错。",
            new InvalidOperationException("主错第一项。"),
            new AggregateException("主错嵌套项。", new OperationCanceledException("内层操作取消。")));
        return kind switch
        {
            "ordinary" => new InvalidOperationException("受控未翻译主错。"),
            "nested" => nested,
            "wrapped" => new Exception("受控安全包装，不触发 IO/密码学翻译。", nested),
            "oce-nested" => new OperationCanceledException("受控 OCE 原树。", nested, caller),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static Exception CreateCleanup(string resource, string kind) => kind switch
    {
        "nested" => new AggregateException($"受控 {resource} 原始聚合清理错。",
            new IOException($"{resource} 内层第一项。"),
            new AggregateException(new OperationCanceledException($"{resource} 内层取消。"))),
        "wrapped" or "canceled" => new OperationCanceledException($"受控 {resource} 释放取消。",
            new CancellationToken(canceled: true)),
        _ => new IOException($"受控 {resource} 清理错。"),
    };

    private static Exception ExtractPrimary(Task original, Exception? observed, Exception[] cleanup)
    {
        Assert.NotNull(observed);
        if (cleanup.Length == 0) return observed;
        Assert.Equal(TaskStatus.Faulted, original.Status);
        Assert.False(original.IsCanceled);
        AggregateException outer = Assert.IsType<AggregateException>(observed);
        Assert.Equal(2, outer.InnerExceptions.Count);
        AssertCleanupGroup(outer.InnerExceptions[1], cleanup);
        return outer.InnerExceptions[0];
    }

    private static void AssertRealRejection(Task original, Exception? observed, string rejection, Exception[] cleanup)
    {
        ControlClientAuthenticationException primary = Assert.IsType<ControlClientAuthenticationException>(
            ExtractPrimary(original, observed, cleanup));
        Assert.Equal(rejection, primary.Rejection);
        Assert.Null(primary.InnerException);
        Assert.Equal(TaskStatus.Faulted, original.Status);
    }

    private static void AssertOriginalFailure(Task original, Exception? observed, Exception primary, Exception[] cleanup)
    {
        AssertSameTree(primary, ExtractPrimary(original, observed, cleanup));
        Assert.Equal(cleanup.Length == 0 && primary is OperationCanceledException
            ? TaskStatus.Canceled : TaskStatus.Faulted, original.Status);
    }

    private static void AssertCleanupGroup(Exception? observed, Exception[] expected)
    {
        AggregateException group = Assert.IsType<AggregateException>(observed);
        AssertDiagnosticSnapshot(group.InnerExceptions, expected);
    }

    private static void AssertDiagnosticSnapshot(IReadOnlyList<Exception> observed, Exception[] expected)
    {
        Assert.Equal(expected.Length, observed.Count);
        for (int i = 0; i < expected.Length; i++) AssertSameTree(expected[i], observed[i]);
    }

    private static void AssertSameTree(Exception expected, Exception actual)
    {
        Assert.Same(expected, actual);
        if (expected is OperationCanceledException canceled)
            Assert.Equal(canceled.CancellationToken,
                Assert.IsAssignableFrom<OperationCanceledException>(actual).CancellationToken);
        // 不 Flatten，不剥离包装；每层、每位置均保留同一实例。
        if (expected is AggregateException aggregate)
            AssertDiagnosticSnapshot(Assert.IsType<AggregateException>(actual).InnerExceptions,
                aggregate.InnerExceptions.ToArray());
        else if (expected.InnerException is not null)
            AssertSameTree(expected.InnerException, Assert.IsAssignableFrom<Exception>(actual.InnerException));
        else
            Assert.Null(actual.InnerException);
    }

    private static byte[] ReadSuspendedPrivateKey(Task<AuthenticatedControlSession> original)
    {
        Assert.False(original.IsCompleted);
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public
            | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        FieldInfo? stateMachineField = null;
        // 只读原 Task 的指定 StateMachine 字段，不遍历 continuation 图，也不写生产状态。
        for (Type? type = original.GetType(); type is not null; type = type.BaseType)
        {
            stateMachineField = type.GetField("StateMachine", fields);
            if (stateMachineField is null) continue;
            Assert.True(type.Name == "AsyncStateMachineBox`1"
                && type.DeclaringType == typeof(AsyncTaskMethodBuilder<>),
                $"不支持的状态机承载类型：{type}。");
            break;
        }
        Assert.NotNull(stateMachineField);
        object? machine = stateMachineField.GetValue(original);
        Assert.NotNull(machine);
        Type? expected = typeof(ControlClientConnector)
            .GetMethod(nameof(ControlClientConnector.ConnectAndAuthenticateAsync))!
            .GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
        Assert.Equal(expected, machine.GetType());
        FieldInfo local = Assert.Single(machine.GetType().GetFields(fields),
            field => field.Name.StartsWith("<key>5__", StringComparison.Ordinal));
        byte[] key = Assert.IsType<byte[]>(local.GetValue(machine));
        Assert.Equal(AccessSecret.AccessKeyByteLength, key.Length);
        return key;
    }

    private static async Task<Exception?> ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); return null; }
        catch (Exception error) { return error; }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static byte[] Frame(byte[] payload)
    {
        byte[] framed = new byte[TransportConstants.LengthPrefixBytes + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(framed, (uint)payload.Length);
        payload.CopyTo(framed, TransportConstants.LengthPrefixBytes);
        return framed;
    }

    private sealed class OwnerFixture
    {
        private readonly string _wire;
        private readonly CancellationToken _caller;
        private readonly byte[] _keyBefore;
        private readonly TransportTimeouts _budget = new(TransportTimeouts.Maximum, TransportTimeouts.Maximum,
            TransportTimeouts.Maximum, TransportTimeouts.Maximum, TransportTimeouts.Maximum, TransportTimeouts.Maximum);
        private readonly ControlClientAuthOptions _options = new()
        {
            MachineWindow = TransportTimeouts.Maximum,
            ApprovalWindow = TransportTimeouts.Maximum,
        };
        private readonly TimeProvider _clock = TimeProvider.System;
        private readonly Guid _clientId = Guid.NewGuid();
        private readonly ProbeTcpClient _tcp;
        private Thread? _thread;
        private int _connectCalls;
        internal byte[] Key { get; } = Convert.FromHexString("102132435465768798A9BACBDCEDFE0F");
        internal ConnectionTarget Target { get; }
        internal TlsConnection Connection { get; }
        internal ProbeSslStream Ssl { get; }
        internal CloseProbe TcpClose { get; }
        internal CloseProbe SslClose { get; }
        internal Exception[] Errors { get; }
        internal ConcurrentQueue<string> CloseOrder { get; } = new();
        internal TaskCompletionSource<Task<AuthenticatedControlSession>> Returned { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int CallerThreadId => _thread!.ManagedThreadId;

        internal OwnerFixture(string wire, string stage, bool asynchronous, int cleanup,
            Exception? primary = null, CancellationToken caller = default, bool blockClose = false,
            bool pauseAtPayload = false, string cleanupKind = "ordinary")
        {
            _wire = wire;
            _caller = caller;
            _keyBefore = Key.ToArray();
            byte[] pin = Convert.FromHexString("808182838485868788898A8B8C8D8E8F909192939495969798999A9B9C9D9E9F");
            Assert.True(ConnectionTarget.TryCreate(Guid.NewGuid(), IPAddress.Loopback, 12345,
                Convert.ToHexString(pin), out ConnectionTarget? target));
            Target = target!;
            Assert.True(ConnectionIdentity.TryCreate(Target, pin, out ConnectionIdentity? identity));
            Exception? tcpError = (cleanup & 1) != 0 ? CreateCleanup("TCP", cleanupKind) : null;
            Exception? sslError = (cleanup & 2) != 0 ? CreateCleanup("SSL", cleanupKind) : null;
            Errors = new[] { tcpError, sslError }.OfType<Exception>().ToArray();
            TcpClose = new("tcp", CloseOrder, tcpError, blockClose);
            SslClose = new("ssl", CloseOrder, sslError, blockClose);
            byte[] rejection = AuthenticationFailedFrame.Serialize();
            Assert.True(AuthenticationFailedFrame.TryParse(rejection, out _));
            byte[] input = wire switch
            {
                "rejected" => Frame(rejection),
                "reply-rejected" => [.. Frame(new AuthChallengeFrame(Guid.NewGuid(), Target.DeviceId,
                    new byte[AuthProtocol.NonceByteLength], pin, 86_400_000).Serialize()), .. Frame(rejection)],
                "malformed" => Frame("{"u8.ToArray()),
                "zero-length" => new byte[TransportConstants.LengthPrefixBytes],
                "truncated" => Frame(rejection)[..^1],
                _ => throw new ArgumentOutOfRangeException(nameof(wire)),
            };
            Ssl = new(input, stage, asynchronous, primary, SslClose, pauseAtPayload);
            _tcp = new(TcpClose);
            Connection = new TlsConnection(identity!, _tcp, Ssl);
        }

        internal Task<TlsConnection> ConnectTls(ConnectionTarget target, TransportTimeouts budget,
            TimeProvider clock, CancellationToken token)
        {
            Interlocked.Increment(ref _connectCalls);
            Assert.Same(Target, target);
            Assert.Same(_budget, budget);
            Assert.Same(_clock, clock);
            Assert.Equal(_caller, token);
            return Task.FromResult(Connection);
        }

        internal void Start(ControlClientConnector? connector = null)
        {
            connector ??= new ControlClientConnector(ConnectTls);
            _thread = new Thread(() =>
            {
                try
                {
                    Returned.TrySetResult(connector.ConnectAndAuthenticateAsync(Target, _clientId,
                        "受控所有权测试客户端", Key, SessionPermission.Control, _options, _budget, _clock, _caller));
                }
                catch (Exception error) { Returned.TrySetException(error); }
            }) { IsBackground = true };
            _thread.Start();
        }

        internal void AssertCallerKeyUnchanged() => Assert.Equal(_keyBefore, Key);

        internal void AssertNotClosed()
        {
            Assert.False(Connection.IsCloseRequested);
            Assert.Equal(0, TcpClose.Calls);
            Assert.Equal(0, SslClose.Calls);
            Assert.Empty(CloseOrder);
        }

        internal void AssertClosed()
        {
            Assert.Equal(1, Volatile.Read(ref _connectCalls));
            Assert.True(Connection.IsCloseRequested);
            Assert.Equal(1, TcpClose.Calls);
            Assert.Equal(1, SslClose.Calls);
            Assert.Equal(new[] { "tcp", "ssl" }, CloseOrder.ToArray());
            AssertDiagnosticSnapshot(Connection.CleanupErrors, Errors);
            AssertCallerKeyUnchanged();
        }

        internal void AssertCloseOffCallingThread()
        {
            Assert.NotEqual(CallerThreadId, TcpClose.ThreadId);
            Assert.NotEqual(CallerThreadId, SslClose.ThreadId);
            Assert.True(TcpClose.OnThreadPool);
            Assert.True(SslClose.OnThreadPool);
        }

        internal void AssertBoundaryReached(string stage)
        {
            Assert.True(Ssl.BoundaryEntered.Task.IsCompletedSuccessfully);
            Assert.Equal(1, Ssl.BoundaryCalls);
            Assert.Single(Ssl.Writes);
            AssertHello(Ssl.Writes[0]);
            Assert.Equal(stage == "write" ? 0 : 1, Ssl.FlushCalls);
            Assert.Equal(stage == "read" ? 1 : 0, Ssl.ReadCalls);
        }

        internal void AssertWireConsumed()
        {
            Assert.Equal(Ssl.InputLength, Ssl.BytesRead);
            Assert.Equal(_wire == "reply-rejected" ? 4 : _wire == "zero-length" ? 1
                : _wire == "truncated" ? 3 : 2, Ssl.ReadCalls);
            Assert.Equal(_wire == "reply-rejected" ? 2 : 1, Ssl.Writes.Count);
            Assert.Equal(Ssl.Writes.Count, Ssl.FlushCalls);
            AssertHello(Ssl.Writes[0]);
            if (_wire == "reply-rejected")
            {
                byte[] response = Ssl.Writes[1];
                Assert.Equal((uint)(response.Length - TransportConstants.LengthPrefixBytes),
                    BinaryPrimitives.ReadUInt32BigEndian(response));
                Assert.True(AuthResponseFrame.TryParse(response.AsSpan(TransportConstants.LengthPrefixBytes),
                    out AuthResponseFrame? parsed, out _));
                Assert.Equal(_clientId, parsed!.ClientDeviceId);
                Assert.Equal(SessionPermission.Control, parsed.RequestedPermission);
            }
        }

        private static void AssertHello(byte[] frame)
        {
            Assert.Equal((uint)(frame.Length - TransportConstants.LengthPrefixBytes),
                BinaryPrimitives.ReadUInt32BigEndian(frame));
            Assert.True(HelloFrame.TryParse(frame.AsSpan(TransportConstants.LengthPrefixBytes), out _));
        }

        internal void ReleaseAll()
        {
            Ssl.ReleaseBoundary();
            TcpClose.Release();
            SslClose.Release();
        }

        internal async Task FinishAsync()
        {
            // 每个测试 finally 都先放所有闸，再 join 原 IO/公开 Task/专用线程；超时不被 Observe 吞掉。
            ReleaseAll();
            try
            {
                await ObserveAsync(Ssl.PendingBoundary).WaitAsync(Guard);
                if (_thread is not null)
                {
                    Task<AuthenticatedControlSession> original = await Returned.Task.WaitAsync(Guard);
                    await ObserveAsync(original).WaitAsync(Guard);
                    if (original.IsCompletedSuccessfully) (await original).Dispose();
                    Assert.True(original.IsCompleted);
                }
            }
            finally
            {
                try
                {
                    if (_thread is not null) Assert.True(_thread.Join(Guard), "专用公开入口调用线程未结束。");
                    await Connection.CloseAsync().WaitAsync(Guard);
                    AssertCallerKeyUnchanged();
                }
                finally
                {
                    // 只在断言/生产任务观察之后兜底释放实际资源，绝不补做生产计数或制造清理证据。
                    try { _tcp.DisposeForCleanup(); }
                    finally
                    {
                        Ssl.DisposeForCleanup();
                        TcpClose.Dispose();
                        SslClose.Dispose();
                    }
                }
            }
        }
    }

    private sealed class CloseProbe : IDisposable
    {
        private readonly string _name;
        private readonly ConcurrentQueue<string> _order;
        private readonly Exception? _error;
        private readonly ManualResetEventSlim _release;
        private int _calls;
        internal TaskCompletionSource Entered { get; } = Signal();
        internal int Calls => Volatile.Read(ref _calls);
        internal int ThreadId { get; private set; }
        internal bool OnThreadPool { get; private set; }

        internal CloseProbe(string name, ConcurrentQueue<string> order, Exception? error, bool blocked)
        {
            _name = name;
            _order = order;
            _error = error;
            _release = new(initialState: !blocked);
        }

        internal void Run()
        {
            ThreadId = Environment.CurrentManagedThreadId;
            OnThreadPool = Thread.CurrentThread.IsThreadPoolThread;
            Interlocked.Increment(ref _calls);
            _order.Enqueue(_name);
            Entered.TrySetResult();
            if (!_release.Wait(Guard + Guard + Guard)) throw new TimeoutException("测试释放闸门未放行。");
            if (_error is not null) throw _error;
        }

        internal void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
    }

    private sealed class ProbeTcpClient(CloseProbe close) : TcpClient(AddressFamily.InterNetwork)
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing) close.Run();
            else base.Dispose(false);
        }
        internal void DisposeForCleanup() => base.Dispose(true);
    }

    private sealed class ProbeSslStream : SslStream
    {
        private readonly byte[] _input;
        private readonly string _stage;
        private readonly bool _asynchronous;
        private readonly Exception? _primary;
        private readonly CloseProbe _close;
        private readonly bool _pauseAtPayload;
        private readonly TaskCompletionSource _pending = Signal();
        private int _offset;
        private int _boundaryCalls;
        internal TaskCompletionSource BoundaryEntered { get; } = Signal();
        internal Task PendingBoundary => _pending.Task;
        internal Action? BeforeSynchronousFailure { get; set; }
        internal int BoundaryCalls => Volatile.Read(ref _boundaryCalls);
        internal int ReadCalls { get; private set; }
        internal int FlushCalls { get; private set; }
        internal List<byte[]> Writes { get; } = new();
        internal int BytesRead => _offset;
        internal int InputLength => _input.Length;

        internal ProbeSslStream(byte[] input, string stage, bool asynchronous, Exception? primary,
            CloseProbe close, bool pauseAtPayload) : base(new MemoryStream(), leaveInnerStreamOpen: false)
        {
            _input = input;
            _stage = stage;
            _asynchronous = asynchronous;
            _primary = primary;
            _close = close;
            _pauseAtPayload = pauseAtPayload;
        }

        private ValueTask Boundary(string stage)
        {
            if (stage != _stage || BoundaryCalls != 0
                || (stage == "read" && _pauseAtPayload && _offset == 0)) return ValueTask.CompletedTask;
            Interlocked.Increment(ref _boundaryCalls);
            BoundaryEntered.TrySetResult();
            if (_asynchronous) return new ValueTask(_pending.Task);
            BeforeSynchronousFailure?.Invoke();
            if (_primary is not null) throw _primary;
            return ValueTask.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes.Add(buffer.ToArray());
            return Boundary("write");
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCalls++;
            return Boundary("flush").AsTask();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            ValueTask boundary = Boundary("read");
            return boundary.IsCompletedSuccessfully ? ValueTask.FromResult(CopyInput(buffer))
                : ReadAfterBoundaryAsync(boundary, buffer);
        }

        private async ValueTask<int> ReadAfterBoundaryAsync(ValueTask boundary, Memory<byte> buffer)
        {
            // 故意不注册 caller token；测试控制原操作何时真正完成，取消不能抢先交付另一种故障。
            await boundary.ConfigureAwait(false);
            return CopyInput(buffer);
        }

        private int CopyInput(Memory<byte> buffer)
        {
            int count = Math.Min(buffer.Length, _input.Length - _offset);
            _input.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        internal void ReleaseBoundary()
        {
            if (_asynchronous && _primary is not null) _pending.TrySetException(_primary);
            else _pending.TrySetResult();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _close.Run();
            else base.Dispose(false);
        }
        internal void DisposeForCleanup() => base.Dispose(true);
    }
}
