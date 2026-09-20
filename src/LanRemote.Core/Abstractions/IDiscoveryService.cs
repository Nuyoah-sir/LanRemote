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
}
