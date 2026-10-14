using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LanRemote.Transport;

namespace LanRemote.App;

/// <summary>解码后的视频、只读目录与断开入口；没有选屏或键鼠传输接口。</summary>
public partial class RemoteViewWindow : Window
{
    private Action? _requestDisconnect;
    private bool _closing;
    private bool _allowClose;
    private bool _sessionEnded;
    private bool _catalogStarted;
    private bool _catalogStopped;
    private DispatcherTimer? _catalogTimer;
    private Func<DisplayCatalogSnapshot?>? _readCatalog;
    private DisplayCatalogSnapshot? _displayCatalog;
    private CancellationToken _catalogStop;

    public RemoteViewWindow(ConnectionTarget target, Action requestDisconnect)
    {
        ArgumentNullException.ThrowIfNull(target);
        _requestDisconnect = requestDisconnect ?? throw new ArgumentNullException(nameof(requestDisconnect));
        InitializeComponent();
        Title = $"远端屏幕（仅查看） · {target.RemoteAddress}";
    }

    public Image VideoImage => RemoteImage;

    internal bool DisconnectHandlerReleased => _sessionEnded && _requestDisconnect is null && RemoteImage.Source is null
        && _catalogStopped && _catalogTimer is null && _readCatalog is null && _displayCatalog is null
        && _catalogStop == default && DisplayCatalogItems.ItemsSource is null;

    /// <summary>每窗口仅绑定一次父会话；一个UI计时器读取单槽，不注册网络读者或排队后台更新。</summary>
    internal void StartDisplayCatalog(Func<DisplayCatalogSnapshot?> readCatalog, CancellationToken stop)
    {
        Dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(readCatalog);
        if (_catalogStarted || _catalogStopped || _closing || _sessionEnded)
            throw new InvalidOperationException("目录显示不可重复绑定或在停止后重启。");
        _catalogStarted = true;
        _readCatalog = readCatalog;
        _catalogStop = stop;
        _catalogTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _catalogTimer.Tick += OnCatalogTick;
        OnCatalogTick(null, EventArgs.Empty);
        _catalogTimer?.Start();
    }

    private void OnCatalogTick(object? sender, EventArgs e)
    {
        Dispatcher.VerifyAccess();
        if (_catalogStopped || _closing || _sessionEnded || _catalogStop.IsCancellationRequested)
        {
            StopDisplayCatalog();
            return;
        }
        DisplayCatalogSnapshot? snapshot = _readCatalog?.Invoke();
        // 取值过程中停止可能已发生；历史快照是数据而非资格，不能凭非空结果恢复已停止UI。
        if (_catalogStopped || _closing || _sessionEnded || _catalogStop.IsCancellationRequested)
        {
            StopDisplayCatalog();
            return;
        }
        if (ReferenceEquals(snapshot, _displayCatalog)) return;
        _displayCatalog = snapshot;
        DisplayCatalogItems.ItemsSource = snapshot?.Displays;
        CatalogStatusText.Text = snapshot is null
            ? "等待远端屏幕目录（只读，尚不支持切换屏幕）。"
            : $"远端屏幕目录 · {snapshot.Displays.Count} 个（只读，非当前视频选屏状态）";
    }

    /// <summary>在等待慢网络/帧处理排空前移除目录与绑定；已排队的Tick也不能恢复目录。</summary>
    internal void StopDisplayCatalog()
    {
        Dispatcher.VerifyAccess();
        _catalogStopped = true;
        if (_catalogTimer is { } timer)
        {
            timer.Stop();
            timer.Tick -= OnCatalogTick;
            _catalogTimer = null;
        }
        _readCatalog = null;
        _displayCatalog = null;
        _catalogStop = default;
        DisplayCatalogItems.ItemsSource = null;
        CatalogStatusText.Text = "会话目录已清除。";
    }

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
        StopDisplayCatalog();
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
        StopDisplayCatalog();
        DisconnectButton.IsEnabled = false;
        StatusText.Text = "正在断开并等待两条连接及帧处理完整结束…";
        // 仅发出取消请求，不等待包含本窗口 UI 收尾的 Completion；service join 后调用 SessionEnded。
        _requestDisconnect?.Invoke();
    }
}
