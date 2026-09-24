namespace LanRemote.Discovery.Networking;

/// <summary>一个接收 socket 对每个 IPv4 接口只加入同一组一次；地址绑定仍完整保留。</summary>
internal static class MulticastMembership
{
    internal static void JoinUniqueInterfaces(
        IReadOnlyList<NetworkBinding> bindings,
        Action<NetworkBinding> join)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(join);

        // 先验证完整快照，再产生任何 socket 副作用。索引 0 不能回退到默认路由；
        // 同一索引对应两个 Id（或反向冲突）表示快照不一致，不能猜测/合并。
        Dictionary<int, NetworkBinding> byIndex = new();
        Dictionary<string, int> byId = new(StringComparer.OrdinalIgnoreCase);
        List<NetworkBinding> representatives = new();
        foreach (NetworkBinding binding in bindings)
        {
            if (binding.InterfaceIndex <= 0 || string.IsNullOrWhiteSpace(binding.InterfaceId))
            {
                throw new InvalidOperationException(
                    $"发现接口身份不可用：ifIndex={binding.InterfaceIndex}, address={binding.Address}。");
            }

            if (byId.TryGetValue(binding.InterfaceId, out int previousIndex)
                && previousIndex != binding.InterfaceIndex)
            {
                throw new InvalidOperationException("发现网卡快照不一致：同一 Id 对应多个接口索引。");
            }
            byId[binding.InterfaceId] = binding.InterfaceIndex;

            if (byIndex.TryGetValue(binding.InterfaceIndex, out NetworkBinding? previous))
            {
                if (!string.Equals(previous.InterfaceId, binding.InterfaceId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("发现网卡快照不一致：同一接口索引对应多个 Id。");
                }
                continue;
            }

            byIndex.Add(binding.InterfaceIndex, binding);
            representatives.Add(binding);
        }

        foreach (NetworkBinding binding in representatives)
        {
            // 不吞加组失败；调用方记录具体接口并销毁整个半初始化 socket。
            join(binding);
        }
    }
}
