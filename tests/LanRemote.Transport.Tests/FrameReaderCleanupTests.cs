namespace LanRemote.Transport.Tests;

/// <summary>
/// 仅用自定义流验证 payload 数组清理，不代表真实 TLS 的异常类型或内部缓冲区行为。
/// </summary>
public sealed class FrameReaderCleanupTests
{
    private const int PayloadLength = 64;
    private const byte TokenByte = 0xCD;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ReadPayload_Clears_The_Entire_Buffer_On_Eof()
    {
        using CapturingPayloadStream stream = new();
        FrameReader reader = new(stream);

        await Assert.ThrowsAsync<EndOfStreamException>(
            () => reader.ReadPayloadAsync(PayloadLength, ReadTimeout));

        AssertBufferWasFilledThenCleared(stream);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("cancel")]
    [InlineData("other")]
    public async Task ReadPayload_Clears_The_Entire_Buffer_And_Rethrows_The_Same_Exception(string failureKind)
    {
        Exception failure = failureKind switch
        {
            "io" => new IOException("测试读取失败"),
            "cancel" => new OperationCanceledException("测试读取取消"),
            "other" => new InvalidOperationException("测试其它读取异常"),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind)),
        };
        using CapturingPayloadStream stream = new(failure: failure);
        FrameReader reader = new(stream);

        Exception? caught = await Record.ExceptionAsync(
            () => reader.ReadPayloadAsync(PayloadLength, ReadTimeout));

        Assert.Same(failure, caught);
        AssertBufferWasFilledThenCleared(stream);
    }

    [Fact]
    public async Task ReadPayload_Returns_The_Original_Buffer_Without_Clearing_On_Success()
    {
        using CapturingPayloadStream stream = new(completePayload: true);
        FrameReader reader = new(stream);

        byte[] payload = await reader.ReadPayloadAsync(PayloadLength, ReadTimeout);

        Assert.Equal(2, stream.ReadAsyncCalls);
        Assert.Equal(Enumerable.Repeat(TokenByte, PayloadLength).ToArray(), payload);
        Assert.True(stream.CapturedBuffer.Equals(payload.AsMemory()));
        Assert.Equal(stream.BufferBeforeCompletion, stream.CapturedBuffer.ToArray());
    }

    private static void AssertBufferWasFilledThenCleared(CapturingPayloadStream stream)
    {
        Assert.Equal(2, stream.ReadAsyncCalls);
        Assert.Equal(PayloadLength, stream.CapturedBuffer.Length);
        Assert.Equal(Enumerable.Repeat(TokenByte, PayloadLength).ToArray(), stream.BufferBeforeCompletion);
        Assert.All(stream.CapturedBuffer.ToArray(), value => Assert.Equal((byte)0, value));
    }

    private sealed class CapturingPayloadStream : Stream
    {
        private readonly bool _completePayload;
        private readonly Exception? _failure;

        public CapturingPayloadStream(bool completePayload = false, Exception? failure = null)
        {
            _completePayload = completePayload;
            _failure = failure;
        }

        public Memory<byte> CapturedBuffer { get; private set; }
        public byte[] BufferBeforeCompletion { get; private set; } = [];
        public int ReadAsyncCalls { get; private set; }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadAsyncCalls++;
            if (ReadAsyncCalls == 1)
            {
                CapturedBuffer = buffer;
                buffer.Span[..(PayloadLength / 2)].Fill(TokenByte);
                return ValueTask.FromResult(PayloadLength / 2);
            }

            // 第二次读取先写入剩余区域，再失败；尚未计入已读长度的字节也必须清理。
            buffer.Span.Fill(TokenByte);
            BufferBeforeCompletion = CapturedBuffer.ToArray();
            if (_failure is not null)
            {
                return ValueTask.FromException<int>(_failure);
            }

            return ValueTask.FromResult(_completePayload ? buffer.Length : 0);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
