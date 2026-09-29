using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

public sealed partial class VideoAttachTlsTests
{
    [Fact(Timeout = 60_000)]
    public async Task Authenticated_Control_And_Second_Tls_Read_Ack_Then_Independent_Golden_Frame()
    {
        await using TlsScenario scenario = new();
        scenario.Start();
        AuthenticatedControlSession control = await scenario.OpenControlAsync();
        VideoPeer video = await scenario.AttachAsync(control);

        scenario.AssertControlAlive(control);
        Assert.Equal(VideoAttachStatus.Attached, video.Server.Router.AttachStatus);
        Assert.Equal(1, video.Server.Router.AttachAttempts);
        Assert.Null(video.Server.Router.Rejection);
        Assert.False(video.Server.Finished.Task.IsCompleted);
        Assert.False((await scenario.Source.Blocked.Task.WaitAsync(Guard)).IsCompleted);
        Assert.All(scenario.Source.SessionIds, id => Assert.Equal(control.SessionId, id));
        Assert.Equal(1, Assert.Single(scenario.Source.Owners).DisposeCalls);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wrong_Proof_Does_Not_Consume_Attach_And_Correct_Proof_Then_Succeeds(bool useWrongPin)
    {
        await using TlsScenario scenario = new();
        scenario.Start();
        AuthenticatedControlSession control = await scenario.OpenControlAsync();
        VideoPeer rejected = await scenario.OpenVideoAsync();
        await scenario.AssertDistinctLiveConnectionsAsync(rejected);
        await SendHelloAsync(control, rejected, corruptProof: !useWrongPin, useWrongPin: useWrongPin);

        await AssertRejectedAsync(rejected, VideoAttachStatus.InvalidProof);
        await scenario.WaitForConnectionCountAsync(1);
        Assert.Equal(0, scenario.Source.ReadCount);
        Assert.Empty(scenario.Source.Owners);
        scenario.AssertControlAlive(control);

        // 不替换 Control/session/token；在新建的真实第二 TLS 上重新提交正确证明。
        VideoPeer accepted = await scenario.AttachAsync(control);
        Assert.NotEqual(rejected.Server.Connection.Security.ConnectionId,
            accepted.Server.Connection.Security.ConnectionId);
        Assert.Equal(VideoAttachStatus.Attached, accepted.Server.Router.AttachStatus);
    }

    [Fact(Timeout = 60_000)]
    public async Task Second_Attach_Is_Rejected_Without_Disrupting_Existing_Video_Or_Control()
    {
        // 第三条连接必须进入路由，不能只命中默认同源两连接的准入限额。
        await using TlsScenario scenario = new(maxConnectionsPerAddress: 3, secondFrame: true);
        scenario.Start();
        AuthenticatedControlSession control = await scenario.OpenControlAsync();
        VideoPeer first = await scenario.AttachAsync(control);
        Task<EncodedFrame?> pending = await scenario.Source.Blocked.Task.WaitAsync(Guard);
        VideoPeer duplicate = await scenario.OpenVideoAsync();
        await scenario.WaitForConnectionCountAsync(3);
        Assert.Equal(3, new[]
        {
            scenario.ControlServer.Connection.Security.ConnectionId,
            first.Server.Connection.Security.ConnectionId,
            duplicate.Server.Connection.Security.ConnectionId,
        }.Distinct().Count());
        await SendHelloAsync(control, duplicate);

        await AssertRejectedAsync(duplicate, VideoAttachStatus.AlreadyAttached);
        await scenario.WaitForConnectionCountAsync(2);
        Assert.False(pending.IsCompleted);
        Assert.False(first.Server.Finished.Task.IsCompleted);
        Assert.Equal(2, scenario.Source.ReadCount);
        scenario.AssertControlAlive(control);

        // 原视频不只是尚未完成：重复附着失败后仍能实际交付一帧。
        scenario.Source.ReleaseNextFrame();
        await ReadGoldenFrameAsync(first);
        await scenario.Source.WaitingAgain.Task.WaitAsync(Guard);
        Assert.False(first.Server.Finished.Task.IsCompleted);
    }

    [Fact(Timeout = 60_000)]
    public async Task Control_Disconnect_Revokes_Video_And_Joins_Original_Source_Task()
    {
        await using TlsScenario scenario = new();
        scenario.Start();
        AuthenticatedControlSession control = await scenario.OpenControlAsync();
        VideoPeer video = await scenario.AttachAsync(control);
        Task<EncodedFrame?> sourceTask = await scenario.Source.Blocked.Task.WaitAsync(Guard);

        control.Dispose();

        await AssertSourceCancelledAsync(scenario.Source, sourceTask);
        Assert.Null(await scenario.ControlServer.Finished.Task.WaitAsync(Guard));
        Assert.Null(await video.Server.Finished.Task.WaitAsync(Guard));
        await AssertClosedWithoutDataAsync(video.Client.Stream);
        await scenario.WaitForConnectionCountAsync(0);
        Assert.Empty(scenario.Context.SessionRegistry.Snapshot());
        Assert.Equal(1, Assert.Single(scenario.Source.Owners).DisposeCalls);
    }

    [Fact(Timeout = 60_000)]
    public async Task Video_Disconnect_Joins_Source_But_Keeps_Original_Control_Registered()
    {
        await using TlsScenario scenario = new();
        scenario.Start();
        AuthenticatedControlSession control = await scenario.OpenControlAsync();
        VideoPeer video = await scenario.AttachAsync(control);
        Task<EncodedFrame?> sourceTask = await scenario.Source.Blocked.Task.WaitAsync(Guard);

        video.Client.Dispose();

        await AssertSourceCancelledAsync(scenario.Source, sourceTask);
        Assert.Null(await video.Server.Finished.Task.WaitAsync(Guard));
        await scenario.WaitForConnectionCountAsync(1);
        scenario.AssertControlAlive(control);
        Assert.Contains(control.SessionToken.ToArray(), value => value != 0);

        // 视频断开也不能恢复一次性资格；同一个活 Control 的重附着仍为 AlreadyAttached。
        VideoPeer retry = await scenario.OpenVideoAsync();
        await scenario.AssertDistinctLiveConnectionsAsync(retry);
        await SendHelloAsync(control, retry);
        await AssertRejectedAsync(retry, VideoAttachStatus.AlreadyAttached);
        await scenario.WaitForConnectionCountAsync(1);
        scenario.AssertControlAlive(control);
        Assert.Equal(2, scenario.Source.ReadCount);
    }

    [Fact(Timeout = 60_000)]
    public async Task Host_Stop_Closes_Both_Tls_Connections_And_Joins_Source()
    {
        await using TlsScenario scenario = new();
        scenario.Start();
        AuthenticatedControlSession control = await scenario.OpenControlAsync();
        VideoPeer video = await scenario.AttachAsync(control);
        Task<EncodedFrame?> sourceTask = await scenario.Source.Blocked.Task.WaitAsync(Guard);

        // 客户端此时仍持有两条流，不能用先 Dispose 客户端伪造 Host 的停机效果。
        await scenario.AssertStoppedAsync();

        await AssertSourceCancelledAsync(scenario.Source, sourceTask);
        Assert.Null(await scenario.ControlServer.Finished.Task.WaitAsync(Guard));
        Assert.Null(await video.Server.Finished.Task.WaitAsync(Guard));
        await AssertClosedWithoutDataAsync(control.Stream);
        await AssertClosedWithoutDataAsync(video.Client.Stream);
        Assert.Empty(scenario.Host.LifecycleErrors.Snapshot);
    }

    [Fact(Timeout = 60_000)]
    public async Task Repeated_Stop_Reports_Unfinished_While_Source_Ignores_Cancellation_Until_Finally_Release()
    {
        await using TlsScenario scenario = new(ignoreCancellation: true);
        scenario.Start();
        AuthenticatedControlSession control = await scenario.OpenControlAsync();
        VideoPeer video = await scenario.AttachAsync(control);
        Task<EncodedFrame?> sourceTask = await scenario.Source.Blocked.Task.WaitAsync(Guard);

        try
        {
            TransportHostStopReport first = await scenario.Host.StopAsync(TimeSpan.FromMilliseconds(100)).WaitAsync(Guard);
            Assert.False(first.AllFinished);
            Assert.True(first.UnfinishedConnections >= 1);
            await scenario.Source.CancellationObserved.Task.WaitAsync(Guard);
            Assert.Null(await scenario.ControlServer.Finished.Task.WaitAsync(Guard));
            await scenario.WaitForConnectionCountAsync(1);
            await AssertClosedWithoutDataAsync(control.Stream);
            await AssertClosedWithoutDataAsync(video.Client.Stream);
            Assert.True(scenario.Source.LifetimeToken.IsCancellationRequested);
            Assert.False(sourceTask.IsCompleted);
            Assert.False(video.Server.Finished.Task.IsCompleted);

            TransportHostStopReport second = await scenario.Host.StopAsync(TimeSpan.FromMilliseconds(100)).WaitAsync(Guard);
            Assert.False(second.AllFinished);
            Assert.Equal(1, second.UnfinishedConnections);
            Assert.Equal(1, scenario.Host.ActiveConnections);
            Assert.Equal(1, scenario.Host.AdmittedConnections);
            Assert.False(sourceTask.IsCompleted);
            Assert.False(video.Server.Finished.Task.IsCompleted);
        }
        finally
        {
            // 断言失败也要解除不响应取消的源；await using 的统一 finally 会再 join 源和 Host。
            scenario.Source.ReleaseAll();
        }

        Assert.Null(await sourceTask.WaitAsync(Guard));
        Assert.True(sourceTask.IsCompletedSuccessfully);
        await scenario.AssertStoppedAsync();
        Assert.Null(await video.Server.Finished.Task.WaitAsync(Guard));
        Assert.Empty(scenario.Host.LifecycleErrors.Snapshot);
    }

    [Fact(Timeout = 60_000)]
    public async Task Cancellation_Callback_Failure_Is_Visible_In_Host_Diagnostics_After_Join()
    {
        InvalidOperationException expected = new("测试视频源取消回调故障");
        await using TlsScenario scenario = new(cancellationFailure: expected);
        scenario.Start();
        AuthenticatedControlSession control = await scenario.OpenControlAsync();
        VideoPeer video = await scenario.AttachAsync(control);
        Task<EncodedFrame?> sourceTask = await scenario.Source.Blocked.Task.WaitAsync(Guard);

        control.Dispose();

        await AssertSourceCancelledAsync(scenario.Source, sourceTask);
        Exception? handlerError = await video.Server.Finished.Task.WaitAsync(Guard);
        Assert.NotNull(handlerError);
        Assert.Same(expected, Assert.Single(LeafErrors(handlerError)));
        await AssertClosedWithoutDataAsync(video.Client.Stream);
        await scenario.AssertStoppedAsync();
        HostLifecycleError diagnostic = Assert.Single(scenario.Host.LifecycleErrors.Snapshot);
        Assert.Equal(HostLifecycleErrorKind.Handler, diagnostic.Kind);
        Assert.Same(expected, Assert.Single(LeafErrors(diagnostic.Error)));
    }

    [Fact(Timeout = 60_000)]
    public async Task Source_Oce_Before_Cancellation_Remains_In_Host_Diagnostics_When_Stop_Overtakes_Router_Join()
    {
        OperationCanceledException expected = new("测试源在未取消时抛出的原始 OCE", CancellationToken.None);
        await using TlsScenario scenario = new(sourceFailure: expected);
        Task<TransportHostStopReport>? stop = null;
        try
        {
            scenario.Start();
            AuthenticatedControlSession control = await scenario.OpenControlAsync();
            VideoPeer video = await scenario.AttachAsync(control);
            Task<EncodedFrame?> sourceTask = await scenario.Source.Blocked.Task.WaitAsync(Guard);
            TaskCompletionSource handlerCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = video.Server.HandlerToken.Register(
                () => handlerCancelled.TrySetResult());
            Assert.False(scenario.Source.LifetimeToken.IsCancellationRequested);
            Assert.False(video.Server.HandlerToken.IsCancellationRequested);

            scenario.Source.ReleaseSourceFailure();
            await scenario.Source.CancellationCallbackEntered.Task.WaitAsync(Guard);
            Assert.False(scenario.Source.LifetimeCancelledAtSourceFailure);
            Assert.Same(expected, await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sourceTask.WaitAsync(Guard)));
            Assert.True(scenario.Source.LifetimeToken.IsCancellationRequested);
            Assert.False(handlerCancelled.Task.IsCompleted);
            Assert.False(video.Server.HandlerToken.IsCancellationRequested);
            Assert.False(video.Server.Finished.Task.IsCompleted);
            scenario.AssertControlAlive(control);

            // Router 已因源故障进入 CancelAsync 回调；必须等 registry 的 handler token 真正取消再放行。
            stop = scenario.Host.StopAsync(TimeSpan.FromMilliseconds(100));
            await handlerCancelled.Task.WaitAsync(Guard);
            Assert.True(video.Server.HandlerToken.IsCancellationRequested);
            TransportHostStopReport report = await stop.WaitAsync(Guard);
            Assert.False(report.AllFinished);
            Assert.True(report.UnfinishedConnections >= 1);
            Assert.False(video.Server.Finished.Task.IsCompleted);

            scenario.Source.ReleaseAll();
            Assert.Same(expected, await video.Server.Finished.Task.WaitAsync(Guard));
            await scenario.AssertStoppedAsync();
            HostLifecycleError diagnostic = Assert.Single(scenario.Host.LifecycleErrors.Snapshot);
            Assert.Equal(HostLifecycleErrorKind.Handler, diagnostic.Kind);
            Assert.Same(expected, diagnostic.Error);
        }
        finally
        {
            // 断言失败也放行全部闸门并 join Stop；外层 await using 再 join 源、回调及所有 handler。
            scenario.Source.ReleaseAll();
            if (stop is not null) await stop.WaitAsync(Guard + Guard);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Extra_Upstream_Byte_Is_Rejected_And_Does_Not_Revoke_Control()
    {
        await using TlsScenario scenario = new();
        scenario.Start();
        AuthenticatedControlSession control = await scenario.OpenControlAsync();
        VideoPeer video = await scenario.AttachAsync(control);
        Task<EncodedFrame?> sourceTask = await scenario.Source.Blocked.Task.WaitAsync(Guard);
        using CancellationTokenSource guard = new(Guard);
        await video.Client.Stream.WriteAsync(new byte[] { 0x7F }, guard.Token);
        await video.Client.Stream.FlushAsync(guard.Token);

        await AssertSourceCancelledAsync(scenario.Source, sourceTask);
        Assert.Null(await video.Server.Finished.Task.WaitAsync(Guard));
        Assert.Equal("video-unexpected-upstream-data", video.Server.Router.Rejection);
        Assert.Equal(VideoAttachStatus.Attached, video.Server.Router.AttachStatus);
        await AssertClosedWithoutDataAsync(video.Client.Stream);
        await scenario.WaitForConnectionCountAsync(1);
        scenario.AssertControlAlive(control);
    }

    [Fact(Timeout = 60_000)]
    public async Task Expired_Hello_Envelope_And_Attach_Ttl_Do_Not_Cancel_Holding_Or_Later_Video_Frame()
    {
        await using TlsScenario scenario = new(secondFrame: true);
        scenario.Start();
        AuthenticatedControlSession control = await scenario.OpenControlAsync();
        VideoPeer video = await scenario.AttachAsync(control);
        Task<EncodedFrame?> sourceTask = await scenario.Source.Blocked.Task.WaitAsync(Guard);
        TimeSpan elapsed = TimeSpan.FromMinutes(1);
        Assert.True(elapsed > Timeouts.PreAuthEnvelopeTimeout);
        Assert.True(elapsed > TimeSpan.FromMilliseconds(control.VideoAttachExpiresInMsHint));

        // 已进入下一次取帧，第一帧写时限已释放；只推进注入服务端的单调时钟并派发到期 timer。
        scenario.Clock.Advance(elapsed);
        Assert.False(sourceTask.IsCompleted);
        Assert.False(scenario.Source.LifetimeToken.IsCancellationRequested);
        scenario.AssertControlAlive(control);
        scenario.Source.ReleaseNextFrame();

        await ReadGoldenFrameAsync(video);
        Task<EncodedFrame?> nextRead = await scenario.Source.WaitingAgain.Task.WaitAsync(Guard);
        Assert.True(sourceTask.IsCompletedSuccessfully);
        Assert.False(nextRead.IsCompleted);
        Assert.Equal(3, scenario.Source.ReadCount);
        Assert.Equal(2, scenario.Source.Owners.Count);
        Assert.All(scenario.Source.Owners, owner => Assert.Equal(1, owner.DisposeCalls));
        Assert.False(video.Server.Finished.Task.IsCompleted);
        scenario.AssertControlAlive(control);
    }

    [Fact(Timeout = 60_000)]
    public async Task Legacy_ControlPreAuth_Rejects_Complete_Valid_Video_Hello_Over_Real_Tls()
    {
        await using TlsScenario scenario = new(legacyVideo: true);
        scenario.Start();
        AuthenticatedControlSession control = await scenario.OpenControlAsync();
        VideoPeer video = await scenario.OpenVideoAsync();
        await scenario.AssertDistinctLiveConnectionsAsync(video);
        // 使用真实已认证 token 和第二 TLS pin，首帧不是缺少 session/nonce/proof 的残缺 JSON。
        await SendHelloAsync(control, video);

        Assert.Null(await video.Server.Finished.Task.WaitAsync(Guard));
        ControlPreAuthResult result = Assert.IsType<ControlPreAuthResult>(video.Server.LegacyResult);
        Assert.False(result.Completed);
        Assert.Equal(ControlSessionState.Closed, result.State);
        // 旧 parser 先因 sessionId/nonce/proof 这些未知字段拒绝，不会走到 channel 值检查。
        Assert.Equal(HelloFrame.RejectMalformedJson, result.Rejection);
        Assert.Null(result.Handoff);
        await AssertClosedWithoutDataAsync(video.Client.Stream);
        await scenario.WaitForConnectionCountAsync(1);
        Assert.Equal(0, scenario.Source.ReadCount);
        scenario.AssertControlAlive(control);
    }
}
