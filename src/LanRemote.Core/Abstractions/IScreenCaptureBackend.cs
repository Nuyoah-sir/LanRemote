using LanRemote.Core.Models;

namespace LanRemote.Core.Abstractions;

/// <summary>
/// 屏幕采集后端。
/// </summary>
/// <remarks>
/// v1 使用 GDI BitBlt（ADR-008），后续可能替换为 Windows.Graphics.Capture（M11）。
/// 实现必须保证：所有 GDI handle 在 <c>finally</c> 中释放、处理负坐标、失败可重试。
/// </remarks>
public interface IScreenCaptureBackend
{
    /// <summary>枚举当前可用显示器。</summary>
    /// <returns>显示器列表，顺序稳定。</returns>
    IReadOnlyList<DisplayInfo> GetDisplays();

    /// <summary>采集指定显示器的一帧。</summary>
    /// <param name="displayId">目标显示器。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>原始帧，调用方负责 <see cref="IDisposable.Dispose"/>。</returns>
    ValueTask<CapturedFrame> CaptureAsync(DisplayId displayId, CancellationToken cancellationToken);
}
