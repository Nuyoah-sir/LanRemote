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
/// 产品装配层的自动画质联调：真实 HostRuntimeService + 真实 TLS 认证会话 + 真实视频附着。
/// 只证明产品把 ping/来源观察/周期 worker 装上并在真实 Control 流上结算 RTT；
/// 不证明两机、真人、实屏或画质换代后的显示效果。
/// </summary>
[Collection(AcceptanceStageFiveCollection.Name)]
public sealed class ProductAutoQualityWiringTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RoundTripGuard = TimeSpan.FromSeconds(25);
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact(Timeout = 90_000)]
    public Task Product_Host_Assembles_Ping_Source_Observer_And_A_Running_Auto_Quality_Worker() =>
        InProductHostAsync(scenario =>
        {
            object catalog = Assert.IsAssignableFrom<object>(ReadProperty(scenario.Context, "DisplayCatalogSend"));
            Assert.NotNull(ReadProperty(catalog, "VideoSourcePublished"));
            Assert.NotNull(ReadProperty(catalog, "VideoSourceWithdrawn"));
            Assert.NotNull(ReadProperty(scenario.Context, "ControlPing"));

            HostAutoQualityLoop loop = ReadWorker(scenario.Runtime);
            Task worker = Assert.IsAssignableFrom<Task>(loop.Completion);
            Assert.False(worker.IsCompleted, "产品 Host 监听发布后必须已经启动自动画质 worker。");
            Assert.Equal(0, ReadField<ControlPingTelemetry>(scenario.Runtime, "_controlPingTelemetry").Count);
            Assert.Empty(SnapshotSources(scenario.Runtime));
            return Task.CompletedTask;
        });

    [Fact(Timeout = 150_000)]
    public Task Product_Real_Session_Settles_Round_Trip_And_Registers_Its_Published_Source() =>
        InProductHostAsync(async scenario =>
        {
            AuthenticatedControlSession client = await ConnectAndApproveAsync(scenario);
            try
            {
                var video = await client.AttachVideoWithControlMonitorCoreAsync(scenario.Token).WaitAsync(Guard);
                Assert.NotNull(video);
                Guid sessionId = client.SessionId;
                Assert.Equal(sessionId, Assert.Single(scenario.Context.SessionRegistry.Snapshot()).SessionId);

                // 已发布来源必须真的进入产品 Host 的登记：观察接缝装上，且换代沿用本次实际选中的显示器。
                ReplaceableVideoFrameSource source = await WaitForPublishedSourceAsync(scenario, sessionId);
                Assert.Equal(sessionId, source.SessionId);
                IVideoProducerTelemetry measured = await WaitForActiveTelemetryAsync(source);
                Assert.NotNull(measured.Display);
                Assert.True(measured.Display!.IsPrimary, "初始代际必须沿用本次实际选中的主屏。");

                // 真实 Control 流上的一次 ping/pong 往返；不是计时器伪造，也不是元数据回填。
                ControlPingTelemetry telemetry = ReadField<ControlPingTelemetry>(scenario.Runtime, "_controlPingTelemetry");
                Assert.True(await PollAsync(() => telemetry.TryGet(sessionId, out ControlPingTelemetry.SessionSnapshot? probe)
                        && probe is { SampleCount: >= 1, LatestRtt: not null }, RoundTripGuard),
                    "真实产品会话必须完成一次 ping/pong 往返并按会话登记 RTT。");
                Assert.True(telemetry.TryGet(sessionId, out ControlPingTelemetry.SessionSnapshot? snapshot));
                Assert.NotNull(snapshot!.LatestRtt);
                Assert.True(snapshot.LatestRtt!.Value >= TimeSpan.Zero);
                Assert.True(snapshot.SampleCount >= 1);
            }
            finally
            {
                await DisposeClientAsync(scenario, client);
            }
            await WaitNaturalDrainAsync(scenario);
        });

    [Fact(Timeout = 150_000)]
    public Task Product_Stop_Joins_Worker_And_Clears_Registry_After_Session_Withdrawal() =>
        InProductHostAsync(async scenario =>
        {
            HostAutoQualityLoop loop = ReadWorker(scenario.Runtime);
            Task worker = Assert.IsAssignableFrom<Task>(loop.Completion);

            AuthenticatedControlSession client = await ConnectAndApproveAsync(scenario);
            await client.AttachVideoWithControlMonitorCoreAsync(scenario.Token).WaitAsync(Guard);
            Guid sessionId = client.SessionId;
            await WaitForPublishedSourceAsync(scenario, sessionId);
            Assert.Contains(sessionId, SnapshotSources(scenario.Runtime).Select(entry => entry.Key));

            await DisposeClientAsync(scenario, client);
            await WaitNaturalDrainAsync(scenario);
            Assert.True(await PollAsync(
                    () => !SnapshotSources(scenario.Runtime).Any(entry => entry.Key == sessionId), Guard),
                "会话摘槽后产品 Host 必须注销该会话的视频来源登记。");

            await scenario.Runtime.StopAndDisposeAsync().WaitAsync(Guard);
            Assert.True(worker.IsCompleted, "停机必须 join 自动画质 worker，不能留下仍在运行的周期任务。");
            Assert.Null(ReadField<HostAutoQualityLoop?>(scenario.Runtime, "_autoQualityLoop"));
            Assert.Empty(SnapshotSources(scenario.Runtime));
            Assert.Equal(0, ReadField<ControlPingTelemetry>(scenario.Runtime, "_controlPingTelemetry").Count);
        });

    private static async Task InProductHostAsync(Func<ProductScenario, Task> test)
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
        HostRuntimeService runtime = new(paths, identity, certificates, secrets, new WiringLoopbackBindings(),
            new WiringLoopbackPolicy(), gate, NullLogger<HostRuntimeService>.Instance);
        using CancellationTokenSource budget = new(TimeSpan.FromSeconds(150));
        ProductScenario? scenario = null;
        AccessSecret? secret = null;
        Exception? failure = null;
        List<Exception> cleanupErrors = [];
        try
        {
            Assert.True(runtime.OpenApprovals());
            runtime.AllowListening();
            TransportHostStartResult started = await runtime.StartAsync(budget.Token, null, true).WaitAsync(Guard);
            Assert.True(started.IsListening, "固定协议端口必须可在回环地址监听；测试不修改端口或系统网络配置。");
            Assert.Empty(started.Failures);
            TransportHost host = ReadField<TransportHost>(runtime, "_host");
            Delegate handler = ReadField<Delegate>(host, "_sessionHandler");
            object captured = Assert.IsAssignableFrom<object>(handler.Target);
            ControlAuthContext context = Assert.Single(captured.GetType().GetFields(InstanceMembers)
                .Where(field => field.FieldType == typeof(ControlAuthContext))
                .Select(field => Assert.IsType<ControlAuthContext>(field.GetValue(captured))));
            Assert.Empty(context.SessionRegistry.Snapshot());
            DeviceCertificate certificate = await certificates.GetOrCreateAsync(budget.Token);
            Assert.True(ConnectionTarget.TryCreate(context.ServerDeviceId, IPAddress.Loopback,
                TransportConstants.Port, certificate.Sha256FingerprintHex, out ConnectionTarget? target));
            secret = await secrets.LoadOrCreateAsync(budget.Token);
            scenario = new ProductScenario(runtime, host, context, dispatcher, target!, secret.AccessKeyBytes,
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
                // 主体已释放的原客户端不重复释放；否则同一原诊断会被算成第二次清理失败。
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
            throw new AggregateException("产品自动画质接线测试或原资源清理失败。",
                failure is null ? cleanupErrors : [failure, .. cleanupErrors]);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();

        async Task CleanupAsync(Func<Task> operation)
        {
            try { await operation(); }
            catch (Exception error) { cleanupErrors.Add(error); }
        }
    }

    private static HostAutoQualityLoop ReadWorker(HostRuntimeService runtime) =>
        Assert.IsType<HostAutoQualityLoop>(ReadField<object>(runtime, "_autoQualityLoop"));

    private static IReadOnlyList<KeyValuePair<Guid, ReplaceableVideoFrameSource>> SnapshotSources(
        HostRuntimeService runtime) =>
        (IReadOnlyList<KeyValuePair<Guid, ReplaceableVideoFrameSource>>)runtime.GetType()
            .GetMethod("SnapshotVideoSources", InstanceMembers)!.Invoke(runtime, [])!;

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

    private static async Task<AuthenticatedControlSession> ConnectAndApproveAsync(ProductScenario scenario)
    {
        Task<AuthenticatedControlSession> connecting = scenario.Connect();
        Window approval = await WaitForApprovalAsync(scenario, connecting);
        Assert.False(connecting.IsCompleted);
        await scenario.Dispatcher.InvokeAsync(() =>
        {
            (RadioButton viewOnly, Button approve, _) = ApprovalControls(approval);
            Assert.False(approve.IsEnabled);
            Assert.False(viewOnly.IsChecked == true);
            viewOnly.IsChecked = true;
            approve.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, approve));
        }).Task.WaitAsync(Guard);
        AuthenticatedControlSession client = await connecting.WaitAsync(Guard);
        Assert.Equal(SessionPermission.ViewOnly, client.GrantedPermission);
        return client;
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

    private static async Task WaitNaturalDrainAsync(ProductScenario scenario)
    {
        Assert.True(await PollAsync(() => scenario.Host.ActiveConnections == 0 &&
                scenario.Host.AdmittedConnections == 0 &&
                scenario.Context.SessionRegistry.ActiveSessionCount == 0, Guard),
            "客户端释放后 Host 必须在停机前自然排空原连接。");
        Assert.True(scenario.Host.IsRunning);
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

    private static object? ReadProperty(object instance, string name) =>
        instance.GetType().GetProperty(name, InstanceMembers)!.GetValue(instance);

    private sealed class ProductScenario(HostRuntimeService runtime, TransportHost host,
        ControlAuthContext context, Dispatcher dispatcher, ConnectionTarget target, byte[] key,
        CancellationToken token)
    {
        internal HostRuntimeService Runtime { get; } = runtime;
        internal TransportHost Host { get; } = host;
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
                    Guid.NewGuid(), "产品自动画质接线测试", supplied, SessionPermission.ViewOnly,
                    cancellationToken: Token);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(supplied);
            }
        }
    }

    private sealed class WiringLoopbackBindings : INetworkBindingProvider
    {
        public IReadOnlyList<NetworkBinding> GetBindings() =>
        [new("auto-quality-loopback", "仅产品自动画质接线测试", NetworkInterfaceType.Loopback,
            IPAddress.Loopback, IPAddress.Parse("255.0.0.0"), IPAddress.Parse("127.255.255.255"), 1)];
        public void Refresh() { }
    }

    private sealed class WiringLoopbackPolicy : ISubnetPolicy
    {
        public bool IsAllowedPeer(IPAddress localAddress, IPAddress remoteAddress) =>
            IPAddress.Loopback.Equals(localAddress) && IPAddress.Loopback.Equals(remoteAddress);
    }
}
