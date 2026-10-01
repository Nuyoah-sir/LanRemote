using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

public sealed partial class FirstFrameRouterBoundaryTests
{
    // 真实时间只作卡死保护，所有合同内的截止/重试时序均由 ManualDeadlineClock 驱动。
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);
    private static readonly Guid SessionId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly IPAddress LocalAddress = IPAddress.Parse("192.168.10.10");
    private static readonly IPAddress RemoteAddress = IPAddress.Parse("192.168.10.20");
    private static readonly byte[] Token = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
    private static readonly byte[] Pin = Enumerable.Range(64, 32).Select(value => (byte)value).ToArray();
    private static readonly byte[] Nonce = Enumerable.Range(128, 16).Select(value => (byte)value).ToArray();

    private static TransportTimeouts NewTimeouts(int prefixMs = 1000, int payloadMs = 1000, int envelopeMs = 5000) =>
        new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(prefixMs),
            TimeSpan.FromMilliseconds(payloadMs), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(envelopeMs));

    private static ConnectionSecurityContext Security() =>
        new(LocalAddress, RemoteAddress, 23456, SslProtocols.Tls13, Pin);

    // 复用 Registry 测试的独立 transcript 模式，不调用生产 VideoAttachProof。
    private static byte[] Proof(byte[] nonce)
    {
        byte[] transcript = new byte[83];
        Encoding.UTF8.GetBytes("LANREMOTE-VIDEO-V1\0").CopyTo(transcript, 0);
        Convert.FromHexString(SessionId.ToString("N")).CopyTo(transcript, 19);
        nonce.CopyTo(transcript, 35);
        Pin.CopyTo(transcript, 51);
        using HMACSHA256 hmac = new(Token);
        return hmac.ComputeHash(transcript);
    }

    private static byte[] HelloWire(bool badProof)
    {
        byte[] proof = Proof(Nonce);
        if (badProof) proof[0] ^= 1;
        byte[] payload = new VideoHelloFrame(SessionId, Nonce, proof).Serialize();
        byte[] wire = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(wire, (uint)payload.Length);
        payload.CopyTo(wire, 4);
        return wire;
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class BoundaryScenario : IAsyncDisposable
    {
        private readonly ConnectionCloseHandle _closer;
        private Task? _run;

        internal BoundaryScenario(byte[]? wire = null, bool badProof = false, int holdRead = 0,
            bool holdWrite = false, bool holdFlush = false, bool failWrite = false, bool failFlush = false,
            TransportTimeouts? timeouts = null, bool cancelAwareRead = false,
            IVideoFrameProducerFactory? producerFactory = null)
        {
            Stream = new ControlledSslStream(wire ?? HelloWire(badProof), holdRead,
                holdWrite, holdFlush, failWrite, failFlush, cancelAwareRead);
            // 按 Host 内部真实顺序绑定同一 SSL/安全上下文；不是 public record 或伪造关闭权限。
            // MemoryStream 仅替代 socket 资源，SSL Dispose 仍由真实 CloseHandle 执行并 join。
            _closer = new ConnectionCloseHandle(new MemoryStream(), new HostLifecycleErrors());
            _closer.Attach(Stream);
            Connection = new AcceptedConnection(Security(), Stream, _closer);
            Source = new EofSource(Stream);
            UnexpectedControlServices control = new();
            ControlAuthContext context = new()
            {
                ServerDeviceId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
                AccessSecretStore = control,
                FailedAuthLimiter = new FailedAuthLimiter(),
                PendingApprovalLimiter = new ConnectionAdmissionLimiter(3, 1),
                ApprovalGate = control,
                SessionRegistry = Registry,
                TimeProvider = Clock
            };
            Router = producerFactory is null
                ? new FirstFrameRouter(context, timeouts ?? NewTimeouts(), Source)
                : new FirstFrameRouter(context, timeouts ?? NewTimeouts(), producerFactory);
        }

        internal ObservedClock Clock { get; } = new();
        internal SessionRegistry Registry { get; } = new();
        internal CancellationTokenSource HostStop { get; } = new();
        internal ControlledSslStream Stream { get; }
        internal AcceptedConnection Connection { get; }
        internal FirstFrameRouter Router { get; }
        internal EofSource Source { get; }

        internal Task Start()
        {
            Assert.Null(_run);
            Assert.True(Connection.HasCloseAuthority);
            return _run = Router.RunAsync(Connection, HostStop.Token);
        }

        internal SessionRegistry.SessionRegistration Register(TimeProvider? windowClock = null,
            CancellationToken controlToken = default, Guid? connectionId = null, int expiresInMs = 15_000) =>
            Registry.Register(SessionId, connectionId ?? Guid.NewGuid(),
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), "首帧路由边界测试",
                SessionPermission.ViewOnly, RemoteAddress, 12345, Token,
                new VideoAttachWindow(windowClock ?? Clock.Manual, expiresInMs), Pin, controlToken);

        internal VideoAttachStatus ProbeAttach(bool freshNonce = false)
        {
            byte[] nonce = Nonce.ToArray();
            if (freshNonce) nonce[0] ^= 1;
            return Registry.TryAttachVideo(SessionId, Security(), nonce, Proof(nonce), default, out _);
        }

        public async ValueTask DisposeAsync()
        {
            // await using 即调用方的 finally；必须先放行全部 gate，失败断言也不能遗留等待者。
            Stream.ReleaseAll();
            try
            {
                await HostStop.CancelAsync();
            }
            finally
            {
                try
                {
                    await _closer.CloseAsync().WaitAsync(Guard);
                }
                finally
                {
                    try
                    {
                        if (_run is not null)
                        {
                            try { await _run.WaitAsync(Guard); }
                            catch (OperationCanceledException) when (_run.IsCanceled && HostStop.IsCancellationRequested) { }
                        }
                    }
                    finally
                    {
                        try { await Stream.JoinIOAsync(); }
                        finally { HostStop.Dispose(); }
                    }
                }
            }
            Assert.Empty(_closer.CleanupErrors);
        }
    }

    private sealed class ObservedClock : TimeProvider
    {
        private readonly Channel<TimerNotice> _created = Channel.CreateUnbounded<TimerNotice>();
        internal ManualDeadlineClock Manual { get; } = new();
        public override long TimestampFrequency => Manual.TimestampFrequency;
        public override long GetTimestamp() => Manual.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => Manual.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            long startedAt = GetTimestamp();
            ITimer timer = Manual.CreateTimer(callback, state, dueTime, period);
            _created.Writer.TryWrite(new TimerNotice(Manual, startedAt, dueTime, () => callback(state), state));
            return timer;
        }

        internal async Task<TimerNotice> ReadTimerAsync()
        {
            using CancellationTokenSource guard = new(Guard);
            return await _created.Reader.ReadAsync(guard.Token);
        }

        internal async Task<TimerNotice> ReadFirstRetryAsync()
        {
            // 首帧两段完成后才建立 registration 窗口和首个 retry，不按线程调度猜测进度。
            Assert.Equal(TimeSpan.FromSeconds(1), (await ReadTimerAsync()).DueTime);
            Assert.Equal(TimeSpan.FromSeconds(1), (await ReadTimerAsync()).DueTime);
            Assert.Equal(TimeSpan.FromMilliseconds(100), (await ReadTimerAsync()).DueTime);
            TimerNotice retry = await ReadTimerAsync();
            Assert.Equal(TimeSpan.FromMilliseconds(40), retry.DueTime);
            return retry;
        }
    }

    private sealed class TimerNotice(
        ManualDeadlineClock clock, long startedAt, TimeSpan dueTime, Action callback, object? state)
    {
        private int _dispatched;
        internal long StartedAt { get; } = startedAt;
        internal TimeSpan DueTime { get; } = dueTime;
        // 只观测传给 TimeProvider 的原始 state，不反射或替换 deadline/Task.Delay。
        internal object? State { get; } = state;

        internal void Dispatch()
        {
            Assert.True(clock.GetElapsedTime(StartedAt) >= DueTime, "只能派发已经到期的 timer 通知。");
            Assert.Equal(0, Interlocked.Exchange(ref _dispatched, 1));
            // 仅派发捕获的公开 TimeProvider 回调；允许 deadline 通知仍排队，验证迟到通知不能续命。
            // 测试不再调用 FireTimers；Task.Delay 的完成路径会释放原 ManualTimer。
            callback();
        }
    }

    // 与 VideoAttachRegistryTests 一样，仅为 registry window 注入取时重入；不数 Router 私有取时次数。
    private sealed class WindowSampleClock(ManualDeadlineClock inner) : TimeProvider
    {
        internal int TimestampReads { get; private set; }
        internal Action<int>? AfterSample { get; set; }
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
        public override long GetTimestamp()
        {
            long sample = inner.GetTimestamp();
            int read = ++TimestampReads;
            AfterSample?.Invoke(read);
            return sample;
        }
    }

    private sealed class ControlledSslStream : SslStream
    {
        private readonly byte[] _input;
        private readonly int _holdRead;
        private readonly bool _cancelAwareRead;
        private readonly bool _failWrite;
        private readonly bool _failFlush;
        private readonly TaskCompletionSource _readRelease = Signal();
        private readonly TaskCompletionSource _writeRelease = Signal();
        private readonly TaskCompletionSource _flushRelease = Signal();
        private readonly ConcurrentQueue<Task> _operations = new();
        private int _offset;
        private int _readCalls;
        private int _disposeCount;
        private int _writeCompleted;
        private int _flushCompleted;
        private int _peerReadCompleted;

        internal ControlledSslStream(byte[] input, int holdRead, bool holdWrite, bool holdFlush,
            bool failWrite, bool failFlush, bool cancelAwareRead) : base(new MemoryStream())
        {
            _input = input;
            _holdRead = holdRead;
            _cancelAwareRead = cancelAwareRead;
            _failWrite = failWrite;
            _failFlush = failFlush;
            if (holdRead == 0) ReleaseRead();
            if (!holdWrite) ReleaseWrite();
            if (!holdFlush) ReleaseFlush();
        }

        internal TaskCompletionSource ReadEntered { get; } = Signal();
        internal CancellationToken HeldReadToken { get; private set; }
        internal TaskCompletionSource<OperationCanceledException> ReadCancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource WriteEntered { get; } = Signal();
        internal TaskCompletionSource FlushEntered { get; } = Signal();
        internal TaskCompletionSource Closed { get; } = Signal();
        internal ConcurrentQueue<byte[]> Writes { get; } = new();
        internal Action<int>? BeforeReadCompletes { get; set; }
        internal int ReadCalls => Volatile.Read(ref _readCalls);
        internal int BytesRead => Volatile.Read(ref _offset);
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        internal bool WriteCompleted => Volatile.Read(ref _writeCompleted) != 0;
        internal bool FlushCompleted => Volatile.Read(ref _flushCompleted) != 0;
        internal bool PeerReadCompleted => Volatile.Read(ref _peerReadCompleted) != 0;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Task<int> task = ReadCoreAsync(buffer, cancellationToken);
            _operations.Enqueue(task);
            return new ValueTask<int>(task);
        }

        private async Task<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = Interlocked.Increment(ref _readCalls);
            if (read == _holdRead)
            {
                HeldReadToken = cancellationToken;
                // 默认仍允许取消后读成功；新回归显式开启真实的可取消等待，并在注册后通知已进入。
                Task wait = _cancelAwareRead
                    ? _readRelease.Task.WaitAsync(cancellationToken)
                    : _readRelease.Task;
                ReadEntered.TrySetResult();
                try
                {
                    await wait.ConfigureAwait(false);
                }
                catch (OperationCanceledException error)
                {
                    ReadCancelled.TrySetResult(error);
                    throw;
                }
            }
            if (_offset < _input.Length)
            {
                int length = Math.Min(buffer.Length, _input.Length - _offset);
                _input.AsMemory(_offset, length).CopyTo(buffer);
                _offset += length;
                BeforeReadCompletes?.Invoke(read);
                return length;
            }

            try
            {
                // 首帧后仅留一个下行通道的 peer reader，直到真实关闭句柄 Dispose 本流或取消。
                await Closed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return 0;
            }
            finally
            {
                Volatile.Write(ref _peerReadCompleted, 1);
            }
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Task task = WriteCoreAsync(buffer, cancellationToken);
            _operations.Enqueue(task);
            return new ValueTask(task);
        }

        private async Task WriteCoreAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes.Enqueue(buffer.ToArray());
            WriteEntered.TrySetResult();
            // 故意允许取消/Dispose 后才返回成功，检验 Router 的写后检查和 join，不靠测试流代劳。
            await _writeRelease.Task.ConfigureAwait(false);
            if (_failWrite) throw new IOException("测试 ACK 写入失败。");
            Volatile.Write(ref _writeCompleted, 1);
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Task task = FlushCoreAsync(cancellationToken);
            _operations.Enqueue(task);
            return task;
        }

        private async Task FlushCoreAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FlushEntered.TrySetResult();
            await _flushRelease.Task.ConfigureAwait(false);
            if (_failFlush) throw new IOException("测试 ACK flush 失败。");
            Volatile.Write(ref _flushCompleted, 1);
        }

        internal void ReleaseRead() => _readRelease.TrySetResult();
        internal void ReleaseWrite() => _writeRelease.TrySetResult();
        internal void ReleaseFlush() => _flushRelease.TrySetResult();
        internal void ReleaseAll()
        {
            ReleaseRead();
            ReleaseWrite();
            ReleaseFlush();
        }

        internal async Task JoinIOAsync()
        {
            foreach (Task operation in _operations)
            {
                try { await operation.WaitAsync(Guard); }
                catch (Exception error) when (operation.IsCompleted &&
                    error is OperationCanceledException or IOException or ObjectDisposedException) { }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Interlocked.Increment(ref _disposeCount);
            try { base.Dispose(disposing); }
            finally { if (disposing) Closed.TrySetResult(); }
        }
    }

    private sealed class EofSource(ControlledSslStream stream) : IVideoFrameSource
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        internal bool SawCompletedFlush { get; private set; }
        internal Guid SessionId { get; private set; }

        public ValueTask<EncodedFrame?> ReadNextAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            SawCompletedFlush = stream.FlushCompleted;
            SessionId = sessionId;
            Interlocked.Increment(ref _calls);
            return ValueTask.FromResult<EncodedFrame?>(null);
        }
    }

    private sealed class UnexpectedControlServices : IAccessSecretStore, ILocalApprovalGate
    {
        public Task<AccessSecret> LoadOrCreateAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("视频首帧不得加载 Control 密钥。");
        public Task<AccessSecret> RegenerateAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("视频首帧不得重建 Control 密钥。");
        public ValueTask<LocalApprovalDecision> RequestApprovalAsync(
            LocalApprovalRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("视频首帧不得请求 Control 审批。");
    }
}
