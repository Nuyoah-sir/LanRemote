using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Tasks.Sources;
using Xunit.Sdk;
using ReadEnd = LanRemote.Transport.ControlReadAhead.ReadEnd;
using ReadResult = LanRemote.Transport.ControlReadAhead.ReadResult;
using ReadSnapshot = LanRemote.Transport.ControlReadAhead.ReadSnapshot;

namespace LanRemote.Transport.Tests;

public sealed class ControlReadAheadTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private const int TestTimeout = 60_000;
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const byte Marker = 0xA5;

    [Fact(Timeout = TestTimeout)]
    public async Task Constructor_RejectsNull_AndDoesNotTouchBorrowedStream()
    {
        ReadFixture f = new();
        try
        {
            Assert.Equal("stream", Assert.Throws<ArgumentNullException>(() => new ControlReadAhead(null!)).ParamName);
            Assert.Null(f.Ahead.Completion);
            ReadSnapshot first = f.Ahead.Snapshot;
            AssertSnapshot(first, ReadEnd.Pending);
            Assert.NotSame(first, f.Ahead.Snapshot);
            AssertNoTake(f.Ahead);
            Assert.Equal(0, f.Stream.ReadCalls);
            f.Stream.AssertNoOtherOperations();
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Start_RegistersWorkerBeforeRead_AndWaitsForExactlyOneUncancelableOriginalRead()
    {
        ReadFixture f = new();
        try
        {
            Task completion = f.Ahead.Start();
            Assert.Same(completion, f.Ahead.Completion);
            await f.Stream.ReadEntered.Task.WaitAsync(Guard);
            await AssertAwaitPathAsync(f.Original.Task, completion);
            Assert.Same(completion, f.RegisteredAtRead);
            Assert.NotNull(f.WorkerTaskId);
            Assert.True(f.ReadOnThreadPool);
            Assert.Same(TaskScheduler.Default, f.ReadScheduler);
            f.Stream.AssertOneByteRead();
            AssertSnapshot(f.Ahead.Snapshot, ReadEnd.Pending);
            AssertNoTake(f.Ahead);
            Assert.Throws<InvalidOperationException>(() => { _ = f.Ahead.Start(); });
            Assert.Same(completion, f.Ahead.Completion);
            Assert.False(f.Original.Task.IsCompleted);
            Assert.False(completion.IsCompleted);

            f.Original.SetResult(1);
            await AssertDrainedAsync(f.Ahead);
            Assert.True(f.Original.Task.IsCompletedSuccessfully);
            AssertSnapshot(f.Ahead.Snapshot, ReadEnd.Byte);
            Assert.Throws<InvalidOperationException>(() => { _ = f.Ahead.Start(); });
            Assert.Same(completion, f.Ahead.Completion);
            f.Stream.AssertOneByteRead();
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ConcurrentStart_HasOneWinnerAndOneOriginalRead()
    {
        ReadFixture f = new();
        TaskCompletionSource release = f.NewSignal();
        TaskCompletionSource leftReady = f.NewSignal(), rightReady = f.NewSignal();
        Task<(Task? Completion, Exception? Error)> left = f.Keep(AttemptAsync(leftReady));
        Task<(Task? Completion, Exception? Error)> right = f.Keep(AttemptAsync(rightReady));
        try
        {
            await Task.WhenAll(leftReady.Task, rightReady.Task).WaitAsync(Guard);
            release.SetResult();
            var attempts = await Task.WhenAll(left, right).WaitAsync(Guard);
            var winner = Assert.Single(attempts, attempt => attempt.Error is null);
            var loser = Assert.Single(attempts, attempt => attempt.Error is not null);
            Assert.IsType<InvalidOperationException>(loser.Error);
            Assert.Null(loser.Completion);
            Task completion = Assert.IsAssignableFrom<Task>(winner.Completion);
            Assert.Same(completion, f.Ahead.Completion);
            await f.Stream.ReadEntered.Task.WaitAsync(Guard);
            await AssertAwaitPathAsync(f.Original.Task, completion);
            f.Stream.AssertOneByteRead();
            f.Original.SetResult(0);
            await AssertDrainedAsync(f.Ahead);
            Assert.Throws<InvalidOperationException>(() => { _ = f.Ahead.Start(); });
            f.Stream.AssertOneByteRead();
        }
        finally { await f.FinishAsync(); }

        Task<(Task? Completion, Exception? Error)> AttemptAsync(TaskCompletionSource ready) => Task.Run<(Task? Completion, Exception? Error)>(async () =>
        {
            ready.SetResult();
            await release.Task.ConfigureAwait(false);
            Task? completion = null;
            Exception? error = Record.Exception(() => { completion = f.Ahead.Start(); });
            return (completion, error);
        });
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(0)]
    [InlineData(255)]
    public async Task Byte_IsDeliveredOnceAsScalar_AndSnapshotsRemainImmutable(int firstByte)
    {
        ReadFixture f = new((byte)firstByte);
        try
        {
            ReadSnapshot unstarted = f.Ahead.Snapshot;
            _ = f.Ahead.Start();
            await AssertAwaitPathAsync(f.Original.Task, f.Ahead.Completion!);
            ReadSnapshot pending = f.Ahead.Snapshot;
            AssertNoTake(f.Ahead);
            f.Original.SetResult(1);
            await AssertDrainedAsync(f.Ahead);
            ReadSnapshot available = f.Ahead.Snapshot;
            AssertSnapshot(available, ReadEnd.Byte);
            Assert.Equal(0, f.Stream.Borrowed.Span[0]);

            // 故意保留流拿到的 Memory；修改它不能改变已经保存的首字节标量。
            f.Stream.Borrowed.Span[0] = (byte)(firstByte ^ 0xFF);
            Assert.True(f.Ahead.TryTake(out ReadResult? result));
            Assert.NotNull(result);
            Assert.Equal((byte?)firstByte, result.FirstByte);
            ReadSnapshot taken = f.Ahead.Snapshot;
            AssertSnapshot(taken, ReadEnd.Byte, taken: true);
            AssertNoTake(f.Ahead);
            f.Stream.Borrowed.Span[0] = Marker;
            f.Ahead.RequestStop();
            Assert.Equal((byte?)firstByte, result.FirstByte);
            AssertNoTake(f.Ahead);

            AssertSnapshot(unstarted, ReadEnd.Pending);
            AssertSnapshot(pending, ReadEnd.Pending);
            AssertSnapshot(available, ReadEnd.Byte);
            AssertSnapshot(taken, ReadEnd.Byte, taken: true);
            AssertSnapshot(f.Ahead.Snapshot, ReadEnd.Byte, stop: true, taken: true);
            Assert.NotSame(unstarted, pending);
            Assert.NotSame(pending, available);
            Assert.NotSame(available, taken);
            Assert.NotSame(taken, f.Ahead.Snapshot);
            Assert.NotSame(f.Ahead.Snapshot, f.Ahead.Snapshot);
            f.Stream.AssertOneByteRead();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(0, 165)]
    [InlineData(1, 0)]
    [InlineData(1, 255)]
    public async Task SynchronouslyCompletedValueTask_StillRegistersClearsAndDeliversOnce(int count, int firstByte)
    {
        ReadFixture f = new((byte)firstByte, (_, _) => new ValueTask<int>(count));
        try
        {
            Task completion = f.Ahead.Start();
            await AssertDrainedAsync(f.Ahead);
            Assert.Same(completion, f.RegisteredAtRead);
            Assert.NotNull(f.WorkerTaskId);
            AssertSnapshot(f.Ahead.Snapshot, count == 0 ? ReadEnd.EndOfStream : ReadEnd.Byte);
            Assert.Equal(0, f.Stream.Borrowed.Span[0]);
            f.Stream.Borrowed.Span[0] = (byte)(firstByte ^ 0xFF);
            Assert.True(f.Ahead.TryTake(out ReadResult? result));
            Assert.NotNull(result);
            Assert.Equal(count == 0 ? (byte?)null : (byte)firstByte, result.FirstByte);
            AssertNoTake(f.Ahead);
            f.Stream.AssertOneByteRead();
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task EndOfStream_IsOneSuccessfulTakeWithNull_NotAZeroByte()
    {
        ReadFixture f = new();
        try
        {
            _ = f.Ahead.Start();
            await AssertAwaitPathAsync(f.Original.Task, f.Ahead.Completion!);
            Assert.Equal(Marker, f.Stream.Borrowed.Span[0]);
            f.Original.SetResult(0);
            await AssertDrainedAsync(f.Ahead);
            AssertSnapshot(f.Ahead.Snapshot, ReadEnd.EndOfStream);
            Assert.Equal(0, f.Stream.Borrowed.Span[0]);
            Assert.True(f.Ahead.TryTake(out ReadResult? result));
            Assert.NotNull(result);
            Assert.Null(result.FirstByte);
            AssertNoTake(f.Ahead);
            AssertSnapshot(f.Ahead.Snapshot, ReadEnd.EndOfStream, taken: true);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ConcurrentTake_HasExactlyOneByteOrEofWinner(int count)
    {
        ReadFixture f = new();
        TaskCompletionSource release = f.NewSignal();
        TaskCompletionSource leftReady = f.NewSignal(), rightReady = f.NewSignal();
        try
        {
            _ = f.Ahead.Start();
            await AssertAwaitPathAsync(f.Original.Task, f.Ahead.Completion!);
            f.Original.SetResult(count);
            await AssertDrainedAsync(f.Ahead);
            Task<(bool Taken, ReadResult? Result)> left = f.Keep(TakeAsync(leftReady));
            Task<(bool Taken, ReadResult? Result)> right = f.Keep(TakeAsync(rightReady));
            await Task.WhenAll(leftReady.Task, rightReady.Task).WaitAsync(Guard);
            release.SetResult();
            var attempts = await Task.WhenAll(left, right).WaitAsync(Guard);
            var winner = Assert.Single(attempts, attempt => attempt.Taken);
            var loser = Assert.Single(attempts, attempt => !attempt.Taken);
            Assert.NotNull(winner.Result);
            Assert.Equal(count == 0 ? (byte?)null : Marker, winner.Result.FirstByte);
            Assert.Null(loser.Result);
            AssertNoTake(f.Ahead);
            AssertSnapshot(f.Ahead.Snapshot, count == 0 ? ReadEnd.EndOfStream : ReadEnd.Byte, taken: true);
        }
        finally { await f.FinishAsync(); }

        Task<(bool Taken, ReadResult? Result)> TakeAsync(TaskCompletionSource ready) => Task.Run(async () =>
        {
            ready.SetResult();
            await release.Task.ConfigureAwait(false);
            bool taken = f.Ahead.TryTake(out ReadResult? result);
            return (taken, result);
        });
    }

    [Fact(Timeout = TestTimeout)]
    public async Task StopBeforeStart_IsIdempotentAndSkipsAllStreamAccess()
    {
        ReadFixture f = new();
        try
        {
            f.Ahead.RequestStop();
            f.Ahead.RequestStop();
            ReadSnapshot stopped = f.Ahead.Snapshot;
            AssertSnapshot(stopped, ReadEnd.Pending, stop: true);
            Assert.Null(f.Ahead.Completion);
            AssertNoTake(f.Ahead);
            Assert.Equal(0, f.Stream.ReadCalls);
            _ = f.Ahead.Start();
            await AssertDrainedAsync(f.Ahead);
            AssertSnapshot(f.Ahead.Snapshot, ReadEnd.SkippedLocalStop, stop: true);
            AssertSnapshot(stopped, ReadEnd.Pending, stop: true);
            AssertNoTake(f.Ahead);
            Assert.Throws<InvalidOperationException>(() => { _ = f.Ahead.Start(); });
            Assert.Equal(0, f.Stream.ReadCalls);
            f.Stream.AssertNoOtherOperations();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task StopPending_DoesNotCancelCloseClearOrCompleteOriginal_AndSuppressesLateResult(int count)
    {
        ReadFixture f = new();
        try
        {
            Task completion = f.Ahead.Start();
            await AssertAwaitPathAsync(f.Original.Task, completion);
            ReadSnapshot beforeStop = f.Ahead.Snapshot;
            f.Ahead.RequestStop();
            f.Ahead.RequestStop();
            AssertSnapshot(f.Ahead.Snapshot, ReadEnd.Pending, stop: true);
            AssertSnapshot(beforeStop, ReadEnd.Pending);
            AssertNoTake(f.Ahead);
            f.Stream.AssertOneByteRead();
            f.Stream.AssertNoOtherOperations();
            Assert.False(f.Stream.ReadToken.IsCancellationRequested);
            Assert.Equal(0, f.Stream.CancellationCallbacks);
            Assert.False(f.Original.Task.IsCompleted);
            Assert.False(completion.IsCompleted);
            Assert.Same(completion, f.Ahead.Completion);
            Assert.Equal(Marker, f.Stream.Borrowed.Span[0]);
            await AssertAwaitPathAsync(f.Original.Task, completion);

            f.Original.SetResult(count);
            await AssertDrainedAsync(f.Ahead);
            AssertSnapshot(f.Ahead.Snapshot, count == 0 ? ReadEnd.EndOfStream : ReadEnd.Byte, stop: true);
            AssertNoTake(f.Ahead);
            Assert.Equal(0, f.Stream.Borrowed.Span[0]);
            Assert.Equal(0, f.Stream.CancellationCallbacks);
            f.Stream.AssertOneByteRead();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task StopAfterReadBeforeTake_SuppressesAlreadyAvailableByteOrEof(int count)
    {
        ReadFixture f = new();
        try
        {
            _ = f.Ahead.Start();
            await AssertAwaitPathAsync(f.Original.Task, f.Ahead.Completion!);
            f.Original.SetResult(count);
            await AssertDrainedAsync(f.Ahead);
            ReadEnd end = count == 0 ? ReadEnd.EndOfStream : ReadEnd.Byte;
            ReadSnapshot beforeStop = f.Ahead.Snapshot;
            AssertSnapshot(beforeStop, end);
            f.Ahead.RequestStop();
            AssertNoTake(f.Ahead);
            AssertSnapshot(beforeStop, end);
            AssertSnapshot(f.Ahead.Snapshot, end, stop: true);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("single", "none")]
    [InlineData("single", "before")]
    [InlineData("single", "race")]
    [InlineData("nested", "none")]
    [InlineData("nested", "before")]
    [InlineData("nested", "race")]
    [InlineData("siblings", "none")]
    [InlineData("siblings", "before")]
    [InlineData("siblings", "race")]
    [InlineData("repeated", "none")]
    [InlineData("repeated", "before")]
    [InlineData("repeated", "race")]
    [InlineData("disposed", "none")]
    [InlineData("disposed", "before")]
    [InlineData("disposed", "race")]
    public async Task OriginalTaskFaults_PreserveRootAndNestedReferences_EvenWhenRacingStop(string shape, string stopMode)
    {
        ReadFixture f = new();
        bool stopRequested = stopMode != "none";
        IOException leaf = new("首项失败");
        OperationCanceledException nestedCancellation = new("嵌套取消并非原任务取消");
        InvalidOperationException nestedLeaf = new("嵌套失败");
        AggregateException nested = new("嵌套分支", nestedCancellation, nestedLeaf);
        IOException sibling = new("同级失败");
        ObjectDisposedException disposed = new("borrowed-stream", "原读迟到释放失败不能按类型吞掉");
        Exception[] roots = shape switch
        {
            "single" => [leaf],
            "nested" => [nested],
            "repeated" => [leaf, nested, leaf],
            "disposed" => [disposed],
            _ => [leaf, nested, sibling]
        };
        try
        {
            _ = f.Ahead.Start();
            await AssertAwaitPathAsync(f.Original.Task, f.Ahead.Completion!);
            ReadSnapshot pending = f.Ahead.Snapshot;
            if (stopMode == "race")
            {
                TaskCompletionSource release = f.NewSignal();
                TaskCompletionSource stopReady = f.NewSignal(), faultReady = f.NewSignal();
                Task stop = f.Keep(Task.Run(async () =>
                {
                    stopReady.SetResult();
                    await release.Task.ConfigureAwait(false);
                    f.Ahead.RequestStop();
                }));
                Task fault = f.Keep(Task.Run(async () =>
                {
                    faultReady.SetResult();
                    await release.Task.ConfigureAwait(false);
                    f.Original.SetException(roots);
                }));
                await Task.WhenAll(stopReady.Task, faultReady.Task).WaitAsync(Guard);
                release.SetResult();
                await Task.WhenAll(stop, fault).WaitAsync(Guard);
            }
            else
            {
                if (stopMode == "before") f.Ahead.RequestStop();
                f.Original.SetException(roots);
            }

            await AssertDrainedAsync(f.Ahead);
            Assert.True(f.Original.Task.IsFaulted);
            Exception saved = Assert.IsAssignableFrom<Exception>(f.Ahead.Snapshot.Error);
            if (roots.Length == 1) Assert.Same(roots[0], saved);
            else
            {
                AggregateException aggregate = Assert.IsType<AggregateException>(saved);
                Assert.Equal(roots.Length, aggregate.InnerExceptions.Count);
                AggregateException originalTree = Assert.IsType<AggregateException>(f.Original.Task.Exception);
                for (int i = 0; i < roots.Length; i++)
                {
                    Assert.Same(roots[i], originalTree.InnerExceptions[i]);
                    Assert.Same(originalTree.InnerExceptions[i], aggregate.InnerExceptions[i]);
                }
                Assert.Same(nested, aggregate.InnerExceptions[1]);
            }
            Assert.Same(nestedCancellation, nested.InnerExceptions[0]);
            Assert.Same(nestedLeaf, nested.InnerExceptions[1]);
            Assert.Equal(2, nested.InnerExceptions.Count);
            AssertSnapshot(f.Ahead.Snapshot, ReadEnd.Faulted, stop: stopRequested, error: saved);
            AssertSnapshot(pending, ReadEnd.Pending);
            AssertNoTake(f.Ahead);
            Assert.Equal(0, f.Stream.Borrowed.Span[0]);
            ReadSnapshot failed = f.Ahead.Snapshot;
            f.Ahead.RequestStop();
            AssertSnapshot(failed, ReadEnd.Faulted, stop: stopRequested, error: saved);
            AssertSnapshot(f.Ahead.Snapshot, ReadEnd.Faulted, stop: true, error: saved);
            Assert.Same(saved, f.Ahead.Snapshot.Error);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledOriginalTask_IsRecordedAsError_NotCanceledCompletion(bool stop)
    {
        ReadFixture f = new();
        using CancellationTokenSource source = new();
        try
        {
            _ = f.Ahead.Start();
            await AssertAwaitPathAsync(f.Original.Task, f.Ahead.Completion!);
            if (stop) f.Ahead.RequestStop();
            source.Cancel();
            f.Original.SetCanceled(source.Token);
            await AssertDrainedAsync(f.Ahead);
            Assert.True(f.Original.Task.IsCanceled);
            Assert.Null(f.Original.Task.Exception);
            TaskCanceledException error = Assert.IsType<TaskCanceledException>(f.Ahead.Snapshot.Error);
            Assert.Equal(source.Token, error.CancellationToken);
            Assert.Same(f.Original.Task, error.Task);
            AssertSnapshot(f.Ahead.Snapshot, ReadEnd.Faulted, stop: stop, error: error);
            AssertNoTake(f.Ahead);
            Assert.Equal(0, f.Stream.Borrowed.Span[0]);
            f.Stream.AssertOneByteRead();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task InvalidReadCount_IsFaultedWithoutDelivery_AndClearsReturnedBuffer(int count)
    {
        ReadFixture f = new();
        try
        {
            _ = f.Ahead.Start();
            await AssertAwaitPathAsync(f.Original.Task, f.Ahead.Completion!);
            f.Original.SetResult(count);
            await AssertDrainedAsync(f.Ahead);
            FrameProtocolException error = Assert.IsType<FrameProtocolException>(f.Ahead.Snapshot.Error);
            Assert.Equal("control-read-invalid-count", error.Reason);
            AssertSnapshot(f.Ahead.Snapshot, ReadEnd.Faulted, error: error);
            AssertNoTake(f.Ahead);
            Assert.Equal(0, f.Stream.Borrowed.Span[0]);
            f.Stream.AssertOneByteRead();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SynchronousReadOrAsTaskFailure_PreservesExceptionAndDoesNotClearUnknownBorrow(bool failAsTask)
    {
        AggregateException error = new("同步失败", new IOException("原始分支"),
            new AggregateException(new InvalidOperationException("嵌套原始分支")));
        ThrowingStatusSource source = new(error);
        ReadFixture f = new(read: (_, _) => failAsTask ? new ValueTask<int>(source, 0) : throw error);
        try
        {
            Task completion = f.Ahead.Start();
            await AssertDrainedAsync(f.Ahead);
            Assert.Same(completion, f.RegisteredAtRead);
            Assert.NotNull(f.WorkerTaskId);
            AssertSnapshot(f.Ahead.Snapshot, ReadEnd.Faulted, error: error);
            AssertNoTake(f.Ahead);
            Assert.Equal(failAsTask ? 1 : 0, source.StatusCalls);
            Assert.Equal(0, source.GetResultCalls);
            Assert.Equal(0, source.OnCompletedCalls);
            // 未取得原 Task 时无法证明任意流的隐藏 I/O 已退出，不能抢先清写借用区。
            Assert.Equal(Marker, f.Stream.Borrowed.Span[0]);
            f.Stream.AssertOneByteRead();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockedSynchronousPrefix_DoesNotHoldGate_AndCompletionRemainsRegistered(bool blockAsTask)
    {
        IOException statusError = new("受闸控制的 AsTask 失败");
        SynchronousPause? pause = null;
        ThrowingStatusSource source = new(statusError, () => pause!.Wait());
        ReadFixture f = new(read: (fixture, _) =>
        {
            if (blockAsTask) return new ValueTask<int>(source, 0);
            pause!.Wait();
            return new ValueTask<int>(fixture.Original.Task);
        });
        pause = f.NewPause();
        Task? registeredBeforePause = null;
        int? callerTaskId = null;
        Task<Task> startCall = f.Keep(Task.Factory.StartNew(() =>
        {
            callerTaskId = Task.CurrentId;
            Task completion = f.Ahead.Start();
            registeredBeforePause = f.Ahead.Completion;
            return completion;
        }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default));
        try
        {
            await pause.Reached.Task.WaitAsync(Guard);
            // Start 调用已返回，但原读取同步前缀仍然受闸阻塞。
            Task completion = await startCall.WaitAsync(Guard);
            Assert.Same(completion, registeredBeforePause);
            Assert.Same(completion, f.RegisteredAtRead);
            Assert.NotNull(f.WorkerTaskId);
            Assert.NotEqual(callerTaskId, f.WorkerTaskId);
            Assert.False(completion.IsCompleted);

            Task<ReadSnapshot> snapshotCall = f.Keep(Task.Run(() => f.Ahead.Snapshot));
            ReadSnapshot pending = await snapshotCall.WaitAsync(Guard);
            AssertSnapshot(pending, ReadEnd.Pending);
            Task<Exception?> repeatCall = f.Keep(Task.Run<Exception?>(() => Record.Exception(() => { _ = f.Ahead.Start(); })));
            Assert.IsType<InvalidOperationException>(await repeatCall.WaitAsync(Guard));
            Task stopCall = f.Keep(Task.Run(f.Ahead.RequestStop));
            await stopCall.WaitAsync(Guard);
            Task<ReadSnapshot> stoppedCall = f.Keep(Task.Run(() => f.Ahead.Snapshot));
            AssertSnapshot(await stoppedCall.WaitAsync(Guard), ReadEnd.Pending, stop: true);
            Task<Task?> registeredCall = f.Keep(Task.Run(() => Task.FromResult(f.Ahead.Completion)));
            Assert.Same(completion, await registeredCall.WaitAsync(Guard));
            Task<bool> takeCall = f.Keep(Task.Run(() => f.Ahead.TryTake(out _)));
            Assert.False(await takeCall.WaitAsync(Guard));
            Assert.False(completion.IsCompleted);
            Assert.Equal(Marker, f.Stream.Borrowed.Span[0]);
            f.Stream.AssertNoOtherOperations();
            pause.Open();

            if (!blockAsTask)
            {
                await AssertAwaitPathAsync(f.Original.Task, completion);
                Assert.False(completion.IsCompleted);
                Assert.Equal(Marker, f.Stream.Borrowed.Span[0]);
                f.Original.SetResult(1);
            }
            await AssertDrainedAsync(f.Ahead);
            AssertSnapshot(f.Ahead.Snapshot, blockAsTask ? ReadEnd.Faulted : ReadEnd.Byte,
                stop: true, error: blockAsTask ? statusError : null);
            AssertNoTake(f.Ahead);
            Assert.Equal(blockAsTask ? Marker : 0, f.Stream.Borrowed.Span[0]);
            AssertSnapshot(pending, ReadEnd.Pending);
            f.Stream.AssertOneByteRead();
        }
        finally { await f.FinishAsync(); }
    }

    private static void AssertSnapshot(ReadSnapshot snapshot, ReadEnd end, bool stop = false,
        bool taken = false, Exception? error = null)
    {
        Assert.Equal(end, snapshot.End);
        Assert.Equal(stop, snapshot.StopRequested);
        Assert.Equal(taken, snapshot.Taken);
        Assert.Same(error, snapshot.Error);
    }

    private static void AssertNoTake(ControlReadAhead ahead)
    {
        Assert.False(ahead.TryTake(out ReadResult? result));
        Assert.Null(result);
    }

    private static async Task AssertDrainedAsync(ControlReadAhead ahead)
    {
        Task completion = Assert.IsAssignableFrom<Task>(ahead.Completion);
        await completion.WaitAsync(Guard);
        Assert.Equal(TaskStatus.RanToCompletion, completion.Status);
        Assert.Null(completion.Exception);
        Assert.Same(completion, ahead.Completion);
    }

    // 只检查真实原 Task -> await 状态机 -> Task.Run unwrap 链；不把测试信号当作排空证据。
    // 与既有 monitor 测试相同，运行时字段布局变化必须显式失败，不能静默放过检查。
    private static async Task AssertAwaitPathAsync(Task original, Task completion)
    {
        using CancellationTokenSource guard = new(Guard);
        while (!HasAwaitPath(original, completion, new HashSet<Task>()))
        {
            Assert.False(original.IsCompleted, "原读取提前退出，无法证明受闸控制的等待。");
            Assert.False(completion.IsCompleted, "Completion 提前退出，没有排空原读取。");
            if (guard.IsCancellationRequested)
                throw new XunitException("Guard：未找到原读取到 Completion 的真实 await 链。");
            await Task.Yield();
        }
    }

    private static bool HasAwaitPath(Task current, Task target, HashSet<Task> visited)
    {
        if (!visited.Add(current)) return false;
        foreach (object continuation in Continuations(current))
        {
            object? box = continuation is Delegate action ? action.Target : continuation;
            if (box is not Task && box is not null && RuntimeField(box.GetType(), "m_action")?.GetValue(box) is Delegate scheduled)
                box = scheduled.Target;
            if (box is not Task next) continue;
            object? machine = RuntimeField(next.GetType(), "StateMachine")?.GetValue(next);
            bool exactAwait = machine is not null && machine.GetType().GetFields(Fields)
                .Where(field => field.Name.StartsWith("<>u__", StringComparison.Ordinal))
                .Select(field => field.GetValue(machine)).OfType<object>()
                .Any(awaiter => awaiter.GetType().GetFields(Fields)
                    .Any(field => ReferenceEquals(field.GetValue(awaiter), current)));
            bool unwrap = (next.GetType().FullName ?? "").StartsWith("System.Threading.Tasks.UnwrapPromise`", StringComparison.Ordinal);
            if (!exactAwait && !unwrap) continue;
            if (ReferenceEquals(next, target) || HasAwaitPath(next, target, visited)) return true;
        }
        return false;
    }

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

    private sealed class ReadFixture
    {
        private readonly List<TaskCompletionSource> _signals = [];
        private readonly List<SynchronousPause> _pauses = [];
        private readonly List<Task> _tasks = [];
        internal TaskCompletionSource<int> Original { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ProbeStream Stream { get; }
        internal ControlReadAhead Ahead { get; }
        internal Task? RegisteredAtRead { get; private set; }
        internal int? WorkerTaskId { get; private set; }
        internal bool ReadOnThreadPool { get; private set; }
        internal TaskScheduler? ReadScheduler { get; private set; }

        internal ReadFixture(byte value = Marker, Func<ReadFixture, Memory<byte>, ValueTask<int>>? read = null)
        {
            Stream = new ProbeStream((memory, _) =>
            {
                RegisteredAtRead = Ahead!.Completion;
                WorkerTaskId = Task.CurrentId;
                ReadOnThreadPool = Thread.CurrentThread.IsThreadPoolThread;
                ReadScheduler = TaskScheduler.Current;
                if (!memory.IsEmpty) memory.Span[0] = value;
                return read is null ? new ValueTask<int>(Original.Task) : read(this, memory);
            });
            Ahead = new ControlReadAhead(Stream);
        }

        internal TaskCompletionSource NewSignal()
        {
            TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _signals.Add(signal);
            return signal;
        }

        internal SynchronousPause NewPause()
        {
            SynchronousPause pause = new();
            _pauses.Add(pause);
            return pause;
        }

        internal T Keep<T>(T task) where T : Task
        {
            _tasks.Add(task);
            return task;
        }

        internal async Task FinishAsync()
        {
            // 必须先释放所有同步/异步闸，再等待调用者和最后登记的 Completion。
            foreach (SynchronousPause pause in _pauses) pause.Open();
            foreach (TaskCompletionSource signal in _signals) signal.TrySetResult();
            Ahead.RequestStop();
            Original.TrySetResult(0);
            try
            {
                try { await Task.WhenAll(_tasks).WaitAsync(Guard); }
                finally
                {
                    if (Ahead.Completion is { } completion) await completion.WaitAsync(Guard);
                }
            }
            finally
            {
                foreach (SynchronousPause pause in _pauses) pause.Dispose();
                Stream.ReleaseRegistrations();
                Stream.AssertNoOtherOperations();
            }
        }
    }

    private sealed class SynchronousPause : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Wait()
        {
            Reached.TrySetResult();
            if (!_release.Wait(Guard)) throw new XunitException("Guard：同步前缀未被释放。");
        }
        internal void Open() => _release.Set();
        public void Dispose() => _release.Dispose();
    }

    private sealed class ThrowingStatusSource(Exception error, Action? beforeStatus = null) : IValueTaskSource<int>
    {
        private int _statusCalls, _getResultCalls, _onCompletedCalls;
        internal int StatusCalls => Volatile.Read(ref _statusCalls);
        internal int GetResultCalls => Volatile.Read(ref _getResultCalls);
        internal int OnCompletedCalls => Volatile.Read(ref _onCompletedCalls);
        public ValueTaskSourceStatus GetStatus(short token)
        {
            Interlocked.Increment(ref _statusCalls);
            beforeStatus?.Invoke();
            throw error;
        }
        public int GetResult(short token)
        {
            Interlocked.Increment(ref _getResultCalls);
            throw new XunitException("GetStatus 已抛错，不应调用 GetResult。");
        }
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            Interlocked.Increment(ref _onCompletedCalls);
            throw new XunitException("GetStatus 已抛错，不应登记 OnCompleted。");
        }
    }

    // 直接包装指定读取委托，不派生 MemoryStream，避免基类转发造成双层读计数。
    private sealed class ProbeStream(Func<Memory<byte>, CancellationToken, ValueTask<int>> read) : Stream
    {
        private readonly ConcurrentQueue<string> _otherOperations = new();
        private readonly ConcurrentQueue<CancellationTokenRegistration> _registrations = new();
        private int _readCalls, _cancellationCallbacks;
        internal int ReadCalls => Volatile.Read(ref _readCalls);
        internal int CancellationCallbacks => Volatile.Read(ref _cancellationCallbacks);
        internal Memory<byte> Borrowed { get; private set; }
        internal CancellationToken ReadToken { get; private set; }
        internal TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readCalls);
            Borrowed = buffer;
            ReadToken = cancellationToken;
            _registrations.Enqueue(cancellationToken.Register(() => Interlocked.Increment(ref _cancellationCallbacks)));
            ReadEntered.TrySetResult();
            return read(buffer, cancellationToken);
        }

        internal void AssertOneByteRead()
        {
            Assert.Equal(1, ReadCalls);
            Assert.Equal(1, Borrowed.Length);
            Assert.Equal(CancellationToken.None, ReadToken);
            Assert.False(ReadToken.CanBeCanceled);
            Assert.Equal(0, CancellationCallbacks);
            AssertNoOtherOperations();
        }
        internal void AssertNoOtherOperations() => Assert.Empty(_otherOperations);
        internal void ReleaseRegistrations()
        {
            while (_registrations.TryDequeue(out CancellationTokenRegistration registration)) registration.Dispose();
        }
        private Exception Unexpected(string operation)
        {
            _otherOperations.Enqueue(operation);
            return new XunitException($"借用流不应执行 {operation}。");
        }
        public override bool CanRead => throw Unexpected(nameof(CanRead));
        public override bool CanWrite => throw Unexpected(nameof(CanWrite));
        public override bool CanSeek => throw Unexpected(nameof(CanSeek));
        public override bool CanTimeout => throw Unexpected(nameof(CanTimeout));
        public override long Length => throw Unexpected(nameof(Length));
        public override long Position
        {
            get => throw Unexpected("Position.get");
            set => throw Unexpected("Position.set");
        }
        public override int ReadTimeout
        {
            get => throw Unexpected("ReadTimeout.get");
            set => throw Unexpected("ReadTimeout.set");
        }
        public override int WriteTimeout
        {
            get => throw Unexpected("WriteTimeout.get");
            set => throw Unexpected("WriteTimeout.set");
        }
        public override int Read(byte[] buffer, int offset, int count) => throw Unexpected("Read(byte[])");
        public override int Read(Span<byte> buffer) => throw Unexpected("Read(Span)");
        public override int ReadByte() => throw Unexpected(nameof(ReadByte));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw Unexpected("ReadAsync(byte[])");
        public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) => throw Unexpected(nameof(BeginRead));
        public override int EndRead(IAsyncResult asyncResult) => throw Unexpected(nameof(EndRead));
        public override void Write(byte[] buffer, int offset, int count) => throw Unexpected("Write(byte[])");
        public override void Write(ReadOnlySpan<byte> buffer) => throw Unexpected("Write(Span)");
        public override void WriteByte(byte value) => throw Unexpected(nameof(WriteByte));
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw Unexpected("WriteAsync(byte[])");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => throw Unexpected("WriteAsync(Memory)");
        public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) => throw Unexpected(nameof(BeginWrite));
        public override void EndWrite(IAsyncResult asyncResult) => throw Unexpected(nameof(EndWrite));
        public override void Flush() => throw Unexpected(nameof(Flush));
        public override Task FlushAsync(CancellationToken cancellationToken) => throw Unexpected(nameof(FlushAsync));
        public override long Seek(long offset, SeekOrigin origin) => throw Unexpected(nameof(Seek));
        public override void SetLength(long value) => throw Unexpected(nameof(SetLength));
        public override void Close() => _otherOperations.Enqueue(nameof(Close));
        protected override void Dispose(bool disposing) => _otherOperations.Enqueue(nameof(Dispose));
        public override ValueTask DisposeAsync()
        {
            _otherOperations.Enqueue(nameof(DisposeAsync));
            return ValueTask.CompletedTask;
        }
    }
}
