namespace LanRemote.Transport;

/// <summary>认证目录发出后的 Host ping 周期 worker；只复用会话唯一写者，不另起写流或无界队列。</summary>
/// <remarks>
/// 写槽忙时跳过本轮，不排队；只有写者接纳的 ping 才登记 pending。停止由 HostControlWriterLifetime
/// 请求并 join，原发送、delay 与计量清理均不形成脱离会话的后台根。
/// </remarks>
internal sealed class HostControlPingLoop
{
    private readonly object _gate = new();
    private readonly HostControlWriter _writer;
    private readonly PingRttMeter _meter;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _pendingTimeout;
    private readonly CancellationTokenSource _stop = new();
    private Task? _completion;
    private bool _stopping;

    internal HostControlPingLoop(HostControlWriter writer, PingRttMeter meter, TimeProvider clock,
        TimeSpan interval, TimeSpan pendingTimeout)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(clock);
        _writer = writer;
        _meter = meter;
        _clock = clock;
        _interval = interval;
        _pendingTimeout = pendingTimeout;
    }

    internal Task? Completion { get { lock (_gate) return _completion; } }

    internal Task Start()
    {
        lock (_gate)
        {
            if (_completion is not null) throw new InvalidOperationException("Control ping worker 不能重复启动。");
            if (_stopping) return _completion = Task.CompletedTask;
            return _completion = Task.Run(RunAsync);
        }
    }

    internal void RequestStop()
    {
        bool cancel;
        lock (_gate)
        {
            cancel = !_stopping;
            _stopping = true;
        }
        if (cancel) _stop.Cancel();
    }

    private async Task RunAsync()
    {
        List<Exception> errors = [];
        Task<HostControlWriter.WriteResult>? sending = null;
        try
        {
            while (true)
            {
                await Task.Delay(_interval, _clock, _stop.Token).ConfigureAwait(false);
                _stop.Token.ThrowIfCancellationRequested();
                _meter.Prune(_pendingTimeout);
                sending = _writer.TryWritePingAsync(_meter, _stop.Token);
                if (sending is null) continue; // 目录/回执优先；本轮不排队、不补发。
                await sending.ConfigureAwait(false);
                sending = null;
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception caught) { errors.Add(caught); }
        finally
        {
            if (errors.Count != 0) _writer.RequestStop();
            if (sending is not null)
            {
                try { await sending.ConfigureAwait(false); }
                catch (Exception caught) { errors.Add(caught); }
            }
            try { _stop.Dispose(); }
            catch (Exception caught) { errors.Add(caught); }
            if (errors.Count != 0) _writer.RequestStop();
        }
        if (errors.Count == 1) throw errors[0];
        if (errors.Count != 0) throw new AggregateException("Control ping worker 已停止。", errors);
    }
}
