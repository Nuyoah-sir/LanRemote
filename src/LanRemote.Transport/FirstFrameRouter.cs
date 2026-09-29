using System.Runtime.ExceptionServices;
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
    private readonly IVideoFrameSource _frames;
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
        List<Exception> errors = new(5);
        // 主体的早退或故障都不能跳过收尾；各项 join 只收集错误，最后统一传播。
        await JoinAsync(RunCoreAsync(), errors).ConfigureAwait(false);
        // 关闭请求不是 join；先解堵原流，再等待原审批读和决定，绝不新增 Control reader。
        await JoinAsync(connection.CloseAsync(), errors).ConfigureAwait(false);
        if (auth is not null)
            await JoinAsync(auth.JoinOwnedOperationsAsync(), errors).ConfigureAwait(false);
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
                lease = await AttachAsync(hello, connection.Security, enteredAt, cancellationToken)
                    .ConfigureAwait(false);
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
        VideoHelloFrame hello, ConnectionSecurityContext security, long enteredAt, CancellationToken cancellationToken)
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
        // 不把帧源任意取消回调直接接到 Host 同步取消链上。
        using CancellationTokenRegistration stopRegistration = hostToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(), stopped);
        Task<int> peer = ReadVideoPeerAsync(connection.Stream, lifetime.Token);
        VideoFrameSender sender = new();
        Task transfer = Task.Run(async () =>
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
            // ACK 完整 flush 后才可向源请求第一帧；此处后不再使用首帧信封或附着窗口。
            CheckActive();
            await sender.SendAsync(lease.SessionId, new VideoFrameWriter(connection.CreateVideoStream()),
                _frames, _context.TimeProvider, _options.FrameWriteTimeout, lifetime.Token).ConfigureAwait(false);
        });

        await Task.WhenAny(transfer, peer, lease.Revoked, stopped.Task).ConfigureAwait(false);
        // 两者独立发起：恶意/故障取消回调不能阻止 socket 被关闭。
        Task close = connection.CloseAsync();
        Task cancellation = lifetime.CancelAsync();
        List<Exception> errors = new(5);
        await JoinAsync(transfer, errors, expectedNetworkEnd: true).ConfigureAwait(false);
        if (sender.SourceFailure is { } sourceFailure)
            AddError(errors, sourceFailure);
        foreach (Exception error in sender.CleanupErrors)
            AddError(errors, error);
        await JoinAsync(peer, errors, expectedNetworkEnd: true).ConfigureAwait(false);
        await JoinAsync(cancellation, errors).ConfigureAwait(false);
        await JoinAsync(close, errors).ConfigureAwait(false);
        if (peer.IsCompletedSuccessfully && peer.Result != 0)
            Rejection = "video-unexpected-upstream-data";
        if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1) throw new AggregateException("视频连接收尾发生多个错误。", errors);
        hostToken.ThrowIfCancellationRequested();

        void CheckActive()
        {
            hostToken.ThrowIfCancellationRequested();
            lifetime.Token.ThrowIfCancellationRequested();
            if (lease.Revoked.IsCompleted || peer.IsCompleted || stopped.Task.IsCompleted)
                throw new OperationCanceledException("视频连接已撤销或关闭。", lifetime.Token);
        }
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
