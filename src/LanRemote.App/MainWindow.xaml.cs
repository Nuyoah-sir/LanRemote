using System.Windows;
using System.Windows.Threading;
using LanRemote.App.ViewModels;

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

    /// <summary>由 DI 注入的 ViewModel。</summary>
    public MainViewModel ViewModel { get; }

    /// <summary>构造主窗口。</summary>
    /// <param name="viewModel">主窗口 ViewModel。</param>
    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = ViewModel;
        InitializeComponent();
    }

    /// <summary>
    /// 内容渲染完成后异步加载配置与本机身份。
    /// </summary>
    /// <remarks>
    /// 这是事件 handler，因此允许 <c>async void</c>（06_DEV_STANDARDS.md 第 2 节）。
    /// 内部异常必须被吞掉并提示，不能让 UI 线程炸掉。
    /// 错误信息只显示异常 Message，不含任何秘密；完整堆栈进日志。
    /// </remarks>
    private async void MainWindow_OnContentRendered(object? sender, EventArgs e)
    {
        try
        {
            await ViewModel.LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"初始化失败：{ex.Message}";
        }
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        if (_clipboardClearTimer is not null)
        {
            _clipboardClearTimer.Stop();
            _clipboardClearTimer = null;
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
