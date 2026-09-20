using System.Net;

namespace LanRemote.Discovery.Protocol;

/// <summary>
/// probe 回应目标端点的计算。
/// </summary>
/// <remarks>
/// <para><b>为什么必须存在这个类</b>：发送 probe 的 socket 绑定的是
/// <c>binding.Address:0</c>，也就是<b>随机临时端口</b>，例如 <c>192.168.1.20:53742</c>。
/// 而 LanRemote 的 discovery receiver 永远监听固定的 <see cref="DiscoveryConstants.Port"/>。
/// 因此 unicast 回应<b>绝不能</b>发回 probe 报文的源端口——那样回应只会打到一个已经没人听的临时端口上，
/// 表现为「刷新按钮点了没反应」。</para>
/// <para>回应目标是 <c>源地址 : DiscoveryConstants.Port</c>，端口恒定，只有地址取自报文来源。</para>
/// <para>这个类型是 internal：它不是对外协议的一部分，只是为了把「端口选择」这条不变量
/// 变成可单元测试的纯函数（测试工程通过 <c>InternalsVisibleTo</c> 访问）。</para>
/// </remarks>
internal static class DiscoveryReplyTarget
{
    /// <summary>
    /// 由 probe 的 UDP 来源端点算出 unicast 回应的目标端点。
    /// </summary>
    /// <param name="probeSource">probe 报文的 UDP 来源端点（地址权威，<b>端口不可用</b>）。</param>
    /// <returns>回应目标：源地址 + 固定发现端口。</returns>
    internal static IPEndPoint ForProbe(IPEndPoint probeSource)
    {
        ArgumentNullException.ThrowIfNull(probeSource);

        if (probeSource.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new ArgumentException(
                "probe 来源必须是 IPv4 端点。",
                nameof(probeSource));
        }

        return new IPEndPoint(probeSource.Address, DiscoveryConstants.Port);
    }
}
