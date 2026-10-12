using System.Windows;
using LanRemote.Transport;

namespace LanRemote.App;

/// <summary>只收集本次远端密钥，并展示尚未认证的审批等待状态。</summary>
public partial class RemoteConnectDialog : Window
{
    private readonly TaskCompletionSource _keyReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _remoteKey;

    public RemoteConnectDialog(ConnectionTarget target, string? remoteKey = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        InitializeComponent();
        TargetText.Text = $"目标设备：{target.DeviceId}  ·  {target.RemoteAddress}:{target.Port}";
        if (remoteKey is not null)
        {
            _remoteKey = remoteKey;
            KeyPanel.Visibility = Visibility.Collapsed;
            StatusText.Text = "正在连接远端设备（仅查看）…";
            _keyReady.TrySetResult();
        }
    }

    public Task WaitForKeyAsync(CancellationToken cancellationToken) =>
        _keyReady.Task.WaitAsync(cancellationToken);

    /// <summary>只取一次；不会把密钥留在窗口字段或完成任务的结果里。</summary>
    public string? TakeRemoteKey()
    {
        Dispatcher.VerifyAccess();
        string? key = _remoteKey;
        _remoteKey = null;
        return key;
    }

    public void ShowConnecting() => StatusText.Text = "正在验证远端身份并连接视频（仅查看）…";

    public void ShowApprovalPending(string shortCode) =>
        StatusText.Text = $"等待远端本机审批；核对短码 {shortCode}。尚未认证，也尚未连接。";

    private void OnConnectClick(object sender, RoutedEventArgs e)
    {
        _remoteKey = RemoteKeyBox.Password;
        RemoteKeyBox.Clear();
        KeyPanel.Visibility = Visibility.Collapsed;
        ShowConnecting();
        _keyReady.TrySetResult();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        RemoteKeyBox.Clear();
        _remoteKey = null;
        _keyReady.TrySetResult();
    }
}
