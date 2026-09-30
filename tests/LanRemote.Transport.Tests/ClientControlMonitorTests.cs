using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks.Sources;
using LanRemote.Core.Models;
using Xunit.Sdk;
using Child = LanRemote.Transport.AuthenticatedControlSession.ClientVideoLifetime;

namespace LanRemote.Transport.Tests;

public sealed class ClientControlMonitorTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private const int TestTimeout = 60_000;
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string SessionText = "00112233-4455-6677-8899-aabbccddeeff";
    private const string TokenHex = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
    private const string PinHex = "808182838485868788898A8B8C8D8E8F909192939495969798999A9B9C9D9E9F";
    private const string AckJson = """{"type":"video_attach_ack","channel":"video","protocol":1,"sessionId":"00112233-4455-6677-8899-aabbccddeeff"}""";

    [Fact(Timeout = TestTimeout)]
    public async Task LegacyEntry_NeverRegistersMonitorOrReadsControl_EvenAfterChildAndParentStop()
    {
        Fixture f = new();
        try
        {
            Child child = await f.Start(monitored: false).WaitAsync(Guard);
            f.AssertNoMonitor();
            Assert.Equal(1, f.Clock.TimerCreates);
            await child.StopAndJoinAsync().WaitAsync(Guard);
            f.AssertControlLive();
            f.AssertNoMonitor();
            await f.Parent.CloseAndJoinAsync().WaitAsync(Guard);
            f.AssertNoMonitor();
            f.AssertRevoked();
            Assert.Empty(f.Control.Ssl.ReadRequests);
            Assert.Equal(0, f.Control.Ssl.WriteCalls);
            Assert.Equal(0, f.Control.Ssl.FlushCalls);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("cancelled", false)]
    [InlineData("cancelled", true)]
    [InlineData("undelivered", false)]
    [InlineData("undelivered", true)]
    [InlineData("revoked", false)]
    [InlineData("revoked", true)]
    [InlineData("expired", false)]
    [InlineData("expired", true)]
    public async Task ExplicitEntry_RejectedBeforeRegistration_HasNoMonitor(string reason, bool defaultConnector)
    {
        Fixture f = new(delivered: reason != "undelivered");
        using CancellationTokenSource caller = new();
        try
        {
            if (reason == "cancelled") caller.Cancel();
            if (reason == "revoked") f.Parent.RevokeForOwnerCleanup();
            if (reason == "expired") f.Clock.Timestamp = TimeSpan.FromSeconds(15).Ticks;
            Exception error = await InvocationErrorAsync(() => defaultConnector
                ? f.Keep(f.Parent.AttachVideoWithControlMonitorCoreAsync(caller.Token))
                : f.Start(token: caller.Token));
            if (reason == "cancelled")
                Assert.Equal(caller.Token, Assert.IsAssignableFrom<OperationCanceledException>(error).CancellationToken);
            else if (reason == "revoked") Assert.IsType<ObjectDisposedException>(error);
            else if (reason == "expired") Assert.IsType<TimeoutException>(error);
            else Assert.IsType<InvalidOperationException>(error);
            f.AssertNoMonitor();
            Assert.Null(f.RegisteredChild);
            Assert.Equal(0, f.ConnectCalls);
            Assert.Equal(0, f.Clock.TimerCreates);
            Assert.False(f.Control.Connection.IsCloseRequested);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task NullFactory_DoesNotConsumeAttempt_AndBothOverloadsStayInternal()
    {
        Fixture f = new();
        try
        {
            MethodInfo[] entries = typeof(AuthenticatedControlSession).GetMethods(Fields)
                .Where(method => method.Name == "AttachVideoWithControlMonitorCoreAsync").ToArray();
            Assert.Equal(2, entries.Length);
            Assert.All(entries, method =>
            {
                Assert.True(method.IsAssembly);
                Assert.Equal(typeof(Task<Child>), method.ReturnType);
                ParameterInfo token = method.GetParameters()[^1];
                Assert.Equal(typeof(CancellationToken), token.ParameterType);
                Assert.True(token.IsOptional);
            });
            MethodInfo factory = Assert.Single(entries, method => method.GetParameters().Length == 2);
            Assert.Equal(typeof(Func<ConnectionTarget, TransportTimeouts, TimeProvider, CancellationToken, Task<TlsConnection>>),
                factory.GetParameters()[0].ParameterType);
            Assert.Single(entries, method => method.GetParameters().Length == 1);
            Assert.IsType<ArgumentNullException>(await InvocationErrorAsync(() =>
                f.Keep(f.Parent.AttachVideoWithControlMonitorCoreAsync(null!))));
            f.AssertNoMonitor();
            Assert.Null(f.RegisteredChild);
            Assert.Equal(0, f.Clock.TimestampReads);
            Child child = await f.Start().WaitAsync(Guard);
            Assert.Same(child, f.Child);
            await f.ControlRead.Reached.Task.WaitAsync(Guard);
            AssertReadContract(f);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact]
    public void FrameTestingEntry_IsInternal_AndKeepsBothOrdinaryMonitorOverloadsUnchanged()
    {
        MethodInfo[] methods = typeof(AuthenticatedControlSession).GetMethods(Fields);
        Type factoryType = typeof(Func<ConnectionTarget, TransportTimeouts, TimeProvider,
            CancellationToken, Task<TlsConnection>>);
        MethodInfo testing = Assert.Single(methods,
            method => method.Name == "AttachVideoWithControlMonitorForTestingAsync");
        Assert.True(testing.IsAssembly);
        Assert.Equal(typeof(Task<Child>), testing.ReturnType);
        ParameterInfo[] parameters = testing.GetParameters();
        Assert.Equal(new[] { factoryType, typeof(Func<int, IMemoryOwner<byte>>),
            typeof(Action<EncodedFrame?>), typeof(CancellationToken) },
            parameters.Select(parameter => parameter.ParameterType).ToArray());
        Assert.All(parameters.Take(3), parameter => Assert.False(parameter.IsOptional));
        Assert.True(parameters[3].IsOptional);
        Assert.True(parameters[3].HasDefaultValue);

        MethodInfo[] ordinary = methods.Where(method => method.Name == "AttachVideoWithControlMonitorCoreAsync").ToArray();
        Assert.Equal(2, ordinary.Length);
        Assert.All(ordinary, method =>
        {
            Assert.True(method.IsAssembly);
            Assert.Equal(typeof(Task<Child>), method.ReturnType);
            ParameterInfo token = method.GetParameters()[^1];
            Assert.Equal(typeof(CancellationToken), token.ParameterType);
            Assert.True(token.IsOptional);
            Assert.True(token.HasDefaultValue);
        });
        MethodInfo defaultEntry = Assert.Single(ordinary, method => method.GetParameters().Length == 1);
        Assert.Equal(typeof(CancellationToken), Assert.Single(defaultEntry.GetParameters()).ParameterType);
        MethodInfo factoryEntry = Assert.Single(ordinary, method => method.GetParameters().Length == 2);
        Assert.Equal(factoryType, factoryEntry.GetParameters()[0].ParameterType);
        Assert.False(factoryEntry.GetParameters()[0].IsOptional);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ExplicitEntry_RegistersBeforeIo_ReadsExactlyOneByteWithNone_AndAddsNoTimer()
    {
        Fixture f = new();
        try
        {
            await f.Start().WaitAsync(Guard);
            await f.ControlRead.Reached.Task.WaitAsync(Guard);
            Task monitor = f.MonitorTask;
            await AssertAwaitPathAsync(f.OriginalControlRead, monitor);
            object before = f.Snapshot;
            AssertSnapshot(before, "Pending", false, null);
            AssertSnapshotShape(before);
            AssertReadContract(f);
            Assert.Equal(1, f.Clock.TimerCreates);
            int samples = f.Clock.TimestampReads;
            f.Clock.RejectSampling = true;

            f.ControlRead.Open();
            await ObserveAsync(monitor);
            Assert.Same(monitor, f.MonitorTask);
            AssertSnapshot(f.Snapshot, "EndOfStream", false, null);
            AssertSnapshot(before, "Pending", false, null);
            Assert.NotSame(before, f.Snapshot);
            await f.Parent.CloseAndJoinAsync().WaitAsync(Guard);
            AssertReadContract(f);
            Assert.Equal(samples, f.Clock.TimestampReads);
            Assert.Equal(1, f.Clock.TimerCreates);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Registration_StartsMonitorWhileConnectIsPending_AndTerminalWorkerDoesNotJoinParent()
    {
        Fixture f = new();
        Pause connectReturn = f.NewPause();
        Task<TlsConnection> originalConnect = f.Keep(ConnectAsync());
        try
        {
            Task<Child> attach = f.Start(connect: _ => originalConnect);
            await f.Connecting.Task.WaitAsync(Guard);
            await f.ControlRead.Reached.Task.WaitAsync(Guard);
            await AssertAwaitPathAsync(originalConnect, attach);
            await AssertAwaitPathAsync(f.OriginalControlRead, f.MonitorTask);
            f.ControlRead.Open();
            // connect 仍被非合作原操作卡住；monitor 必须仅请求 stop，不能等待父/子 join。
            await ObserveAsync(f.MonitorTask);
            f.AssertRevoked();
            AssertSnapshot(f.Snapshot, "EndOfStream", false, null);
            Assert.True(f.Control.Connection.IsCloseRequested);
            await f.ChildTask("_cancel").WaitAsync(Guard);
            Assert.True(f.ConnectToken.IsCancellationRequested);
            Assert.False(f.Video.Connection.IsCloseRequested);
            Task join = f.Keep(f.Parent.CloseAndJoinAsync());
            await AssertAwaitPathAsync(originalConnect, join);
            connectReturn.Open();
            Assert.Same(f.Video.Connection, await originalConnect.WaitAsync(Guard));
            Assert.IsType<ObjectDisposedException>(await ErrorAsync(attach));
            await join.WaitAsync(Guard);
            f.Control.AssertClosedOnce();
            f.Video.AssertClosedOnce();
            Assert.Equal(0, f.Video.Ssl.WriteCalls);
            AssertReadContract(f);
        }
        finally { await f.FinishAsync(); }

        async Task<TlsConnection> ConnectAsync()
        {
            await connectReturn.WaitAsync().ConfigureAwait(false);
            return f.Video.Connection;
        }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("eof")]
    [InlineData("data")]
    [InlineData("io")]
    [InlineData("disposed")]
    [InlineData("nested")]
    public async Task ControlTerminal_RecordsExactOutcome_RevokesToken_AndStopsDeliveredChild(string outcome)
    {
        Fixture f = new();
        Exception? failure = outcome switch
        {
            "io" => new IOException("Control 原读取失败。"),
            "disposed" => new ObjectDisposedException("Control 原读取"),
            "nested" => MixedRoot(),
            _ => null,
        };
        f.ControlFailure = failure;
        f.ControlResult = outcome == "data" ? 1 : 0;
        try
        {
            Child child = await f.Start().WaitAsync(Guard);
            await f.ControlRead.Reached.Task.WaitAsync(Guard);
            await AssertAwaitPathAsync(f.OriginalControlRead, f.MonitorTask);
            f.ControlRead.Open();
            await ObserveAsync(f.MonitorTask);
            await ObserveAsync(f.OriginalControlRead);
            f.AssertRevoked();
            Assert.True(f.ChildStopped);
            Assert.True(f.Control.Connection.IsCloseRequested);
            Assert.True(f.Video.Connection.IsCloseRequested);
            await f.ChildTask("_cancel").WaitAsync(Guard);
            Assert.True(f.ConnectToken.IsCancellationRequested);
            Assert.IsType<ObjectDisposedException>(await InvocationErrorAsync(() => f.Keep(child.ReadFrameAsync())));
            if (outcome == "data")
            {
                Exception actual = Assert.IsType<FrameProtocolException>(SnapshotError(f.Snapshot));
                Assert.Equal("control-unexpected-data", ((FrameProtocolException)actual).Reason);
                AssertSnapshot(f.Snapshot, "UnexpectedData", false, actual);
            }
            else AssertSnapshot(f.Snapshot, failure is null ? "EndOfStream" : "Faulted", false, failure);
            object terminal = f.Snapshot;
            await ObserveAsync(f.Parent.CloseAndJoinAsync());
            AssertSnapshot(f.Snapshot, PropertyValue(terminal, "End")!.ToString()!, false, SnapshotError(terminal));
            f.Control.AssertClosedOnce();
            f.Video.AssertClosedOnce();
            AssertReadContract(f);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("connect")]
    [InlineData("ack")]
    [InlineData("frame")]
    [InlineData("child-stop")]
    public async Task VideoFailureOrChildStop_DoesNotRevokeControlOrStopItsMonitor(string stage)
    {
        Fixture f = new();
        IOException videoFailure = new("普通视频失败不能停止父 Control 监测。 ");
        if (stage == "ack") f.VideoInput = Framed(AckJson.Replace("video_attach_ack", "auth_success"));
        try
        {
            Task<Child> attach = f.Start(connect: stage == "connect" ? _ => throw videoFailure : null);
            if (stage == "connect") Assert.Same(videoFailure, await ErrorAsync(attach));
            else if (stage == "ack") Assert.IsType<FrameProtocolException>(await ErrorAsync(attach));
            else
            {
                Child child = await attach.WaitAsync(Guard);
                if (stage == "frame")
                {
                    f.VideoFailure = videoFailure;
                    Assert.Same(videoFailure, await ErrorAsync(f.Keep(child.ReadFrameAsync())));
                }
                await child.StopAndJoinAsync().WaitAsync(Guard);
            }
            await f.ControlRead.Reached.Task.WaitAsync(Guard);
            Task monitor = f.MonitorTask;
            await AssertAwaitPathAsync(f.OriginalControlRead, monitor);
            Assert.True(f.ChildStopped);
            f.AssertControlLive();
            AssertSnapshot(f.Snapshot, "Pending", false, null);
            AssertReadContract(f);
            if (stage != "connect") f.Video.AssertClosedOnce();
            f.ControlRead.Open();
            await ObserveAsync(monitor);
            AssertSnapshot(f.Snapshot, "EndOfStream", false, null);
            f.AssertRevoked();
            Assert.Same(monitor, f.MonitorTask);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task OldAndNewEntryLosers_CannotRegisterMonitorOrStopWinner(
        bool monitoredWinner, bool monitoredLoser, bool deliveredWinner)
    {
        Fixture f = new();
        Pause connectReturn = f.NewPause();
        Task<TlsConnection> original = f.Keep(ConnectAsync());
        try
        {
            Task<Child> winner = f.Start(monitoredWinner, _ => original);
            await f.Connecting.Task.WaitAsync(Guard);
            if (deliveredWinner)
            {
                connectReturn.Open();
                await winner.WaitAsync(Guard);
            }
            Child registered = f.Child;
            Task? monitor = f.OptionalMonitor;
            int calls = f.ConnectCalls;
            Assert.IsType<InvalidOperationException>(await InvocationErrorAsync(() => f.Start(monitoredLoser)));
            Assert.Equal(calls, f.ConnectCalls);
            Assert.Same(registered, f.Child);
            Assert.Same(monitor, f.OptionalMonitor);
            Assert.False(f.ChildStopped);
            Assert.False(f.ConnectToken.IsCancellationRequested);
            Assert.False(f.Video.Connection.IsCloseRequested);
            Assert.Empty(registered.LifetimeErrors);
            f.AssertControlLive();
            if (monitoredWinner)
            {
                await f.ControlRead.Reached.Task.WaitAsync(Guard);
                await AssertAwaitPathAsync(f.OriginalControlRead, f.MonitorTask);
                AssertReadContract(f);
            }
            else f.AssertNoMonitor();
            connectReturn.Open();
            Assert.Same(registered, await winner.WaitAsync(Guard));
        }
        finally { await f.FinishAsync(); }

        async Task<TlsConnection> ConnectAsync()
        {
            await connectReturn.WaitAsync().ConfigureAwait(false);
            return f.Video.Connection;
        }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("timestamp", false, true)]
    [InlineData("timestamp", true, false)]
    [InlineData("timestamp", true, true)]
    [InlineData("frequency", false, true)]
    [InlineData("frequency", true, false)]
    [InlineData("frequency", true, true)]
    public async Task ReentrantClock_OnlyInnerWinnerMayOwnMonitor(string clockPoint, bool monitoredWinner, bool monitoredLoser)
    {
        Fixture f = new();
        Pause connectReturn = f.NewPause();
        Task<TlsConnection> original = f.Keep(ConnectAsync());
        Task<Child>? winner = null;
        int losingConnectCalls = 0;
        try
        {
            f.Clock.Arm(clockPoint, () => winner = f.Start(monitoredWinner, _ => original));
            Assert.IsType<InvalidOperationException>(await InvocationErrorAsync(() => f.Start(monitoredLoser, _ =>
            {
                Interlocked.Increment(ref losingConnectCalls);
                return Task.FromResult(f.Video.Connection);
            })));
            Assert.NotNull(winner);
            await f.Connecting.Task.WaitAsync(Guard);
            Assert.Equal(0, losingConnectCalls);
            Assert.Equal(1, f.ConnectCalls);
            Assert.False(f.ChildStopped);
            Assert.False(f.ConnectToken.IsCancellationRequested);
            f.AssertControlLive();
            await AssertAwaitPathAsync(original, winner);
            if (monitoredWinner)
            {
                await f.ControlRead.Reached.Task.WaitAsync(Guard);
                await AssertAwaitPathAsync(f.OriginalControlRead, f.MonitorTask);
                AssertReadContract(f);
            }
            else f.AssertNoMonitor();
            connectReturn.Open();
            Assert.Same(f.Child, await winner.WaitAsync(Guard));
        }
        finally { await f.FinishAsync(); }

        async Task<TlsConnection> ConnectAsync()
        {
            await connectReturn.WaitAsync().ConfigureAwait(false);
            return f.Video.Connection;
        }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("original-read")]
    [InlineData("sync-prefix")]
    [InlineData("as-task")]
    public async Task ParentJoin_WaitsForNonCooperativeRead_SynchronousPrefix_AndAsTask(string blockedStage)
    {
        Fixture f = new();
        Pause? invocation = blockedStage == "original-read" ? null : f.NewPause();
        if (blockedStage == "sync-prefix") f.ControlPrefix = invocation;
        if (blockedStage == "as-task") f.ControlAsTask = invocation;
        try
        {
            await f.Start().WaitAsync(Guard);
            if (invocation is not null) await invocation.Reached.Task.WaitAsync(Guard);
            else
            {
                await f.ControlRead.Reached.Task.WaitAsync(Guard);
                await AssertAwaitPathAsync(f.OriginalControlRead, f.MonitorTask);
            }
            Task monitor = f.MonitorTask;
            Task join = f.Keep(f.Parent.CloseAndJoinAsync());
            Assert.Same(join, f.Parent.CloseAndJoinAsync());
            f.AssertRevoked();
            Assert.True(f.Control.Connection.IsCloseRequested);
            Assert.True(f.Video.Connection.IsCloseRequested);
            await Task.WhenAll(f.Control.Connection.CloseAsync(), f.Video.Connection.CloseAsync(),
                f.ChildTask("_join")).WaitAsync(Guard);
            // 两连接、child 原 join 均已退出；正向 await 链证明不是瞬时 IsCompleted 采样。
            await AssertAwaitPathAsync(monitor, join);
            AssertSnapshot(f.Snapshot, "Pending", true, null);
            invocation?.Open();
            await f.ControlRead.Reached.Task.WaitAsync(Guard);
            if (blockedStage == "as-task")
            {
                // ValueTaskSource 本身没有 Task 后端；AsTask 的真实承诺任务仍由 monitor await。
                Task asTask = await f.AsTaskPromise.Task.WaitAsync(Guard);
                await AssertAwaitPathAsync(asTask, monitor);
                await AssertAwaitPathAsync(asTask, join);
            }
            else
            {
                await AssertAwaitPathAsync(f.OriginalControlRead, monitor);
                await AssertAwaitPathAsync(f.OriginalControlRead, join);
            }
            AssertReadContract(f);
            f.ControlRead.Open();
            await ObserveAsync(f.OriginalControlRead);
            await ObserveAsync(monitor);
            await join.WaitAsync(Guard);
            AssertSnapshot(f.Snapshot, "EndOfStream", true, null);
            f.Control.AssertClosedOnce();
            f.Video.AssertClosedOnce();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LocalStopObservation_UsesOriginalTaskCompletion_NotDelayedGateContinuation_AndPreservesTree(
        bool stopBeforeFault, bool multipleTaskRoots)
    {
        Fixture f = new();
        Pause faultReturn = f.NewPause();
        AggregateException branch = MixedRoot();
        ApplicationException sibling = new("独立原任务分支。", new IOException("独立叶子。"));
        Task<int> original = multipleTaskRoots
            ? MultiFaultRead(f, faultReturn, branch, sibling)
            : f.Keep(SingleFaultAsync());
        f.ControlOverride = original;
        try
        {
            await f.Start().WaitAsync(Guard);
            await AssertAwaitPathAsync(original, f.MonitorTask);
            object before = f.Snapshot;
            AssertSnapshot(before, "Pending", false, null);
            object stopped;
            if (stopBeforeFault)
            {
                f.Parent.RevokeForOwnerCleanup();
                stopped = f.Snapshot;
                AssertSnapshot(stopped, "Pending", true, null);
                f.AssertRevokedWithoutClose();
                faultReturn.Open();
            }
            else
            {
                lock (f.ParentGate)
                {
                    faultReturn.Open();
                    // 原 Task 在独立 worker 上真正 fault；monitor 的结局 continuation 无法越过同一 gate。
                    ObserveSynchronously(original);
                    Assert.True(original.IsFaulted);
                    AssertSnapshot(f.Snapshot, "Pending", false, null);
                    f.Parent.RevokeForOwnerCleanup();
                    stopped = f.Snapshot;
                    AssertSnapshot(stopped, "Pending", false, null);
                    f.AssertRevokedWithoutClose();
                }
            }
            await ObserveAsync(original);
            await ObserveAsync(f.MonitorTask);
            Exception saved = Assert.IsAssignableFrom<Exception>(SnapshotError(f.Snapshot));
            AssertOriginalRoot(original, saved);
            if (multipleTaskRoots)
            {
                Assert.Equal(2, Assert.IsType<AggregateException>(saved).InnerExceptions.Count);
                Assert.True(ContainsReference(saved, sibling));
            }
            else Assert.Same(branch, saved);
            Assert.True(ContainsReference(saved, branch));
            Assert.Collection(branch.InnerExceptions,
                error => Assert.IsType<IOException>(error),
                error => Assert.IsType<ObjectDisposedException>(error),
                error => Assert.IsType<InvalidOperationException>(error),
                error => Assert.Same(branch.InnerExceptions[2], error));
            AssertSnapshot(f.Snapshot, "Faulted", stopBeforeFault, saved);
            AssertSnapshot(before, "Pending", false, null);
            AssertSnapshot(stopped, "Pending", stopBeforeFault, null);
            Assert.NotSame(stopped, f.Snapshot);
            await ObserveAsync(f.Parent.CloseAndJoinAsync());
            Assert.Same(saved, Assert.Single(f.Parent.LifetimeErrors));
            AssertOriginalRoot(original, Assert.Single(f.Parent.LifetimeErrors));
            AssertReadContract(f);
        }
        finally { await f.FinishAsync(); }

        async Task<int> SingleFaultAsync()
        {
            await faultReturn.WaitAsync().ConfigureAwait(false);
            throw branch;
        }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("read-prefix")]
    [InlineData("as-task")]
    public async Task SynchronousInvocationFault_AfterLocalStop_IsStillSavedInFull(string stage)
    {
        Fixture f = new();
        Pause invocation = f.NewPause();
        AggregateException original = MixedRoot();
        if (stage == "read-prefix")
        {
            f.ControlPrefix = invocation;
            f.ControlPrefixFailure = original;
        }
        else
        {
            f.ControlAsTask = invocation;
            f.ControlAsTaskFailure = original;
        }
        try
        {
            await f.Start().WaitAsync(Guard);
            await invocation.Reached.Task.WaitAsync(Guard);
            f.Parent.RevokeForOwnerCleanup();
            f.AssertRevokedWithoutClose();
            AssertSnapshot(f.Snapshot, "Pending", true, null);
            Task join = f.Keep(f.Parent.CloseAndJoinAsync());
            await AssertAwaitPathAsync(f.MonitorTask, join);
            invocation.Open();
            await ObserveAsync(f.MonitorTask);
            AssertSnapshot(f.Snapshot, "Faulted", true, original);
            await ObserveAsync(join);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("control-close")]
    [InlineData("child-read")]
    [InlineData("monitor")]
    public async Task ParentJoin_DrainsEveryBranchDespiteIoAndCleanupFailures(string lastBranch)
    {
        Fixture f = new();
        Pause controlClose = f.NewPause(), videoClose = f.NewPause(), videoRead = f.NewPause();
        IOException controlFailure = new("Control 原读取错误。"), videoFailure = new("视频原读取错误。");
        IOException tcpFailure = new("Control TCP 关闭错误。"), sslFailure = new("Control SSL 关闭错误。");
        ApplicationException videoCloseFailure = new("视频 SSL 关闭错误。");
        f.ControlFailure = controlFailure;
        f.Control.Ssl.ClosePause = controlClose;
        f.Video.Ssl.ClosePause = videoClose;
        f.Control.Client.DisposeFailure = tcpFailure;
        f.Control.Ssl.DisposeFailure = sslFailure;
        f.Video.Ssl.DisposeFailure = videoCloseFailure;
        try
        {
            Child child = await f.Start().WaitAsync(Guard);
            f.VideoReadPause = videoRead;
            f.VideoFailure = videoFailure;
            Task<EncodedFrame?> read = f.Keep(child.ReadFrameAsync());
            await videoRead.Reached.Task.WaitAsync(Guard);
            Task originalVideo = f.OriginalVideoReads.Last();
            await AssertAwaitPathAsync(originalVideo, read);
            await f.ControlRead.Reached.Task.WaitAsync(Guard);
            await AssertAwaitPathAsync(f.OriginalControlRead, f.MonitorTask);
            Task join = f.Keep(f.Parent.CloseAndJoinAsync());
            await Task.WhenAll(controlClose.Reached.Task, videoClose.Reached.Task).WaitAsync(Guard);
            // 慢 Control close 尚未释放，视频 close 与取消已经各自启动，不能串行卡住 child stop。
            Assert.True(f.ChildStopped);
            await f.ChildTask("_cancel").WaitAsync(Guard);
            Assert.True(f.ConnectToken.IsCancellationRequested);
            Assert.True(f.Video.Connection.IsCloseRequested);
            Task controlCloseTask = f.Control.Connection.CloseAsync();
            Task childJoin = f.ChildTask("_join");
            videoClose.Open();
            if (lastBranch != "child-read") videoRead.Open();
            if (lastBranch != "control-close") controlClose.Open();
            if (lastBranch != "monitor") f.ControlRead.Open();

            if (lastBranch == "control-close")
            {
                await ObserveAsync(f.MonitorTask);
                await childJoin.WaitAsync(Guard);
                AssertSnapshot(f.Snapshot, "Faulted", true, controlFailure);
                await AssertAwaitPathAsync(controlCloseTask, join);
                controlClose.Open();
            }
            else if (lastBranch == "child-read")
            {
                await ObserveAsync(f.MonitorTask);
                await controlCloseTask.WaitAsync(Guard);
                AssertSnapshot(f.Snapshot, "Faulted", true, controlFailure);
                await AssertAwaitPathAsync(originalVideo, childJoin);
                await AssertAwaitPathAsync(originalVideo, join);
                videoRead.Open();
            }
            else
            {
                await Task.WhenAll(controlCloseTask, childJoin).WaitAsync(Guard);
                await AssertAwaitPathAsync(f.OriginalControlRead, f.MonitorTask);
                await AssertAwaitPathAsync(f.OriginalControlRead, join);
                f.ControlRead.Open();
            }
            await ObserveAsync(join);
            Assert.Same(videoFailure, await ErrorAsync(read));
            Assert.True(controlCloseTask.IsCompletedSuccessfully);
            Assert.True(childJoin.IsCompletedSuccessfully);
            Assert.True(f.MonitorTask.IsCompleted);
            AssertSnapshot(f.Snapshot, "Faulted", true, controlFailure);
            Assert.Collection(f.Control.Connection.CleanupErrors,
                error => Assert.Same(tcpFailure, error), error => Assert.Same(sslFailure, error));
            Assert.Same(videoCloseFailure, Assert.Single(f.Video.Connection.CleanupErrors));
            AssertRootReferences(child.LifetimeErrors, videoFailure, videoCloseFailure);
            AssertRootReferences(f.Parent.LifetimeErrors,
                tcpFailure, sslFailure, controlFailure, videoFailure, videoCloseFailure);
            f.Control.AssertClosedOnce();
            f.Video.AssertClosedOnce();
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task FullJoin_ParentDiagnosticsKeepMonitorMultiFaultTree_ChildTree_AndBothConnectionsCleanup()
    {
        Fixture f = new();
        Pause monitorFault = f.NewPause(), videoRead = f.NewPause();
        Pause controlClose = f.NewPause(), videoClose = f.NewPause();
        AggregateException monitorBranch = MixedRoot(), childRoot = MixedRoot();
        ApplicationException sibling = new("Monitor 独立原任务错误。", new IOException("独立嵌套叶子。"));
        IOException controlTcp = new("Control TCP 清理错误。"), controlSsl = new("Control SSL 清理错误。");
        IOException videoTcp = new("视频 TCP 清理错误。"), videoSsl = new("视频 SSL 清理错误。");
        Task<int> originalMonitor = MultiFaultRead(f, monitorFault, monitorBranch, sibling);
        f.ControlOverride = originalMonitor;
        f.Control.Client.DisposeFailure = controlTcp;
        f.Control.Ssl.DisposeFailure = controlSsl;
        f.Control.Ssl.ClosePause = controlClose;
        f.Video.Client.DisposeFailure = videoTcp;
        f.Video.Ssl.DisposeFailure = videoSsl;
        f.Video.Ssl.ClosePause = videoClose;
        try
        {
            Child child = await f.Start().WaitAsync(Guard);
            await AssertAwaitPathAsync(originalMonitor, f.MonitorTask);
            IReadOnlyList<Exception> before = f.Parent.LifetimeErrors;
            Assert.Empty(before);
            f.VideoReadPause = videoRead;
            f.VideoFailure = childRoot;
            Task<EncodedFrame?> read = f.Keep(child.ReadFrameAsync());
            await videoRead.Reached.Task.WaitAsync(Guard);
            Task originalVideo = f.OriginalVideoReads.Last();
            await AssertAwaitPathAsync(originalVideo, read);
            Task join = f.Keep(f.Parent.CloseAndJoinAsync());
            await Task.WhenAll(controlClose.Reached.Task, videoClose.Reached.Task).WaitAsync(Guard);
            monitorFault.Open();
            videoRead.Open();
            await ObserveAsync(originalMonitor);
            await f.MonitorTask.WaitAsync(Guard);
            Assert.Same(childRoot, await ErrorAsync(read));
            Exception saved = Assert.IsType<AggregateException>(SnapshotError(f.Snapshot));
            AssertOriginalRoot(originalMonitor, saved);
            Assert.True(ContainsReference(saved, monitorBranch));
            Assert.True(ContainsReference(saved, sibling));
            AssertSnapshot(f.Snapshot, "Faulted", true, saved);
            // 两个原错误均已退出，full join 仍必须覆盖两个连接的实际清理任务。
            await AssertAwaitPathAsync(f.Control.Connection.CloseAsync(), join);
            await AssertAwaitPathAsync(f.Video.Connection.CloseAsync(), join);
            controlClose.Open();
            videoClose.Open();
            await join.WaitAsync(Guard);
            await child.StopAndJoinAsync().WaitAsync(Guard);

            IReadOnlyList<Exception> after = f.Parent.LifetimeErrors;
            AssertRootReferences(after, controlTcp, controlSsl, saved, childRoot, videoTcp, videoSsl);
            AssertRootReferences(child.LifetimeErrors, childRoot, videoTcp, videoSsl);
            AssertRootReferences(f.Control.Connection.CleanupErrors, controlTcp, controlSsl);
            AssertRootReferences(f.Video.Connection.CleanupErrors, videoTcp, videoSsl);
            Exception parentMonitorRoot = Assert.Single(after, root => ReferenceEquals(root, saved));
            AssertOriginalRoot(originalMonitor, parentMonitorRoot);
            // 父诊断引用原根，原根内部的中间包装和重复分支都不能被重建或 Flatten。
            foreach (AggregateException root in new[] { monitorBranch, childRoot })
            {
                Assert.Contains(after, parentRoot => ContainsReference(parentRoot, root));
                Assert.Equal(4, root.InnerExceptions.Count);
                Assert.Same(root.InnerExceptions[2], root.InnerExceptions[3]);
                AggregateException nested = Assert.IsType<AggregateException>(root.InnerExceptions[2].InnerException);
                Assert.Same(root.InnerExceptions[0], nested.InnerExceptions[0]);
                Assert.Same(root.InnerExceptions[1], nested.InnerExceptions[1]);
            }
            Assert.Empty(before);
            Assert.NotSame(before, after);
            Assert.NotSame(after, f.Parent.LifetimeErrors);
            f.Control.AssertClosedOnce();
            f.Video.AssertClosedOnce();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MonitorAndAttachCommit_AtRealTimerQuiescence_RespectBothOrders(bool monitorFirst)
    {
        Fixture f = new();
        Pause timerDispose = f.NewPause(), videoClose = f.NewPause();
        TaskCompletionSource<Task> disposing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Clock.TimerDispose = () =>
        {
            Assert.False(Monitor.IsEntered(f.ParentGate));
            Task original = f.Keep(timerDispose.WaitAsync());
            disposing.TrySetResult(original);
            return original;
        };
        f.Video.Ssl.ClosePause = videoClose;
        try
        {
            Task<Child> attach = f.Start();
            Task originalDispose = await disposing.Task.WaitAsync(Guard);
            await timerDispose.Reached.Task.WaitAsync(Guard);
            await AssertAwaitPathAsync(originalDispose, attach);
            await f.ControlRead.Reached.Task.WaitAsync(Guard);
            await AssertAwaitPathAsync(f.OriginalControlRead, f.MonitorTask);
            Child child = f.Child;
            Task originalAttach = f.ChildOperationTask("_attach");
            Assert.False(f.ChildOperationCommitted("_attach"));
            Assert.Equal(new[] { 4, Encoding.UTF8.GetByteCount(AckJson) }, f.Video.Ssl.ReadRequests);
            Assert.All(f.OriginalVideoReads, original => Assert.True(original.IsCompletedSuccessfully));
            // 到达真实 timer DisposeAsync 表示严格 ACK 初始化已成功；此时尚未越过联合提交。
            if (monitorFirst)
            {
                f.ControlRead.Open();
                await f.MonitorTask.WaitAsync(Guard);
                f.AssertRevoked();
                AssertSnapshot(f.Snapshot, "EndOfStream", false, null);
                Task join = f.Keep(f.Parent.CloseAndJoinAsync());
                await AssertAwaitPathAsync(originalDispose, originalAttach);
                await AssertAwaitPathAsync(originalDispose, join);
                timerDispose.Open();
                Assert.IsType<ObjectDisposedException>(await ErrorAsync(originalAttach));
                await videoClose.Reached.Task.WaitAsync(Guard);
                // attempt 的原 worker 已失败，但入口仍在等待它自己 child 的慢清理，不能提前报错。
                await AssertAwaitPathAsync(f.Video.Connection.CloseAsync(), attach);
                await AssertAwaitPathAsync(f.Video.Connection.CloseAsync(), join);
                videoClose.Open();
                Assert.IsType<ObjectDisposedException>(await ErrorAsync(attach));
                await join.WaitAsync(Guard);
                Assert.False(f.ChildOperationCommitted("_attach"));
                Assert.False(f.ChildDelivered);
            }
            else
            {
                timerDispose.Open();
                Assert.Same(child, await attach.WaitAsync(Guard));
                Assert.True(f.ChildOperationCommitted("_attach"));
                Assert.True(f.ChildDelivered);
                f.ControlRead.Open();
                await f.MonitorTask.WaitAsync(Guard);
                f.AssertRevoked();
                Task join = f.Keep(f.Parent.CloseAndJoinAsync());
                await videoClose.Reached.Task.WaitAsync(Guard);
                await AssertAwaitPathAsync(f.Video.Connection.CloseAsync(), join);
                Assert.Same(child, await attach.WaitAsync(Guard));
                videoClose.Open();
                await join.WaitAsync(Guard);
                Assert.Same(child, await attach.WaitAsync(Guard));
                Assert.True(originalAttach.IsCompletedSuccessfully);
                Assert.True(f.ChildOperationCommitted("_attach"));
            }
            await child.StopAndJoinAsync().WaitAsync(Guard);
            Assert.True(f.ChildStopped);
            f.Control.AssertClosedOnce();
            f.Video.AssertClosedOnce();
            AssertReadContract(f);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task MonitorBeforePayloadReadCompletes_RejectsFrame_AndJoinsOriginalRead()
    {
        Fixture f = new();
        Pause payload = f.NewPause();
        try
        {
            Child child = await f.Start().WaitAsync(Guard);
            f.VideoReadPause = payload;
            f.VideoPauseAt = Framed(AckJson).Length + VideoFrameHeader.Size;
            Task<EncodedFrame?> read = f.Keep(child.ReadFrameAsync());
            await payload.Reached.Task.WaitAsync(Guard);
            Task original = f.OriginalVideoReads.Last();
            Assert.Equal(VideoFrameTestData.Payload().Length, f.Video.Ssl.ReadRequests.Last());
            await AssertAwaitPathAsync(original, read);
            Assert.False(f.ChildOperationCommitted("_read"));
            await f.ControlRead.Reached.Task.WaitAsync(Guard);
            await AssertAwaitPathAsync(f.OriginalControlRead, f.MonitorTask);
            f.ControlRead.Open();
            await f.MonitorTask.WaitAsync(Guard);
            f.AssertRevoked();
            AssertSnapshot(f.Snapshot, "EndOfStream", false, null);
            await f.ChildTask("_cancel").WaitAsync(Guard);
            await f.ChildTask("_readerDispose").WaitAsync(Guard);
            await f.Video.Connection.CloseAsync().WaitAsync(Guard);
            Task join = f.Keep(f.Parent.CloseAndJoinAsync());
            await AssertAwaitPathAsync(original, read);
            await AssertAwaitPathAsync(original, join);
            payload.Open();
            // 此窗口在真实 reader 内、租用之后但尚未返回帧；不冒充 frameRead 联合提交接缝。
            // 本例仍走普通显式入口，不使用 rent/frameRead；默认 owner 无 Dispose 计数，不宣称恰一次释放。
            Assert.Equal(f.ConnectToken,
                Assert.IsAssignableFrom<OperationCanceledException>(await ErrorAsync(read)).CancellationToken);
            await join.WaitAsync(Guard);
            Assert.False(read.IsCompletedSuccessfully);
            Assert.False(f.ChildOperationCommitted("_read"));
            f.Control.AssertClosedOnce();
            f.Video.AssertClosedOnce();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MonitorAndFrameJointCommit_AfterReaderReturns_KeepExactOwnerDisposal(bool monitorFirst)
    {
        Fixture f = new();
        Pause beforeCommit = f.NewPause();
        byte[] payload = VideoFrameTestData.Payload();
        RecordingFrameOwner owner = new(payload.Length + 17);
        EncodedFrame? observed = null;
        int rents = 0, observations = 0;
        try
        {
            Child child = await f.Start(rent: length =>
            {
                Assert.False(Monitor.IsEntered(f.ParentGate));
                Assert.Equal(payload.Length, length);
                Interlocked.Increment(ref rents);
                return owner;
            }, frameRead: frame =>
            {
                // 使用生产已贯通的实例接缝：真实 reader 已成功返回，父/子联合提交尚未发生。
                Assert.False(Monitor.IsEntered(f.ParentGate));
                observed = Assert.IsType<EncodedFrame>(frame);
                Interlocked.Increment(ref observations);
                beforeCommit.WaitSynchronously();
            }).WaitAsync(Guard);
            Assert.Equal(0, Volatile.Read(ref rents));
            Assert.Equal(0, Volatile.Read(ref observations));
            Assert.True(f.Video.Ssl.WriteCalls > 0);
            Assert.Equal(new[] { 4, Encoding.UTF8.GetByteCount(AckJson) }, f.Video.Ssl.ReadRequests);
            await f.ControlRead.Reached.Task.WaitAsync(Guard);
            await AssertAwaitPathAsync(f.OriginalControlRead, f.MonitorTask);
            Task<EncodedFrame?> read = f.Keep(child.ReadFrameAsync());
            await beforeCommit.Reached.Task.WaitAsync(Guard);
            EncodedFrame uncommitted = Assert.IsType<EncodedFrame>(observed);
            Assert.Same(read, f.ChildOperationTask("_read"));
            Assert.False(f.ChildOperationCommitted("_read"));
            Assert.Equal(1, Volatile.Read(ref rents));
            Assert.Equal(1, Volatile.Read(ref observations));
            Assert.Equal(0, owner.DisposeCalls);
            Assert.Equal(payload, uncommitted.Payload.ToArray());
            Assert.Equal(new[] { 4, Encoding.UTF8.GetByteCount(AckJson), VideoFrameHeader.Size, payload.Length },
                f.Video.Ssl.ReadRequests);
            Assert.All(f.OriginalVideoReads, original => Assert.True(original.IsCompletedSuccessfully));

            if (monitorFirst)
            {
                f.ControlRead.Open();
                await f.MonitorTask.WaitAsync(Guard);
                Assert.Equal(0, await f.OriginalControlRead.WaitAsync(Guard));
                AssertSnapshot(f.Snapshot, "EndOfStream", false, null);
                f.AssertRevoked();
                Assert.True(f.ChildStopped);
                Task join = f.Keep(f.Parent.CloseAndJoinAsync());
                // monitor 已完成，reader 也已返回；完整 join 仍必须等待卡在交付观察回调里的原 read worker。
                await AssertAwaitPathAsync(read, f.ChildTask("_join"));
                await AssertAwaitPathAsync(read, join);
                Assert.False(f.ChildOperationCommitted("_read"));
                Assert.Equal(0, owner.DisposeCalls);
                beforeCommit.Open();
                ObjectDisposedException error = Assert.IsType<ObjectDisposedException>(await ErrorAsync(read));
                await join.WaitAsync(Guard);
                Assert.True(read.IsFaulted);
                Assert.False(f.ChildOperationCommitted("_read"));
                Assert.Equal(1, owner.DisposeCalls);
                Assert.Equal(0, uncommitted.PayloadLength);
                Assert.Throws<ObjectDisposedException>(() => { _ = uncommitted.Payload; });
                AssertRootReferences(child.LifetimeErrors, error);
                AssertRootReferences(f.Parent.LifetimeErrors, error);
                child.RequestStop();
                await child.StopAndJoinAsync().WaitAsync(Guard);
                Assert.Same(join, f.Parent.CloseAndJoinAsync());
                Assert.Equal(1, owner.DisposeCalls);
            }
            else
            {
                beforeCommit.Open();
                EncodedFrame delivered = Assert.IsType<EncodedFrame>(await read.WaitAsync(Guard));
                Assert.Same(uncommitted, delivered);
                Assert.True(f.ChildOperationCommitted("_read"));
                Assert.Equal(0, owner.DisposeCalls);
                f.ControlRead.Open();
                await f.MonitorTask.WaitAsync(Guard);
                Assert.Equal(0, await f.OriginalControlRead.WaitAsync(Guard));
                AssertSnapshot(f.Snapshot, "EndOfStream", false, null);
                f.AssertRevoked();
                await f.Parent.CloseAndJoinAsync().WaitAsync(Guard);
                await child.StopAndJoinAsync().WaitAsync(Guard);
                Assert.True(f.ChildStopped);
                Assert.Same(delivered, await read.WaitAsync(Guard));
                Assert.Equal(0, owner.DisposeCalls);
                Assert.Equal(payload.Length, delivered.PayloadLength);
                Assert.Equal(payload, delivered.Payload.ToArray());
                Assert.Empty(f.Parent.LifetimeErrors);
                delivered.Dispose();
                Assert.Equal(1, owner.DisposeCalls);
                Assert.Equal(0, delivered.PayloadLength);
                Assert.Throws<ObjectDisposedException>(() => { _ = delivered.Payload; });
                delivered.Dispose();
                Assert.Equal(1, owner.DisposeCalls);
            }
            Assert.Equal(1, Volatile.Read(ref rents));
            Assert.Equal(1, Volatile.Read(ref observations));
            f.Control.AssertClosedOnce();
            f.Video.AssertClosedOnce();
            AssertReadContract(f);
        }
        finally
        {
            // 先开闸并 join 原 worker，之后才兜底释放观察到的帧，不能让测试清理冒充生产释放。
            try { await f.FinishAsync(); }
            finally { observed?.Dispose(); }
        }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task FrameCommittedBeforeMonitorTerminal_RemainsOwnedByCallerAfterFullJoin()
    {
        Fixture f = new();
        EncodedFrame? frame = null;
        try
        {
            Child child = await f.Start().WaitAsync(Guard);
            Task<EncodedFrame?> read = f.Keep(child.ReadFrameAsync());
            frame = Assert.IsType<EncodedFrame>(await read.WaitAsync(Guard));
            Assert.True(f.ChildOperationCommitted("_read"));
            object owner = Field<object>(frame, "_owner");
            Assert.NotNull(owner);
            Assert.Equal(VideoFrameTestData.Payload(), frame.Payload.ToArray());
            await f.ControlRead.Reached.Task.WaitAsync(Guard);
            await AssertAwaitPathAsync(f.OriginalControlRead, f.MonitorTask);
            f.ControlRead.Open();
            await f.MonitorTask.WaitAsync(Guard);
            await f.Parent.CloseAndJoinAsync().WaitAsync(Guard);
            f.AssertRevoked();
            Assert.True(f.ChildStopped);
            Assert.Same(frame, await read.WaitAsync(Guard));
            Assert.Same(owner, Field<object>(frame, "_owner"));
            Assert.Equal(VideoFrameTestData.Payload(), frame.Payload.ToArray());
            Assert.Equal(VideoFrameTestData.Payload().Length, frame.PayloadLength);
            Assert.Empty(f.Parent.LifetimeErrors);
            frame.Dispose();
            Assert.Null(Field<object?>(frame, "_owner"));
            Assert.Equal(0, frame.PayloadLength);
            Assert.Throws<ObjectDisposedException>(() => { _ = frame.Payload; });
        }
        finally
        {
            try { await f.FinishAsync(); }
            finally { frame?.Dispose(); }
        }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task CloseContendingDuringRegistration_SeesChildAndMonitorTogether_AndJoinsPublishedWorker()
    {
        Fixture f = new();
        Pause prefix = f.NewPause(), connect = f.NewPause();
        f.ControlPrefix = prefix;
        TaskCompletionSource<Thread> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Task>? closeCall = null;
        f.Clock.Arm("timestamp", () =>
        {
            Assert.True(Monitor.IsEntered(f.ParentGate));
            Assert.Null(f.RegisteredChild);
            Assert.Null(f.OptionalMonitor);
            closeCall = f.Keep(Task.Factory.StartNew(() =>
            {
                entered.TrySetResult(Thread.CurrentThread);
                lock (f.ParentGate)
                {
                    Assert.NotNull(f.RegisteredChild);
                    Task monitor = f.MonitorTask;
                    Task join = f.Keep(f.Parent.CloseAndJoinAsync());
                    Assert.Same(join, f.Parent.CloseAndJoinAsync());
                    // monitor 若抢先进入 prefix 会被闸住，否则被此 gate 挡住；此处绝不等待终态 worker。
                    Assert.True(HasAwaitPath(monitor, join, new HashSet<Task>(), 24),
                        "并发 close 的实际 join 未登记已发布的 monitor worker。");
                    Assert.True(HasAwaitPath(f.ChildTask("_join"), join, new HashSet<Task>(), 24),
                        "并发 close 的实际 join 未登记 child join。");
                    return join;
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
            Thread thread = entered.Task.WaitAsync(Guard).GetAwaiter().GetResult();
            bool blocked = false;
            Assert.True(SpinWait.SpinUntil(() =>
            {
                blocked = (thread.ThreadState & ThreadState.WaitSleepJoin) != 0;
                return blocked || closeCall.IsCompleted;
            }, Guard), "Guard：并发 close 未到达登记 gate。");
            Assert.True(blocked, "child 登记前的并发 close 未被同一 gate 阻挡。");
            Assert.False(closeCall.IsCompleted);
        });
        try
        {
            Task<Child> attach = f.Start(connect: async _ =>
            {
                await connect.WaitAsync().ConfigureAwait(false);
                return f.Video.Connection;
            });
            Assert.NotNull(closeCall);
            Task join = await closeCall.WaitAsync(Guard);
            Assert.Same(join, f.Parent.CloseAndJoinAsync());
            f.AssertRevoked();
            prefix.Open();
            connect.Open();
            f.ControlRead.Open();
            Assert.IsType<ObjectDisposedException>(await ErrorAsync(attach));
            await join.WaitAsync(Guard);
            await f.MonitorTask.WaitAsync(Guard);
            Assert.True(f.ChildTask("_join").IsCompletedSuccessfully);
            Assert.True(f.ChildOperationTask("_attach").IsCompleted);
            Assert.False(f.ChildOperationCommitted("_attach"));
            Assert.False(f.ChildDelivered);
            string end = PropertyValue(f.Snapshot, "End")!.ToString()!;
            Assert.Contains(end, new[] { "SkippedLocalStop", "EndOfStream" });
            AssertSnapshot(f.Snapshot, end, true, null);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task MonitorTerminal_DoesNotWaitForSlowControlClose_BeforeRequestingChildStop()
    {
        Fixture f = new();
        Pause controlClose = f.NewPause();
        f.Control.Ssl.ClosePause = controlClose;
        try
        {
            await f.Start().WaitAsync(Guard);
            await f.ControlRead.Reached.Task.WaitAsync(Guard);
            f.ControlRead.Open();
            await controlClose.Reached.Task.WaitAsync(Guard);
            await ObserveAsync(f.MonitorTask);
            await f.ChildTask("_join").WaitAsync(Guard);
            f.AssertRevoked();
            f.Video.AssertClosedOnce();
            AssertSnapshot(f.Snapshot, "EndOfStream", false, null);
            Task join = f.Keep(f.Parent.CloseAndJoinAsync());
            await AssertAwaitPathAsync(f.Control.Connection.CloseAsync(), join);
            controlClose.Open();
            await join.WaitAsync(Guard);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task RevokeBeforeRegisteredWorkerStarts_SkipsIo_AndOwnerRevokeRemainsPureLogic()
    {
        Fixture f = new();
        Task<Child>? attach = null;
        try
        {
            lock (f.ParentGate)
            {
                attach = f.Start();
                AssertSnapshot(f.Snapshot, "Pending", false, null);
                Assert.NotNull(f.OptionalMonitor);
                f.Parent.RevokeForOwnerCleanup();
                f.AssertRevokedWithoutClose();
                Assert.True(f.ChildStopped);
                Assert.Empty(f.Control.Ssl.ReadRequests);
                f.Parent.RevokeForOwnerCleanup();
                f.Parent.Dispose();
                f.AssertRevokedWithoutClose();
            }
            await ObserveAsync(f.MonitorTask);
            Assert.IsType<ObjectDisposedException>(await ErrorAsync(attach));
            AssertSnapshot(f.Snapshot, "SkippedLocalStop", true, null);
            Assert.Empty(f.Control.Ssl.ReadRequests);
            Assert.False(f.Control.Connection.IsCloseRequested);
            Assert.Equal(0, f.ConnectCalls);
            await f.Parent.CloseAndJoinAsync().WaitAsync(Guard);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task SynchronousDispose_WaitsForControlClose_ButDoesNotJoinPendingMonitor()
    {
        Fixture f = new();
        Pause controlClose = f.NewPause();
        f.Control.Ssl.ClosePause = controlClose;
        try
        {
            await f.Start().WaitAsync(Guard);
            await f.ControlRead.Reached.Task.WaitAsync(Guard);
            await AssertAwaitPathAsync(f.OriginalControlRead, f.MonitorTask);
            TaskCompletionSource<Thread> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task dispose = f.Keep(Task.Factory.StartNew(() =>
            {
                entered.TrySetResult(Thread.CurrentThread);
                f.Parent.Dispose();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
            Thread thread = await entered.Task.WaitAsync(Guard);
            await controlClose.Reached.Task.WaitAsync(Guard);
            await f.ChildTask("_join").WaitAsync(Guard);
            bool blocked = false;
            Assert.True(SpinWait.SpinUntil(() =>
            {
                blocked = (thread.ThreadState & ThreadState.WaitSleepJoin) != 0;
                return blocked || dispose.IsCompleted;
            }, Guard), "Guard：同步 Dispose 未到达受控关闭等待点。");
            Assert.True(blocked, "同步 Dispose 未等待 Control close 即返回。");
            Assert.False(dispose.IsCompleted);
            f.AssertRevoked();
            AssertSnapshot(f.Snapshot, "Pending", true, null);
            // 独立调用线程确实阻塞，Control close 仍受闸门控制；放行后正向等待 Dispose 返回。
            controlClose.Open();
            await dispose.WaitAsync(Guard);
            await f.Control.Connection.CloseAsync().WaitAsync(Guard);
            await AssertAwaitPathAsync(f.OriginalControlRead, f.MonitorTask);
            f.Parent.Dispose();
            AssertReadContract(f);
            f.ControlRead.Open();
            await ObserveAsync(f.MonitorTask);
            await f.Parent.CloseAndJoinAsync().WaitAsync(Guard);
            AssertSnapshot(f.Snapshot, "EndOfStream", true, null);
        }
        finally { await f.FinishAsync(); }
    }

    private static void AssertReadContract(Fixture f)
    {
        Assert.Equal(new[] { 1 }, f.Control.Ssl.ReadRequests);
        Assert.Equal(CancellationToken.None, Assert.Single(f.Control.Ssl.ReadTokens));
        Assert.Equal(0, f.Control.Ssl.WriteCalls);
        Assert.Equal(0, f.Control.Ssl.FlushCalls);
        Assert.Equal(1, f.ControlPrefixChecks);
    }

    // 观察接口仅依赖约定名称，避免要求 enum/record 必须处于某个嵌套类型位置。
    private static PropertyInfo Property(Type type, string name) => type.GetProperty(name, Fields)
        ?? throw new XunitException($"缺少约定观察属性 {type.Name}.{name}。");
    private static object? PropertyValue(object instance, string name) => Property(instance.GetType(), name).GetValue(instance);
    private static Exception? SnapshotError(object snapshot) => (Exception?)PropertyValue(snapshot, "Error");
    private static void AssertSnapshot(object snapshot, string end, bool localStop, Exception? error)
    {
        object actualEnd = Assert.IsAssignableFrom<Enum>(PropertyValue(snapshot, "End"));
        Assert.Equal(end, actualEnd.ToString());
        Assert.Equal(localStop, Assert.IsType<bool>(PropertyValue(snapshot, "LocalStopObservedBeforeCompletion")));
        Assert.Same(error, SnapshotError(snapshot));
    }

    private static void AssertSnapshotShape(object snapshot)
    {
        Type parent = typeof(AuthenticatedControlSession);
        foreach (string name in new[] { "ControlMonitorState", "ControlMonitorCompletion" })
        {
            PropertyInfo property = Property(parent, name);
            Assert.True(property.GetMethod!.IsAssembly);
            Assert.Null(property.SetMethod);
        }
        Assert.Equal(typeof(Task), Property(parent, "ControlMonitorCompletion").PropertyType);
        Type snapshotType = snapshot.GetType();
        Assert.Equal("ControlMonitorSnapshot", snapshotType.Name);
        Assert.False(snapshotType.IsVisible);
        Type endType = Property(snapshotType, "End").PropertyType;
        Assert.Equal("ControlMonitorEnd", endType.Name);
        Assert.Equal(new[] { "EndOfStream", "Faulted", "Pending", "SkippedLocalStop", "UnexpectedData" },
            Enum.GetNames(endType).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        foreach (string name in new[] { "End", "LocalStopObservedBeforeCompletion", "Error" })
        {
            MethodInfo? setter = Property(snapshotType, name).SetMethod;
            if (setter is not null)
                Assert.Contains(typeof(IsExternalInit), setter.ReturnParameter.GetRequiredCustomModifiers());
        }
        Assert.All(snapshotType.GetFields(Fields), field => Assert.True(field.IsInitOnly));
        Assert.NotNull(snapshotType.GetMethod("PrintMembers", Fields));
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(parent));
        MethodInfo disposeAsync = Assert.Single(parent.GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            method => method.Name == "DisposeAsync");
        Assert.Equal(typeof(ValueTask), disposeAsync.ReturnType);
        Assert.Empty(disposeAsync.GetParameters());
    }

    private static AggregateException MixedRoot()
    {
        IOException io = new("必须保留的 IO 叶子。");
        ObjectDisposedException disposed = new("必须保留的 ODE 叶子");
        InvalidOperationException wrapper = new("不能 flatten 的中间包装。", new AggregateException("内部聚合。", io, disposed));
        return new AggregateException("混合原始根含重复分支。", io, disposed, wrapper, wrapper);
    }

    private static Task<int> MultiFaultRead(Fixture f, Pause release, Exception branch, Exception sibling) =>
        f.Keep(Task.Factory.StartNew(() =>
        {
            release.WaitSynchronously();
            f.Keep(Task.Factory.StartNew(() => { throw branch; }, CancellationToken.None,
                TaskCreationOptions.AttachedToParent, TaskScheduler.Default));
            f.Keep(Task.Factory.StartNew(() => { throw sibling; }, CancellationToken.None,
                TaskCreationOptions.AttachedToParent, TaskScheduler.Default));
            return 0;
        }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default));

    private static void AssertOriginalRoot(Task original, Exception saved)
    {
        AggregateException container = Assert.IsType<AggregateException>(original.Exception);
        if (container.InnerExceptions.Count == 1) Assert.Same(container.InnerExceptions[0], saved);
        else
        {
            AggregateException actual = Assert.IsType<AggregateException>(saved);
            Assert.Equal(container.InnerExceptions.Count, actual.InnerExceptions.Count);
            // 取原 Task 实际树逐项核对引用；不假设附加子任务或 WhenAll 的多错先后顺序。
            for (int i = 0; i < container.InnerExceptions.Count; i++)
                Assert.Same(container.InnerExceptions[i], actual.InnerExceptions[i]);
        }
    }

    private static void AssertRootReferences(IReadOnlyList<Exception> actual, params Exception[] expected)
    {
        Assert.Equal(expected.Length, actual.Count);
        foreach (Exception root in expected)
            Assert.Same(root, Assert.Single(actual, candidate => ReferenceEquals(candidate, root)));
        Assert.True(Assert.IsAssignableFrom<ICollection<Exception>>(actual).IsReadOnly);
    }

    private static bool ContainsReference(Exception root, Exception expected) => ReferenceEquals(root, expected) ||
        (root is AggregateException aggregate ? aggregate.InnerExceptions.Any(inner => ContainsReference(inner, expected))
            : root.InnerException is { } inner && ContainsReference(inner, expected));

    private static byte[] Framed(string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] result = new byte[payload.Length + 4];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)payload.Length);
        payload.CopyTo(result, 4);
        return result;
    }

    private static async Task<Exception> InvocationErrorAsync(Func<Task> invoke)
    {
        Task? original = null;
        Exception? synchronous = Record.Exception(() => { original = invoke(); });
        return synchronous ?? await ErrorAsync(Assert.IsAssignableFrom<Task>(original));
    }

    private static async Task<Exception> ErrorAsync(Task original)
    {
        await GuardCompletionAsync(original);
        Exception? error = await Record.ExceptionAsync(() => original);
        Assert.NotNull(error);
        return error;
    }

    private static async Task ObserveAsync(Task original)
    {
        await GuardCompletionAsync(original);
        // Guard 在 catch 外；超时绝不能被吞成一次已退出的业务失败。
        try { await original; }
        catch { _ = original.Exception; }
    }

    private static async Task GuardCompletionAsync(Task original)
    {
        using CancellationTokenSource cancellation = new();
        Task guard = Task.Delay(Guard, cancellation.Token);
        try { Assert.Same(original, await Task.WhenAny(original, guard).ConfigureAwait(false)); }
        finally { cancellation.Cancel(); }
    }

    private static void ObserveSynchronously(Task original)
    {
        // 只观察原 Task 已完成；不能让 WhenAny 回调排在被父 gate 阻塞的 inline continuation 后。
        Assert.True(SpinWait.SpinUntil(() => original.IsCompleted, Guard), "Guard：原 Task 尚未完成。");
        try { original.GetAwaiter().GetResult(); }
        catch { _ = original.Exception; }
    }

    private static async Task AssertAwaitPathAsync(Task awaited, Task returned)
    {
        using CancellationTokenSource cancellation = new();
        Task guard = Task.Delay(Guard, cancellation.Token);
        try
        {
            while (!HasAwaitPath(awaited, returned, new HashSet<Task>(), 24))
            {
                Assert.False(awaited.IsCompleted, "原等待任务已经退出，不能证明受闸控制的 await。");
                Assert.False(returned.IsCompleted, "返回任务提前退出，遗漏原操作或 join。");
                if (guard.IsCompleted)
                    throw new XunitException("Guard：未找到真实 await 链；运行时布局不兼容时必须显式失败。");
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
            bool exactAwait = machine is not null && machine.GetType().GetFields(Fields)
                .Where(field => field.Name.StartsWith("<>u__", StringComparison.Ordinal))
                .Select(field => field.GetValue(machine)).OfType<object>()
                .Any(awaiter => AwaiterReferencesTask(awaiter, awaited));
            string name = next.GetType().FullName ?? "";
            bool unwrap = name.StartsWith("System.Threading.Tasks.UnwrapPromise`", StringComparison.Ordinal);
            // WhenAll 必须直接登记在被等待原 Task 上；绝不认可 WhenAny、测试 TCS 或任意委托桥接。
            bool all = name.StartsWith("System.Threading.Tasks.Task+WhenAllPromise", StringComparison.Ordinal)
                || name.StartsWith("System.Threading.Tasks.Task+WhenAllPromise`", StringComparison.Ordinal);
            if (!exactAwait && !unwrap && !all) continue;
            if (ReferenceEquals(next, returned) || HasAwaitPath(next, returned, visited, remaining - 1)) return true;
        }
        return false;
    }

    private static bool AwaiterReferencesTask(object awaiter, Task awaited) =>
        awaiter.GetType().GetFields(Fields).Any(field =>
        {
            object? value = field.GetValue(awaiter);
            if (value is Task task) return ReferenceEquals(task, awaited);
            if (field.FieldType != typeof(ValueTask<int>) && field.FieldType != typeof(ValueTask)) return false;
            return ReferenceEquals(RuntimeField(field.FieldType, "_obj")?.GetValue(value), awaited);
        });

    private static object[] Continuations(Task task)
    {
        FieldInfo field = RuntimeField(typeof(Task), "m_continuationObject")
            ?? throw new XunitException("运行时缺少 Task continuation 字段，不能跳过 await 证明。");
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
        ?? throw new XunitException($"缺少已核对的只读字段 {instance.GetType().Name}.{name}。" )).GetValue(instance)!;
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            catch (TimeoutException) { throw new XunitException("Guard：同步测试闸门未释放，不是业务超时。"); }
        }
    }

    private sealed class RecordingFrameOwner(int length) : IMemoryOwner<byte>
    {
        private readonly byte[] _memory = new byte[length];
        private int _disposeCalls;
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        public Memory<byte> Memory
        {
            get
            {
                ObjectDisposedException.ThrowIf(DisposeCalls != 0, this);
                return _memory;
            }
        }
        // 不在夹具中去重，生产的重复释放必须反映到精确计数。
        public void Dispose() => Interlocked.Increment(ref _disposeCalls);
    }

    private sealed class ProbeClock : TimeProvider
    {
        private Action? _timestamp, _frequency;
        private int _reads, _timers;
        internal long Timestamp;
        internal bool RejectSampling;
        internal Func<Task>? TimerDispose;
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
            if (RejectSampling) throw new XunitException("Control monitor 不得额外采样视频附着时钟。");
            Interlocked.Exchange(ref _timestamp, null)?.Invoke();
            return Timestamp;
        }
        public override long TimestampFrequency
        {
            get
            {
                if (RejectSampling) throw new XunitException("Control monitor 不得额外读取时钟频率。");
                Interlocked.Exchange(ref _frequency, null)?.Invoke();
                return TimeSpan.TicksPerSecond;
            }
        }
        public override DateTimeOffset GetUtcNow() => throw new XunitException("不得读取墙钟。");
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _timers);
            return new InertTimer(TimerDispose);
        }
        private sealed class InertTimer(Func<Task>? dispose) : ITimer
        {
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync()
            {
                Dispose();
                return dispose is null ? ValueTask.CompletedTask : new ValueTask(dispose());
            }
        }
    }

    private sealed class Fixture
    {
        private readonly ConcurrentBag<Task> _tasks = [];
        private readonly List<Pause> _pauses = [];
        private readonly ConcurrentQueue<Task<int>> _videoReads = [];
        private readonly object _controlIoGate = new(), _videoIoGate = new();
        private readonly byte[] _callerToken = Convert.FromHexString(TokenHex);
        private readonly byte[] _ownedToken;
        private int _connectCalls, _prefixChecks, _videoOffset;
        private Task<int>? _controlOriginal;
        internal Endpoint Control { get; } = new();
        internal Endpoint Video { get; } = new();
        internal ProbeClock Clock { get; } = new();
        internal AuthenticatedControlSession Parent { get; }
        internal Pause ControlRead { get; }
        internal Pause? ControlPrefix, ControlAsTask, VideoReadPause;
        internal Exception? ControlFailure, ControlPrefixFailure, ControlAsTaskFailure, VideoFailure;
        internal Task<int>? ControlOverride;
        internal int ControlResult;
        internal int VideoPauseAt = -1;
        internal byte[] VideoInput = [.. Framed(AckJson), .. VideoFrameTestData.Wire()];
        internal CancellationToken ConnectToken;
        internal TaskCompletionSource Connecting { get; } = Signal();
        internal TaskCompletionSource<Task> AsTaskPromise { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ConnectCalls => Volatile.Read(ref _connectCalls);
        internal int ControlPrefixChecks => Volatile.Read(ref _prefixChecks);
        internal object ParentGate => Field<object>(Parent, "_gate");
        internal Child? RegisteredChild { get { lock (ParentGate) return Field<Child?>(Parent, "_videoLifetime"); } }
        internal Child Child => Assert.IsType<Child>(RegisteredChild);
        internal bool ChildStopped { get { lock (ParentGate) return Field<bool>(Child, "_stopped"); } }
        internal bool ChildDelivered { get { lock (ParentGate) return Field<bool>(Child, "_delivered"); } }
        internal Task ChildOperationTask(string name)
        {
            lock (ParentGate) return Assert.IsAssignableFrom<Task>(Field<Task?>(Field<object>(Child, name), "Worker"));
        }
        internal bool ChildOperationCommitted(string name)
        {
            lock (ParentGate) return Field<bool>(Field<object>(Child, name), "Committed");
        }
        internal Task? OptionalMonitor => (Task?)PropertyValue(Parent, "ControlMonitorCompletion");
        internal Task MonitorTask => Assert.IsAssignableFrom<Task>(OptionalMonitor);
        internal object Snapshot => Assert.IsAssignableFrom<object>(PropertyValue(Parent, "ControlMonitorState"));
        internal Task<int> OriginalControlRead
        {
            get { lock (_controlIoGate) return Assert.IsAssignableFrom<Task<int>>(_controlOriginal); }
        }
        internal Task<int>[] OriginalVideoReads { get { lock (_videoIoGate) return _videoReads.ToArray(); } }
        internal Task ChildTask(string name) { lock (ParentGate) return Assert.IsAssignableFrom<Task>(Field<Task?>(Child, name)); }

        internal Fixture(bool delivered = true)
        {
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
        internal Task<Child> Start(bool monitored = true, Func<CancellationToken, Task<TlsConnection>>? connect = null,
            CancellationToken token = default, Func<int, IMemoryOwner<byte>>? rent = null,
            Action<EncodedFrame?>? frameRead = null)
        {
            Task<TlsConnection> Connect(ConnectionTarget target, TransportTimeouts timeouts, TimeProvider clock, CancellationToken cancellation)
            {
                Assert.False(Monitor.IsEntered(ParentGate));
                Interlocked.Increment(ref _connectCalls);
                Assert.Same(Clock, clock);
                Assert.Same(TransportTimeouts.Default, timeouts);
                Assert.Equal(Parent.Identity.DeviceId, target.DeviceId);
                ConnectToken = cancellation;
                try { return Keep(connect is null ? Task.FromResult(Video.Connection) : connect(cancellation)); }
                finally { Connecting.TrySetResult(); }
            }
            if (rent is not null || frameRead is not null)
            {
                Assert.True(monitored);
                Assert.NotNull(rent);
                Assert.NotNull(frameRead);
                return Keep(Parent.AttachVideoWithControlMonitorForTestingAsync(Connect, rent, frameRead, token));
            }
            return Keep(monitored ? Parent.AttachVideoWithControlMonitorCoreAsync(Connect, token)
                : Parent.AttachVideoCoreAsync(Connect, token));
        }

        private ValueTask<int> ReadControl(Memory<byte> memory, CancellationToken token)
        {
            VerifyMonitorWorker();
            Interlocked.Increment(ref _prefixChecks);
            Assert.Equal(1, memory.Length);
            Assert.Equal(CancellationToken.None, token);
            ControlPrefix?.WaitSynchronously();
            if (ControlPrefixFailure is not null) throw ControlPrefixFailure;
            lock (_controlIoGate)
            {
                // 到达通知可能早于 async 方法返回；快照也取此锁，不能漏掉尚未发布的原 Task。
                Task<int> original = Keep(ControlOverride ?? ReadControlAsync(memory));
                _controlOriginal = original;
                if (ControlAsTask is null) return new ValueTask<int>(original);
                BlockingValueTaskSource source = new(this, original, ControlAsTask);
                return new ValueTask<int>(source, source.Version);
            }
        }

        internal void VerifyMonitorWorker()
        {
            Assert.False(Monitor.IsEntered(ParentGate));
            Assert.NotNull(OptionalMonitor);
            Assert.NotNull(RegisteredChild);
        }

        private async Task<int> ReadControlAsync(Memory<byte> memory)
        {
            // 忽略取消和 Dispose：只有原 I/O 闸门归还内存，关闭本身不能代表原操作结束。
            await ControlRead.WaitAsync().ConfigureAwait(false);
            if (ControlFailure is not null) throw ControlFailure;
            if (ControlResult != 0) memory.Span[0] = 0x7f;
            return ControlResult;
        }

        private async Task<int> ReadVideoAsync(Memory<byte> memory)
        {
            if (VideoReadPause is not null && (VideoPauseAt < 0 || _videoOffset == VideoPauseAt))
                await VideoReadPause.WaitAsync().ConfigureAwait(false);
            if (VideoFailure is not null) throw VideoFailure;
            int count = Math.Min(memory.Length, VideoInput.Length - _videoOffset);
            VideoInput.AsMemory(_videoOffset, count).CopyTo(memory);
            _videoOffset += count;
            return count;
        }

        internal void AssertNoMonitor()
        {
            Assert.Null(OptionalMonitor);
            Assert.Null(PropertyValue(Parent, "ControlMonitorState"));
            Assert.Empty(Control.Ssl.ReadRequests);
        }
        internal void AssertControlLive()
        {
            Assert.Same(Control.Ssl, Parent.Stream);
            Assert.Equal(TokenHex, Convert.ToHexString(_ownedToken));
            Assert.False(Control.Connection.IsCloseRequested);
            Assert.Equal(0, Control.Ssl.DisposeCalls);
            Assert.Equal(0, Control.Client.DisposeCalls);
            Assert.Empty(Control.Connection.CleanupErrors);
        }
        internal void AssertRevoked()
        {
            Assert.Same(_ownedToken, Field<byte[]>(Parent, "_sessionToken"));
            Assert.All(_ownedToken, value => Assert.Equal((byte)0, value));
            Assert.Equal(TokenHex, Convert.ToHexString(_callerToken));
            Assert.Throws<ObjectDisposedException>(() => { _ = Parent.Stream; });
            Assert.Throws<ObjectDisposedException>(() => Parent.CreateVideoAttachProof(new byte[16], Convert.FromHexString(PinHex)));
        }
        internal void AssertRevokedWithoutClose()
        {
            AssertRevoked();
            Assert.False(Control.Connection.IsCloseRequested);
            Assert.False(Video.Connection.IsCloseRequested);
            Assert.Equal(0, Control.Client.DisposeCalls);
            Assert.Equal(0, Control.Ssl.DisposeCalls);
            Assert.Equal(0, Video.Client.DisposeCalls);
            Assert.Equal(0, Video.Ssl.DisposeCalls);
            lock (ParentGate)
            {
                if (RegisteredChild is { } child)
                {
                    Assert.Null(Field<Task?>(child, "_cancel"));
                    Assert.Null(Field<Task?>(child, "_join"));
                }
            }
        }

        internal async Task FinishAsync()
        {
            Clock.Disarm();
            foreach (Pause pause in _pauses) pause.Open();
            try
            {
                Task join = Parent.CloseAndJoinAsync();
                Task childJoin = RegisteredChild?.StopAndJoinAsync() ?? Task.CompletedTask;
                await ObserveAsync(Task.WhenAll(_tasks.Append(join).Append(childJoin)));
                if (OptionalMonitor is { } monitor) await ObserveAsync(monitor);
                // 同步前缀、AsTask 和附加子任务可在开闸后才发布；再取快照 join 全部原操作。
                await ObserveAsync(Task.WhenAll(_tasks));
            }
            finally
            {
                try
                {
                    await ObserveAsync(Task.WhenAll(Control.Connection.CloseAsync(), Video.Connection.CloseAsync()));
                    await ObserveAsync(Task.WhenAll(_tasks));
                    foreach (Task<EncodedFrame?> read in _tasks.OfType<Task<EncodedFrame?>>())
                        if (read.IsCompletedSuccessfully) read.GetAwaiter().GetResult()?.Dispose();
                }
                finally
                {
                    try { Control.ReleaseResources(); }
                    finally { Video.ReleaseResources(); }
                }
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
            _fixture.VerifyMonitorWorker();
            if (Interlocked.Increment(ref _statusCalls) == 1)
            {
                _pause.WaitSynchronously();
                if (_fixture.ControlAsTaskFailure is { } error) throw error;
            }
            return _source.GetStatus(token);
        }
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            _fixture.VerifyMonitorWorker();
            // ValueTask.AsTask 注册的 state 就是运行时 ValueTaskSourceAsTask；只读捕获，不替换生产 worker。
            Task promise = Assert.IsAssignableFrom<Task>(state);
            Assert.Contains("ValueTaskSourceAsTask", promise.GetType().Name, StringComparison.Ordinal);
            _source.OnCompleted(continuation, state, token, flags);
            _fixture.AsTaskPromise.TrySetResult(promise);
        }
        public int GetResult(short token) => _source.GetResult(token);
    }

    // 未连接 TCP 与派生 SSL 是实例级 I/O/释放夹具；不执行、不冒充真实 TLS 握手。
    private sealed class Endpoint
    {
        internal ProbeTcpClient Client { get; } = new();
        internal ProbeSslStream Ssl { get; } = new();
        internal TlsConnection Connection { get; }
        internal Endpoint()
        {
            Assert.True(ConnectionTarget.TryCreate(new Guid("11111111-2222-3333-4444-555555555555"),
                IPAddress.Parse("192.168.50.7"), 12345, PinHex, out ConnectionTarget? target));
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
                    if (DisposeFailure is { } failure) throw failure;
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
        private int _writeCalls, _flushCalls, _disposeCalls;
        internal Func<Memory<byte>, CancellationToken, ValueTask<int>> ReadBody = null!;
        internal Pause? ClosePause;
        internal Exception? DisposeFailure;
        internal int[] ReadRequests => _requests.ToArray();
        internal CancellationToken[] ReadTokens => _tokens.ToArray();
        internal int WriteCalls => Volatile.Read(ref _writeCalls);
        internal int FlushCalls => Volatile.Read(ref _flushCalls);
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _requests.Enqueue(buffer.Length);
            _tokens.Enqueue(cancellationToken);
            return ReadBody(buffer, cancellationToken);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _writeCalls);
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
                    if (DisposeFailure is { } failure) throw failure;
                }
            }
            finally { base.Dispose(disposing); }
        }
        internal void ReleaseResources() => base.Dispose(true);
    }
}
