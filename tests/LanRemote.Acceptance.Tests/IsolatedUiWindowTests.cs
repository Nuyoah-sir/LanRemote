using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using LanRemote.Transport;

namespace LanRemote.Acceptance.Tests;

[Collection(AcceptanceStageFiveCollection.Name)]
public sealed class IsolatedUiWindowTests
{
    private const string ProofFailure = "远端身份验证失败，可能是错误密码或伪造设备广播";
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

    [Fact]
    public void Actual_Startup_Router_Preserves_Normal_And_Headless_And_Requires_Exact_Isolated_Switch()
    {
        Assert.Equal(App.StartupMode.NormalUi, App.RouteStartup([], out var command, out var error));
        Assert.Null(command);
        Assert.Null(error);
        Assert.Equal(App.StartupMode.IsolatedUi, App.RouteStartup(["--isolated-ui"], out command, out error));
        Assert.Null(command);
        Assert.Null(error);
        foreach (string role in new[] { "info", "host", "client" })
        {
            string[] args = role == "client" ? ["--headless", role, "--peer", "TEST-ONLY"] : ["--headless", role];
            Assert.Equal(App.StartupMode.Headless, App.RouteStartup(args, out command, out error));
            Assert.Equal(role, command!.Role);
            Assert.Null(error);
        }
    }

    [Theory]
    [InlineData("--isolated-ui --isolated-ui")]
    [InlineData("--isolated-ui --headless info")]
    [InlineData("--headless info --isolated-ui")]
    [InlineData("--headless client --peer --isolated-ui")]
    [InlineData("--isolated-ui --address 127.0.0.1")]
    [InlineData("--isolated-ui --key PRIVATE-ARGUMENT")]
    [InlineData("--isolated-ui --peer TEST-ONLY")]
    [InlineData("--isolated-ui --pin PRIVATE-ARGUMENT")]
    [InlineData("--isolated-ui --port 45873")]
    [InlineData("--isolated-ui --log-dir PRIVATE-ARGUMENT")]
    [InlineData("--isolated-ui host")]
    [InlineData("--isolated-ui=true")]
    [InlineData("--ISOLATED-UI")]
    [InlineData("--unknown")]
    public void Invalid_Startup_Arguments_Never_Fall_Back_To_Self_Check(string arguments)
    {
        Assert.Equal(App.StartupMode.Invalid,
            App.RouteStartup(arguments.Split(' '), out var command, out var error));
        Assert.Null(command);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.DoesNotContain("PRIVATE-ARGUMENT", error!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Isolated_Only_Startup_Accepts_No_Arguments_Or_The_Exact_Switch(bool explicitSwitch)
    {
        string[] args = explicitSwitch ? ["--isolated-ui"] : [];
        Assert.Equal(App.StartupMode.IsolatedUi,
            App.RouteStartup(args, out var command, out var error, isolatedOnly: true));
        Assert.Null(command);
        Assert.Null(error);
        Assert.True(typeof(App).GetField("_isolatedOnly", BindingFlags.Instance | BindingFlags.NonPublic)!.IsInitOnly);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--headless")]
    [InlineData("--headless info")]
    [InlineData("--headless host")]
    [InlineData("--headless client --peer TEST-ONLY")]
    [InlineData("--headless client --address 127.0.0.1 --pin PRIVATE-ARGUMENT")]
    [InlineData("--headless host --seconds PRIVATE-ARGUMENT")]
    [InlineData("info")]
    [InlineData("host")]
    [InlineData("client")]
    [InlineData("--isolated-ui --headless info")]
    [InlineData("--headless info --isolated-ui")]
    [InlineData("--headless client --peer --isolated-ui")]
    [InlineData("--isolated-ui --isolated-ui")]
    [InlineData("--isolated-ui --address 127.0.0.1")]
    [InlineData("--isolated-ui --key PRIVATE-ARGUMENT")]
    [InlineData("--isolated-ui --peer TEST-ONLY")]
    [InlineData("--isolated-ui --pin PRIVATE-ARGUMENT")]
    [InlineData("--isolated-ui --port 45873")]
    [InlineData("--isolated-ui --log-dir PRIVATE-ARGUMENT")]
    [InlineData("--isolated-ui host")]
    [InlineData("--isolated-ui=true")]
    [InlineData("--ISOLATED-UI")]
    [InlineData("--log-dir PRIVATE-ARGUMENT")]
    [InlineData("--unknown")]
    public void Isolated_Only_Startup_Rejects_All_Other_Arguments_Before_Headless_Parsing(string arguments)
    {
        // 空字符串也保留为一个实参，与无参数入口区分；不创建 App、窗口或身份 vault。
        Assert.Equal(App.StartupMode.Invalid,
            App.RouteStartup(arguments.Split(' '), out var command, out var error, isolatedOnly: true));
        Assert.Null(command);
        Assert.Equal("本机隔离启动器只接受无参数或唯一参数 --isolated-ui；不得运行普通自检或 headless。", error);
        Assert.DoesNotContain("PRIVATE-ARGUMENT", error!);
    }

    [Fact(Timeout = 60_000)]
    public async Task Startup_Crash_And_Window_Logs_Share_A_Fresh_Isolated_Directory()
    {
        FieldInfo crashDirectory = typeof(App).GetField("_crashLogDirectory", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? previous = crashDirectory.GetValue(null);
        string first = App.ConfigureStartupLogDirectory(isolatedUi: true);
        string directory = App.ConfigureStartupLogDirectory(isolatedUi: true);
        try
        {
            Assert.NotEqual(first, directory);
            Assert.Equal(Path.Combine(Path.GetTempPath(), "LanRemote-Isolated"), Path.GetDirectoryName(directory));
            Assert.True(Guid.TryParseExact(Path.GetFileName(directory), "N", out _));
            await using StaDispatcherFixture sta = new();
            Dispatcher dispatcher = await sta.Ready.WaitAsync(Guard);
            await dispatcher.InvokeAsync(() =>
            {
                MainWindow window = new(directory, isolatedUi: true);
                try
                {
                    Assert.True(Field<bool>(window, "_isolatedUi"));
                    Assert.Equal(directory, Field<string>(window, "_logDirectory"));
                    Assert.Equal(Path.Combine(directory, "gui.log"), Field<AcceptanceLog>(window, "_processLog").FilePath);
                    AssertOffscreen(window);
                    AssertNoIdentityWork(window);
                    string path = (string)typeof(App).GetMethod("TryWriteCrash", BindingFlags.Static | BindingFlags.NonPublic)!
                        .Invoke(null, [new InvalidOperationException("PRIVATE-CRASH-DETAIL")])!;
                    Assert.Equal(Path.Combine(directory, "crash.log"), path);
                    Assert.DoesNotContain("PRIVATE-CRASH-DETAIL", File.ReadAllText(path));
                }
                finally { window.Close(); }
            }).Task.WaitAsync(Guard);
        }
        finally
        {
            crashDirectory.SetValue(null, previous);
            if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
            if (Directory.Exists(first)) { Directory.Delete(first, recursive: true); }
        }
    }

    [Fact(Timeout = 60_000)]
    public Task Loaded_And_Misrouted_Ordinary_Handlers_Cannot_Start_Identity_Or_Busy_Work() => WithWindowAsync(async ui =>
    {
        Assert.True(typeof(MainWindow).GetField("_isolatedUi", BindingFlags.Instance | BindingFlags.NonPublic)!.IsInitOnly);
        Assert.True(Field<bool>(ui.Window, "_isolatedUi"));
        // 真实 Loaded 路由事件，而不是仅检查控件隐藏；不创建桌面窗口。
        ui.Window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, ui.Window));
        AssertNoIdentityWork(ui.Window);
        FieldInfo ready = typeof(MainWindow).GetField("_ready", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ready.SetValue(ui.Window, true); // 排除普通 _ready 闸门掩盖隔离防护。
        try
        {
            foreach (string name in new[] { "SelfCheckButton", "HostButton", "ClientButton", "StopClientButton", "ShowKeyButton" })
            {
                ui.Click(name); // 故意投递到已禁用的真实控件。
                Invoke(ui.Window, name + "_Click", ui.Named<Button>(name), new RoutedEventArgs(Button.ClickEvent));
                AssertNoIdentityWork(ui.Window);
            }
            await ((Task)Invoke(ui.Window, "RunSelfCheckAsync")!).WaitAsync(Guard);
            AssertNoIdentityWork(ui.Window);
        }
        finally { ready.SetValue(ui.Window, false); }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        AssertNoIdentityWork(ui.Window);
        foreach (string name in new[] { "IdentityPanel", "NormalRolePanel" })
        {
            Assert.Equal(Visibility.Collapsed, ui.Named<GroupBox>(name).Visibility);
            Assert.False(ui.Named<GroupBox>(name).IsEnabled);
        }
        Assert.Equal("检测中…", ui.Named<TextBlock>("DeviceCodeText").Text);
        Assert.Equal("检测中…", ui.Named<TextBlock>("CertPinText").Text);
        Assert.Equal("检测中…", ui.Named<TextBlock>("ListenText").Text);
        Assert.Equal("停止本机专项", ui.Named<Button>("StopHostButton").Content);
        Assert.False(ui.Named<Button>("StopHostButton").IsEnabled);
        Assert.Contains("不等于 M4 整体通过", ui.Named<TextBlock>("WindowHeading").Text);
        Assert.Contains("尚未启动监听", ui.Named<TextBlock>("RoleBannerText").Text);
        ui.AssertLayout();
    });

    [Fact(Timeout = 60_000)]
    public Task Default_Constructor_Argument_Keeps_Ordinary_Ui_And_Rejects_Isolated_Clicks() => WithWindowAsync(async ui =>
    {
        Assert.False(Field<bool>(ui.Window, "_isolatedUi"));
        Assert.Equal(Visibility.Visible, ui.Named<GroupBox>("IdentityPanel").Visibility);
        Assert.True(ui.Named<GroupBox>("NormalRolePanel").IsEnabled);
        Assert.True(ui.Named<Button>("SelfCheckButton").IsEnabled);
        foreach (string name in new[] { "IsolatedActiveStopButton", "IsolatedProofMismatchButton" })
        {
            Assert.Equal(Visibility.Collapsed, ui.Named<Button>(name).Visibility);
            ui.Click(name);
            Invoke(ui.Window, name + "_Click", ui.Named<Button>(name), new RoutedEventArgs(Button.ClickEvent));
        }
        // 普通 Loaded 会有意读取真实身份，故本测试只核对普通路由与构造默认值，不触发它。
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        AssertNoIdentityWork(ui.Window);
    }, isolatedUi: false);

    [Theory(Timeout = 60_000)]
    [InlineData("ApproveButton", "Control")]
    [InlineData("ViewOnlyButton", "ViewOnly")]
    public Task Real_Proof_Mismatch_Through_Manual_Approval_Reaches_ResultBanner(string approvalButton, string grant) =>
        WithWindowAsync(async ui =>
        {
            WindowRun run = ui.Start("IsolatedProofMismatchButton");
            LocalApprovalSnapshot request = await ui.PendingAsync(run);
            // 未选择时的误点击不能提交，pending 更不能当作成功。
            ui.Click("ApproveButton");
            Assert.Single(run.State.Inbox!.GetSnapshot());
            Assert.Contains("submitted=False", run.State.Run.Log.All);
            ui.AssertLayout();
            ui.SelectAndApprove(approvalButton);
            Assert.Equal(AcceptanceOutcome.Fail, await run.Worker.WaitAsync(Guard));
            await ui.WaitReapedAsync();
            Assert.Equal(ProofFailure, run.State.AuthenticationFailure);
            Assert.Contains(ProofFailure, ui.Named<TextBlock>("ResultBannerText").Text);
            Assert.Contains("（FAIL）", ui.Named<TextBlock>("ResultBannerText").Text);
            Assert.Contains("不等于 M4 整体通过", ui.Named<TextBlock>("ResultBannerText").Text);
            string log = run.State.Run.Log.All;
            Assert.Contains($"[ISOLATED][INJECT] grant={grant} flippedBits=1", log);
            Assert.Contains($"requestId={request.RequestId} generation={request.Generation}", log);
            Assert.Contains("submitted=True decision=Approved grant=" + grant, log);
            Assert.Contains("clientOutcome=FAIL stage=authentication rejection=client-server-proof-mismatch", log);
            Assert.Contains("beforeHostStop=True", log);
            Assert.True(log.IndexOf("[ISOLATED][PEER-CLOSE]", StringComparison.Ordinal) <
                log.IndexOf("[ISOLATED][HOST-STOP]", StringComparison.Ordinal));
            Assert.DoesNotContain("serverProof=verified", log);
            Assert.DoesNotContain("clientOutcome=PASS", log);
            AssertCleanAndSettled(run, "FAIL");
            Assert.Equal(string.Empty, ui.Named<TextBlock>("ClientPendingText").Text);
            ui.AssertLayout();
        });

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public Task Real_Active_Host_Stop_And_OnClosing_Join_The_Same_Run(bool close) => WithWindowAsync(async ui =>
    {
        WindowRun run = ui.Start("IsolatedActiveStopButton");
        LocalApprovalSnapshot request = await ui.PendingAsync(run);
        ui.SelectAndApprove("ApproveButton");
        await WaitUntilAsync(() => ui.Named<TextBlock>("RoleBannerText").Text.StartsWith("已双向认证", StringComparison.Ordinal));
        Assert.Contains($"serverProof=verified sessionId={request.Request.SessionId}", run.State.Run.Log.All);
        Assert.Contains("localObjectHoldTargetMs=120000", run.State.Run.Log.All);
        Assert.Contains("Host registry=1", ui.Named<TextBlock>("RoleBannerText").Text);
        Assert.Contains("120 秒", ui.Named<TextBlock>("HostWindowText").Text);
        Assert.DoesNotContain("180 秒", ui.Named<TextBlock>("HostWindowText").Text);
        Assert.False(run.Worker.IsCompleted);
        Assert.Null(run.State.GetHostContext());
        Assert.False(ui.Named<Button>("ShowKeyButton").IsEnabled);
        ui.Click("ShowKeyButton");
        ui.Click("StopClientButton");
        Assert.Null(Field<Task?>(ui.Window, "_keyTask"));
        Assert.False(run.State.IsStopped);
        Assert.False(run.State.Run.AbortedByOperator);
        ui.AssertLayout();
        bool closed = false;
        ui.Window.Closed += (_, _) => closed = true;
        if (close)
        {
            CancelEventArgs args = new();
            Invoke(ui.Window, "OnClosing", ui.Window, args);
            Assert.True(args.Cancel);
            Assert.False(ui.Window.IsEnabled);
            Assert.False(closed);
        }
        else { ui.Click("StopHostButton"); }
        Assert.True(run.State.IsStopped);
        Assert.True(run.State.Run.AbortedByOperator);
        Task cancellation = Assert.IsAssignableFrom<Task>(Field<Task?>(ui.Window, "_runCancellationTask"));
        Assert.False(ui.Named<Button>("IsolatedActiveStopButton").IsEnabled);
        ui.Click("IsolatedProofMismatchButton");
        Assert.Same(run.Worker, Field<Task?>(ui.Window, "_runTask"));
        await cancellation.WaitAsync(Guard);
        Assert.Equal(AcceptanceOutcome.InvalidRun, await run.Worker.WaitAsync(Guard));
        await ui.WaitReapedAsync();
        Assert.Contains("（INVALID_RUN）", ui.Named<TextBlock>("ResultBannerText").Text);
        Assert.Contains("[HOST][STOP] authenticatedActiveBeforeStop=1", run.State.Run.Log.All);
        Assert.Contains("closeOrigin=host-forced-close naturalRelease=UNMET", run.State.Run.Log.All);
        Assert.Contains("[ISOLATED][HOST-STOP] first=True clientTaskCompleted=False", run.State.Run.Log.All);
        Assert.DoesNotContain("clientOutcome=PASS", run.State.Run.Log.All);
        AssertCleanAndSettled(run, "INVALID_RUN");
        if (close) { await WaitUntilAsync(() => closed); }
        else { ui.AssertLayout(); }
    });

    [Fact(Timeout = 60_000)]
    public Task Completed_Real_Worker_Waits_For_CancelAsync_Callbacks_Before_Reaping() => WithWindowAsync(async ui =>
    {
        WindowRun run = ui.Start("IsolatedProofMismatchButton");
        await ui.PendingAsync(run);
        TaskCompletionSource cleanupReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancellationEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseCancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void AtCleanup(string line)
        {
            if (!line.StartsWith("[ISOLATED][CLEANUP]", StringComparison.Ordinal)) { return; }
            cleanupReached.TrySetResult();
            releaseCleanup.Task.GetAwaiter().GetResult();
        }
        run.State.Run.Log.LineWritten += AtCleanup;
        using CancellationTokenRegistration registration = run.State.Cts.Token.Register(() =>
        {
            cancellationEntered.TrySetResult();
            releaseCancellation.Task.GetAwaiter().GetResult();
        });
        try
        {
            ui.SelectAndApprove("ApproveButton");
            // 只暂停真实引擎的收尾日志回调；没有伪造日志、结果或角色任务。
            await cleanupReached.Task.WaitAsync(Guard);
            ui.Click("StopHostButton");
            await cancellationEntered.Task.WaitAsync(Guard);
            Task cancellation = Field<Task>(ui.Window, "_runCancellationTask");
            releaseCleanup.TrySetResult();
            Assert.Equal(AcceptanceOutcome.InvalidRun, await run.Worker.WaitAsync(Guard));
            Assert.False(cancellation.IsCompleted);
            Invoke(ui.Window, "ReapCompletedTasks");
            Assert.Same(run.State, Field<MainWindow.RunState>(ui.Window, "_activeRun"));
            Assert.Same(run.State.Cts, Field<CancellationTokenSource>(ui.Window, "_runCts"));
            Assert.Same(run.Worker, Field<Task?>(ui.Window, "_runTask"));
            Assert.False(ui.Named<Button>("IsolatedActiveStopButton").IsEnabled);
            Assert.Equal(Visibility.Collapsed, ui.Named<Border>("ResultBanner").Visibility);
        }
        finally
        {
            releaseCleanup.TrySetResult();
            releaseCancellation.TrySetResult();
            if (Field<Task?>(ui.Window, "_runCancellationTask") is { } cancellation) { await cancellation.WaitAsync(Guard); }
            run.State.Run.Log.LineWritten -= AtCleanup;
        }
        await ui.WaitReapedAsync();
        AssertCleanAndSettled(run, "INVALID_RUN");
    });

    [Fact(Timeout = 60_000)]
    public Task Bounded_Background_Status_From_Retired_Run_Cannot_Pollute_New_Run() => WithWindowAsync(async ui =>
    {
        WindowRun old = ui.Start("IsolatedProofMismatchButton");
        await ui.PendingAsync(old);
        ui.SelectAndApprove("ApproveButton");
        Assert.Equal(AcceptanceOutcome.Fail, await old.Worker.WaitAsync(Guard));
        await ui.WaitReapedAsync();
        string finished = ui.Named<TextBlock>("RoleBannerText").Text;
        await Task.Run(() => old.State.PublishIsolatedStatus(new string('旧', 4096)));
        Assert.Equal(512, old.State.GetIsolatedStatus()!.Length);
        Invoke(ui.Window, "OnUiTick", ui.Window, EventArgs.Empty);
        Assert.Equal(finished, ui.Named<TextBlock>("RoleBannerText").Text);
        WindowRun next = ui.Start("IsolatedActiveStopButton");
        Assert.NotSame(old.State.Inbox, next.State.Inbox);
        Assert.NotEqual(old.State.Run.RunId, next.State.Run.RunId);
        Assert.NotEqual(old.State.Run.Log.FilePath, next.State.Run.Log.FilePath);
        await ui.PendingAsync(next);
        await Task.Run(() => old.State.PublishIsolatedStatus("旧轮次的迟到状态"));
        Invoke(ui.Window, "OnUiTick", ui.Window, EventArgs.Empty);
        Assert.DoesNotContain("旧", ui.Named<TextBlock>("RoleBannerText").Text);
        Assert.Null(next.State.AuthenticationFailure);
        Assert.Null(ui.Named<ListBox>("ApprovalList").SelectedItem);
        ui.Click("StopHostButton");
        Assert.Equal(AcceptanceOutcome.InvalidRun, await next.Worker.WaitAsync(Guard));
        await ui.WaitReapedAsync();
        AssertCleanAndSettled(next, "INVALID_RUN");
    });

    private static void AssertCleanAndSettled(WindowRun run, string outcome)
    {
        string log = run.State.Run.Log.All;
        Assert.False(run.State.Run.HasBackgroundFaults);
        Assert.Null(run.State.GetHostContext());
        Assert.Null(run.State.GetPending());
        Assert.True(run.State.IsStopped);
        Assert.Contains("activeHandlers=0 activeRegistry=0 pendingApprovals=0 inboxPending=0 activeConnections=0 admittedConnections=0", log);
        Assert.Contains("handlersJoined=True clientJoined=True keyCleared=True certificateDisposed=True cleanupFault=False", log);
        string[] lines = run.State.Run.Log.ReadFrom(0, int.MaxValue).ToArray();
        Assert.Equal("[RESULT] outcome   = " + outcome, Assert.Single(lines, line => line.StartsWith("[RESULT] outcome", StringComparison.Ordinal)));
        Assert.Single(lines, line => line == "RUN COMPLETE");
        Assert.True(log.IndexOf("[ISOLATED][CLEANUP]", StringComparison.Ordinal) < log.IndexOf("RUN COMPLETE", StringComparison.Ordinal));
        Assert.Contains("RUN COMPLETE", File.ReadAllText(run.State.Run.Log.FilePath!));
    }

    private static void AssertNoIdentityWork(MainWindow window)
    {
        Assert.False(Field<bool>(window, "_busy"));
        Assert.Null(Field<Task?>(window, "_busyTask"));
        Assert.Null(Field<Task?>(window, "_runTask"));
        Assert.Null(Field<AcceptanceRun?>(window, "_run"));
        Assert.Null(Field<MainWindow.RunState?>(window, "_activeRun"));
        Assert.Null(Field<CancellationTokenSource?>(window, "_runCts"));
        Assert.Null(Field<Task?>(window, "_keyTask"));
        Assert.Null(Field<object?>(window, "_keyWork"));
        Assert.False(Field<bool>(window, "_keyConfirming"));
        // 手工投递 Loaded 后 WPF 可排队触发 ContentRendered，即使没有 HWND。
        // 只容许这一条非身份的生命周期记录，不能以空日志误判是否读取真实身份。
        Assert.All(Field<AcceptanceLog>(window, "_processLog").ReadFrom(0, int.MaxValue),
            line => Assert.StartsWith("[GUI] M4 窗口渲染完成。", line));
        Assert.Equal(new[] { "gui.log" }, Directory.GetFiles(Field<string>(window, "_logDirectory")).Select(Path.GetFileName));
    }

    private static async Task WithWindowAsync(Func<WindowUi, Task> test, bool isolatedUi = true)
    {
        using AcceptanceTestDirectory directory = new();
        await using StaDispatcherFixture sta = new();
        Dispatcher dispatcher = await sta.Ready.WaitAsync(Guard);
        await dispatcher.InvokeAsync(async () =>
        {
            await using WindowUi ui = new(directory.Path, isolatedUi);
            AssertOffscreen(ui.Window);
            await test(ui);
            AssertOffscreen(ui.Window);
        }).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(50));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource guard = new(Guard);
        while (!condition()) { await Task.Delay(20, guard.Token); }
    }

    private static void AssertOffscreen(MainWindow window)
    {
        Assert.False(window.IsVisible);
        Assert.Equal(IntPtr.Zero, new WindowInteropHelper(window).Handle);
        Assert.Null(PresentationSource.FromVisual(window));
    }

    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static object? Invoke(MainWindow window, string name, params object[] args) => typeof(MainWindow)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.Invoke(window, args);

    private sealed record WindowRun(MainWindow.RunState State, Task<AcceptanceOutcome> Worker);

    private sealed class WindowUi : IAsyncDisposable
    {
        private readonly Grid _root;
        private readonly ResourceDictionary _applicationResources;
        internal MainWindow Window { get; }

        internal WindowUi(string directory, bool isolatedUi)
        {
            // 普通分支特意使用省略 bool 的兼容构造函数；两条路均不触及用户默认目录。
            Window = isolatedUi ? new MainWindow(directory, isolatedUi: true) : new MainWindow(directory);
            _applicationResources = LoadApplicationResources();
            Window.Resources.MergedDictionaries.Add(_applicationResources);
            _root = (Grid)Window.Content;
            Window.Content = null;
            _root.Resources.MergedDictionaries.Add(Window.Resources);
            if (isolatedUi) { Window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, Window)); }
        }

        internal T Named<T>(string name) where T : class => Assert.IsAssignableFrom<T>(Window.FindName(name));
        internal void Click(string name)
        {
            Button button = Named<Button>(name);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
        }

        internal WindowRun Start(string name)
        {
            Assert.True(Named<Button>(name).IsEnabled);
            Click(name);
            MainWindow.RunState state = Field<MainWindow.RunState>(Window, "_activeRun");
            Task<AcceptanceOutcome> worker = Field<Task<AcceptanceOutcome>>(Window, "_runTask");
            Assert.True(state.IsHost);
            Assert.NotNull(state.Inbox);
            Assert.Null(state.GetHostContext());
            Assert.Same(state.Run, Field<AcceptanceRun>(Window, "_run"));
            Assert.Equal(Field<string>(Window, "_logDirectory"), Path.GetDirectoryName(state.Run.Log.FilePath));
            // 第二次投递不能替换正在运行的生命周期。
            Click(name);
            Assert.Same(worker, Field<Task?>(Window, "_runTask"));
            return new(state, worker);
        }

        internal async Task<LocalApprovalSnapshot> PendingAsync(WindowRun run)
        {
            await WaitUntilAsync(() => Named<ListBox>("ApprovalList").Items.Count == 1 &&
                Named<TextBlock>("ClientPendingText").Text.Contains("已收到 pending", StringComparison.Ordinal));
            LocalApprovalSnapshot request = Assert.Single(run.State.Inbox!.GetSnapshot());
            Assert.Equal(request.RequestId, Assert.IsType<LocalApprovalSnapshot>(Named<ListBox>("ApprovalList").Items[0]).RequestId);
            Assert.Equal(request.Request.SessionId, run.State.GetPending()!.SessionId);
            Assert.Null(Named<ListBox>("ApprovalList").SelectedItem);
            foreach (string name in new[] { "ApproveButton", "ViewOnlyButton", "RejectButton" }) { Assert.False(Named<Button>(name).IsEnabled); }
            Assert.False(run.Worker.IsCompleted);
            Assert.DoesNotContain("serverProof=verified", run.State.Run.Log.All);
            Assert.Contains("尚未验证远端身份，也未获得权限", Named<TextBlock>("ClientPendingText").Text);
            Assert.Equal(Visibility.Collapsed, Named<Border>("ResultBanner").Visibility);
            Assert.Null(run.State.AuthenticationFailure);
            return request;
        }

        internal void SelectAndApprove(string button)
        {
            Named<ListBox>("ApprovalList").SelectedIndex = 0;
            Assert.True(Named<Button>(button).IsEnabled);
            Click(button);
            Assert.Contains("不等于已授权", Named<TextBlock>("ApprovalStatusText").Text);
        }

        internal async Task WaitReapedAsync()
        {
            await WaitUntilAsync(() => Field<MainWindow.RunState?>(Window, "_activeRun") is null &&
                (Field<bool>(Window, "_closing") || Named<Button>("IsolatedActiveStopButton").IsEnabled));
            Assert.Null(Field<Task?>(Window, "_runTask"));
            Assert.Null(Field<Task?>(Window, "_runCancellationTask"));
            Assert.Null(Field<CancellationTokenSource?>(Window, "_runCts"));
            Assert.Null(Field<Task?>(Window, "_keyTask"));
            Assert.Equal(Visibility.Visible, Named<Border>("ResultBanner").Visibility);
        }

        internal void AssertLayout()
        {
            // 真实 App.xaml 样式 + 800×640 客户区预算；不 Show、不调用外部桌面自动化。
            _root.Measure(new Size(800, 640));
            _root.Arrange(new Rect(0, 0, 800, 640));
            _root.UpdateLayout();
            Rect bounds = new(0, 0, 776, 616);
            Assert.True(_root.ActualWidth <= bounds.Width + 0.5 && _root.ActualHeight <= bounds.Height + 0.5);
            foreach (string name in new[] { "ModeBanner", "RoleBanner", "IsolatedActiveStopButton", "IsolatedProofMismatchButton",
                         "StopHostButton", "ApprovalPanel", "ApproveButton", "ViewOnlyButton", "RejectButton", "LogBox", "LogPathText" })
            {
                Within(_root, Named<FrameworkElement>(name), bounds);
            }
            foreach (string name in new[] { "ApproveButton", "ViewOnlyButton", "RejectButton", "StopHostButton", "IsolatedActiveStopButton" })
            {
                Button button = Named<Button>(name);
                Assert.Same(_applicationResources[typeof(Button)], button.Style);
                Assert.Equal(new Thickness(12, 6, 12, 6), button.Padding);
                Assert.Equal(96d, button.MinWidth);
            }
            Assert.Same(_applicationResources[typeof(GroupBox)], Named<GroupBox>("ApprovalPanel").Style);
            if (Named<ListBox>("ApprovalList").Items.Count > 0)
            {
                Within(_root, Named<ListBox>("ApprovalList"), bounds);
                Within(_root, Named<TextBlock>("ClientPendingText"), bounds);
            }
            if (Named<Border>("ResultBanner").Visibility == Visibility.Visible)
            {
                Within(_root, Named<Border>("ResultBanner"), bounds);
                Within(_root, Named<TextBlock>("ResultBannerText"), bounds);
            }
            ScrollViewer roles = Assert.Single(_root.Children.OfType<ScrollViewer>());
            foreach (string name in new[] { "IsolatedActiveStopButton", "IsolatedProofMismatchButton", "StopHostButton" })
            {
                Within(roles, Named<Button>(name), new Rect(new Point(), roles.RenderSize));
            }
            Assert.Null(PresentationSource.FromVisual(_root));
            AssertOffscreen(Window);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (Field<MainWindow.RunState?>(Window, "_activeRun") is not null)
                {
                    Invoke(Window, "RequestRunStop", "离屏测试清理");
                    if (Field<Task?>(Window, "_runCancellationTask") is { } cancellation) { await cancellation.WaitAsync(Guard); }
                    if (Field<Task?>(Window, "_runTask") is { } worker) { await worker.WaitAsync(Guard); }
                    Invoke(Window, "ReapCompletedTasks");
                }
            }
            finally
            {
                Field<DispatcherTimer>(Window, "_uiTimer").Stop();
                Window.Close();
            }
        }
    }

    private static void Within(Visual ancestor, FrameworkElement element, Rect bounds)
    {
        Assert.Equal(Visibility.Visible, element.Visibility);
        Assert.True(element.ActualWidth > 0 && element.ActualHeight > 0, $"{element.Name} 必须具有实际布局面积。");
        Rect actual = element.TransformToAncestor(ancestor).TransformBounds(new Rect(new Point(), element.RenderSize));
        Assert.True(actual.Left >= bounds.Left - 0.5 && actual.Top >= bounds.Top - 0.5 &&
            actual.Right <= bounds.Right + 0.5 && actual.Bottom <= bounds.Bottom + 0.5,
            $"{element.Name} 的矩形 {actual} 超出客户区 {bounds}。");
    }

    private static ResourceDictionary LoadApplicationResources()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "tools", "LanRemote.Acceptance", "App.xaml");
            if (!File.Exists(path)) { continue; }
            XElement app = XDocument.Load(path).Root!;
            XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XElement dictionary = new(ns + "ResourceDictionary", app.Attributes().Where(a => a.IsNamespaceDeclaration),
                app.Element(ns + "Application.Resources")!.Elements());
            using var reader = dictionary.CreateReader();
            return Assert.IsType<ResourceDictionary>(XamlReader.Load(reader));
        }
        throw new DirectoryNotFoundException("未找到仓库中的真实 App.xaml 资源。");
    }
}
