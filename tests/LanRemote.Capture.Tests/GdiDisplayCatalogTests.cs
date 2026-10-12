using System.Collections;
using System.ComponentModel;
using LanRemote.Capture;
using LanRemote.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace LanRemote.Capture.Tests;

/// <summary>以无像素、无 DC 能力的接缝验证元数据目录，另保留一个真实桌面只读事实测试。</summary>
public sealed class GdiDisplayCatalogTests(ITestOutputHelper output)
{
    private const uint Attached = 0x00000001;
    private const uint Primary = 0x00000004;
    private const uint Mirroring = 0x00000008;
    private const uint RequiredFields = 0x00000020 | 0x00080000 | 0x00100000;

    /// <summary>原始索引及顺序不因过滤或主屏位置重排，负坐标和竖屏不做交换或缩放。</summary>
    [Fact]
    public void GetDisplays_PreservesRawOrderIdsAndPhysicalGeometryAcrossCompletePasses()
    {
        var native = new DisplayOnlyNative();
        IReadOnlyList<DisplayInfo> displays = new GdiDisplayCatalog(native).GetDisplays();

        Assert.Collection(displays,
            display => Assert.Equal(new DisplayInfo(new DisplayId(1), "合成显示设备 LEFT-PORTRAIT",
                -1080, -240, 1080, 1920, false), display),
            display => Assert.Equal(new DisplayInfo(new DisplayId(3), "合成显示设备 MAIN",
                0, 0, 2560, 1440, true), display),
            display => Assert.Equal(new DisplayInfo(new DisplayId(4), "合成显示设备 RIGHT",
                2560, 180, 1920, 1080, false), display));
        Assert.Equal(0, displays[0].Right);
        Assert.Equal(1680, displays[0].Bottom);
        Assert.Equal(4480, displays[2].Right);
        Assert.Equal(1260, displays[2].Bottom);
        Assert.Equal(2, native.Passes);
        for (int pass = 1; pass <= 2; pass++)
        {
            Assert.Equal(Enumerable.Range(0, 7).Select(index => (uint)index).ToArray(),
                native.Enumerated.Where(call => call.Pass == pass).Select(call => call.Index).ToArray());
            Assert.Equal(new[] { "LEFT-PORTRAIT", "MAIN", "RIGHT" },
                native.ModeRequests.Where(call => call.Pass == pass).Select(call => call.DeviceName).ToArray());
        }
    }

    /// <summary>友好名缺失时只回退设备名称，不改写原始索引。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void GetDisplays_UsesDeviceNameWhenFriendlyNameIsMissing(string? friendlyName)
    {
        var native = new DisplayOnlyNative();
        native.Entries[1].Device.DeviceString = friendlyName!;

        DisplayInfo display = new GdiDisplayCatalog(native).GetDisplays()[0];

        Assert.Equal("LEFT-PORTRAIT", display.Name);
        Assert.Equal(new DisplayId(1), display.Id);
        Assert.False(display.IsPrimary);
    }

    /// <summary>空列表、脱离设备及镜像设备均不能被当作可用桌面。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetDisplays_RejectsDesktopWithoutAttachedNonMirroringDevices(bool empty)
    {
        var native = new DisplayOnlyNative();
        native.Entries = empty ? [] : [native.Entries[0], native.Entries[2], native.Entries[5]];

        Assert.Throws<NotSupportedException>(() => new GdiDisplayCatalog(native).GetDisplays());

        Assert.Empty(native.ModeRequests);
        Assert.Equal(1, native.Passes);
    }

    /// <summary>有附着屏但没有主屏时拒绝目录，不擅自把第一个设备提升为主屏。</summary>
    [Fact]
    public void GetDisplays_RejectsAttachedDevicesWithoutPrimary()
    {
        var native = new DisplayOnlyNative();
        native.Entries[3].Device.StateFlags = Attached;

        Assert.Throws<InvalidOperationException>(() => new GdiDisplayCatalog(native).GetDisplays());
    }

    /// <summary>两个真实附着主屏必须拒绝，不能遇到第一个主屏就停止枚举。</summary>
    [Fact]
    public void GetDisplays_RejectsMultipleAttachedPrimaries()
    {
        var native = new DisplayOnlyNative();
        native.Entries[4].Device.StateFlags |= Primary;

        Assert.Throws<InvalidOperationException>(() => new GdiDisplayCatalog(native).GetDisplays());
    }

    /// <summary>附着设备名称不得为空白，也不能把异常名称传入模式读取。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void GetDisplays_RejectsMissingAttachedDeviceName(string? name)
    {
        var native = new DisplayOnlyNative();
        native.Entries[1].Device.DeviceName = name!;

        Assert.Throws<InvalidOperationException>(() => new GdiDisplayCatalog(native).GetDisplays());

        Assert.Empty(native.ModeRequests);
    }

    /// <summary>设备名称按不区分大小写的序数比较去重，非主屏重复也必须拒绝。</summary>
    [Theory]
    [InlineData("LEFT-PORTRAIT")]
    [InlineData("left-portrait")]
    public void GetDisplays_RejectsDuplicateAttachedDeviceNames(string duplicateName)
    {
        var native = new DisplayOnlyNative();
        native.Entries[4].Device.DeviceName = duplicateName;

        Assert.Throws<InvalidOperationException>(() => new GdiDisplayCatalog(native).GetDisplays());
    }

    /// <summary>被过滤条目的空名称、重复名称和无效模式不能污染附着设备目录。</summary>
    [Fact]
    public void GetDisplays_DoesNotValidateOrReadModesOfFilteredDevices()
    {
        var native = new DisplayOnlyNative();
        native.Entries[0].Device.DeviceName = "";
        native.Entries[2].Device.DeviceName = "MAIN";
        native.Entries[5].Device.DeviceName = "left-portrait";

        IReadOnlyList<DisplayInfo> displays = new GdiDisplayCatalog(native).GetDisplays();

        Assert.Equal(new[] { 1, 3, 4 }, displays.Select(display => display.Id.Value).ToArray());
        Assert.Equal(6, native.ModeRequests.Count);
        Assert.All(native.ModeRequests, call => Assert.Contains(call.DeviceName,
            new[] { "LEFT-PORTRAIT", "MAIN", "RIGHT" }));
    }

    /// <summary>位置、宽度和高度字段均必须有效；数值看似合理也不能弥补缺失字段位。</summary>
    [Theory]
    [InlineData(0u)]
    [InlineData(0x00000020u)]
    [InlineData(0x00080000u)]
    [InlineData(0x00100000u)]
    [InlineData(0x00180000u)]
    [InlineData(0x00100020u)]
    [InlineData(0x00080020u)]
    public void GetDisplays_RejectsMissingRequiredModeFields(uint fields)
    {
        var native = new DisplayOnlyNative();
        native.Entries[1].Mode.Fields = fields;

        Assert.Throws<InvalidOperationException>(() => new GdiDisplayCatalog(native).GetDisplays());
    }

    /// <summary>零尺寸及无法表示成正 int 的尺寸必须拒绝，而不能截断或溢出回绕。</summary>
    [Theory]
    [InlineData(0u, 1u)]
    [InlineData(1u, 0u)]
    [InlineData(2147483648u, 1u)]
    [InlineData(1u, 2147483648u)]
    [InlineData(uint.MaxValue, 1u)]
    [InlineData(1u, uint.MaxValue)]
    public void GetDisplays_RejectsUnrepresentableDimensions(uint width, uint height)
    {
        var native = SingleDisplay(0, 0, width, height);

        Assert.Throws<InvalidOperationException>(() => new GdiDisplayCatalog(native).GetDisplays());
    }

    /// <summary>宽高单独合法但右边界或下边界超过 int 时仍须拒绝。</summary>
    [Theory]
    [InlineData(int.MaxValue, 0, 1u, 1u)]
    [InlineData(0, int.MaxValue, 1u, 1u)]
    [InlineData(2147483637, 0, 11u, 1u)]
    [InlineData(0, 2147483637, 1u, 11u)]
    public void GetDisplays_RejectsRightOrBottomOverflow(int x, int y, uint width, uint height)
    {
        var native = SingleDisplay(x, y, width, height);

        Assert.Throws<InvalidOperationException>(() => new GdiDisplayCatalog(native).GetDisplays());
    }

    /// <summary>只读元数据不沿用像素采集预算，包括超预算大屏和 int 上边界，且保持原尺寸。</summary>
    [Theory]
    [InlineData(1u, 1u)]
    [InlineData(8192u, 8192u)]
    [InlineData(8193u, 1u)]
    [InlineData(1u, 8193u)]
    [InlineData(2147483647u, 1u)]
    [InlineData(1u, 2147483647u)]
    public void GetDisplays_PreservesValidMetadataBeyondCaptureBudget(uint width, uint height)
    {
        var native = SingleDisplay(0, 0, width, height);

        DisplayInfo display = Assert.Single(new GdiDisplayCatalog(native).GetDisplays());

        Assert.Equal(checked((int)width), display.Width);
        Assert.Equal(checked((int)height), display.Height);
        Assert.Equal(checked((int)width), display.Right);
        Assert.Equal(checked((int)height), display.Bottom);
        Assert.Equal(2, native.ModeRequests.Count);
    }

    /// <summary>合法极端坐标及恰好达到 int 边界的几何不会误判溢出。</summary>
    [Theory]
    [InlineData(int.MinValue, int.MinValue, 1u, 1u, -2147483647, -2147483647)]
    [InlineData(2147483646, 2147483646, 1u, 1u, int.MaxValue, int.MaxValue)]
    [InlineData(-1920, -1080, 1920u, 1080u, 0, 0)]
    public void GetDisplays_PreservesValidSignedCoordinateBoundaries(
        int x, int y, uint width, uint height, int right, int bottom)
    {
        DisplayInfo display = Assert.Single(new GdiDisplayCatalog(SingleDisplay(x, y, width, height)).GetDisplays());

        Assert.Equal(x, display.X);
        Assert.Equal(y, display.Y);
        Assert.Equal(right, display.Right);
        Assert.Equal(bottom, display.Bottom);
    }

    /// <summary>第二遍非主屏的完整身份、状态或模式变化均拒绝，不能仅比较对外几何或主屏。</summary>
    [Theory]
    [InlineData("DeviceName")]
    [InlineData("DeviceNameCase")]
    [InlineData("DeviceId")]
    [InlineData("DeviceKey")]
    [InlineData("FriendlyName")]
    [InlineData("StateFlags")]
    [InlineData("Fields")]
    [InlineData("X")]
    [InlineData("Y")]
    [InlineData("Width")]
    [InlineData("Height")]
    [InlineData("Orientation")]
    [InlineData("FixedOutput")]
    [InlineData("BitsPerPixel")]
    [InlineData("Frequency")]
    [InlineData("DisplayFlags")]
    [InlineData("PanningWidth")]
    [InlineData("PanningHeight")]
    public void GetDisplays_RejectsSecondPassIdentityStateOrModeChangeWithoutRetry(string change)
    {
        var native = new DisplayOnlyNative();
        native.OnPass = pass =>
        {
            if (pass != 2) return;
            DisplayEntry entry = native.Entries[1];
            switch (change)
            {
                case "DeviceName": entry.Device.DeviceName = "REPLACEMENT"; break;
                case "DeviceNameCase": entry.Device.DeviceName = "left-portrait"; break;
                case "DeviceId": entry.Device.DeviceId = "REPLACEMENT-ID"; break;
                case "DeviceKey": entry.Device.DeviceKey = "REPLACEMENT-KEY"; break;
                case "FriendlyName": entry.Device.DeviceString = "替换后的友好名称"; break;
                case "StateFlags": entry.Device.StateFlags |= 0x00000010; break;
                case "Fields": entry.Mode.Fields |= 0x00040000; break;
                case "X": entry.Mode.PositionX--; break;
                case "Y": entry.Mode.PositionY--; break;
                case "Width": entry.Mode.PelsWidth++; break;
                case "Height": entry.Mode.PelsHeight++; break;
                case "Orientation": entry.Mode.DisplayOrientation++; break;
                case "FixedOutput": entry.Mode.DisplayFixedOutput++; break;
                case "BitsPerPixel": entry.Mode.BitsPerPel = 24; break;
                case "Frequency": entry.Mode.DisplayFrequency++; break;
                case "DisplayFlags": entry.Mode.DisplayFlags++; break;
                case "PanningWidth": entry.Mode.PanningWidth++; break;
                case "PanningHeight": entry.Mode.PanningHeight++; break;
                default: throw new ArgumentOutOfRangeException(nameof(change));
            }
        };

        Assert.Throws<InvalidOperationException>(() => new GdiDisplayCatalog(native).GetDisplays());

        Assert.Equal(2, native.Passes);
        Assert.Equal(14, native.Enumerated.Count);
        Assert.Equal(6, native.ModeRequests.Count);
    }

    /// <summary>交换顺序、附着集合及主屏归属变化不能被排序归一化掩盖。</summary>
    [Theory]
    [InlineData("SwapOrder")]
    [InlineData("RemoveSecondary")]
    [InlineData("AddSecondary")]
    [InlineData("DetachSecondary")]
    [InlineData("AttachFiltered")]
    [InlineData("MirrorSecondary")]
    [InlineData("MovePrimary")]
    public void GetDisplays_RejectsSecondPassTopologyChangeWithoutRetry(string change)
    {
        var native = new DisplayOnlyNative();
        native.OnPass = pass =>
        {
            if (pass != 2) return;
            switch (change)
            {
                case "SwapOrder":
                    (native.Entries[1], native.Entries[4]) = (native.Entries[4], native.Entries[1]);
                    break;
                case "RemoveSecondary":
                    native.Entries = native.Entries.Where((_, index) => index != 4).ToArray();
                    break;
                case "AddSecondary":
                    native.Entries = [.. native.Entries, Entry("ADDED", Attached, 4480, 0, 800, 600)];
                    break;
                case "DetachSecondary": native.Entries[1].Device.StateFlags = 0; break;
                case "AttachFiltered": native.Entries[0] = Entry("DETACHED", Attached, -1920, 0, 800, 600); break;
                case "MirrorSecondary": native.Entries[1].Device.StateFlags |= Mirroring; break;
                case "MovePrimary":
                    native.Entries[1].Device.StateFlags |= Primary;
                    native.Entries[3].Device.StateFlags &= ~Primary;
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(change));
            }
        };

        Assert.Throws<InvalidOperationException>(() => new GdiDisplayCatalog(native).GetDisplays());

        Assert.Equal(2, native.Passes);
        Assert.Equal((uint)native.Entries.Length, native.Enumerated[^1].Index);
    }

    /// <summary>每遍恰好 256 个原始设备合法；索引 256 仅作为结束探测，过滤项也占枚举额度。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetDisplays_AllowsExactly256RawDevicesAndChecksEndOnBothPasses(bool allAttached)
    {
        var native = new DisplayOnlyNative { Entries = BoundaryEntries(allAttached) };

        IReadOnlyList<DisplayInfo> displays = new GdiDisplayCatalog(native).GetDisplays();

        Assert.Equal(allAttached ? 256 : 1, displays.Count);
        Assert.Equal(new DisplayId(255), Assert.Single(displays, display => display.IsPrimary).Id);
        Assert.Equal(allAttached ? Enumerable.Range(0, 256).ToArray() : new[] { 255 },
            displays.Select(display => display.Id.Value).ToArray());
        Assert.Equal(2, native.Passes);
        Assert.Equal(514, native.Enumerated.Count);
        Assert.Equal(allAttached ? 512 : 2, native.ModeRequests.Count);
        for (int pass = 1; pass <= 2; pass++)
        {
            Assert.Equal(Enumerable.Range(0, 257).Select(index => (uint)index).ToArray(),
                native.Enumerated.Where(call => call.Pass == pass).Select(call => call.Index).ToArray());
        }
    }

    /// <summary>任一遍的第 257 个原始条目都立即失败，不读取其模式或探测更大索引，也不重试。</summary>
    [Theory]
    [InlineData(1, Attached)]
    [InlineData(2, Attached)]
    [InlineData(1, 0u)]
    [InlineData(2, 0u)]
    [InlineData(1, Attached | Mirroring)]
    [InlineData(2, Attached | Mirroring)]
    public void GetDisplays_Rejects257thRawDeviceBeforeModeRead(int overflowPass, uint overflowFlags)
    {
        var native = new DisplayOnlyNative { Entries = BoundaryEntries(false) };
        native.OnPass = pass =>
        {
            if (pass == overflowPass)
            {
                native.Entries = [.. native.Entries, Entry("OVER-LIMIT", overflowFlags, 0, 0, 1, 1)];
            }
        };

        Assert.Throws<InvalidOperationException>(() => new GdiDisplayCatalog(native).GetDisplays());

        Assert.Equal(overflowPass, native.Passes);
        Assert.Equal(257 * overflowPass, native.Enumerated.Count);
        Assert.Equal((overflowPass, 256u), native.Enumerated[^1]);
        Assert.Equal(overflowPass, native.ModeRequests.Count);
        Assert.DoesNotContain(native.ModeRequests, call => call.DeviceName == "OVER-LIMIT");
    }

    /// <summary>枚举和模式读取在任一遍抛出的 native 错误保持对象身份及错误码，不包装吞错或自动重试。</summary>
    [Theory]
    [InlineData(1, "Enum:0", false)]
    [InlineData(1, "Enum:6", true)]
    [InlineData(1, "Mode:LEFT-PORTRAIT", true)]
    [InlineData(1, "Mode:RIGHT", false)]
    [InlineData(2, "Enum:0", true)]
    [InlineData(2, "Enum:6", false)]
    [InlineData(2, "Mode:LEFT-PORTRAIT", false)]
    [InlineData(2, "Mode:RIGHT", true)]
    public void GetDisplays_PreservesNativeExceptionInstance(int pass, string point, bool win32)
    {
        var native = new DisplayOnlyNative();
        Exception original = win32
            ? new Win32Exception(5, "受控只读枚举失败")
            : new InvalidOperationException("受控只读枚举失败，无扩展错误码");
        native.Failures.Add($"{pass}:{point}", original);

        Exception? actual = Record.Exception(() => new GdiDisplayCatalog(native).GetDisplays());

        Assert.Same(original, actual);
        Assert.Equal(pass, native.Passes);
        Assert.Equal(1, native.Calls.Count(call => call == $"{pass}:{point}"));
        if (win32) Assert.Equal(5, Assert.IsType<Win32Exception>(actual).NativeErrorCode);
    }

    /// <summary>目录只暴露不可改写集合；即使尝试泛型或非泛型 IList 修改，内容仍保持快照。</summary>
    [Fact]
    public void GetDisplays_ReturnsCollectionThatCannotBeMutatedThroughIList()
    {
        IReadOnlyList<DisplayInfo> snapshot = new GdiDisplayCatalog(new DisplayOnlyNative()).GetDisplays();
        DisplayInfo[] original = snapshot.ToArray();
        DisplayInfo replacement = original[0] with { Name = "外部改写" };

        if (snapshot is IList<DisplayInfo> generic)
        {
            Assert.True(generic.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => generic[0] = replacement);
            Assert.Throws<NotSupportedException>(() => generic.Add(replacement));
            Assert.Throws<NotSupportedException>(() => generic.RemoveAt(0));
            Assert.Throws<NotSupportedException>(() => generic.Clear());
        }

        if (snapshot is IList nongeneric)
        {
            Assert.True(nongeneric.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => nongeneric[0] = replacement);
            Assert.Throws<NotSupportedException>(() => nongeneric.Add(replacement));
            Assert.Throws<NotSupportedException>(() => nongeneric.RemoveAt(0));
            Assert.Throws<NotSupportedException>(() => nongeneric.Clear());
        }

        Assert.Equal(original, snapshot.ToArray());
    }

    /// <summary>每次调用都进行两遍刷新；设备之后变更和后续返回值均不能改写已交付快照。</summary>
    [Fact]
    public void GetDisplays_RefreshesEveryCallAndKeepsPreviouslyReturnedSnapshotsUnchanged()
    {
        var native = new DisplayOnlyNative();
        var catalog = new GdiDisplayCatalog(native);
        IReadOnlyList<DisplayInfo> first = catalog.GetDisplays();
        DisplayInfo[] original = first.ToArray();
        Assert.Equal(2, native.Passes);
        native.Entries[1].Device.DeviceString = "刷新后的竖屏";
        native.Entries[1].Mode.PositionX = -1200;
        native.Entries[1].Mode.PelsWidth = 1200;
        native.Entries[3].Mode.PelsWidth = 3840;
        native.Entries[3].Mode.PelsHeight = 2160;
        native.Entries[4].Device.StateFlags = 0;

        IReadOnlyList<DisplayInfo> second = catalog.GetDisplays();
        Assert.Equal(4, native.Passes);
        Assert.NotSame(first, second);
        Assert.Equal(original, first.ToArray());
        Assert.Collection(second,
            display => Assert.Equal(new DisplayInfo(new DisplayId(1), "刷新后的竖屏",
                -1200, -240, 1200, 1920, false), display),
            display => Assert.Equal(new DisplayInfo(new DisplayId(3), "合成显示设备 MAIN",
                0, 0, 3840, 2160, true), display));

        IReadOnlyList<DisplayInfo> third = catalog.GetDisplays();
        Assert.Equal(6, native.Passes);
        Assert.NotSame(second, third);
        Assert.Equal(second.ToArray(), third.ToArray());
        Assert.Equal(original, first.ToArray());
    }

    /// <summary>真实桌面只读取当前设备名称、索引和几何；无桌面或枚举失败必须失败而非跳过。</summary>
    [Fact]
    public void RealDesktop_ReadOnlyEnumeration_ReportsCurrentSnapshot()
    {
        Assert.True(OperatingSystem.IsWindows(), "真实桌面枚举需要 Windows，不把不支持的环境当作通过。");

        IReadOnlyList<DisplayInfo> displays = new GdiDisplayCatalog().GetDisplays();

        Assert.NotEmpty(displays);
        Assert.Single(displays, display => display.IsPrimary);
        Assert.Equal(displays.Count, displays.Select(display => display.Id).Distinct().Count());
        int previousIndex = -1;
        foreach (DisplayInfo display in displays)
        {
            output.WriteLine($"名称={display.Name}；枚举索引={display.Id.Value}；主屏={display.IsPrimary}；" +
                $"左上=({display.X},{display.Y})；尺寸={display.Width}x{display.Height}；" +
                $"右下=({display.Right},{display.Bottom})");
            Assert.False(string.IsNullOrWhiteSpace(display.Name));
            Assert.InRange(display.Id.Value, 0, 255);
            Assert.True(display.Id.Value > previousIndex, "返回顺序应保持原始枚举索引递增。");
            Assert.InRange(display.Width, 1, int.MaxValue);
            Assert.InRange(display.Height, 1, int.MaxValue);
            Assert.InRange((long)display.X + display.Width, (long)int.MinValue, int.MaxValue);
            Assert.InRange((long)display.Y + display.Height, (long)int.MinValue, int.MaxValue);
            Assert.Equal((long)display.X + display.Width, display.Right);
            Assert.Equal((long)display.Y + display.Height, display.Bottom);
            previousIndex = display.Id.Value;
        }

        output.WriteLine($"本次真实只读快照共 {displays.Count} 台设备；不代表多屏硬件验收或像素采集通过。");
    }

    private static DisplayOnlyNative SingleDisplay(int x, int y, uint width, uint height) => new()
    {
        Entries = [Entry("SINGLE", Attached | Primary, x, y, width, height)],
    };

    private static DisplayEntry[] BoundaryEntries(bool allAttached) => Enumerable.Range(0, 256)
        .Select(index => Entry($"DISPLAY-{index}", index == 255 ? Attached | Primary : allAttached ? Attached : 0,
            index, 0, 1, 1)).ToArray();

    private static DisplayEntry Entry(string name, uint flags, int x, int y, uint width, uint height,
        uint orientation = 0) => new(
        new GdiDisplayDevice
        {
            DeviceName = name,
            DeviceString = $"合成显示设备 {name}",
            StateFlags = flags,
            DeviceId = $"ID-{name}",
            DeviceKey = $"KEY-{name}",
        },
        new GdiDevMode
        {
            Fields = RequiredFields,
            PositionX = x,
            PositionY = y,
            PelsWidth = width,
            PelsHeight = height,
            DisplayOrientation = orientation,
            BitsPerPel = 32,
            DisplayFrequency = 60,
        });

    private sealed class DisplayEntry(GdiDisplayDevice device, GdiDevMode mode)
    {
        internal GdiDisplayDevice Device = device;
        internal GdiDevMode Mode = mode;
    }

    // 此合成实现只持有两个只读方法，不能获得像素、句柄或修改系统设置。
    private sealed class DisplayOnlyNative : IGdiDisplayNative
    {
        internal DisplayEntry[] Entries =
        [
            Entry("DETACHED", Primary, 0, 0, 0, 0),
            Entry("LEFT-PORTRAIT", Attached, -1080, -240, 1080, 1920, 1),
            Entry("MIRROR", Attached | Primary | Mirroring, 0, 0, 0, 0),
            Entry("MAIN", Attached | Primary, 0, 0, 2560, 1440),
            Entry("RIGHT", Attached, 2560, 180, 1920, 1080),
            Entry("TRAILING-DETACHED", 0, 0, 0, 0, 0),
        ];
        internal readonly List<(int Pass, uint Index)> Enumerated = [];
        internal readonly List<(int Pass, string DeviceName)> ModeRequests = [];
        internal readonly List<string> Calls = [];
        internal readonly Dictionary<string, Exception> Failures = [];
        internal Action<int>? OnPass;
        internal int Passes;

        public bool EnumDisplayDevices(uint index, out GdiDisplayDevice device)
        {
            if (index == 0)
            {
                Passes++;
                OnPass?.Invoke(Passes);
            }

            Enumerated.Add((Passes, index));
            Step($"Enum:{index}");
            Assert.InRange(index, 0u, 256u);
            device = index < Entries.Length ? Entries[(int)index].Device : default;
            return index < Entries.Length;
        }

        public GdiDevMode GetDisplayMode(string deviceName)
        {
            ModeRequests.Add((Passes, deviceName));
            Step($"Mode:{deviceName}");
            // 被过滤镜像/脱离项可具有相同噪声名称；模式查询代表当前附着的非镜像设备。
            return Entries.First(entry => (entry.Device.StateFlags & Attached) != 0 &&
                (entry.Device.StateFlags & Mirroring) == 0 &&
                string.Equals(entry.Device.DeviceName, deviceName, StringComparison.Ordinal)).Mode;
        }

        private void Step(string point)
        {
            string key = $"{Passes}:{point}";
            Calls.Add(key);
            if (Failures.TryGetValue(key, out Exception? error)) throw error;
        }
    }
}
