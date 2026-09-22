using System.Buffers.Binary;
using System.Security.Cryptography;

namespace LanRemote.Transport;

/// <summary>
/// 长度前缀帧的协议违规。
/// </summary>
/// <remarks>
/// 抛出后连接应当<b>直接关闭</b>，不做 drain、不做重同步——
/// 违规的那一侧没有资格让我们继续读它的数据。
/// </remarks>
public sealed class FrameProtocolException : Exception
{
    /// <summary>构造异常。</summary>
    /// <param name="reason">机器可读的短原因，只用于本地日志。</param>
    public FrameProtocolException(string reason)
        : base($"帧协议违规：{reason}")
    {
        Reason = reason;
    }

    /// <summary>机器可读的短原因。</summary>
    public string Reason { get; }
}

/// <summary>
/// 长度前缀帧读取器：每一段都用<b>绝对</b> deadline。
/// </summary>
/// <remarks>
/// <para><b>评审 A-8</b>：deadline 必须是「从本段起点算起」，不是「距上次读到字节 N 秒」。
/// 滑动窗口会被「每 <c>timeout - ε</c> 发一个字节」的低速攻击无限续命。
/// 这里的每一段（长度前缀 / 整帧 payload）都用独立的
/// <c>CancellationTokenSource.CancelAfter</c> 实现，从该段起点计时。</para>
/// <para><b>不用 <c>Stream.ReadTimeout</c></b>：它只覆盖同步读，异步读不受它约束。</para>
/// <para><b>评审 A-10</b>：长度先在 <see cref="uint"/> 域上校验（拒绝 0、拒绝超过上限），
/// <b>然后</b>才转 <see cref="int"/>。反过来会有符号/溢出问题——
/// <c>0xFFFFFFFF</c> 直接转 int 是 <c>-1</c>，拿它去分配数组会炸得很奇怪。</para>
/// </remarks>
public sealed class FrameReader
{
    /// <summary>长度为 0。</summary>
    public const string RejectZeroLength = "length-zero";

    /// <summary>长度超过当前阶段上限。</summary>
    public const string RejectTooLarge = "length-exceeds-limit";

    private readonly Stream _stream;

    /// <summary>构造读取器。</summary>
    /// <param name="stream">底层流（通常是已认证的 <c>SslStream</c>）。</param>
    public FrameReader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
    }

    /// <summary>
    /// 校验长度前缀。
    /// </summary>
    /// <param name="length">读到的 <see cref="uint"/> 长度。</param>
    /// <param name="maxBytes">当前阶段上限（必须为正）。</param>
    /// <param name="lengthBytes">通过时为转换后的长度。</param>
    /// <param name="rejection">拒绝原因短码；通过时为 <see langword="null"/>。</param>
    /// <returns>是否可以按这个长度继续读。</returns>
    public static bool TryValidateLength(
        uint length,
        int maxBytes,
        out int lengthBytes,
        out string? rejection)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        lengthBytes = 0;
        rejection = null;

        // 全部判断都在 uint 域里做完，最后一步才转 int。
        if (length == 0)
        {
            rejection = RejectZeroLength;
            return false;
        }

        if (length > (uint)maxBytes)
        {
            rejection = RejectTooLarge;
            return false;
        }

        lengthBytes = (int)length;
        return true;
    }

    /// <summary>
    /// 读 4 字节长度前缀（大端），绝对 deadline。
    /// </summary>
    /// <param name="timeout">本段绝对时限。</param>
    /// <param name="cancellationToken">外部取消。</param>
    /// <returns>长度值（尚未校验）。</returns>
    /// <exception cref="EndOfStreamException">前缀未读满就 EOF。</exception>
    public async Task<uint> ReadLengthPrefixAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        byte[] buffer = new byte[TransportConstants.LengthPrefixBytes];
        await ReadExactlyAsync(buffer, timeout, cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32BigEndian(buffer);
    }

    /// <summary>
    /// 读整帧 payload，绝对 deadline。
    /// </summary>
    /// <param name="lengthBytes">要读的字节数（必须已经过 <see cref="TryValidateLength"/>）。</param>
    /// <param name="timeout">本段绝对时限。</param>
    /// <param name="cancellationToken">外部取消。</param>
    /// <returns>payload；调用方负责处置其中的敏感内容。</returns>
    /// <exception cref="EndOfStreamException">payload 未读满就 EOF。</exception>
    public async Task<byte[]> ReadPayloadAsync(
        int lengthBytes,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lengthBytes);

        byte[] buffer = new byte[lengthBytes];
        try
        {
            await ReadExactlyAsync(buffer, timeout, cancellationToken).ConfigureAwait(false);
            return buffer;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(buffer);
            throw;
        }
    }

    /// <summary>
    /// 一步读完一帧：长度前缀 + 校验 + payload。
    /// </summary>
    /// <param name="maxBytes">当前阶段上限。</param>
    /// <param name="prefixTimeout">长度前缀的绝对时限。</param>
    /// <param name="payloadTimeout">payload 的绝对时限。</param>
    /// <param name="cancellationToken">外部取消。</param>
    /// <returns>payload。</returns>
    /// <remarks>
    /// 两段各自独立计时：前缀用掉的时间<b>不</b>计入 payload，
    /// 否则「慢慢发前缀 + 慢慢发 payload」就能把总时长翻倍。
    /// </remarks>
    /// <exception cref="FrameProtocolException">长度为 0 或超过上限；此时<b>不读取 payload</b>。</exception>
    /// <exception cref="EndOfStreamException">任一段提前 EOF。</exception>
    public async Task<byte[]> ReadFrameAsync(
        int maxBytes,
        TimeSpan prefixTimeout,
        TimeSpan payloadTimeout,
        CancellationToken cancellationToken = default)
    {
        uint length = await ReadLengthPrefixAsync(prefixTimeout, cancellationToken).ConfigureAwait(false);

        if (!TryValidateLength(length, maxBytes, out int lengthBytes, out string? rejection))
        {
            // 超限一律不 drain：直接让调用方关连接。
            throw new FrameProtocolException(rejection!);
        }

        return await ReadPayloadAsync(lengthBytes, payloadTimeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadExactlyAsync(
        byte[] buffer,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "读取时限必须为正。");
        }

        // 这一段自己的绝对 deadline：从进入本段开始计时，中途读到多少字节都不重置。
        using CancellationTokenSource stageCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        stageCts.CancelAfter(timeout);

        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await _stream
                .ReadAsync(buffer.AsMemory(offset), stageCts.Token)
                .ConfigureAwait(false);

            if (read == 0)
            {
                throw new EndOfStreamException($"读到 {offset}/{buffer.Length} 字节时遇到 EOF。");
            }

            offset += read;
        }
    }
}
