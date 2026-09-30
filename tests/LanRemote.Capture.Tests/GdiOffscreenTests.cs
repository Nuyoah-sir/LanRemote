using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using LanRemote.Capture;
using LanRemote.Core.Models;
using Xunit;

namespace LanRemote.Capture.Tests;

/// <summary>用真实离屏 source DIB 验证生产 BitBlt 核心；不枚举或读取实时桌面。</summary>
[Collection(GdiBufferObservationCollection.Name)]
public sealed class GdiOffscreenTests
{
    /// <summary>真实 GDI 复制必须保留行顺序和 BGR 分量，并将每个 alpha 归一化为 255。</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(5, 3)]
    public async Task Capture_RealOffscreenDibUsesProductionBitBltAndCleanup(int width, int height)
    {
        Assert.True(OperatingSystem.IsWindows(), "此真实 GDI 测试需要 Windows，不把不支持或权限错误当作通过。");
        var native = new OffscreenNative(width, height);
        var backend = new GdiScreenCaptureBackend(native);
        DisplayInfo display = Assert.Single(backend.GetDisplays());
        Assert.Equal(new DisplayId(0), display.Id);
        Assert.Equal(-400, display.X);
        Assert.Equal(-200, display.Y);
        using CapturedFrame frame = await backend.CaptureAsync(display.Id, default);
        Assert.Equal(width, frame.Width);
        Assert.Equal(height, frame.Height);
        Assert.Equal(width * 4, frame.Stride);
        Assert.Equal(FramePixelFormat.Bgra32, frame.PixelFormat);
        byte[] expected = CreatePattern(width, height);
        for (int i = 3; i < expected.Length; i += 4) expected[i] = 255;
        Assert.Equal(expected, frame.Pixels.ToArray());
        Assert.Equal(expected.Length, frame.Pixels.Length);
        Assert.InRange(frame.TimestampUs, 0L, long.MaxValue);
        Assert.Equal(1, native.BitBltCalls);
        Assert.Equal(1, native.FlushCalls);
        Assert.Equal(1, native.CopyCalls);
        Assert.Equal(1, native.SourceCreated);
        Assert.Equal(1, native.SourceClosed);
        Assert.Equal(0, native.LiveSourceResources);
        Assert.Equal(new[] { "Restore", "DeleteBitmap", "DeleteMemory", "DeleteSource" }, native.CleanupCalls);
        Assert.NotNull(native.CopiedBuffer);
        frame.Dispose();
        Assert.All(native.CopiedBuffer.Take(expected.Length), value => Assert.Equal((byte)0, value));
    }

    private static byte[] CreatePattern(int width, int height)
    {
        byte[] pixels = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                pixels[offset] = (byte)(11 + x * 19 + y * 3);
                pixels[offset + 1] = (byte)(31 + x * 5 + y * 47);
                pixels[offset + 2] = (byte)(71 + x * 7 + y * 29);
                pixels[offset + 3] = (byte)((x + y) % 3 * 97);
            }
        }

        return pixels;
    }

    // 仅替换设备元数据及源 DC 的创建/销毁，目标 DIB、选择、BitBlt、Flush、复制均走生产实现。
    private sealed class OffscreenNative(int width, int height) : IGdiNative
    {
        private readonly GdiNative _real = new();
        private nint _sourceDc;
        private nint _sourceBitmap;
        private nint _sourcePrevious;
        private bool _sourceSelected;
        private nint _destinationDc;
        private nint _destinationBitmap;
        private bool _destinationSelected;
        private bool _flushed;
        internal int SourceCreated;
        internal int SourceClosed;
        internal int BitBltCalls;
        internal int FlushCalls;
        internal int CopyCalls;
        internal byte[]? CopiedBuffer;
        internal readonly List<string> CleanupCalls = [];
        internal int LiveSourceResources => (_sourceDc != 0 ? 1 : 0) + (_sourceBitmap != 0 ? 1 : 0);

        public bool EnumDisplayDevices(uint index, out GdiDisplayDevice device)
        {
            device = GdiTestNative.Device("OFFSCREEN-DIB", GdiNative.AttachedToDesktop | GdiNative.PrimaryDevice);
            return index == 0;
        }

        public GdiDevMode GetDisplayMode(string deviceName)
        {
            Assert.Equal("OFFSCREEN-DIB", deviceName);
            return new GdiDevMode
            {
                PositionX = -400,
                PositionY = -200,
                PelsWidth = (uint)width,
                PelsHeight = (uint)height,
                BitsPerPel = 32,
            };
        }

        public nint CreateSourceDc(string deviceName)
        {
            Assert.Equal("OFFSCREEN-DIB", deviceName);
            try
            {
                // CreateCompatibleDC(NULL) 创建内存 DC，不调用 CreateDC(DISPLAY)、GetDC 或桌面 BitBlt。
                _sourceDc = _real.CreateMemoryDc(0);
                byte[] pixels = CreatePattern(width, height);
                GdiBitmapInfo info = GdiBitmapInfo.Create(width, height, pixels.Length);
                _sourceBitmap = _real.CreateDibSection(_sourceDc, ref info, out nint bits);
                Assert.NotEqual((nint)0, bits);
                _sourcePrevious = _real.SelectObject(_sourceDc, _sourceBitmap);
                _sourceSelected = true;
                _real.Flush();
                Marshal.Copy(pixels, 0, bits, pixels.Length);
                SourceCreated++;
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
            Assert.Equal(width, info.Header.Width);
            Assert.Equal(-height, info.Header.Height);
            Assert.Equal((ushort)32, info.Header.BitCount);
            return _destinationBitmap = _real.CreateDibSection(dc, ref info, out bits);
        }

        public nint SelectObject(nint dc, nint value)
        {
            Assert.Equal(_destinationDc, dc);
            if (_destinationSelected) CleanupCalls.Add("Restore");
            nint previous = _real.SelectObject(dc, value);
            _destinationSelected = value == _destinationBitmap;
            return previous;
        }

        public void BitBlt(nint destination, int copyWidth, int copyHeight, nint source, uint operation)
        {
            Assert.Equal(_sourceDc, source);
            Assert.Equal(_destinationDc, destination);
            Assert.Equal(width, copyWidth);
            Assert.Equal(height, copyHeight);
            Assert.Equal(0x40CC0020u, operation);
            _real.BitBlt(destination, copyWidth, copyHeight, source, operation);
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
            _real.CopyPixels(bits, destination, length);
            CopiedBuffer = destination;
            CopyCalls++;
        }

        public void DeleteObject(nint value)
        {
            Assert.Equal(_destinationBitmap, value);
            Assert.False(_destinationSelected);
            CleanupCalls.Add("DeleteBitmap");
            _real.DeleteObject(value);
            _destinationBitmap = 0;
        }

        public void DeleteDc(nint dc)
        {
            if (dc == _sourceDc)
            {
                CleanupCalls.Add("DeleteSource");
                var errors = new List<Exception>();
                CloseSource(errors);
                ThrowErrors(errors);
                SourceClosed++;
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
            bool mayDeleteBitmap = true;
            bool dcDeleteAttempted = false;
            if (_sourceSelected)
            {
                if (TryCleanup(() => _real.SelectObject(_sourceDc, _sourcePrevious), errors))
                {
                    _sourceSelected = false;
                }
                else
                {
                    dcDeleteAttempted = true;
                    mayDeleteBitmap = TryCleanup(() => _real.DeleteDc(_sourceDc), errors);
                    if (mayDeleteBitmap)
                    {
                        _sourceDc = 0;
                        _sourceSelected = false;
                    }
                }
            }

            if (_sourceBitmap != 0 && mayDeleteBitmap && TryCleanup(() => _real.DeleteObject(_sourceBitmap), errors))
            {
                _sourceBitmap = 0;
            }

            if (_sourceDc != 0 && !dcDeleteAttempted && TryCleanup(() => _real.DeleteDc(_sourceDc), errors))
            {
                _sourceDc = 0;
            }
        }

        private static bool TryCleanup(Action action, List<Exception> errors)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception error)
            {
                errors.Add(error);
                return false;
            }
        }

        private static void ThrowErrors(List<Exception> errors)
        {
            if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
            if (errors.Count > 1) throw new AggregateException("离屏 source 的原始错误及清理错误。", errors);
        }
    }
}
