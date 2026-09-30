using System.Buffers.Binary;
using System.Security.Cryptography;

namespace LanRemote.Transport;

/// <summary>
/// 接管一个含长度前缀的完整敏感帧，原 Write/Flush 退出后清零整个原数组。
/// 不复制 wire、不拥有 Stream、不重试、不创建新的计时器；外层负责绝对截止与关闭。
/// 调用后调用者不得再访问或并发提交该数组。本层不保证 TLS/Stream 内部副本擦除。
/// </summary>
internal static class SensitiveFrameWriter
{
    internal static async Task WriteOwnedFrameAsync(
        Stream stream,
        byte[] ownedWire,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ownedWire);
        Task? original = null;
        try
        {
            // 非 null 数组的所有权已接管；包括所有入口校验失败都必须进入 finally。
            ArgumentNullException.ThrowIfNull(stream);
            cancellationToken.ThrowIfCancellationRequested();
            if (ownedWire.Length < TransportConstants.LengthPrefixBytes)
                throw new FrameProtocolException("sensitive-frame-incomplete-prefix");
            uint prefix = BinaryPrimitives.ReadUInt32BigEndian(ownedWire);
            if (!FrameReader.TryValidateLength(prefix, maxBytes, out int length, out string? rejection))
                throw new FrameProtocolException(rejection!);
            if (length != ownedWire.Length - TransportConstants.LengthPrefixBytes)
                throw new FrameProtocolException("sensitive-frame-length-mismatch");

            // 调用的同步前缀及原任务都属于本次操作，ValueTask 仅转换/消费一次。
            original = stream.WriteAsync(ownedWire.AsMemory(), cancellationToken).AsTask();
            await original.ConfigureAwait(false);
            original = null;
            cancellationToken.ThrowIfCancellationRequested();
            original = stream.FlushAsync(cancellationToken)
                ?? throw new InvalidOperationException("敏感帧刷新没有返回任务。");
            await original.ConfigureAwait(false);
            original = null;
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            // await 只抛首项。保存本层看到的原 Task 多错；不 Flatten 用户异常包装。
            if (original?.Exception is { } errors && errors.InnerExceptions.Count > 1)
                throw errors;
            // 单项 await 已保留原异常身份/堆栈；同步前缀错误也原样重抛。
            throw;
        }
        finally
        {
            // 不以取消代理提前退出，否则仍在使用原 Memory 的 I/O 会看到被提前擦除的数据。
            CryptographicOperations.ZeroMemory(ownedWire);
        }
    }
}
