using System.Net;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport;

/// <summary>
/// 认证失败限流（规格 04 §14；按 remote IP）。
/// </summary>
/// <remarks>
/// <para><b>只计「到达密码学校验且失败」</b>（ADR-038 第 2 条）：帧格式违规、拒绝/超时/断连、
/// 审批拒绝与审批超时都<b>不</b>计。理由：限流防的是「猜密钥」；把协议层噪声计进去，
/// 攻击者能用垃圾帧给受害者 IP 刷封禁（放大面），也会把用户自己的审批决定罚成攻击。</para>
/// <para><b>语义</b>：10 分钟滑动窗口内累计 5 次失败 → 记一次 60 s 封禁；
/// 封禁到点后<b>允许再试</b>（不因「窗口内仍有 ≥5 条旧记录」继续拒），再失败则再封；
/// 成功即清空该 IP 的全部记录（含封禁）。</para>
/// <para><b>时钟可注入</b>（<see cref="TimeProvider"/>）：窗口与封禁的推进在测试里用假钟，
/// 不去动系统时间。条目按 IP 懒清理（只在对应 IP 再被触碰时回收）：
/// 同子网 /24 的地址空间有界，本类不假设「全网 IPv4 都会被攻击」。</para>
/// <para>本类不做任何日志——key / proof 绝不进日志的前提之一是「接触它们的层不说话」。</para>
/// </remarks>
public sealed class FailedAuthLimiter
{
    private readonly object _gate = new();
    private readonly Dictionary<IPAddress, Entry> _entries = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _window;
    private readonly TimeSpan _blockDuration;
    private readonly int _maxFailures;

    /// <summary>构造限流器（默认值 = 规格常量：10 分钟 / 5 次 / 60 秒）。</summary>
    /// <param name="timeProvider">时钟；为空用系统钟（测试注入假钟）。</param>
    public FailedAuthLimiter(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _window = TimeSpan.FromMilliseconds(AuthProtocol.FailedAuthWindowMilliseconds);
        _blockDuration = TimeSpan.FromMilliseconds(AuthProtocol.FailedAuthBlockMilliseconds);
        _maxFailures = AuthProtocol.MaxFailedAuthAttempts;
    }

    /// <summary>触发封禁所需的失败次数（规格常量，测试引用）。</summary>
    public int MaxFailures => _maxFailures;

    /// <summary>
    /// 该来源 IP 当前是否处于封禁中。
    /// </summary>
    /// <param name="source">来源 IP。</param>
    /// <param name="retryAfter">封禁中时为剩余时长；否则为零。</param>
    /// <returns>是否封禁中。</returns>
    public bool IsBlocked(IPAddress source, out TimeSpan retryAfter)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_gate)
        {
            if (!_entries.TryGetValue(source, out Entry? entry))
            {
                retryAfter = TimeSpan.Zero;
                return false;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            PruneFailures(entry, now);

            if (entry.BlockedUntil is { } blockedUntil && blockedUntil > now)
            {
                retryAfter = blockedUntil - now;
                return true;
            }

            // 无失败记录且封禁已过期：条目彻底无用，顺手回收。
            if (entry.Failures.Count == 0)
            {
                _entries.Remove(source);
            }

            retryAfter = TimeSpan.Zero;
            return false;
        }
    }

    /// <summary>
    /// 记一次「到达密码学校验且失败」。达到阈值时记下 60 s 封禁（含本次）。
    /// </summary>
    /// <param name="source">来源 IP。</param>
    public void RecordFailure(IPAddress source)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_gate)
        {
            if (!_entries.TryGetValue(source, out Entry? entry))
            {
                entry = new Entry();
                _entries.Add(source, entry);
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();

            // 只丢过期记录，不删条目——紧接着要往条目里写本次失败。
            PruneFailures(entry, now);
            entry.Failures.Enqueue(now);

            if (entry.Failures.Count >= _maxFailures)
            {
                entry.BlockedUntil = now + _blockDuration;
            }
        }
    }

    /// <summary>认证成功：清空该 IP 的全部失败记录与封禁。</summary>
    /// <param name="source">来源 IP。</param>
    public void RecordSuccess(IPAddress source)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_gate)
        {
            _entries.Remove(source);
        }
    }

    /// <summary>窗口内的失败次数（本机 UI 显示用；不改变任何状态）。</summary>
    /// <param name="source">来源 IP。</param>
    /// <returns>失败次数。</returns>
    public int CountRecentFailures(IPAddress source)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_gate)
        {
            if (!_entries.TryGetValue(source, out Entry? entry))
            {
                return 0;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            PruneFailures(entry, now);

            // 无失败记录且封禁已过期：条目彻底无用，顺手回收。
            if (entry.Failures.Count == 0 && !(entry.BlockedUntil is { } until && until > now))
            {
                _entries.Remove(source);
            }

            return entry.Failures.Count;
        }
    }

    /// <summary>丢掉窗口外的失败记录（不动封禁字段）。</summary>
    private void PruneFailures(Entry entry, DateTimeOffset now)
    {
        DateTimeOffset cutoff = now - _window;
        while (entry.Failures.Count > 0 && entry.Failures.Peek() < cutoff)
        {
            _ = entry.Failures.Dequeue();
        }
    }

    /// <summary>单个 IP 的状态：窗口内的失败时刻 + 封禁截止。</summary>
    private sealed class Entry
    {
        public Queue<DateTimeOffset> Failures { get; } = new();

        public DateTimeOffset? BlockedUntil { get; set; }
    }
}
