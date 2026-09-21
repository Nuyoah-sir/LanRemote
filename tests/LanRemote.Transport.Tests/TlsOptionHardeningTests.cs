using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using LanRemote.Core.Abstractions;

namespace LanRemote.Transport.Tests;

/// <summary>
/// 锁死「TLS 选项必须显式设置」这件事本身（triage A-16 / A-17）。
/// </summary>
/// <remarks>
/// <para>这些开关<b>没有</b>低成本的外部可观测行为：删掉 `AllowTlsResume = false`
/// 之后，全部现有测试依然会绿。所以这里直接断言选项对象——
/// 并把「默认值是什么」也一起断言出来，作为「为什么必须显式写」的证据。</para>
/// <para>默认值三条均为本机 .NET 10.0.12 实测，非推断。</para>
/// </remarks>
public sealed class TlsOptionHardeningTests
{
    /// <summary>
    /// 默认值就是必须显式设置的理由：**不写，拿到的是我们不要的那个**。
    /// </summary>
    [Fact]
    public void Defaults_Are_What_We_Must_Not_Rely_On()
    {
        SslClientAuthenticationOptions clientDefaults = new();
        SslServerAuthenticationOptions serverDefaults = new();

        // 会话恢复：两端默认都是开着的（评审 B-21 要求：在证明回调行为之前保持关闭）。
        Assert.True(clientDefaults.AllowTlsResume);
        Assert.True(serverDefaults.AllowTlsResume);

        // 重协商：客户端默认开、服务端默认关——不对称，更不能靠默认。
        Assert.True(clientDefaults.AllowRenegotiation);
        Assert.False(serverDefaults.AllowRenegotiation);

        // 协议版本默认是 None（= 交给系统策略），不是「TLS 1.2 + 1.3」。
        Assert.Equal(SslProtocols.None, clientDefaults.EnabledSslProtocols);
        Assert.Equal(SslProtocols.None, serverDefaults.EnabledSslProtocols);
    }

    [Fact]
    public void Client_Options_Are_Explicit()
    {
        SslClientAuthenticationOptions options = TlsClientConnector.CreateClientOptions();

        Assert.False(options.AllowTlsResume);
        Assert.False(options.AllowRenegotiation);
        Assert.Equal(SslProtocols.Tls12 | SslProtocols.Tls13, options.EnabledSslProtocols);
        Assert.Equal(X509RevocationMode.NoCheck, options.CertificateRevocationCheckMode);

        // 不发 SNI：CN 是 LanRemote-<设备码>，SNI 是明文，等于向局域网广播设备标识。
        Assert.Equal(string.Empty, options.TargetHost);
    }

    [Fact]
    public async Task Server_Options_Are_Explicit()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();

        TransportHost host = new(
            new[] { IPAddress.Loopback },
            new StubSubnetPolicy(allow: true),
            certificate,
            (_, _) => Task.CompletedTask);

        await using (host)
        {
            SslServerAuthenticationOptions options = host.CreateServerOptions();

            Assert.False(options.AllowTlsResume);
            Assert.False(options.AllowRenegotiation);
            Assert.Equal(SslProtocols.Tls12 | SslProtocols.Tls13, options.EnabledSslProtocols);
            Assert.Equal(X509RevocationMode.NoCheck, options.CertificateRevocationCheckMode);

            // 没有客户端证书体系：要了也校验不了，只会把无关设备挡在外面。
            Assert.False(options.ClientCertificateRequired);
            Assert.Same(certificate, options.ServerCertificate);
        }
    }

    private sealed class StubSubnetPolicy : ISubnetPolicy
    {
        private readonly bool _allow;

        public StubSubnetPolicy(bool allow)
        {
            _allow = allow;
        }

        public bool IsAllowedPeer(IPAddress localAddress, IPAddress remoteAddress) => _allow;
    }
}
