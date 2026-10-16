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
using Admission = LanRemote.Transport.SessionRegistry.SessionRegistration.DisplaySwitchAdmission;
using ReplyResult = LanRemote.Transport.DisplaySwitchExecutor.ReplyResult;
using ReplyStatus = LanRemote.Transport.DisplaySwitchExecutor.ReplyStatus;
using Status = LanRemote.Transport.SessionRegistry.SessionRegistration.DisplaySwitchAdmissionStatus;

namespace LanRemote.Transport.Tests;

public sealed class HostControlWriterIntegrationTests
{
    private static readonly TimeSpan Guard = CatalogLifetimeScenario.Guard;

    [Fact(Timeout = 60_000)]
    public async Task Real_Auth_Catalog_Admission_Video_First_Send_And_Reply_Share_One_Writer()
    {
        await using Fixture f = new();
        f.Start();
        await f.AuthenticateAsync();
        Task originalHolding = f.Holding;
        Assert.False(originalHolding.IsCompleted);
        Assert.False(f.Lifetime.Ready.IsCompleted);
        Assert.Equal(1, f.Inner.HoldingCalls);
        VideoAttachLease lease = f.AttachVideo();
        HostControlWriter writer = await f.MakeReadyAsync();
        DisplayCatalogFrame catalog = Assert.IsType<DisplayCatalogFrame>(f.Lifetime.Catalog);
        Assert.Equal(0, catalog.Sequence);
        Assert.NotSame(f.SourceCatalog, catalog);
        Assert.Equal(f.SourceCatalog.Displays, catalog.Displays);
        Assert.Equal(f.Clock.GetUtcNow().ToUnixTimeMilliseconds(), catalog.TimestampMs);
        Assert.True(DisplayCatalogFrame.TryParse(Payload(Assert.Single(f.Stream.PostAuthWrites)), out var receivedCatalog, out _));
        Assert.Equal(catalog.Sequence, receivedCatalog!.Sequence);
        Assert.Equal(catalog.Displays, receivedCatalog.Displays);

        f.Source = new ReplaceableVideoFrameSource(f.Auth.SessionId);
        await f.Track(f.Source.ReplaceAsync(f.Producer())).WaitAsync(Guard);
        f.Publication = f.Context.SessionRegistry.PublishVideoSource(lease, f.Source);
        // 必须使用写者实际写出的目录；来源模板的 seq=37 不得进入真实准入。
        f.Admission = f.Registration.CreateDisplaySwitchAdmission(catalog);
        Producer candidate = f.Producer(withFrame: true);
        f.Executor = f.Admission.CreateReplyExecutor((target, _) =>
        {
            Assert.Same(catalog.Displays[1], target);
            return Task.FromResult<IVideoFrameProducer>(candidate);
        });
        DisplaySwitchRequestFrame request = new(f.Auth.SessionId, 9, 1234, catalog.Sequence, catalog.Displays[1].Id);
        Assert.Equal(Status.Accepted, f.Executor.TryExecute(request, out var execution, out var receipt));
        var result = await f.Track(Assert.IsAssignableFrom<Task<DisplaySwitchExecutor.ExecutionResult>>(execution)).WaitAsync(Guard);
        Assert.True(result.Transferred && result.ReplacementCompleted);
        var accepted = Assert.IsType<DisplaySwitchExecutor.RequestExecutionReceipt>(receipt);
        var sendReceipt = Assert.IsType<DisplaySwitchExecutor.RequestSendReceipt>(result.SendReceipt);
        Assert.True(accepted.Binding.IsCompletedSuccessfully);
        Assert.Same(sendReceipt.Generation, await accepted.Binding);
        Assert.Equal(ReplyStatus.NotReady, f.Executor.TrySendReply(accepted, writer, out var premature));
        Assert.Null(premature);
        Assert.Single(f.Stream.PostAuthWrites);

        VideoFrameSender sender = new(70, f.Source);
        Task sending = f.Track(sender.SendAsync(f.Auth.SessionId, new VideoFrameWriter(f.Video), f.Source,
            f.Clock, TimeSpan.FromSeconds(10), f.HostStop.Token));
        await f.Video.WriteBlocked.Task.WaitAsync(Guard);
        Assert.False(sendReceipt.FirstSend.IsCompleted);
        Assert.Equal(ReplyStatus.NotReady, f.Executor.TrySendReply(accepted, writer, out premature));
        Assert.Null(premature);
        Assert.Single(f.Stream.PostAuthWrites);
        f.Video.ReleaseWrite();
        await candidate.NextReadEntered.Task.WaitAsync(Guard);
        Assert.True(sendReceipt.FirstSend.IsCompletedSuccessfully);
        Assert.Equal(new VideoFrameSender.SentFrame(70, 640, 360), await sendReceipt.FirstSend);
        Assert.False(sending.IsCompleted);
        f.Clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(ReplyStatus.Accepted, f.Executor.TrySendReply(accepted, writer, out var replying));
        Assert.Equal(new ReplyResult(true, true, true),
            await f.Track(Assert.IsAssignableFrom<Task<ReplyResult>>(replying)).WaitAsync(Guard));
        byte[][] frames = f.Stream.PostAuthWrites.ToArray();
        Assert.Equal(2, frames.Length);
        Assert.True(DisplayChangedFrame.TryParse(Payload(frames[1]), f.Auth.SessionId, out var changed, out _));
        Assert.Equal(1, changed!.Sequence);
        Assert.Equal(f.Clock.GetUtcNow().ToUnixTimeMilliseconds(), changed.TimestampMs);
        Assert.Equal(request.Sequence, changed.RequestSequence);
        Assert.Equal(0, changed.CatalogSequence);
        Assert.Equal(request.DisplayId, changed.DisplayId);
        Assert.Equal((ulong)70, changed.FirstFrameId);
        Assert.Equal(640, changed.Width);
        Assert.Equal(360, changed.Height);
        Assert.Same(originalHolding, f.Holding);
        Assert.Equal(1, f.Inner.HoldingCalls);
        Assert.False(f.CloseRequired.Task.IsCompleted);

        // Ready 是历史结果；父结束之后即使保留该引用，也不能再次写出。
        f.Inner.ReleaseHolding(0);
        await f.Lifetime.StopRequired.WaitAsync(Guard);
        await lease.Revoked.WaitAsync(Guard);
        Assert.True(f.Lifetime.Ready.IsCompletedSuccessfully);
        Assert.Same(writer, await f.Lifetime.Ready);
        Assert.Throws<InvalidOperationException>(() => { _ = writer.WriteDisplayChangedAsync(changed); });
        Assert.Equal(2, f.Stream.PostAuthWrites.Count);
        Assert.Equal(0, f.Context.SessionRegistry.ActiveSessionCount);
        Assert.False(f.Run!.IsCompleted);
        await f.Track(f.StopVideoAsync()).WaitAsync(Guard);
        Assert.True((await f.Run.WaitAsync(Guard)).Completed);
        await f.Lifetime.Completion!.WaitAsync(Guard);
        await writer.Completion.WaitAsync(Guard);
        List<Exception> errors = [];
        f.Auth.AddCatalogErrors(errors);
        Assert.Empty(errors);
    }

    [Theory(Timeout = 60_000)]
    [InlineData("holding")]
    [InlineData("host-stop")]
    [InlineData("writer-budget")]
    public async Task Stop_Revokes_Parent_Before_Joining_Noncooperative_Catalog_IO_And_Original_Holding(string reason)
    {
        await using Fixture f = new();
        f.Start();
        await f.AuthenticateAsync();
        VideoAttachLease lease = f.AttachVideo();
        HostControlWriter writer = CatalogLifetimeScenario.ReadField<HostControlWriter>(f.Lifetime);
        f.ReleaseCatalog();
        await f.Inner.CatalogWrite.Entered.Task.WaitAsync(Guard);
        Task originalWrite = Assert.IsAssignableFrom<Task>(f.Inner.CatalogWrite.Operation);
        Task completion = Assert.IsAssignableFrom<Task>(f.Lifetime.Completion);
        Assert.False(f.Lifetime.Ready.IsCompleted);
        if (reason == "holding") f.Inner.ReleaseHolding(0);
        else if (reason == "host-stop") await f.Track(f.HostStop.CancelAsync()).WaitAsync(Guard);
        else f.Clock.Advance(CatalogLifetimeScenario.Budget);

        await f.CloseRequired.Task.WaitAsync(Guard);
        await f.Lifetime.StopRequired.WaitAsync(Guard);
        await writer.StopRequired.WaitAsync(Guard);
        await lease.Revoked.WaitAsync(Guard);
        await f.Inner.DisposeExited.Task.WaitAsync(Guard);
        Assert.True(f.Lifetime.Ready.IsCompletedSuccessfully);
        Assert.Null(await f.Lifetime.Ready);
        Assert.Null(f.Lifetime.Catalog);
        Assert.Equal(0, f.Context.SessionRegistry.ActiveSessionCount);
        Assert.False(originalWrite.IsCompleted);
        Assert.False(completion.IsCompleted);
        Assert.False(f.Run!.IsCompleted);
        Assert.False(lease.Completed.IsCompleted);
        if (reason != "holding") Assert.False(f.Inner.HoldingOperation!.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => { _ = writer.WriteCatalogAsync(() => ValueTask.FromResult(f.SourceCatalog)); });
        await CatalogLifetimeScenario.AssertAwaitPathAsync(originalWrite, writer.Completion);
        await CatalogLifetimeScenario.AssertAwaitPathAsync(originalWrite, completion);
        await CatalogLifetimeScenario.AssertAwaitPathAsync(originalWrite, f.Run);

        f.Inner.CatalogWrite.Release();
        f.Inner.CatalogFlush.Release();
        if (reason != "holding")
        {
            await CatalogLifetimeScenario.AssertAwaitPathAsync(f.Holding, completion);
            Assert.False(completion.IsCompleted);
        }
        f.Inner.ReleaseHolding(0);
        lease.Complete();
        // 原受控读取忽略取消并最终返回 EOF；保持原 holding 的成功分类，不伪造取消结果。
        Assert.True((await f.Run.WaitAsync(Guard)).Completed);
        List<Exception> errors = [];
        f.Auth.AddCatalogErrors(errors);
        HostDisplayCatalogSendAttempt attempt = CatalogLifetimeScenario.ReadField<HostDisplayCatalogSendAttempt>(writer);
        Exception? budgetError = attempt.Snapshot.Budget?.Error;
        if (reason == "writer-budget") Assert.Equal(ControlFrameBudget.BudgetEnd.Expired, attempt.Snapshot.Budget!.End);
        if (reason == "writer-budget" && budgetError is not null)
        {
            AggregateException ownerError = Assert.IsType<AggregateException>(await ObserveAsync(completion));
            AggregateException writerError = Assert.IsType<AggregateException>(await ObserveAsync(writer.Completion));
            Assert.Contains(ExceptionTree(writerError), error => ReferenceEquals(budgetError, error));
            Exception diagnostic = Assert.Single(errors);
            Assert.Equal("host-catalog-owner-completion", diagnostic.Message);
            Assert.Same(ownerError, diagnostic.InnerException);
        }
        else
        {
            await completion.WaitAsync(Guard);
            await writer.Completion.WaitAsync(Guard);
            Assert.Empty(errors);
        }
        Assert.True(originalWrite.IsCompleted);
        Assert.True(f.Holding.IsCompleted);
        Assert.True(lease.Completed.IsCompletedSuccessfully);
        Assert.Equal(1, f.Inner.HoldingCalls);
        Assert.Equal(0, f.Clock.TimerCount);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_Auth_Success_Write_Or_Flush_Never_Constructs_The_Session_Writer(bool flush)
    {
        await using Fixture f = new();
        IOException failure = new("受控认证成功帧未交付。");
        f.Start();
        await f.Inner.SuccessWrite.Entered.Task.WaitAsync(Guard);
        Assert.Null(f.Auth.ControlWriterLifetime);
        if (flush)
        {
            f.Inner.SuccessWrite.Release();
            await f.Inner.SuccessFlush.Entered.Task.WaitAsync(Guard);
            f.Inner.SuccessFlush.Fail(failure);
        }
        else f.Inner.SuccessWrite.Fail(failure);
        ControlAuthResult result = await f.Run!.WaitAsync(Guard);
        Assert.False(result.Completed);
        Assert.Equal(ControlAuthSession.RejectSuccessNotDelivered, result.Rejection);
        Assert.Null(f.Auth.ControlWriterLifetime);
        Assert.Equal(0, f.SourceCalls);
        Assert.Equal(0, f.Inner.HoldingCalls);
        Assert.Equal(0, f.Context.SessionRegistry.ActiveSessionCount);
        Assert.Empty(f.Stream.PostAuthWrites);
        Assert.Equal(new[] { "auth_challenge", "display_catalog_offer", "auth_success", "authentication_failed" }, f.Inner.Writes);
        List<Exception> errors = [];
        f.Auth.AddCatalogErrors(errors);
        Assert.Empty(errors);
    }

    [Fact(Timeout = 60_000)]
    public async Task Writer_Source_Error_Tree_Reaches_AddCatalogErrors_Without_Flattening_Or_Deduplicating()
    {
        await using Fixture f = new();
        f.Start();
        await f.AuthenticateAsync();
        VideoAttachLease lease = f.AttachVideo();
        HostControlWriter writer = CatalogLifetimeScenario.ReadField<HostControlWriter>(f.Lifetime);
        IOException repeated = new("来源重复的原异常。");
        AggregateException nested = new("来源原嵌套。", new OperationCanceledException("来源自发取消。"), repeated);
        f.SourceResult.SetException([repeated, nested, repeated]);
        await f.Lifetime.StopRequired.WaitAsync(Guard);
        await lease.Revoked.WaitAsync(Guard);
        Assert.True(f.Lifetime.Ready.IsCompletedSuccessfully);
        Assert.Null(await f.Lifetime.Ready);
        Assert.False(f.Run!.IsCompleted);
        Assert.Empty(f.Stream.PostAuthWrites);
        Assert.Equal(0, f.Context.SessionRegistry.ActiveSessionCount);
        f.Inner.ReleaseHolding(0);
        lease.Complete();
        Assert.True((await f.Run.WaitAsync(Guard)).Completed);
        Exception? ownerError = await ObserveAsync(f.Lifetime.Completion!);
        Assert.IsType<AggregateException>(ownerError);
        Assert.IsType<AggregateException>(await ObserveAsync(writer.Completion));
        List<Exception> errors = [];
        f.Auth.AddCatalogErrors(errors);
        Exception diagnostic = Assert.Single(errors);
        Assert.Equal("host-catalog-owner-completion", diagnostic.Message);
        Assert.Same(ownerError, diagnostic.InnerException);
        Assert.Contains(ExceptionTree(diagnostic), error => error is AggregateException tree &&
            tree.InnerExceptions.Count == 3 && ReferenceEquals(repeated, tree.InnerExceptions[0]) &&
            ReferenceEquals(nested, tree.InnerExceptions[1]) && ReferenceEquals(repeated, tree.InnerExceptions[2]));
        Assert.Same(repeated, nested.InnerExceptions[1]);
        Assert.Equal(1, f.SourceCalls);
        Assert.Equal(0, f.Clock.TimerCount);
    }

    [Fact(Timeout = 60_000)]
    public async Task Default_Catalog_Binding_Still_Uses_Original_Attempt_And_Source_Sequence()
    {
        await using Fixture f = new(useSessionWriter: false);
        f.Start();
        await f.AuthenticateAsync();
        f.ReleaseCatalog();
        await f.ReleaseCatalogIOAsync();
        HostAuthenticatedCatalogLifetime lifetime = CatalogLifetimeScenario.ReadField<HostAuthenticatedCatalogLifetime>(f.Auth);
        HostDisplayCatalogSendAttempt attempt = CatalogLifetimeScenario.ReadField<HostDisplayCatalogSendAttempt>(lifetime);
        Assert.Equal(HostDisplayCatalogSendAttempt.AttemptEnd.Sent, (await attempt.Completion!.WaitAsync(Guard)).End);
        Assert.Null(f.Auth.ControlWriterLifetime);
        Assert.True(DisplayCatalogFrame.TryParse(Payload(Assert.Single(f.Stream.PostAuthWrites)), out var catalog, out _));
        Assert.Equal(f.SourceCatalog.Sequence, catalog!.Sequence);
        Assert.Equal(f.SourceCatalog.TimestampMs, catalog.TimestampMs);
        Assert.Equal(1, f.Inner.HoldingCalls);
        f.Inner.ReleaseHolding(0);
        Assert.True((await f.Run!.WaitAsync(Guard)).Completed);
        await lifetime.Completion!.WaitAsync(Guard);
        Assert.True(attempt.Snapshot.AllOwnedRootsReleased);
        List<Exception> errors = [];
        f.Auth.AddCatalogErrors(errors);
        Assert.Empty(errors);
    }

    private static ReadOnlySpan<byte> Payload(byte[] frame)
    {
        Assert.True(frame.Length > TransportConstants.LengthPrefixBytes);
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

    private static async Task<Exception?> ObserveAsync(Task task)
    {
        try { await task.WaitAsync(Guard); return null; }
        catch (Exception error) when (task.IsCompleted && error is not TimeoutException) { return error; }
    }

    private static async Task DrainAsync(Task? task)
    {
        if (task is null) return;
        // 正文看门狗只作诊断；收尾直接等待原根，即使上一原根失败也不跳过其他根。
        try { await task.ConfigureAwait(false); }
        catch (Exception) { }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly List<Task> _roots = [];
        private readonly List<Producer> _producers = [];
        private readonly List<EncodedFrame> _frames = [];
        private readonly bool _useSessionWriter;
        private Task? _closeSupervisor;
        private int _sourceCalls;
        internal Fixture(bool useSessionWriter = true)
        {
            _useSessionWriter = useSessionWriter;
            Stream = new RecordingControlStream(Inner);
            Closer = new ConnectionCloseHandle(new MemoryStream(), new HostLifecycleErrors());
            Closer.Attach(Stream);
            Security = new(IPAddress.Loopback, IPAddress.Loopback, 12345, SslProtocols.None, new byte[CertificatePin.LengthBytes]);
            Context = new ControlAuthContext
            {
                ServerDeviceId = CatalogLifetimeScenario.ServerId,
                AccessSecretStore = SecretStore,
                FailedAuthLimiter = new FailedAuthLimiter(),
                PendingApprovalLimiter = new ConnectionAdmissionLimiter(3, 1),
                ApprovalGate = new CatalogApproval(),
                SessionRegistry = new SessionRegistry(),
                TimeProvider = Clock,
                Options = new ControlAuthOptions { RequireLocalApproval = false, FailureDelayMin = TimeSpan.Zero, FailureDelayMax = TimeSpan.Zero },
            };
            TransportTimeouts timeouts = new(TransportTimeouts.Maximum, TransportTimeouts.Maximum,
                TransportTimeouts.Maximum, TransportTimeouts.Maximum, TransportTimeouts.Maximum, TransportTimeouts.Maximum);
            Auth = new ControlAuthSession(Stream, Security, Context, timeouts);
            HostDisplayCatalogSendOptions options = useSessionWriter
                ? new(ReadCatalog, CatalogLifetimeScenario.Budget) { UseSessionWriter = true }
                : new(ReadCatalog, CatalogLifetimeScenario.Budget);
            Auth.BindCatalogLifetime(options, CloseRequired);
        }
        internal CatalogControlledSslStream Inner { get; } = new(hello: false, holdClose: false);
        internal RecordingControlStream Stream { get; }
        internal CatalogSecretStore SecretStore { get; } = new();
        internal ManualDeadlineClock Clock { get; } = new();
        internal CancellationTokenSource HostStop { get; } = new();
        internal TaskCompletionSource CloseRequired { get; } = CatalogLifetimeScenario.Signal();
        internal TaskCompletionSource SourceEntered { get; } = CatalogLifetimeScenario.Signal();
        internal TaskCompletionSource<DisplayCatalogFrame> SourceResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal DisplayCatalogFrame SourceCatalog { get; } = new(37, 12,
        [
            new DisplayInfo(new DisplayId(3), "主显示器", 0, 0, 1920, 1080, true),
            new DisplayInfo(new DisplayId(17), "左侧显示器", -1280, -100, 1280, 1024, false),
        ]);
        internal ConnectionCloseHandle Closer { get; }
        internal ConnectionSecurityContext Security { get; }
        internal ControlAuthContext Context { get; }
        internal ControlAuthSession Auth { get; }
        internal Task<ControlAuthResult>? Run { get; private set; }
        internal int SourceCalls => Volatile.Read(ref _sourceCalls);
        internal HostControlWriterLifetime Lifetime => Assert.IsType<HostControlWriterLifetime>(Auth.ControlWriterLifetime);
        internal Task Holding => Assert.IsAssignableFrom<Task>(typeof(ControlAuthSession)
            .GetField("_catalogHolding", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Auth));
        internal SessionRegistry.SessionRegistration Registration => Assert.IsType<SessionRegistry.SessionRegistration>(
            CatalogLifetimeScenario.ReadLocal(Run!, typeof(ControlAuthSession), "registration"));
        internal VideoAttachLease? Lease { get; private set; }
        internal ReplaceableVideoFrameSource? Source { get; set; }
        internal IDisposable? Publication { get; set; }
        internal Admission? Admission { get; set; }
        internal DisplaySwitchExecutor? Executor { get; set; }
        internal RecordingVideoStream Video { get; } = new() { GateWriteAt = 0 };

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
            Assert.Equal(1, Inner.HoldingCalls);
            Assert.All(SecretStore.ReturnedKey!, value => Assert.Equal((byte)0, value));
            SourceEntered.TrySetResult();
            return new(SourceResult.Task);
        }
        internal async Task AuthenticateAsync()
        {
            await Inner.SuccessWrite.Entered.Task.WaitAsync(Guard);
            Assert.Null(Auth.ControlWriterLifetime);
            Assert.Equal(0, SourceCalls);
            Inner.SuccessWrite.Release();
            await Inner.SuccessFlush.Entered.Task.WaitAsync(Guard);
            Assert.Null(Auth.ControlWriterLifetime);
            Assert.Equal(0, Context.SessionRegistry.ActiveSessionCount);
            Assert.Equal(0, SourceCalls);
            Inner.SuccessFlush.Release();
            await SourceEntered.Task.WaitAsync(Guard);
        }
        internal void ReleaseCatalog() => SourceResult.TrySetResult(SourceCatalog);
        internal async Task ReleaseCatalogIOAsync()
        {
            await Inner.CatalogWrite.Entered.Task.WaitAsync(Guard);
            Inner.CatalogWrite.Release();
            await Inner.CatalogFlush.Entered.Task.WaitAsync(Guard);
            Inner.CatalogFlush.Release();
        }
        internal async Task<HostControlWriter> MakeReadyAsync()
        {
            ReleaseCatalog();
            await ReleaseCatalogIOAsync();
            return Assert.IsType<HostControlWriter>(await Lifetime.Ready.WaitAsync(Guard));
        }
        internal VideoAttachLease AttachVideo()
        {
            Assert.Null(Lease);
            byte[] nonce = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
            ConnectionSecurityContext videoSecurity = new(IPAddress.Loopback, IPAddress.Loopback, 12346,
                SslProtocols.None, Security.ServerCertificateSha256.Span);
            byte[] transcript = [.. Encoding.UTF8.GetBytes("LANREMOTE-VIDEO-V1\0"),
                .. Convert.FromHexString(Auth.SessionId.ToString("N")), .. nonce, .. Security.ServerCertificateSha256.ToArray()];
            AuthSuccessFrame success = Assert.IsType<AuthSuccessFrame>(Inner.WireSuccess);
            byte[] proof = HMACSHA256.HashData(success.SessionToken.Span, transcript);
            try
            {
                Assert.Equal(VideoAttachStatus.Attached, Context.SessionRegistry.TryAttachVideo(Auth.SessionId,
                    videoSecurity, nonce, proof, default, out VideoAttachLease? lease));
                return Lease = Assert.IsType<VideoAttachLease>(lease);
            }
            finally { CryptographicOperations.ZeroMemory(proof); success.ClearSessionToken(); }
        }
        internal Producer Producer(bool withFrame = false)
        {
            EncodedFrame? frame = null;
            if (withFrame)
            {
                RecordingVideoOwner owner = new(32);
                VideoFrameTestData.Payload().CopyTo(owner.Bytes, 0);
                frame = new(VideoCodec.Jpeg, 640, 360, 999, 1234, 60, owner, 5);
                _frames.Add(frame);
            }
            Producer producer = new(frame);
            _producers.Add(producer);
            return producer;
        }
        internal T Track<T>(T task) where T : Task { _roots.Add(task); return task; }
        internal async Task StopVideoAsync()
        {
            foreach (Producer producer in _producers) producer.Release();
            Video.ReleaseWrite();
            Task? closing = Source?.DisposeAsync().AsTask();
            Executor?.RequestStop();
            Publication?.Dispose();
            Admission?.Dispose();
            if (closing is not null) await closing;
            if (Source is not null) await Source.Completion;
            if (Executor is not null) await Executor.Completion;
            Lease?.Complete();
        }
        public async ValueTask DisposeAsync()
        {
            // 先放所有非合作 gate，再通知各 owner；绝不使用 WaitAsync 放弃真实清理根。
            Inner.ReleaseAll();
            ReleaseCatalog();
            foreach (Producer producer in _producers) producer.Release();
            Video.ReleaseWrite();
            CloseRequired.TrySetResult();
            Task cancel = HostStop.CancelAsync();
            Task close = Closer.CloseAsync();
            Task? sourceClose = Source?.DisposeAsync().AsTask();
            Executor?.RequestStop();
            HostControlWriterLifetime? lifetime = Auth.ControlWriterLifetime;
            HostControlWriter? writer = lifetime is null ? null : CatalogLifetimeScenario.ReadField<HostControlWriter>(lifetime);
            writer?.RequestStop();
            Publication?.Dispose();
            Admission?.Dispose();
            Lease?.Complete();
            await DrainAsync(cancel);
            await DrainAsync(close);
            await DrainAsync(sourceClose);
            foreach (Task root in _roots) await DrainAsync(root);
            await DrainAsync(Source?.Completion);
            await DrainAsync(Executor?.Completion);
            await DrainAsync(Run);
            await DrainAsync(Auth.ControlWriterLifetime?.Completion);
            await DrainAsync(writer?.Completion);
            if (!_useSessionWriter && SourceCalls != 0)
            {
                HostAuthenticatedCatalogLifetime owner = CatalogLifetimeScenario.ReadField<HostAuthenticatedCatalogLifetime>(Auth);
                await DrainAsync(owner.Completion);
                await DrainAsync(CatalogLifetimeScenario.ReadField<HostDisplayCatalogSendAttempt>(owner).Completion);
            }
            await DrainAsync(Auth.JoinOwnedOperationsAsync());
            await DrainAsync(_closeSupervisor);
            await DrainAsync(SourceResult.Task);
            await DrainAsync(Inner.HoldingOperation);
            foreach (Task operation in Inner.Operations) await DrainAsync(operation);
            foreach (Producer producer in _producers) { await DrainAsync(producer.StopAsync()); await DrainAsync(producer.Completion); }
            foreach (EncodedFrame frame in _frames) frame.Dispose();
            Inner.WireSuccess?.ClearSessionToken();
            Video.Dispose();
            HostStop.Dispose();
            Assert.Empty(Closer.CleanupErrors);
            Assert.Equal(0, Clock.TimerCount);
        }
    }

    // 复用 V2 proof/Offer/Accept 夹具；仅追加原始认证后帧记录，不创建网络或执行 TLS 握手。
    private sealed class RecordingControlStream(CatalogControlledSslStream inner) : SslStream(new MemoryStream())
    {
        internal ConcurrentQueue<byte[]> PostAuthWrites { get; } = new();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
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
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class Producer(EncodedFrame? frame) : IVideoFrameProducer
    {
        private readonly TaskCompletionSource _completion = CatalogLifetimeScenario.Signal();
        private readonly TaskCompletionSource<EncodedFrame?> _next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;
        internal TaskCompletionSource NextReadEntered { get; } = CatalogLifetimeScenario.Signal();
        public void Start() { }
        public ValueTask<EncodedFrame?> ReadNextAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _reads) == 1) return ValueTask.FromResult(frame);
            NextReadEntered.TrySetResult();
            return new(_next.Task);
        }
        internal void Release() => _next.TrySetResult(null);
        public Task StopAsync() { Release(); _completion.TrySetResult(); return _completion.Task; }
        public ValueTask DisposeAsync() => new(StopAsync());
        public Task Completion => _completion.Task;
    }
}
