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
    private readonly object _workGate = new();
    private WorkLease? _work;
    private bool _stopping;
    private Task? _disposeTask;

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
        AccessSecretStore = new DpapiAccessSecretStore(
            Vault, _loggerFactory.CreateLogger<DpapiAccessSecretStore>());
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

    public DpapiAccessSecretStore AccessSecretStore { get; }

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

        // 先保存实例：即使启动中途失败，DisposeAsync 仍负责停掉它。
        await Discovery.StartAsync(cancellationToken);
    }

    /// <summary>合格网卡的 IPv4 地址——TransportHost 就监听这些。</summary>
    public IReadOnlyList<IPAddress> ListenAddresses() =>
        Bindings.GetBindings().Select(binding => binding.Address).ToArray();

    internal bool IsStopping { get { lock (_workGate) { return _stopping; } } }

    /// <summary>至多拥有一个附属工作；取得 lease 后，即使停止也必须由工作方交还。</summary>
    internal WorkLease? TryAcquireWork()
    {
        lock (_workGate)
        {
            if (_stopping || (_work is not null && !_work.Completion.IsCompletedSuccessfully)) { return null; }
            return _work = new WorkLease();
        }
    }

    internal Task StopWorkAsync()
    {
        WorkLease? work;
        lock (_workGate)
        {
            _stopping = true;
            work = _work;
        }
        return work is null ? Task.CompletedTask : work.StopAndJoinAsync();
    }

    /// <summary>附属工作完全归还后才释放 context；重复 Dispose 等待同一清理任务。</summary>
    public ValueTask DisposeAsync()
    {
        lock (_workGate)
        {
            _stopping = true;
            // 不让发现服务/日志订阅方在生命周期锁内执行同步代码。
            return new ValueTask(_disposeTask ??= Task.Run(DisposeCoreAsync));
        }
    }

    private async Task DisposeCoreAsync()
    {
        try { await StopWorkAsync().ConfigureAwait(false); }
        finally { await DisposeResourcesAsync().ConfigureAwait(false); }
    }

    private async Task DisposeResourcesAsync()
    {
        try
        {
            if (Discovery is not null)
            {
                using CancellationTokenSource budget = new(TimeSpan.FromSeconds(3));
                await Discovery.StopAsync(budget.Token).ConfigureAwait(false);
                // 产品 StopAsync 会吞取消；不能把本层预算耗尽伪报为已完成。
                budget.Token.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            try
            {
                _certificates.Dispose();
            }
            finally
            {
                _loggerFactory.Dispose();
            }
        }
    }

    /// <summary>不保存密钥结果；只拥有取消及工作归还信号，取消回调绝不在持锁线程同步执行。</summary>
    internal sealed class WorkLease : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _cancellation;
        private bool _disposed;

        internal Task Completion => _completion.Task;
        internal bool IsStopRequested => _stop.IsCancellationRequested;

        internal Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work) =>
            Task.Run(() => work(_stop.Token));

        internal Task RequestStop()
        {
            lock (_gate)
            {
                if (_disposed) { return _cancellation ?? Task.CompletedTask; }
                return _cancellation ??= _stop.CancelAsync();
            }
        }

        internal async Task StopAndJoinAsync()
        {
            try { await RequestStop().ConfigureAwait(false); }
            finally { await Completion.ConfigureAwait(false); }
        }

        public async ValueTask DisposeAsync()
        {
            Exception? fault = null;
            try { await RequestStop().ConfigureAwait(false); }
            catch (Exception ex) { fault = ex; throw; }
            finally
            {
                lock (_gate) { _disposed = true; }
                // 已 join 取消回调，且后续 RequestStop 不再访问 CTS；不在锁内 Dispose。
                _stop.Dispose();
                if (fault is null) { _completion.TrySetResult(); }
                else { _completion.TrySetException(fault); }
            }
        }
    }
}
