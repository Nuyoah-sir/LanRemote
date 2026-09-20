using System.IO;
using LanRemote.Core.Infrastructure;
using Microsoft.Extensions.Logging;

namespace LanRemote.Security.Tests;

/// <summary>
/// 测试用的一次性数据根目录，保证每条用例都拿到干净的 secrets.bin。
/// </summary>
public sealed class TempSecretRoot : IDisposable
{
    /// <summary>创建临时目录。</summary>
    public TempSecretRoot()
    {
        Root = Path.Combine(Path.GetTempPath(), $"LanRemote-Sec-{Guid.NewGuid():N}");
        Paths = AppPaths.FromRootDirectory(Root);
        Paths.EnsureCreated();
    }

    /// <summary>根目录。</summary>
    public string Root { get; }

    /// <summary>定位。</summary>
    public AppPaths Paths { get; }

    /// <summary>删除临时目录。</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响用例结论。
        }
    }
}

/// <summary>
/// 捕获所有日志消息的容器。
/// </summary>
public sealed class LogSink
{
    private readonly List<string> _messages = new();

    /// <summary>追加一条消息。</summary>
    /// <param name="message">消息。</param>
    public void Add(string message)
    {
        lock (_messages)
        {
            _messages.Add(message);
        }
    }

    /// <summary>已捕获的消息快照。</summary>
    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_messages)
            {
                return _messages.ToArray();
            }
        }
    }

    /// <summary>是否有任意一条消息包含指定子串（大小写不敏感）。</summary>
    /// <param name="needle">子串。</param>
    /// <returns>是否包含。</returns>
    public bool Contains(string needle) =>
        Messages.Any(m => m.Contains(needle, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 把 <see cref="ILogger{TCategoryName}"/> 输出导向 <see cref="LogSink"/> 的实现。
/// </summary>
/// <typeparam name="T">类别。</typeparam>
/// <remarks>
/// 直接实现接口而不是用 <c>LoggerFactory</c>，是为了不给测试项目引入额外的 NuGet 依赖。
/// 异常<b>自身及其内层异常的消息</b>也会被捕获，这样才能验证「异常消息里也没有 secret」。
/// </remarks>
public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly LogSink _sink;

    /// <summary>构造日志器。</summary>
    /// <param name="sink">消息容器。</param>
    public CapturingLogger(LogSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _sink.Add(formatter(state, exception));

        Exception? current = exception;
        while (current is not null)
        {
            _sink.Add(current.Message);
            current = current.InnerException;
        }
    }
}
