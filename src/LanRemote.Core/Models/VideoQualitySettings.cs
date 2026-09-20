namespace LanRemote.Core.Models;

/// <summary>
/// 视频画质设置。
/// </summary>
/// <param name="TargetFps">目标帧率，取值 5/10/15/20/30。</param>
/// <param name="Scale">缩放比例，取值 0.50/0.67/0.75/1.00。</param>
/// <param name="JpegQuality">JPEG 质量，取值 40~85。</param>
/// <param name="Auto">是否启用自动画质调控。</param>
/// <remarks>
/// 每个会话独立持有本设置，互不影响（见 <c>07_MILESTONES_AND_TASKS.md</c> M6 DoD）。
/// </remarks>
public sealed record VideoQualitySettings(
    int TargetFps,
    double Scale,
    int JpegQuality,
    bool Auto)
{
    /// <summary>允许的目标帧率档位。</summary>
    public static IReadOnlyList<int> AllowedFps { get; } = [5, 10, 15, 20, 30];

    /// <summary>允许的缩放档位。</summary>
    public static IReadOnlyList<double> AllowedScales { get; } = [0.50, 0.67, 0.75, 1.00];

    /// <summary>JPEG 质量下限。</summary>
    public const int MinJpegQuality = 40;

    /// <summary>JPEG 质量上限。</summary>
    public const int MaxJpegQuality = 85;

    /// <summary>「均衡」预设。</summary>
    public static VideoQualitySettings Balanced { get; } = new(20, 0.75, 60, false);

    /// <summary>「低延迟」预设。</summary>
    public static VideoQualitySettings LowLatency { get; } = new(30, 0.67, 45, false);

    /// <summary>「高画质」预设。</summary>
    public static VideoQualitySettings HighQuality { get; } = new(20, 1.00, 75, false);

    /// <summary>把任意输入钳制到合法范围内，非法/缺失值回落到 <see cref="Balanced"/>。</summary>
    /// <param name="targetFps">期望帧率。</param>
    /// <param name="scale">期望缩放。</param>
    /// <param name="jpegQuality">期望 JPEG 质量。</param>
    /// <param name="auto">是否自动。</param>
    /// <returns>合法的设置实例。</returns>
    public static VideoQualitySettings Clamp(int targetFps, double scale, int jpegQuality, bool auto)
    {
        int fps = AllowedFps.Contains(targetFps) ? targetFps : Balanced.TargetFps;

        double? nearestScale = AllowedScales
            .Select(s => (double?)s)
            .OrderBy(s => Math.Abs(s!.Value - scale))
            .FirstOrDefault();

        int quality = jpegQuality < MinJpegQuality
            ? MinJpegQuality
            : jpegQuality > MaxJpegQuality
                ? MaxJpegQuality
                : jpegQuality;

        return new VideoQualitySettings(fps, nearestScale ?? Balanced.Scale, quality, auto);
    }
}
