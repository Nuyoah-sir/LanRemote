using System.Collections.Concurrent;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;
using Admission = LanRemote.Transport.SessionRegistry.SessionRegistration.DisplaySwitchAdmission;
using Result = LanRemote.Transport.DisplaySwitchExecutor.ExecutionResult;
using Status = LanRemote.Transport.SessionRegistry.SessionRegistration.DisplaySwitchAdmissionStatus;

namespace LanRemote.Transport.Tests;

public sealed class DisplaySwitchExecutorTests
{
    // 门闸证明顺序；超时只用于报告挂起，不能作为互斥或完整 join 的通过证据。
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Construction_And_Idle_Stop_Do_Not_Touch_Factory_Source_Or_Lease()
    {
        await using Fixture f = await Fixture.CreateAsync();
        int calls = 0;
        DisplaySwitchExecutor executor = f.Executor((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<IVideoFrameProducer>(f.Producer());
        });
        Task stop = executor.StopRequired, completion = executor.Completion;
        Assert.False(stop.IsCompleted);
        Assert.False(completion.IsCompleted);
        Assert.False(executor.HasActiveExecution);
        Assert.Equal(0, calls);
        AssertResourcesUnchanged(f);

        executor.RequestStop();
        executor.RequestStop();
        Assert.Same(stop, executor.StopRequired);
        Assert.Same(completion, executor.Completion);
        Assert.Same(completion, executor.DisposeAsync().AsTask());
        await Join(completion);
        Assert.True(stop.IsCompletedSuccessfully);
        Assert.Equal(0, calls);
        Reject(executor, f.Request(0), Status.Unavailable);
        AssertResourcesUnchanged(f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Executor_Can_Be_Assembled_Only_Once_Even_After_Stop(bool stop)
    {
        await using Fixture f = await Fixture.CreateAsync();
        DisplaySwitchExecutor executor = f.Executor((_, _) => Task.FromResult<IVideoFrameProducer>(f.Producer()));
        if (stop) await Join(executor.DisposeAsync().AsTask());
        Assert.Throws<InvalidOperationException>(() => f.Admission.CreateExecutor((_, _) => null!));
        AssertResourcesUnchanged(f);
    }

    [Fact]
    public async Task Null_Factory_Does_Not_Consume_Assembly()
    {
        await using Fixture f = await Fixture.CreateAsync();
        Assert.Equal("prepare", Assert.Throws<ArgumentNullException>(() => f.Admission.CreateExecutor(null!)).ParamName);
        DisplaySwitchExecutor executor = f.Executor((_, _) => Task.FromResult<IVideoFrameProducer>(f.Producer()));
        Assert.False(executor.StopRequired.IsCompleted);
        AssertResourcesUnchanged(f);
    }

    [Fact]
    public async Task Active_Manual_Reservation_Prevents_Assembly_Without_Taking_Ownership()
    {
        await using Fixture f = await Fixture.CreateAsync();
        Assert.Equal(Status.Accepted, f.Admission.TryReserve(f.Request(0), out var reservation));
        using (reservation)
        {
            Assert.Throws<InvalidOperationException>(() => f.Admission.CreateExecutor((_, _) => null!));
            Assert.True(reservation!.IsCurrent);
        }
        DisplaySwitchExecutor executor = f.Executor((_, _) => Task.FromResult<IVideoFrameProducer>(f.Producer()));
        Reject(executor, f.Request(0), Status.StaleSequence);
        await Join(f.Execute(executor, f.Request(1)));
    }

    [Fact]
    public async Task Manually_Constructed_Admission_Cannot_Assemble_An_Executor()
    {
        await using Fixture f = await Fixture.CreateAsync();
        using Admission forged = new(f.Registration, f.Catalog);
        Assert.Throws<InvalidOperationException>(() => forged.CreateExecutor((_, _) => null!));
        DisplaySwitchExecutor executor = f.Executor((_, _) => Task.FromResult<IVideoFrameProducer>(f.Producer()));
        Assert.True((await Join(f.Execute(executor, f.Request(0)))).Transferred);
    }

    [Fact]
    public async Task Forged_Executor_Cannot_Borrow_Identity_Or_Close_Real_Executor()
    {
        await using Fixture f = await Fixture.CreateAsync();
        int forgedCalls = 0;
        DisplaySwitchExecutor real = f.Executor((_, _) => Task.FromResult<IVideoFrameProducer>(f.Producer()));
        DisplaySwitchExecutor forged = f.TrackExecutor(new DisplaySwitchExecutor(new object(), f.Admission, (_, _) =>
        {
            Interlocked.Increment(ref forgedCalls);
            return null!;
        }));
        Reject(forged, f.Request(long.MaxValue), Status.Unavailable);
        await Join(forged.DisposeAsync().AsTask());
        Assert.Equal(0, forgedCalls);
        Assert.True((await Join(f.Execute(real, f.Request(0)))).ReplacementCompleted);
        Assert.False(real.StopRequired.IsCompleted);
    }

    [Fact]
    public async Task Assembly_Rejects_Manual_Reservation_Without_Consuming_Sequence()
    {
        await using Fixture f = await Fixture.CreateAsync();
        DisplaySwitchExecutor executor = f.Executor((_, _) => Task.FromResult<IVideoFrameProducer>(f.Producer()));
        Assert.Equal(Status.Unavailable, f.Admission.TryReserve(f.Request(91), out var reservation));
        Assert.Null(reservation);
        Assert.True((await Join(f.Execute(executor, f.Request(0)))).Transferred);
    }

    [Theory]
    [InlineData("withdraw")]
    [InlineData("control-cancel")]
    [InlineData("unregister")]
    [InlineData("lease-complete")]
    [InlineData("admission-close")]
    [InlineData("source-stop")]
    public async Task Invalid_Parent_Or_Closed_Admission_Prevents_Assembly(string reason)
    {
        await using Fixture f = await Fixture.CreateAsync();
        f.Invalidate(reason);
        Assert.Throws<InvalidOperationException>(() => f.Admission.CreateExecutor((_, _) => null!));
    }

    [Fact]
    public async Task Null_Request_Does_Not_Close_Executor_Or_Consume_Sequence()
    {
        await using Fixture f = await Fixture.CreateAsync();
        DisplaySwitchExecutor executor = f.Executor((_, _) => Task.FromResult<IVideoFrameProducer>(f.Producer()));
        Assert.Equal("request", Assert.Throws<ArgumentNullException>(() => executor.TryExecute(null!, out _)).ParamName);
        Assert.True((await Join(f.Execute(executor, f.Request(0)))).ReplacementCompleted);
    }

    [Fact]
    public async Task Success_Passes_Exact_Target_And_Retires_Initial_Before_Starting_Candidate()
    {
        await using Fixture f = await Fixture.CreateAsync();
        ProbeProducer candidate = f.Producer();
        TaskCompletionSource oldLifetime = f.Pending();
        f.Initial.SetAllRoots(oldLifetime.Task);
        ControlledFactory factory = f.Factory(candidate);
        factory.Result.SetResult(candidate);
        DisplaySwitchExecutor executor = f.Executor(factory.Invoke);
        Task<Result> execution = f.Execute(executor, f.Request(0));
        await Join(f.Initial.AllEntered);
        Assert.Same(f.Catalog.Displays[1], factory.Target);
        Assert.Equal(0, candidate.StartCalls);
        Assert.Equal(0, candidate.ReadCalls);
        Assert.False(execution.IsCompleted);
        Assert.True(executor.HasActiveExecution);
        oldLifetime.SetResult();
        Result result = await Join(execution);
        Assert.Equal(new Result(true, true), result);
        Assert.Equal(1, candidate.StartCalls);
        Assert.Equal(1, f.Initial.Stop.Calls);
        Assert.Equal(1, f.Initial.Dispose.Calls);
        Assert.Equal(1, f.Initial.Lifetime.Calls);
        Assert.False(executor.HasActiveExecution);
        Assert.False(executor.StopRequired.IsCompleted);
        Assert.False(executor.Completion.IsCompleted);
        Assert.False(f.Source.Termination.IsCompleted);
        Assert.False(f.Lease.Completed.IsCompleted);
    }

    [Fact]
    public async Task Historical_Success_Does_Not_Become_A_Current_Permission_Or_Send_Barrier()
    {
        await using Fixture f = await Fixture.CreateAsync();
        ProbeProducer candidate = f.Producer();
        DisplaySwitchExecutor executor = f.Executor((_, _) => Task.FromResult<IVideoFrameProducer>(candidate));
        Result result = await Join(f.Execute(executor, f.Request(0)));
        f.Publication.Dispose();
        f.Registration.Dispose();
        executor.RequestStop();
        await Join(executor.Completion);
        Assert.Equal(new Result(true, true), result);
        Assert.Equal(0, candidate.ReadCalls);
        Assert.Equal(0, candidate.Stop.Calls);
        Assert.False(f.Source.Termination.IsCompleted);
        Assert.False(f.Lease.Completed.IsCompleted);
        Reject(executor, f.Request(1), Status.Unavailable);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("catalog")]
    [InlineData("display")]
    public async Task Invalid_Request_Never_Invokes_Factory_And_Does_Not_Consume_Sequence(string reason)
    {
        await using Fixture f = await Fixture.CreateAsync();
        ControlledFactory factory = f.Factory();
        factory.Result.SetResult(factory.Candidate);
        DisplaySwitchExecutor executor = f.Executor(factory.Invoke);
        DisplaySwitchRequestFrame invalid = reason switch
        {
            "session" => f.Request(91, session: Guid.NewGuid()),
            "catalog" => f.Request(91, catalog: f.Catalog.Sequence + 1),
            _ => f.Request(91, display: new DisplayId(255)),
        };
        Status expected = reason switch
        {
            "session" => Status.SessionMismatch,
            "catalog" => Status.CatalogMismatch,
            _ => Status.DisplayNotFound,
        };
        Reject(executor, invalid, expected);
        Assert.Equal(0, factory.Calls);
        await Join(f.Execute(executor, f.Request(0)));
        Assert.Equal(1, factory.Calls);
    }

    [Fact]
    public async Task Single_Flight_Rejects_Without_Queue_And_Busy_Does_Not_Consume_Sequence()
    {
        await using Fixture f = await Fixture.CreateAsync();
        ControlledFactory factory = f.Factory();
        int calls = 0;
        DisplaySwitchExecutor executor = f.Executor((target, token) =>
            Interlocked.Increment(ref calls) == 1 ? factory.Invoke(target, token) : Task.FromResult<IVideoFrameProducer>(f.Producer()));
        Task<Result> first = f.Execute(executor, f.Request(0));
        await Join(factory.Entered.Task);
        Reject(executor, f.Request(10), Status.Busy);
        Reject(executor, f.Request(0), Status.Busy);
        Assert.Equal(1, calls);
        factory.Result.SetResult(factory.Candidate);
        await Join(first);
        Assert.Equal(1, calls);
        Reject(executor, f.Request(0), Status.StaleSequence);
        await Join(f.Execute(executor, f.Request(1)));
        await Join(f.Execute(executor, f.Request(10)));
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData("withdraw")]
    [InlineData("control-cancel")]
    [InlineData("unregister")]
    [InlineData("lease-complete")]
    [InlineData("admission-close")]
    [InlineData("source-stop")]
    public async Task Active_Execution_Preserves_Unavailable_And_SessionMismatch_Before_Busy(string reason)
    {
        await using Fixture f = await Fixture.CreateAsync();
        ControlledFactory factory = f.Factory();
        DisplaySwitchExecutor executor = f.Executor(factory.Invoke);
        Task<Result> execution = f.Execute(executor, f.Request(0));
        await Join(factory.Entered.Task);
        Reject(executor, f.Request(1, session: Guid.NewGuid()), Status.SessionMismatch);
        Reject(executor, f.Request(1), Status.Busy);
        f.Invalidate(reason);
        Reject(executor, f.Request(1, session: Guid.NewGuid()), Status.Unavailable);
        Reject(executor, f.Request(1), Status.Unavailable);
        Assert.Equal(1, factory.Calls);
        executor.RequestStop();
        factory.Result.SetResult(factory.Candidate);
        Assert.Equal(new Result(false, false), await Join(execution));
        await Join(executor.Completion);
        AssertLocallyRetired(factory.Candidate);
    }

    [Fact]
    public async Task Null_Executor_Cannot_Reserve_Or_Transfer_A_Manual_Reservation()
    {
        await using Fixture f = await Fixture.CreateAsync();
        Assert.Equal("executor", Assert.Throws<ArgumentNullException>(() =>
            f.Admission.TryReserveForExecutor(null!, f.Request(91), out _)).ParamName);
        Assert.Equal(Status.Accepted, f.Admission.TryReserve(f.Request(0), out var reservation));
        using (reservation)
        {
            ProbeProducer candidate = f.Producer();
            Assert.Equal("executor", Assert.Throws<ArgumentNullException>(() =>
            {
                _ = reservation!.ReplaceProducerAsync(null!, candidate);
            }).ParamName);
            Assert.True(reservation!.IsCurrent);
            Assert.Equal(0, candidate.StartCalls);
            AssertResourcesUnchanged(f);
        }
        DisplaySwitchExecutor executor = f.Executor((_, _) => Task.FromResult<IVideoFrameProducer>(f.Producer()));
        Assert.True((await Join(f.Execute(executor, f.Request(1)))).Transferred);
    }

    [Fact]
    public async Task Original_Execution_Task_Remains_Single_Flight_After_Reservation_Release()
    {
        await using Fixture f = await Fixture.CreateAsync();
        ControlledFactory factory = f.Factory();
        int calls = 0;
        DisplaySwitchExecutor executor = f.Executor((target, token) =>
            Interlocked.Increment(ref calls) == 1
                ? factory.Invoke(target, token)
                : Task.FromResult<IVideoFrameProducer>(f.Producer()));
        Task<Result> original = f.Execute(executor, f.Request(0));
        try
        {
            await Join(factory.Entered.Task);
            // 仅测试反射释放真实预约，确定性隔离原Task门禁；不增加生产hook。
            var field = typeof(Admission).GetField("_active",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(field);
            var reservation = Assert.IsType<Admission.DisplaySwitchReservation>(field.GetValue(f.Admission));
            reservation.Dispose();
            Assert.False(reservation.IsCurrent);
            Assert.False(original.IsCompleted);
            Assert.True(executor.HasActiveExecution);
            Status status = executor.TryExecute(f.Request(1), out Task<Result>? unexpected);
            if (unexpected is not null) _ = f.Track(unexpected);
            Assert.Equal(Status.Busy, status);
            Assert.Null(unexpected);
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally
        {
            executor.RequestStop();
            factory.Release();
        }
        Assert.Equal(new Result(false, false), await Join(original));
        await Join(executor.Completion);
        AssertLocallyRetired(factory.Candidate);
    }

    [Fact]
    public async Task Maximum_Sequence_Is_Accepted_Only_Once()
    {
        await using Fixture f = await Fixture.CreateAsync();
        ControlledFactory factory = f.Factory();
        factory.Result.SetResult(factory.Candidate);
        DisplaySwitchExecutor executor = f.Executor(factory.Invoke);
        await Join(f.Execute(executor, f.Request(long.MaxValue)));
        Reject(executor, f.Request(long.MaxValue), Status.StaleSequence);
        Reject(executor, f.Request(long.MaxValue - 1), Status.StaleSequence);
        Reject(executor, f.Request(0), Status.StaleSequence);
        Assert.Equal(1, factory.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_Joins_Factory_Synchronous_Prefix_And_Late_Original_Task(bool synchronousPrefix)
    {
        await using Fixture f = await Fixture.CreateAsync();
        ControlledFactory factory = f.Factory(blockPrefix: synchronousPrefix);
        DisplaySwitchExecutor executor = f.Executor(factory.Invoke);
        Task<Result> execution = f.Execute(executor, f.Request(0));
        await Join(factory.Entered.Task);
        TaskCompletionSource cancelled = Signal();
        using CancellationTokenRegistration registration = factory.Token.Register(() => cancelled.TrySetResult());
        Task completion = executor.Completion;
        Task disposal = executor.DisposeAsync().AsTask();
        await Join(cancelled.Task);
        Assert.Same(completion, disposal);
        Assert.True(executor.StopRequired.IsCompletedSuccessfully);
        Assert.False(execution.IsCompleted);
        Assert.False(completion.IsCompleted);
        Reject(executor, f.Request(1), Status.Unavailable);
        AssertResourcesUnchanged(f);
        factory.Prefix.Release();
        await Join(factory.Returned.Task);
        Assert.False(execution.IsCompleted);
        Assert.False(completion.IsCompleted);
        factory.Result.SetResult(factory.Candidate);
        Assert.Equal(new Result(false, false), await Join(execution));
        await Join(completion);
        AssertLocallyRetired(factory.Candidate);
        AssertResourcesUnchanged(f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_Callback_Cannot_Block_Three_Cleanup_Notifications_But_Is_Joined(bool throws)
    {
        await using Fixture f = await Fixture.CreateAsync();
        ControlledFactory factory = f.Factory();
        Barrier callback = f.Gate();
        TaskCompletionSource callbackExited = Signal();
        InvalidOperationException failure = new("取消回调失败");
        DisplaySwitchExecutor executor = f.Executor(factory.Invoke);
        Task<Result> execution = f.Execute(executor, f.Request(0));
        await Join(factory.Entered.Task);
        CancellationTokenRegistration registration = factory.Token.Register(() =>
        {
            try
            {
                callback.Block();
                if (throws) throw failure;
            }
            finally { callbackExited.TrySetResult(); }
        });
        try
        {
            executor.RequestStop();
            await Join(callback.Entered.Task);
            factory.Result.SetResult(factory.Candidate);
            await Join(factory.Candidate.AllEntered);
            Assert.Equal(new Result(false, false), await Join(execution));
            Assert.False(executor.Completion.IsCompleted);
            AssertLocallyRetired(factory.Candidate);
            AssertResourcesUnchanged(f);
            callback.Release();
            await Join(callbackExited.Task);
            if (throws)
            {
                AggregateException terminal = await Failure(executor.Completion);
                AggregateException callbackTree = Assert.IsType<AggregateException>(Assert.Single(terminal.InnerExceptions));
                Assert.Same(failure, Assert.Single(callbackTree.InnerExceptions));
            }
            else await Join(executor.Completion);
        }
        finally
        {
            callback.Release();
            executor.RequestStop();
            await Drain(callbackExited.Task);
            registration.Dispose();
        }
    }

    [Theory]
    [InlineData("sync-throw")]
    [InlineData("null-task")]
    [InlineData("null-producer")]
    public async Task Invalid_Factory_Result_Faults_Execution_And_Requires_Stop(string mode)
    {
        await using Fixture f = await Fixture.CreateAsync();
        InvalidOperationException failure = new("同步准备失败");
        DisplaySwitchExecutor executor = f.Executor((_, _) => mode switch
        {
            "sync-throw" => throw failure,
            "null-task" => null!,
            _ => Task.FromResult<IVideoFrameProducer>(null!),
        });
        AggregateException executionError = await Failure(f.Execute(executor, f.Request(0)));
        Exception leaf = Assert.Single(executionError.InnerExceptions);
        Assert.IsType<InvalidOperationException>(leaf);
        if (mode == "sync-throw") Assert.Same(failure, leaf);
        Assert.True(executor.StopRequired.IsCompletedSuccessfully);
        Assert.Same(executionError, Assert.Single((await Failure(executor.Completion)).InnerExceptions));
        Reject(executor, f.Request(1), Status.Unavailable);
        AssertResourcesUnchanged(f);
    }

    [Fact]
    public async Task Factory_Original_Task_Preserves_All_Exception_Branches_And_Nesting()
    {
        await using Fixture f = await Fixture.CreateAsync();
        Exception first = new InvalidOperationException("准备一"), second = new ArgumentException("准备二");
        AggregateException nested = new("原始嵌套", first);
        TaskCompletionSource<IVideoFrameProducer> original = new(TaskCreationOptions.RunContinuationsAsynchronously);
        original.SetException([nested, second]);
        _ = f.Track(original.Task);
        DisplaySwitchExecutor executor = f.Executor((_, _) => original.Task);
        AggregateException operation = await Failure(f.Execute(executor, f.Request(0)));
        AggregateException originalTree = Assert.IsType<AggregateException>(Assert.Single(operation.InnerExceptions));
        Assert.Collection(originalTree.InnerExceptions, error => Assert.Same(nested, error), error => Assert.Same(second, error));
        Assert.Same(first, Assert.Single(nested.InnerExceptions));
        AggregateException terminal = await Failure(executor.Completion);
        Assert.Same(operation, Assert.Single(terminal.InnerExceptions));
        AssertResourcesUnchanged(f);
    }

    [Theory]
    [InlineData("own-canceled", false)]
    [InlineData("foreign-canceled", true)]
    [InlineData("own-faulted-oce", true)]
    public async Task Only_True_Cancellation_With_Own_Token_Is_A_Normal_Stop(string mode, bool fails)
    {
        await using Fixture f = await Fixture.CreateAsync();
        ControlledFactory factory = f.Factory();
        using CancellationTokenSource foreign = new();
        foreign.Cancel();
        DisplaySwitchExecutor executor = f.Executor(factory.Invoke);
        Task<Result> execution = f.Execute(executor, f.Request(0));
        await Join(factory.Entered.Task);
        TaskCompletionSource cancelled = Signal();
        using CancellationTokenRegistration registration = factory.Token.Register(() => cancelled.TrySetResult());
        executor.RequestStop();
        await Join(cancelled.Task);
        OperationCanceledException? failure = null;
        if (mode == "own-faulted-oce")
        {
            failure = new OperationCanceledException(factory.Token);
            factory.Result.SetException(failure);
        }
        else factory.Result.SetCanceled(mode == "own-canceled" ? factory.Token : foreign.Token);
        if (fails)
        {
            AggregateException operation = await Failure(execution);
            Exception error = Assert.Single(operation.InnerExceptions);
            Assert.IsAssignableFrom<OperationCanceledException>(error);
            if (failure is not null) Assert.Same(failure, error);
            Assert.Same(operation, Assert.Single((await Failure(executor.Completion)).InnerExceptions));
        }
        else
        {
            Assert.Equal(new Result(false, false), await Join(execution));
            await Join(executor.Completion);
            Assert.True(factory.Result.Task.IsCanceled);
        }
        Assert.Equal(0, factory.Candidate.Stop.Calls);
        AssertResourcesUnchanged(f);
    }

    [Theory]
    [InlineData("withdraw")]
    [InlineData("control-cancel")]
    [InlineData("unregister")]
    [InlineData("lease-complete")]
    [InlineData("admission-close")]
    [InlineData("source-stop")]
    public async Task Parent_Invalidation_After_Factory_Entry_Rejects_Transfer_And_Locally_Retires(string reason)
    {
        await using Fixture f = await Fixture.CreateAsync();
        ControlledFactory factory = f.Factory();
        DisplaySwitchExecutor executor = f.Executor(factory.Invoke);
        Task<Result> execution = f.Execute(executor, f.Request(0));
        await Join(factory.Entered.Task);
        f.Invalidate(reason);
        factory.Result.SetResult(factory.Candidate);
        AggregateException operation = await Failure(execution);
        Assert.IsAssignableFrom<InvalidOperationException>(Assert.Single(operation.InnerExceptions));
        await Failure(executor.Completion);
        Assert.True(executor.StopRequired.IsCompletedSuccessfully);
        AssertLocallyRetired(factory.Candidate);
        Assert.Equal(reason == "lease-complete", f.Lease.Completed.IsCompleted);
        Assert.Equal(reason == "unregister", f.Lease.Revoked.IsCompleted);
        if (reason != "source-stop") Assert.False(f.Source.Termination.IsCompleted);
    }

    [Theory]
    [InlineData("withdraw")]
    [InlineData("control-cancel")]
    [InlineData("unregister")]
    [InlineData("lease-complete")]
    [InlineData("admission-close")]
    [InlineData("source-stop")]
    public async Task Invalid_Parent_Rejection_Does_Not_Invoke_Factory(string reason)
    {
        await using Fixture f = await Fixture.CreateAsync();
        ControlledFactory factory = f.Factory();
        DisplaySwitchExecutor executor = f.Executor(factory.Invoke);
        f.Invalidate(reason);
        Reject(executor, f.Request(0), Status.Unavailable);
        Assert.Equal(0, factory.Calls);
        Assert.False(executor.HasActiveExecution);
    }

    [Fact]
    public async Task Transferred_Pending_Replace_Is_Not_Locally_Cleaned_On_Stop_And_Needs_Outer_Source_Stop()
    {
        await using Fixture f = await Fixture.CreateAsync();
        ProbeProducer candidate = f.Producer();
        TaskCompletionSource oldLifetime = f.Pending();
        f.Initial.SetAllRoots(oldLifetime.Task);
        DisplaySwitchExecutor executor = f.Executor((_, _) => Task.FromResult<IVideoFrameProducer>(candidate));
        Task<Result> execution = f.Execute(executor, f.Request(0));
        await Join(f.Initial.AllEntered);
        Task closingExecutor = executor.DisposeAsync().AsTask();
        Assert.True(executor.StopRequired.IsCompletedSuccessfully);
        Assert.False(closingExecutor.IsCompleted);
        Assert.False(execution.IsCompleted);
        Assert.Equal(0, candidate.StartCalls);
        Assert.Equal(0, candidate.Stop.Calls);
        Assert.Equal(0, candidate.Dispose.Calls);
        Assert.Equal(0, candidate.Lifetime.Calls);
        Assert.False(f.Source.Termination.IsCompleted);
        Assert.False(f.Lease.Completed.IsCompleted);

        Task closingSource = f.Track(f.Source.DisposeAsync().AsTask());
        await Join(candidate.AllEntered);
        Assert.False(closingSource.IsCompleted);
        Assert.False(execution.IsCompleted);
        AssertLocallyRetired(candidate);
        oldLifetime.SetResult();
        await Join(closingSource);
        await Failure(execution);
        await Failure(closingExecutor);
        AssertLocallyRetired(candidate);
        Assert.False(f.Lease.Completed.IsCompleted);
    }

    [Fact]
    public async Task Transferred_Start_Failure_Preserves_Tree_Without_Local_Double_Cleanup()
    {
        await using Fixture f = await Fixture.CreateAsync();
        ProbeProducer candidate = f.Producer();
        Exception first = new InvalidOperationException("启动一"), second = new ArgumentException("启动二");
        AggregateException original = new("启动错误树", first, second);
        candidate.StartAction = () => throw original;
        DisplaySwitchExecutor executor = f.Executor((_, _) => Task.FromResult<IVideoFrameProducer>(candidate));
        AggregateException operation = await Failure(f.Execute(executor, f.Request(0)));
        AggregateException replacement = Assert.IsType<AggregateException>(Assert.Single(operation.InnerExceptions));
        Assert.Same(original, Assert.Single(replacement.InnerExceptions));
        Assert.Same(operation, Assert.Single((await Failure(executor.Completion)).InnerExceptions));
        await Failure(f.Source.Completion);
        Assert.Equal(1, candidate.StartCalls);
        Assert.Equal(1, candidate.Stop.Calls);
        Assert.Equal(1, candidate.Dispose.Calls);
        Assert.Equal(1, candidate.Lifetime.Calls);
        Assert.False(f.Lease.Completed.IsCompleted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Every_Cleanup_Synchronous_Prefix_Is_Independent_And_Fully_Joined(int blockedRoot)
    {
        await using Fixture f = await Fixture.CreateAsync();
        ControlledFactory factory = f.Factory();
        Barrier prefix = f.Gate();
        TaskCompletionSource original = f.Pending();
        factory.Candidate.Endpoints[blockedRoot].Before = prefix.Block;
        factory.Candidate.Endpoints[blockedRoot].Operation = () => original.Task;
        DisplaySwitchExecutor executor = f.Executor(factory.Invoke);
        Task<Result> execution = f.Execute(executor, f.Request(0));
        await Join(factory.Entered.Task);
        executor.RequestStop();
        factory.Result.SetResult(factory.Candidate);
        await Join(factory.Candidate.AllEntered);
        await Join(prefix.Entered.Task);
        foreach (Endpoint endpoint in factory.Candidate.Endpoints.Where((_, index) => index != blockedRoot))
            await Join(endpoint.Returned.Task);
        Assert.False(execution.IsCompleted);
        Assert.False(executor.Completion.IsCompleted);
        prefix.Release();
        await Join(factory.Candidate.Endpoints[blockedRoot].Returned.Task);
        Assert.False(original.Task.IsCompleted);
        Assert.False(execution.IsCompleted);
        Assert.False(executor.Completion.IsCompleted);
        original.SetResult();
        Assert.Equal(new Result(false, false), await Join(execution));
        await Join(executor.Completion);
        AssertLocallyRetired(factory.Candidate);
    }

    [Fact]
    public async Task Three_Distinct_Cleanup_Original_Tasks_Must_All_Complete()
    {
        await using Fixture f = await Fixture.CreateAsync();
        ControlledFactory factory = f.Factory();
        TaskCompletionSource[] originals = [f.Pending(), f.Pending(), f.Pending()];
        for (int index = 0; index < originals.Length; index++)
        {
            Task root = originals[index].Task;
            factory.Candidate.Endpoints[index].Operation = () => root;
        }
        DisplaySwitchExecutor executor = f.Executor(factory.Invoke);
        Task<Result> execution = f.Execute(executor, f.Request(0));
        await Join(factory.Entered.Task);
        executor.RequestStop();
        factory.Result.SetResult(factory.Candidate);
        await Join(factory.Candidate.AllReturned);
        Assert.False(execution.IsCompleted);
        originals[0].SetResult();
        originals[1].SetResult();
        Assert.False(execution.IsCompleted);
        Assert.False(executor.Completion.IsCompleted);
        originals[2].SetResult();
        await Join(execution);
        await Join(executor.Completion);
        AssertLocallyRetired(factory.Candidate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Cleanup_Synchronous_Error_Does_Not_Skip_Other_Notifications_Or_Join(int failedRoot)
    {
        await using Fixture f = await Fixture.CreateAsync();
        ControlledFactory factory = f.Factory();
        InvalidOperationException original = new("同步清理失败");
        TaskCompletionSource delayed = f.Pending();
        factory.Candidate.SetAllRoots(delayed.Task);
        factory.Candidate.Endpoints[failedRoot].Operation = () => throw original;
        DisplaySwitchExecutor executor = f.Executor(factory.Invoke);
        Task<Result> execution = f.Execute(executor, f.Request(0));
        await Join(factory.Entered.Task);
        executor.RequestStop();
        factory.Result.SetResult(factory.Candidate);
        await Join(factory.Candidate.AllReturned);
        Assert.False(execution.IsCompleted);
        Assert.False(executor.Completion.IsCompleted);
        delayed.SetResult();
        AggregateException error = await Failure(execution);
        Assert.Same(original, Assert.Single(error.InnerExceptions));
        Assert.Same(error, Assert.Single((await Failure(executor.Completion)).InnerExceptions));
        AssertLocallyRetired(factory.Candidate);
    }

    [Fact]
    public async Task Same_Cleanup_Original_Task_Is_Deduplicated_Without_Flattening_Its_Tree()
    {
        await using Fixture f = await Fixture.CreateAsync();
        ControlledFactory factory = f.Factory();
        Exception first = new InvalidOperationException("清理一"), second = new ArgumentException("清理二");
        AggregateException nested = new("清理嵌套", first);
        TaskCompletionSource original = f.Pending();
        factory.Candidate.SetAllRoots(original.Task);
        DisplaySwitchExecutor executor = f.Executor(factory.Invoke);
        Task<Result> execution = f.Execute(executor, f.Request(0));
        await Join(factory.Entered.Task);
        executor.RequestStop();
        factory.Result.SetResult(factory.Candidate);
        await Join(factory.Candidate.AllReturned);
        original.SetException([nested, second]);
        AggregateException error = await Failure(execution);
        AggregateException tree = Assert.IsType<AggregateException>(Assert.Single(error.InnerExceptions));
        Assert.Collection(tree.InnerExceptions, item => Assert.Same(nested, item), item => Assert.Same(second, item));
        Assert.Same(error, Assert.Single((await Failure(executor.Completion)).InnerExceptions));
        AssertLocallyRetired(factory.Candidate);
    }

    [Fact]
    public async Task Same_Exception_On_Different_Cleanup_Tasks_Is_Not_Deduplicated()
    {
        await using Fixture f = await Fixture.CreateAsync();
        ControlledFactory factory = f.Factory();
        Exception shared = new InvalidOperationException("不同原任务的同一错误实例");
        foreach (Endpoint endpoint in factory.Candidate.Endpoints)
        {
            Task original = Task.FromException(shared);
            _ = f.Track(original);
            endpoint.Operation = () => original;
        }
        DisplaySwitchExecutor executor = f.Executor(factory.Invoke);
        Task<Result> execution = f.Execute(executor, f.Request(0));
        await Join(factory.Entered.Task);
        executor.RequestStop();
        factory.Result.SetResult(factory.Candidate);
        AggregateException error = await Failure(execution);
        Assert.Equal(3, error.InnerExceptions.Count);
        Assert.All(error.InnerExceptions, item => Assert.Same(shared, item));
        await Failure(executor.Completion);
        AssertLocallyRetired(factory.Candidate);
    }

    [Fact]
    public async Task Transfer_Rejection_And_All_Cleanup_Errors_Are_Retained_Together()
    {
        await using Fixture f = await Fixture.CreateAsync();
        ControlledFactory factory = f.Factory();
        Exception[] originals = [new InvalidOperationException("停止失败"), new ArgumentException("释放失败"), new AggregateException("完成嵌套", new IOException("完成失败"))];
        for (int index = 0; index < originals.Length; index++)
        {
            Exception error = originals[index];
            Task root = Task.FromException(error);
            _ = f.Track(root);
            factory.Candidate.Endpoints[index].Operation = () => root;
        }
        DisplaySwitchExecutor executor = f.Executor(factory.Invoke);
        Task<Result> execution = f.Execute(executor, f.Request(0));
        await Join(factory.Entered.Task);
        f.Publication.Dispose();
        factory.Result.SetResult(factory.Candidate);
        AggregateException operation = await Failure(execution);
        Assert.Equal(4, operation.InnerExceptions.Count);
        Assert.IsType<InvalidOperationException>(operation.InnerExceptions[0]);
        for (int index = 0; index < originals.Length; index++) Assert.Same(originals[index], operation.InnerExceptions[index + 1]);
        Assert.Same(operation, Assert.Single((await Failure(executor.Completion)).InnerExceptions));
        AssertLocallyRetired(factory.Candidate);
    }

    [Fact]
    public async Task Cancellation_Callback_And_Execution_Errors_Are_Both_Joined_Without_Flattening()
    {
        await using Fixture f = await Fixture.CreateAsync();
        ControlledFactory factory = f.Factory();
        Exception preparing = new InvalidOperationException("准备失败"), cancellation = new ArgumentException("取消失败");
        DisplaySwitchExecutor executor = f.Executor(factory.Invoke);
        Task<Result> execution = f.Execute(executor, f.Request(0));
        await Join(factory.Entered.Task);
        using CancellationTokenRegistration registration = factory.Token.Register(() => throw cancellation);
        factory.Result.SetException(preparing);
        AggregateException operation = await Failure(execution);
        Assert.Same(preparing, Assert.Single(operation.InnerExceptions));
        AggregateException terminal = await Failure(executor.Completion);
        Assert.Equal(2, terminal.InnerExceptions.Count);
        Assert.Same(operation, terminal.InnerExceptions[0]);
        Assert.Same(cancellation, Assert.Single(Assert.IsType<AggregateException>(terminal.InnerExceptions[1]).InnerExceptions));
        AssertResourcesUnchanged(f);
    }

    private static void Reject(DisplaySwitchExecutor executor, DisplaySwitchRequestFrame request, Status expected)
    {
        Assert.Equal(expected, executor.TryExecute(request, out Task<Result>? execution));
        Assert.Null(execution);
    }

    private static void AssertLocallyRetired(ProbeProducer candidate)
    {
        Assert.Equal(0, candidate.StartCalls);
        Assert.Equal(0, candidate.ReadCalls);
        Assert.Equal(1, candidate.Stop.Calls);
        Assert.Equal(1, candidate.Dispose.Calls);
        Assert.Equal(1, candidate.Lifetime.Calls);
    }

    private static void AssertResourcesUnchanged(Fixture f)
    {
        Assert.Equal(1, f.Initial.StartCalls);
        Assert.Equal(0, f.Initial.ReadCalls);
        Assert.Equal(0, f.Initial.Stop.Calls);
        Assert.Equal(0, f.Initial.Dispose.Calls);
        Assert.Equal(0, f.Initial.Lifetime.Calls);
        Assert.Equal(1, f.Registry.ActiveSessionCount);
        Assert.False(f.Control.IsCancellationRequested);
        Assert.False(f.Source.Termination.IsCompleted);
        Assert.False(f.Source.Completion.IsCompleted);
        Assert.False(f.Lease.Completed.IsCompleted);
        Assert.False(f.Lease.Revoked.IsCompleted);
        Assert.Same(f.Lease.Completed, f.Registration.VideoCompletion);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Join(Task task) => await task.WaitAsync(Watchdog);
    private static async Task<T> Join<T>(Task<T> task) => await task.WaitAsync(Watchdog);
    private static async Task<AggregateException> Failure(Task task) =>
        await Assert.ThrowsAsync<AggregateException>(() => Join(task));

    private static async Task Drain(Task task)
    {
        // 正文Watchdog仅用于诊断；最终收尾必须等待原根真实结算，不能超时后遗弃。
        try { await task.ConfigureAwait(false); }
        catch (Exception) { /* 原Task已结算，正文负责断言其结果。 */ }
    }

    private sealed class Barrier
    {
        private readonly TaskCompletionSource _released = Signal();
        internal TaskCompletionSource Entered { get; } = Signal();
        internal void Block()
        {
            Entered.TrySetResult();
            _released.Task.GetAwaiter().GetResult();
        }
        internal void Release() => _released.TrySetResult();
    }

    private sealed class ControlledFactory
    {
        private int _calls;
        internal ControlledFactory(ProbeProducer candidate, Barrier prefix)
        {
            Candidate = candidate;
            Prefix = prefix;
        }
        internal ProbeProducer Candidate { get; }
        internal Barrier Prefix { get; }
        internal TaskCompletionSource<IVideoFrameProducer> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Entered { get; } = Signal();
        internal TaskCompletionSource Returned { get; } = Signal();
        internal DisplayInfo? Target { get; private set; }
        internal CancellationToken Token { get; private set; }
        internal int Calls => Volatile.Read(ref _calls);
        internal Task<IVideoFrameProducer> Invoke(DisplayInfo target, CancellationToken token)
        {
            Interlocked.Increment(ref _calls);
            Target = target;
            Token = token;
            Entered.TrySetResult();
            try
            {
                Prefix.Block();
                return Result.Task;
            }
            finally { Returned.TrySetResult(); }
        }
        internal void Release()
        {
            Prefix.Release();
            Result.TrySetResult(Candidate);
        }
    }

    private sealed class Endpoint
    {
        private readonly ConcurrentQueue<Task> _originals = new();
        private int _calls;
        internal Endpoint(Func<Task> operation) => Operation = operation;
        internal Func<Task> Operation { get; set; }
        internal Action? Before { get; set; }
        internal int Calls => Volatile.Read(ref _calls);
        internal TaskCompletionSource Entered { get; } = Signal();
        internal TaskCompletionSource Returned { get; } = Signal();
        internal Task Invoke()
        {
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            try
            {
                Before?.Invoke();
                Task original = Operation();
                if (original is not null) _originals.Enqueue(original);
                return original!;
            }
            finally { Returned.TrySetResult(); }
        }
        internal async Task DrainAsync()
        {
            if (Entered.Task.IsCompleted) await Drain(Returned.Task);
            foreach (Task original in _originals) await Drain(original);
        }
    }

    private sealed class ProbeProducer : IVideoFrameProducer
    {
        private readonly TaskCompletionSource _lifetime = Signal();
        private int _startCalls, _readCalls;
        internal ProbeProducer()
        {
            Stop = new Endpoint(Complete);
            Dispose = new Endpoint(Complete);
            Lifetime = new Endpoint(() => _lifetime.Task);
            Endpoints = [Stop, Dispose, Lifetime];
        }
        internal Endpoint Stop { get; }
        internal Endpoint Dispose { get; }
        internal Endpoint Lifetime { get; }
        internal Endpoint[] Endpoints { get; }
        internal Action? StartAction { get; set; }
        internal int StartCalls => Volatile.Read(ref _startCalls);
        internal int ReadCalls => Volatile.Read(ref _readCalls);
        internal Task AllEntered => Task.WhenAll(Endpoints.Select(endpoint => endpoint.Entered.Task));
        internal Task AllReturned => Task.WhenAll(Endpoints.Select(endpoint => endpoint.Returned.Task));
        internal void SetAllRoots(Task task)
        {
            foreach (Endpoint endpoint in Endpoints) endpoint.Operation = () => task;
        }
        private Task Complete()
        {
            _lifetime.TrySetResult();
            return _lifetime.Task;
        }
        internal void Release() => _lifetime.TrySetResult();
        internal async Task DrainAsync()
        {
            foreach (Endpoint endpoint in Endpoints) await endpoint.DrainAsync();
        }
        public void Start()
        {
            Interlocked.Increment(ref _startCalls);
            StartAction?.Invoke();
        }
        public ValueTask<EncodedFrame?> ReadNextAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readCalls);
            return ValueTask.FromResult<EncodedFrame?>(null);
        }
        public Task StopAsync() => Stop.Invoke();
        public ValueTask DisposeAsync() => new(Dispose.Invoke());
        public Task Completion => Lifetime.Invoke();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ConcurrentQueue<ProbeProducer> _producers = new();
        private readonly List<Barrier> _gates = [];
        private readonly List<ControlledFactory> _factories = [];
        private readonly List<TaskCompletionSource> _pending = [];
        private readonly List<DisplaySwitchExecutor> _executors = [];
        private readonly List<Task> _originals = [];
        private Fixture()
        {
            byte[] token = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
            byte[] pin = Enumerable.Range(64, 32).Select(value => (byte)value).ToArray();
            byte[] nonce = Enumerable.Range(128, 16).Select(value => (byte)value).ToArray();
            IPAddress remote = IPAddress.Parse("192.168.10.20");
            Guid session = Guid.NewGuid();
            Registration = Registry.Register(session, Guid.NewGuid(), Guid.NewGuid(), "切屏执行器接缝测试",
                SessionPermission.ViewOnly, remote, 12345, token, new VideoAttachWindow(Clock, 15_000),
                pin, Control.Token, V2AgreementFixture.Create(session));
            ConnectionSecurityContext video = new(IPAddress.Parse("192.168.10.10"), remote, 23456, SslProtocols.Tls13, pin);
            // 独立构造真实附着证明，不伪造 lease 或借用生产证明 helper。
            byte[] transcript = new byte[83];
            Encoding.UTF8.GetBytes("LANREMOTE-VIDEO-V1\0").CopyTo(transcript, 0);
            Convert.FromHexString(session.ToString("N")).CopyTo(transcript, 19);
            nonce.CopyTo(transcript, 35);
            pin.CopyTo(transcript, 51);
            using HMACSHA256 hmac = new(token);
            Assert.Equal(VideoAttachStatus.Attached, Registry.TryAttachVideo(session, video, nonce,
                hmac.ComputeHash(transcript), default, out VideoAttachLease? lease));
            Lease = lease!;
            Source = new ReplaceableVideoFrameSource(session);
            Initial = Producer();
        }
        internal static async Task<Fixture> CreateAsync()
        {
            Fixture fixture = new();
            try
            {
                await Join(fixture.Track(fixture.Source.ReplaceAsync(fixture.Initial)));
                fixture.Publication = fixture.Registry.PublishVideoSource(fixture.Lease, fixture.Source);
                fixture.Admission = fixture.Registration.CreateDisplaySwitchAdmission(fixture.Catalog);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }
        internal SessionRegistry Registry { get; } = new();
        internal SessionRegistry.SessionRegistration Registration { get; }
        internal ReplaceableVideoFrameSource Source { get; }
        internal ManualDeadlineClock Clock { get; } = new();
        internal CancellationTokenSource Control { get; } = new();
        internal VideoAttachLease Lease { get; }
        internal ProbeProducer Initial { get; }
        internal IDisposable Publication { get; private set; } = null!;
        internal Admission Admission { get; private set; } = null!;
        internal DisplayCatalogFrame Catalog { get; } = new(23, 44,
        [
            new DisplayInfo(new DisplayId(3), "主显示器", 0, 0, 1920, 1080, true),
            new DisplayInfo(new DisplayId(17), "左侧显示器", -1280, -200, 1280, 1024, false),
        ]);
        internal DisplaySwitchRequestFrame Request(long sequence, Guid? session = null, long? catalog = null, DisplayId? display = null) =>
            new(session ?? Registration.SessionId, sequence, 1234, catalog ?? Catalog.Sequence, display ?? new DisplayId(17));
        internal ProbeProducer Producer()
        {
            ProbeProducer producer = new();
            _producers.Enqueue(producer);
            return producer;
        }
        internal Barrier Gate()
        {
            Barrier gate = new();
            _gates.Add(gate);
            return gate;
        }
        internal TaskCompletionSource Pending()
        {
            TaskCompletionSource pending = Signal();
            _pending.Add(pending);
            Track(pending.Task);
            return pending;
        }
        internal ControlledFactory Factory(ProbeProducer? candidate = null, bool blockPrefix = false)
        {
            Barrier prefix = Gate();
            if (!blockPrefix) prefix.Release();
            ControlledFactory factory = new(candidate ?? Producer(), prefix);
            _factories.Add(factory);
            Track(factory.Result.Task);
            return factory;
        }
        internal T Track<T>(T original) where T : Task
        {
            _originals.Add(original);
            return original;
        }
        internal DisplaySwitchExecutor TrackExecutor(DisplaySwitchExecutor executor)
        {
            _executors.Add(executor);
            return executor;
        }
        internal DisplaySwitchExecutor Executor(Func<DisplayInfo, CancellationToken, Task<IVideoFrameProducer>> factory) =>
            TrackExecutor(Admission.CreateExecutor(factory));
        internal Task<Result> Execute(DisplaySwitchExecutor executor, DisplaySwitchRequestFrame request)
        {
            Status status = executor.TryExecute(request, out Task<Result>? execution);
            if (execution is not null) Track(execution);
            Assert.Equal(Status.Accepted, status);
            Assert.NotNull(execution);
            return execution;
        }
        internal void Invalidate(string reason)
        {
            switch (reason)
            {
                case "withdraw": Publication.Dispose(); break;
                case "control-cancel": Control.Cancel(); break;
                case "unregister": Registration.Dispose(); break;
                case "lease-complete": Lease.Complete(); break;
                case "admission-close": Admission.Dispose(); break;
                case "source-stop": Track(Source.DisposeAsync().AsTask()); break;
                default: throw new ArgumentOutOfRangeException(nameof(reason));
            }
        }
        public async ValueTask DisposeAsync()
        {
            // 先放行全部门闸，随后独立停止 source；不能先 join 仍依赖 source 的执行器。
            foreach (Barrier gate in _gates) gate.Release();
            foreach (ControlledFactory factory in _factories) factory.Release();
            foreach (TaskCompletionSource pending in _pending) pending.TrySetResult();
            foreach (ProbeProducer producer in _producers) producer.Release();
            foreach (DisplaySwitchExecutor executor in _executors) executor.RequestStop();
            Publication?.Dispose();
            Admission?.Dispose();
            Registration.Dispose();
            try
            {
                await Drain(Source.DisposeAsync().AsTask());
                foreach (Task original in _originals) await Drain(original);
                foreach (DisplaySwitchExecutor executor in _executors) await Drain(executor.Completion);
                foreach (ControlledFactory factory in _factories)
                    if (factory.Entered.Task.IsCompleted) await Drain(factory.Returned.Task);
                // 即使变异错误地提前发布 Completion，仍观察已进入的同步入口和原清理根。
                foreach (ProbeProducer producer in _producers) await producer.DrainAsync();
                Assert.Equal(0, Clock.TimerCount);
            }
            finally
            {
                if (Source.Completion.IsCompleted) Lease.Complete();
                Control.Dispose();
            }
        }
    }
}
