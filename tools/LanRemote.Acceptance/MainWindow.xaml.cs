using System.Diagnostics;
using System.IO;
using System.Windows;
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
/// </remarks>
public partial class MainWindow : Window
{
    private readonly AcceptanceLog _log;
    private CancellationTokenSource? _runCts;

    public MainWindow()
    {
        InitializeComponent();

        string directory = Path.Combine(Path.GetTempPath(), "lanremote-m3-acceptance");
        _log = new AcceptanceLog(directory, "gui.log");

        // 日志行可能来自任意后台线程（accept 循环、discovery、TLS 回调），
        // 所以每次回调都要回到 UI 线程再追加。
        _log.LineWritten += line =>
        {
            Dispatcher.BeginInvoke(new Action<string>(AppendLine), line);
        };

        LogPathText.Text = "日志目录：" + directory;
        Loaded += OnLoaded;

        // 渲染完成 = WPF 排版/字体缓存真的跑通了。
        // 这一行是「窗口没崩」的证据，不是装饰：
        // WinExe 没有控制台，若 Measure 阶段抛异常，用户只会看到窗口一闪就没了。
        ContentRendered += OnContentRendered;
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnContentRendered;
        _log.WriteLine($"[GUI] 窗口渲染完成。 ActualWidth={ActualWidth} ActualHeight={ActualHeight}");
    }

    private void AppendLine(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    // -----------------------------------------------------------------------
    // 启动自检
    // -----------------------------------------------------------------------
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            InfoRole.InfoResult info = await InfoRole.RunAsync(_log, CancellationToken.None);

            DeviceCodeText.Text = info.DeviceCode;
            CertPinText.Text = info.CertSha256;
            ListenText.Text = info.ListenAddresses.Count == 0
                ? "(无合格 RFC1918 网卡)"
                : string.Join(", ", info.ListenAddresses);

            if (info.Ready)
            {
                ReadyText.Text = "可以参与两机验收";
                ReadyText.Foreground = System.Windows.Media.Brushes.DarkGreen;
                SetRoleButtonsEnabled(true);
            }
            else
            {
                ReadyText.Text = "不能参与：本机没有合格的 RFC1918 私有网卡。"
                    + " 请先用管理员 PowerShell 跑 set-lab-ip.ps1 -Role A（或 -Role B）。";
                ReadyText.Foreground = System.Windows.Media.Brushes.DarkRed;
                SetRoleButtonsEnabled(false);
            }
        }
        catch (Exception ex)
        {
            ReadyText.Text = "自检失败：" + ex.Message;
            ReadyText.Foreground = System.Windows.Media.Brushes.DarkRed;
            _log.WriteLine("[FATAL] 自检失败：" + ex);
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
        CancellationTokenSource cts = new();
        _runCts = cts;

        HostButton.IsEnabled = false;
        ClientButton.IsEnabled = false;
        StopHostButton.IsEnabled = true;

        try
        {
            _log.WriteLine("===== 被控端开始监听 " + DateTime.Now.ToString("HH:mm:ss") + " =====");
            await HostRole.RunAsync(_log, HostRole.RunUntilCancelled, cts.Token);
        }
        catch (Exception ex)
        {
            _log.WriteLine("[FATAL] 被控端崩溃：" + ex);
        }
        finally
        {
            StopHostButton.IsEnabled = false;
            HostButton.IsEnabled = true;
            ClientButton.IsEnabled = true;
            _runCts = null;
        }
    }

    private void StopHostButton_Click(object sender, RoutedEventArgs e)
    {
        _log.WriteLine("[UI] 用户点了「停止监听」。");
        StopHostButton.IsEnabled = false;
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

        CancellationTokenSource cts = new();
        _runCts = cts;

        HostButton.IsEnabled = false;
        ClientButton.IsEnabled = false;
        StopClientButton.IsEnabled = true;

        try
        {
            _log.WriteLine("===== 控制端开始验收 " + DateTime.Now.ToString("HH:mm:ss") +
                           " 对端=" + peerCode + " =====");

            int passed = 0;
            int total = ClientRole.MandatoryScenarios.Length;

            foreach (string scenario in ClientRole.MandatoryScenarios)
            {
                if (cts.IsCancellationRequested)
                {
                    break;
                }

                _log.WriteLine("");
                _log.WriteLine("----- 场景 " + scenario + " -----");

                int code = await ClientRole.RunAsync(
                    _log,
                    scenario,
                    peerCode,
                    null,
                    null,
                    DiscoveryConstants.ExpectedTransportPort,
                    cts.Token);

                if (code == 0)
                {
                    passed++;
                }

                _log.WriteLine($"----- 场景 {scenario} 结束，退出码 {code} " +
                               $"({code switch { 0 => "符合预期", 1 => "不符合预期", _ => "前置条件不满足" }}) -----");
            }

            _log.WriteLine("");
            _log.WriteLine($"===== 控制端汇总：{passed}/{total} 个场景符合预期 =====");

            MessageBox.Show(
                $"三个场景跑完，{passed}/{total} 个符合预期。\n\n" +
                "请把「被控端」那台机器的日志也一起拷回来——\n" +
                "两个场景的判定需要两边对得上（详见日志末尾的说明）。",
                "验收结束", MessageBoxButton.OK,
                passed == total ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            _log.WriteLine("[FATAL] 控制端崩溃：" + ex);
        }
        finally
        {
            StopClientButton.IsEnabled = false;
            HostButton.IsEnabled = true;
            ClientButton.IsEnabled = true;
            _runCts = null;
        }
    }

    private void StopClientButton_Click(object sender, RoutedEventArgs e)
    {
        _log.WriteLine("[UI] 用户点了「中止」。");
        StopClientButton.IsEnabled = false;
        _runCts?.Cancel();
    }

    // -----------------------------------------------------------------------
    // 日志工具
    // -----------------------------------------------------------------------
    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_log.All);
            MessageBox.Show("已把整段日志复制到剪贴板。", "复制完成",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("复制失败：" + ex.Message + "\n\n可以直接打开日志目录取文件。",
                "复制失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Clear();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        string? path = _log.FilePath is null ? null : Path.GetDirectoryName(_log.FilePath);
        if (path is null || !Directory.Exists(path))
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
