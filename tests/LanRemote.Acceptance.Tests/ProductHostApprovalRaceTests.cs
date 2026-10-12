using System.IO;
using System.Net;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LanRemote.App.Services;
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
public sealed class ProductHostApprovalRaceTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private const string ApprovalTitle = "LanRemote 本机连接审批";

    [Fact(Timeout = 60_000)]
    public async Task Closed_Gate_Fails_Closed_And_Control_Or_Expired_Requests_Do_Not_Reserve_An_Id()
    {
        await using GateFixture fixture = await GateFixture.CreateAsync();
        Guid id = Guid.NewGuid();
        LocalApprovalRequest viewOnly = Request(id);
        Assert.False(fixture.Gate.AreApprovalsOpen);

        Task<LocalApprovalDecision> closed = fixture.Request(viewOnly);
        Assert.True(closed.IsCompletedSuccessfully);
        await AssertDecisionAsync(closed, viewOnly, LocalApprovalOutcome.Unavailable);

        Assert.True(fixture.Gate.OpenApprovals());
        LocalApprovalRequest control = viewOnly with { RequestedPermission = SessionPermission.Control };
        Task<LocalApprovalDecision> denied = fixture.Request(control);
        Assert.True(denied.IsCompletedSuccessfully);
        await AssertDecisionAsync(denied, control, LocalApprovalOutcome.Denied);

        LocalApprovalRequest expired = viewOnly with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) };
        Task<LocalApprovalDecision> timedOut = fixture.Request(expired);
        Assert.True(timedOut.IsCompletedSuccessfully);
        await AssertDecisionAsync(timedOut, expired, LocalApprovalOutcome.TimedOut);
        // OpenApprovals 在仍有 pending 时返回 false；同 ID 的拒绝和到期均未占用名额。
        Assert.True(fixture.Gate.OpenApprovals());
        await fixture.Dispatcher.InvokeAsync(() => Assert.Empty(ApprovalWindows(fixture.Dispatcher)))
            .Task.WaitAsync(Guard);
    }

    [Fact(Timeout = 60_000)]
    public async Task Block_Revokes_Unrendered_Request_While_Dispatcher_Is_Blocked_And_Old_Window_Never_Shows()
    {
        await using GateFixture fixture = await GateFixture.CreateAsync();
        Dispatcher dispatcher = fixture.Dispatcher;
        Assert.True(fixture.Gate.OpenApprovals());
        string oldName = "阻断前旧请求";
        LocalApprovalRequest old = Request(Guid.NewGuid()) with { ClientName = oldName };
        int oldShown = 0;
        DispatcherHookEventHandler observe = (_, _) =>
        {
            if (ApprovalWindows(dispatcher).Any(window => ShowsClient(window, oldName)))
            {
                Interlocked.Exchange(ref oldShown, 1);
            }
        };
        DispatcherHooks hooks = await dispatcher.InvokeAsync(() =>
        {
            DispatcherHooks current = dispatcher.Hooks;
            current.OperationCompleted += observe;
            return current;
        }).Task.WaitAsync(Guard);
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherOperation blocking = dispatcher.InvokeAsync(() =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("测试 Dispatcher 未被释放。");
            }
        }, DispatcherPriority.Send);
        try
        {
            await entered.Task.WaitAsync(Guard);
            Task<LocalApprovalDecision> revoked = fixture.Request(old);
            Assert.False(revoked.IsCompleted);
            Assert.False(fixture.Gate.OpenApprovals());
            Task<LocalApprovalDecision> duplicate = fixture.Request(old);
            Assert.True(duplicate.IsCompletedSuccessfully);
            await AssertDecisionAsync(duplicate, old, LocalApprovalOutcome.Unavailable);

            await Task.Run(fixture.Gate.BlockApprovals).WaitAsync(Guard);
            Assert.False(fixture.Gate.AreApprovalsOpen);
            await AssertDecisionAsync(revoked, old, LocalApprovalOutcome.Unavailable);
            Assert.False(blocking.Task.IsCompleted);
            Assert.False(fixture.Gate.JoinBlockedApprovalsAsync().IsCompleted);
            Assert.False(fixture.Gate.OpenApprovals());

            release.Set();
            await blocking.Task.WaitAsync(Guard);
            await fixture.Gate.JoinBlockedApprovalsAsync().WaitAsync(Guard);
            Assert.True(fixture.Gate.OpenApprovals());
            LocalApprovalRequest next = Request(Guid.NewGuid()) with { ClientName = "阻断后新请求" };
            Task<LocalApprovalDecision> replacement = fixture.Request(next);
            Assert.False(replacement.IsCompleted);
            await dispatcher.InvokeAsync(() =>
            {
                Window window = Assert.Single(ApprovalWindows(dispatcher));
                Assert.True(ShowsClient(window, next.ClientName));
                Assert.False(ShowsClient(window, oldName));
                Click(Controls(window).Deny);
            }, DispatcherPriority.ContextIdle).Task.WaitAsync(Guard);
            Assert.Equal(0, Volatile.Read(ref oldShown));
            await AssertDecisionAsync(replacement, next, LocalApprovalOutcome.Denied);
        }
        finally
        {
            release.Set();
            await blocking.Task.WaitAsync(Guard);
            await dispatcher.InvokeAsync(() => hooks.OperationCompleted -= observe).Task.WaitAsync(Guard);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Block_Revokes_Real_Host_Pending_Before_Paused_Enable_Can_Allow_Listening()
    {
        await using GateFixture fixture = await GateFixture.CreateAsync();
        string root = Path.Combine(Path.GetTempPath(), $"LanRemote-approval-race-{Guid.NewGuid():N}");
        AppPaths paths = AppPaths.FromRootDirectory(root);
        DpapiSecretVault vault = new(paths);
        using DeviceCertificateService certificates = new(vault);
        DeviceIdentityService identity = new(vault, certificates);
        NeverRefreshBindings bindings = new();
        HostRuntimeService runtime = new(paths, identity, certificates,
            new DpapiAccessSecretStore(vault), bindings, new SubnetPolicy(bindings),
            fixture.Gate, NullLogger<HostRuntimeService>.Instance);
        Assert.False(fixture.Gate.AreApprovalsOpen);
        SemaphoreSlim lifetime = Assert.IsType<SemaphoreSlim>(typeof(HostRuntimeService)
            .GetField("_lifetime", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(runtime));

        TaskCompletionSource<bool> opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resumeEnable = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task enabling = Task.Run(async () =>
        {
            opened.TrySetResult(runtime.OpenApprovals());
            await resumeEnable.Task;
            runtime.AllowListening();
        });
        bool lifetimeHeld = false;
        Task? blocking = null;
        try
        {
            Assert.True(await opened.Task.WaitAsync(Guard));
            LocalApprovalRequest old = Request(Guid.NewGuid());
            Task<LocalApprovalDecision> pending = fixture.Request(old);
            Button staleApprove = await fixture.Dispatcher.InvokeAsync(() =>
            {
                Window window = Assert.Single(ApprovalWindows(fixture.Dispatcher));
                var controls = Controls(window);
                controls.ViewOnly.IsChecked = true;
                Assert.True(controls.Approve.IsEnabled);
                return controls.Approve;
            }, DispatcherPriority.ContextIdle).Task.WaitAsync(Guard);
            Assert.False(pending.IsCompleted);

            // 模拟启动持有 lifetime：同步阻断不能等它，更不能等 STA 关闭窗口。
            await lifetime.WaitAsync().WaitAsync(Guard);
            lifetimeHeld = true;
            blocking = Task.Run(runtime.BlockApprovals);
            await blocking.WaitAsync(Guard);
            Assert.False(fixture.Gate.AreApprovalsOpen);
            await AssertDecisionAsync(pending, old, LocalApprovalOutcome.Unavailable);
            await fixture.Dispatcher.InvokeAsync(() =>
            {
                Assert.Empty(ApprovalWindows(fixture.Dispatcher));
                Click(staleApprove);
            }, DispatcherPriority.ContextIdle).Task.WaitAsync(Guard);
            await AssertDecisionAsync(pending, old, LocalApprovalOutcome.Unavailable);

            resumeEnable.TrySetResult();
            await enabling.WaitAsync(Guard);
            Assert.False(runtime.ApprovalsAvailable);
            Assert.False(fixture.Gate.AreApprovalsOpen);
            LocalApprovalRequest afterBlock = Request(Guid.NewGuid());
            await AssertDecisionAsync(fixture.Request(afterBlock), afterBlock, LocalApprovalOutcome.Unavailable);
            Assert.False(Directory.Exists(root)); // 未启动 Host，不能读取密钥或刷新网卡。
        }
        finally
        {
            resumeEnable.TrySetResult();
            if (lifetimeHeld) lifetime.Release();
            if (blocking is not null) await blocking.WaitAsync(Guard);
            await enabling.WaitAsync(Guard);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Stale_Manual_Approval_Click_Cannot_Approve_Reused_Request_Id()
    {
        await using GateFixture fixture = await GateFixture.CreateAsync();
        Dispatcher dispatcher = fixture.Dispatcher;
        Assert.True(fixture.Gate.OpenApprovals());
        LocalApprovalRequest old = Request(Guid.NewGuid()) with { ClientName = "旧代审批" };
        Task<LocalApprovalDecision> first = fixture.Request(old);
        (Window oldWindow, Button oldApprove) = await dispatcher.InvokeAsync(() =>
        {
            Window window = Assert.Single(ApprovalWindows(dispatcher));
            var controls = Controls(window);
            Assert.False(controls.Approve.IsEnabled);
            controls.ViewOnly.IsChecked = true; // 真实按钮必须先由本机明确勾选，才可点击。
            Assert.True(controls.Approve.IsEnabled);
            return (window, controls.Approve);
        }).Task.WaitAsync(Guard);
        Assert.False(first.IsCompleted);
        Task<LocalApprovalDecision> duplicate = fixture.Request(old);
        await AssertDecisionAsync(duplicate, old, LocalApprovalOutcome.Unavailable);

        fixture.Gate.BlockApprovals();
        await AssertDecisionAsync(first, old, LocalApprovalOutcome.Unavailable);
        await fixture.Gate.JoinBlockedApprovalsAsync().WaitAsync(Guard);
        Assert.True(fixture.Gate.OpenApprovals());
        LocalApprovalRequest current = old with
        {
            SessionId = Guid.NewGuid(),
            ConnectionId = Guid.NewGuid(),
            ClientName = "新代审批",
        };
        Task<LocalApprovalDecision> replacement = fixture.Request(current);
        bool staleClickDelivered = false;
        await dispatcher.InvokeAsync(() =>
        {
            Assert.False(oldWindow.IsVisible);
            Window currentWindow = Assert.Single(ApprovalWindows(dispatcher));
            Assert.NotSame(oldWindow, currentWindow);
            Assert.True(ShowsClient(currentWindow, current.ClientName));
            oldApprove.Click += (_, _) => staleClickDelivered = true;
            Click(oldApprove);
            Assert.True(staleClickDelivered);
            Assert.False(replacement.IsCompleted);
            var controls = Controls(currentWindow);
            Assert.False(controls.Approve.IsEnabled);
            Click(controls.Deny);
        }, DispatcherPriority.ContextIdle).Task.WaitAsync(Guard);
        await AssertDecisionAsync(replacement, current, LocalApprovalOutcome.Denied);
    }

    [Fact(Timeout = 60_000)]
    public async Task Request_Admitted_Before_Expiry_Times_Out_Before_Its_Blocked_Dispatcher_Can_Show_It()
    {
        await using GateFixture fixture = await GateFixture.CreateAsync();
        Dispatcher dispatcher = fixture.Dispatcher;
        Assert.True(fixture.Gate.OpenApprovals());
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherOperation blocking = dispatcher.InvokeAsync(() =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("测试 Dispatcher 未被释放。");
            }
        }, DispatcherPriority.Send);
        try
        {
            await entered.Task.WaitAsync(Guard);
            LocalApprovalRequest request = Request(Guid.NewGuid()) with
            {
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(1),
            };
            Task<LocalApprovalDecision> decision = fixture.Request(request);
            Assert.False(decision.IsCompleted); // 已登记而不是在入口直接超时。
            TimeSpan remaining = request.ExpiresAt - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(50);
            await Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero).WaitAsync(Guard);
            Assert.False(decision.IsCompleted);
            release.Set();
            await blocking.Task.WaitAsync(Guard);
            await AssertDecisionAsync(decision, request, LocalApprovalOutcome.TimedOut);
            await dispatcher.InvokeAsync(() => Assert.Empty(ApprovalWindows(dispatcher)),
                DispatcherPriority.ContextIdle).Task.WaitAsync(Guard);
        }
        finally
        {
            release.Set();
            await blocking.Task.WaitAsync(Guard);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Blocked_Async_Cancellation_Callback_Does_Not_Block_Ui_Requests_Or_Let_Manual_Click_Approve()
    {
        await using GateFixture fixture = await GateFixture.CreateAsync();
        Dispatcher dispatcher = fixture.Dispatcher;
        Assert.True(fixture.Gate.OpenApprovals());
        using CancellationTokenSource cancellation = new();
        LocalApprovalRequest old = Request(Guid.NewGuid());
        Task<LocalApprovalDecision> first = fixture.Request(old, cancellation.Token);
        Button approve = await dispatcher.InvokeAsync(() =>
        {
            Window window = Assert.Single(ApprovalWindows(dispatcher));
            var controls = Controls(window);
            controls.ViewOnly.IsChecked = true;
            Assert.True(controls.Approve.IsEnabled);
            return controls.Approve;
        }).Task.WaitAsync(Guard);

        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // 后注册的测试回调先于 Gate 的取消回调执行，故意让 CancelAsync 尚未完成。
        using CancellationTokenRegistration held = cancellation.Token.Register(() =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("测试取消回调未被释放。");
            }
        });
        Task? cancelling = null;
        Task<LocalApprovalDecision>? second = null;
        LocalApprovalRequest next = Request(Guid.NewGuid());
        try
        {
            cancelling = cancellation.CancelAsync();
            await entered.Task.WaitAsync(Guard);
            // 点击真实批准按钮并从同一 STA 调用 Gate；均不能等待被阻塞的取消回调。
            second = await dispatcher.InvokeAsync(() =>
            {
                Click(approve);
                return fixture.Request(next);
            }).Task.WaitAsync(Guard);
            Assert.False(cancelling.IsCompleted);
            Assert.False(second.IsCompleted);
            fixture.Gate.BlockApprovals();
            await AssertDecisionAsync(second, next, LocalApprovalOutcome.Unavailable);
        }
        finally
        {
            release.Set();
            if (cancelling is not null) { await cancelling.WaitAsync(Guard); }
        }
        await AssertDecisionAsync(first, old, LocalApprovalOutcome.Cancelled);
    }

    [Fact(Timeout = 60_000)]
    public async Task Stop_Waits_For_Real_Window_Closed_Even_Without_Transport_Host()
    {
        await using GateFixture fixture = await GateFixture.CreateAsync();
        string root = Path.Combine(Path.GetTempPath(), $"LanRemote-approval-stop-{Guid.NewGuid():N}");
        AppPaths paths = AppPaths.FromRootDirectory(root);
        DpapiSecretVault vault = new(paths);
        using DeviceCertificateService certificates = new(vault);
        DeviceIdentityService identity = new(vault, certificates);
        NeverRefreshBindings bindings = new();
        HostRuntimeService runtime = new(paths, identity, certificates,
            new DpapiAccessSecretStore(vault), bindings, new SubnetPolicy(bindings),
            fixture.Gate, NullLogger<HostRuntimeService>.Instance);
        Assert.True(runtime.OpenApprovals());
        LocalApprovalRequest request = Request(Guid.NewGuid());
        Task<LocalApprovalDecision> decision = fixture.Request(request);
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        (Window window, Button staleApprove) = await fixture.Dispatcher.InvokeAsync(() =>
        {
            Window shown = Assert.Single(ApprovalWindows(fixture.Dispatcher));
            var controls = Controls(shown);
            controls.ViewOnly.IsChecked = true;
            Assert.True(controls.Approve.IsEnabled);
            shown.Closed += (_, _) => closed.TrySetResult();
            Assert.True(shown.IsVisible);
            return (shown, controls.Approve);
        }, DispatcherPriority.ContextIdle).Task.WaitAsync(Guard);

        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource sample = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> visibleWhileStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherOperation blocker = fixture.Dispatcher.InvokeAsync(() =>
        {
            entered.TrySetResult();
            if (!sample.Task.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("测试未请求 STA 窗口可见性快照。");
            }
            visibleWhileStopped.TrySetResult(window.IsVisible);
            if (!release.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("测试 Dispatcher 未被释放。");
            }
        }, DispatcherPriority.Send);
        Task? stop = null;
        Task? repeated = null;
        try
        {
            await entered.Task.WaitAsync(Guard);
            TaskCompletionSource<Task> launched = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task initiating = Task.Run(() =>
            {
                try { launched.TrySetResult(runtime.StopAndDisposeAsync()); }
                catch (Exception error) { launched.TrySetException(error); }
            });
            stop = await launched.Task.WaitAsync(Guard);
            await initiating.WaitAsync(Guard);
            await AssertDecisionAsync(decision, request, LocalApprovalOutcome.Unavailable);
            Assert.False(runtime.ApprovalsAvailable);
            Assert.False(stop.IsCompleted);
            Assert.False(runtime.IsFullyDrained);
            Task blockedJoin = fixture.Gate.JoinBlockedApprovalsAsync();
            runtime.BeginShutdown();
            Assert.Same(blockedJoin, fixture.Gate.JoinBlockedApprovalsAsync());
            repeated = runtime.StopAndDisposeAsync();
            Assert.Same(stop, repeated);
            Assert.False(repeated.IsCompleted);
            sample.TrySetResult();
            Assert.True(await visibleWhileStopped.Task.WaitAsync(Guard));
            Assert.False(closed.Task.IsCompleted);
            Assert.False(stop.IsCompleted);
            Assert.False(runtime.OpenApprovals());

            release.Set();
            await blocker.Task.WaitAsync(Guard);
            await closed.Task.WaitAsync(Guard);
            await stop.WaitAsync(Guard);
            await repeated.WaitAsync(Guard);
            Assert.True(runtime.IsFullyDrained);
            Assert.False(runtime.ApprovalsAvailable);
            await fixture.Dispatcher.InvokeAsync(() =>
            {
                Assert.False(window.IsVisible);
                Assert.Empty(ApprovalWindows(fixture.Dispatcher));
                Click(staleApprove);
            }, DispatcherPriority.ContextIdle).Task.WaitAsync(Guard);
            await AssertDecisionAsync(decision, request, LocalApprovalOutcome.Unavailable);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            sample.TrySetResult();
            release.Set();
            await blocker.Task.WaitAsync(Guard);
            if (stop is not null) await stop.WaitAsync(Guard);
            if (repeated is not null) await repeated.WaitAsync(Guard);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Aborted_Show_Operation_Faults_Original_Stop_Report()
    {
        await using GateFixture fixture = await GateFixture.CreateAsync();
        string root = Path.Combine(Path.GetTempPath(), $"LanRemote-approval-abort-{Guid.NewGuid():N}");
        AppPaths paths = AppPaths.FromRootDirectory(root);
        DpapiSecretVault vault = new(paths);
        using DeviceCertificateService certificates = new(vault);
        DeviceIdentityService identity = new(vault, certificates);
        NeverRefreshBindings bindings = new();
        HostRuntimeService runtime = new(paths, identity, certificates,
            new DpapiAccessSecretStore(vault), bindings, new SubnetPolicy(bindings),
            fixture.Gate, NullLogger<HostRuntimeService>.Instance);
        Assert.True(runtime.OpenApprovals());
        Dispatcher dispatcher = fixture.Dispatcher;
        TaskCompletionSource<DispatcherOperation> showPosted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        int capture = 0;
        DispatcherHookEventHandler observe = (_, args) =>
        {
            if (Volatile.Read(ref capture) != 0)
            {
                showPosted.TrySetResult(args.Operation);
            }
        };
        DispatcherHooks hooks = await dispatcher.InvokeAsync(() =>
        {
            DispatcherHooks current = dispatcher.Hooks;
            current.OperationPosted += observe;
            return current;
        }).Task.WaitAsync(Guard);
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherOperation blocker = dispatcher.InvokeAsync(() =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("测试 Dispatcher 未被释放。");
            }
        }, DispatcherPriority.Send);
        Task? stop = null;
        try
        {
            await entered.Task.WaitAsync(Guard);
            Volatile.Write(ref capture, 1);
            LocalApprovalRequest request = Request(Guid.NewGuid());
            Task<LocalApprovalDecision> decision = fixture.Request(request);
            DispatcherOperation show = await showPosted.Task.WaitAsync(Guard);
            Assert.False(decision.IsCompleted);
            stop = Task.Run(runtime.StopAndDisposeAsync);
            await AssertDecisionAsync(decision, request, LocalApprovalOutcome.Unavailable);
            Assert.False(stop.IsCompleted);
            Assert.True(show.Abort());
            await Assert.ThrowsAsync<InvalidOperationException>(() => stop.WaitAsync(Guard));
            Assert.False(runtime.IsFullyDrained);
            Assert.False(runtime.OpenApprovals());
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            release.Set();
            await blocker.Task.WaitAsync(Guard);
            await dispatcher.InvokeAsync(() => hooks.OperationPosted -= observe).Task.WaitAsync(Guard);
            if (stop is not null) await Assert.ThrowsAsync<InvalidOperationException>(() => stop.WaitAsync(Guard));
        }
        await dispatcher.InvokeAsync(() => Assert.Empty(ApprovalWindows(dispatcher)),
            DispatcherPriority.ContextIdle).Task.WaitAsync(Guard);
    }

    private static LocalApprovalRequest Request(Guid requestId) => new(
        requestId, Guid.NewGuid(), Guid.NewGuid(), IPAddress.Loopback, 43210,
        Guid.NewGuid(), "本机竞态测试客户端", SessionPermission.ViewOnly,
        "A1B2C3", DateTimeOffset.UtcNow.AddMinutes(1));

    private static async Task AssertDecisionAsync(Task<LocalApprovalDecision> task,
        LocalApprovalRequest request, LocalApprovalOutcome outcome)
    {
        LocalApprovalDecision result = await task.WaitAsync(Guard);
        Assert.Equal(request.RequestId, result.RequestId);
        Assert.Equal(outcome, result.Outcome);
        Assert.Null(result.GrantedPermission);
    }

    private static Window[] ApprovalWindows(Dispatcher dispatcher) =>
        PresentationSource.CurrentSources.Cast<PresentationSource>()
            .Select(source => source.RootVisual).OfType<Window>()
            .Where(window => ReferenceEquals(window.Dispatcher, dispatcher) &&
                window.Title == ApprovalTitle && window.IsVisible).ToArray();

    private static bool ShowsClient(Window window, string name) =>
        window.Content is StackPanel content && content.Children.OfType<TextBlock>()
            .Any(line => line.Text == $"自称设备名：{name}");

    private static (RadioButton ViewOnly, Button Approve, Button Deny) Controls(Window window)
    {
        StackPanel content = Assert.IsType<StackPanel>(window.Content);
        RadioButton viewOnly = Assert.Single(content.Children.OfType<RadioButton>());
        StackPanel buttons = Assert.Single(content.Children.OfType<StackPanel>());
        Button approve = Assert.Single(buttons.Children.OfType<Button>(), button =>
            Equals(button.Content, "批准仅查看"));
        Button deny = Assert.Single(buttons.Children.OfType<Button>(), button =>
            Equals(button.Content, "拒绝"));
        return (viewOnly, approve, deny);
    }

    private static void Click(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

    private sealed class NeverRefreshBindings : INetworkBindingProvider
    {
        public IReadOnlyList<NetworkBinding> GetBindings() =>
            throw new InvalidOperationException("审批竞态测试不得枚举网卡。");

        public void Refresh() => throw new InvalidOperationException("审批竞态测试不得刷新网卡。");
    }

    private sealed class GateFixture(StaDispatcherFixture sta, Dispatcher dispatcher) : IAsyncDisposable
    {
        private readonly List<Task<LocalApprovalDecision>> _decisions = new();
        public Dispatcher Dispatcher { get; } = dispatcher;
        public WpfLocalApprovalGate Gate { get; } = new(dispatcher, NullLogger<WpfLocalApprovalGate>.Instance);

        public static async Task<GateFixture> CreateAsync()
        {
            StaDispatcherFixture sta = new();
            try { return new GateFixture(sta, await sta.Ready.WaitAsync(Guard)); }
            catch
            {
                await sta.DisposeAsync();
                throw;
            }
        }

        public Task<LocalApprovalDecision> Request(LocalApprovalRequest request,
            CancellationToken cancellationToken = default)
        {
            Task<LocalApprovalDecision> decision = Gate.RequestApprovalAsync(request, cancellationToken).AsTask();
            _decisions.Add(decision);
            return decision;
        }

        public async ValueTask DisposeAsync()
        {
            Gate.BlockApprovals();
            try
            {
                await Task.WhenAll(_decisions).WaitAsync(Guard);
            }
            finally
            {
                try
                {
                    await Dispatcher.InvokeAsync(() => Assert.Empty(ApprovalWindows(Dispatcher)),
                        DispatcherPriority.ContextIdle).Task.WaitAsync(Guard);
                }
                finally { await sta.DisposeAsync(); }
            }
        }
    }
}
