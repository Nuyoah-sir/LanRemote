using System.Collections.ObjectModel;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using LanRemote.App.Services;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Configuration;
using LanRemote.Core.Encoding;
using LanRemote.Core.Infrastructure;
using LanRemote.Core.Models;
using LanRemote.Discovery;
using LanRemote.Security.Identity;
using LanRemote.Sessions;
using LanRemote.Transport;
using Microsoft.Extensions.Logging;

namespace LanRemote.App.ViewModels;

/// <summary>
/// 主窗口 ViewModel。
/// </summary>
/// <remarks>
/// <para><b>安全约定（AGENTS.md 第 3 条）</b>：所有日志只允许出现设备名、设备码、证书指纹前缀、
/// 远端 IP 与能力标签；<b>绝不</b>记录访问密钥本体。密钥只在用户显式点击「显示 / 复制」的瞬间存在于内存中。</para>
/// <para><b>ViewModel 不持有裸 Socket</b>：所有网络动作都通过 <see cref="IDiscoveryService"/>。
/// 设备集合的所有修改都必须 marshalling 回 UI 线程。</para>
/// </remarks>
public sealed class MainViewModel : ViewModelBase
{
    /// <summary>UI 侧判定设备离线的 TTL，与 Discovery 缓存 TTL 保持一致。</summary>
    public static readonly TimeSpan DeviceTtl =
        TimeSpan.FromMilliseconds(DiscoveryConstants.DeviceCacheTtlMs);

    private readonly AppPaths _paths;
    private readonly AppConfigStore _configStore;
    private readonly DeviceIdentityService _identityService;
    private readonly IAccessSecretStore _secretStore;
    private readonly IDiscoveryService _discovery;
    private readonly DiscoveryRuntimeState _runtimeState;
    private readonly HostRuntimeService _hostRuntime;
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private readonly ILogger<MainViewModel> _logger;
    private readonly SynchronizationContext? _uiContext;

    private AppConfig _config = new();
    private DeviceIdentity? _identity;
    private string? _revealedAccessKey;
    private bool _isBusy;
    private string _statusText = "正在初始化…";
    private bool _isAccessKeyVisible;
    private bool _hostSettingsReady;
    private bool _useClearerPreview;
    private bool _useSmootherPreview;
    private bool _useFastPreview;
    private bool _useThirtyFpsPreview;
    private double _manualPreviewScale = 0.75;
    private int _manualJpegQuality = 60;
    private bool _useManualVideoQuality;
    private bool _autoQuality;
    private bool _isExiting;
    private string _hostStatusText = "主机未监听：正在验证配置与身份。";
    private string _hostProtocolStatusText = string.Empty;

    private CancellationTokenSource? _watchCts;
    private CancellationTokenSource? _cleanupCts;
    private Task? _watchTask;
    private Task? _cleanupTask;
    private Task? _hostStopTask;
    private Task? _exitStopTask;
    private long _sharingIntentVersion;

    /// <summary>由 DI 容器构造。</summary>
    /// <param name="paths">数据目录定位。</param>
    /// <param name="configStore">配置存储。</param>
    /// <param name="identityService">设备身份服务。</param>
    /// <param name="secretStore">访问密钥存储。</param>
    /// <param name="discovery">局域网发现服务。</param>
    /// <param name="runtimeState">发现用的运行时状态。</param>
    /// <param name="logger">日志器。</param>
    public MainViewModel(
        AppPaths paths,
        AppConfigStore configStore,
        DeviceIdentityService identityService,
        IAccessSecretStore secretStore,
        IDiscoveryService discovery,
        DiscoveryRuntimeState runtimeState,
        HostRuntimeService hostRuntime,
        ILogger<MainViewModel> logger)
    {
        _paths = paths;
        _configStore = configStore;
        _identityService = identityService;
        _secretStore = secretStore;
        _discovery = discovery;
        _runtimeState = runtimeState;
        _hostRuntime = hostRuntime;
        _logger = logger;

        // 在 UI 线程构造时捕获上下文，用于把后台线程的设备更新 marshalling 回来。
        _uiContext = SynchronizationContext.Current;
    }

    /// <summary>局域网中发现的设备。</summary>
    public ObservableCollection<DiscoveredDevice> DiscoveredDevices { get; } = new();

    /// <summary>本机名称。</summary>
    public string LocalDeviceName => Environment.MachineName;

    /// <summary>本机设备码；尚未加载时为占位文本。</summary>
    public string DeviceCode => _identity?.DeviceCode ?? "加载中…";

    /// <summary>证书指纹的短前缀（仅用于人工核对，不参与任何安全判定）。</summary>
    public string CertificateFingerprintShort =>
        _identity is null ? "—" : $"{_identity.CertificateSha256[..12]}…";

    /// <summary>
    /// 访问密钥的显示形态：默认遮挡，显式揭示后显示分组 Base32。
    /// </summary>
    /// <remarks>遮挡串的形状刻意与真实密钥完全一致（26 个字符按 5 分组），
    /// 这样切换显示/隐藏时界面不会跳动。</remarks>
    public string AccessKeyDisplay =>
        IsAccessKeyVisible && _revealedAccessKey is not null
            ? _revealedAccessKey
            : AccessKeyMask;

    /// <summary>与真实密钥同形的遮挡串。</summary>
    public const string AccessKeyMask = "•••••-•••••-•••••-•••••-•••••-•";

    /// <summary>是否显示访问密钥。</summary>
    public bool IsAccessKeyVisible
    {
        get => _isAccessKeyVisible;
        private set
        {
            if (SetProperty(ref _isAccessKeyVisible, value))
            {
                OnPropertyChanged(nameof(AccessKeyDisplay));
            }
        }
    }

    /// <summary>是否忙碌。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    /// <summary>状态栏文本。</summary>
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    /// <summary>传输主机的独立状态；不会被设备发现或配置保存的文案覆盖。</summary>
    public string HostStatusText
    {
        get => _hostStatusText;
        private set => SetProperty(ref _hostStatusText, value);
    }

    /// <summary>与停止、排空状态分开显示，不把版本声明当作已验证的远端身份。</summary>
    public string HostProtocolStatusText
    {
        get => _hostProtocolStatusText;
        private set => SetProperty(ref _hostProtocolStatusText, value);
    }

    public void RefreshHostProtocolStatus() =>
        HostProtocolStatusText = _hostRuntime.LastProtocolRejection switch
        {
            "control-upgrade-required" => "已拒绝旧版协议声明（未经身份认证）。请将两端升级到兼容版本；不会回退到旧协议。",
            "control-protocol-unsupported" => "已拒绝不支持的协议版本声明（未经身份认证）。请将两端升级到兼容版本；不会回退到旧协议。",
            _ => string.Empty,
        };

    public bool HostSettingsReady
    {
        get => _hostSettingsReady;
        private set => SetProperty(ref _hostSettingsReady, value);
    }

    public bool NeedsLocalApprovalRepair => HostSettingsReady && !RequireLocalApproval;

    /// <summary>只影响下一次明确开启的主机；不写入 config.json，也不修改在途会话。</summary>
    public bool UseClearerPreview
    {
        get => _useClearerPreview;
        set
        {
            if (!SetProperty(ref _useClearerPreview, value)) return;
            if (value && _useSmootherPreview)
            {
                _useSmootherPreview = false;
                OnPropertyChanged(nameof(UseSmootherPreview));
            }
            if (value && _useFastPreview)
            {
                _useFastPreview = false;
                OnPropertyChanged(nameof(UseFastPreview));
            }
            if (value && _useThirtyFpsPreview)
            {
                _useThirtyFpsPreview = false;
                OnPropertyChanged(nameof(UseThirtyFpsPreview));
            }
            ReportPendingVideoSelection();
        }
    }

    /// <summary>同为75%缩放的15FPS试验档；与10FPS互斥，未授权时不能自动启动共享。</summary>
    public bool UseSmootherPreview
    {
        get => _useSmootherPreview;
        set
        {
            if (!SetProperty(ref _useSmootherPreview, value)) return;
            if (value && _useClearerPreview)
            {
                _useClearerPreview = false;
                OnPropertyChanged(nameof(UseClearerPreview));
            }
            if (value && _useFastPreview)
            {
                _useFastPreview = false;
                OnPropertyChanged(nameof(UseFastPreview));
            }
            if (value && _useThirtyFpsPreview)
            {
                _useThirtyFpsPreview = false;
                OnPropertyChanged(nameof(UseThirtyFpsPreview));
            }
            ReportPendingVideoSelection();
        }
    }

    /// <summary>20FPS固定试验档；与10/15FPS互斥且只在下一次显式开启时生效。</summary>
    public bool UseFastPreview
    {
        get => _useFastPreview;
        set
        {
            if (!SetProperty(ref _useFastPreview, value)) return;
            if (value && _useClearerPreview)
            {
                _useClearerPreview = false;
                OnPropertyChanged(nameof(UseClearerPreview));
            }
            if (value && _useSmootherPreview)
            {
                _useSmootherPreview = false;
                OnPropertyChanged(nameof(UseSmootherPreview));
            }
            if (value && _useThirtyFpsPreview)
            {
                _useThirtyFpsPreview = false;
                OnPropertyChanged(nameof(UseThirtyFpsPreview));
            }
            ReportPendingVideoSelection();
        }
    }

    /// <summary>30FPS固定试验档；与其他帧率档互斥且仅下一次显式共享开启时生效。</summary>
    public bool UseThirtyFpsPreview
    {
        get => _useThirtyFpsPreview;
        set
        {
            if (!SetProperty(ref _useThirtyFpsPreview, value)) return;
            if (value && _useClearerPreview)
            {
                _useClearerPreview = false;
                OnPropertyChanged(nameof(UseClearerPreview));
            }
            if (value && _useSmootherPreview)
            {
                _useSmootherPreview = false;
                OnPropertyChanged(nameof(UseSmootherPreview));
            }
            if (value && _useFastPreview)
            {
                _useFastPreview = false;
                OnPropertyChanged(nameof(UseFastPreview));
            }
            ReportPendingVideoSelection();
        }
    }

    /// <summary>手动画质须另外显式勾选；单纯浏览下拉值不影响默认档或现役会话。</summary>
    public bool UseManualVideoQuality
    {
        get => _useManualVideoQuality;
        set
        {
            if (SetProperty(ref _useManualVideoQuality, value)) ReportPendingVideoSelection();
        }
    }

    /// <summary>
    /// 自动画质须本机显式勾选：开启后本次共享的会话以命名预设 balanced 为起点，
    /// 并按真实 RTT 与实际帧率在三个命名预设间升降；关闭时行为与引入开关前完全一致。
    /// </summary>
    public bool AutoQuality
    {
        get => _autoQuality;
        set
        {
            if (SetProperty(ref _autoQuality, value)) ReportPendingVideoSelection();
        }
    }

    public IReadOnlyList<double> ManualPreviewScales => VideoQualitySettings.AllowedScales;

    public IReadOnlyList<int> ManualJpegQualities { get; } =
        Enumerable.Range(VideoQualitySettings.MinJpegQuality,
            VideoQualitySettings.MaxJpegQuality - VideoQualitySettings.MinJpegQuality + 1).ToArray();

    public double ManualPreviewScale
    {
        get => _manualPreviewScale;
        set
        {
            if (SetProperty(ref _manualPreviewScale, value)) ReportPendingVideoSelection();
        }
    }

    public int ManualJpegQuality
    {
        get => _manualJpegQuality;
        set
        {
            if (SetProperty(ref _manualJpegQuality, value)) ReportPendingVideoSelection();
        }
    }

    private static string ActivePreviewDescription(VideoQualitySettings? settings) => settings is null
        ? "5 FPS / 50% / JPEG 60"
        : $"{settings.TargetFps} FPS / {settings.Scale * 100:0}% / JPEG {settings.JpegQuality}";

    private void ReportPendingVideoSelection()
    {
        if (!_hostRuntime.IsListening) return;
        HostStatusText = $"仅查看主机正在监听：当前 {ActivePreviewDescription(_hostRuntime.ActivePreviewSettings)}；" +
            "更改帧率或手动画质须先紧急停止共享，再显式开启。";
    }

    /// <summary>数据目录。</summary>
    public string ConfigRootPath => _paths.RootDirectory;

    /// <summary>配置文件路径。</summary>
    public string ConfigFilePath => _paths.ConfigFilePath;

    /// <summary>秘密文件路径。</summary>
    public string SecretsFilePath => _paths.SecretsFilePath;

    /// <summary>是否允许被发现。</summary>
    public bool AllowDiscovery
    {
        get => _config.AllowDiscovery;
        set => UpdateConfig(_config with { AllowDiscovery = value }, nameof(AllowDiscovery));
    }

    /// <summary>是否允许查看。</summary>
    public bool AllowViewing
    {
        get => _config.AllowViewing;
        set => UpdateConfig(_config with { AllowViewing = value }, nameof(AllowViewing));
    }

    /// <summary>是否允许控制。</summary>
    public bool AllowControl
    {
        get => false;
        set => OnPropertyChanged(nameof(AllowControl));
    }

    /// <summary>陌生控制端是否需要本机确认。</summary>
    public bool RequireLocalApproval
    {
        get => _config.RequireLocalApprovalForUnknownController;
        set
        {
            if (!value)
            {
                OnPropertyChanged(nameof(RequireLocalApproval));
                return;
            }
            UpdateConfig(_config with { RequireLocalApprovalForUnknownController = true },
                nameof(RequireLocalApproval));
        }
    }

    /// <summary>是否随 Windows 登录自动启动。</summary>
    public bool AutoStartOnLogin
    {
        get => _config.AutoStartOnLogin;
        set => UpdateConfig(_config with { AutoStartOnLogin = value }, nameof(AutoStartOnLogin));
    }

    /// <summary>
    /// 异步加载配置与本机身份，然后才启动局域网发现。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 顺序是隐私要求，不能颠倒：
    /// <c>load config → load identity → runtimeState.Initialize → start discovery</c>。
    /// 这样磁盘上 <c>AllowDiscovery=false</c> 时，程序绝不会先广播一两次身份再关闭。
    /// 身份/DPAPI 失败时<b>不</b>启动发现，避免发出不完整或临时身份。
    /// </remarks>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (LoadingStopped(cancellationToken))
        {
            return;
        }

        IsBusy = true;
        StatusText = "正在加载配置…";

        try
        {
            bool configVerified = true;
            bool firstInstallDiscovery = false;
            AppConfig loaded;
            try
            {
                loaded = await _hostRuntime.LoadVerifiedConfigAsync(cancellationToken).ConfigureAwait(true);
                if (LoadingStopped(cancellationToken)) return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                if (LoadingStopped(cancellationToken)) return;
                // 产品规格要求首次安装的设备自动发现；只在严格读取的同一配置文件确实不存在时启用。
                // 损坏、字段缺失或无权访问时禁止公告；两种情况均不授权屏幕共享。
                firstInstallDiscovery = ex is FileNotFoundException { FileName: { } missingFile } &&
                    string.Equals(Path.GetFullPath(missingFile), Path.GetFullPath(_paths.ConfigFilePath),
                        StringComparison.OrdinalIgnoreCase) &&
                    !File.Exists(_paths.ConfigFilePath);
                configVerified = false;
                loaded = new AppConfig
                {
                    AllowDiscovery = firstInstallDiscovery,
                    AllowViewing = false,
                    AllowControl = false,
                    RequireLocalApprovalForUnknownController = true,
                };
                HostStatusText = firstInstallDiscovery
                    ? "主机未监听：首次安装自动发现设备；仅查看共享须在本机显式开启。"
                    : "主机未监听：配置不存在或损坏；请在本机显式开启“允许查看屏幕”。";
                _logger.LogWarning(ex, "配置没有通过严格读取，传输监听保持关闭。");
            }
            ReplaceConfig(loaded with { AllowControl = false }, raisePropertyChanged: true);

            DeviceIdentity identity = await _identityService.GetOrCreateAsync(cancellationToken)
                .ConfigureAwait(true);
            if (LoadingStopped(cancellationToken)) return;
            SetIdentity(identity);
            HostSettingsReady = true;
            OnPropertyChanged(nameof(NeedsLocalApprovalRepair));

            // M5 只广播查看能力；即使旧配置写有 AllowControl=true，也不能对外宣称已支持控制。
            _runtimeState.Initialize(identity, _config with { AllowViewing = false, AllowControl = false });

            _logger.LogInformation(
                "本机={MachineName}，设备码={DeviceCode}，允许被发现={AllowDiscovery}，允许控制=false，陌生控制端需本机确认={RequireApproval}",
                LocalDeviceName,
                identity.DeviceCode,
                _config.AllowDiscovery,
                _config.RequireLocalApprovalForUnknownController);

            long sharingIntentBeforeDiscovery = _sharingIntentVersion;
            bool discoveryStarted = await StartDiscoveryAsync(cancellationToken).ConfigureAwait(true);
            if (LoadingStopped(cancellationToken)) return;
            if (discoveryStarted)
            {
                StatusText = configVerified
                    ? "已从磁盘加载配置与本机身份。"
                    : firstInstallDiscovery
                        ? "首次安装已启动局域网自动发现；查看权限仍关闭。"
                        : "配置不可用，已用禁止发现与查看的默认值加载本机身份。";
            }

            // 历史配置里的 AllowViewing=true 并不证明本轮操作者已同意共享。
            // 旧版本的无关设置保存会把默认 true 写入磁盘，因此每次启动均保持零监听。
            if (configVerified && sharingIntentBeforeDiscovery == _sharingIntentVersion)
            {
                HostStatusText = !_config.AllowViewing
                    ? "主机未监听：未允许查看屏幕。"
                    : !RequireLocalApproval
                        ? "主机未监听：配置关闭了本机审批；请勾选修复后再显式开启。"
                        : "主机未监听：请在本机明确点击“开启仅查看共享”。";
            }
        }
        catch (OperationCanceledException)
        {
            if (!LoadingStopped(cancellationToken))
            {
                HostStatusText = "主机未监听：初始化已取消。";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!LoadingStopped(cancellationToken))
            {
                HostStatusText = "主机未监听：身份或本机秘密加载失败。";
            }
            _logger.LogError(ex, "身份或本机秘密加载失败，传输监听保持关闭。");
        }
        catch (CryptographicException ex)
        {
            // DPAPI 失败通常意味着换了用户或用户配置损坏，不是可以自愈的错误。
            if (!LoadingStopped(cancellationToken))
            {
                StatusText = "本机秘密无法读取（可能更换了 Windows 用户账户）。";
            }
            _logger.LogError(ex, "访问 secrets.bin 失败。");
        }
        catch (Exception ex) when (LoadingStopped(cancellationToken))
        {
            _logger.LogDebug(ex, "退出或取消后初始化已停止。");
        }
        finally
        {
            if (!_isExiting) IsBusy = false;
        }
    }

    private bool LoadingStopped(CancellationToken token) => _isExiting || token.IsCancellationRequested;

    /// <summary>
    /// 完整等待 ViewModel 的设备流与 TTL 清理原任务；不把等待预算超时当已退出。
    /// </summary>
    /// <remarks>socket 与发现服务循环另由 LanDiscoveryService.JoinAndDisposeAsync 完整等待。</remarks>
    public async Task StopDiscoveryAsync()
    {
        List<Task> cancellations = new();
        if (_watchCts is { } watch) cancellations.Add(watch.CancelAsync());
        if (_cleanupCts is { } cleanup) cancellations.Add(cleanup.CancelAsync());

        List<Task> tasks = new(cancellations);
        if (_watchTask is not null) tasks.Add(_watchTask);
        if (_cleanupTask is not null) tasks.Add(_cleanupTask);

        Task joined = Task.WhenAll(tasks);
        try
        {
            await joined.ConfigureAwait(true);
        }
        finally
        {
            // 若任一原任务仍未退出（例如注入的取消回调不合作），不能提前释放根 CTS。
            if (tasks.All(task => task.IsCompleted))
            {
                _watchCts?.Dispose();
                _cleanupCts?.Dispose();
                _watchCts = null;
                _cleanupCts = null;
            }
        }
    }

    /// <summary>手动刷新：立即发一次 probe（组播 + 各网卡定向广播）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _discovery.ProbeAsync(cancellationToken).ConfigureAwait(true);
            StatusText = "已发送一次局域网探测。";
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException
                                     or InvalidOperationException)
        {
            StatusText = "刷新失败：局域网发现未运行。";
            _logger.LogDebug(ex, "手动 probe 失败。");
        }
    }

    /// <summary>切换访问密钥的显示状态。</summary>
    /// <returns>表示切换后是否处于显示状态。</returns>
    public async Task<bool> ToggleAccessKeyVisibilityAsync(CancellationToken cancellationToken = default)
    {
        if (IsAccessKeyVisible)
        {
            HideAccessKey();
            return false;
        }

        string key = await LoadDisplayableAccessKeyAsync(cancellationToken).ConfigureAwait(true);
        ShowAccessKey(key);
        return true;
    }

    /// <summary>取一个可用于写入剪贴板的密钥串，不改变显示状态。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>分组后的 Base32 密钥。</returns>
    public Task<string> GetAccessKeyForClipboardAsync(CancellationToken cancellationToken = default) =>
        LoadDisplayableAccessKeyAsync(cancellationToken);

    /// <summary>重新生成访问密钥。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>旧密钥在写入新值后立刻失配：下一次认证读到的一定是新值。</remarks>
    public async Task RegenerateAccessKeyAsync(CancellationToken cancellationToken = default)
    {
        AccessSecret? rotated = null;

        try
        {
            IsBusy = true;
            rotated = await _secretStore.RegenerateAsync(cancellationToken).ConfigureAwait(true);

            HideAccessKey();
            StatusText = "访问密钥已重新生成，旧密钥立即失效。";
            _logger.LogInformation("用户已重新生成本机访问密钥，旧密钥失效。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                     or CryptographicException)
        {
            StatusText = "访问密钥重新生成失败。";
            _logger.LogError(ex, "重新生成访问密钥失败。");
        }
        finally
        {
            // UI 不需要长期持有新密钥：拿到之后立刻把原始字节清零。
            // 注意 .NET 的 string 不可变，无法保证进程内存中不残留副本。
            if (rotated is not null)
            {
                CryptographicOperations.ZeroMemory(rotated.AccessKeyBytes);
            }

            IsBusy = false;
        }
    }

    /// <summary>
    /// 启动局域网发现。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否成功启动；<see langword="false"/> 表示身份仍然可用，只是发现不可用。</returns>
    /// <remarks>
    /// 失败时<b>由本方法自己</b>写入状态文本，调用方不再覆盖——
    /// 否则「端口被占用」这类真实网络错误会被「配置加载成功」的文案盖掉。
    /// 身份<b>不会</b>因为 discovery 失败而重置。
    /// </remarks>
    private async Task<bool> StartDiscoveryAsync(CancellationToken cancellationToken)
    {
        if (LoadingStopped(cancellationToken)) return false;
        try
        {
            await _discovery.StartAsync(cancellationToken).ConfigureAwait(true);
            if (LoadingStopped(cancellationToken)) return false;

            StartDeviceWatch();
            StartTtlCleanupLoop();

            return true;
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException
                                     or UnauthorizedAccessException)
        {
            // 端口被占用、网卡异常等都不能影响 M1：身份照旧，只是发现不可用。
            if (!LoadingStopped(cancellationToken))
            {
                StatusText = "本机身份已加载，但局域网发现启动失败。";
            }
            _logger.LogError(ex, "局域网发现启动失败；本机身份保持原样。");
            return false;
        }
    }

    private void StartDeviceWatch()
    {
        _watchCts = new CancellationTokenSource();
        CancellationToken token = _watchCts.Token;

        _watchTask = Task.Run(
            async () =>
            {
                try
                {
                    await foreach (DiscoveredDevice device in _discovery.WatchAsync(token)
                                       .ConfigureAwait(false))
                    {
                        UpsertDevice(device);
                    }
                }
                catch (OperationCanceledException)
                {
                    // 正常停止。
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "设备流消费结束。");
                }
            },
            CancellationToken.None);
    }

    private void StartTtlCleanupLoop()
    {
        _cleanupCts = new CancellationTokenSource();
        CancellationToken token = _cleanupCts.Token;

        _cleanupTask = Task.Run(
            async () =>
            {
                using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));

                try
                {
                    while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                    {
                        PruneExpiredDevices();
                    }
                }
                catch (OperationCanceledException)
                {
                    // 正常停止。
                }
            },
            CancellationToken.None);
    }

    private void UpsertDevice(DiscoveredDevice device)
    {
        PostToUiThread(
            () =>
            {
                for (int i = 0; i < DiscoveredDevices.Count; i++)
                {
                    if (DiscoveredDevices[i].DeviceId == device.DeviceId)
                    {
                        DiscoveredDevices[i] = device;
                        return;
                    }
                }

                DiscoveredDevices.Add(device);
            });
    }

    private void PruneExpiredDevices()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - DeviceTtl;

        PostToUiThread(
            () =>
            {
                for (int i = DiscoveredDevices.Count - 1; i >= 0; i--)
                {
                    if (DiscoveredDevices[i].LastSeen <= cutoff)
                    {
                        DiscoveredDevices.RemoveAt(i);
                    }
                }
            });
    }

    private void PostToUiThread(Action action)
    {
        if (_uiContext is null)
        {
            action();
            return;
        }

        _uiContext.Post(
            _ =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "更新设备列表时出错。");
                }
            },
            null);
    }

    private async Task<string> LoadDisplayableAccessKeyAsync(CancellationToken cancellationToken)
    {
        AccessSecret secret = await _secretStore.LoadOrCreateAsync(cancellationToken).ConfigureAwait(true);

        try
        {
            string encoded = CrockfordBase32.Encode(secret.AccessKeyBytes);
            return CrockfordBase32.Group(encoded, 5);
        }
        finally
        {
            // 尽量缩短原始字节在内存中的存活时间。
            CryptographicOperations.ZeroMemory(secret.AccessKeyBytes);
        }
    }

    private void ShowAccessKey(string key)
    {
        _revealedAccessKey = key;
        IsAccessKeyVisible = true;
    }

    private void HideAccessKey()
    {
        _revealedAccessKey = null;
        IsAccessKeyVisible = false;
        OnPropertyChanged(nameof(AccessKeyDisplay));
    }

    private void SetIdentity(DeviceIdentity identity)
    {
        _identity = identity;
        OnPropertyChanged(nameof(DeviceCode));
        OnPropertyChanged(nameof(CertificateFingerprintShort));
    }

    /// <summary>仅由本机明确点击“开启仅查看共享”时调用；历史配置和勾选设置不授权监听。</summary>
    public async Task EnableViewOnlySharingAsync(CancellationToken token = default, bool useClearerPreview = false,
        bool useSmootherPreview = false, bool useFastPreview = false, bool useThirtyFpsPreview = false,
        bool useManualVideoQuality = false, double manualScale = 0.75, int manualJpegQuality = 60,
        bool autoQuality = false)
    {
        if (_isExiting) return;
        if ((useClearerPreview ? 1 : 0) + (useSmootherPreview ? 1 : 0) +
            (useFastPreview ? 1 : 0) + (useThirtyFpsPreview ? 1 : 0) > 1)
        {
            throw new ArgumentException("不能同时启用多个预览帧率档。");
        }
        int fps = useThirtyFpsPreview ? 30 : useFastPreview ? 20 : useSmootherPreview ? 15 :
            useClearerPreview ? 10 : 5;
        // 无勾选则保留原5/50/60或显式FPS的75/60，UI选择值本身不能静默改变产品行为。
        VideoQualitySettings? previewSettings = useManualVideoQuality
            ? new VideoQualitySettings(fps, manualScale, manualJpegQuality, false)
            : fps == 5 ? null : new VideoQualitySettings(fps, 0.75, 60, false);
        if (previewSettings is not null) FramePipeline.ValidateSettings(previewSettings);
        long intent = ++_sharingIntentVersion;
        const string notReady = "主机未监听：请先允许查看屏幕并启用本机审批，再明确点击开启。";
        if (!CanEnableViewOnlySharing(intent))
        {
            _hostRuntime.BlockListening();
            _hostRuntime.BlockApprovals();
            RefreshHostProtocolStatus();
            HostStatusText = notReady;
            PublishDiscoveryCapabilities();
            Task stopTask = _hostRuntime.StopAndDisposeAsync();
            _hostStopTask = stopTask;
            try
            {
                await stopTask.ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                if (intent == _sharingIntentVersion && !_isExiting)
                {
                    HostStatusText = "未排空，不安全关闭；请查看本机日志。";
                }
                _logger.LogError(ex, "开启条件不足时旧传输主机未能完整停止。");
                throw;
            }
            return;
        }

        try
        {
            await _persistGate.WaitAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (intent == _sharingIntentVersion && !_hostRuntime.IsListening && !_isExiting)
            {
                HostStatusText = "主机未监听：启动已取消。";
            }
            return;
        }
        bool started = false;
        try
        {
            if (_hostStopTask is { } stopTask)
            {
                await stopTask.ConfigureAwait(true);
            }
            if (!CanEnableViewOnlySharing(intent))
            {
                if (intent == _sharingIntentVersion && !_isExiting) HostStatusText = notReady;
                return;
            }
            AppConfig verified = await _hostRuntime.LoadVerifiedConfigAsync(token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (!CanEnableViewOnlySharing(intent) ||
                !verified.AllowViewing || !verified.RequireLocalApprovalForUnknownController ||
                verified.TransportPort != _config.TransportPort ||
                verified.TransportPort != TransportConstants.Port)
            {
                if (intent == _sharingIntentVersion && !_isExiting)
                {
                    HostStatusText = "主机未监听：磁盘配置与当前安全设置或传输端口不一致。";
                }
                return;
            }
            if (_hostRuntime.IsListening)
            {
                started = true;
                HostStatusText = $"仅查看主机已在监听：本次 {ActivePreviewDescription(_hostRuntime.ActivePreviewSettings)}；" +
                    "须先紧急停止并完整排空后再显式开启才能切换画质。";
                PublishDiscoveryCapabilities();
                return;
            }
            if (!_hostRuntime.OpenApprovals())
            {
                HostStatusText = "主机未监听：本机审批不可用或旧主机尚未排空。";
                return;
            }

            _hostRuntime.AllowListening();
            started = await StartHostAsync(token, intent, previewSettings, autoQuality).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (intent == _sharingIntentVersion && !_isExiting)
            {
                HostStatusText = "主机未监听：启动已取消。";
            }
        }
        catch (Exception ex)
        {
            if (intent == _sharingIntentVersion && !_isExiting)
            {
                HostStatusText = "主机未监听：配置、审批或旧连接排空失败（详见日志）。";
            }
            _logger.LogError(ex, "仅查看共享未能显式开启。");
        }
        finally
        {
            try
            {
                if (!started)
                {
                    _hostRuntime.BlockListening();
                    _hostRuntime.BlockApprovals();
                    RefreshHostProtocolStatus();
                    try
                    {
                        Task cleanupTask = _hostRuntime.StopAndDisposeAsync();
                        _hostStopTask = cleanupTask;
                        await cleanupTask.ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        if (intent == _sharingIntentVersion && !_isExiting)
                        {
                            HostStatusText = "未排空，不安全关闭；请查看本机日志。";
                        }
                        _logger.LogError(ex, "开启失败后旧传输主机未能完整停止。");
                        throw;
                    }
                    PublishDiscoveryCapabilities();
                }
            }
            finally
            {
                _persistGate.Release();
            }
        }
    }

    private bool CanEnableViewOnlySharing(long intent) =>
        intent == _sharingIntentVersion && HostSettingsReady && _identity is not null && !_isExiting &&
        _config.AllowViewing && _config.RequireLocalApprovalForUnknownController;

    /// <summary>紧急撤销仅查看共享；只影响当前进程，不修改下次启动仍需显式开启的配置。</summary>
    public Task StopViewOnlySharingAsync()
    {
        long intent = ++_sharingIntentVersion;
        _hostRuntime.BlockListening();
        _hostRuntime.BlockApprovals();
        RefreshHostProtocolStatus();
        PublishDiscoveryCapabilities();
        if (!_isExiting)
        {
            HostStatusText = "正在停止仅查看共享并排空连接；下次仍需显式点击开启。";
        }

        Task stopTask = StopAndReportAsync(_hostRuntime.StopAndDisposeAsync(), intent);
        _hostStopTask = stopTask;
        return stopTask;
    }

    private async Task StopAndReportAsync(Task stopTask, long intent)
    {
        try
        {
            await stopTask.ConfigureAwait(true);
            if (intent == _sharingIntentVersion && !_isExiting)
            {
                HostStatusText = "仅查看共享已停止且连接已排空；下次仍需显式点击开启。";
            }
        }
        catch (Exception ex)
        {
            if (intent == _sharingIntentVersion && !_isExiting)
            {
                HostStatusText = "共享未排空，不安全关闭；请查看本机日志。";
            }
            _logger.LogError(ex, "紧急停止仅查看共享未能完整排空。");
            throw;
        }
    }

    /// <summary>同步禁止关窗后开启监听与审批，并立即撤销发现广播的查看能力。</summary>
    public void BeginExit()
    {
        if (_isExiting) return;
        _isExiting = true;
        ++_sharingIntentVersion;
        HostSettingsReady = false;
        OnPropertyChanged(nameof(NeedsLocalApprovalRepair));
        _hostRuntime.BeginShutdown();
        RefreshHostProtocolStatus();
        PublishDiscoveryCapabilities();
        HostStatusText = "正在退出：仅查看共享已撤销，等待连接排空…";
    }

    public Task StopHostForExitAsync()
    {
        BeginExit();
        if (_exitStopTask is null || _exitStopTask.IsFaulted || _exitStopTask.IsCanceled)
        {
            _exitStopTask = StopHostForExitCoreAsync();
        }
        return _exitStopTask;
    }

    private async Task StopHostForExitCoreAsync()
    {
        Task stopTask = _hostRuntime.StopAndDisposeAsync();
        try
        {
            // 所有在途 Enable/PersistAsync 都先释放 gate；停机任务已在排队，不会被配置保存绕过。
            await _persistGate.WaitAsync().ConfigureAwait(true);
            _persistGate.Release();
            await stopTask.ConfigureAwait(true);
            HostStatusText = "主机未监听：退出前连接已完整排空。";
        }
        catch (Exception ex)
        {
            HostStatusText = "未排空，不安全关闭；请查看本机日志后重试。";
            _logger.LogError(ex, "关窗前传输主机或在途配置保存未能完整停止。");
            throw;
        }
    }

    private async Task<bool> StartHostAsync(CancellationToken cancellationToken, long intent,
        VideoQualitySettings? previewSettings, bool autoQuality)
    {
        RefreshHostProtocolStatus();
        try
        {
            TransportHostStartResult result = await _hostRuntime.StartAsync(cancellationToken, previewSettings,
                autoQuality)
                .ConfigureAwait(true);
            if (result.IsListening && _hostRuntime.IsListening && !cancellationToken.IsCancellationRequested &&
                CanEnableViewOnlySharing(intent))
            {
                HostStatusText = result.Failures.Count == 0
                    ? $"仅查看主机正在监听：{string.Join(", ", result.BoundAddresses)}"
                    : $"仅查看主机部分网卡可用：{string.Join(", ", result.BoundAddresses)}；另有 {result.Failures.Count} 个地址绑定失败（详见日志）。";
                HostStatusText += $"；本次 {ActivePreviewDescription(_hostRuntime.ActivePreviewSettings)}。";
                PublishDiscoveryCapabilities();
                return true;
            }

            if (intent == _sharingIntentVersion && !_isExiting)
            {
                HostStatusText = result.IsListening
                    ? "主机未监听：开启过程中查看权限或本机审批被撤销。"
                    : result.Failures.Count == 0
                        ? "主机未监听：无合格 RFC1918 网卡地址；未修改网络设置。"
                        : $"主机未监听：{result.Failures.Count} 个地址绑定失败（详见日志）。";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (intent == _sharingIntentVersion && !_isExiting)
            {
                HostStatusText = "主机未监听：启动已取消。";
            }
        }
        catch (Exception ex)
        {
            if (intent == _sharingIntentVersion && !_isExiting)
            {
                HostStatusText = "主机未监听：认证素材或网络初始化失败（详见日志）。";
            }
            _logger.LogError(ex, "仅查看传输主机未能启动。");
        }
        return false;
    }

    private void PublishDiscoveryCapabilities() =>
        _runtimeState.UpdateFromConfig(_config with
        {
            AllowViewing = !_isExiting && _config.AllowViewing &&
                _config.RequireLocalApprovalForUnknownController && _hostRuntime.IsListening,
            AllowControl = false,
        });

    private void UpdateConfig(AppConfig newConfig, string propertyName)
    {
        if (!HostSettingsReady || _isExiting)
        {
            OnPropertyChanged(propertyName);
            return;
        }
        if (newConfig.Equals(_config))
        {
            return;
        }

        Task? immediateStop = null;
        if (propertyName == nameof(AllowViewing) && !newConfig.AllowViewing)
        {
            // 在任何保存 I/O 前同步撤销监听和全部待审批，再异步完整 join。
            ++_sharingIntentVersion;
            _hostRuntime.BlockListening();
            _hostRuntime.BlockApprovals();
            RefreshHostProtocolStatus();
            immediateStop = _hostRuntime.StopAndDisposeAsync();
            _hostStopTask = immediateStop;
            HostStatusText = "正在停止仅查看主机并排空连接…";
        }
        else if (propertyName == nameof(AllowViewing) && newConfig.AllowViewing)
        {
            HostStatusText = "主机未监听：请在本机明确点击“开启仅查看共享”。";
        }

        _config = newConfig with { AllowControl = false };
        PublishDiscoveryCapabilities();
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(NeedsLocalApprovalRepair));
        _ = PersistAsync(_config, immediateStop, _sharingIntentVersion);
    }

    private void ReplaceConfig(AppConfig newConfig, bool raisePropertyChanged)
    {
        _config = newConfig;

        if (!raisePropertyChanged)
        {
            return;
        }

        OnPropertyChanged(nameof(AllowDiscovery));
        OnPropertyChanged(nameof(AllowViewing));
        OnPropertyChanged(nameof(AllowControl));
        OnPropertyChanged(nameof(RequireLocalApproval));
        OnPropertyChanged(nameof(NeedsLocalApprovalRepair));
        OnPropertyChanged(nameof(AutoStartOnLogin));
    }

    private async Task PersistAsync(AppConfig snapshot, Task? immediateStop, long intent)
    {
        await _persistGate.WaitAsync().ConfigureAwait(true);
        try
        {
            Exception? stopError = null;
            if (immediateStop is not null)
            {
                try
                {
                    await immediateStop.ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    stopError = ex;
                    if (intent == _sharingIntentVersion && !_isExiting)
                    {
                        HostStatusText = "未排空，不安全关闭；请查看本机日志。";
                    }
                    _logger.LogError(ex, "禁止查看时传输主机未能完整停止。");
                }
            }

            try
            {
                await _configStore.SaveAsync(snapshot).ConfigureAwait(true);
                if (!_isExiting) StatusText = "配置已保存。";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!_isExiting) StatusText = "配置保存失败。";
                if (!snapshot.AllowViewing && intent == _sharingIntentVersion && !_isExiting)
                {
                    HostStatusText = stopError is null
                        ? "主机已停止，但配置保存失败；重启前请修复配置。"
                        : "共享未排空且配置保存失败；请查看本机日志。";
                }
                _logger.LogError(ex, "保存配置失败。");
                return;
            }

            if (stopError is not null)
            {
                return;
            }
            if (immediateStop is not null && !_config.AllowViewing &&
                intent == _sharingIntentVersion && !_isExiting)
            {
                HostStatusText = "主机未监听：查看屏幕已关闭且连接已完整排空。";
            }
            // 保存配置（包括重新勾选查看/修复审批）不代表本轮操作者同意开启主机。
        }
        finally
        {
            _persistGate.Release();
        }
    }
}
