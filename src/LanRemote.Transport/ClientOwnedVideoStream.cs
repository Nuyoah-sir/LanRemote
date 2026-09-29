using System.Net.Security;

namespace LanRemote.Transport;

/// <summary>转发客户端真实 SSL，不转移 TCP/SSL 的最终所有权。</summary>
/// <remarks>
/// 同步 Dispose 只请求所属连接关闭；DisposeAsync 等待同一关闭任务。
/// 两者都不消费 CleanupErrors；高层必须检查该诊断并单独 join 原读写任务。
/// reader 的 StreamDisposeSucceeded 因此只代表请求正常发出，不代表物理释放成功。
/// </remarks>
internal sealed class ClientOwnedVideoStream(TlsConnection connection) : Stream
{
    private int _disposed;

    private SslStream ActiveStream
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return connection.Stream;
        }
    }

    private bool IsActive => Volatile.Read(ref _disposed) == 0 && !connection.IsCloseRequested;
    public override bool CanRead => IsActive && connection.Stream.CanRead;
    public override bool CanWrite => IsActive && connection.Stream.CanWrite;
    public override bool CanSeek => false;
    public override bool CanTimeout => ActiveStream.CanTimeout;
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
    public override Task FlushAsync(CancellationToken cancellationToken) => ActiveStream.FlushAsync(cancellationToken);
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
            _ = connection.CloseAsync();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        Dispose();
        return new ValueTask(connection.CloseAsync());
    }
}
