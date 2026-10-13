using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using LanRemote.Core.Abstractions;
using ProductMainWindow = LanRemote.App.MainWindow;
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

[Collection(AcceptanceStageFiveCollection.Name)]
public sealed class ProductHostConfigTests
{
    [Theory(Timeout = 60_000)]
    [InlineData(1, "control-upgrade-required", "已拒绝旧版协议声明（未经身份认证）。请将两端升级到兼容版本；不会回退到旧协议。")]
    [InlineData(99, "control-protocol-unsupported", "已拒绝不支持的协议版本声明（未经身份认证）。请将两端升级到兼容版本；不会回退到旧协议。")]
    public async Task Protocol_Rejection_Refreshes_Real_Product_Window_Without_Changing_Safety_Status(
        int protocol, string rejection, string expected)
    {
        await InProtocolWindowAsync(async (window, runtime, certificatePin) =>
        {
            MainViewModel viewModel = window.ViewModel;
            Assert.Null(ReadPrivate<DispatcherTimer?>(window, "_hostProtocolTimer"));
            InvokePrivate(window, "MainWindow_OnContentRendered", window, EventArgs.Empty);
            DispatcherTimer timer = ReadPrivate<DispatcherTimer>(window, "_hostProtocolTimer");
            Task load = ReadPrivate<Task>(window, "_loadTask");
            InvokePrivate(window, "MainWindow_OnContentRendered", window, EventArgs.Empty);
            Assert.Same(timer, ReadPrivate<DispatcherTimer>(window, "_hostProtocolTimer"));
            Assert.Same(load, ReadPrivate<Task>(window, "_loadTask"));
            Assert.True(timer.IsEnabled);
            Assert.Equal(TimeSpan.FromSeconds(1), timer.Interval);
            Assert.True(TimerReferences(timer, window));
            await load.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(runtime.IsListening); // 历史配置不能代替本轮显式开启。
            Assert.Null(runtime.LastProtocolRejection);
            Assert.Equal(string.Empty, viewModel.HostProtocolStatusText);

            await viewModel.EnableViewOnlySharingAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(runtime.IsListening);
            Assert.True(runtime.ApprovalsAvailable);
            Assert.True(viewModel.RequireLocalApproval);
            Assert.False(viewModel.AllowControl);
            TransportHost firstHost = ReadPrivate<TransportHost>(runtime, "_host");
            string safetyStatus = viewModel.HostStatusText;
            await SendDeclarationAndObserveCloseAsync(firstHost, certificatePin, null);
            await SendDeclarationAndObserveCloseAsync(firstHost, certificatePin,
                "{\"type\":\"channel_hello\",\"channel\":\"control\",\"protocol\":1,\"remoteText\":\"PRIVATE-REMOTE-TEXT\"}");
            viewModel.RefreshHostProtocolStatus();
            Assert.Null(runtime.LastProtocolRejection);
            Assert.Equal(string.Empty, viewModel.HostProtocolStatusText);

            await SendDeclarationAndObserveCloseAsync(firstHost, certificatePin,
                $"{{\"type\":\"channel_hello\",\"channel\":\"control\",\"protocol\":{protocol}}}");
            Assert.Equal(rejection, runtime.LastProtocolRejection);
            // 不手动调用刷新：真实 DispatcherTimer 必须把 Host 的只读状态送到产品 VM。
            using (CancellationTokenSource budget = new(TimeSpan.FromSeconds(5)))
            {
                while (viewModel.HostProtocolStatusText != expected) await Task.Delay(20, budget.Token);
            }
            Assert.Equal(safetyStatus, viewModel.HostStatusText);
            TextBlock text = Assert.IsType<TextBlock>(window.FindName("HostProtocolStatus"));
            BindingExpression binding = Assert.IsType<BindingExpression>(text.GetBindingExpression(TextBlock.TextProperty));
            Assert.Equal(nameof(MainViewModel.HostProtocolStatusText), binding.ParentBinding.Path.Path);
            binding.UpdateTarget();
            Assert.Equal(expected, text.Text);
            Assert.DoesNotContain("PRIVATE-REMOTE-TEXT", text.Text);
            Assert.Empty(ReadPrivate<System.Collections.IEnumerable>(
                ReadPrivate<WpfLocalApprovalGate>(runtime, "_approvalGate"), "_owned"));

            Task stop = viewModel.StopViewOnlySharingAsync();
            Assert.Null(runtime.LastProtocolRejection);
            Assert.Equal(string.Empty, viewModel.HostProtocolStatusText);
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
            string stopped = viewModel.HostStatusText;
            viewModel.RefreshHostProtocolStatus();
            Assert.Equal(stopped, viewModel.HostStatusText);
            Assert.True(runtime.IsFullyDrained);
            Assert.Equal(rejection, firstHost.LastProtocolRejection); // 旧 Host 仍保留槽，但产品不能再读取它。

            await viewModel.EnableViewOnlySharingAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(runtime.IsListening);
            TransportHost nextHost = ReadPrivate<TransportHost>(runtime, "_host");
            Assert.NotSame(firstHost, nextHost);
            viewModel.RefreshHostProtocolStatus();
            Assert.Null(runtime.LastProtocolRejection);
            Assert.Equal(string.Empty, viewModel.HostProtocolStatusText);
            await SendDeclarationAndObserveCloseAsync(nextHost, certificatePin,
                "{\"type\":\"channel_hello\",\"channel\":\"control\",\"protocol\":1,\"protocol\":1}");
            viewModel.RefreshHostProtocolStatus();
            Assert.Null(runtime.LastProtocolRejection);
            Assert.Equal(string.Empty, viewModel.HostProtocolStatusText);
        });
    }

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

    private static async Task InProtocolWindowAsync(
        Func<ProductMainWindow, HostRuntimeService, string, Task> test)
    {
        using AcceptanceTestDirectory directory = new();
        await using StaDispatcherFixture sta = new();
        Dispatcher dispatcher = await sta.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        await dispatcher.InvokeAsync(async () =>
        {
            AppPaths paths = AppPaths.FromRootDirectory(directory.Path);
            AppConfigStore config = new(paths);
            await config.SaveAsync(new AppConfig
            {
                AllowDiscovery = false, AllowViewing = true, AllowControl = false,
                RequireLocalApprovalForUnknownController = true,
            });
            DpapiSecretVault vault = new(paths);
            using DeviceCertificateService certificates = new(vault);
            DeviceIdentityService identity = new(vault, certificates);
            DpapiAccessSecretStore secrets = new(vault);
            WpfLocalApprovalGate gate = new(dispatcher, NullLogger<WpfLocalApprovalGate>.Instance);
            ProtocolLoopbackBindings bindings = new();
            HostRuntimeService runtime = new(paths, identity, certificates, secrets, bindings,
                new ProtocolLoopbackPolicy(), gate, NullLogger<HostRuntimeService>.Instance);
            NoSocketDiscovery discovery = new();
            MainViewModel viewModel = new(paths, config, identity, secrets, discovery,
                new DiscoveryRuntimeState(), runtime, NullLogger<MainViewModel>.Instance);
            ClientConnectService client = new(identity, new ControlClientConnector());
            ProductMainWindow window = new(viewModel, client, NullLogger<ProductMainWindow>.Instance);
            try
            {
                string pin = (await certificates.GetOrCreateAsync()).Sha256FingerprintHex;
                await test(window, runtime, pin);
            }
            finally
            {
                DispatcherTimer? timer = ReadPrivate<DispatcherTimer?>(window, "_hostProtocolTimer");
                viewModel.BeginExit();
                Task loadingStop = window.StopLoadingAsync();
                Task ownersStop = Task.WhenAll(viewModel.StopHostForExitAsync(), viewModel.StopDiscoveryAsync(),
                    client.StopAndDisposeAsync());
                // 首次加载故障也必须等待其他真实所有者停止；WhenAll 保留每个原任务的失败。
                await Task.WhenAll(loadingStop, ownersStop).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(runtime.IsFullyDrained);
                Assert.Null(runtime.LastProtocolRejection);
                Assert.Equal(string.Empty, viewModel.HostProtocolStatusText);
                // 测试没有 App 单例；先完整 join 真实所有者，再移除仅负责调用 App 的 Closing 协调回调。
                MethodInfo closing = typeof(ProductMainWindow).GetMethod("MainWindow_OnClosing",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                window.Closing -= (CancelEventHandler)closing.CreateDelegate(typeof(CancelEventHandler), window);
                bool closed = false;
                window.Closed += (_, _) => closed = true;
                window.Close();
                Assert.True(closed);
                Assert.Null(ReadPrivate<DispatcherTimer?>(window, "_hostProtocolTimer"));
                if (timer is not null)
                {
                    Assert.False(timer.IsEnabled);
                    Assert.False(TimerReferences(timer, window));
                }
                InvokePrivate(window, "MainWindow_OnContentRendered", window, EventArgs.Empty);
                Assert.Null(ReadPrivate<DispatcherTimer?>(window, "_hostProtocolTimer"));
            }
        }).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(50));
    }

    private static T ReadPrivate<T>(object instance, string name) => (T)instance.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static void InvokePrivate(object instance, string name, params object[] arguments) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(instance, arguments);

    private static bool TimerReferences(DispatcherTimer timer, ProductMainWindow window) =>
        typeof(DispatcherTimer).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(field => typeof(Delegate).IsAssignableFrom(field.FieldType))
            .Select(field => field.GetValue(timer)).OfType<Delegate>()
            .SelectMany(handler => handler.GetInvocationList()).Any(handler => ReferenceEquals(handler.Target, window));

    private static async Task SendDeclarationAndObserveCloseAsync(
        TransportHost host, string certificatePin, string? declaration)
    {
        using CancellationTokenSource budget = new(TimeSpan.FromSeconds(10));
        using (TcpClient tcp = new())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, TransportConstants.Port, budget.Token);
            using SslStream tls = new(tcp.GetStream(), false, (_, certificate, _, _) =>
                certificate is not null && Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())) == certificatePin);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" }, budget.Token);
            if (declaration is not null)
            {
                await FrameWriter.WriteFrameAsync(tls, Encoding.UTF8.GetBytes(declaration),
                    TransportConstants.MaxPreAuthMessageBytes, TimeSpan.FromSeconds(10), budget.Token);
                try
                {
                    // 拒绝帧可以先到，但必须继续读至自然 EOF/RST；Host Stop 不能代替关闭证据。
                    byte[] buffer = new byte[256];
                    while (await tls.ReadAsync(buffer, budget.Token) != 0) { }
                }
                catch (IOException error) when (error.InnerException is SocketException
                    { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted })
                {
                    budget.Token.ThrowIfCancellationRequested();
                }
            }
        }
        while (host.ActiveConnections != 0 || host.AdmittedConnections != 0)
            await Task.Delay(10, budget.Token);
        Assert.True(host.IsRunning);
        Assert.Equal(0, host.ActiveConnections);
        Assert.Equal(0, host.AdmittedConnections);
    }

    private sealed class ProtocolLoopbackBindings : INetworkBindingProvider
    {
        public IReadOnlyList<NetworkBinding> GetBindings() =>
        [new("test-loopback", "仅协议拒绝测试", NetworkInterfaceType.Loopback,
            IPAddress.Loopback, IPAddress.Parse("255.0.0.0"), IPAddress.Parse("127.255.255.255"), 1)];
        public void Refresh() { } // 不枚举或修改本机网卡。
    }

    private sealed class ProtocolLoopbackPolicy : ISubnetPolicy
    {
        public bool IsAllowedPeer(IPAddress localAddress, IPAddress remoteAddress) =>
            localAddress.Equals(IPAddress.Loopback) && remoteAddress.Equals(IPAddress.Loopback);
    }

    private sealed class NoSocketDiscovery : IDiscoveryService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ProbeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public async IAsyncEnumerable<DiscoveredDevice> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }
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
