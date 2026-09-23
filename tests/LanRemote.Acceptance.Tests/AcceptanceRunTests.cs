using System.IO;

namespace LanRemote.Acceptance.Tests;

[Collection(AcceptanceStageFiveCollection.Name)]
public sealed class AcceptanceRunTests
{
    [Fact(Timeout = 20_000)]
    public async Task Initial_File_Unavailable_Poisons_Pass_Without_Using_Default_Directory()
    {
        await Task.Yield();
        using AcceptanceTestDirectory directory = new();
        string blocked = System.IO.Path.Combine(directory.Path, "directory-not-a-log");
        Directory.CreateDirectory(blocked);
        AcceptanceRun run = AcceptanceRun.CreateAtFile(blocked, "completion-test");

        Assert.True(run.Log.FileUnavailable);
        Assert.Null(run.Log.FilePath);
        Assert.Equal(AcceptanceOutcome.HarnessError, run.Complete(AcceptanceOutcome.Pass, "初始落盘失败"));
        Assert.Contains("[RESULT] outcome   = HARNESS_ERROR", run.Log.ReadFrom(0, 100));
        Assert.Single(run.Log.ReadFrom(0, 100).Where(line => line == "RUN COMPLETE"));
    }

    [Theory(Timeout = 20_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exclusive_File_Lock_Poisons_Append_Or_Footer_And_Remains_Poisoned(bool failInFooter)
    {
        await Task.Yield();
        using AcceptanceTestDirectory directory = new();
        AcceptanceRun run = AcceptanceRun.Create(directory.Path, "completion-test");
        string file = Assert.IsType<string>(run.Log.FilePath);
        run.Log.WriteLine("落盘基线");
        Assert.False(run.Log.FileUnavailable);
        Assert.Contains("落盘基线", File.ReadAllLines(file));

        // FileShare.None 触发真正的 File.AppendAllText 失败，不用事件抛错假装磁盘失败。
        using (FileStream exclusive = new(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            if (!failInFooter)
            {
                run.Log.WriteLine("独占期间缺失的证据");
                Assert.True(run.Log.FileUnavailable);
                Assert.Contains("独占期间缺失的证据", run.Log.ReadFrom(0, 100));
            }
            Assert.Equal(AcceptanceOutcome.HarnessError, run.Complete(AcceptanceOutcome.Pass, "真实写盘失败"));
            Assert.True(run.Log.FileUnavailable);
        }

        string[] disk = File.ReadAllLines(file);
        Assert.Equal(new[] { "落盘基线" }, disk);
        Assert.DoesNotContain("RUN COMPLETE", disk);
        string before = run.Log.All;
        Assert.Equal(AcceptanceOutcome.HarnessError, run.Complete(AcceptanceOutcome.Pass, "重试不能修复证据"));
        Assert.Equal(before, run.Log.All);
        Assert.Single(run.Log.ReadFrom(0, 100).Where(line => line == "RUN COMPLETE"));
        if (failInFooter)
            Assert.Contains(run.Log.ReadFrom(0, 100), line => line.StartsWith(
                "[RESULT][CORRECTION] outcome=HARNESS_ERROR reason=footer-write-failed;", StringComparison.Ordinal));
        run.Log.WriteLine("解除锁后也不得恢复为有效证据");
        Assert.True(run.Log.FileUnavailable);
        Assert.Equal(disk, File.ReadAllLines(file));
    }

    [Fact(Timeout = 20_000)]
    public async Task Concurrent_Complete_Is_Idempotent_And_Writes_Exactly_One_Footer()
    {
        using AcceptanceTestDirectory directory = new();
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(12));
        AcceptanceRun run = AcceptanceRun.Create(directory.Path, "completion-test");
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<AcceptanceOutcome>[] calls = Enumerable.Range(0, 16).Select(index => Task.Run(async () =>
        {
            await start.Task.WaitAsync(guard.Token);
            return run.Complete(AcceptanceOutcome.Pass, $"并发结算 {index}");
        })).ToArray();
        try
        {
            start.SetResult();
            AcceptanceOutcome[] results = await Task.WhenAll(calls).WaitAsync(guard.Token);
            Assert.All(results, result => Assert.Equal(AcceptanceOutcome.Pass, result));
            string before = run.Log.All;
            Assert.Equal((int)AcceptanceOutcome.Pass, AcceptanceRun.Finish(run, AcceptanceOutcome.Fail, "迟到结算"));
            Assert.Equal(before, run.Log.All);
            Assert.Single(run.Log.ReadFrom(0, 100).Where(line => line == "RUN COMPLETE"));
            Assert.Single(File.ReadAllLines(run.Log.FilePath!).Where(line => line == "RUN COMPLETE"));
            Assert.Single(run.Log.ReadFrom(0, 100).Where(line => line.StartsWith("[RESULT] outcome", StringComparison.Ordinal)));
        }
        finally
        {
            start.TrySetResult();
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
            await Task.WhenAll(calls).WaitAsync(cleanup.Token);
        }
    }

    [Theory(Timeout = 20_000)]
    [InlineData(AcceptanceOutcome.Pass)]
    [InlineData(AcceptanceOutcome.Fail)]
    [InlineData(AcceptanceOutcome.PreconditionUnmet)]
    [InlineData(AcceptanceOutcome.HarnessError)]
    [InlineData(AcceptanceOutcome.InvalidRun)]
    public async Task Operator_Abort_Outranks_Background_Fault_And_Observed_Outcome(AcceptanceOutcome observed)
    {
        await Task.Yield();
        using AcceptanceTestDirectory directory = new();
        AcceptanceRun run = AcceptanceRun.Create(directory.Path, "completion-test");
        run.ReportBackgroundFault("测试后台工作", new InvalidOperationException("人工故障"));
        run.MarkOperatorAbort("测试操作者停止");
        Assert.True(run.AbortedByOperator);
        Assert.True(run.HasBackgroundFaults);
        Assert.Equal(AcceptanceOutcome.InvalidRun, run.Settle(observed));
        Assert.Equal(AcceptanceOutcome.InvalidRun, run.Complete(observed, "优先级"));
        Assert.Contains("[RESULT] outcome   = INVALID_RUN", File.ReadAllLines(run.Log.FilePath!));
        Assert.Single(run.Log.ReadFrom(0, 100).Where(line => line == "RUN COMPLETE"));
    }

    [Theory(Timeout = 20_000)]
    [InlineData(AcceptanceOutcome.Pass, AcceptanceOutcome.HarnessError)]
    [InlineData(AcceptanceOutcome.Fail, AcceptanceOutcome.HarnessError)]
    [InlineData(AcceptanceOutcome.InvalidRun, AcceptanceOutcome.InvalidRun)]
    public async Task Background_Fault_Poisons_Pass_But_Cannot_Overwrite_Invalid(
        AcceptanceOutcome observed, AcceptanceOutcome expected)
    {
        await Task.Yield();
        using AcceptanceTestDirectory directory = new();
        AcceptanceRun run = AcceptanceRun.Create(directory.Path, "completion-test");
        run.ReportBackgroundFault("测试采样器", new IOException("人工故障"));
        Assert.False(run.AbortedByOperator);
        Assert.False(run.Log.FileUnavailable);
        Assert.Contains("测试采样器: IOException: 人工故障", run.BackgroundFaultSummary);
        Assert.Equal(expected, run.Complete(observed, "后台故障优先级"));
        Assert.Contains("[RESULT] outcome   = " + expected.Code(), File.ReadAllLines(run.Log.FilePath!));
    }

    [Fact(Timeout = 20_000)]
    public async Task ReadFrom_Is_Bounded_Validates_Arguments_And_Returns_Independent_Snapshots()
    {
        await Task.Yield();
        using AcceptanceTestDirectory directory = new();
        AcceptanceRun run = AcceptanceRun.Create(directory.Path, "log-test");
        string[] lines = Enumerable.Range(0, 451).Select(index => $"证据 {index}").ToArray();
        foreach (string line in lines) { run.Log.WriteLine(line); }

        IReadOnlyList<string> first = run.Log.ReadFrom(0);
        Assert.Equal(lines.Take(200), first);
        Assert.Equal(lines.Skip(200).Take(37), run.Log.ReadFrom(200, 37));
        Assert.Equal(new[] { lines[^1] }, run.Log.ReadFrom(450, 200));
        Assert.Empty(run.Log.ReadFrom(451));
        Assert.Empty(run.Log.ReadFrom(int.MaxValue, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => run.Log.ReadFrom(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => run.Log.ReadFrom(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => run.Log.ReadFrom(0, -1));
        run.Log.WriteLine("追加证据");
        Assert.Equal(200, first.Count);
        Assert.Equal(lines.Take(200), first);
        Assert.Equal(new[] { "追加证据" }, run.Log.ReadFrom(451, 1));
        Assert.Equal(lines.Append("追加证据"), File.ReadAllLines(run.Log.FilePath!));
        Assert.False(run.Log.FileUnavailable);
    }
}

// xUnit 2 的 Timeout 要求禁用 collection 并行；也避免同进程其它测试争抢采样线程。
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AcceptanceStageFiveCollection
{
    public const string Name = "阶段5有界测试";
}

// 所有阶段5测试只把日志放进自己创建的随机临时目录；绝不清理默认目录或父目录。
internal sealed class AcceptanceTestDirectory : IDisposable
{
    private readonly DirectoryInfo _owned = Directory.CreateTempSubdirectory("lanremote-stage5-tests-");
    internal string Path => _owned.FullName;
    public void Dispose() => _owned.Delete(recursive: true);
}
