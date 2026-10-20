using LanRemote.Core.Models;

namespace LanRemote.Transport;

public sealed partial class AuthenticatedControlSession
{
    private ClientDisplaySwitchState? _displaySwitch;

    /// <summary>只在最终交付后、视频附着与唯一 Control 读者启动前选择切屏模式。</summary>
    internal void EnableDisplaySwitching()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_connection is null, this);
            if (!_deliveryCommitted || _videoLifetime is not null || _controlMonitor is not null || _displaySwitch is not null)
                throw new InvalidOperationException("显示切换必须在已交付会话启动视频及监控之前唯一启用。");
            _displaySwitch = new ClientDisplaySwitchState(this);
        }
    }

    /// <summary>登记真实请求并返回覆盖发送、回执等待及注册清理的原 worker；不创建额外读者。</summary>
    internal Task<DisplayChangedFrame?> RequestDisplayAsync(DisplayId displayId, CancellationToken token = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_connection is null, this);
            if (!_deliveryCommitted) throw new InvalidOperationException("控制认证尚未完成最终交付。");
            ClientDisplaySwitchState state = _displaySwitch
                ?? throw new InvalidOperationException("本会话未启用显示切换。");
            if (_videoLifetime is null || !_videoLifetime.CanSwitchUnderGate)
                throw new InvalidOperationException("公开视频会话当前不可用于显示切换。");
            if (_controlMonitor is null || _controlMonitor.Worker.IsCompleted)
                throw new InvalidOperationException("显示切换需要仍在运行的唯一 Control 读者。");
            DisplayCatalogFrame catalog = _latestDisplayCatalog
                ?? throw new InvalidOperationException("尚未收到显示目录。");
            return state.StartUnderGate(catalog, displayId, token);
        }
    }

    /// <summary>当前有效会话的实际回执下界；不授予视频或 UI 资格，已返回对象仅是历史。</summary>
    internal DisplayChangedFrame? LatestDisplayChange
    {
        get
        {
            lock (_gate)
                return _connection is not null && _deliveryCommitted ? _displaySwitch?.ReceiptUnderGate : null;
        }
    }

    private void StopDisplaySwitch()
    {
        _ = RevokeCore();
        // 两项停止独立请求；不能等待包含当前切屏 worker 的父 join。
        List<Exception> errors = [];
        try { _ = _ownedConnection.CloseAsync(); }
        catch (Exception error) { errors.Add(error); }
        try { _videoLifetime?.RequestStop(); }
        catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("显示切换请求关闭连接时失败。", errors);
    }

    /// <summary>只拥有一个请求槽；与父共用 gate，构造不取时、不执行 I/O 或外部回调。</summary>
    private sealed class ClientDisplaySwitchState(AuthenticatedControlSession parent)
    {
        private static readonly TimeSpan WriteBudget = TimeSpan.FromSeconds(10);
        private readonly TaskCompletionSource _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<Exception> _errors = [];
        private PendingRequest? _pending;
        private DisplayCatalogFrame? _frozenCatalog;
        private DisplayChangedFrame? _receipt;
        private HostDisplayCatalogSendAttempt? _attempt;
        private Task<HostDisplayCatalogSendAttempt.SendOutcome>? _original;
        private Task? _active;
        private Exception? _error;
        private long _nextSequence;
        private long _lastHostSeq = -1;
        private bool _sequenceExhausted;
        private bool _stopped;
        private bool _registrationReleased = true;

        internal Task Completion { get { lock (parent._gate) return _active ?? Task.CompletedTask; } }
        internal Exception? Error { get { lock (parent._gate) return _error; } }
        internal bool AllOwnedRootsReleased
        {
            get
            {
                lock (parent._gate)
                {
                    // 未创建发送 attempt 时没有发送根；已有 caller 注册仍须证明已排空。
                    if (_attempt is null) return _registrationReleased;
                    return _active is { IsCompleted: true } && _registrationReleased &&
                        (_original is null || _attempt.Snapshot.AllOwnedRootsReleased);
                }
            }
        }

        internal bool CanBindPresentationUnderGate => !_stopped && _active is null;
        internal bool HasPendingRequestUnderGate => !_stopped && _pending is not null && _receipt is null;

        internal DisplayChangedFrame? ReceiptUnderGate =>
            parent._connection is not null && parent._deliveryCommitted && !_stopped ? _receipt : null;

        internal Task<DisplayChangedFrame?> StartUnderGate(DisplayCatalogFrame catalog, DisplayId id, CancellationToken caller)
        {
            ObjectDisposedException.ThrowIf(_stopped || parent._connection is null, parent);
            if (_active is { IsCompleted: false })
                throw new InvalidOperationException("显示切换不允许重叠请求。");
            caller.ThrowIfCancellationRequested();
            if (!ReferenceEquals(parent._latestDisplayCatalog, catalog))
                throw new InvalidOperationException("显示切换必须引用当前实际目录实例。");
            bool found = false;
            foreach (DisplayInfo display in catalog.Displays)
                if (display.Id == id) { found = true; break; }
            if (!found) throw new ArgumentOutOfRangeException(nameof(id), "目标不在当前显示目录中。");
            if (_sequenceExhausted) throw new InvalidOperationException("显示切换请求序号已经耗尽。");

            PendingRequest pending = new(catalog, id, _nextSequence);
            if (_nextSequence == long.MaxValue) _sequenceExhausted = true;
            else _nextSequence++;
            _pending = pending;
            _frozenCatalog = catalog;
            _receipt = null; // 新请求接纳即隐藏旧 fence，不等待新发送开始。
            _attempt = null;
            _original = null;
            parent._displayPresentation?.BeginRequestUnderGate();
            Task<DisplayChangedFrame?> worker = Task.Run(() => RunAsync(pending, caller));
            _active = worker;
            return worker;
        }

        internal void AcceptUnderGate(DisplayChangedFrame reply)
        {
            PendingRequest? pending = _pending;
            if (pending is null || !IsCurrentUnderGate(pending) || _active is not { IsCompleted: false } ||
                pending.Received.Task.IsCompleted || reply.SessionId != parent.SessionId ||
                reply.RequestSequence != pending.Sequence || reply.CatalogSequence != pending.Catalog.Sequence ||
                reply.DisplayId != pending.Id || reply.Sequence <= pending.Catalog.Sequence || reply.Sequence <= _lastHostSeq)
                throw RejectUnderGate("display-switch-receipt-invalid");

            _lastHostSeq = reply.Sequence;
            // TCS 就是对应请求的唯一 earlyAck 槽；读者不等待发送，也不在这里发布 fence。
            pending.Received.TrySetResult(reply);
        }

        internal void CatalogUnderGate(DisplayCatalogFrame catalog)
        {
            if ((_pending is not null || _receipt is not null) && !ReferenceEquals(catalog, _frozenCatalog))
                throw RejectUnderGate("display-switch-catalog-changed");
        }

        internal void RevokeUnderGate()
        {
            _stopped = true;
            _receipt = null;
            parent._displayPresentation?.RevokeUnderGate();
            _stop.TrySetResult();
            _attempt?.RequestStop();
        }

        private FrameProtocolException RejectUnderGate(string reason)
        {
            // Monitor 负责记录协议错误及锁外关线；先令本槽失效，避免其退出前再发布旧 fence。
            RevokeUnderGate();
            return new FrameProtocolException(reason);
        }

        private bool IsCurrentUnderGate(PendingRequest pending) =>
            !_stopped && parent._connection is not null && parent._deliveryCommitted &&
            ReferenceEquals(parent._displaySwitch, this) && ReferenceEquals(_pending, pending) &&
            ReferenceEquals(_frozenCatalog, pending.Catalog) && ReferenceEquals(parent._latestDisplayCatalog, pending.Catalog) &&
            parent._videoLifetime is { CanSwitchUnderGate: true } &&
            parent._controlMonitor is { Worker: { IsCompleted: false } };

        private DisplayChangedFrame? CandidateUnderGate(PendingRequest pending, CancellationToken caller)
        {
            if (!IsCurrentUnderGate(pending) || caller.IsCancellationRequested || !pending.Received.Task.IsCompletedSuccessfully)
                return null;
            // 仅由已完整 join 且成功的发送路径调用；已完成的 TCS 不会阻塞或执行外部代码。
            return pending.Received.Task.GetAwaiter().GetResult(); // 清理完成前仅候选，不对外发布。
        }

        private async Task<DisplayChangedFrame?> RunAsync(PendingRequest pending, CancellationToken caller)
        {
            CancellationTokenRegistration registration = default;
            HostDisplayCatalogSendAttempt? attempt = null;
            Task<HostDisplayCatalogSendAttempt.SendOutcome>? original = null;
            bool originalObserved = false;
            DisplayChangedFrame? result = null;
            try
            {
                Stream stream;
                lock (parent._gate)
                {
                    // 原 worker 的发布屏障；停止先赢时不注册新回调、不创建或启动发送。
                    if (!IsCurrentUnderGate(pending)) return null;
                    stream = parent._ownedConnection.Stream;
                    _registrationReleased = false;
                }
                registration = caller.Register(static state => ((ClientDisplaySwitchState)state!).Stop(), this);
                // 构造在父锁外且不执行 source；UTC 取样和序列化均在原 attempt 的预算内发生。
                attempt = HostDisplayCatalogSendAttempt.CreatePayload(stream, () =>
                {
                    long requestTime = parent._clock.GetUtcNow().ToUnixTimeMilliseconds();
                    DisplaySwitchRequestFrame request = new(parent.SessionId, pending.Sequence, requestTime,
                        pending.Catalog.Sequence, pending.Id);
                    return new ValueTask<byte[]>(request.Serialize());
                }, DisplaySwitchRequestFrame.MaxPayloadByteLength, parent._clock, WriteBudget);
                lock (parent._gate)
                {
                    if (!IsCurrentUnderGate(pending) || caller.IsCancellationRequested) return null;
                    _attempt = attempt;
                    original = _original = attempt.Start();
                }

                await Task.WhenAny(original, attempt.AbortRequired, _stop.Task).ConfigureAwait(false);
                if (attempt.AbortRequired.IsCompleted || _stop.Task.IsCompleted || caller.IsCancellationRequested) Stop();
                HostDisplayCatalogSendAttempt.SendOutcome outcome;
                try { outcome = await original.ConfigureAwait(false); }
                finally { originalObserved = true; }
                HostDisplayCatalogSendAttempt.AttemptSnapshot snapshot = attempt.Snapshot;
                if (outcome.End != HostDisplayCatalogSendAttempt.AttemptEnd.Sent || !snapshot.AllOwnedRootsReleased)
                {
                    Stop();
                    return null;
                }

                bool current;
                lock (parent._gate)
                {
                    current = IsCurrentUnderGate(pending) && !caller.IsCancellationRequested;
                    if (current) result = CandidateUnderGate(pending, caller);
                }
                if (!current) { Stop(); return null; }
                if (result is null)
                {
                    await Task.WhenAny(pending.Received.Task, _stop.Task).ConfigureAwait(false);
                    lock (parent._gate) result = CandidateUnderGate(pending, caller);
                    if (result is null) Stop();
                }
            }
            catch (Exception caught)
            {
                RecordError(OriginalError(originalObserved ? original : null, caught));
                Stop();
            }
            finally
            {
                // 协调中途出错也不能遗弃原发送；先关两条流，再完整等待同一个 Task。
                if (original is not null && !originalObserved)
                {
                    Stop();
                    try { await original.ConfigureAwait(false); }
                    catch (Exception caught) { RecordError(OriginalError(original, caught)); }
                }
                if (attempt is not null && original is not null) RecordAttemptErrors(attempt.Snapshot);

                Task? disposeRegistration = null;
                try
                {
                    disposeRegistration = registration.DisposeAsync().AsTask();
                    await disposeRegistration.ConfigureAwait(false);
                    lock (parent._gate) _registrationReleased = true;
                }
                catch (Exception caught)
                {
                    RecordError(OriginalError(disposeRegistration, caught));
                    Stop();
                }

                bool stopAfterCleanup = false;
                lock (parent._gate)
                {
                    if (result is not null && (!IsCurrentUnderGate(pending) || caller.IsCancellationRequested || !_registrationReleased || _errors.Count != 0))
                    {
                        result = null;
                        stopAfterCleanup = true;
                    }
                    if (result is not null)
                    {
                        _receipt = result; // 原发送与caller注册都退出后的唯一发布点。
                        parent._displayPresentation?.AcceptReceiptUnderGate(result);
                    }
                    if (ReferenceEquals(_pending, pending)) _pending = null;
                }
                if (stopAfterCleanup) Stop();
            }
            return result;
        }

        private void Stop()
        {
            // 回调与 worker 共用非等待停止路径，绝不调用 CloseAndJoinAsync 形成自环。
            lock (parent._gate) RevokeUnderGate();
            try { parent.StopDisplaySwitch(); }
            catch (Exception error) { RecordError(error); }
        }

        private void RecordAttemptErrors(HostDisplayCatalogSendAttempt.AttemptSnapshot snapshot)
        {
            // 每个独立槽都保留：不 Flatten、不按异常引用去重，也不过滤主动关闭产生的错误。
            foreach (Exception? error in new[] { snapshot.OperationError, snapshot.ObservationError,
                snapshot.FinalSampleError, snapshot.CoordinationError, snapshot.Budget?.Error, snapshot.Budget?.CleanupError })
                if (error is not null) RecordError(error);
        }

        private void RecordError(Exception error)
        {
            lock (parent._gate)
            {
                _errors.Add(error);
                _error = _errors.Count == 1 ? error : new AggregateException("显示切换发送或收尾失败。", _errors);
            }
        }

        private static Exception OriginalError(Task? original, Exception caught)
        {
            AggregateException? container = original?.Exception;
            return container is null ? caught : container.InnerExceptions.Count == 1 ? container.InnerExceptions[0] : container;
        }

        private sealed class PendingRequest(DisplayCatalogFrame catalog, DisplayId id, long sequence)
        {
            internal DisplayCatalogFrame Catalog { get; } = catalog;
            internal DisplayId Id { get; } = id;
            internal long Sequence { get; } = sequence;
            internal TaskCompletionSource<DisplayChangedFrame> Received { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
