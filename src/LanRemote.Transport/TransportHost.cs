using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using LanRemote.Core.Abstractions;

namespace LanRemote.Transport;

/// <summary>
/// 被控端监听 Host：每个本地地址一个 listener，accept → 同子网 → 准入 → TLS。
/// </summary>
/// <remarks>
/// <para><b>顺序不可调换</b>（评审 A-6 / A-7）：
/// <c>accept</c> → <b>同子网校验</b> → <b>准入限额</b> → <b>TLS 握手</b>。
/// 把 TLS 提到最前面等于让任意远端都能逼我们做握手；把子网校验放到 TLS 之后
/// 等于先给了对方一次握手的机会再决定要不要认识它。</para>
/// <para><b>ADR-031：启动按网卡降级</b>。某个地址 bind 失败只记进
/// <see cref="TransportHostStartResult.Failures"/>，不影响其它地址。</para>
/// <para><b>关于 <c>ExclusiveAddressUse</c>：不要过度声称</b>。完整实测矩阵见
/// <c>MultiAddressListenTests</c>（本机 Win11 25H2 / 26200）：</para>
/// <list type="bullet">
/// <item><description>同端口 + 两个不同具体地址：开不开都<b>可以</b> bind（本类型靠这条工作）；</description></item>
/// <item><description>同地址同端口第二次：开不开都<b>被拒</b>（<see cref="SocketError.AddressAlreadyInUse"/>）；</description></item>
/// <item><description>别人先占更宽地址（<c>0.0.0.0</c>）且未开 exclusive：我们<b>仍会 bind 成功</b>——
/// <b>开 exclusive 也发现不了这种情况</b>；不过实测连到具体地址的连接会交给<b>更具体</b>的 listener，
/// 流量不会被更宽的 socket 截走；</description></item>
/// <item><description>带 <c>SO_REUSEADDR</c> 的后来者抢端口：开不开都<b>被拒</b>
/// （<see cref="SocketError.AccessDenied"/>）。</description></item>
/// </list>
/// <para>结论：<b>在本机可观测范围内它没有带来差别</b>。仍然保留 <c>true</c> 只作为
/// 对「<c>SO_REUSEADDR</c> 语义更宽松的旧版 Windows」的防御，
/// 不要把它当成「能发现端口已被占用」的手段——真正的冲突信号是
/// <see cref="SocketError.AddressAlreadyInUse"/>，它已记进
/// <see cref="TransportHostStartResult.Failures"/>。</para>
/// </remarks>
public sealed class TransportHost : IAsyncDisposable
{
    private readonly IReadOnlyList<IPAddress> _localAddresses;
    private readonly ISubnetPolicy _subnetPolicy;
    private readonly X509Certificate2 _serverCertificate;
    private readonly byte[] _serverCertificateSha256;
    private readonly Func<AcceptedConnection, CancellationToken, Task> _sessionHandler;
    private readonly TransportHostOptions _options;
    private readonly ConnectionAdmissionLimiter _limiter;
    private readonly ConnectionRegistry _registry = new();
    private readonly List<TcpListener> _listeners = new();
    private readonly List<Task> _acceptLoops = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lifetimeGate = new();
    private readonly HashSet<Task<TransportHostStopReport>> _pendingStops = new();

    private int _started;
    private bool _stopping;
    private bool _disposed;
    private Task? _stopCancellationTask;
    private Task? _disposeTask;

    /// <summary>构造 Host。</summary>
    /// <param name="localAddresses">要监听的本地 IPv4 地址（每张合格网卡一个）。</param>
    /// <param name="subnetPolicy">同子网策略；用<b>接受连接的那个本地地址</b>去校验。</param>
    /// <param name="serverCertificate">服务端证书（带私钥）。</param>
    /// <param name="sessionHandler">TLS 完成后的会话处理器；由它决定应用层做什么。</param>
    /// <param name="options">可调参数。</param>
    public TransportHost(
        IReadOnlyList<IPAddress> localAddresses,
        ISubnetPolicy subnetPolicy,
        X509Certificate2 serverCertificate,
        Func<AcceptedConnection, CancellationToken, Task> sessionHandler,
        TransportHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(localAddresses);
        ArgumentNullException.ThrowIfNull(subnetPolicy);
        ArgumentNullException.ThrowIfNull(serverCertificate);
        ArgumentNullException.ThrowIfNull(sessionHandler);

        _localAddresses = localAddresses;
        _subnetPolicy = subnetPolicy;
        _serverCertificate = serverCertificate;

        // 本机出示证书的指纹在构造期算一次、全生命周期冻结（ADR-037 第 3 条）：
        // 它是 challenge `certSha256` 的唯一来源——「challenge 声称的指纹 = 本连接实际出示的证书」。
        _serverCertificateSha256 = CertificatePin.Compute(serverCertificate);

        _sessionHandler = sessionHandler;
        _options = options ?? new TransportHostOptions();
        if (_options.ShutdownTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), _options.ShutdownTimeout,
                "停机预算必须为正。");
        }

        _limiter = new ConnectionAdmissionLimiter(
            _options.MaxConnections,
            _options.MaxConnectionsPerAddress);
    }

    /// <summary>显式内部装配 M5 双通道；既有 public handler 与 Control-only 入口不变。</summary>
    internal static TransportHost CreateWithChannelRouter(
        IReadOnlyList<IPAddress> localAddresses,
        ISubnetPolicy subnetPolicy,
        X509Certificate2 serverCertificate,
        ControlAuthContext authContext,
        IVideoFrameSource videoFrames,
        VideoSessionOptions? videoOptions = null,
        TransportHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(authContext);
        ArgumentNullException.ThrowIfNull(videoFrames);
        TransportHostOptions hostOptions = options ?? new TransportHostOptions();
        return new TransportHost(localAddresses, subnetPolicy, serverCertificate,
            (connection, token) => new FirstFrameRouter(
                authContext, hostOptions.Timeouts, videoFrames, videoOptions).RunAsync(connection, token),
            hostOptions);
    }

    /// <summary>装配认证后的单会话视频生产者；工厂为借用对象，不接触流或会话凭据。</summary>
    public static TransportHost CreateWithVideo(
        IReadOnlyList<IPAddress> localAddresses,
        ISubnetPolicy subnetPolicy,
        X509Certificate2 serverCertificate,
        ControlAuthContext authContext,
        IVideoFrameProducerFactory videoFactory,
        TransportHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(authContext);
        ArgumentNullException.ThrowIfNull(videoFactory);
        TransportHostOptions hostOptions = options ?? new TransportHostOptions();
        return new TransportHost(localAddresses, subnetPolicy, serverCertificate,
            (connection, token) => new FirstFrameRouter(
                authContext, hostOptions.Timeouts, videoFactory).RunAsync(connection, token),
            hostOptions);
    }

    /// <summary>是否正在运行。</summary>
    public bool IsRunning => Volatile.Read(ref _started) == 1 &&
        !Volatile.Read(ref _stopping) && !_stop.IsCancellationRequested;

    /// <summary>登记表中的活动连接数。</summary>
    public int ActiveConnections => _registry.Count;

    /// <summary>准入限额当前占用数（应当总是 ≤ <see cref="ActiveConnections"/> 的来源侧）。</summary>
    public int AdmittedConnections => _limiter.GlobalInUse;

    /// <summary>准入限额器（测试用）。</summary>
    public ConnectionAdmissionLimiter Limiter => _limiter;

    /// <summary>连接处理及逐项清理的有界诊断；每个固定类别只保留首个错误。</summary>
    internal HostLifecycleErrors LifecycleErrors { get; } = new();

    /// <summary>
    /// 启动所有 listener 并开始 accept。
    /// </summary>
    /// <returns>启动结果（哪些在听、哪些失败）。</returns>
    /// <remarks>按网卡降级（ADR-031）；一个都没听上<b>不抛异常</b>，由调用方决定如何呈现。</remarks>
    public TransportHostStartResult Start()
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopping)
            {
                throw new InvalidOperationException("TransportHost 已停止，不能启动。");
            }

            if (_started != 0)
            {
                throw new InvalidOperationException("TransportHost 只能启动一次。");
            }

            _started = 1;
            List<IPAddress> bound = new();
            List<TransportHostBindFailure> failures = new();

            foreach (IPAddress address in _localAddresses)
            {
                if (address is null)
                {
                    continue;
                }

                if (address.AddressFamily != AddressFamily.InterNetwork)
                {
                    failures.Add(new TransportHostBindFailure(address, null, "不是 IPv4 地址。"));
                    continue;
                }

                TcpListener listener = new(address, _options.Port)
                {
                    // 见类型说明：不开就会与更宽的 bind 静默共享端口。
                    ExclusiveAddressUse = true,
                };

                try
                {
                    listener.Start(_options.ListenBacklog);
                }
                catch (SocketException ex)
                {
                    failures.Add(new TransportHostBindFailure(address, ex.SocketErrorCode, ex.Message));
                    continue;
                }

                _listeners.Add(listener);
                bound.Add(address);
                _acceptLoops.Add(Task.Run(() => AcceptLoopAsync(listener, address)));
            }

            return new TransportHostStartResult(bound, failures);
        }
    }

    /// <summary>
    /// 停机：停止 accept、取消全部连接、在预算内 join。
    /// </summary>
    /// <param name="timeout">停机预算；为空则用 <see cref="TransportHostOptions.ShutdownTimeout"/>。</param>
    /// <returns>停机报告：accept 循环与连接各自是否在预算内结束、未完成连接数。</returns>
    /// <remarks>
    /// <b>预算超限必须可观测</b>（评审 B18）：<see cref="TransportHostStopReport.AllFinished"/> 为
    /// <see langword="false"/> 时，调用方必须能说出「是 accept 循环没停、还是几条连接没结束」，
    /// 不得与干净成功不可区分。根取消或物理关闭回调仍运行时抛超时异常，
    /// 不以伪造连接数表达第三种未完成阶段。
    /// </remarks>
    public Task<TransportHostStopReport> StopAsync(TimeSpan? timeout = null)
    {
        TimeSpan budget = timeout ?? _options.ShutdownTimeout;
        if (budget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), budget, "停机预算必须为正。");
        }

        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _stopping = true;
            PruneCompletedStops();
            // 根取消的同步回调可能阻塞；不让它占住发布锁或调用方线程。
            Task<TransportHostStopReport> stop = Task.Run(() => StopCoreAsync(budget));
            _pendingStops.Add(stop);
            _ = stop.ContinueWith(completed =>
            {
                lock (_lifetimeGate)
                {
                    if (_pendingStops.Remove(completed))
                    {
                        RecordCompletedStop(completed);
                    }
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return stop;
        }
    }

    private async Task<TransportHostStopReport> StopCoreAsync(TimeSpan budget)
    {
        Stopwatch clock = Stopwatch.StartNew();
        List<Exception> errors = new();
        Task cancellation;
        lock (_lifetimeGate)
        {
            cancellation = _stopCancellationTask ??= Task.Run(() => _stop.Cancel());
        }

        foreach (TcpListener listener in _listeners)
        {
            try
            {
                listener.Stop();
            }
            catch (SocketException)
            {
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }

        // 立即发出连接停机；不能让同步根取消回调或未登记连接收尾挡住物理关闭。
        TimeSpan acceptBudget = TimeSpan.FromTicks(budget.Ticks / 4);
        Task<ConnectionStopReport>? registryStop = null;
        try
        {
            registryStop = _registry.StopAllAsync(budget - acceptBudget);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }

        // accept 仍持有未登记连接的异步关闭，必须单独计入报告与完整 Dispose join。
        bool acceptFinished = true;
        if (_acceptLoops.Count > 0)
        {
            Task accept = Task.WhenAll(_acceptLoops);
            try
            {
                await accept.WaitAsync(acceptBudget).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                acceptFinished = false;
            }
            catch (Exception error)
            {
                if (accept.Exception is { } failure)
                {
                    errors.AddRange(failure.InnerExceptions);
                }
                else
                {
                    errors.Add(error);
                }
            }
        }

        ConnectionStopReport? connections = null;
        if (registryStop is not null)
        {
            try
            {
                // Registry 自身仅等待两段有限预算，永不无限 await 原连接或同步回调。
                connections = await registryStop.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                if (registryStop.Exception is { } failure)
                {
                    errors.AddRange(failure.InnerExceptions);
                }
                else
                {
                    errors.Add(error);
                }
            }
        }

        foreach (TcpListener listener in _listeners)
        {
            try
            {
                listener.Dispose();
            }
            catch (SocketException)
            {
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }

        // Registry/accept 结束不代表 Host 根取消回调已退出；剩余预算内观察原回调。
        TimeSpan remaining = budget - clock.Elapsed;
        if (!cancellation.IsCompleted && remaining > TimeSpan.Zero)
        {
            try
            {
                await cancellation.WaitAsync(remaining).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            catch (Exception)
            {
                // 从原 Task.Exception 读取完整错误树。
            }
        }

        if (!cancellation.IsCompleted)
        {
            errors.Add(new TimeoutException(
                $"Host 停机预算已耗尽：根取消回调仍未退出，" +
                $"accept 循环完成={acceptFinished}，当前登记连接={_registry.Count}；不报告为干净成功。"));
        }
        else if (cancellation.Exception is { } cancellationFailure)
        {
            errors.AddRange(cancellationFailure.InnerExceptions);
        }

        if (!acceptFinished && errors.Count != 0)
        {
            errors.Add(new TimeoutException(
                $"Host 停机预算内 accept 循环仍未结束；当前登记连接={_registry.Count}。"));
        }

        if (errors.Count == 1 && errors[0] is TimeoutException timeout)
        {
            throw timeout;
        }

        if (errors.Count != 0)
        {
            throw new AggregateException("TransportHost 停机清理失败。", errors);
        }

        return new TransportHostStopReport(acceptFinished, connections!.Unfinished);
    }

    /// <summary>完整等待停机、accept 与连接收尾，再释放根取消源；并发调用共享同一任务。</summary>
    public ValueTask DisposeAsync()
    {
        lock (_lifetimeGate)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                _stopping = true;
                PruneCompletedStops();
                Task<TransportHostStopReport>[] pendingStops = _pendingStops.ToArray();
                _disposeTask = Task.Run(() => DisposeCoreAsync(pendingStops));
            }

            return new ValueTask(_disposeTask);
        }
    }

    private void PruneCompletedStops()
    {
        _pendingStops.RemoveWhere(stop =>
        {
            if (!stop.IsCompleted)
            {
                return false;
            }

            RecordCompletedStop(stop);
            return true;
        });
    }

    private void RecordCompletedStop(Task<TransportHostStopReport> stop)
    {
        // 已完成 Stop 的所有原错误仍在各自返回的 Task 上；Host 只留有界诊断，
        // Dispose 不重复汇报历史已完成 Stop，仅完整汇报它启动时尚未结束的 Stop。
        if (stop.Exception is { } failure)
        {
            foreach (Exception error in failure.InnerExceptions)
            {
                LifecycleErrors.Record(HostLifecycleErrorKind.Connection, error);
            }
        }
    }

    private async Task DisposeCoreAsync(Task<TransportHostStopReport>[] pendingStops)
    {
        List<Exception> errors = new();
        try
        {
            await StopCoreAsync(_options.ShutdownTimeout).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }

        // 预算报告可先返回；旧 Stop 必须完整 join，不能由 Count=0 或报告替代。
        try
        {
            await Task.WhenAll(pendingStops).ConfigureAwait(false);
        }
        catch
        {
            foreach (Task<TransportHostStopReport> stop in pendingStops)
            {
                if (stop.Exception is { } failure)
                {
                    errors.AddRange(failure.InnerExceptions);
                }
            }
        }

        Task accept = Task.WhenAll(_acceptLoops);
        try
        {
            await accept.ConfigureAwait(false);
        }
        catch
        {
            if (accept.Exception is { } failure)
            {
                errors.AddRange(failure.InnerExceptions);
            }
        }

        Task? cancellation;
        lock (_lifetimeGate)
        {
            cancellation = _stopCancellationTask;
        }

        if (cancellation is not null)
        {
            try
            {
                await cancellation.ConfigureAwait(false);
            }
            catch
            {
                if (cancellation.Exception is { } failure)
                {
                    errors.AddRange(failure.InnerExceptions);
                }
            }
        }

        // Registry 完整 join 原回调、force 与 linked CTS；故障不阻止独立回收 Host 根。
        try
        {
            await _registry.JoinAndDisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }

        if (cancellation is { IsCompleted: true } && accept.IsCompleted &&
            pendingStops.All(stop => stop.IsCompleted))
        {
            try
            {
                _stop.Dispose();
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }
        else
        {
            errors.Add(new InvalidOperationException(
                "Host 根取消源未释放：原取消、accept 或旧 Stop 尚未完整结束。"));
        }

        if (errors.Count != 0)
        {
            throw new AggregateException("TransportHost 停机清理失败。", errors);
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, IPAddress localAddress)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                // listener.Stop() 会打断 pending accept，这是停机的正常路径。
                break;
            }

            // 登记标记在 HandleAsync 首次 await 前同步发布，不依赖异步 continuation。
            // 未登记的拒绝路径必须由 accept 持有至关闭完成；已登记 TLS 仍可并行处理。
            bool registered = false;
            Task handling = HandleAsync(client, localAddress, () => registered = true);
            await AwaitUnregisteredHandlingAsync(handling, registered).ConfigureAwait(false);
        }
    }

    /// <summary>未登记收尾纳入 accept join；已登记生命周期由 registry 跟踪，但两类任务故障都要观察。</summary>
    internal Task AwaitUnregisteredHandlingAsync(Task handling, bool registered)
    {
        Task observed = ObserveHandlingAsync(handling);
        return registered ? Task.CompletedTask : observed;
    }

    private async Task ObserveHandlingAsync(Task handling)
    {
        try
        {
            await handling.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // 兜住包括 finally 在内的意外故障，不能从 accept 逃逸或留下未观察 Task。
            LifecycleErrors.Record(HostLifecycleErrorKind.Connection, error);
        }
    }

    private async Task HandleAsync(TcpClient client, IPAddress listenerAddress, Action onRegistered)
    {
        AdmissionLease? lease = null;
        ConnectionRegistration? registration = null;
        ConnectionCloseHandle closer = new(client, LifecycleErrors);

        try
        {
            if (client.Client.RemoteEndPoint is not IPEndPoint remoteEndPoint
                || client.Client.LocalEndPoint is not IPEndPoint localEndPoint)
            {
                return;
            }

            // ① 同子网校验：用**接受它的那个**本地地址去查绑定与掩码。
            if (!_subnetPolicy.IsAllowedPeer(localEndPoint.Address, remoteEndPoint.Address))
            {
                return;
            }

            // ② 准入限额：在 TLS 之前占用。
            if (!_limiter.TryAcquire(remoteEndPoint.Address, out lease) || lease is null)
            {
                return;
            }

            // ③ 登记提交与停机发布共用闸门；子网策略及准入逻辑都不能占住此锁。
            lock (_lifetimeGate)
            {
                if (!_stopping && !_disposed)
                {
                    registration = _registry.TryRegister(closer);
                }
            }

            if (registration is null)
            {
                return;
            }

            // 必须保持在任何 await 之前；回调仅由 accept 用于同步标记生命周期的持有方。
            onRegistered();

            // ④ 才是 TLS。
            SslStream stream = new(client.GetStream(), leaveInnerStreamOpen: false);
            closer.Attach(stream);

            using (CancellationTokenSource handshakeCts =
                   CancellationTokenSource.CreateLinkedTokenSource(registration.Cancellation))
            {
                handshakeCts.CancelAfter(_options.Timeouts.HandshakeTimeout);
                await stream.AuthenticateAsServerAsync(CreateServerOptions(), handshakeCts.Token)
                    .ConfigureAwait(false);
            }

            // TLS 完成即冻结本连接的安全事实快照（ADR-037 第 3 条）：
            // 地址 / 端口 / 协商版本 / 本机证书指纹——认证期间的唯一素材来源。
            ConnectionSecurityContext security = new(
                localEndPoint.Address,
                remoteEndPoint.Address,
                remoteEndPoint.Port,
                stream.SslProtocol,
                _serverCertificateSha256);

            AcceptedConnection accepted = new(security, stream, closer);

            try
            {
                await _sessionHandler(accepted, registration.Cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException error) when (
                registration.Cancellation.IsCancellationRequested &&
                error.CancellationToken == registration.Cancellation)
            {
                // 仅接受明确归属于本次 Host 令牌的取消。后来停机不能把来源故障追认为正常取消。
            }
            catch (Exception error)
            {
                // 包括同步抛出与返回故障 Task；不能让 fire-and-forget handler 成为未观察异常。
                LifecycleErrors.Record(HostLifecycleErrorKind.Handler, error);
            }
        }
        catch (OperationCanceledException)
        {
            // 两种来源都会走到这里，当前处理相同（直接断开），但别把注释写成单一来源：
            //   ① 停机取消（registration.Cancellation）；
            //   ② 握手时限到点（handshakeCts）——这是被卡住的<b>未认证</b>连接，属于预期防御。
            // 实测见 PreAuthDeadlineTests：400 ms 时限下 3 条连接 436 ms 内名额全部归还。
            // M3 阶段 4 引入会话状态机后，这两者应当能被区分（可观测性），届时再拆。
        }
        catch (AuthenticationException)
        {
            // 对端握手失败：没有可上报的通道，也不该上报细节。
        }
        catch (IOException)
        {
            // 对端断开发生在任何阶段都可能；不必记录。
        }
        catch (ObjectDisposedException)
        {
            // 停机时 socket 已被强制释放。
        }
        catch (Exception error)
        {
            LifecycleErrors.Record(HostLifecycleErrorKind.Connection, error);
        }
        finally
        {
            await FinishConnectionAsync(closer, lease, registration).ConfigureAwait(false);
        }
    }

    /// <summary>关闭任务完成后才归还名额、最后结束登记；任一项失败都不能阻断后续清理。</summary>
    internal async Task FinishConnectionAsync(
        ConnectionCloseHandle closer,
        IDisposable? admission,
        IDisposable? registration)
    {
        try
        {
            closer.CompleteAttachment();
            await closer.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            LifecycleErrors.Record(HostLifecycleErrorKind.CloseCleanup, error);
        }

        try
        {
            admission?.Dispose();
        }
        catch (Exception error)
        {
            LifecycleErrors.Record(HostLifecycleErrorKind.AdmissionCleanup, error);
        }

        try
        {
            registration?.Dispose();
        }
        catch (Exception error)
        {
            LifecycleErrors.Record(HostLifecycleErrorKind.RegistrationCleanup, error);
        }
    }

    /// <summary>
    /// 服务端 TLS 选项。
    /// </summary>
    /// <remarks>
    /// 单独成一个方法是为了让这些<b>必须显式设置</b>的开关能被测试直接断言
    /// （见 <c>TlsOptionHardeningTests</c>）——否则删掉一行不会有任何东西变红。
    /// </remarks>
    internal SslServerAuthenticationOptions CreateServerOptions()
    {
        return new SslServerAuthenticationOptions
        {
            ServerCertificate = _serverCertificate,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            AllowTlsResume = false,
            AllowRenegotiation = false,
            ClientCertificateRequired = false,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        };
    }
}
