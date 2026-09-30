using System.Collections.Concurrent;
using LanRemote.Capture;
using Xunit;

namespace LanRemote.Capture.Tests;

// 所有句柄和像素均为合成值；这个接缝不调用任何桌面 API。
internal sealed class GdiTestNative : IGdiNative
{
    internal const int PrimaryIndex = 2;
    internal static readonly nint Source = 11;
    internal static readonly nint Memory = 22;
    internal static readonly nint Bitmap = 33;
    internal static readonly nint Previous = 44;
    internal static readonly nint Bits = 55;

    internal readonly ConcurrentQueue<string> Calls = new();
    internal readonly Dictionary<string, Exception> Failures = new();
    internal readonly HashSet<nint> Owned = new();
    internal GdiDisplayDevice[] Devices =
    [
        Device("SECONDARY", GdiNative.AttachedToDesktop),
        Device("DETACHED", GdiNative.PrimaryDevice),
        Device("SYNTHETIC", GdiNative.AttachedToDesktop | GdiNative.PrimaryDevice),
    ];
    internal GdiDevMode Mode = new()
    {
        PositionX = -120,
        PositionY = -80,
        PelsWidth = 3,
        PelsHeight = 2,
        BitsPerPel = 32,
        DisplayFrequency = 60,
    };
    internal Action<string>? OnCall;
    internal Action<int>? OnEnumeration;
    internal bool NullBits;
    internal bool Selected;
    internal int Enumerations;
    internal GdiBitmapInfo DibInfo;
    internal byte[]? CopiedBuffer;
    internal int CopiedLength;
    internal string? SourceDeviceName;
    internal uint CopyOperation;

    internal static GdiDisplayDevice Device(string name, uint flags) => new()
    {
        DeviceName = name,
        DeviceString = $"合成显示设备 {name}",
        StateFlags = flags,
        DeviceId = $"ID-{name}",
        DeviceKey = $"KEY-{name}",
    };

    internal static byte PixelByte(int offset) => unchecked((byte)(offset * 13 + 7));

    private void Step(string name)
    {
        Calls.Enqueue(name);
        OnCall?.Invoke(name);
        if (Failures.TryGetValue(name, out Exception? error))
        {
            throw error;
        }
    }

    public bool EnumDisplayDevices(uint index, out GdiDisplayDevice device)
    {
        if (index == 0)
        {
            Enumerations++;
            OnEnumeration?.Invoke(Enumerations);
        }

        Step($"Enum:{index}");
        device = index < Devices.Length ? Devices[(int)index] : default;
        return index < Devices.Length;
    }

    public GdiDevMode GetDisplayMode(string deviceName)
    {
        Step("Mode");
        Assert.Contains(Devices, device => device.DeviceName == deviceName);
        return Mode;
    }

    public nint CreateSourceDc(string deviceName)
    {
        Step("CreateSource");
        SourceDeviceName = deviceName;
        Assert.True(Owned.Add(Source));
        return Source;
    }

    public nint CreateMemoryDc(nint source)
    {
        Step("CreateMemory");
        Assert.Equal(Source, source);
        Assert.True(Owned.Add(Memory));
        return Memory;
    }

    public nint CreateDibSection(nint dc, ref GdiBitmapInfo info, out nint bits)
    {
        Step("CreateDib");
        Assert.Equal(Memory, dc);
        DibInfo = info;
        bits = NullBits ? 0 : Bits;
        Assert.True(Owned.Add(Bitmap));
        return Bitmap;
    }

    public nint SelectObject(nint dc, nint value)
    {
        Assert.Equal(Memory, dc);
        if (value == Bitmap)
        {
            Step("Select");
            Assert.False(Selected);
            Selected = true;
            return Previous;
        }

        Assert.Equal(Previous, value);
        Step("Restore");
        Assert.True(Selected);
        Selected = false;
        return Bitmap;
    }

    public void BitBlt(nint destination, int width, int height, nint source, uint operation)
    {
        Step("BitBlt");
        Assert.Equal(Memory, destination);
        Assert.Equal(Source, source);
        Assert.Equal(DibInfo.Header.Width, width);
        Assert.Equal(-DibInfo.Header.Height, height);
        Assert.True(Selected);
        CopyOperation = operation;
    }

    public void Flush() => Step("Flush");

    public void CopyPixels(nint bits, byte[] destination, int length)
    {
        Assert.Equal(Bits, bits);
        CopiedBuffer = destination;
        CopiedLength = length;
        for (int i = 0; i < length; i++)
        {
            destination[i] = PixelByte(i);
        }

        // 先写入标记，再允许阻塞或抛错，以观察迟到 buffer 的清零释放。
        Step("CopyPixels");
    }

    public void DeleteObject(nint value)
    {
        Assert.Equal(Bitmap, value); // 借用对象和 bits 都不允许被删除。
        Assert.False(Selected);
        Step("DeleteBitmap");
        Assert.True(Owned.Remove(Bitmap));
    }

    public void DeleteDc(nint dc)
    {
        Assert.True(dc == Memory || dc == Source);
        Step(dc == Memory ? "DeleteMemory" : "DeleteSource");
        Assert.True(Owned.Remove(dc));
        if (dc == Memory)
        {
            Selected = false;
        }
    }
}

/// <summary>隔离共享池观察测试，避免其他测试在断言前复用刚归还的数组。</summary>
[CollectionDefinition(GdiBufferObservationCollection.Name, DisableParallelization = true)]
public sealed class GdiBufferObservationCollection
{
    /// <summary>GDI 测试共享的串行集合名称。</summary>
    public const string Name = "GDI buffer 生命周期观察";
}
