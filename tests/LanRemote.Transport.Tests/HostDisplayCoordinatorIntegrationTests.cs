using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

// 真实认证、请求读取、协调、视频附着及发送链；流是受控内存流，不是 TLS/GDI/默认 App 验收。
public sealed class HostDisplayCoordinatorIntegrationTests
{
    private const int TestTimeout = 60_000;
    private static readonly TimeSpan Guard = CatalogLifetimeScenario.Guard;
    private static readonly TransportTimeouts Timeouts = new(TransportTimeouts.Maximum, TransportTimeouts.Maximum,
        TransportTimeouts.Maximum, TransportTimeouts.Maximum, TransportTimeouts.Maximum, TransportTimeouts.Maximum);

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prepare_Only_Wiring_Waits_For_Real_Video_Flush_Then_Same_Control_Writer_Reply(bool approval)
    {
        await using Fixture f = new(approval: approval);
        f.Start();
        await f.AuthenticateAsync();
        Task firstRead = Assert.Single(f.Stream.Reads).Original.Task;
        Task completion = f.Coordinator.Completion;
        await f.MakeReadyAsync();
        DisplayCatalogFrame catalog = Assert.IsType<DisplayCatalogFrame>(f.Lifetime.Catalog);
        Assert.NotSame(f.SourceCatalog, catalog);
        Assert.Equal(0, catalog.Sequence);
        Assert.True(DisplayCatalogFrame.TryParse(Payload(Assert.Single(f.Stream.PostAuthWrites)), out var wireCatalog, out _));
        Assert.Equal(catalog.Displays, wireCatalog!.Displays);
        Assert.Equal(0, wireCatalog.Sequence);
        Assert.Null(f.Context.DisplayCatalogSend!.DisplayRequestHandler);
        Assert.NotNull(f.Context.DisplayCatalogSend.DisplayRequestPrepare);
        Assert.Null(Field<object?>(f.Coordinator, "_executor"));

        // 不传 enableProducerReplacement；同时覆盖默认起点 0 和显式起点 70。
        await f.AttachAndWaitAsync(approval ? 70UL : null);
        ulong firstId = approval ? 70UL : 0UL;
        Assert.Equal(firstId, Assert.Single(f.Video!.Headers()).FrameId);
        byte[] request = Frame(f.Request(9).Serialize());
        f.Stream.Feed(request);
        await f.PrepareEntered.Task.WaitAsync(Guard);
        Assert.Same(catalog.Displays[1], f.PreparedDisplay);
        Assert.False(f.PrepareToken.IsCancellationRequested);
        f.ReleasePrepare();
        await f.Next.ReadEntered.Task.WaitAsync(Guard);
        DisplaySwitchExecutor.ExecutionResult replaced = await f.Execution.WaitAsync(Guard);
        Assert.True(replaced.Transferred && replaced.ReplacementCompleted);
        var generation = Assert.IsType<DisplaySwitchExecutor.RequestSendReceipt>(replaced.SendReceipt).Generation;
        Task handling = f.Active;
        Assert.Equal(1, f.Initial.StopCalls);
        Assert.Equal(1, f.Initial.DisposeCalls);
        Assert.Equal(1, f.Next.StartCalls);
        f.Next.DeliverFrame();
        await f.Video.SwitchFlush.Entered.Task.WaitAsync(Guard);
        Task originalFlush = Assert.IsAssignableFrom<Task>(f.Video.SwitchFlush.Operation);
        Assert.False(originalFlush.IsCompleted);
        Assert.False(generation.FirstSend.IsCompleted);
        Assert.False(handling.IsCompleted);
        Assert.False(f.HandlerWorker.IsCompleted);
        Assert.Single(f.Stream.PostAuthWrites);
        VideoFrameHeader sentHeader = f.Video.Headers()[1];
        Assert.Equal(firstId + 1, sentHeader.FrameId);
        Assert.Equal(640, sentHeader.Width);
        Assert.Equal(360, sentHeader.Height);
        Assert.NotEqual(catalog.Displays[1].Width, sentHeader.Width);
        Assert.Equal(VideoFrameTestData.Payload(), f.Video.Writes.Last());

        f.Video.SwitchFlush.Release();
        await f.Stream.ReplyFlush.Entered.Task.WaitAsync(Guard);
        Assert.Equal(new VideoFrameSender.SentFrame(sentHeader.FrameId, sentHeader.Width, sentHeader.Height),
            await generation.FirstSend.WaitAsync(Guard));
        // 首发成功仍不等于本次 Handle 完成；它也必须 join 真实 Control 回执的 Flush。
        Assert.False(f.Stream.ReplyFlush.Operation!.IsCompleted);
        Assert.False(handling.IsCompleted);
        f.Stream.ReplyFlush.Release();
        await handling.WaitAsync(Guard);
        await f.HandlerWorker.WaitAsync(Guard);
        await f.Stream.ReadRequested(4).WaitAsync(Guard);
        DisplayChangedFrame changed = f.Changed();
        Assert.Equal(1, changed.Sequence);
        Assert.Equal(9, changed.RequestSequence);
        Assert.Equal(catalog.Sequence, changed.CatalogSequence);
        Assert.Equal(catalog.Displays[1].Id, changed.DisplayId);
        Assert.Equal(sentHeader.FrameId, changed.FirstFrameId);
        Assert.Equal(sentHeader.Width, changed.Width);
        Assert.Equal(sentHeader.Height, changed.Height);
        Assert.Equal(f.Clock.GetUtcNow().ToUnixTimeMilliseconds(), changed.TimestampMs);
        Assert.Same(completion, f.Coordinator.Completion);
        Assert.Same(handling, f.Active);
        Assert.False(completion.IsCompleted);
        Assert.False(f.CloseRequired.Task.IsCompleted);
        Assert.Equal(1, f.Factory.Calls);
        Assert.Equal(1, f.PrepareCalls);
        Assert.Equal(new[] { 1, 3, request.Length - 4, 1 }, f.Stream.Reads.Select(read => read.Buffer.Length));
        Assert.Equal(new byte[] { 0 }, f.Stream.Reads.First().Delivered);
        Assert.Same(firstRead, f.Stream.Reads.First().Original.Task);
        Assert.Equal(1, f.Stream.MaxConcurrentReads);
        Assert.Equal(0, f.Inner.HoldingCalls);

        await f.EndControlAsync();
        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Null(f.Reader.Error);
        Assert.Empty(f.Diagnostics());
        Assert.Equal(1, f.Next.StopCalls);
        Assert.Equal(1, f.Next.DisposeCalls);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Reply_Flush_Fault_Is_Reported_Once_By_Writer_Not_Coordinator()
    {
        await using Fixture f = new();
        await f.StartReadyVideoAsync();
        f.Stream.Feed(Frame(f.Request(1).Serialize()));
        await f.PrepareEntered.Task.WaitAsync(Guard);
        f.ReleasePrepare();
        await f.Next.ReadEntered.Task.WaitAsync(Guard);
        f.Next.DeliverFrame();
        await f.Video!.SwitchFlush.Entered.Task.WaitAsync(Guard);
        f.Video.SwitchFlush.Release();
        await f.Stream.ReplyFlush.Entered.Task.WaitAsync(Guard);
        IOException failure = new("回执原Flush故障唯一归属。");
        f.Stream.ReplyFlush.Fail(failure);
        await f.CloseRequired.Task.WaitAsync(Guard);
        await f.Revoked.WaitAsync(Guard);
        await f.Run!.WaitAsync(Guard);
        await f.VideoRun!.WaitAsync(Guard);
        Assert.Null(f.Coordinator.Error);
        Assert.Null(f.Reader.Error);
        Exception diagnostic = Assert.Single(f.Diagnostics());
        Assert.Equal("host-catalog-owner-completion", diagnostic.Message);
        Assert.Single(Tree(diagnostic), error => ReferenceEquals(error, failure));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Ready_Catalog_Without_Published_Video_Is_Lazy_Until_Request_Then_Fails_Closed_With_Zero_Prepare()
    {
        await using Fixture f = new(holdInitialStart: true);
        f.Start();
        await f.AuthenticateAsync();
        await f.MakeReadyAsync();
        f.AttachVideo();
        await f.Factory.Entered.Task.WaitAsync(Guard);
        await f.Initial.StartPause.Entered.Task.WaitAsync(Guard);
        Assert.Equal(VideoAttachStatus.Attached, f.Router!.AttachStatus);
        Assert.True(f.Lifetime.Ready.IsCompletedSuccessfully);
        Assert.Null(f.PublishedSource);
        Assert.Null(Field<object?>(f.Coordinator, "_executor"));
        Assert.False(f.CloseRequired.Task.IsCompleted);
        Assert.False(f.Coordinator.Completion.IsCompleted);
        Assert.False(f.Run!.IsCompleted);
        Assert.False(f.VideoRun!.IsCompleted);
        Assert.Equal(0, f.PrepareCalls);
        Assert.Single(f.Video!.Writes); // 真实附着 ACK 已完成，但初代 Start 尚未返回。

        f.Stream.Feed(Frame(f.Request(1).Serialize()));
        await f.CloseRequired.Task.WaitAsync(Guard);
        await f.Revoked.WaitAsync(Guard);
        await f.Initial.StopEntered.Task.WaitAsync(Guard);
        Assert.False(f.Registration.CheckCurrent());
        Assert.Equal(0, f.Context.SessionRegistry.ActiveSessionCount);
        Assert.True((await f.Run.WaitAsync(Guard)).Completed);
        await f.VideoRun.WaitAsync(Guard);
        Assert.Equal(0, f.PrepareCalls);
        Assert.Equal(0, f.Next.StartCalls);
        Assert.Null(Field<object?>(f.Coordinator, "_executor"));
        Assert.Null(f.Reader.Error);
        Exception diagnostic = Assert.Single(f.Diagnostics());
        Assert.Equal("host-catalog-request-coordinator", diagnostic.Message);
        Assert.Same(f.Coordinator.Error, diagnostic.InnerException);
        Assert.IsType<InvalidOperationException>(f.Coordinator.Error);
        Assert.Single(f.Stream.PostAuthWrites);
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Eof_Unregisters_And_Stops_Video_Before_Joining_Prepare_And_Late_Local_Candidate(bool blockPrefix)
    {
        await using Fixture f = new(blockPreparePrefix: blockPrefix, holdCandidateCleanup: true);
        await f.StartReadyVideoAsync();
        Task completion = f.Coordinator.Completion;
        f.Stream.Feed(Frame(f.Request(1).Serialize()));
        await f.PrepareEntered.Task.WaitAsync(Guard);
        if (blockPrefix) await f.PreparePause.Entered.Task.WaitAsync(Guard);
        await f.Stream.ReadRequested(4).WaitAsync(Guard);
        Task handling = f.Active;
        f.Stream.CompleteInput();
        await f.Revoked.WaitAsync(Guard);
        await f.Initial.StopEntered.Task.WaitAsync(Guard);
        await f.Video!.Closed.Task.WaitAsync(Guard);
        await f.Inner.DisposeExited.Task.WaitAsync(Guard);
        Assert.True(f.CloseRequired.Task.IsCompleted);
        Assert.False(f.Registration.CheckCurrent());
        Assert.Equal(0, f.Context.SessionRegistry.ActiveSessionCount);
        Assert.False(f.PrepareResult.Task.IsCompleted);
        Assert.False(handling.IsCompleted);
        Assert.False(f.Reader.Completion!.IsCompleted);
        Assert.False(completion.IsCompleted);
        Assert.False(f.Run!.IsCompleted);

        f.PreparePause.Open();
        await CatalogLifetimeScenario.AssertAwaitPathAsync(f.PrepareResult.Task, f.Run);
        f.ReleasePrepare();
        await Task.WhenAll(f.Next.StopEntered.Task, f.Next.DisposeEntered.Task, f.Next.CompletionObserved.Task).WaitAsync(Guard);
        await f.VideoRun!.WaitAsync(Guard);
        Assert.Equal(0, f.Next.StartCalls);
        Assert.Equal(0, f.Next.ReadCalls);
        Assert.False(f.Next.OriginalCompletion.IsCompleted);
        Assert.False(handling.IsCompleted);
        Assert.False(completion.IsCompleted);
        Assert.False(f.Run.IsCompleted);
        await CatalogLifetimeScenario.AssertAwaitPathAsync(f.Next.OriginalCompletion, f.Run);
        f.Next.ReleaseCleanup();
        Assert.True((await f.Run.WaitAsync(Guard)).Completed);
        Assert.Same(completion, f.Coordinator.Completion);
        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Equal(1, f.Next.StopCalls);
        Assert.Equal(1, f.Next.DisposeCalls);
        Assert.Null(f.Reader.Error);
        Assert.Empty(f.Diagnostics());
        Assert.Single(f.Stream.PostAuthWrites);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Second_Real_Request_While_Prepare_Is_Busy_Fails_Closed_Without_Second_Prepare()
    {
        await using Fixture f = new();
        await f.StartReadyVideoAsync();
        f.Stream.Feed(Frame(f.Request(1).Serialize()));
        await f.PrepareEntered.Task.WaitAsync(Guard);
        Task handling = f.Active;
        await f.Stream.ReadRequested(4).WaitAsync(Guard);
        f.Stream.Feed(Frame(f.Request(2).Serialize()));
        await f.CloseRequired.Task.WaitAsync(Guard);
        await f.Revoked.WaitAsync(Guard);
        await f.Initial.StopEntered.Task.WaitAsync(Guard);
        Assert.Equal(1, f.PrepareCalls);
        Assert.Same(handling, f.Active);
        Assert.False(handling.IsCompleted);
        Assert.False(f.Run!.IsCompleted);
        f.ReleasePrepare();
        Assert.True((await f.Run.WaitAsync(Guard)).Completed);
        await f.VideoRun!.WaitAsync(Guard);
        Assert.Equal(1, f.PrepareCalls);
        Assert.Equal(0, f.Next.StartCalls);
        Assert.Equal(1, f.Next.StopCalls);
        Assert.Equal(1, f.Next.DisposeCalls);
        Assert.Equal("request-display-handler-busy", Assert.IsType<FrameProtocolException>(f.Reader.Error).Reason);
        Assert.Equal("host-catalog-request-reader", Assert.Single(f.Diagnostics()).Message);
        Assert.Null(f.Coordinator.Error);
        Assert.Single(f.Stream.PostAuthWrites);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Prepare_Original_Error_Tree_Is_Reported_Only_In_Auth_Coordinator_Slot()
    {
        await using Fixture f = new();
        await f.StartReadyVideoAsync();
        f.Stream.Feed(Frame(f.Request(1).Serialize()));
        await f.PrepareEntered.Task.WaitAsync(Guard);
        IOException repeated = new("准备原任务重复故障。");
        AggregateException nested = new("准备原嵌套故障。", repeated, new OperationCanceledException("故障态取消。"));
        f.PrepareResult.TrySetException([repeated, nested, repeated]);
        Assert.True((await f.Run!.WaitAsync(Guard)).Completed);
        await f.VideoRun!.WaitAsync(Guard);
        Assert.Null(f.Reader.Error);
        Exception diagnostic = Assert.Single(f.Diagnostics());
        Assert.Equal("host-catalog-request-coordinator", diagnostic.Message);
        Assert.Same(f.Coordinator.Error, diagnostic.InnerException);
        Assert.Equal(3, Tree(diagnostic).Count(error => ReferenceEquals(error, repeated)));
        Assert.Single(Tree(diagnostic), error => ReferenceEquals(error, nested));
        Assert.Contains(Tree(diagnostic), error => error is AggregateException tree && tree.InnerExceptions.Count == 3 &&
            ReferenceEquals(tree.InnerExceptions[0], repeated) && ReferenceEquals(tree.InnerExceptions[1], nested) &&
            ReferenceEquals(tree.InnerExceptions[2], repeated));
        Assert.Equal(1, f.PrepareCalls);
        Assert.Equal(0, f.Next.StartCalls);
        Assert.Single(f.Stream.PostAuthWrites);
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replacement_Zero_Frame_Eof_Or_Failed_Real_Flush_Never_Produces_Control_Reply(bool failFlush)
    {
        await using Fixture f = new();
        await f.StartReadyVideoAsync();
        f.Stream.Feed(Frame(f.Request(1).Serialize()));
        await f.PrepareEntered.Task.WaitAsync(Guard);
        f.ReleasePrepare();
        await f.Next.ReadEntered.Task.WaitAsync(Guard);
        var result = await f.Execution.WaitAsync(Guard);
        Assert.True(result.Transferred && result.ReplacementCompleted);
        var generation = Assert.IsType<DisplaySwitchExecutor.RequestSendReceipt>(result.SendReceipt).Generation;
        if (failFlush)
        {
            f.Next.DeliverFrame();
            await f.Video!.SwitchFlush.Entered.Task.WaitAsync(Guard);
            Assert.False(generation.FirstSend.IsCompleted);
            Assert.Single(f.Stream.PostAuthWrites);
            f.Video.SwitchFlush.Fail(new IOException("真实视频 Flush 原任务失败。"));
        }
        else f.Next.EndRead();
        await f.CloseRequired.Task.WaitAsync(Guard);
        Assert.True((await f.Run!.WaitAsync(Guard)).Completed);
        await f.VideoRun!.WaitAsync(Guard);
        Assert.Null(await generation.FirstSend.WaitAsync(Guard));
        Assert.False(f.Stream.ReplyFlush.Entered.Task.IsCompleted);
        Assert.Single(f.Stream.PostAuthWrites);
        Assert.Equal(failFlush ? 2 : 1, f.Video!.Headers().Length);
        Assert.Null(f.Reader.Error);
        Assert.Equal("request-display-first-send-missing", Assert.IsType<FrameProtocolException>(f.Coordinator.Error).Reason);
        Assert.Equal("host-catalog-request-coordinator", Assert.Single(f.Diagnostics()).Message);
        Assert.Equal(1, f.Next.StopCalls);
        Assert.Equal(1, f.Next.DisposeCalls);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Missing_Writer_Conflicting_Handler_And_Frames_Only_Router_Are_Rejected_Before_IO()
    {
        await using Fixture f = new(bind: false);
        foreach (bool conflictingHandler in new[] { false, true })
        {
            HostDisplayCatalogSendOptions invalid = new(() => ValueTask.FromResult(f.SourceCatalog), CatalogLifetimeScenario.Budget)
            {
                UseSessionWriter = conflictingHandler,
                DisplayRequestPrepare = (_, _) => f.PrepareResult.Task,
                DisplayRequestHandler = conflictingHandler ? (_, _, _, _, _) => Task.CompletedTask : null,
            };
            Assert.Throws<ArgumentException>(() => f.Auth.BindCatalogLifetime(invalid, f.CloseRequired));
            Assert.Throws<ArgumentException>(() => new FirstFrameRouter(f.Context with { DisplayCatalogSend = invalid }, Timeouts, f.Factory));
        }
        Assert.Throws<ArgumentException>(() => new FirstFrameRouter(f.Context, Timeouts, new NoFrames()));
        Assert.Null(f.Run);
        Assert.Null(f.Auth.ControlWriterLifetime);
        Assert.Null(f.OptionalCoordinator);
        Assert.Equal(0, f.Factory.Calls);
        Assert.Equal(0, f.PrepareCalls);
        Assert.Equal(0, f.SourceCalls);
        Assert.Equal(0, f.Stream.ReadCalls);
        Assert.Equal(0, f.Inner.FramedReadCalls);
        Assert.Empty(f.Inner.Writes);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Factory_Router_Without_Prepare_Keeps_Untracked_Source_Ids_And_Original_Holding()
    {
        await using Fixture f = new(withPrepare: false);
        await f.StartReadyVideoAsync();
        Assert.Null(f.OptionalCoordinator);
        Assert.Null(f.OptionalReader);
        Assert.False(Field<bool>(f.Router!, "_enableProducerReplacement"));
        Assert.Null(f.PublishedSource);
        VideoFrameHeader header = Assert.Single(f.Video!.Headers());
        Assert.Equal(f.Initial.Frame.FrameId, header.FrameId);
        Assert.Equal(999UL, header.FrameId);
        Assert.Equal(0, f.PrepareCalls);
        Assert.Equal(1, f.Factory.Calls);
        Assert.False(f.CloseRequired.Task.IsCompleted);
        Assert.Single(f.Stream.PostAuthWrites);
        await f.EndControlAsync();
        Assert.Equal(1, f.Stream.ReadCalls);
        Assert.Equal(1, f.Initial.StopCalls);
        Assert.Equal(1, f.Initial.DisposeCalls);
        Assert.Empty(f.Diagnostics());
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static byte[] Frame(byte[] payload)
    {
        byte[] frame = new byte[TransportConstants.LengthPrefixBytes + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, TransportConstants.LengthPrefixBytes);
        return frame;
    }
    private static ReadOnlySpan<byte> Payload(byte[] frame)
    {
        Assert.Equal((uint)(frame.Length - TransportConstants.LengthPrefixBytes), BinaryPrimitives.ReadUInt32BigEndian(frame));
        return frame.AsSpan(TransportConstants.LengthPrefixBytes);
    }
    private static IEnumerable<Exception> Tree(Exception error)
    {
        yield return error;
        if (error is AggregateException aggregate)
        {
            foreach (Exception child in aggregate.InnerExceptions)
                foreach (Exception descendant in Tree(child)) yield return descendant;
        }
        else if (error.InnerException is { } inner)
            foreach (Exception descendant in Tree(inner)) yield return descendant;
    }
    private static async Task DrainAsync(Task? task)
    {
        if (task is null) return;
        try { await task.ConfigureAwait(false); }
        catch (Exception) { /* 正文断言错误槽；finally 不让一个故障跳过其他原任务。 */ }
    }

    // 最小复制 HostDisplayRequestIntegrationTests 的私有认证/首读夹具；增加真实 Video 路由，移除手工 handler。
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly bool _approval, _blockPreparePrefix, _holdInitialStart;
        private readonly object _producerGate = new();
        private readonly List<Producer> _producers = [];
        private bool _disposing;
        private Task? _closeSupervisor;
        private SessionRegistry.SessionRegistration? _registration;
        private Producer? _initial;
        private ReplaceableVideoFrameSource? _source;
        private int _sourceCalls, _prepareCalls;

        internal Fixture(bool approval = false, bool withPrepare = true, bool bind = true,
            bool blockPreparePrefix = false, bool holdInitialStart = false, bool holdCandidateCleanup = false)
        {
            _approval = approval;
            _blockPreparePrefix = blockPreparePrefix;
            _holdInitialStart = holdInitialStart;
            Next = NewProducer(640, 360, holdCleanup: holdCandidateCleanup);
            Stream = new(Inner);
            Closer = new(new MemoryStream(), new HostLifecycleErrors());
            Closer.Attach(Stream);
            Context = new()
            {
                ServerDeviceId = CatalogLifetimeScenario.ServerId,
                AccessSecretStore = SecretStore,
                FailedAuthLimiter = new FailedAuthLimiter(),
                PendingApprovalLimiter = new ConnectionAdmissionLimiter(3, 1),
                ApprovalGate = Approval,
                SessionRegistry = new SessionRegistry(),
                TimeProvider = Clock,
                Options = new ControlAuthOptions
                {
                    RequireLocalApproval = approval,
                    FailureDelayMin = TimeSpan.Zero,
                    FailureDelayMax = TimeSpan.Zero,
                },
                DisplayCatalogSend = new HostDisplayCatalogSendOptions(ReadCatalog, CatalogLifetimeScenario.Budget)
                {
                    UseSessionWriter = true,
                    DisplayRequestPrepare = withPrepare ? Prepare : null,
                },
            };
            Auth = new(Stream, new ConnectionSecurityContext(IPAddress.Loopback, IPAddress.Loopback, 12345,
                SslProtocols.None, new byte[CertificatePin.LengthBytes]), Context, Timeouts);
            Factory = new(this);
            if (bind) Auth.BindCatalogLifetime(Context.DisplayCatalogSend!, CloseRequired);
        }

        internal CatalogControlledSslStream Inner { get; } = new(hello: false, holdClose: false);
        internal RequestInputStream Stream { get; }
        internal CatalogApproval Approval { get; } = new();
        internal CatalogSecretStore SecretStore { get; } = new();
        internal ManualDeadlineClock Clock { get; } = new();
        internal CancellationTokenSource HostStop { get; } = new();
        internal TaskCompletionSource CloseRequired { get; } = Signal();
        internal TaskCompletionSource SourceEntered { get; } = Signal();
        internal TaskCompletionSource<DisplayCatalogFrame> SourceResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource PrepareEntered { get; } = Signal();
        internal TaskCompletionSource<IVideoFrameProducer> PrepareResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<IVideoFrameProducer>? PrepareOperation { get; private set; }
        internal Pause PreparePause { get; } = new();
        internal DisplayInfo? PreparedDisplay { get; private set; }
        internal CancellationToken PrepareToken { get; private set; }
        internal DisplayCatalogFrame SourceCatalog { get; } = new(37, 12,
        [
            new DisplayInfo(new DisplayId(3), "主显示器", 0, 0, 1920, 1080, true),
            new DisplayInfo(new DisplayId(17), "左侧显示器", -1280, -100, 1280, 1024, false),
        ]);
        internal ControlAuthContext Context { get; }
        internal ControlAuthSession Auth { get; }
        internal ConnectionCloseHandle Closer { get; }
        internal InitialFactory Factory { get; }
        internal Producer Next { get; }
        internal Producer Initial => Assert.IsType<Producer>(_initial);
        internal Task<ControlAuthResult>? Run { get; private set; }
        internal FirstFrameRouter? Router { get; private set; }
        internal VideoInputStream? Video { get; private set; }
        internal ConnectionCloseHandle? VideoCloser { get; private set; }
        internal Task? VideoRun { get; private set; }
        internal Task Revoked { get; private set; } = Task.CompletedTask;
        internal int SourceCalls => Volatile.Read(ref _sourceCalls);
        internal int PrepareCalls => Volatile.Read(ref _prepareCalls);
        internal SessionRegistry.SessionRegistration Registration => Assert.IsType<SessionRegistry.SessionRegistration>(_registration);
        internal HostControlWriterLifetime Lifetime => Assert.IsType<HostControlWriterLifetime>(Auth.ControlWriterLifetime);
        internal HostDisplayRequestCoordinator? OptionalCoordinator => Field<HostDisplayRequestCoordinator?>(Auth, "_requestCoordinator");
        internal HostDisplayRequestCoordinator Coordinator => Assert.IsType<HostDisplayRequestCoordinator>(OptionalCoordinator);
        internal HostDisplayRequestReader? OptionalReader => Field<HostDisplayRequestReader?>(Auth, "_requestReader");
        internal HostDisplayRequestReader Reader => Assert.IsType<HostDisplayRequestReader>(OptionalReader);
        internal Task Active { get { lock (Field<object>(Coordinator, "_gate")) return Field<Task>(Coordinator, "_active"); } }
        internal Task HandlerWorker { get { lock (Field<object>(Reader, "_gate")) return Field<Task>(Reader, "_handlerWorker"); } }
        internal Task<DisplaySwitchExecutor.ExecutionResult> Execution
        {
            get
            {
                DisplaySwitchExecutor executor;
                lock (Field<object>(Coordinator, "_gate")) executor = Field<DisplaySwitchExecutor>(Coordinator, "_executor");
                lock (Field<object>(Context.SessionRegistry, "_gate"))
                    return Field<Task<DisplaySwitchExecutor.ExecutionResult>>(executor, "_active");
            }
        }
        internal ReplaceableVideoFrameSource? PublishedSource
        {
            get
            {
                lock (Field<object>(Context.SessionRegistry, "_gate"))
                {
                    object entry = Field<object>(Registration, "_entry");
                    return (ReplaceableVideoFrameSource?)entry.GetType().GetProperty("VideoSource")!.GetValue(entry);
                }
            }
        }

        internal void Start()
        {
            Assert.Null(Run);
            _closeSupervisor = Task.Run(async () =>
            {
                await CloseRequired.Task.ConfigureAwait(false);
                await Closer.CloseAsync().ConfigureAwait(false);
            });
            Run = Auth.RunAsync(HostStop.Token);
        }
        private ValueTask<DisplayCatalogFrame> ReadCatalog()
        {
            Assert.Equal(1, Interlocked.Increment(ref _sourceCalls));
            Assert.True(Inner.SuccessWrite.Operation!.IsCompletedSuccessfully);
            Assert.True(Inner.SuccessFlush.Operation!.IsCompletedSuccessfully);
            Assert.Equal(1, Context.SessionRegistry.ActiveSessionCount);
            Assert.Equal(1, Stream.ReadCalls);
            Assert.All(SecretStore.ReturnedKey!, value => Assert.Equal((byte)0, value));
            SourceEntered.TrySetResult();
            return new(SourceResult.Task);
        }
        private Task<IVideoFrameProducer> Prepare(DisplayInfo display, CancellationToken token)
        {
            Interlocked.Increment(ref _prepareCalls);
            PreparedDisplay = display;
            PrepareToken = token;
            PrepareEntered.TrySetResult();
            if (_blockPreparePrefix) PreparePause.Block();
            return PrepareOperation = PrepareResult.Task;
        }
        internal void ReleasePrepare() => PrepareResult.TrySetResult(Next);
        internal DisplaySwitchRequestFrame Request(long sequence) =>
            new(Auth.SessionId, sequence, 1234, Lifetime.Catalog!.Sequence, SourceCatalog.Displays[1].Id);
        internal async Task AuthenticateAsync()
        {
            Task? approvalRead = null;
            if (_approval)
            {
                await Approval.Entered.Task.WaitAsync(Guard);
                approvalRead = Assert.Single(Stream.Reads).Original.Task;
                Assert.False(approvalRead.IsCompleted);
                Assert.Equal(1, Context.PendingApprovalLimiter.GlobalInUse);
                Approval.Release();
            }
            await Inner.SuccessWrite.Entered.Task.WaitAsync(Guard);
            Assert.Null(Auth.ControlWriterLifetime);
            Assert.Equal(0, SourceCalls);
            Inner.SuccessWrite.Release();
            await Inner.SuccessFlush.Entered.Task.WaitAsync(Guard);
            Assert.Equal(0, Context.SessionRegistry.ActiveSessionCount);
            Inner.SuccessFlush.Release();
            await SourceEntered.Task.WaitAsync(Guard);
            _registration = Assert.IsType<SessionRegistry.SessionRegistration>(
                CatalogLifetimeScenario.ReadLocal(Run!, typeof(ControlAuthSession), "registration"));
            object entry = Field<object>(Registration, "_entry");
            Revoked = Assert.IsType<TaskCompletionSource>(entry.GetType().GetProperty("Revoked")!.GetValue(entry)).Task;
            Assert.Equal(1, Stream.ReadCalls);
            Assert.Equal(0, Context.PendingApprovalLimiter.GlobalInUse);
            if (approvalRead is not null) Assert.Same(approvalRead, Assert.Single(Stream.Reads).Original.Task);
        }
        internal async Task MakeReadyAsync()
        {
            SourceResult.TrySetResult(SourceCatalog);
            await Inner.CatalogWrite.Entered.Task.WaitAsync(Guard);
            Inner.CatalogWrite.Release();
            await Inner.CatalogFlush.Entered.Task.WaitAsync(Guard);
            Inner.CatalogFlush.Release();
            Assert.IsType<HostControlWriter>(await Lifetime.Ready.WaitAsync(Guard));
        }
        internal void AttachVideo(ulong? firstFrameId = null)
        {
            Assert.Null(VideoRun);
            Assert.Equal(Auth.SessionId, Assert.Single(Context.SessionRegistry.Snapshot()).SessionId);
            ConnectionSecurityContext security = new(IPAddress.Loopback, IPAddress.Loopback, 12346,
                SslProtocols.None, Auth.Security.ServerCertificateSha256.Span);
            byte[] nonce = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
            AuthSuccessFrame success = Assert.IsType<AuthSuccessFrame>(Inner.WireSuccess);
            // 复用 CatalogLifetimeScenario.AttachVideo 的独立 transcript；token 来自真实 auth_success。
            byte[] transcript = [.. Encoding.UTF8.GetBytes("LANREMOTE-VIDEO-V1\0"),
                .. Convert.FromHexString(Auth.SessionId.ToString("N")), .. nonce, .. security.ServerCertificateSha256.ToArray()];
            byte[] proof = HMACSHA256.HashData(success.SessionToken.Span, transcript);
            try { Video = new(Frame(new VideoHelloFrame(Auth.SessionId, nonce, proof).Serialize())); }
            finally { CryptographicOperations.ZeroMemory(proof); success.ClearSessionToken(); }
            // 与 BoundaryTests.Fixture 一致：真实 closeAuthority，不使用 public record 伪造关闭权限。
            VideoCloser = new(new MemoryStream(), new HostLifecycleErrors());
            VideoCloser.Attach(Video);
            AcceptedConnection connection = new(security, Video, VideoCloser);
            Assert.True(connection.HasCloseAuthority);
            Router = new(Context, Timeouts, Factory, firstFrameId: firstFrameId);
            VideoRun = Router.RunAsync(connection, HostStop.Token);
        }
        internal async Task AttachAndWaitAsync(ulong? firstFrameId = null)
        {
            AttachVideo(firstFrameId);
            await Factory.Entered.Task.WaitAsync(Guard);
            await Initial.ReadWaiting.Task.WaitAsync(Guard);
            Assert.Equal(VideoAttachStatus.Attached, Router!.AttachStatus);
            Assert.Equal(1, Router.AttachAttempts);
            Assert.True(VideoAttachAckFrame.TryParse(Payload(Video!.Writes.First()), out var ack, out _));
            Assert.Equal(Auth.SessionId, ack!.SessionId);
            _source = PublishedSource;
            if (Context.DisplayCatalogSend!.DisplayRequestPrepare is not null)
                Assert.Equal(Auth.SessionId, Assert.IsType<ReplaceableVideoFrameSource>(_source).SessionId);
        }
        internal async Task StartReadyVideoAsync()
        {
            Start();
            await AuthenticateAsync();
            await MakeReadyAsync();
            await AttachAndWaitAsync();
        }
        internal async Task EndControlAsync()
        {
            Stream.CompleteInput();
            Assert.True((await Run!.WaitAsync(Guard)).Completed);
            if (VideoRun is not null) await VideoRun.WaitAsync(Guard);
            Assert.Equal(0, Context.SessionRegistry.ActiveSessionCount);
            Assert.True(Registration.VideoCompletion.IsCompletedSuccessfully);
        }
        internal DisplayChangedFrame Changed()
        {
            byte[][] writes = Stream.PostAuthWrites.ToArray();
            Assert.Equal(2, writes.Length);
            Assert.True(DisplayChangedFrame.TryParse(Payload(writes[1]), Auth.SessionId, out var changed, out _));
            return changed!;
        }
        internal List<Exception> Diagnostics() { List<Exception> errors = []; Auth.AddCatalogErrors(errors); return errors; }
        private Producer NewProducer(int width, int height, bool holdStart = false, bool holdCleanup = false)
        {
            lock (_producerGate)
            {
                Producer producer = new(width, height, holdStart, holdCleanup);
                _producers.Add(producer);
                if (_disposing) producer.ReleaseAll();
                return producer;
            }
        }
        public async ValueTask DisposeAsync()
        {
            // await using 即各测试的 finally：先放全部原闸，再发独立停止，最后逐项 join 原根。
            SourceResult.TrySetResult(SourceCatalog);
            ReleasePrepare();
            PreparePause.Open();
            Inner.ReleaseAll();
            Stream.ReplyFlush.Release();
            Stream.CompleteInput();
            Video?.SwitchFlush.Release();
            Approval.Release();
            lock (_producerGate)
            {
                _disposing = true;
                foreach (Producer producer in _producers) producer.ReleaseAll();
            }
            CloseRequired.TrySetResult();
            Task cancel = HostStop.CancelAsync();
            Task close = Closer.CloseAsync();
            Task? videoClose = VideoCloser?.CloseAsync();
            await DrainAsync(cancel);
            await DrainAsync(close);
            await DrainAsync(videoClose);
            await DrainAsync(Run);
            await DrainAsync(VideoRun); // 原 Router 根包含 setup/sender/source/peer/close，不等 FirstSend 结果承诺代替它。
            await DrainAsync(_source?.Completion);
            await DrainAsync(OptionalCoordinator?.Completion);
            await DrainAsync(OptionalReader?.Completion);
            await DrainAsync(Auth.ControlWriterLifetime?.Completion);
            await DrainAsync(Auth.JoinOwnedOperationsAsync());
            await DrainAsync(_closeSupervisor);
            await DrainAsync(PrepareOperation);
            await DrainAsync(SourceResult.Task);
            await DrainAsync(Approval.Operation);
            await DrainAsync(Factory.Operation);
            foreach (ReadProbe read in Stream.Reads) await DrainAsync(read.Original.Task);
            foreach (Task operation in Inner.Operations.Concat(Stream.Operations)) await DrainAsync(operation);
            if (Video is not null)
                foreach (Task operation in Video.Operations) await DrainAsync(operation);
            // 未交付的测试候选仍归夹具；已交付者此前必须由生产路径停止/释放，正文会核对次数。
            foreach (Producer producer in _producers)
            {
                if (producer.StopCalls == 0) await DrainAsync(producer.StopAsync());
                if (producer.DisposeCalls == 0) await DrainAsync(producer.DisposeAsync().AsTask());
                await DrainAsync(producer.OriginalCompletion);
                foreach (Task operation in producer.Operations) await DrainAsync(operation);
                producer.Frame.Dispose();
                producer.StartPause.Dispose();
            }
            Inner.WireSuccess?.ClearSessionToken();
            PreparePause.Dispose();
            HostStop.Dispose();
            Assert.Empty(Closer.CleanupErrors);
            if (VideoCloser is not null) Assert.Empty(VideoCloser.CleanupErrors);
            Assert.Equal(0, Clock.TimerCount);
        }

        internal sealed class InitialFactory(Fixture owner) : IVideoFrameProducerFactory
        {
            private int _calls;
            internal int Calls => Volatile.Read(ref _calls);
            internal TaskCompletionSource Entered { get; } = Signal();
            internal Task<IVideoFrameProducer>? Operation { get; private set; }
            public ValueTask<IVideoFrameProducer> CreateAsync(Guid sessionId, CancellationToken cancellationToken)
            {
                Assert.Equal(owner.Auth.SessionId, sessionId);
                Assert.Equal(1, Interlocked.Increment(ref _calls));
                Assert.Equal(1, owner.Video!.FlushCalls); // 真实 ACK 完整 Flush 后才进入外部工厂。
                Producer producer = owner.NewProducer(1920, 1080, holdStart: owner._holdInitialStart);
                owner._initial = producer;
                producer.DeliverFrame();
                Operation = Task.FromResult<IVideoFrameProducer>(producer);
                Entered.TrySetResult();
                return new(Operation);
            }
        }
    }

    private sealed class Pause : IDisposable
    {
        private readonly ManualResetEventSlim _open = new();
        internal TaskCompletionSource Entered { get; } = Signal();
        internal void Block() { Entered.TrySetResult(); _open.Wait(); }
        internal void Open() => _open.Set();
        public void Dispose() => _open.Dispose();
    }

    private sealed class Producer : IVideoFrameProducer
    {
        private readonly TaskCompletionSource _stop = Signal(), _cleanup = Signal();
        private readonly TaskCompletionSource<EncodedFrame?> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<EncodedFrame?> _following = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _starts, _reads, _stops, _disposes;
        internal Producer(int width, int height, bool holdStart, bool holdCleanup)
        {
            RecordingVideoOwner owner = new(32);
            VideoFrameTestData.Payload().CopyTo(owner.Bytes, 0);
            Frame = new(VideoCodec.Jpeg, width, height, 999, 1234, 60, owner, 5);
            if (!holdStart) StartPause.Open();
            if (!holdCleanup) ReleaseCleanup();
            OriginalCompletion = CompleteAsync();
        }
        internal EncodedFrame Frame { get; }
        internal Pause StartPause { get; } = new();
        internal TaskCompletionSource ReadEntered { get; } = Signal();
        internal TaskCompletionSource ReadWaiting { get; } = Signal();
        internal TaskCompletionSource StopEntered { get; } = Signal();
        internal TaskCompletionSource DisposeEntered { get; } = Signal();
        internal TaskCompletionSource CompletionObserved { get; } = Signal();
        internal ConcurrentQueue<Task> Operations { get; } = new();
        internal Task OriginalCompletion { get; }
        internal int StartCalls => Volatile.Read(ref _starts);
        internal int ReadCalls => Volatile.Read(ref _reads);
        internal int StopCalls => Volatile.Read(ref _stops);
        internal int DisposeCalls => Volatile.Read(ref _disposes);
        public Task Completion { get { CompletionObserved.TrySetResult(); return OriginalCompletion; } }
        public void Start() { Interlocked.Increment(ref _starts); StartPause.Block(); }
        public ValueTask<EncodedFrame?> ReadNextAsync(CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _reads);
            Task<EncodedFrame?> original = call == 1 ? _first.Task : _following.Task;
            Operations.Enqueue(original);
            (call == 1 ? ReadEntered : ReadWaiting).TrySetResult();
            return new(original);
        }
        internal void DeliverFrame() => _first.TrySetResult(Frame);
        internal void EndRead() { _first.TrySetResult(null); _following.TrySetResult(null); }
        internal void ReleaseCleanup() => _cleanup.TrySetResult();
        internal void ReleaseAll() { StartPause.Open(); EndRead(); ReleaseCleanup(); }
        private void RequestStop() { StartPause.Open(); EndRead(); _stop.TrySetResult(); }
        public Task StopAsync()
        {
            Interlocked.Increment(ref _stops);
            RequestStop();
            StopEntered.TrySetResult();
            return OriginalCompletion;
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposes);
            RequestStop();
            DisposeEntered.TrySetResult();
            return new(OriginalCompletion);
        }
        private async Task CompleteAsync()
        {
            await _stop.Task.ConfigureAwait(false);
            await _cleanup.Task.ConfigureAwait(false);
        }
    }

    private sealed class ReadProbe(Memory<byte> buffer)
    {
        internal Memory<byte> Buffer { get; } = buffer;
        internal TaskCompletionSource<int> Original { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal byte[] Delivered { get; set; } = [];
    }

    // 原私有夹具只接管一字节审批/holding 读及后继请求；Challenge/Offer/Accept/proof 均委托现有夹具。
    private sealed class RequestInputStream(CatalogControlledSslStream inner) : SslStream(new MemoryStream())
    {
        private readonly object _gate = new();
        private readonly Queue<byte> _input = new();
        private readonly Queue<ReadProbe> _pending = new();
        private readonly Dictionary<int, TaskCompletionSource> _readRequested = [];
        private bool _requestInput, _eof, _reply;
        private int _calls, _active, _maximum;
        internal ConcurrentQueue<ReadProbe> Reads { get; } = new();
        internal ConcurrentQueue<byte[]> PostAuthWrites { get; } = new();
        internal ConcurrentQueue<Task> Operations { get; } = new();
        internal CatalogOperationGate ReplyFlush { get; } = new();
        internal int ReadCalls { get { lock (_gate) return _calls; } }
        internal int MaxConcurrentReads { get { lock (_gate) return _maximum; } }
        internal Task ReadRequested(int call)
        {
            lock (_gate)
            {
                if (_calls >= call) return Task.CompletedTask;
                if (!_readRequested.TryGetValue(call, out TaskCompletionSource? signal))
                    _readRequested.Add(call, signal = Signal());
                return signal.Task;
            }
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_requestInput && buffer.Length != 1) return inner.ReadAsync(buffer, cancellationToken);
            lock (_gate)
            {
                _requestInput = true;
                ReadProbe read = new(buffer);
                Reads.Enqueue(read);
                _pending.Enqueue(read);
                _calls++;
                _maximum = Math.Max(_maximum, ++_active);
                if (_readRequested.Remove(_calls, out TaskCompletionSource? signal)) signal.TrySetResult();
                Deliver();
                return new(read.Original.Task);
            }
        }
        internal void Feed(byte[] bytes)
        {
            lock (_gate)
            {
                Assert.False(_eof);
                foreach (byte value in bytes) _input.Enqueue(value);
                Deliver();
            }
        }
        internal void CompleteInput() { lock (_gate) { _eof = true; Deliver(); } }
        private void Deliver()
        {
            while (_pending.Count != 0 && (_input.Count != 0 || _eof))
            {
                ReadProbe read = _pending.Dequeue();
                _active--;
                int count = Math.Min(read.Buffer.Length, _input.Count);
                for (int index = 0; index < count; index++) read.Buffer.Span[index] = _input.Dequeue();
                read.Delivered = read.Buffer[..count].ToArray();
                read.Original.TrySetResult(count);
            }
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using JsonDocument document = JsonDocument.Parse(buffer[TransportConstants.LengthPrefixBytes..]);
            string? type = document.RootElement.GetProperty("type").GetString();
            _reply = type == DisplayChangedFrame.ExpectedType;
            if (type is DisplayCatalogFrame.ExpectedType or DisplayChangedFrame.ExpectedType)
                PostAuthWrites.Enqueue(buffer.ToArray());
            return inner.WriteAsync(buffer, cancellationToken);
        }
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            if (!_reply) return inner.FlushAsync(cancellationToken);
            Task original = ReplyFlush.Enter();
            Operations.Enqueue(original);
            return original;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { CompleteInput(); inner.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private sealed class VideoInputStream(byte[] wire) : SslStream(new MemoryStream())
    {
        private readonly TaskCompletionSource<int> _peer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _offset, _flushes;
        internal ConcurrentQueue<byte[]> Writes { get; } = new();
        internal ConcurrentQueue<Task> Operations { get; } = new();
        internal CatalogOperationGate SwitchFlush { get; } = new();
        internal TaskCompletionSource Closed { get; } = Signal();
        internal int FlushCalls => Volatile.Read(ref _flushes);
        internal VideoFrameHeader[] Headers() => Writes
            .Where(bytes => bytes.Length == VideoFrameHeader.Size && bytes.AsSpan(0, 4).SequenceEqual("LRVF"u8))
            .Select(bytes => VideoFrameHeader.Read(bytes)).ToArray();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_offset < wire.Length)
            {
                int count = Math.Min(buffer.Length, wire.Length - _offset);
                wire.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return ValueTask.FromResult(count);
            }
            Operations.Enqueue(_peer.Task);
            return new(_peer.Task);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes.Enqueue(buffer.ToArray());
            return ValueTask.CompletedTask;
        }
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 第一次是附着 ACK，第二次是初代帧，第三次才是切屏候选帧。
            Task original = Interlocked.Increment(ref _flushes) == 3 ? SwitchFlush.Enter() : Task.CompletedTask;
            Operations.Enqueue(original);
            return original;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _peer.TrySetResult(0);
            try { base.Dispose(disposing); }
            finally { if (disposing) Closed.TrySetResult(); }
        }
    }

    private sealed class NoFrames : IVideoFrameSource
    {
        public ValueTask<EncodedFrame?> ReadNextAsync(Guid sessionId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("无效 frames-only 配置不得进入读取。");
    }
}
