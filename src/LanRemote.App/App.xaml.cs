using System.Diagnostics;
using System.Windows;
using LanRemote.App.Logging;
using LanRemote.App.Services;
using LanRemote.App.ViewModels;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Configuration;
using LanRemote.Core.Infrastructure;
using LanRemote.Discovery;
using LanRemote.Discovery.Networking;
using LanRemote.Security.Certificates;
using LanRemote.Security.Identity;
using LanRemote.Security.Secrets;
using LanRemote.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LanRemote.App;

    /// <summary>退出协调结果；故障是否真正释放资源根必须由子系统单独证明。</summary>
public enum ExitDrainStatus
{
    Pending,
    Clean,
    Unproven,
    DrainedWithDiagnostics,
}

/// <summary>
/// WPF 应用程序入口：负责单实例判定、DI 宿主、日志落地与未处理异常兜底。
/// </summary>
/// <remarks>
/// 所有后台循环最终都必须能被取消；退出时才真正停止网络监听（M3 起生效）。
/// UI 线程不做阻塞 I/O，配置 I/O 一律异步（06_DEV_STANDARDS.md 第 7 节）。
/// </remarks>
public partial class App : Application
{
    /// <summary>位于 %LOCALAPPDATA%\LanRemote 的数据目录定位。</summary>
    public static AppPaths Paths { get; private set; } = AppPaths.Default;

    private enum ExitStop { Client, Loading, LocalHost, Discovery, Watcher, DiHost }

    private IHost? _host;
    private SingleInstanceGuard? _singleInstanceGuard;
    private readonly object _exitGate = new();
    private readonly List<Task> _exitRecheckTasks = new();
    private Task? _stopBeforeExitTask;
    private Task[]? _originalExitStops;
    private Task? _latestVmExitRecheck;
    private HostRuntimeService? _exitHostRuntime;
    private ClientConnectService? _exitClientRuntime;
    private AggregateException? _originalExitFailure;
    private bool _stoppedForExit;
    private bool _shutdownAuthorized;

    /// <summary>全局 DI 服务提供器。</summary>
    public static IServiceProvider Services =>
        ((App)Current)._host?.Services ?? throw new InvalidOperationException("DI 宿主尚未初始化。");

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        SessionEnding += OnSessionEnding;

        // 单实例：第二个实例直接退出，避免端口抢占与重复的“被控制”指示。
        _singleInstanceGuard = SingleInstanceGuard.TryAcquire();
        if (_singleInstanceGuard is null)
        {
            MessageBox.Show(
                "LanRemote 已经在运行。请查看托盘或已有的主窗口。",
                "LanRemote",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            _shutdownAuthorized = true; // 无宿主、无网络任务的第二个实例直接退出。
            Shutdown();
            return;
        }

        Paths = AppPaths.Default;
        Paths.EnsureCreated();

        _host = BuildHost(Paths);
        _host.Start();

        ILogger<App> logger = _host.Services.GetRequiredService<ILogger<App>>();
        logger.LogInformation(
            "LanRemote 启动。数据目录={RootDirectory}，配置={ConfigFilePath}",
            Paths.RootDirectory,
            Paths.ConfigFilePath);

        MainWindow window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
    }

    /// <summary>仅暴露证明级别，不向窗口或外部返回任何原始任务、异常或资源句柄。</summary>
    public ExitDrainStatus ExitDrainState
    {
        get
        {
            Task? coordinator;
            Task[]? originals;
            Task[] rechecks;
            HostRuntimeService? runtime;
            ClientConnectService? client;
            lock (_exitGate)
            {
                coordinator = _stopBeforeExitTask;
                originals = _originalExitStops;
                rechecks = _exitRecheckTasks.ToArray();
                runtime = _exitHostRuntime;
                client = _exitClientRuntime;
            }

            // 第二个实例没有 DI 宿主和原网络任务；产品实例必须逐项核实六个原任务。
            if (_host is null) return _stoppedForExit ? ExitDrainStatus.Clean : ExitDrainStatus.Pending;
            if (coordinator is null || !coordinator.IsCompleted) return ExitDrainStatus.Pending;
            return originals is null ? ExitDrainStatus.Unproven
                : EvaluateExitDrainWithClientEvidence(originals, rechecks,
                    runtime?.IsFullyDrained == true, client?.IsFullyDrained == true, client);
        }
    }

    /// <summary>
    /// 只用六类原停机任务与产品 Host/Client 资源根的成功报告决定出口。
    /// 任一清理异常都可能意味着 socket、CTS 或 registry 根未释放；重试成功不洗绿历史故障。
    /// </summary>
    internal static ExitDrainStatus EvaluateExitDrainState(
        IReadOnlyList<Task> originalStops, IReadOnlyList<Task> recheckTasks,
        bool productHostFullyDrained, bool productClientFullyDrained)
    {
        if (originalStops.Count != 6) return ExitDrainStatus.Unproven;
        if (originalStops.Any(task => !task.IsCompleted) ||
            recheckTasks.Any(task => !task.IsCompleted)) return ExitDrainStatus.Pending;
        if (!productHostFullyDrained || !productClientFullyDrained ||
            originalStops.Any(task => !task.IsCompletedSuccessfully) ||
            recheckTasks.Any(task => !task.IsCompletedSuccessfully))
            return ExitDrainStatus.Unproven;
        return ExitDrainStatus.Clean;
    }

    /// <summary>仅允许客户端原停机任务有诊断、且客户端自身证明同一任务的全部资源根已释放。</summary>
    internal static ExitDrainStatus EvaluateExitDrainWithClientEvidence(
        IReadOnlyList<Task> originalStops, IReadOnlyList<Task> recheckTasks,
        bool productHostFullyDrained, bool productClientFullyDrained, ClientConnectService? client)
    {
        ExitDrainStatus strict = EvaluateExitDrainState(originalStops, recheckTasks,
            productHostFullyDrained, productClientFullyDrained);
        if (strict != ExitDrainStatus.Unproven) return strict;
        if (originalStops.Count != 6 || !productHostFullyDrained ||
            originalStops.Any(task => !task.IsCompleted) ||
            recheckTasks.Any(task => !task.IsCompleted) ||
            originalStops.Skip(1).Any(task => !task.IsCompletedSuccessfully) ||
            recheckTasks.Any(task => !task.IsCompletedSuccessfully))
            return ExitDrainStatus.Unproven;

        Task originalClient = originalStops[(int)ExitStop.Client];
        return originalClient.IsFaulted && client is not null &&
            ReferenceEquals(originalClient, client.LastCleanupTask) &&
            client.HasSuccessfulDrainEvidenceFor(originalClient)
                ? ExitDrainStatus.DrainedWithDiagnostics : ExitDrainStatus.Unproven;
    }

    /// <summary>进行中共享同一任务；故障后的下一次请求仅复核原任务，不重置其错误。</summary>
    public Task StopBeforeExitAsync()
    {
        lock (_exitGate)
        {
            if (_stopBeforeExitTask is { IsCompleted: false } inFlight) return inFlight;
            if (_stopBeforeExitTask is { IsCompletedSuccessfully: true } stopped &&
                ExitDrainState is ExitDrainStatus.Clean or ExitDrainStatus.DrainedWithDiagnostics)
                return stopped;

            bool firstAttempt = _stopBeforeExitTask is null;
            TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopBeforeExitTask = completion.Task;
            // 先发布共享任务，再启动停机；即使同步前缀重入 Closing/SessionEnding 也不会重复启动。
            _ = CompleteExitAttemptAsync(completion, firstAttempt);
            return completion.Task;
        }
    }

    private async Task CompleteExitAttemptAsync(TaskCompletionSource completion, bool firstAttempt)
    {
        try
        {
            if (firstAttempt) await StopBeforeExitCoreAsync().ConfigureAwait(true);
            else await RecheckStopBeforeExitCoreAsync().ConfigureAwait(true);
            completion.TrySetResult();
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
        }
    }

    private async Task StopBeforeExitCoreAsync()
    {
        IHost? host = _host;
        if (host is null)
        {
            _stoppedForExit = true;
            return;
        }

        MainWindow window = host.Services.GetRequiredService<MainWindow>();
        MainViewModel viewModel = host.Services.GetRequiredService<MainViewModel>();
        _exitHostRuntime = host.Services.GetRequiredService<HostRuntimeService>();
        _exitClientRuntime = host.Services.GetRequiredService<ClientConnectService>();
        // 先阻止新认证，再排空客户端两条 TLS、原帧读/解码和首次身份加载；
        // 最后才排空本机 Host。OnExit 不负责第一次 await。
        viewModel.BeginExit();
        // 任一子系统故障也不能跳过其他独立原操作的关闭与 join。
        Task client = BeginStop(_exitClientRuntime.StopAndDisposeAsync);
        Task loading = BeginStop(window.StopLoadingAsync);
        Task localHost = BeginStop(viewModel.StopHostForExitAsync);
        // 有界 StopAsync 可吞预算超时；产品关窗必须持有发现循环的严格原任务。
        Task discovery = BeginStop(() => host.Services.GetRequiredService<LanDiscoveryService>()
            .JoinAndDisposeAsync());
        Task watcher = BeginStop(viewModel.StopDiscoveryAsync);
        Task diHost = BeginStop(() => host.StopAsync());
        Task[] stops = [client, loading, localHost, discovery, watcher, diHost];
        _originalExitStops = stops; // 永不以重试任务覆盖首次六类 Task 的身份及原始错误树。
        Task joined = Task.WhenAll(stops);
        try
        {
            await joined.ConfigureAwait(true);
        }
        catch (Exception)
        {
            // WhenAll 结束意味着六项均已终结；下面保留每项 Task.Exception 的完整嵌套，
            // 包括 Transport 报告中的原 OCE/IOException，不能只取首错或吞掉异常。
        }

        _originalExitFailure = CollectOriginalExitErrors(stops);
        if (_originalExitFailure is not null)
        {
            host.Services.GetRequiredService<ILogger<App>>().LogError(_originalExitFailure,
                "首次应用停机的原任务存在诊断；保留原错误树，不声明干净排空。");
        }
        CompleteExitDecision(stops);
    }

    private async Task RecheckStopBeforeExitCoreAsync()
    {
        Task[] stops = _originalExitStops ??
            throw new InvalidOperationException("首次停机未能建立六项原任务，资源状态无法证明。");
        // 通常 WhenAll 已经等待完六项。若初始化阶段异常打断了协调者，仍绝不跳过原任务。
        if (stops.Any(task => !task.IsCompleted))
        {
            try { await Task.WhenAll(stops).ConfigureAwait(true); }
            catch (Exception) { /* 原故障保留在六个 Task 上，并由下方统一上报。 */ }
        }
        if (stops.Any(task => !task.IsCompleted))
            throw new InvalidOperationException("首次停机原任务仍在运行，不允许执行退出复核。");
        _originalExitFailure ??= CollectOriginalExitErrors(stops);

        IHost host = _host ?? throw new InvalidOperationException("DI 宿主已丢失，资源状态无法证明。");
        HostRuntimeService runtime = _exitHostRuntime ??
            throw new InvalidOperationException("产品主机停机证明不可用。");
        MainViewModel viewModel = host.Services.GetRequiredService<MainViewModel>();
        if (!stops[(int)ExitStop.LocalHost].IsCompletedSuccessfully &&
            _latestVmExitRecheck?.IsCompletedSuccessfully != true)
        {
            // VM 包含配置保存 gate 的等待；仅这条具备明确重试能力，不覆盖原 VM 诊断。
            Task vmRetry = BeginStop(viewModel.StopHostForExitAsync);
            TrackExitRecheck(vmRetry, vm: true);
            try { await vmRetry.ConfigureAwait(true); }
            catch (Exception error)
            {
                host.Services.GetRequiredService<ILogger<App>>().LogError(error,
                    "复核 VM 退出仍有诊断；原任务及资源证明保持不变。");
            }
        }
        if (!runtime.IsFullyDrained)
        {
            // HostRuntime 缓存同一 Host 的原 Stop/Dispose；失败时可能返回原 fault，
            // 此时必须继续 Unproven，不得借第二次调用重复 Stop 或宣称已释放 HostRegistry。
            Task hostRetry = BeginStop(runtime.StopAndDisposeAsync);
            TrackExitRecheck(hostRetry, vm: false);
            try { await hostRetry.ConfigureAwait(true); }
            catch (Exception error)
            {
                host.Services.GetRequiredService<ILogger<App>>().LogError(error,
                    "复核产品 Host 停机仍有诊断；资源根未被证明释放。");
            }
        }
        CompleteExitDecision(stops);
    }

    private void TrackExitRecheck(Task task, bool vm)
    {
        lock (_exitGate)
        {
            if (!_exitRecheckTasks.Contains(task)) _exitRecheckTasks.Add(task);
            if (vm) _latestVmExitRecheck = task;
        }
    }

    private void CompleteExitDecision(Task[] stops)
    {
        Task[] rechecks;
        lock (_exitGate)
        {
            rechecks = _exitRecheckTasks.ToArray();
        }
        ExitDrainStatus state = EvaluateExitDrainWithClientEvidence(stops, rechecks,
            _exitHostRuntime?.IsFullyDrained == true, _exitClientRuntime?.IsFullyDrained == true,
            _exitClientRuntime);
        if (state is ExitDrainStatus.Clean or ExitDrainStatus.DrainedWithDiagnostics)
        {
            _stoppedForExit = true;
            return;
        }
        if (_originalExitFailure is not null) throw _originalExitFailure;
        _host?.Services.GetService<ILogger<App>>()?.LogError(
            "退出复核未能证明全部原任务与产品主机资源根已排空；继续保持运行。");
        throw new InvalidOperationException("停机未能证明全部原任务与产品主机资源根已排空。");
    }

    private static AggregateException? CollectOriginalExitErrors(Task[] stops)
    {
        List<Exception> errors = new();
        foreach (Task stop in stops)
        {
            if (stop.Exception is { } failure) errors.Add(failure); // 不 Flatten：保留 Transport 的原报告层级。
            else if (stop.IsCanceled) errors.Add(new TaskCanceledException(stop));
        }
        return errors.Count == 0 ? null : new AggregateException("首次应用停机的原任务错误。", errors);
    }

    private static Task BeginStop(Func<Task> begin)
    {
        try
        {
            return begin();
        }
        catch (Exception error)
        {
            return Task.FromException(error);
        }
    }

    /// <summary>Windows 注销/关机先拒绝本次结束，待原任务完整退出后主动结束应用。</summary>
    private async void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        if (_shutdownAuthorized || ExitDrainState is ExitDrainStatus.Clean or ExitDrainStatus.DrainedWithDiagnostics)
            return;
        e.Cancel = true;
        try
        {
            await StopBeforeExitAsync().ConfigureAwait(true);
            // 本次 Windows 会话结束请求已取消，不能回写 e.Cancel；但应用资源根已证实排空，
            // 应显式退出，而不是把主窗留下在无法再开启服务的终态。
            ShutdownAfterDrain();
        }
        catch (Exception error)
        {
            _host?.Services.GetService<ILogger<App>>()?.LogError(error,
                "Windows 会话结束时未能证实资源根排空；继续阻止会话结束。");
        }
    }

    /// <summary>正常关闭只接受六项原任务成功，或仅客户端原任务带诊断且同一资源根已证明释放。</summary>
    public void ShutdownAfterDrain()
    {
        if (!_stoppedForExit || ExitDrainState is not (ExitDrainStatus.Clean or ExitDrainStatus.DrainedWithDiagnostics))
            throw new InvalidOperationException("应用资源根尚未证实排空，不可走正常退出入口。");
        _shutdownAuthorized = true;
        Shutdown();
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            // WPF Exit 不可异步等待；未走统一关窗/显式Shutdown的场合先尽力撤销新访问，
            // 不能把进程强制退出误报为已完整排空。
            if (_host is not null && !_shutdownAuthorized)
            {
                _host.Services.GetService<MainViewModel>()?.BeginExit();
                _host.Services.GetService<ILogger<App>>()?.LogError(
                    "应用异常退出未经过经证实的资源排空；不声明资源已排空。");
            }
            _host?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"释放已停止的 DI 宿主失败: {ex}");
        }
        finally
        {
            _singleInstanceGuard?.Dispose();
            base.OnExit(e);
        }
    }

    private static IHost BuildHost(AppPaths paths) =>
        Host.CreateApplicationBuilder()
            .Configure(paths)
            .Build();

    private void OnDispatcherUnhandledException(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        // 错误信息不得泄露访问密钥等秘密，完整异常进入日志而非弹窗（05_UI_UX_SPEC.md 第 8 节）。
        _host?.Services.GetService<ILogger<App>>()?.LogError(
            e.Exception,
            "UI 线程未处理异常，已拦截以避免进程崩溃。");

        MessageBox.Show(
            "发生未预期的错误；已记录本机日志。不会在窗口展示可能包含访问密钥的异常内容。",
            "LanRemote",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Exception? exception = e.ExceptionObject as Exception;
        _host?.Services.GetService<ILogger<App>>()?.LogError(
            exception,
            "非 UI 线程未处理异常。IsTerminating={IsTerminating}",
            e.IsTerminating);
    }
}

internal static class HostBuilderConfiguration
{
    /// <summary>把 LanRemote 的服务与日志装配到通用宿主上。</summary>
    /// <param name="builder">宿主构建器。</param>
    /// <param name="paths">数据目录定位。</param>
    /// <returns>同一个构建器，便于链式调用。</returns>
    public static HostApplicationBuilder Configure(this HostApplicationBuilder builder, AppPaths paths)
    {
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
        builder.Logging.AddDebug();
        builder.Logging.AddProvider(new SimpleFileLoggerProvider(paths.LogsDirectory));

        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton<AppConfigStore>();

        // M1：设备身份与安全存储。
        // 显式工厂而不是 AddSingleton<T>()：依赖容器对「带默认值的可选参数」的支持并不稳定，
        // 显式写出来既不踩坑，也让构造契约一目了然。
        builder.Services.AddSingleton(
            provider => new DpapiSecretVault(
                provider.GetRequiredService<AppPaths>(),
                provider.GetRequiredService<ILogger<DpapiSecretVault>>()));
        builder.Services.AddSingleton(
            provider => new DeviceCertificateService(
                provider.GetRequiredService<DpapiSecretVault>(),
                provider.GetRequiredService<ILogger<DeviceCertificateService>>()));
        builder.Services.AddSingleton(
            provider => new DeviceIdentityService(
                provider.GetRequiredService<DpapiSecretVault>(),
                provider.GetRequiredService<DeviceCertificateService>(),
                provider.GetRequiredService<ILogger<DeviceIdentityService>>()));
        builder.Services.AddSingleton<IAccessSecretStore>(
            provider => new DpapiAccessSecretStore(
                provider.GetRequiredService<DpapiSecretVault>(),
                provider.GetRequiredService<ILogger<DpapiAccessSecretStore>>()));

        // M2：局域网发现。
        builder.Services.AddSingleton<INetworkInterfaceSource, SystemNetworkInterfaceSource>();
        builder.Services.AddSingleton<INetworkBindingProvider>(
            provider => new LocalNetworkBindingProvider(
                provider.GetRequiredService<INetworkInterfaceSource>()));
        builder.Services.AddSingleton<ISubnetPolicy>(
            provider => new SubnetPolicy(provider.GetRequiredService<INetworkBindingProvider>()));
        builder.Services.AddSingleton<DiscoveryRuntimeState>();

        // concrete 与 interface 必须指向同一个实例，否则会出现两套 UDP 服务。
        builder.Services.AddSingleton<LanDiscoveryService>();
        builder.Services.AddSingleton<IDiscoveryService>(
            provider => provider.GetRequiredService<LanDiscoveryService>());

        builder.Services.AddSingleton<ILocalApprovalGate>(
            provider => new WpfLocalApprovalGate(
                Application.Current.Dispatcher,
                provider.GetRequiredService<ILogger<WpfLocalApprovalGate>>()));
        builder.Services.AddSingleton<HostRuntimeService>();
        builder.Services.AddSingleton<ControlClientConnector>();
        builder.Services.AddSingleton<ClientConnectService>();

        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        return builder;
    }
}
