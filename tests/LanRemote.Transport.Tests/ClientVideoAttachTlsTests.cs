using System.Security.Authentication;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

public sealed partial class ClientVideoAttachTlsTests
{
    [Fact]
    public async Task Production_Router_Attaches_Second_Tls_And_Reads_First_Frame()
    {
        await using TlsScenario f = new();
        await f.OpenControlAsync();
        var child = await f.Attach().WaitAsync(Guard);
        using EncodedFrame? frame = await f.Read(child).WaitAsync(Guard);
        AssertGoldenFrame(frame);
        await f.AssertDistinctConnectionsAsync();
        Assert.Equal(VideoAttachStatus.Attached, (await f.Second.Task.WaitAsync(Guard)).Router.AttachStatus);
        Assert.Equal(f.Control.SessionId, f.Source.SessionId);
        await f.Source.Waiting.Task.WaitAsync(Guard);
        Assert.False(f.Source.Pending!.IsCompleted);
        Assert.Equal(1, f.Source.Owner!.DisposeCalls);

        await f.Own(child.StopAndJoinAsync()).WaitAsync(Guard);
        await f.WaitForConnectionCountAsync(1);
        await JoinedCancellationAsync(f.Source.Pending);
        f.AssertControlAlive();
        Assert.Empty(child.LifetimeErrors);
    }

    [Fact]
    public async Task Ack_And_Golden_Frame_In_One_Write_Leave_Frame_For_Child_Reader()
    {
        await using TlsScenario f = new(Reply.Combined);
        await f.OpenControlAsync();
        var child = await f.Attach().WaitAsync(Guard);
        await f.HelloVerified.Task.WaitAsync(Guard);
        using EncodedFrame? frame = await f.Read(child).WaitAsync(Guard);
        AssertGoldenFrame(frame);
        await f.AssertDistinctConnectionsAsync();
        f.AssertControlAlive();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    public async Task Fragmented_Ack_Waits_For_Full_Prefix_And_Payload(int splitAt)
    {
        await using TlsScenario f = new(Reply.Fragmented, splitAt: splitAt);
        await f.OpenControlAsync();
        Task<AuthenticatedControlSession.ClientVideoLifetime> attach = f.Attach();
        await f.PartialAck.Task.WaitAsync(Guard);
        Assert.False(attach.IsCompleted);
        f.ReleaseAck.TrySetResult();
        var child = await attach.WaitAsync(Guard);
        using EncodedFrame? frame = await f.Read(child).WaitAsync(Guard);
        AssertGoldenFrame(frame);
        f.AssertControlAlive();
    }

    public enum InvalidAck
    {
        WrongSessionId, WrongType, DuplicateField, ZeroLength, OversizeLength,
        UnsignedLength, TruncatedPrefix, TruncatedPayload,
    }

    [Theory]
    [InlineData(InvalidAck.WrongSessionId)]
    [InlineData(InvalidAck.WrongType)]
    [InlineData(InvalidAck.DuplicateField)]
    [InlineData(InvalidAck.ZeroLength)]
    [InlineData(InvalidAck.OversizeLength)]
    [InlineData(InvalidAck.UnsignedLength)]
    [InlineData(InvalidAck.TruncatedPrefix)]
    [InlineData(InvalidAck.TruncatedPayload)]
    public async Task Invalid_Ack_Closes_Only_Video_And_Preserves_Exact_Failure(InvalidAck invalid)
    {
        await using TlsScenario f = new(Reply.Invalid, invalid: invalid);
        await f.OpenControlAsync();
        Exception error = await f.FailureAsync(f.Attach());
        if (invalid is InvalidAck.TruncatedPrefix or InvalidAck.TruncatedPayload)
            Assert.IsType<EndOfStreamException>(error);
        else
        {
            string reason = invalid switch
            {
                InvalidAck.WrongSessionId => "video-attach-ack-session-mismatch",
                InvalidAck.WrongType => VideoAttachAckFrame.RejectWrongType,
                InvalidAck.DuplicateField => VideoAttachAckFrame.RejectMalformedJson,
                InvalidAck.ZeroLength => FrameReader.RejectZeroLength,
                InvalidAck.OversizeLength or InvalidAck.UnsignedLength => FrameReader.RejectTooLarge,
                _ => throw new InvalidOperationException("未声明的 ACK 负例。"),
            };
            Assert.Equal(reason, Assert.IsType<FrameProtocolException>(error).Reason);
        }
        await f.HelloVerified.Task.WaitAsync(Guard);
        // 不先调用父/child 的兜底关闭：入口返回失败时 timer 和诊断必须已收尾，再等服务端观察关闭。
        Assert.Equal(0, f.Clock.TimerCount);
        Assert.Same(error, Assert.Single(f.Control.LifetimeErrors));
        await f.PeerClosed.Task.WaitAsync(Guard);
        await f.WaitForConnectionCountAsync(1);
        f.AssertControlAlive();
    }

    [Fact]
    public async Task Second_Ack_Is_Not_Consumed_By_Initialization_And_Reader_Rejects_It()
    {
        await using TlsScenario f = new(Reply.SecondAck);
        await f.OpenControlAsync();
        var child = await f.Attach().WaitAsync(Guard);
        Exception error = await f.FailureAsync(f.Read(child));
        Assert.Equal("video-magic", Assert.IsType<FrameProtocolException>(error).Reason);
        await f.Own(child.StopAndJoinAsync()).WaitAsync(Guard);
        await f.PeerClosed.Task.WaitAsync(Guard);
        await f.WaitForConnectionCountAsync(1);
        Assert.Same(error, Assert.Single(child.LifetimeErrors));
        f.AssertControlAlive();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hanging_Ack_Is_Stopped_By_Caller_Cancellation_Or_Parent_Revoke(bool revokeParent)
    {
        await using TlsScenario f = new(Reply.Hang);
        using CancellationTokenSource caller = new();
        await f.OpenControlAsync();
        Task<AuthenticatedControlSession.ClientVideoLifetime> attach = f.Attach(caller.Token);
        await f.HelloVerified.Task.WaitAsync(Guard);
        Assert.False(attach.IsCompleted);
        Task stop = revokeParent ? f.Control.CloseAndJoinAsync() : caller.CancelAsync();
        await f.Own(stop).WaitAsync(Guard);
        Exception error = await f.FailureAsync(attach);
        // 真实 SSL 取消与 socket 关闭存在竞态；只接受这三类原 I/O 终止，不接受 Timeout 或认证/协议错误。
        Assert.True(error is OperationCanceledException or ObjectDisposedException or IOException,
            $"挂起 ACK 的终止类型错误：{error.GetType().FullName}");
        Assert.Same(error, Assert.Single(f.Control.LifetimeErrors));
        Assert.Equal(0, f.Clock.TimerCount);
        await f.PeerClosed.Task.WaitAsync(Guard);
        await f.WaitForConnectionCountAsync(revokeParent ? 0 : 1);
        if (revokeParent)
        {
            Assert.Empty(f.Context.SessionRegistry.Snapshot());
            Assert.Throws<ObjectDisposedException>(() => { _ = f.Control.Stream; });
        }
        else
        {
            Assert.True(caller.IsCancellationRequested);
            f.AssertControlAlive();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Duplicate_Attach_Loser_Does_Not_Stop_Pending_Or_Delivered_Winner(bool delivered)
    {
        await using TlsScenario f = new(Reply.Fragmented);
        await f.OpenControlAsync();
        Task<AuthenticatedControlSession.ClientVideoLifetime> winner = f.Attach();
        await f.PartialAck.Task.WaitAsync(Guard);
        if (delivered)
        {
            f.ReleaseAck.TrySetResult();
            await winner.WaitAsync(Guard);
        }
        Exception error = await f.FailureAsync(f.Attach());
        Assert.IsType<InvalidOperationException>(error);
        if (!delivered) Assert.False(winner.IsCompleted);
        f.ReleaseAck.TrySetResult();
        var child = await winner.WaitAsync(Guard);
        using EncodedFrame? frame = await f.Read(child).WaitAsync(Guard);
        AssertGoldenFrame(frame);
        await f.AssertDistinctConnectionsAsync();
        Assert.Equal(2, f.HandlerCount);
        Assert.False(f.PeerClosed.Task.IsCompleted);
        Assert.Empty(child.LifetimeErrors);
        f.AssertControlAlive();
    }

    [Fact]
    public async Task Host_Policy_Allows_Control_But_Rejects_Second_Connection_Before_Handler()
    {
        await using TlsScenario f = new(rejectSecond: true);
        await f.OpenControlAsync();
        Exception error = await f.FailureAsync(f.Attach());
        await f.Policy.Rejected.Task.WaitAsync(Guard);
        Assert.True(error is IOException or AuthenticationException or System.Net.Sockets.SocketException,
            $"TLS 前拒绝应当表现为建连/握手失败，而非 {error.GetType().FullName}");
        Assert.Equal(2, f.Policy.Calls);
        Assert.Equal(1, f.HandlerCount);
        Assert.False(f.Second.Task.IsCompleted);
        await f.WaitForConnectionCountAsync(1);
        Assert.Equal(0, f.Clock.TimerCount);
        f.AssertControlAlive();
    }

    [Fact]
    public async Task Second_Tls_Changed_Certificate_Is_Rejected_By_Real_Pin_Callback()
    {
        await using TlsScenario f = new(rotateCertificate: true);
        await f.OpenControlAsync();
        Assert.True(f.Control.Identity.PinsMatch);
        Exception error = await f.FailureAsync(f.Attach());
        Assert.Contains(PeerCertificateValidator.RejectionPinMismatch,
            Assert.IsType<AuthenticationException>(error).Message);
        await f.CertificateSwitch!.SecondAccepted.Task.WaitAsync(Guard);
        Assert.NotEqual(TestCertificateFactory.Fingerprint(f.Certificate),
            TestCertificateFactory.Fingerprint(f.CertificateSwitch.Certificate));
        Assert.Equal(1, f.HandlerCount);
        Assert.Equal(0, f.Clock.TimerCount);
        f.AssertControlAlive();
    }

    private static void AssertGoldenFrame(EncodedFrame? frame)
    {
        Assert.NotNull(frame);
        Assert.Equal(VideoCodec.Jpeg, frame.Codec);
        Assert.Equal(1920, frame.Width);
        Assert.Equal(1080, frame.Height);
        Assert.Equal(0x0123456789ABCDEFul, frame.FrameId);
        Assert.Equal(0x1020304050607080L, frame.TimestampUs);
        Assert.Equal((byte)60, frame.JpegQuality);
        Assert.Equal(VideoFrameTestData.Payload(), frame.Payload.ToArray());
    }
}
