using System.Net;

namespace LanRemote.Core.Abstractions;

/// <summary>
/// 同子网连接策略。
/// </summary>
/// <remarks>
/// ADR-006 &amp; <c>04_PROTOCOL_AND_SECURITY.md</c> 第 4 节：
/// v1 把「同一个局域网」定义为「同一 IPv4 子网」，并且远端地址必须属于 RFC1918 私有空间。
/// 必须按真实掩码计算网络号，不能只比较地址前缀字符串；掩码不存在的地址一律拒绝。
/// </remarks>
public interface ISubnetPolicy
{
    /// <summary>判断远端地址是否允许连接。</summary>
    /// <param name="localAddress">监听端本地 IPv4 地址。</param>
    /// <param name="remoteAddress">远端 IPv4 地址。</param>
    /// <returns>允许则为 <see langword="true"/>。</returns>
    bool IsAllowedPeer(IPAddress localAddress, IPAddress remoteAddress);
}
