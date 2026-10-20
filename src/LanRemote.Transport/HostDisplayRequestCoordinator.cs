using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using Admission = LanRemote.Transport.SessionRegistry.SessionRegistration.DisplaySwitchAdmission;
using Status = LanRemote.Transport.SessionRegistry.SessionRegistration.DisplaySwitchAdmissionStatus;

namespace LanRemote.Transport;

/// <summary>单会话显示请求协调器；真实换代首发成功后，才向同一Control写者提交回执。</summary>
/// <remarks>
/// Handle返回单次原worker，Completion从构造起稳定且等待停止；两者均不传播业务异常。
/// Error是本组件唯一报告槽，交给认证层AddCatalogErrors收集，不能再经reader重复上报。
/// StopRequired/closeRequired仅通知；父必须独立撤销、关线并停止source/sender，不能先join本组件。
/// 不拥有流、source或lease，不等待reader.Completion；prepare及其取消回调不得反等本组件。
/// </remarks>
internal sealed class HostDisplayRequestCoordinator : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SessionRegistry.SessionRegistration _registration;
    private readonly Func<DisplayInfo, CancellationToken, Task<IVideoFrameProducer>> _prepare;
    private readonly TaskCompletionSource _closeRequired;
    private readonly CancellationToken _hostToken;
    private readonly TaskCompletionSource _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _completion;
    private readonly List<Exception> _errors = [];
    private HostControlWriter? _writer;
    private DisplayCatalogFrame? _catalog;
    private Admission? _admission;
    private DisplaySwitchExecutor? _executor;
    private Task? _active;
    private Exception? _error;
    private bool _stopping;

    internal HostDisplayRequestCoordinator(SessionRegistry.SessionRegistration registration,
        Func<DisplayInfo, CancellationToken, Task<IVideoFrameProducer>> prepare,
        TaskCompletionSource closeRequired, CancellationToken hostToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(prepare);
        ArgumentNullException.ThrowIfNull(closeRequired);
        _registration = registration;
        _prepare = prepare;
        _closeRequired = closeRequired;
        _hostToken = hostToken;
        lock (_gate) _completion = Task.Run(CompleteAsync);
    }

    internal Task Completion => _completion;
    internal Task StopRequired => _stop.Task;
    internal Exception? Error { get { lock (_gate) return _error; } }

    internal Task Handle(HostControlWriter writer, DisplayCatalogFrame catalog,
        DisplaySwitchRequestFrame request, CancellationToken readerToken)
    {
        lock (_gate)
        {
            if (_stopping) return Task.CompletedTask;
            if (_hostToken.IsCancellationRequested || _closeRequired.Task.IsCompleted || readerToken.IsCancellationRequested)
            {
                RequestStop();
                return Task.CompletedTask;
            }
            try
            {
                ArgumentNullException.ThrowIfNull(writer);
                ArgumentNullException.ThrowIfNull(catalog);
                ArgumentNullException.ThrowIfNull(request);
                if (writer.SessionId != _registration.SessionId || request.SessionId != _registration.SessionId)
                    throw new FrameProtocolException("request-display-session-mismatch");
                if (_active is { IsCompleted: false })
                    throw new FrameProtocolException("request-display-coordinator-busy");
                if (_writer is not null && (!ReferenceEquals(_writer, writer) || !ReferenceEquals(_catalog, catalog)))
                    throw new FrameProtocolException("request-display-owner-mismatch");
                if (_executor is null)
                {
                    _writer = writer;
                    _catalog = catalog;
                    // 保存后即拥有；即使创建executor失败，也由Completion释放admission。
                    _admission = _registration.CreateDisplaySwitchAdmission(catalog);
                    _executor = _admission.CreateReplyExecutor(_prepare);
                }
                DisplaySwitchExecutor executor = _executor;
                return _active = Task.Run(() => RunRequestAsync(executor, writer, request, readerToken));
            }
            catch (Exception caught)
            {
                _errors.Add(caught);
                RequestStop();
                return Task.CompletedTask;
            }
        }
    }

    internal void RequestStop()
    {
        lock (_gate)
        {
            if (_stopping) return;
            _stopping = true;
            _stop.TrySetResult();
            _closeRequired.TrySetResult();
            _executor?.RequestStop();
        }
    }

    public ValueTask DisposeAsync()
    {
        RequestStop();
        return new(_completion);
    }

    private bool CanContinue() { lock (_gate) return !_stopping; }
    private void AddError(Exception error) { lock (_gate) _errors.Add(error); }

    private async Task RunRequestAsync(DisplaySwitchExecutor executor, HostControlWriter writer,
        DisplaySwitchRequestFrame request, CancellationToken readerToken)
    {
        CancellationTokenRegistration registration = default;
        try
        {
            lock (_gate) if (_stopping) return; // 原worker已发布；外部调用只能在gate外发生。
            registration = readerToken.Register(static state => ((HostDisplayRequestCoordinator)state!).RequestStop(), this);
            if (!CanContinue()) return;
            Status status = executor.TryExecute(request, out var execution, out var receipt);
            if (status != Status.Accepted)
                throw new FrameProtocolException($"request-display-execution-{status}");

            await Task.WhenAny(execution!, executor.StopRequired, _stop.Task).ConfigureAwait(false);
            if (executor.StopRequired.IsCompleted || !CanContinue()) RequestStop();
            DisplaySwitchExecutor.ExecutionResult result;
            try { result = await execution!.ConfigureAwait(false); }
            catch (Exception)
            {
                // 原execution必须join；它的完整错误树只从executor.Completion收集一次。
                RequestStop();
                return;
            }
            if (!CanContinue() || !result.Transferred || !result.ReplacementCompleted)
            {
                RequestStop();
                return;
            }

            ReplaceableVideoFrameSource.GenerationIdentity? generation = await receipt!.Binding.ConfigureAwait(false);
            if (generation is null) throw new FrameProtocolException("request-display-generation-missing");
            Task<VideoFrameSender.SentFrame?> firstSend = generation.FirstSend;
            await Task.WhenAny(firstSend, _stop.Task, executor.StopRequired).ConfigureAwait(false);
            if (!CanContinue() || executor.StopRequired.IsCompleted)
            {
                // FirstSend是结果promise；source/sender的资源原根仍由父join，不在这里反等它们。
                RequestStop();
                return;
            }
            if (await firstSend.ConfigureAwait(false) is null)
                throw new FrameProtocolException("request-display-first-send-missing");
            DisplaySwitchExecutor.ReplyStatus replyStatus = executor.TrySendOwnedReply(receipt, writer, out var reply);
            if (replyStatus != DisplaySwitchExecutor.ReplyStatus.Accepted)
                throw new FrameProtocolException($"request-display-reply-{replyStatus}");

            await Task.WhenAny(reply!, _stop.Task, executor.StopRequired).ConfigureAwait(false);
            if (!CanContinue() || executor.StopRequired.IsCompleted) RequestStop();
            try
            {
                DisplaySwitchExecutor.ReplyResult replied = await reply!.ConfigureAwait(false);
                if (!replied.WriteCompleted) RequestStop();
            }
            catch (Exception)
            {
                // writer有自己的生命周期归属；这里不重复保存reply传播的同一执行器错误。
                RequestStop();
            }
        }
        catch (Exception caught)
        {
            AddError(caught);
            RequestStop();
        }
        finally
        {
            Task? disposing = null;
            try
            {
                disposing = registration.DisposeAsync().AsTask();
                await disposing.ConfigureAwait(false);
            }
            catch (Exception caught)
            {
                AddError(OriginalError(disposing, caught));
                RequestStop();
            }
        }
    }

    private async Task CompleteAsync()
    {
        CancellationTokenRegistration registration = default;
        TaskCompletionSource hostCancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            lock (_gate) { } // 构造时先发布稳定Completion，再注册可能同步触发的回调。
            registration = _hostToken.Register(() =>
            {
                RequestStop();
                hostCancellation.TrySetResult();
            });
            await Task.WhenAny(_closeRequired.Task, _stop.Task, hostCancellation.Task).ConfigureAwait(false);
        }
        catch (Exception caught) { AddError(caught); }
        finally
        {
            RequestStop();
            Task? active;
            DisplaySwitchExecutor? executor;
            Admission? admission;
            lock (_gate) { active = _active; executor = _executor; admission = _admission; }
            if (active is not null)
            {
                try { await active.ConfigureAwait(false); }
                catch (Exception caught) { AddError(OriginalError(active, caught)); }
            }
            if (executor is not null)
            {
                Task original = executor.Completion;
                try { await original.ConfigureAwait(false); }
                catch (Exception caught) { AddError(OriginalError(original, caught)); }
            }
            try { admission?.Dispose(); }
            catch (Exception caught) { AddError(caught); }
            Task? disposing = null;
            try
            {
                disposing = registration.DisposeAsync().AsTask();
                await disposing.ConfigureAwait(false);
            }
            catch (Exception caught) { AddError(OriginalError(disposing, caught)); }
            lock (_gate)
                _error = _errors.Count == 0 ? null : _errors.Count == 1 ? _errors[0]
                    : new AggregateException("显示请求协调及收尾失败。", _errors);
        }
    }

    private static Exception OriginalError(Task? original, Exception caught) =>
        (Exception?)original?.Exception ?? caught;
}
