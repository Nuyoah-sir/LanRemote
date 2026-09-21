namespace LanRemote.Acceptance;

/// <summary>
/// 验收结局分类。<b>枚举值就是进程退出码</b>，别再另立一套映射。
/// </summary>
/// <remarks>
/// <para><b>为什么必须有 <see cref="HarnessError"/> 与 <see cref="InvalidRun"/>。</b>
/// 原来的分类只有「符合预期 / 真失败 / 前置条件不满足」三种，
/// 于是「测量仪器自己坏了」和「被测对象没通过」被压进同一个桶：
/// 验收器自身抛异常、后台任务静默死掉、操作员中途点了中止，
/// 都会让某些场景看起来「没通过」——但那是<b>没测成</b>，不是<b>测出问题</b>。
/// 把这两类混为一谈，结论就变成了「M3 有缺陷」，方向直接错。</para>
/// <para><see cref="InvalidRun"/> 尤其重要：操作员点中止会让 socket 被强行关闭，
/// 而「连接被关闭」正是多个场景的 PASS 条件，不毒化整轮就等于用中止动作伪造 PASS。</para>
/// </remarks>
public enum AcceptanceOutcome
{
    /// <summary>场景表现与预期一致。</summary>
    Pass = 0,

    /// <summary>测量有效，被测行为不符合预期。</summary>
    Fail = 1,

    /// <summary>环境没配好（对端没开、网卡不合格、发现不到对端），什么都没测到。</summary>
    PreconditionUnmet = 2,

    /// <summary>验收器自身故障（未处理异常、后台任务 faulted、日志写不进去）。</summary>
    HarnessError = 3,

    /// <summary>操作员中止 / 运行被外部打断——本轮证据整段作废。</summary>
    InvalidRun = 4,
}

/// <summary>
/// <see cref="AcceptanceOutcome"/> 的呈现helper。
/// </summary>
internal static class AcceptanceOutcomeText
{
    /// <summary>中文短语，用于日志与 UI。</summary>
    public static string Describe(this AcceptanceOutcome outcome) => outcome switch
    {
        AcceptanceOutcome.Pass => "符合预期",
        AcceptanceOutcome.Fail => "不符合预期",
        AcceptanceOutcome.PreconditionUnmet => "前置条件不满足",
        AcceptanceOutcome.HarnessError => "验收器故障（本轮无效）",
        AcceptanceOutcome.InvalidRun => "运行已作废（操作员中止）",
        _ => "未知",
    };

    /// <summary>机器可读的结局名，写进 <c>[RESULT]</c> 行。</summary>
    public static string Code(this AcceptanceOutcome outcome) => outcome switch
    {
        AcceptanceOutcome.Pass => "PASS",
        AcceptanceOutcome.Fail => "FAIL",
        AcceptanceOutcome.PreconditionUnmet => "UNMET",
        AcceptanceOutcome.HarnessError => "HARNESS_ERROR",
        AcceptanceOutcome.InvalidRun => "INVALID_RUN",
        _ => "UNKNOWN",
    };

    /// <summary>把若干结局合并成整轮结局：越靠后越优先。</summary>
    /// <remarks>
    /// 优先级 <c>InvalidRun &gt; HarnessError &gt; Fail &gt; PreconditionUnmet &gt; Pass</c>。
    /// 只要有一条作废/故障，整轮就不是 PASS——不允许用「另外两条通过了」把无效运行洗回来。
    /// </remarks>
    public static AcceptanceOutcome Combine(this IEnumerable<AcceptanceOutcome> outcomes)
    {
        AcceptanceOutcome worst = AcceptanceOutcome.Pass;

        foreach (AcceptanceOutcome outcome in outcomes)
        {
            if (Rank(outcome) > Rank(worst))
            {
                worst = outcome;
            }
        }

        return worst;

        static int Rank(AcceptanceOutcome value) => value switch
        {
            AcceptanceOutcome.Pass => 0,
            AcceptanceOutcome.PreconditionUnmet => 1,
            AcceptanceOutcome.Fail => 2,
            AcceptanceOutcome.HarnessError => 3,
            AcceptanceOutcome.InvalidRun => 4,
            _ => 5,
        };
    }
}
