using System.Buffers;
using System.Reflection;
using LanRemote.Core.Models;
using Xunit;

namespace LanRemote.Core.Tests;

/// <summary>有界帧队列的所有权、取消、并发停止及释放异常测试。</summary>
public sealed class BoundedFrameQueueTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void Constructor_DefaultCapacityIsTwo()
    {
        using BoundedFrameQueue<TrackedFrame> queue = new();
        Assert.Equal(2, queue.Capacity);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void Constructor_RejectsCapacityOtherThanOneOrTwo(int capacity)
    {
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
            () => new BoundedFrameQueue<TrackedFrame>(capacity));
        Assert.Equal("capacity", error.ParamName);
    }

    [Fact]
    public void TryWrite_RejectsNullWithoutChangingQueue()
    {
        using BoundedFrameQueue<TrackedFrame> queue = new();
        Assert.Throws<ArgumentNullException>(() => queue.TryWrite(null!, out _));
        TrackedFrame frame = new();
        WriteAccepted(queue, frame);
        queue.Stop();
        Assert.Equal(1, frame.DisposeCalls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ReadAsync_TransfersOwnershipInFifoOrderAndSupportsReuse(int capacity)
    {
        using BoundedFrameQueue<TrackedFrame> queue = new(capacity);
        Assert.Equal(capacity, queue.Capacity);
        List<TrackedFrame> all = new();
        for (int round = 0; round < 8; round++)
        {
            TrackedFrame[] batch = Enumerable.Range(0, capacity).Select(_ => new TrackedFrame()).ToArray();
            all.AddRange(batch);
            foreach (TrackedFrame frame in batch)
            {
                WriteAccepted(queue, frame);
            }

            foreach (TrackedFrame expected in batch)
            {
                TrackedFrame actual = await queue.ReadAsync().AsTask().WaitAsync(TestTimeout);
                Assert.Same(expected, actual);
                Assert.Equal(0, actual.DisposeCalls);
                actual.Dispose();
            }
        }

        queue.Stop();
        Assert.All(all, frame => Assert.Equal(1, frame.DisposeCalls));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task TryWrite_WithoutConsumerKeepsOnlyNewestFramesAndDisposesDroppedOnes(int capacity)
    {
        using BoundedFrameQueue<TrackedFrame> queue = new(capacity);
        TrackedFrame[] frames = Enumerable.Range(0, 100).Select(_ => new TrackedFrame()).ToArray();
        foreach (TrackedFrame frame in frames)
        {
            WriteAccepted(queue, frame);
        }

        for (int i = 0; i < frames.Length; i++)
        {
            Assert.Equal(i < frames.Length - capacity ? 1 : 0, frames[i].DisposeCalls);
        }

        foreach (TrackedFrame expected in frames.TakeLast(capacity))
        {
            TrackedFrame actual = await queue.ReadAsync().AsTask().WaitAsync(TestTimeout);
            Assert.Same(expected, actual);
            actual.Dispose();
        }

        queue.Dispose();
        Assert.All(frames, frame => Assert.Equal(1, frame.DisposeCalls));
    }

    [Fact]
    public async Task ReadAsync_EmptyQueueWaitsUntilWrite()
    {
        using BoundedFrameQueue<TrackedFrame> queue = new();
        Task<TrackedFrame> pending = queue.ReadAsync().AsTask();
        Assert.False(pending.IsCompleted);
        TrackedFrame frame = new();
        WriteAccepted(queue, frame);

        Assert.Same(frame, await pending.WaitAsync(TestTimeout));
        queue.Dispose();
        Assert.Equal(0, frame.DisposeCalls);
        frame.Dispose();
        Assert.Equal(1, frame.DisposeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_PreCanceledTokenNeverTakesBufferedFrame(bool buffered)
    {
        using BoundedFrameQueue<TrackedFrame> queue = new();
        using CancellationTokenSource cancellation = new();
        TrackedFrame frame = new();
        if (buffered)
        {
            WriteAccepted(queue, frame);
        }

        cancellation.Cancel();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queue.ReadAsync(cancellation.Token).AsTask().WaitAsync(TestTimeout));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        if (!buffered)
        {
            WriteAccepted(queue, frame);
        }

        Assert.Same(frame, await queue.ReadAsync().AsTask().WaitAsync(TestTimeout));
        Assert.Equal(0, frame.DisposeCalls);
        frame.Dispose();
    }

    [Fact]
    public async Task ReadAsync_CancellationAfterTransferDoesNotRevokeConsumerOwnership()
    {
        using BoundedFrameQueue<TrackedFrame> queue = new();
        using CancellationTokenSource cancellation = new();
        TrackedFrame frame = new();
        WriteAccepted(queue, frame);
        ValueTask<TrackedFrame> read = queue.ReadAsync(cancellation.Token);
        Assert.True(read.IsCompletedSuccessfully);

        cancellation.Cancel();
        queue.Stop();
        Assert.Same(frame, await read);
        Assert.Equal(0, frame.DisposeCalls);
        frame.Dispose();
        Assert.Equal(1, frame.DisposeCalls);
    }

    [Fact]
    public async Task ReadAsync_RepeatedCanceledWaitsDoNotStealLaterFrame()
    {
        using BoundedFrameQueue<TrackedFrame> queue = new();
        for (int i = 0; i < 32; i++)
        {
            using CancellationTokenSource cancellation = new();
            Task<TrackedFrame> canceled = queue.ReadAsync(cancellation.Token).AsTask();
            Assert.False(canceled.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.WaitAsync(TestTimeout));
        }

        Task<TrackedFrame> survivor = queue.ReadAsync().AsTask();
        TrackedFrame frame = new();
        WriteAccepted(queue, frame);
        Assert.Same(frame, await survivor.WaitAsync(TestTimeout));
        frame.Dispose();
        queue.Stop();
        Assert.Equal(1, frame.DisposeCalls);
    }

    [Fact]
    public async Task ReadAsync_CancelingOneWaiterDoesNotCancelAnother()
    {
        using BoundedFrameQueue<TrackedFrame> queue = new();
        using CancellationTokenSource cancellation = new();
        Task<TrackedFrame> canceled = queue.ReadAsync(cancellation.Token).AsTask();
        Task<TrackedFrame> survivor = queue.ReadAsync().AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.WaitAsync(TestTimeout));
        Assert.False(survivor.IsCompleted);

        TrackedFrame frame = new();
        WriteAccepted(queue, frame);
        Assert.Same(frame, await survivor.WaitAsync(TestTimeout));
        frame.Dispose();
    }

    [Fact]
    public async Task ReadAsync_CancellationRacingWriteEitherTransfersOrLeavesFrameQueued()
    {
        for (int i = 0; i < 100; i++)
        {
            using BoundedFrameQueue<TrackedFrame> queue = new(1);
            using CancellationTokenSource cancellation = new();
            TrackedFrame frame = new();
            Task<TrackedFrame> read = queue.ReadAsync(cancellation.Token).AsTask();
            TaskCompletionSource start = NewSignal();
            Task cancel = Task.Run(async () =>
            {
                await start.Task;
                cancellation.Cancel();
            });
            Task write = Task.Run(async () =>
            {
                await start.Task;
                WriteAccepted(queue, frame);
            });
            start.SetResult();
            await Task.WhenAll(cancel, write).WaitAsync(TestTimeout);

            TrackedFrame received;
            try
            {
                received = await read.WaitAsync(TestTimeout);
            }
            catch (OperationCanceledException error)
            {
                Assert.Equal(cancellation.Token, error.CancellationToken);
                received = await queue.ReadAsync().AsTask().WaitAsync(TestTimeout);
            }

            Assert.Same(frame, received);
            Assert.Equal(0, frame.DisposeCalls);
            received.Dispose();
            queue.Stop();
            Assert.Equal(1, frame.DisposeCalls);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopOrDispose_WakesAllWaitersAndRejectsFutureOperations(bool dispose)
    {
        using BoundedFrameQueue<TrackedFrame> queue = new();
        Task<TrackedFrame>[] waiters = Enumerable.Range(0, 8)
            .Select(_ => queue.ReadAsync().AsTask()).ToArray();
        Assert.All(waiters, waiter => Assert.False(waiter.IsCompleted));

        if (dispose)
        {
            queue.Dispose();
        }
        else
        {
            queue.Stop();
        }

        foreach (Task<TrackedFrame> waiter in waiters)
        {
            await Assert.ThrowsAsync<ObjectDisposedException>(() => waiter.WaitAsync(TestTimeout));
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(() => queue.ReadAsync().AsTask());
        TrackedFrame rejected = new();
        Assert.False(queue.TryWrite(rejected, out Exception? error));
        Assert.Null(error);
        queue.Stop();
        queue.Dispose();
        Assert.Equal(0, rejected.DisposeCalls);
        rejected.Dispose();
        Assert.Equal(1, rejected.DisposeCalls);
    }

    [Fact]
    public async Task ReadAsync_CancellationTakesPrecedenceOverStoppedState()
    {
        using BoundedFrameQueue<TrackedFrame> queue = new();
        using CancellationTokenSource cancellation = new();
        queue.Stop();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queue.ReadAsync(cancellation.Token).AsTask());
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task StopOrDispose_DrainsQueueWithoutReleasingConsumerFrame(int capacity, bool dispose)
    {
        using BoundedFrameQueue<TrackedFrame> queue = new(capacity);
        TrackedFrame ownedByConsumer = new();
        WriteAccepted(queue, ownedByConsumer);
        Assert.Same(ownedByConsumer, await queue.ReadAsync());
        TrackedFrame[] queued = Enumerable.Range(0, capacity).Select(_ => new TrackedFrame()).ToArray();
        foreach (TrackedFrame frame in queued)
        {
            WriteAccepted(queue, frame);
        }

        if (dispose)
        {
            queue.Dispose();
        }
        else
        {
            queue.Stop();
        }

        Assert.All(queued, frame => Assert.Equal(1, frame.DisposeCalls));
        Assert.Equal(0, ownedByConsumer.DisposeCalls);
        queue.Stop();
        queue.Dispose();
        ownedByConsumer.Dispose();
        Assert.All(queued, frame => Assert.Equal(1, frame.DisposeCalls));
        Assert.Equal(1, ownedByConsumer.DisposeCalls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task TryWrite_DroppedDisposeErrorDoesNotHideAcceptedOwnership(int capacity)
    {
        using BoundedFrameQueue<TrackedFrame> queue = new(capacity);
        InvalidOperationException failure = new("丢旧帧释放失败");
        TrackedFrame dropped = new(() => throw failure);
        WriteAccepted(queue, dropped);
        TrackedFrame? middle = capacity == 2 ? new TrackedFrame() : null;
        if (middle is not null)
        {
            WriteAccepted(queue, middle);
        }

        TrackedFrame accepted = new();
        Assert.True(queue.TryWrite(accepted, out Exception? disposalError));
        Assert.Same(failure, disposalError);
        Assert.Equal(1, dropped.DisposeCalls);
        Assert.Equal(0, accepted.DisposeCalls);
        if (middle is not null)
        {
            Assert.Same(middle, await queue.ReadAsync());
            middle.Dispose();
        }

        Assert.Same(accepted, await queue.ReadAsync());
        queue.Stop();
        Assert.Equal(0, accepted.DisposeCalls);
        accepted.Dispose();
        Assert.Equal(1, dropped.DisposeCalls);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task StopOrDispose_AggregatesErrorsAndStillAttemptsEveryFrame(
        bool dispose, bool firstThrows, bool secondThrows)
    {
        using BoundedFrameQueue<TrackedFrame> queue = new();
        InvalidOperationException firstError = new("第一帧释放失败");
        IOException secondError = new("第二帧释放失败");
        TrackedFrame first = new(firstThrows ? () => throw firstError : null);
        TrackedFrame second = new(secondThrows ? () => throw secondError : null);
        WriteAccepted(queue, first);
        WriteAccepted(queue, second);

        AggregateException aggregate = Assert.Throws<AggregateException>(() =>
        {
            if (dispose)
            {
                queue.Dispose();
            }
            else
            {
                queue.Stop();
            }
        });
        List<Exception> expected = new();
        if (firstThrows)
        {
            expected.Add(firstError);
        }
        if (secondThrows)
        {
            expected.Add(secondError);
        }

        Assert.Equal(expected, aggregate.InnerExceptions);
        Assert.Equal(1, first.DisposeCalls);
        Assert.Equal(1, second.DisposeCalls);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queue.ReadAsync().AsTask());
        TrackedFrame rejected = new();
        Assert.False(queue.TryWrite(rejected, out Exception? error));
        Assert.Null(error);
        Assert.Equal(0, rejected.DisposeCalls);
        rejected.Dispose();
        queue.Stop();
        queue.Dispose();
        Assert.Equal(1, first.DisposeCalls);
        Assert.Equal(1, second.DisposeCalls);
    }

    [Fact]
    public async Task TryWrite_BlockingDroppedDisposeDoesNotHoldLockOrDelayStop()
    {
        using BoundedFrameQueue<TrackedFrame> queue = new(1);
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = NewSignal();
        InvalidOperationException failure = new("丢旧释放结束时失败");
        TrackedFrame dropped = new(() =>
        {
            entered.SetResult();
            Assert.True(release.Wait(TestTimeout));
            throw failure;
        });
        TrackedFrame accepted = new();
        TrackedFrame remaining = new();
        WriteAccepted(queue, dropped);
        Task<(bool Accepted, Exception? Error)> writer = Task.Run(() =>
        {
            bool result = queue.TryWrite(accepted, out Exception? error);
            return (result, error);
        });

        try
        {
            await entered.Task.WaitAsync(TestTimeout);
            Assert.False(writer.IsCompleted);
            TrackedFrame received = await Task.Run(async () => await queue.ReadAsync()).WaitAsync(TestTimeout);
            Assert.Same(accepted, received);
            await Task.Run(() => WriteAccepted(queue, remaining)).WaitAsync(TestTimeout);
            await Task.Run(queue.Stop).WaitAsync(TestTimeout);
            Assert.Equal(1, remaining.DisposeCalls);
            Assert.Equal(0, accepted.DisposeCalls);
            Assert.False(writer.IsCompleted);
            accepted.Dispose();
        }
        finally
        {
            release.Set();
            await writer.WaitAsync(TestTimeout);
        }

        (bool success, Exception? disposalError) = await writer;
        Assert.True(success);
        Assert.Same(failure, disposalError);
        Assert.Equal(1, dropped.DisposeCalls);
        Assert.Equal(1, accepted.DisposeCalls);
    }

    [Fact]
    public async Task Stop_BlockingDisposePublishesStoppedStateBeforeCleanupAndAllowsConcurrentStop()
    {
        using BoundedFrameQueue<TrackedFrame> queue = new();
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = NewSignal();
        InvalidOperationException failure = new("首帧释放失败");
        TrackedFrame first = new(() =>
        {
            entered.SetResult();
            Assert.True(release.Wait(TestTimeout));
            throw failure;
        });
        TrackedFrame second = new();
        WriteAccepted(queue, first);
        WriteAccepted(queue, second);
        Task stop = Task.Run(queue.Stop);
        AggregateException? error = null;
        try
        {
            await entered.Task.WaitAsync(TestTimeout);
            Assert.False(stop.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => Task.Run(async () => await queue.ReadAsync()).WaitAsync(TestTimeout));
            await Task.Run(queue.Dispose).WaitAsync(TestTimeout);
            TrackedFrame rejected = new();
            await Task.Run(() =>
            {
                Assert.False(queue.TryWrite(rejected, out Exception? writeError));
                Assert.Null(writeError);
            }).WaitAsync(TestTimeout);
            Assert.Equal(0, rejected.DisposeCalls);
            rejected.Dispose();
        }
        finally
        {
            release.Set();
            error = await Assert.ThrowsAsync<AggregateException>(() => stop.WaitAsync(TestTimeout));
        }

        Assert.Same(failure, Assert.Single(error.InnerExceptions));
        Assert.Equal(1, first.DisposeCalls);
        Assert.Equal(1, second.DisposeCalls);
    }

    [Fact]
    public void TryWrite_ReentrantDisposeCanStopQueueWithoutChangingWriteResult()
    {
        using BoundedFrameQueue<TrackedFrame> queue = new(1);
        InvalidOperationException failure = new("新帧释放失败");
        TrackedFrame old = new(queue.Stop);
        TrackedFrame accepted = new(() => throw failure);
        WriteAccepted(queue, old);

        Assert.True(queue.TryWrite(accepted, out Exception? error));
        AggregateException aggregate = Assert.IsType<AggregateException>(error);
        Assert.Same(failure, Assert.Single(aggregate.InnerExceptions));
        queue.Stop();
        queue.Dispose();
        Assert.Equal(1, old.DisposeCalls);
        Assert.Equal(1, accepted.DisposeCalls);
    }

    [Fact]
    public async Task Stop_ConcurrentCallsAttemptEachFrameOnceAndReportErrorsOnce()
    {
        using BoundedFrameQueue<TrackedFrame> queue = new();
        TrackedFrame first = new(() => throw new InvalidOperationException("第一帧"));
        TrackedFrame second = new(() => throw new IOException("第二帧"));
        WriteAccepted(queue, first);
        WriteAccepted(queue, second);
        TaskCompletionSource start = NewSignal();
        Task<Exception?>[] stops = Enumerable.Range(0, 16).Select(index => Task.Run<Exception?>(async () =>
        {
            await start.Task;
            return Record.Exception(() =>
            {
                if (index % 2 == 0)
                {
                    queue.Stop();
                }
                else
                {
                    queue.Dispose();
                }
            });
        })).ToArray();
        start.SetResult();
        Exception?[] errors = await Task.WhenAll(stops).WaitAsync(TestTimeout);
        AggregateException aggregate = Assert.IsType<AggregateException>(
            Assert.Single(errors, error => error is not null));
        Assert.Equal(2, aggregate.InnerExceptions.Count);
        Assert.Equal(1, first.DisposeCalls);
        Assert.Equal(1, second.DisposeCalls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ConcurrentWritersReadersAndStop_KeepEveryFrameOwnedAndDisposedExactlyOnce(int capacity)
    {
        for (int round = 0; round < 12; round++)
        {
            using BoundedFrameQueue<TrackedFrame> queue = new(capacity);
            TrackedFrame[] frames = Enumerable.Range(0, 400).Select(_ => new TrackedFrame()).ToArray();
            int[] accepted = new int[frames.Length];
            TaskCompletionSource start = NewSignal();
            TaskCompletionSource halfway = NewSignal();
            TaskCompletionSource stopped = NewSignal();
            int writes = 0;
            Task[] readers = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
            {
                await start.Task;
                while (true)
                {
                    TrackedFrame frame;
                    try
                    {
                        frame = await queue.ReadAsync();
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }

                    frame.Consume();
                    await Task.Yield();
                    Assert.Equal(0, frame.DisposeCalls);
                    frame.Dispose();
                }
            })).ToArray();
            Task[] writers = Enumerable.Range(0, 4).Select(writer => Task.Run(async () =>
            {
                await start.Task;
                for (int i = writer; i < frames.Length; i += 4)
                {
                    // 保证后半部分包含确定的拒绝，同时让前半部分和停止产生竞争。
                    if (i >= 300)
                    {
                        await stopped.Task;
                    }

                    bool result = queue.TryWrite(frames[i], out Exception? error);
                    Assert.Null(error);
                    accepted[i] = result ? 1 : -1;
                    if (!result)
                    {
                        Assert.Equal(0, frames[i].DisposeCalls);
                        frames[i].Dispose();
                    }

                    if (Interlocked.Increment(ref writes) == 100)
                    {
                        halfway.SetResult();
                    }
                    await Task.Yield();
                }
            })).ToArray();
            Task stop = Task.Run(async () =>
            {
                await halfway.Task;
                queue.Stop();
                stopped.SetResult();
            });
            start.SetResult();
            try
            {
                await Task.WhenAll(writers.Concat(readers).Append(stop)).WaitAsync(TestTimeout);
            }
            finally
            {
                queue.Dispose();
            }

            Assert.Contains(1, accepted);
            Assert.Contains(-1, accepted);
            Assert.DoesNotContain(0, accepted);
            for (int i = 0; i < frames.Length; i++)
            {
                Assert.Equal(1, frames[i].DisposeCalls);
                Assert.InRange(frames[i].ConsumeCalls, 0, accepted[i] == 1 ? 1 : 0);
            }
        }
    }

    [Fact]
    public async Task CapturedFrame_DropAndStopReturnBuffersButConsumerKeepsBorrowedMemoryAlive()
    {
        using BoundedFrameQueue<CapturedFrame> queue = new(1);
        CountingMemoryOwner oldOwner = new();
        CountingMemoryOwner consumerOwner = new();
        CountingMemoryOwner queuedOwner = new();
        CapturedFrame old = Capture(oldOwner);
        CapturedFrame consumer = Capture(consumerOwner);
        CapturedFrame queued = Capture(queuedOwner);
        WriteAccepted(queue, old);
        WriteAccepted(queue, consumer);
        Assert.Equal(1, oldOwner.DisposeCalls);
        Assert.Throws<ObjectDisposedException>(() => old.Pixels);
        Assert.Same(consumer, await queue.ReadAsync());
        ReadOnlyMemory<byte> borrowed = consumer.Pixels;
        WriteAccepted(queue, queued);

        queue.Stop();
        queue.Dispose();
        Assert.Equal(1, queuedOwner.DisposeCalls);
        Assert.Equal(0, consumerOwner.DisposeCalls);
        Assert.Equal((byte)42, borrowed.Span[0]);
        consumer.Dispose();
        Assert.Equal(1, consumerOwner.DisposeCalls);
        Assert.Throws<ObjectDisposedException>(() => consumer.Pixels);
        Assert.Throws<ObjectDisposedException>(() => queued.Pixels);
        // 借用视图在消费者 Dispose 后不再读取。
    }

    [Fact]
    public async Task EncodedFrame_DropAndStopReturnBuffersButConsumerKeepsBorrowedMemoryAlive()
    {
        using BoundedFrameQueue<EncodedFrame> queue = new(1);
        CountingMemoryOwner oldOwner = new();
        CountingMemoryOwner consumerOwner = new();
        CountingMemoryOwner queuedOwner = new();
        EncodedFrame old = Encode(oldOwner);
        EncodedFrame consumer = Encode(consumerOwner);
        EncodedFrame queued = Encode(queuedOwner);
        WriteAccepted(queue, old);
        WriteAccepted(queue, consumer);
        Assert.Equal(1, oldOwner.DisposeCalls);
        Assert.Throws<ObjectDisposedException>(() => old.Payload);
        Assert.Same(consumer, await queue.ReadAsync());
        ReadOnlyMemory<byte> borrowed = consumer.Payload;
        WriteAccepted(queue, queued);

        queue.Dispose();
        queue.Stop();
        Assert.Equal(1, queuedOwner.DisposeCalls);
        Assert.Equal(0, consumerOwner.DisposeCalls);
        Assert.Equal((byte)42, borrowed.Span[0]);
        consumer.Dispose();
        Assert.Equal(1, consumerOwner.DisposeCalls);
        Assert.Throws<ObjectDisposedException>(() => consumer.Payload);
        Assert.Throws<ObjectDisposedException>(() => queued.Payload);
    }

    [Fact]
    public void PublicApi_DoesNotExposeChannelEndpointsOrAsyncWrite()
    {
        Type type = typeof(BoundedFrameQueue<TrackedFrame>);
        BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        Assert.Empty(type.GetFields(flags));
        Assert.Empty(type.GetEvents(flags));
        Assert.Equal(new[] { "Capacity" }, type.GetProperties(flags).Select(property => property.Name));
        Assert.Equal(new[] { "Dispose", "ReadAsync", "Stop", "TryWrite" },
            type.GetMethods(flags).Where(method => !method.IsSpecialName)
                .Select(method => method.Name).OrderBy(name => name));
    }

    private static void WriteAccepted<T>(BoundedFrameQueue<T> queue, T frame) where T : class, IDisposable
    {
        Assert.True(queue.TryWrite(frame, out Exception? error));
        Assert.Null(error);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static CapturedFrame Capture(IMemoryOwner<byte> owner) =>
        new(new DisplayId(0), 1, 1, 4, FramePixelFormat.Bgra32, owner, 0);

    private static EncodedFrame Encode(IMemoryOwner<byte> owner) =>
        new(VideoCodec.Jpeg, 1, 1, 1, 0, 60, owner);

    private sealed class TrackedFrame(Action? onDispose = null) : IDisposable
    {
        private int _disposeCalls;
        private int _consumeCalls;

        public int DisposeCalls => Volatile.Read(ref _disposeCalls);
        public int ConsumeCalls => Volatile.Read(ref _consumeCalls);

        public void Consume()
        {
            Assert.Equal(1, Interlocked.Increment(ref _consumeCalls));
            Assert.Equal(0, DisposeCalls);
        }

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCalls);
            onDispose?.Invoke();
        }
    }

    private sealed class CountingMemoryOwner : IMemoryOwner<byte>
    {
        private readonly byte[] _bytes = [42, 0, 0, 0];
        public int DisposeCalls { get; private set; }
        public Memory<byte> Memory
        {
            get
            {
                ObjectDisposedException.ThrowIf(DisposeCalls != 0, this);
                return _bytes;
            }
        }

        public void Dispose() => DisposeCalls++;
    }
}
