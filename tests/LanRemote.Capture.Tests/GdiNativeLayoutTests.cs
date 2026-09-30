using System.Reflection;
using System.Runtime.InteropServices;
using LanRemote.Capture;
using Xunit;

namespace LanRemote.Capture.Tests;

/// <summary>通过实际 Marshal 布局检查 Win32 Unicode 结构尺寸和关键偏移。</summary>
public sealed class GdiNativeLayoutTests
{
    /// <summary>只有提供扩展错误合同的 P/Invoke 声明捕获 LastError，不调用实时桌面 API。</summary>
    [Theory]
    [InlineData("EnumDisplayDevicesW", false)]
    [InlineData("EnumDisplaySettingsExW", false)]
    [InlineData("CreateDCW", false)]
    [InlineData("CreateCompatibleDC", false)]
    [InlineData("SelectObject", false)]
    [InlineData("GdiFlush", false)]
    [InlineData("DeleteObject", false)]
    [InlineData("DeleteDC", false)]
    [InlineData("BitBlt", true)]
    [InlineData("CreateDIBSection", true)]
    public void NativeDeclarations_CaptureLastErrorOnlyWithAnExtendedErrorContract(string methodName, bool expected)
    {
        Type? nativeMethods = typeof(GdiNative).GetNestedType("NativeMethods", BindingFlags.NonPublic);
        Assert.NotNull(nativeMethods);
        MethodInfo? method = nativeMethods.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        DllImportAttribute? declaration = method.GetCustomAttribute<DllImportAttribute>();
        Assert.NotNull(declaration);
        Assert.Equal(expected, declaration.SetLastError);
    }

    /// <summary>结构大小必须与 Win32 的 DISPLAY_DEVICEW、DEVMODEW 和 DIB 合同一致。</summary>
    [Theory]
    [InlineData(typeof(GdiDisplayDevice), 840)]
    [InlineData(typeof(GdiDevMode), 220)]
    [InlineData(typeof(GdiBitmapInfoHeader), 40)]
    [InlineData(typeof(GdiBitmapInfo), 44)]
    public void Structures_HaveExpectedMarshaledSize(Type type, int expected)
    {
        Assert.Equal(expected, Marshal.SizeOf(type));
    }

    /// <summary>字符串宽度、DEVMODE 联合体和尾部对齐不能只靠总大小碰巧通过。</summary>
    [Theory]
    [InlineData(typeof(GdiDisplayDevice), nameof(GdiDisplayDevice.DeviceName), 4)]
    [InlineData(typeof(GdiDisplayDevice), nameof(GdiDisplayDevice.StateFlags), 324)]
    [InlineData(typeof(GdiDisplayDevice), nameof(GdiDisplayDevice.DeviceKey), 584)]
    [InlineData(typeof(GdiDevMode), nameof(GdiDevMode.Size), 68)]
    [InlineData(typeof(GdiDevMode), nameof(GdiDevMode.PositionX), 76)]
    [InlineData(typeof(GdiDevMode), nameof(GdiDevMode.DisplayOrientation), 84)]
    [InlineData(typeof(GdiDevMode), nameof(GdiDevMode.FormName), 102)]
    [InlineData(typeof(GdiDevMode), nameof(GdiDevMode.BitsPerPel), 168)]
    [InlineData(typeof(GdiDevMode), nameof(GdiDevMode.PelsWidth), 172)]
    [InlineData(typeof(GdiDevMode), nameof(GdiDevMode.PanningHeight), 216)]
    [InlineData(typeof(GdiBitmapInfoHeader), nameof(GdiBitmapInfoHeader.BitCount), 14)]
    [InlineData(typeof(GdiBitmapInfo), nameof(GdiBitmapInfo.Colors), 40)]
    public void Structures_HaveExpectedFieldOffsets(Type type, string field, int expected)
    {
        Assert.Equal(expected, Marshal.OffsetOf(type, field).ToInt32());
    }
}
