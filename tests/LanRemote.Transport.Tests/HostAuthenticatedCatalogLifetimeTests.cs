using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;
using Xunit.Sdk;

namespace LanRemote.Transport.Tests;

public sealed class HostAuthenticatedCatalogLifetimeTests
{
    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Catalog_Starts_Only_After_Success_Write_Flush_Registration_And_Secret_Finally(bool approval)
    {
        await using CatalogLifetimeScenario scenario = new(router: false, approval: approval);
        Task run = scenario.Start();
        await scenario.ReleaseApprovalAsync();
        await scenario.Stream.SuccessWrite.Entered.Task.WaitAsync(CatalogLifetimeScenario.Guard);
        Task<ControlAuthResult> authRun = Assert.IsAssignableFrom<Task<ControlAuthResult>>(run);
        byte[] token = Assert.IsType<byte[]>(CatalogLifetimeScenario.ReadLocal(authRun, typeof(ControlAuthSession), "sessionToken"));
        byte[] payload = Assert.IsType<byte[]>(CatalogLifetimeScenario.ReadLocal(authRun, typeof(ControlAuthSession), "successPayload"));
        AuthSuccessFrame success = Assert.IsType<AuthSuccessFrame>(CatalogLifetimeScenario.ReadLocal(authRun, typeof(ControlAuthSession), "success"));
        ReadOnlyMemory<byte> frameToken = success.SessionToken;
        Assert.Contains(token, value => value != 0);
        Assert.Contains(payload, value => value != 0);
        Assert.True(frameToken.Span.IndexOfAnyExcept((byte)0) >= 0);
        Assert.Equal(0, scenario.Context.SessionRegistry.ActiveSessionCount);
        Assert.Equal(0, scenario.SourceCalls);
        Task<int>? originalApprovalRead = scenario.Stream.HoldingOperation;
        Assert.Equal(approval ? 1 : 0, scenario.Stream.HoldingCalls);
        scenario.SourceCheck = () =>
        {
            Assert.True(token.AsSpan().IndexOfAnyExcept((byte)0) < 0);
            Assert.True(payload.AsSpan().IndexOfAnyExcept((byte)0) < 0);
            Assert.True(frameToken.Span.IndexOfAnyExcept((byte)0) < 0);
            Assert.All(scenario.SecretStore.ReturnedKey!, value => Assert.Equal((byte)0, value));
        };

        scenario.Stream.SuccessWrite.Release();
        await scenario.Stream.SuccessFlush.Entered.Task.WaitAsync(CatalogLifetimeScenario.Guard);
        Assert.Equal(0, scenario.Context.SessionRegistry.ActiveSessionCount);
        Assert.Equal(0, scenario.SourceCalls);
        Assert.False(run.IsCompleted);
        scenario.Stream.SuccessFlush.Release();
        await scenario.SourceEntered.Task.WaitAsync(CatalogLifetimeScenario.Guard);
        Assert.Equal(1, scenario.Context.SessionRegistry.ActiveSessionCount);
        Assert.Equal(1, scenario.Stream.HoldingCalls);
        if (approval) Assert.Same(originalApprovalRead, scenario.Stream.HoldingOperation);
        Assert.Equal(0, scenario.Context.PendingApprovalLimiter.GlobalInUse);
        Assert.False(scenario.Stream.HoldingOperation!.IsCompleted);
        Assert.False(run.IsCompleted);

        scenario.ReleaseCatalog();
        await scenario.Stream.CatalogWrite.Entered.Task.WaitAsync(CatalogLifetimeScenario.Guard);
        scenario.Stream.CatalogWrite.Release();
        await scenario.Stream.CatalogFlush.Entered.Task.WaitAsync(CatalogLifetimeScenario.Guard);
        scenario.Stream.CatalogFlush.Release();
        await scenario.WaitForSentAsync();
        Assert.False(run.IsCompleted);
        Assert.False(scenario.CloseRequired.Task.IsCompleted);
        scenario.Stream.ReleaseHolding(0);
        Assert.True((await authRun.WaitAsync(CatalogLifetimeScenario.Guard)).Completed);
        Assert.True(scenario.CloseRequired.Task.IsCompletedSuccessfully);
        Assert.Equal(0, scenario.Context.SessionRegistry.ActiveSessionCount);
        Assert.Equal(1, scenario.SourceCalls);
        Assert.Equal(1, scenario.Stream.HoldingCalls);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_Success_Write_Or_Flush_Never_Starts_Catalog(bool failFlush)
    {
        IOException failure = new("受控 auth_success 写出失败。");
        await using CatalogLifetimeScenario scenario = new(router: false, approval: false);
        scenario.ExpectedErrors.Add(failure);
        Task run = scenario.Start();
        await scenario.Stream.SuccessWrite.Entered.Task.WaitAsync(CatalogLifetimeScenario.Guard);
        if (failFlush)
        {
            scenario.Stream.SuccessWrite.Release();
            await scenario.Stream.SuccessFlush.Entered.Task.WaitAsync(CatalogLifetimeScenario.Guard);
            scenario.Stream.SuccessFlush.Fail(failure);
        }
        else scenario.Stream.SuccessWrite.Fail(failure);
        ControlAuthResult result = await ((Task<ControlAuthResult>)run).WaitAsync(CatalogLifetimeScenario.Guard);
        Assert.False(result.Completed);
        Assert.Equal(ControlAuthSession.RejectSuccessNotDelivered, result.Rejection);
        Assert.Equal(0, scenario.SourceCalls);
        Assert.Equal(0, scenario.Stream.HoldingCalls);
        Assert.Equal(0, scenario.Context.SessionRegistry.ActiveSessionCount);
        Assert.Equal(new[] { "auth_challenge", "display_catalog_offer", "auth_success", "authentication_failed" }, scenario.Stream.Writes);
    }
}

// 两个测试文件共享夹具；首帧、Challenge2、Offer/Accept 和 proof 均走真实认证，不创建网络。
internal sealed class CatalogLifetimeScenario : IAsyncDisposable
{
    internal static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(100);
    private static readonly TransportTimeouts Timeouts = new(TransportTimeouts.Maximum, TransportTimeouts.Maximum,
        TransportTimeouts.Maximum, TransportTimeouts.Maximum, TransportTimeouts.Maximum, TransportTimeouts.Maximum);
    internal static readonly Guid ServerId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    internal static readonly Guid ClientId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    internal static readonly byte[] Key = Convert.FromHexString("0F1E2D3C4B5A69788796A5B4C3D2E1F0");
    private readonly bool _router;
    private readonly bool _approval;
    private readonly TaskCompletionSource<DisplayCatalogFrame> _source = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<Task> _cancellations = new();
    private Task? _directCloseSupervisor;
    private VideoAttachLease? _videoLease;
    private int _sourceCalls;

    internal CatalogLifetimeScenario(bool router = true, bool approval = false, bool holdClose = false,
        Exception? catalogTimerDisposeFailure = null)
    {
        _router = router;
        _approval = approval;
        if (catalogTimerDisposeFailure is not null)
            CleanupClock = new CatalogCleanupFailureClock(Clock, catalogTimerDisposeFailure);
        Stream = new CatalogControlledSslStream(router, holdClose);
        Closer = new ConnectionCloseHandle(new MemoryStream(), new HostLifecycleErrors());
        Closer.Attach(Stream);
        Connection = new AcceptedConnection(new ConnectionSecurityContext(IPAddress.Loopback, IPAddress.Loopback,
            12345, SslProtocols.None, new byte[CertificatePin.LengthBytes]), Stream, Closer);
        Context = new ControlAuthContext
        {
            ServerDeviceId = ServerId,
            AccessSecretStore = SecretStore,
            FailedAuthLimiter = new FailedAuthLimiter(),
            PendingApprovalLimiter = new ConnectionAdmissionLimiter(3, 1),
            ApprovalGate = Approval,
            SessionRegistry = new SessionRegistry(),
            TimeProvider = (TimeProvider?)CleanupClock ?? Clock,
            Options = new ControlAuthOptions { RequireLocalApproval = approval, FailureDelayMin = TimeSpan.Zero, FailureDelayMax = TimeSpan.Zero },
            DisplayCatalogSend = new HostDisplayCatalogSendOptions(Source, Budget),
        };
        Router = new FirstFrameRouter(Context, Timeouts, new NoVideoSource());
        if (!router)
        {
            Auth = new ControlAuthSession(Stream, Connection.Security, Context, Timeouts);
            Auth.BindCatalogLifetime(Context.DisplayCatalogSend!, CloseRequired);
        }
    }

    internal CancellationTokenSource HostStop { get; } = new();
    internal ManualDeadlineClock Clock { get; } = new();
    internal CatalogCleanupFailureClock? CleanupClock { get; }
    internal CatalogSecretStore SecretStore { get; } = new();
    internal CatalogApproval Approval { get; } = new();
    internal CatalogControlledSslStream Stream { get; }
    internal ConnectionCloseHandle Closer { get; }
    internal AcceptedConnection Connection { get; }
    internal ControlAuthContext Context { get; }
    internal FirstFrameRouter Router { get; }
    internal ControlAuthSession? Auth { get; }
    internal TaskCompletionSource CloseRequired { get; } = Signal();
    internal TaskCompletionSource SourceEntered { get; } = Signal();
    internal Task<DisplayCatalogFrame>? SourceOperation { get; private set; }
    internal Task? Revoked { get; private set; }
    internal Task? Run { get; private set; }
    internal List<Exception> ExpectedErrors { get; } = [];
    internal Action? SourceCheck { get; set; }
    internal int SourceCalls => Volatile.Read(ref _sourceCalls);
    internal DisplayCatalogFrame Catalog { get; } = new(7, 12,
        [new DisplayInfo(new DisplayId(0), "受控显示器", 0, 0, 1920, 1080, true)]);

    internal Task Start()
    {
        Assert.Null(Run);
        if (!_router)
            _directCloseSupervisor = Task.Run(async () =>
            {
                await CloseRequired.Task.ConfigureAwait(false);
                await Connection.CloseAsync().ConfigureAwait(false);
            });
        return Run = _router ? Router.RunAsync(Connection, HostStop.Token) : Auth!.RunAsync(HostStop.Token);
    }

    private ValueTask<DisplayCatalogFrame> Source()
    {
        Assert.Equal(1, Interlocked.Increment(ref _sourceCalls));
        Assert.True(Stream.SuccessWrite.Operation!.IsCompletedSuccessfully);
        Assert.True(Stream.SuccessFlush.Operation!.IsCompletedSuccessfully);
        Assert.Equal(1, Context.SessionRegistry.ActiveSessionCount);
        Assert.Equal(1, Stream.HoldingCalls);
        Assert.All(SecretStore.ReturnedKey!, value => Assert.Equal((byte)0, value));
        SourceCheck?.Invoke();
        Revoked = ReadRevocation();
        SourceOperation = _source.Task;
        SourceEntered.TrySetResult();
        return new ValueTask<DisplayCatalogFrame>(SourceOperation);
    }

    internal async Task ReleaseApprovalAsync()
    {
        if (!_approval) return;
        await Approval.Entered.Task.WaitAsync(Guard);
        Assert.Equal(1, Stream.HoldingCalls);
        Assert.Equal(1, Context.PendingApprovalLimiter.GlobalInUse);
        Approval.Release();
    }

    internal async Task AuthenticateAsync()
    {
        await ReleaseApprovalAsync();
        await Stream.SuccessWrite.Entered.Task.WaitAsync(Guard);
        Stream.SuccessWrite.Release();
        await Stream.SuccessFlush.Entered.Task.WaitAsync(Guard);
        Stream.SuccessFlush.Release();
        await SourceEntered.Task.WaitAsync(Guard);
    }

    internal VideoAttachLease AttachVideo()
    {
        Assert.Null(_videoLease);
        Guid sessionId = Assert.Single(Context.SessionRegistry.Snapshot()).SessionId;
        ConnectionSecurityContext videoSecurity = new(IPAddress.Loopback, IPAddress.Loopback, 12346,
            SslProtocols.None, Connection.Security.ServerCertificateSha256.Span);
        byte[] nonce = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        AuthSuccessFrame success = Assert.IsType<AuthSuccessFrame>(Stream.WireSuccess);
        // 只用真实成功帧的测试自有副本构造视频 proof；不伪造登记或绕过 TryAttachVideo 校验。
        byte[] transcript = [.. System.Text.Encoding.UTF8.GetBytes("LANREMOTE-VIDEO-V1\0"),
            .. Convert.FromHexString(sessionId.ToString("N")), .. nonce,
            .. videoSecurity.ServerCertificateSha256.ToArray()];
        byte[] proof = HMACSHA256.HashData(success.SessionToken.Span, transcript);
        try
        {
            Assert.Equal(VideoAttachStatus.Attached, Context.SessionRegistry.TryAttachVideo(
                sessionId, videoSecurity, nonce, proof, CancellationToken.None, out _videoLease));
            return Assert.IsType<VideoAttachLease>(_videoLease);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(proof);
            success.ClearSessionToken();
        }
    }

    internal void ReleaseCatalog() => _source.TrySetResult(Catalog);
    internal void FailSource(params Exception[] failures) => _source.TrySetException(failures);
    internal Task CancelHostAsync()
    {
        Task cancellation = HostStop.CancelAsync();
        _cancellations.Enqueue(cancellation);
        return cancellation;
    }

    internal HostAuthenticatedCatalogLifetime Lifetime
    {
        get
        {
            ControlAuthSession auth = Auth ?? Assert.IsType<ControlAuthSession>(Router.CatalogAuthSession);
            return ReadField<HostAuthenticatedCatalogLifetime>(auth);
        }
    }

    internal Task OriginalHolding => Assert.IsAssignableFrom<Task>(typeof(ControlAuthSession)
        .GetField("_catalogHolding", BindingFlags.Instance | BindingFlags.NonPublic)!
        .GetValue(Auth ?? Router.CatalogAuthSession));

    internal async Task<HostDisplayCatalogSendAttempt> WaitForSentAsync()
    {
        HostAuthenticatedCatalogLifetime lifetime = Lifetime;
        HostDisplayCatalogSendAttempt attempt = ReadField<HostDisplayCatalogSendAttempt>(lifetime);
        Task<HostDisplayCatalogSendAttempt.SendOutcome> completion = Assert.IsAssignableFrom<Task<HostDisplayCatalogSendAttempt.SendOutcome>>(attempt.Completion);
        Assert.Equal(HostDisplayCatalogSendAttempt.AttemptEnd.Sent, (await completion.WaitAsync(Guard)).End);
        Assert.False(lifetime.Completion!.IsCompleted);
        return attempt;
    }

    internal static T ReadField<T>(object instance) where T : class => Assert.IsType<T>(ReadOptionalField<T>(instance));

    private static T? ReadOptionalField<T>(object instance) where T : class => (T?)
        Assert.Single(instance.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
            field => field.FieldType == typeof(T)).GetValue(instance);

    private Task ReadRevocation()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object gate = typeof(SessionRegistry).GetField("_gate", flags)!.GetValue(Context.SessionRegistry)!;
        lock (gate)
        {
            IDictionary entries = Assert.IsAssignableFrom<IDictionary>(typeof(SessionRegistry).GetField("_sessions", flags)!.GetValue(Context.SessionRegistry));
            object entry = Assert.Single(entries.Values.Cast<object>());
            return Assert.IsType<TaskCompletionSource>(entry.GetType().GetProperty("Revoked")!.GetValue(entry)).Task;
        }
    }

    internal static object? ReadLocal(Task run, Type owner, string name)
    {
        Assert.False(run.IsCompleted);
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        FieldInfo? stateField = null;
        for (Type? type = run.GetType(); type is not null; type = type.BaseType)
        {
            stateField = type.GetField("StateMachine", fields);
            if (stateField is not null) break;
        }
        Assert.NotNull(stateField);
        object state = stateField.GetValue(run)!;
        Type? expected = owner.GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
        Assert.Equal(expected, state.GetType());
        return Assert.Single(state.GetType().GetFields(fields), field => field.Name.StartsWith($"<{name}>5__", StringComparison.Ordinal)).GetValue(state);
    }

    internal async Task AssertClosedWhileJoiningAsync(Task run)
    {
        await AssertSignalAsync(Stream.DisposeExited.Task);
        await AssertSignalAsync(Connection.CloseAsync());
        await AssertSignalAsync(Revoked!);
        Assert.Equal(0, Context.SessionRegistry.ActiveSessionCount);
        Assert.False(Stream.HoldingOperation!.IsCompleted);
        Assert.False(run.IsCompleted);
        Assert.True(Stream.DisposeSucceeded);
    }

    internal void AssertJoined()
    {
        Assert.True(Run!.IsCompleted);
        Assert.True(Connection.CloseAsync().IsCompletedSuccessfully);
        Assert.True(Stream.HoldingOperation!.IsCompleted);
        Assert.Equal(1, Stream.HoldingCalls);
        Assert.Equal(1, SourceCalls);
        Assert.Equal(1, Stream.DisposeCalls);
        Assert.Equal(0, Context.SessionRegistry.ActiveSessionCount);
        Assert.Equal(0, Context.PendingApprovalLimiter.GlobalInUse);
        Assert.Equal(0, Context.FailedAuthLimiter.CountRecentFailures(IPAddress.Loopback));
        Assert.Equal(0, Clock.TimerCount);
        Assert.Empty(Closer.CleanupErrors);
    }

    internal static async Task<Exception?> CaptureAsync(Task task)
    {
        try { await task.WaitAsync(Guard); return null; }
        catch (Exception error) when (task.IsCompleted && error is not TimeoutException) { return error; }
    }

    internal static async Task AssertSignalAsync(Task signal)
    {
        // 有界看门狗只裁决预期通知缺失；不靠经过时间推断任何原操作已经退出。
        using CancellationTokenSource watchdogStop = new();
        Task watchdog = Task.Delay(Guard, watchdogStop.Token);
        try
        {
            Assert.Same(signal, await Task.WhenAny(signal, watchdog));
            await signal;
        }
        finally
        {
            await watchdogStop.CancelAsync();
            try { await watchdog; }
            catch (OperationCanceledException) when (watchdog.IsCanceled) { }
        }
    }

    private const BindingFlags AwaitFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // 沿用发送底座测试的精确运行时证据：只认可 awaiter、Task.Run unwrap 与已注册 WhenAll 输入。
    internal static async Task AssertAwaitPathAsync(Task original, Task completion)
    {
        using CancellationTokenSource guard = new(Guard);
        while (!HasAwaitPath(original, completion, new HashSet<Task>()))
        {
            Assert.False(original.IsCompleted, "Assert.False：原任务提前退出，失去受闸等待证据。");
            Assert.False(completion.IsCompleted, "Assert.False：目录生命周期提前退出，未等待指定原任务。");
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
            bool exact = machine is not null && machine.GetType().GetFields(AwaitFields)
                .Where(field => field.Name.StartsWith("<>u__", StringComparison.Ordinal))
                .Select(field => field.GetValue(machine)).OfType<object>()
                .Any(awaiter => awaiter.GetType().GetFields(AwaitFields)
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
            if (current.GetField(name, AwaitFields | BindingFlags.DeclaredOnly) is { } field) return field;
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        // 先打开全部闸；每次加入真实已发布操作，避免断言失败遗留不合作原根。
        _videoLease?.Complete();
        Stream.ReleaseAll();
        Approval.Release();
        ReleaseCatalog();
        CloseRequired.TrySetResult();
        List<Exception> unexpected = [];
        try
        {
            await JoinAsync(CancelHostAsync());
            await JoinAsync(Connection.CloseAsync());
            await JoinAsync(Run);
            // 跳过生产 join 的变异下，Router 完成不代表这些已发布包装根退出。
            ControlAuthSession? activeAuth = Auth ?? Router.CatalogAuthSession;
            HostAuthenticatedCatalogLifetime? owner = activeAuth is null ? null : ReadOptionalField<HostAuthenticatedCatalogLifetime>(activeAuth);
            HostDisplayCatalogSendAttempt? attempt = owner is null ? null : ReadOptionalField<HostDisplayCatalogSendAttempt>(owner);
            await JoinAsync(owner?.Completion);
            await JoinAsync(attempt?.Completion);
            if (activeAuth is not null) await JoinAsync(activeAuth.JoinOwnedOperationsAsync());
            await JoinAsync(_directCloseSupervisor);
            await JoinAsync(SourceOperation);
            await JoinAsync(Approval.Operation);
            await JoinAsync(Stream.HoldingOperation);
            foreach (Task task in Stream.Operations.Concat(_cancellations)) await JoinAsync(task);
            await JoinAsync(_videoLease?.Completed);
            await JoinAsync(CleanupClock?.DisposeOperation);
            unexpected.AddRange(Closer.CleanupErrors);
        }
        finally
        {
            Stream.WireSuccess?.ClearSessionToken();
            HostStop.Dispose();
        }
        if (unexpected.Count == 1) ExceptionDispatchInfo.Capture(unexpected[0]).Throw();
        if (unexpected.Count > 1) throw new AggregateException("目录生命周期测试清理发现非预期故障。", unexpected);

        async Task JoinAsync(Task? task)
        {
            if (task is null) return;
            try { await task.WaitAsync(Guard); }
            catch (Exception error)
            {
                if (!task.IsCompleted || error is TimeoutException) { unexpected.Add(error); return; }
                if (task.Exception is { } roots)
                {
                    foreach (Exception root in roots.InnerExceptions) Check(root);
                }
                else if (error is not OperationCanceledException cancelled || !task.IsCanceled ||
                    !HostStop.IsCancellationRequested || cancelled.CancellationToken != HostStop.Token)
                    Check(error);
            }
        }
        void Check(Exception error)
        {
            if (ExpectedErrors.Any(expected => ReferenceEquals(expected, error))) return;
            if (error is AggregateException aggregate)
            {
                foreach (Exception child in aggregate.InnerExceptions) Check(child);
                return;
            }
            if (error.GetType() == typeof(Exception) && error.InnerException is { } original &&
                error.Message is "host-catalog-operation-Source" or "host-catalog-operation-Write" or
                    "host-catalog-operation-Flush" or "host-catalog-budget" or "host-catalog-budget-cleanup")
            {
                Check(original);
                return;
            }
            unexpected.Add(error);
        }
    }

    internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class NoVideoSource : IVideoFrameSource
    {
        public ValueTask<EncodedFrame?> ReadNextAsync(Guid sessionId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("控制目录用例不得启动视频来源。");
    }
}

// Entered 只证明夹具已发布将返回的原 Task，不宣称生产调用栈此刻已经取得该句柄。
internal sealed class CatalogOperationGate
{
    private readonly TaskCompletionSource _release = CatalogLifetimeScenario.Signal();
    internal TaskCompletionSource Entered { get; } = CatalogLifetimeScenario.Signal();
    internal Task? Operation { get; private set; }
    internal Task Enter()
    {
        Assert.Null(Operation);
        Operation = _release.Task;
        Entered.TrySetResult();
        return Operation;
    }
    internal void Release() => _release.TrySetResult();
    internal void Fail(params Exception[] errors) => _release.TrySetException(errors);
}

internal sealed class CatalogApproval : ILocalApprovalGate
{
    private readonly TaskCompletionSource<LocalApprovalDecision> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private LocalApprovalRequest? _request;
    private bool _released;
    internal TaskCompletionSource Entered { get; } = CatalogLifetimeScenario.Signal();
    internal Task<LocalApprovalDecision>? Operation { get; private set; }
    public ValueTask<LocalApprovalDecision> RequestApprovalAsync(LocalApprovalRequest request, CancellationToken cancellationToken)
    {
        _request = request;
        Operation = _decision.Task;
        if (_released) Release();
        Entered.TrySetResult();
        return new ValueTask<LocalApprovalDecision>(Operation);
    }
    internal void Release()
    {
        _released = true;
        if (_request is { } request)
            _decision.TrySetResult(new LocalApprovalDecision(request.RequestId, LocalApprovalOutcome.Approved, request.RequestedPermission));
    }
}

internal sealed class CatalogSecretStore : IAccessSecretStore
{
    internal byte[]? ReturnedKey { get; private set; }
    public Task<AccessSecret> LoadOrCreateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AccessSecret(ReturnedKey = CatalogLifetimeScenario.Key.ToArray()));
    public Task<AccessSecret> RegenerateAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("测试不轮换密钥。");
}

// 仅包装目录预算所有者创建的 timer；认证与路由 deadline 仍使用原手动时钟和原 timer。
internal sealed class CatalogCleanupFailureClock(ManualDeadlineClock clock, Exception failure) : TimeProvider
{
    private int _catalogTimers;
    private int _disposeCalls;
    internal int CatalogTimers => Volatile.Read(ref _catalogTimers);
    internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
    internal Task? DisposeOperation { get; private set; }
    public override long TimestampFrequency => clock.TimestampFrequency;
    public override long GetTimestamp() => clock.GetTimestamp();
    public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ITimer timer = clock.CreateTimer(callback, state, dueTime, period);
        if (state is not ControlFrameBudget) return timer;
        Interlocked.Increment(ref _catalogTimers);
        return new FailingCleanupTimer(this, timer, failure);
    }

    private sealed class FailingCleanupTimer(CatalogCleanupFailureClock owner, ITimer timer, Exception error) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(dueTime, period);
        public void Dispose()
        {
            Interlocked.Increment(ref owner._disposeCalls);
            timer.Dispose();
            throw error;
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref owner._disposeCalls);
            Task operation = CompleteDisposeAsync();
            owner.DisposeOperation = operation;
            return new ValueTask(operation);
        }
        private async Task CompleteDisposeAsync()
        {
            await timer.DisposeAsync().ConfigureAwait(false);
            throw error;
        }
    }
}

internal sealed class CatalogControlledSslStream : SslStream
{
    private byte[] _input;
    private int _offset;
    private string? _lastType;
    private AuthChallengeV2? _challenge;
    private readonly TaskCompletionSource<int> _holding = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _dispose = CatalogLifetimeScenario.Signal();
    private int _disposeCalls;
    private int _disposeSucceeded;
    internal CatalogControlledSslStream(bool hello, bool holdClose) : base(new MemoryStream())
    {
        _input = hello ? Wire(ControlHelloV2.Serialize()) : [];
        if (!holdClose) ReleaseDispose();
    }
    internal CatalogOperationGate SuccessWrite { get; } = new();
    internal CatalogOperationGate SuccessFlush { get; } = new();
    internal CatalogOperationGate CatalogWrite { get; } = new();
    internal CatalogOperationGate CatalogFlush { get; } = new();
    internal ConcurrentQueue<Task> Operations { get; } = new();
    internal ConcurrentQueue<string> Writes { get; } = new();
    internal TaskCompletionSource DisposeEntered { get; } = CatalogLifetimeScenario.Signal();
    internal TaskCompletionSource DisposeExited { get; } = CatalogLifetimeScenario.Signal();
    internal Task<int>? HoldingOperation { get; private set; }
    internal int HoldingCalls { get; private set; }
    internal int FramedReadCalls { get; private set; }
    internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
    internal bool DisposeSucceeded => Volatile.Read(ref _disposeSucceeded) != 0;
    internal byte[]? CatalogWire { get; private set; }
    internal AuthSuccessFrame? WireSuccess { get; private set; }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_offset < _input.Length)
        {
            int count = Math.Min(buffer.Length, _input.Length - _offset);
            _input.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            FramedReadCalls++;
            return ValueTask.FromResult(count);
        }
        Assert.Equal(1, buffer.Length);
        Assert.Equal(1, ++HoldingCalls);
        buffer.Span[0] = 0x5a;
        HoldingOperation = _holding.Task;
        return new ValueTask<int>(HoldingOperation);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadOnlyMemory<byte> payload = buffer[TransportConstants.LengthPrefixBytes..];
        Assert.Equal((uint)payload.Length, BinaryPrimitives.ReadUInt32BigEndian(buffer.Span));
        using JsonDocument document = JsonDocument.Parse(payload);
        _lastType = document.RootElement.GetProperty("type").GetString()!;
        Writes.Enqueue(_lastType);
        if (_lastType == "auth_challenge")
        {
            Assert.Equal(_input.Length, _offset);
            Assert.Equal(WireV2ParseStatus.AcceptedV2, AuthChallengeV2.Parse(payload.Span, out _challenge));
        }
        else if (_lastType == DisplayCatalogAgreementFrame.OfferType)
        {
            AuthChallengeV2 challenge = Assert.IsType<AuthChallengeV2>(_challenge);
            Assert.True(DisplayCatalogAgreementFrame.TryParseOffer(payload.Span, challenge.SessionId, out var offer, out _));
            byte[] accepted = DisplayCatalogAgreementFrame.SerializeAccept(challenge.SessionId);
            Assert.True(DisplayCatalogAgreementFrame.TryParseAccept(accepted, challenge.SessionId, out var accept, out _));
            DisplayCatalogAgreementBinding agreement = DisplayCatalogAgreementBinding.Create(challenge.SessionId, offer!, accept!);
            byte[] nonce = new byte[AuthProtocol.NonceByteLength];
            AuthTranscriptV2 transcript = AuthTranscriptV2.CreateClient(challenge.SessionId, challenge.ServerDeviceId,
                CatalogLifetimeScenario.ClientId, challenge.CopyServerNonce(), nonce, challenge.CopyCertificateSha256(),
                SessionPermission.ViewOnly, agreement);
            byte[] proof = transcript.ComputeClientProof(CatalogLifetimeScenario.Key);
            try
            {
                _input = [.. Wire(accepted), .. Wire(new AuthResponseFrame(CatalogLifetimeScenario.ClientId,
                    "受控目录客户端", nonce, SessionPermission.ViewOnly, proof).Serialize())];
                _offset = 0;
            }
            finally { transcript.Clear(); CryptographicOperations.ZeroMemory(proof); }
        }
        else if (_lastType == "auth_success")
        {
            Assert.True(AuthSuccessFrame.TryParse(payload.Span, out AuthSuccessFrame? success, out _));
            WireSuccess = success;
            return Operation(SuccessWrite);
        }
        else if (_lastType == "host_status")
        {
            CatalogWire = buffer.ToArray();
            return Operation(CatalogWrite);
        }
        return ValueTask.CompletedTask;
    }

    private ValueTask Operation(CatalogOperationGate gate)
    {
        Task task = gate.Enter();
        Operations.Enqueue(task);
        return new ValueTask(task);
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CatalogOperationGate? gate = _lastType switch { "auth_success" => SuccessFlush, "host_status" => CatalogFlush, _ => null };
        if (gate is null) return Task.CompletedTask;
        Task task = gate.Enter();
        Operations.Enqueue(task);
        return task;
    }

    internal void ReleaseHolding(int bytes) => _holding.TrySetResult(bytes);
    internal void ReleaseDispose() => _dispose.TrySetResult();
    internal void ReleaseAll()
    {
        SuccessWrite.Release();
        SuccessFlush.Release();
        CatalogWrite.Release();
        CatalogFlush.Release();
        ReleaseHolding(0);
        ReleaseDispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing) { base.Dispose(disposing); return; }
        Interlocked.Increment(ref _disposeCalls);
        DisposeEntered.TrySetResult();
        try
        {
            _dispose.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            base.Dispose(disposing);
            Volatile.Write(ref _disposeSucceeded, 1);
        }
        finally { DisposeExited.TrySetResult(); }
    }

    private static byte[] Wire(byte[] payload)
    {
        byte[] wire = new byte[TransportConstants.LengthPrefixBytes + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(wire, (uint)payload.Length);
        payload.CopyTo(wire, TransportConstants.LengthPrefixBytes);
        return wire;
    }
}
