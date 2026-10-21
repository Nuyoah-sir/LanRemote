using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using LanRemote.Capture;
using LanRemote.Core.Models;
using Xunit;

namespace LanRemote.Capture.Tests;

/// <summary>合成多屏元数据配合真实离屏 DIB；不枚举、读取桌面或创建窗口，不代表真机多屏验证。</summary>
[Collection(GdiBufferObservationCollection.Name)]
public sealed class GdiMultiDisplayOffscreenTests
{
    private static readonly string[] NormalCleanup = ["Restore", "DeleteBitmap", "DeleteMemory", "DeleteSource"];

    [Theory]
    [InlineData(2, 1, 1)]
    [InlineData(2, 3, 2)]
    [InlineData(2, 5, 3)]
    [InlineData(4, 1, 2)]
    [InlineData(4, 2, 3)]
    [InlineData(4, 3, 5)]
    public async Task Capture_RawIndexUsesLocalTopDownPixelsAndOnlyTargetBudget(int rawIndex, int width, int height)
    {
        var native = new OffscreenNative();
        native.Targets[rawIndex] = new Target(rawIndex, width, height);
        int other = rawIndex == 2 ? 4 : 2;
        native.Targets[other] = new Target(other, 8192, 8192);
        var backend = GdiScreenCaptureBackend.CreateMultiDisplay(native);
        var displays = backend.GetDisplays();
        Assert.Equal(new[] { new DisplayId(2), new DisplayId(4) }, displays.Select(display => display.Id));
        foreach (DisplayInfo display in displays)
        {
            Target target = native.Targets[display.Id.Value];
            Assert.Equal((target.X, target.Y, target.Width, target.Height, target.Id == 2),
                (display.X, display.Y, display.Width, display.Height, display.IsPrimary));
        }
        Assert.Equal(new[] { "Pass", "2", "4", "Pass", "2", "4" }, native.Observations);
        Assert.Equal(0, native.SourceCreated);
        await CaptureAndAssertAsync(backend, native, rawIndex);
    }

    [Fact]
    public async Task Capture_AlternatesDevicesAndRetriesAfterRealSourceAndCopyFailures()
    {
        var native = new OffscreenNative();
        var backend = GdiScreenCaptureBackend.CreateMultiDisplay(native);
        foreach (int id in new[] { 4, 2, 4, 2 }) await CaptureAndAssertAsync(backend, native, id);
        foreach (string point in new[] { "Source", "Copy" })
        {
            native.BeginCapture(4);
            native.FailAt = point;
            Exception? error = await Record.ExceptionAsync(async () =>
            {
                using CapturedFrame unexpected = await CaptureJoinedAsync(backend, 4);
            });
            Assert.Same(native.Failure, error);
            native.AssertReleased();
            Assert.Equal(point == "Source" ? Array.Empty<string>() : NormalCleanup, native.CleanupCalls);
            Assert.Equal(point == "Source" ? 0 : 1, native.CopyCalls);
            if (point == "Copy") AssertBufferCleared(native);
            else Assert.Null(native.CopiedBuffer);
            native.FailAt = null;
            await CaptureAndAssertAsync(backend, native, 2);
            await CaptureAndAssertAsync(backend, native, 4);
        }
    }

    [Fact]
    public async Task DefaultConstructor_RemainsPrimaryOnlyWithSameMultipleMetadata()
    {
        var native = new OffscreenNative();
        var backend = new GdiScreenCaptureBackend(native);
        DisplayInfo display = Assert.Single(backend.GetDisplays());
        Assert.Equal(new DisplayId(2), display.Id);
        Assert.True(display.IsPrimary);
        await CaptureAndAssertAsync(backend, native, 2, multi: false);
        native.BeginCapture(4);
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            using CapturedFrame unexpected = await CaptureJoinedAsync(backend, 4);
        });
        Assert.Equal(0, native.SourceCreated);
        Assert.Equal(0, native.LiveResources);
        Assert.Empty(native.CleanupCalls);
        Assert.Null(native.CopiedBuffer);
        await CaptureAndAssertAsync(backend, native, 2, multi: false);
    }

    private static async Task<CapturedFrame> CaptureJoinedAsync(GdiScreenCaptureBackend backend, int id)
    {
        Task<CapturedFrame> operation = backend.CaptureAsync(new DisplayId(id), default).AsTask();
        Task worker = backend.OriginalWorker ?? operation;
        try
        {
            // 不以超时或公开 operation 先完成替代原 worker 的清理完成。
            await Task.WhenAll(operation, worker);
            return await operation;
        }
        catch
        {
            if (operation.IsCompletedSuccessfully) operation.Result.Dispose();
            if (worker is Task<CapturedFrame> { IsCompletedSuccessfully: true } completed) completed.Result.Dispose();
            throw;
        }
    }

    private static async Task CaptureAndAssertAsync(GdiScreenCaptureBackend backend, OffscreenNative native,
        int id, bool multi = true)
    {
        native.BeginCapture(id);
        Target target = native.Targets[id];
        using CapturedFrame frame = await CaptureJoinedAsync(backend, id);
        Assert.Equal(new DisplayId(id), frame.DisplayId);
        Assert.Equal((target.Width, target.Height, target.Width * 4), (frame.Width, frame.Height, frame.Stride));
        Assert.Equal(FramePixelFormat.Bgra32, frame.PixelFormat);
        byte[] expected = CreatePattern(target);
        for (int i = 3; i < expected.Length; i += 4) expected[i] = 255;
        Assert.Equal(expected.Length, frame.Pixels.Length);
        Assert.Equal(expected, frame.Pixels.ToArray());
        Assert.InRange(frame.TimestampUs, 0L, long.MaxValue);
        Assert.Equal((1, 1, 1), (native.BitBltCalls, native.FlushCalls, native.CopyCalls));
        native.AssertReleased();
        Assert.Equal(NormalCleanup, native.CleanupCalls);
        string[] observations = multi
            ? ["Pass", "2", "4", "Pass", "2", "4", "Source", "Pass", "2", "4", "Pass", "2", "4"]
            : ["Pass", "2", "Source", "Pass", "2"];
        Assert.Equal(observations, native.Observations);
        frame.Dispose();
        AssertBufferCleared(native);
    }

    private static void AssertBufferCleared(OffscreenNative native)
    {
        Assert.NotNull(native.CopiedBuffer);
        Assert.All(native.CopiedBuffer, value => Assert.Equal((byte)0, value));
    }

    private sealed record Target(int Id, int Width, int Height)
    {
        internal string Name => Id == 2 ? "OFFSCREEN-PRIMARY" : "OFFSCREEN-SECONDARY";
        internal int X => Id == 2 ? 0 : -400;
        internal int Y => Id == 2 ? 0 : -200;
    }

    private static byte[] CreatePattern(Target target)
    {
        byte[] pixels = new byte[checked(target.Width * target.Height * 4)];
        for (int y = 0; y < target.Height; y++)
        for (int x = 0; x < target.Width; x++)
        {
            int offset = (y * target.Width + x) * 4;
            pixels[offset] = (byte)(11 + target.Id * 17 + x * 19 + y * 3);
            pixels[offset + 1] = (byte)(31 + target.Id * 11 + x * 5 + y * 47);
            pixels[offset + 2] = (byte)(71 + target.Id * 7 + x * 7 + y * 29);
            pixels[offset + 3] = (byte)((x + y) % 3 * 97);
        }
        return pixels;
    }

    // 最小复制旧 OffscreenNative；只替换元数据和源 DC，全部像素操作及删除仍走 GdiNative。
    private sealed class OffscreenNative : IGdiNative
    {
        private readonly GdiNative _real = new();
        private nint _sourceDc, _sourceBitmap, _sourcePrevious, _destinationDc, _destinationBitmap, _destinationBits;
        private bool _sourceSelected, _destinationSelected, _flushed;
        private Target? _expected, _sourceTarget;
        internal readonly Dictionary<int, Target> Targets = new() { [2] = new(2, 3, 2), [4] = new(4, 2, 5) };
        internal readonly List<string> CleanupCalls = [], Observations = [];
        internal readonly Exception Failure = new InvalidOperationException("受控离屏失败，保留原始异常。");
        internal string? FailAt;
        internal int SourceCreated, SourceClosed, BitBltCalls, FlushCalls, CopyCalls;
        internal byte[]? CopiedBuffer;
        internal int LiveResources => (_sourceDc != 0 ? 1 : 0) + (_sourceBitmap != 0 ? 1 : 0)
            + (_destinationDc != 0 ? 1 : 0) + (_destinationBitmap != 0 ? 1 : 0);

        internal OffscreenNative() => Assert.True(OperatingSystem.IsWindows(), "真实离屏 GDI 测试必须在 Windows 执行，不绿色跳过。");

        internal void BeginCapture(int id)
        {
            Assert.Equal(0, LiveResources);
            Assert.False(_sourceSelected || _destinationSelected);
            _sourcePrevious = _destinationBits = 0;
            _expected = Targets[id];
            _sourceTarget = null;
            _flushed = false;
            SourceCreated = SourceClosed = BitBltCalls = FlushCalls = CopyCalls = 0;
            CopiedBuffer = null;
            CleanupCalls.Clear();
            Observations.Clear();
        }

        internal void AssertReleased()
        {
            Assert.Equal((1, 1, 0), (SourceCreated, SourceClosed, LiveResources));
            Assert.False(_sourceSelected || _destinationSelected);
        }

        public bool EnumDisplayDevices(uint index, out GdiDisplayDevice device)
        {
            Assert.Equal(0, LiveResources);
            if (index == 0) Observations.Add("Pass");
            device = index switch
            {
                0 => GdiTestNative.Device("MIRROR", GdiNative.AttachedToDesktop | 0x00000008),
                1 or 3 => GdiTestNative.Device($"DETACHED-{index}", 0),
                2 => GdiTestNative.Device(Targets[2].Name, GdiNative.AttachedToDesktop | GdiNative.PrimaryDevice),
                4 => GdiTestNative.Device(Targets[4].Name, GdiNative.AttachedToDesktop),
                _ => default,
            };
            return index < 5;
        }

        public GdiDevMode GetDisplayMode(string deviceName)
        {
            Target target = Assert.Single(Targets.Values, target => target.Name == deviceName);
            Observations.Add(target.Id.ToString());
            return new GdiDevMode
            {
                Fields = 0x00000020 | 0x00080000 | 0x00100000, // DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT
                PositionX = target.X, PositionY = target.Y,
                PelsWidth = (uint)target.Width, PelsHeight = (uint)target.Height,
                BitsPerPel = 32, DisplayOrientation = target.Id == 4 ? 1u : 0u,
            };
        }

        public nint CreateSourceDc(string deviceName)
        {
            _sourceTarget = Assert.Single(Targets.Values, target => target.Name == deviceName);
            Assert.Equal(_expected, _sourceTarget); // 选错主屏不能用相同 pattern 掩盖。
            Observations.Add("Source");
            try
            {
                _sourceDc = _real.CreateMemoryDc(0); // 内存 DC，不调用 CreateDC(DISPLAY)、GetDC 或真实枚举。
                SourceCreated++;
                byte[] pixels = CreatePattern(_sourceTarget);
                GdiBitmapInfo info = GdiBitmapInfo.Create(_sourceTarget.Width, _sourceTarget.Height, pixels.Length);
                _sourceBitmap = _real.CreateDibSection(_sourceDc, ref info, out nint bits);
                Assert.NotEqual((nint)0, bits);
                _sourcePrevious = _real.SelectObject(_sourceDc, _sourceBitmap);
                _sourceSelected = true;
                _real.Flush();
                Marshal.Copy(pixels, 0, bits, pixels.Length);
                if (FailAt == "Source") throw Failure;
                return _sourceDc;
            }
            catch (Exception original)
            {
                var errors = new List<Exception> { original };
                CloseSource(errors);
                ThrowErrors(errors);
                throw;
            }
        }

        public nint CreateMemoryDc(nint source)
        {
            Assert.Equal(_sourceDc, source);
            return _destinationDc = _real.CreateMemoryDc(source);
        }

        public nint CreateDibSection(nint dc, ref GdiBitmapInfo info, out nint bits)
        {
            Assert.Equal(_destinationDc, dc);
            Assert.NotNull(_sourceTarget);
            Assert.Equal((_sourceTarget.Width, -_sourceTarget.Height, (ushort)32),
                (info.Header.Width, info.Header.Height, info.Header.BitCount));
            _destinationBitmap = _real.CreateDibSection(dc, ref info, out bits);
            _destinationBits = bits;
            return _destinationBitmap;
        }

        public nint SelectObject(nint dc, nint value)
        {
            Assert.Equal(_destinationDc, dc);
            if (_destinationSelected) CleanupCalls.Add("Restore");
            nint previous = _real.SelectObject(dc, value);
            _destinationSelected = value == _destinationBitmap;
            return previous;
        }

        public void BitBlt(nint destination, int width, int height, nint source, uint operation)
        {
            Assert.Equal((_sourceDc, _destinationDc), (source, destination));
            Assert.NotNull(_sourceTarget);
            Assert.Equal((_sourceTarget.Width, _sourceTarget.Height), (width, height));
            Assert.Equal(0x40CC0020u, operation);
            _real.BitBlt(destination, width, height, source, operation); // 原生产方法固定 source 0,0。
            BitBltCalls++;
        }

        public void Flush()
        {
            _real.Flush();
            _flushed = true;
            FlushCalls++;
        }

        public void CopyPixels(nint bits, byte[] destination, int length)
        {
            Assert.True(_flushed);
            Assert.Equal(_destinationBits, bits);
            Assert.Equal(checked(_sourceTarget!.Width * _sourceTarget.Height * 4), length);
            CopiedBuffer = destination;
            Array.Fill(destination, (byte)0xA7); // 连池数组尾部也染色，验证整个实际数组被清零。
            _real.CopyPixels(bits, destination, length);
            CopyCalls++;
            if (FailAt == "Copy") throw Failure;
        }

        public void DeleteObject(nint value)
        {
            Assert.Equal(_destinationBitmap, value);
            Assert.False(_destinationSelected);
            CleanupCalls.Add("DeleteBitmap");
            _real.DeleteObject(value);
            _destinationBitmap = _destinationBits = 0;
        }

        public void DeleteDc(nint dc)
        {
            if (dc == _sourceDc)
            {
                CleanupCalls.Add("DeleteSource");
                var errors = new List<Exception>();
                CloseSource(errors);
                ThrowErrors(errors);
                return;
            }
            Assert.Equal(_destinationDc, dc);
            CleanupCalls.Add("DeleteMemory");
            _real.DeleteDc(dc);
            _destinationDc = 0;
            _destinationSelected = false;
        }

        private void CloseSource(List<Exception> errors)
        {
            bool mayDeleteBitmap = true, dcDeleteAttempted = false;
            if (_sourceSelected)
            {
                if (TryCleanup(() => _real.SelectObject(_sourceDc, _sourcePrevious), errors)) _sourceSelected = false;
                else
                {
                    dcDeleteAttempted = true;
                    mayDeleteBitmap = TryCleanup(() => _real.DeleteDc(_sourceDc), errors);
                    if (mayDeleteBitmap) { _sourceDc = 0; _sourceSelected = false; SourceClosed++; }
                }
            }
            if (_sourceBitmap != 0 && mayDeleteBitmap && TryCleanup(() => _real.DeleteObject(_sourceBitmap), errors))
                _sourceBitmap = 0;
            if (_sourceDc != 0 && !dcDeleteAttempted && TryCleanup(() => _real.DeleteDc(_sourceDc), errors))
            { _sourceDc = 0; SourceClosed++; }
        }

        private static bool TryCleanup(Action action, List<Exception> errors)
        {
            try { action(); return true; }
            catch (Exception error) { errors.Add(error); return false; }
        }

        private static void ThrowErrors(List<Exception> errors)
        {
            if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
            if (errors.Count > 1) throw new AggregateException("离屏 source 的原始错误及清理错误。", errors);
        }
    }
}
