using System.Runtime.ExceptionServices;
using System.Security.Cryptography;

namespace LanRemote.Transport;

public sealed partial class AuthenticatedControlSession
{
    private ControlDisconnectMonitor? _controlMonitor;
    private DisplayCatalogFrame? _latestDisplayCatalog;

    /// <summary>当前已交付连接上最后一个完整目录；撤销时清空，不是选屏或帧坐标授权。</summary>
    internal DisplayCatalogFrame? LatestDisplayCatalog
    {
        get { lock (_gate) return _connection is not null && _deliveryCommitted ? _latestDisplayCatalog : null; }
    }

    /// <summary>取得当前已交付且未撤销会话的最后一个完整显示目录；尚未收到目录时返回 null。</summary>
    /// <remarks>重复读取同一目录返回同一不可变实例。撤销只阻止后续查询，不能清除调用方已保留的历史快照。</remarks>
    public DisplayCatalogSnapshot? GetDisplayCatalogSnapshot()
    {
        lock (_gate)
            return _connection is not null && _deliveryCommitted ? _latestDisplayCatalog?.PublicSnapshot : null;
    }

    internal enum ControlMonitorEnd { Pending, SkippedLocalStop, EndOfStream, UnexpectedData, Faulted }

    /// <summary>
    /// 独立不可变快照。LocalStopObservedBeforeCompletion 仅表示首次本地撤销时尚未观察到当前原读/接续完成：
    /// 已发布 Task 用 IsCompleted 复核，ReadAsync 同步前缀/AsTask 尚未返回的区间无法判定物理先后。
    /// 不据此删除 IO/ODE 等错误；End 不是原 worker 已 join 的证明。
    /// </summary>
    internal sealed record ControlMonitorSnapshot(
        ControlMonitorEnd End, bool LocalStopObservedBeforeCompletion, Exception? Error);

    internal ControlMonitorSnapshot? ControlMonitorState
    {
        get { lock (_gate) return _controlMonitor?.Snapshot; }
    }

    internal Task? ControlMonitorCompletion
    {
        get { lock (_gate) return _controlMonitor?.Worker; }
    }

    /// <summary>
    /// 父会话拥有的唯一读者；空闲沿用原单字节读，不建空闲计时器。
    /// 原首读退出后才接续一个最多256KiB的目录帧，仅接受严格Host→Client host_status。
    /// 每帧预算自本地观察首字节起算，10秒为初始值；不把远端seq/时间戳当截止或权限。
    /// </summary>
    private sealed class ControlDisconnectMonitor(AuthenticatedControlSession parent)
    {
        private static readonly TimeSpan CatalogReadBudget = TimeSpan.FromSeconds(10);
        private Task<int>? _original;
        private ControlFrameReadAttempt? _attempt;
        private bool _localStopObserved;
        private bool _localStopBeforeCompletion;
        private bool _remoteStopping;
        private bool _catalogRootsReleased = true;
        internal bool AllOwnedCatalogRootsReleased => _catalogRootsReleased;
        private ControlMonitorEnd _end;
        private Exception? _error;
        internal Task Worker { get; private set; } = null!;
        internal ControlMonitorSnapshot Snapshot => new(_end, _localStopBeforeCompletion, _error);

        internal void StartUnderGate() => Worker = Task.Run(RunAsync);

        // 仅在父 gate 内调用；逻辑失效，不执行外部回调、网络关闭或等待。
        internal void ObserveLocalStopUnderGate()
        {
            if (_localStopObserved || _remoteStopping || _end != ControlMonitorEnd.Pending) return;
            _localStopObserved = true;
            Task? operation = _attempt?.Completion ?? (Task?)_original;
            _localStopBeforeCompletion = operation is null || !operation.IsCompleted;
            _attempt?.RequestStop();
        }

        private async Task RunAsync()
        {
            Task<int>? original = null;
            ControlMonitorEnd end;
            Exception? failure = null;
            try
            {
                while (true)
                {
                    Stream stream;
                    lock (parent._gate)
                    {
                        // 发布屏障：Worker 已登记；停止先赢则不再发起新的首读。
                        if (parent._connection is null)
                        {
                            _end = ControlMonitorEnd.SkippedLocalStop;
                            return;
                        }
                        stream = parent._ownedConnection.Stream;
                        _original = null;
                        _attempt = null;
                    }
                    // 保留原Task及其异常树；同步前缀/AsTask仍在Worker内，不持父gate。
                    byte[] first = new byte[1];
                    original = null;
                    original = stream.ReadAsync(first, CancellationToken.None).AsTask();
                    lock (parent._gate) _original = original;
                    int count = await original.ConfigureAwait(false);
                    if (count == 0) { end = ControlMonitorEnd.EndOfStream; break; }
                    // 256KiB长度的大端首字节必为0；其它值无需继续读或分配payload。
                    if (count != 1 || first[0] != 0)
                    {
                        end = ControlMonitorEnd.UnexpectedData;
                        failure = new FrameProtocolException("control-unexpected-data");
                        break;
                    }
                    original = null; // 后续错误不可借上一已成功Task解释。
                    ControlMessage? message = await ReadCatalogAsync(stream, first[0]).ConfigureAwait(false);
                    if (message is null) { end = ControlMonitorEnd.SkippedLocalStop; break; }
                    lock (parent._gate)
                    {
                        // 帧时限只证明读取历史；业务发布还须同父gate检查真实交付和当前有效性。
                        if (parent._connection is null || !parent._deliveryCommitted)
                        { end = ControlMonitorEnd.SkippedLocalStop; break; }
                        if (message.Catalog is { } catalog)
                        {
                            parent._displaySwitch?.CatalogUnderGate(catalog);
                            parent._latestDisplayCatalog = catalog; // 默认目录单槽和降序兼容不变。
                        }
                        else if (message.Change is { } change && parent._displaySwitch is { } switching)
                            switching.AcceptUnderGate(change); // 只关联真实登记，绝不等发送而挡住EOF。
                        else throw new FrameProtocolException("control-unexpected-data");
                    }
                }
            }
            catch (Exception error)
            {
                AggregateException? container = original?.Exception;
                failure = container is null ? error
                    : container.InnerExceptions.Count == 1 ? container.InnerExceptions[0] : container;
                end = failure is FrameProtocolException { Reason: "control-unexpected-data" }
                    ? ControlMonitorEnd.UnexpectedData : ControlMonitorEnd.Faulted;
            }

            ClientVideoLifetime? child;
            lock (parent._gate)
            {
                // 终态发布和撤销与attach/frame同锁；不得出现已terminal但仍可提交的新增窗口。
                _end = end;
                _error = failure;
                _remoteStopping = true;
                _ = parent.RevokeCore();
                child = parent._videoLifetime;
            }
            _ = parent._ownedConnection.CloseAsync();
            child?.RequestStop();
        }

        private sealed record ControlMessage(DisplayCatalogFrame? Catalog, DisplayChangedFrame? Change);

        private async Task<ControlMessage?> ReadCatalogAsync(Stream stream, byte firstByte)
        {
            lock (parent._gate)
                if (parent._connection is null) return null;
            // 只有原首读已退出后才观察；不是物理到达时刻，不重新读取已消费字节。
            long frequency = parent._clock.TimestampFrequency;
            long observedAt = parent._clock.GetTimestamp();
            if (frequency <= 0 || parent._clock.TimestampFrequency != frequency)
                throw new InvalidOperationException("control-catalog-observation-frequency-invalid");
            CatalogClock clock = new(parent._clock, observedAt, frequency);
            ControlFrameReadAttempt attempt = ControlFrameReadAttempt.FromObservedFirstByte(
                stream, firstByte, clock, observedAt, frequency, CatalogReadBudget,
                DisplayCatalogFrame.MaxPayloadByteLength);
            Task<ControlFrameReadAttempt.FrameOutcome> completion;
            lock (parent._gate)
            {
                if (parent._connection is null) return null;
                _attempt = attempt;
                completion = attempt.Start();
            }
            await Task.WhenAny(completion, attempt.AbortRequired).ConfigureAwait(false);
            // 超时/读失败先撤销并请求关线解堵，但仍等待同一个原组合/原IO/计时器释放。
            if (attempt.AbortRequired.IsCompleted) RequestConnectionStop();
            ControlFrameReadAttempt.FrameOutcome outcome = await completion.ConfigureAwait(false);
            ControlFrameReadAttempt.AttemptSnapshot state = attempt.Snapshot;
            lock (parent._gate)
                _catalogRootsReleased &= state.Continuation?.BorrowExitUnproven != true &&
                    state.Budget?.CleanupExitUnproven != true && state.Budget?.CleanupError is null &&
                    (state.Budget?.TimerAcquired != true || state.Budget.TimerReleased);
            byte[]? payload = outcome.Payload;
            try
            {
                if (outcome.End != ControlFrameReadAttempt.AttemptEnd.Frame || payload is null)
                {
                    List<Exception> errors = [];
                    foreach (Exception? error in new[] { state.FirstRead?.Error, state.Continuation?.Error,
                        state.Budget?.Error, state.Budget?.CleanupError, state.ObservationError,
                        state.FinalSampleError, state.CoordinationError })
                        if (error is not null) AddVideoFailureRoot(errors, error);
                    if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
                    if (errors.Count > 1) throw new AggregateException("Control目录读取及收尾失败。", errors);
                    lock (parent._gate)
                        if (parent._connection is null && _localStopObserved) return null;
                    throw new FrameProtocolException("control-catalog-read-rejected");
                }
                bool parsed = DisplayCatalogFrame.TryParse(payload, out DisplayCatalogFrame? catalog, out _);
                DisplayChangedFrame? change = null;
                bool switchingEnabled;
                lock (parent._gate) switchingEnabled = parent._displaySwitch is not null;
                if (!parsed && switchingEnabled)
                    parsed = DisplayChangedFrame.TryParse(payload, parent.SessionId, out change, out _);
                // 原组合成功不是未来消费许可；合法/非法载荷都先按原观察锚点复核截止。
                _ = clock.TimestampFrequency;
                long now = clock.GetTimestamp();
                _ = clock.TimestampFrequency;
                Int128 elapsed = (Int128)now - observedAt;
                if (elapsed < 0 || elapsed > long.MaxValue ||
                    elapsed * TimeSpan.TicksPerSecond >= (Int128)CatalogReadBudget.Ticks * frequency)
                    throw new TimeoutException("control-catalog-deadline-expired");
                if (!parsed) throw new FrameProtocolException("control-unexpected-data");
                return new(catalog, change);
            }
            finally
            {
                // 仅清理原组合已完整交付的静止数组；不碰在途/未知借用区或TLS内部副本。
                if (payload is not null) CryptographicOperations.ZeroMemory(payload);
                lock (parent._gate) _attempt = null;
            }
        }

        // 接续和解析末检共用观察高水位；不包装原timer，不把远端时间戳当本地时钟。
        private sealed class CatalogClock(TimeProvider source, long observedAt, long frequency) : TimeProvider
        {
            private long _last = observedAt;
            public override long TimestampFrequency => source.TimestampFrequency == frequency
                ? frequency : throw new InvalidOperationException("control-catalog-frequency-changed");
            public override long GetTimestamp()
            {
                long now = source.GetTimestamp();
                if (now < _last) throw new InvalidOperationException("control-catalog-clock-regressed");
                _last = now;
                return now;
            }
            public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
                source.CreateTimer(callback, state, dueTime, period);
        }

        private void RequestConnectionStop()
        {
            ClientVideoLifetime? child;
            lock (parent._gate)
            {
                _remoteStopping = true; // 远端拒绝不冒充用户本地停止。
                _ = parent.RevokeCore();
                child = parent._videoLifetime;
            }
            // 不等待包含本Worker的父join；慢Control close也不阻塞child stop请求。
            _ = parent._ownedConnection.CloseAsync();
            child?.RequestStop();
        }
    }
}
