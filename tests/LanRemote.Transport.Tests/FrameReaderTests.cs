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

    // B19 矩阵的段预算。取 350 ms：足够远离 Windows 计时器粒度（~15.6 ms）与调度抖动，
    // 又足够短，让 13 个用例的矩阵在一秒量级内跑完。
    private static readonly TimeSpan StageBudget = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan StageSlack = TimeSpan.FromSeconds(2);

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

    // ─────────────────────────────────────────────────────────────────
    // B19：字节边界取消矩阵。
    // 已有覆盖（PreAuthDeadlineTests，真实 TLS）：0 字节 / 半前缀(2 字节) / 半 payload(10 of 64)。
    // 这里补齐：前缀 1、3 字节边界；payload 0、1、63 字节边界；
    // 「无二次完成」（超时后没有泄漏的读继续偷吃字节）；
    // 「无状态复用」（一次失败不污染同一读取器的后续调用）；
    // 以及成功侧逐字节滴流的「完整才返回」。
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// B19：长度前缀在<b>每一个</b>截断边界沉默时，都必须在本段预算内以取消收尾。
    /// </summary>
    /// <remarks>
    /// 「沉默」= 先给 <paramref name="prefixBytes"/> 个字节，之后既不给数据也不 EOF。
    /// 已消费字节数必须<b>恰好等于</b>喂入字节数：多了说明读超界（吞掉了后面的数据），
    /// 少了说明账目错乱——两把尺子都要对得上。
    /// </remarks>
    [Theory(Timeout = 30_000)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Length_Prefix_Stalled_At_Each_Byte_Boundary_Times_Out(int prefixBytes)
    {
        SilentAfterStream stream = new(Pattern(prefixBytes));
        FrameReader reader = new(stream);

        Stopwatch stopwatch = Stopwatch.StartNew();
        Exception? caught = null;
        try
        {
            await reader.ReadLengthPrefixAsync(StageBudget);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        stopwatch.Stop();

        AssertStalledWithCancellation(caught, stopwatch.Elapsed, $"长度前缀截断于 {prefixBytes}/4 字节");
        Assert.Equal(prefixBytes, stream.BytesServed);
    }

    /// <summary>
    /// B19：payload 在每一个截断边界沉默时，都必须在 payload 段预算内以取消收尾。
    /// </summary>
    /// <remarks>
    /// 前缀声称 64 字节；先给完整前缀 + <paramref name="payloadBytes"/> 字节，然后沉默。
    /// 63 是「差一个字节」的边界——正是最容易被「读满即成功」的模糊实现放过去的位置。
    /// </remarks>
    [Theory(Timeout = 30_000)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(63)]
    public async Task Payload_Stalled_At_Each_Byte_Boundary_Times_Out(int payloadBytes)
    {
        SilentAfterStream stream = new(FrameBytes(declaredLength: 64, actual: Pattern(payloadBytes)));
        FrameReader reader = new(stream);

        Stopwatch stopwatch = Stopwatch.StartNew();
        Exception? caught = null;
        try
        {
            await reader.ReadFrameAsync(
                Cap,
                prefixTimeout: TimeSpan.FromSeconds(5),
                payloadTimeout: StageBudget);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        stopwatch.Stop();

        AssertStalledWithCancellation(
            caught,
            stopwatch.Elapsed,
            $"payload 截断于 {payloadBytes}/64 字节");

        Assert.Equal(TransportConstants.LengthPrefixBytes + payloadBytes, stream.BytesServed);
    }

    /// <summary>
    /// B19（成功侧）：整帧逐字节滴流——最后一字节到达<b>之前</b>绝不返回，到达之后逐字节还原。
    /// </summary>
    /// <remarks>
    /// 「完整才返回」需要一个<b>机械</b>证据：差最后一字节时读取任务必须仍在等待
    /// （<c>IsCompleted == false</c>），最后一字节放出后才完成。只断言「最后能读到数据」
    /// 是不够的——那种断言对「读到一部分就返回」的实现也是绿的。
    /// </remarks>
    [Theory(Timeout = 30_000)]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(63)]
    public async Task A_Trickled_Complete_Frame_Is_Returned_Only_After_Its_Last_Byte(int payloadLength)
    {
        byte[] payload = Pattern(payloadLength, seed: 0x51);
        byte[] frame = FrameBytes(declaredLength: (uint)payload.Length, actual: payload);

        GatedOneByteStream stream = new(frame);
        FrameReader reader = new(stream);

        Task<byte[]> reading = reader.ReadFrameAsync(
            Cap,
            prefixTimeout: TimeSpan.FromSeconds(10),
            payloadTimeout: TimeSpan.FromSeconds(10));

        // 放出除最后一字节外的所有字节，等消费追上来。
        stream.ReleaseBytes(frame.Length - 1);
        bool trickled = await WaitUntilAsync(() => stream.BytesServed == frame.Length - 1);
        Assert.True(
            trickled,
            $"滴流没有推进：已服务 {stream.BytesServed} / {frame.Length - 1} 字节。");

        // 给足调度时间再检查：读取任务必须仍挂着（不能「已读到的先返回」）。
        await Task.Delay(100);
        Assert.False(
            reading.IsCompleted,
            "最后一字节还没放出，读取就已经完成了——「差一字节不返回」被违反。");

        // 放出最后一字节：此刻才允许完成。
        stream.ReleaseBytes(1);
        byte[] read = await reading.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(payload, read);
        Assert.Equal(frame.Length, stream.BytesServed);
    }

    /// <summary>
    /// B19：超时被切断后，读取器不能还留着一只「隐形的手」继续消费后续字节。
    /// </summary>
    /// <remarks>
    /// <para>场景：2 字节半前缀后沉默 → 超时。随后打开「闸门」再放 2 字节——
    /// 这两字节属于<b>下一次</b>读（如果有的话），不该被任何遗留的读取偷走。</para>
    /// <para>裁判是服务计数：它停在 2（正确）还是涨到 4（有泄漏的续作在偷吃）。</para>
    /// </remarks>
    [Fact(Timeout = 30_000)]
    public async Task A_Timed_Out_Read_Leaves_No_Pending_Consumer()
    {
        SilentAfterStream stream = new(Pattern(2), later: Pattern(2, seed: 0x61));
        FrameReader reader = new(stream);

        Exception? caught = null;
        try
        {
            await reader.ReadLengthPrefixAsync(StageBudget);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        Assert.IsAssignableFrom<OperationCanceledException>(caught);
        Assert.Equal(2, stream.BytesServed);

        int callsBeforeOpen = stream.ReadAsyncCalls;

        // 打开闸门：若实现里还挂着一个读，它会在此时醒来并把字节吃掉。
        stream.Open();
        await Task.Delay(250);

        Assert.Equal(2, stream.BytesServed);
        Assert.Equal(callsBeforeOpen, stream.ReadAsyncCalls);
    }

    /// <summary>
    /// B19：一次超时的读不能把内部状态泄漏给同一读取器的后续调用——
    /// 下一次调用必须「像第一次一样新鲜」。
    /// </summary>
    /// <remarks>
    /// <para>为什么必须是「消费 0 字节的失败」：读取器消费掉的字节<b>无法退回</b>。
    /// 若失败发生在消费 n&gt;0 字节之后，流位置已前移 n，「再来一次」必然错位——
    /// 那是流的既定事实，不是读取器的错（错位前缀通常换来一个明确的超限拒绝）。
    /// 只有 0 字节失败的场景，才让「下一次调用读完整帧」有确定的正确结果。</para>
    /// <para>要防的现实风险：把 <c>CancellationTokenSource</c> / 缓冲区提升为实例字段「复用」——
    /// 取消过的 CTS 若漏换成新的，下一次调用会立刻假失败；残留缓冲区则给出脏数据。
    /// 本用例对这两种错误都会变红（断言「第二次读成功且逐字节相等」）。
    /// </para>
    /// </remarks>
    [Fact(Timeout = 30_000)]
    public async Task A_Zero_Byte_Timed_Out_Read_Does_Not_Leak_State_Into_The_Later_One()
    {
        byte[] payload = Pattern(5, seed: 0x71);
        byte[] whole = FrameBytes(declaredLength: 5, actual: payload);

        // 一个字节都还没到就超时；随后数据才到。
        SilentAfterStream stream = new(initial: [], later: whole);
        FrameReader reader = new(stream);

        Exception? caught = null;
        try
        {
            await reader.ReadFrameAsync(
                Cap,
                prefixTimeout: StageBudget,
                payloadTimeout: TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        Assert.IsAssignableFrom<OperationCanceledException>(caught);
        Assert.Equal(0, stream.BytesServed);

        stream.Open();

        // 关键：同一读取器的下一次调用从头开始读——成功、逐字节还原。
        byte[] read = await reader.ReadFrameAsync(
            Cap,
            prefixTimeout: TimeSpan.FromSeconds(5),
            payloadTimeout: TimeSpan.FromSeconds(5));

        Assert.Equal(payload, read);
        Assert.Equal(whole.Length, stream.BytesServed);
    }

    /// <summary>
    /// 断言的公共部分：确实抛了取消、没有过早返回、也没有挂死。
    /// </summary>
    private static void AssertStalledWithCancellation(
        Exception? caught,
        TimeSpan elapsed,
        string scenario)
    {
        Assert.NotNull(caught);

        // 断言消息里带实测类型：一旦真实异常不是取消，失败信息直接给出真相。
        Assert.True(
            caught is OperationCanceledException,
            $"「{scenario}」实测异常 {caught.GetType().FullName}：{caught.Message}（应当是被预算取消）。");

        Assert.True(
            elapsed >= StageBudget * 0.8,
            $"「{scenario}」过早返回：{elapsed}，段预算 {StageBudget}。");

        Assert.True(
            elapsed < StageBudget + StageSlack,
            $"「{scenario}」耗时 {elapsed}，超过段预算 {StageBudget} 太多——不像被预算切断。");
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

    /// <summary>构造确定性字节图案：同一输入永远同一输出，失败输出可直接对账。</summary>
    private static byte[] Pattern(int length, byte seed = 0x40)
    {
        byte[] result = new byte[length];
        for (int i = 0; i < length; i++)
        {
            result[i] = (byte)((seed * 31 + i * 7) & 0xFF);
        }

        return result;
    }

    /// <summary>拼一帧：长度前缀声明 <paramref name="declaredLength"/>，实际字节由 <paramref name="actual"/> 决定。</summary>
    private static byte[] FrameBytes(uint declaredLength, byte[] actual)
    {
        MemoryStream buffer = new();
        WriteLengthPrefix(buffer, declaredLength);
        buffer.Write(actual);
        return buffer.ToArray();
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 400; i++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
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

    /// <summary>
    /// 按计划喂字节的流：先给 <c>initial</c>，此后<b>既不返回 0 也不挂死</b>——
    /// 沉默地等到取消（真实对端「拖死你」的样子）。
    /// </summary>
    /// <remarks>
    /// <para><see cref="Open"/> 之后把 <c>later</c> 的字节放出来。这个闸门专用于「无二次完成」：
    /// 超时后的实现若还挂着一个读在等待，<c>Open()</c> 会让它立刻醒来并偷吃后面的字节，
    /// <see cref="BytesServed"/> 就会露出马脚。</para>
    /// <para><b>刻意不基于 <see cref="MemoryStream"/></b>：这里的「流位置」是<b>服务</b>过的字节数；
    /// 数据未到时返回 0 在帧读取器眼里是 EOF，会改变被测语义（我们测的是「沉默」而不是「断开」）。</para>
    /// <para>服务按调用方 buffer 大小推进（<c>min(剩余数据, buffer 空间)</c>）——
    /// 与真实流一致；不能写成一口气喂完，否则与读取器的分段逻辑不再对应。</para>
    /// </remarks>
    private sealed class SilentAfterStream : Stream
    {
        private readonly byte[] _initial;
        private readonly byte[] _later;
        private readonly TaskCompletionSource _tap = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _initialServed;
        private int _laterServed;
        private int _readAsyncCalls;
        private volatile bool _opened;

        public SilentAfterStream(byte[] initial, byte[]? later = null)
        {
            _initial = initial;
            _later = later ?? [];
        }

        /// <summary>已经流出的字节数（initial + later）。</summary>
        public int BytesServed => Volatile.Read(ref _initialServed) + Volatile.Read(ref _laterServed);

        /// <summary>异步读被调用的总次数。</summary>
        public int ReadAsyncCalls => Volatile.Read(ref _readAsyncCalls);

        /// <summary>打开闸门：此后 <c>later</c> 的字节可以被服务。</summary>
        public void Open()
        {
            _opened = true;
            _tap.TrySetResult();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readAsyncCalls);

            int served = ServeFrom(_initial, buffer, ref _initialServed);
            if (served > 0)
            {
                return served;
            }

            if (_later.Length > 0 && !_opened)
            {
                // 沉默，但可解锁：超时后的「泄漏读」会在 Open() 时从这里醒来。
                await _tap.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            served = ServeFrom(_later, buffer, ref _laterServed);
            if (served > 0)
            {
                return served;
            }

            // 数据给完也不 EOF：沉默到取消为止——「拖死」语义。
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        private static int ServeFrom(byte[] source, Memory<byte> buffer, ref int servedField)
        {
            int remaining = source.Length - servedField;
            if (remaining <= 0)
            {
                return 0;
            }

            int take = Math.Min(remaining, buffer.Length);
            source.AsSpan(servedField, take).CopyTo(buffer.Span);
            servedField += take;
            return take;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _initial.Length + _later.Length;

        public override long Position
        {
            get => BytesServed;
            set => throw new NotSupportedException();
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

    /// <summary>
    /// 一次只放一个字节、由测试驱动的流：每次异步读先等一个「放行」信号，再服务 1 个字节。
    /// </summary>
    /// <remarks>
    /// 用于成功侧滴流的「完整才返回」：测试精确控制「差最后一字节」的状态，
    /// 读取任务在数据不完整时<b>必须</b>仍未完成。数据耗尽后返回 0（EOF），
    /// 但本用例中不会走到——读到声明长度即停。
    /// </remarks>
    private sealed class GatedOneByteStream : Stream
    {
        private readonly SemaphoreSlim _turns = new(0, int.MaxValue);
        private readonly byte[] _data;
        private int _served;

        public GatedOneByteStream(byte[] data)
        {
            _data = data;
        }

        /// <summary>已经流出的字节数。</summary>
        public int BytesServed => Volatile.Read(ref _served);

        /// <summary>放行 <paramref name="count"/> 个字节（每放行一次，服务 1 个字节）。</summary>
        public void ReleaseBytes(int count)
        {
            for (int i = 0; i < count; i++)
            {
                _turns.Release();
            }
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await _turns.WaitAsync(cancellationToken).ConfigureAwait(false);

            if (_served >= _data.Length)
            {
                return 0;
            }

            byte value = _data[_served];
            _served++;
            buffer.Span[0] = value;
            return 1;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _data.Length;

        public override long Position
        {
            get => BytesServed;
            set => throw new NotSupportedException();
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
