using System.Diagnostics;
using System.IO;
using System.Text;

namespace LanRemote.Acceptance;

/// <summary>
/// 提升实例：把「准备本机 lab 网段」这件事做掉，然后<b>用状态核验结果</b>。
/// </summary>
/// <remarks>
/// <para><b>它为什么必须是一个独立动词</b>：ADR-035 定了主进程永不提权，
/// 提权只发生在窄域 helper 里，而 helper 的动词集是固定的。
/// <c>lab-apply</c> / <c>lab-undo</c> 就是这两个动词，参数只有「A 还是 B」和日志落点。</para>
/// <para><b>它自己不写网络配置</b>：真正动手的是包里的 <c>set-lab-ip.ps1</c>——
/// 那份脚本已经在实机跑通过，而且<b>是同一台机器上唯一一份</b>改网络的实现。
/// 若在这里用 C# 再写一份，两条路径迟早会分叉，「手动跑好的」和「一键跑好的」
/// 就不再是同一件事。所以这里的职责是：<b>校验前置条件 → 跑脚本 → 核验结果</b>。</para>
/// <para><b>核验看状态，不看退出码</b>：脚本退出码 0 只说明它自己觉得做完了；
/// 「本机到底有没有合格地址」要由网卡枚举说了算。两者不一致时记 WARN 并留痕，
/// 不静默取信任何一个。</para>
/// <para><b>三层防线</b>（ADR-035）：① 不是管理员就什么都不做；
/// ② 脚本必须解析到<b>本程序自己的目录内</b>、且不是符号链接/联接点（TOCTOU 重校验）；
/// ③ 跑完用状态核验，脚本报成功但状态没到位就记失败。</para>
/// </remarks>
internal static class LabWorkerRole
{
    /// <summary>脚本本身的预算。脚本里最坏情形要重试 5 次配置文件（每次 3 秒）。</summary>
    private static readonly TimeSpan ScriptBudget = TimeSpan.FromMinutes(3);

    /// <summary>改完地址之后等网卡枚举反映出来。</summary>
    private static readonly TimeSpan VerifyBudget = TimeSpan.FromSeconds(25);

    /// <summary>本进程是否以管理员身份运行。</summary>
    /// <remarks>
    /// 用 <see cref="Environment.IsPrivilegedProcess"/> 而不是
    /// <c>WindowsPrincipal.IsInRole</c>：前者就是「当前进程是否被提权」的语义，
    /// 不需要额外程序集；后者回答的是「这个身份是否属于管理员组」，
    /// 在「管理员组的用户但以标准令牌运行」时是 <see langword="true"/>——
    /// 而那恰恰是最需要被拒的一种情形。
    /// </remarks>
    public static bool IsElevated() => Environment.IsPrivilegedProcess;

    /// <summary>跑一个提升动词并给出结局。</summary>
    public static async Task<AcceptanceOutcome> RunAsync(
        AcceptanceRun run,
        LabAction action,
        CancellationToken cancellationToken)
    {
        AcceptanceLog log = run.Log;

        run.WriteHeader(timeouts: null);

        log.WriteLine($"[LAB] action     = {action.Verb()} ({action.Describe()})");
        log.WriteLine($"[LAB] labAddress = {action.Address() ?? "(撤销：不写入任何地址)"}");
        log.WriteLine($"[LAB] elevated   = {(IsElevated() ? "yes" : "no")}");
        log.WriteLine($"[LAB] scriptLog  = {LabSetupRole.ScriptLogPath}");

        if (!IsElevated())
        {
            // 这不是错误输入，是「有人在没提权的 shell 里手敲了这个动词」。
            // 什么都不做地说清楚，比尝试半途提权（不可能）或报一个含混的失败要好。
            log.WriteLine(
                "[LAB][RESULT] outcome=UNMET reason=not-elevated // 修改网络配置必须在管理员权限下进行；" +
                "当前进程不是管理员，本机没有发生任何更改。请回到窗口点「准备为 A 机 / B 机」。");

            return Finish(run, AcceptanceOutcome.PreconditionUnmet, "提升实例未提权，什么都没做。");
        }

        if (!TryResolveScript(out string scriptPath, out string scriptReason))
        {
            log.WriteLine("[LAB][RESULT] outcome=HARNESS_ERROR reason=lab-script-unusable // " + scriptReason);
            return Finish(run, AcceptanceOutcome.HarnessError, scriptReason);
        }

        log.WriteLine($"[LAB] script     = {scriptPath} ({new FileInfo(scriptPath).Length} bytes)");
        log.WriteLine("[LAB] psOutEnc   = utf-8（脚本在管道模式下说 UTF-8；见 set-lab-ip.ps1 头部 v4 说明）");

        long scriptLogOffset = ScriptLogOffset();

        (int exitCode, bool timedOut, string? fatal) = await RunScriptAsync(
            run, scriptPath, action, cancellationToken).ConfigureAwait(false);

        LogScriptDelta(run, scriptLogOffset);

        if (fatal is not null)
        {
            log.WriteLine("[LAB][RESULT] outcome=HARNESS_ERROR reason=script-not-runnable // " + fatal);
            return Finish(run, AcceptanceOutcome.HarnessError, fatal);
        }

        (bool satisfied, string detail) = await LabSetupRole.WaitForStateAsync(
            action, VerifyBudget, cancellationToken).ConfigureAwait(false);

        log.WriteLine($"[LAB] scriptExit = {exitCode}{(timedOut ? "（超时，已被本程序中止）" : string.Empty)}");
        log.WriteLine($"[LAB] stateCheck = {detail}");

        AcceptanceOutcome outcome = satisfied ? AcceptanceOutcome.Pass : AcceptanceOutcome.Fail;

        // 退出码与状态不一致时，两个事实都写下来。这里刻意不「取一个信一个」：
        // 状态没到位却报成功，说明脚本在骗人；状态到位而退出码非 0，
        // 说明脚本的返回值不可信——两种都得有人看见。
        if (satisfied && exitCode != 0)
        {
            log.WriteLine(
                "[LAB][WARN] 脚本退出码不是 0，但本机状态确实达成了。以状态为准记 PASS，退出码留在这里备查。");

            if (timedOut)
            {
                log.WriteLine("[LAB][WARN] 同上，但脚本是被超时中止的：它可能在大功告成之后卡在收尾。");
            }
        }
        else if (!satisfied && exitCode == 0)
        {
            log.WriteLine(
                "[LAB][WARN] 脚本自称成功（退出码 0）但状态没达成。以状态为准记 FAIL——" +
                "退出码是脚本的自我评价，网卡枚举才是事实。");
        }

        if (!satisfied && timedOut)
        {
            log.WriteLine(
                "[LAB][WARN] 脚本被超时中止，网卡可能停在「一半配好」的状态。" +
                "再跑一次这个动作是安全的（脚本幂等）；要退回原状就点「撤销准备」。");
        }

        return Finish(run, outcome, $"{action.Describe()}：{detail}");
    }

    /// <summary>
    /// 解析包内的 <c>set-lab-ip.ps1</c>，并做 TOCTOU 重校验。
    /// </summary>
    /// <remarks>
    /// 只在<b>本 exe 自己的目录</b>里找，绝不接受调用方给的路径——
    /// 提权实例执行什么，不能由外面的参数决定。符号链接 / 联接点一律拒绝：
    /// 一个指到别处的 <c>set-lab-ip.ps1</c> 在提权下跑，等于借我们的手执行任意脚本。
    /// （这条不是理论洁癖：解压目录本身可写，链接是最省的替换手法。）
    /// </remarks>
    public static bool TryResolveScript(out string path, out string reason)
    {
        string baseDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        string candidate = Path.GetFullPath(Path.Combine(baseDirectory, LabSetupRole.ScriptFileName));

        if (!candidate.StartsWith(baseDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            path = string.Empty;
            reason = $"{LabSetupRole.ScriptFileName} 解析后跑到 exe 目录外面去了（{candidate}），拒绝执行。";
            return false;
        }

        if (!File.Exists(candidate))
        {
            path = string.Empty;
            reason = $"这个包不完整：找不到 {LabSetupRole.ScriptFileName}（它应该和 LanRemote.Acceptance.exe 同目录）。";
            return false;
        }

        if (File.GetAttributes(candidate).HasFlag(FileAttributes.ReparsePoint))
        {
            path = string.Empty;
            reason = $"{LabSetupRole.ScriptFileName} 是符号链接 / 联接点，拒绝在管理员权限下执行它。";
            return false;
        }

        path = candidate;
        reason = string.Empty;
        return true;
    }

    /// <summary>跑脚本，输出实时转进验收日志。</summary>
    private static async Task<(int ExitCode, bool TimedOut, string? Fatal)> RunScriptAsync(
        AcceptanceRun run,
        string scriptPath,
        LabAction action,
        CancellationToken cancellationToken)
    {
        AcceptanceLog log = run.Log;

        string powershell = Path.Combine(
            Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

        if (!File.Exists(powershell))
        {
            log.WriteLine($"[LAB][WARN] 找不到 Windows PowerShell：{powershell}");
            return (-1, false, $"找不到 Windows PowerShell（{powershell}），脚本没法跑。");
        }

        List<string> arguments =
        [
            "-NoProfile",          // 别人的 profile 不该影响验收机器的网络配置
            "-NonInteractive",     // 卡在一个没人能点的提示上是最坏的结果
            "-ExecutionPolicy", "Bypass", // 解压出来的包带 MOTW，Default/RemoteSigned 会直接拒绝
            "-File", scriptPath,
        ];

        string? labRole = action.LabRole();

        if (labRole is null)
        {
            arguments.Add("-Undo");
        }
        else
        {
            arguments.Add("-Role");
            arguments.Add(labRole);
        }

        ProcessStartInfo startInfo = new(powershell)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
            StandardOutputEncoding = PipeTextEncoding,
            StandardErrorEncoding = PipeTextEncoding,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        log.WriteLine("[LAB] 命令       = powershell.exe " + string.Join(' ', arguments));

        using Process process = new() { StartInfo = startInfo };

        // 解不开的字节（U+FFFD）是「编码协议没对上」的唯一客观证据：
        // 脚本说 UTF-8、我们按 UTF-8 解，正常路径下永远不该出现。
        // 真出现了就记下来——那时候该怀疑的是「跑的是不是同一个版本的脚本」。
        bool sawUndecodable = false;

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                sawUndecodable |= args.Data.Contains('\uFFFD', StringComparison.Ordinal);
                log.WriteLine("[LAB][PS]     " + args.Data);
            }
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                sawUndecodable |= args.Data.Contains('\uFFFD', StringComparison.Ordinal);
                log.WriteLine("[LAB][PS-ERR] " + args.Data);
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            log.WriteLine("[LAB][WARN] 起不了 PowerShell：" + ex.Message);
            return (-1, false, "起不了 PowerShell：" + ex.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(ScriptBudget);

        try
        {
            await process.WaitForExitAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // 杀不掉也得往下走：日志里写清楚它在超时那一刻还活着。
            }

            return (-1, true, null);
        }

        // 无参 WaitForExit 会把两个异步读取器里剩下的行走完，
        // 否则末尾几行（往往正是结论）会在进程退出后丢掉。
        process.WaitForExit();

        if (sawUndecodable)
        {
            log.WriteLine(
                "[LAB][WARN] 子进程输出里有解不开的字节（U+FFFD）。脚本本应在管道模式下说 UTF-8——" +
                "出现这个说明包里的 set-lab-ip.ps1 可能不是配套版本。中文以 [LAB][SCRIPT]（UTF-8 文件）为准。");
        }

        return (process.ExitCode, false, null);
    }

    /// <summary>脚本自己那份日志（<c>%TEMP%\lanremote-lab-ip.log</c>，UTF-8）本次新增的部分。</summary>
    /// <remarks>
    /// 双份是有意的：控制台那份走的是控制台代码页（中文系统是 936，可能是乱码），
    /// 而脚本写文件时用的是 UTF-8。有这一份，「证据坏了」这件事就不会发生。
    /// </remarks>
    private static void LogScriptDelta(AcceptanceRun run, long offset)
    {
        string path = LabSetupRole.ScriptLogPath;

        try
        {
            if (!File.Exists(path))
            {
                run.Log.WriteLine("[LAB][WARN] 脚本日志不存在（脚本可能在写第一行之前就失败了）。");
                return;
            }

            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = offset <= stream.Length ? offset : 0;
            stream.Seek(start, SeekOrigin.Begin);

            using StreamReader reader = new(stream, new UTF8Encoding(false));
            string text = reader.ReadToEnd().TrimStart('\uFEFF');

            if (text.Trim().Length == 0)
            {
                run.Log.WriteLine("[LAB][WARN] 脚本日志本次没有新增内容。");
                return;
            }

            run.Log.WriteLine("-------- 脚本自己的日志（本次新增，UTF-8）--------");

            foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
            {
                run.Log.WriteLine("[LAB][SCRIPT] " + line.TrimEnd());
            }

            run.Log.WriteLine("--------");
        }
        catch (Exception ex)
        {
            run.Log.WriteLine("[LAB][WARN] 读不到脚本日志：" + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static long ScriptLogOffset()
    {
        try
        {
            string path = LabSetupRole.ScriptLogPath;
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// 子进程输出的解码方式：固定 UTF-8，不是「猜」出来的。
    /// </summary>
    /// <remarks>
    /// <para>子进程就是我们自己那份 <c>set-lab-ip.ps1</c>，它<b>在 stdout 是管道时会把自己的输出
    /// 切到 UTF-8</b>（脚本头部「console output encoding (v4)」，实测：不这么做的 Windows
    /// PowerShell 5.1 会按控制台代码页 936 输出，中文全是乱码）。所以这里按 UTF-8 解，
    /// 是遵守约定，不是碰运气。</para>
    /// <para><b>为什么不能「按控制台代码页解」</b>：那正是第一版写法——
    /// <c>Encoding.GetEncoding(GetOEMCP())</c>。.NET 10 没有内置的 CP936 解码器
    /// （需要 <c>System.Text.Encoding.CodePages</c> 包，而本项目不引它），
    /// 于是 <c>GetEncoding(936)</c> 抛异常 → 落回 UTF-8 兜底 → <c>[LAB][PS]</c> 行里
    /// 中文变成 U+FFFD。实测证据：<c>以太网</c> 的 GBK 字节 <c>D2 D4 CC AB CD F8</c>
    /// 被按 UTF-8 解出来是「两个替换字符 + U+032B 组合符 + 替换字符」——
    /// ASCII 和数字全都正常，只有词坏了，所以它看起来像「日志坏了」而不是「解错了」。</para>
    /// <para>如果哪天包里的脚本没跟着换，<see cref="RunScriptAsync"/> 会因 U+FFFD 记一条 WARN，
    /// 不会静默地把乱码当正常输出。</para>
    /// </remarks>
    private static readonly UTF8Encoding PipeTextEncoding = new(encoderShouldEmitUTF8Identifier: false);

    private static AcceptanceOutcome Finish(AcceptanceRun run, AcceptanceOutcome outcome, string detail)
    {
        AcceptanceOutcome settled = run.Settle(outcome);
        AcceptanceRun.Finish(run, settled, detail);
        return settled;
    }
}
