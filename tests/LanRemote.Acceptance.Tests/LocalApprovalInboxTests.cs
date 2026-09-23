using System.Net;
using System.Runtime.CompilerServices;
using LanRemote.Core.Models;
using LanRemote.Transport;

namespace LanRemote.Acceptance.Tests;

public sealed partial class LocalApprovalInboxTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [ThreadStatic]
    private static bool _insideCompletion;

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Capacity_Must_Be_Positive(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalApprovalInbox(capacity));
    }

    [Theory(Timeout = 30_000)]
    [InlineData(SessionPermission.Control, LocalApprovalOutcome.Approved, SessionPermission.Control)]
    [InlineData(SessionPermission.Control, LocalApprovalOutcome.Approved, SessionPermission.ViewOnly)]
    [InlineData(SessionPermission.ViewOnly, LocalApprovalOutcome.Approved, SessionPermission.ViewOnly)]
    [InlineData(SessionPermission.Control, LocalApprovalOutcome.Denied, null)]
    public async Task Explicit_Decision_Completes_Exactly_Once(
        SessionPermission requested, LocalApprovalOutcome outcome, SessionPermission? granted)
    {
        using LocalApprovalInbox inbox = new();
        LocalApprovalRequest request = CreateRequest(permission: requested);
        Task<LocalApprovalDecision> pending = inbox.RequestApprovalAsync(request, CancellationToken.None).AsTask();
        LocalApprovalSnapshot snapshot = Assert.Single(inbox.GetSnapshot());

        Assert.False(pending.IsCompleted);
        Assert.Equal(request, snapshot.Request);
        Assert.NotEqual(Guid.Empty, snapshot.Generation);
        Assert.True(inbox.TrySubmit(request.RequestId, snapshot.Generation, outcome, granted));
        LocalApprovalDecision result = await pending.WaitAsync(TestTimeout);
        Assert.Equal(new LocalApprovalDecision(request.RequestId, outcome, granted), result);
        Assert.Empty(inbox.GetSnapshot());
        Assert.Equal(0, inbox.PendingCount);
        Assert.False(inbox.TrySubmit(request.RequestId, snapshot.Generation, outcome, granted));
        Assert.False(inbox.Cancel(request.RequestId, snapshot.Generation));
        inbox.Stop();
        Assert.Same(result, await pending.WaitAsync(TestTimeout));
    }

    [Theory(Timeout = 30_000)]
    [InlineData(SessionPermission.ViewOnly, LocalApprovalOutcome.Approved, SessionPermission.Control)]
    [InlineData(SessionPermission.Control, LocalApprovalOutcome.Approved, null)]
    [InlineData(SessionPermission.Control, LocalApprovalOutcome.Approved, (SessionPermission)99)]
    [InlineData((SessionPermission)99, LocalApprovalOutcome.Approved, SessionPermission.ViewOnly)]
    [InlineData(SessionPermission.Control, LocalApprovalOutcome.Denied, SessionPermission.ViewOnly)]
    [InlineData(SessionPermission.Control, LocalApprovalOutcome.Denied, (SessionPermission)99)]
    [InlineData(SessionPermission.Control, LocalApprovalOutcome.Cancelled, null)]
    [InlineData(SessionPermission.Control, LocalApprovalOutcome.TimedOut, null)]
    [InlineData(SessionPermission.Control, LocalApprovalOutcome.Unavailable, null)]
    [InlineData(SessionPermission.Control, (LocalApprovalOutcome)99, null)]
    public async Task Illegal_Decisions_Do_Not_Consume_The_Pending_Request(
        SessionPermission requested, LocalApprovalOutcome outcome, SessionPermission? granted)
    {
        using LocalApprovalInbox inbox = new();
        Task<LocalApprovalDecision> pending = inbox.RequestApprovalAsync(
            CreateRequest(permission: requested), CancellationToken.None).AsTask();
        LocalApprovalSnapshot snapshot = Assert.Single(inbox.GetSnapshot());

        Assert.False(inbox.TrySubmit(snapshot.RequestId, snapshot.Generation, outcome, granted));
        Assert.False(pending.IsCompleted);
        Assert.Equal(snapshot, Assert.Single(inbox.GetSnapshot()));
        Assert.True(inbox.TrySubmit(snapshot.RequestId, snapshot.Generation, LocalApprovalOutcome.Denied));
        Assert.Equal(LocalApprovalOutcome.Denied, (await pending.WaitAsync(TestTimeout)).Outcome);
    }

    [Fact(Timeout = 30_000)]
    public async Task Unknown_Id_Or_Generation_Cannot_Submit_Or_Cancel()
    {
        using LocalApprovalInbox inbox = new();
        Task<LocalApprovalDecision> pending = inbox.RequestApprovalAsync(CreateRequest(), CancellationToken.None).AsTask();
        LocalApprovalSnapshot snapshot = Assert.Single(inbox.GetSnapshot());

        Assert.False(inbox.TrySubmit(Guid.NewGuid(), snapshot.Generation,
            LocalApprovalOutcome.Approved, SessionPermission.Control));
        Assert.False(inbox.TrySubmit(snapshot.RequestId, Guid.NewGuid(),
            LocalApprovalOutcome.Approved, SessionPermission.Control));
        Assert.False(inbox.Cancel(snapshot.RequestId, Guid.NewGuid()));
        Assert.False(inbox.Cancel(Guid.NewGuid(), snapshot.Generation));
        Assert.False(pending.IsCompleted);
        Assert.True(inbox.Cancel(snapshot.RequestId, snapshot.Generation));
        Assert.False(inbox.Cancel(snapshot.RequestId, snapshot.Generation));
        Assert.Equal(LocalApprovalOutcome.Cancelled, (await pending.WaitAsync(TestTimeout)).Outcome);
    }

    [Fact(Timeout = 30_000)]
    public async Task Default_Capacity_Is_Three_Overflow_Is_Not_Queued()
    {
        using LocalApprovalInbox inbox = new();
        Task<LocalApprovalDecision>[] pending = Enumerable.Range(0, 3)
            .Select(_ => inbox.RequestApprovalAsync(CreateRequest(), CancellationToken.None).AsTask()).ToArray();

        Assert.Equal(3, inbox.Capacity);
        for (int i = 0; i < 64; i++)
        {
            LocalApprovalRequest request = CreateRequest();
            ValueTask<LocalApprovalDecision> overflow = inbox.RequestApprovalAsync(request, CancellationToken.None);
            Assert.True(overflow.IsCompletedSuccessfully);
            Assert.Equal(new LocalApprovalDecision(request.RequestId, LocalApprovalOutcome.Unavailable), await overflow);
            Assert.Equal(3, inbox.PendingCount);
            Assert.Equal(3, inbox.GetSnapshot().Count);
        }
        Assert.All(pending, task => Assert.False(task.IsCompleted));

        LocalApprovalSnapshot first = inbox.GetSnapshot()[0];
        Assert.True(inbox.Cancel(first.RequestId, first.Generation));
        Task<LocalApprovalDecision> replacement = inbox.RequestApprovalAsync(CreateRequest(), CancellationToken.None).AsTask();
        Assert.False(replacement.IsCompleted);
        Assert.Equal(3, inbox.PendingCount);
        inbox.Stop();
        Assert.All(await Task.WhenAll(pending.Append(replacement)).WaitAsync(TestTimeout),
            result => Assert.Equal(LocalApprovalOutcome.Cancelled, result.Outcome));
    }

    [Theory(Timeout = 30_000)]
    [InlineData(1)]
    [InlineData(5)]
    public async Task Constructor_Capacity_Is_Enforced(int capacity)
    {
        using LocalApprovalInbox inbox = new(capacity);
        Task<LocalApprovalDecision>[] pending = Enumerable.Range(0, capacity)
            .Select(_ => inbox.RequestApprovalAsync(CreateRequest(), CancellationToken.None).AsTask()).ToArray();
        Assert.Equal(capacity, inbox.PendingCount);
        Assert.Equal(capacity, inbox.GetSnapshot().Count);
        Assert.Equal(LocalApprovalOutcome.Unavailable,
            (await inbox.RequestApprovalAsync(CreateRequest(), CancellationToken.None)).Outcome);
        inbox.Stop();
        Assert.All(await Task.WhenAll(pending).WaitAsync(TestTimeout),
            result => Assert.Equal(LocalApprovalOutcome.Cancelled, result.Outcome));
    }

    [Fact(Timeout = 30_000)]
    public async Task Duplicate_Pending_Id_Does_Not_Replace_Or_Cancel_Original()
    {
        using LocalApprovalInbox inbox = new();
        using CancellationTokenSource duplicateCancellation = new();
        LocalApprovalRequest request = CreateRequest();
        Task<LocalApprovalDecision> original = inbox.RequestApprovalAsync(request, CancellationToken.None).AsTask();
        LocalApprovalSnapshot before = Assert.Single(inbox.GetSnapshot());
        LocalApprovalDecision duplicate = await inbox.RequestApprovalAsync(
            request with { ClientName = "重复请求", RequestedPermission = SessionPermission.ViewOnly },
            duplicateCancellation.Token);
        duplicateCancellation.Cancel();

        Assert.Equal(LocalApprovalOutcome.Unavailable, duplicate.Outcome);
        Assert.Equal(before, Assert.Single(inbox.GetSnapshot()));
        Assert.False(original.IsCompleted);
        Assert.True(inbox.TrySubmit(before.RequestId, before.Generation,
            LocalApprovalOutcome.Approved, SessionPermission.Control));
        Assert.Equal(LocalApprovalOutcome.Approved, (await original.WaitAsync(TestTimeout)).Outcome);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(LocalApprovalOutcome.Approved)]
    [InlineData(LocalApprovalOutcome.Denied)]
    [InlineData(LocalApprovalOutcome.Cancelled)]
    public async Task Reused_Id_Gets_New_Generation_And_Rejects_Old_Click(LocalApprovalOutcome firstOutcome)
    {
        using LocalApprovalInbox inbox = new();
        using CancellationTokenSource oldCancellation = new();
        LocalApprovalRequest request = CreateRequest();
        Task<LocalApprovalDecision> first = inbox.RequestApprovalAsync(request, oldCancellation.Token).AsTask();
        LocalApprovalSnapshot old = Assert.Single(inbox.GetSnapshot());
        if (firstOutcome == LocalApprovalOutcome.Cancelled)
        {
            oldCancellation.Cancel();
        }
        else
        {
            Assert.True(inbox.TrySubmit(old.RequestId, old.Generation, firstOutcome,
                firstOutcome == LocalApprovalOutcome.Approved ? SessionPermission.Control : null));
        }
        Assert.Equal(firstOutcome, (await first.WaitAsync(TestTimeout)).Outcome);

        Task<LocalApprovalDecision> second = inbox.RequestApprovalAsync(request, CancellationToken.None).AsTask();
        LocalApprovalSnapshot current = Assert.Single(inbox.GetSnapshot());
        Assert.NotEqual(old.Generation, current.Generation);
        Assert.False(inbox.TrySubmit(old.RequestId, old.Generation,
            LocalApprovalOutcome.Approved, SessionPermission.Control));
        Assert.False(inbox.Cancel(old.RequestId, old.Generation));
        oldCancellation.Cancel();
        Assert.False(second.IsCompleted);
        Assert.True(inbox.TrySubmit(current.RequestId, current.Generation, LocalApprovalOutcome.Denied));
        Assert.Equal(LocalApprovalOutcome.Denied, (await second.WaitAsync(TestTimeout)).Outcome);
    }

    [Fact(Timeout = 30_000)]
    public async Task Generations_Are_Per_Entry_And_Also_Differ_Across_Runs()
    {
        using LocalApprovalInbox oldRun = new();
        using LocalApprovalInbox newRun = new();
        LocalApprovalRequest request = CreateRequest();
        Task<LocalApprovalDecision> first = oldRun.RequestApprovalAsync(request, CancellationToken.None).AsTask();
        LocalApprovalSnapshot old = Assert.Single(oldRun.GetSnapshot());
        Task<LocalApprovalDecision> other = oldRun.RequestApprovalAsync(CreateRequest(), CancellationToken.None).AsTask();
        Assert.Equal(2, oldRun.GetSnapshot().Select(snapshot => snapshot.Generation).Distinct().Count());
        Assert.Contains(oldRun.GetSnapshot(), snapshot => snapshot == old);
        oldRun.Stop();
        await Task.WhenAll(first, other).WaitAsync(TestTimeout);

        Task<LocalApprovalDecision> current = newRun.RequestApprovalAsync(request, CancellationToken.None).AsTask();
        LocalApprovalSnapshot fresh = Assert.Single(newRun.GetSnapshot());
        Assert.NotEqual(old.Generation, fresh.Generation);
        Assert.False(newRun.TrySubmit(old.RequestId, old.Generation,
            LocalApprovalOutcome.Approved, SessionPermission.Control));
        Assert.False(current.IsCompleted);
        newRun.Stop();
        Assert.Equal(LocalApprovalOutcome.Cancelled, (await current.WaitAsync(TestTimeout)).Outcome);
    }

    [Fact(Timeout = 30_000)]
    public async Task Snapshots_Are_Detached_Including_Mutable_Ip_Address()
    {
        using LocalApprovalInbox inbox = new();
        LocalApprovalRequest request = CreateRequest() with { RemoteAddress = IPAddress.Parse("fe80::1%7") };
        Task<LocalApprovalDecision> pending = inbox.RequestApprovalAsync(request, CancellationToken.None).AsTask();
        request.RemoteAddress.ScopeId = 8;
        IReadOnlyList<LocalApprovalSnapshot> oldList = inbox.GetSnapshot();
        LocalApprovalSnapshot old = Assert.Single(oldList);
        Assert.Equal(7L, old.Request.RemoteAddress.ScopeId);
        old.Request.RemoteAddress.ScopeId = 9;
        LocalApprovalSnapshot fresh = Assert.Single(inbox.GetSnapshot());
        Assert.NotSame(old.Request, fresh.Request);
        Assert.Equal(7L, fresh.Request.RemoteAddress.ScopeId);
        Assert.Equal(old.Generation, fresh.Generation);

        inbox.Stop();
        Assert.Single(oldList);
        Assert.Empty(inbox.GetSnapshot());
        Assert.False(inbox.TrySubmit(old.RequestId, old.Generation,
            LocalApprovalOutcome.Approved, SessionPermission.Control));
        Assert.Equal(LocalApprovalOutcome.Cancelled, (await pending.WaitAsync(TestTimeout)).Outcome);
    }

    [Fact(Timeout = 30_000)]
    public async Task Request_Returns_Quickly_Without_UI_And_Does_Not_Auto_Approve()
    {
        using LocalApprovalInbox inbox = new();
        Task<Task<LocalApprovalDecision>> invoking = Task.Factory.StartNew(
            () => inbox.RequestApprovalAsync(CreateRequest(), CancellationToken.None).AsTask(),
            CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        Task<LocalApprovalDecision> pending = await invoking.WaitAsync(TestTimeout);
        Assert.False(pending.IsCompleted);
        Assert.Single(inbox.GetSnapshot());
        inbox.Stop();
        Assert.Equal(LocalApprovalOutcome.Cancelled, (await pending.WaitAsync(TestTimeout)).Outcome);
        Assert.Equal(LocalApprovalOutcome.Unavailable,
            (await inbox.RequestApprovalAsync(CreateRequest(), CancellationToken.None)).Outcome);
    }

    [Fact(Timeout = 30_000)]
    public async Task Already_Cancelled_Request_Does_Not_Consume_Capacity()
    {
        using LocalApprovalInbox inbox = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        LocalApprovalRequest request = CreateRequest();
        ValueTask<LocalApprovalDecision> result = inbox.RequestApprovalAsync(request, cancellation.Token);
        Assert.True(result.IsCompletedSuccessfully);
        Assert.Equal(new LocalApprovalDecision(request.RequestId, LocalApprovalOutcome.Cancelled), await result);
        Assert.Equal(0, inbox.PendingCount);
        Assert.Empty(inbox.GetSnapshot());
    }

    [Fact(Timeout = 30_000)]
    public async Task Token_Cancellation_Clears_Pending_And_Invalidates_Snapshot()
    {
        using LocalApprovalInbox inbox = new();
        using CancellationTokenSource cancellation = new();
        Task<LocalApprovalDecision> pending = inbox.RequestApprovalAsync(CreateRequest(), cancellation.Token).AsTask();
        LocalApprovalSnapshot old = Assert.Single(inbox.GetSnapshot());
        cancellation.Cancel();
        LocalApprovalDecision result = await pending.WaitAsync(TestTimeout);
        Assert.Equal(new LocalApprovalDecision(old.RequestId, LocalApprovalOutcome.Cancelled), result);
        Assert.Equal(0, inbox.PendingCount);
        Assert.Empty(inbox.GetSnapshot());
        cancellation.Cancel();
        Assert.False(inbox.TrySubmit(old.RequestId, old.Generation,
            LocalApprovalOutcome.Approved, SessionPermission.Control));
        Assert.Same(result, await pending.WaitAsync(TestTimeout));
    }

    [Fact(Timeout = 30_000)]
    public async Task Cancellation_After_Completed_Handoff_Does_Not_Rewrite_The_Result()
    {
        using LocalApprovalInbox inbox = new();
        using CancellationTokenSource cancellation = new();
        Task<LocalApprovalDecision> pending = inbox.RequestApprovalAsync(CreateRequest(), cancellation.Token).AsTask();
        LocalApprovalSnapshot snapshot = Assert.Single(inbox.GetSnapshot());
        Assert.True(inbox.TrySubmit(snapshot.RequestId, snapshot.Generation,
            LocalApprovalOutcome.Approved, SessionPermission.Control));
        LocalApprovalDecision result = await pending.WaitAsync(TestTimeout);
        cancellation.Cancel();
        inbox.Stop();
        Assert.Equal(LocalApprovalOutcome.Approved, result.Outcome);
        Assert.Same(result, await pending.WaitAsync(TestTimeout));
        // 收件箱已交接的决定不可倒写；Transport 仍须在实际接受时检查取消和 deadline。
    }

    [Theory(Timeout = 60_000)]
    [InlineData(LocalApprovalOutcome.Approved)]
    [InlineData(LocalApprovalOutcome.Denied)]
    public async Task Signalled_Cancellation_Wins_Even_When_Its_Inbox_Callback_Has_Not_Run(
        LocalApprovalOutcome outcome)
    {
        using LocalApprovalInbox inbox = new();
        using CancellationTokenSource cancellation = new();
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<LocalApprovalDecision> pending = inbox.RequestApprovalAsync(CreateRequest(), cancellation.Token).AsTask();
        LocalApprovalSnapshot snapshot = Assert.Single(inbox.GetSnapshot());

        // CTS 后注册的回调先执行：令牌已置位，但收件箱回调确定尚未开始。
        using CancellationTokenRegistration blocker = cancellation.Token.UnsafeRegister(_ =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("测试取消回调未被释放。");
            }
        }, null);
        Task cancelling = Task.Factory.StartNew(cancellation.Cancel,
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await entered.Task.WaitAsync(TestTimeout);
            Assert.True(cancellation.IsCancellationRequested);
            Assert.False(pending.IsCompleted);
            bool accepted = await Task.Run(() => inbox.TrySubmit(snapshot.RequestId, snapshot.Generation,
                outcome, outcome == LocalApprovalOutcome.Approved ? SessionPermission.Control : null))
                .WaitAsync(TestTimeout);
            Assert.False(accepted);
            Assert.Equal(LocalApprovalOutcome.Cancelled, (await pending.WaitAsync(TestTimeout)).Outcome);
            Assert.Empty(inbox.GetSnapshot());
            Assert.False(cancelling.IsCompleted);
        }
        finally
        {
            release.Set();
            await cancelling.WaitAsync(TestTimeout);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Concurrent_Submit_And_Cancel_Have_One_Consistent_Terminal_Result()
    {
        using LocalApprovalInbox inbox = new();
        for (int i = 0; i < 100; i++)
        {
            using CancellationTokenSource cancellation = new();
            TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<LocalApprovalDecision> pending = inbox.RequestApprovalAsync(CreateRequest(), cancellation.Token).AsTask();
            LocalApprovalSnapshot snapshot = Assert.Single(inbox.GetSnapshot());
            Task<bool> submitting = Task.Run(async () =>
            {
                await start.Task;
                return inbox.TrySubmit(snapshot.RequestId, snapshot.Generation,
                    LocalApprovalOutcome.Approved, SessionPermission.Control);
            });
            Task cancelling = Task.Run(async () =>
            {
                await start.Task;
                cancellation.Cancel();
            });
            start.SetResult();
            await Task.WhenAll(submitting, cancelling).WaitAsync(TestTimeout);
            LocalApprovalDecision result = await pending.WaitAsync(TestTimeout);
            Assert.Equal(await submitting ? LocalApprovalOutcome.Approved : LocalApprovalOutcome.Cancelled,
                result.Outcome);
            Assert.Equal(result.Outcome == LocalApprovalOutcome.Approved ? SessionPermission.Control :
                (SessionPermission?)null, result.GrantedPermission);
            Assert.Empty(inbox.GetSnapshot());
            Assert.False(inbox.TrySubmit(snapshot.RequestId, snapshot.Generation, LocalApprovalOutcome.Denied));
            Assert.Same(result, await pending.WaitAsync(TestTimeout));
        }
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_And_Dispose_Are_Idempotent_And_Permanently_Unavailable(bool disposeFirst)
    {
        using LocalApprovalInbox inbox = new();
        using CancellationTokenSource cancellation = new();
        Task<LocalApprovalDecision>[] pending = Enumerable.Range(0, 3)
            .Select(_ => inbox.RequestApprovalAsync(CreateRequest(), cancellation.Token).AsTask()).ToArray();
        IReadOnlyList<LocalApprovalSnapshot> old = inbox.GetSnapshot();
        if (disposeFirst)
        {
            inbox.Dispose();
        }
        else
        {
            inbox.Stop();
        }
        inbox.Stop();
        inbox.Dispose();
        cancellation.Cancel();
        Assert.All(await Task.WhenAll(pending).WaitAsync(TestTimeout),
            result => Assert.Equal(LocalApprovalOutcome.Cancelled, result.Outcome));
        Assert.Equal(0, inbox.PendingCount);
        Assert.Empty(inbox.GetSnapshot());
        Assert.All(old, snapshot => Assert.False(inbox.TrySubmit(snapshot.RequestId, snapshot.Generation,
            LocalApprovalOutcome.Approved, SessionPermission.Control)));
        Assert.Equal(LocalApprovalOutcome.Unavailable,
            (await inbox.RequestApprovalAsync(CreateRequest(), CancellationToken.None)).Outcome);
        Assert.Equal(LocalApprovalOutcome.Unavailable,
            (await inbox.RequestApprovalAsync(CreateRequest(), cancellation.Token)).Outcome);
    }

    [Fact(Timeout = 60_000)]
    public async Task Competing_Decisions_Stop_And_Dispose_Complete_Each_Request_Only_Once()
    {
        for (int i = 0; i < 100; i++)
        {
            using LocalApprovalInbox inbox = new();
            TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<LocalApprovalDecision> pending = inbox.RequestApprovalAsync(CreateRequest(), CancellationToken.None).AsTask();
            LocalApprovalSnapshot snapshot = Assert.Single(inbox.GetSnapshot());
            LocalApprovalOutcome[] outcomes = [LocalApprovalOutcome.Approved, LocalApprovalOutcome.Denied];
            Task<bool>[] submissions = outcomes.Select(outcome => Task.Run(async () =>
            {
                await start.Task;
                return inbox.TrySubmit(snapshot.RequestId, snapshot.Generation, outcome,
                    outcome == LocalApprovalOutcome.Approved ? SessionPermission.Control : null);
            })).ToArray();
            Task stopping = Task.Run(async () =>
            {
                await start.Task;
                inbox.Stop();
            });
            Task disposing = Task.Run(async () =>
            {
                await start.Task;
                inbox.Dispose();
            });
            start.SetResult();
            await Task.WhenAll(submissions.Cast<Task>().Append(stopping).Append(disposing)).WaitAsync(TestTimeout);
            bool[] accepted = await Task.WhenAll(submissions);
            Assert.InRange(accepted.Count(value => value), 0, 1);
            LocalApprovalOutcome expected = accepted[0] ? LocalApprovalOutcome.Approved
                : accepted[1] ? LocalApprovalOutcome.Denied : LocalApprovalOutcome.Cancelled;
            LocalApprovalDecision result = await pending.WaitAsync(TestTimeout);
            Assert.Equal(expected, result.Outcome);
            Assert.Same(result, await pending.WaitAsync(TestTimeout));
            Assert.Equal(0, inbox.PendingCount);
            Assert.Empty(inbox.GetSnapshot());
            Assert.False(inbox.TrySubmit(snapshot.RequestId, snapshot.Generation, LocalApprovalOutcome.Denied));
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Registration_Racing_Stop_And_Cancel_Does_Not_Leave_Pending_Tasks()
    {
        for (int i = 0; i < 100; i++)
        {
            using LocalApprovalInbox inbox = new();
            using CancellationTokenSource cancellation = new();
            TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<LocalApprovalDecision> request = Task.Run(async () =>
            {
                await start.Task;
                return await inbox.RequestApprovalAsync(CreateRequest(), cancellation.Token);
            });
            Task stopping = Task.Run(async () =>
            {
                await start.Task;
                inbox.Stop();
                inbox.Dispose();
            });
            Task cancelling = Task.Run(async () =>
            {
                await start.Task;
                cancellation.Cancel();
            });
            start.SetResult();
            await Task.WhenAll(request, stopping, cancelling).WaitAsync(TestTimeout);
            Assert.Contains((await request).Outcome,
                new[] { LocalApprovalOutcome.Cancelled, LocalApprovalOutcome.Unavailable });
            Assert.Equal(0, inbox.PendingCount);
            Assert.Empty(inbox.GetSnapshot());
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Concurrent_Requests_Never_Exceed_The_Pending_Bound()
    {
        using LocalApprovalInbox inbox = new();
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Task<LocalApprovalDecision>>[] producers = Enumerable.Range(0, 64).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            Task<LocalApprovalDecision> decision = inbox.RequestApprovalAsync(CreateRequest(), CancellationToken.None).AsTask();
            Assert.InRange(inbox.PendingCount, 0, 3);
            Assert.InRange(inbox.GetSnapshot().Count, 0, 3);
            return decision;
        })).ToArray();
        start.SetResult();
        Task<LocalApprovalDecision>[] requests = await Task.WhenAll(producers).WaitAsync(TestTimeout);
        Assert.Equal(3, requests.Count(task => !task.IsCompleted));
        Assert.Equal(3, inbox.PendingCount);
        foreach (Task<LocalApprovalDecision> rejected in requests.Where(task => task.IsCompleted))
        {
            Assert.Equal(LocalApprovalOutcome.Unavailable, (await rejected).Outcome);
        }
        inbox.Stop();
        LocalApprovalDecision[] results = await Task.WhenAll(requests).WaitAsync(TestTimeout);
        Assert.Equal(3, results.Count(result => result.Outcome == LocalApprovalOutcome.Cancelled));
        Assert.Equal(61, results.Count(result => result.Outcome == LocalApprovalOutcome.Unavailable));
    }

    [Theory(Timeout = 30_000)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Completion_Does_Not_Run_Continuations_Inline(int finish)
    {
        using LocalApprovalInbox inbox = new();
        using CancellationTokenSource cancellation = new();
        Task<LocalApprovalDecision> pending = inbox.RequestApprovalAsync(CreateRequest(), cancellation.Token).AsTask();
        LocalApprovalSnapshot snapshot = Assert.Single(inbox.GetSnapshot());
        Assert.True((pending.CreationOptions & TaskCreationOptions.RunContinuationsAsynchronously) != 0);
        Task<bool> continuation = pending.ContinueWith(_ => _insideCompletion,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        await Task.Run(() =>
        {
            _insideCompletion = true;
            try
            {
                if (finish == 0)
                {
                    Assert.True(inbox.TrySubmit(snapshot.RequestId, snapshot.Generation, LocalApprovalOutcome.Denied));
                }
                else if (finish == 1)
                {
                    cancellation.Cancel();
                }
                else
                {
                    inbox.Stop();
                }
            }
            finally
            {
                _insideCompletion = false;
            }
        }).WaitAsync(TestTimeout);
        Assert.False(await continuation.WaitAsync(TestTimeout));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Completed_Entries_Do_Not_Stay_Rooted_By_A_Long_Lived_Cancellation_Source(int finish)
    {
        using CancellationTokenSource cancellation = new();
        WeakReference reference = CompleteAndRelease(cancellation.Token, finish);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(reference.IsAlive);
        GC.KeepAlive(cancellation);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiresAt_Is_Display_Only_Not_A_Local_Authorization_Rule(bool displayInPast)
    {
        using LocalApprovalInbox inbox = new();
        LocalApprovalRequest request = CreateRequest() with
        {
            ExpiresAt = displayInPast ? DateTimeOffset.UnixEpoch : DateTimeOffset.MaxValue,
        };
        Task<LocalApprovalDecision> pending = inbox.RequestApprovalAsync(request, CancellationToken.None).AsTask();
        LocalApprovalSnapshot snapshot = Assert.Single(inbox.GetSnapshot());
        Assert.Equal(request.ExpiresAt, snapshot.Request.ExpiresAt);
        Assert.False(pending.IsCompleted);
        Assert.True(inbox.TrySubmit(snapshot.RequestId, snapshot.Generation,
            LocalApprovalOutcome.Approved, SessionPermission.ViewOnly));
        Assert.Equal(LocalApprovalOutcome.Approved, (await pending.WaitAsync(TestTimeout)).Outcome);
        // 这里只证明收件箱交回决定，不声称过期请求可以通过 Transport 的授权接受检查。
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CompleteAndRelease(CancellationToken token, int finish)
    {
        using LocalApprovalInbox inbox = new();
        Task<LocalApprovalDecision> pending = inbox.RequestApprovalAsync(CreateRequest(), token).AsTask();
        LocalApprovalSnapshot snapshot = Assert.Single(inbox.GetSnapshot());
        switch (finish)
        {
            case 0:
                Assert.True(inbox.TrySubmit(snapshot.RequestId, snapshot.Generation,
                    LocalApprovalOutcome.Approved, SessionPermission.Control));
                break;
            case 1:
                Assert.True(inbox.TrySubmit(snapshot.RequestId, snapshot.Generation, LocalApprovalOutcome.Denied));
                break;
            case 2:
                Assert.True(inbox.Cancel(snapshot.RequestId, snapshot.Generation));
                break;
            default:
                inbox.Dispose();
                break;
        }
        Assert.True(pending.IsCompletedSuccessfully);
        return new WeakReference(inbox);
    }

    private static LocalApprovalRequest CreateRequest(
        Guid? requestId = null, SessionPermission permission = SessionPermission.Control) => new(
        requestId ?? Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        IPAddress.Loopback,
        45678,
        Guid.NewGuid(),
        "自称测试客户端",
        permission,
        "A1B2C3",
        DateTimeOffset.MaxValue);
}
