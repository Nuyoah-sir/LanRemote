using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LanRemote.Capture;

// 每个后端独立持有接缝；仅 BitBlt/CreateDIBSection 有扩展错误合同，失败处立即保存错误码。
// 其余 API 以返回值报告失败，不把线程残留的 LastError 猜作本次错误。
internal interface IGdiNative
{
    bool EnumDisplayDevices(uint index, out GdiDisplayDevice device);
    GdiDevMode GetDisplayMode(string deviceName);
    nint CreateSourceDc(string deviceName);
    nint CreateMemoryDc(nint source);
    nint CreateDibSection(nint dc, ref GdiBitmapInfo info, out nint bits);
    nint SelectObject(nint dc, nint value);
    void BitBlt(nint destination, int width, int height, nint source, uint operation);
    void Flush();
    void CopyPixels(nint bits, byte[] destination, int length);
    void DeleteObject(nint value);
    void DeleteDc(nint dc);
}

internal sealed class GdiNative : IGdiNative
{
    internal const uint AttachedToDesktop = 0x00000001;
    internal const uint PrimaryDevice = 0x00000004;
    internal const uint CopyOperation = 0x00CC0020 | 0x40000000; // SRCCOPY | CAPTUREBLT

    public bool EnumDisplayDevices(uint index, out GdiDisplayDevice device)
    {
        device = new GdiDisplayDevice { Size = (uint)Marshal.SizeOf<GdiDisplayDevice>() };
        // 此 API 的 FALSE 也表示正常枚举结束，没有可用的扩展错误合同。
        return NativeMethods.EnumDisplayDevicesW(null, index, ref device, 0);
    }

    public GdiDevMode GetDisplayMode(string deviceName)
    {
        var mode = new GdiDevMode { Size = checked((ushort)Marshal.SizeOf<GdiDevMode>()) };
        if (!NativeMethods.EnumDisplaySettingsExW(deviceName, -1, ref mode, 0))
        {
            throw new InvalidOperationException("EnumDisplaySettingsExW 读取当前物理模式失败（无扩展错误码合同）。");
        }

        return mode;
    }

    public nint CreateSourceDc(string deviceName)
    {
        nint dc = NativeMethods.CreateDCW("DISPLAY", deviceName, null, 0);
        if (dc == 0)
        {
            throw new InvalidOperationException("CreateDCW(DISPLAY) 失败（无扩展错误码合同）。");
        }

        return dc;
    }

    public nint CreateMemoryDc(nint source)
    {
        nint dc = NativeMethods.CreateCompatibleDC(source);
        if (dc == 0)
        {
            throw new InvalidOperationException("CreateCompatibleDC 失败（无扩展错误码合同）。");
        }

        return dc;
    }

    public nint CreateDibSection(nint dc, ref GdiBitmapInfo info, out nint bits)
    {
        nint bitmap = NativeMethods.CreateDIBSection(dc, ref info, 0, out bits, 0, 0);
        if (bitmap == 0)
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, "CreateDIBSection 失败。");
        }

        return bitmap;
    }

    public nint SelectObject(nint dc, nint value)
    {
        nint previous = NativeMethods.SelectObject(dc, value);
        if (previous == 0 || previous == -1)
        {
            throw new InvalidOperationException("SelectObject 失败（无扩展错误码合同）。");
        }

        return previous;
    }

    public void BitBlt(nint destination, int width, int height, nint source, uint operation)
    {
        // DISPLAY 设备 DC 使用设备本地坐标，而不是虚拟桌面的 X/Y。
        if (!NativeMethods.BitBlt(destination, 0, 0, width, height, source, 0, 0, operation))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, "BitBlt 失败。");
        }
    }

    public void Flush()
    {
        if (!NativeMethods.GdiFlush())
        {
            throw new InvalidOperationException("GdiFlush 失败（无扩展错误码合同）。");
        }
    }

    public void CopyPixels(nint bits, byte[] destination, int length) =>
        Marshal.Copy(bits, destination, 0, length);

    public void DeleteObject(nint value)
    {
        if (!NativeMethods.DeleteObject(value))
        {
            throw new InvalidOperationException("DeleteObject 失败（无扩展错误码合同）。");
        }
    }

    public void DeleteDc(nint dc)
    {
        if (!NativeMethods.DeleteDC(dc))
        {
            throw new InvalidOperationException("DeleteDC 失败（无扩展错误码合同）。");
        }
    }

    private static class NativeMethods
    {
        // 仅有扩展错误合同的声明设置 SetLastError；BOOL 编组与失败返回值检查独立保留。
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplayDevicesW(string? device, uint index, ref GdiDisplayDevice display, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplaySettingsExW(string deviceName, int modeNumber, ref GdiDevMode mode, uint flags);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern nint CreateDCW(string driver, string device, string? output, nint mode);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        internal static extern nint CreateCompatibleDC(nint dc);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        internal static extern nint CreateDIBSection(nint dc, ref GdiBitmapInfo info, uint usage, out nint bits, nint section, uint offset);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        internal static extern nint SelectObject(nint dc, nint value);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GdiFlush();

        [DllImport("gdi32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteObject(nint value);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteDC(nint dc);
    }
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct GdiDisplayDevice
{
    internal uint Size;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string DeviceName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string DeviceString;
    internal uint StateFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string DeviceId;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string DeviceKey;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct GdiDevMode
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string DeviceName;
    internal ushort SpecVersion;
    internal ushort DriverVersion;
    internal ushort Size;
    internal ushort DriverExtra;
    internal uint Fields;
    // DEVMODEW 的打印/显示联合体取显示分支，恰好 16 字节。
    internal int PositionX;
    internal int PositionY;
    internal uint DisplayOrientation;
    internal uint DisplayFixedOutput;
    internal short Color;
    internal short Duplex;
    internal short YResolution;
    internal short TTOption;
    internal short Collate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string FormName;
    internal ushort LogPixels;
    internal uint BitsPerPel;
    internal uint PelsWidth;
    internal uint PelsHeight;
    internal uint DisplayFlags;
    internal uint DisplayFrequency;
    internal uint IcmMethod;
    internal uint IcmIntent;
    internal uint MediaType;
    internal uint DitherType;
    internal uint Reserved1;
    internal uint Reserved2;
    internal uint PanningWidth;
    internal uint PanningHeight;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GdiBitmapInfoHeader
{
    internal uint Size;
    internal int Width;
    internal int Height;
    internal ushort Planes;
    internal ushort BitCount;
    internal uint Compression;
    internal uint SizeImage;
    internal int XPelsPerMeter;
    internal int YPelsPerMeter;
    internal uint ClrUsed;
    internal uint ClrImportant;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GdiBitmapInfo
{
    internal GdiBitmapInfoHeader Header;
    internal uint Colors;

    internal static GdiBitmapInfo Create(int width, int height, int length) => new()
    {
        Header = new GdiBitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<GdiBitmapInfoHeader>(),
            Width = width,
            Height = checked(-height),
            Planes = 1,
            BitCount = 32,
            Compression = 0, // BI_RGB，top-down BGRX。
            SizeImage = checked((uint)length),
        },
    };
}
