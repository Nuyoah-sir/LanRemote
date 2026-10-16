using System.Buffers.Binary;
using System.Security.Cryptography;

namespace LanRemote.Transport;

/// <summary>单次目录发送及可选Control载荷底座；目录路径已接认证Host，可选写者复用原预算/IO链。</summary>
/// <remarks>
/// 借用流且要求唯一顺序 writer；不读取、不关闭、不释放流。AbortRequired 要求外层先锁外关线并持有原关闭任务。
/// 构造无 I/O/取时；Start 发布固定协调 Task，另一个固定操作 Task 隔离 source/write/flush 的同步前缀。
/// 预算从 source 前的本地观察开始，覆盖来源、序列化、写出、Flush 和原 timer 释放；不表示物理网络到达。
/// RequestStop 只持短 gate 并发通知，不调用 clock/source/流/取消回调，也不 join。状态检查与锁外外部调用非物理原子，
/// 已越过检查的调用可能在 Stop 返回后才进入。阻塞/违规 clock 可拖住采样与截止通知，但不阻塞 RequestStop。
/// AbortRequired 只是通知；Completion 必须等待已取得的原操作和预算 worker，不能证明隐藏操作或外层关闭已退出。
/// 原同步前缀/AsTask 未交还 Task 时记录未知退出；未知写借用不清零。Completion 结束不等于全部 roots 已释放。
/// Sent 仅记录最后有效样本及 owner gate 的历史接受，不证明对端收到、未来时限或父会话权限仍有效。
/// 后续 Stop/预算拒绝不能回滚已经写出的字节；Rejected 不证明线缆上从未发送。
/// </remarks>
internal sealed class HostDisplayCatalogSendAttempt
{
    internal enum AttemptEnd { Pending, Sent, Rejected }
    internal enum OperationStage { None, Source, Serialize, Buffer, Write, Flush, Finished }
    internal sealed record SendOutcome(AttemptEnd End);
    internal sealed record AttemptSnapshot(
        AttemptEnd End, bool StopRequested, bool Committed, OperationStage Stage,
        bool OperationStarted, bool OperationFinished, bool OperationExitUnproven,
        bool WireBorrowExitUnproven, bool TimerCreationExitUnproven,
        bool OriginalOperationsFinished, bool AllOwnedRootsReleased,
        OperationStage ErrorStage, Exception? OperationError, Exception? ObservationError,
        Exception? FinalSampleError, Exception? CoordinationError, ControlFrameBudget.BudgetSnapshot? Budget);

    private static readonly SendOutcome RejectedOutcome = new(AttemptEnd.Rejected);
    private static readonly SendOutcome SentOutcome = new(AttemptEnd.Sent);
    private readonly object _gate = new();
    private readonly Stream _stream;
    private readonly Func<ValueTask<DisplayCatalogFrame>>? _source;
    private readonly Func<ValueTask<byte[]>>? _payloadSource;
    private readonly Func<DisplayCatalogFrame, byte[]>? _serializeCatalog;
    private readonly int _maxPayloadBytes;
    private readonly SampledClock _clock;
    private readonly TimeSpan _budget;
    private readonly TaskCompletionSource _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _abort = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task<SendOutcome>? _completion;
    private Task<bool>? _operation;
    private Task? _budgetTask;
    private ControlFrameBudget? _deadline;
    private AttemptEnd _end;
    private OperationStage _stage, _errorStage;
    private bool _stopRequested, _committed, _operationFinished, _operationExitUnproven, _wireBorrowExitUnproven;
    private Exception? _operationError, _observationError, _finalSampleError, _coordinationError;

    internal HostDisplayCatalogSendAttempt(Stream stream, Func<ValueTask<DisplayCatalogFrame>> source,
        TimeProvider clock, TimeSpan budget)
        : this(stream, source ?? throw new ArgumentNullException(nameof(source)), null,
            DisplayCatalogFrame.MaxPayloadByteLength, clock, budget) { }

    // 仅复用已验证的原预算/IO/Flush/清理链；source须交付独占数组，原Task返回后由本底座清零。
    internal static HostDisplayCatalogSendAttempt CreatePayload(Stream stream, Func<ValueTask<byte[]>> source,
        int maxPayloadBytes, TimeProvider clock, TimeSpan budget) =>
        new(stream, null, source ?? throw new ArgumentNullException(nameof(source)), maxPayloadBytes, clock, budget);

    internal static HostDisplayCatalogSendAttempt CreateCatalogPayload(Stream stream,
        Func<ValueTask<DisplayCatalogFrame>> source, Func<DisplayCatalogFrame, byte[]> serialize,
        TimeProvider clock, TimeSpan budget) =>
        new(stream, source ?? throw new ArgumentNullException(nameof(source)), null,
            DisplayCatalogFrame.MaxPayloadByteLength, clock, budget, serialize ?? throw new ArgumentNullException(nameof(serialize)));

    private HostDisplayCatalogSendAttempt(Stream stream, Func<ValueTask<DisplayCatalogFrame>>? source,
        Func<ValueTask<byte[]>>? payloadSource, int maxPayloadBytes, TimeProvider clock, TimeSpan budget,
        Func<DisplayCatalogFrame, byte[]>? serializeCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPayloadBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxPayloadBytes, DisplayCatalogFrame.MaxPayloadByteLength);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThan(budget, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(budget, ControlFrameBudget.MaxBudget);
        _stream = stream;
        _source = source;
        _payloadSource = payloadSource;
        _serializeCatalog = serializeCatalog;
        _maxPayloadBytes = maxPayloadBytes;
        _clock = new SampledClock(clock);
        _budget = budget;
    }

    internal Task AbortRequired => _abort.Task;
    internal Task<SendOutcome>? Completion { get { lock (_gate) return _completion; } }
    internal AttemptSnapshot Snapshot
    {
        get
        {
            ControlFrameBudget? deadline;
            bool joined;
            AttemptSnapshot snapshot;
            lock (_gate)
            {
                deadline = _deadline;
                joined = _completion?.IsCompleted == true && (_operation is null || _operation.IsCompleted) &&
                    (_budgetTask is null || _budgetTask.IsCompleted);
                snapshot = new(_end, _stopRequested, _committed, _stage, _operation is not null,
                    _operationFinished, _operationExitUnproven, _wireBorrowExitUnproven, false,
                    joined, false, _errorStage, _operationError, _observationError,
                    _finalSampleError, _coordinationError, null);
            }
            // 不嵌套 owner/预算/sample gate；进行中的快照不是事务证明，Completion 后才稳定（后续 Stop 除外）。
            ControlFrameBudget.BudgetSnapshot? budget = deadline?.Snapshot;
            bool unknownTimer = _clock.TimerCreationExitUnproven;
            bool released = joined && !snapshot.OperationExitUnproven && !snapshot.WireBorrowExitUnproven &&
                !unknownTimer && (budget is null || (!budget.CleanupExitUnproven && budget.CleanupError is null &&
                    (!budget.TimerAcquired || budget.TimerReleased)));
            return snapshot with { Budget = budget, TimerCreationExitUnproven = unknownTimer, AllOwnedRootsReleased = released };
        }
    }

    internal Task<SendOutcome> Start()
    {
        lock (_gate)
        {
            if (_completion is not null) throw new InvalidOperationException("单次目录发送不能重复启动。");
            return _completion = Task.Run(RunAsync);
        }
    }

    internal void RequestStop()
    {
        lock (_gate)
        {
            _stopRequested = true;
            if (!_committed) { _end = AttemptEnd.Rejected; _abort.TrySetResult(); }
            _stop.TrySetResult();
        }
    }

    private bool CanContinue() { lock (_gate) return !_stopRequested && _end == AttemptEnd.Pending; }
    private void Reject()
    {
        lock (_gate)
        {
            if (!_committed) { _end = AttemptEnd.Rejected; _abort.TrySetResult(); }
        }
    }

    private async Task<SendOutcome> RunAsync()
    {
        Task<bool>? operation = null;
        Task? budgetTask = null;
        ControlFrameBudget? deadline = null;
        long anchor = 0, frequency = 0;
        bool candidate = false;
        try
        {
            if (!CanContinue()) return RejectedOutcome; // 与 Start 相同 gate 的发布屏障。
            try { (anchor, frequency) = _clock.Sample(); }
            catch (Exception error)
            {
                lock (_gate) _observationError = error;
                Reject(); return RejectedOutcome;
            }
            if (!CanContinue()) return RejectedOutcome;
            deadline = new ControlFrameBudget(_clock, anchor, frequency, _budget);
            lock (_gate) _deadline = deadline;
            if (!CanContinue()) deadline.RequestStop();
            budgetTask = deadline.Start();
            lock (_gate) _budgetTask = budgetTask;
            await Task.WhenAny(deadline.Ready, _stop.Task).ConfigureAwait(false);
            if (!CanContinue() || deadline.Snapshot.End != ControlFrameBudget.BudgetEnd.Pending)
            { Reject(); return RejectedOutcome; }
            lock (_gate)
            {
                if (_stopRequested || _end != AttemptEnd.Pending) return RejectedOutcome;
                operation = _operation = Task.Run(() => RunOperationAsync(deadline, anchor, frequency));
            }
            await Task.WhenAny(operation, deadline.Decision, _stop.Task).ConfigureAwait(false);
            // 全部可见拒绝状态优先；WhenAny 参数顺序不授予成功优先级。
            if (!CanContinue() || deadline.Snapshot.End != ControlFrameBudget.BudgetEnd.Pending || !operation.IsCompleted)
            { Reject(); return RejectedOutcome; }
            if (!await operation.ConfigureAwait(false)) { Reject(); return RejectedOutcome; }
            deadline.RequestFinish();
            await Task.WhenAny(deadline.Decision, _stop.Task).ConfigureAwait(false);
            if (!CanContinue() || deadline.Snapshot.End != ControlFrameBudget.BudgetEnd.FinishedInBudget)
            { Reject(); return RejectedOutcome; }
            await budgetTask.ConfigureAwait(false);
            ControlFrameBudget.BudgetSnapshot state = deadline.Snapshot;
            if (state.End != ControlFrameBudget.BudgetEnd.FinishedInBudget || state.Error is not null ||
                state.CleanupError is not null || state.CleanupExitUnproven ||
                (state.TimerAcquired && !state.TimerReleased) || _clock.TimerCreationExitUnproven)
            { Reject(); return RejectedOutcome; }
            candidate = true;
        }
        catch (Exception error)
        {
            lock (_gate) _coordinationError = error;
            Reject();
        }
        finally
        {
            if (!candidate) { Reject(); deadline?.RequestStop(); }
            // 固定原 worker 完整 join；拒绝已先通知外层关线，不用 WaitAsync/取消包装遗弃原操作。
            Task all = Task.WhenAll((Task?)operation ?? Task.CompletedTask, budgetTask ?? Task.CompletedTask);
            try { await all.ConfigureAwait(false); }
            catch (Exception caught)
            {
                lock (_gate) _coordinationError ??= OriginalError(all, caught);
                candidate = false;
                Reject();
            }
        }
        if (!candidate || !CanContinue()) return RejectedOutcome;
        try { SampleDeadline(anchor, frequency); }
        catch (Exception error)
        {
            lock (_gate) _finalSampleError = error;
            Reject(); return RejectedOutcome;
        }
        lock (_gate)
        {
            if (_stopRequested || _end != AttemptEnd.Pending) return RejectedOutcome;
            _committed = true;
            _end = AttemptEnd.Sent;
        }
        return SentOutcome;
    }

    private bool CheckStage(ControlFrameBudget deadline, long anchor, long frequency, OperationStage stage)
    {
        if (!CanContinue() || deadline.Snapshot.End != ControlFrameBudget.BudgetEnd.Pending) return false;
        lock (_gate) _stage = stage;
        try { SampleDeadline(anchor, frequency); }
        catch (Exception error)
        {
            lock (_gate) _observationError ??= error;
            Reject(); return false;
        }
        return CanContinue() && deadline.Snapshot.End == ControlFrameBudget.BudgetEnd.Pending;
    }

    private void SampleDeadline(long anchor, long frequency)
    {
        (long now, long observedFrequency) = _clock.Sample();
        if (observedFrequency != frequency) throw new InvalidOperationException("host-catalog-clock-frequency-changed");
        Int128 delta = (Int128)now - anchor;
        if (delta < 0 || delta > long.MaxValue) throw new InvalidOperationException("host-catalog-clock-range-invalid");
        if (delta * TimeSpan.TicksPerSecond >= (Int128)_budget.Ticks * frequency)
            throw new TimeoutException("host-catalog-budget-expired");
    }

    private async Task<bool> RunOperationAsync(ControlFrameBudget deadline, long anchor, long frequency)
    {
        Task? original = null;
        Task? originalWrite = null;
        byte[]? payload = null, wire = null;
        bool prefixUnreturned = false, writeEntered = false;
        OperationStage stage = OperationStage.Source;
        try
        {
            if (!CheckStage(deadline, anchor, frequency, stage)) return false;
            prefixUnreturned = true;
            if (_payloadSource is { } payloadSource)
            {
                Task<byte[]> originalSource = payloadSource().AsTask();
                original = originalSource;
                prefixUnreturned = false;
                payload = await originalSource.ConfigureAwait(false)
                    ?? throw new InvalidOperationException("host-control-payload-missing");
                original = null;
                if (!CheckStage(deadline, anchor, frequency, stage)) return false;
                stage = OperationStage.Serialize;
                if (!CheckStage(deadline, anchor, frequency, stage)) return false;
            }
            else
            {
                Task<DisplayCatalogFrame> originalSource = _source!().AsTask();
                original = originalSource;
                prefixUnreturned = false;
                DisplayCatalogFrame frame = await originalSource.ConfigureAwait(false)
                    ?? throw new InvalidOperationException("host-catalog-source-missing");
                original = null;
                if (!CheckStage(deadline, anchor, frequency, stage)) return false;
                stage = OperationStage.Serialize;
                if (!CheckStage(deadline, anchor, frequency, stage)) return false;
                payload = _serializeCatalog is { } serialize ? serialize(frame) : frame.Serialize();
            }
            if (payload.Length < 1 || payload.Length > _maxPayloadBytes)
                throw new InvalidOperationException("host-catalog-payload-length-invalid");
            if (!CheckStage(deadline, anchor, frequency, stage)) return false;

            stage = OperationStage.Buffer;
            if (!CheckStage(deadline, anchor, frequency, stage)) return false;
            wire = new byte[TransportConstants.LengthPrefixBytes + payload.Length];
            BinaryPrimitives.WriteUInt32BigEndian(wire, (uint)payload.Length);
            payload.CopyTo(wire, TransportConstants.LengthPrefixBytes);
            CryptographicOperations.ZeroMemory(payload);
            payload = null;
            if (!CheckStage(deadline, anchor, frequency, stage)) return false;

            stage = OperationStage.Write;
            if (!CheckStage(deadline, anchor, frequency, stage)) return false;
            prefixUnreturned = writeEntered = true;
            original = originalWrite = _stream.WriteAsync(wire.AsMemory(), CancellationToken.None).AsTask();
            prefixUnreturned = false;
            await originalWrite.ConfigureAwait(false);
            original = null;
            CryptographicOperations.ZeroMemory(wire);
            if (!CheckStage(deadline, anchor, frequency, stage)) return false;

            stage = OperationStage.Flush;
            if (!CheckStage(deadline, anchor, frequency, stage)) return false;
            prefixUnreturned = true;
            original = _stream.FlushAsync(CancellationToken.None)
                ?? throw new InvalidOperationException("host-catalog-flush-task-missing");
            prefixUnreturned = false;
            await original.ConfigureAwait(false);
            original = null;
            if (!CheckStage(deadline, anchor, frequency, stage)) return false;
            lock (_gate) _stage = OperationStage.Finished;
            return true;
        }
        catch (Exception caught)
        {
            lock (_gate)
            {
                _operationError = OriginalError(original, caught);
                _errorStage = stage;
                _operationExitUnproven = prefixUnreturned;
                _wireBorrowExitUnproven = writeEntered && originalWrite is null;
            }
            Reject();
            return false;
        }
        finally
        {
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
            // 写同步前缀未返回 Task 时隐藏借用未知，不能把 worker 退出当数组静止证明。
            if (wire is not null && (!writeEntered || originalWrite?.IsCompleted == true))
                CryptographicOperations.ZeroMemory(wire);
            lock (_gate) _operationFinished = true;
        }
    }

    private static Exception OriginalError(Task? original, Exception caught)
    {
        AggregateException? container = original?.Exception;
        return container is null ? caught : container.InnerExceptions.Count == 1 ? container.InnerExceptions[0] : container;
    }

    /// <summary>时钟外部采样全段串行；owner gate 与此 gate 不嵌套，外部 clock 只在专用 sampleGate 内采样。</summary>
    private sealed class SampledClock(TimeProvider inner) : TimeProvider
    {
        private readonly object _sampleGate = new();
        private bool _seen;
        private long _frequency, _last;
        private int _timerCreationExitUnproven;
        internal bool TimerCreationExitUnproven => Volatile.Read(ref _timerCreationExitUnproven) != 0;
        internal (long Timestamp, long Frequency) Sample()
        {
            lock (_sampleGate)
            {
                long before = inner.TimestampFrequency;
                CheckFrequency(before);
                long now = inner.GetTimestamp();
                long after = inner.TimestampFrequency;
                if (after != before) throw new InvalidOperationException("host-catalog-clock-frequency-changed");
                CheckFrequency(after);
                if (_seen && now < _last) throw new InvalidOperationException("host-catalog-clock-regressed");
                _frequency = before;
                _last = now;
                _seen = true;
                return (now, before);
            }
        }
        private void CheckFrequency(long frequency)
        {
            if (frequency <= 0 || (_seen && frequency != _frequency))
                throw new InvalidOperationException("host-catalog-clock-frequency-changed");
        }
        public override long TimestampFrequency
        {
            get
            {
                lock (_sampleGate)
                {
                    long frequency = inner.TimestampFrequency;
                    CheckFrequency(frequency);
                    return frequency;
                }
            }
        }
        public override long GetTimestamp() => Sample().Timestamp;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            try
            {
                // 进行中的同步前缀由原 worker 未完成表达；退出却未交付 timer 才记录未知隐藏根。
                // null 是违规 provider 未交付资源，保守保留未知，不伪称取得或释放了 timer。
                return inner.CreateTimer(callback, state, dueTime, period)
                    ?? throw new InvalidOperationException("host-catalog-timer-missing");
            }
            catch
            {
                Volatile.Write(ref _timerCreationExitUnproven, 1);
                throw;
            }
            // 不包装 timer 或 DisposeAsync，原释放 Task 仍由预算 worker 持有。
        }
    }
}
