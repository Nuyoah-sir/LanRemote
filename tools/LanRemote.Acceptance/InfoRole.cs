using System.Net;
using LanRemote.Core.Models;
using LanRemote.Discovery.Networking;
using Microsoft.Extensions.Logging;

namespace LanRemote.Acceptance;

/// <summary>
/// 环境自检：把「这台机器到底能不能参与验收」讲清楚。
/// </summary>
/// <remarks>
/// M2 那次验收最大的时间浪费就是两台机器不在 RFC1918 网段却没人先确认。
/// 这个角色存在的唯一目的就是在开跑之前把这件事说清楚。
/// </remarks>
internal static class InfoRole
{
    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        using AcceptanceContext context = new(LogLevel.Warning);
        await context.InitializeAsync(cancellationToken);

        DeviceIdentity identity = context.Identity;

        Console.WriteLine("deviceCode  = " + identity.DeviceCode);
        Console.WriteLine("deviceId    = " + identity.DeviceId);
        Console.WriteLine("deviceName  = " + identity.DeviceName);
        Console.WriteLine("certSha256  = " + context.Certificate.Sha256FingerprintHex);
        Console.WriteLine("configRoot  = " + context.Paths.RootDirectory);
        Console.WriteLine("udpDiscover = " + context.Config.DiscoveryPort);
        Console.WriteLine("tcpTransmit = " + context.Config.TransportPort);
        Console.WriteLine("bindings    :");

        IReadOnlyList<NetworkBinding> bindings = context.Bindings.GetBindings();
        if (bindings.Count == 0)
        {
            Console.WriteLine("  (无) —— 本机没有合格网卡，不能参与两机验收。");
        }

        foreach (NetworkBinding binding in bindings)
        {
            Console.WriteLine(
                $"  - {binding.Address}/{binding.SubnetMask} [{binding.InterfaceName}] " +
                $"{binding.InterfaceType} ifIndex={binding.InterfaceIndex}");
        }

        IReadOnlyList<IPAddress> listen = context.ListenAddresses();
        Console.WriteLine("listenOn    = " +
            (listen.Count == 0 ? "(none)" : string.Join(", ", listen)));

        Console.WriteLine(listen.Count == 0
            ? "[INFO][RESULT] outcome=FAIL reason=no-qualified-rfc1918-nic // 先用 set-lab-ip.ps1 配置 lab 网段"
            : "[INFO][RESULT] outcome=PASS // 本机可以参与两机验收");

        return listen.Count == 0 ? 2 : 0;
    }
}
