namespace LanRemote.Transport;

public sealed partial class AuthenticatedControlSession
{
    private ControlDisconnectMonitor? _controlMonitor;

    internal enum ControlMonitorEnd { Pending, SkippedLocalStop, EndOfStream, UnexpectedData, Faulted }

    /// <summary>
    /// 独立不可变快照。LocalStopObservedBeforeCompletion 仅表示首次本地撤销时尚未观察到原读完成：
    /// 已发布 Task 用 IsCompleted 复核，ReadAsync 同步前缀/AsTask 尚未返回的区间无法判定物理先后。
    /// 不据此删除 IO/ODE 等错误；未来 public 异步释放的报告策略另行定案。
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
    /// 父会话拥有的固定单读槽；不是心跳或完整 Control 消息分发器。
    /// 当前没有后续 Control 消费方，任何额外字节均结束会话，不静默吞掉或猜测消息类型。
    /// 无 CTS/循环/阶段 timer；关闭只能请求解堵，完整 join 仍须等待原操作真正退出。
    /// </summary>
    private sealed class ControlDisconnectMonitor(AuthenticatedControlSession parent)
    {
        private Task<int>? _original;
        private bool _localStopObserved;
        private bool _localStopBeforeCompletion;
        private ControlMonitorEnd _end;
        private Exception? _error;
        internal Task Worker { get; private set; } = null!;
        internal ControlMonitorSnapshot Snapshot => new(_end, _localStopBeforeCompletion, _error);

        internal void StartUnderGate() => Worker = Task.Run(RunAsync);

        // 仅在父 gate 内调用；纯观察，不执行任意回调、网络关闭或等待。
        internal void ObserveLocalStopUnderGate()
        {
            if (_localStopObserved || _end != ControlMonitorEnd.Pending) return;
            _localStopObserved = true;
            _localStopBeforeCompletion = _original is null || !_original.IsCompleted;
        }

        private async Task RunAsync()
        {
            Task<int>? original = null;
            ControlMonitorEnd end;
            Exception? failure = null;
            try
            {
                Stream stream;
                lock (parent._gate)
                {
                    // 发布屏障：Worker 已登记；本地停止先赢则不再发起新读取。
                    if (parent._connection is null)
                    {
                        _end = ControlMonitorEnd.SkippedLocalStop;
                        return;
                    }
                    stream = parent._ownedConnection.Stream;
                }
                // 同步前缀、ValueTask.AsTask 和原 Task 都包含在 Worker 内，且不持有父 gate。
                // 不用 async 适配包装原读，避免丢失 Task 多 fault 的兄弟异常。
                original = stream.ReadAsync(new byte[1], CancellationToken.None).AsTask();
                lock (parent._gate) _original = original;
                int count = await original.ConfigureAwait(false);
                end = count == 0 ? ControlMonitorEnd.EndOfStream : ControlMonitorEnd.UnexpectedData;
                if (count != 0) failure = new FrameProtocolException("control-unexpected-data");
            }
            catch (Exception error)
            {
                AggregateException? container = original?.Exception;
                failure = container is null ? error
                    : container.InnerExceptions.Count == 1 ? container.InnerExceptions[0] : container;
                end = ControlMonitorEnd.Faulted;
            }

            ClientVideoLifetime? child;
            lock (parent._gate)
            {
                _end = end;
                _error = failure;
                // 同 gate 撤销，和 attach/frame 联合提交互斥；monitor 自身结束不冒充本地停止。
                _ = parent.RevokeCore();
                child = parent._videoLifetime;
            }
            // 只请求，不等待包含本 Worker 的父 join；慢 Control close 不阻塞 child stop 请求。
            _ = parent._ownedConnection.CloseAsync();
            child?.RequestStop();
        }
    }
}
