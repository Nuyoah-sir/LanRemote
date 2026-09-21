using System.Net;
using System.Security.Cryptography;
using Xunit;

namespace LanRemote.Transport.Tests;

/// <summary>
/// 连接身份上下文：ADR-028 要求 M4 的 transcript 绑定「实际出示的」证书指纹。
/// </summary>
public sealed class ConnectionIdentityTests
{
    private const string ValidSha = "3F4FD8C5CE4C062FDCA7CA1492BB9935AB11548F85B7A682A3A96992CC090D1F";

    private static ConnectionTarget CreateTarget()
    {
        Assert.True(ConnectionTarget.TryCreate(
            Guid.NewGuid(), IPAddress.Parse("192.168.1.20"), 45873, ValidSha, out ConnectionTarget? target));
        return target!;
    }

    [Fact]
    public void TryCreate_CapturesExpectedAndPresented()
    {
        ConnectionTarget target = CreateTarget();
        byte[] presented = Convert.FromHexString(ValidSha);

        Assert.True(ConnectionIdentity.TryCreate(target, presented, out ConnectionIdentity? identity));
        Assert.NotNull(identity);
        Assert.Equal(32, identity!.ExpectedCertSha256.Length);
        Assert.Equal(32, identity.PresentedCertSha256.Length);
        Assert.Equal(target.DeviceId, identity.DeviceId);
        Assert.Equal(IPAddress.Parse("192.168.1.20"), identity.RemoteAddress);
        Assert.Equal(45873, identity.Port);
    }

    [Fact]
    public void PinsMatch_IsTrue_WhenPresentedEqualsExpected()
    {
        ConnectionTarget target = CreateTarget();

        Assert.True(ConnectionIdentity.TryCreate(
            target, Convert.FromHexString(ValidSha), out ConnectionIdentity? identity));
        Assert.True(identity!.PinsMatch);
    }

    [Fact]
    public void PinsMatch_IsFalse_WhenAttackerPresentsDifferentCertificate()
    {
        // ADR-028 的核心场景：连到了别的证书（例如伪造的 discovery 记录把客户端指到攻击者证书）。
        ConnectionTarget target = CreateTarget();
        byte[] other = new byte[32];
        RandomNumberGenerator.Fill(other);

        Assert.True(ConnectionIdentity.TryCreate(target, other, out ConnectionIdentity? identity));
        Assert.False(identity!.PinsMatch);
    }

    [Fact]
    public void TryCreate_RejectsPresentedWithWrongLength()
    {
        ConnectionTarget target = CreateTarget();

        Assert.False(ConnectionIdentity.TryCreate(target, new byte[31], out _));
        Assert.False(ConnectionIdentity.TryCreate(target, new byte[33], out _));
        Assert.False(ConnectionIdentity.TryCreate(target, null, out _));
    }

    [Fact]
    public void Identity_IsNotAffectedByMutatingThePresentedArrayAfterCreation()
    {
        ConnectionTarget target = CreateTarget();
        byte[] presented = Convert.FromHexString(ValidSha);

        Assert.True(ConnectionIdentity.TryCreate(target, presented, out ConnectionIdentity? identity));

        presented[0] ^= 0xFF;

        // 身份上下文必须自持一份拷贝，不能引用回调里的数组。
        Assert.True(identity!.PinsMatch);
        Assert.Equal(Convert.FromHexString(ValidSha)[0], identity.PresentedCertSha256.Span[0]);
    }

    [Fact]
    public void RemoteEndPoint_ReturnsFreshObjectEachCall()
    {
        ConnectionTarget target = CreateTarget();

        Assert.True(ConnectionIdentity.TryCreate(
            target, Convert.FromHexString(ValidSha), out ConnectionIdentity? identity));

        IPEndPoint first = identity!.RemoteEndPoint;
        IPEndPoint second = identity.RemoteEndPoint;

        Assert.NotSame(first, second);
        Assert.Equal(first, second);
    }
}
