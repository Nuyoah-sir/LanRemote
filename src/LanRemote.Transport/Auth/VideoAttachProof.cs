using System.Security.Cryptography;

namespace LanRemote.Transport.Auth;

/// <summary>ADR-048 视频附着证明：固定二进制 transcript，不复用 M4 的文本布局。</summary>
internal static class VideoAttachProof
{
    internal const int NonceByteLength = 16;
    internal const int TokenByteLength = 32;
    internal const int CertificateSha256ByteLength = 32;
    internal const int ProofByteLength = 32;
    internal const int TranscriptByteLength = 83;

    internal static byte[] BuildTranscript(
        Guid sessionId,
        ReadOnlySpan<byte> attachNonce,
        ReadOnlySpan<byte> certificateSha256)
    {
        if (attachNonce.Length != NonceByteLength)
        {
            throw new ArgumentException("attachNonce 必须是 16 字节。", nameof(attachNonce));
        }

        if (certificateSha256.Length != CertificateSha256ByteLength)
        {
            throw new ArgumentException("certificateSha256 必须是 32 字节。", nameof(certificateSha256));
        }

        byte[] transcript = new byte[TranscriptByteLength];
        "LANREMOTE-VIDEO-V1\0"u8.CopyTo(transcript);
        // Guid 默认混合端序不符合协议；19..34 必须是 RFC 网络端序。
        sessionId.TryWriteBytes(transcript.AsSpan(19, 16), bigEndian: true, out _);
        attachNonce.CopyTo(transcript.AsSpan(35, NonceByteLength));
        certificateSha256.CopyTo(transcript.AsSpan(51, CertificateSha256ByteLength));
        return transcript;
    }

    internal static byte[] ComputeProof(
        ReadOnlySpan<byte> sessionToken,
        Guid sessionId,
        ReadOnlySpan<byte> attachNonce,
        ReadOnlySpan<byte> certificateSha256)
    {
        if (sessionToken.Length != TokenByteLength)
        {
            throw new ArgumentException("sessionToken 必须是 32 字节。", nameof(sessionToken));
        }

        return HMACSHA256.HashData(sessionToken, BuildTranscript(sessionId, attachNonce, certificateSha256));
    }
}
