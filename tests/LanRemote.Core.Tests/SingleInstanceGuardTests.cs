using LanRemote.Core.Infrastructure;
using Xunit;

namespace LanRemote.Core.Tests;

/// <summary>
/// <see cref="SingleInstanceGuard"/> 行为测试。
/// </summary>
/// <remarks>
/// 注意：这里只能覆盖「同一进程内」的所有权语义。
/// 真正的跨进程验证需要人工双击 exe 两次，见 HANDOFF.md 的手工验证章节。
/// </remarks>
public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void TryAcquire_FirstCallSucceeds()
    {
        using SingleInstanceGuard? guard = SingleInstanceGuard.TryAcquire();

        Assert.NotNull(guard);
    }

    [Fact]
    public void TryAcquire_SecondCallWhileHeld_ReturnsNull()
    {
        using SingleInstanceGuard? first = SingleInstanceGuard.TryAcquire();
        Assert.NotNull(first);

        SingleInstanceGuard? second = SingleInstanceGuard.TryAcquire();

        Assert.Null(second);
    }

    [Fact]
    public void TryAcquire_AfterDispose_SucceedsAgain()
    {
        SingleInstanceGuard? first = SingleInstanceGuard.TryAcquire();
        Assert.NotNull(first);

        first.Dispose();

        using SingleInstanceGuard? second = SingleInstanceGuard.TryAcquire();
        Assert.NotNull(second);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        SingleInstanceGuard? guard = SingleInstanceGuard.TryAcquire();
        Assert.NotNull(guard);

        guard.Dispose();

        Exception? exception = Record.Exception(guard.Dispose);
        Assert.Null(exception);
    }

    [Fact]
    public void MutexName_IsSessionScoped()
    {
        Assert.StartsWith(@"Local\", SingleInstanceGuard.MutexName, StringComparison.Ordinal);
    }
}
