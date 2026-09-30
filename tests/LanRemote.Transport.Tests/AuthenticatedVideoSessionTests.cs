using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks.Sources;
using LanRemote.Core.Models;
using Xunit.Sdk;
using Child = LanRemote.Transport.AuthenticatedControlSession.ClientVideoLifetime;

namespace LanRemote.Transport.Tests;

public sealed class AuthenticatedVideoSessionTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CleanupGuard = TimeSpan.FromSeconds(20);
    private const int TestTimeout = 60_000;
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string SessionText = "00112233-4455-6677-8899-aabbccddeeff";
    private const string TokenHex = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
    private const string PinHex = "808182838485868788898A8B8C8D8E8F909192939495969798999A9B9C9D9E9F";
    private const string AckJson = """{"type":"video_attach_ack","channel":"video","protocol":1,"sessionId":"00112233-4455-6677-8899-aabbccddeeff"}""";

    [Fact]
    public void PublicSurface_IsSealedAndOpaque_AndTestingSeamIsInternal()
    {
        const BindingFlags declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        Type video = typeof(AuthenticatedVideoSession);
        Assert.True(video.IsPublic);
        Assert.True(video.IsSealed);
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(video));
        Assert.False(typeof(IDisposable).IsAssignableFrom(video));
        Assert.Empty(video.GetConstructors(declared));
        Assert.Empty(video.GetFields(declared));
        Assert.Empty(video.GetProperties(declared));
        Assert.Empty(video.GetEvents(declared));
        Assert.Equal(new[] { "DisposeAsync", "ReadFrameAsync" }, video.GetMethods(declared)
            .Select(method => method.Name).OrderBy(name => name, StringComparer.Ordinal));
        ConstructorInfo constructor = Assert.Single(video.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.True(constructor.IsAssembly);
        Assert.Equal(typeof(Child), Assert.Single(constructor.GetParameters()).ParameterType);
        MethodInfo read = video.GetMethod("ReadFrameAsync")!;
        Assert.Equal(typeof(Task<EncodedFrame>), read.ReturnType);
        AssertDefaultCancellation(read);
        Assert.Equal(typeof(ValueTask), video.GetMethod("DisposeAsync")!.ReturnType);
        Assert.Empty(video.GetMethod("DisposeAsync")!.GetParameters());

        Type parent = typeof(AuthenticatedControlSession);
        Assert.True(typeof(IDisposable).IsAssignableFrom(parent));
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(parent));
        MethodInfo attach = parent.GetMethod("AttachVideoAsync", declared)!;
        Assert.NotNull(attach);
        Assert.Equal(typeof(Task<AuthenticatedVideoSession>), attach.ReturnType);
        AssertDefaultCancellation(attach);
        Assert.Equal(typeof(ValueTask), parent.GetMethod("DisposeAsync", declared)!.ReturnType);
        MethodInfo seam = parent.GetMethod("AttachPublicVideoForTestingAsync", Fields)!;
        Assert.NotNull(seam);
        Assert.True(seam.IsAssembly);
        Assert.False(seam.IsStatic);
        Assert.Equal(typeof(Task<AuthenticatedVideoSession>), seam.ReturnType);
        ParameterInfo[] parameters = seam.GetParameters();
        Assert.Equal(new[]
        {
            typeof(Func<ConnectionTarget, TransportTimeouts, TimeProvider, CancellationToken, Task<TlsConnection>>),
            typeof(CancellationToken), typeof(Func<int, IMemoryOwner<byte>>),
            typeof(Action<EncodedFrame>), typeof(TaskScheduler)
        }, parameters.Select(parameter => parameter.ParameterType));
        Assert.False(parameters[0].IsOptional);
        Assert.All(parameters.Skip(1), parameter => Assert.True(parameter.IsOptional));
        Assert.All(parameters.Skip(2), parameter => Assert.Null(parameter.DefaultValue));
        MethodInfo begin = parent.GetMethod("BeginPublicVideoAttach", Fields)!;
        Assert.NotNull(begin);
        Assert.True(begin.IsPrivate);
        Assert.False(begin.IsStatic);
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("not-delivered")]
    [InlineData("cancelled")]
    [InlineData("revoked")]
    [InlineData("expired")]
    public Task PublicEntry_RejectionIsSynchronous_AndDoesNotRegister(string reason) => RunAsync(async f =>
    {
        using CancellationTokenSource caller = new();
        if (reason == "cancelled") caller.Cancel();
        if (reason == "revoked") f.Parent.RevokeForOwnerCleanup();
        if (reason == "expired") f.Clock.Timestamp = TimeSpan.FromSeconds(15).Ticks;
        Exception error = Assert.IsAssignableFrom<Exception>(Record.Exception(() =>
        {
            f.Keep(f.Parent.AttachVideoAsync(caller.Token));
        }));
        switch (reason)
        {
            case "not-delivered": Assert.IsType<InvalidOperationException>(error); break;
            case "cancelled": Assert.Equal(caller.Token, Assert.IsAssignableFrom<OperationCanceledException>(error).CancellationToken); break;
            case "revoked": Assert.IsType<ObjectDisposedException>(error); break;
            default: Assert.IsType<TimeoutException>(error); break;
        }
        Assert.Null(f.RegisteredChild);
        Assert.Null(f.PublicAttach);
        Assert.Null(f.Parent.ControlMonitorCompletion);
        Assert.Empty(f.Control.Ssl.ReadRequests);
        Assert.Equal(0, f.ConnectCalls);
        Assert.Equal(0, f.Clock.TimerCreates);
        if (reason != "expired") Assert.Equal(0, f.Clock.TimestampReads);
        if (reason == "revoked")
        {
            f.AssertRevoked();
            Assert.False(f.Control.Connection.IsCloseRequested);
            return;
        }
        f.AssertControlLive();
        if (reason == "not-delivered") f.Parent.CommitDelivery();
        // 测试时钟回拨仅用于证明先前资格拒绝未消费本地 attempt。
        f.Clock.Timestamp = 0;
        AuthenticatedVideoSession winner = await SuccessAsync(f.Start());
        Assert.Same(f.Child.PublicSession, winner);
    }, delivered: reason != "not-delivered");

    [Fact(Timeout = TestTimeout)]
    public Task NullFactory_DoesNotConsumeAttemptOrPublishProjection() => RunAsync(async f =>
    {
        Assert.IsType<ArgumentNullException>(Record.Exception(() => { _ = f.Keep(f.Parent.AttachPublicVideoForTestingAsync(null!)); }));
        Assert.Null(f.RegisteredChild);
        Assert.Null(f.PublicAttach);
        Assert.Null(f.Parent.ControlMonitorCompletion);
        Assert.Equal(0, f.Clock.TimestampReads);
        Assert.Equal(0, f.Clock.TimerCreates);
        await SuccessAsync(f.Start());
        Assert.Equal(1, f.ConnectCalls);
    });

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public Task DuplicateEntry_IsSynchronous_AndCannotStopOrOverwriteWinner(bool legacyLoser) => RunAsync(async f =>
    {
        Pause connect = f.NewPause();
        Task<TlsConnection> original = f.Keep(ConnectAsync());
        Task<AuthenticatedVideoSession> winner = f.Start(_ => original);
        await SuccessAsync(connect.Reached.Task);
        Task monitor = f.MonitorTask;
        Child child = f.Child;
        AuthenticatedVideoSession wrapper = child.PublicSession;
        Assert.Same(winner, f.PublicAttach);
        if (legacyLoser)
            Assert.IsType<InvalidOperationException>(Record.Exception(() => { _ = f.Keep(f.Parent.AttachVideoCoreAsync()); }));
        else
        {
            Assert.IsType<InvalidOperationException>(Record.Exception(() => { _ = f.Keep(f.Parent.AttachVideoAsync()); }));
            Assert.IsType<InvalidOperationException>(Record.Exception(() => { _ = f.Start(); }));
        }
        Assert.Same(winner, f.PublicAttach);
        Assert.Same(child, f.Child);
        Assert.Same(monitor, f.MonitorTask);
        Assert.False(f.ChildStopped);
        Assert.False(f.ConnectToken.IsCancellationRequested);
        await AssertAwaitPathAsync(original, f.OperationTask("_attach"));
        connect.Open();
        Assert.Same(wrapper, await SuccessAsync(winner));
        Assert.Equal(1, f.ConnectCalls);
        f.AssertControlLive();

        async Task<TlsConnection> ConnectAsync()
        {
            await connect.WaitAsync();
            return f.Video.Connection;
        }
    });

    [Theory(Timeout = TestTimeout)]
    [InlineData("timestamp")]
    [InlineData("frequency")]
    public Task ReentrantClock_OnlyInnerWinnerOwnsChildMonitorAndProjection(string point) => RunAsync(async f =>
    {
        Task<AuthenticatedVideoSession>? inner = null;
        f.Clock.Arm(point, () => inner = f.Start());
        Assert.IsType<InvalidOperationException>(Record.Exception(() => { _ = f.Start(); }));
        Task<AuthenticatedVideoSession> winner = Assert.IsAssignableFrom<Task<AuthenticatedVideoSession>>(inner);
        Assert.Same(winner, f.PublicAttach);
        AuthenticatedVideoSession wrapper = await SuccessAsync(winner);
        Assert.Same(f.Child.PublicSession, wrapper);
        await SuccessAsync(f.ControlRead.Reached.Task);
        Assert.Single(f.Control.Ssl.ReadRequests);
        Assert.Equal(1, f.ConnectCalls);
        Assert.False(f.ChildStopped);
        f.AssertControlLive();
    });

    [Fact(Timeout = TestTimeout)]
    public Task Attach_UsesRealIdentityHelloAck_AndAlwaysStartsOneMonitor() => RunAsync(async f =>
    {
        AuthenticatedVideoSession video = await SuccessAsync(f.Start());
        Assert.Same(f.Child.PublicSession, video);
        Assert.True(f.OperationCommitted("_attach"));
        await SuccessAsync(f.ControlRead.Reached.Task);
        Assert.Equal(new[] { 1 }, f.Control.Ssl.ReadRequests);
        Assert.Equal(new[] { CancellationToken.None }, f.Control.Ssl.ReadTokens);
        Assert.Equal(0, f.Control.Ssl.WriteCalls);
        Assert.Equal(0, f.Control.Ssl.FlushCalls);
        Assert.Equal(1, f.Video.Ssl.WriteCalls);
        Assert.Equal(1, f.Video.Ssl.FlushCalls);
        Assert.Equal(Framed(AckJson).Length, f.VideoOffset);
        AssertHello(f.Video.Ssl);
        int timers = f.Clock.TimerCreates;
        Assert.Equal(1, timers);
        f.Clock.RejectSampling = true;
        Task<EncodedFrame?> read = f.Read(video);
        EncodedFrame frame = Assert.IsType<EncodedFrame>(await SuccessAsync(read));
        Assert.Equal(FramePayload(), frame.Payload.ToArray());
        Assert.Equal(timers, f.Clock.TimerCreates);
        Assert.Same(read, f.OperationTask("_read"));
        await AssertAwaitPathAsync(f.OriginalControlRead, f.MonitorTask);
    });

    [Theory(Timeout = TestTimeout)]
    [InlineData("identity")]
    [InlineData("ack")]
    public Task InvalidHandshake_FailsThroughRealPipeline_AndCanBeReportedAgain(string stage) => RunAsync(async f =>
    {
        if (stage == "ack") f.VideoInput = Framed(AckJson.Replace("\"protocol\":1", "\"protocol\":2"));
        Exception operation = await ErrorAsync(f.Start());
        if (stage == "identity") Assert.IsType<AuthenticationException>(operation);
        else Assert.IsType<FrameProtocolException>(operation);
        Assert.Equal(stage == "identity" ? 0 : 1, f.Video.Ssl.WriteCalls);
        Task childTask = f.Keep(f.Child.PublicSession.DisposeAsync().AsTask());
        AggregateException child = await ReportAsync(childTask);
        Assert.Contains(child.InnerExceptions, error => ReferenceEquals(error, operation));
        f.AssertControlLive();
        await SuccessAsync(f.ControlRead.Reached.Task);
        Task parentTask = f.Keep(f.Parent.DisposeAsync().AsTask());
        f.ControlRead.Open();
        AggregateException parent = await ReportAsync(parentTask);
        AssertContains(parent, operation);
        Assert.Same(childTask, f.Child.PublicSession.DisposeAsync().AsTask());
        Assert.Same(parentTask, f.Parent.DisposeAsync().AsTask());
        f.Video.AssertClosedOnce();
    }, wrongIdentity: stage == "identity");

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public Task CommittedCore_WithProjectionHeld_StillReturnsPrebuiltWrapperAfterStop(bool monitorStop) => RunAsync(async f =>
    {
        ManualProjectionScheduler scheduler = f.NewScheduler();
        Task<AuthenticatedVideoSession> attach = f.Start(scheduler: scheduler);
        await SuccessAsync(scheduler.Queued.Task);
        AuthenticatedVideoSession prebuilt = f.Child.PublicSession;
        Task core = f.OperationTask("_attach");
        await SuccessAsync(core);
        Assert.True(f.OperationCommitted("_attach"));
        Assert.Equal(0, scheduler.Executions);
        Assert.Same(attach, f.PublicAttach);
        await SuccessAsync(f.ControlRead.Reached.Task);
        if (monitorStop)
        {
            f.ControlRead.Open();
            // monitor 不能等待包含自己的父报告，也不能等待还没调度的 public 投影。
            await SuccessAsync(f.MonitorTask);
        }
        Task parentReport = f.Keep(f.Parent.DisposeAsync().AsTask());
        if (!monitorStop) f.ControlRead.Open();
        await SuccessAsync(f.Parent.CloseAndJoinAsync());
        f.AssertRevoked();
        Assert.True(f.ChildStopped);
        await AssertAwaitPathAsync(attach, parentReport);
        Assert.Same(parentReport, f.Parent.DisposeAsync().AsTask());
        Task childReport = f.Keep(prebuilt.DisposeAsync().AsTask());
        await SuccessAsync(childReport);
        Assert.Same(childReport, prebuilt.DisposeAsync().AsTask());
        Assert.Equal(0, scheduler.Executions);
        await AssertAwaitPathAsync(attach, parentReport);
        scheduler.Release();
        await GuardCompletionAsync(attach);
        Exception? error = await Record.ExceptionAsync(() => attach);
        Assert.Null(error);
        Assert.Same(prebuilt, await SuccessAsync(attach));
        await SuccessAsync(parentReport);
        Assert.Equal(1, scheduler.Executions);
        Assert.IsType<ObjectDisposedException>(Record.Exception(() => { _ = f.Read(prebuilt); }));
        await SuccessAsync(prebuilt.DisposeAsync().AsTask());
    });

    [Fact(Timeout = TestTimeout)]
    public Task CommittedCore_CallerCancellationWhileProjectionHeld_CannotChangeOutcome() => RunAsync(async f =>
    {
        using CancellationTokenSource caller = new();
        ManualProjectionScheduler scheduler = f.NewScheduler();
        Task<AuthenticatedVideoSession> attach = f.Start(scheduler: scheduler, cancellationToken: caller.Token);
        await SuccessAsync(scheduler.Queued.Task);
        Task core = f.OperationTask("_attach");
        await SuccessAsync(core);
        Assert.True(f.OperationCommitted("_attach"));
        AuthenticatedVideoSession prebuilt = f.Child.PublicSession;
        await AssertAwaitPathAsync(scheduler.QueuedTask, attach);
        caller.Cancel();
        Assert.False(f.ChildStopped);
        Assert.False(f.ConnectToken.IsCancellationRequested);
        Assert.False(f.Video.Connection.IsCloseRequested);
        f.AssertControlLive();
        Assert.Same(attach, f.PublicAttach);
        Assert.Equal(0, scheduler.Executions);

        Task parent = f.Keep(f.Parent.DisposeAsync().AsTask());
        f.ControlRead.Open();
        await SuccessAsync(f.Parent.CloseAndJoinAsync());
        await AssertAwaitPathAsync(attach, parent);
        scheduler.Release();
        Assert.Same(prebuilt, await SuccessAsync(attach));
        Assert.True(f.OperationCommitted("_attach"));
        await SuccessAsync(parent);
        Assert.Same(parent, f.Parent.DisposeAsync().AsTask());
    });

    [Fact(Timeout = TestTimeout)]
    public Task SynchronousProjectionScheduler_PublishesBeforeQueue_AndKeepsRealAttachAwaitChain() => RunAsync(async f =>
    {
        Pause connect = f.NewPause();
        Task<TlsConnection> original = f.Keep(ConnectAsync());
        Task<AuthenticatedVideoSession>? published = null;
        ManualProjectionScheduler scheduler = f.NewScheduler(
            onQueue: task => published = AssertPublishedProjection(f, task), executeSynchronously: true);
        Task<AuthenticatedVideoSession> attach = f.Start(_ => original, scheduler: scheduler);
        Assert.Same(published, attach);
        Assert.Same(attach, f.PublicAttach);
        Assert.Equal(1, scheduler.Executions);
        await SuccessAsync(scheduler.QueuedTask);
        // 先确认投影已离开调度屏障并停在受闸的原 attach 上，再读取稳定状态机。
        await AssertAwaitPathAsync(original, attach);
        Task core = f.OperationTask("_attach");
        Task<Child> originalAttach = f.Keep(OriginalProjectionAttachTask(attach));
        Assert.NotSame(core, originalAttach);
        Assert.NotSame(core, attach);
        await AssertAwaitPathAsync(original, core);
        await AssertAwaitPathAsync(core, originalAttach);
        await AssertAwaitPathAsync(originalAttach, attach);
        connect.Open();
        Assert.Same(f.Child.PublicSession, await SuccessAsync(attach));
        Assert.True(f.OperationCommitted("_attach"));
        Assert.Equal(1, f.ConnectCalls);
        Assert.False(f.ChildStopped);
        f.AssertControlLive();

        async Task<TlsConnection> ConnectAsync()
        {
            await connect.WaitAsync();
            return f.Video.Connection;
        }
    });

    [Fact(Timeout = TestTimeout)]
    public Task ThrowingProjectionScheduler_ReturnsFaultedPublicTask_AndJoinsOwnChildWithAllOriginalErrors() => RunAsync(async f =>
    {
        Pause cancellation = f.NewPause();
        Pause connect = f.NewPause();
        Task<TlsConnection> original = f.Keep(ConnectAsync());
        AggregateException queueFailure = MixedRoot();
        AggregateException cancellationFailure = MixedRoot();
        CancellationTokenRegistration registration = default;
        Task<AuthenticatedVideoSession>? published = null;
        Task<Child>? originalAttach = null;
        ManualProjectionScheduler scheduler = f.NewScheduler(onQueue: task =>
        {
            published = AssertPublishedProjection(f, task);
            originalAttach = f.Keep(OriginalProjectionAttachTask(published));
            // 只读取自己的停止令牌并挂接真实回调；不替生产代码 RequestStop，也不改写任务槽。
            registration = Field<CancellationTokenSource>(f.Child, "_stopSource").Token.UnsafeRegister(_ =>
            {
                cancellation.WaitSynchronously();
                throw cancellationFailure;
            }, null);
        }, queueFailure: queueFailure);
        try
        {
            Task<AuthenticatedVideoSession>? returned = null;
            Exception? synchronous;
            lock (f.ParentGate)
                synchronous = Record.Exception(() => { returned = f.Start(_ => original, scheduler: scheduler); });
            Assert.Null(synchronous); // 调度失败发生在接受之后，不是资格同步拒绝。
            Task<AuthenticatedVideoSession> attach = Assert.IsAssignableFrom<Task<AuthenticatedVideoSession>>(returned);
            Assert.Same(published, attach);
            Assert.Same(attach, f.PublicAttach);
            TaskSchedulerException scheduling = Assert.IsType<TaskSchedulerException>(await ErrorAsync(scheduler.QueuedTask));
            Assert.Same(scheduling, Assert.Single(scheduler.QueuedTask.Exception!.InnerExceptions));
            Assert.Same(queueFailure, scheduling.InnerException);
            Assert.Equal(0, scheduler.Executions);

            // 必须由投影失败自己请求停止；在闸门命中前不能调用子/父 Dispose 来替它收尾。
            await SuccessAsync(cancellation.Reached.Task);
            Assert.True(f.ChildStopped);
            f.AssertControlLive();
            // 即使原工厂抢先开始，也只能在停止已生效后返回，不能竞速提交成功。
            connect.Open();
            Task core = f.OperationTask("_attach");
            Exception coreFailure = await ErrorAsync(core);
            Task cancel;
            lock (f.ParentGate) cancel = Field<Task>(f.Child, "_cancel");
            Task<Child> rawAttach = Assert.IsAssignableFrom<Task<Child>>(originalAttach);
            Assert.NotSame(core, rawAttach);
            await AssertAwaitPathAsync(cancel, rawAttach);
            await AssertAwaitPathAsync(rawAttach, attach);
            Task child = f.Keep(f.Child.PublicSession.DisposeAsync().AsTask());
            Task parent = f.Keep(f.Parent.DisposeAsync().AsTask());
            f.ControlRead.Open();
            await AssertAwaitPathAsync(cancel, child);
            await AssertAwaitPathAsync(cancel, parent);
            await AssertAwaitPathAsync(attach, parent);
            Assert.Same(child, f.Child.PublicSession.DisposeAsync().AsTask());
            Assert.Same(parent, f.Parent.DisposeAsync().AsTask());

            cancellation.Open();
            AggregateException operation = Assert.IsType<AggregateException>(await ErrorAsync(attach));
            Assert.True(attach.IsFaulted);
            Assert.True(core.IsCompleted);
            Assert.True(rawAttach.IsCompleted);
            Assert.True(cancel.IsCompletedSuccessfully);
            Task join;
            lock (f.ParentGate) join = Field<Task>(f.Child, "_join");
            Assert.True(join.IsCompletedSuccessfully);
            foreach (Exception error in f.Child.LifetimeErrors) AssertContains(operation, error);
            AssertContains(operation, scheduling);
            AssertContains(operation, queueFailure);
            AssertContains(operation, coreFailure);
            AssertContains(operation, cancellationFailure);
            AssertMixedTree(queueFailure);
            AssertMixedTree(cancellationFailure);
            AggregateException childError = await ReportAsync(child);
            AssertContains(childError, coreFailure);
            AssertContains(childError, cancellationFailure);
            AggregateException parentError = await ReportAsync(parent);
            AssertContains(parentError, scheduling);
            AssertContains(parentError, queueFailure);
            AssertContains(parentError, coreFailure);
            AssertContains(parentError, cancellationFailure);
            Assert.Same(operation, await ErrorAsync(attach));
            Assert.Same(childError, await ReportAsync(child));
            Assert.Same(parentError, await ReportAsync(parent));
        }
        finally
        {
            cancellation.Open();
            await registration.DisposeAsync();
        }

        async Task<TlsConnection> ConnectAsync()
        {
            await connect.WaitAsync();
            return f.Video.Connection;
        }
    });

    [Fact(Timeout = TestTimeout)]
    public Task ReentrantProjectionQueue_ParentDisposeWaitsAcceptedProjectionHeldByScheduler() => RunAsync(async f =>
    {
        Task<AuthenticatedVideoSession>? published = null;
        Task? reentrantReport = null;
        ManualProjectionScheduler scheduler = f.NewScheduler(onQueue: task =>
        {
            published = AssertPublishedProjection(f, task);
            reentrantReport = f.Keep(f.Parent.DisposeAsync().AsTask());
            Assert.Same(reentrantReport, f.Parent.DisposeAsync().AsTask());
        });
        Task<AuthenticatedVideoSession> attach;
        // 只持有发布锁，让 QueueTask 的同线程重入确定性先于原 attach/monitor 开始。
        lock (f.ParentGate) attach = f.Start(scheduler: scheduler);
        Assert.Same(published, attach);
        Assert.Same(attach, f.PublicAttach);
        Task parent = Assert.IsAssignableFrom<Task>(reentrantReport);
        Assert.Same(parent, f.Parent.DisposeAsync().AsTask());
        f.ControlRead.Open();
        await SuccessAsync(f.Parent.CloseAndJoinAsync());
        Exception coreFailure = await ErrorAsync(f.OperationTask("_attach"));
        Task child = f.Keep(f.Child.PublicSession.DisposeAsync().AsTask());
        AggregateException childError = await ReportAsync(child);
        AssertContains(childError, coreFailure);
        Assert.Equal(0, scheduler.Executions);
        Assert.Equal(0, f.ConnectCalls);
        Assert.Empty(f.Control.Ssl.ReadRequests);
        f.AssertRevoked();
        await AssertAwaitPathAsync(scheduler.QueuedTask, attach);
        await AssertAwaitPathAsync(attach, parent);
        await AssertAwaitPathAsync(scheduler.QueuedTask, parent);
        scheduler.Release();
        Assert.Same(coreFailure, await ErrorAsync(attach));
        AssertContains(await ReportAsync(parent), coreFailure);
        Assert.Equal(1, scheduler.Executions);
        Assert.Same(child, f.Child.PublicSession.DisposeAsync().AsTask());
        Assert.Same(childError, await ReportAsync(child));
    });

    [Fact(Timeout = TestTimeout)]
    public Task FailedCore_ParentWaitsAcceptedProjection_WithoutAttachSelfJoin() => RunAsync(async f =>
    {
        ManualProjectionScheduler scheduler = f.NewScheduler();
        IOException failure = new("原连接失败。");
        Task<AuthenticatedVideoSession> attach = f.Start(_ => Task.FromException<TlsConnection>(failure), scheduler: scheduler);
        await SuccessAsync(scheduler.Queued.Task);
        AuthenticatedVideoSession prebuilt = f.Child.PublicSession;
        await ErrorAsync(f.OperationTask("_attach"));
        await SuccessAsync(f.ControlRead.Reached.Task);
        Task parentTask = f.Keep(f.Parent.DisposeAsync().AsTask());
        f.ControlRead.Open();
        // 先证明 raw join 已独立结束，再证明报告仍在等待精确挂起的 public 投影。
        await SuccessAsync(f.Parent.CloseAndJoinAsync());
        await AssertAwaitPathAsync(attach, parentTask);
        Task childReport = f.Keep(prebuilt.DisposeAsync().AsTask());
        AssertContains(await ReportAsync(childReport), failure);
        Assert.Same(childReport, prebuilt.DisposeAsync().AsTask());
        Assert.Equal(0, scheduler.Executions);
        await AssertAwaitPathAsync(attach, parentTask);
        scheduler.Release();
        Assert.Same(failure, await ErrorAsync(attach));
        AssertContains(await ReportAsync(parentTask), failure);
        AssertContains(await ReportAsync(prebuilt.DisposeAsync().AsTask()), failure);
    });

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public Task PublicRead_PreCancelledCaller_DoesNotRegisterOrReplaceOriginalRead(bool pendingRead) => RunAsync(async f =>
    {
        using CancellationTokenSource caller = new();
        AuthenticatedVideoSession video = await SuccessAsync(f.Start());
        Task<EncodedFrame?>? accepted = null;
        if (pendingRead)
        {
            f.VideoReadPause = f.NewPause();
            accepted = f.Read(video);
            await SuccessAsync(f.VideoReadPause.Reached.Task);
            await AssertAwaitPathAsync(f.OriginalVideoReads.Last(), accepted);
        }
        object? slot;
        lock (f.ParentGate) slot = Field<object?>(f.Child, "_read");
        if (!pendingRead) Assert.Null(slot);
        int reads = f.Video.Ssl.ReadRequests.Length;
        int timers = f.Clock.TimerCreates;
        int timestamps = f.Clock.TimestampReads;
        caller.Cancel();
        OperationCanceledException rejection = Assert.IsAssignableFrom<OperationCanceledException>(Record.Exception(() =>
        {
            _ = f.Read(video, caller.Token);
        }));
        Assert.Equal(caller.Token, rejection.CancellationToken);
        lock (f.ParentGate) Assert.Same(slot, Field<object?>(f.Child, "_read"));
        Assert.Equal(reads, f.Video.Ssl.ReadRequests.Length);
        Assert.Equal(timers, f.Clock.TimerCreates);
        Assert.Equal(timestamps, f.Clock.TimestampReads);
        Assert.False(f.ChildStopped);
        f.AssertControlLive();
        if (accepted is not null)
        {
            Assert.Same(accepted, f.OperationTask("_read"));
            await AssertAwaitPathAsync(f.OriginalVideoReads.Last(), accepted);
        }
        f.VideoReadPause?.Open();
        Task<EncodedFrame?> read = accepted ?? f.Read(video);
        EncodedFrame frame = Assert.IsType<EncodedFrame>(await SuccessAsync(read));
        Assert.Same(read, f.OperationTask("_read"));
        Assert.True(f.OperationCommitted("_read"));
        Assert.Equal(FramePayload(), frame.Payload.ToArray());
    });

    [Fact(Timeout = TestTimeout)]
    public Task PublicRead_ActiveCallerCancellation_BothReportsStillJoinHeldOriginalRead() => RunAsync(async f =>
    {
        using CancellationTokenSource caller = new();
        AuthenticatedVideoSession video = await SuccessAsync(f.Start());
        f.VideoReadPause = f.NewPause();
        Task<EncodedFrame?> read = f.Read(video, caller.Token);
        await SuccessAsync(f.VideoReadPause.Reached.Task);
        Task<int> original = f.OriginalVideoReads.Last();
        CancellationToken ioToken = f.Video.Ssl.ReadTokens.Last();
        Assert.Same(read, f.OperationTask("_read"));
        Assert.True(ioToken.CanBeCanceled);
        Assert.False(ioToken.IsCancellationRequested);
        await AssertAwaitPathAsync(original, read);
        caller.Cancel();
        Assert.True(f.ChildStopped);
        Task cancel;
        lock (f.ParentGate) cancel = Field<Task>(f.Child, "_cancel");
        await SuccessAsync(cancel);
        Assert.True(ioToken.IsCancellationRequested);
        f.AssertControlLive();
        Task child = f.Keep(video.DisposeAsync().AsTask());
        Task parent = f.Keep(f.Parent.DisposeAsync().AsTask());
        f.ControlRead.Open();
        await AssertAwaitPathAsync(original, read);
        await AssertAwaitPathAsync(original, child);
        await AssertAwaitPathAsync(original, parent);
        await AssertAwaitPathAsync(read, child);
        await AssertAwaitPathAsync(read, parent);
        Assert.Same(child, video.DisposeAsync().AsTask());
        Assert.Same(parent, f.Parent.DisposeAsync().AsTask());
        Assert.False(f.OperationCommitted("_read"));
        f.VideoReadPause.Open();
        await SuccessAsync(original);
        OperationCanceledException operation = Assert.IsAssignableFrom<OperationCanceledException>(await ErrorAsync(read));
        Assert.Equal(ioToken, operation.CancellationToken);
        Assert.True(read.IsCanceled);
        Assert.False(f.OperationCommitted("_read"));
        AggregateException childError = await ReportAsync(child);
        AggregateException parentError = await ReportAsync(parent);
        AssertContains(childError, operation);
        AssertContains(parentError, operation);
        Assert.Same(childError, await ReportAsync(child));
        Assert.Same(parentError, await ReportAsync(parent));
        f.Video.AssertClosedOnce();
    });

    [Fact(Timeout = TestTimeout)]
    public Task Read_ReturnsOriginalTask_AndCommittedFrameStaysCallerOwnedAfterBothJoins() => RunAsync(async f =>
    {
        RecordingOwner? owner = null;
        AuthenticatedVideoSession video = await SuccessAsync(f.Start(rent: length => owner = new(length)));
        Task<EncodedFrame?> read = f.Read(video);
        Assert.Same(read, f.OperationTask("_read"));
        EncodedFrame frame = Assert.IsType<EncodedFrame>(await SuccessAsync(read));
        Assert.True(f.OperationCommitted("_read"));
        RecordingOwner owned = Assert.IsType<RecordingOwner>(owner);
        Assert.Equal(0, owned.DisposeCalls);
        await SuccessAsync(f.ControlRead.Reached.Task);
        Task child = f.Keep(video.DisposeAsync().AsTask());
        Task parent = f.Keep(f.Parent.DisposeAsync().AsTask());
        f.ControlRead.Open();
        await SuccessAsync(Task.WhenAll(child, parent));
        Assert.Equal(0, owned.DisposeCalls);
        Assert.Equal(FramePayload(), frame.Payload.ToArray());
        frame.Dispose();
        Assert.Equal(1, owned.DisposeCalls);
        Assert.Same(child, video.DisposeAsync().AsTask());
        Assert.Same(parent, f.Parent.DisposeAsync().AsTask());
    });

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public Task ReaderReturned_ButJointCommitBlocked_StopDisposesOwnerExactlyOnce(bool parentStops) => RunAsync(async f =>
    {
        Pause commit = f.NewPause();
        RecordingOwner? owner = null;
        AuthenticatedVideoSession video = await SuccessAsync(f.Start(rent: length => owner = new(length), frameRead: frame =>
        {
            Assert.NotNull(frame);
            Assert.False(Monitor.IsEntered(f.ParentGate));
            commit.WaitSynchronously();
        }));
        Task<EncodedFrame?> read = f.Read(video);
        await SuccessAsync(commit.Reached.Task);
        RecordingOwner owned = Assert.IsType<RecordingOwner>(owner);
        Assert.Equal(0, owned.DisposeCalls);
        Assert.False(f.OperationCommitted("_read"));
        Task? parent = parentStops ? f.Keep(f.Parent.DisposeAsync().AsTask()) : null;
        Task child = f.Keep(video.DisposeAsync().AsTask());
        await AssertAwaitPathAsync(read, child);
        if (parent is not null) await AssertAwaitPathAsync(read, parent);
        commit.Open();
        Exception operation = await ErrorAsync(read);
        Assert.IsType<ObjectDisposedException>(operation);
        AssertContains(await ReportAsync(child), operation);
        if (parent is not null)
        {
            f.ControlRead.Open();
            AssertContains(await ReportAsync(parent), operation);
        }
        else f.AssertControlLive();
        // 断言位于夹具兜底释放之前，只能由生产未提交清理满足。
        Assert.Equal(1, owned.DisposeCalls);
        Assert.False(f.OperationCommitted("_read"));
    });

    [Fact(Timeout = TestTimeout)]
    public Task ChildStop_DoesNotStopControl_AndLaterParentFailureCannotChangeChildReport() => RunAsync(async f =>
    {
        AuthenticatedVideoSession video = await SuccessAsync(f.Start());
        await SuccessAsync(f.ControlRead.Reached.Task);
        Task child = f.Keep(video.DisposeAsync().AsTask());
        await SuccessAsync(child);
        f.AssertControlLive();
        await AssertAwaitPathAsync(f.OriginalControlRead, f.MonitorTask);
        IOException later = new("子报告成功之后的控制读失败。");
        f.ControlFailure = later;
        f.ControlRead.Open();
        await SuccessAsync(f.MonitorTask);
        f.AssertRevoked();
        AggregateException parent = await ReportAsync(f.Parent.DisposeAsync().AsTask());
        Assert.Same(later, Assert.Single(parent.InnerExceptions));
        Assert.Same(child, video.DisposeAsync().AsTask());
        await SuccessAsync(child);
        Assert.IsType<ObjectDisposedException>(Record.Exception(() => { _ = f.Read(video); }));
    });

    [Theory(Timeout = TestTimeout)]
    [InlineData("read")]
    [InlineData("prefix")]
    [InlineData("as-task")]
    public Task ParentDispose_JoinsMonitorOriginalRead_SynchronousPrefix_AndAsTask(string stage) => RunAsync(async f =>
    {
        Pause? prefix = stage == "read" ? null : f.NewPause();
        if (stage == "prefix") f.ControlPrefix = prefix;
        if (stage == "as-task") f.ControlAsTask = prefix;
        AuthenticatedVideoSession video = await SuccessAsync(f.Start());
        await SuccessAsync((prefix?.Reached ?? f.ControlRead.Reached).Task);
        Task parent = f.Keep(f.Parent.DisposeAsync().AsTask());
        Task child = f.Keep(video.DisposeAsync().AsTask());
        await SuccessAsync(child);
        await AssertAwaitPathAsync(f.MonitorTask, parent);
        prefix?.Open();
        await SuccessAsync(f.ControlRead.Reached.Task);
        if (stage == "as-task")
        {
            Task promise = await SuccessAsync(f.AsTaskPromise.Task);
            await AssertAwaitPathAsync(promise, parent);
            Assert.NotSame(f.OriginalControlRead, promise);
        }
        else await AssertAwaitPathAsync(f.OriginalControlRead, parent);
        Assert.Same(parent, f.Parent.DisposeAsync().AsTask());
        f.ControlRead.Open();
        await SuccessAsync(parent);
        f.Control.AssertClosedOnce();
        f.Video.AssertClosedOnce();
    });

    [Fact(Timeout = TestTimeout)]
    public Task VideoOriginalRead_IgnoresCloseAndCancellation_BothReportsMustJoinIt() => RunAsync(async f =>
    {
        AuthenticatedVideoSession video = await SuccessAsync(f.Start());
        f.VideoReadPause = f.NewPause();
        Task<EncodedFrame?> read = f.Read(video);
        await SuccessAsync(f.VideoReadPause.Reached.Task);
        Task<int> original = f.OriginalVideoReads.Last();
        Task child = f.Keep(video.DisposeAsync().AsTask());
        Task parent = f.Keep(f.Parent.DisposeAsync().AsTask());
        await AssertAwaitPathAsync(original, child);
        await AssertAwaitPathAsync(original, parent);
        Assert.Same(child, video.DisposeAsync().AsTask());
        Assert.Same(parent, f.Parent.DisposeAsync().AsTask());
        f.VideoReadPause.Open();
        Exception operation = await ErrorAsync(read);
        AssertContains(await ReportAsync(child), operation);
        f.ControlRead.Open();
        AssertContains(await ReportAsync(parent), operation);
    });

    [Fact(Timeout = TestTimeout)]
    public Task ConnectSynchronousPrefix_IsPublishedBeforeInvocation_AndParentJoinsLateConnection() => RunAsync(async f =>
    {
        Pause prefix = f.NewPause();
        Task<AuthenticatedVideoSession> attach = f.Start(_ =>
        {
            Assert.IsType<AuthenticatedVideoSession>(f.Child.PublicSession);
            Assert.NotNull(f.PublicAttach);
            prefix.WaitSynchronously();
            return Task.FromResult(f.Video.Connection);
        });
        await SuccessAsync(prefix.Reached.Task);
        Task parent = f.Keep(f.Parent.DisposeAsync().AsTask());
        await AssertAwaitPathAsync(f.OperationTask("_attach"), parent);
        prefix.Open();
        Exception operation = await ErrorAsync(attach);
        f.ControlRead.Open();
        AssertContains(await ReportAsync(parent), operation);
        Assert.Equal(0, f.Video.Ssl.WriteCalls);
        f.Video.AssertClosedOnce();
    });

    [Fact(Timeout = TestTimeout)]
    public Task SyncThenAsyncDispose_KeepsOneReportPerObject_AndJoinsBeyondSynchronousClose() => RunAsync(async f =>
    {
        AuthenticatedVideoSession video = await SuccessAsync(f.Start());
        await SuccessAsync(f.ControlRead.Reached.Task);
        f.Video.Ssl.ClosePause = f.NewPause();
        IOException synchronous = new("同步 Dispose 已观察，但异步报告不能消费掉的错误。");
        f.Control.Ssl.DisposeFailure = synchronous;
        Assert.Same(synchronous, Record.Exception(f.Parent.Dispose));
        await SuccessAsync(f.Video.Ssl.ClosePause.Reached.Task);
        f.Parent.Dispose();
        Task parent = f.Keep(f.Parent.DisposeAsync().AsTask());
        Task child = f.Keep(video.DisposeAsync().AsTask());
        Assert.Same(parent, f.Parent.DisposeAsync().AsTask());
        Assert.Same(child, video.DisposeAsync().AsTask());
        Assert.NotSame(parent, child);
        await AssertAwaitPathAsync(f.Video.Connection.CloseAsync(), child);
        await AssertAwaitPathAsync(f.OriginalControlRead, parent);
        f.Video.Ssl.ClosePause.Open();
        await SuccessAsync(child);
        await AssertAwaitPathAsync(f.OriginalControlRead, parent);
        f.ControlRead.Open();
        AggregateException report = await ReportAsync(parent);
        Assert.Same(synchronous, Assert.Single(report.InnerExceptions));
        Assert.Same(report, await ReportAsync(parent));
        Assert.Same(parent, f.Parent.DisposeAsync().AsTask());
        Assert.Same(child, video.DisposeAsync().AsTask());
    });

    [Fact(Timeout = TestTimeout)]
    public Task OriginalConnectMultiFault_PreservesAllBranchesAndRepeatedReferences_InEveryObservation() => RunAsync(async f =>
    {
        AggregateException branch = MixedRoot();
        ApplicationException sibling = new("原 Task 的独立兄弟。", new IOException("兄弟叶子。"));
        Task<TlsConnection> original = f.Keep(Task.Factory.StartNew(() =>
        {
            f.Keep(Task.Factory.StartNew(() => { throw branch; }, CancellationToken.None,
                TaskCreationOptions.AttachedToParent, TaskScheduler.Default));
            f.Keep(Task.Factory.StartNew(() => { throw sibling; }, CancellationToken.None,
                TaskCreationOptions.AttachedToParent, TaskScheduler.Default));
            return f.Video.Connection;
        }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default));
        Exception operation = await ErrorAsync(f.Start(_ => original));
        await ObserveAsync(original);
        AggregateException saved = Assert.IsType<AggregateException>(Assert.Single(f.Child.LifetimeErrors));
        AggregateException raw = original.Exception!;
        Assert.Equal(2, raw.InnerExceptions.Count);
        Assert.Equal(raw.InnerExceptions.Count, saved.InnerExceptions.Count);
        for (int i = 0; i < raw.InnerExceptions.Count; i++) Assert.Same(raw.InnerExceptions[i], saved.InnerExceptions[i]);
        AssertContains(saved, branch);
        AssertContains(saved, sibling);
        Assert.Same(saved, operation);
        AssertMixedTree(branch);
        Task childTask = f.Keep(f.Child.PublicSession.DisposeAsync().AsTask());
        AggregateException child = await ReportAsync(childTask);
        Assert.Same(saved, Assert.Single(child.InnerExceptions));
        await SuccessAsync(f.ControlRead.Reached.Task);
        Task parentTask = f.Keep(f.Parent.DisposeAsync().AsTask());
        f.ControlRead.Open();
        AggregateException parent = await ReportAsync(parentTask);
        Assert.Same(saved, Assert.Single(parent.InnerExceptions));
        Assert.Same(child, await ReportAsync(childTask));
        Assert.Same(parent, await ReportAsync(parentTask));
        Assert.Same(childTask, f.Child.PublicSession.DisposeAsync().AsTask());
        Assert.Same(parentTask, f.Parent.DisposeAsync().AsTask());
    });

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public Task ReportRoots_MergeContainmentInBothDirections_WithoutFlattening(bool parentRootFirst) => RunAsync(async f =>
    {
        AuthenticatedVideoSession video = await SuccessAsync(f.Start());
        await SuccessAsync(f.ControlRead.Reached.Task);
        AggregateException root = MixedRoot();
        Exception leaf = root.InnerExceptions[0];
        f.ControlFailure = parentRootFirst ? root : leaf;
        // TCP/SSL 清理按固定槽给出相反顺序，子报告也必须执行双向顶层包含归并。
        f.Video.Client.DisposeFailure = parentRootFirst ? root : leaf;
        f.Video.Ssl.DisposeFailure = parentRootFirst ? leaf : root;
        Task parentTask = f.Keep(f.Parent.DisposeAsync().AsTask());
        Task childTask = f.Keep(video.DisposeAsync().AsTask());
        f.ControlRead.Open();
        AggregateException child = await ReportAsync(childTask);
        Assert.Same(root, Assert.Single(child.InnerExceptions));
        AggregateException parent = await ReportAsync(parentTask);
        Assert.Same(root, Assert.Single(parent.InnerExceptions));
        AssertMixedTree(root);
    });

    [Fact(Timeout = TestTimeout)]
    public Task ChildAndParentReports_HaveDifferentScopes_AndNeitherConsumesOtherObservers() => RunAsync(async f =>
    {
        Pause monitorReturn = f.NewPause();
        AggregateException monitorBranch = MixedRoot();
        ObjectDisposedException monitorSibling = new("原 monitor Task 的独立兄弟");
        Task<int> original = f.Keep(Task.Factory.StartNew(() =>
        {
            monitorReturn.WaitSynchronously();
            f.Keep(Task.Factory.StartNew(() => { throw monitorBranch; }, CancellationToken.None,
                TaskCreationOptions.AttachedToParent, TaskScheduler.Default));
            f.Keep(Task.Factory.StartNew(() => { throw monitorSibling; }, CancellationToken.None,
                TaskCreationOptions.AttachedToParent, TaskScheduler.Default));
            return 0;
        }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default));
        f.ControlOverride = original;
        AuthenticatedVideoSession video = await SuccessAsync(f.Start());
        await SuccessAsync(monitorReturn.Reached.Task);
        await AssertAwaitPathAsync(original, f.MonitorTask);
        AggregateException videoError = MixedRoot();
        IOException controlClose = new("仅父持有的 Control close 错误。");
        f.Video.Ssl.DisposeFailure = videoError;
        f.Control.Client.DisposeFailure = controlClose;
        Task childTask = f.Keep(video.DisposeAsync().AsTask());
        AggregateException child = await ReportAsync(childTask);
        Assert.Same(videoError, Assert.Single(child.InnerExceptions));
        f.AssertControlLive();
        Task parentTask = f.Keep(f.Parent.DisposeAsync().AsTask());
        await AssertAwaitPathAsync(original, parentTask);
        monitorReturn.Open();
        AggregateException parent = await ReportAsync(parentTask);
        AggregateException monitorError = Assert.IsType<AggregateException>(f.Parent.ControlMonitorState!.Error);
        AggregateException raw = original.Exception!;
        Assert.Equal(2, raw.InnerExceptions.Count);
        Assert.Equal(raw.InnerExceptions.Count, monitorError.InnerExceptions.Count);
        for (int i = 0; i < raw.InnerExceptions.Count; i++) Assert.Same(raw.InnerExceptions[i], monitorError.InnerExceptions[i]);
        AssertContains(monitorError, monitorBranch);
        AssertContains(monitorError, monitorSibling);
        AssertMixedTree(monitorBranch);
        Assert.Equal(3, parent.InnerExceptions.Count);
        foreach (Exception error in new Exception[] { videoError, controlClose, monitorError })
            Assert.Contains(parent.InnerExceptions, candidate => ReferenceEquals(error, candidate));
        Assert.DoesNotContain(child.InnerExceptions, error => ReferenceEquals(error, controlClose) || ReferenceEquals(error, monitorError));
        Assert.Same(child, await ReportAsync(childTask));
        Assert.Same(parent, await ReportAsync(parentTask));
        Assert.Same(childTask, video.DisposeAsync().AsTask());
        Assert.Same(parentTask, f.Parent.DisposeAsync().AsTask());
    });

    [Theory(Timeout = TestTimeout)]
    [InlineData("io")]
    [InlineData("disposed")]
    [InlineData("cancelled")]
    public Task LocalStop_DoesNotSuppressIoDisposedOrCancellationFromOriginalMonitor(string kind) => RunAsync(async f =>
    {
        AuthenticatedVideoSession video = await SuccessAsync(f.Start());
        await SuccessAsync(f.ControlRead.Reached.Task);
        Exception original = kind switch
        {
            "io" => new IOException("主动关闭后仍须报告的 IO。"),
            "disposed" => new ObjectDisposedException("主动关闭后仍须报告的 ODE"),
            _ => new OperationCanceledException("主动关闭后仍须报告的 OCE。")
        };
        f.ControlFailure = original;
        Task parentTask = f.Keep(f.Parent.DisposeAsync().AsTask());
        await AssertAwaitPathAsync(f.OriginalControlRead, parentTask);
        Assert.True(f.Parent.ControlMonitorState!.LocalStopObservedBeforeCompletion);
        f.ControlRead.Open();
        AggregateException parent = await ReportAsync(parentTask);
        Exception saved = Assert.Single(parent.InnerExceptions);
        Assert.Same(original, saved);
        Assert.Same(await ErrorAsync(f.OriginalControlRead), saved);
        Assert.Same(f.Parent.ControlMonitorState!.Error, saved);
        await SuccessAsync(video.DisposeAsync().AsTask());
        Assert.Same(parentTask, f.Parent.DisposeAsync().AsTask());
        Assert.Same(parent, await ReportAsync(parentTask));
    });

    [Fact(Timeout = TestTimeout)]
    public Task FrameObserverAndOwnerCleanupFailures_BothSurviveOperationAndReports() => RunAsync(async f =>
    {
        AggregateException observer = MixedRoot();
        IOException cleanup = new("未提交 owner 的清理失败。");
        RecordingOwner? owner = null;
        AuthenticatedVideoSession video = await SuccessAsync(f.Start(
            rent: length => owner = new(length) { DisposeFailure = cleanup },
            frameRead: _ => throw observer));
        Exception operation = await ErrorAsync(f.Read(video));
        Assert.Same(observer, operation);
        AggregateException child = await ReportAsync(video.DisposeAsync().AsTask());
        Assert.Equal(2, child.InnerExceptions.Count);
        AssertContains(child, observer);
        AssertContains(child, cleanup);
        Assert.Equal(1, Assert.IsType<RecordingOwner>(owner).DisposeCalls);
        await SuccessAsync(f.ControlRead.Reached.Task);
        Task parentTask = f.Keep(f.Parent.DisposeAsync().AsTask());
        f.ControlRead.Open();
        AggregateException parent = await ReportAsync(parentTask);
        AssertContains(parent, observer);
        AssertContains(parent, cleanup);
        AssertMixedTree(observer);
    });

    [Fact(Timeout = TestTimeout)]
    public Task NoVideo_AsyncDisposeSucceeds_AndCachesReportWithoutStartingMonitor() => RunAsync(async f =>
    {
        Task first = f.Keep(f.Parent.DisposeAsync().AsTask());
        Assert.Same(first, f.Parent.DisposeAsync().AsTask());
        await SuccessAsync(first);
        Assert.Same(first, f.Parent.DisposeAsync().AsTask());
        Assert.Null(f.RegisteredChild);
        Assert.Null(f.PublicAttach);
        Assert.Null(f.Parent.ControlMonitorCompletion);
        Assert.Empty(f.Control.Ssl.ReadRequests);
        f.Control.AssertClosedOnce();
        Assert.False(f.Video.Connection.IsCloseRequested);
    });

    private static Task<AuthenticatedVideoSession> AssertPublishedProjection(Fixture fixture, Task barrier)
    {
        Task<AuthenticatedVideoSession> projection = Assert.IsAssignableFrom<Task<AuthenticatedVideoSession>>(fixture.PublicAttach);
        Assert.NotSame(barrier, projection);
        // QueueTask 尚未执行：public 原 async Task 必须已登记并真实 await 这个预建调度屏障。
        Assert.True(HasAwaitPath(barrier, projection, new HashSet<Task>(), 32),
            "投影必须先构造并登记再 Start，不能在调度后发布或用 TCS 桥接完成。");
        return projection;
    }

    private static Task<Child> OriginalProjectionAttachTask(Task<AuthenticatedVideoSession> projection)
    {
        object machine = Field<object>(projection, "StateMachine");
        return Assert.Single(machine.GetType().GetFields(Fields)
            .Select(field => field.GetValue(machine)).OfType<Task<Child>>().Distinct());
    }

    private static void AssertDefaultCancellation(MethodInfo method)
    {
        ParameterInfo token = Assert.Single(method.GetParameters());
        Assert.Equal(typeof(CancellationToken), token.ParameterType);
        Assert.True(token.IsOptional);
        Assert.True(token.HasDefaultValue);
        Assert.True(token.DefaultValue is null || Equals(default(CancellationToken), token.DefaultValue));
    }

    private static AggregateException MixedRoot()
    {
        IOException io = new("必须保留的 IO 叶子。");
        ObjectDisposedException disposed = new("必须保留的 ODE 叶子");
        InvalidOperationException wrapper = new("不允许 flatten 的中间包装。",
            new AggregateException("内部聚合。", io, disposed));
        return new AggregateException("原根及重复引用必须保留。", io, disposed, wrapper, wrapper);
    }

    private static void AssertMixedTree(AggregateException root)
    {
        Assert.Equal(4, root.InnerExceptions.Count);
        Assert.IsType<IOException>(root.InnerExceptions[0]);
        Assert.IsType<ObjectDisposedException>(root.InnerExceptions[1]);
        Exception wrapper = Assert.IsType<InvalidOperationException>(root.InnerExceptions[2]);
        Assert.Same(wrapper, root.InnerExceptions[3]);
        AggregateException inner = Assert.IsType<AggregateException>(wrapper.InnerException);
        Assert.Collection(inner.InnerExceptions,
            error => Assert.Same(root.InnerExceptions[0], error),
            error => Assert.Same(root.InnerExceptions[1], error));
    }

    private static IEnumerable<Exception> Tree(Exception error)
    {
        yield return error;
        if (error is AggregateException aggregate)
        {
            foreach (Exception child in aggregate.InnerExceptions)
                foreach (Exception nested in Tree(child)) yield return nested;
        }
        else if (error.InnerException is { } inner)
            foreach (Exception nested in Tree(inner)) yield return nested;
    }

    private static void AssertContains(Exception root, Exception expected) =>
        Assert.Contains(Tree(root), error => ReferenceEquals(error, expected));

    private static void RethrowTestFailure(Exception error)
    {
        if (Tree(error).OfType<XunitException>().FirstOrDefault() is { } failure)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static async Task RunAsync(Func<Fixture, Task> body, bool delivered = true, bool wrongIdentity = false)
    {
        Fixture fixture = new(delivered, wrongIdentity);
        Exception? primary = null;
        try { await body(fixture); }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            try { await fixture.FinishAsync(); }
            catch (Exception cleanup) when (primary is not null)
            {
                // 保留主 Assert 的类型和堆栈，清理 Guard 作为附加诊断，不覆盖主失败。
                primary.Data["AuthenticatedVideoSessionTests.CleanupFailure"] = cleanup;
            }
        }
    }

    private static async Task GuardCompletionAsync(Task task)
    {
        using CancellationTokenSource cancellation = new();
        Task guard = Task.Delay(Guard, cancellation.Token);
        try
        {
            if (!ReferenceEquals(task, await Task.WhenAny(task, guard).ConfigureAwait(false)))
                throw new XunitException("Guard：原任务未退出，不是业务超时或预期错误。");
        }
        finally { cancellation.Cancel(); }
    }

    private static async Task SuccessAsync(Task task)
    {
        await GuardCompletionAsync(task);
        await task;
    }

    private static async Task<T> SuccessAsync<T>(Task<T> task)
    {
        await GuardCompletionAsync(task);
        return await task;
    }

    private static async Task<Exception> ErrorAsync(Task task)
    {
        await GuardCompletionAsync(task);
        Exception? error = await Record.ExceptionAsync(() => task);
        Assert.NotNull(error);
        RethrowTestFailure(error);
        return error;
    }

    private static async Task<AggregateException> ReportAsync(Task task) =>
        Assert.IsType<AggregateException>(await ErrorAsync(task));

    private static async Task ObserveAsync(Task task)
    {
        await GuardCompletionAsync(task);
        try { await task; }
        catch (Exception error) { RethrowTestFailure(task.Exception ?? error); }
    }

    // 仅认可原 Task continuation 上的真实 await/Unwrap/WhenAll；不认可 WhenAny 或测试 TCS 桥接。
    private static async Task AssertAwaitPathAsync(Task awaited, Task returned)
    {
        using CancellationTokenSource cancellation = new();
        Task guard = Task.Delay(Guard, cancellation.Token);
        try
        {
            while (!HasAwaitPath(awaited, returned, new HashSet<Task>(), 32))
            {
                Assert.False(awaited.IsCompleted, "受闸原操作已退出，无法证明 join。");
                Assert.False(returned.IsCompleted, "报告提前退出，遗漏了原操作或投影。");
                if (guard.IsCompleted) throw new XunitException("Guard：没有真实 await 链，不能降级为瞬时 IsCompleted 断言。");
                await Task.Yield();
            }
        }
        finally { cancellation.Cancel(); }
    }

    private static bool HasAwaitPath(Task awaited, Task returned, HashSet<Task> visited, int remaining)
    {
        if (remaining == 0 || !visited.Add(awaited)) return false;
        foreach (object continuation in Continuations(awaited))
        {
            object? box = continuation is Delegate action ? action.Target : continuation;
            if (box is not Task && box is not null && RuntimeField(box.GetType(), "m_action")?.GetValue(box) is Delegate scheduled)
                box = scheduled.Target;
            if (box is not Task next) continue;
            object? machine = RuntimeField(next.GetType(), "StateMachine")?.GetValue(next);
            bool exact = machine is not null && machine.GetType().GetFields(Fields)
                .Where(field => field.Name.StartsWith("<>u__", StringComparison.Ordinal))
                .Select(field => field.GetValue(machine)).OfType<object>()
                .Any(awaiter => awaiter.GetType().GetFields(Fields).Any(field =>
                {
                    object? value = field.GetValue(awaiter);
                    if (value is Task task) return ReferenceEquals(task, awaited);
                    return (field.FieldType == typeof(ValueTask<int>) || field.FieldType == typeof(ValueTask))
                        && ReferenceEquals(RuntimeField(field.FieldType, "_obj")?.GetValue(value), awaited);
                }));
            string name = next.GetType().FullName ?? "";
            bool unwrap = name.StartsWith("System.Threading.Tasks.UnwrapPromise`", StringComparison.Ordinal);
            bool all = name.StartsWith("System.Threading.Tasks.Task+WhenAllPromise", StringComparison.Ordinal);
            if (!exact && !unwrap && !all) continue;
            if (ReferenceEquals(next, returned) || HasAwaitPath(next, returned, visited, remaining - 1)) return true;
        }
        return false;
    }

    private static object[] Continuations(Task task)
    {
        FieldInfo field = RuntimeField(typeof(Task), "m_continuationObject")
            ?? throw new XunitException("运行时缺少 Task continuation 字段，不能跳过 join 证明。");
        object? continuation = field.GetValue(task);
        if (continuation is IList list)
        {
            lock (list) return list.Cast<object?>().OfType<object>().ToArray();
        }
        return continuation is null ? [] : [continuation];
    }

    private static FieldInfo? RuntimeField(Type type, string name)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
            if (current.GetField(name, Fields | BindingFlags.DeclaredOnly) is { } field) return field;
        return null;
    }

    private static T Field<T>(object instance, string name) => (T)(RuntimeField(instance.GetType(), name)
        ?? throw new XunitException($"缺少只读观察字段 {instance.GetType().Name}.{name}。")).GetValue(instance)!;
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static byte[] Framed(string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] result = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)payload.Length);
        payload.CopyTo(result, 4);
        return result;
    }

    private static byte[] FramePayload() => [0x10, 0x20, 0x30, 0x40, 0x50];
    // 独立手工 wire，不调用生产 Writer；payload 是不透明字节，不宣称可解码 JPEG。
    private static byte[] FrameWire() => [.. Convert.FromHexString(
        "4C525646010100000123456789ABCDEF102030405060708000000780000004383C00000000000005"), .. FramePayload()];

    private static void AssertHello(ProbeSslStream ssl)
    {
        byte[] original = Assert.IsType<byte[]>(ssl.OriginalWire);
        Assert.All(original, value => Assert.Equal((byte)0, value));
        byte[] wire = Assert.IsType<byte[]>(ssl.WireSnapshot);
        Assert.Equal(212, wire.Length);
        Assert.Equal(208u, BinaryPrimitives.ReadUInt32BigEndian(wire));
        using JsonDocument document = JsonDocument.Parse(wire.AsMemory(4));
        JsonElement json = document.RootElement;
        Assert.Equal(6, json.EnumerateObject().Count());
        Assert.Equal("channel_hello", json.GetProperty("type").GetString());
        Assert.Equal("video", json.GetProperty("channel").GetString());
        Assert.Equal(1, json.GetProperty("protocol").GetInt32());
        Assert.Equal(SessionText, json.GetProperty("sessionId").GetString());
        byte[] nonce = Convert.FromBase64String(json.GetProperty("attachNonce").GetString()!);
        Assert.Equal(16, nonce.Length);
        byte[] transcript = [.. Encoding.ASCII.GetBytes("LANREMOTE-VIDEO-V1\0"),
            .. Convert.FromHexString(SessionText.Replace("-", "")), .. nonce, .. Convert.FromHexString(PinHex)];
        Assert.Equal(HMACSHA256.HashData(Convert.FromHexString(TokenHex), transcript),
            Convert.FromBase64String(json.GetProperty("attachProof").GetString()!));
    }

    private sealed class Pause
    {
        private readonly TaskCompletionSource _release = Signal();
        internal TaskCompletionSource Reached { get; } = Signal();
        internal void Open() => _release.TrySetResult();
        internal async Task WaitAsync()
        {
            Reached.TrySetResult();
            await _release.Task.ConfigureAwait(false);
        }
        internal void WaitSynchronously()
        {
            Reached.TrySetResult();
            try { _release.Task.WaitAsync(Guard).GetAwaiter().GetResult(); }
            catch (TimeoutException) { throw new XunitException("Guard：同步夹具闸门未释放，不是业务错误。"); }
        }
    }

    private sealed class ManualProjectionScheduler(Action<Task> onQueue,
        bool executeSynchronously, Exception? queueFailure) : TaskScheduler
    {
        private readonly object _gate = new();
        private Task? _task, _queuedTask;
        private bool _released;
        private int _executions;
        internal TaskCompletionSource Queued { get; } = Signal();
        internal Task QueuedTask { get { lock (_gate) return Assert.IsAssignableFrom<Task>(_queuedTask); } }
        internal int Executions => Volatile.Read(ref _executions);
        protected override IEnumerable<Task> GetScheduledTasks()
        {
            lock (_gate) return _task is { } task ? [task] : [];
        }
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
        protected override void QueueTask(Task task)
        {
            lock (_gate)
            {
                Assert.Null(_task);
                Assert.False(_released);
                _task = _queuedTask = task;
                Queued.TrySetResult();
            }
            try
            {
                onQueue(task);
                if (queueFailure is { } error) throw error;
                if (executeSynchronously) Release();
            }
            catch
            {
                // Start 会把原调度任务置为 faulted；兜底清理不能再次执行被拒绝的任务。
                lock (_gate) { _task = null; _released = true; }
                throw;
            }
        }
        internal void Release()
        {
            Task? task;
            lock (_gate)
            {
                if (_released) return;
                _released = true;
                task = _task;
                _task = null;
            }
            if (task is null) return;
            Interlocked.Increment(ref _executions);
            Assert.True(TryExecuteTask(task));
        }
    }

    private sealed class RecordingOwner(int length) : IMemoryOwner<byte>
    {
        private readonly byte[] _bytes = new byte[length];
        private int _disposeCalls;
        internal Exception? DisposeFailure;
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        public Memory<byte> Memory
        {
            get { ObjectDisposedException.ThrowIf(DisposeCalls != 0, this); return _bytes; }
        }
        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCalls);
            if (DisposeFailure is { } error) throw error;
        }
    }

    private sealed class ProbeClock : TimeProvider
    {
        private Action? _timestamp, _frequency;
        private int _reads, _timers;
        internal long Timestamp;
        internal bool RejectSampling;
        internal int TimestampReads => Volatile.Read(ref _reads);
        internal int TimerCreates => Volatile.Read(ref _timers);
        internal void Arm(string point, Action action)
        {
            if (point == "timestamp") _timestamp = action;
            else _frequency = action;
        }
        internal void Disarm() { _timestamp = _frequency = null; RejectSampling = false; }
        public override long GetTimestamp()
        {
            Interlocked.Increment(ref _reads);
            if (RejectSampling) throw new XunitException("已交付读取或 monitor 不得再采样 attach 时钟。");
            Interlocked.Exchange(ref _timestamp, null)?.Invoke();
            return Timestamp;
        }
        public override long TimestampFrequency
        {
            get
            {
                if (RejectSampling) throw new XunitException("已交付后不得再读取 attach 时钟频率。");
                Interlocked.Exchange(ref _frequency, null)?.Invoke();
                return TimeSpan.TicksPerSecond;
            }
        }
        public override DateTimeOffset GetUtcNow() => throw new XunitException("不得读取墙钟。");
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _timers);
            return new InertTimer();
        }
        private sealed class InertTimer : ITimer
        {
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class Fixture
    {
        private readonly ConcurrentBag<Task> _tasks = [];
        private readonly ConcurrentBag<EncodedFrame> _frames = [];
        private readonly List<Pause> _pauses = [];
        private readonly List<ManualProjectionScheduler> _schedulers = [];
        private readonly ConcurrentQueue<Task<int>> _videoReads = [];
        private readonly object _controlIoGate = new(), _videoIoGate = new();
        private readonly byte[] _callerToken = Convert.FromHexString(TokenHex);
        private readonly byte[] _ownedToken;
        private Task<int>? _controlOriginal;
        private int _videoOffset, _connectCalls;
        internal Endpoint Control { get; } = new();
        internal Endpoint Video { get; }
        internal ProbeClock Clock { get; } = new();
        internal AuthenticatedControlSession Parent { get; }
        internal Pause ControlRead { get; }
        internal Pause? ControlPrefix, ControlAsTask, VideoReadPause;
        internal Exception? ControlFailure;
        internal Task<int>? ControlOverride;
        internal byte[] VideoInput = [.. Framed(AckJson), .. FrameWire()];
        internal CancellationToken ConnectToken;
        internal TaskCompletionSource<Task> AsTaskPromise { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ConnectCalls => Volatile.Read(ref _connectCalls);
        internal int VideoOffset => Volatile.Read(ref _videoOffset);
        internal object ParentGate => Field<object>(Parent, "_gate");
        internal Child? RegisteredChild { get { lock (ParentGate) return Field<Child?>(Parent, "_videoLifetime"); } }
        internal Child Child => Assert.IsType<Child>(RegisteredChild);
        internal Task? PublicAttach { get { lock (ParentGate) return Field<Task?>(Parent, "_publicAttachTask"); } }
        internal Task MonitorTask => Assert.IsAssignableFrom<Task>(Parent.ControlMonitorCompletion);
        internal bool ChildStopped { get { lock (ParentGate) return Field<bool>(Child, "_stopped"); } }
        internal Task<int> OriginalControlRead { get { lock (_controlIoGate) return Assert.IsAssignableFrom<Task<int>>(_controlOriginal); } }
        internal Task<int>[] OriginalVideoReads { get { lock (_videoIoGate) return _videoReads.ToArray(); } }
        internal Task OperationTask(string name)
        {
            lock (ParentGate) return Assert.IsAssignableFrom<Task>(Field<Task?>(Field<object>(Child, name), "Worker"));
        }
        internal bool OperationCommitted(string name)
        {
            lock (ParentGate) return Field<bool>(Field<object>(Child, name), "Committed");
        }

        internal Fixture(bool delivered, bool wrongIdentity)
        {
            Video = new Endpoint(wrongIdentity);
            Parent = new AuthenticatedControlSession(Control.Connection, SessionPermission.Control,
                new Guid(SessionText), "ABCDEF", _callerToken, 15_000, 0, Clock);
            _ownedToken = Field<byte[]>(Parent, "_sessionToken");
            Assert.NotSame(_callerToken, _ownedToken);
            ControlRead = NewPause();
            Control.Ssl.ReadBody = ReadControl;
            Video.Ssl.ReadBody = (memory, _) =>
            {
                lock (_videoIoGate)
                {
                    Task<int> original = Keep(ReadVideoAsync(memory));
                    _videoReads.Enqueue(original);
                    return new ValueTask<int>(original);
                }
            };
            if (delivered) Parent.CommitDelivery();
        }

        internal T Keep<T>(T task) where T : Task { _tasks.Add(task); return task; }
        internal Pause NewPause() { Pause pause = new(); _pauses.Add(pause); return pause; }
        internal ManualProjectionScheduler NewScheduler(Action<Task>? onQueue = null,
            bool executeSynchronously = false, Exception? queueFailure = null)
        {
            ManualProjectionScheduler scheduler = new(task =>
            {
                _ = Keep(task);
                onQueue?.Invoke(task);
            }, executeSynchronously, queueFailure);
            _schedulers.Add(scheduler);
            return scheduler;
        }
        internal Task<EncodedFrame?> Read(AuthenticatedVideoSession video, CancellationToken cancellationToken = default) =>
            Keep(video.ReadFrameAsync(cancellationToken));
        internal Task<AuthenticatedVideoSession> Start(Func<CancellationToken, Task<TlsConnection>>? connect = null,
            Func<int, IMemoryOwner<byte>>? rent = null, Action<EncodedFrame?>? frameRead = null,
            TaskScheduler? scheduler = null, CancellationToken cancellationToken = default)
        {
            Task<TlsConnection> Connect(ConnectionTarget target, TransportTimeouts timeouts, TimeProvider clock, CancellationToken token)
            {
                Assert.False(Monitor.IsEntered(ParentGate));
                Assert.Same(Clock, clock);
                Assert.Same(TransportTimeouts.Default, timeouts);
                Assert.Equal(Parent.Identity.DeviceId, target.DeviceId);
                Assert.Equal(Parent.Identity.RemoteAddress, target.RemoteAddress);
                Assert.NotSame(Parent.Identity.RemoteAddress, target.RemoteAddress);
                Assert.Equal(Parent.Identity.Port, target.Port);
                Assert.Equal(Parent.Identity.ExpectedCertSha256.ToArray(), target.ExpectedCertSha256.ToArray());
                Interlocked.Increment(ref _connectCalls);
                ConnectToken = token;
                return Keep(connect is null ? Task.FromResult(Video.Connection) : connect(token));
            }
            void ObserveFrame(EncodedFrame? frame)
            {
                if (frame is not null) _frames.Add(frame);
                frameRead?.Invoke(frame);
            }
            return Keep(Parent.AttachPublicVideoForTestingAsync(Connect, cancellationToken,
                rent: rent, frameRead: ObserveFrame, projectionScheduler: scheduler));
        }

        private ValueTask<int> ReadControl(Memory<byte> memory, CancellationToken token)
        {
            Assert.False(Monitor.IsEntered(ParentGate));
            Assert.NotNull(RegisteredChild);
            Assert.NotNull(Parent.ControlMonitorCompletion);
            Assert.Equal(1, memory.Length);
            Assert.Equal(CancellationToken.None, token);
            ControlPrefix?.WaitSynchronously();
            lock (_controlIoGate)
            {
                Task<int> original = Keep(ControlOverride ?? ReadControlAsync());
                _controlOriginal = original;
                if (ControlAsTask is null) return new ValueTask<int>(original);
                BlockingValueTaskSource source = new(this, original, ControlAsTask);
                return new ValueTask<int>(source, source.Version);
            }
        }
        private async Task<int> ReadControlAsync()
        {
            // 故意忽略取消和 Dispose，只有原闸门开放才真正完成。
            await ControlRead.WaitAsync().ConfigureAwait(false);
            if (ControlFailure is { } error) throw error;
            return 0;
        }
        private async Task<int> ReadVideoAsync(Memory<byte> memory)
        {
            if (VideoReadPause is { } pause) await pause.WaitAsync().ConfigureAwait(false);
            int count = Math.Min(memory.Length, VideoInput.Length - _videoOffset);
            VideoInput.AsMemory(_videoOffset, count).CopyTo(memory);
            _videoOffset += count;
            return count;
        }
        internal void AssertControlLive()
        {
            Assert.Same(Control.Ssl, Parent.Stream);
            Assert.Equal(TokenHex, Convert.ToHexString(_ownedToken));
            Assert.False(Control.Connection.IsCloseRequested);
            Assert.Equal(0, Control.Client.DisposeCalls);
            Assert.Equal(0, Control.Ssl.DisposeCalls);
        }
        internal void AssertRevoked()
        {
            Assert.Same(_ownedToken, Field<byte[]>(Parent, "_sessionToken"));
            Assert.All(_ownedToken, value => Assert.Equal((byte)0, value));
            Assert.Equal(TokenHex, Convert.ToHexString(_callerToken));
            Assert.Throws<ObjectDisposedException>(() => { _ = Parent.Stream; });
        }

        internal async Task FinishAsync()
        {
            using CancellationTokenSource budget = new(CleanupGuard);
            Clock.Disarm();
            List<Exception> cleanup = [];
            ConcurrentQueue<Exception> drainErrors = [];
            int resourcesReleased = 0;
            foreach (Pause pause in _pauses) pause.Open();
            foreach (ManualProjectionScheduler scheduler in _schedulers)
            {
                try { scheduler.Release(); }
                catch (Exception error) { cleanup.Add(error); }
            }
            void Track(Func<Task> invoke)
            {
                try { Keep(invoke()); }
                catch (Exception error) { cleanup.Add(error); }
            }
            // 先请求所有分支，再批量等待；独立关闭未移交给 child 的 endpoint。
            Track(Parent.CloseAndJoinAsync);
            if (RegisteredChild is { } child)
            {
                Track(child.StopAndJoinAsync);
                Track(() => child.PublicSession.DisposeAsync().AsTask());
                Track(() => OperationTask("_attach"));
            }
            if (PublicAttach is { } attach) _ = Keep(attach);
            if (Parent.ControlMonitorCompletion is { } monitor) _ = Keep(monitor);
            Track(() => Parent.DisposeAsync().AsTask());
            Track(Control.Connection.CloseAsync);
            Track(Video.Connection.CloseAsync);

            Task drain = DrainAllAsync();
            try { await drain.WaitAsync(budget.Token); }
            catch (OperationCanceledException) when (budget.IsCancellationRequested)
            {
                Task[] pending = _tasks.Distinct().Where(task => !task.IsCompleted).ToArray();
                XunitException timeout = new($"夹具收尾 Guard：共享 {CleanupGuard.TotalSeconds} 秒预算耗尽，真实排空未完成；" +
                    $"尚未退出的任务：{string.Join(", ", pending.Select(task => $"{task.Id}({task.Status})"))}。");
                timeout.Data["PendingTasks"] = pending;
                timeout.Data["DrainTask"] = drain;
                timeout.Data["TrackedTasks"] = _tasks;
                timeout.Data["LateCleanupErrors"] = drainErrors;
                cleanup.Add(timeout);
                // 只结束测试的等待，不取消原任务或谎称 join；排空任务仍拥有迟到任务/帧的清理。
                ReleaseResources();
            }
            cleanup.AddRange(drainErrors);
            if (cleanup.Count != 0) throw new AggregateException("夹具收尾失败（不属于业务错误）。", cleanup);

            async Task DrainAllAsync()
            {
                HashSet<Task> joined = [];
                try
                {
                    while (true)
                    {
                        Task[] batch = _tasks.Where(task => !joined.Contains(task)).Distinct().ToArray();
                        if (batch.Length == 0) break;
                        Task all = Task.WhenAll(batch);
                        // WhenAll 即使 fault 也必须等所有原任务退出；业务错误留给测试报告断言。
                        await all.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                        foreach (Task task in batch)
                        {
                            joined.Add(task);
                            if (task.Exception is { } errors)
                                foreach (XunitException failure in Tree(errors).OfType<XunitException>())
                                    drainErrors.Enqueue(failure);
                        }
                        // 同步前缀/原 worker 退出后才能确认没有迟到登记，不能只等首次快照。
                    }
                }
                catch (Exception error) { drainErrors.Enqueue(error); }
                finally
                {
                    foreach (Task<EncodedFrame?> read in _tasks.OfType<Task<EncodedFrame?>>())
                    {
                        if (!read.IsCompletedSuccessfully) continue;
                        try { read.GetAwaiter().GetResult()?.Dispose(); }
                        catch (Exception error) { drainErrors.Enqueue(error); }
                    }
                    // 未成功返回的观察帧也须兜底；所有 owner 精确计数断言均发生在此之前。
                    foreach (EncodedFrame frame in _frames)
                    {
                        try { frame.Dispose(); }
                        catch (Exception error) { drainErrors.Enqueue(error); }
                    }
                    ReleaseResources();
                }
            }

            void ReleaseResources()
            {
                if (Interlocked.Exchange(ref resourcesReleased, 1) != 0) return;
                try { Control.ReleaseResources(); }
                catch (Exception error) { drainErrors.Enqueue(error); }
                try { Video.ReleaseResources(); }
                catch (Exception error) { drainErrors.Enqueue(error); }
            }
        }
    }

    private sealed class BlockingValueTaskSource : IValueTaskSource<int>
    {
        private ManualResetValueTaskSourceCore<int> _source = new() { RunContinuationsAsynchronously = true };
        private readonly Fixture _fixture;
        private readonly Pause _pause;
        private int _statusCalls;
        internal short Version => _source.Version;
        internal BlockingValueTaskSource(Fixture fixture, Task<int> original, Pause pause)
        {
            _fixture = fixture;
            _pause = pause;
            fixture.Keep(CompleteAsync(original));
        }
        private async Task CompleteAsync(Task<int> original)
        {
            try { _source.SetResult(await original.ConfigureAwait(false)); }
            catch (Exception error) { _source.SetException(error); }
        }
        public ValueTaskSourceStatus GetStatus(short token)
        {
            Assert.False(Monitor.IsEntered(_fixture.ParentGate));
            if (Interlocked.Increment(ref _statusCalls) == 1) _pause.WaitSynchronously();
            return _source.GetStatus(token);
        }
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            Task promise = Assert.IsAssignableFrom<Task>(state);
            Assert.Contains("ValueTaskSourceAsTask", promise.GetType().Name, StringComparison.Ordinal);
            _source.OnCompleted(continuation, state, token, flags);
            _fixture.AsTaskPromise.TrySetResult(promise);
        }
        public int GetResult(short token) => _source.GetResult(token);
    }

    // 正常构造 TlsConnection 的未连接 TCP/派生 SSL 夹具；不执行真实网络或 TLS 握手。
    private sealed class Endpoint
    {
        internal ProbeTcpClient Client { get; } = new();
        internal ProbeSslStream Ssl { get; } = new();
        internal TlsConnection Connection { get; }
        internal Endpoint(bool wrongIdentity = false)
        {
            Guid device = new(wrongIdentity ? "22222222-2222-3333-4444-555555555555" : "11111111-2222-3333-4444-555555555555");
            Assert.True(ConnectionTarget.TryCreate(device, IPAddress.Parse("192.168.50.7"), 12345, PinHex, out ConnectionTarget? target));
            Assert.True(ConnectionIdentity.TryCreate(target!, Convert.FromHexString(PinHex), out ConnectionIdentity? identity));
            Connection = new TlsConnection(identity!, Client, Ssl);
        }
        internal void AssertClosedOnce()
        {
            Assert.True(Connection.IsCloseRequested);
            Assert.True(Connection.CloseAsync().IsCompletedSuccessfully);
            Assert.Equal(1, Client.DisposeCalls);
            Assert.Equal(1, Ssl.DisposeCalls);
        }
        internal void ReleaseResources()
        {
            try { Ssl.ReleaseResources(); }
            finally { Client.ReleaseResources(); }
        }
    }

    private sealed class ProbeTcpClient : TcpClient
    {
        private int _disposeCalls;
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        internal Exception? DisposeFailure;
        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    Interlocked.Increment(ref _disposeCalls);
                    if (DisposeFailure is { } error) throw error;
                }
            }
            finally { base.Dispose(disposing); }
        }
        internal void ReleaseResources() => base.Dispose(true);
    }

    private sealed class ProbeSslStream() : SslStream(new MemoryStream(), leaveInnerStreamOpen: false)
    {
        private readonly ConcurrentQueue<int> _requests = [];
        private readonly ConcurrentQueue<CancellationToken> _tokens = [];
        private int _disposeCalls, _writeCalls, _flushCalls;
        internal Func<Memory<byte>, CancellationToken, ValueTask<int>> ReadBody = null!;
        internal Pause? ClosePause;
        internal Exception? DisposeFailure;
        internal byte[]? OriginalWire, WireSnapshot;
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        internal int WriteCalls => Volatile.Read(ref _writeCalls);
        internal int FlushCalls => Volatile.Read(ref _flushCalls);
        internal int[] ReadRequests => _requests.ToArray();
        internal CancellationToken[] ReadTokens => _tokens.ToArray();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _requests.Enqueue(buffer.Length);
            _tokens.Enqueue(cancellationToken);
            return ReadBody(buffer, cancellationToken);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _writeCalls);
            Assert.True(MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> segment));
            Assert.Equal(0, segment.Offset);
            OriginalWire = segment.Array;
            WireSnapshot = buffer.ToArray();
            return ValueTask.CompletedTask;
        }
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _flushCalls);
            return Task.CompletedTask;
        }
        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    Interlocked.Increment(ref _disposeCalls);
                    ClosePause?.WaitSynchronously();
                    if (DisposeFailure is { } error) throw error;
                }
            }
            finally { base.Dispose(disposing); }
        }
        internal void ReleaseResources() => base.Dispose(true);
    }
}
