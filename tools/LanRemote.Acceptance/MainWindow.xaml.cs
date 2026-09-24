using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using LanRemote.Core.Models;
using LanRemote.Discovery;
using LanRemote.Security.Secrets;
using LanRemote.Transport;

namespace LanRemote.Acceptance;

/// <summary>WinExe 双击入口；UI 只拉取快照，角色和密钥工作均有可回收的任务。</summary>
public partial class MainWindow : Window
{
    private const int HostWindowSeconds = 180;
    private const int LogBatchSize = 200;
    private const int UiLogCharacterLimit = 160_000;
    private readonly string _logDirectory = AcceptanceLog.DefaultDirectory;
    private readonly AcceptanceLog _processLog = new(AcceptanceLog.DefaultDirectory, "gui.log");
    private readonly List<LogCursor> _logs = new();
    private readonly DispatcherTimer _uiTimer;
    private int _nextLog;
    private AcceptanceRun? _run;
    private RunState? _activeRun;
    private CancellationTokenSource? _runCts;
    private Task<AcceptanceOutcome>? _runTask;
    private Task? _runCancellationTask;
    private Task? _busyTask;
    private bool _ready;
    private bool _busy;
    private bool _faulted;
    private bool _closing;
    private bool _allowClose;
    private long _closeStarted;

    public MainWindow()
    {
        InitializeComponent();
        _logs.Add(new LogCursor(_processLog));
        _uiTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _uiTimer.Tick += OnUiTick;
        Loaded += OnLoaded;
        ContentRendered += OnContentRendered;
        Deactivated += OnDeactivated;
        Closing += OnClosing;
        Closed += OnClosed;
        UpdateButtons();
        _uiTimer.Start();
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnContentRendered;
        _processLog.WriteLine($"[GUI] M4 窗口渲染完成。ActualWidth={ActualWidth} ActualHeight={ActualHeight}");
    }

    private bool CanStart => !_busy && !_closing && !_faulted && _activeRun is null && _runTask is null && _keyTask is null;

    private AcceptanceRun BeginRun(string role)
    {
        _run = AcceptanceRun.Create(_logDirectory, role);
        _logs.Add(new LogCursor(_run.Log));
        LogPathText.Text = "本轮日志：" + (_run.Log.FilePath ?? "未落盘；证据无效，内存日志仍可复制");
        return _run;
    }

    private void OnUiTick(object? sender, EventArgs e)
    {
        try
        {
            RefreshAuthentication();
            PullLogs();
            UpdateButtons();
        }
        catch (Exception) { HandleDispatcherFault(); }
        finally
        {
            // 渲染/日志失败不能跳过回收；任务是否结束不以日志游标是否追平来判定。
            ReapCompletedTasks();
        }
        try
        {
            if (_closing)
            {
                if (_activeRun is null && _runTask is null && _keyTask is null && !_busy)
                {
                    _allowClose = true;
                    Close();
                }
                else if (Stopwatch.GetElapsedTime(_closeStarted) >= TimeSpan.FromSeconds(8))
                {
                    ShowBanner("关闭等待已超过 8 秒；任务仍未收回，窗口保持禁用。不强杀、不宣称完成，收回后自动关闭。",
                        Brushes.MistyRose, Brushes.DarkRed);
                }
            }
        }
        catch (Exception)
        {
            HandleDispatcherFault();
        }
    }

    private void ReapCompletedTasks()
    {
        if (_keyTask is { IsCompleted: true } keyTask)
        {
            _keyTask = null;
            try { keyTask.GetAwaiter().GetResult(); }
            catch (Exception) { HandleDispatcherFault(); }
        }
        if (_busyTask is { IsCompleted: true } busyTask)
        {
            _busyTask = null;
            _busy = false;
            try { busyTask.GetAwaiter().GetResult(); }
            catch (Exception) { HandleDispatcherFault(); }
        }
        if (_runTask is { IsCompleted: true } && _keyTask is null
            && (_runCancellationTask is null || _runCancellationTask.IsCompleted))
        {
            try { FinishRole(); }
            catch (Exception) { HandleDispatcherFault(); }
        }
    }

    private void PullLogs()
    {
        int remaining = LogBatchSize;
        StringBuilder batch = new();
        // 所有旧 run 都保留游标，换轮不会丢掉未显示的行；每 tick 总量有限。
        for (int visited = 0; visited < _logs.Count && remaining > 0; visited++)
        {
            LogCursor cursor = _logs[_nextLog];
            _nextLog = (_nextLog + 1) % _logs.Count;
            IReadOnlyList<string> lines = cursor.Log.ReadFrom(cursor.Offset, remaining);
            cursor.Offset += lines.Count;
            remaining -= lines.Count;
            foreach (string line in lines)
            {
                batch.AppendLine(line);
            }
        }
        if (batch.Length > 0)
        {
            LogBox.AppendText(batch.ToString());
            if (LogBox.Text.Length > UiLogCharacterLimit)
            {
                LogBox.Text = LogBox.Text[^UiLogCharacterLimit..];
            }
            LogBox.ScrollToEnd();
        }
        if (_run?.Log.FileUnavailable == true)
        {
            LogPathText.Text = "本轮磁盘日志不可用，证据无效；可复制完整内存日志（不是仅界面可见部分）。";
        }
    }

    private void ShowResult(AcceptanceOutcome outcome, string extra)
    {
        ResultBanner.Background = outcome switch
        {
            AcceptanceOutcome.Pass => Brushes.Honeydew,
            AcceptanceOutcome.PreconditionUnmet => Brushes.Cornsilk,
            _ => Brushes.MistyRose,
        };
        ResultBanner.Visibility = Visibility.Visible;
        ResultBannerText.Foreground = outcome == AcceptanceOutcome.Pass ? Brushes.DarkGreen
            : outcome == AcceptanceOutcome.PreconditionUnmet ? Brushes.DarkOrange : Brushes.DarkRed;
        ResultBannerText.Text = $"本轮结论：{outcome.Describe()}（{outcome.Code()}）。{extra}";
    }

    private void ShowBanner(string text, Brush background, Brush foreground)
    {
        RoleBanner.Background = background;
        RoleBannerText.Foreground = foreground;
        RoleBannerText.Text = text;
    }

    private void UpdateButtons()
    {
        bool idle = CanStart;
        HostButton.IsEnabled = idle && _ready;
        ClientButton.IsEnabled = idle && _ready;
        PeerCodeBox.IsEnabled = idle;
        PeerKeyBox.IsEnabled = idle;
        PermissionBox.IsEnabled = idle;
        SelfCheckButton.IsEnabled = idle;
        bool running = !_closing && !_faulted && _activeRun is { IsStopped: false } && _runTask is { IsCompleted: false };
        StopHostButton.IsEnabled = running && _activeRun!.IsHost;
        StopClientButton.IsEnabled = running && !_activeRun!.IsHost;
        ShowKeyButton.IsEnabled = CanViewHostKey() && _keyTask is null && !_keyConfirming;
        HideKeyButton.IsEnabled = _keyWork is not null || HostKeyText.Text.Length > 0;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => StartBusy(RunSelfCheckAsync);
    private void SelfCheckButton_Click(object sender, RoutedEventArgs e) => StartBusy(RunSelfCheckAsync);

    private void StartBusy(Func<Task> work)
    {
        if (!CanStart) { return; }
        _busy = true;
        // 先建立可回收任务，再做可能抛出的 UI 更新，避免留下永远为 true 的 busy。
        _busyTask = RunBusyAsync(work);
        PeerKeyBox.Clear();
        UpdateButtons();
    }

    private async Task RunBusyAsync(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception)
        {
            ReportFault(_run, "窗口只读检查");
            _ready = false;
            ShowResult(_run?.Settle(AcceptanceOutcome.HarnessError) ?? AcceptanceOutcome.HarnessError,
                "工作异常；异常正文未输出。请检查日志与实际环境。");
        }
        // _busy 由定时器在只读检查任务回收后解除。
    }

    private async Task RunSelfCheckAsync()
    {
        AcceptanceRun run = BeginRun("info");
        try
        {
            InfoRole.InfoResult info = await Task.Run(() => InfoRole.RunAsync(run, CancellationToken.None));
            AcceptanceOutcome outcome = run.Complete(info.Ready ? AcceptanceOutcome.Pass : AcceptanceOutcome.PreconditionUnmet,
                InfoRole.LocalCheckScope);
            DeviceCodeText.Text = info.DeviceCode;
            CertPinText.Text = info.CertSha256;
            ListenText.Text = info.ListenAddresses.Count == 0 ? "(无合格 RFC1918 网卡)" : string.Join(", ", info.ListenAddresses);
            _ready = info.Ready && outcome == AcceptanceOutcome.Pass && !_faulted;
            ReadyText.Text = InfoRole.DescribeReadiness(_ready);
            ReadyText.Foreground = _ready ? Brushes.DarkGreen : Brushes.DarkRed;
            ShowResult(outcome, InfoRole.LocalCheckScope);
        }
        catch (Exception)
        {
            ReportFault(run, "MainWindow 自检");
            _ready = false;
            ReadyText.Text = "自检失败；异常正文未输出，请检查日志。";
            ReadyText.Foreground = Brushes.DarkRed;
            ShowResult(run.Settle(run.Complete(AcceptanceOutcome.HarnessError, "自检异常，资源收尾后结算。")), "本机尚未就绪。");
        }
    }

    private void HostButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStart || !_ready) { return; }
        try
        {
            PeerKeyBox.Clear();
            RunState state = StartRole(isHost: true);
            _runTask = Task.Run(() => RunRoleWorkerAsync(state, () => HostRole.RunAsync(
                state.Run, HostWindowSeconds, state.Cts.Token, state.Inbox, state.PublishContext, state.Stop)));
        }
        catch (Exception)
        {
            HandleDispatcherFault();
        }
    }

    private void ClientButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStart || !_ready) { return; }
        byte[] key = Array.Empty<byte>();
        bool transferred = false;
        try
        {
            // Password 只取一次，先 Clear 再解析；字符串及 WPF 副本无法承诺擦净。
            string input;
            try { input = PeerKeyBox.Password; }
            finally { PeerKeyBox.Clear(); }
            bool valid;
            try { valid = SecretGenerator.TryDecodeAccessKey(input, out key); }
            finally { input = string.Empty; }
            string peerCode = PeerCodeBox.Text.Trim();
            if (!valid || peerCode.Length == 0 || IsSelf(peerCode))
            {
                MessageBox.Show(this, "请填写有效的对端访问密钥和非本机设备码。密钥输入已清空，本次未连接。",
                    "检查对端输入", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            SessionPermission permission = PermissionBox.SelectedIndex == 0 ? SessionPermission.ViewOnly : SessionPermission.Control;
            RunState state = StartRole(isHost: false);
            byte[] ownedKey = key;
            // 不给 Task.Run 调度令牌：即使立即取消，委托也必须进入 finally 清零。
            _runTask = Task.Run(async () =>
            {
                try
                {
                    return await RunRoleWorkerAsync(state, () => ClientRole.RunAllAsync(
                        state.Run, ClientRole.MandatoryScenarios, peerCode, null, null,
                        DiscoveryConstants.ExpectedTransportPort, state.Cts.Token,
                        ownedKey, permission, state.PublishPending)).ConfigureAwait(false);
                }
                finally { CryptographicOperations.ZeroMemory(ownedKey); }
            });
            transferred = true;
        }
        catch (Exception)
        {
            HandleDispatcherFault();
        }
        finally
        {
            if (!transferred) { CryptographicOperations.ZeroMemory(key); }
        }
    }

    private RunState StartRole(bool isHost)
    {
        HideHostKey();
        AcceptanceRun run = BeginRun(isHost ? "host" : "client");
        _runCts = new CancellationTokenSource();
        RunState state = new(run, _runCts, isHost);
        _activeRun = state;
        ApprovalList.ItemsSource = null;
        ClientPendingText.Text = string.Empty;
        ApprovalStatusText.Text = "无待批请求。已提交不等于已授权；短码仅作人工关联。";
        ResultBanner.Visibility = Visibility.Collapsed;
        ShowBanner(isHost
            ? "HOST —— 启动后监听 180 秒，到时正常结算；提前停止会作废本轮。"
            : "CLIENT —— 正在依次跑四个场景。pending 不代表认证成功，M4 还需两机证据核对。",
            Brushes.AliceBlue, Brushes.DarkBlue);
        UpdateButtons();
        return state;
    }

    private static async Task<AcceptanceOutcome> RunRoleWorkerAsync(RunState state, Func<Task<AcceptanceOutcome>> role)
    {
        AcceptanceOutcome outcome;
        try
        {
            outcome = await role().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (state.Cts.IsCancellationRequested)
        {
            outcome = AcceptanceOutcome.InvalidRun;
        }
        catch (Exception)
        {
            ReportFault(state.Run, "MainWindow 角色任务");
            outcome = AcceptanceOutcome.HarnessError;
        }
        finally
        {
            try { state.Stop(); }
            catch (Exception) { ReportFault(state.Run, "MainWindow 角色状态停止"); }
        }
        // 正常路径角色已在自己的 finally 后 Complete；这里只为意外逃逸补结算，幂等不写第二个 footer。
        return state.Run.Complete(outcome, "角色调用已返回；意外退出时不能给出通过证据。");
    }

    private void FinishRole()
    {
        RunState state = _activeRun!;
        AcceptanceOutcome returned = AcceptanceOutcome.HarnessError;
        try
        {
            try { _runCancellationTask?.GetAwaiter().GetResult(); }
            catch (Exception) { ReportFault(state.Run, "MainWindow 取消回调收尾"); }
            try { returned = _runTask!.GetAwaiter().GetResult(); }
            catch (Exception)
            {
                ReportFault(state.Run, "MainWindow 角色回收");
                returned = state.Run.Complete(AcceptanceOutcome.HarnessError, "角色任务异常，已收回。");
            }
        }
        finally
        {
            // 所有需要 join 的任务都已结束；先摘除拥有权，任何 UI/日志异常都不会卡住下一次回收。
            _activeRun = null;
            _runTask = null;
            _runCancellationTask = null;
            CancellationTokenSource? cts = _runCts;
            _runCts = null;
            try { state.Dispose(); }
            catch (Exception) { ReportFault(state.Run, "MainWindow 状态释放"); }
            finally { cts?.Dispose(); }
        }
        HideHostKey();
        ApprovalList.ItemsSource = null;
        ClientPendingText.Text = string.Empty;
        AcceptanceOutcome settled = state.Run.Settle(returned);
        if (settled != returned)
        {
            state.Run.Log.WriteLine($"[UI][RESULT][CORRECTION] outcome={settled.Code()} // 角色结算后发生故障或中止，原结论无效。");
        }
        ShowResult(settled, state.AuthenticationFailure ?? (state.IsHost
            ? "请配对控制端日志，核对 Host registry 实测及自然注销；不是只看连接条数。"
            : "控制端本地观测不等于 M4 通过；请核对 [CLIENT][RESULT] 与两机交叉核对清单。"));
        HostWindowText.Text = "本轮任务已收回；下次开始会建立独立的 180 秒监听窗口与审批收件箱。";
        if (!_closing && !_faulted)
        {
            ShowBanner("本轮已收尾。再次开始会创建新的 Run ID、日志与审批收件箱。", Brushes.WhiteSmoke, Brushes.DimGray);
        }
    }

    private void StopHostButton_Click(object sender, RoutedEventArgs e) => StopCurrentRun("UI 提前停止监听");
    private void StopClientButton_Click(object sender, RoutedEventArgs e) => StopCurrentRun("UI 中止控制端");

    private void StopCurrentRun(string source)
    {
        RequestRunStop(source);
        HideHostKey();
        PeerKeyBox.Clear();
        ApprovalList.ItemsSource = null;
        ClientPendingText.Text = string.Empty;
        ShowBanner("已停止接收审批并请求取消；本轮作废，正在等待角色与密钥任务清理。", Brushes.Cornsilk, Brushes.DarkOrange);
        UpdateButtons();
    }

    private void RequestRunStop(string source)
    {
        RunState? state = _activeRun;
        try { state?.Stop(); }
        catch (Exception) { ReportFault(state?.Run, "MainWindow 状态停止"); }
        _keyRequestValid = false;
        ++_keyGeneration;
        _keyWork?.RequestStop();
        if (state is null || _runTask?.IsCompleted == true) { return; }
        try
        {
            if (!state.Run.AbortedByOperator) { state.Run.MarkOperatorAbort(source); }
        }
        catch (Exception) { ReportFault(state.Run, "MainWindow 中止记账"); }
        finally
        {
            // CancelAsync 的任务也属于本轮，回收后才 Dispose CTS，不同步等待任何回调。
            try { _runCancellationTask ??= state.Cts.CancelAsync(); }
            catch (Exception) { ReportFault(state.Run, "MainWindow 请求取消"); }
        }
    }

    internal void HandleDispatcherFault()
    {
        // 先做不触碰控件的停止与回收准备；展示失败不得打断这一段或递归抛错。
        bool first = !_faulted;
        _faulted = true;
        _ready = false;
        if (!_closing)
        {
            _closing = true;
            _closeStarted = Stopwatch.GetTimestamp();
        }
        AcceptanceRun? faultRun = _activeRun is { } active && _runTask?.IsCompleted != true
            ? active.Run : _busy ? _run : null;
        if (first) { ReportFault(faultRun, "MainWindow Dispatcher"); }
        RequestRunStop("UI 故障作废");
        if (_activeRun is { } state && _runTask is null)
        {
            // 即使 Complete 的日志订阅方抛错，也已有可回收的任务，不会留下孤立 activeRun。
            _runTask = Task.Run(() => state.Run.Complete(AcceptanceOutcome.HarnessError, "角色尚未启动，界面发生故障。"));
        }
        try
        {
            HideHostKey();
            PeerKeyBox.Clear();
            ApprovalList.ItemsSource = null;
            ClientPendingText.Text = string.Empty;
            ShowResult(faultRun?.Settle(AcceptanceOutcome.HarnessError) ?? AcceptanceOutcome.HarnessError,
                "界面发生故障，正在收回当前任务后关闭；不改写已经完成的历史结论。");
            IsEnabled = false;
            UpdateButtons();
        }
        catch (Exception)
        {
            // 渲染已经损坏时只保留消息泵和任务回收机会，不再次更新同一控件。
        }
    }

    private static void ReportFault(AcceptanceRun? run, string source)
    {
        try
        {
            run?.ReportBackgroundFault(source, new InvalidOperationException("发生未预期故障；异常正文及内部异常未输出，避免秘密进入日志。"));
        }
        catch (Exception)
        {
            // ReportBackgroundFault 先登记故障；日志订阅方异常不能阻断其它清理。
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) { return; }
        if (_busy)
        {
            e.Cancel = true;
            HideHostKey();
            PeerKeyBox.Clear();
            ShowBanner("只读检查尚未收尾，已阻止关窗；请检查结束后再关闭。", Brushes.Cornsilk, Brushes.DarkOrange);
            return;
        }
        if (_runTask is not null || _keyTask is not null || _activeRun is not null)
        {
            e.Cancel = true;
            if (!_closing)
            {
                _closing = true;
                _closeStarted = Stopwatch.GetTimestamp();
                StopCurrentRun("关闭窗口");
                IsEnabled = false;
            }
            return;
        }
        HideHostKey();
        PeerKeyBox.Clear();
    }

    private void OnDeactivated(object? sender, EventArgs e) => HideHostKey();

    private void OnClosed(object? sender, EventArgs e)
    {
        _uiTimer.Stop();
        HideHostKey();
        PeerKeyBox.Clear();
    }

    private bool IsSelf(string peerCode)
    {
        static string Normalize(string value) => value.Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        return string.Equals(Normalize(DeviceCodeText.Text), Normalize(peerCode), StringComparison.Ordinal);
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 完整来源不含 HostKeyText/PasswordBox，也不依赖界面截断或尚未拉取的日志。
            Clipboard.SetText(string.Join(Environment.NewLine, _logs.Select(cursor => cursor.Log.All)));
            MessageBox.Show(this, "已复制进程及所有运行的完整日志。两机验收还需要对端日志。", "复制完成",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception)
        {
            MessageBox.Show(this, "复制失败，可以打开日志目录取文件。", "复制失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Directory.Exists(_logDirectory))
            {
                MessageBox.Show(this, "日志目录还不存在。", "打开日志目录", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Process.Start(new ProcessStartInfo(_logDirectory) { UseShellExecute = true });
        }
        catch (Exception)
        {
            MessageBox.Show(this, "无法打开日志目录；异常正文未输出。", "打开日志目录", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private sealed class LogCursor(AcceptanceLog log)
    {
        public AcceptanceLog Log { get; } = log;
        public int Offset { get; set; }
    }
}
