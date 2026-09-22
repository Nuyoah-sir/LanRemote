using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

public sealed partial class ControlClientConnectorTests
{
    private static readonly Guid ServerDeviceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid ClientDeviceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly byte[] GoodKey = Convert.FromHexString("0F1E2D3C4B5A69788796A5B4C3D2E1F0");
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private const string ClientName = "真实 TLS 测试客户端";
    private const string ProofFailureMessage = "远端身份验证失败，可能是错误密码或伪造设备广播";

    private static ControlClientAuthOptions ClientOptions => new()
    {
        MachineWindow = TimeSpan.FromSeconds(5),
        ApprovalWindow = TimeSpan.FromSeconds(5),
    };

    private static TransportTimeouts Timeouts(int prefixMs = 5000, int payloadMs = 5000) => new(
        connectTimeout: TimeSpan.FromSeconds(3),
        handshakeTimeout: TimeSpan.FromSeconds(3),
        lengthPrefixTimeout: TimeSpan.FromMilliseconds(prefixMs),
        payloadTimeout: TimeSpan.FromMilliseconds(payloadMs),
        helloTimeout: TimeSpan.FromSeconds(2),
        preAuthEnvelopeTimeout: TimeSpan.FromSeconds(10));

    [Theory(Timeout = 30_000)]
    [InlineData(true, SessionPermission.Control)]
    [InlineData(true, SessionPermission.ViewOnly)]
    [InlineData(false, SessionPermission.Control)]
    public async Task Real_ControlAuthSession_Accepts_Correct_Key_And_Holds_Registered_Session(
        bool requireApproval, SessionPermission granted)
    {
        RecordingApprovalGate gate = new(granted);
        ControlAuthContext context = CreateContext(gate, requireApproval);
        ControlAuthResult? result = null;
        await using TlsScenario scenario = new(async (connection, ct) =>
        {
            ControlAuthSession auth = await BeginRealAuthenticationAsync(connection, context, ct);
            result = await auth.RunAsync(ct);
        });
        scenario.Start();
        AuthenticatedControlSession session = await scenario.ConnectAsync().WaitAsync(Guard);

        // RunAsync 在连接关闭前不返回；先验证活登记，不能先等待 result。
        await WaitUntilAsync(() => context.SessionRegistry.ActiveSessionCount == 1);
        Assert.Null(result);
        Assert.False(scenario.ServerFinished.Task.IsCompleted);
        Assert.Equal(1, scenario.Host.ActiveConnections);
        ControlSessionSummary summary = Assert.Single(context.SessionRegistry.Snapshot());
        Assert.Equal(session.SessionId, summary.SessionId);
        Assert.Equal(ClientDeviceId, summary.ClientDeviceId);
        Assert.Equal(ClientName, summary.ClientName);
        Assert.Equal(granted, summary.GrantedPermission);
        Assert.Equal(granted, session.GrantedPermission);
        Assert.Equal(IPAddress.Loopback, summary.RemoteAddress);
        Assert.Equal(ServerDeviceId, session.Identity.DeviceId);
        Assert.True(session.Identity.PinsMatch);
        Assert.Equal(scenario.Target.ExpectedCertSha256.ToArray(), session.Identity.PresentedCertSha256.ToArray());
        Assert.Matches("^[0-9A-F]{6}$", session.ShortCode);
        Assert.Equal(requireApproval ? 1 : 0, gate.RequestCount);
        if (requireApproval)
        {
            LocalApprovalRequest request = Assert.IsType<LocalApprovalRequest>(gate.LastRequest);
            Assert.Equal(session.SessionId, request.SessionId);
            Assert.Equal(session.ShortCode, request.ShortCode);
            Assert.Equal(SessionPermission.Control, request.RequestedPermission);
        }
        else
        {
            Assert.Null(gate.LastRequest);
        }

        ReadOnlyMemory<byte> tokenView = session.SessionToken;
        Assert.Equal(32, tokenView.Length);
        Assert.Contains(tokenView.ToArray(), value => value != 0);
        Assert.True(context.SessionRegistry.TryGetSessionToken(session.SessionId, out ReadOnlyMemory<byte> serverToken));
        Assert.Equal(serverToken.ToArray(), tokenView.ToArray());
        Assert.Equal(15_000, session.VideoAttachExpiresInMsHint);
        Assert.Equal(0, context.FailedAuthLimiter.CountRecentFailures(IPAddress.Loopback));

        session.Dispose();
        session.Dispose();
        Assert.All(tokenView.ToArray(), value => Assert.Equal((byte)0, value));
        Assert.Throws<ObjectDisposedException>(() => session.SessionToken);
        Assert.Throws<ObjectDisposedException>(() => session.Stream);
        await scenario.AssertServerFinishedAsync();
        Assert.NotNull(result);
        Assert.True(result.Completed, result.Rejection);
        Assert.Equal(ControlSessionState.Authenticated, result.State);
        Assert.Equal(session.SessionId, result.SessionId);
        Assert.Equal(0, context.SessionRegistry.ActiveSessionCount);
        Assert.False(context.SessionRegistry.TryGetSessionToken(session.SessionId, out _));
        Assert.All(serverToken.ToArray(), value => Assert.Equal((byte)0, value));
    }

    [Fact(Timeout = 30_000)]
    public async Task Real_ControlAuthSession_Rejects_Wrong_Key_Generically_Without_Session_Or_Token()
    {
        RecordingApprovalGate gate = new(SessionPermission.Control);
        ControlAuthContext context = CreateContext(gate, requireApproval: true);
        ControlAuthResult? result = null;
        await using TlsScenario scenario = new(async (connection, ct) =>
        {
            ControlAuthSession auth = await BeginRealAuthenticationAsync(connection, context, ct);
            result = await auth.RunAsync(ct);
        });
        scenario.Start();
        byte[] wrongKey = Convert.FromHexString("F0E1D2C3B4A5968778695A4B3C2D1E0F");
        Task<AuthenticatedControlSession> client = scenario.ConnectAsync(key: wrongKey);
        await AssertRejectedAsync(scenario, client, "client-remote-authentication-failed", "远端拒绝了认证或审批请求。");
        Assert.NotNull(result);
        Assert.False(result.Completed);
        Assert.Equal(ControlAuthSession.RejectProofMismatch, result.Rejection);
        Assert.Equal(ControlSessionState.Closed, result.State);
        Assert.Equal(0, context.SessionRegistry.ActiveSessionCount);
        Assert.Empty(context.SessionRegistry.Snapshot());
        Assert.False(context.SessionRegistry.TryGetSessionToken(result.SessionId, out _));
        Assert.Equal(0, gate.RequestCount);
        Assert.Equal(1, context.FailedAuthLimiter.CountRecentFailures(IPAddress.Loopback));
    }

    [Fact]
    public void Authenticated_Session_Public_Surface_Exposes_Neither_Stream_Nor_Token()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        MemberInfo[] members = typeof(AuthenticatedControlSession).GetMembers(flags);
        Assert.DoesNotContain(members, member =>
            member.Name.Contains("Stream", StringComparison.OrdinalIgnoreCase)
            || member.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            new[] { "GrantedPermission", "Identity", "SessionId", "ShortCode" },
            typeof(AuthenticatedControlSession).GetProperties(flags).Select(property => property.Name).Order().ToArray());
        Assert.Empty(typeof(AuthenticatedControlSession).GetConstructors());
        Assert.DoesNotContain(typeof(AuthenticatedControlSession).GetMethods(flags), method =>
            typeof(Stream).IsAssignableFrom(method.ReturnType));
    }

    [Theory(Timeout = 30_000)]
    [InlineData(SessionPermission.Control, SessionPermission.Control)]
    [InlineData(SessionPermission.Control, SessionPermission.ViewOnly)]
    [InlineData(SessionPermission.ViewOnly, SessionPermission.ViewOnly)]
    public async Task Independent_Oracle_Verifies_Response_Binds_Actual_Tls_Pin_And_Client_Accepts_Server_Proof(
        SessionPermission requested, SessionPermission granted)
    {
        ScriptedPeer? observedPeer = null;
        await using TlsScenario scenario = TlsScenario.Scripted(async (peer, ct) =>
        {
            observedPeer = peer;
            await peer.SendChallengeAsync(ct);
            byte[] transcript = await peer.ReadAndVerifyResponseAsync(requested, ct);
            await peer.SendAsync(peer.Success(transcript, granted), ct);
            await peer.AssertClosedWithoutDataAsync(ct);
        });
        scenario.Start();
        AuthenticatedControlSession session = await scenario.ConnectAsync(requested).WaitAsync(Guard);
        ScriptedPeer peer = Assert.IsType<ScriptedPeer>(observedPeer);
        Assert.Equal(peer.SessionId, session.SessionId);
        Assert.Equal(granted, session.GrantedPermission);
        Assert.Equal(peer.ActualPin, session.Identity.PresentedCertSha256.ToArray());
        ReadOnlyMemory<byte> tokenView = session.SessionToken;
        Assert.Equal(peer.Token, tokenView.ToArray());
        Assert.False(scenario.ServerFinished.Task.IsCompleted);
        session.Dispose();
        session.Dispose();
        Assert.All(tokenView.ToArray(), value => Assert.Equal((byte)0, value));
        await scenario.AssertServerFinishedAsync();
    }

    [Theory(Timeout = 30_000)]
    [InlineData("proof", false, "client-server-proof-mismatch", ProofFailureMessage)]
    [InlineData("proof", true, "client-server-proof-mismatch", ProofFailureMessage)]
    [InlineData("grant", false, "client-server-proof-mismatch", ProofFailureMessage)]
    [InlineData("grant", true, "client-server-proof-mismatch", ProofFailureMessage)]
    [InlineData("overgrant", false, "client-overgrant", "远端授予了超出请求的权限。")]
    [InlineData("overgrant", true, "client-overgrant", "远端授予了超出请求的权限。")]
    public async Task Invalid_Proof_Or_Grant_Fails_Closed_Without_Returning_Token_Or_Sending_Input(
        string mutation, bool pending, string rejection, string message)
    {
        SessionPermission requested = mutation == "overgrant" ? SessionPermission.ViewOnly : SessionPermission.Control;
        await using TlsScenario scenario = TlsScenario.Scripted(async (peer, ct) =>
        {
            await peer.SendChallengeAsync(ct);
            byte[] transcript = await peer.ReadAndVerifyResponseAsync(requested, ct);
            if (pending)
            {
                await peer.SendPendingAsync(ct);
            }

            // grant 篡改只改线上权限，保留原 control proof；overgrant 则故意提供有效的 control proof。
            SessionPermission wireGrant = mutation == "grant" ? SessionPermission.ViewOnly : SessionPermission.Control;
            byte[] proof = ScriptedPeer.ServerProof(transcript, SessionPermission.Control);
            if (mutation == "proof")
            {
                proof[0] ^= 0x80;
            }
            await peer.SendAsync(peer.SuccessWithProof(wireGrant, proof), ct);
            await peer.AssertClosedWithoutDataAsync(ct);
        });
        scenario.Start();
        await AssertRejectedAsync(scenario, scenario.ConnectAsync(requested), rejection, message);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(true, "client-server-device-mismatch", "远端设备身份与连接目标不一致。")]
    [InlineData(false, "client-challenge-pin-mismatch", "远端认证证书与实际 TLS 连接不一致。")]
    public async Task Challenge_Identity_Mismatch_Closes_Before_Any_Response(
        bool wrongDevice, string rejection, string message)
    {
        await using TlsScenario scenario = TlsScenario.Scripted(async (peer, ct) =>
        {
            byte[] otherPin = peer.ActualPin.ToArray();
            otherPin[0] ^= 0x80;
            await peer.SendAsync(peer.Challenge(
                deviceId: wrongDevice ? Guid.Parse("22222222-3333-4444-5555-666666666666") : ServerDeviceId,
                pin: wrongDevice ? peer.ActualPin : otherPin), ct);
            // 只消费过 hello：这里即使收到一个 auth_response 前缀字节也必须失败。
            await peer.AssertClosedWithoutDataAsync(ct);
        });
        scenario.Start();
        await AssertRejectedAsync(scenario, scenario.ConnectAsync(), rejection, message);
    }

    [Theory(Timeout = 30_000)]
    [InlineData("success", "challenge-wrong-type")]
    [InlineData("pending", "challenge-wrong-type")]
    [InlineData("unknown", "challenge-wrong-type")]
    [InlineData("malformed", "challenge-malformed-json")]
    public async Task Frames_Before_Challenge_Fail_Closed_Without_Response(string frame, string rejection)
    {
        await using TlsScenario scenario = TlsScenario.Scripted(async (peer, ct) =>
        {
            byte[] payload = frame switch
            {
                "success" => peer.SuccessWithProof(SessionPermission.Control, new byte[32]),
                "pending" => "{\"type\":\"approval_pending\"}"u8.ToArray(),
                "unknown" => "{\"type\":\"unknown_frame\"}"u8.ToArray(),
                "malformed" => "{"u8.ToArray(),
                _ => throw new ArgumentOutOfRangeException(nameof(frame)),
            };
            await peer.SendAsync(payload, ct);
            await peer.AssertClosedWithoutDataAsync(ct);
        });
        scenario.Start();
        await AssertRejectedAsync(scenario, scenario.ConnectAsync(), rejection, "远端认证 challenge 无效或顺序错误。");
    }

    [Theory(Timeout = 30_000)]
    [InlineData("challenge", false, "client-unexpected-auth-frame")]
    [InlineData("challenge", true, "client-unexpected-auth-frame")]
    [InlineData("unknown", false, "client-unexpected-auth-frame")]
    [InlineData("unknown", true, "client-unexpected-auth-frame")]
    [InlineData("malformed", false, "success-malformed-json")]
    [InlineData("malformed", true, "success-malformed-json")]
    [InlineData("incomplete-success", false, "success-missing-field")]
    [InlineData("incomplete-success", true, "success-missing-field")]
    [InlineData("malformed-pending", false, "client-unexpected-auth-frame")]
    [InlineData("malformed-pending", true, "client-unexpected-auth-frame")]
    public async Task Duplicate_Challenge_Unknown_And_Malformed_Replies_Fail_Closed(
        string frame, bool pending, string rejection)
    {
        await using TlsScenario scenario = TlsScenario.Scripted(async (peer, ct) =>
        {
            await peer.SendChallengeAsync(ct);
            _ = await peer.ReadAndVerifyResponseAsync(SessionPermission.Control, ct);
            if (pending)
            {
                await peer.SendPendingAsync(ct);
            }
            byte[] payload = frame switch
            {
                "challenge" => peer.Challenge(),
                "unknown" => "{\"type\":\"unknown_frame\"}"u8.ToArray(),
                "malformed" => "{"u8.ToArray(),
                "incomplete-success" => "{\"type\":\"auth_success\"}"u8.ToArray(),
                "malformed-pending" => "{\"type\":\"approval_pending\",\"extra\":true}"u8.ToArray(),
                _ => throw new ArgumentOutOfRangeException(nameof(frame)),
            };
            await peer.SendAsync(payload, ct);
            await peer.AssertClosedWithoutDataAsync(ct);
        });
        scenario.Start();
        await AssertRejectedAsync(scenario, scenario.ConnectAsync(), rejection, "远端认证帧无效或顺序错误。");
    }

    [Theory(Timeout = 30_000)]
    [InlineData(0, false, "length-zero")]
    [InlineData(0, true, "length-zero")]
    [InlineData(1_000_000, false, "length-exceeds-limit")]
    [InlineData(1_000_000, true, "length-exceeds-limit")]
    public async Task Invalid_Length_Prefix_Fails_Closed(int length, bool afterResponse, string rejection)
    {
        await using TlsScenario scenario = TlsScenario.Scripted(async (peer, ct) =>
        {
            if (afterResponse)
            {
                await peer.SendChallengeAsync(ct);
                _ = await peer.ReadAndVerifyResponseAsync(SessionPermission.Control, ct);
            }
            byte[] prefix = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)length);
            await peer.Stream.WriteAsync(prefix, ct);
            await peer.AssertClosedWithoutDataAsync(ct);
        });
        scenario.Start();
        await AssertRejectedAsync(scenario, scenario.ConnectAsync(), rejection, "远端认证数据格式错误。");
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_Cancellation_While_Waiting_For_Challenge_Or_Approval_Preserves_Oce_And_Closes(bool pending)
    {
        ClientDeadlineClock clock = new();
        TimeSpan approvalBudget = TimeSpan.FromSeconds(3);
        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using TlsScenario scenario = TlsScenario.Scripted(async (peer, ct) =>
        {
            if (pending)
            {
                await peer.SendChallengeAsync(ct);
                _ = await peer.ReadAndVerifyResponseAsync(SessionPermission.Control, ct);
                await peer.SendPendingAsync(ct);
            }
            ready.SetResult();
            await peer.AssertClosedWithoutDataAsync(ct);
        });
        scenario.Start();
        using CancellationTokenSource caller = new();
        Task<AuthenticatedControlSession> client = scenario.ConnectAsync(
            options: ClientOptions with { ApprovalWindow = approvalBudget },
            cancellationToken: caller.Token, clock: clock);
        await ready.Task.WaitAsync(Guard);
        if (pending)
        {
            // hello 为 2 秒、机器窗口为 5 秒；3 秒 timer 只可能在接受 pending 后创建。
            _ = await clock.WaitForTimerAsync(approvalBudget);
        }
        Assert.False(client.IsCompleted);
        caller.Cancel();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.WaitAsync(Guard));
        Assert.True(error.CancellationToken.IsCancellationRequested);
        Assert.True(client.IsCanceled);
        await scenario.AssertServerFinishedAsync();
    }

    [Theory(Timeout = 30_000)]
    [InlineData(400, 15_000, 250)]
    [InlineData(5_000, 100, 60)]
    public async Task Remote_Expiry_Can_Only_Narrow_Local_Machine_Window(
        int machineMs, int expiresMs, int minimumMs)
    {
        TimeSpan elapsed = TimeSpan.Zero;
        await using TlsScenario scenario = TlsScenario.Scripted(async (peer, ct) =>
        {
            // 从已完成 TLS 且收到 hello 后计时，不能把握手耗时误算成认证超时。
            Stopwatch watch = Stopwatch.StartNew();
            await peer.SendAsync(peer.Challenge(expiresMs: expiresMs), ct);
            _ = await peer.ReadAndVerifyResponseAsync(SessionPermission.Control, ct);
            // 不发送 pending/success，也不主动断线：只有客户端自身的窗口能结束等待。
            await peer.AssertClosedWithoutDataAsync(ct);
            elapsed = watch.Elapsed;
        });
        scenario.Start();
        ControlClientAuthOptions options = ClientOptions with { MachineWindow = TimeSpan.FromMilliseconds(machineMs) };
        await AssertRejectedAsync(scenario, scenario.ConnectAsync(options: options),
            "client-machine-timeout", "远端认证等待超时，请重试。", TimeSpan.FromSeconds(2));
        Assert.InRange(elapsed.TotalMilliseconds, minimumMs, 1800);
    }

    [Fact(Timeout = 30_000)]
    public async Task Approval_Waits_600ms_Past_100ms_Prefix_And_Expired_Machine_Window_Then_Succeeds()
    {
        TimeSpan elapsed = TimeSpan.Zero;
        await using TlsScenario scenario = TlsScenario.Scripted(async (peer, ct) =>
        {
            await peer.SendChallengeAsync(ct);
            byte[] transcript = await peer.ReadAndVerifyResponseAsync(SessionPermission.Control, ct);
            await peer.SendPendingAsync(ct);
            Stopwatch watch = Stopwatch.StartNew();
            await Task.Delay(600, ct);
            await peer.SendAsync(peer.Success(transcript, SessionPermission.ViewOnly), ct);
            elapsed = watch.Elapsed;
            await peer.AssertClosedWithoutDataAsync(ct);
        });
        scenario.Start();
        ControlClientAuthOptions options = ClientOptions with
        {
            MachineWindow = TimeSpan.FromMilliseconds(400),
            ApprovalWindow = TimeSpan.FromSeconds(3),
        };
        AuthenticatedControlSession session = await scenario.ConnectAsync(
            options: options, timeouts: Timeouts(prefixMs: 100, payloadMs: 1000)).WaitAsync(Guard);
        Assert.Equal(SessionPermission.ViewOnly, session.GrantedPermission);
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(550), $"审批实际等待不足：{elapsed}。");
        session.Dispose();
        await scenario.AssertServerFinishedAsync();
    }

    [Fact(Timeout = 30_000)]
    public async Task Duplicate_Pending_Is_Rejected_Immediately_Instead_Of_Renewing_Approval_Window()
    {
        TaskCompletionSource repeated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TimeSpan afterDuplicate = TimeSpan.Zero;
        await using TlsScenario scenario = TlsScenario.Scripted(async (peer, ct) =>
        {
            await peer.SendChallengeAsync(ct);
            _ = await peer.ReadAndVerifyResponseAsync(SessionPermission.Control, ct);
            await peer.SendPendingAsync(ct);
            await Task.Delay(300, ct);
            Stopwatch watch = Stopwatch.StartNew();
            await peer.SendPendingAsync(ct);
            repeated.SetResult();
            await peer.AssertClosedWithoutDataAsync(ct);
            afterDuplicate = watch.Elapsed;
        });
        scenario.Start();
        Task<AuthenticatedControlSession> client = scenario.ConnectAsync(options: ClientOptions with
        {
            ApprovalWindow = TimeSpan.FromMilliseconds(600),
        });
        await repeated.Task.WaitAsync(Guard);
        await AssertRejectedAsync(scenario, client, "client-repeated-pending", "远端重复发送审批等待帧。",
            TimeSpan.FromMilliseconds(450));
        Assert.True(afterDuplicate < TimeSpan.FromMilliseconds(450), $"重复 pending 未立即拒绝：{afterDuplicate}。");
    }

    [Fact(Timeout = 30_000)]
    public async Task Silent_Approval_Uses_Local_Approval_Window_And_Closes()
    {
        TimeSpan elapsed = TimeSpan.Zero;
        await using TlsScenario scenario = TlsScenario.Scripted(async (peer, ct) =>
        {
            await peer.SendChallengeAsync(ct);
            _ = await peer.ReadAndVerifyResponseAsync(SessionPermission.Control, ct);
            await peer.SendPendingAsync(ct);
            Stopwatch watch = Stopwatch.StartNew();
            await peer.AssertClosedWithoutDataAsync(ct);
            elapsed = watch.Elapsed;
        });
        scenario.Start();
        await AssertRejectedAsync(scenario, scenario.ConnectAsync(options: ClientOptions with
        {
            ApprovalWindow = TimeSpan.FromMilliseconds(400),
        }), "client-approval-timeout", "等待远端审批超时，请重试。", TimeSpan.FromSeconds(2));
        Assert.InRange(elapsed.TotalMilliseconds, 250, 1800);
    }

    private static async Task AssertRejectedAsync(
        TlsScenario scenario, Task<AuthenticatedControlSession> client,
        string rejection, string message, TimeSpan? wait = null)
    {
        AuthenticatedControlSession? returned = null;
        ReadOnlyMemory<byte> returnedToken = default;
        ControlClientAuthenticationException error = await Assert.ThrowsAsync<ControlClientAuthenticationException>(async () =>
        {
            returned = await client.WaitAsync(wait ?? Guard);
            returnedToken = returned.SessionToken;
        });
        Assert.Equal(rejection, error.Rejection);
        Assert.Equal(message, error.DisplayMessage);
        Assert.Equal(message, error.Message);
        Assert.Null(error.InnerException);
        Assert.Null(returned);
        Assert.True(returnedToken.IsEmpty);
        Assert.True(client.IsFaulted);
        Assert.DoesNotContain(Convert.ToBase64String(GoodKey), error.ToString(), StringComparison.Ordinal);
        await scenario.AssertServerFinishedAsync();
    }

    private static ControlAuthContext CreateContext(RecordingApprovalGate gate, bool requireApproval) => new()
    {
        ServerDeviceId = ServerDeviceId,
        AccessSecretStore = new FixedSecretStore(),
        FailedAuthLimiter = new FailedAuthLimiter(),
        PendingApprovalLimiter = new ConnectionAdmissionLimiter(3, 1),
        ApprovalGate = gate,
        SessionRegistry = new SessionRegistry(),
        Options = new ControlAuthOptions
        {
            RequireLocalApproval = requireApproval,
            MachineWindow = TimeSpan.FromSeconds(5),
            ApprovalWindow = TimeSpan.FromSeconds(5),
            FailureDelayMin = TimeSpan.Zero,
            FailureDelayMax = TimeSpan.Zero,
        },
    };

    private static async Task<ControlAuthSession> BeginRealAuthenticationAsync(
        AcceptedConnection connection, ControlAuthContext context, CancellationToken ct)
    {
        Assert.True(connection.Stream.IsAuthenticated);
        ControlPreAuthResult pre = await new ControlPreAuthSession().RunAsync(connection, Timeouts(), ct);
        Assert.True(pre.Completed, pre.Rejection);
        Assert.NotNull(pre.Handoff);
        return pre.Handoff.BeginAuthentication(context, Timeouts());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < Guard)
        {
            await Task.Delay(10);
        }
        Assert.True(condition(), "等待服务端登记或连接回收超时。");
    }

    private sealed class RecordingApprovalGate(SessionPermission granted) : ILocalApprovalGate
    {
        public int RequestCount { get; private set; }
        public LocalApprovalRequest? LastRequest { get; private set; }

        public ValueTask<LocalApprovalDecision> RequestApprovalAsync(LocalApprovalRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            RequestCount++;
            return ValueTask.FromResult(new LocalApprovalDecision(request.RequestId, LocalApprovalOutcome.Approved, granted));
        }
    }

    private sealed class FixedSecretStore : IAccessSecretStore
    {
        public Task<AccessSecret> LoadOrCreateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccessSecret(GoodKey.ToArray()));

        public Task<AccessSecret> RegenerateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("测试不轮换密钥。");
    }

    private sealed class LoopbackPolicy : ISubnetPolicy
    {
        public bool IsAllowedPeer(IPAddress localAddress, IPAddress remoteAddress) =>
            IPAddress.IsLoopback(localAddress) && IPAddress.IsLoopback(remoteAddress);
    }

    // Host 持有 TLS 服务端资源；夹具持有客户端任务，断言失败也能回收迟到的成功会话。
    private sealed class TlsScenario : IAsyncDisposable
    {
        private readonly X509Certificate2 _certificate = TestCertificateFactory.Create();
        private readonly CancellationTokenSource _clientStop = new();
        private Task<AuthenticatedControlSession>? _client;
        private bool _handlerStarted;

        public TlsScenario(Func<AcceptedConnection, CancellationToken, Task> handler)
        {
            using TcpListener reservation = new(IPAddress.Loopback, 0);
            reservation.Start();
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            Assert.True(ConnectionTarget.TryCreate(ServerDeviceId, IPAddress.Loopback, port,
                TestCertificateFactory.Fingerprint(_certificate), out ConnectionTarget? target));
            Target = target!;
            Host = new TransportHost(new[] { IPAddress.Loopback }, new LoopbackPolicy(), _certificate,
                async (connection, ct) =>
                {
                    _handlerStarted = true;
                    try
                    {
                        await handler(connection, ct);
                        ServerFinished.TrySetResult();
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        ServerFinished.TrySetCanceled(ct);
                    }
                    catch (Exception error)
                    {
                        // 转交给主测试和 finally 观察，绝不把 XunitException 当作断连吞掉。
                        ServerFinished.TrySetException(error);
                    }
                }, new TransportHostOptions { Port = port, Timeouts = Timeouts() });
        }

        public TransportHost Host { get; }
        public ConnectionTarget Target { get; }
        public TaskCompletionSource ServerFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static TlsScenario Scripted(Func<ScriptedPeer, CancellationToken, Task> script) => new(async (connection, ct) =>
        {
            ScriptedPeer peer = new(connection);
            byte[] hello = await peer.ReadAsync(ct);
            Assert.True(HelloFrame.TryParse(hello, out string? rejection), rejection);
            await script(peer, ct);
        });

        public void Start()
        {
            TransportHostStartResult result = Host.Start();
            Assert.True(result.IsListening);
            Assert.Empty(result.Failures);
        }

        public Task<AuthenticatedControlSession> ConnectAsync(
            SessionPermission requested = SessionPermission.Control,
            byte[]? key = null, ControlClientAuthOptions? options = null,
            TransportTimeouts? timeouts = null, CancellationToken cancellationToken = default,
            TimeProvider? clock = null)
        {
            Assert.Null(_client);
            _client = ConnectCoreAsync();
            return _client;

            async Task<AuthenticatedControlSession> ConnectCoreAsync()
            {
                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_clientStop.Token, cancellationToken);
                return await new ControlClientConnector().ConnectAndAuthenticateAsync(
                    Target, ClientDeviceId, ClientName, key ?? GoodKey, requested,
                    options ?? ClientOptions, timeouts ?? Timeouts(), clock: clock, cancellationToken: linked.Token);
            }
        }

        public async Task AssertServerFinishedAsync()
        {
            await ServerFinished.Task.WaitAsync(Guard);
            await WaitUntilAsync(() => Host.ActiveConnections == 0 && Host.AdmittedConnections == 0);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _clientStop.Cancel();
                if (_client is not null)
                {
                    try
                    {
                        (await _client.WaitAsync(Guard)).Dispose();
                    }
                    catch (Exception error) when (error is AuthenticationException or OperationCanceledException or IOException or SocketException)
                    {
                        // 这里只回收产品调用任务；服务端脚本的断言在下方单独 await。
                    }
                }
            }
            finally
            {
                try
                {
                    TransportHostStopReport report = await Host.StopAsync(Guard);
                    Assert.True(report.AllFinished, "TLS host 停机后仍有未收回的连接。");
                    if (_handlerStarted)
                    {
                        try
                        {
                            await ServerFinished.Task.WaitAsync(Guard);
                        }
                        catch (OperationCanceledException) when (ServerFinished.Task.IsCanceled)
                        {
                        }
                    }
                    Assert.Equal(0, Host.ActiveConnections);
                    Assert.Equal(0, Host.AdmittedConnections);
                }
                finally
                {
                    try
                    {
                        await Host.DisposeAsync();
                    }
                    finally
                    {
                        _clientStop.Dispose();
                        _certificate.Dispose();
                    }
                }
            }
        }
    }

    private sealed class ScriptedPeer
    {
        private readonly byte[] _serverNonce = RandomNumberGenerator.GetBytes(32);

        public ScriptedPeer(AcceptedConnection connection)
        {
            Stream = connection.Stream;
            Assert.True(Stream.IsAuthenticated);
            Assert.True(Stream.IsEncrypted);
            Assert.NotNull(Stream.LocalCertificate);
            // 从这条真实 TLS 连接的证书取指纹，不信任 challenge 字段或冻结目标副本。
            ActualPin = SHA256.HashData(Stream.LocalCertificate.GetRawCertData());
            Assert.Equal(connection.Security.ServerCertificateSha256.ToArray(), ActualPin);
        }

        public SslStream Stream { get; }
        public byte[] ActualPin { get; }
        public Guid SessionId { get; } = Guid.NewGuid();
        public byte[] Token { get; } = RandomNumberGenerator.GetBytes(32);

        public Task<byte[]> ReadAsync(CancellationToken ct) => new FrameReader(Stream).ReadFrameAsync(
            TransportConstants.MaxPreAuthMessageBytes, Guard, Guard, ct);

        public Task SendAsync(byte[] payload, CancellationToken ct) => FrameWriter.WriteFrameAsync(
            Stream, payload, TransportConstants.MaxPreAuthMessageBytes, Guard, ct);

        public Task SendChallengeAsync(CancellationToken ct) => SendAsync(Challenge(), ct);
        public Task SendPendingAsync(CancellationToken ct) => SendAsync("{\"type\":\"approval_pending\"}"u8.ToArray(), ct);

        public byte[] Challenge(Guid? deviceId = null, byte[]? pin = null, int expiresMs = 15_000) =>
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "auth_challenge",
                protocol = 1,
                sessionId = SessionId.ToString("D"),
                serverDeviceId = (deviceId ?? ServerDeviceId).ToString("D"),
                serverNonce = Convert.ToBase64String(_serverNonce),
                certSha256 = Convert.ToHexString(pin ?? ActualPin),
                expiresInMs = expiresMs,
            });

        public async Task<byte[]> ReadAndVerifyResponseAsync(SessionPermission requested, CancellationToken ct)
        {
            byte[] payload = await ReadAsync(ct);
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            Assert.Equal("auth_response", root.GetProperty("type").GetString());
            Assert.Equal(ClientDeviceId.ToString("D"), root.GetProperty("clientDeviceId").GetString());
            Assert.Equal(ClientName, root.GetProperty("clientName").GetString());
            Assert.Equal(Permission(requested), root.GetProperty("requestedPermission").GetString());
            Assert.Equal(new[] { "clientDeviceId", "clientName", "clientNonce", "clientProof", "requestedPermission", "type" },
                root.EnumerateObject().Select(property => property.Name).Order().ToArray());
            string nonceText = root.GetProperty("clientNonce").GetString()!;
            byte[] nonce = Convert.FromBase64String(nonceText);
            Assert.Equal(32, nonce.Length);
            Assert.Equal(Convert.ToBase64String(nonce), nonceText);
            byte[] proof = Convert.FromBase64String(root.GetProperty("clientProof").GetString()!);
            Assert.Equal(32, proof.Length);
            byte[] transcript = ClientTranscript(nonceText, requested, ActualPin);
            using HMACSHA256 hmac = new(GoodKey);
            Assert.Equal(hmac.ComputeHash(transcript), proof);
            byte[] otherPin = ActualPin.ToArray();
            otherPin[0] ^= 0x80;
            Assert.False(CryptographicOperations.FixedTimeEquals(
                hmac.ComputeHash(ClientTranscript(nonceText, requested, otherPin)), proof),
                "response MAC 不得匹配替换后的证书指纹。");
            return transcript;
        }

        // ADR-038 §1 的独立字节 oracle：NUL 分隔、无尾 NUL、UUID 小写 D、指纹大写 HEX。
        // 不调用产品 transcript/proof helper，避免客户端与伪服务端共享同一个错误。
        private byte[] ClientTranscript(string clientNonce, SessionPermission requested, byte[] pin) =>
            Encoding.UTF8.GetBytes(string.Join('\0',
                "LANREMOTE-AUTH-V1", SessionId.ToString("D"), ServerDeviceId.ToString("D"),
                ClientDeviceId.ToString("D"), Convert.ToBase64String(_serverNonce),
                clientNonce, Convert.ToHexString(pin), Permission(requested)));

        public static byte[] ServerProof(byte[] clientTranscript, SessionPermission granted)
        {
            string grant = string.Join('\0', "server", "LANREMOTE-GRANT-V1",
                Convert.ToHexString(SHA256.HashData(clientTranscript)), Permission(granted));
            using HMACSHA256 hmac = new(GoodKey);
            return hmac.ComputeHash(Encoding.UTF8.GetBytes(grant));
        }

        private static string Permission(SessionPermission permission) => permission switch
        {
            SessionPermission.Control => "control",
            SessionPermission.ViewOnly => "view",
            _ => throw new ArgumentOutOfRangeException(nameof(permission)),
        };

        public byte[] Success(byte[] transcript, SessionPermission granted) =>
            SuccessWithProof(granted, ServerProof(transcript, granted));

        public byte[] SuccessWithProof(SessionPermission granted, byte[] proof) => JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "auth_success",
            grantedPermission = Permission(granted),
            serverProof = Convert.ToBase64String(proof),
            sessionToken = Convert.ToBase64String(Token),
            videoAttachExpiresInMs = 15_000,
        });

        public async Task AssertClosedWithoutDataAsync(CancellationToken ct)
        {
            int bytes;
            try
            {
                bytes = await Stream.ReadAsync(new byte[1], ct).AsTask().WaitAsync(Guard);
            }
            catch (IOException)
            {
                // TLS 已握手、hello 已读完；RST 是既有连接中断，不是「未建立连接」。
                // 本地停机不算对端主动关闭的证据。
                ct.ThrowIfCancellationRequested();
                return;
            }
            Assert.Equal(0, bytes);
        }
    }
}
