namespace LanRemote.Transport;

/// <summary>
/// 按 seq 结算 ping/pong 往返时延的计量内核：发起 ping 时登记起点，收到回显该 seq 的 pong 时结算一次。
/// </summary>
/// <remarks>
/// 待确认样本<b>有界</b>，超出上限一律拒绝登记，绝不无界增长；重复或未知的回显不结算也不抛异常
/// ——回显序号属于对端数据，对端数据不能让本进程抛出。时钟可注入，便于确定性测量；
/// 本类不收发字节、不建立连接、不定时，ping 的发送节奏由上层会话决定。
/// </remarks>
internal sealed class PingRttMeter
{
    /// <summary>同时未确认的 ping 上限；超出即拒绝登记，不排队、不静默丢弃旧样本以外的无限增长。</summary>
    internal const int MaxPending = 32;

    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<long, long> _pending = new();
    private readonly object _gate = new();
    private TimeSpan? _latest;
    private long _samples;

    internal PingRttMeter(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>尚未结算的 ping 数；恒不超过 <see cref="MaxPending"/>。</summary>
    internal int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>已成功结算的往返时延样本数；重复与未知回显不计数。</summary>
    internal long SampleCount => Interlocked.Read(ref _samples);

    /// <summary>最近一次成功结算的往返时延；尚无样本时为 null，不虚构 0。</summary>
    internal TimeSpan? LatestRtt
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    /// <summary>登记一次发出的 ping；序号重复或已达上限返回 false，负数序号抛异常。</summary>
    internal bool Arm(long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        lock (_gate)
        {
            if (_pending.Count >= MaxPending || _pending.ContainsKey(sequence))
            {
                return false;
            }

            _pending[sequence] = _timeProvider.GetTimestamp();
            return true;
        }
    }

    /// <summary>用 pong 回显的 seq 结算往返时延；未知或已结算过的回显返回 false，对端数据不抛异常。</summary>
    internal bool Settle(long echoedSequence, out TimeSpan roundTrip)
    {
        lock (_gate)
        {
            if (echoedSequence < 0 || !_pending.Remove(echoedSequence, out long started))
            {
                roundTrip = TimeSpan.Zero;
                return false;
            }

            roundTrip = _timeProvider.GetElapsedTime(started);
            _latest = roundTrip;
            Interlocked.Increment(ref _samples);
            return true;
        }
    }

    /// <summary>清理早于 maxAge 仍未结算的样本，返回清理条数；对端不回 pong 时不让其无限堆积。</summary>
    internal int Prune(TimeSpan maxAge)
    {
        if (maxAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAge), maxAge, "清理时限必须非负。");
        }
        long now = _timeProvider.GetTimestamp();
        List<long> stale = [];
        lock (_gate)
        {
            foreach ((long sequence, long started) in _pending)
            {
                if (_timeProvider.GetElapsedTime(started, now) >= maxAge)
                {
                    stale.Add(sequence);
                }
            }

            foreach (long sequence in stale)
            {
                _pending.Remove(sequence);
            }
        }

        return stale.Count;
    }

    /// <summary>整体清空；不跨会话/主机生命周期保留，也不影响已读出的历史值。</summary>
    internal void Reset()
    {
        lock (_gate)
        {
            _pending.Clear();
            _latest = null;
            Interlocked.Exchange(ref _samples, 0);
        }
    }
}
