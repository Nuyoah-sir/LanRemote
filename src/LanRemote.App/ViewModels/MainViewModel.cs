using System.IO;
using System.Security.Cryptography;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Configuration;
using LanRemote.Core.Encoding;
using LanRemote.Core.Infrastructure;
using LanRemote.Core.Models;
using LanRemote.Security.Identity;
using Microsoft.Extensions.Logging;

namespace LanRemote.App.ViewModels;

/// <summary>
/// 主窗口 ViewModel。
/// </summary>
/// <remarks>
/// <para>M0 打通配置持久化；M1 接入真实的本机身份（设备码、证书指纹）与访问密钥操作。</para>
/// <para><b>安全约定（AGENTS.md 第 3 条）</b>：所有日志只允许出现设备名、设备码、证书指纹前缀；
/// <b>绝不</b>记录访问密钥本体。密钥只在用户显式点击「显示 / 复制」的瞬间存在于内存中。</para>
/// <para>已知实现细节：C# 的 <see cref="string"/> 是不可变的，因此隐藏密钥时只能丢弃引用，
/// 无法保证进程内存中不残留副本。这是 .NET 的语言级限制，不是偷懒。</para>
/// </remarks>
public sealed class MainViewModel : ViewModelBase
{
    private readonly AppPaths _paths;
    private readonly AppConfigStore _configStore;
    private readonly DeviceIdentityService _identityService;
    private readonly IAccessSecretStore _secretStore;
    private readonly ILogger<MainViewModel> _logger;

    private AppConfig _config = new();
    private DeviceIdentity? _identity;
    private string? _revealedAccessKey;
    private bool _isBusy;
    private string _statusText = "正在初始化…";
    private bool _isAccessKeyVisible;

    /// <summary>由 DI 容器构造。</summary>
    /// <param name="paths">数据目录定位。</param>
    /// <param name="configStore">配置存储。</param>
    /// <param name="identityService">设备身份服务。</param>
    /// <param name="secretStore">访问密钥存储。</param>
    /// <param name="logger">日志器。</param>
    public MainViewModel(
        AppPaths paths,
        AppConfigStore configStore,
        DeviceIdentityService identityService,
        IAccessSecretStore secretStore,
        ILogger<MainViewModel> logger)
    {
        _paths = paths;
        _configStore = configStore;
        _identityService = identityService;
        _secretStore = secretStore;
        _logger = logger;
    }

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

    /// <summary>异步加载配置与本机身份。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>由 View 的事件 handler 调用，不阻塞 UI 线程。</remarks>
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

            StatusText = File.Exists(_paths.ConfigFilePath)
                ? "已从磁盘加载配置与本机身份。"
                : "未发现配置文件，使用安全默认值。";

            // 注意：这里只记录设备名/设备码/开关状态。访问密钥永远不进日志。
            _logger.LogInformation(
                "本机={MachineName}，设备码={DeviceCode}，允许被发现={AllowDiscovery}，允许控制={AllowControl}，陌生控制端需本机确认={RequireApproval}",
                LocalDeviceName,
                identity.DeviceCode,
                _config.AllowDiscovery,
                _config.AllowControl,
                _config.RequireLocalApprovalForUnknownController);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            ReplaceConfig(new AppConfig(), raisePropertyChanged: true);
            StatusText = "配置加载失败，已回落到安全默认值。";
            _logger.LogWarning(ex, "加载配置失败，使用默认配置。");
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            // DPAPI 失败通常意味着换了用户或用户配置文件损坏，不是可以自愈的错误。
            StatusText = "本机秘密无法读取（可能更换了 Windows 用户账户）。";
            _logger.LogError(ex, "访问 secrets.bin 失败。");
        }
        finally
        {
            IsBusy = false;
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
