using System.Windows.Threading;
using LanRemote.Core.Models;
using LanRemote.Transport;

namespace LanRemote.Acceptance.Tests;

public sealed partial class LocalApprovalInboxTests
{
    [Fact(Timeout = 60_000)]
    public async Task Background_Request_And_Cancel_Finish_While_Real_Sta_Dispatcher_Is_Blocked()
    {
        await using StaDispatcherFixture sta = new();
        using LocalApprovalInbox inbox = new();
        using CancellationTokenSource cancellation = new();
        using ManualResetEventSlim release = new();
        Dispatcher dispatcher = await sta.Ready.WaitAsync(TestTimeout);

        // 首条请求故意在 STA 提交，验证注册没有捕获 Dispatcher 同步上下文。
        Task<LocalApprovalDecision> first = await dispatcher.InvokeAsync(() =>
        {
            Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
            Assert.IsType<DispatcherSynchronizationContext>(SynchronizationContext.Current);
            return inbox.RequestApprovalAsync(CreateRequest(), cancellation.Token).AsTask();
        }).Task.WaitAsync(TestTimeout);
        LocalApprovalSnapshot old = await dispatcher.InvokeAsync(() => Assert.Single(inbox.GetSnapshot()))
            .Task.WaitAsync(TestTimeout);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherOperation blocking = dispatcher.InvokeAsync(() =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("测试 Dispatcher 未被释放。");
            }
        });
        try
        {
            await entered.Task.WaitAsync(TestTimeout);
            // 只等待同步前缀返回，而不把返回的审批 Task 自动展开等待。
            Task<Task<LocalApprovalDecision>> invoking = Task.Factory.StartNew(
                () => inbox.RequestApprovalAsync(CreateRequest(), cancellation.Token).AsTask(),
                CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            Task<LocalApprovalDecision> second = await invoking.WaitAsync(TestTimeout);
            Assert.False(second.IsCompleted);
            Assert.Equal(2, inbox.PendingCount);

            await Task.Run(cancellation.Cancel).WaitAsync(TestTimeout);
            Assert.All(await Task.WhenAll(first, second).WaitAsync(TestTimeout),
                result => Assert.Equal(LocalApprovalOutcome.Cancelled, result.Outcome));
            Assert.False(blocking.Task.IsCompleted);
            Assert.Equal(0, inbox.PendingCount);
            Assert.Empty(inbox.GetSnapshot());

            // 同一 ID 再次受理后，队列里的旧点击也不能批准新一代请求。
            Task<LocalApprovalDecision> replacement = inbox.RequestApprovalAsync(
                old.Request, CancellationToken.None).AsTask();
            DispatcherOperation<bool> staleClick = dispatcher.InvokeAsync(() => inbox.TrySubmit(
                old.RequestId, old.Generation, LocalApprovalOutcome.Approved, SessionPermission.Control));
            Assert.False(staleClick.Task.IsCompleted);
            release.Set();
            await blocking.Task.WaitAsync(TestTimeout);
            Assert.False(await staleClick.Task.WaitAsync(TestTimeout));
            Assert.False(replacement.IsCompleted);
            LocalApprovalSnapshot current = await dispatcher.InvokeAsync(() => Assert.Single(inbox.GetSnapshot()))
                .Task.WaitAsync(TestTimeout);
            Assert.NotEqual(old.Generation, current.Generation);
            Assert.True(await dispatcher.InvokeAsync(() => inbox.TrySubmit(
                current.RequestId, current.Generation, LocalApprovalOutcome.Denied)).Task.WaitAsync(TestTimeout));
            Assert.Equal(LocalApprovalOutcome.Denied, (await replacement.WaitAsync(TestTimeout)).Outcome);
        }
        finally
        {
            release.Set();
            await blocking.Task.WaitAsync(TestTimeout);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Blocked_Dispatcher_Does_Not_Accumulate_Per_Request_Operations_Or_Unbounded_Pending()
    {
        await using StaDispatcherFixture sta = new();
        using LocalApprovalInbox inbox = new();
        using CancellationTokenSource cancellation = new();
        using ManualResetEventSlim release = new();
        Dispatcher dispatcher = await sta.Ready.WaitAsync(TestTimeout);
        int posts = 0;
        DispatcherHookEventHandler onPosted = (_, _) => Interlocked.Increment(ref posts);
        DispatcherHooks hooks = await dispatcher.InvokeAsync(() =>
        {
            DispatcherHooks current = dispatcher.Hooks;
            current.OperationPosted += onPosted;
            return current;
        }).Task.WaitAsync(TestTimeout);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherOperation blocking = dispatcher.InvokeAsync(() =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("测试 Dispatcher 未被释放。");
            }
        });
        try
        {
            await entered.Task.WaitAsync(TestTimeout);
            int baseline = Volatile.Read(ref posts);
            Task<LocalApprovalDecision>[] pending = Enumerable.Range(0, 3)
                .Select(_ => inbox.RequestApprovalAsync(CreateRequest(), cancellation.Token).AsTask()).ToArray();
            for (int i = 0; i < 100; i++)
            {
                ValueTask<LocalApprovalDecision> overflow = inbox.RequestApprovalAsync(CreateRequest(), cancellation.Token);
                Assert.True(overflow.IsCompletedSuccessfully);
                Assert.Equal(LocalApprovalOutcome.Unavailable, (await overflow).Outcome);
                Assert.Equal(3, inbox.PendingCount);
                Assert.Equal(3, inbox.GetSnapshot().Count);
            }
            await Task.Run(cancellation.Cancel).WaitAsync(TestTimeout);
            Assert.All(await Task.WhenAll(pending).WaitAsync(TestTimeout),
                result => Assert.Equal(LocalApprovalOutcome.Cancelled, result.Outcome));

            // 即便连续回收、复用条目，也没有逐请求排入 UI 的通知或清理操作。
            for (int i = 0; i < 100; i++)
            {
                Task<LocalApprovalDecision> request = inbox.RequestApprovalAsync(CreateRequest(), CancellationToken.None).AsTask();
                LocalApprovalSnapshot snapshot = Assert.Single(inbox.GetSnapshot());
                Assert.True(inbox.Cancel(snapshot.RequestId, snapshot.Generation));
                Assert.Equal(LocalApprovalOutcome.Cancelled, (await request.WaitAsync(TestTimeout)).Outcome);
            }
            Task<LocalApprovalDecision> last = inbox.RequestApprovalAsync(CreateRequest(), CancellationToken.None).AsTask();
            await Task.Run(inbox.Dispose).WaitAsync(TestTimeout);
            Assert.Equal(LocalApprovalOutcome.Cancelled, (await last.WaitAsync(TestTimeout)).Outcome);
            Assert.Equal(0, inbox.PendingCount);
            Assert.Equal(baseline, Volatile.Read(ref posts));
            Assert.False(blocking.Task.IsCompleted);
        }
        finally
        {
            release.Set();
            await blocking.Task.WaitAsync(TestTimeout);
            await dispatcher.InvokeAsync(() => hooks.OperationPosted -= onPosted).Task.WaitAsync(TestTimeout);
        }
    }
}
