using System.Collections.Concurrent;
using LanRemote.Core.Models;
using LanRemote.Sessions;

namespace LanRemote.App.Services;

/// <summary>按真实认证会话保存画质：会话首次解析时快照主机当前档位，之后主机档位变化不影响该会话。</summary>
/// <remarks>
/// 只按会话标识保存值对象，不做认证、不接触采集或编码；非法档位不进入表。
/// 会话表随主机停止整体清空，不跨主机生命周期保留；未显式设定的会话回退工厂默认档位。
/// </remarks>
internal sealed class SessionQualityResolver
{
    private readonly ConcurrentDictionary<Guid, VideoQualitySettings> _bySession = new();
    private readonly Func<VideoQualitySettings?> _current;

    internal SessionQualityResolver(Func<VideoQualitySettings?> current)
    {
        ArgumentNullException.ThrowIfNull(current);
        _current = current;
    }

    internal int Count => _bySession.Count;

    /// <summary>解析该会话画质：已设定的优先；否则把主机当前档位快照一次后返回。</summary>
    internal VideoQualitySettings? Resolve(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("会话关联标识不能为空。", nameof(sessionId));
        }

        if (_bySession.TryGetValue(sessionId, out VideoQualitySettings? existing)) return existing;
        VideoQualitySettings? current = _current();
        // 主机未指定档位时不落表，保持工厂默认，也不是“本会话有独立档位”。
        return current is null ? null : _bySession.GetOrAdd(sessionId, current);
    }

    /// <summary>为会话显式设定画质；非法档位与空标识都拒绝，不改变其他会话。</summary>
    internal void Set(Guid sessionId, VideoQualitySettings quality)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("会话关联标识不能为空。", nameof(sessionId));
        }

        ArgumentNullException.ThrowIfNull(quality);
        FramePipeline.ValidateSettings(quality);
        _bySession[sessionId] = quality;
    }

    /// <summary>按预设名为会话显式设定画质；未知名一律拒绝，不回落到其他预设，不改变其他会话。</summary>
    internal void SetPreset(Guid sessionId, string presetName)
    {
        VideoQualitySettings quality = VideoQualityPresets.Resolve(presetName);
        Set(sessionId, quality);
    }

    internal void Clear() => _bySession.Clear();
}
