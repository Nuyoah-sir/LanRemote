using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;
using Admission = LanRemote.Transport.SessionRegistry.SessionRegistration.DisplaySwitchAdmission;
using Reservation = LanRemote.Transport.SessionRegistry.SessionRegistration.DisplaySwitchAdmission.DisplaySwitchReservation;
using Status = LanRemote.Transport.SessionRegistry.SessionRegistration.DisplaySwitchAdmissionStatus;

namespace LanRemote.Transport.Tests;

public sealed class DisplaySwitchAdmissionTests
{
    // 只验证已发布来源上的准入，不模拟切屏执行、网络回执、输入注入或帧屏障。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_Attachment_Or_Publication_Does_Not_Consume_Creation(bool attached)
    {
        await using Fixture fixture = new(attach: attached);

        Assert.Throws<InvalidOperationException>(() => fixture.Create());
        Assert.Equal(0, fixture.Initial.StartCalls);
        Assert.Equal(0, fixture.Initial.StopCalls);
        Assert.Equal(0, fixture.Initial.DisposeCalls);
        if (!attached) fixture.Attach();
        await fixture.InstallAndPublishAsync();
        using Admission admission = fixture.Create();
        using Reservation reservation = Accept(admission, fixture.Request(0));
        Assert.True(reservation.IsCurrent);
        AssertResourcesUnchanged(fixture);
    }

    [Theory]
    [InlineData("withdraw")]
    [InlineData("control-cancel")]
    [InlineData("unregister")]
    [InlineData("lease-complete")]
    public async Task Invalidated_Parent_Lease_Or_Publication_Cannot_Create(string reason)
    {
        await using Fixture fixture = new();
        await fixture.InstallAndPublishAsync();
        fixture.Invalidate(reason);

        Assert.Throws<InvalidOperationException>(() => fixture.Create());
        AssertProducerUnchanged(fixture.Initial);
        Assert.False(fixture.Source.Termination.IsCompleted);
        Assert.Equal(reason == "lease-complete", fixture.Lease!.Completed.IsCompleted);
        Assert.Equal(reason == "unregister", fixture.Lease.Revoked.IsCompleted);
    }

    [Fact]
    public async Task Source_Termination_Prevents_Creation_Before_Full_Completion()
    {
        await using Fixture fixture = new();
        await fixture.InstallAndPublishAsync();
        fixture.Initial.HoldCleanup();
        Task closing = fixture.Source.DisposeAsync().AsTask();
        try
        {
            Assert.True(fixture.Source.Termination.IsCompleted);
            Assert.False(closing.IsCompleted);
            Assert.False(fixture.Source.Completion.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => fixture.Create());
            Assert.False(fixture.Lease!.Completed.IsCompleted);
            Assert.False(fixture.Lease.Revoked.IsCompleted);
        }
        finally
        {
            fixture.Initial.ReleaseCleanup();
            await closing;
        }
    }

    [Fact]
    public async Task Null_Catalog_Does_Not_Consume_Creation()
    {
        await using Fixture fixture = new();
        await fixture.InstallAndPublishAsync();

        ArgumentNullException error = Assert.Throws<ArgumentNullException>(
            () => fixture.Registration.CreateDisplaySwitchAdmission(null!));
        Assert.Equal("catalog", error.ParamName);
        using Admission admission = fixture.Create();
        using Reservation reservation = Accept(admission, fixture.Request(0));
        Assert.True(reservation.IsCurrent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task One_Entry_Can_Create_Only_Once_Even_After_Close(bool close)
    {
        await using Fixture fixture = new();
        await fixture.InstallAndPublishAsync();
        using Admission admission = fixture.Create();
        if (close)
        {
            admission.Dispose();
            admission.Dispose();
        }

        Assert.Throws<InvalidOperationException>(() => fixture.Create());
        if (close) Reject(admission, fixture.Request(0), Status.Unavailable);
        else
        {
            using Reservation reservation = Accept(admission, fixture.Request(0));
            Assert.True(reservation.IsCurrent);
        }
        AssertResourcesUnchanged(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manually_Constructed_Admission_Cannot_Bypass_Entry_Identity(bool createRealFirst)
    {
        await using Fixture fixture = new();
        await fixture.InstallAndPublishAsync();
        using Admission? first = createRealFirst ? fixture.Create() : null;
        using Admission forged = new(fixture.Registration, fixture.Catalog);

        Reject(forged, fixture.Request(long.MaxValue), Status.Unavailable);
        using Admission admission = first ?? fixture.Create();
        using Reservation reservation = Accept(admission, fixture.Request(0));
        forged.Dispose();
        Reject(forged, fixture.Request(1), Status.Unavailable);
        Assert.True(reservation.IsCurrent);
        Assert.Throws<InvalidOperationException>(() => fixture.Create());
        AssertResourcesUnchanged(fixture);
    }

    [Fact]
    public async Task Concurrent_Creation_Publishes_Exactly_One_Admission()
    {
        await using Fixture fixture = new();
        await fixture.InstallAndPublishAsync();
        Admission? first = null, second = null;
        Exception? firstError = null, secondError = null;
        try
        {
            await RunConcurrentlyAsync(
                () => firstError = Record.Exception(() => { first = fixture.Create(); }),
                () => secondError = Record.Exception(() => { second = fixture.Create(); }));

            Assert.NotEqual(first is null, second is null);
            Assert.Null(first is not null ? firstError : secondError);
            Assert.IsType<InvalidOperationException>(first is null ? firstError : secondError);
            using Reservation reservation = Accept(first ?? second!, fixture.Request(0));
            Assert.True(reservation.IsCurrent);
            AssertResourcesUnchanged(fixture);
        }
        finally
        {
            first?.Dispose();
            second?.Dispose();
        }
    }

    [Theory]
    [InlineData(SessionPermission.ViewOnly)]
    [InlineData(SessionPermission.Control)]
    public async Task Display_Selection_Is_Legal_For_Both_Permissions(SessionPermission permission)
    {
        await using Fixture fixture = new(permission: permission);
        await fixture.InstallAndPublishAsync();
        using Admission admission = fixture.Create();
        using Reservation reservation = Accept(admission, fixture.Request(0));

        Assert.Equal(new DisplayId(17), reservation.Target.Id);
        Assert.Equal(permission, Assert.Single(fixture.Registry.Snapshot()).GrantedPermission);
        Assert.True(reservation.IsCurrent);
        AssertResourcesUnchanged(fixture);
    }

    [Fact]
    public async Task Expired_Attach_Window_Does_Not_Invalidate_An_Already_Attached_Child()
    {
        await using Fixture fixture = new();
        fixture.Clock.Advance(TimeSpan.FromDays(1));
        Assert.True(fixture.AttachWindow.IsExpired);
        await fixture.InstallAndPublishAsync();
        using Admission admission = fixture.Create();
        using Reservation reservation = Accept(admission, fixture.Request(0));

        Assert.True(reservation.IsCurrent);
        AssertResourcesUnchanged(fixture);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(91L)]
    public async Task First_Sequence_May_Be_Zero_Or_Skip_Ahead(long sequence)
    {
        await using Fixture fixture = new();
        await fixture.InstallAndPublishAsync();
        using Admission admission = fixture.Create();
        using Reservation reservation = Accept(admission, fixture.Request(sequence));

        Assert.Equal(sequence, reservation.Request.Sequence);
        Assert.True(reservation.IsCurrent);
    }

    [Fact]
    public async Task Accepted_Sequences_Are_Strictly_Increasing_And_May_Skip()
    {
        await using Fixture fixture = new();
        await fixture.InstallAndPublishAsync();
        using Admission admission = fixture.Create();
        Accept(admission, fixture.Request(0)).Dispose();
        Accept(admission, fixture.Request(10)).Dispose();

        Reject(admission, fixture.Request(0), Status.StaleSequence);
        Reject(admission, fixture.Request(9), Status.StaleSequence);
        Reject(admission, fixture.Request(10), Status.StaleSequence);
        using Reservation current = Accept(admission, fixture.Request(11));
        Assert.True(current.IsCurrent);
        AssertResourcesUnchanged(fixture);
    }

    [Fact]
    public async Task Invalid_Requests_Do_Not_Consume_Sequence_Or_Reserve_The_Slot()
    {
        await using Fixture fixture = new();
        await fixture.InstallAndPublishAsync();
        using Admission admission = fixture.Create();

        Reject(admission, fixture.Request(20, sessionId: Guid.NewGuid()), Status.SessionMismatch);
        Reject(admission, fixture.Request(20, catalogSequence: fixture.Catalog.Sequence + 1), Status.CatalogMismatch);
        Reject(admission, fixture.Request(20, displayId: new DisplayId(255)), Status.DisplayNotFound);
        using Reservation current = Accept(admission, fixture.Request(20));
        Assert.True(current.IsCurrent);
        AssertResourcesUnchanged(fixture);
    }

    [Fact]
    public async Task Failure_Priority_Is_Unavailable_Session_Busy_Stale_Catalog_Display()
    {
        await using Fixture fixture = new();
        await fixture.InstallAndPublishAsync();
        using Admission admission = fixture.Create();
        using Reservation active = Accept(admission, fixture.Request(5));
        DisplayId missing = new(255);
        long wrongCatalog = fixture.Catalog.Sequence + 1;

        Reject(admission, fixture.Request(0, Guid.NewGuid(), wrongCatalog, missing), Status.SessionMismatch);
        Reject(admission, fixture.Request(0, catalogSequence: wrongCatalog, displayId: missing), Status.Busy);
        active.Dispose();
        Reject(admission, fixture.Request(5, catalogSequence: wrongCatalog, displayId: missing), Status.StaleSequence);
        Reject(admission, fixture.Request(6, catalogSequence: wrongCatalog, displayId: missing), Status.CatalogMismatch);
        Reject(admission, fixture.Request(6, displayId: missing), Status.DisplayNotFound);
        using Reservation next = Accept(admission, fixture.Request(6));
        admission.Dispose();
        Reject(admission, fixture.Request(0, Guid.NewGuid(), wrongCatalog, missing), Status.Unavailable);
        Assert.False(next.IsCurrent);
        AssertResourcesUnchanged(fixture);
    }

    [Fact]
    public async Task Null_Request_Does_Not_Consume_Sequence_Or_Close_Admission()
    {
        await using Fixture fixture = new();
        await fixture.InstallAndPublishAsync();
        using Admission admission = fixture.Create();

        ArgumentNullException error = Assert.Throws<ArgumentNullException>(() => admission.TryReserve(null!, out _));
        Assert.Equal("request", error.ParamName);
        using Reservation current = Accept(admission, fixture.Request(0));
        Assert.True(current.IsCurrent);
    }

    [Fact]
    public async Task Busy_Request_Is_Not_Queued_And_Does_Not_Consume_Its_Sequence()
    {
        await using Fixture fixture = new();
        await fixture.InstallAndPublishAsync();
        using Admission admission = fixture.Create();
        using Reservation first = Accept(admission, fixture.Request(0));
        DisplaySwitchRequestFrame retry = fixture.Request(10);

        Reject(admission, retry, Status.Busy);
        Assert.True(first.IsCurrent);
        first.Dispose();
        using Reservation intermediate = Accept(admission, fixture.Request(1));
        Reject(admission, retry, Status.Busy);
        intermediate.Dispose();
        using Reservation retried = Accept(admission, retry);
        Assert.Same(retry, retried.Request);
        Assert.True(retried.IsCurrent);
        AssertResourcesUnchanged(fixture);
    }

    [Fact]
    public async Task Maximum_Sequence_Is_Accepted_Once_Without_Overflow_Or_Reset()
    {
        await using Fixture fixture = new();
        await fixture.InstallAndPublishAsync();
        using Admission admission = fixture.Create();
        Reject(admission, fixture.Request(long.MaxValue, displayId: new DisplayId(255)), Status.DisplayNotFound);
        using Reservation maximum = Accept(admission, fixture.Request(long.MaxValue));

        Reject(admission, fixture.Request(long.MaxValue), Status.Busy);
        maximum.Dispose();
        maximum.Dispose();
        Reject(admission, fixture.Request(long.MaxValue), Status.StaleSequence);
        Reject(admission, fixture.Request(long.MaxValue - 1), Status.StaleSequence);
        Reject(admission, fixture.Request(0), Status.StaleSequence);
        AssertResourcesUnchanged(fixture);
    }

    [Fact]
    public async Task Old_Reservation_Dispose_Is_Idempotent_And_Cannot_Release_A_New_Slot()
    {
        await using Fixture fixture = new();
        await fixture.InstallAndPublishAsync();
        using Admission admission = fixture.Create();
        DisplaySwitchRequestFrame firstRequest = fixture.Request(0);
        using Reservation first = Accept(admission, firstRequest);
        DisplayInfo firstTarget = first.Target;
        first.Dispose();
        Assert.False(first.IsCurrent);
        using Reservation current = Accept(admission, fixture.Request(1));

        first.Dispose();
        first.Dispose();
        Assert.True(current.IsCurrent);
        Reject(admission, fixture.Request(2), Status.Busy);
        Assert.Same(firstRequest, first.Request);
        Assert.Same(firstTarget, first.Target);
        current.Dispose();
        using Reservation next = Accept(admission, fixture.Request(2));
        Assert.True(next.IsCurrent);
        AssertResourcesUnchanged(fixture);
    }

    [Theory]
    [InlineData("withdraw")]
    [InlineData("control-cancel")]
    [InlineData("unregister")]
    [InlineData("lease-complete")]
    [InlineData("admission-close")]
    public async Task Invalidation_Revokes_Current_Snapshot_But_Preserves_Reservation_History(string reason)
    {
        await using Fixture fixture = new();
        await fixture.InstallAndPublishAsync();
        using Admission admission = fixture.Create();
        DisplaySwitchRequestFrame request = fixture.Request(7);
        using Reservation reservation = Accept(admission, request);
        DisplayInfo target = reservation.Target;
        Assert.True(reservation.IsCurrent);

        if (reason == "admission-close") admission.Dispose();
        else fixture.Invalidate(reason);

        Assert.False(reservation.IsCurrent);
        Assert.Same(request, reservation.Request);
        Assert.Same(target, reservation.Target);
        Reject(admission, fixture.Request(8), Status.Unavailable);
        reservation.Dispose();
        reservation.Dispose();
        Assert.False(reservation.IsCurrent);
        Assert.Same(request, reservation.Request);
        Assert.Same(target, reservation.Target);
        AssertProducerUnchanged(fixture.Initial);
        Assert.False(fixture.Source.Termination.IsCompleted);
    }

    [Fact]
    public async Task Source_Termination_Revokes_Current_Snapshot_Before_Producer_Join()
    {
        await using Fixture fixture = new();
        await fixture.InstallAndPublishAsync();
        using Admission admission = fixture.Create();
        DisplaySwitchRequestFrame request = fixture.Request(0);
        using Reservation reservation = Accept(admission, request);
        DisplayInfo target = reservation.Target;
        fixture.Initial.HoldCleanup();
        Task closing = fixture.Source.DisposeAsync().AsTask();
        try
        {
            Assert.True(fixture.Source.Termination.IsCompleted);
            Assert.False(fixture.Source.Completion.IsCompleted);
            Assert.False(reservation.IsCurrent);
            Reject(admission, fixture.Request(1), Status.Unavailable);
            Assert.Same(request, reservation.Request);
            Assert.Same(target, reservation.Target);
            Assert.False(fixture.Lease!.Completed.IsCompleted);
        }
        finally
        {
            fixture.Initial.ReleaseCleanup();
            await closing;
        }
    }

    [Fact]
    public async Task Reused_Session_Id_Does_Not_Rebind_Old_Admission_Or_Reservation()
    {
        await using Fixture old = new();
        await old.InstallAndPublishAsync();
        using Admission oldAdmission = old.Create();
        DisplaySwitchRequestFrame oldRequest = old.Request(10);
        using Reservation oldReservation = Accept(oldAdmission, oldRequest);
        DisplayInfo oldTarget = oldReservation.Target;
        old.Registration.Dispose();
        await using Fixture current = new(old.Registry, old.SessionId);
        await current.InstallAndPublishAsync();
        using Admission currentAdmission = current.Create();
        using Reservation currentReservation = Accept(currentAdmission, current.Request(0));

        Assert.Throws<InvalidOperationException>(() => old.Create());
        Reject(oldAdmission, old.Request(11), Status.Unavailable);
        Assert.False(oldReservation.IsCurrent);
        oldReservation.Dispose();
        oldAdmission.Dispose();
        old.Publication!.Dispose();
        old.Registration.Dispose();
        Assert.True(currentReservation.IsCurrent);
        Reject(currentAdmission, current.Request(1), Status.Busy);
        Assert.Same(oldRequest, oldReservation.Request);
        Assert.Same(oldTarget, oldReservation.Target);
        Assert.True(old.Lease!.Revoked.IsCompletedSuccessfully);
        Assert.NotSame(old.Registration.VideoCompletion, current.Registration.VideoCompletion);
        currentReservation.Dispose();
        using Reservation next = Accept(currentAdmission, current.Request(1));
        Assert.True(next.IsCurrent);
        AssertResourcesUnchanged(current);
        AssertProducerUnchanged(old.Initial);
    }

    [Fact]
    public async Task Catalog_Copies_Caller_List_And_Reservation_Retains_Original_Request_And_Record()
    {
        await using Fixture fixture = new();
        await fixture.InstallAndPublishAsync();
        DisplayInfo primary = new(new DisplayId(3), "主显示器", 0, 0, 1920, 1080, true);
        DisplayInfo target = new(new DisplayId(17), "左侧显示器", -1280, -200, 1280, 1024, false);
        List<DisplayInfo> callerList = [primary, target];
        DisplayCatalogFrame catalog = new(123, 456, callerList);
        using Admission admission = fixture.Registration.CreateDisplaySwitchAdmission(catalog);
        callerList[1] = target with { Id = new DisplayId(255), Name = "替换条目", X = 4000 };
        callerList.Clear();
        DisplaySwitchRequestFrame request = fixture.Request(7, catalogSequence: 123, displayId: target.Id);
        using Reservation reservation = Accept(admission, request);

        Assert.Equal(2, catalog.Displays.Count);
        Assert.Same(target, catalog.Displays[1]);
        Assert.Same(request, reservation.Request);
        Assert.Same(target, reservation.Target);
        Assert.Equal(-1280, reservation.Target.X);
        Assert.Equal(-200, reservation.Target.Y);
        Assert.Equal(123, reservation.Request.CatalogSequence);
        reservation.Dispose();
        Reject(admission, fixture.Request(8, catalogSequence: 123, displayId: new DisplayId(255)), Status.DisplayNotFound);
        using Reservation next = Accept(admission, fixture.Request(8, catalogSequence: 123, displayId: target.Id));
        Assert.Same(target, next.Target);
        Assert.Same(request, reservation.Request);
        Assert.Same(target, reservation.Target);
        AssertResourcesUnchanged(fixture);
    }

    [Fact]
    public async Task Admission_And_Reservation_Dispose_Only_Close_Their_Own_Logical_State()
    {
        await using Fixture fixture = new();
        await fixture.InstallAndPublishAsync();
        Task completion = fixture.Registration.VideoCompletion;
        using Admission admission = fixture.Create();
        using Reservation first = Accept(admission, fixture.Request(0));
        AssertResourcesUnchanged(fixture);
        first.Dispose();
        first.Dispose();
        AssertResourcesUnchanged(fixture);
        using Reservation second = Accept(admission, fixture.Request(1));
        admission.Dispose();
        admission.Dispose();

        Assert.False(second.IsCurrent);
        Reject(admission, fixture.Request(2), Status.Unavailable);
        Assert.Same(completion, fixture.Registration.VideoCompletion);
        AssertResourcesUnchanged(fixture);
        second.Dispose();
        second.Dispose();
        AssertResourcesUnchanged(fixture);
    }

    [Fact]
    public async Task Two_Concurrent_Requests_Have_One_Winner_And_No_Queued_Reservation()
    {
        await using Fixture fixture = new();
        await fixture.InstallAndPublishAsync();
        using Admission admission = fixture.Create();
        Status firstStatus = default, secondStatus = default;
        Reservation? first = null, second = null;
        try
        {
            await RunConcurrentlyAsync(
                () => firstStatus = admission.TryReserve(fixture.Request(0, displayId: new DisplayId(3)), out first),
                () => secondStatus = admission.TryReserve(fixture.Request(0), out second));

            Assert.Equal(1, new[] { firstStatus, secondStatus }.Count(status => status == Status.Accepted));
            Assert.Equal(1, new[] { firstStatus, secondStatus }.Count(status => status == Status.Busy));
            Assert.NotEqual(first is null, second is null);
            Reservation winner = first ?? second!;
            Assert.True(winner.IsCurrent);
            winner.Dispose();
            using Reservation next = Accept(admission, fixture.Request(1));
            Assert.True(next.IsCurrent);
            AssertResourcesUnchanged(fixture);
        }
        finally
        {
            first?.Dispose();
            second?.Dispose();
        }
    }

    [Fact]
    public async Task Concurrent_Unregister_And_Reserve_Have_A_Linearizable_Result()
    {
        await using Fixture fixture = new();
        await fixture.InstallAndPublishAsync();
        using Admission admission = fixture.Create();
        DisplaySwitchRequestFrame request = fixture.Request(0);
        Status status = default;
        Reservation? reservation = null;
        try
        {
            await RunConcurrentlyAsync(
                () => status = admission.TryReserve(request, out reservation),
                fixture.Registration.Dispose);

            Assert.True(status is Status.Accepted or Status.Unavailable);
            if (status == Status.Accepted)
            {
                Assert.NotNull(reservation);
                Assert.Same(request, reservation.Request);
                Assert.Same(fixture.Catalog.Displays[1], reservation.Target);
                Assert.False(reservation.IsCurrent);
            }
            else Assert.Null(reservation);
            Assert.Equal(0, fixture.Registry.ActiveSessionCount);
            Assert.True(fixture.Lease!.Revoked.IsCompletedSuccessfully);
            Reject(admission, fixture.Request(1), Status.Unavailable);
            Assert.False(fixture.Lease.Completed.IsCompleted);
            Assert.False(fixture.Source.Termination.IsCompleted);
            AssertProducerUnchanged(fixture.Initial);
        }
        finally
        {
            reservation?.Dispose();
        }
    }

    private static Reservation Accept(Admission admission, DisplaySwitchRequestFrame request)
    {
        Status status = admission.TryReserve(request, out Reservation? reservation);
        Assert.Equal(Status.Accepted, status);
        Assert.NotNull(reservation);
        Assert.Same(request, reservation.Request);
        return reservation;
    }

    private static void Reject(Admission admission, DisplaySwitchRequestFrame request, Status expected)
    {
        Assert.Equal(expected, admission.TryReserve(request, out Reservation? reservation));
        Assert.Null(reservation);
    }

    private static void AssertProducerUnchanged(Producer producer)
    {
        Assert.Equal(1, producer.StartCalls);
        Assert.Equal(0, producer.ReadCalls);
        Assert.Equal(0, producer.StopCalls);
        Assert.Equal(0, producer.DisposeCalls);
        Assert.Equal(0, producer.CompletionReads);
    }

    private static void AssertResourcesUnchanged(Fixture fixture)
    {
        AssertProducerUnchanged(fixture.Initial);
        Assert.Equal(1, fixture.Registry.ActiveSessionCount);
        Assert.False(fixture.Control.IsCancellationRequested);
        Assert.False(fixture.Source.Termination.IsCompleted);
        Assert.False(fixture.Source.Completion.IsCompleted);
        Assert.False(fixture.Lease!.Revoked.IsCompleted);
        Assert.False(fixture.Lease.Completed.IsCompleted);
        Assert.Same(fixture.Lease.Completed, fixture.Registration.VideoCompletion);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task RunConcurrentlyAsync(Action first, Action second)
    {
        TaskCompletionSource start = Signal(), firstReady = Signal(), secondReady = Signal();
        Task firstTask = Task.Run(async () =>
        {
            firstReady.SetResult();
            await start.Task;
            first();
        });
        Task secondTask = Task.Run(async () =>
        {
            secondReady.SetResult();
            await start.Task;
            second();
        });
        try
        {
            await Task.WhenAll(firstReady.Task, secondReady.Task);
        }
        finally
        {
            // 即使等待失败也放行，且观察原始任务，不以延迟或超时证明互斥。
            start.TrySetResult();
            await Task.WhenAll(firstTask, secondTask);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private static readonly IPAddress Remote = IPAddress.Parse("192.168.10.20");
        private static readonly byte[] Token = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        private static readonly byte[] Pin = Enumerable.Range(64, 32).Select(value => (byte)value).ToArray();
        private static readonly byte[] Nonce = Enumerable.Range(128, 16).Select(value => (byte)value).ToArray();
        private bool _installed;

        internal Fixture(SessionRegistry? registry = null, Guid? sessionId = null, bool attach = true,
            SessionPermission permission = SessionPermission.ViewOnly)
        {
            Registry = registry ?? new SessionRegistry();
            SessionId = sessionId ?? Guid.NewGuid();
            Video = new ConnectionSecurityContext(IPAddress.Parse("192.168.10.10"), Remote,
                23456, SslProtocols.Tls13, Pin);
            AttachWindow = new VideoAttachWindow(Clock, 15_000);
            Registration = Registry.Register(SessionId, Guid.NewGuid(), Guid.NewGuid(), "切屏准入接缝测试",
                permission, Remote, 12345, Token, AttachWindow, Pin, Control.Token,
                V2AgreementFixture.Create(SessionId));
            Source = new ReplaceableVideoFrameSource(SessionId);
            if (attach) Attach();
        }

        internal Guid SessionId { get; }
        internal SessionRegistry Registry { get; }
        internal SessionRegistry.SessionRegistration Registration { get; }
        internal ReplaceableVideoFrameSource Source { get; }
        internal ConnectionSecurityContext Video { get; }
        internal ManualDeadlineClock Clock { get; } = new();
        internal VideoAttachWindow AttachWindow { get; }
        internal CancellationTokenSource Control { get; } = new();
        internal Producer Initial { get; } = new();
        internal VideoAttachLease? Lease { get; private set; }
        internal IDisposable? Publication { get; private set; }
        internal DisplayCatalogFrame Catalog { get; } = new(23, 44,
        [
            new DisplayInfo(new DisplayId(3), "主显示器", 0, 0, 1920, 1080, true),
            new DisplayInfo(new DisplayId(17), "左侧显示器", -1280, -200, 1280, 1024, false),
        ]);

        internal void Attach()
        {
            // 独立构造真实 proof transcript，不直接造租约或调用生产证明 helper。
            byte[] transcript = new byte[83];
            Encoding.UTF8.GetBytes("LANREMOTE-VIDEO-V1\0").CopyTo(transcript, 0);
            Convert.FromHexString(SessionId.ToString("N")).CopyTo(transcript, 19);
            Nonce.CopyTo(transcript, 35);
            Pin.CopyTo(transcript, 51);
            using HMACSHA256 hmac = new(Token);
            VideoAttachStatus status = Registry.TryAttachVideo(SessionId, Video, Nonce,
                hmac.ComputeHash(transcript), default, out VideoAttachLease? lease);
            Lease = lease;
            Assert.Equal(VideoAttachStatus.Attached, status);
            Assert.NotNull(Lease);
        }

        internal async Task InstallAndPublishAsync()
        {
            Task installing = Source.ReplaceAsync(Initial);
            _installed = true;
            await installing;
            Publication = Registry.PublishVideoSource(Lease!, Source);
        }

        internal Admission Create() => Registration.CreateDisplaySwitchAdmission(Catalog);

        internal DisplaySwitchRequestFrame Request(long sequence, Guid? sessionId = null,
            long? catalogSequence = null, DisplayId? displayId = null) =>
            new(sessionId ?? SessionId, sequence, 1234, catalogSequence ?? Catalog.Sequence,
                displayId ?? new DisplayId(17));

        internal void Invalidate(string reason)
        {
            switch (reason)
            {
                case "withdraw": Publication!.Dispose(); break;
                case "control-cancel": Control.Cancel(); break;
                case "unregister": Registration.Dispose(); break;
                case "lease-complete": Lease!.Complete(); break;
                default: throw new ArgumentOutOfRangeException(nameof(reason));
            }
        }

        public async ValueTask DisposeAsync()
        {
            Publication?.Dispose();
            Registration.Dispose();
            Initial.ReleaseCleanup();
            try
            {
                await Source.DisposeAsync();
                await Source.Completion;
                Assert.Equal(0, Clock.TimerCount);
            }
            finally
            {
                if (Source.Completion.IsCompleted) Lease?.Complete();
                Control.Dispose();
                if (!_installed)
                {
                    await Initial.DisposeAsync();
                    await Initial.Completion;
                }
            }
        }
    }

    private sealed class Producer : IVideoFrameProducer
    {
        private readonly TaskCompletionSource _lifetime = Signal();
        private TaskCompletionSource? _cleanup;
        private int _startCalls, _readCalls, _stopCalls, _disposeCalls, _completionReads;

        internal int StartCalls => Volatile.Read(ref _startCalls);
        internal int ReadCalls => Volatile.Read(ref _readCalls);
        internal int StopCalls => Volatile.Read(ref _stopCalls);
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        internal int CompletionReads => Volatile.Read(ref _completionReads);
        internal void HoldCleanup() => _cleanup = Signal();
        internal void ReleaseCleanup() => _cleanup?.TrySetResult();

        public void Start() => Interlocked.Increment(ref _startCalls);

        public ValueTask<EncodedFrame?> ReadNextAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readCalls);
            return ValueTask.FromResult<EncodedFrame?>(null);
        }

        public Task StopAsync()
        {
            Interlocked.Increment(ref _stopCalls);
            if (_cleanup is not null) return _cleanup.Task;
            _lifetime.TrySetResult();
            return _lifetime.Task;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            if (_cleanup is not null) return new ValueTask(_cleanup.Task);
            _lifetime.TrySetResult();
            return new ValueTask(_lifetime.Task);
        }

        public Task Completion
        {
            get
            {
                Interlocked.Increment(ref _completionReads);
                return _cleanup?.Task ?? _lifetime.Task;
            }
        }
    }
}
