using System.Net;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

public sealed class VideoSourcePublicationTests
{
    // 本组只覆盖登记表接缝，不模拟 Router、ACK、控制消息或真实 sender。
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_Attachment_Or_Publication_Rejects_Candidate_Synchronously(bool attached)
    {
        await using Fixture fixture = new(attach: attached);
        Producer candidate = fixture.Candidate();

        Assert.Throws<InvalidOperationException>(() => { _ = fixture.Replace(candidate); });
        AssertUntouched(candidate);
        Assert.Equal(!attached, fixture.Registration.VideoCompletion.IsCompleted);
        Assert.False(fixture.Source.Completion.IsCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Forged_Lease_With_Identical_Guids_Cannot_Publish_Or_Consume_Publication(bool attached)
    {
        await using Fixture fixture = new(attach: attached);
        VideoAttachLease forged = new(fixture.SessionId, fixture.Video.ConnectionId,
            fixture.Lease?.Revoked ?? Signal().Task);
        try
        {
            Assert.Throws<InvalidOperationException>(() => fixture.Registry.PublishVideoSource(forged, fixture.Source));
            Assert.False(forged.Completed.IsCompleted);
            if (!attached) fixture.Attach();
            Assert.Equal(fixture.Lease!.SessionId, forged.SessionId);
            Assert.Equal(fixture.Lease.VideoConnectionId, forged.VideoConnectionId);
            Assert.NotSame(fixture.Lease, forged);
            await fixture.InstallAndPublishAsync();
            await fixture.Replace(fixture.Candidate()).WaitAsync(Guard);
            Assert.False(fixture.Lease.Completed.IsCompleted);
        }
        finally
        {
            forged.Complete();
        }
    }

    [Fact]
    public async Task Foreign_Registry_Real_Lease_With_Same_Session_And_Connection_Cannot_Publish()
    {
        await using Fixture local = new();
        await using Fixture foreign = new(sessionId: local.SessionId, video: local.Video);

        Assert.Equal(local.Lease!.SessionId, foreign.Lease!.SessionId);
        Assert.Equal(local.Lease.VideoConnectionId, foreign.Lease.VideoConnectionId);
        Assert.NotSame(local.Lease, foreign.Lease);
        Assert.Throws<InvalidOperationException>(() => local.Registry.PublishVideoSource(foreign.Lease, local.Source));
        await local.InstallAndPublishAsync();
        await foreign.InstallAndPublishAsync();
        await local.Replace(local.Candidate()).WaitAsync(Guard);
        await foreign.Replace(foreign.Candidate()).WaitAsync(Guard);
        Assert.False(local.Lease.Revoked.IsCompleted);
        Assert.False(foreign.Lease.Revoked.IsCompleted);
    }

    [Fact]
    public async Task Source_Session_Mismatch_Does_Not_Consume_The_Real_Lease_Publication()
    {
        await using Fixture fixture = new();
        ReplaceableVideoFrameSource foreignSource = new(Guid.NewGuid());
        try
        {
            ArgumentException error = Assert.Throws<ArgumentException>(
                () => fixture.Registry.PublishVideoSource(fixture.Lease!, foreignSource));
            Assert.Equal("source", error.ParamName);
            Assert.False(foreignSource.Completion.IsCompleted);
            await fixture.InstallAndPublishAsync();
            await fixture.Replace(fixture.Candidate()).WaitAsync(Guard);
        }
        finally
        {
            await foreignSource.DisposeAsync().AsTask().WaitAsync(Guard);
            await foreignSource.Completion.WaitAsync(Guard);
        }
    }

    [Theory]
    [InlineData("lease")]
    [InlineData("source")]
    public async Task Null_Publication_Argument_Is_Rejected_Without_Consuming_The_Slot(string missing)
    {
        await using Fixture fixture = new();
        ArgumentNullException error = Assert.Throws<ArgumentNullException>(() => fixture.Registry.PublishVideoSource(
            missing == "lease" ? null! : fixture.Lease!, missing == "source" ? null! : fixture.Source));

        Assert.Equal(missing, error.ParamName);
        await fixture.InstallAndPublishAsync();
        Assert.False(fixture.Lease!.Completed.IsCompleted);
        Assert.False(fixture.Source.Completion.IsCompleted);
    }

    [Fact]
    public async Task Null_Replacement_Is_Synchronous_And_Does_Not_Close_The_Published_Source()
    {
        await using Fixture fixture = new();
        await fixture.InstallAndPublishAsync();

        ArgumentNullException error = Assert.Throws<ArgumentNullException>(
            () => { _ = fixture.Registration.ReplaceVideoProducerAsync(null!); });
        Assert.Equal("next", error.ParamName);
        Producer valid = fixture.Candidate();
        await fixture.Replace(valid).WaitAsync(Guard);
        Assert.Equal(1, valid.StartCalls);
        Assert.False(fixture.Source.Completion.IsCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Lease_Can_Publish_Only_Once_Even_After_Idempotent_Unpublication(bool unpublish)
    {
        await using Fixture fixture = new();
        await fixture.InstallAndPublishAsync();
        if (unpublish)
        {
            fixture.Publication!.Dispose();
            fixture.Publication.Dispose();
        }

        Assert.Throws<InvalidOperationException>(() => fixture.Registry.PublishVideoSource(fixture.Lease!, fixture.Source));
        Producer candidate = fixture.Candidate();
        if (unpublish)
        {
            Assert.Throws<InvalidOperationException>(() => { _ = fixture.Replace(candidate); });
            AssertUntouched(candidate);
        }
        else
        {
            await fixture.Replace(candidate).WaitAsync(Guard);
            Assert.Equal(1, candidate.StartCalls);
        }
        Assert.False(fixture.Lease!.Completed.IsCompleted);
        Assert.False(fixture.Lease.Revoked.IsCompleted);
    }

    [Fact]
    public async Task Expired_Attach_Window_Does_Not_Prevent_Publication_Or_Replacement_Of_The_Attached_Child()
    {
        await using Fixture fixture = new();
        VideoAttachLease lease = fixture.Lease!;
        Task childCompletion = fixture.Registration.VideoCompletion;
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(15_000));
        Assert.True(fixture.AttachWindow.IsExpired);

        await fixture.InstallAndPublishAsync();
        fixture.Clock.Advance(TimeSpan.FromDays(1));
        Producer candidate = fixture.Candidate();
        await fixture.Replace(candidate).WaitAsync(Guard);

        Assert.True(fixture.AttachWindow.IsExpired);
        Assert.Same(lease, fixture.Lease);
        Assert.Same(childCompletion, fixture.Registration.VideoCompletion);
        Assert.Equal(1, candidate.StartCalls);
        AssertRetiredOnce(fixture.Initial);
        Assert.False(childCompletion.IsCompleted);
        Assert.False(lease.Revoked.IsCompleted);
        Assert.False(fixture.Source.Completion.IsCompleted);
        Assert.Equal(1, fixture.Registry.ActiveSessionCount);
    }

    [Theory]
    [InlineData("control-cancel")]
    [InlineData("unregister")]
    [InlineData("child-complete")]
    public async Task Invalidated_Parent_Or_Child_Cannot_Publish(string reason)
    {
        await using Fixture fixture = new();
        fixture.Invalidate(reason);

        Assert.Throws<InvalidOperationException>(() => fixture.Registry.PublishVideoSource(fixture.Lease!, fixture.Source));
        Assert.False(fixture.Source.Completion.IsCompleted);
        Assert.Equal(reason == "child-complete", fixture.Lease!.Completed.IsCompleted);
        Assert.Equal(reason == "unregister", fixture.Lease.Revoked.IsCompleted);
    }

    [Theory]
    [InlineData("control-cancel")]
    [InlineData("unregister")]
    [InlineData("child-complete")]
    public async Task Published_Slot_Does_Not_Bypass_Current_Parent_Or_Child_Eligibility(string reason)
    {
        await using Fixture fixture = new();
        await fixture.InstallAndPublishAsync();
        Task childCompletion = fixture.Registration.VideoCompletion;
        fixture.Invalidate(reason);
        Producer candidate = fixture.Candidate();

        Assert.Throws<InvalidOperationException>(() => { _ = fixture.Replace(candidate); });
        AssertUntouched(candidate);
        Assert.Same(childCompletion, fixture.Registration.VideoCompletion);
        Assert.Equal(reason == "child-complete", childCompletion.IsCompleted);
        Assert.Equal(0, fixture.Initial.StopCalls);
        Assert.Equal(0, fixture.Initial.DisposeCalls);
        Assert.False(fixture.Source.Completion.IsCompleted);
    }

    [Fact]
    public async Task Old_Registration_With_Reused_Id_Cannot_Replace_Or_Unregister_The_New_Entry()
    {
        await using Fixture old = new();
        await old.InstallAndPublishAsync();
        Task oldChild = old.Registration.VideoCompletion;
        old.Registration.Dispose();
        await using Fixture current = new(old.Registry, old.SessionId);
        await current.InstallAndPublishAsync();
        Producer rejected = old.Candidate();

        Assert.Throws<InvalidOperationException>(() => { _ = old.Replace(rejected); });
        old.Registration.Dispose();
        AssertUntouched(rejected);
        Assert.Equal(1, current.Registry.ActiveSessionCount);
        Assert.Same(oldChild, old.Registration.VideoCompletion);
        Assert.NotSame(oldChild, current.Registration.VideoCompletion);
        Assert.True(old.Lease!.Revoked.IsCompletedSuccessfully);
        Assert.False(current.Lease!.Revoked.IsCompleted);
        Producer accepted = current.Candidate();
        await current.Replace(accepted).WaitAsync(Guard);
        Assert.Equal(1, accepted.StartCalls);
    }

    [Fact]
    public async Task Stale_Publication_Dispose_Does_Not_Clear_A_New_Entry_With_The_Same_Id()
    {
        await using Fixture old = new();
        await old.InstallAndPublishAsync();
        old.Registration.Dispose();
        await using Fixture current = new(old.Registry, old.SessionId);
        await current.InstallAndPublishAsync();

        old.Publication!.Dispose();
        old.Publication.Dispose();
        Producer candidate = current.Candidate();
        await current.Replace(candidate).WaitAsync(Guard);
        Assert.Equal(1, candidate.StartCalls);
        Assert.False(current.Lease!.Completed.IsCompleted);
        Assert.False(current.Lease.Revoked.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => old.Registry.PublishVideoSource(old.Lease!, old.Source));
    }

    [Fact]
    public async Task Publication_And_Its_Dispose_Do_Not_Start_Stop_Read_Or_Complete_The_Child()
    {
        await using Fixture fixture = new();
        await fixture.InstallAndPublishAsync();
        Task childCompletion = fixture.Registration.VideoCompletion;

        Assert.Equal(1, fixture.Initial.StartCalls);
        Assert.Equal(0, fixture.Initial.ReadCalls);
        Assert.Equal(0, fixture.Initial.StopCalls);
        Assert.Equal(0, fixture.Initial.DisposeCalls);
        Assert.Equal(0, fixture.Initial.CompletionReads);
        fixture.Publication!.Dispose();
        fixture.Publication.Dispose();
        Assert.Equal(1, fixture.Initial.StartCalls);
        Assert.Equal(0, fixture.Initial.ReadCalls);
        Assert.Equal(0, fixture.Initial.StopCalls);
        Assert.Equal(0, fixture.Initial.DisposeCalls);
        Assert.Equal(0, fixture.Initial.CompletionReads);
        Assert.False(childCompletion.IsCompleted);
        Assert.Same(fixture.Lease!.Completed, childCompletion);
        Assert.Same(childCompletion, fixture.Registration.VideoCompletion);
        Assert.False(fixture.Source.Completion.IsCompleted);
        Assert.False(fixture.Lease.Revoked.IsCompleted);

        // 摘槽不夺走 source；原拥有者仍可独立操作并承担最终 join。
        Producer direct = fixture.Candidate();
        await fixture.InstallDirectAsync(direct).WaitAsync(Guard);
        Assert.Equal(1, direct.StartCalls);
        AssertRetiredOnce(fixture.Initial);
    }

    [Fact]
    public async Task Replacement_Returns_The_Exact_Source_Task_And_Unpublication_Does_Not_Reclaim_It()
    {
        using ManualResetEventSlim releaseStart = new();
        await using Fixture fixture = new();
        await fixture.InstallAndPublishAsync();
        Producer candidate = fixture.Candidate();
        candidate.OnStart = () => releaseStart.Wait();
        try
        {
            Task replacing = fixture.Replace(candidate);
            await candidate.StartEntered.Task.WaitAsync(Guard);
            Task original = SourceReplacement(fixture.Source);
            Assert.Same(original, replacing);
            Assert.False(replacing.IsCompleted);
            fixture.Publication!.Dispose();
            Assert.False(replacing.IsCompleted);
            Assert.Equal(0, candidate.StopCalls);
            Assert.Equal(0, candidate.DisposeCalls);
            Assert.False(fixture.Lease!.Completed.IsCompleted);

            releaseStart.Set();
            await replacing.WaitAsync(Guard);
            Assert.Equal(1, candidate.StartCalls);
            Assert.Equal(0, candidate.StopCalls);
            Assert.Equal(0, candidate.DisposeCalls);
            Assert.False(fixture.Source.Completion.IsCompleted);
            Assert.False(fixture.Registration.VideoCompletion.IsCompleted);
        }
        finally
        {
            releaseStart.Set();
        }
    }

    [Fact]
    public async Task Accepted_Start_Failure_Is_Asynchronous_Preserves_The_Error_Tree_And_Ownership()
    {
        await using Fixture fixture = new() { ExpectSourceFailure = true };
        await fixture.InstallAndPublishAsync();
        AggregateException nested = new("嵌套启动错误。", new IOException("原始叶子。"));
        AggregateException startTree = new("启动错误树。", nested, new IOException("另一分支。"));
        Producer candidate = fixture.Candidate();
        candidate.OnStart = () => throw startTree;
        Task? replacing = null;

        Assert.Null(Record.Exception(() => { replacing = fixture.Replace(candidate); }));
        Assert.NotNull(replacing);
        Assert.Same(SourceReplacement(fixture.Source), replacing);
        AggregateException error = Assert.IsType<AggregateException>(await ObserveAsync(replacing));
        Assert.Same(startTree, Assert.Single(error.InnerExceptions));
        Assert.Same(nested, startTree.InnerExceptions[0]);
        AggregateException terminal = Assert.IsType<AggregateException>(await ObserveAsync(fixture.Source.Completion));
        Assert.Same(startTree, Assert.Single(terminal.InnerExceptions));
        AssertRetiredOnce(candidate);
        Assert.Equal(1, candidate.StartCalls);
        Assert.Equal(0, candidate.ReadCalls);
        Assert.False(fixture.Lease!.Completed.IsCompleted);
        fixture.Publication!.Dispose();
        AssertRetiredOnce(candidate);
    }

    [Fact]
    public async Task Retirement_Failure_Preserves_Original_Task_Error_Trees_And_Cleans_Unstarted_Candidate()
    {
        await using Fixture fixture = new() { ExpectSourceFailure = true };
        AggregateException stopTree = new("旧代停止树。", new IOException("停止叶子。"),
            new AggregateException("停止子树。", new IOException("深层叶子。")));
        IOException disposeError = new("旧代释放错误。");
        IOException completionError = new("旧代完成错误。");
        AggregateException candidateTree = new("候选清理树。", new IOException("候选叶子。"));
        Task stop = fixture.Track(Task.FromException(stopTree));
        Task dispose = fixture.Track(Task.FromException(disposeError));
        Task completion = fixture.Track(Task.FromException(completionError));
        Task candidateCleanup = fixture.Track(Task.FromException(candidateTree));
        Producer initial = fixture.Candidate();
        initial.Stop = () => stop;
        initial.Dispose = () => dispose;
        initial.GetCompletion = () => completion;
        await fixture.InstallAndPublishAsync(initial);
        Producer candidate = fixture.Candidate();
        candidate.Stop = () => candidateCleanup;
        candidate.Dispose = () => candidateCleanup;
        candidate.GetCompletion = () => candidateCleanup;

        Task replacing = fixture.Replace(candidate);
        Task original = SourceReplacement(fixture.Source);
        Assert.Same(original, replacing);
        AggregateException error = Assert.IsType<AggregateException>(await ObserveAsync(replacing));
        Assert.Same(error, await ObserveAsync(original));
        Assert.Collection(error.InnerExceptions,
            value => Assert.Same(stopTree, value),
            value => Assert.Same(disposeError, value),
            value => Assert.Same(completionError, value),
            value => Assert.Same(candidateTree, value));
        AggregateException terminal = Assert.IsType<AggregateException>(await ObserveAsync(fixture.Source.Completion));
        Assert.Equal(error.InnerExceptions.ToArray(), terminal.InnerExceptions.ToArray());
        Assert.Equal(0, candidate.StartCalls);
        Assert.Equal(0, candidate.ReadCalls);
        AssertRetiredOnce(initial);
        AssertRetiredOnce(candidate);
        Assert.False(fixture.Lease!.Completed.IsCompleted);
    }

    [Fact]
    public async Task Overlapping_Replacement_Rejects_Synchronously_Without_Taking_The_New_Candidate()
    {
        using ManualResetEventSlim releaseStart = new();
        await using Fixture fixture = new();
        await fixture.InstallAndPublishAsync();
        Producer accepted = fixture.Candidate();
        accepted.OnStart = () => releaseStart.Wait();
        Producer rejected = fixture.Candidate();
        try
        {
            Task replacing = fixture.Replace(accepted);
            await accepted.StartEntered.Task.WaitAsync(Guard);
            Assert.Throws<InvalidOperationException>(() => { _ = fixture.Replace(rejected); });
            Assert.Throws<InvalidOperationException>(() => { _ = fixture.Replace(accepted); });
            Assert.Same(replacing, SourceReplacement(fixture.Source));
            AssertUntouched(rejected);
            Assert.Equal(1, accepted.StartCalls);
            Assert.Equal(0, accepted.StopCalls);
            Assert.False(replacing.IsCompleted);
            releaseStart.Set();
            await replacing.WaitAsync(Guard);
            AssertUntouched(rejected);
            Assert.Equal(0, accepted.StopCalls);
        }
        finally
        {
            releaseStart.Set();
        }
    }

    [Theory]
    [InlineData("start")]
    [InlineData("read")]
    [InlineData("stop")]
    public async Task External_Producer_Entrances_Do_Not_Hold_The_Registry_Gate(string entrance)
    {
        using ManualResetEventSlim release = new();
        RecordingVideoOwner owner = new(32);
        VideoFrameTestData.Payload().CopyTo(owner.Bytes, 0);
        using EncodedFrame frame = VideoFrameTestData.Frame(owner);
        await using Fixture fixture = new();
        object registryGate = RegistryGate(fixture.Registry);
        TaskCompletionSource entered = Signal();
        void Probe()
        {
            Assert.False(Monitor.IsEntered(registryGate));
            entered.TrySetResult();
            release.Wait();
        }
        Producer initial = fixture.Candidate();
        if (entrance == "stop") initial.Stop = () => { Probe(); return Task.CompletedTask; };
        await fixture.InstallAndPublishAsync(initial);
        Producer candidate = fixture.Candidate();
        if (entrance == "start") candidate.OnStart = Probe;
        candidate.Read = _ =>
        {
            if (entrance == "read") Probe();
            return ValueTask.FromResult<EncodedFrame?>(frame);
        };
        try
        {
            Task replacing = fixture.Replace(candidate);
            Task<EncodedFrame?>? reading = null;
            if (entrance == "read")
            {
                await replacing.WaitAsync(Guard);
                reading = fixture.Track(fixture.Source.ReadNextAsync(fixture.SessionId, default).AsTask());
            }
            await entered.Task.WaitAsync(Guard);
            Task registryProbe = fixture.Track(Task.Run(() =>
            {
                Assert.Equal(1, fixture.Registry.ActiveSessionCount);
                Assert.Same(fixture.Lease!.Completed, fixture.Registration.VideoCompletion);
            }));
            // 外部入口仍被 gate 卡住，独立线程已完成真实登记表访问，不能用同线程锁重入冒充。
            await registryProbe.WaitAsync(Guard);
            Assert.False(entrance == "read" ? reading!.IsCompleted : replacing.IsCompleted);
            release.Set();
            await replacing.WaitAsync(Guard);
            if (reading is not null) Assert.Same(frame, await reading.WaitAsync(Guard));
            AssertRetiredOnce(initial);
            Assert.Equal(1, candidate.StartCalls);
        }
        finally
        {
            release.Set();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Blocking_Start_Allows_Unpublication_Or_Unregister_But_Still_Requires_Full_Join(bool unregister)
    {
        using ManualResetEventSlim releaseStart = new();
        await using Fixture fixture = new();
        await fixture.InstallAndPublishAsync();
        TaskCompletionSource startExited = Signal();
        TaskCompletionSource stopTail = Signal();
        TaskCompletionSource disposeTail = Signal();
        TaskCompletionSource completionTail = Signal();
        _ = fixture.Track(stopTail.Task);
        _ = fixture.Track(disposeTail.Task);
        _ = fixture.Track(completionTail.Task);
        Producer candidate = fixture.Candidate();
        candidate.OnStart = () => { releaseStart.Wait(); startExited.TrySetResult(); };
        candidate.Stop = () => stopTail.Task;
        candidate.Dispose = () => disposeTail.Task;
        candidate.GetCompletion = () => completionTail.Task;
        try
        {
            Task replacing = fixture.Replace(candidate);
            await candidate.StartEntered.Task.WaitAsync(Guard);
            Task removing = fixture.Track(Task.Run(() =>
            {
                if (unregister) fixture.Registration.Dispose();
                else fixture.Publication!.Dispose();
            }));
            await removing.WaitAsync(Guard);
            Assert.False(replacing.IsCompleted);
            Assert.Equal(0, candidate.StopCalls);
            Assert.Equal(0, candidate.DisposeCalls);
            Assert.Equal(0, candidate.CompletionReads);
            Assert.False(fixture.Lease!.Completed.IsCompleted);
            Assert.Equal(unregister, fixture.Lease.Revoked.IsCompleted);
            Producer rejected = fixture.Candidate();
            Assert.Throws<InvalidOperationException>(() => { _ = fixture.Replace(rejected); });
            AssertUntouched(rejected);

            Task closing = fixture.Track(fixture.Source.DisposeAsync().AsTask());
            await Task.WhenAll(candidate.StopEntered.Task, candidate.DisposeEntered.Task,
                candidate.CompletionEntered.Task).WaitAsync(Guard);
            Assert.False(closing.IsCompleted);
            Assert.False(replacing.IsCompleted);
            AssertRetiredOnce(candidate);
            releaseStart.Set();
            await startExited.Task.WaitAsync(Guard);
            stopTail.SetResult();
            disposeTail.SetResult();
            Assert.False(completionTail.Task.IsCompleted);
            Assert.False(replacing.IsCompleted);
            Assert.False(closing.IsCompleted);
            Assert.False(fixture.Registration.VideoCompletion.IsCompleted);
            completionTail.SetResult();
            Assert.IsType<InvalidOperationException>(await ObserveAsync(replacing));
            await closing.WaitAsync(Guard);
            await fixture.Source.Completion.WaitAsync(Guard);
            Assert.Equal(0, candidate.ReadCalls);
            AssertRetiredOnce(candidate);
            Assert.False(fixture.Registration.VideoCompletion.IsCompleted);
            fixture.Lease.Complete();
            await fixture.Registration.VideoCompletion.WaitAsync(Guard);
        }
        finally
        {
            releaseStart.Set();
            stopTail.TrySetResult();
            disposeTail.TrySetResult();
            completionTail.TrySetResult();
        }
    }

    [Fact]
    public async Task Closed_Source_Rejection_Is_Synchronous_And_Does_Not_Transfer_Candidate_Ownership()
    {
        await using Fixture fixture = new();
        await fixture.InstallAndPublishAsync();
        await fixture.Track(fixture.Source.DisposeAsync().AsTask()).WaitAsync(Guard);
        Producer candidate = fixture.Candidate();

        Assert.Throws<ObjectDisposedException>(() => { _ = fixture.Replace(candidate); });
        AssertUntouched(candidate);
        AssertRetiredOnce(fixture.Initial);
        Assert.False(fixture.Lease!.Completed.IsCompleted);
        Assert.False(fixture.Lease.Revoked.IsCompleted);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static object RegistryGate(SessionRegistry registry) => typeof(SessionRegistry)
        .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registry)!;

    // 仅观察已有字段，保证不是 async 包装/超时代理 Task；不增加生产测试入口。
    private static Task SourceReplacement(ReplaceableVideoFrameSource source) => (Task)typeof(ReplaceableVideoFrameSource)
        .GetField("_replace", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;

    private static void AssertUntouched(Producer producer)
    {
        Assert.Equal(0, producer.StartCalls);
        Assert.Equal(0, producer.ReadCalls);
        Assert.Equal(0, producer.StopCalls);
        Assert.Equal(0, producer.DisposeCalls);
        Assert.Equal(0, producer.CompletionReads);
    }

    private static void AssertRetiredOnce(Producer producer)
    {
        Assert.Equal(1, producer.StopCalls);
        Assert.Equal(1, producer.DisposeCalls);
        Assert.Equal(1, producer.CompletionReads);
    }

    private static async Task<Exception?> ObserveAsync(Task operation)
    {
        try
        {
            await operation.WaitAsync(Guard);
            return null;
        }
        catch (TimeoutException)
        {
            // Guard 仅防止测试卡死，不能伪装成预期生产错误。
            throw;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private static readonly IPAddress Remote = IPAddress.Parse("192.168.10.20");
        private static readonly byte[] Token = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        private static readonly byte[] Pin = Enumerable.Range(64, 32).Select(value => (byte)value).ToArray();
        private static readonly byte[] Nonce = Enumerable.Range(128, 16).Select(value => (byte)value).ToArray();
        private readonly List<Task> _operations = [];
        private readonly HashSet<Producer> _unowned = [];

        internal Fixture(SessionRegistry? registry = null, Guid? sessionId = null,
            bool attach = true, ConnectionSecurityContext? video = null)
        {
            Registry = registry ?? new SessionRegistry();
            SessionId = sessionId ?? Guid.NewGuid();
            Video = video ?? new ConnectionSecurityContext(IPAddress.Parse("192.168.10.10"), Remote,
                23456, SslProtocols.Tls13, Pin);
            AttachWindow = new VideoAttachWindow(Clock, 15_000);
            Registration = Registry.Register(SessionId, Guid.NewGuid(), Guid.NewGuid(), "视频发布接缝测试",
                SessionPermission.ViewOnly, Remote, 12345, Token, AttachWindow,
                Pin, Control.Token, V2AgreementFixture.Create(SessionId));
            if (attach) Attach();
            Source = new ReplaceableVideoFrameSource(SessionId);
        }

        internal Guid SessionId { get; }
        internal SessionRegistry Registry { get; }
        internal SessionRegistry.SessionRegistration Registration { get; }
        internal ReplaceableVideoFrameSource Source { get; }
        internal ConnectionSecurityContext Video { get; }
        internal ManualDeadlineClock Clock { get; } = new();
        internal VideoAttachWindow AttachWindow { get; }
        internal CancellationTokenSource Control { get; } = new();
        internal VideoAttachLease? Lease { get; private set; }
        internal IDisposable? Publication { get; private set; }
        internal Producer Initial { get; private set; } = null!;
        internal bool ExpectSourceFailure { get; init; }

        internal void Attach()
        {
            // 独立拼装 83 字节 transcript，不调用生产证明 helper。
            byte[] transcript = new byte[83];
            Encoding.UTF8.GetBytes("LANREMOTE-VIDEO-V1\0").CopyTo(transcript, 0);
            Convert.FromHexString(SessionId.ToString("N")).CopyTo(transcript, 19);
            Nonce.CopyTo(transcript, 35);
            Pin.CopyTo(transcript, 51);
            using HMACSHA256 hmac = new(Token);
            byte[] proof = hmac.ComputeHash(transcript);
            VideoAttachStatus status = Registry.TryAttachVideo(SessionId, Video, Nonce, proof, default, out var lease);
            Lease = lease;
            Assert.Equal(VideoAttachStatus.Attached, status);
            Assert.NotNull(Lease);
        }

        internal Producer Candidate()
        {
            Producer candidate = new();
            _unowned.Add(candidate);
            return candidate;
        }

        internal T Track<T>(T task) where T : Task
        {
            _operations.Add(task);
            return task;
        }

        internal Task Replace(Producer candidate)
        {
            Task original = Registration.ReplaceVideoProducerAsync(candidate);
            // 只有正常返回才转移；同步异常仍由夹具作为调用方释放。
            _unowned.Remove(candidate);
            return Track(original);
        }

        internal Task InstallDirectAsync(Producer producer)
        {
            Task original = Source.ReplaceAsync(producer);
            _unowned.Remove(producer);
            return Track(original);
        }

        internal async Task InstallAndPublishAsync(Producer? initial = null)
        {
            Initial = initial ?? Candidate();
            await InstallDirectAsync(Initial).WaitAsync(Guard);
            Publication = Registry.PublishVideoSource(Lease!, Source);
        }

        internal void Invalidate(string reason)
        {
            switch (reason)
            {
                case "control-cancel": Control.Cancel(); break;
                case "unregister": Registration.Dispose(); break;
                case "child-complete": Lease!.Complete(); break;
                default: throw new ArgumentOutOfRangeException(nameof(reason));
            }
        }

        public async ValueTask DisposeAsync()
        {
            // 测试 finally 必须先放行所有 gate。原操作并行观察，任一超时都必须使测试失败。
            Publication?.Dispose();
            Registration.Dispose();
            Task closing = Source.DisposeAsync().AsTask();
            try
            {
                await Task.WhenAll(_operations.Append(closing).Append(Source.Completion).Distinct()
                    .Select(ObserveAsync));
                if (!ExpectSourceFailure) Assert.Null(await ObserveAsync(Source.Completion));
                Assert.Equal(0, Clock.TimerCount);
            }
            finally
            {
                if (Source.Completion.IsCompleted) Lease?.Complete();
                Control.Dispose();
                foreach (Producer producer in _unowned)
                {
                    try
                    {
                        // 连 source 的完整收尾也不能擅自回收同步拒绝的候选。
                        AssertUntouched(producer);
                    }
                    finally
                    {
                        await producer.DisposeAsync().AsTask().WaitAsync(Guard);
                        await producer.Completion.WaitAsync(Guard);
                    }
                }
            }
        }
    }

    private sealed class Producer : IVideoFrameProducer
    {
        private readonly TaskCompletionSource _lifetime = Signal();
        private int _startCalls;
        private int _readCalls;
        private int _stopCalls;
        private int _disposeCalls;
        private int _completionReads;

        internal Action? OnStart { get; set; }
        internal Func<CancellationToken, ValueTask<EncodedFrame?>> Read { get; set; } =
            _ => ValueTask.FromResult<EncodedFrame?>(null);
        internal Func<Task>? Stop { get; set; }
        internal Func<Task>? Dispose { get; set; }
        internal Func<Task>? GetCompletion { get; set; }
        internal TaskCompletionSource StartEntered { get; } = Signal();
        internal TaskCompletionSource StopEntered { get; } = Signal();
        internal TaskCompletionSource DisposeEntered { get; } = Signal();
        internal TaskCompletionSource CompletionEntered { get; } = Signal();
        internal int StartCalls => Volatile.Read(ref _startCalls);
        internal int ReadCalls => Volatile.Read(ref _readCalls);
        internal int StopCalls => Volatile.Read(ref _stopCalls);
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        internal int CompletionReads => Volatile.Read(ref _completionReads);

        public void Start()
        {
            Interlocked.Increment(ref _startCalls);
            StartEntered.TrySetResult();
            OnStart?.Invoke();
        }

        public ValueTask<EncodedFrame?> ReadNextAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readCalls);
            return Read(cancellationToken);
        }

        public Task StopAsync()
        {
            Interlocked.Increment(ref _stopCalls);
            StopEntered.TrySetResult();
            if (Stop is not null) return Stop();
            _lifetime.TrySetResult();
            return _lifetime.Task;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            DisposeEntered.TrySetResult();
            if (Dispose is not null) return new(Dispose());
            _lifetime.TrySetResult();
            return new(_lifetime.Task);
        }

        public Task Completion
        {
            get
            {
                Interlocked.Increment(ref _completionReads);
                CompletionEntered.TrySetResult();
                return GetCompletion is not null ? GetCompletion() : _lifetime.Task;
            }
        }
    }
}
