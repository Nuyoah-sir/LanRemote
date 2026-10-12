using System.Net;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Threading.Tasks.Sources;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

public sealed partial class ControlAuthSessionTests
{
    private const byte ActivitySentinel = 0xC7;

    // 受控 IO，不是真实 TLS。原 Memory 视图由 ReadAsync 参数取得，不依赖新 owner 类型存在。
    [Theory(Timeout = 120_000)]
    [InlineData(0, 199, ControlAuthSession.RejectApprovalDisconnected)]
    [InlineData(1, 199, ControlAuthSession.RejectUnexpectedData)]
    [InlineData(1, 0, ControlAuthSession.RejectUnexpectedData)]
    public async Task Host_Read_Ownership_Approval_Activity_Success_Clears_Original_Buffer_Only_After_Join(
        int bytes, int value, string expected)
    {
        using CleanupIoSslStream stream = new() { SingleReadSentinel = ActivitySentinel };
        TaskCompletionSource<LocalApprovalDecision> late = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubApprovalGate gate = new((_, _) => new ValueTask<LocalApprovalDecision>(late.Task));
        ControlAuthSession session = NewReadOwnershipSession(stream, gate);
        Task<ControlAuthResult> run = session.RunAsync(CancellationToken.None);
        Task<int>? activity = null;
        Task? join = null;
        try
        {
            await gate.FirstRequest.Task.WaitAsync(FrameDeadline);
            activity = ReadPendingApprovalTask(session);
            object? owner = ReadPendingApprovalOwner(session);
            ReadOnlyMemory<byte> original = stream.SingleReadBuffer;
            Assert.NotSame(stream.PendingRead, activity);
            Assert.False(activity.IsCompleted);
            Assert.Equal(ActivitySentinel, original.Span[0]);
            Assert.Equal(1, original.Length);

            stream.CompleteSingleRead(bytes, (byte)value);
            ControlAuthResult result = await run.WaitAsync(FrameDeadline);
            Assert.False(result.Completed);
            Assert.Equal(expected, result.Rejection);
            Assert.True(activity.IsCompletedSuccessfully);
            Assert.Equal(bytes, await activity);
            Assert.Equal((byte)value, original.Span[0]);
            Assert.False(late.Task.IsCompleted);
            Assert.Equal(0, session.Context.SessionRegistry.ActiveSessionCount);
            Assert.Equal(0, session.Context.PendingApprovalLimiter.GlobalInUse);
            Assert.DoesNotContain("auth_success", stream.AcceptedFrames);

            join = session.JoinPendingReadAsync();
            await join.WaitAsync(FrameDeadline);
            Assert.True(join.IsCompletedSuccessfully);
            Assert.Equal((byte)0, original.Span[0]);
            Assert.Same(owner, ReadPendingApprovalOwner(session));
            Assert.Same(activity, ReadPendingApprovalTask(session));
            Assert.Equal(1, stream.PendingReadCalls);

            // 成功清理已经释放所有权；再次 join 不能重新清写原视图。
            stream.SingleReadBuffer.Span[0] = 0x52;
            await session.JoinPendingReadAsync().WaitAsync(FrameDeadline);
            Assert.Equal((byte)0x52, original.Span[0]);
        }
        finally
        {
            CompleteReadTestDecision(late, gate);
            await FinishReadOwnershipTestAsync(session, stream, run,
                extraTasks: new Task?[] { activity, join, late.Task });
        }
    }

    [Theory(Timeout = 120_000)]
    [InlineData(false, 0, 199)]
    [InlineData(false, 1, 199)]
    [InlineData(false, 1, 0)]
    [InlineData(true, 0, 199)]
    [InlineData(true, 1, 199)]
    [InlineData(true, 1, 0)]
    public async Task Host_Read_Ownership_Holding_Success_Clears_Original_Buffer_Before_Run_Returns(
        bool requireApproval, int bytes, int value)
    {
        using CancellationTokenSource caller = new();
        using CleanupIoSslStream stream = new() { SingleReadSentinel = ActivitySentinel };
        StubApprovalGate gate = StubApprovalGate.Approve();
        ControlAuthSession session = NewReadOwnershipSession(stream, gate, requireApproval);
        Task<ControlAuthResult> run = session.RunAsync(caller.Token);
        Task<int>? activity = null;
        try
        {
            await stream.SingleReadStarted.Task.WaitAsync(FrameDeadline);
            ReadOnlyMemory<byte> original = stream.SingleReadBuffer;
            Assert.Equal(ActivitySentinel, original.Span[0]);
            Assert.Equal(caller.Token, stream.SingleReadToken);
            Assert.Equal(ControlSessionState.Authenticated, session.State);
            Assert.Equal(1, session.Context.SessionRegistry.ActiveSessionCount);
            Assert.False(run.IsCompleted);
            Assert.False(stream.PendingRead.IsCompleted);
            if (requireApproval)
            {
                activity = ReadPendingApprovalTask(session);
                Assert.NotSame(stream.PendingRead, activity);
                Assert.False(activity.IsCompleted);
            }
            else
            {
                Assert.Null(ReadPendingApprovalOwner(session));
                Assert.True(session.JoinPendingReadAsync().IsCompletedSuccessfully);
            }

            stream.CompleteSingleRead(bytes, (byte)value);
            ControlAuthResult result = await run.WaitAsync(FrameDeadline);
            Assert.True(result.Completed);
            Assert.Null(result.Rejection);
            Assert.Equal(0, session.Context.SessionRegistry.ActiveSessionCount);
            Assert.Equal(bytes, await stream.PendingRead);
            // 特别是 EOF：源预写过 sentinel 后返回 0，不能因没读到载荷而跳过清零。
            Assert.Equal((byte)0, original.Span[0]);
            Assert.Equal(requireApproval ? 1 : 0, gate.RequestCount);
            Assert.Equal(1, stream.PendingReadCalls);
            if (requireApproval)
            {
                Assert.Same(activity, ReadPendingApprovalTask(session));
            }
            else
            {
                Assert.Null(ReadPendingApprovalOwner(session));
            }
        }
        finally
        {
            await FinishReadOwnershipTestAsync(session, stream, run, caller.Token,
                extraTasks: new Task?[] { activity });
        }
    }

    // 拒绝和取消并不拥有底层读的终止权；原读不合作时，Run 可先退出，但 join 必须留下。
    [Theory(Timeout = 120_000)]
    [InlineData("denied", 0)]
    [InlineData("denied", 1)]
    [InlineData("decision-cancelled", 0)]
    [InlineData("decision-cancelled", 1)]
    [InlineData("caller-cancelled", 0)]
    [InlineData("caller-cancelled", 1)]
    [InlineData("timeout", 0)]
    [InlineData("timeout", 1)]
    [InlineData("write", 0)]
    [InlineData("write", 1)]
    [InlineData("flush", 0)]
    [InlineData("flush", 1)]
    public async Task Host_Read_Ownership_Failed_Run_Keeps_Noncooperative_Read_Buffer_Until_Successful_Join(string stop, int bytes)
    {
        ManualDeadlineClock clock = new();
        using CancellationTokenSource caller = new();
        using CleanupIoSslStream stream = new() { SingleReadSentinel = ActivitySentinel };
        TaskCompletionSource<LocalApprovalDecision> late = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubApprovalGate gate = new((request, _) => stop is "timeout" or "caller-cancelled"
            ? new ValueTask<LocalApprovalDecision>(late.Task)
            : ValueTask.FromResult(new LocalApprovalDecision(request.RequestId,
                stop == "denied" ? LocalApprovalOutcome.Denied
                    : stop == "decision-cancelled" ? LocalApprovalOutcome.Cancelled
                    : LocalApprovalOutcome.Approved,
                stop is "write" or "flush" ? request.RequestedPermission : null)));
        if (stop == "write")
        {
            stream.OnSuccessWrite = () => throw new IOException("受控 success 写出失败。");
        }
        if (stop == "flush")
        {
            stream.OnSuccessFlush = () => throw new IOException("受控 success flush 失败。");
        }
        ControlAuthSession session = NewReadOwnershipSession(stream, gate, clock: clock);
        Task<ControlAuthResult> run = session.RunAsync(caller.Token);
        Task<int>? activity = null;
        Task? join = null;
        Task? ownedJoin = null;
        try
        {
            await gate.FirstRequest.Task.WaitAsync(FrameDeadline);
            activity = ReadPendingApprovalTask(session);
            ReadOnlyMemory<byte> original = stream.SingleReadBuffer;
            Assert.NotSame(stream.PendingRead, activity);
            Assert.Equal(caller.Token, stream.SingleReadToken);
            if (stop == "caller-cancelled")
            {
                caller.Cancel();
                OperationCanceledException cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => run.WaitAsync(FrameDeadline));
                Assert.Equal(caller.Token, cancelled.CancellationToken);
            }
            else
            {
                if (stop == "timeout")
                {
                    clock.Advance(FastOptions.ApprovalWindow);
                }
                ControlAuthResult result = await run.WaitAsync(FrameDeadline);
                Assert.False(result.Completed);
                Assert.Equal(stop switch
                {
                    "denied" => ControlAuthSession.RejectApprovalDenied,
                    "decision-cancelled" => ControlAuthSession.RejectApprovalCancelled,
                    "timeout" => ControlAuthSession.RejectApprovalTimeout,
                    _ => ControlAuthSession.RejectSuccessNotDelivered,
                }, result.Rejection);
            }
            Assert.Equal(ControlSessionState.Closed, session.State);
            Assert.Equal(0, session.Context.SessionRegistry.ActiveSessionCount);
            Assert.Equal(0, session.Context.PendingApprovalLimiter.GlobalInUse);
            Assert.Equal(ActivitySentinel, original.Span[0]);
            Assert.False(activity.IsCompleted);
            Assert.False(stream.PendingRead.IsCompleted);

            // 决定先完成，排除 owned join 只是等 gate、其实已遗弃读的假阳性。
            CompleteReadTestDecision(late, gate);
            await ReadApprovalDecisionTask(session).WaitAsync(FrameDeadline);
            join = session.JoinPendingReadAsync();
            ownedJoin = session.JoinOwnedOperationsAsync();
            Assert.False(join.IsCompleted);
            Assert.False(ownedJoin.IsCompleted);
            Assert.Equal(ActivitySentinel, original.Span[0]);
            Assert.Equal(1, stream.PendingReadCalls);

            stream.CompleteSingleRead(bytes);
            await join.WaitAsync(FrameDeadline);
            await ownedJoin.WaitAsync(FrameDeadline);
            Assert.True(activity.IsCompletedSuccessfully);
            Assert.Equal(bytes, await activity);
            Assert.Equal((byte)0, original.Span[0]);
            Assert.Same(activity, ReadPendingApprovalTask(session));
            Assert.Equal(1, stream.PendingReadCalls);
        }
        finally
        {
            CompleteReadTestDecision(late, gate);
            await FinishReadOwnershipTestAsync(session, stream, run, caller.Token,
                extraTasks: new Task?[] { activity, join, ownedJoin, late.Task });
        }
    }

    [Theory(Timeout = 120_000)]
    [InlineData("denied", "io")]
    [InlineData("denied", "disposed")]
    [InlineData("denied", "unexpected")]
    [InlineData("denied", "cancelled")]
    [InlineData("approved", "io")]
    [InlineData("approved", "disposed")]
    [InlineData("approved", "unexpected")]
    [InlineData("approved", "cancelled")]
    [InlineData("no-approval", "io")]
    [InlineData("no-approval", "disposed")]
    [InlineData("no-approval", "unexpected")]
    [InlineData("no-approval", "cancelled")]
    public async Task Host_Read_Ownership_Unsuccessful_Activity_Never_Clears_The_Source_Written_Buffer(string path, string outcome)
    {
        Exception failure = outcome switch
        {
            "io" => new IOException("受控读 IO 故障。"),
            "disposed" => new ObjectDisposedException("受控单字节读"),
            "cancelled" => new OperationCanceledException(new CancellationToken(true)),
            _ => new InvalidOperationException("受控读意外故障。"),
        };
        using CleanupIoSslStream stream = new() { SingleReadSentinel = ActivitySentinel };
        StubApprovalGate gate = path == "denied"
            ? StubApprovalGate.WithOutcome(LocalApprovalOutcome.Denied)
            : StubApprovalGate.Approve();
        ControlAuthSession session = NewReadOwnershipSession(stream, gate, path != "no-approval");
        Task<ControlAuthResult> run = session.RunAsync(CancellationToken.None);
        Task<int>? activity = null;
        Task? join = null;
        try
        {
            await stream.SingleReadStarted.Task.WaitAsync(FrameDeadline);
            ReadOnlyMemory<byte> original = stream.SingleReadBuffer;
            Assert.Equal(ActivitySentinel, original.Span[0]);
            if (path != "no-approval")
            {
                activity = ReadPendingApprovalTask(session);
                Assert.NotSame(stream.PendingRead, activity);
                Assert.False(activity.IsCompleted);
            }
            else
            {
                Assert.Null(ReadPendingApprovalOwner(session));
            }
            if (path == "denied")
            {
                Assert.Equal(ControlAuthSession.RejectApprovalDenied, (await run.WaitAsync(FrameDeadline)).Rejection);
            }
            else
            {
                Assert.False(run.IsCompleted);
                Assert.Equal(1, session.Context.SessionRegistry.ActiveSessionCount);
            }
            join = session.JoinPendingReadAsync();
            Assert.Equal(path == "no-approval", join.IsCompletedSuccessfully);
            Assert.Equal(ActivitySentinel, original.Span[0]);

            // sentinel 在正常 ReadAsync 前缀中写入；故障/取消来自原源，不伪造 owner 或 Task 状态。
            stream.FailSingleRead(failure);
            ControlAuthResult result = await run.WaitAsync(FrameDeadline);
            Assert.Equal(path != "denied", result.Completed);
            if (path != "no-approval" && outcome == "unexpected")
            {
                Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
                    () => join.WaitAsync(FrameDeadline)));
            }
            else
            {
                await join.WaitAsync(FrameDeadline);
            }
            Assert.Equal(outcome == "cancelled", stream.PendingRead.IsCanceled);
            Assert.Equal(outcome != "cancelled", stream.PendingRead.IsFaulted);
            Assert.Equal(ActivitySentinel, original.Span[0]);
            Assert.Equal(0, session.Context.SessionRegistry.ActiveSessionCount);
            Assert.Equal(1, stream.PendingReadCalls);
            if (activity is not null)
            {
                Assert.False(activity.IsCompletedSuccessfully);
                Assert.Same(activity, ReadPendingApprovalTask(session));
            }
        }
        finally
        {
            await FinishReadOwnershipTestAsync(session, stream, run, readFailure: failure,
                extraTasks: new Task?[] { activity, join });
        }
    }

    [Theory(Timeout = 120_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_Read_Ownership_Holding_Caller_Cancellation_Propagates_Without_Clearing_Unsuccessful_Read(bool requireApproval)
    {
        using CancellationTokenSource caller = new();
        using CleanupIoSslStream stream = new() { SingleReadSentinel = ActivitySentinel };
        ControlAuthSession session = NewReadOwnershipSession(stream, StubApprovalGate.Approve(), requireApproval);
        Task<ControlAuthResult> run = session.RunAsync(caller.Token);
        try
        {
            await stream.SingleReadStarted.Task.WaitAsync(FrameDeadline);
            Assert.Equal(ControlSessionState.Authenticated, session.State);
            Assert.Equal(caller.Token, stream.SingleReadToken);
            caller.Cancel();
            Assert.False(stream.PendingRead.IsCompleted);
            Assert.False(run.IsCompleted);
            Assert.Equal(ActivitySentinel, stream.SingleReadBuffer.Span[0]);
            stream.FailSingleRead(new OperationCanceledException(caller.Token));
            OperationCanceledException cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => run.WaitAsync(FrameDeadline));
            Assert.Equal(caller.Token, cancelled.CancellationToken);
            await session.JoinPendingReadAsync().WaitAsync(FrameDeadline);
            Assert.Equal(ActivitySentinel, stream.SingleReadBuffer.Span[0]);
            Assert.Equal(0, session.Context.SessionRegistry.ActiveSessionCount);
            Assert.Equal(1, stream.PendingReadCalls);
        }
        finally
        {
            await FinishReadOwnershipTestAsync(session, stream, run, caller.Token);
        }
    }

    // 全部前序依赖同步完成：直接记录 ReadAsync 前缀的线程和时钟，不靠 Task.Delay 猜时序。
    [Theory(Timeout = 120_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_Read_Ownership_Activity_Read_Prefix_Stays_On_Calling_Thread_At_The_Original_Position(bool requireApproval)
    {
        ManualDeadlineClock clock = new();
        using CancellationTokenSource caller = new();
        using CleanupIoSslStream stream = new() { SingleReadSentinel = ActivitySentinel };
        int readThread = -1;
        int readTimers = -1;
        int readRegistrations = -1;
        int gateTimers = -1;
        int writeReads = -1;
        int flushReads = -1;
        object? ownerInGate = null;
        Task<int>? taskInGate = null;
        Memory<byte> argumentMemory = default;
        CancellationToken argumentToken = default;
        ControlSessionState? stateAtRead = null;
        object? fieldAtRead = null;
        ControlAuthSession? session = null;
        StubApprovalGate gate = new((request, _) =>
        {
            gateTimers = clock.TimerCount;
            ownerInGate = ReadPendingApprovalOwner(session!);
            taskInGate = ReadPendingApprovalTask(session!);
            return ValueTask.FromResult(new LocalApprovalDecision(
                request.RequestId, LocalApprovalOutcome.Approved, request.RequestedPermission));
        });
        session = NewReadOwnershipSession(stream, gate, requireApproval, clock);
        stream.OnSingleRead = (buffer, token) =>
        {
            readThread = Environment.CurrentManagedThreadId;
            readTimers = clock.TimerCount;
            readRegistrations = session.Context.SessionRegistry.ActiveSessionCount;
            stateAtRead = session.State;
            fieldAtRead = ReadPendingApprovalOwner(session);
            argumentMemory = buffer;
            argumentToken = token;
            // 跨过整个审批时长；此处本来就在审批 timer 创建之前，不能挪进窗口或另一个线程。
            clock.Advance(FastOptions.ApprovalWindow + TimeSpan.FromTicks(1), fireTimers: false);
        };
        stream.OnSuccessWrite = () => writeReads = stream.PendingReadCalls;
        stream.OnSuccessFlush = () => flushReads = stream.PendingReadCalls;
        int callingThread = Environment.CurrentManagedThreadId;
        Task<ControlAuthResult> run = session.RunAsync(caller.Token);
        try
        {
            Assert.Equal(callingThread, readThread);
            Assert.Equal(0, readTimers);
            Assert.Equal(requireApproval ? 0 : 1, readRegistrations);
            Assert.Equal(requireApproval ? ControlSessionState.Authenticating : ControlSessionState.Authenticated,
                stateAtRead);
            Assert.Null(fieldAtRead);
            Assert.Equal(requireApproval ? 1 : -1, gateTimers);
            Assert.Equal(requireApproval ? 1 : 0, writeReads);
            Assert.Equal(requireApproval ? 1 : 0, flushReads);
            Assert.Equal(caller.Token, argumentToken);
            Assert.Equal(caller.Token, stream.SingleReadToken);
            Assert.True(argumentMemory.Equals(stream.SingleReadBuffer));
            Assert.True(MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)argumentMemory, out var originalArray));
            Assert.Single(Assert.IsType<byte[]>(originalArray.Array));
            Assert.Equal(1, originalArray.Count);
            Assert.Equal(0, originalArray.Offset);
            Assert.Equal(ControlSessionState.Authenticated, session.State);
            Assert.Equal(1, session.Context.SessionRegistry.ActiveSessionCount);
            Assert.False(run.IsCompleted);
            Assert.False(stream.PendingRead.IsCompleted);
            Assert.Equal(1, stream.PendingReadCalls);
            if (requireApproval)
            {
                Assert.NotSame(stream.PendingRead, taskInGate);
                Assert.Same(ownerInGate, ReadPendingApprovalOwner(session));
                Assert.Same(taskInGate, ReadPendingApprovalTask(session));
            }
            else
            {
                Assert.Null(ReadPendingApprovalOwner(session));
                Assert.True(session.JoinPendingReadAsync().IsCompletedSuccessfully);
                Assert.Equal(0, gate.RequestCount);
            }
            stream.CompleteSingleRead(0);
            Assert.True((await run.WaitAsync(FrameDeadline)).Completed);
        }
        finally
        {
            await FinishReadOwnershipTestAsync(session, stream, run, caller.Token,
                extraTasks: new Task?[] { taskInGate });
        }
    }

    [Theory(Timeout = 120_000)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Host_Read_Ownership_Activity_During_Success_Write_Does_Not_Retroactively_Reject_Approval(int bytes)
    {
        using CleanupIoSslStream stream = new() { SingleReadSentinel = ActivitySentinel };
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        stream.OnWriteAsync = async type =>
        {
            if (type == "auth_success")
            {
                entered.TrySetResult();
                await resume.Task.ConfigureAwait(false);
            }
        };
        ControlAuthSession session = NewReadOwnershipSession(stream, StubApprovalGate.Approve());
        Task<ControlAuthResult> run = session.RunAsync(CancellationToken.None);
        Task<int>? activity = null;
        try
        {
            await Task.WhenAny(entered.Task, run).WaitAsync(FrameDeadline);
            Assert.True(entered.Task.IsCompletedSuccessfully);
            activity = ReadPendingApprovalTask(session);
            Assert.False(activity.IsCompleted);
            Assert.Equal(0, session.Context.SessionRegistry.ActiveSessionCount);
            stream.CompleteSingleRead(bytes);
            Assert.Equal(bytes, await activity.WaitAsync(FrameDeadline));
            Assert.False(run.IsCompleted);
            Assert.Equal(ActivitySentinel, stream.SingleReadBuffer.Span[0]);
            Assert.DoesNotContain("authentication_failed", stream.WriteAttempts);
            resume.SetResult();
            ControlAuthResult result = await run.WaitAsync(FrameDeadline);
            Assert.True(result.Completed);
            Assert.Null(result.Rejection);
            Assert.Equal(new[] { "auth_challenge", "approval_pending", "auth_success" }, stream.AcceptedFrames);
            Assert.Equal(0, session.Context.SessionRegistry.ActiveSessionCount);
            Assert.Same(activity, ReadPendingApprovalTask(session));
            Assert.Equal(1, stream.PendingReadCalls);
        }
        finally
        {
            resume.TrySetResult();
            await FinishReadOwnershipTestAsync(session, stream, run,
                extraTasks: new Task?[] { activity });
        }
    }

    // 合法 IValueTaskSource：源已成功不等于 async 包装任务已完成；continuation 注册正常返回。
    [Theory(Timeout = 120_000)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Host_Read_Ownership_Approval_Observes_Wrapper_Completion_Not_ValueTask_Source_Status(int bytes)
    {
        DeferredActivitySource source = new();
        using CleanupIoSslStream stream = new()
        {
            SingleReadSentinel = ActivitySentinel,
            SingleReadSource = (_, _) => source.Read(),
        };
        TaskCompletionSource<LocalApprovalDecision> late = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubApprovalGate gate = new((_, _) => new ValueTask<LocalApprovalDecision>(late.Task));
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        stream.OnWriteAsync = async type =>
        {
            if (type == "auth_success")
            {
                entered.TrySetResult();
                await resume.Task.ConfigureAwait(false);
            }
        };
        ControlAuthSession session = NewReadOwnershipSession(stream, gate);
        Task<ControlAuthResult> run = session.RunAsync(CancellationToken.None);
        Task<int>? activity = null;
        try
        {
            await source.Registered.Task.WaitAsync(FrameDeadline);
            LocalApprovalRequest request = await gate.FirstRequest.Task.WaitAsync(FrameDeadline);
            activity = ReadPendingApprovalTask(session);
            Assert.False(activity.IsCompleted);
            Assert.Equal(1, source.Registrations);
            source.CompleteWithoutDispatch(bytes);
            Assert.Equal(ValueTaskSourceStatus.Succeeded, source.Status);
            Assert.Equal(0, source.ResultCalls);
            Assert.False(activity.IsCompleted);
            Assert.Equal(ActivitySentinel, stream.SingleReadBuffer.Span[0]);

            late.SetResult(new LocalApprovalDecision(
                request.RequestId, LocalApprovalOutcome.Approved, request.RequestedPermission));
            await Task.WhenAny(entered.Task, run).WaitAsync(FrameDeadline);
            Assert.True(entered.Task.IsCompletedSuccessfully);
            Assert.False(activity.IsCompleted);
            Assert.False(run.IsCompleted);
            Assert.Equal(0, source.ResultCalls);
            Assert.Equal(0, session.Context.SessionRegistry.ActiveSessionCount);
            Assert.DoesNotContain("authentication_failed", stream.WriteAttempts);
            Assert.Equal(ActivitySentinel, stream.SingleReadBuffer.Span[0]);

            source.ReleaseContinuation();
            Assert.Equal(bytes, await activity.WaitAsync(FrameDeadline));
            Assert.Equal(1, source.ResultCalls);
            Assert.Equal(ActivitySentinel, stream.SingleReadBuffer.Span[0]);
            resume.SetResult();
            Assert.True((await run.WaitAsync(FrameDeadline)).Completed);
            Assert.Same(activity, ReadPendingApprovalTask(session));
            Assert.Equal(1, stream.PendingReadCalls);
            Assert.Equal(1, source.Registrations);
            Assert.Equal(new[] { "auth_challenge", "approval_pending", "auth_success" }, stream.AcceptedFrames);
        }
        finally
        {
            source.CompleteWithoutDispatch(0);
            source.ReleaseContinuation();
            CompleteReadTestDecision(late, gate);
            resume.TrySetResult();
            await FinishReadOwnershipTestAsync(session, stream, run,
                extraTasks: new Task?[] { activity, late.Task });
        }
    }

    [Theory(Timeout = 120_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_Read_Ownership_Activity_Read_Synchronous_Exception_Remains_Captured_And_Uncleared(bool requireApproval)
    {
        InvalidOperationException failure = new("受控 ReadAsync 同步前缀故障。");
        using CleanupIoSslStream stream = new()
        {
            SingleReadSentinel = ActivitySentinel,
            OnSingleRead = (_, _) => throw failure,
        };
        StubApprovalGate gate = StubApprovalGate.Approve();
        ControlAuthSession session = NewReadOwnershipSession(stream, gate, requireApproval);
        // 这里必须返回原 Run Task，不能从构造 owner 的同步调用点抛出读异常。
        Task<ControlAuthResult> run = session.RunAsync(CancellationToken.None);
        Task<int>? activity = null;
        try
        {
            ControlAuthResult result = await run.WaitAsync(FrameDeadline);
            Assert.Equal(!requireApproval, result.Completed);
            Assert.Equal(requireApproval ? ControlAuthSession.RejectApprovalDisconnected : null, result.Rejection);
            Assert.Equal(ActivitySentinel, stream.SingleReadBuffer.Span[0]);
            Assert.Equal(1, stream.PendingReadCalls);
            Assert.Equal(requireApproval ? 1 : 0, gate.RequestCount);
            Assert.Equal(0, session.Context.SessionRegistry.ActiveSessionCount);
            if (requireApproval)
            {
                activity = ReadPendingApprovalTask(session);
                Assert.True(activity.IsFaulted);
                Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
                    session.JoinPendingReadAsync));
            }
            else
            {
                Assert.Null(ReadPendingApprovalOwner(session));
                await session.JoinPendingReadAsync().WaitAsync(FrameDeadline);
                Assert.Contains("auth_success", stream.AcceptedFrames);
            }
        }
        finally
        {
            await FinishReadOwnershipTestAsync(session, stream, run, readFailure: failure,
                extraTasks: new Task?[] { activity });
        }
    }

    [Theory(Timeout = 120_000)]
    [InlineData(false, "io")]
    [InlineData(false, "cancelled")]
    [InlineData(false, "unexpected")]
    [InlineData(true, "io")]
    [InlineData(true, "cancelled")]
    [InlineData(true, "unexpected")]
    public async Task Host_Read_Ownership_ValueTask_GetResult_Fault_Is_Uncleared(bool requireApproval, string outcome)
    {
        Exception failure = outcome switch
        {
            "io" => new IOException("受控异步源 GetResult 故障。"),
            "cancelled" => new OperationCanceledException(new CancellationToken(true)),
            _ => new InvalidOperationException("受控异步源 GetResult 意外故障。"),
        };
        DeferredActivitySource source = new();
        using CleanupIoSslStream stream = new()
        {
            SingleReadSentinel = ActivitySentinel,
            SingleReadSource = (_, _) => source.Read(),
        };
        ControlAuthSession session = NewReadOwnershipSession(stream, StubApprovalGate.Approve(), requireApproval);
        Task<ControlAuthResult> run = session.RunAsync(CancellationToken.None);
        Task<int>? activity = null;
        try
        {
            await source.Registered.Task.WaitAsync(FrameDeadline);
            Assert.Equal(ControlSessionState.Authenticated, session.State);
            Assert.False(run.IsCompleted);
            if (requireApproval) activity = ReadPendingApprovalTask(session);
            else Assert.Null(ReadPendingApprovalOwner(session));
            source.FailWithoutDispatch(failure);
            Assert.Equal(outcome == "cancelled" ? ValueTaskSourceStatus.Canceled : ValueTaskSourceStatus.Faulted, source.Status);
            Assert.Equal(0, source.ResultCalls);
            Assert.False(run.IsCompleted);
            Assert.Equal(ActivitySentinel, stream.SingleReadBuffer.Span[0]);
            if (activity is not null) Assert.False(activity.IsCompleted);
            source.ReleaseContinuation();
            Assert.True((await run.WaitAsync(FrameDeadline)).Completed);
            Assert.Equal(1, source.ResultCalls);
            if (requireApproval && outcome == "unexpected")
                Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(session.JoinPendingReadAsync));
            else
                await session.JoinPendingReadAsync().WaitAsync(FrameDeadline);
            Assert.Equal(ActivitySentinel, stream.SingleReadBuffer.Span[0]);
            Assert.Equal(0, session.Context.SessionRegistry.ActiveSessionCount);
            Assert.Equal(1, stream.PendingReadCalls);
            Assert.Equal(1, source.Registrations);
        }
        finally
        {
            source.CompleteWithoutDispatch(0);
            source.ReleaseContinuation();
            await FinishReadOwnershipTestAsync(session, stream, run, readFailure: failure,
                extraTasks: new Task?[] { activity });
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task Host_Read_Ownership_Cleanup_Observes_All_Faults_And_Joins_Later_Task()
    {
        using CleanupIoSslStream stream = new();
        ControlAuthSession session = NewReadOwnershipSession(stream, StubApprovalGate.Approve());
        InvalidOperationException injected = new("明确注入故障。");
        IOException sibling = new("不得豁免的兄弟故障。");
        Task both = Task.WhenAll(Task.FromException(injected), Task.FromException(sibling));
        TaskCompletionSource later = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task finish = FinishReadOwnershipTestAsync(session, stream, Task.CompletedTask,
            readFailure: injected, extraTasks: new Task?[] { both, later.Task });
        try
        {
            Assert.False(finish.IsCompleted);
            later.SetResult();
            AggregateException error = await Assert.ThrowsAsync<AggregateException>(
                () => finish.WaitAsync(FrameDeadline));
            Assert.Same(sibling, Assert.Single(error.Flatten().InnerExceptions));
        }
        finally
        {
            later.TrySetResult();
            try { await finish.WaitAsync(FrameDeadline); }
            catch (AggregateException error) when (
                error.Flatten().InnerExceptions.All(ex => ReferenceEquals(ex, sibling))) { }
        }
    }

    [Theory(Timeout = 120_000)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Host_Read_Ownership_Cleanup_Exempts_Only_The_Expected_Cancellation_Token(
        bool useCaller, bool matchingToken)
    {
        using CancellationTokenSource expected = new();
        using CancellationTokenSource other = new();
        expected.Cancel();
        other.Cancel();
        using CleanupIoSslStream stream = new();
        ControlAuthSession session = NewReadOwnershipSession(stream, StubApprovalGate.Approve());
        CancellationToken actual = matchingToken ? expected.Token : other.Token;
        Task cancelled = Task.FromCanceled(actual);
        Task finish = FinishReadOwnershipTestAsync(session, stream, Task.CompletedTask,
            caller: useCaller ? expected.Token : default,
            readFailure: useCaller ? null : new OperationCanceledException(expected.Token),
            extraTasks: new Task?[] { cancelled });
        if (matchingToken)
        {
            await finish.WaitAsync(FrameDeadline);
        }
        else
        {
            AggregateException error = await Assert.ThrowsAsync<AggregateException>(
                () => finish.WaitAsync(FrameDeadline));
            OperationCanceledException cancellation = Assert.IsAssignableFrom<OperationCanceledException>(
                Assert.Single(error.Flatten().InnerExceptions));
            Assert.Equal(actual, cancellation.CancellationToken);
        }
    }

    private static ControlAuthSession NewReadOwnershipSession(
        CleanupIoSslStream stream, StubApprovalGate gate, bool requireApproval = true, TimeProvider? clock = null)
    {
        ControlAuthContext context = NewContext(gate, options: FastOptions with
        {
            RequireLocalApproval = requireApproval,
        }) with { TimeProvider = clock ?? new ManualDeadlineClock() };
        ConnectionSecurityContext security = new(
            IPAddress.Loopback, IPAddress.Loopback, 0, SslProtocols.None,
            new byte[CertificatePin.LengthBytes]);
        return new ControlAuthSession(stream, security, context, BuildPausedSuccessTimeouts());
    }

    private static void CompleteReadTestDecision(
        TaskCompletionSource<LocalApprovalDecision> late, StubApprovalGate gate) =>
        late.TrySetResult(new LocalApprovalDecision(
            gate.LastRequest?.RequestId ?? Guid.Empty, LocalApprovalOutcome.Denied));

    private static async Task FinishReadOwnershipTestAsync(
        ControlAuthSession session, CleanupIoSslStream stream, Task run,
        CancellationToken caller = default, Exception? readFailure = null, Task?[]? extraTasks = null)
    {
        stream.Dispose();
        List<Exception> errors = new();
        // 每项独立 await：原 Run 的失败不能使原读、已保存的 join 或原 gate 任务无人观察。
        foreach (Task? task in new Task?[] { run, stream.PendingRead }.Concat(extraTasks ?? Array.Empty<Task?>()))
        {
            await JoinOneAsync(task);
        }
        // 等 Run 之后再取得这两项，避免提前快照漏掉随后才被保存的 owner/decision。
        await JoinOneAsync(session.JoinPendingReadAsync());
        await JoinOneAsync(session.JoinOwnedOperationsAsync());
        if (errors.Count != 0)
        {
            throw new AggregateException("控制活动读测试收尾失败。", errors);
        }

        async Task JoinOneAsync(Task? task)
        {
            if (task is null) return;
            try
            {
                await task.WaitAsync(FrameDeadline);
            }
            catch (OperationCanceledException ex) when (task.IsCanceled
                && ((caller.IsCancellationRequested && ex.CancellationToken == caller)
                    || (readFailure is OperationCanceledException expected
                        && ex.CancellationToken == expected.CancellationToken)))
            {
            }
            catch (Exception ex)
            {
                // await 只抛首错；逐项核对原 Task 的异常树，不能连带豁免兄弟故障。
                if (task.IsFaulted && task.Exception is { } failure)
                {
                    errors.AddRange(failure.Flatten().InnerExceptions.Where(
                        item => !ReferenceEquals(item, readFailure)));
                }
                else
                {
                    // 包括未匹配 token 的取消及 WaitAsync 自身的超时。
                    errors.Add(ex);
                }
            }
        }
    }

    private sealed class DeferredActivitySource : IValueTaskSource<int>
    {
        private readonly object _gate = new();
        private ManualResetValueTaskSourceCore<int> _result;
        private ManualResetValueTaskSourceCore<int> _dispatch = new() { RunContinuationsAsynchronously = true };
        private bool _completed;
        private bool _released;
        private int _registrations;
        private int _resultCalls;

        public TaskCompletionSource Registered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Registrations => Volatile.Read(ref _registrations);
        public int ResultCalls => Volatile.Read(ref _resultCalls);
        public ValueTaskSourceStatus Status => _result.GetStatus(_result.Version);
        public ValueTask<int> Read() => new(this, _result.Version);

        public void CompleteWithoutDispatch(int result)
        {
            lock (_gate)
            {
                if (_completed) return;
                _result.SetResult(result);
                _completed = true;
            }
        }

        public void FailWithoutDispatch(Exception failure)
        {
            lock (_gate)
            {
                if (_completed) return;
                _result.SetException(failure);
                _completed = true;
            }
        }

        public void ReleaseContinuation()
        {
            lock (_gate)
            {
                if (_released) return;
                if (!_completed) throw new InvalidOperationException("必须先完成原读源，再派发 continuation。");
                _released = true;
                _dispatch.SetResult(0);
            }
        }

        public int GetResult(short token)
        {
            Interlocked.Increment(ref _resultCalls);
            return _result.GetResult(token);
        }

        public ValueTaskSourceStatus GetStatus(short token) => _result.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token,
            ValueTaskSourceOnCompletedFlags flags)
        {
            Interlocked.Increment(ref _registrations);
            // 两个 core 不 Reset，版本相同。dispatch core 按真实 flags 保存并派发唯一 continuation；
            // result core 单独发布成功状态，因此可以合法延后 continuation，而不是令 OnCompleted 抛异常。
            _dispatch.OnCompleted(continuation, state, token, flags);
            Registered.TrySetResult();
        }
    }
}
