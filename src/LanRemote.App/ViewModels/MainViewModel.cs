using System.Collections.ObjectModel;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Configuration;
using LanRemote.Core.Encoding;
using LanRemote.Core.Infrastructure;
using LanRemote.Core.Models;
using LanRemote.Discovery;
using LanRemote.Security.Identity;
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
    private readonly ILogger<MainViewModel> _logger;
    private readonly SynchronizationContext? _uiContext;

    private AppConfig _config = new();
    private DeviceIdentity? _identity;
    private string? _revealedAccessKey;
    private bool _isBusy;
    private string _statusText = "正在初始化…";
    private bool _isAccessKeyVisible;

    private CancellationTokenSource? _watchCts;
    private CancellationTokenSource? _cleanupCts;
    private Task? _watchTask;
    private Task? _cleanupTask;

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
        ILogger<MainViewModel> logger)
    {
        _paths = paths;
        _configStore = configStore;
        _identityService = identityService;
        _secretStore = secretStore;
        _discovery = discovery;
        _runtimeState = runtimeState;
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
        get => _config.AllowControl;
        set => UpdateConfig(_config with { AllowControl = value }, nameof(AllowControl));
    }

    /// <summary>陌生控制端是否需要本机确认。</summary>
    public bool RequireLocalApproval
    {
        get => _config.RequireLocalApprovalForUnknownController;
        set => UpdateConfig(
            _config with { RequireLocalApprovalForUnknownController = value },
            nameof(RequireLocalApproval));
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
        IsBusy = true;
        StatusText = "正在加载配置…";

        try
        {
            AppConfig loaded = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(true);
            ReplaceConfig(loaded, raisePropertyChanged: true);

            DeviceIdentity identity = await _identityService.GetOrCreateAsync(cancellationToken)
                .ConfigureAwait(true);
            SetIdentity(identity);

            // 身份与配置都就绪之后才允许 discovery 看见它们。
            _runtimeState.Initialize(identity, _config);

            // 注意：这里只记录设备名/设备码/开关状态。访问密钥永远不进日志。
            _logger.LogInformation(
                "本机={MachineName}，设备码={DeviceCode}，允许被发现={AllowDiscovery}，允许控制={AllowControl}，陌生控制端需本机确认={RequireApproval}",
                LocalDeviceName,
                identity.DeviceCode,
                _config.AllowDiscovery,
                _config.AllowControl,
                _config.RequireLocalApprovalForUnknownController);

            await StartDiscoveryAsync(cancellationToken).ConfigureAwait(true);

            StatusText = File.Exists(_paths.ConfigFilePath)
                ? "已从磁盘加载配置与本机身份。"
                : "未发现配置文件，使用安全默认值。";
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            ReplaceConfig(new AppConfig(), raisePropertyChanged: true);
            StatusText = "配置加载失败，已回落到安全默认值。";
            _logger.LogWarning(ex, "加载配置失败，使用默认配置。");
        }
        catch (CryptographicException ex)
        {
            // DPAPI 失败通常意味着换了用户或用户配置损坏，不是可以自愈的错误。
            StatusText = "本机秘密无法读取（可能更换了 Windows 用户账户）。";
            _logger.LogError(ex, "访问 secrets.bin 失败。");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 停止设备流消费与 TTL 清理循环。
    /// </summary>
    /// <remarks>只停止 ViewModel 自己的后台任务；socket 的释放由
    /// <see cref="IDiscoveryService.StopAsync"/> 负责（App.OnExit 调用）。</remarks>
    public async Task StopDiscoveryAsync()
    {
        _watchCts?.Cancel();
        _cleanupCts?.Cancel();

        List<Task> tasks = new();
        if (_watchTask is not null)
        {
            tasks.Add(_watchTask);
        }

        if (_cleanupTask is not null)
        {
            tasks.Add(_cleanupTask);
        }

        if (tasks.Count > 0)
        {
            try
            {
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                _logger.LogDebug(ex, "停止设备流/清理循环时超时或取消。");
            }
        }

        _watchCts?.Dispose();
        _cleanupCts?.Dispose();
        _watchCts = null;
        _cleanupCts = null;
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

    private async Task StartDiscoveryAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _discovery.StartAsync(cancellationToken).ConfigureAwait(true);

            StartDeviceWatch();
            StartTtlCleanupLoop();

            StatusText = "局域网发现已启动。";
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException
                                     or UnauthorizedAccessException)
        {
            // 端口被占用、网卡异常等都不能影响 M1：身份照旧，只是发现不可用。
            StatusText = "本机身份已加载，但局域网发现启动失败。";
            _logger.LogError(ex, "局域网发现启动失败；本机身份保持原样。");
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

    private void UpdateConfig(AppConfig newConfig, string propertyName)
    {
        if (newConfig.Equals(_config))
        {
            return;
        }

        _config = newConfig;

        // 先让 discovery 看到新的快照：下一次 announce / probe 响应立即使用最新值，
        // 不需要重启 UDP 服务，也不需要等下一轮读盘。
        _runtimeState.UpdateFromConfig(_config);

        OnPropertyChanged(propertyName);
        _ = PersistAsync();
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
        OnPropertyChanged(nameof(AutoStartOnLogin));
    }

    private async Task PersistAsync()
    {
        try
        {
            await _configStore.SaveAsync(_config).ConfigureAwait(true);
            StatusText = "配置已保存。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = "配置保存失败。";
            _logger.LogError(ex, "保存配置失败。");
        }
    }
}
