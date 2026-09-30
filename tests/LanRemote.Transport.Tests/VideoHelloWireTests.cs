using System.Buffers.Binary;
using System.Text;
using LanRemote.Transport.Auth;

namespace LanRemote.Transport.Tests;

public sealed class VideoHelloWireTests
{
    // 沿用 VideoHelloFrameTests 的两个独立 Python 向量；期望值不经过生产序列化器。
    private const string SessionId = "00112233-4455-6677-8899-aabbccddeeff";
    private const string NonceHex = "202122232425262728292A2B2C2D2E2F";
    private const string ProofHex = "602656EADC023F939F0F922B6311A362C3AE9048A880FCF4381E552F178B7A3A";
    private const string Hello = """{"type":"channel_hello","channel":"video","protocol":1,"sessionId":"00112233-4455-6677-8899-aabbccddeeff","attachNonce":"ICEiIyQlJicoKSorLC0uLw==","attachProof":"YCZW6twCP5OfD5IrYxGjYsOukEiogPz0OB5VLxeLejo="}""";
    private const string SecondSessionId = "0f8fad5b-d9cb-469f-a165-70867728950e";
    private const string SecondNonceHex = "F0F1F2F3F4F5F6F7F8F9FAFBFCFDFEFF";
    private const string SecondProofHex = "C6BE7F30721E3B84E95D9969EEC7E4FC78F95177CA937228FFAF69E9508A345C";
    private const string SecondHello = """{"type":"channel_hello","channel":"video","protocol":1,"sessionId":"0f8fad5b-d9cb-469f-a165-70867728950e","attachNonce":"8PHy8/T19vf4+fr7/P3+/w==","attachProof":"xr5/MHIeO4TpXZlp7sfk/Hj5UXfKk3Io/69p6VCKNFw="}""";

    private static void AssertWire(string expectedPayload, byte[] wire)
    {
        byte[] payload = Encoding.UTF8.GetBytes(expectedPayload);
        byte[] expected = [0x00, 0x00, 0x00, 0xD0, .. payload];
        Assert.Equal(208, payload.Length);
        Assert.Equal(212, wire.Length);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0xD0 }, wire[..4]);
        Assert.Equal(208, BinaryPrimitives.ReadInt32BigEndian(wire.AsSpan(0, 4)));
        Assert.Equal(expected, wire);
    }

    [Fact]
    public void Byte_Length_Constants_Match_The_Wire_Contract()
    {
        Assert.Equal(208, VideoHelloWire.PayloadByteLength);
        Assert.Equal(212, VideoHelloWire.WireByteLength);
    }

    [Theory]
    [InlineData(SessionId, NonceHex, ProofHex, Hello)]
    [InlineData(SecondSessionId, SecondNonceHex, SecondProofHex, SecondHello)]
    public void Serialize_Frame_Matches_Complete_Independent_Python_Wire(
        string sessionId, string nonceHex, string proofHex, string expectedPayload)
    {
        byte[] wire = VideoHelloWire.SerializeFrame(
            new Guid(sessionId), Convert.FromHexString(nonceHex), Convert.FromHexString(proofHex));

        AssertWire(expectedPayload, wire);
    }

    [Fact]
    public void Standard_Base64_Plus_And_Slash_Are_Not_Escaped()
    {
        byte[] wire = VideoHelloWire.SerializeFrame(
            new Guid(SecondSessionId), Convert.FromHexString(SecondNonceHex), Convert.FromHexString(SecondProofHex));

        Assert.Contains((byte)'+', wire);
        Assert.Contains((byte)'/', wire);
        Assert.DoesNotContain((byte)'\\', wire);
        AssertWire(SecondHello, wire);
    }

    [Fact]
    public void Guid_Empty_And_Zero_Inputs_Produce_The_Complete_Expected_Wire()
    {
        const string hello = """{"type":"channel_hello","channel":"video","protocol":1,"sessionId":"00000000-0000-0000-0000-000000000000","attachNonce":"AAAAAAAAAAAAAAAAAAAAAA==","attachProof":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="}""";
        byte[] wire = VideoHelloWire.SerializeFrame(Guid.Empty, new byte[16], new byte[32]);

        AssertWire(hello, wire);
        Assert.True(VideoHelloFrame.TryParse(wire.AsSpan(4), out VideoHelloFrame? parsed, out string? rejection));
        Assert.Null(rejection);
        Assert.NotNull(parsed);
        Assert.Equal(Guid.Empty, parsed.SessionId);
        Assert.Equal(new byte[16], parsed.AttachNonce.ToArray());
        Assert.Equal(new byte[32], parsed.AttachProof.ToArray());
    }

    [Fact]
    public void Guid_From_Uppercase_Input_Is_Written_In_Canonical_Lowercase_D_Format()
    {
        byte[] wire = VideoHelloWire.SerializeFrame(
            new Guid("00112233-4455-6677-8899-AABBCCDDEEFF"),
            Convert.FromHexString(NonceHex), Convert.FromHexString(ProofHex));

        AssertWire(Hello, wire);
    }

    [Fact]
    public void Only_Input_Slices_Are_Borrowed_And_All_Input_Bytes_Remain_Unchanged()
    {
        byte[] nonce = [0xA5, .. Convert.FromHexString(NonceHex), 0x5A];
        byte[] proof = [0xC3, .. Convert.FromHexString(ProofHex), 0x3C];
        byte[] nonceBefore = nonce.ToArray();
        byte[] proofBefore = proof.ToArray();

        byte[] wire = VideoHelloWire.SerializeFrame(new Guid(SessionId), nonce.AsSpan(1, 16), proof.AsSpan(1, 32));

        Assert.Equal(nonceBefore, nonce);
        Assert.Equal(proofBefore, proof);
        AssertWire(Hello, wire);
    }

    [Fact]
    public void Mutating_Borrowed_Inputs_After_Return_Does_Not_Change_The_Wire()
    {
        byte[] nonce = Convert.FromHexString(NonceHex);
        byte[] proof = Convert.FromHexString(ProofHex);
        byte[] wire = VideoHelloWire.SerializeFrame(new Guid(SessionId), nonce, proof);

        Array.Clear(nonce);
        Array.Clear(proof);

        AssertWire(Hello, wire);
    }

    [Fact]
    public void Each_Call_Returns_An_Independent_Array()
    {
        byte[] nonce = Convert.FromHexString(NonceHex);
        byte[] proof = Convert.FromHexString(ProofHex);
        byte[] first = VideoHelloWire.SerializeFrame(new Guid(SessionId), nonce, proof);
        byte[] second = VideoHelloWire.SerializeFrame(new Guid(SessionId), nonce, proof);
        Assert.NotSame(first, second);
        AssertWire(Hello, first);
        AssertWire(Hello, second);

        Array.Clear(first);

        AssertWire(Hello, second);
        Assert.Equal(Convert.FromHexString(NonceHex), nonce);
        Assert.Equal(Convert.FromHexString(ProofHex), proof);
        byte[] third = VideoHelloWire.SerializeFrame(new Guid(SessionId), nonce, proof);
        Assert.NotSame(first, third);
        Assert.NotSame(second, third);
        AssertWire(Hello, third);
    }

    [Theory]
    [InlineData(0, 32, "attachNonce")]
    [InlineData(15, 32, "attachNonce")]
    [InlineData(17, 32, "attachNonce")]
    [InlineData(16, 0, "attachProof")]
    [InlineData(16, 31, "attachProof")]
    [InlineData(16, 33, "attachProof")]
    public void Invalid_Input_Lengths_Throw_ArgumentException_With_The_Correct_Parameter(
        int nonceLength, int proofLength, string parameter)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            VideoHelloWire.SerializeFrame(Guid.Empty, new byte[nonceLength], new byte[proofLength]));

        Assert.Equal(parameter, exception.ParamName);
    }

    [Theory]
    [InlineData(SessionId, NonceHex, ProofHex, Hello)]
    [InlineData(SecondSessionId, SecondNonceHex, SecondProofHex, SecondHello)]
    public void Payload_After_The_Length_Prefix_Matches_The_Existing_Strict_Parser(
        string sessionId, string nonceHex, string proofHex, string expectedPayload)
    {
        byte[] nonce = Convert.FromHexString(nonceHex);
        byte[] proof = Convert.FromHexString(proofHex);
        byte[] wire = VideoHelloWire.SerializeFrame(new Guid(sessionId), nonce, proof);

        AssertWire(expectedPayload, wire);
        int payloadLength = BinaryPrimitives.ReadInt32BigEndian(wire.AsSpan(0, 4));
        Assert.True(VideoHelloFrame.TryParse(wire.AsSpan(4, payloadLength), out VideoHelloFrame? parsed, out string? rejection));
        Assert.Null(rejection);
        Assert.NotNull(parsed);
        Assert.Equal(new Guid(sessionId), parsed.SessionId);
        Assert.Equal(Convert.FromHexString(nonceHex), parsed.AttachNonce.ToArray());
        Assert.Equal(Convert.FromHexString(proofHex), parsed.AttachProof.ToArray());
    }
}
