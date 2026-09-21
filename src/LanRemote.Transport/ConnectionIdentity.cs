using System.Net;

namespace LanRemote.Transport;

/// <summary>
/// 一条 TLS 连接的<b>不可变</b>身份上下文：连的是谁、期望哪个证书、实际出示了哪个证书。
/// </summary>
/// <remarks>
/// <para><b>ADR-028（M3 → M4 契约）</b>：M4 的 canonical transcript <b>必须</b>包含
/// <see cref="PresentedCertSha256"/>（TLS 实际出示证书的指纹），并且客户端必须校验
/// 它等于连接时冻结的 <see cref="ExpectedCertSha256"/>。只绑定期望指纹是不够的——
/// 那样攻击者可以在一条 TLS 连接上收下 nonce、在另一条连接上把 challenge/proof
/// 中继给真正的服务器（凭据中继）。</para>
/// <para><see cref="PresentedCertSha256"/> 在 TLS 证书校验回调里捕获，此后不可变。
/// 它是 <b>M3 的交付物</b>，不是 M4 的内部细节。</para>
/// <para>不保存 <see cref="IPEndPoint"/> 实例：<c>IPEndPoint.Port</c> 可写，
/// 共享出去等于把可变状态交给调用方。这里保存地址与端口，需要时才构造端点对象。</para>
/// </remarks>
public sealed class ConnectionIdentity
{
    private readonly byte[] _expectedCertSha256;
    private readonly byte[] _presentedCertSha256;

    private ConnectionIdentity(
        Guid deviceId,
        IPAddress remoteAddress,
        int port,
        byte[] expectedCertSha256,
        byte[] presentedCertSha256)
    {
        DeviceId = deviceId;
        RemoteAddress = remoteAddress;
        Port = port;
        _expectedCertSha256 = expectedCertSha256;
        _presentedCertSha256 = presentedCertSha256;
    }

    /// <summary>对端设备号。</summary>
    public Guid DeviceId { get; }

    /// <summary>对端 IPv4 地址。</summary>
    public IPAddress RemoteAddress { get; }

    /// <summary>对端 TCP 端口。</summary>
    public int Port { get; }

    /// <summary>连接前冻结的期望指纹（32 字节）。</summary>
    public ReadOnlyMemory<byte> ExpectedCertSha256 => _expectedCertSha256;

    /// <summary>TLS 握手时实际出示证书的指纹（32 字节），由校验回调捕获。</summary>
    public ReadOnlyMemory<byte> PresentedCertSha256 => _presentedCertSha256;

    /// <summary>对端端点；每次调用返回新对象，不共享可变状态。</summary>
    public IPEndPoint RemoteEndPoint => new(RemoteAddress, Port);

    /// <summary>期望指纹与实际指纹是否一致（定长时间比较）。</summary>
    public bool PinsMatch => CertificatePin.Matches(_expectedCertSha256, _presentedCertSha256);

    /// <summary>
    /// 创建身份上下文。
    /// </summary>
    /// <param name="target">连接前冻结的目标快照。</param>
    /// <param name="presentedCertSha256">TLS 校验回调里算出的实际指纹。</param>
    /// <param name="identity">成功时为不可变上下文；失败为 <see langword="null"/>。</param>
    /// <returns>两个指纹都是 32 字节则为 <see langword="true"/>。</returns>
    public static bool TryCreate(
        ConnectionTarget target,
        byte[]? presentedCertSha256,
        out ConnectionIdentity? identity)
    {
        ArgumentNullException.ThrowIfNull(target);
        identity = null;

        if (presentedCertSha256 is null || presentedCertSha256.Length != CertificatePin.LengthBytes)
        {
            return false;
        }

        byte[] expected = target.ExpectedCertSha256.ToArray();
        byte[] presented = presentedCertSha256.AsSpan().ToArray();

        identity = new ConnectionIdentity(
            target.DeviceId,
            target.RemoteAddress,
            target.Port,
            expected,
            presented);
        return true;
    }
}
