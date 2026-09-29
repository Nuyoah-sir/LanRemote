using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

public sealed class VideoAttachProofTests
{
    // 期望字节来自独立 Python uuid.bytes + hmac；不调用生产 helper 生成期望。
    private static readonly Guid SessionId = new("00112233-4455-6677-8899-aabbccddeeff");
    private const string TokenHex = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
    private const string NonceHex = "202122232425262728292A2B2C2D2E2F";
    private const string PinHex = "808182838485868788898A8B8C8D8E8F909192939495969798999A9B9C9D9E9F";
    private const string TranscriptHex = "4C414E52454D4F54452D564944454F2D56310000112233445566778899AABBCCDDEEFF202122232425262728292A2B2C2D2E2F808182838485868788898A8B8C8D8E8F909192939495969798999A9B9C9D9E9F";
    private const string ProofHex = "602656EADC023F939F0F922B6311A362C3AE9048A880FCF4381E552F178B7A3A";

    [Fact]
    public void Network_Order_Vector_Matches_Independent_Python_Bytes()
    {
        AssertGolden(SessionId, TokenHex, NonceHex, PinHex, TranscriptHex, ProofHex);
    }

    [Fact]
    public void Empty_Guid_And_Zero_Inputs_Match_Independent_Python_Vector()
    {
        AssertGolden(
            Guid.Empty,
            "0000000000000000000000000000000000000000000000000000000000000000",
            "00000000000000000000000000000000",
            "0000000000000000000000000000000000000000000000000000000000000000",
            "4C414E52454D4F54452D564944454F2D56310000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
            "AA31DB29080803F485C964C90F5966A5EF8ECBB976A59BB1F1C9A6D6445EAF9C");
    }

    [Fact]
    public void High_Bytes_Vector_Matches_Independent_Python_Bytes()
    {
        AssertGolden(
            new Guid("0f8fad5b-d9cb-469f-a165-70867728950e"),
            "FFFEFDFCFBFAF9F8F7F6F5F4F3F2F1F0EFEEEDECEBEAE9E8E7E6E5E4E3E2E1E0",
            "F0F1F2F3F4F5F6F7F8F9FAFBFCFDFEFF",
            "F0BE8FE4757F1F4E3F4E57BD2B8079DDD4E958C97905F27317D0383BB4D63530",
            "4C414E52454D4F54452D564944454F2D5631000F8FAD5BD9CB469FA16570867728950EF0F1F2F3F4F5F6F7F8F9FAFBFCFDFEFFF0BE8FE4757F1F4E3F4E57BD2B8079DDD4E958C97905F27317D0383BB4D63530",
            "C6BE7F30721E3B84E95D9969EEC7E4FC78F95177CA937228FFAF69E9508A345C");
    }

    [Fact]
    public void Transcript_Has_Exact_Offsets_Raw_Fields_And_No_Extra_Separators()
    {
        byte[] transcript = VideoAttachProof.BuildTranscript(
            SessionId, Convert.FromHexString(NonceHex), Convert.FromHexString(PinHex));

        Assert.Equal(83, transcript.Length);
        Assert.Equal("LANREMOTE-VIDEO-V1\0"u8.ToArray(), transcript[..19]);
        Assert.Equal(Convert.FromHexString("00112233445566778899AABBCCDDEEFF"), transcript[19..35]);
        Assert.NotEqual(SessionId.ToByteArray(), transcript[19..35]);
        Assert.Equal(Convert.FromHexString(NonceHex), transcript[35..51]);
        Assert.Equal(Convert.FromHexString(PinHex), transcript[51..83]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(17)]
    [InlineData(32)]
    public void Wrong_Nonce_Length_Is_Rejected_By_Both_Entry_Points(int length)
    {
        byte[] nonce = new byte[length];
        byte[] pin = Convert.FromHexString(PinHex);
        byte[] token = Convert.FromHexString(TokenHex);
        Assert.Equal("attachNonce", Assert.Throws<ArgumentException>(
            () => VideoAttachProof.BuildTranscript(SessionId, nonce, pin)).ParamName);
        Assert.Equal("attachNonce", Assert.Throws<ArgumentException>(
            () => VideoAttachProof.ComputeProof(token, SessionId, nonce, pin)).ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    public void Wrong_Pin_Length_Is_Rejected_By_Both_Entry_Points(int length)
    {
        byte[] nonce = Convert.FromHexString(NonceHex);
        byte[] pin = new byte[length];
        byte[] token = Convert.FromHexString(TokenHex);
        Assert.Equal("certificateSha256", Assert.Throws<ArgumentException>(
            () => VideoAttachProof.BuildTranscript(SessionId, nonce, pin)).ParamName);
        Assert.Equal("certificateSha256", Assert.Throws<ArgumentException>(
            () => VideoAttachProof.ComputeProof(token, SessionId, nonce, pin)).ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    public void Wrong_Token_Length_Is_Rejected(int length)
    {
        Assert.Equal("sessionToken", Assert.Throws<ArgumentException>(
            () => VideoAttachProof.ComputeProof(new byte[length], SessionId,
                Convert.FromHexString(NonceHex), Convert.FromHexString(PinHex))).ParamName);
    }

    [Theory]
    [InlineData("sessionId", 0)]
    [InlineData("token", 0)]
    [InlineData("token", 31)]
    [InlineData("nonce", 0)]
    [InlineData("nonce", 15)]
    [InlineData("pin", 0)]
    [InlineData("pin", 31)]
    public void Changing_Any_Bound_Field_Changes_The_Golden_Proof(string field, int index)
    {
        Guid sessionId = SessionId;
        byte[] token = Convert.FromHexString(TokenHex);
        byte[] nonce = Convert.FromHexString(NonceHex);
        byte[] pin = Convert.FromHexString(PinHex);
        switch (field)
        {
            case "sessionId": sessionId = new Guid("00112233-4455-6677-8899-aabbccddeefe"); break;
            case "token": token[index] ^= 1; break;
            case "nonce": nonce[index] ^= 1; break;
            case "pin": pin[index] ^= 1; break;
        }

        Assert.NotEqual(ProofHex, Convert.ToHexString(
            VideoAttachProof.ComputeProof(token, sessionId, nonce, pin)));
    }

    [Fact]
    public void Entry_Points_Respect_Span_Slices()
    {
        byte[] token = [0xFF, .. Convert.FromHexString(TokenHex), 0xFF];
        byte[] nonce = [0xFF, .. Convert.FromHexString(NonceHex), 0xFF];
        byte[] pin = [0xFF, .. Convert.FromHexString(PinHex), 0xFF];
        Assert.Equal(TranscriptHex, Convert.ToHexString(
            VideoAttachProof.BuildTranscript(SessionId, nonce.AsSpan(1, 16), pin.AsSpan(1, 32))));
        Assert.Equal(ProofHex, Convert.ToHexString(
            VideoAttachProof.ComputeProof(token.AsSpan(1, 32), SessionId, nonce.AsSpan(1, 16), pin.AsSpan(1, 32))));
    }

    [Fact]
    public void Inputs_Are_Not_Modified_And_Results_Are_Independent()
    {
        byte[] token = Convert.FromHexString(TokenHex);
        byte[] nonce = Convert.FromHexString(NonceHex);
        byte[] pin = Convert.FromHexString(PinHex);
        byte[] transcript = VideoAttachProof.BuildTranscript(SessionId, nonce, pin);
        byte[] proof = VideoAttachProof.ComputeProof(token, SessionId, nonce, pin);
        Assert.Equal(TokenHex, Convert.ToHexString(token));
        Assert.Equal(NonceHex, Convert.ToHexString(nonce));
        Assert.Equal(PinHex, Convert.ToHexString(pin));

        byte[] nextTranscript = VideoAttachProof.BuildTranscript(SessionId, nonce, pin);
        byte[] nextProof = VideoAttachProof.ComputeProof(token, SessionId, nonce, pin);
        Assert.NotSame(transcript, nextTranscript);
        Assert.NotSame(proof, nextProof);
        Array.Clear(token);
        Array.Clear(nonce);
        Array.Clear(pin);
        Assert.Equal(TranscriptHex, Convert.ToHexString(transcript));
        Assert.Equal(ProofHex, Convert.ToHexString(proof));
        Array.Clear(transcript);
        Array.Clear(proof);
        Assert.Equal(TranscriptHex, Convert.ToHexString(nextTranscript));
        Assert.Equal(ProofHex, Convert.ToHexString(nextProof));
    }

    private static void AssertGolden(
        Guid sessionId, string tokenHex, string nonceHex, string pinHex, string transcriptHex, string proofHex)
    {
        byte[] nonce = Convert.FromHexString(nonceHex);
        byte[] pin = Convert.FromHexString(pinHex);
        byte[] transcript = VideoAttachProof.BuildTranscript(sessionId, nonce, pin);
        byte[] proof = VideoAttachProof.ComputeProof(Convert.FromHexString(tokenHex), sessionId, nonce, pin);
        Assert.Equal(83, transcript.Length);
        Assert.Equal(Convert.FromHexString(transcriptHex), transcript);
        Assert.Equal(32, proof.Length);
        Assert.Equal(Convert.FromHexString(proofHex), proof);
    }
}
