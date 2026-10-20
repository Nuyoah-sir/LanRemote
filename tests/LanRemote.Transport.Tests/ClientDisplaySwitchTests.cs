using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LanRemote.Core.Models;
using Xunit.Sdk;
using Child = LanRemote.Transport.AuthenticatedControlSession.ClientVideoLifetime;

namespace LanRemote.Transport.Tests;

public sealed class ClientDisplaySwitchTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string SessionText = "00112233-4455-6677-8899-aabbccddeeff";
    private const string TokenHex = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
    private const string PinHex = "808182838485868788898A8B8C8D8E8F909192939495969798999A9B9C9D9E9F";
    private const string AttachAck = """{"type":"video_attach_ack","channel":"video","protocol":1,"sessionId":"00112233-4455-6677-8899-aabbccddeeff"}""";
    private const string Catalog = """{"type":"host_status","protocol":1,"seq":7,"timestampMs":123,"displays":[{"id":4,"name":"主屏","x":-1920,"y":0,"width":1920,"height":1080,"isPrimary":true},{"id":9,"name":"副屏","x":0,"y":0,"width":1280,"height":720,"isPrimary":false}]}""";

    [Fact]
    public async Task Enable_RequiresDelivery_IsUnique_AndPrecedesAttachWithoutIoOrClock()
    {
        Fixture f = new(delivered: false, enable: false);
        try
        {
            Assert.Null(f.Parent.LatestDisplayChange);
            Assert.Throws<InvalidOperationException>(() => f.Parent.EnableDisplaySwitching());
            f.AssertNoControlWrite();
            Assert.Empty(f.Control.Ssl.ReadSizes);
            Assert.Equal(0, f.Clock.TimestampReads);
            f.Parent.CommitDelivery();
            f.Parent.EnableDisplaySwitching();
            Assert.Throws<InvalidOperationException>(() => f.Parent.EnableDisplaySwitching());
            Assert.Equal(0, f.Clock.TimestampReads);
            Assert.Equal(0, f.Clock.UtcReads);
            Assert.Empty(f.Clock.Timers);
            Assert.Empty(f.Control.Ssl.ReadSizes);
            f.AssertNoControlWrite();
            PropertyInfo latest = typeof(AuthenticatedControlSession).GetProperty("LatestDisplayChange", Fields)!;
            Assert.Null(latest.SetMethod);
            Assert.True(latest.GetMethod!.IsAssembly);
            Assert.True(typeof(AuthenticatedControlSession).GetMethod("EnableDisplaySwitching", Fields)!.IsAssembly);
            MethodInfo request = typeof(AuthenticatedControlSession).GetMethod("RequestDisplayAsync", Fields)!;
            Assert.True(request.IsAssembly);
            Assert.Equal(typeof(Task<DisplayChangedFrame>), request.ReturnType);
            Assert.Equal(typeof(CancellationToken), request.GetParameters()[1].ParameterType);
            Assert.True(request.GetParameters()[1].IsOptional);
        }
        finally { await f.FinishAsync(); }

        Fixture late = new(enable: false);
        try
        {
            await late.ReadyAsync();
            int timers = late.Clock.Timers.Count, reads = late.Control.Ssl.ReadSizes.Count;
            Assert.Throws<InvalidOperationException>(() => late.Parent.EnableDisplaySwitching());
            Assert.Equal(timers, late.Clock.Timers.Count);
            Assert.Equal(reads, late.Control.Ssl.ReadSizes.Count);
            late.AssertNoControlWrite();
            late.Parent.RevokeForOwnerCleanup();
            Assert.Throws<ObjectDisposedException>(() => late.Parent.EnableDisplaySwitching());
            late.AssertNoControlWrite();
        }
        finally { await late.FinishAsync(); }
    }

    [Fact]
    public async Task Request_PreconditionsRejectWithoutWriteSamplingOrSequenceConsumption()
    {
        Fixture f = new(enable: false);
        using CancellationTokenSource caller = new();
        try
        {
            Assert.Throws<InvalidOperationException>(() => { _ = f.Parent.RequestDisplayAsync(new DisplayId(4)); });
            f.Parent.EnableDisplaySwitching();
            Assert.Throws<InvalidOperationException>(() => { _ = f.Parent.RequestDisplayAsync(new DisplayId(4)); });
            Assert.Equal(0, f.Clock.TimestampReads);
            Assert.Equal(0, f.Clock.UtcReads);
            await f.StartAsync();
            int timers = f.Clock.Timers.Count, samples = f.Clock.TimestampReads;
            Assert.Throws<InvalidOperationException>(() => { _ = f.Parent.RequestDisplayAsync(new DisplayId(4)); });
            Assert.Equal(samples, f.Clock.TimestampReads);
            Assert.Equal(timers, f.Clock.Timers.Count);
            await f.FeedAndContinueAsync(Catalog);
            caller.Cancel();
            samples = f.Clock.TimestampReads;
            timers = f.Clock.Timers.Count;
            Assert.Throws<ArgumentOutOfRangeException>(() => { _ = f.Parent.RequestDisplayAsync(new DisplayId(8)); });
            OperationCanceledException error = Assert.Throws<OperationCanceledException>(() =>
                { _ = f.Parent.RequestDisplayAsync(new DisplayId(4), caller.Token); });
            Assert.Equal(caller.Token, error.CancellationToken);
            Assert.Equal(samples, f.Clock.TimestampReads);
            Assert.Equal(timers, f.Clock.Timers.Count);
            Assert.Equal(0, f.Clock.UtcReads);
            Assert.Equal(0L, f.NextSequence);
            Assert.Null(f.Pending);
            f.AssertNoControlWrite();
        }
        finally { await f.FinishAsync(); }
    }

    [Fact]
    public async Task DisabledMode_KeepsDescendingHostStatus_AndRejectsDisplayChangedWithoutSending()
    {
        Fixture f = new(enable: false);
        try
        {
            await f.ReadyAsync();
            DisplayCatalogFrame first = Assert.IsType<DisplayCatalogFrame>(f.Parent.LatestDisplayCatalog);
            await f.FeedAndContinueAsync(Catalog.Replace("\"seq\":7", "\"seq\":1"));
            Assert.Equal(1L, f.Parent.LatestDisplayCatalog!.Sequence);
            Assert.NotSame(first, f.Parent.LatestDisplayCatalog);
            Assert.Throws<InvalidOperationException>(() => { _ = f.Parent.RequestDisplayAsync(new DisplayId(4)); });
            f.AssertNoControlWrite();
            f.Feed(UnsolicitedAck());
            await f.AssertRejectedAsync("control-unexpected-data");
            f.AssertNoControlWrite();
        }
        finally { await f.FinishAsync(); }
    }

    [Fact]
    public async Task RealRequest_StartsAtZero_FreezesActualCatalog_AndBusyDoesNotConsumeNextSequence()
    {
        Fixture f = new();
        try
        {
            await f.ReadyAsync();
            Task reader = f.MonitorTask;
            DisplayCatalogFrame catalog = Assert.IsType<DisplayCatalogFrame>(f.Parent.LatestDisplayCatalog);
            Task<DisplayChangedFrame?> first = f.Request(9);
            SentWrite write = await f.NextWriteAsync();
            DisplaySwitchRequestFrame request = ParseRequest(write);
            Assert.Equal(0L, request.Sequence);
            Assert.Equal(new Guid(SessionText), request.SessionId);
            Assert.Equal(7L, request.CatalogSequence);
            Assert.Equal(9, request.DisplayId.Value);
            Assert.Equal(f.Clock.UtcNow.ToUnixTimeMilliseconds(), request.TimestampMs);
            Assert.Same(catalog, Field<DisplayCatalogFrame>(f.Pending!, "<Catalog>k__BackingField"));
            Assert.Same(catalog, Field<DisplayCatalogFrame>(f.SwitchState, "_frozenCatalog"));
            Assert.Equal(4096, Field<int>(f.SendAttempt, "_maxPayloadBytes"));
            Assert.Equal(TimeSpan.FromSeconds(10), Field<TimeSpan>(f.SendAttempt, "_budget"));
            await f.SentTask.WaitAsync(Guard);
            Assert.Null(f.Parent.LatestDisplayChange);
            Assert.Throws<InvalidOperationException>(() => { _ = f.Parent.RequestDisplayAsync(new DisplayId(4)); });
            Assert.Equal(1L, f.NextSequence);
            Assert.Single(f.Control.Ssl.Writes);
            await f.FeedAndContinueAsync(Ack(request));
            DisplayChangedFrame receipt = Assert.IsType<DisplayChangedFrame>(await first.WaitAsync(Guard));
            Assert.Same(receipt, f.Parent.LatestDisplayChange);
            Assert.Equal(0L, receipt.RequestSequence);
            Assert.Equal(8L, receipt.Sequence);
            Assert.Equal(9, receipt.DisplayId.Value);
            Assert.Null(f.Pending);
            Assert.Same(reader, f.MonitorTask);
            Assert.Equal(1, f.MaxConcurrentReads);

            Task<DisplayChangedFrame?> second = f.Request(4);
            Assert.Null(f.Parent.LatestDisplayChange);
            DisplaySwitchRequestFrame next = ParseRequest(await f.NextWriteAsync());
            Assert.Equal(1L, next.Sequence);
            await f.FeedAndContinueAsync(Ack(next, hostSequence: 9));
            Assert.Same(await second.WaitAsync(Guard), f.Parent.LatestDisplayChange);
            Assert.Equal(0L, receipt.RequestSequence);
            Assert.Equal(2, f.Control.Ssl.FlushCalls);
            Assert.All(f.Control.Ssl.ReadTokens, token => Assert.Equal(CancellationToken.None, token));
            Assert.All(f.Control.Ssl.WriteTokens, token => Assert.Equal(CancellationToken.None, token));
            Assert.All(f.Control.Ssl.FlushTokens, token => Assert.Equal(CancellationToken.None, token));
            f.AssertBorrowedMemoryZero();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory]
    [InlineData("write")]
    [InlineData("flush")]
    public async Task EarlyAck_IsHeldUntilOriginalWriteAndFlushFinish_WithoutBlockingReader(string stage)
    {
        Fixture f = new();
        Pause hold = f.NewPause();
        try
        {
            await f.ReadyAsync();
            f.HoldSend(stage, hold);
            Task<DisplayChangedFrame?> worker = f.Request();
            SentWrite write = await f.NextWriteAsync();
            await hold.Reached.Task.WaitAsync(Guard);
            Task sent = f.SentTask;
            await f.FeedAndContinueAsync(Ack(ParseRequest(write)));
            Assert.True(f.ReceiptReceived);
            Assert.Null(f.Parent.LatestDisplayChange);
            Assert.False(worker.IsCompleted);
            Assert.False(sent.IsCompleted);
            Assert.False(f.SendAttempt.Snapshot.AllOwnedRootsReleased);
            Assert.Equal(1, f.MaxConcurrentReads);
            Assert.False(f.MonitorTask.IsCompleted);
            if (stage == "write")
            {
                Assert.Equal(write.Copy, write.Borrowed.ToArray());
                Assert.Equal(0, f.Control.Ssl.FlushCalls);
            }
            else AssertZero(write.Borrowed);
            hold.Open();
            await sent.WaitAsync(Guard);
            DisplayChangedFrame receipt = Assert.IsType<DisplayChangedFrame>(await worker.WaitAsync(Guard));
            Assert.Same(receipt, f.Parent.LatestDisplayChange);
            Assert.True(f.SendAttempt.Snapshot.AllOwnedRootsReleased);
            f.AssertBorrowedMemoryZero();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory]
    [InlineData("write")]
    [InlineData("flush")]
    public async Task EarlyAck_OriginalReaderStillObservesEof_AndParentEvidenceWaitsForSend(string stage)
    {
        Fixture f = new();
        Pause hold = f.NewPause();
        try
        {
            await f.ReadyAsync();
            f.HoldSend(stage, hold);
            Task<DisplayChangedFrame?> worker = f.Request();
            SentWrite write = await f.NextWriteAsync();
            await hold.Reached.Task.WaitAsync(Guard);
            Task original = stage == "write" ? write.Original : f.LastFlush;
            await f.FeedAndContinueAsync(Ack(ParseRequest(write)));
            Assert.True(f.ReceiptReceived);
            f.EndInput();
            await f.MonitorTask.WaitAsync(Guard);
            Assert.Equal(AuthenticatedControlSession.ControlMonitorEnd.EndOfStream, f.Parent.ControlMonitorState!.End);
            f.AssertRevoked();
            Task join = f.Keep(f.Parent.CloseAndJoinAsync());
            Task<ClientDisposalEvidence> evidence = f.Keep(f.Parent.GetDisposalEvidenceAsync());
            await AssertAwaitPathAsync(original, worker);
            await AssertAwaitPathAsync(original, join);
            Assert.False(evidence.IsCompleted);
            Assert.False(f.SwitchRootsReleased);
            Assert.False(f.SendAttempt.Snapshot.AllOwnedRootsReleased);
            hold.Open();
            await original.WaitAsync(Guard);
            Assert.Null(await worker.WaitAsync(Guard));
            await join.WaitAsync(Guard);
            Assert.True((await evidence.WaitAsync(Guard)).AllOwnedRootsReleased);
            Assert.Null(f.Parent.LatestDisplayChange);
            f.AssertBorrowedMemoryZero();
        }
        finally { await f.FinishAsync(); }
    }

    [Fact]
    public async Task EarlyAck_WriteFaultNeverPublishes_AndPreservesOriginalFailure()
    {
        Fixture f = new();
        Pause hold = f.NewPause();
        IOException failure = new("受控原写失败");
        try
        {
            await f.ReadyAsync();
            f.WritePause = hold;
            f.WriteFailure = failure;
            Task<DisplayChangedFrame?> worker = f.Request();
            SentWrite write = await f.NextWriteAsync();
            await hold.Reached.Task.WaitAsync(Guard);
            await f.FeedAndContinueAsync(Ack(ParseRequest(write)));
            Assert.True(f.ReceiptReceived);
            Assert.Null(f.Parent.LatestDisplayChange);
            hold.Open();
            Assert.Null(await worker.WaitAsync(Guard));
            await ObserveAsync(write.Original);
            f.AssertRevoked();
            f.EndInput();
            await f.Parent.CloseAndJoinAsync().WaitAsync(Guard);
            Assert.Same(failure, f.SendAttempt.Snapshot.OperationError);
            Assert.Contains(f.Parent.LifetimeErrors, root => ContainsReference(root, failure));
            Assert.Equal(0, f.Control.Ssl.FlushCalls);
            f.AssertBorrowedMemoryZero();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory]
    [InlineData("session")]
    [InlineData("request")]
    [InlineData("catalog")]
    [InlineData("id")]
    [InlineData("host-sequence")]
    [InlineData("json")]
    [InlineData("4097")]
    [InlineData("width")]
    [InlineData("height")]
    public async Task InvalidAck_FailsClosedAgainstActualSentRequest(string invalid)
    {
        Fixture f = new();
        try
        {
            await f.ReadyAsync();
            Task<DisplayChangedFrame?> worker = f.Request();
            DisplaySwitchRequestFrame request = ParseRequest(await f.NextWriteAsync());
            await f.SentTask.WaitAsync(Guard);
            string json = Ack(request);
            json = invalid switch
            {
                "session" => json.Replace(SessionText, "11112233-4455-6677-8899-aabbccddeeff"),
                "request" => json.Replace("\"requestSequence\":0", "\"requestSequence\":1"),
                "catalog" => json.Replace("\"catalogSequence\":7", "\"catalogSequence\":6"),
                "id" => json.Replace("\"displayId\":4", "\"displayId\":9"),
                "host-sequence" => json.Replace("\"seq\":8", "\"seq\":7"),
                "json" => json.Replace("\"width\":1920", "\"width\":1920,\"width\":1920"),
                "4097" => json + new string(' ', 4097 - Encoding.UTF8.GetByteCount(json)),
                "width" => json.Replace("\"width\":1920", "\"width\":0"),
                "height" => json.Replace("\"height\":1080", $"\"height\":{FrameLimits.MaxDimension + 1}"),
                _ => throw new XunitException("未知测试输入"),
            };
            f.Feed(json);
            await f.AssertRejectedAsync(invalid is "request" or "catalog" or "id" or "host-sequence"
                ? "display-switch-receipt-invalid" : "control-unexpected-data");
            Assert.Null(await worker.WaitAsync(Guard));
            Assert.Single(f.Control.Ssl.Writes);
            Assert.Equal(1, f.MaxConcurrentReads);
            f.AssertBorrowedMemoryZero();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory]
    [InlineData("unsolicited")]
    [InlineData("duplicate")]
    [InlineData("previous-host-sequence")]
    public async Task AckWithoutUniqueCurrentSlotOrIncreasingHostSequence_FailsClosed(string kind)
    {
        Fixture f = new();
        Pause hold = f.NewPause();
        try
        {
            await f.ReadyAsync();
            if (kind == "unsolicited")
            {
                Assert.Null(f.Pending);
                f.Feed(UnsolicitedAck());
                f.AssertNoControlWrite();
            }
            else if (kind == "duplicate")
            {
                f.WritePause = hold;
                _ = f.Request();
                DisplaySwitchRequestFrame request = ParseRequest(await f.NextWriteAsync());
                await hold.Reached.Task.WaitAsync(Guard);
                await f.FeedAndContinueAsync(Ack(request));
                Assert.True(f.ReceiptReceived);
                f.Feed(Ack(request));
            }
            else
            {
                Task<DisplayChangedFrame?> first = f.Request();
                DisplaySwitchRequestFrame request = ParseRequest(await f.NextWriteAsync());
                await f.FeedAndContinueAsync(Ack(request));
                Assert.NotNull(await first.WaitAsync(Guard));
                _ = f.Request(9);
                DisplaySwitchRequestFrame next = ParseRequest(await f.NextWriteAsync());
                Assert.Equal(1L, next.Sequence);
                f.Feed(Ack(next, hostSequence: 8));
            }
            await f.AssertRejectedAsync("display-switch-receipt-invalid");
            hold.Open();
            await f.Parent.CloseAndJoinAsync().WaitAsync(Guard);
            Assert.Null(f.Parent.LatestDisplayChange);
            f.AssertBorrowedMemoryZero();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("9223372036854775808")]
    [InlineData("18446744073709551615")]
    public async Task Receipt_PreservesUnsignedFrameId_AndCodecDimensionsRatherThanCatalogUiSize(string text)
    {
        Fixture f = new();
        try
        {
            await f.ReadyAsync();
            Task<DisplayChangedFrame?> worker = f.Request();
            DisplaySwitchRequestFrame request = ParseRequest(await f.NextWriteAsync());
            ulong id = ulong.Parse(text, CultureInfo.InvariantCulture);
            string json = Ack(request, firstFrame: id, width: 1, height: FrameLimits.MaxDimension);
            // 最大合法载荷也必须接受；长度上限包括空白。
            if (id == ulong.MaxValue) json += new string(' ', 4096 - Encoding.UTF8.GetByteCount(json));
            await f.FeedAndContinueAsync(json);
            DisplayChangedFrame receipt = Assert.IsType<DisplayChangedFrame>(await worker.WaitAsync(Guard));
            Assert.Equal(id, receipt.FirstFrameId);
            Assert.Equal(1, receipt.Width);
            Assert.Equal(FrameLimits.MaxDimension, receipt.Height);
            Assert.Same(receipt, f.Parent.LatestDisplayChange);
            Assert.Equal(1920, f.Parent.LatestDisplayCatalog!.Displays[0].Width);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReparsedIdenticalCatalog_IsANewInstance_AndInvalidatesPendingOrPublishedReceipt(bool published)
    {
        Fixture f = new();
        Pause hold = f.NewPause();
        try
        {
            await f.ReadyAsync();
            DisplayCatalogFrame catalog = f.Parent.LatestDisplayCatalog!;
            if (!published) f.WritePause = hold;
            Task<DisplayChangedFrame?> worker = f.Request();
            DisplaySwitchRequestFrame request = ParseRequest(await f.NextWriteAsync());
            DisplayChangedFrame? history = null;
            if (published)
            {
                await f.FeedAndContinueAsync(Ack(request));
                history = Assert.IsType<DisplayChangedFrame>(await worker.WaitAsync(Guard));
            }
            else await hold.Reached.Task.WaitAsync(Guard);
            Assert.True(DisplayCatalogFrame.TryParse(Encoding.UTF8.GetBytes(Catalog), out var copy, out _));
            Assert.NotSame(catalog, copy);
            Assert.Equal(catalog.Sequence, copy!.Sequence);
            // 不注入 copy：让唯一 reader 重新解析同一份 wire，检验实际对象身份冻结。
            f.Feed(Catalog);
            await f.AssertRejectedAsync("display-switch-catalog-changed");
            hold.Open();
            if (!published) Assert.Null(await worker.WaitAsync(Guard));
            else Assert.Equal(0L, history!.RequestSequence);
            await f.Parent.CloseAndJoinAsync().WaitAsync(Guard);
            Assert.Null(f.Parent.LatestDisplayCatalog);
            Assert.Null(f.Parent.LatestDisplayChange);
            f.AssertBorrowedMemoryZero();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("child")]
    [InlineData("caller")]
    public async Task Stop_RevokesPendingEarlyAckBeforeJoiningOriginalWriteAndConnectionClose(string owner)
    {
        Fixture f = new();
        Pause writeHold = f.NewPause(), closeHold = f.NewPause();
        using CancellationTokenSource caller = new();
        try
        {
            await f.ReadyAsync();
            f.WritePause = writeHold;
            f.Control.Ssl.ClosePause = closeHold;
            Task<DisplayChangedFrame?> worker = f.Request(token: caller.Token);
            SentWrite write = await f.NextWriteAsync();
            await writeHold.Reached.Task.WaitAsync(Guard);
            await f.FeedAndContinueAsync(Ack(ParseRequest(write)));
            Assert.True(f.ReceiptReceived);
            lock (f.ParentGate)
            {
                if (owner == "parent") f.Parent.RevokeForOwnerCleanup();
                else if (owner == "child") f.Child.MarkStoppedUnderGate();
                else caller.Cancel();
                // 父/child 直接撤销，不借助稍后才运行的取消回调。
                Assert.True(Field<bool>(f.SwitchState, "_stopped"));
                Assert.Null(f.Parent.LatestDisplayChange);
            }
            Task join = f.Keep(f.Parent.CloseAndJoinAsync());
            Task<ClientDisposalEvidence> evidence = f.Keep(f.Parent.GetDisposalEvidenceAsync());
            f.EndInput();
            await closeHold.Reached.Task.WaitAsync(Guard);
            await f.MonitorTask.WaitAsync(Guard);
            await AssertAwaitPathAsync(write.Original, worker);
            await AssertAwaitPathAsync(write.Original, join);
            Assert.False(evidence.IsCompleted);
            Assert.False(f.SwitchRootsReleased);
            Assert.Equal(write.Copy, write.Borrowed.ToArray());
            writeHold.Open();
            Assert.Null(await worker.WaitAsync(Guard));
            Assert.True(write.Original.IsCompleted);
            Assert.False(join.IsCompleted);
            Assert.False(evidence.IsCompleted);
            AssertZero(write.Borrowed);
            closeHold.Open();
            await join.WaitAsync(Guard);
            ClientDisposalEvidence complete = await evidence.WaitAsync(Guard);
            Assert.True(complete.OriginalOperationsFinished);
            Assert.True(complete.AllOwnedRootsReleased);
            f.AssertRevoked();
            f.Control.AssertClosedOnce();
            f.Video.AssertClosedOnce();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("child")]
    public async Task Stop_ClearsAlreadyPublishedReceiptBeforeAnyCloseOrCancellationWorker(string owner)
    {
        Fixture f = new();
        Pause close = f.NewPause();
        try
        {
            await f.ReadyAsync();
            Task<DisplayChangedFrame?> worker = f.Request();
            await f.FeedAndContinueAsync(Ack(ParseRequest(await f.NextWriteAsync())));
            DisplayChangedFrame history = Assert.IsType<DisplayChangedFrame>(await worker.WaitAsync(Guard));
            f.Control.Ssl.ClosePause = close;
            lock (f.ParentGate)
            {
                Assert.Same(history, f.Parent.LatestDisplayChange);
                if (owner == "parent") f.Parent.RevokeForOwnerCleanup();
                else f.Child.MarkStoppedUnderGate();
                Assert.Null(f.Parent.LatestDisplayChange);
                Assert.True(Field<bool>(f.SwitchState, "_stopped"));
                Assert.False(f.Control.Connection.IsCloseRequested);
                Assert.Null(Field<Task?>(f.Child, "_cancel"));
            }
            Assert.Equal(0L, history.RequestSequence);
            Task join = f.Keep(f.Parent.CloseAndJoinAsync());
            f.EndInput();
            await close.Reached.Task.WaitAsync(Guard);
            Assert.False(join.IsCompleted);
            Assert.Null(f.Parent.LatestDisplayChange);
            close.Open();
            await join.WaitAsync(Guard);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact]
    public async Task WriteMultiFault_TimerCleanupAndBothCloseRootsRemainWhole_AndEvidenceDoesNotClaimRelease()
    {
        Fixture f = new();
        Pause writeHold = f.NewPause(), timerHold = f.NewPause();
        IOException leaf = new("共享叶子");
        AggregateException branch = new("不能展平或去重的原分支", leaf,
            new InvalidOperationException("包装", leaf), leaf);
        IOException sibling = new("原写兄弟"), cleanup = new("timer释放根"),
            controlClose = new("Control关闭根"), videoClose = new("Video关闭根");
        TaskCompletionSource<Task> disposing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await f.ReadyAsync();
            Task original = f.Keep(Task.Factory.StartNew(() =>
            {
                writeHold.WaitSynchronously();
                f.Keep(Task.Factory.StartNew(() => { throw branch; }, CancellationToken.None,
                    TaskCreationOptions.AttachedToParent, TaskScheduler.Default));
                f.Keep(Task.Factory.StartNew(() => { throw sibling; }, CancellationToken.None,
                    TaskCreationOptions.AttachedToParent, TaskScheduler.Default));
            }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default));
            f.WriteOverride = original;
            f.Clock.TimerDispose = () =>
            {
                Task task = f.Keep(DisposeTimerAsync());
                disposing.TrySetResult(task);
                return task;
            };
            f.Control.Ssl.DisposeFailure = controlClose;
            f.Video.Ssl.DisposeFailure = videoClose;
            Task<DisplayChangedFrame?> worker = f.Request();
            SentWrite write = await f.NextWriteAsync();
            Assert.Same(original, write.Original);
            await writeHold.Reached.Task.WaitAsync(Guard);
            // timer 释放策略在创建时冻结；后续 ACK 读预算不注入这个故障。
            f.Clock.TimerDispose = null;
            await f.FeedAndContinueAsync(Ack(ParseRequest(write)));
            writeHold.Open();
            await timerHold.Reached.Task.WaitAsync(Guard);
            Task timerTask = await disposing.Task.WaitAsync(Guard);
            f.EndInput();
            Task join = f.Keep(f.Parent.CloseAndJoinAsync());
            await AssertAwaitPathAsync(timerTask, worker);
            await AssertAwaitPathAsync(timerTask, join);
            Assert.False(f.SwitchRootsReleased);
            timerHold.Open();
            Assert.Null(await worker.WaitAsync(Guard));
            await ObserveAsync(original);
            await ObserveAsync(timerTask);
            await ObserveAsync(join);
            Exception saved = Assert.IsAssignableFrom<Exception>(f.SendAttempt.Snapshot.OperationError);
            AssertOriginalRoot(original, saved);
            Assert.True(ContainsReference(saved, branch));
            Assert.True(ContainsReference(saved, sibling));
            Assert.Same(leaf, branch.InnerExceptions[0]);
            Assert.Same(leaf, branch.InnerExceptions[2]);
            foreach (Exception root in new Exception[] { saved, cleanup, controlClose, videoClose })
                Assert.Contains(f.Parent.LifetimeErrors, error => ContainsReference(error, root));
            ClientDisposalEvidence evidence = await f.Parent.GetDisposalEvidenceAsync().WaitAsync(Guard);
            Assert.True(evidence.OriginalOperationsFinished);
            Assert.False(evidence.AllOwnedRootsReleased);
            Assert.Null(f.Parent.LatestDisplayChange);
            f.AssertBorrowedMemoryZero();
        }
        finally { await f.FinishAsync(); }

        async Task DisposeTimerAsync()
        {
            await timerHold.WaitAsync().ConfigureAwait(false);
            throw cleanup;
        }
    }

    [Fact]
    public async Task UtcSamplingConsumesOriginalTenSecondSendBudget_WithoutWritingAfterExpiry()
    {
        Fixture f = new();
        Pause utc = f.NewPause();
        try
        {
            await f.ReadyAsync();
            f.Clock.UtcHook = utc.WaitSynchronously;
            Task<DisplayChangedFrame?> worker = f.Request();
            await utc.Reached.Task.WaitAsync(Guard);
            Assert.Equal(1, f.Clock.UtcReads);
            Assert.Equal(TimeSpan.FromSeconds(10), f.Clock.Timers.Last().DueTime);
            f.Clock.Timestamp = TimeSpan.FromSeconds(10).Ticks;
            // 不派发 timer；UTC 返回后的原绝对预算末检必须自行拒绝。
            utc.Open();
            Assert.Null(await worker.WaitAsync(Guard));
            f.AssertNoControlWrite();
            Assert.Null(f.Parent.LatestDisplayChange);
            f.AssertRevoked();
            f.EndInput();
            await f.Parent.CloseAndJoinAsync().WaitAsync(Guard);
            Assert.Contains(f.Parent.LifetimeErrors, root => ContainsType<TimeoutException>(root));
        }
        finally { await f.FinishAsync(); }
    }

    [Fact]
    public async Task AckIdleAddsNoTimer_AndPartialFrameKeepsFirstByteDeadlineAcrossProgress()
    {
        Fixture f = new();
        Pause prefix = f.NewPause(), payload = f.NewPause();
        try
        {
            await f.ReadyAsync();
            Task<DisplayChangedFrame?> worker = f.Request();
            DisplaySwitchRequestFrame request = ParseRequest(await f.NextWriteAsync());
            await f.SentTask.WaitAsync(Guard);
            Assert.Equal(3, f.Clock.Timers.Count); // attach、目录、发送；等 ACK 没有新 timer。
            f.Clock.Timestamp = TimeSpan.FromMinutes(1).Ticks;
            Assert.False(worker.IsCompleted);
            Assert.False(f.MonitorTask.IsCompleted);
            Assert.Equal(3, f.Clock.Timers.Count);
            Packet packet = new(Framed(Ack(request)));
            packet.Stops.Add(1, prefix);
            packet.Stops.Add(4, payload);
            f.Feed(packet);
            await prefix.Reached.Task.WaitAsync(Guard);
            Assert.Equal(4, f.Clock.Timers.Count);
            Assert.Equal(TimeSpan.FromSeconds(10), f.Clock.Timers.Last().DueTime);
            f.Clock.Timestamp = TimeSpan.FromSeconds(69).Ticks;
            prefix.Open();
            await payload.Reached.Task.WaitAsync(Guard);
            f.Clock.Timestamp = TimeSpan.FromSeconds(70).Ticks;
            payload.Open();
            await f.MonitorTask.WaitAsync(Guard);
            Assert.IsType<TimeoutException>(f.Parent.ControlMonitorState!.Error);
            Assert.Null(await worker.WaitAsync(Guard));
            Assert.Equal(4, f.Clock.Timers.Count);
            f.AssertRevoked();
            f.AssertBorrowedMemoryZero();
        }
        finally { await f.FinishAsync(); }
    }

    [Fact]
    public async Task Sequence_LongMaxIsSentOnce_ThenRejectsWithoutWrapOrExtraIo()
    {
        Fixture f = new();
        try
        {
            await f.ReadyAsync();
            lock (f.ParentGate)
                RuntimeField(f.SwitchState.GetType(), "_nextSequence")!.SetValue(f.SwitchState, long.MaxValue);
            Task<DisplayChangedFrame?> worker = f.Request();
            DisplaySwitchRequestFrame request = ParseRequest(await f.NextWriteAsync());
            Assert.Equal(long.MaxValue, request.Sequence);
            await f.FeedAndContinueAsync(Ack(request));
            DisplayChangedFrame receipt = Assert.IsType<DisplayChangedFrame>(await worker.WaitAsync(Guard));
            Assert.Equal(long.MaxValue, receipt.RequestSequence);
            int samples = f.Clock.TimestampReads, timers = f.Clock.Timers.Count;
            Assert.Throws<InvalidOperationException>(() => { _ = f.Parent.RequestDisplayAsync(new DisplayId(9)); });
            Assert.Throws<InvalidOperationException>(() => { _ = f.Parent.RequestDisplayAsync(new DisplayId(4)); });
            Assert.Equal(long.MaxValue, f.NextSequence);
            Assert.Same(receipt, f.Parent.LatestDisplayChange);
            Assert.Single(f.Control.Ssl.Writes);
            Assert.Equal(1, f.Control.Ssl.FlushCalls);
            Assert.Equal(samples, f.Clock.TimestampReads);
            Assert.Equal(timers, f.Clock.Timers.Count);
        }
        finally { await f.FinishAsync(); }
    }

    private static DisplaySwitchRequestFrame ParseRequest(SentWrite write)
    {
        uint length = BinaryPrimitives.ReadUInt32BigEndian(write.Copy);
        Assert.InRange(length, 1U, 4096U);
        Assert.Equal((int)length + 4, write.Copy.Length);
        Assert.True(DisplaySwitchRequestFrame.TryParse(write.Copy.AsSpan(4), new Guid(SessionText), out var request, out var rejection), rejection);
        using JsonDocument json = JsonDocument.Parse(write.Copy.AsMemory(4));
        Assert.Equal("request_display", json.RootElement.GetProperty("type").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("protocol").GetInt32());
        Assert.Equal(7, json.RootElement.EnumerateObject().Count());
        return Assert.IsType<DisplaySwitchRequestFrame>(request);
    }

    private static string Ack(DisplaySwitchRequestFrame request, long hostSequence = 8, ulong firstFrame = 0,
        int width = 1920, int height = 1080) => Encoding.UTF8.GetString(new DisplayChangedFrame(request.SessionId,
            hostSequence, 456, request.Sequence, request.CatalogSequence, request.DisplayId, firstFrame, width, height).Serialize());

    private static string UnsolicitedAck() => Encoding.UTF8.GetString(new DisplayChangedFrame(new Guid(SessionText),
        8, 456, 0, 7, new DisplayId(4), 0, 1920, 1080).Serialize());

    private static byte[] Framed(string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] result = new byte[payload.Length + 4];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)payload.Length);
        payload.CopyTo(result, 4);
        return result;
    }

    private static void AssertZero(ReadOnlyMemory<byte> memory) =>
        Assert.True(memory.Span.IndexOfAnyExcept((byte)0) < 0, "原借用退出后必须清零。");

    private static bool ContainsReference(Exception root, Exception expected) => ReferenceEquals(root, expected) ||
        (root is AggregateException aggregate ? aggregate.InnerExceptions.Any(inner => ContainsReference(inner, expected))
            : root.InnerException is { } inner && ContainsReference(inner, expected));

    private static bool ContainsType<T>(Exception root) where T : Exception => root is T ||
        (root is AggregateException aggregate ? aggregate.InnerExceptions.Any(ContainsType<T>)
            : root.InnerException is { } inner && ContainsType<T>(inner));

    private static void AssertOriginalRoot(Task original, Exception saved)
    {
        AggregateException container = Assert.IsType<AggregateException>(original.Exception);
        if (container.InnerExceptions.Count == 1) Assert.Same(container.InnerExceptions[0], saved);
        else
        {
            AggregateException actual = Assert.IsType<AggregateException>(saved);
            Assert.Equal(container.InnerExceptions.Count, actual.InnerExceptions.Count);
            for (int i = 0; i < container.InnerExceptions.Count; i++)
                Assert.Same(container.InnerExceptions[i], actual.InnerExceptions[i]);
        }
    }

    private static async Task ObserveAsync(Task original)
    {
        // watchdog 只报诊断，不触发停止，不把超时吞作业务错误。
        using CancellationTokenSource watchdog = new();
        Task guard = Task.Delay(Guard, watchdog.Token);
        try { Assert.Same(original, await Task.WhenAny(original, guard).ConfigureAwait(false)); }
        finally { watchdog.Cancel(); }
        try { await original.ConfigureAwait(false); }
        catch { _ = original.Exception; }
    }

    // 最小复制旧 monitor 夹具的原 Task await 证明；不认可 WhenAny 或测试 TCS 作为 join。
    private static async Task AssertAwaitPathAsync(Task awaited, Task returned)
    {
        using CancellationTokenSource watchdog = new();
        Task guard = Task.Delay(Guard, watchdog.Token);
        try
        {
            while (!HasAwaitPath(awaited, returned, [], 24))
            {
                Assert.False(awaited.IsCompleted, "原任务已退出，无法证明受闸等待。");
                Assert.False(returned.IsCompleted, "返回任务遗漏了原任务 join。");
                if (guard.IsCompleted) throw new XunitException("watchdog：未找到原 Task await 链；不能据此冒充业务断言。");
                await Task.Yield();
            }
        }
        finally { watchdog.Cancel(); }
    }

    private static bool HasAwaitPath(Task awaited, Task returned, HashSet<Task> visited, int remaining)
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
            if (!exactAwait && !unwrap && !all) continue;
            if (ReferenceEquals(next, returned) || HasAwaitPath(next, returned, visited, remaining - 1)) return true;
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

    private sealed class Packet(byte[] data)
    {
        internal byte[] Data { get; } = data;
        internal Dictionary<int, Pause> Stops { get; } = [];
        internal TaskCompletionSource Continued { get; } = Signal();
        internal int Offset;
    }

    private sealed record SentWrite(byte[] Copy, ReadOnlyMemory<byte> Borrowed, Task Original);

    private sealed class ProbeClock : TimeProvider
    {
        private long _timestamp;
        private int _reads, _utcReads;
        internal Action? UtcHook;
        internal Func<Task>? TimerDispose;
        internal DateTimeOffset UtcNow { get; } = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_123);
        internal ConcurrentQueue<ProbeTimer> Timers { get; } = new();
        internal long Timestamp { get => Interlocked.Read(ref _timestamp); set => Interlocked.Exchange(ref _timestamp, value); }
        internal int TimestampReads => Volatile.Read(ref _reads);
        internal int UtcReads => Volatile.Read(ref _utcReads);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { Interlocked.Increment(ref _reads); return Timestamp; }
        public override DateTimeOffset GetUtcNow()
        {
            Interlocked.Increment(ref _utcReads);
            Interlocked.Exchange(ref UtcHook, null)?.Invoke();
            return UtcNow;
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            ProbeTimer timer = new(dueTime, TimerDispose);
            Timers.Enqueue(timer);
            return timer;
        }
    }

    private sealed class ProbeTimer(TimeSpan dueTime, Func<Task>? dispose) : ITimer
    {
        private bool _disposed;
        internal TimeSpan DueTime { get; private set; } = dueTime;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_disposed) return false;
            if (dueTime != Timeout.InfiniteTimeSpan) DueTime = dueTime;
            return true;
        }
        public void Dispose() => _disposed = true;
        public ValueTask DisposeAsync()
        {
            Dispose();
            return dispose is null ? ValueTask.CompletedTask : new ValueTask(dispose());
        }
    }

    private sealed class Fixture
    {
        private readonly ConcurrentBag<Task> _tasks = [];
        private readonly List<Pause> _pauses = [];
        private readonly Channel<Packet> _input = Channel.CreateUnbounded<Packet>();
        private readonly Channel<SentWrite> _writes = Channel.CreateUnbounded<SentWrite>();
        private readonly byte[] _callerToken = Convert.FromHexString(TokenHex);
        private readonly byte[] _ownedToken;
        private readonly byte[] _videoInput = Framed(AttachAck);
        private readonly ConcurrentQueue<Memory<byte>> _payloadMemories = [];
        private Packet? _packet;
        private int _activeReads, _maxConcurrentReads, _videoOffset;
        private Task _lastFlush = Task.CompletedTask;
        internal Endpoint Control { get; } = new();
        internal Endpoint Video { get; } = new();
        internal ProbeClock Clock { get; } = new();
        internal AuthenticatedControlSession Parent { get; }
        internal Pause? WritePause, FlushPause;
        internal Task? WriteOverride;
        internal Exception? WriteFailure;
        internal object ParentGate => Field<object>(Parent, "_gate");
        internal Child Child { get { lock (ParentGate) return Field<Child>(Parent, "_videoLifetime"); } }
        internal object SwitchState { get { lock (ParentGate) return Field<object>(Parent, "_displaySwitch"); } }
        internal object? Pending { get { lock (ParentGate) return Field<object?>(SwitchState, "_pending"); } }
        internal long NextSequence { get { lock (ParentGate) return Field<long>(SwitchState, "_nextSequence"); } }
        internal HostDisplayCatalogSendAttempt SendAttempt
        { get { lock (ParentGate) return Field<HostDisplayCatalogSendAttempt>(SwitchState, "_attempt"); } }
        internal Task SentTask { get { lock (ParentGate) return Field<Task>(SwitchState, "_original"); } }
        internal Task MonitorTask => Assert.IsAssignableFrom<Task>(Parent.ControlMonitorCompletion);
        internal Task LastFlush => Volatile.Read(ref _lastFlush);
        internal int MaxConcurrentReads => Volatile.Read(ref _maxConcurrentReads);
        internal bool SwitchRootsReleased
        { get { lock (ParentGate) return (bool)SwitchState.GetType().GetProperty("AllOwnedRootsReleased", Fields)!.GetValue(SwitchState)!; } }
        internal bool ReceiptReceived
        {
            get
            {
                lock (ParentGate)
                    return Pending is { } pending && Field<TaskCompletionSource<DisplayChangedFrame>>(pending,
                        "<Received>k__BackingField").Task.IsCompletedSuccessfully;
            }
        }

        internal Fixture(bool delivered = true, bool enable = true)
        {
            Parent = new AuthenticatedControlSession(Control.Connection, SessionPermission.Control,
                new Guid(SessionText), "ABCDEF", _callerToken, 15_000, 0, Clock,
                V2AgreementFixture.Create(new Guid(SessionText)));
            _ownedToken = Field<byte[]>(Parent, "_sessionToken");
            Assert.NotSame(_callerToken, _ownedToken);
            Control.Ssl.ReadBody = (memory, token) =>
            {
                Assert.False(Monitor.IsEntered(ParentGate));
                Assert.NotNull(Parent.ControlMonitorCompletion);
                Assert.Equal(CancellationToken.None, token);
                return new ValueTask<int>(Keep(ReadControlAsync(memory)));
            };
            Control.Ssl.WriteBody = (memory, token) =>
            {
                Assert.False(Monitor.IsEntered(ParentGate));
                Assert.Equal(CancellationToken.None, token);
                Task original = Keep(WriteOverride ?? WriteAsync());
                _writes.Writer.TryWrite(new SentWrite(memory.ToArray(), memory, original));
                return new ValueTask(original);
            };
            Control.Ssl.FlushBody = token =>
            {
                Assert.Equal(CancellationToken.None, token);
                Task original = Keep(FlushPause?.WaitAsync() ?? Task.CompletedTask);
                Volatile.Write(ref _lastFlush, original);
                return original;
            };
            Video.Ssl.ReadBody = (memory, _) =>
            {
                int count = Math.Min(memory.Length, _videoInput.Length - _videoOffset);
                _videoInput.AsMemory(_videoOffset, count).CopyTo(memory);
                _videoOffset += count;
                return new ValueTask<int>(Keep(Task.FromResult(count)));
            };
            if (delivered) Parent.CommitDelivery();
            if (enable) Parent.EnableDisplaySwitching();
        }

        internal T Keep<T>(T task) where T : Task { _tasks.Add(task); return task; }
        internal Pause NewPause() { Pause pause = new(); _pauses.Add(pause); return pause; }
        internal Task<DisplayChangedFrame?> Request(int id = 4, CancellationToken token = default) =>
            Keep(Parent.RequestDisplayAsync(new DisplayId(id), token));
        internal async Task<SentWrite> NextWriteAsync() => await _writes.Reader.ReadAsync().AsTask().WaitAsync(Guard);
        internal void Feed(string json) => Feed(new Packet(Framed(json)));
        internal void Feed(Packet packet) => Assert.True(_input.Writer.TryWrite(packet));
        internal void EndInput() => _input.Writer.TryComplete();
        internal void HoldSend(string stage, Pause hold)
        {
            if (stage == "write") WritePause = hold;
            else FlushPause = hold;
        }
        internal async Task FeedAndContinueAsync(string json)
        {
            Packet packet = new(Framed(json));
            Feed(packet);
            await Task.WhenAny(packet.Continued.Task, MonitorTask).WaitAsync(Guard);
            Assert.False(MonitorTask.IsCompleted, "预期合法帧却导致唯一 reader 终止。");
            Assert.True(packet.Continued.Task.IsCompleted);
        }
        internal async Task StartAsync()
        {
            Task<TlsConnection> Connect(ConnectionTarget target, TransportTimeouts timeouts, TimeProvider clock, CancellationToken token)
            {
                Assert.False(Monitor.IsEntered(ParentGate));
                Assert.Same(Clock, clock);
                Assert.Same(TransportTimeouts.Default, timeouts);
                Assert.Equal(Parent.Identity.DeviceId, target.DeviceId);
                return Keep(Task.FromResult(Video.Connection));
            }
            await Keep(Parent.AttachVideoWithControlMonitorForTestingAsync(Connect,
                length => MemoryPool<byte>.Shared.Rent(length), _ => throw new XunitException("切屏测试不应消费视频帧。"))).WaitAsync(Guard);
            Assert.Equal(_videoInput.Length, _videoOffset);
            byte[] proof = Assert.Single(Video.Ssl.Writes);
            using JsonDocument json = JsonDocument.Parse(proof.AsMemory(4));
            Assert.Equal("channel_hello", json.RootElement.GetProperty("type").GetString());
            Assert.Equal(SessionText, json.RootElement.GetProperty("sessionId").GetString());
            Assert.All(Video.Ssl.BorrowedWrites, AssertZero);
        }
        internal async Task ReadyAsync()
        {
            await StartAsync();
            await FeedAndContinueAsync(Catalog);
            Assert.NotNull(Parent.LatestDisplayCatalog);
            Assert.Equal(2, Clock.Timers.Count);
        }
        private async Task WriteAsync()
        {
            if (WritePause is { } pause) await pause.WaitAsync().ConfigureAwait(false);
            if (WriteFailure is { } failure) throw failure;
        }
        private async Task<int> ReadControlAsync(Memory<byte> memory)
        {
            int active = Interlocked.Increment(ref _activeReads);
            int old;
            do { old = Volatile.Read(ref _maxConcurrentReads); if (old >= active) break; }
            while (Interlocked.CompareExchange(ref _maxConcurrentReads, active, old) != old);
            try
            {
                if (_packet is null || _packet.Offset == _packet.Data.Length)
                {
                    Assert.Equal(1, memory.Length);
                    _packet?.Continued.TrySetResult();
                    if (!await _input.Reader.WaitToReadAsync().ConfigureAwait(false)) return 0;
                    _packet = await _input.Reader.ReadAsync().ConfigureAwait(false);
                }
                Packet packet = _packet;
                int offset = packet.Offset;
                if (packet.Stops.TryGetValue(offset, out Pause? pause)) await pause.WaitAsync().ConfigureAwait(false);
                int count = Math.Min(memory.Length, packet.Data.Length - offset);
                packet.Data.AsMemory(offset, count).CopyTo(memory);
                if (offset >= 4) _payloadMemories.Enqueue(memory);
                packet.Offset += count;
                return count;
            }
            finally { Interlocked.Decrement(ref _activeReads); }
        }
        internal void AssertNoControlWrite()
        {
            Assert.Empty(Control.Ssl.Writes);
            Assert.Equal(0, Control.Ssl.FlushCalls);
        }
        internal void AssertRevoked()
        {
            Assert.Null(Parent.LatestDisplayChange);
            Assert.Null(Parent.LatestDisplayCatalog);
            Assert.Same(_ownedToken, Field<byte[]>(Parent, "_sessionToken"));
            Assert.All(_ownedToken, value => Assert.Equal((byte)0, value));
            Assert.Equal(TokenHex, Convert.ToHexString(_callerToken));
            Assert.Throws<ObjectDisposedException>(() => { _ = Parent.Stream; });
        }
        internal async Task AssertRejectedAsync(string reason)
        {
            await MonitorTask.WaitAsync(Guard);
            FrameProtocolException error = Assert.IsType<FrameProtocolException>(Parent.ControlMonitorState!.Error);
            Assert.Equal(reason, error.Reason);
            AssertRevoked();
            Assert.True(Control.Connection.IsCloseRequested);
            Assert.True(Video.Connection.IsCloseRequested);
        }
        internal void AssertBorrowedMemoryZero()
        {
            Assert.All(Control.Ssl.BorrowedWrites, AssertZero);
            Assert.All(_payloadMemories, memory => AssertZero(memory));
        }
        internal async Task FinishAsync()
        {
            Clock.UtcHook = null;
            Clock.TimerDispose = null;
            foreach (Pause pause in _pauses) pause.Open();
            EndInput();
            try
            {
                Task join = Parent.CloseAndJoinAsync();
                await ObserveAsync(join);
                if (Parent.ControlMonitorCompletion is { } monitor) await ObserveAsync(monitor);
                // 只 join 真实原操作，不登记也不等待可能永不完成的 pending.Received promise。
                await ObserveAsync(Task.WhenAll(_tasks));
                await Parent.GetDisposalEvidenceAsync().WaitAsync(Guard);
                await ObserveAsync(Task.WhenAll(_tasks));
            }
            finally
            {
                try
                {
                    await ObserveAsync(Task.WhenAll(Control.Connection.CloseAsync(), Video.Connection.CloseAsync()));
                    await ObserveAsync(Task.WhenAll(_tasks));
                }
                finally
                {
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
        internal void AssertClosedOnce()
        {
            Assert.True(Connection.IsCloseRequested);
            Assert.True(Connection.CloseAsync().IsCompletedSuccessfully);
            Assert.Equal(1, Ssl.DisposeCalls);
            Assert.Equal(1, Client.DisposeCalls);
        }
        internal void ReleaseResources()
        {
            try { Ssl.ReleaseResources(); }
            finally { Client.ReleaseResources(); }
        }
    }

    private sealed class ProbeTcpClient : TcpClient
    {
        private int _disposeCalls;
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        protected override void Dispose(bool disposing)
        {
            if (disposing) Interlocked.Increment(ref _disposeCalls);
            base.Dispose(disposing);
        }
        internal void ReleaseResources() => base.Dispose(true);
    }

    private sealed class ProbeSslStream() : SslStream(new MemoryStream(), leaveInnerStreamOpen: false)
    {
        private int _flushCalls, _disposeCalls;
        internal Func<Memory<byte>, CancellationToken, ValueTask<int>> ReadBody = null!;
        internal Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>? WriteBody;
        internal Func<CancellationToken, Task>? FlushBody;
        internal Pause? ClosePause;
        internal Exception? DisposeFailure;
        internal ConcurrentQueue<int> ReadSizes { get; } = new();
        internal ConcurrentQueue<CancellationToken> ReadTokens { get; } = new();
        internal ConcurrentQueue<CancellationToken> WriteTokens { get; } = new();
        internal ConcurrentQueue<CancellationToken> FlushTokens { get; } = new();
        internal ConcurrentQueue<byte[]> Writes { get; } = new();
        internal ConcurrentQueue<ReadOnlyMemory<byte>> BorrowedWrites { get; } = new();
        internal int FlushCalls => Volatile.Read(ref _flushCalls);
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        public override ValueTask<int> ReadAsync(Memory<byte> memory, CancellationToken cancellationToken = default)
        {
            ReadSizes.Enqueue(memory.Length);
            ReadTokens.Enqueue(cancellationToken);
            return ReadBody(memory, cancellationToken);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> memory, CancellationToken cancellationToken = default)
        {
            WriteTokens.Enqueue(cancellationToken);
            Writes.Enqueue(memory.ToArray());
            BorrowedWrites.Enqueue(memory);
            return WriteBody?.Invoke(memory, cancellationToken) ?? ValueTask.CompletedTask;
        }
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _flushCalls);
            FlushTokens.Enqueue(cancellationToken);
            return FlushBody?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }
        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    Interlocked.Increment(ref _disposeCalls);
                    ClosePause?.WaitSynchronously();
                    if (DisposeFailure is { } error) throw error;
                }
            }
            finally { base.Dispose(disposing); }
        }
        internal void ReleaseResources() => base.Dispose(true);
    }
}
