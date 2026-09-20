namespace LanRemote.Core.Models;

/// <summary>
/// 显示器标识。
/// </summary>
/// <param name="Value">Win32 <c>HMONITOR</c> 枚举序号。负坐标/多显示器场景下由 <see cref="DisplayInfo"/> 承载几何信息。</param>
public readonly record struct DisplayId(int Value);

/// <summary>
/// 一台显示设备的几何与元信息。
/// </summary>
/// <param name="Id">显示器标识。</param>
/// <param name="Name">设备友好名。</param>
/// <param name="X">虚拟桌面坐标系左上角 X，可能为负数（多显示器）。</param>
/// <param name="Y">虚拟桌面坐标系左上角 Y，可能为负数。</param>
/// <param name="Width">像素宽度。</param>
/// <param name="Height">像素高度。</param>
/// <param name="IsPrimary">是否主显示器。</param>
/// <remarks>
/// 坐标使用 Windows 虚拟桌面坐标系，允许负坐标；
/// 采集实现必须直接处理负坐标，不能假设原点为 (0,0)（见 <c>03_ARCHITECTURE.md</c> 第 6 节）。
/// </remarks>
public sealed record DisplayInfo(
    DisplayId Id,
    string Name,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsPrimary)
{
    /// <summary>右侧边界的虚拟桌面 X 坐标。</summary>
    public int Right => X + Width;

    /// <summary>下侧边界的虚拟桌面 Y 坐标。</summary>
    public int Bottom => Y + Height;
}
