using LanRemote.Core.Models;
using Xunit;

namespace LanRemote.Core.Tests;

/// <summary>
/// 画质设置的边界测试，对应 02_PRODUCT_SPEC.md 第 7 节的取值范围。
/// </summary>
public sealed class VideoQualitySettingsTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(20)]
    [InlineData(30)]
    public void Clamp_KeepsAllowedFps(int fps)
    {
        VideoQualitySettings settings = VideoQualitySettings.Clamp(fps, 0.75, 60, false);

        Assert.Equal(fps, settings.TargetFps);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(45)]
    [InlineData(60)]
    [InlineData(0)]
    public void Clamp_ReplacesDisallowedFpsWithBalanced(int fps)
    {
        VideoQualitySettings settings = VideoQualitySettings.Clamp(fps, 1.0, 60, false);

        Assert.Equal(20, settings.TargetFps);
    }

    [Theory]
    [InlineData(0, 40)]
    [InlineData(39, 40)]
    [InlineData(60, 60)]
    [InlineData(85, 85)]
    [InlineData(86, 85)]
    [InlineData(int.MaxValue, 85)]
    public void Clamp_KeepsJpegQualityWithinRange(int input, int expected)
    {
        VideoQualitySettings settings = VideoQualitySettings.Clamp(15, 0.75, input, false);

        Assert.Equal(expected, settings.JpegQuality);
    }

    [Theory]
    [InlineData(0.50)]
    [InlineData(0.67)]
    [InlineData(0.75)]
    [InlineData(1.00)]
    public void Clamp_KeepsExactAllowedScales(double scale)
    {
        VideoQualitySettings settings = VideoQualitySettings.Clamp(15, scale, 60, false);

        Assert.Equal(scale, settings.Scale, precision: 2);
    }

    [Theory]
    [InlineData(0.60, 0.67)]
    [InlineData(0.90, 1.00)]
    [InlineData(0.10, 0.50)]
    public void Clamp_SnapsToNearestAllowedScale(double input, double expected)
    {
        VideoQualitySettings settings = VideoQualitySettings.Clamp(15, input, 60, false);

        Assert.Equal(expected, settings.Scale, precision: 2);
    }

    [Fact]
    public void Presets_UseDocumentedValues()
    {
        Assert.Equal(30, VideoQualitySettings.LowLatency.TargetFps);
        Assert.Equal(45, VideoQualitySettings.LowLatency.JpegQuality);

        Assert.Equal(0.75, VideoQualitySettings.Balanced.Scale, precision: 2);
        Assert.Equal(60, VideoQualitySettings.Balanced.JpegQuality);

        Assert.Equal(1.00, VideoQualitySettings.HighQuality.Scale, precision: 2);
        Assert.Equal(75, VideoQualitySettings.HighQuality.JpegQuality);
    }
}
