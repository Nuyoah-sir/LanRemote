using System.Runtime.ExceptionServices;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport;

/// <summary>
/// 显式内部装配的双通道路由。每连接一个实例；只消费一次首帧，不改变 public Control-only 入口。
/// Host 仍拥有 socket。RunAsync 完成前等待本路由启动的读、发送、取消回调与关闭任务。
/// </summary>
internal sealed class FirstFrameRouter
{
    private readonly ControlAuthContext _context;
    private readonly TransportTimeouts _timeouts;
    private readonly IVideoFrameSource? _frames;
    private readonly IVideoFrameProducerFactory? _factory;
    private readonly VideoSessionOptions _options;
    private int _started;

    internal FirstFrameRouter(
        ControlAuthContext context,
        TransportTimeouts timeouts,
        IVideoFrameSource frames,
        VideoSessionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(timeouts);
        ArgumentNullException.ThrowIfNull(frames);
        _context = context;
        _timeouts = timeouts;
        _frames = frames;
        _options = options ?? new VideoSessionOptions();
    }

    internal FirstFrameRouter(
        ControlAuthContext context,
        TransportTimeouts timeouts,
        IVideoFrameProducerFactory factory,
        VideoSessionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(timeouts);
        ArgumentNullException.ThrowIfNull(factory);
        _context = context;
        _timeouts = timeouts;
        _factory = factory;
        _options = options ?? new VideoSessionOptions();
    }

    internal string? Rejection { get; private set; }
    internal VideoAttachStatus? AttachStatus { get; private set; }
    internal int AttachAttempts { get; private set; }

    internal async Task RunAsync(AcceptedConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("一个连接的首帧路由只能运行一次。");
        if (!connection.HasCloseAuthority)
            throw new InvalidOperationException("双通道路由必须显式绑定 Host 的原始连接。");

        long enteredAt = _context.TimeProvider.GetTimestamp();
        ControlAuthSession? auth = null;
        VideoAttachLease? attachedLease = null;
        List<Exception> errors = new(5);
        try
        {
            // 主体的早退或故障都不能跳过收尾；各项 join 只收集错误，最后统一传播。
            await JoinAsync(RunCoreAsync(), errors).ConfigureAwait(false);
            // 关闭请求不是 join；先解堵原流，再等待原审批读和决定，绝不新增 Control reader。
            try
            {
                await JoinAsync(connection.CloseAsync(), errors).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // 若授权关闭在返回任务前同步失败，不可宣称物理关闭已 join。
                AddError(errors, error);
            }
            if (auth is not null)
                await JoinAsync(auth.JoinOwnedOperationsAsync(), errors).ConfigureAwait(false);
        }
        finally
        {
            // 父 Control 只能在本视频路由和已启动的 Host 原关闭任务收尾后解除子连接预约。
            attachedLease?.Complete();
        }
        if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1) throw new AggregateException("连接路由收尾发生多个错误。", errors);

        async Task RunCoreAsync()
        {
            VideoAttachLease? lease;
            try
            {
                byte[] payload = await ReadHelloAsync(connection, enteredAt, cancellationToken).ConfigureAwait(false);
                bool control = HelloFrame.TryParse(payload, out _);
                CheckEnvelope(enteredAt, cancellationToken);
                if (control)
                {
                    // 首帧已消费；不能再调用 public pre-auth 重读，也不能把信封令牌泄漏到 holding。
                    ControlPreAuthHandoff handoff = new(connection.Stream, connection.Security);
                    auth = handoff.BeginAuthentication(_context, _timeouts);
                    await auth.RunAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (!VideoHelloFrame.TryParse(payload, out VideoHelloFrame? hello, out _))
                {
                    Rejection = "channel-hello-invalid";
                    return;
                }
                CheckEnvelope(enteredAt, cancellationToken);
                lease = await AttachAsync(hello, connection.Security, enteredAt, cancellationToken,
                    attached => attachedLease = attached).ConfigureAwait(false);
                if (lease is null)
                {
                    Rejection = "video-attach-rejected";
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                // 首帧/登记等待的子 deadline 取消归一为 Host 令牌；caller 优先于阶段截止。
                // 此处尚未进入视频来源，不能将该规则扩展到来源/收尾故障。
                cancellationToken.ThrowIfCancellationRequested();
                Rejection ??= "channel-timeout-or-revoked";
                return;
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or FrameProtocolException)
            {
                Rejection ??= "channel-closed-or-invalid";
                return;
            }

            // 视频内部已分类网络结束和取消；真实来源/清理故障不能再套用上述豁免。
            await RunVideoAsync(connection, lease, enteredAt, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<byte[]> ReadHelloAsync(
        AcceptedConnection connection, long enteredAt, CancellationToken cancellationToken)
    {
        FrameReader reader = new(connection.Stream);
        uint wireLength;
        using (AuthenticationDeadline prefix = new(
            _context.TimeProvider, Min(_timeouts.LengthPrefixTimeout, CheckEnvelope(enteredAt, cancellationToken)),
            cancellationToken))
        {
            wireLength = await reader.ReadLengthPrefixAsync(prefix.Token).ConfigureAwait(false);
            CheckStage(prefix, cancellationToken);
        }
        CheckEnvelope(enteredAt, cancellationToken);
        if (!FrameReader.TryValidateLength(wireLength, TransportConstants.MaxPreAuthMessageBytes,
                out int length, out string? rejection))
            throw new FrameProtocolException(rejection!);

        using AuthenticationDeadline payload = new(
            _context.TimeProvider, Min(_timeouts.PayloadTimeout, CheckEnvelope(enteredAt, cancellationToken)),
            cancellationToken);
        byte[] bytes = await reader.ReadPayloadAsync(length, payload.Token).ConfigureAwait(false);
        CheckStage(payload, cancellationToken);
        CheckEnvelope(enteredAt, cancellationToken);
        return bytes;
    }

    private async Task<VideoAttachLease?> AttachAsync(
        VideoHelloFrame hello, ConnectionSecurityContext security, long enteredAt, CancellationToken cancellationToken,
        Action<VideoAttachLease> onAttached)
    {
        long waitStarted = _context.TimeProvider.GetTimestamp();
        using AuthenticationDeadline wait = new(
            _context.TimeProvider, Min(VideoSessionOptions.RegistrationWait, CheckEnvelope(enteredAt, cancellationToken)),
            cancellationToken);
        for (int attempt = 0; attempt < VideoSessionOptions.MaxAttachAttempts; attempt++)
        {
            CheckStage(wait, cancellationToken);
            CheckEnvelope(enteredAt, cancellationToken);
            CheckElapsed(waitStarted, VideoSessionOptions.RegistrationWait, cancellationToken);
            AttachAttempts++;
            AttachStatus = _context.SessionRegistry.TryAttachVideo(
                hello.SessionId, security, hello.AttachNonce.Span, hello.AttachProof.Span,
                wait.Token, out VideoAttachLease? lease);
            if (AttachStatus == VideoAttachStatus.Attached && lease is not null)
                onAttached(lease);
            // 即使本机调度/时钟使调用跨越路由预算，也不得发送 ACK；已消费资格不回滚。
            CheckStage(wait, cancellationToken);
            CheckEnvelope(enteredAt, cancellationToken);
            CheckElapsed(waitStarted, VideoSessionOptions.RegistrationWait, cancellationToken);
            if (AttachStatus == VideoAttachStatus.Attached)
                return lease;
            if (AttachStatus != VideoAttachStatus.NotRegistered || attempt == VideoSessionOptions.MaxAttachAttempts - 1)
                return null;
            await Task.Delay(VideoSessionOptions.RegistrationRetry, _context.TimeProvider, wait.Token)
                .ConfigureAwait(false);
        }
        return null;
    }

    private async Task RunVideoAsync(
        AcceptedConnection connection, VideoAttachLease lease, long enteredAt, CancellationToken hostToken)
    {
        using CancellationTokenSource lifetime = new();
        TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // 回调仅发信号；生产者的 Stop 不在 Host 取消/撤销回调的同一栈上自 join。
        using CancellationTokenRegistration stopRegistration = hostToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(), stopped);
        Task<int> peer = ReadVideoPeerAsync(connection.Stream, lifetime.Token);
        VideoFrameSender sender = new();
        TaskCompletionSource transferEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        object producerGate = new();
        IVideoFrameProducer? producer = null;
        FixedProducerFrameSource? ownedSource = null;
        Task? producerStop = null;
        Task? stopOperation = null;
        Task? disposeOperation = null;
        bool stopRequested = false;
        Exception? factoryFailure = null;
        Exception? startFailure = null;
        Exception? probeFailure = null;
        Exception? stopFailure = null;
        Exception? disposeFailure = null;

        Task transfer = Task.Run(async () =>
        {
            try
            {
                CheckActive();
                using (AuthenticationDeadline ack = new(
                    _context.TimeProvider, Min(_options.AckWriteTimeout, CheckEnvelope(enteredAt, lifetime.Token)),
                    lifetime.Token))
                {
                    CheckActive();
                    await FrameWriter.WriteFrameAsync(connection.Stream,
                        new VideoAttachAckFrame(lease.SessionId).Serialize(),
                        TransportConstants.MaxPreAuthMessageBytes, ack.Token).ConfigureAwait(false);
                    CheckStage(ack, lifetime.Token);
                    CheckEnvelope(enteredAt, lifetime.Token);
                }
                // ACK 完整 flush 且检查通过后才创建；同步工厂前缀也只在受监督的 transfer 内。
                CheckActive();
                if (_factory is null)
                {
                    await sender.SendAsync(lease.SessionId, new VideoFrameWriter(connection.CreateVideoStream()),
                        _frames!, _context.TimeProvider, _options.FrameWriteTimeout, lifetime.Token)
                        .ConfigureAwait(false);
                    return;
                }

                Task<IVideoFrameProducer>? creation = null;
                IVideoFrameProducer created;
                try
                {
                    creation = _factory.CreateAsync(lease.SessionId, lifetime.Token).AsTask();
                    created = await creation.ConfigureAwait(false)
                        ?? throw new InvalidOperationException("视频工厂未交付生产者。");
                }
                catch (Exception error) when (IsExpectedCancellation(error, creation, lifetime.Token))
                {
                    return;
                }
                catch (Exception error)
                {
                    factoryFailure = OriginalFailure(creation, error);
                    return;
                }

                // 从创建任务取得成功结果的同一 continuation 接管；迟到结果仍必须进入 finally 清理。
                bool startAllowed;
                lock (producerGate)
                {
                    producer = created;
                    startAllowed = !stopRequested;
                    if (startAllowed) CheckActive();
                }
                if (!startAllowed) return;
                // 外部 Start 不占 Stop 所需的锁；FramePipeline 原子状态检查会拒绝停止后的启动。
                try { created.Start(); }
                catch (Exception error)
                {
                    startFailure = error;
                    bool stoppedBeforeStart;
                    lock (producerGate) stoppedBeforeStart = stopRequested;
                    if (stoppedBeforeStart && error.GetType() == typeof(InvalidOperationException) &&
                        created is IVideoFrameProducerStartRejection evidence)
                    {
                        try
                        {
                            if (evidence.IsStopBeforeStartRejection((InvalidOperationException)error))
                                startFailure = null;
                        }
                        catch (Exception failure) { probeFailure = failure; }
                    }
                    return;
                }
                CheckActive();

                FixedProducerFrameSource source = new(created);
                ownedSource = source;
                try
                {
                    await sender.SendAsync(lease.SessionId, new VideoFrameWriter(connection.CreateVideoStream()),
                        source, _context.TimeProvider, _options.FrameWriteTimeout, lifetime.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception) when (source.ReadFailure is not null)
                {
                    // 读操作的原 Task 错误树由适配器保存，不能由网络结束豁免或 await 截断。
                }
            }
            finally
            {
                // 通知外层先关闭、取消、请求 Stop；不能等生产者清理完成才开始断开。
                transferEnded.TrySetResult();
                if (producer is { } owned)
                {
                    Task stop = RequestProducerStop()!;
                    Task dispose = Task.Run(async () =>
                    {
                        try
                        {
                            disposeOperation = owned.DisposeAsync().AsTask();
                            await disposeOperation.ConfigureAwait(false);
                        }
                        catch (Exception error) { disposeFailure = OriginalFailure(disposeOperation, error); }
                    });
                    await Task.WhenAll(stop, dispose).ConfigureAwait(false);
                }
            }
        });

        await Task.WhenAny(transferEnded.Task, peer, lease.Revoked, stopped.Task).ConfigureAwait(false);
        // 三项互不等待：释放 socket、取消创建/读取、对已接管生产者请求停止。
        Task close = connection.CloseAsync();
        Task cancellation = lifetime.CancelAsync();
        Task stopRequest = Task.Run(async () =>
        {
            Task? stop = RequestProducerStop();
            if (stop is not null) await stop.ConfigureAwait(false);
        });
        List<Exception> errors = new(10);
        await JoinAsync(transfer, errors, expectedNetworkEnd: true).ConfigureAwait(false);
        if (ownedSource?.ReadFailure is { } readFailure)
            AddError(errors, readFailure);
        else if (sender.SourceFailure is { } sourceFailure)
            AddError(errors, sourceFailure);
        foreach (Exception error in sender.CleanupErrors)
            AddError(errors, error);
        await JoinAsync(peer, errors, expectedNetworkEnd: true).ConfigureAwait(false);
        await JoinAsync(cancellation, errors).ConfigureAwait(false);
        await JoinAsync(close, errors).ConfigureAwait(false);
        await JoinAsync(stopRequest, errors).ConfigureAwait(false);
        // 各操作各保留一棵原始错误树；不同操作即便抛出同一实例也分别记录。
        if (factoryFailure is not null) errors.Add(factoryFailure);
        if (startFailure is not null) errors.Add(startFailure);
        if (probeFailure is not null) errors.Add(probeFailure);
        if (stopFailure is not null) errors.Add(stopFailure);
        // 按原任务身份去重，而不是按异常引用去重。
        if (disposeFailure is not null &&
            !(stopOperation is not null && ReferenceEquals(stopOperation, disposeOperation)))
            errors.Add(disposeFailure);
        if (peer.IsCompletedSuccessfully && peer.Result != 0)
            Rejection = "video-unexpected-upstream-data";
        if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1) throw new AggregateException("视频连接收尾发生多个错误。", errors);
        hostToken.ThrowIfCancellationRequested();

        Task? RequestProducerStop()
        {
            lock (producerGate)
            {
                stopRequested = true;
                if (producer is not { } owned) return null;
                return producerStop ??= Task.Run(async () =>
                {
                    try
                    {
                        stopOperation = owned.StopAsync();
                        await stopOperation.ConfigureAwait(false);
                    }
                    catch (Exception error) { stopFailure = OriginalFailure(stopOperation, error); }
                });
            }
        }

        void CheckActive()
        {
            hostToken.ThrowIfCancellationRequested();
            lifetime.Token.ThrowIfCancellationRequested();
            if (lease.Revoked.IsCompleted || peer.IsCompleted || stopped.Task.IsCompleted)
                throw new OperationCanceledException("视频连接已撤销或关闭。", lifetime.Token);
        }
    }

    private sealed class FixedProducerFrameSource(IVideoFrameProducer producer) : IVideoFrameSource
    {
        internal Exception? ReadFailure { get; private set; }

        public async ValueTask<EncodedFrame?> ReadNextAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            Task<EncodedFrame?>? read = null;
            try
            {
                // 绑定本会话已经接管的生产者，不允许每帧重新调用工厂。
                read = producer.ReadNextAsync(cancellationToken).AsTask();
                return await read.ConfigureAwait(false);
            }
            catch (Exception error) when (IsExpectedCancellation(error, read, cancellationToken))
            {
                throw;
            }
            catch (Exception error)
            {
                ReadFailure ??= OriginalFailure(read, error);
                throw;
            }
        }
    }

    private static bool IsExpectedCancellation(Exception error, Task? operation, CancellationToken token) =>
        error is OperationCanceledException cancelled && token.IsCancellationRequested &&
        cancelled.CancellationToken == token && (operation is null || operation.IsCanceled);

    private static Exception OriginalFailure(Task? operation, Exception fallback)
    {
        // 不 Flatten：原任务可能同时包含多个错误，甚至重复引用的不同故障位置。
        AggregateException? errors = operation?.Exception;
        return errors is null ? fallback : errors.InnerExceptions.Count == 1
            ? errors.InnerExceptions[0] : errors;
    }

    private static async Task JoinAsync(Task task, List<Exception> errors, bool expectedNetworkEnd = false)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception error)
        {
            // await WhenAll 只抛其中一项；任务完成后从原任务读取完整集合，取消任务则保留所抛实例。
            IEnumerable<Exception> failures = task.Exception is { } aggregate
                ? aggregate.InnerExceptions : new[] { error };
            foreach (Exception failure in failures)
            {
                if (expectedNetworkEnd &&
                    failure is OperationCanceledException or IOException or ObjectDisposedException)
                    continue;
                AddError(errors, failure);
            }
        }
    }

    private static void AddError(List<Exception> errors, Exception error)
    {
        if (!errors.Any(existing => ReferenceEquals(existing, error))) errors.Add(error);
    }

    private static async Task<int> ReadVideoPeerAsync(Stream stream, CancellationToken cancellationToken) =>
        await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false);

    private TimeSpan CheckEnvelope(long enteredAt, CancellationToken cancellationToken) =>
        CheckElapsed(enteredAt, _timeouts.PreAuthEnvelopeTimeout, cancellationToken);

    private TimeSpan CheckElapsed(long startedAt, TimeSpan budget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TimeSpan elapsed = _context.TimeProvider.GetElapsedTime(startedAt);
        cancellationToken.ThrowIfCancellationRequested();
        if (elapsed < TimeSpan.Zero || elapsed >= budget)
            throw new OperationCanceledException("通道绝对时限到期。", cancellationToken);
        return budget - elapsed;
    }

    private static void CheckStage(AuthenticationDeadline deadline, CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        bool expired = deadline.IsExpired;
        caller.ThrowIfCancellationRequested();
        if (expired) throw new OperationCanceledException("通道分段时限到期。", deadline.Token);
        deadline.Token.ThrowIfCancellationRequested();
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}

/// <summary>内部接线初值，不代表网络性能调优结论；所有阶段均有正值上限。</summary>
internal sealed class VideoSessionOptions
{
    internal static readonly TimeSpan RegistrationWait = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan RegistrationRetry = TimeSpan.FromMilliseconds(40);
    internal const int MaxAttachAttempts = 3;

    internal VideoSessionOptions(TimeSpan? ackWriteTimeout = null, TimeSpan? frameWriteTimeout = null)
    {
        AckWriteTimeout = Validate(ackWriteTimeout ?? TimeSpan.FromSeconds(2));
        FrameWriteTimeout = Validate(frameWriteTimeout ?? TimeSpan.FromSeconds(5));
    }

    internal TimeSpan AckWriteTimeout { get; }
    internal TimeSpan FrameWriteTimeout { get; }

    private static TimeSpan Validate(TimeSpan value) => value > TimeSpan.Zero && value <= TimeSpan.FromDays(1)
        ? value : throw new ArgumentOutOfRangeException(nameof(value));
}
