using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LanRemote.Capture;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Configuration;
using LanRemote.Core.Infrastructure;
using LanRemote.Core.Models;
using LanRemote.Discovery.Networking;
using LanRemote.Security.Certificates;
using LanRemote.Security.Identity;
using LanRemote.Sessions;
using LanRemote.Transport;
using Microsoft.Extensions.Logging;

namespace LanRemote.App.Services;

/// <summary>只有配置、身份、证书、密钥和只读网卡快照都就绪才开启 M5 仅查看监听。</summary>
public sealed class HostRuntimeService
{
    private static readonly JsonSerializerOptions ConfigJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly AppPaths _paths;
    private readonly DeviceIdentityService _identityService;
    private readonly DeviceCertificateService _certificateService;
    private readonly IAccessSecretStore _accessSecrets;
    private readonly INetworkBindingProvider _bindings;
    private readonly ISubnetPolicy _subnetPolicy;
    private readonly ILocalApprovalGate _approvalGate;
    private readonly ILogger<HostRuntimeService> _logger;
    private readonly SemaphoreSlim _lifetime = new(1, 1);
    private readonly object _drainGate = new();
    private readonly object _intentGate = new();
    private TransportHost? _host;
    private TransportHostStartResult? _startResult;
    private VideoQualitySettings? _activePreviewSettings;
    private Task? _drainTask;
    private TransportHost? _drainingHost;
    private Task<TransportHostStopReport>? _hostStopTask;
    private Task? _hostDisposeTask;
    private int _stopRequested = 1;
    private int _shutdown;

    public bool IsListening => Volatile.Read(ref _stopRequested) == 0 &&
        ApprovalsAvailable && Volatile.Read(ref _host)?.IsRunning == true;

    /// <summary>本轮已发布 Host 的只读档位；停止后保留报告直到下一轮发布，不授予监听资格。</summary>
    public VideoQualitySettings? ActivePreviewSettings => Volatile.Read(ref _activePreviewSettings);

    /// <summary>只有本轮原停机报告成功完成且宿主已移除，才能确认完整排空。</summary>
    public bool IsFullyDrained => Volatile.Read(ref _drainTask)?.IsCompletedSuccessfully == true &&
        Volatile.Read(ref _host) is null;

    public bool ApprovalsAvailable => _approvalGate is WpfLocalApprovalGate gate && gate.AreApprovalsOpen;

    public HostRuntimeService(
        AppPaths paths,
        DeviceIdentityService identityService,
        DeviceCertificateService certificateService,
        IAccessSecretStore accessSecrets,
        INetworkBindingProvider bindings,
        ISubnetPolicy subnetPolicy,
        ILocalApprovalGate approvalGate,
        ILogger<HostRuntimeService> logger)
    {
        _paths = paths;
        _identityService = identityService;
        _certificateService = certificateService;
        _accessSecrets = accessSecrets;
        _bindings = bindings;
        _subnetPolicy = subnetPolicy;
        _approvalGate = approvalGate;
        _logger = logger;
    }

    /// <summary>只读、严格解析；没有文件/损坏/查看权限未明确写出均不视为授权。</summary>
    public async Task<AppConfig> LoadVerifiedConfigAsync(CancellationToken cancellationToken = default)
    {
        await using FileStream stream = new(_paths.ConfigFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }, cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("config.json 必须是配置对象，主机未监听。");
        }

        AppConfig config = document.RootElement.Deserialize<AppConfig>(ConfigJsonOptions)
            ?? throw new InvalidDataException("config.json 为空，主机未监听。");
        bool allowDiscovery = ReadSingleSafetyBoolean(document.RootElement, "allowDiscovery");
        bool allowViewing = ReadSingleSafetyBoolean(document.RootElement, "allowViewing");
        bool requireApproval = ReadSingleSafetyBoolean(
            document.RootElement, "requireLocalApprovalForUnknownController");
        if (allowDiscovery != config.AllowDiscovery ||
            allowViewing != config.AllowViewing ||
            requireApproval != config.RequireLocalApprovalForUnknownController)
        {
            throw new InvalidDataException("config.json 安全字段解析不一致，主机未监听。");
        }

        return config;
    }

    private static bool ReadSingleSafetyBoolean(JsonElement root, string name)
    {
        JsonElement? found = null;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (found is not null)
            {
                throw new InvalidDataException($"config.json 安全字段 {name} 重复，主机未监听。");
            }
            found = property.Value;
        }

        if (found is not { } value || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException($"config.json 安全字段 {name} 缺失或非布尔值，主机未监听。");
        }
        return value.ValueKind == JsonValueKind.True;
    }

    /// <summary>操作者禁用查看时先同步阻断并发发布，再异步完整排空。</summary>
    public void BlockListening()
    {
        lock (_intentGate) Volatile.Write(ref _stopRequested, 1);
    }

    /// <summary>只允许用户显式开启时恢复监听意图；终态关窗不可恢复。</summary>
    public void AllowListening()
    {
        lock (_intentGate)
        {
            if (Volatile.Read(ref _shutdown) == 0 && ApprovalsAvailable)
            {
                Volatile.Write(ref _stopRequested, 0);
            }
        }
    }

    public bool OpenApprovals()
    {
        if (!_lifetime.Wait(0))
        {
            return false;
        }
        try
        {
            return Volatile.Read(ref _shutdown) == 0 && Volatile.Read(ref _stopRequested) != 0 &&
                _host is null && _approvalGate is WpfLocalApprovalGate gate && gate.OpenApprovals();
        }
        finally
        {
            _lifetime.Release();
        }
    }

    public void BlockApprovals()
    {
        // 决定本轮不再发布在短锁内完成；窗口撤销在锁外，不能在锁内等 UI 回调。
        lock (_intentGate) Volatile.Write(ref _stopRequested, 1);
        if (_approvalGate is WpfLocalApprovalGate gate)
        {
            gate.BlockApprovals();
        }
    }

    public void BeginShutdown()
    {
        lock (_intentGate)
        {
            Volatile.Write(ref _shutdown, 1);
            Volatile.Write(ref _stopRequested, 1);
        }
        BlockApprovals();
    }

    public async Task<TransportHostStartResult> StartAsync(CancellationToken cancellationToken = default,
        VideoQualitySettings? previewSettings = null)
    {
        // 首道门禁在取身份/网卡/密钥前拒非法画质，不把M6自动预设悄悄启用。
        if (previewSettings is not null) FramePipeline.ValidateSettings(previewSettings);
        // 不能在 _lifetime 内等待旧报告：停机协调者也需要先取得这道闸门。
        while (true)
        {
            Task? drain = Volatile.Read(ref _drainTask);
            if (drain is not null)
            {
                await drain.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            await _lifetime.WaitAsync(cancellationToken).ConfigureAwait(false);
            drain = Volatile.Read(ref _drainTask);
            if (drain is null || drain.IsCompletedSuccessfully)
            {
                break;
            }
            _lifetime.Release();
        }

        try
        {
            lock (_drainGate)
            {
                if (_drainTask is { IsCompletedSuccessfully: true } && _host is null)
                {
                    _drainTask = null;
                    _drainingHost = null;
                    _hostStopTask = null;
                    _hostDisposeTask = null;
                }
            }

            if (_host is not null)
            {
                if (previewSettings != _activePreviewSettings)
                {
                    throw new InvalidOperationException("当前仅查看共享已在运行；更换预览档位须先完整停止再显式开启。");
                }
                if (_startResult is null)
                {
                    throw new InvalidOperationException("前次主机启动清理失败，不允许创建新的会话登记表。");
                }
                EnsureStartAllowed();
                return _startResult;
            }

            AppConfig config = await LoadVerifiedConfigAsync(cancellationToken).ConfigureAwait(false);
            if (!config.AllowViewing || !config.RequireLocalApprovalForUnknownController)
            {
                throw new InvalidOperationException("未明确允许查看或本机审批已关闭，主机未监听。");
            }
            if (config.TransportPort != TransportConstants.Port)
            {
                throw new InvalidDataException("config.json 传输端口与协议固定端口不一致，主机未监听。");
            }
            EnsureStartAllowed();

            DeviceIdentity identity = await _identityService.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
            DeviceCertificate certificate = await _certificateService.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
            if (!certificate.Certificate.HasPrivateKey ||
                !string.Equals(identity.CertificateSha256, certificate.Sha256FingerprintHex, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("设备身份、证书私钥或证书指纹不匹配，主机未监听。");
            }

            AccessSecret accessSecret = await _accessSecrets.LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (accessSecret.AccessKeyBytes.Length != 16)
                {
                    throw new InvalidDataException("本机访问密钥长度不正确，主机未监听。");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(accessSecret.AccessKeyBytes);
            }

            await Task.Run(_bindings.Refresh, cancellationToken).ConfigureAwait(false);
            IPAddress[] addresses = _bindings.GetBindings().Select(binding => binding.Address).Distinct().ToArray();
            EnsureStartAllowed();
            if (addresses.Length == 0)
            {
                _logger.LogWarning("没有合格的 RFC1918 网卡地址，传输主机未监听；未修改任何网络配置。");
                return new TransportHostStartResult(Array.Empty<IPAddress>(), Array.Empty<TransportHostBindFailure>());
            }

            ControlAuthContext context = new()
            {
                ServerDeviceId = identity.DeviceId,
                AccessSecretStore = _accessSecrets,
                FailedAuthLimiter = new FailedAuthLimiter(),
                PendingApprovalLimiter = new ConnectionAdmissionLimiter(3, 1),
                SessionRegistry = new SessionRegistry(),
                ApprovalGate = _approvalGate,
                Options = new ControlAuthOptions { RequireLocalApproval = true },
            };
            FramePipelineVideoProducerFactory factory = new(
                () => new GdiScreenCaptureBackend(),
                () => new WpfJpegFrameEncoder(),
                settings: previewSettings);
            TransportHost host = TransportHost.CreateWithVideo(
                addresses, _subnetPolicy, certificate.Certificate, context, factory);
            Task? startupDispose = null;
            try
            {
                EnsureStartAllowed();
                TransportHostStartResult result = await Task.Run(host.Start, cancellationToken).ConfigureAwait(false);
                foreach (TransportHostBindFailure failure in result.Failures)
                {
                    _logger.LogWarning("传输地址 {Address} 监听失败：{Error}，{Reason}",
                        failure.Address, failure.Error, failure.Message);
                }

                cancellationToken.ThrowIfCancellationRequested();
                EnsureStartAllowed();
                if (!result.IsListening)
                {
                    startupDispose = BeginHostDispose(host);
                    await startupDispose.ConfigureAwait(false);
                    return result;
                }
                // 最后一次校验与发布必须同一短锁：BeginShutdown/BlockListening
                // 可以在其前赢得阻断，也可以在其后立即阻断，但绝不能抢在中间被晚发布绕过。
                lock (_intentGate)
                {
                    EnsureStartAllowed();
                    cancellationToken.ThrowIfCancellationRequested();
                    _activePreviewSettings = previewSettings;
                    _startResult = result;
                    _host = host;
                }
                // 日志提供者可能同步阻塞/抛错；发布之后的日志不是事务的一部分。
                // 不能在本轮因日志故障而重复释放已经交给 _host 的实例。
                try
                {
                    _logger.LogInformation("仅查看传输主机已监听：{Addresses}",
                        string.Join(", ", result.BoundAddresses));
                }
                catch (Exception)
                {
                    // 不影响启动资格与 Host 所有权；IsListening 仍按真实状态报告。
                }
                return result;
            }
            catch (Exception startError)
            {
                if (startupDispose is null)
                {
                    startupDispose = BeginHostDispose(host);
                    try
                    {
                        await startupDispose.ConfigureAwait(false);
                    }
                    catch (Exception disposalError)
                    {
                        RetainFailedStartupCleanup(host, startupDispose);
                        throw new AggregateException("传输主机启动失败且清理未完成。", startError, disposalError);
                    }
                }
                else if (!startupDispose.IsCompletedSuccessfully)
                {
                    RetainFailedStartupCleanup(host, startupDispose);
                }
                throw;
            }
        }
        finally
        {
            _lifetime.Release();
        }
    }

    /// <summary>同步阻断新访问；同一宿主的所有调用共享原任务的完整停机报告。</summary>
    public Task StopAndDisposeAsync()
    {
        BlockApprovals();
        BlockListening();
        Task approvalJoin = _approvalGate is WpfLocalApprovalGate gate
            ? gate.JoinBlockedApprovalsAsync() : Task.CompletedTask;
        TaskCompletionSource completion;
        lock (_drainGate)
        {
            // 已成功的旧报告不能替代新一轮（可能正持有 _lifetime 启动）停机。
            if (_drainTask is { IsCompletedSuccessfully: false } pendingOrFailed)
            {
                return pendingOrFailed;
            }

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _drainTask = completion.Task;
        }

        _ = CompleteDrainAsync(completion, approvalJoin);
        return completion.Task;
    }

    private async Task CompleteDrainAsync(TaskCompletionSource completion, Task approvalJoin)
    {
        try
        {
            Task hostDrain = DrainHostAsync();
            await Task.WhenAll(hostDrain, approvalJoin).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
        }
    }

    private void RetainFailedStartupCleanup(TransportHost host, Task disposeTask)
    {
        // 启动时尚未发布的 Host 也不能在原 Dispose 故障后遗失。
        _drainingHost = host;
        _hostStopTask = null;
        _hostDisposeTask = disposeTask;
        _host = host;
    }

    private static Task BeginHostDispose(TransportHost host)
    {
        try
        {
            return host.DisposeAsync().AsTask();
        }
        catch (Exception error)
        {
            return Task.FromException(error);
        }
    }

    private async Task DrainHostAsync()
    {
        await _lifetime.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_host is not { } host)
            {
                return;
            }

            if (!ReferenceEquals(_drainingHost, host))
            {
                _drainingHost = host;
                _hostStopTask = null;
                _hostDisposeTask = null;
            }

            Task<TransportHostStopReport>? stopTask = _hostStopTask;
            if (stopTask is null && _hostDisposeTask is null)
            {
                try
                {
                    stopTask = host.StopAsync();
                }
                catch (Exception error)
                {
                    stopTask = Task.FromException<TransportHostStopReport>(error);
                }
                _hostStopTask = stopTask;
            }

            if (stopTask is not null)
            {
                try
                {
                    TransportHostStopReport report = await stopTask.ConfigureAwait(false);
                    if (!report.AllFinished)
                    {
                        _logger.LogWarning("传输停机预算内未排空：accept={AcceptFinished}，连接={Unfinished}；继续完整等待。",
                            report.AcceptLoopsFinished, report.UnfinishedConnections);
                    }
                }
                catch (Exception error)
                {
                    _logger.LogWarning(error, "传输停机预算阶段失败，继续完整等待 DisposeAsync。");
                }
            }

            Task disposeTask = _hostDisposeTask ?? BeginHostDispose(host);
            _hostDisposeTask = disposeTask;

            try
            {
                await disposeTask.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                _logger.LogError(error, "传输主机释放有故障；原任务及资源根不确认已全部排空。");
            }

            List<Exception> errors = new();
            if (stopTask is not null)
            {
                CollectTaskErrors(stopTask, errors);
            }
            CollectTaskErrors(disposeTask, errors);
            if (errors.Count != 0)
            {
                throw new AggregateException("传输主机停机或释放有故障，不确认完整排空。", errors);
            }

            _host = null;
            _startResult = null;
            _logger.LogInformation("传输主机已完整停止并释放。");
        }
        finally
        {
            _lifetime.Release();
        }
    }

    private static void CollectTaskErrors(Task task, List<Exception> errors)
    {
        if (task.Exception is { } failure)
        {
            errors.AddRange(failure.InnerExceptions);
        }
        else if (task.IsCanceled)
        {
            errors.Add(new TaskCanceledException(task));
        }
    }

    private void EnsureStartAllowed()
    {
        if (Volatile.Read(ref _stopRequested) != 0 || Volatile.Read(ref _shutdown) != 0 ||
            !ApprovalsAvailable)
        {
            throw new InvalidOperationException("查看权限已关闭、本机审批不可用或正在退出，主机未监听。");
        }
    }
}

/// <summary>WPF 本机审批面；短码仅供人工核对，任何请求都必须人工选中“仅查看”才能批准。</summary>
public sealed class WpfLocalApprovalGate : ILocalApprovalGate
{
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<WpfLocalApprovalGate> _logger;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, PendingApproval> _pending = new();
    private readonly HashSet<PendingApproval> _owned = new();
    private Task? _blockedJoin;
    private bool _approvalsOpen;

    public WpfLocalApprovalGate(Dispatcher dispatcher, ILogger<WpfLocalApprovalGate> logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public bool AreApprovalsOpen
    {
        get
        {
            lock (_sync)
            {
                return _approvalsOpen && !_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished;
            }
        }
    }

    public bool OpenApprovals()
    {
        lock (_sync)
        {
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished ||
                _pending.Count != 0 || _blockedJoin is { IsCompletedSuccessfully: false } ||
                _owned.Any(pending => !pending.Presentation.IsCompletedSuccessfully))
            {
                return false;
            }
            _owned.Clear();
            _blockedJoin = null;
            _approvalsOpen = true;
            return true;
        }
    }

    public void BlockApprovals()
    {
        PendingApproval[] owned;
        bool shutdown;
        lock (_sync)
        {
            bool wasOpen = _approvalsOpen;
            _approvalsOpen = false;
            foreach (PendingApproval pending in _pending.Values)
            {
                pending.Complete(LocalApprovalOutcome.Unavailable);
            }
            _pending.Clear();
            owned = _owned.ToArray();
            if (wasOpen || _blockedJoin is null)
            {
                _blockedJoin = Task.WhenAll(owned.Select(pending => pending.Presentation));
            }
            shutdown = _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished;
        }

        foreach (PendingApproval pending in owned)
        {
            if (shutdown)
            {
                FailPresentation(pending, new InvalidOperationException("Dispatcher 已关闭，无法确认审批窗口关闭。"));
            }
            else
            {
                CloseWindow(pending);
            }
        }
    }

    public Task JoinBlockedApprovalsAsync()
    {
        lock (_sync)
        {
            return _blockedJoin ?? Task.FromException(new InvalidOperationException("审批尚未阻断。"));
        }
    }

    public ValueTask<LocalApprovalDecision> RequestApprovalAsync(
        LocalApprovalRequest request, CancellationToken cancellationToken)
    {
        // M5 不提供键鼠授权；Control 请求无论审批门状态都直接拒绝，不降级批准。
        if (request.RequestedPermission != SessionPermission.ViewOnly)
        {
            return ValueTask.FromResult(new LocalApprovalDecision(request.RequestId, LocalApprovalOutcome.Denied));
        }

        PendingApproval pending;
        lock (_sync)
        {
            if (!_approvalsOpen || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished ||
                _pending.ContainsKey(request.RequestId))
            {
                return ValueTask.FromResult(new LocalApprovalDecision(request.RequestId, LocalApprovalOutcome.Unavailable));
            }
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromResult(new LocalApprovalDecision(request.RequestId, LocalApprovalOutcome.Cancelled));
            }
            if (DateTimeOffset.UtcNow >= request.ExpiresAt)
            {
                return ValueTask.FromResult(new LocalApprovalDecision(request.RequestId, LocalApprovalOutcome.TimedOut));
            }

            pending = new PendingApproval(request, cancellationToken);
            _pending.Add(request.RequestId, pending);
            _owned.Add(pending);
            _ = pending.Presentation.ContinueWith(task =>
            {
                if (task.IsCompletedSuccessfully)
                {
                    lock (_sync) _owned.Remove(pending);
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        CancellationTokenRegistration registration = cancellationToken.Register(
            () => CancelPending(pending));
        try
        {
            DispatcherOperation show = _dispatcher.BeginInvoke(new Action(() => ShowApproval(pending)));
            _ = show.Task.ContinueWith(task =>
            {
                if (task.IsCanceled || task.IsFaulted)
                {
                    FailPresentation(pending, new InvalidOperationException(
                        "审批展示操作已中止，无法确认窗口状态。", task.Exception));
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (Exception error)
        {
            FailPresentation(pending, new InvalidOperationException("无法投递审批展示操作。", error));
        }
        return new ValueTask<LocalApprovalDecision>(AwaitDecisionAsync(pending, registration));
    }

    private void CancelPending(PendingApproval pending)
    {
        CompletePending(pending, LocalApprovalOutcome.Cancelled);
        CloseWindow(pending);
    }

    private void CompletePending(PendingApproval pending, LocalApprovalOutcome outcome,
        SessionPermission? permission = null)
    {
        lock (_sync)
        {
            if (_pending.TryGetValue(pending.Request.RequestId, out PendingApproval? current) &&
                ReferenceEquals(current, pending))
            {
                _pending.Remove(pending.Request.RequestId);
                pending.Complete(outcome, permission);
            }
        }
    }

    private bool IsPendingCurrent(PendingApproval pending)
    {
        lock (_sync)
        {
            return _approvalsOpen && _pending.TryGetValue(pending.Request.RequestId, out PendingApproval? current) &&
                ReferenceEquals(current, pending) && !pending.Decision.IsCompleted;
        }
    }

    private void FailPresentation(PendingApproval pending, Exception error)
    {
        CompletePending(pending, LocalApprovalOutcome.Unavailable);
        pending.FailPresentation(error);
    }

    private void CloseWindowOnUi(PendingApproval pending)
    {
        if (pending.Window is not { } window || pending.IsWindowClosed)
        {
            return;
        }
        pending.BeginClose();
        try
        {
            window.Close();
        }
        catch (Exception error)
        {
            FailPresentation(pending, new InvalidOperationException("审批窗口关闭失败。", error));
        }
        finally
        {
            pending.EndClose();
        }
    }

    private void CloseWindow(PendingApproval pending)
    {
        if (!pending.TryScheduleClose())
        {
            return;
        }
        try
        {
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            {
                throw new InvalidOperationException("Dispatcher 已关闭，无法确认审批窗口关闭。");
            }
            DispatcherOperation close = _dispatcher.BeginInvoke(new Action(() => CloseWindowOnUi(pending)));
            _ = close.Task.ContinueWith(task =>
            {
                if (task.IsCanceled || task.IsFaulted)
                {
                    FailPresentation(pending, new InvalidOperationException(
                        "审批关闭操作已中止，无法确认窗口状态。", task.Exception));
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (Exception error)
        {
            FailPresentation(pending, error);
        }
    }

    private static async Task<LocalApprovalDecision> AwaitDecisionAsync(
        PendingApproval pending, CancellationTokenRegistration registration)
    {
        try
        {
            return await pending.Decision.ConfigureAwait(false);
        }
        finally
        {
            registration.Dispose();
        }
    }

    private void ShowApproval(PendingApproval pending)
    {
        try
        {
            ShowApprovalCore(pending);
        }
        catch (Exception error)
        {
            _logger.LogError(error, "本机审批窗口无法展示；请求={RequestId}，按不可用拒绝。", pending.Request.RequestId);
            FailPresentation(pending, new InvalidOperationException("审批窗口展示失败，无法确认关闭状态。", error));
            CloseWindowOnUi(pending);
        }
        finally
        {
            pending.MarkShowFinished();
        }
    }

    private void ShowApprovalCore(PendingApproval pending)
    {
        LocalApprovalRequest request = pending.Request;
        if (!IsPendingCurrent(pending) || _dispatcher.HasShutdownStarted)
        {
            CompletePending(pending, LocalApprovalOutcome.Unavailable);
            return;
        }
        if (DateTimeOffset.UtcNow >= request.ExpiresAt)
        {
            CompletePending(pending, LocalApprovalOutcome.TimedOut);
            return;
        }

        Window window = new()
        {
            Title = "LanRemote 本机连接审批",
            Width = 440,
            Height = 380,
            MinWidth = 400,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = true,
        };
        if (Application.Current?.MainWindow is { IsVisible: true } owner)
        {
            window.Owner = owner;
        }
        StackPanel content = new() { Margin = new Thickness(18) };
        content.Children.Add(new TextBlock
        {
            Text = "远端请求查看屏幕。以下名称和设备号均为对方自称，请在本机核对后决定。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });
        AddLine($"自称设备名：{request.ClientName}");
        AddLine($"自称设备号：{request.ClientDeviceId}");
        AddLine($"来源 IP：{request.RemoteAddress}");
        AddLine("请求权限：仅查看（键鼠控制不可用）");
        AddLine($"六位短码：{request.ShortCode}（只供核对，不是认证凭据）");
        TextBlock remaining = new() { Margin = new Thickness(0, 5, 0, 10) };
        content.Children.Add(remaining);
        RadioButton viewOnly = new()
        {
            Content = "我明确选择仅查看，不允许控制",
            IsChecked = false,
            Margin = new Thickness(0, 0, 0, 12),
        };
        content.Children.Add(viewOnly);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal };
        Button approve = new() { Content = "批准仅查看", IsEnabled = false, Padding = new Thickness(12, 5, 12, 5) };
        Button deny = new() { Content = "拒绝", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(10, 0, 0, 0) };
        viewOnly.Checked += (_, _) => approve.IsEnabled = true;
        viewOnly.Unchecked += (_, _) => approve.IsEnabled = false;
        approve.Click += (_, _) => Resolve(LocalApprovalOutcome.Approved);
        deny.Click += (_, _) => Resolve(LocalApprovalOutcome.Denied);
        buttons.Children.Add(approve);
        buttons.Children.Add(deny);
        content.Children.Add(buttons);
        window.Content = content;

        DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => UpdateCountdown();
        window.Closed += (_, _) =>
        {
            timer.Stop();
            pending.MarkWindowClosed();
            CompletePending(pending, LocalApprovalOutcome.Cancelled);
        };
        pending.Window = window;
        UpdateCountdown();
        if (!IsPendingCurrent(pending))
        {
            CloseWindowOnUi(pending);
            return;
        }
        window.Show();
        pending.MarkWindowShown();
        timer.Start();
        return;

        void AddLine(string text) => content.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 7),
        });

        void UpdateCountdown()
        {
            double seconds = (request.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds;
            remaining.Text = $"审批剩余：{Math.Max(0, (int)Math.Ceiling(seconds))} 秒";
            if (seconds <= 0)
            {
                CompletePending(pending, LocalApprovalOutcome.TimedOut);
                CloseOnUi();
            }
        }

        void CloseOnUi() => CloseWindowOnUi(pending);

        void Resolve(LocalApprovalOutcome outcome)
        {
            lock (_sync)
            {
                if (!_approvalsOpen || _dispatcher.HasShutdownStarted ||
                    !_pending.TryGetValue(request.RequestId, out PendingApproval? current) ||
                    !ReferenceEquals(current, pending) || pending.Decision.IsCompleted)
                {
                    pending.Complete(LocalApprovalOutcome.Unavailable);
                }
                else if (pending.CancellationToken.IsCancellationRequested)
                {
                    _pending.Remove(request.RequestId);
                    pending.Complete(LocalApprovalOutcome.Cancelled);
                }
                else if (DateTimeOffset.UtcNow >= request.ExpiresAt)
                {
                    _pending.Remove(request.RequestId);
                    pending.Complete(LocalApprovalOutcome.TimedOut);
                }
                else if (outcome == LocalApprovalOutcome.Approved && viewOnly.IsChecked != true)
                {
                    return;
                }
                else
                {
                    _pending.Remove(request.RequestId);
                    pending.Complete(outcome, outcome == LocalApprovalOutcome.Approved
                        ? SessionPermission.ViewOnly : null);
                }
            }
            CloseOnUi();
        }
    }

    private sealed class PendingApproval(LocalApprovalRequest request, CancellationToken cancellationToken)
    {
        private readonly TaskCompletionSource<LocalApprovalDecision> _decision =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _presentation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _showFinished;
        private bool _shown;
        private bool _closed;
        private int _closing;
        private int _closeScheduled;

        public LocalApprovalRequest Request { get; } = request;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public Task<LocalApprovalDecision> Decision => _decision.Task;
        public Task Presentation => _presentation.Task;
        public Window? Window { get; set; }

        public bool TryScheduleClose() => Interlocked.CompareExchange(ref _closeScheduled, 1, 0) == 0;
        public bool IsWindowClosed => _closed;

        public void BeginClose() => _closing++;

        public void EndClose()
        {
            _closing--;
            CompletePresentationIfClosed();
        }

        public void MarkWindowShown() => _shown = true;

        public void MarkWindowClosed()
        {
            _closed = true;
            CompletePresentationIfClosed();
        }

        public void MarkShowFinished()
        {
            _showFinished = true;
            CompletePresentationIfClosed();
        }

        private void CompletePresentationIfClosed()
        {
            if (_showFinished && _closing == 0 && (_closed || (!_shown && Window?.IsVisible != true)))
            {
                _presentation.TrySetResult();
            }
        }

        public void FailPresentation(Exception error) => _presentation.TrySetException(error);

        public void Complete(LocalApprovalOutcome outcome, SessionPermission? permission = null) =>
            _decision.TrySetResult(new LocalApprovalDecision(Request.RequestId, outcome, permission));
    }
}
