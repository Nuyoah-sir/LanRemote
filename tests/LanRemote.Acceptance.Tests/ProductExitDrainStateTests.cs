using System.IO;
using System.Reflection;
using LanRemote.App;
using LanRemote.App.Services;
using LanRemote.Core.Infrastructure;
using LanRemote.Security.Certificates;
using LanRemote.Security.Identity;
using LanRemote.Security.Secrets;
using LanRemote.Transport;

namespace LanRemote.Acceptance.Tests;

public sealed class ProductExitDrainStateTests
{
    private static readonly Func<IReadOnlyList<Task>, IReadOnlyList<Task>, bool, bool, ExitDrainStatus> Evaluate =
        typeof(LanRemote.App.App)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Single(method => method.Name == "EvaluateExitDrainState")
            .CreateDelegate<Func<IReadOnlyList<Task>, IReadOnlyList<Task>, bool, bool, ExitDrainStatus>>();

    private static readonly Func<IReadOnlyList<Task>, IReadOnlyList<Task>, bool, bool,
        ClientConnectService?, ExitDrainStatus> EvaluateWithClientEvidence =
        typeof(LanRemote.App.App)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Single(method => method.Name == "EvaluateExitDrainWithClientEvidence")
            .CreateDelegate<Func<IReadOnlyList<Task>, IReadOnlyList<Task>, bool, bool,
                ClientConnectService?, ExitDrainStatus>>();

    [Fact(Timeout = 20_000)]
    public async Task Any_Unfinished_Original_Stop_Remains_Pending_Even_With_Other_Faults_And_Undrained_Host()
    {
        for (int pendingIndex = 0; pendingIndex < 6; pendingIndex++)
        {
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task[] originals = OriginalStops();
            originals[pendingIndex] = release.Task;
            originals[(pendingIndex + 1) % originals.Length] = FaultedTask();
            try
            {
                Assert.Equal(ExitDrainStatus.Pending, Evaluate(originals, [], false, false));
            }
            finally
            {
                release.TrySetResult();
                await release.Task;
            }
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task Six_Successful_Original_Stops_And_Drained_Host_Are_Clean()
    {
        await Task.Yield();
        Assert.Equal(ExitDrainStatus.Clean, Evaluate(OriginalStops(), [], true, true));
    }

    [Fact(Timeout = 20_000)]
    public async Task Successful_Stops_Without_Drained_Host_Are_Unproven()
    {
        await Task.Yield();
        Assert.Equal(ExitDrainStatus.Unproven, Evaluate(OriginalStops(), [], false, true));
    }

    [Fact(Timeout = 20_000)]
    public async Task Client_Without_Independent_Cleanup_Proof_Cannot_Be_Clean()
    {
        await Task.Yield();
        Assert.Equal(ExitDrainStatus.Unproven, Evaluate(OriginalStops(), [], true, false));
    }

    [Fact(Timeout = 20_000)]
    public async Task Original_Client_Fault_Cannot_Be_Washed_Clean_By_Successful_Recheck()
    {
        await Task.Yield();
        Task client = FaultedTask();
        Assert.True(client.IsCompleted);
        Assert.Equal(ExitDrainStatus.Unproven,
            Evaluate(OriginalStops(client: client), [Task.CompletedTask], true, true));
    }

    [Fact(Timeout = 20_000)]
    public async Task Original_Local_Host_Fault_Cannot_Be_Cleared_By_Successful_Vm_Recheck()
    {
        await Task.Yield();
        // 传入 true 只覆盖纯分类分支，不证明真实 Host 故障后能取得 IsFullyDrained。
        ExitDrainStatus state = Evaluate(
            OriginalStops(localHost: FaultedTask()), [Task.CompletedTask], true, true);
        Assert.Equal(ExitDrainStatus.Unproven, state);
    }

    [Fact(Timeout = 20_000)]
    public async Task Original_Discovery_Fault_Cannot_Allow_Diagnostic_Exit()
    {
        await Task.Yield();
        ExitDrainStatus state = Evaluate(OriginalStops(discovery: FaultedTask()), [], true, true);
        Assert.Equal(ExitDrainStatus.Unproven, state);
    }

    [Fact(Timeout = 20_000)]
    public async Task Fault_Or_Cancellation_In_Any_Original_Stop_Is_Unproven()
    {
        await Task.Yield();
        for (int index = 0; index < 6; index++)
        {
            foreach (Task failed in new[] { FaultedTask(), Task.FromCanceled(new CancellationToken(true)) })
            {
                Task[] originals = OriginalStops();
                originals[index] = failed;
                ExitDrainStatus state = Evaluate(originals, [], true, true);
                Assert.Equal(ExitDrainStatus.Unproven, state);
            }
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task Pending_Recheck_Blocks_Drain_And_Faulted_Recheck_Is_Unproven()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] originals = OriginalStops();
        try
        {
            Assert.Equal(ExitDrainStatus.Pending, Evaluate(originals, [release.Task], true, true));
        }
        finally
        {
            release.TrySetResult();
            await release.Task;
        }

        ExitDrainStatus faulted = Evaluate(originals, [FaultedTask()], true, true);
        Assert.Equal(ExitDrainStatus.Unproven, faulted);
        Assert.Equal(ExitDrainStatus.Unproven,
            Evaluate(originals, [Task.FromCanceled(new CancellationToken(true))], true, true));
    }

    [Fact(Timeout = 20_000)]
    public async Task Fewer_Than_Six_Original_Stops_Are_Unproven()
    {
        await Task.Yield();
        Assert.Equal(ExitDrainStatus.Unproven,
            Evaluate([Task.CompletedTask, Task.CompletedTask, Task.CompletedTask,
                Task.CompletedTask, Task.CompletedTask], [], true, true));
    }

    [Fact(Timeout = 20_000)]
    public async Task New_Evaluator_Preserves_Strict_Clean_And_Client_Drain_Requirement()
    {
        await Task.Yield();
        Assert.Equal(ExitDrainStatus.Clean,
            EvaluateWithClientEvidence(OriginalStops(), [], true, true, null));
        Assert.Equal(ExitDrainStatus.Unproven,
            EvaluateWithClientEvidence(OriginalStops(), [], true, false, null));
    }

    [Fact(Timeout = 20_000)]
    public async Task Faulted_Client_Requires_A_Service_Certificate_For_The_Same_Original_Task()
    {
        await Task.Yield();
        using AcceptanceTestDirectory directory = new();
        DpapiSecretVault vault = new(AppPaths.FromRootDirectory(directory.Path));
        using DeviceCertificateService certificates = new(vault);
        ClientConnectService service = new(new DeviceIdentityService(vault, certificates),
            new ControlClientConnector());
        Task originalClient = FaultedTask();
        FieldInfo latestTask = typeof(ClientConnectService)
            .GetField("_lastCleanupTask", BindingFlags.NonPublic | BindingFlags.Instance)!;

        Assert.Equal(ExitDrainStatus.Unproven,
            EvaluateWithClientEvidence(OriginalStops(client: originalClient), [], true, false, null));
        // 仅把最近任务改为同一个故障 Task 并不能制造服务内部的资源释放证书。
        latestTask.SetValue(service, originalClient);
        Assert.Same(originalClient, service.LastCleanupTask);
        Assert.Equal(ExitDrainStatus.Unproven,
            EvaluateWithClientEvidence(OriginalStops(client: originalClient), [], true, false, service));

        Task unrelated = FaultedTask();
        latestTask.SetValue(service, unrelated);
        Assert.NotSame(originalClient, service.LastCleanupTask);
        Assert.Equal(ExitDrainStatus.Unproven,
            EvaluateWithClientEvidence(OriginalStops(client: originalClient), [], true, false, service));
        Assert.Equal(ExitDrainStatus.Unproven,
            EvaluateWithClientEvidence(OriginalStops(client: originalClient), [], false, false, service));
    }

    [Fact(Timeout = 20_000)]
    public async Task Canceled_Client_And_Other_Failed_Original_Stops_Cannot_Exit_With_Diagnostics()
    {
        await Task.Yield();
        Task canceled = Task.FromCanceled(new CancellationToken(true));
        Assert.Equal(ExitDrainStatus.Unproven,
            EvaluateWithClientEvidence(OriginalStops(client: canceled), [], true, false, null));

        for (int index = 1; index < 6; index++)
        {
            Task[] originals = OriginalStops(client: FaultedTask());
            originals[index] = FaultedTask();
            Assert.Equal(ExitDrainStatus.Unproven,
                EvaluateWithClientEvidence(originals, [], true, false, null));
        }
        Assert.Equal(ExitDrainStatus.Unproven,
            EvaluateWithClientEvidence(OriginalStops(client: FaultedTask()), [FaultedTask()], true, false, null));
        Assert.Equal(ExitDrainStatus.Unproven,
            EvaluateWithClientEvidence(OriginalStops(client: FaultedTask()),
                [Task.FromCanceled(new CancellationToken(true))], true, false, null));
    }

    [Fact(Timeout = 20_000)]
    public async Task Pending_Client_Or_Recheck_Still_Blocks_Evidence_Decision()
    {
        await Task.Yield();
        TaskCompletionSource pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Assert.Equal(ExitDrainStatus.Pending,
                EvaluateWithClientEvidence(OriginalStops(client: pending.Task), [], true, false, null));
            Assert.Equal(ExitDrainStatus.Pending,
                EvaluateWithClientEvidence(OriginalStops(client: FaultedTask()), [pending.Task], true, false, null));
        }
        finally
        {
            pending.TrySetResult();
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task Original_Client_Fault_Tree_Is_Kept_Without_Flattening()
    {
        await Task.Yield();
        OperationCanceledException canceledRead = new("主动关闭视频读取");
        IOException controlMonitor = new("主动关闭控制监控");
        AggregateException originalReport = new("双通道读取诊断", canceledRead, controlMonitor);
        Task client = Task.FromException(originalReport);
        AggregateException expectedTask = client.Exception!;
        MethodInfo collect = typeof(LanRemote.App.App).GetMethod("CollectOriginalExitErrors",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        AggregateException retained = Assert.IsType<AggregateException>(
            collect.Invoke(null, [OriginalStops(client: client)]));
        AggregateException taskError = Assert.IsType<AggregateException>(Assert.Single(retained.InnerExceptions));
        Assert.Same(originalReport, Assert.Single(expectedTask.InnerExceptions));
        Assert.Same(originalReport, Assert.Single(taskError.InnerExceptions));
        Assert.Same(canceledRead, originalReport.InnerExceptions[0]);
        Assert.Same(controlMonitor, originalReport.InnerExceptions[1]);
    }

    private static Task[] OriginalStops(
        Task? client = null, Task? loading = null, Task? localHost = null,
        Task? discovery = null, Task? watcher = null, Task? diHost = null) =>
        [client ?? Task.CompletedTask, loading ?? Task.CompletedTask, localHost ?? Task.CompletedTask,
            discovery ?? Task.CompletedTask, watcher ?? Task.CompletedTask, diHost ?? Task.CompletedTask];

    private static Task FaultedTask() => Task.FromException(new InvalidOperationException("停机故障"));
}
