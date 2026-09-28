using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using LanRemote.Transport;
using LanRemote.Transport.Auth;

namespace LanRemote.Acceptance.Tests;

[Collection(AcceptanceStageFiveCollection.Name)]
public sealed class IsolatedUiScenarioTests
{
    private const string ProofFailureMessage = "远端身份验证失败，可能是错误密码或伪造设备广播";
    private const string PrivateError = "不得进入日志的异常正文-9217";
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Agreed_Api_Uses_The_Host_RunState_Inbox(bool mismatch)
    {
        await using ScenarioRun test = new(Case(mismatch), useAgreedApi: true);
        LocalApprovalSnapshot request = await test.AssertPendingAsync();
        Submit(test, request, LocalApprovalOutcome.Approved, SessionPermission.Control);
        if (!mismatch)
        {
            await test.Active.Task.WaitAsync(Guard);
            await test.Stop.CancelAsync();
        }
        Assert.Equal(mismatch ? AcceptanceOutcome.Fail : AcceptanceOutcome.InvalidRun,
            await test.Worker.WaitAsync(Guard));
        Assert.True(test.State.IsHost);
        Assert.Null(test.State.GetHostContext());
        Assert.Equal(mismatch ? ProofFailureMessage : null, test.State.AuthenticationFailure);
        AssertSettled(test, mismatch ? "FAIL" : "INVALID_RUN");
    }

    [Theory(Timeout = 60_000)]
    [InlineData(SessionPermission.Control)]
    [InlineData(SessionPermission.ViewOnly)]
    public async Task Real_Proof_Is_One_Bit_Wrong_For_The_Actual_Grant_And_Client_Closes_First(SessionPermission grant)
    {
        await using ScenarioRun test = new(IsolatedUiCase.ServerProofMismatch);
        LocalApprovalSnapshot request = await test.AssertPendingAsync();
        Assert.Equal(0, test.Resources!.AuthContext.SessionRegistry.ActiveSessionCount);
        Submit(test, request, LocalApprovalOutcome.Approved, grant);
        Assert.Equal(AcceptanceOutcome.Fail, await test.Worker.WaitAsync(Guard));

        Assert.Equal(ProofFailureMessage, test.State.AuthenticationFailure);
        Assert.Null(test.State.GetPending());
        string log = test.Run.Log.All;
        Assert.Contains("[CLIENT][RESULT] scenario=success clientOutcome=FAIL stage=authentication rejection=client-server-proof-mismatch", log);
        Assert.Contains("// " + ProofFailureMessage, log);
        Assert.Contains($"[ISOLATED][INJECT] grant={grant} flippedBits=1 registry=NOT_APPLICABLE", log);
        Assert.DoesNotContain("serverProof=verified", log);
        Assert.DoesNotContain("[CLIENT][HOLD]", log);
        Assert.DoesNotContain("[CLIENT][DISPOSE]", log);
        Assert.DoesNotContain("[HOST][SESSION]", log);
        Assert.DoesNotContain("clientOutcome=PASS", log);
        Assert.DoesNotContain(test.Status, text => text.StartsWith("已双向认证", StringComparison.Ordinal));

        // 读取的就是被测引擎将要送上 TLS 的缓冲，而不是另一套模拟对端。
        byte[] transcript = Assert.Single(test.Secrets.Select(secret => secret.Copy), bytes =>
            bytes.AsSpan().StartsWith("LANREMOTE-AUTH-V1\0"u8));
        byte[] frame = Assert.Single(test.Secrets.Select(secret => secret.Copy), bytes =>
            Encoding.UTF8.GetString(bytes).Contains("\"auth_success\"", StringComparison.Ordinal));
        using JsonDocument success = JsonDocument.Parse(frame);
        string wireGrant = grant == SessionPermission.ViewOnly ? "view" : "control";
        Assert.Equal(wireGrant, success.RootElement.GetProperty("grantedPermission").GetString());
        byte[] actual = Convert.FromBase64String(success.RootElement.GetProperty("serverProof").GetString()!);
        byte[] digest = SHA256.HashData(transcript);
        byte[] grantTranscript = Encoding.UTF8.GetBytes(string.Join('\0',
            "server", "LANREMOTE-GRANT-V1", Convert.ToHexString(digest), wireGrant));
        using HMACSHA256 hmac = new(test.KeyCopy!);
        byte[] correct = hmac.ComputeHash(grantTranscript);
        try
        {
            Assert.Equal(32, actual.Length);
            Assert.Equal((byte)(correct[0] ^ 0x80), actual[0]);
            Assert.Equal(correct.AsSpan(1).ToArray(), actual.AsSpan(1).ToArray());
        }
        finally
        {
            foreach (byte[] bytes in new[] { actual, correct, digest, grantTranscript }) { CryptographicOperations.ZeroMemory(bytes); }
        }

        string close = Assert.Single(test.Run.Log.ReadFrom(0, int.MaxValue), line => line.StartsWith("[ISOLATED][PEER-CLOSE]", StringComparison.Ordinal));
        Assert.Contains("beforeHostStop=True", close);
        if (close.Contains("kind=eof", StringComparison.Ordinal)) { Assert.Contains("bytesAfterReply=0 ", close); }
        else
        {
            Assert.Contains("kind=rst", close);
            Assert.Contains("bytesAfterReply=UNKNOWN ", close);
        }
        Assert.True(log.IndexOf("[ISOLATED][PEER-CLOSE]", StringComparison.Ordinal) <
            log.IndexOf("[ISOLATED][HOST-STOP]", StringComparison.Ordinal));
        AssertClean(test);
        AssertSettled(test, "FAIL");
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Active_Stop_Or_Close_Snapshots_The_Real_Registry_Before_Cancelling_Client(bool stopStateFirst)
    {
        await using ScenarioRun test = new(IsolatedUiCase.ActiveHostStop);
        LocalApprovalSnapshot request = await test.AssertPendingAsync();
        Assert.Equal(1, test.Resources!.AuthContext.PendingApprovalLimiter.GlobalInUse);
        Submit(test, request, LocalApprovalOutcome.Approved, SessionPermission.ViewOnly);
        await test.Active.Task.WaitAsync(Guard);
        ControlSessionSummary session = Assert.Single(test.Resources.AuthContext.SessionRegistry.Snapshot());
        Assert.Equal(request.Request.SessionId, session.SessionId);
        Assert.Equal(SessionPermission.ViewOnly, session.GrantedPermission);
        Assert.Equal(IPAddress.Loopback, session.RemoteAddress);
        Assert.Contains("[CLIENT][HOLD] localObjectHoldTargetMs=120000 hostRegistry=UNOBSERVED", test.Run.Log.All);
        Assert.False(test.Worker.IsCompleted);
        if (stopStateFirst) { test.State.Stop(); }
        await test.Stop.CancelAsync();
        Assert.Equal(AcceptanceOutcome.InvalidRun, await test.Worker.WaitAsync(Guard));

        string log = test.Run.Log.All;
        Assert.Contains("[HOST][STOP] authenticatedActiveBeforeStop=1", log);
        Assert.Contains($"[HOST][STOP] sessionId={session.SessionId} connectionId={session.ConnectionId} closeOrigin=host-forced-close naturalRelease=UNMET", log);
        Assert.Contains("[ISOLATED][HOST-STOP] first=True clientTaskCompleted=False", log);
        string evidence = Assert.Single(test.Run.Log.ReadFrom(0, int.MaxValue), line => line.StartsWith("[HOST][SESSION]", StringComparison.Ordinal));
        Assert.Contains("authenticated=True", evidence);
        Assert.Contains("deregisteredAtRunEnd=True", evidence);
        Assert.Contains("hostForcedClose=True", evidence);
        Assert.Contains("evidence=UNMET", evidence);
        Assert.Contains("terminal=authenticated-host-forced-close", log);
        Assert.DoesNotContain("clientOutcome=PASS", log);
        Assert.Equal(0, test.StatusAfterCancellation);
        AssertClean(test);
        AssertSettled(test, "INVALID_RUN");
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hold_Expiry_Is_Unmet_And_Default_Client_Still_Holds_Five_Seconds(bool normalDefault)
    {
        IsolatedUiScenario.Options options = new()
        {
            ActiveHoldDuration = normalDefault ? null : TimeSpan.FromMilliseconds(350),
        };
        await using ScenarioRun test = new(IsolatedUiCase.ActiveHostStop, options);
        LocalApprovalSnapshot request = await test.AssertPendingAsync();
        Submit(test, request, LocalApprovalOutcome.Approved, SessionPermission.Control);
        Assert.Equal(AcceptanceOutcome.PreconditionUnmet, await test.Worker.WaitAsync(Guard));
        Assert.False(test.Run.AbortedByOperator);
        Assert.Contains(normalDefault
            ? "[CLIENT][HOLD] localObjectHoldTargetMs=5000 hostRegistry=UNOBSERVED"
            : "[CLIENT][HOLD] localObjectHoldTargetMs=350 hostRegistry=UNOBSERVED", test.Run.Log.All);
        string held = Assert.Single(test.Run.Log.ReadFrom(0, int.MaxValue), line =>
            line.StartsWith("[CLIENT][HOLD] sessionId=", StringComparison.Ordinal));
        string measurement = Assert.Single(held.Split(' '), field => field.StartsWith("localObjectHeldMs=", StringComparison.Ordinal));
        long milliseconds = long.Parse(measurement.Split('=')[1], CultureInfo.InvariantCulture);
        Assert.True(milliseconds >= (normalDefault ? 5000 : 350));
        Assert.Contains("[CLIENT][DISPOSE]", test.Run.Log.All);
        if (normalDefault)
        {
            Assert.Contains("serverProof 已通过；本地持有已验证会话至少五秒后同步释放。Host 保持及注销仍须按 SessionId 实测核对。", test.Run.Log.All);
            MethodInfo method = typeof(ClientRole).GetMethod("RunSuccessAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            ParameterInfo parameter = method.GetParameters()[^1];
            Assert.Equal("holdDuration", parameter.Name);
            Assert.True(parameter.IsOptional);
            Assert.Null(parameter.DefaultValue);
        }
        AssertClean(test);
        AssertSettled(test, "UNMET");
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Denial_Does_Not_Authenticate_Or_Inject(bool mismatch)
    {
        await using ScenarioRun test = new(Case(mismatch));
        LocalApprovalSnapshot request = await test.AssertPendingAsync();
        Submit(test, request, LocalApprovalOutcome.Denied);
        Assert.Equal(mismatch ? AcceptanceOutcome.PreconditionUnmet : AcceptanceOutcome.Fail,
            await test.Worker.WaitAsync(Guard));
        Assert.DoesNotContain("[ISOLATED][INJECT]", test.Run.Log.All);
        Assert.DoesNotContain("serverProof=verified", test.Run.Log.All);
        Assert.DoesNotContain("[CLIENT][HOLD]", test.Run.Log.All);
        Assert.Null(test.State.AuthenticationFailure);
        AssertClean(test);
        AssertSettled(test, mismatch ? "UNMET" : "FAIL");
    }

    [Fact(Timeout = 100_000)]
    public async Task Original_Sixty_Second_Approval_Expiry_Is_Unmet_Not_An_Instrument_Fault()
    {
        await using ScenarioRun test = new(IsolatedUiCase.ServerProofMismatch);
        LocalApprovalSnapshot request = await test.AssertPendingAsync();
        Assert.Equal(TimeSpan.FromSeconds(60), test.State.GetPending()!.ApprovalWindow);
        Assert.Equal(AcceptanceOutcome.PreconditionUnmet,
            await test.Worker.WaitAsync(TimeSpan.FromSeconds(80)));
        Assert.False(test.Run.AbortedByOperator);
        Assert.DoesNotContain("[ISOLATED][INJECT]", test.Run.Log.All);
        Assert.DoesNotContain("serverProof=verified", test.Run.Log.All);
        Assert.DoesNotContain("[CLIENT][HOLD]", test.Run.Log.All);
        Assert.Contains("[ISOLATED][UNMET]", test.Run.Log.All);
        Assert.Null(test.State.AuthenticationFailure);
        Assert.False(test.State.Inbox!.TrySubmit(request.RequestId, request.Generation,
            LocalApprovalOutcome.Approved, SessionPermission.Control));
        AssertClean(test);
        AssertSettled(test, "UNMET");
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelling_While_Pending_Joins_All_Work_And_Invalidates_Once(bool mismatch)
    {
        await using ScenarioRun test = new(Case(mismatch));
        await test.AssertPendingAsync();
        await test.Stop.CancelAsync();
        Assert.Equal(AcceptanceOutcome.InvalidRun, await test.Worker.WaitAsync(Guard));
        Assert.DoesNotContain("[ISOLATED][INJECT]", test.Run.Log.All);
        Assert.DoesNotContain("serverProof=verified", test.Run.Log.All);
        Assert.Equal(0, test.StatusAfterCancellation);
        AssertClean(test);
        AssertSettled(test, "INVALID_RUN");
    }

    [Fact(Timeout = 60_000)]
    public async Task Repeated_Runs_Use_Fresh_Identities_Certificates_Keys_And_Sessions()
    {
        HashSet<Guid> identities = new();
        HashSet<Guid> sessions = new();
        HashSet<string> certificates = new();
        HashSet<string> keyDigests = new();
        for (int iteration = 0; iteration < 2; iteration++)
        {
            await using ScenarioRun test = new(IsolatedUiCase.ServerProofMismatch);
            LocalApprovalSnapshot request = await test.AssertPendingAsync();
            IsolatedUiScenario.Resources resources = test.Resources!;
            Assert.True(identities.Add(resources.Target.DeviceId));
            Assert.True(identities.Add(resources.ClientIdentity.DeviceId));
            Assert.True(sessions.Add(request.Request.SessionId));
            Assert.True(certificates.Add(Convert.ToHexString(resources.Target.ExpectedCertSha256.Span)));
            Assert.True(keyDigests.Add(Convert.ToHexString(SHA256.HashData(test.KeyCopy!))));
            Assert.Equal(16, test.KeyCopy!.Length);
            Assert.Equal(IPAddress.Loopback, resources.Target.RemoteAddress);
            Assert.InRange(resources.Target.Port, 1, 65535);
            Submit(test, request, LocalApprovalOutcome.Approved, SessionPermission.Control);
            Assert.Equal(AcceptanceOutcome.Fail, await test.Worker.WaitAsync(Guard));
            AssertClean(test);
            AssertSettled(test, "FAIL");
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initialization_And_Bind_Failures_Clear_Allocated_Resources(bool occupyPort)
    {
        TcpListener? blocker = null;
        try
        {
            IsolatedUiScenario.Options options = new()
            {
                Initialized = resources =>
                {
                    if (!occupyPort) { throw new InvalidOperationException(PrivateError + Convert.ToBase64String(resources.AccessKey)); }
                    blocker = new TcpListener(IPAddress.Loopback, resources.Target.Port) { ExclusiveAddressUse = true };
                    blocker.Start();
                },
            };
            await using ScenarioRun test = new(IsolatedUiCase.ActiveHostStop, options);
            Assert.Equal(AcceptanceOutcome.HarnessError, await test.Worker.WaitAsync(Guard));
            Assert.False(test.Pending.Task.IsCompleted);
            Assert.DoesNotContain("[ISOLATED][START]", test.Run.Log.All);
            AssertClean(test, expectedFault: true);
            AssertSettled(test, "HARNESS_ERROR");
        }
        finally { blocker?.Dispose(); }
    }

    [Fact(Timeout = 60_000)]
    public async Task Handler_IOException_Is_Transferred_To_The_Owner_Not_Swallowed_By_Transport()
    {
        await using ScenarioRun test = new(IsolatedUiCase.ServerProofMismatch, new IsolatedUiScenario.Options
        {
            BeforePeerReply = () => throw new IOException(PrivateError),
        });
        LocalApprovalSnapshot request = await test.AssertPendingAsync();
        Submit(test, request, LocalApprovalOutcome.Approved, SessionPermission.ViewOnly);
        Assert.Equal(AcceptanceOutcome.HarnessError, await test.Worker.WaitAsync(Guard));
        Assert.DoesNotContain("[ISOLATED][INJECT]", test.Run.Log.All);
        Assert.DoesNotContain("[CLIENT][HOLD]", test.Run.Log.All);
        AssertClean(test, expectedFault: true);
        AssertSettled(test, "HARNESS_ERROR");
    }

    [Fact(Timeout = 60_000)]
    public async Task Status_Callback_Failure_Still_Stops_And_Clears_The_Engine()
    {
        await using ScenarioRun test = new(IsolatedUiCase.ActiveHostStop,
            statusFailure: () => throw new InvalidOperationException(PrivateError));
        Assert.Equal(AcceptanceOutcome.HarnessError, await test.Worker.WaitAsync(Guard));
        AssertClean(test, expectedFault: true);
        AssertSettled(test, "HARNESS_ERROR");
    }

    [Fact(Timeout = 60_000)]
    public async Task Cleanup_Log_And_Fault_Observers_Cannot_Skip_Host_Stop_Or_Secret_Cleanup()
    {
        await using ScenarioRun test = new(IsolatedUiCase.ActiveHostStop);
        LocalApprovalSnapshot request = await test.AssertPendingAsync();
        Submit(test, request, LocalApprovalOutcome.Approved, SessionPermission.Control);
        await test.Active.Task.WaitAsync(Guard);
        void ThrowFromLog(string line)
        {
            if (line.StartsWith("[ISOLATED][HOST-STOP]", StringComparison.Ordinal) ||
                line.StartsWith("[HARNESS][FAULT]", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(PrivateError);
            }
        }
        test.Run.Log.LineWritten += ThrowFromLog;
        try
        {
            await test.Stop.CancelAsync();
            Assert.Equal(AcceptanceOutcome.InvalidRun, await test.Worker.WaitAsync(Guard));
        }
        finally { test.Run.Log.LineWritten -= ThrowFromLog; }
        Assert.Contains("[HOST][STOP] authenticatedActiveBeforeStop=1", test.Run.Log.All);
        AssertClean(test, expectedFault: true, expectedCleanupFault: true);
        AssertSettled(test, "INVALID_RUN");
    }

    [Fact(Timeout = 60_000)]
    public async Task Client_Result_Log_Fault_Preserves_The_Real_Proof_Rejection_Cache()
    {
        await using ScenarioRun test = new(IsolatedUiCase.ServerProofMismatch);
        LocalApprovalSnapshot request = await test.AssertPendingAsync();
        void ThrowFromLog(string line)
        {
            if (line.StartsWith("[CLIENT][RESULT] scenario=success clientOutcome=FAIL ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(PrivateError);
            }
        }
        test.Run.Log.LineWritten += ThrowFromLog;
        try
        {
            Submit(test, request, LocalApprovalOutcome.Approved, SessionPermission.Control);
            Assert.Equal(AcceptanceOutcome.HarnessError, await test.Worker.WaitAsync(Guard));
        }
        finally { test.Run.Log.LineWritten -= ThrowFromLog; }
        Assert.Equal(ProofFailureMessage, test.State.AuthenticationFailure);
        Assert.DoesNotContain("[CLIENT][HOLD]", test.Run.Log.All);
        AssertClean(test, expectedFault: true, expectedCleanupFault: true);
        AssertSettled(test, "HARNESS_ERROR");
    }

    [Fact]
    public void Loopback_Policy_Is_Exact_And_Unknown_IO_Is_Not_Reset_Evidence()
    {
        Type type = typeof(IsolatedUiScenario).GetNestedType("ExactLoopbackPolicy", BindingFlags.NonPublic)!;
        ISubnetPolicy policy = (ISubnetPolicy)Activator.CreateInstance(type, nonPublic: true)!;
        Assert.True(policy.IsAllowedPeer(IPAddress.Loopback, IPAddress.Loopback));
        foreach (IPAddress other in new[] { IPAddress.Parse("127.0.0.2"), IPAddress.IPv6Loopback,
                     IPAddress.Loopback.MapToIPv6(), IPAddress.Parse("192.168.1.2") })
        {
            Assert.False(policy.IsAllowedPeer(IPAddress.Loopback, other));
            Assert.False(policy.IsAllowedPeer(other, IPAddress.Loopback));
        }
        Assert.True(IsolatedUiScenario.IsConnectionReset(new IOException("", new SocketException((int)SocketError.ConnectionReset))));
        Assert.False(IsolatedUiScenario.IsConnectionReset(new IOException("connection reset")));
        Assert.False(IsolatedUiScenario.IsConnectionReset(new IOException("", new SocketException((int)SocketError.TimedOut))));
    }

    private static IsolatedUiCase Case(bool mismatch) => mismatch ? IsolatedUiCase.ServerProofMismatch : IsolatedUiCase.ActiveHostStop;

    private static void Submit(ScenarioRun test, LocalApprovalSnapshot snapshot, LocalApprovalOutcome outcome,
        SessionPermission? grant = null) => Assert.True(test.State.Inbox!.TrySubmit(snapshot.RequestId, snapshot.Generation, outcome, grant));

    private static void AssertSettled(ScenarioRun test, string outcome)
    {
        string[] lines = test.Run.Log.ReadFrom(0, int.MaxValue).ToArray();
        Assert.Single(lines, line => line == "RUN COMPLETE");
        Assert.Equal("[RESULT] outcome   = " + outcome,
            Assert.Single(lines, line => line.StartsWith("[RESULT] outcome", StringComparison.Ordinal)));
        Assert.True(Array.FindIndex(lines, line => line.StartsWith("[ISOLATED][CLEANUP]", StringComparison.Ordinal)) <
            Array.FindIndex(lines, line => line.StartsWith("[RESULT] outcome", StringComparison.Ordinal)));
        Assert.Equal(lines, File.ReadAllLines(test.Run.Log.FilePath!));
        Assert.DoesNotContain("[CORRECTION]", test.Run.Log.All);
    }

    private static void AssertClean(ScenarioRun test, bool expectedFault = false, bool expectedCleanupFault = false)
    {
        IsolatedUiScenario.Resources resources = Assert.IsType<IsolatedUiScenario.Resources>(test.Resources);
        Assert.False(resources.Host.IsRunning);
        Assert.Equal(0, resources.Host.ActiveConnections);
        Assert.Equal(0, resources.Host.AdmittedConnections);
        Assert.Equal(0, resources.AuthContext.SessionRegistry.ActiveSessionCount);
        Assert.Equal(0, resources.AuthContext.PendingApprovalLimiter.GlobalInUse);
        Assert.Equal(0, test.State.Inbox!.PendingCount);
        Assert.All(resources.AccessKey, value => Assert.Equal((byte)0, value));
        Assert.ThrowsAny<CryptographicException>(() => resources.Certificate.GetCertHash());
        Assert.Equal(expectedFault, test.Run.HasBackgroundFaults);
        string cleanup = Assert.Single(test.Run.Log.ReadFrom(0, int.MaxValue), line => line.StartsWith("[ISOLATED][CLEANUP]", StringComparison.Ordinal));
        Assert.Contains("firstStopAllFinishedWithinBudget=True", cleanup);
        Assert.Contains("handlersJoined=True clientJoined=True keyCleared=True certificateDisposed=True " +
            $"cleanupFault={expectedCleanupFault}", cleanup);
        foreach ((byte[] owned, byte[] copy) in test.Secrets)
        {
            Assert.All(owned, value => Assert.Equal((byte)0, value));
            if (copy.Length < 16) { continue; }
            string base64 = Convert.ToBase64String(copy);
            Assert.DoesNotContain(base64, test.Run.Log.All);
            Assert.DoesNotContain(JsonSerializer.Serialize(base64)[1..^1], test.Run.Log.All);
            Assert.DoesNotContain(Convert.ToHexString(copy), test.Run.Log.All, StringComparison.OrdinalIgnoreCase);
        }
        foreach (string forbidden in new[] { "clientProof", "clientNonce", "serverNonce", "sessionToken", PrivateError })
        {
            Assert.DoesNotContain(forbidden, test.Run.Log.All);
        }
        Assert.DoesNotContain(Convert.ToBase64String(test.KeyCopy!), test.Run.Log.All);
        Assert.DoesNotContain(Convert.ToHexString(test.KeyCopy!), test.Run.Log.All, StringComparison.OrdinalIgnoreCase);
    }

    // 仅管理被测 RunAsync 的输入、真实 Inbox 人工提交和断言信号，不实现 TLS 或认证替身。
    private sealed class ScenarioRun : IAsyncDisposable
    {
        private readonly AcceptanceTestDirectory _directory = new();
        private int _pendingCount;
        private int _statusAfterCancellation;
        internal CancellationTokenSource Stop { get; } = new();
        internal AcceptanceRun Run { get; }
        internal MainWindow.RunState State { get; }
        internal Task<AcceptanceOutcome> Worker { get; }
        internal IsolatedUiScenario.Resources? Resources { get; private set; }
        internal byte[]? KeyCopy { get; private set; }
        internal List<(byte[] Owned, byte[] Copy)> Secrets { get; } = new();
        internal ConcurrentQueue<string> Status { get; } = new();
        internal int StatusAfterCancellation => Volatile.Read(ref _statusAfterCancellation);
        internal TaskCompletionSource<ControlClientApprovalPending> Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Active { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ScenarioRun(IsolatedUiCase scenario, IsolatedUiScenario.Options? options = null,
            bool useAgreedApi = false, Action? statusFailure = null)
        {
            Run = AcceptanceRun.Create(_directory.Path, "isolated-engine");
            State = new MainWindow.RunState(Run, Stop, isHost: true);
            IsolatedUiScenario.Options effective = (options ?? new IsolatedUiScenario.Options()) with
            {
                Initialized = resources =>
                {
                    Resources = resources;
                    KeyCopy = resources.AccessKey.ToArray();
                    options?.Initialized?.Invoke(resources);
                },
                SecretAllocated = bytes => Secrets.Add((bytes, bytes.ToArray())),
            };
            Worker = useAgreedApi
                ? IsolatedUiScenario.RunAsync(Run, State.Inbox!, scenario, OnPending, OnStatus, Stop.Token)
                : IsolatedUiScenario.RunAsync(Run, State.Inbox!, scenario, OnPending, OnStatus, Stop.Token, effective);

            void OnPending(ControlClientApprovalPending value)
            {
                Interlocked.Increment(ref _pendingCount);
                State.PublishPending(value);
                Pending.TrySetResult(value);
            }
            void OnStatus(string text)
            {
                if (Stop.IsCancellationRequested) { Interlocked.Increment(ref _statusAfterCancellation); }
                Status.Enqueue(text);
                if (text.StartsWith("已双向认证", StringComparison.Ordinal)) { Active.TrySetResult(); }
                statusFailure?.Invoke();
            }
        }

        internal async Task<LocalApprovalSnapshot> AssertPendingAsync()
        {
            await Task.WhenAny(Pending.Task, Worker).WaitAsync(Guard);
            Assert.False(Worker.IsCompleted, Run.Log.All);
            ControlClientApprovalPending notification = await Pending.Task.WaitAsync(Guard);
            using CancellationTokenSource budget = new(Guard);
            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(10));
            while (State.Inbox!.PendingCount == 0)
            {
                Assert.False(Worker.IsCompleted, Run.Log.All);
                await timer.WaitForNextTickAsync(budget.Token);
            }
            LocalApprovalSnapshot snapshot = Assert.Single(State.Inbox.GetSnapshot());
            Assert.Equal(notification.SessionId, snapshot.Request.SessionId);
            Assert.Equal(notification.ShortCode, snapshot.Request.ShortCode);
            Assert.Equal(SessionPermission.Control, snapshot.Request.RequestedPermission);
            Assert.Equal(IPAddress.Loopback, snapshot.Request.RemoteAddress);
            Assert.Equal(1, Volatile.Read(ref _pendingCount));
            Assert.Equal(TimeSpan.FromMilliseconds(AuthProtocol.ApprovalWindowMilliseconds), notification.ApprovalWindow);
            Assert.False(Active.Task.IsCompleted);
            Assert.Null(State.AuthenticationFailure);
            Assert.Null(State.GetHostContext());
            Assert.DoesNotContain("serverProof=verified", Run.Log.All);
            Assert.DoesNotContain("[CLIENT][HOLD]", Run.Log.All);
            Assert.DoesNotContain("[ISOLATED][INJECT]", Run.Log.All);
            Assert.DoesNotContain("clientOutcome=PASS", Run.Log.All);
            Assert.DoesNotContain("RUN COMPLETE", Run.Log.All);
            if (Resources is not null) { Assert.Equal(0, Resources.AuthContext.SessionRegistry.ActiveSessionCount); }
            return snapshot;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!Worker.IsCompleted) { await Stop.CancelAsync(); }
                await Worker.WaitAsync(Guard);
            }
            finally
            {
                State.Dispose();
                Stop.Dispose();
                if (KeyCopy is not null) { CryptographicOperations.ZeroMemory(KeyCopy); }
                foreach (var (_, copy) in Secrets) { CryptographicOperations.ZeroMemory(copy); }
                _directory.Dispose();
            }
        }
    }
}
