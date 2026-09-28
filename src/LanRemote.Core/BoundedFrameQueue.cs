namespace LanRemote.Core;

/// <summary>
/// 用于 <see cref="Models.CapturedFrame"/> 或 <see cref="Models.EncodedFrame"/> 的有界拥有型队列，
/// 满时释放最旧帧，不等待消费者腾出空间。
/// </summary>
/// <typeparam name="T">独占持有资源、可释放的帧引用类型。</typeparam>
/// <remarks>
/// <para>容量仅为 1 或 2，默认 2，对齐 03_ARCHITECTURE.md 第 5 节的 DropOldest 管线。
/// 不缓存待写帧，不提供异步写入或裸 Reader/Writer；容量仅计算队列内帧，不含消费者持有的帧
/// 和正在锁外同步释放的帧。帧的 Dispose 应及时返回，不应执行网络等待。</para>
/// <para>队列支持并发写入、读取及停止，但不使帧本身线程安全。同一帧不得重复交付或在释放后交付。
/// 写入成功后生产者不得再访问或释放帧；读出后由消费者独占并负责释放。
/// Pixels/Payload 等内存视图仅为借用：不得跨越所有权移交或帧释放继续使用，
/// 也不得与该帧的 Dispose 并发使用。队列停止不会释放已经交给消费者的帧。</para>
/// <para>每个被丢弃或清空的帧仅尝试释放一次；若帧的 Dispose 抛异常，队列不能保证其内部资源已释放，
/// 也不会重试。写入的释放异常通过输出参数报告，停止的释放异常聚合抛出。</para>
/// </remarks>
public sealed class BoundedFrameQueue<T> : IDisposable where T : class, IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<T> _frames;
    private TaskCompletionSource? _available;
    private bool _stopped;

    /// <summary>创建容量为 1 或 2 的 DropOldest 队列。</summary>
    /// <param name="capacity">最大排队帧数，默认 2。</param>
    /// <exception cref="ArgumentOutOfRangeException">容量不是 1 或 2。</exception>
    public BoundedFrameQueue(int capacity = 2)
    {
        if (capacity is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "容量只能为 1 或 2。");
        }

        Capacity = capacity;
        _frames = new Queue<T>(capacity);
    }

    /// <summary>最大排队帧数。</summary>
    public int Capacity { get; }

    /// <summary>尝试移交一帧；满时丢弃并同步释放最旧帧，不等待网络或消费者。</summary>
    /// <param name="frame">由调用方独占持有的非空帧。</param>
    /// <param name="disposalError">
    /// 丢旧帧的 Dispose 异常；没有异常时为 null。调用方应检查此输出，但异常不改变返回值或所有权。
    /// </param>
    /// <returns>
    /// true 表示所有权已交给队列（返回前也可能已被读出、丢弃或因停止释放）；
    /// false 表示队列已停止，帧仍归调用方，队列没有访问或释放它。
    /// </returns>
    /// <exception cref="ArgumentNullException">frame 为 null，未发生所有权移交。</exception>
    /// <remarks>
    /// 丢旧帧的 Dispose 在锁外执行，异常不从本方法抛出，以免已接受的新帧所有权被异常掩盖。
    /// 与停止并发时，锁内先完成的操作决定接受或拒绝；已移出的旧帧仍由本次写入负责释放和报告异常。
    /// </remarks>
    public bool TryWrite(T frame, out Exception? disposalError)
    {
        ArgumentNullException.ThrowIfNull(frame);
        disposalError = null;
        T? dropped = null;

        lock (_gate)
        {
            if (_stopped)
            {
                return false;
            }

            if (_frames.Count == Capacity)
            {
                dropped = _frames.Dequeue();
            }

            _frames.Enqueue(frame);
            SignalReaders();
        }

        if (dropped is not null)
        {
            try
            {
                dropped.Dispose();
            }
            catch (Exception exception)
            {
                disposalError = exception;
            }
        }

        return true;
    }

    /// <summary>等待并取出最旧的可用帧，将所有权移交给消费者。</summary>
    /// <param name="cancellationToken">仅取消本次读取，不停止队列，也不取走或释放帧。</param>
    /// <returns>由消费者独占持有、必须释放的帧。</returns>
    /// <exception cref="OperationCanceledException">读取在取得帧前观察到取消。</exception>
    /// <exception cref="ObjectDisposedException">队列已停止或释放。</exception>
    /// <remarks>
    /// 唤醒通知不携带帧；读取在锁内检查取消后才取帧。取消与取帧竞争时，
    /// 若已经取帧则返回成功，不会再因取消丢失该帧。已取消的令牌优先于停止状态。
    /// 多个读者之间不保证公平性。
    /// </remarks>
    public async ValueTask<T> ReadAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task available;
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_stopped, this);

                if (_frames.Count != 0)
                {
                    return _frames.Dequeue();
                }

                _available ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                available = _available.Task;
            }

            await available.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>永久停止队列，拒绝后续写入、唤醒等待者并释放存量帧，不等待消费者。</summary>
    /// <exception cref="AggregateException">所有存量帧均尝试释放后，汇总本次释放中出现的异常。</exception>
    /// <remarks>
    /// 先在锁内标记停止、清空并唤醒，再锁外同步逐项释放；一帧失败不跳过其他帧。
    /// 即使释放失败也保持停止状态，重复 Stop/Dispose 无操作、不会再次释放或重抛历史异常。
    /// 并发重复调用不会等待首次停止的清理；也不等待正在锁外丢旧释放的写入，
    /// 该写入自行报告释放异常。首次调用仍需等待自己的存量帧 Dispose 返回。
    /// </remarks>
    public void Stop()
    {
        T[] remaining;
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            remaining = _frames.ToArray();
            _frames.Clear();
            _stopped = true;
            SignalReaders();
        }

        List<Exception>? errors = null;
        foreach (T frame in remaining)
        {
            try
            {
                frame.Dispose();
            }
            catch (Exception exception)
            {
                (errors ??= new List<Exception>()).Add(exception);
            }
        }

        if (errors is not null)
        {
            throw new AggregateException("停止帧队列时释放帧失败；所有存量帧均已尝试释放。", errors);
        }
    }

    /// <summary>等同于 Stop；不等待消费者，重复调用无害。</summary>
    /// <exception cref="AggregateException">所有存量帧均尝试释放后，汇总本次释放中出现的异常。</exception>
    public void Dispose() => Stop();

    // 仅在持锁时调用；异步 continuation 不会在锁内执行消费者代码。
    private void SignalReaders()
    {
        _available?.TrySetResult();
        _available = null;
    }
}
