using Microsoft.Extensions.Logging;

namespace LanRemote.Acceptance;

/// <summary>
/// 把产品自身的 <see cref="ILogger"/> 输出转发进验收日志。
/// </summary>
/// <remarks>
/// <para><b>为什么必须有</b>：验收器复刻了 App 的装配方式，产品代码里大量的
/// <c>LogInformation</c> 走的是 <c>AddSimpleConsole</c> —— 而本进程是 <c>WinExe</c>，
/// 没有控制台，那些日志<b>写了等于没写</b>。结果是：验收日志里只能看到验收器自己打印的行，
/// 一旦失败（DPAPI 读不出来、证书加载被拒、发现服务起不来），
/// 唯一的诊断信息就丢在虚空里，只能靠重跑加打印来找。</para>
/// <para>转发是纯附加的：不改变产品行为，只是把已有的事件带走。
/// 级别用 <see cref="LogLevel.Information"/> 起步——<c>Debug</c> 会把 SslStream
/// 这种高频来源也带进来，把证据冲淡。</para>
/// </remarks>
internal sealed class AcceptanceLoggerProvider : ILoggerProvider
{
    private readonly Action<string> _write;
    private readonly LogLevel _minimum;

    public AcceptanceLoggerProvider(Action<string> write, LogLevel minimum = LogLevel.Information)
    {
        _write = write;
        _minimum = minimum;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new Forwarder(categoryName, _write, _minimum);

    /// <inheritdoc />
    public void Dispose()
    {
        // 没有非托管资源。
    }

    private sealed class Forwarder : ILogger
    {
        private readonly string _category;
        private readonly Action<string> _write;
        private readonly LogLevel _minimum;

        public Forwarder(string category, Action<string> write, LogLevel minimum)
        {
            // 只留类型名：「LanRemote.Security.Secrets.DpapiSecretVault」→「DpapiSecretVault」。
            int lastDot = category.LastIndexOf('.');
            _category = lastDot >= 0 && lastDot < category.Length - 1
                ? category[(lastDot + 1)..]
                : category;

            _write = write;
            _minimum = minimum;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= _minimum;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || formatter is null)
            {
                return;
            }

            string message = formatter(state, exception);
            _write($"[LOG:{_category}] {logLevel}: {message}");

            if (exception is not null)
            {
                _write($"[LOG:{_category}] 异常：{exception}");
            }
        }
    }
}
