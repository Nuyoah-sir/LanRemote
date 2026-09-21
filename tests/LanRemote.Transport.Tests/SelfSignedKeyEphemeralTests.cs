using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace LanRemote.Transport.Tests;

/// <summary>
/// 记录一条本机实测结论：<c>CertificateRequest.CreateSelfSigned</c> 直接产出的证书
/// <b>不能</b>用作 Windows TLS 服务端凭据。
/// </summary>
/// <remarks>
/// <para>2026-09-21 实测（回环 + 真实 <c>SslStream</c>）：服务端在 <c>AcquireCredentialsHandle</c> 处
/// 抛 <c>AuthenticationException: Authentication failed because the platform does not support ephemeral keys.</c>，
/// inner <c>Win32Exception (0x8009030E)</c>。</para>
/// <para><b>为什么值得单独钉一个测试</b>：这个错误和 ADR-029 里 <c>EphemeralKeySet</c> 的失败<b>完全一样</b>。
/// 两者合起来说明 Schannel 拒绝的是<b>密钥的 ephemeral 属性</b>，而不是「某个叫 EphemeralKeySet 的导入 flag」。
/// 也就是说：ADR-029 只把导入 flag 改成 <c>DefaultKeySet</c> 还不够——
/// 生产签发的那一步也必须靠 PFX 往返把密钥落到非 ephemeral 的容器里，
/// 而 <c>DeviceCertificateService</c> 恰好已经这么做了。</para>
/// <para>它同时解释了一件容易踩的事：测试里「自己 <c>CreateSelfSigned</c> 一张证书当服务端」
/// 会失败，而且客户端只会看到 <c>IOException: unexpected EOF</c>，
/// 真实原因在服务端——不要只看客户端异常就下结论（<c>HANDOFF.md</c> 第 16 节）。</para>
/// </remarks>
public sealed class SelfSignedKeyEphemeralTests
{
    [Fact(Timeout = 60_000)]
    public async Task CreateSelfSigned_Certificate_Cannot_Serve_Tls_Without_Pfx_Roundtrip()
    {
        if (!OperatingSystem.IsWindows())
        {
            // 这条结论是 Windows / Schannel 的行为，其它平台不适用。
            return;
        }

        using X509Certificate2 ephemeral = TestCertificateFactory.CreateWithoutPfxRoundtrip();
        Assert.True(ephemeral.HasPrivateKey, "前置条件：CreateSelfSigned 声称自己带私钥。");

        Exception? serverFailure = null;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            Task serverTask = Task.Run(async () =>
            {
                try
                {
                    using TcpClient accepted = await listener.AcceptTcpClientAsync();
                    var stream = new SslStream(accepted.GetStream(), false);
                    await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = ephemeral,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        ClientCertificateRequired = false,
                    });
                }
                catch (Exception ex)
                {
                    serverFailure = ex;
                }
            });

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var clientStream = new SslStream(
                client.GetStream(),
                false,
                (_, _, _, _) => true);

            await Assert.ThrowsAnyAsync<Exception>(
                () => clientStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = string.Empty,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }));

            await serverTask;
        }
        finally
        {
            listener.Stop();
        }

        Assert.NotNull(serverFailure);
        Assert.IsType<AuthenticationException>(serverFailure);
        Assert.Contains("ephemeral", serverFailure.Message);
    }
}
