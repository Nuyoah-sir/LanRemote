using System.IO;
using System.Text.Json;
using LanRemote.Core.Configuration;
using LanRemote.Core.Infrastructure;
using Xunit;

namespace LanRemote.Core.Tests;

/// <summary>
/// <see cref="AppConfigStore"/> 的持久化行为测试。
/// </summary>
public sealed class AppConfigStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"LanRemote-{Guid.NewGuid():N}");
    private readonly AppPaths _paths;
    private readonly AppConfigStore _store;

    public AppConfigStoreTests()
    {
        _paths = AppPaths.FromRootDirectory(_root);
        _store = new AppConfigStore(_paths);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_WhenFileMissing_ReturnsSafeDefaults()
    {
        AppConfig config = await _store.LoadAsync();

        Assert.True(config.AllowDiscovery);
        Assert.True(config.AllowViewing);
        Assert.True(config.AllowControl);

        // 05_UI_UX_SPEC.md 第 3 节：本机确认必须默认开启，这是默认安全的核心。
        Assert.True(config.RequireLocalApprovalForUnknownController);
        Assert.False(config.AutoStartOnLogin);
    }

    [Fact]
    public async Task LoadAsync_WhenFileMissing_DoesNotTouchSecretsFile()
    {
        _ = await _store.LoadAsync();

        Assert.False(File.Exists(_paths.SecretsFilePath));
        Assert.False(File.Exists(_paths.ConfigFilePath));
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsEveryField()
    {
        AppConfig expected = new()
        {
            AllowDiscovery = false,
            AllowViewing = false,
            AllowControl = true,
            RequireLocalApprovalForUnknownController = true,
            AutoStartOnLogin = true,
            DefaultQualityPreset = "HighQuality",
            DiscoveryPort = 45872,
            TransportPort = 45873,
            MaxViewSessions = 7,
            MaxControlSessions = 2,
        };

        await _store.SaveAsync(expected);
        AppConfig actual = await _store.LoadAsync();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task SaveAsync_CreatesDirectoriesAndOnlyWritesConfigFile()
    {
        await _store.SaveAsync(new AppConfig());

        Assert.True(Directory.Exists(_paths.RootDirectory));
        Assert.True(File.Exists(_paths.ConfigFilePath));

        // 秘密文件必须由 Security 模块单独创建，配置存储绝不碰它。
        Assert.False(File.Exists(_paths.SecretsFilePath));
    }

    [Fact]
    public async Task LoadAsync_WhenJsonIsCorrupt_FallsBackToDefaults()
    {
        _paths.EnsureCreated();
        await File.WriteAllTextAsync(_paths.ConfigFilePath, "{ this is not json");

        AppConfig config = await _store.LoadAsync();

        Assert.True(config.RequireLocalApprovalForUnknownController);
        Assert.Equal(45872, config.DiscoveryPort);
        Assert.Equal(45873, config.TransportPort);
    }

    [Fact]
    public async Task SavedJson_UsesCamelCaseKeys()
    {
        await _store.SaveAsync(new AppConfig { MaxViewSessions = 9 });

        string json = await File.ReadAllTextAsync(_paths.ConfigFilePath);
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.True(document.RootElement.TryGetProperty("maxViewSessions", out JsonElement element));
        Assert.Equal(9, element.GetInt32());
    }

    [Fact]
    public async Task SavedJson_NeverContainsSecretsPlaceholderKeys()
    {
        await _store.SaveAsync(new AppConfig());

        string json = await File.ReadAllTextAsync(_paths.ConfigFilePath);

        // 弱抽查：配置文件里不允许出现任何形如密钥的字段名。
        Assert.DoesNotContain("accessKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sessionToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("privateKey", json, StringComparison.OrdinalIgnoreCase);
    }
}
