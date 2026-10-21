// 只验证窗口侧切屏的 WPF 交互生命周期：目录快照由反射夹具构造，回执由测试委托按 TCS 时机提供；
// 不建立任何真实网络连接，不代表 Host 侧发送、物理 scanout 或双机验收。

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LanRemote.App;
using LanRemote.Core.Models;
using LanRemote.Transport;

namespace LanRemote.Acceptance.Tests;

// 只验证 WPF 切屏交互生命周期；回执由委托提供，非真实网络证据。
[Collection(AcceptanceStageFiveCollection.Name)]
public sealed class ProductDisplaySwitchWindowTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

    [Fact(Timeout = 20_000)]
    public Task Window_Switch_Buttons_Stay_Disabled_Until_Switching_Is_Enabled() => OnSta(async view =>
    {
        DisplayCatalogSnapshot? current = Snapshot(7, 4);
        Start(view, () => current);
        Button button = await SwitchButtonAsync(view);
        Assert.False(button.IsEnabled, "未启用切屏时目录项按钮必须保持 XAML 默认禁用。");
        Assert.Equal("切换：未请求。", SwitchStatus(view));
        Assert.Null(Field(view, "_requestSwitch"));

        StartSwitch(view, Silent(), default);
        Assert.True(button.IsEnabled, "目录绑定后启用切屏必须刷新既有容器内按钮。");
        Assert.NotNull(Field(view, "_requestSwitch"));
        Assert.Equal(default, (CancellationToken)Field(view, "_switchStop")!);
        Assert.True((bool)Field(view, "_switchStarted")!);
    });

    [Fact(Timeout = 20_000)]
    public Task Window_Switch_Button_Click_Requests_Once_And_Publishes_Receipt() => OnSta(async view =>
    {
        TaskCompletionSource<DisplayChangedFrame?> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<DisplayId> called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        Start(view, () => Snapshot(7, 4));
        Button button = await SwitchButtonAsync(view);
        Assert.False(button.IsEnabled, "启用切屏前目录项按钮必须保持禁用。");
        StartSwitch(view, (id, token) =>
        {
            Interlocked.Increment(ref calls);
            called.TrySetResult(id);
            return gate.Task;
        }, default);
        Assert.True(button.IsEnabled);

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        DisplayId requested = await called.Task.WaitAsync(Guard);
        Assert.Equal(new DisplayId(4), requested);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Contains("正在请求切换屏幕 4", SwitchStatus(view));
        Assert.True((bool)Field(view, "_switching")!);

        DisplayChangedFrame receipt = Receipt();
        try
        {
            gate.SetResult(receipt);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Contains("已收到切换回执", SwitchStatus(view));
            Assert.Contains($"屏幕 {receipt.DisplayId.Value}", SwitchStatus(view));
            Assert.Contains($"首帧 {receipt.FirstFrameId}", SwitchStatus(view));
            Assert.Contains($"{receipt.Width}×{receipt.Height}", SwitchStatus(view));
            Assert.Contains("回执不代表物理输出", SwitchStatus(view));
            Assert.False((bool)Field(view, "_switching")!);
            Assert.True(button.IsEnabled, "回执落定后忙状态必须解除并刷新按钮。");
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally { gate.TrySetResult(receipt); }
    });

    [Fact(Timeout = 20_000)]
    public Task Window_Switch_Busy_State_Blocks_Second_Click_And_Direct_Request() => OnSta(async view =>
    {
        TaskCompletionSource<DisplayChangedFrame?> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<DisplayId> called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        Start(view, () => Snapshot(7, 4));
        Button button = await SwitchButtonAsync(view);
        Assert.False(button.IsEnabled);
        StartSwitch(view, (id, token) =>
        {
            Interlocked.Increment(ref calls);
            called.TrySetResult(id);
            return gate.Task;
        }, default);
        Assert.True(button.IsEnabled);

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await called.Task.WaitAsync(Guard);
        Assert.Contains("正在请求切换屏幕 4", SwitchStatus(view));

        // 忙：再次点击与直接请求都不得发起第二次回调。
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        DisplayChangedFrame? busy = await Request(view, new DisplayId(4)).WaitAsync(Guard);
        Assert.Null(busy);
        Assert.Equal("当前不能请求切换屏幕。", SwitchStatus(view));
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.True((bool)Field(view, "_switching")!);

        DisplayChangedFrame receipt = Receipt();
        try
        {
            gate.SetResult(receipt);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Contains("已收到切换回执", SwitchStatus(view));
            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.False((bool)Field(view, "_switching")!);
        }
        finally { gate.TrySetResult(receipt); }
    });

    [Fact(Timeout = 20_000)]
    public Task Window_Switch_Null_Receipt_Reports_Unconfirmed() => OnSta(async view =>
    {
        TaskCompletionSource<DisplayChangedFrame?> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<DisplayId> called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        Start(view, () => Snapshot(7, 4));
        Button button = await SwitchButtonAsync(view);
        Assert.False(button.IsEnabled);
        StartSwitch(view, (id, token) =>
        {
            Interlocked.Increment(ref calls);
            called.TrySetResult(id);
            return gate.Task;
        }, default);
        Assert.True(button.IsEnabled);

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await called.Task.WaitAsync(Guard);
        Assert.Contains("正在请求切换屏幕 4", SwitchStatus(view));
        try
        {
            gate.SetResult(null);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal("切换未完成：远端未确认新画面。", SwitchStatus(view));
            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.False((bool)Field(view, "_switching")!);
        }
        finally { gate.TrySetResult(null); }
    });

    [Theory(Timeout = 20_000)]
    [InlineData("InvalidOperationException", "切换请求被拒绝：目标不在当前目录或会话暂不可用。")]
    [InlineData("IOException", "切换请求失败；会话可能已断开。")]
    [InlineData("OperationCanceledException", "切换请求已取消。")]
    public Task Window_Switch_Callback_Failures_Report_Distinct_Status(string failure, string expected) => OnSta(async view =>
    {
        Exception error = failure switch
        {
            "InvalidOperationException" => new InvalidOperationException("目标不在当前目录"),
            "IOException" => new IOException("连接已断开"),
            _ => new OperationCanceledException("请求已取消"),
        };
        int calls = 0;
        Start(view, () => Snapshot(7, 4));
        StartSwitch(view, async (id, token) =>
        {
            Interlocked.Increment(ref calls);
            await Task.CompletedTask;
            throw error;
        }, default);
        await SwitchButtonAsync(view);

        DisplayChangedFrame? receipt = await Request(view, new DisplayId(4)).WaitAsync(Guard);
        Assert.Null(receipt);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal(expected, SwitchStatus(view));
        Assert.False((bool)Field(view, "_switching")!);
    });

    [Fact(Timeout = 20_000)]
    public Task Window_Switch_Request_Outside_Catalog_Is_Rejected_Without_Callback() => OnSta(async view =>
    {
        int calls = 0;
        Start(view, () => Snapshot(7, 4));
        StartSwitch(view, (id, token) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<DisplayChangedFrame?>(Receipt());
        }, default);
        await SwitchButtonAsync(view);

        // 目录只含屏幕 4：请求屏幕 5 在窗口内按当前目录快照拒绝。
        DisplayChangedFrame? rejected = await Request(view, new DisplayId(5)).WaitAsync(Guard);
        Assert.Null(rejected);
        Assert.Equal(0, Volatile.Read(ref calls));
        Assert.Equal("当前不能请求切换屏幕。", SwitchStatus(view));
    });

    [Fact(Timeout = 20_000)]
    public Task Window_Switch_Enable_Requires_Catalog_And_Is_Single_Use() => OnSta(view =>
    {
        // 未绑定目录显示就启用切屏：拒绝。
        Assert.Throws<InvalidOperationException>(() => StartSwitch(view, Silent(), default));

        Start(view, () => Snapshot(7, 4));
        StartSwitch(view, Silent(), default);
        Assert.NotNull(Field(view, "_requestSwitch"));
        Assert.True((bool)Field(view, "_switchStarted")!);
        // 同一窗口重复启用：拒绝。
        Assert.Throws<InvalidOperationException>(() => StartSwitch(view, Silent(), default));

        Invoke(view, "StopDisplayCatalog");
        // 目录停止后也不能再启用切屏。
        Assert.Throws<InvalidOperationException>(() => StartSwitch(view, Silent(), default));
        return Task.CompletedTask;
    });

    [Fact(Timeout = 20_000)]
    public Task Window_StopDisplayCatalog_Releases_Switch_Binding() => OnSta(async view =>
    {
        int calls = 0;
        Start(view, () => Snapshot(7, 4));
        Button button = await SwitchButtonAsync(view);
        Assert.False(button.IsEnabled);
        StartSwitch(view, (id, token) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<DisplayChangedFrame?>(Receipt());
        }, default);
        Assert.True(button.IsEnabled);

        Invoke(view, "StopDisplayCatalog");
        Tick(view); // 已排队但尚未执行的旧 Tick 不能恢复切屏绑定。
        Assert.Equal("切换：已随目录解除绑定。", SwitchStatus(view));
        Detached(view);
        Assert.Null(Items(view).ItemsSource);
        Assert.Empty(Items(view).Items);

        // 目录已解除绑定：即使测试再塞残留目录，新容器按钮也保持禁用。
        Items(view).ItemsSource = Snapshot(1, 9).Displays;
        Button fresh = await SwitchButtonAsync(view);
        Assert.False(fresh.IsEnabled, "解绑后不得再启用任何目录项按钮。");
        Items(view).ItemsSource = null;

        // 解绑后再请求：不发起回调，只写拒绝文本。
        DisplayChangedFrame? rejected = await Request(view, new DisplayId(4)).WaitAsync(Guard);
        Assert.Null(rejected);
        Assert.Equal(0, Volatile.Read(ref calls));
        Assert.Equal("当前不能请求切换屏幕。", SwitchStatus(view));
    });

    [Fact(Timeout = 20_000)]
    public Task Window_SessionEnded_Releases_Switch_Binding_And_Keeps_Drain_Evidence() => OnSta(async view =>
    {
        int calls = 0;
        Start(view, () => Snapshot(7, 4));
        StartSwitch(view, (id, token) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<DisplayChangedFrame?>(Receipt());
        }, default);
        await SwitchButtonAsync(view);

        view.SessionEnded("远端诊断保留", true);
        Assert.True(view.IsVisible, "带错结束时窗口必须保留诊断。");
        Assert.Equal("切换：已随目录解除绑定。", SwitchStatus(view));
        Detached(view);
        Assert.True(DisconnectHandlerReleased(view));

        // 仅测试残留不能伪造排空证据。
        Items(view).ItemsSource = Snapshot(1, 9).Displays;
        Assert.False(DisconnectHandlerReleased(view));
        Button stale = await SwitchButtonAsync(view);
        Assert.False(stale.IsEnabled, "会话结束后不得再启用任何目录项按钮。");
        Items(view).ItemsSource = null;

        DisplayChangedFrame? rejected = await Request(view, new DisplayId(4)).WaitAsync(Guard);
        Assert.Null(rejected);
        Assert.Equal(0, Volatile.Read(ref calls));
        Assert.Equal("当前不能请求切换屏幕。", SwitchStatus(view));
    });

    [Fact(Timeout = 20_000)]
    public Task Window_Switch_Cancelled_Stop_Token_Blocks_Requests_But_Not_Enable() => OnSta(async view =>
    {
        using CancellationTokenSource stop = new();
        stop.Cancel();
        int calls = 0;
        Start(view, () => Snapshot(7, 4));
        // 启用不检查取消：已取消的 stop 仍允许绑定。
        StartSwitch(view, (id, token) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<DisplayChangedFrame?>(Receipt());
        }, stop.Token);
        Assert.True((bool)Field(view, "_switchStarted")!);
        Button button = await SwitchButtonAsync(view);
        Assert.False(button.IsEnabled, "stop 已取消时不得启用目录项按钮。");

        DisplayChangedFrame? rejected = await Request(view, new DisplayId(4)).WaitAsync(Guard);
        Assert.Null(rejected);
        Assert.Equal(0, Volatile.Read(ref calls));
        Assert.Equal("当前不能请求切换屏幕。", SwitchStatus(view));

        // 点击同样被核心拒绝，不发起回调。
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(0, Volatile.Read(ref calls));
        Assert.Equal("当前不能请求切换屏幕。", SwitchStatus(view));
    });

    [Fact(Timeout = 20_000)]
    public Task Window_Switch_Click_Ignores_Non_Display_DataContext() => OnSta(view =>
    {
        int calls = 0;
        Start(view, () => Snapshot(7, 4));
        StartSwitch(view, (id, token) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<DisplayChangedFrame?>(Receipt());
        }, default);

        // sender 非 Button：直接忽略。
        Invoke(view, "OnSwitchDisplayClick", new object(), new RoutedEventArgs(Button.ClickEvent));
        // Button 但 DataContext 为空：直接忽略。
        Invoke(view, "OnSwitchDisplayClick", new Button(), new RoutedEventArgs(Button.ClickEvent));
        // Button 但 DataContext 非 DisplayInfo：直接忽略。
        Invoke(view, "OnSwitchDisplayClick", new Button { DataContext = "不是 DisplayInfo" },
            new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(0, Volatile.Read(ref calls));
        Assert.Equal("切换：未请求。", SwitchStatus(view));
        return Task.CompletedTask;
    });

    [Fact(Timeout = 20_000)]
    public Task Window_Disconnect_Evidence_Stays_False_While_Switch_Binding_Alive() => OnSta(view =>
    {
        Start(view, () => Snapshot(7, 4));
        StartSwitch(view, Silent(), default);
        Assert.False(DisconnectHandlerReleased(view), "仅绑定目录与切屏、会话未结束不得算排空。");

        Invoke(view, "StopDisplayCatalog");
        Assert.False(DisconnectHandlerReleased(view), "切屏已解绑但会话未结束仍不得算排空。");

        Items(view).ItemsSource = Snapshot(1, 9).Displays; // 仅测试残留照旧使之为 false。
        Assert.False(DisconnectHandlerReleased(view));
        Items(view).ItemsSource = null;
        return Task.CompletedTask;
    });

    [Fact(Timeout = 20_000)]
    public Task Window_Switch_Receipt_Text_Contains_Real_Receipt_Fields() => OnSta(async view =>
    {
        TaskCompletionSource<DisplayChangedFrame?> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<DisplayId> called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        DisplayChangedFrame receipt = Receipt(firstFrameId: 9_007_199_254_740_993UL, width: 2560, height: 1440);
        Start(view, () => Snapshot(7, 4));
        Button button = await SwitchButtonAsync(view);
        Assert.False(button.IsEnabled);
        StartSwitch(view, (id, token) =>
        {
            Interlocked.Increment(ref calls);
            called.TrySetResult(id);
            return gate.Task;
        }, default);
        Assert.True(button.IsEnabled);

        try
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await called.Task.WaitAsync(Guard);
            gate.SetResult(receipt);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Contains("已收到切换回执", SwitchStatus(view));
            Assert.Contains($"首帧 {receipt.FirstFrameId}", SwitchStatus(view));
            Assert.Contains($"{receipt.Width}×{receipt.Height}", SwitchStatus(view));
            Assert.Contains($"屏幕 {receipt.DisplayId.Value}", SwitchStatus(view));
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally { gate.TrySetResult(receipt); }
    });

    [Fact(Timeout = 20_000)]
    public Task Window_Switch_Second_Request_Succeeds_After_First_Receipt() => OnSta(async view =>
    {
        TaskCompletionSource<DisplayChangedFrame?> first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<DisplayChangedFrame?> second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<DisplayId> called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        Start(view, () => Snapshot(7, 4));
        Button button = await SwitchButtonAsync(view);
        Assert.False(button.IsEnabled);
        StartSwitch(view, (id, token) =>
        {
            int number = Interlocked.Increment(ref calls);
            if (number == 1) called.TrySetResult(id);
            return number == 1 ? first.Task : second.Task;
        }, default);
        Assert.True(button.IsEnabled);

        DisplayChangedFrame firstReceipt = Receipt(firstFrameId: 111UL, width: 1920, height: 1080);
        DisplayChangedFrame secondReceipt = Receipt(firstFrameId: 222UL, width: 1280, height: 720);
        try
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await called.Task.WaitAsync(Guard);
            Assert.Contains("正在请求切换屏幕 4", SwitchStatus(view));
            first.SetResult(firstReceipt);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Contains($"首帧 {firstReceipt.FirstFrameId}", SwitchStatus(view));

            // 忙状态解除后再次点击：新回调下再次发起并更新回执文本。
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains("正在请求切换屏幕 4", SwitchStatus(view));
            second.SetResult(secondReceipt);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Contains($"首帧 {secondReceipt.FirstFrameId}", SwitchStatus(view));
            Assert.Equal(2, Volatile.Read(ref calls));
            Assert.True(button.IsEnabled);
        }
        finally
        {
            first.TrySetResult(firstReceipt);
            second.TrySetResult(secondReceipt);
        }
    });

    private static DisplayCatalogSnapshot Snapshot(long seq, int id)
    {
        ReadOnlyCollection<DisplayInfo> rows = Array.AsReadOnly(new[] {
            new DisplayInfo(new DisplayId(id), "屏幕 <不作为标记>", -1920, -100, 1920, 1080, true) });
        return (DisplayCatalogSnapshot)Activator.CreateInstance(typeof(DisplayCatalogSnapshot), Hidden,
            binder: null, args: [seq, 123L, rows], culture: null)!;
    }

    // 回执由测试委托构造：字段值只用于验证状态文本，不构成任何网络证据。
    private static DisplayChangedFrame Receipt(ulong firstFrameId = 424242UL, int width = 3840, int height = 2160) =>
        new(Guid.NewGuid(), 11L, 987_654_321L, 5L, 7L, new DisplayId(4), firstFrameId, width, height);

    private static Func<DisplayId, CancellationToken, Task<DisplayChangedFrame?>> Silent() =>
        (id, token) => Task.FromResult<DisplayChangedFrame?>(null);

    private static void Start(RemoteViewWindow view, Func<DisplayCatalogSnapshot?> read, CancellationToken stop = default) =>
        Invoke(view, "StartDisplayCatalog", read, stop);
    private static void StartSwitch(RemoteViewWindow view,
        Func<DisplayId, CancellationToken, Task<DisplayChangedFrame?>> requestSwitch, CancellationToken stop) =>
        Invoke(view, "StartDisplaySwitching", requestSwitch, stop);
    private static Task<DisplayChangedFrame?> Request(RemoteViewWindow view, DisplayId displayId)
    {
        try
        {
            return (Task<DisplayChangedFrame?>)typeof(RemoteViewWindow)
                .GetMethod("RequestDisplaySwitchAsync", Hidden)!.Invoke(view, [displayId])!;
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }
    private static void Tick(RemoteViewWindow view) => Invoke(view, "OnCatalogTick", null, EventArgs.Empty);
    private static ItemsControl Items(RemoteViewWindow view) => (ItemsControl)view.FindName("DisplayCatalogItems");
    private static string SwitchStatus(RemoteViewWindow view) => ((TextBlock)view.FindName("SwitchStatusText")).Text;
    private static object? Field(RemoteViewWindow view, string name) => typeof(RemoteViewWindow).GetField(name, Hidden)!.GetValue(view);
    private static bool DisconnectHandlerReleased(RemoteViewWindow view) =>
        (bool)typeof(RemoteViewWindow).GetProperty("DisconnectHandlerReleased", Hidden)!.GetValue(view)!;
    private static void Detached(RemoteViewWindow view)
    {
        Assert.Null(Items(view).ItemsSource);
        foreach (string name in new[] { "_catalogTimer", "_readCatalog", "_displayCatalog", "_requestSwitch" })
            Assert.Null(Field(view, name));
        Assert.Equal(default, (CancellationToken)Field(view, "_catalogStop")!);
        Assert.Equal(default, (CancellationToken)Field(view, "_switchStop")!);
        Assert.True((bool)Field(view, "_catalogStopped")!);
        Assert.True((bool)Field(view, "_switchStarted")!);
        Assert.True((bool)Field(view, "_switchStopped")!);
        Assert.False((bool)Field(view, "_switching")!);
    }
    private static void Invoke(RemoteViewWindow view, string name, params object?[] args)
    {
        try { typeof(RemoteViewWindow).GetMethod(name, Hidden)!.Invoke(view, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
    }
    private static RemoteViewWindow NewView()
    {
        Assert.True(ConnectionTarget.TryCreate(Guid.NewGuid(), IPAddress.Loopback, 1, new string('A', 64), out ConnectionTarget? target));
        return new RemoteViewWindow(target!, () => { }) { ShowActivated = false, ShowInTaskbar = false };
    }
    // 容器在布局后才生成；缺失容器不是业务失败，用有界等待显式断言其出现。
    private static async Task<Button> SwitchButtonAsync(RemoteViewWindow view)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            view.UpdateLayout();
            if (Items(view).ItemContainerGenerator.ContainerFromIndex(0) is { } container &&
                FindVisualChild<Button>(container) is { } button)
            {
                return button;
            }
            Assert.True(Stopwatch.GetElapsedTime(started) < Guard, "目录项容器或切换按钮未在有界等待内生成。");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
    }
    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }
    private static async Task OnSta(Func<RemoteViewWindow, Task> verify)
    {
        await using StaDispatcherFixture sta = new();
        Dispatcher dispatcher = await sta.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        Task run = dispatcher.InvokeAsync(async () =>
        {
            RemoteViewWindow view = NewView();
            view.Show();
            try { await verify(view); }
            finally { view.SessionEnded("夹具结束", false); }
        }).Task.Unwrap();
        try { await run.WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { await run.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); }
    }
}
