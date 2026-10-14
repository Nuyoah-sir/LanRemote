using System.Collections;
using System.Reflection;
using LanRemote.Core.Models;

namespace LanRemote.Transport.Tests;

public sealed partial class ClientControlMonitorTests
{
    [Fact]
    public void PublicCatalog_Surface_Is_Sealed_GetOnly_Data_Without_Constructors_Wire_Or_Io()
    {
        Type type = typeof(DisplayCatalogSnapshot);
        const BindingFlags publicDeclared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        Assert.True(type.IsPublic);
        Assert.True(type.IsSealed);
        Assert.Empty(type.GetConstructors());
        Assert.True(Assert.Single(type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)).IsAssembly);
        Assert.Empty(type.GetFields(publicDeclared));
        Assert.Empty(type.GetEvents(publicDeclared));
        Assert.Empty(type.GetInterfaces());
        PropertyInfo[] properties = type.GetProperties(publicDeclared);
        Assert.Equal(new[] { "Displays", "Sequence", "TimestampMs" },
            properties.Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Equal(typeof(long), Property(type, "Sequence").PropertyType);
        Assert.Equal(typeof(long), Property(type, "TimestampMs").PropertyType);
        Assert.Equal(typeof(IReadOnlyList<DisplayInfo>), Property(type, "Displays").PropertyType);
        Assert.All(properties, property =>
        {
            Assert.True(property.GetMethod!.IsPublic);
            Assert.False(property.GetMethod.IsStatic);
            Assert.Null(property.GetSetMethod(nonPublic: true));
        });
        Assert.All(type.GetFields(Fields), field => Assert.True(field.IsInitOnly));
        Assert.Equal(new[] { "get_Displays", "get_Sequence", "get_TimestampMs" },
            type.GetMethods(publicDeclared).Select(method => method.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());

        MethodInfo getter = Assert.Single(typeof(AuthenticatedControlSession).GetMethods(publicDeclared),
            method => method.Name == "GetDisplayCatalogSnapshot");
        Assert.False(getter.IsStatic);
        Assert.Equal(type, getter.ReturnType);
        Assert.Empty(getter.GetParameters());
        Assert.False(typeof(DisplayCatalogFrame).IsVisible);
        Assert.Null(typeof(AuthenticatedControlSession).GetProperty("LatestDisplayCatalog", publicDeclared));
        Assert.Null(typeof(AuthenticatedControlSession).GetProperty("Stream", publicDeclared));
        Assert.Null(typeof(AuthenticatedControlSession).GetField("_sessionToken", publicDeclared));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task PublicCatalog_Is_Null_Before_Delivery_And_Until_A_Complete_Frame_Is_Read()
    {
        Fixture f = new(delivered: false);
        CatalogInput input = new(f, Framed(CatalogJson), pauseAt: 4);
        try
        {
            Assert.Null(f.Parent.GetDisplayCatalogSnapshot());
            f.AssertNoMonitor();
            Assert.Equal(0, f.Clock.TimerCreates);
            f.Parent.CommitDelivery();
            Assert.Null(f.Parent.GetDisplayCatalogSnapshot());
            await input.StartAsync();
            await input.Middle.Reached.Task.WaitAsync(Guard);
            Assert.Null(f.Parent.GetDisplayCatalogSnapshot());
            input.Middle.Open();
            await input.Tail.Reached.Task.WaitAsync(Guard);
            Assert.NotNull(f.Parent.GetDisplayCatalogSnapshot());
            Assert.Equal(1, input.MaxConcurrent);
            Assert.Equal(0, f.Control.Ssl.WriteCalls);
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(262144)]
    public async Task PublicCatalog_Real_Reader_Publishes_One_Immutable_Instance_Without_Extra_Io(int chunk)
    {
        Fixture f = new();
        CatalogInput input = new(f, Framed(CatalogJson), chunk);
        try
        {
            await input.StartAsync();
            await input.Tail.Reached.Task.WaitAsync(Guard);
            DisplayCatalogSnapshot snapshot = Assert.IsType<DisplayCatalogSnapshot>(f.Parent.GetDisplayCatalogSnapshot());
            int reads = f.Control.Ssl.ReadRequests.Length;
            for (int index = 0; index < 3; index++) Assert.Same(snapshot, f.Parent.GetDisplayCatalogSnapshot());
            Assert.Same(Assert.IsType<DisplayCatalogFrame>(f.Parent.LatestDisplayCatalog).PublicSnapshot, snapshot);
            Assert.Equal(7, snapshot.Sequence);
            Assert.Equal(123, snapshot.TimestampMs);
            DisplayInfo screen = Assert.Single(snapshot.Displays);
            Assert.Equal(4, screen.Id.Value);
            Assert.Equal(-1920, screen.X);
            Assert.Equal("主屏 <不是HTML>", screen.Name);
            Assert.False(snapshot.Displays is DisplayInfo[]);
            IList<DisplayInfo> generic = Assert.IsAssignableFrom<IList<DisplayInfo>>(snapshot.Displays);
            IList nongeneric = Assert.IsAssignableFrom<IList>(snapshot.Displays);
            Assert.True(generic.IsReadOnly);
            Assert.True(nongeneric.IsReadOnly);
            DisplayInfo replacement = screen with { Name = "不能替换原快照" };
            Assert.Throws<NotSupportedException>(() => generic[0] = replacement);
            Assert.Throws<NotSupportedException>(() => generic.Add(replacement));
            Assert.Throws<NotSupportedException>(() => generic.Clear());
            Assert.Throws<NotSupportedException>(() => nongeneric[0] = replacement);
            Assert.Throws<NotSupportedException>(() => nongeneric.Add(replacement));
            Assert.Throws<NotSupportedException>(() => nongeneric.RemoveAt(0));
            Array.Fill(input.Data, (byte)0);
            Assert.Same(screen, Assert.Single(snapshot.Displays));
            Assert.Equal("主屏 <不是HTML>", screen.Name);
            Assert.Equal(reads, f.Control.Ssl.ReadRequests.Length);
            Assert.Equal(1, input.MaxConcurrent);
            Assert.Equal(2, f.Clock.TimerCreates);
            Assert.Equal(0, f.Control.Ssl.WriteCalls);
            Assert.Equal(0, f.Control.Ssl.FlushCalls);
            f.AssertControlLive();
            input.Tail.Open();
            await f.MonitorTask.WaitAsync(Guard);
            Assert.Null(f.Parent.GetDisplayCatalogSnapshot());
            Assert.Equal(7, snapshot.Sequence);
            Assert.Same(screen, Assert.Single(snapshot.Displays));
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task PublicCatalog_Replaces_The_Instance_With_Lower_Sequence_And_Retains_The_Old_Snapshot()
    {
        Fixture f = new();
        byte[] firstFrame = Framed(CatalogJson);
        byte[] secondFrame = Framed(CatalogJson.Replace("\"seq\":7", "\"seq\":1")
            .Replace("\"timestampMs\":123", "\"timestampMs\":0").Replace("\"id\":4", "\"id\":9"));
        CatalogInput input = new(f, [.. firstFrame, .. secondFrame], pauseAt: firstFrame.Length);
        try
        {
            await input.StartAsync();
            await input.Middle.Reached.Task.WaitAsync(Guard);
            DisplayCatalogSnapshot first = Assert.IsType<DisplayCatalogSnapshot>(f.Parent.GetDisplayCatalogSnapshot());
            Assert.Same(first, f.Parent.GetDisplayCatalogSnapshot());
            input.Middle.Open();
            await input.Tail.Reached.Task.WaitAsync(Guard);
            DisplayCatalogSnapshot latest = Assert.IsType<DisplayCatalogSnapshot>(f.Parent.GetDisplayCatalogSnapshot());
            Assert.NotSame(first, latest);
            Assert.Same(latest, f.Parent.GetDisplayCatalogSnapshot());
            Assert.Equal(7, first.Sequence);
            Assert.Equal(123, first.TimestampMs);
            Assert.Equal(4, Assert.Single(first.Displays).Id.Value);
            Assert.Equal(1, latest.Sequence);
            Assert.Equal(0, latest.TimestampMs);
            Assert.Equal(9, Assert.Single(latest.Displays).Id.Value);
            Assert.Equal(1, input.MaxConcurrent);
            Assert.Equal(3, f.Clock.TimerCreates);
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("owner")]
    [InlineData("dispose")]
    [InlineData("dispose-async")]
    public async Task PublicCatalog_Revocation_Hides_Current_Data_Before_Original_Io_Exits_And_Discards_Late_Replacement(string stop)
    {
        Fixture f = new();
        byte[] firstFrame = Framed(CatalogJson);
        byte[] secondFrame = Framed(CatalogJson.Replace("\"seq\":7", "\"seq\":8").Replace("\"id\":4", "\"id\":9"));
        CatalogInput input = new(f, [.. firstFrame, .. secondFrame], pauseAt: firstFrame.Length + 4);
        try
        {
            await input.StartAsync();
            await input.Middle.Reached.Task.WaitAsync(Guard);
            Task original = await input.PausedRead.Task.WaitAsync(Guard);
            DisplayCatalogSnapshot history = Assert.IsType<DisplayCatalogSnapshot>(f.Parent.GetDisplayCatalogSnapshot());
            Task? publicDispose = null;
            if (stop == "owner")
            {
                f.Parent.RevokeForOwnerCleanup();
                // 原目录attempt收到停止后可并发请求close；这里只证明资格已撤销，不能断言还未关线。
                f.AssertRevoked();
            }
            else if (stop == "dispose") f.Parent.Dispose();
            else publicDispose = f.Keep(f.Parent.DisposeAsync().AsTask());
            f.AssertRevoked();
            Assert.Null(f.Parent.GetDisplayCatalogSnapshot());
            Assert.False(original.IsCompleted);
            Assert.False(f.MonitorTask.IsCompleted);
            Assert.Equal(7, history.Sequence);
            Assert.Equal(4, Assert.Single(history.Displays).Id.Value);
            Task join = f.Keep(f.Parent.CloseAndJoinAsync());
            await AssertAwaitPathAsync(original, join);
            Assert.False(join.IsCompleted);
            input.Middle.Open();
            await join.WaitAsync(Guard);
            if (publicDispose is not null) await publicDispose.WaitAsync(Guard);
            Assert.Equal(input.Data.Length, input.Offset);
            Assert.Null(f.Parent.GetDisplayCatalogSnapshot());
            Assert.Equal(7, history.Sequence);
            Assert.Equal(4, Assert.Single(history.Displays).Id.Value);
            Assert.Equal(1, input.MaxConcurrent);
            Assert.All(input.OriginalReads, read => Assert.True(read.IsCompleted));
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task PublicCatalog_Completed_Frame_Cannot_Publish_After_Parent_Revocation()
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
                Task<ControlFrameReadAttempt.FrameOutcome> completion = attempt.Completion!;
                dispose.Open();
                ObserveSynchronously(completion);
                Assert.Equal(ControlFrameReadAttempt.AttemptEnd.Frame, attempt.Snapshot.End);
                f.Parent.RevokeForOwnerCleanup();
                Assert.Null(f.Parent.GetDisplayCatalogSnapshot());
            }
            // 原组合已成功，只让真实父 reader 在撤销后继续；不直接写入任何目录状态。
            await Task.WhenAny(f.MonitorTask, input.Tail.Reached.Task).WaitAsync(Guard);
            Assert.Null(f.Parent.GetDisplayCatalogSnapshot());
            await f.MonitorTask.WaitAsync(Guard);
            AssertSnapshot(f.Snapshot, "SkippedLocalStop", false, null);
            Assert.Equal(1, input.MaxConcurrent);
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task PublicCatalog_Two_Sessions_Publish_And_Revoke_Independently()
    {
        Fixture left = new(), right = new();
        CatalogInput leftInput = new(left, Framed(CatalogJson));
        CatalogInput rightInput = new(right, Framed(CatalogJson.Replace("\"id\":4", "\"id\":9")), pauseAt: 4);
        try
        {
            await leftInput.StartAsync();
            await leftInput.Tail.Reached.Task.WaitAsync(Guard);
            DisplayCatalogSnapshot leftSnapshot = Assert.IsType<DisplayCatalogSnapshot>(left.Parent.GetDisplayCatalogSnapshot());
            Assert.Null(right.Parent.GetDisplayCatalogSnapshot());
            await rightInput.StartAsync();
            await rightInput.Middle.Reached.Task.WaitAsync(Guard);
            Assert.Null(right.Parent.GetDisplayCatalogSnapshot());
            Assert.Same(leftSnapshot, left.Parent.GetDisplayCatalogSnapshot());
            rightInput.Middle.Open();
            await rightInput.Tail.Reached.Task.WaitAsync(Guard);
            DisplayCatalogSnapshot rightSnapshot = Assert.IsType<DisplayCatalogSnapshot>(right.Parent.GetDisplayCatalogSnapshot());
            Assert.NotSame(leftSnapshot, rightSnapshot);
            Assert.Equal(leftSnapshot.Sequence, rightSnapshot.Sequence);
            Assert.Equal(leftSnapshot.TimestampMs, rightSnapshot.TimestampMs);
            Assert.Equal(4, Assert.Single(leftSnapshot.Displays).Id.Value);
            Assert.Equal(9, Assert.Single(rightSnapshot.Displays).Id.Value);
            left.Parent.RevokeForOwnerCleanup();
            Assert.Null(left.Parent.GetDisplayCatalogSnapshot());
            Assert.Same(rightSnapshot, right.Parent.GetDisplayCatalogSnapshot());
            Assert.False(left.MonitorTask.IsCompleted);
            right.AssertControlLive();
            rightInput.Tail.Open();
            await right.MonitorTask.WaitAsync(Guard);
            Assert.Null(right.Parent.GetDisplayCatalogSnapshot());
            Assert.Equal(4, Assert.Single(leftSnapshot.Displays).Id.Value);
            Assert.Equal(9, Assert.Single(rightSnapshot.Displays).Id.Value);
            Assert.Equal(1, leftInput.MaxConcurrent);
            Assert.Equal(1, rightInput.MaxConcurrent);
        }
        finally
        {
            try { await left.FinishAsync(); }
            finally { await right.FinishAsync(); }
        }
    }
}
