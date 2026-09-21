using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace LanRemote.Transport;

/// <summary>
/// 客户端侧的 TLS 连接器：TCP 连接 → TLS 握手 → 产出不可变身份上下文。
/// </summary>
/// <remarks>
/// <para><b>只认冻结快照</b>（TOCTOU，外部红队评审 A 桶第 2 条）：
/// <see cref="ConnectAsync"/> 的参数类型是 <see cref="ConnectionTarget"/>，
/// 它<b>不持有</b> discovery 缓存的任何引用。这不只是个约定——
/// 类型的形状本身就让「握手期间回读缓存」无法实现。改动时请不要为了省事
/// 把它换成 <c>DiscoveredDevice</c> 或缓存对象。</para>
/// <para><b>不依赖任何 TLS 默认值</b>（本机 .NET 10.0.12 实测）：
/// <c>AllowTlsResume</c> 两端默认都是 <see langword="true"/>、
/// <c>AllowRenegotiation</c> 客户端默认 <see langword="true"/>、
/// <c>EnabledSslProtocols</c> 默认是 <see cref="SslProtocols.None"/>。三个都必须显式设置。</para>
/// <para><b>不使用 SNI</b>：<c>TargetHost</c> 设为空串。证书 CN 是 <c>LanRemote-&lt;设备码&gt;</c>，
/// 把它放进 SNI 等于在明文里向整个局域网广播设备标识；而我们靠 pinning 校验，
/// 主机名校验本来就不参与安全判定。空串会跳过 SNI 与名字校验。</para>
/// </remarks>
public sealed class TlsClientConnector
{
    /// <summary>
    /// 建立一条 TLS 连接。
    /// </summary>
    /// <param name="target">连接前冻结的目标快照。</param>
    /// <param name="timeouts">时限预算；为空则用 <see cref="TransportTimeouts.Default"/>。</param>
    /// <param name="clock">时间源；为空则用 <see cref="TimeProvider.System"/>。</param>
    /// <param name="cancellationToken">外部取消。</param>
    /// <returns>已认证的连接；调用方负责释放。</returns>
    /// <exception cref="AuthenticationException">证书未通过 pinning 或不变量检查。</exception>
    /// <exception cref="OperationCanceledException">外部取消。</exception>
    /// <exception cref="TimeoutException">
    /// 连接或握手超过<b>绝对</b>时限。实际抛出的具体类型由 .NET 决定，
    /// M3 步骤 15 实测后会如实记录，不要在这里凭印象断言。
    /// </exception>
    public async Task<TlsConnection> ConnectAsync(
        ConnectionTarget target,
        TransportTimeouts? timeouts = null,
        TimeProvider? clock = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        TransportTimeouts budget = timeouts ?? TransportTimeouts.Default;
        TimeProvider effectiveClock = clock ?? TimeProvider.System;

        TcpClient? client = null;
        SslStream? stream = null;

        // 回调里捕获的结果：presentedPin 是 M3 必须交给 M4 的交付物（ADR-028）。
        byte[]? presentedPin = null;
        string? rejection = null;

        try
        {
            client = new TcpClient(target.RemoteAddress.AddressFamily);

            using (CancellationTokenSource connectCts =
                   CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectCts.CancelAfter(budget.ConnectTimeout);
                await client.ConnectAsync(target.RemoteAddress, target.Port, connectCts.Token)
                    .ConfigureAwait(false);
            }

            stream = new SslStream(
                client.GetStream(),
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (_, certificate, _, _) =>
                {
                    bool accepted = PeerCertificateValidator.TryValidate(
                        certificate,
                        target,
                        effectiveClock,
                        out byte[] pin,
                        out string? reason);

                    presentedPin = pin;
                    rejection = accepted ? null : reason;
                    return accepted;
                });

            SslClientAuthenticationOptions options = new()
            {
                // 空串 = 不发 SNI、不做主机名校验（见类型说明）。
                TargetHost = string.Empty,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                AllowTlsResume = false,
                AllowRenegotiation = false,

                // 自签名 + pinning，没有 CRL 可分发的分发点；不要依赖默认值。
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            };

            using (CancellationTokenSource handshakeCts =
                   CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                handshakeCts.CancelAfter(budget.HandshakeTimeout);
                await stream.AuthenticateAsClientAsync(options, handshakeCts.Token)
                    .ConfigureAwait(false);
            }

            if (presentedPin is null || presentedPin.Length != CertificatePin.LengthBytes)
            {
                // 走到这里说明握手成功但回调没跑——pinning 根本没发生，必须 fail closed。
                throw new AuthenticationException(
                    "TLS 握手完成但证书校验回调未产生 32 字节指纹，已按失败处理。");
            }

            if (!ConnectionIdentity.TryCreate(target, presentedPin, out ConnectionIdentity? identity)
                || identity is null)
            {
                throw new AuthenticationException("无法构造连接身份上下文，已按失败处理。");
            }

            TlsConnection connection = new(identity, client, stream);

            // 交给返回值，finally 不再释放。
            client = null;
            stream = null;
            return connection;
        }
        catch (AuthenticationException ex) when (rejection is not null)
        {
            // 把回调的拒绝原因带出来给本地日志；不要把它发给对端。
            throw new AuthenticationException(
                $"对端证书未通过校验（{rejection}）。",
                ex);
        }
        finally
        {
            stream?.Dispose();
            client?.Dispose();
        }
    }
}
