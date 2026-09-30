using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

// 替换连接获取和应用层 IO，不执行或声称执行真实 TLS 握手。
// 公开认证入口仍执行真实 framing、严格解析、随机 nonce、serverProof 校验和会话构造。
public sealed class ControlClientSuccessTimestampTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MachineBudget = TimeSpan.FromSeconds(9);
    private static readonly TimeSpan ApprovalBudget = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan PayloadBudget = TimeSpan.FromMilliseconds(1100);
    private static readonly TimeSpan PendingParseGap = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan NotificationCost = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan SuccessReadCost = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan ConsumerDelay = TimeSpan.FromMilliseconds(250);

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Success_Uses_Original_Complete_Payload_Not_Zero_Construction_Or_First_Query(bool pending)
    {
        Fixture fixture = new(pending, hint: 5000);
        try
        {
            Task<AuthenticatedControlSession> original = fixture.Start();
            await fixture.ReleaseSuccessAsync(original);
            AuthenticatedControlSession session = await original.WaitAsync(Guard);
            fixture.AssertDelivered(original, session);

            // 原 payload deadline.Dispose 在 ReadFrameAsync 采样之后、ReadReplyAsync 解析之前。
            // 此独立间隙覆盖解析/授权/MAC 之前的构造前耗时；不假称观察到 MAC 内部耗时。
            Assert.Equal(TimeSpan.FromMilliseconds(400), fixture.Clock.GetElapsedTime(
                fixture.Clock.CompletedAt["success"], fixture.ConstructedAt));
            Assert.Equal(TimeSpan.FromMilliseconds(600), fixture.Clock.GetElapsedTime(
                fixture.ConstructedAt, fixture.Clock.GetTimestamp()));
            fixture.Clock.Advance(ConsumerDelay);
            Assert.Equal(TimeSpan.FromMilliseconds(3750), session.GetRemainingAttachBudget());
            Assert.Equal(TimeSpan.FromMilliseconds(3750), session.GetRemainingAttachBudget());

            // 非系统时钟、非 TimeSpan tick 频率；UTC 前后跳变都不能改变单调余量。
            fixture.Clock.AdvanceUtc(TimeSpan.FromDays(3));
            Assert.Equal(TimeSpan.FromMilliseconds(3750), session.GetRemainingAttachBudget());
            fixture.Clock.AdvanceUtc(TimeSpan.FromDays(-6));
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(125));
            Assert.Equal(TimeSpan.FromMilliseconds(3625), session.GetRemainingAttachBudget());
            fixture.AssertControlUsable(session);
        }
        finally
        {
            await fixture.FinishAsync();
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false, 250)]
    [InlineData(false, 251)]
    [InlineData(true, 250)]
    [InlineData(true, 251)]
    public async Task Hint_Exhausted_Before_Construction_Still_Delivers_Control(bool pending, int beforeConstructionMs)
    {
        Fixture fixture = new(pending, hint: 250, beforeConstructionMs: beforeConstructionMs, observerMs: 50);
        try
        {
            Task<AuthenticatedControlSession> original = fixture.Start();
            await fixture.ReleaseSuccessAsync(original);
            AuthenticatedControlSession session = await original.WaitAsync(Guard);
            fixture.AssertDelivered(original, session);
            Assert.Equal(TimeSpan.FromMilliseconds(beforeConstructionMs), fixture.Clock.GetElapsedTime(
                fixture.Clock.CompletedAt["success"], fixture.ConstructedAt));
            Assert.True(fixture.Clock.GetElapsedTime(fixture.Clock.CompletedAt["success"], fixture.ConstructedAt)
                >= TimeSpan.FromMilliseconds(session.VideoAttachExpiresInMsHint));
            fixture.AssertControlUsable(session);
            Assert.Throws<TimeoutException>(() => session.GetRemainingAttachBudget());
            Assert.Throws<TimeoutException>(() => session.GetRemainingAttachBudget());
            fixture.AssertControlUsable(session);
        }
        finally
        {
            await fixture.FinishAsync();
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false, 15_000)]
    [InlineData(false, 15_001)]
    [InlineData(false, int.MaxValue)]
    [InlineData(true, 15_000)]
    [InlineData(true, 15_001)]
    [InlineData(true, int.MaxValue)]
    public async Task Large_Hint_Parses_Unchanged_But_Local_Consumption_Is_Capped_At_Fifteen_Seconds(bool pending, int hint)
    {
        Fixture fixture = new(pending, hint);
        try
        {
            Task<AuthenticatedControlSession> original = fixture.Start();
            await fixture.ReleaseSuccessAsync(original);
            AuthenticatedControlSession session = await original.WaitAsync(Guard);
            fixture.AssertDelivered(original, session);
            Assert.Equal(hint, session.VideoAttachExpiresInMsHint);
            fixture.Clock.Advance(ConsumerDelay);
            TimeSpan remaining = TimeSpan.FromMilliseconds(13_750);
            Assert.Equal(remaining, session.GetRemainingAttachBudget());
            fixture.Clock.Advance(remaining - TimeSpan.FromTicks(1));
            Assert.Equal(TimeSpan.FromTicks(1), session.GetRemainingAttachBudget());
            fixture.Clock.Advance(TimeSpan.FromTicks(1));
            Assert.Throws<TimeoutException>(() => session.GetRemainingAttachBudget());
            fixture.Clock.Advance(TimeSpan.FromTicks(1));
            Assert.Throws<TimeoutException>(() => session.GetRemainingAttachBudget());
            fixture.AssertControlUsable(session);
        }
        finally
        {
            await fixture.FinishAsync();
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false, "live")]
    [InlineData(false, "expired")]
    [InlineData(false, "negative")]
    [InlineData(true, "live")]
    [InlineData(true, "expired")]
    [InlineData(true, "negative")]
    public async Task Delivered_Session_Orders_Revocation_Then_Cancellation_Then_Timeout(bool pending, string state)
    {
        Fixture fixture = new(pending, hint: 5000);
        using CancellationTokenSource canceled = new();
        try
        {
            Task<AuthenticatedControlSession> original = fixture.Start();
            await fixture.ReleaseSuccessAsync(original);
            AuthenticatedControlSession session = await original.WaitAsync(Guard);
            fixture.AssertDelivered(original, session);
            if (state == "negative")
            {
                // 只在认证交付后模拟时钟倒退；不改变 timer，也不在取时调用内制造事件。
                fixture.Clock.SetTimestamp(fixture.Clock.CompletedAt["success"] - 2);
                Assert.Equal(-TimeSpan.FromTicks(1), fixture.Clock.GetElapsedTime(
                    fixture.Clock.CompletedAt["success"], fixture.Clock.GetTimestamp()));
            }
            else if (state == "expired")
            {
                fixture.Clock.Advance(TimeSpan.FromSeconds(4));
            }

            if (state == "live") Assert.Equal(TimeSpan.FromSeconds(4), session.GetRemainingAttachBudget());
            else Assert.Throws<TimeoutException>(() => session.GetRemainingAttachBudget());
            canceled.Cancel();
            OperationCanceledException error = Assert.Throws<OperationCanceledException>(
                () => session.GetRemainingAttachBudget(canceled.Token));
            Assert.Equal(canceled.Token, error.CancellationToken);
            fixture.AssertControlUsable(session);

            session.RevokeForOwnerCleanup();
            Assert.Throws<ObjectDisposedException>(() => session.GetRemainingAttachBudget(canceled.Token));
            Assert.Throws<ObjectDisposedException>(() => session.GetRemainingAttachBudget());
            Assert.False(fixture.Connection.IsCloseRequested);
            Assert.Equal(0, fixture.Ssl.DisposeCalls);
        }
        finally
        {
            await fixture.FinishAsync();
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wrong_Server_Proof_Still_Rejects_Before_Any_Session_Construction(bool pending)
    {
        Fixture fixture = new(pending, hint: 5000, wrongProof: true);
        try
        {
            Task<AuthenticatedControlSession> original = fixture.Start();
            await fixture.ReleaseSuccessAsync(original);
            Exception? observed = await ObserveAsync(original).WaitAsync(Guard);
            ControlClientAuthenticationException error = Assert.IsType<ControlClientAuthenticationException>(observed);
            Assert.Equal("client-server-proof-mismatch", error.Rejection);
            Assert.Equal(ControlClientAuthenticationException.ServerProofFailureMessage, error.DisplayMessage);
            Assert.Null(error.InnerException);
            Assert.Equal(TaskStatus.Faulted, original.Status);
            Assert.Same(error, await ObserveAsync(original).WaitAsync(Guard));
            Assert.Empty(fixture.Constructions);
            Assert.DoesNotContain("session-constructed", fixture.Clock.Events);
            fixture.AssertPayloadTimeline();
            fixture.Ssl.AssertConsumed(pending);
            Assert.True(fixture.Connection.IsCloseRequested);
            Assert.Equal(1, fixture.Ssl.DisposeCalls);
            Assert.Empty(fixture.Connection.CleanupErrors);
            Assert.Equal(0, fixture.Clock.ActiveTimers);
            Assert.Equal(0, fixture.Clock.TimerCallbacks);
        }
        finally
        {
            await fixture.FinishAsync();
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<Exception?> ObserveAsync(Task original)
    {
        try { await original.ConfigureAwait(false); return null; }
        catch (Exception error) { return error; }
    }

    private sealed class Fixture
    {
        private const string KeyHex = "0F1E2D3C4B5A69788796A5B4C3D2E1F0";
        private readonly bool _pending;
        private readonly int _hint;
        private readonly TimeSpan _beforeConstruction;
        private readonly TimeSpan _observerCost;
        private readonly byte[] _key = Convert.FromHexString(KeyHex);
        private readonly TcpClient _tcp = new(AddressFamily.InterNetwork);
        private readonly CancellationTokenSource _caller = new();
        private readonly ConnectionTarget _target;
        private readonly TransportTimeouts _timeouts = new(
            connectTimeout: TimeSpan.FromSeconds(3), handshakeTimeout: TimeSpan.FromSeconds(5),
            lengthPrefixTimeout: TimeSpan.FromMilliseconds(1700), payloadTimeout: PayloadBudget,
            helloTimeout: TimeSpan.FromSeconds(2), preAuthEnvelopeTimeout: TimeSpan.FromSeconds(8));
        private Task<AuthenticatedControlSession>? _original;
        private ControlClientApprovalPending? _notification;
        private long _notificationBefore;
        private long _notificationAfter;
        private int _connectCalls;
        internal EventClock Clock { get; } = new();
        internal ScriptedSsl Ssl { get; }
        internal TlsConnection Connection { get; }
        internal List<AuthenticatedControlSession> Constructions { get; } = new();
        internal long ConstructedAt { get; private set; }

        internal Fixture(bool pending, int hint, int beforeConstructionMs = 400, int observerMs = 600,
            bool wrongProof = false)
        {
            _pending = pending;
            _hint = hint;
            _beforeConstruction = TimeSpan.FromMilliseconds(beforeConstructionMs);
            _observerCost = TimeSpan.FromMilliseconds(observerMs);
            byte[] pin = Convert.FromHexString("808182838485868788898A8B8C8D8E8F909192939495969798999A9B9C9D9E9F");
            Assert.True(ConnectionTarget.TryCreate(Guid.NewGuid(), IPAddress.Loopback, 12345,
                Convert.ToHexString(pin), out ConnectionTarget? target));
            _target = target!;
            Assert.True(ConnectionIdentity.TryCreate(_target, pin, out ConnectionIdentity? identity));
            Ssl = new(_target.DeviceId, pin, _key, pending, hint, wrongProof, Clock, _beforeConstruction);
            Connection = new(identity!, _tcp, Ssl);
        }

        internal Task<AuthenticatedControlSession> Start()
        {
            Assert.Null(_original);
            ControlClientConnector connector = new((target, timeouts, clock, token) =>
            {
                _connectCalls++;
                Assert.Same(_target, target);
                Assert.Same(_timeouts, timeouts);
                Assert.Same(Clock, clock);
                Assert.Equal(_caller.Token, token);
                return Task.FromResult(Connection);
            }, session =>
            {
                // 来自生产 new 后的真实 observer，不以时钟访问次数代替构造事件。
                Constructions.Add(session);
                ConstructedAt = Clock.GetTimestamp();
                Clock.Events.Add("session-constructed");
                Clock.Advance(_observerCost);
            });
            _original = connector.ConnectAndAuthenticateAsync(_target, Ssl.ClientId, ScriptedSsl.ClientName,
                _key, SessionPermission.Control, new ControlClientAuthOptions
                {
                    MachineWindow = MachineBudget,
                    ApprovalWindow = ApprovalBudget,
                    ApprovalPending = notification =>
                    {
                        Assert.True(_pending);
                        Assert.Null(_notification);
                        _notification = notification;
                        _notificationBefore = Clock.GetTimestamp();
                        Assert.Equal(_notificationBefore, notification.AcceptedAtTimestamp);
                        Assert.Equal(PendingParseGap, Clock.GetElapsedTime(
                            Clock.CompletedAt["pending"], notification.AcceptedAtTimestamp));
                        Clock.AssertActiveTimer(ApprovalBudget, notification.AcceptedAtTimestamp);
                        Clock.Events.Add("notification-before");
                        Clock.Advance(NotificationCost);
                        _notificationAfter = Clock.GetTimestamp();
                        Assert.Equal(_notificationBefore, notification.AcceptedAtTimestamp);
                        Clock.Events.Add("notification-after");
                    },
                }, _timeouts, Clock, _caller.Token);
            return _original;
        }

        internal async Task ReleaseSuccessAsync(Task<AuthenticatedControlSession> original)
        {
            Assert.Same(_original, original);
            Task first = await Task.WhenAny(Ssl.SuccessPayloadEntered.Task, original).WaitAsync(Guard);
            if (ReferenceEquals(first, original))
            {
                // 提前认证失败时传播原故障，不等待读入口 Guard 超时掩盖它。
                _ = await original;
            }
            await Ssl.SuccessPayloadEntered.Task.WaitAsync(Guard);
            Assert.False(original.IsCompleted);
            Assert.Empty(Constructions);
            Assert.False(Clock.CompletedAt.ContainsKey("success"));
            Ssl.ReleaseSuccess();
        }

        internal void AssertPayloadTimeline()
        {
            long receivedAt = Clock.CompletedAt["success"];
            Assert.NotEqual(0, receivedAt);
            Assert.Equal(receivedAt, Clock.DisposedAt["success"]);
            Assert.Equal(SuccessReadCost, Clock.GetElapsedTime(Ssl.SuccessPayloadStartedAt, receivedAt));
            if (_pending)
            {
                Assert.NotNull(_notification);
                Assert.Equal(ApprovalBudget, _notification.ApprovalWindow);
                Assert.Equal(_notificationBefore, _notification.AcceptedAtTimestamp);
                Assert.Equal(NotificationCost, Clock.GetElapsedTime(_notificationBefore, _notificationAfter));
                Assert.Equal(PendingParseGap, Clock.GetElapsedTime(
                    Clock.CompletedAt["pending"], _notificationBefore));
                Assert.Equal(Clock.CompletedAt["pending"], Clock.DisposedAt["pending"]);
                Assert.Equal(NotificationCost + SuccessReadCost,
                    Clock.GetElapsedTime(_notification.AcceptedAtTimestamp, receivedAt));
                Assert.True(receivedAt > _notificationAfter);
                Assert.Equal(ApprovalBudget - NotificationCost, Clock.ApprovalPrefixBudget);
                Assert.Equal(_notificationAfter, Clock.ApprovalPrefixStartedAt);
            }
            else
            {
                Assert.Null(_notification);
                Assert.False(Clock.CompletedAt.ContainsKey("pending"));
            }
        }

        internal void AssertDelivered(Task<AuthenticatedControlSession> original, AuthenticatedControlSession session)
        {
            Assert.Same(_original, original);
            Assert.Equal(TaskStatus.RanToCompletion, original.Status);
            Assert.Same(session, original.GetAwaiter().GetResult());
            Assert.Same(session, Assert.Single(Constructions));
            Assert.Equal(1, _connectCalls);
            Assert.Equal(KeyHex, Convert.ToHexString(_key));
            Assert.Equal(_hint, session.VideoAttachExpiresInMsHint);
            AssertPayloadTimeline();
            Assert.Equal(_beforeConstruction, Clock.GetElapsedTime(Clock.CompletedAt["success"], ConstructedAt));
            Assert.Equal(_observerCost, Clock.GetElapsedTime(ConstructedAt, Clock.GetTimestamp()));
            Assert.Equal(_pending
                ? new[] { "pending-payload-completed", "pending-payload-disposed", "notification-before",
                    "notification-after", "success-payload-completed", "success-payload-disposed", "session-constructed" }
                : new[] { "success-payload-completed", "success-payload-disposed", "session-constructed" }, Clock.Events);
            if (_notification is not null)
            {
                Assert.Equal(session.SessionId, _notification.SessionId);
                Assert.Equal(session.ShortCode, _notification.ShortCode);
                Assert.True(Clock.GetElapsedTime(_notification.AcceptedAtTimestamp) < ApprovalBudget);
            }
            else
            {
                Assert.True(Clock.GetElapsedTime(EventClock.Origin) < MachineBudget);
            }
            Ssl.AssertConsumed(_pending);
            Assert.Equal(0, Clock.ActiveTimers);
            Assert.Equal(0, Clock.TimerCallbacks);
            AssertControlUsable(session);
        }

        internal void AssertControlUsable(AuthenticatedControlSession session)
        {
            Assert.Equal(SessionPermission.Control, session.GrantedPermission);
            Assert.Same(Ssl, session.Stream);
            Assert.False(Connection.IsCloseRequested);
            Assert.Equal(0, Ssl.DisposeCalls);
            byte[] nonce = Convert.FromHexString("202122232425262728292A2B2C2D2E2F");
            byte[] transcript = new byte[83];
            "LANREMOTE-VIDEO-V1\0"u8.CopyTo(transcript);
            Convert.FromHexString(Ssl.SessionId.ToString("N")).CopyTo(transcript, 19);
            nonce.CopyTo(transcript, 35);
            Ssl.Pin.CopyTo(transcript, 51);
            using HMACSHA256 hmac = new(Ssl.SessionToken);
            byte[] expected = hmac.ComputeHash(transcript);
            byte[]? actual = null;
            try
            {
                actual = session.CreateVideoAttachProof(nonce, Ssl.Pin);
                Assert.Equal(expected, actual);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expected);
                if (actual is not null) CryptographicOperations.ZeroMemory(actual);
            }
        }

        internal async Task FinishAsync()
        {
            // 先放行原 IO，再观察原 IO Task 和公开入口的原 Task；不拿 WaitAsync 代理状态代替它们。
            Ssl.ReleaseSuccess();
            _caller.Cancel();
            Task<Exception?> readObservation = ObserveAsync(Ssl.OriginalRead);
            Task<Exception?> authObservation = _original is null
                ? Task.FromResult<Exception?>(null) : ObserveAsync(_original);
            try
            {
                await Task.WhenAll(readObservation, authObservation).WaitAsync(Guard);
            }
            finally
            {
                foreach (AuthenticatedControlSession session in Constructions) session.RevokeForOwnerCleanup();
                if (_original is { IsCompletedSuccessfully: true }) _original.Result.RevokeForOwnerCleanup();
                try { await Connection.CloseAsync().WaitAsync(Guard); }
                finally
                {
                    _tcp.Dispose();
                    _caller.Dispose();
                    CryptographicOperations.ZeroMemory(_key);
                }
            }
        }
    }

    private sealed class EventClock : TimeProvider
    {
        internal const long Origin = 8_000_000_000;
        private readonly ManualDeadlineClock _inner = new();
        private readonly List<ObservedTimer> _timers = new();
        private long _offset;
        internal Dictionary<string, long> CompletedAt { get; } = new();
        internal Dictionary<string, long> DisposedAt { get; } = new();
        internal List<string> Events { get; } = new();
        internal int ActiveTimers => _inner.TimerCount;
        internal int TimerCallbacks { get; private set; }
        internal TimeSpan ApprovalPrefixBudget { get; private set; }
        internal long ApprovalPrefixStartedAt { get; private set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond * 2;
        public override long GetTimestamp() => Origin + _offset + _inner.GetTimestamp() * 2;
        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
        internal void Advance(TimeSpan elapsed) => _inner.Advance(elapsed, fireTimers: false);
        internal void AdvanceUtc(TimeSpan elapsed) => _inner.AdvanceUtc(elapsed);
        internal void SetTimestamp(long timestamp) => _offset = timestamp - Origin - _inner.GetTimestamp() * 2;

        internal void PayloadCompleted(string kind)
        {
            CompletedAt.Add(kind, GetTimestamp());
            Events.Add($"{kind}-payload-completed");
        }

        internal void WatchPayloadDeadline(string kind, TimeSpan afterSample)
        {
            // 在实际 payload ReadAsync 边界定位唯一活跃的 payload timer；前缀 timer 已释放。
            ObservedTimer timer = Assert.Single(_timers, candidate => !candidate.Disposed && candidate.Budget == PayloadBudget);
            Assert.Null(timer.AfterDispose);
            timer.AfterDispose = () =>
            {
                DisposedAt.Add(kind, GetTimestamp());
                Events.Add($"{kind}-payload-disposed");
                Advance(afterSample);
            };
        }

        internal void AssertActiveTimer(TimeSpan budget, long startedAt)
        {
            ObservedTimer timer = Assert.Single(_timers, candidate => !candidate.Disposed && candidate.Budget == budget);
            Assert.Equal(startedAt, timer.StartedAt);
        }

        internal void ObserveApprovalPrefix()
        {
            ObservedTimer prefix = Assert.Single(_timers, candidate => !candidate.Disposed && candidate.Budget != ApprovalBudget);
            ApprovalPrefixBudget = prefix.Budget;
            ApprovalPrefixStartedAt = prefix.StartedAt;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer inner = _inner.CreateTimer(value => { TimerCallbacks++; callback(value); }, state, dueTime, period);
            ObservedTimer timer = new(inner, dueTime, GetTimestamp());
            _timers.Add(timer);
            return timer;
        }
    }

    private sealed class ObservedTimer(ITimer inner, TimeSpan budget, long startedAt) : ITimer
    {
        internal TimeSpan Budget { get; } = budget;
        internal long StartedAt { get; } = startedAt;
        internal bool Disposed { get; private set; }
        internal Action? AfterDispose { get; set; }
        public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);
        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            inner.Dispose();
            AfterDispose?.Invoke();
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class ScriptedSsl : SslStream
    {
        internal const string ClientName = "成功帧时间戳测试客户端";
        private readonly Guid _serverId;
        private readonly byte[] _serverNonce = RandomNumberGenerator.GetBytes(32);
        private readonly byte[] _oracleKey;
        private readonly bool _pending;
        private readonly int _hint;
        private readonly bool _wrongProof;
        private readonly EventClock _clock;
        private readonly TimeSpan _beforeConstruction;
        private readonly Queue<(string Kind, byte[] Wire)> _input = new();
        private readonly List<string> _consumed = new();
        private readonly TaskCompletionSource _release = Signal();
        private int _offset;
        private int _writes;
        private int _flushes;
        private int _successPayloadReads;
        internal Guid ClientId { get; } = Guid.NewGuid();
        internal Guid SessionId { get; } = Guid.NewGuid();
        internal byte[] Pin { get; }
        internal byte[] SessionToken { get; } = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        internal TaskCompletionSource SuccessPayloadEntered { get; } = Signal();
        internal Task OriginalRead { get; private set; } = Task.CompletedTask;
        internal long SuccessPayloadStartedAt { get; private set; }
        internal int DisposeCalls { get; private set; }

        internal ScriptedSsl(Guid serverId, byte[] pin, byte[] key, bool pending, int hint, bool wrongProof,
            EventClock clock, TimeSpan beforeConstruction) : base(new MemoryStream(), false)
        {
            _serverId = serverId;
            Pin = pin;
            _oracleKey = key.ToArray();
            _pending = pending;
            _hint = hint;
            _wrongProof = wrongProof;
            _clock = clock;
            _beforeConstruction = beforeConstruction;
            Enqueue("challenge", JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "auth_challenge", protocol = 1, sessionId = SessionId.ToString("D"),
                serverDeviceId = _serverId.ToString("D"), serverNonce = Convert.ToBase64String(_serverNonce),
                certSha256 = Convert.ToHexString(Pin), expiresInMs = 15_000,
            }));
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal((uint)(buffer.Length - 4), BinaryPrimitives.ReadUInt32BigEndian(buffer.Span));
            ReadOnlyMemory<byte> payload = buffer[4..];
            if (++_writes == 1)
            {
                Assert.True(HelloFrame.TryParse(payload.Span, out _));
            }
            else
            {
                Assert.Equal(2, _writes);
                byte[] proof = VerifyResponseAndComputeServerProof(payload);
                try
                {
                    if (_wrongProof) proof[0] ^= 0x80;
                    if (_pending) Enqueue("pending", "{\"type\":\"approval_pending\"}"u8.ToArray());
                    Enqueue("success", JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        type = "auth_success", grantedPermission = "control", serverProof = Convert.ToBase64String(proof),
                        sessionToken = Convert.ToBase64String(SessionToken), videoAttachExpiresInMs = _hint,
                    }));
                }
                finally { CryptographicOperations.ZeroMemory(proof); }
            }
            return ValueTask.CompletedTask;
        }

        private byte[] VerifyResponseAndComputeServerProof(ReadOnlyMemory<byte> payload)
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            Assert.Equal(new[] { "clientDeviceId", "clientName", "clientNonce", "clientProof", "requestedPermission", "type" },
                root.EnumerateObject().Select(property => property.Name).Order().ToArray());
            Assert.Equal("auth_response", root.GetProperty("type").GetString());
            Assert.Equal(ClientId.ToString("D"), root.GetProperty("clientDeviceId").GetString());
            Assert.Equal(ClientName, root.GetProperty("clientName").GetString());
            Assert.Equal("control", root.GetProperty("requestedPermission").GetString());
            string nonce = root.GetProperty("clientNonce").GetString()!;
            byte[] decodedNonce = Convert.FromBase64String(nonce);
            Assert.Equal(32, decodedNonce.Length);
            Assert.Equal(Convert.ToBase64String(decodedNonce), nonce);
            // 独立 HMAC oracle 读取真实随机 clientNonce；不调用生产 transcript/proof helper。
            byte[] transcript = Encoding.UTF8.GetBytes(string.Join('\0', "LANREMOTE-AUTH-V1",
                SessionId.ToString("D"), _serverId.ToString("D"), ClientId.ToString("D"),
                Convert.ToBase64String(_serverNonce), nonce, Convert.ToHexString(Pin), "control"));
            byte[] grant = Encoding.UTF8.GetBytes(string.Join('\0', "server", "LANREMOTE-GRANT-V1",
                Convert.ToHexString(SHA256.HashData(transcript)), "control"));
            using HMACSHA256 hmac = new(_oracleKey);
            try
            {
                Assert.Equal(hmac.ComputeHash(transcript), Convert.FromBase64String(root.GetProperty("clientProof").GetString()!));
                return hmac.ComputeHash(grant);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(transcript);
                CryptographicOperations.ZeroMemory(grant);
            }
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _flushes++;
            return Task.CompletedTask;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = _input.Peek();
            if (_pending && frame.Kind == "success" && _offset == 0) _clock.ObserveApprovalPrefix();
            if (_offset == 4 && frame.Kind is "pending" or "success")
            {
                _clock.WatchPayloadDeadline(frame.Kind, frame.Kind == "pending" ? PendingParseGap : _beforeConstruction);
                if (frame.Kind == "success")
                {
                    SuccessPayloadStartedAt = _clock.GetTimestamp();
                    Task<int> read = ReadFirstSuccessChunkAsync(buffer, cancellationToken);
                    OriginalRead = read;
                    SuccessPayloadEntered.TrySetResult();
                    return new(read);
                }
            }
            return ValueTask.FromResult(CopyInput(buffer));
        }

        private async Task<int> ReadFirstSuccessChunkAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return CopyInput(buffer);
        }

        private int CopyInput(Memory<byte> buffer)
        {
            var frame = _input.Peek();
            int count = Math.Min(buffer.Length, frame.Wire.Length - _offset);
            if (frame.Kind == "success" && _offset >= 4)
            {
                _successPayloadReads++;
                if (_offset == 4) count--; // 保留最后一字节，起点不能偷换为首片到达。
                else _clock.Advance(SuccessReadCost);
            }
            frame.Wire.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            if (_offset == frame.Wire.Length)
            {
                if (frame.Kind == "challenge") _clock.Advance(TimeSpan.FromMilliseconds(100));
                if (frame.Kind == "pending") _clock.Advance(TimeSpan.FromMilliseconds(120));
                if (frame.Kind is "pending" or "success") _clock.PayloadCompleted(frame.Kind);
                _input.Dequeue();
                _consumed.Add(frame.Kind);
                _offset = 0;
            }
            return count;
        }

        private void Enqueue(string kind, byte[] payload)
        {
            byte[] wire = new byte[4 + payload.Length];
            BinaryPrimitives.WriteUInt32BigEndian(wire, (uint)payload.Length);
            payload.CopyTo(wire, 4);
            _input.Enqueue((kind, wire));
        }

        internal void AssertConsumed(bool pending)
        {
            Assert.Equal(2, _writes);
            Assert.Equal(2, _flushes);
            Assert.Equal(2, _successPayloadReads);
            Assert.Equal(pending ? new[] { "challenge", "pending", "success" } : new[] { "challenge", "success" }, _consumed);
            Assert.Empty(_input);
            Assert.True(OriginalRead.IsCompletedSuccessfully);
        }

        internal void ReleaseSuccess() => _release.TrySetResult();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCalls++;
                CryptographicOperations.ZeroMemory(_oracleKey);
                CryptographicOperations.ZeroMemory(SessionToken);
            }
            base.Dispose(disposing);
        }
    }
}
