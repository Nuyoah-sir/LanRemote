using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace LanRemote.Acceptance;

/// <summary>
/// 验收器应用入口。
/// </summary>
/// <remarks>
/// <para>这里的异常处理有一条硬性要求：<b>WinExe 没有控制台</b>，未捕获异常只会让窗口
/// 无声消失，用户既不知道发生了什么，也拿不到任何证据。所以任何逃逸到这里的异常
/// 都必须先落到磁盘上的 crash 文件，再弹出来——验收的价值全在证据能带走。</para>
/// <para><b>为什么要防重入</b>：如果异常发生在渲染/排版路径上
/// （例如曾经踩过的字体缓存崩溃），每次重绘都会立刻再抛一次，
/// 于是 <c>MessageBox</c> 弹出 → 重绘 → 再弹出 …… 变成弹框风暴，
/// 用户除了强制结束进程什么也做不了，连日志都读不到。
/// 这里用两个闸门阻断：只弹第一个框；并且第二次异常开始只写盘不弹。</para>
/// </remarks>
public partial class App : Application
{
    private static int _crashReportCount;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        // 写盘永远做（每次追加，保留全部现场），弹框只做一次。
        string path = TryWriteCrash(args.Exception);
        int seen = Interlocked.Increment(ref _crashReportCount);

        if (seen == 1)
        {
            MessageBox.Show(
                "验收器发生未处理异常：\n\n" + args.Exception.Message +
                "\n\n完整信息已写入：\n" + path +
                (seen < 2 ? "\n\n（若窗口继续闪退，请直接把 crash.log 整段贴回。）" : string.Empty),
                "LanRemote M3 验收器",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        // 必须置 true，否则异常会继续往上传，把进程直接带走（连窗口都没有了）。
        args.Handled = true;
    }

    private static string TryWriteCrash(Exception exception)
    {
        string directory = Path.Combine(Path.GetTempPath(), "lanremote-m3-acceptance");
        try
        {
            Directory.CreateDirectory(directory);

            // 覆盖写 + 追加：同一次会话里崩溃多次时，没有 AppendAllText 会把之前的现场抹掉。
            // 用 append 而不是覆盖，是为了保住「第一次崩在哪」这个最有用的信息。
            string path = Path.Combine(directory, "crash.log");
            File.AppendAllText(
                path,
                "===== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " =====\r\n" +
                exception + "\r\n",
                new UTF8Encoding(false));
            return path;
        }
        catch (Exception)
        {
            return "(写不进 " + directory + ")";
        }
    }
}
