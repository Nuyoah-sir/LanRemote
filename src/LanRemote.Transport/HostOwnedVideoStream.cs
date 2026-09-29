using System.Net.Security;

namespace LanRemote.Transport;

/// <summary>仅转发真实 SSL 读写的适配器；不转移 socket/SSL 的最终所有权。</summary>
/// <remarks>
/// Dispose 发出 Host 关闭请求，不等待物理释放；pipeline 必须 await AcceptedConnection.CloseAsync()。
/// 因此 writer 的 StreamDisposeSucceeded 只证明请求正常发出，不能证明物理关闭完成。
/// DisposeAsync 则等待同一关闭任务；任务完成后的释放错误仍由 Host 有界诊断报告。
/// </remarks>
internal sealed class HostOwnedVideoStream : Stream
{
    private readonly SslStream _stream;
    private readonly ConnectionCloseHandle _closer;
    private int _disposed;

    internal HostOwnedVideoStream(SslStream stream, ConnectionCloseHandle closer)
    {
        _stream = stream;
        _closer = closer;
    }

    private SslStream ActiveStream
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _stream;
        }
    }

    public override bool CanRead => Volatile.Read(ref _disposed) == 0 && _stream.CanRead;
    public override bool CanWrite => Volatile.Read(ref _disposed) == 0 && _stream.CanWrite;
    public override bool CanSeek => false;
    public override bool CanTimeout => _stream.CanTimeout;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int ReadTimeout
    {
        get => ActiveStream.ReadTimeout;
        set => ActiveStream.ReadTimeout = value;
    }

    public override int WriteTimeout
    {
        get => ActiveStream.WriteTimeout;
        set => ActiveStream.WriteTimeout = value;
    }

    public override void Flush() => ActiveStream.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) =>
        ActiveStream.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => ActiveStream.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => ActiveStream.Read(buffer);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ActiveStream.ReadAsync(buffer, offset, count, cancellationToken);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ActiveStream.ReadAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => ActiveStream.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => ActiveStream.Write(buffer);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ActiveStream.WriteAsync(buffer, offset, count, cancellationToken);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        ActiveStream.WriteAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _ = _closer.CloseAsync();
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        Dispose();
        return new ValueTask(_closer.CloseAsync());
    }
}
