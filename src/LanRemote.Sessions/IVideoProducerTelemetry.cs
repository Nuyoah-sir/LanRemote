using LanRemote.Core.Models;

namespace LanRemote.Sessions;

/// <summary>自动画质只读遥测接缝：只暴露当前代际生产者的真实测量与本次目标显示器。</summary>
/// <remarks>
/// 仅供 Host 自动画质读取，不代表发送、显示或采集成功；不新增公开 API。
/// 指标为启动以来累计平均编码帧率，不是目标帧率、显示帧率或网络发送帧率；Quality 是本代际实际采用的档位。
/// </remarks>
internal interface IVideoProducerTelemetry
{
    long EncodedFrames { get; }
    double ActualFps { get; }
    DisplayInfo? Display { get; }
    VideoQualitySettings Quality { get; }
}
