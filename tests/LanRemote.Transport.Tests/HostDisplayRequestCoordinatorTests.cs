using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;
using Admission = LanRemote.Transport.SessionRegistry.SessionRegistration.DisplaySwitchAdmission;

namespace LanRemote.Transport.Tests;

public sealed class HostDisplayRequestCoordinatorTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private const int Timeout = 60_000;

    [Fact(Timeout = Timeout)]
    public async Task Real_First_Send_Must_Finish_Before_Same_Writer_Emits_DisplayChanged()
    {
        await using Fixture f = await Fixture.CreateAsync();
        Producer candidate = f.Producer(withFrame: true);
        HostDisplayRequestCoordinator coordinator = f.Coordinate((target, _) =>
        {
            Assert.Same(f.Catalog.Displays[1], target);
            return Task.FromResult<IVideoFrameProducer>(candidate);
        });
        Task completion = coordinator.Completion;
        Task handling = f.Handle();
        Assert.Same(handling, f.Active);
        var generation = await f.GenerationAsync(candidate);
        Assert.False(handling.IsCompleted);
        Assert.False(generation.FirstSend.IsCompleted);
        Assert.Single(f.ControlFrames());
        f.Video.HoldFlush = true;
        _ = f.StartSender();
        await f.Video.FlushEntered.Task.WaitAsync(Guard);
        Assert.False(generation.FirstSend.IsCompleted);
        Assert.False(handling.IsCompleted);
        Assert.Single(f.ControlFrames());

        f.Video.Release();
        await handling.WaitAsync(Guard);
        Assert.Equal(new VideoFrameSender.SentFrame(70, 640, 360), await generation.FirstSend);
        Assert.Same(generation, f.Sender!.LastSuccessfulGeneration!.Generation);
        DisplayChangedFrame changed = f.Changed(1);
        Assert.Equal(1, changed.Sequence);
        Assert.Equal(f.Clock.GetUtcNow().ToUnixTimeMilliseconds(), changed.TimestampMs);
        Assert.Equal(0, changed.RequestSequence);
        Assert.Equal(f.Catalog.Sequence, changed.CatalogSequence);
        Assert.Equal(f.Catalog.Displays[1].Id, changed.DisplayId);
        Assert.Equal((ulong)70, changed.FirstFrameId);
        Assert.Equal(640, changed.Width);
        Assert.Equal(360, changed.Height);
        Assert.Same(completion, coordinator.Completion);
        Assert.False(completion.IsCompleted);
        Assert.False(coordinator.StopRequired.IsCompleted);
        Assert.False(f.Close.Task.IsCompleted);
        Assert.Null(coordinator.Error);
        f.RevokeParent();
        Assert.Same(completion, coordinator.DisposeAsync().AsTask());
        await completion.WaitAsync(Guard);
        Assert.Null(coordinator.Error);
        Assert.False(f.Source.Completion.IsCompleted);
        Assert.False(f.Lease.Completed.IsCompleted);
        Assert.Equal(0, f.Control.DisposeCalls);
    }

    [Fact(Timeout = Timeout)]
    public async Task Two_Sequential_Requests_Keep_One_Admission_Executor_And_Stable_Session_Completion()
    {
        await using Fixture f = await Fixture.CreateAsync();
        Producer first = f.Producer(withFrame: true), second = f.Producer(withFrame: true);
        int next = 0;
        HostDisplayRequestCoordinator coordinator = f.Coordinate((_, _) =>
            Task.FromResult<IVideoFrameProducer>(Interlocked.Increment(ref next) == 1 ? first : second));
        Task completion = coordinator.Completion;
        Task handlingA = f.Handle();
        var generationA = await f.GenerationAsync(first);
        DisplaySwitchExecutor executor = f.Executor;
        Admission admission = Field<Admission>(coordinator, "_admission");
        _ = f.StartSender();
        await handlingA.WaitAsync(Guard);
        await first.ReadWaiting.Task.WaitAsync(Guard);
        Task handlingB = f.Handle(1);
        Assert.NotSame(handlingA, handlingB);
        Assert.Same(handlingB, f.Active);
        var generationB = await f.GenerationAsync(second);
        await handlingB.WaitAsync(Guard);
        Assert.NotSame(generationA, generationB);
        Assert.Same(executor, f.Executor);
        Assert.Same(admission, Field<Admission>(coordinator, "_admission"));
        Assert.Same(completion, coordinator.Completion);
        Assert.False(completion.IsCompleted);
        Assert.False(executor.Completion.IsCompleted);
        Assert.Equal(2, f.PrepareCalls);
        Assert.Equal(3, f.ControlFrames().Count);
        Assert.Equal((ulong)70, f.Changed(1).FirstFrameId);
        Assert.Equal((ulong)71, f.Changed(2).FirstFrameId);
        Assert.Equal(1, f.Changed(2).RequestSequence);
        Assert.Equal(2, f.Changed(2).Sequence);
        Assert.False(f.Close.Task.IsCompleted);
        f.RevokeParent();
        await coordinator.DisposeAsync();
        Assert.Null(coordinator.Error);
    }

    [Fact(Timeout = Timeout)]
    public async Task Missing_Published_Source_Fails_Closed_Without_Prepare_Or_Waiting_For_Publication()
    {
        await using Fixture f = await Fixture.CreateAsync(publish: false);
        HostDisplayRequestCoordinator coordinator = f.Coordinate((_, _) => throw new InvalidOperationException("不应准备。"));
        Assert.Null(Field<object?>(coordinator, "_admission"));
        Task handling = f.Handle();
        Assert.True(handling.IsCompletedSuccessfully);
        Assert.True(coordinator.StopRequired.IsCompleted);
        Assert.True(f.Close.Task.IsCompleted);
        // 即使随后来源发布，已经失败的初始化也不会重试。
        f.Publish();
        await f.Handle(1).WaitAsync(Guard);
        f.RevokeParent();
        await coordinator.Completion.WaitAsync(Guard);
        Assert.IsType<InvalidOperationException>(coordinator.Error);
        Assert.Null(Field<object?>(coordinator, "_executor"));
        Assert.Equal(0, f.PrepareCalls);
        Assert.Single(f.ControlFrames());
        Assert.False(f.Source.Termination.IsCompleted);
        Assert.False(f.Lease.Completed.IsCompleted);
    }

    [Fact(Timeout = Timeout)]
    public async Task Late_Prepared_Candidate_Is_Locally_Stopped_Disposed_And_Fully_Joined()
    {
        await using Fixture f = await Fixture.CreateAsync();
        TaskCompletionSource entered = Signal(), stopTail = Signal(), disposeTail = Signal(), completionTail = Signal();
        TaskCompletionSource<IVideoFrameProducer> prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Producer candidate = f.Producer();
        candidate.StopTail = stopTail.Task;
        candidate.DisposeTail = disposeTail.Task;
        candidate.CompletionTail = completionTail.Task;
        HostDisplayRequestCoordinator coordinator = f.Coordinate((_, _) => { entered.TrySetResult(); return prepared.Task; });
        Task handling = f.Handle();
        try
        {
            await entered.Task.WaitAsync(Guard);
            f.RevokeParent();
            coordinator.RequestStop();
            Assert.True(f.Close.Task.IsCompleted);
            Assert.False(handling.IsCompleted);
            Assert.False(coordinator.Completion.IsCompleted);
            prepared.SetResult(candidate);
            await Task.WhenAll(candidate.StopEntered.Task, candidate.DisposeEntered.Task, candidate.CompletionObserved.Task).WaitAsync(Guard);
            Assert.Equal(0, candidate.StartCalls);
            Task execution = f.Execution;
            await CatalogLifetimeScenario.AssertAwaitPathAsync(execution, handling);
            await CatalogLifetimeScenario.AssertAwaitPathAsync(handling, coordinator.Completion);
            stopTail.SetResult();
            disposeTail.SetResult();
            Assert.False(handling.IsCompleted);
            Assert.False(coordinator.Completion.IsCompleted);
            completionTail.SetResult();
            await handling.WaitAsync(Guard);
            await coordinator.Completion.WaitAsync(Guard);
            Assert.Null(coordinator.Error);
            Assert.Equal(1, candidate.StopCalls);
            Assert.Equal(1, candidate.DisposeCalls);
            Assert.Single(f.ControlFrames());
            Assert.False(f.Source.Termination.IsCompleted);
            Assert.False(f.Lease.Completed.IsCompleted);
        }
        finally
        {
            prepared.TrySetResult(candidate);
            stopTail.TrySetResult();
            disposeTail.TrySetResult();
            completionTail.TrySetResult();
        }
    }

    [Fact(Timeout = Timeout)]
    public async Task Prepare_Multiple_Roots_And_Cancellation_Fault_Are_Reported_Only_Through_Executor_Completion()
    {
        await using Fixture f = await Fixture.CreateAsync();
        IOException repeated = new("准备重复根。"), cancellation = new("准备取消回调失败。");
        AggregateException nested = new("原嵌套。", new OperationCanceledException("自发取消。"), repeated);
        TaskCompletionSource entered = Signal();
        TaskCompletionSource<IVideoFrameProducer> prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration callback = default;
        HostDisplayRequestCoordinator coordinator = f.Coordinate((_, token) =>
        {
            callback = token.Register(() => throw cancellation);
            entered.TrySetResult();
            return prepared.Task;
        });
        Task handling = f.Handle();
        try
        {
            await entered.Task.WaitAsync(Guard);
            prepared.SetException([repeated, nested, repeated]);
            await f.Close.Task.WaitAsync(Guard);
            f.RevokeParent();
            await handling.WaitAsync(Guard);
            await coordinator.Completion.WaitAsync(Guard);
            Assert.True(handling.IsCompletedSuccessfully);
            Assert.True(coordinator.Completion.IsCompletedSuccessfully);
            AggregateException report = Assert.IsType<AggregateException>(coordinator.Error);
            Exception executorError = Assert.IsType<AggregateException>(await ObserveAsync(f.Executor.Completion));
            Assert.Same(executorError, Assert.Single(report.InnerExceptions));
            Assert.Equal(3, Tree(report).Count(error => ReferenceEquals(error, repeated)));
            Assert.Single(Tree(report), error => ReferenceEquals(error, nested));
            Assert.Single(Tree(report), error => ReferenceEquals(error, cancellation));
            Assert.Contains(Tree(report), error => error is AggregateException tree && tree.InnerExceptions.Count == 3 &&
                ReferenceEquals(tree.InnerExceptions[0], repeated) && ReferenceEquals(tree.InnerExceptions[1], nested) &&
                ReferenceEquals(tree.InnerExceptions[2], repeated));
            Assert.Single(f.ControlFrames());
        }
        finally
        {
            prepared.TrySetException(repeated);
            await coordinator.DisposeAsync();
            await callback.DisposeAsync();
        }
    }

    [Fact(Timeout = Timeout)]
    public async Task Synchronous_Prepare_Is_Outside_Both_Gates_And_Cannot_Block_Stop_Notification()
    {
        await using Fixture f = await Fixture.CreateAsync();
        TaskCompletionSource entered = Signal(), release = Signal();
        Producer candidate = f.Producer();
        HostDisplayRequestCoordinator coordinator = f.Coordinate((_, _) =>
        {
            Assert.False(Monitor.IsEntered(Field<object>(f.Coordinator!, "_gate")));
            Assert.False(Monitor.IsEntered(Field<object>(f.Registry, "_gate")));
            Assert.False(f.Active.IsCompleted);
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return Task.FromResult<IVideoFrameProducer>(candidate);
        });
        Task handling = f.Handle();
        try
        {
            await entered.Task.WaitAsync(Guard);
            Assert.Same(handling, f.Active);
            f.RevokeParent();
            await Task.Run(coordinator.RequestStop).WaitAsync(Guard);
            Assert.True(f.Close.Task.IsCompleted);
            Assert.False(handling.IsCompleted);
            Assert.False(coordinator.Completion.IsCompleted);
            release.SetResult();
            await handling.WaitAsync(Guard);
            await coordinator.Completion.WaitAsync(Guard);
            Assert.Null(coordinator.Error);
            Assert.Equal(0, candidate.StartCalls);
            Assert.Equal(1, candidate.StopCalls);
            Assert.Equal(1, candidate.DisposeCalls);
        }
        finally { release.TrySetResult(); }
    }

    [Fact(Timeout = Timeout)]
    public async Task Busy_Request_Fails_Closed_Without_Replacing_Original_Active_Task()
    {
        await using Fixture f = await Fixture.CreateAsync();
        TaskCompletionSource entered = Signal();
        TaskCompletionSource<IVideoFrameProducer> prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Producer candidate = f.Producer();
        HostDisplayRequestCoordinator coordinator = f.Coordinate((_, _) => { entered.TrySetResult(); return prepared.Task; });
        Task handling = f.Handle();
        try
        {
            await entered.Task.WaitAsync(Guard);
            Task rejected = f.Handle(1);
            Assert.True(rejected.IsCompletedSuccessfully);
            Assert.Same(handling, f.Active);
            Assert.True(f.Close.Task.IsCompleted);
            Assert.False(handling.IsCompleted);
            f.RevokeParent();
            prepared.SetResult(candidate);
            await handling.WaitAsync(Guard);
            await coordinator.Completion.WaitAsync(Guard);
            Assert.Equal("request-display-coordinator-busy", Assert.IsType<FrameProtocolException>(coordinator.Error).Reason);
            Assert.Equal(1, f.PrepareCalls);
        }
        finally { prepared.TrySetResult(candidate); }
    }

    [Theory(Timeout = Timeout)]
    [InlineData("writer")]
    [InlineData("request")]
    public async Task Session_Mismatch_Is_Rejected_Before_Creating_Admission(string mismatch)
    {
        await using Fixture f = await Fixture.CreateAsync();
        HostDisplayRequestCoordinator coordinator = f.Coordinate((_, _) => throw new InvalidOperationException("不应准备。"));
        HostControlWriter writer = mismatch == "writer" ? f.NewWriter(Guid.NewGuid()) : f.Writer;
        DisplaySwitchRequestFrame request = mismatch == "request" ? f.Request(session: Guid.NewGuid()) : f.Request();
        Task handling = coordinator.Handle(writer, f.Catalog, request, default);
        Assert.True(handling.IsCompletedSuccessfully);
        Assert.True(f.Close.Task.IsCompleted);
        f.RevokeParent();
        await coordinator.Completion.WaitAsync(Guard);
        Assert.Equal("request-display-session-mismatch", Assert.IsType<FrameProtocolException>(coordinator.Error).Reason);
        Assert.Null(Field<object?>(coordinator, "_admission"));
        Assert.Equal(0, f.PrepareCalls);
    }

    [Theory(Timeout = Timeout)]
    [InlineData("writer")]
    [InlineData("catalog")]
    public async Task Later_Requests_Require_Exact_Writer_And_Catalog_References(string mismatch)
    {
        await using Fixture f = await Fixture.CreateAsync();
        Producer candidate = f.Producer(withFrame: true);
        HostDisplayRequestCoordinator coordinator = f.Coordinate((_, _) => Task.FromResult<IVideoFrameProducer>(candidate));
        Task first = f.Handle();
        await f.GenerationAsync(candidate);
        _ = f.StartSender();
        await first.WaitAsync(Guard);
        HostControlWriter writer = mismatch == "writer" ? f.NewWriter(f.Registration.SessionId) : f.Writer;
        DisplayCatalogFrame catalog = mismatch == "catalog"
            ? new(f.Catalog.Sequence, f.Catalog.TimestampMs, f.Catalog.Displays) : f.Catalog;
        await coordinator.Handle(writer, catalog, f.Request(1), default).WaitAsync(Guard);
        Assert.Same(first, f.Active);
        f.RevokeParent();
        await coordinator.Completion.WaitAsync(Guard);
        Assert.Equal("request-display-owner-mismatch", Assert.IsType<FrameProtocolException>(coordinator.Error).Reason);
        Assert.Equal(1, f.PrepareCalls);
        Assert.Equal(2, f.ControlFrames().Count);
    }

    [Theory(Timeout = Timeout)]
    [InlineData("stop")]
    [InlineData("host")]
    [InlineData("reader")]
    [InlineData("close")]
    public async Task Stop_Before_First_Handle_Is_Lazy_Nonthrowing_And_Completion_Is_Stable(string reason)
    {
        await using Fixture f = await Fixture.CreateAsync();
        using CancellationTokenSource reader = new();
        if (reason == "host") await f.HostStop.CancelAsync();
        if (reason == "reader") await reader.CancelAsync();
        if (reason == "close") f.Close.TrySetResult();
        HostDisplayRequestCoordinator coordinator = f.Coordinate((_, _) => throw new InvalidOperationException("不应准备。"));
        Task completion = coordinator.Completion;
        if (reason == "stop")
        {
            Assert.False(completion.IsCompleted);
            Assert.Throws<ArgumentNullException>(() => new HostDisplayRequestCoordinator(null!, (_, _) => null!, f.Close, default));
            Assert.Throws<ArgumentNullException>(() => new HostDisplayRequestCoordinator(f.Registration, null!, f.Close, default));
            Assert.Throws<ArgumentNullException>(() => new HostDisplayRequestCoordinator(f.Registration, (_, _) => null!, null!, default));
            coordinator.RequestStop();
        }
        await coordinator.Handle(f.Writer, f.Catalog, f.Request(), reader.Token).WaitAsync(Guard);
        f.RevokeParent();
        await completion.WaitAsync(Guard);
        Assert.Same(completion, coordinator.Completion);
        Assert.Same(completion, coordinator.DisposeAsync().AsTask());
        Assert.True(coordinator.StopRequired.IsCompletedSuccessfully);
        Assert.True(f.Close.Task.IsCompletedSuccessfully);
        Assert.Null(coordinator.Error);
        Assert.Null(Field<object?>(coordinator, "_admission"));
        Assert.Equal(0, f.PrepareCalls);
        Assert.False(f.Source.Termination.IsCompleted);
        Assert.False(f.Lease.Completed.IsCompleted);
        Assert.Equal(0, f.Control.DisposeCalls);
    }

    [Fact(Timeout = Timeout)]
    public async Task Admission_Rejection_Is_A_Local_Protocol_Error_Not_An_Execution_Error()
    {
        await using Fixture f = await Fixture.CreateAsync();
        HostDisplayRequestCoordinator coordinator = f.Coordinate((_, _) => throw new InvalidOperationException("不应准备。"));
        DisplaySwitchRequestFrame request = new(f.Registration.SessionId, 0, 1234, f.Catalog.Sequence + 1, f.Catalog.Displays[1].Id);
        await coordinator.Handle(f.Writer, f.Catalog, request, default).WaitAsync(Guard);
        f.RevokeParent();
        await coordinator.Completion.WaitAsync(Guard);
        Assert.Equal("request-display-execution-CatalogMismatch", Assert.IsType<FrameProtocolException>(coordinator.Error).Reason);
        Assert.True(f.Executor.Completion.IsCompletedSuccessfully);
        Assert.Equal(0, f.PrepareCalls);
        Assert.Single(f.ControlFrames());
    }

    [Fact(Timeout = Timeout)]
    public async Task Naturally_Sealed_Null_FirstSend_Fails_Closed_Without_Reply()
    {
        await using Fixture f = await Fixture.CreateAsync();
        Producer candidate = f.Producer();
        HostDisplayRequestCoordinator coordinator = f.Coordinate((_, _) => Task.FromResult<IVideoFrameProducer>(candidate));
        Task handling = f.Handle();
        var generation = await f.GenerationAsync(candidate);
        Assert.False(generation.FirstSend.IsCompleted);
        // 由真实换代封口旧代；禁止手工完成FirstSend。
        await f.Track(f.Source.ReplaceAsync(f.Producer())).WaitAsync(Guard);
        Assert.Null(await generation.FirstSend.WaitAsync(Guard));
        await f.Close.Task.WaitAsync(Guard);
        f.RevokeParent();
        await handling.WaitAsync(Guard);
        await coordinator.Completion.WaitAsync(Guard);
        Assert.Equal("request-display-first-send-missing", Assert.IsType<FrameProtocolException>(coordinator.Error).Reason);
        Assert.Single(f.ControlFrames());
    }

    [Fact(Timeout = Timeout)]
    public async Task Reader_Stop_Can_Leave_FirstSend_Promise_While_Parent_Still_Owns_Real_Sender_Join()
    {
        await using Fixture f = await Fixture.CreateAsync();
        using CancellationTokenSource reader = new();
        Producer candidate = f.Producer(withFrame: true);
        HostDisplayRequestCoordinator coordinator = f.Coordinate((_, _) => Task.FromResult<IVideoFrameProducer>(candidate));
        Task handling = f.Track(coordinator.Handle(f.Writer, f.Catalog, f.Request(), reader.Token));
        var generation = await f.GenerationAsync(candidate);
        f.Video.HoldFlush = true;
        Task sending = f.StartSender();
        try
        {
            await f.Video.FlushEntered.Task.WaitAsync(Guard);
            f.RevokeParent();
            await reader.CancelAsync();
            Assert.True(f.Close.Task.IsCompleted);
            await f.Track(f.Source.DisposeAsync().AsTask()).WaitAsync(Guard);
            await handling.WaitAsync(Guard);
            await coordinator.Completion.WaitAsync(Guard);
            Assert.Null(coordinator.Error);
            Assert.False(generation.FirstSend.IsCompleted);
            Assert.False(sending.IsCompleted);
            Assert.False(f.Lease.Completed.IsCompleted);
            Assert.Single(f.ControlFrames());
            f.Video.Release();
            await sending.WaitAsync(Guard);
            Assert.Equal(new VideoFrameSender.SentFrame(70, 640, 360), await generation.FirstSend);
            Assert.Single(f.ControlFrames());
        }
        finally { f.Video.Release(); }
    }

    [Theory(Timeout = Timeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Blocked_Reply_Notifies_Close_Before_Joining_Original_IO_And_Reports_Tail_Fault_Once(bool failTail)
    {
        await using Fixture f = await Fixture.CreateAsync();
        Producer candidate = f.Producer(withFrame: true);
        HostDisplayRequestCoordinator coordinator = f.Coordinate((_, _) => Task.FromResult<IVideoFrameProducer>(candidate));
        f.Control.HoldReply = true;
        IOException failure = new("回执原I/O尾部失败。");
        if (failTail) f.Control.ReplyFailure = failure;
        Task handling = f.Handle();
        await f.GenerationAsync(candidate);
        _ = f.StartSender();
        try
        {
            Task originalIO = await f.Control.ReplyOperation.Task.WaitAsync(Guard);
            await f.Control.ReplyEntered.Task.WaitAsync(Guard);
            Task reply = Field<Task>(f.Executor, "_reply");
            // 回执预算先到期；通知必须能穿过writer/executor/coordinator到达父关线逻辑。
            f.Clock.Advance(Budget);
            await f.Close.Task.WaitAsync(Guard);
            await coordinator.StopRequired.WaitAsync(Guard);
            f.RevokeParent();
            f.Control.Dispose();
            Assert.Equal(1, f.Control.DisposeCalls);
            Assert.False(originalIO.IsCompleted);
            Assert.False(reply.IsCompleted);
            Assert.False(handling.IsCompleted);
            Assert.False(coordinator.Completion.IsCompleted);
            await CatalogLifetimeScenario.AssertAwaitPathAsync(originalIO, reply);
            await CatalogLifetimeScenario.AssertAwaitPathAsync(reply, handling);
            await CatalogLifetimeScenario.AssertAwaitPathAsync(handling, coordinator.Completion);
            f.Control.Release();
            await handling.WaitAsync(Guard);
            await coordinator.Completion.WaitAsync(Guard);
            Assert.True(handling.IsCompletedSuccessfully);
            Assert.True(originalIO.IsCompleted);
            Assert.Null(coordinator.Error);
            Assert.Null(await ObserveAsync(f.Executor.Completion));
            Exception? writerError = await ObserveAsync(f.Writer.Completion);
            Assert.NotNull(writerError);
            if (failTail)
                Assert.Single(Tree(writerError!), error => ReferenceEquals(error, failure));
        }
        finally { f.Control.Release(); }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    private static IEnumerable<Exception> Tree(Exception error)
    {
        yield return error;
        if (error is AggregateException aggregate)
        {
            foreach (Exception child in aggregate.InnerExceptions)
                foreach (Exception descendant in Tree(child)) yield return descendant;
        }
        else if (error.InnerException is { } inner)
            foreach (Exception descendant in Tree(inner)) yield return descendant;
    }

    private static async Task<Exception?> ObserveAsync(Task task)
    {
        try { await task.WaitAsync(Guard); return null; }
        catch (Exception error) when (task.IsCompleted && error is not TimeoutException) { return error; }
    }

    private static async Task DrainAsync(Task? task)
    {
        if (task is null) return;
        // 正文看门狗只作诊断；收尾直接等待原根，失败不跳过其余原根。
        try { await task.ConfigureAwait(false); }
        catch (Exception) { }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly List<Task> _roots = [];
        private readonly List<Producer> _producers = [];
        private readonly List<EncodedFrame> _frames = [];
        private readonly List<HostControlWriter> _extraWriters = [];
        private int _prepareCalls;
        private IDisposable? _publication;

        private Fixture()
        {
            Guid session = Guid.NewGuid();
            byte[] token = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
            byte[] pin = Enumerable.Range(64, 32).Select(value => (byte)value).ToArray();
            byte[] nonce = Enumerable.Range(128, 16).Select(value => (byte)value).ToArray();
            Registration = Registry.Register(session, Guid.NewGuid(), Guid.NewGuid(), "显示请求协调测试",
                SessionPermission.ViewOnly, IPAddress.Loopback, 12345, token, new VideoAttachWindow(Clock, 15_000),
                pin, default, V2AgreementFixture.Create(session));
            ConnectionSecurityContext security = new(IPAddress.Loopback, IPAddress.Loopback, 23456, SslProtocols.Tls13, pin);
            byte[] transcript = [.. Encoding.UTF8.GetBytes("LANREMOTE-VIDEO-V1\0"),
                .. Convert.FromHexString(session.ToString("N")), .. nonce, .. pin];
            byte[] proof = HMACSHA256.HashData(token, transcript);
            try
            {
                Assert.Equal(VideoAttachStatus.Attached, Registry.TryAttachVideo(session, security, nonce, proof, default, out var lease));
                Lease = Assert.IsType<VideoAttachLease>(lease);
            }
            finally { CryptographicOperations.ZeroMemory(proof); CryptographicOperations.ZeroMemory(token); }
            Source = new(session);
            Writer = new(Control, session, Clock, Budget, Close);
        }

        internal static async Task<Fixture> CreateAsync(bool publish = true)
        {
            Fixture f = new();
            try
            {
                await f.Track(f.Source.ReplaceAsync(f.Producer())).WaitAsync(Guard);
                if (publish) f.Publish();
                DisplayCatalogFrame template = new(37, 12,
                [
                    new DisplayInfo(new DisplayId(3), "主显示器", 0, 0, 1920, 1080, true),
                    new DisplayInfo(new DisplayId(17), "左侧显示器", -1280, -100, 1280, 1024, false),
                ]);
                var written = await f.Track(f.Writer.WriteCatalogAsync(() => ValueTask.FromResult(template))).WaitAsync(Guard);
                f.Catalog = Assert.IsType<DisplayCatalogFrame>(written.Catalog);
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }

        internal SessionRegistry Registry { get; } = new();
        internal SessionRegistry.SessionRegistration Registration { get; }
        internal VideoAttachLease Lease { get; }
        internal ReplaceableVideoFrameSource Source { get; }
        internal ManualDeadlineClock Clock { get; } = new();
        internal CancellationTokenSource HostStop { get; } = new();
        internal TaskCompletionSource Close { get; } = Signal();
        internal ControlStream Control { get; } = new();
        internal VideoStream Video { get; } = new();
        internal HostControlWriter Writer { get; }
        internal DisplayCatalogFrame Catalog { get; private set; } = null!;
        internal HostDisplayRequestCoordinator? Coordinator { get; private set; }
        internal VideoFrameSender? Sender { get; private set; }
        internal int PrepareCalls => Volatile.Read(ref _prepareCalls);
        internal Task Active { get { lock (Field<object>(Coordinator!, "_gate")) return Field<Task>(Coordinator!, "_active"); } }
        internal DisplaySwitchExecutor Executor
        { get { lock (Field<object>(Coordinator!, "_gate")) return Field<DisplaySwitchExecutor>(Coordinator!, "_executor"); } }
        internal Task<DisplaySwitchExecutor.ExecutionResult> Execution
        {
            get
            {
                DisplaySwitchExecutor executor = Executor;
                lock (Field<object>(Registry, "_gate"))
                    return Field<Task<DisplaySwitchExecutor.ExecutionResult>>(executor, "_active");
            }
        }

        internal void Publish() => _publication = Registry.PublishVideoSource(Lease, Source);
        internal void RevokeParent() { Registration.Dispose(); _publication?.Dispose(); }
        internal T Track<T>(T task) where T : Task { _roots.Add(task); return task; }
        internal DisplaySwitchRequestFrame Request(long sequence = 0, Guid? session = null) =>
            new(session ?? Registration.SessionId, sequence, 1234, Catalog.Sequence, Catalog.Displays[1].Id);
        internal Task Handle(long sequence = 0) => Track(Coordinator!.Handle(Writer, Catalog, Request(sequence), default));
        internal HostDisplayRequestCoordinator Coordinate(Func<DisplayInfo, CancellationToken, Task<IVideoFrameProducer>> prepare) =>
            Coordinator = new(Registration, (display, token) =>
            {
                Interlocked.Increment(ref _prepareCalls);
                return prepare(display, token);
            }, Close, HostStop.Token);

        internal HostControlWriter NewWriter(Guid session)
        {
            HostControlWriter writer = new(Control, session, Clock, Budget, Close);
            _extraWriters.Add(writer);
            return writer;
        }

        internal Producer Producer(bool withFrame = false)
        {
            EncodedFrame? frame = null;
            if (withFrame)
            {
                RecordingVideoOwner owner = new(32);
                VideoFrameTestData.Payload().CopyTo(owner.Bytes, 0);
                frame = new(VideoCodec.Jpeg, 640, 360, 999, 1234, 60, owner, 5);
                _frames.Add(frame);
            }
            Producer producer = new(frame);
            _producers.Add(producer);
            return producer;
        }

        internal async Task<ReplaceableVideoFrameSource.GenerationIdentity> GenerationAsync(Producer producer)
        {
            await producer.Started.Task.WaitAsync(Guard);
            var result = await Execution.WaitAsync(Guard);
            Assert.True(result.Transferred && result.ReplacementCompleted);
            return Assert.IsType<DisplaySwitchExecutor.RequestSendReceipt>(result.SendReceipt).Generation;
        }

        internal Task StartSender()
        {
            Assert.Null(Sender);
            Sender = new(70, Source);
            return Track(Sender.SendAsync(Registration.SessionId, new VideoFrameWriter(Video), Source, Clock, Budget, default));
        }

        internal List<byte[]> ControlFrames()
        {
            byte[] bytes = Control.ToArray();
            List<byte[]> frames = [];
            for (int offset = 0; offset < bytes.Length;)
            {
                int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4)));
                frames.Add(bytes.AsSpan(offset + 4, length).ToArray());
                offset += 4 + length;
            }
            return frames;
        }

        internal DisplayChangedFrame Changed(int index)
        {
            Assert.True(DisplayChangedFrame.TryParse(ControlFrames()[index], Registration.SessionId, out var changed, out _));
            return changed!;
        }

        public async ValueTask DisposeAsync()
        {
            // 先释放全部非合作闸门并独立撤销父/停止来源；不能先等待依赖它们的协调器。
            Control.Release();
            Video.Release();
            foreach (Producer producer in _producers) producer.ReleaseRead();
            RevokeParent();
            Coordinator?.RequestStop();
            Writer.RequestStop();
            foreach (HostControlWriter writer in _extraWriters) writer.RequestStop();
            Task closing = Source.DisposeAsync().AsTask();
            Task cancellation = HostStop.CancelAsync();
            foreach (Task root in _roots) await DrainAsync(root);
            await DrainAsync(closing);
            await DrainAsync(Source.Completion);
            await DrainAsync(Coordinator?.Completion);
            await DrainAsync(Writer.Completion);
            foreach (HostControlWriter writer in _extraWriters) await DrainAsync(writer.Completion);
            await DrainAsync(cancellation);
            Lease.Complete();
            foreach (EncodedFrame frame in _frames) frame.Dispose();
            Control.Dispose();
            Video.Dispose();
            HostStop.Dispose();
            Assert.Equal(0, Clock.TimerCount);
        }
    }

    private sealed class Producer(EncodedFrame? frame) : IVideoFrameProducer
    {
        private readonly TaskCompletionSource _completion = Signal();
        private readonly TaskCompletionSource<EncodedFrame?> _next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads, _starts, _stops, _disposes;
        internal Task? StopTail { get; set; }
        internal Task? DisposeTail { get; set; }
        internal Task? CompletionTail { get; set; }
        internal TaskCompletionSource Started { get; } = Signal();
        internal TaskCompletionSource ReadWaiting { get; } = Signal();
        internal TaskCompletionSource StopEntered { get; } = Signal();
        internal TaskCompletionSource DisposeEntered { get; } = Signal();
        internal TaskCompletionSource CompletionObserved { get; } = Signal();
        internal int StartCalls => Volatile.Read(ref _starts);
        internal int StopCalls => Volatile.Read(ref _stops);
        internal int DisposeCalls => Volatile.Read(ref _disposes);
        public void Start() { Interlocked.Increment(ref _starts); Started.TrySetResult(); }
        public ValueTask<EncodedFrame?> ReadNextAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _reads) == 1 && frame is not null) return ValueTask.FromResult<EncodedFrame?>(frame);
            ReadWaiting.TrySetResult();
            return new(_next.Task);
        }
        internal void ReleaseRead() => _next.TrySetResult(null);
        public Task StopAsync()
        {
            Interlocked.Increment(ref _stops);
            ReleaseRead();
            _completion.TrySetResult();
            StopEntered.TrySetResult();
            return StopTail ?? _completion.Task;
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposes);
            ReleaseRead();
            _completion.TrySetResult();
            DisposeEntered.TrySetResult();
            return new(DisposeTail ?? _completion.Task);
        }
        public Task Completion
        {
            get { CompletionObserved.TrySetResult(); return CompletionTail ?? _completion.Task; }
        }
    }

    private sealed class ControlStream : MemoryStream
    {
        private readonly TaskCompletionSource _release = Signal();
        internal bool HoldReply { get; set; }
        internal Exception? ReplyFailure { get; set; }
        internal int DisposeCalls { get; private set; }
        internal TaskCompletionSource ReplyEntered { get; } = Signal();
        internal TaskCompletionSource<Task> ReplyOperation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Release() => _release.TrySetResult();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!HoldReply) return base.WriteAsync(buffer, cancellationToken);
            Task original = WriteReplyAsync(buffer, cancellationToken);
            ReplyOperation.TrySetResult(original);
            return new(original);
        }
        private async Task WriteReplyAsync(ReadOnlyMemory<byte> buffer, CancellationToken token)
        {
            await base.WriteAsync(buffer, token);
            ReplyEntered.TrySetResult();
            await _release.Task; // 原I/O尾部忽略取消及close，不能用关闭通知替代join。
            if (ReplyFailure is { } failure) throw failure;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeCalls++;
            base.Dispose(disposing);
        }
    }

    private sealed class VideoStream : MemoryStream
    {
        private readonly TaskCompletionSource _release = Signal();
        internal bool HoldFlush { get; set; }
        internal TaskCompletionSource FlushEntered { get; } = Signal();
        internal void Release() => _release.TrySetResult();
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await base.FlushAsync(cancellationToken);
            FlushEntered.TrySetResult();
            if (HoldFlush) await _release.Task;
        }
    }
}
