using LanRemote.Core.Models;
using LanRemote.Sessions;

namespace LanRemote.App.Services;

/// <summary>
/// 命名画质预设：把「预设名」与 <see cref="VideoQualitySettings"/> 的合法档位一一对应，
/// 供界面选择与自动画质控制器按名选档。
/// </summary>
/// <remarks>
/// 档位数值以 <see cref="VideoQualitySettings"/> 的三个静态预设为唯一事实来源，本表不重复定义数值。
/// 解析是严格的：未知名一律拒绝，<b>不静默回落到其他预设</b>——这与
/// <see cref="VideoQualitySettings.Clamp"/> 的宽松钳制语义不同，后者保持原行为不变。
/// 名称按序数精确匹配，不忽略大小写、不裁剪空白。
/// </remarks>
internal static class VideoQualityPresets
{
    private static readonly (string Name, VideoQualitySettings Settings)[] All =
    [
        ("low-latency", VideoQualitySettings.LowLatency),
        ("balanced", VideoQualitySettings.Balanced),
        ("high-quality", VideoQualitySettings.HighQuality),
    ];

    /// <summary>全部预设名，顺序稳定且互不重复。</summary>
    internal static IReadOnlyList<string> Names { get; } = Array.ConvertAll(All, item => item.Name);

    /// <summary>按预设名解析档位；未知名或空输入抛异常，绝不回落。</summary>
    internal static VideoQualitySettings Resolve(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        VideoQualitySettings settings = Find(name)
            ?? throw new ArgumentOutOfRangeException(nameof(name), name, "未知的画质预设名称。");
        FramePipeline.ValidateSettings(settings);
        return settings;
    }

    /// <summary>尝试按预设名解析；未知名返回 false 且输出为 null，不抛、不回落。</summary>
    internal static bool TryResolve(string name, out VideoQualitySettings? settings)
    {
        settings = Find(name);
        if (settings is null)
        {
            return false;
        }

        FramePipeline.ValidateSettings(settings);
        return true;
    }

    /// <summary>反查该档位对应的预设名；不属于任何预设（例如手工档位）时返回 null。</summary>
    internal static string? NameOf(VideoQualitySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        foreach ((string name, VideoQualitySettings candidate) in All)
        {
            if (candidate == settings)
            {
                return name;
            }
        }

        return null;
    }

    private static VideoQualitySettings? Find(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        foreach ((string candidate, VideoQualitySettings settings) in All)
        {
            if (string.Equals(candidate, name, StringComparison.Ordinal))
            {
                return settings;
            }
        }

        return null;
    }
}
