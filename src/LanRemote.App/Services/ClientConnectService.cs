using System.IO;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;
using LanRemote.Core.Encoding;
using LanRemote.Core.Models;
using LanRemote.Security.Identity;
using LanRemote.Transport;

namespace LanRemote.App.Services;

/// <summary>独占客户端连接、视频读取及两个窗口的生命周期；远端密钥绝不来自本机密钥存储。</summary>
public sealed class ClientConnectService
{
    private readonly DeviceIdentityService _identityService;
    private readonly ControlClientConnector _connector;
    private readonly object _gate = new();
    private Attempt? _active;
    private Task? _lastCleanupTask;
    private DrainEvidence? _lastDrainEvidence;
    private bool _shutdown;

    /// <summary>仅含固定分类或认证状态机的安全文案；失败时不要直接展示原始异常 Message。</summary>
    public string LastStatus { get; private set; } = "未连接。";

    /// <summary>最近一次尝试的原清理证明；连接业务失败不改变此任务的成功状态。</summary>
    public Task? LastCleanupTask
    {
        get { lock (_gate) return _lastCleanupTask; }
    }

    /// <summary>没有在途尝试，且最近一次原清理任务确实成功，才确认客户端已排空。</summary>
    public bool IsFullyDrained
    {
        get { lock (_gate) return _active is null && (_lastCleanupTask?.IsCompletedSuccessfully ?? true); }
    }

    internal bool HasSuccessfulDrainEvidenceFor(Task originalStop)
    {
        ArgumentNullException.ThrowIfNull(originalStop);
        lock (_gate)
        {
            return _active is null && ReferenceEquals(_lastCleanupTask, originalStop)
                && _lastDrainEvidence is { OriginalOperationsFinished: true, AllOwnedRootsReleased: true } evidence
                && ReferenceEquals(evidence.Attempt.Cleanup.Task, originalStop)
                && ReferenceEquals(evidence.OriginalStop, originalStop)
                && (evidence.Attempt.Control is null
                    ? !evidence.Attempt.ConnectorInvoked
                    : evidence.Attempt.ControlDisposalEvidence is
                        { OriginalOperationsFinished: true, AllOwnedRootsReleased: true })
                && (evidence.Attempt.Presenter is null
                    ? !evidence.Attempt.PresenterInvoked
                    : evidence.Attempt.PresenterDisposalEvidence is
                        { OriginalOperationsFinished: true, AllOwnedRootsReleased: true });
        }
    }

    public ClientConnectService(DeviceIdentityService identityService, ControlClientConnector connector)
    {
        _identityService = identityService ?? throw new ArgumentNullException(nameof(identityService));
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
    }

    /// <summary>由服务显示 PasswordBox 并收取远端密钥；目标必须由点击时的发现结果提前冻结。</summary>
    public Task ConnectAsync(ConnectionTarget target, Window ownerWindow, CancellationToken cancellationToken = default) =>
        Start(target, remoteKey: null, ownerWindow, cancellationToken);

    /// <summary>已有本次远端密钥时直接移交给后台 worker；不持久化，也不读取本机访问密钥。</summary>
    public Task ConnectAsync(ConnectionTarget target, string remoteKey, Window ownerWindow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteKey);
        return Start(target, remoteKey, ownerWindow, cancellationToken);
    }

    private Task Start(ConnectionTarget target, string? remoteKey, Window ownerWindow,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(ownerWindow);
        ownerWindow.Dispatcher.VerifyAccess();
        Attempt attempt = new(target, ownerWindow,
            new RemoteConnectDialog(target, remoteKey) { Owner = ownerWindow },
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
        lock (_gate)
        {
            if (_shutdown || _active is not null)
            {
                attempt.Stop.Dispose();
                LastStatus = "客户端正在连接、查看或关闭，不能重复连接。";
                throw new InvalidOperationException("客户端正在连接、查看或关闭，不能重复连接。");
            }
            if (_lastCleanupTask is { IsCompletedSuccessfully: false })
            {
                attempt.Stop.Dispose();
                LastStatus = "前次客户端清理未成功，不允许重新连接。";
                throw new InvalidOperationException("前次客户端清理未成功，不允许重新连接。");
            }
            _active = attempt;
            _lastCleanupTask = attempt.Cleanup.Task;
            _lastDrainEvidence = null;
            LastStatus = "正在连接远端设备（仅查看）…";
        }

        // Completion 在发布前已创建。RunAttemptAsync 转交异常，不留下窗口私有网络任务。
        _ = RunAttemptAsync(attempt);
        return attempt.Completion.Task;
    }

    /// <summary>阻止新的连接，取消当前尝试并等待原资源清理证明；业务连接失败不等于清理失败。</summary>
    public Task StopAndDisposeAsync()
    {
        lock (_gate)
        {
            _shutdown = true;
            // 与 finally 的 Stop.Dispose 和 _active 清空使用同一道锁，避免取消已释放的 CTS。
            if (_active is { } attempt) _ = attempt.RequestStop();
            return _lastCleanupTask ?? Task.CompletedTask;
        }
    }

    private async Task RunAttemptAsync(Attempt attempt)
    {
        Exception? primary = null;
        Exception? cleanup = null;
        EventHandler ownerClosed = (_, _) => _ = attempt.RequestStop();
        EventHandler dialogClosed = (_, _) =>
        {
            attempt.DialogClosed = true;
            if (!attempt.Finishing) _ = attempt.RequestStop();
        };
        bool ownerSubscribed = false;
        bool dialogSubscribed = false;
        try
        {
            attempt.Dialog.Closed += dialogClosed;
            dialogSubscribed = true;
            attempt.Owner.Closed += ownerClosed;
            ownerSubscribed = true;
            attempt.Dialog.Show();
            await attempt.Dialog.WaitForKeyAsync(attempt.Stop.Token).ConfigureAwait(true);
            string? remoteKey = attempt.Dialog.TakeRemoteKey();
            if (remoteKey is null) throw new OperationCanceledException(attempt.Stop.Token);
            attempt.Dialog.ShowConnecting();
            // 不把 cancellationToken 传入 Task.Run：一旦取得字符串，worker 必须确实运行并清零解码字节。
            await Task.Run(() => RunNetworkAsync(attempt, remoteKey), CancellationToken.None).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (attempt.Stop.IsCancellationRequested)
        {
            // 本次主动取消只负责触发完整 Drain，不作为清理失败；DisposeAsync 的 Aggregate 另行报告。
        }
        catch (Exception error)
        {
            primary = error;
        }

        // UI目录先失效，不能因原TLS/帧任务慢退出而继续显示旧会话目录。
        try { attempt.View?.StopDisplayCatalog(); }
        catch (Exception error) { cleanup = AddCleanupError(cleanup, error); }
        // 四条收尾分支并发请求停止、独立等待；任一报错不妨碍其余 TLS/原读取 join。
        try { await DrainAsync(attempt).ConfigureAwait(true); }
        catch (Exception error) { cleanup = AddCleanupError(cleanup, error); }
        // Connector 在交付 Control 之前也可能已拥有 TLS；认证失败时若原错误为聚合，
        // 其中可能含其内部 CloseAsync 的清理故障，此处的空 Control 无法再次证明资源根释放。
        // 保留原异常树但将这种无法拆清的报告归入不可信清理，不因本地 Drain 为空而洗绿。
        if (attempt.ConnectorInvoked && attempt.Control is null && primary is AggregateException unprovenConnectorCleanup)
        {
            primary = null;
            cleanup = AddCleanupError(cleanup, unprovenConnectorCleanup);
        }

        attempt.Finishing = true;
        string status = ClassifyStatus(CombineErrors(primary, cleanup),
            attempt.Stop.IsCancellationRequested, cleanup is not null);
        LastStatus = status;
        try { if (!attempt.DialogClosed) attempt.Dialog.Close(); }
        catch (Exception error) { cleanup = AddCleanupError(cleanup, error); }
        try
        {
            status = ClassifyStatus(CombineErrors(primary, cleanup), attempt.Stop.IsCancellationRequested,
                cleanup is not null);
            attempt.View?.SessionEnded(status, primary is not null || cleanup is not null);
        }
        catch (Exception error) { cleanup = AddCleanupError(cleanup, error); }
        finally
        {
            try
            {
                if (dialogSubscribed)
                {
                    attempt.Dialog.Closed -= dialogClosed;
                    dialogSubscribed = false;
                }
            }
            catch (Exception error) { cleanup = AddCleanupError(cleanup, error); }
            try
            {
                if (ownerSubscribed)
                {
                    attempt.Owner.Closed -= ownerClosed;
                    ownerSubscribed = false;
                }
            }
            catch (Exception error) { cleanup = AddCleanupError(cleanup, error); }
            bool cancelled = attempt.Stop.IsCancellationRequested;
            bool stopDisposed = false;
            try { attempt.Stop.Dispose(); stopDisposed = true; }
            catch (Exception error) { cleanup = AddCleanupError(cleanup, error); }

            bool windowReleased = attempt.DialogClosed && !dialogSubscribed && !ownerSubscribed
                && (attempt.View is null
                    ? !attempt.ViewInvoked
                    : attempt.View.DisconnectHandlerReleased);
            bool rootsReleased = attempt.DrainRootsReleased && windowReleased && stopDisposed;
            Exception? finalError = CombineErrors(primary, cleanup);
            status = ClassifyStatus(finalError, cancelled, cleanup is not null, rootsReleased);
            try { attempt.View?.ShowStatus(status); }
            catch (Exception error)
            {
                cleanup = AddCleanupError(cleanup, error);
                rootsReleased = false;
                finalError = CombineErrors(primary, cleanup);
                status = ClassifyStatus(finalError, cancelled, cleanup is not null);
            }

            lock (_gate)
            {
                _lastDrainEvidence = rootsReleased
                    ? new DrainEvidence(attempt, attempt.Cleanup.Task,
                        attempt.DrainOperationsFinished && windowReleased && stopDisposed, true)
                    : null;
                LastStatus = status;
                // 原清理证明包括窗口收尾和 CTS.Dispose；先确定结果，再放弃 active 资源根。
                if (cleanup is null) attempt.Cleanup.TrySetResult();
                else attempt.Cleanup.TrySetException(cleanup);
                if (ReferenceEquals(_active, attempt)) _active = null;
            }
            if (finalError is null) attempt.Completion.TrySetResult();
            else attempt.Completion.TrySetException(finalError);
        }
    }

    private async Task RunNetworkAsync(Attempt attempt, string remoteKey)
    {
        byte[] key = Array.Empty<byte>();
        try
        {
            DeviceIdentity local = await _identityService.GetOrCreateAsync(attempt.Stop.Token).ConfigureAwait(false);
            if (!CrockfordBase32.TryDecodeExact(remoteKey, AccessSecret.AccessKeyByteLength, out key))
                throw new InvalidRemoteKeyException();
            remoteKey = string.Empty; // 字符串不可清零；绝不写入磁盘、配置或日志。
            ControlClientAuthOptions options = new()
            {
                ApprovalPending = pending => PostApprovalPending(attempt, pending),
            };
            Task<AuthenticatedControlSession> connect;
            try
            {
                // 真实 connector 在返回 Task 前复制调用方的 16 字节数组。
                attempt.ConnectorInvoked = true;
                connect = _connector.ConnectAndAuthenticateAsync(
                    attempt.Target, local.DeviceId, local.DeviceName, key,
                    SessionPermission.ViewOnly, options: options, cancellationToken: attempt.Stop.Token);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
            attempt.Control = await connect.ConfigureAwait(false);
            // 产品路径：认证最终交付后、视频附着与唯一 Control 读者启动前启用切屏；
            // 启用失败按业务错误进入 Drain，不影响后续资源收尾。
            attempt.Control.EnableDisplaySwitching();
            // success 原 payload 收齐时就开始倒计时。此 continuation 不等待 UI、Dispatcher 或开窗。
            attempt.Video = await attempt.Control.AttachVideoAsync(attempt.Stop.Token).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        // 窗口切屏请求：真实登记、发送与回执等待都在 Control 会话内；null 表示未确认、被拒或已停止。
        async Task<DisplayChangedFrame?> RequestDisplaySwitch(DisplayId displayId, CancellationToken cancellationToken) =>
            await attempt.Control!.RequestDisplayAsync(displayId, cancellationToken).ConfigureAwait(false);

        Task show = attempt.Owner.Dispatcher.InvokeAsync(() =>
        {
            attempt.Stop.Token.ThrowIfCancellationRequested();
            attempt.ViewInvoked = true;
            RemoteViewWindow view = new(attempt.Target, () => { _ = attempt.RequestStop(); })
            {
                Owner = attempt.Owner,
            };
            attempt.View = view;
            view.StartDisplayCatalog(attempt.Control!.GetDisplayCatalogSnapshot, attempt.Stop.Token);
            // 切屏请求经同一 Control 会话真实发送并等待回执；窗口只管理忙状态与状态文本。
            view.StartDisplaySwitching(RequestDisplaySwitch, attempt.Stop.Token);
            view.Show();
            attempt.Dialog.Hide();
            attempt.PresenterInvoked = true;
            // 三参构造把视频读取、JPEG 解码与 Image.Source 提交并入请求代际屏障；先绑定再启动读取。
            attempt.Presenter = new RemoteVideoPresenter(attempt.Video!, view.VideoImage, attempt.Control!);
        }, DispatcherPriority.Normal, attempt.Stop.Token).Task;
        await show.ConfigureAwait(false);
        // 取消只提前退出等待层；Drain 随后才真正取消并 join 原读帧/解码/两条 TLS。
        await attempt.Presenter!.Completion.WaitAsync(attempt.Stop.Token).ConfigureAwait(false);
    }

    private static void PostApprovalPending(Attempt attempt, ControlClientApprovalPending pending)
    {
        Dispatcher dispatcher = attempt.Owner.Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        try
        {
            // 回调同步快速返回；只投递短码和未认证状态，绝不等待 Dispatcher 或开始视频读取。
            _ = dispatcher.BeginInvoke(new Action(() =>
            {
                if (!attempt.Stop.IsCancellationRequested && attempt.Dialog.IsVisible)
                    attempt.Dialog.ShowApprovalPending(pending.ShortCode);
            }), DispatcherPriority.Background);
        }
        catch (InvalidOperationException)
        {
            // Dispatcher 已关闭；认证的取消/截止仍由 Transport 判定。
        }
    }

    private static async Task DrainAsync(Attempt attempt)
    {
        // 即使某个 DisposeAsync 的同步前缀失败，也必须启动并等待其他三个资源的收尾。
        List<Exception> startErrors = [];
        Task cancel = Task.CompletedTask;
        Task presenter = Task.CompletedTask;
        Task video = Task.CompletedTask;
        Task control = Task.CompletedTask;
        try { cancel = attempt.RequestStop(); }
        catch (Exception error) { startErrors.Add(error); }
        try { presenter = attempt.Presenter?.StopAndDisposeAsync() ?? Task.CompletedTask; }
        catch (Exception error) { startErrors.Add(error); }
        try { video = attempt.Video?.DisposeAsync().AsTask() ?? Task.CompletedTask; }
        catch (Exception error) { startErrors.Add(error); }
        try { control = attempt.Control?.DisposeAsync().AsTask() ?? Task.CompletedTask; }
        catch (Exception error) { startErrors.Add(error); }

        bool startsSucceeded = startErrors.Count == 0;
        Task joined = Task.WhenAll(cancel, presenter, video, control);
        try { await joined.ConfigureAwait(false); }
        catch { /* 所有原任务完成后一次性报告，不因首个异常跳过其他连接。 */ }
        if (joined.Exception is { } aggregate) startErrors.AddRange(aggregate.InnerExceptions);
        else if (joined.IsCanceled) startErrors.Add(new OperationCanceledException("客户端资源排空被取消。"));

        // 原 Stop 任务可保留 I/O 诊断；只读证书另行等待，不让某一旧故障跳过另一条 TLS 或帧 worker。
        Task<PresenterDrainEvidence?> presenterEvidence = ObservePresenterEvidenceAsync(attempt.Presenter);
        Task<ClientDisposalEvidence?> controlEvidence = ObserveControlEvidenceAsync(attempt.Control);
        Task evidenceJoined = Task.WhenAll(presenterEvidence, controlEvidence);
        try { await evidenceJoined.ConfigureAwait(false); }
        catch { /* 两份证书任务都已结束后再报告观察故障。 */ }
        if (evidenceJoined.Exception is { } evidenceErrors) startErrors.AddRange(evidenceErrors.InnerExceptions);
        else if (evidenceJoined.IsCanceled) startErrors.Add(new OperationCanceledException("客户端排空证据读取被取消。"));

        if (evidenceJoined.IsCompletedSuccessfully)
        {
            attempt.PresenterDisposalEvidence = presenterEvidence.Result;
            attempt.ControlDisposalEvidence = controlEvidence.Result;
        }
        attempt.DrainOperationsFinished = startsSucceeded && joined.IsCompleted && evidenceJoined.IsCompletedSuccessfully
            && (attempt.Control is null
                ? !attempt.ConnectorInvoked
                : attempt.ControlDisposalEvidence is { OriginalOperationsFinished: true })
            && (attempt.Presenter is null
                ? !attempt.PresenterInvoked
                : attempt.PresenterDisposalEvidence is { OriginalOperationsFinished: true });
        attempt.DrainRootsReleased = attempt.DrainOperationsFinished && cancel.IsCompletedSuccessfully
            && (attempt.Control is null
                || attempt.ControlDisposalEvidence is { AllOwnedRootsReleased: true })
            && (attempt.Presenter is null
                || attempt.PresenterDisposalEvidence is { AllOwnedRootsReleased: true });
        if (startErrors.Count != 0) throw new AggregateException("客户端资源排空发生错误。", startErrors);
    }

    private static async Task<PresenterDrainEvidence?> ObservePresenterEvidenceAsync(RemoteVideoPresenter? presenter) =>
        presenter is null ? null : await presenter.GetDisposalEvidenceAsync().ConfigureAwait(false);

    private static async Task<ClientDisposalEvidence?> ObserveControlEvidenceAsync(AuthenticatedControlSession? control) =>
        control is null ? null : await control.GetDisposalEvidenceAsync().ConfigureAwait(false);

    private static Exception? CombineErrors(Exception? primary, Exception? cleanup) =>
        primary is null ? cleanup : cleanup is null ? primary
            : new AggregateException("连接和清理均发生错误。", primary, cleanup);

    private static Exception AddCleanupError(Exception? previous, Exception error) =>
        previous is null ? error : new AggregateException("客户端清理发生多项错误。", previous, error);

    private static string ClassifyStatus(Exception? error, bool cancelled, bool cleanupFailed,
        bool releasedWithDiagnostics = false)
    {
        if (cleanupFailed) return releasedWithDiagnostics
            ? "远端会话已结束，操作诊断已保留；客户端资源已排空。"
            : "远端会话已结束，关闭时有清理错误；请检查应用状态。";
        return error switch
        {
            null when cancelled => "已取消连接或主动断开；客户端资源已排空。",
            null => "远端会话已结束；客户端资源已排空。",
            AggregateException => "远端会话已结束，关闭时有清理错误；请检查应用状态。",
            InvalidRemoteKeyException => "远端访问密钥格式错误，请输入远端设备显示的完整密钥。",
            ControlClientAuthenticationException authentication => authentication.DisplayMessage,
            AuthenticationException => "远端 TLS 身份或访问密钥认证失败。",
            TimeoutException => "远端认证或视频附着等待超时。",
            OperationCanceledException => "已取消连接或主动断开。",
            IOException => "远端网络连接中断。",
            _ => "远端连接或解码失败；请检查设备连接状态。",
        };
    }

    private sealed class InvalidRemoteKeyException : FormatException { }

    private sealed record DrainEvidence(Attempt Attempt, Task OriginalStop,
        bool OriginalOperationsFinished, bool AllOwnedRootsReleased);

    private sealed class Attempt(
        ConnectionTarget target, Window owner, RemoteConnectDialog dialog, CancellationTokenSource stop)
    {
        private readonly object _stopGate = new();
        private Task? _cancel;

        internal ConnectionTarget Target { get; } = target;
        internal Window Owner { get; } = owner;
        internal RemoteConnectDialog Dialog { get; } = dialog;
        internal CancellationTokenSource Stop { get; } = stop;
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal AuthenticatedControlSession? Control { get; set; }
        internal AuthenticatedVideoSession? Video { get; set; }
        internal RemoteVideoPresenter? Presenter { get; set; }
        internal RemoteViewWindow? View { get; set; }
        internal bool ConnectorInvoked { get; set; }
        internal bool ViewInvoked { get; set; }
        internal bool PresenterInvoked { get; set; }
        internal ClientDisposalEvidence? ControlDisposalEvidence { get; set; }
        internal PresenterDrainEvidence? PresenterDisposalEvidence { get; set; }
        internal bool DrainOperationsFinished { get; set; }
        internal bool DrainRootsReleased { get; set; }
        internal bool DialogClosed { get; set; }
        internal bool Finishing { get; set; }

        internal Task RequestStop()
        {
            lock (_stopGate) return _cancel ??= Stop.CancelAsync();
        }
    }
}
