// 真实 Control TLS 回环 + 协调 Host（ControlAuthContext.WithDisplayCatalogSource +
// WithSessionDisplayRequestPrepare，同一会话写者发目录与回执）+ 真实 ClientConnectService /
// RemoteViewWindow 切屏接线；但显示目录为合成 DisplayInfo、两个视频源为合成 2x2 JPEG 生产者，
// 不代表真实副屏枚举、物理 scanout 或双机验收。

using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LanRemote.App;
using LanRemote.App.Services;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Encoding;
using LanRemote.Core.Infrastructure;
using LanRemote.Core.Models;
using LanRemote.Security.Certificates;
using LanRemote.Security.Identity;
using LanRemote.Security.Secrets;
using LanRemote.Transport;

namespace LanRemote.Acceptance.Tests;

[Collection(AcceptanceStageFiveCollection.Name)]
public sealed class ProductDisplaySwitchLoopbackTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

    [Fact(Timeout = 60_000)]
    public Task Product_Client_Switches_Display_Over_Real_Control_Tls_And_Presents_New_Generation() =>
        RunLoopbackAsync(rejectOnly: false);

    [Fact(Timeout = 60_000)]
    public Task Product_Client_Rejects_Display_Outside_Catalog_Without_Ending_Session() =>
        RunLoopbackAsync(rejectOnly: true);

    private static async Task RunLoopbackAsync(bool rejectOnly)
    {
        await using StaDispatcherFixture sta = new();
        Dispatcher dispatcher = await sta.Ready.WaitAsync(Guard);
        Task run = dispatcher.InvokeAsync(() => RunOnStaAsync(rejectOnly)).Task.Unwrap();
        try { await run.WaitAsync(TimeSpan.FromSeconds(55)); }
        finally { await run.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing); }
    }

    private static async Task RunOnStaAsync(bool rejectOnly)
    {
        Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
        string root = Path.Combine(Path.GetTempPath(), $"LanRemote-product-switch-loopback-{Guid.NewGuid():N}");
        byte[] remoteKey = Convert.FromHexString("0F1E2D3C4B5A69788796A5B4C3D2E1F0");
        byte[] localKey = Convert.FromHexString("F0E1D2C3B4A5968778695A4B3C2D1E0F");
        X509Certificate2? certificate = null;
        DeviceCertificateService? localCertificates = null;
        TransportHost? host = null;
        Window? owner = null;
        ClientConnectService? clientService = null;
        Task? client = null;
        Task? observedClient = null;
        Task? observedCleanup = null;
        ManualApprovalGate gate = new();
        SwitchLoopbackFactory? factory = null;
        ControlAuthContext? context = null;
        LoopbackPolicy policy = new();
        Exception? failure = null;
        List<Exception> cleanupErrors = [];
        int catalogCalls = 0;
        DisplayInfo[] catalogDisplays = [
            new(new DisplayId(4), "远端主屏", 0, 0, 1920, 1080, true),
            new(new DisplayId(9), "左侧屏幕 <不解析为标记>", -1280, -120, 1280, 1024, false)];
        using CancellationTokenSource externalCancellation = new();
        try
        {
            Directory.CreateDirectory(root);
            AppPaths paths = AppPaths.FromRootDirectory(root);
            DpapiSecretVault vault = new(paths);
            await vault.UpdateAsync(bundle =>
            {
                bundle.AccessKey = CrockfordBase32.Encode(localKey);
                return true;
            });
            DpapiAccessSecretStore localStore = new(vault);
            AccessSecret local = await localStore.LoadOrCreateAsync();
            try
            {
                Assert.Equal(localKey, local.AccessKeyBytes);
                Assert.NotEqual(remoteKey, local.AccessKeyBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(local.AccessKeyBytes);
            }

            localCertificates = new DeviceCertificateService(vault);
            DeviceIdentityService identity = new(vault, localCertificates);
            DeviceIdentity clientIdentity = await identity.GetOrCreateAsync();
            Guid remoteDeviceId = Guid.NewGuid();
            Assert.NotEqual(clientIdentity.DeviceId, remoteDeviceId);
            certificate = CreateServerCertificate();
            factory = new SwitchLoopbackFactory(
                EncodeTwoPixelJpeg(Brushes.CornflowerBlue), EncodeTwoPixelJpeg(Brushes.IndianRed));
            context = new ControlAuthContext
            {
                ServerDeviceId = remoteDeviceId,
                AccessSecretStore = new FixedRemoteSecretStore(remoteKey),
                FailedAuthLimiter = new FailedAuthLimiter(),
                PendingApprovalLimiter = new ConnectionAdmissionLimiter(3, 1),
                ApprovalGate = gate,
                SessionRegistry = new SessionRegistry(),
                Options = new ControlAuthOptions
                {
                    RequireLocalApproval = true,
                    ApprovalWindow = TimeSpan.FromSeconds(15),
                    FailureDelayMin = TimeSpan.Zero,
                    FailureDelayMax = TimeSpan.Zero,
                },
            };
            context = context.WithDisplayCatalogSource(() =>
            {
                Interlocked.Increment(ref catalogCalls);
                return ValueTask.FromResult<IReadOnlyList<DisplayInfo>>(catalogDisplays);
            }, TimeSpan.FromSeconds(10));
            // 自动切屏协调：同一会话写者发目录与回执；prepare 只认 Transport 从登记表原样传入的会话身份。
            context = context.WithSessionDisplayRequestPrepare(
                (sessionId, expectedDisplay, token) => factory.CreateForDisplayAsync(sessionId, expectedDisplay, token));
            using TcpListener reservation = new(IPAddress.Loopback, 0);
            reservation.Start();
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            Assert.True(ConnectionTarget.TryCreate(remoteDeviceId, IPAddress.Loopback, port,
                Convert.ToHexString(SHA256.HashData(certificate.RawData)), out ConnectionTarget? target));
            host = TransportHost.CreateWithVideo([IPAddress.Loopback], policy, certificate, context,
                factory, new TransportHostOptions { Port = port, MaxConnectionsPerAddress = 2 });
            TransportHostStartResult started = host.Start();
            Assert.True(started.IsListening);
            Assert.Empty(started.Failures);
            Assert.Equal(IPAddress.Loopback, Assert.Single(started.BoundAddresses));

            owner = new Window
            {
                Width = 1, Height = 1, Left = -10000, Top = -10000,
                WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            };
            owner.Show();
            clientService = new ClientConnectService(identity, new ControlClientConnector());
            string enteredRemoteKey = CrockfordBase32.Encode(remoteKey);
            Assert.NotEqual(CrockfordBase32.Encode(localKey), enteredRemoteKey);
            client = clientService.ConnectAsync(target!, enteredRemoteKey, owner, externalCancellation.Token);

            LocalApprovalRequest pending = await gate.Received.WaitAsync(Guard);
            Assert.Equal(remoteDeviceId, context.ServerDeviceId);
            Assert.Equal(clientIdentity.DeviceId, pending.ClientDeviceId);
            Assert.Equal(SessionPermission.ViewOnly, pending.RequestedPermission);
            Assert.False(client.IsCompleted, "等待审批时产品客户端不得交付或结束连接。");
            Assert.Null(ReadAttemptWindow<RemoteViewWindow>(clientService, "View"));
            Assert.Empty(context.SessionRegistry.Snapshot());
            Assert.Equal(1, context.PendingApprovalLimiter.GlobalInUse);
            Assert.Equal(0, factory.Calls);
            Assert.Equal(0, Volatile.Read(ref catalogCalls));
            Assert.Equal(1, host.ActiveConnections);
            Assert.Equal(1, host.AdmittedConnections);
            RemoteConnectDialog dialog = Assert.IsType<RemoteConnectDialog>(
                ReadAttemptWindow<RemoteConnectDialog>(clientService, "Dialog"));
            await WaitOnUiAsync(() => ((TextBlock)dialog.FindName("StatusText")).Text.Contains(pending.ShortCode), client);
            Assert.Contains("尚未认证", ((TextBlock)dialog.FindName("StatusText")).Text);
            Assert.False(client.IsCompleted);

            // 测试专用 gate 没有自动批准：只有观察到真实 RequestId 后才显式授权仅查看。
            gate.ApproveViewOnly(pending.RequestId);
            SwitchFrameProducer producer = await factory.Created.WaitAsync(Guard);
            await producer.Waiting.WaitAsync(Guard);
            await WaitOnUiAsync(() => ReadAttemptWindow<RemoteViewWindow>(clientService, "View")?.VideoImage.Source
                is BitmapSource { IsFrozen: true, PixelWidth: 2, PixelHeight: 2 }, client);
            RemoteViewWindow view = Assert.IsType<RemoteViewWindow>(
                ReadAttemptWindow<RemoteViewWindow>(clientService, "View"));
            BitmapSource initial = Assert.IsAssignableFrom<BitmapSource>(view.VideoImage.Source);
            Assert.True(initial.IsFrozen);
            Assert.Equal(2, initial.PixelWidth);
            Assert.Equal(2, initial.PixelHeight);
            AssertBluePixels(initial);
            Assert.False(client.IsCompleted);
            Assert.Equal(pending.SessionId, factory.SessionId);
            Assert.Equal(1, factory.Calls);

            ItemsControl items = Assert.IsType<ItemsControl>(view.FindName("DisplayCatalogItems"));
            await WaitOnUiAsync(() => items.Items.Count == 2, client);
            Assert.NotNull(typeof(RemoteViewWindow).GetField("_readCatalog",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view));
            Assert.Equal(catalogDisplays, items.Items.Cast<DisplayInfo>());
            Assert.Contains("可请求切换", ((TextBlock)view.FindName("CatalogStatusText")).Text);
            Assert.Contains("回执不等同物理输出", ((TextBlock)view.FindName("CatalogStatusText")).Text);
            Assert.Equal(1, Volatile.Read(ref catalogCalls));
            Assert.Equal(0, factory.SwitchCalls);
            ControlSessionSummary session = Assert.Single(context.SessionRegistry.Snapshot());
            Assert.Equal(pending.SessionId, session.SessionId);
            Assert.Equal(SessionPermission.ViewOnly, session.GrantedPermission);
            Assert.Equal(clientIdentity.DeviceId, session.ClientDeviceId);
            Assert.Equal(2, host.ActiveConnections);
            Assert.Equal(2, host.AdmittedConnections);
            Assert.Equal(2, policy.Calls);

            if (rejectOnly)
            {
                // 目录外：窗口在发送前就按当前目录快照拒绝，不产生任何请求、生产者或会话影响。
                DisplayChangedFrame? outside = await view.RequestDisplaySwitchAsync(new DisplayId(5));
                Assert.Null(outside);
                Assert.Contains("当前不能请求切换屏幕", ((TextBlock)view.FindName("SwitchStatusText")).Text);
                Assert.False(client.IsCompleted);
                Assert.False(SessionEnded(view));
                Assert.Equal(0, factory.SwitchCalls);
                Assert.Equal(2, host.ActiveConnections);
                Assert.Single(context.SessionRegistry.Snapshot());

                // 目录外：Transport 会话本身也拒绝；同步失败不结束会话、不消耗请求序号。
                AuthenticatedControlSession control = ReadControlSession(clientService);
                Assert.Throws<ArgumentOutOfRangeException>(() => { _ = control.RequestDisplayAsync(new DisplayId(5)); });
                Assert.False(client.IsCompleted);
                Assert.False(SessionEnded(view));
                Assert.Equal(0, factory.SwitchCalls);
                Assert.Single(context.SessionRegistry.Snapshot());

                // 忙：第一个真实请求在途时，窗口立即拒绝第二次请求，不叠加发送。
                Task<DisplayChangedFrame?> inFlight = view.RequestDisplaySwitchAsync(new DisplayId(9));
                DisplayChangedFrame? busy = await view.RequestDisplaySwitchAsync(new DisplayId(4));
                Assert.Null(busy);
                Assert.Contains("当前不能请求切换屏幕", ((TextBlock)view.FindName("SwitchStatusText")).Text);
                DisplayChangedFrame? receipt = await inFlight.WaitAsync(Guard);
                AssertSwitchReceipt(receipt, pending, factory, view);
                Assert.Equal(1, factory.SwitchCalls);
                await WaitOnUiAsync(() => IsRedSource(view.VideoImage.Source, initial), client);
                AssertRedPixels(Assert.IsAssignableFrom<BitmapSource>(view.VideoImage.Source));
                Assert.False(client.IsCompleted);
                Assert.False(SessionEnded(view));
            }
            else
            {
                DisplayChangedFrame? receipt = await view.RequestDisplaySwitchAsync(new DisplayId(9));
                AssertSwitchReceipt(receipt, pending, factory, view);
                await WaitOnUiAsync(() => IsRedSource(view.VideoImage.Source, initial), client);
                BitmapSource switched = Assert.IsAssignableFrom<BitmapSource>(view.VideoImage.Source);
                Assert.False(ReferenceEquals(switched, initial), "切换后必须呈现新一代生产者的画面。");
                AssertRedPixels(switched);
                // 旧代际不得覆盖新画面：有界空转若干轮后仍为红。
                for (int round = 0; round < 50; round++)
                {
                    Assert.False(client.IsCompleted, "呈现新代际画面后客户端不得提前结束。");
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                }
                AssertRedPixels(Assert.IsAssignableFrom<BitmapSource>(view.VideoImage.Source));
                Assert.False(client.IsCompleted);
                Assert.False(SessionEnded(view));
            }

            Task pendingCleanup = Assert.IsAssignableFrom<Task>(clientService.LastCleanupTask);
            Assert.False(pendingCleanup.IsCompleted);
            Assert.False(HasDrainEvidenceFor(clientService, pendingCleanup));
            if (rejectOnly)
            {
                Assert.Same(pendingCleanup, clientService.StopAndDisposeAsync());
            }
            else
            {
                view.Close();
                AssertCatalogDetached(view);
            }
            Assert.True(view.IsVisible, "真实窗口不得在原连接与帧处理完成前立即关闭。");
            Assert.False(HasDrainEvidenceFor(clientService, pendingCleanup));
            try
            {
                AggregateException report = await Assert.ThrowsAsync<AggregateException>(() => client.WaitAsync(Guard));
                Assert.True(client.IsCompleted && client.IsFaulted,
                    "原客户端任务必须已排空并报告诊断，而不是仍在运行或静默成功。");
                Assert.Same(report, Assert.Single(client.Exception!.InnerExceptions));
                Assert.StartsWith("客户端资源排空发生错误。", report.Message);
                Assert.Contains(report.InnerExceptions, error => error is AggregateException drained &&
                    drained.Message.StartsWith("会话已排空，但存在操作或清理错误。", StringComparison.Ordinal));
                AggregateException roots = report.Flatten();
                // 不固定取消竞争的异常类型：SSL 读可能以 OCE 或 IO/Socket abort 报告同一个主动断开。
                Assert.Contains(roots.InnerExceptions, error => error is OperationCanceledException or IOException);
                Assert.True(roots.InnerExceptions.Count >= 1);
                Task originalCleanup = Assert.IsAssignableFrom<Task>(clientService.LastCleanupTask);
                Assert.True(originalCleanup.IsCompleted && originalCleanup.IsFaulted);
                Assert.Same(pendingCleanup, originalCleanup);
                Assert.False(clientService.IsFullyDrained);
                Assert.True(HasDrainEvidenceFor(clientService, originalCleanup));
                Assert.False(HasDrainEvidenceFor(clientService, client));
                observedCleanup = originalCleanup;
                observedClient = client;

                SwitchFrameProducer switched = await factory.Switched.WaitAsync(Guard);
                foreach (SwitchFrameProducer finished in new[] { producer, switched })
                {
                    await finished.Completion.WaitAsync(Guard);
                    Assert.Null(await finished.PendingRead!.WaitAsync(Guard));
                    Assert.True(finished.Completion.IsCompletedSuccessfully);
                    Assert.Equal(1, finished.Owner!.DisposeCount);
                    Assert.Equal(1, finished.Starts);
                    Assert.Equal(2, finished.Reads);
                }

                long drainedAt = Stopwatch.GetTimestamp();
                while (host.ActiveConnections != 0 || host.AdmittedConnections != 0 ||
                    context.SessionRegistry.Snapshot().Count != 0 || context.PendingApprovalLimiter.GlobalInUse != 0)
                {
                    Assert.True(Stopwatch.GetElapsedTime(drainedAt) < Guard,
                        "用户主动关闭后，Host 原连接必须在调用 Host.StopAsync 前自然排空。");
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                }
                Assert.True(host.IsRunning, "原连接排空时 Host 监听仍在运行，不能依赖 Host 停机造出零计数。");
                const string safeStatus = "远端会话已结束，操作诊断已保留；客户端资源已排空。";
                Assert.Equal(safeStatus, clientService.LastStatus);
                Assert.Equal(safeStatus, ((TextBlock)view.FindName("StatusText")).Text);
                Assert.Null(view.VideoImage.Source);
                AssertCatalogDetached(view);
                Assert.True(view.IsVisible, "原诊断保留在窗口中，供用户主动二次关闭。");
                Button closeButton = Assert.IsType<Button>(view.FindName("DisconnectButton"));
                Assert.Equal("关闭窗口", closeButton.Content);
                Assert.True(closeButton.IsEnabled);
                closeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(view.IsVisible);
            }
            finally
            {
                if (client.IsCompleted) observedClient = client;
            }
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            gate.ReleasePending();
            if (clientService is not null)
            {
                Task cleanup = clientService.StopAndDisposeAsync();
                if (ReferenceEquals(cleanup, observedCleanup))
                {
                    // 原 Task 已在主体断言过；重复观察不把同一原诊断算作第二次清理失败。
                    Assert.True(cleanup.IsCompleted && cleanup.IsFaulted);
                    _ = cleanup.Exception;
                }
                else
                {
                    await CleanupAsync(cleanupErrors, () => cleanup.WaitAsync(Guard));
                }
            }
            if (client is not null)
            {
                if (ReferenceEquals(client, observedClient))
                    _ = client.Exception; // 原任务已完成并观察，不能把同一诊断重复计为清理失败。
                else
                    await CleanupAsync(cleanupErrors, async () => await client.WaitAsync(Guard));
            }
            if (owner is not null)
                await CleanupAsync(cleanupErrors, () => { if (owner.IsLoaded) owner.Close(); return Task.CompletedTask; });
            if (host is not null)
            {
                await CleanupAsync(cleanupErrors, async () =>
                {
                    TransportHostStopReport report = await host.StopAsync(Guard);
                    Assert.True(report.AllFinished, "Host 必须 join 原 accept 与双 TLS 连接任务。");
                });
                await CleanupAsync(cleanupErrors, () => host.DisposeAsync().AsTask());
            }
            if (factory is not null)
            {
                foreach (SwitchFrameProducer? finished in new[] { factory.Produced, factory.SwitchedProducer })
                {
                    if (finished is null) continue;
                    await CleanupAsync(cleanupErrors, async () => await finished.Completion);
                    if (finished.PendingRead is { } originalRead)
                        await CleanupAsync(cleanupErrors, async () => Assert.Null(await originalRead));
                    await CleanupAsync(cleanupErrors, () =>
                    {
                        Assert.Equal(1, finished.Owner!.DisposeCount);
                        Assert.Equal(1, finished.Starts);
                        Assert.Equal(2, finished.Reads);
                        Assert.True(finished.Completion.IsCompletedSuccessfully);
                        return Task.CompletedTask;
                    });
                }
            }
            if (host is not null && context is not null)
                await CleanupAsync(cleanupErrors, () =>
                {
                    Assert.Equal(0, host.ActiveConnections);
                    Assert.Equal(0, host.AdmittedConnections);
                    Assert.Empty(context.SessionRegistry.Snapshot());
                    Assert.Equal(0, context.PendingApprovalLimiter.GlobalInUse);
                    return Task.CompletedTask;
                });
            if (localCertificates is not null)
                await CleanupAsync(cleanupErrors, () => { localCertificates.Dispose(); return Task.CompletedTask; });
            if (certificate is not null)
                await CleanupAsync(cleanupErrors, () => { certificate.Dispose(); return Task.CompletedTask; });
            CryptographicOperations.ZeroMemory(remoteKey);
            CryptographicOperations.ZeroMemory(localKey);
            if (Directory.Exists(root))
                await CleanupAsync(cleanupErrors, () => { Directory.Delete(root, recursive: true); return Task.CompletedTask; });
        }

        if (cleanupErrors.Count != 0)
            throw failure is null ? new AggregateException("切屏回环测试清理失败。", cleanupErrors)
                : new AggregateException("切屏回环测试及清理失败。", [failure, .. cleanupErrors]);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void AssertSwitchReceipt(DisplayChangedFrame? receipt, LocalApprovalRequest pending,
        SwitchLoopbackFactory factory, RemoteViewWindow view)
    {
        Assert.NotNull(receipt);
        Assert.Equal(new DisplayId(9), receipt.DisplayId);
        Assert.Equal(2, receipt.Width);
        Assert.Equal(2, receipt.Height);
        // 回执与准备工厂都必须绑定本 Host 实际登记的认证会话身份，拒绝共享或伪造标识。
        Assert.Equal(pending.SessionId, receipt.SessionId);
        Assert.Equal(pending.SessionId, factory.SwitchSessionId);
        Assert.NotNull(factory.SwitchTarget);
        Assert.Equal(new DisplayId(9), factory.SwitchTarget!.Id);
        Assert.Equal("左侧屏幕 <不解析为标记>", factory.SwitchTarget!.Name);
        Assert.Contains("已收到切换回执", ((TextBlock)view.FindName("SwitchStatusText")).Text);
        Assert.Contains("回执不代表物理输出", ((TextBlock)view.FindName("SwitchStatusText")).Text);
    }

    private static bool SessionEnded(RemoteViewWindow view) =>
        (bool)typeof(RemoteViewWindow).GetField("_sessionEnded",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;

    private static AuthenticatedControlSession ReadControlSession(ClientConnectService service)
    {
        object attempt = typeof(ClientConnectService)
            .GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        return (AuthenticatedControlSession)attempt.GetType().GetProperty("Control",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(attempt)!;
    }

    private static bool IsRedSource(object? source, BitmapSource initial) =>
        source is BitmapSource bitmap && !ReferenceEquals(bitmap, initial) && bitmap.IsFrozen
        && bitmap.PixelWidth == 2 && bitmap.PixelHeight == 2 && HasRedPixels(bitmap);

    private static bool HasRedPixels(BitmapSource bitmap)
    {
        FormatConvertedBitmap converted = new(bitmap, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[2 * 2 * 4];
        converted.CopyPixels(pixels, 2 * 4, 0);
        for (int i = 0; i < pixels.Length; i += 4)
            if (pixels[i] > 180 || pixels[i + 2] < 150 || pixels[i + 3] != 255)
                return false;
        return true;
    }

    private static bool HasDrainEvidenceFor(ClientConnectService service, Task originalStop) =>
        Assert.IsType<bool>(typeof(ClientConnectService).GetMethod("HasSuccessfulDrainEvidenceFor",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [originalStop]));

    private static T? ReadAttemptWindow<T>(ClientConnectService service, string propertyName) where T : Window
    {
        object? attempt = typeof(ClientConnectService)
            .GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service);
        return (T?)attempt?.GetType().GetProperty(propertyName,
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(attempt);
    }

    private static async Task WaitOnUiAsync(Func<bool> condition, Task connection)
    {
        long start = Stopwatch.GetTimestamp();
        while (!condition())
        {
            Assert.False(connection.IsCompleted, $"客户端在收到画面前提前结束：{connection.Exception}");
            Assert.True(Stopwatch.GetElapsedTime(start) < Guard, "产品窗口没有在 Guard 内呈现真实视频帧。");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
    }

    private static async Task CleanupAsync(List<Exception> errors, Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception error) { errors.Add(error); }
    }

    private static void AssertCatalogDetached(RemoteViewWindow view)
    {
        Assert.Null(((ItemsControl)view.FindName("DisplayCatalogItems")).ItemsSource);
        Assert.Empty(((ItemsControl)view.FindName("DisplayCatalogItems")).Items);
        foreach (string name in new[] { "_catalogTimer", "_readCatalog", "_displayCatalog" })
            Assert.Null(typeof(RemoteViewWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view));
        Assert.Equal(default, (CancellationToken)typeof(RemoteViewWindow)
            .GetField("_catalogStop", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!);
        Assert.True((bool)typeof(RemoteViewWindow)
            .GetField("_catalogStopped", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!);
        Assert.True((bool)typeof(RemoteViewWindow)
            .GetField("_switchStopped", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!);
        Assert.Null(typeof(RemoteViewWindow)
            .GetField("_requestSwitch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view));
    }

    private static void AssertBluePixels(BitmapSource bitmap)
    {
        FormatConvertedBitmap converted = new(bitmap, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[2 * 2 * 4];
        converted.CopyPixels(pixels, 2 * 4, 0);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            Assert.InRange(pixels[i], (byte)210, (byte)255);
            Assert.InRange(pixels[i + 1], (byte)125, (byte)175);
            Assert.InRange(pixels[i + 2], (byte)75, (byte)125);
            Assert.Equal((byte)255, pixels[i + 3]);
        }
    }

    // IndianRed(205,92,92)：B/G 与蓝色 B∈[210,255] 不相交，R 与蓝色 R∈[75,125] 不相交。
    private static void AssertRedPixels(BitmapSource bitmap)
    {
        FormatConvertedBitmap converted = new(bitmap, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[2 * 2 * 4];
        converted.CopyPixels(pixels, 2 * 4, 0);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            Assert.InRange(pixels[i], (byte)40, (byte)160);
            Assert.InRange(pixels[i + 1], (byte)40, (byte)160);
            Assert.InRange(pixels[i + 2], (byte)160, (byte)255);
            Assert.Equal((byte)255, pixels[i + 3]);
        }
    }

    private static byte[] EncodeTwoPixelJpeg(Brush fill)
    {
        RenderTargetBitmap target = new(2, 2, 96, 96, PixelFormats.Pbgra32);
        DrawingVisual visual = new();
        using (DrawingContext drawing = visual.RenderOpen())
            drawing.DrawRectangle(fill, null, new Rect(0, 0, 2, 2));
        target.Render(visual);
        JpegBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using MemoryStream stream = new();
        encoder.Save(stream);
        byte[] jpeg = stream.ToArray();
        int offset = 2;
        Assert.Equal((byte)0xFF, jpeg[0]);
        Assert.Equal((byte)0xD8, jpeg[1]);
        while (offset + 4 <= jpeg.Length)
        {
            Assert.Equal((byte)0xFF, jpeg[offset++]);
            while (jpeg[offset] == 0xFF) offset++;
            byte marker = jpeg[offset++];
            Assert.NotEqual((byte)0xDA, marker);
            int length = (jpeg[offset] << 8) | jpeg[offset + 1];
            Assert.InRange(length, 2, jpeg.Length - offset);
            if (marker == 0xC0)
            {
                Assert.Equal(2, (jpeg[offset + 3] << 8) | jpeg[offset + 4]);
                Assert.Equal(2, (jpeg[offset + 5] << 8) | jpeg[offset + 6]);
                return jpeg;
            }
            offset += length;
        }
        throw new InvalidDataException("WPF JPEG 编码器未生成 2x2 baseline SOF0。");
    }

    private static X509Certificate2 CreateServerCertificate()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new("CN=lanremote-product-switch-loopback-test", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new(PeerCertificateValidator.ServerAuthEkuOid) }, false));
        using X509Certificate2 signed = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        byte[] pfx = signed.Export(X509ContentType.Pfx, password);
        try { return X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.DefaultKeySet); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    private sealed class LoopbackPolicy : ISubnetPolicy
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        public bool IsAllowedPeer(IPAddress localAddress, IPAddress remoteAddress)
        {
            Interlocked.Increment(ref _calls);
            return IPAddress.Loopback.Equals(localAddress) && IPAddress.Loopback.Equals(remoteAddress);
        }
    }

    private sealed class FixedRemoteSecretStore(byte[] remoteKey) : IAccessSecretStore
    {
        public Task<AccessSecret> LoadOrCreateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AccessSecret(remoteKey.ToArray()));
        }

        public Task<AccessSecret> RegenerateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("测试远端访问密钥不得轮换。");
    }

    // 只测产品客户端和真实 Transport；此 gate 不代表 WpfLocalApprovalGate 的实际按钮 UI。
    private sealed class ManualApprovalGate : ILocalApprovalGate
    {
        private readonly TaskCompletionSource<LocalApprovalRequest> _received =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<LocalApprovalDecision> _decision =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<LocalApprovalRequest> Received => _received.Task;

        public async ValueTask<LocalApprovalDecision> RequestApprovalAsync(
            LocalApprovalRequest request, CancellationToken cancellationToken)
        {
            if (!_received.TrySetResult(request))
                throw new InvalidOperationException("本测试只允许一条真实本机审批请求。");
            return await _decision.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        internal void ApproveViewOnly(Guid requestId)
        {
            if (!_received.Task.IsCompletedSuccessfully || _received.Task.Result.RequestId != requestId)
                throw new InvalidOperationException("必须先按真实 RequestId 观察待批请求。");
            if (!_decision.TrySetResult(new LocalApprovalDecision(requestId,
                LocalApprovalOutcome.Approved, SessionPermission.ViewOnly)))
                throw new InvalidOperationException("审批只能显式提交一次。");
        }

        internal void ReleasePending()
        {
            if (_received.Task.IsCompletedSuccessfully)
                _decision.TrySetResult(new LocalApprovalDecision(_received.Task.Result.RequestId,
                    LocalApprovalOutcome.Denied));
        }
    }

    // 初始主屏与切屏目标各一个专用生产者；切屏准备只允许一次，并记录 Transport 原样传入的会话身份与目标。
    private sealed class SwitchLoopbackFactory(byte[] initialJpeg, byte[] switchedJpeg) : IVideoFrameProducerFactory
    {
        private readonly TaskCompletionSource<SwitchFrameProducer> _created =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<SwitchFrameProducer> _switched =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        private int _switchCalls;
        internal Task<SwitchFrameProducer> Created => _created.Task;
        internal Task<SwitchFrameProducer> Switched => _switched.Task;
        internal SwitchFrameProducer? Produced => _created.Task.IsCompletedSuccessfully ? _created.Task.Result : null;
        internal SwitchFrameProducer? SwitchedProducer => _switched.Task.IsCompletedSuccessfully ? _switched.Task.Result : null;
        internal int Calls => Volatile.Read(ref _calls);
        internal int SwitchCalls => Volatile.Read(ref _switchCalls);
        internal Guid SessionId { get; private set; }
        internal Guid SwitchSessionId { get; private set; }
        internal DisplayInfo? SwitchTarget { get; private set; }

        public ValueTask<IVideoFrameProducer> CreateAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _calls) != 1)
                throw new InvalidOperationException("一次视频 attach 只能创建一个生产者。");
            SessionId = sessionId;
            SwitchFrameProducer producer = new(initialJpeg);
            _created.TrySetResult(producer);
            return ValueTask.FromResult<IVideoFrameProducer>(producer);
        }

        internal Task<IVideoFrameProducer> CreateForDisplayAsync(Guid sessionId, DisplayInfo target,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _switchCalls) != 1)
                throw new InvalidOperationException("一次切屏只能准备一个目标生产者。");
            SwitchSessionId = sessionId;
            SwitchTarget = target;
            SwitchFrameProducer producer = new(switchedJpeg);
            _switched.TrySetResult(producer);
            return Task.FromResult<IVideoFrameProducer>(producer);
        }
    }

    private sealed class SwitchFrameProducer(byte[] jpeg) : IVideoFrameProducer
    {
        private readonly object _sync = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _stop;
        private int _reads;
        private int _starts;
        internal CountingOwner? Owner { get; private set; }
        internal Task<EncodedFrame?>? PendingRead { get; private set; }
        internal Task Waiting => _waiting.Task;
        internal int Reads => Volatile.Read(ref _reads);
        internal int Starts => Volatile.Read(ref _starts);
        public Task Completion => _completed.Task;

        public void Start()
        {
            lock (_sync)
            {
                if (_stop is not null || Interlocked.Increment(ref _starts) != 1)
                    throw new InvalidOperationException("生产者已经启动或停止。");
            }
        }

        public ValueTask<EncodedFrame?> ReadNextAsync(CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                if (_starts != 1 || _stop is not null)
                    throw new InvalidOperationException("生产者未运行。");
                int number = Interlocked.Increment(ref _reads);
                if (number == 1)
                {
                    Owner = new CountingOwner(jpeg);
                    return ValueTask.FromResult<EncodedFrame?>(
                        new EncodedFrame(VideoCodec.Jpeg, 2, 2, 1, 1, 80, Owner));
                }
                if (number != 2)
                    throw new InvalidOperationException("只有首帧和随后持续等待的原读取。");
                PendingRead = HoldUntilStopAsync(cancellationToken);
                _waiting.TrySetResult();
                return new ValueTask<EncodedFrame?>(PendingRead);
            }
        }

        private async Task<EncodedFrame?> HoldUntilStopAsync(CancellationToken cancellationToken)
        {
            try { await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            return null;
        }

        public Task StopAsync()
        {
            lock (_sync) return _stop ??= StopCoreAsync();
        }

        private async Task StopCoreAsync()
        {
            _release.TrySetResult();
            if (PendingRead is { } originalRead) await originalRead.ConfigureAwait(false);
            _completed.TrySetResult();
        }

        public ValueTask DisposeAsync() => new(StopAsync());
    }

    private sealed class CountingOwner(byte[] bytes) : IMemoryOwner<byte>
    {
        private byte[]? _bytes = bytes.ToArray();
        private int _disposeCount;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        public Memory<byte> Memory => Volatile.Read(ref _bytes)
            ?? throw new ObjectDisposedException(nameof(CountingOwner));
        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            Interlocked.Exchange(ref _bytes, null);
        }
    }
}
