using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

public sealed class HostDisplayRequestIntegrationTests
{
    private const int TestTimeout = 60_000;
    private static readonly TimeSpan Guard = CatalogLifetimeScenario.Guard;

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_Auth_Reuses_First_Read_And_Dispatches_Two_Requests_With_Actual_Catalog(bool approval)
    {
        await using Fixture f = new(requireApproval: approval);
        ConcurrentQueue<Task> replies = new();
        TaskCompletionSource first = Signal(), second = Signal();
        f.Handler = call =>
        {
            // 仅验证同一写者的写入适配；这些固定尺寸不代表真实 Video 首帧发送完成。
            Task original = call.Writer.WriteDisplayChangedAsync(new(call.Request.SessionId, 99, 12,
                call.Request.Sequence, call.Request.CatalogSequence, call.Request.DisplayId, 70, 640, 360), call.Token);
            replies.Enqueue(original);
            (call.Request.Sequence == 9 ? first : second).TrySetResult();
            return original;
        };
        f.Start();
        await f.AuthenticateAsync();
        Task originalFirstRead = Assert.Single(f.Stream.Reads).Original.Task;
        Task holding = f.Holding;
        Assert.Same(holding, f.Reader.Completion);
        Assert.False(originalFirstRead.IsCompleted);
        Assert.Equal(1, f.Stream.ReadCalls);
        Assert.Equal(0, f.Inner.HoldingCalls);
        Assert.False(f.Lifetime.Ready.IsCompleted);
        HostControlWriter writer = await f.MakeReadyAsync();
        DisplayCatalogFrame catalog = Assert.IsType<DisplayCatalogFrame>(f.Lifetime.Catalog);
        Assert.NotSame(f.SourceCatalog, catalog);
        Assert.Equal(0, catalog.Sequence);
        Assert.Equal(f.SourceCatalog.Displays, catalog.Displays);
        Assert.True(DisplayCatalogFrame.TryParse(Payload(Assert.Single(f.Stream.PostAuthWrites)), out var sent, out _));
        Assert.Equal(catalog.Sequence, sent!.Sequence);
        Assert.Equal(catalog.Displays, sent.Displays);

        byte[] firstWire = Frame(f.Request(9, catalog).Serialize());
        byte[] secondWire = Frame(f.Request(10, catalog).Serialize());
        f.Stream.Feed(firstWire);
        await first.Task.WaitAsync(Guard);
        await Assert.Single(replies).WaitAsync(Guard);
        await f.HandlerWorker.WaitAsync(Guard);
        await f.Stream.ReadRequested(4).WaitAsync(Guard);
        Assert.False(f.CloseRequired.Task.IsCompleted);
        f.Stream.Feed(secondWire);
        await second.Task.WaitAsync(Guard);
        await replies.Last().WaitAsync(Guard);
        await f.HandlerWorker.WaitAsync(Guard);
        await f.Stream.ReadRequested(7).WaitAsync(Guard);

        Invocation[] calls = f.Calls.ToArray();
        Assert.Equal(new long[] { 9, 10 }, calls.Select(call => call.Request.Sequence));
        Assert.All(calls, call =>
        {
            Assert.Same(f.Registration, call.Registration);
            Assert.True(call.Registration.CheckCurrent());
            Assert.Same(writer, call.Writer);
            Assert.Same(catalog, call.Catalog);
            Assert.Equal(f.Auth.SessionId, call.Request.SessionId);
            Assert.Equal(catalog.Sequence, call.Request.CatalogSequence);
            Assert.Equal(catalog.Displays[1].Id, call.Request.DisplayId);
            Assert.Equal(1234, call.Request.TimestampMs);
            Assert.False(call.Token.IsCancellationRequested);
        });
        byte[][] writes = f.Stream.PostAuthWrites.ToArray();
        Assert.Equal(3, writes.Length);
        for (int index = 1; index < writes.Length; index++)
        {
            Assert.True(DisplayChangedFrame.TryParse(Payload(writes[index]), f.Auth.SessionId, out var reply, out _));
            Assert.Equal(index, reply!.Sequence);
            Assert.Equal(calls[index - 1].Request.Sequence, reply.RequestSequence);
            Assert.Equal(catalog.Sequence, reply.CatalogSequence);
            Assert.Equal(catalog.Displays[1].Id, reply.DisplayId);
        }
        Assert.Equal(new[] { 1, 3, firstWire.Length - 4, 1, 3, secondWire.Length - 4, 1 },
            f.Stream.Reads.Select(read => read.Buffer.Length));
        Assert.Equal(firstWire.Concat(secondWire), f.Stream.Reads.SelectMany(read => read.Delivered));
        Assert.Equal(new byte[] { 0 }, f.Stream.Reads.First().Delivered);
        Assert.Same(originalFirstRead, f.Stream.Reads.First().Original.Task);
        Assert.Same(holding, f.Holding);
        Assert.Equal(1, f.Stream.MaxConcurrentReads);
        Assert.Equal(1, f.SourceCalls);
        f.Stream.CompleteInput();
        Assert.True((await f.Run!.WaitAsync(Guard)).Completed);
        Assert.Equal(0, f.Context.SessionRegistry.ActiveSessionCount);
        Assert.Null(f.Reader.Error);
        Assert.Empty(f.Diagnostics());
        Assert.Equal(0, f.Context.PendingApprovalLimiter.GlobalInUse);
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Eof_Revokes_Parent_Before_Joining_Handler_Prefix_And_Original_Task(bool blockPrefix)
    {
        await using Fixture f = new();
        Pause prefix = f.NewPause();
        TaskCompletionSource entered = Signal(), original = f.NewHandlerGate();
        f.Handler = _ => { entered.TrySetResult(); if (blockPrefix) prefix.Block(); return original.Task; };
        f.Start();
        await f.AuthenticateAsync();
        HostControlWriter writer = await f.MakeReadyAsync();
        f.Stream.Feed(Frame(f.Request(1).Serialize()));
        await entered.Task.WaitAsync(Guard);
        if (blockPrefix) await prefix.Entered.Task.WaitAsync(Guard);
        await f.Stream.ReadRequested(4).WaitAsync(Guard);
        Task idleRead = f.Stream.Reads.Last().Original.Task;
        f.Stream.CompleteInput();
        await f.CloseRequired.Task.WaitAsync(Guard);
        await f.Revoked.WaitAsync(Guard);
        await f.Inner.DisposeExited.Task.WaitAsync(Guard);
        await f.Lifetime.StopRequired.WaitAsync(Guard);
        Assert.True(idleRead.IsCompletedSuccessfully);
        Assert.False(f.Registration.CheckCurrent());
        Assert.Equal(0, f.Context.SessionRegistry.ActiveSessionCount);
        Assert.False(original.Task.IsCompleted);
        Assert.False(f.Holding.IsCompleted);
        Assert.False(f.Run!.IsCompleted);
        Assert.False(f.Lifetime.Completion!.IsCompleted);
        Assert.Same(writer, await f.Lifetime.Ready);
        prefix.Open();
        await CatalogLifetimeScenario.AssertAwaitPathAsync(original.Task, f.Holding);
        await CatalogLifetimeScenario.AssertAwaitPathAsync(original.Task, f.Run);

        IOException repeated = new("处理器原重复异常。");
        AggregateException nested = new("处理器原嵌套异常。", new OperationCanceledException("故障态取消。"), repeated);
        original.TrySetException([repeated, nested, repeated]);
        Assert.True((await f.Run.WaitAsync(Guard)).Completed);
        Exception diagnostic = Assert.Single(f.Diagnostics());
        Assert.Equal("host-catalog-request-reader", diagnostic.Message);
        Assert.Same(f.Reader.Error, diagnostic.InnerException);
        Assert.Contains(ExceptionTree(diagnostic), error => error is AggregateException tree &&
            tree.InnerExceptions.Count == 3 && ReferenceEquals(repeated, tree.InnerExceptions[0]) &&
            ReferenceEquals(nested, tree.InnerExceptions[1]) && ReferenceEquals(repeated, tree.InnerExceptions[2]));
        Assert.Single(f.Calls);
        Assert.Equal(1, f.Stream.MaxConcurrentReads);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Request_Before_Catalog_Ready_Fails_Closed_Without_Waiting_For_Blocked_Source()
    {
        await using Fixture f = new();
        f.Start();
        await f.AuthenticateAsync();
        f.Stream.Feed(Frame(f.Request(1).Serialize()));
        await f.CloseRequired.Task.WaitAsync(Guard);
        await f.Revoked.WaitAsync(Guard);
        await f.Lifetime.StopRequired.WaitAsync(Guard);
        Assert.Empty(f.Calls);
        Assert.Null(await f.Lifetime.Ready.WaitAsync(Guard));
        Assert.Null(f.Lifetime.Catalog);
        Assert.False(f.SourceResult.Task.IsCompleted);
        Assert.False(f.Lifetime.Completion!.IsCompleted);
        Assert.False(f.Run!.IsCompleted);
        Assert.Equal(0, f.Context.SessionRegistry.ActiveSessionCount);
        Assert.Empty(f.Stream.PostAuthWrites);
        f.SourceResult.TrySetResult(f.SourceCatalog);
        Assert.True((await f.Run.WaitAsync(Guard)).Completed);
        Assert.Empty(f.Calls);
        Assert.Null(await f.Lifetime.Ready);
        Assert.Equal("host-catalog-request-reader", Assert.Single(f.Diagnostics()).Message);
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("unknown")]
    [InlineData("json")]
    [InlineData("session")]
    public async Task Unknown_Malformed_And_Wrong_Session_Frames_Never_Invoke_Handler(string kind)
    {
        await using Fixture f = new();
        f.Start();
        await f.AuthenticateAsync();
        await f.MakeReadyAsync();
        string payload = Encoding.UTF8.GetString(f.Request(1).Serialize());
        payload = kind switch
        {
            "unknown" => payload.Replace("request_display", "unknown_control"),
            "json" => "{",
            _ => payload.Replace(f.Auth.SessionId.ToString("D"), Guid.Empty.ToString("D")),
        };
        f.Stream.Feed(Frame(Encoding.UTF8.GetBytes(payload)));
        await f.CloseRequired.Task.WaitAsync(Guard);
        Assert.True((await f.Run!.WaitAsync(Guard)).Completed);
        Assert.Empty(f.Calls);
        Assert.IsType<FrameProtocolException>(f.Reader.Error);
        Exception diagnostic = Assert.Single(f.Diagnostics());
        Assert.Equal("host-catalog-request-reader", diagnostic.Message);
        Assert.Same(f.Reader.Error, diagnostic.InnerException);
        Assert.Equal(0, f.Context.SessionRegistry.ActiveSessionCount);
        Assert.Equal(1, f.Stream.MaxConcurrentReads);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Configured_Handler_Without_Session_Writer_Is_Rejected_At_Bind_Before_IO()
    {
        await using Fixture f = new(bind: false);
        HostDisplayCatalogSendOptions options = new(() => ValueTask.FromResult(f.SourceCatalog), CatalogLifetimeScenario.Budget)
        {
            UseSessionWriter = false,
            DisplayRequestHandler = (_, _, _, _, _) => Task.CompletedTask,
        };
        Assert.Throws<ArgumentException>(() => f.Auth.BindCatalogLifetime(options, f.CloseRequired));
        Assert.Null(f.Run);
        Assert.Null(f.Auth.ControlWriterLifetime);
        Assert.Equal(0, f.Stream.ReadCalls);
        Assert.Equal(0, f.Inner.FramedReadCalls);
        Assert.Equal(0, f.SourceCalls);
        Assert.Empty(f.Inner.Writes);
        Assert.Empty(f.Calls);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Null_Handler_Keeps_Original_Holding_And_Extra_Byte_Ends_Session()
    {
        await using Fixture f = new(withHandler: false);
        f.Start();
        await f.AuthenticateAsync();
        await f.MakeReadyAsync();
        Assert.Null(f.OptionalReader);
        f.Stream.Feed([0]);
        Assert.True((await f.Run!.WaitAsync(Guard)).Completed);
        Assert.True(f.CloseRequired.Task.IsCompletedSuccessfully);
        Assert.Equal(1, f.Stream.ReadCalls);
        Assert.Equal(new byte[] { 0 }, Assert.Single(f.Stream.Reads).Delivered);
        Assert.Empty(f.Calls);
        Assert.Empty(f.Diagnostics());
        Assert.Equal(0, f.Context.SessionRegistry.ActiveSessionCount);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Second_Valid_Frame_While_Handler_Busy_Fails_Closed_Without_Queueing()
    {
        await using Fixture f = new();
        TaskCompletionSource entered = Signal(), secondEntered = Signal(), original = f.NewHandlerGate();
        f.Handler = _ => { (f.Calls.Count == 1 ? entered : secondEntered).TrySetResult(); return original.Task; };
        f.Start();
        await f.AuthenticateAsync();
        await f.MakeReadyAsync();
        f.Stream.Feed(Frame(f.Request(1).Serialize()));
        await entered.Task.WaitAsync(Guard);
        await f.Stream.ReadRequested(4).WaitAsync(Guard);
        f.Stream.Feed(Frame(f.Request(2).Serialize()));
        await Task.WhenAny(f.CloseRequired.Task, secondEntered.Task).WaitAsync(Guard);
        Assert.False(secondEntered.Task.IsCompleted);
        await f.CloseRequired.Task.WaitAsync(Guard);
        await f.Revoked.WaitAsync(Guard);
        Assert.Single(f.Calls);
        Assert.False(original.Task.IsCompleted);
        Assert.False(f.Run!.IsCompleted);
        Assert.False(f.Holding.IsCompleted);
        original.TrySetResult();
        Assert.True((await f.Run.WaitAsync(Guard)).Completed);
        Assert.Single(f.Calls);
        Assert.Equal(1, f.Calls.Single().Request.Sequence);
        Assert.IsType<FrameProtocolException>(f.Reader.Error);
        Assert.Equal("host-catalog-request-reader", Assert.Single(f.Diagnostics()).Message);
        Assert.Equal(1, f.Stream.MaxConcurrentReads);
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Original_First_Read_Error_Tree_Is_Reported_In_One_Reader_Slot(bool siblings)
    {
        await using Fixture f = new();
        f.Start();
        await f.AuthenticateAsync();
        await f.MakeReadyAsync();
        IOException repeated = new("首读原异常。");
        AggregateException nested = new("首读原嵌套。", repeated, new OperationCanceledException("故障态取消。"));
        AggregateException failure = new("首读错误树。", repeated, nested, repeated);
        if (siblings) f.Stream.FailInputSiblings(failure.InnerExceptions);
        else f.Stream.FailInput(failure);
        Assert.True((await f.Run!.WaitAsync(Guard)).Completed);
        Assert.Empty(f.Calls);
        if (siblings) Assert.Equal(failure.InnerExceptions, Assert.IsType<AggregateException>(f.Reader.Error).InnerExceptions);
        else Assert.Same(failure, f.Reader.Error);
        Exception diagnostic = Assert.Single(f.Diagnostics());
        Assert.Equal("host-catalog-request-reader", diagnostic.Message);
        Assert.Same(f.Reader.Error, diagnostic.InnerException);
        Assert.Same(repeated, failure.InnerExceptions[0]);
        Assert.Same(nested, failure.InnerExceptions[1]);
        Assert.Same(repeated, failure.InnerExceptions[2]);
        await f.Auth.JoinOwnedOperationsAsync().WaitAsync(Guard);
        Assert.Equal(1, f.Stream.ReadCalls);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Historical_Ready_Does_Not_Dispatch_After_Parent_Registration_Is_Revoked()
    {
        await using Fixture f = new();
        f.Start();
        await f.AuthenticateAsync();
        HostControlWriter writer = await f.MakeReadyAsync();
        f.Registration.Dispose();
        await f.Revoked.WaitAsync(Guard);
        Assert.Same(writer, await f.Lifetime.Ready);
        Assert.False(f.Registration.CheckCurrent());
        TaskCompletionSource unexpectedlyHandled = Signal();
        f.Handler = _ => { unexpectedlyHandled.TrySetResult(); return Task.CompletedTask; };
        f.Stream.Feed(Frame(f.Request(1).Serialize()));
        await Task.WhenAny(f.CloseRequired.Task, unexpectedlyHandled.Task).WaitAsync(Guard);
        Assert.False(unexpectedlyHandled.Task.IsCompleted);
        Assert.True((await f.Run!.WaitAsync(Guard)).Completed);
        Assert.Empty(f.Calls);
        Assert.IsType<FrameProtocolException>(f.Reader.Error);
        Assert.Equal("host-catalog-request-reader", Assert.Single(f.Diagnostics()).Message);
        Assert.Equal(0, f.Context.SessionRegistry.ActiveSessionCount);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static byte[] Frame(byte[] payload)
    {
        byte[] result = new byte[TransportConstants.LengthPrefixBytes + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)payload.Length);
        payload.CopyTo(result, TransportConstants.LengthPrefixBytes);
        return result;
    }
    private static ReadOnlySpan<byte> Payload(byte[] frame)
    {
        Assert.Equal((uint)(frame.Length - TransportConstants.LengthPrefixBytes), BinaryPrimitives.ReadUInt32BigEndian(frame));
        return frame.AsSpan(TransportConstants.LengthPrefixBytes);
    }
    private static IEnumerable<Exception> ExceptionTree(Exception error)
    {
        yield return error;
        if (error is AggregateException aggregate)
        {
            foreach (Exception child in aggregate.InnerExceptions)
                foreach (Exception descendant in ExceptionTree(child)) yield return descendant;
        }
        else if (error.InnerException is { } inner)
            foreach (Exception descendant in ExceptionTree(inner)) yield return descendant;
    }
    private static async Task DrainAsync(Task? task)
    {
        if (task is null) return;
        try { await task.ConfigureAwait(false); }
        catch (Exception) { }
    }
    private sealed record Invocation(SessionRegistry.SessionRegistration Registration, HostControlWriter Writer,
        DisplayCatalogFrame Catalog, DisplaySwitchRequestFrame Request, CancellationToken Token);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly bool _requireApproval;
        private readonly List<TaskCompletionSource> _handlerGates = [];
        private readonly List<Pause> _pauses = [];
        private readonly ConcurrentQueue<Task> _handlerTasks = new();
        private Task? _closeSupervisor;
        private SessionRegistry.SessionRegistration? _registration;
        private int _sourceCalls;
        internal Fixture(bool requireApproval = false, bool withHandler = true, bool bind = true)
        {
            _requireApproval = requireApproval;
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
                    RequireLocalApproval = requireApproval,
                    FailureDelayMin = TimeSpan.Zero,
                    FailureDelayMax = TimeSpan.Zero,
                },
            };
            ConnectionSecurityContext security = new(IPAddress.Loopback, IPAddress.Loopback, 12345,
                SslProtocols.None, new byte[CertificatePin.LengthBytes]);
            TransportTimeouts timeouts = new(TransportTimeouts.Maximum, TransportTimeouts.Maximum,
                TransportTimeouts.Maximum, TransportTimeouts.Maximum, TransportTimeouts.Maximum, TransportTimeouts.Maximum);
            Auth = new(Stream, security, Context, timeouts);
            if (bind)
            {
                HostDisplayCatalogSendOptions options = new(ReadCatalog, CatalogLifetimeScenario.Budget)
                {
                    UseSessionWriter = true,
                    DisplayRequestHandler = withHandler ? Dispatch : null,
                };
                Auth.BindCatalogLifetime(options, CloseRequired);
            }
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
        internal DisplayCatalogFrame SourceCatalog { get; } = new(37, 12,
        [
            new DisplayInfo(new DisplayId(3), "主显示器", 0, 0, 1920, 1080, true),
            new DisplayInfo(new DisplayId(17), "左侧显示器", -1280, -100, 1280, 1024, false),
        ]);
        internal ConnectionCloseHandle Closer { get; }
        internal ControlAuthContext Context { get; }
        internal ControlAuthSession Auth { get; }
        internal Task<ControlAuthResult>? Run { get; private set; }
        internal ConcurrentQueue<Invocation> Calls { get; } = new();
        internal Func<Invocation, Task> Handler { get; set; } = _ => Task.CompletedTask;
        internal int SourceCalls => Volatile.Read(ref _sourceCalls);
        internal HostControlWriterLifetime Lifetime => Assert.IsType<HostControlWriterLifetime>(Auth.ControlWriterLifetime);
        internal SessionRegistry.SessionRegistration Registration => Assert.IsType<SessionRegistry.SessionRegistration>(_registration);
        internal Task Revoked { get; private set; } = Task.CompletedTask;
        internal HostDisplayRequestReader? OptionalReader => (HostDisplayRequestReader?)typeof(ControlAuthSession)
            .GetField("_requestReader", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Auth);
        internal HostDisplayRequestReader Reader => Assert.IsType<HostDisplayRequestReader>(OptionalReader);
        internal Task Holding => Assert.IsAssignableFrom<Task>(typeof(ControlAuthSession)
            .GetField("_catalogHolding", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Auth));
        internal Task HandlerWorker => Assert.IsAssignableFrom<Task>(typeof(HostDisplayRequestReader)
            .GetField("_handlerWorker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Reader));
        internal DisplaySwitchRequestFrame Request(long sequence, DisplayCatalogFrame? catalog = null) =>
            new(Auth.SessionId, sequence, 1234, catalog?.Sequence ?? Lifetime.Catalog?.Sequence ?? 0, SourceCatalog.Displays[1].Id);
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
        private Task Dispatch(SessionRegistry.SessionRegistration registration, HostControlWriter writer,
            DisplayCatalogFrame catalog, DisplaySwitchRequestFrame request, CancellationToken token)
        {
            Invocation call = new(registration, writer, catalog, request, token);
            Calls.Enqueue(call);
            Task original = Handler(call);
            _handlerTasks.Enqueue(original);
            return original;
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
        internal async Task AuthenticateAsync()
        {
            Task? approvalRead = null;
            if (_requireApproval)
            {
                await Approval.Entered.Task.WaitAsync(Guard);
                approvalRead = Assert.Single(Stream.Reads).Original.Task;
                Assert.False(approvalRead.IsCompleted);
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
            object entry = typeof(SessionRegistry.SessionRegistration)
                .GetField("_entry", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Registration)!;
            Revoked = Assert.IsType<TaskCompletionSource>(entry.GetType().GetProperty("Revoked")!.GetValue(entry)).Task;
            Assert.Equal(1, Stream.ReadCalls);
            if (approvalRead is not null) Assert.Same(approvalRead, Assert.Single(Stream.Reads).Original.Task);
        }
        internal async Task<HostControlWriter> MakeReadyAsync()
        {
            SourceResult.TrySetResult(SourceCatalog);
            await Inner.CatalogWrite.Entered.Task.WaitAsync(Guard);
            Inner.CatalogWrite.Release();
            await Inner.CatalogFlush.Entered.Task.WaitAsync(Guard);
            Inner.CatalogFlush.Release();
            return Assert.IsType<HostControlWriter>(await Lifetime.Ready.WaitAsync(Guard));
        }
        internal List<Exception> Diagnostics() { List<Exception> errors = []; Auth.AddCatalogErrors(errors); return errors; }
        internal TaskCompletionSource NewHandlerGate() { TaskCompletionSource gate = Signal(); _handlerGates.Add(gate); return gate; }
        internal Pause NewPause() { Pause pause = new(); _pauses.Add(pause); return pause; }
        public async ValueTask DisposeAsync()
        {
            // 先放来源、流、同步前缀及原handler全部闸，再关线/取消；收尾直接join原任务，Ready不作清理根。
            SourceResult.TrySetResult(SourceCatalog);
            Inner.ReleaseAll();
            Stream.CompleteInput();
            Approval.Release();
            foreach (Pause pause in _pauses) pause.Open();
            foreach (TaskCompletionSource gate in _handlerGates) gate.TrySetResult();
            CloseRequired.TrySetResult();
            Task cancel = HostStop.CancelAsync();
            Task close = Closer.CloseAsync();
            OptionalReader?.RequestStop();
            HostControlWriterLifetime? lifetime = Auth.ControlWriterLifetime;
            HostControlWriter? writer = lifetime is null ? null : CatalogLifetimeScenario.ReadField<HostControlWriter>(lifetime);
            writer?.RequestStop();
            await DrainAsync(cancel);
            await DrainAsync(close);
            await DrainAsync(Run);
            await DrainAsync(OptionalReader?.Completion);
            await DrainAsync(lifetime?.Completion);
            await DrainAsync(writer?.Completion);
            await DrainAsync(Auth.JoinOwnedOperationsAsync());
            await DrainAsync(_closeSupervisor);
            await DrainAsync(SourceResult.Task);
            await DrainAsync(Approval.Operation);
            foreach (Task task in _handlerTasks) await DrainAsync(task);
            foreach (TaskCompletionSource gate in _handlerGates) await DrainAsync(gate.Task);
            foreach (ReadProbe read in Stream.Reads) await DrainAsync(read.Original.Task);
            foreach (Task operation in Inner.Operations) await DrainAsync(operation);
            Inner.WireSuccess?.ClearSessionToken();
            foreach (Pause pause in _pauses) pause.Dispose();
            HostStop.Dispose();
            Assert.Empty(Closer.CleanupErrors);
            Assert.Equal(0, Clock.TimerCount);
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

    private sealed class ReadProbe(Memory<byte> buffer)
    {
        internal Memory<byte> Buffer { get; } = buffer;
        internal TaskCompletionSource<int> Original { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal byte[] Delivered { get; set; } = [];
    }

    // 复用真实V2 challenge/offer/accept/proof夹具；仅接管原一字节活动读及认证后接续，不启动TLS或GDI。
    private sealed class RequestInputStream(CatalogControlledSslStream inner) : SslStream(new MemoryStream())
    {
        private readonly object _gate = new();
        private readonly Queue<byte> _input = new();
        private readonly Queue<ReadProbe> _pending = new();
        private readonly Dictionary<int, TaskCompletionSource> _readRequested = [];
        private bool _requestInput, _eof;
        private Exception? _readError;
        private IEnumerable<Exception>? _readErrors;
        private int _calls, _active, _maximum;
        internal ConcurrentQueue<ReadProbe> Reads { get; } = new();
        internal ConcurrentQueue<byte[]> PostAuthWrites { get; } = new();
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
            // 认证帧读取长度从不为1；审批启用时此处也接管审批已经挂起的那次活动读。
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
        internal void FailInput(Exception failure) { lock (_gate) { _readError = failure; _eof = true; Deliver(); } }
        internal void FailInputSiblings(IEnumerable<Exception> failures) { lock (_gate) { _readErrors = failures; _eof = true; Deliver(); } }
        private void Deliver()
        {
            while (_pending.Count != 0 && (_input.Count != 0 || _eof))
            {
                ReadProbe read = _pending.Dequeue();
                _active--;
                if (_readErrors is { } errors) { read.Original.TrySetException(errors); continue; }
                if (_readError is { } error) { read.Original.TrySetException(error); continue; }
                int count = Math.Min(read.Buffer.Length, _input.Count);
                for (int index = 0; index < count; index++) read.Buffer.Span[index] = _input.Dequeue();
                read.Delivered = read.Buffer[..count].ToArray();
                read.Original.TrySetResult(count);
            }
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using JsonDocument document = JsonDocument.Parse(buffer[TransportConstants.LengthPrefixBytes..]);
            if (document.RootElement.GetProperty("type").GetString() is DisplayCatalogFrame.ExpectedType or DisplayChangedFrame.ExpectedType)
                PostAuthWrites.Enqueue(buffer.ToArray());
            return inner.WriteAsync(buffer, cancellationToken);
        }
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing) { CompleteInput(); inner.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
