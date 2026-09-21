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
/// <para>写文件是 best-effort：磁盘满 / 目录不可写不该让验收跑不起来。</para>
/// </remarks>
public sealed class AcceptanceLog
{
    private readonly object _gate = new();
    private readonly List<string> _lines = new();
    private readonly string? _filePath;

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
        }
    }

    /// <summary>每写一行触发一次；订阅方负责回到 UI 线程。</summary>
    public event Action<string>? LineWritten;

    /// <summary>写一行。</summary>
    public void WriteLine(string line = "")
    {
        lock (_gate)
        {
            _lines.Add(line);
        }

        LineWritten?.Invoke(line);

        if (_filePath is null)
        {
            return;
        }

        try
        {
            File.AppendAllText(_filePath, line + Environment.NewLine, new UTF8Encoding(false));
        }
        catch (Exception)
        {
            // 见类型说明：best-effort。
        }
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
}
