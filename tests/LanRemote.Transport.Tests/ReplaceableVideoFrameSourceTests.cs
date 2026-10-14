using System.Buffers.Binary;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;

namespace LanRemote.Transport.Tests;

public sealed class ReplaceableVideoFrameSourceTests
{
    // 契约局限：候选必须专用且未 Start；没有替换取消/超时代理，也不是发送或显示屏障。
    // producer/owner 回调不得等待来源操作或 Completion；本组测试不引入自 join 的非法依赖。
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

    [Fact]
    public void Empty_Session_Is_Rejected()
    {
        Assert.Throws<ArgumentException>(() => new ReplaceableVideoFrameSource(Guid.Empty));
    }

    [Fact]
    public async Task Initial_Read_Waits_For_Installation_And_Transfers_The_Frame()
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = Frame(owner);
        Producer producer = new() { Read = _ => ValueTask.FromResult<EncodedFrame?>(frame) };
        Task<EncodedFrame?> reading = source.ReadNextAsync(session, default).AsTask();
        try
        {
            Assert.False(reading.IsCompleted);
            Assert.False(source.Completion.IsCompleted);
            await source.ReplaceAsync(producer).WaitAsync(Guard);
            Assert.Same(frame, await reading.WaitAsync(Guard));
            Assert.Equal(1, producer.StartCalls);
            Assert.Equal(1, producer.ReadCalls);
            Assert.Equal(0, owner.DisposeCalls);
            Assert.False(source.Completion.IsCompleted);
        }
        finally
        {
            await FinishAsync(source, false, reading);
        }
        Assert.Equal(0, owner.DisposeCalls);
        AssertRetiredOnce(producer);
    }

    [Fact]
    public async Task Empty_Dispose_Unblocks_Read_And_All_Dispose_Calls_Return_Stable_Completion()
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        Task completion = source.Completion;
        Task<EncodedFrame?> reading = source.ReadNextAsync(session, default).AsTask();
        try
        {
            Assert.False(reading.IsCompleted);
            Assert.Same(completion, source.Completion);
            Assert.Same(completion, source.DisposeAsync().AsTask());
            Assert.Same(completion, source.DisposeAsync().AsTask());
            Assert.Null(await reading.WaitAsync(Guard));
            await completion.WaitAsync(Guard);
            Assert.Null(await source.ReadNextAsync(session, default).AsTask().WaitAsync(Guard));
            Assert.Same(completion, source.DisposeAsync().AsTask());
        }
        finally
        {
            await FinishAsync(source, false, reading);
        }
    }

    [Fact]
    public async Task Empty_Read_Cancellation_Does_Not_Close_The_Source()
    {
        Guid session = Guid.NewGuid();
        using CancellationTokenSource caller = new();
        ReplaceableVideoFrameSource source = new(session);
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = Frame(owner);
        Producer producer = new() { Read = _ => ValueTask.FromResult<EncodedFrame?>(frame) };
        Task<EncodedFrame?> reading = source.ReadNextAsync(session, caller.Token).AsTask();
        try
        {
            caller.Cancel();
            OperationCanceledException error = Assert.IsAssignableFrom<OperationCanceledException>(await ObserveAsync(reading));
            Assert.Equal(caller.Token, error.CancellationToken);
            Assert.True(reading.IsCanceled);
            Assert.False(source.Completion.IsCompleted);
            await source.ReplaceAsync(producer).WaitAsync(Guard);
            Assert.Same(frame, await source.ReadNextAsync(session, default).AsTask().WaitAsync(Guard));
        }
        finally
        {
            caller.Cancel();
            await FinishAsync(source, false, reading);
        }
    }

    [Fact]
    public async Task Consecutive_Installations_Retire_Each_Previous_Generation_Exactly_Once()
    {
        ReplaceableVideoFrameSource source = new(Guid.NewGuid());
        Producer first = new();
        Producer second = new();
        Producer third = new();
        second.OnStart = () => AssertRetiredOnce(first);
        third.OnStart = () => AssertRetiredOnce(second);
        try
        {
            await source.ReplaceAsync(first).WaitAsync(Guard);
            await source.ReplaceAsync(second).WaitAsync(Guard);
            await source.ReplaceAsync(third).WaitAsync(Guard);
            Assert.Equal(1, first.StartCalls);
            Assert.Equal(1, second.StartCalls);
            Assert.Equal(1, third.StartCalls);
            Assert.Equal(0, third.StopCalls);
            Assert.Equal(0, third.DisposeCalls);
            Assert.False(source.Completion.IsCompleted);
        }
        finally
        {
            await FinishAsync(source, false);
        }
        AssertRetiredOnce(first);
        AssertRetiredOnce(second);
        AssertRetiredOnce(third);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Foreign_Session_Is_Rejected_Synchronously_Without_Reading(bool install)
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        Producer producer = new();
        try
        {
            if (install) await source.ReplaceAsync(producer).WaitAsync(Guard);
            Assert.Throws<ArgumentException>(() => { _ = source.ReadNextAsync(Guid.NewGuid(), default); });
            Assert.Equal(0, producer.ReadCalls);
            Assert.False(source.Completion.IsCompleted);
        }
        finally
        {
            await FinishAsync(source, false);
        }
    }

    [Fact]
    public async Task PreCanceled_Read_Is_Rejected_Without_Invoking_The_Producer()
    {
        Guid session = Guid.NewGuid();
        using CancellationTokenSource caller = new();
        ReplaceableVideoFrameSource source = new(session);
        Producer producer = new();
        try
        {
            await source.ReplaceAsync(producer).WaitAsync(Guard);
            caller.Cancel();
            OperationCanceledException error = Assert.Throws<OperationCanceledException>(
                () => { _ = source.ReadNextAsync(session, caller.Token); });
            Assert.Equal(caller.Token, error.CancellationToken);
            Assert.Equal(0, producer.ReadCalls);
            Assert.False(source.Completion.IsCompleted);
        }
        finally
        {
            await FinishAsync(source, false);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Overlapping_Read_Is_Rejected_Without_Replacing_The_Owned_Read(bool install)
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        TaskCompletionSource<EncodedFrame?> result = FrameSignal();
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = Frame(owner);
        Producer producer = new() { Read = _ => new(result.Task) };
        Task<EncodedFrame?>? reading = null;
        try
        {
            if (install) await source.ReplaceAsync(producer).WaitAsync(Guard);
            reading = source.ReadNextAsync(session, default).AsTask();
            if (install) await producer.ReadEntered.Task.WaitAsync(Guard);
            Assert.Throws<InvalidOperationException>(() => { _ = source.ReadNextAsync(session, default); });
            Assert.Equal(install ? 1 : 0, producer.ReadCalls);
            if (!install) await source.ReplaceAsync(producer).WaitAsync(Guard);
            result.SetResult(frame);
            Assert.Same(frame, await reading.WaitAsync(Guard));
            Assert.Equal(1, producer.ReadCalls);
            Assert.Equal(0, owner.DisposeCalls);
        }
        finally
        {
            result.TrySetResult(null);
            await FinishAsync(source, false, reading);
        }
    }

    [Fact]
    public async Task Null_Replacement_Is_Rejected_Without_Closing_The_Source()
    {
        ReplaceableVideoFrameSource source = new(Guid.NewGuid());
        Producer valid = new();
        try
        {
            Assert.Throws<ArgumentNullException>(() => { _ = source.ReplaceAsync(null!); });
            await source.ReplaceAsync(valid).WaitAsync(Guard);
            Assert.Equal(1, valid.StartCalls);
            Assert.False(source.Completion.IsCompleted);
        }
        finally
        {
            await FinishAsync(source, false);
        }
    }

    [Fact]
    public async Task Overlapping_Replace_Does_Not_Take_New_Candidate_Or_Duplicate_Owned_Candidate()
    {
        ReplaceableVideoFrameSource source = new(Guid.NewGuid());
        using ManualResetEventSlim release = new();
        Producer accepted = new() { OnStart = () => release.Wait() };
        Producer rejected = new();
        Task replacing = source.ReplaceAsync(accepted);
        try
        {
            await accepted.StartEntered.Task.WaitAsync(Guard);
            Assert.Throws<InvalidOperationException>(() => { _ = source.ReplaceAsync(rejected); });
            Assert.Throws<InvalidOperationException>(() => { _ = source.ReplaceAsync(accepted); });
            AssertUntouched(rejected);
            Assert.Equal(1, accepted.StartCalls);
            Assert.Equal(0, accepted.StopCalls);
            release.Set();
            await replacing.WaitAsync(Guard);
        }
        finally
        {
            release.Set();
            try
            {
                await FinishAsync(source, false, replacing);
                AssertUntouched(rejected);
            }
            finally
            {
                await ReleaseUnownedAsync(rejected);
            }
        }
        AssertRetiredOnce(accepted);
    }

    [Fact]
    public async Task Replacing_With_Active_Instance_Is_Rejected_Without_Changing_Ownership()
    {
        ReplaceableVideoFrameSource source = new(Guid.NewGuid());
        Producer producer = new();
        try
        {
            await source.ReplaceAsync(producer).WaitAsync(Guard);
            Assert.Throws<InvalidOperationException>(() => { _ = source.ReplaceAsync(producer); });
            Assert.Equal(1, producer.StartCalls);
            Assert.Equal(0, producer.StopCalls);
            Assert.Equal(0, producer.DisposeCalls);
        }
        finally
        {
            await FinishAsync(source, false);
        }
        AssertRetiredOnce(producer);
    }

    [Fact]
    public async Task Terminal_Replace_Rejects_Synchronously_And_Leaves_Candidate_With_Caller()
    {
        ReplaceableVideoFrameSource source = new(Guid.NewGuid());
        Producer rejected = new();
        try
        {
            await source.DisposeAsync().AsTask().WaitAsync(Guard);
            Assert.Throws<ObjectDisposedException>(() => { _ = source.ReplaceAsync(rejected); });
            AssertUntouched(rejected);
        }
        finally
        {
            await FinishAsync(source, false);
            await ReleaseUnownedAsync(rejected);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retired_Read_Late_Frame_Or_Eof_Is_Not_Published_And_Read_Continues_On_New_Generation(bool eof)
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        TaskCompletionSource<EncodedFrame?> late = FrameSignal();
        RecordingVideoOwner oldOwner = new(32);
        RecordingVideoOwner newOwner = new(32);
        using EncodedFrame oldFrame = Frame(oldOwner);
        using EncodedFrame newFrame = Frame(newOwner);
        Producer old = new() { Read = _ => new(late.Task) };
        Producer next = new() { Read = _ => ValueTask.FromResult<EncodedFrame?>(newFrame) };
        Task<EncodedFrame?>? reading = null;
        Task? replacing = null;
        try
        {
            await source.ReplaceAsync(old).WaitAsync(Guard);
            reading = source.ReadNextAsync(session, default).AsTask();
            await old.ReadEntered.Task.WaitAsync(Guard);
            replacing = source.ReplaceAsync(next);
            await RetirementEnteredAsync(old);
            Assert.False(replacing.IsCompleted);
            Assert.False(reading.IsCompleted);
            Assert.Equal(0, next.StartCalls);
            late.SetResult(eof ? null : oldFrame);
            await replacing.WaitAsync(Guard);
            Assert.Same(newFrame, await reading.WaitAsync(Guard));
            Assert.Equal(eof ? 0 : 1, oldOwner.DisposeCalls);
            Assert.Equal(0, newOwner.DisposeCalls);
            Assert.False(source.Completion.IsCompleted);
            Assert.Equal(1, old.ReadCalls);
            Assert.Equal(1, next.ReadCalls);
        }
        finally
        {
            late.TrySetResult(null);
            await FinishAsync(source, false, replacing, reading);
        }
        AssertRetiredOnce(old);
        AssertRetiredOnce(next);
    }

    [Fact]
    public async Task Original_Read_Synchronous_Prefix_And_Async_Tail_Are_Both_Joined_Before_New_Start()
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        using ManualResetEventSlim prefixRelease = new();
        TaskCompletionSource tailEntered = Signal();
        TaskCompletionSource<EncodedFrame?> tail = FrameSignal();
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = Frame(owner);
        Producer old = new()
        {
            Read = _ =>
            {
                prefixRelease.Wait();
                tailEntered.TrySetResult();
                return new(tail.Task);
            },
        };
        Producer next = new() { Read = _ => ValueTask.FromResult<EncodedFrame?>(frame) };
        Task<Task<EncodedFrame?>>? invocation = null;
        Task? replacing = null;
        try
        {
            await source.ReplaceAsync(old).WaitAsync(Guard);
            invocation = Task.Factory.StartNew(() => source.ReadNextAsync(session, default).AsTask(),
                CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            await old.ReadEntered.Task.WaitAsync(Guard);
            Task<EncodedFrame?> reading = await invocation.WaitAsync(Guard);
            replacing = source.ReplaceAsync(next);
            await RetirementEnteredAsync(old);
            Assert.False(reading.IsCompleted);
            Assert.False(replacing.IsCompleted);
            Assert.Equal(0, next.StartCalls);
            prefixRelease.Set();
            await tailEntered.Task.WaitAsync(Guard);
            Assert.False(reading.IsCompleted);
            Assert.False(replacing.IsCompleted);
            Assert.Equal(0, next.StartCalls);
            tail.SetResult(null);
            await replacing.WaitAsync(Guard);
            Assert.Same(frame, await reading.WaitAsync(Guard));
        }
        finally
        {
            prefixRelease.Set();
            tail.TrySetResult(null);
            await FinishAsync(source, false, replacing, invocation?.Unwrap());
        }
    }

    [Fact]
    public async Task Late_Owner_Dispose_Is_Joined_Before_Replacement_Starts()
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        TaskCompletionSource<EncodedFrame?> late = FrameSignal();
        TaskCompletionSource ownerEntered = Signal();
        using ManualResetEventSlim ownerRelease = new();
        RecordingVideoOwner oldOwner = new(32)
        {
            BeforeDispose = () => { ownerEntered.TrySetResult(); ownerRelease.Wait(); },
        };
        RecordingVideoOwner newOwner = new(32);
        using EncodedFrame oldFrame = Frame(oldOwner);
        using EncodedFrame newFrame = Frame(newOwner);
        Producer old = new() { Read = _ => new(late.Task) };
        Producer next = new() { Read = _ => ValueTask.FromResult<EncodedFrame?>(newFrame) };
        Task<EncodedFrame?>? reading = null;
        Task? replacing = null;
        try
        {
            await source.ReplaceAsync(old).WaitAsync(Guard);
            reading = source.ReadNextAsync(session, default).AsTask();
            await old.ReadEntered.Task.WaitAsync(Guard);
            replacing = source.ReplaceAsync(next);
            await RetirementEnteredAsync(old);
            late.SetResult(oldFrame);
            await ownerEntered.Task.WaitAsync(Guard);
            Assert.False(replacing.IsCompleted);
            Assert.False(reading.IsCompleted);
            Assert.Equal(0, next.StartCalls);
            Assert.Equal(1, oldOwner.DisposeCalls);
            ownerRelease.Set();
            await replacing.WaitAsync(Guard);
            Assert.Same(newFrame, await reading.WaitAsync(Guard));
            Assert.Equal(1, oldOwner.DisposeCalls);
        }
        finally
        {
            ownerRelease.Set();
            late.TrySetResult(null);
            await FinishAsync(source, false, replacing, reading);
        }
    }

    [Fact]
    public async Task Retired_Read_Late_Fault_Closes_Source_And_Cleans_Unstarted_Candidate()
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        TaskCompletionSource<EncodedFrame?> late = FrameSignal();
        AggregateException primary = new("原读取错误树。", new IOException("外层一。"),
            new AggregateException("嵌套。", new IOException("内层二。")));
        Producer old = new() { Read = _ => new(late.Task) };
        Producer candidate = new();
        Task<EncodedFrame?>? reading = null;
        Task? replacing = null;
        try
        {
            await source.ReplaceAsync(old).WaitAsync(Guard);
            reading = source.ReadNextAsync(session, default).AsTask();
            await old.ReadEntered.Task.WaitAsync(Guard);
            replacing = source.ReplaceAsync(candidate);
            await RetirementEnteredAsync(old);
            late.SetException(primary);
            AggregateException terminal = Assert.IsType<AggregateException>(await ObserveAsync(source.Completion));
            Assert.Same(primary, Assert.Single(terminal.InnerExceptions));
            Assert.Same(terminal, await ObserveAsync(reading));
            AggregateException replacementError = Assert.IsType<AggregateException>(await ObserveAsync(replacing));
            Assert.Same(primary, Assert.Single(replacementError.InnerExceptions));
            Assert.Equal(0, candidate.StartCalls);
            Assert.Equal(0, candidate.ReadCalls);
            AssertRetiredOnce(old);
            AssertRetiredOnce(candidate);
        }
        finally
        {
            late.TrySetResult(null);
            await FinishAsync(source, true, replacing, reading);
        }
    }

    [Fact]
    public async Task Late_Owner_Cleanup_Fault_Is_Terminal_And_Is_Not_Retried()
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        TaskCompletionSource<EncodedFrame?> late = FrameSignal();
        IOException failure = new("迟到 owner 清理失败。");
        RecordingVideoOwner owner = new(32) { DisposeFailure = failure };
        EncodedFrame frame = Frame(owner);
        Producer old = new() { Read = _ => new(late.Task) };
        Producer candidate = new();
        Task<EncodedFrame?>? reading = null;
        Task? replacing = null;
        try
        {
            await source.ReplaceAsync(old).WaitAsync(Guard);
            reading = source.ReadNextAsync(session, default).AsTask();
            await old.ReadEntered.Task.WaitAsync(Guard);
            replacing = source.ReplaceAsync(candidate);
            await RetirementEnteredAsync(old);
            late.SetResult(frame);
            AggregateException terminal = Assert.IsType<AggregateException>(await ObserveAsync(source.Completion));
            Assert.Same(failure, Assert.Single(terminal.InnerExceptions));
            Assert.Same(terminal, await ObserveAsync(reading));
            AggregateException replacementError = Assert.IsType<AggregateException>(await ObserveAsync(replacing));
            Assert.Same(failure, Assert.Single(replacementError.InnerExceptions));
            Assert.Equal(1, owner.DisposeCalls);
            Assert.Equal(0, candidate.StartCalls);
            AssertRetiredOnce(candidate);
        }
        finally
        {
            late.TrySetResult(null);
            try { await FinishAsync(source, true, replacing, reading); }
            finally
            {
                owner.DisposeFailure = null;
                frame.Dispose();
            }
        }
        Assert.Equal(1, owner.DisposeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Committed_Frame_Cannot_Be_Reclaimed_By_Replace_Or_Dispose(bool dispose)
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = Frame(owner);
        Producer old = new() { Read = _ => ValueTask.FromResult<EncodedFrame?>(frame) };
        Producer next = new();
        try
        {
            await source.ReplaceAsync(old).WaitAsync(Guard);
            Assert.Same(frame, await source.ReadNextAsync(session, default).AsTask().WaitAsync(Guard));
            if (dispose) await source.DisposeAsync().AsTask().WaitAsync(Guard);
            else await source.ReplaceAsync(next).WaitAsync(Guard);
            AssertRetiredOnce(old);
            Assert.Equal(0, owner.DisposeCalls);
            Assert.Equal(VideoFrameTestData.Payload(), frame.Payload.ToArray());
            frame.Dispose();
            Assert.Equal(1, owner.DisposeCalls);
        }
        finally
        {
            await FinishAsync(source, false);
        }
        Assert.Equal(1, owner.DisposeCalls);
    }

    [Fact]
    public async Task Blocking_Start_Does_Not_Block_Replace_Entrance_Or_Independent_Dispose_And_Never_Publishes_Late()
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        using ManualResetEventSlim startRelease = new();
        Producer candidate = new() { OnStart = () => startRelease.Wait() };
        Task<Task> invocation = Task.Factory.StartNew(() => source.ReplaceAsync(candidate),
            CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        Task<EncodedFrame?>? reading = null;
        try
        {
            await candidate.StartEntered.Task.WaitAsync(Guard);
            Task replacing = await invocation.WaitAsync(Guard);
            Task disposing = source.DisposeAsync().AsTask();
            Assert.Same(source.Completion, disposing);
            await RetirementEnteredAsync(candidate);
            Assert.False(disposing.IsCompleted);
            Assert.False(replacing.IsCompleted);
            reading = source.ReadNextAsync(session, default).AsTask();
            Assert.False(reading.IsCompleted);
            Assert.Equal(0, candidate.ReadCalls);
            startRelease.Set();
            Assert.IsType<InvalidOperationException>(await ObserveAsync(replacing));
            await disposing.WaitAsync(Guard);
            Assert.Null(await reading.WaitAsync(Guard));
            Assert.Equal(0, candidate.ReadCalls);
            AssertRetiredOnce(candidate);
        }
        finally
        {
            startRelease.Set();
            await FinishAsync(source, false, invocation.Unwrap(), reading);
        }
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("dispose")]
    [InlineData("completion")]
    public async Task Blocking_Cleanup_Prefix_Does_Not_Prevent_Other_Cleanup_Entrances(string slot)
    {
        ReplaceableVideoFrameSource source = new(Guid.NewGuid());
        using ManualResetEventSlim release = new();
        Task Select(string current)
        {
            if (current == slot) release.Wait();
            return Task.CompletedTask;
        }
        Producer producer = new()
        {
            Stop = () => Select("stop"),
            Dispose = () => Select("dispose"),
            GetCompletion = () => Select("completion"),
        };
        Task? disposing = null;
        try
        {
            await source.ReplaceAsync(producer).WaitAsync(Guard);
            disposing = source.DisposeAsync().AsTask();
            await RetirementEnteredAsync(producer);
            Assert.False(disposing.IsCompleted);
            AssertRetiredOnce(producer);
            release.Set();
            await disposing.WaitAsync(Guard);
        }
        finally
        {
            release.Set();
            await FinishAsync(source, false, disposing);
        }
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("dispose")]
    [InlineData("completion")]
    public async Task Each_Original_Cleanup_Task_Is_Joined_Before_Candidate_Start(string slot)
    {
        ReplaceableVideoFrameSource source = new(Guid.NewGuid());
        TaskCompletionSource release = Signal();
        Task Select(string current) => current == slot ? release.Task : Task.CompletedTask;
        Producer old = new()
        {
            Stop = () => Select("stop"),
            Dispose = () => Select("dispose"),
            GetCompletion = () => Select("completion"),
        };
        Producer next = new();
        Task? replacing = null;
        try
        {
            await source.ReplaceAsync(old).WaitAsync(Guard);
            replacing = source.ReplaceAsync(next);
            await RetirementEnteredAsync(old);
            Assert.False(replacing.IsCompleted);
            Assert.Equal(0, next.StartCalls);
            release.SetResult();
            await replacing.WaitAsync(Guard);
            Assert.Equal(1, next.StartCalls);
            AssertRetiredOnce(old);
            Assert.False(source.Completion.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await FinishAsync(source, false, replacing);
        }
    }

    [Fact]
    public async Task Old_Retirement_Failure_Prevents_Candidate_Start_And_Preserves_All_Cleanup_Error_Trees()
    {
        ReplaceableVideoFrameSource source = new(Guid.NewGuid());
        AggregateException stopTree = new("旧 Stop 树。", new IOException("第一分支。"),
            new AggregateException("第二分支。", new IOException("叶子。")));
        IOException disposeError = new("旧 Dispose 错误。");
        IOException completionError = new("旧 Completion 错误。");
        IOException candidateError = new("候选清理错误。");
        Task stop = Task.FromException(stopTree);
        Task dispose = Task.FromException(disposeError);
        Task completion = Task.FromException(completionError);
        Task candidateCleanup = Task.FromException(candidateError);
        Producer old = new() { Stop = () => stop, Dispose = () => dispose, GetCompletion = () => completion };
        Producer candidate = new()
        {
            Stop = () => candidateCleanup,
            Dispose = () => candidateCleanup,
            GetCompletion = () => candidateCleanup,
        };
        Task? replacing = null;
        try
        {
            await source.ReplaceAsync(old).WaitAsync(Guard);
            replacing = source.ReplaceAsync(candidate);
            AggregateException replacementError = Assert.IsType<AggregateException>(await ObserveAsync(replacing));
            AggregateException terminal = Assert.IsType<AggregateException>(await ObserveAsync(source.Completion));
            Assert.Collection(terminal.InnerExceptions,
                error => Assert.Same(stopTree, error),
                error => Assert.Same(disposeError, error),
                error => Assert.Same(completionError, error),
                error => Assert.Same(candidateError, error));
            Assert.Equal(terminal.InnerExceptions.ToArray(), replacementError.InnerExceptions.ToArray());
            Assert.Equal(0, candidate.StartCalls);
            Assert.Equal(0, candidate.ReadCalls);
            AssertRetiredOnce(old);
            AssertRetiredOnce(candidate);
        }
        finally
        {
            await FinishAsync(source, true, replacing, stop, dispose, completion, candidateCleanup);
        }
    }

    [Fact]
    public async Task Accepted_Candidate_Start_Failure_Is_Asynchronous_And_Does_Not_Return_Ownership()
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        AggregateException startError = new("启动树。", new IOException("原始错误。"));
        Producer candidate = new() { OnStart = () => throw startError };
        Task? replacing = null;
        try
        {
            Exception? synchronous = Record.Exception(() => { replacing = source.ReplaceAsync(candidate); });
            Assert.Null(synchronous);
            Assert.NotNull(replacing);
            AggregateException replacementError = Assert.IsType<AggregateException>(await ObserveAsync(replacing));
            Assert.Same(startError, Assert.Single(replacementError.InnerExceptions));
            AggregateException terminal = Assert.IsType<AggregateException>(await ObserveAsync(source.Completion));
            Assert.Same(startError, Assert.Single(terminal.InnerExceptions));
            Assert.Same(terminal, await ObserveAsync(source.ReadNextAsync(session, default).AsTask()));
            AssertRetiredOnce(candidate);
            Assert.Equal(1, candidate.StartCalls);
            Assert.Equal(0, candidate.ReadCalls);
        }
        finally
        {
            await FinishAsync(source, true, replacing);
        }
    }

    [Fact]
    public async Task Same_Original_Lifecycle_Task_Is_Deduplicated_Without_Flattening_Its_Error_Tree()
    {
        ReplaceableVideoFrameSource source = new(Guid.NewGuid());
        AggregateException inner = new("内层聚合。", new IOException("叶一。"), new IOException("叶二。"));
        AggregateException outer = new("外层聚合。", inner, new IOException("叶三。"));
        Task original = Task.FromException(outer);
        Producer producer = new()
        {
            Stop = () => original,
            Dispose = () => original,
            GetCompletion = () => original,
        };
        try
        {
            await source.ReplaceAsync(producer).WaitAsync(Guard);
            Task disposing = source.DisposeAsync().AsTask();
            AggregateException terminal = Assert.IsType<AggregateException>(await ObserveAsync(disposing));
            Assert.Same(outer, Assert.Single(terminal.InnerExceptions));
            Assert.Same(inner, outer.InnerExceptions[0]);
            Assert.Same(terminal, await ObserveAsync(source.Completion));
            Assert.Same(disposing, source.DisposeAsync().AsTask());
            AssertRetiredOnce(producer);
        }
        finally
        {
            await FinishAsync(source, true, original);
        }
    }

    [Fact]
    public async Task Different_Original_Tasks_With_The_Same_Exception_Instance_Are_Not_Deduplicated()
    {
        ReplaceableVideoFrameSource source = new(Guid.NewGuid());
        AggregateException shared = new("同一异常实例。", new IOException("叶子。"));
        Task stop = Task.FromException(shared);
        Task dispose = Task.FromException(shared);
        Task completion = Task.FromException(shared);
        Producer producer = new() { Stop = () => stop, Dispose = () => dispose, GetCompletion = () => completion };
        try
        {
            await source.ReplaceAsync(producer).WaitAsync(Guard);
            AggregateException terminal = Assert.IsType<AggregateException>(await ObserveAsync(source.DisposeAsync().AsTask()));
            Assert.Equal(3, terminal.InnerExceptions.Count);
            Assert.All(terminal.InnerExceptions, error => Assert.Same(shared, error));
            AssertRetiredOnce(producer);
        }
        finally
        {
            await FinishAsync(source, true, stop, dispose, completion);
        }
    }

    [Fact]
    public async Task Canceled_Cleanup_Task_Is_A_Terminal_Fault_Not_Caller_Cancellation()
    {
        ReplaceableVideoFrameSource source = new(Guid.NewGuid());
        using CancellationTokenSource cleanup = new();
        cleanup.Cancel();
        Task original = Task.FromCanceled(cleanup.Token);
        Producer producer = new()
        {
            Stop = () => original,
            Dispose = () => original,
            GetCompletion = () => original,
        };
        try
        {
            await source.ReplaceAsync(producer).WaitAsync(Guard);
            AggregateException terminal = Assert.IsType<AggregateException>(await ObserveAsync(source.DisposeAsync().AsTask()));
            OperationCanceledException error = Assert.IsAssignableFrom<OperationCanceledException>(Assert.Single(terminal.InnerExceptions));
            Assert.Equal(cleanup.Token, error.CancellationToken);
            Assert.True(source.Completion.IsFaulted);
            Assert.False(source.Completion.IsCanceled);
            AssertRetiredOnce(producer);
        }
        finally
        {
            await FinishAsync(source, true, original);
        }
    }

    [Fact]
    public async Task Original_Read_True_Caller_Cancellation_Does_Not_Stop_Producer_And_Next_Read_Succeeds()
    {
        Guid session = Guid.NewGuid();
        using CancellationTokenSource caller = new();
        ReplaceableVideoFrameSource source = new(session);
        TaskCompletionSource<EncodedFrame?> original = FrameSignal();
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = Frame(owner);
        Producer producer = new()
        {
            Read = token =>
            {
                Assert.Equal(caller.Token, token);
                return new(original.Task);
            },
        };
        Task<EncodedFrame?>? reading = null;
        try
        {
            await source.ReplaceAsync(producer).WaitAsync(Guard);
            reading = source.ReadNextAsync(session, caller.Token).AsTask();
            await producer.ReadEntered.Task.WaitAsync(Guard);
            caller.Cancel();
            Assert.False(reading.IsCompleted);
            original.SetCanceled(caller.Token);
            OperationCanceledException error = Assert.IsAssignableFrom<OperationCanceledException>(await ObserveAsync(reading));
            Assert.Equal(caller.Token, error.CancellationToken);
            Assert.True(reading.IsCanceled);
            Assert.Equal(0, producer.StopCalls);
            Assert.Equal(0, producer.DisposeCalls);
            Assert.False(source.Completion.IsCompleted);
            producer.Read = _ => ValueTask.FromResult<EncodedFrame?>(frame);
            Assert.Same(frame, await source.ReadNextAsync(session, default).AsTask().WaitAsync(Guard));
            Assert.Equal(2, producer.ReadCalls);
        }
        finally
        {
            original.TrySetCanceled(caller.Token);
            await FinishAsync(source, false, reading, original.Task);
        }
    }

    [Fact]
    public async Task Faulted_Original_Read_Oce_Remains_A_Fault_Even_With_The_Canceled_Caller_Token()
    {
        Guid session = Guid.NewGuid();
        using CancellationTokenSource caller = new();
        ReplaceableVideoFrameSource source = new(session);
        TaskCompletionSource<EncodedFrame?> original = FrameSignal();
        OperationCanceledException primary = new("原任务 faulted，不能视为 caller 取消。", caller.Token);
        Producer producer = new() { Read = _ => new(original.Task) };
        Task<EncodedFrame?>? reading = null;
        try
        {
            await source.ReplaceAsync(producer).WaitAsync(Guard);
            reading = source.ReadNextAsync(session, caller.Token).AsTask();
            await producer.ReadEntered.Task.WaitAsync(Guard);
            caller.Cancel();
            original.SetException(primary);
            AggregateException terminal = Assert.IsType<AggregateException>(await ObserveAsync(source.Completion));
            Assert.True(original.Task.IsFaulted);
            Assert.True(reading.IsFaulted);
            Assert.Same(primary, Assert.Single(terminal.InnerExceptions));
            Assert.Same(terminal, await ObserveAsync(reading));
            AssertRetiredOnce(producer);
        }
        finally
        {
            original.TrySetResult(null);
            await FinishAsync(source, true, reading, original.Task);
        }
    }

    [Fact]
    public async Task Active_Eof_Read_Waits_For_Full_Cleanup_Without_Joining_Itself()
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        TaskCompletionSource cleanup = Signal();
        Producer producer = new() { Stop = () => cleanup.Task };
        Task<EncodedFrame?>? reading = null;
        try
        {
            await source.ReplaceAsync(producer).WaitAsync(Guard);
            reading = source.ReadNextAsync(session, default).AsTask();
            await RetirementEnteredAsync(producer);
            Assert.False(reading.IsCompleted);
            Assert.False(source.Completion.IsCompleted);
            cleanup.SetResult();
            Assert.Null(await reading.WaitAsync(Guard));
            await source.Completion.WaitAsync(Guard);
            Assert.Null(await source.ReadNextAsync(session, default).AsTask().WaitAsync(Guard));
            Assert.Equal(1, producer.ReadCalls);
            AssertRetiredOnce(producer);
        }
        finally
        {
            cleanup.TrySetResult();
            await FinishAsync(source, false, reading, cleanup.Task);
        }
    }

    [Fact]
    public async Task Active_Completion_Fault_Is_Not_Automatically_Monitored_Before_Read_Eof_Or_Dispose()
    {
        // 此来源刻意不提供 active Completion 监视器；生产者应通过其 Read 边界呈现退出。
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        IOException failure = new("提前结束的生产者。");
        Task original = Task.FromException(failure);
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = Frame(owner);
        Producer producer = new()
        {
            Read = _ => ValueTask.FromResult<EncodedFrame?>(frame),
            Stop = () => original,
            Dispose = () => original,
            GetCompletion = () => original,
        };
        try
        {
            await source.ReplaceAsync(producer).WaitAsync(Guard);
            Assert.Same(frame, await source.ReadNextAsync(session, default).AsTask().WaitAsync(Guard));
            Assert.Equal(0, producer.CompletionReads);
            Assert.False(source.Completion.IsCompleted);
            Assert.Equal(0, owner.DisposeCalls);
            AggregateException terminal = Assert.IsType<AggregateException>(await ObserveAsync(source.DisposeAsync().AsTask()));
            Assert.Same(failure, Assert.Single(terminal.InnerExceptions));
            Assert.Equal(0, owner.DisposeCalls);
        }
        finally
        {
            await FinishAsync(source, true, original);
        }
    }

    [Fact]
    public async Task One_Sender_Keeps_Continuous_Wire_Ids_Across_Producer_Replacement()
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        RecordingVideoOwner firstOwner = new(32);
        RecordingVideoOwner secondOwner = new(32);
        using EncodedFrame first = Frame(firstOwner);
        using EncodedFrame second = Frame(secondOwner);
        TaskCompletionSource secondReadEntered = Signal();
        TaskCompletionSource<EncodedFrame?> oldTail = FrameSignal();
        int oldReads = 0;
        int newReads = 0;
        Producer old = new()
        {
            Read = _ =>
            {
                if (Interlocked.Increment(ref oldReads) == 1) return ValueTask.FromResult<EncodedFrame?>(first);
                secondReadEntered.TrySetResult();
                return new(oldTail.Task);
            },
        };
        Producer next = new()
        {
            Read = _ => ValueTask.FromResult<EncodedFrame?>(Interlocked.Increment(ref newReads) == 1 ? second : null),
        };
        RecordingVideoStream stream = new();
        using VideoFrameWriter writer = new(stream);
        VideoFrameSender sender = new(41);
        ManualDeadlineClock clock = new();
        Task? sending = null;
        Task? replacing = null;
        try
        {
            await source.ReplaceAsync(old).WaitAsync(Guard);
            sending = sender.SendAsync(session, writer, source, clock, TimeSpan.FromSeconds(10), default);
            await secondReadEntered.Task.WaitAsync(Guard);
            Assert.Equal(1, firstOwner.DisposeCalls);
            Assert.Equal(1, stream.FlushCalls);
            Assert.Equal(0, clock.TimerCount);
            replacing = source.ReplaceAsync(next);
            await RetirementEnteredAsync(old);
            Assert.False(sending.IsCompleted);
            oldTail.SetResult(null);
            await replacing.WaitAsync(Guard);
            await sending.WaitAsync(Guard);
            await source.Completion.WaitAsync(Guard);
            byte[] firstWire = VideoFrameTestData.Wire();
            byte[] secondWire = VideoFrameTestData.Wire();
            BinaryPrimitives.WriteUInt64BigEndian(firstWire.AsSpan(8, 8), 41);
            BinaryPrimitives.WriteUInt64BigEndian(secondWire.AsSpan(8, 8), 42);
            Assert.Equal(firstWire.Concat(secondWire).ToArray(), stream.Written);
            Assert.Equal(first.FrameId, second.FrameId);
            Assert.Equal(2, stream.FlushCalls);
            Assert.Equal(1, firstOwner.DisposeCalls);
            Assert.Equal(1, secondOwner.DisposeCalls);
            Assert.Equal(0, clock.TimerCount);
            Assert.Empty(sender.CleanupErrors);
            Assert.Null(sender.SourceFailure);
            AssertRetiredOnce(old);
            AssertRetiredOnce(next);
        }
        finally
        {
            oldTail.TrySetResult(null);
            await FinishAsync(source, false, replacing, sending);
        }
    }

    [Fact]
    public async Task Synchronous_Stop_Fault_Does_Not_Skip_Original_Dispose_And_Completion_Tasks()
    {
        ReplaceableVideoFrameSource source = new(Guid.NewGuid());
        IOException stopFailure = new("同步 Stop 失败。");
        AggregateException disposeFailure = new("独立 Dispose 树。", new IOException("叶子。"));
        TaskCompletionSource disposing = Signal();
        TaskCompletionSource completing = Signal();
        Producer producer = new()
        {
            Stop = () => throw stopFailure,
            Dispose = () => disposing.Task,
            GetCompletion = () => completing.Task,
        };
        try
        {
            await source.ReplaceAsync(producer).WaitAsync(Guard);
            Task closing = source.DisposeAsync().AsTask();
            await RetirementEnteredAsync(producer);
            AssertRetiredOnce(producer);
            Assert.False(closing.IsCompleted);
            disposing.SetException(disposeFailure);
            Assert.False(closing.IsCompleted);
            completing.SetResult();
            AggregateException terminal = Assert.IsType<AggregateException>(await ObserveAsync(closing));
            Assert.Collection(terminal.InnerExceptions,
                error => Assert.Same(stopFailure, error),
                error => Assert.Same(disposeFailure, error));
            Assert.True(disposing.Task.IsCompleted);
            Assert.True(completing.Task.IsCompleted);
        }
        finally
        {
            disposing.TrySetResult();
            completing.TrySetResult();
            await FinishAsync(source, true, disposing.Task, completing.Task);
        }
    }

    [Fact]
    public async Task Multiple_Roots_Of_One_Original_Task_Keep_Their_Container_And_Nested_Tree()
    {
        ReplaceableVideoFrameSource source = new(Guid.NewGuid());
        IOException first = new("原任务第一根。");
        AggregateException second = new("原任务第二根。", new IOException("嵌套叶子。"));
        TaskCompletionSource original = Signal();
        original.SetException([first, second]);
        Producer producer = new()
        {
            Stop = () => original.Task,
            Dispose = () => original.Task,
            GetCompletion = () => original.Task,
        };
        try
        {
            await source.ReplaceAsync(producer).WaitAsync(Guard);
            AggregateException terminal = Assert.IsType<AggregateException>(await ObserveAsync(source.DisposeAsync().AsTask()));
            AggregateException roots = Assert.IsType<AggregateException>(Assert.Single(terminal.InnerExceptions));
            Assert.Collection(roots.InnerExceptions,
                error => Assert.Same(first, error),
                error => Assert.Same(second, error));
            AssertRetiredOnce(producer);
        }
        finally
        {
            await FinishAsync(source, true, original.Task);
        }
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("throws")]
    public async Task Concurrent_Stop_Only_Suppresses_Start_Rejection_With_Explicit_Successful_Evidence(string evidence)
    {
        ReplaceableVideoFrameSource source = new(Guid.NewGuid());
        using ManualResetEventSlim release = new();
        InvalidOperationException rejection = new("启动边界拒绝。");
        IOException proofFailure = new("拒绝证据查询失败。");
        ProvenProducer producer = new()
        {
            OnStart = () => { release.Wait(); throw rejection; },
            Prove = error =>
            {
                Assert.Same(rejection, error);
                if (evidence == "throws") throw proofFailure;
                return evidence == "true";
            },
        };
        Task replacing = source.ReplaceAsync(producer);
        try
        {
            await producer.StartEntered.Task.WaitAsync(Guard);
            Task closing = source.DisposeAsync().AsTask();
            await RetirementEnteredAsync(producer);
            Assert.False(closing.IsCompleted);
            Assert.False(replacing.IsCompleted);
            Assert.Equal(0, producer.ProofCalls);
            release.Set();
            Exception? replacementError = await ObserveAsync(replacing);
            Exception? terminalError = await ObserveAsync(closing);
            Assert.Equal(1, producer.ProofCalls);
            AssertRetiredOnce(producer);
            if (evidence == "true")
            {
                Assert.IsType<InvalidOperationException>(replacementError);
                Assert.Null(terminalError);
            }
            else
            {
                AggregateException replacementTree = Assert.IsType<AggregateException>(replacementError);
                AggregateException terminalTree = Assert.IsType<AggregateException>(terminalError);
                if (evidence == "false")
                {
                    Assert.Same(rejection, Assert.Single(terminalTree.InnerExceptions));
                }
                else
                {
                    Assert.Collection(terminalTree.InnerExceptions,
                        error => Assert.Same(rejection, error),
                        error => Assert.Same(proofFailure, error));
                }
                Assert.Equal(terminalTree.InnerExceptions.ToArray(), replacementTree.InnerExceptions.ToArray());
            }
        }
        finally
        {
            release.Set();
            await FinishAsync(source, evidence != "true", replacing);
        }
    }

    [Fact]
    public async Task Start_Failure_Before_Retirement_Is_Not_Reclassified_Using_Later_Stop_Evidence()
    {
        ReplaceableVideoFrameSource source = new(Guid.NewGuid());
        InvalidOperationException startFailure = new("停止请求之前的真实启动故障。");
        ProvenProducer producer = new()
        {
            OnStart = () => throw startFailure,
            Prove = _ => true,
        };
        Task replacing = source.ReplaceAsync(producer);
        try
        {
            AggregateException replacementError = Assert.IsType<AggregateException>(await ObserveAsync(replacing));
            Assert.Same(startFailure, Assert.Single(replacementError.InnerExceptions));
            AggregateException terminal = Assert.IsType<AggregateException>(await ObserveAsync(source.Completion));
            Assert.Same(startFailure, Assert.Single(terminal.InnerExceptions));
            Assert.Equal(0, producer.ProofCalls);
            AssertRetiredOnce(producer);
        }
        finally
        {
            await FinishAsync(source, true, replacing);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Replacement_Does_Not_Reclaim_Senders_Frame_Or_Cancel_Original_Wire_Operation(int boundary)
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        RecordingVideoOwner oldOwner = new(32);
        RecordingVideoOwner nextOwner = new(32);
        using EncodedFrame first = Frame(oldOwner);
        using EncodedFrame second = Frame(nextOwner);
        Producer old = new() { Read = _ => ValueTask.FromResult<EncodedFrame?>(first) };
        int reads = 0;
        Producer next = new()
        {
            Read = _ => ValueTask.FromResult<EncodedFrame?>(Interlocked.Increment(ref reads) == 1 ? second : null),
        };
        using HeldWireStream stream = new(boundary);
        VideoFrameSender sender = new(70);
        Task? sending = null;
        try
        {
            await source.ReplaceAsync(old).WaitAsync(Guard);
            sending = sender.SendAsync(session, new VideoFrameWriter(stream), source,
                new ManualDeadlineClock(), TimeSpan.FromSeconds(10), default);
            await stream.Entered.Task.WaitAsync(Guard);
            await source.ReplaceAsync(next).WaitAsync(Guard);
            // 替换已真实结束，而旧帧原 I/O 仍被 gate 卡住；这不是瞬时调度猜测。
            AssertRetiredOnce(old);
            Assert.Equal(1, next.StartCalls);
            Assert.Equal(0, next.ReadCalls);
            Assert.False(sending.IsCompleted);
            Assert.False(stream.OriginalToken.IsCancellationRequested);
            Assert.Equal(0, oldOwner.DisposeCalls);
            Assert.Equal(0, nextOwner.DisposeCalls);
            Assert.False(stream.OriginalExited);
            stream.Release.TrySetResult();
            await sending.WaitAsync(Guard);
            await source.Completion.WaitAsync(Guard);
            Assert.True(stream.OriginalExited);
            byte[] a = VideoFrameTestData.Wire();
            byte[] b = VideoFrameTestData.Wire();
            BinaryPrimitives.WriteUInt64BigEndian(a.AsSpan(8, 8), 70);
            BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(8, 8), 71);
            Assert.Equal(a.Concat(b).ToArray(), stream.Bytes);
            Assert.Equal(1, oldOwner.DisposeCalls);
            Assert.Equal(1, nextOwner.DisposeCalls);
            AssertRetiredOnce(next);
        }
        finally
        {
            stream.Release.TrySetResult();
            await FinishAsync(source, false, sending);
        }
    }

    private sealed class HeldWireStream(int boundary) : Stream
    {
        private readonly MemoryStream _bytes = new();
        private int _operation;
        internal TaskCompletionSource Entered { get; } = Signal();
        internal TaskCompletionSource Release { get; } = Signal();
        internal CancellationToken OriginalToken { get; private set; }
        internal bool OriginalExited { get; private set; }
        internal byte[] Bytes => _bytes.ToArray();
        private async Task Hold(CancellationToken token)
        {
            if (++_operation != boundary) return;
            OriginalToken = token;
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            OriginalExited = true;
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Hold(cancellationToken);
            _bytes.Write(buffer.Span);
        }
        public override Task FlushAsync(CancellationToken cancellationToken) => Hold(cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing) _bytes.Dispose();
            base.Dispose(disposing);
        }
        public override bool CanRead => false;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<EncodedFrame?> FrameSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static EncodedFrame Frame(RecordingVideoOwner owner)
    {
        VideoFrameTestData.Payload().CopyTo(owner.Bytes, 0);
        return VideoFrameTestData.Frame(owner);
    }

    private static Task RetirementEnteredAsync(Producer producer) => Task.WhenAll(
        producer.StopEntered.Task, producer.DisposeEntered.Task, producer.CompletionEntered.Task).WaitAsync(Guard);

    private static void AssertRetiredOnce(Producer producer)
    {
        Assert.Equal(1, producer.StopCalls);
        Assert.Equal(1, producer.DisposeCalls);
        Assert.Equal(1, producer.CompletionReads);
    }

    private static void AssertUntouched(Producer producer)
    {
        Assert.Equal(0, producer.StartCalls);
        Assert.Equal(0, producer.ReadCalls);
        Assert.Equal(0, producer.StopCalls);
        Assert.Equal(0, producer.DisposeCalls);
        Assert.Equal(0, producer.CompletionReads);
    }

    private static async Task ReleaseUnownedAsync(Producer producer)
    {
        await producer.DisposeAsync().AsTask().WaitAsync(Guard);
        await producer.Completion.WaitAsync(Guard);
    }

    private static async Task<Exception?> ObserveAsync(Task task)
    {
        try
        {
            await task.WaitAsync(Guard);
            return null;
        }
        catch (TimeoutException)
        {
            // Guard 只防止测试卡死；超时不是被接受的生产错误或取消。
            throw;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private static async Task FinishAsync(ReplaceableVideoFrameSource source, bool expectFailure, params Task?[] operations)
    {
        // 调用者必须先在 finally 中放行全部 gate；每个外部原任务和稳定 Completion 都要被观察。
        Task disposing = source.DisposeAsync().AsTask();
        foreach (Task? operation in operations)
        {
            if (operation is not null) _ = await ObserveAsync(operation);
        }
        Exception? disposeError = await ObserveAsync(disposing);
        Exception? completionError = await ObserveAsync(source.Completion);
        if (!expectFailure)
        {
            Assert.Null(disposeError);
            Assert.Null(completionError);
        }
    }

    private sealed class ProvenProducer : Producer, IVideoFrameProducerStartRejection
    {
        private int _proofCalls;
        internal required Func<InvalidOperationException, bool> Prove { get; init; }
        internal int ProofCalls => Volatile.Read(ref _proofCalls);

        public bool IsStopBeforeStartRejection(InvalidOperationException error)
        {
            Interlocked.Increment(ref _proofCalls);
            return Prove(error);
        }
    }

    private class Producer : IVideoFrameProducer
    {
        private readonly TaskCompletionSource _lifetime = Signal();
        private int _startCalls;
        private int _readCalls;
        private int _stopCalls;
        private int _disposeCalls;
        private int _completionReads;

        internal Action? OnStart { get; set; }
        internal Func<CancellationToken, ValueTask<EncodedFrame?>> Read { get; set; } = _ => ValueTask.FromResult<EncodedFrame?>(null);
        internal Func<Task>? Stop { get; init; }
        internal Func<Task>? Dispose { get; init; }
        internal Func<Task>? GetCompletion { get; init; }
        internal TaskCompletionSource StartEntered { get; } = Signal();
        internal TaskCompletionSource ReadEntered { get; } = Signal();
        internal TaskCompletionSource StopEntered { get; } = Signal();
        internal TaskCompletionSource DisposeEntered { get; } = Signal();
        internal TaskCompletionSource CompletionEntered { get; } = Signal();
        internal int StartCalls => Volatile.Read(ref _startCalls);
        internal int ReadCalls => Volatile.Read(ref _readCalls);
        internal int StopCalls => Volatile.Read(ref _stopCalls);
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        internal int CompletionReads => Volatile.Read(ref _completionReads);

        public void Start()
        {
            Interlocked.Increment(ref _startCalls);
            StartEntered.TrySetResult();
            OnStart?.Invoke();
        }

        public ValueTask<EncodedFrame?> ReadNextAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readCalls);
            ReadEntered.TrySetResult();
            return Read(cancellationToken);
        }

        public Task StopAsync()
        {
            Interlocked.Increment(ref _stopCalls);
            StopEntered.TrySetResult();
            if (Stop is not null) return Stop();
            _lifetime.TrySetResult();
            return _lifetime.Task;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            DisposeEntered.TrySetResult();
            if (Dispose is not null) return new(Dispose());
            _lifetime.TrySetResult();
            return new(_lifetime.Task);
        }

        public Task Completion
        {
            get
            {
                Interlocked.Increment(ref _completionReads);
                CompletionEntered.TrySetResult();
                return GetCompletion is not null ? GetCompletion() : _lifetime.Task;
            }
        }
    }
}
