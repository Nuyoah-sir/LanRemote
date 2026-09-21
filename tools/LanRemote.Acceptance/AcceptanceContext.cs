using System.Net;
using System.Security.Cryptography.X509Certificates;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Configuration;
using LanRemote.Core.Infrastructure;
using LanRemote.Core.Models;
using LanRemote.Discovery;
using LanRemote.Discovery.Networking;
using LanRemote.Security.Certificates;
using LanRemote.Security.Identity;
using LanRemote.Security.Secrets;
using Microsoft.Extensions.Logging;

namespace LanRemote.Acceptance;

/// <summary>
/// 把 M1/M2 的服务按 <c>App.xaml.cs</c> 里<b>完全一样</b>的方式装配起来。
/// </summary>
/// <remarks>
/// <b>刻意复刻而不是另写一套</b>：验收要证的是「真实产品路径能跑通」，
/// 如果验收器自己拼一套轻量依赖，那它验的就是验收器自己。
/// 证书加载、DPAPI、PFX 往返、网卡筛选，全部走产品代码。
/// </remarks>
internal sealed class AcceptanceContext : IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly DeviceCertificateService _certificates;

    public AcceptanceContext(LogLevel minimumLevel)
    {
        _loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(minimumLevel)
            .AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss.fff ";
            }));

        Paths = AppPaths.Default;
        Vault = new DpapiSecretVault(Paths, _loggerFactory.CreateLogger<DpapiSecretVault>());
        _certificates = new DeviceCertificateService(
            Vault,
            _loggerFactory.CreateLogger<DeviceCertificateService>());

        INetworkBindingProvider bindings =
            new LocalNetworkBindingProvider(new SystemNetworkInterfaceSource());

        Bindings = bindings;
        SubnetPolicy = new SubnetPolicy(bindings);
        RuntimeState = new DiscoveryRuntimeState();
    }

    public AppPaths Paths { get; }

    public DpapiSecretVault Vault { get; }

    public INetworkBindingProvider Bindings { get; }

    public ISubnetPolicy SubnetPolicy { get; }

    public DiscoveryRuntimeState RuntimeState { get; }

    public ILoggerFactory Loggers => _loggerFactory;

    public DeviceIdentity Identity { get; private set; } = null!;

    public AppConfig Config { get; private set; } = new();

    public DeviceCertificate Certificate { get; private set; } = null!;

    public LanDiscoveryService Discovery { get; private set; } = null!;

    /// <summary>加载身份与配置，并初始化发现运行时。</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        DeviceIdentityService identityService = new(
            Vault,
            _certificates,
            _loggerFactory.CreateLogger<DeviceIdentityService>());

        Identity = await identityService.GetOrCreateAsync(cancellationToken);
        Certificate = await _certificates.GetOrCreateAsync(cancellationToken);

        AppConfigStore store = new(Paths);
        Config = await store.LoadAsync(cancellationToken);

        // Initialize 之前拿不到快照（这是隐私保证，不是限制）。
        RuntimeState.Initialize(Identity, Config);
    }

    /// <summary>起发现服务（announce + 扫描）。</summary>
    public async Task StartDiscoveryAsync(CancellationToken cancellationToken)
    {
        Discovery = new LanDiscoveryService(
            Bindings,
            RuntimeState,
            _loggerFactory.CreateLogger<LanDiscoveryService>());

        await Discovery.StartAsync(cancellationToken);
    }

    /// <summary>合格网卡的 IPv4 地址——TransportHost 就监听这些。</summary>
    public IReadOnlyList<IPAddress> ListenAddresses() =>
        Bindings.GetBindings().Select(binding => binding.Address).ToArray();

    public void Dispose()
    {
        _certificates.Dispose();
        _loggerFactory.Dispose();
    }
}
