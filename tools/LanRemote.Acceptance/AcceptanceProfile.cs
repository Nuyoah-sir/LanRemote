using LanRemote.Transport;

namespace LanRemote.Acceptance;

/// <summary>
/// 两侧共用的时限参数与由此推出的判定窗口。
/// </summary>
/// <remarks>
/// <para><b>为什么把时限抽出来共用</b>：控制端的 PASS 判据里包含
/// 「对端在第几秒切断我」，这个期望值必须<b>由被控端真正使用的那个时限推出来</b>。
/// 早先版本是两边各写一份常量（host 里 <c>TimeSpan.FromSeconds(5)</c>、
/// client 里另一个魔数），改了一边另一边不会红——属于「看起来在测，其实在测自己的期望」。</para>
/// <para>所有窗口都写成相对 <see cref="TransportTimeouts.LengthPrefixTimeout"/> 的表达式，
/// 这样换时限时判据跟着走。</para>
/// </remarks>
internal static class AcceptanceProfile
{
    /// <summary>被控端实际使用的五个时限。</summary>
    public static TransportTimeouts Timeouts { get; } = new(
        connectTimeout: TimeSpan.FromSeconds(3),
        handshakeTimeout: TimeSpan.FromSeconds(5),
        lengthPrefixTimeout: TimeSpan.FromSeconds(5),
        payloadTimeout: TimeSpan.FromSeconds(10),
        helloTimeout: TimeSpan.FromSeconds(5));

    /// <summary>
    /// <c>success</c> 场景：对端必须在<b>明显早于</b>长度前缀时限时收尾。
    /// </summary>
    /// <remarks>
    /// 这是把「hello 被接受」和「对端只是等到了时限」区分开的<b>唯一客户端可观测判据</b>。
    /// hello 合法时服务端读到就走完流程（毫秒级）；若它根本没认我们的 hello，
    /// 收尾时刻必然落在时限附近。取时限 − 2s 留出余量。
    /// </remarks>
    public static TimeSpan SuccessCloseMax =>
        Timeouts.LengthPrefixTimeout - TimeSpan.FromSeconds(2);

    /// <summary><c>timeout</c> 场景：对端收尾时刻的允许下界（时限 − 2s）。</summary>
    public static TimeSpan TimeoutCloseMin =>
        Timeouts.LengthPrefixTimeout - TimeSpan.FromSeconds(2);

    /// <summary><c>timeout</c> 场景：允许的上界（时限 + 7s，容忍调度抖动）。</summary>
    public static TimeSpan TimeoutCloseMax =>
        Timeouts.LengthPrefixTimeout + TimeSpan.FromSeconds(7);

    /// <summary>慢滴场景里两个字节之间的间隔。</summary>
    public static TimeSpan DribbleInterval => TimeSpan.FromSeconds(2);

    /// <summary>
    /// 慢滴场景：允许的上界（时限 + 4s）。
    /// </summary>
    /// <remarks>
    /// 绝对时限下，对端会在第 5 秒左右切断（我们才发了 2~3 个字节），远早于此。
    /// 可重置的空闲时限下，4 个字节会在第 6 秒才发完，然后再等 10 秒 payload 时限，
    /// 收尾时刻是 16 秒左右——**超出这个上界，于是能被抓到**。这正是本场景存在的理由。
    /// </remarks>
    public static TimeSpan DribbleCloseMax =>
        Timeouts.LengthPrefixTimeout + TimeSpan.FromSeconds(4);

    /// <summary>单次读的预算：必须比「最坏合法情形」还长，否则区分不出「还挂着」。</summary>
    public static TimeSpan ReadBudget =>
        Timeouts.LengthPrefixTimeout + Timeouts.PayloadTimeout + TimeSpan.FromSeconds(5);
}
