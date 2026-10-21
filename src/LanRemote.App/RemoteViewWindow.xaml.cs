using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LanRemote.Core.Models;
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
    private Func<DisplayId, CancellationToken, Task<DisplayChangedFrame?>>? _requestSwitch;
    private CancellationToken _switchStop;
    private bool _switchStarted;
    private bool _switchStopped;
    private bool _switching;

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
        && _catalogStop == default && DisplayCatalogItems.ItemsSource is null
        && _switchStopped && _requestSwitch is null && _switchStop == default;

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

    /// <summary>
    /// 目录绑定后启用切屏请求；请求回调由 Client 服务提供，真实发送与回执等待都在 Transport 会话内。
    /// 回调不得反等本窗口；忙状态只防 UI 重复点击，不替代会话侧单飞门禁。
    /// </summary>
    internal void StartDisplaySwitching(
        Func<DisplayId, CancellationToken, Task<DisplayChangedFrame?>> requestSwitch, CancellationToken stop)
    {
        Dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(requestSwitch);
        if (!_catalogStarted || _catalogStopped || _switchStarted || _switchStopped || _closing || _sessionEnded)
            throw new InvalidOperationException("显示切换只能在目录显示绑定后启用一次。");
        _switchStarted = true;
        _requestSwitch = requestSwitch;
        _switchStop = stop;
        RefreshSwitchButtons();
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
        if (!ReferenceEquals(snapshot, _displayCatalog))
        {
            _displayCatalog = snapshot;
            DisplayCatalogItems.ItemsSource = snapshot?.Displays;
            CatalogStatusText.Text = snapshot is null
                ? "等待远端屏幕目录（可请求切换屏幕；回执不等于物理输出）。"
                : $"远端屏幕目录 · {snapshot.Displays.Count} 个；可请求切换，回执不等同物理输出。";
        }
        // 容器在布局后才生成；同一快照的后续 Tick 也补刷按钮可用性。
        RefreshSwitchButtons();
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
        // 切屏绑定与目录同一时机解绑；在途请求的 await 结束后只写状态文本，不能再发起新请求。
        _switchStopped = true;
        _requestSwitch = null;
        _switching = false;
        _switchStop = default;
        SwitchStatusText.Text = "切换：已随目录解除绑定。";
        RefreshSwitchButtons();
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

    private void OnSwitchDisplayClick(object sender, RoutedEventArgs e)
    {
        // 按钮只转交内部核心；所有拒绝、忙状态与异常都在核心内归并为状态文本。
        if (sender is not Button { DataContext: DisplayInfo display }) return;
        _ = RequestDisplaySwitchAsync(display.Id);
    }

    /// <summary>UI线程内请求切换；返回真实回执或 null。同步拒绝与异步失败都不向调用线程抛未观察异常。</summary>
    internal async Task<DisplayChangedFrame?> RequestDisplaySwitchAsync(DisplayId displayId)
    {
        Dispatcher.VerifyAccess();
        Func<DisplayId, CancellationToken, Task<DisplayChangedFrame?>>? request = _requestSwitch;
        if (_switchStopped || _closing || _sessionEnded || _switching || request is null ||
            _switchStop.IsCancellationRequested || !ContainsDisplay(displayId))
        {
            SwitchStatusText.Text = "当前不能请求切换屏幕。";
            return null;
        }

        _switching = true;
        RefreshSwitchButtons();
        try
        {
            SwitchStatusText.Text = $"正在请求切换屏幕 {displayId.Value}…";
            DisplayChangedFrame? receipt = await request(displayId, _switchStop).ConfigureAwait(true);
            SwitchStatusText.Text = receipt is null
                ? "切换未完成：远端未确认新画面。"
                : $"已收到切换回执：屏幕 {receipt.DisplayId.Value} · 首帧 {receipt.FirstFrameId} · " +
                  $"{receipt.Width}×{receipt.Height}（回执不代表物理输出）。";
            return receipt;
        }
        catch (Exception error)
        {
            // 同步拒绝（目标不在目录、会话不可用、重复请求）不断开连接；其余失败可能已随发送停止会话。
            SwitchStatusText.Text = error switch
            {
                OperationCanceledException => "切换请求已取消。",
                InvalidOperationException or ArgumentOutOfRangeException or ObjectDisposedException =>
                    "切换请求被拒绝：目标不在当前目录或会话暂不可用。",
                _ => "切换请求失败；会话可能已断开。",
            };
            return null;
        }
        finally
        {
            _switching = false;
            RefreshSwitchButtons();
        }
    }

    private bool ContainsDisplay(DisplayId displayId)
    {
        DisplayCatalogSnapshot? snapshot = _displayCatalog;
        if (snapshot is null) return false;
        foreach (DisplayInfo display in snapshot.Displays)
        {
            if (display.Id == displayId) return true;
        }
        return false;
    }

    // 目录项容器在布局后才生成；缺失容器只是暂无可刷新按钮，真正的门禁仍在请求核心内。
    private void RefreshSwitchButtons()
    {
        bool enabled = _requestSwitch is not null && !_switching && !_switchStopped && !_closing &&
            !_sessionEnded && !_switchStop.IsCancellationRequested && DisplayCatalogItems.ItemsSource is not null;
        if (DisplayCatalogItems.ItemsSource is null) return;
        for (int index = 0; index < DisplayCatalogItems.Items.Count; index++)
        {
            if (DisplayCatalogItems.ItemContainerGenerator.ContainerFromIndex(index) is not { } container) continue;
            if (FindVisualChild<Button>(container) is { } button) button.IsEnabled = enabled;
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
