using LanRemote.Core.Models;
using Xunit;

namespace LanRemote.Core.Tests;

/// <summary>
/// 指针坐标映射测试。
/// </summary>
/// <remarks>
/// 对应 05_UI_UX_SPEC.md 第 6 节：letterbox 黑边不得参与归一化换算，坐标必须 clamp 到 [0,1]。
/// </remarks>
public sealed class NormalizedPointTests
{
    [Theory]
    [InlineData(-5.0, 0.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(9.9, 1.0)]
    public void Clamp_ConstrainsToUnitRange(double input, double expected)
    {
        NormalizedPoint point = NormalizedPoint.Clamp(input, input);

        Assert.Equal(expected, point.X);
        Assert.Equal(expected, point.Y);
    }

    [Fact]
    public void FromImageRect_MapsCenterToHalf()
    {
        NormalizedPoint point = NormalizedPoint.FromImageRect(
            pixelX: 700,
            pixelY: 300,
            imageX: 200,
            imageY: 50,
            imageWidth: 1000,
            imageHeight: 500);

        Assert.Equal(0.5, point.X);
        Assert.Equal(0.5, point.Y);
    }

    [Fact]
    public void FromImageRect_IgnoresLetterboxOffsets()
    {
        // 黑边宽度为 100px：图像左上角位于 x=100 处。
        // 指针正好落在图像左边缘 => 归一化为 0，而不是 (100-0)/1100。
        NormalizedPoint point = NormalizedPoint.FromImageRect(
            pixelX: 100,
            pixelY: 20,
            imageX: 100,
            imageY: 20,
            imageWidth: 800,
            imageHeight: 400);

        Assert.Equal(0.0, point.X);
        Assert.Equal(0.0, point.Y);
    }

    [Fact]
    public void FromImageRect_ClampsPointerOutsideImageArea()
    {
        NormalizedPoint left = NormalizedPoint.FromImageRect(0, 0, 100, 20, 800, 400);
        NormalizedPoint right = NormalizedPoint.FromImageRect(9999, 9999, 100, 20, 800, 400);

        Assert.Equal(0.0, left.X);
        Assert.Equal(1.0, right.X);
        Assert.Equal(1.0, right.Y);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void FromImageRect_RejectsNonPositiveWidth(double width)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => NormalizedPoint.FromImageRect(0, 0, 0, 0, width, 100));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void FromImageRect_RejectsNonPositiveHeight(double height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => NormalizedPoint.FromImageRect(0, 0, 0, 0, 100, height));
    }
}
