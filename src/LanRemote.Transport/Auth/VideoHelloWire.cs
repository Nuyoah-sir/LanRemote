using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;

namespace LanRemote.Transport.Auth;

/// <summary>
/// 客户端敏感 hello 的完整 wire。直接写入唯一的可清零数组，不创建 nonce/proof 字符串。
/// 输入借用且不修改；成功返回后由调用者拥有数组，交给 SensitiveFrameWriter 后不得再使用。
/// 服务端既有 VideoHelloFrame 的解析与复制合同不变。
/// </summary>
internal static class VideoHelloWire
{
    internal const int PayloadByteLength = 208;
    internal const int WireByteLength = TransportConstants.LengthPrefixBytes + PayloadByteLength;

    internal static byte[] SerializeFrame(
        Guid sessionId,
        ReadOnlySpan<byte> attachNonce,
        ReadOnlySpan<byte> attachProof)
    {
        if (attachNonce.Length != VideoAttachProof.NonceByteLength)
            throw new ArgumentException("attachNonce 必须是 16 字节。", nameof(attachNonce));
        if (attachProof.Length != VideoAttachProof.ProofByteLength)
            throw new ArgumentException("attachProof 必须是 32 字节。", nameof(attachProof));

        byte[] wire = new byte[WireByteLength];
        try
        {
            BinaryPrimitives.WriteUInt32BigEndian(wire, PayloadByteLength);
            Span<byte> payload = wire.AsSpan(TransportConstants.LengthPrefixBytes);
            "{\"type\":\"channel_hello\",\"channel\":\"video\",\"protocol\":1,\"sessionId\":\""u8.CopyTo(payload);
            if (!sessionId.TryFormat(payload.Slice(68, 36), out int guidBytes, "D") || guidBytes != 36)
                throw new InvalidOperationException("视频附着会话标识编码失败。");
            "\",\"attachNonce\":\""u8.CopyTo(payload[104..]);
            Encode(attachNonce, payload.Slice(121, 24));
            "\",\"attachProof\":\""u8.CopyTo(payload[145..]);
            Encode(attachProof, payload.Slice(162, 44));
            "\"}"u8.CopyTo(payload[206..]);
            return wire;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(wire);
            throw;
        }
    }

    private static void Encode(ReadOnlySpan<byte> source, Span<byte> target)
    {
        if (Base64.EncodeToUtf8(source, target, out int consumed, out int written) != OperationStatus.Done
            || consumed != source.Length || written != target.Length)
            throw new InvalidOperationException("视频附着证明编码失败。");
    }
}
