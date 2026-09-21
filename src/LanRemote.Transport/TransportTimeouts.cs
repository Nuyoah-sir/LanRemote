namespace LanRemote.Transport;

/// <summary>
/// 传输层的<b>绝对</b>时限预算。
/// </summary>
/// <remarks>
/// <para><b>外部红队评审 A 桶第 8 条</b>：每一段必须是「从本段起点算起的绝对 deadline」，
/// 不是「距上次读到字节 N 秒」的滑动窗口。滑动窗口会被「每 <c>timeout - ε</c> 发一个字节」
/// 的低速攻击无限续命；绝对 deadline 下同一攻击仍会在原时刻被切断。</para>
/// <para>因此：不要用 <c>Stream.ReadTimeout</c> 来做网络时限——它只覆盖同步读，
/// 异步读不受它约束。这里的每一段都用一个独立的
/// <see cref="CancellationTokenSource"/> 加 <c>CancelAfter</c> 实现。</para>
/// <para>五个阶段各自独立、各自重新计时：TCP 连接 / TLS 握手 / 4 字节长度前缀 /
/// 整帧 payload / 首个 hello。前一个阶段用掉的时间不应计入后一个阶段。</para>
/// <para><b>这里的数值是初始值，不是实测结论</b>：M3 步骤 15 要在本机实测取消延迟后
/// 再回来调整，并把实测值写进 <c>HANDOFF.md</c>。不要把它当成已被验证的数字。</para>
/// </remarks>
public sealed class TransportTimeouts
{
    /// <summary>允许的最小值：低于它就不是「超时」而是「误杀」。</summary>
    public static readonly TimeSpan Minimum = TimeSpan.FromMilliseconds(50);

    /// <summary>允许的最大值：超过它说明调用方大概率写错了单位。</summary>
    public static readonly TimeSpan Maximum = TimeSpan.FromDays(1);

    /// <summary>
    /// 构造时限预算。
    /// </summary>
    /// <param name="connectTimeout">TCP 连接建立的绝对时限。</param>
    /// <param name="handshakeTimeout">TLS 握手的绝对时限。</param>
    /// <param name="lengthPrefixTimeout">读到完整 4 字节长度前缀的绝对时限。</param>
    /// <param name="payloadTimeout">读到整帧 payload 的绝对时限。</param>
    /// <param name="helloTimeout">首个 hello 帧到达的绝对时限。</param>
    /// <exception cref="ArgumentOutOfRangeException">任一项不在 <see cref="Minimum"/>..<see cref="Maximum"/> 内。</exception>
    public TransportTimeouts(
        TimeSpan connectTimeout,
        TimeSpan handshakeTimeout,
        TimeSpan lengthPrefixTimeout,
        TimeSpan payloadTimeout,
        TimeSpan helloTimeout)
    {
        Validate(connectTimeout, nameof(connectTimeout));
        Validate(handshakeTimeout, nameof(handshakeTimeout));
        Validate(lengthPrefixTimeout, nameof(lengthPrefixTimeout));
        Validate(payloadTimeout, nameof(payloadTimeout));
        Validate(helloTimeout, nameof(helloTimeout));

        ConnectTimeout = connectTimeout;
        HandshakeTimeout = handshakeTimeout;
        LengthPrefixTimeout = lengthPrefixTimeout;
        PayloadTimeout = payloadTimeout;
        HelloTimeout = helloTimeout;
    }

    /// <summary>TCP 连接建立的绝对时限。</summary>
    public TimeSpan ConnectTimeout { get; }

    /// <summary>TLS 握手的绝对时限。</summary>
    public TimeSpan HandshakeTimeout { get; }

    /// <summary>读到完整 4 字节长度前缀的绝对时限。</summary>
    public TimeSpan LengthPrefixTimeout { get; }

    /// <summary>读到整帧 payload 的绝对时限。</summary>
    public TimeSpan PayloadTimeout { get; }

    /// <summary>首个 hello 帧到达的绝对时限。</summary>
    public TimeSpan HelloTimeout { get; }

    /// <summary>
    /// 默认预算（初始值，待步骤 15 实测后调整）。
    /// </summary>
    public static TransportTimeouts Default { get; } = new(
        connectTimeout: TimeSpan.FromSeconds(3),
        handshakeTimeout: TimeSpan.FromSeconds(5),
        lengthPrefixTimeout: TimeSpan.FromSeconds(5),
        payloadTimeout: TimeSpan.FromSeconds(10),
        helloTimeout: TimeSpan.FromSeconds(5));

    private static void Validate(TimeSpan value, string parameterName)
    {
        if (value < Minimum || value > Maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"时限必须落在 [{Minimum}, {Maximum}] 之间。");
        }
    }
}
