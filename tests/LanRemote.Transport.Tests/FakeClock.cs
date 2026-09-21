namespace LanRemote.Transport.Tests;

/// <summary>
/// 可注入的时钟。
/// </summary>
/// <remarks>
/// 存在的目的：证书有效期必须能被测试到，而<b>绝不允许</b>通过改系统时钟来测
/// （那是全局副作用，会污染同机其它进程，且需要管理员权限）。
/// </remarks>
internal sealed class FakeClock : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _now;

    public FakeClock(DateTimeOffset now)
    {
        _now = now;
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    /// <summary>把时间推进一段。</summary>
    public void Advance(TimeSpan delta)
    {
        lock (_gate)
        {
            _now += delta;
        }
    }
}
