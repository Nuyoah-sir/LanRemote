using System.Net.Security;

namespace LanRemote.Transport;

/// <summary>Host 独占的关闭句柄；登记表的同步 Dispose 只请求同一个异步关闭任务。</summary>
internal sealed class ConnectionCloseHandle : IDisposable
{
    private readonly object _gate = new();
    private readonly IDisposable _socket;
    private readonly HostLifecycleErrors _hostErrors;
    private readonly TaskCompletionSource<SslStream?> _attachment =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _closeTask;
    private Exception? _socketError;
    private Exception? _streamError;

    internal ConnectionCloseHandle(IDisposable socket, HostLifecycleErrors hostErrors)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(hostErrors);
        _socket = socket;
        _hostErrors = hostErrors;
    }

    /// <summary>只能附加一次；关闭请求可以先到，但不能早于这个附加阶段结束而报告完成。</summary>
    internal void Attach(SslStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!_attachment.TrySetResult(stream))
        {
            throw new InvalidOperationException("SSL 附加阶段已经结束。");
        }
    }

    /// <summary>Host 在 finally 中封口；未创建 SSL 的早退路径也必须调用，之后不再允许 Attach。</summary>
    internal void CompleteAttachment() => _attachment.TrySetResult(null);

    /// <summary>
    /// 重复调用返回同一 Task。完成表示 socket 与已附加 SSL 的释放都已尝试，
    /// 不表示释放必然成功；错误保存在 CleanupErrors 和 Host 的有界诊断中，不产生故障后台任务。
    /// </summary>
    internal Task CloseAsync()
    {
        lock (_gate)
        {
            // 不在请求方（包括登记表的 force 路径）执行可能阻塞的同步 Dispose。
            return _closeTask ??= Task.Run(CloseCoreAsync);
        }
    }

    internal IReadOnlyList<Exception> CleanupErrors
    {
        get
        {
            Exception? socket = Volatile.Read(ref _socketError);
            Exception? stream = Volatile.Read(ref _streamError);
            List<Exception> errors = new(2);
            if (socket is not null)
            {
                errors.Add(socket);
            }

            if (stream is not null)
            {
                errors.Add(stream);
            }

            return errors.AsReadOnly();
        }
    }

    public void Dispose() => _ = CloseAsync();

    private async Task CloseCoreAsync()
    {
        // 先断 socket 解堵；无论它是否抛错，都继续尝试 SSL 释放。
        try
        {
            _socket.Dispose();
        }
        catch (Exception error)
        {
            Volatile.Write(ref _socketError, error);
            _hostErrors.Record(HostLifecycleErrorKind.SocketCleanup, error);
        }

        // force 可能发生在 GetStream / new SslStream / Attach 之间；等待迟到的 SSL，不能漏收。
        SslStream? stream = await _attachment.Task.ConfigureAwait(false);
        try
        {
            stream?.Dispose();
        }
        catch (Exception error)
        {
            Volatile.Write(ref _streamError, error);
            _hostErrors.Record(HostLifecycleErrorKind.StreamCleanup, error);
        }
    }
}
