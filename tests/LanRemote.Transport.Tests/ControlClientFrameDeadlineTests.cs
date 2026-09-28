using System.Reflection;

namespace LanRemote.Transport.Tests;

public sealed partial class ControlClientConnectorTests
{
    // 受控 Stream + 真实客户端读帧/预算判定路径，不将它冒充为 TLS 集成测试。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Early_Read_Timer_Cannot_Turn_The_Approval_Window_Into_A_Frame_Timeout(bool payload)
    {
        using FrameReadScenario scenario = new(approval: true, payload, shortStage: false);
        scenario.Clock.Advance(TimeSpan.FromMilliseconds(399.5));
        Assert.False(scenario.Reading.IsCompleted);

        scenario.FireReadTimerEarly();
        bool cancelledEarly = scenario.Stream.ReadToken.IsCancellationRequested;
        if (!cancelledEarly)
        {
            Assert.False(scenario.Reading.IsCompleted);
            Assert.Equal(scenario.Clock.LastTimer.Token, scenario.Stream.ReadToken);
            // 一毫秒的通知排期不能成为接受宽限：到原始 400ms 时先复核绝对截止。
            scenario.Clock.Advance(TimeSpan.FromMilliseconds(0.5));
            scenario.Clock.LastTimer.FireCaptured();
        }

        ControlClientAuthenticationException error = await Assert.ThrowsAsync<ControlClientAuthenticationException>(
            () => scenario.Reading.WaitAsync(Guard));
        Assert.Equal("client-approval-timeout", error.Rejection);
        Assert.False(cancelledEarly);
        Assert.Equal(TimeSpan.FromMilliseconds(400).Ticks, scenario.Clock.GetTimestamp());
        if (payload)
        {
            Assert.All(scenario.Stream.PayloadBuffer.ToArray(), value => Assert.Equal((byte)0, value));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Short_Frame_Deadline_Still_Reports_Frame_Timeout_Before_The_Outer_Window(
        bool approval, bool payload)
    {
        using FrameReadScenario scenario = new(approval, payload, shortStage: true);
        Assert.Equal(TimeSpan.FromMilliseconds(100), scenario.Clock.LastTimer.Budget);
        scenario.Clock.Advance(TimeSpan.FromMilliseconds(99.5));
        scenario.FireReadTimerEarly();
        bool cancelledEarly = scenario.Stream.ReadToken.IsCancellationRequested;
        if (!cancelledEarly)
        {
            Assert.False(scenario.Reading.IsCompleted);
            scenario.Clock.Advance(TimeSpan.FromMilliseconds(0.5));
            scenario.Clock.LastTimer.FireCaptured();
        }

        ControlClientAuthenticationException error = await Assert.ThrowsAsync<ControlClientAuthenticationException>(
            () => scenario.Reading.WaitAsync(Guard));
        Assert.Equal("client-frame-timeout", error.Rejection);
        Assert.False(cancelledEarly);
        Assert.Equal(TimeSpan.FromMilliseconds(100).Ticks, scenario.Clock.GetTimestamp());
        Assert.False(scenario.Caller.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false, false, "cancel")]
    [InlineData(false, false, "io")]
    [InlineData(false, false, "eof")]
    [InlineData(false, false, "protocol")]
    [InlineData(false, true, "cancel")]
    [InlineData(false, true, "io")]
    [InlineData(false, true, "eof")]
    [InlineData(false, true, "protocol")]
    [InlineData(true, true, "cancel")]
    [InlineData(true, true, "io")]
    [InlineData(true, true, "eof")]
    [InlineData(true, true, "protocol")]
    public async Task Frame_Read_Failure_Preserves_Caller_Then_Absolute_Deadline_Then_Frame_Error(
        bool callerCancelled, bool expired, string failureKind)
    {
        using FrameReadScenario scenario = new(approval: true, payload: true, shortStage: false, cooperative: false);
        Exception? failure = failureKind switch
        {
            "cancel" => new OperationCanceledException("受控读取取消。"),
            "io" => new IOException("受控读取故障。"),
            "protocol" => new FrameProtocolException(FrameReader.RejectTooLarge),
            _ => null,
        };
        // 不派发到期 timer，排除取消通知与读失败的调度顺序对结论的影响。
        scenario.Clock.Advance(TimeSpan.FromMilliseconds(expired ? 400 : 200));
        if (callerCancelled)
        {
            scenario.Caller.Cancel();
        }
        scenario.Stream.Fail(failure);

        Exception? error = await Record.ExceptionAsync(() => scenario.Reading.WaitAsync(Guard));
        if (callerCancelled)
        {
            OperationCanceledException cancelled = Assert.IsAssignableFrom<OperationCanceledException>(error);
            Assert.Equal(scenario.Caller.Token, cancelled.CancellationToken);
        }
        else if (expired || failureKind == "cancel")
        {
            ControlClientAuthenticationException rejected = Assert.IsType<ControlClientAuthenticationException>(error);
            Assert.Equal(expired ? "client-approval-timeout" : "client-frame-timeout", rejected.Rejection);
        }
        else if (failure is not null)
        {
            Assert.Same(failure, error);
        }
        else
        {
            Assert.IsType<EndOfStreamException>(error);
        }
        Assert.All(scenario.Stream.PayloadBuffer.ToArray(), value => Assert.Equal((byte)0, value));
    }

    private sealed class FrameReadScenario : IDisposable
    {
        private readonly IDisposable _window;
        public FrameReadClock Clock { get; } = new();
        public CancellationTokenSource Caller { get; } = new();
        public PendingFrameStream Stream { get; }
        public Task<(byte[] Payload, long ReceivedAt)> Reading { get; }

        public FrameReadScenario(bool approval, bool payload, bool shortStage, bool cooperative = true)
        {
            Stream = new PendingFrameStream(payload, cooperative);
            // 只打开既有私有边界做单元测试，不为测试扩大产品 API。
            Type windowType = typeof(ControlClientConnector).GetNestedType("ClientAuthWindow", BindingFlags.NonPublic)!;
            _window = (IDisposable)Activator.CreateInstance(windowType,
                Clock, TimeSpan.FromMilliseconds(400), Caller.Token,
                approval ? "client-approval-timeout" : "client-machine-timeout", null, null)!;
            MethodInfo read = typeof(ControlClientConnector).GetMethod("ReadFrameAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
            Reading = (Task<(byte[], long)>)read.Invoke(null,
                [new FrameReader(Stream), _window, Timeouts(prefixMs: shortStage ? 100 : 1000,
                    payloadMs: shortStage ? 100 : 1000), approval])!;
        }

        public void FireReadTimerEarly()
        {
            if (Stream.ReadToken != Clock.LastTimer.Token)
            {
                // 旧实现额外的 FrameReader CTS 没有注入时钟。直接取消这只实际 CTS，
                // 确定性模拟它的 CancelAfter 提前触发；不是向读取器凭空抛一个 OCE。
                // 修复后直接传递已有 deadline.Token，不再存在这只独立计时的 CTS。
                FieldInfo sourceField = typeof(CancellationToken).GetField("_source", BindingFlags.Instance | BindingFlags.NonPublic)!;
                CancellationTokenSource source = (CancellationTokenSource)sourceField.GetValue(Stream.ReadToken)!;
                source.Cancel();
                return;
            }
            Clock.LastTimer.FireEarly();
        }

        public void Dispose()
        {
            Caller.Cancel();
            _window.Dispose();
            Stream.Dispose();
            Caller.Dispose();
        }
    }

    private sealed class FrameReadClock : TimeProvider
    {
        private readonly ManualDeadlineClock _inner = new();
        public FrameReadTimer LastTimer { get; private set; } = null!;
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override long GetTimestamp() => _inner.GetTimestamp();
        public void Advance(TimeSpan elapsed) => _inner.Advance(elapsed, fireTimers: false);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer inner = _inner.CreateTimer(callback, state, dueTime, period);
            LastTimer = new FrameReadTimer(inner, callback, state,
                ((AuthenticationDeadline)state!).Token, dueTime);
            return LastTimer;
        }
    }

    private sealed class FrameReadTimer(
        ITimer inner, TimerCallback callback, object? state, CancellationToken token, TimeSpan budget) : ITimer
    {
        public CancellationToken Token { get; } = token;
        public TimeSpan Budget { get; } = budget;
        public void FireEarly()
        {
            inner.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            FireCaptured();
        }
        public void FireCaptured() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);
        public void Dispose() => inner.Dispose();
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class PendingFrameStream(bool payload, bool cooperative) : Stream
    {
        private readonly TaskCompletionSource<int> _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;
        public CancellationToken ReadToken { get; private set; }
        public Memory<byte> PayloadBuffer { get; private set; }
        public void Fail(Exception? error)
        {
            if (error is null)
            {
                _pending.SetResult(0);
            }
            else
            {
                _pending.SetException(error);
            }
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _reads++;
            if (payload && _reads == 1)
            {
                new byte[] { 0, 0, 0, 64 }.AsSpan().CopyTo(buffer.Span);
                return ValueTask.FromResult(4);
            }
            if (payload && _reads == 2)
            {
                PayloadBuffer = buffer;
                buffer.Span.Fill(0xCD);
                return ValueTask.FromResult(1);
            }
            ReadToken = cancellationToken;
            return new ValueTask<int>(cooperative ? _pending.Task.WaitAsync(cancellationToken) : _pending.Task);
        }
        protected override void Dispose(bool disposing)
        {
            _pending.TrySetResult(0);
            base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
