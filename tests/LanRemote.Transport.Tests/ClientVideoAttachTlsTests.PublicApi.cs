using System.Net.Security;
using System.Reflection;
using System.Security.Authentication;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

public sealed partial class ClientVideoAttachTlsTests
{
    [Fact]
    public Task Public_Router_First_Frame_And_Child_DisposeAsync_Leave_Parent_Monitor_Alive() => RunPublicAsync(new(), async f =>
    {
        await f.OpenControlAsync();
        Assert.Null(f.Control.ControlMonitorCompletion);
        AuthenticatedVideoSession video = await f.AttachPublic().WaitAsync(Guard);
        var lifetime = PublicPathLifetime(f);
        using EncodedFrame? frame = await f.Read(video).WaitAsync(Guard);
        AssertGoldenFrame(frame);
        await f.AssertDistinctConnectionsAsync();
        Assert.Equal(VideoAttachStatus.Attached, (await f.Second.Task.WaitAsync(Guard)).Router.AttachStatus);
        Assert.Equal(f.Control.SessionId, f.Source.SessionId);
        await f.Source.Waiting.Task.WaitAsync(Guard);
        Task monitor = PendingControlMonitor(f);

        await AssertPublicDisposeReportAsync(f, video, () => lifetime.LifetimeErrors);
        await JoinedCancellationAsync(f.Source.Pending!);
        await f.WaitForConnectionCountAsync(1);
        f.AssertControlAlive();
        Assert.Same(monitor, PendingControlMonitor(f));
        Assert.Equal(0, f.Clock.TimerCount);

        // 在 child 异步释放后才发字节，以同一 monitor 的实际响应证明父监控仍然存活。
        await f.SendServerControlByteAsync(0x7f).WaitAsync(Guard);
        await monitor.WaitAsync(Guard);
        AssertUnexpectedControlData(f, monitor);
        await AssertPublicDisposeReportAsync(f, f.Control, () => f.Control.LifetimeErrors);
        await f.JoinServerConnectionsAsync();
    });

    [Fact]
    public Task Public_Hanging_Ack_Server_Control_Close_Ends_Attach_Without_Video_Lease() => RunPublicAsync(new(Reply.Hang), async f =>
    {
        await f.OpenControlAsync();
        Task<AuthenticatedVideoSession> attach = f.AttachPublic();
        await f.HelloVerified.Task.WaitAsync(Guard);
        await f.AssertDistinctConnectionsAsync();
        Task monitor = PendingControlMonitor(f);
        Assert.False(attach.IsCompleted);
        Assert.False(f.PeerClosed.Task.IsCompleted);

        // 独立 video 脚本不登记 lease，也不发 ACK；这里只关 server Control。
        // 不取消 caller、不推进 clock、不关客户端，排除 router 联动关闭视频造成的假阳性。
        await f.CloseServerControlAsync().WaitAsync(Guard);
        await monitor.WaitAsync(Guard);
        AssertRemoteControlEnd(f, monitor);
        Exception error = await f.FailureAsync(attach);
        Assert.True(error is OperationCanceledException or ObjectDisposedException or IOException,
            $"挂起 ACK 的终止类型错误：{error.GetType().FullName}");
        Assert.Contains(f.Control.LifetimeErrors, root => HasExceptionReference(root, error));
        await f.PeerClosed.Task.WaitAsync(Guard);
        await f.JoinServerConnectionsAsync();
        AssertMonitorRevoked(f);
        Assert.Equal(0, f.Clock.TimerCount);
        await AssertPublicDisposeReportAsync(f, f.Control, () => f.Control.LifetimeErrors);
    });

    [Fact]
    public Task Public_Delivered_Server_Control_Close_Revokes_Independent_Video() => RunPublicAsync(new(Reply.Combined), async f =>
    {
        await f.OpenControlAsync();
        AuthenticatedVideoSession video = await f.AttachPublic().WaitAsync(Guard);
        var lifetime = PublicPathLifetime(f);
        using EncodedFrame? frame = await f.Read(video).WaitAsync(Guard);
        AssertGoldenFrame(frame);
        await f.AssertDistinctConnectionsAsync();
        Task monitor = PendingControlMonitor(f);

        await f.CloseServerControlAsync().WaitAsync(Guard);
        // 任何客户端 read/dispose 之前，先观察 monitor 及无 lease 的视频对端最终退出。
        await monitor.WaitAsync(Guard);
        await f.PeerClosed.Task.WaitAsync(Guard);
        await f.JoinServerConnectionsAsync();
        AssertRemoteControlEnd(f, monitor);
        AssertMonitorRevoked(f);
        Assert.Throws<ObjectDisposedException>(() => { _ = f.Read(video); });
        await AssertPublicDisposeReportAsync(f, f.Control, () => f.Control.LifetimeErrors);
        await AssertPublicDisposeReportAsync(f, video, () => lifetime.LifetimeErrors);
        Assert.Equal(0, f.Clock.TimerCount);
    });

    [Fact]
    public Task Public_Delivered_Extra_Control_Byte_Reports_Original_Protocol_Error() => RunPublicAsync(new(Reply.Combined), async f =>
    {
        await f.OpenControlAsync();
        AuthenticatedVideoSession video = await f.AttachPublic().WaitAsync(Guard);
        var lifetime = PublicPathLifetime(f);
        using EncodedFrame? frame = await f.Read(video).WaitAsync(Guard);
        AssertGoldenFrame(frame);
        Task monitor = PendingControlMonitor(f);

        await f.SendServerControlByteAsync(0xff).WaitAsync(Guard);
        await monitor.WaitAsync(Guard);
        AssertUnexpectedControlData(f, monitor);
        Exception protocolError = f.Control.ControlMonitorState!.Error!;
        await f.PeerClosed.Task.WaitAsync(Guard);
        await f.JoinServerConnectionsAsync();
        AssertMonitorRevoked(f);
        Assert.Throws<ObjectDisposedException>(() => { _ = f.Read(video); });
        Exception[] reported = await AssertPublicDisposeReportAsync(f, f.Control, () => f.Control.LifetimeErrors);
        Assert.Contains(reported, root => HasExceptionReference(root, protocolError));
        await AssertPublicDisposeReportAsync(f, video, () => lifetime.LifetimeErrors);
        Assert.Equal(0, f.Clock.TimerCount);
    });

    [Fact]
    public Task Public_Local_Parent_DisposeAsync_Pending_Tls_Reads_Eventually_Exit_And_Report_Saved_Errors() => RunPublicAsync(new(), async f =>
    {
        await f.OpenControlAsync();
        SslStream controlStream = Assert.IsType<SslStream>(f.Control.Stream);
        Task<AuthenticatedVideoSession> attach = f.AttachPublic();
        AuthenticatedVideoSession video = await attach.WaitAsync(Guard);
        var lifetime = PublicPathLifetime(f);
        using EncodedFrame? frame = await f.Read(video).WaitAsync(Guard);
        AssertGoldenFrame(frame);
        await f.Source.Waiting.Task.WaitAsync(Guard);
        await f.AssertDistinctConnectionsAsync();
        Task monitor = PendingControlMonitor(f);
        await WaitForRealControlReadAsync(controlStream);
        object monitorOwner = PublicPathField(f.Control, "_controlMonitor")!;
        await WaitUntilAsync(() => PublicPathField(monitorOwner, "_original") is Task<int>);
        Task<int> originalControlRead = f.Own(Assert.IsAssignableFrom<Task<int>>(
            PublicPathField(monitorOwner, "_original")));
        TlsConnection connection = Assert.IsType<TlsConnection>(PublicPathField(lifetime, "_connection"));
        SslStream videoStream = Assert.IsType<SslStream>(connection.Stream);
        Task<EncodedFrame?> read = f.Read(video);
        object readOperation = PublicPathField(lifetime, "_read")!;
        Assert.Same(read, PublicPathField(readOperation, "Worker"));
        // 复用 SslStream 的只读在读标志探针；不替换流，也不创建第二个 reader。
        await WaitForRealControlReadAsync(videoStream);
        Assert.False(originalControlRead.IsCompleted);
        Assert.False(read.IsCompleted);

        // 不先关 server 或发送数据。报告须匹配实际保存的诊断，不能假定主动 close 无错误。
        Exception[] reported = await AssertPublicDisposeReportAsync(f, f.Control, () => f.Control.LifetimeErrors);
        // 以下独立等待只证明真实 TLS 最终退出，不作为 DisposeAsync 完整 join 的强证据。
        await CompletedAsync(originalControlRead);
        await monitor.WaitAsync(Guard);
        await attach.WaitAsync(Guard);
        Exception readError = await f.FailureAsync(read);
        Assert.Contains(lifetime.LifetimeErrors, root => HasExceptionReference(root, readError));
        var state = f.Control.ControlMonitorState!;
        LogControlMonitor(f);
        Assert.True(state.LocalStopObservedBeforeCompletion);
        if (originalControlRead.IsCompletedSuccessfully)
        {
            Assert.Equal(0, await originalControlRead);
            Assert.Equal("EndOfStream", state.End.ToString());
            Assert.Null(state.Error);
        }
        else
        {
            Exception controlError = await f.FailureAsync(originalControlRead);
            Assert.Equal("Faulted", state.End.ToString());
            Assert.NotNull(state.Error);
            Assert.True(HasExceptionReference(state.Error, controlError));
            Assert.Contains(f.Control.LifetimeErrors, root => HasExceptionReference(root, controlError));
        }
        AssertSamePublicErrorRoots(reported, f.Control.LifetimeErrors);
        await JoinedCancellationAsync(f.Source.Pending!);
        await f.JoinServerConnectionsAsync();
        await AssertPublicDisposeReportAsync(f, video, () => lifetime.LifetimeErrors);
        AssertMonitorRevoked(f);
        Assert.Same(monitor, f.Control.ControlMonitorCompletion);
        Assert.Equal(0, f.Clock.TimerCount);
    });

    [Fact]
    public Task Public_Wrong_Ack_SessionId_Preserves_Failure_And_Control_Monitor() =>
        RunPublicAsync(new(Reply.Invalid, invalid: InvalidAck.WrongSessionId), async f =>
    {
        await f.OpenControlAsync();
        Exception error = await f.FailureAsync(f.AttachPublic());
        Assert.Equal("video-attach-ack-session-mismatch", Assert.IsType<FrameProtocolException>(error).Reason);
        await f.HelloVerified.Task.WaitAsync(Guard);
        await f.PeerClosed.Task.WaitAsync(Guard);
        await f.WaitForConnectionCountAsync(1);
        Assert.Contains(f.Control.LifetimeErrors, root => HasExceptionReference(root, error));
        f.AssertControlAlive();
        Task monitor = PendingControlMonitor(f);
        Assert.Equal(0, f.Clock.TimerCount);

        await f.SendServerControlByteAsync(0x55).WaitAsync(Guard);
        await monitor.WaitAsync(Guard);
        AssertUnexpectedControlData(f, monitor);
        Exception[] reported = await AssertPublicDisposeReportAsync(f, f.Control, () => f.Control.LifetimeErrors);
        Assert.Contains(reported, root => HasExceptionReference(root, error));
        await f.JoinServerConnectionsAsync();
    });

    [Fact]
    public Task Public_Changed_Second_Tls_Certificate_Is_Rejected_And_Reported() => RunPublicAsync(new(rotateCertificate: true), async f =>
    {
        await f.OpenControlAsync();
        Assert.True(f.Control.Identity.PinsMatch);
        Exception error = await f.FailureAsync(f.AttachPublic());
        Assert.Contains(PeerCertificateValidator.RejectionPinMismatch,
            Assert.IsType<AuthenticationException>(error).Message);
        await f.CertificateSwitch!.SecondAccepted.Task.WaitAsync(Guard);
        Assert.NotEqual(TestCertificateFactory.Fingerprint(f.Certificate),
            TestCertificateFactory.Fingerprint(f.CertificateSwitch.Certificate));
        Assert.Equal(1, f.HandlerCount);
        Assert.Equal(0, f.Clock.TimerCount);
        f.AssertControlAlive();
        Task monitor = PendingControlMonitor(f);
        Assert.Contains(f.Control.LifetimeErrors, root => HasExceptionReference(root, error));

        Exception[] reported = await AssertPublicDisposeReportAsync(f, f.Control, () => f.Control.LifetimeErrors);
        Assert.Contains(reported, root => HasExceptionReference(root, error));
        await monitor.WaitAsync(Guard);
        // 透明代理不转发 TCP 半关闭；代理原 pump 和 server 连接由既有夹具 finally 完整收尾。
    });

    [Fact]
    public Task Public_Combined_Ack_And_First_Frame_Leave_Frame_For_Public_Reader() => RunPublicAsync(new(Reply.Combined), async f =>
    {
        await f.OpenControlAsync();
        AuthenticatedVideoSession video = await f.AttachPublic().WaitAsync(Guard);
        var lifetime = PublicPathLifetime(f);
        await f.HelloVerified.Task.WaitAsync(Guard);
        using EncodedFrame? frame = await f.Read(video).WaitAsync(Guard);
        AssertGoldenFrame(frame);
        await f.AssertDistinctConnectionsAsync();
        Task monitor = PendingControlMonitor(f);

        // ACK 和黄金帧共用一次应用 Write；不假设对应单个 TLS record 或 TCP 分段。
        await AssertPublicDisposeReportAsync(f, video, () => lifetime.LifetimeErrors);
        await f.PeerClosed.Task.WaitAsync(Guard);
        await f.WaitForConnectionCountAsync(1);
        f.AssertControlAlive();
        Assert.Same(monitor, PendingControlMonitor(f));
        await AssertPublicDisposeReportAsync(f, f.Control, () => f.Control.LifetimeErrors);
        await monitor.WaitAsync(Guard);
        await f.JoinServerConnectionsAsync();
        Assert.Equal(0, f.Clock.TimerCount);
    });

    private static async Task RunPublicAsync(TlsScenario fixture, Func<TlsScenario, Task> body)
    {
        Exception? primary = null;
        try { await body(fixture); }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            try { await fixture.DisposeAsync(); }
            catch (Exception cleanup) when (primary is not null)
            {
                // 保留 body 主异常的对象、类型及堆栈；清理失败只作为附加诊断。
                primary.Data["ClientVideoAttachTlsTests.PublicApi.CleanupFailure"] = cleanup;
            }
        }
    }

    private static async Task<Exception[]> AssertPublicDisposeReportAsync(
        TlsScenario f, IAsyncDisposable session, Func<IReadOnlyList<Exception>> diagnostics)
    {
        Task dispose = f.Own(session.DisposeAsync().AsTask());
        await CompletedAsync(dispose);
        Exception[] saved = diagnostics().ToArray();
        await AssertReportAsync(dispose);
        Task repeated = f.Own(session.DisposeAsync().AsTask());
        await CompletedAsync(repeated);
        AssertSamePublicErrorRoots(saved, diagnostics());
        await AssertReportAsync(repeated);
        return saved;

        async Task AssertReportAsync(Task task)
        {
            if (saved.Length == 0)
            {
                await task;
                return;
            }
            AggregateException report = Assert.IsType<AggregateException>(await f.FailureAsync(task));
            // 精确比较顶层原根的顺序和引用，保留多 fault 原树；不 Flatten，也不按 IO/ODE 类型删错。
            AssertSamePublicErrorRoots(saved, report.InnerExceptions);
        }
    }

    private static void AssertSamePublicErrorRoots(IReadOnlyList<Exception> expected, IReadOnlyList<Exception> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++) Assert.Same(expected[i], actual[i]);
    }

    private static AuthenticatedControlSession.ClientVideoLifetime PublicPathLifetime(TlsScenario f) =>
        Assert.IsType<AuthenticatedControlSession.ClientVideoLifetime>(PublicPathField(f.Control, "_videoLifetime"));

    private static object? PublicPathField(object owner, string name)
    {
        // 仅观察真实生产对象的原任务/诊断；不改字段、不调用内部 attach/read/stop，不注入工厂。
        FieldInfo? field = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field.GetValue(owner);
    }
}
