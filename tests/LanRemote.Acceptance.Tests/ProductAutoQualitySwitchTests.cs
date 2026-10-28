using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LanRemote.App.Services;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Configuration;
using LanRemote.Core.Infrastructure;
using LanRemote.Core.Models;
using LanRemote.Discovery.Networking;
using LanRemote.Security.Certificates;
using LanRemote.Security.Identity;
using LanRemote.Security.Secrets;
using LanRemote.Sessions;
using LanRemote.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace LanRemote.Acceptance.Tests;

/// <summary>
/// 自动画质开关的产品装配验证：只有本机显式勾选才给会话命名起点并启动周期 worker；
/// 关闭时与引入开关前完全一致（无 worker、无受管起点）。真实产品 Host + 真实回环会话。
/// </summary>
[Collection(AcceptanceStageFiveCollection.Name)]
public sealed class ProductAutoQualitySwitchTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact(Timeout = 120_000)]
    public Task Switch_off_keeps_sessions_unmanaged_and_starts_no_worker() =>
        InProductHostAsync(autoQuality: false, previewSettings: null, async scenario =>
        {
            Assert.Null(ReadField<object?>(scenario.Runtime, "_autoQualityLoop"));

            AuthenticatedControlSession client = await ConnectAndApproveAsync(scenario);
            try
            {
                await client.AttachVideoWithControlMonitorCoreAsync(scenario.Token).WaitAsync(Guard);
                Guid sessionId = client.SessionId;
                ReplaceableVideoFrameSource source = await WaitForPublishedSourceAsync(scenario, sessionId);
                Assert.Null(ResolveSessionQuality(scenario.Runtime, sessionId));
                IVideoProducerTelemetry measured = await WaitForActiveTelemetryAsync(source);
                Assert.Null(VideoQualityPresets.NameOf(measured.Quality));
            }
            finally
            {
                await DisposeClientAsync(scenario, client);
            }
        });

    [Fact(Timeout = 150_000)]
    public Task Switch_on_gives_the_session_a_named_starting_preset_and_stops_cleanly() =>
        InProductHostAsync(autoQuality: true, previewSettings: null, async scenario =>
        {
            HostAutoQualityLoop loop = Assert.IsType<HostAutoQualityLoop>(
                ReadField<object?>(scenario.Runtime, "_autoQualityLoop"));
            Task worker = Assert.IsAssignableFrom<Task>(loop.Completion);
            Assert.False(worker.IsCompleted);

            AuthenticatedControlSession client = await ConnectAndApproveAsync(scenario);
            try
            {
                await client.AttachVideoWithControlMonitorCoreAsync(scenario.Token).WaitAsync(Guard);
                Guid sessionId = client.SessionId;
                ReplaceableVideoFrameSource source = await WaitForPublishedSourceAsync(scenario, sessionId);
                Assert.Equal(VideoQualitySettings.Balanced, ResolveSessionQuality(scenario.Runtime, sessionId));
                IVideoProducerTelemetry measured = await WaitForActiveTelemetryAsync(source);
                Assert.Equal(VideoQualitySettings.Balanced, measured.Quality);
                Assert.Equal("balanced", VideoQualityPresets.NameOf(measured.Quality));
            }
            finally
            {
                await DisposeClientAsync(scenario, client);
            }

            await scenario.Runtime.StopAndDisposeAsync().WaitAsync(Guard);
            Assert.True(worker.IsCompleted, "停机必须 join 自动画质 worker。");
            Assert.Null(ReadField<object?>(scenario.Runtime, "_autoQualityLoop"));
            Assert.False(ReadField<bool>(scenario.Runtime, "_autoQualityEnabled"));
        });

    [Fact(Timeout = 150_000)]
    public Task Switch_on_keeps_an_explicit_host_preset_as_the_starting_point() =>
        InProductHostAsync(autoQuality: true, previewSettings: VideoQualitySettings.LowLatency, async scenario =>
        {
            Assert.NotNull(ReadField<object?>(scenario.Runtime, "_autoQualityLoop"));

            AuthenticatedControlSession client = await ConnectAndApproveAsync(scenario);
            try
            {
                await client.AttachVideoWithControlMonitorCoreAsync(scenario.Token).WaitAsync(Guard);
                Guid sessionId = client.SessionId;
                ReplaceableVideoFrameSource source = await WaitForPublishedSourceAsync(scenario, sessionId);
                Assert.Equal(VideoQualitySettings.LowLatency, ResolveSessionQuality(scenario.Runtime, sessionId));
                IVideoProducerTelemetry measured = await WaitForActiveTelemetryAsync(source);
                Assert.Equal(VideoQualitySettings.LowLatency, measured.Quality);
            }
            finally
            {
                await DisposeClientAsync(scenario, client);
            }
        });

    private static object? ResolveSessionQuality(HostRuntimeService runtime, Guid sessionId)
    {
        object resolver = ReadField<object>(runtime, "_sessionQuality");
        return resolver.GetType().GetMethod("Resolve", InstanceMembers)!.Invoke(resolver, [sessionId]);
    }

    private static async Task<ReplaceableVideoFrameSource> WaitForPublishedSourceAsync(
        ProductScenario scenario, Guid sessionId)
    {
        ReplaceableVideoFrameSource? source = null;
        Assert.True(await PollAsync(() =>
        {
            source = SnapshotSources(scenario.Runtime)
                .Where(entry => entry.Key == sessionId).Select(entry => entry.Value).FirstOrDefault();
            return source is not null;
        }, Guard), "真实会话的视频来源必须被产品 Host 登记。");
        return source!;
    }

    private static async Task<IVideoProducerTelemetry> WaitForActiveTelemetryAsync(ReplaceableVideoFrameSource source)
    {
        IVideoProducerTelemetry? measured = null;
        Assert.True(await PollAsync(() =>
        {
            measured = source.ActiveProducer as IVideoProducerTelemetry;
            return measured is not null;
        }, Guard), "已发布的 active 代际必须可读出真实生产者遥测。");
        return measured!;
    }

    private static IReadOnlyList<KeyValuePair<Guid, ReplaceableVideoFrameSource>> SnapshotSources(
        HostRuntimeService runtime) =>
        (IReadOnlyList<KeyValuePair<Guid, ReplaceableVideoFrameSource>>)runtime.GetType()
            .GetMethod("SnapshotVideoSources", InstanceMembers)!.Invoke(runtime, [])!;

    private static async Task InProductHostAsync(bool autoQuality, VideoQualitySettings? previewSettings,
        Func<ProductScenario, Task> test)
    {
        using AcceptanceTestDirectory directory = new();
        await using StaDispatcherFixture sta = new();
        Dispatcher dispatcher = await sta.Ready.WaitAsync(Guard);
        AppPaths paths = AppPaths.FromRootDirectory(directory.Path);
        await new AppConfigStore(paths).SaveAsync(new AppConfig
        {
            AllowDiscovery = false,
            AllowViewing = true,
            AllowControl = false,
            RequireLocalApprovalForUnknownController = true,
            TransportPort = TransportConstants.Port,
        });
        DpapiSecretVault vault = new(paths);
        using DeviceCertificateService certificates = new(vault);
        DeviceIdentityService identity = new(vault, certificates);
        DpapiAccessSecretStore secrets = new(vault);
        WpfLocalApprovalGate gate = new(dispatcher, NullLogger<WpfLocalApprovalGate>.Instance);
        HostRuntimeService runtime = new(paths, identity, certificates, secrets, new SwitchLoopbackBindings(),
            new SwitchLoopbackPolicy(), gate, NullLogger<HostRuntimeService>.Instance);
        using CancellationTokenSource budget = new(TimeSpan.FromSeconds(150));
        ProductScenario? scenario = null;
        AccessSecret? secret = null;
        Exception? failure = null;
        List<Exception> cleanupErrors = [];
        try
        {
            Assert.True(runtime.OpenApprovals());
            runtime.AllowListening();
            TransportHostStartResult started = await runtime.StartAsync(budget.Token, previewSettings, autoQuality)
                .WaitAsync(Guard);
            Assert.True(started.IsListening, "固定协议端口必须可在回环地址监听；测试不修改端口或系统网络配置。");
            TransportHost host = ReadField<TransportHost>(runtime, "_host");
            Assert.True(host.IsRunning);
            Delegate handler = ReadField<Delegate>(host, "_sessionHandler");
            object captured = Assert.IsAssignableFrom<object>(handler.Target);
            ControlAuthContext context = Assert.Single(captured.GetType().GetFields(InstanceMembers)
                .Where(field => field.FieldType == typeof(ControlAuthContext))
                .Select(field => Assert.IsType<ControlAuthContext>(field.GetValue(captured))));
            DeviceCertificate certificate = await certificates.GetOrCreateAsync(budget.Token);
            Assert.True(ConnectionTarget.TryCreate(context.ServerDeviceId, IPAddress.Loopback,
                TransportConstants.Port, certificate.Sha256FingerprintHex, out ConnectionTarget? target));
            secret = await secrets.LoadOrCreateAsync(budget.Token);
            scenario = new ProductScenario(runtime, context, dispatcher, target!, secret.AccessKeyBytes,
                budget.Token);
            await test(scenario);
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            await CleanupAsync(async () =>
            {
                if (scenario?.Connecting is not { } connecting || scenario.ObservedClient is not null) return;
                try
                {
                    AuthenticatedControlSession client = await connecting.WaitAsync(Guard);
                    await client.DisposeAsync().AsTask().WaitAsync(Guard);
                }
                catch (OperationCanceledException) when (budget.IsCancellationRequested && connecting.IsCanceled) { }
            });
            await CleanupAsync(() => budget.CancelAsync());
            await CleanupAsync(async () =>
            {
                await runtime.StopAndDisposeAsync().WaitAsync(Guard);
                Assert.True(runtime.IsFullyDrained);
            });
            if (secret is not null) CryptographicOperations.ZeroMemory(secret.AccessKeyBytes);
        }

        if (cleanupErrors.Count != 0)
            throw new AggregateException("自动画质开关测试或原资源清理失败。",
                failure is null ? cleanupErrors : [failure, .. cleanupErrors]);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();

        async Task CleanupAsync(Func<Task> operation)
        {
            try { await operation(); }
            catch (Exception error) { cleanupErrors.Add(error); }
        }
    }

    private static async Task<AuthenticatedControlSession> ConnectAndApproveAsync(ProductScenario scenario)
    {
        Task<AuthenticatedControlSession> connecting = scenario.Connect();
        Window approval = await WaitForApprovalAsync(scenario, connecting);
        Assert.False(connecting.IsCompleted);
        await scenario.Dispatcher.InvokeAsync(() =>
        {
            (RadioButton viewOnly, Button approve, _) = ApprovalControls(approval);
            viewOnly.IsChecked = true;
            approve.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, approve));
        }).Task.WaitAsync(Guard);
        return await connecting.WaitAsync(Guard);
    }

    private static async Task<Window> WaitForApprovalAsync(ProductScenario scenario, Task connecting)
    {
        long start = Stopwatch.GetTimestamp();
        while (true)
        {
            Window[] windows = await scenario.Dispatcher.InvokeAsync(
                () => ApprovalWindows(scenario.Dispatcher), DispatcherPriority.ContextIdle).Task.WaitAsync(Guard);
            if (windows.Length != 0) return Assert.Single(windows);
            Assert.False(connecting.IsCompleted, $"真实本机审批出现前认证已结束：{connecting.Exception}");
            Assert.True(Stopwatch.GetElapsedTime(start) < Guard, "未观察到真实 WPF 本机审批窗口。");
            await Task.Delay(10, scenario.Token);
        }
    }

    // 带 Control monitor 的会话主动断开必然留下原读中断诊断；真实产品把它保留为「操作诊断已保留」，
    // 不洗成静默成功。这里只吞掉这一类既定诊断，其它故障仍然让本用例失败。
    private static async Task DisposeClientAsync(ProductScenario scenario, AuthenticatedControlSession client)
    {
        try
        {
            await client.DisposeAsync().AsTask().WaitAsync(Guard);
        }
        catch (AggregateException error) when (error.Flatten().InnerExceptions.All(
            inner => inner is IOException or OperationCanceledException))
        {
        }
        scenario.ObservedClient = client;
    }

    private static async Task<bool> PollAsync(Func<bool> predicate, TimeSpan limit)
    {
        long start = Stopwatch.GetTimestamp();
        while (!predicate())
        {
            if (Stopwatch.GetElapsedTime(start) >= limit) return false;
            await Task.Delay(20);
        }
        return true;
    }

    private static Window[] ApprovalWindows(Dispatcher dispatcher) =>
        PresentationSource.CurrentSources.Cast<PresentationSource>().Select(source => source.RootVisual)
            .OfType<Window>().Where(window => ReferenceEquals(window.Dispatcher, dispatcher) &&
                window.Title == "LanRemote 本机连接审批" && window.IsVisible).ToArray();

    private static (RadioButton ViewOnly, Button Approve, Button Deny) ApprovalControls(Window window)
    {
        StackPanel content = Assert.IsType<StackPanel>(window.Content);
        StackPanel buttons = Assert.Single(content.Children.OfType<StackPanel>());
        return (Assert.Single(content.Children.OfType<RadioButton>()),
            Assert.Single(buttons.Children.OfType<Button>(), button => Equals(button.Content, "批准仅查看")),
            Assert.Single(buttons.Children.OfType<Button>(), button => Equals(button.Content, "拒绝")));
    }

    private static T ReadField<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, InstanceMembers)!.GetValue(instance)!;

    private sealed class ProductScenario(HostRuntimeService runtime,
        ControlAuthContext context, Dispatcher dispatcher, ConnectionTarget target, byte[] key,
        CancellationToken token)
    {
        internal HostRuntimeService Runtime { get; } = runtime;
        internal ControlAuthContext Context { get; } = context;
        internal Dispatcher Dispatcher { get; } = dispatcher;
        internal CancellationToken Token { get; } = token;
        internal Task<AuthenticatedControlSession>? Connecting { get; private set; }
        internal AuthenticatedControlSession? ObservedClient { get; set; }

        internal Task<AuthenticatedControlSession> Connect()
        {
            Assert.Null(Connecting);
            byte[] supplied = key.ToArray();
            try
            {
                return Connecting = new ControlClientConnector().ConnectAndAuthenticateAsync(target,
                    Guid.NewGuid(), "自动画质开关测试", supplied, SessionPermission.ViewOnly,
                    cancellationToken: Token);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(supplied);
            }
        }
    }

    private sealed class SwitchLoopbackBindings : INetworkBindingProvider
    {
        public IReadOnlyList<NetworkBinding> GetBindings() =>
        [new("auto-quality-switch-loopback", "仅自动画质开关测试", NetworkInterfaceType.Loopback,
            IPAddress.Loopback, IPAddress.Parse("255.0.0.0"), IPAddress.Parse("127.255.255.255"), 1)];
        public void Refresh() { }
    }

    private sealed class SwitchLoopbackPolicy : ISubnetPolicy
    {
        public bool IsAllowedPeer(IPAddress localAddress, IPAddress remoteAddress) =>
            IPAddress.Loopback.Equals(localAddress) && IPAddress.Loopback.Equals(remoteAddress);
    }
}
