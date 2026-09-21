using System.Net;
using System.Net.Sockets;

namespace LanRemote.Transport.Tests;

/// <summary>
/// <see cref="ConnectionAdmissionLimiter"/> 的行为。
/// </summary>
/// <remarks>
/// 覆盖评审 A-7：限额必须在握手之前占用、必须在 <c>finally</c> 里释放，
/// 而且释放必须幂等（连接处理有多条退出分支）。
/// </remarks>
public sealed class ConnectionAdmissionLimiterTests
{
    private static readonly IPAddress A = IPAddress.Parse("192.168.1.10");
    private static readonly IPAddress B = IPAddress.Parse("192.168.1.11");

    [Fact]
    public void Acquires_Up_To_Global_Limit_Then_Refuses()
    {
        ConnectionAdmissionLimiter limiter = new(globalLimit: 2, perAddressLimit: 2);

        Assert.True(limiter.TryAcquire(A, out AdmissionLease? first));
        Assert.True(limiter.TryAcquire(B, out AdmissionLease? second));
        Assert.False(limiter.TryAcquire(A, out AdmissionLease? third));
        Assert.Null(third);

        Assert.Equal(2, limiter.GlobalInUse);

        first!.Dispose();
        second!.Dispose();
        Assert.Equal(0, limiter.GlobalInUse);
    }

    [Fact]
    public void Refuses_Same_Address_Beyond_Per_Address_Limit_Even_When_Global_Has_Room()
    {
        ConnectionAdmissionLimiter limiter = new(globalLimit: 4, perAddressLimit: 1);

        Assert.True(limiter.TryAcquire(A, out AdmissionLease? first));
        Assert.False(limiter.TryAcquire(A, out AdmissionLease? second));
        Assert.Null(second);

        // 换个 IP 仍然可以：证明拦的是"每源 IP"，不是全局。
        Assert.True(limiter.TryAcquire(B, out AdmissionLease? other));

        first!.Dispose();
        other!.Dispose();
        Assert.Equal(0, limiter.InUseFor(A));
        Assert.Equal(0, limiter.InUseFor(B));
    }

    [Fact]
    public void Release_Is_Idempotent()
    {
        ConnectionAdmissionLimiter limiter = new(globalLimit: 3, perAddressLimit: 3);

        Assert.True(limiter.TryAcquire(A, out AdmissionLease? lease));

        lease!.Dispose();
        lease.Dispose();
        lease.Dispose();

        // 幂等不等于"多还了"：占用数必须还是 0，不能变成负数。
        Assert.Equal(0, limiter.GlobalInUse);
        Assert.Equal(0, limiter.InUseFor(A));
        Assert.True(lease.IsReleased);
    }

    [Fact]
    public void Slot_Is_Reclaimed_After_Release()
    {
        ConnectionAdmissionLimiter limiter = new(globalLimit: 1, perAddressLimit: 1);

        Assert.True(limiter.TryAcquire(A, out AdmissionLease? lease));
        Assert.False(limiter.TryAcquire(A, out _));

        lease!.Dispose();
        Assert.True(limiter.TryAcquire(A, out AdmissionLease? again));
        again!.Dispose();
    }

    [Fact]
    public void Per_Address_Limit_Cannot_Exceed_Global_Limit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ConnectionAdmissionLimiter(globalLimit: 2, perAddressLimit: 3));
    }

    [Fact]
    public void Limits_Must_Be_Positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ConnectionAdmissionLimiter(globalLimit: 0, perAddressLimit: 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ConnectionAdmissionLimiter(globalLimit: 1, perAddressLimit: 0));
    }
}
