using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using LanRemote.Capture;
using LanRemote.Core.Models;
using Xunit;
using Xunit.Sdk;

namespace LanRemote.Capture.Tests;

/// <summary>仅验证合成 native 的多屏合同，不代表真实 GDI、物理副屏或混合 DPI 验收。</summary>
[Collection(GdiBufferObservationCollection.Name)]
public sealed class GdiMultiDisplayCaptureTests
{
    private const uint Attached = GdiNative.AttachedToDesktop;
    private const uint Primary = GdiNative.PrimaryDevice;
    private const uint Mirroring = 0x00000008;
    private const uint RequiredFields = 0x00000020 | 0x00080000 | 0x00100000;
    private static readonly DisplayId LeftId = new(1);
    private static readonly DisplayId MainId = new(3);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);
    private static readonly string[] NormalCleanup = ["Restore", "DeleteBitmap", "DeleteMemory", "DeleteSource"];
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public async Task Factories_AreLazy_PreCanceledHasNoIo_AndLegacyConstructorStillUsesOnlyPrimary()
    {
        var native = new MultiNative();
        var multi = GdiScreenCaptureBackend.CreateMultiDisplay(native);
        var legacy = new GdiScreenCaptureBackend(native);
        Assert.NotSame(multi, legacy);
        Assert.Empty(native.Calls);
        Assert.Empty(native.Io.Owned);
        Assert.Null(multi.OriginalWorker);
        Assert.Null(new GdiScreenCaptureBackend().OriginalWorker);
        Assert.Null(GdiScreenCaptureBackend.CreateMultiDisplay().OriginalWorker);
        Assert.Throws<ArgumentNullException>(() => GdiScreenCaptureBackend.CreateMultiDisplay(null!));

        MethodInfo factory = Assert.Single(typeof(GdiScreenCaptureBackend).GetMethods(
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
            method => method.Name == "CreateMultiDisplay" && method.GetParameters().Length == 1);
        Assert.True(factory.IsAssembly);
        Assert.Equal(typeof(IGdiNative), Assert.Single(factory.GetParameters()).ParameterType);
        Assert.Equal(typeof(GdiScreenCaptureBackend), factory.ReturnType);
        MethodInfo parameterlessFactory = Assert.Single(typeof(GdiScreenCaptureBackend).GetMethods(
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
            method => method.Name == "CreateMultiDisplay" && method.GetParameters().Length == 0);
        Assert.True(parameterlessFactory.IsAssembly);
        Assert.Equal(typeof(GdiScreenCaptureBackend), parameterlessFactory.ReturnType);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        ValueTask<CapturedFrame> canceled = multi.CaptureAsync(LeftId, cancellation.Token);
        Assert.True(canceled.IsCanceled);
        Exception? canceledError = await ObserveCaptureCompletionAsync(canceled.AsTask());
        Assert.Equal(cancellation.Token,
            Assert.IsAssignableFrom<OperationCanceledException>(canceledError).CancellationToken);
        Assert.Empty(native.Calls);
        Assert.Null(multi.OriginalWorker);

        DisplayInfo primary = Assert.Single(legacy.GetDisplays());
        Assert.Equal(MainId, primary.Id);
        Assert.True(primary.IsPrimary);
        Assert.Equal(new[] { "1:Enum:0", "1:Enum:1", "1:Enum:2", "1:Enum:3", "1:Mode:MAIN" },
            native.Calls.ToArray());
        Assert.Empty(native.Io.Owned);
        Exception? unsupported = await ObserveCaptureCompletionAsync(legacy.CaptureAsync(LeftId, default).AsTask());
        Assert.IsType<NotSupportedException>(unsupported);
        Assert.DoesNotContain("CreateSource", native.Calls);
        Assert.Null(native.Io.CopiedBuffer);
        using (CapturedFrame frame = await legacy.CaptureAsync(MainId, default))
        {
            Assert.Equal(MainId, frame.DisplayId);
            Assert.Equal("MAIN", native.Io.SourceDeviceName);
            Assert.Equal(NormalCleanup, CleanupCalls(native));
            Assert.Empty(native.Io.Owned);
        }
        AssertBufferCleared(native);

        using CapturedFrame multiRetry = await multi.CaptureAsync(LeftId, default);
        Assert.Equal(LeftId, multiRetry.DisplayId);
        Assert.Equal("LEFT", native.Io.SourceDeviceName);
    }

    [Fact]
    public async Task GetDisplays_KeepsReadonlyFullRawSnapshot_AndOversizedOtherScreenDoesNotBlockSmallTarget()
    {
        var native = new MultiNative();
        GdiDevMode enormous = native.Modes["MAIN"];
        enormous.PelsWidth = 8193;
        enormous.PelsHeight = 8193;
        native.Modes["MAIN"] = enormous;
        var backend = GdiScreenCaptureBackend.CreateMultiDisplay(native);
        IReadOnlyList<DisplayInfo> snapshot = backend.GetDisplays();
        DisplayInfo[] original = snapshot.ToArray();
        Assert.Equal(new[] { 1, 3, 4 }, snapshot.Select(display => display.Id.Value).ToArray());
        Assert.Equal(new DisplayInfo(LeftId, "合成显示设备 LEFT", -30, -20, 3, 2, false), snapshot[0]);
        Assert.Equal(8193, snapshot[1].Width);
        Assert.Equal(8193, snapshot[1].Height);
        Assert.True(snapshot[1].IsPrimary);
        Assert.Equal(new DisplayInfo(new DisplayId(4), "合成显示设备 RIGHT", 8, -5, 2, 3, false), snapshot[2]);
        Assert.Equal(2, native.Enumerations);
        Assert.Equal(SnapshotCalls(1, native.Devices).Concat(SnapshotCalls(2, native.Devices)), native.Calls.ToArray());
        Assert.DoesNotContain("CreateSource", native.Calls);
        Assert.Empty(native.Io.Owned);
        Assert.Null(native.Io.CopiedBuffer);

        if (snapshot is IList<DisplayInfo> generic)
        {
            Assert.True(generic.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => generic[0] = original[1]);
            Assert.Throws<NotSupportedException>(() => generic.Add(original[1]));
            Assert.Throws<NotSupportedException>(() => generic.RemoveAt(0));
            Assert.Throws<NotSupportedException>(() => generic.Clear());
        }
        if (snapshot is IList nongeneric)
        {
            Assert.True(nongeneric.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => nongeneric[0] = original[1]);
            Assert.Throws<NotSupportedException>(() => nongeneric.Clear());
        }
        Assert.Equal(original, snapshot.ToArray());

        using (CapturedFrame frame = await backend.CaptureAsync(LeftId, default))
        {
            Assert.Equal(LeftId, frame.DisplayId);
            Assert.Equal(3, frame.Width);
            Assert.Equal(2, frame.Height);
            Assert.Equal("LEFT", native.Io.SourceDeviceName);
            Assert.Equal(6, native.Enumerations);
            Assert.Equal(NormalCleanup, CleanupCalls(native));
            Assert.Empty(native.Io.Owned);
        }
        AssertBufferCleared(native);
        native.Devices[1].DeviceString = "下次刷新名称";
        IReadOnlyList<DisplayInfo> refreshed = backend.GetDisplays();
        Assert.Equal(8, native.Enumerations);
        Assert.Equal("下次刷新名称", refreshed[0].Name);
        Assert.Equal(original, snapshot.ToArray());
        Assert.NotSame(snapshot, refreshed);
        Assert.Equal(1, native.Calls.Count(point => point == "CreateSource"));
    }

    [Fact]
    public async Task Capture_NegativeNonPrimaryFrameUsesCurrentRawIndex_TargetDc_AndExactOpaqueBgraOwnership()
    {
        var native = new MultiNative();
        var backend = GdiScreenCaptureBackend.CreateMultiDisplay(native);
        native.OnEnumeration = pass =>
        {
            if (pass is 3 or 4)
            {
                Assert.Equal(NormalCleanup, CleanupCalls(native));
                Assert.Empty(native.Io.Owned);
                Assert.False(native.Io.Selected);
            }
        };
        using (CapturedFrame frame = await backend.CaptureAsync(LeftId, default))
        {
            AssertFrame(native, frame, LeftId, "LEFT", 3, 2);
            Assert.Equal(4, native.Enumerations);
            string[] core = ["CreateSource", "CreateMemory", "CreateDib", "Select", "BitBlt", "Flush", "CopyPixels"];
            Assert.Equal(SnapshotCalls(1, native.Devices).Concat(SnapshotCalls(2, native.Devices))
                .Concat(core).Concat(NormalCleanup).Concat(SnapshotCalls(3, native.Devices))
                .Concat(SnapshotCalls(4, native.Devices)), native.Calls.ToArray());
            Assert.Equal(NormalCleanup, CleanupCalls(native));
            Assert.Empty(native.Io.Owned);
            Assert.False(native.Io.Selected);
            Assert.NotEqual((byte)0, native.Io.CopiedBuffer![0]);
            Assert.True(native.Io.CopiedBuffer.Length > native.Io.CopiedLength);
            Assert.Equal((byte)0xD3, native.Io.CopiedBuffer[^1]);
            frame.Dispose();
            frame.Dispose();
            Assert.Throws<ObjectDisposedException>(() => { _ = frame.Pixels; });
            AssertBufferCleared(native);
        }

        // ID 是当前原始索引，不是持久设备身份；同一实例不可缓存上次索引对应的 LEFT。
        native.OnEnumeration = null;
        (native.Devices[1], native.Devices[4]) = (native.Devices[4], native.Devices[1]);
        using (CapturedFrame current = await backend.CaptureAsync(LeftId, default))
        {
            AssertFrame(native, current, LeftId, "RIGHT", 2, 3);
            Assert.Equal(8, native.Enumerations);
        }
        AssertBufferCleared(native);
        using (CapturedFrame moved = await backend.CaptureAsync(new DisplayId(4), default))
        {
            AssertFrame(native, moved, new DisplayId(4), "LEFT", 3, 2);
            Assert.Equal(12, native.Enumerations);
            Assert.Empty(native.Io.Owned);
        }
        AssertBufferCleared(native);
    }

    [Theory]
    [InlineData(0u, 2u)]
    [InlineData(8193u, 2u)]
    [InlineData(3u, 8193u)]
    [InlineData(4096u, 4097u)]
    public async Task Capture_RejectsSelectedTargetInvalidDimensionsBeforeAnyDc(uint width, uint height)
    {
        var native = new MultiNative();
        GdiDevMode mode = native.Modes["LEFT"];
        mode.PelsWidth = width;
        mode.PelsHeight = height;
        native.Modes["LEFT"] = mode;
        Exception? error = await ObserveCaptureCompletionAsync(
            GdiScreenCaptureBackend.CreateMultiDisplay(native).CaptureAsync(LeftId, default).AsTask());
        if (width == 0) Assert.IsType<InvalidOperationException>(error);
        else Assert.IsType<ArgumentOutOfRangeException>(error);
        Assert.DoesNotContain("CreateSource", native.Calls);
        Assert.DoesNotContain("CreateMemory", native.Calls);
        Assert.DoesNotContain("CreateDib", native.Calls);
        Assert.DoesNotContain("BitBlt", native.Calls);
        Assert.Empty(native.Io.Owned);
        Assert.Null(native.Io.CopiedBuffer);
        Assert.Equal(width == 0 ? 1 : 2, native.Enumerations);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(6)]
    public async Task Capture_DetachedMirroredOrMissingRawIdNeverFallsBackToPrimaryOrFilteredIndex(int id)
    {
        var native = new MultiNative();
        Exception? error = await ObserveCaptureCompletionAsync(
            GdiScreenCaptureBackend.CreateMultiDisplay(native).CaptureAsync(new DisplayId(id), default).AsTask());
        Assert.IsType<NotSupportedException>(error);
        Assert.Equal(2, native.Enumerations);
        Assert.Equal(SnapshotCalls(1, native.Devices).Concat(SnapshotCalls(2, native.Devices)), native.Calls.ToArray());
        Assert.DoesNotContain("CreateSource", native.Calls);
        Assert.Null(native.Io.SourceDeviceName);
        Assert.Null(native.Io.CopiedBuffer);
        Assert.Empty(native.Io.Owned);
    }

    [Theory]
    [InlineData("ModeFields")]
    [InlineData("DuplicateName")]
    [InlineData("NoPrimary")]
    [InlineData("MultiplePrimaries")]
    public async Task Capture_UsesWholeCatalogValidationEvenWhenSelectedTargetIsValid(string fault)
    {
        var native = new MultiNative();
        switch (fault)
        {
            case "ModeFields":
                GdiDevMode mode = native.Modes["RIGHT"];
                mode.Fields &= ~0x00000020u;
                native.Modes["RIGHT"] = mode;
                break;
            case "DuplicateName": native.Devices[4].DeviceName = "left"; break;
            case "NoPrimary": native.Devices[3].StateFlags = Attached; break;
            case "MultiplePrimaries": native.Devices[4].StateFlags |= Primary; break;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }
        // 仅选取四个目录合同代表，不在这里复制目录自身的全部测试矩阵。
        Assert.Throws<InvalidOperationException>(() => new GdiDisplayCatalog(native).GetDisplays());
        native.ClearObservations();
        var backend = GdiScreenCaptureBackend.CreateMultiDisplay(native);
        Assert.Throws<InvalidOperationException>(() => backend.GetDisplays());
        Assert.DoesNotContain("CreateSource", native.Calls);
        native.ClearObservations();
        Exception? error = await ObserveCaptureCompletionAsync(backend.CaptureAsync(LeftId, default).AsTask());
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(1, native.Enumerations);
        Assert.DoesNotContain("CreateSource", native.Calls);
        Assert.Empty(native.Io.Owned);
        Assert.Null(native.Io.CopiedBuffer);
    }

    [Fact]
    public async Task Capture_CountsFilteredRawEntriesAt256Boundary_AndNeverTruncates257th()
    {
        var native = new MultiNative();
        native.Devices = Enumerable.Range(0, 256)
            .Select(index => GdiTestNative.Device($"RAW-{index}", 0)).ToArray();
        native.Devices[254] = GdiTestNative.Device("MAIN", Attached | Primary);
        native.Devices[255] = GdiTestNative.Device("LEFT", Attached);
        var backend = GdiScreenCaptureBackend.CreateMultiDisplay(native);
        using (CapturedFrame frame = await backend.CaptureAsync(new DisplayId(255), default))
        {
            AssertFrame(native, frame, new DisplayId(255), "LEFT", 3, 2);
            Assert.Equal(4, native.Enumerations);
            for (int pass = 1; pass <= 4; pass++)
            {
                Assert.Equal(Enumerable.Range(0, 257).Select(index => (uint)index),
                    native.Enumerated.Where(call => call.Pass == pass).Select(call => call.Index));
                Assert.Equal(new[] { "MAIN", "LEFT" },
                    native.ModeRequests.Where(call => call.Pass == pass).Select(call => call.DeviceName));
            }
            Assert.Equal(NormalCleanup, CleanupCalls(native));
            Assert.Empty(native.Io.Owned);
        }
        AssertBufferCleared(native);
        native.ClearObservations();
        native.Devices = [.. native.Devices, GdiTestNative.Device("OVER-LIMIT", 0)];
        Exception? error = await ObserveCaptureCompletionAsync(backend.CaptureAsync(new DisplayId(255), default).AsTask());
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(1, native.Enumerations);
        Assert.Equal(257, native.Enumerated.Count);
        Assert.Equal((1, 256u), native.Enumerated[^1]);
        Assert.DoesNotContain(native.ModeRequests, call => call.DeviceName == "OVER-LIMIT");
        Assert.DoesNotContain(native.Enumerated, call => call.Index > 256);
        Assert.DoesNotContain("CreateSource", native.Calls);
        Assert.Empty(native.Io.Owned);
        AssertBufferCleared(native);
    }

    [Fact]
    public async Task Capture_RejectsOtherScreenChangeBetweenOpeningPassesBeforeAnyPixelIo()
    {
        var native = new MultiNative();
        native.OnEnumeration = pass =>
        {
            if (pass == 2) native.Devices[4].DeviceKey = "改变的非目标设备键";
        };
        Exception? error = await ObserveCaptureCompletionAsync(
            GdiScreenCaptureBackend.CreateMultiDisplay(native).CaptureAsync(LeftId, default).AsTask());
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(2, native.Enumerations);
        Assert.Equal(SnapshotCalls(1, native.Devices).Concat(SnapshotCalls(2, native.Devices)), native.Calls.ToArray());
        Assert.DoesNotContain("CreateSource", native.Calls);
        Assert.DoesNotContain("CopyPixels", native.Calls);
        Assert.Empty(native.Io.Owned);
        Assert.Null(native.Io.CopiedBuffer);
    }

    [Theory]
    [InlineData("DeviceId")]
    [InlineData("DeviceKey")]
    [InlineData("DeviceName")]
    [InlineData("FriendlyName")]
    [InlineData("PositionX")]
    [InlineData("PositionY")]
    [InlineData("Width")]
    [InlineData("Height")]
    [InlineData("Orientation")]
    [InlineData("FixedOutput")]
    [InlineData("BitsPerPixel")]
    [InlineData("Frequency")]
    [InlineData("PanningWidth")]
    [InlineData("PanningHeight")]
    [InlineData("DisplayFlags")]
    [InlineData("StateFlags")]
    [InlineData("ModeFields")]
    [InlineData("OtherScreenMode")]
    [InlineData("AddOtherScreen")]
    [InlineData("RemoveOtherScreen")]
    [InlineData("ReorderOtherScreens")]
    public async Task Capture_RejectsStableNewWholeObservationAfterCleanup_AndClearsEntireBuffer(string change)
    {
        var native = new MultiNative();
        GdiDisplayDevice[] before = native.Devices.ToArray();
        int mutations = 0;
        native.OnEnumeration = pass =>
        {
            if (pass is not (3 or 4)) return;
            Assert.Equal(NormalCleanup, CleanupCalls(native));
            Assert.Empty(native.Io.Owned);
            Assert.False(native.Io.Selected);
            Assert.NotNull(native.Io.CopiedBuffer);
            Assert.NotEqual((byte)0, native.Io.CopiedBuffer[0]);
            Assert.Equal((byte)0xD3, native.Io.CopiedBuffer[^1]);
            if (pass == 3)
            {
                ChangeObservation(native, change);
                mutations++;
            }
        };
        Exception? error = await ObserveCaptureCompletionAsync(
            GdiScreenCaptureBackend.CreateMultiDisplay(native).CaptureAsync(LeftId, default).AsTask());
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(1, mutations);
        // 第三、四遍彼此一致，拒绝原因必须是与捕获前全量 Observation 不一致。
        Assert.Equal(4, native.Enumerations);
        for (int pass = 1; pass <= 4; pass++)
        {
            GdiDisplayDevice[] devices = pass <= 2 ? before : native.Devices;
            Assert.Equal(Enumerable.Range(0, devices.Length + 1).Select(index => (uint)index),
                native.Enumerated.Where(call => call.Pass == pass).Select(call => call.Index));
            Assert.Equal(devices.Where(IsAttached).Select(device => device.DeviceName),
                native.ModeRequests.Where(call => call.Pass == pass).Select(call => call.DeviceName));
        }
        Assert.Equal(NormalCleanup, CleanupCalls(native));
        Assert.Equal(1, native.Calls.Count(point => point == "CopyPixels"));
        Assert.Empty(native.Io.Owned);
        Assert.False(native.Io.Selected);
        AssertBufferCleared(native);
    }

    [Fact]
    public async Task Capture_LastPassOtherScreenNativeReadFailurePreservesExactExceptionAndClearsBuffer()
    {
        var native = new MultiNative();
        var original = new Win32Exception(5, "合成末检模式读取失败");
        native.MetadataFailures.Add("4:Mode:RIGHT", original);
        native.OnEnumeration = pass =>
        {
            if (pass < 3) return;
            Assert.Empty(native.Io.Owned);
            Assert.Equal(NormalCleanup, CleanupCalls(native));
        };
        Exception? error = await ObserveCaptureCompletionAsync(
            GdiScreenCaptureBackend.CreateMultiDisplay(native).CaptureAsync(LeftId, default).AsTask());
        Assert.Same(original, error);
        Assert.Equal(5, Assert.IsType<Win32Exception>(error).NativeErrorCode);
        Assert.Equal(4, native.Enumerations);
        Assert.Equal("4:Mode:RIGHT", native.Calls.Last());
        Assert.Equal(1, native.Calls.Count(point => point == "4:Mode:RIGHT"));
        Assert.Equal(NormalCleanup, CleanupCalls(native));
        Assert.Empty(native.Io.Owned);
        AssertBufferCleared(native);
    }

    [Theory]
    [InlineData("BitBlt", false)]
    [InlineData("CopyPixels", true)]
    [InlineData("DeleteSource", true)]
    [InlineData("Recheck", true)]
    public async Task Capture_CancellationKeepsOriginalWorkerAwaitAndBusyGateUntilNativeReturns(string phase, bool hasBuffer)
    {
        var native = new MultiNative();
        using var pause = new NativePause();
        using var cancellation = new CancellationTokenSource();
        native.Io.OnCall = point => { if (point == phase) pause.BlockNative(); };
        native.OnEnumeration = pass =>
        {
            if (phase == "Recheck" && pass == 4)
            {
                Assert.Empty(native.Io.Owned);
                Assert.Equal(NormalCleanup, CleanupCalls(native));
                pause.BlockNative();
            }
        };
        var backend = GdiScreenCaptureBackend.CreateMultiDisplay(native);
        Task<CapturedFrame>? operation = null;
        Task<CapturedFrame>? worker = null;
        Task<CapturedFrame>? busyTask = null;
        Task<CapturedFrame>? unexpectedWorker = null;
        Exception? completion = null;
        Exception? workerCompletion = null;
        try
        {
            operation = backend.CaptureAsync(LeftId, cancellation.Token).AsTask();
            worker = Assert.IsAssignableFrom<Task<CapturedFrame>>(backend.OriginalWorker);
            await AssertNativePausedAsync(pause, operation, worker);
            Assert.NotSame(operation, worker);
            await AssertDirectWorkerAwaitAsync(backend, operation, worker, pause);
            Assert.Equal(hasBuffer, native.Io.CopiedBuffer is not null);
            cancellation.Cancel();
            Assert.True(cancellation.IsCancellationRequested);
            Assert.False(operation.IsCompleted);
            Assert.False(worker.IsCompleted);
            await AssertDirectWorkerAwaitAsync(backend, operation, worker, pause);
            int callsBeforeBusy = native.Calls.Count;
            ValueTask<CapturedFrame> busy = backend.CaptureAsync(MainId, default);
            busyTask = busy.AsTask();
            unexpectedWorker = backend.OriginalWorker as Task<CapturedFrame>;
            Assert.True(busy.IsCompleted);
            Exception? busyError = await ObserveCaptureCompletionAsync(busyTask);
            Assert.IsType<InvalidOperationException>(busyError);
            Assert.Equal(callsBeforeBusy, native.Calls.Count);
            Assert.Same(worker, backend.OriginalWorker);
            Assert.False(operation.IsCompleted);
            Assert.False(worker.IsCompleted);
            await AssertDirectWorkerAwaitAsync(backend, operation, worker, pause);
        }
        finally
        {
            // 先放所有 native 闸，再 join 公开 Task 与原 worker；绝不拿超时代理当 worker 已退出。
            pause.Release();
            try
            {
                if (operation is not null) completion = await ObserveCaptureCompletionAsync(operation);
            }
            finally
            {
                worker ??= backend.OriginalWorker as Task<CapturedFrame>;
                if (worker is not null) workerCompletion = await ObserveCaptureCompletionAsync(worker);
                if (busyTask is not null) await ObserveCaptureCompletionAsync(busyTask);
                if (unexpectedWorker is not null && !ReferenceEquals(unexpectedWorker, worker))
                    await ObserveCaptureCompletionAsync(unexpectedWorker);
            }
        }
        Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(completion).CancellationToken);
        Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(workerCompletion).CancellationToken);
        Assert.True(operation!.IsCanceled);
        Assert.True(worker!.IsCompleted);
        Assert.Equal(NormalCleanup, CleanupCalls(native));
        Assert.Equal(phase == "Recheck" ? 4 : 2, native.Enumerations);
        Assert.Empty(native.Io.Owned);
        Assert.False(native.Io.Selected);
        if (hasBuffer) AssertBufferCleared(native);
        else Assert.Null(native.Io.CopiedBuffer);
        native.Io.OnCall = null;
        native.OnEnumeration = null;
        Assert.Equal(1, native.Calls.Count(point => point == "CreateSource"));
        using CapturedFrame retry = await backend.CaptureAsync(LeftId, default);
        Assert.Equal(LeftId, retry.DisplayId);
        Assert.Equal(2, native.Calls.Count(point => point == "CreateSource"));
        Assert.NotSame(worker, backend.OriginalWorker);
        Assert.Empty(native.Io.Owned);
    }

    [Fact]
    public async Task Capture_DifferentInstancesDoNotShareGateOrOriginalWorker()
    {
        var firstNative = new MultiNative();
        var secondNative = new MultiNative();
        using var firstPause = new NativePause();
        using var secondPause = new NativePause();
        firstNative.Io.OnCall = point => { if (point == "BitBlt") firstPause.BlockNative(); };
        secondNative.Io.OnCall = point => { if (point == "BitBlt") secondPause.BlockNative(); };
        var firstBackend = GdiScreenCaptureBackend.CreateMultiDisplay(firstNative);
        var secondBackend = GdiScreenCaptureBackend.CreateMultiDisplay(secondNative);
        // 直接杀死共享现有 gate 的变异，不以第二次采集等到超时来证明实例隔离。
        FieldInfo gateField = typeof(GdiScreenCaptureBackend).GetField("_captureGate", Fields | BindingFlags.Static)
            ?? throw new XunitException("缺少现有单飞 gate 字段。");
        Assert.False(gateField.IsStatic);
        object firstGate = Assert.IsType<object>(gateField.GetValue(firstBackend));
        object secondGate = Assert.IsType<object>(gateField.GetValue(secondBackend));
        Assert.NotSame(firstGate, secondGate);
        Task<CapturedFrame>? first = null;
        Task<CapturedFrame>? second = null;
        Task<CapturedFrame>? firstWorker = null;
        Task<CapturedFrame>? secondWorker = null;
        Exception? firstCompletion = null;
        Exception? secondCompletion = null;
        Exception? firstWorkerCompletion = null;
        Exception? secondWorkerCompletion = null;
        try
        {
            first = firstBackend.CaptureAsync(LeftId, default).AsTask();
            firstWorker = Assert.IsAssignableFrom<Task<CapturedFrame>>(firstBackend.OriginalWorker);
            await AssertNativePausedAsync(firstPause, first, firstWorker);
            await AssertDirectWorkerAwaitAsync(firstBackend, first, firstWorker, firstPause);
            second = secondBackend.CaptureAsync(new DisplayId(4), default).AsTask();
            secondWorker = Assert.IsAssignableFrom<Task<CapturedFrame>>(secondBackend.OriginalWorker);
            Assert.NotSame(firstWorker, secondWorker);
            await AssertNativePausedAsync(secondPause, second, secondWorker);
            await AssertDirectWorkerAwaitAsync(secondBackend, second, secondWorker, secondPause);
            Assert.Equal("RIGHT", secondNative.Io.SourceDeviceName);
            secondPause.Release();
            secondCompletion = await ObserveCaptureCompletionAsync(second);
            Assert.Null(secondCompletion);
            Assert.True(second.IsCompletedSuccessfully);
            Assert.False(first.IsCompleted);
            Assert.False(firstWorker.IsCompleted);
            Assert.False(firstPause.IsReleased);
            Assert.Equal(NormalCleanup, CleanupCalls(secondNative));
            Assert.Empty(secondNative.Io.Owned);
            AssertBufferCleared(secondNative);
        }
        finally
        {
            firstPause.Release();
            secondPause.Release();
            try
            {
                if (first is not null) firstCompletion = await ObserveCaptureCompletionAsync(first);
                if (second is not null) secondCompletion = await ObserveCaptureCompletionAsync(second);
            }
            finally
            {
                firstWorker ??= firstBackend.OriginalWorker as Task<CapturedFrame>;
                secondWorker ??= secondBackend.OriginalWorker as Task<CapturedFrame>;
                if (firstWorker is not null) firstWorkerCompletion = await ObserveCaptureCompletionAsync(firstWorker);
                if (secondWorker is not null) secondWorkerCompletion = await ObserveCaptureCompletionAsync(secondWorker);
            }
        }
        Assert.Null(firstCompletion);
        Assert.Null(secondCompletion);
        Assert.Null(firstWorkerCompletion);
        Assert.Null(secondWorkerCompletion);
        Assert.Equal(NormalCleanup, CleanupCalls(firstNative));
        Assert.Empty(firstNative.Io.Owned);
        AssertBufferCleared(firstNative);
        AssertBufferCleared(secondNative);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capture_PreservesNestedOriginalAndThreeCleanupSlots_WithRepeatedLeaves(bool restoreFails)
    {
        var native = new MultiNative();
        var repeated = new Win32Exception(5, "各槽重复引用的同一个合成叶异常");
        string[] points = ["CopyPixels", restoreFails ? "Restore" : "DeleteBitmap", "DeleteMemory", "DeleteSource"];
        var injected = new AggregateException[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            injected[i] = new AggregateException($"合成嵌套槽 {points[i]}",
                repeated, new AggregateException("重复叶不能压平或去重", repeated, repeated));
            native.Io.Failures.Add(points[i], injected[i]);
        }
        Exception? actual = await ObserveCaptureCompletionAsync(
            GdiScreenCaptureBackend.CreateMultiDisplay(native).CaptureAsync(LeftId, default).AsTask());
        var error = Assert.IsType<AggregateException>(actual);
        Assert.Equal(4, error.InnerExceptions.Count);
        Assert.Same(injected[0], error.InnerExceptions[0]);
        for (int i = 0; i < injected.Length; i++)
        {
            Exception? slot = i == 0 ? error.InnerExceptions[0] :
                Assert.IsType<InvalidOperationException>(error.InnerExceptions[i]).InnerException;
            Assert.Same(injected[i], slot);
            var nested = Assert.IsType<AggregateException>(slot);
            Assert.Equal(2, nested.InnerExceptions.Count);
            Assert.Same(repeated, nested.InnerExceptions[0]);
            var repeatedTree = Assert.IsType<AggregateException>(nested.InnerExceptions[1]);
            Assert.Equal(2, repeatedTree.InnerExceptions.Count);
            Assert.All(repeatedTree.InnerExceptions, leaf => Assert.Same(repeated, leaf));
            Assert.Equal(1, native.Calls.Count(point => point == points[i]));
        }
        Assert.Equal(2, native.Enumerations);
        Assert.DoesNotContain(native.Calls, point => point.StartsWith("3:", StringComparison.Ordinal));
        Assert.Equal(new[] { GdiTestNative.Source, GdiTestNative.Memory, GdiTestNative.Bitmap },
            native.Io.Owned.OrderBy(handle => (long)handle).ToArray());
        if (restoreFails)
        {
            Assert.Equal(new[] { "Restore", "DeleteMemory", "DeleteSource" }, CleanupCalls(native));
            Assert.DoesNotContain("DeleteBitmap", native.Calls);
            Assert.True(native.Io.Selected);
            Assert.Contains("保留可能仍选入的位图", error.InnerExceptions[2].Message);
        }
        else
        {
            Assert.Equal(NormalCleanup, CleanupCalls(native));
            Assert.False(native.Io.Selected);
        }
        AssertBufferCleared(native);
    }

    private static void AssertFrame(MultiNative native, CapturedFrame frame, DisplayId id,
        string deviceName, int width, int height)
    {
        int length = checked(width * height * 4);
        Assert.Equal(id, frame.DisplayId);
        Assert.Equal(deviceName, native.Io.SourceDeviceName);
        Assert.Equal(width, frame.Width);
        Assert.Equal(height, frame.Height);
        Assert.Equal(width * 4, frame.Stride);
        Assert.Equal(FramePixelFormat.Bgra32, frame.PixelFormat);
        Assert.Equal(length, frame.Pixels.Length);
        Assert.Equal((long)length, frame.EstimatedBytes);
        Assert.InRange(frame.TimestampUs, 0L, long.MaxValue);
        Assert.Equal(length, native.Io.CopiedLength);
        Assert.Equal(0x40CC0020u, native.Io.CopyOperation);
        Assert.Equal(40u, native.Io.DibInfo.Header.Size);
        Assert.Equal(width, native.Io.DibInfo.Header.Width);
        Assert.Equal(-height, native.Io.DibInfo.Header.Height);
        Assert.Equal((ushort)1, native.Io.DibInfo.Header.Planes);
        Assert.Equal((ushort)32, native.Io.DibInfo.Header.BitCount);
        Assert.Equal(0u, native.Io.DibInfo.Header.Compression);
        Assert.Equal((uint)length, native.Io.DibInfo.Header.SizeImage);
        byte[] pixels = frame.Pixels.ToArray();
        for (int i = 0; i < length; i++)
            Assert.Equal(i % 4 == 3 ? (byte)255 : GdiTestNative.PixelByte(i), pixels[i]);
    }

    private static void ChangeObservation(MultiNative native, string change)
    {
        GdiDevMode mode = native.Modes["LEFT"];
        switch (change)
        {
            case "DeviceId": native.Devices[1].DeviceId = "替换的目标 ID"; break;
            case "DeviceKey": native.Devices[1].DeviceKey = "替换的目标 KEY"; break;
            case "DeviceName":
                native.Devices[1].DeviceName = "RENAMED-LEFT";
                native.Modes.Add("RENAMED-LEFT", mode);
                break;
            case "FriendlyName": native.Devices[1].DeviceString = "替换的目标友好名称"; break;
            case "StateFlags": native.Devices[1].StateFlags |= 0x00000010; break;
            case "ModeFields": mode.Fields |= 0x00040000; break;
            case "PositionX": mode.PositionX--; break;
            case "PositionY": mode.PositionY--; break;
            case "Width": mode.PelsWidth++; break;
            case "Height": mode.PelsHeight++; break;
            case "Orientation": mode.DisplayOrientation++; break;
            case "FixedOutput": mode.DisplayFixedOutput++; break;
            case "BitsPerPixel": mode.BitsPerPel = 24; break;
            case "Frequency": mode.DisplayFrequency++; break;
            case "PanningWidth": mode.PanningWidth++; break;
            case "PanningHeight": mode.PanningHeight++; break;
            case "DisplayFlags": mode.DisplayFlags++; break;
            case "OtherScreenMode":
                GdiDevMode other = native.Modes["RIGHT"];
                other.DisplayFrequency++;
                native.Modes["RIGHT"] = other;
                break;
            case "AddOtherScreen":
                native.Devices = [.. native.Devices, GdiTestNative.Device("ADDED", Attached)];
                native.Modes.Add("ADDED", Mode(12, 0, 1, 1));
                break;
            case "RemoveOtherScreen": native.Devices = native.Devices.Where((_, index) => index != 4).ToArray(); break;
            case "ReorderOtherScreens":
                (native.Devices[3], native.Devices[4]) = (native.Devices[4], native.Devices[3]);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }
        native.Modes["LEFT"] = mode;
    }

    private static bool IsAttached(GdiDisplayDevice device) =>
        (device.StateFlags & Attached) != 0 && (device.StateFlags & Mirroring) == 0;

    private static string[] SnapshotCalls(int pass, GdiDisplayDevice[] devices)
    {
        var calls = new List<string>();
        for (int index = 0; index <= devices.Length; index++)
        {
            calls.Add($"{pass}:Enum:{index}");
            if (index < devices.Length && IsAttached(devices[index]))
                calls.Add($"{pass}:Mode:{devices[index].DeviceName}");
        }
        return calls.ToArray();
    }

    private static string[] CleanupCalls(MultiNative native) => native.Calls
        .Where(point => point == "Restore" || point.StartsWith("Delete", StringComparison.Ordinal)).ToArray();

    private static void AssertBufferCleared(MultiNative native)
    {
        byte[] buffer = Assert.IsType<byte[]>(native.Io.CopiedBuffer);
        Assert.True(native.Io.CopiedLength > 0);
        Assert.True(buffer.Length > native.Io.CopiedLength);
        // 不只扫描可见像素；合成复制事先污染了池的隐藏尾部。
        Assert.All(buffer, value => Assert.Equal((byte)0, value));
    }

    private static async Task<Exception?> ObserveCaptureCompletionAsync(Task<CapturedFrame> original) =>
        await Record.ExceptionAsync(async () =>
        {
            // 始终 await 原 Task；意外成功的帧也必须归还，不能让负断言泄漏 owner。
            using CapturedFrame frame = await original;
        });

    private static async Task AssertNativePausedAsync(NativePause pause, Task operation, Task worker)
    {
        using var watchdogCancellation = new CancellationTokenSource();
        Task watchdog = Task.Delay(Guard, watchdogCancellation.Token);
        try
        {
            while (!pause.HasEntered)
            {
                Assert.False(pause.IsReleased);
                Assert.False(operation.IsCompleted, "公开采集已退出，而预期的 native 阻塞点未进入。");
                Assert.False(worker.IsCompleted, "原 worker 已退出，而预期的 native 阻塞点未进入。");
                if (watchdog.IsCompleted)
                    throw new XunitException("诊断 watchdog：native 尚未进入测试闸；不视作取消或正常退出。");
                await Task.Yield();
            }
            Assert.False(pause.IsReleased);
            Assert.False(operation.IsCompleted);
            Assert.False(worker.IsCompleted);
        }
        finally { watchdogCancellation.Cancel(); }
    }

    private static async Task AssertDirectWorkerAwaitAsync(GdiScreenCaptureBackend backend,
        Task<CapturedFrame> operation, Task worker, NativePause pause)
    {
        using var watchdogCancellation = new CancellationTokenSource();
        Task watchdog = Task.Delay(Guard, watchdogCancellation.Token);
        try
        {
            while (true)
            {
                Assert.True(pause.HasEntered);
                Assert.False(pause.IsReleased);
                Assert.Same(worker, backend.OriginalWorker);
                Assert.False(worker.IsCompleted);
                Assert.False(operation.IsCompleted);
                Task? awaitedTask = OperationAwaitedTask(backend, operation);
                bool registered = false;
                if (awaitedTask is not null)
                {
                    // 观察到代理/WhenAny/WaitAsync 必须立即失败，不能等 watchdog 代替身份断言。
                    Assert.Same(worker, awaitedTask);
                    registered = Continuations(worker).Any(continuation =>
                        ReferenceEquals(continuation is Delegate action ? action.Target : continuation, operation));
                }
                Assert.Same(worker, backend.OriginalWorker);
                Assert.False(worker.IsCompleted);
                Assert.False(operation.IsCompleted);
                Assert.False(pause.IsReleased);
                if (registered) return;
                if (watchdog.IsCompleted)
                    throw new XunitException("诊断 watchdog：公开 operation 的 awaiter 或 continuation 尚未发布。");
                await Task.Yield();
            }
        }
        finally { watchdogCancellation.Cancel(); }
    }

    private static Task? OperationAwaitedTask(GdiScreenCaptureBackend backend, Task operation)
    {
        Type stateMachineType = typeof(GdiScreenCaptureBackend).GetMethod("CaptureOperationAsync", Fields)?
            .GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
            ?? throw new XunitException("缺少现有 CaptureOperationAsync 的实际状态机元数据。");
        FieldInfo stateMachineField = RuntimeField(operation.GetType(), "StateMachine")
            ?? throw new XunitException("公开 operation 缺少 StateMachine 字段。");
        object? stateMachine = stateMachineField.GetValue(operation);
        if (stateMachine is null) return null;
        Assert.IsType(stateMachineType, stateMachine);
        Assert.Same(backend, RuntimeField(stateMachineType, "<>4__this")?.GetValue(stateMachine));
        int state = Assert.IsType<int>(RuntimeField(stateMachineType, "<>1__state")?.GetValue(stateMachine));
        if (state == -1) return null;
        Assert.InRange(state, 0, int.MaxValue);
        object builder = RuntimeField(stateMachineType, "<>t__builder")?.GetValue(stateMachine)
            ?? throw new XunitException("状态机缺少实际 async builder。");
        Assert.Same(operation, RuntimeField(builder.GetType(), "m_task")?.GetValue(builder));
        FieldInfo awaiterField = Assert.Single(stateMachineType.GetFields(Fields),
            field => field.Name.StartsWith("<>u__", StringComparison.Ordinal));
        object awaiter = Assert.IsAssignableFrom<INotifyCompletion>(awaiterField.GetValue(stateMachine));
        FieldInfo taskField = Assert.Single(awaiter.GetType().GetFields(Fields),
            field => typeof(Task).IsAssignableFrom(field.FieldType));
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
            if (current.GetField(name, Fields | BindingFlags.DeclaredOnly) is { } field) return field;
        return null;
    }

    private static GdiDevMode Mode(int x, int y, uint width, uint height) => new()
    {
        Fields = RequiredFields,
        PositionX = x,
        PositionY = y,
        PelsWidth = width,
        PelsHeight = height,
        BitsPerPel = 32,
        DisplayFrequency = 60,
    };

    // 真正阻塞合成 native 的调用栈；只有测试 finally 放闸，没有超时假装 native 已完成。
    private sealed class NativePause : IDisposable
    {
        private readonly ManualResetEventSlim _entered = new();
        private readonly ManualResetEventSlim _release = new();
        internal bool HasEntered => _entered.IsSet;
        internal bool IsReleased => _release.IsSet;
        internal void BlockNative()
        {
            _entered.Set();
            _release.Wait();
        }
        internal void Release() => _release.Set();
        public void Dispose()
        {
            _entered.Dispose();
            _release.Dispose();
        }
    }

    // 元数据独立持有；资源 IO 只组合旧 helper，绝不调用它的 Devices/Mode 枚举逻辑。
    private sealed class MultiNative : IGdiNative
    {
        internal readonly GdiTestNative Io = new();
        internal ConcurrentQueue<string> Calls => Io.Calls;
        internal GdiDisplayDevice[] Devices =
        [
            GdiTestNative.Device("DETACHED", Primary),
            GdiTestNative.Device("LEFT", Attached),
            GdiTestNative.Device("MIRROR", Attached | Mirroring),
            GdiTestNative.Device("MAIN", Attached | Primary),
            GdiTestNative.Device("RIGHT", Attached),
            GdiTestNative.Device("TRAILING-DETACHED", 0),
        ];
        internal readonly Dictionary<string, GdiDevMode> Modes = new(StringComparer.Ordinal)
        {
            ["LEFT"] = Mode(-30, -20, 3, 2) with { DisplayOrientation = 1 },
            ["MAIN"] = Mode(0, 0, 8, 6),
            ["RIGHT"] = Mode(8, -5, 2, 3),
        };
        internal readonly Dictionary<string, Exception> MetadataFailures = new();
        internal readonly List<(int Pass, uint Index)> Enumerated = [];
        internal readonly List<(int Pass, string DeviceName)> ModeRequests = [];
        internal Action<int>? OnEnumeration;
        internal int Enumerations;

        public bool EnumDisplayDevices(uint index, out GdiDisplayDevice device)
        {
            if (index == 0)
            {
                Enumerations++;
                OnEnumeration?.Invoke(Enumerations);
            }
            Enumerated.Add((Enumerations, index));
            MetadataStep($"Enum:{index}");
            Assert.InRange(index, 0u, 256u);
            device = index < Devices.Length ? Devices[(int)index] : default;
            return index < Devices.Length;
        }

        public GdiDevMode GetDisplayMode(string deviceName)
        {
            ModeRequests.Add((Enumerations, deviceName));
            MetadataStep($"Mode:{deviceName}");
            Assert.Contains(Devices, device => device.DeviceName == deviceName && IsAttached(device));
            return Modes[deviceName];
        }

        private void MetadataStep(string point)
        {
            string key = $"{Enumerations}:{point}";
            Calls.Enqueue(key);
            if (MetadataFailures.TryGetValue(key, out Exception? error)) throw error;
        }

        internal void ClearObservations()
        {
            Calls.Clear();
            Enumerated.Clear();
            ModeRequests.Clear();
            Enumerations = 0;
        }

        public nint CreateSourceDc(string deviceName) => Io.CreateSourceDc(deviceName);
        public nint CreateMemoryDc(nint source) => Io.CreateMemoryDc(source);
        public nint CreateDibSection(nint dc, ref GdiBitmapInfo info, out nint bits) =>
            Io.CreateDibSection(dc, ref info, out bits);
        public nint SelectObject(nint dc, nint value) => Io.SelectObject(dc, value);
        public void BitBlt(nint destination, int width, int height, nint source, uint operation) =>
            Io.BitBlt(destination, width, height, source, operation);
        public void Flush() => Io.Flush();
        public void CopyPixels(nint bits, byte[] destination, int length)
        {
            // 24 字节图像会租到更长数组；同时污染尾部以区分只清可见段的错误实现。
            Array.Fill(destination, (byte)0xD3, length, destination.Length - length);
            Io.CopyPixels(bits, destination, length);
        }
        public void DeleteObject(nint value) => Io.DeleteObject(value);
        public void DeleteDc(nint dc) => Io.DeleteDc(dc);
    }
}
