using LanRemote.Core.Models;

namespace LanRemote.Transport.Tests;

public sealed class VideoFrameSenderTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Sends_Whole_Golden_Vectors_Without_Pool_Tails_And_Releases_Before_Next_Read()
    {
        Guid sessionId = Guid.NewGuid();
        using CancellationTokenSource caller = new();
        ManualDeadlineClock clock = new();
        clock.Advance(TimeSpan.FromDays(3));
        RecordingVideoOwner firstOwner = new(1024);
        RecordingVideoOwner secondOwner = new(2048);
        using EncodedFrame first = CreateFrame(firstOwner);
        using EncodedFrame second = CreateFrame(secondOwner);
        RecordingVideoStream stream = new();
        using VideoFrameWriter writer = new(stream);
        VideoFrameSender sender = new();
        int reads = 0;
        DelegateSource source = new((id, token) =>
        {
            Assert.Equal(sessionId, id);
            Assert.Equal(caller.Token, token);
            Assert.Equal(0, clock.TimerCount);
            Assert.Equal(reads, stream.FlushCalls);
            if (reads > 0)
            {
                Assert.Equal(1, firstOwner.DisposeCalls);
            }
            if (reads > 1)
            {
                Assert.Equal(1, secondOwner.DisposeCalls);
            }

            // 取帧不属于整帧写出预算；每帧预算也不能沿用上一帧的起点。
            clock.Advance(Budget + Budget);
            return ValueTask.FromResult<EncodedFrame?>(reads++ switch { 0 => first, 1 => second, _ => null });
        });

        await sender.SendAsync(sessionId, writer, source, clock, Budget, caller.Token);

        Assert.Equal(3, source.Calls);
        Assert.Equal(VideoFrameTestData.Wire().Concat(VideoFrameTestData.Wire()).ToArray(), stream.Written);
        Assert.Equal(new[] { 40, 5, 40, 5 }, stream.WriteRequests);
        Assert.Equal(2, stream.FlushCalls);
        Assert.Equal(1, firstOwner.DisposeCalls);
        Assert.Equal(1, secondOwner.DisposeCalls);
        Assert.All(firstOwner.Bytes[5..], value => Assert.Equal(0xCC, value));
        Assert.All(secondOwner.Bytes[5..], value => Assert.Equal(0xCC, value));
        Assert.Equal(6, stream.Tokens.Count);
        Assert.All(stream.Tokens.Take(3), token => Assert.Equal(stream.Tokens[0], token));
        Assert.All(stream.Tokens.Skip(3), token => Assert.Equal(stream.Tokens[3], token));
        Assert.NotEqual(stream.Tokens[0], stream.Tokens[3]);
        Assert.All(stream.Tokens, token => Assert.NotEqual(caller.Token, token));
        Assert.Equal(1, stream.DisposeCalls);
        Assert.Equal(0, clock.TimerCount);
        Assert.Empty(sender.CleanupErrors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_Or_PreCanceled_Run_Closes_Writer_Without_Starting_A_Deadline(bool canceled)
    {
        using CancellationTokenSource caller = new();
        if (canceled)
        {
            caller.Cancel();
        }
        ProbeClock clock = new(new ManualDeadlineClock());
        RecordingVideoStream stream = new();
        using VideoFrameWriter writer = new(stream);
        VideoFrameSender sender = new();
        DelegateSource source = new((_, _) => ValueTask.FromResult<EncodedFrame?>(null));

        Exception? error = await Record.ExceptionAsync(() =>
            sender.SendAsync(Guid.NewGuid(), writer, source, clock, Budget, caller.Token));

        if (canceled)
        {
            Assert.Equal(caller.Token, Assert.IsAssignableFrom<OperationCanceledException>(error).CancellationToken);
        }
        else
        {
            Assert.Null(error);
        }
        Assert.Equal(canceled ? 0 : 1, source.Calls);
        Assert.Equal(0, clock.TimerCreations);
        Assert.Empty(stream.WriteRequests);
        Assert.Equal(0, stream.FlushCalls);
        Assert.Equal(1, stream.DisposeCalls);
        Assert.Empty(sender.CleanupErrors);
    }

    [Fact]
    public async Task Run_Once_Rejects_Overlap_Without_Closing_The_Active_Writer_Or_Prefetching()
    {
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = CreateFrame(owner);
        RecordingVideoStream stream = new() { GateWriteAt = 41 };
        using VideoFrameWriter writer = new(stream);
        ManualDeadlineClock clock = new();
        VideoFrameSender sender = new();
        int reads = 0;
        DelegateSource source = new((_, _) => ValueTask.FromResult<EncodedFrame?>(reads++ == 0 ? frame : null));
        Guid sessionId = Guid.NewGuid();
        Task sending = sender.SendAsync(sessionId, writer, source, clock, Budget, CancellationToken.None);
        try
        {
            await stream.WriteBlocked.Task.WaitAsync(Watchdog);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                sender.SendAsync(sessionId, writer, source, clock, Budget, CancellationToken.None));
            Assert.False(sending.IsCompleted);
            Assert.Equal(1, source.Calls);
            Assert.Equal(0, owner.DisposeCalls);
            Assert.Equal(0, stream.DisposeCalls);
            Assert.Equal(0, stream.FlushCalls);
            stream.ReleaseWrite();
            await sending.WaitAsync(Watchdog);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                sender.SendAsync(sessionId, writer, source, clock, Budget, CancellationToken.None));
            Assert.Equal(2, source.Calls);
            Assert.Equal(1, owner.DisposeCalls);
            Assert.Equal(1, stream.DisposeCalls);
        }
        finally
        {
            stream.ReleaseWrite();
            await Record.ExceptionAsync(() => sending);
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(39, false)]
    [InlineData(40, false)]
    [InlineData(41, false)]
    [InlineData(44, false)]
    [InlineData(45, true)]
    public async Task Write_And_Flush_Failures_Release_Exactly_Once_And_Stop_Reading(int boundary, bool flush)
    {
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = CreateFrame(owner);
        RecordingVideoStream stream = new() { FailWriteAt = flush ? int.MaxValue : boundary, FailFlush = flush };
        using VideoFrameWriter writer = new(stream);
        ManualDeadlineClock clock = new();
        VideoFrameSender sender = new();
        DelegateSource source = new((_, _) => ValueTask.FromResult<EncodedFrame?>(frame));

        IOException error = await Assert.ThrowsAsync<IOException>(() =>
            sender.SendAsync(Guid.NewGuid(), writer, source, clock, Budget, CancellationToken.None));

        if (flush)
        {
            Assert.Equal("测试刷新失败。", error.Message);
        }
        else
        {
            Assert.Same(stream.WriteFailure, error);
        }
        Assert.Equal(VideoFrameTestData.Wire()[..boundary], stream.Written);
        Assert.Equal(flush ? 1 : 0, stream.FlushCalls);
        Assert.Equal(1, source.Calls);
        Assert.Equal(1, owner.DisposeCalls);
        Assert.Equal(1, stream.DisposeCalls);
        Assert.Equal(0, clock.TimerCount);
        Assert.Empty(sender.CleanupErrors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(39)]
    [InlineData(40)]
    [InlineData(41)]
    [InlineData(44)]
    public async Task Caller_Cancellation_Reaches_Whole_Frame_Linked_Token_And_Releases_Once(int boundary)
    {
        using CancellationTokenSource caller = new();
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = CreateFrame(owner);
        RecordingVideoStream stream = new() { GateWriteAt = boundary };
        using VideoFrameWriter writer = new(stream);
        ManualDeadlineClock clock = new();
        VideoFrameSender sender = new();
        DelegateSource source = new((_, token) =>
        {
            Assert.Equal(caller.Token, token);
            return ValueTask.FromResult<EncodedFrame?>(frame);
        });
        Task sending = sender.SendAsync(Guid.NewGuid(), writer, source, clock, Budget, caller.Token);
        try
        {
            await stream.WriteBlocked.Task.WaitAsync(Watchdog);
            Assert.Equal(0, owner.DisposeCalls);
            caller.Cancel();
            OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
            Assert.Equal(stream.Tokens[0], error.CancellationToken);
            Assert.NotEqual(caller.Token, error.CancellationToken);
            Assert.All(stream.Tokens, token => Assert.True(token.IsCancellationRequested));
            Assert.Equal(VideoFrameTestData.Wire()[..boundary], stream.Written);
            Assert.Equal(1, source.Calls);
            Assert.Equal(1, owner.DisposeCalls);
            Assert.Equal(1, stream.DisposeCalls);
            Assert.Equal(0, clock.TimerCount);
        }
        finally
        {
            stream.ReleaseWrite();
            caller.Cancel();
            await Record.ExceptionAsync(() => sending);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_Frame_Or_Eof_After_Cancellation_Is_Joined_And_Never_Written(bool eof)
    {
        using CancellationTokenSource caller = new();
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = CreateFrame(owner);
        RecordingVideoStream stream = new();
        using VideoFrameWriter writer = new(stream);
        ProbeClock clock = new(new ManualDeadlineClock());
        TaskCompletionSource<EncodedFrame?> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DelegateSource source = new((_, _) => new ValueTask<EncodedFrame?>(pending.Task));
        VideoFrameSender sender = new();
        Task sending = sender.SendAsync(Guid.NewGuid(), writer, source, clock, Budget, caller.Token);
        try
        {
            Assert.Equal(1, source.Calls);
            caller.Cancel();
            Assert.False(sending.IsCompleted);
            Assert.Equal(0, owner.DisposeCalls);
            Assert.Equal(0, stream.DisposeCalls);
            pending.SetResult(eof ? null : frame);
            OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
            Assert.Equal(caller.Token, error.CancellationToken);
            Assert.Equal(eof ? 0 : 1, owner.DisposeCalls);
            Assert.Equal(0, clock.TimerCreations);
            Assert.Empty(stream.WriteRequests);
            Assert.Equal(1, stream.DisposeCalls);
        }
        finally
        {
            pending.TrySetResult(eof ? null : frame);
            await Record.ExceptionAsync(() => sending);
        }
    }

    [Fact]
    public async Task Blocking_Source_Prefix_Is_Supervised_Externally_And_Not_Abandoned_On_Cancellation()
    {
        using CancellationTokenSource caller = new();
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = CreateFrame(owner);
        RecordingVideoStream stream = new();
        using VideoFrameWriter writer = new(stream);
        ManualDeadlineClock clock = new();
        VideoFrameSender sender = new();
        DelegateSource source = new((_, _) =>
        {
            entered.TrySetResult();
            release.Wait();
            return ValueTask.FromResult<EncodedFrame?>(frame);
        });
        // 模拟上层 Router 的监督入口，sender 内部不得再另起无人 join 的任务。
        Task sending = Task.Run(() => sender.SendAsync(Guid.NewGuid(), writer, source, clock, Budget, caller.Token));
        try
        {
            await entered.Task.WaitAsync(Watchdog);
            clock.Advance(Budget + Budget);
            caller.Cancel();
            Assert.False(sending.IsCompleted);
            Assert.Equal(0, owner.DisposeCalls);
            Assert.Equal(0, clock.TimerCount);
            release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
            Assert.Equal(1, owner.DisposeCalls);
            Assert.Empty(stream.WriteRequests);
            Assert.Equal(1, stream.DisposeCalls);
        }
        finally
        {
            release.Set();
            await Record.ExceptionAsync(() => sending);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Uncooperative_Write_Or_Flush_Keeps_Owner_Until_Joined(bool flush, bool expire)
    {
        using CancellationTokenSource caller = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = CreateFrame(owner);
        RecordingVideoStream resource = new();
        HookVideoStream stream = new(resource);
        CancellationToken activeToken = default;
        async Task Block(CancellationToken token)
        {
            activeToken = token;
            entered.TrySetResult();
            await release.Task;
            Assert.Equal(0, owner.DisposeCalls);
        }
        if (flush)
        {
            stream.BeforeFlush = Block;
        }
        else
        {
            stream.BeforeWrite = async (bytes, token) =>
            {
                if (bytes.Length == 5)
                {
                    await Block(token);
                    Assert.Equal(VideoFrameTestData.Payload(), bytes.ToArray());
                }
            };
        }
        using VideoFrameWriter writer = new(stream);
        ManualDeadlineClock clock = new();
        VideoFrameSender sender = new();
        DelegateSource source = new((_, _) => ValueTask.FromResult<EncodedFrame?>(frame));
        Task sending = sender.SendAsync(Guid.NewGuid(), writer, source, clock, Budget, caller.Token);
        try
        {
            await entered.Task.WaitAsync(Watchdog);
            Assert.Equal(1, clock.TimerCount);
            if (expire)
            {
                clock.Advance(Budget);
            }
            else
            {
                caller.Cancel();
            }
            Assert.True(activeToken.IsCancellationRequested);
            Assert.Equal(!expire, caller.IsCancellationRequested);
            Assert.False(sending.IsCompleted);
            Assert.Equal(0, owner.DisposeCalls);
            Assert.Equal(0, resource.DisposeCalls);
            Assert.Equal(1, source.Calls);
            Assert.Equal(flush ? 45 : 40, resource.BytesWritten);
            release.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
            Assert.Equal(1, owner.DisposeCalls);
            Assert.Equal(1, resource.DisposeCalls);
            Assert.Equal(0, clock.TimerCount);
        }
        finally
        {
            release.TrySetResult();
            caller.Cancel();
            await Record.ExceptionAsync(() => sending);
        }
    }

    [Fact]
    public async Task Shared_Source_Separates_Sessions_And_Cancellation_Without_Being_Disposed()
    {
        Guid firstId = Guid.NewGuid();
        Guid secondId = Guid.NewGuid();
        using CancellationTokenSource firstCaller = new();
        using CancellationTokenSource secondCaller = new();
        RecordingVideoOwner firstOwner = new(32);
        RecordingVideoOwner secondOwner = new(32);
        using EncodedFrame firstFrame = CreateFrame(firstOwner);
        using EncodedFrame secondFrame = CreateFrame(secondOwner);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingVideoStream firstStream = new();
        RecordingVideoStream secondStream = new();
        using VideoFrameWriter firstWriter = new(firstStream);
        using VideoFrameWriter secondWriter = new(secondStream);
        int firstReads = 0;
        int secondReads = 0;
        DelegateSource source = new(async (id, token) =>
        {
            if (id == firstId)
            {
                Assert.Equal(firstCaller.Token, token);
                Interlocked.Increment(ref firstReads);
                await release.Task;
                return firstFrame;
            }
            Assert.Equal(secondId, id);
            Assert.Equal(secondCaller.Token, token);
            if (Interlocked.Increment(ref secondReads) > 1)
            {
                return null;
            }
            await release.Task;
            return secondFrame;
        });
        Task first = new VideoFrameSender().SendAsync(firstId, firstWriter, source,
            new ManualDeadlineClock(), Budget, firstCaller.Token);
        Task second = new VideoFrameSender().SendAsync(secondId, secondWriter, source,
            new ManualDeadlineClock(), Budget, secondCaller.Token);
        try
        {
            Assert.Equal(2, source.Calls);
            firstCaller.Cancel();
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            release.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            await second.WaitAsync(Watchdog);
            Assert.Equal(1, firstReads);
            Assert.Equal(2, secondReads);
            Assert.Empty(firstStream.Written);
            Assert.Equal(VideoFrameTestData.Wire(), secondStream.Written);
            Assert.Equal(1, firstOwner.DisposeCalls);
            Assert.Equal(1, secondOwner.DisposeCalls);
            Assert.Equal(1, firstStream.DisposeCalls);
            Assert.Equal(1, secondStream.DisposeCalls);
            Assert.False(secondCaller.IsCancellationRequested);
            Assert.Equal(0, source.DisposeCalls);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(Record.ExceptionAsync(() => first), Record.ExceptionAsync(() => second));
        }
    }

    [Theory]
    [InlineData("sync")]
    [InlineData("async")]
    [InlineData("cancel")]
    public async Task Source_Fault_Preserves_Original_Instance_Despite_Writer_Cleanup_Fault(string mode)
    {
        using CancellationTokenSource caller = new();
        Exception primary = mode == "cancel"
            ? new OperationCanceledException("来源原始取消。", caller.Token)
            : new IOException("来源原始故障。");
        DelegateSource source = mode == "sync"
            ? new((_, _) => throw primary)
            : new(async (_, _) => { await Task.Yield(); throw primary; });
        RecordingVideoStream stream = new() { FailDispose = true };
        using VideoFrameWriter writer = new(stream);
        VideoFrameSender sender = new();

        Exception? actual = await Record.ExceptionAsync(() => sender.SendAsync(
            Guid.NewGuid(), writer, source, new ManualDeadlineClock(), Budget, caller.Token));

        Assert.Same(primary, actual);
        if (mode == "cancel")
        {
            Assert.Equal(caller.Token, Assert.IsType<OperationCanceledException>(actual).CancellationToken);
        }
        Assert.Same(Assert.Single(writer.CleanupErrors), Assert.Single(sender.CleanupErrors));
        Assert.Equal(1, source.Calls);
        Assert.Empty(stream.WriteRequests);
        Assert.Equal(1, stream.DisposeCalls);
        Assert.Equal(0, source.DisposeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Write_Fault_Wins_Over_Both_Cleanup_Faults_And_Diagnostics_Are_Bounded_Snapshots(bool canceled)
    {
        using CancellationTokenSource caller = new();
        Exception primary = canceled
            ? new OperationCanceledException("原始写出取消。", caller.Token)
            : new IOException("原始写出故障。");
        IOException frameCleanup = new("帧释放故障。");
        RecordingVideoOwner owner = new(32) { DisposeFailure = frameCleanup };
        using EncodedFrame frame = CreateFrame(owner);
        RecordingVideoStream stream = new()
        {
            FailWriteAt = 41,
            WriteFailure = primary,
            BeforeIoFailure = canceled ? caller.Cancel : null,
            FailDispose = true,
        };
        using VideoFrameWriter writer = new(stream);
        VideoFrameSender sender = new();
        DelegateSource source = new((_, _) => ValueTask.FromResult<EncodedFrame?>(frame));
        ManualDeadlineClock clock = new();
        IReadOnlyList<Exception> before = sender.CleanupErrors;

        Exception? actual = await Record.ExceptionAsync(() =>
            sender.SendAsync(Guid.NewGuid(), writer, source, clock, Budget, caller.Token));

        Assert.Same(primary, actual);
        if (canceled)
        {
            Assert.Equal(caller.Token, Assert.IsType<OperationCanceledException>(actual).CancellationToken);
        }
        IReadOnlyList<Exception> errors = sender.CleanupErrors;
        Assert.Equal(2, errors.Count);
        Assert.Same(frameCleanup, errors[0]);
        Assert.Same(Assert.Single(writer.CleanupErrors), errors[1]);
        Assert.Empty(before);
        VideoReviewDiagnostics.AssertReadOnly(errors);
        for (int i = 0; i < 32; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                sender.SendAsync(Guid.NewGuid(), writer, source, clock, Budget, CancellationToken.None));
        }
        Assert.Equal(errors, sender.CleanupErrors);
        Assert.Equal(1, owner.DisposeCalls);
        Assert.Equal(1, stream.DisposeCalls);
        Assert.Equal(1, source.Calls);
        Assert.Equal(0, clock.TimerCount);
    }

    [Fact]
    public async Task Frame_Cleanup_Only_Fault_Is_Visible_And_Stops_Before_Another_Read()
    {
        IOException cleanup = new("帧释放故障。");
        RecordingVideoOwner owner = new(32) { DisposeFailure = cleanup };
        using EncodedFrame frame = CreateFrame(owner);
        RecordingVideoStream stream = new();
        using VideoFrameWriter writer = new(stream);
        VideoFrameSender sender = new();
        DelegateSource source = new((_, _) => ValueTask.FromResult<EncodedFrame?>(frame));

        Exception? actual = await Record.ExceptionAsync(() => sender.SendAsync(
            Guid.NewGuid(), writer, source, new ManualDeadlineClock(), Budget, CancellationToken.None));

        Assert.Same(cleanup, actual);
        Assert.Same(cleanup, Assert.Single(sender.CleanupErrors));
        Assert.Equal(VideoFrameTestData.Wire(), stream.Written);
        Assert.Equal(1, source.Calls);
        Assert.Equal(1, owner.DisposeCalls);
        Assert.Equal(1, stream.DisposeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Writer_Cleanup_Only_Fault_Is_Visible_After_Eof(bool sendFrame)
    {
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = CreateFrame(owner);
        RecordingVideoStream stream = new() { FailDispose = true };
        using VideoFrameWriter writer = new(stream);
        VideoFrameSender sender = new();
        int reads = 0;
        DelegateSource source = new((_, _) => ValueTask.FromResult<EncodedFrame?>(sendFrame && reads++ == 0 ? frame : null));

        Exception? actual = await Record.ExceptionAsync(() => sender.SendAsync(
            Guid.NewGuid(), writer, source, new ManualDeadlineClock(), Budget, CancellationToken.None));

        Assert.Same(Assert.Single(writer.CleanupErrors), actual);
        Assert.Same(actual, Assert.Single(sender.CleanupErrors));
        Assert.Equal(sendFrame ? 1 : 0, owner.DisposeCalls);
        Assert.Equal(1, stream.DisposeCalls);
        Assert.Equal(sendFrame ? 2 : 1, source.Calls);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Exact_Deadline_After_Flush_Uses_Monotonic_Time_Without_Timer_Dispatch(int offsetTicks)
    {
        ManualDeadlineClock clock = new();
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = CreateFrame(owner);
        RecordingVideoStream resource = new();
        HookVideoStream stream = new(resource)
        {
            AfterFlush = () =>
            {
                clock.AdvanceUtc(TimeSpan.FromDays(-365));
                clock.Advance(TimeSpan.FromTicks(Budget.Ticks + offsetTicks), fireTimers: false);
            },
        };
        using VideoFrameWriter writer = new(stream);
        VideoFrameSender sender = new();
        int reads = 0;
        DelegateSource source = new((_, _) => ValueTask.FromResult<EncodedFrame?>(reads++ == 0 ? frame : null));

        Exception? error = await Record.ExceptionAsync(() =>
            sender.SendAsync(Guid.NewGuid(), writer, source, clock, Budget, CancellationToken.None));

        if (offsetTicks < 0)
        {
            Assert.Null(error);
            Assert.Equal(2, source.Calls);
        }
        else
        {
            Assert.IsAssignableFrom<OperationCanceledException>(error);
            Assert.Equal(1, source.Calls);
        }
        Assert.Equal(3, resource.Tokens.Count);
        Assert.All(resource.Tokens, token => Assert.False(token.IsCancellationRequested));
        Assert.Equal(VideoFrameTestData.Wire(), resource.Written);
        Assert.Equal(1, resource.FlushCalls);
        Assert.Equal(1, owner.DisposeCalls);
        Assert.Equal(1, resource.DisposeCalls);
        Assert.Equal(0, clock.TimerCount);
    }

    [Fact]
    public async Task Header_Payload_And_Flush_Share_One_Budget_And_One_Linked_Token()
    {
        ManualDeadlineClock manual = new();
        ProbeClock clock = new(manual);
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = CreateFrame(owner);
        RecordingVideoStream resource = new();
        HookVideoStream stream = new(resource)
        {
            BeforeWrite = (_, _) =>
            {
                Assert.Equal(1, manual.TimerCount);
                manual.Advance(TimeSpan.FromSeconds(4), fireTimers: false);
                return ValueTask.CompletedTask;
            },
            AfterFlush = () => manual.Advance(TimeSpan.FromSeconds(2), fireTimers: false),
        };
        using VideoFrameWriter writer = new(stream);
        VideoFrameSender sender = new();
        DelegateSource source = new((_, _) => ValueTask.FromResult<EncodedFrame?>(frame));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sender.SendAsync(Guid.NewGuid(), writer, source, clock, Budget, CancellationToken.None));

        Assert.Equal(1, clock.TimerCreations);
        Assert.Equal(3, resource.Tokens.Count);
        Assert.All(resource.Tokens, token => Assert.Equal(resource.Tokens[0], token));
        Assert.All(resource.Tokens, token => Assert.False(token.IsCancellationRequested));
        Assert.Equal(VideoFrameTestData.Wire(), resource.Written);
        Assert.Equal(1, source.Calls);
        Assert.Equal(1, owner.DisposeCalls);
        Assert.Equal(0, manual.TimerCount);
    }

    [Theory]
    [InlineData("before-deadline")]
    [InlineData("creating-deadline")]
    [InlineData("before-write")]
    [InlineData("after-write")]
    public async Task Deadline_Boundaries_Reject_Time_Consumed_While_Reading_Clock_Without_Timer_Dispatch(string stage)
    {
        ManualDeadlineClock manual = new();
        ProbeClock clock = new(manual);
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = CreateFrame(owner);
        RecordingVideoStream resource = new();
        HookVideoStream stream = new(resource);
        Action expire = () => manual.Advance(Budget, fireTimers: false);
        if (stage == "before-deadline")
        {
            clock.AfterNextTimestamp = expire;
        }
        else if (stage == "creating-deadline")
        {
            clock.DuringTimerCreation = expire;
        }
        else if (stage == "before-write")
        {
            // 构造器最后一次 Remaining 取到旧值后推进时间：构造器的 timer 仍未取消。
            clock.DuringTimerCreation = () => clock.AfterNextTimestamp = expire;
        }
        else
        {
            stream.AfterFlush = () => clock.BeforeNextTimestamp = expire;
        }
        using VideoFrameWriter writer = new(stream);
        VideoFrameSender sender = new();
        DelegateSource source = new((_, _) => ValueTask.FromResult<EncodedFrame?>(frame));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sender.SendAsync(Guid.NewGuid(), writer, source, clock, Budget, CancellationToken.None));

        Assert.Equal(stage == "after-write" ? 45 : 0, resource.BytesWritten);
        Assert.Equal(stage == "before-deadline" ? 0 : 1, clock.TimerCreations);
        Assert.All(resource.Tokens, token => Assert.False(token.IsCancellationRequested));
        Assert.Equal(1, source.Calls);
        Assert.Equal(1, owner.DisposeCalls);
        Assert.Equal(1, resource.DisposeCalls);
        Assert.Equal(0, manual.TimerCount);
    }

    [Theory]
    [InlineData("before-deadline")]
    [InlineData("creating-deadline")]
    [InlineData("before-write")]
    [InlineData("after-write")]
    public async Task Caller_Cancellation_Is_Rechecked_After_Reentrant_Clock_Access(string stage)
    {
        using CancellationTokenSource caller = new();
        ManualDeadlineClock manual = new();
        ProbeClock clock = new(manual);
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = CreateFrame(owner);
        RecordingVideoStream resource = new();
        HookVideoStream stream = new(resource);
        if (stage == "before-deadline")
        {
            clock.AfterNextTimestamp = caller.Cancel;
        }
        else if (stage == "creating-deadline")
        {
            clock.DuringTimerCreation = caller.Cancel;
        }
        else if (stage == "before-write")
        {
            clock.DuringTimerCreation = () => clock.AfterNextTimestamp = caller.Cancel;
        }
        else
        {
            stream.AfterFlush = () => clock.BeforeNextTimestamp = caller.Cancel;
        }
        using VideoFrameWriter writer = new(stream);
        VideoFrameSender sender = new();
        DelegateSource source = new((_, _) => ValueTask.FromResult<EncodedFrame?>(frame));

        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sender.SendAsync(Guid.NewGuid(), writer, source, clock, Budget, caller.Token));

        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Equal(stage == "after-write" ? 45 : 0, resource.BytesWritten);
        Assert.Equal(stage == "before-deadline" ? 0 : 1, clock.TimerCreations);
        Assert.Equal(1, source.Calls);
        Assert.Equal(1, owner.DisposeCalls);
        Assert.Equal(1, resource.DisposeCalls);
        Assert.Equal(0, manual.TimerCount);
    }

    [Fact]
    public async Task Zero_Budget_Takes_Ownership_But_Writes_Nothing_And_Creates_No_Timer()
    {
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = CreateFrame(owner);
        RecordingVideoStream stream = new();
        using VideoFrameWriter writer = new(stream);
        ProbeClock clock = new(new ManualDeadlineClock());
        DelegateSource source = new((_, _) => ValueTask.FromResult<EncodedFrame?>(frame));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new VideoFrameSender().SendAsync(
            Guid.NewGuid(), writer, source, clock, TimeSpan.Zero, CancellationToken.None));

        Assert.Empty(stream.WriteRequests);
        Assert.Equal(0, clock.TimerCreations);
        Assert.Equal(1, owner.DisposeCalls);
        Assert.Equal(1, stream.DisposeCalls);
    }

    private static EncodedFrame CreateFrame(RecordingVideoOwner owner)
    {
        VideoFrameTestData.Payload().CopyTo(owner.Bytes, 0);
        return VideoFrameTestData.Frame(owner);
    }

    private sealed class DelegateSource(Func<Guid, CancellationToken, ValueTask<EncodedFrame?>> read) : IVideoFrameSource, IDisposable
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        internal int DisposeCalls { get; private set; }

        public ValueTask<EncodedFrame?> ReadNextAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return read(sessionId, cancellationToken);
        }

        public void Dispose() => DisposeCalls++;
    }

    private sealed class ProbeClock(ManualDeadlineClock inner) : TimeProvider
    {
        internal Action? BeforeNextTimestamp { get; set; }
        internal Action? AfterNextTimestamp { get; set; }
        internal Action? DuringTimerCreation { get; set; }
        internal int TimerCreations { get; private set; }
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long GetTimestamp()
        {
            Action? before = BeforeNextTimestamp;
            BeforeNextTimestamp = null;
            before?.Invoke();
            long timestamp = inner.GetTimestamp();
            Action? after = AfterNextTimestamp;
            AfterNextTimestamp = null;
            after?.Invoke();
            return timestamp;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            TimerCreations++;
            ITimer timer = inner.CreateTimer(callback, state, dueTime, period);
            DuringTimerCreation?.Invoke();
            return timer;
        }
    }

    // 沿用记录流，仅增加 I/O 边界钩子；不配合取消的 gate 必须由测试 finally 放行并 join。
    private sealed class HookVideoStream(RecordingVideoStream inner) : Stream
    {
        internal Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>? BeforeWrite { get; set; }
        internal Func<CancellationToken, Task>? BeforeFlush { get; set; }
        internal Action? AfterFlush { get; set; }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (BeforeWrite is not null)
            {
                await BeforeWrite(buffer, cancellationToken);
            }
            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (BeforeFlush is not null)
            {
                await BeforeFlush(cancellationToken);
            }
            await inner.FlushAsync(cancellationToken);
            AfterFlush?.Invoke();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => false;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
