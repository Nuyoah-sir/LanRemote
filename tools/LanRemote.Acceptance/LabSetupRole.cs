using System.Globalization;
using System.IO;
using System.Text;
using LanRemote.Discovery.Networking;

namespace LanRemote.Acceptance;

/// <summary>
/// 「一键准备本机」的编排者：<b>不提权的那个进程</b>请管理员权限、等它做完、再核验结果。
/// </summary>
/// <remarks>
/// <para><b>窗口按钮和命令行走的是同一条路</b>。窗口里的「准备为 A 机 / B 机 / 撤销准备」
/// 按钮调用的就是这里的 <see cref="PrepareAsync"/>；<c>--headless prepare-lab</c> 也调它。
/// 这与本工具一开始的约定一致：<b>不存在「脚本跑的是另一套逻辑」</b>。</para>
/// <para><b>为什么要有这个中间层</b>（而不是直接让窗口去拉提升实例）：
/// 「请权限 → 等结束 → 读回日志 → 核验状态」这四步本身就会出错，
/// 出错时的判别逻辑必须只有一份。放在窗口里就变成「界面代码里的判断」，
/// 而界面代码没法在命令行里复现——那种差异正是本工具最想避免的东西。</para>
/// <para><b>提权那一半</b>在 <see cref="LabWorkerRole"/>：真正的改动只发生在提升实例里，
/// 本进程从头到尾都是普通权限（ADR-035）。</para>
/// <para><b>结束时给的是「状态」而不是「退出码」</b>：子进程说自己成功不算数，
/// 本机到底有没有那个地址，由网卡枚举说了算。</para>
/// </remarks>
internal static class LabSetupRole
{
    /// <summary>包内那支脚本的文件名（与 exe 同目录）。</summary>
    public const string ScriptFileName = "set-lab-ip.ps1";

    /// <summary>A 机的 lab 地址。</summary>
    public const string AddressA = "192.168.1.10";

    /// <summary>B 机的 lab 地址。</summary>
    public const string AddressB = "192.168.1.20";

    /// <summary>脚本自己写的日志（UTF-8）。</summary>
    public static string ScriptLogPath =>
        Path.Combine(Path.GetTempPath(), "lanremote-lab-ip.log");

    /// <summary>两个 lab 地址，用于「在不在」这类判断。</summary>
    private static readonly string[] LabAddresses = [AddressA, AddressB];

    /// <summary>等提升实例结束的上限。</summary>
    private static readonly TimeSpan HandoffBudget = TimeSpan.FromMinutes(5);

    /// <summary>提升实例结束后，等状态反映出来的上限。</summary>
    private static readonly TimeSpan VerifyBudget = TimeSpan.FromSeconds(20);

    /// <summary>准备本机（或撤销准备）。</summary>
    public static async Task<AcceptanceOutcome> PrepareAsync(
        AcceptanceRun run,
        LabAction action,
        CancellationToken cancellationToken)
    {
        AcceptanceLog log = run.Log;

        run.WriteHeader(timeouts: null);

        log.WriteLine($"[LAB] 动作        = {action.Describe()}（{action.Verb()}）");
        log.WriteLine($"[LAB] 要写的地址   = {action.Address() ?? "(无 —— 撤销)"}");
        log.WriteLine(
            $"[LAB] 本进程提升   = {(LabWorkerRole.IsElevated() ? "是（不会再弹 UAC）" : "否（会弹 UAC，需要点「是」）")}");

        // 先把两样东西解析出来，再决定要不要弹 UAC。
        // 包不完整却先弹一个 UAC，是最招人烦的一种失败。
        if (!LabWorkerRole.TryResolveScript(out string scriptPath, out string scriptReason))
        {
            log.WriteLine("[LAB][RESULT] outcome=HARNESS_ERROR reason=lab-script-unusable // " + scriptReason);
            return Finish(run, AcceptanceOutcome.HarnessError, scriptReason);
        }

        if (!TryResolveSelfExe(out string exePath, out string exeReason))
        {
            log.WriteLine("[LAB][RESULT] outcome=HARNESS_ERROR reason=self-exe-not-found // " + exeReason);
            return Finish(run, AcceptanceOutcome.HarnessError, exeReason);
        }

        log.WriteLine($"[LAB] 提升实例     = {exePath}");
        log.WriteLine($"[LAB] 脚本         = {scriptPath}");

        // 提升实例的日志由本进程指定：它没有控制台，本进程拿不到它的 stdout，
        // 只能双方约定一个文件，然后把内容读回来并进本轮日志。
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string childLogPath = Path.Combine(
            AcceptanceLog.DefaultDirectory, $"m3-{action.Verb()}-{stamp}-{run.RunId[..4]}.log");

        List<string> arguments = ["--headless", action.Verb(), "--log-file", childLogPath];
        string? labRole = action.LabRole();

        if (labRole is not null)
        {
            arguments.Add("--lab-role");
            arguments.Add(labRole);
        }

        log.WriteLine($"[LAB] 提升实例日志 = {childLogPath}");
        log.WriteLine("[LAB] 请求管理员权限——若弹出 UAC，请点「是」（点「否」不会改动本机任何设置）。");

        // 边跑边把它写的行读回来，用户才能看见「它到底在干什么」。
        ChildLogTailer tailer = new(childLogPath);
        using CancellationTokenSource tailStop = new();
        Task tailing = tailer.RunAsync(log, tailStop.Token);

        ElevatedLauncher.LaunchResult launch;

        try
        {
            launch = await ElevatedLauncher.RunElevatedAsync(exePath, arguments, HandoffBudget)
                .ConfigureAwait(false);
        }
        finally
        {
            await tailStop.CancelAsync().ConfigureAwait(false);

            try
            {
                await tailing.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 尾随失败不影响结论：证据在它自己的文件里，下面还会整段读一次。
            }

            tailer.Flush(log, final: true);
        }

        // 转发对账：提升实例写了几行，本进程就必须转发出几行。
        // 这不是装饰——「按行数记账」的转发器曾经在文件正好以换行结尾那一刻越过下一行的行首，
        // 静默丢掉 7 行证据（见 ChildLogTailer.Flush 的注释）。丢证据是最不该发生的事，
        // 所以每次都对一次账；两边的数从两条互相独立的算法来，对不上就当场喊出来。
        int childLines = CountCompleteLines(childLogPath);

        if (childLines > 0)
        {
            bool inSync = tailer.Forwarded == childLines;

            log.WriteLine(
                $"[LAB] 转发对账     = 提升实例 {childLines} 行 / 已转发 {tailer.Forwarded} 行" +
                (inSync ? "（一致）" : " ← 不一致"));

            if (!inSync)
            {
                log.WriteLine(
                    "[LAB][WARN] 转发行数和提升实例的文件对不上，上面这几段可能缺行。" +
                    $"以提升实例自己那份为准：{childLogPath}");
            }
        }

        if (launch.Cancelled)
        {
            log.WriteLine("[LAB][RESULT] outcome=UNMET reason=uac-declined // " + launch.Error);
            log.WriteLine("[LAB] 本机没有任何更改。要参与验收，随时可以再点一次。");
            return Finish(run, AcceptanceOutcome.PreconditionUnmet, "UAC 未同意，未做任何更改。");
        }

        if (!launch.Started)
        {
            log.WriteLine("[LAB][RESULT] outcome=UNMET reason=elevation-failed // " + launch.Error);
            log.WriteLine(
                "[LAB] 提升实例没起来。常见原因：组策略禁止提权、安全软件拦截、" +
                "或本机不是管理员账户（那就得用管理员账户登录）。");
            return Finish(run, AcceptanceOutcome.PreconditionUnmet, "提权失败：" + launch.Error);
        }

        log.WriteLine($"[LAB] 提升实例退出码 = {launch.ExitCode}");

        if (launch.ExitCode < 0)
        {
            log.WriteLine("[LAB][RESULT] outcome=UNMET reason=handoff-incomplete // " + launch.Error);
            log.WriteLine(
                "[LAB] 注意：提升实例是独立进程，它可能仍在运行并把日志写下去；" +
                "过一会儿重新自检就能看到结果。");
            return Finish(run, AcceptanceOutcome.PreconditionUnmet, launch.Error ?? "没能等到提升实例结束。");
        }

        (bool satisfied, string detail) = await WaitForStateAsync(action, VerifyBudget, cancellationToken)
            .ConfigureAwait(false);

        log.WriteLine($"[LAB] 状态核验     = {detail}");

        AcceptanceOutcome outcome = satisfied ? AcceptanceOutcome.Pass : AcceptanceOutcome.Fail;

        log.WriteLine(satisfied
            ? "[LAB][RESULT] outcome=PASS // 本机已经准备好，可以参与两机验收了"
            : "[LAB][RESULT] outcome=FAIL // 状态没达成；上面几行是提升实例与脚本的原话");

        return Finish(run, outcome, $"{action.Describe()}：{detail}");
    }

    /// <summary>
    /// 等本机状态变成该动作期望的样子，并给出可读的核验说明。
    /// </summary>
    /// <remarks>
    /// 提升实例自己也会核验一次；这里再来一次是因为<b>两次核验的时点不同</b>：
    /// 提升实例核验的是「它做完那一刻」，本进程核验的是「现在还成立吗」——
    /// 两者之间隔着进程退出、别的软件抢地址、以及用户手动改回去的可能。
    /// </remarks>
    public static async Task<(bool Satisfied, string Detail)> WaitForStateAsync(
        LabAction action,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + budget;
        string? expected = action.Address();
        string detail = "(还没有取样)";

        while (true)
        {
            IReadOnlyList<string> current = QualifiedAddresses();
            detail = Describe(expected, current);

            bool satisfied = expected is null
                ? !current.Any(address => LabAddresses.Contains(address, StringComparer.Ordinal))
                : current.Contains(expected, StringComparer.Ordinal);

            if (satisfied || DateTimeOffset.UtcNow >= deadline)
            {
                return (satisfied, detail);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        static string Describe(string? expected, IReadOnlyList<string> addresses)
        {
            string now = addresses.Count == 0 ? "(无合格地址)" : string.Join(", ", addresses);

            if (expected is null)
            {
                bool stillThere = addresses.Any(address => LabAddresses.Contains(address, StringComparer.Ordinal));

                return stillThere
                    ? $"撤销未完成：仍然存在 lab 地址（当前 {now}）"
                    : $"已撤销：lab 地址不在了（当前 {now}）";
            }

            return addresses.Contains(expected, StringComparer.Ordinal)
                ? $"已就绪：{expected} 在监听地址里（当前 {now}）"
                : $"{expected} 还没有出现在合格地址里（当前 {now}）";
        }
    }

    /// <summary>当前被产品认可的（RFC1918）监听地址。</summary>
    /// <remarks>
    /// 每次都新建一个 provider：它在构造时做一次网卡快照，
    /// 拿旧实例重复问只会得到改地址之前的答案——那正是「点了按钮没反应」的经典成因。
    /// </remarks>
    private static IReadOnlyList<string> QualifiedAddresses()
    {
        try
        {
            LocalNetworkBindingProvider provider = new(new SystemNetworkInterfaceSource());

            return provider.GetBindings()
                .Select(binding => binding.Address.ToString())
                .ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// 文件里已经写完的行数——用<b>和转发器不同的算法</b>数一遍。
    /// </summary>
    /// <remarks>
    /// 转发器按 <c>Split('\n')</c> 的段数记账；这里直接数换行符：
    /// 每个换行收尾一行，末尾没换行的那一段也算一行（文件都已经写完了）。
    /// 两条算法必须独立，否则「对账」就变成自己证明自己。
    /// </remarks>
    private static int CountCompleteLines(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return 0;
            }

            string text;

            using (FileStream stream = new(
                       path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new(stream, new UTF8Encoding(false)))
            {
                text = reader.ReadToEnd();
            }

            if (text.Length == 0)
            {
                return 0;
            }

            int breaks = text.Count(character => character == '\n');
            return text.EndsWith('\n') ? breaks : breaks + 1;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>解析本程序自己的 exe。</summary>
    /// <remarks>
    /// <c>Environment.ProcessPath</c> 在发布出来的 apphost 下是本 exe，
    /// 但开发期用 <c>dotnet run</c> 时是 <c>dotnet.exe</c>——那种情况下拉起「它自己」
    /// 会把 dotnet 宿主当成我们的程序跑，参数全错。所以显式校验文件名，不匹配就退回
    /// exe 目录里找。
    /// </remarks>
    private static bool TryResolveSelfExe(out string path, out string reason)
    {
        string processPath = Environment.ProcessPath ?? string.Empty;

        if (processPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileNameWithoutExtension(processPath)
                .Equals("LanRemote.Acceptance", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(processPath))
        {
            path = processPath;
            reason = string.Empty;
            return true;
        }

        string beside = Path.Combine(AppContext.BaseDirectory, "LanRemote.Acceptance.exe");

        if (File.Exists(beside))
        {
            path = beside;
            reason = string.Empty;
            return true;
        }

        path = string.Empty;
        reason = "拿不到本程序自己的 exe（开发期用 dotnet run 就是这个样子）。" +
                 "一键准备需要以管理员身份再拉起同一个 exe，所以请在解压出来的包里用它。";
        return false;
    }

    private static AcceptanceOutcome Finish(AcceptanceRun run, AcceptanceOutcome outcome, string detail)
    {
        AcceptanceOutcome settled = run.Settle(outcome);
        AcceptanceRun.Finish(run, settled, detail);
        return settled;
    }

    /// <summary>
    /// 跟着读提升实例的日志文件。
    /// </summary>
    /// <remarks>
    /// <para>为什么要有这个：提升实例是 <c>WinExe</c>，没有控制台，本进程<b>拿不到它的 stdout</b>。
    /// 让双方约定一个日志文件、本进程按行跟着读，是唯一能既保住证据、又让用户看见进度的办法。</para>
    /// <para>读失败一律忽略：真正的证据在文件里，读不动不该让准备动作失败。</para>
    /// <para>按<b>行数</b>而不是字节偏移量做记号：<c>StreamReader</c> 有自己的缓冲，
    /// 用字节位置去对齐会漏行或重复，而日志文件只有几 KB，整读的代价可以忽略。</para>
    /// <para>但「按行数记账」要成立，账必须记在<b>确定写完的行</b>上——
    /// 这条被写错过一次，代价是静默丢证据行（见 <see cref="Flush"/> 里的注释）。</para>
    /// </remarks>
    private sealed class ChildLogTailer(string path)
    {
        private int _emitted;

        /// <summary>已经转发出去的行数。给「转发对账」用——两个数必须来自两条独立算法。</summary>
        public int Forwarded => _emitted;

        public async Task RunAsync(AcceptanceLog log, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    Flush(log, final: false);
                    await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常停止路径。
            }

            Flush(log, final: true);
        }

        /// <summary>
        /// 把还没转发过的行写进本轮日志。
        /// </summary>
        /// <param name="log">目标日志。</param>
        /// <param name="final">
        /// 是否已经收尾。没收尾时不转发最后一行——它可能正被写到一半，
        /// 半行日志比没有日志更误导人。
        /// </param>
        public void Flush(AcceptanceLog log, bool final)
        {
            int seen = _emitted;

            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                string text;

                using (FileStream stream = new(
                           path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader reader = new(stream, new UTF8Encoding(false)))
                {
                    text = reader.ReadToEnd();
                }

                string[] lines = text.Replace("\r\n", "\n").Split('\n');

                if (text.Length == 0)
                {
                    return; // 文件还在，但还没写出任何东西。
                }

                // 数「已经写完的行」，不数「数组里有几段」。
                //
                // Split 的最后一段永远是特例：要么是结尾那个换行留下的空占位，
                // 要么是此刻还没写完的那一行——两种都不算数，所以
                //     usable = lines.Length - 1
                // 就是此刻文件里确定写完的行数；只有收尾时才把没带换行的末行也认下来。
                //
                // 这里曾经写成「收尾时把末段减掉」，于是在文件正好以换行结尾的某一刻，
                // _emitted 会越过那个空占位——而下一行开始时占的就是那个位置，
                // 它于是永远不再被转发。实测丢过 7 行（adapter / profile /
                // 两条 firewall rule / 一条分隔线 / 脚本日志里的一行）：
                // 子实例写了 90 行，父进程只转发出 83 行。
                // 「按行数记账」这招要成立，前提就是账必须记在确定写完的行上。
                int usable = lines.Length - 1;

                if (final && lines[^1].Length > 0)
                {
                    usable = lines.Length;
                }

                for (; seen < usable; seen++)
                {
                    log.WriteLine("[LAB][提升] " + lines[seen].TrimEnd());
                }
            }
            catch (Exception)
            {
                // 见类型说明。
            }
            finally
            {
                _emitted = seen;
            }
        }
    }
}
