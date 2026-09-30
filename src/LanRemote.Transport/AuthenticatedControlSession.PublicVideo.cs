using System.Buffers;
using LanRemote.Core.Models;

namespace LanRemote.Transport;

public sealed partial class AuthenticatedControlSession
{
    // 单个已接受 attempt 的投影追踪槽，不是重复调用的幂等缓存。
    private Task<AuthenticatedVideoSession>? _publicAttachTask;
    private Task? _publicDisposeTask;

    /// <summary>
    /// 使用控制会话冻结的目标和身份附着独立视频 TLS，同时启用唯一 Control 断连读取。
    /// 本地最多尝试一次；资格/重复拒绝同步抛出，失败不恢复尝试资格。
    /// </summary>
    public Task<AuthenticatedVideoSession> AttachVideoAsync(CancellationToken cancellationToken = default) =>
        BeginPublicVideoAttach(static (target, timeouts, clock, token) =>
            new TlsClientConnector().ConnectAsync(target, timeouts, clock, token), cancellationToken);

    internal Task<AuthenticatedVideoSession> AttachPublicVideoForTestingAsync(
        Func<ConnectionTarget, TransportTimeouts, TimeProvider, CancellationToken, Task<TlsConnection>> connectTls,
        CancellationToken cancellationToken = default,
        Func<int, IMemoryOwner<byte>>? rent = null,
        Action<EncodedFrame?>? frameRead = null,
        TaskScheduler? projectionScheduler = null) =>
        BeginPublicVideoAttach(connectTls, cancellationToken, rent, frameRead, projectionScheduler);

    private Task<AuthenticatedVideoSession> BeginPublicVideoAttach(
        Func<ConnectionTarget, TransportTimeouts, TimeProvider, CancellationToken, Task<TlsConnection>> connectTls,
        CancellationToken cancellationToken,
        Func<int, IMemoryOwner<byte>>? rent = null,
        Action<EncodedFrame?>? frameRead = null,
        TaskScheduler? projectionScheduler = null)
    {
        ArgumentNullException.ThrowIfNull(connectTls);
        lock (_gate)
        {
            // 失败直接离开，绝不能清理或覆盖其他 attempt（包括 clock 重入的赢家）。
            Task<ClientVideoLifetime> attach = AttachVideoCoreAsync(
                connectTls, monitorControl: true, cancellationToken, rent, frameRead);
            ClientVideoLifetime child = _videoLifetime!;
            // 先创建未启动的调度屏障和真正 await 它的投影，再发布。自定义调度器可同步重入。
            Task scheduled = new(static () => { }, CancellationToken.None, TaskCreationOptions.DenyChildAttach);
            Task<AuthenticatedVideoSession> projection = ProjectVideoAsync(scheduled, attach, child);
            _publicAttachTask = projection;
            try { scheduled.Start(projectionScheduler ?? TaskScheduler.Default); }
            catch (TaskSchedulerException)
            {
                // Task.Start 已将原调度错误写入 scheduled；投影负责保存原树、停止并排空自己的 attempt。
                // 不重新同步抛出，避免已接受 attempt 丢失 public 观察者。
            }
            return projection;
        }
    }

    private static async Task<AuthenticatedVideoSession> ProjectVideoAsync(
        Task scheduled, Task<ClientVideoLifetime> attach, ClientVideoLifetime child)
    {
        try { await scheduled.ConfigureAwait(false); }
        catch (Exception schedulingError)
        {
            Task stopped = child.StopAndJoinAsync();
            Task complete = Task.WhenAll(attach, stopped);
            Exception? observed = null;
            try { await complete.ConfigureAwait(false); }
            catch (Exception error) { observed = error; }
            List<Exception> roots = [];
            foreach (Exception error in child.LifetimeErrors) AddVideoFailureRoot(roots, error);
            if (scheduled.Exception is { } schedulingContainer)
                foreach (Exception error in schedulingContainer.InnerExceptions) AddVideoFailureRoot(roots, error);
            else AddVideoFailureRoot(roots, schedulingError);
            if (complete.Exception is { } container)
                foreach (Exception error in container.InnerExceptions) AddVideoFailureRoot(roots, error);
            else if (observed is not null) AddVideoFailureRoot(roots, observed);
            throw new AggregateException("视频交付调度及收尾失败。", roots);
        }
        ClientVideoLifetime delivered = await attach.ConfigureAwait(false);
        // 对象在 child 发布前预建；底座已提交后不新增分配、观察者、取消或父状态终检。
        return delivered.PublicSession;
    }

    /// <summary>
    /// 撤销并完整排空 Control、视频、原监控读取和已接受的 public 附着投影。
    /// 重复调用共享报告任务。排空后以 AggregateException 报告全部原始诊断，不过滤主动关闭的 I/O 错误。
    /// 不可从被跟踪的操作或回调内等待本方法。同步 Dispose 的既有合同不变。
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_publicDisposeTask is not null) return new(_publicDisposeTask);
            Task rawJoin = CloseAndJoinAsync();
            Task complete = _publicAttachTask is { } attach ? Task.WhenAll(rawJoin, attach) : rawJoin;
            return new(_publicDisposeTask = ReportDisposalAsync(complete, () => LifetimeErrors));
        }
    }

    private static async Task ReportDisposalAsync(Task complete, Func<IReadOnlyList<Exception>> getErrors)
    {
        Exception? observed = null;
        try { await complete.ConfigureAwait(false); }
        catch (Exception error) { observed = error; }

        // 仅在完整退出后取得稳定快照。各观察者独立报告，不消费其他观察者的错误。
        List<Exception> roots = [];
        foreach (Exception error in getErrors()) AddVideoFailureRoot(roots, error);
        if (complete.Exception is { } container)
        {
            // 只去掉 Task 自动包装的一层；原 Aggregate 的嵌套、兄弟和重复引用全保留。
            foreach (Exception error in container.InnerExceptions) AddVideoFailureRoot(roots, error);
        }
        else if (observed is not null) AddVideoFailureRoot(roots, observed);
        if (roots.Count != 0) throw new AggregateException("会话已排空，但存在操作或清理错误。", roots);
    }

    internal sealed partial class ClientVideoLifetime
    {
        private Task? _publicDisposeTask;
        internal AuthenticatedVideoSession PublicSession { get; }

        internal Task GetPublicDisposeTask()
        {
            lock (Gate)
            {
                // 单向依赖：子只等自身 raw join，不等父报告或 public attach 投影。
                return _publicDisposeTask ??= ReportDisposalAsync(StopAndJoinAsync(), () => LifetimeErrors);
            }
        }
    }
}
