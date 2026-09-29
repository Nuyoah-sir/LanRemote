using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using LanRemote.Core.Models;
using LanRemote.Transport;
using Xunit.Abstractions;

namespace LanRemote.Acceptance.Tests;

public sealed class ClientRoleTests(ITestOutputHelper output) : IDisposable
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

    private const string SensitiveFailure = "不得输出的主失败清理失败包装正文与堆栈";

    public static IEnumerable<object[]> CompositeHandshakeCases() =>
        from shape in CompositeShapes()
        from scenario in ClientRole.KnownScenarios
        select new object[] { shape, scenario };

    public static IEnumerable<object[]> CompositeCancellationCases() =>
        from shape in CompositeShapes()
        from cancelled in new[] { false, true }
        select new object[] { shape, cancelled };

    private static string[] CompositeShapes() =>
        ["direct", "io-wrapper", "auth-wrapper", "cancel-wrapper", "deep-wrapper", "secondary-tree", "empty", "single", "unreadable"];

    [Theory]
    [MemberData(nameof(CompositeHandshakeCases))]
    public void Composite_Handshake_Is_Always_Unobserved_Harness_Error(string shape, string scenario)
    {
        AcceptanceRun run = CreateRun();
        Exception failure = CompositeFailure(shape);

        ClientRole.ScenarioOutcome outcome = ClientRole.ClassifyHandshakeFailure(run, scenario, failure);

        AssertHarnessFailure(outcome);
        Assert.True(run.HasBackgroundFaults);
        AssertSanitized(run);
        Assert.Contains("AggregateException", run.BackgroundFaultSummary);
    }

    [Theory]
    [MemberData(nameof(CompositeCancellationCases))]
    public void Composite_Authentication_Is_Not_Masked_By_Caller_Cancellation(string shape, bool cancelled)
    {
        using CancellationTokenSource caller = new();
        if (cancelled) { caller.Cancel(); }

        ClientRole.ScenarioOutcome outcome = ClientRole.ClassifyAuthenticationFailure(
            CompositeFailure(shape), caller.Token);

        AssertHarnessFailure(outcome);
    }

    [Theory]
    [InlineData(ClientRole.ScenarioPinMismatch, AcceptanceOutcome.Pass)]
    [InlineData(ClientRole.ScenarioCrossSubnet, AcceptanceOutcome.Pass)]
    [InlineData(ClientRole.ScenarioTimeout, AcceptanceOutcome.Fail)]
    public void Ordinary_Handshake_Retains_Its_Verdict_Without_Logging_Payload(
        string scenario, AcceptanceOutcome expected)
    {
        AcceptanceRun run = CreateRun();
        AuthenticationException failure = new(
            SensitiveFailure + PeerCertificateValidator.RejectionPinMismatch, new IOException(SensitiveFailure));

        ClientRole.ScenarioOutcome outcome = ClientRole.ClassifyHandshakeFailure(run, scenario, failure);

        Assert.Equal(expected, outcome.Outcome);
        Assert.True(outcome.TlsStageRejection);
        Assert.False(outcome.ConnectionObservationUnknown);
        Assert.False(run.HasBackgroundFaults);
        Assert.DoesNotContain(SensitiveFailure, Describe(outcome));
        AssertSanitized(run);
    }

    [Theory]
    [InlineData(SocketError.ConnectionRefused)]
    [InlineData(SocketError.HostUnreachable)]
    [InlineData(SocketError.NetworkUnreachable)]
    [InlineData(SocketError.NetworkDown)]
    [InlineData(SocketError.TimedOut)]
    [InlineData(SocketError.AddressNotAvailable)]
    public void Ordinary_Wrapped_Socket_Handshake_Remains_Unmet(SocketError error)
    {
        AcceptanceRun run = CreateRun();
        ClientRole.ScenarioOutcome outcome = ClientRole.ClassifyHandshakeFailure(run,
            ClientRole.ScenarioCrossSubnet, new IOException(SensitiveFailure, new SocketException((int)error)));

        Assert.Equal(AcceptanceOutcome.PreconditionUnmet, outcome.Outcome);
        Assert.Equal("tcp", Field(outcome, "stage"));
        Assert.False(outcome.ReachedWire);
        Assert.False(outcome.ConnectionObservationUnknown);
        Assert.False(run.HasBackgroundFaults);
        AssertSanitized(run);
    }

    [Theory]
    [MemberData(nameof(CompositeCancellationCases))]
    public async Task Composite_Connect_Failure_Survives_Execute_Run_And_Final_Combination(
        string shape, bool cancelled)
    {
        AcceptanceRun run = CreateRun();
        using CancellationTokenSource caller = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<TlsConnection> connecting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> trace = new();
        int scenarioCalls = 0;
        run.Log.LineWritten += line =>
        {
            if (line.StartsWith("[HARNESS][FAULT]", StringComparison.Ordinal)) { trace.Add("fault"); }
            if (line.StartsWith("[CLIENT][RESULT]", StringComparison.Ordinal)) { trace.Add("result"); }
            if (line == "RUN COMPLETE") { trace.Add("complete"); }
        };

        Task<AcceptanceOutcome> running = ClientRole.RunAllAsync(run,
            cancelled ? [ClientRole.ScenarioCrossSubnet, ClientRole.ScenarioPinMismatch] : [ClientRole.ScenarioCrossSubnet],
            caller.Token, scenario =>
            {
                scenarioCalls++;
                return ClientRole.RunAsync(run, caller.Token,
                    () => ClientRole.ExecuteAsync(run, null!, scenario, Target(), caller.Token,
                        default, SessionPermission.Control, null, (_, token) =>
                        {
                            Assert.Equal(caller.Token, token);
                            trace.Add("connect");
                            entered.SetResult();
                            return connecting.Task;
                        }),
                    () => { trace.Add("cleanup"); return ValueTask.CompletedTask; });
            });
        AcceptanceOutcome result;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancelled) { caller.Cancel(); }
            connecting.SetException(CompositeFailure(shape));
            result = await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            connecting.TrySetCanceled();
            try { await running; }
            catch (Exception)
            {
                // 主断言路径负责报告失败；这里始终放闸并观察原任务，避免超时后遗弃任务。
            }
        }

        Assert.Equal(cancelled ? AcceptanceOutcome.InvalidRun : AcceptanceOutcome.HarnessError, result);
        Assert.Equal(cancelled, run.AbortedByOperator);
        Assert.Equal(1, scenarioCalls);
        Assert.True(run.HasBackgroundFaults);
        Assert.Contains("scenario=cross-subnet clientOutcome=HARNESS_ERROR", run.Log.All);
        Assert.Contains("connection=UNOBSERVED", run.Log.All);
        Assert.Contains("不能给出整轮 sessionHandled 确定区间", run.Log.All);
        Assert.Contains("AggregateException", run.BackgroundFaultSummary);
        Assert.True(trace.IndexOf("connect") < trace.IndexOf("fault"));
        Assert.True(trace.IndexOf("fault") < trace.IndexOf("cleanup"));
        Assert.True(trace.IndexOf("cleanup") < trace.IndexOf("result"));
        Assert.True(trace.IndexOf("result") < trace.IndexOf("complete"));
        AssertSanitized(run);
        AssertFooter(run, result);
        output.WriteLine($"trace={string.Join(" -> ", trace)}; scenario=HARNESS_ERROR; " +
            $"final={result.Code()}; aborted={run.AbortedByOperator}; fault={run.HasBackgroundFaults}");
        output.WriteLine(run.BackgroundFaultSummary);
    }

    [Theory]
    [InlineData("auth")]
    [InlineData("io")]
    [InlineData("cancel")]
    public async Task Ordinary_Execute_Failure_Still_Prefers_Caller_Cancellation(string kind)
    {
        AcceptanceRun run = CreateRun();
        using CancellationTokenSource caller = new();
        Exception failure = kind switch
        {
            "auth" => new AuthenticationException(PeerCertificateValidator.RejectionPinMismatch),
            "io" => new IOException(SensitiveFailure),
            _ => new OperationCanceledException(SensitiveFailure),
        };

        OperationCanceledException observed = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ClientRole.ExecuteAsync(run, null!, ClientRole.ScenarioCrossSubnet, Target(), caller.Token,
                default, SessionPermission.Control, null, (_, _) =>
                {
                    caller.Cancel();
                    return Task.FromException<TlsConnection>(failure);
                }));

        Assert.Equal(caller.Token, observed.CancellationToken);
        Assert.Null(observed.InnerException);
        Assert.False(run.HasBackgroundFaults);
        Assert.DoesNotContain("handshake failed", run.Log.All);
        AssertSanitized(run);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_Scope_Records_Composite_Execution_Or_Cleanup_Before_Cancellation(bool duringCleanup)
    {
        AcceptanceRun run = CreateRun();
        using CancellationTokenSource caller = new();
        bool cleaned = false;
        ClientRole.ScenarioOutcome outcome = await ClientRole.RunAsync(run, caller.Token,
            () =>
            {
                if (duringCleanup) { return Task.FromResult(UnmetOutcome()); }
                caller.Cancel();
                return Task.FromException<ClientRole.ScenarioOutcome>(CompositeFailure("cancel-wrapper"));
            },
            () =>
            {
                cleaned = true;
                if (duringCleanup)
                {
                    caller.Cancel();
                    return ValueTask.FromException(CompositeFailure("deep-wrapper"));
                }
                return ValueTask.CompletedTask;
            });

        Assert.True(cleaned);
        Assert.Equal(AcceptanceOutcome.HarnessError, outcome.Outcome);
        Assert.True(run.HasBackgroundFaults);
        Assert.Contains("AggregateException", run.BackgroundFaultSummary);
        AssertSanitized(run);
    }

    [Theory]
    [InlineData("execution")]
    [InlineData("cleanup")]
    [InlineData("outcome")]
    public async Task Ordinary_Run_Scope_Failure_Still_Prefers_Caller_Cancellation(string source)
    {
        AcceptanceRun run = CreateRun();
        using CancellationTokenSource caller = new();
        bool cleaned = false;

        OperationCanceledException observed = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ClientRole.RunAsync(run, caller.Token,
                () =>
                {
                    if (source == "cleanup") { return Task.FromResult(UnmetOutcome()); }
                    caller.Cancel();
                    return source == "execution"
                        ? Task.FromException<ClientRole.ScenarioOutcome>(new InvalidOperationException(SensitiveFailure))
                        : Task.FromResult(ClientRole.ClassifyAuthenticationFailure(
                            new InvalidOperationException(SensitiveFailure), CancellationToken.None));
                },
                () =>
                {
                    cleaned = true;
                    if (source != "cleanup") { return ValueTask.CompletedTask; }
                    caller.Cancel();
                    return ValueTask.FromException(new IOException(SensitiveFailure));
                }));

        Assert.True(cleaned);
        Assert.Equal(caller.Token, observed.CancellationToken);
        Assert.Null(observed.InnerException);
        Assert.Equal(source != "outcome", run.HasBackgroundFaults);
        AssertSanitized(run);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_Cancellation_Without_Composite_Cleanup_Rethrows_Original(bool cleanupFails)
    {
        AcceptanceRun run = CreateRun();
        using CancellationTokenSource caller = new();
        IOException inner = new(SensitiveFailure);
        OperationCanceledException original = new(SensitiveFailure, inner, caller.Token);
        bool cleaned = false;

        Task<ClientRole.ScenarioOutcome> CancelAtSource()
        {
            caller.Cancel();
            throw original;
        }

        OperationCanceledException observed = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ClientRole.RunAsync(run, caller.Token, CancelAtSource, () =>
            {
                cleaned = true;
                return cleanupFails ? ValueTask.FromException(new IOException(SensitiveFailure)) : ValueTask.CompletedTask;
            }));

        Assert.True(cleaned);
        Assert.Same(original, observed);
        Assert.Same(inner, observed.InnerException);
        Assert.Equal(caller.Token, observed.CancellationToken);
        Assert.Contains(nameof(CancelAtSource), observed.StackTrace);
        Assert.Equal(cleanupFails, run.HasBackgroundFaults);
        AssertSanitized(run);
    }

    [Fact]
    public async Task Ordinary_Cancellation_With_Composite_Cleanup_Retains_Abort_And_Fault()
    {
        AcceptanceRun run = CreateRun();
        using CancellationTokenSource caller = new();
        ClientRole.ScenarioOutcome? scenarioOutcome = null;
        AcceptanceOutcome result = await ClientRole.RunAllAsync(run, [ClientRole.ScenarioCrossSubnet],
            caller.Token, async _ => scenarioOutcome = await ClientRole.RunAsync(run, caller.Token,
                () =>
                {
                    caller.Cancel();
                    return Task.FromCanceled<ClientRole.ScenarioOutcome>(caller.Token);
                }, () => ValueTask.FromException(CompositeFailure("direct"))));

        Assert.Equal(AcceptanceOutcome.InvalidRun, result);
        Assert.True(run.AbortedByOperator);
        Assert.True(run.HasBackgroundFaults);
        Assert.Contains("Client Discovery/context 释放", run.BackgroundFaultSummary);
        Assert.Contains("scenario=cross-subnet clientOutcome=HARNESS_ERROR", run.Log.All);
        Assert.Contains("connection=UNOBSERVED", run.Log.All);
        Assert.Contains("不能给出整轮 sessionHandled 确定区间", run.Log.All);
        AssertHarnessFailure(Assert.IsType<ClientRole.ScenarioOutcome>(scenarioOutcome));
        Assert.True(scenarioOutcome!.HasCompositeFailure);
        AssertSanitized(run);
        AssertFooter(run, result);
    }

    [Theory]
    [InlineData("loop", false)]
    [InlineData("loop", true)]
    [InlineData("summary", false)]
    [InlineData("summary", true)]
    [InlineData("abort", true)]
    public async Task Composite_Log_Fault_Is_Observed_Even_When_Wrapped_In_Cancellation(
        string boundary, bool cancelled)
    {
        AcceptanceRun run = CreateRun();
        using CancellationTokenSource caller = new();
        string prefix = boundary switch
        {
            "loop" => "[CLIENT] scenario",
            "summary" => "[VERDICT]",
            "abort" => "[RUN] 操作员中止",
            _ => throw new ArgumentOutOfRangeException(nameof(boundary)),
        };
        bool injected = false;
        run.Log.LineWritten += line =>
        {
            if (boundary == "abort" && line.StartsWith("[CLIENT] scenario", StringComparison.Ordinal))
            {
                caller.Cancel();
            }
            if (!injected && line.StartsWith(prefix, StringComparison.Ordinal))
            {
                injected = true;
                if (cancelled) { caller.Cancel(); }
                throw CompositeFailure("cancel-wrapper");
            }
        };

        AcceptanceOutcome result = await ClientRole.RunAllAsync(run,
            [ClientRole.ScenarioSuccess], null, null, null, 45873, caller.Token);

        Assert.True(injected);
        Assert.Equal(cancelled ? AcceptanceOutcome.InvalidRun : AcceptanceOutcome.HarnessError, result);
        Assert.Equal(cancelled, run.AbortedByOperator);
        Assert.True(run.HasBackgroundFaults);
        Assert.Contains("AggregateException", run.BackgroundFaultSummary);
        AssertSanitized(run);
        AssertFooter(run, result);
    }

    [Theory]
    [InlineData("[RESULT] outcome", false)]
    [InlineData("[RESULT] outcome", true)]
    [InlineData("RUN COMPLETE", false)]
    [InlineData("RUN COMPLETE", true)]
    public async Task Composite_Footer_Fault_Rethrows_Original_Tree_Without_Replaying_Lines(
        string boundary, bool cancelled)
    {
        AcceptanceRun run = CreateRun();
        using CancellationTokenSource caller = new();
        Exception original = CompositeFailure("cancel-wrapper");
        AggregateException aggregate = Assert.IsType<AggregateException>(original.InnerException);
        Exception primary = aggregate.InnerExceptions[0];
        AggregateException cleanup = Assert.IsType<AggregateException>(aggregate.InnerExceptions[1]);
        Exception cleanupInner = Assert.Single(cleanup.InnerExceptions);
        bool injected = false;
        run.Log.LineWritten += line =>
        {
            if (!injected && line.StartsWith(boundary, StringComparison.Ordinal))
            {
                injected = true;
                if (cancelled) { caller.Cancel(); }
                throw original;
            }
        };

        Exception? observed = await Record.ExceptionAsync(async () =>
        {
            await ClientRole.RunAllAsync(run, [ClientRole.ScenarioSuccess], null, null, null, 45873, caller.Token);
        });

        Assert.True(injected);
        Assert.Equal(cancelled, run.AbortedByOperator);
        Assert.True(run.HasBackgroundFaults);
        Assert.Contains("Client 完成日志", run.BackgroundFaultSummary);
        Assert.Contains("AggregateException", run.BackgroundFaultSummary);
        AssertSanitized(run);
        string[] prefixes = ["[RESULT] outcome", "[RESULT] detail", "[RESULT] background",
            "[RESULT] aborted", "[RESULT] exitCode", "RUN COMPLETE"];
        foreach (string log in new[] { run.Log.All, File.ReadAllText(Assert.IsType<string>(run.Log.FilePath)) })
        {
            string[] lines = log.Split(Environment.NewLine);
            Assert.Single(lines, line => line.StartsWith(boundary, StringComparison.Ordinal));
            foreach (string prefix in prefixes)
            {
                Assert.InRange(lines.Count(line => line.StartsWith(prefix, StringComparison.Ordinal)), 0, 1);
            }
        }
        Assert.Same(original, observed);
        Assert.Same(aggregate, observed!.InnerException);
        Assert.Equal(2, aggregate.InnerExceptions.Count);
        Assert.Same(primary, aggregate.InnerExceptions[0]);
        Assert.Same(cleanup, aggregate.InnerExceptions[1]);
        Assert.Same(cleanupInner, Assert.Single(cleanup.InnerExceptions));
        output.WriteLine($"footer={boundary}; cancelled={cancelled}; originalTree=True; fault=True; replay=False");
    }

    [Fact]
    public async Task Fault_Logging_Callback_Cannot_Erase_Composite_Fault_Or_Prevent_Cleanup()
    {
        AcceptanceRun run = CreateRun();
        bool cleaned = false;
        run.Log.LineWritten += line =>
        {
            if (line.StartsWith("[HARNESS][FAULT]", StringComparison.Ordinal))
            {
                throw CompositeFailure("io-wrapper");
            }
        };
        AcceptanceOutcome result = await ClientRole.RunAllAsync(run, [ClientRole.ScenarioCrossSubnet],
            CancellationToken.None, _ => ClientRole.RunAsync(run, CancellationToken.None,
                () => Task.FromException<ClientRole.ScenarioOutcome>(CompositeFailure("direct")),
                () => { cleaned = true; return ValueTask.CompletedTask; }));

        Assert.True(cleaned);
        Assert.Equal(AcceptanceOutcome.HarnessError, result);
        Assert.True(run.HasBackgroundFaults);
        AssertSanitized(run);
        AssertFooter(run, result);
    }

    private static Exception CompositeFailure(string shape)
    {
        AggregateException aggregate = new(SensitiveFailure,
            new IOException(SensitiveFailure, new SocketException((int)SocketError.ConnectionRefused)),
            new AggregateException(SensitiveFailure, new InvalidOperationException(SensitiveFailure)));
        return shape switch
        {
            "direct" => aggregate,
            "io-wrapper" => new IOException(SensitiveFailure, aggregate),
            "auth-wrapper" => new AuthenticationException(SensitiveFailure + PeerCertificateValidator.RejectionPinMismatch, aggregate),
            "cancel-wrapper" => new OperationCanceledException(SensitiveFailure, aggregate),
            "deep-wrapper" => new Exception(SensitiveFailure, new IOException(SensitiveFailure, aggregate)),
            "secondary-tree" => new AggregateException(SensitiveFailure,
                new AuthenticationException(PeerCertificateValidator.RejectionPinMismatch), aggregate),
            "empty" => new AggregateException(SensitiveFailure, Array.Empty<Exception>()),
            "single" => new AggregateException(SensitiveFailure, new OperationCanceledException(SensitiveFailure)),
            "unreadable" => new UnreadableException(aggregate),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
    }

    private sealed class UnreadableException(Exception inner) : Exception(SensitiveFailure, inner)
    {
        public override string Message => throw new InvalidOperationException("不应读取复合异常消息");
        public override string ToString() => throw new InvalidOperationException("不应格式化复合异常树");
    }

    private static ConnectionTarget Target()
    {
        Assert.True(ConnectionTarget.TryCreate(Guid.Parse("11111111-1111-1111-1111-111111111111"),
            IPAddress.Loopback, 45873, new string('A', 64), out ConnectionTarget? target));
        return Assert.IsType<ConnectionTarget>(target);
    }

    private static ClientRole.ScenarioOutcome UnmetOutcome() => new(
        AcceptanceOutcome.PreconditionUnmet, "测试未建连", [], "无连接", ReachedWire: false);

    private static void AssertHarnessFailure(ClientRole.ScenarioOutcome outcome)
    {
        Assert.Equal(AcceptanceOutcome.HarnessError, outcome.Outcome);
        Assert.True(outcome.ConnectionObservationUnknown);
        Assert.False(outcome.ReachedWire);
        Assert.False(outcome.TlsStageRejection);
        Assert.Equal("UNOBSERVED", Field(outcome, "connection"));
        Assert.DoesNotContain(SensitiveFailure, Describe(outcome));
        Assert.DoesNotContain(outcome.Fields, pair => pair.Key is "rejection" or "sessionId" or "serverProof");
    }

    private static void AssertSanitized(AcceptanceRun run)
    {
        Assert.DoesNotContain(SensitiveFailure, run.Log.All);
        Assert.DoesNotContain(SensitiveFailure, run.BackgroundFaultSummary);
        if (run.Log.FilePath is { } path) { Assert.DoesNotContain(SensitiveFailure, File.ReadAllText(path)); }
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
