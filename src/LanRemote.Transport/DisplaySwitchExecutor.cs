using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using Admission = LanRemote.Transport.SessionRegistry.SessionRegistration.DisplaySwitchAdmission;
using Status = LanRemote.Transport.SessionRegistry.SessionRegistration.DisplaySwitchAdmissionStatus;
using Reservation = LanRemote.Transport.SessionRegistry.SessionRegistration.DisplaySwitchAdmission.DisplaySwitchReservation;

namespace LanRemote.Transport;

/// <summary>单会话拥有型请求执行器；只通过真实准入创建，不接网络或生产默认路径。</summary>
/// <remarks>
/// factory须交付专用、尚未Start且未由其他来源拥有的候选；原Task成功返回即归本执行器。
/// Replace正常同步返回原Task即移交，异步失败不返还；同步拒绝的候选由本地完整退役。
/// RequestStop只封口/通知并调度取消，既不停止source也不完成lease；外层必须先独立停止
/// source等依赖再join本Completion，否则在途Replace可能仍等待原代际。父结束需外层调用RequestStop。
/// factory/生产者/取消回调不得等待本执行器的操作或Completion。取消不能硬中断同步前缀。
/// 无队列/定时器/重试。Completion包括已登记原执行、取消回调与本地候选清理，不代表隐藏根已释放。
/// </remarks>
internal sealed class DisplaySwitchExecutor : IAsyncDisposable
{
    // 仅本次操作的历史事实；不是wire成功、当前权限或发送/显示屏障。
    internal sealed record ExecutionResult(bool Transferred, bool ReplacementCompleted)
    {
        internal RequestSendReceipt? SendReceipt { get; init; }
    }

    // 只在显式追踪执行器成功移交后返回；不缓存请求集合，不含生产者或流。
    internal sealed record RequestSendReceipt(
        DisplaySwitchRequestFrame Request, ReplaceableVideoFrameSource.GenerationIdentity Generation)
    {
        internal Task<VideoFrameSender.SentFrame?> FirstSend => Generation.FirstSend;
    }

    // 接受请求时即交给调用者；异步准备/换代失败也不丢失是否移交的真实身份。
    internal sealed class RequestExecutionReceipt(DisplaySwitchRequestFrame request)
    {
        private readonly TaskCompletionSource<ReplaceableVideoFrameSource.GenerationIdentity?> _binding =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal DisplaySwitchRequestFrame Request { get; } = request;
        internal Task<ReplaceableVideoFrameSource.GenerationIdentity?> Binding => _binding.Task;
        internal void Bind(ReplaceableVideoFrameSource.GenerationIdentity? generation) => _binding.TrySetResult(generation);
    }

    private readonly bool _trackSend;
    private readonly object _gate;
    private readonly Admission _admission;
    private readonly Func<DisplayInfo, CancellationToken, Task<IVideoFrameProducer>> _prepare;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _completion;
    private Task<ExecutionResult>? _active;
    private Task? _cancel;
    private bool _stopping;

    internal DisplaySwitchExecutor(object gate, Admission admission,
        Func<DisplayInfo, CancellationToken, Task<IVideoFrameProducer>> prepare, bool trackSend = false)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(prepare);
        _gate = gate;
        _admission = admission;
        _prepare = prepare;
        _trackSend = trackSend;
        _completion = Task.Run(CompleteAsync);
    }

    internal Task StopRequired => _stop.Task;
    internal Task Completion => _completion;
    internal bool HasActiveExecution { get { lock (_gate) return _active is { IsCompleted: false }; } }

    internal Status TryExecute(DisplaySwitchRequestFrame request, out Task<ExecutionResult>? execution) =>
        TryExecute(request, out execution, out _);

    internal Status TryExecute(DisplaySwitchRequestFrame request, out Task<ExecutionResult>? execution,
        out RequestExecutionReceipt? receipt)
    {
        ArgumentNullException.ThrowIfNull(request);
        execution = null;
        receipt = null;
        lock (_gate)
        {
            if (_stopping || !_admission.OwnsExecutor(this)) return Status.Unavailable;
            // 预约在finally释放不等于原执行Task已结束；后者仍是本执行器的单飞门禁。
            Status status = _admission.TryReserveForExecutor(this, request, out Reservation? reservation);
            if (status != Status.Accepted) return status;
            RequestExecutionReceipt? accepted = _trackSend ? new(request) : null;
            execution = _active = Task.Run(() => ExecuteAsync(reservation!, accepted));
            receipt = accepted;
            return Status.Accepted;
        }
    }

    internal void RequestStop()
    {
        lock (_gate)
        {
            if (_stopping) return;
            _stopping = true;
            _admission.CloseExecutor(this);
            _stop.TrySetResult();
            // Cancel的同步回调不得阻塞RequestStop或候选退役；原worker由Completion持有。
            _cancel = Task.Run(() => _cancellation.Cancel());
        }
    }

    public ValueTask DisposeAsync()
    {
        RequestStop();
        return new ValueTask(_completion);
    }

    private async Task<ExecutionResult> ExecuteAsync(Reservation reservation, RequestExecutionReceipt? receipt)
    {
        IVideoFrameProducer? owned = null;
        Task? original = null;
        bool transferred = false, replaced = false;
        ReplaceableVideoFrameSource.GenerationIdentity? generation = null;
        List<Exception> errors = [];
        CancellationToken token = _cancellation.Token;
        try
        {
            // 与发布执行Task共用gate；外部factory调用仍在锁外，Stop不承诺硬中断同步前缀。
            lock (_gate)
            {
                if (_stopping || !reservation.IsCurrent)
                {
                    RequestStop();
                    return new(false, false);
                }
            }
            Task<IVideoFrameProducer> preparing = _prepare(reservation.Target, token)
                ?? throw new InvalidOperationException("目标准备未返回原任务。");
            original = preparing;
            owned = await preparing.ConfigureAwait(false)
                ?? throw new InvalidOperationException("目标准备未交付生产者。");
            original = null;
            lock (_gate)
            {
                if (!_stopping)
                {
                    // 预约、执行器身份与真实父资格在原registry gate内一起校验。
                    original = reservation.ReplaceProducerAsync(this, owned, out generation);
                    owned = null; // 只有正常同步返回才移交；随后失败也不能本地重复清理。
                    transferred = true;
                    receipt?.Bind(generation);
                }
            }
            if (transferred)
            {
                await original!.ConfigureAwait(false);
                replaced = true;
            }
        }
        catch (Exception caught)
        {
            Exception error = OriginalError(original, caught);
            if (!(error is OperationCanceledException cancelled && token.IsCancellationRequested &&
                cancelled.CancellationToken == token && (original is null || original.IsCanceled)))
                errors.Add(error);
            RequestStop();
        }
        finally
        {
            // 未移交的路径也必须结算绑定；这不是本地候选清理已退出的证明。
            receipt?.Bind(null);
            if (owned is not null)
            {
                // 不串行调用外部Stop/Dispose/getter；任何同步阻塞都不阻止另外两个通知。
                Task<Observation> stop = Task.Run(() => ObserveAsync(() => owned.StopAsync()));
                Task<Observation> dispose = Task.Run(() => ObserveAsync(() => owned.DisposeAsync().AsTask()));
                Task<Observation> completion = Task.Run(() => ObserveAsync(() => owned.Completion));
                Observation[] observations = await Task.WhenAll(stop, dispose, completion).ConfigureAwait(false);
                HashSet<Task> seen = new(ReferenceEqualityComparer.Instance);
                foreach (Observation observation in observations)
                {
                    if (observation.Operation is not null && !seen.Add(observation.Operation)) continue;
                    if (observation.Error is not null) errors.Add(observation.Error);
                }
            }
            reservation.Dispose();
        }
        if (errors.Count != 0)
        {
            RequestStop();
            throw new AggregateException("切屏请求执行失败。", errors);
        }
        return new(transferred, replaced)
        {
            SendReceipt = _trackSend && transferred ? new(reservation.Request, generation!) : null,
        };
    }

    private async Task CompleteAsync()
    {
        await _stop.Task.ConfigureAwait(false);
        Task? active, cancel;
        lock (_gate) { active = _active; cancel = _cancel; }
        List<Exception> errors = [];
        // 原执行先join，取消worker并行运行，不被其阻塞同步回调挡住本地候选清理。
        foreach (Task? operation in new[] { active, cancel })
        {
            if (operation is null) continue;
            try { await operation.ConfigureAwait(false); }
            catch (Exception caught) { errors.Add(OriginalError(operation, caught)); }
        }
        _cancellation.Dispose();
        if (errors.Count != 0) throw new AggregateException("切屏请求执行器已停止。", errors);
    }

    private sealed record Observation(Task? Operation, Exception? Error);
    private static async Task<Observation> ObserveAsync(Func<Task> invoke)
    {
        Task? original = null;
        try
        {
            original = invoke() ?? throw new InvalidOperationException("生产者未返回完整生命周期任务。");
            await original.ConfigureAwait(false);
            return new(original, null);
        }
        catch (Exception caught) { return new(original, OriginalError(original, caught)); }
    }

    private static Exception OriginalError(Task? original, Exception caught) =>
        original?.Exception is not { } tree ? caught : tree.InnerExceptions.Count == 1 ? tree.InnerExceptions[0] : tree;
}
