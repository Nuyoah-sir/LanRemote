using System.IO;
using System.Reflection;

namespace LanRemote.Acceptance.Tests;

[Collection(AcceptanceStageFiveCollection.Name)]
public sealed class ReadOnlyNetworkTests
{
    [Theory]
    [InlineData("prepare-lab --lab-role A")]
    [InlineData("prepare-lab --lab-role B")]
    [InlineData("lab-apply --lab-role A")]
    [InlineData("lab-apply --lab-role B")]
    [InlineData("lab-undo")]
    [InlineData("LAB-APPLY --lab-role B")]
    public void Legacy_Verbs_Are_Rejected_With_Or_Without_Headless_And_Elevated(string invocation)
    {
        foreach (bool headless in new[] { false, true })
        foreach (bool elevated in new[] { false, true })
        {
            List<string> args = new();
            if (headless) { args.Add("--headless"); }
            args.AddRange(invocation.Split(' '));
            if (elevated) { args.Add("--elevated"); }
            AssertRejected(args.ToArray());
        }
    }

    [Theory]
    [InlineData("--elevated")]
    [InlineData("--elevated --headless info")]
    [InlineData("--headless info --elevated")]
    [InlineData("--headless info --ELEVATED=true")]
    [InlineData("--headless host --elevated false")]
    [InlineData("--headless client --peer TEST-CODE --elevated")]
    [InlineData("--headless info --log-dir --elevated")]
    [InlineData("--headless info --log-dir lab-undo")]
    [InlineData("--headless info --lab-role A")]
    [InlineData("--headless info --lab-role=B")]
    [InlineData("--headless info --log-file C:/unused.log")]
    [InlineData("--headless info --log-file=C:/unused.log")]
    public void Legacy_Options_Cannot_Bypass_Rejection_As_Another_Options_Value(string invocation) =>
        AssertRejected(invocation.Split(' '));

    [Theory]
    [InlineData("--headless info", "info")]
    [InlineData("--headless host --seconds 180", "host")]
    [InlineData("--headless client --peer TEST-CODE --all", "client")]
    [InlineData("--headless client --address 192.168.137.2 --pin ABCD --all", "client")]
    public void Supported_Commands_Still_Parse_Without_Executing_Roles(string invocation, string role)
    {
        Assert.True(HeadlessCommand.TryParse(invocation.Split(' '), out HeadlessCommand? command, out string? error));
        Assert.Null(error);
        Assert.NotNull(command);
        Assert.Equal(role, command.Role);
        Assert.Null(command.LabRole);
        Assert.Null(command.LogFile);
    }

    [Fact]
    public void Empty_Arguments_Remain_The_Only_Normal_Gui_Startup_Input()
    {
        Assert.False(HeadlessCommand.TryParse([], out HeadlessCommand? command, out string? error));
        Assert.Null(command);
        Assert.Null(error);
    }

    [Theory(Timeout = 20_000)]
    [InlineData("prepare-lab", "A", false)]
    [InlineData("prepare-lab", "B", false)]
    [InlineData("lab-apply", "A", true)]
    [InlineData("lab-apply", "B", true)]
    [InlineData("lab-undo", null, true)]
    [InlineData("lab-undo", null, false)]
    [InlineData("info", "A", false)]
    [InlineData("info", null, true)]
    public async Task Directly_Constructed_Legacy_Commands_Are_Rejected_Before_Log_Or_Role_Creation(
        string role, string? labRole, bool hasLogFile)
    {
        AssertExecutionImplementationsRemoved();
        using AcceptanceTestDirectory directory = new();
        HeadlessCommand command = new(role, 0, null, [], null, null, 45873,
            directory.Path, labRole, hasLogFile ? Path.Combine(directory.Path, "unused.log") : null);

        int result = await HeadlessRunner.RunAsync(command);

        Assert.Equal((int)AcceptanceOutcome.HarnessError, result);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Theory(Timeout = 20_000)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Both_Legacy_Role_Entrypoints_Reject_Every_Action_Even_With_Cancelled_Token(int action)
    {
        AssertExecutionImplementationsRemoved();
        using AcceptanceTestDirectory directory = new();
        foreach (bool worker in new[] { false, true })
        foreach (bool cancelled in new[] { false, true })
        {
            AcceptanceRun run = AcceptanceRun.Create(directory.Path, "disabled-network-test");
            CancellationToken token = new(cancelled);
            AcceptanceOutcome outcome = worker
                ? await LabWorkerRole.RunAsync(run, (LabAction)action, token)
                : await LabSetupRole.PrepareAsync(run, (LabAction)action, token);

            Assert.Equal(AcceptanceOutcome.HarnessError, outcome);
            Assert.Contains("reason=network-configuration-disabled", run.Log.All);
            Assert.Contains(LabSetupRole.DisabledMessage, run.Log.All);
            Assert.DoesNotContain("outcome=PASS", run.Log.All);
            Assert.Single(run.Log.ReadFrom(0, 100), line => line == "RUN COMPLETE");
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task Removed_Elevation_Launcher_Returns_Refusal_Without_Starting_Anything()
    {
        // 先静态核对旧执行实现不存在；绝不拿真实 exe 或脚本试探 UAC。
        AssertExecutionImplementationsRemoved();
        ElevatedLauncher.LaunchResult result = await ElevatedLauncher.RunElevatedAsync(
            string.Empty, ["--headless", "lab-undo", "--elevated"], TimeSpan.Zero);

        Assert.False(result.Started);
        Assert.False(result.Cancelled);
        Assert.Equal((int)AcceptanceOutcome.HarnessError, result.ExitCode);
        Assert.Equal(LabSetupRole.DisabledMessage, result.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Local_Check_Text_Does_Not_Claim_End_To_End_Success_Or_Suggest_Network_Changes(bool ready)
    {
        // 只验证纯文本，不初始化真实身份存储、网卡或发现服务。
        string text = InfoRole.DescribeReadiness(ready);
        Assert.Contains("本地 PASS 仅代表本地检查通过", text);
        Assert.Contains("不代表发现、互通、认证或互联网可用", text);
        Assert.DoesNotContain("准备为", text);
        Assert.DoesNotContain("set-lab-ip", text);
        Assert.DoesNotContain("UAC", text);
        if (!ready)
        {
            Assert.Contains("未满足本地检查条件", text);
            Assert.Contains("检查现有网络连接", text);
            Assert.Contains("不会修改网络配置", text);
        }
    }

    [Fact]
    public void Window_Has_No_Legacy_Network_Buttons_Or_Event_Handlers()
    {
        // 只检查编译后的字段和方法，不实例化窗口、Application 或 Dispatcher。
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (string button in new[] { "PrepareAButton", "PrepareBButton", "UndoPrepareButton" })
        {
            Assert.Null(typeof(MainWindow).GetField(button, flags));
            Assert.Null(typeof(MainWindow).GetMethod(button + "_Click", flags));
        }
        Assert.Null(typeof(MainWindow).GetMethod("PrepareLab", flags));
        Assert.NotNull(typeof(MainWindow).GetMethod("SelfCheckButton_Click", flags));
    }

    [Fact]
    public void Usage_Only_Describes_Old_Network_Commands_As_Disabled()
    {
        Assert.Contains("--elevated 已禁用", HeadlessCommand.Usage);
        Assert.Contains("管理员权限或 UAC 不予豁免", HeadlessCommand.Usage);
        Assert.DoesNotContain("LanRemote.Acceptance.exe --headless prepare-lab", HeadlessCommand.Usage);
        Assert.DoesNotContain("请 UAC 提权", HeadlessCommand.Usage);
    }

    private static void AssertRejected(string[] args)
    {
        Assert.False(HeadlessCommand.TryParse(args, out HeadlessCommand? command, out string? error));
        Assert.Null(command);
        Assert.Equal(LabSetupRole.DisabledMessage, error);
    }

    private static void AssertExecutionImplementationsRemoved()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;
        // 旧实现回流时先失败，不调用可能带脚本/提权的入口。
        Assert.Equal("PrepareAsync", Assert.Single(typeof(LabSetupRole).GetMethods(flags)).Name);
        Assert.Equal("RunAsync", Assert.Single(typeof(LabWorkerRole).GetMethods(flags)).Name);
        Assert.Equal("RunElevatedAsync", Assert.Single(typeof(ElevatedLauncher).GetMethods(flags)).Name);
    }
}
