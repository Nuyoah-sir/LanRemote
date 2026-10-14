using System.Buffers.Binary;
using System.Text.Json;
using System.Threading.Tasks.Sources;
using LanRemote.Core.Models;

namespace LanRemote.Transport.Tests;

public sealed class HostDisplayCatalogSourceTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private static DisplayInfo Screen => new(new DisplayId(7), "副坐标主屏", -1920, -100, 1920, 1080, true);
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ControlAuthContext Context(TimeProvider? clock = null) => new()
    {
        ServerDeviceId = CatalogLifetimeScenario.ServerId,
        AccessSecretStore = new CatalogSecretStore(),
        FailedAuthLimiter = new FailedAuthLimiter(),
        PendingApprovalLimiter = new ConnectionAdmissionLimiter(3, 1),
        ApprovalGate = new CatalogApproval(),
        SessionRegistry = new SessionRegistry(),
        TimeProvider = clock ?? new ManualDeadlineClock(),
    };

    [Fact]
    public void ConfigurationIsColdAndCopyKeepsEveryHostSharedRoot()
    {
        ControlAuthContext original = Context(new ForbiddenClock());
        int calls = 0;
        ControlAuthContext copy = original.WithDisplayCatalogSource(() =>
        {
            calls++;
            throw new InvalidOperationException("must-not-enumerate-during-configuration");
        }, Budget);
        Assert.NotSame(original, copy);
        Assert.Null(original.DisplayCatalogSend);
        Assert.NotNull(copy.DisplayCatalogSend);
        Assert.Equal(Budget, copy.DisplayCatalogSend.Budget);
        Assert.Equal(0, calls);
        Assert.Equal(original.ServerDeviceId, copy.ServerDeviceId);
        Assert.Same(original.SecretLoader, copy.SecretLoader);
        Assert.Same(original.AccessSecretStore, copy.AccessSecretStore);
        Assert.Same(original.FailedAuthLimiter, copy.FailedAuthLimiter);
        Assert.Same(original.PendingApprovalLimiter, copy.PendingApprovalLimiter);
        Assert.Same(original.ApprovalGate, copy.ApprovalGate);
        Assert.Same(original.SessionRegistry, copy.SessionRegistry);
        Assert.Same(original.TimeProvider, copy.TimeProvider);
        Assert.Same(original.Options, copy.Options);
    }

    [Fact]
    public void InvalidConfigurationRejectsBeforeCallingSource()
    {
        ControlAuthContext context = Context(new ForbiddenClock());
        Assert.Equal("source", Assert.Throws<ArgumentNullException>(() =>
            context.WithDisplayCatalogSource(null!, Budget)).ParamName);
        foreach (TimeSpan budget in new[] { TimeSpan.MinValue, TimeSpan.FromTicks(-1),
            ControlFrameBudget.MaxBudget + TimeSpan.FromTicks(1), TimeSpan.MaxValue })
            Assert.Equal("budget", Assert.Throws<ArgumentOutOfRangeException>(() =>
                context.WithDisplayCatalogSource(() => throw new InvalidOperationException("cold"), budget)).ParamName);
        _ = context.WithDisplayCatalogSource(() => throw new InvalidOperationException("cold"), TimeSpan.Zero);
        _ = context.WithDisplayCatalogSource(() => throw new InvalidOperationException("cold"), ControlFrameBudget.MaxBudget);
    }

    [Fact(Timeout = 60_000)]
    public async Task ProjectionCopiesDataAndSenderWritesStrictSingleCatalog()
    {
        List<DisplayInfo> mutable = [Screen];
        int calls = 0;
        ControlAuthContext context = Context().WithDisplayCatalogSource(() =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult<IReadOnlyList<DisplayInfo>>(mutable);
        }, Budget);
        DisplayCatalogFrame projected = await context.DisplayCatalogSend!.Source();
        mutable.Clear();
        Assert.Equal(Screen, Assert.Single(projected.Displays));
        Assert.Equal(0, projected.Sequence);
        Assert.True(projected.TimestampMs > 0);
        mutable.Add(Screen);
        using MemoryStream stream = new();
        HostDisplayCatalogSendAttempt attempt = new(stream, context.DisplayCatalogSend.Source, context.TimeProvider, Budget);
        Assert.Equal(HostDisplayCatalogSendAttempt.AttemptEnd.Sent, (await attempt.Start().WaitAsync(Guard)).End);
        Assert.Equal(2, calls); // 一次直接投影 + 一次发送来源；并非两次每连接调用。
        byte[] wire = stream.ToArray();
        Assert.Equal((uint)(wire.Length - 4), BinaryPrimitives.ReadUInt32BigEndian(wire));
        using JsonDocument document = JsonDocument.Parse(wire.AsMemory(4));
        JsonElement root = document.RootElement;
        Assert.Equal("host_status", root.GetProperty("type").GetString());
        Assert.Equal(1, root.GetProperty("protocol").GetInt32());
        Assert.Equal(0, root.GetProperty("seq").GetInt64());
        JsonElement display = Assert.Single(root.GetProperty("displays").EnumerateArray());
        Assert.Equal(7, display.GetProperty("id").GetInt32());
        Assert.Equal(-1920, display.GetProperty("x").GetInt32());
        Assert.True(display.GetProperty("isPrimary").GetBoolean());
        Assert.True(attempt.Snapshot.AllOwnedRootsReleased);
    }

    [Theory(Timeout = 60_000)]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("duplicate-id")]
    [InlineData("no-primary")]
    [InlineData("two-primary")]
    [InlineData("width")]
    [InlineData("name")]
    [InlineData("too-many")]
    public async Task InvalidSourceDataIsRejectedBeforeAnyWire(string invalid)
    {
        IReadOnlyList<DisplayInfo>? data = invalid switch
        {
            "null" => null,
            "empty" => [],
            "duplicate-id" => [Screen, Screen with { IsPrimary = false }],
            "no-primary" => [Screen with { IsPrimary = false }],
            "two-primary" => [Screen, Screen with { Id = new DisplayId(8) }],
            "width" => [Screen with { Width = 0 }],
            "name" => [Screen with { Name = "bad\nname" }],
            "too-many" => Enumerable.Range(0, 257).Select(i => Screen with { Id = new DisplayId(i), IsPrimary = i == 0 }).ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(invalid)),
        };
        ControlAuthContext context = Context().WithDisplayCatalogSource(() => ValueTask.FromResult(data!), Budget);
        using MemoryStream stream = new();
        HostDisplayCatalogSendAttempt attempt = new(stream, context.DisplayCatalogSend!.Source, context.TimeProvider, Budget);
        Assert.Equal(HostDisplayCatalogSendAttempt.AttemptEnd.Rejected, (await attempt.Start().WaitAsync(Guard)).End);
        Assert.Equal(0, stream.Length);
        Assert.Equal(HostDisplayCatalogSendAttempt.OperationStage.Source, attempt.Snapshot.ErrorStage);
        Assert.NotNull(attempt.Snapshot.OperationError);
        Assert.False(attempt.Snapshot.OperationExitUnproven);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceOriginalExceptionTreeRetainsSiblingsNestingAndRepeatedReferences(bool delayed)
    {
        TaskCompletionSource<IReadOnlyList<DisplayInfo>> original = Signal<IReadOnlyList<DisplayInfo>>();
        Exception leaf = new IOException("source-leaf");
        Exception nested = new AggregateException("source-nested", leaf, new OperationCanceledException("source-cancel"));
        if (!delayed) original.SetException([leaf, nested, leaf]);
        TaskCompletionSource<bool> entered = Signal<bool>();
        ControlAuthContext context = Context().WithDisplayCatalogSource(() =>
        { entered.TrySetResult(true); return new(original.Task); }, Budget);
        using MemoryStream stream = new();
        HostDisplayCatalogSendAttempt attempt = new(stream, context.DisplayCatalogSend!.Source, context.TimeProvider, Budget);
        Task<HostDisplayCatalogSendAttempt.SendOutcome> completion = attempt.Start();
        try
        {
            if (delayed)
            {
                // 原Task确实经投影进入等待链；通知或WhenAny不算join。
                await entered.Task.WaitAsync(Guard);
                Task<bool> operation = Assert.IsAssignableFrom<Task<bool>>(typeof(HostDisplayCatalogSendAttempt)
                    .GetField("_operation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(attempt));
                await CatalogLifetimeScenario.AssertAwaitPathAsync(original.Task, operation);
                original.SetException([leaf, nested, leaf]);
            }
            Assert.Equal(HostDisplayCatalogSendAttempt.AttemptEnd.Rejected, (await completion.WaitAsync(Guard)).End);
            AggregateException error = Assert.IsType<AggregateException>(attempt.Snapshot.OperationError);
            Assert.Equal(3, error.InnerExceptions.Count);
            Assert.Same(leaf, error.InnerExceptions[0]);
            Assert.Same(nested, error.InnerExceptions[1]);
            Assert.Same(leaf, error.InnerExceptions[2]);
            Assert.Equal(0, stream.Length);
        }
        finally { original.TrySetResult([Screen]); await completion.WaitAsync(Guard); }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopOrBudgetWaitsForTheOriginalSourceBeforeCompletion(bool expire)
    {
        ManualDeadlineClock clock = new();
        TaskCompletionSource<IReadOnlyList<DisplayInfo>> original = Signal<IReadOnlyList<DisplayInfo>>();
        TaskCompletionSource<bool> entered = Signal<bool>();
        ControlAuthContext context = Context(clock).WithDisplayCatalogSource(() =>
        { entered.TrySetResult(true); return new(original.Task); }, Budget);
        using MemoryStream stream = new();
        HostDisplayCatalogSendAttempt attempt = new(stream, context.DisplayCatalogSend!.Source, clock, Budget);
        Task<HostDisplayCatalogSendAttempt.SendOutcome> completion = attempt.Start();
        try
        {
            await entered.Task.WaitAsync(Guard);
            if (expire) clock.Advance(Budget); else attempt.RequestStop();
            await attempt.AbortRequired.WaitAsync(Guard);
            await CatalogLifetimeScenario.AssertAwaitPathAsync(original.Task, completion);
            Assert.False(completion.IsCompleted);
            Assert.False(original.Task.IsCompleted);
            original.SetResult([Screen]);
            Assert.Equal(HostDisplayCatalogSendAttempt.AttemptEnd.Rejected, (await completion.WaitAsync(Guard)).End);
            Assert.Equal(0, stream.Length);
            Assert.True(attempt.Snapshot.AllOwnedRootsReleased);
            Assert.Equal(0, clock.TimerCount);
        }
        finally { original.TrySetResult([Screen]); attempt.RequestStop(); await completion.WaitAsync(Guard); }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SynchronousSourceOrAsTaskFailureKeepsUnknownExitEvidence(bool asTask)
    {
        Exception expected = new IOException("original-prefix-did-not-hand-back-task");
        ControlAuthContext context = Context().WithDisplayCatalogSource(() => asTask
            ? new ValueTask<IReadOnlyList<DisplayInfo>>(new FailingValueSource(expected), 0)
            : throw expected, Budget);
        using MemoryStream stream = new();
        HostDisplayCatalogSendAttempt attempt = new(stream, context.DisplayCatalogSend!.Source, context.TimeProvider, Budget);
        Assert.Equal(HostDisplayCatalogSendAttempt.AttemptEnd.Rejected, (await attempt.Start().WaitAsync(Guard)).End);
        Assert.Same(expected, attempt.Snapshot.OperationError);
        Assert.True(attempt.Snapshot.OperationExitUnproven);
        Assert.True(attempt.Snapshot.OriginalOperationsFinished);
        Assert.False(attempt.Snapshot.AllOwnedRootsReleased);
        Assert.Equal(0, stream.Length);
    }

    [Fact(Timeout = 60_000)]
    public async Task CanceledOriginalRemainsAnErrorRatherThanSuccessfulStop()
    {
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        ControlAuthContext context = Context().WithDisplayCatalogSource(() =>
            new(Task.FromCanceled<IReadOnlyList<DisplayInfo>>(canceled.Token)), Budget);
        using MemoryStream stream = new();
        HostDisplayCatalogSendAttempt attempt = new(stream, context.DisplayCatalogSend!.Source, context.TimeProvider, Budget);
        Assert.Equal(HostDisplayCatalogSendAttempt.AttemptEnd.Rejected, (await attempt.Start().WaitAsync(Guard)).End);
        OperationCanceledException error = Assert.IsAssignableFrom<OperationCanceledException>(attempt.Snapshot.OperationError);
        Assert.Equal(canceled.Token, error.CancellationToken);
        Assert.False(attempt.Snapshot.OperationExitUnproven);
        Assert.Equal(0, stream.Length);
    }

    [Fact(Timeout = 60_000)]
    public async Task SharedConfigurationKeepsConcurrentSourceOperationsIndependent()
    {
        TaskCompletionSource<IReadOnlyList<DisplayInfo>> first = Signal<IReadOnlyList<DisplayInfo>>();
        TaskCompletionSource<IReadOnlyList<DisplayInfo>> second = Signal<IReadOnlyList<DisplayInfo>>();
        TaskCompletionSource<bool> firstEntered = Signal<bool>(), secondEntered = Signal<bool>();
        int calls = 0;
        ControlAuthContext context = Context().WithDisplayCatalogSource(() =>
        {
            if (Interlocked.Increment(ref calls) == 1) { firstEntered.SetResult(true); return new(first.Task); }
            secondEntered.SetResult(true); return new(second.Task);
        }, Budget);
        using MemoryStream streamA = new(), streamB = new();
        HostDisplayCatalogSendAttempt a = new(streamA, context.DisplayCatalogSend!.Source, context.TimeProvider, Budget);
        HostDisplayCatalogSendAttempt b = new(streamB, context.DisplayCatalogSend.Source, context.TimeProvider, Budget);
        Task<HostDisplayCatalogSendAttempt.SendOutcome> runA = a.Start();
        Task<HostDisplayCatalogSendAttempt.SendOutcome>? runB = null;
        try
        {
            await firstEntered.Task.WaitAsync(Guard);
            runB = b.Start();
            await secondEntered.Task.WaitAsync(Guard);
            a.RequestStop();
            await a.AbortRequired.WaitAsync(Guard);
            await CatalogLifetimeScenario.AssertAwaitPathAsync(first.Task, runA);
            second.SetResult([Screen with { Name = "B" }]);
            Assert.Equal(HostDisplayCatalogSendAttempt.AttemptEnd.Sent, (await runB.WaitAsync(Guard)).End);
            Assert.False(runA.IsCompleted);
            first.SetResult([Screen]);
            Assert.Equal(HostDisplayCatalogSendAttempt.AttemptEnd.Rejected, (await runA.WaitAsync(Guard)).End);
            Assert.Equal(0, streamA.Length);
            Assert.True(streamB.Length > 4);
            Assert.Equal(2, calls);
        }
        finally
        {
            first.TrySetResult([Screen]); second.TrySetResult([Screen]); a.RequestStop(); b.RequestStop();
            await runA.WaitAsync(Guard);
            if (runB is not null) await runB.WaitAsync(Guard);
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData("on-completed", true)]
    [InlineData("get-result", false)]
    public async Task ValueTaskConversionDistinguishesUnknownPrefixFromReturnedFaultedTask(string stage, bool unknown)
    {
        Exception expected = new IOException(stage);
        ControlAuthContext context = Context().WithDisplayCatalogSource(() =>
            new ValueTask<IReadOnlyList<DisplayInfo>>(new ConversionFailureSource(stage, expected), 0), Budget);
        using MemoryStream stream = new();
        HostDisplayCatalogSendAttempt attempt = new(stream, context.DisplayCatalogSend!.Source, context.TimeProvider, Budget);
        Assert.Equal(HostDisplayCatalogSendAttempt.AttemptEnd.Rejected, (await attempt.Start().WaitAsync(Guard)).End);
        Assert.Same(expected, attempt.Snapshot.OperationError);
        Assert.Equal(unknown, attempt.Snapshot.OperationExitUnproven);
        Assert.Equal(!unknown, attempt.Snapshot.AllOwnedRootsReleased);
        Assert.Equal(0, stream.Length);
    }

    [Fact(Timeout = 60_000)]
    public async Task BlockingSynchronousPrefixIsNotHiddenInsideCompletedProjection()
    {
        TaskCompletionSource<bool> entered = Signal<bool>();
        using ManualResetEventSlim release = new();
        TaskCompletionSource<IReadOnlyList<DisplayInfo>> original = Signal<IReadOnlyList<DisplayInfo>>();
        ControlAuthContext context = Context().WithDisplayCatalogSource(() =>
        {
            entered.SetResult(true);
            if (!release.Wait(Guard)) throw new TimeoutException("test-source-prefix-guard");
            return new(original.Task);
        }, Budget);
        using MemoryStream stream = new();
        HostDisplayCatalogSendAttempt attempt = new(stream, context.DisplayCatalogSend!.Source, context.TimeProvider, Budget);
        Task<HostDisplayCatalogSendAttempt.SendOutcome> completion = attempt.Start();
        try
        {
            await entered.Task.WaitAsync(Guard);
            attempt.RequestStop();
            await attempt.AbortRequired.WaitAsync(Guard);
            Assert.False(completion.IsCompleted);
            release.Set();
            await CatalogLifetimeScenario.AssertAwaitPathAsync(original.Task, completion);
            Assert.False(completion.IsCompleted);
            original.SetResult([Screen]);
            Assert.Equal(HostDisplayCatalogSendAttempt.AttemptEnd.Rejected, (await completion.WaitAsync(Guard)).End);
            Assert.False(attempt.Snapshot.OperationExitUnproven);
            Assert.Equal(0, stream.Length);
        }
        finally { release.Set(); original.TrySetResult([Screen]); attempt.RequestStop(); await completion.WaitAsync(Guard); }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FaultedCancellationPreservesOriginalInstanceAndOptionalSiblings(bool siblings)
    {
        using CancellationTokenSource sourceToken = new();
        sourceToken.Cancel();
        Exception cancellation = new OperationCanceledException("spontaneous-source-fault", sourceToken.Token);
        Exception other = new IOException("second-source-root");
        TaskCompletionSource<IReadOnlyList<DisplayInfo>> original = Signal<IReadOnlyList<DisplayInfo>>();
        original.SetException(siblings ? [cancellation, other] : [cancellation]);
        Assert.True(original.Task.IsFaulted);
        ControlAuthContext context = Context().WithDisplayCatalogSource(() => new(original.Task), Budget);
        using MemoryStream stream = new();
        HostDisplayCatalogSendAttempt attempt = new(stream, context.DisplayCatalogSend!.Source, context.TimeProvider, Budget);
        Assert.Equal(HostDisplayCatalogSendAttempt.AttemptEnd.Rejected, (await attempt.Start().WaitAsync(Guard)).End);
        if (siblings)
        {
            AggregateException tree = Assert.IsType<AggregateException>(attempt.Snapshot.OperationError);
            Assert.Equal(2, tree.InnerExceptions.Count);
            Assert.Same(cancellation, tree.InnerExceptions[0]);
            Assert.Same(other, tree.InnerExceptions[1]);
        }
        else Assert.Same(cancellation, attempt.Snapshot.OperationError);
        Assert.False(attempt.Snapshot.OperationExitUnproven);
        Assert.Equal(0, stream.Length);
        // 这里只保留异常身份/树；async投影可将原faulted OCE表现为Canceled，不声称Task状态等价。
    }

    private sealed class ConversionFailureSource(string stage, Exception error) : IValueTaskSource<IReadOnlyList<DisplayInfo>>
    {
        public ValueTaskSourceStatus GetStatus(short token) => stage == "on-completed"
            ? ValueTaskSourceStatus.Pending : ValueTaskSourceStatus.Succeeded;
        public IReadOnlyList<DisplayInfo> GetResult(short token) => throw error;
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => throw error;
    }

    private sealed class FailingValueSource(Exception error) : IValueTaskSource<IReadOnlyList<DisplayInfo>>
    {
        public ValueTaskSourceStatus GetStatus(short token) => throw error;
        public IReadOnlyList<DisplayInfo> GetResult(short token) => throw new InvalidOperationException("unreachable");
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => throw new InvalidOperationException("unreachable");
    }

    private sealed class ForbiddenClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("cold-utc");
        public override long GetTimestamp() => throw new InvalidOperationException("cold-timestamp");
        public override long TimestampFrequency => throw new InvalidOperationException("cold-frequency");
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => throw new InvalidOperationException("cold-timer");
    }
}
