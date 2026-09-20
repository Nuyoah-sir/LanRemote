namespace LanRemote.Core.Models;

/// <summary>
/// 归一化坐标，取值必须落在 [0, 1]。
/// </summary>
/// <param name="X">横向归一化坐标。</param>
/// <param name="Y">纵向归一化坐标。</param>
/// <remarks>
/// 输入消息一律使用归一化坐标，绝不直接传输客户端像素（见 <c>04_PROTOCOL_AND_SECURITY.md</c> 第 12 节）。
/// 服务端收到后仍必须再次 clamp，不能信任客户端。
/// </remarks>
public readonly record struct NormalizedPoint(double X, double Y)
{
    /// <summary>把任意数值钳制到 [0, 1] 后构造。</summary>
    /// <param name="x">原始横向坐标。</param>
    /// <param name="y">原始纵向坐标。</param>
    /// <returns>合法的归一化坐标。</returns>
    public static NormalizedPoint Clamp(double x, double y) =>
        new(Math.Clamp(x, 0.0, 1.0), Math.Clamp(y, 0.0, 1.0));

    /// <summary>把像素坐标按图像矩形换算为归一化坐标。</summary>
    /// <param name="pixelX">指针 X。</param>
    /// <param name="pixelY">指针 Y。</param>
    /// <param name="imageX">图像区域左上角 X（不含 letterbox 黑边）。</param>
    /// <param name="imageY">图像区域左上角 Y。</param>
    /// <param name="imageWidth">图像区域宽度，必须大于 0。</param>
    /// <param name="imageHeight">图像区域高度，必须大于 0。</param>
    /// <returns>合法归一化坐标。</returns>
    public static NormalizedPoint FromImageRect(
        double pixelX,
        double pixelY,
        double imageX,
        double imageY,
        double imageWidth,
        double imageHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(imageWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(imageHeight);

        double nx = (pixelX - imageX) / imageWidth;
        double ny = (pixelY - imageY) / imageHeight;
        return Clamp(nx, ny);
    }
}

/// <summary>
/// 鼠标按键标识。
/// </summary>
public enum MouseButton : byte
{
    /// <summary>左键。</summary>
    Left = 0,

    /// <summary>右键。</summary>
    Right = 1,

    /// <summary>中键。</summary>
    Middle = 2,
}

/// <summary>
/// 鼠标按键事件。
/// </summary>
/// <param name="Button">按键。</param>
/// <param name="IsDown"><see langword="true"/> 表示按下，否则为抬起。</param>
/// <param name="Point">按下瞬间的位置。</param>
public sealed record MouseButtonEvent(MouseButton Button, bool IsDown, NormalizedPoint Point);

/// <summary>
/// 滚轮事件。
/// </summary>
/// <param name="Delta">滚轮增量，正数为向前滚动。</param>
/// <param name="IsHorizontal">是否为横向滚轮。</param>
/// <param name="Point">滚动时指针位置。</param>
public sealed record MouseWheelEvent(int Delta, bool IsHorizontal, NormalizedPoint Point);

/// <summary>
/// 键盘事件。
/// </summary>
/// <param name="VirtualKey">Windows 虚拟键码。</param>
/// <param name="ScanCode">扫描码。</param>
/// <param name="IsKeyDown">是否为按下事件。</param>
/// <param name="IsExtendedKey">是否为扩展键。</param>
/// <param name="KeyValue">对应的字符码，可能为 0；该字段仅用于注入，不参与日志。</param>
/// <remarks>
/// 安全约束：服务端不得构造 SAS / Ctrl+Alt+Del；键盘文本内容默认永不写入日志
/// （见 <c>04_PROTOCOL_AND_SECURITY.md</c> 第 17 节）。
/// </remarks>
public sealed record KeyboardEvent(
    ushort VirtualKey,
    ushort ScanCode,
    bool IsKeyDown,
    bool IsExtendedKey,
    char KeyValue);
