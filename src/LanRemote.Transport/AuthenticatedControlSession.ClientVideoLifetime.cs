using System.Buffers;
using System.Runtime.ExceptionServices;
using LanRemote.Core.Models;

namespace LanRemote.Transport;

public sealed partial class AuthenticatedControlSession
{
    private readonly TlsConnection _ownedConnection;
    private bool _deliveryCommitted;
    private ClientVideoLifetime? _videoLifetime;
    private Task? _closeAndJoin;

    /// <summary>仅在认证窗口完全退出后的最终移交处调用；提交之后不再执行可失败的认证步骤。</summary>
    internal void CommitDelivery(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_connection is null, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_deliveryCommitted) throw new InvalidOperationException("控制会话已经交付。");
            _deliveryCommitted = true;
        }
    }

    /// <summary>
    /// 内部生命周期底座，不是公开视频认证入口。connect 返回前拥有其资源；返回后本层独占。
    /// initialize 借用连接，必须等待自身所有 I/O 完成；真实接线仍须校验第二 TLS pin、proof 和严格 ACK。
    /// 登记即永久消费本地单次尝试；失败不恢复，但不关闭 Control。rent/frameRead 仅为实例级测试接缝。
    /// </summary>
    internal Task<ClientVideoLifetime> StartVideoLifetimeAsync(
        Func<CancellationToken, Task<TlsConnection>> connectOwned,
        Func<TlsConnection, CancellationToken, Task> initializeBorrowed,
        CancellationToken cancellationToken = default,
        Func<int, IMemoryOwner<byte>>? rent = null,
        Action<EncodedFrame?>? frameRead = null,
        TaskScheduler? joinScheduler = null)
    {
        ArgumentNullException.ThrowIfNull(connectOwned);
        ArgumentNullException.ThrowIfNull(initializeBorrowed);
        lock (_gate)
        {
            CheckCanStartVideo(cancellationToken);
            _ = GetRemainingAttachBudget(cancellationToken);
            // TimeProvider 可重入启动另一 attempt；取时后必须重新检查新增状态。
            CheckCanStartVideo(cancellationToken);
            ClientVideoLifetime child = new(this, rent, frameRead, joinScheduler);
            _videoLifetime = child;
            return child.StartUnderGate(connectOwned, initializeBorrowed, cancellationToken);
        }
    }

    private void CheckCanStartVideo(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_connection is null, this);
        token.ThrowIfCancellationRequested();
        if (!_deliveryCommitted) throw new InvalidOperationException("控制认证尚未完成最终交付。");
        if (_videoLifetime is not null) throw new InvalidOperationException("本地视频附着尝试已经使用。");
    }

    /// <summary>
    /// 显式完整收尾。先失效并独立请求已知连接关闭，再等待 child 原操作/回调/迟到资源。
    /// 同一任务完成只代表全部尝试已退出；原始诊断见 LifetimeErrors，不消费 public Dispose 报错权。
    /// 不能从被本层跟踪的操作或回调内同步等待本任务。
    /// </summary>
    internal Task CloseAndJoinAsync()
    {
        lock (_gate)
        {
            if (_closeAndJoin is not null) return _closeAndJoin;
            _ = RevokeCore();
            Task controlClose = _ownedConnection.CloseAsync();
            Task videoJoin = _videoLifetime?.StopAndJoinAsync() ?? Task.CompletedTask;
            return _closeAndJoin = Task.Run(async () =>
            {
                await controlClose.ConfigureAwait(false);
                await videoJoin.ConfigureAwait(false);
            });
        }
    }

    internal IReadOnlyList<Exception> LifetimeErrors
    {
        get
        {
            List<Exception> errors = new();
            AddErrors(errors, _ownedConnection.CleanupErrors);
            lock (_gate)
            {
                if (_videoLifetime is not null) AddErrors(errors, _videoLifetime.LifetimeErrors);
            }
            return errors.AsReadOnly();
        }
    }

    private static void AddErrors(List<Exception> errors, IEnumerable<Exception> candidates)
    {
        foreach (Exception candidate in candidates)
        {
            if (!errors.Any(error => ContainsReference(error, candidate))) errors.Add(candidate);
        }
    }

    private static bool ContainsReference(Exception root, Exception candidate)
    {
        if (ReferenceEquals(root, candidate)) return true;
        if (root is AggregateException aggregate)
            return aggregate.InnerExceptions.Any(error => ContainsReference(error, candidate));
        return root.InnerException is { } inner && ContainsReference(inner, candidate);
    }

    /// <summary>
    /// 一个固定 attach 槽和一个固定 read 槽；与父会话共用交付锁，不公开 reader/Stream。
    /// 每个 worker 在锁内登记，在锁外调用并等待原 Task；关闭不是原操作已退出的证明。
    /// </summary>
    internal sealed partial class ClientVideoLifetime
    {
        private readonly AuthenticatedControlSession _parent;
        private readonly CancellationTokenSource _stopSource = new();
        private readonly Func<int, IMemoryOwner<byte>>? _rent;
        private readonly Action<EncodedFrame?>? _frameRead;
        private readonly TaskScheduler _joinScheduler;
        private readonly Operation _attach = new();
        private Operation? _read;
        private TlsConnection? _connection;
        private VideoFrameReader? _reader;
        private bool _stopped;
        private bool _delivered;
        private Task? _join;
        private Task? _videoClose;
        private Task? _cancel;
        private Task? _readerDispose;
        private Exception? _cancelError;
        private Exception? _readerDisposeError;
        private object Gate => _parent._gate;

        internal ClientVideoLifetime(AuthenticatedControlSession parent,
            Func<int, IMemoryOwner<byte>>? rent, Action<EncodedFrame?>? frameRead, TaskScheduler? joinScheduler)
        {
            _parent = parent;
            _rent = rent;
            _frameRead = frameRead;
            _joinScheduler = joinScheduler ?? TaskScheduler.Default;
            _deadline = new AttachDeadline(this);
        }

        internal Task<ClientVideoLifetime> StartUnderGate(
            Func<CancellationToken, Task<TlsConnection>> connect,
            Func<TlsConnection, CancellationToken, Task> initialize,
            CancellationToken caller)
        {
            // 两个固定 worker 均先登记，首次取得父 gate 后才执行原外部调用。
            _deadline.StartUnderGate();
            // 不向 Task.Run 传取消令牌：登记成功的协调者即使先被停止也必须运行并收尾。
            Task<ClientVideoLifetime> worker = Task.Run(() => AttachCoreAsync(connect, initialize, caller));
            _attach.Worker = worker;
            return worker;
        }

        private async Task<ClientVideoLifetime> AttachCoreAsync(
            Func<CancellationToken, Task<TlsConnection>> connect,
            Func<TlsConnection, CancellationToken, Task> initialize,
            CancellationToken caller)
        {
            CancellationTokenRegistration registration = default;
            try
            {
                // 第一次 gate 同时是发布屏障：原工厂不可能早于 Worker 槽登记执行。
                lock (Gate) CheckActive(caller);
                registration = RegisterCaller(caller, _attach);
                await _deadline.Ready.ConfigureAwait(false);
                lock (Gate) CheckActive(caller);
                Task<TlsConnection> originalConnect = connect(_stopSource.Token)
                    ?? throw new InvalidOperationException("连接工厂没有返回任务。");
                TlsConnection connection = await ObserveOriginalAsync(originalConnect, _attach).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("连接工厂没有返回连接。");
                lock (Gate)
                {
                    // 无论停止是否已赢，都先接管迟到连接；catch 会安排它的关闭。
                    _connection = connection;
                    CheckActive(caller);
                }
                Task originalInitialize = initialize(connection, _stopSource.Token)
                    ?? throw new InvalidOperationException("初始化没有返回任务。");
                await ObserveOriginalAsync(originalInitialize, _attach).ConfigureAwait(false);
                // 原 timer/回调和 caller 注册退出后才可提交；收尾耗时仍从 success 原锚点扣除。
                await _deadline.QuiesceAsync().ConfigureAwait(false);
                await DrainCallerAsync(registration, _attach).ConfigureAwait(false);
                registration = default;
                lock (Gate)
                {
                    CheckActive(caller);
                    // 先裁决父/caller/deadline 再创建适配器，不能让已关闭连接的 ODE 抢先覆盖原因。
                    // 构造只保存本层字段，不执行 I/O、租用函数或任何外部回调。
                    _reader = new VideoFrameReader(connection.CreateVideoStream(), _rent);
                    _ = _parent.GetRemainingAttachBudget(caller);
                    // clock 的任意重入可停止 child；不能只依赖预算方法的父状态终检。
                    CheckActive(caller);
                    _delivered = true;
                    _attach.Committed = true;
                }
                return this;
            }
            catch (Exception error)
            {
                lock (Gate) _attach.Error ??= error;
                RequestStop();
                throw;
            }
            finally
            {
                if (!_attach.Committed)
                {
                    await _deadline.QuiesceAsync().ConfigureAwait(false);
                    await DrainCallerAsync(registration, _attach).ConfigureAwait(false);
                }
            }
        }

        /// <summary>最多一个原读取；整帧通过父/子共同终检才交付，附着预算不限制持续视频读取。</summary>
        internal Task<EncodedFrame?> ReadFrameAsync(CancellationToken cancellationToken = default)
        {
            lock (Gate)
            {
                CheckActive(cancellationToken);
                if (!_delivered) throw new InvalidOperationException("视频会话尚未交付。");
                if (_read?.Worker is { IsCompleted: false })
                    throw new InvalidOperationException("视频会话不允许重叠读取。");
                Operation operation = new();
                _read = operation;
                Task<EncodedFrame?> worker = Task.Run(() => ReadCoreAsync(operation, cancellationToken));
                operation.Worker = worker;
                return worker;
            }
        }

        private async Task<EncodedFrame?> ReadCoreAsync(Operation operation, CancellationToken caller)
        {
            CancellationTokenRegistration registration = default;
            EncodedFrame? uncommitted = null;
            try
            {
                lock (Gate) CheckActive(caller);
                registration = RegisterCaller(caller, operation);
                lock (Gate) CheckActive(caller);
                Task<EncodedFrame?> original = _reader!.ReadFrameAsync(_stopSource.Token);
                uncommitted = await ObserveOriginalAsync(original, operation).ConfigureAwait(false);
                _frameRead?.Invoke(uncommitted);
                bool eof = uncommitted is null;
                lock (Gate)
                {
                    CheckActive(caller);
                    if (!ReferenceEquals(_read, operation))
                        throw new InvalidOperationException("视频读取槽身份失效。");
                    operation.Committed = true;
                    if (eof) MarkStoppedUnderGate();
                }
                EncodedFrame? delivered = uncommitted;
                uncommitted = null;
                if (eof) RequestStop();
                return delivered;
            }
            catch (Exception error)
            {
                lock (Gate) operation.Error ??= error;
                RequestStop();
                throw;
            }
            finally
            {
                // 不在父锁内调用外部 owner.Dispose；原 worker 覆盖这一整段收尾。
                try { uncommitted?.Dispose(); }
                catch (Exception error) { lock (Gate) operation.CleanupError = error; }
                await DrainCallerAsync(registration, operation).ConfigureAwait(false);
            }
        }

        private async Task DrainCallerAsync(CancellationTokenRegistration registration, Operation operation)
        {
            try { await registration.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error)
            {
                lock (Gate) operation.RegistrationError ??= error;
                RequestStop();
            }
        }

        private CancellationTokenRegistration RegisterCaller(CancellationToken caller, Operation operation) =>
            caller.UnsafeRegister(_ =>
            {
                try
                {
                    lock (Gate)
                    {
                        // 接受之后的 caller 取消不能追溯停止已交付 child/帧。
                        if (operation.Committed) return;
                        MarkStoppedUnderGate();
                    }
                    EnsureStopStarted();
                }
                catch (Exception error)
                {
                    lock (Gate) operation.CallbackError ??= error;
                }
            }, null);

        private void CheckActive(CancellationToken caller)
        {
            ObjectDisposedException.ThrowIf(_parent._connection is null, _parent);
            caller.ThrowIfCancellationRequested();
            if (_deadlineFailure is { } deadlineError) ExceptionDispatchInfo.Capture(deadlineError).Throw();
            if (_attach.RegistrationError is { } registrationError) ExceptionDispatchInfo.Capture(registrationError).Throw();
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (!ReferenceEquals(_parent._videoLifetime, this))
                throw new InvalidOperationException("视频会话不属于当前父会话。");
        }

        internal void MarkStoppedUnderGate() => _stopped = true;

        internal void RequestStop()
        {
            lock (Gate) MarkStoppedUnderGate();
            EnsureStopStarted();
        }

        internal Task StopAndJoinAsync()
        {
            RequestStop();
            lock (Gate) return _join!;
        }

        private void EnsureStopStarted()
        {
            lock (Gate)
            {
                // 只向内部协调者发通知；原 timer 方法由它在父锁外执行，不等待原操作退出才停 timer。
                _deadline.StopScheduling();
                // CloseAsync 只登记独立 worker，不在此线程执行释放；取消另起 worker，互不串行等待。
                if (_connection is not null) _videoClose ??= _connection.CloseAsync();
                if (_reader is not null) _readerDispose ??= Task.Run(() =>
                {
                    try { _reader.Dispose(); }
                    catch (Exception error) { lock (Gate) _readerDisposeError = error; }
                });
                _cancel ??= Task.Run(() =>
                {
                    try { _stopSource.Cancel(); }
                    catch (Exception error) { lock (Gate) _cancelError = error; }
                });
                // 默认仍在线程池执行；实例级测试调度器能确定性证明 read await，不靠瞬时采样。
                _join ??= Task.Factory.StartNew(JoinCoreAsync, CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach, _joinScheduler).Unwrap();
            }
        }

        private async Task JoinCoreAsync()
        {
            Task attach;
            lock (Gate) attach = _attach.Worker!;
            await JoinWorkerAsync(attach).ConfigureAwait(false);
            Task? read;
            lock (Gate) read = _read?.Worker;
            if (read is not null) await JoinWorkerAsync(read).ConfigureAwait(false);
            // 原工厂/初始化现已结束，不再可能发布新连接或 reader；补齐迟到资源的关闭。
            EnsureStopStarted();
            Task? videoClose;
            Task? readerDispose;
            Task cancel;
            lock (Gate)
            {
                videoClose = _videoClose;
                readerDispose = _readerDispose;
                cancel = _cancel!;
            }
            if (videoClose is not null) await videoClose.ConfigureAwait(false);
            if (readerDispose is not null) await readerDispose.ConfigureAwait(false);
            await cancel.ConfigureAwait(false);
            await _deadline.QuiesceAsync().ConfigureAwait(false);
            _stopSource.Dispose();
        }

        private static async Task JoinWorkerAsync(Task worker)
        {
            try { await worker.ConfigureAwait(false); }
            catch { _ = worker.Exception; } // 操作槽保留原 Task 的完整多错；此处只保证其他收尾仍被 join。
        }

        private async Task<T> ObserveOriginalAsync<T>(Task<T> original, Operation operation)
        {
            try { return await original.ConfigureAwait(false); }
            catch (Exception error)
            {
                lock (Gate) operation.Error = OriginalError(original, error);
                throw;
            }
        }

        private async Task ObserveOriginalAsync(Task original, Operation operation)
        {
            try { await original.ConfigureAwait(false); }
            catch (Exception error)
            {
                lock (Gate) operation.Error = OriginalError(original, error);
                throw;
            }
        }

        private static Exception OriginalError(Task original, Exception observed)
        {
            AggregateException? container = original.Exception;
            // 只去掉 Task 自动添加的单项容器；用户自己的嵌套 Aggregate 原样保留。
            return container is null ? observed
                : container.InnerExceptions.Count == 1 ? container.InnerExceptions[0] : container;
        }

        internal IReadOnlyList<Exception> LifetimeErrors
        {
            get
            {
                lock (Gate)
                {
                    List<Exception> errors = new();
                    Exception?[] operations = [_deadlineFailure, _deadline.CleanupError,
                        _attach.Error, _attach.RegistrationError, _attach.CallbackError,
                        _read?.Error, _read?.CleanupError, _read?.RegistrationError, _read?.CallbackError,
                        _cancelError, _readerDisposeError];
                    AddErrors(errors, operations.OfType<Exception>());
                    if (_reader is not null) AddErrors(errors, _reader.CleanupErrors);
                    if (_connection is not null) AddErrors(errors, _connection.CleanupErrors);
                    return errors.AsReadOnly();
                }
            }
        }

        private sealed class Operation
        {
            internal Task? Worker;
            internal bool Committed;
            internal Exception? Error;
            internal Exception? CleanupError;
            internal Exception? RegistrationError;
            internal Exception? CallbackError;
        }
    }
}
