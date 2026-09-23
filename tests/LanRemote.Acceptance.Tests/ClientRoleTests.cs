using System.IO;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using LanRemote.Core.Models;
using LanRemote.Transport;

namespace LanRemote.Acceptance.Tests;

public sealed class ClientRoleTests : IDisposable
{
    private readonly string _logDirectory = Path.Combine(
        Path.GetTempPath(), "LanRemote.ClientRoleTests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(17)]
    [InlineData(32)]
    public void Missing_Or_Invalid_Key_Length_Is_Unmet_Before_Connecting(int length)
    {
        string? problem = ClientRole.ValidateSuccessConfiguration(length, SessionPermission.Control, null, null);

        Assert.NotNull(problem);
        Assert.Contains("访问密钥", problem);
    }

    [Theory]
    [InlineData(SessionPermission.Control)]
    [InlineData(SessionPermission.ViewOnly)]
    public void Discovery_With_Valid_Key_Allows_Either_Defined_Permission(SessionPermission permission)
    {
        Assert.Null(ClientRole.ValidateSuccessConfiguration(16, permission, null, null));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1234)]
    public void Undefined_Permission_Is_Unmet(int permission)
    {
        string? problem = ClientRole.ValidateSuccessConfiguration(16, (SessionPermission)permission, null, null);

        Assert.NotNull(problem);
        Assert.Contains("请求权限", problem);
    }

    [Theory]
    [InlineData("127.0.0.1", "valid-pin-placeholder", true)]
    [InlineData("invalid-address", "invalid-pin", true)]
    [InlineData("127.0.0.1", null, false)]
    [InlineData(null, "valid-pin-placeholder", false)]
    [InlineData(" ", "valid-pin-placeholder", false)]
    [InlineData("127.0.0.1", " ", false)]
    public void Only_The_Direct_Address_And_Pin_Path_Is_Rejected_For_Placeholder_Identity(
        string? address, string? pin, bool direct)
    {
        string? problem = ClientRole.ValidateSuccessConfiguration(16, SessionPermission.Control, address, pin);

        if (direct)
        {
            Assert.NotNull(problem);
            Assert.Contains("占位 DeviceId", problem);
        }
        else
        {
            // 非直连仍由目标解析要求 deviceCode；此处通过不表示已找到设备或已认证。
            Assert.Null(problem);
        }
    }

    [Theory]
    [InlineData("client-server-proof-mismatch", ControlClientAuthenticationException.ServerProofFailureMessage)]
    [InlineData("client-remote-authentication-failed", "远端拒绝了认证或审批请求。")]
    [InlineData("client-challenge-pin-mismatch", "远端认证证书与实际 TLS 连接不一致。")]
    [InlineData("client-approval-timeout", "等待远端审批超时，请重试。")]
    [InlineData("client-approval-notification-failed", "无法显示远端审批等待状态，请重试。")]
    [InlineData("client-overgrant", "远端授予了超出请求的权限。")]
    public void Authentication_Rejection_Is_Fail_Not_A_TLS_Rejection(string rejection, string message)
    {
        ControlClientAuthenticationException exception = new(rejection, message);

        ClientRole.ScenarioOutcome outcome = ClientRole.ClassifyAuthenticationFailure(exception, CancellationToken.None);

        Assert.Equal(AcceptanceOutcome.Fail, outcome.Outcome);
        Assert.Equal(message, outcome.Detail);
        Assert.Equal("authentication", Field(outcome, "stage"));
        Assert.Equal(rejection, Field(outcome, "rejection"));
        Assert.True(outcome.ReachedWire);
        Assert.False(outcome.TlsStageRejection);
        Assert.False(outcome.ConnectionObservationUnknown);
        Assert.DoesNotContain(outcome.Fields, pair => pair.Key is "sessionId" or "grant" or "serverProof");
    }

    [Fact]
    public void Actual_TLS_Pin_Rejection_Remains_TLS_And_Does_Not_Echo_Exception_Payload()
    {
        const string secret = "不得回显的异常载荷";
        AuthenticationException exception = new(
            secret + " " + PeerCertificateValidator.RejectionPinMismatch, new IOException(secret));

        ClientRole.ScenarioOutcome outcome = ClientRole.ClassifyAuthenticationFailure(exception, CancellationToken.None);

        Assert.Equal(AcceptanceOutcome.Fail, outcome.Outcome);
        Assert.True(outcome.TlsStageRejection);
        Assert.Equal("tls", Field(outcome, "stage"));
        Assert.Equal(PeerCertificateValidator.RejectionPinMismatch, Field(outcome, "rejection"));
        Assert.DoesNotContain(secret, Describe(outcome));
    }

    [Theory]
    [InlineData(SocketError.ConnectionRefused)]
    [InlineData(SocketError.HostUnreachable)]
    [InlineData(SocketError.NetworkUnreachable)]
    [InlineData(SocketError.NetworkDown)]
    [InlineData(SocketError.TimedOut)]
    [InlineData(SocketError.AddressNotAvailable)]
    public void Connection_Preconditions_Are_Unmet_Even_When_Socket_Error_Is_Wrapped(SocketError error)
    {
        IOException exception = new("不应打印的包装消息", new SocketException((int)error));

        ClientRole.ScenarioOutcome outcome = ClientRole.ClassifyAuthenticationFailure(exception, CancellationToken.None);

        Assert.Equal(AcceptanceOutcome.PreconditionUnmet, outcome.Outcome);
        Assert.False(outcome.ReachedWire);
        Assert.False(outcome.ConnectionObservationUnknown);
        Assert.Equal("tcp", Field(outcome, "stage"));
        Assert.DoesNotContain(exception.Message, Describe(outcome));
    }

    [Fact]
    public void Reset_Is_Not_An_Unreachable_Server_And_Does_Not_Invent_Connection_Stage()
    {
        ClientRole.ScenarioOutcome outcome = ClientRole.ClassifyAuthenticationFailure(
            new SocketException((int)SocketError.ConnectionReset), CancellationToken.None);

        Assert.Equal(AcceptanceOutcome.Fail, outcome.Outcome);
        Assert.True(outcome.ConnectionObservationUnknown);
        Assert.False(outcome.ReachedWire);
        Assert.Equal("UNOBSERVED", Field(outcome, "connection"));
    }

    [Fact]
    public void Local_Connection_Budget_Cancellation_Is_Not_Operator_Abort()
    {
        ClientRole.ScenarioOutcome outcome = ClientRole.ClassifyAuthenticationFailure(
            new OperationCanceledException("本地连接预算到期"), CancellationToken.None);

        Assert.Equal(AcceptanceOutcome.Fail, outcome.Outcome);
        Assert.True(outcome.ConnectionObservationUnknown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Caller_Cancellation_Takes_Precedence_Over_Authentication_And_IO(bool authFailure)
    {
        using CancellationTokenSource caller = new();
        caller.Cancel();
        Exception failure = authFailure
            ? new ControlClientAuthenticationException("client-server-proof-mismatch",
                ControlClientAuthenticationException.ServerProofFailureMessage)
            : new IOException("取消同时发生的 I/O 异常");

        OperationCanceledException cancelled = Assert.Throws<OperationCanceledException>(() =>
            ClientRole.ClassifyAuthenticationFailure(failure, caller.Token));

        Assert.Equal(caller.Token, cancelled.CancellationToken);
        Assert.Null(cancelled.InnerException);
    }

    [Fact]
    public void Unexpected_Exception_Is_Harness_Error_Without_Secret_Text()
    {
        const string secret = "异常中模拟的敏感内容";
        InvalidOperationException exception = new(secret, new Exception(secret));

        ClientRole.ScenarioOutcome outcome = ClientRole.ClassifyAuthenticationFailure(exception, CancellationToken.None);

        Assert.Equal(AcceptanceOutcome.HarnessError, outcome.Outcome);
        Assert.True(outcome.ConnectionObservationUnknown);
        Assert.DoesNotContain(secret, Describe(outcome));
    }

    [Fact]
    public async Task Original_Seven_Arguments_Without_Key_Return_Unmet_And_A_Complete_Footer()
    {
        AcceptanceRun run = CreateRun();

        AcceptanceOutcome result = await ClientRole.RunAllAsync(
            run, [ClientRole.ScenarioSuccess], "7K3M-P9QX", null, null, 45873, CancellationToken.None);

        Assert.Equal(AcceptanceOutcome.PreconditionUnmet, result);
        Assert.Contains("scenario=success clientOutcome=UNMET", run.Log.All);
        Assert.Contains("hostEvidence=N/A", run.Log.All);
        Assert.DoesNotContain("[ID]", run.Log.All);
        Assert.DoesNotContain("[CLIENT][AUTH]", run.Log.All);
        Assert.Contains("[VERDICT] M4 = PENDING-HOST-EVIDENCE", run.Log.All);
        AssertFooter(run, result);
    }

    [Fact]
    public async Task Direct_Success_With_Key_Is_Unmet_Without_Invoking_Pending_Or_Logging_Key()
    {
        AcceptanceRun run = CreateRun();
        byte[] key = Encoding.ASCII.GetBytes("0123456789ABCDEF");
        int notifications = 0;

        AcceptanceOutcome result = await ClientRole.RunAllAsync(
            run, [ClientRole.ScenarioSuccess], "7K3M-P9QX", "127.0.0.1", new string('A', 64), 45873,
            CancellationToken.None, key, SessionPermission.ViewOnly, _ => notifications++);

        Assert.Equal(AcceptanceOutcome.PreconditionUnmet, result);
        Assert.Contains("占位 DeviceId", run.Log.All);
        Assert.Equal(0, notifications);
        Assert.DoesNotContain("[ID]", run.Log.All);
        Assert.DoesNotContain("[CLIENT][AUTH]", run.Log.All);
        Assert.DoesNotContain(Encoding.ASCII.GetString(key), run.Log.All);
        Assert.DoesNotContain(Convert.ToHexString(key), run.Log.All);
        Assert.DoesNotContain(Convert.ToBase64String(key), run.Log.All);
        AssertFooter(run, result);
    }

    [Fact]
    public async Task Precancelled_Run_Is_Operator_Abort_With_Footer_Not_A_Failed_Handshake()
    {
        AcceptanceRun run = CreateRun();
        using CancellationTokenSource caller = new();
        caller.Cancel();

        AcceptanceOutcome result = await ClientRole.RunAllAsync(
            run, [ClientRole.ScenarioSuccess], null, null, null, 45873, caller.Token);

        Assert.Equal(AcceptanceOutcome.InvalidRun, result);
        Assert.True(run.AbortedByOperator);
        Assert.Contains("（未执行）", run.Log.All);
        Assert.DoesNotContain("handshake failed", run.Log.All);
        AssertFooter(run, result);
    }

    [Fact]
    public async Task Cancellation_Inside_Loop_Leaves_An_Incomplete_Scenario_And_Footer()
    {
        AcceptanceRun run = CreateRun();
        using CancellationTokenSource caller = new();
        run.Log.LineWritten += line =>
        {
            if (line.StartsWith("[CLIENT] scenario", StringComparison.Ordinal)) { caller.Cancel(); }
        };

        AcceptanceOutcome result = await ClientRole.RunAllAsync(
            run, [ClientRole.ScenarioSuccess], null, null, null, 45873, caller.Token);

        Assert.Equal(AcceptanceOutcome.InvalidRun, result);
        Assert.True(run.AbortedByOperator);
        Assert.Contains("scenario=success clientOutcome=INVALID_RUN", run.Log.All);
        Assert.Contains("connection=UNOBSERVED", run.Log.All);
        Assert.DoesNotContain("[ID]", run.Log.All);
        AssertFooter(run, result);
    }

    [Fact]
    public async Task Loop_Exception_Is_Sanitized_And_Still_Completes_Footer()
    {
        AcceptanceRun run = CreateRun();
        const string secret = "模拟日志订阅方的敏感异常正文";
        bool injected = false;
        run.Log.LineWritten += line =>
        {
            if (!injected && line.StartsWith("[CLIENT] scenario", StringComparison.Ordinal))
            {
                injected = true;
                throw new InvalidOperationException(secret, new Exception(secret));
            }
        };

        AcceptanceOutcome result = await ClientRole.RunAllAsync(
            run, [ClientRole.ScenarioSuccess], null, null, null, 45873, CancellationToken.None);

        Assert.True(injected);
        Assert.Equal(AcceptanceOutcome.HarnessError, result);
        Assert.True(run.HasBackgroundFaults);
        Assert.Contains("scenario=success clientOutcome=HARNESS_ERROR", run.Log.All);
        Assert.DoesNotContain(secret, run.BackgroundFaultSummary);
        Assert.DoesNotContain(secret, run.Log.All);
        AssertFooter(run, result);
    }

    [Fact]
    public async Task Separate_Runs_Do_Not_Share_Last_Scenario_State()
    {
        AcceptanceRun missingKey = CreateRun();
        AcceptanceRun direct = CreateRun();

        AcceptanceOutcome[] results = await Task.WhenAll(
            ClientRole.RunAllAsync(missingKey, [ClientRole.ScenarioSuccess], null, null, null, 45873,
                CancellationToken.None),
            ClientRole.RunAllAsync(direct, [ClientRole.ScenarioSuccess], null, "127.0.0.1", new string('B', 64), 45873,
                CancellationToken.None, new byte[16]));

        Assert.All(results, result => Assert.Equal(AcceptanceOutcome.PreconditionUnmet, result));
        Assert.Contains("缺失或长度无效", missingKey.Log.All);
        Assert.DoesNotContain("占位 DeviceId", missingKey.Log.All);
        Assert.Contains("占位 DeviceId", direct.Log.All);
        Assert.DoesNotContain("缺失或长度无效", direct.Log.All);
        AssertFooter(missingKey, results[0]);
        AssertFooter(direct, results[1]);
    }

    [Fact]
    public async Task Empty_Scenario_List_Cannot_Pass()
    {
        AcceptanceRun run = CreateRun();

        AcceptanceOutcome result = await ClientRole.RunAllAsync(
            run, [], null, null, null, 45873, CancellationToken.None);

        Assert.Equal(AcceptanceOutcome.PreconditionUnmet, result);
        AssertFooter(run, result);
    }

    private AcceptanceRun CreateRun() => AcceptanceRun.Create(_logDirectory, "client-test");

    private static string Field(ClientRole.ScenarioOutcome outcome, string key) =>
        Assert.Single(outcome.Fields, pair => pair.Key == key).Value;

    private static string Describe(ClientRole.ScenarioOutcome outcome) =>
        outcome.Detail + outcome.HostExpectation + string.Join(" ", outcome.Fields.Select(pair => pair.Value));

    private static void AssertFooter(AcceptanceRun run, AcceptanceOutcome outcome)
    {
        string[] lines = run.Log.All.Split(Environment.NewLine);
        Assert.Single(lines, line => line == "RUN COMPLETE");
        Assert.Contains("[RESULT] outcome   = " + outcome.Code(), lines);
        Assert.Contains($"[RESULT] aborted   = {run.AbortedByOperator}", lines);
        Assert.Contains($"[RESULT] exitCode  = {(int)outcome} ({outcome.Describe()})", lines);
    }

    public void Dispose()
    {
        if (Directory.Exists(_logDirectory)) { Directory.Delete(_logDirectory, recursive: true); }
    }
}
