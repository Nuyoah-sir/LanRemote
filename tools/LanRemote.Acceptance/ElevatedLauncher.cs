using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LanRemote.Acceptance;

/// <summary>
/// 以管理员身份拉起一个「窄域提升实例」，并等它结束、取回退出码。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：本机没有合格 RFC1918 网卡时，用户要在窗口里一键把本机
/// 准备好（加 lab 地址 + 两条入站规则）。这一步必须提权，而 ADR-035 定的模型是
/// <b>主进程永不提权</b>：提权只发生在「同一个 exe + 固定动词 + 结构化参数」的窄域
/// helper 里，一次只做一件事。这个类就是那条通道——它只把
/// <see cref="LabSetupRole"/> 拼好的固定参数交给系统，<b>不接受</b>任意命令或脚本路径。</para>
/// <para><b>为什么是 <c>ShellExecuteExW</c></b>：它是 Windows 上「请求提权」的最直接约定，
/// 返回的 <c>hProcess</c> 能等、能取退出码。本机实测（HANDOFF §16）：</para>
/// <list type="bullet">
/// <item><c>Start-Process -Verb RunAs</c> 被本机安全策略拦截（"bypasses PowerShell security
/// checks"），提权路径不能依赖它；</item>
/// <item><c>ShellExecuteExW</c> + <c>WaitForSingleObject</c> + <c>GetExitCodeProcess</c> 可用。</item>
/// </list>
/// <para><b>绝不能在 UI 线程上调用</b>：UAC 弹窗期间 <c>ShellExecuteExW</c> 不返回，
/// 在 UI 线程上等它会把窗口冻住——用户看到的是「点了按钮程序就死了」。
/// 所以对外只暴露 <see cref="RunElevatedAsync"/>，内部自己 <c>Task.Run</c>。</para>
/// <para><b>UAC 被拒绝是正常输入</b>：<c>ERROR_CANCELLED</c>（1223）按「用户取消」报告，
/// 绝不循环弹窗（ADR-035）。</para>
/// <para><b>提权实例独立于本进程</b>：窗口关掉之后它照样跑完并把日志写盘，
/// 所以「父进程退出了」不等于「什么都没发生」——报告里必须说清楚这一点。</para>
/// </remarks>
internal static class ElevatedLauncher
{
    /// <summary>用户在 UAC 里点了「否」时 <c>ShellExecuteExW</c> 留下的错误码。</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>要 <c>hProcess</c>；否则进程句柄是 NULL，无法等待。</summary>
    private const uint SeeMaskNoCloseProcess = 0x00000040;

    /// <summary>让 ShellExecuteEx 在真正把进程拉起来之后再返回。</summary>
    private const uint SeeMaskNoAsync = 0x00000100;

    private const int SwShowNormal = 1;
    private const uint WaitTimeout = 258;

    /// <summary>一次提权启动的结果。</summary>
    /// <param name="Started">子进程是否真的起来了。</param>
    /// <param name="Cancelled">是否被 UAC 拒绝（用户点了「否」）。</param>
    /// <param name="ExitCode">子进程退出码；没跑完时是 <c>-1</c>。</param>
    /// <param name="Error">失败原因；成功为 <see langword="null"/>。</param>
    public sealed record LaunchResult(bool Started, bool Cancelled, int ExitCode, string? Error);

    /// <summary>提权启动并等待结束。</summary>
    /// <param name="exePath">要拉起的可执行文件（本程序自己）。</param>
    /// <param name="arguments">结构化参数（原样传给子进程，逐个引用）。</param>
    /// <param name="timeout">等待上限。</param>
    public static async Task<LaunchResult> RunElevatedAsync(
        string exePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout) =>
        await Task.Run(() => RunElevated(exePath, arguments, timeout)).ConfigureAwait(false);

    private static LaunchResult RunElevated(string exePath, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        ShellExecuteInfo info = new()
        {
            Size = Marshal.SizeOf<ShellExecuteInfo>(),
            Mask = SeeMaskNoCloseProcess | SeeMaskNoAsync,
            Verb = "runas",
            File = exePath,
            Parameters = string.Join(' ', arguments.Select(QuoteArgument)),
            Directory = Path.GetDirectoryName(exePath),
            Show = SwShowNormal,
        };

        if (!ShellExecuteExW(ref info))
        {
            int error = Marshal.GetLastWin32Error();

            if (error == ErrorCancelled)
            {
                return new LaunchResult(false, true, -1, "用户取消了管理员权限请求（UAC 未同意）。");
            }

            return new LaunchResult(
                false,
                false,
                -1,
                new Win32Exception(error).Message + $"（Win32 {error}）");
        }

        if (info.Process == IntPtr.Zero)
        {
            return new LaunchResult(true, false, -1, "系统没有给出子进程句柄，无法等它结束。");
        }

        try
        {
            uint milliseconds = (uint)Math.Clamp(timeout.TotalMilliseconds, 1d, 4_294_967_294d);
            uint waited = WaitForSingleObject(info.Process, milliseconds);

            if (waited == WaitTimeout)
            {
                return new LaunchResult(
                    true,
                    false,
                    -1,
                    $"等子进程结束超过 {timeout.TotalMinutes:0.#} 分钟；它可能仍在跑。");
            }

            if (!GetExitCodeProcess(info.Process, out uint exitCode))
            {
                int error = Marshal.GetLastWin32Error();
                return new LaunchResult(
                    true,
                    false,
                    -1,
                    new Win32Exception(error).Message + $"（Win32 {error}）");
            }

            return new LaunchResult(true, false, unchecked((int)exitCode), null);
        }
        finally
        {
            CloseHandle(info.Process);
        }
    }

    /// <summary>
    /// 按 Windows 的命令行引用规则拼一个参数。
    /// </summary>
    /// <remarks>
    /// 不能简单地在两侧加引号：命令行的解析规则里，<b>反斜杠只有在引号前才需要成对</b>，
    /// 而日志路径里恰好可能出现空格（用户名带空格时 <c>%TEMP%</c> 就带空格）。
    /// 这里用与 CRT <c>argv</c> 一致的那套算法，避免出现「路径被截断成两半」这种
    /// 只在个别机器上复现的故障。
    /// </remarks>
    private static string QuoteArgument(string value)
    {
        bool needsQuotes = value.Length == 0;

        foreach (char character in value)
        {
            if (character is ' ' or '\t' or '"')
            {
                needsQuotes = true;
                break;
            }
        }

        if (!needsQuotes)
        {
            return value;
        }

        StringBuilder quoted = new(value.Length + 8);
        quoted.Append('"');

        for (int index = 0; index < value.Length; index++)
        {
            int backslashes = 0;

            while (index < value.Length && value[index] == '\\')
            {
                backslashes++;
                index++;
            }

            if (index == value.Length)
            {
                // 结尾的反斜杠必须翻倍，否则会把收尾的引号转义掉。
                quoted.Append('\\', backslashes * 2);
                break;
            }

            if (value[index] == '"')
            {
                quoted.Append('\\', (backslashes * 2) + 1);
                quoted.Append('"');
            }
            else
            {
                quoted.Append('\\', backslashes);
                quoted.Append(value[index]);
            }
        }

        quoted.Append('"');
        return quoted.ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int Size;
        public uint Mask;
        public IntPtr Hwnd;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Verb;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string File;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Parameters;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Directory;

        public int Show;
        public IntPtr Instance;
        public IntPtr IdList;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Class;

        public IntPtr ClassKey;
        public uint HotKey;
        public IntPtr IconOrMonitor;
        public IntPtr Process;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteExW(ref ShellExecuteInfo info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
