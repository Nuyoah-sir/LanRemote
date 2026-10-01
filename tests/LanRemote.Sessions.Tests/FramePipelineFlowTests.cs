using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using LanRemote.Sessions;
using Xunit;

namespace LanRemote.Sessions.Tests;

public sealed class FramePipelineFlowTests
{
    // 仅限制故障等待；就绪和推进只依靠原操作、帧移交及手动计时器的信号。
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Constructor_rejects_null_borrowed_dependencies(bool nullCapture)
    {
        var clock = new ManualClock();
        var frames = new FrameLedger();
        var capture = new ControlledCapture(clock, frames);
        var encoder = new ControlledEncoder(frames);
        FramePipeline? unexpected = null;
        try
        {
            Assert.Throws<ArgumentNullException>(() =>
            {
                unexpected = new FramePipeline(nullCapture ? null! : capture,
                    nullCapture ? encoder : null!, new DisplayId(7), timeProvider: clock);
            });
        }
        finally
        {
            Task stop = RequestStopForCleanup(unexpected);
            capture.ReleaseAll();
            encoder.ReleaseAll();
            try
            {
                await Guard(Task.WhenAll(new[] { stop }.Concat(capture.Originals).Concat(encoder.Originals)));
            }
            finally
            {
                capture.Finish();
                encoder.Finish();
                clock.Finish();
            }
        }
        frames.AssertReleased();
        Assert.Equal(0, capture.DisposeCount);
        Assert.Equal(0, encoder.DisposeCount);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(3, 1)]
    [InlineData(int.MinValue, 1)]
    [InlineData(int.MaxValue, 1)]
    [InlineData(1, -1)]
    [InlineData(1, 0)]
    [InlineData(1, 3)]
    [InlineData(1, int.MinValue)]
    [InlineData(1, int.MaxValue)]
    public async Task Constructor_rejects_each_capacity_outside_one_or_two(int raw, int encoded)
    {
        var clock = new ManualClock();
        var frames = new FrameLedger();
        var capture = new ControlledCapture(clock, frames);
        var encoder = new ControlledEncoder(frames);
        FramePipeline? unexpected = null;
        try
        {
            // 不把内部队列的参数名当作 FramePipeline 的公开合同。
            Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                unexpected = new FramePipeline(capture, encoder, new DisplayId(7), raw, encoded, clock);
            });
        }
        finally
        {
            Task stop = RequestStopForCleanup(unexpected);
            capture.ReleaseAll();
            encoder.ReleaseAll();
            try
            {
                await Guard(Task.WhenAll(new[] { stop }.Concat(capture.Originals).Concat(encoder.Originals)));
            }
            finally
            {
                capture.Finish();
                encoder.Finish();
                clock.Finish();
            }
        }
        frames.AssertReleased();
        Assert.Equal(0, capture.DisposeCount);
        Assert.Equal(0, encoder.DisposeCount);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    public async Task Construction_is_idle_and_stop_before_start_is_permanent(int raw, int encoded)
    {
        var rig = new Rig(raw, encoded);
        try
        {
            Task completion = rig.Pipeline.Completion;
            Assert.Same(completion, rig.Pipeline.Completion);
            Assert.False(completion.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => { _ = rig.Read(); });
            Assert.Equal(0, rig.Capture.Count);
            Assert.Equal(0, rig.Encoder.Count);
            Assert.Equal(0, rig.Clock.Advance(TimeSpan.FromDays(1)));

            Assert.Same(completion, rig.Pipeline.StopAsync());
            Assert.Same(completion, rig.Pipeline.StopAsync());
            Assert.Same(completion, rig.Pipeline.DisposeAsync().AsTask());
            await Guard(completion);

            // 以完整停止后的计数证明没有采集，而非等待一段墙钟时间。
            Assert.Equal(0, rig.Capture.Count);
            Assert.Equal(0, rig.Encoder.Count);
            Assert.Equal(0, rig.Clock.ScheduleCount);
            Assert.Throws<InvalidOperationException>(rig.Pipeline.Start);
            Assert.Throws<InvalidOperationException>(() => { _ = rig.Read(); });
        }
        finally
        {
            await rig.DisposeAsync();
        }
        rig.AssertReleased();
    }

    [Fact]
    public async Task Start_is_unique_and_completion_is_shared_by_stop_and_dispose()
    {
        var rig = new Rig();
        try
        {
            Task completion = rig.Pipeline.Completion;
            rig.Pipeline.Start();
            await Guard(rig.Capture.At(0));
            Assert.Throws<InvalidOperationException>(rig.Pipeline.Start);
            Assert.Equal(1, rig.Capture.Count);
            Assert.Same(completion, rig.Pipeline.StopAsync());
            Assert.Same(completion, rig.Pipeline.DisposeAsync().AsTask());
            Assert.Throws<InvalidOperationException>(rig.Pipeline.Start);
            rig.Capture.ReleaseAll();
            await Guard(completion);
            Assert.True(completion.IsCompletedSuccessfully);
            Assert.Null(await Guard(rig.Read()));
        }
        finally
        {
            await rig.DisposeAsync();
        }
        rig.AssertReleased();
    }

    [Fact]
    public async Task Concurrent_start_has_exactly_one_winner()
    {
        var rig = new Rig();
        var gate = NewSignal();
        Task<Exception?>[] starters = Enumerable.Range(0, 8).Select(_ => AttemptStart()).ToArray();
        Task<Exception?[]> starting = Task.WhenAll(starters);
        rig.TrackStartCalls(starting);
        async Task<Exception?> AttemptStart()
        {
            await gate.Task;
            return Record.Exception(rig.Pipeline.Start);
        }
        try
        {
            gate.TrySetResult();
            Exception?[] results = await Guard(starting);
            Assert.Single(results, error => error is null);
            Assert.All(results.Where(error => error is not null), error => Assert.IsType<InvalidOperationException>(error));
            await Guard(rig.Capture.At(0));
            Assert.Equal(1, rig.Capture.Count);
        }
        finally
        {
            gate.TrySetResult();
            await rig.DisposeAsync();
        }
        rig.AssertReleased();
    }

    [Fact]
    public async Task Default_settings_and_display_are_forwarded_and_reader_owns_the_result()
    {
        var rig = new Rig(display: new DisplayId(23));
        try
        {
            rig.Pipeline.Start();
            CaptureCall capture = await Guard(rig.Capture.At(0));
            Assert.Equal(new DisplayId(23), capture.Display);
            Tracked<CapturedFrame> raw = capture.Reply();
            EncodeCall encode = await Guard(rig.Encoder.At(0));
            Assert.Same(raw.Frame, encode.Input);
            Assert.Equal(5, encode.Settings.TargetFps);
            Assert.Equal(0.5, encode.Settings.Scale);
            Assert.Equal(60, encode.Settings.JpegQuality);
            Assert.False(encode.Settings.Auto);
            Assert.Equal(0, raw.Owner.DisposeCount);
            Assert.Equal(4, encode.Input.Pixels.Length);

            Tracked<EncodedFrame> encoded = encode.Reply();
            EncodedFrame result = Assert.IsType<EncodedFrame>(await Guard(rig.Read()));
            Assert.Same(encoded.Frame, result);
            Assert.Equal(1, raw.Owner.DisposeCount);
            Assert.Equal(0, encoded.Owner.DisposeCount);

            await Guard(rig.Pipeline.StopAsync());
            Assert.Equal(0, encoded.Owner.DisposeCount);
            Assert.Equal(4, result.Payload.Length);
            Assert.Equal(0, rig.Capture.DisposeCount);
            Assert.Equal(0, rig.Encoder.DisposeCount);
            rig.ReleaseRead(result);
            Assert.Equal(1, encoded.Owner.DisposeCount);
        }
        finally
        {
            await rig.DisposeAsync();
        }
        rig.AssertReleased();
    }

    [Fact]
    public async Task Overlapping_read_is_rejected_and_reader_cancellation_does_not_stop_pipeline()
    {
        var rig = new Rig();
        using var canceledRead = new CancellationTokenSource();
        try
        {
            rig.Pipeline.Start();
            CaptureCall capture = await Guard(rig.Capture.At(0));
            Task<EncodedFrame?> first = rig.Read(canceledRead.Token);
            Assert.False(first.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => { _ = rig.Read(); });
            canceledRead.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await Guard(first); });
            Assert.False(capture.Token.IsCancellationRequested);
            Assert.False(rig.Pipeline.Completion.IsCompleted);

            Task<EncodedFrame?> next = rig.Read();
            capture.Reply();
            EncodeCall encode = await Guard(rig.Encoder.At(0));
            Tracked<EncodedFrame> output = encode.Reply();
            EncodedFrame result = Assert.IsType<EncodedFrame>(await Guard(next));
            Assert.Same(output.Frame, result);
            rig.ReleaseRead(result);
        }
        finally
        {
            await rig.DisposeAsync();
        }
        rig.AssertReleased();
    }

    [Fact]
    public async Task Stop_joins_original_capture_and_reclaims_a_late_raw_result()
    {
        var rig = new Rig();
        try
        {
            rig.Pipeline.Start();
            CaptureCall capture = await Guard(rig.Capture.At(0));
            Task<EncodedFrame?> reader = rig.Read();
            Task stop = rig.Pipeline.StopAsync();
            await Guard(capture.CancellationSeen);
            Assert.False(capture.Original.IsCompleted);
            Assert.False(stop.IsCompleted);
            Tracked<CapturedFrame> late = capture.Reply();
            await Guard(stop);
            await Guard(capture.Original);
            Assert.Null(await Guard(reader));
            Assert.Equal(1, late.Owner.DisposeCount);
            Assert.Equal(0, rig.Encoder.Count);
            Assert.Throws<InvalidOperationException>(rig.Pipeline.Start);
        }
        finally
        {
            await rig.DisposeAsync();
        }
        rig.AssertReleased();
    }

    [Fact]
    public async Task Stop_keeps_raw_alive_until_original_encode_returns_and_reclaims_late_encoded()
    {
        var rig = new Rig();
        try
        {
            rig.Pipeline.Start();
            Tracked<CapturedFrame> raw = (await Guard(rig.Capture.At(0))).Reply();
            EncodeCall encode = await Guard(rig.Encoder.At(0));
            Task stop = rig.Pipeline.StopAsync();
            await Guard(encode.CancellationSeen);
            Assert.False(encode.Original.IsCompleted);
            Assert.False(stop.IsCompleted);
            Assert.Equal(0, raw.Owner.DisposeCount);
            Assert.Equal(4, encode.Input.Pixels.Length);
            Tracked<EncodedFrame> late = encode.Reply();
            await Guard(stop);
            await Guard(encode.Original);
            Assert.Equal(1, raw.Owner.DisposeCount);
            Assert.Equal(1, late.Owner.DisposeCount);
            Assert.Null(await Guard(rig.Read()));
        }
        finally
        {
            await rig.DisposeAsync();
        }
        rig.AssertReleased();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Raw_drop_oldest_releases_each_evicted_owner_and_preserves_fifo_survivors(int capacity)
    {
        var rig = new Rig(rawCapacity: capacity, encodedCapacity: 1);
        var raws = new List<Tracked<CapturedFrame>>();
        try
        {
            rig.Pipeline.Start();
            raws.Add((await Guard(rig.Capture.At(0))).Reply());
            EncodeCall current = await Guard(rig.Encoder.At(0));
            int total = capacity + 3;
            for (int index = 1; index < total; index++)
            {
                CaptureCall capture = await NextCapture(rig, index);
                raws.Add(capture.Reply());
            }
            // 计时器在本轮 raw 写入（包括丢旧释放）后才登记；编码器仍持有第一帧。
            await Guard(rig.Clock.ScheduleAt(total - 1));
            Assert.Equal(1, rig.Encoder.Count);
            Assert.Equal(0, raws[0].Owner.DisposeCount);
            int survivorStart = total - capacity;
            for (int index = 1; index < total; index++)
                Assert.Equal(index < survivorStart ? 1 : 0, raws[index].Owner.DisposeCount);

            Tracked<EncodedFrame> previous = current.Reply();
            for (int index = survivorStart; index < total; index++)
            {
                current = await Guard(rig.Encoder.At(index - survivorStart + 1));
                Assert.Same(raws[index].Frame, current.Input);
                // 下一次 Encode 已进入，证明上一个 encoded 已完成入队。
                EncodedFrame read = Assert.IsType<EncodedFrame>(await Guard(rig.Read()));
                Assert.Same(previous.Frame, read);
                Assert.Equal(0, previous.Owner.DisposeCount);
                rig.ReleaseRead(read);
                previous = current.Reply();
            }
            EncodedFrame last = Assert.IsType<EncodedFrame>(await Guard(rig.Read()));
            Assert.Same(previous.Frame, last);
            rig.ReleaseRead(last);
            Assert.Equal(capacity + 1, rig.Encoder.Count);
            Assert.All(raws, raw => Assert.Equal(1, raw.Owner.DisposeCount));
        }
        finally
        {
            await rig.DisposeAsync();
        }
        rig.AssertReleased();
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(2, false)]
    public async Task Slow_reader_does_not_backpressure_and_encoded_drop_oldest_releases_exact_owners(int capacity, bool readSurvivors)
    {
        var rig = new Rig(rawCapacity: 1, encodedCapacity: capacity);
        var outputs = new List<Tracked<EncodedFrame>>();
        try
        {
            rig.Pipeline.Start();
            (await Guard(rig.Capture.At(0))).Reply();
            outputs.Add((await Guard(rig.Encoder.At(0))).Reply());
            EncodedFrame held = Assert.IsType<EncodedFrame>(await Guard(rig.Read()));
            Assert.Same(outputs[0].Frame, held);

            int queuedTotal = capacity + 3;
            for (int index = 1; index <= queuedTotal; index++)
            {
                (await NextCapture(rig, index)).Reply();
                outputs.Add((await Guard(rig.Encoder.At(index))).Reply());
            }
            // 留一帧在编码器内，作为所有前序 encoded 均已写入的因果屏障。
            (await NextCapture(rig, queuedTotal + 1)).Reply();
            EncodeCall sentinel = await Guard(rig.Encoder.At(queuedTotal + 1));
            Assert.Equal(queuedTotal + 2, rig.Capture.Count);
            Assert.Equal(0, outputs[0].Owner.DisposeCount);
            int survivorStart = queuedTotal - capacity + 1;
            for (int index = 1; index <= queuedTotal; index++)
                Assert.Equal(index < survivorStart ? 1 : 0, outputs[index].Owner.DisposeCount);

            if (readSurvivors)
            {
                for (int index = survivorStart; index <= queuedTotal; index++)
                {
                    EncodedFrame result = Assert.IsType<EncodedFrame>(await Guard(rig.Read()));
                    Assert.Same(outputs[index].Frame, result);
                    Assert.Equal(0, outputs[index].Owner.DisposeCount);
                    rig.ReleaseRead(result);
                    Assert.Equal(1, outputs[index].Owner.DisposeCount);
                }
            }
            Task stop = rig.Pipeline.StopAsync();
            await Guard(sentinel.CancellationSeen);
            Tracked<EncodedFrame> late = sentinel.Reply();
            await Guard(stop);
            Assert.Equal(1, late.Owner.DisposeCount);
            // 未读分支由 Stop 清空存量；已读分支由读者释放，均须逐 owner 恰好一次。
            Assert.All(outputs.Skip(1), output => Assert.Equal(1, output.Owner.DisposeCount));
            // 已交给读者的 held 不计入队列容量，也不能被 DropOldest 或 Stop 释放。
            Assert.Equal(0, outputs[0].Owner.DisposeCount);
            Assert.Equal(4, held.Payload.Length);
            rig.ReleaseRead(held);
            Assert.All(outputs, output => Assert.Equal(1, output.Owner.DisposeCount));
        }
        finally
        {
            await rig.DisposeAsync();
        }
        rig.AssertReleased();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Blocked_pipeline_does_not_stall_or_cancel_an_independent_pipeline(int capacity)
    {
        var slow = new Rig(capacity, capacity, new DisplayId(11));
        var fast = new Rig(capacity, capacity, new DisplayId(22));
        var slowRaws = new List<Tracked<CapturedFrame>>();
        try
        {
            slow.Pipeline.Start();
            fast.Pipeline.Start();
            slowRaws.Add((await Guard(slow.Capture.At(0))).Reply());
            EncodeCall blocked = await Guard(slow.Encoder.At(0));
            for (int index = 1; index <= capacity + 2; index++)
                slowRaws.Add((await NextCapture(slow, index)).Reply());
            await Guard(slow.Clock.ScheduleAt(capacity + 2));

            for (int index = 0; index < 3; index++)
                await RoundTrip(fast, index, new DisplayId(22));
            Assert.False(blocked.Original.IsCompleted);
            Assert.Equal(1, slow.Encoder.Count);
            Assert.Equal(0, slowRaws[0].Owner.DisposeCount);
            Assert.Equal(1, slowRaws[1].Owner.DisposeCount);
            Assert.Equal(1, slowRaws[2].Owner.DisposeCount);
            Assert.All(slowRaws.Skip(3), raw => Assert.Equal(0, raw.Owner.DisposeCount));

            await slow.DisposeAsync();
            slow.AssertReleased();
            Assert.False(fast.Pipeline.Completion.IsCompleted);
            await RoundTrip(fast, 3, new DisplayId(22));
            Assert.Equal(4, fast.Encoder.Count);
            Assert.Equal(capacity + 3, slow.Capture.Count);
        }
        finally
        {
            // 两侧先各自放行全部门闩，再同时 join；一侧故障不能跳过另一侧清理。
            await Guard(Task.WhenAll(slow.DisposeAsync().AsTask(), fast.DisposeAsync().AsTask()));
        }
        slow.AssertReleased();
        fast.AssertReleased();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(75)]
    [InlineData(199)]
    public async Task Capture_starts_are_200ms_apart_not_200ms_after_capture_finishes(int workMs)
    {
        var rig = new Rig();
        try
        {
            rig.Pipeline.Start();
            CaptureCall first = await Guard(rig.Capture.At(0));
            Assert.Equal(TimeSpan.Zero, first.StartedAt);
            Assert.Equal(0, rig.Clock.Advance(TimeSpan.FromMilliseconds(workMs)));
            first.Reply();
            ScheduledDelay delay = await Guard(rig.Clock.ScheduleAt(0));
            Assert.Equal(TimeSpan.FromMilliseconds(200 - workMs), delay.Delay);
            Assert.Equal(TimeSpan.FromMilliseconds(200), delay.DueAt);
            Assert.Equal(0, rig.Clock.Advance(TimeSpan.FromMilliseconds(199 - workMs)));
            Assert.Equal(1, rig.Capture.Count);
            Assert.Equal(1, rig.Clock.Advance(TimeSpan.FromMilliseconds(1)));
            CaptureCall second = await Guard(rig.Capture.At(1));
            Assert.Equal(TimeSpan.FromMilliseconds(200), second.StartedAt);
            second.Reply();
            ScheduledDelay next = await Guard(rig.Clock.ScheduleAt(1));
            Assert.Equal(TimeSpan.FromMilliseconds(400), next.DueAt);
            Assert.Equal(1, rig.Clock.Advance(TimeSpan.FromMilliseconds(200)));
            CaptureCall third = await Guard(rig.Capture.At(2));
            Assert.Equal(TimeSpan.FromMilliseconds(400), third.StartedAt);
        }
        finally
        {
            await rig.DisposeAsync();
        }
        rig.AssertReleased();
    }

    [Theory]
    [InlineData(200)]
    [InlineData(450)]
    [InlineData(1000)]
    public async Task Over_budget_capture_starts_next_immediately_without_catching_up_old_deadlines(int workMs)
    {
        var rig = new Rig();
        try
        {
            rig.Pipeline.Start();
            CaptureCall first = await Guard(rig.Capture.At(0));
            Assert.Equal(TimeSpan.Zero, first.StartedAt);
            Assert.Equal(0, rig.Clock.Advance(TimeSpan.FromMilliseconds(workMs)));
            first.Reply();
            CaptureCall second = await Guard(rig.Capture.At(1));
            Assert.Equal(TimeSpan.FromMilliseconds(workMs), second.StartedAt);
            Assert.Equal(0, rig.Clock.ScheduleCount);
            second.Reply();
            ScheduledDelay delay = await Guard(rig.Clock.ScheduleAt(0));
            Assert.Equal(TimeSpan.FromMilliseconds(200), delay.Delay);
            Assert.Equal(TimeSpan.FromMilliseconds(workMs + 200), delay.DueAt);
            Assert.Equal(0, rig.Clock.Advance(TimeSpan.FromMilliseconds(199)));
            Assert.Equal(2, rig.Capture.Count);
            Assert.Equal(1, rig.Clock.Advance(TimeSpan.FromMilliseconds(1)));
            CaptureCall third = await Guard(rig.Capture.At(2));
            Assert.Equal(TimeSpan.FromMilliseconds(workMs + 200), third.StartedAt);
        }
        finally
        {
            await rig.DisposeAsync();
        }
        rig.AssertReleased();
    }

    private static async Task<CaptureCall> NextCapture(Rig rig, int index)
    {
        ScheduledDelay delay = await Guard(rig.Clock.ScheduleAt(index - 1));
        Assert.Equal(1, rig.Clock.Advance(delay.DueAt - rig.Clock.Now));
        return await Guard(rig.Capture.At(index));
    }

    private static async Task RoundTrip(Rig rig, int index, DisplayId display)
    {
        CaptureCall capture = index == 0
            ? await Guard(rig.Capture.At(0))
            : await NextCapture(rig, index);
        Assert.Equal(display, capture.Display);
        Assert.False(capture.Token.IsCancellationRequested);
        Tracked<CapturedFrame> raw = capture.Reply();
        EncodeCall encode = await Guard(rig.Encoder.At(index));
        Assert.Same(raw.Frame, encode.Input);
        Assert.Equal(display, encode.Input.DisplayId);
        Assert.False(encode.Token.IsCancellationRequested);
        Tracked<EncodedFrame> output = encode.Reply();
        EncodedFrame read = Assert.IsType<EncodedFrame>(await Guard(rig.Read()));
        Assert.Same(output.Frame, read);
        Assert.Equal(1, raw.Owner.DisposeCount);
        Assert.Equal(0, output.Owner.DisposeCount);
        rig.ReleaseRead(read);
        Assert.Equal(1, output.Owner.DisposeCount);
    }

    private static Task Guard(Task task) => task.WaitAsync(Watchdog);
    private static Task<T> Guard<T>(Task<T> task) => task.WaitAsync(Watchdog);
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task RequestStopForCleanup(FramePipeline? pipeline)
    {
        try { return pipeline?.StopAsync() ?? Task.CompletedTask; }
        catch (Exception error) { return Task.FromException(error); }
    }

    private static Task ReadLoopForCleanup(FramePipeline pipeline, string name)
    {
        try
        {
            // 只读反射仅用于失败清理，不作为行为断言，也不修改生产状态。
            FieldInfo field = typeof(FramePipeline).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"无法定位清理所需的真实循环 {name}。");
            return field.GetValue(pipeline) as Task
                ?? throw new InvalidOperationException($"循环 {name} 尚未发布为 Task。");
        }
        catch (Exception error) { return Task.FromException(error); }
    }

    private sealed record PendingRead(Task<EncodedFrame?> Original, CancellationTokenSource Cancellation);

    private sealed class Rig : IAsyncDisposable
    {
        private readonly FrameLedger _frames = new();
        private readonly List<PendingRead> _reads = new();
        private readonly object _readerGate = new();
        private readonly HashSet<EncodedFrame> _releasedByReader = new();
        private Task _startCalls = Task.CompletedTask;
        private Task? _cleanup;

        public Rig(int rawCapacity = 2, int encodedCapacity = 2, DisplayId? display = null)
        {
            Capture = new ControlledCapture(Clock, _frames);
            Encoder = new ControlledEncoder(_frames);
            Pipeline = new FramePipeline(Capture, Encoder, display ?? new DisplayId(7), rawCapacity, encodedCapacity, Clock);
        }

        public ManualClock Clock { get; } = new();
        public ControlledCapture Capture { get; }
        public ControlledEncoder Encoder { get; }
        public FramePipeline Pipeline { get; }

        public void TrackStartCalls(Task calls) => _startCalls = calls;

        public Task<EncodedFrame?> Read(CancellationToken token = default)
        {
            var cancellation = token.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(token)
                : new CancellationTokenSource();
            try
            {
                // 令牌直接传给原读取；不以 WaitAsync 取消代理替代原操作。
                Task<EncodedFrame?> original = Pipeline.ReadNextAsync(cancellation.Token).AsTask();
                _reads.Add(new PendingRead(original, cancellation));
                return original;
            }
            catch
            {
                cancellation.Dispose();
                throw;
            }
        }

        public void ReleaseRead(EncodedFrame frame)
        {
            lock (_readerGate)
            {
                if (!_releasedByReader.Add(frame)) return;
            }
            frame.Dispose();
        }

        public ValueTask DisposeAsync() => new(_cleanup ??= Cleanup());

        private async Task Cleanup()
        {
            // 同步 Stop 异常转为待观察的故障任务，不能跳过放闸或读取取消。
            var tasks = new List<Task> { RequestStopForCleanup(Pipeline), Pipeline.Completion };
            void Attempt(Action action)
            {
                try { action(); }
                catch (Exception error) { tasks.Add(Task.FromException(error)); }
            }

            // 先放行全部真实 fake 操作，再取消原读取；一项失败不跳过其他项。
            Attempt(Capture.ReleaseAll);
            Attempt(Encoder.ReleaseAll);
            foreach (PendingRead read in _reads) Attempt(read.Cancellation.Cancel);

            // 当前普通 Start 均同步返回；并发 Start 必须等真实调用结束后再读循环引用。
            // 测试主体的 Guard 超时不等于 Start 已结束，不能据此读取初始 CompletedTask。
            try { await _startCalls.ConfigureAwait(false); }
            catch { tasks.Add(_startCalls); }
            tasks.Add(ReadLoopForCleanup(Pipeline, "_captureLoop"));
            tasks.Add(ReadLoopForCleanup(Pipeline, "_encodeLoop"));
            tasks.AddRange(Capture.Originals);
            tasks.AddRange(Encoder.Originals);
            tasks.AddRange(_reads.Select(JoinRead));
            Task joined = Task.WhenAll(tasks);
            try
            {
                await Guard(joined);
            }
            catch (TimeoutException error) when (!joined.IsCompleted)
            {
                throw new TimeoutException("清理看门狗超时：仍有原任务未完成 join，不能视为已清理。", error);
            }
            finally
            {
                // 不能因代理超时就释放仍被真实循环使用的注册或关闭其信号。
                if (joined.IsCompleted)
                {
                    Capture.Finish();
                    Encoder.Finish();
                    Clock.Finish();
                }
            }
        }

        private async Task JoinRead(PendingRead read)
        {
            try
            {
                EncodedFrame? frame = await read.Original.ConfigureAwait(false);
                if (frame is not null) ReleaseRead(frame);
            }
            catch (OperationCanceledException) when (read.Original.IsCanceled && read.Cancellation.IsCancellationRequested)
            {
            }
            finally
            {
                // 仅在原读取终止后释放 CTS，已移交的帧只走读者释放路径。
                read.Cancellation.Dispose();
            }
        }

        public void AssertReleased()
        {
            _frames.AssertReleased();
            Assert.Equal(0, Capture.DisposeCount);
            Assert.Equal(0, Encoder.DisposeCount);
        }
    }

    private sealed record Tracked<T>(T Frame, CountingOwner Owner) where T : class, IDisposable;

    private sealed class CountingOwner : IMemoryOwner<byte>
    {
        private readonly byte[] _bytes = new byte[4];
        private int _disposeCount;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public Memory<byte> Memory
        {
            get
            {
                ObjectDisposedException.ThrowIf(DisposeCount != 0, this);
                return _bytes;
            }
        }
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class FrameLedger
    {
        private readonly object _gate = new();
        private readonly List<Tracked<CapturedFrame>> _raws = new();
        private readonly List<Tracked<EncodedFrame>> _encoded = new();

        public Tracked<CapturedFrame> Raw(DisplayId display)
        {
            lock (_gate)
            {
                var owner = new CountingOwner();
                var frame = new CapturedFrame(display, 1, 1, 4, FramePixelFormat.Bgra32, owner, _raws.Count + 1);
                var tracked = new Tracked<CapturedFrame>(frame, owner);
                _raws.Add(tracked);
                return tracked;
            }
        }

        public Tracked<EncodedFrame> Encoded(CapturedFrame raw)
        {
            lock (_gate)
            {
                var owner = new CountingOwner();
                var frame = new EncodedFrame(VideoCodec.Jpeg, 1, 1, (ulong)raw.TimestampUs, raw.TimestampUs, 60, owner);
                var tracked = new Tracked<EncodedFrame>(frame, owner);
                _encoded.Add(tracked);
                return tracked;
            }
        }

        public void AssertReleased()
        {
            lock (_gate)
            {
                // 这里只证明底层 owner 的 Dispose 次数；帧模型幂等，无法据此排除重复 Frame.Dispose。
                // 不在此兜底释放生产者持有的帧，避免用测试清理掩盖泄漏。
                Assert.All(_raws, item => Assert.True(item.Owner.DisposeCount == 1,
                    $"raw {item.Frame.TimestampUs} 的 owner 释放次数为 {item.Owner.DisposeCount}，应为 1。"));
                Assert.All(_encoded, item => Assert.True(item.Owner.DisposeCount == 1,
                    $"encoded {item.Frame.FrameId} 的 owner 释放次数为 {item.Owner.DisposeCount}，应为 1。"));
            }
        }
    }

    private abstract class PendingCall<T> where T : class, IDisposable
    {
        private readonly object _gate = new();
        private readonly Func<Tracked<T>> _produce;
        private readonly TaskCompletionSource<T> _reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationSeen = NewSignal();
        private readonly CancellationTokenRegistration _registration;
        private Tracked<T>? _result;

        protected PendingCall(CancellationToken token, Func<Tracked<T>> produce)
        {
            Token = token;
            _produce = produce;
            _registration = token.Register(() => _cancellationSeen.TrySetResult());
        }

        public CancellationToken Token { get; }
        public Task<T> Original => _reply.Task;
        public Task CancellationSeen => _cancellationSeen.Task;
        public Tracked<T> Reply()
        {
            lock (_gate)
            {
                if (_result is null)
                {
                    _result = _produce();
                    _reply.SetResult(_result.Frame);
                }
                return _result;
            }
        }
        public void Finish() => _registration.Dispose();
    }

    private sealed class CaptureCall : PendingCall<CapturedFrame>
    {
        public CaptureCall(DisplayId display, TimeSpan startedAt, CancellationToken token, FrameLedger frames)
            : base(token, () => frames.Raw(display))
        {
            Display = display;
            StartedAt = startedAt;
        }
        public DisplayId Display { get; }
        public TimeSpan StartedAt { get; }
    }

    private sealed class EncodeCall : PendingCall<EncodedFrame>
    {
        public EncodeCall(CapturedFrame input, VideoQualitySettings settings, CancellationToken token, FrameLedger frames)
            : base(token, () => frames.Encoded(input))
        {
            Input = input;
            Settings = settings;
        }
        public CapturedFrame Input { get; }
        public VideoQualitySettings Settings { get; }
    }

    private sealed class ControlledCapture : IScreenCaptureBackend, IDisposable, IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly ManualClock _clock;
        private readonly FrameLedger _frames;
        private readonly SignalLog<CaptureCall> _calls = new();
        private bool _released;
        private int _disposeCount;

        public ControlledCapture(ManualClock clock, FrameLedger frames) { _clock = clock; _frames = frames; }
        public int Count => _calls.Count;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public Task<CaptureCall> At(int index) => _calls.At(index);
        public IEnumerable<Task> Originals => _calls.Snapshot().Select(call => (Task)call.Original);
        public IReadOnlyList<DisplayInfo> GetDisplays() => Array.Empty<DisplayInfo>();
        public ValueTask<CapturedFrame> CaptureAsync(DisplayId displayId, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var call = new CaptureCall(displayId, _clock.Now, cancellationToken, _frames);
                _calls.Add(call);
                if (_released) call.Reply();
                // 返回真实门闩任务，不创建可被取消提前完成的 WaitAsync 代理。
                return new ValueTask<CapturedFrame>(call.Original);
            }
        }
        public void ReleaseAll()
        {
            lock (_gate)
            {
                _released = true;
                foreach (CaptureCall call in _calls.Snapshot()) call.Reply();
            }
        }
        public void Finish()
        {
            foreach (CaptureCall call in _calls.Snapshot()) call.Finish();
            _calls.Finish();
        }
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class ControlledEncoder : IFrameEncoder, IDisposable, IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly FrameLedger _frames;
        private readonly SignalLog<EncodeCall> _calls = new();
        private bool _released;
        private int _disposeCount;

        public ControlledEncoder(FrameLedger frames) => _frames = frames;
        public int Count => _calls.Count;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public Task<EncodeCall> At(int index) => _calls.At(index);
        public IEnumerable<Task> Originals => _calls.Snapshot().Select(call => (Task)call.Original);
        public ValueTask<EncodedFrame> EncodeAsync(CapturedFrame frame, VideoQualitySettings settings, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var call = new EncodeCall(frame, settings, cancellationToken, _frames);
                _calls.Add(call);
                if (_released) call.Reply();
                return new ValueTask<EncodedFrame>(call.Original);
            }
        }
        public void ReleaseAll()
        {
            lock (_gate)
            {
                _released = true;
                foreach (EncodeCall call in _calls.Snapshot()) call.Reply();
            }
        }
        public void Finish()
        {
            foreach (EncodeCall call in _calls.Snapshot()) call.Finish();
            _calls.Finish();
        }
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class SignalLog<T>
    {
        private readonly object _gate = new();
        private readonly List<T> _items = new();
        private readonly Dictionary<int, TaskCompletionSource<T>> _waiting = new();
        public int Count { get { lock (_gate) return _items.Count; } }
        public T[] Snapshot() { lock (_gate) return _items.ToArray(); }
        public void Add(T value)
        {
            lock (_gate)
            {
                int index = _items.Count;
                _items.Add(value);
                if (_waiting.Remove(index, out TaskCompletionSource<T>? waiter)) waiter.TrySetResult(value);
            }
        }
        public Task<T> At(int index)
        {
            lock (_gate)
            {
                if (index < _items.Count) return Task.FromResult(_items[index]);
                if (!_waiting.TryGetValue(index, out TaskCompletionSource<T>? waiter))
                {
                    waiter = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiting.Add(index, waiter);
                }
                return waiter.Task;
            }
        }
        public void Finish()
        {
            lock (_gate)
            {
                foreach (TaskCompletionSource<T> waiter in _waiting.Values) waiter.TrySetCanceled();
                _waiting.Clear();
            }
        }
    }

    private sealed record ScheduledDelay(TimeSpan Delay, TimeSpan DueAt);

    private sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = new();
        private readonly SignalLog<ScheduledDelay> _schedules = new();
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_gate) return _ticks; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        public TimeSpan Now => TimeSpan.FromTicks(GetTimestamp());
        public int ScheduleCount => _schedules.Count;
        public Task<ScheduledDelay> ScheduleAt(int index) => _schedules.At(index);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            lock (_gate)
            {
                var timer = new ManualTimer(this, callback, state);
                _timers.Add(timer);
                Change(timer, dueTime, period);
                return timer;
            }
        }

        public int Advance(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
            var ready = new List<ManualTimer>();
            lock (_gate)
            {
                _ticks = checked(_ticks + elapsed.Ticks);
                foreach (ManualTimer timer in _timers)
                {
                    if (timer.Disposed || timer.DueTicks is not long due || due > _ticks) continue;
                    timer.DueTicks = timer.PeriodTicks > 0
                        ? checked(due + ((_ticks - due) / timer.PeriodTicks + 1) * timer.PeriodTicks)
                        : null;
                    ready.Add(timer);
                }
            }
            // 回调不持时钟锁，不阻塞工作线程；后续 continuation 使用异步门闩通知测试。
            foreach (ManualTimer timer in ready) timer.Callback(timer.State);
            return ready.Count;
        }

        private bool Change(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime < TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(dueTime));
            if (period < TimeSpan.Zero && period != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(period));
            lock (_gate)
            {
                if (timer.Disposed) return false;
                timer.DueTicks = dueTime == Timeout.InfiniteTimeSpan ? null : checked(_ticks + dueTime.Ticks);
                timer.PeriodTicks = period.Ticks;
                if (timer.DueTicks is long due)
                    _schedules.Add(new ScheduledDelay(dueTime, TimeSpan.FromTicks(due)));
                return true;
            }
        }

        public void Finish() => _schedules.Finish();

        private sealed class ManualTimer : ITimer
        {
            private readonly ManualClock _clock;
            public ManualTimer(ManualClock clock, TimerCallback callback, object? state)
            {
                _clock = clock;
                Callback = callback;
                State = state;
            }
            public TimerCallback Callback { get; }
            public object? State { get; }
            public long? DueTicks { get; set; }
            public long PeriodTicks { get; set; }
            public bool Disposed { get; set; }
            public bool Change(TimeSpan dueTime, TimeSpan period) => _clock.Change(this, dueTime, period);
            public void Dispose()
            {
                lock (_clock._gate)
                {
                    Disposed = true;
                    DueTicks = null;
                }
            }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
