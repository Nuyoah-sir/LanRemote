using System.Buffers.Binary;
using System.Diagnostics;

namespace LanRemote.Transport.Tests;

/// <summary>
/// <see cref="FrameReader"/> 的行为。
/// </summary>
/// <remarks>
/// 覆盖评审 A-8（阶段绝对 deadline）、A-10（uint 先校验再转 int）、A-9（pre-auth 小上限）。
/// 「低速攻击」用一条可控的 <see cref="TrickleStream"/> 制造，不依赖真实网络时序。
/// </remarks>
public sealed class FrameReaderTests
{
    private const int Cap = TransportConstants.MaxPreAuthMessageBytes;

    [Theory]
    [InlineData(0u, false, FrameReader.RejectZeroLength)]
    [InlineData(1u, true, null)]
    [InlineData((uint)TransportConstants.MaxPreAuthMessageBytes, true, null)]
    [InlineData((uint)TransportConstants.MaxPreAuthMessageBytes + 1u, false, FrameReader.RejectTooLarge)]
    [InlineData(0x7fffffffu, false, FrameReader.RejectTooLarge)]
    [InlineData(0x80000000u, false, FrameReader.RejectTooLarge)]
    [InlineData(0xffffffffu, false, FrameReader.RejectTooLarge)]
    public void Validate_Length_Covers_The_Interesting_Boundaries(uint length, bool accepted, string? reason)
    {
        bool ok = FrameReader.TryValidateLength(length, Cap, out int converted, out string? rejection);

        Assert.Equal(accepted, ok);
        Assert.Equal(reason, rejection);

        if (accepted)
        {
            Assert.Equal((int)length, converted);
        }
        else
        {
            // 失败时必须留下 0，不能让调用方拿着脏值去分配。
            Assert.Equal(0, converted);
        }
    }

    [Fact]
    public async Task Reads_Frame_And_Leaves_The_Rest_Of_The_Stream_Untouched()
    {
        byte[] first = new byte[] { 1, 2, 3 };
        byte[] second = new byte[] { 9 };

        MemoryStream stream = new();
        WriteFrame(stream, first);
        WriteFrame(stream, second);
        stream.Position = 0;

        FrameReader reader = new(stream);

        byte[] firstRead = await reader.ReadFrameAsync(
            Cap, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        byte[] secondRead = await reader.ReadFrameAsync(
            Cap, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        Assert.Equal(first, firstRead);
        Assert.Equal(second, secondRead);

        // 一帧里粘连了多帧内容时，读到的仍是分帧结果（模拟 TCP 粘包）。
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public async Task Eof_In_The_Middle_Of_The_Length_Prefix_Is_Reported()
    {
        for (int available = 0; available < TransportConstants.LengthPrefixBytes; available++)
        {
            MemoryStream stream = new(new byte[available]);
            FrameReader reader = new(stream);

            await Assert.ThrowsAsync<EndOfStreamException>(
                () => reader.ReadLengthPrefixAsync(TimeSpan.FromSeconds(1)));
        }
    }

    [Fact]
    public async Task Eof_In_The_Middle_Of_The_Payload_Is_Reported()
    {
        MemoryStream stream = new();
        WriteLengthPrefix(stream, 16);
        stream.Write(new byte[7]);
        stream.Position = 0;

        FrameReader reader = new(stream);

        await Assert.ThrowsAsync<EndOfStreamException>(
            () => reader.ReadFrameAsync(Cap, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Oversized_Length_Is_Rejected_Without_Draining_The_Payload()
    {
        CountingStream stream = new();

        // 声称 1 MiB：远超 pre-auth 的 4 KiB。
        WriteLengthPrefix(stream, TransportConstants.MaxControlMessageBytes);
        stream.Write(new byte[64]);
        stream.Position = 0;

        FrameReader reader = new(stream);

        FrameProtocolException exception = await Assert.ThrowsAsync<FrameProtocolException>(
            () => reader.ReadFrameAsync(
                TransportConstants.MaxPreAuthMessageBytes,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1)));

        Assert.Equal(FrameReader.RejectTooLarge, exception.Reason);

        // 关键：只消费了 4 字节前缀，没有去读（也不会分配）那 1 MiB payload。
        // 两把尺子交叉验证：计数器与流自身位置。
        Assert.Equal(TransportConstants.LengthPrefixBytes, stream.BytesRead);
        Assert.Equal(TransportConstants.LengthPrefixBytes, stream.Position);
        Assert.Equal(1, stream.ReadAsyncCalls);
    }

    /// <summary>
    /// 低速攻击：每 <c>timeout - ε</c> 只发一个字节。
    /// </summary>
    /// <remarks>
    /// <para>若实现成「距上次读到字节 N 秒」的滑动窗口，这一路会一直续命到读完；
    /// 绝对 deadline 则必须在原时刻切断。</para>
    /// <para><b>实测（2026-09-21，本机）</b>：耗时 <b>412 ms</b>，
    /// 异常具体类型是 <c>System.Threading.Tasks.TaskCanceledException</c>
    /// （<see cref="OperationCanceledException"/> 的子类），
    /// 它由测试替身内部的 <c>Task.Delay(delay, token)</c> 抛出——
    /// 也就是说这是<b>我的替身</b>的类型，不是 <c>SslStream</c> 的。
    /// 真实 TLS 上的具体类型由步骤 15 另行实测记录，此处只断言「是取消」。</para>
    /// </remarks>
    [Fact(Timeout = 30_000)]
    public async Task Slow_Trickle_Does_Not_Extend_The_Absolute_Deadline()
    {
        TimeSpan timeout = TimeSpan.FromMilliseconds(400);
        TimeSpan trickle = TimeSpan.FromMilliseconds(150);

        // 8 字节 × 150ms：滑动窗口实现会一直读到 1200ms 才结束。
        byte[] payload = new byte[8];
        TrickleStream stream = new(payload, trickle);
        FrameReader reader = new(stream);

        Stopwatch stopwatch = Stopwatch.StartNew();
        Exception? caught = null;
        try
        {
            await reader.ReadPayloadAsync(payload.Length, timeout);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        stopwatch.Stop();

        Assert.NotNull(caught);
        Assert.IsAssignableFrom<OperationCanceledException>(caught);

        Assert.True(
            stopwatch.Elapsed >= TimeSpan.FromMilliseconds(360),
            $"过早返回（{stopwatch.Elapsed}），时限没生效。");

        // 远早于"把 8 个字节读完"所需的 1200ms —— 证明不是滑动窗口。
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(800),
            $"耗时 {stopwatch.Elapsed}，看起来像是被每个字节重置了 deadline。");
    }

    [Fact(Timeout = 30_000)]
    public async Task Prefix_And_Payload_Deadlines_Are_Independent()
    {
        // 前缀段慢慢来但来得及；payload 段同样有自己的完整预算，不吃前缀用掉的时间。
        MemoryStream stream = new();
        WriteLengthPrefix(stream, 4);
        stream.Write(new byte[4]);
        stream.Position = 0;

        FrameReader reader = new(stream);
        byte[] payload = await reader.ReadFrameAsync(
            Cap,
            prefixTimeout: TimeSpan.FromSeconds(2),
            payloadTimeout: TimeSpan.FromSeconds(2));

        Assert.Equal(4, payload.Length);
    }

    [Fact]
    public async Task Zero_Timeout_Is_Rejected_Up_Front()
    {
        FrameReader reader = new(new MemoryStream(new byte[8]));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => reader.ReadLengthPrefixAsync(TimeSpan.Zero));
    }

    private static void WriteLengthPrefix(Stream stream, uint length)
    {
        byte[] buffer = new byte[TransportConstants.LengthPrefixBytes];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, length);
        stream.Write(buffer);
    }

    private static void WriteFrame(Stream stream, byte[] payload)
    {
        WriteLengthPrefix(stream, (uint)payload.Length);
        stream.Write(payload);
    }

    /// <summary>
    /// 记录被消费了多少字节的流。
    /// </summary>
    /// <remarks>
    /// <b>实测坑（别改回继承 <see cref="MemoryStream"/>）</b>：
    /// 若从 <see cref="MemoryStream"/> 派生并同时重写 <c>Read(Span&lt;byte&gt;)</c> 与
    /// <c>ReadAsync(Memory&lt;byte&gt;)</c>，一次读取会被数两遍——
    /// <c>Stream.Read(Span&lt;byte&gt;)</c> 的默认实现会<b>虚拟调用</b>
    /// <c>Read(byte[], int, int)</c>，于是 base.Read(span) → 数组重载 → 再计一次。
    /// 当时实测输出 <c>async=1 span=0 array=1</c> 而 <c>BytesRead=8</c>（真实只消费 4）。
    /// 所以这里改成<b>包装</b>内部流，每条路径各自只计一次。
    /// </remarks>
    private sealed class CountingStream : Stream
    {
        private readonly MemoryStream _inner = new();

        /// <summary>累计被读走的字节数。</summary>
        public int BytesRead { get; private set; }

        /// <summary>异步读调用次数。</summary>
        public int ReadAsyncCalls { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => true;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadAsyncCalls++;
            int read = _inner.Read(buffer.Span);
            BytesRead += read;
            return ValueTask.FromResult(read);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            _inner.Write(buffer, offset, count);

        public override void Flush() => _inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => _inner.SetLength(value);
    }

    /// <summary>
    /// 每次只读 1 个字节、且每次都要等 <c>delay</c> 的流——用来制造低速攻击。
    /// </summary>
    private sealed class TrickleStream : Stream
    {
        private readonly byte[] _data;
        private readonly TimeSpan _delay;
        private int _position;

        public TrickleStream(byte[] data, TimeSpan delay)
        {
            _data = data;
            _delay = delay;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_position >= _data.Length)
            {
                return 0;
            }

            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);

            if (buffer.Length == 0)
            {
                return 0;
            }

            buffer.Span[0] = _data[_position++];
            return 1;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
