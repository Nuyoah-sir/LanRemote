using LanRemote.Core.Models;
using Xunit;

namespace LanRemote.Core.Tests;

/// <summary>
/// 显示器几何测试，重点是多显示器负坐标（03_ARCHITECTURE.md 第 6 节）。
/// </summary>
public sealed class DisplayInfoTests
{
    [Fact]
    public void RightAndBottom_AreComputedFromOrigin()
    {
        DisplayInfo display = new(new DisplayId(2), "DISPLAY2", X: 1920, Y: -300, Width: 2560, Height: 1440, IsPrimary: false);

        Assert.Equal(4480, display.Right);
        Assert.Equal(1140, display.Bottom);
    }

    [Fact]
    public void SecondaryDisplayOnLeft_HasNegativeOrigin()
    {
        DisplayInfo display = new(new DisplayId(1), "LEFT", X: -1920, Y: 0, Width: 1920, Height: 1080, IsPrimary: false);

        Assert.True(display.X < 0);
        Assert.Equal(0, display.Right);
    }

    [Fact]
    public void RecordsWithSameValues_AreEqual()
    {
        DisplayInfo a = new(new DisplayId(1), "A", 0, 0, 1920, 1080, true);
        DisplayInfo b = new(new DisplayId(1), "A", 0, 0, 1920, 1080, true);

        Assert.Equal(a, b);
    }

    [Fact]
    public void DisplayId_IsValueTypeAndComparable()
    {
        Assert.Equal(new DisplayId(3), new DisplayId(3));
        Assert.NotEqual(new DisplayId(3), new DisplayId(4));
    }
}
