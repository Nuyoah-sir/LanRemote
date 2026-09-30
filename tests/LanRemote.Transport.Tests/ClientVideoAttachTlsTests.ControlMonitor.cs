using System.Net.Security;
using System.Reflection;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;
using Xunit.Abstractions;

namespace LanRemote.Transport.Tests;

public sealed partial class ClientVideoAttachTlsTests
{
    private readonly ITestOutputHelper _output;

    public ClientVideoAttachTlsTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Monitored_Production_Router_Reads_First_Frame_And_Child_Stop_Leaves_Control_Monitor_Alive()
    {
        await using TlsScenario f = new();
        await f.OpenControlAsync();
        Assert.Null(f.Control.ControlMonitorState);
        Assert.Null(f.Control.ControlMonitorCompletion);
        var child = await f.AttachMonitored().WaitAsync(Guard);
        using EncodedFrame? frame = await f.Read(child).WaitAsync(Guard);
        AssertGoldenFrame(frame);
        await f.AssertDistinctConnectionsAsync();
        Assert.Equal(VideoAttachStatus.Attached, (await f.Second.Task.WaitAsync(Guard)).Router.AttachStatus);
        Assert.Equal(f.Control.SessionId, f.Source.SessionId);
        await f.Source.Waiting.Task.WaitAsync(Guard);
        Task monitor = PendingControlMonitor(f);

        await f.Own(child.StopAndJoinAsync()).WaitAsync(Guard);
        await JoinedCancellationAsync(f.Source.Pending!);
        await f.WaitForConnectionCountAsync(1);
        f.AssertControlAlive();
        Assert.Empty(child.LifetimeErrors);
        Assert.Equal(0, f.Clock.TimerCount);
        Assert.Same(monitor, PendingControlMonitor(f));

        // child 已完整退出后才发送：须由同一 monitor 读到，不能以某一瞬间未完成代替存活证明。
        await f.SendServerControlByteAsync(0x7f).WaitAsync(Guard);
        await monitor.WaitAsync(Guard);
        AssertUnexpectedControlData(f, monitor);
        AssertMonitorRevoked(f, child);
        await f.JoinServerConnectionsAsync();
        await f.Own(f.Control.CloseAndJoinAsync()).WaitAsync(Guard);
    }

    [Fact]
    public async Task Monitored_Hanging_Ack_Server_Control_Close_Fails_Attach_Without_Video_Peer_Intervention()
    {
        await using TlsScenario f = new(Reply.Hang);
        await f.OpenControlAsync();
        Task<AuthenticatedControlSession.ClientVideoLifetime> attach = f.AttachMonitored();
        await f.HelloVerified.Task.WaitAsync(Guard);
        await f.AssertDistinctConnectionsAsync();
        Task monitor = PendingControlMonitor(f);

        // Hang 脚本不发送 ACK，也不登记服务端 video lease；关闭 Control 不会联动这个视频脚本。
        // 不取消 caller、不推进 clock、不释放 ACK、不先请求客户端关闭，必须由 Control 原读触发撤销。
        await f.CloseServerControlAsync().WaitAsync(Guard);
        await monitor.WaitAsync(Guard);
        AssertRemoteControlEnd(f, monitor);
        Exception error = await f.FailureAsync(attach);
        Assert.True(error is OperationCanceledException or ObjectDisposedException or IOException,
            $"Control 断开后的挂起 ACK 终止类型错误：{error.GetType().FullName}");
        Assert.Contains(f.Control.LifetimeErrors, root => HasExceptionReference(root, error));
        AssertMonitorRevoked(f);
        await f.PeerClosed.Task.WaitAsync(Guard);
        await f.JoinServerConnectionsAsync();
        Assert.Equal(0, f.Clock.TimerCount);

        Task join = f.Own(f.Control.CloseAndJoinAsync());
        await join.WaitAsync(Guard);
        await CompletedAsync(attach);
        await monitor.WaitAsync(Guard);
        Assert.Same(join, f.Control.CloseAndJoinAsync());
    }

    [Fact]
    public async Task Monitored_Delivered_Server_Control_Close_Actively_Revokes_Parent_And_Child()
    {
        // 视频脚本仍是独立真实 TLS，避免 server router 撤销 video lease 造成假阳性。
        await using TlsScenario f = new(Reply.Combined);
        await f.OpenControlAsync();
        var child = await f.AttachMonitored().WaitAsync(Guard);
        using EncodedFrame? frame = await f.Read(child).WaitAsync(Guard);
        AssertGoldenFrame(frame);
        await f.AssertDistinctConnectionsAsync();
        Task monitor = PendingControlMonitor(f);

        await f.CloseServerControlAsync().WaitAsync(Guard);
        // 在任何客户端 read/stop/dispose 之前，先等待 monitor 和独立 video 对端的关闭事实。
        await monitor.WaitAsync(Guard);
        await f.PeerClosed.Task.WaitAsync(Guard);
        await f.JoinServerConnectionsAsync();
        AssertRemoteControlEnd(f, monitor);
        AssertMonitorRevoked(f, child);
        Assert.Empty(child.LifetimeErrors);

        await f.Own(f.Control.CloseAndJoinAsync()).WaitAsync(Guard);
        await f.Own(child.StopAndJoinAsync()).WaitAsync(Guard);
        await monitor.WaitAsync(Guard);
        Assert.Equal(0, f.Clock.TimerCount);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)255)]
    public async Task Monitored_One_Extra_Control_Byte_Has_Exact_Protocol_Reason(byte value)
    {
        await using TlsScenario f = new(Reply.Combined);
        await f.OpenControlAsync();
        var child = await f.AttachMonitored().WaitAsync(Guard);
        using EncodedFrame? frame = await f.Read(child).WaitAsync(Guard);
        AssertGoldenFrame(frame);
        Task monitor = PendingControlMonitor(f);

        // 只写一个应用字节，不假设 TLS record/TCP 分段；读长与无循环由组件闸门测试裁决。
        await f.SendServerControlByteAsync(value).WaitAsync(Guard);
        await monitor.WaitAsync(Guard);
        AssertUnexpectedControlData(f, monitor);
        await f.PeerClosed.Task.WaitAsync(Guard);
        await f.JoinServerConnectionsAsync();
        AssertMonitorRevoked(f, child);
        await f.Own(f.Control.CloseAndJoinAsync()).WaitAsync(Guard);
        await f.Own(child.StopAndJoinAsync()).WaitAsync(Guard);
        Assert.Empty(child.LifetimeErrors);
        Assert.Equal("control-unexpected-data",
            Assert.IsType<FrameProtocolException>(Assert.Single(f.Control.LifetimeErrors)).Reason);
        Assert.Equal(0, f.Clock.TimerCount);
    }

    [Fact]
    public async Task Monitored_Local_CloseAndJoin_Ends_Real_Tls_Pending_Read_And_Joins_All_Workers()
    {
        await using TlsScenario f = new();
        await f.OpenControlAsync();
        SslStream controlStream = Assert.IsType<SslStream>(f.Control.Stream);
        Task<AuthenticatedControlSession.ClientVideoLifetime> attach = f.AttachMonitored();
        var child = await attach.WaitAsync(Guard);
        using EncodedFrame? frame = await f.Read(child).WaitAsync(Guard);
        AssertGoldenFrame(frame);
        await f.Source.Waiting.Task.WaitAsync(Guard);
        await f.AssertDistinctConnectionsAsync();
        Task monitor = PendingControlMonitor(f);
        await WaitForRealControlReadAsync(controlStream);
        Task<EncodedFrame?> read = f.Read(child);

        // 不向 Control 发送数据或先关 server；只调用父 join，让真实 TLS 关闭解堵 None-token 原读。
        Task join = f.Own(f.Control.CloseAndJoinAsync());
        await join.WaitAsync(Guard);
        // 分别 await 已保存的原任务，不用 IsCompleted 的瞬时采样宣称 join 成立。
        await monitor.WaitAsync(Guard);
        await attach.WaitAsync(Guard);
        Exception readError = await f.FailureAsync(read);
        Assert.True(readError is OperationCanceledException or ObjectDisposedException or IOException,
            $"本地关闭后视频原读的终止类型错误：{readError.GetType().FullName}");
        Assert.Contains(child.LifetimeErrors, root => HasExceptionReference(root, readError));
        await JoinedCancellationAsync(f.Source.Pending!);
        await f.JoinServerConnectionsAsync();
        await f.Own(child.StopAndJoinAsync()).WaitAsync(Guard);
        Assert.Same(join, f.Control.CloseAndJoinAsync());
        Assert.Same(monitor, f.Control.ControlMonitorCompletion);
        AssertMonitorRevoked(f, child);
        Assert.Equal(0, f.Clock.TimerCount);

        var state = f.Control.ControlMonitorState ?? throw new InvalidOperationException("未登记 Control monitor。");
        LogControlMonitor(f);
        Assert.True(state.LocalStopObservedBeforeCompletion);
        // 本例必须实际执行原读；SkippedLocalStop 只能由另外的启动前停止组件用例证明。
        Assert.Contains(state.End.ToString(), new[] { "EndOfStream", "Faulted" });
        if (state.End.ToString() == "EndOfStream") Assert.Null(state.Error);
        else
        {
            Assert.True(state.Error is IOException or ObjectDisposedException or OperationCanceledException,
                $"本地关闭后的 Control 原读类型错误：{state.Error?.GetType().FullName ?? "无错误"}");
            Assert.Contains(f.Control.LifetimeErrors, root => HasExceptionReference(root, state.Error!));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Monitored_Video_Attach_Or_Read_Failure_Does_Not_Stop_Valid_Control_Monitor(bool readFailure)
    {
        await using TlsScenario f = new(readFailure ? Reply.SecondAck : Reply.Invalid,
            invalid: InvalidAck.WrongType);
        await f.OpenControlAsync();
        Task<AuthenticatedControlSession.ClientVideoLifetime> attach = f.AttachMonitored();
        await f.HelloVerified.Task.WaitAsync(Guard);
        Task monitor = PendingControlMonitor(f);
        if (readFailure)
        {
            var child = await attach.WaitAsync(Guard);
            Exception error = await f.FailureAsync(f.Read(child));
            Assert.Equal("video-magic", Assert.IsType<FrameProtocolException>(error).Reason);
            await f.Own(child.StopAndJoinAsync()).WaitAsync(Guard);
            Assert.Same(error, Assert.Single(child.LifetimeErrors));
        }
        else
        {
            Exception error = await f.FailureAsync(attach);
            Assert.Equal(VideoAttachAckFrame.RejectWrongType, Assert.IsType<FrameProtocolException>(error).Reason);
            Assert.Same(error, Assert.Single(f.Control.LifetimeErrors));
        }
        await f.PeerClosed.Task.WaitAsync(Guard);
        await f.WaitForConnectionCountAsync(1);
        f.AssertControlAlive();
        Assert.Equal(0, f.Clock.TimerCount);
        Assert.Same(monitor, PendingControlMonitor(f));

        await f.SendServerControlByteAsync(0x55).WaitAsync(Guard);
        await monitor.WaitAsync(Guard);
        AssertUnexpectedControlData(f, monitor);
        AssertMonitorRevoked(f);
        await f.JoinServerConnectionsAsync();
        await f.Own(f.Control.CloseAndJoinAsync()).WaitAsync(Guard);
    }

    private static async Task WaitForRealControlReadAsync(SslStream stream)
    {
        // Pending 快照只证明登记，不能证明 worker 已进入原读。只读 net10.0 SslStream 的在读标志，
        // 不替换流、不添加第二 reader；服务端不发送 Control 数据，所以读只能由随后本地关闭解堵。
        FieldInfo? nestedRead = typeof(SslStream).GetField("_nestedRead", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(nestedRead);
        // 本机 .NET 10.0.12 实测为 NestedState enum，不是 int；按名称匹配而非猜测数字含义。
        Assert.True(nestedRead.FieldType.IsEnum);
        Assert.Equal(new[] { "StreamNotInUse", "StreamInUse", "StreamDisposed" }, Enum.GetNames(nestedRead.FieldType));
        object inUse = Enum.Parse(nestedRead.FieldType, "StreamInUse");
        await WaitUntilAsync(() => Equals(nestedRead.GetValue(stream), inUse));
    }

    private static Task PendingControlMonitor(TlsScenario f)
    {
        Task monitor = Assert.IsAssignableFrom<Task>(f.Control.ControlMonitorCompletion);
        var state = f.Control.ControlMonitorState ?? throw new InvalidOperationException("未登记 Control monitor。");
        Assert.Equal("Pending", state.End.ToString());
        Assert.False(state.LocalStopObservedBeforeCompletion);
        Assert.Null(state.Error);
        return monitor;
    }

    private void AssertRemoteControlEnd(TlsScenario f, Task monitor)
    {
        Assert.Same(monitor, f.Control.ControlMonitorCompletion);
        var state = f.Control.ControlMonitorState ?? throw new InvalidOperationException("未登记 Control monitor。");
        LogControlMonitor(f);
        Assert.False(state.LocalStopObservedBeforeCompletion);
        // Host 关闭原 socket 不承诺 TLS close_notify；按原读事实区分 EOF 和 IOException，拒绝其他归因。
        Assert.Contains(state.End.ToString(), new[] { "EndOfStream", "Faulted" });
        if (state.End.ToString() == "EndOfStream") Assert.Null(state.Error);
        else
        {
            Assert.IsAssignableFrom<IOException>(state.Error);
            Assert.Contains(f.Control.LifetimeErrors, root => HasExceptionReference(root, state.Error!));
        }
    }

    private void AssertUnexpectedControlData(TlsScenario f, Task monitor)
    {
        Assert.Same(monitor, f.Control.ControlMonitorCompletion);
        var state = f.Control.ControlMonitorState ?? throw new InvalidOperationException("未登记 Control monitor。");
        LogControlMonitor(f);
        Assert.Equal("UnexpectedData", state.End.ToString());
        Assert.False(state.LocalStopObservedBeforeCompletion);
        FrameProtocolException error = Assert.IsType<FrameProtocolException>(state.Error);
        Assert.Equal("control-unexpected-data", error.Reason);
        Assert.Contains(f.Control.LifetimeErrors, root => HasExceptionReference(root, error));
    }

    private static void AssertMonitorRevoked(TlsScenario f, AuthenticatedControlSession.ClientVideoLifetime? child = null)
    {
        Assert.Throws<ObjectDisposedException>(() => { _ = f.Control.Stream; });
        if (child is not null) Assert.Throws<ObjectDisposedException>(() => { _ = f.Read(child); });
    }

    private void LogControlMonitor(TlsScenario f)
    {
        var state = f.Control.ControlMonitorState ?? throw new InvalidOperationException("未登记 Control monitor。");
        _output.WriteLine($"Control 原读观测：End={state.End}；" +
            $"LocalStopObservedBeforeCompletion={state.LocalStopObservedBeforeCompletion}；" +
            $"Error={state.Error?.GetType().FullName ?? "无"}。诊断不作为物理关闭原因判据。");
    }

    private static bool HasExceptionReference(Exception root, Exception expected) =>
        ReferenceEquals(root, expected)
        || (root is AggregateException aggregate
            ? aggregate.InnerExceptions.Any(error => HasExceptionReference(error, expected))
            : root.InnerException is { } inner && HasExceptionReference(inner, expected));
}
