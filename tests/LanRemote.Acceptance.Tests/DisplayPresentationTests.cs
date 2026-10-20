using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LanRemote.App;
using LanRemote.Core.Models;
using LanRemote.Transport;
using LanRemote.Transport.Auth;
using Xunit.Sdk;
using Bridge = LanRemote.Transport.AuthenticatedControlSession.ClientDisplayPresentation;
using PresentationFrame = LanRemote.Transport.AuthenticatedControlSession.PresentationFrame;

namespace LanRemote.Acceptance.Tests;

// 合成已认证 Control 状态与目录约定；不是 TLS、serverProof、live 或物理 scanout 验收。
// 附着 proof/严格 ACK、Control 请求收发、LRVF 读取、JPEG 解码及 Image.Source 均走生产链路。
public sealed class DisplayPresentationTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string SessionText = "00112233-4455-6677-8899-aabbccddeeff";
    private const string TokenHex = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
    private const string PinHex = "808182838485868788898A8B8C8D8E8F909192939495969798999A9B9C9D9E9F";
    private const string AttachAck = """{"type":"video_attach_ack","channel":"video","protocol":1,"sessionId":"00112233-4455-6677-8899-aabbccddeeff"}""";
    private const string Catalog = """{"type":"host_status","protocol":1,"seq":7,"timestampMs":123,"displays":[{"id":4,"name":"主屏","x":-1920,"y":0,"width":1920,"height":1080,"isPrimary":true},{"id":9,"name":"副屏","x":0,"y":0,"width":1280,"height":720,"isPrimary":false}]}""";

    [Fact]
    public async Task InitialFrame_UsesRealReaderBaselineDecodeAndFrozenImageSource()
    {
        await using StaDispatcherFixture sta = new();
        Fixture f = new(await sta.Ready.WaitAsync(Guard));
        try
        {
            await f.ReadyAsync();
            FrameProbe frame = f.FeedFrame(1, f.Red);
            await f.DecodeExitedAsync(frame);
            await f.SettleRendererAsync();
            await f.AssertImageAsync(f.Red, 1, null);
            Assert.Single(f.SourceChanges);
            Assert.Equal(3, frame.Owner!.MemoryReads); // reader、EncodedFrame 构造、真实 Payload.ToArray。
            Assert.Equal(new ReadFrame(1, f.Red.Width, f.Red.Height, f.Red.Bytes.Length), Assert.Single(f.FramesRead));
            Assert.Empty(f.Control.Ssl.Writes);
            Assert.False(f.Presenter!.Completion.IsCompleted); // ACK 后的视频输入并未伪造 EOF。
        }
        finally { await f.FinishAsync(); }
    }

    [Theory]
    [InlineData(20UL)]
    [InlineData(23UL)]
    public async Task AckBeforeVideo_DropsBelowFence_AndAcceptsFenceOrGreater(ulong acceptedId)
    {
        await using StaDispatcherFixture sta = new();
        Fixture f = new(await sta.Ready.WaitAsync(Guard));
        try
        {
            await f.ReadyAsync();
            await f.SwitchAsync(4, 20, f.Green);
            FrameProbe below = f.FeedFrame(19, f.Red);
            await f.DecodeExitedAsync(below);
            await f.SettleRendererAsync();
            await f.AssertBlankAsync();
            Assert.Null(f.Bridge.LastPresented);
            Assert.False(f.Presenter!.Completion.IsCompleted);

            FrameProbe accepted = f.FeedFrame(acceptedId, f.Green);
            await f.DecodeExitedAsync(accepted);
            await f.SettleRendererAsync();
            await f.AssertImageAsync(f.Green, acceptedId, 0);
            Assert.Single(f.SourceChanges);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact]
    public async Task VideoBeforeAck_KeepsOnlyLatestDecoded_AndAckAlonePresentsIt()
    {
        await using StaDispatcherFixture sta = new();
        Fixture f = new(await sta.Ready.WaitAsync(Guard));
        try
        {
            await f.ReadyAsync();
            Task<DisplayChangedFrame?> request = f.Request(9);
            DisplaySwitchRequestFrame wire = ParseRequest(await f.NextWriteAsync());
            FrameProbe older = f.FeedFrame(30, f.Red);
            await f.DecodeExitedAsync(older);
            await f.SettleRendererAsync();
            await f.AssertBlankAsync();
            f.AssertDecodedSize(f.Red);

            FrameProbe latest = f.FeedFrame(31, f.Blue);
            await f.DecodeExitedAsync(latest);
            await f.SettleRendererAsync();
            f.AssertDecodedSize(f.Blue);
            await f.AssertBlankAsync();
            Assert.Null(f.Bridge.LastPresented);
            Assert.False(request.IsCompleted);
            int reads = f.FramesRead.Count;

            await f.AcknowledgeAsync(request, wire, 30, f.Blue);
            await f.SettleRendererAsync();
            await f.AssertImageAsync(f.Blue, 31, wire.Sequence);
            Assert.Equal(reads, f.FramesRead.Count); // ACK 唤醒保留的 bitmap，不靠再来一帧。
            Assert.Single(f.SourceChanges);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact]
    public async Task EarlyAck_DoesNotPresentBeforeOriginalFlushExits()
    {
        await using StaDispatcherFixture sta = new();
        Fixture f = new(await sta.Ready.WaitAsync(Guard));
        Pause flush = f.NewPause();
        try
        {
            await f.ReadyAsync();
            f.FlushPause = flush;
            Task<DisplayChangedFrame?> request = f.Request(4);
            DisplaySwitchRequestFrame wire = ParseRequest(await f.NextWriteAsync());
            await flush.Reached.Task.WaitAsync(Guard);
            Task originalFlush = await f.FlushPublished.Task.WaitAsync(Guard);
            await f.FeedControlAndContinueAsync(Ack(wire, 40, f.Green));
            FrameProbe frame = f.FeedFrame(40, f.Green);
            await f.DecodeExitedAsync(frame);
            await f.SettleRendererAsync();
            await f.AssertBlankAsync();
            Assert.Null(f.Parent.LatestDisplayChange);
            Assert.Null(f.Bridge.LastPresented);
            Assert.False(request.IsCompleted);
            // 活动发送此时在 WhenAny 中监督截止/停止，并非已经进入收尾的直接 join。
            // 由原 Flush 受闸、请求未退出/未呈现及放行后的成功结果验证，不伪造直接 await 链。
            Assert.False(originalFlush.IsCompleted);

            flush.Open();
            DisplayChangedFrame receipt = Assert.IsType<DisplayChangedFrame>(await request.WaitAsync(Guard));
            Assert.True(originalFlush.IsCompletedSuccessfully);
            Assert.Same(receipt, f.Parent.LatestDisplayChange);
            await f.SettleRendererAsync();
            await f.AssertImageAsync(f.Green, 40, wire.Sequence);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact]
    public async Task ConsecutiveRequests_LatePayloadCopyCannotOverwriteOrEvictNewEpochFrame()
    {
        await using StaDispatcherFixture sta = new();
        Fixture f = new(await sta.Ready.WaitAsync(Guard));
        Pause copy = f.NewPause();
        try
        {
            await f.ReadyAsync();
            await f.SwitchAsync(4, 50, f.Green);
            FrameProbe old = f.FeedFrame(50, f.Green, copyPause: copy);
            await copy.Reached.Task.WaitAsync(Guard);
            Assert.Equal(3, old.Owner!.MemoryReads);
            Assert.False(f.Decoder.IsCompleted);

            DisplayChangedFrame next = await f.SwitchAsync(9, 60, f.Blue);
            Assert.Equal(1L, next.RequestSequence);
            FrameProbe current = f.FeedFrame(60, f.Blue);
            await f.WaitVideoContinuedAsync(current);
            lock (f.Gate)
            {
                PresentationFrame encoded = Assert.IsType<PresentationFrame>(Field<object?>(f.Fenced, "_encoded"));
                Assert.Equal(60UL, encoded.Frame.FrameId);
                Assert.Null(Field<object?>(f.Fenced, "_decoded"));
            }
            Assert.Equal(0, old.Owner.DisposeCalls);
            await f.AssertBlankAsync();

            copy.Open();
            await f.DecodeExitedAsync(old);
            await f.DecodeExitedAsync(current);
            await f.SettleRendererAsync();
            await f.AssertImageAsync(f.Blue, 60, 1);
            Assert.Single(f.SourceChanges); // 单 decoder 的旧复制先退出，新槽随后才解码；旧帧从未显示。
            Assert.Equal(1, old.Owner.DisposeCalls);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact]
    public async Task QueuedOldUiCallback_IsRejectedAfterNewRequest_ThenNewFramePresents()
    {
        await using StaDispatcherFixture sta = new();
        Fixture f = new(await sta.Ready.WaitAsync(Guard));
        try
        {
            await f.ReadyAsync();
            await f.SwitchAsync(4, 70, f.Green);
            UiPause ui = await f.HoldUiAsync();
            FrameProbe old = f.FeedFrame(70, f.Green);
            await f.DecodeExitedAsync(old);
            await f.SettleRendererAsync();
            DispatcherOperation operation = f.AssertQueuedRendererOperation();
            await AssertAwaitPathAsync(operation.Task, f.Renderer);

            await f.SwitchAsync(9, 80, f.Blue);
            Assert.Null(f.Bridge.LastPresented);
            ui.Open();
            await operation.Task.WaitAsync(Guard);
            await f.BarrierAsync();
            await f.AssertBlankAsync();
            Assert.Null(f.Bridge.LastPresented);

            FrameProbe current = f.FeedFrame(80, f.Blue);
            await f.DecodeExitedAsync(current);
            await f.SettleRendererAsync();
            await f.AssertImageAsync(f.Blue, 80, 1);
            Assert.Single(f.SourceChanges);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory]
    [InlineData("presenter")]
    [InlineData("parent")]
    [InlineData("child")]
    public async Task RevokeWhileUiQueued_RejectsSourceAndStopJoinsOriginalDispatcherTask(string owner)
    {
        await using StaDispatcherFixture sta = new();
        Fixture f = new(await sta.Ready.WaitAsync(Guard));
        try
        {
            await f.ReadyAsync();
            UiPause ui = await f.HoldUiAsync();
            FrameProbe frame = f.FeedFrame(90, f.Red);
            await f.DecodeExitedAsync(frame);
            await f.SettleRendererAsync();
            DispatcherOperation operation = f.AssertQueuedRendererOperation();
            await AssertAwaitPathAsync(operation.Task, f.Renderer);
            f.Revoke(owner);
            Assert.Null(f.Bridge.LastPresented);
            Task stop = f.StopPresenter();
            Task parentStop = f.Keep(f.Parent.CloseAndJoinAsync()); // 独立请求，不串行等 presenter。
            await AssertAwaitPathAsync(operation.Task, stop);
            Assert.False(stop.IsCompleted);
            Assert.False(operation.Task.IsCompleted);

            ui.Open();
            await ObserveAsync(stop);
            await ObserveAsync(parentStop);
            Assert.True(operation.Task.IsCompletedSuccessfully);
            Assert.True(f.Presenter!.Completion.IsCompleted);
            await f.AssertBlankAsync();
            Assert.Null(f.Bridge.LastPresented);
            Assert.True((await f.Keep(f.Presenter.GetDisposalEvidenceAsync()).WaitAsync(Guard)).OriginalOperationsFinished);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory]
    [InlineData("presenter")]
    [InlineData("parent")]
    [InlineData("child")]
    public async Task RevokeDuringRealDecode_RejectsLateBitmapAndStopJoinsOriginalDecoder(string owner)
    {
        await using StaDispatcherFixture sta = new();
        Fixture f = new(await sta.Ready.WaitAsync(Guard));
        Pause copy = f.NewPause();
        try
        {
            await f.ReadyAsync();
            FrameProbe frame = f.FeedFrame(100, f.Red, copyPause: copy);
            await copy.Reached.Task.WaitAsync(Guard);
            Task decoder = f.Decoder;
            f.Revoke(owner);
            Assert.Null(f.Bridge.LastPresented);
            Task stop = f.StopPresenter();
            Task parentStop = f.Keep(f.Parent.CloseAndJoinAsync());
            await AssertAwaitPathAsync(decoder, stop);
            Assert.False(stop.IsCompleted);
            Assert.Equal(0, frame.Owner!.DisposeCalls);

            copy.Open();
            await ObserveAsync(stop);
            await ObserveAsync(parentStop);
            Assert.True(decoder.IsCompleted);
            Assert.Equal(1, frame.Owner.DisposeCalls);
            await f.AssertBlankAsync();
            Assert.Null(f.Bridge.LastPresented);
            Assert.Empty(f.RenderOperations);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("9223372036854775808")]
    [InlineData("18446744073709551615")]
    public async Task UnsignedFrameIds_ArePreservedThroughActualAckLrvfAndPresentation(string text)
    {
        await using StaDispatcherFixture sta = new();
        Fixture f = new(await sta.Ready.WaitAsync(Guard));
        try
        {
            await f.ReadyAsync();
            ulong id = ulong.Parse(text, CultureInfo.InvariantCulture);
            DisplayChangedFrame receipt = await f.SwitchAsync(4, id, f.Green);
            Assert.Equal(id, receipt.FirstFrameId);
            FrameProbe frame = f.FeedFrame(id, f.Green);
            Assert.Equal(id, BinaryPrimitives.ReadUInt64BigEndian(frame.Packet.Data.AsSpan(8, 8)));
            await f.DecodeExitedAsync(frame);
            await f.SettleRendererAsync();
            await f.AssertImageAsync(f.Green, id, 0);
            Assert.Equal(id, Assert.Single(f.FramesRead).Id);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory]
    [InlineData(110UL)]
    [InlineData(109UL)]
    public async Task OptIn_DuplicateOrDescendingFrameIdFailsClosed(ulong invalidId)
    {
        await using StaDispatcherFixture sta = new();
        Fixture f = new(await sta.Ready.WaitAsync(Guard));
        try
        {
            await f.ReadyAsync();
            FrameProbe first = f.FeedFrame(110, f.Red);
            await f.DecodeExitedAsync(first);
            await f.SettleRendererAsync();
            await f.AssertImageAsync(f.Red, 110, null);
            FrameProbe invalid = f.FeedFrame(invalidId, f.Blue);
            await invalid.Disposed.Task.WaitAsync(Guard);
            // read 终检的错误先于 owner.Dispose；变异若只丢帧而不停机会在原 Assert 失败。
            Assert.Contains(f.Parent.LifetimeErrors,
                error => ContainsProtocol(error, "display-presentation-frame-id-not-increasing"));
            await ObserveAsync(f.Presenter!.Completion);
            Assert.True(f.Presenter.Completion.IsFaulted);
            Assert.Null(f.Bridge.LastPresented);
            await f.UiAsync(() => f.AssertPixels(f.Red));
            Assert.Single(f.SourceChanges);
            Assert.True(f.Control.Connection.IsCloseRequested);
            Assert.True(f.Video.Connection.IsCloseRequested);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory]
    [InlineData("header-size")]
    [InlineData("jpeg")]
    [InlineData("receipt-size")]
    public async Task InvalidDimensionsOrJpeg_StopWithoutAnyPresentation(string invalid)
    {
        await using StaDispatcherFixture sta = new();
        Fixture f = new(await sta.Ready.WaitAsync(Guard));
        try
        {
            await f.ReadyAsync();
            Task<DisplayChangedFrame?> request = f.Request(4);
            DisplaySwitchRequestFrame wire = ParseRequest(await f.NextWriteAsync());
            await f.AcknowledgeAsync(request, wire, 120, f.Green,
                width: invalid == "receipt-size" ? f.Green.Width + 1 : f.Green.Width);
            FrameProbe frame = f.FeedFrame(120, f.Green,
                headerWidth: invalid == "header-size" ? f.Green.Width + 1 : null,
                payload: invalid == "jpeg" ? new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 } : null);
            await frame.Disposed.Task.WaitAsync(Guard);
            Task failingWorker = invalid == "receipt-size" ? f.Renderer : f.Decoder;
            await f.SettleWorkerAsync(failingWorker);
            // 等到原 worker 退出或再次挂起后直接断言 fault，不以 Completion 超时证明拒绝。
            Assert.True(failingWorker.IsFaulted);
            if (invalid == "receipt-size")
                Assert.True(ContainsProtocol(failingWorker.Exception!, "display-presentation-receipt-size-mismatch"));
            else Assert.True(ContainsType<InvalidDataException>(failingWorker.Exception!));
            await ObserveAsync(f.Presenter!.Completion);
            await f.AssertBlankAsync();
            Assert.Null(f.Bridge.LastPresented);
            Assert.True(f.Control.Connection.IsCloseRequested);
            Assert.True(f.Video.Connection.IsCloseRequested);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact]
    public async Task BindingRejectsOtherParentWrongVideoDisabledAndPriorRequestOrReadWithoutSideEffects()
    {
        await using StaDispatcherFixture sta = new();
        Dispatcher dispatcher = await sta.Ready.WaitAsync(Guard);
        foreach (string invalid in new[] { "other-parent", "wrong-video", "disabled", "request", "read" })
        {
            Fixture f = new(dispatcher, enable: invalid != "disabled");
            Fixture other = new(dispatcher);
            try
            {
                await f.ReadyAsync(startPresenter: false);
                if (invalid == "wrong-video") await other.ReadyAsync(startPresenter: false);
                if (invalid == "request") await f.SwitchAsync(4, 130, f.Green);
                if (invalid == "read")
                {
                    Task<EncodedFrame?> read = f.Keep(f.PublicVideo.ReadFrameAsync());
                    f.FeedFrame(130, f.Red);
                    using EncodedFrame actual = Assert.IsType<EncodedFrame>(await read.WaitAsync(Guard));
                    Assert.Equal(130UL, actual.FrameId);
                }
                AuthenticatedControlSession control = invalid == "other-parent" ? other.Parent : f.Parent;
                AuthenticatedVideoSession video = invalid == "wrong-video" ? other.PublicVideo : f.PublicVideo;
                IoSnapshot before = f.Snapshot(), otherBefore = other.Snapshot();
                await f.UiAsync(() => Assert.Throws<InvalidOperationException>(() =>
                    { _ = new RemoteVideoPresenter(video, f.Image, control); }));
                Assert.Equal(before, f.Snapshot());
                Assert.Equal(otherBefore, other.Snapshot());
                Assert.Null(Field<object?>(f.Parent, "_displayPresentation"));
                Assert.Null(Field<object?>(other.Parent, "_displayPresentation"));
                Assert.False(f.Control.Connection.IsCloseRequested);
                Assert.False(f.Video.Connection.IsCloseRequested);
                await f.AssertBlankAsync();

                if (invalid is "other-parent" or "wrong-video")
                {
                    await f.StartPresenterAsync(); // 错误绑定没有消费正确 child 的 opt-in 资格。
                    FrameProbe frame = f.FeedFrame(131, f.Red);
                    await f.DecodeExitedAsync(frame);
                    await f.SettleRendererAsync();
                    await f.AssertImageAsync(f.Red, 131, null);
                }
            }
            finally
            {
                try { await f.FinishAsync(); }
                finally { await other.FinishAsync(); }
            }
        }
    }

    private static DisplaySwitchRequestFrame ParseRequest(SentWrite write)
    {
        uint length = BinaryPrimitives.ReadUInt32BigEndian(write.Copy);
        Assert.InRange(length, 1U, 4096U);
        Assert.Equal((int)length + 4, write.Copy.Length);
        Assert.True(DisplaySwitchRequestFrame.TryParse(write.Copy.AsSpan(4), new Guid(SessionText),
            out var request, out var rejection), rejection);
        using JsonDocument json = JsonDocument.Parse(write.Copy.AsMemory(4));
        Assert.Equal("request_display", json.RootElement.GetProperty("type").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("protocol").GetInt32());
        Assert.Equal(7, json.RootElement.EnumerateObject().Count());
        return Assert.IsType<DisplaySwitchRequestFrame>(request);
    }

    private static string Ack(DisplaySwitchRequestFrame request, ulong firstFrame, Jpeg image, int? width = null) =>
        Encoding.UTF8.GetString(new DisplayChangedFrame(request.SessionId, 8 + request.Sequence, 456,
            request.Sequence, request.CatalogSequence, request.DisplayId, firstFrame, width ?? image.Width, image.Height).Serialize());

    private static byte[] Framed(string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] result = new byte[payload.Length + 4];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)payload.Length);
        payload.CopyTo(result, 4);
        return result;
    }

    private static bool ContainsProtocol(Exception root, string reason) =>
        root is FrameProtocolException protocol && protocol.Reason == reason ||
        (root is AggregateException aggregate ? aggregate.InnerExceptions.Any(error => ContainsProtocol(error, reason))
            : root.InnerException is { } inner && ContainsProtocol(inner, reason));

    private static bool ContainsType<T>(Exception root) where T : Exception => root is T ||
        (root is AggregateException aggregate ? aggregate.InnerExceptions.Any(ContainsType<T>)
            : root.InnerException is { } inner && ContainsType<T>(inner));

    private static async Task ObserveAsync(Task original)
    {
        // watchdog 仅限制坏测试；不将其捕获为业务成功或主动停止的证明。
        using CancellationTokenSource watchdog = new();
        Task guard = Task.Delay(Guard, watchdog.Token);
        try { Assert.Same(original, await Task.WhenAny(original, guard).ConfigureAwait(false)); }
        finally { watchdog.Cancel(); }
        await original.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private static async Task AssertAwaitPathAsync(Task awaited, Task returned)
    {
        using CancellationTokenSource watchdog = new();
        Task guard = Task.Delay(Guard, watchdog.Token);
        try
        {
            while (!HasAwaitPath(awaited, returned, [], 24, allowWhenAny: false))
            {
                Assert.False(awaited.IsCompleted, "原受闸任务提前退出，不能证明 join。");
                Assert.False(returned.IsCompleted, "返回任务遗漏了原任务的 join。");
                if (guard.IsCompleted) throw new XunitException("watchdog：未找到原 Task await 链。");
                await Task.Yield();
            }
        }
        finally { watchdog.Cancel(); }
    }

    // 来自 ClientDisplaySwitchTests 的只读 await 观察。join 排除 WhenAny；仅观察 worker 空闲时允许它。
    private static bool HasAwaitPath(Task awaited, Task returned, HashSet<Task> visited, int remaining, bool allowWhenAny)
    {
        if (remaining == 0 || !visited.Add(awaited)) return false;
        foreach (object continuation in Continuations(awaited))
        {
            object? box = continuation is Delegate action ? action.Target : continuation;
            if (box is not Task && box is not null && RuntimeField(box.GetType(), "m_action")?.GetValue(box) is Delegate scheduled)
                box = scheduled.Target;
            if (box is not Task next) continue;
            object? machine = RuntimeField(next.GetType(), "StateMachine")?.GetValue(next);
            bool exactAwait = machine is not null && machine.GetType().GetFields(Fields)
                .Where(field => field.Name.StartsWith("<>u__", StringComparison.Ordinal))
                .Select(field => field.GetValue(machine)).OfType<object>()
                .Any(awaiter => awaiter.GetType().GetFields(Fields).Any(field =>
                {
                    object? value = field.GetValue(awaiter);
                    if (value is Task task) return ReferenceEquals(task, awaited);
                    if (field.FieldType != typeof(ValueTask<int>) && field.FieldType != typeof(ValueTask)) return false;
                    return ReferenceEquals(RuntimeField(field.FieldType, "_obj")?.GetValue(value), awaited);
                }));
            string name = next.GetType().FullName ?? "";
            bool unwrap = name.StartsWith("System.Threading.Tasks.UnwrapPromise`", StringComparison.Ordinal);
            bool all = name.StartsWith("System.Threading.Tasks.Task+WhenAllPromise", StringComparison.Ordinal);
            bool any = allowWhenAny && (name.Contains("WhenAnyPromise", StringComparison.Ordinal) ||
                name.Contains("CompleteOnInvokePromise", StringComparison.Ordinal));
            if (!exactAwait && !unwrap && !all && !any) continue;
            if (ReferenceEquals(next, returned) || HasAwaitPath(next, returned, visited, remaining - 1, allowWhenAny)) return true;
        }
        return false;
    }

    private static object[] Continuations(Task task)
    {
        FieldInfo field = RuntimeField(typeof(Task), "m_continuationObject")
            ?? throw new XunitException("运行时缺少 continuation 字段，不能跳过 await 证明。");
        object? value = field.GetValue(task);
        if (value is IList list)
        {
            lock (list) return list.Cast<object?>().OfType<object>().ToArray();
        }
        return value is null ? [] : [value];
    }

    private static FieldInfo? RuntimeField(Type type, string name)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
            if (current.GetField(name, Fields | BindingFlags.DeclaredOnly) is { } field) return field;
        return null;
    }

    private static T Field<T>(object instance, string name) => (T)(RuntimeField(instance.GetType(), name)
        ?? throw new XunitException($"缺少已核对字段 {instance.GetType().Name}.{name}。")).GetValue(instance)!;

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Pause
    {
        private readonly TaskCompletionSource _release = Signal();
        internal TaskCompletionSource Reached { get; } = Signal();
        internal void Open() => _release.TrySetResult();
        internal async Task WaitAsync()
        {
            Reached.TrySetResult();
            await _release.Task.ConfigureAwait(false);
        }
        internal void WaitSynchronously()
        {
            Reached.TrySetResult();
            _release.Task.GetAwaiter().GetResult();
        }
    }

    private sealed class UiPause : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        internal TaskCompletionSource Reached { get; } = Signal();
        internal void Wait()
        {
            Reached.TrySetResult();
            _release.Wait();
        }
        internal void Open() => _release.Set();
        public void Dispose() => _release.Dispose();
    }

    private sealed class Packet(byte[] data)
    {
        internal byte[] Data { get; } = data;
        internal TaskCompletionSource Continued { get; } = Signal();
        internal int Offset;
    }

    private sealed class PacketInput
    {
        private readonly Channel<Packet> _input = Channel.CreateUnbounded<Packet>();
        private Packet? _packet;
        internal void Feed(Packet packet) => Assert.True(_input.Writer.TryWrite(packet));
        internal void End() => _input.Writer.TryComplete();
        internal async Task<int> ReadAsync(Memory<byte> memory, CancellationToken token)
        {
            if (_packet is null || _packet.Offset == _packet.Data.Length)
            {
                _packet?.Continued.TrySetResult();
                if (!await _input.Reader.WaitToReadAsync(token).ConfigureAwait(false)) return 0;
                _packet = await _input.Reader.ReadAsync(token).ConfigureAwait(false);
            }
            Packet packet = _packet;
            int count = Math.Min(memory.Length, packet.Data.Length - packet.Offset);
            packet.Data.AsMemory(packet.Offset, count).CopyTo(memory);
            packet.Offset += count;
            return count;
        }
    }

    private sealed record SentWrite(byte[] Copy, ReadOnlyMemory<byte> Borrowed, Task Original);
    private sealed record ReadFrame(ulong Id, int Width, int Height, int PayloadLength);
    private sealed record IoSnapshot(int ControlReads, int VideoReads, int Writes, int Flushes,
        int TimestampReads, int UtcReads, int Timers, int Rents);
    private sealed record Jpeg(byte[] Bytes, int Width, int Height, byte R, byte G, byte B);

    private sealed class FrameProbe(Packet packet, Pause? copyPause)
    {
        internal Packet Packet { get; } = packet;
        internal Pause? CopyPause { get; } = copyPause;
        internal TaskCompletionSource Disposed { get; } = Signal();
        internal CountOwner? Owner;
    }

    private sealed class CountOwner(int length, FrameProbe probe) : IMemoryOwner<byte>
    {
        private readonly byte[] _bytes = new byte[length];
        private int _memoryReads, _disposeCalls;
        internal int MemoryReads => Volatile.Read(ref _memoryReads);
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        public Memory<byte> Memory
        {
            get
            {
                if (Interlocked.Increment(ref _memoryReads) == 3) probe.CopyPause?.WaitSynchronously();
                return _bytes;
            }
        }
        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCalls);
            probe.Disposed.TrySetResult(); // 只证明 owner 已被释放，不能当作显示信号。
        }
    }

    private sealed class ProbeClock : TimeProvider
    {
        private int _timestampReads, _utcReads, _timers;
        internal int TimestampReads => Volatile.Read(ref _timestampReads);
        internal int UtcReads => Volatile.Read(ref _utcReads);
        internal int Timers => Volatile.Read(ref _timers);
        internal DateTimeOffset UtcNow { get; } = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_123);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { Interlocked.Increment(ref _timestampReads); return 0; }
        public override DateTimeOffset GetUtcNow() { Interlocked.Increment(ref _utcReads); return UtcNow; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            Interlocked.Increment(ref _timers);
            return new ProbeTimer();
        }
    }

    private sealed class ProbeTimer : ITimer
    {
        private bool _disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
        public void Dispose() => _disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    // ClientDisplaySwitchTests.private Fixture 的最小本地副本；视频输入改为持久在线 Channel。
    private sealed class Fixture
    {
        private readonly ConcurrentBag<Task> _tasks = [];
        private readonly List<Pause> _pauses = [];
        private readonly List<UiPause> _uiPauses = [];
        private readonly PacketInput _controlInput = new(), _videoInput = new();
        private readonly Channel<SentWrite> _writes = Channel.CreateUnbounded<SentWrite>();
        private readonly ConcurrentQueue<FrameProbe> _rents = [];
        private readonly ConcurrentQueue<CountOwner> _owners = [];
        private readonly Dispatcher _dispatcher;
        private DependencyPropertyDescriptor? _sourceDescriptor;
        private EventHandler? _sourceChanged;
        internal Endpoint Control { get; } = new();
        internal Endpoint Video { get; } = new();
        internal ProbeClock Clock { get; } = new();
        internal AuthenticatedControlSession Parent { get; }
        internal AuthenticatedVideoSession PublicVideo { get; private set; } = null!;
        internal RemoteVideoPresenter? Presenter { get; private set; }
        internal Image Image { get; private set; } = null!;
        internal Jpeg Red { get; private set; } = null!;
        internal Jpeg Green { get; private set; } = null!;
        internal Jpeg Blue { get; private set; } = null!;
        internal ConcurrentQueue<BitmapSource> SourceChanges { get; } = [];
        internal ConcurrentQueue<DispatcherOperation> RenderOperations { get; } = [];
        internal ConcurrentQueue<ReadFrame> FramesRead { get; } = [];
        internal Pause? FlushPause;
        internal object Gate => Field<object>(Parent, "_gate");
        internal Bridge Bridge => Field<Bridge>(Parent, "_displayPresentation");
        internal object Fenced => Field<object>(Presenter!, "_fenced");
        internal Task Decoder => Field<Task>(Fenced, "_decoder");
        internal Task Renderer => Field<Task>(Fenced, "_renderer");
        internal Task MonitorTask => Assert.IsAssignableFrom<Task>(Parent.ControlMonitorCompletion);
        internal TaskCompletionSource<Task> FlushPublished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Fixture(Dispatcher dispatcher, bool enable = true)
        {
            _dispatcher = dispatcher;
            Guid session = new(SessionText);
            Assert.True(DisplayCatalogAgreementFrame.TryParseOffer(DisplayCatalogAgreementFrame.SerializeOffer(session),
                session, out var offer, out _));
            Assert.True(DisplayCatalogAgreementFrame.TryParseAccept(DisplayCatalogAgreementFrame.SerializeAccept(session),
                session, out var accept, out _));
            Parent = new AuthenticatedControlSession(Control.Connection, SessionPermission.Control, session, "ABCDEF",
                Convert.FromHexString(TokenHex), 15_000, 0, Clock, DisplayCatalogAgreementBinding.Create(session, offer!, accept!));
            Parent.CommitDelivery();
            if (enable) Parent.EnableDisplaySwitching();
            Control.Ssl.ReadBody = (memory, token) =>
            {
                Assert.False(Monitor.IsEntered(Gate));
                Assert.Equal(CancellationToken.None, token);
                return new ValueTask<int>(Keep(_controlInput.ReadAsync(memory, token)));
            };
            Control.Ssl.WriteBody = (memory, token) =>
            {
                Assert.False(Monitor.IsEntered(Gate));
                Assert.Equal(CancellationToken.None, token);
                Task original = Keep(Task.CompletedTask);
                _writes.Writer.TryWrite(new SentWrite(memory.ToArray(), memory, original));
                return new ValueTask(original);
            };
            Control.Ssl.FlushBody = token =>
            {
                Assert.Equal(CancellationToken.None, token);
                Task original = Keep(FlushPause?.WaitAsync() ?? Task.CompletedTask);
                FlushPublished.TrySetResult(original); // 交付原任务引用，不用早到的Entered推断任务已发布。
                return original;
            };
            Video.Ssl.ReadBody = (memory, token) => new ValueTask<int>(Keep(_videoInput.ReadAsync(memory, token)));
            Control.Ssl.OnClose = _controlInput.End;
            Video.Ssl.OnClose = _videoInput.End;
        }

        internal T Keep<T>(T task) where T : Task { _tasks.Add(task); return task; }
        internal Pause NewPause() { Pause pause = new(); _pauses.Add(pause); return pause; }
        internal Task UiAsync(Action body) => Keep(_dispatcher.InvokeAsync(body, DispatcherPriority.Background).Task);
        internal Task BarrierAsync() => UiAsync(() => { });

        internal async Task ReadyAsync(bool startPresenter = true)
        {
            await UiAsync(() =>
            {
                Image = new Image();
                Red = MakeJpeg(8, 6, 220, 30, 40);
                Green = MakeJpeg(10, 8, 25, 215, 45);
                Blue = MakeJpeg(12, 10, 30, 45, 220);
                _sourceDescriptor = DependencyPropertyDescriptor.FromProperty(System.Windows.Controls.Image.SourceProperty, typeof(Image));
                _sourceChanged = (_, _) =>
                {
                    if (Image.Source is BitmapSource bitmap) SourceChanges.Enqueue(bitmap);
                };
                _sourceDescriptor.AddValueChanged(Image, _sourceChanged);
            }).WaitAsync(Guard);
            _videoInput.Feed(new Packet(Framed(AttachAck)));
            Task<TlsConnection> Connect(ConnectionTarget target, TransportTimeouts timeouts, TimeProvider clock, CancellationToken token)
            {
                Assert.False(Monitor.IsEntered(Gate));
                Assert.Same(Clock, clock);
                Assert.Same(TransportTimeouts.Default, timeouts);
                Assert.Equal(Parent.Identity.DeviceId, target.DeviceId);
                return Keep(Task.FromResult(Video.Connection));
            }
            PublicVideo = await Keep(Parent.AttachPublicVideoForTestingAsync(Connect, rent: Rent,
                frameRead: frame =>
                {
                    // 只观察，绝不访问 Payload、阻塞或抛错来代替生产读取/签发。
                    if (frame is not null) FramesRead.Enqueue(new(frame.FrameId, frame.Width, frame.Height, frame.PayloadLength));
                })).WaitAsync(Guard);
            _ = Keep(MonitorTask);
            AssertAttachProof();
            Assert.Empty(_owners);
            await FeedControlAndContinueAsync(Catalog);
            Assert.NotNull(Parent.LatestDisplayCatalog);
            if (startPresenter) await StartPresenterAsync();
        }

        internal async Task StartPresenterAsync()
        {
            await UiAsync(() =>
            {
                _dispatcher.Hooks.OperationPosted += OnOperationPosted;
                Presenter = new RemoteVideoPresenter(PublicVideo, Image, Parent);
                Keep(Presenter.Completion);
            }).WaitAsync(Guard);
            Assert.NotNull(Bridge);
        }

        private void OnOperationPosted(object? sender, DispatcherHookEventArgs args)
        {
            if (args.Operation.Priority != DispatcherPriority.Render) return;
            RenderOperations.Enqueue(args.Operation);
            Keep(args.Operation.Task);
        }

        private void AssertAttachProof()
        {
            byte[] wire = Assert.Single(Video.Ssl.Writes);
            Assert.Equal(wire.Length - 4, (int)BinaryPrimitives.ReadUInt32BigEndian(wire));
            using JsonDocument json = JsonDocument.Parse(wire.AsMemory(4));
            JsonElement hello = json.RootElement;
            Assert.Equal("channel_hello", hello.GetProperty("type").GetString());
            Assert.Equal("video", hello.GetProperty("channel").GetString());
            Assert.Equal(1, hello.GetProperty("protocol").GetInt32());
            Assert.Equal(SessionText, hello.GetProperty("sessionId").GetString());
            byte[] nonce = hello.GetProperty("attachNonce").GetBytesFromBase64();
            Assert.Equal(16, nonce.Length);
            Assert.Equal(VideoAttachProof.ComputeProof(Convert.FromHexString(TokenHex), new Guid(SessionText), nonce,
                Convert.FromHexString(PinHex)), hello.GetProperty("attachProof").GetBytesFromBase64());
            Assert.All(Video.Ssl.BorrowedWrites, memory => Assert.True(memory.Span.IndexOfAnyExcept((byte)0) < 0));
        }

        private IMemoryOwner<byte> Rent(int length)
        {
            Assert.True(_rents.TryDequeue(out FrameProbe? frame));
            CountOwner owner = new(length, frame!);
            frame!.Owner = owner;
            _owners.Enqueue(owner);
            return owner;
        }

        internal FrameProbe FeedFrame(ulong id, Jpeg image, Pause? copyPause = null,
            int? headerWidth = null, byte[]? payload = null)
        {
            byte[] jpeg = payload ?? image.Bytes;
            byte[] wire = new byte[40 + jpeg.Length];
            "LRVF"u8.CopyTo(wire);
            wire[4] = 1;
            wire[5] = (byte)VideoCodec.Jpeg;
            BinaryPrimitives.WriteUInt64BigEndian(wire.AsSpan(8, 8), id);
            BinaryPrimitives.WriteUInt64BigEndian(wire.AsSpan(16, 8), 123);
            BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(24, 4), (uint)(headerWidth ?? image.Width));
            BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(28, 4), (uint)image.Height);
            wire[32] = 85;
            BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(36, 4), (uint)jpeg.Length);
            jpeg.CopyTo(wire, 40);
            FrameProbe frame = new(new Packet(wire), copyPause);
            _rents.Enqueue(frame);
            _videoInput.Feed(frame.Packet);
            return frame;
        }

        internal async Task DecodeExitedAsync(FrameProbe frame)
        {
            await Task.WhenAny(frame.Disposed.Task, Presenter!.Completion).WaitAsync(Guard);
            Assert.True(frame.Disposed.Task.IsCompleted, "原管线在释放目标帧之前退出。");
            Assert.Equal(1, frame.Owner!.DisposeCalls);
        }

        internal async Task WaitVideoContinuedAsync(FrameProbe frame)
        {
            await Task.WhenAny(frame.Packet.Continued.Task, Presenter!.Completion).WaitAsync(Guard);
            Assert.False(Presenter.Completion.IsCompleted);
            Assert.True(frame.Packet.Continued.Task.IsCompleted, "真实 reader 尚未接纳并继续下一帧读取。");
        }

        internal Task<DisplayChangedFrame?> Request(int id) => Keep(Parent.RequestDisplayAsync(new DisplayId(id)));
        internal async Task<SentWrite> NextWriteAsync() => await _writes.Reader.ReadAsync().AsTask().WaitAsync(Guard);

        internal async Task FeedControlAndContinueAsync(string json)
        {
            Packet packet = new(Framed(json));
            _controlInput.Feed(packet);
            await Task.WhenAny(packet.Continued.Task, MonitorTask).WaitAsync(Guard);
            Assert.False(MonitorTask.IsCompleted, "合法 Control 包不应关闭唯一 reader。");
            Assert.True(packet.Continued.Task.IsCompleted);
        }

        internal async Task<DisplayChangedFrame> AcknowledgeAsync(Task<DisplayChangedFrame?> request,
            DisplaySwitchRequestFrame wire, ulong firstFrame, Jpeg image, int? width = null)
        {
            await FeedControlAndContinueAsync(Ack(wire, firstFrame, image, width));
            DisplayChangedFrame receipt = Assert.IsType<DisplayChangedFrame>(await request.WaitAsync(Guard));
            Assert.Same(receipt, Parent.LatestDisplayChange);
            Assert.Equal(wire.Sequence, receipt.RequestSequence);
            Assert.Equal(wire.CatalogSequence, receipt.CatalogSequence);
            Assert.Equal(wire.DisplayId, receipt.DisplayId);
            Assert.Equal(firstFrame, receipt.FirstFrameId);
            return receipt;
        }

        internal async Task<DisplayChangedFrame> SwitchAsync(int display, ulong firstFrame, Jpeg image)
        {
            Task<DisplayChangedFrame?> task = Request(display);
            SentWrite sent = await NextWriteAsync();
            DisplaySwitchRequestFrame wire = ParseRequest(sent);
            Assert.Equal(new DisplayId(display), wire.DisplayId);
            Assert.Equal(7L, wire.CatalogSequence);
            Assert.Equal(Clock.UtcNow.ToUnixTimeMilliseconds(), wire.TimestampMs);
            await sent.Original.WaitAsync(Guard);
            return await AcknowledgeAsync(task, wire, firstFrame, image);
        }

        internal async Task<UiPause> HoldUiAsync()
        {
            UiPause pause = new();
            _uiPauses.Add(pause);
            _ = Keep(_dispatcher.InvokeAsync(pause.Wait, DispatcherPriority.Send).Task);
            await pause.Reached.Task.WaitAsync(Guard);
            return pause;
        }

        internal Task SettleRendererAsync() => SettleWorkerAsync(Renderer);

        internal DispatcherOperation AssertQueuedRendererOperation()
        {
            // 排除 WPF 自己的 Render 优先级工作，只认可原 renderer 实际 await 的操作。
            lock (Gate)
                return Assert.Single(RenderOperations, operation => !operation.Task.IsCompleted &&
                    HasAwaitPath(operation.Task, Renderer, [], 24, allowWhenAny: false));
        }

        internal async Task SettleWorkerAsync(Task worker)
        {
            using CancellationTokenSource watchdog = new();
            Task guard = Task.Delay(Guard, watchdog.Token);
            try
            {
                while (true)
                {
                    lock (Gate)
                    {
                        if (worker.IsCompleted) return;
                        Task changed = Field<TaskCompletionSource>(Fenced, "_changed").Task;
                        // 必须是最新槽信号的实际 await 链；上一轮已经完成的信号不算检查过新 candidate。
                        if (!changed.IsCompleted && HasAwaitPath(changed, worker, [], 24, allowWhenAny: true)) return;
                        if (ReferenceEquals(worker, Renderer) && RenderOperations.Any(operation => !operation.Task.IsCompleted &&
                            HasAwaitPath(operation.Task, worker, [], 24, allowWhenAny: false))) return;
                    }
                    if (guard.IsCompleted) throw new XunitException("watchdog：原 worker 未到达可观察检查点。");
                    await Task.Yield();
                }
            }
            finally { watchdog.Cancel(); }
        }

        internal void AssertDecodedSize(Jpeg image)
        {
            lock (Gate)
            {
                object decoded = Assert.IsAssignableFrom<object>(Field<object?>(Fenced, "_decoded"));
                BitmapSource bitmap = Field<BitmapSource>(decoded, "<Bitmap>k__BackingField");
                Assert.True(bitmap.IsFrozen);
                Assert.Equal(image.Width, bitmap.PixelWidth);
                Assert.Equal(image.Height, bitmap.PixelHeight);
                Assert.Null(Field<object?>(Fenced, "_encoded"));
            }
        }

        internal async Task AssertImageAsync(Jpeg expected, ulong id, long? requestSequence)
        {
            await UiAsync(() => AssertPixels(expected)).WaitAsync(Guard);
            AuthenticatedControlSession.PresentedFrame presented = Assert.IsType<AuthenticatedControlSession.PresentedFrame>(Bridge.LastPresented);
            Assert.Equal(id, presented.FrameId);
            Assert.Equal(expected.Width, presented.Width);
            Assert.Equal(expected.Height, presented.Height);
            Assert.Equal(requestSequence, presented.RequestSequence);
            Assert.False(Presenter!.Completion.IsCompleted);
        }

        internal void AssertPixels(Jpeg expected)
        {
            Assert.True(_dispatcher.CheckAccess());
            BitmapSource bitmap = Assert.IsAssignableFrom<BitmapSource>(Image.Source);
            Assert.True(bitmap.IsFrozen);
            Assert.Equal(expected.Width, bitmap.PixelWidth);
            Assert.Equal(expected.Height, bitmap.PixelHeight);
            FormatConvertedBitmap converted = new(bitmap, PixelFormats.Bgra32, null, 0);
            byte[] pixels = new byte[expected.Width * expected.Height * 4];
            converted.CopyPixels(pixels, expected.Width * 4, 0);
            for (int i = 0; i < pixels.Length; i += 4)
            {
                Assert.InRange((int)pixels[i], expected.B - 12, expected.B + 12);
                Assert.InRange((int)pixels[i + 1], expected.G - 12, expected.G + 12);
                Assert.InRange((int)pixels[i + 2], expected.R - 12, expected.R + 12);
                Assert.Equal((byte)255, pixels[i + 3]);
            }
        }

        internal async Task AssertBlankAsync()
        {
            await UiAsync(() => Assert.Null(Image.Source)).WaitAsync(Guard);
            Assert.Empty(SourceChanges);
        }

        private static Jpeg MakeJpeg(int width, int height, byte r, byte g, byte b)
        {
            byte[] pixels = new byte[width * height * 3];
            for (int i = 0; i < pixels.Length; i += 3)
            {
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
            }
            BitmapSource source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, width * 3);
            source.Freeze();
            JpegBitmapEncoder encoder = new() { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using MemoryStream stream = new();
            encoder.Save(stream);
            return new Jpeg(stream.ToArray(), width, height, r, g, b);
        }

        internal void Revoke(string owner)
        {
            if (owner == "presenter") StopPresenter();
            else if (owner == "parent") Keep(Parent.CloseAndJoinAsync());
            else Keep(PublicVideo.DisposeAsync().AsTask());
        }

        internal Task StopPresenter() => Keep(Presenter!.StopAndDisposeAsync());
        internal IoSnapshot Snapshot() => new(Control.Ssl.ReadSizes.Count, Video.Ssl.ReadSizes.Count,
            Control.Ssl.Writes.Count + Video.Ssl.Writes.Count, Control.Ssl.FlushCalls + Video.Ssl.FlushCalls,
            Clock.TimestampReads, Clock.UtcReads, Clock.Timers, _owners.Count);

        internal async Task FinishAsync()
        {
            // 无论断言在哪失败，先放所有闸，独立请求两条停止，再 join 原任务；纯信号不登记为主任务。
            foreach (Pause pause in _pauses) pause.Open();
            foreach (UiPause pause in _uiPauses) pause.Open();
            Task presenterStop = Presenter is null ? Task.CompletedTask : StopPresenter();
            Task parentStop = Keep(Parent.CloseAndJoinAsync());
            _controlInput.End();
            _videoInput.End();
            _writes.Writer.TryComplete();
            try
            {
                await ObserveAsync(Keep(Task.WhenAll(presenterStop, parentStop)));
                await ObserveAsync(Keep(Parent.DisposeAsync().AsTask()));
                if (Parent.ControlMonitorCompletion is { } monitor) await ObserveAsync(Keep(monitor));
                await ObserveAsync(Task.WhenAll(_tasks));
                if (Presenter is not null)
                {
                    PresenterDrainEvidence evidence = await Keep(Presenter.GetDisposalEvidenceAsync()).WaitAsync(Guard);
                    Assert.True(evidence.OriginalOperationsFinished);
                    Assert.True(evidence.AllOwnedRootsReleased);
                }
                ClientDisposalEvidence parentEvidence = await Keep(Parent.GetDisposalEvidenceAsync()).WaitAsync(Guard);
                Assert.True(parentEvidence.OriginalOperationsFinished);
                Assert.True(parentEvidence.AllOwnedRootsReleased);
                Assert.All(_owners, owner => Assert.Equal(1, owner.DisposeCalls));
            }
            finally
            {
                try
                {
                    await ObserveAsync(Keep(Task.WhenAll(Control.Connection.CloseAsync(), Video.Connection.CloseAsync())));
                    await ObserveAsync(Task.WhenAll(_tasks));
                    await UiAsync(() =>
                    {
                        _dispatcher.Hooks.OperationPosted -= OnOperationPosted;
                        if (_sourceDescriptor is not null && _sourceChanged is not null)
                            _sourceDescriptor.RemoveValueChanged(Image, _sourceChanged);
                    }).WaitAsync(Guard);
                }
                finally
                {
                    foreach (UiPause pause in _uiPauses) pause.Dispose();
                    try { Control.ReleaseResources(); }
                    finally { Video.ReleaseResources(); }
                }
            }
        }
    }

    private sealed class Endpoint
    {
        internal ProbeTcpClient Client { get; } = new();
        internal ProbeSslStream Ssl { get; } = new();
        internal TlsConnection Connection { get; }
        internal Endpoint()
        {
            Assert.True(ConnectionTarget.TryCreate(new Guid("11111111-2222-3333-4444-555555555555"),
                IPAddress.Parse("192.168.50.7"), 12345, PinHex, out ConnectionTarget? target));
            Assert.True(ConnectionIdentity.TryCreate(target!, Convert.FromHexString(PinHex), out ConnectionIdentity? identity));
            Connection = new TlsConnection(identity!, Client, Ssl);
        }
        internal void ReleaseResources()
        {
            try { Ssl.ReleaseResources(); }
            finally { Client.ReleaseResources(); }
        }
    }

    private sealed class ProbeTcpClient : TcpClient
    {
        internal void ReleaseResources() => base.Dispose(true);
    }

    private sealed class ProbeSslStream() : SslStream(new MemoryStream(), leaveInnerStreamOpen: false)
    {
        private int _flushCalls;
        internal Func<Memory<byte>, CancellationToken, ValueTask<int>> ReadBody = null!;
        internal Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>? WriteBody;
        internal Func<CancellationToken, Task>? FlushBody;
        internal Action? OnClose;
        internal ConcurrentQueue<int> ReadSizes { get; } = [];
        internal ConcurrentQueue<byte[]> Writes { get; } = [];
        internal ConcurrentQueue<ReadOnlyMemory<byte>> BorrowedWrites { get; } = [];
        internal int FlushCalls => Volatile.Read(ref _flushCalls);
        public override ValueTask<int> ReadAsync(Memory<byte> memory, CancellationToken cancellationToken = default)
        {
            ReadSizes.Enqueue(memory.Length);
            return ReadBody(memory, cancellationToken);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> memory, CancellationToken cancellationToken = default)
        {
            Writes.Enqueue(memory.ToArray());
            BorrowedWrites.Enqueue(memory);
            return WriteBody?.Invoke(memory, cancellationToken) ?? ValueTask.CompletedTask;
        }
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _flushCalls);
            return FlushBody?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }
        protected override void Dispose(bool disposing)
        {
            try { if (disposing) OnClose?.Invoke(); }
            finally { base.Dispose(disposing); }
        }
        internal void ReleaseResources() => base.Dispose(true);
    }
}
