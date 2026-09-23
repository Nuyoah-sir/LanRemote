using System.Windows.Threading;

namespace LanRemote.Acceptance.Tests;

/// <summary>独立 STA 消息泵，不创建 Application 或任何视觉窗口。</summary>
internal sealed class StaDispatcherFixture : IAsyncDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);
    private readonly TaskCompletionSource<Dispatcher> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finished =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private int _disposed;

    public StaDispatcherFixture()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Acceptance 审批测试 STA",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<Dispatcher> Ready => _ready.Task;

    private void Run()
    {
        try
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(DispatcherPriority.Send,
                new Action(() => _ready.TrySetResult(dispatcher)));
            Dispatcher.Run();
            _finished.TrySetResult();
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
            _finished.TrySetException(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        Dispatcher dispatcher = await _ready.Task.WaitAsync(ShutdownTimeout).ConfigureAwait(false);
        dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        await _finished.Task.WaitAsync(ShutdownTimeout).ConfigureAwait(false);
        if (!_thread.Join(ShutdownTimeout))
        {
            throw new TimeoutException("测试 STA 线程未能退出。");
        }
    }
}
