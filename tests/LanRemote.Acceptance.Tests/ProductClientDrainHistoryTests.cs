using System.Net;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using LanRemote.App;
using LanRemote.App.Services;
using LanRemote.Core.Encoding;
using LanRemote.Core.Infrastructure;
using LanRemote.Security.Certificates;
using LanRemote.Security.Identity;
using LanRemote.Security.Secrets;
using LanRemote.Transport;

namespace LanRemote.Acceptance.Tests;

[Collection(AcceptanceStageFiveCollection.Name)]
public sealed class ProductClientDrainHistoryTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

    [Fact(Timeout = 20_000)]
    public Task Cleanup_Fault_After_Attempt_Is_Released_Remains_The_Original_Stop_Fault() =>
        OnStaAsync(async (service, target, owner) =>
        {
            Task connection = service.ConnectAsync(target, owner);
            Task cleanup = Assert.IsAssignableFrom<Task>(service.LastCleanupTask);
            InvalidOperationException injected = new("取消回调清理故障");
            // CancelAsync 将回调异常归入原取消任务，不从 WPF 窗口事件抛出。
            using CancellationTokenRegistration callback = ActiveStopToken(service).Register(() => throw injected);

            Task firstStop = service.StopAndDisposeAsync();
            Assert.Same(cleanup, firstStop);
            AggregateException connectionError = await Assert.ThrowsAsync<AggregateException>(
                () => connection.WaitAsync(Guard));
            Assert.True(cleanup.IsFaulted);
            Assert.Equal("远端会话已结束，关闭时有清理错误；请检查应用状态。", service.LastStatus);
            Assert.False(service.IsFullyDrained);
            Assert.False(HasDrainEvidenceFor(service, cleanup));
            Assert.False(HasDrainEvidenceFor(service, connection));
            Assert.Same(cleanup, service.LastCleanupTask);
            Assert.NotSame(connection, cleanup);

            Task secondStop = service.StopAndDisposeAsync();
            Task thirdStop = service.StopAndDisposeAsync();
            Assert.Same(cleanup, secondStop);
            Assert.Same(secondStop, thirdStop);
            AggregateException cleanupError = await Assert.ThrowsAsync<AggregateException>(
                () => thirdStop.WaitAsync(Guard));
            Assert.Same(cleanupError, Assert.Single(cleanup.Exception!.InnerExceptions));
            Assert.Same(connectionError, cleanupError);
            Assert.Contains(cleanupError.Flatten().InnerExceptions, error => ReferenceEquals(error, injected));
            Assert.False(service.IsFullyDrained);
        });

    [Fact(Timeout = 20_000)]
    public Task Invalid_Remote_Key_Fails_Connect_Without_Contaminating_Successful_Cleanup() =>
        OnStaAsync(async (service, target, owner) =>
        {
            Task connection = service.ConnectAsync(target, "invalid-remote-key", owner);
            Task cleanup = Assert.IsAssignableFrom<Task>(service.LastCleanupTask);
            await Assert.ThrowsAnyAsync<FormatException>(() => connection.WaitAsync(Guard));
            await cleanup.WaitAsync(Guard);
            Assert.True(cleanup.IsCompletedSuccessfully);
            Assert.True(service.IsFullyDrained);
            Assert.True(HasDrainEvidenceFor(service, cleanup));
            Assert.False(HasDrainEvidenceFor(service, connection));
            Assert.Equal("远端访问密钥格式错误，请输入远端设备显示的完整密钥。", service.LastStatus);
            Assert.Same(cleanup, service.StopAndDisposeAsync());
            await service.StopAndDisposeAsync().WaitAsync(Guard);
            Assert.True(connection.IsFaulted);
        });

    [Fact(Timeout = 20_000)]
    public Task A_Later_Attempt_Cannot_Reuse_An_Earlier_Cleanup_Certificate() =>
        OnStaAsync(async (service, target, owner) =>
        {
            Task first = service.ConnectAsync(target, "invalid-remote-key", owner);
            Task oldCleanup = Assert.IsAssignableFrom<Task>(service.LastCleanupTask);
            await Assert.ThrowsAnyAsync<FormatException>(() => first.WaitAsync(Guard));
            await oldCleanup.WaitAsync(Guard);
            Assert.True(HasDrainEvidenceFor(service, oldCleanup));

            Task second = service.ConnectAsync(target, owner);
            Task currentCleanup = Assert.IsAssignableFrom<Task>(service.LastCleanupTask);
            Assert.NotSame(oldCleanup, currentCleanup);
            Assert.False(HasDrainEvidenceFor(service, oldCleanup));
            Assert.False(HasDrainEvidenceFor(service, currentCleanup));
            Assert.Same(currentCleanup, service.StopAndDisposeAsync());
            await currentCleanup.WaitAsync(Guard);
            await second.WaitAsync(Guard);
            Assert.True(HasDrainEvidenceFor(service, currentCleanup));
            Assert.False(HasDrainEvidenceFor(service, oldCleanup));
        });

    [Fact(Timeout = 20_000)]
    public Task Stop_During_Key_Wait_Joins_The_Published_Original_Cleanup() =>
        OnStaAsync(async (service, target, owner) =>
        {
            Task connection = service.ConnectAsync(target, owner);
            Task cleanup = Assert.IsAssignableFrom<Task>(service.LastCleanupTask);
            Task firstStop = service.StopAndDisposeAsync();
            Task secondStop = service.StopAndDisposeAsync();
            Assert.Same(cleanup, firstStop);
            Assert.Same(firstStop, secondStop);
            Assert.False(firstStop.IsCompleted);
            Assert.False(service.IsFullyDrained);
            Assert.False(HasDrainEvidenceFor(service, firstStop));

            await firstStop.WaitAsync(Guard);
            await connection.WaitAsync(Guard);
            Assert.True(service.IsFullyDrained);
            Assert.True(HasDrainEvidenceFor(service, firstStop));
            Assert.Same(cleanup, service.LastCleanupTask);
        });

    [Fact(Timeout = 20_000)]
    public Task Fault_Rechecks_Do_Not_Rerun_Window_Cleanup_Or_Allow_New_Attempts() =>
        OnStaAsync(async (service, target, owner) =>
        {
            Task connection = service.ConnectAsync(target, owner);
            Task original = Assert.IsAssignableFrom<Task>(service.LastCleanupTask);
            RemoteConnectDialog dialog = Assert.Single(owner.OwnedWindows.OfType<RemoteConnectDialog>());
            int closedCount = 0;
            dialog.Closed += (_, _) => closedCount++;
            InvalidOperationException injected = new("取消回调清理故障");
            using CancellationTokenRegistration callback = ActiveStopToken(service).Register(() => throw injected);

            dialog.Close();
            AggregateException connectionError = await Assert.ThrowsAsync<AggregateException>(
                () => connection.WaitAsync(Guard));
            Assert.Contains(connectionError.Flatten().InnerExceptions, error => ReferenceEquals(error, injected));
            InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
                () => { _ = service.ConnectAsync(target, owner); });
            Assert.Contains("前次客户端清理未成功", refused.Message);
            for (int i = 0; i < 3; i++)
            {
                Task retry = service.StopAndDisposeAsync();
                Assert.Same(original, retry);
                AggregateException cleanupError = await Assert.ThrowsAsync<AggregateException>(
                    () => retry.WaitAsync(Guard));
                Assert.Same(connectionError, cleanupError);
            }
            Assert.Equal(1, closedCount);
            Assert.Same(original, service.LastCleanupTask);
            Assert.False(service.IsFullyDrained);
            Assert.False(HasDrainEvidenceFor(service, original));
        });

    [Fact(Timeout = 20_000)]
    public Task Connector_Failure_Before_Control_Delivery_Cannot_Claim_A_Drain_Certificate() =>
        OnStaAsync(async (service, target, owner) =>
        {
            Task connection = service.ConnectAsync(target, CrockfordBase32.Encode(new byte[16]), owner);
            Task cleanup = Assert.IsAssignableFrom<Task>(service.LastCleanupTask);
            object attempt = Assert.IsAssignableFrom<object>(typeof(ClientConnectService)
                .GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service));
            Assert.False(HasDrainEvidenceFor(service, cleanup));
            await Assert.ThrowsAnyAsync<Exception>(() => connection.WaitAsync(Guard));
            await cleanup.WaitAsync(Guard);
            Assert.True(cleanup.IsCompletedSuccessfully);
            Assert.True(service.IsFullyDrained);
            Assert.False(HasDrainEvidenceFor(service, cleanup));
            Assert.Same(cleanup, service.StopAndDisposeAsync());

            Assert.Null(typeof(ClientConnectService).GetField("_lastDrainEvidence",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service));
            Assert.True(Assert.IsType<bool>(attempt.GetType().GetProperty("ConnectorInvoked",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(attempt)));
            Assert.Null(attempt.GetType().GetProperty("Control",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(attempt));
        });

    private static bool HasDrainEvidenceFor(ClientConnectService service, Task originalStop) =>
        Assert.IsType<bool>(typeof(ClientConnectService).GetMethod("HasSuccessfulDrainEvidenceFor",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [originalStop]));

    private static CancellationToken ActiveStopToken(ClientConnectService service)
    {
        object? attempt = typeof(ClientConnectService)
            .GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service);
        Assert.NotNull(attempt);
        CancellationTokenSource stop = Assert.IsType<CancellationTokenSource>(attempt.GetType()
            .GetProperty("Stop", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(attempt));
        return stop.Token;
    }

    private static async Task OnStaAsync(Func<ClientConnectService, ConnectionTarget, Window, Task> verify)
    {
        await using StaDispatcherFixture sta = new();
        Dispatcher dispatcher = await sta.Ready.WaitAsync(Guard);
        using AcceptanceTestDirectory directory = new();
        DpapiSecretVault vault = new(AppPaths.FromRootDirectory(directory.Path));
        using DeviceCertificateService certificates = new(vault);
        DeviceIdentityService identity = new(vault, certificates);
        ClientConnectService service = new(identity, new ControlClientConnector());
        Assert.True(ConnectionTarget.TryCreate(Guid.NewGuid(), IPAddress.Loopback, 1,
            new string('A', 64), out ConnectionTarget? target));

        Task run = dispatcher.InvokeAsync(async () =>
        {
            Window owner = new()
            {
                Width = 1, Height = 1, Left = -10000, Top = -10000,
                WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            };
            owner.Show();
            try { await verify(service, target!, owner); }
            finally
            {
                try { await service.StopAndDisposeAsync().WaitAsync(Guard); }
                catch when (service.LastCleanupTask?.IsFaulted == true) { }
                if (owner.IsVisible) owner.Close();
            }
        }).Task.Unwrap();
        await run.WaitAsync(Guard);
    }
}
