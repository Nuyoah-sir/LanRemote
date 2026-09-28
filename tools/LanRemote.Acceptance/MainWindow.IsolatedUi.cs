using System.Windows;
using System.Windows.Media;

namespace LanRemote.Acceptance;

public partial class MainWindow
{
    private const string IsolatedScope = "仅本机隔离专项，不等于 M4 整体通过；仍需两机验收证据。";

    private void InitializeIsolatedUi()
    {
        Title = "LanRemote 本机隔离专项";
        WindowHeading.Text = "隔离模式 —— 本机专项，不等于 M4 整体通过";
        WindowDescription.Text = "仅 127.0.0.1、临时身份与独立日志；不读取真实 vault、发现或配置。选择专项后才监听。";
        ModeBanner.Background = Brushes.Cornsilk;
        ModeBanner.Padding = new Thickness(8, 4, 8, 4);
        IdentityPanel.Visibility = Visibility.Collapsed;
        IdentityPanel.IsEnabled = false;
        NormalRolePanel.Visibility = Visibility.Collapsed;
        NormalRolePanel.IsEnabled = false;
        HostButton.Visibility = Visibility.Collapsed;
        IsolatedActiveStopButton.Visibility = Visibility.Visible;
        IsolatedProofMismatchButton.Visibility = Visibility.Visible;
        StopHostButton.Content = "停止本机专项";
        HostWindowText.Text = "活动专项在认证后观察最长 120 秒；proof 错误专项保持 FAIL，不转换为通过。";
        ShowBanner("尚未启动监听。请选择专项；待批请求必须手动选中并审批。", Brushes.AliceBlue, Brushes.DarkBlue);
        LogPathText.Text = "隔离日志：" + _logDirectory;
        // 为真实审批、pending 和结论保留空间；普通窗口的布局预算不变。
        ContentGrid.RowDefinitions[1].Height = GridLength.Auto;
        ContentGrid.RowDefinitions[3].MinHeight = 80;
        LogPathText.MaxWidth = 420;
    }

    private void IsolatedActiveStopButton_Click(object sender, RoutedEventArgs e) =>
        StartIsolatedScenario(IsolatedUiCase.ActiveHostStop);

    private void IsolatedProofMismatchButton_Click(object sender, RoutedEventArgs e) =>
        StartIsolatedScenario(IsolatedUiCase.ServerProofMismatch);

    private void StartIsolatedScenario(IsolatedUiCase scenario)
    {
        if (!_isolatedUi || !CanStart) { return; }
        try
        {
            // 两个专项均拥有真实 Host 收件箱，不给普通 Client 添加审批入口。
            RunState state = StartRole(isHost: true);
            // 不传调度取消令牌；委托必须进入同一 worker 的停止、清理和结果链。
            _runTask = Task.Run(() => RunRoleWorkerAsync(state, () => IsolatedUiScenario.RunAsync(
                state.Run, state.Inbox!, scenario, state.PublishPending, state.PublishIsolatedStatus, state.Cts.Token)));
        }
        catch (Exception)
        {
            HandleDispatcherFault();
        }
    }

    private void RefreshIsolatedStatus()
    {
        if (_isolatedUi && !_closing && !_faulted && _activeRun is { IsStopped: false } state
            && _runTask is { IsCompleted: false } && state.GetIsolatedStatus() is { } status)
        {
            ShowBanner(status, Brushes.AliceBlue, Brushes.DarkBlue);
        }
    }
}
