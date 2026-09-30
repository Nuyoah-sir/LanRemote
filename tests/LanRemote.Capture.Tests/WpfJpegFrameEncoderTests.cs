using System.Buffers;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LanRemote.Core.Models;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace LanRemote.Capture.Tests;

[Collection(GdiBufferObservationCollection.Name)]
public sealed class WpfJpegFrameEncoderTests(ITestOutputHelper diagnostic)
{
    private const int SmallPayloadLimit = 16_381;
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);
    private static readonly VideoQualitySettings Settings = new(20, 1.0, 85, false);

    [Theory]
    [InlineData(1, 1, FramePixelFormat.Bgr24, 0.50, 40, 5, 0)]
    [InlineData(1, 1, FramePixelFormat.Bgra32, 0.67, 85, 30, 5)]
    [InlineData(65, 49, FramePixelFormat.Bgr24, 0.50, 40, 5, 7)]
    [InlineData(65, 49, FramePixelFormat.Bgra32, 0.50, 85, 10, 3)]
    [InlineData(65, 49, FramePixelFormat.Bgr24, 0.67, 60, 15, 0)]
    [InlineData(65, 49, FramePixelFormat.Bgra32, 0.67, 40, 20, 7)]
    [InlineData(65, 49, FramePixelFormat.Bgr24, 0.75, 85, 30, 5)]
    [InlineData(65, 49, FramePixelFormat.Bgra32, 0.75, 60, 5, 0)]
    [InlineData(65, 49, FramePixelFormat.Bgr24, 1.00, 40, 10, 3)]
    [InlineData(65, 49, FramePixelFormat.Bgra32, 1.00, 85, 15, 5)]
    public async Task Encode_RealJpegPreservesScaledPixelsMetadataAndExactPayload(
        int width, int height, FramePixelFormat format, double scale, int quality, int fps, int padding)
    {
        using CapturedFrame source = CreateFrame(out TrackingOwner owner, width, height, format, padding);
        byte[] original = owner.Bytes.ToArray();
        var settings = new VideoQualitySettings(fps, scale, quality, true);
        int expectedWidth = Math.Max(1, (int)Math.Floor(width * scale));
        int expectedHeight = Math.Max(1, (int)Math.Floor(height * scale));
        byte[] expectedPixels = ExpectedBgr(source, expectedWidth, expectedHeight);
        byte[]? savedBytes = null;
        long savedLength = 0;
        int saveCalls = 0;
        var encoder = new WpfJpegFrameEncoder((bitmap, actualQuality, stream) =>
        {
            saveCalls++;
            Assert.True(bitmap.IsFrozen);
            Assert.Equal(PixelFormats.Bgr24, bitmap.Format);
            Assert.Equal(expectedWidth, bitmap.PixelWidth);
            Assert.Equal(expectedHeight, bitmap.PixelHeight);
            Assert.Equal(quality, actualQuality);
            byte[] actualPixels = new byte[expectedPixels.Length];
            bitmap.CopyPixels(actualPixels, expectedWidth * 3, 0);
            Assert.Equal(expectedPixels, actualPixels);

            // 污染暂存尾部，再清空逻辑长度；交付不能包含旧尾部或池容量。
            var output = Assert.IsAssignableFrom<MemoryStream>(stream);
            byte[] poison = new byte[SmallPayloadLimit];
            Array.Fill(poison, (byte)0xA5);
            output.Write(poison);
            output.SetLength(0);
            output.Position = 0;
            WpfJpegFrameEncoder.SaveJpeg(bitmap, actualQuality, output);
            savedLength = output.Length;
            savedBytes = output.ToArray();
        }, SmallPayloadLimit);

        using EncodedFrame encoded = await encoder.EncodeAsync(source, settings, default);
        Assert.Equal(1, saveCalls);
        Assert.Equal(VideoCodec.Jpeg, encoded.Codec);
        Assert.Equal(expectedWidth, encoded.Width);
        Assert.Equal(expectedHeight, encoded.Height);
        Assert.Equal(source.TimestampUs, encoded.TimestampUs);
        Assert.Equal((byte)quality, encoded.JpegQuality);
        Assert.Equal(1UL, encoded.FrameId);
        Assert.InRange(encoded.PayloadLength, 1, SmallPayloadLimit - 1);
        Assert.Equal(savedLength, (long)encoded.PayloadLength);
        Assert.Equal(encoded.PayloadLength, encoded.Payload.Length);
        Assert.NotNull(savedBytes);
        Assert.Equal(savedBytes, encoded.Payload.ToArray());
        byte[] decoded = DecodeBgr(encoded);
        Assert.Equal(expectedPixels.Length, decoded.Length);
        for (int i = 0; i < decoded.Length; i++)
        {
            Assert.InRange(Math.Abs(decoded[i] - expectedPixels[i]), 0, 24);
        }

        AssertBorrowed(owner, original);
        encoded.Dispose();
        Assert.Equal(0, encoded.PayloadLength);
        Assert.Throws<ObjectDisposedException>(() => { _ = encoded.Payload; });
        AssertBorrowed(owner, original);
    }

    [Fact]
    public async Task Encode_PublicEncoderIgnoresAlphaAndKeepsInputAndOutputsIndependent()
    {
        using CapturedFrame bgr = CreateFrame(out TrackingOwner bgrOwner, 17, 13, FramePixelFormat.Bgr24, 7);
        using CapturedFrame transparent = CreateFrame(out TrackingOwner transparentOwner, 17, 13, FramePixelFormat.Bgra32, 3, alpha: 0);
        using CapturedFrame opaque = CreateFrame(out TrackingOwner opaqueOwner, 17, 13, FramePixelFormat.Bgra32, 5, alpha: 255);
        byte[] bgrOriginal = bgrOwner.Bytes.ToArray();
        byte[] transparentOriginal = transparentOwner.Bytes.ToArray();
        byte[] opaqueOriginal = opaqueOwner.Bytes.ToArray();
        var encoder = new WpfJpegFrameEncoder();
        using EncodedFrame first = await encoder.EncodeAsync(bgr, Settings, default);
        byte[] firstPayload = first.Payload.ToArray();
        using EncodedFrame second = await encoder.EncodeAsync(transparent, Settings, default);
        using EncodedFrame third = await encoder.EncodeAsync(opaque, Settings, default);
        Assert.Equal(firstPayload, second.Payload.ToArray());
        Assert.Equal(firstPayload, third.Payload.ToArray());
        Assert.Equal(1UL, first.FrameId);
        Assert.Equal(2UL, second.FrameId);
        Assert.Equal(3UL, third.FrameId);
        Assert.Equal(DecodeBgr(first), DecodeBgr(second));
        Assert.Equal(DecodeBgr(first), DecodeBgr(third));
        second.Dispose();
        third.Dispose();
        Assert.Equal(firstPayload, first.Payload.ToArray());
        AssertBorrowed(bgrOwner, bgrOriginal);
        AssertBorrowed(transparentOwner, transparentOriginal);
        AssertBorrowed(opaqueOwner, opaqueOriginal);

        bgr.Dispose();
        Assert.Equal(1, bgrOwner.DisposeCalls);
        Array.Fill(bgrOwner.Bytes, (byte)0xE7);
        Assert.Equal(firstPayload, first.Payload.ToArray());
        Assert.NotEmpty(DecodeBgr(first));
        first.Dispose();
        Assert.Equal(1, bgrOwner.DisposeCalls);
    }

    [Theory]
    [InlineData(0, 1.0, 60)]
    [InlineData(6, 1.0, 60)]
    [InlineData(31, 1.0, 60)]
    [InlineData(20, 0.0, 60)]
    [InlineData(20, 0.66, 60)]
    [InlineData(20, 1.01, 60)]
    [InlineData(20, double.NaN, 60)]
    [InlineData(20, double.PositiveInfinity, 60)]
    [InlineData(20, double.NegativeInfinity, 60)]
    [InlineData(20, 1.0, 39)]
    [InlineData(20, 1.0, 86)]
    public async Task Encode_RejectsInvalidSettingsWithoutDisposingInputOrConsumingId(int fps, double scale, int quality)
    {
        using CapturedFrame source = CreateFrame(out TrackingOwner owner);
        byte[] original = owner.Bytes.ToArray();
        int saves = 0;
        var encoder = new WpfJpegFrameEncoder((bitmap, q, output) =>
        {
            saves++;
            WpfJpegFrameEncoder.SaveJpeg(bitmap, q, output);
        }, SmallPayloadLimit);
        ValueTask<EncodedFrame> operation = encoder.EncodeAsync(source, new VideoQualitySettings(fps, scale, quality, false), default);
        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => DisposeResultAsync(operation));
        Assert.Equal("settings", error.ParamName);
        Assert.Equal(0, saves);
        Assert.Null(encoder.OriginalWorker);
        AssertBorrowed(owner, original);
        using EncodedFrame retry = await encoder.EncodeAsync(source, Settings, default);
        Assert.Equal(1UL, retry.FrameId);
        Assert.Equal(1, saves);
        AssertBorrowed(owner, original);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Encode_RejectsNullArguments(bool nullFrame)
    {
        using CapturedFrame source = CreateFrame(out TrackingOwner owner);
        var encoder = new WpfJpegFrameEncoder(WpfJpegFrameEncoder.SaveJpeg, SmallPayloadLimit);
        ValueTask<EncodedFrame> operation = encoder.EncodeAsync(nullFrame ? null! : source, nullFrame ? Settings : null!, default);
        var error = await Assert.ThrowsAsync<ArgumentNullException>(() => DisposeResultAsync(operation));
        Assert.Equal(nullFrame ? "frame" : "settings", error.ParamName);
        Assert.Null(encoder.OriginalWorker);
        Assert.Equal(0, owner.DisposeCalls);
        using EncodedFrame retry = await encoder.EncodeAsync(source, Settings, default);
        Assert.Equal(1UL, retry.FrameId);
    }

    [Theory]
    [InlineData("短 owner")]
    [InlineData("未知格式")]
    [InlineData("负时间戳")]
    [InlineData("已释放")]
    [InlineData("stride 溢出")]
    public async Task Encode_RejectsInvalidInputAndRecovers(string kind)
    {
        var owner = new TrackingOwner(kind == "短 owner" ? 23 : 24);
        Array.Fill(owner.Bytes, (byte)0x5A);
        using var source = new CapturedFrame(new DisplayId(7), 3, 2,
            kind == "stride 溢出" ? int.MaxValue : 12,
            kind == "未知格式" ? (FramePixelFormat)1234 : FramePixelFormat.Bgra32,
            owner, kind == "负时间戳" ? -1 : 123);
        byte[] original = owner.Bytes.ToArray();
        if (kind == "已释放") source.Dispose();
        int saves = 0;
        var encoder = new WpfJpegFrameEncoder((bitmap, quality, output) =>
        {
            saves++;
            WpfJpegFrameEncoder.SaveJpeg(bitmap, quality, output);
        }, SmallPayloadLimit);

        ValueTask<EncodedFrame> operation = encoder.EncodeAsync(source, Settings, default);
        Exception? error = await Record.ExceptionAsync(() => DisposeResultAsync(operation));
        if (kind == "已释放") Assert.IsType<ObjectDisposedException>(error);
        else if (kind == "短 owner") Assert.IsType<ArgumentException>(error);
        else Assert.IsType<ArgumentOutOfRangeException>(error);
        Assert.Equal(0, saves);
        Assert.Equal(kind == "已释放" ? 1 : 0, owner.DisposeCalls);
        Assert.Equal(original, owner.Bytes);
        using CapturedFrame valid = CreateFrame(out TrackingOwner validOwner);
        using EncodedFrame retry = await encoder.EncodeAsync(valid, Settings, default);
        Assert.Equal(1UL, retry.FrameId);
        Assert.Equal(1, saves);
        Assert.NotEmpty(DecodeBgr(retry));
        Assert.Equal(0, validOwner.DisposeCalls);
        Assert.Equal(kind == "已释放" ? 1 : 0, owner.DisposeCalls);
    }

    [Fact]
    public async Task Encode_PreCanceledTokenDoesNotReadOrDisposeInputOrConsumeId()
    {
        using CapturedFrame source = CreateFrame(out TrackingOwner owner);
        byte[] original = owner.Bytes.ToArray();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int saves = 0;
        var encoder = new WpfJpegFrameEncoder((bitmap, quality, output) =>
        {
            saves++;
            WpfJpegFrameEncoder.SaveJpeg(bitmap, quality, output);
        }, SmallPayloadLimit);
        Task<EncodedFrame> operation = encoder.EncodeAsync(source, Settings, cancellation.Token).AsTask();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using EncodedFrame unexpected = await operation;
        });
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(operation.IsCanceled);
        Assert.Null(encoder.OriginalWorker);
        Assert.Equal(0, saves);
        Assert.Equal(0, owner.MemoryReads);
        AssertBorrowed(owner, original);
        using EncodedFrame retry = await encoder.EncodeAsync(source, Settings, default);
        Assert.Equal(1UL, retry.FrameId);
        Assert.Equal(1, saves);
        AssertBorrowed(owner, original);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Encode_SingleFlightAndCancellationWaitForOriginalSynchronousSave(bool cancel, bool afterSave)
    {
        using CapturedFrame source = CreateFrame(out TrackingOwner owner);
        byte[] original = owner.Bytes.ToArray();
        using var cancellation = new CancellationTokenSource();
        using var releaseSave = new ManualResetEventSlim();
        using var releaseReturn = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int saves = 0;
        int saveReturned = 0;
        Stream? heldStream = null;
        var encoder = new WpfJpegFrameEncoder((bitmap, quality, output) =>
        {
            if (Interlocked.Increment(ref saves) != 1)
            {
                WpfJpegFrameEncoder.SaveJpeg(bitmap, quality, output);
                return;
            }

            heldStream = output;
            try
            {
                entered.TrySetResult();
                if (!releaseSave.Wait(Guard)) throw new TimeoutException("Save 前同步闸未放行。");
                WpfJpegFrameEncoder.SaveJpeg(bitmap, quality, output);
                saved.TrySetResult();
                if (!releaseReturn.Wait(Guard)) throw new TimeoutException("Save 返回前同步闸未放行。");
            }
            finally
            {
                Volatile.Write(ref saveReturned, 1);
            }
        }, SmallPayloadLimit);
        Task<EncodedFrame> operation = encoder.EncodeAsync(source, Settings, cancellation.Token).AsTask();

        async Task<(Exception? Error, ulong? FrameId, bool SaveReturned)> ObserveCompletionAsync(Task<EncodedFrame> originalTask)
        {
            using var guardCancellation = new CancellationTokenSource();
            Task guard = Task.Delay(Guard, guardCancellation.Token);
            try
            {
                Assert.Same(originalTask, await Task.WhenAny(originalTask, guard));
                // Guard 仅防挂起；必须 await 原任务，意外交付时也释放帧。
                try
                {
                    using EncodedFrame result = await originalTask;
                    return (null, result.FrameId, Volatile.Read(ref saveReturned) == 1);
                }
                catch (Exception error)
                {
                    return (error, null, Volatile.Read(ref saveReturned) == 1);
                }
            }
            finally { guardCancellation.Cancel(); }
        }

        Task<EncodedFrame>? worker = null;
        Task<EncodedFrame>? busy = null;
        (Exception? Error, ulong? FrameId, bool SaveReturned) completion;
        (Exception? Error, ulong? FrameId, bool SaveReturned) workerCompletion;
        try
        {
            await entered.Task.WaitAsync(Guard);
            worker = Assert.IsAssignableFrom<Task<EncodedFrame>>(encoder.OriginalWorker);
            Assert.NotSame(operation, worker);
            if (afterSave)
            {
                releaseSave.Set();
                await saved.Task.WaitAsync(Guard);
            }
            ManualResetEventSlim heldGate = afterSave ? releaseReturn : releaseSave;
            // 到达事件只说明 Save 已进入；正向证明公开 operation 直接 await 这个原 worker。
            await AssertDirectWorkerAwaitAsync(encoder, operation, worker, heldGate);
            if (cancel) cancellation.Cancel();
            await AssertDirectWorkerAwaitAsync(encoder, operation, worker, heldGate);
            Assert.Equal(0, Volatile.Read(ref saveReturned));
            AssertBorrowed(owner, original);

            busy = encoder.EncodeAsync(source, Settings, default).AsTask();
            Assert.True(busy.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => busy);
            Assert.Equal(1, Volatile.Read(ref saves));
            Assert.Same(worker, encoder.OriginalWorker);
            await AssertDirectWorkerAwaitAsync(encoder, operation, worker, heldGate);

            // 单飞是实例级的；另一个实例完成不能解除当前实例对原 worker 的等待。
            var independent = new WpfJpegFrameEncoder(WpfJpegFrameEncoder.SaveJpeg, SmallPayloadLimit);
            using EncodedFrame other = await independent.EncodeAsync(source, Settings, default);
            Assert.Equal(1UL, other.FrameId);
            await AssertDirectWorkerAwaitAsync(encoder, operation, worker, heldGate);
            Assert.Equal(0, Volatile.Read(ref saveReturned));
        }
        finally
        {
            releaseSave.Set();
            releaseReturn.Set();
            // 公开 operation 即使被错误实现提前结束，也必须另行 join 原 Task.Run，再释放输入和闸。
            try
            {
                completion = await ObserveCompletionAsync(operation);
            }
            finally
            {
                try
                {
                    worker ??= Assert.IsAssignableFrom<Task<EncodedFrame>>(encoder.OriginalWorker);
                    workerCompletion = await ObserveCompletionAsync(worker);
                }
                finally
                {
                    if (busy is not null) await ObserveCompletionAsync(busy);
                }
            }
        }

        Assert.True(saved.Task.IsCompletedSuccessfully);
        Assert.True(completion.SaveReturned);
        Assert.True(workerCompletion.SaveReturned);
        Assert.NotNull(heldStream);
        Assert.False(heldStream.CanWrite);
        if (cancel)
        {
            var error = Assert.IsAssignableFrom<OperationCanceledException>(completion.Error);
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.True(operation.IsCanceled);
            Assert.Null(completion.FrameId);
            Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(workerCompletion.Error).CancellationToken);
            Assert.Null(workerCompletion.FrameId);
        }
        else
        {
            Assert.Null(completion.Error);
            Assert.Equal((ulong?)1UL, completion.FrameId);
            Assert.Null(workerCompletion.Error);
            Assert.Equal(completion.FrameId, workerCompletion.FrameId);
        }
        AssertBorrowed(owner, original);
        using EncodedFrame retry = await encoder.EncodeAsync(source, Settings, default);
        Assert.Equal(cancel ? 1UL : 2UL, retry.FrameId);
        Assert.Equal(2, saves);
        Assert.NotSame(worker, encoder.OriginalWorker);
        Assert.NotEmpty(DecodeBgr(retry));
        AssertBorrowed(owner, original);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Encode_SaveFailureOrEmptyOutputRecoversWithoutConsumingId(bool emptyOutput)
    {
        using CapturedFrame source = CreateFrame(out TrackingOwner owner);
        byte[] original = owner.Bytes.ToArray();
        var originalError = new IOException("合成 Save 失败。");
        Stream? failedStream = null;
        int saves = 0;
        var encoder = new WpfJpegFrameEncoder((bitmap, quality, output) =>
        {
            saves++;
            if (saves == 2)
            {
                failedStream = output;
                if (emptyOutput) return;
                WpfJpegFrameEncoder.SaveJpeg(bitmap, quality, output);
                throw originalError;
            }
            WpfJpegFrameEncoder.SaveJpeg(bitmap, quality, output);
        }, SmallPayloadLimit);
        using EncodedFrame first = await encoder.EncodeAsync(source, Settings, default);
        byte[] firstPayload = first.Payload.ToArray();
        Assert.Equal(1UL, first.FrameId);
        Exception? error = await Record.ExceptionAsync(() => DisposeResultAsync(encoder.EncodeAsync(source, Settings, default)));
        if (emptyOutput) Assert.IsType<InvalidDataException>(error);
        else Assert.Same(originalError, error);
        Assert.NotNull(failedStream);
        Assert.False(failedStream.CanWrite);
        AssertBorrowed(owner, original);
        using EncodedFrame retry = await encoder.EncodeAsync(source, Settings, default);
        Assert.Equal(2UL, retry.FrameId);
        Assert.Equal(3, saves);
        Assert.Equal(firstPayload, first.Payload.ToArray());
        Assert.NotEmpty(DecodeBgr(retry));
        AssertBorrowed(owner, original);
    }

    [Fact]
    public async Task Encode_LastSequenceIsDeliveredOnceAndNeverWrapsEvenAfterFailure()
    {
        using CapturedFrame source = CreateFrame(out TrackingOwner owner);
        byte[] original = owner.Bytes.ToArray();
        var failure = new IOException("最大序号前合成失败。");
        int saves = 0;
        var encoder = new WpfJpegFrameEncoder((bitmap, quality, output) =>
        {
            saves++;
            WpfJpegFrameEncoder.SaveJpeg(bitmap, quality, output);
            if (saves == 2) throw failure;
        }, SmallPayloadLimit, ulong.MaxValue - 1);
        using EncodedFrame penultimate = await encoder.EncodeAsync(source, Settings, default);
        Assert.Equal(ulong.MaxValue - 1, penultimate.FrameId);
        Exception? error = await Record.ExceptionAsync(() => DisposeResultAsync(encoder.EncodeAsync(source, Settings, default)));
        Assert.Same(failure, error);
        using EncodedFrame last = await encoder.EncodeAsync(source, Settings, default);
        Assert.Equal(ulong.MaxValue, last.FrameId);
        byte[] lastPayload = last.Payload.ToArray();
        for (int i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => DisposeResultAsync(encoder.EncodeAsync(source, Settings, default)));
        }
        Assert.Equal(3, saves);
        Assert.Equal(lastPayload, last.Payload.ToArray());
        Assert.NotEmpty(DecodeBgr(last));
        AssertBorrowed(owner, original);
    }

    [Fact]
    public async Task Encode_RealWpfCannotExceedSmallHardLimitAndCanRetrySmallerImage()
    {
        const int limit = 1021;
        using CapturedFrame noisy = CreateFrame(out TrackingOwner owner, 129, 97, FramePixelFormat.Bgr24, 5);
        new Random(731).NextBytes(owner.Bytes);
        byte[] original = owner.Bytes.ToArray();
        JpegOutputStream? failedStream = null;
        Exception? saveFailure = null;
        bool firstSaveCompleted = false;
        long failureLength = -1;
        int saves = 0;
        var encoder = new WpfJpegFrameEncoder((bitmap, quality, output) =>
        {
            saves++;
            if (saves == 1)
            {
                var memory = Assert.IsAssignableFrom<MemoryStream>(output);
                failedStream = Assert.IsType<JpegOutputStream>(memory);
                Assert.Equal(limit, memory.Capacity);
                Assert.Equal(0L, memory.Length);
                Assert.Null(failedStream.Failure);
                try
                {
                    WpfJpegFrameEncoder.SaveJpeg(bitmap, quality, output);
                    firstSaveCompleted = true;
                }
                catch (Exception failure)
                {
                    saveFailure = failure;
                    throw;
                }
                finally
                {
                    failureLength = output.Length;
                    diagnostic.WriteLine($"真实 Save 调用={saves}，返回={firstSaveCompleted}，容量={memory.Capacity}，长度={failureLength}");
                }
            }
            else WpfJpegFrameEncoder.SaveJpeg(bitmap, quality, output);
        }, limit);

        // WIC 可以吞掉或包装写异常；实际底层失败必须锁存，最终错误必须保留同一个原对象。
        Exception? error = await Record.ExceptionAsync(() => DisposeResultAsync(encoder.EncodeAsync(noisy, Settings, default)));
        Assert.NotNull(failedStream);
        var writeFailure = Assert.IsType<NotSupportedException>(failedStream.Failure);
        Assert.Contains("System.IO.MemoryStream.Write", writeFailure.StackTrace);
        Assert.NotNull(error);
        Assert.True(ExceptionTreeContains(error, writeFailure));
        if (firstSaveCompleted)
        {
            Assert.Null(saveFailure);
            Assert.Same(writeFailure, error);
        }
        else
        {
            Assert.NotNull(saveFailure);
            Assert.True(ExceptionTreeContains(error, saveFailure));
        }
        diagnostic.WriteLine($"锁存原错误与最终错误同一对象={ReferenceEquals(writeFailure, error)}；最终错误：{error}");
        Assert.Equal(1, saves);
        Assert.InRange(failureLength, 0L, (long)limit);
        Assert.False(failedStream.CanWrite);
        AssertBorrowed(owner, original);
        using CapturedFrame tiny = CreateFrame(out TrackingOwner tinyOwner, 1, 1);
        using EncodedFrame retry = await encoder.EncodeAsync(tiny, Settings, default);
        Assert.Equal(1UL, retry.FrameId);
        Assert.Equal(2, saves);
        Assert.InRange(retry.PayloadLength, 1, limit);
        Assert.Equal(3, DecodeBgr(retry).Length);
        Assert.Equal(0, tinyOwner.DisposeCalls);
        AssertBorrowed(owner, original);
    }

    [Theory]
    [InlineData("Write")]
    [InlineData("WriteByte")]
    [InlineData("SetLength")]
    [InlineData("Capacity")]
    public async Task Encode_StagingIsNonExpandableAtLogicalLimitNotPoolBucketSize(string overflow)
    {
        const int limit = 1021;
        using CapturedFrame source = CreateFrame(out TrackingOwner owner, 1, 1);
        byte[] original = owner.Bytes.ToArray();
        int saves = 0;
        MemoryStream? failedStream = null;
        var encoder = new WpfJpegFrameEncoder((bitmap, quality, output) =>
        {
            saves++;
            var stream = Assert.IsAssignableFrom<MemoryStream>(output);
            Assert.Equal(limit, stream.Capacity);
            Assert.Equal(0L, stream.Length);
            Assert.False(stream.TryGetBuffer(out _));
            if (saves == 1)
            {
                failedStream = stream;
                // limit 故意不是池桶边界；用实际越界操作排除“初始容量等于上限”的伪约束。
                switch (overflow)
                {
                    case "Write":
                        stream.Position = limit - 1;
                        stream.Write(new byte[] { 0xA5, 0x5A }, 0, 2);
                        break;
                    case "WriteByte":
                        stream.Position = limit;
                        stream.WriteByte(0xA5);
                        break;
                    case "SetLength":
                        stream.SetLength(limit + 1);
                        break;
                    case "Capacity":
                        stream.Capacity = limit + 1;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(overflow));
                }
                return;
            }
            WpfJpegFrameEncoder.SaveJpeg(bitmap, quality, output);
        }, limit);

        await Assert.ThrowsAsync<NotSupportedException>(() => DisposeResultAsync(encoder.EncodeAsync(source, Settings, default)));
        Assert.NotNull(failedStream);
        Assert.False(failedStream.CanWrite);
        AssertBorrowed(owner, original);
        using EncodedFrame retry = await encoder.EncodeAsync(source, Settings, default);
        Assert.Equal(1UL, retry.FrameId);
        Assert.Equal(2, saves);
        Assert.Equal(3, DecodeBgr(retry).Length);
        AssertBorrowed(owner, original);
    }

    [Theory]
    [InlineData("Write", typeof(NotSupportedException))]
    [InlineData("WriteSpan", typeof(NotSupportedException))]
    [InlineData("WriteByte", typeof(NotSupportedException))]
    [InlineData("WriteAsync", typeof(NotSupportedException))]
    [InlineData("WriteMemoryAsync", typeof(NotSupportedException))]
    [InlineData("SetLength", typeof(NotSupportedException))]
    [InlineData("Capacity", typeof(NotSupportedException))]
    [InlineData("Seek", typeof(IOException))]
    [InlineData("Position", typeof(ArgumentOutOfRangeException))]
    public async Task Encode_SaveSwallowsOutputFailureAndReturnsPartialBytesMustNotDeliver(string method, Type expectedFailure)
    {
        const int limit = 1021;
        using CapturedFrame source = CreateFrame(out TrackingOwner owner, 1, 1);
        byte[] original = owner.Bytes.ToArray();
        JpegOutputStream? failedStream = null;
        Exception? swallowed = null;
        bool saveReturned = false;
        long partialLength = -1;
        int saves = 0;
        var encoder = new WpfJpegFrameEncoder((bitmap, quality, output) =>
        {
            var stream = Assert.IsType<JpegOutputStream>(output);
            Assert.Null(stream.Failure);
            if (++saves != 1)
            {
                WpfJpegFrameEncoder.SaveJpeg(bitmap, quality, output);
                return;
            }

            failedStream = stream;
            Assert.Equal(limit, stream.Capacity);
            stream.Position = limit;
            swallowed = Record.Exception(() =>
            {
                switch (method)
                {
                    case "Write": stream.Write(new byte[] { 0xA5 }, 0, 1); break;
                    case "WriteSpan": stream.Write(new byte[] { 0xA5 }.AsSpan()); break;
                    case "WriteByte": stream.WriteByte(0xA5); break;
                    case "WriteAsync": stream.WriteAsync(new byte[] { 0xA5 }, 0, 1).GetAwaiter().GetResult(); break;
                    case "WriteMemoryAsync": stream.WriteAsync(new byte[] { 0xA5 }.AsMemory()).GetAwaiter().GetResult(); break;
                    case "SetLength": stream.SetLength(limit + 1); break;
                    case "Capacity": stream.Capacity = limit + 1; break;
                    case "Seek": stream.Seek(-1, SeekOrigin.Begin); break;
                    case "Position": stream.Position = -1; break;
                    default: throw new ArgumentOutOfRangeException(nameof(method));
                }
            });
            Assert.NotNull(swallowed);
            Assert.IsType(expectedFailure, swallowed);
            Assert.Same(swallowed, stream.Failure);

            // 主动吞错后重置并伪造非空片段；即使首尾标记存在，也不能凭长度交付。
            stream.SetLength(0);
            stream.Seek(0, SeekOrigin.Begin);
            stream.Write(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
            partialLength = stream.Length;
            Assert.Same(swallowed, stream.Failure);
            saveReturned = true;
        }, limit);

        Exception? error = await Record.ExceptionAsync(() => DisposeResultAsync(encoder.EncodeAsync(source, Settings, default)));
        Assert.True(saveReturned);
        Assert.Equal(4L, partialLength);
        Assert.NotNull(swallowed);
        Assert.Same(swallowed, error);
        Assert.NotNull(failedStream);
        Assert.False(failedStream.CanWrite);
        AssertBorrowed(owner, original);
        diagnostic.WriteLine($"吞错接缝={method}，Save 返回={saveReturned}，片段长度={partialLength}，最终错误保留原对象={ReferenceEquals(swallowed, error)}");
        using EncodedFrame retry = await encoder.EncodeAsync(source, Settings, default);
        Assert.Equal(1UL, retry.FrameId);
        Assert.Equal(2, saves);
        Assert.Equal(3, DecodeBgr(retry).Length);
        AssertBorrowed(owner, original);
    }

    [Theory]
    [InlineData("独立")]
    [InlineData("嵌套")]
    [InlineData("含底层")]
    public async Task Encode_SaveAndOutputFailuresKeepOriginalExceptionTreeAndCanRetry(string shape)
    {
        const int limit = 1021;
        using CapturedFrame source = CreateFrame(out TrackingOwner owner, 1, 1);
        byte[] original = owner.Bytes.ToArray();
        var leaf = new IOException("Save 的独立原错误。");
        var nested = new AggregateException("Save 原有内层。", leaf);
        Exception? saveFailure = null;
        Exception? writeFailure = null;
        JpegOutputStream? failedStream = null;
        int saves = 0;
        var encoder = new WpfJpegFrameEncoder((bitmap, quality, output) =>
        {
            if (++saves != 1)
            {
                WpfJpegFrameEncoder.SaveJpeg(bitmap, quality, output);
                return;
            }

            failedStream = Assert.IsType<JpegOutputStream>(output);
            output.Position = limit;
            writeFailure = Assert.Throws<NotSupportedException>(() => output.WriteByte(0xA5));
            Assert.Same(writeFailure, failedStream.Failure);
            saveFailure = shape switch
            {
                "独立" => leaf,
                "嵌套" => new AggregateException("Save 原有外层。", nested),
                "含底层" => new AggregateException("Save 原有混合树。", nested, writeFailure),
                _ => throw new ArgumentOutOfRangeException(nameof(shape))
            };
            throw saveFailure;
        }, limit);

        Exception? error = await Record.ExceptionAsync(() => DisposeResultAsync(encoder.EncodeAsync(source, Settings, default)));
        var combined = Assert.IsType<AggregateException>(error);
        Assert.Equal(2, combined.InnerExceptions.Count);
        Assert.NotNull(saveFailure);
        Assert.NotNull(writeFailure);
        Assert.Same(saveFailure, combined.InnerExceptions[0]);
        Assert.Same(writeFailure, combined.InnerExceptions[1]);
        Assert.True(ExceptionTreeContains(combined, leaf));
        if (shape != "独立")
        {
            var originalTree = Assert.IsType<AggregateException>(saveFailure);
            Assert.Same(nested, originalTree.InnerExceptions[0]);
            Assert.Same(leaf, Assert.Single(nested.InnerExceptions));
            Assert.Equal(shape == "含底层" ? 2 : 1, originalTree.InnerExceptions.Count);
            if (shape == "含底层") Assert.Same(writeFailure, originalTree.InnerExceptions[1]);
        }
        Assert.NotNull(failedStream);
        Assert.False(failedStream.CanWrite);
        AssertBorrowed(owner, original);
        using EncodedFrame retry = await encoder.EncodeAsync(source, Settings, default);
        Assert.Equal(1UL, retry.FrameId);
        Assert.Equal(2, saves);
        Assert.Equal(3, DecodeBgr(retry).Length);
        AssertBorrowed(owner, original);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(FrameLimits.MaxPayloadBytes + 1)]
    public void Constructor_RejectsInvalidPayloadLimit(int limit)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WpfJpegFrameEncoder(WpfJpegFrameEncoder.SaveJpeg, limit));
        Assert.Equal("payloadLimit", error.ParamName);
    }

    [Fact]
    public void Constructor_RejectsNullSave()
    {
        Assert.Throws<ArgumentNullException>(() => new WpfJpegFrameEncoder(null!, SmallPayloadLimit));
    }

    private static bool ExceptionTreeContains(Exception root, Exception expected)
    {
        if (ReferenceEquals(root, expected)) return true;
        if (root is AggregateException aggregate)
            return aggregate.InnerExceptions.Any(inner => ExceptionTreeContains(inner, expected));
        return root.InnerException is { } innerError && ExceptionTreeContains(innerError, expected);
    }

    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static async Task AssertDirectWorkerAwaitAsync(WpfJpegFrameEncoder encoder,
        Task<EncodedFrame> operation, Task worker, ManualResetEventSlim release)
    {
        using var guardCancellation = new CancellationTokenSource();
        Task guard = Task.Delay(Guard, guardCancellation.Token);
        try
        {
            // 从公开 operation 观察 awaiter；原任务或代理都可被观察，身份合同单独断言。
            while (true)
            {
                Assert.False(release.IsSet, "Save 闸已开，无法证明阻塞期间的等待关系。");
                Assert.Same(worker, encoder.OriginalWorker);
                Assert.False(worker.IsCompleted, "原 worker 已退出，不能证明受闸控制的 await。");
                Assert.False(operation.IsCompleted, "公开 operation 已提前退出。");
                Task? awaitedTask = OperationAwaitedTask(encoder, operation);
                bool registered = false;
                if (awaitedTask is not null)
                {
                    // 不匹配必须立即失败，不能当成尚未就绪继续等待 Guard。
                    Assert.Same(worker, awaitedTask);
                    registered = Continuations(awaitedTask).Any(continuation =>
                        ReferenceEquals(continuation is Delegate action ? action.Target : continuation, operation));
                }
                // 防止检查期间原 worker 退出、被后续编码覆盖，或认错已完成的同名状态机。
                Assert.Same(worker, encoder.OriginalWorker);
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

    private static Task? OperationAwaitedTask(WpfJpegFrameEncoder encoder, Task operation)
    {
        Type stateMachineType = typeof(WpfJpegFrameEncoder).GetMethod("EncodeOperationAsync", Fields)?
            .GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
            ?? throw new XunitException("缺少 EncodeOperationAsync 的实际异步状态机元数据。");
        FieldInfo stateMachineField = RuntimeField(operation.GetType(), "StateMachine")
            ?? throw new XunitException("公开 operation 缺少 StateMachine 字段，不能证明等待关系。");
        object? stateMachine = stateMachineField.GetValue(operation);
        if (stateMachine is null) return null;
        Assert.IsType(stateMachineType, stateMachine);
        Assert.Same(encoder, RuntimeField(stateMachineType, "<>4__this")?.GetValue(stateMachine));
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

    private static CapturedFrame CreateFrame(out TrackingOwner owner, int width = 17, int height = 13,
        FramePixelFormat format = FramePixelFormat.Bgra32, int padding = 3, int alpha = -1)
    {
        int pixelBytes = format == FramePixelFormat.Bgra32 ? 4 : 3;
        int stride = width * pixelBytes + padding;
        owner = new TrackingOwner(stride * height);
        Array.Fill(owner.Bytes, (byte)0xE3);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = y * stride + x * pixelBytes;
                int horizontal = 120 * x / Math.Max(1, width - 1);
                int vertical = 100 * y / Math.Max(1, height - 1);
                owner.Bytes[offset] = (byte)(25 + horizontal);
                owner.Bytes[offset + 1] = (byte)(45 + vertical);
                owner.Bytes[offset + 2] = (byte)(205 - horizontal / 2 - vertical / 2);
                if (pixelBytes == 4)
                    owner.Bytes[offset + 3] = alpha < 0 ? (byte)((x * 37 + y * 19) % 256) : (byte)alpha;
            }
        }
        return new CapturedFrame(new DisplayId(7), width, height, stride, format, owner, 987_654_321);
    }

    private static byte[] ExpectedBgr(CapturedFrame source, int width, int height)
    {
        int pixelBytes = source.PixelFormat == FramePixelFormat.Bgra32 ? 4 : 3;
        byte[] expected = new byte[width * height * 3];
        ReadOnlySpan<byte> pixels = source.Pixels.Span;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int sourceOffset = (y * source.Height / height) * source.Stride + (x * source.Width / width) * pixelBytes;
                pixels.Slice(sourceOffset, 3).CopyTo(expected.AsSpan((y * width + x) * 3, 3));
            }
        }
        return expected;
    }

    private static byte[] DecodeBgr(EncodedFrame encoded)
    {
        byte[] payload = encoded.Payload.ToArray();
        Assert.Equal(encoded.PayloadLength, payload.Length);
        Assert.True(payload.Length >= 4);
        Assert.Equal((byte)0xFF, payload[0]);
        Assert.Equal((byte)0xD8, payload[1]);
        Assert.Equal((byte)0xFF, payload[^2]);
        Assert.Equal((byte)0xD9, payload[^1]);
        BitmapSource bitmap;
        using (var input = new MemoryStream(payload, writable: false))
        {
            BitmapDecoder decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Assert.IsType<JpegBitmapDecoder>(decoder);
            bitmap = Assert.Single(decoder.Frames);
            bitmap.Freeze();
        }
        // 必须在输入流关闭后取像素，验证 OnLoad 解码不借用 JPEG 流。
        Assert.Equal(encoded.Width, bitmap.PixelWidth);
        Assert.Equal(encoded.Height, bitmap.PixelHeight);
        var bgr = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0);
        byte[] pixels = new byte[encoded.Width * encoded.Height * 3];
        bgr.CopyPixels(pixels, encoded.Width * 3, 0);
        return pixels;
    }

    private static async Task DisposeResultAsync(ValueTask<EncodedFrame> operation)
    {
        // 负例意外交付时也归还输出，不能让失败测试泄漏池内存。
        using EncodedFrame unexpected = await operation;
    }

    private static void AssertBorrowed(TrackingOwner owner, byte[] original)
    {
        Assert.Equal(0, owner.DisposeCalls);
        Assert.Equal(original, owner.Bytes);
        Assert.Equal(original.Length, owner.Memory.Length);
    }

    private sealed class TrackingOwner(int length) : IMemoryOwner<byte>
    {
        private int _disposeCalls;
        private int _memoryReads;
        internal byte[] Bytes { get; } = new byte[length];
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        internal int MemoryReads => Volatile.Read(ref _memoryReads);

        public Memory<byte> Memory
        {
            get
            {
                Interlocked.Increment(ref _memoryReads);
                ObjectDisposedException.ThrowIf(DisposeCalls != 0, this);
                return Bytes;
            }
        }

        public void Dispose() => Interlocked.Increment(ref _disposeCalls);
    }
}
