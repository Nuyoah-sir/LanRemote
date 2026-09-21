using System.Net;

namespace LanRemote.Transport;

/// <summary>
/// 连接准入限额：全局上限 + 更小的每源 IP 上限。
/// </summary>
/// <remarks>
/// <para><b>评审 A-7</b>：限额必须在 <b>accept 之后、TLS 握手之前</b>占用，
/// 并且一定要在 <c>finally</c> 里释放。放在握手之后等于让未认证的远端免费消耗握手算力。</para>
/// <para><b><c>TcpListener.Start(backlog)</c> 的 backlog 不是准入防线</b>：
/// 它只是「已建连但还没被 accept」的队列长度，超了之后操作系统会静默丢弃 SYN，
/// 既不计数也不限流，更不能表达「同一个 IP 只能来几条」。应用层必须有自己的限额。</para>
/// <para>每源 IP 上限刻意设得比全局小：局域网里伪造 IP 成本不低，
/// 但一台设备因为 bug 疯狂重连时，小上限能把损害限制在它自己身上。</para>
/// </remarks>
public sealed class ConnectionAdmissionLimiter
{
    private readonly object _gate = new();
    private readonly Dictionary<IPAddress, int> _perAddress = new();
    private readonly int _globalLimit;
    private readonly int _perAddressLimit;

    private int _globalInUse;

    /// <summary>构造限额器。</summary>
    /// <param name="globalLimit">全局同时连接数上限（&gt; 0）。</param>
    /// <param name="perAddressLimit">单个源 IP 同时连接数上限（&gt; 0，且不大于 <paramref name="globalLimit"/>）。</param>
    public ConnectionAdmissionLimiter(int globalLimit, int perAddressLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(globalLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(perAddressLimit);

        if (perAddressLimit > globalLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(perAddressLimit),
                perAddressLimit,
                "每源 IP 上限不应大于全局上限，否则全局上限形同虚设。");
        }

        _globalLimit = globalLimit;
        _perAddressLimit = perAddressLimit;
    }

    /// <summary>全局上限。</summary>
    public int GlobalLimit => _globalLimit;

    /// <summary>每源 IP 上限。</summary>
    public int PerAddressLimit => _perAddressLimit;

    /// <summary>当前全局占用数。</summary>
    public int GlobalInUse
    {
        get
        {
            lock (_gate)
            {
                return _globalInUse;
            }
        }
    }

    /// <summary>指定源 IP 当前的占用数。</summary>
    /// <param name="remoteAddress">源 IP。</param>
    /// <returns>占用数。</returns>
    public int InUseFor(IPAddress remoteAddress)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);

        lock (_gate)
        {
            return _perAddress.TryGetValue(remoteAddress, out int count) ? count : 0;
        }
    }

    /// <summary>
    /// 尝试占用一个名额。
    /// </summary>
    /// <param name="remoteAddress">源 IP。</param>
    /// <param name="lease">成功时为租约；失败为 <see langword="null"/>。</param>
    /// <returns>是否获得名额。</returns>
    /// <remarks>租约<b>必须</b>释放，否则名额永久泄漏。<see cref="AdmissionLease.Dispose"/> 是幂等的。</remarks>
    public bool TryAcquire(IPAddress remoteAddress, out AdmissionLease? lease)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);

        lock (_gate)
        {
            if (_globalInUse >= _globalLimit)
            {
                lease = null;
                return false;
            }

            int used = _perAddress.TryGetValue(remoteAddress, out int count) ? count : 0;
            if (used >= _perAddressLimit)
            {
                lease = null;
                return false;
            }

            _globalInUse++;
            _perAddress[remoteAddress] = used + 1;
            lease = new AdmissionLease(this, remoteAddress);
            return true;
        }
    }

    internal void Release(IPAddress remoteAddress)
    {
        lock (_gate)
        {
            if (_perAddress.TryGetValue(remoteAddress, out int count))
            {
                if (count <= 1)
                {
                    // 归零就移除：字典里不留空条目，避免长期运行下无限增长。
                    _perAddress.Remove(remoteAddress);
                }
                else
                {
                    _perAddress[remoteAddress] = count - 1;
                }
            }

            if (_globalInUse > 0)
            {
                _globalInUse--;
            }
        }
    }
}

/// <summary>
/// 一个准入名额的租约。
/// </summary>
/// <remarks>
/// 释放是<b>幂等</b>的：连接处理路径上有多条退出分支（正常结束 / 异常 / 停机取消），
/// 幂等才能让调用方放心地到处写 <c>Dispose</c> 而不必追踪是否已释放。
/// </remarks>
public sealed class AdmissionLease : IDisposable
{
    private readonly ConnectionAdmissionLimiter _limiter;
    private readonly IPAddress _remoteAddress;
    private int _released;

    internal AdmissionLease(ConnectionAdmissionLimiter limiter, IPAddress remoteAddress)
    {
        _limiter = limiter;
        _remoteAddress = remoteAddress;
    }

    /// <summary>本租约对应的源 IP。</summary>
    public IPAddress RemoteAddress => _remoteAddress;

    /// <summary>是否已释放。</summary>
    public bool IsReleased => Volatile.Read(ref _released) != 0;

    /// <summary>释放名额；重复调用无副作用。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _limiter.Release(_remoteAddress);
        }
    }
}
