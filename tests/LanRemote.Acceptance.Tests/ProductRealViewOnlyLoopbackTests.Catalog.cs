using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using LanRemote.App;

namespace LanRemote.Acceptance.Tests;

public sealed partial class ProductRealViewOnlyLoopbackTests
{
    [Theory(Timeout = 60_000)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Product_Client_Displays_ReadOnly_Catalog_Over_Control_Tls_And_Clears_On_Stop(
        bool delayedCatalog, bool externalStop)
    {
        await using StaDispatcherFixture sta = new();
        Dispatcher dispatcher = await sta.Ready.WaitAsync(Guard);
        Task run = dispatcher.InvokeAsync(() => RunOnStaAsync(
            withCatalog: true, delayedCatalog: delayedCatalog, externalStop: externalStop)).Task.Unwrap();
        try { await run.WaitAsync(TimeSpan.FromSeconds(55)); }
        finally { await run.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing); }
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
    }
}
