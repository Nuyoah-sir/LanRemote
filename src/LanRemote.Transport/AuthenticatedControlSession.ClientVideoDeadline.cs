namespace LanRemote.Transport;

public sealed partial class AuthenticatedControlSession
{
    internal sealed partial class ClientVideoLifetime
    {
        private readonly AttachDeadline _deadline;
        private Exception? _deadlineFailure;

        /// <summary>
        /// 一个固定协调 worker 独占 Create/Change/DisposeAsync 的整个同步前缀和原 Task。
        /// timer 回调只合并通知，不取时、不重排、不调用任意取消回调或关闭网络。
        /// 此对象没有自己的起点；每次采样都使用父 success 的同源单调锚点。
        /// </summary>
        private sealed class AttachDeadline(ClientVideoLifetime owner)
        {
            private readonly object _signalGate = new();
            private readonly TaskCompletionSource _ready = NewSignal();
            private TaskCompletionSource _wake = NewSignal();
            private bool _quiescing;
            private Task? _worker;

            // 两个有界诊断来源：首次执行期失败在 owner，原 timer 释放失败单独保留。
            // 写入和读取均受父 gate 保护；不递归剥掉用户的异常包装。
            internal Exception? CleanupError { get; private set; }
            internal Task Ready => _ready.Task;

            internal void StartUnderGate() => _worker = Task.Run(RunAsync);

            internal void StopScheduling()
            {
                lock (_signalGate)
                {
                    _quiescing = true;
                    _wake.TrySetResult();
                }
            }

            internal Task QuiesceAsync()
            {
                StopScheduling();
                // 必须返回原协调 worker，不能把 ready/wake 通知当作原操作退出。
                return _worker!;
            }

            private static TaskCompletionSource NewSignal() =>
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            private void OnTimer()
            {
                // 整个回调只有此短临界区；即使损坏的 provider 在释放后回调也没有副作用。
                // 正常 provider 的原 DisposeAsync 还会排空已经开始但未返回的回调。
                lock (_signalGate)
                {
                    if (!_quiescing) _wake.TrySetResult();
                }
            }

            private bool IsRunning()
            {
                // 与父停止路径一致：父 gate -> 通知 gate；通知回调绝不反向取得父 gate。
                lock (owner.Gate)
                {
                    if (owner._stopped || owner._parent._connection is null) return false;
                    lock (_signalGate) return !_quiescing;
                }
            }

            private TimeSpan? SampleRemaining()
            {
                if (!IsRunning()) return null;
                AuthenticatedControlSession parent = owner._parent;
                // 外部 TimeProvider 同步前缀不持父锁；可重入撤销/停止，所以取时后重新裁决。
                long now = parent._clock.GetTimestamp();
                TimeSpan elapsed = parent._clock.GetElapsedTime(parent._successReceivedAt, now);
                lock (owner.Gate)
                {
                    if (!IsRunning()) return null;
                    if (elapsed < TimeSpan.Zero || elapsed >= parent._attachBudget)
                        throw new TimeoutException("本地视频附着等待预算已耗尽。");
                    return parent._attachBudget - elapsed;
                }
            }

            private void Fail(Exception error)
            {
                lock (owner.Gate)
                {
                    owner._deadlineFailure ??= error;
                    owner.MarkStoppedUnderGate();
                }
                // 只请求停止，绝不 await 包含当前 worker 的 child/parent join。
                owner.EnsureStopStarted();
            }

            private async Task RunAsync()
            {
                ITimer? timer = null;
                try
                {
                    // 首次父 gate 是两个 worker 都登记完成后的发布屏障。
                    if (!IsRunning()) return;
                    // 禁用态创建，返回后无条件接管：重入停止不能使迟到 timer 丢失。
                    timer = owner._parent._clock.CreateTimer(
                        static state => ((AttachDeadline)state!).OnTimer(), this,
                        Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan)
                        ?? throw new InvalidOperationException("时钟没有返回附着计时器。");

                    int immediateWakeups = 0;
                    while (SampleRemaining() is { } remaining)
                    {
                        // 1ms 只控制通知粒度；不会给最终接受判据增加宽限。
                        TimeSpan due = remaining < TimeSpan.FromMilliseconds(1)
                            ? TimeSpan.FromMilliseconds(1) : remaining;
                        if (!timer.Change(due, Timeout.InfiniteTimeSpan))
                            throw new InvalidOperationException("附着计时器重排失败。");
                        // Change 的同步前缀也扣预算，重入停止/到期不能放行 connect。
                        if (SampleRemaining() is null) return;
                        _ready.TrySetResult();

                        Task wake;
                        lock (_signalGate)
                        {
                            if (_quiescing) return;
                            wake = _wake.Task;
                        }
                        // 防止非合作 provider 每次 Change 都同步早回调造成无限忙循环。
                        // 有界合并通知不建每回调 Task；正常异步唤醒会重置此计数。
                        immediateWakeups = wake.IsCompleted ? immediateWakeups + 1 : 0;
                        if (immediateWakeups > 32)
                            throw new InvalidOperationException("附着计时器连续同步提前回调，无法建立等待。");
                        await wake.ConfigureAwait(false);
                        lock (_signalGate)
                        {
                            if (_quiescing) return;
                            _wake = NewSignal();
                        }
                    }
                }
                catch (Exception error) { Fail(error); }
                finally
                {
                    StopScheduling();
                    // ready 只放行状态复查，不证明 timer 已释放；attach finally 仍等待原 worker。
                    _ready.TrySetResult();
                    if (timer is not null)
                    {
                        Task? originalDispose = null;
                        try
                        {
                            // 调用本身也在此 worker/父锁外；ValueTask 只转换一次并等待原 Task。
                            originalDispose = timer.DisposeAsync().AsTask();
                            await originalDispose.ConfigureAwait(false);
                        }
                        catch (Exception error)
                        {
                            Exception saved = originalDispose is null ? error : OriginalError(originalDispose, error);
                            lock (owner.Gate) CleanupError = saved;
                            Fail(saved);
                        }
                    }
                }
            }
        }
    }
}
