using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Runtime.ExceptionServices;
using System.Security.Authentication;
using System.Text;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

// 不创建网络连接；真实 Router、AcceptedConnection 和 CloseHandle 共同管理受控 SSL/socket 资源。
public sealed class FirstFrameRouterCloseSupervisionTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BlockingGuard = TimeSpan.FromSeconds(30);
    private static readonly TransportTimeouts Timeouts = new(
        TransportTimeouts.Maximum, TransportTimeouts.Maximum, TransportTimeouts.Maximum,
        TransportTimeouts.Maximum, TransportTimeouts.Maximum, TransportTimeouts.Maximum);

    [Theory(Timeout = 60_000)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Host_Cancellation_Closes_Before_Uncooperative_First_Read_Completes(int heldRead)
    {
        await using Scenario scenario = new(heldRead: heldRead);
        Task run = scenario.Start();
        await scenario.Stream.ReadEntered.Task.WaitAsync(Guard);
        Task<int> originalRead = Assert.IsAssignableFrom<Task<int>>(scenario.Stream.HeldReadOperation);
        Assert.Equal(heldRead, scenario.Stream.ReadCalls);
        Assert.Equal(heldRead == 1 ? 0 : TransportConstants.LengthPrefixBytes, scenario.Stream.BytesRead);
        Assert.False(originalRead.IsCompleted);

        await scenario.CancelHostAsync().WaitAsync(Guard);
        await AssertSignalAsync(scenario.Stream.DisposeExited.Task);
        Task close = scenario.Connection.CloseAsync();
        await close.WaitAsync(Guard);

        Assert.True(scenario.Stream.HeldReadToken.IsCancellationRequested);
        Assert.True(scenario.Socket.DisposeSucceeded);
        Assert.True(scenario.Stream.DisposeSucceeded);
        Assert.False(originalRead.IsCompleted);
        AssertRouterPending(scenario, run);
        AssertNoAuthentication(scenario);

        scenario.Stream.ReleaseRead();
        await originalRead.WaitAsync(Guard);
        await AssertHostCancelledAsync(scenario, run);
        Assert.Equal(heldRead, scenario.Stream.ReadCalls);
        AssertJoined(scenario);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_Cancellation_Followed_By_Original_Read_Network_Failure_Keeps_Existing_Classification(bool disposed)
    {
        Exception readFailure = disposed
            ? new ObjectDisposedException("受控首帧读")
            : new IOException("关闭后原首帧读报告 IO 失败。");
        await using Scenario scenario = new(heldRead: 1, readFailure: readFailure);
        Task run = scenario.Start();
        await scenario.Stream.ReadEntered.Task.WaitAsync(Guard);
        Task<int> originalRead = Assert.IsAssignableFrom<Task<int>>(scenario.Stream.HeldReadOperation);

        await scenario.CancelHostAsync().WaitAsync(Guard);
        await AssertSignalAsync(scenario.Stream.DisposeExited.Task);
        Task close = scenario.Connection.CloseAsync();
        await close.WaitAsync(Guard);
        Assert.True(scenario.Stream.HeldReadToken.IsCancellationRequested);
        Assert.False(originalRead.IsCompleted);
        AssertRouterPending(scenario, run);

        // 保持原首帧 IO/ODE catch 分类；Host 取消不把这种原故障改写成 OCE。
        scenario.Stream.ReleaseRead();
        Assert.Same(readFailure, await CaptureAsync(originalRead));
        await run.WaitAsync(Guard);
        Assert.True(run.IsCompletedSuccessfully);
        Assert.Equal("channel-closed-or-invalid", scenario.Router.Rejection);
        Assert.Same(originalRead, Assert.Single(scenario.Stream.Operations));
        Assert.True(originalRead.IsFaulted);
        Assert.Equal(1, scenario.Stream.ReadCalls);
        Assert.True(close.IsCompletedSuccessfully);
        Assert.True(scenario.Socket.DisposeSucceeded);
        Assert.True(scenario.Stream.DisposeSucceeded);
        Assert.Equal(1, scenario.Socket.DisposeCalls);
        Assert.Equal(1, scenario.Stream.DisposeCalls);
        Assert.Equal(0, scenario.Clock.TimerCount);
        Assert.Empty(scenario.Closer.CleanupErrors);
        Assert.Empty(scenario.HostErrors.Snapshot);
        AssertNoAuthentication(scenario);
    }

    [Fact(Timeout = 60_000)]
    public async Task Synchronous_Read_Prefix_Stays_On_Caller_Thread_While_Host_Cancellation_Closes()
    {
        await using Scenario scenario = new(heldRead: 1, synchronousReadPrefix: true);
        // 只有测试调用端用一个固定 worker 承载同步阻塞；生产 Router 不得移动原 ReadAsync 调用。
        Task invocation = scenario.StartOnWorker();
        await scenario.Stream.ReadEntered.Task.WaitAsync(Guard);
        Task<int> originalRead = Assert.IsAssignableFrom<Task<int>>(scenario.Stream.HeldReadOperation);
        Assert.Equal(scenario.CallerThreadId, scenario.Stream.HeldReadThreadId);
        Assert.NotEqual(0, scenario.CallerThreadId);
        Assert.False(scenario.InvocationReturned.Task.IsCompleted);
        Assert.Null(scenario.Run);

        Task cancellation = scenario.CancelHostAsync();
        await cancellation.WaitAsync(Guard);
        await AssertSignalAsync(scenario.Stream.DisposeExited.Task);
        await scenario.Connection.CloseAsync().WaitAsync(Guard);

        Assert.True(cancellation.IsCompletedSuccessfully);
        Assert.True(scenario.Socket.DisposeSucceeded);
        Assert.True(scenario.Stream.DisposeSucceeded);
        Assert.False(invocation.IsCompleted);
        Assert.False(scenario.InvocationReturned.Task.IsCompleted);
        Assert.False(originalRead.IsCompleted);
        Assert.Equal(scenario.CallerThreadId, scenario.Stream.HeldReadThreadId);

        scenario.Stream.ReleaseSynchronousPrefix();
        await scenario.InvocationReturned.Task.WaitAsync(Guard);
        Task run = Assert.IsAssignableFrom<Task>(scenario.Run);
        AssertRouterPending(scenario, run);
        Assert.False(originalRead.IsCompleted);
        scenario.Stream.ReleaseRead();
        await originalRead.WaitAsync(Guard);
        await AssertHostCancelledAsync(scenario, run);
        await AssertHostCancelledAsync(scenario, invocation);
        AssertNoAuthentication(scenario);
        AssertJoined(scenario);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_Hello_Early_Return_Joins_Real_Close_And_Repeated_Close_Disposes_Once(bool holdSocket)
    {
        await using Scenario scenario = new(holdSocket: holdSocket, holdStreamDispose: !holdSocket);
        Task run = scenario.Start();
        await scenario.InvocationReturned.Task.WaitAsync(Guard);
        await AssertSignalAsync(holdSocket ? scenario.Socket.DisposeEntered.Task : scenario.Stream.DisposeEntered.Task);
        Task close = scenario.Connection.CloseAsync();

        // 首帧的两次读均同步完成，拒绝结果已经发布；此时唯一未放行的是实际 Dispose 闸门。
        Assert.Equal("channel-hello-invalid", scenario.Router.Rejection);
        Assert.Equal(2, scenario.Stream.ReadCalls);
        Assert.All(scenario.Stream.Operations, operation => Assert.True(operation.IsCompletedSuccessfully));
        Assert.False(close.IsCompleted);
        AssertRouterPending(scenario, run);
        Assert.False(holdSocket ? scenario.Socket.DisposeExited.Task.IsCompleted : scenario.Stream.DisposeExited.Task.IsCompleted);
        if (holdSocket) Assert.False(scenario.Stream.DisposeEntered.Task.IsCompleted);

        await scenario.CancelHostAsync().WaitAsync(Guard);
        await scenario.CancelHostAsync().WaitAsync(Guard);
        scenario.Closer.Dispose();
        Assert.Same(close, scenario.Connection.CloseAsync());
        Assert.Same(close, scenario.Closer.CloseAsync());
        AssertRouterPending(scenario, run);
        Assert.False(close.IsCompleted);
        Assert.Equal(1, scenario.Socket.DisposeCalls);
        Assert.Equal(holdSocket ? 0 : 1, scenario.Stream.DisposeCalls);

        scenario.Socket.Release();
        scenario.Stream.ReleaseDispose();
        await run.WaitAsync(Guard);
        await close.WaitAsync(Guard);
        Assert.True(run.IsCompletedSuccessfully);
        AssertNoAuthentication(scenario);
        AssertJoined(scenario);
        Assert.Same(close, scenario.Connection.CloseAsync());
    }

    [Fact(Timeout = 60_000)]
    public async Task Already_Cancelled_Host_Does_Not_Start_First_Read_But_Still_Joins_Close()
    {
        await using Scenario scenario = new(holdStreamDispose: true);
        await scenario.CancelHostAsync().WaitAsync(Guard);
        Task run = scenario.Start();
        await AssertSignalAsync(scenario.Stream.DisposeEntered.Task);
        Task close = scenario.Connection.CloseAsync();
        Assert.Equal(0, scenario.Stream.ReadCalls);
        Assert.False(scenario.Stream.ReadEntered.Task.IsCompleted);
        Assert.False(close.IsCompleted);
        AssertRouterPending(scenario, run);
        AssertNoAuthentication(scenario);

        scenario.Stream.ReleaseDispose();
        await AssertHostCancelledAsync(scenario, run);
        Assert.Equal(0, scenario.Stream.ReadCalls);
        AssertJoined(scenario);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Close_Task_Completion_Preserves_Physical_Dispose_Failures_In_Host_Diagnostics(
        bool failSocket, bool failStream)
    {
        IOException? socketFailure = failSocket ? new IOException("受控 socket 释放失败。") : null;
        InvalidOperationException? streamFailure = failStream ? new InvalidOperationException("受控 SSL 释放失败。") : null;
        await using Scenario scenario = new(socketFailure: socketFailure, streamFailure: streamFailure);
        Task run = scenario.Start();
        await run.WaitAsync(Guard);
        Task close = scenario.Connection.CloseAsync();
        await close.WaitAsync(Guard);

        // CloseHandle 的正常完成只承诺两种释放均已尝试，不能据此断言物理释放成功。
        Assert.True(close.IsCompletedSuccessfully);
        Assert.Equal(!failSocket, scenario.Socket.DisposeSucceeded);
        Assert.Equal(!failStream, scenario.Stream.DisposeSucceeded);
        Assert.True(scenario.Socket.DisposeExited.Task.IsCompletedSuccessfully);
        Assert.True(scenario.Stream.DisposeExited.Task.IsCompletedSuccessfully);
        AssertDisposeFailures(scenario, socketFailure, streamFailure);
        AssertNoAuthentication(scenario);
        Assert.Equal("channel-hello-invalid", scenario.Router.Rejection);
        Assert.Equal(1, scenario.Socket.DisposeCalls);
        Assert.Equal(1, scenario.Stream.DisposeCalls);
        Assert.Same(close, scenario.Closer.CloseAsync());
    }

    [Theory(Timeout = 60_000)]
    [InlineData("invalid-operation", false)]
    [InlineData("argument", false)]
    [InlineData("invalid-operation", true)]
    [InlineData("argument", true)]
    public async Task Original_Nonnetwork_Read_Failure_Is_Not_Replaced_By_Close_Or_Host_Cancellation(
        string failureKind, bool cancelHost)
    {
        Exception readFailure = failureKind == "invalid-operation"
            ? new InvalidOperationException("原始读的非网络故障。")
            : new ArgumentException("原始读的参数故障。");
        IOException socketFailure = new("原读故障后的 socket 释放失败。");
        IOException streamFailure = new("原读故障后的 SSL 释放失败。");
        await using Scenario scenario = new(heldRead: 1, holdStreamDispose: true,
            readFailure: readFailure, socketFailure: socketFailure, streamFailure: streamFailure);
        Task run = scenario.Start();
        await scenario.Stream.ReadEntered.Task.WaitAsync(Guard);
        Task<int> originalRead = Assert.IsAssignableFrom<Task<int>>(scenario.Stream.HeldReadOperation);
        if (cancelHost)
        {
            await scenario.CancelHostAsync().WaitAsync(Guard);
            await AssertSignalAsync(scenario.Stream.DisposeEntered.Task);
            Assert.False(originalRead.IsCompleted);
            AssertRouterPending(scenario, run);
        }

        scenario.Stream.ReleaseRead();
        Assert.Same(readFailure, await CaptureAsync(originalRead));
        await AssertSignalAsync(scenario.Stream.DisposeEntered.Task);
        Task close = scenario.Connection.CloseAsync();
        Assert.False(close.IsCompleted);
        AssertRouterPending(scenario, run);
        scenario.Stream.ReleaseDispose();

        Assert.Same(readFailure, await CaptureAsync(run));
        Assert.True(run.IsFaulted);
        Assert.Same(readFailure, Assert.Single(run.Exception!.InnerExceptions));
        await close.WaitAsync(Guard);
        AssertDisposeFailures(scenario, socketFailure, streamFailure);
        Assert.False(scenario.Socket.DisposeSucceeded);
        Assert.False(scenario.Stream.DisposeSucceeded);
        Assert.Equal(1, scenario.Socket.DisposeCalls);
        Assert.Equal(1, scenario.Stream.DisposeCalls);
        AssertNoAuthentication(scenario);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hello2_Challenge_Uncooperative_Write_Or_Flush_Is_Closed_And_Joined(bool holdFlush)
    {
        await using Scenario scenario = new(controlHello: true, holdWrite: !holdFlush, holdFlush: holdFlush);
        Task run = scenario.Start();
        await (holdFlush ? scenario.Stream.FlushEntered.Task : scenario.Stream.WriteEntered.Task).WaitAsync(Guard);
        Task original = Assert.IsAssignableFrom<Task>(holdFlush ? scenario.Stream.FlushOperation : scenario.Stream.WriteOperation);
        Assert.False(original.IsCompleted);
        byte[] challenge = Assert.Single(scenario.Stream.Writes);
        Assert.Equal((uint)(challenge.Length - TransportConstants.LengthPrefixBytes), BinaryPrimitives.ReadUInt32BigEndian(challenge));
        Assert.Equal(WireV2ParseStatus.AcceptedV2,
            AuthChallengeV2.Parse(challenge.AsSpan(TransportConstants.LengthPrefixBytes), out _));
        Assert.Equal(2, scenario.Stream.ReadCalls);

        await scenario.CancelHostAsync().WaitAsync(Guard);
        await AssertSignalAsync(scenario.Stream.DisposeExited.Task);
        await scenario.Connection.CloseAsync().WaitAsync(Guard);
        Assert.True(scenario.Socket.DisposeSucceeded);
        Assert.True(scenario.Stream.DisposeSucceeded);
        Assert.False(original.IsCompleted);
        AssertRouterPending(scenario, run);
        AssertNoAuthentication(scenario, challengeWritten: true);

        scenario.Stream.ReleaseWrite();
        scenario.Stream.ReleaseFlush();
        await original.WaitAsync(Guard);
        await AssertHostCancelledAsync(scenario, run);
        Assert.Equal(1, scenario.Stream.WriteCalls);
        Assert.Equal(holdFlush ? 1 : 0, scenario.Stream.FlushCalls);
        Assert.Equal(2, scenario.Stream.ReadCalls);
        AssertNoAuthentication(scenario, challengeWritten: true);
        AssertJoined(scenario);
    }

    private static async Task AssertSignalAsync(Task signal)
    {
        // Delay 只作负向变异的有限看门狗，不据此推断 pending；缺少关闭通知必须明确断言失败。
        using CancellationTokenSource watchdogStop = new();
        Task watchdog = Task.Delay(Guard, watchdogStop.Token);
        try
        {
            Task observed = await Task.WhenAny(signal, watchdog);
            Assert.Same(signal, observed);
            Assert.True(signal.IsCompletedSuccessfully);
        }
        finally
        {
            await watchdogStop.CancelAsync();
            try { await watchdog; }
            catch (OperationCanceledException) when (watchdog.IsCanceled) { }
        }
    }

    private static void AssertRouterPending(Scenario scenario, Task run)
    {
        Assert.False(run.IsCompleted);
        Assert.False(scenario.RouterEnded.Task.IsCompleted);
    }

    private static void AssertNoAuthentication(Scenario scenario, bool challengeWritten = false)
    {
        Assert.Equal(0, scenario.Services.SecretCalls);
        Assert.Equal(0, scenario.Services.ApprovalCalls);
        Assert.Equal(0, scenario.Services.FrameCalls);
        Assert.Equal(0, scenario.Context.SessionRegistry.ActiveSessionCount);
        Assert.Equal(0, scenario.Context.PendingApprovalLimiter.GlobalInUse);
        Assert.Equal(0, scenario.Context.FailedAuthLimiter.CountRecentFailures(IPAddress.Loopback));
        Assert.Equal(0, scenario.Router.AttachAttempts);
        Assert.Null(scenario.Router.AttachStatus);
        Assert.Equal(challengeWritten ? 1 : 0, scenario.Stream.WriteCalls);
        if (!challengeWritten) Assert.Equal(0, scenario.Stream.FlushCalls);
    }

    private static void AssertJoined(Scenario scenario)
    {
        Assert.True(scenario.Run!.IsCompleted);
        Assert.True(scenario.Connection.CloseAsync().IsCompletedSuccessfully);
        Assert.True(scenario.Socket.DisposeSucceeded);
        Assert.True(scenario.Stream.DisposeSucceeded);
        Assert.Equal(1, scenario.Socket.DisposeCalls);
        Assert.Equal(1, scenario.Stream.DisposeCalls);
        Assert.All(scenario.Stream.Operations, operation => Assert.True(operation.IsCompletedSuccessfully));
        Assert.Equal(0, scenario.Clock.TimerCount);
        Assert.Empty(scenario.Closer.CleanupErrors);
        Assert.Empty(scenario.HostErrors.Snapshot);
    }

    private static void AssertDisposeFailures(Scenario scenario, Exception? socketFailure, Exception? streamFailure)
    {
        Exception[] expected = new[] { socketFailure, streamFailure }.OfType<Exception>().ToArray();
        Assert.Equal(expected.Length, scenario.Closer.CleanupErrors.Count);
        Assert.Equal(expected.Length, scenario.HostErrors.Snapshot.Count);
        foreach (Exception error in expected)
            Assert.Same(error, Assert.Single(scenario.Closer.CleanupErrors, item => ReferenceEquals(item, error)));
        if (socketFailure is not null)
            Assert.Same(socketFailure, Assert.Single(scenario.HostErrors.Snapshot,
                item => item.Kind == HostLifecycleErrorKind.SocketCleanup).Error);
        if (streamFailure is not null)
            Assert.Same(streamFailure, Assert.Single(scenario.HostErrors.Snapshot,
                item => item.Kind == HostLifecycleErrorKind.StreamCleanup).Error);
    }

    private static async Task AssertHostCancelledAsync(Scenario scenario, Task task)
    {
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(Guard));
        Assert.True(task.IsCanceled);
        Assert.Equal(scenario.HostStop.Token, error.CancellationToken);
    }

    private static async Task<Exception?> CaptureAsync(Task task)
    {
        try { await task.WaitAsync(Guard); return null; }
        catch (Exception error) when (task.IsCompleted && error is not TimeoutException) { return error; }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static byte[] Wire(byte[] payload)
    {
        byte[] wire = new byte[TransportConstants.LengthPrefixBytes + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(wire, (uint)payload.Length);
        payload.CopyTo(wire, TransportConstants.LengthPrefixBytes);
        return wire;
    }

    private sealed class Scenario : IAsyncDisposable
    {
        private readonly Exception? _readFailure;
        private readonly Exception? _socketFailure;
        private readonly Exception? _streamFailure;
        private Task? _invocation;
        private Task? _completionObserver;
        private readonly ConcurrentQueue<Task> _cancellations = new();

        internal Scenario(int heldRead = 0, bool synchronousReadPrefix = false, bool holdSocket = false,
            bool holdStreamDispose = false, Exception? readFailure = null, Exception? socketFailure = null,
            Exception? streamFailure = null, bool controlHello = false, bool holdWrite = false, bool holdFlush = false)
        {
            _readFailure = readFailure;
            _socketFailure = socketFailure;
            _streamFailure = streamFailure;
            byte[] hello = controlHello ? ControlHelloV2.Serialize() : Encoding.UTF8.GetBytes("{\"type\":\"unknown-channel\"}");
            Stream = new ControlledSslStream(Wire(hello), heldRead, synchronousReadPrefix,
                holdWrite, holdFlush, holdStreamDispose, readFailure, streamFailure);
            Socket = new GatedSocket(holdSocket, socketFailure);
            Closer = new ConnectionCloseHandle(Socket, HostErrors);
            Closer.Attach(Stream);
            Connection = new AcceptedConnection(new ConnectionSecurityContext(
                IPAddress.Loopback, IPAddress.Loopback, 12345, SslProtocols.None,
                new byte[CertificatePin.LengthBytes]), Stream, Closer);
            Context = new ControlAuthContext
            {
                ServerDeviceId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
                AccessSecretStore = Services,
                FailedAuthLimiter = new FailedAuthLimiter(),
                PendingApprovalLimiter = new ConnectionAdmissionLimiter(3, 1),
                ApprovalGate = Services,
                SessionRegistry = new SessionRegistry(),
                TimeProvider = Clock,
                Options = new ControlAuthOptions { FailureDelayMin = TimeSpan.Zero, FailureDelayMax = TimeSpan.Zero },
            };
            Router = new FirstFrameRouter(Context, Timeouts, Services);
        }

        internal CancellationTokenSource HostStop { get; } = new();
        internal HostLifecycleErrors HostErrors { get; } = new();
        internal ManualDeadlineClock Clock { get; } = new();
        internal UnexpectedServices Services { get; } = new();
        internal ControlledSslStream Stream { get; }
        internal GatedSocket Socket { get; }
        internal ConnectionCloseHandle Closer { get; }
        internal AcceptedConnection Connection { get; }
        internal ControlAuthContext Context { get; }
        internal FirstFrameRouter Router { get; }
        internal Task? Run { get; private set; }
        internal int CallerThreadId { get; private set; }
        internal TaskCompletionSource InvocationReturned { get; } = Signal();
        internal TaskCompletionSource RouterEnded { get; } = Signal();

        internal Task Start()
        {
            Assert.Null(Run);
            Assert.True(Connection.HasCloseAuthority);
            CallerThreadId = Environment.CurrentManagedThreadId;
            Run = Router.RunAsync(Connection, HostStop.Token);
            _completionObserver = Run.ContinueWith(_ => RouterEnded.TrySetResult(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            InvocationReturned.TrySetResult();
            return Run;
        }

        internal Task StartOnWorker() => _invocation = Task.Run(() => Start());

        internal Task CancelHostAsync()
        {
            Task cancellation = HostStop.CancelAsync();
            _cancellations.Enqueue(cancellation);
            return cancellation;
        }

        public async ValueTask DisposeAsync()
        {
            // await using 的 finally 先放开所有门，再 join 实际 run、调用代理、原 IO 和 close；看门狗失败仍须报告。
            Stream.ReleaseAll();
            Socket.Release();
            ConcurrentQueue<Exception> unexpected = new();
            try
            {
                Task cancellation = CancelHostAsync();
                Task close = Connection.CloseAsync();
                await Task.WhenAll(JoinAsync(cancellation), JoinAsync(close), JoinAsync(_invocation), JoinAsync(Run));
                // 同步前缀可能直到上一轮 join 才交付 Run 和其完成观察任务，不能提前快照后漏收。
                await Task.WhenAll(JoinAsync(Run), JoinAsync(_completionObserver));
                await Task.WhenAll(Stream.Operations.Concat(_cancellations).Select(JoinAsync));
                foreach (Exception error in Closer.CleanupErrors)
                    if (!ReferenceEquals(error, _socketFailure) && !ReferenceEquals(error, _streamFailure))
                        unexpected.Enqueue(error);
            }
            finally
            {
                HostStop.Dispose();
            }
            if (unexpected.Count == 1) ExceptionDispatchInfo.Capture(unexpected.Single()).Throw();
            if (!unexpected.IsEmpty) throw new AggregateException("测试清理观察到非预期故障。", unexpected);

            async Task JoinAsync(Task? task)
            {
                if (task is null) return;
                try { await task.WaitAsync(Guard); }
                catch (Exception error)
                {
                    if (!task.IsCompleted || error is TimeoutException)
                    {
                        unexpected.Enqueue(error);
                        if (!task.IsCompleted) return;
                    }
                    if (task.Exception is { } failures)
                    {
                        // await 只抛首根；逐根检查原 Task，保留未知嵌套树与重复故障，不 Flatten 或去重。
                        foreach (Exception root in failures.InnerExceptions)
                            if (root is TimeoutException || !ReferenceEquals(root, _readFailure))
                                unexpected.Enqueue(root);
                        return;
                    }
                    if (error is TimeoutException) return;
                    if (error is OperationCanceledException cancelled && task.IsCanceled &&
                        HostStop.IsCancellationRequested && cancelled.CancellationToken == HostStop.Token) return;
                    // 不吞断言或未知错误；尝试 join 全部任务后再抛出。
                    unexpected.Enqueue(error);
                }
            }
        }
    }

    private sealed class ControlledSslStream : SslStream
    {
        private readonly byte[] _input;
        private readonly int _heldRead;
        private readonly bool _synchronousPrefix;
        private readonly Exception? _readFailure;
        private readonly Exception? _disposeFailure;
        private readonly TaskCompletionSource _readRelease = Signal();
        private readonly TaskCompletionSource _prefixRelease = Signal();
        private readonly TaskCompletionSource _writeRelease = Signal();
        private readonly TaskCompletionSource _flushRelease = Signal();
        private readonly TaskCompletionSource _disposeRelease = Signal();
        private int _offset;
        private int _readCalls;
        private int _writeCalls;
        private int _flushCalls;
        private int _disposeCalls;
        private int _disposeSucceeded;

        internal ControlledSslStream(byte[] input, int heldRead, bool synchronousPrefix,
            bool holdWrite, bool holdFlush, bool holdDispose, Exception? readFailure, Exception? disposeFailure)
            : base(new MemoryStream())
        {
            _input = input;
            _heldRead = heldRead;
            _synchronousPrefix = synchronousPrefix;
            _readFailure = readFailure;
            _disposeFailure = disposeFailure;
            if (heldRead == 0) ReleaseRead();
            if (!synchronousPrefix) ReleaseSynchronousPrefix();
            if (!holdWrite) ReleaseWrite();
            if (!holdFlush) ReleaseFlush();
            if (!holdDispose) ReleaseDispose();
        }

        internal ConcurrentQueue<Task> Operations { get; } = new();
        internal ConcurrentQueue<byte[]> Writes { get; } = new();
        internal TaskCompletionSource ReadEntered { get; } = Signal();
        internal TaskCompletionSource WriteEntered { get; } = Signal();
        internal TaskCompletionSource FlushEntered { get; } = Signal();
        internal TaskCompletionSource DisposeEntered { get; } = Signal();
        internal TaskCompletionSource DisposeExited { get; } = Signal();
        internal Task<int>? HeldReadOperation { get; private set; }
        internal Task? WriteOperation { get; private set; }
        internal Task? FlushOperation { get; private set; }
        internal CancellationToken HeldReadToken { get; private set; }
        internal int HeldReadThreadId { get; private set; }
        internal int ReadCalls => Volatile.Read(ref _readCalls);
        internal int WriteCalls => Volatile.Read(ref _writeCalls);
        internal int FlushCalls => Volatile.Read(ref _flushCalls);
        internal int BytesRead => Volatile.Read(ref _offset);
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        internal bool DisposeSucceeded => Volatile.Read(ref _disposeSucceeded) != 0;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = Interlocked.Increment(ref _readCalls);
            cancellationToken.ThrowIfCancellationRequested();
            if (read == _heldRead)
            {
                HeldReadToken = cancellationToken;
                HeldReadThreadId = Environment.CurrentManagedThreadId;
                HeldReadOperation = CompleteReadAsync(buffer);
                Operations.Enqueue(HeldReadOperation);
                ReadEntered.TrySetResult();
                // 同步前缀门与原异步读门互不代替；关闭和取消都不负责替测试放行。
                if (_synchronousPrefix) _prefixRelease.Task.WaitAsync(BlockingGuard).GetAwaiter().GetResult();
                return new ValueTask<int>(HeldReadOperation);
            }
            Task<int> operation = Task.FromResult(CopyInput(buffer));
            Operations.Enqueue(operation);
            return new ValueTask<int>(operation);
        }

        private async Task<int> CompleteReadAsync(Memory<byte> buffer)
        {
            await _readRelease.Task.ConfigureAwait(false);
            if (_readFailure is not null) throw _readFailure;
            return CopyInput(buffer);
        }

        private int CopyInput(Memory<byte> buffer)
        {
            if (_offset == _input.Length)
                throw new InvalidOperationException("本用例不得在首帧后再启动读取。");
            int count = Math.Min(buffer.Length, _input.Length - _offset);
            _input.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _writeCalls);
            Writes.Enqueue(buffer.ToArray());
            WriteOperation = CompleteWriteAsync();
            Operations.Enqueue(WriteOperation);
            WriteEntered.TrySetResult();
            return new ValueTask(WriteOperation);
        }

        private async Task CompleteWriteAsync() => await _writeRelease.Task.ConfigureAwait(false);

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _flushCalls);
            FlushOperation = CompleteFlushAsync();
            Operations.Enqueue(FlushOperation);
            FlushEntered.TrySetResult();
            return FlushOperation;
        }

        private async Task CompleteFlushAsync() => await _flushRelease.Task.ConfigureAwait(false);

        internal void ReleaseRead() => _readRelease.TrySetResult();
        internal void ReleaseSynchronousPrefix() => _prefixRelease.TrySetResult();
        internal void ReleaseWrite() => _writeRelease.TrySetResult();
        internal void ReleaseFlush() => _flushRelease.TrySetResult();
        internal void ReleaseDispose() => _disposeRelease.TrySetResult();
        internal void ReleaseAll()
        {
            ReleaseRead();
            ReleaseSynchronousPrefix();
            ReleaseWrite();
            ReleaseFlush();
            ReleaseDispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing) { base.Dispose(disposing); return; }
            Interlocked.Increment(ref _disposeCalls);
            DisposeEntered.TrySetResult();
            try
            {
                _disposeRelease.Task.WaitAsync(BlockingGuard).GetAwaiter().GetResult();
                if (_disposeFailure is not null) throw _disposeFailure;
                base.Dispose(disposing);
                Volatile.Write(ref _disposeSucceeded, 1);
            }
            finally { DisposeExited.TrySetResult(); }
        }
    }

    private sealed class GatedSocket : IDisposable
    {
        private readonly TaskCompletionSource _release = Signal();
        private readonly Exception? _failure;
        private int _disposeCalls;
        private int _disposeSucceeded;

        internal GatedSocket(bool holdDispose, Exception? failure)
        {
            _failure = failure;
            if (!holdDispose) Release();
        }

        internal TaskCompletionSource DisposeEntered { get; } = Signal();
        internal TaskCompletionSource DisposeExited { get; } = Signal();
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        internal bool DisposeSucceeded => Volatile.Read(ref _disposeSucceeded) != 0;
        internal void Release() => _release.TrySetResult();

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCalls);
            DisposeEntered.TrySetResult();
            try
            {
                _release.Task.WaitAsync(BlockingGuard).GetAwaiter().GetResult();
                if (_failure is not null) throw _failure;
                Volatile.Write(ref _disposeSucceeded, 1);
            }
            finally { DisposeExited.TrySetResult(); }
        }
    }

    private sealed class UnexpectedServices : IAccessSecretStore, ILocalApprovalGate, IVideoFrameSource
    {
        internal int SecretCalls { get; private set; }
        internal int ApprovalCalls { get; private set; }
        internal int FrameCalls { get; private set; }

        public Task<AccessSecret> LoadOrCreateAsync(CancellationToken cancellationToken = default)
        {
            SecretCalls++;
            throw new InvalidOperationException("本用例不得加载访问密钥。");
        }

        public Task<AccessSecret> RegenerateAsync(CancellationToken cancellationToken = default)
        {
            SecretCalls++;
            throw new InvalidOperationException("本用例不得轮换访问密钥。");
        }

        public ValueTask<LocalApprovalDecision> RequestApprovalAsync(LocalApprovalRequest request, CancellationToken cancellationToken)
        {
            ApprovalCalls++;
            throw new InvalidOperationException("本用例不得请求本机审批。");
        }

        public ValueTask<EncodedFrame?> ReadNextAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            FrameCalls++;
            throw new InvalidOperationException("本用例不得请求视频帧。");
        }
    }
}
