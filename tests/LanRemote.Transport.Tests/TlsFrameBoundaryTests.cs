using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using LanRemote.Core.Abstractions;
using Xunit.Abstractions;

namespace LanRemote.Transport.Tests;

/// <summary>
/// <b>M3.1 B16</b>：真实 TLS 上的帧边界——粘包要被拆对，拆包要被拼对，逐字节还原。
/// </summary>
/// <remarks>
/// <para>与 <see cref="FrameReaderTests"/> 的 <c>MemoryStream</c> 版一一对应：
/// 那边证明读取逻辑，这边证明同一逻辑在真实 <c>SslStream</c> 的传输形态下依然成立。
/// TLS 记录层不保留写边界——两帧合并进同一条记录、或一帧横跨多条记录，
/// 由记录层决定，不由我们的 <c>WriteAsync</c> 调用形状决定。</para>
/// <para><b>刻意不经过 <see cref="ControlPreAuthSession"/></b>：会话层只消费一帧
/// （M3 终态 = 干净关闭），「第二帧字节级完好」必须在本文件的
/// <see cref="FrameReader"/> 层证明。</para>
/// <para>判定口径两条：① 每帧与发送端<b>逐字节相等</b>；② 声明帧数读完后<b>没有残余字节</b>——
/// 残余探针在真实网络上的表现是「下一次读是 EOF 或连接被拆」，对应 <c>MemoryStream</c> 版的
/// <c>Length == Position</c>。探针另有一条<b>反向用例</b>
/// （<see cref="Garbage_After_The_Declared_Frames_Is_Detected_Not_Ignored"/>）：故意多写 4 字节，
/// 探针必须报残余——否则「探针从不报残余」就成了空断言。</para>
/// <para>诊断量（见 <see cref="ObservingStream"/>）：读调用次数与<b>半读</b>次数。
/// 半读计数让拆分用例在运行期自证前提成立，而不是跑一个可能被本机合并掉、
/// 什么都没测到的空用例。</para>
/// <para><b>变异验证（2026-09-21，本机，确认真变红后恢复实现）</b>：</para>
/// <list type="bullet">
/// <item><description><c>ReadExactlyAsync</c> 每次读完再多读 1 字节 → 5/5 全红：
/// 粘包用例报出字节整体右移一位（<c>Expected: [23, 30, 37, 44, 51]</c> /
/// <c>Actual: [30, 37, 44, 51, 0]</c>）——边界平移被逐字节比较抓住；</description></item>
/// <item><description><c>ReadExactlyAsync</c> 改成单次读、不补满 → 拆包与大帧两条变红
/// （<c>Actual: [···, 46, 53, 0, 0, 0, ···]</c>，短读被当成完整读、尾部补 0）；
/// 恢复实现后 5/5 全绿。</description></item>
/// </list>
/// </remarks>
public sealed class TlsFrameBoundaryTests(ITestOutputHelper output)
{
    private static readonly Guid DeviceId = Guid.Parse("99999999-8888-7777-6666-555555555555");

    /// <summary>
    /// 粘包的真实形态：两帧在<b>同一次</b> <c>WriteAsync</c> 里到达。
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Two_Glued_Frames_In_One_Write_Are_Decoded_Byte_Exact()
    {
        byte[] first = BuildPattern(seed: 1, length: 3);
        byte[] second = BuildPattern(seed: 2, length: 1);
        byte[] glued = Glue(BuildFrame(first), BuildFrame(second));

        ScenarioOutcome outcome = await RunScenarioAsync(
            frameCount: 2,
            client: async connection =>
            {
                await connection.Stream.WriteAsync(glued);
                await connection.Stream.FlushAsync();
            });

        AssertFrames(outcome, first, second);
    }

    /// <summary>
    /// 长度悬殊的粘连帧（1 / 7 / 512 / 1000 字节）：每帧的边界都落在不同偏移上。
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Glued_Frames_Of_Very_Different_Sizes_Are_All_Recovered()
    {
        byte[][] payloads =
        {
            BuildPattern(seed: 1, length: 1),
            BuildPattern(seed: 2, length: 7),
            BuildPattern(seed: 3, length: 512),
            BuildPattern(seed: 4, length: 1000),
        };

        byte[] glued = Glue(payloads.Select(BuildFrame).ToArray());

        ScenarioOutcome outcome = await RunScenarioAsync(
            frameCount: payloads.Length,
            client: async connection =>
            {
                await connection.Stream.WriteAsync(glued);
                await connection.Stream.FlushAsync();
            });

        AssertFrames(outcome, payloads);
    }

    /// <summary>
    /// 大帧在真实记录拆分下依然逐字节还原。
    /// </summary>
    /// <remarks>
    /// <para>两帧合计 18 440 字节，超过单条 TLS 记录 16 KiB 上限——
    /// 记录层必然把这次写拆成多条记录。读侧是否出现<b>短读</b>取决于到达时序
    /// （本机两轮实测：一轮 5 次读全部读满、半读 0 次；另一轮出现短读被补读循环救回），
    /// 所以判定只看「逐字节还原」。补读循环的真伪由变异验证锚住——
    /// 去掉循环（单次读）时本用例立刻变红。</para>
    /// <para>上限用 <c>MaxControlMessageBytes</c>：这里测的是传输形态，不是 pre-auth 的
    /// 4 KiB 闸门（那条在会话层与 B19 边界矩阵里）。</para>
    /// </remarks>
    [Fact(Timeout = 60_000)]
    public async Task Large_Frames_Crossing_The_Tls_Record_Limit_Are_Reassembled()
    {
        byte[] first = BuildPattern(seed: 11, length: 9216);
        byte[] second = BuildPattern(seed: 12, length: 9216);
        byte[] glued = Glue(BuildFrame(first), BuildFrame(second));

        ScenarioOutcome outcome = await RunScenarioAsync(
            frameCount: 2,
            maxBytes: TransportConstants.MaxControlMessageBytes,
            client: async connection =>
            {
                await connection.Stream.WriteAsync(glued);
                await connection.Stream.FlushAsync();
            });

        AssertFrames(outcome, first, second);
    }

    /// <summary>
    /// 拆包：一帧被拆成 1 + 4 + 63 字节、分三段、隔 250 + 30 ms 才送完——仍须逐字节还原。
    /// </summary>
    /// <remarks>
    /// 首个字节落地后刻意停 250 ms：服务端的第一个 <c>ReadAsync</c> 只可能拿到 1 个字节，
    /// <b>半读是可观测的</b>。用例末尾因此硬断言「本轮确实观测到了半读」——
    /// 若哪天本机把三段合并了（观测不到半读），它会以明确文案失败、要求加大首个空档，
    /// 而不是悄悄退化成一条什么都没测到的空用例。
    /// </remarks>
    [Fact(Timeout = 60_000)]
    public async Task A_Frame_Delivered_In_Weird_Chunks_Is_Reassembled_Byte_Exact()
    {
        byte[] payload = BuildPattern(seed: 7, length: 64);
        byte[] frame = BuildFrame(payload);

        ScenarioOutcome outcome = await RunScenarioAsync(
            frameCount: 1,
            client: async connection =>
            {
                await WriteChunkAsync(connection, frame, offset: 0, length: 1);
                await Task.Delay(TimeSpan.FromMilliseconds(250));

                await WriteChunkAsync(connection, frame, offset: 1, length: 4);
                await Task.Delay(TimeSpan.FromMilliseconds(30));

                await WriteChunkAsync(connection, frame, offset: 5, length: frame.Length - 5);
            });

        // 期望值是 payload：ReadFrameAsync 把长度前缀剥掉，只把载荷交给上层。
        AssertFrames(outcome, payload);

        Assert.True(
            outcome.PartialReads >= 1,
            $"本轮没有观测到任何半读（ReadAsync 共 {outcome.ReadCalls} 次）——" +
            "用例前提「拆分确实发生」没有成立；把首个空档（250 ms）加大后重跑。");
    }

    /// <summary>
    /// 反向用例：帧读完后还多出 4 个字节——残余探针必须<b>报残余</b>，不许当没看见。
    /// </summary>
    /// <remarks>
    /// 没有这一条，「无残余」断言无法排除「探针从不报残余」的退化实现。
    /// </remarks>
    [Fact(Timeout = 60_000)]
    public async Task Garbage_After_The_Declared_Frames_Is_Detected_Not_Ignored()
    {
        byte[] payload = BuildPattern(seed: 9, length: 5);
        byte[] frame = BuildFrame(payload);
        byte[] junk = new byte[] { 0x00, 0x00, 0x00, 0x07 };

        ScenarioOutcome outcome = await RunScenarioAsync(
            frameCount: 1,
            client: async connection =>
            {
                await connection.Stream.WriteAsync(Glue(frame, junk));
                await connection.Stream.FlushAsync();
            });

        Assert.True(outcome.Error is null, $"服务端读取路径抛了异常：{outcome.Error}");

        Assert.Single(outcome.Frames);
        Assert.Equal(payload, outcome.Frames[0]);

        Assert.False(outcome.EofObserved, "帧后还有 4 个字节，探针不该报「已到 EOF」。");
        Assert.NotNull(outcome.LeftoverPrefix);
        Assert.Equal(7u, outcome.LeftoverPrefix.Value);
    }

    /// <summary>
    /// 起一个真实 Host、客户端按脚本送字节；服务端按 <paramref name="frameCount"/> 读帧，
    /// 再执行「帧后无残余」探针，把两次结果一起带回来。
    /// </summary>
    /// <param name="frameCount">服务端要读的帧数。</param>
    /// <param name="client">客户端脚本；本方法保证它在服务端已进入读循环之后才执行。</param>
    /// <param name="maxBytes">读帧上限；默认 pre-auth 的 4 KiB。</param>
    private static async Task<ScenarioOutcome> RunScenarioAsync(
        int frameCount,
        Func<TlsConnection, Task> client,
        int maxBytes = TransportConstants.MaxPreAuthMessageBytes)
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        int port = GetFreePort();

        TimeSpan prefixDeadline = TimeSpan.FromSeconds(5);
        TimeSpan payloadDeadline = TimeSpan.FromSeconds(5);

        TaskCompletionSource readingStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ScenarioOutcome> outcome =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        TransportTimeouts timeouts = new(
            connectTimeout: TimeSpan.FromSeconds(3),
            handshakeTimeout: TimeSpan.FromSeconds(5),
            lengthPrefixTimeout: prefixDeadline,
            payloadTimeout: payloadDeadline,
            helloTimeout: TimeSpan.FromSeconds(2),
            // 信封只在 ControlPreAuthSession 里被消费；本文件不经它，给个常规值即可。
            preAuthEnvelopeTimeout: TimeSpan.FromSeconds(8));

        TransportHost host = new(
            new[] { IPAddress.Loopback },
            new StubSubnetPolicy(allow: true),
            certificate,
            async (connection, cancellationToken) =>
            {
                List<byte[]> frames = new();
                ObservingStream observing = new(connection.Stream);

                try
                {
                    FrameReader reader = new(observing);

                    // 就位信号：客户端等到它之后再开写，「半读可观测」就不依赖调度运气。
                    readingStarted.TrySetResult();

                    for (int i = 0; i < frameCount; i++)
                    {
                        frames.Add(await reader.ReadFrameAsync(
                            maxBytes, prefixDeadline, payloadDeadline, cancellationToken));
                    }

                    (bool eofObserved, uint? leftover, string? eofType) =
                        await ProbeForLeftoverAsync(reader, cancellationToken);

                    outcome.TrySetResult(new ScenarioOutcome(
                        frames,
                        observing.TotalBytesRead,
                        observing.ReadCalls,
                        observing.PartialReads,
                        eofObserved,
                        leftover,
                        eofType,
                        Error: null));
                }
                catch (Exception ex)
                {
                    // 处理器里飞出来的异常也必须变成可断言的结局，而不是让测试挂到超时。
                    outcome.TrySetResult(new ScenarioOutcome(
                        frames,
                        observing.TotalBytesRead,
                        observing.ReadCalls,
                        observing.PartialReads,
                        EofObserved: false,
                        LeftoverPrefix: null,
                        EofErrorType: ex.GetType().FullName,
                        Error: ex));
                }
            },
            new TransportHostOptions { Port = port, Timeouts = timeouts });

        await using (host)
        {
            TransportHostStartResult start = host.Start();
            Assert.True(start.IsListening, "测试前置条件：Host 应处于监听。");

            ConnectionTarget target = CreateTarget(
                port, TestCertificateFactory.Fingerprint(certificate));

            using TlsConnection connection = await new TlsClientConnector().ConnectAsync(target);

            Task startedTask = readingStarted.Task;
            Task started = await Task.WhenAny(startedTask, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(startedTask, started);
            await startedTask;

            await client(connection);

            // 客户端收摊（关闭 TLS + FIN）：服务端的残余探针以此为「数据到此为止」的信号。
            connection.Dispose();

            Task<ScenarioOutcome> outcomeTask = outcome.Task;
            Task finished = await Task.WhenAny(outcomeTask, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.Same(outcomeTask, finished);

            ScenarioOutcome result = await outcomeTask;

            // 名额与登记表必须回到 0，否则就是连接泄漏。
            Assert.True(
                await WaitUntilAsync(() => host.ActiveConnections == 0),
                "连接收尾后登记表应清空。");
            Assert.True(
                await WaitUntilAsync(() => host.AdmittedConnections == 0),
                "连接收尾后准入名额应归还。");

            return result;
        }
    }

    /// <summary>
    /// 「帧后无残余」探针：再读一个长度前缀。
    /// </summary>
    /// <returns>
    /// EOF / 连接被拆 = 没有残余（正常）；读出一个前缀 = 有残余（把值带回来，测试据此失败）；
    /// 其它异常（例如一直挂到取消）= 未收尾，同样带回来。
    /// </returns>
    private static async Task<(bool EofObserved, uint? LeftoverPrefix, string? ErrorType)>
        ProbeForLeftoverAsync(FrameReader reader, CancellationToken cancellationToken)
    {
        try
        {
            uint leftover = await reader.ReadLengthPrefixAsync(
                TimeSpan.FromSeconds(5), cancellationToken);

            return (false, leftover, null);
        }
        catch (EndOfStreamException ex)
        {
            // 干净 EOF。
            return (true, null, ex.GetType().Name);
        }
        catch (IOException ex)
        {
            // 对端被拆掉（没有走 close_notify）——同样意味着没有更多帧字节。
            // 注意顺序：EndOfStreamException 派生自 IOException，必须先接住前者。
            return (true, null, ex.GetType().Name);
        }
        catch (Exception ex)
        {
            return (false, null, ex.GetType().FullName);
        }
    }

    private void AssertFrames(ScenarioOutcome outcome, params byte[][] expected)
    {
        string probe = outcome.EofObserved
            ? $"EOF（{outcome.EofErrorType}）"
            : outcome.LeftoverPrefix is { } leftover
                ? $"残余前缀 {leftover}"
                : $"未收尾（{outcome.EofErrorType}）";

        output.WriteLine(
            $"[M3.1 B16] 读回 {outcome.Frames.Count} 帧 / {outcome.TotalBytesRead} 字节；" +
            $"ReadAsync {outcome.ReadCalls} 次（半读 {outcome.PartialReads} 次）；帧后探针：{probe}。");

        Assert.True(outcome.Error is null, $"服务端读取路径抛了异常：{outcome.Error}");

        Assert.Equal(expected.Length, outcome.Frames.Count);

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], outcome.Frames[i]);
        }

        Assert.True(
            outcome.EofObserved,
            $"声明的 {expected.Length} 帧读完后流里还有东西或没有收尾——" +
            $"残余前缀 {outcome.LeftoverPrefix?.ToString() ?? "无"} / {outcome.EofErrorType}。");
    }

    /// <summary>造一段可复现的模式字节；不同 seed 产出不同内容，帧被错位/互换时立刻能看出来。</summary>
    private static byte[] BuildPattern(int seed, int length)
    {
        byte[] payload = new byte[length];
        for (int i = 0; i < length; i++)
        {
            payload[i] = (byte)((seed * 31 + i * 7) & 0xFF);
        }

        return payload;
    }

    /// <summary>给 payload 加上 4 字节大端长度前缀。</summary>
    private static byte[] BuildFrame(byte[] payload)
    {
        byte[] frame = new byte[TransportConstants.LengthPrefixBytes + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(
            frame.AsSpan(0, TransportConstants.LengthPrefixBytes), (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(TransportConstants.LengthPrefixBytes));
        return frame;
    }

    /// <summary>把多段字节拼成一段——模拟「一次写入里粘连多帧」。</summary>
    private static byte[] Glue(params byte[][] parts)
    {
        byte[] glued = new byte[parts.Sum(part => part.Length)];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(glued.AsSpan(offset));
            offset += part.Length;
        }

        return glued;
    }

    private static async Task WriteChunkAsync(
        TlsConnection connection, byte[] frame, int offset, int length)
    {
        await connection.Stream.WriteAsync(frame.AsMemory(offset, length));
        await connection.Stream.FlushAsync();
    }

    private static ConnectionTarget CreateTarget(int port, string pinHex)
    {
        bool created = ConnectionTarget.TryCreate(
            DeviceId,
            IPAddress.Loopback,
            port,
            pinHex,
            out ConnectionTarget? target);

        Assert.True(created, "测试前置条件：目标快照应能创建。");
        return target!;
    }

    private static int GetFreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200; i++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }

    /// <summary>服务端一侧量到的全部事实（本文件的判定都从它派生）。</summary>
    private sealed record ScenarioOutcome(
        IReadOnlyList<byte[]> Frames,
        int TotalBytesRead,
        int ReadCalls,
        int PartialReads,
        bool EofObserved,
        uint? LeftoverPrefix,
        string? EofErrorType,
        Exception? Error);

    /// <summary>
    /// 包在 <c>SslStream</c> 外面计数的流：读调用次数、读到的总字节、<b>半读</b>次数。
    /// </summary>
    /// <remarks>
    /// <para><b>只计 <see cref="ReadAsync(Memory{byte}, CancellationToken)"/> 这一条路径</b>——
    /// 它是 <see cref="FrameReader"/> 唯一的读口。别学 <see cref="FrameReaderTests"/> 里
    /// 记过的那个坑：既重写 <c>Read(Span)</c> 又重写 <c>ReadAsync(Memory)</c> 会把一次读数两遍。</para>
    /// <para>不持有内部流的所有权（连接由 Host 释放），因此不实现 <c>Dispose</c> 转发。</para>
    /// </remarks>
    private sealed class ObservingStream(Stream inner) : Stream
    {
        /// <summary>累计读到的字节数（含 EOF 的 0 字节读）。</summary>
        public int TotalBytesRead { get; private set; }

        /// <summary>读调用次数。</summary>
        public int ReadCalls { get; private set; }

        /// <summary>「读到的比要的少」的次数——拆分/记录边界在本层的可观测形态。</summary>
        public int PartialReads { get; private set; }

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            ReadCalls++;
            TotalBytesRead += read;

            if (read > 0 && read < buffer.Length)
            {
                PartialReads++;
            }

            return read;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            inner.Write(buffer, offset, count);
    }

    private sealed class StubSubnetPolicy : ISubnetPolicy
    {
        private readonly bool _allow;

        public StubSubnetPolicy(bool allow)
        {
            _allow = allow;
        }

        public bool IsAllowedPeer(IPAddress localAddress, IPAddress remoteAddress) => _allow;
    }
}
