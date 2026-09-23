using System.Security.Authentication;
using System.Security.Cryptography;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport;

/// <summary>从冻结目标建立 TLS 并完成双向访问密钥认证，不向调用方交出中间连接。</summary>
public sealed class ControlClientConnector
{
    /// <summary>成功才移交独占连接的会话；失败或取消均关闭连接。</summary>
    /// <param name="target">连接前冻结的目标。</param>
    /// <param name="clientDeviceId">本机设备号，不得为空。</param>
    /// <param name="clientName">本机显示名，遵循 auth_response 的名称规则。</param>
    /// <param name="accessKey">上层提供的 16 字节密钥；首次 await 前复制，退出时清零私有副本。</param>
    /// <param name="requestedPermission">请求权限，只允许保持或从 Control 降为 ViewOnly。</param>
    /// <param name="options">本地机器与审批窗口。</param>
    /// <param name="timeouts">TLS、hello 及帧分段预算。</param>
    /// <param name="clock">单调时间与认证 timer 的来源。</param>
    /// <param name="cancellationToken">调用方取消；向上保留 OperationCanceledException。</param>
    /// <exception cref="ControlClientAuthenticationException">认证协议、proof、授权或时限校验失败。</exception>
    /// <exception cref="AuthenticationException">底层 TLS 认证或 pin 合同失败，保持原异常类别。</exception>
    public async Task<AuthenticatedControlSession> ConnectAndAuthenticateAsync(
        ConnectionTarget target,
        Guid clientDeviceId,
        string clientName,
        ReadOnlyMemory<byte> accessKey,
        SessionPermission requestedPermission,
        ControlClientAuthOptions? options = null,
        TransportTimeouts? timeouts = null,
        TimeProvider? clock = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (accessKey.Length != AccessSecret.AccessKeyByteLength)
        {
            throw new ArgumentException("访问密钥必须恰好为 16 字节。", nameof(accessKey));
        }

        // 同步前缀完成复制；后续只读私有副本，不保留调用方可变内存。
        byte[] key = accessKey.ToArray();
        accessKey = default;
        TlsConnection? connection = null;
        AuthenticatedControlSession? session = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(clientName);
            if (clientDeviceId == Guid.Empty)
            {
                throw new ArgumentException("客户端设备号不得为空。", nameof(clientDeviceId));
            }

            if (!AuthResponseFrame.IsAcceptableClientName(clientName))
            {
                throw new ArgumentException(
                    "clientName 必须非空、长度不超过上限、无控制字符且 UTF-16 良构。", nameof(clientName));
            }

            _ = AuthProtocol.EncodePermission(requestedPermission);
            ControlClientAuthOptions authOptions = options ?? new();
            authOptions.Validate();
            TransportTimeouts budget = timeouts ?? TransportTimeouts.Default;
            TimeProvider effectiveClock = clock ?? TimeProvider.System;

            // TLS 的 AuthenticationException 不经过下面的认证异常翻译。
            connection = await new TlsClientConnector()
                .ConnectAsync(target, budget, effectiveClock, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!connection.Identity.PinsMatch)
            {
                throw new AuthenticationException("TLS 连接的实际证书指纹与冻结目标不一致。");
            }

            session = await AuthenticateConnectedAsync(
                connection, target, clientDeviceId, clientName, key, requestedPermission,
                authOptions, budget, effectiveClock, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            AuthenticatedControlSession result = session;
            session = null;
            connection = null;
            return result;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // 取消同时表现为 I/O 失败时仍保留调用方取消语义，不携带可能含载荷的内层异常。
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            try
            {
                session?.Dispose();
            }
            finally
            {
                connection?.Dispose();
            }
        }
    }

    private static async Task<AuthenticatedControlSession> AuthenticateConnectedAsync(
        TlsConnection connection,
        ConnectionTarget target,
        Guid clientDeviceId,
        string clientName,
        byte[] key,
        SessionPermission requestedPermission,
        ControlClientAuthOptions options,
        TransportTimeouts timeouts,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        string timeoutRejection = "client-hello-timeout";
        byte[]? clientTranscript = null;
        AuthSuccessFrame? success = null;
        try
        {
            long helloSentAt;
            using (ClientAuthWindow hello = new(clock, timeouts.HelloTimeout, cancellationToken, timeoutRejection))
            {
                await FrameWriter.WriteHelloAsync(connection.Stream, timeouts.HelloTimeout, hello.Token)
                    .ConfigureAwait(false);
                helloSentAt = clock.GetTimestamp();
                _ = hello.GetRemaining();
            }

            FrameReader reader = new(connection.Stream);
            Guid sessionId;
            string shortCode;
            long pendingAcceptedAt;
            timeoutRejection = "client-machine-timeout";
            using (ClientAuthWindow machine = new(
                clock, options.MachineWindow, cancellationToken, timeoutRejection, helloSentAt))
            {
                (AuthChallengeFrame challenge, long receivedAt) = await ReadChallengeAsync(
                    reader, connection.Identity, target, machine, timeouts).ConfigureAwait(false);
                sessionId = challenge.SessionId;

                // 从收齐 challenge 起算；解析耗时已经消耗提示预算，父机器窗口从不重置。
                using ClientAuthWindow challengeWindow = new(
                    clock, TimeSpan.FromMilliseconds(challenge.ExpiresInMs), cancellationToken,
                    timeoutRejection, receivedAt, machine);
                _ = challengeWindow.GetRemaining();

                byte[] clientNonce = RandomNumberGenerator.GetBytes(AuthProtocol.NonceByteLength);
                clientTranscript = AuthTranscriptBuilder.BuildClientTranscript(
                    sessionId, target.DeviceId, clientDeviceId, challenge.ServerNonce.Span,
                    clientNonce, connection.Identity.PresentedCertSha256.Span, requestedPermission);
                shortCode = LocalApprovalRequest.ComputeShortCode(sessionId, clientNonce);
                byte[] clientProof = AuthTranscriptBuilder.ComputeClientProof(key, clientTranscript);
                try
                {
                    byte[] response = new AuthResponseFrame(
                        clientDeviceId, clientName, clientNonce, requestedPermission, clientProof).Serialize();
                    try
                    {
                        TimeSpan remaining = challengeWindow.GetRemaining();
                        await FrameWriter.WriteFrameAsync(
                            connection.Stream, response, TransportConstants.MaxPreAuthMessageBytes,
                            remaining, challengeWindow.Token).ConfigureAwait(false);
                        _ = challengeWindow.GetRemaining();
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(response);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(clientProof);
                }

                // 只有 response 写完才允许 pending；返回 null 唯一表示第一次合法 pending。
                (success, pendingAcceptedAt) = await ReadReplyAsync(
                    reader, challengeWindow, timeouts, allowPending: true, approval: false).ConfigureAwait(false);
                if (success is not null)
                {
                    return VerifyAndCreateSession(
                        connection, success, clientTranscript, key, requestedPermission,
                        sessionId, shortCode, challengeWindow);
                }
            }

            // 独立本地审批窗口，不链接已结束的机器窗口；起点保留 pending 接受时刻。
            timeoutRejection = "client-approval-timeout";
            using ClientAuthWindow approvalWindow = new(
                clock, options.ApprovalWindow, cancellationToken, timeoutRejection, pendingAcceptedAt);
            _ = approvalWindow.GetRemaining();
            try
            {
                options.ApprovalPending?.Invoke(new ControlClientApprovalPending(
                    sessionId, shortCode, pendingAcceptedAt, options.ApprovalWindow));
            }
            catch (Exception)
            {
                // 取消/到期优先于本地通知故障；不泄漏任意回调异常中的敏感数据。
                _ = approvalWindow.GetRemaining();
                throw new ControlClientAuthenticationException(
                    "client-approval-notification-failed", "无法显示远端审批等待状态，请重试。");
            }
            _ = approvalWindow.GetRemaining();
            (success, _) = await ReadReplyAsync(
                reader, approvalWindow, timeouts, allowPending: false, approval: true).ConfigureAwait(false);
            return VerifyAndCreateSession(
                connection, success!, clientTranscript, key, requestedPermission,
                sessionId, shortCode, approvalWindow);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw TimeoutFailure(timeoutRejection);
        }
        catch (FrameProtocolException ex)
        {
            throw new ControlClientAuthenticationException(ex.Reason, "远端认证数据格式错误。");
        }
        catch (IOException)
        {
            throw new ControlClientAuthenticationException("client-connection-closed", "认证连接中断，请重试。");
        }
        catch (CryptographicException)
        {
            throw new ControlClientAuthenticationException("client-crypto-failed", "无法完成远端身份验证。");
        }
        finally
        {
            success?.ClearSessionToken();
            if (clientTranscript is not null)
            {
                CryptographicOperations.ZeroMemory(clientTranscript);
            }
        }
    }

    private static async Task<(AuthChallengeFrame Frame, long ReceivedAt)> ReadChallengeAsync(
        FrameReader reader,
        ConnectionIdentity identity,
        ConnectionTarget target,
        ClientAuthWindow window,
        TransportTimeouts timeouts)
    {
        (byte[] payload, long receivedAt) = await ReadFrameAsync(reader, window, timeouts, approval: false)
            .ConfigureAwait(false);
        try
        {
            bool parsed = AuthChallengeFrame.TryParse(payload, out AuthChallengeFrame? challenge, out string? rejection);
            bool failed = !parsed && AuthenticationFailedFrame.TryParse(payload, out _);
            _ = window.GetRemaining();
            if (failed)
            {
                throw RemoteFailure();
            }

            if (!parsed)
            {
                throw new ControlClientAuthenticationException(rejection!, "远端认证 challenge 无效或顺序错误。");
            }

            bool deviceMatches = challenge!.ServerDeviceId == target.DeviceId;
            bool pinMatches = CryptographicOperations.FixedTimeEquals(
                challenge.CertificateSha256.Span, identity.PresentedCertSha256.Span);
            _ = window.GetRemaining();
            if (!deviceMatches)
            {
                throw new ControlClientAuthenticationException(
                    "client-server-device-mismatch", "远端设备身份与连接目标不一致。");
            }

            if (!pinMatches)
            {
                throw new ControlClientAuthenticationException(
                    "client-challenge-pin-mismatch", "远端认证证书与实际 TLS 连接不一致。");
            }

            return (challenge, receivedAt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static async Task<(AuthSuccessFrame? Success, long AcceptedAt)> ReadReplyAsync(
        FrameReader reader,
        ClientAuthWindow window,
        TransportTimeouts timeouts,
        bool allowPending,
        bool approval)
    {
        var (payload, _) = await ReadFrameAsync(reader, window, timeouts, approval).ConfigureAwait(false);
        AuthSuccessFrame? success = null;
        try
        {
            if (AuthSuccessFrame.TryParse(payload, out success, out string? rejection))
            {
                _ = window.GetRemaining();
                AuthSuccessFrame result = success!;
                success = null;
                return (result, 0);
            }

            // 只使用完整严格解析器的结果，不把 JSON type 提示当作合法帧。
            bool failed = AuthenticationFailedFrame.TryParse(payload, out _);
            bool pending = ApprovalPendingFrame.TryParse(payload, out _);
            long acceptedAt = window.Clock.GetTimestamp();
            _ = window.GetRemaining();
            if (failed)
            {
                throw RemoteFailure();
            }

            if (pending)
            {
                if (!allowPending)
                {
                    throw new ControlClientAuthenticationException(
                        "client-repeated-pending", "远端重复发送审批等待帧。");
                }

                return (null, acceptedAt);
            }

            throw new ControlClientAuthenticationException(
                rejection == AuthSuccessFrame.RejectWrongType ? "client-unexpected-auth-frame" : rejection!,
                "远端认证帧无效或顺序错误。");
        }
        finally
        {
            success?.ClearSessionToken();
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static AuthenticatedControlSession VerifyAndCreateSession(
        TlsConnection connection,
        AuthSuccessFrame success,
        byte[] clientTranscript,
        byte[] key,
        SessionPermission requestedPermission,
        Guid sessionId,
        string shortCode,
        ClientAuthWindow window)
    {
        bool grantAllowed = success.GrantedPermission == requestedPermission
            || (requestedPermission == SessionPermission.Control
                && success.GrantedPermission == SessionPermission.ViewOnly);
        _ = window.GetRemaining();
        if (!grantAllowed)
        {
            throw new ControlClientAuthenticationException("client-overgrant", "远端授予了超出请求的权限。");
        }

        byte[] grantTranscript = AuthTranscriptBuilder.BuildGrantTranscript(clientTranscript, success.GrantedPermission);
        byte[] expectedProof = AuthTranscriptBuilder.ComputeServerProof(key, grantTranscript);
        try
        {
            bool proofMatches = CryptographicOperations.FixedTimeEquals(expectedProof, success.ServerProof.Span);
            // timer 只负责中断 I/O；完成解析和 MAC 后的单调检查才决定是否接受。
            _ = window.GetRemaining();
            if (!proofMatches)
            {
                throw new ControlClientAuthenticationException(
                    "client-server-proof-mismatch", ControlClientAuthenticationException.ServerProofFailureMessage);
            }

            AuthenticatedControlSession session = new(
                connection, success.GrantedPermission, sessionId, shortCode,
                success.SessionToken.Span, success.VideoAttachExpiresInMs);
            try
            {
                _ = window.GetRemaining();
                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedProof);
            CryptographicOperations.ZeroMemory(grantTranscript);
        }
    }

    private static async Task<(byte[] Payload, long ReceivedAt)> ReadFrameAsync(
        FrameReader reader, ClientAuthWindow window, TransportTimeouts timeouts, bool approval)
    {
        try
        {
            TimeSpan remaining = window.GetRemaining();
            // 等人审批时允许整个剩余窗口等待前缀，不能沿用默认五秒。
            TimeSpan prefixBudget = approval ? remaining : Min(timeouts.LengthPrefixTimeout, remaining);
            uint length;
            using (AuthenticationDeadline prefix = new(window.Clock, prefixBudget, window.Token))
            {
                length = await reader.ReadLengthPrefixAsync(prefixBudget, prefix.Token).ConfigureAwait(false);
                _ = window.GetRemaining();
                CheckReadStage(prefix);
            }

            if (!FrameReader.TryValidateLength(
                length, TransportConstants.MaxPreAuthMessageBytes, out int lengthBytes, out string? rejection))
            {
                throw new FrameProtocolException(rejection!);
            }

            TimeSpan payloadBudget = Min(timeouts.PayloadTimeout, window.GetRemaining());
            using AuthenticationDeadline payloadDeadline = new(window.Clock, payloadBudget, window.Token);
            byte[]? payload = null;
            try
            {
                payload = await reader.ReadPayloadAsync(lengthBytes, payloadBudget, payloadDeadline.Token)
                    .ConfigureAwait(false);
                long receivedAt = window.Clock.GetTimestamp();
                _ = window.GetRemaining();
                CheckReadStage(payloadDeadline);
                byte[] result = payload;
                payload = null;
                return (result, receivedAt);
            }
            finally
            {
                if (payload is not null)
                {
                    CryptographicOperations.ZeroMemory(payload);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _ = window.GetRemaining();
            throw TimeoutFailure("client-frame-timeout");
        }
    }

    private static void CheckReadStage(AuthenticationDeadline deadline)
    {
        if (deadline.IsExpired || deadline.Token.IsCancellationRequested)
        {
            throw TimeoutFailure("client-frame-timeout");
        }
    }

    private static ControlClientAuthenticationException RemoteFailure() =>
        new("client-remote-authentication-failed", "远端拒绝了认证或审批请求。");

    private static ControlClientAuthenticationException TimeoutFailure(string rejection) =>
        new(rejection, rejection == "client-approval-timeout"
            ? "等待远端审批超时，请重试。"
            : "远端认证等待超时，请重试。");

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    /// <summary>保留协议事件的原始起点；现有 deadline 提供 I/O 取消，单调余量提供接受判据。</summary>
    private sealed class ClientAuthWindow : IDisposable
    {
        private readonly TimeSpan _budget;
        private readonly long _startedAt;
        private readonly CancellationToken _callerCancellation;
        private readonly string _timeoutRejection;
        private readonly ClientAuthWindow? _parent;
        private readonly AuthenticationDeadline _deadline;

        public ClientAuthWindow(
            TimeProvider clock,
            TimeSpan budget,
            CancellationToken callerCancellation,
            string timeoutRejection,
            long? startedAt = null,
            ClientAuthWindow? parent = null)
        {
            Clock = clock;
            _budget = budget;
            _startedAt = startedAt ?? clock.GetTimestamp();
            _callerCancellation = callerCancellation;
            _timeoutRejection = timeoutRejection;
            _parent = parent;
            _deadline = new AuthenticationDeadline(clock, Remaining, parent?.Token ?? callerCancellation);
        }

        public TimeProvider Clock { get; }
        public CancellationToken Token => _deadline.Token;

        private TimeSpan Remaining => RemainingAt(Clock.GetTimestamp());

        private TimeSpan RemainingAt(long timestamp)
        {
            // 父子窗口使用同一次采样，接受边界不混入两次取时之间的偏移。
            TimeSpan elapsed = Clock.GetElapsedTime(_startedAt, timestamp);
            TimeSpan own = elapsed >= _budget ? TimeSpan.Zero : _budget - elapsed;
            return _parent is null ? own : Min(own, _parent.RemainingAt(timestamp));
        }

        public TimeSpan GetRemaining()
        {
            _callerCancellation.ThrowIfCancellationRequested();
            TimeSpan remaining = Remaining;
            if (remaining <= TimeSpan.Zero || Token.IsCancellationRequested)
            {
                throw TimeoutFailure(_timeoutRejection);
            }

            return remaining;
        }

        public void Dispose() => _deadline.Dispose();
    }
}
