namespace LanRemote.Discovery.Networking;

/// <summary>
/// 虚拟网卡 / VPN 适配器的保守启发式过滤。
/// </summary>
/// <remarks>
/// <para>类型过滤之后仍可能留下 Hyper-V、VMware、WireGuard、Tailscale 之类的
/// 「伪装成 Ethernet」的虚拟网卡。v1 默认排除这些明显的虚拟接口。</para>
/// <para>关键词刻意保守：只用「几乎只可能出现在虚拟适配器里」的词，
/// 不用 microsoft / intel / realtek 这类会出现在真实网卡名称里的词，否则会误杀。</para>
/// <para>比较一律大小写不敏感。</para>
/// <para>将来若要支持「通过 VPN 的局域网」，应另开一个明确的实验性设置，而不是放宽这里。</para>
/// </remarks>
public static class VirtualAdapterFilter
{
    /// <summary>被判定为虚拟适配器时可能出现的关键词。</summary>
    public static IReadOnlyList<string> VirtualTokens { get; } = new[]
    {
        "virtual",
        "hyper-v",
        "vethernet",
        "vmware",
        "virtualbox",
        "wireguard",
        "wintun",
        "tailscale",
        "zerotier",
        "tap",
        "tunnel",
        "vpn",
    };

    /// <summary>判断名称/描述是否像虚拟适配器。</summary>
    /// <param name="name">网卡名称，可为 <see langword="null"/>。</param>
    /// <param name="description">网卡描述，可为 <see langword="null"/>。</param>
    /// <returns>是否应当排除。</returns>
    public static bool IsLikelyVirtual(string? name, string? description)
    {
        string haystack = string.Concat(name ?? string.Empty, " ", description ?? string.Empty);

        if (haystack.Length <= 1)
        {
            return false;
        }

        foreach (string token in VirtualTokens)
        {
            if (haystack.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
