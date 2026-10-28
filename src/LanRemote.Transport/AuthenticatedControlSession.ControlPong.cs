namespace LanRemote.Transport;

public sealed partial class AuthenticatedControlSession
{
    private readonly ClientControlWriteSlot _controlWriteSlot = new();
    private ClientControlPongState? _controlPong;

    /// <summary>Control 方向唯一写槽；pong 可跳过，唯一切屏请求可等待当前 pong 原写入退出。</summary>
    private sealed class ClientControlWriteSlot
    {
        private bool _busy;
        private TaskCompletionSource _released = CompletedRelease();
        internal bool TryEnterUnderGate()
        {
            if (_busy) return false;
            _busy = true;
            return true;
        }
        internal Task ReleaseWaitUnderGate => _busy ? _released.Task : Task.CompletedTask;
        internal void ExitUnderGate()
        {
            if (!_busy) throw new InvalidOperationException("Control 写槽重复释放。");
            _busy = false;
            TaskCompletionSource released = _released;
            _released = CompletedRelease();
            released.TrySetResult();
        }
        private static TaskCompletionSource CompletedRelease()
        {
            TaskCompletionSource source = new(TaskCreationOptions.RunContinuationsAsynchronously);
            source.TrySetResult();
            return source;
        }
    }

    /// <summary>无队列 pong 单槽；与切屏请求共用写槽和停止/收尾证据。</summary>
    private sealed class ClientControlPongState(AuthenticatedControlSession parent)
    {
        private static readonly TimeSpan WriteBudget = TimeSpan.FromSeconds(10);
        private readonly TaskCompletionSource _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<Exception> _errors = [];
        private HostDisplayCatalogSendAttempt? _attempt;
        private Task<HostDisplayCatalogSendAttempt.SendOutcome>? _original;
        private Task? _active;
        private Exception? _error;
        private long _nextSequence;
        private bool _sequenceExhausted;
        private bool _stopped;
        private bool _writeSlotHeld;

        internal Task Completion { get { lock (parent._gate) return _active ?? Task.CompletedTask; } }
        internal Exception? Error { get { lock (parent._gate) return _error; } }
        internal bool AllOwnedRootsReleased
        {
            get
            {
                lock (parent._gate)
                    return !_writeSlotHeld && (_attempt is null ||
                        (_active is { IsCompleted: true } && (_original is null || _attempt.Snapshot.AllOwnedRootsReleased)));
            }
        }

        // 仅在父 gate 内调用；不在锁内取时、序列化或执行网络 I/O。
        internal bool TryStartUnderGate(PingFrame ping)
        {
            if (_stopped || _active is { IsCompleted: false } || parent._connection is null || !parent._deliveryCommitted)
                return false;
            if (ping.SessionId != parent.SessionId) throw new FrameProtocolException("control-ping-session-mismatch");
            if (_sequenceExhausted) throw new InvalidOperationException("pong 序号已经耗尽。");
            if (!parent._controlWriteSlot.TryEnterUnderGate()) return false; // 切屏或上一 pong 优先，本轮跳过。
            long sequence = _nextSequence;
            if (_nextSequence == long.MaxValue) _sequenceExhausted = true;
            else _nextSequence++;
            _writeSlotHeld = true;
            _attempt = null;
            _original = null;
            _active = Task.Run(() => RunAsync(ping, sequence));
            return true;
        }

        internal void RevokeUnderGate()
        {
            _stopped = true;
            _stop.TrySetResult();
            _attempt?.RequestStop();
        }

        private async Task RunAsync(PingFrame ping, long sequence)
        {
            HostDisplayCatalogSendAttempt? attempt = null;
            Task<HostDisplayCatalogSendAttempt.SendOutcome>? original = null;
            bool originalObserved = false;
            bool sent = false;
            try
            {
                Stream stream;
                lock (parent._gate)
                {
                    if (_stopped || parent._connection is null || !parent._deliveryCommitted) return;
                    stream = parent._ownedConnection.Stream;
                }
                attempt = HostDisplayCatalogSendAttempt.CreatePayload(stream, () =>
                {
                    PongFrame pong = new(parent.SessionId, sequence,
                        parent._clock.GetUtcNow().ToUnixTimeMilliseconds(), ping.Sequence);
                    return new ValueTask<byte[]>(pong.Serialize());
                }, PongFrame.MaxPayloadByteLength, parent._clock, WriteBudget);
                lock (parent._gate)
                {
                    if (_stopped || parent._connection is null || !parent._deliveryCommitted) return;
                    _attempt = attempt;
                    original = _original = attempt.Start();
                }
                await Task.WhenAny(original, attempt.AbortRequired, _stop.Task).ConfigureAwait(false);
                if (attempt.AbortRequired.IsCompleted || _stop.Task.IsCompleted) Stop();
                HostDisplayCatalogSendAttempt.SendOutcome outcome;
                try { outcome = await original.ConfigureAwait(false); }
                finally { originalObserved = true; }
                HostDisplayCatalogSendAttempt.AttemptSnapshot snapshot = attempt.Snapshot;
                if (outcome.End != HostDisplayCatalogSendAttempt.AttemptEnd.Sent || !snapshot.AllOwnedRootsReleased)
                {
                    Stop();
                    return;
                }
                sent = true;
            }
            catch (Exception caught)
            {
                RecordError(OriginalError(originalObserved ? original : null, caught));
                Stop();
            }
            finally
            {
                if (original is not null && !originalObserved)
                {
                    if (!sent) Stop();
                    try { await original.ConfigureAwait(false); }
                    catch (Exception caught) { RecordError(OriginalError(original, caught)); }
                }
                if (attempt is not null && original is not null) RecordAttemptErrors(attempt.Snapshot);
                lock (parent._gate) ReleaseWriteSlotUnderGate();
            }
        }

        private void ReleaseWriteSlotUnderGate()
        {
            if (!_writeSlotHeld) return;
            _writeSlotHeld = false;
            parent._controlWriteSlot.ExitUnderGate();
        }

        private void Stop()
        {
            lock (parent._gate) RevokeUnderGate();
            try { parent.StopDisplaySwitch(); }
            catch (Exception error) { RecordError(error); }
        }

        private void RecordAttemptErrors(HostDisplayCatalogSendAttempt.AttemptSnapshot snapshot)
        {
            foreach (Exception? error in new[] { snapshot.OperationError, snapshot.ObservationError,
                snapshot.FinalSampleError, snapshot.CoordinationError, snapshot.Budget?.Error, snapshot.Budget?.CleanupError })
                if (error is not null) RecordError(error);
        }

        private void RecordError(Exception error)
        {
            lock (parent._gate)
            {
                _errors.Add(error);
                _error = _errors.Count == 1 ? error : new AggregateException("pong 发送或收尾失败。", _errors);
            }
        }

        private static Exception OriginalError(Task? original, Exception caught)
        {
            AggregateException? container = original?.Exception;
            return container is null ? caught : container.InnerExceptions.Count == 1 ? container.InnerExceptions[0] : container;
        }
    }
}
