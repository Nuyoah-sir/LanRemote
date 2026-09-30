using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using LanRemote.Capture;
using LanRemote.Core.Models;
using Xunit;
using Xunit.Sdk;

namespace LanRemote.Capture.Tests;

/// <summary>仅使用实例级合成 native，验证采集和资源所有权合同。</summary>
[Collection(GdiBufferObservationCollection.Name)]
public sealed class GdiScreenCaptureBackendTests
{
    private static readonly DisplayId PrimaryId = new(GdiTestNative.PrimaryIndex);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);
    private static readonly string[] NormalCleanup = ["Restore", "DeleteBitmap", "DeleteMemory", "DeleteSource"];

    /// <summary>主屏 ID 保留真实枚举位置，同时保留物理模式中的负坐标。</summary>
    [Fact]
    public void GetDisplays_ReturnsOnlyAttachedPrimaryWithActualEnumerationIndex()
    {
        var native = new GdiTestNative();
        var backend = new GdiScreenCaptureBackend(native);
        DisplayInfo display = Assert.Single(backend.GetDisplays());
        Assert.Equal(PrimaryId, display.Id);
        Assert.True(display.IsPrimary);
        Assert.Equal(-120, display.X);
        Assert.Equal(-80, display.Y);
        Assert.Equal(3, display.Width);
        Assert.Equal(2, display.Height);
        Assert.Equal("合成显示设备 SYNTHETIC", display.Name);
        Assert.Equal(new[] { "Enum:0", "Enum:1", "Enum:2", "Mode" }, native.Calls.ToArray());
        Assert.Empty(native.Owned);
    }

    /// <summary>友好名称缺失时使用设备名称，不改变设备标识。</summary>
    [Fact]
    public void GetDisplays_UsesDeviceNameWhenFriendlyNameIsEmpty()
    {
        var native = new GdiTestNative();
        native.Devices[2].DeviceString = "";
        DisplayInfo display = Assert.Single(new GdiScreenCaptureBackend(native).GetDisplays());
        Assert.Equal("SYNTHETIC", display.Name);
        Assert.Equal(PrimaryId, display.Id);
    }

    /// <summary>不存在主屏或主屏未附着时明确拒绝，不创建 DC。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Capture_RejectsMissingAttachedPrimary(bool noDevices)
    {
        var native = new GdiTestNative();
        if (noDevices)
        {
            native.Devices = [];
        }
        else
        {
            native.Devices[2].StateFlags = GdiNative.PrimaryDevice;
        }

        var backend = new GdiScreenCaptureBackend(native);
        Assert.Throws<NotSupportedException>(() => backend.GetDisplays());
        await Assert.ThrowsAsync<NotSupportedException>(() => backend.CaptureAsync(PrimaryId, default).AsTask());
        Assert.DoesNotContain("CreateSource", native.Calls);
    }

    /// <summary>空设备名称在创建 DC 前被拒绝。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Capture_RejectsEmptyDeviceName(string name)
    {
        var native = new GdiTestNative();
        native.Devices[2].DeviceName = name;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GdiScreenCaptureBackend(native).CaptureAsync(PrimaryId, default).AsTask());
        Assert.DoesNotContain("CreateSource", native.Calls);
    }

    /// <summary>过滤列表位置、负 ID 及非主屏 ID 均不允许采集。</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public async Task Capture_RejectsNonPrimaryIdWithoutAllocating(int id)
    {
        var native = new GdiTestNative();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            new GdiScreenCaptureBackend(native).CaptureAsync(new DisplayId(id), default).AsTask());
        Assert.DoesNotContain("CreateSource", native.Calls);
        Assert.Empty(native.Owned);
        Assert.Null(native.CopiedBuffer);
    }

    /// <summary>零、超上限及 uint 极值尺寸在所有 native 分配前被拒绝。</summary>
    [Theory]
    [InlineData(0u, 2u)]
    [InlineData(3u, 0u)]
    [InlineData(8193u, 2u)]
    [InlineData(3u, 8193u)]
    [InlineData(uint.MaxValue, 2u)]
    [InlineData(3u, uint.MaxValue)]
    public async Task Capture_RejectsInvalidPhysicalDimensions(uint width, uint height)
    {
        var native = new GdiTestNative();
        native.Mode.PelsWidth = width;
        native.Mode.PelsHeight = height;
        var backend = new GdiScreenCaptureBackend(native);
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.GetDisplays());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => backend.CaptureAsync(PrimaryId, default).AsTask());
        Assert.DoesNotContain("CreateSource", native.Calls);
    }

    /// <summary>合法边界尺寸产生精确长度的 top-down BGRA 帧，且资源清理先于交付。</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(8192, 1)]
    [InlineData(1, 8192)]
    public async Task Capture_ProducesExactTopDownOpaqueFrameAndTransfersOwner(int width, int height)
    {
        var native = new GdiTestNative();
        native.Mode.PelsWidth = (uint)width;
        native.Mode.PelsHeight = (uint)height;
        var backend = new GdiScreenCaptureBackend(native);
        using CapturedFrame frame = await backend.CaptureAsync(PrimaryId, default);
        int stride = checked(width * 4);
        int length = checked(stride * height);
        Assert.Equal(PrimaryId, frame.DisplayId);
        Assert.Equal(width, frame.Width);
        Assert.Equal(height, frame.Height);
        Assert.Equal(stride, frame.Stride);
        Assert.Equal(FramePixelFormat.Bgra32, frame.PixelFormat);
        Assert.Equal(length, frame.Pixels.Length);
        Assert.Equal((long)length, frame.EstimatedBytes);
        Assert.InRange(frame.TimestampUs, 0L, long.MaxValue);
        Assert.Equal("SYNTHETIC", native.SourceDeviceName);
        Assert.Equal(0x40CC0020u, native.CopyOperation);
        Assert.Equal(40u, native.DibInfo.Header.Size);
        Assert.Equal(width, native.DibInfo.Header.Width);
        Assert.Equal(-height, native.DibInfo.Header.Height);
        Assert.Equal((ushort)1, native.DibInfo.Header.Planes);
        Assert.Equal((ushort)32, native.DibInfo.Header.BitCount);
        Assert.Equal(0u, native.DibInfo.Header.Compression);
        Assert.Equal((uint)length, native.DibInfo.Header.SizeImage);
        byte[] pixels = frame.Pixels.ToArray();
        for (int i = 0; i < length; i++)
        {
            Assert.Equal(i % 4 == 3 ? (byte)255 : GdiTestNative.PixelByte(i), pixels[i]);
        }

        Assert.Equal(NormalCleanup, CleanupCalls(native));
        Assert.Empty(native.Owned);
        Assert.False(native.Selected);
        Assert.Equal(2, native.Enumerations);
        Assert.Equal(new[]
        {
            "Enum:0", "Enum:1", "Enum:2", "Mode", "CreateSource", "CreateMemory", "CreateDib",
            "Select", "BitBlt", "Flush", "CopyPixels", "Restore", "DeleteBitmap", "DeleteMemory",
            "DeleteSource", "Enum:0", "Enum:1", "Enum:2", "Mode",
        }, native.Calls.ToArray());
        Assert.NotEqual((byte)0, native.CopiedBuffer![0]);
        frame.Dispose();
        frame.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { _ = frame.Pixels; });
        AssertBufferCleared(native);
    }

    /// <summary>连续采集的微秒时间戳非负且不倒退。</summary>
    [Fact]
    public async Task Capture_TimestampsAreMonotonicAcrossFrames()
    {
        var backend = new GdiScreenCaptureBackend(new GdiTestNative());
        using CapturedFrame first = await backend.CaptureAsync(PrimaryId, default);
        using CapturedFrame second = await backend.CaptureAsync(PrimaryId, default);
        Assert.InRange(first.TimestampUs, 0L, long.MaxValue);
        Assert.True(second.TimestampUs >= first.TimestampUs);
    }

    /// <summary>逐点注入原始错误，核对已获得资源的清理和失败后重试。</summary>
    [Theory]
    [InlineData("Enum:0", "")]
    [InlineData("Mode", "")]
    [InlineData("CreateSource", "")]
    [InlineData("CreateMemory", "DeleteSource")]
    [InlineData("CreateDib", "DeleteMemory|DeleteSource")]
    [InlineData("Select", "DeleteBitmap|DeleteMemory|DeleteSource")]
    [InlineData("BitBlt", "Restore|DeleteBitmap|DeleteMemory|DeleteSource")]
    [InlineData("Flush", "Restore|DeleteBitmap|DeleteMemory|DeleteSource")]
    [InlineData("CopyPixels", "Restore|DeleteBitmap|DeleteMemory|DeleteSource")]
    public async Task Capture_PreservesEachOperationFailureAndCanRetry(string point, string cleanup)
    {
        var native = new GdiTestNative();
        Exception original = point is "BitBlt" or "CreateDib"
            ? new Win32Exception(5, $"受控失败 {point}")
            : new InvalidOperationException($"受控失败 {point}，不提供扩展错误码");
        native.Failures.Add(point, original);
        var backend = new GdiScreenCaptureBackend(native);
        Exception? error = await Record.ExceptionAsync(() => backend.CaptureAsync(PrimaryId, default).AsTask());
        Assert.Same(original, error);
        if (point is "BitBlt" or "CreateDib")
        {
            Assert.Equal(5, Assert.IsType<Win32Exception>(error).NativeErrorCode);
        }
        else
        {
            Assert.IsType<InvalidOperationException>(error);
        }
        Assert.Equal(cleanup.Split('|', StringSplitOptions.RemoveEmptyEntries), CleanupCalls(native));
        Assert.Equal(1, native.Calls.Count(call => call == point));
        Assert.Empty(native.Owned);
        if (point == "CopyPixels")
        {
            AssertBufferCleared(native);
        }
        else
        {
            Assert.Null(native.CopiedBuffer);
        }

        native.Failures.Clear();
        using CapturedFrame retry = await backend.CaptureAsync(PrimaryId, default);
        Assert.Equal(PrimaryId, retry.DisplayId);
        Assert.Empty(native.Owned);
    }

    /// <summary>DIB 已分配但 bits 为空时仍删除位图和 DC，且不选入对象。</summary>
    [Fact]
    public async Task Capture_NullDibBitsDoesNotLeakBitmap()
    {
        var native = new GdiTestNative { NullBits = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GdiScreenCaptureBackend(native).CaptureAsync(PrimaryId, default).AsTask());
        Assert.Equal(new[] { "DeleteBitmap", "DeleteMemory", "DeleteSource" }, CleanupCalls(native));
        Assert.DoesNotContain("Select", native.Calls);
        Assert.Empty(native.Owned);
        Assert.Null(native.CopiedBuffer);
    }

    /// <summary>每项 cleanup 失败都阻止交付，保留原始异常并清零已分配数组。</summary>
    [Theory]
    [InlineData("Restore")]
    [InlineData("DeleteBitmap")]
    [InlineData("DeleteMemory")]
    [InlineData("DeleteSource")]
    public async Task Capture_RejectsFrameOnEachCleanupFailure(string point)
    {
        var native = new GdiTestNative();
        var original = new InvalidOperationException($"受控清理失败 {point}，不提供扩展错误码");
        native.Failures.Add(point, original);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GdiScreenCaptureBackend(native).CaptureAsync(PrimaryId, default).AsTask());
        Assert.Same(original, error.InnerException);
        Assert.Contains("GDI 清理失败", error.Message);
        string[] expected = point == "Restore"
            ? ["Restore", "DeleteMemory", "DeleteBitmap", "DeleteSource"]
            : NormalCleanup;
        Assert.Equal(expected, CleanupCalls(native));
        Assert.Equal(1, native.Enumerations);
        nint[] retained = point switch
        {
            "DeleteBitmap" => [GdiTestNative.Bitmap],
            "DeleteMemory" => [GdiTestNative.Memory],
            "DeleteSource" => [GdiTestNative.Source],
            _ => [],
        };
        Assert.Equal(retained, native.Owned.OrderBy(handle => (long)handle).ToArray());
        AssertBufferCleared(native);
    }

    /// <summary>恢复及删除内存 DC 都失败时，不删除可能仍选入的位图，也不重试清理。</summary>
    [Fact]
    public async Task Capture_RetainsSelectedBitmapAndFullErrorTreeWhenDcCannotBeDeleted()
    {
        var native = new GdiTestNative();
        string[] points = ["CopyPixels", "Restore", "DeleteMemory", "DeleteSource"];
        foreach (string point in points)
        {
            native.Failures.Add(point, new InvalidOperationException($"受控失败 {point}"));
        }

        var error = await Assert.ThrowsAsync<AggregateException>(() =>
            new GdiScreenCaptureBackend(native).CaptureAsync(PrimaryId, default).AsTask());
        Assert.Equal(4, error.InnerExceptions.Count);
        Assert.Same(native.Failures["CopyPixels"], error.InnerExceptions[0]);
        for (int i = 1; i < points.Length; i++)
        {
            Assert.Same(native.Failures[points[i]], error.InnerExceptions[i].InnerException);
        }

        Assert.Contains("保留可能仍选入的位图", error.InnerExceptions[2].Message);
        Assert.Equal(new[] { "Restore", "DeleteMemory", "DeleteSource" }, CleanupCalls(native));
        Assert.DoesNotContain("DeleteBitmap", native.Calls);
        Assert.True(native.Selected);
        Assert.Equal(3, native.Owned.Count);
        AssertBufferCleared(native);
    }

    /// <summary>原始 BitBlt 错误和多个独立删除错误按发生顺序全部保留。</summary>
    [Fact]
    public async Task Capture_PreservesOriginalAndEveryIndependentCleanupFailure()
    {
        var native = new GdiTestNative();
        string[] points = ["BitBlt", "DeleteBitmap", "DeleteMemory", "DeleteSource"];
        foreach (string point in points)
        {
            native.Failures.Add(point, point == "BitBlt"
                ? new Win32Exception(5, point)
                : new InvalidOperationException(point));
        }

        var error = await Assert.ThrowsAsync<AggregateException>(() =>
            new GdiScreenCaptureBackend(native).CaptureAsync(PrimaryId, default).AsTask());
        Assert.Equal(4, error.InnerExceptions.Count);
        Assert.Same(native.Failures[points[0]], error.InnerExceptions[0]);
        for (int i = 1; i < points.Length; i++)
        {
            Assert.Same(native.Failures[points[i]], error.InnerExceptions[i].InnerException);
        }

        Assert.Equal(NormalCleanup, CleanupCalls(native));
        Assert.Null(native.CopiedBuffer);
        Assert.False(native.Selected);
    }

    /// <summary>单独出现多个 cleanup 错误时仍形成完整错误树，不依赖原始采集错误。</summary>
    [Fact]
    public async Task Capture_AggregatesCleanupOnlyFailures()
    {
        var native = new GdiTestNative();
        var bitmapError = new InvalidOperationException("删除位图失败，无扩展错误码");
        var sourceError = new InvalidOperationException("删除源 DC 失败，无扩展错误码");
        native.Failures.Add("DeleteBitmap", bitmapError);
        native.Failures.Add("DeleteSource", sourceError);
        var error = await Assert.ThrowsAsync<AggregateException>(() =>
            new GdiScreenCaptureBackend(native).CaptureAsync(PrimaryId, default).AsTask());
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Same(bitmapError, error.InnerExceptions[0].InnerException);
        Assert.Same(sourceError, error.InnerExceptions[1].InnerException);
        Assert.Equal(NormalCleanup, CleanupCalls(native));
        AssertBufferCleared(native);
    }

    /// <summary>开始前取消不调度 native，也不占用单飞槽位。</summary>
    [Fact]
    public async Task Capture_PreCanceledTokenDoesNotTouchNative()
    {
        var native = new GdiTestNative();
        var backend = new GdiScreenCaptureBackend(native);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        ValueTask<CapturedFrame> operation = backend.CaptureAsync(PrimaryId, cancellation.Token);
        Assert.True(operation.IsCanceled);
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.AsTask());
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(native.Calls);
        Assert.Null(backend.OriginalWorker);
        using CapturedFrame retry = await backend.CaptureAsync(PrimaryId, default);
    }

    /// <summary>原 native、复制或清理阻塞期间取消不能早完成，忙时拒绝且不排队。</summary>
    [Theory]
    [InlineData("BitBlt", false)]
    [InlineData("Flush", false)]
    [InlineData("CopyPixels", true)]
    [InlineData("DeleteSource", true)]
    [InlineData("Recheck", true)]
    public async Task Capture_CancellationWaitsForOriginalOperationAndReclaimsLateBuffer(string phase, bool hasBuffer)
    {
        var native = new GdiTestNative();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        void Block()
        {
            entered.TrySetResult();
            if (!release.Wait(Guard))
            {
                throw new TimeoutException("测试防挂起保护触发，不是预期的取消结果。");
            }
        }

        native.OnCall = point => { if (point == phase) Block(); };
        native.OnEnumeration = count => { if (phase == "Recheck" && count == 2) Block(); };
        var backend = new GdiScreenCaptureBackend(native);
        Task<CapturedFrame> operation = backend.CaptureAsync(PrimaryId, cancellation.Token).AsTask();
        Task<CapturedFrame>? worker = null;
        Exception? completion;
        Exception? workerCompletion;
        try
        {
            await entered.Task.WaitAsync(Guard);
            worker = Assert.IsAssignableFrom<Task<CapturedFrame>>(backend.OriginalWorker);
            Assert.NotSame(operation, worker);
            // 到达事件只说明 native 已进入；必须正向观察公开 operation 直接 await 这个原 worker。
            await AssertDirectWorkerAwaitAsync(backend, operation, worker, release);
            Assert.Equal(hasBuffer, native.CopiedBuffer is not null);
            cancellation.Cancel();
            await AssertDirectWorkerAwaitAsync(backend, operation, worker, release);
            int callsBeforeBusy = native.Calls.Count;
            ValueTask<CapturedFrame> busy = backend.CaptureAsync(PrimaryId, default);
            Assert.True(busy.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => busy.AsTask());
            Assert.Equal(callsBeforeBusy, native.Calls.Count);
            Assert.Same(worker, backend.OriginalWorker);
            await AssertDirectWorkerAwaitAsync(backend, operation, worker, release);
        }
        finally
        {
            release.Set();
            // 即使错误实现让公开 operation 提前结束，也单独 join 原 worker，不能先释放 native 夹具。
            try
            {
                completion = await ObserveCaptureCompletionAsync(operation);
            }
            finally
            {
                worker ??= Assert.IsAssignableFrom<Task<CapturedFrame>>(backend.OriginalWorker);
                workerCompletion = await ObserveCaptureCompletionAsync(worker);
            }
        }

        var error = Assert.IsAssignableFrom<OperationCanceledException>(completion);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(operation.IsCanceled);
        Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(workerCompletion).CancellationToken);
        Assert.Equal(NormalCleanup, CleanupCalls(native));
        Assert.Empty(native.Owned);
        if (hasBuffer) AssertBufferCleared(native);
        else Assert.Null(native.CopiedBuffer);
        native.OnCall = null;
        native.OnEnumeration = null;
        int createdBeforeRetry = native.Calls.Count(point => point == "CreateSource");
        Assert.Equal(1, createdBeforeRetry);
        using CapturedFrame retry = await backend.CaptureAsync(PrimaryId, default);
        Assert.Equal(createdBeforeRetry + 1, native.Calls.Count(point => point == "CreateSource"));
        Assert.NotSame(worker, backend.OriginalWorker);
    }

    /// <summary>取消与 cleanup 错误并存时不可把清理错误隐藏成普通取消。</summary>
    [Fact]
    public async Task Capture_CancellationAndCleanupFailureAreBothPreserved()
    {
        var native = new GdiTestNative();
        using var cancellation = new CancellationTokenSource();
        native.OnCall = point => { if (point == "CopyPixels") cancellation.Cancel(); };
        var cleanupError = new InvalidOperationException("删除源 DC 失败，无扩展错误码");
        native.Failures.Add("DeleteSource", cleanupError);
        Task<CapturedFrame> operation = new GdiScreenCaptureBackend(native).CaptureAsync(PrimaryId, cancellation.Token).AsTask();
        var error = await Assert.ThrowsAsync<AggregateException>(() => operation);
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(error.InnerExceptions[0]).CancellationToken);
        Assert.Same(cleanupError, error.InnerExceptions[1].InnerException);
        Assert.False(operation.IsCanceled);
        Assert.Equal(NormalCleanup, CleanupCalls(native));
        AssertBufferCleared(native);
    }

    /// <summary>单飞是实例级的；一个实例阻塞不阻止另一个实例完成。</summary>
    [Fact]
    public async Task Capture_DifferentInstancesDoNotShareSingleFlightState()
    {
        var firstNative = new GdiTestNative();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        firstNative.OnCall = point =>
        {
            if (point != "BitBlt") return;
            entered.TrySetResult();
            if (!release.Wait(Guard)) throw new TimeoutException("测试防挂起保护触发。");
        };
        Task<CapturedFrame> first = new GdiScreenCaptureBackend(firstNative).CaptureAsync(PrimaryId, default).AsTask();
        try
        {
            await entered.Task.WaitAsync(Guard);
            using CapturedFrame second = await new GdiScreenCaptureBackend(new GdiTestNative())
                .CaptureAsync(PrimaryId, default).AsTask().WaitAsync(Guard);
            Assert.False(first.IsCompleted);
            Assert.Equal(PrimaryId, second.DisplayId);
        }
        finally
        {
            release.Set();
            using CapturedFrame completed = await first.WaitAsync(Guard);
        }
    }

    /// <summary>采集期间设备身份、枚举索引或物理模式变化均拒绝交付并释放 owner。</summary>
    [Theory]
    [InlineData("DeviceName")]
    [InlineData("DeviceId")]
    [InlineData("DeviceKey")]
    [InlineData("Index")]
    [InlineData("X")]
    [InlineData("Y")]
    [InlineData("Width")]
    [InlineData("Height")]
    [InlineData("Orientation")]
    [InlineData("FixedOutput")]
    [InlineData("BitsPerPixel")]
    [InlineData("Frequency")]
    [InlineData("Flags")]
    public async Task Capture_RejectsChangedPrimaryAfterNativeCleanup(string change)
    {
        var native = new GdiTestNative();
        native.OnEnumeration = count =>
        {
            if (count != 2) return;
            Assert.Empty(native.Owned);
            Assert.Equal(NormalCleanup, CleanupCalls(native));
            switch (change)
            {
                case "DeviceName": native.Devices[2].DeviceName = "REPLACEMENT"; break;
                case "DeviceId": native.Devices[2].DeviceId = "REPLACEMENT-ID"; break;
                case "DeviceKey": native.Devices[2].DeviceKey = "REPLACEMENT-KEY"; break;
                case "Index":
                    native.Devices[0] = native.Devices[2];
                    native.Devices[2].StateFlags = GdiNative.AttachedToDesktop;
                    break;
                case "X": native.Mode.PositionX++; break;
                case "Y": native.Mode.PositionY++; break;
                case "Width": native.Mode.PelsWidth++; break;
                case "Height": native.Mode.PelsHeight++; break;
                case "Orientation": native.Mode.DisplayOrientation++; break;
                case "FixedOutput": native.Mode.DisplayFixedOutput++; break;
                case "BitsPerPixel": native.Mode.BitsPerPel = 24; break;
                case "Frequency": native.Mode.DisplayFrequency++; break;
                case "Flags": native.Mode.DisplayFlags++; break;
                default: throw new ArgumentOutOfRangeException(nameof(change));
            }
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GdiScreenCaptureBackend(native).CaptureAsync(PrimaryId, default).AsTask());
        Assert.Contains("发生变化", error.Message);
        Assert.Empty(native.Owned);
        AssertBufferCleared(native);
    }

    /// <summary>重新枚举或读取模式失败时保留错误，不交付已复制的像素。</summary>
    [Theory]
    [InlineData("Enum:0")]
    [InlineData("Mode")]
    public async Task Capture_RecheckFailureReturnsOwnerAndPreservesOriginalError(string point)
    {
        var native = new GdiTestNative();
        var original = new InvalidOperationException("重新核对失败，无扩展错误码");
        native.OnEnumeration = count => { if (count == 2) native.Failures.Add(point, original); };
        Exception? error = await Record.ExceptionAsync(() =>
            new GdiScreenCaptureBackend(native).CaptureAsync(PrimaryId, default).AsTask());
        Assert.Same(original, error);
        Assert.Empty(native.Owned);
        AssertBufferCleared(native);
    }

    /// <summary>主屏在捕获后消失时拒绝交付，已复制 buffer 必须回收。</summary>
    [Fact]
    public async Task Capture_PrimaryDisappearingBeforeDeliveryReturnsOwner()
    {
        var native = new GdiTestNative();
        native.OnEnumeration = count => { if (count == 2) native.Devices = []; };
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            new GdiScreenCaptureBackend(native).CaptureAsync(PrimaryId, default).AsTask());
        Assert.Empty(native.Owned);
        AssertBufferCleared(native);
    }

    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static async Task AssertDirectWorkerAwaitAsync(GdiScreenCaptureBackend backend,
        Task<CapturedFrame> operation, Task worker, ManualResetEventSlim release)
    {
        using var guardCancellation = new CancellationTokenSource();
        Task guard = Task.Delay(Guard, guardCancellation.Token);
        try
        {
            // 从公开 operation 观察 awaiter；原任务或代理都可被观察，身份合同单独断言。
            while (true)
            {
                Assert.False(release.IsSet, "native 闸已开，无法证明阻塞期间的等待关系。");
                Assert.Same(worker, backend.OriginalWorker);
                Assert.False(worker.IsCompleted, "原 worker 已退出，不能证明受闸控制的 await。");
                Assert.False(operation.IsCompleted, "公开 operation 已提前退出。");
                Task? awaitedTask = OperationAwaitedTask(backend, operation);
                bool registered = false;
                if (awaitedTask is not null)
                {
                    // 不匹配必须立即失败，不能当成尚未就绪继续等待 Guard。
                    Assert.Same(worker, awaitedTask);
                    registered = Continuations(awaitedTask).Any(continuation =>
                        ReferenceEquals(continuation is Delegate action ? action.Target : continuation, operation));
                }
                // 防止检查期间 worker 退出、观察字段被后续采集覆盖，或认错已完成的同名状态机。
                Assert.Same(worker, backend.OriginalWorker);
                Assert.False(worker.IsCompleted);
                Assert.False(operation.IsCompleted);
                Assert.False(release.IsSet);
                if (registered) return;
                if (guard.IsCompleted)
                {
                    throw new XunitException("Guard：公开 operation 的 awaiter Task 或 continuation 登记仍不可观察。");
                }

                await Task.Yield();
            }
        }
        finally { guardCancellation.Cancel(); }
    }

    private static Task? OperationAwaitedTask(GdiScreenCaptureBackend backend, Task operation)
    {
        Type stateMachineType = typeof(GdiScreenCaptureBackend).GetMethod("CaptureOperationAsync", Fields)?
            .GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
            ?? throw new XunitException("缺少 CaptureOperationAsync 的实际异步状态机元数据。");
        FieldInfo stateMachineField = RuntimeField(operation.GetType(), "StateMachine")
            ?? throw new XunitException("公开 operation 缺少 StateMachine 字段，不能证明等待关系。");
        object? stateMachine = stateMachineField.GetValue(operation);
        if (stateMachine is null) return null;
        Assert.IsType(stateMachineType, stateMachine);
        Assert.Same(backend, RuntimeField(stateMachineType, "<>4__this")?.GetValue(stateMachine));
        int state = Assert.IsType<int>(RuntimeField(stateMachineType, "<>1__state")?.GetValue(stateMachine));
        if (state == -1) return null;
        Assert.InRange(state, 0, int.MaxValue);

        // 核对真实 builder 已持有公开 operation；只读字段，不调用可能创建任务的 Task getter。
        object builder = RuntimeField(stateMachineType, "<>t__builder")?.GetValue(stateMachine)
            ?? throw new XunitException("状态机缺少实际 async builder。");
        Assert.Same(operation, RuntimeField(builder.GetType(), "m_task")?.GetValue(builder));
        FieldInfo awaiterField = Assert.Single(stateMachineType.GetFields(Fields),
            field => field.Name.StartsWith("<>u__", StringComparison.Ordinal));
        object awaiter = Assert.IsAssignableFrom<INotifyCompletion>(awaiterField.GetValue(stateMachine));
        FieldInfo taskField = Assert.Single(awaiter.GetType().GetFields(Fields),
            field => typeof(Task).IsAssignableFrom(field.FieldType));
        // 默认 awaiter 的 Task 尚为空时才等待发布；任何非空 Task 都必须交给身份断言。
        return (Task?)taskField.GetValue(awaiter);
    }

    private static object[] Continuations(Task task)
    {
        FieldInfo field = RuntimeField(typeof(Task), "m_continuationObject")
            ?? throw new XunitException("运行时缺少 Task continuation 字段，不能跳过原任务等待证明。");
        object? continuation = field.GetValue(task);
        if (continuation is IList list)
        {
            lock (list) return list.Cast<object?>().OfType<object>().ToArray();
        }

        return continuation is null ? [] : [continuation];
    }

    private static FieldInfo? RuntimeField(Type type, string name)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            if (current.GetField(name, Fields | BindingFlags.DeclaredOnly) is { } field) return field;
        }

        return null;
    }

    private static async Task<Exception?> ObserveCaptureCompletionAsync(Task<CapturedFrame> original)
    {
        using var guardCancellation = new CancellationTokenSource();
        Task guard = Task.Delay(Guard, guardCancellation.Token);
        try
        {
            Assert.Same(original, await Task.WhenAny(original, guard));
            // 仅捕获原任务结果，不把 Guard Timeout 当成采集退出；意外成功时也释放帧。
            return await Record.ExceptionAsync(async () =>
            {
                using CapturedFrame unexpected = await original;
            });
        }
        finally { guardCancellation.Cancel(); }
    }

    private static string[] CleanupCalls(GdiTestNative native) => native.Calls
        .Where(point => point == "Restore" || point.StartsWith("Delete", StringComparison.Ordinal)).ToArray();

    private static void AssertBufferCleared(GdiTestNative native)
    {
        Assert.NotNull(native.CopiedBuffer);
        Assert.True(native.CopiedLength > 0);
        Assert.All(native.CopiedBuffer.Take(native.CopiedLength), value => Assert.Equal((byte)0, value));
    }
}
