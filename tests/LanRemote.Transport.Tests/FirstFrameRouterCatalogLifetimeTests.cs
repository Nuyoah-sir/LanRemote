using System.Buffers.Binary;
using Xunit;

namespace LanRemote.Transport.Tests;

public sealed class FirstFrameRouterCatalogLifetimeTests
{
    [Theory(Timeout = 60_000)]
    [InlineData("Source", false)]
    [InlineData("Write", false)]
    [InlineData("Flush", false)]
    [InlineData("Source", true)]
    [InlineData("Write", true)]
    [InlineData("Flush", true)]
    public async Task Stop_Closes_And_Unregisters_Before_Joining_Uncooperative_Catalog_And_Holding(
        string stage, bool budgetExpires)
    {
        await using CatalogLifetimeScenario scenario = new();
        Task run = scenario.Start();
        await scenario.AuthenticateAsync();
        Task original = await EnterStageAsync(scenario, stage);
        HostAuthenticatedCatalogLifetime lifetime = scenario.Lifetime;
        Task originalHolding = scenario.OriginalHolding;
        Assert.False(original.IsCompleted);
        Assert.False(scenario.Stream.HoldingOperation!.IsCompleted);
        Assert.Equal(1, scenario.Context.SessionRegistry.ActiveSessionCount);

        if (budgetExpires) scenario.Clock.Advance(CatalogLifetimeScenario.Budget);
        else await scenario.CancelHostAsync().WaitAsync(CatalogLifetimeScenario.Guard);
        await CatalogLifetimeScenario.AssertSignalAsync(lifetime.StopRequired);
        Exception? budgetFailure = null;
        if (budgetExpires)
        {
            budgetFailure = Assert.IsType<TimeoutException>(lifetime.Snapshot.Attempt.Budget!.Error);
            scenario.ExpectedErrors.Add(budgetFailure);
        }
        await scenario.AssertClosedWhileJoiningAsync(run);
        Assert.True(lifetime.StopRequired.IsCompletedSuccessfully);
        Assert.False(lifetime.Completion!.IsCompleted);
        Assert.False(original.IsCompleted);
        Assert.False(lifetime.Snapshot.OriginalOperationsFinished);

        // 原 holding 先退出仍不能遗弃不合作 source/write/flush。
        scenario.Stream.ReleaseHolding(0);
        await scenario.Stream.HoldingOperation.WaitAsync(CatalogLifetimeScenario.Guard);
        await originalHolding.WaitAsync(CatalogLifetimeScenario.Guard);
        await CatalogLifetimeScenario.AssertAwaitPathAsync(original, lifetime.Completion!);
        Assert.False(lifetime.Completion!.IsCompleted);
        Assert.False(original.IsCompleted);
        Assert.False(run.IsCompleted);
        ReleaseStage(scenario, stage);
        await original.WaitAsync(CatalogLifetimeScenario.Guard);
        Exception? actual = await CatalogLifetimeScenario.CaptureAsync(run);
        if (budgetExpires) AssertDiagnostic(run, actual, "host-catalog-budget", budgetFailure!);
        else Assert.Null(actual);
        Assert.True(lifetime.Snapshot.OriginalOperationsFinished);
        Assert.True(lifetime.Snapshot.AllOwnedRootsReleased);
        Assert.Equal(HostDisplayCatalogSendAttempt.AttemptEnd.Rejected, lifetime.Snapshot.Attempt.End);
        Assert.Equal(stage == "Source" ? 0 : 1, scenario.Stream.Writes.Count(type => type == "host_status"));
        Assert.Equal(stage == "Flush", scenario.Stream.CatalogFlush.Operation is not null);
        scenario.AssertJoined();
    }

    [Theory(Timeout = 60_000)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Sent_Catalog_Keeps_Original_Holding_Until_Eof_Or_Reverse_Data(int bytes)
    {
        await using CatalogLifetimeScenario scenario = new(approval: true);
        Task run = scenario.Start();
        await scenario.AuthenticateAsync();
        Task<int> originalRead = scenario.Stream.HoldingOperation!;
        await SendCatalogAsync(scenario);
        HostAuthenticatedCatalogLifetime lifetime = scenario.Lifetime;
        await scenario.WaitForSentAsync();
        Assert.False(run.IsCompleted);
        Assert.False(originalRead.IsCompleted);
        Assert.False(lifetime.StopRequired.IsCompleted);
        Assert.False(scenario.Stream.DisposeEntered.Task.IsCompleted);
        Assert.Equal(1, scenario.Context.SessionRegistry.ActiveSessionCount);
        Assert.Equal(6, scenario.Stream.FramedReadCalls);
        Assert.Equal(1, scenario.Stream.HoldingCalls);
        Assert.Equal(new[] { "auth_challenge", "display_catalog_offer", "approval_pending", "auth_success", "host_status" }, scenario.Stream.Writes);
        byte[] payload = scenario.Catalog.Serialize();
        byte[] wire = Assert.IsType<byte[]>(scenario.Stream.CatalogWire);
        Assert.Equal((uint)payload.Length, BinaryPrimitives.ReadUInt32BigEndian(wire));
        Assert.Equal(payload, wire[4..]);

        scenario.Stream.ReleaseHolding(bytes);
        await run.WaitAsync(CatalogLifetimeScenario.Guard);
        Assert.Same(originalRead, scenario.Stream.HoldingOperation);
        Assert.Equal(bytes, await originalRead);
        Assert.True(lifetime.StopRequired.IsCompletedSuccessfully);
        Assert.Equal(HostDisplayCatalogSendAttempt.AttemptEnd.Sent, lifetime.Snapshot.Attempt.End);
        Assert.True(lifetime.Snapshot.AllOwnedRootsReleased);
        scenario.AssertJoined();
    }

    [Fact(Timeout = 60_000)]
    public async Task Host_Stop_After_Sent_Still_Closes_And_Joins_Original_Holding()
    {
        await using CatalogLifetimeScenario scenario = new();
        Task run = scenario.Start();
        await scenario.AuthenticateAsync();
        await SendCatalogAsync(scenario);
        HostDisplayCatalogSendAttempt attempt = await scenario.WaitForSentAsync();
        HostAuthenticatedCatalogLifetime lifetime = scenario.Lifetime;
        Assert.False(attempt.AbortRequired.IsCompleted);
        await scenario.CancelHostAsync().WaitAsync(CatalogLifetimeScenario.Guard);
        await scenario.AssertClosedWhileJoiningAsync(run);
        Assert.True(lifetime.StopRequired.IsCompletedSuccessfully);
        Assert.False(attempt.AbortRequired.IsCompleted);
        Assert.Equal(HostDisplayCatalogSendAttempt.AttemptEnd.Sent, attempt.Snapshot.End);
        Assert.True(attempt.Snapshot.StopRequested);
        Assert.False(lifetime.Completion!.IsCompleted);

        scenario.Stream.ReleaseHolding(0);
        await run.WaitAsync(CatalogLifetimeScenario.Guard);
        Assert.True(lifetime.Snapshot.AllOwnedRootsReleased);
        Assert.Equal(HostDisplayCatalogSendAttempt.AttemptEnd.Sent, attempt.Snapshot.End);
        scenario.AssertJoined();
    }

    [Theory(Timeout = 60_000)]
    [InlineData("io")]
    [InlineData("disposed")]
    [InlineData("cancelled")]
    [InlineData("host-token-cancelled")]
    public async Task Source_Failure_Keeps_Original_Instance_And_Label_Outside_Router_Legacy_Catches(string kind)
    {
        await using CatalogLifetimeScenario scenario = new(holdClose: true);
        Exception expected = kind switch
        {
            "io" => new IOException("目录来源 IO 故障，不是网络断连。"),
            "disposed" => new ObjectDisposedException("目录来源"),
            "host-token-cancelled" => new OperationCanceledException("目录来源使用同一个 Host 令牌报告故障。", scenario.HostStop.Token),
            _ => new OperationCanceledException("目录来源未请求取消却报告 OCE。", CancellationToken.None),
        };
        scenario.ExpectedErrors.Add(expected);
        Task run = scenario.Start();
        await scenario.AuthenticateAsync();
        HostAuthenticatedCatalogLifetime lifetime = scenario.Lifetime;
        if (kind == "host-token-cancelled")
            await scenario.CancelHostAsync().WaitAsync(CatalogLifetimeScenario.Guard);
        scenario.FailSource(expected);
        await scenario.Stream.DisposeEntered.Task.WaitAsync(CatalogLifetimeScenario.Guard);
        Assert.False(run.IsCompleted);
        Assert.False(scenario.Connection.CloseAsync().IsCompleted);
        Assert.Same(expected, await CatalogLifetimeScenario.CaptureAsync(scenario.SourceOperation!));
        Assert.True(scenario.SourceOperation!.IsFaulted);
        scenario.Stream.ReleaseHolding(0);
        await lifetime.Completion!.WaitAsync(CatalogLifetimeScenario.Guard);
        await scenario.Revoked!.WaitAsync(CatalogLifetimeScenario.Guard);
        Assert.Equal(0, scenario.Context.SessionRegistry.ActiveSessionCount);
        Assert.False(run.IsCompleted);
        Assert.False(scenario.Connection.CloseAsync().IsCompleted);
        Assert.False(scenario.Stream.DisposeExited.Task.IsCompleted);
        scenario.Stream.ReleaseDispose();

        Exception? actual = await CatalogLifetimeScenario.CaptureAsync(run);
        AssertDiagnostic(run, actual, "host-catalog-operation-Source", expected);
        Assert.Null(scenario.Router.Rejection);
        Assert.Equal(HostDisplayCatalogSendAttempt.OperationStage.Source, lifetime.Snapshot.Attempt.ErrorStage);
        Assert.Same(expected, lifetime.Snapshot.Attempt.OperationError);
        scenario.AssertJoined();
    }

    [Theory(Timeout = 60_000)]
    [InlineData("Source")]
    [InlineData("Write")]
    [InlineData("Flush")]
    public async Task Original_Multiple_Roots_Keep_Nesting_Duplicates_And_Stage_After_Real_Close(string stage)
    {
        IOException repeated = new("两个原故障位置共享同一个实例。");
        ObjectDisposedException nestedLeaf = new("嵌套目录故障");
        AggregateException nested = new("原始嵌套根", nestedLeaf);
        Exception[] roots = [repeated, nested, repeated];
        await using CatalogLifetimeScenario scenario = new(holdClose: true);
        scenario.ExpectedErrors.AddRange(roots);
        Task run = scenario.Start();
        await scenario.AuthenticateAsync();
        Task original = await EnterStageAsync(scenario, stage);
        HostAuthenticatedCatalogLifetime lifetime = scenario.Lifetime;
        if (stage == "Source") scenario.FailSource(roots);
        else if (stage == "Write") scenario.Stream.CatalogWrite.Fail(roots);
        else scenario.Stream.CatalogFlush.Fail(roots);
        await scenario.Stream.DisposeEntered.Task.WaitAsync(CatalogLifetimeScenario.Guard);
        await CatalogLifetimeScenario.CaptureAsync(original);
        Assert.True(original.IsFaulted);
        AssertRoots(original.Exception!);
        Assert.False(run.IsCompleted);
        scenario.Stream.ReleaseHolding(0);
        await lifetime.Completion!.WaitAsync(CatalogLifetimeScenario.Guard);
        await scenario.Revoked!.WaitAsync(CatalogLifetimeScenario.Guard);
        Assert.False(run.IsCompleted);
        Task close = scenario.Connection.CloseAsync();
        Assert.False(close.IsCompleted);
        Assert.Same(close, scenario.Connection.CloseAsync());
        scenario.Stream.ReleaseDispose();

        Exception? actual = await CatalogLifetimeScenario.CaptureAsync(run);
        Exception diagnostic = Assert.IsType<Exception>(actual);
        Assert.Equal($"host-catalog-operation-{stage}", diagnostic.Message);
        AssertRoots(Assert.IsType<AggregateException>(diagnostic.InnerException));
        Assert.True(run.IsFaulted);
        Assert.Same(diagnostic, Assert.Single(run.Exception!.InnerExceptions));
        AssertRoots(Assert.IsType<AggregateException>(lifetime.Snapshot.Attempt.OperationError));
        Assert.Null(scenario.Router.Rejection);
        Assert.True(close.IsCompletedSuccessfully);
        scenario.AssertJoined();

        void AssertRoots(AggregateException error)
        {
            // 不 Flatten，不按异常引用去重；原树的三处故障位置与嵌套容器身份均必须保留。
            Assert.Collection(error.InnerExceptions,
                error => Assert.Same(repeated, error),
                error => Assert.Same(nested, error),
                error => Assert.Same(repeated, error));
            Assert.Same(nestedLeaf, Assert.Single(nested.InnerExceptions));
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Parent_Stop_Revokes_Real_Video_Lease_And_Joins_Original_Video_Completion()
    {
        await using CatalogLifetimeScenario scenario = new();
        Task run = scenario.Start();
        await scenario.AuthenticateAsync();
        HostAuthenticatedCatalogLifetime lifetime = scenario.Lifetime;
        VideoAttachLease video = scenario.AttachVideo();
        Task originalVideoCompletion = video.Completed;
        Assert.Same(scenario.Revoked, video.Revoked);
        Assert.False(originalVideoCompletion.IsCompleted);
        Assert.False(video.Revoked.IsCompleted);
        Assert.False(scenario.SourceOperation!.IsCompleted);

        await scenario.CancelHostAsync().WaitAsync(CatalogLifetimeScenario.Guard);
        await scenario.AssertClosedWhileJoiningAsync(run);
        await CatalogLifetimeScenario.AssertSignalAsync(video.Revoked);
        Assert.Equal(0, scenario.Context.SessionRegistry.ActiveSessionCount);
        Assert.False(originalVideoCompletion.IsCompleted);
        Assert.False(scenario.SourceOperation.IsCompleted);
        Assert.False(run.IsCompleted);

        // 目录、原 holding 与受控关闭任务全部结束，唯一尚未放行的是实际登记返回的视频完成根。
        scenario.ReleaseCatalog();
        scenario.Stream.ReleaseHolding(0);
        await scenario.SourceOperation.WaitAsync(CatalogLifetimeScenario.Guard);
        await scenario.Stream.HoldingOperation!.WaitAsync(CatalogLifetimeScenario.Guard);
        await lifetime.Completion!.WaitAsync(CatalogLifetimeScenario.Guard);
        await scenario.Connection.CloseAsync().WaitAsync(CatalogLifetimeScenario.Guard);
        Assert.True(lifetime.Snapshot.AllOwnedRootsReleased);
        Assert.False(originalVideoCompletion.IsCompleted);
        Assert.False(run.IsCompleted);
        Assert.Same(originalVideoCompletion, video.Completed);
        video.Complete();
        await run.WaitAsync(CatalogLifetimeScenario.Guard);
        Assert.True(originalVideoCompletion.IsCompletedSuccessfully);
        scenario.AssertJoined();
    }

    [Fact(Timeout = 60_000)]
    public async Task Source_And_Timer_Dispose_Sharing_Exception_Keep_Two_Independent_Source_Labels()
    {
        InvalidOperationException shared = new("目录来源与预算 timer 释放共享的原异常实例。");
        await using CatalogLifetimeScenario scenario = new(catalogTimerDisposeFailure: shared);
        scenario.ExpectedErrors.Add(shared);
        Task run = scenario.Start();
        await scenario.AuthenticateAsync();
        HostAuthenticatedCatalogLifetime lifetime = scenario.Lifetime;
        Assert.Equal(1, scenario.CleanupClock!.CatalogTimers);
        scenario.FailSource(shared);
        await scenario.AssertClosedWhileJoiningAsync(run);
        Assert.Same(shared, await CatalogLifetimeScenario.CaptureAsync(scenario.SourceOperation!));
        scenario.Stream.ReleaseHolding(0);
        Exception? actual = await CatalogLifetimeScenario.CaptureAsync(run);

        AggregateException result = Assert.IsType<AggregateException>(actual);
        Assert.Equal(2, result.InnerExceptions.Count);
        Exception operation = Assert.Single(result.InnerExceptions,
            error => error.Message == "host-catalog-operation-Source");
        Exception cleanup = Assert.Single(result.InnerExceptions,
            error => error.Message == "host-catalog-budget-cleanup");
        Assert.IsType<Exception>(operation);
        Assert.IsType<Exception>(cleanup);
        Assert.NotSame(operation, cleanup);
        Assert.Same(shared, operation.InnerException);
        Assert.Same(shared, cleanup.InnerException);
        Assert.True(run.IsFaulted);
        Assert.Same(result, Assert.Single(run.Exception!.InnerExceptions));
        Assert.Same(shared, lifetime.Snapshot.Attempt.OperationError);
        Assert.Same(shared, lifetime.Snapshot.Attempt.Budget!.CleanupError);
        Assert.Equal(1, scenario.CleanupClock.DisposeCalls);
        Task originalDispose = Assert.IsAssignableFrom<Task>(scenario.CleanupClock.DisposeOperation);
        Assert.True(originalDispose.IsFaulted);
        Assert.Same(shared, Assert.Single(originalDispose.Exception!.InnerExceptions));
        Assert.False(lifetime.Snapshot.AllOwnedRootsReleased);
        scenario.AssertJoined();
    }

    private static async Task<Task> EnterStageAsync(CatalogLifetimeScenario scenario, string stage)
    {
        if (stage == "Source") return scenario.SourceOperation!;
        scenario.ReleaseCatalog();
        await scenario.Stream.CatalogWrite.Entered.Task.WaitAsync(CatalogLifetimeScenario.Guard);
        if (stage == "Write") return scenario.Stream.CatalogWrite.Operation!;
        scenario.Stream.CatalogWrite.Release();
        await scenario.Stream.CatalogFlush.Entered.Task.WaitAsync(CatalogLifetimeScenario.Guard);
        return scenario.Stream.CatalogFlush.Operation!;
    }

    private static void ReleaseStage(CatalogLifetimeScenario scenario, string stage)
    {
        if (stage == "Source") scenario.ReleaseCatalog();
        else if (stage == "Write") scenario.Stream.CatalogWrite.Release();
        else scenario.Stream.CatalogFlush.Release();
    }

    private static async Task SendCatalogAsync(CatalogLifetimeScenario scenario)
    {
        await EnterStageAsync(scenario, "Flush");
        scenario.Stream.CatalogFlush.Release();
    }

    private static void AssertDiagnostic(Task run, Exception? actual, string label, Exception original)
    {
        Exception diagnostic = Assert.IsType<Exception>(actual);
        Assert.Equal(label, diagnostic.Message);
        Assert.Same(original, diagnostic.InnerException);
        Assert.True(run.IsFaulted);
        Assert.Same(diagnostic, Assert.Single(run.Exception!.InnerExceptions));
    }
}
