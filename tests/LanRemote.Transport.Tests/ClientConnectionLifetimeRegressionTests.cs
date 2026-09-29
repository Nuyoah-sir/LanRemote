using System.Net;
using System.Net.Security;
using System.Net.Sockets;

namespace LanRemote.Transport.Tests;

public sealed class ClientConnectionLifetimeRegressionTests
{
    [Fact]
    public void Dispose_WhenSslThrows_StillAttemptsClientDisposal()
    {
        ConnectionIdentity identity = CreateIdentity();
        IOException sslError = new("测试 SSL 释放失败");
        ProbeTcpClient client = new();
        ProbeSslStream ssl = new(sslError);

        try
        {
            TlsConnection connection = new(identity, client, ssl);

            Exception? observed = Record.Exception(connection.Dispose);

            Assert.Equal(1, ssl.DisposeCount);
            // 必须在夹具兜底清理之前观察，不能由 finally 替生产代码完成释放后再断言。
            Assert.Equal(1, client.DisposeCount);
            AssertOriginalErrorObserved(observed, sslError);
        }
        finally
        {
            try
            {
                ssl.DisposeForCleanup();
            }
            finally
            {
                client.DisposeForCleanup();
            }
        }
    }

    [Fact]
    public void Dispose_WhenSslAndClientThrow_PreservesBothOriginalErrors()
    {
        ConnectionIdentity identity = CreateIdentity();
        IOException sslError = new("测试 SSL 原始释放错误");
        InvalidOperationException clientError = new("测试 client 原始释放错误");
        ProbeTcpClient client = new(clientError);
        ProbeSslStream ssl = new(sslError);

        try
        {
            TlsConnection connection = new(identity, client, ssl);

            Exception? observed = Record.Exception(connection.Dispose);

            AssertOriginalErrorObserved(observed, sslError);
            AssertOriginalErrorObserved(observed, clientError);
            Assert.Equal(1, ssl.DisposeCount);
            Assert.Equal(1, client.DisposeCount);
        }
        finally
        {
            try
            {
                ssl.DisposeForCleanup();
            }
            finally
            {
                client.DisposeForCleanup();
            }
        }
    }

    private static void AssertOriginalErrorObserved(Exception? observed, Exception expected)
    {
        if (observed is AggregateException aggregate)
        {
            Assert.Contains(aggregate.Flatten().InnerExceptions, error => ReferenceEquals(expected, error));
        }
        else
        {
            Assert.Same(expected, observed);
        }
    }

    // 仅构造未连接的本地资源，不依赖网络、证书生成或真实 TLS 握手。
    private static ConnectionIdentity CreateIdentity()
    {
        byte[] pin = Convert.FromHexString(
            "808182838485868788898A8B8C8D8E8F909192939495969798999A9B9C9D9E9F");
        Assert.True(ConnectionTarget.TryCreate(
            new Guid("11111111-2222-3333-4444-555555555555"),
            IPAddress.Loopback, 12345, Convert.ToHexString(pin), out ConnectionTarget? target));
        Assert.True(ConnectionIdentity.TryCreate(target!, pin, out ConnectionIdentity? identity));
        return identity!;
    }

    private sealed class ProbeTcpClient(Exception? disposeError = null) : TcpClient
    {
        internal int DisposeCount { get; private set; }

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    DisposeCount++;
                    if (disposeError is not null)
                    {
                        throw disposeError;
                    }
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }

        // 绕过故障注入与计数，即使生产释放跳过 client，夹具也不会泄漏 socket。
        internal void DisposeForCleanup() => base.Dispose(true);
    }

    private sealed class ProbeSslStream(Exception disposeError)
        : SslStream(new MemoryStream(), leaveInnerStreamOpen: false)
    {
        internal int DisposeCount { get; private set; }

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    DisposeCount++;
                    throw disposeError;
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }

        // 清理不再抛注入错误，避免覆盖测试断言；基类同时释放内部 MemoryStream。
        internal void DisposeForCleanup() => base.Dispose(true);
    }
}
