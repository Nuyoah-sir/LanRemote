using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LanRemote.Security.Certificates;
using LanRemote.Security.Secrets;
using Xunit;

namespace LanRemote.Security.Tests;

/// <summary>
/// 真实 SslStream 握手：证明 <see cref="DeviceCertificateService"/> 产出的证书与
/// <see cref="DeviceCertificateService.ImportFlags"/> 满足 TLS <b>服务端</b>要求。
/// </summary>
/// <remarks>
/// <para>这是 ADR-018 遗留的强制要求，也是 ADR-029 的守护测试：
/// ADR-018 选的 <c>EphemeralKeySet</c> 在 Windows 上用作服务端时 9/9 失败
/// （<c>AuthenticationException: ... platform does not support ephemeral keys.</c>）。
/// 只靠「单元测试没报错」发现不了——必须先有真实握手。</para>
/// <para>本测试跑在回环地址上，不做任何网络和端口暴露以外的操作；
/// 证书与私钥仍由 DPAPI 临时目录提供（<see cref="TempSecretRoot"/>）。</para>
/// </remarks>
public sealed class DeviceCertificateTlsHandshakeTests : IDisposable
{
    private readonly TempSecretRoot _root = new();
    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (IDisposable disposable in _disposables)
        {
            disposable.Dispose();
        }

        _root.Dispose();
    }

    [Fact(Timeout = 60_000)]
    public async Task DeviceCertificate_Works_As_Tls_Server_Certificate_Over_Loopback()
    {
        DpapiSecretVault vault = new(_root.Paths);
        DeviceCertificateService certificates = new(vault);
        _disposables.Add(certificates);

        DeviceCertificate device = await certificates.GetOrCreateAsync();

        // 期望指纹：连之前就解码成 32 字节（M3 的 pin 比较必须是字节对字节，不是 hex 字符串）。
        byte[] expectedPin = Convert.FromHexString(device.Sha256FingerprintHex);
        Assert.Equal(32, expectedPin.Length);

        bool callbackRan = false;
        bool pinMatched = false;

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Exception? serverFailure = null;
        Task serverTask = Task.Run(async () =>
        {
            try
            {
                using TcpClient accepted = await listener.AcceptTcpClientAsync();
                using var ssl = new SslStream(accepted.GetStream(), false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = device.Certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    AllowTlsResume = false,
                    AllowRenegotiation = false,
                    ClientCertificateRequired = false,
                });

                // 读一帧、回一帧：证明握手之后流真的可用。
                byte[] header = await ReadExactAsync(ssl, 4);
                uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
                byte[] body = await ReadExactAsync(ssl, (int)length);

                byte[] response = System.Text.Encoding.UTF8.GetBytes("pong:" + System.Text.Encoding.UTF8.GetString(body));
                byte[] responseHeader = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(responseHeader, (uint)response.Length);
                await ssl.WriteAsync(responseHeader);
                await ssl.WriteAsync(response);
                await ssl.FlushAsync();
            }
            catch (Exception ex)
            {
                // 客户端只能看到 IOException: unexpected EOF，真实原因在这里——必须两边都捕获。
                serverFailure = ex;
            }
        });

        SslProtocols negotiated = SslProtocols.None;
        string echo;
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var clientSsl = new SslStream(
                client.GetStream(),
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (_, certificate, _, _) =>
                {
                    callbackRan = true;
                    if (certificate is null)
                    {
                        return false;
                    }

                    // .NET 10 的回调参数是 X509Certificate（基类），没有 RawData，
                    // 必须用 GetRawCertData()（或转型成 X509Certificate2）。
                    byte[] presented = SHA256.HashData(certificate.GetRawCertData());
                    pinMatched = CryptographicOperations.FixedTimeEquals(presented, expectedPin);
                    return pinMatched;
                });

            await clientSsl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                AllowTlsResume = false,
                AllowRenegotiation = false,
            });

            negotiated = clientSsl.SslProtocol;

            byte[] payload = System.Text.Encoding.UTF8.GetBytes("hello-lanremote");
            byte[] outHeader = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(outHeader, (uint)payload.Length);
            await clientSsl.WriteAsync(outHeader);
            await clientSsl.WriteAsync(payload);
            await clientSsl.FlushAsync();

            byte[] responseHeader = await ReadExactAsync(clientSsl, 4);
            uint responseLength = BinaryPrimitives.ReadUInt32BigEndian(responseHeader);
            byte[] responseBody = await ReadExactAsync(clientSsl, (int)responseLength);
            echo = System.Text.Encoding.UTF8.GetString(responseBody);
        }

        await serverTask;

        Assert.True(
            callbackRan,
            "证书校验回调没有被执行——pinning 根本没机会生效。");
        Assert.True(
            pinMatched,
            "出示证书的 SHA-256 与设备指纹不一致（pinning 失败）。");
        Assert.Null(serverFailure);
        Assert.Contains(negotiated, new[] { SslProtocols.Tls12, SslProtocols.Tls13 });
        Assert.Equal("pong:hello-lanremote", echo);
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count)
    {
        byte[] buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read, count - read));
            if (n == 0)
            {
                throw new EndOfStreamException($"EOF after {read}/{count} bytes.");
            }

            read += n;
        }

        return buffer;
    }
}
