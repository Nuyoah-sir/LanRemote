using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using LanRemote.Core.Models;

namespace LanRemote.Transport.Tests;

public sealed class HostDisplayRequestReaderTests
{
    private const int TestTimeout = 60_000;
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly Guid SessionId = Guid.Parse("c139b29e-201a-4b97-ab4b-7f843d94fe62");

    [Fact(Timeout = TestTimeout)]
    public async Task ConstructionDoesNotReadOrSampleClockAndStartPublishesOneOriginalWorker()
    {
        Fixture f = new();
        try
        {
            Assert.Null(f.Reader.Completion);
            Assert.Null(f.Reader.Error);
            Assert.False(f.Reader.StopRequired.IsCompleted);
            Assert.Equal(0, f.Stream.CallCount);
            Assert.Equal(0, f.Clock.TimerCount);
            Task completion = f.Reader.Start();
            Assert.Same(completion, f.Reader.Completion);
            Assert.Throws<InvalidOperationException>(() => { _ = f.Reader.Start(); });
            Assert.Equal(0, f.Stream.CallCount);
            Assert.False(completion.IsCompleted);
            f.First.TrySetResult(null);
            await completion.WaitAsync(Guard);
            Assert.Null(f.Reader.Error);
            Assert.True(f.Close.Task.IsCompletedSuccessfully);
            Assert.True(f.Reader.StopRequired.IsCompletedSuccessfully);
            Assert.Equal(0, f.Stream.DisposeCount);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(1)]
    [InlineData(19)]
    [InlineData(4096)]
    public async Task ExternalZeroByteIsConsumedExactlyOnceAndFragmentedRequestDispatches(int chunk)
    {
        Fixture f = new();
        f.Stream.Chunk = chunk;
        try
        {
            await f.Start().WaitAsync(Guard);
            DisplaySwitchRequestFrame frame = Assert.Single(f.Requests);
            Assert.Equal(SessionId, frame.SessionId);
            Assert.Equal(0, frame.Sequence);
            Assert.Equal(7, frame.CatalogSequence);
            Assert.Equal(2, frame.DisplayId.Value);
            Assert.Equal(3, f.Stream.Reads.First().Length);
            Assert.Equal(f.Stream.Data.Length, f.Stream.Offset);
            Assert.Null(f.Reader.Error);
            Assert.True(f.Close.Task.IsCompletedSuccessfully);
            Assert.Equal(0, f.Clock.TimerCount);
            Assert.Equal(0, f.Stream.DisposeCount);
            Assert.All(f.Stream.Reads, memory => Assert.True(memory.Span.IndexOfAnyExcept((byte)0) < 0));
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(1)]
    [InlineData(255)]
    public async Task NonzeroExternalFirstByteRejectsWithoutContinuationOrTimer(byte value)
    {
        Fixture f = new();
        try
        {
            await f.Start(value).WaitAsync(Guard);
            Assert.IsType<FrameProtocolException>(f.Reader.Error);
            Assert.Empty(f.Requests);
            Assert.Equal(0, f.Stream.CallCount);
            Assert.Equal(0, f.Clock.TimerCount);
            Assert.True(f.Close.Task.IsCompletedSuccessfully);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("type")]
    [InlineData("session")]
    [InlineData("json")]
    [InlineData("duplicate")]
    [InlineData("protocol")]
    public async Task WrongDirectionSessionAndInvalidJsonNeverReachHandler(string kind)
    {
        string json = Encoding.UTF8.GetString(RequestPayload());
        json = kind switch
        {
            "type" => json.Replace("request_display", "display_changed"),
            "session" => json.Replace(SessionId.ToString("D"), Guid.Empty.ToString("D")),
            "json" => "{",
            "duplicate" => json.Replace("\"seq\":0", "\"seq\":0,\"seq\":1"),
            _ => json.Replace("\"protocol\":1", "\"protocol\":2"),
        };
        Fixture f = new(Frame(Encoding.UTF8.GetBytes(json))[1..]);
        try
        {
            await f.Start().WaitAsync(Guard);
            Assert.IsType<FrameProtocolException>(f.Reader.Error);
            Assert.Empty(f.Requests);
            Assert.True(f.Close.Task.IsCompletedSuccessfully);
            Assert.All(f.Stream.Reads, memory => Assert.True(memory.Span.IndexOfAnyExcept((byte)0) < 0));
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(0)]
    [InlineData(4097)]
    public async Task InvalidLengthRejectsBeforePayloadRead(int length)
    {
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)length);
        Fixture f = new(prefix[1..]);
        try
        {
            await f.Start().WaitAsync(Guard);
            Assert.IsType<FrameProtocolException>(f.Reader.Error);
            Assert.Empty(f.Requests);
            Assert.Equal(1, f.Stream.CallCount);
            Assert.True(f.Close.Task.IsCompletedSuccessfully);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PartialLengthPrefixPreservesOriginalEofError(int remaining)
    {
        Fixture f = new(new byte[remaining]);
        try
        {
            await f.Start().WaitAsync(Guard);
            Assert.IsType<EndOfStreamException>(f.Reader.Error);
            Assert.Empty(f.Requests);
            Assert.True(f.Close.Task.IsCompletedSuccessfully);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Exactly4096BytePayloadIsAcceptedWithoutChangingRequestSemantics()
    {
        byte[] payload = RequestPayload();
        byte[] padded = [.. payload, .. Enumerable.Repeat((byte)' ', 4096 - payload.Length)];
        Fixture f = new(Frame(padded)[1..]);
        try
        {
            await f.Start().WaitAsync(Guard);
            Assert.Single(f.Requests);
            Assert.Null(f.Reader.Error);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task SecondRequestWhileOriginalHandlerRunsClosesWithoutQueueing()
    {
        // 第二请求故意复用同一seq：业务反重放由handler负责，reader不伪授权。
        Fixture f = new([.. Frame(RequestPayload())[1..], .. Frame(RequestPayload())]);
        HeldRead next = f.Stream.Hold(3);
        TaskCompletionSource firstHandler = f.NewHeldTask();
        TaskCompletionSource reached = NewSignal();
        f.Handler = (_, _) =>
        {
            if (f.Requests.Count != 1) return Task.CompletedTask;
            reached.TrySetResult();
            return firstHandler.Task;
        };
        try
        {
            Task completion = f.Start();
            await reached.Task.WaitAsync(Guard);
            await next.Reached.Task.WaitAsync(Guard);
            Assert.Equal(3, f.Stream.CallCount);
            Assert.Single(f.Requests);
            Assert.False(completion.IsCompleted);
            next.Release();
            await f.Close.Task.WaitAsync(Guard);
            Assert.False(completion.IsCompleted);
            firstHandler.TrySetResult();
            await completion.WaitAsync(Guard);
            Assert.Single(f.Requests);
            Assert.Equal("request-display-handler-busy", Assert.IsType<FrameProtocolException>(f.Reader.Error).Reason);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopNotifiesCloseButJoinsAlreadyStartedExternalFirstRead(bool beforeStart)
    {
        Fixture f = new();
        try
        {
            if (beforeStart) f.Reader.RequestStop();
            Task completion = f.Reader.Start();
            f.Reader.RequestStop();
            Assert.True(f.Close.Task.IsCompletedSuccessfully);
            Assert.True(f.Reader.StopRequired.IsCompletedSuccessfully);
            Assert.False(completion.IsCompleted);
            Assert.Equal(0, f.Stream.CallCount);
            f.First.TrySetResult(0);
            await completion.WaitAsync(Guard);
            Assert.Null(f.Reader.Error);
            Assert.Empty(f.Requests);
            Assert.Equal(0, f.Stream.CallCount);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task DisposeBeforeStartStillJoinsExternalFirstReadWithoutClosingBorrowedStream()
    {
        Fixture f = new();
        try
        {
            Task disposing = f.Reader.DisposeAsync().AsTask();
            Assert.Same(disposing, f.Reader.Completion);
            Assert.True(f.Close.Task.IsCompletedSuccessfully);
            Assert.False(disposing.IsCompleted);
            Assert.Equal(0, f.Stream.DisposeCount);
            f.First.TrySetResult(null);
            await disposing.WaitAsync(Guard);
            Assert.Null(f.Reader.Error);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StopBeforeOriginalContinuationExitNotifiesButNeverDispatches(int call)
    {
        Fixture f = new();
        HeldRead held = f.Stream.Hold(call);
        try
        {
            Task completion = f.Start();
            await held.Reached.Task.WaitAsync(Guard);
            f.Reader.RequestStop();
            Assert.True(f.Close.Task.IsCompletedSuccessfully);
            Assert.False(held.Original.Task.IsCompleted);
            Assert.False(completion.IsCompleted);
            Assert.Equal(call, f.Stream.CallCount);
            held.Release();
            await completion.WaitAsync(Guard);
            Assert.True(held.Original.Task.IsCompletedSuccessfully);
            Assert.Empty(f.Requests);
            Assert.Null(f.Reader.Error);
            Assert.Equal(call, f.Stream.CallCount);
            Assert.Equal(0, f.Stream.DisposeCount);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task IdleReadAheadHasNoTimerAndStopJoinsItsOriginalRead()
    {
        Fixture f = new();
        HeldRead held = f.Stream.Hold(3);
        try
        {
            Task completion = f.Start();
            await held.Reached.Task.WaitAsync(Guard);
            Assert.Single(f.Requests);
            Assert.Equal(1, f.Stream.Reads.Last().Length);
            Assert.Equal(0, f.Clock.TimerCount);
            f.Reader.RequestStop();
            Assert.True(f.Close.Task.IsCompletedSuccessfully);
            Assert.False(completion.IsCompleted);
            held.Release();
            await completion.WaitAsync(Guard);
            Assert.Null(f.Reader.Error);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task StopDuringHandlerSynchronousPrefixDoesNotBlockIdleReader()
    {
        Fixture f = new();
        Pause prefix = f.NewPause();
        TaskCompletionSource handler = f.NewHeldTask();
        f.Handler = (_, _) => { prefix.Block(); return handler.Task; };
        try
        {
            Task completion = f.Start();
            await prefix.Reached.Task.WaitAsync(Guard);
            Task stopping = f.Keep(Task.Run(f.Reader.RequestStop));
            await stopping.WaitAsync(Guard);
            Assert.True(f.Close.Task.IsCompletedSuccessfully);
            Assert.False(completion.IsCompleted);
            Assert.InRange(f.Stream.CallCount, 2, 3);
            prefix.Open();
            Assert.False(completion.IsCompleted);
            handler.TrySetResult();
            await completion.WaitAsync(Guard);
            Assert.Null(f.Reader.Error);
            Assert.InRange(f.Stream.CallCount, 2, 3);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task BlockingCancellationCallbackRunsOutsideStopAndIsJoinedByCompletion()
    {
        Fixture f = new();
        Pause callback = f.NewPause();
        TaskCompletionSource entered = NewSignal();
        TaskCompletionSource handler = f.NewHeldTask();
        f.Handler = (_, token) =>
        {
            f.Registrations.Enqueue(token.Register(callback.Block));
            entered.TrySetResult();
            return handler.Task;
        };
        try
        {
            Task completion = f.Start();
            await entered.Task.WaitAsync(Guard);
            Task stopping = f.Keep(Task.Run(f.Reader.RequestStop));
            await stopping.WaitAsync(Guard);
            await callback.Reached.Task.WaitAsync(Guard);
            Assert.True(f.Close.Task.IsCompletedSuccessfully);
            handler.TrySetResult();
            Assert.False(completion.IsCompleted);
            callback.Open();
            await completion.WaitAsync(Guard);
            Assert.Null(f.Reader.Error);
            Assert.InRange(f.Stream.CallCount, 2, 3);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ExternalFirstTaskPreservesSiblingOrderNestedTreeAndRepeatedReferences()
    {
        Fixture f = new();
        IOException same = new("same");
        AggregateException nested = new("nested", new OperationCanceledException("faulted-oce"), same);
        try
        {
            Task completion = f.Reader.Start();
            f.First.TrySetException([same, nested, same]);
            await completion.WaitAsync(Guard);
            AggregateException tree = Assert.IsType<AggregateException>(f.Reader.Error);
            Assert.Equal(3, tree.InnerExceptions.Count);
            Assert.Same(same, tree.InnerExceptions[0]);
            Assert.Same(nested, tree.InnerExceptions[1]);
            Assert.Same(same, tree.InnerExceptions[2]);
            Assert.Equal(0, f.Stream.CallCount);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task LateContinuationFaultRetainsAllOriginalRootsAfterStop()
    {
        Fixture f = new();
        HeldRead held = f.Stream.Hold(2);
        OperationCanceledException oce = new("faulted-not-canceled");
        IOException io = new("io");
        try
        {
            Task completion = f.Start();
            await held.Reached.Task.WaitAsync(Guard);
            f.Reader.RequestStop();
            held.Original.TrySetException([oce, io, oce]);
            await completion.WaitAsync(Guard);
            AggregateException tree = Assert.IsType<AggregateException>(f.Reader.Error);
            Assert.Equal(new Exception[] { oce, io, oce }, tree.InnerExceptions);
            Assert.Empty(f.Requests);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task DistinctContinuationAndCleanupSlotsRetainTheSameExceptionReferenceTwice()
    {
        IOException same = new("same-slot-root");
        Fixture f = new(clock: new CleanupFaultClock(same));
        HeldRead held = f.Stream.Hold(2);
        try
        {
            Task completion = f.Start();
            await held.Reached.Task.WaitAsync(Guard);
            f.Reader.RequestStop();
            held.Original.TrySetException(same);
            await completion.WaitAsync(Guard);
            AggregateException tree = Assert.IsType<AggregateException>(f.Reader.Error);
            Assert.Equal(2, tree.InnerExceptions.Count);
            Assert.Same(same, tree.InnerExceptions[0]);
            Assert.Same(same, tree.InnerExceptions[1]);
            Assert.Empty(f.Requests);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task LateHandlerFaultAndCancellationCallbackFaultAreBothPreserved()
    {
        Fixture f = new();
        TaskCompletionSource handler = f.NewHeldTask();
        TaskCompletionSource reached = NewSignal();
        IOException first = new("handler-first");
        OperationCanceledException second = new("handler-faulted-oce");
        InvalidOperationException callback = new("cancel-callback");
        f.Handler = (_, token) =>
        {
            f.Registrations.Enqueue(token.Register(() => throw callback));
            reached.TrySetResult();
            return handler.Task;
        };
        try
        {
            Task completion = f.Start();
            await reached.Task.WaitAsync(Guard);
            f.Reader.RequestStop();
            handler.TrySetException([first, second, first]);
            await completion.WaitAsync(Guard);
            AggregateException tree = Assert.IsType<AggregateException>(f.Reader.Error);
            Assert.Equal(2, tree.InnerExceptions.Count);
            AggregateException handlerTree = Assert.IsType<AggregateException>(tree.InnerExceptions[0]);
            Assert.Equal(new Exception[] { first, second, first }, handlerTree.InnerExceptions);
            AggregateException cancelTree = Assert.IsType<AggregateException>(tree.InnerExceptions[1]);
            Assert.Same(callback, Assert.Single(cancelTree.InnerExceptions));
            Assert.InRange(f.Stream.CallCount, 2, 3);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task HandlerReturningNullIsRejectedWithoutNextFirstRead()
    {
        Fixture f = new();
        f.Handler = (_, _) => null!;
        try
        {
            await f.Start().WaitAsync(Guard);
            Assert.IsType<InvalidOperationException>(f.Reader.Error);
            Assert.Single(f.Requests);
            Assert.InRange(f.Stream.CallCount, 2, 3);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task HostCancellationOnlyNotifiesAndCooperativeHandlerCancellationIsNotAnError()
    {
        using CancellationTokenSource host = new();
        Fixture f = new(hostToken: host.Token);
        TaskCompletionSource reached = NewSignal();
        f.Handler = (_, token) =>
        {
            reached.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        try
        {
            Task completion = f.Start();
            await reached.Task.WaitAsync(Guard);
            host.Cancel();
            Assert.True(f.Close.Task.IsCompletedSuccessfully);
            await completion.WaitAsync(Guard);
            Assert.Null(f.Reader.Error);
            Assert.InRange(f.Stream.CallCount, 2, 3);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task TenSecondBudgetUsesOriginalAnchorAcrossPrefixAndPayload()
    {
        Fixture f = new();
        HeldRead prefix = f.Stream.Hold(1);
        HeldRead payload = f.Stream.Hold(2);
        try
        {
            Task completion = f.Start();
            await prefix.Reached.Task.WaitAsync(Guard);
            f.Clock.Advance(TimeSpan.FromSeconds(9));
            prefix.Release();
            await payload.Reached.Task.WaitAsync(Guard);
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            await f.Close.Task.WaitAsync(Guard);
            Assert.False(payload.Original.Task.IsCompleted);
            Assert.False(completion.IsCompleted);
            payload.Release();
            await completion.WaitAsync(Guard);
            Assert.IsType<TimeoutException>(f.Reader.Error);
            Assert.Empty(f.Requests);
            Assert.Equal(0, f.Clock.TimerCount);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("deadline")]
    [InlineData("regression")]
    [InlineData("frequency")]
    public async Task ParseFinalSampleRejectsBoundaryRegressionAndFrequencyChange(string mode)
    {
        FinalSampleClock clock = new(mode);
        Fixture f = new(clock: clock);
        try
        {
            await f.Start().WaitAsync(Guard);
            Assert.Equal(3, clock.SamplesAfterDispose);
            if (mode == "deadline") Assert.IsType<TimeoutException>(f.Reader.Error);
            else Assert.IsType<InvalidOperationException>(f.Reader.Error);
            Assert.Empty(f.Requests);
            Assert.True(f.Close.Task.IsCompletedSuccessfully);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task EofStopsParentBeforeNoncooperativeHandlerReturns()
    {
        Fixture f = new();
        TaskCompletionSource handler = f.NewHeldTask();
        f.Handler = (_, _) => handler.Task;
        try
        {
            Task completion = f.Start();
            await f.Dispatched.Task.WaitAsync(Guard);
            await f.Close.Task.WaitAsync(Guard);
            Assert.True(f.Reader.StopRequired.IsCompletedSuccessfully);
            Assert.False(handler.Task.IsCompleted);
            Assert.False(completion.IsCompleted);
            Assert.Equal(3, f.Stream.CallCount);
            handler.TrySetResult();
            await completion.WaitAsync(Guard);
            Assert.Null(f.Reader.Error);
            Assert.Single(f.Requests);
        }
        finally { await f.FinishAsync(); }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static byte[] RequestPayload() => new DisplaySwitchRequestFrame(SessionId, 0, 17, 7, new DisplayId(2)).Serialize();
    private static byte[] Frame(byte[] payload)
    {
        byte[] framed = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(framed, (uint)payload.Length);
        payload.CopyTo(framed, 4);
        return framed;
    }

    private sealed class Fixture
    {
        internal readonly ManualDeadlineClock Clock = new();
        internal readonly TaskCompletionSource<byte?> First = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Close = NewSignal();
        internal readonly TaskCompletionSource Dispatched = NewSignal();
        internal readonly ConcurrentQueue<DisplaySwitchRequestFrame> Requests = new();
        internal readonly ConcurrentQueue<CancellationTokenRegistration> Registrations = new();
        internal readonly ControlledStream Stream;
        internal readonly HostDisplayRequestReader Reader;
        internal Func<DisplaySwitchRequestFrame, CancellationToken, Task> Handler = (_, _) => Task.CompletedTask;
        private readonly List<TaskCompletionSource> _handlers = [];
        private readonly List<Pause> _pauses = [];
        private readonly List<Task> _workers = [];

        internal Fixture(byte[]? data = null, TimeProvider? clock = null, CancellationToken hostToken = default)
        {
            Stream = new(data ?? Frame(RequestPayload())[1..]) { BeforeIdleEof = Dispatched.Task };
            Reader = new(Stream, First.Task, SessionId, clock ?? Clock, TimeSpan.FromSeconds(10),
                (frame, token) =>
                {
                    Requests.Enqueue(frame);
                    try { return Handler(frame, token); }
                    finally { Dispatched.TrySetResult(); }
                }, Close, hostToken);
        }
        internal Task Start(byte? first = 0) { First.TrySetResult(first); return Reader.Start(); }
        internal TaskCompletionSource NewHeldTask()
        {
            TaskCompletionSource source = NewSignal();
            _handlers.Add(source);
            return source;
        }
        internal Pause NewPause() { Pause pause = new(); _pauses.Add(pause); return pause; }
        internal Task Keep(Task worker) { _workers.Add(worker); return worker; }
        internal async Task FinishAsync()
        {
            First.TrySetResult(null);
            Dispatched.TrySetResult();
            foreach (Pause pause in _pauses) pause.Open();
            Stream.ReleaseAll();
            foreach (TaskCompletionSource handler in _handlers) handler.TrySetResult();
            Reader.RequestStop();
            // 正文watchdog只诊断；finally释放所有原闸并直接join原worker，不等待纯结果promise。
            await Reader.DisposeAsync();
            foreach (Task worker in _workers) await worker;
            foreach (CancellationTokenRegistration registration in Registrations) await registration.DisposeAsync();
            foreach (Pause pause in _pauses) pause.Dispose();
        }
    }

    private sealed class Pause : IDisposable
    {
        private readonly ManualResetEventSlim _open = new();
        internal TaskCompletionSource Reached { get; } = NewSignal();
        internal void Block() { Reached.TrySetResult(); _open.Wait(); }
        internal void Open() => _open.Set();
        public void Dispose() => _open.Dispose();
    }

    private sealed class HeldRead
    {
        private readonly object _gate = new();
        private Func<int>? _fill;
        private bool _released;
        internal TaskCompletionSource Reached { get; } = NewSignal();
        internal TaskCompletionSource<int> Original { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ValueTask<int> Enter(Func<int> fill)
        {
            lock (_gate)
            {
                _fill = fill;
                Reached.TrySetResult();
                if (_released && !Original.Task.IsCompleted) Original.TrySetResult(fill());
                return new(Original.Task);
            }
        }
        internal void Release()
        {
            lock (_gate)
            {
                _released = true;
                if (_fill is not null && !Original.Task.IsCompleted) Original.TrySetResult(_fill());
            }
        }
    }

    private sealed class ControlledStream(byte[] data) : Stream
    {
        private int _calls, _offset, _disposed;
        private readonly Dictionary<int, HeldRead> _held = [];
        internal ConcurrentQueue<Memory<byte>> Reads { get; } = new();
        internal byte[] Data => data;
        internal int Chunk { get; set; } = int.MaxValue;
        internal Task BeforeIdleEof { get; init; } = Task.CompletedTask;
        internal int CallCount => Volatile.Read(ref _calls);
        internal int Offset => Volatile.Read(ref _offset);
        internal int DisposeCount => Volatile.Read(ref _disposed);
        internal HeldRead Hold(int call) { HeldRead held = new(); _held.Add(call, held); return held; }
        internal void ReleaseAll() { foreach (HeldRead held in _held.Values) held.Release(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _calls);
            Reads.Enqueue(buffer);
            if (_held.TryGetValue(call, out HeldRead? held)) return held.Enter(() => Fill(buffer));
            if (_offset == data.Length && buffer.Length == 1 && call > 2) return new(IdleEofAsync());
            return ValueTask.FromResult(Fill(buffer));
        }
        private async Task<int> IdleEofAsync() { await BeforeIdleEof; return 0; }
        private int Fill(Memory<byte> buffer)
        {
            int count = Math.Min(Math.Min(buffer.Length, Chunk), data.Length - _offset);
            data.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
        protected override void Dispose(bool disposing) { Interlocked.Increment(ref _disposed); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class CleanupFaultClock(Exception error) : TimeProvider
    {
        private readonly ManualDeadlineClock _inner = new();
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override long GetTimestamp() => _inner.GetTimestamp();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new Timer(_inner.CreateTimer(callback, state, dueTime, period), error);
        private sealed class Timer(ITimer inner, Exception error) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);
            public void Dispose() => inner.Dispose();
            public ValueTask DisposeAsync()
            {
                inner.Dispose();
                return new(Task.FromException(error));
            }
        }
    }

    private sealed class FinalSampleClock(string mode) : TimeProvider
    {
        private readonly ManualDeadlineClock _inner = new();
        private bool _disposed, _frequencyChanged;
        internal int SamplesAfterDispose { get; private set; }
        public override long TimestampFrequency => _frequencyChanged ? TimeSpan.TicksPerSecond + 1 : TimeSpan.TicksPerSecond;
        public override long GetTimestamp()
        {
            if (!_disposed) return _inner.GetTimestamp();
            SamplesAfterDispose++;
            if (SamplesAfterDispose == 1) _inner.Advance(TimeSpan.FromSeconds(1), false);
            if (SamplesAfterDispose == 3)
            {
                if (mode == "deadline") _inner.Advance(TimeSpan.FromSeconds(9), false);
                if (mode == "regression") return 0;
                if (mode == "frequency") _frequencyChanged = true;
            }
            return _inner.GetTimestamp();
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new Timer(_inner.CreateTimer(callback, state, dueTime, period), this);
        private sealed class Timer(ITimer inner, FinalSampleClock parent) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);
            public void Dispose() { inner.Dispose(); parent._disposed = true; }
            public async ValueTask DisposeAsync() { await inner.DisposeAsync(); parent._disposed = true; }
        }
    }
}
