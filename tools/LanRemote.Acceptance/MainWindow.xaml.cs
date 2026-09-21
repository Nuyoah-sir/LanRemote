using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using LanRemote.Discovery;

namespace LanRemote.Acceptance;

/// <summary>
/// 验收器主窗口。
/// </summary>
/// <remarks>
/// <para><b>为什么要有这个窗口</b>：本项目是桌面软件，M2 的两机验收就是两台机器开界面跑的
/// （HANDOFF §9.1：「用户在 B 机界面确认」「B 点刷新」），ADR-024/025/026 也定了
/// 「终端用户永远不需要打开 PowerShell」。控制台 exe 双击只会打一行用法然后退出，
/// 等于把验收推回终端，与这两条都冲突。</para>
/// <para>因此本程序是 <c>WinExe</c> + WPF：<b>双击就是一个窗口</b>，不需要任何脚本。
/// 所有输出走 <see cref="AcceptanceLog"/>（UI + 磁盘各一份），
/// 「复制全部日志」把证据一次性带走。</para>
/// <para><b>每次点开始都开一轮新的运行</b>：新一轮 = 新的 Run ID + 新的不可变日志文件。
/// 于是「这一次到底测了什么」有确定边界，不会和上一次混在同一个文件里。
/// UI 日志区是累积的（方便一次拷走），但每轮的开头都会打出自己的 Run ID 与文件名。</para>
/// </remarks>
public partial class MainWindow : Window
{
    private readonly string _logDirectory = AcceptanceLog.DefaultDirectory;
    private AcceptanceRun? _run;
    private CancellationTokenSource? _runCts;

    public MainWindow()
    {
        InitializeComponent();

        Loaded += OnLoaded;

        // 渲染完成 = WPF 排版/字体缓存真的跑通了。
        // 这一行是「窗口没崩」的证据，不是装饰：
        // WinExe 没有控制台，若 Measure 阶段抛异常，用户只会看到窗口一闪就没了。
        ContentRendered += OnContentRendered;
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnContentRendered;
        AppendLine($"[GUI] 窗口渲染完成。 ActualWidth={ActualWidth} ActualHeight={ActualHeight}");
    }

    // -----------------------------------------------------------------------
    // 运行生命周期
    // -----------------------------------------------------------------------
    /// <summary>开一轮新的运行：新 Run ID、新不可变日志文件。</summary>
    private AcceptanceRun BeginRun(string role)
    {
        if (_run is not null)
        {
            _run.Log.LineWritten -= OnLineWritten;
        }

        _run = AcceptanceRun.Create(_logDirectory, role);
        _run.Log.LineWritten += OnLineWritten;

        // 日志行可能来自任意后台线程（accept 循环、discovery、TLS 回调），
        // 所以每次回调都要回到 UI 线程再追加。
        LogPathText.Text = "本轮日志：" + (_run.Log.FilePath ?? "(落不了盘，只有 UI 里这一份)");
        return _run;
    }

    private void OnLineWritten(string line) =>
        Dispatcher.BeginInvoke(new Action<string>(AppendLine), line);

    private void AppendLine(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    private void ShowResult(AcceptanceOutcome outcome, string extra)
    {
        (Brush background, Brush foreground) = outcome switch
        {
            AcceptanceOutcome.Pass =>
                (new SolidColorBrush(Color.FromRgb(0xE6, 0xF4, 0xEA)), Brushes.DarkGreen),
            AcceptanceOutcome.PreconditionUnmet =>
                (new SolidColorBrush(Color.FromRgb(0xFF, 0xF6, 0xE0)), Brushes.DarkOrange),
            _ => (new SolidColorBrush(Color.FromRgb(0xFD, 0xEC, 0xEA)), Brushes.DarkRed),
        };

        ResultBanner.Background = background;
        ResultBanner.Visibility = Visibility.Visible;
        ResultBannerText.Foreground = foreground;
        ResultBannerText.Text = $"本轮结论：{outcome.Describe()}（{outcome.Code()}）。{extra}";
    }

    private void ShowBanner(string text, Brush background, Brush foreground)
    {
        RoleBanner.Background = background;
        RoleBannerText.Foreground = foreground;
        RoleBannerText.Text = text;
    }

    // -----------------------------------------------------------------------
    // 启动自检
    // -----------------------------------------------------------------------
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AcceptanceRun run = BeginRun("info");

        try
        {
            InfoRole.InfoResult info = await InfoRole.RunAsync(run, CancellationToken.None);

            DeviceCodeText.Text = info.DeviceCode;
            CertPinText.Text = info.CertSha256;
            ListenText.Text = info.ListenAddresses.Count == 0
                ? "(无合格 RFC1918 网卡)"
                : string.Join(", ", info.ListenAddresses);

            if (info.Ready)
            {
                ReadyText.Text = "可以参与两机验收";
                ReadyText.Foreground = Brushes.DarkGreen;
                SetRoleButtonsEnabled(true);
            }
            else
            {
                ReadyText.Text = "不能参与：本机没有合格的 RFC1918 私有网卡。"
                    + " 请先用管理员 PowerShell 跑 set-lab-ip.ps1 -Role A（或 -Role B）。";
                ReadyText.Foreground = Brushes.DarkRed;
                SetRoleButtonsEnabled(false);
            }
        }
        catch (Exception ex)
        {
            ReadyText.Text = "自检失败：" + ex.Message;
            ReadyText.Foreground = Brushes.DarkRed;
            run.Log.WriteLine("[FATAL] 自检失败：" + ex);
            SetRoleButtonsEnabled(false);
        }
    }

    private void SetRoleButtonsEnabled(bool enabled)
    {
        HostButton.IsEnabled = enabled;
        ClientButton.IsEnabled = enabled;
    }

    // -----------------------------------------------------------------------
    // 被控端
    // -----------------------------------------------------------------------
    private async void HostButton_Click(object sender, RoutedEventArgs e)
    {
        AcceptanceRun run = BeginRun("host");

        CancellationTokenSource cts = new();
        _runCts = cts;

        // 角色锁：选定被控端后，控制端那组到停机为止都不能再用。
        // 同一台机器既当被控端又当控制端，会让「对端」的语义消失，
        // 测出来的东西无法解释。
        LockRole(lockedToHost: true);

        try
        {
            AcceptanceOutcome outcome = await HostRole.RunAsync(
                run, HostRole.RunUntilCancelled, cts.Token);

            ShowResult(outcome, outcome == AcceptanceOutcome.Pass
                ? "把这一整段日志拷走，它就是被控端的证据；控制端日志要能对上这里的汇总行。"
                : "看日志末尾的 [HOST][SUMMARY] 与 [RESULT]。");
        }
        catch (Exception ex)
        {
            run.Log.WriteLine("[FATAL] 被控端崩溃：" + ex);
            ShowResult(AcceptanceOutcome.HarnessError, "被控端抛出未预期异常，见日志。");
        }
        finally
        {
            UnlockRoles();
            _runCts = null;
        }
    }

    private void StopHostButton_Click(object sender, RoutedEventArgs e)
    {
        _run?.Log.WriteLine("[UI] 用户点了「停止监听」。");
        StopHostButton.IsEnabled = false;

        // 停机会强行关掉 socket，而「连接被关闭」正是若干场景的通过条件。
        // 不整轮作废，就等于用一次按停操作伪造出通过。
        _run?.MarkOperatorAbort("UI 停止监听");
        _runCts?.Cancel();
    }

    // -----------------------------------------------------------------------
    // 控制端
    // -----------------------------------------------------------------------
    private async void ClientButton_Click(object sender, RoutedEventArgs e)
    {
        string peerCode = PeerCodeBox.Text.Trim();

        if (peerCode.Length == 0)
        {
            MessageBox.Show(
                "请填写对端（被控端）的设备码，形如 M5WC-14GX。\n" +
                "它显示在对方窗口顶部的「设备码」一栏。",
                "缺少对端设备码", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (IsSelf(peerCode))
        {
            MessageBox.Show(
                "对端设备码就是本机自己。\n\n" +
                "两机验收必须一台当被控端、另一台当控制端；" +
                "填自己的设备码会让「同一子网校验」「pinning」全部失去意义。",
                "对端不能是本机", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        AcceptanceRun run = BeginRun("client");

        CancellationTokenSource cts = new();
        _runCts = cts;

        LockRole(lockedToHost: false);

        try
        {
            run.Log.WriteLine("[CLIENT] 对端设备码 = " + peerCode);

            AcceptanceOutcome outcome = await ClientRole.RunAllAsync(
                run,
                ClientRole.MandatoryScenarios,
                peerCode,
                address: null,
                pinHex: null,
                DiscoveryConstants.ExpectedTransportPort,
                cts.Token);

            ShowResult(outcome, outcome == AcceptanceOutcome.Pass
                ? "控制端自己只给到 PASS-CLIENT；里程碑是否通过，要拿被控端汇总行核 " +
                  "日志末尾的「两机交叉核对」。"
                : "看日志末尾的 [CLIENT][RESULT] 与「两机交叉核对」。");
        }
        catch (Exception ex)
        {
            run.Log.WriteLine("[FATAL] 控制端崩溃：" + ex);
            run.ReportBackgroundFault("MainWindow 控制端", ex);
            ShowResult(AcceptanceOutcome.HarnessError, "控制端抛出未预期异常，见日志。");
        }
        finally
        {
            UnlockRoles();
            _runCts = null;
        }
    }

    private void StopClientButton_Click(object sender, RoutedEventArgs e)
    {
        _run?.Log.WriteLine("[UI] 用户点了「中止」。");
        StopClientButton.IsEnabled = false;

        // 同被控端：中止会让 socket 被强行关掉，而「被关掉」是若干场景的通过条件。
        _run?.MarkOperatorAbort("UI 中止");
        _runCts?.Cancel();
    }

    /// <summary>本机设备码是否等于用户填的对端设备码。</summary>
    private bool IsSelf(string peerCode)
    {
        string self = DeviceCodeText.Text;

        if (string.IsNullOrWhiteSpace(self) || self.StartsWith("检测", StringComparison.Ordinal))
        {
            return false;
        }

        static string Normalize(string value) =>
            value.Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

        return string.Equals(Normalize(self), Normalize(peerCode), StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // 角色锁
    // -----------------------------------------------------------------------
    private void LockRole(bool lockedToHost)
    {
        HostButton.IsEnabled = false;
        ClientButton.IsEnabled = false;
        PeerCodeBox.IsEnabled = false;

        if (lockedToHost)
        {
            StopHostButton.IsEnabled = true;
            ShowBanner("HOST —— 让这台机器继续监听，不要点别的。被控端本轮的日志就是这里的证据。",
                new SolidColorBrush(Color.FromRgb(0xE3, 0xF2, 0xFD)), Brushes.DarkBlue);
        }
        else
        {
            StopClientButton.IsEnabled = true;
            ShowBanner("CLIENT —— 这台机器在跑测试。四个场景会自动依次跑完，中途别切角色。",
                new SolidColorBrush(Color.FromRgb(0xE6, 0xF4, 0xEA)), Brushes.DarkGreen);
        }

        ResultBanner.Visibility = Visibility.Collapsed;
    }

    private void UnlockRoles()
    {
        HostButton.IsEnabled = true;
        ClientButton.IsEnabled = true;
        PeerCodeBox.IsEnabled = true;
        StopHostButton.IsEnabled = false;
        StopClientButton.IsEnabled = false;

        ShowBanner("本轮结束。要换角色请直接点另一组；换角色会开一轮新的运行（新 Run ID、新日志文件）。",
            new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE)), Brushes.DimGray);
    }

    // -----------------------------------------------------------------------
    // 日志工具
    // -----------------------------------------------------------------------
    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(LogBox.Text);
            MessageBox.Show(
                "已把整段日志复制到剪贴板。\n\n" +
                "两机验收要两份：这一份，加上对端那台机器的。",
                "复制完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("复制失败：" + ex.Message + "\n\n可以直接打开日志目录取文件。",
                "复制失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        string? path = Directory.Exists(_logDirectory) ? _logDirectory : null;

        if (path is null)
        {
            MessageBox.Show("日志目录还不存在。", "打开日志目录",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开失败：" + ex.Message, "打开日志目录",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
