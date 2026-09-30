using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit.Sdk;
using LanRemote.Core.Models;
using Child = LanRemote.Transport.AuthenticatedControlSession.ClientVideoLifetime;

namespace LanRemote.Transport.Tests;

// 独立的执行期 deadline 合同；保留原 37 例并新增 11 例，共 48 个展开例。
// 不使用真实 TLS、墙钟或延时推进预算；可取消 Delay 仅作 Guard，finally 取消以释放保护 timer。
public sealed class ClientVideoAttachDeadlineTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private const int TestTimeout = 60_000;
    private const long SuccessAt = 72_000_000_000L;
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    [ThreadStatic] private static bool _insideTimerCallback;

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ControlOnly_AndBudgetQueries_NeverCreateTimerOrConsumeAttempt(bool query)
    {
        Fixture f = new();
        try
        {
            if (query)
            {
                f.Clock.Now = SuccessAt + TimeSpan.FromSeconds(4).Ticks;
                Assert.Equal(TimeSpan.FromSeconds(11), f.Parent.GetRemainingAttachBudget());
                Assert.Equal(TimeSpan.FromSeconds(11), f.Parent.GetRemainingAttachBudget());
                f.Clock.Now = f.Deadline;
                Assert.Throws<TimeoutException>(() => f.Parent.GetRemainingAttachBudget());
            }
            f.AssertControlLive();
            Assert.Null(f.RegisteredChild);
            Assert.Equal(0, f.Clock.CreateCalls);
            Assert.Equal(0, f.Timer.ChangeCalls);
            Assert.Equal(0, f.Timer.AsyncDisposeCalls);
            Assert.Equal(0, f.ConnectCalls);
            await f.Parent.CloseAndJoinAsync().WaitAsync(Guard);
            Assert.Equal(0, f.Clock.CreateCalls);
            Assert.Equal(0, f.Timer.SyncDisposeCalls);
            Assert.Equal(0, f.Timer.AsyncDisposeCalls);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(2_000, 500)]
    [InlineData(60_000, 4_000)]
    public async Task DefaultTimer_UsesOriginalSuccessAndCappedHint_NeitherPhaseRenewsBudget(int hint, int elapsedMs)
    {
        Fixture f = new(hint);
        Pause connect = f.NewPause();
        Pause initialize = f.NewPause();
        try
        {
            f.Clock.Now = SuccessAt + TimeSpan.FromMilliseconds(elapsedMs).Ticks;
            Task<Child> attach = f.Start(connect: _ => ConnectAsync(), initialize: (_, _) => initialize.WaitAsync());
            await Task.WhenAll(f.Connecting.Task, connect.Reached.Task).WaitAsync(Guard);
            Assert.Equal(1, f.Clock.CreateCalls);
            Assert.Equal(Timeout.InfiniteTimeSpan, f.Clock.CreatedDueTime);
            Assert.Equal(Timeout.InfiniteTimeSpan, f.Clock.CreatedPeriod);
            Assert.Equal(f.Budget - TimeSpan.FromMilliseconds(elapsedMs), (await f.Timer.ChangeAtAsync(0)).Due);
            Assert.Equal(Timeout.InfiniteTimeSpan, f.Timer.Changes[0].Period);

            f.Clock.Now = f.Deadline - TimeSpan.FromMilliseconds(250).Ticks;
            connect.Open();
            await Task.WhenAll(f.Initializing.Task, initialize.Reached.Task).WaitAsync(Guard);
            Assert.True(f.OriginalConnect!.IsCompletedSuccessfully);
            Assert.Equal(1, f.Clock.CreateCalls);
            Assert.All(f.Timer.Changes, change =>
                Assert.True(change.Due <= f.Budget - TimeSpan.FromMilliseconds(elapsedMs)));
            f.Clock.Now = f.Deadline;
            f.Timer.Fire();
            await f.Cancelled.Task.WaitAsync(Guard);
            Assert.False(f.OriginalInitialize!.IsCompleted);
            initialize.Open();
            Assert.IsType<TimeoutException>(await ErrorAsync(attach));
            Child child = f.Child;
            await f.JoinAsync(child);
            AssertTimeoutRecorded(child.LifetimeErrors);
            f.Video.AssertClosedOnce();
            f.Timer.AssertReleasedOnce();
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }

        async Task<TlsConnection> ConnectAsync()
        {
            await connect.WaitAsync();
            return f.Video.Connection;
        }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task SlowCreatePrefix_IsChargedToOriginalBudget_BeforeFirstChangeAndBeforeConnect()
    {
        Fixture f = new();
        Pause create = f.NewPause();
        Pause operation = f.NewPause();
        f.Clock.CreatePause = create;
        try
        {
            f.Clock.Now = SuccessAt + TimeSpan.FromSeconds(2).Ticks;
            Task<Task<Child>> invocation = f.Keep(Task.Factory.StartNew(
                () => f.StartBlocked("connect", operation), CancellationToken.None,
                TaskCreationOptions.DenyChildAttach, TaskScheduler.Default));
            await create.Reached.Task.WaitAsync(Guard);
            Task<Child> attach = await invocation.WaitAsync(Guard);
            await f.AssertGateAvailableAsync();
            Assert.Equal(0, f.ConnectCalls);
            f.Clock.Now = f.Deadline - TimeSpan.TicksPerMillisecond / 2;
            create.Open();
            await f.WaitForOperationAsync("connect", operation);
            Assert.Equal(TimeSpan.FromMilliseconds(1), (await f.Timer.ChangeAtAsync(0)).Due);
            f.Clock.Now = f.Deadline;
            f.Timer.Fire();
            await f.Cancelled.Task.WaitAsync(Guard);
            operation.Open();
            Assert.IsType<TimeoutException>(await ErrorAsync(attach));
            await f.JoinAsync(f.Child);
            Assert.Equal(0, f.InitializeCalls);
            f.Timer.AssertReleasedOnce();
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("connect")]
    [InlineData("initialize")]
    public async Task RuntimeExpiry_CancelsOriginalOperation_ButIgnoredCancellationCannotFinishAttachOrJoin(string phase)
    {
        Fixture f = new();
        Pause operation = f.NewPause();
        try
        {
            Task<Child> attach = f.StartBlocked(phase, operation);
            await f.WaitForOperationAsync(phase, operation);
            Task original = f.Original(phase);
            f.Clock.Now = f.Deadline;
            f.Timer.Fire();
            await f.Cancelled.Task.WaitAsync(Guard);
            await f.CancelTask.WaitAsync(Guard);
            await f.Timer.WaitForOriginalDisposeAsync();
            if (phase == "initialize") await f.Video.WaitForRequestedCloseAsync();
            Child child = f.Child;
            Task join = f.Keep(child.StopAndJoinAsync());
            Assert.True(f.Scheduler.RunQueued());
            // 已同步跑过 join 前缀，且取消、timer 释放、已知连接关闭均已真正结束。
            Assert.False(operation.IsOpen);
            Assert.False(original.IsCompleted);
            Assert.False(attach.IsCompleted);
            Assert.False(join.IsCompleted);
            Assert.True(f.OperationToken.IsCancellationRequested);
            Assert.Equal(phase == "connect" ? 0 : 1, f.InitializeCalls);
            f.AssertControlLive();

            operation.Open();
            await original.WaitAsync(Guard);
            Assert.IsType<TimeoutException>(await ErrorAsync(attach));
            await join.WaitAsync(Guard);
            f.Video.AssertClosedOnce();
            f.Timer.AssertReleasedOnce();
            AssertTimeoutRecorded(child.LifetimeErrors);
            AssertTimeoutRecorded(f.Parent.LifetimeErrors);
            Assert.Throws<InvalidOperationException>(() => { _ = f.Start(); });
            Assert.Equal(1, f.ConnectCalls);
            Assert.Equal(phase == "connect" ? 0 : 1, f.InitializeCalls);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("connect", false)]
    [InlineData("initialize", true)]
    public async Task ExpiredOriginalIoFault_RemainsPrimary_WhileDiagnosticsAlsoKeepDeadlineReason(string phase, bool cancelledIo)
    {
        Fixture f = new();
        Pause operation = f.NewPause();
        using CancellationTokenSource ioCancellation = new();
        ioCancellation.Cancel();
        IOException leaf = new("原 I/O 的叶子错误。");
        Exception originalError = cancelledIo
            ? new OperationCanceledException("原 I/O 自身的取消不能改写为 Timeout。", leaf, ioCancellation.Token)
            : new IOException("不得被新 Timeout 覆盖的原 I/O await 错误。", leaf);
        try
        {
            Task<Child> attach = f.StartBlocked(phase, operation, originalError);
            await f.WaitForOperationAsync(phase, operation);
            f.Clock.Now = f.Deadline;
            f.Timer.Fire();
            await f.Cancelled.Task.WaitAsync(Guard);
            await f.Timer.WaitForOriginalDisposeAsync();
            operation.Open();
            Assert.Same(originalError, await ErrorAsync(attach));
            Assert.Same(originalError, await ErrorAsync(f.Original(phase)));
            await f.JoinAsync(f.Child);
            AssertTreeContains(f.Child.LifetimeErrors, originalError);
            AssertTreeContains(f.Child.LifetimeErrors, leaf);
            AssertTimeoutRecorded(f.Child.LifetimeErrors);
            AssertTreeContains(f.Parent.LifetimeErrors, originalError);
            AssertTimeoutRecorded(f.Parent.LifetimeErrors);
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("control")]
    [InlineData("video")]
    [InlineData("callback")]
    public async Task DeadlineStop_ParentCloseAndArbitraryCancellationCallbacks_AreIndependent(string blocked)
    {
        Fixture f = new();
        Pause operation = f.NewPause();
        Pause controlClose = f.NewPause(open: blocked != "control");
        Pause videoClose = f.NewPause(open: blocked != "video");
        Pause callback = f.NewPause(open: blocked != "callback");
        f.Control.Ssl.ClosePause = controlClose;
        f.Video.Ssl.ClosePause = videoClose;
        IOException callbackError = new("任意取消回调错误。");
        TaskCompletionSource callbackExited = Signal();
        try
        {
            Task<Child> attach = f.StartBlocked("initialize", operation);
            await f.WaitForOperationAsync("initialize", operation);
            f.Own(f.OperationToken.Register(() =>
            {
                try
                {
                    Assert.False(_insideTimerCallback);
                    callback.WaitSynchronously();
                    throw callbackError;
                }
                finally { callbackExited.TrySetResult(); }
            }));
            f.Clock.Now = f.Deadline;
            f.Timer.Fire();
            // callback 卡住时，不能把它后面的另一个 token 回调当作到期证据。
            await Task.WhenAll(callback.Reached.Task, videoClose.Reached.Task).WaitAsync(Guard);
            Task parentJoin = f.Keep(f.Parent.CloseAndJoinAsync());
            Task childJoin = f.Keep(f.Child.StopAndJoinAsync());
            await controlClose.Reached.Task.WaitAsync(Guard);
            await f.Timer.WaitForOriginalDisposeAsync();
            if (blocked != "control") await f.Control.RequestedClose.WaitAsync(Guard);
            if (blocked != "video") await f.Video.RequestedClose.WaitAsync(Guard);
            if (blocked != "callback")
            {
                await callbackExited.Task.WaitAsync(Guard);
                await f.CancelTask.WaitAsync(Guard);
            }
            operation.Open();
            await f.OriginalInitialize!.WaitAsync(Guard);
            Assert.IsType<ObjectDisposedException>(await ErrorAsync(attach));
            Assert.True(f.Scheduler.RunQueued());
            if (blocked == "control") await childJoin.WaitAsync(Guard);
            else Assert.False(childJoin.IsCompleted);
            Assert.False(parentJoin.IsCompleted);
            Assert.False((blocked == "control" ? controlClose : blocked == "video" ? videoClose : callback).IsOpen);

            controlClose.Open();
            videoClose.Open();
            callback.Open();
            await Task.WhenAll(childJoin, parentJoin).WaitAsync(Guard);
            await callbackExited.Task.WaitAsync(Guard);
            f.Control.AssertClosedOnce();
            f.Video.AssertClosedOnce();
            f.Timer.AssertReleasedOnce();
            AssertTreeContains(f.Child.LifetimeErrors, callbackError);
            AssertTreeContains(f.Parent.LifetimeErrors, callbackError);
            AssertTimeoutRecorded(f.Child.LifetimeErrors);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("create")]
    [InlineData("change")]
    [InlineData("manual")]
    public async Task EarlyCallbacks_IncludingSynchronousCreateAndChange_RearmMinimumOneMsWithoutGrace(string point)
    {
        Fixture f = new();
        Pause operation = f.NewPause();
        f.Clock.FireDuringCreate = point == "create";
        f.Timer.FireDuringFirstChange = point == "change";
        try
        {
            Task<Child> attach = f.StartBlocked("connect", operation);
            await f.WaitForOperationAsync("connect", operation);
            Assert.Equal(1, f.Clock.CreateCalls);
            Assert.Equal(Timeout.InfiniteTimeSpan, f.Clock.CreatedDueTime);
            Assert.Equal(Timeout.InfiniteTimeSpan, f.Clock.CreatedPeriod);
            Assert.False(f.OperationToken.IsCancellationRequested);
            Assert.Equal(f.Budget, f.Timer.Changes[0].Due);

            f.Clock.Now = f.Deadline - TimeSpan.TicksPerMillisecond / 2;
            f.Timer.Fire();
            ChangeRecord rearm = await f.Timer.WaitForDueAsync(TimeSpan.FromMilliseconds(1));
            Assert.Equal(Timeout.InfiniteTimeSpan, rearm.Period);
            Assert.False(f.OperationToken.IsCancellationRequested);
            Assert.False(operation.IsOpen);
            Assert.False(f.OriginalConnect!.IsCompleted);
            // floor 只作用于调度，预算仍在原 deadline 耗尽，不能续到这个 1ms 的末端。
            f.Clock.Now = f.Deadline;
            f.Timer.Fire();
            await f.Cancelled.Task.WaitAsync(Guard);
            operation.Open();
            Assert.IsType<TimeoutException>(await ErrorAsync(attach));
            await f.JoinAsync(f.Child);
            Assert.Equal(0, f.InitializeCalls);
            Assert.All(f.Timer.Changes, change => Assert.True(change.Due >= TimeSpan.FromMilliseconds(1)));
            f.Timer.AssertReleasedOnce();
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task CallbackStorm_OnlyCoalescesNotifications_ExternalClockRunsOnOneFixedWorkerOutsideCallback()
    {
        Fixture f = new();
        Pause operation = f.NewPause();
        Pause clockPrefix = f.NewPause();
        try
        {
            Task<Child> attach = f.StartBlocked("initialize", operation);
            await f.WaitForOperationAsync("initialize", operation);
            int reads = f.Clock.TimestampReads;
            f.Clock.TimestampProbe = clockPrefix.WaitSynchronously;
            f.Timer.Fire();
            await clockPrefix.Reached.Task.WaitAsync(Guard);
            Task burst = f.Keep(Task.Run(() =>
            {
                for (int i = 0; i < 1_024; i++) f.Timer.Fire();
            }));
            // 原 clock 同步前缀仍卡住；通知调用本身必须可以独立结束。
            await burst.WaitAsync(Guard);
            await f.AssertGateAvailableAsync();
            Assert.Equal(reads + 1, f.Clock.TimestampReads);
            Assert.Equal(1, f.Clock.MaximumConcurrentTimestampCalls);
            AssertNoWorkQueue(f.Child);
            f.Clock.Now = f.Deadline;
            f.Clock.TimestampProbe = null;
            clockPrefix.Open();
            await f.Cancelled.Task.WaitAsync(Guard);
            operation.Open();
            Assert.IsType<TimeoutException>(await ErrorAsync(attach));
            await f.JoinAsync(f.Child);
            Assert.InRange(f.Clock.TimestampReads - reads, 1, 2);
            Assert.InRange(f.Child.LifetimeErrors.Count, 1, 2);
            Assert.Equal(1, f.Clock.MaximumConcurrentTimestampCalls);
            f.Timer.AssertReleasedOnce();
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("create", false)]
    [InlineData("create", true)]
    [InlineData("change", false)]
    public async Task BlockedTimerPrefix_StopCannotAbandonFactory_LateTimerIsOwnedAndReleasedExactlyOnce(string point, bool closeParent)
    {
        Fixture f = new();
        Pause create = f.NewPause();
        Pause disposePrefix = f.NewPause();
        Pause disposeCompletion = f.NewPause();
        if (point == "create") f.Clock.CreatePause = create;
        else f.Timer.ChangePause = create;
        f.Timer.DisposePrefix = disposePrefix;
        f.Timer.DisposeCompletion = disposeCompletion;
        IOException cleanupError = new("停止后晚到 timer 的原释放故障。");
        f.Timer.DisposeFailures = [cleanupError];
        try
        {
            // 保留调用本身，不用 Unwrap 的代理冒充 StartVideoLifetimeAsync 返回的原任务。
            Task<Task<Child>> invocation = f.Keep(Task.Factory.StartNew(() => f.Start(),
                CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default));
            await create.Reached.Task.WaitAsync(Guard);
            Task<Child> attach = await invocation.WaitAsync(Guard);
            Child child = f.Child;
            Assert.Same(attach, Field<Task>(Field<object>(child, "_attach"), "Worker"));
            await f.AssertGateAvailableAsync();
            Task join = f.Keep(closeParent ? f.Parent.CloseAndJoinAsync() : child.StopAndJoinAsync());
            if (closeParent) await f.Control.WaitForRequestedCloseAsync();
            await f.CancelTask.WaitAsync(Guard);
            Assert.True(f.Scheduler.RunQueued());
            Assert.False(create.IsOpen);
            Assert.False(attach.IsCompleted);
            Assert.False(join.IsCompleted);
            Assert.Equal(0, f.ConnectCalls);
            Assert.Equal(0, f.Timer.AsyncDisposeCalls);

            create.Open();
            await disposePrefix.Reached.Task.WaitAsync(Guard);
            Assert.Same(child, f.RegisteredChild);
            Assert.Equal(1, f.Timer.AsyncDisposeCalls);
            Assert.Equal(0, f.Timer.SyncDisposeCalls);
            await f.AssertGateAvailableAsync();
            await AssertAwaitEdgesAsync(new AwaitEdge(f.DeadlineWorker, attach, child, "AttachCoreAsync"));
            child.RequestStop();
            f.Timer.Fire();
            Assert.False(join.IsCompleted);
            disposePrefix.Open();
            await Task.WhenAll(f.Timer.DisposeReturned.Task, disposeCompletion.Reached.Task).WaitAsync(Guard);
            Task originalDispose = Assert.IsAssignableFrom<Task>(f.Timer.OriginalDispose);
            Assert.False(originalDispose.IsCompleted);
            Assert.False(join.IsCompleted);
            await AssertAttachWaitsForOriginalDisposeAsync(f, attach, originalDispose);
            disposeCompletion.Open();
            await ErrorAsync(attach);
            await ErrorAsync(originalDispose);
            await join.WaitAsync(Guard);
            AssertTreeContains(child.LifetimeErrors, cleanupError);
            AssertTreeContains(f.Parent.LifetimeErrors, cleanupError);
            f.Timer.AssertReleasedOnce();
            Assert.Equal(0, f.ConnectCalls);
            Assert.Equal(0, f.InitializeCalls);
            Assert.False(f.Video.Connection.IsCloseRequested);
            Assert.Equal(0, f.Video.Ssl.DisposeCalls);
            if (!closeParent) f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("prefix")]
    [InlineData("async-single")]
    [InlineData("async-multiple")]
    public async Task TimerDispose_TracksSynchronousPrefixAndOriginalMultiFaultTask_ImmutableSnapshotsKeepWholeTrees(string mode)
    {
        Fixture f = new();
        Pause prefix = f.NewPause();
        Pause completion = f.NewPause();
        Pause videoClose = f.NewPause();
        f.Timer.DisposePrefix = prefix;
        f.Timer.DisposeCompletion = completion;
        f.Video.Ssl.ClosePause = videoClose;
        IOException firstLeaf = new("第一 timer 释放叶子。");
        IOException secondLeaf = new("第二 timer 释放叶子。");
        IOException thirdLeaf = new("兄弟分支叶子。");
        InvalidOperationException nested = new("不可丢弃的中间包装。", secondLeaf);
        AggregateException branch = new("不可 flatten 的用户聚合。", firstLeaf, nested);
        ApplicationException sibling = new("原 Task 的另一个独立错误。", thirdLeaf);
        IOException closeError = new("快照之后才出现的 Video 关闭错误。");
        f.Video.Ssl.DisposeFailure = closeError;
        if (mode == "prefix") f.Timer.DisposePrefixFailure = branch;
        else f.Timer.DisposeFailures = mode == "async-single" ? [branch] : [branch, sibling];
        try
        {
            Task<Child> attach = f.Start();
            await prefix.Reached.Task.WaitAsync(Guard);
            Child child = f.Child;
            IReadOnlyList<Exception> empty = child.LifetimeErrors;
            Assert.Empty(empty);
            Assert.True(f.OriginalInitialize!.IsCompletedSuccessfully);
            Assert.False(attach.IsCompleted);
            Assert.Equal(1, f.Timer.AsyncDisposeCalls);
            await f.AssertGateAvailableAsync();
            await AssertAwaitEdgesAsync(new AwaitEdge(f.DeadlineWorker, attach, child, "AttachCoreAsync"));
            prefix.Open();
            if (mode != "prefix")
            {
                await Task.WhenAll(f.Timer.DisposeReturned.Task, completion.Reached.Task).WaitAsync(Guard);
                Task original = Assert.IsAssignableFrom<Task>(f.Timer.OriginalDispose);
                Assert.False(original.IsCompleted);
                Assert.False(attach.IsCompleted);
                Assert.False(Field<bool>(child, "_delivered"));
                await AssertAttachWaitsForOriginalDisposeAsync(f, attach, original);
                completion.Open();
                await ErrorAsync(original);
                if (mode == "async-multiple") Assert.Equal(2, original.Exception!.InnerExceptions.Count);
            }
            Exception observed = await ErrorAsync(attach);
            await videoClose.Reached.Task.WaitAsync(Guard);
            Task join = f.Keep(child.StopAndJoinAsync());
            Assert.True(f.Scheduler.RunQueued());
            Assert.False(join.IsCompleted);
            IReadOnlyList<Exception> snapshot = child.LifetimeErrors;
            IReadOnlyList<Exception> parentSnapshot = f.Parent.LifetimeErrors;
            AssertTreeContains(snapshot, branch);
            AssertTreeContains(snapshot, nested);
            AssertTreeContains(snapshot, firstLeaf);
            AssertTreeContains(snapshot, secondLeaf);
            AssertTreeContains(snapshot, observed);
            if (mode == "async-multiple")
            {
                AggregateException originalTree = f.Timer.OriginalDispose!.Exception!;
                AggregateException saved = Assert.IsType<AggregateException>(Assert.Single(snapshot,
                    root => root is AggregateException aggregate && aggregate.InnerExceptions.Count == 2 &&
                        ReferenceEquals(aggregate.InnerExceptions[0], originalTree.InnerExceptions[0]) &&
                        ReferenceEquals(aggregate.InnerExceptions[1], originalTree.InnerExceptions[1])));
                AssertTreeContains([saved], sibling);
                AssertTreeContains([saved], thirdLeaf);
                AssertTreeContains(parentSnapshot, saved);
            }
            AssertReadOnly(snapshot);
            AssertReadOnly(parentSnapshot);
            Assert.NotSame(snapshot, child.LifetimeErrors);
            Assert.InRange(snapshot.Count, 1, 3);
            Assert.DoesNotContain(snapshot, root => ContainsReference(root, closeError));
            Exception[] before = snapshot.ToArray();
            videoClose.Open();
            await join.WaitAsync(Guard);
            Assert.Equal(before, snapshot);
            Assert.Empty(empty);
            AssertTreeContains(child.LifetimeErrors, closeError);
            AssertTreeContains(f.Parent.LifetimeErrors, closeError);
            Assert.DoesNotContain(parentSnapshot, root => ContainsReference(root, closeError));
            f.Video.AssertClosedOnce();
            f.Timer.AssertReleasedOnce();
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("success")]
    [InlineData("elapsed")]
    [InlineData("caller")]
    [InlineData("parent")]
    [InlineData("child")]
    public async Task Delivery_WaitsForOriginalTimerRelease_ThenRechecksOriginalBudgetAndJointState(string finalState)
    {
        Fixture f = new();
        Pause prefix = f.NewPause();
        Pause completion = f.NewPause();
        f.Timer.DisposePrefix = prefix;
        f.Timer.DisposeCompletion = completion;
        using CancellationTokenSource caller = new();
        int finalSamples = 0;
        try
        {
            Task<Child> attach = f.Start(token: caller.Token, initialize: (_, _) =>
            {
                // 独立的初始化事件安装末检探针，不按第 N 次取时猜测阶段。
                f.Clock.TimestampProbe = () =>
                {
                    Assert.True(f.Timer.OriginalDispose?.IsCompletedSuccessfully == true,
                        "成功末检必须在原 DisposeAsync Task 完成后，不能仅等待代理通知。");
                    Interlocked.Increment(ref finalSamples);
                };
                return Task.CompletedTask;
            });
            await prefix.Reached.Task.WaitAsync(Guard);
            await f.AssertGateAvailableAsync();
            Assert.False(attach.IsCompleted);
            Assert.False(Field<bool>(f.Child, "_delivered"));
            await AssertAwaitEdgesAsync(new AwaitEdge(f.DeadlineWorker, attach, f.Child, "AttachCoreAsync"));
            prefix.Open();
            await Task.WhenAll(f.Timer.DisposeReturned.Task, completion.Reached.Task).WaitAsync(Guard);
            Task originalDispose = Assert.IsAssignableFrom<Task>(f.Timer.OriginalDispose);
            Assert.False(originalDispose.IsCompleted);
            Assert.False(attach.IsCompleted);
            await AssertAttachWaitsForOriginalDisposeAsync(f, attach, originalDispose);
            Assert.Equal(0, Volatile.Read(ref finalSamples));
            int reads = f.Clock.TimestampReads;
            // 已开始释放，旧通知现在也必须被停止，不能趁 DisposeAsync await 尚未结束继续采样。
            for (int i = 0; i < 16; i++) f.Timer.Fire();
            if (finalState == "elapsed") f.Clock.Now = f.Deadline;
            if (finalState == "caller") caller.Cancel();
            if (finalState == "parent") f.Parent.RevokeForOwnerCleanup();
            if (finalState == "child") f.Child.RequestStop();
            Assert.False(completion.IsOpen);
            completion.Open();
            await originalDispose.WaitAsync(Guard);
            if (finalState == "success")
            {
                Assert.Same(f.Child, await attach.WaitAsync(Guard));
                Assert.True(Volatile.Read(ref finalSamples) > 0);
                Assert.False(f.Video.Connection.IsCloseRequested);
            }
            else
            {
                Exception error = await ErrorAsync(attach);
                if (finalState == "elapsed") Assert.IsType<TimeoutException>(error);
                else if (finalState == "caller")
                    Assert.Equal(caller.Token, Assert.IsAssignableFrom<OperationCanceledException>(error).CancellationToken);
                else Assert.IsType<ObjectDisposedException>(error);
                Assert.False(Field<bool>(f.Child, "_delivered"));
            }
            // 成功路径只有最终预算采样；停止后的重复 callback 不应增加任何额外采样。
            Assert.Equal(reads + Volatile.Read(ref finalSamples), f.Clock.TimestampReads);
            f.Clock.TimestampProbe = null;
            await f.JoinAsync(f.Child);
            f.Timer.AssertReleasedOnce();
            f.Video.AssertClosedOnce();
            if (finalState != "parent") f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("parent", false)]
    [InlineData("caller", true)]
    [InlineData("deadline", false)]
    public async Task CheckActive_PrioritizesParentThenCallerThenDeadlineFailureOverOrdinaryStop(string winner, bool clockFault)
    {
        Fixture f = new();
        Pause operation = f.NewPause();
        using CancellationTokenSource caller = new();
        IOException failure = new("deadline 的原 clock 故障。");
        try
        {
            Task<Child> attach = f.StartBlocked("initialize", operation, token: caller.Token);
            await f.WaitForOperationAsync("initialize", operation);
            if (clockFault) f.Clock.TimestampProbe = () => throw failure;
            else f.Clock.Now = f.Deadline;
            f.Timer.Fire();
            await f.Cancelled.Task.WaitAsync(Guard);
            await f.Timer.WaitForOriginalDisposeAsync();
            f.Child.RequestStop();
            if (winner is "parent" or "caller") caller.Cancel();
            if (winner == "parent") f.Parent.RevokeForOwnerCleanup();
            operation.Open();
            Exception error = await ErrorAsync(attach);
            if (winner == "parent")
                Assert.Equal(typeof(AuthenticatedControlSession).FullName, Assert.IsType<ObjectDisposedException>(error).ObjectName);
            else if (winner == "caller")
                Assert.Equal(caller.Token, Assert.IsAssignableFrom<OperationCanceledException>(error).CancellationToken);
            else Assert.IsType<TimeoutException>(error);
            await f.JoinAsync(f.Child);
            if (clockFault)
            {
                AssertTreeContains(f.Child.LifetimeErrors, failure);
                AssertTreeContains(f.Parent.LifetimeErrors, failure);
            }
            else
            {
                AssertTimeoutRecorded(f.Child.LifetimeErrors);
                AssertTimeoutRecorded(f.Parent.LifetimeErrors);
            }
            f.Video.AssertClosedOnce();
            if (winner != "parent") f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("create")]
    [InlineData("change-throw")]
    [InlineData("change-false")]
    [InlineData("timestamp")]
    [InlineData("frequency")]
    public async Task TimerInfrastructureFailure_PreventsBothFactories_RecordsOriginalFailureAndPermanentlyConsumesAttempt(string point)
    {
        Fixture f = new();
        IOException failure = new("deadline 基础设施原错误。");
        if (point == "create") f.Clock.CreateFailure = failure;
        if (point == "change-throw") f.Timer.ChangeFailure = failure;
        if (point == "change-false") f.Timer.ChangeResult = false;
        if (point is "timestamp" or "frequency")
        {
            f.Clock.AfterCreatePrefix = () =>
            {
                if (point == "timestamp") f.Clock.TimestampProbe = () => throw failure;
                else f.Clock.FrequencyProbe = () => throw failure;
            };
        }
        try
        {
            Task<Child> attach = f.Start();
            Exception error = await ErrorAsync(attach);
            if (point != "change-false") Assert.Same(failure, error);
            else
            {
                Assert.IsNotType<ObjectDisposedException>(error);
                Assert.False(error is OperationCanceledException);
            }
            Child child = f.Child;
            await f.JoinAsync(child);
            Assert.Equal(0, f.ConnectCalls);
            Assert.Equal(0, f.InitializeCalls);
            Assert.False(f.Video.Connection.IsCloseRequested);
            AssertTreeContains(child.LifetimeErrors, error);
            AssertTreeContains(f.Parent.LifetimeErrors, error);
            Assert.InRange(child.LifetimeErrors.Count, 1, 2);
            if (point == "create") Assert.Equal(0, f.Timer.AsyncDisposeCalls);
            else f.Timer.AssertReleasedOnce();
            Assert.Equal(0, f.Timer.SyncDisposeCalls);
            f.Clock.ClearProbes();
            f.Clock.Now = SuccessAt;
            Assert.Throws<InvalidOperationException>(() => { _ = f.Start(); });
            Assert.Same(child, f.RegisteredChild);
            Assert.Equal(1, f.Clock.CreateCalls);
            Assert.Equal(0, f.ConnectCalls);
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task DeliveredChild_OldTimerCallbacksHaveNoEffectAndNeverSampleClock()
    {
        Fixture f = new();
        using CancellationTokenSource caller = new();
        try
        {
            Child child = await f.Start(token: caller.Token).WaitAsync(Guard);
            f.Timer.AssertReleasedOnce();
            int timestamps = f.Clock.TimestampReads;
            int frequencies = f.Clock.FrequencyReads;
            f.Clock.Now = f.Deadline + TimeSpan.FromDays(1).Ticks;
            f.Clock.TimestampProbe = () => throw new IOException("交付后的旧通知不得读取 timestamp。");
            f.Clock.FrequencyProbe = () => throw new IOException("交付后的旧通知不得读取 frequency。");
            Task callbacks = f.Keep(Task.Run(() =>
            {
                for (int i = 0; i < 128; i++) f.Timer.Fire();
            }));
            await callbacks.WaitAsync(Guard);
            caller.Cancel();
            Assert.False(f.OperationToken.IsCancellationRequested);
            Assert.False(f.Video.Connection.IsCloseRequested);
            // 真实 reader 读受控 SSL 的 EOF；不做 TLS，也不以计数瞬时值代替交付后可用性。
            Assert.Null(await f.Keep(child.ReadFrameAsync()).WaitAsync(Guard));
            await f.JoinAsync(child);
            Assert.Equal(timestamps, f.Clock.TimestampReads);
            Assert.Equal(frequencies, f.Clock.FrequencyReads);
            Assert.Empty(child.LifetimeErrors);
            Assert.Empty(f.Parent.LifetimeErrors);
            f.Timer.AssertReleasedOnce();
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task PureOwnerRevoke_DuringAttach_DoesNotItselfCloseConnectionsOrCancelCallbacks()
    {
        Fixture f = new();
        Pause operation = f.NewPause();
        Pause controlClose = f.NewPause();
        Pause videoClose = f.NewPause();
        Pause callback = f.NewPause();
        f.Control.Ssl.ClosePause = controlClose;
        f.Video.Ssl.ClosePause = videoClose;
        try
        {
            Task<Child> attach = f.StartBlocked("initialize", operation);
            await f.WaitForOperationAsync("initialize", operation);
            Child child = f.Child;
            f.Own(f.OperationToken.Register(callback.WaitSynchronously));
            byte[] secret = Field<byte[]>(f.Parent, "_sessionToken");
            Task revoke = f.Keep(Task.Run(() =>
            {
                f.Parent.RevokeForOwnerCleanup();
                f.Parent.RevokeForOwnerCleanup();
            }));
            Assert.Same(revoke, await Task.WhenAny(revoke, controlClose.Reached.Task,
                videoClose.Reached.Task, callback.Reached.Task).WaitAsync(Guard));
            await revoke.WaitAsync(Guard);
            lock (f.ParentGate)
            {
                // 检查是否已经登记副作用，避免异步 worker 还没被调度造成假阴性。
                Assert.Null(Field<Task?>(f.Parent, "_closeAndJoin"));
                Assert.Null(Field<Task?>(child, "_videoClose"));
                Assert.Null(Field<Task?>(child, "_readerDispose"));
                Assert.Null(Field<Task?>(child, "_cancel"));
                Assert.Null(Field<Task?>(child, "_join"));
            }
            Assert.All(secret, value => Assert.Equal((byte)0, value));
            Assert.False(f.OperationToken.IsCancellationRequested);
            Assert.False(f.Control.Connection.IsCloseRequested);
            Assert.False(f.Video.Connection.IsCloseRequested);
            Assert.Equal(0, f.Control.Ssl.DisposeCalls);
            Assert.Equal(0, f.Video.Ssl.DisposeCalls);
            Assert.False(f.OriginalInitialize!.IsCompleted);
            Assert.Throws<ObjectDisposedException>(() => { _ = f.Parent.Stream; });

            Task join = f.Keep(f.Parent.CloseAndJoinAsync());
            await Task.WhenAll(controlClose.Reached.Task, videoClose.Reached.Task, callback.Reached.Task).WaitAsync(Guard);
            controlClose.Open();
            videoClose.Open();
            callback.Open();
            await Task.WhenAll(f.Control.RequestedClose, f.Video.RequestedClose, f.CancelTask).WaitAsync(Guard);
            Assert.True(f.Scheduler.RunQueued());
            Assert.False(join.IsCompleted);
            operation.Open();
            Assert.IsType<ObjectDisposedException>(await ErrorAsync(attach));
            await join.WaitAsync(Guard);
            f.Control.AssertClosedOnce();
            f.Video.AssertClosedOnce();
            f.Timer.AssertReleasedOnce();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("timestamp", false)]
    [InlineData("frequency", false)]
    [InlineData("timestamp", true)]
    [InlineData("frequency", true)]
    public async Task BlockedClockPrefix_StopCannotFinishJoinUntilOriginalDeadlineWorkerExits(string point, bool runtime)
    {
        Fixture f = new();
        Pause clockPrefix = f.NewPause();
        Pause operation = f.NewPause();
        void InstallProbe()
        {
            if (point == "timestamp") f.Clock.TimestampProbe = clockPrefix.WaitSynchronously;
            else f.Clock.FrequencyProbe = clockPrefix.WaitSynchronously;
        }
        if (!runtime) f.Clock.AfterCreatePrefix = InstallProbe;
        try
        {
            Task<Child> attach = f.StartBlocked("initialize", operation);
            if (runtime)
            {
                await f.WaitForOperationAsync("initialize", operation);
                InstallProbe();
                f.Timer.Fire();
            }
            await clockPrefix.Reached.Task.WaitAsync(Guard);
            await f.AssertGateAvailableAsync();
            Task deadlineWorker = f.DeadlineWorker;
            if (!runtime)
                await AssertAwaitEdgesAsync(new AwaitEdge(f.DeadlineReady, attach, f.Child, "AttachCoreAsync"));

            Task join = f.Keep(f.Child.StopAndJoinAsync());
            await f.CancelTask.WaitAsync(Guard);
            if (runtime)
            {
                await f.Video.WaitForRequestedCloseAsync();
                operation.Open();
                await f.OriginalInitialize!.WaitAsync(Guard);
                // 原 I/O、关闭及取消已退出，只剩真正阻塞在 clock 前缀的 deadline worker。
                await AssertAwaitEdgesAsync(new AwaitEdge(deadlineWorker, attach, f.Child, "AttachCoreAsync"));
            }
            else
                await AssertAwaitEdgesAsync(new AwaitEdge(f.DeadlineReady, attach, f.Child, "AttachCoreAsync"));
            Assert.True(f.Scheduler.RunQueued());
            Assert.False(clockPrefix.IsOpen);
            Assert.False(deadlineWorker.IsCompleted);
            Assert.False(attach.IsCompleted);
            Assert.False(join.IsCompleted);
            Assert.Equal(0, f.Timer.AsyncDisposeCalls);
            Assert.Equal(runtime ? 1 : 0, f.ConnectCalls);
            Assert.Equal(runtime ? 1 : 0, f.InitializeCalls);
            f.AssertControlLive();

            f.Clock.ClearProbes();
            clockPrefix.Open();
            Assert.IsType<ObjectDisposedException>(await ErrorAsync(attach));
            await join.WaitAsync(Guard);
            await deadlineWorker.WaitAsync(Guard);
            f.Timer.AssertReleasedOnce();
            Assert.DoesNotContain(f.Child.LifetimeErrors, ContainsTimeout);
            if (runtime) f.Video.AssertClosedOnce();
            else Assert.False(f.Video.Connection.IsCloseRequested);
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task SlowChangePrefix_CrossingOriginalDeadline_RejectsBeforeConnectWithoutRenewingBudget()
    {
        Fixture f = new();
        Pause change = f.NewPause();
        f.Timer.ChangePause = change;
        try
        {
            f.Clock.Now = SuccessAt + TimeSpan.FromSeconds(3).Ticks;
            Task<Task<Child>> invocation = f.Keep(Task.Factory.StartNew(() => f.Start(),
                CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default));
            await change.Reached.Task.WaitAsync(Guard);
            Task<Child> attach = await invocation.WaitAsync(Guard);
            await f.AssertGateAvailableAsync();
            Assert.Equal(TimeSpan.FromSeconds(12), Assert.Single(f.Timer.Changes).Due);
            Assert.Equal(0, f.ConnectCalls);
            f.Clock.Now = f.Deadline + 1;
            change.Open();
            Exception error = await ErrorAsync(attach);
            Assert.IsType<TimeoutException>(error);
            await f.JoinAsync(f.Child);
            Assert.Equal(0, f.ConnectCalls);
            Assert.Equal(0, f.InitializeCalls);
            Assert.Equal(1, f.Timer.ChangeCalls);
            AssertTreeContains(f.Child.LifetimeErrors, error);
            AssertTreeContains(f.Parent.LifetimeErrors, error);
            f.Timer.AssertReleasedOnce();
            Assert.False(f.Video.Connection.IsCloseRequested);
            Assert.Throws<InvalidOperationException>(() => { _ = f.Start(); });
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("change")]
    [InlineData("timestamp")]
    [InlineData("frequency")]
    public async Task TimerAndClockSynchronousReentry_RequestStopWithoutWaitingOwnJoin_PreventsConnect(string point)
    {
        Fixture f = new();
        TaskCompletionSource stopReturned = Signal();
        int reentries = 0;
        void ReenterStop()
        {
            f.AssertExternalTimerCall();
            Assert.Equal(1, Interlocked.Increment(ref reentries));
            // 在原同步前缀只请求停止；不得同步等待包含自身的 join。
            f.Child.RequestStop();
            stopReturned.TrySetResult();
        }
        if (point == "change") f.Timer.ChangeProbe = ReenterStop;
        else f.Clock.AfterCreatePrefix = () =>
        {
            if (point == "timestamp") f.Clock.TimestampProbe = ReenterStop;
            else f.Clock.FrequencyProbe = ReenterStop;
        };
        try
        {
            Task<Child> attach = f.Start();
            await stopReturned.Task.WaitAsync(Guard);
            Assert.IsType<ObjectDisposedException>(await ErrorAsync(attach));
            await f.JoinAsync(f.Child);
            Assert.Equal(1, Volatile.Read(ref reentries));
            Assert.Equal(1, f.Clock.CreateCalls);
            Assert.Equal(point == "change" ? 1 : 0, f.Timer.ChangeCalls);
            Assert.Equal(0, f.ConnectCalls);
            Assert.Equal(0, f.InitializeCalls);
            Assert.DoesNotContain(f.Child.LifetimeErrors, ContainsTimeout);
            f.Timer.AssertReleasedOnce();
            Assert.False(f.Video.Connection.IsCloseRequested);
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimerDisposeAsync_DrainsActualInFlightFireTail_BeforeDeliveryOrStopJoin(bool stop)
    {
        Fixture f = new();
        Pause operation = f.NewPause();
        Pause tail = f.NewPause();
        try
        {
            Task<Child> attach = f.StartBlocked("initialize", operation);
            await f.WaitForOperationAsync("initialize", operation);
            Task fire = f.Timer.FireWithTail(tail);
            await tail.Reached.Task.WaitAsync(Guard);
            Task? join = stop ? f.Keep(f.Child.StopAndJoinAsync()) : null;
            operation.Open();
            await f.OriginalInitialize!.WaitAsync(Guard);
            await f.Timer.DisposeReturned.Task.WaitAsync(Guard);
            Task originalDispose = Assert.IsAssignableFrom<Task>(f.Timer.OriginalDispose);
            await AssertAttachWaitsForOriginalDisposeAsync(f, attach, originalDispose);
            await AssertAwaitEdgesAsync(new AwaitEdge(fire, originalDispose, f.Timer, "CompleteAsync"));
            if (stop)
            {
                await f.CancelTask.WaitAsync(Guard);
                await f.Video.WaitForRequestedCloseAsync();
                Assert.True(f.Scheduler.RunQueued());
                Assert.False(join!.IsCompleted);
            }
            else
            {
                Assert.False(f.OperationToken.IsCancellationRequested);
                Assert.False(f.Video.Connection.IsCloseRequested);
            }
            // 四个实际 Task 的依赖链均已见到：attach -> deadline -> DisposeAsync -> 原 Fire。
            Assert.False(tail.IsOpen);
            Assert.False(fire.IsCompleted);
            Assert.False(originalDispose.IsCompleted);
            Assert.False(f.DeadlineWorker.IsCompleted);
            Assert.False(attach.IsCompleted);
            Assert.False(Field<bool>(f.Child, "_delivered"));
            int timestampReads = f.Clock.TimestampReads;
            int frequencyReads = f.Clock.FrequencyReads;
            for (int i = 0; i < 16; i++) f.Timer.Fire();
            // 尾部开闸前不允许终检；如果生产提前交付，此探针和已观察的 awaiter 链会分别报错。
            f.Clock.TimestampProbe = () =>
            {
                Assert.True(fire.IsCompletedSuccessfully);
                Assert.True(originalDispose.IsCompletedSuccessfully);
                Assert.True(f.DeadlineWorker.IsCompletedSuccessfully);
            };
            tail.Open();
            await fire.WaitAsync(Guard);
            await originalDispose.WaitAsync(Guard);
            if (stop)
            {
                Assert.IsType<ObjectDisposedException>(await ErrorAsync(attach));
                await join!.WaitAsync(Guard);
                Assert.Equal(timestampReads, f.Clock.TimestampReads);
                Assert.Equal(frequencyReads, f.Clock.FrequencyReads);
            }
            else
            {
                Assert.Same(f.Child, await attach.WaitAsync(Guard));
                Assert.Empty(f.Child.LifetimeErrors);
                Assert.False(f.Video.Connection.IsCloseRequested);
            }
            f.Clock.ClearProbes();
            await f.JoinAsync(f.Child);
            f.Timer.AssertReleasedOnce();
            f.Video.AssertClosedOnce();
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task EveryChangeSynchronouslyCallsBack_ProductionBoundFailsBeforeTestRecordCapacity()
    {
        Fixture f = new();
        Pause operation = f.NewPause();
        f.Timer.FireDuringEveryChange = true;
        try
        {
            Task<Child> attach = f.StartBlocked("connect", operation);
            await f.Timer.WaitForOriginalDisposeAsync();
            await f.DeadlineWorker.WaitAsync(Guard);
            Assert.Equal(33, f.Timer.ChangeCalls);
            // ready 首次放行后 connect 可能已开始；其原任务仍归 owner，不能用计时猜哪边先调度。
            operation.Open();
            InvalidOperationException error = Assert.IsType<InvalidOperationException>(await ErrorAsync(attach));
            Assert.Equal("附着计时器连续同步提前回调，无法建立等待。", error.Message);
            await f.JoinAsync(f.Child);
            Assert.InRange(f.ConnectCalls, 0, 1);
            Assert.Equal(0, f.InitializeCalls);
            Assert.Equal(1, f.Clock.CreateCalls);
            AssertTreeContains(f.Child.LifetimeErrors, error);
            AssertTreeContains(f.Parent.LifetimeErrors, error);
            AssertNoWorkQueue(f.Child);
            Assert.All(f.Timer.Changes, change => Assert.Equal(f.Budget, change.Due));
            f.Timer.AssertReleasedOnce();
            Assert.Throws<InvalidOperationException>(() => { _ = f.Start(); });
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    private static async Task<Exception> ErrorAsync(Task original)
    {
        using CancellationTokenSource guardCancellation = new();
        Task guardTask = Task.Delay(Guard, guardCancellation.Token);
        try
        {
            Task winner = await Task.WhenAny(original, guardTask);
            Assert.Same(original, winner);
            // 只捕获原任务：即使 Guard 获胜后原任务恰好完成，也绝不能冒充业务 Timeout。
            Exception? error = await Record.ExceptionAsync(() => original);
            Assert.NotNull(error);
            Assert.True(original.IsCompleted, "Guard 只是死锁保护，不是原操作退出的证据。");
            return error;
        }
        finally { guardCancellation.Cancel(); }
    }

    private static async Task ObserveAsync(Task original)
    {
        using CancellationTokenSource guardCancellation = new();
        Task guardTask = Task.Delay(Guard, guardCancellation.Token);
        try
        {
            Task winner = await Task.WhenAny(original, guardTask);
            Assert.Same(original, winner);
            try { await original; }
            catch { _ = original.Exception; }
        }
        finally { guardCancellation.Cancel(); }
    }

    private readonly record struct AwaitEdge(Task Awaited, Task PublishedWorker, object Owner, string Method);

    private static async Task AssertAwaitEdgesAsync(params AwaitEdge[] edges)
    {
        using CancellationTokenSource guardCancellation = new();
        Task guardTask = Task.Delay(Guard, guardCancellation.Token);
        try
        {
            // 正向观察真实 awaiter 的登记，不以一次 IsCompleted、延时或 TCS 通知判定已挂起。
            // 仅让出执行权等待登记可见；Guard 不参与产品预算，也不把“尚未调度”判为通过。
            while (!edges.All(HasAwaitEdge))
            {
                if (guardTask.IsCompleted)
                    throw new XunitException("Guard：未观察到指定原任务的 awaiter 链；运行时布局不支持时也必须显式失败。");
                foreach (AwaitEdge edge in edges)
                {
                    Assert.False(edge.Awaited.IsCompleted, "原等待对象已退出，无法证明受闸控制时的 await 关系。");
                    Assert.False(edge.PublishedWorker.IsCompleted, "实际 worker 已退出，不能用通知或瞬时状态冒充 await。");
                }
                await Task.Yield();
            }
        }
        finally { guardCancellation.Cancel(); }
    }

    private static bool HasAwaitEdge(AwaitEdge edge)
    {
        Type stateMachineType = edge.Owner.GetType().GetMethod(edge.Method, Fields)?
            .GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
            ?? throw new XunitException($"缺少实际异步状态机元数据：{edge.Owner.GetType().Name}.{edge.Method}。");
        foreach (object continuation in Continuations(edge.Awaited))
        {
            object? box = continuation is Delegate action ? action.Target : continuation;
            if (box is not Task coreTask) continue;
            object? stateMachine = RuntimeField(box.GetType(), "StateMachine")?.GetValue(box);
            if (stateMachine is null || stateMachine.GetType() != stateMachineType) continue;
            if (!ReferenceEquals(RuntimeField(stateMachineType, "<>4__this")?.GetValue(stateMachine), edge.Owner)) continue;
            bool awaitingOriginal = stateMachineType.GetFields(Fields)
                .Where(field => field.Name.StartsWith("<>u__", StringComparison.Ordinal))
                .Select(field => field.GetValue(stateMachine))
                .OfType<object>()
                .Any(awaiter => awaiter.GetType().GetFields(Fields).Any(field =>
                    typeof(Task).IsAssignableFrom(field.FieldType) && ReferenceEquals(field.GetValue(awaiter), edge.Awaited)));
            // Task.Run 的 UnwrapPromise 必须确实登记在该状态机原 Task 上，不能认错同名状态机。
            if (awaitingOriginal && (ReferenceEquals(coreTask, edge.PublishedWorker) ||
                Continuations(coreTask).Any(value => ReferenceEquals(value, edge.PublishedWorker)))) return true;
        }
        return false;
    }

    private static object[] Continuations(Task task)
    {
        FieldInfo field = RuntimeField(typeof(Task), "m_continuationObject")
            ?? throw new XunitException("运行时缺少 Task continuation 字段；需适配布局或改用实例级窄调度接缝，不能跳过证明。");
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

    private static Task AssertAttachWaitsForOriginalDisposeAsync(Fixture f, Task<Child> attach, Task originalDispose)
    {
        Assert.Same(attach, Field<Task>(Field<object>(f.Child, "_attach"), "Worker"));
        return AssertAwaitEdgesAsync(
            new AwaitEdge(f.DeadlineWorker, attach, f.Child, "AttachCoreAsync"),
            new AwaitEdge(originalDispose, f.DeadlineWorker, f.DeadlineOwner, "RunAsync"));
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Fields)!.GetValue(instance)!;

    private static bool ContainsReference(Exception root, Exception expected) =>
        ReferenceEquals(root, expected) || (root is AggregateException aggregate
            ? aggregate.InnerExceptions.Any(inner => ContainsReference(inner, expected))
            : root.InnerException is { } inner && ContainsReference(inner, expected));

    private static bool ContainsTimeout(Exception root) => root is TimeoutException ||
        (root is AggregateException aggregate ? aggregate.InnerExceptions.Any(ContainsTimeout)
            : root.InnerException is { } inner && ContainsTimeout(inner));

    private static void AssertTreeContains(IEnumerable<Exception> errors, Exception expected) =>
        Assert.Contains(errors, root => ContainsReference(root, expected));

    private static void AssertTimeoutRecorded(IEnumerable<Exception> errors) => Assert.Contains(errors, ContainsTimeout);

    private static void AssertReadOnly(IReadOnlyList<Exception> snapshot)
    {
        if (snapshot is ICollection<Exception> collection)
        {
            Assert.True(collection.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => collection.Add(new IOException("不得修改诊断快照。")));
        }
        if (snapshot is IList list)
            Assert.Throws<NotSupportedException>(() => list.Add(new IOException("不得修改非泛型快照。")));
    }

    private static void AssertNoWorkQueue(Child child)
    {
        // 只检查本层固定 owner 的声明，不扫描 Task continuation 或运行时对象图。
        HashSet<object> visited = new(ReferenceEqualityComparer.Instance);
        Inspect(child, 0);
        void Inspect(object owner, int depth)
        {
            if (depth > 4 || !visited.Add(owner)) return;
            foreach (FieldInfo field in owner.GetType().GetFields(Fields))
            {
                Type type = field.FieldType;
                foreach (Type contract in type.GetInterfaces().Append(type))
                {
                    if (!contract.IsGenericType || contract.GetGenericTypeDefinition() != typeof(IEnumerable<>)) continue;
                    Type item = contract.GetGenericArguments()[0];
                    Assert.False(typeof(Task).IsAssignableFrom(item) || item == typeof(CancellationTokenRegistration) ||
                        item == typeof(object) || item.Name.Contains("Operation", StringComparison.Ordinal) ||
                        item.Name.Contains("Registration", StringComparison.Ordinal),
                        $"固定 deadline owner 不应保留通用工作队列：{owner.GetType().Name}.{field.Name}");
                }
                if (field.Name == "_parent") continue;
                object? value = field.GetValue(owner);
                if (value is not null && value.GetType().Assembly == typeof(Child).Assembly &&
                    value is not TlsConnection && value is not VideoFrameReader)
                    Inspect(value, depth + 1);
            }
        }
    }

    // TCS 只用作开闸/到达事件；原 connect、initialize、DisposeAsync 都保留实际 Task。
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
            using CancellationTokenSource guardCancellation = new();
            Task guardTask = Task.Delay(Guard, guardCancellation.Token);
            try
            {
                Task winner = Task.WhenAny(_release.Task, guardTask).GetAwaiter().GetResult();
                if (!ReferenceEquals(_release.Task, winner))
                    throw new XunitException("Guard：同步测试闸门未放行；这是死锁保护失败，不是业务 Timeout。");
                _release.Task.GetAwaiter().GetResult();
            }
            finally { guardCancellation.Cancel(); }
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
                Assert.False(_scheduled, "一个 child 只能登记一次共享 join。");
                _scheduled = true;
                _queued = task;
            }
        }
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
        internal bool RunQueued()
        {
            Task? task;
            lock (_gate) { task = _queued; _queued = null; }
            if (task is null) return false;
            Assert.True(TryExecuteTask(task));
            return true;
        }
    }

    private readonly record struct ChangeRecord(TimeSpan Due, TimeSpan Period);

    private sealed class ManualClock(Fixture fixture) : TimeProvider
    {
        private long _now = SuccessAt;
        private int _timestampReads;
        private int _frequencyReads;
        private int _createCalls;
        private int _activeTimestampCalls;
        private int _maximumConcurrentTimestampCalls;
        private Action? _timestampProbe;
        private Action? _frequencyProbe;
        internal long Now { get => Volatile.Read(ref _now); set => Volatile.Write(ref _now, value); }
        internal int TimestampReads => Volatile.Read(ref _timestampReads);
        internal int FrequencyReads => Volatile.Read(ref _frequencyReads);
        internal int CreateCalls => Volatile.Read(ref _createCalls);
        internal int MaximumConcurrentTimestampCalls => Volatile.Read(ref _maximumConcurrentTimestampCalls);
        internal TimeSpan CreatedDueTime { get; private set; }
        internal TimeSpan CreatedPeriod { get; private set; }
        internal Pause? CreatePause { get; set; }
        internal Exception? CreateFailure { get; set; }
        internal Action? AfterCreatePrefix { get; set; }
        internal bool FireDuringCreate { get; set; }
        internal Action? TimestampProbe { get => Volatile.Read(ref _timestampProbe); set => Volatile.Write(ref _timestampProbe, value); }
        internal Action? FrequencyProbe { get => Volatile.Read(ref _frequencyProbe); set => Volatile.Write(ref _frequencyProbe, value); }

        public override long GetTimestamp()
        {
            Assert.False(_insideTimerCallback, "timer callback 不得采样外部 clock。");
            Interlocked.Increment(ref _timestampReads);
            int active = Interlocked.Increment(ref _activeTimestampCalls);
            int maximum;
            do { maximum = Volatile.Read(ref _maximumConcurrentTimestampCalls); }
            while (active > maximum && Interlocked.CompareExchange(ref _maximumConcurrentTimestampCalls, active, maximum) != maximum);
            try
            {
                TimestampProbe?.Invoke();
                return Now;
            }
            finally { Interlocked.Decrement(ref _activeTimestampCalls); }
        }
        public override long TimestampFrequency
        {
            get
            {
                Assert.False(_insideTimerCallback, "timer callback 不得读取外部频率。");
                Interlocked.Increment(ref _frequencyReads);
                FrequencyProbe?.Invoke();
                return TimeSpan.TicksPerSecond;
            }
        }
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("测试禁止读取墙钟。");
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            fixture.AssertExternalTimerCall();
            Assert.Equal(1, Interlocked.Increment(ref _createCalls));
            CreatedDueTime = dueTime;
            CreatedPeriod = period;
            Assert.Equal(Timeout.InfiniteTimeSpan, dueTime);
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            fixture.Timer.Bind(callback, state);
            if (FireDuringCreate) fixture.Timer.Fire();
            CreatePause?.WaitSynchronously();
            if (CreateFailure is { } error) throw error;
            AfterCreatePrefix?.Invoke();
            return fixture.Timer;
        }
        internal void ClearProbes()
        {
            TimestampProbe = null;
            FrequencyProbe = null;
            AfterCreatePrefix = null;
        }
    }

    private sealed class ManualTimer(Fixture fixture) : ITimer
    {
        private readonly object _gate = new();
        private readonly ChangeRecord[] _changes = new ChangeRecord[64];
        private readonly TaskCompletionSource<ChangeRecord>[] _changed = Enumerable.Range(0, 64)
            .Select(_ => new TaskCompletionSource<ChangeRecord>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        private TimerCallback? _callback;
        private object? _state;
        private int _changeCalls;
        private int _asyncDisposeCalls;
        private int _syncDisposeCalls;
        private Task? _originalDispose;
        private Task? _inFlightFire;
        internal int ChangeCalls => Volatile.Read(ref _changeCalls);
        internal int AsyncDisposeCalls => Volatile.Read(ref _asyncDisposeCalls);
        internal int SyncDisposeCalls => Volatile.Read(ref _syncDisposeCalls);
        internal Task? OriginalDispose => Volatile.Read(ref _originalDispose);
        internal TaskCompletionSource DisposeReturned { get; } = Signal();
        internal Pause? DisposePrefix { get; set; }
        internal Pause? DisposeCompletion { get; set; }
        internal Exception? DisposePrefixFailure { get; set; }
        internal Exception[] DisposeFailures { get; set; } = [];
        internal Exception? ChangeFailure { get; set; }
        internal Pause? ChangePause { get; set; }
        internal bool ChangeResult { get; set; } = true;
        internal bool FireDuringFirstChange { get; set; }
        internal bool FireDuringEveryChange { get; set; }
        internal Action? ChangeProbe { get; set; }
        internal ChangeRecord[] Changes
        {
            get { lock (_gate) return _changes.Take(Math.Min(_changeCalls, _changes.Length)).ToArray(); }
        }
        internal void Bind(TimerCallback callback, object? state)
        {
            lock (_gate) { Assert.Null(_callback); _callback = callback; _state = state; }
        }
        internal void Fire() => FireCore(tail: null);
        internal Task FireWithTail(Pause tail)
        {
            lock (_gate)
            {
                Assert.Null(_inFlightFire);
                // 原执行 Task 覆盖生产 callback 以及 provider 尾部；先登记再允许其进入 FireCore。
                _inFlightFire = fixture.Keep(Task.Factory.StartNew(() => FireCore(tail),
                    CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default));
                return _inFlightFire;
            }
        }
        private void FireCore(Pause? tail)
        {
            TimerCallback callback;
            object? state;
            lock (_gate) { callback = Assert.IsType<TimerCallback>(_callback); state = _state; }
            bool previous = _insideTimerCallback;
            _insideTimerCallback = true;
            try { callback(state); }
            finally { _insideTimerCallback = previous; }
            // 到达此闸意味着生产回调已返回，但 timer provider 的这次回调执行还未退出。
            tail?.WaitSynchronously();
        }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            fixture.AssertExternalTimerCall();
            int index;
            ChangeRecord record = new(dueTime, period);
            lock (_gate)
            {
                index = _changeCalls++;
                Assert.InRange(index, 0, _changes.Length - 1);
                _changes[index] = record;
            }
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            // 下限由测试正文直接核对记录值；不要在模拟 provider 中抢先抛错，
            // 否则删除生产下限时会绕开连接闸，最后被 Guard 遮蔽真实合同失败。
            try
            {
                if (FireDuringEveryChange || (index == 0 && FireDuringFirstChange)) Fire();
                ChangePause?.WaitSynchronously();
                ChangeProbe?.Invoke();
                if (ChangeFailure is { } error) throw error;
                return ChangeResult;
            }
            finally { _changed[index].TrySetResult(record); }
        }
        internal Task<ChangeRecord> ChangeAtAsync(int index) => _changed[index].Task.WaitAsync(Guard);
        internal async Task<ChangeRecord> WaitForDueAsync(TimeSpan expected)
        {
            for (int i = 0; i < _changed.Length; i++)
            {
                ChangeRecord change = await ChangeAtAsync(i);
                if (change.Due == expected) return change;
            }
            throw new InvalidOperationException("有限重排记录中没有预期剩余预算。");
        }
        public void Dispose()
        {
            Interlocked.Increment(ref _syncDisposeCalls);
            throw new InvalidOperationException("必须调用并等待原 ITimer.DisposeAsync，不能降级为 Dispose。");
        }
        public ValueTask DisposeAsync()
        {
            fixture.AssertExternalTimerCall();
            Assert.Equal(1, Interlocked.Increment(ref _asyncDisposeCalls));
            DisposePrefix?.WaitSynchronously();
            if (DisposePrefixFailure is { } prefixError) throw prefixError;
            Task fire;
            lock (_gate) fire = _inFlightFire ?? Task.CompletedTask;
            Task original = DisposeFailures.Length > 1
                ? Task.WhenAll(DisposeFailures.Select(error => fixture.Keep(CompleteAsync(error, fire))))
                : CompleteAsync(DisposeFailures.SingleOrDefault(), fire);
            fixture.Keep(original);
            Volatile.Write(ref _originalDispose, original);
            DisposeReturned.TrySetResult();
            return new ValueTask(original);
        }
        private async Task CompleteAsync(Exception? failure, Task fire)
        {
            await fire.ConfigureAwait(false);
            if (DisposeCompletion is { } pause) await pause.WaitAsync().ConfigureAwait(false);
            if (failure is not null) throw failure;
        }
        internal async Task WaitForOriginalDisposeAsync()
        {
            await DisposeReturned.Task.WaitAsync(Guard);
            await ObserveAsync(Assert.IsAssignableFrom<Task>(OriginalDispose));
        }
        internal void AssertReleasedOnce()
        {
            Assert.Equal(1, AsyncDisposeCalls);
            Assert.Equal(0, SyncDisposeCalls);
            if (DisposePrefixFailure is null)
                Assert.True(OriginalDispose?.IsCompleted == true, "必须等待原释放 Task，而不是仅发出 Dispose 通知。");
        }
    }

    private sealed class Fixture
    {
        // 测试自己的收尾句柄也设置上限；不以无界队列模拟生产固定槽。
        private readonly ConcurrentBag<Task> _tasks = [];
        private readonly List<Pause> _pauses = [];
        private readonly List<CancellationTokenRegistration> _registrations = [];
        private int _connectCalls;
        private int _initializeCalls;
        internal Endpoint Control { get; } = new();
        internal Endpoint Video { get; } = new();
        internal ManualClock Clock { get; }
        internal ManualTimer Timer { get; }
        internal ManualJoinScheduler Scheduler { get; } = new();
        internal AuthenticatedControlSession Parent { get; }
        internal TimeSpan Budget { get; }
        internal long Deadline => SuccessAt + Budget.Ticks;
        internal object ParentGate => Field<object>(Parent, "_gate");
        internal Child? RegisteredChild => Field<Child?>(Parent, "_videoLifetime");
        internal Child Child => Assert.IsType<Child>(RegisteredChild);
        internal object DeadlineOwner => Field<object>(Child, "_deadline");
        internal Task DeadlineWorker
        {
            get { lock (ParentGate) return Field<Task>(DeadlineOwner, "_worker"); }
        }
        internal Task DeadlineReady => Field<TaskCompletionSource>(DeadlineOwner, "_ready").Task;
        internal TaskCompletionSource Connecting { get; } = Signal();
        internal TaskCompletionSource Initializing { get; } = Signal();
        internal TaskCompletionSource Cancelled { get; } = Signal();
        internal CancellationToken OperationToken { get; private set; }
        internal Task<TlsConnection>? OriginalConnect { get; private set; }
        internal Task? OriginalInitialize { get; private set; }
        internal int ConnectCalls => Volatile.Read(ref _connectCalls);
        internal int InitializeCalls => Volatile.Read(ref _initializeCalls);
        internal Task CancelTask
        {
            get { lock (ParentGate) return Field<Task>(Child, "_cancel"); }
        }
        internal Fixture(int hint = 15_000)
        {
            Budget = TimeSpan.FromMilliseconds(Math.Min(hint, 15_000));
            Clock = new(this);
            Timer = new(this);
            Parent = new AuthenticatedControlSession(Control.Connection, SessionPermission.Control,
                new Guid("00112233-4455-6677-8899-aabbccddeeff"), "ABCDEF",
                Convert.FromHexString("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F"),
                hint, SuccessAt, Clock);
            Parent.CommitDelivery();
        }
        internal Pause NewPause(bool open = false)
        {
            Pause pause = new(open);
            _pauses.Add(pause);
            return pause;
        }
        internal T Keep<T>(T task) where T : Task
        {
            Assert.True(_tasks.Count < 128, "测试只跟踪有限个原操作与共享 join。");
            _tasks.Add(task);
            return task;
        }
        internal void Own(CancellationTokenRegistration registration)
        {
            lock (_registrations) _registrations.Add(registration);
        }
        internal Task<Child> Start(Func<CancellationToken, Task<TlsConnection>>? connect = null,
            Func<TlsConnection, CancellationToken, Task>? initialize = null, CancellationToken token = default) =>
            Keep(Parent.StartVideoLifetimeAsync(cancellation =>
            {
                Interlocked.Increment(ref _connectCalls);
                OperationToken = cancellation;
                Own(cancellation.Register(() =>
                {
                    Assert.False(_insideTimerCallback, "timer callback 不得执行任意 token 回调。");
                    Cancelled.TrySetResult();
                }));
                OriginalConnect = Keep(connect is null ? Task.FromResult(Video.Connection) : connect(cancellation));
                Connecting.TrySetResult();
                return OriginalConnect;
            }, (connection, cancellation) =>
            {
                Interlocked.Increment(ref _initializeCalls);
                Assert.Same(Video.Connection, connection);
                Assert.Equal(OperationToken, cancellation);
                OriginalInitialize = Keep(initialize is null ? Task.CompletedTask : initialize(connection, cancellation));
                Initializing.TrySetResult();
                return OriginalInitialize;
            }, token, rent: null, frameRead: null, joinScheduler: Scheduler));

        internal Task<Child> StartBlocked(string phase, Pause pause, Exception? failure = null, CancellationToken token = default)
        {
            return Start(connect: phase == "connect" ? _ => ConnectAsync() : null,
                initialize: phase == "initialize" ? (_, _) => InitializeAsync() : null, token: token);

            // 不响应 token：原操作是否结束只由这个闸门决定，不能用可取消代理替代。
            async Task<TlsConnection> ConnectAsync()
            {
                await pause.WaitAsync();
                if (failure is not null) throw failure;
                return Video.Connection;
            }
            async Task InitializeAsync()
            {
                await pause.WaitAsync();
                if (failure is not null) throw failure;
            }
        }

        internal Task Original(string phase) => phase == "connect" ? OriginalConnect! : OriginalInitialize!;
        internal async Task WaitForOperationAsync(string phase, Pause pause) =>
            await Task.WhenAll(phase == "connect" ? Connecting.Task : Initializing.Task, pause.Reached.Task).WaitAsync(Guard);
        internal async Task JoinAsync(Child child)
        {
            Task join = Keep(child.StopAndJoinAsync());
            Scheduler.RunQueued();
            await join.WaitAsync(Guard);
        }
        internal Task AssertGateAvailableAsync() => Keep(Task.Run(() =>
        {
            lock (ParentGate) Assert.Same(Child, RegisteredChild);
        })).WaitAsync(Guard);
        internal void AssertExternalTimerCall()
        {
            Assert.False(_insideTimerCallback, "通知回调不得调用 Create/Change/DisposeAsync。");
            Assert.False(Monitor.IsEntered(ParentGate), "原 timer 方法必须在父锁外执行。");
            Assert.NotNull(RegisteredChild);
        }
        internal void AssertControlLive()
        {
            Assert.False(Control.Connection.IsCloseRequested);
            Assert.Same(Control.Ssl, Parent.Stream);
            Assert.Equal(0, Control.Client.DisposeCalls);
            Assert.Equal(0, Control.Ssl.DisposeCalls);
            Assert.Empty(Control.Connection.CleanupErrors);
        }
        internal async Task FinishAsync()
        {
            Clock.ClearProbes();
            foreach (Pause pause in _pauses) pause.Open();
            try
            {
                Task join = Parent.CloseAndJoinAsync();
                Task childJoin = RegisteredChild?.StopAndJoinAsync() ?? Task.CompletedTask;
                Scheduler.RunQueued();
                await ObserveAsync(Task.WhenAll(_tasks.Append(join).Append(childJoin)));
                // 同步前缀放行后才能取得晚到的原任务，故在 worker 结束后再取收尾快照。
                await ObserveAsync(Task.WhenAll(_tasks));
            }
            finally
            {
                try
                {
                    await ObserveAsync(Task.WhenAll(Control.Connection.CloseAsync(), Video.Connection.CloseAsync()));
                    if (Timer.OriginalDispose is { } original) await ObserveAsync(original);
                    CancellationTokenRegistration[] registrations;
                    lock (_registrations) registrations = _registrations.ToArray();
                    foreach (CancellationTokenRegistration registration in registrations) await registration.DisposeAsync();
                }
                finally
                {
                    Control.ReleaseResources();
                    Video.ReleaseResources();
                }
            }
        }
    }

    // 未连接 TCP 和派生 SSL 仅提供受控关闭/EOF；从不连接网络或发起 TLS 握手。
    private sealed class Endpoint
    {
        internal ProbeTcpClient Client { get; } = new();
        internal ProbeSslStream Ssl { get; } = new();
        internal TlsConnection Connection { get; }
        internal Task RequestedClose
        {
            get
            {
                Assert.True(Connection.IsCloseRequested, "不能由测试调用 CloseAsync 补齐生产遗漏的关闭。");
                return Connection.CloseAsync();
            }
        }
        internal Endpoint()
        {
            byte[] pin = Convert.FromHexString("808182838485868788898A8B8C8D8E8F909192939495969798999A9B9C9D9E9F");
            Assert.True(ConnectionTarget.TryCreate(new Guid("11111111-2222-3333-4444-555555555555"),
                IPAddress.Loopback, 12345, Convert.ToHexString(pin), out ConnectionTarget? target));
            Assert.True(ConnectionIdentity.TryCreate(target!, pin, out ConnectionIdentity? identity));
            Connection = new TlsConnection(identity!, Client, Ssl);
        }
        internal async Task WaitForRequestedCloseAsync()
        {
            await Ssl.CloseEntered.Task.WaitAsync(Guard);
            await RequestedClose.WaitAsync(Guard);
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
            if (disposing)
            {
                Assert.False(_insideTimerCallback, "timer callback 不得关闭 TCP。");
                Interlocked.Increment(ref _disposeCalls);
            }
            base.Dispose(disposing);
        }
        internal void ReleaseResources() => base.Dispose(true);
    }

    private sealed class ProbeSslStream() : SslStream(new MemoryStream(), leaveInnerStreamOpen: false)
    {
        private int _disposeCalls;
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        internal Pause? ClosePause { get; set; }
        internal Exception? DisposeFailure { get; set; }
        internal TaskCompletionSource CloseEntered { get; } = Signal();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(0);
        protected override void Dispose(bool disposing)
        {
            try
            {
                if (!disposing) return;
                Assert.False(_insideTimerCallback, "timer callback 不得关闭 SSL。");
                Interlocked.Increment(ref _disposeCalls);
                CloseEntered.TrySetResult();
                ClosePause?.WaitSynchronously();
                if (DisposeFailure is { } error) throw error;
            }
            finally { base.Dispose(disposing); }
        }
        internal void ReleaseResources() => base.Dispose(true);
    }
}
