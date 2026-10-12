using System.Buffers.Binary;
using System.Security.Cryptography;

namespace LanRemote.Transport;

/// <summary>M6 独立单帧接续：首字节已经被同一流的唯一读者持有；尚未接 live Control。</summary>
/// <remarks>
/// 不验证首字节来源或此前读者退出，这些仍是 owner 的前置义务。没有 deadline、timer、取消源、
/// JSON/方向/权限校验或队列，不可直接用于真实连接。只接续余三字节前缀和一个受限 payload。
/// 借用流且不关流；Stop 只失效并清理已静止的未交付数组，不等 I/O，不调用流或外部回调。
/// owner 必须锁外关流并 await Completion；已取得的每个原 Task 必须退出，不能取消等待假称排空。
/// Completion 表示 worker/已取得原 Task 结束，不表示读取成功，必须检查 Snapshot.Error。
/// Snapshot 终态可先于 Completion 发布，不能当作 join；成功未 Take 的数组仍归组件，须 Take 或 Stop。
/// Stop 准入检查与锁外 Read 不原子；已经越过检查的读取可以在 Stop 返回后才进入流同步前缀。
/// ReadAsync/AsTask 抛错且没有取得 Task 时无法证明隐藏 I/O 退出，整块当前借用数组不清写。
/// TryTake 仅一次转移可变 byte[] 所有权（不是不可变 DTO）；成功后清理义务归调用者。
/// </remarks>
internal sealed class ControlFrameContinuation
{
    internal enum ReadEnd { Pending, Stopped, Frame, Faulted }
    internal sealed record ReadSnapshot(
        ReadEnd End, bool StopRequested, bool Taken, bool BorrowExitUnproven, Exception? Error);

    private readonly object _gate = new();
    private readonly Stream _stream;
    private readonly byte _firstByte;
    private readonly int _maxBytes;
    private Task? _completion;
    private bool _stopRequested;
    private bool _taken;
    private bool _borrowExitUnproven;
    private ReadEnd _end;
    private Exception? _error;
    private byte[]? _payload;

    internal ControlFrameContinuation(Stream stream, byte firstByte, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBytes, TransportConstants.MaxControlMessageBytes);
        _stream = stream;
        _firstByte = firstByte;
        _maxBytes = maxBytes;
    }

    internal Task? Completion { get { lock (_gate) return _completion; } }
    internal ReadSnapshot Snapshot
    {
        get { lock (_gate) return new(_end, _stopRequested, _taken, _borrowExitUnproven, _error); }
    }

    internal Task Start()
    {
        lock (_gate)
        {
            if (_completion is not null)
                throw new InvalidOperationException("单帧接续不能重复启动。");
            return _completion = Task.Run(RunAsync);
        }
    }

    /// <summary>非 wait-free：短 gate 内最多清一个有界且已静止的 payload，不碰在途借用区。</summary>
    internal void RequestStop()
    {
        lock (_gate)
        {
            _stopRequested = true;
            if (_payload is not null)
            {
                CryptographicOperations.ZeroMemory(_payload);
                _payload = null;
            }
        }
    }

    /// <summary>单次移交完整数组；Take 先赢后 Stop 不能回收或清写已交付结果。</summary>
    internal bool TryTake(out byte[]? payload)
    {
        lock (_gate)
        {
            payload = null;
            if (_completion is null || !_completion.IsCompleted || _stopRequested || _taken ||
                _end != ReadEnd.Frame || _payload is null)
                return false;
            payload = _payload;
            _payload = null;
            _taken = true;
            return true;
        }
    }

    private async Task RunAsync()
    {
        lock (_gate)
        {
            // 同一 gate 发布屏障：Completion 已登记，构造和 Start 调用方不执行流同步前缀。
            if (_stopRequested)
            {
                _end = ReadEnd.Stopped;
                return;
            }
        }

        byte[]? prefix = null;
        byte[]? payload = null;
        byte[]? unknownBorrow = null;
        Task<int>? original = null;
        ReadEnd end = ReadEnd.Stopped;
        Exception? error = null;
        try
        {
            prefix = new byte[TransportConstants.LengthPrefixBytes];
            prefix[0] = _firstByte;
            byte[] current = prefix;
            int offset = 1;
            while (true)
            {
                lock (_gate) { if (_stopRequested) break; }
                if (offset == current.Length)
                {
                    if (payload is not null)
                    {
                        end = ReadEnd.Frame;
                        break;
                    }
                    uint length = BinaryPrimitives.ReadUInt32BigEndian(prefix);
                    if (!FrameReader.TryValidateLength(length, _maxBytes, out int count, out string? rejection))
                        throw new FrameProtocolException(rejection!);
                    // 远端 uint 已验证后才转为已验证 int 分配；最多 1 MiB，不是进程总内存上限。
                    payload = new byte[count];
                    current = payload;
                    offset = 0;
                    continue;
                }

                // 每次都重置原 Task；后续分片未取到 Task 不能误用上次成功 Task 冒充借用退出证明。
                original = null;
                unknownBorrow = current;
                original = _stream.ReadAsync(current.AsMemory(offset), CancellationToken.None).AsTask();
                int read = await original.ConfigureAwait(false);
                unknownBorrow = null;
                // 获得原 Task 后必须先 await，Stop 不能抢先返回或掩盖迟到原读故障。
                if (read < 0 || read > current.Length - offset)
                    throw new FrameProtocolException("control-read-invalid-count");
                if (read == 0)
                    throw new EndOfStreamException(payload is null ? "control-prefix-truncated" : "control-payload-truncated");
                offset += read;
            }
        }
        catch (Exception caught)
        {
            // await 只抛首项；保留原 Task 的全部兄弟、顺序、重复引用及嵌套，不 Flatten。
            AggregateException? container = original?.Exception;
            error = container is null ? caught
                : container.InnerExceptions.Count == 1 ? container.InnerExceptions[0] : container;
            if (original is not null) unknownBorrow = null;
            end = ReadEnd.Faulted;
        }

        // 未取得 Task 的异常借用按整块数组隔离；已结束的其它数组仍可安全清理。
        if (prefix is not null && !ReferenceEquals(prefix, unknownBorrow))
            CryptographicOperations.ZeroMemory(prefix);
        lock (_gate)
        {
            _end = end;
            _error = error;
            _borrowExitUnproven = unknownBorrow is not null;
            if (end == ReadEnd.Frame && !_stopRequested)
            {
                _payload = payload;
                payload = null;
            }
        }
        if (payload is not null && !ReferenceEquals(payload, unknownBorrow))
            CryptographicOperations.ZeroMemory(payload);
    }
}
