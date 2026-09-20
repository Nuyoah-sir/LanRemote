using System.IO;
using LanRemote.Core.Configuration;
using LanRemote.Core.Infrastructure;
using Xunit;

namespace LanRemote.IntegrationTests;

/// <summary>
/// 配置持久化针对真实文件系统的集成测试。
/// </summary>
/// <remarks>
/// 与单元测试的区别：这里走完整的一次 <c>%TEMP%</c> 落地 + 重读闭环，
/// 验证目录创建、文件覆盖、损坏恢复等在真实磁盘上的行为。
/// </remarks>
public sealed class ConfigurationFileLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"LanRemote-It-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AppConfigStore CreateStore() => new(AppPaths.FromRootDirectory(_root));

    [Fact]
    public async Task ColdStartThenModifyThenRestart_PersistsChanges()
    {
        AppConfigStore firstRun = CreateStore();

        AppConfig initial = await firstRun.LoadAsync();
        Assert.True(initial.RequireLocalApprovalForUnknownController);

        await firstRun.SaveAsync(initial with { AllowControl = false, MaxViewSessions = 5 });

        // 模拟进程重启：新 store 实例指向同一目录。
        AppConfigStore secondRun = CreateStore();
        AppConfig reload = await secondRun.LoadAsync();

        Assert.False(reload.AllowControl);
        Assert.Equal(5, reload.MaxViewSessions);

        // 未被修改的字段保持默认值，不能被写坏。
        Assert.True(reload.AllowViewing);
        Assert.Equal(45872, reload.DiscoveryPort);
    }

    [Fact]
    public async Task OverwritesExistingFileWithoutLeavingBackups()
    {
        AppConfigStore store = CreateStore();

        await store.SaveAsync(new AppConfig { DefaultQualityPreset = "LowLatency" });
        await store.SaveAsync(new AppConfig { DefaultQualityPreset = "HighQuality" });

        AppConfig reload = await store.LoadAsync();
        Assert.Equal("HighQuality", reload.DefaultQualityPreset);

        AppPaths paths = AppPaths.FromRootDirectory(_root);
        string[] files = Directory.GetFiles(paths.RootDirectory);

        Assert.Single(files);
        Assert.EndsWith("config.json", files[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorruptedConfig_SurvivesAndCanBeRepaired()
    {
        AppPaths paths = AppPaths.FromRootDirectory(_root);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.ConfigFilePath, "{{{broken");

        AppConfigStore store = CreateStore();
        AppConfig recovered = await store.LoadAsync();

        Assert.True(recovered.AllowDiscovery);

        // 修复：写回一份合法配置后应能正常读回。
        await store.SaveAsync(recovered with { AllowDiscovery = false });

        AppConfig repaired = await store.LoadAsync();
        Assert.False(repaired.AllowDiscovery);
    }
}
