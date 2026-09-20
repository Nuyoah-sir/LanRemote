using System.Net;
using System.Net.NetworkInformation;

namespace LanRemote.Discovery.Networking;

/// <summary>
/// 一个合格的本地 IPv4 绑定。
/// </summary>
/// <param name="InterfaceId">网卡标识。</param>
/// <param name="InterfaceName">网卡名称。</param>
/// <param name="InterfaceType">网卡类型。</param>
/// <param name="Address">本机 IPv4（RFC1918）。</param>
/// <param name="SubnetMask">真实子网掩码。</param>
/// <param name="DirectedBroadcast">由 <c>address | ~mask</c> 算出的定向广播地址。</param>
/// <param name="InterfaceIndex">网卡索引，用于组播 membership 与发送选路。</param>
public sealed record NetworkBinding(
    string InterfaceId,
    string InterfaceName,
    NetworkInterfaceType InterfaceType,
    IPAddress Address,
    IPAddress SubnetMask,
    IPAddress DirectedBroadcast,
    int InterfaceIndex);

/// <summary>
/// 网卡上的一个 IPv4 单播地址。
/// </summary>
/// <param name="Address">地址。</param>
/// <param name="Ipv4Mask">对应的 IPv4 掩码；可能为 <see langword="null"/>。</param>
public sealed record Ipv4UnicastAddress(IPAddress Address, IPAddress? Ipv4Mask);

/// <summary>
/// 网卡快照。
/// </summary>
/// <remarks>
/// 存在的目的：让网卡筛选逻辑可以在单元测试里用假数据覆盖，
/// 而不必真的去改 Windows 网卡状态。
/// </remarks>
/// <param name="Id">网卡 Id。</param>
/// <param name="Name">网卡名称。</param>
/// <param name="Description">网卡描述。</param>
/// <param name="InterfaceType">网卡类型。</param>
/// <param name="OperationalStatus">运行状态。</param>
/// <param name="InterfaceIndex">网卡索引。</param>
/// <param name="UnicastAddresses">该网卡上的单播地址。</param>
public sealed record NetworkInterfaceSnapshot(
    string Id,
    string Name,
    string Description,
    NetworkInterfaceType InterfaceType,
    OperationalStatus OperationalStatus,
    int InterfaceIndex,
    IReadOnlyList<Ipv4UnicastAddress> UnicastAddresses);
