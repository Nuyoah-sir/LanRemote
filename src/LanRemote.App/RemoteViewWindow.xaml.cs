using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using LanRemote.Transport;

namespace LanRemote.App;

/// <summary>只有解码后的视频画面与断开入口；没有键鼠传输接口。</summary>
public partial class RemoteViewWindow : Window
{
    private Action? _requestDisconnect;
    private bool _closing;
    private bool _allowClose;
    private bool _sessionEnded;

    public RemoteViewWindow(ConnectionTarget target, Action requestDisconnect)
    {
        ArgumentNullException.ThrowIfNull(target);
        _requestDisconnect = requestDisconnect ?? throw new ArgumentNullException(nameof(requestDisconnect));
        InitializeComponent();
        Title = $"远端屏幕（仅查看） · {target.RemoteAddress}";
    }

    public Image VideoImage => RemoteImage;

    internal bool DisconnectHandlerReleased => _sessionEnded && _requestDisconnect is null && RemoteImage.Source is null;

    public void ShowStatus(string text)
    {
        Dispatcher.VerifyAccess();
        StatusText.Text = text;
    }

    /// <summary>停止后先保留错误在窗口中；用户明确关闭才移除诊断。</summary>
    public void SessionEnded(string status, bool hasError)
    {
        Dispatcher.VerifyAccess();
        _sessionEnded = true;
        RemoteImage.Source = null;
        _requestDisconnect = null;
        StatusText.Text = status;
        if (hasError)
        {
            DisconnectButton.Content = "关闭窗口";
            DisconnectButton.IsEnabled = true;
        }
        else
        {
            _allowClose = true;
            Close();
        }
    }

    private void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        if (_sessionEnded)
        {
            _allowClose = true;
        }
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || _sessionEnded) return;

        e.Cancel = true;
        if (_closing) return;

        _closing = true;
        DisconnectButton.IsEnabled = false;
        StatusText.Text = "正在断开并等待两条连接及帧处理完整结束…";
        // 仅发出取消请求，不等待包含本窗口 UI 收尾的 Completion；service join 后调用 SessionEnded。
        _requestDisconnect?.Invoke();
    }
}
