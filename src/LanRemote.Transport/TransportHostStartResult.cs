using System.Net;
using System.Net.Sockets;

namespace LanRemote.Transport;

/// <summary>某个本地地址没能 bind 上的原因。</summary>
/// <param name="Address">本地地址。</param>
/// <param name="Error">Socket 错误码；非 Socket 异常（例如地址不是 IPv4）时为 <see langword="null"/>。</param>
/// <param name="Message">异常消息，只用于本地诊断。</param>
public sealed record TransportHostBindFailure(IPAddress Address, SocketError? Error, string Message);

/// <summary>
/// Host 启动结果：哪些地址真的在听、哪些没听成。
/// </summary>
/// <param name="BoundAddresses">成功 bind 并已开始 accept 的地址。</param>
/// <param name="Failures">失败的地址与原因。</param>
/// <remarks>
/// <para><b>ADR-031：按网卡降级，不整体失败</b>。一张网卡 bind 失败（被占用、地址消失、
/// 权限问题）不应该让其它网卡也不监听——那会把「一个网卡有问题」放大成「整台机器不能被发现」。</para>
/// <para>但这条降级<b>只适用于互不重叠的地址</b>。所有 listener 都开
/// <c>ExclusiveAddressUse</c>，若某个地址与已有（更宽的）bind 重叠，
/// 实测会以 <see cref="SocketError.AccessDenied"/> 失败并被记进 <paramref name="Failures"/>——
/// 这正是我们要的「响亮失败」，不能因为它是 <c>AccessDenied</c> 就当成无关紧要的错误吞掉。</para>
/// <para><paramref name="BoundAddresses"/> 为空表示<b>一个都没听上</b>。
/// 这在「没有合格的 RFC1918 网卡」时是预期状态（与 M2 discovery 的行为一致），
/// 由调用方决定怎么告诉用户，Host 不抛异常。</para>
/// </remarks>
public sealed record TransportHostStartResult(
    IReadOnlyList<IPAddress> BoundAddresses,
    IReadOnlyList<TransportHostBindFailure> Failures)
{
    /// <summary>是否至少有一个地址在听。</summary>
    public bool IsListening => BoundAddresses.Count > 0;
}
