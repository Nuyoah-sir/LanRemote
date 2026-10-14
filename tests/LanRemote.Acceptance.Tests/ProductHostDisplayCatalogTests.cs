using System.Diagnostics;
using System.IO;
using Expression = System.Linq.Expressions.Expression;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LanRemote.App.Services;
using LanRemote.Capture;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Configuration;
using LanRemote.Core.Infrastructure;
using LanRemote.Core.Models;
using LanRemote.Discovery.Networking;
using LanRemote.Security.Certificates;
using LanRemote.Security.Identity;
using LanRemote.Security.Secrets;
using LanRemote.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace LanRemote.Acceptance.Tests;

[Collection(AcceptanceStageFiveCollection.Name)]
public sealed class ProductHostDisplayCatalogTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public Task Product_Start_Installs_Catalog_But_Wrong_Key_Or_Real_Local_Denial_Never_Calls_Source(bool wrongKey) =>
        InProductHostAsync(async scenario =>
        {
            Task<AuthenticatedControlSession> connecting = scenario.Connect(wrongKey);
            if (!wrongKey)
            {
                Window approval = await WaitForApprovalAsync(scenario, connecting);
                Assert.False(connecting.IsCompleted);
                Assert.Equal(0, scenario.Source.Calls);
                Assert.Empty(scenario.Context.SessionRegistry.Snapshot());
                Assert.Equal(1, scenario.Context.PendingApprovalLimiter.GlobalInUse);
                await scenario.Dispatcher.InvokeAsync(() =>
                {
                    var controls = ApprovalControls(approval);
                    Assert.False(controls.Approve.IsEnabled);
                    Assert.False(controls.ViewOnly.IsChecked == true);
                    controls.Deny.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, controls.Deny));
                }).Task.WaitAsync(Guard);
            }

            ControlClientAuthenticationException error = await Assert.ThrowsAsync<ControlClientAuthenticationException>(
                () => connecting.WaitAsync(Guard));
            scenario.ObservedConnectionError = error;
            Assert.Equal("client-remote-authentication-failed", error.Rejection);
            await AssertNaturallyDrainedAsync(scenario);
            Assert.Equal(0, scenario.Source.Calls);
            Assert.Equal(wrongKey ? 1 : 0,
                scenario.Context.FailedAuthLimiter.CountRecentFailures(IPAddress.Loopback));
            await scenario.Dispatcher.InvokeAsync(() => Assert.Empty(ApprovalWindows(scenario.Dispatcher)))
                .Task.WaitAsync(Guard);
        });

    [Fact(Timeout = 60_000)]
    [Trait("Category", "InteractiveDesktop")]
    public Task Product_Start_Real_Gdi_Catalog_Reaches_Authenticated_Tls_Only_After_Wpf_ViewOnly_Approval() =>
        InProductHostAsync(async scenario =>
        {
            Task<AuthenticatedControlSession> connecting = scenario.Connect(wrongKey: false);
            Window approval = await WaitForApprovalAsync(scenario, connecting);
            Assert.False(connecting.IsCompleted);
            Assert.Equal(0, scenario.Source.Calls);
            Assert.Empty(scenario.Context.SessionRegistry.Snapshot());
            Assert.Equal(1, scenario.Context.PendingApprovalLimiter.GlobalInUse);

            // 桌面前置：这里显式读取真实 GDI 元数据作比对，不是替换产品来源。
            // 无附着显示设备或测试期间拓扑变化必须使本用例失败，不能冒充实机验证通过。
            DisplayInfo[] expected = new GdiDisplayCatalog().GetDisplays().ToArray();
            Assert.NotEmpty(expected);
            long beforeApproval = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await scenario.Dispatcher.InvokeAsync(() =>
            {
                var controls = ApprovalControls(approval);
                Assert.False(controls.Approve.IsEnabled);
                controls.ViewOnly.IsChecked = true;
                Assert.True(controls.Approve.IsEnabled);
                controls.Approve.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, controls.Approve));
            }).Task.WaitAsync(Guard);

            AuthenticatedControlSession client = await connecting.WaitAsync(Guard);
            Assert.Equal(SessionPermission.ViewOnly, client.GrantedPermission);
            Assert.Equal(scenario.Context.ServerDeviceId, client.Identity.DeviceId);
            Assert.True(client.Identity.PinsMatch);
            // 没有视频 attach，因此没有启动客户端 Control monitor；本测试是认证终帧后的唯一读者。
            Assert.Null(ReadField<object?>(client, "_controlMonitor"));
            Assert.Null(ReadField<object?>(client, "_videoLifetime"));
            Stream stream = Assert.IsAssignableFrom<Stream>(ReadProperty(client, "Stream"));
            byte[] payload = await new FrameReader(stream).ReadFrameAsync(256 * 1024, Guard, Guard, scenario.Token);
            try
            {
                using JsonDocument document = JsonDocument.Parse(payload);
                JsonElement root = document.RootElement;
                Assert.Equal(new[] { "displays", "protocol", "seq", "timestampMs", "type" },
                    root.EnumerateObject().Select(property => property.Name).Order().ToArray());
                Assert.Equal("host_status", root.GetProperty("type").GetString());
                Assert.Equal(1, root.GetProperty("protocol").GetInt32());
                Assert.Equal(0L, root.GetProperty("seq").GetInt64());
                Assert.InRange(root.GetProperty("timestampMs").GetInt64(), beforeApproval,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                DisplayInfo[] actual = root.GetProperty("displays").EnumerateArray().Select(item =>
                {
                    Assert.Equal(new[] { "height", "id", "isPrimary", "name", "width", "x", "y" },
                        item.EnumerateObject().Select(property => property.Name).Order().ToArray());
                    return new DisplayInfo(new DisplayId(item.GetProperty("id").GetInt32()),
                        item.GetProperty("name").GetString()!, item.GetProperty("x").GetInt32(),
                        item.GetProperty("y").GetInt32(), item.GetProperty("width").GetInt32(),
                        item.GetProperty("height").GetInt32(), item.GetProperty("isPrimary").GetBoolean());
                }).ToArray();
                Assert.Equal(expected, actual);
            }
            finally { CryptographicOperations.ZeroMemory(payload); }

            Assert.Equal(1, scenario.Source.Calls);
            ControlSessionSummary registered = Assert.Single(scenario.Context.SessionRegistry.Snapshot());
            Assert.Equal(client.SessionId, registered.SessionId);
            Assert.Equal(SessionPermission.ViewOnly, registered.GrantedPermission);
            Assert.Equal(0, scenario.Context.PendingApprovalLimiter.GlobalInUse);
            Assert.Equal(1, scenario.Host.ActiveConnections);
            Assert.Equal(1, scenario.Host.AdmittedConnections);
            Assert.True(scenario.Runtime.IsListening);

            await client.DisposeAsync().AsTask().WaitAsync(Guard);
            await AssertNaturallyDrainedAsync(scenario);
            Assert.Equal(1, scenario.Source.Calls);
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
        HostRuntimeService runtime = new(paths, identity, certificates, secrets,
            new CatalogLoopbackBindings(), new CatalogLoopbackPolicy(), gate,
            NullLogger<HostRuntimeService>.Instance);
        using CancellationTokenSource budget = new(TimeSpan.FromSeconds(45));
        ProductScenario? scenario = null;
        AccessSecret? secret = null;
        Exception? failure = null;
        List<Exception> cleanupErrors = [];
        try
        {
            Assert.True(runtime.OpenApprovals());
            runtime.AllowListening();
            TransportHostStartResult started = await runtime.StartAsync(budget.Token).WaitAsync(Guard);
            Assert.True(started.IsListening, "固定协议端口必须可在回环地址监听；测试不修改端口或系统网络配置。");
            Assert.Empty(started.Failures);
            Assert.Equal(IPAddress.Loopback, Assert.Single(started.BoundAddresses));
            Assert.True(runtime.IsListening);
            Assert.Null(runtime.ActivePreviewSettings);
            TransportHost host = ReadField<TransportHost>(runtime, "_host");
            Delegate handler = ReadField<Delegate>(host, "_sessionHandler");
            object captured = Assert.IsAssignableFrom<object>(handler.Target);
            ControlAuthContext context = Assert.Single(captured.GetType().GetFields(InstanceMembers)
                .Where(field => field.FieldType == typeof(ControlAuthContext))
                .Select(field => Assert.IsType<ControlAuthContext>(field.GetValue(captured))));
            Assert.Same(gate, context.ApprovalGate);
            Assert.Same(secrets, context.AccessSecretStore);
            Assert.True(context.Options.RequireLocalApproval);
            Assert.Empty(context.SessionRegistry.Snapshot());
            Assert.Equal(0, host.ActiveConnections);
            Assert.Equal(0, host.AdmittedConnections);

            SourceProbe source = ObserveInstalledSource(context);
            Assert.Equal(0, source.Calls);
            DeviceCertificate certificate = await certificates.GetOrCreateAsync(budget.Token);
            Assert.True(ConnectionTarget.TryCreate(context.ServerDeviceId, IPAddress.Loopback,
                TransportConstants.Port, certificate.Sha256FingerprintHex, out ConnectionTarget? target));
            secret = await secrets.LoadOrCreateAsync(budget.Token);
            scenario = new ProductScenario(runtime, host, context, dispatcher, target!, secret.AccessKeyBytes,
                source, budget.Token);
            await test(scenario);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            await CleanupAsync(() => budget.CancelAsync());
            await CleanupAsync(async () =>
            {
                if (scenario?.Connecting is not { } connecting) return;
                // 失败路径仍 join 原认证；仅不重复计入主体已观察的同一拒绝，以及清理主动取消。
                // 其它认证或释放错误保留，且不能阻止下面的原 Host 停机。
                try
                {
                    AuthenticatedControlSession client = await connecting.WaitAsync(Guard);
                    await client.DisposeAsync().AsTask().WaitAsync(Guard);
                }
                catch (Exception error) when (ReferenceEquals(error, scenario.ObservedConnectionError)) { }
                catch (OperationCanceledException) when (budget.IsCancellationRequested && connecting.IsCanceled) { }
            });
            await CleanupAsync(async () =>
            {
                await runtime.StopAndDisposeAsync().WaitAsync(Guard);
                Assert.True(runtime.IsFullyDrained);
            });
            if (secret is not null) CryptographicOperations.ZeroMemory(secret.AccessKeyBytes);
        }

        if (cleanupErrors.Count != 0)
            throw new AggregateException("产品目录测试或原资源清理失败。",
                failure is null ? cleanupErrors : [failure, .. cleanupErrors]);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();

        async Task CleanupAsync(Func<Task> operation)
        {
            try { await operation(); }
            catch (Exception error) { cleanupErrors.Add(error); }
        }
    }

    private static SourceProbe ObserveInstalledSource(ControlAuthContext context)
    {
        object options = Assert.IsAssignableFrom<object>(ReadProperty(context, "DisplayCatalogSend"));
        Assert.Equal(TimeSpan.FromSeconds(10), Assert.IsType<TimeSpan>(ReadProperty(options, "Budget")));
        Delegate original = Assert.IsAssignableFrom<Delegate>(ReadProperty(options, "Source"));
        SourceProbe probe = new();
        // 只给真实 StartAsync 已装配的 delegate 增加计数；不调用来源、不重建帧、不换成测试目录。
        // 观察从 StartAsync 返回后开始，不能据此声称启动期间从未执行 GDI。
        Delegate observed = Expression.Lambda(original.GetType(), Expression.Block(
            Expression.Call(Expression.Constant(probe), typeof(SourceProbe).GetMethod(nameof(SourceProbe.Enter))!),
            Expression.Invoke(Expression.Constant(original, original.GetType())))).Compile();
        ConstructorInfo constructor = Assert.Single(options.GetType().GetConstructors(InstanceMembers));
        object observingOptions = constructor.Invoke([observed, TimeSpan.FromSeconds(10)]);
        typeof(ControlAuthContext).GetProperty("DisplayCatalogSend", InstanceMembers)!.SetValue(context, observingOptions);
        return probe;
    }

    private static async Task<Window> WaitForApprovalAsync(ProductScenario scenario, Task connecting)
    {
        long start = Stopwatch.GetTimestamp();
        while (true)
        {
            Window[] windows = await scenario.Dispatcher.InvokeAsync(() => ApprovalWindows(scenario.Dispatcher),
                DispatcherPriority.ContextIdle).Task.WaitAsync(Guard);
            if (windows.Length != 0) return Assert.Single(windows);
            Assert.False(connecting.IsCompleted, $"真实本机审批出现前认证已结束：{connecting.Exception}");
            Assert.True(Stopwatch.GetElapsedTime(start) < Guard, "未观察到真实 WPF 本机审批窗口。");
            await Task.Delay(10, scenario.Token);
        }
    }

    private static async Task AssertNaturallyDrainedAsync(ProductScenario scenario)
    {
        using CancellationTokenSource budget = new(Guard);
        while (scenario.Host.ActiveConnections != 0 || scenario.Host.AdmittedConnections != 0 ||
            scenario.Context.SessionRegistry.ActiveSessionCount != 0 ||
            scenario.Context.PendingApprovalLimiter.GlobalInUse != 0)
            await Task.Delay(10, budget.Token);
        Assert.True(scenario.Host.IsRunning, "连接必须在 Host 停机前自然排空。");
        Assert.True(scenario.Runtime.IsListening);
        Assert.Empty(scenario.Context.SessionRegistry.Snapshot());
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

    private sealed class SourceProbe
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        public void Enter() => Interlocked.Increment(ref _calls);
    }

    private sealed class ProductScenario(HostRuntimeService runtime, TransportHost host,
        ControlAuthContext context, Dispatcher dispatcher, ConnectionTarget target, byte[] key,
        SourceProbe source, CancellationToken token)
    {
        internal HostRuntimeService Runtime { get; } = runtime;
        internal TransportHost Host { get; } = host;
        internal ControlAuthContext Context { get; } = context;
        internal Dispatcher Dispatcher { get; } = dispatcher;
        internal SourceProbe Source { get; } = source;
        internal CancellationToken Token { get; } = token;
        internal Task<AuthenticatedControlSession>? Connecting { get; private set; }
        internal Exception? ObservedConnectionError { get; set; }

        internal Task<AuthenticatedControlSession> Connect(bool wrongKey)
        {
            Assert.Null(Connecting);
            byte[] supplied = key.ToArray();
            if (wrongKey) supplied[0] ^= 0xff;
            try
            {
                return Connecting = new ControlClientConnector().ConnectAndAuthenticateAsync(target,
                    Guid.NewGuid(), "产品只读目录接线测试", supplied, SessionPermission.ViewOnly,
                    cancellationToken: Token);
            }
            finally { CryptographicOperations.ZeroMemory(supplied); }
        }
    }

    private sealed class CatalogLoopbackBindings : INetworkBindingProvider
    {
        public IReadOnlyList<NetworkBinding> GetBindings() =>
        [new("catalog-loopback", "仅产品目录测试", NetworkInterfaceType.Loopback,
            IPAddress.Loopback, IPAddress.Parse("255.0.0.0"), IPAddress.Parse("127.255.255.255"), 1)];
        public void Refresh() { }
    }

    private sealed class CatalogLoopbackPolicy : ISubnetPolicy
    {
        public bool IsAllowedPeer(IPAddress localAddress, IPAddress remoteAddress) =>
            IPAddress.Loopback.Equals(localAddress) && IPAddress.Loopback.Equals(remoteAddress);
    }
}
