using System.Buffers;
using LanRemote.Core.Models;

namespace LanRemote.Transport.Tests;

internal static class VideoFrameTestData
{
    // 独立手工黄金向量：不是生产 Writer 的输出。payload 是不透明测试字节，不宣称为可解码 JPEG。
    internal static byte[] Header() => Convert.FromHexString(
        "4C52564601010000" +
        "0123456789ABCDEF" +
        "1020304050607080" +
        "0000078000000438" +
        "3C00000000000005");

    internal static byte[] Payload() => [0x10, 0x20, 0x30, 0x40, 0x50];
    internal static byte[] Wire() => [.. Header(), .. Payload()];

    internal static EncodedFrame Frame(RecordingVideoOwner owner) => new(
        VideoCodec.Jpeg, 1920, 1080, 0x0123456789ABCDEFul, 0x1020304050607080L, 60, owner, 5);
}

internal sealed class RecordingVideoOwner : IMemoryOwner<byte>
{
    internal RecordingVideoOwner(int capacity)
    {
        Bytes = new byte[capacity];
        Bytes.AsSpan().Fill(0xCC);
        VisibleLength = capacity;
    }

    internal byte[] Bytes { get; }
    internal int VisibleLength { get; set; }
    internal int MemoryReads { get; private set; }
    internal int ThrowOnMemoryRead { get; set; } = int.MaxValue;
    internal Exception MemoryFailure { get; set; } = new IOException("测试 owner 内存访问失败。");
    internal Action<int>? OnMemoryRead { get; set; }
    internal Action? BeforeDispose { get; set; }
    internal Exception? DisposeFailure { get; set; }
    internal int DisposeCalls { get; private set; }

    public Memory<byte> Memory
    {
        get
        {
            MemoryReads++;
            OnMemoryRead?.Invoke(MemoryReads);
            if (MemoryReads == ThrowOnMemoryRead)
            {
                throw MemoryFailure;
            }

            ObjectDisposedException.ThrowIf(DisposeCalls != 0, this);
            return Bytes.AsMemory(0, VisibleLength);
        }
    }

    public void Dispose()
    {
        DisposeCalls++;
        BeforeDispose?.Invoke();
        if (DisposeFailure is not null)
        {
            throw DisposeFailure;
        }
    }
}

internal sealed class RecordingVideoPool
{
    internal List<int> RequestedLengths { get; } = [];
    internal List<RecordingVideoOwner> Owners { get; } = [];

    internal IMemoryOwner<byte> Rent(int length)
    {
        RequestedLengths.Add(length);
        RecordingVideoOwner owner = new(length + 17);
        Owners.Add(owner);
        return owner;
    }
}

// 包装而非派生 MemoryStream，分别记录实际字节、请求长度、令牌和释放次数，避免虚调用双计数。
internal sealed class RecordingVideoStream(byte[]? input = null, int chunkSize = int.MaxValue) : Stream
{
    private readonly byte[] _input = input ?? [];
    private readonly MemoryStream _output = new();
    private readonly TaskCompletionSource _readGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _writeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal int BytesRead { get; private set; }
    internal int BytesWritten { get; private set; }
    internal byte[] Written => _output.ToArray();
    internal List<int> ReadRequests { get; } = [];
    internal List<int> WriteRequests { get; } = [];
    internal List<CancellationToken> Tokens { get; } = [];
    internal int DisposeCalls { get; private set; }
    internal int FlushCalls { get; private set; }
    internal int FailReadAt { get; init; } = int.MaxValue;
    internal int FailWriteAt { get; init; } = int.MaxValue;
    internal int GateReadAt { get; init; } = int.MaxValue;
    internal int GateWriteAt { get; init; } = int.MaxValue;
    internal bool FailFlush { get; init; }
    internal bool FailDispose { get; init; }
    internal Exception ReadFailure { get; init; } = new IOException("测试读取失败。");
    internal Exception WriteFailure { get; init; } = new IOException("测试写入失败。");
    internal Action? BeforeIoFailure { get; init; }
    internal TaskCompletionSource ReadBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource WriteBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void ReleaseRead() => _readGate.TrySetResult();
    internal void ReleaseWrite() => _writeGate.TrySetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ReadRequests.Add(buffer.Length);
        Tokens.Add(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(DisposeCalls != 0, this);
        if (BytesRead >= FailReadAt)
        {
            BeforeIoFailure?.Invoke();
            throw ReadFailure;
        }

        if (BytesRead >= GateReadAt)
        {
            ReadBlocked.TrySetResult();
            await _readGate.Task.WaitAsync(cancellationToken);
            ObjectDisposedException.ThrowIf(DisposeCalls != 0, this);
        }

        int count = Math.Min(Math.Min(buffer.Length, chunkSize), _input.Length - BytesRead);
        count = Math.Min(count, FailReadAt - BytesRead);
        if (BytesRead < GateReadAt)
        {
            count = Math.Min(count, GateReadAt - BytesRead);
        }

        _input.AsMemory(BytesRead, count).CopyTo(buffer);
        BytesRead += count;
        return count;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        WriteRequests.Add(buffer.Length);
        Tokens.Add(cancellationToken);
        int offset = 0;
        while (offset < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(DisposeCalls != 0, this);
            if (BytesWritten >= FailWriteAt)
            {
                BeforeIoFailure?.Invoke();
                throw WriteFailure;
            }

            if (BytesWritten >= GateWriteAt)
            {
                WriteBlocked.TrySetResult();
                await _writeGate.Task.WaitAsync(cancellationToken);
                ObjectDisposedException.ThrowIf(DisposeCalls != 0, this);
            }

            int count = Math.Min(buffer.Length - offset, FailWriteAt - BytesWritten);
            if (BytesWritten < GateWriteAt)
            {
                count = Math.Min(count, GateWriteAt - BytesWritten);
            }

            _output.Write(buffer.Span.Slice(offset, count));
            BytesWritten += count;
            offset += count;
        }
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        FlushCalls++;
        Tokens.Add(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(DisposeCalls != 0, this);
        if (FailFlush)
        {
            throw new IOException("测试刷新失败。");
        }

        return Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeCalls++;
            // 唤醒在途 I/O，由它们观察已关闭状态，不留下后台读写。
            _readGate.TrySetResult();
            _writeGate.TrySetResult();
            _output.Dispose();
            if (FailDispose)
            {
                throw new IOException("测试关闭失败。");
            }
        }

        base.Dispose(disposing);
    }

    public override bool CanRead => DisposeCalls == 0;
    public override bool CanWrite => DisposeCalls == 0;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

// 在关闭真实持有资源之前抛错：Dispose 被调用不等于资源已关闭。
// 测试必须在 finally 中调用 ReleaseResources，不通过重复 Dispose 假装修好。
internal sealed class BeforeCloseFailureVideoStream(RecordingVideoStream resource) : Stream
{
    internal IOException CleanupFailure { get; } = new("测试在真实 close 前失败。");
    internal int DisposeAttempts { get; private set; }
    internal bool IsClosed => !resource.CanRead;
    internal void ReleaseResources() => resource.Dispose();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeAttempts++;
            throw CleanupFailure;
        }
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        resource.ReadAsync(buffer, cancellationToken);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        resource.WriteAsync(buffer, cancellationToken);
    public override Task FlushAsync(CancellationToken cancellationToken) => resource.FlushAsync(cancellationToken);
    public override bool CanRead => resource.CanRead;
    public override bool CanWrite => resource.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

internal static class VideoReviewDiagnostics
{
    // 保留反射断言：诊断尚不存在时也能执行红测，同时约束不得扩大为 public API。
    private static T GetInternal<T>(object codec, string name)
    {
        System.Reflection.PropertyInfo? property = codec.GetType().GetProperty(
            name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(property);
        Assert.Equal(typeof(T), property.PropertyType);
        Assert.True(property.GetMethod!.IsAssembly);
        return Assert.IsAssignableFrom<T>(property.GetValue(codec));
    }

    internal static IReadOnlyList<Exception> Errors(object codec) => GetInternal<IReadOnlyList<Exception>>(codec, "CleanupErrors");
    internal static bool IsTerminated(object codec) => GetInternal<bool>(codec, "IsTerminated");
    internal static bool StreamDisposeSucceeded(object codec) => GetInternal<bool>(codec, "StreamDisposeSucceeded");

    internal static void AssertReadOnly(IReadOnlyList<Exception> errors)
    {
        IList<Exception> list = Assert.IsAssignableFrom<IList<Exception>>(errors);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list[0] = new Exception("不允许改写诊断快照。"));
    }
}
