using System.Buffers;

namespace LanRemote.Capture;

/// <summary>精确可见长度的数组池 owner；归还时清零整个实际租用数组。</summary>
internal sealed class PooledByteOwner : IMemoryOwner<byte>
{
    private byte[]? _array;
    private readonly int _length;

    private PooledByteOwner(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        _array = ArrayPool<byte>.Shared.Rent(length);
        _length = length;
    }

    internal static PooledByteOwner Rent(int length) => new(length);

    internal byte[] Array => Volatile.Read(ref _array)
        ?? throw new ObjectDisposedException(nameof(PooledByteOwner));

    public Memory<byte> Memory => Array.AsMemory(0, _length);

    public void Dispose()
    {
        byte[]? array = Interlocked.Exchange(ref _array, null);
        if (array is not null)
            ArrayPool<byte>.Shared.Return(array, clearArray: true);
    }
}
