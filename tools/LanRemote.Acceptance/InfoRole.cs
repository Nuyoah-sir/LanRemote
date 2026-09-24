using System.Net;
using LanRemote.Core.Models;
using LanRemote.Discovery.Networking;
using Microsoft.Extensions.Logging;

namespace LanRemote.Acceptance;

/// <summary>
/// 本地身份与现有网络的只读检查，不验证发现、互通、认证或互联网。
/// </summary>
/// <remarks>
/// M2 那次验收最大的时间浪费就是两台机器不在 RFC1918 网段却没人先确认。
/// 这个角色存在的唯一目的就是在开跑之前把这件事说清楚。
///
/// 输出走 <see cref="AcceptanceLog"/>：这是 <c>WinExe</c>，没有控制台。
/// </remarks>
internal static class InfoRole
{
    internal const string LocalCheckScope =
        "本地 PASS 仅代表本地检查通过，不代表发现、互通、认证或互联网可用。";

    internal static string DescribeReadiness(bool ready) => (ready
        ? "本机存在合格 RFC1918 地址；对端是否同子网仍需两机核验。"
        : "未满足本地检查条件：请检查现有网络连接、合格 RFC1918 网卡或自检故障；验收器不会修改网络配置。") +
        LocalCheckScope;

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

        // outcome 用的是和退出码同一套词（见 AcceptanceOutcome.Code()）：
        // 这里是 UNMET（前置条件不满足）而不是 FAIL —— 网卡不合格不是产品缺陷，
        // 而且 headless 跑这个角色本来就退 2。文本与退出码说同一件事，
        // 才不会出现「日志说 FAIL、脚本看到 2」这种要人命的不一致。
        log.WriteLine((ready
            ? "[INFO][RESULT] outcome=PASS scope=local-only // "
            : "[INFO][RESULT] outcome=UNMET reason=no-qualified-rfc1918-nic // ") + DescribeReadiness(ready));

        return new InfoResult(
            ready,
            identity.DeviceCode,
            context.Certificate.Sha256FingerprintHex,
            listen);
    }

    /// <summary>自检结果，供 UI 顶部直接展示。</summary>
    /// <param name="Ready">本地检查是否存在合格监听地址，不代表两机验收或互联网通过。</param>
    /// <param name="DeviceCode">本机设备码（不是秘密）。</param>
    /// <param name="CertSha256">本机证书指纹（会被对端 pin）。</param>
    /// <param name="ListenAddresses">将要监听的地址。</param>
    public sealed record InfoResult(
        bool Ready,
        string DeviceCode,
        string CertSha256,
        IReadOnlyList<IPAddress> ListenAddresses);
}
