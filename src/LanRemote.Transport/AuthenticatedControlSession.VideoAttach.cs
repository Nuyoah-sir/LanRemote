using System.Net;
using System.Runtime.ExceptionServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport;

public sealed partial class AuthenticatedControlSession
{
    /// <summary>
    /// 封闭的内部真实附着入口；不公开 Stream、token 或可替换的目标。
    /// 第二 TLS 使用默认 TCP/TLS 分段时限，叠加父 success 原锚点的附着总预算。
    /// </summary>
    internal Task<ClientVideoLifetime> AttachVideoCoreAsync(CancellationToken cancellationToken = default) =>
        AttachVideoCoreAsync(static (target, timeouts, clock, token) =>
            new TlsClientConnector().ConnectAsync(target, timeouts, clock, token), cancellationToken);

    /// <summary>
    /// 实例级连接接缝；工厂必须返回新建且独占的第二连接，不得返回父连接或共享连接。
    /// 身份校验、敏感 hello 和严格 ACK 均不可替换。仅成功登记者拥有本 attempt 的清理权。
    /// </summary>
    internal Task<ClientVideoLifetime> AttachVideoCoreAsync(
        Func<ConnectionTarget, TransportTimeouts, TimeProvider, CancellationToken, Task<TlsConnection>> connectTls,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectTls);
        Task<ClientVideoLifetime> originalAttach;
        ClientVideoLifetime child;
        lock (_gate)
        {
            CheckCanStartVideo(cancellationToken);
            // 从已交付 Control 身份重建独立目标；不查询发现缓存，也不共享可变 IPAddress。
            if (!ConnectionTarget.TryCreate(Identity.DeviceId,
                    new IPAddress(Identity.RemoteAddress.GetAddressBytes()), Identity.Port,
                    Convert.ToHexString(Identity.ExpectedCertSha256.Span), out ConnectionTarget? target) || target is null)
                throw new AuthenticationException("控制会话冻结目标无效。");

            // 必须直接返回原连接任务，避免 async 包装丢失 Task 的兄弟异常。
            originalAttach = StartVideoLifetimeAsync(
                token => connectTls(target, TransportTimeouts.Default, _clock, token),
                (connection, token) => InitializeVideoAsync(connection, target, token), cancellationToken);
            child = _videoLifetime!;
        }
        // 登记失败不进入此处；clock 重入/重复入口的输家不能停止别人的 child。
        return AwaitVideoAttachAsync(originalAttach, child);
    }

    private static async Task<ClientVideoLifetime> AwaitVideoAttachAsync(
        Task<ClientVideoLifetime> originalAttach, ClientVideoLifetime child)
    {
        try
        {
            // 底座已经完成联合提交；成功后不追加 caller/预算终检。
            return await originalAttach.ConfigureAwait(false);
        }
        catch (Exception primary)
        {
            Exception? joinError = null;
            try { await child.StopAndJoinAsync().ConfigureAwait(false); }
            catch (Exception error) { joinError = error; }

            // 原 attach 退出后才等待自己的完整 join，原 connect/init 内从不自 join。
            // 稳定诊断优先，保留原树；迟到取消不追溯替换已有业务失败。
            List<Exception> roots = [];
            foreach (Exception error in child.LifetimeErrors) AddVideoFailureRoot(roots, error);
            AddVideoFailureRoot(roots, primary);
            if (joinError is not null) AddVideoFailureRoot(roots, joinError);
            if (roots.Count == 1) ExceptionDispatchInfo.Capture(roots[0]).Throw();
            throw new AggregateException("视频附着及收尾失败。", roots);
        }
    }

    private static void AddVideoFailureRoot(List<Exception> roots, Exception candidate)
    {
        if (roots.Any(root => ContainsReference(root, candidate))) return;
        // 新根可能反过来包含旧根；仅合并顶层，原 Aggregate 的结构/重复叶子保持不变。
        roots.RemoveAll(root => ContainsReference(candidate, root));
        roots.Add(candidate);
    }

    private async Task InitializeVideoAsync(TlsConnection connection, ConnectionTarget target, CancellationToken token)
    {
        ConnectionIdentity videoIdentity = connection.Identity;
        if (!videoIdentity.PinsMatch
            || videoIdentity.DeviceId != target.DeviceId
            || !videoIdentity.RemoteAddress.Equals(target.RemoteAddress)
            || videoIdentity.Port != target.Port
            || !CertificatePin.Matches(videoIdentity.ExpectedCertSha256.Span, target.ExpectedCertSha256.Span))
            throw new AuthenticationException("视频 TLS 身份与控制会话冻结目标不一致。");

        // 先取得借用流，避免 getter 失败时遗留已生成的敏感 wire；连接始终由 child 独占关闭。
        Stream stream = connection.Stream;
        byte[]? wire = null;
        try
        {
            wire = CreateVideoHelloWire(videoIdentity, token);
            Task write = SensitiveFrameWriter.WriteOwnedFrameAsync(
                stream, wire, TransportConstants.MaxPreAuthMessageBytes, token);
            wire = null; // writer 接管原数组；await 前放弃所有权，不因取消提前擦除原 I/O 的 Memory。
            await write.ConfigureAwait(false);
        }
        finally
        {
            if (wire is not null) CryptographicOperations.ZeroMemory(wire);
        }

        FrameReader reader = new(stream);
        uint prefix = await reader.ReadLengthPrefixAsync(token).ConfigureAwait(false);
        if (!FrameReader.TryValidateLength(prefix, VideoAttachAckFrame.MaxPayloadByteLength,
                out int length, out string? rejection))
            throw new FrameProtocolException(rejection!);
        byte[] payload = await reader.ReadPayloadAsync(length, token).ConfigureAwait(false);
        try
        {
            if (!VideoAttachAckFrame.TryParse(payload, out VideoAttachAckFrame? ack, out rejection))
                throw new FrameProtocolException(rejection!);
            if (ack.SessionId != SessionId)
                throw new FrameProtocolException("video-attach-ack-session-mismatch");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
        // 只消费一帧 ACK；不探 EOF、不 drain、不预读后续前缀。第二 ACK 交由视频 reader 拒绝。
    }

    private byte[] CreateVideoHelloWire(ConnectionIdentity videoIdentity, CancellationToken token)
    {
        Span<byte> nonce = stackalloc byte[VideoAttachProof.NonceByteLength];
        byte[]? proof = null;
        try
        {
            RandomNumberGenerator.Fill(nonce);
            // 使用真实第二 TLS 回调生成的 presented pin，不能用 expected pin 代替。
            proof = CreateVideoAttachProof(nonce, videoIdentity.PresentedCertSha256.Span, token);
            return VideoHelloWire.SerializeFrame(SessionId, nonce, proof);
        }
        finally
        {
            if (proof is not null) CryptographicOperations.ZeroMemory(proof);
            CryptographicOperations.ZeroMemory(nonce);
            // 仅清理此 helper 自有缓冲区；不声称既有 proof transcript 或 TLS 内部副本已擦除。
        }
    }
}
