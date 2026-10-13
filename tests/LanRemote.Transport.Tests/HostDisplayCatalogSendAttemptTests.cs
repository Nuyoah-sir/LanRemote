using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks.Sources;
using LanRemote.Core.Models;
using Xunit.Sdk;
using AttemptEnd = LanRemote.Transport.HostDisplayCatalogSendAttempt.AttemptEnd;
using OperationStage = LanRemote.Transport.HostDisplayCatalogSendAttempt.OperationStage;
using SendOutcome = LanRemote.Transport.HostDisplayCatalogSendAttempt.SendOutcome;
using BudgetEnd = LanRemote.Transport.ControlFrameBudget.BudgetEnd;

namespace LanRemote.Transport.Tests;

public sealed class HostDisplayCatalogSendAttemptTests
{
    // Guard 只防夹具挂死；有效证据来自显式闸、原句柄发布和真正 await 链，不把通知当 join。
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private const int TestTimeout = 60_000;
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact(Timeout = TestTimeout)]
    public async Task ConstructorValidatesWithoutCallingSourceStreamOrClock()
    {
        Fixture f = new();
        try
        {
            Assert.Equal("stream", Assert.Throws<ArgumentNullException>(() =>
                new HostDisplayCatalogSendAttempt(null!, f.Source, f.Clock, TimeSpan.Zero)).ParamName);
            Assert.Equal("source", Assert.Throws<ArgumentNullException>(() =>
                new HostDisplayCatalogSendAttempt(f.Stream, null!, f.Clock, TimeSpan.Zero)).ParamName);
            Assert.Equal("clock", Assert.Throws<ArgumentNullException>(() =>
                new HostDisplayCatalogSendAttempt(f.Stream, f.Source, null!, TimeSpan.Zero)).ParamName);
            foreach (TimeSpan budget in new[] { TimeSpan.MinValue, TimeSpan.FromTicks(-1),
                ControlFrameBudget.MaxBudget + TimeSpan.FromTicks(1), TimeSpan.MaxValue })
                Assert.Equal("budget", Assert.Throws<ArgumentOutOfRangeException>(() =>
                    new HostDisplayCatalogSendAttempt(f.Stream, f.Source, f.Clock, budget)).ParamName);
            _ = new HostDisplayCatalogSendAttempt(f.Stream, f.Source, f.Clock, ControlFrameBudget.MaxBudget);
            Assert.Null(f.Attempt.Completion);
            Assert.False(f.Attempt.AbortRequired.IsCompleted);
            var before = f.Attempt.Snapshot;
            Assert.NotSame(before, f.Attempt.Snapshot);
            Assert.Equal(AttemptEnd.Pending, before.End);
            Assert.False(before.OriginalOperationsFinished);
            Assert.False(before.AllOwnedRootsReleased);
            Assert.Empty(f.Calls);
            Assert.Equal(0, f.Clock.ExternalCalls);
            f.Attempt.RequestStop();
            Assert.False(before.StopRequested);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task StopBeforeStartPublishesStableCompletionWithoutExternalOperations()
    {
        Fixture f = new();
        try
        {
            f.Attempt.RequestStop();
            f.Attempt.RequestStop();
            Assert.True(f.Attempt.AbortRequired.IsCompletedSuccessfully);
            Task<SendOutcome> completion = f.Attempt.Start();
            Assert.Same(completion, f.Attempt.Completion);
            Assert.Throws<InvalidOperationException>(() => { _ = f.Attempt.Start(); });
            AssertRejected(await f.DrainAsync(), f);
            Assert.Empty(f.Calls);
            Assert.Equal(0, f.Clock.ExternalCalls);
            Assert.False(f.Attempt.Snapshot.OperationStarted);
            Assert.True(f.Attempt.Snapshot.AllOwnedRootsReleased);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task SuccessWritesExactlyOneBigEndianCatalogThenFlushesAndKeepsHistoricalResult()
    {
        Fixture f = new();
        try
        {
            Task<SendOutcome> completion = f.Attempt.Start();
            SendOutcome sent = await f.DrainAsync();
            Assert.Equal(AttemptEnd.Sent, sent.End);
            Assert.Equal(new[] { "source", "write", "flush" }, f.Calls);
            byte[] payload = f.Catalog.Serialize();
            byte[] expected = new byte[4 + payload.Length];
            BinaryPrimitives.WriteUInt32BigEndian(expected, (uint)payload.Length);
            payload.CopyTo(expected, 4);
            Assert.Equal(expected, Assert.Single(f.Stream.Writes).Copy);
            AssertCleared(f.Stream.Writes[0].Array);
            Assert.True(f.Attempt.Snapshot.Committed);
            Assert.True(f.Attempt.Snapshot.AllOwnedRootsReleased);
            Assert.Equal(BudgetEnd.FinishedInBudget, f.Attempt.Snapshot.Budget!.End);
            Assert.Equal(1, f.Clock.Timer.DisposeCount);
            Assert.False(f.Attempt.AbortRequired.IsCompleted);
            var historical = f.Attempt.Snapshot;
            f.Attempt.RequestStop();
            Assert.Same(sent, await completion);
            Assert.Same(completion, f.Attempt.Completion);
            Assert.Equal(AttemptEnd.Sent, f.Attempt.Snapshot.End);
            Assert.False(historical.StopRequested);
            Assert.True(f.Attempt.Snapshot.StopRequested);
            Assert.False(f.Attempt.AbortRequired.IsCompleted);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task SuccessWireMatchesIndependentUtf8LiteralAndBigEndianPrefix()
    {
        Fixture f = new();
        DisplayCatalogFrame catalog = new(7, 12,
            [new DisplayInfo(new DisplayId(0), "Screen A", -100, 20, 1920, 1080, true)]);
        f.SourceHook = () => new ValueTask<DisplayCatalogFrame>(catalog);
        try
        {
            _ = f.Attempt.Start();
            Assert.Equal(AttemptEnd.Sent, (await f.DrainAsync()).End);
            byte[] literal = """{"type":"host_status","protocol":1,"seq":7,"timestampMs":12,"displays":[{"id":0,"name":"Screen A","x":-100,"y":20,"width":1920,"height":1080,"isPrimary":true}]}"""u8.ToArray();
            Assert.Equal(160, literal.Length);
            byte[] expected = new byte[] { 0, 0, 0, 160 }.Concat(literal).ToArray();
            WriteCall write = Assert.Single(f.Stream.Writes);
            Assert.Equal(expected, write.Copy);
            Assert.Equal(new[] { "source", "write", "flush" }, f.Calls);
            AssertCleared(write.Array);
            Assert.True(f.Attempt.Snapshot.AllOwnedRootsReleased);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task MaximumCatalogEntriesNamesAndNumbersSendWithinPayloadLimit()
    {
        Fixture f = new();
        string name = new('界', 128);
        DisplayInfo[] displays = Enumerable.Range(0, 256).Select(index =>
            new DisplayInfo(new DisplayId(index), name, int.MinValue, int.MinValue,
                int.MaxValue, int.MaxValue, index == 255)).ToArray();
        DisplayCatalogFrame catalog = new(long.MaxValue, long.MaxValue, displays);
        f.SourceHook = () => new ValueTask<DisplayCatalogFrame>(catalog);
        try
        {
            _ = f.Attempt.Start();
            Assert.Equal(AttemptEnd.Sent, (await f.DrainAsync()).End);
            Assert.Equal(new[] { "source", "write", "flush" }, f.Calls);
            WriteCall write = Assert.Single(f.Stream.Writes);
            int payloadLength = write.Copy.Length - 4;
            Assert.InRange(payloadLength, 1, 256 * 1024);
            Assert.Equal((uint)payloadLength, BinaryPrimitives.ReadUInt32BigEndian(write.Copy));
            using JsonDocument document = JsonDocument.Parse(write.Copy.AsMemory(4));
            JsonElement root = document.RootElement;
            Assert.Equal("host_status", root.GetProperty("type").GetString());
            Assert.Equal(1, root.GetProperty("protocol").GetInt32());
            Assert.Equal(long.MaxValue, root.GetProperty("seq").GetInt64());
            Assert.Equal(long.MaxValue, root.GetProperty("timestampMs").GetInt64());
            JsonElement items = root.GetProperty("displays");
            Assert.Equal(256, items.GetArrayLength());
            int index = 0;
            foreach (JsonElement item in items.EnumerateArray())
            {
                Assert.Equal(index, item.GetProperty("id").GetInt32());
                string actualName = Assert.IsType<string>(item.GetProperty("name").GetString());
                Assert.Equal(128, actualName.Length);
                Assert.Equal(name, actualName);
                Assert.Equal(int.MinValue, item.GetProperty("x").GetInt32());
                Assert.Equal(int.MinValue, item.GetProperty("y").GetInt32());
                Assert.Equal(int.MaxValue, item.GetProperty("width").GetInt32());
                Assert.Equal(int.MaxValue, item.GetProperty("height").GetInt32());
                Assert.Equal(index == 255, item.GetProperty("isPrimary").GetBoolean());
                index++;
            }
            // 合法字段上界组合不等于任意256KiB载荷都能由目录协议生成。
            AssertCleared(write.Array);
            Assert.True(f.Attempt.Snapshot.AllOwnedRootsReleased);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ZeroBudgetRejectsBeforeSource()
    {
        Fixture f = new(TimeSpan.Zero);
        try
        {
            _ = f.Attempt.Start();
            AssertRejected(await f.DrainAsync(), f);
            Assert.Empty(f.Calls);
            Assert.Equal(BudgetEnd.Expired, f.Attempt.Snapshot.Budget!.End);
            Assert.Equal(0, f.Clock.CreateCount);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("source", false)]
    [InlineData("source", true)]
    [InlineData("write", false)]
    [InlineData("write", true)]
    [InlineData("flush", false)]
    [InlineData("flush", true)]
    public async Task BlockedSynchronousPrefixDoesNotBlockStopOrExpiryAndLateOriginalIsJoined(string stage, bool expire)
    {
        Fixture f = new();
        Pause prefix = f.NewPause();
        Held held = f.Hold(stage, prefix);
        try
        {
            Task<SendOutcome> completion = f.Attempt.Start();
            await ReachedAsync(prefix.Reached.Task, completion);
            Assert.NotNull(f.Attempt.Completion);
            Assert.False(held.Published.Task.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => { _ = f.Attempt.Start(); });
            if (expire) { f.Clock.Now = f.BudgetTicks; f.Clock.Timer.Fire(); }
            else await f.StopAsync();
            await AssertAwaitPathAsync(f.Operation, completion);
            Assert.True(f.Attempt.AbortRequired.IsCompletedSuccessfully);
            Assert.False(completion.IsCompleted);
            if (stage == "write") Assert.Equal(f.Stream.Writes[0].Copy, f.Stream.Writes[0].Array);
            prefix.Open();
            Task original = await held.Published.Task.WaitAsync(Guard);
            Assert.Same(held.Original, original);
            await AssertAwaitPathAsync(original, completion);
            Assert.False(original.IsCompleted);
            Assert.False(completion.IsCompleted);
            held.Complete();
            AssertRejected(await f.DrainAsync(), f);
            Assert.Equal(CallsThrough(stage), f.Calls);
            Assert.False(f.Attempt.Snapshot.OperationExitUnproven);
            Assert.All(f.Stream.Writes, write => AssertCleared(write.Array));
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("source", false)]
    [InlineData("source", true)]
    [InlineData("write", false)]
    [InlineData("write", true)]
    [InlineData("flush", false)]
    [InlineData("flush", true)]
    public async Task UncooperativeOriginalTaskNotifiesBeforeJoinAndNeverStartsNextStage(string stage, bool expire)
    {
        Fixture f = new();
        Held held = f.Hold(stage);
        try
        {
            Task<SendOutcome> completion = f.Attempt.Start();
            Task original = await held.Published.Task.WaitAsync(Guard);
            await AssertAwaitPathAsync(original, f.Operation);
            if (expire) { f.Clock.Now = f.BudgetTicks; f.Clock.Timer.Fire(); }
            else await f.StopAsync();
            await AssertAwaitPathAsync(original, completion);
            Assert.True(f.Attempt.AbortRequired.IsCompletedSuccessfully);
            Assert.False(completion.IsCompleted);
            Assert.False(f.Attempt.Snapshot.OriginalOperationsFinished);
            if (stage == "write") Assert.Equal(f.Stream.Writes[0].Copy, f.Stream.Writes[0].Array);
            held.Complete();
            AssertRejected(await f.DrainAsync(), f);
            Assert.Equal(CallsThrough(stage), f.Calls);
            Assert.All(f.Stream.Writes, write => AssertCleared(write.Array));
            Assert.True(f.Attempt.Snapshot.AllOwnedRootsReleased);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("source")]
    [InlineData("write")]
    [InlineData("flush")]
    public async Task UndispatchedTimerCannotRefundBudgetAfterOriginalOperation(string stage)
    {
        Fixture f = new();
        Held held = f.Hold(stage);
        try
        {
            _ = f.Attempt.Start();
            await held.Published.Task.WaitAsync(Guard);
            await AssertAwaitPathAsync(held.Original, f.Operation);
            f.Clock.Now = f.BudgetTicks;
            held.Complete();
            AssertRejected(await f.DrainAsync(), f);
            Assert.IsType<TimeoutException>(f.Attempt.Snapshot.ObservationError);
            Assert.Equal(CallsThrough(stage), f.Calls);
            Assert.Equal(0, f.Clock.Timer.FireCount);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("Source")]
    [InlineData("Serialize")]
    [InlineData("Buffer")]
    [InlineData("Write")]
    [InlineData("Flush")]
    public async Task DeadlineAtStageEntryPreventsThatExternalStage(string stageName)
    {
        OperationStage stage = Enum.Parse<OperationStage>(stageName);
        Fixture f = new();
        f.Clock.ReadTimestamp = () => f.Attempt.Snapshot.Stage == stage ? f.BudgetTicks : 0;
        try
        {
            _ = f.Attempt.Start();
            AssertRejected(await f.DrainAsync(), f);
            Assert.IsType<TimeoutException>(f.Attempt.Snapshot.ObservationError);
            Assert.Equal(stage == OperationStage.Source ? Array.Empty<string>() :
                stage == OperationStage.Flush ? new[] { "source", "write" } : new[] { "source" }, f.Calls);
            Assert.Equal(0, f.Clock.Timer.FireCount);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task StopAndDeadlineVisibleTogetherRejectEvenWhenOriginalReturnsSuccessfully()
    {
        Fixture f = new();
        f.SourceHook = () =>
        {
            f.Clock.Now = f.BudgetTicks;
            f.Attempt.RequestStop();
            return new ValueTask<DisplayCatalogFrame>(f.Catalog);
        };
        try
        {
            _ = f.Attempt.Start();
            AssertRejected(await f.DrainAsync(), f);
            Assert.True(f.Attempt.Snapshot.StopRequested);
            Assert.Equal(new[] { "source" }, f.Calls);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("source", "sync")]
    [InlineData("source", "status")]
    [InlineData("source", "registration")]
    [InlineData("write", "sync")]
    [InlineData("write", "status")]
    [InlineData("write", "registration")]
    [InlineData("flush", "sync")]
    public async Task UnreturnedOriginalPrefixRetainsUnknownRootsAndUnknownWriteBorrow(string stage, string point)
    {
        Fixture f = new();
        Exception error = new AggregateException("未知原操作", new IOException("叶节点"));
        if (stage == "source") f.SourceHook = () => point == "sync" ? throw error :
            new ValueTask<DisplayCatalogFrame>(new ThrowingCatalogSource(error, point == "registration"), 0);
        if (stage == "write") f.Stream.WriteHook = _ => point == "sync" ? throw error :
            new ValueTask(new ThrowingVoidSource(error, point == "registration"), 0);
        if (stage == "flush") f.Stream.FlushHook = () => throw error;
        try
        {
            _ = f.Attempt.Start();
            AssertRejected(await f.DrainAsync(), f);
            var snapshot = f.Attempt.Snapshot;
            Assert.Same(error, snapshot.OperationError);
            Assert.True(snapshot.OperationExitUnproven);
            Assert.Equal(stage == "write", snapshot.WireBorrowExitUnproven);
            Assert.True(snapshot.OriginalOperationsFinished);
            Assert.False(snapshot.AllOwnedRootsReleased);
            Assert.Equal(CallsThrough(stage), f.Calls);
            if (stage == "write")
            {
                Assert.Equal(f.Stream.Writes[0].Copy, f.Stream.Writes[0].Array);
                f.Attempt.RequestStop();
                Assert.Equal(f.Stream.Writes[0].Copy, f.Stream.Writes[0].Array);
            }
            else Assert.All(f.Stream.Writes, write => AssertCleared(write.Array));
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("source")]
    [InlineData("write")]
    [InlineData("flush")]
    public async Task OriginalFaultTreeAndTimerFailureCoexistWithoutFlatteningOrDroppingDuplicates(string stage)
    {
        Fixture f = new();
        Held operation = f.Hold(stage), dispose = f.Hold("dispose");
        Exception leaf = new IOException("原任务叶节点");
        Exception branch = new AggregateException("原任务嵌套", new OperationCanceledException("不是合作取消"), leaf);
        Exception[] roots = [leaf, branch, leaf];
        Exception cleanup = new IOException("timer 原释放错误");
        try
        {
            Task<SendOutcome> completion = f.Attempt.Start();
            await operation.Published.Task.WaitAsync(Guard);
            await AssertAwaitPathAsync(operation.Original, f.Operation);
            await f.StopAsync();
            Task disposing = await dispose.Published.Task.WaitAsync(Guard);
            await AssertAwaitPathAsync(disposing, completion);
            await AssertAwaitPathAsync(operation.Original, completion);
            operation.Fail(roots);
            await f.Operation.WaitAsync(Guard);
            Assert.True(f.Attempt.AbortRequired.IsCompletedSuccessfully);
            Assert.False(completion.IsCompleted);
            Assert.All(f.Stream.Writes, write => AssertCleared(write.Array));
            dispose.Fail(cleanup);
            AssertRejected(await f.DrainAsync(), f);
            AggregateException saved = Assert.IsType<AggregateException>(f.Attempt.Snapshot.OperationError);
            Assert.Equal(roots.Length, saved.InnerExceptions.Count);
            for (int i = 0; i < roots.Length; i++) Assert.Same(roots[i], saved.InnerExceptions[i]);
            Assert.Same(cleanup, f.Attempt.Snapshot.Budget!.CleanupError);
            Assert.False(f.Attempt.Snapshot.OperationExitUnproven);
            Assert.False(f.Attempt.Snapshot.Budget.CleanupExitUnproven);
            Assert.True(f.Attempt.Snapshot.OriginalOperationsFinished);
            Assert.False(f.Attempt.Snapshot.AllOwnedRootsReleased);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("source")]
    [InlineData("write")]
    [InlineData("flush")]
    public async Task NoneCancellationRemainsAnOriginalErrorAfterStop(string stage)
    {
        Fixture f = new();
        Held held = f.Hold(stage);
        using CancellationTokenSource cancellation = new();
        try
        {
            _ = f.Attempt.Start();
            await held.Published.Task.WaitAsync(Guard);
            await AssertAwaitPathAsync(held.Original, f.Operation);
            await f.StopAsync();
            cancellation.Cancel();
            held.Cancel(cancellation.Token);
            AssertRejected(await f.DrainAsync(), f);
            TaskCanceledException error = Assert.IsType<TaskCanceledException>(f.Attempt.Snapshot.OperationError);
            Assert.Same(held.Original, error.Task);
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.False(f.Attempt.Snapshot.OperationExitUnproven);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalTimerDisposeIsJoinedBeforeSuccessAndStopCanStillReject(bool stop)
    {
        Fixture f = new();
        Held dispose = f.Hold("dispose");
        try
        {
            Task<SendOutcome> completion = f.Attempt.Start();
            Task original = await dispose.Published.Task.WaitAsync(Guard);
            await AssertAwaitPathAsync(original, f.Deadline.Completion!);
            Assert.False(completion.IsCompleted);
            Assert.True(f.Attempt.Snapshot.Budget!.FinishRequested);
            Assert.False(f.Attempt.Snapshot.AllOwnedRootsReleased);
            Assert.All(f.Stream.Writes, write => AssertCleared(write.Array));
            if (stop)
            {
                await f.StopAsync();
                await AssertAwaitPathAsync(original, completion);
                Assert.True(f.Attempt.AbortRequired.IsCompletedSuccessfully);
                Assert.False(completion.IsCompleted);
            }
            dispose.Complete();
            SendOutcome outcome = await f.DrainAsync();
            if (stop) AssertRejected(outcome, f);
            else Assert.Equal(AttemptEnd.Sent, outcome.End);
            Assert.True(f.Attempt.Snapshot.Budget.TimerReleased);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task BlockedDisposePrefixCannotBeSkippedAfterStop()
    {
        Fixture f = new();
        Pause prefix = f.NewPause();
        Held dispose = f.Hold("dispose", prefix);
        try
        {
            Task<SendOutcome> completion = f.Attempt.Start();
            await ReachedAsync(prefix.Reached.Task, completion);
            await f.StopAsync();
            await AssertAwaitPathAsync(f.Deadline.Completion!, completion);
            Assert.True(f.Attempt.AbortRequired.IsCompletedSuccessfully);
            Assert.False(completion.IsCompleted);
            prefix.Open();
            Task original = await dispose.Published.Task.WaitAsync(Guard);
            await AssertAwaitPathAsync(original, completion);
            dispose.Complete();
            AssertRejected(await f.DrainAsync(), f);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task RunningCreatePrefixDoesNotClaimAnExitedUnknownRoot()
    {
        Fixture f = new();
        Pause create = f.NewPause();
        f.Clock.CreateHook = create.Wait;
        try
        {
            Task<SendOutcome> completion = f.Attempt.Start();
            await ReachedAsync(create.Reached.Task, completion);
            Assert.False(f.Attempt.Snapshot.TimerCreationExitUnproven);
            Assert.False(f.Attempt.Snapshot.OriginalOperationsFinished);
            Assert.False(f.Attempt.Snapshot.AllOwnedRootsReleased);
            await f.StopAsync();
            await AssertAwaitPathAsync(f.Deadline.Completion!, completion);
            Assert.True(f.Attempt.AbortRequired.IsCompletedSuccessfully);
            Assert.False(completion.IsCompleted);
            create.Open();
            AssertRejected(await f.DrainAsync(), f);
            Assert.False(f.Attempt.Snapshot.TimerCreationExitUnproven);
            Assert.True(f.Attempt.Snapshot.AllOwnedRootsReleased);
            Assert.Empty(f.Calls);
            Assert.Equal(1, f.Clock.Timer.DisposeCount);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("create")]
    [InlineData("dispose")]
    [InlineData("dispose-status")]
    [InlineData("dispose-registration")]
    public async Task UnknownTimerRootPreventsAllRootsReleasedEvenAfterCompletion(string point)
    {
        Fixture f = new();
        Exception error = new IOException("未知 timer 原操作");
        if (point == "create") f.Clock.CreateHook = () => throw error;
        else f.Clock.Timer.DisposeHook = () => point == "dispose" ? throw error :
            new ValueTask(new ThrowingVoidSource(error, point == "dispose-registration"), 0);
        try
        {
            _ = f.Attempt.Start();
            AssertRejected(await f.DrainAsync(), f);
            var snapshot = f.Attempt.Snapshot;
            Assert.True(snapshot.OriginalOperationsFinished);
            Assert.False(snapshot.AllOwnedRootsReleased);
            Assert.Equal(point == "create", snapshot.TimerCreationExitUnproven);
            if (point == "create") Assert.Same(error, snapshot.Budget!.Error);
            else
            {
                Assert.True(snapshot.Budget!.CleanupExitUnproven);
                Assert.Same(error, snapshot.Budget.CleanupError);
            }
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAndFinalAcceptanceRemainChargedToOriginalUndispatchedDeadline(bool finalOnly)
    {
        Fixture f = new();
        if (finalOnly) f.Clock.ReadTimestamp = () => f.Attempt.Snapshot.Budget?.End == BudgetEnd.FinishedInBudget ? f.BudgetTicks : 0;
        else f.Clock.Timer.DisposeHook = () => { f.Clock.Now = f.BudgetTicks; return ValueTask.CompletedTask; };
        try
        {
            _ = f.Attempt.Start();
            AssertRejected(await f.DrainAsync(), f);
            if (finalOnly)
            {
                Assert.IsType<TimeoutException>(f.Attempt.Snapshot.FinalSampleError);
                Assert.Equal(BudgetEnd.FinishedInBudget, f.Attempt.Snapshot.Budget!.End);
            }
            else Assert.Equal(BudgetEnd.Expired, f.Attempt.Snapshot.Budget!.End);
            Assert.Equal(0, f.Clock.Timer.FireCount);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("frequency")]
    [InlineData("frequency-after")]
    [InlineData("regression")]
    [InlineData("observation")]
    [InlineData("final")]
    public async Task ClockFailuresArePreservedAtTheirObservedStage(string point)
    {
        Fixture f = new();
        Exception error = new IOException("原 clock 错误");
        if (point == "observation") f.Clock.ReadTimestamp = () => throw error;
        else if (point == "final") f.Clock.ReadTimestamp = () => f.Attempt.Snapshot.Budget?.End == BudgetEnd.FinishedInBudget ? throw error : 0;
        else
        {
            f.SourceHook = () =>
            {
                if (point == "frequency") f.Clock.Frequency++;
                else if (point == "regression") f.Clock.Now = -1;
                else f.Clock.ReadTimestamp = () => { f.Clock.Frequency++; return 0; };
                return new ValueTask<DisplayCatalogFrame>(f.Catalog);
            };
        }
        try
        {
            _ = f.Attempt.Start();
            AssertRejected(await f.DrainAsync(), f);
            if (point == "observation") Assert.Same(error, f.Attempt.Snapshot.ObservationError);
            else if (point == "final") Assert.Same(error, f.Attempt.Snapshot.FinalSampleError);
            else Assert.IsType<InvalidOperationException>(f.Attempt.Snapshot.ObservationError);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task BlockedBudgetSampleKeepsStopIndependentAndInnerCallsRequireSampleGate()
    {
        Fixture f = new();
        Held source = f.Hold("source");
        Pause sample = f.NewPause();
        try
        {
            Task<SendOutcome> completion = f.Attempt.Start();
            await source.Published.Task.WaitAsync(Guard);
            await AssertAwaitPathAsync(source.Original, f.Operation);
            f.Clock.ReadTimestamp = () => { sample.Wait(); return 0; };
            f.Clock.Timer.Fire();
            await ReachedAsync(sample.Reached.Task, completion);
            source.Complete();
            // Budget 持有 sampleGate 时 Stop 不依赖它；不声称此调度必然已使操作 worker 竞争采样。
            await f.StopAsync();
            await AssertAwaitPathAsync(f.Deadline.Completion!, completion);
            Assert.True(f.Attempt.AbortRequired.IsCompletedSuccessfully);
            Assert.False(completion.IsCompleted);
            Assert.Equal(1, f.Clock.MaxConcurrentCalls);
            sample.Open();
            AssertRejected(await f.DrainAsync(), f);
            Assert.Equal(1, f.Clock.MaxConcurrentCalls);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("initial")]
    [InlineData("stage")]
    [InlineData("final")]
    public async Task ClockCanReenterStopWithoutOwnerAndSampleGateDeadlock(string at)
    {
        Fixture f = new();
        bool reentered = false;
        f.Clock.ReadTimestamp = () =>
        {
            bool selected = at == "initial" || at == "stage" && f.Attempt.Snapshot.Stage == OperationStage.Source ||
                at == "final" && f.Attempt.Snapshot.Budget?.End == BudgetEnd.FinishedInBudget;
            if (selected)
            {
                reentered = true;
                f.Attempt.RequestStop();
                Assert.True(f.Attempt.AbortRequired.IsCompletedSuccessfully);
            }
            return 0;
        };
        try
        {
            _ = f.Attempt.Start();
            AssertRejected(await f.DrainAsync(), f);
            Assert.True(reentered);
            Assert.True(f.Attempt.Snapshot.StopRequested);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactInt128DeadlineHasNoRoundedTimerGrace(bool atDeadline)
    {
        Fixture f = new(TimeSpan.FromSeconds(1));
        f.Clock.Frequency = 3;
        f.SourceHook = () =>
        {
            f.Clock.Now = atDeadline ? 3 : 2;
            return new ValueTask<DisplayCatalogFrame>(f.Catalog);
        };
        try
        {
            _ = f.Attempt.Start();
            SendOutcome outcome = await f.DrainAsync();
            if (atDeadline) AssertRejected(outcome, f);
            else Assert.Equal(AttemptEnd.Sent, outcome.End);
            Assert.Equal(atDeadline ? new[] { "source" } : new[] { "source", "write", "flush" }, f.Calls);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task SharedHighWaterRejectsRealRegressionEvenAboveOriginalAnchor()
    {
        Fixture f = new();
        f.Clock.Now = 100;
        f.SourceHook = () => { f.Clock.Now = 105; return new ValueTask<DisplayCatalogFrame>(f.Catalog); };
        f.Clock.ReadTimestamp = () => f.Attempt.Snapshot.Stage == OperationStage.Serialize ? 104 : f.Clock.Now;
        try
        {
            _ = f.Attempt.Start();
            AssertRejected(await f.DrainAsync(), f);
            InvalidOperationException error = Assert.IsType<InvalidOperationException>(f.Attempt.Snapshot.ObservationError);
            Assert.Equal("host-catalog-clock-regressed", error.Message);
            Assert.Equal(new[] { "source" }, f.Calls);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task BudgetHighWaterRejectsLaterOperationRegressionAboveOriginalAnchor()
    {
        Fixture f = new();
        Held source = f.Hold("source");
        TaskCompletionSource<Task> nextWake = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _ = f.Attempt.Start();
            await source.Published.Task.WaitAsync(Guard);
            await AssertAwaitPathAsync(source.Original, f.Operation);
            Task FirstWake() => Assert.IsType<TaskCompletionSource>(
                RuntimeField(typeof(ControlFrameBudget), "_wake")!.GetValue(f.Deadline)).Task;
            await AssertAwaitPathAsync(FirstWake(), f.Deadline.Completion!);
            f.Clock.Timer.Changing = call =>
            {
                if (call == 2) nextWake.TrySetResult(FirstWake());
            };
            f.Clock.Now = 50;
            f.Clock.Timer.Fire();
            Task wake = await nextWake.Task.WaitAsync(Guard);
            // 只有预算已完成50的采样且真正等待下一次唤醒，才交还较小但仍高于anchor的49。
            await AssertAwaitPathAsync(wake, f.Deadline.Completion!);
            f.Clock.Now = 49;
            source.Complete();
            AssertRejected(await f.DrainAsync(), f);
            InvalidOperationException error = Assert.IsType<InvalidOperationException>(f.Attempt.Snapshot.ObservationError);
            Assert.Equal("host-catalog-clock-regressed", error.Message);
            Assert.Equal(new[] { "source" }, f.Calls);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task StopDuringBlockedFinalSampleDoesNotWaitForClock()
    {
        Fixture f = new();
        Pause final = f.NewPause();
        f.Clock.ReadTimestamp = () =>
        {
            if (f.Attempt.Snapshot.Budget?.End == BudgetEnd.FinishedInBudget) final.Wait();
            return 0;
        };
        try
        {
            Task<SendOutcome> completion = f.Attempt.Start();
            await ReachedAsync(final.Reached.Task, completion);
            await f.StopAsync();
            Assert.True(f.Attempt.AbortRequired.IsCompletedSuccessfully);
            Assert.False(completion.IsCompleted);
            final.Open();
            AssertRejected(await f.DrainAsync(), f);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task NullCatalogIsRejectedWithoutInventingUnknownTaskExit()
    {
        Fixture f = new();
        f.SourceHook = () => new ValueTask<DisplayCatalogFrame>((DisplayCatalogFrame)null!);
        try
        {
            _ = f.Attempt.Start();
            AssertRejected(await f.DrainAsync(), f);
            Assert.IsType<InvalidOperationException>(f.Attempt.Snapshot.OperationError);
            Assert.False(f.Attempt.Snapshot.OperationExitUnproven);
            Assert.Empty(f.Stream.Writes);
        }
        finally { await f.FinishAsync(); }
    }

    private static string[] CallsThrough(string stage) => stage switch
    {
        "source" => ["source"], "write" => ["source", "write"], _ => ["source", "write", "flush"]
    };
    private static void AssertRejected(SendOutcome outcome, Fixture f)
    {
        Assert.Equal(AttemptEnd.Rejected, outcome.End);
        Assert.Equal(AttemptEnd.Rejected, f.Attempt.Snapshot.End);
        Assert.False(f.Attempt.Snapshot.Committed);
        Assert.True(f.Attempt.AbortRequired.IsCompletedSuccessfully);
        Assert.Null(f.Attempt.Snapshot.CoordinationError);
    }
    private static void AssertCleared(byte[] bytes) => Assert.All(bytes, value => Assert.Equal((byte)0, value));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task ReachedAsync(Task reached, Task completion)
    {
        await Task.WhenAny(reached, completion).WaitAsync(Guard);
        Assert.True(reached.IsCompletedSuccessfully, "未到达指定同步前缀就完成。");
        Assert.False(completion.IsCompleted, "受闸同步前缀尚未返回，不得完成。");
    }

    // 只认可精确 awaiter、Task.Run unwrap 和已注册的 WhenAll 输入边；WhenAny/任意通知均排除。
    private static async Task AssertAwaitPathAsync(Task original, Task completion)
    {
        using CancellationTokenSource guard = new(Guard);
        while (!HasAwaitPath(original, completion, new HashSet<Task>()))
        {
            Assert.False(original.IsCompleted, "Assert.False：原任务提前退出，失去受闸等待证据。");
            Assert.False(completion.IsCompleted, "Assert.False：Completion 提前退出，未等待指定原任务。");
            if (guard.IsCancellationRequested)
                throw new XunitException("Guard：没有精确 await/unwrap/WhenAll 链，不能略过运行时证明。");
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
            ?? throw new XunitException("运行时缺少 Task continuation 字段。");
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

    private sealed class Fixture
    {
        private readonly List<Held> _held = [];
        private readonly List<Pause> _pauses = [];
        private readonly List<Task> _helpers = [];
        private readonly ConcurrentQueue<string> _calls = new();
        internal DisplayCatalogFrame Catalog { get; } = new(7, 12,
            [new DisplayInfo(new DisplayId(0), "主屏", -100, 20, 1920, 1080, true)]);
        internal ProbeStream Stream { get; }
        internal ProbeClock Clock { get; }
        internal HostDisplayCatalogSendAttempt Attempt { get; }
        internal Func<ValueTask<DisplayCatalogFrame>>? SourceHook { get; set; }
        internal long BudgetTicks { get; }
        internal string[] Calls => _calls.ToArray();
        internal Task Operation => Child<Task>("_operation");
        internal ControlFrameBudget Deadline => Child<ControlFrameBudget>("_deadline");
        internal object SampleGate
        {
            get
            {
                object clock = Child<TimeProvider>("_clock");
                return RuntimeField(clock.GetType(), "_sampleGate")!.GetValue(clock)!;
            }
        }
        internal Fixture(TimeSpan? budget = null)
        {
            TimeSpan duration = budget ?? TimeSpan.FromTicks(100);
            BudgetTicks = duration.Ticks;
            Stream = new(this);
            Clock = new(this);
            Attempt = new(Stream, Source, Clock, duration);
        }
        private T Child<T>(string name) where T : class =>
            Assert.IsAssignableFrom<T>(typeof(HostDisplayCatalogSendAttempt).GetField(name, Fields)!.GetValue(Attempt));
        internal ValueTask<DisplayCatalogFrame> Source()
        {
            Record("source");
            return SourceHook?.Invoke() ?? new ValueTask<DisplayCatalogFrame>(Catalog);
        }
        internal void Record(string stage)
        {
            Assert.NotNull(Attempt.Completion);
            Assert.True(Thread.CurrentThread.IsThreadPoolThread);
            _calls.Enqueue(stage);
        }
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
                case "dispose": Clock.Timer.DisposeHook = () => new ValueTask(Enter()); break;
                default: throw new ArgumentOutOfRangeException(nameof(stage));
            }
            return held;
        }
        internal Pause NewPause() { Pause pause = new(); _pauses.Add(pause); return pause; }
        internal async Task StopAsync()
        {
            Task stop = Task.Run(Attempt.RequestStop);
            _helpers.Add(stop);
            await stop.WaitAsync(Guard);
        }
        internal async Task<SendOutcome> DrainAsync()
        {
            Task<SendOutcome> completion = Assert.IsAssignableFrom<Task<SendOutcome>>(Attempt.Completion);
            SendOutcome outcome = await completion.WaitAsync(Guard);
            Assert.True(completion.IsCompletedSuccessfully);
            Assert.Same(completion, Attempt.Completion);
            Assert.True(Attempt.Snapshot.OriginalOperationsFinished);
            Assert.Empty(Stream.UnexpectedCalls);
            Assert.Equal(0, Clock.Timer.SyncDisposeCount);
            return outcome;
        }
        internal async Task FinishAsync()
        {
            foreach (Pause pause in _pauses) pause.Open();
            Attempt.RequestStop();
            foreach (Held held in _held) held.Complete();
            try
            {
                try { await Task.WhenAll(_helpers).WaitAsync(Guard); }
                finally
                {
                    if (Attempt.Completion is { } completion) await completion.WaitAsync(Guard);
                    // 即使变异破坏 owner join，夹具也独立等待已创建 worker 和所有原句柄。
                    if (Attempt.Snapshot.OperationStarted) await Operation.WaitAsync(Guard);
                    if (Attempt.Snapshot.Budget is not null && Deadline.Completion is { } budget) await budget.WaitAsync(Guard);
                    foreach (Held held in _held)
                    {
                        Assert.True(held.Original.IsCompleted);
                        try { await held.Original; } catch { _ = held.Original.Exception; }
                    }
                }
            }
            finally
            {
                foreach (Pause pause in _pauses) pause.Dispose();
                Assert.Empty(Stream.UnexpectedCalls);
            }
        }
    }

    private sealed class Held(string stage, DisplayCatalogFrame frame)
    {
        private readonly TaskCompletionSource<DisplayCatalogFrame> _source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _io = Signal();
        internal TaskCompletionSource<Task> Published { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Original => stage == "source" ? _source.Task : _io.Task;
        internal void Complete() { if (stage == "source") _source.TrySetResult(frame); else _io.TrySetResult(); }
        internal void Fail(params Exception[] errors) { if (stage == "source") _source.TrySetException(errors); else _io.TrySetException(errors); }
        internal void Cancel(CancellationToken token) { if (stage == "source") _source.TrySetCanceled(token); else _io.TrySetCanceled(token); }
    }
    private sealed class Pause : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        internal TaskCompletionSource Reached { get; } = Signal();
        internal void Wait()
        {
            Reached.TrySetResult();
            if (!_release.Wait(Guard)) throw new XunitException("Guard：同步闸没有放行。");
        }
        internal void Open() => _release.Set();
        public void Dispose() => _release.Dispose();
    }
    private sealed class ProbeClock(Fixture f) : TimeProvider
    {
        private long _now, _frequency = TimeSpan.TicksPerSecond;
        private int _external, _active, _maxActive, _creates;
        internal ProbeTimer Timer { get; } = new();
        internal long Now { get => Volatile.Read(ref _now); set => Volatile.Write(ref _now, value); }
        internal long Frequency { get => Volatile.Read(ref _frequency); set => Volatile.Write(ref _frequency, value); }
        internal int ExternalCalls => Volatile.Read(ref _external);
        internal int MaxConcurrentCalls => Volatile.Read(ref _maxActive);
        internal int CreateCount => Volatile.Read(ref _creates);
        internal Func<long>? ReadTimestamp { get; set; }
        internal Action? CreateHook { get; set; }
        private void Enter()
        {
            Assert.NotNull(f.Attempt.Completion);
            Assert.True(Monitor.IsEntered(f.SampleGate), "原频率/时间戳调用必须全段持有同一专用 sampleGate。");
            Interlocked.Increment(ref _external);
            int active = Interlocked.Increment(ref _active);
            int old;
            do { old = Volatile.Read(ref _maxActive); if (old >= active) break; }
            while (Interlocked.CompareExchange(ref _maxActive, active, old) != old);
        }
        public override long TimestampFrequency
        {
            get { Enter(); try { return Frequency; } finally { Interlocked.Decrement(ref _active); } }
        }
        public override long GetTimestamp()
        {
            Enter();
            try { return ReadTimestamp?.Invoke() ?? Now; }
            finally { Interlocked.Decrement(ref _active); }
        }
        public override DateTimeOffset GetUtcNow() => throw new XunitException("禁止读取墙钟。");
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _external);
            Interlocked.Increment(ref _creates);
            Assert.Equal(Timeout.InfiniteTimeSpan, dueTime);
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            Timer.Bind(callback, state);
            CreateHook?.Invoke();
            return Timer;
        }
    }
    private sealed class ProbeTimer : ITimer
    {
        private TimerCallback? _callback;
        private object? _state;
        private int _disposes, _syncDisposes, _fires, _changes;
        internal Action<int>? Changing { get; set; }
        internal Func<ValueTask>? DisposeHook { get; set; }
        internal int DisposeCount => Volatile.Read(ref _disposes);
        internal int SyncDisposeCount => Volatile.Read(ref _syncDisposes);
        internal int FireCount => Volatile.Read(ref _fires);
        internal void Bind(TimerCallback callback, object? state) { _callback = callback; _state = state; }
        internal void Fire() { Interlocked.Increment(ref _fires); Assert.IsType<TimerCallback>(_callback)(_state); }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Assert.True(dueTime > TimeSpan.Zero);
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            int call = Interlocked.Increment(ref _changes);
            Changing?.Invoke(call);
            return true;
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposes);
            return DisposeHook?.Invoke() ?? ValueTask.CompletedTask;
        }
        public void Dispose() { Interlocked.Increment(ref _syncDisposes); throw new XunitException("禁止同步释放 timer。"); }
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
            return new XunitException($"借用流不应执行 {operation}。");
        }
        public override bool CanRead => throw Unexpected(nameof(CanRead));
        public override bool CanWrite => throw Unexpected(nameof(CanWrite));
        public override bool CanSeek => throw Unexpected(nameof(CanSeek));
        public override bool CanTimeout => throw Unexpected(nameof(CanTimeout));
        public override long Length => throw Unexpected(nameof(Length));
        public override long Position { get => throw Unexpected("Position.get"); set => throw Unexpected("Position.set"); }
        public override int ReadTimeout { get => throw Unexpected("ReadTimeout.get"); set => throw Unexpected("ReadTimeout.set"); }
        public override int WriteTimeout { get => throw Unexpected("WriteTimeout.get"); set => throw Unexpected("WriteTimeout.set"); }
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
    private sealed class ThrowingCatalogSource(Exception error, bool registration) : IValueTaskSource<DisplayCatalogFrame>
    {
        public ValueTaskSourceStatus GetStatus(short token) => registration ? ValueTaskSourceStatus.Pending : throw error;
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => throw error;
        public DisplayCatalogFrame GetResult(short token) => throw new XunitException("未取得原来源 Task，不应取得结果。");
    }
    private sealed class ThrowingVoidSource(Exception error, bool registration) : IValueTaskSource
    {
        public ValueTaskSourceStatus GetStatus(short token) => registration ? ValueTaskSourceStatus.Pending : throw error;
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => throw error;
        public void GetResult(short token) => throw new XunitException("未取得原操作 Task，不应取得结果。");
    }
}
