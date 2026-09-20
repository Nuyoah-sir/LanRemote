namespace LanRemote.Transport;

/// <summary>
/// 传输层固定参数与长度上限。
/// </summary>
/// <remarks>
/// <para>来源：<c>04_PROTOCOL_AND_SECURITY.md</c> 第 2、6、13 节。</para>
/// <para>所有网络读取必须先用这里的常量做范围校验，再分配数组；
/// 禁止按远端给出的任意 int 直接 <c>new byte[int]</c>（<c>06_DEV_STANDARDS.md</c> 第 4 节）。</para>
/// </remarks>
public static class TransportConstants
{
    /// <summary>TLS/TCP 监听端口。</summary>
    public const int Port = 45873;

    /// <summary>Control 通道 JSON 消息上限，1 MiB。</summary>
    public const int MaxControlMessageBytes = 1024 * 1024;

    /// <summary>正常 Control 消息的建议上限，用于告警而非拒绝。</summary>
    public const int NominalControlMessageBytes = 64 * 1024;

    /// <summary>长度前缀字段长度（字节）。</summary>
    public const int LengthPrefixBytes = 4;

    /// <summary>视频帧二进制头的魔数 "LRVF"。</summary>
    public const string VideoFrameMagic = "LRVF";

    /// <summary>视频帧二进制头长度（字节）。</summary>
    public const int VideoFrameHeaderBytes = 40;

    /// <summary>视频帧头版本。</summary>
    public const byte VideoFrameVersion = 1;
}
