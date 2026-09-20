namespace LanRemote.Discovery.Networking;

/// <summary>
/// 基于 <see cref="INetworkInterfaceSource"/> 的绑定提供者。
/// </summary>
public sealed class LocalNetworkBindingProvider : INetworkBindingProvider
{
    private readonly INetworkInterfaceSource _source;
    private readonly object _gate = new();

    private IReadOnlyList<NetworkBinding> _bindings;

    /// <summary>构造时立即做一次网卡快照。</summary>
    /// <param name="source">网卡来源。</param>
    public LocalNetworkBindingProvider(INetworkInterfaceSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _bindings = NetworkInterfaceSelector.Select(source.GetInterfaces());
    }

    /// <inheritdoc />
    public IReadOnlyList<NetworkBinding> GetBindings()
    {
        lock (_gate)
        {
            return _bindings;
        }
    }

    /// <inheritdoc />
    public void Refresh()
    {
        IReadOnlyList<NetworkBinding> next = NetworkInterfaceSelector.Select(_source.GetInterfaces());

        lock (_gate)
        {
            _bindings = next;
        }
    }
}
