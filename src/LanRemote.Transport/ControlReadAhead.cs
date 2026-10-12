namespace LanRemote.Transport;

/// <summary>M6 单次首字节所有权原语；尚未接真实认证、审批或 Control 消息分发。</summary>
/// <remarks>
/// 不拥有流、不关闭流、不创建取消源或计时器。Stop 只禁止后续交付；所有者仍须在锁外关流，
/// 并等待 Completion，不能把逻辑停止当作原读取已退出。过了读取准入检查的同步前缀可能仍在运行。
/// Completion 表示 worker/已取得的原 Task 已退出，不表示读成功；失败必须检查 Snapshot.Error。
/// 不提供认证、首字节到达时间或 deadline 证明，也不阻止另一个外部对象误读同一流。
/// 若违规流在 ReadAsync/AsTask 抛错前启动了不可获取的后台 I/O，只能证明调用 worker 退出，
/// 无法证明未知 I/O 已退出；未取得原 Task 时不主动清写借出 buffer。
/// </remarks>
internal sealed class ControlReadAhead
{
    internal enum ReadEnd { Pending, SkippedLocalStop, Byte, EndOfStream, Faulted }
    internal sealed record ReadSnapshot(ReadEnd End, bool StopRequested, bool Taken, Exception? Error);
    // null 表示 EOF；0x00 是有效首字节，绝不能与 EOF 混淆。无可变 buffer 暴露。
    internal sealed record ReadResult(byte? FirstByte);

    private readonly object _gate = new();
    private readonly Stream _stream;
    private Task? _completion;
    private bool _stopRequested;
    private bool _taken;
    private byte _value;
    private ReadEnd _end;
    private Exception? _error;

    internal ControlReadAhead(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
    }

    internal Task? Completion { get { lock (_gate) return _completion; } }
    internal ReadSnapshot Snapshot
    {
        get { lock (_gate) return new(_end, _stopRequested, _taken, _error); }
    }

    /// <summary>只准启动一次。先登记 worker，再在锁外执行流的任意同步前缀。</summary>
    internal Task Start()
    {
        lock (_gate)
        {
            if (_completion is not null)
                throw new InvalidOperationException("单次首字节读取不能重复启动。");
            return _completion = Task.Run(RunAsync);
        }
    }

    /// <summary>幂等逻辑停止；可短暂竞争 gate，但不等 I/O、不调用流或任意取消回调。</summary>
    internal void RequestStop()
    {
        lock (_gate)
        {
            _stopRequested = true;
            _value = 0;
        }
    }

    /// <summary>原 worker 退出之后最多交付一次；停止或失败后不交付字节/EOF。</summary>
    internal bool TryTake(out ReadResult? result)
    {
        lock (_gate)
        {
            result = null;
            if (_completion is null || !_completion.IsCompleted || _stopRequested || _taken ||
                _end is not (ReadEnd.Byte or ReadEnd.EndOfStream))
                return false;
            result = new ReadResult(_end == ReadEnd.Byte ? _value : null);
            _taken = true;
            _value = 0;
            return true;
        }
    }

    private async Task RunAsync()
    {
        lock (_gate)
        {
            if (_stopRequested)
            {
                _end = ReadEnd.SkippedLocalStop;
                return;
            }
        }

        // 生命周期覆盖 ReadAsync 同步前缀、AsTask 转换和取得的原 Task，不在 gate 内执行流代码。
        byte[] buffer = new byte[1];
        Task<int>? original = null;
        ReadEnd end;
        Exception? error = null;
        byte value = 0;
        try
        {
            original = _stream.ReadAsync(buffer.AsMemory(), CancellationToken.None).AsTask();
            int count = await original.ConfigureAwait(false);
            if (count is < 0 or > 1)
                throw new FrameProtocolException("control-read-invalid-count");
            end = count == 0 ? ReadEnd.EndOfStream : ReadEnd.Byte;
            if (count == 1) value = buffer[0];
        }
        catch (Exception caught)
        {
            // await 只抛第一项，必须从原 Task 恢复同级多 fault；不 Flatten 或按异常类型吞错。
            AggregateException? container = original?.Exception;
            error = container is null ? caught
                : container.InnerExceptions.Count == 1 ? container.InnerExceptions[0] : container;
            end = ReadEnd.Faulted;
        }
        finally
        {
            // 仅已取得且 await 退出的原 Task 能证明本方借用结束；不猜测异常流的隐藏 I/O。
            // 只清这一个本方 buffer，不声称底层 TLS 副本已擦除。
            if (original is not null) buffer[0] = 0;
        }

        lock (_gate)
        {
            _end = end;
            _error = error;
            _value = _stopRequested ? (byte)0 : value;
        }
    }
}
