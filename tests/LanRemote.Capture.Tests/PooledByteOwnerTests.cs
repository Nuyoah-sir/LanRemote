using Xunit;

namespace LanRemote.Capture.Tests;

[Collection(GdiBufferObservationCollection.Name)]
public sealed class PooledByteOwnerTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(1025)]
    public void Rent_ExposesOnlyRequestedLengthAndSharesThatPrefixWithArray(int length)
    {
        using PooledByteOwner owner = PooledByteOwner.Rent(length);
        Assert.Equal(length, owner.Memory.Length);
        Assert.True(owner.Array.Length >= length);
        Assert.Same(owner.Array, owner.Array);
        owner.Memory.Span.Fill(0x5A);
        Assert.All(owner.Array.Take(length), value => Assert.Equal((byte)0x5A, value));
        owner.Array[length - 1] = 0xA5;
        Assert.Equal((byte)0xA5, owner.Memory.Span[^1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = owner.Memory.Slice(0, length + 1); });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Rent_RejectsNonPositiveLength(int length)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PooledByteOwner.Rent(length));
    }

    [Fact]
    public void Dispose_IsIdempotentAndInvalidatesBothAccessors()
    {
        using PooledByteOwner owner = PooledByteOwner.Rent(17);
        owner.Memory.Span.Fill(0xC7);
        owner.Dispose();
        owner.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { _ = owner.Memory; });
        Assert.Throws<ObjectDisposedException>(() => { _ = owner.Array; });

        // 新租用期间重复释放旧 owner 不能再次归还或清零活跃内存。
        using PooledByteOwner active = PooledByteOwner.Rent(17);
        active.Memory.Span.Fill(0x69);
        owner.Dispose();
        Assert.All(active.Memory.ToArray(), value => Assert.Equal((byte)0x69, value));
        Assert.Equal(17, active.Memory.Length);
    }

    [Fact]
    public void Dispose_ClearsEntireRentedArrayIncludingHiddenTailWithoutParallelReuse()
    {
        using PooledByteOwner owner = PooledByteOwner.Rent(17);
        byte[] rented = owner.Array;
        Assert.True(rented.Length > owner.Memory.Length);
        Array.Fill(rented, (byte)0xD3);
        Assert.Equal((byte)0xD3, rented[^1]);

        // 本集合禁止并行。归还与同步观察之间不 await、不租用、不启动其他操作；
        // 先完成全数组扫描，再调用断言，避免观察与并行租用竞争。
        owner.Dispose();
        int firstDirtyIndex = -1;
        for (int i = 0; i < rented.Length; i++)
        {
            if (rented[i] != 0)
            {
                firstDirtyIndex = i;
                break;
            }
        }
        Assert.Equal(-1, firstDirtyIndex);
        Assert.Throws<ObjectDisposedException>(() => { _ = owner.Memory; });
    }
}
