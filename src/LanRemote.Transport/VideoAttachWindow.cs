namespace LanRemote.Transport;

/// <summary>success 序列化后、首次写出前捕获的绝对视频附着窗口；登记不重置。</summary>
internal sealed class VideoAttachWindow
{
    private readonly TimeProvider _timeProvider;
    private readonly long _startedAt;
    private readonly TimeSpan _budget;

    internal VideoAttachWindow(TimeProvider timeProvider, int expiresInMs)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expiresInMs);
        _timeProvider = timeProvider;
        _budget = TimeSpan.FromMilliseconds(Math.Min(expiresInMs, 15_000));
        _startedAt = timeProvider.GetTimestamp();
    }

    internal bool IsExpired
    {
        get
        {
            TimeSpan elapsed = _timeProvider.GetElapsedTime(_startedAt);
            return elapsed < TimeSpan.Zero || elapsed >= _budget;
        }
    }
}
