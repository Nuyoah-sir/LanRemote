using System.Text.Json;
using System.Text.Json.Serialization;
using LanRemote.Core.Infrastructure;

namespace LanRemote.Core.Configuration;

/// <summary>
/// 普通（非秘密）主机配置。
/// </summary>
/// <remarks>
/// 字段与 <c>PROJECT_BOOTSTRAP.md</c> 的 Config 草案一致。
/// 安全默认值来自 <c>02_PRODUCT_SPEC.md</c> 第 5 节：默认情况下允许被发现/查看/控制，
/// 且「陌生控制端首次连接需本机确认」必须为 <see langword="true"/>。
/// </remarks>
public sealed record AppConfig
{
    /// <summary>是否允许本设备出现在局域网设备列表中。默认 <see langword="true"/>。</summary>
    public bool AllowDiscovery { get; init; } = true;

    /// <summary>是否允许远端查看本设备屏幕。默认 <see langword="true"/>。</summary>
    public bool AllowViewing { get; init; } = true;

    /// <summary>是否允许远端注入键鼠输入。默认 <see langword="true"/>。</summary>
    public bool AllowControl { get; init; } = true;

    /// <summary>陌生控制端连接前是否需要本机人工确认。默认 <see langword="true"/>。</summary>
    public bool RequireLocalApprovalForUnknownController { get; init; } = true;

    /// <summary>Windows 登录后是否自动启动到托盘。默认 <see langword="false"/>。</summary>
    public bool AutoStartOnLogin { get; init; }

    /// <summary>新建会话时使用的默认画质预设。</summary>
    public string DefaultQualityPreset { get; init; } = "Balanced";

    /// <summary>UDP 局域网发现端口。协议 v1 固定为 45872。</summary>
    public int DiscoveryPort { get; init; } = 45872;

    /// <summary>TLS/TCP 传输端口。协议 v1 固定为 45873。</summary>
    public int TransportPort { get; init; } = 45873;

    /// <summary>本机同时允许的「仅查看」会话上限。</summary>
    public int MaxViewSessions { get; init; } = 3;

    /// <summary>本机同时允许的「控制」会话上限。</summary>
    public int MaxControlSessions { get; init; } = 1;
}

/// <summary>
/// <see cref="AppConfig"/> 的 JSON 持久化。
/// </summary>
/// <remarks>
/// 序列化选项固定（见 <c>06_DEV_STANDARDS.md</c> 第 4 节）：不启用多态 type handling，
/// 反序列化后由 <see cref="AppConfig"/> 自身承担范围语义。
/// 本类只处理普通配置，绝不接触访问密钥、私钥或 session token。
/// </remarks>
public sealed class AppConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AppPaths _paths;

    /// <summary>用默认 <c>%LOCALAPPDATA%\LanRemote</c> 目录构造存储。</summary>
    public AppConfigStore()
        : this(AppPaths.Default)
    {
    }

    /// <summary>用指定路径构造存储，便于测试隔离。</summary>
    /// <param name="paths">数据目录定位。</param>
    public AppConfigStore(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <summary>
    /// 读取配置文件；文件不存在、为空或解析失败时返回默认值而非抛异常。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>有效配置。</returns>
    public async Task<AppConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.ConfigFilePath))
        {
            return new AppConfig();
        }

        try
        {
            await using FileStream stream = File.OpenRead(_paths.ConfigFilePath);
            AppConfig? config = await JsonSerializer.DeserializeAsync<AppConfig>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);

            return config ?? new AppConfig();
        }
        catch (JsonException)
        {
            // 配置文件损坏不应导致应用无法启动；回落到安全默认值。
            // 调用方负责记录日志，此处不吞掉上下文之外的信息。
            return new AppConfig();
        }
    }

    /// <summary>
    /// 写入配置文件。会先确保目录存在。
    /// </summary>
    /// <param name="config">要保存的配置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task SaveAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        _paths.EnsureCreated();

        await using FileStream stream = new(
            _paths.ConfigFilePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);

        await JsonSerializer.SerializeAsync(stream, config, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);
    }
}
