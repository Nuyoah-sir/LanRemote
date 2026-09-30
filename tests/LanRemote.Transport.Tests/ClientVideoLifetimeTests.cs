using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using LanRemote.Core.Models;
using Child = LanRemote.Transport.AuthenticatedControlSession.ClientVideoLifetime;

namespace LanRemote.Transport.Tests;

public sealed class ClientVideoLifetimeTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private const int TestTimeout = 60_000;
    private const long BudgetTicks = 15_000 * TimeSpan.TicksPerMillisecond;

    [Theory(Timeout = TestTimeout)]
    [InlineData("connect-null")]
    [InlineData("initialize-null")]
    [InlineData("cancelled")]
    [InlineData("not-delivered")]
    public async Task EntryRejection_DoesNotConsumeTheAttempt(string rejection)
    {
        Fixture f = new(delivered: rejection != "not-delivered");
        using CancellationTokenSource caller = new();
        try
        {
            Func<CancellationToken, Task<TlsConnection>> connect = _ =>
                throw new InvalidOperationException("拒绝入口不应调用连接工厂。");
            Func<TlsConnection, CancellationToken, Task> initialize = (_, _) =>
                throw new InvalidOperationException("拒绝入口不应调用初始化。");
            if (rejection == "cancelled") caller.Cancel();

            Exception? error = Record.Exception(() =>
            {
                _ = f.Parent.StartVideoLifetimeAsync(
                    rejection == "connect-null" ? null! : connect,
                    rejection == "initialize-null" ? null! : initialize, caller.Token);
            });
            if (rejection.EndsWith("-null", StringComparison.Ordinal))
                Assert.IsType<ArgumentNullException>(error);
            else if (rejection == "cancelled")
                Assert.Equal(caller.Token, Assert.IsType<OperationCanceledException>(error).CancellationToken);
            else
                Assert.IsType<InvalidOperationException>(error);

            Assert.Equal(0, f.Clock.TimestampReads);
            Assert.Null(f.RegisteredChild);
            f.AssertControlLive();
            if (rejection == "not-delivered") f.Parent.CommitDelivery();
            Child child = await f.Start().WaitAsync(Guard);
            Assert.Same(child, f.RegisteredChild);
            Assert.Equal(1, f.ConnectCalls);
            Assert.Equal(1, f.InitializeCalls);
            await child.StopAndJoinAsync().WaitAsync(Guard);
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(1000, 10_000_000L)]
    [InlineData(15_001, BudgetTicks)]
    [InlineData(15_000, -1L)]
    public async Task EntryBudgetRejection_DoesNotConsumeOrCloseControl(int hint, long timestamp)
    {
        Fixture f = new(hint: hint);
        try
        {
            f.Clock.Timestamp = timestamp;
            Assert.Throws<TimeoutException>(() => { _ = f.Start(); });
            Assert.Equal(0, f.ConnectCalls);
            Assert.Null(f.RegisteredChild);
            f.AssertControlLive();

            f.Clock.Timestamp = 0;
            Child child = await f.Start().WaitAsync(Guard);
            await child.StopAndJoinAsync().WaitAsync(Guard);
            Assert.Equal(1, f.ConnectCalls);
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("connect")]
    [InlineData("initialize")]
    public async Task FailedOriginalOperation_PermanentlyConsumesAttempt_ButControlLives(string phase)
    {
        Fixture f = new();
        Pause operation = f.NewPause();
        IOException failure = new("原操作的业务错误。");
        Task<TlsConnection>? originalConnect = null;
        Task? originalInitialize = null;
        try
        {
            if (phase == "connect") originalConnect = f.Keep(FailConnectAsync());
            else originalInitialize = f.Keep(FailInitializeAsync());
            Task<Child> attach = f.Start(
                connect: originalConnect is null ? null : _ => originalConnect!,
                initialize: originalInitialize is null ? null : (_, _) => originalInitialize!);
            await (phase == "connect" ? f.Connecting.Task : f.Initializing.Task).WaitAsync(Guard);
            await operation.Reached.Task.WaitAsync(Guard);
            operation.Open();
            Assert.Same(failure, await ErrorAsync(attach));
            Task original = phase == "connect" ? originalConnect! : originalInitialize!;
            Assert.True(original.IsFaulted);
            await ObserveAsync(original);
            Child child = Assert.IsType<Child>(f.RegisteredChild);
            await child.StopAndJoinAsync().WaitAsync(Guard);
            AssertTreeContains(child.LifetimeErrors, failure);
            AssertTreeContains(f.Parent.LifetimeErrors, failure);
            if (phase == "initialize") f.Video.AssertClosedOnce();
            else Assert.False(f.Video.Connection.IsCloseRequested);
            f.AssertControlLive();
            Assert.Throws<InvalidOperationException>(() => { _ = f.Start(); });
            Assert.Equal(1, f.ConnectCalls);
            Assert.Equal(phase == "connect" ? 0 : 1, f.InitializeCalls);
        }
        finally { await f.FinishAsync(); }

        async Task<TlsConnection> FailConnectAsync()
        {
            await operation.WaitAsync();
            throw failure;
        }
        async Task FailInitializeAsync()
        {
            await operation.WaitAsync();
            throw failure;
        }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("connect", "throw")]
    [InlineData("initialize", "throw")]
    [InlineData("connect", "null-task")]
    [InlineData("initialize", "null-task")]
    [InlineData("connect", "null-connection")]
    public async Task InvalidFactoryOutcome_JoinsWithoutClosingControlOrRestoringAttempt(string phase, string outcome)
    {
        Fixture f = new();
        IOException failure = new("工厂同步前缀抛出的原始错误。");
        try
        {
            Task<Child> attach = f.Start(connect: _ =>
            {
                if (phase != "connect") return Task.FromResult(f.Video.Connection);
                if (outcome == "throw") throw failure;
                if (outcome == "null-task") return null!;
                return Task.FromResult<TlsConnection>(null!);
            }, initialize: (_, _) =>
            {
                if (phase != "initialize") return Task.CompletedTask;
                if (outcome == "throw") throw failure;
                return null!;
            });
            Exception observed = await ErrorAsync(attach);
            if (outcome == "throw") Assert.Same(failure, observed);
            else Assert.IsType<InvalidOperationException>(observed);
            Assert.True(attach.IsFaulted);
            Child child = Assert.IsType<Child>(f.RegisteredChild);
            Assert.Throws<InvalidOperationException>(() => { _ = f.Start(); });
            Task join = f.Keep(child.StopAndJoinAsync());
            await join.WaitAsync(Guard);
            Assert.True(join.IsCompletedSuccessfully);
            Assert.Same(observed, Assert.Single(child.LifetimeErrors));
            Assert.Same(observed, Assert.Single(f.Parent.LifetimeErrors));
            if (phase == "initialize") f.Video.AssertClosedOnce();
            else
            {
                Assert.False(f.Video.Connection.IsCloseRequested);
                Assert.Equal(0, f.Video.Ssl.DisposeCalls);
                Assert.Equal(0, f.Video.Client.DisposeCalls);
            }
            f.AssertControlLive();
            // 原 worker 已 join；只约束重复入口不再取时，不约束已登记 attempt 的调度取时次数。
            int timestampReads = f.Clock.TimestampReads;
            int frequencyReads = f.Clock.FrequencyReads;
            Assert.Throws<InvalidOperationException>(() => { _ = f.Start(); });
            Assert.Same(child, f.RegisteredChild);
            Assert.Equal(1, f.ConnectCalls);
            Assert.Equal(phase == "initialize" ? 1 : 0, f.InitializeCalls);
            Assert.Equal(timestampReads, f.Clock.TimestampReads);
            Assert.Equal(frequencyReads, f.Clock.FrequencyReads);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerCleanup_WithDeliveredChild_OnlyRevokesUntilExplicitCloseAndJoin(bool reading)
    {
        Fixture f = new();
        Pause controlClose = f.NewPause();
        Pause videoClose = f.NewPause();
        Pause callback = f.NewPause();
        Pause readReturn = f.NewPause();
        f.Control.Ssl.ClosePause = controlClose;
        f.Video.Ssl.ClosePause = videoClose;
        f.Video.Ssl.ReadPause = readReturn;
        f.Video.Ssl.GateReadAt = 0;
        int callbacks = 0;
        try
        {
            Child child = await f.Start().WaitAsync(Guard);
            f.Own(f.StopToken.Register(() =>
            {
                Interlocked.Increment(ref callbacks);
                callback.WaitSynchronously();
            }));
            Task<EncodedFrame?>? read = null;
            Task<int>? originalIo = null;
            if (reading)
            {
                read = f.Read(child);
                await readReturn.Reached.Task.WaitAsync(Guard);
                originalIo = Assert.Single(f.Video.Ssl.OriginalReads);
                Assert.False(originalIo.IsCompleted);
            }
            byte[] secret = Field<byte[]>(f.Parent, "_sessionToken");
            Assert.Contains(secret, value => value != 0);
            Task revoke = f.Keep(Task.Run(() =>
            {
                f.Parent.RevokeForOwnerCleanup();
                f.Parent.RevokeForOwnerCleanup();
            }));
            // 错误地同步关闭/取消会先碰到对应闸门，直接断言失败，不靠 Guard 超时制造红灯。
            Task first = await Task.WhenAny(revoke, controlClose.Reached.Task,
                videoClose.Reached.Task, callback.Reached.Task).WaitAsync(Guard);
            Assert.Same(revoke, first);
            await revoke.WaitAsync(Guard);
            lock (f.ParentGate)
            {
                // 连异步关闭/取消 worker 的登记都不允许；不能只检查尚未来得及运行的回调计数。
                Assert.Null(Field<Task?>(f.Parent, "_closeAndJoin"));
                Assert.Null(Field<Task?>(child, "_videoClose"));
                Assert.Null(Field<Task?>(child, "_readerDispose"));
                Assert.Null(Field<Task?>(child, "_cancel"));
                Assert.Null(Field<Task?>(child, "_join"));
            }
            Assert.All(secret, value => Assert.Equal((byte)0, value));
            Assert.False(f.Control.Connection.IsCloseRequested);
            Assert.False(f.Video.Connection.IsCloseRequested);
            Assert.Equal(0, f.Control.Ssl.DisposeCalls);
            Assert.Equal(0, f.Control.Client.DisposeCalls);
            Assert.Equal(0, f.Video.Ssl.DisposeCalls);
            Assert.Equal(0, f.Video.Client.DisposeCalls);
            Assert.Equal(0, Volatile.Read(ref callbacks));
            Assert.False(f.StopToken.IsCancellationRequested);
            Assert.Throws<ObjectDisposedException>(() => { _ = f.Parent.Stream; });
            Assert.Throws<ObjectDisposedException>(() => { _ = child.ReadFrameAsync(); });
            Assert.Equal(reading ? 1 : 0, f.Video.Ssl.OriginalReads.Length);
            Assert.Empty(child.LifetimeErrors);
            Assert.Empty(f.Parent.LifetimeErrors);

            Task join = f.Keep(f.Parent.CloseAndJoinAsync());
            await Task.WhenAll(controlClose.Reached.Task, videoClose.Reached.Task,
                callback.Reached.Task).WaitAsync(Guard);
            Assert.False(join.IsCompleted);
            controlClose.Open();
            videoClose.Open();
            callback.Open();
            Task cancel;
            lock (f.ParentGate) cancel = Field<Task>(child, "_cancel");
            await Task.WhenAll(f.Control.RequestedClose, f.Video.RequestedClose, cancel).WaitAsync(Guard);
            if (reading)
            {
                Assert.False(originalIo!.IsCompleted);
                Assert.False(read!.IsCompleted);
                Assert.False(join.IsCompleted);
                readReturn.Open();
                Assert.True(await originalIo.WaitAsync(Guard) > 0);
                OperationCanceledException error = Assert.IsAssignableFrom<OperationCanceledException>(await ErrorAsync(read));
                Assert.Equal(f.StopToken, error.CancellationToken);
            }
            await join.WaitAsync(Guard);
            Assert.True(join.IsCompletedSuccessfully);
            Assert.Equal(1, Volatile.Read(ref callbacks));
            f.Control.AssertClosedOnce();
            f.Video.AssertClosedOnce();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task CommitDelivery_RevokedThenCallerThenDuplicate_TakesPrecedence(bool revoked, bool cancelled)
    {
        Fixture f = new(); // 已交付，所以每一例都同时具有 duplicate 条件。
        using CancellationTokenSource caller = new();
        try
        {
            if (revoked) f.Parent.RevokeForOwnerCleanup();
            if (cancelled) caller.Cancel();
            Exception? error = Record.Exception(() => f.Parent.CommitDelivery(caller.Token));
            if (revoked) Assert.IsType<ObjectDisposedException>(error);
            else if (cancelled)
                Assert.Equal(caller.Token, Assert.IsType<OperationCanceledException>(error).CancellationToken);
            else Assert.IsType<InvalidOperationException>(error);

            if (revoked) Assert.Throws<ObjectDisposedException>(() => f.Parent.CommitDelivery());
            else
            {
                Assert.Throws<InvalidOperationException>(() => f.Parent.CommitDelivery());
                f.AssertControlLive();
            }
            Assert.Null(f.RegisteredChild);
            Assert.Equal(0, f.ConnectCalls);
            Assert.Equal(0, f.InitializeCalls);
            Assert.Equal(0, f.Clock.TimestampReads);
            Assert.False(f.Control.Connection.IsCloseRequested);
            Assert.Equal(0, f.Control.Ssl.DisposeCalls);
            Assert.Equal(0, f.Control.Client.DisposeCalls);
            Assert.Empty(f.Parent.LifetimeErrors);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("timestamp")]
    [InlineData("frequency")]
    public async Task ReentrantEntryClock_InnerRegistrationWins_OuterMustRecheck(string clockPoint)
    {
        Fixture f = new();
        Task<Child>? inner = null;
        int outerFactoryCalls = 0;
        try
        {
            f.Clock.Arm(clockPoint, () => inner = f.Start());
            Assert.Throws<InvalidOperationException>(() =>
            {
                _ = f.Start(connect: _ =>
                {
                    Interlocked.Increment(ref outerFactoryCalls);
                    return Task.FromResult(f.Video.Connection);
                });
            });
            Assert.NotNull(inner);
            Child child = await inner.WaitAsync(Guard);
            Assert.Same(child, f.RegisteredChild);
            Assert.Equal(0, outerFactoryCalls);
            Assert.Equal(1, f.ConnectCalls);
            await child.StopAndJoinAsync().WaitAsync(Guard);
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("timestamp", false)]
    [InlineData("frequency", false)]
    [InlineData("timestamp", true)]
    [InlineData("frequency", true)]
    public async Task ReentrantCommitClock_StopWinsBeforeDelivery(string clockPoint, bool closeParent)
    {
        Fixture f = new();
        int callbacks = 0;
        try
        {
            Task<Child> attach = f.Start(initialize: (_, _) =>
            {
                f.Clock.Arm(clockPoint, () =>
                {
                    Interlocked.Increment(ref callbacks);
                    if (closeParent) f.Keep(f.Parent.CloseAndJoinAsync());
                    else Assert.IsType<Child>(f.RegisteredChild).RequestStop();
                });
                return Task.CompletedTask;
            });
            Assert.IsType<ObjectDisposedException>(await ErrorAsync(attach));
            Assert.Equal(1, callbacks);
            Child child = Assert.IsType<Child>(f.RegisteredChild);
            await child.StopAndJoinAsync().WaitAsync(Guard);
            f.Video.AssertClosedOnce();
            Assert.Throws<ObjectDisposedException>(() => { _ = child.ReadFrameAsync(); });
            if (closeParent) await f.Parent.CloseAndJoinAsync().WaitAsync(Guard);
            else f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("connect")]
    [InlineData("initialize")]
    public async Task FactorySynchronousPrefix_IsRegisteredAndOutsideGate_StartReturns_JoinWaits(string phase)
    {
        Fixture f = new();
        Pause prefix = f.NewPause();
        try
        {
            // StartNew 保留调用本身的 Task<Task<Child>>，不是用 TCS 假装 Start 已经返回。
            Task<Task<Child>> invocation = f.Keep(Task.Factory.StartNew(() => f.Start(
                connect: _ =>
                {
                    if (phase == "connect") BlockPrefix();
                    return Task.FromResult(f.Video.Connection);
                },
                initialize: (_, _) =>
                {
                    if (phase == "initialize") BlockPrefix();
                    return Task.CompletedTask;
                }), CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default));
            await prefix.Reached.Task.WaitAsync(Guard);
            Task<Child> attach = await invocation.WaitAsync(Guard);
            Assert.False(attach.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => { _ = f.Start(); });

            Task parentJoin = f.Keep(f.Parent.CloseAndJoinAsync());
            await f.Control.RequestedClose.WaitAsync(Guard);
            if (phase == "initialize") await f.Video.RequestedClose.WaitAsync(Guard);
            // 工厂尚未返回原 Task；关闭已完成仍不能越过这个同步前缀。
            Assert.False(prefix.IsOpen);
            Assert.False(attach.IsCompleted);
            Assert.False(parentJoin.IsCompleted);
            prefix.Open();
            Assert.IsType<ObjectDisposedException>(await ErrorAsync(attach));
            await parentJoin.WaitAsync(Guard);
            f.Video.AssertClosedOnce();
            Assert.Equal(phase == "connect" ? 0 : 1, f.InitializeCalls);
        }
        finally { await f.FinishAsync(); }

        void BlockPrefix()
        {
            Assert.False(Monitor.IsEntered(f.ParentGate));
            Child child = Assert.IsType<Child>(f.RegisteredChild);
            object slot = Field<object>(child, "_attach");
            Task worker = Field<Task>(slot, "Worker");
            Assert.NotNull(worker);
            Assert.False(worker.IsCompleted);
            prefix.WaitSynchronously();
        }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateOriginalConnect_AfterStop_IsClosedWithoutInitialization(bool closeParent)
    {
        Fixture f = new();
        Pause connectReturn = f.NewPause();
        Task<TlsConnection> original = f.Keep(ConnectAsync());
        try
        {
            Task<Child> attach = f.Start(connect: _ => original);
            await f.Connecting.Task.WaitAsync(Guard);
            await connectReturn.Reached.Task.WaitAsync(Guard);
            Child child = Assert.IsType<Child>(f.RegisteredChild);
            Task join = f.Keep(closeParent ? f.Parent.CloseAndJoinAsync() : child.StopAndJoinAsync());
            if (closeParent) await f.Control.RequestedClose.WaitAsync(Guard);
            Assert.False(original.IsCompleted);
            Assert.False(join.IsCompleted);
            Assert.False(f.Video.Connection.IsCloseRequested);
            Assert.Equal(0, f.InitializeCalls);

            connectReturn.Open();
            Assert.Same(f.Video.Connection, await original.WaitAsync(Guard));
            Assert.IsType<ObjectDisposedException>(await ErrorAsync(attach));
            await join.WaitAsync(Guard);
            f.Video.AssertClosedOnce();
            Assert.Equal(0, f.InitializeCalls);
            if (!closeParent) f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }

        async Task<TlsConnection> ConnectAsync()
        {
            await connectReturn.WaitAsync();
            return f.Video.Connection;
        }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task OriginalInitialize_IgnoresCancellation_BothClosesFinishBeforeJoinCanFinish()
    {
        Fixture f = new();
        Pause initializeReturn = f.NewPause();
        Task original = f.Keep(InitializeAsync());
        try
        {
            Task<Child> attach = f.Start(initialize: (_, _) => original);
            await f.Initializing.Task.WaitAsync(Guard);
            await initializeReturn.Reached.Task.WaitAsync(Guard);
            Task join = f.Keep(f.Parent.CloseAndJoinAsync());
            await Task.WhenAll(f.Control.RequestedClose, f.Video.RequestedClose).WaitAsync(Guard);
            f.Control.AssertClosedOnce();
            f.Video.AssertClosedOnce();
            Assert.False(original.IsCompleted);
            Assert.False(attach.IsCompleted);
            Assert.False(join.IsCompleted);

            initializeReturn.Open();
            await original.WaitAsync(Guard);
            Assert.IsType<ObjectDisposedException>(await ErrorAsync(attach));
            await join.WaitAsync(Guard);
        }
        finally { await f.FinishAsync(); }

        async Task InitializeAsync() => await initializeReturn.WaitAsync();
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(0)]
    [InlineData(40)]
    public async Task OriginalSslRead_IgnoresCloseAndCancellation_ReaderAndParentStillMustJoin(int gateAt)
    {
        Fixture f = new();
        ManualJoinScheduler scheduler = new();
        Pause readReturn = f.NewPause();
        f.Video.Ssl.ReadPause = readReturn;
        f.Video.Ssl.GateReadAt = gateAt;
        RecordingVideoPool pool = new();
        try
        {
            Task<Child> attach = f.Start(rent: pool.Rent, joinScheduler: scheduler);
            Child child = await attach.WaitAsync(Guard);
            Task<EncodedFrame?> read = f.Read(child);
            await readReturn.Reached.Task.WaitAsync(Guard);
            // 这是受控 SSL 真正返回给 ClientOwnedVideoStream 的原 I/O Task，非观察者代理。
            Task<int> originalIo = Assert.Single(f.Video.Ssl.OriginalReads, task => !task.IsCompleted);
            CancellationToken readToken = f.Video.Ssl.LastReadToken;
            TaskCompletionSource cancelled = Signal();
            f.Own(readToken.Register(() => cancelled.TrySetResult()));
            Task join = f.Keep(f.Parent.CloseAndJoinAsync());
            Task childJoin = f.Keep(child.StopAndJoinAsync());
            Task attachWorker;
            Task videoClose;
            Task cancel;
            Task readerDispose;
            lock (f.ParentGate)
            {
                attachWorker = Field<Task>(Field<object>(child, "_attach"), "Worker");
                videoClose = Field<Task>(child, "_videoClose");
                cancel = Field<Task>(child, "_cancel");
                readerDispose = Field<Task>(child, "_readerDispose");
            }
            // 正向等待所有其他原任务结束，不能用字段快照或回调通知替代这些 await。
            await Task.WhenAll(attach, attachWorker, f.Control.RequestedClose, f.Video.RequestedClose,
                videoClose, cancel, readerDispose).WaitAsync(Guard);
            await cancelled.Task.WaitAsync(Guard);
            Assert.True(attach.IsCompletedSuccessfully);
            // 测试线程同步跑完 join 前缀；删去 read await 时，childJoin 在返回前就会完成。
            Assert.True(scheduler.RunQueued());
            Assert.False(childJoin.IsCompleted);
            Assert.False(originalIo.IsCompleted);
            // 原 VideoFrameReader Task 必须仍 await 这个原 I/O；外层 read worker 也必须存活。
            Assert.False(read.IsCompleted);
            Assert.False(join.IsCompleted);
            Assert.Equal(gateAt == 0 ? 0 : 1, pool.Owners.Count);

            readReturn.Open();
            Assert.True(await originalIo.WaitAsync(Guard) > 0);
            OperationCanceledException error = Assert.IsAssignableFrom<OperationCanceledException>(await ErrorAsync(read));
            Assert.Equal(readToken, error.CancellationToken);
            await Task.WhenAll(childJoin, join).WaitAsync(Guard);
            Assert.All(pool.Owners, owner => Assert.Equal(1, owner.DisposeCalls));
            f.Video.AssertClosedOnce();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("control-close")]
    [InlineData("video-close")]
    [InlineData("callback")]
    public async Task BothConnectionClosesAndCancellationCallback_AreIndependent(string blocked)
    {
        Fixture f = new();
        Pause controlClose = f.NewPause(open: blocked != "control-close");
        Pause videoClose = f.NewPause(open: blocked != "video-close");
        Pause callback = f.NewPause(open: blocked != "callback");
        f.Control.Ssl.ClosePause = controlClose;
        f.Video.Ssl.ClosePause = videoClose;
        IOException controlError = new("Control SSL 关闭错误。");
        IOException videoError = new("Video SSL 关闭错误。");
        IOException callbackError = new("停止回调错误。");
        f.Control.Ssl.DisposeFailure = controlError;
        f.Video.Ssl.DisposeFailure = videoError;
        TaskCompletionSource callbackExited = Signal();
        try
        {
            Child child = await f.Start().WaitAsync(Guard);
            f.Own(f.StopToken.Register(() =>
            {
                try
                {
                    callback.WaitSynchronously();
                    throw callbackError;
                }
                finally { callbackExited.TrySetResult(); }
            }));
            Task parentJoin = f.Keep(f.Parent.CloseAndJoinAsync());
            Task childJoin = f.Keep(child.StopAndJoinAsync());
            Task controlTask = f.Control.RequestedClose;
            Task videoTask = f.Video.RequestedClose;
            await Task.WhenAll(controlClose.Reached.Task, videoClose.Reached.Task, callback.Reached.Task).WaitAsync(Guard);
            if (blocked != "control-close") await controlTask.WaitAsync(Guard);
            if (blocked != "video-close") await videoTask.WaitAsync(Guard);
            if (blocked != "callback") await callbackExited.Task.WaitAsync(Guard);

            // 每个展开例只阻塞一种来源；另两条完成路径均有独立的正向完成证据。
            Assert.False(parentJoin.IsCompleted);
            if (blocked == "control-close")
            {
                Assert.False(controlTask.IsCompleted);
                await childJoin.WaitAsync(Guard);
            }
            else
            {
                Assert.False(childJoin.IsCompleted);
                if (blocked == "video-close") Assert.False(videoTask.IsCompleted);
                else Assert.False(callbackExited.Task.IsCompleted);
            }
            controlClose.Open();
            videoClose.Open();
            callback.Open();
            await Task.WhenAll(parentJoin, childJoin).WaitAsync(Guard);
            f.Control.AssertClosedOnce();
            f.Video.AssertClosedOnce();
            AssertTreeContains(child.LifetimeErrors, videoError);
            AssertTreeContains(child.LifetimeErrors, callbackError);
            Assert.DoesNotContain(child.LifetimeErrors, root => ContainsReference(root, controlError));
            foreach (Exception error in new[] { controlError, videoError, callbackError })
                AssertTreeContains(f.Parent.LifetimeErrors, error);
            VideoReviewDiagnostics.AssertReadOnly(f.Parent.LifetimeErrors);
            Assert.True(parentJoin.IsCompletedSuccessfully);
            Assert.True(childJoin.IsCompletedSuccessfully);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task RepeatedPublicDispose_DoesNotWaitForFirstSlowControlClose()
    {
        Fixture f = new();
        Pause controlClose = f.NewPause();
        Pause videoClose = f.NewPause(open: true);
        f.Control.Ssl.ClosePause = controlClose;
        f.Video.Ssl.ClosePause = videoClose;
        try
        {
            Child child = await f.Start().WaitAsync(Guard);
            Task first = f.Keep(Task.Run(f.Parent.Dispose));
            await Task.WhenAll(controlClose.Reached.Task, videoClose.Reached.Task).WaitAsync(Guard);
            await f.Video.RequestedClose.WaitAsync(Guard);
            Task repeated = f.Keep(Task.Run(f.Parent.Dispose));
            await repeated.WaitAsync(Guard);
            Assert.False(first.IsCompleted);
            Assert.Throws<ObjectDisposedException>(() => { _ = child.ReadFrameAsync(); });
            Task join = f.Keep(f.Parent.CloseAndJoinAsync());
            Assert.False(join.IsCompleted);
            controlClose.Open();
            await Task.WhenAll(first, join).WaitAsync(Guard);
            f.Control.AssertClosedOnce();
            f.Video.AssertClosedOnce();
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task StopAndParentJoin_ReturnStableSharedTasks_BeforeAndAfterCompletion()
    {
        Fixture f = new();
        Pause videoClose = f.NewPause();
        f.Video.Ssl.ClosePause = videoClose;
        try
        {
            Child child = await f.Start().WaitAsync(Guard);
            child.RequestStop();
            Task childJoin = f.Keep(child.StopAndJoinAsync());
            await videoClose.Reached.Task.WaitAsync(Guard);
            Assert.Same(childJoin, child.StopAndJoinAsync());
            child.RequestStop();
            Assert.Same(childJoin, child.StopAndJoinAsync());
            Task parentJoin = f.Keep(f.Parent.CloseAndJoinAsync());
            Assert.Same(parentJoin, f.Parent.CloseAndJoinAsync());
            Assert.False(childJoin.IsCompleted);
            Assert.False(parentJoin.IsCompleted);
            videoClose.Open();
            await Task.WhenAll(childJoin, parentJoin).WaitAsync(Guard);
            Assert.Same(childJoin, child.StopAndJoinAsync());
            Assert.Same(parentJoin, f.Parent.CloseAndJoinAsync());
            Assert.True(childJoin.IsCompletedSuccessfully);
            Assert.True(parentJoin.IsCompletedSuccessfully);
            f.Control.AssertClosedOnce();
            f.Video.AssertClosedOnce();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("connect")]
    [InlineData("initialize")]
    public async Task OriginalTaskMultipleFaults_PreserveEveryBranchAndNestedException(string phase)
    {
        Fixture f = new();
        IOException firstLeaf = new("第一叶子。");
        IOException secondLeaf = new("第二叶子。");
        IOException thirdLeaf = new("第三叶子。");
        InvalidOperationException nested = new("中间包装不可丢失。", secondLeaf);
        AggregateException branch = new("原始聚合分支不可 flatten。", firstLeaf, nested);
        ApplicationException sibling = new("独立分支。", thirdLeaf);
        try
        {
            Task<TlsConnection>? connectTask = null;
            Task? initializeTask = null;
            if (phase == "connect")
            {
                // 附着子任务使真正的 Task<TlsConnection> 拥有多个故障分支，无代理 TCS。
                connectTask = f.Keep(Task.Factory.StartNew(() =>
                {
                    f.Keep(Task.Factory.StartNew(() => { throw branch; }, CancellationToken.None,
                        TaskCreationOptions.AttachedToParent, TaskScheduler.Default));
                    f.Keep(Task.Factory.StartNew(() => { throw sibling; }, CancellationToken.None,
                        TaskCreationOptions.AttachedToParent, TaskScheduler.Default));
                    return f.Video.Connection;
                }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default));
            }
            else
            {
                Task first = f.Keep(Task.Run(() => { throw branch; }));
                Task second = f.Keep(Task.Run(() => { throw sibling; }));
                initializeTask = f.Keep(Task.WhenAll(first, second));
            }
            Task original = phase == "connect" ? connectTask! : initializeTask!;
            Task<Child> attach = f.Start(
                connect: connectTask is null ? null : _ => connectTask!,
                initialize: initializeTask is null ? null : (_, _) => initializeTask!);
            Exception observed = await ErrorAsync(attach);
            await ObserveAsync(original);
            Assert.True(original.IsFaulted);
            AggregateException originalTree = original.Exception!;
            Assert.Equal(2, originalTree.InnerExceptions.Count);
            Child child = Assert.IsType<Child>(f.RegisteredChild);
            await child.StopAndJoinAsync().WaitAsync(Guard);
            IReadOnlyList<Exception> snapshot = child.LifetimeErrors;
            AggregateException saved = Assert.IsType<AggregateException>(Assert.Single(snapshot));
            Assert.Equal(originalTree.InnerExceptions.Count, saved.InnerExceptions.Count);
            for (int i = 0; i < originalTree.InnerExceptions.Count; i++)
                Assert.Same(originalTree.InnerExceptions[i], saved.InnerExceptions[i]);
            foreach (Exception error in new Exception[] { observed, branch, sibling, nested, firstLeaf, secondLeaf, thirdLeaf })
                AssertTreeContains(snapshot, error);
            Assert.Same(saved, Assert.Single(f.Parent.LifetimeErrors));
            VideoReviewDiagnostics.AssertReadOnly(snapshot);
            Assert.NotSame(snapshot, child.LifetimeErrors);
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(0, false)]
    [InlineData(40, false)]
    [InlineData(0, true)]
    [InlineData(40, true)]
    public async Task SslFaults_PreserveTheTreeThrownByOriginalReader_NotLostLowerLayerBranches(
        int gateAt, bool multipleIoFaults)
    {
        Fixture f = new();
        Pause readReturn = f.NewPause();
        TaskCompletionSource selected = Signal();
        RecordingVideoPool pool = new();
        IOException firstLeaf = new("用户聚合的第一叶子。");
        IOException secondLeaf = new("用户聚合的第二叶子。");
        IOException thirdLeaf = new("用户聚合的第三叶子。");
        InvalidOperationException nested = new("必须保留的中间包装。", secondLeaf);
        AggregateException branch = new("必须保留的嵌套聚合。", firstLeaf, nested);
        ApplicationException sibling = new("用户聚合中的独立分支。", thirdLeaf);
        AggregateException userAggregate = new("SSL 原操作直接抛出的自有聚合。", branch, sibling);
        IOException otherIoFault = new("SSL 多故障 Task 的另一分支，不要求跨 await 还原。");
        Task<int>? originalIo = null;
        f.Video.Ssl.GateReadAt = gateAt;
        f.Video.Ssl.ReadAtGate = () =>
        {
            originalIo = f.Keep(multipleIoFaults
                ? Task.Factory.StartNew(() =>
                {
                    readReturn.WaitSynchronously();
                    f.Keep(Task.Factory.StartNew(() => { throw userAggregate; }, CancellationToken.None,
                        TaskCreationOptions.AttachedToParent, TaskScheduler.Default));
                    f.Keep(Task.Factory.StartNew(() => { throw otherIoFault; }, CancellationToken.None,
                        TaskCreationOptions.AttachedToParent, TaskScheduler.Default));
                    return 0;
                }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default)
                : ThrowUserAggregateAsync());
            selected.TrySetResult();
            return originalIo;
        };
        try
        {
            Child child = await f.Start(rent: pool.Rent).WaitAsync(Guard);
            Task<EncodedFrame?> read = f.Read(child);
            await Task.WhenAll(selected.Task, readReturn.Reached.Task).WaitAsync(Guard);
            Assert.Same(originalIo, Assert.Single(f.Video.Ssl.OriginalReads, task => !task.IsCompleted));
            Assert.False(read.IsCompleted);
            Assert.Equal(gateAt == 0 ? 0 : 1, pool.Owners.Count);
            readReturn.Open();
            Exception observed = await ErrorAsync(read);
            await ObserveAsync(originalIo!);
            Assert.True(originalIo!.IsFaulted);
            AggregateException ioTree = originalIo.Exception!;
            Assert.Equal(multipleIoFaults ? 2 : 1, ioTree.InnerExceptions.Count);
            AssertTreeContains([ioTree], userAggregate);
            if (multipleIoFaults) AssertTreeContains([ioTree], otherIoFault);
            else Assert.Same(userAggregate, observed);

            // SSL 原 Task 与本层直接等待的原 VideoFrameReader Task 不是同一个句柄。
            // 中间 await 会把 SSL 多故障压为首分支；这里只验证原 reader 实际抛出的 observed 树，
            // 不把 ioTree 冒充 reader.Task.Exception，也不宣称恢复已丢失的下层兄弟分支。
            Assert.Same(ioTree.InnerExceptions[0], observed);
            await child.StopAndJoinAsync().WaitAsync(Guard);
            AggregateException saved = Assert.IsType<AggregateException>(Assert.Single(child.LifetimeErrors));
            Assert.Same(observed, saved);
            Assert.Same(saved, Assert.Single(f.Parent.LifetimeErrors));
            if (!multipleIoFaults)
            {
                // 只去掉原 reader Task 的单项包装；用户自有聚合的两个分支及嵌套包装均不可 flatten。
                Assert.Collection(saved.InnerExceptions,
                    error => Assert.Same(branch, error), error => Assert.Same(sibling, error));
                Assert.Collection(branch.InnerExceptions,
                    error => Assert.Same(firstLeaf, error), error => Assert.Same(nested, error));
                Assert.Same(secondLeaf, nested.InnerException);
                Assert.Same(thirdLeaf, sibling.InnerException);
                foreach (Exception error in new Exception[] { userAggregate, branch, sibling, nested,
                    firstLeaf, secondLeaf, thirdLeaf })
                    AssertTreeContains(child.LifetimeErrors, error);
            }
            Assert.All(pool.Owners, owner => Assert.Equal(1, owner.DisposeCalls));
            VideoReviewDiagnostics.AssertReadOnly(child.LifetimeErrors);
            f.Video.AssertClosedOnce();
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }

        async Task<int> ThrowUserAggregateAsync()
        {
            await readReturn.WaitAsync();
            throw userAggregate;
        }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationCallbackFaultTree_AndOldSnapshots_SurviveLaterCloseErrors(bool closeParent)
    {
        Fixture f = new();
        Pause videoClose = f.NewPause();
        Pause controlClose = f.NewPause();
        f.Video.Ssl.ClosePause = videoClose;
        f.Control.Ssl.ClosePause = controlClose;
        IOException videoError = new("稍后才补入的 Video 关闭错误。");
        IOException controlError = new("最后才补入的 Control 关闭错误。");
        f.Video.Ssl.DisposeFailure = videoError;
        f.Control.Ssl.DisposeFailure = controlError;
        IOException firstLeaf = new("取消回调第一叶子。");
        IOException secondLeaf = new("取消回调第二叶子。");
        IOException thirdLeaf = new("另一取消回调叶子。");
        InvalidOperationException nested = new("取消回调中间包装。", secondLeaf);
        AggregateException branch = new("回调自己抛出的嵌套聚合。", firstLeaf, nested);
        ApplicationException sibling = new("另一个回调的独立分支。", thirdLeaf);
        int callbacks = 0;
        try
        {
            Child child = await f.Start().WaitAsync(Guard);
            IReadOnlyList<Exception> emptyChildSnapshot = child.LifetimeErrors;
            IReadOnlyList<Exception> emptyParentSnapshot = f.Parent.LifetimeErrors;
            Assert.Empty(emptyChildSnapshot);
            Assert.Empty(emptyParentSnapshot);
            f.Own(f.StopToken.Register(() =>
            {
                Interlocked.Increment(ref callbacks);
                throw branch;
            }));
            f.Own(f.StopToken.Register(() =>
            {
                Interlocked.Increment(ref callbacks);
                throw sibling;
            }));
            Task? parentJoin = closeParent ? f.Keep(f.Parent.CloseAndJoinAsync()) : null;
            Task childJoin = f.Keep(child.StopAndJoinAsync());
            Task cancel;
            lock (f.ParentGate) cancel = Field<Task>(child, "_cancel");
            // 等实际取消 worker 完成，确保两个回调错误已经发布；两个关闭错误仍由闸门隔离。
            await Task.WhenAll(cancel, videoClose.Reached.Task).WaitAsync(Guard);
            if (closeParent) await controlClose.Reached.Task.WaitAsync(Guard);
            else f.AssertControlLive();
            Assert.Equal(2, Volatile.Read(ref callbacks));
            Assert.False(childJoin.IsCompleted);
            if (parentJoin is not null) Assert.False(parentJoin.IsCompleted);
            IReadOnlyList<Exception> childSnapshot = child.LifetimeErrors;
            IReadOnlyList<Exception> parentSnapshot = f.Parent.LifetimeErrors;
            AggregateException saved = Assert.IsType<AggregateException>(Assert.Single(childSnapshot));
            Assert.Same(saved, Assert.Single(parentSnapshot));
            Assert.Equal(2, saved.InnerExceptions.Count);
            Assert.Contains(branch, saved.InnerExceptions);
            Assert.Contains(sibling, saved.InnerExceptions);
            Assert.Collection(branch.InnerExceptions,
                error => Assert.Same(firstLeaf, error), error => Assert.Same(nested, error));
            Assert.Same(secondLeaf, nested.InnerException);
            Assert.Same(thirdLeaf, sibling.InnerException);
            foreach (Exception error in new Exception[] { branch, sibling, nested, firstLeaf, secondLeaf, thirdLeaf })
                AssertTreeContains(childSnapshot, error);
            VideoReviewDiagnostics.AssertReadOnly(childSnapshot);
            VideoReviewDiagnostics.AssertReadOnly(parentSnapshot);

            videoClose.Open();
            await childJoin.WaitAsync(Guard);
            IReadOnlyList<Exception> childAfterVideoClose = child.LifetimeErrors;
            Assert.Equal(2, childAfterVideoClose.Count);
            Assert.Contains(saved, childAfterVideoClose);
            Assert.Contains(videoError, childAfterVideoClose);
            Assert.Same(saved, Assert.Single(childSnapshot));
            Assert.Same(saved, Assert.Single(parentSnapshot));
            parentJoin ??= f.Keep(f.Parent.CloseAndJoinAsync());
            await controlClose.Reached.Task.WaitAsync(Guard);
            Assert.False(parentJoin.IsCompleted);
            controlClose.Open();
            await parentJoin.WaitAsync(Guard);
            IReadOnlyList<Exception> finalParentSnapshot = f.Parent.LifetimeErrors;
            Assert.Equal(3, finalParentSnapshot.Count);
            Assert.Contains(saved, finalParentSnapshot);
            Assert.Contains(videoError, finalParentSnapshot);
            Assert.Contains(controlError, finalParentSnapshot);
            Assert.Empty(emptyChildSnapshot);
            Assert.Empty(emptyParentSnapshot);
            Assert.Same(saved, Assert.Single(childSnapshot));
            Assert.Same(saved, Assert.Single(parentSnapshot));
            Assert.Equal(2, childAfterVideoClose.Count);
            Assert.DoesNotContain(childAfterVideoClose, error => ContainsReference(error, controlError));
            Assert.NotSame(childSnapshot, child.LifetimeErrors);
            Assert.NotSame(parentSnapshot, finalParentSnapshot);
            VideoReviewDiagnostics.AssertReadOnly(finalParentSnapshot);
            Assert.Equal(2, Volatile.Read(ref callbacks));
            Assert.True(childJoin.IsCompletedSuccessfully);
            Assert.True(parentJoin.IsCompletedSuccessfully);
            f.Video.AssertClosedOnce();
            f.Control.AssertClosedOnce();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("child", false)]
    [InlineData("parent", true)]
    [InlineData("caller", true)]
    public async Task ReaderReturnedButJointCommitHasNotRun_StopDisposesFrameExactlyOnce(
        string stop, bool ownerThrows)
    {
        Fixture f = new();
        Pause observer = f.NewPause();
        IOException cleanupError = new("未交付帧 owner 释放失败。");
        RecordingVideoOwner owner = new(22) { DisposeFailure = ownerThrows ? cleanupError : null };
        using CancellationTokenSource caller = new();
        EncodedFrame? observedFrame = null;
        int rents = 0;
        try
        {
            Child child = await f.Start(rent: length =>
            {
                Assert.Equal(5, length);
                Interlocked.Increment(ref rents);
                return owner;
            }, frameRead: frame =>
            {
                // 此 observer 位于真实 reader 已返回与父/子共同提交之间，不是 owner.Memory 猜测点。
                observedFrame = Assert.IsType<EncodedFrame>(frame);
                observer.WaitSynchronously();
            }).WaitAsync(Guard);
            Task<EncodedFrame?> read = f.Read(child, caller.Token);
            await observer.Reached.Task.WaitAsync(Guard);
            Assert.NotNull(observedFrame);
            Assert.Equal(VideoFrameTestData.Payload(), observedFrame.Payload.ToArray());
            Assert.All(f.Video.Ssl.OriginalReads, task => Assert.True(task.IsCompletedSuccessfully));
            Assert.Equal(0, owner.DisposeCalls);
            if (stop == "parent") _ = f.Keep(f.Parent.CloseAndJoinAsync());
            else if (stop == "caller") caller.Cancel();
            else child.RequestStop();
            Task join = f.Keep(child.StopAndJoinAsync());
            await f.Video.RequestedClose.WaitAsync(Guard);
            Assert.False(read.IsCompleted);
            Assert.False(join.IsCompleted);
            Assert.Equal(0, owner.DisposeCalls);

            observer.Open();
            Exception error = await ErrorAsync(read);
            if (stop == "caller")
                Assert.Equal(caller.Token, Assert.IsAssignableFrom<OperationCanceledException>(error).CancellationToken);
            else Assert.IsType<ObjectDisposedException>(error);
            await join.WaitAsync(Guard);
            Assert.Equal(1, rents);
            Assert.Equal(1, owner.DisposeCalls);
            Assert.Throws<ObjectDisposedException>(() => { _ = observedFrame.Payload; });
            AssertTreeContains(child.LifetimeErrors, error);
            if (ownerThrows)
            {
                AssertTreeContains(child.LifetimeErrors, cleanupError);
                AssertTreeContains(f.Parent.LifetimeErrors, cleanupError);
                Assert.Equal(1, child.LifetimeErrors.Count(root => ContainsReference(root, cleanupError)));
            }
            else Assert.DoesNotContain(child.LifetimeErrors, root => ContainsReference(root, cleanupError));
            child.RequestStop();
            await child.StopAndJoinAsync().WaitAsync(Guard);
            Assert.Equal(1, owner.DisposeCalls);
            if (stop != "parent") f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task FrameReadObserverThrows_PreservesPrimaryAndOwnerCleanupErrors()
    {
        Fixture f = new();
        IOException observerError = new("reader 返回后的观察者错误。");
        IOException cleanupError = new("未交付 owner 错误。");
        RecordingVideoOwner owner = new(5) { DisposeFailure = cleanupError };
        try
        {
            Child child = await f.Start(rent: _ => owner, frameRead: frame =>
            {
                Assert.NotNull(frame);
                throw observerError;
            }).WaitAsync(Guard);
            Assert.Same(observerError, await ErrorAsync(f.Read(child)));
            await child.StopAndJoinAsync().WaitAsync(Guard);
            Assert.Equal(1, owner.DisposeCalls);
            AssertTreeContains(child.LifetimeErrors, observerError);
            AssertTreeContains(child.LifetimeErrors, cleanupError);
            AssertTreeContains(f.Parent.LifetimeErrors, observerError);
            AssertTreeContains(f.Parent.LifetimeErrors, cleanupError);
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task UncommittedOwnerDispose_BlocksWorkerAndJoin_ButDoesNotHoldParentGate()
    {
        Fixture f = new();
        Pause ownerDispose = f.NewPause();
        RecordingVideoOwner owner = new(5) { BeforeDispose = ownerDispose.WaitSynchronously };
        try
        {
            Child child = await f.Start(rent: _ => owner,
                frameRead: _ => Assert.IsType<Child>(f.RegisteredChild).RequestStop()).WaitAsync(Guard);
            Task<EncodedFrame?> read = f.Read(child);
            await ownerDispose.Reached.Task.WaitAsync(Guard);
            Task join = f.Keep(child.StopAndJoinAsync());
            await f.Video.RequestedClose.WaitAsync(Guard);
            Assert.False(read.IsCompleted);
            Assert.False(join.IsCompleted);
            // 从另一线程成功取得父 gate，证明外部 Dispose 不在父锁内执行。
            Task<TimeSpan> query = f.Keep(Task.Run(() => f.Parent.GetRemainingAttachBudget()));
            Assert.Equal(TimeSpan.FromSeconds(15), await query.WaitAsync(Guard));
            ownerDispose.Open();
            Assert.IsType<ObjectDisposedException>(await ErrorAsync(read));
            await join.WaitAsync(Guard);
            Assert.Equal(1, owner.DisposeCalls);
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedFrame_IsNotReclaimedByLaterCancellationOrStop(bool closeParent)
    {
        Fixture f = new();
        f.Video.Ssl.Input = [.. VideoFrameTestData.Wire(), .. VideoFrameTestData.Wire()];
        RecordingVideoPool pool = new();
        using CancellationTokenSource attachCaller = new();
        using CancellationTokenSource readCaller = new();
        try
        {
            Child child = await f.Start(token: attachCaller.Token, rent: pool.Rent).WaitAsync(Guard);
            attachCaller.Cancel();
            Assert.False(f.Video.Connection.IsCloseRequested);
            EncodedFrame first = Assert.IsType<EncodedFrame>(await f.Read(child, readCaller.Token).WaitAsync(Guard));
            readCaller.Cancel();
            Assert.False(f.Video.Connection.IsCloseRequested);
            EncodedFrame second = Assert.IsType<EncodedFrame>(await f.Read(child).WaitAsync(Guard));
            Task join = f.Keep(closeParent ? f.Parent.CloseAndJoinAsync() : child.StopAndJoinAsync());
            await join.WaitAsync(Guard);
            Assert.Equal(2, pool.Owners.Count);
            Assert.All(pool.Owners, owner => Assert.Equal(0, owner.DisposeCalls));
            Assert.Equal(VideoFrameTestData.Payload(), first.Payload.ToArray());
            Assert.Equal(VideoFrameTestData.Payload(), second.Payload.ToArray());
            first.Dispose();
            second.Dispose();
            Assert.All(pool.Owners, owner => Assert.Equal(1, owner.DisposeCalls));
            Assert.Empty(child.LifetimeErrors);
            if (!closeParent) f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ReadEntryCancellationAndOverlap_DoNotConsumeSlot_EofTerminatesOnlyVideo()
    {
        Fixture f = new();
        Pause firstIo = f.NewPause();
        f.Video.Ssl.ReadPause = firstIo;
        f.Video.Ssl.GateReadAt = 0;
        RecordingVideoPool pool = new();
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        try
        {
            Child child = await f.Start(rent: pool.Rent).WaitAsync(Guard);
            Assert.Throws<OperationCanceledException>(() => { _ = child.ReadFrameAsync(cancelled.Token); });
            Assert.Empty(f.Video.Ssl.OriginalReads);
            Assert.False(f.Video.Connection.IsCloseRequested);
            Task<EncodedFrame?> first = f.Read(child);
            await firstIo.Reached.Task.WaitAsync(Guard);
            Assert.Throws<InvalidOperationException>(() => { _ = child.ReadFrameAsync(); });
            _ = Assert.Single(f.Video.Ssl.OriginalReads);
            Assert.False(f.Video.Connection.IsCloseRequested);
            firstIo.Open();
            EncodedFrame frame = Assert.IsType<EncodedFrame>(await first.WaitAsync(Guard));
            Assert.Null(await f.Read(child).WaitAsync(Guard));
            await child.StopAndJoinAsync().WaitAsync(Guard);
            Assert.Throws<ObjectDisposedException>(() => { _ = child.ReadFrameAsync(); });
            Assert.Equal(0, Assert.Single(pool.Owners).DisposeCalls);
            frame.Dispose();
            Assert.Equal(1, pool.Owners[0].DisposeCalls);
            f.Video.AssertClosedOnce();
            Assert.Empty(child.LifetimeErrors);
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task DeliveredVideo_ReadsAndEofHaveNoAttachTtlOrClockSampling()
    {
        Fixture f = new();
        f.Video.Ssl.Input = [.. VideoFrameTestData.Wire(), .. VideoFrameTestData.Wire()];
        RecordingVideoPool pool = new();
        try
        {
            Child child = await f.Start(rent: pool.Rent).WaitAsync(Guard);
            int timestampReads = f.Clock.TimestampReads;
            int frequencyReads = f.Clock.FrequencyReads;
            f.Clock.Timestamp = TimeSpan.FromDays(365).Ticks;
            f.Clock.Arm("timestamp", () => throw new InvalidOperationException("交付后读帧不应取时。"));
            f.Clock.Arm("frequency", () => throw new InvalidOperationException("交付后读帧不应查询频率。"));
            EncodedFrame first = Assert.IsType<EncodedFrame>(await f.Read(child).WaitAsync(Guard));
            EncodedFrame second = Assert.IsType<EncodedFrame>(await f.Read(child).WaitAsync(Guard));
            Assert.Null(await f.Read(child).WaitAsync(Guard));
            await child.StopAndJoinAsync().WaitAsync(Guard);
            Assert.Equal(timestampReads, f.Clock.TimestampReads);
            Assert.Equal(frequencyReads, f.Clock.FrequencyReads);
            Assert.Equal(VideoFrameTestData.Payload(), first.Payload.ToArray());
            Assert.Equal(VideoFrameTestData.Payload(), second.Payload.ToArray());
            first.Dispose();
            second.Dispose();
            Assert.All(pool.Owners, owner => Assert.Equal(1, owner.DisposeCalls));
            Assert.Empty(child.LifetimeErrors);
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(BudgetTicks)]
    [InlineData(-1L)]
    public async Task AttachCommit_RechecksBudgetAfterOriginalInitialization(long commitTimestamp)
    {
        Fixture f = new();
        Pause initializeReturn = f.NewPause();
        Task original = f.Keep(InitializeAsync());
        try
        {
            Task<Child> attach = f.Start(initialize: (_, _) => original);
            await f.Initializing.Task.WaitAsync(Guard);
            await initializeReturn.Reached.Task.WaitAsync(Guard);
            Assert.Equal(TimeSpan.FromTicks(BudgetTicks), f.Parent.GetRemainingAttachBudget());
            Assert.False(original.IsCompleted);
            Assert.False(attach.IsCompleted);
            // 手动 timer 不触发；原初始化开闸后必须以原锚点的剩余预算拒绝提交。
            f.Clock.Timestamp = commitTimestamp;
            initializeReturn.Open();
            await original.WaitAsync(Guard);
            Assert.IsType<TimeoutException>(await ErrorAsync(attach));
            await Assert.IsType<Child>(f.RegisteredChild).StopAndJoinAsync().WaitAsync(Guard);
            Assert.True(attach.IsFaulted);
            f.Video.AssertClosedOnce();
            f.AssertControlLive();
            f.Clock.Timestamp = 0;
            Assert.Throws<InvalidOperationException>(() => { _ = f.Start(); });
            Assert.Equal(1, f.ConnectCalls);
        }
        finally { await f.FinishAsync(); }

        async Task InitializeAsync() => await initializeReturn.WaitAsync();
    }

    private static async Task<Exception> ErrorAsync(Task original)
    {
        using CancellationTokenSource guard = new();
        Task guardTask = Task.Delay(Guard, guard.Token);
        try
        {
            Task winner = await Task.WhenAny(original, guardTask);
            // Guard 获胜即失败，不能用稍后变化的原任务状态把保护超时当成业务异常。
            Assert.Same(original, winner);
            Exception? error = await Record.ExceptionAsync(async () => await original);
            Assert.NotNull(error);
            return error;
        }
        finally { guard.Cancel(); }
    }

    private static async Task ObserveAsync(Task original)
    {
        using CancellationTokenSource guard = new();
        Task guardTask = Task.Delay(Guard, guard.Token);
        try
        {
            Task winner = await Task.WhenAny(original, guardTask);
            Assert.Same(original, winner);
            // 只观察原任务异常；Guard 断言必须在吞异常的范围之外。
            try { await original; }
            catch { _ = original.Exception; }
        }
        finally { guard.Cancel(); }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static T Field<T>(object instance, string name) => (T)instance.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(instance)!;

    private static bool ContainsReference(Exception root, Exception expected) =>
        ReferenceEquals(root, expected) ||
        (root is AggregateException aggregate
            ? aggregate.InnerExceptions.Any(error => ContainsReference(error, expected))
            : root.InnerException is { } inner && ContainsReference(inner, expected));

    private static void AssertTreeContains(IEnumerable<Exception> errors, Exception expected) =>
        Assert.Contains(errors, error => ContainsReference(error, expected));

    // 所有 TCS 仅用于发出到达通知或开闸；操作句柄始终是原 async/Task.Run/WhenAll 返回的 Task。
    private sealed class Pause(bool open)
    {
        private readonly TaskCompletionSource _release = Signal();
        internal TaskCompletionSource Reached { get; } = Signal();
        internal bool IsOpen => _release.Task.IsCompleted;

        internal void Open() => _release.TrySetResult();

        internal async Task WaitAsync()
        {
            if (open) Open();
            Reached.TrySetResult();
            await _release.Task.ConfigureAwait(false);
        }

        internal void WaitSynchronously()
        {
            if (open) Open();
            Reached.TrySetResult();
            // 仅作死锁失败保护；正常路径总由测试 finally 或明确提交顺序开闸。
            try { _release.Task.WaitAsync(Guard).GetAwaiter().GetResult(); }
            catch (TimeoutException)
            {
                throw new Xunit.Sdk.XunitException("Pause 同步等待超过 Guard：测试未及时开闸，不是业务超时。");
            }
        }
    }

    private sealed class ManualJoinScheduler : TaskScheduler
    {
        private readonly object _gate = new();
        private Task? _queued;
        private bool _scheduled;

        protected override IEnumerable<Task> GetScheduledTasks()
        {
            lock (_gate) return _queued is { } task ? [task] : [];
        }

        protected override void QueueTask(Task task)
        {
            lock (_gate)
            {
                if (_scheduled) throw new InvalidOperationException("只允许调度一个 join 任务。");
                _scheduled = true;
                _queued = task;
            }
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        internal bool RunQueued()
        {
            Task? task;
            lock (_gate)
            {
                task = _queued;
                _queued = null;
            }
            if (task is null) return false;
            // 仅由测试正文或 finally 同步调用，QueueTask 与内联路径绝不执行任务。
            Assert.True(TryExecuteTask(task));
            return true;
        }
    }

    private sealed class ProbeClock : TimeProvider
    {
        private Action? _onTimestamp;
        private Action? _onFrequency;
        private int _timestampReads;
        private int _frequencyReads;
        internal long Timestamp;
        internal int TimestampReads => Volatile.Read(ref _timestampReads);
        internal int FrequencyReads => Volatile.Read(ref _frequencyReads);

        internal void Arm(string point, Action callback)
        {
            if (point == "timestamp") _onTimestamp = callback;
            else _onFrequency = callback;
        }

        internal void Disarm()
        {
            _onTimestamp = null;
            _onFrequency = null;
        }

        public override long GetTimestamp()
        {
            Interlocked.Increment(ref _timestampReads);
            long captured = Volatile.Read(ref Timestamp);
            Interlocked.Exchange(ref _onTimestamp, null)?.Invoke();
            return captured;
        }

        public override long TimestampFrequency
        {
            get
            {
                Interlocked.Increment(ref _frequencyReads);
                Interlocked.Exchange(ref _onFrequency, null)?.Invoke();
                return TimeSpan.TicksPerSecond;
            }
        }

        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("不得读取墙钟。");
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new ManualTimer();

        // 只接受手动调度/释放，不推进时间，也不自动执行 timer 回调。
        private sealed class ManualTimer : ITimer
        {
            private int _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref _disposed) == 0;

            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    // 未连接的 TCP + 派生 SslStream 只模拟受控 I/O/释放，不执行或宣称真实 TLS 握手。
    // 帧解析走生产 VideoFrameReader、ClientOwnedVideoStream 和独立手工黄金 wire。
    private sealed class Fixture
    {
        private readonly ConcurrentBag<Task> _tasks = [];
        private readonly ConcurrentBag<ManualJoinScheduler> _joinSchedulers = [];
        private readonly List<Pause> _pauses = [];
        private readonly List<CancellationTokenRegistration> _registrations = [];
        private int _connectCalls;
        private int _initializeCalls;
        internal Endpoint Control { get; } = new([]);
        internal Endpoint Video { get; } = new(VideoFrameTestData.Wire());
        internal ProbeClock Clock { get; } = new();
        internal AuthenticatedControlSession Parent { get; }
        internal TaskCompletionSource Connecting { get; } = Signal();
        internal TaskCompletionSource Initializing { get; } = Signal();
        internal CancellationToken StopToken { get; private set; }
        internal int ConnectCalls => Volatile.Read(ref _connectCalls);
        internal int InitializeCalls => Volatile.Read(ref _initializeCalls);
        internal object ParentGate => Field<object>(Parent, "_gate");
        internal Child? RegisteredChild => Field<Child?>(Parent, "_videoLifetime");

        internal Fixture(bool delivered = true, int hint = 15_000)
        {
            Parent = new AuthenticatedControlSession(Control.Connection, SessionPermission.Control,
                new Guid("00112233-4455-6677-8899-aabbccddeeff"), "ABCDEF",
                Convert.FromHexString("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F"),
                hint, 0, Clock);
            if (delivered) Parent.CommitDelivery();
        }

        internal Pause NewPause(bool open = false)
        {
            Pause pause = new(open);
            _pauses.Add(pause);
            return pause;
        }

        internal T Keep<T>(T task) where T : Task
        {
            // 工厂违规返回 null 时原样交给生产入口裁决，不往 finally 的 join 集合塞空句柄。
            if (task is not null) _tasks.Add(task);
            return task!;
        }

        internal void Own(CancellationTokenRegistration registration) => _registrations.Add(registration);

        internal Task<Child> Start(
            Func<CancellationToken, Task<TlsConnection>>? connect = null,
            Func<TlsConnection, CancellationToken, Task>? initialize = null,
            CancellationToken token = default,
            Func<int, IMemoryOwner<byte>>? rent = null,
            Action<EncodedFrame?>? frameRead = null,
            TaskScheduler? joinScheduler = null)
        {
            if (joinScheduler is ManualJoinScheduler manual) _joinSchedulers.Add(manual);
            return Keep(Parent.StartVideoLifetimeAsync(
                cancellation =>
                {
                    Interlocked.Increment(ref _connectCalls);
                    Task<TlsConnection> original = Keep(connect is null ? Task.FromResult(Video.Connection) : connect(cancellation));
                    Connecting.TrySetResult();
                    return original;
                },
                (connection, cancellation) =>
                {
                    Assert.Same(Video.Connection, connection);
                    StopToken = cancellation;
                    Interlocked.Increment(ref _initializeCalls);
                    Task original = Keep(initialize is null ? Task.CompletedTask : initialize(connection, cancellation));
                    Initializing.TrySetResult();
                    return original;
                }, token, rent, frameRead, joinScheduler));
        }

        internal Task<EncodedFrame?> Read(Child child, CancellationToken token = default) => Keep(child.ReadFrameAsync(token));

        internal void AssertControlLive()
        {
            Assert.False(Control.Connection.IsCloseRequested);
            Assert.Same(Control.Ssl, Parent.Stream);
            Assert.Equal(0, Control.Client.DisposeCalls);
            Assert.Equal(0, Control.Ssl.DisposeCalls);
            Assert.Empty(Control.Ssl.OriginalReads);
            Assert.Empty(Control.Connection.CleanupErrors);
        }

        internal async Task FinishAsync()
        {
            // 失败路径也先放全部闸，再 await 所有真实操作、外层调用和共享 join，不把超时当作退出。
            Clock.Disarm();
            foreach (Pause pause in _pauses) pause.Open();
            try
            {
                Task join = Parent.CloseAndJoinAsync();
                Task childJoin = RegisteredChild?.StopAndJoinAsync() ?? Task.CompletedTask;
                foreach (ManualJoinScheduler scheduler in _joinSchedulers) scheduler.RunQueued();
                await ObserveAsync(Task.WhenAll(_tasks.Append(join).Append(childJoin)));
                // worker 结束后才取第二份快照，覆盖在同步前缀放行后才发布的原工厂 Task。
                await ObserveAsync(Task.WhenAll(_tasks));
            }
            finally
            {
                try
                {
                    await ObserveAsync(Task.WhenAll(Control.Connection.CloseAsync(), Video.Connection.CloseAsync()));
                    await ObserveAsync(Task.WhenAll(Video.Ssl.OriginalReads));
                    foreach (CancellationTokenRegistration registration in _registrations)
                        await registration.DisposeAsync();
                    foreach (Task<EncodedFrame?> read in _tasks.OfType<Task<EncodedFrame?>>())
                    {
                        if (!read.IsCompletedSuccessfully) continue;
                        EncodedFrame? frame = read.GetAwaiter().GetResult();
                        try { frame?.Dispose(); }
                        catch
                        {
                            // 只隔离最后兜底释放的异常，避免错误交付的 owner 覆盖原断言。
                        }
                    }
                }
                finally
                {
                    // 绕过探针计数的兜底仅在所有断言之后，不能用它冒充生产释放证据。
                    Control.ReleaseResources();
                    Video.ReleaseResources();
                }
            }
        }
    }

    private sealed class Endpoint
    {
        internal ProbeTcpClient Client { get; } = new();
        internal ProbeSslStream Ssl { get; }
        internal TlsConnection Connection { get; }
        internal Task RequestedClose
        {
            get
            {
                // 先证明生产已经请求关闭，不能由测试调用 CloseAsync 补齐遗漏的关闭。
                Assert.True(Connection.IsCloseRequested);
                return Connection.CloseAsync();
            }
        }

        internal Endpoint(byte[] input)
        {
            Ssl = new ProbeSslStream { Input = input };
            byte[] pin = Convert.FromHexString("808182838485868788898A8B8C8D8E8F909192939495969798999A9B9C9D9E9F");
            Assert.True(ConnectionTarget.TryCreate(new Guid("11111111-2222-3333-4444-555555555555"),
                IPAddress.Loopback, 12345, Convert.ToHexString(pin), out ConnectionTarget? target));
            Assert.True(ConnectionIdentity.TryCreate(target!, pin, out ConnectionIdentity? identity));
            Connection = new TlsConnection(identity!, Client, Ssl);
        }

        internal void AssertClosedOnce()
        {
            Assert.True(RequestedClose.IsCompletedSuccessfully);
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
        protected override void Dispose(bool disposing)
        {
            if (disposing) Interlocked.Increment(ref _disposeCalls);
            base.Dispose(disposing);
        }
        internal void ReleaseResources() => base.Dispose(true);
    }

    private sealed class ProbeSslStream() : SslStream(new MemoryStream(), leaveInnerStreamOpen: false)
    {
        private readonly List<Task<int>> _originalReads = [];
        private int _offset;
        private int _readPaused;
        private int _disposeCalls;
        internal byte[] Input { get; set; } = [];
        internal Pause? ReadPause { get; set; }
        internal Func<Task<int>>? ReadAtGate { get; set; }
        internal Pause? ClosePause { get; set; }
        internal Exception? DisposeFailure { get; set; }
        internal int GateReadAt { get; set; } = int.MaxValue;
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        internal CancellationToken LastReadToken { get; private set; }
        internal Task<int>[] OriginalReads
        {
            get { lock (_originalReads) return _originalReads.ToArray(); }
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (_originalReads)
            {
                LastReadToken = cancellationToken;
                Task<int> original = _offset >= GateReadAt && ReadAtGate is not null
                    ? ReadAtGate() : ReadCoreAsync(buffer);
                _originalReads.Add(original);
                return new ValueTask<int>(original);
            }
        }

        private async Task<int> ReadCoreAsync(Memory<byte> buffer)
        {
            // 故意不响应 token/Dispose：验证 close 完成绝不能替代原读取 Task 的 join。
            if (_offset >= GateReadAt && Interlocked.Exchange(ref _readPaused, 1) == 0 && ReadPause is not null)
                await ReadPause.WaitAsync().ConfigureAwait(false);
            int count = Math.Min(buffer.Length, Input.Length - _offset);
            Input.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    Interlocked.Increment(ref _disposeCalls);
                    ClosePause?.WaitSynchronously();
                    if (DisposeFailure is not null) throw DisposeFailure;
                }
            }
            finally { base.Dispose(disposing); }
        }

        internal void ReleaseResources() => base.Dispose(true);
    }
}
