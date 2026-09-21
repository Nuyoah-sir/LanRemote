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
///
/// 输出走 <see cref="AcceptanceLog"/>：这是 <c>WinExe</c>，没有控制台。
/// </remarks>
internal static class InfoRole
{
    public static async Task<InfoResult> RunAsync(
        AcceptanceRun run,
        CancellationToken cancellationToken)
    {
        AcceptanceLog log = run.Log;

        await using AcceptanceContext context = new(LogLevel.Warning, log.WriteLine);
        await context.InitializeAsync(cancellationToken);

        DeviceIdentity identity = context.Identity;
        IReadOnlyList<NetworkBinding> bindings = context.Bindings.GetBindings();
        IReadOnlyList<IPAddress> listen = context.ListenAddresses();

        run.WriteHeader(timeouts: null);
        run.WriteIdentity(identity, context.Certificate, listen);

        log.WriteLine("deviceName  = " + identity.DeviceName);
        log.WriteLine("configRoot  = " + context.Paths.RootDirectory);
        log.WriteLine("udpDiscover = " + context.Config.DiscoveryPort);
        log.WriteLine("tcpTransmit = " + context.Config.TransportPort);
        log.WriteLine("bindings    :");

        if (bindings.Count == 0)
        {
            log.WriteLine("  (无) —— 本机没有合格网卡，不能参与两机验收。");
        }

        foreach (NetworkBinding binding in bindings)
        {
            log.WriteLine(
                $"  - {binding.Address}/{binding.SubnetMask} [{binding.InterfaceName}] " +
                $"{binding.InterfaceType} ifIndex={binding.InterfaceIndex}");
        }

        bool ready = listen.Count > 0;

        log.WriteLine(ready
            ? "[INFO][RESULT] outcome=PASS // 本机可以参与两机验收"
            : "[INFO][RESULT] outcome=FAIL reason=no-qualified-rfc1918-nic // 先用 set-lab-ip.ps1 配置 lab 网段");

        return new InfoResult(
            ready,
            identity.DeviceCode,
            context.Certificate.Sha256FingerprintHex,
            listen);
    }

    /// <summary>自检结果，供 UI 顶部直接展示。</summary>
    /// <param name="Ready">本机能否参与验收。</param>
    /// <param name="DeviceCode">本机设备码（不是秘密）。</param>
    /// <param name="CertSha256">本机证书指纹（会被对端 pin）。</param>
    /// <param name="ListenAddresses">将要监听的地址。</param>
    public sealed record InfoResult(
        bool Ready,
        string DeviceCode,
        string CertSha256,
        IReadOnlyList<IPAddress> ListenAddresses);
}
