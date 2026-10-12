using System.ComponentModel;
using System.IO;
using System.Net;
using System.Text.Json;
using LanRemote.App.Services;
using LanRemote.App.ViewModels;
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

public sealed class ProductHostConfigTests
{
    [Fact(Timeout = 40_000)]
    public async Task First_Run_ViewModel_Persists_Viewing_Before_Explicit_Enable_With_No_Network_Bindings()
    {
        await InIsolatedViewModelAsync(async (paths, viewModel, runtime, discoveryState, bindings) =>
        {
            Assert.False(File.Exists(paths.ConfigFilePath));
            await viewModel.LoadAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(viewModel.HostSettingsReady);
            Assert.False(viewModel.AllowViewing);
            Assert.True(viewModel.AllowDiscovery); // 首装只公告身份，空 capabilities 不授权查看。
            Assert.True(viewModel.RequireLocalApproval);
            Assert.False(runtime.IsListening);
            Assert.False(runtime.ApprovalsAvailable);
            Assert.False(discoveryState.Current.AllowViewing);
            Assert.True(discoveryState.Current.AllowDiscovery);
            Assert.Empty(discoveryState.Current.Capabilities);
            Assert.True(File.Exists(paths.SecretsFilePath)); // 身份首次加载会在隔离目录创建 DPAPI 秘密。
            Assert.False(File.Exists(paths.ConfigFilePath));
            Assert.Equal(0, bindings.RefreshCount);

            await ChangeAndWaitForSaveAsync(viewModel, () => viewModel.AllowViewing = true);
            Assert.False(runtime.IsListening); // 勾选与保存本身不代表授权监听。
            AppConfig persisted = await runtime.LoadVerifiedConfigAsync();
            Assert.True(persisted.AllowViewing);
            Assert.True(persisted.AllowDiscovery);
            Assert.True(persisted.RequireLocalApprovalForUnknownController);

            await viewModel.EnableViewOnlySharingAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("主机未监听：无合格 RFC1918 网卡地址；未修改网络设置。", viewModel.HostStatusText);
            Assert.False(runtime.IsListening);
            Assert.False(runtime.ApprovalsAvailable);
            Assert.False(discoveryState.Current.AllowViewing);
            Assert.Equal(1, bindings.RefreshCount);
        });
    }

    [Fact(Timeout = 40_000)]
    public async Task Historical_Viewing_True_Remains_Closed_After_Load_And_Unrelated_Save()
    {
        await InIsolatedViewModelAsync(async (paths, viewModel, runtime, discoveryState, bindings) =>
        {
            await new AppConfigStore(paths).SaveAsync(new AppConfig
            {
                AllowViewing = true,
                AllowDiscovery = false,
                RequireLocalApprovalForUnknownController = true,
            });

            await viewModel.LoadAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(viewModel.HostSettingsReady);
            Assert.True(viewModel.AllowViewing);
            Assert.False(runtime.IsListening);
            Assert.False(runtime.ApprovalsAvailable);
            Assert.False(discoveryState.Current.AllowViewing);

            viewModel.RequireLocalApproval = false; // UI 不允许关闭本机审批。
            Assert.True(viewModel.RequireLocalApproval);
            await ChangeAndWaitForSaveAsync(viewModel, () => viewModel.AutoStartOnLogin = true);
            AppConfig persisted = await runtime.LoadVerifiedConfigAsync();
            Assert.True(persisted.AllowViewing);
            Assert.True(persisted.RequireLocalApprovalForUnknownController);
            Assert.True(persisted.AutoStartOnLogin);
            Assert.False(runtime.IsListening);
            Assert.False(runtime.ApprovalsAvailable);
            Assert.False(discoveryState.Current.AllowViewing);
            Assert.Equal(0, bindings.RefreshCount);
        });
    }

    [Fact(Timeout = 40_000)]
    public async Task Disabled_Approval_Requires_Repair_And_Separate_Explicit_Enable()
    {
        await InIsolatedViewModelAsync(async (paths, viewModel, runtime, discoveryState, bindings) =>
        {
            await new AppConfigStore(paths).SaveAsync(new AppConfig
            {
                AllowDiscovery = false,
                AllowViewing = true,
                RequireLocalApprovalForUnknownController = false,
            });

            await viewModel.LoadAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(viewModel.HostSettingsReady);
            Assert.True(viewModel.NeedsLocalApprovalRepair);
            await viewModel.EnableViewOnlySharingAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(runtime.IsListening);
            Assert.False(runtime.ApprovalsAvailable);
            Assert.Equal(0, bindings.RefreshCount);

            await ChangeAndWaitForSaveAsync(viewModel, () => viewModel.RequireLocalApproval = true);
            Assert.False(viewModel.NeedsLocalApprovalRepair);
            Assert.True((await runtime.LoadVerifiedConfigAsync()).RequireLocalApprovalForUnknownController);
            Assert.False(runtime.IsListening); // 修复审批开关本身仍不能开启监听。
            Assert.False(discoveryState.Current.AllowViewing);

            await viewModel.EnableViewOnlySharingAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("主机未监听：无合格 RFC1918 网卡地址；未修改网络设置。", viewModel.HostStatusText);
            Assert.False(runtime.IsListening);
            Assert.False(runtime.ApprovalsAvailable);
            Assert.Equal(1, bindings.RefreshCount);
        });
    }

    [Fact(Timeout = 20_000)]
    public async Task Experimental_preview_is_explicit_and_does_not_turn_on_viewing_or_network()
    {
        await InIsolatedViewModelAsync(async (paths, viewModel, runtime, discoveryState, bindings) =>
        {
            await viewModel.LoadAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(viewModel.HostSettingsReady);
            Assert.False(viewModel.AllowViewing);
            Assert.False(runtime.IsListening);
            Assert.False(viewModel.UseClearerPreview);
            Assert.False(viewModel.UseSmootherPreview);
            Assert.False(viewModel.UseFastPreview);
            Assert.False(viewModel.UseThirtyFpsPreview);
            viewModel.UseClearerPreview = true;
            Assert.True(viewModel.UseClearerPreview);
            viewModel.UseSmootherPreview = true;
            Assert.True(viewModel.UseSmootherPreview);
            Assert.False(viewModel.UseClearerPreview);
            viewModel.UseFastPreview = true;
            Assert.True(viewModel.UseFastPreview);
            Assert.False(viewModel.UseClearerPreview);
            Assert.False(viewModel.UseSmootherPreview);
            viewModel.UseThirtyFpsPreview = true;
            Assert.True(viewModel.UseThirtyFpsPreview);
            Assert.False(viewModel.UseFastPreview);
            await viewModel.EnableViewOnlySharingAsync(useClearerPreview: viewModel.UseClearerPreview,
                useSmootherPreview: viewModel.UseSmootherPreview,
                useFastPreview: viewModel.UseFastPreview,
                useThirtyFpsPreview: viewModel.UseThirtyFpsPreview).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(runtime.IsListening);
            Assert.False(runtime.ApprovalsAvailable);
            Assert.Equal(0, bindings.RefreshCount);
            Assert.False(discoveryState.Current.AllowViewing);
            Assert.False(File.Exists(paths.ConfigFilePath));
            viewModel.UseClearerPreview = true;
            Assert.False(viewModel.UseSmootherPreview);
            Assert.False(viewModel.UseFastPreview);
            Assert.False(viewModel.UseThirtyFpsPreview);
            Assert.True(viewModel.UseClearerPreview);
            viewModel.UseClearerPreview = false;
            Assert.False(viewModel.UseClearerPreview);
            viewModel.UseFastPreview = true;
            viewModel.UseSmootherPreview = true;
            Assert.False(viewModel.UseFastPreview);
            Assert.True(viewModel.UseSmootherPreview);
            viewModel.UseSmootherPreview = false;
            Assert.False(File.Exists(paths.ConfigFilePath));
        });
    }

    [Theory(Timeout = 20_000)]
    [InlineData(false, false, false, false, 0.75, 60)]
    [InlineData(false, false, false, true, 0.50, 40)]
    [InlineData(true, false, false, false, 0.67, 85)]
    [InlineData(false, false, true, false, 1.00, 75)]
    public async Task Manual_quality_only_takes_effect_when_explicit_and_never_grants_viewing(
        bool clearer, bool smoother, bool fast, bool thirty, double scale, int quality)
    {
        await InIsolatedViewModelAsync(async (paths, viewModel, runtime, discoveryState, bindings) =>
        {
            await viewModel.LoadAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(viewModel.AllowViewing);
            Assert.False(viewModel.UseManualVideoQuality);
            viewModel.ManualPreviewScale = scale;
            viewModel.ManualJpegQuality = quality;
            Assert.False(viewModel.UseManualVideoQuality);
            viewModel.UseManualVideoQuality = true;
            await viewModel.EnableViewOnlySharingAsync(useClearerPreview: clearer,
                useSmootherPreview: smoother, useFastPreview: fast, useThirtyFpsPreview: thirty,
                useManualVideoQuality: viewModel.UseManualVideoQuality,
                manualScale: viewModel.ManualPreviewScale, manualJpegQuality: viewModel.ManualJpegQuality)
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(runtime.IsListening);
            Assert.False(runtime.ApprovalsAvailable);
            Assert.Equal(0, bindings.RefreshCount);
            Assert.False(discoveryState.Current.AllowViewing);
            Assert.False(File.Exists(paths.ConfigFilePath));
        });
    }

    [Theory(Timeout = 20_000)]
    [InlineData(0.55, 60)]
    [InlineData(double.NaN, 60)]
    [InlineData(double.PositiveInfinity, 60)]
    [InlineData(0.75, 39)]
    [InlineData(0.75, 86)]
    public async Task Invalid_manual_quality_is_rejected_before_identity_or_network(
        double scale, int quality)
    {
        await InIsolatedViewModelAsync(async (paths, viewModel, runtime, discoveryState, bindings) =>
        {
            await viewModel.LoadAsync().WaitAsync(TimeSpan.FromSeconds(10));
            // 首次加载本身创建DPAPI身份；拒绝手动画质必须保持其文件原字节不变。
            byte[] originalSecrets = await File.ReadAllBytesAsync(paths.SecretsFilePath);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                viewModel.EnableViewOnlySharingAsync(useManualVideoQuality: true,
                    manualScale: scale, manualJpegQuality: quality));
            Assert.False(runtime.IsListening);
            Assert.False(runtime.ApprovalsAvailable);
            Assert.Equal(0, bindings.RefreshCount);
            Assert.False(discoveryState.Current.AllowViewing);
            Assert.Equal(originalSecrets, await File.ReadAllBytesAsync(paths.SecretsFilePath));
            Assert.False(File.Exists(paths.ConfigFilePath));
        });
    }

    [Theory(Timeout = 20_000)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(true, true, true, true)]
    public async Task Preview_modes_cannot_both_be_requested_even_when_viewing_permission_is_off(
        bool clearer, bool smoother, bool fast, bool thirty)
    {
        await InIsolatedViewModelAsync(async (paths, viewModel, runtime, discoveryState, bindings) =>
        {
            await viewModel.LoadAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                viewModel.EnableViewOnlySharingAsync(useClearerPreview: clearer, useSmootherPreview: smoother,
                    useFastPreview: fast, useThirtyFpsPreview: thirty));
            Assert.False(runtime.IsListening);
            Assert.False(runtime.ApprovalsAvailable);
            Assert.Equal(0, bindings.RefreshCount);
            Assert.False(discoveryState.Current.AllowViewing);
            Assert.False(File.Exists(paths.ConfigFilePath));
        });
    }

    [Theory(Timeout = 20_000)]
    [InlineData(30, 0.55, 60, false)]
    [InlineData(30, 0.75, 60, true)]
    [InlineData(30, 0.75, 39, false)]
    [InlineData(20, 0.75, 86, false)]
    [InlineData(20, 0.75, 60, true)]
    [InlineData(10, double.NaN, 60, false)]
    [InlineData(15, double.PositiveInfinity, 85, false)]
    [InlineData(6, 0.75, 60, false)]
    public async Task Unsupported_preview_does_not_acquire_identity_or_touch_network(
        int fps, double scale, int quality, bool auto)
    {
        await InIsolatedDirectoryAsync(async (paths, runtime) =>
        {
            VideoQualitySettings unsupported = new(fps, scale, quality, auto);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                runtime.StartAsync(previewSettings: unsupported));
            Assert.False(runtime.IsListening);
            Assert.False(runtime.ApprovalsAvailable);
            Assert.False(File.Exists(paths.SecretsFilePath));
            Assert.False(File.Exists(paths.ConfigFilePath));
        });
    }

    [Fact(Timeout = 20_000)]
    public async Task Missing_Config_File_Rejects_Host_Without_Creating_Secrets()
    {
        await InIsolatedDirectoryAsync(async (paths, runtime) =>
        {
            Assert.False(File.Exists(paths.ConfigFilePath));
            await AssertRejectedBeforeListeningAsync<FileNotFoundException>(paths, runtime);
        });
    }

    [Theory(Timeout = 20_000)]
    [InlineData("", true)]
    [InlineData("{ invalid json", true)]
    [InlineData("null", false)]
    [InlineData("[]", false)]
    public async Task Empty_Malformed_Or_NonObject_Config_Rejects_Host(string json, bool syntaxError)
    {
        await InIsolatedDirectoryAsync(async (paths, runtime) =>
        {
            await File.WriteAllTextAsync(paths.ConfigFilePath, json);
            if (syntaxError)
            {
                await AssertJsonSyntaxRejectedBeforeListeningAsync(paths, runtime);
            }
            else
            {
                await AssertRejectedBeforeListeningAsync<InvalidDataException>(paths, runtime);
            }
        });
    }

    [Theory(Timeout = 20_000)]
    [InlineData("{}")]
    [InlineData("{\"requireLocalApprovalForUnknownController\":true}")]
    [InlineData("{\"allowViewing\":true}")]
    public async Task Missing_Explicit_Safety_Permissions_Rejects_Host(string json)
    {
        await InIsolatedDirectoryAsync(async (paths, runtime) =>
        {
            await File.WriteAllTextAsync(paths.ConfigFilePath, json);
            await AssertRejectedBeforeListeningAsync<InvalidDataException>(paths, runtime);
        });
    }

    [Theory(Timeout = 20_000)]
    [InlineData("{\"allowViewing\":false,\"requireLocalApprovalForUnknownController\":true}")]
    [InlineData("{\"allowDiscovery\":\"true\",\"allowViewing\":false,\"requireLocalApprovalForUnknownController\":true}")]
    [InlineData("{\"allowDiscovery\":null,\"allowViewing\":false,\"requireLocalApprovalForUnknownController\":true}")]
    [InlineData("{\"allowDiscovery\":true,\"ALLOWDISCOVERY\":true,\"allowViewing\":false,\"requireLocalApprovalForUnknownController\":true}")]
    [InlineData("{\"allowDiscovery\":false,\"ALLOWDISCOVERY\":true,\"allowViewing\":false,\"requireLocalApprovalForUnknownController\":true}")]
    public async Task Missing_Invalid_Or_Duplicate_Discovery_Field_Rejects_Host(string json)
    {
        await InIsolatedDirectoryAsync(async (paths, runtime) =>
        {
            await File.WriteAllTextAsync(paths.ConfigFilePath, json);
            await AssertInvalidConfigRejectedBeforeListeningAsync(paths, runtime);
        });
    }

    [Theory(Timeout = 20_000)]
    [InlineData("{\"allowViewing\":\"true\",\"requireLocalApprovalForUnknownController\":true}")]
    [InlineData("{\"allowViewing\":null,\"requireLocalApprovalForUnknownController\":true}")]
    [InlineData("{\"allowViewing\":true,\"requireLocalApprovalForUnknownController\":\"true\"}")]
    [InlineData("{\"allowViewing\":true,\"requireLocalApprovalForUnknownController\":null}")]
    public async Task NonBoolean_Safety_Permissions_Reject_Host(string json)
    {
        await InIsolatedDirectoryAsync(async (paths, runtime) =>
        {
            await File.WriteAllTextAsync(paths.ConfigFilePath, json);
            await AssertInvalidConfigRejectedBeforeListeningAsync(paths, runtime);
        });
    }

    [Theory(Timeout = 20_000)]
    [InlineData("{\"allowViewing\":true,\"allowViewing\":true,\"requireLocalApprovalForUnknownController\":true}")]
    [InlineData("{\"allowViewing\":false,\"allowViewing\":true,\"requireLocalApprovalForUnknownController\":true}")]
    [InlineData("{\"allowViewing\":true,\"ALLOWVIEWING\":true,\"requireLocalApprovalForUnknownController\":true}")]
    [InlineData("{\"allowViewing\":true,\"requireLocalApprovalForUnknownController\":true,\"requireLocalApprovalForUnknownController\":true}")]
    [InlineData("{\"allowViewing\":true,\"requireLocalApprovalForUnknownController\":false,\"REQUIRELOCALAPPROVALFORUNKNOWNCONTROLLER\":true}")]
    public async Task Duplicate_Safety_Fields_Reject_Even_When_Last_Value_Is_True(string json)
    {
        await InIsolatedDirectoryAsync(async (paths, runtime) =>
        {
            await File.WriteAllTextAsync(paths.ConfigFilePath, json);
            await AssertInvalidConfigRejectedBeforeListeningAsync(paths, runtime);
        });
    }

    [Fact(Timeout = 20_000)]
    public async Task Persisted_Viewing_True_Does_Not_Authorize_New_Runtime_Without_Current_Run_OptIn()
    {
        await InIsolatedDirectoryAsync(async (paths, runtime) =>
        {
            await new AppConfigStore(paths).SaveAsync(new AppConfig
            {
                AllowViewing = true,
                RequireLocalApprovalForUnknownController = true,
                TransportPort = TransportConstants.Port,
            });

            AppConfig config = await runtime.LoadVerifiedConfigAsync();
            Assert.True(config.AllowViewing);
            Assert.True(config.RequireLocalApprovalForUnknownController);
            // 故意直接请求启动：磁盘上的 true 不得替代本轮用户显式开启监听。
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartAsync());
            Assert.False(runtime.IsListening);
            Assert.False(File.Exists(paths.SecretsFilePath));
        });
    }

    [Theory(Timeout = 20_000)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Explicit_False_Is_Preserved_But_Cannot_Enable_Host(bool allowViewing, bool requireApproval)
    {
        await InIsolatedDirectoryAsync(async (paths, runtime) =>
        {
            await new AppConfigStore(paths).SaveAsync(new AppConfig
            {
                AllowViewing = allowViewing,
                RequireLocalApprovalForUnknownController = requireApproval,
                AllowControl = false,
            });

            AppConfig parsed = await runtime.LoadVerifiedConfigAsync();
            Assert.Equal(allowViewing, parsed.AllowViewing);
            Assert.Equal(requireApproval, parsed.RequireLocalApprovalForUnknownController);
            Assert.True(runtime.OpenApprovals());
            runtime.AllowListening();
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartAsync());
            Assert.False(runtime.IsListening);
            Assert.False(File.Exists(paths.SecretsFilePath));
        });
    }

    [Fact(Timeout = 20_000)]
    public async Task Saved_Private_ViewOnly_Config_Is_Verified_Without_Reading_Secrets()
    {
        await InIsolatedDirectoryAsync(async (paths, runtime) =>
        {
            AppConfig expected = new()
            {
                AllowDiscovery = false,
                AllowViewing = true,
                AllowControl = false,
                RequireLocalApprovalForUnknownController = true,
                TransportPort = TransportConstants.Port,
                MaxViewSessions = 2,
            };
            await new AppConfigStore(paths).SaveAsync(expected);

            Assert.True(File.Exists(paths.ConfigFilePath));
            Assert.Equal(expected, await runtime.LoadVerifiedConfigAsync());
            Assert.False(runtime.IsListening);
            Assert.False(File.Exists(paths.SecretsFilePath));
        });
    }

    [Theory(Timeout = 20_000)]
    [InlineData(45872)]
    [InlineData(45874)]
    public async Task NonProtocol_Port_Is_Rejected_Before_Identity_Secrets_Or_Network(int port)
    {
        await InIsolatedDirectoryAsync(async (paths, runtime) =>
        {
            await new AppConfigStore(paths).SaveAsync(new AppConfig
            {
                AllowViewing = true,
                RequireLocalApprovalForUnknownController = true,
                TransportPort = port,
            });

            Assert.True(runtime.OpenApprovals());
            runtime.AllowListening();
            await Assert.ThrowsAsync<InvalidDataException>(() => runtime.StartAsync());
            Assert.False(runtime.IsListening);
            Assert.False(File.Exists(paths.SecretsFilePath));
        });
    }

    [Fact(Timeout = 20_000)]
    public async Task Approval_Gate_Denies_Control_And_Reports_Unavailable_Without_Dispatcher()
    {
        await using StaDispatcherFixture sta = new();
        var dispatcher = await sta.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        WpfLocalApprovalGate gate = new(dispatcher, NullLogger<WpfLocalApprovalGate>.Instance);
        Assert.True(gate.OpenApprovals());

        LocalApprovalRequest control = ApprovalRequest(SessionPermission.Control);
        ValueTask<LocalApprovalDecision> deniedTask = gate.RequestApprovalAsync(control, CancellationToken.None);
        Assert.True(deniedTask.IsCompletedSuccessfully);
        LocalApprovalDecision denied = await deniedTask;
        Assert.Equal(control.RequestId, denied.RequestId);
        Assert.Equal(LocalApprovalOutcome.Denied, denied.Outcome);
        Assert.Null(denied.GrantedPermission);

        await sta.DisposeAsync();
        Assert.False(gate.AreApprovalsOpen);
        LocalApprovalRequest viewOnly = ApprovalRequest(SessionPermission.ViewOnly);
        ValueTask<LocalApprovalDecision> unavailableTask = gate.RequestApprovalAsync(viewOnly, CancellationToken.None);
        Assert.True(unavailableTask.IsCompletedSuccessfully);
        LocalApprovalDecision unavailable = await unavailableTask;
        Assert.Equal(viewOnly.RequestId, unavailable.RequestId);
        Assert.Equal(LocalApprovalOutcome.Unavailable, unavailable.Outcome);
        Assert.Null(unavailable.GrantedPermission);
    }

    private static async Task ChangeAndWaitForSaveAsync(MainViewModel viewModel, Action change)
    {
        TaskCompletionSource saved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.StatusText) &&
                viewModel.StatusText == "配置已保存。")
            {
                saved.TrySetResult();
            }
        };
        viewModel.PropertyChanged += handler;
        try
        {
            change();
            await saved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            viewModel.PropertyChanged -= handler;
        }
    }

    private static LocalApprovalRequest ApprovalRequest(SessionPermission permission) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), IPAddress.Loopback, 43210,
        Guid.NewGuid(), "本机审批测试", permission, "A1B2C3", DateTimeOffset.UtcNow.AddMinutes(1));

    private static async Task AssertRejectedBeforeListeningAsync<TException>(AppPaths paths, HostRuntimeService runtime)
        where TException : Exception
    {
        await Assert.ThrowsAsync<TException>(() => runtime.LoadVerifiedConfigAsync());
        Assert.False(File.Exists(paths.SecretsFilePath));
        Assert.True(runtime.OpenApprovals());
        runtime.AllowListening();
        await Assert.ThrowsAsync<TException>(() => runtime.StartAsync());
        Assert.False(runtime.IsListening);
        Assert.False(File.Exists(paths.SecretsFilePath));
    }

    private static async Task AssertJsonSyntaxRejectedBeforeListeningAsync(AppPaths paths, HostRuntimeService runtime)
    {
        await Assert.ThrowsAnyAsync<JsonException>(() => runtime.LoadVerifiedConfigAsync());
        Assert.False(File.Exists(paths.SecretsFilePath));
        Assert.True(runtime.OpenApprovals());
        runtime.AllowListening();
        await Assert.ThrowsAnyAsync<JsonException>(() => runtime.StartAsync());
        Assert.False(runtime.IsListening);
        Assert.False(File.Exists(paths.SecretsFilePath));
    }

    private static async Task AssertInvalidConfigRejectedBeforeListeningAsync(AppPaths paths, HostRuntimeService runtime)
    {
        await AssertInvalidConfigErrorAsync(() => runtime.LoadVerifiedConfigAsync());
        Assert.False(File.Exists(paths.SecretsFilePath));
        Assert.True(runtime.OpenApprovals());
        runtime.AllowListening();
        await AssertInvalidConfigErrorAsync(() => runtime.StartAsync());
        Assert.False(runtime.IsListening);
        Assert.False(File.Exists(paths.SecretsFilePath));
    }

    private static async Task AssertInvalidConfigErrorAsync(Func<Task> action)
    {
        Exception? error = await Record.ExceptionAsync(action);
        Assert.True(error is InvalidDataException or JsonException,
            $"严格解析须以 JSON/配置错误拒绝，实际为：{error?.GetType().Name ?? "无异常"}");
    }

    private static async Task InIsolatedViewModelAsync(
        Func<AppPaths, MainViewModel, HostRuntimeService, DiscoveryRuntimeState, EmptyBindings, Task> test)
    {
        string root = Path.Combine(Path.GetTempPath(), $"LanRemote-product-vm-{Guid.NewGuid():N}");
        AppPaths paths = AppPaths.FromRootDirectory(root);
        try
        {
            Directory.CreateDirectory(root);
            await using StaDispatcherFixture sta = new();
            var dispatcher = await sta.Ready.WaitAsync(TimeSpan.FromSeconds(10));
            WpfLocalApprovalGate gate = new(dispatcher, NullLogger<WpfLocalApprovalGate>.Instance);
            DpapiSecretVault vault = new(paths);
            using DeviceCertificateService certificates = new(vault);
            DeviceIdentityService identity = new(vault, certificates);
            DpapiAccessSecretStore accessSecrets = new(vault);
            EmptyBindings bindings = new();
            DiscoveryRuntimeState discoveryState = new();
            LanDiscoveryService discovery = new(bindings, discoveryState, NullLogger<LanDiscoveryService>.Instance);
            HostRuntimeService runtime = new(paths, identity, certificates,
                accessSecrets, bindings, new SubnetPolicy(bindings),
                gate, NullLogger<HostRuntimeService>.Instance);
            MainViewModel viewModel = new(paths, new AppConfigStore(paths), identity,
                accessSecrets, discovery, discoveryState, runtime, NullLogger<MainViewModel>.Instance);
            try
            {
                await test(paths, viewModel, runtime, discoveryState, bindings);
            }
            finally
            {
                viewModel.BeginExit();
                await Task.WhenAll(viewModel.StopHostForExitAsync(), viewModel.StopDiscoveryAsync(),
                    discovery.JoinAndDisposeAsync());
                gate.BlockApprovals();
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class EmptyBindings : INetworkBindingProvider
    {
        private int _refreshCount;
        public int RefreshCount => Volatile.Read(ref _refreshCount);
        public IReadOnlyList<NetworkBinding> GetBindings() => Array.Empty<NetworkBinding>();
        public void Refresh() => Interlocked.Increment(ref _refreshCount);
    }

    private static async Task InIsolatedDirectoryAsync(Func<AppPaths, HostRuntimeService, Task> test)
    {
        string root = Path.Combine(Path.GetTempPath(), $"LanRemote-product-host-{Guid.NewGuid():N}");
        AppPaths paths = AppPaths.FromRootDirectory(root);
        try
        {
            Directory.CreateDirectory(root);
            await using StaDispatcherFixture sta = new();
            var dispatcher = await sta.Ready.WaitAsync(TimeSpan.FromSeconds(10));
            WpfLocalApprovalGate gate = new(dispatcher, NullLogger<WpfLocalApprovalGate>.Instance);
            DpapiSecretVault vault = new(paths);
            using DeviceCertificateService certificates = new(vault);
            DeviceIdentityService identity = new(vault, certificates);
            PassiveBindings bindings = new();
            HostRuntimeService runtime = new(paths, identity, certificates,
                new DpapiAccessSecretStore(vault), bindings, new SubnetPolicy(bindings),
                gate, NullLogger<HostRuntimeService>.Instance);
            try
            {
                await test(paths, runtime);
            }
            finally
            {
                gate.BlockApprovals();
                await runtime.StopAndDisposeAsync();
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class PassiveBindings : INetworkBindingProvider
    {
        public IReadOnlyList<NetworkBinding> GetBindings() =>
            throw new InvalidOperationException("拒绝监听的测试不得枚举网卡。");

        public void Refresh() => throw new InvalidOperationException("拒绝监听的测试不得刷新网卡。");
    }
}
