namespace LanRemote.Core.Infrastructure;

/// <summary>
/// LanRemote 在本机使用的数据目录定位。
/// </summary>
/// <remarks>
/// <para>约定目录（见 <c>02_PRODUCT_SPEC.md</c> 第 9 节）：</para>
/// <list type="bullet">
/// <item><description><c>%LOCALAPPDATA%\LanRemote\config.json</c> — 普通设置；</description></item>
/// <item><description><c>%LOCALAPPDATA%\LanRemote\secrets.bin</c> — DPAPI 保护的秘密；</description></item>
/// <item><description><c>%LOCALAPPDATA%\LanRemote\logs\</c> — 日志。</description></item>
/// </list>
/// <para>硬约束：秘密绝不写入 <c>config.json</c>。本类型只负责定位，不负责内容。</para>
/// </remarks>
public sealed class AppPaths
{
    /// <summary>应用程序数据目录名。</summary>
    public const string ApplicationFolderName = "LanRemote";

    /// <summary>配置文件名。</summary>
    public const string ConfigFileName = "config.json";

    /// <summary>DPAPI 保护的秘密文件名。</summary>
    public const string SecretsFileName = "secrets.bin";

    /// <summary>日志目录名。</summary>
    public const string LogsFolderName = "logs";

    /// <summary>基于真实 <c>%LOCALAPPDATA%</c> 的默认实例。</summary>
    public static AppPaths Default { get; } = FromRootDirectory(DefaultRootDirectoryPath());

    private AppPaths(string rootDirectory)
    {
        RootDirectory = rootDirectory;
    }

    /// <summary>数据根目录，例如 <c>%LOCALAPPDATA%\LanRemote</c>。</summary>
    public string RootDirectory { get; }

    /// <summary>配置文件路径。</summary>
    public string ConfigFilePath => Path.Combine(RootDirectory, ConfigFileName);

    /// <summary>秘密文件路径。</summary>
    public string SecretsFilePath => Path.Combine(RootDirectory, SecretsFileName);

    /// <summary>日志目录路径。</summary>
    public string LogsDirectory => Path.Combine(RootDirectory, LogsFolderName);

    /// <summary>计算默认数据根目录。</summary>
    /// <returns><c>%LOCALAPPDATA%\LanRemote</c>。</returns>
    public static string DefaultRootDirectoryPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ApplicationFolderName);

    /// <summary>以指定根目录构造实例，供测试与自定义部署使用。</summary>
    /// <param name="rootDirectory">数据根目录绝对路径。</param>
    /// <exception cref="ArgumentException"><paramref name="rootDirectory"/> 为空或空白。</exception>
    public static AppPaths FromRootDirectory(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        return new AppPaths(rootDirectory);
    }

    /// <summary>创建数据根目录与日志目录（已存在则无操作）。</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    /// <inheritdoc />
    public override string ToString() => RootDirectory;
}
