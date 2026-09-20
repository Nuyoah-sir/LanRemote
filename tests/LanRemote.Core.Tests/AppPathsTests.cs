using System.IO;
using LanRemote.Core.Infrastructure;
using Xunit;

namespace LanRemote.Core.Tests;

/// <summary>
/// <see cref="AppPaths"/> 的路径约定测试。
/// </summary>
/// <remarks>
/// 对应 02_PRODUCT_SPEC.md 第 9 节：配置、秘密、日志必须落在固定的三个位置，
/// 且秘密文件独立于 config.json。
/// </remarks>
public sealed class AppPathsTests
{
    [Fact]
    public void DefaultRootDirectoryPath_UsesLocalAppData()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppPaths.ApplicationFolderName);

        Assert.Equal(expected, AppPaths.DefaultRootDirectoryPath());
    }

    [Fact]
    public void Default_Instance_UsesDefaultRoot()
    {
        Assert.Equal(AppPaths.DefaultRootDirectoryPath(), AppPaths.Default.RootDirectory);
    }

    [Fact]
    public void Paths_AreLocatedUnderRootDirectory()
    {
        AppPaths paths = AppPaths.FromRootDirectory(@"C:\Temp\LanRemoteTestRoot");

        Assert.Equal(@"C:\Temp\LanRemoteTestRoot\config.json", paths.ConfigFilePath);
        Assert.Equal(@"C:\Temp\LanRemoteTestRoot\secrets.bin", paths.SecretsFilePath);
        Assert.Equal(@"C:\Temp\LanRemoteTestRoot\logs", paths.LogsDirectory);
    }

    [Fact]
    public void SecretsFile_IsDifferentFromConfigFile()
    {
        AppPaths paths = AppPaths.FromRootDirectory(@"C:\Temp\LanRemoteTestRoot");

        // 秘密绝不写入 config.json —— 至少在路径层面两者必须分离。
        Assert.NotEqual(paths.ConfigFilePath, paths.SecretsFilePath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void FromRootDirectory_RejectsBlankInput(string? rootDirectory)
    {
        // 空串/空白抛 ArgumentException；null 抛其子类 ArgumentNullException，
        // 因此这里断言「ArgumentException 家族」而不是精确类型。
        Assert.ThrowsAny<ArgumentException>(() => AppPaths.FromRootDirectory(rootDirectory!));
    }

    [Fact]
    public void EnsureCreated_CreatesRootAndLogsDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), $"LanRemote-{Guid.NewGuid():N}");
        AppPaths paths = AppPaths.FromRootDirectory(root);

        try
        {
            Assert.False(Directory.Exists(root));

            paths.EnsureCreated();

            Assert.True(Directory.Exists(paths.RootDirectory));
            Assert.True(Directory.Exists(paths.LogsDirectory));

            // 幂等：重复调用不应抛异常。
            paths.EnsureCreated();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
