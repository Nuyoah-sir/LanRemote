using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

public sealed class FirstFrameRouterFailureTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly Guid ServerDeviceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid ClientDeviceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid VideoSessionId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
    private static readonly byte[] AccessKey = Convert.FromHexString("0F1E2D3C4B5A69788796A5B4C3D2E1F0");
    // 真实时间只作测试看门狗；生产 deadline 使用静止时钟，Control 写预算也不抢跑闸门。
    private static readonly TransportTimeouts Timeouts = new(
        TransportTimeouts.Maximum, TransportTimeouts.Maximum, TransportTimeouts.Maximum,
        TransportTimeouts.Maximum, TransportTimeouts.Maximum, TransportTimeouts.Maximum);

    [Theory(Timeout = 60_000)]
    [InlineData("io")]
    [InlineData("disposed")]
    [InlineData("uncancelled")]
    public async Task Video_Source_Failure_After_Ack_Preserves_Original_Exception(string failureKind)
    {
        Exception expected = failureKind switch
        {
            "io" => new IOException("受控帧源 IO 故障。"),
            "disposed" => new ObjectDisposedException("受控帧源"),
            _ => new OperationCanceledException("帧源故障，没有任何取消请求。", CancellationToken.None),
        };
        await using Scenario scenario = new([expected], video: true);
        Task run = scenario.Start();

        await scenario.Source.Entered.Task.WaitAsync(Guard);
        AssertVideoStarted(scenario, run);
        Assert.False(scenario.Source.LifetimeToken.IsCancellationRequested);
        scenario.Source.Release(expected);

        await AssertClosedButStillJoiningAsync(scenario, run);
        Assert.False(scenario.Source.CancelledAtRelease);
        Assert.Same(expected, await CaptureAsync(scenario.Source.Operation!));
        scenario.Stream.ReleaseRead();
        Exception? actual = await CaptureAsync(run);
        AssertJoined(scenario);
        Assert.Empty(scenario.Stream.VideoWrites);
        // 当前 transfer 的网络异常豁免会吞掉帧源故障，Router 错误地正常返回。
        Assert.Same(expected, actual);
    }

    [Fact(Timeout = 60_000)]
    public async Task Video_Sole_Frame_Owner_Dispose_IOException_Preserves_Original_Exception()
    {
        IOException expected = new("唯一 frame owner 释放失败。");
        RecordingVideoOwner owner = new(5) { DisposeFailure = expected };
        VideoFrameTestData.Payload().CopyTo(owner.Bytes, 0);
        await using Scenario scenario = new([expected], video: true, owner: owner);
        Task run = scenario.Start();

        await scenario.Source.Entered.Task.WaitAsync(Guard);
        AssertVideoStarted(scenario, run);
        Assert.Equal(0, owner.DisposeCalls);
        scenario.Source.Release();

        await AssertClosedButStillJoiningAsync(scenario, run);
        Assert.True(scenario.Stream.VideoFlushed.Task.IsCompletedSuccessfully);
        Assert.Equal(1, owner.DisposeCalls);
        scenario.Stream.ReleaseRead();
        Exception? actual = await CaptureAsync(run);
        AssertJoined(scenario);
        Assert.Equal(1, scenario.Source.ReadCalls);
        Assert.Equal(1, owner.DisposeCalls);
        Assert.Collection(scenario.Stream.VideoWrites,
            bytes => Assert.Equal(VideoFrameTestData.Header(), bytes),
            bytes => Assert.Equal(VideoFrameTestData.Payload(), bytes));
        // sender.CleanupErrors 能保留此单项，但 Router 外层 IOException catch 又将它吞掉。
        Assert.Same(expected, actual);
    }

    [Fact(Timeout = 60_000)]
    public async Task Control_Success_Write_And_Pending_Read_Failures_Preserve_Both_Original_Exceptions()
    {
        InvalidOperationException writeFailure = new("E1：auth_success 写出失败。");
        InvalidOperationException readFailure = new("E2：关闭后原 pending read 意外失败。");
        await using Scenario scenario = new([writeFailure, readFailure], successWriteFailure: writeFailure);
        Task run = scenario.Start();

        await scenario.Approval.Entered.Task.WaitAsync(Guard);
        AssertControlPending(scenario, run);
        scenario.Approval.Release();
        await scenario.Stream.SuccessWriteEntered.Task.WaitAsync(Guard);
        Assert.False(scenario.Stream.PendingReadOperation!.IsCompleted);
        Assert.False(run.IsCompleted);
        Assert.False(scenario.Stream.CloseObserved.Task.IsCompleted);
        scenario.Stream.ReleaseSuccessWrite();

        await AssertClosedButStillJoiningAsync(scenario, run);
        Assert.Equal(new[] { "auth_challenge", "approval_pending", "auth_success" },
            scenario.Stream.WriteAttempts);
        // 只在物理关闭已完成后放行 E2；它不能在 success 写失败前抢占认证终态。
        scenario.Stream.ReleaseRead(readFailure);
        Exception? actual = await CaptureAsync(run);
        AssertJoined(scenario);
        Assert.True(scenario.Approval.Operation!.IsCompletedSuccessfully);
        Assert.Same(readFailure, await CaptureAsync(scenario.Stream.PendingReadOperation!));
        AssertControlReleased(scenario);
        // 当前 finally 中的 join 抛 E2，覆盖 try 中尚未传播的 E1。
        AssertOriginalPair(run, actual, writeFailure, readFailure);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false, "io")]
    [InlineData(false, "disposed")]
    [InlineData(false, "unexpected")]
    [InlineData(true, "io")]
    [InlineData(true, "disposed")]
    [InlineData(true, "unexpected")]
    public async Task Control_Gate_And_Pending_Read_Failures_Join_Both_And_Preserve_Original_Exceptions(
        bool gateFaultFirst, string gateFaultKind)
    {
        Exception gateFailure = gateFaultKind switch
        {
            "io" => new IOException("G：审批 gate IO 故障，不是网络断连。"),
            "disposed" => new ObjectDisposedException("G：审批 gate"),
            _ => new InvalidOperationException("G：审批 gate 意外故障。"),
        };
        InvalidOperationException readFailure = new("R：原 pending read 意外故障。");
        await using Scenario scenario = new([gateFailure, readFailure]);
        Task run = scenario.Start();

        await scenario.Approval.Entered.Task.WaitAsync(Guard);
        AssertControlPending(scenario, run);
        // 沿用 ControlAuthCleanupTests 的两种顺序，但观察真实 Router，而非直接调用 session join。
        if (gateFaultFirst)
            scenario.Approval.Release(gateFailure);
        else
            scenario.Stream.ReleaseRead(readFailure);

        await scenario.Stream.CloseObserved.Task.WaitAsync(Guard);
        await scenario.Connection.CloseAsync().WaitAsync(Guard);
        Assert.False(run.IsCompleted);
        Assert.Equal(new[] { "auth_challenge", "approval_pending", "authentication_failed" },
            scenario.Stream.WriteAttempts);
        AssertControlReleased(scenario);
        if (gateFaultFirst)
        {
            Assert.Same(gateFailure, await CaptureAsync(scenario.Approval.Operation!));
            Assert.False(scenario.Stream.PendingReadOperation!.IsCompleted);
            scenario.Stream.ReleaseRead(readFailure);
        }
        else
        {
            Assert.Same(readFailure, await CaptureAsync(scenario.Stream.PendingReadOperation!));
            Assert.False(scenario.Approval.Operation!.IsCompleted);
            scenario.Approval.Release(gateFailure);
        }

        Exception? actual = await CaptureAsync(run);
        AssertJoined(scenario);
        Assert.True(scenario.Approval.Operation!.IsFaulted);
        Assert.Same(gateFailure, await CaptureAsync(scenario.Approval.Operation!));
        Assert.Same(readFailure, await CaptureAsync(scenario.Stream.PendingReadOperation!));
        AssertControlReleased(scenario);
        // 当前直接 await WhenAll 只把一个异常带出 Router，另一原始实例丢失。
        AssertOriginalPair(run, actual, gateFailure, readFailure);
    }

    private static void AssertVideoStarted(Scenario scenario, Task run)
    {
        Assert.True(scenario.Connection.HasCloseAuthority);
        Assert.Equal(VideoAttachStatus.Attached, scenario.Router.AttachStatus);
        Assert.Equal(1, scenario.Router.AttachAttempts);
        Assert.Equal(VideoSessionId, scenario.Stream.AckSessionId);
        Assert.True(scenario.Stream.AckFlushed.Task.IsCompletedSuccessfully);
        Assert.Equal(VideoSessionId, scenario.Source.SessionId);
        Assert.Equal(1, scenario.Source.ReadCalls);
        Assert.Equal(1, scenario.Stream.PendingReadCalls);
        Assert.False(scenario.Stream.PendingReadOperation!.IsCompleted);
        Assert.False(scenario.Source.Operation!.IsCompleted);
        Assert.False(run.IsCompleted);
    }

    private static void AssertControlPending(Scenario scenario, Task run)
    {
        Assert.True(scenario.Connection.HasCloseAuthority);
        Assert.Equal(4, scenario.Stream.FramedReadCalls); // hello 与 auth_response 各有前缀、载荷两次读。
        Assert.Equal(1, scenario.Stream.PendingReadCalls);
        Assert.False(scenario.Stream.PendingReadOperation!.IsCompleted);
        Assert.Equal(1, scenario.Approval.Calls);
        Assert.False(scenario.Approval.Operation!.IsCompleted);
        Assert.False(scenario.Approval.Token.IsCancellationRequested);
        Assert.Equal(1, scenario.Context.PendingApprovalLimiter.GlobalInUse);
        Assert.False(run.IsCompleted);
    }

    private static void AssertControlReleased(Scenario scenario)
    {
        Assert.Equal(0, scenario.Context.SessionRegistry.ActiveSessionCount);
        Assert.Equal(0, scenario.Context.PendingApprovalLimiter.GlobalInUse);
        Assert.Equal(0, scenario.Context.FailedAuthLimiter.CountRecentFailures(IPAddress.Loopback));
        Assert.Equal(1, scenario.Approval.Calls);
        Assert.Equal(0, scenario.Source.ReadCalls);
    }

    private static async Task AssertClosedButStillJoiningAsync(Scenario scenario, Task run)
    {
        await scenario.Stream.CloseObserved.Task.WaitAsync(Guard);
        await scenario.Connection.CloseAsync().WaitAsync(Guard);
        Assert.False(scenario.Stream.PendingReadOperation!.IsCompleted);
        Assert.False(run.IsCompleted);
    }

    private static void AssertJoined(Scenario scenario)
    {
        Assert.True(scenario.Run!.IsCompleted);
        Assert.True(scenario.Connection.CloseAsync().IsCompletedSuccessfully);
        Assert.True(scenario.Stream.PendingReadOperation!.IsCompleted);
        Assert.Equal(1, scenario.Stream.PendingReadCalls);
        Assert.Equal(1, scenario.Stream.DisposeCalls);
        Assert.Empty(scenario.Closer.CleanupErrors);
    }

    private static void AssertOriginalPair(Task run, Exception? actual, Exception first, Exception second)
    {
        // 同时约束 await 的最终异常与 Router 返回任务；不以底层 TCS 的聚合充当 Router 的证据。
        AggregateException aggregate = Assert.IsType<AggregateException>(actual);
        AssertPair(aggregate);
        Assert.True(run.IsFaulted);
        AssertPair(Assert.IsType<AggregateException>(run.Exception));

        void AssertPair(AggregateException error)
        {
            var leaves = error.Flatten().InnerExceptions;
            Assert.Equal(2, leaves.Count);
            Assert.Same(first, Assert.Single(leaves, item => ReferenceEquals(item, first)));
            Assert.Same(second, Assert.Single(leaves, item => ReferenceEquals(item, second)));
        }
    }

    private static async Task<Exception?> CaptureAsync(Task task)
    {
        try { await task.WaitAsync(Guard); return null; }
        catch (Exception error) when (task.IsCompleted && error is not TimeoutException) { return error; }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<Exception?> NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ConnectionSecurityContext Security() => new(
        IPAddress.Loopback, IPAddress.Loopback, 12345, SslProtocols.None, new byte[CertificatePin.LengthBytes]);

    private static byte[] Wire(byte[] payload)
    {
        byte[] wire = new byte[TransportConstants.LengthPrefixBytes + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(wire, (uint)payload.Length);
        payload.CopyTo(wire, TransportConstants.LengthPrefixBytes);
        return wire;
    }

    // 无 listener/socket/TLS 握手；仅合成视频登记前置条件，hello/attach/ACK/发送均走真实 Router。
    // await using 生成 finally：先放行全部闸门，再 join Router 与实际返回的受控操作任务。
    private sealed class Scenario : IAsyncDisposable
    {
        private readonly Exception[] _expectedFailures;
        private readonly SessionRegistry.SessionRegistration? _registration;

        internal Scenario(Exception[] expectedFailures, bool video = false,
            Exception? successWriteFailure = null, RecordingVideoOwner? owner = null)
        {
            _expectedFailures = expectedFailures;
            Context = new ControlAuthContext
            {
                ServerDeviceId = ServerDeviceId,
                AccessSecretStore = new FixedSecretStore(),
                FailedAuthLimiter = new FailedAuthLimiter(),
                PendingApprovalLimiter = new ConnectionAdmissionLimiter(3, 1),
                ApprovalGate = Approval,
                SessionRegistry = new SessionRegistry(),
                TimeProvider = new ManualDeadlineClock(),
                Options = new ControlAuthOptions { FailureDelayMin = TimeSpan.Zero, FailureDelayMax = TimeSpan.Zero },
            };
            ConnectionSecurityContext security = Security();
            byte[] hello = HelloFrame.Serialize();
            if (video)
            {
                byte[] token = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
                byte[] nonce = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
                // 与 VideoAttachTlsTests 相同的独立 transcript，不调用生产视频 proof helper。
                byte[] transcript = [.. Encoding.UTF8.GetBytes("LANREMOTE-VIDEO-V1\0"),
                    .. Convert.FromHexString(VideoSessionId.ToString("N")), .. nonce,
                    .. security.ServerCertificateSha256.ToArray()];
                byte[] proof = HMACSHA256.HashData(token, transcript);
                _registration = Context.SessionRegistry.Register(VideoSessionId, Security().ConnectionId,
                    ClientDeviceId, "受控视频客户端", SessionPermission.ViewOnly, IPAddress.Loopback, 12344,
                    token, new VideoAttachWindow(Context.TimeProvider, 15_000),
                    security.ServerCertificateSha256.Span, CancellationToken.None);
                CryptographicOperations.ZeroMemory(token);
                hello = new VideoHelloFrame(VideoSessionId, nonce, proof).Serialize();
            }
            Stream = new ControlledSslStream(hello, video, successWriteFailure);
            Closer = new ConnectionCloseHandle(new MemoryStream(), new HostLifecycleErrors());
            Closer.Attach(Stream);
            Connection = new AcceptedConnection(security, Stream, Closer);
            Source = new GatedFrameSource(Stream, owner);
            Router = new FirstFrameRouter(Context, Timeouts, Source);
        }

        internal ControlAuthContext Context { get; }
        internal GatedApproval Approval { get; } = new();
        internal ControlledSslStream Stream { get; }
        internal GatedFrameSource Source { get; }
        internal ConnectionCloseHandle Closer { get; }
        internal AcceptedConnection Connection { get; }
        internal FirstFrameRouter Router { get; }
        internal Task? Run { get; private set; }
        internal Task Start() => Run = Router.RunAsync(Connection, CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            // 提前断言失败时也只产生已知故障，不让尚未放行的审批意外转入 success/holding。
            Approval.Release(_expectedFailures[0]);
            Source.Release(_expectedFailures[0]);
            Stream.ReleaseSuccessWrite();
            Stream.ReleaseRead();
            try
            {
                await Task.WhenAll(Connection.CloseAsync().WaitAsync(Guard),
                    JoinAsync(Run), JoinAsync(Approval.Operation),
                    JoinAsync(Source.Operation), JoinAsync(Stream.PendingReadOperation));
            }
            finally
            {
                _registration?.Dispose();
            }
        }

        private async Task JoinAsync(Task? task)
        {
            if (task is null) return;
            Exception? error = await CaptureAsync(task);
            if (error is null) return;
            IEnumerable<Exception> leaves = error is AggregateException aggregate
                ? aggregate.Flatten().InnerExceptions : new[] { error };
            // 只接受本用例注入的原实例；清理不能掩盖断言或看门狗异常。
            Assert.All(leaves, leaf => Assert.Contains(_expectedFailures, item => ReferenceEquals(item, leaf)));
        }
    }

    private sealed class GatedApproval : ILocalApprovalGate
    {
        private readonly TaskCompletionSource<Exception?> _release = NewGate();
        internal TaskCompletionSource Entered { get; } = NewSignal();
        internal Task<LocalApprovalDecision>? Operation { get; private set; }
        internal CancellationToken Token { get; private set; }
        internal int Calls { get; private set; }
        internal void Release(Exception? failure = null) => _release.TrySetResult(failure);

        public ValueTask<LocalApprovalDecision> RequestApprovalAsync(LocalApprovalRequest request, CancellationToken token)
        {
            Calls++;
            Token = token;
            Operation = CompleteAsync(request);
            Entered.TrySetResult();
            return new ValueTask<LocalApprovalDecision>(Operation);
        }

        private async Task<LocalApprovalDecision> CompleteAsync(LocalApprovalRequest request)
        {
            // 故意不响应取消：Router 必须等待原 gate，而不是遗弃它的结果。
            Exception? failure = await _release.Task.ConfigureAwait(false);
            if (failure is not null) throw failure;
            return new LocalApprovalDecision(request.RequestId, LocalApprovalOutcome.Approved, request.RequestedPermission);
        }
    }

    private sealed class GatedFrameSource(ControlledSslStream stream, RecordingVideoOwner? owner) : IVideoFrameSource
    {
        private readonly TaskCompletionSource<Exception?> _release = NewGate();
        internal TaskCompletionSource Entered { get; } = NewSignal();
        internal Task<EncodedFrame?>? Operation { get; private set; }
        internal CancellationToken LifetimeToken { get; private set; }
        internal Guid SessionId { get; private set; }
        internal int ReadCalls { get; private set; }
        internal bool CancelledAtRelease { get; private set; }
        internal void Release(Exception? failure = null) => _release.TrySetResult(failure);

        public ValueTask<EncodedFrame?> ReadNextAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            Assert.True(stream.AckFlushed.Task.IsCompletedSuccessfully);
            Assert.Equal(1, ++ReadCalls);
            SessionId = sessionId;
            LifetimeToken = cancellationToken;
            Operation = CompleteAsync(cancellationToken);
            Entered.TrySetResult();
            return new ValueTask<EncodedFrame?>(Operation);
        }

        private async Task<EncodedFrame?> CompleteAsync(CancellationToken token)
        {
            Exception? failure = await _release.Task.ConfigureAwait(false);
            CancelledAtRelease = token.IsCancellationRequested;
            if (failure is not null) throw failure;
            return owner is null ? null : VideoFrameTestData.Frame(owner);
        }
    }

    private sealed class ControlledSslStream(byte[] hello, bool video, Exception? successWriteFailure)
        : SslStream(new MemoryStream())
    {
        private byte[] _input = Wire(hello);
        private int _offset;
        private int _disposeCalls;
        private string? _lastType;
        private readonly TaskCompletionSource<Exception?> _readRelease = NewGate();
        private readonly TaskCompletionSource _successRelease = NewSignal();
        internal TaskCompletionSource CloseObserved { get; } = NewSignal();
        internal TaskCompletionSource SuccessWriteEntered { get; } = NewSignal();
        internal TaskCompletionSource AckFlushed { get; } = NewSignal();
        internal TaskCompletionSource VideoFlushed { get; } = NewSignal();
        internal Task<int>? PendingReadOperation { get; private set; }
        internal int PendingReadCalls { get; private set; }
        internal int FramedReadCalls { get; private set; }
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        internal Guid? AckSessionId { get; private set; }
        internal List<string> WriteAttempts { get; } = [];
        internal List<byte[]> VideoWrites { get; } = [];
        internal void ReleaseRead(Exception? failure = null) => _readRelease.TrySetResult(failure);
        internal void ReleaseSuccessWrite() => _successRelease.TrySetResult();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_offset < _input.Length)
            {
                int count = Math.Min(buffer.Length, _input.Length - _offset);
                _input.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                FramedReadCalls++;
                return ValueTask.FromResult(count);
            }
            Assert.Equal(1, buffer.Length);
            Assert.Equal(1, ++PendingReadCalls);
            PendingReadOperation = CompleteReadAsync();
            return new ValueTask<int>(PendingReadOperation);
        }

        private async Task<int> CompleteReadAsync()
        {
            // 关闭和取消都不抢先完成此原读；由测试固定 EOF/R 与 gate/write 的相对顺序。
            Exception? failure = await _readRelease.Task.ConfigureAwait(false);
            if (failure is not null) throw failure;
            return 0;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (video && AckFlushed.Task.IsCompletedSuccessfully)
            {
                VideoWrites.Add(buffer.ToArray());
                return;
            }
            Assert.True(buffer.Length > TransportConstants.LengthPrefixBytes);
            ReadOnlyMemory<byte> payload = buffer[TransportConstants.LengthPrefixBytes..];
            Assert.Equal((uint)payload.Length, BinaryPrimitives.ReadUInt32BigEndian(buffer.Span));
            using JsonDocument document = JsonDocument.Parse(payload);
            _lastType = document.RootElement.GetProperty("type").GetString()!;
            WriteAttempts.Add(_lastType);
            if (_lastType == "auth_challenge")
            {
                Assert.Equal(_input.Length, _offset);
                Assert.True(AuthChallengeFrame.TryParse(payload.Span, out var challenge, out _));
                byte[] nonce = new byte[AuthProtocol.NonceByteLength];
                byte[] transcript = AuthTranscriptBuilder.BuildClientTranscript(challenge!.SessionId,
                    challenge.ServerDeviceId, ClientDeviceId, challenge.ServerNonce.Span, nonce,
                    challenge.CertificateSha256.Span, SessionPermission.ViewOnly);
                byte[] proof = AuthTranscriptBuilder.ComputeClientProof(AccessKey, transcript);
                _input = Wire(new AuthResponseFrame(ClientDeviceId, "受控认证客户端", nonce,
                    SessionPermission.ViewOnly, proof).Serialize());
                _offset = 0;
            }
            else if (_lastType == "video_attach_ack")
            {
                Assert.True(VideoAttachAckFrame.TryParse(payload.Span, out var ack, out _));
                AckSessionId = ack!.SessionId;
            }
            else if (_lastType == "auth_success")
            {
                Assert.NotNull(successWriteFailure);
                SuccessWriteEntered.TrySetResult();
                await _successRelease.Task.ConfigureAwait(false);
                throw successWriteFailure;
            }
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (video && VideoWrites.Count != 0)
                VideoFlushed.TrySetResult();
            else if (_lastType == "video_attach_ack")
                AckFlushed.TrySetResult();
            return Task.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing) Interlocked.Increment(ref _disposeCalls);
                base.Dispose(disposing);
            }
            finally
            {
                if (disposing) CloseObserved.TrySetResult();
            }
        }
    }

    private sealed class FixedSecretStore : IAccessSecretStore
    {
        public Task<AccessSecret> LoadOrCreateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccessSecret(AccessKey.ToArray()));
        public Task<AccessSecret> RegenerateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("测试不轮换密钥。");
    }
}
