using System.Collections.ObjectModel;
using LanRemote.Core.Models;

namespace LanRemote.Transport;

/// <summary>认证后收到的显示目录不可变快照；仅供展示，不授予选屏、采集或输入权限。</summary>
/// <remarks>
/// 序号与时间戳是远端消息元数据，不是拓扑世代、反重放或视频帧坐标关联证明。
/// 历史快照不会随会话撤销而清除；是否仍可取得当前目录由父会话门禁决定。
/// </remarks>
public sealed class DisplayCatalogSnapshot
{
    // 仅接收目录帧严格验证、复制后生成的只读集合，不保留调用方或 wire 对象。
    internal DisplayCatalogSnapshot(long sequence, long timestampMs, ReadOnlyCollection<DisplayInfo> displays)
    {
        Sequence = sequence;
        TimestampMs = timestampMs;
        Displays = displays;
    }

    public long Sequence { get; }
    public long TimestampMs { get; }
    public IReadOnlyList<DisplayInfo> Displays { get; }
}
