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
/// <para><b>刻意复刻而不是另写一套</b>：验收要证的是「真实产品路径能跑通」，
/// 如果验收器自己拼一套轻量依赖，那它验的就是验收器自己。
/// 证书加载、DPAPI、PFX 往返、网卡筛选，全部走产品代码。</para>
/// <para><b>为什么是 <see cref="IAsyncDisposable"/> 而不是 <see cref="IDisposable"/></b>：
/// 发现服务持有着 UDP 45872 的 socket。<c>LanDiscoveryService.StopAsync</c> 是幂等的，
/// 但<b>停掉之后不能再启动</b>——所以每个上下文必须在结束前把自己的发现服务停干净，
/// 否则下一个上下文 <c>StartAsync</c> 会绑不上端口。而那个失败现象是
/// 「发现不到对端」，看起来像环境问题，实际是前一个场景留下的残留。</para>
/// </remarks>
internal sealed class AcceptanceContext : IAsyncDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly DeviceCertificateService _certificates;
    private bool _discoveryStarted;
    private bool _disposed;

    public AcceptanceContext(LogLevel minimumLevel, Action<string>? logSink = null)
    {
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .SetMinimumLevel(minimumLevel)
                .AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.TimestampFormat = "HH:mm:ss.fff ";
                });

            // WinExe 没有控制台，上面那个 console provider 在验收里是死信投递；
            // 把同一批事件转发进验收日志，失败时才有现场可看。
            if (logSink is not null)
            {
                builder.AddProvider(new AcceptanceLoggerProvider(logSink, minimumLevel));
            }
        });

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
        _discoveryStarted = true;
    }

    /// <summary>合格网卡的 IPv4 地址——TransportHost 就监听这些。</summary>
    public IReadOnlyList<IPAddress> ListenAddresses() =>
        Bindings.GetBindings().Select(binding => binding.Address).ToArray();

    /// <summary>停掉发现服务并释放日志工厂与证书服务。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_discoveryStarted)
        {
            using CancellationTokenSource budget = new(TimeSpan.FromSeconds(3));

            try
            {
                await Discovery.StopAsync(budget.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 收尾失败不该改变任何场景结论；socket 由超时兜底释放。
            }
        }

        _certificates.Dispose();
        _loggerFactory.Dispose();
    }
}
