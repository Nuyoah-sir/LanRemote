using LanRemote.Core.Models;

namespace LanRemote.Capture;

/// <summary>只读枚举当前桌面附着显示设备的物理几何；不提供采集或远端切屏能力。</summary>
/// <remarks>
/// 独立于默认主屏采集路径，也供内部显式多屏采集使用。设备 ID 是本次 EnumDisplayDevicesW 的原始索引，
/// 不是 HMONITOR、持久身份或未来切屏授权。两遍一致仅检测观察到的变化，
/// 不承诺原子拓扑快照、检测 ABA、之后不热插拔或真实混合 DPI 已验证；不改变 DPI、网络或权限。
/// </remarks>
public sealed class GdiDisplayCatalog
{
    // 包括被过滤项；额外一次查询只用于判断枚举是否结束，禁止截断后返回部分成功。
    private const uint MaxDeviceEntries = 256;
    private const uint MirroringDriver = 0x00000008;
    private const uint GeometryFields = 0x00000020 | 0x00080000 | 0x00100000;
    private readonly IGdiDisplayNative _native;

    /// <summary>创建仅使用设备枚举和当前模式读取 API 的目录。</summary>
    public GdiDisplayCatalog() : this(new GdiNative())
    {
    }

    internal GdiDisplayCatalog(IGdiDisplayNative native)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
    }

    /// <summary>返回不可变的本次显示设备列表，保留系统枚举顺序和物理负坐标。</summary>
    /// <returns>只读列表；大屏元数据可列出，但不代表它符合现有帧采集/解码预算。</returns>
    /// <exception cref="NotSupportedException">当前桌面没有附着显示设备。</exception>
    /// <exception cref="InvalidOperationException">身份、主屏、几何无效，枚举超限或两次观察不一致。</exception>
    public IReadOnlyList<DisplayInfo> GetDisplays() =>
        Array.AsReadOnly(ReadStableSnapshot().Select(item => item.Info).ToArray());

    /// <summary>两遍完整观察一致后返回只读快照，保留完整身份、状态、模式和枚举顺序。</summary>
    /// <remarks>不暴露底层数组；观察一致不承诺原子性、检测 ABA 或未来设备身份。</remarks>
    internal IReadOnlyList<DisplayObservation> ReadStableSnapshot()
    {
        DisplayObservation[] first = ReadSnapshot();
        DisplayObservation[] second = ReadSnapshot();
        if (!first.SequenceEqual(second))
        {
            throw new InvalidOperationException("两次只读枚举的显示设备或物理模式不一致；未交付列表，请重新枚举。");
        }

        return Array.AsReadOnly(first);
    }

    private DisplayObservation[] ReadSnapshot()
    {
        var displays = new List<DisplayObservation>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (uint index = 0; ; index++)
        {
            if (!_native.EnumDisplayDevices(index, out GdiDisplayDevice device)) break;
            if (index >= MaxDeviceEntries)
            {
                throw new InvalidOperationException("显示设备枚举超过256项；不返回截断列表。");
            }
            if ((device.StateFlags & GdiNative.AttachedToDesktop) == 0 ||
                (device.StateFlags & MirroringDriver) != 0) continue;

            if (string.IsNullOrWhiteSpace(device.DeviceName) || !names.Add(device.DeviceName))
            {
                throw new InvalidOperationException("附着显示设备名称为空或重复；无法确定设备身份。");
            }

            GdiDevMode mode = _native.GetDisplayMode(device.DeviceName);
            if ((mode.Fields & GeometryFields) != GeometryFields ||
                mode.PelsWidth is 0 or > int.MaxValue || mode.PelsHeight is 0 or > int.MaxValue ||
                (long)mode.PositionX + mode.PelsWidth > int.MaxValue ||
                (long)mode.PositionY + mode.PelsHeight > int.MaxValue)
            {
                throw new InvalidOperationException("显示模式缺少有效物理几何字段或坐标范围不可表示；未交付列表。");
            }

            // 这里只存元数据，不分配任何屏幕像素，也不改变 M5 的16,777,216像素预算。
            var info = new DisplayInfo(new DisplayId((int)index),
                string.IsNullOrWhiteSpace(device.DeviceString) ? device.DeviceName : device.DeviceString,
                mode.PositionX, mode.PositionY, (int)mode.PelsWidth, (int)mode.PelsHeight,
                (device.StateFlags & GdiNative.PrimaryDevice) != 0);
            displays.Add(new DisplayObservation(info, device.DeviceName, device.DeviceId, device.DeviceKey,
                device.DeviceString, device.StateFlags, mode.Fields, mode.DisplayOrientation,
                mode.DisplayFixedOutput, mode.BitsPerPel, mode.DisplayFrequency, mode.DisplayFlags,
                mode.PanningWidth, mode.PanningHeight));
        }

        if (displays.Count == 0)
        {
            throw new NotSupportedException("没有可枚举的当前桌面附着显示设备。");
        }
        if (displays.Count(display => display.Info.IsPrimary) != 1)
        {
            throw new InvalidOperationException("附着显示设备必须恰好包含一个主显示器；未交付列表。");
        }
        return displays.ToArray();
    }

    internal sealed record DisplayObservation(DisplayInfo Info, string DeviceName, string DeviceId,
        string DeviceKey, string FriendlyName, uint StateFlags, uint ModeFields, uint Orientation,
        uint FixedOutput, uint BitsPerPixel, uint Frequency, uint Flags, uint PanningWidth, uint PanningHeight);
}
