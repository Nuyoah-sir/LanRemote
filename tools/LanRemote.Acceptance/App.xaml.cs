using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace LanRemote.Acceptance;

/// <summary>
/// 验收器应用入口，同时是两个入口的调度点。
/// </summary>
/// <remarks>
/// <para><b>两个入口</b>：无参数 → 开窗口（给人用，双击就是这个）；
/// 带 <c>--headless</c> → 跑命令行（给脚本用）。两条路调用完全相同的
/// <see cref="HostRole"/> / <see cref="ClientRole"/>。</para>
/// <para><b>异常处理有硬性要求</b>：<c>WinExe</c> 没有控制台，未捕获异常只会让窗口
/// 无声消失，用户既不知道发生了什么，也拿不到任何证据。所以任何逃逸到这里的异常
/// 都必须记录泛化故障并停止/作废当前运行；不输出可能含密钥的异常正文或堆栈。</para>
/// <para><b>为什么要防重入</b>：如果异常发生在渲染/排版路径上
/// （例如曾经踩过的字体缓存崩溃），每次重绘都会立刻再抛一次，
/// 于是 <c>MessageBox</c> 弹出 → 重绘 → 再弹出 …… 变成弹框风暴，
/// 用户除了强制结束进程什么也做不了，连日志都读不到。
/// 这里用两个闸门阻断：只弹第一个框；并且第二次异常开始只写盘不弹。</para>
/// <para><b>本轮新增的两个 handler 是被实测逼出来的</b>（HANDOFF §15）：
/// <c>AppDomain.UnhandledException</c> 覆盖非 UI 线程——这类异常会直接带走进程；
/// <c>TaskScheduler.UnobservedTaskException</c> 覆盖 fire-and-forget 任务——这类异常
/// <b>不会</b>带走进程，因而会完全静默。两个方向都要有落盘动作。</para>
/// </remarks>
public partial class App : Application
{
    private static int _crashReportCount;
    private static int _headless;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);

        if (e.Args.Length > 0)
        {
            Interlocked.Exchange(ref _headless, 1);

            bool parsed = HeadlessCommand.TryParse(e.Args, out HeadlessCommand? command, out string? error);

            // 两个条件都要判。只判 parsed 是不够的——曾经就因为 TryParse 在参数错误时
            // 返回 true，于是这里拿着 null 命令走下去，参数错误的消息被彻底丢掉，
            // 用户看到的只有「退出码 3、零输出」。详见 HeadlessCommand.TryParse 的 remarks。
            if (!parsed || error is not null || command is null)
            {
                HeadlessRunner.WriteUsage(
                    error ?? "参数里没有 --headless。不带任何参数双击才是打开窗口。");

                Shutdown((int)AcceptanceOutcome.HarnessError);
                return;
            }

            RunHeadless(command);
            return;
        }

        ShutdownMode = ShutdownMode.OnMainWindowClose;
        MainWindow window = new();
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// 无界面执行。
    /// </summary>
    /// <remarks>
    /// <b>绝不能在 <see cref="OnStartup"/> 里同步等待</b>：此刻消息循环还没开始转，
    /// UI 线程上的 <c>SynchronizationContext</c> 却已经装好了，任何
    /// <c>await</c> 的续体都会被投回一个不转的泵——直接死锁。
    /// 所以放到 <see cref="Task.Run(Func{Task})"/> 上跑（线程池上没有 WPF 的上下文，
    /// 续体不会被投回 UI 线程），跑完再用 <see cref="Dispatcher"/> 回到 UI 线程退出。
    /// </remarks>
    private void RunHeadless(HeadlessCommand command)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _ = Task.Run(async () =>
        {
            int code;

            try
            {
                code = await HeadlessRunner.RunAsync(command);
            }
            catch (Exception ex)
            {
                TryWriteCrash(ex);
                code = (int)AcceptanceOutcome.HarnessError;
            }

            Dispatcher.Invoke(() => Shutdown(code));
        });
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        args.Handled = true;
        // handled 只为保留清理机会，不是继续使用本轮的 PASS；先停止审批、清密钥并作废当前运行。
        if (Current.MainWindow is MainWindow window)
        {
            try { window.HandleDispatcherFault(); }
            catch (Exception)
            {
                // 窗口先记故障，停止路径用 finally 保证取消；渲染再次失败不递归弹框。
            }
        }
        else
        {
            Current.Shutdown((int)AcceptanceOutcome.HarnessError);
        }
        string path = TryWriteCrash(args.Exception);
        int seen = Interlocked.Increment(ref _crashReportCount);
        if (seen == 1 && Volatile.Read(ref _headless) == 0)
        {
            try
            {
                MessageBox.Show(
                    "验收器界面发生故障，当前未完成运行不能作为通过证据。\n" +
                    "已请求停止并等待任务收尾；lab 提升动作不会被中途终止。\n\n" +
                    "泛化故障信息已写入：\n" + path +
                    "\n\n异常正文、内部异常及堆栈未输出，避免秘密进入日志。",
                    "LanRemote M4 验收器",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch (Exception)
            {
                // 故障对话框本身也可能触发渲染异常；保留原窗口消息泵继续收尾。
            }
        }
    }

    /// <remarks>
    /// 非 UI 线程上的未处理异常<b>无法被处理</b>——本机实测确认进程会当场结束。
    /// 这里唯一能做的是把现场写进日志，让事后能知道发生了什么。
    /// 那个 <c>IsTerminating</c> 判断就是说明这件事的。
    /// </remarks>
    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception exception)
        {
            TryWriteCrash(exception);
        }
    }

    /// <remarks>
    /// fire-and-forget 任务里漏出来的异常。本机实测：.NET 上它<b>不会</b>终止进程，
    /// 所以不处理就等于彻底静默。这里一律标记为已观察并落盘。
    /// </remarks>
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        TryWriteCrash(args.Exception);
        args.SetObserved();
    }

    private static string TryWriteCrash(Exception exception)
    {
        string directory = AcceptanceLog.DefaultDirectory;

        try
        {
            Directory.CreateDirectory(directory);

            // 用 append 而不是覆盖，是为了保住「第一次崩在哪」这个最有用的信息；
            // 同一次会话里崩溃多次时，覆盖写会把之前的现场抹掉。
            string path = Path.Combine(directory, "crash.log");
            File.AppendAllText(
                path,
                "===== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " =====\r\n" +
                "未预期异常（正文、内部异常及堆栈未输出，避免秘密进入日志）。\r\n",
                new UTF8Encoding(false));
            return path;
        }
        catch (Exception)
        {
            return "(写不进 " + directory + ")";
        }
    }
}
