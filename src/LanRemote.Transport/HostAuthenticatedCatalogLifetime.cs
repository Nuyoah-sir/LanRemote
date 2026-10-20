using System.Runtime.ExceptionServices;

namespace LanRemote.Transport;

/// <summary>仅供内部显式装配；构造只验证值，不调用目录来源或时钟。</summary>
internal sealed class HostDisplayCatalogSendOptions
{
    internal HostDisplayCatalogSendOptions(Func<ValueTask<DisplayCatalogFrame>> source, TimeSpan budget)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(budget, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(budget, ControlFrameBudget.MaxBudget);
        Source = source;
        Budget = budget;
    }

    internal Func<ValueTask<DisplayCatalogFrame>> Source { get; }
    internal TimeSpan Budget { get; }
    internal bool UseSessionWriter { get; init; }
    // 自动协调模式仅注入专用未Start候选的准备工厂；与原handler模式互斥。
    internal Func<LanRemote.Core.Models.DisplayInfo, CancellationToken, Task<LanRemote.Core.Abstractions.IVideoFrameProducer>>?
        DisplayRequestPrepare { get; init; }
    // 显式请求接缝；返回原处理任务，必须覆盖业务操作/回执的完整生命周期，不可反等reader。
    internal Func<SessionRegistry.SessionRegistration, HostControlWriter, DisplayCatalogFrame,
        DisplaySwitchRequestFrame, CancellationToken, Task>? DisplayRequestHandler { get; init; }
}

/// <summary>认证后单次目录发送与原 holding 的固定生命周期所有者；不读、不关流、不拥有会话登记。</summary>
/// <remarks>
/// StopRequired 只通知认证栈先注销，不是 join。Sent 不终止会话；原来源、读写或时钟不合作时，
/// Completion 不保证有限完成。快照只涵盖本所有者与发送底座的根，不证明 Host 已关闭或视频已退出。
/// </remarks>
internal sealed class HostAuthenticatedCatalogLifetime
{
    internal sealed record LifetimeSnapshot(
        bool Started, bool StopRequested, bool HoldingFinished, bool AttemptFinished,
        bool HostRegistrationReleased, bool OriginalOperationsFinished, bool AllOwnedRootsReleased,
        Exception? HoldingError, Exception? AttemptError, Exception? CoordinationError, Exception? CleanupError,
        HostDisplayCatalogSendAttempt.AttemptSnapshot Attempt);

    private readonly object _gate = new();
    private readonly HostDisplayCatalogSendAttempt _attempt;
    private readonly Task _holding;
    private readonly TaskCompletionSource _closeRequired;
    private readonly CancellationToken _hostToken;
    private readonly TaskCompletionSource _hostStop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopRequired = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _completion;
    private bool _startRequested, _stopRequested, _hostRegistrationReleased;
    private Exception? _holdingError, _attemptError, _coordinationError, _cleanupError;

    internal HostAuthenticatedCatalogLifetime(HostDisplayCatalogSendAttempt attempt, Task holding,
        TaskCompletionSource closeRequired, CancellationToken hostToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(holding);
        ArgumentNullException.ThrowIfNull(closeRequired);
        _attempt = attempt;
        _holding = holding;
        _closeRequired = closeRequired;
        _hostToken = hostToken;
    }

    internal Task? Completion { get { lock (_gate) return _completion; } }
    internal Task StopRequired => _stopRequired.Task;
    internal LifetimeSnapshot Snapshot
    {
        get
        {
            // 不嵌套 owner 和发送底座的 gate；进行中的快照不是事务证明。
            HostDisplayCatalogSendAttempt.AttemptSnapshot attempt = _attempt.Snapshot;
            Task? sending = _attempt.Completion;
            lock (_gate)
            {
                bool joined = _completion?.IsCompleted == true && _holding.IsCompleted &&
                    (sending is null || sending.IsCompleted);
                return new(_completion is not null, _stopRequested, _holding.IsCompleted,
                    sending is null || sending.IsCompleted, _hostRegistrationReleased, joined,
                    joined && _hostRegistrationReleased && _cleanupError is null &&
                        (sending is null || attempt.AllOwnedRootsReleased),
                    _holdingError, _attemptError, _coordinationError, _cleanupError, attempt);
            }
        }
    }

    internal Task Start()
    {
        Exception failure;
        lock (_gate)
        {
            if (_startRequested) throw new InvalidOperationException("认证目录生命周期不能重复启动。");
            _startRequested = true;
            try { return _completion = Task.Run(RunAsync); }
            catch (Exception error) { failure = error; }
        }
        // 发布失败仍须唤醒外层关线；调用方保有构造前创建的原 holding。
        RequestStop();
        ExceptionDispatchInfo.Capture(failure).Throw();
        throw new InvalidOperationException("认证目录生命周期未发布。");
    }

    private void RequestStop()
    {
        _closeRequired.TrySetResult();
        _attempt.RequestStop();
        lock (_gate) _stopRequested = true;
        _stopRequired.TrySetResult();
    }

    private async Task RunAsync()
    {
        CancellationTokenRegistration registration = default;
        try
        {
            // 与 Start 共用发布屏障；外部发送开始前原 worker 已归属本所有者。
            lock (_gate) { if (_stopRequested) return; }
            // 完成的原 holding 或已到达的 Host 停止优先；不得因此启动来源。
            if (_holding.IsCompleted || _hostToken.IsCancellationRequested) return;
            registration = _hostToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetResult(), _hostStop);
            if (_holding.IsCompleted || _hostToken.IsCancellationRequested) return;
            Task<HostDisplayCatalogSendAttempt.SendOutcome> sending = _attempt.Start();
            await Task.WhenAny(_holding, _attempt.AbortRequired, _hostStop.Task, sending).ConfigureAwait(false);
            // 即使底座意外结束却没发 Abort，也必须停止；原错误只在最终 join 收集一次。
            if (!sending.IsCompletedSuccessfully || sending.Result.End != HostDisplayCatalogSendAttempt.AttemptEnd.Sent)
                return;
            // Sent 不是父会话结束；不把已完成 sending 放回竞速，避免忙循环。
            await Task.WhenAny(_holding, _attempt.AbortRequired, _hostStop.Task).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            lock (_gate) _coordinationError = error;
        }
        finally
        {
            RequestStop();
            // 两项原根都必须退出；不能让一项 fault 跳过另一项，也不以取消包装假装退出。
            await Task.WhenAll(JoinHoldingAsync(), JoinAttemptAsync()).ConfigureAwait(false);
            try
            {
                registration.Dispose();
                lock (_gate) _hostRegistrationReleased = true;
            }
            catch (Exception error)
            {
                lock (_gate) _cleanupError = error;
            }
        }
    }

    private async Task JoinHoldingAsync()
    {
        try { await _holding.ConfigureAwait(false); }
        catch (Exception error)
        {
            lock (_gate) _holdingError = OriginalFailure(_holding, error);
        }
    }

    private async Task JoinAttemptAsync()
    {
        // Start 失败亦检查底座已发布的原任务，而不是假定没有在途操作。
        Task? sending = _attempt.Completion;
        if (sending is null) return;
        try { await sending.ConfigureAwait(false); }
        catch (Exception error)
        {
            lock (_gate) _attemptError = OriginalFailure(sending, error);
        }
    }

    private static Exception OriginalFailure(Task original, Exception fallback)
    {
        AggregateException? errors = original.Exception;
        return errors is null ? fallback : errors.InnerExceptions.Count == 1 ? errors.InnerExceptions[0] : errors;
    }
}
