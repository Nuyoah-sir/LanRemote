using System.Diagnostics;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using LanRemote.Core.Encoding;
using LanRemote.Core.Models;
using LanRemote.Transport;

namespace LanRemote.Acceptance;

public partial class MainWindow
{
    private static readonly TimeSpan KeyViewWindow = TimeSpan.FromSeconds(15);
    private Task? _keyTask;
    private AcceptanceContext.WorkLease? _keyWork;
    private long _keyGeneration;
    private long _keyStarted;
    private bool _keyRequestValid;
    private bool _keyConfirming;
    private IReadOnlyList<LocalApprovalSnapshot> _displayedApprovals = Array.Empty<LocalApprovalSnapshot>();
    private RunState? _displayedApprovalOwner;

    private bool CanViewHostKey() => !_closing && !_faulted && _activeRun is { IsHost: true } state
        && _runTask is { IsCompleted: false } && state.GetHostContext() is not null;

    private void ShowKeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanViewHostKey() || _keyTask is not null || _keyConfirming) { return; }
        _keyConfirming = true;
        try
        {
            RunState state = _activeRun!;
            Guid runGeneration = state.Generation;
            if (MessageBox.Show(this,
                    "将显示本轮 Host 使用的本机访问密钥，仅供你当面或经可信渠道交给对方。\n\n" +
                    "不复制到剪贴板、不主动轮换、不记录密钥。确认起最多 15 秒；失焦、隐藏或停机即清空显示。\n" +
                    "原始字节会清零，但不可变字符串及 WPF 内部副本无法保证擦净。确认查看吗？",
                    "确认查看本机密钥", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            {
                return;
            }
            // 模态确认期间角色可已结束；必须复核同一轮、同一 context。
            if (!CanViewHostKey() || !ReferenceEquals(state, _activeRun) || state.Generation != runGeneration
                || !IsActive || _keyTask is not null || state.GetHostContext() is not { } context)
            {
                KeyStatusText.Text = "本轮 Host 已不可查看，未启动密钥读取。";
                return;
            }
            HideHostKey();
            long generation = ++_keyGeneration;
            _keyStarted = Stopwatch.GetTimestamp();
            _keyRequestValid = true;
            KeyStatusText.Text = "正在读取；本次 15 秒窗口已开始，超时不会另外启动读取任务。";
            AcceptanceContext.WorkLease? work = context.TryAcquireWork();
            if (work is null)
            {
                _keyRequestValid = false;
                KeyStatusText.Text = "Host 已开始清理或尚有读取未归还，未启动密钥读取。";
                return;
            }
            _keyWork = work;
            _keyTask = ReadHostKeyAsync(state, context, generation, work);
        }
        catch (Exception)
        {
            HandleDispatcherFault();
        }
        finally
        {
            _keyConfirming = false;
            UpdateButtons();
        }
    }

    private async Task ReadHostKeyAsync(RunState state, AcceptanceContext context, long generation, AcceptanceContext.WorkLease work)
    {
        byte[] bytes = Array.Empty<byte>();
        try
        {
            // 只有这一项后台读取；不用 WaitAsync 把未合作的底层读遗留在后台后再重开。
            AccessSecret secret = await work.RunAsync(async token =>
            {
                using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(token);
                budget.CancelAfter(KeyViewWindow);
                return await context.AccessSecretStore.LoadOrCreateAsync(budget.Token).ConfigureAwait(false);
            });
            bytes = secret.AccessKeyBytes;
            if (bytes.Length != AccessSecret.AccessKeyByteLength)
            {
                throw new InvalidOperationException("密钥长度无效。");
            }
            if (_keyRequestValid && generation == _keyGeneration && ReferenceEquals(state, _activeRun)
                && ReferenceEquals(context, state.GetHostContext()) && CanViewHostKey() && IsActive
                && !work.IsStopRequested && Stopwatch.GetElapsedTime(_keyStarted) < KeyViewWindow)
            {
                HostKeyText.Text = CrockfordBase32.Encode(bytes);
                KeyStatusText.Text = "本机密钥已显示；到达本次 15 秒截止或失焦即清空显示。";
            }
        }
        catch (OperationCanceledException) when (work.IsStopRequested || Stopwatch.GetElapsedTime(_keyStarted) >= KeyViewWindow)
        {
            // 正常隐藏/超时；不把取消当作密钥错误，也不回填过期结果。
        }
        catch (Exception)
        {
            ReportFault(state.Run, "MainWindow 本机密钥读取");
            try
            {
                if (ReferenceEquals(state, _activeRun))
                {
                    StopCurrentRun("本机密钥读取故障");
                    KeyStatusText.Text = "密钥读取失败；异常正文未输出，本轮已作废并请求停止。";
                }
            }
            catch (Exception) { ReportFault(state.Run, "MainWindow 密钥故障界面清理"); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (ReferenceEquals(_keyWork, work)) { _keyWork = null; }
            // 最后归还 lease；前面的读取、界面异常都已在 Host Complete 之前记账。
            try { await work.DisposeAsync(); }
            catch (Exception) { ReportFault(state.Run, "MainWindow 密钥取消收尾"); }
        }
    }

    private void HideKeyButton_Click(object sender, RoutedEventArgs e) => HideHostKey();

    private void HideHostKey()
    {
        _keyRequestValid = false;
        ++_keyGeneration;
        // RequestStop 只发布取消；回调由 lease join，不能阻塞 Dispatcher 或与 Dispose 锁反转。
        _keyWork?.RequestStop();
        HostKeyText.Text = string.Empty;
        KeyStatusText.Text = _keyTask is { IsCompleted: false }
            ? "显示已清空；原读取任务尚未收回，不能重新读取。"
            : "密钥显示已清空；不可变字符串及 WPF 内部副本无法保证擦净。";
        HideKeyButton.IsEnabled = false;
    }

    private void RefreshAuthentication()
    {
        if (_keyRequestValid && (!CanViewHostKey() || !IsActive
                || Stopwatch.GetElapsedTime(_keyStarted) >= KeyViewWindow
                || _keyWork?.IsStopRequested == true))
        {
            HideHostKey();
        }
        RunState? state = _activeRun;
        if (state is { IsHost: true, IsStopped: false })
        {
            HostWindowText.Text = $"预定监听窗口：成功监听后 180 秒；本轮已用 {Stopwatch.GetElapsedTime(state.Started).TotalSeconds:0} 秒（含初始化/清理，不是监听倒计时）。到时正常结算，提前停止作废。";
        }
        ControlClientApprovalPending? pending = state?.GetPending();
        ClientPendingText.Text = pending is null ? string.Empty
            : $"已收到 pending；尚未验证远端身份，也未获得权限。短码 {pending.ShortCode} 只供人工关联，不能作为凭据。原审批预算 {pending.ApprovalWindow.TotalSeconds:0} 秒，显示不重置期限；最终结果以 serverProof 校验为准。";
        RefreshApprovalList();
    }

    private void RefreshApprovalList()
    {
        RunState? state = _activeRun;
        IReadOnlyList<LocalApprovalSnapshot> current = state is { IsHost: true, IsStopped: false }
            ? state.Inbox!.GetSnapshot()
            : Array.Empty<LocalApprovalSnapshot>();
        if (!_displayedApprovals.Select(item => (item.RequestId, item.Generation))
                .SequenceEqual(current.Select(item => (item.RequestId, item.Generation))))
        {
            // 只记有限快照的差集；不积累历史ID、不按100ms重复写日志。
            foreach (LocalApprovalSnapshot added in current.Where(item => !_displayedApprovals.Any(old =>
                         old.RequestId == item.RequestId && old.Generation == item.Generation)))
            {
                state!.Run.Log.WriteLine($"[UI][APPROVAL-OBSERVED] utc={DateTimeOffset.UtcNow:O} requestId={added.RequestId} generation={added.Generation} sessionId={added.Request.SessionId} connectionId={added.Request.ConnectionId} expiresUtc={added.Request.ExpiresAt.ToUniversalTime():O} // UI 已拉取待批快照，不证明操作者已看到或控件实际可见。");
            }
            foreach (LocalApprovalSnapshot removed in _displayedApprovals.Where(item => !current.Any(now =>
                         now.RequestId == item.RequestId && now.Generation == item.Generation)))
            {
                _displayedApprovalOwner?.Run.Log.WriteLine($"[UI][APPROVAL-REMOVED] utc={DateTimeOffset.UtcNow:O} requestId={removed.RequestId} generation={removed.Generation} sessionId={removed.Request.SessionId} connectionId={removed.Request.ConnectionId} // 已离开待批快照，原因须核对状态机终态。");
            }
            LocalApprovalSnapshot? selected = ApprovalList.SelectedItem as LocalApprovalSnapshot;
            _displayedApprovals = current;
            _displayedApprovalOwner = current.Count > 0 ? state : null;
            ApprovalList.ItemsSource = current;
            if (selected is not null)
            {
                ApprovalList.SelectedItem = current.FirstOrDefault(item => item.RequestId == selected.RequestId
                    && item.Generation == selected.Generation);
            }
            // 不自动选中下一条，避免旧请求消失后把一次点击交给新请求。
        }
        UpdateApprovalQueueText();
        UpdateApprovalButtons();
    }

    private void UpdateApprovalQueueText()
    {
        int count = _displayedApprovals.Count;
        ApprovalQueueText.Text = count == 0
            ? "无待批请求。新请求到达后会在此列出，不会自动批准。"
            : $"待审批 {count} 条：" + (ApprovalList.SelectedItem is null
                ? "请先选中请求，核对短码后再点击批准、降为仅观看或拒绝。"
                : "已选中请求，请核对来源与短码后作出决定；等待本身不会批准。");
    }

    private void ApprovalList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateApprovalQueueText();
        UpdateApprovalButtons();
    }

    private void UpdateApprovalButtons()
    {
        LocalApprovalSnapshot? selected = ApprovalList.SelectedItem as LocalApprovalSnapshot;
        bool available = !_closing && !_faulted && selected is not null
            && _activeRun is { IsHost: true, IsStopped: false } state
            && state.Inbox!.GetSnapshot().Any(item => item.RequestId == selected.RequestId && item.Generation == selected.Generation);
        ApproveButton.IsEnabled = available;
        ViewOnlyButton.IsEnabled = available && selected!.Request.RequestedPermission == SessionPermission.Control;
        RejectButton.IsEnabled = available;
    }

    private void ApproveButton_Click(object sender, RoutedEventArgs e) => SubmitApproval(LocalApprovalOutcome.Approved, downgrade: false);
    private void ViewOnlyButton_Click(object sender, RoutedEventArgs e) => SubmitApproval(LocalApprovalOutcome.Approved, downgrade: true);
    private void RejectButton_Click(object sender, RoutedEventArgs e) => SubmitApproval(LocalApprovalOutcome.Denied, downgrade: false);

    private void SubmitApproval(LocalApprovalOutcome outcome, bool downgrade)
    {
        if (_closing || _faulted) { return; }
        LocalApprovalSnapshot? selected = ApprovalList.SelectedItem as LocalApprovalSnapshot;
        RunState? state = _activeRun;
        SessionPermission? grant = outcome == LocalApprovalOutcome.Approved && selected is not null
            ? downgrade ? SessionPermission.ViewOnly : selected.Request.RequestedPermission
            : null;
        bool submitted = selected is not null && state is { IsHost: true, IsStopped: false }
            && state.Inbox!.TrySubmit(selected.RequestId, selected.Generation, outcome, grant);
        // 先记录实际收件箱提交结果，再更新UI；记录失败点击，且不把决定提交当作认证。
        (state?.Run.Log ?? _processLog).WriteLine($"[UI][APPROVAL] utc={DateTimeOffset.UtcNow:O} requestId={selected?.RequestId.ToString() ?? "-"} generation={selected?.Generation.ToString() ?? "-"} sessionId={selected?.Request.SessionId.ToString() ?? "-"} connectionId={selected?.Request.ConnectionId.ToString() ?? "-"} submitted={submitted} decision={outcome} grant={grant?.ToString() ?? "-"} // 仅记录收件箱提交结果，不代表授权。");
        ApprovalList.SelectedItem = null;
        RefreshApprovalList();
        ApprovalStatusText.Text = submitted
            ? "决定已提交，不等于已授权；期限、断连及最终授权由 Transport 继续复核，请看最终日志。"
            : "未选中有效请求或请求已失效，决定未提交；请重新核对待批列表。";
    }

    /// <summary>每 run 独立状态。后台回调只写有界内存；取消与清理不调 Dispatcher。</summary>
    internal sealed class RunState : IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenRegistration _registration;
        private AcceptanceContext? _context;
        private ControlClientApprovalPending? _pending;
        private string? _authenticationFailure;
        private bool _stopped;

        public RunState(AcceptanceRun run, CancellationTokenSource cts, bool isHost)
        {
            Run = run;
            Cts = cts;
            IsHost = isHost;
            Inbox = isHost ? new LocalApprovalInbox() : null;
            run.Log.LineWritten += OnLineWritten;
            _registration = cts.Token.UnsafeRegister(static state => ((RunState)state!).Stop(), this);
        }

        public AcceptanceRun Run { get; }
        public Guid Generation { get; } = Guid.NewGuid();
        public long Started { get; } = Stopwatch.GetTimestamp();
        public CancellationTokenSource Cts { get; }
        public bool IsHost { get; }
        public LocalApprovalInbox? Inbox { get; }
        public bool IsStopped { get { lock (_gate) { return _stopped; } } }
        public string? AuthenticationFailure { get { lock (_gate) { return _authenticationFailure; } } }

        public void PublishContext(AcceptanceContext context)
        {
            lock (_gate)
            {
                if (_stopped) { return; }
                _context = context;
            }
        }

        public AcceptanceContext? GetHostContext()
        {
            lock (_gate)
            {
                return !_stopped && _context is { IsStopping: false } ? _context : null;
            }
        }

        public void PublishPending(ControlClientApprovalPending pending)
        {
            lock (_gate) { if (!_stopped) { _pending = pending; } }
        }

        public ControlClientApprovalPending? GetPending()
        {
            lock (_gate)
            {
                // 当前 ClientRole 使用系统 TimeProvider；只限制提示寿命，不裁决认证或授权。
                if (_pending is { } pending && TimeProvider.System.GetElapsedTime(pending.AcceptedAtTimestamp) >= pending.ApprovalWindow)
                {
                    _pending = null;
                }
                return _pending;
            }
        }

        private void OnLineWritten(string line)
        {
            // 仅清理展示提示，不驱动任何资源生命周期；不等 UI 日志队列追平。
            if (line.StartsWith("[CLIENT][AUTH] serverProof=verified ", StringComparison.Ordinal)
                || line.StartsWith("[CLIENT][RESULT] scenario=success ", StringComparison.Ordinal))
            {
                ClearPending();
            }
            if (line.StartsWith("[CLIENT][RESULT] scenario=success clientOutcome=FAIL ", StringComparison.Ordinal)
                && line.EndsWith("// " + ControlClientAuthenticationException.ServerProofFailureMessage, StringComparison.Ordinal))
            {
                lock (_gate) { _authenticationFailure = ControlClientAuthenticationException.ServerProofFailureMessage; }
            }
        }

        public void ClearPending()
        {
            lock (_gate) { _pending = null; }
        }

        public void Stop()
        {
            lock (_gate)
            {
                _stopped = true;
                _context = null;
                _pending = null;
            }
            Inbox?.Stop();
        }

        public void Dispose()
        {
            try { Stop(); }
            finally
            {
                Run.Log.LineWritten -= OnLineWritten;
                // 不在任何状态锁中等待取消回调；已进入的 Stop 只改内存，允许自行退出。
                _registration.Unregister();
                Inbox?.Dispose();
            }
        }
    }
}
