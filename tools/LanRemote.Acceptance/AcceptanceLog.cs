using System.Globalization;
using System.IO;
using System.Text;

namespace LanRemote.Acceptance;

/// <summary>
/// 验收输出：一行行收集，同时喂给 UI 和磁盘上的日志文件。
/// </summary>
/// <remarks>
/// <para>存在的原因：<c>OutputType</c> 是 <c>WinExe</c>，进程没有控制台，
/// <c>Console.WriteLine</c> 写了也没人看得见。验收的全部价值在于「证据能被拷走」，
/// 所以输出必须有确定的去处——这里是 UI 日志区 + 磁盘文件两处。</para>
/// <para><b>每次运行一个不可变文件</b>：文件名带 UTC 时刻 + Run ID
/// （见 <see cref="CreateForRun"/>），同一秒跑两次也不会互相覆盖。
/// 刻意<b>不提供</b>「清空日志」——验收仪器不该允许选择性抹除证据。</para>
/// <para><b>写盘必须在锁内</b>：日志行来自 accept 循环、发现服务、TLS 回调等多个线程，
/// 两个线程同时 <c>AppendAllText</c> 到同一文件会让其中一个抛
/// <c>IOException</c>（文件被占用），而那行证据就永久丢了。
/// 早先版本把写盘放在锁外，属于「日志看起来正常但少了几行」的隐性证据损坏。</para>
/// <para>写文件是 best-effort：磁盘满 / 目录不可写不该让验收跑不起来。</para>
/// </remarks>
public sealed class AcceptanceLog
{
    private readonly object _gate = new();
    private readonly List<string> _lines = new();
    private readonly string? _filePath;
    private bool _fileFailed;

    /// <summary>
    /// 新建日志；<paramref name="fileName"/> 为空则不落盘。
    /// </summary>
    public AcceptanceLog(string? directory, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            _filePath = Path.Combine(directory, fileName);
            File.WriteAllText(_filePath, string.Empty, new UTF8Encoding(false));
        }
        catch (Exception)
        {
            // 落不了盘也要能跑：UI 日志区仍然是完整证据。
            _filePath = null;
            _fileFailed = true;
        }
    }

    /// <summary>验收日志的默认目录。</summary>
    public static string DefaultDirectory =>
        Path.Combine(Path.GetTempPath(), "lanremote-m3-acceptance");

    /// <summary>
    /// 为一次运行创建<b>不可变</b>日志文件：<c>m3-&lt;prefix&gt;-&lt;UTC时刻&gt;-&lt;runId&gt;.log</c>。
    /// </summary>
    /// <param name="directory">目录；为空则用 <see cref="DefaultDirectory"/>。</param>
    /// <param name="prefix">角色前缀，例如 <c>host</c> / <c>client</c> / <c>gui</c> / <c>info</c>。</param>
    /// <param name="runId">8 位 Run ID。</param>
    public static AcceptanceLog CreateForRun(string? directory, string prefix, string runId)
    {
        string effective = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory : directory;
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return new AcceptanceLog(effective, $"m3-{prefix}-{stamp}-{runId}.log");
    }

    /// <summary>每写一行触发一次；订阅方负责回到 UI 线程。</summary>
    public event Action<string>? LineWritten;

    /// <summary>写一行。</summary>
    public void WriteLine(string line = "")
    {
        lock (_gate)
        {
            _lines.Add(line);

            if (_filePath is not null && !_fileFailed)
            {
                try
                {
                    // 锁内追加：见类型说明，锁外追加会丢行。
                    File.AppendAllText(_filePath, line + Environment.NewLine, new UTF8Encoding(false));
                }
                catch (Exception)
                {
                    // 一次失败之后就别再每次都试了，但不要因此丢掉内存里的行。
                    _fileFailed = true;
                }
            }
        }

        // 回调放在锁外：订阅方会切到 UI 线程，锁内回调容易变成死锁。
        LineWritten?.Invoke(line);
    }

    /// <summary>写一行带前缀的行。</summary>
    public void WriteLine(string prefix, string line) => WriteLine(prefix + " " + line);

    /// <summary>到目前为止的全部内容。</summary>
    public string All
    {
        get
        {
            lock (_gate)
            {
                return string.Join(Environment.NewLine, _lines);
            }
        }
    }

    /// <summary>落盘路径；未落盘则为 <see langword="null"/>。</summary>
    public string? FilePath => _filePath;

    /// <summary>磁盘日志是否不可用（此时只剩 UI 里那一份）。</summary>
    public bool FileUnavailable
    {
        get { lock (_gate) { return _filePath is null || _fileFailed; } }
    }

    /// <summary>UI 定时拉取有限批次，不为每行堆积 Dispatcher 操作。</summary>
    public IReadOnlyList<string> ReadFrom(int offset, int limit = 200)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        lock (_gate)
        {
            return _lines.Skip(offset).Take(limit).ToArray();
        }
    }
}
