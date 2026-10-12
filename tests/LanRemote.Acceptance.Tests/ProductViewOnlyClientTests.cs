using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LanRemote.App;
using LanRemote.Core;
using LanRemote.Core.Models;
using LanRemote.Transport;

namespace LanRemote.Acceptance.Tests;

public sealed class ProductViewOnlyClientTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);
    private static readonly BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly MethodInfo DecodeMethod = typeof(RemoteVideoPresenter).GetMethod(
        "DecodeJpeg", PrivateStatic, null, [typeof(EncodedFrame)], null)
        ?? throw new MissingMethodException(typeof(RemoteVideoPresenter).FullName, "DecodeJpeg");
    private static readonly MethodInfo PrecheckMethod = typeof(RemoteVideoPresenter).GetMethod(
        "ValidateBaselineJpeg", PrivateStatic, null,
        [typeof(byte[]), typeof(int), typeof(int)], null)
        ?? throw new MissingMethodException(typeof(RemoteVideoPresenter).FullName, "ValidateBaselineJpeg");
    private static readonly Func<EncodedFrame, BitmapSource> Decode =
        DecodeMethod.CreateDelegate<Func<EncodedFrame, BitmapSource>>();
    private static readonly JpegPrecheck Precheck = PrecheckMethod.CreateDelegate<JpegPrecheck>();

    // 白盒组件边界：只调用产品的静态解码/预检，不构造真实 TLS 会话，不证明网络认证或窗口集成。
    private delegate void JpegPrecheck(byte[] jpeg, int width, int height);

    [Fact(Timeout = 20_000)]
    public Task Valid_Jpeg_Decodes_On_STA_With_OnLoad_And_Freeze_Independent_Of_Owner() => OnStaAsync(() =>
    {
        byte[] jpeg = EncodeOnePixelJpeg();
        CountingMemoryOwner owner = new(jpeg);
        BitmapSource bitmap;
        using (EncodedFrame frame = Frame(owner))
        {
            bitmap = Decode(frame);
            Assert.Equal(1, bitmap.PixelWidth);
            Assert.Equal(1, bitmap.PixelHeight);
            Assert.True(bitmap.IsFrozen);
            Assert.Equal(0, owner.DisposeCount);
        }

        Assert.Equal(1, owner.DisposeCount);
        Assert.Equal(1, bitmap.PixelWidth);
        Assert.Equal(1, bitmap.PixelHeight);
        int stride = (bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 7) / 8;
        byte[] pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        Assert.NotEmpty(pixels);
        return true;
    });

    [Fact(Timeout = 20_000)]
    public Task Jpeg_Dimensions_Differing_From_Frame_Metadata_Are_Rejected_And_Owner_Returned_Once() => OnStaAsync(() =>
    {
        CountingMemoryOwner owner = new(EncodeOnePixelJpeg());
        using (EncodedFrame frame = Frame(owner, width: 2, height: 2))
        {
            Assert.Throws<InvalidDataException>(() => Decode(frame));
            Assert.Equal(0, owner.DisposeCount);
        }

        Assert.Equal(1, owner.DisposeCount);
        return true;
    });

    [Fact(Timeout = 20_000)]
    public async Task Oversized_Sof_Is_Stopped_By_Product_Byte_Precheck_Without_Decoding()
    {
        byte[] jpeg = await OnStaAsync(EncodeOnePixelJpeg);
        int dimensions = FindSofDimensions(jpeg);
        // 保持合法 1x1 JPEG 的其余字节不变，只把 SOF 的高/宽各改为 8193。
        jpeg[dimensions] = 0x20;
        jpeg[dimensions + 1] = 0x01;
        jpeg[dimensions + 2] = 0x20;
        jpeg[dimensions + 3] = 0x01;

        // 绝不对篡改后的大尺寸 JPEG 调用 WPF Decoder：只执行产品的字节级预检。
        Assert.Throws<InvalidDataException>(() => Precheck(jpeg, 1, 1));
    }

    [Fact(Timeout = 20_000)]
    public Task Missing_Sof_And_Damaged_Jpeg_Are_Rejected_By_Product_Byte_Precheck()
    {
        Assert.Throws<InvalidDataException>(() => Precheck([0xFF, 0xD8, 0xFF, 0xD9], 1, 1));
        Assert.Throws<InvalidDataException>(() => Precheck([0xFF, 0xD8, 0xFF, 0xDB, 0x00, 0x20, 0x00], 1, 1));
        Assert.Throws<InvalidDataException>(() => Precheck([0x00, 0xD8, 0xFF, 0xD9], 1, 1));
        return Task.CompletedTask;
    }

    [Fact(Timeout = 20_000)]
    public async Task DropOldest_Returns_Old_Owner_Immediately_And_Stop_Leaves_Consumer_Ownership_Alone()
    {
        CountingMemoryOwner firstOwner = new([1]);
        CountingMemoryOwner secondOwner = new([2]);
        CountingMemoryOwner thirdOwner = new([3]);
        EncodedFrame first = Frame(firstOwner, id: 1);
        EncodedFrame second = Frame(secondOwner, id: 2);
        EncodedFrame third = Frame(thirdOwner, id: 3);
        using BoundedFrameQueue<EncodedFrame> queue = new(1);

        Assert.True(queue.TryWrite(first, out Exception? firstError));
        Assert.Null(firstError);
        Assert.Equal(0, firstOwner.DisposeCount);
        Assert.True(queue.TryWrite(second, out Exception? secondError));
        Assert.Null(secondError);
        Assert.Equal(1, firstOwner.DisposeCount);
        Assert.Equal(0, secondOwner.DisposeCount);

        EncodedFrame held = await queue.ReadAsync().AsTask().WaitAsync(TestTimeout);
        try
        {
            Assert.Same(second, held);
            Assert.True(queue.TryWrite(third, out Exception? thirdError));
            Assert.Null(thirdError);
            Assert.Equal(0, secondOwner.DisposeCount);
            Assert.Equal(0, thirdOwner.DisposeCount);
            queue.Stop();
            Assert.Equal(1, thirdOwner.DisposeCount);
            Assert.Equal(0, secondOwner.DisposeCount);
        }
        finally
        {
            held.Dispose();
        }

        Assert.Equal(1, secondOwner.DisposeCount);
        queue.Stop();
        Assert.Equal(1, firstOwner.DisposeCount);
        Assert.Equal(1, thirdOwner.DisposeCount);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Presenter_ActiveClose_Retains_Original_Read_Fault_But_Certifies_Released_Roots(bool cancelled)
    {
        await using PresenterProbe probe = await PresenterProbe.CreateAsync(_ => new CountingMemoryOwner([1]));
        ManualVideoRead pending = await probe.Stream.NextReadAsync();
        Exception original = cancelled
            ? new OperationCanceledException("原 Video.Read 底层取消。")
            : new IOException("原 Video.Read 底层 IO。 ");
        CancellationTokenSource stopSource = GetField<CancellationTokenSource>(probe.Presenter, "_stop");
        TaskCompletionSource cancelledRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = stopSource.Token.Register(() => cancelledRequest.TrySetResult());
        Task stop = probe.Presenter.StopAndDisposeAsync();
        Task evidence = EvidenceTask(probe.Presenter);
        await cancelledRequest.Task.WaitAsync(TestTimeout);
        Assert.False(stop.IsCompleted);
        Assert.False(evidence.IsCompleted); // Close 请求并不代表原 Read 已返回。
        pending.Fail(original);

        AggregateException reported = await Assert.ThrowsAsync<AggregateException>(() => stop.WaitAsync(TestTimeout));
        AssertContainsException(reported, original);
        await evidence.WaitAsync(TestTimeout);
        AssertEvidence(evidence, released: true);
        Assert.Same(stop, probe.Presenter.StopAndDisposeAsync());
        Assert.Same(evidence, EvidenceTask(probe.Presenter));
        Assert.Same(reported, await Assert.ThrowsAsync<AggregateException>(() => stop.WaitAsync(TestTimeout)));
        Assert.Equal(1, probe.Stream.DisposeCount);
    }

    [Fact(Timeout = 30_000)]
    public async Task Presenter_Decoder_Returns_Delivered_Owner_Before_Positive_Evidence()
    {
        byte[] jpeg = await OnStaAsync(EncodeOnePixelJpeg);
        CountingMemoryOwner owner = new(new byte[jpeg.Length]);
        await using PresenterProbe probe = await PresenterProbe.CreateAsync(_ => owner);
        await probe.SendFrameAsync(1, jpeg);
        await owner.Disposed.WaitAsync(TestTimeout); // 真实 decoder 完成 JPEG 冻结并释放已交付帧。
        Assert.Equal(1, owner.DisposeCount);
        ManualVideoRead pending = await probe.Stream.NextReadAsync();
        IOException original = new("解码后的原 Video.Read 故障。");
        CancellationTokenSource stopSource = GetField<CancellationTokenSource>(probe.Presenter, "_stop");
        TaskCompletionSource cancelledRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = stopSource.Token.Register(() => cancelledRequest.TrySetResult());
        Task stop = probe.Presenter.StopAndDisposeAsync();
        Task evidence = EvidenceTask(probe.Presenter);
        await cancelledRequest.Task.WaitAsync(TestTimeout);
        Assert.False(evidence.IsCompleted);
        pending.Fail(original);
        AggregateException reported = await Assert.ThrowsAsync<AggregateException>(() => stop.WaitAsync(TestTimeout));
        AssertContainsException(reported, original);
        await evidence.WaitAsync(TestTimeout);
        AssertEvidence(evidence, released: true);
        Assert.Equal(1, owner.DisposeCount);
    }

    [Fact(Timeout = 30_000)]
    public async Task Presenter_Decoder_OwnerDisposeFailure_Is_Red_And_Remains_In_Original_Task()
    {
        byte[] jpeg = await OnStaAsync(EncodeOnePixelJpeg);
        IOException ownerFailure = new("decoder 持有帧的 owner.Dispose 失败。");
        CountingMemoryOwner owner = new(new byte[jpeg.Length]) { DisposeFailure = ownerFailure };
        using BlockingStep decode = new();
        await using PresenterProbe probe = await PresenterProbe.CreateAsync(_ => owner,
            frame => { if (frame is not null) owner.OnMemoryAccess = decode.Wait; });
        ManualVideoRead pending;
        try
        {
            await probe.SendFrameAsync(1, jpeg);
            await decode.Reached.WaitAsync(TestTimeout);
            pending = await probe.Stream.NextReadAsync();
        }
        finally
        {
            decode.Open();
        }
        await owner.Disposed.WaitAsync(TestTimeout);
        Task stop = probe.Presenter.StopAndDisposeAsync();
        Task evidence = EvidenceTask(probe.Presenter);
        Assert.False(evidence.IsCompleted);
        pending.Fail(new IOException("decoder 故障后在途读取结束。"));
        AggregateException reported = await Assert.ThrowsAsync<AggregateException>(() => stop.WaitAsync(TestTimeout));
        AssertContainsException(reported, ownerFailure);
        await evidence.WaitAsync(TestTimeout);
        AssertEvidence(evidence, released: false);
        Assert.Equal(1, owner.DisposeCount);
    }

    [Theory(Timeout = 40_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Presenter_DropOrStop_OwnerDisposeFailure_Remains_Red_After_Second_QueueStop(bool dropOldest)
    {
        byte[] jpeg = await OnStaAsync(EncodeOnePixelJpeg);
        IOException ownerFailure = new("丢弃或清空存量帧的 owner.Dispose 失败。");
        CountingMemoryOwner[] owners =
        [
            new(new byte[jpeg.Length]),
            new(new byte[jpeg.Length]) { DisposeFailure = ownerFailure },
            new(new byte[jpeg.Length]),
        ];
        using BlockingStep decode = new();
        int nextOwner = -1;
        await using PresenterProbe probe = await PresenterProbe.CreateAsync(
            _ => owners[Interlocked.Increment(ref nextOwner)],
            frame =>
            {
                if (frame?.FrameId == 1) owners[0].OnMemoryAccess = decode.Wait;
            });
        Task stop;
        Task evidence;
        try
        {
            await probe.SendFrameAsync(1, jpeg);
            await decode.Reached.WaitAsync(TestTimeout); // decoder 独占已交付的第一帧。
            await probe.SendFrameAsync(2, jpeg);
            ManualVideoRead thirdHeader = await probe.Stream.NextReadAsync();
            if (dropOldest)
            {
                thirdHeader.Succeed(FrameHeader(3, jpeg.Length));
                (await probe.Stream.NextReadAsync()).Succeed(jpeg);
                await owners[1].Disposed.WaitAsync(TestTimeout); // 第三帧 TryWrite 丢旧帧。
                stop = probe.Presenter.StopAndDisposeAsync();
            }
            else
            {
                stop = probe.Presenter.StopAndDisposeAsync();
                await owners[1].Disposed.WaitAsync(TestTimeout); // 首次 Queue.Stop 锁外清空。
                thirdHeader.Fail(new IOException("Stop 后在途读取结束。"));
            }
            evidence = EvidenceTask(probe.Presenter);
            Assert.False(evidence.IsCompleted); // 已交付第一帧仍在 decoder 手里。
        }
        finally
        {
            decode.Open();
        }

        AggregateException reported = await Assert.ThrowsAsync<AggregateException>(() => stop.WaitAsync(TestTimeout));
        AssertContainsException(reported, ownerFailure);
        await evidence.WaitAsync(TestTimeout);
        AssertEvidence(evidence, released: false);
        Assert.Same(reported, await Assert.ThrowsAsync<AggregateException>(() => stop.WaitAsync(TestTimeout)));
        Assert.Equal(1, owners[0].DisposeCount);
        Assert.Equal(1, owners[1].DisposeCount);
        Assert.Equal(dropOldest ? 1 : 0, owners[2].DisposeCount);
    }

    [Fact(Timeout = 30_000)]
    public async Task Presenter_Evidence_Waits_For_CancelAsync_Callback_And_Original_Read()
    {
        await using PresenterProbe probe = await PresenterProbe.CreateAsync(_ => new CountingMemoryOwner([1]));
        ManualVideoRead pending = await probe.Stream.NextReadAsync();
        using BlockingStep callback = new();
        CancellationTokenSource stopSource = GetField<CancellationTokenSource>(probe.Presenter, "_stop");
        using CancellationTokenRegistration registration = stopSource.Token.Register(callback.Wait);
        Task stop;
        Task evidence;
        IOException original = new("取消后原始 Read 故障仍报告。");
        try
        {
            stop = probe.Presenter.StopAndDisposeAsync();
            evidence = EvidenceTask(probe.Presenter);
            await callback.Reached.WaitAsync(TestTimeout);
            pending.Fail(original);
            Assert.False(evidence.IsCompleted); // 原 Read 与取消回调必须都结束。
        }
        finally
        {
            callback.Open();
        }
        AggregateException reported = await Assert.ThrowsAsync<AggregateException>(() => stop.WaitAsync(TestTimeout));
        AssertContainsException(reported, original);
        await evidence.WaitAsync(TestTimeout);
        AssertEvidence(evidence, released: true);
        Assert.Same(reported, await Assert.ThrowsAsync<AggregateException>(() => stop.WaitAsync(TestTimeout)));
    }

    [Fact(Timeout = 30_000)]
    public async Task Presenter_CancelAsync_CallbackFailure_Remains_In_Original_Task_And_Denies_Evidence()
    {
        await using PresenterProbe probe = await PresenterProbe.CreateAsync(_ => new CountingMemoryOwner([1]));
        ManualVideoRead pending = await probe.Stream.NextReadAsync();
        IOException callbackFailure = new("取消回调失败。");
        TaskCompletionSource callbackReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource stopSource = GetField<CancellationTokenSource>(probe.Presenter, "_stop");
        using CancellationTokenRegistration registration = stopSource.Token.Register(() =>
        {
            callbackReached.TrySetResult();
            throw callbackFailure;
        });
        Task stop = probe.Presenter.StopAndDisposeAsync();
        Task evidence = EvidenceTask(probe.Presenter);
        await callbackReached.Task.WaitAsync(TestTimeout);
        Assert.False(evidence.IsCompleted);
        pending.Fail(new IOException("原 Read 返回。"));
        AggregateException reported = await Assert.ThrowsAsync<AggregateException>(() => stop.WaitAsync(TestTimeout));
        AssertContainsException(reported, callbackFailure);
        await evidence.WaitAsync(TestTimeout);
        AssertEvidence(evidence, released: false);
        Assert.Same(reported, await Assert.ThrowsAsync<AggregateException>(() => stop.WaitAsync(TestTimeout)));
    }

    private static EncodedFrame Frame(CountingMemoryOwner owner, int width = 1, int height = 1, ulong id = 1) =>
        new(VideoCodec.Jpeg, width, height, id, 0, 80, owner);

    private static async Task<T> OnStaAsync<T>(Func<T> operation)
    {
        await using StaDispatcherFixture sta = new();
        Dispatcher dispatcher = await sta.Ready.WaitAsync(TestTimeout);
        return await dispatcher.InvokeAsync(operation).Task.WaitAsync(TestTimeout);
    }

    private static byte[] EncodeOnePixelJpeg()
    {
        Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
        RenderTargetBitmap target = new(1, 1, 96, 96, PixelFormats.Pbgra32);
        DrawingVisual visual = new();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.CornflowerBlue, null, new System.Windows.Rect(0, 0, 1, 1));
        }
        target.Render(visual);
        JpegBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using MemoryStream buffer = new();
        encoder.Save(buffer);
        byte[] jpeg = buffer.ToArray();
        FindSofDimensions(jpeg);
        return jpeg;
    }

    private static int FindSofDimensions(byte[] jpeg)
    {
        Assert.True(jpeg.Length >= 4 && jpeg[0] == 0xFF && jpeg[1] == 0xD8);
        int offset = 2;
        while (offset + 4 <= jpeg.Length)
        {
            Assert.Equal((byte)0xFF, jpeg[offset++]);
            while (offset < jpeg.Length && jpeg[offset] == 0xFF) offset++;
            Assert.True(offset < jpeg.Length);
            byte marker = jpeg[offset++];
            Assert.NotEqual((byte)0xDA, marker);
            Assert.True(offset + 2 <= jpeg.Length);
            int length = (jpeg[offset] << 8) | jpeg[offset + 1];
            Assert.True(length >= 2 && length <= jpeg.Length - offset);
            if (marker == 0xC0)
            {
                Assert.True(length >= 8);
                return offset + 3; // SOF0: 长度(2)、精度(1)、高度(2)、宽度(2)。
            }
            offset += length;
        }
        throw new InvalidDataException("测试 JPEG 编码器未生成 baseline SOF0。");
    }

    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private static T GetField<T>(object target, string name) =>
        (T)(target.GetType().GetField(name, PrivateInstance)?.GetValue(target)
            ?? throw new MissingFieldException(target.GetType().FullName, name));

    private static void SetField(object target, string name, object value) =>
        (target.GetType().GetField(name, PrivateInstance)
            ?? throw new MissingFieldException(target.GetType().FullName, name)).SetValue(target, value);

    private static Task EvidenceTask(RemoteVideoPresenter presenter) =>
        (Task)(typeof(RemoteVideoPresenter).GetMethod("GetDisposalEvidenceAsync", PrivateInstance)!
            .Invoke(presenter, null) ?? throw new InvalidOperationException("没有返回呈现器证书任务。"));

    private static void AssertEvidence(Task evidence, bool released)
    {
        Assert.True(evidence.IsCompletedSuccessfully);
        object report = evidence.GetType().GetProperty("Result")!.GetValue(evidence)!;
        Assert.True((bool)report.GetType().GetProperty("OriginalOperationsFinished", PrivateInstance)!.GetValue(report)!);
        Assert.Equal(released, (bool)report.GetType().GetProperty("AllOwnedRootsReleased", PrivateInstance)!.GetValue(report)!);
    }

    private static void AssertContainsException(Exception root, Exception expected)
    {
        Assert.Contains(ExceptionTree(root), error => ReferenceEquals(error, expected));
    }

    private static IEnumerable<Exception> ExceptionTree(Exception error)
    {
        yield return error;
        if (error is AggregateException aggregate)
        {
            foreach (Exception child in aggregate.InnerExceptions)
                foreach (Exception nested in ExceptionTree(child)) yield return nested;
        }
        else if (error.InnerException is { } inner)
            foreach (Exception nested in ExceptionTree(inner)) yield return nested;
    }

    private static byte[] FrameHeader(ulong id, int length)
    {
        byte[] header = new byte[40];
        "LRVF"u8.CopyTo(header);
        header[4] = 1;
        header[5] = (byte)VideoCodec.Jpeg;
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8, 8), id);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(28, 4), 1);
        header[32] = 80;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(36, 4), (uint)length);
        return header;
    }

    private sealed class PresenterProbe(StaDispatcherFixture sta, ManualVideoStream stream,
        AuthenticatedVideoSession video, RemoteVideoPresenter presenter) : IAsyncDisposable
    {
        internal ManualVideoStream Stream { get; } = stream;
        internal RemoteVideoPresenter Presenter { get; } = presenter;

        internal static async Task<PresenterProbe> CreateAsync(Func<int, IMemoryOwner<byte>> rent,
            Action<EncodedFrame?>? frameRead = null)
        {
            StaDispatcherFixture sta = new();
            try
            {
                Dispatcher dispatcher = await sta.Ready.WaitAsync(TestTimeout);
                Image image = await dispatcher.InvokeAsync(() => new Image()).Task.WaitAsync(TestTimeout);
                ManualVideoStream stream = new();
                // 白盒合成仅替换 Transport 的认证/网络外壳；VideoFrameReader 和 Presenter 原 worker 均真实运行。
                AuthenticatedControlSession parent =
                    (AuthenticatedControlSession)RuntimeHelpers.GetUninitializedObject(typeof(AuthenticatedControlSession));
                SetField(parent, "_gate", new object());
                SetField(parent, "_connection", RuntimeHelpers.GetUninitializedObject(typeof(TlsConnection)));
                Type lifetimeType = typeof(AuthenticatedVideoSession).Assembly.GetType(
                    "LanRemote.Transport.AuthenticatedControlSession+ClientVideoLifetime", throwOnError: true)!;
                object lifetime = Activator.CreateInstance(lifetimeType, BindingFlags.Instance | BindingFlags.NonPublic,
                    null, [parent, rent, frameRead, null, null], null)!;
                SetField(parent, "_videoLifetime", lifetime);
                SetField(lifetime, "_delivered", true);
                SetField(GetField<object>(lifetime, "_attach"), "Worker", Task.CompletedTask);
                SetField(GetField<object>(lifetime, "_deadline"), "_worker", Task.CompletedTask);
                Type readerType = typeof(AuthenticatedVideoSession).Assembly.GetType(
                    "LanRemote.Transport.VideoFrameReader", throwOnError: true)!;
                object reader = Activator.CreateInstance(readerType, BindingFlags.Instance | BindingFlags.NonPublic,
                    null, [stream, rent], null)!;
                SetField(lifetime, "_reader", reader);
                AuthenticatedVideoSession video = (AuthenticatedVideoSession)lifetimeType
                    .GetProperty("PublicSession", PrivateInstance)!.GetValue(lifetime)!;
                return new PresenterProbe(sta, stream, video, new RemoteVideoPresenter(video, image));
            }
            catch
            {
                await sta.DisposeAsync();
                throw;
            }
        }

        internal async Task SendFrameAsync(ulong id, byte[] jpeg)
        {
            (await Stream.NextReadAsync()).Succeed(FrameHeader(id, jpeg.Length));
            (await Stream.NextReadAsync()).Succeed(jpeg);
        }

        public async ValueTask DisposeAsync()
        {
            Task stopped = Presenter.StopAndDisposeAsync();
            Stream.FailOutstanding(new IOException("测试夹具收尾读取。"));
            await stopped.WaitAsync(TestTimeout).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await video.DisposeAsync().AsTask().WaitAsync(TestTimeout)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await sta.DisposeAsync();
        }
    }

    private sealed class ManualVideoStream : Stream
    {
        private readonly Channel<ManualVideoRead> _reads = Channel.CreateUnbounded<ManualVideoRead>();
        private readonly ConcurrentQueue<ManualVideoRead> _all = new();
        private int _disposeCount;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        internal async Task<ManualVideoRead> NextReadAsync() =>
            await _reads.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);
        internal void FailOutstanding(Exception error)
        {
            foreach (ManualVideoRead pending in _all) pending.Fail(error);
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ManualVideoRead pending = new(buffer);
            _all.Enqueue(pending);
            Assert.True(_reads.Writer.TryWrite(pending));
            return new ValueTask<int>(pending.Task);
        }
        protected override void Dispose(bool disposing)
        {
            Interlocked.Increment(ref _disposeCount);
            base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ManualVideoRead(Memory<byte> buffer)
    {
        private readonly TaskCompletionSource<int> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<int> Task => _result.Task;
        internal void Succeed(byte[] bytes)
        {
            Assert.True(bytes.Length <= buffer.Length);
            bytes.AsSpan().CopyTo(buffer.Span);
            Assert.True(_result.TrySetResult(bytes.Length));
        }
        internal void Fail(Exception error) => _result.TrySetException(error);
    }

    private sealed class BlockingStep : IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Reached => _reached.Task;
        internal void Wait()
        {
            _reached.TrySetResult();
            if (!_release.Wait(TestTimeout)) throw new TimeoutException("受控回调未收到放行通知。");
        }
        internal void Open() => _release.Set();
        public void Dispose() => _release.Dispose();
    }

    private sealed class CountingMemoryOwner(byte[] bytes) : IMemoryOwner<byte>
    {
        private byte[]? _bytes = bytes.ToArray();
        private int _disposeCount;
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? DisposeFailure { get; init; }
        public Action? OnMemoryAccess { get; set; }
        public Task Disposed => _disposed.Task;
        public Memory<byte> Memory
        {
            get
            {
                OnMemoryAccess?.Invoke();
                return Volatile.Read(ref _bytes) ?? throw new ObjectDisposedException(nameof(CountingMemoryOwner));
            }
        }
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            Interlocked.Exchange(ref _bytes, null);
            _disposed.TrySetResult();
            if (DisposeFailure is { } error) throw error;
        }
    }
}
