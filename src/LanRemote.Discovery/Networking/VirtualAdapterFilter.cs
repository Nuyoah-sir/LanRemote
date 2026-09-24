using System.Net.NetworkInformation;

namespace LanRemote.Discovery.Networking;

/// <summary>
/// 虚拟网卡 / VPN 适配器的保守启发式过滤。
/// </summary>
/// <remarks>
/// <para>类型过滤之后仍可能留下 Hyper-V、VMware、WireGuard、Tailscale 之类的
/// 「伪装成 Ethernet」的虚拟网卡。v1 默认排除这些明显的虚拟接口。</para>
/// <para>关键词刻意保守：只用「几乎只可能出现在虚拟适配器里」的词，
/// 不用 microsoft / intel / realtek 这类会出现在真实网卡名称里的词，否则会误杀。</para>
/// <para>关键词比较大小写不敏感；系统 Wi-Fi Direct 描述必须精确匹配，且仅豁免 virtual 关键词。
/// 此例外支持局域网接口，不识别 ICS 共享角色。</para>
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
    /// <param name="description">系统网卡描述，可为 <see langword="null"/>。</param>
    /// <param name="interfaceType">网卡类型；缺省时不豁免。</param>
    /// <param name="interfaceIndex">网卡索引；必须大于零才可豁免。</param>
    /// <returns>是否应当排除。</returns>
    public static bool IsLikelyVirtual(
        string? name,
        string? description,
        NetworkInterfaceType interfaceType = NetworkInterfaceType.Unknown,
        int interfaceIndex = 0)
    {
        bool allowVirtualToken = interfaceType == NetworkInterfaceType.Wireless80211
            && interfaceIndex > 0
            && IsWindowsWiFiDirectDescription(description);
        string haystack = string.Concat(name ?? string.Empty, " ", description ?? string.Empty);

        if (haystack.Length <= 1)
        {
            return false;
        }

        foreach (string token in VirtualTokens)
        {
            if (allowVirtualToken && token == "virtual")
            {
                continue;
            }

            if (haystack.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWindowsWiFiDirectDescription(string? description)
    {
        const string systemDescription = "Microsoft Wi-Fi Direct Virtual Adapter";
        if (string.Equals(description, systemDescription, StringComparison.Ordinal))
        {
            return true;
        }

        const string instancePrefix = systemDescription + " #";
        if (description is null || !description.StartsWith(instancePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> instance = description.AsSpan(instancePrefix.Length);
        // 实例后缀仅接受无前导零的 ASCII 正整数，不接受空白、符号或附加文本。
        if (instance.IsEmpty || instance[0] is < '1' or > '9')
        {
            return false;
        }

        foreach (char digit in instance)
        {
            if (digit is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}
