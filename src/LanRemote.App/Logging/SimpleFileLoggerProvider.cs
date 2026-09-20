using System.Collections.Concurrent;
using System.IO;
using Microsoft.Extensions.Logging;

namespace LanRemote.App.Logging;

/// <summary>
/// 极简文件日志提供器。
/// </summary>
/// <remarks>
/// <para>刻意保持最小实现：同步写入 + 轻量队列，不依赖第三方日志框架
/// （依赖策略见 06_DEV_STANDARDS.md 第 13 节）。</para>
/// <para>已知限制：M9 应替换为带日志轮转/保留 7 天的正式提供器，并加入结构化接收器。
/// 在此之前，日志目录会持续增长。</para>
/// <para>绝不写入的内容：访问密钥、auth proof、session token、证书私钥、屏幕数据、键盘文本。</para>
/// </remarks>
public sealed class SimpleFileLoggerProvider : ILoggerProvider
{
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 4096);
    private readonly Task _writerTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _filePath;

    /// <summary>构造提供器并立即落盘可能的目录。</summary>
    /// <param name="logsDirectory">日志目录。</param>
    public SimpleFileLoggerProvider(string logsDirectory)
    {
        Directory.CreateDirectory(logsDirectory);
        _filePath = Path.Combine(logsDirectory, $"lanremote-{DateTime.Now:yyyyMMdd}.log");
        _writerTask = Task.Run(WriteLoopAsync);
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
        _queue.CompleteAdding();
        _cts.Cancel();

        try
        {
            // 给写入循环一个有限的上限时间，避免退出时被永久阻塞。
            _writerTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 退出路径：写入失败不应影响关机。
        }

        _cts.Dispose();
        _queue.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Enqueue(string line)
    {
        if (_queue.IsAddingCompleted)
        {
            return;
        }

        try
        {
            // 队列满时丢弃，绝不允许后台日志拖慢或阻塞 UI / 管线。
            _queue.TryAdd(line, millisecondsTimeout: 0);
        }
        catch (InvalidOperationException)
        {
            // 已 CompleteAdding，忽略。
        }
    }

    private async Task WriteLoopAsync()
    {
        using StreamWriter writer = new(
            new FileStream(
                _filePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 64 * 1024))
        {
            AutoFlush = false,
        };

        try
        {
            foreach (string line in _queue.GetConsumingEnumerable(_cts.Token))
            {
                await writer.WriteLineAsync(line).ConfigureAwait(false);

                if (_queue.Count == 0)
                {
                    await writer.FlushAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
        catch (IOException)
        {
            // 磁盘不可用时不可抛出，否则会杀掉宿主。
        }
        finally
        {
            try
            {
                writer.Flush();
            }
            catch (IOException)
            {
                // 刷新失败同样不可抛出，退出路径优先。
            }
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly SimpleFileLoggerProvider _provider;
        private readonly string _categoryName;

        public FileLogger(SimpleFileLoggerProvider provider, string categoryName)
        {
            _provider = provider;
            _categoryName = categoryName;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string message = formatter(state, exception);

            // 结构化日志：时间 + 级别 + 类别 + 消息 + 异常类型。
            // 注意：formatter 产出的消息由调用方提供，日志器不再做内容清洗，
            // 调用方的责任是不把 secret 拼进去（见 06_DEV_STANDARDS.md 第 6 节）。
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel,-11}] {_categoryName}: {message}"
                + (exception is null ? string.Empty : $" | Exception: {exception.GetType().Name}: {exception.Message}");

            _provider.Enqueue(line);
        }
    }
}
