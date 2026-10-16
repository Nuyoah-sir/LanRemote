using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using LanRemote.Core.Models;
using Xunit.Sdk;
using BudgetEnd = LanRemote.Transport.ControlFrameBudget.BudgetEnd;

namespace LanRemote.Transport.Tests;

public sealed class HostControlWriterTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private const int TestTimeout = 60_000;
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact(Timeout = TestTimeout)]
    public async Task ConstructorValidatesAndIdleStopNeverTouchesBorrowedStreamOrClock()
    {
        Fixture f = new();
        try
        {
            Assert.Throws<ArgumentNullException>(() => new HostControlWriter(null!, f.Session, f.Clock, f.Budget, f.Close));
            Assert.Throws<ArgumentNullException>(() => new HostControlWriter(f.Stream, f.Session, null!, f.Budget, f.Close));
            Assert.Throws<ArgumentNullException>(() => new HostControlWriter(f.Stream, f.Session, f.Clock, f.Budget, null!));
            Assert.Throws<ArgumentException>(() => new HostControlWriter(f.Stream, Guid.Empty, f.Clock, f.Budget, f.Close));
            Assert.Throws<ArgumentOutOfRangeException>(() => new HostControlWriter(f.Stream, f.Session, f.Clock, f.Budget, f.Close, -1));
            foreach (TimeSpan budget in new[] { TimeSpan.FromTicks(-1), ControlFrameBudget.MaxBudget + TimeSpan.FromTicks(1) })
                Assert.Throws<ArgumentOutOfRangeException>(() => new HostControlWriter(f.Stream, f.Session, f.Clock, budget, f.Close));
            Assert.Equal(f.Session, f.Writer.SessionId);
            Assert.False(f.Writer.HasActiveWrite);
            Assert.False(f.Writer.Completion.IsCompleted);
            Assert.False(f.Close.Task.IsCompleted);
            Assert.Equal(0, f.Clock.Calls);
            f.Writer.RequestStop();
            f.Writer.RequestStop();
            await JoinAsync(f.Writer.Completion);
            Assert.True(f.Writer.StopRequired.IsCompletedSuccessfully);
            Assert.True(f.Close.Task.IsCompletedSuccessfully);
            Assert.Throws<InvalidOperationException>(() => { _ = f.Writer.WriteCatalogAsync(f.Source); });
            await f.Writer.DisposeAsync();
            Assert.Empty(f.Calls);
            Assert.Equal(0, f.Clock.Calls);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task CatalogAndRepliesShareSequenceAndWriteIndependentBigEndianJsonWithAssignedMetadata()
    {
        Fixture f = new();
        DisplayChangedFrame template = f.Reply();
        try
        {
            f.Clock.UtcMilliseconds = 900;
            HostControlWriter.WriteResult result = await JoinAsync(f.CatalogAsync());
            DisplayCatalogFrame assigned = Assert.IsType<DisplayCatalogFrame>(result.Catalog);
            Assert.NotSame(f.Catalog, assigned);
            Assert.Equal(0, result.Sequence);
            Assert.Equal(0, assigned.Sequence);
            Assert.Equal(900, assigned.TimestampMs);
            Assert.Equal(f.Catalog.Displays.ToArray(), assigned.Displays.ToArray());
            Assert.Equal(91, f.Catalog.Sequence);
            Assert.Equal(92, f.Catalog.TimestampMs);

            // 墙钟可以倒退，序号和预算仍由各自的单调机制决定。
            f.Clock.UtcMilliseconds = 700;
            await JoinAsync(f.ReplyAsync(template));
            f.Clock.UtcMilliseconds = 1200;
            await JoinAsync(f.ReplyAsync(template));

            WriteCall[] writes = f.Stream.Writes;
            Assert.Equal(3, writes.Length);
            byte[] literal = """{"type":"host_status","protocol":1,"seq":0,"timestampMs":900,"displays":[{"id":0,"name":"Screen A","x":-100,"y":20,"width":1920,"height":1080,"isPrimary":true}]}"""u8.ToArray();
            Assert.Equal(literal, writes[0].Copy.AsSpan(4).ToArray());
            using (JsonDocument catalog = Parse(writes[0]))
            {
                Assert.Equal("host_status", catalog.RootElement.GetProperty("type").GetString());
                Assert.Equal(assigned.Sequence, catalog.RootElement.GetProperty("seq").GetInt64());
                Assert.Equal(assigned.TimestampMs, catalog.RootElement.GetProperty("timestampMs").GetInt64());
            }
            for (int i = 1; i < writes.Length; i++)
            {
                using JsonDocument document = Parse(writes[i]);
                JsonElement root = document.RootElement;
                Assert.Equal("display_changed", root.GetProperty("type").GetString());
                Assert.Equal(1, root.GetProperty("protocol").GetInt32());
                Assert.Equal(f.Session.ToString("D"), root.GetProperty("sessionId").GetString());
                Assert.Equal(i, root.GetProperty("seq").GetInt64());
                Assert.Equal(i == 1 ? 700 : 1200, root.GetProperty("timestampMs").GetInt64());
                Assert.Equal(template.RequestSequence, root.GetProperty("requestSequence").GetInt64());
                Assert.Equal(template.CatalogSequence, root.GetProperty("catalogSequence").GetInt64());
                Assert.Equal(template.DisplayId.Value, root.GetProperty("displayId").GetInt32());
                Assert.Equal(ulong.MaxValue, root.GetProperty("firstFrameId").GetUInt64());
                Assert.Equal(template.Width, root.GetProperty("width").GetInt32());
                Assert.Equal(template.Height, root.GetProperty("height").GetInt32());
            }
            Assert.Equal(81, template.Sequence);
            Assert.Equal(82, template.TimestampMs);
            Assert.Equal(new[] { "source", "write", "flush", "write", "flush", "write", "flush" }, f.Calls);
            Assert.All(writes, write => AssertCleared(write.Array));
            Assert.Equal(3, f.Clock.Timers.Length);
            Assert.All(f.Clock.Timers, timer => Assert.Equal(1, timer.DisposeCount));
            Assert.False(f.Writer.HasActiveWrite);
            Assert.False(f.Writer.StopRequired.IsCompleted);
            Assert.False(f.Close.Task.IsCompleted);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task WrongSessionAndPreCanceledRequestsNeverConsumeSequenceOrCloseWriter()
    {
        Fixture f = new(firstSequence: 7);
        using CancellationTokenSource caller = new();
        caller.Cancel();
        try
        {
            Assert.Throws<ArgumentNullException>(() => { _ = f.Writer.WriteCatalogAsync(null!); });
            Assert.Throws<ArgumentNullException>(() => { _ = f.Writer.WriteDisplayChangedAsync(null!); });
            Assert.Throws<ArgumentException>(() => { _ = f.Writer.WriteDisplayChangedAsync(f.Reply(new Guid("db26aa31-722b-4f77-961d-489d31798f44"))); });
            Assert.Throws<OperationCanceledException>(() => { _ = f.Writer.WriteCatalogAsync(f.Source, caller.Token); });
            Assert.Throws<OperationCanceledException>(() => { _ = f.Writer.WriteDisplayChangedAsync(f.Reply(), caller.Token); });
            Assert.Empty(f.Calls);
            Assert.Equal(0, f.Clock.Calls);
            Assert.False(f.Writer.StopRequired.IsCompleted);
            Assert.False(f.Close.Task.IsCompleted);
            Assert.Equal(7, (await JoinAsync(f.CatalogAsync())).Sequence);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task MaximumSequenceIsSentOnceAndOnlyFollowingRequestSealsWriter()
    {
        Fixture f = new(firstSequence: long.MaxValue - 1);
        try
        {
            Assert.Equal(long.MaxValue - 1, (await JoinAsync(f.CatalogAsync())).Sequence);
            await JoinAsync(f.ReplyAsync());
            using JsonDocument last = Parse(f.Stream.Writes[1]);
            Assert.Equal(long.MaxValue, last.RootElement.GetProperty("seq").GetInt64());
            Assert.False(f.Writer.StopRequired.IsCompleted);
            Assert.False(f.Close.Task.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => { _ = f.Writer.WriteCatalogAsync(f.Source); });
            Assert.True(f.Writer.StopRequired.IsCompletedSuccessfully);
            Assert.True(f.Close.Task.IsCompletedSuccessfully);
            await JoinAsync(f.Writer.Completion);
            Assert.Throws<InvalidOperationException>(() => { _ = f.Writer.WriteDisplayChangedAsync(f.Reply()); });
            Assert.Equal(2, f.Stream.Writes.Length);
            Assert.Equal(1, f.Calls.Count(call => call == "source"));
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("source")]
    [InlineData("write")]
    [InlineData("flush")]
    [InlineData("clock")]
    [InlineData("dispose")]
    public async Task BusyKeepsSlotThroughOriginalStageAndDoesNotQueueOrConsumeSequence(string stage)
    {
        Fixture f = new();
        Pause? clock = stage == "clock" ? f.NewPause() : null;
        Held? held = stage == "clock" ? null : f.Hold(stage);
        if (clock is not null) f.Clock.ReadUtc = () => { clock.Wait(); return 900; };
        try
        {
            Task<HostControlWriter.WriteResult> sending = f.CatalogAsync();
            if (clock is not null) await ReachedAsync(clock.Reached.Task, sending);
            else
            {
                Task original = await JoinAsync(held!.Published.Task);
                await AssertAwaitPathAsync(original, stage == "dispose" ? f.Deadline.Completion! : f.Operation);
            }
            Assert.True(f.Writer.HasActiveWrite);
            int calls = f.Calls.Length, clockCalls = f.Clock.Calls;
            Assert.Throws<InvalidOperationException>(() => { _ = f.Writer.WriteCatalogAsync(f.Source); });
            Assert.Throws<InvalidOperationException>(() => { _ = f.Writer.WriteDisplayChangedAsync(f.Reply()); });
            Assert.Equal(calls, f.Calls.Length);
            if (clock is not null) Assert.Equal(clockCalls, f.Clock.Calls);
            Assert.False(sending.IsCompleted);
            Assert.False(f.Writer.StopRequired.IsCompleted);
            Assert.False(f.Close.Task.IsCompleted);
            if (stage == "write") Assert.Equal(f.Stream.Writes[0].Copy, f.Stream.Writes[0].Array);
            if (stage == "flush" || stage == "dispose") AssertCleared(f.Stream.Writes[0].Array);
            clock?.Open();
            held?.Complete();
            Assert.Equal(0, (await JoinAsync(sending)).Sequence);
            f.ClearHooks();
            await JoinAsync(f.ReplyAsync());
            using JsonDocument next = Parse(f.Stream.Writes[1]);
            Assert.Equal(1, next.RootElement.GetProperty("seq").GetInt64());
            Assert.Equal(1, f.Calls.Count(call => call == "source"));
            Assert.Equal(2, f.Stream.Writes.Length);
            Assert.False(f.Writer.HasActiveWrite);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("source", "stop")]
    [InlineData("source", "budget")]
    [InlineData("write", "stop")]
    [InlineData("write", "cancel")]
    [InlineData("write", "budget")]
    [InlineData("flush", "cancel")]
    public async Task StopCancelAndBudgetNotifyButJoinUncooperativeOriginalBeforeCompletion(string stage, string reason)
    {
        Fixture f = new();
        Held held = f.Hold(stage);
        using CancellationTokenSource caller = new();
        try
        {
            Task<HostControlWriter.WriteResult> sending = f.CatalogAsync(caller.Token);
            Task original = await JoinAsync(held.Published.Task);
            await AssertAwaitPathAsync(original, f.Operation);
            await f.InterruptAsync(reason, caller);
            await JoinAsync(f.Writer.StopRequired);
            Assert.True(f.Close.Task.IsCompletedSuccessfully);
            await AssertAwaitPathAsync(original, sending);
            await AssertAwaitPathAsync(original, f.Writer.Completion);
            Assert.True(f.Writer.HasActiveWrite);
            Assert.False(original.IsCompleted);
            Assert.False(sending.IsCompleted);
            Assert.False(f.Writer.Completion.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => { _ = f.Writer.WriteDisplayChangedAsync(f.Reply()); });
            if (stage == "write") Assert.Equal(f.Stream.Writes[0].Copy, f.Stream.Writes[0].Array);
            held.Complete();
            if (reason == "budget")
            {
                Assert.IsType<AggregateException>(await FaultAsync(sending));
                Assert.IsType<AggregateException>(await FaultAsync(f.Writer.Completion));
            }
            else
            {
                Assert.IsAssignableFrom<OperationCanceledException>(await FaultAsync(sending));
                await JoinAsync(f.Writer.Completion);
            }
            Assert.False(f.Writer.HasActiveWrite);
            Assert.Equal(CallsThrough(stage), f.Calls);
            Assert.All(f.Stream.Writes, write => AssertCleared(write.Array));
            Assert.True(f.Attempt.Snapshot.AllOwnedRootsReleased);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("source", "cancel")]
    [InlineData("write", "budget")]
    [InlineData("flush", "stop")]
    public async Task BlockedSynchronousPrefixKeepsOriginalWorkerAndLateTaskJoined(string stage, string reason)
    {
        Fixture f = new();
        Pause prefix = f.NewPause();
        Held held = f.Hold(stage, prefix);
        using CancellationTokenSource caller = new();
        try
        {
            Task<HostControlWriter.WriteResult> sending = f.CatalogAsync(caller.Token);
            await ReachedAsync(prefix.Reached.Task, sending);
            Assert.False(held.Published.Task.IsCompleted);
            await f.InterruptAsync(reason, caller);
            await JoinAsync(f.Writer.StopRequired);
            await AssertAwaitPathAsync(f.Operation, sending);
            Assert.True(f.Close.Task.IsCompletedSuccessfully);
            Assert.False(sending.IsCompleted);
            Assert.True(f.Writer.HasActiveWrite);
            if (stage == "write") Assert.Equal(f.Stream.Writes[0].Copy, f.Stream.Writes[0].Array);
            prefix.Open();
            Task original = await JoinAsync(held.Published.Task);
            Assert.Same(held.Original, original);
            await AssertAwaitPathAsync(original, sending);
            await AssertAwaitPathAsync(original, f.Writer.Completion);
            Assert.False(sending.IsCompleted);
            held.Complete();
            _ = await FaultAsync(sending);
            Assert.Equal(CallsThrough(stage), f.Calls);
            Assert.False(f.Attempt.Snapshot.OperationExitUnproven);
            Assert.True(f.Attempt.Snapshot.AllOwnedRootsReleased);
            Assert.All(f.Stream.Writes, write => AssertCleared(write.Array));
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task LateSourceErrorTreeAndTimerCleanupFailureAreBothPreservedAfterStop()
    {
        Fixture f = new();
        Held source = f.Hold("source"), dispose = f.Hold("dispose");
        Exception leaf = new IOException("重复的原来源叶节点");
        Exception branch = new AggregateException("原来源分支", new OperationCanceledException("非合作取消"), leaf);
        Exception cleanup = new IOException("原timer清理错误");
        try
        {
            Task<HostControlWriter.WriteResult> sending = f.CatalogAsync();
            await JoinAsync(source.Published.Task);
            await AssertAwaitPathAsync(source.Original, f.Operation);
            await f.InterruptAsync("stop", null);
            await JoinAsync(dispose.Published.Task);
            await AssertAwaitPathAsync(source.Original, sending);
            await AssertAwaitPathAsync(dispose.Original, sending);
            source.Fail(leaf, branch, leaf);
            await JoinAsync(f.Operation);
            Assert.False(sending.IsCompleted);
            Assert.False(f.Writer.Completion.IsCompleted);
            dispose.Fail(cleanup);
            AggregateException error = Assert.IsType<AggregateException>(await FaultAsync(sending));
            Assert.Contains(error.InnerExceptions, item => item is AggregateException tree &&
                tree.InnerExceptions.Count == 3 && ReferenceEquals(tree.InnerExceptions[0], leaf) &&
                ReferenceEquals(tree.InnerExceptions[1], branch) && ReferenceEquals(tree.InnerExceptions[2], leaf));
            Assert.Contains(error.InnerExceptions, item => ReferenceEquals(item, cleanup));
            Assert.Same(error, await FaultAsync(f.Writer.Completion));
            Assert.Empty(f.Stream.Writes);
            Assert.False(f.Attempt.Snapshot.AllOwnedRootsReleased);
            Assert.True(f.Attempt.Snapshot.OriginalOperationsFinished);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ThrowingWritePrefixSealsWriterWithoutClearingAnUnprovenBorrow()
    {
        Fixture f = new();
        Exception failure = new IOException("写同步前缀未交还原Task");
        f.Stream.WriteHook = _ => throw failure;
        try
        {
            AggregateException error = Assert.IsType<AggregateException>(await FaultAsync(f.CatalogAsync()));
            Assert.Contains(error.InnerExceptions, item => ReferenceEquals(item, failure));
            Assert.True(f.Writer.StopRequired.IsCompletedSuccessfully);
            Assert.True(f.Close.Task.IsCompletedSuccessfully);
            var snapshot = f.Attempt.Snapshot;
            Assert.True(snapshot.OperationExitUnproven);
            Assert.True(snapshot.WireBorrowExitUnproven);
            Assert.True(snapshot.OriginalOperationsFinished);
            Assert.False(snapshot.AllOwnedRootsReleased);
            WriteCall write = Assert.Single(f.Stream.Writes);
            Assert.Equal(write.Copy, write.Array);
            Assert.Throws<InvalidOperationException>(() => { _ = f.Writer.WriteDisplayChangedAsync(f.Reply()); });
            Assert.Equal(new[] { "source", "write" }, f.Calls);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false, -1)]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, -1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public async Task CleanupAndFinalSampleShareExactMonotonicDeadlineWithoutTimerGrace(bool finalOnly, int offset)
    {
        Fixture f = new();
        if (finalOnly)
            f.Clock.ReadTimestamp = () => f.Attempt.Snapshot.Budget?.End == BudgetEnd.FinishedInBudget ? f.Budget.Ticks + offset : 0;
        else
            f.Clock.DisposeHook = () => { f.Clock.Now = f.Budget.Ticks + offset; return ValueTask.CompletedTask; };
        try
        {
            Task<HostControlWriter.WriteResult> sending = f.CatalogAsync();
            if (offset < 0)
            {
                Assert.Equal(0, (await JoinAsync(sending)).Sequence);
                Assert.False(f.Writer.StopRequired.IsCompleted);
                Assert.False(f.Close.Task.IsCompleted);
            }
            else
            {
                AggregateException error = Assert.IsType<AggregateException>(await FaultAsync(sending));
                Assert.Contains(error.InnerExceptions, item => item is TimeoutException);
                Assert.True(f.Writer.StopRequired.IsCompletedSuccessfully);
                Assert.True(f.Close.Task.IsCompletedSuccessfully);
                if (finalOnly) Assert.IsType<TimeoutException>(f.Attempt.Snapshot.FinalSampleError);
                else Assert.Equal(BudgetEnd.Expired, f.Attempt.Snapshot.Budget!.End);
            }
            Assert.Equal(new[] { "source", "write", "flush" }, f.Calls);
            AssertCleared(Assert.Single(f.Stream.Writes).Array);
            ProbeTimer timer = Assert.Single(f.Clock.Timers);
            Assert.Equal(1, timer.DisposeCount);
            Assert.Equal(0, timer.FireCount);
            Assert.True(f.Attempt.Snapshot.AllOwnedRootsReleased);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ZeroBudgetSealsBeforeSourceSerializationOrIo()
    {
        Fixture f = new(budget: TimeSpan.Zero);
        try
        {
            AggregateException error = Assert.IsType<AggregateException>(await FaultAsync(f.CatalogAsync()));
            Assert.Contains(error.InnerExceptions, item => item is TimeoutException);
            Assert.Empty(f.Calls);
            Assert.Empty(f.Stream.Writes);
            Assert.Empty(f.Clock.Timers);
            Assert.True(f.Writer.StopRequired.IsCompletedSuccessfully);
            Assert.True(f.Close.Task.IsCompletedSuccessfully);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateSourceAfterStopOrDeadlineDoesNotStartUtcSampling(bool deadline)
    {
        Fixture f = new();
        Held held = f.Hold("source");
        int utc = 0;
        f.Clock.ReadUtc = () => { Interlocked.Increment(ref utc); return 900; };
        try
        {
            Task<HostControlWriter.WriteResult> sending = f.CatalogAsync();
            await JoinAsync(held.Published.Task);
            await f.InterruptAsync(deadline ? "budget" : "stop", null);
            await JoinAsync(f.Writer.StopRequired);
            Assert.False(sending.IsCompleted);
            held.Complete();
            _ = await FaultAsync(sending);
            if (deadline) Assert.IsType<AggregateException>(await FaultAsync(f.Writer.Completion));
            else await JoinAsync(f.Writer.Completion);
            Assert.Equal(0, utc);
            Assert.Empty(f.Stream.Writes);
        }
        finally { await f.FinishAsync(); }
    }

    private static string[] CallsThrough(string stage) => stage switch
    {
        "source" => ["source"], "write" => ["source", "write"], _ => ["source", "write", "flush"]
    };
    private static JsonDocument Parse(WriteCall write)
    {
        Assert.Equal((uint)(write.Copy.Length - 4), BinaryPrimitives.ReadUInt32BigEndian(write.Copy));
        return JsonDocument.Parse(write.Copy.AsMemory(4));
    }
    private static void AssertCleared(byte[] bytes) => Assert.All(bytes, value => Assert.Equal((byte)0, value));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // 看门狗只诊断；正常路径和finally均直接join原Task，不使用取消包装替代所有权。
    private static async Task WatchAsync(Task original)
    {
        using CancellationTokenSource timer = new();
        Task guard = Task.Delay(Guard, timer.Token);
        await Task.WhenAny(original, guard);
        timer.Cancel();
        Assert.True(original.IsCompleted, "Guard：原任务未退出，夹具finally仍须放闸并直接join。");
    }
    private static async Task JoinAsync(Task original) { await WatchAsync(original); await original; }
    private static async Task<T> JoinAsync<T>(Task<T> original) { await WatchAsync(original); return await original; }
    private static async Task<Exception> FaultAsync(Task original)
    {
        await WatchAsync(original);
        return await Record.ExceptionAsync(() => original) ?? throw new XunitException("预期原写入失败，不允许成功。");
    }
    private static async Task ReachedAsync(Task reached, Task completion)
    {
        await WatchAsync(Task.WhenAny(reached, completion));
        Assert.True(reached.IsCompletedSuccessfully, "未到达受控同步前缀便退出。");
        Assert.False(completion.IsCompleted, "受控同步前缀未放行，原写入不得完成。");
    }
    private static async Task ObserveAsync(Task original)
    {
        try { await original; }
        catch { _ = original.Exception; }
    }

    // 与目录底座测试相同，仅承认精确awaiter、Task.Run解包及WhenAll边，排除纯通知和WhenAny。
    private static async Task AssertAwaitPathAsync(Task original, Task completion)
    {
        using CancellationTokenSource guard = new(Guard);
        while (!HasAwaitPath(original, completion, new HashSet<Task>()))
        {
            Assert.False(original.IsCompleted, "原任务提前退出，无法证明受控等待。");
            Assert.False(completion.IsCompleted, "完成任务提前退出，未join原任务。");
            if (guard.IsCancellationRequested) throw new XunitException("Guard：缺少精确await/unwrap/WhenAll链。");
            await Task.Yield();
        }
    }
    private static bool HasAwaitPath(Task current, Task target, HashSet<Task> visited)
    {
        if (!visited.Add(current)) return false;
        foreach (object continuation in Continuations(current))
        {
            object? box = continuation is Delegate action ? action.Target : continuation;
            if (box is not Task && box is not null && RuntimeField(box.GetType(), "m_action")?.GetValue(box) is Delegate scheduled)
                box = scheduled.Target;
            if (box is not Task next) continue;
            object? machine = RuntimeField(next.GetType(), "StateMachine")?.GetValue(next);
            bool exact = machine is not null && machine.GetType().GetFields(Fields)
                .Where(field => field.Name.StartsWith("<>u__", StringComparison.Ordinal))
                .Select(field => field.GetValue(machine)).OfType<object>()
                .Any(awaiter => awaiter.GetType().GetFields(Fields)
                    .Any(field => typeof(Task).IsAssignableFrom(field.FieldType) && ReferenceEquals(field.GetValue(awaiter), current)));
            string type = next.GetType().FullName ?? "";
            bool unwrap = type.StartsWith("System.Threading.Tasks.UnwrapPromise`", StringComparison.Ordinal);
            bool whenAll = type == "System.Threading.Tasks.Task+WhenAllPromise" ||
                type.StartsWith("System.Threading.Tasks.Task+WhenAllPromise`", StringComparison.Ordinal);
            if (!exact && !unwrap && !whenAll) continue;
            if (ReferenceEquals(next, target) || HasAwaitPath(next, target, visited)) return true;
        }
        return false;
    }
    private static object[] Continuations(Task task)
    {
        FieldInfo field = RuntimeField(typeof(Task), "m_continuationObject")
            ?? throw new XunitException("运行时缺少Task continuation字段。");
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
    private static T Child<T>(object owner, string name) where T : class =>
        Assert.IsAssignableFrom<T>(RuntimeField(owner.GetType(), name)!.GetValue(owner));

    private sealed class Fixture
    {
        private readonly List<Held> _held = [];
        private readonly List<Pause> _pauses = [];
        private readonly List<Task> _writes = [];
        private readonly List<Task> _helpers = [];
        private readonly List<HostDisplayCatalogSendAttempt> _attempts = [];
        private readonly ConcurrentQueue<string> _calls = new();
        internal Guid Session { get; } = new("9657f5fd-f797-44a8-a474-c93d01e2ade6");
        internal TaskCompletionSource Close { get; } = Signal();
        internal DisplayCatalogFrame Catalog { get; } = new(91, 92,
            [new DisplayInfo(new DisplayId(0), "Screen A", -100, 20, 1920, 1080, true)]);
        internal ProbeStream Stream { get; }
        internal ProbeClock Clock { get; } = new();
        internal HostControlWriter Writer { get; }
        internal TimeSpan Budget { get; }
        internal Func<ValueTask<DisplayCatalogFrame>>? SourceHook { get; set; }
        internal string[] Calls => _calls.ToArray();
        internal HostDisplayCatalogSendAttempt Attempt => Child<HostDisplayCatalogSendAttempt>(Writer, "_attempt");
        internal Task Operation => Child<Task>(Attempt, "_operation");
        internal ControlFrameBudget Deadline => Child<ControlFrameBudget>(Attempt, "_deadline");
        internal Fixture(long firstSequence = 0, TimeSpan? budget = null)
        {
            Budget = budget ?? TimeSpan.FromTicks(100);
            Stream = new(this);
            Writer = new(Stream, Session, Clock, Budget, Close, firstSequence);
        }
        internal DisplayChangedFrame Reply(Guid? session = null) =>
            new(session ?? Session, 81, 82, long.MaxValue - 9, 73, new DisplayId(255), ulong.MaxValue, 2560, 1440);
        internal ValueTask<DisplayCatalogFrame> Source()
        {
            Record("source");
            return SourceHook?.Invoke() ?? ValueTask.FromResult(Catalog);
        }
        internal void Record(string stage) => _calls.Enqueue(stage);
        internal Task<HostControlWriter.WriteResult> CatalogAsync(CancellationToken token = default)
        {
            Task<HostControlWriter.WriteResult> original = Writer.WriteCatalogAsync(Source, token);
            Track(original);
            return original;
        }
        internal Task ReplyAsync(DisplayChangedFrame? frame = null)
        {
            Task original = Writer.WriteDisplayChangedAsync(frame ?? Reply());
            Track(original);
            return original;
        }
        private void Track(Task original) { _writes.Add(original); _attempts.Add(Attempt); }
        internal Held Hold(string stage, Pause? prefix = null)
        {
            Held held = new(stage, Catalog);
            _held.Add(held);
            Task Enter()
            {
                prefix?.Wait();
                held.Published.TrySetResult(held.Original);
                return held.Original;
            }
            switch (stage)
            {
                case "source": SourceHook = () => new ValueTask<DisplayCatalogFrame>((Task<DisplayCatalogFrame>)Enter()); break;
                case "write": Stream.WriteHook = _ => new ValueTask(Enter()); break;
                case "flush": Stream.FlushHook = Enter; break;
                case "dispose": Clock.DisposeHook = () => new ValueTask(Enter()); break;
                default: throw new ArgumentOutOfRangeException(nameof(stage));
            }
            return held;
        }
        internal Pause NewPause() { Pause pause = new(); _pauses.Add(pause); return pause; }
        internal void ClearHooks()
        {
            SourceHook = null;
            Stream.WriteHook = null;
            Stream.FlushHook = null;
            Clock.ReadUtc = null;
            Clock.DisposeHook = null;
        }
        internal async Task InterruptAsync(string reason, CancellationTokenSource? caller)
        {
            if (reason == "budget")
            {
                Clock.Now = Budget.Ticks;
                Assert.Single(Clock.Timers).Fire();
            }
            else
            {
                Task helper = Task.Run(() => { if (reason == "cancel") caller!.Cancel(); else Writer.RequestStop(); });
                _helpers.Add(helper);
                await JoinAsync(helper);
            }
        }
        internal async Task FinishAsync()
        {
            // 外层先解除同步阻塞并结束原IO，再封口；不把通知或带超时的结果包装当join。
            foreach (Pause pause in _pauses) pause.Open();
            foreach (Held held in _held) held.Complete();
            Writer.RequestStop();
            try
            {
                foreach (Task helper in _helpers) await ObserveAsync(helper);
                foreach (Task original in _writes) await ObserveAsync(original);
                foreach (HostDisplayCatalogSendAttempt attempt in _attempts)
                {
                    if (attempt.Completion is { } completion) await ObserveAsync(completion);
                    if (attempt.Snapshot.OperationStarted) await ObserveAsync(Child<Task>(attempt, "_operation"));
                    if (attempt.Snapshot.Budget is not null && Child<ControlFrameBudget>(attempt, "_deadline").Completion is { } budget)
                        await ObserveAsync(budget);
                }
                foreach (Held held in _held) await ObserveAsync(held.Original);
                await ObserveAsync(Writer.Completion);
                try { await Writer.DisposeAsync(); } catch { _ = Writer.Completion.Exception; }
            }
            finally
            {
                foreach (Pause pause in _pauses) pause.Dispose();
                Assert.Empty(Stream.UnexpectedCalls);
                Assert.All(Clock.Timers, timer => Assert.Equal(0, timer.SyncDisposeCount));
            }
        }
    }

    private sealed class Held(string stage, DisplayCatalogFrame catalog)
    {
        private readonly TaskCompletionSource<DisplayCatalogFrame> _source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _io = Signal();
        internal TaskCompletionSource<Task> Published { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Original => stage == "source" ? _source.Task : _io.Task;
        internal void Complete() { if (stage == "source") _source.TrySetResult(catalog); else _io.TrySetResult(); }
        internal void Fail(params Exception[] errors) { if (stage == "source") _source.TrySetException(errors); else _io.TrySetException(errors); }
    }
    private sealed class Pause : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        internal TaskCompletionSource Reached { get; } = Signal();
        internal void Wait() { Reached.TrySetResult(); _release.Wait(); }
        internal void Open() => _release.Set();
        public void Dispose() => _release.Dispose();
    }
    private sealed class ProbeClock : TimeProvider
    {
        private readonly ConcurrentQueue<ProbeTimer> _timers = new();
        private long _now, _utc = 900;
        private int _calls;
        internal long Now { get => Volatile.Read(ref _now); set => Volatile.Write(ref _now, value); }
        internal long UtcMilliseconds { get => Volatile.Read(ref _utc); set => Volatile.Write(ref _utc, value); }
        internal int Calls => Volatile.Read(ref _calls);
        internal ProbeTimer[] Timers => _timers.ToArray();
        internal Func<long>? ReadTimestamp { get; set; }
        internal Func<long>? ReadUtc { get; set; }
        internal Func<ValueTask>? DisposeHook { get; set; }
        public override long TimestampFrequency { get { Interlocked.Increment(ref _calls); return TimeSpan.TicksPerSecond; } }
        public override long GetTimestamp() { Interlocked.Increment(ref _calls); return ReadTimestamp?.Invoke() ?? Now; }
        public override DateTimeOffset GetUtcNow()
        {
            Interlocked.Increment(ref _calls);
            return DateTimeOffset.FromUnixTimeMilliseconds(ReadUtc?.Invoke() ?? UtcMilliseconds);
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _calls);
            Assert.Equal(Timeout.InfiniteTimeSpan, dueTime);
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            ProbeTimer timer = new(callback, state, DisposeHook);
            _timers.Enqueue(timer);
            return timer;
        }
    }
    private sealed class ProbeTimer(TimerCallback callback, object? state, Func<ValueTask>? dispose) : ITimer
    {
        private int _disposes, _syncDisposes, _fires;
        internal int DisposeCount => Volatile.Read(ref _disposes);
        internal int SyncDisposeCount => Volatile.Read(ref _syncDisposes);
        internal int FireCount => Volatile.Read(ref _fires);
        internal void Fire() { Interlocked.Increment(ref _fires); callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Assert.True(dueTime > TimeSpan.Zero);
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            return true;
        }
        public ValueTask DisposeAsync() { Interlocked.Increment(ref _disposes); return dispose?.Invoke() ?? ValueTask.CompletedTask; }
        public void Dispose() { Interlocked.Increment(ref _syncDisposes); throw new XunitException("不得同步释放timer。"); }
    }
    private sealed record WriteCall(byte[] Array, byte[] Copy);
    private sealed class ProbeStream(Fixture f) : Stream
    {
        private readonly ConcurrentQueue<WriteCall> _writes = new();
        private readonly ConcurrentQueue<string> _unexpected = new();
        internal WriteCall[] Writes => _writes.ToArray();
        internal string[] UnexpectedCalls => _unexpected.ToArray();
        internal Func<ReadOnlyMemory<byte>, ValueTask>? WriteHook { get; set; }
        internal Func<Task>? FlushHook { get; set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            f.Record("write");
            Assert.Equal(CancellationToken.None, cancellationToken);
            Assert.True(MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> segment));
            Assert.NotNull(segment.Array);
            Assert.Equal(0, segment.Offset);
            Assert.Equal(segment.Array.Length, segment.Count);
            _writes.Enqueue(new(segment.Array, buffer.ToArray()));
            return WriteHook?.Invoke(buffer) ?? ValueTask.CompletedTask;
        }
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            f.Record("flush");
            Assert.Equal(CancellationToken.None, cancellationToken);
            return FlushHook?.Invoke() ?? Task.CompletedTask;
        }
        private Exception Unexpected(string operation)
        {
            _unexpected.Enqueue(operation);
            return new XunitException($"借用流不应执行{operation}。");
        }
        public override bool CanRead => throw Unexpected(nameof(CanRead));
        public override bool CanWrite => throw Unexpected(nameof(CanWrite));
        public override bool CanSeek => throw Unexpected(nameof(CanSeek));
        public override long Length => throw Unexpected(nameof(Length));
        public override long Position { get => throw Unexpected("Position.get"); set => throw Unexpected("Position.set"); }
        public override int Read(byte[] buffer, int offset, int count) => throw Unexpected("Read(byte[])");
        public override int Read(Span<byte> buffer) => throw Unexpected("Read(Span)");
        public override int ReadByte() => throw Unexpected(nameof(ReadByte));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw Unexpected("ReadAsync(byte[])");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw Unexpected("ReadAsync(Memory)");
        public override void Write(byte[] buffer, int offset, int count) => throw Unexpected("Write(byte[])");
        public override void Write(ReadOnlySpan<byte> buffer) => throw Unexpected("Write(Span)");
        public override void WriteByte(byte value) => throw Unexpected(nameof(WriteByte));
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw Unexpected("WriteAsync(byte[])");
        public override void Flush() => throw Unexpected(nameof(Flush));
        public override long Seek(long offset, SeekOrigin origin) => throw Unexpected(nameof(Seek));
        public override void SetLength(long value) => throw Unexpected(nameof(SetLength));
        public override void Close() => _unexpected.Enqueue(nameof(Close));
        protected override void Dispose(bool disposing) => _unexpected.Enqueue(nameof(Dispose));
        public override ValueTask DisposeAsync() { _unexpected.Enqueue(nameof(DisposeAsync)); return ValueTask.CompletedTask; }
    }
}
