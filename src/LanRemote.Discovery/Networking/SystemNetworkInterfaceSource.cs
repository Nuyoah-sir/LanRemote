using System.Net;
using System.Net.NetworkInformation;

namespace LanRemote.Discovery.Networking;

/// <summary>
/// 网卡枚举来源。
/// </summary>
/// <remarks>抽象出来的唯一理由：让 <see cref="NetworkInterfaceSelector"/> 可以被单元测试覆盖。</remarks>
public interface INetworkInterfaceSource
{
    /// <summary>枚举当前所有网卡的快照。</summary>
    /// <returns>网卡快照列表。</returns>
    IReadOnlyList<NetworkInterfaceSnapshot> GetInterfaces();
}

/// <summary>
/// 基于 <see cref="NetworkInterface.GetAllNetworkInterfaces"/> 的真实来源。
/// </summary>
public sealed class SystemNetworkInterfaceSource : INetworkInterfaceSource
{
    /// <inheritdoc />
    public IReadOnlyList<NetworkInterfaceSnapshot> GetInterfaces()
    {
        List<NetworkInterfaceSnapshot> snapshots = new();

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                // 某些虚拟/异常网卡取属性会抛异常，跳过即可，不影响其它网卡。
                continue;
            }

            List<Ipv4UnicastAddress> unicastAddresses = new();
            foreach (UnicastIPAddressInformation info in properties.UnicastAddresses)
            {
                if (info.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    continue;
                }

                IPAddress? mask = SafeMask(info);
                unicastAddresses.Add(new Ipv4UnicastAddress(info.Address, mask));
            }

            snapshots.Add(new NetworkInterfaceSnapshot(
                nic.Id,
                nic.Name,
                nic.Description,
                nic.NetworkInterfaceType,
                nic.OperationalStatus,
                SafeInterfaceIndex(properties),
                unicastAddresses));
        }

        return snapshots;
    }

    private static IPAddress? SafeMask(UnicastIPAddressInformation info)
    {
        try
        {
            return info.IPv4Mask;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private static int SafeInterfaceIndex(IPInterfaceProperties properties)
    {
        try
        {
            return properties.GetIPv4Properties()?.Index ?? 0;
        }
        catch (NetworkInformationException)
        {
            return 0;
        }
    }
}
