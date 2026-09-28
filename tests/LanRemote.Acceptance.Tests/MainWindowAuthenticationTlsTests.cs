using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using LanRemote.Transport;

namespace LanRemote.Acceptance.Tests;

[Collection(AcceptanceStageFiveCollection.Name)]
public sealed class MainWindowAuthenticationTlsTests
{
    private const string ProofFailureMessage = "远端身份验证失败，可能是错误密码或伪造设备广播";
    private const string GenericRejectionMessage = "远端拒绝了认证或审批请求。";
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan CleanupBudget = TimeSpan.FromSeconds(6);
    private static readonly Guid ServerDeviceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DeviceIdentity ClientIdentity = new(
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), "TEST-ONLY", "验收器真实 TLS 回归客户端", string.Empty);

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public Task Real_ServerProof_Mismatch_Reaches_ResultBanner_Through_The_Same_Run(bool pending) =>
        RunScenarioAsync(pending, abortAuthenticatedSession: false);

    [Fact(Timeout = 60_000)]
    public Task Gui_Abort_While_Real_Authenticated_Session_Is_Held_Closes_Peer_And_Invalidates_Run() =>
        RunScenarioAsync(pending: true, abortAuthenticatedSession: true);

    private static async Task RunScenarioAsync(bool pending, bool abortAuthenticatedSession)
    {
        using AcceptanceTestDirectory directory = new();
        await using StaDispatcherFixture sta = new();
        Dispatcher dispatcher = await sta.Ready.WaitAsync(Guard);
        await dispatcher.InvokeAsync(async () =>
        {
            await using ScriptedPeer peer = new(pending, corruptProof: !abortAuthenticatedSession);
            peer.Start();
            await using WindowRun ui = new(directory.Path, peer);
            AssertOffscreen(ui.Window);
            Assert.False(Field<bool>(ui.Window, "_ready"));
            Assert.False(Field<DispatcherTimer>(ui.Window, "_uiTimer").IsEnabled);
            Assert.Null(Field<Task?>(ui.Window, "_busyTask"));
            Assert.Null(Field<Task?>(ui.Window, "_keyTask"));
            Assert.Null(ui.State.GetHostContext());
            Assert.Null(ui.State.Inbox);
            Assert.Equal(directory.Path, Path.GetDirectoryName(ui.Run.Log.FilePath));
            AcceptanceLog processLog = Field<AcceptanceLog>(ui.Window, "_processLog");
            Assert.Equal(Path.Combine(directory.Path, "gui.log"), processLog.FilePath);
            Assert.False(processLog.FileUnavailable);
            Assert.Same(ui.Run, Field<AcceptanceRun>(ui.Window, "_run"));
            Assert.Same(ui.State, Field<MainWindow.RunState>(ui.Window, "_activeRun"));
            Assert.Same(ui.Worker, Field<Task<AcceptanceOutcome>>(ui.Window, "_runTask"));

            await peer.WaitForSignalAsync(peer.ResponseVerified.Task);
            if (pending)
            {
                await peer.WaitForSignalAsync(ui.PendingReceived.Task);
                ControlClientApprovalPending notification = await ui.PendingReceived.Task;
                Assert.Same(notification, ui.State.GetPending());
                Assert.Equal(peer.SessionId, notification.SessionId);
                Assert.Matches("^[0-9A-F]{6}$", notification.ShortCode);
            }
            else
            {
                Assert.False(ui.PendingReceived.Task.IsCompleted);
                Assert.Null(ui.State.GetPending());
            }
            Invoke(ui.Window, "RefreshAuthentication");
            string pendingText = ui.Named<TextBlock>("ClientPendingText").Text;
            if (pending)
            {
                Assert.Contains("已收到 pending；尚未验证远端身份，也未获得权限。", pendingText);
                Assert.Contains(ui.State.GetPending()!.ShortCode, pendingText);
            }
            else { Assert.Equal(string.Empty, pendingText); }
            Assert.False(ui.Worker.IsCompleted);
            Assert.False(peer.Finished.Task.IsCompleted);
            Assert.DoesNotContain("serverProof=verified", ui.Run.Log.All);
            Assert.Null(ui.State.AuthenticationFailure);
            peer.AllowReply.TrySetResult();

            AcceptanceOutcome expected;
            if (abortAuthenticatedSession)
            {
                // 只接受实际核心进入 using(session) 后的持有日志，连接失败不能替代这个信号。
                await peer.WaitForSignalAsync(ui.HoldingSession.Task);
                Assert.False(ui.Worker.IsCompleted);
                Assert.False(peer.Finished.Task.IsCompleted);
                Assert.Equal(1, peer.Host.ActiveConnections);
                Assert.Equal(1, peer.Host.AdmittedConnections);
                Assert.Contains($"serverProof=verified sessionId={peer.SessionId} grant=Control", ui.Run.Log.All);
                Assert.Null(ui.State.GetPending());
                Invoke(ui.Window, "RefreshAuthentication");
                Assert.Equal(string.Empty, ui.Named<TextBlock>("ClientPendingText").Text);
                Invoke(ui.Window, "UpdateButtons");
                Button stop = ui.Named<Button>("StopClientButton");
                Assert.True(stop.IsEnabled);
                stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, stop));
                Assert.True(ui.State.IsStopped);
                Assert.True(ui.Run.AbortedByOperator);
                Task cancellation = Assert.IsAssignableFrom<Task>(Field<Task?>(ui.Window, "_runCancellationTask"));
                await cancellation.WaitAsync(Guard);
                Assert.True(ui.State.Cts.IsCancellationRequested);
                expected = AcceptanceOutcome.InvalidRun;
            }
            else { expected = AcceptanceOutcome.Fail; }

            Assert.Equal(expected, await ui.Worker.WaitAsync(Guard));
            // 必须先看到 TLS peer 的 EOF/RST 和自然归还，之后 finally 才允许 StopAsync。
            await peer.AssertClosedBeforeStopAsync();
            Assert.True(ui.State.IsStopped);
            Assert.Null(ui.State.GetPending());
            Assert.False(ui.Run.HasBackgroundFaults);
            Assert.Equal(pending ? 1 : 0, ui.PendingCount);
            Assert.Equal(abortAuthenticatedSession, ui.Run.AbortedByOperator);
            if (abortAuthenticatedSession)
            {
                Task<ClientRole.ScenarioOutcome> core = Assert.IsAssignableFrom<Task<ClientRole.ScenarioOutcome>>(ui.Core);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await core.WaitAsync(Guard));
                Assert.True(core.IsCanceled);
                Assert.Null(ui.Outcome);
                Assert.Null(ui.State.AuthenticationFailure);
                Assert.Empty(Lines(ui.Run.Log, "[CLIENT][RESULT]"));
                Assert.DoesNotContain("localObjectHeldMs=", ui.Run.Log.All);
                Assert.DoesNotContain("[CLIENT][DISPOSE]", ui.Run.Log.All);
                Assert.DoesNotContain("clientOutcome=PASS", ui.Run.Log.All);
            }
            else
            {
                ClientRole.ScenarioOutcome outcome = Assert.IsType<ClientRole.ScenarioOutcome>(ui.Outcome);
                Assert.Equal(AcceptanceOutcome.Fail, outcome.Outcome);
                Assert.Equal(ProofFailureMessage, outcome.Detail);
                Assert.True(outcome.ReachedWire);
                Assert.False(outcome.TlsStageRejection);
                Assert.False(outcome.ConnectionObservationUnknown);
                Assert.Equal(new[] { "stage", "rejection" }, outcome.Fields.Select(pair => pair.Key));
                Assert.Equal("authentication", outcome.Fields.Single(pair => pair.Key == "stage").Value);
                Assert.Equal("client-server-proof-mismatch", outcome.Fields.Single(pair => pair.Key == "rejection").Value);
                // 在真实 WriteOutcome 返回时采样，排除随后 worker.Stop() 清 pending 造成的假覆盖。
                Assert.False(ui.StoppedAfterOutcome);
                Assert.Equal(pending, ui.PendingBeforeOutcome is not null);
                Assert.Null(ui.PendingAfterOutcome);
                Assert.Equal(ProofFailureMessage, ui.FailureAfterOutcome);
                Assert.Equal(ProofFailureMessage, ui.State.AuthenticationFailure);
                string result = Assert.Single(Lines(ui.Run.Log, "[CLIENT][RESULT]"));
                Assert.StartsWith("[CLIENT][RESULT] scenario=success clientOutcome=FAIL stage=authentication rejection=client-server-proof-mismatch ", result);
                Assert.EndsWith("// " + ProofFailureMessage, result);
                Assert.DoesNotContain(GenericRejectionMessage, ui.Run.Log.All);
                Assert.DoesNotContain("serverProof=verified", ui.Run.Log.All);
                Assert.DoesNotContain("sessionId=", ui.Run.Log.All);
                Assert.DoesNotContain("[CLIENT][HOLD]", ui.Run.Log.All);
                Assert.DoesNotContain("[CLIENT][DISPOSE]", ui.Run.Log.All);
                Assert.DoesNotContain("clientOutcome=PASS", ui.Run.Log.All);
            }

            // 交给实际 Reap -> FinishRole 展示；不直接调用 ShowResult，不预先摘除拥有权。
            Invoke(ui.Window, "ReapCompletedTasks");
            Assert.Null(Field<MainWindow.RunState?>(ui.Window, "_activeRun"));
            Assert.Null(Field<Task?>(ui.Window, "_runTask"));
            Assert.Null(Field<Task?>(ui.Window, "_runCancellationTask"));
            Assert.Null(Field<CancellationTokenSource?>(ui.Window, "_runCts"));
            Assert.Null(Field<MainWindow.RunState?>(ui.Window, "_displayedApprovalOwner"));
            Assert.Empty(Field<IReadOnlyList<LocalApprovalSnapshot>>(ui.Window, "_displayedApprovals"));
            Assert.False(Field<bool>(ui.Window, "_faulted"));
            Assert.Equal(string.Empty, ui.Named<TextBlock>("ClientPendingText").Text);
            Assert.Equal(Visibility.Visible, ui.Named<Border>("ResultBanner").Visibility);
            string banner = ui.Named<TextBlock>("ResultBannerText").Text;
            Assert.Equal(abortAuthenticatedSession
                ? "本轮结论：运行已作废（操作员中止）（INVALID_RUN）。控制端本地观测不等于 M4 通过；请核对 [CLIENT][RESULT] 与两机交叉核对清单。"
                : "本轮结论：不符合预期（FAIL）。" + ProofFailureMessage, banner);
            Assert.DoesNotContain(GenericRejectionMessage, banner);
            Assert.DoesNotContain("（PASS）", banner);
            Invoke(ui.Window, "ReapCompletedTasks");
            Assert.Equal(banner, ui.Named<TextBlock>("ResultBannerText").Text);
            Assert.Equal(expected, ui.Run.Settle(expected));
            Assert.Single(ui.Run.Log.ReadFrom(0, int.MaxValue), line => line == "RUN COMPLETE");
            Assert.Equal("[RESULT] outcome   = " + expected.Code(), Assert.Single(Lines(ui.Run.Log, "[RESULT] outcome")));
            Assert.DoesNotContain("[RESULT][CORRECTION]", ui.Run.Log.All);
            Assert.False(ui.Run.Log.FileUnavailable);
            Assert.Equal(ui.Run.Log.All + Environment.NewLine, File.ReadAllText(ui.Run.Log.FilePath!));
            Invoke(ui.Window, "PullLogs");
            peer.AssertNoSecrets(ui.Run.Log.All + Field<AcceptanceLog>(ui.Window, "_processLog").All
                + ui.Named<TextBox>("LogBox").Text + banner);
            foreach (string path in Directory.GetFiles(directory.Path, "*.log"))
            {
                peer.AssertNoSecrets(File.ReadAllText(path));
                Assert.DoesNotContain(GenericRejectionMessage, File.ReadAllText(path));
            }
            AssertOffscreen(ui.Window);
        }).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(45));
    }

    private static string[] Lines(AcceptanceLog log, string prefix) => log.ReadFrom(0, int.MaxValue)
        .Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).ToArray();

    private static void AssertOffscreen(MainWindow window)
    {
        Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
        Assert.IsType<DispatcherSynchronizationContext>(SynchronizationContext.Current);
        Assert.False(window.IsLoaded);
        Assert.False(window.IsVisible);
        Assert.Equal(IntPtr.Zero, new WindowInteropHelper(window).Handle);
        Assert.Null(PresentationSource.FromVisual(window));
    }

    private static FieldInfo PrivateField(string name) => typeof(MainWindow).GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingFieldException(typeof(MainWindow).FullName, name);

    private static T Field<T>(MainWindow window, string name) => (T)PrivateField(name).GetValue(window)!;

    private static object? Invoke(MainWindow window, string name, params object[] arguments) =>
        (typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainWindow).FullName, name)).Invoke(window, arguments);

    private sealed class WindowRun : IAsyncDisposable
    {
        public WindowRun(string directory, ScriptedPeer peer)
        {
            Window = new MainWindow(directory);
            Field<DispatcherTimer>(Window, "_uiTimer").Stop();
            State = Assert.IsType<MainWindow.RunState>(Invoke(Window, "StartRole", false));
            Run.Log.LineWritten += OnLine;
            Func<Task<AcceptanceOutcome>> role = async () =>
            {
                Core = ClientRole.RunSuccessAsync(Run, ClientIdentity, peer.Target, peer.Key,
                    SessionPermission.Control, OnPending, State.Cts.Token);
                Outcome = await Core.ConfigureAwait(false);
                PendingBeforeOutcome = State.GetPending();
                ClientRole.WriteOutcome(Run, ClientRole.ScenarioSuccess, Outcome);
                PendingAfterOutcome = State.GetPending();
                FailureAfterOutcome = State.AuthenticationFailure;
                StoppedAfterOutcome = State.IsStopped;
                return Run.Complete(Outcome.Outcome, Outcome.Detail);
            };
            // 仅替换依赖发现/vault 的外层编排；worker、分类、日志、RunState 和展示均为产品代码。
            MethodInfo worker = typeof(MainWindow).GetMethod("RunRoleWorkerAsync", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(typeof(MainWindow).FullName, "RunRoleWorkerAsync");
            Worker = Task.Run(() => (Task<AcceptanceOutcome>)worker.Invoke(null, new object[] { State, role })!);
            PrivateField("_runTask").SetValue(Window, Worker);
        }

        public MainWindow Window { get; }
        public MainWindow.RunState State { get; }
        public AcceptanceRun Run => State.Run;
        public Task<AcceptanceOutcome> Worker { get; }
        public Task<ClientRole.ScenarioOutcome>? Core { get; private set; }
        public ClientRole.ScenarioOutcome? Outcome { get; private set; }
        public ControlClientApprovalPending? PendingBeforeOutcome { get; private set; }
        public ControlClientApprovalPending? PendingAfterOutcome { get; private set; }
        public string? FailureAfterOutcome { get; private set; }
        public bool StoppedAfterOutcome { get; private set; }
        public int PendingCount { get; private set; }
        public TaskCompletionSource<ControlClientApprovalPending> PendingReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HoldingSession { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public T Named<T>(string name) where T : class => Assert.IsType<T>(Window.FindName(name));

        private void OnPending(ControlClientApprovalPending pending)
        {
            State.PublishPending(pending);
            PendingCount++;
            PendingReceived.TrySetResult(pending);
        }

        private void OnLine(string line)
        {
            if (line == "[CLIENT][HOLD] localObjectHoldTargetMs=5000 hostRegistry=UNOBSERVED")
            {
                HoldingSession.TrySetResult();
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!Worker.IsCompleted) { Invoke(Window, "StopCurrentRun", "测试失败后的有界回收"); }
                try
                {
                    if (Field<Task?>(Window, "_runCancellationTask") is { } cancellation)
                    {
                        await cancellation.WaitAsync(CleanupBudget);
                    }
                }
                finally
                {
                    try { await Worker.WaitAsync(CleanupBudget); }
                    finally
                    {
                        if (Core is { } core)
                        {
                            try { await core.WaitAsync(CleanupBudget); }
                            catch (OperationCanceledException) when (core.IsCanceled) { }
                        }
                    }
                }
            }
            finally
            {
                Run.Log.LineWritten -= OnLine;
                try { Invoke(Window, "ReapCompletedTasks"); }
                finally
                {
                    Field<DispatcherTimer>(Window, "_uiTimer").Stop();
                    Window.Close();
                }
            }
        }
    }

    // 仅保留 ControlClientConnectorTests.ScriptedPeer 的有效握手与独立 UTF8/HMAC 算法。
    // 不使用产品 AuthTranscriptBuilder / auth_* 序列化器，也不依赖另一代理的测试文件。
    private sealed class ScriptedPeer : IAsyncDisposable
    {
        private readonly X509Certificate2 _certificate;
        private readonly bool _pending;
        private readonly bool _corruptProof;
        private readonly List<byte[]> _secrets = new();
        private readonly byte[] _serverNonce = RandomNumberGenerator.GetBytes(32);
        private readonly byte[] _token = RandomNumberGenerator.GetBytes(32);
        private int _handlerCount;
        private int _stopRequested;
        private string? _closeKind;
        private int? _bytesAfterReply;
        private bool _closedBeforeStop;

        public ScriptedPeer(bool pending, bool corruptProof)
        {
            _pending = pending;
            _corruptProof = corruptProof;
            _secrets.AddRange(new[] { Key, _serverNonce, _token });
            _certificate = CreateCertificate();
            try
            {
                using TcpListener reservation = new(IPAddress.Loopback, 0);
                reservation.Start();
                int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
                reservation.Stop();
                Assert.True(ConnectionTarget.TryCreate(ServerDeviceId, IPAddress.Loopback, port,
                    Convert.ToHexString(SHA256.HashData(_certificate.RawData)), out ConnectionTarget? target));
                Target = target!;
                Host = new TransportHost(new[] { IPAddress.Loopback }, new LoopbackPolicy(), _certificate,
                    HandleAsync, new TransportHostOptions { Port = port, ShutdownTimeout = CleanupBudget });
            }
            catch
            {
                _certificate.Dispose();
                throw;
            }
        }

        public byte[] Key { get; } = RandomNumberGenerator.GetBytes(16);
        public Guid SessionId { get; } = Guid.NewGuid();
        public TransportHost Host { get; }
        public ConnectionTarget Target { get; }
        public TaskCompletionSource ResponseVerified { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Start()
        {
            TransportHostStartResult started = Host.Start();
            Assert.True(started.IsListening);
            Assert.Empty(started.Failures);
        }

        public async Task WaitForSignalAsync(Task signal)
        {
            await Task.WhenAny(signal, Finished.Task).WaitAsync(Guard);
            if (Finished.Task.IsCompleted) { await Finished.Task; }
            Assert.True(signal.IsCompleted, "TLS peer 已结束，但所需的真实客户端阶段没有到达。");
            await signal;
        }

        private async Task HandleAsync(AcceptedConnection connection, CancellationToken hostToken)
        {
            Interlocked.Increment(ref _handlerCount);
            using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
            budget.CancelAfter(Guard);
            CancellationToken ct = budget.Token;
            try
            {
                SslStream stream = connection.Stream;
                Assert.True(stream.IsAuthenticated);
                Assert.True(stream.IsEncrypted);
                Assert.NotNull(stream.LocalCertificate);
                byte[] pin = SHA256.HashData(stream.LocalCertificate.GetRawCertData());
                Assert.Equal(connection.Security.ServerCertificateSha256.ToArray(), pin);
                Assert.Equal(Target.ExpectedCertSha256.ToArray(), pin);
                byte[] hello = await ReadAsync(stream, ct);
                Assert.True(HelloFrame.TryParse(hello, out string? rejection), rejection);
                await SendAsync(stream, JsonSerializer.SerializeToUtf8Bytes(new
                {
                    type = "auth_challenge", protocol = 1, sessionId = SessionId.ToString("D"),
                    serverDeviceId = ServerDeviceId.ToString("D"), serverNonce = Convert.ToBase64String(_serverNonce),
                    certSha256 = Convert.ToHexString(pin), expiresInMs = 15_000,
                }), ct);
                byte[] response = await ReadAsync(stream, ct);
                _secrets.Add(response);
                using JsonDocument document = JsonDocument.Parse(response);
                JsonElement root = document.RootElement;
                Assert.Equal("auth_response", root.GetProperty("type").GetString());
                Assert.Equal(ClientIdentity.DeviceId.ToString("D"), root.GetProperty("clientDeviceId").GetString());
                Assert.Equal(ClientIdentity.DeviceName, root.GetProperty("clientName").GetString());
                Assert.Equal("control", root.GetProperty("requestedPermission").GetString());
                Assert.Equal(new[] { "clientDeviceId", "clientName", "clientNonce", "clientProof", "requestedPermission", "type" },
                    root.EnumerateObject().Select(property => property.Name).Order().ToArray());
                string nonceText = root.GetProperty("clientNonce").GetString()!;
                byte[] clientNonce = Convert.FromBase64String(nonceText);
                byte[] clientProof = Convert.FromBase64String(root.GetProperty("clientProof").GetString()!);
                _secrets.AddRange(new[] { clientNonce, clientProof });
                Assert.Equal(32, clientNonce.Length);
                Assert.Equal(Convert.ToBase64String(clientNonce), nonceText);
                Assert.Equal(32, clientProof.Length);
                byte[] transcript = Encoding.UTF8.GetBytes(string.Join('\0',
                    "LANREMOTE-AUTH-V1", SessionId.ToString("D"), ServerDeviceId.ToString("D"),
                    ClientIdentity.DeviceId.ToString("D"), Convert.ToBase64String(_serverNonce),
                    nonceText, Convert.ToHexString(pin), "control"));
                _secrets.Add(transcript);
                using HMACSHA256 hmac = new(Key);
                Assert.Equal(hmac.ComputeHash(transcript), clientProof);
                byte[] proof = hmac.ComputeHash(Encoding.UTF8.GetBytes(string.Join('\0',
                    "server", "LANREMOTE-GRANT-V1", Convert.ToHexString(SHA256.HashData(transcript)), "control")));
                _secrets.Add(proof.ToArray());
                if (_corruptProof) { proof[0] ^= 0x80; }
                _secrets.Add(proof);
                if (_pending) { await SendAsync(stream, "{\"type\":\"approval_pending\"}"u8.ToArray(), ct); }
                ResponseVerified.TrySetResult();
                await AllowReply.Task.WaitAsync(ct);
                await SendAsync(stream, JsonSerializer.SerializeToUtf8Bytes(new
                {
                    type = "auth_success", grantedPermission = "control", serverProof = Convert.ToBase64String(proof),
                    sessionToken = Convert.ToBase64String(_token), videoAttachExpiresInMs = 15_000,
                }), ct);
                try
                {
                    _bytesAfterReply = await stream.ReadAsync(new byte[1], ct);
                    Assert.Equal(0, _bytesAfterReply);
                    _closeKind = "eof";
                }
                catch (IOException)
                {
                    // 只在 TLS、challenge、clientProof、auth_success 均完成后把 RST 当成关闭。
                    // 预算取消或本地 host.Stop 绝不能制造关闭证据。
                    ct.ThrowIfCancellationRequested();
                    _closeKind = "reset";
                }
                ct.ThrowIfCancellationRequested();
                _closedBeforeStop = !hostToken.IsCancellationRequested && Volatile.Read(ref _stopRequested) == 0;
                Assert.True(_closedBeforeStop);
                Finished.TrySetResult();
            }
            catch (OperationCanceledException) when (hostToken.IsCancellationRequested)
            {
                Finished.TrySetCanceled(hostToken);
            }
            catch (Exception error)
            {
                // TransportHost 不负责传播脚本断言；独立任务让正文及 finally 都能观察故障。
                Finished.TrySetException(error);
            }
        }

        private static Task<byte[]> ReadAsync(SslStream stream, CancellationToken ct) => new FrameReader(stream)
            .ReadFrameAsync(TransportConstants.MaxPreAuthMessageBytes, Guard, Guard, ct);

        private static Task SendAsync(SslStream stream, byte[] payload, CancellationToken ct) => FrameWriter.WriteFrameAsync(
            stream, payload, TransportConstants.MaxPreAuthMessageBytes, Guard, ct);

        public async Task AssertClosedBeforeStopAsync()
        {
            await Finished.Task.WaitAsync(Guard);
            Assert.Equal(1, Volatile.Read(ref _handlerCount));
            Assert.Equal(0, Volatile.Read(ref _stopRequested));
            Assert.True(Host.IsRunning);
            Assert.True(_closedBeforeStop);
            Assert.Contains(_closeKind, new[] { "eof", "reset" });
            // RST 未返回读取计数，保留未知，不把字段默认 0 当成测量值。
            if (_closeKind == "eof") { Assert.Equal(0, _bytesAfterReply); }
            else { Assert.Null(_bytesAfterReply); }
            using CancellationTokenSource budget = new(Guard);
            while (Host.ActiveConnections != 0 || Host.AdmittedConnections != 0)
            {
                await Task.Delay(10, budget.Token);
            }
            Assert.Equal(0, Host.ActiveConnections);
            Assert.Equal(0, Host.AdmittedConnections);
        }

        public void AssertNoSecrets(string text)
        {
            foreach (byte[] secret in _secrets)
            {
                string base64 = Convert.ToBase64String(secret);
                Assert.DoesNotContain(base64, text);
                // JSON 默认会转义 Base64 中的加号，原样载荷落日志也必须被发现。
                Assert.DoesNotContain(JsonSerializer.Serialize(base64)[1..^1], text);
                Assert.DoesNotContain(Convert.ToHexString(secret), text, StringComparison.OrdinalIgnoreCase);
            }
            Assert.DoesNotContain("clientProof", text);
            Assert.DoesNotContain("sessionToken", text);
            Assert.DoesNotContain("clientNonce", text);
            Assert.DoesNotContain("serverNonce", text);
        }

        public async ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _stopRequested, 1);
            try
            {
                try
                {
                    TransportHostStopReport report = await Host.StopAsync(CleanupBudget).WaitAsync(Guard);
                    Assert.True(report.AllFinished, "TLS host 回收未完成，不能忽略存活连接。");
                }
                finally
                {
                    if (Volatile.Read(ref _handlerCount) != 0)
                    {
                        try { await Finished.Task.WaitAsync(CleanupBudget); }
                        catch (OperationCanceledException) when (Finished.Task.IsCanceled) { }
                    }
                }
                Assert.Equal(0, Host.ActiveConnections);
                Assert.Equal(0, Host.AdmittedConnections);
            }
            finally
            {
                try { await Host.DisposeAsync().AsTask().WaitAsync(Guard); }
                finally
                {
                    _certificate.Dispose();
                    foreach (byte[] secret in _secrets) { CryptographicOperations.ZeroMemory(secret); }
                }
            }
        }

        private static X509Certificate2 CreateCertificate()
        {
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            CertificateRequest request = new("CN=lanremote-ui-tls-test", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new(PeerCertificateValidator.ServerAuthEkuOid) }, false));
            using X509Certificate2 signed = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
            string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            byte[] pfx = signed.Export(X509ContentType.Pfx, password);
            try
            {
                // 与参考工厂一致，Schannel 必须使用 DefaultKeySet 的 PFX 往返，不访问设备 vault。
                return X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.DefaultKeySet);
            }
            finally { CryptographicOperations.ZeroMemory(pfx); }
        }
    }

    private sealed class LoopbackPolicy : ISubnetPolicy
    {
        public bool IsAllowedPeer(IPAddress localAddress, IPAddress remoteAddress) =>
            IPAddress.IsLoopback(localAddress) && IPAddress.IsLoopback(remoteAddress);
    }
}
