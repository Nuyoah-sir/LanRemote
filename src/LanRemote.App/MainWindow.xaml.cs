using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LanRemote.App.Services;
using LanRemote.App.ViewModels;
using LanRemote.Core.Models;
using LanRemote.Transport;
using Microsoft.Extensions.Logging;

namespace LanRemote.App;

/// <summary>
/// 主窗口。
/// </summary>
/// <remarks>
/// <para>M9 之前这里是唯一的窗口；M8 之后每个远端会话为独立 <c>SessionWindow</c>，互不阻塞。</para>
/// <para>窗口只依赖 ViewModel，负责一切 WPF 专有动作（剪贴板、消息框、DispatcherTimer）。
/// 这样 ViewModel 里不存在任何 view-only 的 API 调用。</para>
/// <para>剪贴板策略见 04_PROTOCOL_AND_SECURITY.md 第 8 节：复制密钥后 30 秒尝试清理，
/// 且只有剪贴板内容<b>仍然等于</b>那份密钥时才清，避免覆盖用户后来复制的内容。</para>
/// </remarks>
public partial class MainWindow : Window
{
    private const int ClipboardClearDelaySeconds = 30;

    private DispatcherTimer? _clipboardClearTimer;
    private DispatcherTimer? _hostProtocolTimer;
    private bool _closed;
    private readonly ClientConnectService _client;
    private readonly ILogger<MainWindow> _logger;
    private readonly CancellationTokenSource _loadStop = new();
    private Task? _loadTask;
    private Task? _stopLoadingTask;
    private bool _drainingBeforeClose;
    private bool _allowClose;

    /// <summary>由 DI 注入的 ViewModel。</summary>
    public MainViewModel ViewModel { get; }

    /// <summary>构造主窗口。</summary>
    /// <param name="viewModel">主窗口 ViewModel。</param>
    public MainWindow(MainViewModel viewModel, ClientConnectService client, ILogger<MainWindow> logger)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        DataContext = ViewModel;
        InitializeComponent();
    }

    /// <summary>向关闭协调者交付仍在途的首次身份/发现加载原任务。</summary>
    public Task StopLoadingAsync() => _stopLoadingTask ??= StopLoadingCoreAsync();

    private async Task StopLoadingCoreAsync()
    {
        // 首次取消与首次等待发布成同一任务；重试关闭不得二次访问已释放的 CTS。
        Task cancel = _loadStop.CancelAsync();
        Task load = _loadTask ?? Task.CompletedTask;
        Task joined = Task.WhenAll(cancel, load);
        try
        {
            await joined.ConfigureAwait(true);
        }
        finally
        {
            if (joined.IsCompleted) _loadStop.Dispose();
        }
    }

    /// <summary>原始首次加载任务由主窗持有，关闭必须先取消、再等待它真正退出。</summary>
    private void MainWindow_OnContentRendered(object? sender, EventArgs e)
    {
        if (!_closed && _stopLoadingTask is null && !_loadStop.IsCancellationRequested)
        {
            if (_hostProtocolTimer is null)
            {
                _hostProtocolTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                {
                    Interval = TimeSpan.FromSeconds(1),
                };
                _hostProtocolTimer.Tick += OnHostProtocolTick;
                _hostProtocolTimer.Start();
                ViewModel.RefreshHostProtocolStatus();
            }
            _loadTask ??= LoadAndReportAsync();
        }
    }

    private void OnHostProtocolTick(object? sender, EventArgs e) => ViewModel.RefreshHostProtocolStatus();

    private async Task LoadAndReportAsync()
    {
        try
        {
            await ViewModel.LoadAsync(_loadStop.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_loadStop.IsCancellationRequested)
        {
            // 关窗取消后，加载不得继续启动发现或监听。
        }
        catch (Exception error)
        {
            // 不回显可能含秘密的异常 Message；保留原错误在 _loadTask，退出协调不可将其洗绿。
            ViewModel.StatusText = "初始化失败；原任务故障，退出前须核查本机日志。";
            _logger.LogError("主窗身份与发现初始化意外失败，异常类型={ErrorType}。",
                error.GetType().FullName);
            throw;
        }
    }

    private async void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (_drainingBeforeClose)
        {
            return;
        }

        _drainingBeforeClose = true;
        try
        {
            App application = (App)Application.Current;
            await application.StopBeforeExitAsync().ConfigureAwait(true);
            _allowClose = true;
            Close();
            application.ShutdownAfterDrain();
        }
        catch (Exception)
        {
            ViewModel.StatusText = "未排空，不安全关闭；请查看本机日志并重试。";
        }
        finally
        {
            _drainingBeforeClose = false;
        }
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        if (_hostProtocolTimer is not null)
        {
            _hostProtocolTimer.Stop();
            _hostProtocolTimer.Tick -= OnHostProtocolTick;
            _hostProtocolTimer = null;
        }
        if (_clipboardClearTimer is not null)
        {
            _clipboardClearTimer.Stop();
            _clipboardClearTimer = null;
        }
    }

    private async void StartViewOnlySharing_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.EnableViewOnlySharingAsync(useClearerPreview: ViewModel.UseClearerPreview,
                useSmootherPreview: ViewModel.UseSmootherPreview,
                useFastPreview: ViewModel.UseFastPreview,
                useThirtyFpsPreview: ViewModel.UseThirtyFpsPreview,
                useManualVideoQuality: ViewModel.UseManualVideoQuality,
                manualScale: ViewModel.ManualPreviewScale,
                manualJpegQuality: ViewModel.ManualJpegQuality).ConfigureAwait(true);
        }
        catch (Exception)
        {
            ViewModel.StatusText = "仅查看共享未能开启，请查看本机日志。";
        }
    }

    private async void StopSharing_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.StopViewOnlySharingAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            ViewModel.StatusText = "共享停止未排空；请查看本机日志。";
        }
    }

    private async void ConnectViewOnly_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DiscoveredDevice device })
        {
            return;
        }
        // 用户点击时冻结未认证 discovery 的 GUID/IP/端口/完整指纹；后续不能重查缓存。
        if (!ConnectionTarget.TryCreate(device, out ConnectionTarget? target) || target is null)
        {
            ViewModel.StatusText = "发现信息无效；请刷新设备列表后重试。";
            return;
        }
        try
        {
            await _client.ConnectAsync(target, this).ConfigureAwait(true);
            ViewModel.StatusText = _client.LastStatus;
        }
        catch (Exception)
        {
            // 客户端内部保留原异常任务；界面不展示异常 Message 或远端访问密钥。
            ViewModel.StatusText = _client.LastStatus;
        }
    }

    private async void ToggleAccessKey_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            bool visible = await ViewModel.ToggleAccessKeyVisibilityAsync().ConfigureAwait(true);
            ViewModel.StatusText = visible
                ? "访问密钥已显示，请留意周围屏幕。"
                : "访问密钥已隐藏。";
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"无法显示访问密钥：{ex.Message}";
        }
    }

    private async void CopyAccessKey_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string key = await ViewModel.GetAccessKeyForClipboardAsync().ConfigureAwait(true);
            Clipboard.SetText(key);

            ViewModel.StatusText = $"访问密钥已复制到剪贴板，{ClipboardClearDelaySeconds} 秒后自动尝试清除。";
            ScheduleClipboardClear(key);
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"复制失败：{ex.Message}";
        }
    }

    private void CopyDeviceCode_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ViewModel.DeviceCode);
            ViewModel.StatusText = "设备码已复制到剪贴板。";
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"复制失败：{ex.Message}";
        }
    }

    private async void RefreshDevices_OnClick(object sender, RoutedEventArgs e)
    {
        // 只触发一次 discovery probe：不清除身份、不重读 DPAPI、不重建证书、不重启 socket。
        await ViewModel.RefreshAsync().ConfigureAwait(true);
    }

    private async void RegenerateAccessKey_OnClick(object sender, RoutedEventArgs e)
    {
        MessageBoxResult answer = MessageBox.Show(
            this,
            "重新生成后，持有旧访问密钥的设备将无法连接。\n是否继续？",
            "确认重新生成访问密钥",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await ViewModel.RegenerateAccessKeyAsync().ConfigureAwait(true);
    }

    private void ScheduleClipboardClear(string copiedValue)
    {
        _clipboardClearTimer?.Stop();
        _clipboardClearTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(ClipboardClearDelaySeconds),
        };
        _clipboardClearTimer.Tick += (_, _) =>
        {
            _clipboardClearTimer?.Stop();
            _clipboardClearTimer = null;
            ClearClipboardIfUnchanged(copiedValue);
        };
        _clipboardClearTimer.Start();
    }

    private static void ClearClipboardIfUnchanged(string expected)
    {
        try
        {
            if (Clipboard.ContainsText()
                && string.Equals(Clipboard.GetText(), expected, StringComparison.Ordinal))
            {
                Clipboard.Clear();
            }
        }
        catch (Exception)
        {
            // 剪贴板被其他进程占用时不处理：清理是尽力而为的动作，失败不影响主流程。
        }
    }
}
