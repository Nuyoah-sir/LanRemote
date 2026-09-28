using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using LanRemote.Transport;

namespace LanRemote.Acceptance.Tests;

[Collection(AcceptanceStageFiveCollection.Name)]
public sealed class AcceptanceTlsIntegrationTests
{
    [Theory(Timeout = 20_000)]
    [InlineData(false)]
    [InlineData(true)]
    public Task Approved_Downgrade_Requires_Observed_Hold_And_Natural_Release(bool hostForced) =>
        RunScenarioAsync(async scenario =>
        {
            Task<AuthenticatedControlSession> connecting = scenario.Connect();
            LocalApprovalSnapshot request = await scenario.AssertPendingAsync(connecting);
            Assert.True(scenario.Inbox.TrySubmit(request.RequestId, request.Generation,
                LocalApprovalOutcome.Approved, SessionPermission.ViewOnly));

            // Connect 返回意味着客户端已读完终帧并验证 serverProof，不以 Host 写帧代替。
            AuthenticatedControlSession client = await connecting.WaitAsync(scenario.Token);
            Assert.Equal(request.Request.SessionId, client.SessionId);
            Assert.Equal(request.Request.ShortCode, client.ShortCode);
            Assert.Equal(SessionPermission.ViewOnly, client.GrantedPermission);
            Assert.Equal(scenario.ServerDeviceId, client.Identity.DeviceId);
            Assert.True(client.Identity.PinsMatch);
            Assert.Equal(scenario.Target.ExpectedCertSha256.ToArray(), client.Identity.PresentedCertSha256.ToArray());

            scenario.BeginClientHold();
            ControlSessionSummary registered = await scenario.Registered.Task.WaitAsync(scenario.Token);
            Assert.Equal(client.SessionId, registered.SessionId);
            Assert.Equal(request.Request.ConnectionId, registered.ConnectionId);
            Assert.Equal(scenario.ClientDeviceId, registered.ClientDeviceId);
            Assert.Equal(SessionPermission.ViewOnly, registered.GrantedPermission);
            Assert.Equal(IPAddress.Loopback, registered.RemoteAddress);
            TimeSpan observedSpan = await scenario.Held.Task.WaitAsync(scenario.Token);
            Assert.True(observedSpan >= TimeSpan.FromSeconds(4));
            Assert.False(scenario.HandlerFinished.Task.IsCompleted);
            Assert.Equal(1, scenario.Context.SessionRegistry.ActiveSessionCount);
            Assert.Equal(1, scenario.Host.ActiveConnections);
            Assert.Equal(1, scenario.Counters.Snapshot().Active);
            Assert.Equal(0, scenario.Context.PendingApprovalLimiter.GlobalInUse);

            if (hostForced)
            {
                ControlSessionSummary stopping = Assert.Single(await scenario.StopHostAsync());
                Assert.Equal(client.SessionId, stopping.SessionId);
                // 直到真实 Host Stop 已完成仍由客户端持有会话，不能冒充客户端先释放。
                client.Dispose();
            }
            else
            {
                client.Dispose();
                await scenario.HandlerFinished.Task.WaitAsync(scenario.Token);
                Assert.Empty(await scenario.StopHostAsync());
            }

            await scenario.AssertCleanAsync(hostForced
                ? "authenticated-host-forced-close"
                : "authenticated-ended-unregistered");
            AcceptanceOutcome expected = hostForced ? AcceptanceOutcome.PreconditionUnmet : AcceptanceOutcome.Pass;
            Assert.Equal(expected, scenario.Evaluate());
            Assert.Equal(expected, scenario.Run.Complete(scenario.Evaluate(), "真实 TLS 会话证据"));

            string line = Assert.Single(scenario.Run.Log.ReadFrom(0, 100),
                value => value.StartsWith("[HOST][SESSION] ", StringComparison.Ordinal));
            Assert.Contains($"sessionId={client.SessionId}", line);
            Assert.Contains("authenticated=True ", line);
            Assert.Contains("deregisteredAtRunEnd=True ", line);
            Assert.Contains($"hostForcedClose={hostForced} ", line);
            Assert.Contains($"evidence={expected.Code()} ", line);
            Assert.True(ReadMeasurement(line, "observedSpanMs") >= 4000,
                "不能靠保持计时冒充真实 registry 采样跨度。");
            Assert.True(ReadMeasurement(line, "observedSamples") >= 2);
            Assert.InRange(ReadMeasurement(line, "maxObservedGapMs"), 0, 500);
            Assert.InRange(ReadMeasurement(line, "endObservationGapMs"), 0, 500);
            Assert.Contains("observationInterrupted=False ", line);
        });

    [Theory(Timeout = 20_000)]
    [InlineData(false)]
    [InlineData(true)]
    public Task Wrong_Key_Or_Explicit_Denial_Never_Registers_And_Consumes_One_Terminal_Bucket(bool deny) =>
        RunScenarioAsync(async scenario =>
        {
            Task<AuthenticatedControlSession> connecting = scenario.Connect(wrongKey: !deny);
            if (deny)
            {
                LocalApprovalSnapshot request = await scenario.AssertPendingAsync(connecting);
                Assert.True(scenario.Inbox.TrySubmit(request.RequestId, request.Generation, LocalApprovalOutcome.Denied));
                Assert.False(scenario.Inbox.TrySubmit(request.RequestId, request.Generation,
                    LocalApprovalOutcome.Approved, SessionPermission.Control));
            }

            // 先观察客户端消费 authentication_failed；先停 Host 会把拒绝帧抢成 EOF。
            ControlClientAuthenticationException error = await Assert.ThrowsAsync<ControlClientAuthenticationException>(
                () => connecting.WaitAsync(scenario.Token));
            Assert.Equal("client-remote-authentication-failed", error.Rejection);
            Assert.Equal("远端拒绝了认证或审批请求。", error.DisplayMessage);
            await scenario.HandlerFinished.Task.WaitAsync(scenario.Token);
            Assert.Empty(await scenario.StopHostAsync());
            await scenario.AssertCleanAsync("auth-rejected:" + (deny
                ? ControlAuthSession.RejectApprovalDenied : ControlAuthSession.RejectProofMismatch));
            Assert.Equal(deny ? 1 : 0, scenario.PendingNotifications);
            Assert.Equal(deny, scenario.InboxObserved.Task.IsCompletedSuccessfully);
            Assert.False(scenario.Registered.Task.IsCompleted);
            Assert.Equal(0, scenario.MaximumRegistered);
            Assert.Equal(deny ? 0 : 1, scenario.Context.FailedAuthLimiter.CountRecentFailures(IPAddress.Loopback));
            Assert.Equal(AcceptanceOutcome.PreconditionUnmet, scenario.Evaluate());
            Assert.DoesNotContain("evidence=PASS ", scenario.Run.Log.All);
        });

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public Task Offscreen_Stop_Or_Close_Joins_Authenticated_Tls_Role_And_Settles_Once(bool close) =>
        RunWindowScenarioAsync(close, holdCancellation: false);

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public Task Reap_Preserves_Completed_Tls_Role_Until_Cancellation_Callback_Exits(bool close) =>
        RunWindowScenarioAsync(close, holdCancellation: true);

    private static async Task RunWindowScenarioAsync(bool close, bool holdCancellation)
    {
        using AcceptanceTestDirectory directory = new();
        using RandomSecretStore store = new();
        using X509Certificate2 certificate = CreateCertificate();
        await using StaDispatcherFixture sta = new();
        Dispatcher dispatcher = await sta.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        await dispatcher.InvokeAsync(async () =>
        {
            MainWindow window = new(directory.Path);
            Field<DispatcherTimer>(window, "_uiTimer").Stop();
            bool closed = false;
            System.ComponentModel.CancelEventArgs? closing = null;
            window.Closing += (_, args) => closing = args;
            window.Closed += (_, _) => closed = true;
            MainWindow.RunState? state = null;
            TlsScenario? scenario = null;
            Task<AcceptanceOutcome>? worker = null;
            CancellationTokenRegistration registration = default;
            using ManualResetEventSlim releaseCallback = new(!holdCancellation);
            TaskCompletionSource<int> callbackEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource callbackExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<int> workerStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                Assert.IsType<DispatcherSynchronizationContext>(SynchronizationContext.Current);
                int uiThread = Environment.CurrentManagedThreadId;
                AssertNoNativeWindow(window);
                Assert.False(Field<bool>(window, "_ready"));
                Assert.Null(Field<Task?>(window, "_busyTask"));
                Assert.Null(Field<Task?>(window, "_keyTask"));
                Assert.Equal(directory.Path, Field<string>(window, "_logDirectory"));
                AcceptanceLog processLog = Field<AcceptanceLog>(window, "_processLog");
                Assert.Equal(Path.Combine(directory.Path, "gui.log"), processLog.FilePath);
                Assert.False(processLog.FileUnavailable);
                state = Assert.IsType<MainWindow.RunState>(Invoke(window, "StartRole", true));
                CancellationToken roleToken = state.Cts.Token;
                scenario = new TlsScenario(directory.Path, store, certificate, state.Run, state.Inbox);
                Assert.Same(state.Run, scenario.Run);
                Assert.Same(state.Inbox, scenario.Inbox);
                Assert.Equal(directory.Path, Path.GetDirectoryName(state.Run.Log.FilePath));
                Assert.Null(state.GetHostContext());

                // 先登记闸门，再由角色登记令牌等待；CancelAsync 按后进先出先唤醒角色。
                // 回调不依赖 Dispatcher，且即使测试失败也最多阻塞 5 秒。
                registration = roleToken.UnsafeRegister(_ =>
                {
                    callbackEntered.TrySetResult(Environment.CurrentManagedThreadId);
                    try
                    {
                        if (!releaseCallback.Wait(TimeSpan.FromSeconds(5)))
                            throw new TimeoutException("测试取消回调闸门未及时释放。");
                    }
                    finally { callbackExited.TrySetResult(); }
                }, null);
                Func<Task<AcceptanceOutcome>> role = async () =>
                {
                    Assert.Null(SynchronizationContext.Current);
                    try
                    {
                        scenario.Start();
                        using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(
                            roleToken, scenario.Token);
                        Task stopped = Task.Delay(Timeout.InfiniteTimeSpan, waiting.Token);
                        workerStarted.TrySetResult(Environment.CurrentManagedThreadId);
                        try { await stopped.ConfigureAwait(false); }
                        catch (OperationCanceledException) when (roleToken.IsCancellationRequested) { }
                    }
                    finally
                    {
                        await scenario.StopHostAsync().ConfigureAwait(false);
                        await scenario.AssertCleanAsync("authenticated-host-forced-close").ConfigureAwait(false);
                    }
                    return scenario.Run.Complete(scenario.Evaluate(), "真实 TLS 角色已停止 Host 并核对资源归零。");
                };
                MethodInfo executor = typeof(MainWindow).GetMethod("RunRoleWorkerAsync",
                    BindingFlags.Static | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException(typeof(MainWindow).FullName, "RunRoleWorkerAsync");
                // 与真实按钮的执行器相同；不运行会触碰用户身份/网卡的 HostRole.RunAsync。
                worker = Task.Run(() => (Task<AcceptanceOutcome>)executor.Invoke(null, new object[] { state, role })!);
                PrivateField("_runTask").SetValue(window, worker);
                Assert.NotEqual(uiThread, await workerStarted.Task.WaitAsync(scenario.Token));
                Assert.Same(worker, Field<Task?>(window, "_runTask"));
                Assert.False(worker.IsCompleted);

                Task<AuthenticatedControlSession> connecting = scenario.Connect();
                LocalApprovalSnapshot request = await scenario.AssertPendingAsync(connecting);
                Invoke(window, "RefreshApprovalList");
                ListBox approvals = Assert.IsType<ListBox>(window.FindName("ApprovalList"));
                approvals.SelectedItem = Assert.Single(approvals.Items.Cast<LocalApprovalSnapshot>());
                Assert.Equal(request, approvals.SelectedItem);
                Button approve = Assert.IsType<Button>(window.FindName("ViewOnlyButton"));
                Assert.True(approve.IsEnabled);
                approve.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, approve));
                // 不用 using 提前释放客户端；直到 Host 首次停止、角色收尾和 UI 回收后仍由 scenario 持有。
                AuthenticatedControlSession client = await connecting.WaitAsync(scenario.Token);
                Assert.Equal(request.Request.SessionId, client.SessionId);
                Assert.Equal(request.Request.ShortCode, client.ShortCode);
                Assert.Equal(SessionPermission.ViewOnly, client.GrantedPermission);
                Assert.Equal(scenario.ServerDeviceId, client.Identity.DeviceId);
                Assert.True(client.Identity.PinsMatch);
                Assert.Equal(scenario.Target.ExpectedCertSha256.ToArray(), client.Identity.PresentedCertSha256.ToArray());
                ControlSessionSummary registered = await scenario.Registered.Task.WaitAsync(scenario.Token);
                Assert.Equal(client.SessionId, registered.SessionId);
                Assert.Equal(request.Request.ConnectionId, registered.ConnectionId);
                Assert.Equal(scenario.ClientDeviceId, registered.ClientDeviceId);
                Assert.Equal(SessionPermission.ViewOnly, registered.GrantedPermission);
                Assert.Equal(IPAddress.Loopback, registered.RemoteAddress);
                Assert.Equal(1, scenario.Context.SessionRegistry.ActiveSessionCount);
                Assert.Equal(registered, Assert.Single(scenario.Context.SessionRegistry.Snapshot()));
                Assert.Equal(1, scenario.Host.ActiveConnections);
                Assert.Equal(1, scenario.Host.AdmittedConnections);
                Assert.Equal(1, scenario.Counters.Snapshot().Active);
                Assert.Equal(0, scenario.Context.PendingApprovalLimiter.GlobalInUse);
                Assert.Empty(scenario.Inbox.GetSnapshot());
                Assert.False(scenario.HandlerFinished.Task.IsCompleted);
                Assert.False(state.IsStopped);
                Assert.False(state.Run.AbortedByOperator);
                Assert.False(roleToken.IsCancellationRequested);
                Assert.Null(Field<Task?>(window, "_runCancellationTask"));
                Assert.DoesNotContain(state.Run.Log.ReadFrom(0, int.MaxValue), line => line == "RUN COMPLETE");
                Invoke(window, "UpdateButtons");
                Button stop = Assert.IsType<Button>(window.FindName("StopHostButton"));
                Assert.True(stop.IsEnabled);
                if (close)
                {
                    window.Close();
                    Assert.NotNull(closing);
                    Assert.True(closing.Cancel);
                    Assert.True(Field<bool>(window, "_closing"));
                    Assert.False(window.IsEnabled);
                    Assert.False(closed);
                }
                else
                {
                    stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, stop));
                    Assert.False(Field<bool>(window, "_closing"));
                }
                Assert.True(state.IsStopped);
                Assert.True(state.Run.AbortedByOperator);
                Assert.True(roleToken.IsCancellationRequested);
                Assert.False(stop.IsEnabled);
                Task cancellation = Assert.IsAssignableFrom<Task>(Field<Task?>(window, "_runCancellationTask"));
                Assert.NotEqual(uiThread, await callbackEntered.Task.WaitAsync(scenario.Token));
                Assert.Equal(AcceptanceOutcome.InvalidRun, await worker.WaitAsync(scenario.Token));
                Assert.True(worker.IsCompletedSuccessfully);
                Assert.False(state.Run.HasBackgroundFaults, state.Run.BackgroundFaultSummary);
                // StopHostAsync 只返回缓存的首次报告，不能靠第二次 Stop/Dispose 洗白。
                Assert.Equal(registered, Assert.Single(await scenario.StopHostAsync()));
                await scenario.AssertCleanAsync("authenticated-host-forced-close");
                Assert.Equal(AcceptanceOutcome.PreconditionUnmet, scenario.Evaluate());
                string session = Assert.Single(state.Run.Log.ReadFrom(0, int.MaxValue),
                    line => line.StartsWith("[HOST][SESSION] ", StringComparison.Ordinal));
                Assert.Contains($"sessionId={client.SessionId} ", session);
                Assert.Contains($"connectionId={registered.ConnectionId} ", session);
                Assert.Contains("authenticated=True ", session);
                Assert.Contains("deregisteredAtRunEnd=True ", session);
                Assert.Contains("hostForcedClose=True ", session);
                Assert.Contains($"evidence={AcceptanceOutcome.PreconditionUnmet.Code()} ", session);
                AssertSingleInvalidSettlement(state.Run, close);
                AssertNoNativeWindow(window);

                if (holdCancellation)
                {
                    Assert.False(callbackExited.Task.IsCompleted);
                    Assert.False(cancellation.IsCompleted);
                    for (int i = 0; i < 2; i++)
                    {
                        Invoke(window, "ReapCompletedTasks");
                        Assert.Same(state, Field<MainWindow.RunState?>(window, "_activeRun"));
                        Assert.Same(worker, Field<Task?>(window, "_runTask"));
                        Assert.Same(cancellation, Field<Task?>(window, "_runCancellationTask"));
                        Assert.Same(state.Cts, Field<CancellationTokenSource?>(window, "_runCts"));
                        Assert.Equal(roleToken, state.Cts.Token); // 提前 Dispose CTS 会抛异常。
                        AssertRunSubscription(state, subscribed: true);
                        Assert.Equal(Visibility.Collapsed, Assert.IsType<Border>(window.FindName("ResultBanner")).Visibility);
                        Assert.False(closed);
                        Assert.False(callbackExited.Task.IsCompleted);
                        Assert.False(cancellation.IsCompleted);
                    }
                    releaseCallback.Set();
                }
                await cancellation.WaitAsync(scenario.Token);
                Assert.True(cancellation.IsCompletedSuccessfully);
                Assert.True(callbackExited.Task.IsCompletedSuccessfully);
                Invoke(window, "ReapCompletedTasks");
                AssertUiOwnershipReturned(window, state);
                string settledLog = state.Run.Log.All;
                Invoke(window, "ReapCompletedTasks");
                AssertUiOwnershipReturned(window, state);
                Assert.Equal(settledLog, state.Run.Log.All);
                AssertSingleInvalidSettlement(state.Run, close);
                Assert.False(state.Run.HasBackgroundFaults, state.Run.BackgroundFaultSummary);
                Assert.False(Field<bool>(window, "_faulted"));
                AssertNoNativeWindow(window);
                if (close)
                {
                    Assert.False(closed);
                    // 定时器停用，显式执行真实 tick 验证回收后自动允许关闭。
                    Invoke(window, "OnUiTick", window, EventArgs.Empty);
                    Assert.True(Field<bool>(window, "_allowClose"));
                    Assert.NotNull(closing);
                    Assert.False(closing.Cancel);
                    Assert.True(closed);
                }
                else
                {
                    Assert.False(closed);
                    Assert.True(window.IsEnabled);
                    Assert.False(Field<bool>(window, "_allowClose"));
                }
                Assert.False(Field<bool>(window, "_faulted"));
                Assert.False(Field<bool>(window, "_ready"));
                Assert.False(Field<DispatcherTimer>(window, "_uiTimer").IsEnabled);
                Assert.Equal(settledLog, state.Run.Log.All);
                AssertNoNativeWindow(window);
            }
            finally
            {
                // 无论断言/回调/角色哪一方失败，先放闸，再 join；不伪造完成任务、不摘除在途所有权。
                releaseCallback.Set();
                using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
                try
                {
                    if (worker is not null)
                    {
                        if (!worker.IsCompleted) { Invoke(window, "StopCurrentRun", "测试 finally 收回角色"); }
                        Task cancellation = Field<Task?>(window, "_runCancellationTask") ?? Task.CompletedTask;
                        await Task.WhenAll(worker, cancellation).WaitAsync(cleanup.Token);
                    }
                }
                finally
                {
                    try
                    {
                        await registration.DisposeAsync().AsTask().WaitAsync(cleanup.Token);
                        if (worker is not null) { Invoke(window, "ReapCompletedTasks"); }
                        else if (state is not null)
                        {
                            // 仅构造失败且尚未交付任何 worker 的路径由测试归还状态。
                            state.Dispose();
                            state.Cts.Dispose();
                            PrivateField("_activeRun").SetValue(window, null);
                            PrivateField("_runCts").SetValue(window, null);
                        }
                    }
                    finally
                    {
                        try { if (scenario is not null) { await scenario.DisposeAsync(); } }
                        finally
                        {
                            Field<DispatcherTimer>(window, "_uiTimer").Stop();
                            if (!closed) { window.Close(); }
                        }
                    }
                }
            }
        }).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static void AssertSingleInvalidSettlement(AcceptanceRun run, bool close)
    {
        string[] lines = run.Log.ReadFrom(0, int.MaxValue).ToArray();
        Assert.Equal("[RESULT] outcome   = INVALID_RUN", Assert.Single(lines,
            line => line.StartsWith("[RESULT] outcome", StringComparison.Ordinal)));
        Assert.Equal("[RESULT] aborted   = True", Assert.Single(lines,
            line => line.StartsWith("[RESULT] aborted", StringComparison.Ordinal)));
        Assert.Equal("[RESULT] detail    = 真实 TLS 角色已停止 Host 并核对资源归零。", Assert.Single(lines,
            line => line.StartsWith("[RESULT] detail", StringComparison.Ordinal)));
        string terminal = Assert.Single(lines, line => line.StartsWith("[HOST][RESULT] ", StringComparison.Ordinal));
        Assert.Contains("terminal=authenticated-host-forced-close ", terminal);
        Assert.True(Array.IndexOf(lines, terminal) < Array.FindIndex(lines,
            line => line.StartsWith("[RESULT] outcome", StringComparison.Ordinal)));
        Assert.Single(lines, line => line == "RUN COMPLETE");
        Assert.Contains(close ? "关闭窗口" : "UI 提前停止监听", Assert.Single(lines,
            line => line.StartsWith("[RUN] 操作员中止", StringComparison.Ordinal)));
        Assert.DoesNotContain(lines, line => line.Contains("[CORRECTION]", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("evidence=PASS ", StringComparison.Ordinal));
        Assert.Equal(lines, File.ReadAllLines(run.Log.FilePath!));
    }

    private static void AssertUiOwnershipReturned(MainWindow window, MainWindow.RunState state)
    {
        foreach (string name in new[] { "_activeRun", "_runTask", "_runCancellationTask", "_runCts",
                     "_busyTask", "_keyTask", "_keyWork", "_displayedApprovalOwner" })
        {
            Assert.Null(PrivateField(name).GetValue(window));
        }
        Assert.False(Field<bool>(window, "_busy"));
        Assert.Empty(Field<IReadOnlyList<LocalApprovalSnapshot>>(window, "_displayedApprovals"));
        Assert.Throws<ObjectDisposedException>(() => { _ = state.Cts.Token; });
        AssertRunSubscription(state, subscribed: false);
        Assert.Equal(Visibility.Visible, Assert.IsType<Border>(window.FindName("ResultBanner")).Visibility);
        Assert.Contains("INVALID_RUN", Assert.IsType<TextBlock>(window.FindName("ResultBannerText")).Text);
    }

    private static void AssertRunSubscription(MainWindow.RunState state, bool subscribed)
    {
        // RunState.Dispose 没有公开标志；核对真实日志订阅，不注入伪造的认证事件。
        FieldInfo field = typeof(AcceptanceLog).GetField("LineWritten", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(AcceptanceLog).FullName, "LineWritten");
        Delegate? handlers = (Delegate?)field.GetValue(state.Run.Log);
        Assert.Equal(subscribed, handlers?.GetInvocationList().Any(handler => ReferenceEquals(handler.Target, state)) == true);
    }

    private static void AssertNoNativeWindow(MainWindow window)
    {
        Assert.False(window.IsVisible);
        Assert.False(window.IsLoaded);
        Assert.Equal(IntPtr.Zero, new WindowInteropHelper(window).Handle);
        Assert.Null(PresentationSource.FromVisual(window));
    }

    private static FieldInfo PrivateField(string name) => typeof(MainWindow).GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingFieldException(typeof(MainWindow).FullName, name);

    private static T Field<T>(MainWindow window, string name) => (T)PrivateField(name).GetValue(window)!;

    private static object? Invoke(MainWindow window, string name, params object[] arguments) =>
        (typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainWindow).FullName, name)).Invoke(window, arguments);

    private static double ReadMeasurement(string line, string name)
    {
        string prefix = name + "=";
        string field = Assert.Single(line.Split(' '), value => value.StartsWith(prefix, StringComparison.Ordinal));
        return double.Parse(field[prefix.Length..], CultureInfo.InvariantCulture);
    }

    private static async Task RunScenarioAsync(Func<TlsScenario, Task> test)
    {
        using AcceptanceTestDirectory directory = new();
        using RandomSecretStore store = new();
        using X509Certificate2 certificate = CreateCertificate();
        await using TlsScenario scenario = new(directory.Path, store, certificate);
        scenario.Start();
        await test(scenario);
    }

    private static X509Certificate2 CreateCertificate()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new("CN=lanremote-acceptance-test", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new(PeerCertificateValidator.ServerAuthEkuOid) }, false));
        using X509Certificate2 selfSigned = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        byte[] pfx = selfSigned.Export(X509ContentType.Pfx, password);
        try
        {
            // Schannel 使用 PFX 往返后的私钥，不能直接使用 ephemeral 自签结果。
            return X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.DefaultKeySet);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    private sealed class RandomSecretStore : IAccessSecretStore, IDisposable
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(16);
        public byte[] CopyKey() => _key.ToArray();
        public Task<AccessSecret> LoadOrCreateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AccessSecret(CopyKey()));
        }
        public Task<AccessSecret> RegenerateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("测试不轮换密钥，也不访问真实存储。");
        public void Dispose() => CryptographicOperations.ZeroMemory(_key);
    }

    private sealed class LoopbackPolicy : ISubnetPolicy
    {
        public bool IsAllowedPeer(IPAddress localAddress, IPAddress remoteAddress) =>
            localAddress.Equals(IPAddress.Loopback) && remoteAddress.Equals(IPAddress.Loopback);
    }

    private sealed class TlsScenario : IAsyncDisposable
    {
        // 主流程最多 12 秒；finally 共享 5 秒预算，测试外层硬 guard 为 20 秒。
        private readonly CancellationTokenSource _operation = new(TimeSpan.FromSeconds(12));
        private readonly CancellationTokenSource _clientStop = new();
        private readonly CancellationTokenSource _samplingStop = new();
        private readonly RandomSecretStore _store;
        private readonly bool _ownsInbox;
        private readonly HostSessionEvidence _evidence = new(4);
        private readonly TransportTimeouts _timeouts = new(
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4));
        private readonly TaskCompletionSource<ControlClientApprovalPending> _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task<AuthenticatedControlSession>? _client;
        private Task _sampling = Task.CompletedTask;
        private Task<TransportHostStopReport>? _stop;
        private IReadOnlyList<ControlSessionSummary>? _stopSnapshot;
        private long _holdStarted;
        private int _entered;
        private int _pendingNotifications;
        private int _maximumRegistered;

        internal TlsScenario(string directory, RandomSecretStore store, X509Certificate2 certificate,
            AcceptanceRun? run = null, LocalApprovalInbox? inbox = null)
        {
            if ((run is null) != (inbox is null))
                throw new ArgumentException("外部 Run 与 Inbox 必须成对提供，且属于同一个 UI 角色。");
            _store = store;
            // 外部 Run 的结算由角色工作任务负责；外部 Inbox 只 Stop，由 UI 的 FinishRole 负责 Dispose。
            Run = run ?? AcceptanceRun.Create(directory, "tls-test");
            Inbox = inbox ?? new LocalApprovalInbox();
            _ownsInbox = inbox is null;
            Context = new ControlAuthContext
            {
                ServerDeviceId = ServerDeviceId,
                AccessSecretStore = store,
                FailedAuthLimiter = new FailedAuthLimiter(),
                PendingApprovalLimiter = new ConnectionAdmissionLimiter(3, 1),
                SessionRegistry = new SessionRegistry(),
                ApprovalGate = Inbox,
                Options = new ControlAuthOptions
                {
                    RequireLocalApproval = true,
                    MachineWindow = TimeSpan.FromSeconds(3),
                    ApprovalWindow = TimeSpan.FromSeconds(4),
                    FailureDelayMin = TimeSpan.Zero,
                    FailureDelayMax = TimeSpan.Zero,
                },
            };
            Counters = new HostRole.HostCounters(Context, _evidence);
            using TcpListener reservation = new(IPAddress.Loopback, 0);
            reservation.Start();
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            Assert.True(ConnectionTarget.TryCreate(ServerDeviceId, IPAddress.Loopback, port,
                Convert.ToHexString(SHA256.HashData(certificate.RawData)), out ConnectionTarget? target));
            Target = target!;
            Host = new TransportHost(new[] { IPAddress.Loopback }, new LoopbackPolicy(), certificate,
                HandleAsync, new TransportHostOptions
                {
                    Port = port, Timeouts = _timeouts, MaxConnections = 4,
                    ShutdownTimeout = TimeSpan.FromSeconds(1),
                });
        }

        internal Guid ServerDeviceId { get; } = Guid.NewGuid();
        internal Guid ClientDeviceId { get; } = Guid.NewGuid();
        internal AcceptanceRun Run { get; }
        internal LocalApprovalInbox Inbox { get; }
        internal ControlAuthContext Context { get; }
        internal HostRole.HostCounters Counters { get; }
        internal TransportHost Host { get; }
        internal ConnectionTarget Target { get; }
        internal CancellationToken Token => _operation.Token;
        internal int PendingNotifications => Volatile.Read(ref _pendingNotifications);
        internal int MaximumRegistered => Volatile.Read(ref _maximumRegistered);
        internal TaskCompletionSource HandlerFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<LocalApprovalSnapshot> InboxObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<ControlSessionSummary> Registered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<TimeSpan> Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Start()
        {
            Assert.False(Run.Log.FileUnavailable);
            TransportHostStartResult start = Host.Start();
            Assert.True(start.IsListening);
            Assert.Empty(start.Failures);
            Assert.Equal(IPAddress.Loopback, Assert.Single(start.BoundAddresses));
            _sampling = SampleAsync();
        }

        private async Task HandleAsync(AcceptedConnection connection, CancellationToken cancellationToken)
        {
            Interlocked.Exchange(ref _entered, 1);
            try
            {
                Assert.True(connection.Stream.IsAuthenticated);
                Assert.True(connection.Stream.IsEncrypted);
                Assert.Equal(IPAddress.Loopback, connection.RemoteAddress);
                // 唯一真实处理链；这里不重写 pre-auth、认证、登记或结束桶。
                await Counters.HandleAsync(Run, connection, _timeouts, cancellationToken);
                HandlerFinished.TrySetResult();
            }
            catch (Exception error)
            {
                // Transport 会收住 handler 异常；独立信号保证断言仍被主测试/finally 观察。
                HandlerFinished.TrySetException(error);
                throw;
            }
        }

        internal Task<AuthenticatedControlSession> Connect(bool wrongKey = false)
        {
            Assert.Null(_client);
            _client = ConnectCoreAsync(wrongKey);
            return _client;
        }

        private async Task<AuthenticatedControlSession> ConnectCoreAsync(bool wrongKey)
        {
            byte[] key = _store.CopyKey();
            if (wrongKey) { key[0] ^= 0x80; }
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(Token, _clientStop.Token);
            try
            {
                return await new ControlClientConnector().ConnectAndAuthenticateAsync(
                    Target, ClientDeviceId, "阶段5真实 TLS 客户端", key, SessionPermission.Control,
                    new ControlClientAuthOptions
                    {
                        MachineWindow = TimeSpan.FromSeconds(3),
                        ApprovalWindow = TimeSpan.FromSeconds(4),
                        ApprovalPending = pending =>
                        {
                            Interlocked.Increment(ref _pendingNotifications);
                            _pending.TrySetResult(pending);
                        },
                    }, _timeouts, cancellationToken: linked.Token);
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }

        internal async Task<LocalApprovalSnapshot> AssertPendingAsync(Task<AuthenticatedControlSession> connecting)
        {
            ControlClientApprovalPending pending = await _pending.Task.WaitAsync(Token);
            LocalApprovalSnapshot snapshot = await InboxObserved.Task.WaitAsync(Token);
            Assert.Equal(snapshot.Request.SessionId, pending.SessionId);
            Assert.Equal(snapshot.Request.ShortCode, pending.ShortCode);
            Assert.Equal(SessionPermission.Control, snapshot.Request.RequestedPermission);
            Assert.Equal(ClientDeviceId, snapshot.Request.ClientDeviceId);
            Assert.Equal(1, PendingNotifications);
            Assert.False(connecting.IsCompleted);
            Assert.Equal(snapshot, Assert.Single(Inbox.GetSnapshot()));
            Assert.Equal(1, Context.PendingApprovalLimiter.GlobalInUse);
            Assert.Equal(0, Context.SessionRegistry.ActiveSessionCount);
            Assert.Equal(1, Counters.Snapshot().Active);
            return snapshot;
        }

        internal void BeginClientHold() => Interlocked.Exchange(ref _holdStarted, Stopwatch.GetTimestamp());

        private async Task SampleAsync()
        {
            long? firstPositive = null;
            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(100));
            while (await timer.WaitForNextTickAsync(_samplingStop.Token))
            {
                // 唯一采样入口，只读真实 registry；绝不注入样本或合成单调时间。
                _evidence.Sample(Context.SessionRegistry);
                IReadOnlyList<ControlSessionSummary> snapshot = Context.SessionRegistry.Snapshot();
                Interlocked.Exchange(ref _maximumRegistered, Math.Max(MaximumRegistered, snapshot.Count));
                foreach (LocalApprovalSnapshot approval in Inbox.GetSnapshot()) { InboxObserved.TrySetResult(approval); }
                if (snapshot.Count == 1)
                {
                    long now = Stopwatch.GetTimestamp();
                    firstPositive ??= now;
                    Registered.TrySetResult(snapshot[0]);
                    long holdStarted = Interlocked.Read(ref _holdStarted);
                    if (holdStarted != 0 && Stopwatch.GetElapsedTime(holdStarted, now) >= TimeSpan.FromSeconds(5))
                    {
                        Held.TrySetResult(Stopwatch.GetElapsedTime(firstPositive.Value, now));
                    }
                }
            }
        }

        private Task<TransportHostStopReport> StartStop()
        {
            if (_stop is null)
            {
                _stopSnapshot = _evidence.BeginHostStop(Context.SessionRegistry);
                Inbox.Stop();
                _stop = Host.StopAsync(TimeSpan.FromSeconds(1));
            }
            return _stop;
        }

        internal async Task<IReadOnlyList<ControlSessionSummary>> StopHostAsync()
        {
            TransportHostStopReport report = await StartStop().WaitAsync(Token);
            Assert.True(report.AllFinished);
            return _stopSnapshot!;
        }

        internal AcceptanceOutcome Evaluate()
        {
            HostRole.HostCounterSnapshot counts = Counters.Snapshot();
            bool clean = _stop?.IsCompletedSuccessfully == true && _stop.Result.AllFinished &&
                counts.Active == 0 && Host.ActiveConnections == 0 && Host.AdmittedConnections == 0 &&
                Context.PendingApprovalLimiter.GlobalInUse == 0 && Context.SessionRegistry.ActiveSessionCount == 0;
            return _evidence.Evaluate(counts.PartitionOk, counts.HandlerFaults, clean);
        }

        internal async Task AssertCleanAsync(string bucket)
        {
            await HandlerFinished.Task.WaitAsync(Token);
            await Counters.WaitForIdleAsync(TimeSpan.FromSeconds(1)).WaitAsync(Token);
            HostRole.HostCounterSnapshot counts = Counters.Snapshot();
            Assert.Equal(1L, counts.Handled);
            Assert.Equal(1L, counts.Sum);
            Assert.True(counts.PartitionOk);
            Assert.Equal(0L, counts.HandlerFaults);
            Assert.Equal(bucket + "=1", counts.Buckets);
            AssertCleanState();
        }

        private void AssertCleanState()
        {
            Assert.Equal(0, Counters.Snapshot().Active);
            Assert.Equal(0, Host.ActiveConnections);
            Assert.Equal(0, Host.AdmittedConnections);
            Assert.Equal(0, Context.PendingApprovalLimiter.GlobalInUse);
            Assert.Equal(0, Context.SessionRegistry.ActiveSessionCount);
            Assert.Empty(Context.SessionRegistry.Snapshot());
            Assert.Equal(0, Inbox.PendingCount);
            Assert.Empty(Inbox.GetSnapshot());
            Assert.Equal(0, _evidence.TrackedCount);
            Assert.False(Run.HasBackgroundFaults, Run.BackgroundFaultSummary);
            Assert.False(Run.Log.FileUnavailable);
        }

        public async ValueTask DisposeAsync()
        {
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
            try
            {
                try
                {
                    _clientStop.Cancel();
                    if (_client is not null)
                    {
                        try { (await _client.WaitAsync(cleanup.Token)).Dispose(); }
                        catch (Exception error) when (_client.IsCompleted &&
                            error is AuthenticationException or OperationCanceledException or IOException or SocketException)
                        {
                            // 仅观察客户端产品任务的预期拒绝/取消；不吞测试断言或未完成任务。
                        }
                    }
                }
                finally
                {
                    try
                    {
                        TransportHostStopReport report = await StartStop().WaitAsync(cleanup.Token);
                        Assert.True(report.AllFinished, "第一次停止报告不允许被 Dispose 的第二次停止洗白。");
                    }
                    finally
                    {
                        try
                        {
                            if (Volatile.Read(ref _entered) != 0)
                                await HandlerFinished.Task.WaitAsync(cleanup.Token);
                            await Counters.WaitForIdleAsync(TimeSpan.FromSeconds(1)).WaitAsync(cleanup.Token);
                        }
                        finally
                        {
                            await Host.DisposeAsync().AsTask().WaitAsync(cleanup.Token);
                        }
                    }
                }
            }
            finally
            {
                try
                {
                    _samplingStop.Cancel();
                    try { await _sampling.WaitAsync(cleanup.Token); }
                    catch (OperationCanceledException) when (_sampling.IsCanceled && _samplingStop.IsCancellationRequested) { }
                }
                finally
                {
                    if (_ownsInbox) { Inbox.Dispose(); }
                    _operation.Dispose();
                    _clientStop.Dispose();
                    _samplingStop.Dispose();
                }
            }
            AssertCleanState();
        }
    }
}
