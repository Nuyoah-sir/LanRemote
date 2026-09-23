using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LanRemote.Core.Models;
using LanRemote.Security.Certificates;
using LanRemote.Transport;

namespace LanRemote.Acceptance;

/// <summary>
/// 一次验收运行的上下文：Run ID、不可变日志、构建溯源、后台故障、中止标记。
/// </summary>
/// <remarks>
/// <para><b>存在的理由（外部评审第 9/10/13 条）</b>：没有 Run ID 与运行边界，
/// 两份日志放一起无法证明它们属于同一次测量；没有构建溯源，
/// 「M3 通过」这句话无法回答「哪个二进制、在哪台机器、哪套时限下通过的」；
/// 没有故障通道，「验收器自己崩了」会被读成「被测对象失败了」。</para>
/// <para><b>后台故障为什么不抛异常而是记账</b>：验收器跑在 WPF 里，
/// 本机实测（见 HANDOFF §15 的实测记录）说明裸线程上的未处理异常会
/// <b>当场带走进程</b>，Windows GUI 子系统没有控制台，现场直接蒸发。
/// 所以策略是「绝不把工作放上裸线程 + 每个后台任务的异常都记账 + 收尾时统一结算」，
/// 而不是指望运行时兜底。</para>
/// </remarks>
internal sealed class AcceptanceRun
{
    private readonly List<string> _backgroundFaults = new();
    private readonly object _completionGate = new();
    private AcceptanceOutcome? _completed;
    private int _aborted;

    private AcceptanceRun(string runId, string role, AcceptanceLog log)
    {
        RunId = runId;
        Role = role;
        Log = log;
    }

    /// <summary>本次运行的短标识（8 位十六进制）。</summary>
    public string RunId { get; }

    /// <summary>角色：<c>info</c> / <c>host</c> / <c>client</c>。</summary>
    public string Role { get; }

    /// <summary>本次运行的日志（不可变文件名）。</summary>
    public AcceptanceLog Log { get; }

    /// <summary>运行开始时刻（UTC）。</summary>
    public DateTimeOffset StartedUtc { get; private init; }

    /// <summary>操作员是否请求过中止。</summary>
    public bool AbortedByOperator => Volatile.Read(ref _aborted) != 0;

    /// <summary>是否出现过后台故障。</summary>
    public bool HasBackgroundFaults
    {
        get
        {
            lock (_backgroundFaults)
            {
                return _backgroundFaults.Count > 0;
            }
        }
    }

    /// <summary>后台故障摘要（没有则为空串）。</summary>
    public string BackgroundFaultSummary
    {
        get
        {
            lock (_backgroundFaults)
            {
                return string.Join(" | ", _backgroundFaults);
            }
        }
    }

    /// <summary>建一次运行。</summary>
    /// <param name="directory">日志目录；为空则用默认目录。</param>
    /// <param name="prefix">文件名里的角色前缀。</param>
    public static AcceptanceRun Create(string? directory, string prefix)
    {
        string runId = Guid.NewGuid().ToString("N")[..8];
        AcceptanceLog log = AcceptanceLog.CreateForRun(directory, prefix, runId);
        return new AcceptanceRun(runId, prefix, log) { StartedUtc = DateTimeOffset.UtcNow };
    }

    /// <summary>
    /// 按调用方给出的绝对路径建一次运行（<b>只给提升实例用</b>）。
    /// </summary>
    /// <remarks>
    /// 提升实例是 <c>WinExe</c>，它的父进程拿不到它的 stdout，只能双方约定一个
    /// <b>确定的</b>日志路径再读回来。所以这里是全工具唯一「日志文件名由外面定」的地方，
    /// 其余一律走 <see cref="Create"/>——理由是不变的：验收仪器不该允许选择性抹除证据，
    /// 而「谁都能指定文件名」离那件事只有一步。
    /// </remarks>
    /// <param name="filePath">日志文件的绝对路径。</param>
    /// <param name="prefix">角色名（写进运行头的 <c>[RUN] role</c>）。</param>
    public static AcceptanceRun CreateAtFile(string filePath, string prefix)
    {
        string directory = Path.GetDirectoryName(filePath) ?? AcceptanceLog.DefaultDirectory;
        string fileName = Path.GetFileName(filePath);
        string runId = Guid.NewGuid().ToString("N")[..8];
        AcceptanceLog log = new(directory, fileName);
        return new AcceptanceRun(runId, prefix, log) { StartedUtc = DateTimeOffset.UtcNow };
    }

    /// <summary>标记操作员中止——整轮作废。</summary>
    public void MarkOperatorAbort(string where)
    {
        Interlocked.Exchange(ref _aborted, 1);
        Log.WriteLine($"[RUN] 操作员中止，来源：{where}。本轮所有证据作废（INVALID_RUN）。");
    }

    /// <summary>记一次后台故障：不抛异常，只记账。</summary>
    /// <remarks>
    /// 任何 fire-and-forget 的任务收尾都必须调它。理由：.NET 实测结论是
    /// <b>未观察的 faulted Task 不会杀进程</b>（终结器不再触发进程终止），
    /// 所以后台炸了会<b>完全静默</b>——被控端看起来「跑得好好的」，
    /// 实际 accept 循环早就死了，后面的场景必然测不出东西。
    /// </remarks>
    public void ReportBackgroundFault(string source, Exception exception)
    {
        string line = $"{source}: {exception.GetType().Name}: {exception.Message}";

        lock (_backgroundFaults)
        {
            _backgroundFaults.Add(line);
        }

        Log.WriteLine($"[HARNESS][FAULT] {line}");
        Log.WriteLine($"[HARNESS][FAULT] 堆栈：{exception}");
    }

    /// <summary>写运行头：Run ID、构建溯源、环境、时限。</summary>
    /// <param name="timeouts">本次使用的五个时限；为 <see langword="null"/> 表示本角色不用它们。</param>
    public void WriteHeader(TransportTimeouts? timeouts)
    {
        Log.WriteLine("======================================================================");
        Log.WriteLine($"[RUN] runId        = {RunId}");
        Log.WriteLine($"[RUN] role         = {Role}");
        Log.WriteLine($"[RUN] startedUtc   = {StartedUtc.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}Z");
        Log.WriteLine($"[RUN] logFile      = {Log.FilePath ?? "(未落盘——只有 UI 里这一份)"}");
        Log.WriteLine("======================================================================");

        // ── 构建溯源（评审第 10 条）────────────────────────────────────────
        // 「M3 通过」必须能回答：哪个二进制、哪台机器、哪套时限。
        WriteAssembly("harness", Environment.ProcessPath);
        WriteAssembly("transport", typeof(TransportHost).Assembly.Location);

        Log.WriteLine($"[ENV] os           = {RuntimeInformation.OSDescription}");
        Log.WriteLine($"[ENV] osBuild      = {Environment.OSVersion.Version}");
        Log.WriteLine($"[ENV] arch         = {RuntimeInformation.ProcessArchitecture} / " +
                      RuntimeInformation.OSArchitecture);
        Log.WriteLine($"[ENV] dotnet       = {RuntimeInformation.FrameworkDescription}");
        Log.WriteLine($"[ENV] machine      = {Environment.MachineName}");
        Log.WriteLine($"[ENV] processId    = {Environment.ProcessId}");

        if (timeouts is not null)
        {
            Log.WriteLine($"[ENV] deadline.connect         = {timeouts.ConnectTimeout.TotalMilliseconds:0.#} ms");
            Log.WriteLine($"[ENV] deadline.handshake       = {timeouts.HandshakeTimeout.TotalMilliseconds:0.#} ms");
            Log.WriteLine($"[ENV] deadline.lengthPrefix    = {timeouts.LengthPrefixTimeout.TotalMilliseconds:0.#} ms");
            Log.WriteLine($"[ENV] deadline.payload         = {timeouts.PayloadTimeout.TotalMilliseconds:0.#} ms");

            // M3.1 语义修正：hello 是「客户端写 hello 帧的预算」，服务端 pre-auth 不消费它。
            // 旧表述「首个 hello 帧到达的绝对时限」暗示服务端存在独立顺序段，是坐实过的表述事故
            // （HANDOFF §17 教训 #24）——日志行按修正后的语义如实写，不要改回去。
            Log.WriteLine(
                $"[ENV] deadline.hello           = {timeouts.HelloTimeout.TotalMilliseconds:0.#} ms" +
                "（客户端写预算；服务端不消费）");

            // 信封是跨分段的总量硬上限：分段各自绝对 ≠ 总量有界（前缀 5 s + payload 10 s 可加和），
            // 信封把这类顺序等待整体封顶，且永不被子阶段的进展重置（HANDOFF §17 教训 #25）。
            Log.WriteLine(
                $"[ENV] deadline.preAuthEnvelope = {timeouts.PreAuthEnvelopeTimeout.TotalMilliseconds:0.#} ms" +
                "（pre-auth 总量上限，永不重置）");
        }

        Log.WriteLine("[ENV] preAuthMaxBytes       = " + TransportConstants.MaxPreAuthMessageBytes);
        Log.WriteLine("[ENV] tlsProtocols          = Tls12|Tls13（显式，两端都不依赖默认值）");
        Log.WriteLine("[ENV] allowTlsResume        = false（两端）");
        Log.WriteLine("[ENV] allowRenegotiation    = false（两端）");
    }

    /// <summary>写本机身份与合格网卡。</summary>
    public void WriteIdentity(DeviceIdentity identity, DeviceCertificate certificate, IReadOnlyList<IPAddress> addresses)
    {
        Log.WriteLine($"[ID]  deviceCode    = {identity.DeviceCode}");
        Log.WriteLine($"[ID]  deviceId      = {identity.DeviceId}");
        Log.WriteLine($"[ID]  certSha256    = {certificate.Sha256FingerprintHex}");
        Log.WriteLine($"[ID]  certNotAfter  = " +
                      certificate.Certificate.NotAfter.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture));
        Log.WriteLine($"[ID]  qualifiedNic  = " +
                      (addresses.Count == 0 ? "(无)" : string.Join(", ", addresses)));
    }

    /// <summary>写运行尾。缺了它说明进程没走到收尾——本轮证据不可信。</summary>
    public void WriteFooter(AcceptanceOutcome outcome, string detail)
    {
        Log.WriteLine("[RESULT] outcome   = " + outcome.Code());
        Log.WriteLine($"[RESULT] detail    = {detail}");
        Log.WriteLine($"[RESULT] background = {(HasBackgroundFaults ? BackgroundFaultSummary : "无故障")}");
        Log.WriteLine($"[RESULT] aborted   = {AbortedByOperator}");
        Log.WriteLine($"[RESULT] exitCode  = {(int)outcome} ({outcome.Describe()})");
        Log.WriteLine("RUN COMPLETE");
        Log.WriteLine("======================================================================");
    }

    private void WriteAssembly(string label, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Log.WriteLine($"[BUILD] {label,-10} = (拿不到路径)");
            return;
        }

        string version = "(unknown)";
        try
        {
            version = AssemblyName.GetAssemblyName(path).Version?.ToString() ?? "(null)";
        }
        catch (Exception)
        {
            // 单文件发布时 exe 不是托管程序集，取不到版本不算错误。
        }

        string hash;
        try
        {
            using FileStream stream = File.OpenRead(path);
            hash = Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception ex)
        {
            hash = $"(算不出: {ex.GetType().Name})";
        }

        long size = new FileInfo(path).Length;
        Log.WriteLine($"[BUILD] {label,-10} = {Path.GetFileName(path)} version={version} " +
                      $"bytes={size} sha256={hash}");
        Log.WriteLine($"[BUILD] {label,-10}   path={path}");
    }

    /// <summary>把结局写进日志并返回退出码。</summary>
    public static int Finish(AcceptanceRun run, AcceptanceOutcome outcome, string detail)
    {
        return (int)run.Complete(outcome, detail);
    }

    /// <summary>所有角色资源收尾后调用；GUI/headless 共用一次结算、一次 footer。</summary>
    public AcceptanceOutcome Complete(AcceptanceOutcome observed, string detail)
    {
        lock (_completionGate)
        {
            if (_completed is { } previous)
                return previous;

            AcceptanceOutcome settled = Settle(observed);
            WriteFooter(settled, detail);
            AcceptanceOutcome afterWrite = Settle(settled);
            if (afterWrite != settled)
            {
                // footer 自身落盘失败也必须毒化返回值；内存/UI 明确标废，不掩盖磁盘证据不完整。
                Log.WriteLine($"[RESULT][CORRECTION] outcome={afterWrite.Code()} reason=footer-write-failed; 磁盘证据无效");
            }
            _completed = afterWrite;
            return afterWrite;
        }
    }

    /// <summary>
    /// 结算：把「有后台故障 / 操作员中止」并进最终结局。
    /// </summary>
    /// <remarks>
    /// 这两件事优先于场景结论：只要发生过，整轮就不是 PASS。
    /// </remarks>
    public AcceptanceOutcome Settle(AcceptanceOutcome observed)
    {
        if (AbortedByOperator)
        {
            return AcceptanceOutcome.InvalidRun;
        }

        if (HasBackgroundFaults || Log.FileUnavailable)
        {
            return new[] { observed, AcceptanceOutcome.HarnessError }.Combine();
        }

        return observed;
    }

    /// <summary>把一段说明按行写进日志（用于多行人工核对清单）。</summary>
    public void WriteBlock(string title, IEnumerable<string> lines)
    {
        Log.WriteLine(string.Empty);
        Log.WriteLine("-------- " + title + " --------");

        foreach (string line in lines)
        {
            Log.WriteLine(line);
        }

        Log.WriteLine("-------- " + new string('-', title.Length) + " --------");
    }
}
