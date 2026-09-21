using System.Buffers.Binary;

namespace LanRemote.Transport;

/// <summary>
/// 长度前缀帧写出器。与 <see cref="FrameReader"/> 对称：同样是<b>绝对</b>时限。
/// </summary>
/// <remarks>
/// <para>写出也走同一套长度校验（<see cref="FrameReader.TryValidateLength"/>），
/// 免得「读的一侧很严格、写的一侧随手发」——本机自己发出的超长帧一样是协议违规。</para>
/// <para><see cref="Stream.WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/> 的契约是
/// <b>写完整个 buffer 才算完成</b>（与 <c>ReadAsync</c> 不同，后者允许短读），所以这里不需要循环。</para>
/// </remarks>
public static class FrameWriter
{
    /// <summary>
    /// 写一帧（4 字节大端长度前缀 + payload）。
    /// </summary>
    /// <param name="stream">目标流。</param>
    /// <param name="payload">载荷；长度必须落在 <c>1..maxBytes</c>。</param>
    /// <param name="maxBytes">当前阶段上限。</param>
    /// <param name="timeout">整帧写出的绝对时限。</param>
    /// <param name="cancellationToken">外部取消。</param>
    /// <exception cref="FrameProtocolException">长度为 0 或超过上限；此时<b>一个字节都不发</b>。</exception>
    public static async Task WriteFrameAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        int maxBytes,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "写出时限必须为正。");
        }

        if (!FrameReader.TryValidateLength((uint)payload.Length, maxBytes, out int length, out string? rejection))
        {
            throw new FrameProtocolException(rejection!);
        }

        byte[] frame = new byte[TransportConstants.LengthPrefixBytes + length];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0, TransportConstants.LengthPrefixBytes), (uint)length);
        payload.CopyTo(frame.AsMemory(TransportConstants.LengthPrefixBytes));

        using CancellationTokenSource stageCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        stageCts.CancelAfter(timeout);
        await stream.WriteAsync(frame, stageCts.Token).ConfigureAwait(false);
        await stream.FlushAsync(stageCts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// 写 hello 帧（客户端侧）。
    /// </summary>
    /// <param name="stream">目标流。</param>
    /// <param name="timeout">整帧写出的绝对时限。</param>
    /// <param name="cancellationToken">外部取消。</param>
    public static Task WriteHelloAsync(
        Stream stream,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        WriteFrameAsync(
            stream,
            HelloFrame.Serialize(),
            TransportConstants.MaxPreAuthMessageBytes,
            timeout,
            cancellationToken);
}
