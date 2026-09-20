using LanRemote.Core.Models;

namespace LanRemote.Core.Abstractions;

/// <summary>
/// 局域网设备发现。
/// </summary>
/// <remarks>
/// v1 仅 IPv4，只在私有 RFC1918 网卡上发送与接收；不允许任何公网发现或中继。
/// </remarks>
public interface IDiscoveryService
{
    /// <summary>观察发现到的设备变化流。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>设备更新序列。</returns>
    IAsyncEnumerable<DiscoveredDevice> WatchAsync(CancellationToken cancellationToken);

    /// <summary>启动 announce / receive / cleanup 三个后台循环。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>停止后台循环并释放 socket。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task StopAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 立即主动探测一次。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// <para>用途：UI 的「刷新」按钮，以及启动后的首次主动扫描。
    /// 只发送组播 probe 与各网卡的 directed broadcast probe，
    /// <b>不会</b>重启 socket、不会重读 DPAPI、不会重建证书、不会清除本机身份。</para>
    /// <para>与「允许被发现」无关：即使 <c>AllowDiscovery=false</c>，本机仍然可以扫描别人。</para>
    /// </remarks>
    Task ProbeAsync(CancellationToken cancellationToken);
}
