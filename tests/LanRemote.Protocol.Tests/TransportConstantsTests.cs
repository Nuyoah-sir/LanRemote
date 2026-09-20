using LanRemote.Transport;
using Xunit;

namespace LanRemote.Protocol.Tests;

/// <summary>
/// 传输层常量护栏测试。
/// </summary>
/// <remarks>
/// 目的：防止后续施工「顺手」放宽长度上限或改掉协议端口而没有同步协议文档。
/// 一旦断言失败，说明有人在不知情的情况下改动了安全边界或 wire format。
/// 来源：04_PROTOCOL_AND_SECURITY.md 第 2、6、13 节。
/// </remarks>
public sealed class TransportConstantsTests
{
    [Fact]
    public void TlsTcpPort_IsFixedTo45873()
    {
        Assert.Equal(45873, TransportConstants.Port);
    }

    [Fact]
    public void MaxControlMessageBytes_IsOneMiB()
    {
        Assert.Equal(1024 * 1024, TransportConstants.MaxControlMessageBytes);
    }

    [Fact]
    public void NominalControlMessageLimit_Is64KiB()
    {
        Assert.Equal(64 * 1024, TransportConstants.NominalControlMessageBytes);
    }

    [Fact]
    public void NominalLimit_IsSmallerThanHardLimit()
    {
        Assert.True(
            TransportConstants.NominalControlMessageBytes < TransportConstants.MaxControlMessageBytes,
            "建议上限必须小于硬上限，否则告警阈值失去意义。");
    }

    [Fact]
    public void LengthPrefix_IsFourBytesBigEndian()
    {
        Assert.Equal(4, TransportConstants.LengthPrefixBytes);
    }

    [Fact]
    public void VideoFrameHeader_IsLrvfMagicVersionOne()
    {
        Assert.Equal("LRVF", TransportConstants.VideoFrameMagic);
        Assert.Equal((byte)1, TransportConstants.VideoFrameVersion);
    }

    [Fact]
    public void VideoFrameHeaderSize_MatchesSpecLayout()
    {
        // 规范要求：magic(4) + version(1) + codec(1) + flags(2) + frameId(8)
        //          + timestampUs(8) + width(4) + height(4) + quality(1)
        //          + reserved(3) + payloadLength(4) = 40
        Assert.Equal(40, TransportConstants.VideoFrameHeaderBytes);
    }
}
