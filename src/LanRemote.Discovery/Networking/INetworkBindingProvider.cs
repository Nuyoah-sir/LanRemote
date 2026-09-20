namespace LanRemote.Discovery.Networking;

/// <summary>
/// 本机合格 IPv4 绑定的提供者。
/// </summary>
/// <remarks>
/// M2 只在启动时做一次快照并保持运行期不变；
/// 热插拔 / Wi-Fi 切换留给 M9，因此接口预留了 <see cref="Refresh"/>。
/// </remarks>
public interface INetworkBindingProvider
{
    /// <summary>取当前绑定快照。</summary>
    /// <returns>绑定列表。</returns>
    IReadOnlyList<NetworkBinding> GetBindings();

    /// <summary>重新枚举网卡并刷新快照。</summary>
    void Refresh();
}
