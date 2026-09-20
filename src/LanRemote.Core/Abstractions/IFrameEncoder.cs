using LanRemote.Core.Models;

namespace LanRemote.Core.Abstractions;

/// <summary>
/// 帧编码器。
/// </summary>
/// <remarks>
/// v1 为 JPEG（ADR-007）。编码器不得在 UI 线程执行，也不得自身持有没有上限的内部队列。
/// </remarks>
public interface IFrameEncoder
{
    /// <summary>按画质设置缩放并编码一帧。</summary>
    /// <param name="frame">原始帧，所有权仍归调用方。</param>
    /// <param name="settings">画质设置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>编码后的帧，调用方负责释放。</returns>
    ValueTask<EncodedFrame> EncodeAsync(
        CapturedFrame frame,
        VideoQualitySettings settings,
        CancellationToken cancellationToken);
}
