using System.Diagnostics;
using System.Windows;
using LanRemote.App.Logging;
using LanRemote.App.ViewModels;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Configuration;
using LanRemote.Core.Infrastructure;
using LanRemote.Discovery;
using LanRemote.Discovery.Networking;
using LanRemote.Security.Certificates;
using LanRemote.Security.Identity;
using LanRemote.Security.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LanRemote.App;

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

    private IHost? _host;
    private SingleInstanceGuard? _singleInstanceGuard;

    /// <summary>全局 DI 服务提供器。</summary>
    public static IServiceProvider Services =>
        ((App)Current)._host?.Services ?? throw new InvalidOperationException("DI 宿主尚未初始化。");

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        // 单实例：第二个实例直接退出，避免端口抢占与重复的“被控制”指示。
        _singleInstanceGuard = SingleInstanceGuard.TryAcquire();
        if (_singleInstanceGuard is null)
        {
            MessageBox.Show(
                "LanRemote 已经在运行。请查看托盘或已有的主窗口。",
                "LanRemote",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
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

    /// <inheritdoc />
    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_host is not null)
            {
                // 先停 discovery：它会 complete channel 并关闭 socket。
                // StopAsync 是幂等的，即使 ViewModel 已经停过也不会报错。
                try
                {
                    IDiscoveryService? discovery = _host.Services.GetService<IDiscoveryService>();
                    if (discovery is not null)
                    {
                        using CancellationTokenSource discoveryCts = new(TimeSpan.FromSeconds(3));
                        await discovery.StopAsync(discoveryCts.Token).ConfigureAwait(true);
                    }

                    MainViewModel? viewModel = _host.Services.GetService<MainViewModel>();
                    if (viewModel is not null)
                    {
                        await viewModel.StopDiscoveryAsync().ConfigureAwait(true);
                    }
                }
                catch (Exception ex)
                {
                    // discovery 停止失败不能阻止宿主停止。
                    Debug.WriteLine($"停止局域网发现失败: {ex}");
                }

                using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
                await _host.StopAsync(cts.Token).ConfigureAwait(true);
                _host.Dispose();
            }
        }
        catch (Exception ex)
        {
            // 退出路径上的异常不能再次抛出导致崩溃。此处无 UI，写入临时日志已无意义，静默释放。
            Debug.WriteLine($"停止宿主失败: {ex}");
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
            $"发生未预期的错误：{e.Exception.Message}",
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

        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        return builder;
    }
}
