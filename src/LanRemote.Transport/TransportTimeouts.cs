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
/// <para><b>时限分三类，不要混淆</b>：</para>
/// <list type="bullet">
/// <item><description><b>顺序分段</b>（各自绝对、各自重新计时；前段用时不计入后段，
/// 但多段顺序执行时<b>总量可以加和</b>）：<see cref="ConnectTimeout"/> /
/// <see cref="HandshakeTimeout"/> / <see cref="LengthPrefixTimeout"/> /
/// <see cref="PayloadTimeout"/>；</description></item>
/// <item><description><b>客户端写预算</b>：<see cref="HelloTimeout"/>——客户端（验收器）写 hello 帧时
/// 消费；<b>服务端 pre-auth 不消费本值</b>，它<b>不是</b>服务端的第三个顺序段；</description></item>
/// <item><description><b>外层信封</b>：<see cref="PreAuthEnvelopeTimeout"/>——跨分段的<b>总量硬上限</b>。
/// 「分段各自绝对」不蕴含「总量有界」（5 s + 10 s 顺序执行即可加和，见 HANDOFF §17 教训 #25）；
/// 信封把这类可加和的等待整体封顶，且<b>永不被子阶段的进展重置</b>。</description></item>
/// </list>
/// <para><b>这里的数值是初始值，不是实测结论</b>：M3 步骤 15 只实测过「时限执行得准不准」，
/// 没有量过「数值本身合不合适」；信封 8 s 为第二轮评审建议的 provisional 值。
/// 全部数值待数值实验后一次性定案，并把实测值写进 <c>HANDOFF.md</c>。
/// 不要把它当成已被验证的数字。</para>
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
    /// <param name="helloTimeout">客户端写 hello 帧的预算（服务端不消费，见类型说明）。</param>
    /// <param name="preAuthEnvelopeTimeout">pre-auth 外层信封：自进入 pre-auth 起算的总量硬上限。</param>
    /// <exception cref="ArgumentOutOfRangeException">任一项不在 <see cref="Minimum"/>..<see cref="Maximum"/> 内。</exception>
    public TransportTimeouts(
        TimeSpan connectTimeout,
        TimeSpan handshakeTimeout,
        TimeSpan lengthPrefixTimeout,
        TimeSpan payloadTimeout,
        TimeSpan helloTimeout,
        TimeSpan preAuthEnvelopeTimeout)
    {
        Validate(connectTimeout, nameof(connectTimeout));
        Validate(handshakeTimeout, nameof(handshakeTimeout));
        Validate(lengthPrefixTimeout, nameof(lengthPrefixTimeout));
        Validate(payloadTimeout, nameof(payloadTimeout));
        Validate(helloTimeout, nameof(helloTimeout));
        Validate(preAuthEnvelopeTimeout, nameof(preAuthEnvelopeTimeout));

        ConnectTimeout = connectTimeout;
        HandshakeTimeout = handshakeTimeout;
        LengthPrefixTimeout = lengthPrefixTimeout;
        PayloadTimeout = payloadTimeout;
        HelloTimeout = helloTimeout;
        PreAuthEnvelopeTimeout = preAuthEnvelopeTimeout;
    }

    /// <summary>TCP 连接建立的绝对时限。</summary>
    public TimeSpan ConnectTimeout { get; }

    /// <summary>TLS 握手的绝对时限。</summary>
    public TimeSpan HandshakeTimeout { get; }

    /// <summary>读到完整 4 字节长度前缀的绝对时限。</summary>
    public TimeSpan LengthPrefixTimeout { get; }

    /// <summary>读到整帧 payload 的绝对时限。</summary>
    public TimeSpan PayloadTimeout { get; }

    /// <summary>
    /// 客户端写 hello 帧的预算。
    /// </summary>
    /// <remarks>
    /// <para><b>服务端 pre-auth 不消费本值</b>（2026-09-21 评审核对后核实）：服务端的读取由
    /// <see cref="LengthPrefixTimeout"/> + <see cref="PayloadTimeout"/> 两段 +
    /// <see cref="PreAuthEnvelopeTimeout"/> 信封约束。全仓唯一实质消费点是验收器客户端的
    /// hello 写入（<c>ClientRole.cs</c>）。</para>
    /// <para>此前的注释「首个 hello 帧到达的绝对时限」暗示服务端存在独立顺序段，是<b>坐实过的表述事故</b>
    /// （HANDOFF §17 教训 #24）——不要再把它写回服务端语义。</para>
    /// </remarks>
    public TimeSpan HelloTimeout { get; }

    /// <summary>
    /// pre-auth 外层信封：自进入 pre-auth 起算的总量硬上限，<b>永不重置</b>。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么需要它</b>：分段时限各自绝对 <b>≠</b> 总量有界——前缀 5 s 与 payload 10 s
    /// 顺序执行即可加和（15 s），8 个准入槽循环占用依旧可行。信封把这「多段顺序等待」的整体封顶。</para>
    /// <para>约束范围：覆盖长度前缀 + payload 的读取与校验（对端可控的等待）。hello 读取完成后的收尾
    /// 是本机非阻塞的 best-effort 写（<c>SslStream.ShutdownAsync</c> 在 .NET 10 上无 token 重载，
    /// 已核实），不在信封的可中断范围内。</para>
    /// <para>实现见 <see cref="ControlPreAuthSession.RunAsync"/>；初值 8 s（provisional）。</para>
    /// </remarks>
    public TimeSpan PreAuthEnvelopeTimeout { get; }

    /// <summary>
    /// 默认预算（初始值，待数值实验后定案）。
    /// </summary>
    public static TransportTimeouts Default { get; } = new(
        connectTimeout: TimeSpan.FromSeconds(3),
        handshakeTimeout: TimeSpan.FromSeconds(5),
        lengthPrefixTimeout: TimeSpan.FromSeconds(5),
        payloadTimeout: TimeSpan.FromSeconds(10),
        helloTimeout: TimeSpan.FromSeconds(5),
        preAuthEnvelopeTimeout: TimeSpan.FromSeconds(8));

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
