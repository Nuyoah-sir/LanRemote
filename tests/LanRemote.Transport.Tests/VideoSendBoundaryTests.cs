using System.Buffers.Binary;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;

namespace LanRemote.Transport.Tests;

public sealed class VideoSendBoundaryTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData(1, "success")]
    [InlineData(2, "success")]
    [InlineData(3, "success")]
    [InlineData(1, "fault")]
    [InlineData(2, "fault")]
    [InlineData(3, "fault")]
    [InlineData(1, "cancel")]
    [InlineData(2, "cancel")]
    [InlineData(3, "cancel")]
    [InlineData(1, "deadline")]
    [InlineData(2, "deadline")]
    [InlineData(3, "deadline")]
    [InlineData(1, "closed")]
    [InlineData(2, "closed")]
    [InlineData(3, "closed")]
    public async Task Original_Header_Payload_And_Flush_Tails_Must_Succeed_Before_Publication(int boundary, string outcome)
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        using CancellationTokenSource caller = new();
        ManualDeadlineClock clock = new();
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = Frame(owner, 640, 360);
        Producer producer = Frames(frame);
        RecordingVideoStream resource = new();
        WireGate gate = new();
        IOException failure = new("原 I/O 尾部失败。");
        HookStream stream = new(resource)
        {
            AfterOperation = async (operation, token) =>
            {
                if (operation != boundary) return;
                await gate.HoldAsync(token);
                if (outcome == "fault") throw failure;
            },
        };
        using VideoFrameWriter writer = new(stream);
        VideoFrameSender sender = new(7, source);
        Task installing = source.ReplaceAsync(producer, out var generation);
        Task? sending = null;
        try
        {
            await installing.WaitAsync(Watchdog);
            Assert.Null(sender.LastSuccessfulGeneration);
            sending = sender.SendAsync(session, writer, source, clock, Budget, caller.Token);
            await gate.Entered.Task.WaitAsync(Watchdog);
            Assert.Null(sender.LastSuccessfulGeneration);
            Assert.Equal(boundary == 1 ? 40 : 45, resource.BytesWritten);
            Assert.Equal(boundary == 3 ? 1 : 0, resource.FlushCalls);
            Assert.Equal(1, producer.ReadCalls);
            Assert.Equal(0, owner.DisposeCalls);
            if (outcome == "cancel") caller.Cancel();
            if (outcome == "deadline") clock.Advance(Budget);
            if (outcome == "closed") writer.Dispose();
            Assert.Equal(outcome is "cancel" or "deadline", gate.Token.IsCancellationRequested);
            Assert.False(sending.IsCompleted);
            Assert.Null(sender.LastSuccessfulGeneration);
            Assert.Equal(0, owner.DisposeCalls);
            gate.Release.TrySetResult();
            Exception? error = await ObserveAsync(sending);
            if (outcome == "success")
            {
                Assert.Null(error);
                AssertSnapshot(sender, session, generation, 7, 640, 360, 7, 640, 360);
                Assert.Equal(2, producer.ReadCalls);
            }
            else
            {
                if (outcome == "fault") Assert.Same(failure, error);
                else if (outcome == "closed") Assert.IsType<ObjectDisposedException>(error);
                else Assert.IsAssignableFrom<OperationCanceledException>(error);
                Assert.Null(sender.LastSuccessfulGeneration);
                Assert.Equal(1, producer.ReadCalls);
            }
            Assert.Equal(1, owner.DisposeCalls);
            Assert.Equal(1, resource.DisposeCalls);
            Assert.Equal(0, clock.TimerCount);
        }
        finally
        {
            gate.Release.TrySetResult();
            caller.Cancel();
            await FinishAsync(source, installing, sending);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Successful_Writer_Task_Still_Needs_Senders_Final_Monotonic_Budget_Check(int offsetTicks)
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        ManualDeadlineClock clock = new();
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = Frame(owner, 800, 600);
        Producer producer = Frames(frame);
        RecordingVideoStream resource = new();
        HookStream stream = new(resource)
        {
            AfterOperation = (operation, _) =>
            {
                if (operation == 3)
                {
                    clock.AdvanceUtc(TimeSpan.FromDays(-365));
                    clock.Advance(TimeSpan.FromTicks(Budget.Ticks + offsetTicks), fireTimers: false);
                }
                return Task.CompletedTask;
            },
        };
        using VideoFrameWriter writer = new(stream);
        VideoFrameSender sender = new(0, source);
        Task installing = source.ReplaceAsync(producer, out var generation);
        Task? sending = null;
        try
        {
            await installing.WaitAsync(Watchdog);
            sending = sender.SendAsync(session, writer, source, clock, Budget, default);
            Exception? error = await ObserveAsync(sending);
            if (offsetTicks < 0)
            {
                Assert.Null(error);
                AssertSnapshot(sender, session, generation, 0, 800, 600, 0, 800, 600);
            }
            else
            {
                Assert.IsAssignableFrom<OperationCanceledException>(error);
                Assert.Null(sender.LastSuccessfulGeneration);
            }
            Assert.Equal(Wire(0, 800, 600), resource.Written);
            Assert.Equal(1, resource.FlushCalls);
            Assert.All(resource.Tokens, token => Assert.False(token.IsCancellationRequested));
            Assert.Equal(offsetTicks < 0 ? 2 : 1, producer.ReadCalls);
            Assert.Equal(1, owner.DisposeCalls);
            Assert.Equal(0, clock.TimerCount);
        }
        finally { await FinishAsync(source, installing, sending); }
    }

    [Fact]
    public async Task Consecutive_Successes_Keep_First_And_Update_Latest_Without_Mutating_Old_Snapshots()
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        RecordingVideoOwner a = new(32), b = new(32), c = new(32);
        using EncodedFrame first = Frame(a, 640, 360);
        using EncodedFrame second = Frame(b, 1280, 720);
        using EncodedFrame third = Frame(c, 1920, 1080);
        ReadSlot[] slots = [new(), new(), new(), new()];
        Producer producer = new((read, _) => slots[read].Read());
        RecordingVideoStream stream = new();
        using VideoFrameWriter writer = new(stream);
        VideoFrameSender sender = new(100, source);
        Task installing = source.ReplaceAsync(producer, out var generation);
        Task? sending = null;
        try
        {
            await installing.WaitAsync(Watchdog);
            sending = sender.SendAsync(session, writer, source, new ManualDeadlineClock(), Budget, default);
            await slots[0].Entered.Task.WaitAsync(Watchdog);
            Assert.Null(sender.LastSuccessfulGeneration);
            slots[0].Result.SetResult(first);
            await slots[1].Entered.Task.WaitAsync(Watchdog);
            var firstSnapshot = AssertSnapshot(sender, session, generation, 100, 640, 360, 100, 640, 360);
            Assert.Equal(1, a.DisposeCalls);
            slots[1].Result.SetResult(second);
            await slots[2].Entered.Task.WaitAsync(Watchdog);
            var secondSnapshot = AssertSnapshot(sender, session, generation, 100, 640, 360, 101, 1280, 720);
            Assert.NotSame(firstSnapshot, secondSnapshot);
            Assert.Equal(new VideoFrameSender.SentFrame(100, 640, 360), firstSnapshot.Latest);
            Assert.Equal(1, b.DisposeCalls);
            slots[2].Result.SetResult(third);
            await slots[3].Entered.Task.WaitAsync(Watchdog);
            var thirdSnapshot = AssertSnapshot(sender, session, generation, 100, 640, 360, 102, 1920, 1080);
            Assert.NotSame(secondSnapshot, thirdSnapshot);
            Assert.Equal(new VideoFrameSender.SentFrame(101, 1280, 720), secondSnapshot.Latest);
            Assert.Equal(1, c.DisposeCalls);
            slots[3].Result.SetResult(null);
            await sending.WaitAsync(Watchdog);
            Assert.Same(thirdSnapshot, sender.LastSuccessfulGeneration);
            Assert.Equal(Wire(100, 640, 360).Concat(Wire(101, 1280, 720)).Concat(Wire(102, 1920, 1080)).ToArray(), stream.Written);
        }
        finally
        {
            foreach (ReadSlot slot in slots) slot.Result.TrySetResult(null);
            await FinishAsync(source, installing, sending);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Failed_Later_Frame_Does_Not_Replace_Previous_Success(int boundary)
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        RecordingVideoOwner a = new(32), b = new(32);
        using EncodedFrame first = Frame(a, 640, 360);
        using EncodedFrame second = Frame(b, 1280, 720);
        Producer producer = Frames(first, second);
        RecordingVideoStream resource = new();
        WireGate gate = new();
        IOException failure = new("后续帧失败。");
        HookStream stream = new(resource)
        {
            AfterOperation = async (operation, token) =>
            {
                if (operation != 3 + boundary) return;
                await gate.HoldAsync(token);
                throw failure;
            },
        };
        using VideoFrameWriter writer = new(stream);
        VideoFrameSender sender = new(8, source);
        Task installing = source.ReplaceAsync(producer, out var generation);
        Task? sending = null;
        try
        {
            await installing.WaitAsync(Watchdog);
            sending = sender.SendAsync(session, writer, source, new ManualDeadlineClock(), Budget, default);
            await gate.Entered.Task.WaitAsync(Watchdog);
            var before = AssertSnapshot(sender, session, generation, 8, 640, 360, 8, 640, 360);
            Assert.Equal(1, a.DisposeCalls);
            Assert.Equal(0, b.DisposeCalls);
            gate.Release.SetResult();
            Assert.Same(failure, await ObserveAsync(sending));
            Assert.Same(before, sender.LastSuccessfulGeneration);
            Assert.Equal(1, b.DisposeCalls);
            Assert.Equal(2, producer.ReadCalls);
        }
        finally
        {
            gate.Release.TrySetResult();
            await FinishAsync(source, installing, sending);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Replacement_Is_Not_A_Send_Barrier_And_Zero_Frame_Generations_Do_Not_Replace_History(int boundary)
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        RecordingVideoOwner a = new(32), b = new(32), c = new(32);
        using EncodedFrame first = Frame(a, 640, 360);
        using EncodedFrame oldInFlight = Frame(b, 800, 600);
        using EncodedFrame newFrame = Frame(c, 1920, 1080);
        Producer old = Frames(first, oldInFlight);
        Producer skippedA = Frames(), skippedB = Frames();
        ReadSlot newRead = new(), eof = new();
        Producer next = new((read, _) => (read == 0 ? newRead : eof).Read());
        RecordingVideoStream resource = new();
        WireGate gate = new();
        HookStream stream = new(resource)
        {
            AfterOperation = (operation, token) => operation == 3 + boundary ? gate.HoldAsync(token) : Task.CompletedTask,
        };
        using VideoFrameWriter writer = new(stream);
        VideoFrameSender sender = new(40, source);
        Task installing = source.ReplaceAsync(old, out var oldGeneration);
        Task? sending = null, skipA = null, skipB = null, replacing = null;
        try
        {
            await installing.WaitAsync(Watchdog);
            sending = sender.SendAsync(session, writer, source, new ManualDeadlineClock(), Budget, default);
            await gate.Entered.Task.WaitAsync(Watchdog);
            var before = AssertSnapshot(sender, session, oldGeneration, 40, 640, 360, 40, 640, 360);
            skipA = source.ReplaceAsync(skippedA, out var skippedGenerationA);
            await skipA.WaitAsync(Watchdog);
            Assert.Same(before, sender.LastSuccessfulGeneration);
            skipB = source.ReplaceAsync(skippedB, out var skippedGenerationB);
            await skipB.WaitAsync(Watchdog);
            Assert.Same(before, sender.LastSuccessfulGeneration);
            replacing = source.ReplaceAsync(next, out var nextGeneration);
            await replacing.WaitAsync(Watchdog);
            Assert.Same(before, sender.LastSuccessfulGeneration);
            Assert.NotSame(oldGeneration, skippedGenerationA);
            Assert.NotSame(skippedGenerationA, skippedGenerationB);
            Assert.NotSame(skippedGenerationB, nextGeneration);
            Assert.Equal(0, skippedA.ReadCalls);
            Assert.Equal(0, skippedB.ReadCalls);
            Assert.Equal(0, next.ReadCalls);
            Assert.Equal(1, old.StopCalls);
            Assert.Equal(1, old.DisposeCalls);
            Assert.Equal(0, b.DisposeCalls);
            Assert.False(gate.Token.IsCancellationRequested);
            Assert.False(sending.IsCompleted);
            gate.Release.SetResult();
            await newRead.Entered.Task.WaitAsync(Watchdog);
            var oldSuccess = AssertSnapshot(sender, session, oldGeneration, 40, 640, 360, 41, 800, 600);
            Assert.Equal(1, b.DisposeCalls);
            newRead.Result.SetResult(newFrame);
            await eof.Entered.Task.WaitAsync(Watchdog);
            var newSuccess = AssertSnapshot(sender, session, nextGeneration, 42, 1920, 1080, 42, 1920, 1080);
            Assert.Equal(oldGeneration, oldSuccess.Generation);
            Assert.Equal(new VideoFrameSender.SentFrame(41, 800, 600), oldSuccess.Latest);
            Assert.NotSame(oldSuccess, newSuccess);
            eof.Result.SetResult(null);
            await sending.WaitAsync(Watchdog);
            Assert.Same(newSuccess, sender.LastSuccessfulGeneration);
            Assert.Equal(Wire(40, 640, 360).Concat(Wire(41, 800, 600)).Concat(Wire(42, 1920, 1080)).ToArray(), resource.Written);
        }
        finally
        {
            gate.Release.TrySetResult();
            newRead.Result.TrySetResult(null);
            eof.Result.TrySetResult(null);
            await FinishAsync(source, installing, skipA, skipB, replacing, sending);
        }
    }

    [Fact]
    public async Task Retired_Late_Read_Is_Dropped_And_Does_Not_Mislabel_The_Next_Generations_Frame()
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        RecordingVideoOwner oldOwner = new(32), newOwner = new(32);
        using EncodedFrame late = Frame(oldOwner, 320, 200);
        using EncodedFrame current = Frame(newOwner, 1600, 900);
        ReadSlot oldRead = new(), newRead = new();
        Producer old = new((_, _) => oldRead.Read());
        Producer next = new((read, _) => read == 0 ? newRead.Read() : ValueTask.FromResult<EncodedFrame?>(null));
        RecordingVideoStream stream = new();
        using VideoFrameWriter writer = new(stream);
        VideoFrameSender sender = new(0, source);
        Task installing = source.ReplaceAsync(old, out var oldGeneration);
        Task? sending = null, replacing = null;
        try
        {
            await installing.WaitAsync(Watchdog);
            sending = sender.SendAsync(session, writer, source, new ManualDeadlineClock(), Budget, default);
            await oldRead.Entered.Task.WaitAsync(Watchdog);
            replacing = source.ReplaceAsync(next, out var nextGeneration);
            await old.StopEntered.Task.WaitAsync(Watchdog);
            Assert.False(replacing.IsCompleted);
            Assert.False(sending.IsCompleted);
            Assert.Null(sender.LastSuccessfulGeneration);
            oldRead.Result.SetResult(late);
            await replacing.WaitAsync(Watchdog);
            await newRead.Entered.Task.WaitAsync(Watchdog);
            Assert.Equal(1, oldOwner.DisposeCalls);
            Assert.Null(sender.LastSuccessfulGeneration);
            Assert.Empty(stream.Written);
            newRead.Result.SetResult(current);
            await sending.WaitAsync(Watchdog);
            var success = AssertSnapshot(sender, session, nextGeneration, 0, 1600, 900, 0, 1600, 900);
            Assert.NotSame(oldGeneration, success.Generation);
            Assert.Equal(Wire(0, 1600, 900), stream.Written);
            Assert.Equal(1, newOwner.DisposeCalls);
        }
        finally
        {
            oldRead.Result.TrySetResult(null);
            newRead.Result.TrySetResult(null);
            await FinishAsync(source, installing, replacing, sending);
        }
    }

    [Theory]
    [InlineData("source")]
    [InlineData("session")]
    public async Task Tracking_Rejects_Foreign_Source_Or_Session_Before_Reading(string mismatch)
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource bound = new(session);
        ReplaceableVideoFrameSource foreign = new(session);
        Producer first = Frames(), second = Frames();
        RecordingVideoStream stream = new();
        using VideoFrameWriter writer = new(stream);
        VideoFrameSender sender = new(0, bound);
        Task firstInstall = bound.ReplaceAsync(first);
        Task secondInstall = foreign.ReplaceAsync(second);
        Task? sending = null;
        try
        {
            await Task.WhenAll(firstInstall, secondInstall).WaitAsync(Watchdog);
            sending = sender.SendAsync(mismatch == "session" ? Guid.NewGuid() : session, writer,
                mismatch == "source" ? foreign : bound, new ManualDeadlineClock(), Budget, default);
            Assert.IsType<ArgumentException>(await ObserveAsync(sending));
            Assert.Equal(0, first.ReadCalls);
            Assert.Equal(0, second.ReadCalls);
            Assert.Null(sender.LastSuccessfulGeneration);
            Assert.Empty(stream.Written);
            Assert.Equal(1, stream.DisposeCalls);
            Assert.False(bound.Completion.IsCompleted);
            Assert.False(foreign.Completion.IsCompleted);
        }
        finally
        {
            await Task.WhenAll(FinishAsync(bound, firstInstall, sending), FinishAsync(foreign, secondInstall));
        }
    }

    [Fact]
    public void Tracking_Rejects_Null_Source()
    {
        Assert.Throws<ArgumentNullException>(() => new VideoFrameSender(0, null!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_Constructors_Do_Not_Enable_Tracking_Even_For_Replaceable_Source(bool sequential)
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = Frame(owner, 640, 360);
        Producer producer = Frames(frame);
        RecordingVideoStream stream = new();
        using VideoFrameWriter writer = new(stream);
        VideoFrameSender sender = sequential ? new(19) : new();
        Task installing = source.ReplaceAsync(producer);
        Task? sending = null;
        try
        {
            await installing.WaitAsync(Watchdog);
            sending = sender.SendAsync(session, writer, source, new ManualDeadlineClock(), Budget, default);
            await sending.WaitAsync(Watchdog);
            Assert.Null(sender.LastSuccessfulGeneration);
            Assert.Equal(Wire(sequential ? 19ul : frame.FrameId, 640, 360), stream.Written);
            Assert.Equal(1, owner.DisposeCalls);
        }
        finally { await FinishAsync(source, installing, sending); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaxValue_Success_Remains_After_Eof_Or_An_Extra_Frame_Exhausts_The_Sequence(bool extraFrame)
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        RecordingVideoOwner a = new(32), b = new(32);
        using EncodedFrame last = Frame(a, 640, 360);
        using EncodedFrame extra = Frame(b, 1920, 1080);
        ReadSlot following = new();
        Producer producer = new((read, _) => read == 0 ? ValueTask.FromResult<EncodedFrame?>(last) : following.Read());
        RecordingVideoStream stream = new();
        using VideoFrameWriter writer = new(stream);
        VideoFrameSender sender = new(ulong.MaxValue, source);
        Task installing = source.ReplaceAsync(producer, out var generation);
        Task? sending = null;
        try
        {
            await installing.WaitAsync(Watchdog);
            sending = sender.SendAsync(session, writer, source, new ManualDeadlineClock(), Budget, default);
            await following.Entered.Task.WaitAsync(Watchdog);
            var before = AssertSnapshot(sender, session, generation, ulong.MaxValue, 640, 360, ulong.MaxValue, 640, 360);
            following.Result.SetResult(extraFrame ? extra : null);
            Exception? error = await ObserveAsync(sending);
            if (extraFrame) Assert.IsType<InvalidOperationException>(error);
            else Assert.Null(error);
            Assert.Same(before, sender.LastSuccessfulGeneration);
            Assert.Equal(Wire(ulong.MaxValue, 640, 360), stream.Written);
            Assert.Equal(1, stream.FlushCalls);
            Assert.Equal(1, a.DisposeCalls);
            Assert.Equal(extraFrame ? 1 : 0, b.DisposeCalls);
            Assert.Equal(2, producer.ReadCalls);
        }
        finally
        {
            following.Result.TrySetResult(null);
            await FinishAsync(source, installing, sending);
        }
    }

    [Fact]
    public async Task Owner_Cleanup_Error_After_Whole_Frame_Success_Does_Not_Erase_The_Record()
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        IOException cleanup = new("成功帧 owner 释放失败。");
        RecordingVideoOwner owner = new(32) { DisposeFailure = cleanup };
        using EncodedFrame frame = Frame(owner, 800, 600);
        Producer producer = Frames(frame);
        RecordingVideoStream stream = new();
        using VideoFrameWriter writer = new(stream);
        VideoFrameSender sender = new(99, source);
        Task installing = source.ReplaceAsync(producer, out var generation);
        VideoFrameSender.GenerationSendSnapshot? atCleanup = null;
        owner.BeforeDispose = () => atCleanup = sender.LastSuccessfulGeneration;
        Task? sending = null;
        try
        {
            await installing.WaitAsync(Watchdog);
            sending = sender.SendAsync(session, writer, source, new ManualDeadlineClock(), Budget, default);
            Assert.Same(cleanup, await ObserveAsync(sending));
            var success = AssertSnapshot(sender, session, generation, 99, 800, 600, 99, 800, 600);
            Assert.Same(success, atCleanup);
            Assert.Same(cleanup, Assert.Single(sender.CleanupErrors));
            Assert.Null(sender.SendFailure);
            Assert.Equal(Wire(99, 800, 600), stream.Written);
            Assert.Equal(1, owner.DisposeCalls);
            Assert.Equal(1, producer.ReadCalls);
        }
        finally
        {
            await FinishAsync(source, installing, sending);
            owner.DisposeFailure = null;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_And_Tracked_Reads_Share_The_Same_Single_Reader_Guard(bool trackedFirst)
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = Frame(owner, 640, 360);
        ReadSlot slot = new();
        Producer producer = new((_, _) => slot.Read());
        Task installing = source.ReplaceAsync(producer, out var generation);
        Task<EncodedFrame?>? reading = null;
        ReplaceableVideoFrameSource.ReadOrigin? origin = null;
        try
        {
            await installing.WaitAsync(Watchdog);
            reading = (trackedFirst
                ? source.ReadForSendAsync(session, default, out origin)
                : source.ReadNextAsync(session, default)).AsTask();
            await slot.Entered.Task.WaitAsync(Watchdog);
            if (trackedFirst)
            {
                Assert.Null(origin!.Generation);
                Assert.Throws<InvalidOperationException>(() => { _ = source.ReadNextAsync(session, default); });
            }
            else Assert.Throws<InvalidOperationException>(() => { _ = source.ReadForSendAsync(session, default, out _); });
            Assert.Equal(1, producer.ReadCalls);
            Assert.Equal(0, owner.DisposeCalls);
            slot.Result.SetResult(frame);
            Assert.Same(frame, await reading.WaitAsync(Watchdog));
            if (trackedFirst) Assert.Same(generation, origin!.Generation);
        }
        finally
        {
            slot.Result.TrySetResult(null);
            await FinishAsync(source, installing, reading);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_Origin_Is_Empty_Until_A_NonNull_Frame_Is_Committed(bool eof)
    {
        Guid session = Guid.NewGuid();
        ReplaceableVideoFrameSource source = new(session);
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = Frame(owner, 640, 360);
        ReadSlot slot = new();
        Producer producer = new((_, _) => slot.Read());
        Task installing = source.ReplaceAsync(producer, out var generation);
        Task<EncodedFrame?>? reading = null;
        try
        {
            await installing.WaitAsync(Watchdog);
            reading = source.ReadForSendAsync(session, default, out var origin).AsTask();
            await slot.Entered.Task.WaitAsync(Watchdog);
            Assert.Null(origin.Generation);
            slot.Result.SetResult(eof ? null : frame);
            Assert.Same(eof ? null : frame, await reading.WaitAsync(Watchdog));
            if (eof) Assert.Null(origin.Generation);
            else Assert.Same(generation, origin.Generation);
        }
        finally
        {
            slot.Result.TrySetResult(null);
            await FinishAsync(source, installing, reading);
        }
    }

    private static VideoFrameSender.GenerationSendSnapshot AssertSnapshot(
        VideoFrameSender sender, Guid session, ReplaceableVideoFrameSource.GenerationIdentity generation,
        ulong firstId, int firstWidth, int firstHeight, ulong latestId, int latestWidth, int latestHeight)
    {
        // 仅读取一次原子引用，不能用多次属性读取拼装一个跨代快照。
        var snapshot = Assert.IsType<VideoFrameSender.GenerationSendSnapshot>(sender.LastSuccessfulGeneration);
        Assert.Equal(session, snapshot.SessionId);
        Assert.Same(generation, snapshot.Generation);
        Assert.Equal(new VideoFrameSender.SentFrame(firstId, firstWidth, firstHeight), snapshot.First);
        Assert.Equal(new VideoFrameSender.SentFrame(latestId, latestWidth, latestHeight), snapshot.Latest);
        return snapshot;
    }

    private static EncodedFrame Frame(RecordingVideoOwner owner, int width, int height)
    {
        VideoFrameTestData.Payload().CopyTo(owner.Bytes, 0);
        return new(VideoCodec.Jpeg, width, height, 0x0123456789ABCDEFul, 0x1020304050607080L, 60, owner, 5);
    }

    private static byte[] Wire(ulong frameId, int width, int height)
    {
        byte[] bytes = VideoFrameTestData.Wire();
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8, 8), frameId);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(24, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(28, 4), height);
        return bytes;
    }

    private static Producer Frames(params EncodedFrame[] frames) => new((read, _) =>
        ValueTask.FromResult<EncodedFrame?>(read < frames.Length ? frames[read] : null));

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<Exception?> ObserveAsync(Task operation)
    {
        try { await operation.WaitAsync(Watchdog); return null; }
        catch (TimeoutException) { throw; }
        catch (Exception error) { return error; }
    }

    private static async Task FinishAsync(ReplaceableVideoFrameSource source, params Task?[] roots)
    {
        // 调用者已放行所有 gate；清理不能套 watchdog 代理，必须逐一完整 join 原操作根。
        Task disposal = source.DisposeAsync().AsTask();
        foreach (Task? root in roots)
            if (root is not null) _ = await Record.ExceptionAsync(() => root);
        Exception? disposeError = await Record.ExceptionAsync(() => disposal);
        Exception? completionError = await Record.ExceptionAsync(() => source.Completion);
        Assert.Null(disposeError);
        Assert.Null(completionError);
    }

    private sealed class ReadSlot
    {
        internal TaskCompletionSource Entered { get; } = Signal();
        internal TaskCompletionSource<EncodedFrame?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ValueTask<EncodedFrame?> Read()
        {
            Entered.TrySetResult();
            return new(Result.Task);
        }
    }

    private sealed class Producer(Func<int, CancellationToken, ValueTask<EncodedFrame?>> read) : IVideoFrameProducer
    {
        private readonly TaskCompletionSource _lifetime = Signal();
        private int _reads, _stops, _disposes;
        internal int ReadCalls => Volatile.Read(ref _reads);
        internal int StopCalls => Volatile.Read(ref _stops);
        internal int DisposeCalls => Volatile.Read(ref _disposes);
        internal TaskCompletionSource StopEntered { get; } = Signal();
        public void Start() { }
        public ValueTask<EncodedFrame?> ReadNextAsync(CancellationToken cancellationToken = default) =>
            read(Interlocked.Increment(ref _reads) - 1, cancellationToken);
        public Task StopAsync()
        {
            Interlocked.Increment(ref _stops);
            _lifetime.TrySetResult();
            StopEntered.TrySetResult();
            return _lifetime.Task;
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposes);
            _lifetime.TrySetResult();
            return new(_lifetime.Task);
        }
        public Task Completion => _lifetime.Task;
    }

    private sealed class WireGate
    {
        internal TaskCompletionSource Entered { get; } = Signal();
        internal TaskCompletionSource Release { get; } = Signal();
        internal CancellationToken Token { get; private set; }
        internal async Task HoldAsync(CancellationToken token)
        {
            Token = token;
            Entered.TrySetResult();
            // 故意不响应取消：必须等原流任务尾部，不准用已取消的代理任务冒充 join。
            await Release.Task.ConfigureAwait(false);
        }
    }

    private sealed class HookStream(RecordingVideoStream inner) : Stream
    {
        private int _operation;
        internal Func<int, CancellationToken, Task>? AfterOperation { get; init; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            if (AfterOperation is not null) await AfterOperation(++_operation, cancellationToken);
        }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await inner.FlushAsync(cancellationToken);
            if (AfterOperation is not null) await AfterOperation(++_operation, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
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
