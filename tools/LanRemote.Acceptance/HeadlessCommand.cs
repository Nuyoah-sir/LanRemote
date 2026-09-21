using System.IO;

namespace LanRemote.Acceptance;

/// <summary>
/// 无界面入口的命令行解析。
/// </summary>
/// <remarks>
/// <para><b>为什么同一个 exe 里要有第二个入口。</b>双击入口必须是一个窗口
/// （这个项目的用户不开 PowerShell），但验收是<b>要跑两次、两台机器</b>的事，
/// 每次都要人点按钮、读数字、粘贴设备码——人一多手就会出错，
/// 而且<b>自动化不了</b>：本机（A 机）的界面没法被脚本驱动。</para>
/// <para>所以：<b>双击仍然是窗口</b>（给人用），带 <c>--headless</c> 参数就是纯命令行
/// （给脚本用）。两条路走的是同一套 <see cref="HostRole"/> / <see cref="ClientRole"/>，
/// 不存在「脚本跑的是另一套逻辑」这种偏差。准备 lab 网段同样是两条路共用
/// <see cref="LabSetupRole"/>：窗口里的「准备为 A 机 / B 机」按钮与
/// <c>--headless prepare-lab</c> 调的是同一个方法。</para>
/// <para><b>提升动词的参数面是窄的</b>（ADR-035）：<c>lab-apply</c> / <c>lab-undo</c>
/// 只接受它们自己那几个选项，多给一个就报参数错误。提权那一步只做一件事，
/// 不能让「顺手多带点东西」成为可能。</para>
/// </remarks>
internal sealed record HeadlessCommand(
    string Role,
    int Seconds,
    string? Peer,
    IReadOnlyList<string> Scenarios,
    string? Address,
    string? Pin,
    int Port,
    string? LogDirectory,
    string? LabRole,
    string? LogFile)
{
    /// <summary>角色名：环境自检。</summary>
    public const string RoleInfo = "info";

    /// <summary>角色名：被控端。</summary>
    public const string RoleHost = "host";

    /// <summary>角色名：控制端。</summary>
    public const string RoleClient = "client";

    /// <summary>提升实例动词：写入 lab 地址 + 两条入站规则。<b>必须已是管理员。</b></summary>
    public const string RoleLabApply = "lab-apply";

    /// <summary>提升实例动词：撤销 lab 地址 + 规则。<b>必须已是管理员。</b></summary>
    public const string RoleLabUndo = "lab-undo";

    /// <summary>非提升的编排者：请管理员权限、等它结束、核验状态。</summary>
    public const string RolePrepareLab = "prepare-lab";

    /// <summary>
    /// 本命令对应的 lab 动作；非 lab 角色为 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 刻意<b>不用</b>类型名 <c>LabAction</c> 当属性名：属性与类型同名时，
    /// 后面的代码里每一处 <c>LabAction.Undo</c> 都要靠编译器消歧，
    /// 读的人也会先愣一下。名字差一个词，省掉的是长期的困惑。
    /// </remarks>
    public LabAction? LabOperation => Role switch
    {
        RoleLabApply or RolePrepareLab => LabRole == "B" ? LabAction.ApplyB : LabAction.ApplyA,
        RoleLabUndo => LabAction.Undo,
        _ => null,
    };

    /// <summary>命令行用法。</summary>
    public static string Usage =>        """
        用法（同一个 exe，双击则打开窗口）：
          LanRemote.Acceptance.exe --headless info
          LanRemote.Acceptance.exe --headless host [--seconds N]
          LanRemote.Acceptance.exe --headless client --peer <设备码> [--scenario <场景>]... [--all]
          LanRemote.Acceptance.exe --headless client --address <IP> --pin <指纹> [--all]
          LanRemote.Acceptance.exe --headless prepare-lab --lab-role <A|B>

        全局选项：
          --log-dir <目录>      证据日志落盘目录（默认 %TEMP%\lanremote-m3-acceptance）

        控制端选项：
          --peer <设备码>       对端被控端设备码，形如 M5WC-14GX
          --scenario <场景>     重复给出可跑多个；场景见下
          --all                 跑全部必做场景（等同依次给出四个 --scenario）
          --address <IP>        跨子网场景用：对端监听地址
          --pin <十六进制>       跨子网场景用：对端证书指纹
          --port <端口>         对端 TLS 端口（默认 45873）

        已实现的场景：success | pin-mismatch | timeout | slow-dribble | cross-subnet
        必做场景    ：success | pin-mismatch | timeout | slow-dribble

        准备本机 lab 网段（窗口里那三个按钮走的就是这一条）：
          prepare-lab           普通权限进程用：请 UAC 提权、等它结束、再核验状态
          --lab-role <A|B>      lab 地址：A = 192.168.1.10，B = 192.168.1.20
          lab-apply             只给提升实例用：写入 lab 地址 + 两条入站规则（须已是管理员）
          lab-undo              只给提升实例用：撤销 lab 地址 + 规则（须已是管理员）
          --log-file <路径>     提升实例的日志落点（由调用方指定，父进程要读回来）

        退出码：0 符合预期 / 1 不符合预期 / 2 前置条件不满足
                3 验收器故障（本轮无效）/ 4 操作员中止（本轮作废）
        """;

    /// <summary>
    /// 解析参数。
    /// </summary>
    /// <param name="args">进程参数。</param>
    /// <param name="command">成功时给出命令。</param>
    /// <param name="error">失败原因。</param>
    /// <returns>参数是否构成一个可执行的 headless 命令。</returns>
    /// <remarks>
    /// <para><b>返回值就是字面意思</b>：<see langword="true"/> ⟺ <paramref name="command"/> 非空。</para>
    /// <para>没有 <c>--headless</c> 时返回 <see langword="false"/> 且 <paramref name="error"/> 为
    /// <see langword="null"/>——那是「应该打开窗口」的意思，不是错误。</para>
    /// <para><b>这里踩过一个大坑，别改回去</b>：原先错误分支写的是 <c>return true</c>
    /// （大概是想着「这确实是一次 headless 调用」），于是调用方按「成功」处理，
    /// 拿着 <c>null</c> 命令进了执行路径 → <c>NullReferenceException</c> →
    /// 被顶层兜底变成退出码 3，<b>而参数错误的消息从头到尾一个字都没打出来</b>。
    /// 本机实测：<c>--headless client --bogus</c> 退出 3、stdout/stderr 均为 0 字节，
    /// 只有 crash.log 里一行 NRE。表象是「验收器坏了」，真因是返回值语义反了
    /// ——一个和调用方约定相反、且不可能被单元测试发现的错误约定。</para>
    /// </remarks>
    public static bool TryParse(
        string[] args,
        out HeadlessCommand? command,
        out string? error)
    {
        command = null;
        error = null;

        if (args.Length == 0 || !args.Contains("--headless", StringComparer.Ordinal))
        {
            return false;
        }

        string? role = null;
        int seconds = HostRole.RunUntilCancelled;
        string? peer = null;
        string? address = null;
        string? pin = null;
        string? logDirectory = null;
        string? labRole = null;
        string? logFile = null;
        int port = LanRemote.Discovery.DiscoveryConstants.ExpectedTransportPort;
        List<string> scenarios = new();

        // 见过哪些选项。提升动词要据此判定「有没有多带东西」——
        // 用「值与默认值是否相等」来判断是不行的：显式传入默认值与没传无法区分。
        List<string> seenOptions = new();

        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                seenOptions.Add(arg);
            }

            switch (arg)
            {
                case "--headless":
                    break;

                case "--seconds":
                    if (!TryNext(args, ref index, out string rawSeconds))
                    {
                        error = "--seconds 缺少取值。";
                        return false;
                    }

                    if (!int.TryParse(rawSeconds, out seconds) || seconds < 0)
                    {
                        error = $"--seconds 需要一个非负整数，收到「{rawSeconds}」。";
                        return false;
                    }

                    break;

                case "--peer":
                    if (!TryNext(args, ref index, out string peerValue))
                    {
                        error = "--peer 缺少取值。";
                        return false;
                    }

                    peer = peerValue;
                    break;

                case "--address":
                    if (!TryNext(args, ref index, out string addressValue))
                    {
                        error = "--address 缺少取值。";
                        return false;
                    }

                    address = addressValue;
                    break;

                case "--pin":
                    if (!TryNext(args, ref index, out string pinValue))
                    {
                        error = "--pin 缺少取值。";
                        return false;
                    }

                    pin = pinValue;
                    break;

                case "--port":
                    if (!TryNext(args, ref index, out string rawPort))
                    {
                        error = "--port 缺少取值。";
                        return false;
                    }

                    if (!int.TryParse(rawPort, out port) || port is <= 0 or > 65535)
                    {
                        error = $"--port 需要 1..65535，收到「{rawPort}」。";
                        return false;
                    }

                    break;

                case "--log-dir":
                    if (!TryNext(args, ref index, out string directoryValue))
                    {
                        error = "--log-dir 缺少取值。";
                        return false;
                    }

                    logDirectory = directoryValue;
                    break;

                case "--lab-role":
                    if (!TryNext(args, ref index, out string labRoleValue))
                    {
                        error = "--lab-role 缺少取值。";
                        return false;
                    }

                    labRole = labRoleValue;
                    break;

                case "--log-file":
                    if (!TryNext(args, ref index, out string logFileValue))
                    {
                        error = "--log-file 缺少取值。";
                        return false;
                    }

                    logFile = logFileValue;
                    break;

                case "--scenario":
                    if (!TryNext(args, ref index, out string scenarioValue))
                    {
                        error = "--scenario 缺少取值。";
                        return false;
                    }

                    scenarios.Add(scenarioValue);
                    break;

                case "--all":
                    scenarios.AddRange(ClientRole.MandatoryScenarios);
                    break;

                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        error = $"未知选项 {arg}。";
                        return false;
                    }

                    // 第一个非选项参数当角色。
                    role ??= arg;
                    break;
            }
        }

        if (role is null)
        {
            error = "缺少角色（info / host / client / prepare-lab）。";
            return false;
        }

        if (role is not (RoleInfo or RoleHost or RoleClient or RoleLabApply or RoleLabUndo or RolePrepareLab))
        {
            error = $"未知角色 {role}。";
            return false;
        }

        if (labRole is not null)
        {
            labRole = labRole.ToUpperInvariant();

            if (labRole is not ("A" or "B"))
            {
                error = $"--lab-role 只接受 A 或 B，收到「{labRole}」。";
                return false;
            }
        }

        if (logFile is not null && !Path.IsPathFullyQualified(logFile))
        {
            error = $"--log-file 需要一个绝对路径，收到「{logFile}」。";
            return false;
        }

        // ── 提升动词 / 准备动词：参数面必须窄 ───────────────────────────────
        if (role is RoleLabApply or RoleLabUndo or RolePrepareLab)
        {
            bool allowLabRole = role is RoleLabApply or RolePrepareLab;
            bool allowLogFile = role is RoleLabApply or RoleLabUndo;

            string[] extra = seenOptions
                .Where(option => option switch
                {
                    "--headless" => false,
                    "--lab-role" => !allowLabRole,
                    "--log-file" => !allowLogFile,
                    _ => true,
                })
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (extra.Length > 0)
            {
                string allowed = role switch
                {
                    RoleLabApply => "--headless / --lab-role / --log-file",
                    RoleLabUndo => "--headless / --log-file",
                    _ => "--headless / --lab-role",
                };

                error = $"{role} 只接受 {allowed}，多给了：{string.Join("、", extra)}。" +
                        "（提权那一步只做一件事，参数面是故意做窄的。）";
                return false;
            }

            if (role != RoleLabUndo && labRole is null)
            {
                error = $"{role} 需要 --lab-role A（或 B）。";
                return false;
            }

            if (role == RoleLabUndo && labRole is not null)
            {
                error = "lab-undo 不接受 --lab-role：撤销是两件事一起退，不存在「只撤销 A」。";
                return false;
            }

            command = new HeadlessCommand(
                role,
                HostRole.RunUntilCancelled,
                Peer: null,
                Array.Empty<string>(),
                Address: null,
                Pin: null,
                port,
                logDirectory,
                labRole,
                logFile);

            return true;
        }

        if (labRole is not null)
        {
            error = "--lab-role 只属于 lab-apply / lab-undo / prepare-lab。";
            return false;
        }

        if (logFile is not null)
        {
            error = "--log-file 只属于 lab-apply / lab-undo。";
            return false;
        }

        IReadOnlyList<string> effective = scenarios.Count == 0
            ? ClientRole.MandatoryScenarios
            : scenarios;

        foreach (string scenario in effective)
        {
            if (!ClientRole.KnownScenarios.Contains(scenario, StringComparer.Ordinal))
            {
                error = $"未知场景「{scenario}」。可用：{ClientRole.UsableScenarios()}";
                return false;
            }
        }

        // 需要发现的场景才要求设备码。
        //
        // 直连模式（--address + --pin 都给齐）不需要发现，所以也不要求设备码——
        // 这条最初漏了，于是「直连跑 success」被自己的参数校验挡死，
        // 全体退 3（验收器故障）。参数校验把合法用法判成非法，比不校验更坏：
        // 现象长得像「验收器坏了」，看不出是校验写错。
        bool directMode = !string.IsNullOrWhiteSpace(address) && !string.IsNullOrWhiteSpace(pin);

        bool needsDiscovery = !directMode && effective.Any(
            scenario => !string.Equals(scenario, ClientRole.ScenarioCrossSubnet, StringComparison.Ordinal));

        if (role == RoleClient && needsDiscovery && string.IsNullOrWhiteSpace(peer))
        {
            error = "控制端需要 --peer <对端设备码>，或者用 --address + --pin 走直连。";
            return false;
        }

        command = new HeadlessCommand(role, seconds, peer, effective, address, pin, port, logDirectory, LabRole: null, LogFile: null);
        return true;
    }

    /// <summary>取下一个参数；到末尾则返回 false 且不推进下标。</summary>
    private static bool TryNext(string[] args, ref int index, out string value)
    {
        if (index + 1 >= args.Length)
        {
            value = string.Empty;
            return false;
        }

        index++;
        value = args[index];
        return true;
    }
}
