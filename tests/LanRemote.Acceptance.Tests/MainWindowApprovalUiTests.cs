using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using LanRemote.Core.Models;
using LanRemote.Transport;

namespace LanRemote.Acceptance.Tests;

[Collection(AcceptanceStageFiveCollection.Name)]
public sealed class MainWindowApprovalUiTests
{
    private const string ObservedPrefix = "[UI][APPROVAL-OBSERVED]";
    private const string RemovedPrefix = "[UI][APPROVAL-REMOVED]";
    private const string SubmittedPrefix = "[UI][APPROVAL]";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    [Fact(Timeout = 60_000)]
    public Task Isolated_Constructor_And_StartRole_Do_Not_Load_Window_Or_Start_Workers() => WithWindowAsync(ui =>
    {
        Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
        Assert.IsType<DispatcherSynchronizationContext>(SynchronizationContext.Current);
        Assert.False(Field<DispatcherTimer>(ui.Window, "_uiTimer").IsEnabled);
        Assert.False(Field<bool>(ui.Window, "_ready"));
        Assert.False(Field<bool>(ui.Window, "_busy"));
        Assert.Null(Field<Task?>(ui.Window, "_runTask"));
        Assert.Null(Field<Task?>(ui.Window, "_busyTask"));
        Assert.Null(Field<Task?>(ui.Window, "_keyTask"));
        Assert.True(ui.State.IsHost);
        Assert.False(ui.State.IsStopped);
        Assert.Null(ui.State.GetHostContext());
        Assert.Empty(ui.Inbox.GetSnapshot());
        Assert.Equal(ui.Directory, Field<string>(ui.Window, "_logDirectory"));
        AcceptanceLog processLog = Field<AcceptanceLog>(ui.Window, "_processLog");
        Assert.Equal(Path.Combine(ui.Directory, "gui.log"), processLog.FilePath);
        Assert.Equal(ui.Directory, Path.GetDirectoryName(ui.State.Run.Log.FilePath));
        Assert.False(processLog.FileUnavailable);
        Assert.False(ui.State.Run.Log.FileUnavailable);
        processLog.WriteLine("[TEST] 隔离审批窗口日志");
        Assert.Contains("[TEST] 隔离审批窗口日志", File.ReadAllText(processLog.FilePath!));
        AssertNoNativeWindow(ui.Window);
        return Task.CompletedTask;
    });

    [Fact(Timeout = 60_000)]
    public Task Enqueue_And_Every_Refresh_Update_Count_Without_Automatic_Selection() => WithWindowAsync(ui =>
    {
        Assert.Contains("无待批", ui.Queue.Text);
        Assert.DoesNotContain("无待批", ui.Status.Text);
        string initialAction = ui.Status.Text;
        ui.Refresh();
        AssertQueue(ui, 0);
        AssertNoSelection(ui);

        PendingApproval first = ui.Enqueue();
        // 收件箱本身不推 UI；只有显式拉取才改变实际控件。
        Assert.Empty(ui.List.Items);
        ui.Refresh();
        AssertQueue(ui, 1);
        AssertNoSelection(ui);
        PendingApproval second = ui.Enqueue();
        ui.Refresh();
        AssertQueue(ui, 2);
        AssertNoSelection(ui);
        Assert.Equal(initialAction, ui.Status.Text);

        ui.Queue.Text = "测试用过时提示";
        ui.Refresh();
        AssertQueue(ui, 2);
        Assert.Equal(initialAction, ui.Status.Text);
        Assert.False(first.Decision.IsCompleted);
        Assert.False(second.Decision.IsCompleted);
        Assert.Empty(ui.Lines(SubmittedPrefix));
        return Task.CompletedTask;
    });

    [Theory(Timeout = 60_000)]
    [InlineData("ApproveButton", SessionPermission.Control, LocalApprovalOutcome.Approved, SessionPermission.Control)]
    [InlineData("ApproveButton", SessionPermission.ViewOnly, LocalApprovalOutcome.Approved, SessionPermission.ViewOnly)]
    [InlineData("ViewOnlyButton", SessionPermission.Control, LocalApprovalOutcome.Approved, SessionPermission.ViewOnly)]
    [InlineData("RejectButton", SessionPermission.Control, LocalApprovalOutcome.Denied, null)]
    [InlineData("RejectButton", SessionPermission.ViewOnly, LocalApprovalOutcome.Denied, null)]
    public Task Manual_Selection_And_Routed_Click_Complete_Only_The_Selected_Request(
        string buttonName, SessionPermission requested, LocalApprovalOutcome outcome, SessionPermission? grant) =>
        WithWindowAsync(async ui =>
        {
            PendingApproval other = ui.Enqueue();
            PendingApproval selected = ui.Enqueue(Request(permission: requested));
            ui.Refresh();
            AssertNoSelection(ui);
            ui.Select(selected.Snapshot);
            object selection = ui.List.SelectedItem;
            Assert.True(ui.Named<Button>("ApproveButton").IsEnabled);
            Assert.True(ui.Named<Button>("RejectButton").IsEnabled);
            Assert.Equal(requested == SessionPermission.Control, ui.Named<Button>("ViewOnlyButton").IsEnabled);
            ui.Refresh();
            Assert.Same(selection, ui.List.SelectedItem);
            Assert.DoesNotContain("请先选中", ui.Queue.Text);
            Assert.False(selected.Decision.IsCompleted);
            Assert.False(other.Decision.IsCompleted);

            string line = ClickAndObserveBeforeUiUpdate(ui, buttonName);
            AssertSubmission(line, selected.Snapshot, true, outcome, grant);
            LocalApprovalDecision result = await selected.Decision.WaitAsync(TestTimeout);
            Assert.Equal(selected.Snapshot.RequestId, result.RequestId);
            Assert.Equal(outcome, result.Outcome);
            Assert.Equal(grant, result.GrantedPermission);
            Assert.False(other.Decision.IsCompleted);
            Assert.Equal(other.Snapshot.RequestId, Assert.Single(ui.Inbox.GetSnapshot()).RequestId);
            AssertNoSelection(ui);
            AssertQueue(ui, 1);
            Assert.Contains("已提交", ui.Status.Text);
            Assert.DoesNotContain("无待批", ui.Status.Text);
            string lastAction = ui.Status.Text;
            ui.Refresh();
            Assert.Equal(lastAction, ui.Status.Text);
            Assert.Single(ui.Lines(SubmittedPrefix));
            Assert.Contains(line, File.ReadAllLines(ui.State.Run.Log.FilePath!));
        });

    [Fact(Timeout = 60_000)]
    public Task Repeated_Refresh_Logs_Observed_Once_Per_Request_And_Generation_With_Real_Correlation() =>
        WithWindowAsync(async ui =>
        {
            DateTimeOffset before = DateTimeOffset.UtcNow;
            PendingApproval first = ui.Enqueue();
            PendingApproval other = ui.Enqueue();
            Assert.Empty(ui.Lines(ObservedPrefix));
            for (int i = 0; i < 5; i++) { ui.Refresh(); }
            Assert.Equal(2, ui.Lines(ObservedPrefix).Length);
            AssertObserved(ui, first.Snapshot, before);
            AssertObserved(ui, other.Snapshot, before);

            Assert.True(ui.Inbox.Cancel(first.Snapshot.RequestId, first.Snapshot.Generation));
            Assert.Equal(LocalApprovalOutcome.Cancelled, (await first.Decision.WaitAsync(TestTimeout)).Outcome);
            // 不在移除与重入之间刷新：同 ID 换代也必须分别记录，不能只按 RequestId 去重。
            PendingApproval replacement = ui.Enqueue(first.Snapshot.Request with
            {
                SessionId = Guid.NewGuid(),
                ConnectionId = Guid.NewGuid(),
            });
            Assert.NotEqual(first.Snapshot.Generation, replacement.Snapshot.Generation);
            for (int i = 0; i < 5; i++) { ui.Refresh(); }
            Assert.Equal(3, ui.Lines(ObservedPrefix).Length);
            AssertObserved(ui, first.Snapshot, before);
            AssertObserved(ui, other.Snapshot, before);
            AssertObserved(ui, replacement.Snapshot, before);
            string removed = Assert.Single(ui.Lines(RemovedPrefix));
            Assert.Equal(first.Snapshot.RequestId.ToString(), LogField(removed, "requestId"));
            Assert.Equal(first.Snapshot.Generation.ToString(), LogField(removed, "generation"));
            Assert.False(other.Decision.IsCompleted);
            Assert.False(replacement.Decision.IsCompleted);
            AssertNoSelection(ui);
            AssertNoNativeWindow(ui.Window);
        });

    [Theory(Timeout = 60_000)]
    [InlineData("cancel", LocalApprovalOutcome.Cancelled)]
    [InlineData("approve", LocalApprovalOutcome.Approved)]
    [InlineData("deny", LocalApprovalOutcome.Denied)]
    [InlineData("stop", LocalApprovalOutcome.Cancelled)]
    public Task Removed_Snapshot_Logs_Disappearance_Without_Inferring_Reason_Or_Overwriting_Last_Action(
        string action, LocalApprovalOutcome outcome) => WithWindowAsync(async ui =>
        {
            PendingApproval pending = ui.Enqueue();
            ui.Refresh();
            ui.Select(pending.Snapshot);
            string lastAction = ui.Status.Text;
            switch (action)
            {
                case "cancel":
                    Assert.True(ui.Inbox.Cancel(pending.Snapshot.RequestId, pending.Snapshot.Generation));
                    break;
                case "approve":
                    Assert.True(ui.Inbox.TrySubmit(pending.Snapshot.RequestId, pending.Snapshot.Generation,
                        LocalApprovalOutcome.Approved, SessionPermission.Control));
                    break;
                case "deny":
                    Assert.True(ui.Inbox.TrySubmit(pending.Snapshot.RequestId, pending.Snapshot.Generation,
                        LocalApprovalOutcome.Denied));
                    break;
                case "stop":
                    ui.State.Stop();
                    break;
            }
            Assert.Equal(outcome, (await pending.Decision.WaitAsync(TestTimeout)).Outcome);
            Assert.Empty(ui.Lines(RemovedPrefix));
            for (int i = 0; i < 3; i++) { ui.Refresh(); }
            string removed = Assert.Single(ui.Lines(RemovedPrefix));
            Assert.Equal(pending.Snapshot.RequestId.ToString(), LogField(removed, "requestId"));
            Assert.Equal(pending.Snapshot.Generation.ToString(), LogField(removed, "generation"));
            Assert.DoesNotMatch(@"\b(reason|outcome|decision|grant)=", removed);
            // 只能记队列差集，不能把消失描述为已超时、断连、批准或拒绝。
            Assert.DoesNotMatch("TimedOut|Cancelled|Approved|Denied|已超时|已断连|已批准|已拒绝", removed);
            Assert.Empty(ui.Lines(SubmittedPrefix));
            Assert.Equal(lastAction, ui.Status.Text);
            AssertQueue(ui, 0);
            AssertNoSelection(ui);
        });

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public Task Cancelling_Selected_Request_Never_Selects_Or_Approves_The_Next_Request(bool useToken) =>
        WithWindowAsync(async ui =>
        {
            using CancellationTokenSource cancellation = new();
            PendingApproval first = ui.Enqueue(token: cancellation.Token);
            PendingApproval next = ui.Enqueue();
            ui.Refresh();
            ui.Select(first.Snapshot);
            Assert.True(ui.Named<Button>("ApproveButton").IsEnabled);
            if (useToken) { await cancellation.CancelAsync(); }
            else { Assert.True(ui.Inbox.Cancel(first.Snapshot.RequestId, first.Snapshot.Generation)); }
            Assert.Equal(LocalApprovalOutcome.Cancelled, (await first.Decision.WaitAsync(TestTimeout)).Outcome);
            ui.Refresh();
            AssertQueue(ui, 1);
            AssertNoSelection(ui);
            Assert.False(next.Decision.IsCompleted);

            // 故意向禁用按钮投递已排队的路由事件，不把 IsEnabled 当成安全边界。
            string failed = ClickAndObserveBeforeUiUpdate(ui, "ApproveButton");
            AssertSubmission(failed, null, false, LocalApprovalOutcome.Approved, null);
            ui.Refresh();
            AssertNoSelection(ui);
            AssertQueue(ui, 1);
            Assert.False(next.Decision.IsCompleted);
            Assert.Equal(next.Snapshot.Generation, Assert.Single(ui.Inbox.GetSnapshot()).Generation);
        });

    [Theory(Timeout = 60_000)]
    [InlineData("ApproveButton", LocalApprovalOutcome.Approved, SessionPermission.Control)]
    [InlineData("ViewOnlyButton", LocalApprovalOutcome.Approved, SessionPermission.ViewOnly)]
    [InlineData("RejectButton", LocalApprovalOutcome.Denied, null)]
    public Task Stale_Selected_Generation_Click_Logs_Failure_And_Cannot_Act_On_Reused_Request_Id(
        string buttonName, LocalApprovalOutcome outcome, SessionPermission? grant) => WithWindowAsync(async ui =>
        {
            PendingApproval old = ui.Enqueue();
            ui.Refresh();
            ui.Select(old.Snapshot);
            Assert.True(ui.Named<Button>(buttonName).IsEnabled);
            Assert.True(ui.Inbox.Cancel(old.Snapshot.RequestId, old.Snapshot.Generation));
            Assert.Equal(LocalApprovalOutcome.Cancelled, (await old.Decision.WaitAsync(TestTimeout)).Outcome);
            PendingApproval replacement = ui.Enqueue(old.Snapshot.Request with
            {
                SessionId = Guid.NewGuid(),
                ConnectionId = Guid.NewGuid(),
            });
            Assert.Equal(old.Snapshot.RequestId, replacement.Snapshot.RequestId);
            Assert.NotEqual(old.Snapshot.Generation, replacement.Snapshot.Generation);
            // 不刷新界面，保留真实 ListBox 中选中的旧快照，模拟取消先于已排队点击。
            Assert.Equal(old.Snapshot.Generation, Assert.IsType<LocalApprovalSnapshot>(ui.List.SelectedItem).Generation);
            string failed = ClickAndObserveBeforeUiUpdate(ui, buttonName);
            AssertSubmission(failed, old.Snapshot, false, outcome, grant);
            Assert.DoesNotContain(replacement.Snapshot.Request.SessionId.ToString(), failed);
            Assert.DoesNotContain(replacement.Snapshot.Request.ConnectionId.ToString(), failed);
            Assert.False(replacement.Decision.IsCompleted);
            Assert.Equal(replacement.Snapshot.Generation, Assert.Single(ui.Inbox.GetSnapshot()).Generation);
            AssertNoSelection(ui);
            AssertQueue(ui, 1);
            Assert.Contains("未提交", ui.Status.Text);
            Assert.Contains(failed, File.ReadAllLines(ui.State.Run.Log.FilePath!));

            // 旧点击失败后，只有再次手选新代才能产生决定。
            ui.Select(replacement.Snapshot);
            string accepted = ClickAndObserveBeforeUiUpdate(ui, buttonName);
            AssertSubmission(accepted, replacement.Snapshot, true, outcome, grant);
            LocalApprovalDecision result = await replacement.Decision.WaitAsync(TestTimeout);
            Assert.Equal(outcome, result.Outcome);
            Assert.Equal(grant, result.GrantedPermission);
            Assert.Equal(2, ui.Lines(SubmittedPrefix).Length);
            AssertQueue(ui, 0);
            AssertNoSelection(ui);
        });

    [Theory(Timeout = 60_000)]
    [InlineData("ApproveButton", LocalApprovalOutcome.Approved)]
    [InlineData("ViewOnlyButton", LocalApprovalOutcome.Approved)]
    [InlineData("RejectButton", LocalApprovalOutcome.Denied)]
    public Task Failed_Click_Without_Selection_Logs_Missing_Correlation_And_Keeps_Queue_Text_Independent(
        string buttonName, LocalApprovalOutcome outcome) => WithWindowAsync(ui =>
        {
            PendingApproval first = ui.Enqueue();
            ui.Refresh();
            AssertNoSelection(ui);
            string line = ClickAndObserveBeforeUiUpdate(ui, buttonName);
            AssertSubmission(line, null, false, outcome, null);
            Assert.Contains("未提交", ui.Status.Text);
            string lastAction = ui.Status.Text;
            PendingApproval second = ui.Enqueue();
            ui.Refresh();
            AssertQueue(ui, 2);
            AssertNoSelection(ui);
            Assert.Equal(lastAction, ui.Status.Text);
            Assert.DoesNotContain("无待批", ui.Status.Text);
            Assert.DoesNotMatch("失效|失败|未提交|错误", ui.Queue.Text);
            Assert.False(first.Decision.IsCompleted);
            Assert.False(second.Decision.IsCompleted);
            Assert.Single(ui.Lines(SubmittedPrefix));
            Assert.Contains(line, File.ReadAllLines(ui.State.Run.Log.FilePath!));
            return Task.CompletedTask;
        });

    [Fact(Timeout = 60_000)]
    public Task FinishRole_Flushes_Cached_Removal_To_Old_Run_And_Does_Not_Contaminate_New_Run() =>
        WithWindowAsync(async ui =>
        {
            MainWindow.RunState oldState = ui.State;
            AcceptanceLog oldLog = oldState.Run.Log;
            PendingApproval old = ui.Enqueue();
            ui.Refresh();
            ui.Select(old.Snapshot);
            Assert.Single(LogLines(oldLog, ObservedPrefix));
            Assert.Same(oldState, Field<MainWindow.RunState?>(ui.Window, "_displayedApprovalOwner"));
            Assert.True(ui.Named<Button>("ApproveButton").IsEnabled);

            oldState.Stop();
            Assert.Equal(LocalApprovalOutcome.Cancelled, (await old.Decision.WaitAsync(TestTimeout)).Outcome);
            Assert.Single(Field<IReadOnlyList<LocalApprovalSnapshot>>(ui.Window, "_displayedApprovals"));
            Assert.Empty(LogLines(oldLog, RemovedPrefix));
            // 只模拟已经返回的角色任务；不调角色 worker，也不由测试先刷新列表。
            PrivateField("_runTask").SetValue(ui.Window, Task.FromResult(AcceptanceOutcome.PreconditionUnmet));
            Invoke(ui.Window, "FinishRole");

            Assert.Null(Field<MainWindow.RunState?>(ui.Window, "_activeRun"));
            Assert.Null(Field<Task?>(ui.Window, "_runTask"));
            Assert.Null(Field<Task?>(ui.Window, "_runCancellationTask"));
            Assert.Null(Field<CancellationTokenSource?>(ui.Window, "_runCts"));
            Assert.Null(Field<MainWindow.RunState?>(ui.Window, "_displayedApprovalOwner"));
            Assert.Empty(Field<IReadOnlyList<LocalApprovalSnapshot>>(ui.Window, "_displayedApprovals"));
            AssertQueue(ui, 0);
            AssertNoSelection(ui);
            string removed = Assert.Single(LogLines(oldLog, RemovedPrefix));
            AssertCorrelation(removed, old.Snapshot);
            Assert.Contains(removed, File.ReadAllLines(oldLog.FilePath!));
            Assert.Empty(LogLines(Field<AcceptanceLog>(ui.Window, "_processLog"), RemovedPrefix));
            string completedLog = oldLog.All;

            ui.Refresh();
            ui.StartHost();
            MainWindow.RunState nextState = ui.State;
            Assert.NotSame(oldState, nextState);
            Assert.NotEqual(oldState.Run.RunId, nextState.Run.RunId);
            Assert.Null(Field<Task?>(ui.Window, "_runTask"));
            ui.Refresh();
            AssertQueue(ui, 0);
            AssertNoSelection(ui);
            Assert.Empty(ui.Lines(ObservedPrefix));
            Assert.Empty(ui.Lines(RemovedPrefix));

            DateTimeOffset before = DateTimeOffset.UtcNow;
            PendingApproval next = ui.Enqueue(old.Snapshot.Request with
            {
                SessionId = Guid.NewGuid(),
                ConnectionId = Guid.NewGuid(),
            });
            ui.Refresh();
            ui.Refresh();
            Assert.NotEqual(old.Snapshot.Generation, next.Snapshot.Generation);
            Assert.Same(nextState, Field<MainWindow.RunState?>(ui.Window, "_displayedApprovalOwner"));
            AssertObserved(ui, next.Snapshot, before);
            Assert.Single(ui.Lines(ObservedPrefix));
            Assert.Empty(ui.Lines(RemovedPrefix));
            Assert.Equal(completedLog, oldLog.All);
            AssertQueue(ui, 1);
            AssertNoSelection(ui);
            Assert.False(next.Decision.IsCompleted);
        });

    [Fact(Timeout = 60_000)]
    public Task StopCurrentRun_Synchronously_Clears_Cached_Approvals_And_Logs_Removal_Once() =>
        WithWindowAsync(async ui =>
        {
            MainWindow.RunState state = ui.State;
            PendingApproval pending = ui.Enqueue();
            ui.Refresh();
            ui.Select(pending.Snapshot);
            Assert.Same(state, Field<MainWindow.RunState?>(ui.Window, "_displayedApprovalOwner"));
            Assert.True(ui.Named<Button>("ApproveButton").IsEnabled);
            Assert.Empty(ui.Lines(RemovedPrefix));

            Invoke(ui.Window, "StopCurrentRun", "测试停止审批，不启动网络角色");
            // 在任何 await 或额外 Refresh 之前验证同步清零，不让定时器补做收尾。
            Assert.Same(state, ui.State);
            Assert.True(state.IsStopped);
            Assert.True(state.Run.AbortedByOperator);
            Assert.Empty(state.Inbox!.GetSnapshot());
            Assert.Empty(Field<IReadOnlyList<LocalApprovalSnapshot>>(ui.Window, "_displayedApprovals"));
            AssertQueue(ui, 0);
            AssertNoSelection(ui);
            string removed = Assert.Single(ui.Lines(RemovedPrefix));
            AssertCorrelation(removed, pending.Snapshot);
            Assert.Contains(removed, File.ReadAllLines(state.Run.Log.FilePath!));
            Assert.Empty(ui.Lines(SubmittedPrefix));
            Assert.Equal(LocalApprovalOutcome.Cancelled, (await pending.Decision.WaitAsync(TestTimeout)).Outcome);
            Task cancellation = Assert.IsAssignableFrom<Task>(Field<Task?>(ui.Window, "_runCancellationTask"));
            await cancellation.WaitAsync(TestTimeout);
            Assert.True(state.Cts.IsCancellationRequested);
            ui.Refresh();
            ui.Refresh();
            Assert.Single(ui.Lines(RemovedPrefix));
            AssertQueue(ui, 0);
            AssertNoSelection(ui);
        });

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public Task Minimum_Content_Layout_Keeps_Approval_Panel_Outside_Role_ScrollViewer_And_Buttons_In_Bounds(
        bool showResult) => WithWindowAsync(ui =>
        {
            ResourceDictionary applicationResources = LoadApplicationResources();
            ui.Window.Resources.MergedDictionaries.Add(applicationResources);
            Style buttonStyle = Assert.IsType<Style>(applicationResources[typeof(Button)]);
            Style groupBoxStyle = Assert.IsType<Style>(applicationResources[typeof(GroupBox)]);
            string longClientName = new('测', 64);
            for (int i = 0; i < ui.Inbox.Capacity; i++)
            {
                ui.Enqueue(Request() with { ClientName = longClientName });
            }
            ui.Refresh();
            Assert.All(ui.List.Items.Cast<LocalApprovalSnapshot>(), snapshot =>
                Assert.Equal(longClientName, snapshot.Request.ClientName));
            if (showResult)
            {
                Invoke(ui.Window, "ShowResult", AcceptanceOutcome.PreconditionUnmet,
                    "离屏布局测试：本地结论不能代替两机认证证据。");
            }
            ui.Window.Width = 820;
            ui.Window.Height = 700;
            Assert.Equal(820d, ui.Window.MinWidth);
            Assert.Equal(700d, ui.Window.MinHeight);
            Grid root = Assert.IsType<Grid>(ui.Window.Content);
            GroupBox approval = ui.Named<GroupBox>("ApprovalPanel");
            ScrollViewer roles = Assert.Single(root.Children.OfType<ScrollViewer>(), item => Grid.GetRow(item) == 1);
            Assert.Contains(approval, root.Children.Cast<UIElement>());
            Assert.Same(root, LogicalTreeHelper.GetParent(approval));
            Assert.Equal(2, Grid.GetRow(approval));
            Assert.DoesNotContain(LogicalAncestors(approval), ancestor => ancestor is ScrollViewer);
            Assert.DoesNotContain(LogicalAncestors(approval), ancestor => ReferenceEquals(ancestor, roles));

            Button[] buttons = [ui.Named<Button>("ApproveButton"), ui.Named<Button>("ViewOnlyButton"),
                ui.Named<Button>("RejectButton")];
            Panel buttonPanel = Assert.IsAssignableFrom<Panel>(LogicalTreeHelper.GetParent(buttons[0]));
            Assert.All(buttons, button => Assert.Same(buttonPanel, LogicalTreeHelper.GetParent(button)));
            Assert.Contains(approval, LogicalAncestors(buttonPanel));

            // 仅布局脱离 Window 的内容树；不 Show、不创建 Hwnd、不触发 Window.Loaded/身份检查。
            // 从最小 820×700 预留非客户区，按 800×640 保守客户区预算布局；不声称 DPI/真屏验证。
            // 脱离后保留同一份窗口资源，防止真实应用样式随祖先断开而失效。
            ui.Window.Content = null;
            root.Resources.MergedDictionaries.Add(ui.Window.Resources);
            const double clientWidth = 800;
            const double clientHeight = 640;
            root.Measure(new Size(clientWidth, clientHeight));
            root.Arrange(new Rect(0, 0, clientWidth, clientHeight));
            root.UpdateLayout();
            Assert.True(root.IsMeasureValid);
            Assert.True(root.IsArrangeValid);
            Assert.True(root.ActualWidth > 0 && root.ActualHeight > 0);
            Assert.Same(groupBoxStyle, approval.Style);
            Assert.Equal(new Thickness(8), approval.Padding);
            Assert.All(buttons, button =>
            {
                Assert.Same(buttonStyle, button.Style);
                Assert.Equal(new Thickness(12, 6, 12, 6), button.Padding);
                Assert.Equal(96d, button.MinWidth);
            });
            Assert.Contains(VisualDescendants(ui.List).OfType<TextBlock>(), text =>
                text.Inlines.OfType<System.Windows.Documents.Run>().Any(run => run.Text == longClientName));
            Rect contentBounds = new(0, 0, clientWidth - root.Margin.Left - root.Margin.Right,
                clientHeight - root.Margin.Top - root.Margin.Bottom);
            Assert.True(root.ActualWidth <= contentBounds.Width + 0.5 && root.ActualHeight <= contentBounds.Height + 0.5,
                "不能通过把内容树撑出离屏预算来满足按钮边界断言。");
            AssertWithin(root, approval, contentBounds);
            AssertWithin(root, buttonPanel, contentBounds);
            AssertWithin(approval, buttonPanel, new Rect(new Point(0, 0), approval.RenderSize));
            Rect panelBounds = new(new Point(0, 0), buttonPanel.RenderSize);
            foreach (Button button in buttons)
            {
                AssertWithin(root, button, contentBounds);
                AssertWithin(buttonPanel, button, panelBounds);
                Assert.DoesNotContain(VisualAncestors(button), ancestor => ReferenceEquals(ancestor, roles));
            }
            Assert.Null(PresentationSource.FromVisual(root));
            AssertNoNativeWindow(ui.Window);
            Assert.Null(Field<Task?>(ui.Window, "_busyTask"));
            Assert.Null(ui.State.GetHostContext());
            return Task.CompletedTask;
        });

    private static ResourceDictionary LoadApplicationResources()
    {
        string path = Path.Combine(FindRepoRoot(), "tools", "LanRemote.Acceptance", "App.xaml");
        XDocument document = XDocument.Load(path);
        XElement application = Assert.IsType<XElement>(document.Root);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        Assert.Equal(presentation + "Application", application.Name);
        XElement resources = Assert.Single(application.Elements(presentation + "Application.Resources"));
        // 只把资源子节点交给 XamlReader；不解析 Application 本身及其 x:Class/启动行为。
        XElement dictionary = new(presentation + "ResourceDictionary",
            application.Attributes().Where(attribute => attribute.IsNamespaceDeclaration), resources.Elements());
        Application? existingApplication = Application.Current;
        using var reader = dictionary.CreateReader();
        ResourceDictionary result = Assert.IsType<ResourceDictionary>(XamlReader.Load(reader));
        Assert.Same(existingApplication, Application.Current);
        return result;
    }

    private static string FindRepoRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "tools", "LanRemote.Acceptance", "App.xaml")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("无法从测试输出目录向上定位包含验收器 App.xaml 的源码仓库。");
    }

    private static async Task WithWindowAsync(Func<ApprovalWindow, Task> test)
    {
        using AcceptanceTestDirectory directory = new();
        await using StaDispatcherFixture sta = new();
        Dispatcher dispatcher = await sta.Ready.WaitAsync(TestTimeout);
        await dispatcher.InvokeAsync(async () =>
        {
            MainWindow window = new(directory.Path);
            await using ApprovalWindow ui = new(window, directory.Path);
            Field<DispatcherTimer>(window, "_uiTimer").Stop();
            ui.StartHost();
            AssertNoNativeWindow(window);
            await test(ui);
            AssertNoNativeWindow(window);
        }).Task.Unwrap().WaitAsync(TestTimeout + CleanupTimeout + TestTimeout);
    }

    private static LocalApprovalRequest Request(SessionPermission permission = SessionPermission.Control) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), IPAddress.Loopback, 43210,
        Guid.NewGuid(), "审批界面测试客户端", permission, "A1B2C3", DateTimeOffset.UtcNow.AddMinutes(5));

    private static void AssertQueue(ApprovalWindow ui, int count)
    {
        Assert.Equal(count, ui.List.Items.Count);
        if (count == 0) { Assert.Contains("无待批", ui.Queue.Text); }
        else
        {
            Assert.Matches($@"待审批\s*{count}\s*条", ui.Queue.Text);
            Assert.DoesNotContain("无待批", ui.Queue.Text);
            if (ui.List.SelectedItem is null) { Assert.Contains("请先选中", ui.Queue.Text); }
        }
    }

    private static void AssertNoSelection(ApprovalWindow ui)
    {
        Assert.Null(ui.List.SelectedItem);
        Assert.Equal(-1, ui.List.SelectedIndex);
        foreach (string name in new[] { "ApproveButton", "ViewOnlyButton", "RejectButton" })
        {
            Assert.False(ui.Named<Button>(name).IsEnabled);
        }
    }

    private static void AssertNoNativeWindow(MainWindow window)
    {
        Assert.False(window.IsVisible);
        Assert.False(window.IsLoaded);
        Assert.Equal(IntPtr.Zero, new WindowInteropHelper(window).Handle);
        Assert.Null(PresentationSource.FromVisual(window));
    }

    private static string ClickAndObserveBeforeUiUpdate(ApprovalWindow ui, string buttonName)
    {
        object? selected = ui.List.SelectedItem;
        object? source = ui.List.ItemsSource;
        string queue = ui.Queue.Text;
        string status = ui.Status.Text;
        List<SubmissionObservation> observed = new();
        void OnLine(string line)
        {
            if (line.StartsWith(SubmittedPrefix, StringComparison.Ordinal))
            {
                // 在真实日志回调中取值，回调之外再断言，避免断言异常被产品错误处理吞掉。
                observed.Add(new(line, ui.List.SelectedItem, ui.List.ItemsSource, ui.Queue.Text, ui.Status.Text));
            }
        }
        ui.State.Run.Log.LineWritten += OnLine;
        try
        {
            Button button = ui.Named<Button>(buttonName);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
        }
        finally { ui.State.Run.Log.LineWritten -= OnLine; }
        SubmissionObservation atLog = Assert.Single(observed);
        Assert.Same(selected, atLog.Selected);
        Assert.Same(source, atLog.ItemsSource);
        Assert.Equal(queue, atLog.Queue);
        Assert.Equal(status, atLog.Status);
        return atLog.Line;
    }

    private static void AssertSubmission(string line, LocalApprovalSnapshot? selected, bool submitted,
        LocalApprovalOutcome outcome, SessionPermission? grant)
    {
        Assert.Equal(submitted.ToString(), LogField(line, "submitted"));
        Assert.Equal(selected?.RequestId.ToString() ?? "-", LogField(line, "requestId"));
        Assert.Equal(selected?.Generation.ToString() ?? "-", LogField(line, "generation"));
        Assert.Equal(selected?.Request.SessionId.ToString() ?? "-", LogField(line, "sessionId"));
        Assert.Equal(selected?.Request.ConnectionId.ToString() ?? "-", LogField(line, "connectionId"));
        Assert.Equal(outcome.ToString(), LogField(line, "decision"));
        Assert.Equal(grant?.ToString() ?? "-", LogField(line, "grant"));
    }

    private static void AssertObserved(ApprovalWindow ui, LocalApprovalSnapshot snapshot, DateTimeOffset before)
    {
        string line = Assert.Single(ui.Lines(ObservedPrefix), value =>
            LogField(value, "requestId") == snapshot.RequestId.ToString()
            && LogField(value, "generation") == snapshot.Generation.ToString());
        AssertCorrelation(line, snapshot);
        DateTimeOffset observed = DateTimeOffset.Parse(LogField(line, "utc"), CultureInfo.InvariantCulture);
        Assert.Equal(TimeSpan.Zero, observed.Offset);
        Assert.InRange(observed, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
        DateTimeOffset expires = DateTimeOffset.Parse(LogField(line, "expiresUtc"), CultureInfo.InvariantCulture);
        Assert.Equal(TimeSpan.Zero, expires.Offset);
        Assert.Equal(snapshot.Request.ExpiresAt, expires);
        Assert.Contains("拉取", line);
        Assert.Matches("(不|未|非|不能).*(可见|看见|看到)", line);
    }

    private static void AssertCorrelation(string line, LocalApprovalSnapshot snapshot)
    {
        Assert.Equal(snapshot.RequestId.ToString(), LogField(line, "requestId"));
        Assert.Equal(snapshot.Generation.ToString(), LogField(line, "generation"));
        Assert.Equal(snapshot.Request.SessionId.ToString(), LogField(line, "sessionId"));
        Assert.Equal(snapshot.Request.ConnectionId.ToString(), LogField(line, "connectionId"));
    }

    private static string[] LogLines(AcceptanceLog log, string prefix) => log.ReadFrom(0, int.MaxValue)
        .Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).ToArray();

    private static string LogField(string line, string name)
    {
        Match match = Assert.Single(Regex.Matches(line, $@"(?:^|\s){Regex.Escape(name)}=([^\s]+)").Cast<Match>());
        return match.Groups[1].Value;
    }

    private static IEnumerable<DependencyObject> LogicalAncestors(DependencyObject element)
    {
        for (DependencyObject? parent = LogicalTreeHelper.GetParent(element); parent is not null;
            parent = LogicalTreeHelper.GetParent(parent))
        {
            yield return parent;
        }
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject element)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(element, i);
            yield return child;
            foreach (DependencyObject descendant in VisualDescendants(child)) { yield return descendant; }
        }
    }

    private static IEnumerable<DependencyObject> VisualAncestors(DependencyObject element)
    {
        for (DependencyObject? parent = VisualTreeHelper.GetParent(element); parent is not null;
            parent = VisualTreeHelper.GetParent(parent))
        {
            yield return parent;
        }
    }

    private static void AssertWithin(Visual ancestor, FrameworkElement element, Rect bounds)
    {
        Assert.Equal(Visibility.Visible, element.Visibility);
        Assert.True(element.ActualWidth > 0 && element.ActualHeight > 0,
            $"{element.Name} 必须实际参与离屏布局，不能用零面积通过边界断言。");
        Rect actual = element.TransformToAncestor(ancestor).TransformBounds(new Rect(new Point(0, 0), element.RenderSize));
        const double tolerance = 0.5;
        Assert.True(actual.Left >= bounds.Left - tolerance && actual.Top >= bounds.Top - tolerance
            && actual.Right <= bounds.Right + tolerance && actual.Bottom <= bounds.Bottom + tolerance,
            $"{element.Name} 的离屏矩形 {actual} 超出内容矩形 {bounds}。");
    }

    private static FieldInfo PrivateField(string name) => typeof(MainWindow).GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingFieldException(typeof(MainWindow).FullName, name);

    private static T Field<T>(MainWindow window, string name) => (T)PrivateField(name).GetValue(window)!;

    private static object? Invoke(MainWindow window, string name, params object[] arguments) =>
        (typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainWindow).FullName, name)).Invoke(window, arguments);

    private sealed record PendingApproval(LocalApprovalSnapshot Snapshot, Task<LocalApprovalDecision> Decision);
    private sealed record SubmissionObservation(string Line, object? Selected, object? ItemsSource, string Queue, string Status);

    private sealed class ApprovalWindow(MainWindow window, string directory) : IAsyncDisposable
    {
        private readonly List<Task<LocalApprovalDecision>> _decisions = new();
        public MainWindow Window { get; } = window;
        public string Directory { get; } = directory;
        public MainWindow.RunState State => Field<MainWindow.RunState>(Window, "_activeRun");
        public LocalApprovalInbox Inbox => Assert.IsType<LocalApprovalInbox>(State.Inbox);
        public ListBox List => Named<ListBox>("ApprovalList");
        public TextBlock Queue => Named<TextBlock>("ApprovalQueueText");
        public TextBlock Status => Named<TextBlock>("ApprovalStatusText");
        public T Named<T>(string name) where T : class => Assert.IsType<T>(Window.FindName(name));
        public void StartHost() => Assert.IsType<MainWindow.RunState>(Invoke(Window, "StartRole", true));
        public void Refresh() => Invoke(Window, "RefreshApprovalList");
        public string[] Lines(string prefix) => LogLines(State.Run.Log, prefix);

        public PendingApproval Enqueue(LocalApprovalRequest? request = null, CancellationToken token = default)
        {
            request ??= Request();
            Task<LocalApprovalDecision> decision = Inbox.RequestApprovalAsync(request, token).AsTask();
            _decisions.Add(decision);
            Assert.False(decision.IsCompleted);
            LocalApprovalSnapshot snapshot = Assert.Single(Inbox.GetSnapshot(), item => item.RequestId == request.RequestId);
            return new(snapshot, decision);
        }

        public void Select(LocalApprovalSnapshot snapshot)
        {
            List.SelectedItem = Assert.Single(List.Items.Cast<LocalApprovalSnapshot>(), item =>
                item.RequestId == snapshot.RequestId && item.Generation == snapshot.Generation);
            Assert.Equal(snapshot.Generation, Assert.IsType<LocalApprovalSnapshot>(List.SelectedItem).Generation);
        }

        public async ValueTask DisposeAsync()
        {
            Field<DispatcherTimer>(Window, "_uiTimer").Stop();
            MainWindow.RunState? state = Field<MainWindow.RunState?>(Window, "_activeRun");
            CancellationTokenSource? cts = Field<CancellationTokenSource?>(Window, "_runCts");
            try
            {
                state?.Stop();
                await Task.WhenAll(_decisions).WaitAsync(CleanupTimeout);
                if (Field<Task?>(Window, "_runCancellationTask") is { } cancellation)
                {
                    await cancellation.WaitAsync(CleanupTimeout);
                }
                if (Field<Task?>(Window, "_runTask") is { } completedRole)
                {
                    await completedRole.WaitAsync(CleanupTimeout);
                }
            }
            finally
            {
                // 没有 worker；回收审批、取消和模拟完成任务后摘除拥有权，否则 Closing 会阻止关窗。
                PrivateField("_activeRun").SetValue(Window, null);
                PrivateField("_runCts").SetValue(Window, null);
                PrivateField("_runTask").SetValue(Window, null);
                PrivateField("_runCancellationTask").SetValue(Window, null);
                try { state?.Dispose(); }
                finally
                {
                    cts?.Dispose();
                    Window.Close();
                }
            }
        }
    }
}
