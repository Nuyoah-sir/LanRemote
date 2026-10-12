using System.IO;
using System.Runtime.CompilerServices;
using LanRemote.App.Services;
using LanRemote.App.ViewModels;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Configuration;
using LanRemote.Core.Infrastructure;
using LanRemote.Core.Models;
using LanRemote.Discovery;
using LanRemote.Discovery.Networking;
using LanRemote.Security.Certificates;
using LanRemote.Security.Identity;
using LanRemote.Security.Secrets;
using LanRemote.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace LanRemote.Acceptance.Tests;

public sealed class ProductFirstInstallDiscoveryTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(20);

    [Fact(Timeout = 40_000)]
    public async Task Missing_Config_AutoDiscovers_Without_Opening_View_Host()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Assert.False(File.Exists(fixture.Paths.ConfigFilePath));
        Assert.False(File.Exists(fixture.Paths.SecretsFilePath));
        FileNotFoundException missing = await Assert.ThrowsAsync<FileNotFoundException>(
            () => fixture.Host.LoadVerifiedConfigAsync());
        Assert.Equal(Path.GetFullPath(fixture.Paths.ConfigFilePath), Path.GetFullPath(missing.FileName!));

        await fixture.ViewModel.LoadAsync().WaitAsync(Guard);

        DiscoveryRuntimeSnapshot snapshot = fixture.RuntimeState.Current;
        Assert.True(snapshot.AllowDiscovery);
        Assert.True(snapshot.CanAnnounce);
        Assert.True(snapshot.CanReplyToProbe);
        Assert.False(snapshot.AllowViewing);
        Assert.False(snapshot.AllowControl);
        Assert.Empty(snapshot.Capabilities);
        Assert.True(fixture.ViewModel.AllowDiscovery);
        Assert.False(fixture.ViewModel.AllowViewing);
        Assert.True(fixture.ViewModel.RequireLocalApproval);
        Assert.True(fixture.ViewModel.HostSettingsReady);
        Assert.False(fixture.Host.IsListening);
        Assert.False(fixture.Gate.AreApprovalsOpen);
        Assert.Equal(snapshot, fixture.Discovery.ObservedAtStart);
        Assert.Equal(1, fixture.Discovery.StartCount);
        Assert.Equal(0, fixture.Bindings.RefreshCount);
        Assert.False(File.Exists(fixture.Paths.ConfigFilePath));
        // 真实 GetOrCreateAsync 会把身份写到隔离目录，绝不触碰用户默认数据目录。
        Assert.True(File.Exists(fixture.Paths.SecretsFilePath));
    }

    [Theory(Timeout = 40_000)]
    [InlineData("{ invalid json")]
    [InlineData("{}")]
    [InlineData("{\"allowViewing\":false}")]
    [InlineData("{\"allowViewing\":false,\"requireLocalApprovalForUnknownController\":true}")]
    [InlineData("{\"allowDiscovery\":\"true\",\"allowViewing\":false,\"requireLocalApprovalForUnknownController\":true}")]
    [InlineData("{\"allowDiscovery\":true,\"ALLOWDISCOVERY\":true,\"allowViewing\":false,\"requireLocalApprovalForUnknownController\":true}")]
    [InlineData("{\"allowDiscovery\":false,\"ALLOWDISCOVERY\":true,\"allowViewing\":false,\"requireLocalApprovalForUnknownController\":true}")]
    public async Task Existing_Corrupt_Or_Legacy_Config_Does_Not_Advertise(string json)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(fixture.Paths.ConfigFilePath, json);

        await fixture.ViewModel.LoadAsync().WaitAsync(Guard);

        DiscoveryRuntimeSnapshot snapshot = fixture.RuntimeState.Current;
        Assert.False(snapshot.AllowDiscovery);
        Assert.False(snapshot.CanAnnounce);
        Assert.False(snapshot.CanReplyToProbe);
        Assert.False(snapshot.AllowViewing);
        Assert.False(snapshot.AllowControl);
        Assert.Empty(snapshot.Capabilities);
        Assert.False(fixture.ViewModel.AllowDiscovery);
        Assert.False(fixture.ViewModel.AllowViewing);
        Assert.False(fixture.Host.IsListening);
        Assert.Equal(snapshot, fixture.Discovery.ObservedAtStart);
        Assert.Equal(1, fixture.Discovery.StartCount);
        Assert.Equal(0, fixture.Bindings.RefreshCount);
        Assert.Equal(json, await File.ReadAllTextAsync(fixture.Paths.ConfigFilePath));
    }

    [Fact(Timeout = 40_000)]
    public async Task Explicitly_Disabled_Discovery_Is_Not_Overridden_On_Load()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.ConfigStore.SaveAsync(new AppConfig
        {
            AllowDiscovery = false,
            AllowViewing = false,
            AllowControl = false,
            RequireLocalApprovalForUnknownController = true,
        });

        await fixture.ViewModel.LoadAsync().WaitAsync(Guard);

        DiscoveryRuntimeSnapshot snapshot = fixture.RuntimeState.Current;
        Assert.False(snapshot.AllowDiscovery);
        Assert.False(snapshot.CanAnnounce);
        Assert.False(snapshot.CanReplyToProbe);
        Assert.False(snapshot.AllowViewing);
        Assert.False(snapshot.AllowControl);
        Assert.Empty(snapshot.Capabilities);
        Assert.False(fixture.ViewModel.AllowDiscovery);
        Assert.False(fixture.ViewModel.AllowViewing);
        Assert.False(fixture.Host.IsListening);
        Assert.Equal(snapshot, fixture.Discovery.ObservedAtStart);
        Assert.Equal(1, fixture.Discovery.StartCount);
        Assert.Equal(0, fixture.Bindings.RefreshCount);
        Assert.False((await fixture.Host.LoadVerifiedConfigAsync()).AllowDiscovery);
    }

    [Fact(Timeout = 40_000)]
    public async Task First_Run_Default_Discovery_And_Explicit_View_OptIn_Save_Strict_Booleans_Without_Binding()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.ViewModel.LoadAsync().WaitAsync(Guard);
        Assert.False(fixture.Host.IsListening);
        Assert.False(File.Exists(fixture.Paths.ConfigFilePath));
        // 产品默认自动发现，但首次安装始终不允许查看或控制。
        Assert.True(fixture.ViewModel.AllowDiscovery);
        Assert.True(fixture.RuntimeState.Current.CanAnnounce);
        Assert.True(fixture.RuntimeState.Current.CanReplyToProbe);
        Assert.Empty(fixture.RuntimeState.Current.Capabilities);
        Assert.False(fixture.ViewModel.AllowViewing);
        Assert.False(fixture.Host.IsListening);

        fixture.ViewModel.AllowViewing = true;
        Assert.True(fixture.ViewModel.AllowViewing);
        Assert.False(fixture.Host.IsListening);
        Assert.Empty(fixture.RuntimeState.Current.Capabilities);

        // 查看设置会异步保存：先等待安全字段落盘，再明确点击开启仅查看共享。
        using (CancellationTokenSource saveTimeout = new(Guard))
        {
            while (true)
            {
                try
                {
                    AppConfig persisted = await fixture.Host.LoadVerifiedConfigAsync(saveTimeout.Token);
                    if (persisted.AllowDiscovery && persisted.AllowViewing) break;
                }
                catch (IOException) when (!saveTimeout.IsCancellationRequested)
                {
                    // 配置可能尚未创建，或正由 AppConfigStore 独占写入。
                }
                await Task.Delay(10, saveTimeout.Token);
            }
        }
        await fixture.ViewModel.EnableViewOnlySharingAsync().WaitAsync(Guard);

        AppConfig saved = await fixture.Host.LoadVerifiedConfigAsync().WaitAsync(Guard);
        Assert.True(saved.AllowDiscovery);
        Assert.True(saved.AllowViewing);
        Assert.False(saved.AllowControl);
        Assert.True(saved.RequireLocalApprovalForUnknownController);
        Assert.True(fixture.ViewModel.AllowDiscovery);
        Assert.True(fixture.ViewModel.AllowViewing);
        Assert.True(fixture.RuntimeState.Current.CanAnnounce);
        Assert.True(fixture.RuntimeState.Current.CanReplyToProbe);
        Assert.False(fixture.Host.IsListening);
        Assert.False(fixture.Gate.AreApprovalsOpen);
        Assert.False(fixture.RuntimeState.Current.AllowViewing);
        Assert.False(fixture.RuntimeState.Current.AllowControl);
        Assert.Empty(fixture.RuntimeState.Current.Capabilities);
        Assert.Equal(1, fixture.Bindings.RefreshCount);
        Assert.Equal(1, fixture.Bindings.GetBindingsCount);
        Assert.Equal(1, fixture.Discovery.StartCount);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly StaDispatcherFixture _sta;
        private readonly DeviceCertificateService _certificates;

        private Fixture(string root, AppPaths paths, StaDispatcherFixture sta,
            DeviceCertificateService certificates, WpfLocalApprovalGate gate,
            AppConfigStore configStore, HostRuntimeService host,
            DiscoveryRuntimeState runtimeState, NoSocketDiscovery discovery,
            EmptyBindings bindings, MainViewModel viewModel)
        {
            _root = root;
            _sta = sta;
            _certificates = certificates;
            Paths = paths;
            Gate = gate;
            ConfigStore = configStore;
            Host = host;
            RuntimeState = runtimeState;
            Discovery = discovery;
            Bindings = bindings;
            ViewModel = viewModel;
        }

        public AppPaths Paths { get; }
        public WpfLocalApprovalGate Gate { get; }
        public AppConfigStore ConfigStore { get; }
        public HostRuntimeService Host { get; }
        public DiscoveryRuntimeState RuntimeState { get; }
        public NoSocketDiscovery Discovery { get; }
        public EmptyBindings Bindings { get; }
        public MainViewModel ViewModel { get; }

        public static async Task<Fixture> CreateAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), $"LanRemote-first-discovery-{Guid.NewGuid():N}");
            AppPaths paths = AppPaths.FromRootDirectory(root);
            paths.EnsureCreated();
            StaDispatcherFixture sta = new();
            DeviceCertificateService? certificates = null;
            try
            {
                var dispatcher = await sta.Ready.WaitAsync(Guard);
                WpfLocalApprovalGate gate = new(dispatcher, NullLogger<WpfLocalApprovalGate>.Instance);
                AppConfigStore configStore = new(paths);
                DpapiSecretVault vault = new(paths);
                certificates = new DeviceCertificateService(vault);
                DeviceIdentityService identity = new(vault, certificates);
                DpapiAccessSecretStore secretStore = new(vault);
                EmptyBindings bindings = new();
                HostRuntimeService host = new(paths, identity, certificates, secretStore,
                    bindings, new SubnetPolicy(bindings), gate, NullLogger<HostRuntimeService>.Instance);
                DiscoveryRuntimeState runtimeState = new();
                NoSocketDiscovery discovery = new(runtimeState);
                MainViewModel viewModel = new(paths, configStore, identity, secretStore,
                    discovery, runtimeState, host, NullLogger<MainViewModel>.Instance);
                return new Fixture(root, paths, sta, certificates, gate, configStore, host,
                    runtimeState, discovery, bindings, viewModel);
            }
            catch
            {
                certificates?.Dispose();
                await sta.DisposeAsync();
                Directory.Delete(root, recursive: true);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                ViewModel.BeginExit();
                await ViewModel.StopHostForExitAsync().WaitAsync(Guard);
                await ViewModel.StopDiscoveryAsync().WaitAsync(Guard);
                await Discovery.StopAsync(CancellationToken.None);
            }
            finally
            {
                _certificates.Dispose();
                await _sta.DisposeAsync();
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class NoSocketDiscovery(DiscoveryRuntimeState state) : IDiscoveryService
    {
        public int StartCount { get; private set; }
        public DiscoveryRuntimeSnapshot? ObservedAtStart { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            ObservedAtStart = state.Current;
            StartCount++;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<DiscoveredDevice> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }

        public Task ProbeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class EmptyBindings : INetworkBindingProvider
    {
        public int RefreshCount => Volatile.Read(ref _refreshCount);
        public int GetBindingsCount => Volatile.Read(ref _getBindingsCount);

        public void Refresh() => Interlocked.Increment(ref _refreshCount);

        public IReadOnlyList<NetworkBinding> GetBindings()
        {
            Interlocked.Increment(ref _getBindingsCount);
            return Array.Empty<NetworkBinding>();
        }

        private int _refreshCount;
        private int _getBindingsCount;
    }
}
