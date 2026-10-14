using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Threading;
using LanRemote.App;
using LanRemote.Core.Models;
using LanRemote.Transport;

namespace LanRemote.Acceptance.Tests;

// 只验证WPF目录呈现生命周期；快照由反射夹具构造，不冒充认证或真实Control reader证据。
[Collection(AcceptanceStageFiveCollection.Name)]
public sealed class ProductDisplayCatalogWindowTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact(Timeout = 20_000)]
    public Task Window_Uses_One_Timer_And_Replaces_Only_When_The_Snapshot_Instance_Changes() => OnSta(async view =>
    {
        DisplayCatalogSnapshot? current = null;
        int calls = 0;
        Start(view, () => { calls++; return current; });
        DispatcherTimer timer = Assert.IsType<DispatcherTimer>(Field(view, "_catalogTimer"));
        Assert.True(timer.IsEnabled);
        Assert.Equal(TimeSpan.FromMilliseconds(250), timer.Interval);
        Assert.Null(Items(view).ItemsSource);
        current = Snapshot(7, 4);
        long started = Stopwatch.GetTimestamp();
        while (Items(view).Items.Count == 0)
        {
            Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        Assert.True(calls >= 2, "实际DispatcherTimer必须触发读取，不能只测手动调用。");
        object firstSource = Items(view).ItemsSource;
        Tick(view);
        Assert.Same(firstSource, Items(view).ItemsSource);
        Assert.Same(timer, Field(view, "_catalogTimer"));
        current = Snapshot(1, 9); // seq下降仍是新快照，不按元数据筛选。
        Tick(view);
        Assert.NotSame(firstSource, Items(view).ItemsSource);
        Assert.Equal(9, Assert.Single(Items(view).Items.Cast<DisplayInfo>()).Id.Value);
        current = null;
        Tick(view);
        Assert.Null(Items(view).ItemsSource);
        Assert.Same(timer, Field(view, "_catalogTimer"));
    });

    [Theory(Timeout = 20_000)]
    [InlineData("close")]
    [InlineData("stop")]
    [InlineData("ended-error")]
    [InlineData("ended-success")]
    public Task Window_Clears_Before_Drain_And_Late_Ticks_Cannot_Restore_A_Stopped_Catalog(string stop) => OnSta(view =>
    {
        int reads = 0;
        Start(view, () => { reads++; return Snapshot(7, 4); });
        DispatcherTimer timer = Assert.IsType<DispatcherTimer>(Field(view, "_catalogTimer"));
        Assert.Single(Items(view).Items);
        switch (stop)
        {
            case "close": view.Close(); Assert.True(view.IsVisible); break;
            case "stop": Invoke(view, "StopDisplayCatalog"); break;
            case "ended-error": view.SessionEnded("保留原诊断", true); Assert.True(view.IsVisible); break;
            case "ended-success": view.SessionEnded("正常结束", false); Assert.False(view.IsVisible); break;
        }
        Assert.False(timer.IsEnabled);
        Detached(view);
        int atStop = reads;
        Tick(view); // 模拟已排队但尚未执行的旧Tick。
        Assert.Equal(atStop, reads);
        Detached(view);
        Assert.Throws<InvalidOperationException>(() => Start(view, () => Snapshot(1, 9)));
        if (stop == "ended-error") Assert.Equal("保留原诊断", ((TextBlock)view.FindName("StatusText")).Text);
        return Task.CompletedTask;
    });

    [Theory(Timeout = 20_000)]
    [InlineData(false)]
    [InlineData(true)]
    public Task Window_Cancellation_Before_Or_During_Getter_Prevents_Publication(bool duringRead) => OnSta(view =>
    {
        using CancellationTokenSource stop = new();
        int reads = 0;
        if (!duringRead) stop.Cancel();
        Start(view, () => { reads++; stop.Cancel(); return Snapshot(7, 4); }, stop.Token);
        Assert.Equal(duringRead ? 1 : 0, reads);
        Detached(view);
        Tick(view);
        Detached(view);
        return Task.CompletedTask;
    });

    [Fact(Timeout = 20_000)]
    public Task Window_Stopping_Reentrantly_In_Getter_Cannot_Publish_The_Returned_Historical_Data() => OnSta(view =>
    {
        Start(view, () => { Invoke(view, "StopDisplayCatalog"); return Snapshot(7, 4); });
        Detached(view);
        return Task.CompletedTask;
    });

    [Fact(Timeout = 20_000)]
    public Task Window_Rejects_Rebinding_And_Old_Window_Tick_Does_Not_Affect_New_Window() => OnSta(first =>
    {
        Start(first, () => Snapshot(7, 4));
        Assert.Throws<InvalidOperationException>(() => Start(first, () => Snapshot(1, 8)));
        first.SessionEnded("旧会话诊断", true);
        RemoteViewWindow second = NewView();
        try
        {
            second.Show();
            Start(second, () => Snapshot(7, 9));
            Tick(first);
            Detached(first);
            Assert.Equal(9, Assert.Single(Items(second).Items.Cast<DisplayInfo>()).Id.Value);
            Assert.Equal("旧会话诊断", ((TextBlock)first.FindName("StatusText")).Text);
        }
        finally { second.SessionEnded("结束", false); }
        return Task.CompletedTask;
    });

    [Fact(Timeout = 20_000)]
    public Task Window_Drain_Evidence_Requires_Catalog_Binding_And_Data_To_Be_Released() => OnSta(view =>
    {
        Start(view, () => Snapshot(7, 4));
        Assert.False((bool)typeof(RemoteViewWindow).GetProperty("DisconnectHandlerReleased", Hidden)!.GetValue(view)!);
        view.SessionEnded("诊断", true);
        Assert.True((bool)typeof(RemoteViewWindow).GetProperty("DisconnectHandlerReleased", Hidden)!.GetValue(view)!);
        Items(view).ItemsSource = Snapshot(1, 9).Displays; // 仅测试证书不能无视UI残留。
        Assert.False((bool)typeof(RemoteViewWindow).GetProperty("DisconnectHandlerReleased", Hidden)!.GetValue(view)!);
        Items(view).ItemsSource = null;
        return Task.CompletedTask;
    });

    private static DisplayCatalogSnapshot Snapshot(long seq, int id)
    {
        ReadOnlyCollection<DisplayInfo> rows = Array.AsReadOnly(new[] {
            new DisplayInfo(new DisplayId(id), "屏幕 <不作为标记>", -1920, -100, 1920, 1080, true) });
        return (DisplayCatalogSnapshot)Activator.CreateInstance(typeof(DisplayCatalogSnapshot), Hidden,
            binder: null, args: [seq, 123L, rows], culture: null)!;
    }

    private static void Start(RemoteViewWindow view, Func<DisplayCatalogSnapshot?> read, CancellationToken stop = default) =>
        Invoke(view, "StartDisplayCatalog", read, stop);
    private static void Tick(RemoteViewWindow view) => Invoke(view, "OnCatalogTick", null, EventArgs.Empty);
    private static ItemsControl Items(RemoteViewWindow view) => (ItemsControl)view.FindName("DisplayCatalogItems");
    private static object? Field(RemoteViewWindow view, string name) => typeof(RemoteViewWindow).GetField(name, Hidden)!.GetValue(view);
    private static void Detached(RemoteViewWindow view)
    {
        Assert.Null(Items(view).ItemsSource);
        foreach (string name in new[] { "_catalogTimer", "_readCatalog", "_displayCatalog" }) Assert.Null(Field(view, name));
        Assert.Equal(default, (CancellationToken)Field(view, "_catalogStop")!);
        Assert.True((bool)Field(view, "_catalogStopped")!);
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
