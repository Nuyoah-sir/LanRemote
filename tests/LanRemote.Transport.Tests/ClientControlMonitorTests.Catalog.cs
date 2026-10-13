using System.Collections.Concurrent;
using System.Text;
using LanRemote.Core.Models;

namespace LanRemote.Transport.Tests;

public sealed partial class ClientControlMonitorTests
{
    private const string CatalogJson = """{"type":"host_status","protocol":1,"seq":7,"timestampMs":123,"displays":[{"id":4,"name":"主屏 <不是HTML>","x":-1920,"y":0,"width":1920,"height":1080,"isPrimary":true}]}""";

    [Theory(Timeout = TestTimeout)]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(262144)]
    public async Task Catalog_Valid_Fragmented_Frame_Publishes_Immutable_Single_Slot_And_Continues_Original_Reader(int chunk)
    {
        Fixture f = new();
        CatalogInput input = new(f, Framed(CatalogJson), chunk);
        try
        {
            await input.StartAsync();
            await input.Tail.Reached.Task.WaitAsync(Guard);
            DisplayCatalogFrame catalog = Assert.IsType<DisplayCatalogFrame>(f.Parent.LatestDisplayCatalog);
            Assert.Equal(7, catalog.Sequence);
            Assert.Equal(123, catalog.TimestampMs);
            DisplayInfo screen = Assert.Single(catalog.Displays);
            Assert.Equal(4, screen.Id.Value);
            Assert.Equal(-1920, screen.X);
            Assert.Equal("主屏 <不是HTML>", screen.Name);
            Assert.True(Assert.IsAssignableFrom<ICollection<DisplayInfo>>(catalog.Displays).IsReadOnly);
            f.AssertControlLive();
            Assert.False(f.MonitorTask.IsCompleted);
            Assert.Equal(1, input.MaxConcurrent);
            Assert.Equal(2, f.Clock.TimerCreates); // video attach + one complete catalog; idle has no timer.
            Assert.Equal(0, f.Control.Ssl.WriteCalls);
            Assert.Equal(0, f.Control.Ssl.FlushCalls);
            Assert.All(input.PayloadMemories, memory => Assert.True(memory.Span.IndexOfAnyExcept((byte)0) < 0));
            input.Tail.Open();
            await f.MonitorTask.WaitAsync(Guard);
            f.AssertRevoked();
            Assert.Null(f.Parent.LatestDisplayCatalog);
            Assert.Equal(7, catalog.Sequence); // a previously returned immutable snapshot is not a revocable capability.
            var evidence = await f.Parent.GetDisposalEvidenceAsync().WaitAsync(Guard);
            Assert.True(evidence.OriginalOperationsFinished);
            Assert.True(evidence.AllOwnedRootsReleased);
            Assert.All(input.OriginalReads, read => Assert.True(read.IsCompleted));
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Catalog_Replaces_One_Slot_Without_Treating_Sequence_As_Replay_Or_Topology_Authority()
    {
        Fixture f = new();
        byte[] second = Framed(CatalogJson.Replace("\"seq\":7", "\"seq\":1").Replace("\"id\":4", "\"id\":9"));
        CatalogInput input = new(f, [.. Framed(CatalogJson), .. second]);
        DisplayCatalogFrame? first = null;
        input.BeforeRead = offset =>
        {
            if (offset == Framed(CatalogJson).Length)
                first = Assert.IsType<DisplayCatalogFrame>(f.Parent.LatestDisplayCatalog);
        };
        try
        {
            await input.StartAsync();
            await input.Tail.Reached.Task.WaitAsync(Guard);
            DisplayCatalogFrame latest = Assert.IsType<DisplayCatalogFrame>(f.Parent.LatestDisplayCatalog);
            Assert.NotNull(first);
            Assert.NotSame(first, latest);
            Assert.Equal(7, first.Sequence);
            Assert.Equal(4, Assert.Single(first.Displays).Id.Value);
            Assert.Equal(1, latest.Sequence);
            Assert.Equal(9, Assert.Single(latest.Displays).Id.Value);
            Assert.Equal(3, f.Clock.TimerCreates);
            Assert.Equal(1, input.MaxConcurrent);
            Assert.False(f.MonitorTask.IsCompleted);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("display_catalog_offer")]
    [InlineData("display_catalog_accept")]
    [InlineData("auth_success")]
    [InlineData("auth_pending")]
    [InlineData("set_display")]
    [InlineData("unknown")]
    [InlineData("malformed")]
    [InlineData("duplicate")]
    [InlineData("protocol2")]
    public async Task Catalog_Unknown_Handshake_Reverse_Or_Malformed_Message_Revokes_Without_Publishing(string kind)
    {
        string json = kind switch
        {
            "malformed" => "{",
            "duplicate" => CatalogJson.Replace("\"seq\":7", "\"seq\":7,\"seq\":8"),
            "protocol2" => CatalogJson.Replace("\"protocol\":1", "\"protocol\":2"),
            _ => CatalogJson.Replace("host_status", kind),
        };
        Fixture f = new();
        CatalogInput input = new(f, Framed(json));
        try
        {
            await input.StartAsync();
            await f.MonitorTask.WaitAsync(Guard);
            Assert.Null(f.Parent.LatestDisplayCatalog);
            f.AssertRevoked();
            Assert.True(f.ChildStopped);
            FrameProtocolException error = Assert.IsType<FrameProtocolException>(SnapshotError(f.Snapshot));
            Assert.Equal("control-unexpected-data", error.Reason);
            AssertSnapshot(f.Snapshot, "UnexpectedData", false, error);
            Assert.Equal(input.Data.Length, input.Offset);
            Assert.Equal(1, input.MaxConcurrent);
            await f.Parent.CloseAndJoinAsync().WaitAsync(Guard);
            Assert.Contains(f.Parent.LifetimeErrors, item => ReferenceEquals(item, error));
            Assert.All(input.PayloadMemories, memory => Assert.True(memory.Span.IndexOfAnyExcept((byte)0) < 0));
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Catalog_Exactly_256KiB_Including_Whitespace_Is_Accepted()
    {
        int padding = DisplayCatalogFrame.MaxPayloadByteLength - Encoding.UTF8.GetByteCount(CatalogJson);
        Fixture f = new();
        CatalogInput input = new(f, Framed(CatalogJson + new string(' ', padding)));
        try
        {
            await input.StartAsync();
            await input.Tail.Reached.Task.WaitAsync(Guard);
            Assert.NotNull(f.Parent.LatestDisplayCatalog);
            Assert.Equal(DisplayCatalogFrame.MaxPayloadByteLength + 4, input.Offset);
            Assert.Equal(1, input.MaxConcurrent);
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(0U, 4, "length-zero")]
    [InlineData(262145U, 4, "length-exceeds-limit")]
    [InlineData(uint.MaxValue, 1, "control-unexpected-data")]
    public async Task Catalog_Invalid_Length_Is_Rejected_Before_Payload_Read(uint length, int expectedRead, string reason)
    {
        byte[] bytes = new byte[5];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, length);
        Fixture f = new();
        CatalogInput input = new(f, bytes);
        try
        {
            await input.StartAsync();
            await f.MonitorTask.WaitAsync(Guard);
            Assert.Equal(expectedRead, input.Offset);
            FrameProtocolException error = Assert.IsType<FrameProtocolException>(SnapshotError(f.Snapshot));
            Assert.Equal(reason, error.Reason);
            Assert.Null(f.Parent.LatestDisplayCatalog);
            f.AssertRevoked();
            Assert.Empty(input.PayloadMemories);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Catalog_Original_Frame_Deadline_Rejects_Late_Io_With_Timer_Undispatched(int pauseAt)
    {
        Fixture f = new();
        CatalogInput input = new(f, Framed(CatalogJson), pauseAt: pauseAt);
        try
        {
            await input.StartAsync();
            await input.Middle.Reached.Task.WaitAsync(Guard);
            f.Clock.Timestamp = TimeSpan.FromSeconds(10).Ticks; // original observed anchor is zero; no timer callback.
            input.Middle.Open();
            await f.MonitorTask.WaitAsync(Guard);
            Assert.IsType<TimeoutException>(SnapshotError(f.Snapshot));
            Assert.Null(f.Parent.LatestDisplayCatalog);
            f.AssertRevoked();
            Assert.True(f.ChildStopped);
            Assert.Equal(1, input.MaxConcurrent);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Catalog_Local_Stop_During_Uncooperative_Payload_Read_Joins_Original_And_Discards_Late_Data()
    {
        Fixture f = new();
        CatalogInput input = new(f, Framed(CatalogJson), pauseAt: 4);
        try
        {
            await input.StartAsync();
            await input.Middle.Reached.Task.WaitAsync(Guard);
            Task original = await input.PausedRead.Task.WaitAsync(Guard);
            Task join = f.Keep(f.Parent.CloseAndJoinAsync());
            await AssertAwaitPathAsync(original, join);
            Assert.False(join.IsCompleted);
            Assert.Null(f.Parent.LatestDisplayCatalog);
            input.Middle.Open();
            await join.WaitAsync(Guard);
            Assert.Null(f.Parent.LatestDisplayCatalog);
            Assert.All(input.OriginalReads, read => Assert.True(read.IsCompleted));
            Assert.True((bool)PropertyValue(f.Snapshot, "LocalStopObservedBeforeCompletion")!);
            Assert.Equal(1, input.MaxConcurrent);
            Assert.All(input.PayloadMemories, memory => Assert.True(memory.Span.IndexOfAnyExcept((byte)0) < 0));
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Catalog_Continuation_Fault_Retains_Original_Exception_After_Local_Stop(int failAt)
    {
        Fixture f = new();
        CatalogInput input = new(f, Framed(CatalogJson), pauseAt: failAt);
        AggregateException root = MixedRoot();
        input.Failure = root;
        try
        {
            await input.StartAsync();
            await input.Middle.Reached.Task.WaitAsync(Guard);
            Task join = f.Keep(f.Parent.CloseAndJoinAsync());
            input.Middle.Open();
            await join.WaitAsync(Guard);
            Assert.Same(root, SnapshotError(f.Snapshot));
            Assert.Contains(f.Parent.LifetimeErrors, error => ReferenceEquals(error, root));
            Assert.Null(f.Parent.LatestDisplayCatalog);
            Assert.Equal(1, input.MaxConcurrent);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Catalog_Stop_During_Original_Timer_Dispose_Prevents_Parent_Publication_And_Waits()
    {
        Fixture f = new();
        CatalogInput input = new(f, Framed(CatalogJson), pauseAt: 0);
        Pause dispose = f.NewPause();
        TaskCompletionSource<Task> disposePublished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await input.StartAsync();
            await input.Middle.Reached.Task.WaitAsync(Guard);
            f.Clock.TimerDispose = () =>
            {
                Task original = f.Keep(dispose.WaitAsync());
                disposePublished.SetResult(original);
                return original;
            };
            input.Middle.Open();
            await dispose.Reached.Task.WaitAsync(Guard);
            Assert.Null(f.Parent.LatestDisplayCatalog);
            Task join = f.Keep(f.Parent.CloseAndJoinAsync());
            await AssertAwaitPathAsync(await disposePublished.Task.WaitAsync(Guard), join);
            Assert.False(join.IsCompleted);
            Assert.Null(f.Parent.LatestDisplayCatalog);
            dispose.Open();
            await join.WaitAsync(Guard);
            Assert.Null(f.Parent.LatestDisplayCatalog);
            Assert.All(input.PayloadMemories, memory => Assert.True(memory.Span.IndexOfAnyExcept((byte)0) < 0));
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("stop")]
    [InlineData("expiry")]
    [InlineData("regression")]
    public async Task Catalog_Completed_Attempt_Is_Not_Authority_For_Late_Parent_Publication(string cause)
    {
        Fixture f = new();
        CatalogInput input = new(f, Framed(CatalogJson), pauseAt: 0);
        Pause dispose = f.NewPause();
        try
        {
            await input.StartAsync();
            await input.Middle.Reached.Task.WaitAsync(Guard);
            f.Clock.TimerDispose = () => f.Keep(dispose.WaitAsync());
            input.Middle.Open();
            await dispose.Reached.Task.WaitAsync(Guard);
            lock (f.ParentGate)
            {
                object monitor = Field<object>(f.Parent, "_controlMonitor");
                ControlFrameReadAttempt attempt = Field<ControlFrameReadAttempt>(monitor, "_attempt");
                Task<ControlFrameReadAttempt.FrameOutcome> complete = attempt.Completion!;
                dispose.Open();
                ObserveSynchronously(complete);
                Assert.Equal(ControlFrameReadAttempt.AttemptEnd.Frame, attempt.Snapshot.End);
                if (cause == "stop") f.Parent.RevokeForOwnerCleanup();
                else f.Clock.Timestamp = cause == "expiry" ? TimeSpan.FromSeconds(10).Ticks : -1;
            }
            // 错误发布会进入下一次空闲读；先观察两种真实进展，再由目录断言裁决，不能靠Guard超时杀变异。
            await Task.WhenAny(f.MonitorTask, input.Tail.Reached.Task).WaitAsync(Guard);
            Assert.Null(f.Parent.LatestDisplayCatalog);
            await f.MonitorTask.WaitAsync(Guard);
            if (cause == "stop")
                AssertSnapshot(f.Snapshot, "SkippedLocalStop", false, null);
            else if (cause == "expiry") Assert.IsType<TimeoutException>(SnapshotError(f.Snapshot));
            else Assert.IsType<InvalidOperationException>(SnapshotError(f.Snapshot));
            await f.Parent.CloseAndJoinAsync().WaitAsync(Guard);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Catalog_Idle_Past_Video_Attach_Deadline_Starts_Its_Own_Frame_Window()
    {
        Fixture f = new();
        CatalogInput input = new(f, Framed(CatalogJson), pauseAt: 0);
        try
        {
            await input.StartAsync();
            await input.Middle.Reached.Task.WaitAsync(Guard);
            Assert.Equal(1, f.Clock.TimerCreates);
            f.Clock.Timestamp = TimeSpan.FromMinutes(1).Ticks;
            input.Middle.Open();
            await input.Tail.Reached.Task.WaitAsync(Guard);
            Assert.NotNull(f.Parent.LatestDisplayCatalog);
            f.AssertControlLive();
            Assert.Equal(2, f.Clock.TimerCreates);
            Assert.False(f.MonitorTask.IsCompleted);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Catalog_Timer_Dispose_Failure_Is_Preserved_And_Prevents_Released_Roots_Claim()
    {
        Fixture f = new();
        CatalogInput input = new(f, Framed(CatalogJson), pauseAt: 0);
        IOException failure = new("原目录timer释放失败");
        try
        {
            await input.StartAsync();
            await input.Middle.Reached.Task.WaitAsync(Guard);
            f.Clock.TimerDispose = () => f.Keep(Task.FromException(failure));
            input.Middle.Open();
            await f.MonitorTask.WaitAsync(Guard);
            Assert.Same(failure, SnapshotError(f.Snapshot));
            Assert.Null(f.Parent.LatestDisplayCatalog);
            var evidence = await f.Parent.GetDisposalEvidenceAsync().WaitAsync(Guard);
            Assert.True(evidence.OriginalOperationsFinished);
            Assert.False(evidence.AllOwnedRootsReleased);
            Assert.Contains(f.Parent.LifetimeErrors, error => ReferenceEquals(error, failure));
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Catalog_Expiry_Requests_Close_Before_Uncooperative_Read_And_Timer_Join()
    {
        Fixture f = new();
        CatalogInput input = new(f, Framed(CatalogJson), pauseAt: 4);
        Pause close = f.NewPause();
        f.Control.Ssl.ClosePause = close;
        try
        {
            await input.StartAsync();
            await input.Middle.Reached.Task.WaitAsync(Guard);
            Task original = await input.PausedRead.Task.WaitAsync(Guard);
            f.Clock.Timestamp = TimeSpan.FromSeconds(10).Ticks;
            Assert.NotNull(f.Clock.LastTimerFire);
            f.Clock.LastTimerFire();
            await close.Reached.Task.WaitAsync(Guard);
            f.AssertRevoked();
            await f.ChildTask("_join").WaitAsync(Guard);
            await AssertAwaitPathAsync(original, f.MonitorTask);
            Assert.False(f.MonitorTask.IsCompleted);
            Task join = f.Keep(f.Parent.CloseAndJoinAsync());
            await AssertAwaitPathAsync(original, join);
            close.Open();
            input.Middle.Open();
            await join.WaitAsync(Guard);
            Assert.IsType<TimeoutException>(SnapshotError(f.Snapshot));
            Assert.False((bool)PropertyValue(f.Snapshot, "LocalStopObservedBeforeCompletion")!);
            Assert.Null(f.Parent.LatestDisplayCatalog);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Catalog_Continuation_Original_MultiFault_And_Timer_Dispose_Root_Are_Both_Preserved()
    {
        Fixture f = new();
        CatalogInput input = new(f, Framed(CatalogJson), pauseAt: 4);
        Pause failRead = f.NewPause(), dispose = f.NewPause();
        Exception branch = MixedRoot(), sibling = new IOException("原read兄弟"), cleanup = new IOException("原timer释放根");
        Task<int> original = MultiFaultRead(f, failRead, branch, sibling);
        TaskCompletionSource<Task> disposePublished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task DisposeThenFailAsync()
        {
            await dispose.WaitAsync().ConfigureAwait(false);
            throw cleanup;
        }
        input.ReadOverride = original;
        input.BeforeRead = offset =>
        {
            if (offset == 0) f.Clock.TimerDispose = () =>
            {
                Task original = f.Keep(DisposeThenFailAsync());
                disposePublished.SetResult(original);
                return original;
            };
        };
        try
        {
            await input.StartAsync();
            await input.OverrideSelected.Task.WaitAsync(Guard);
            object monitor = Field<object>(f.Parent, "_controlMonitor");
            ControlFrameReadAttempt attempt = Field<ControlFrameReadAttempt>(monitor, "_attempt");
            ControlFrameContinuation continuation = Field<ControlFrameContinuation>(attempt, "_frame");
            // 正常协调阶段是WhenAny，不冒充完整join；先证原read被接续worker直接等待。
            await AssertAwaitPathAsync(original, Assert.IsAssignableFrom<Task>(continuation.Completion));
            failRead.Open();
            await dispose.Reached.Task.WaitAsync(Guard);
            // 读失败通知之后才进入收尾：原timer释放必须贯穿monitor和父join。
            Task disposing = await disposePublished.Task.WaitAsync(Guard);
            await AssertAwaitPathAsync(disposing, f.MonitorTask);
            f.AssertRevoked();
            Task join = f.Keep(f.Parent.CloseAndJoinAsync());
            await AssertAwaitPathAsync(disposing, join);
            Assert.False(join.IsCompleted);
            dispose.Open();
            await join.WaitAsync(Guard);
            Assert.True(original.IsCompleted);
            Assert.True(disposing.IsCompleted);
            AggregateException all = Assert.IsType<AggregateException>(SnapshotError(f.Snapshot));
            Assert.Equal(2, all.InnerExceptions.Count);
            Exception savedRead = Assert.Single(all.InnerExceptions, error => !ReferenceEquals(error, cleanup));
            AssertOriginalRoot(original, savedRead);
            Assert.Contains(all.InnerExceptions, error => ReferenceEquals(error, cleanup));
            Assert.Null(f.Parent.LatestDisplayCatalog);
            var evidence = await f.Parent.GetDisposalEvidenceAsync().WaitAsync(Guard);
            Assert.True(evidence.OriginalOperationsFinished);
            Assert.False(evidence.AllOwnedRootsReleased);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("read-prefix")]
    [InlineData("timer-dispose-prefix")]
    public async Task Catalog_Unreturned_Original_Operation_Does_Not_Claim_All_Roots_Released(string phase)
    {
        Fixture f = new();
        CatalogInput input = new(f, Framed(CatalogJson), pauseAt: 0);
        IOException failure = new("原操作同步前缀失败");
        try
        {
            await input.StartAsync();
            await input.Middle.Reached.Task.WaitAsync(Guard);
            if (phase == "read-prefix") input.PayloadReadPrefixFailure = failure;
            else f.Clock.TimerDispose = () => throw failure;
            input.Middle.Open();
            await f.MonitorTask.WaitAsync(Guard);
            Assert.Same(failure, SnapshotError(f.Snapshot));
            Assert.Null(f.Parent.LatestDisplayCatalog);
            var evidence = await f.Parent.GetDisposalEvidenceAsync().WaitAsync(Guard);
            Assert.True(evidence.OriginalOperationsFinished); // obtained tasks only; no proof of hidden work.
            Assert.False(evidence.AllOwnedRootsReleased);
            Assert.Contains(f.Parent.LifetimeErrors, error => ReferenceEquals(error, failure));
        }
        finally { await f.FinishAsync(); }
    }

    // Instance-level controlled IO, not real TLS. Every returned original Task is kept and joined by Fixture.
    private sealed class CatalogInput
    {
        private readonly Fixture _fixture;
        private readonly int _chunk, _pauseAt;
        private int _offset, _active, _maxConcurrent;
        internal byte[] Data { get; }
        internal int Offset => Volatile.Read(ref _offset);
        internal int MaxConcurrent => Volatile.Read(ref _maxConcurrent);
        private readonly Pause _initial;
        internal Pause Middle { get; }
        internal Pause Tail { get; }
        internal Action<int>? BeforeRead;
        internal Exception? Failure, PayloadReadPrefixFailure;
        internal Task<int>? ReadOverride;
        internal TaskCompletionSource OverrideSelected { get; } = Signal();
        internal TaskCompletionSource<Task<int>> PausedRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ConcurrentQueue<Task<int>> OriginalReads { get; } = new();
        internal ConcurrentQueue<Memory<byte>> PayloadMemories { get; } = new();

        internal CatalogInput(Fixture fixture, byte[] data, int chunk = 262144, int pauseAt = -1)
        {
            _fixture = fixture;
            Data = data;
            _chunk = chunk;
            _pauseAt = pauseAt;
            _initial = fixture.NewPause();
            Middle = fixture.NewPause();
            Tail = fixture.NewPause();
            fixture.Control.Ssl.ReadBody = (memory, token) =>
            {
                fixture.VerifyMonitorWorker();
                Assert.Equal(CancellationToken.None, token);
                int offset = Offset;
                if (offset == 4 && PayloadReadPrefixFailure is { } prefixFailure) throw prefixFailure;
                Task<int> read = fixture.Keep(offset == _pauseAt && ReadOverride is not null
                    ? ReadOverride : ReadAsync(memory));
                OriginalReads.Enqueue(read);
                if (offset == _pauseAt) PausedRead.TrySetResult(read);
                if (ReferenceEquals(read, ReadOverride)) OverrideSelected.TrySetResult();
                return new ValueTask<int>(read);
            };
        }

        internal async Task StartAsync()
        {
            await _fixture.Start().WaitAsync(Guard);
            _initial.Open();
        }

        private async Task<int> ReadAsync(Memory<byte> memory)
        {
            int active = Interlocked.Increment(ref _active);
            InterlockedExtensionsMax(active);
            try
            {
                int offset = Offset;
                if (offset == 0) await _initial.WaitAsync().ConfigureAwait(false);
                BeforeRead?.Invoke(offset);
                if (offset == _pauseAt)
                {
                    await Middle.WaitAsync().ConfigureAwait(false);
                    if (Failure is not null) throw Failure;
                }
                if (offset == Data.Length)
                {
                    await Tail.WaitAsync().ConfigureAwait(false);
                    return 0;
                }
                int count = Math.Min(Math.Min(memory.Length, _chunk), Data.Length - offset);
                Data.AsMemory(offset, count).CopyTo(memory);
                if (offset >= 4 && memory.Length > 1) PayloadMemories.Enqueue(memory);
                Volatile.Write(ref _offset, offset + count);
                return count;
            }
            finally { Interlocked.Decrement(ref _active); }
        }

        private void InterlockedExtensionsMax(int value)
        {
            int old;
            do { old = Volatile.Read(ref _maxConcurrent); if (old >= value) return; }
            while (Interlocked.CompareExchange(ref _maxConcurrent, value, old) != old);
        }
    }
}
