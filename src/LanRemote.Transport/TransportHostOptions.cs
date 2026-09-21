using System.Net.Sockets;

namespace LanRemote.Transport;

/// <summary>
/// <see cref="TransportHost"/> 的可调参数。
/// </summary>
/// <remarks>
/// <b>数值是初始值，不是实测结论</b>：步骤 15 实测取消延迟与容量之后再回来调整，
/// 并同步更新 <c>HANDOFF.md</c>。
/// </remarks>
public sealed class TransportHostOptions
{
    /// <summary>TCP 监听端口（默认 <see cref="TransportConstants.Port"/>）。</summary>
    public int Port { get; init; } = TransportConstants.Port;

    /// <summary>
    /// 全局同时连接数上限。
    /// </summary>
    /// <remarks>自用工具，同局域网里同时被连的台数是个位数。</remarks>
    public int MaxConnections { get; init; } = 8;

    /// <summary>
    /// 单个源 IP 的同时连接数上限（小于 <see cref="MaxConnections"/>）。
    /// </summary>
    public int MaxConnectionsPerAddress { get; init; } = 2;

    /// <summary>各阶段绝对时限。</summary>
    public TransportTimeouts Timeouts { get; init; } = TransportTimeouts.Default;

    /// <summary>停机预算：取消 + 强制释放 + join 全部在这个预算内完成。</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// <c>listen(2)</c> 的 backlog。
    /// </summary>
    /// <remarks>
    /// <b>它不是准入防线</b>：只是「已建连未 accept」的队列长度，超了由操作系统静默丢 SYN，
    /// 既不计数也不限流。真正的准入是 <see cref="ConnectionAdmissionLimiter"/>。
    /// </remarks>
    public int ListenBacklog { get; init; } = 8;
}
