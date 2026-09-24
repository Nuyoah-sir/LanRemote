using System.Text;

namespace LanRemote.Acceptance;

/// <summary>
/// 无界面执行的调度：解析出来的命令 → 角色实现 → 退出码与日志路径。
/// </summary>
/// <remarks>
/// <para><b>输出去向</b>：本进程是 <c>WinExe</c>，正常双击时没有控制台。
/// 但被脚本拉起时（<c>exe --headless … &gt; out.txt</c>）标准句柄是继承的，
/// <c>Console.Out</c> 能工作。所以这里<b>两处都写</b>：日志文件是权威证据，
/// 控制台是给人/脚本即时看的。控制台写失败一律吞掉，绝不能因为打印不出来而让验收失败。</para>
/// </remarks>
internal static class HeadlessRunner
{
    /// <summary>执行命令并返回退出码。</summary>
    public static async Task<int> RunAsync(HeadlessCommand command)
    {
        // 显式挡在门口。没有它，command 为 null 时会在下面第 21 行抛一个裸 NRE，
        // 被 App 的顶层兜底吞成「验收器故障 3」，看起来像程序坏了而不是参数写错了。
        ArgumentNullException.ThrowIfNull(command);

        ConfigureConsoleEncoding();

        // 即使绕过解析器直接构造旧命令，也必须在创建日志或角色之前拒绝。
        if (command.Role is not (HeadlessCommand.RoleInfo or HeadlessCommand.RoleHost or HeadlessCommand.RoleClient)
            || command.LabRole is not null || command.LogFile is not null)
        {
            WriteUsage(LabSetupRole.DisabledMessage);
            return (int)AcceptanceOutcome.HarnessError;
        }

        AcceptanceRun run = AcceptanceRun.Create(command.LogDirectory, command.Role);

        run.Log.LineWritten += WriteToConsole;

        try
        {
            AcceptanceOutcome outcome = command.Role switch
            {
                HeadlessCommand.RoleInfo => await RunInfoAsync(run),

                HeadlessCommand.RoleHost => await HostRole.RunAsync(
                    run, command.Seconds, CancellationToken.None),

                HeadlessCommand.RoleClient => await RunClientAsync(run, command),

                _ => throw new InvalidOperationException($"角色 {command.Role} 未实现。"),
            };

            // 退出码 = 枚举值。日志路径单独印在最后一行，方便脚本取。
            run.Log.WriteLine($"[RUN] logFile      = {run.Log.FilePath ?? "(未落盘)"}");
            return (int)outcome;
        }
        catch (Exception ex)
        {
            // 走到这里说明连角色实现都没兜住——验收器自身的故障，不是被测对象的问题。
            run.ReportBackgroundFault("HeadlessRunner", ex);
            run.Log.WriteLine("[RESULT] 顶层未处理异常，本轮无效。");
            AcceptanceRun.Finish(run, AcceptanceOutcome.HarnessError, "HeadlessRunner 顶层异常。");
            return (int)AcceptanceOutcome.HarnessError;
        }
    }

    private static async Task<AcceptanceOutcome> RunInfoAsync(AcceptanceRun run)
    {
        InfoRole.InfoResult info = await InfoRole.RunAsync(run, CancellationToken.None);

        AcceptanceOutcome settled = run.Settle(info.Ready
            ? AcceptanceOutcome.Pass
            : AcceptanceOutcome.PreconditionUnmet);

        AcceptanceRun.Finish(run, settled, InfoRole.LocalCheckScope);
        return settled;
    }

    private static async Task<AcceptanceOutcome> RunClientAsync(AcceptanceRun run, HeadlessCommand command)
    {
        // RunAllAsync 内部已经结算过。直连（--address/--pin）与发现两条路都走它，
        // 免得「脚本跑的是另一套逻辑」。
        AcceptanceOutcome outcome = await ClientRole.RunAllAsync(
            run,
            command.Scenarios,
            command.Peer,
            command.Address,
            command.Pin,
            command.Port,
            CancellationToken.None);

        AcceptanceRun.Finish(run, outcome, $"控制端跑了 {command.Scenarios.Count} 个场景。");
        return outcome;
    }

    private static void WriteToConsole(string line)
    {
        try
        {
            Console.Out.WriteLine(line);
        }
        catch (Exception)
        {
            // 没有控制台就算了——日志文件才是权威证据。
        }
    }

    /// <summary>
    /// 输出被重定向时改用 UTF-8。
    /// </summary>
    /// <remarks>
    /// <para><b>本机实测过这个坑</b>：<c>Console.Out</c> 的编码取自控制台代码页（本机 936/GBK），
    /// 于是同一行中文<b>日志文件里是干净的 UTF-8、控制台里却是 GBK 字节</b>，
    /// 被脚本或 CI 按 UTF-8 读就成了乱码。证据本身没坏，但读的人会以为坏了，
    /// 更糟的是可能看错一个数字。</para>
    /// <para>只在<b>重定向</b>时改：真控制台下保持系统代码页，
    /// 否则在中文 cmd.exe 里反而变成乱码——把问题换个方向而已。</para>
    /// </remarks>
    private static void ConfigureConsoleEncoding()
    {
        try
        {
            if (Console.IsOutputRedirected)
            {
                Console.OutputEncoding = new UTF8Encoding(false);
            }
        }
        catch (Exception)
        {
            // 没有真控制台时设置编码可能抛；这不影响验收。
        }
    }

    /// <summary>参数错误时的提示（写控制台 + stderr，不落盘）。</summary>
    /// <remarks>
    /// <para><b>必须两边都写、并且必须 flush</b>。这里是「用户已经出错」的路径，
    /// 再静默就是双重失败——本机实测过一次：参数错误退出码 3、stdout/stderr 全 0 字节，
    /// 使用者根本无从知道错在哪。</para>
    /// <para>落盘是刻意不做的：参数都没解析成功，此刻还没有 runId 与日志文件，
    /// 硬写一个「半截日志」只会污染证据目录。</para>
    /// </remarks>
    public static void WriteUsage(string? error)
    {
        StringBuilder builder = new();

        if (!string.IsNullOrWhiteSpace(error))
        {
            builder.AppendLine("参数错误：" + error);
            builder.AppendLine();
        }

        builder.AppendLine(HeadlessCommand.Usage);
        string text = builder.ToString();

        try
        {
            Console.Out.WriteLine(text);
            Console.Out.Flush();
        }
        catch (Exception)
        {
            // 见类型说明。
        }

        try
        {
            Console.Error.WriteLine(text);
            Console.Error.Flush();
        }
        catch (Exception)
        {
        }
    }
}
