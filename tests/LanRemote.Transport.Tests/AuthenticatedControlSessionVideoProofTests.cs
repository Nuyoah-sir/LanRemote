using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using LanRemote.Core.Models;

namespace LanRemote.Transport.Tests;

public sealed class AuthenticatedControlSessionVideoProofTests
{
    private static readonly Guid SessionId = new("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private const string TokenHex = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
    private const string NonceHex = "202122232425262728292A2B2C2D2E2F";
    private const string PinHex = "808182838485868788898A8B8C8D8E8F909192939495969798999A9B9C9D9E9F";
    private const string TranscriptHex = "4C414E52454D4F54452D564944454F2D56310000112233445566778899AABBCCDDEEFF202122232425262728292A2B2C2D2E2F808182838485868788898A8B8C8D8E8F909192939495969798999A9B9C9D9E9F";
    private const string ProofHex = "602656EADC023F939F0F922B6311A362C3AE9048A880FCF4381E552F178B7A3A";

    [Fact]
    public void Proof_Matches_Independent_83_Byte_Transcript_And_Hmac_Golden()
    {
        byte[] token = Convert.FromHexString(TokenHex);
        byte[] nonce = Convert.FromHexString(NonceHex);
        byte[] pin = Convert.FromHexString(PinHex);
        using AuthenticatedControlSession session = CreateSession(token: token);
        byte[] transcript = IndependentTranscript(SessionId, nonce, pin);
        Assert.Equal(83, transcript.Length);
        Assert.Equal(TranscriptHex, Convert.ToHexString(transcript));
        using HMACSHA256 hmac = new(token);
        byte[] expected = hmac.ComputeHash(transcript);
        Assert.Equal(ProofHex, Convert.ToHexString(expected));

        byte[] proof = session.CreateVideoAttachProof(nonce, pin);
        Assert.Equal(32, proof.Length);
        Assert.Equal(expected, proof);
        Assert.NotEqual(token, proof);
    }

    [Theory]
    [InlineData("nonce", 0)]
    [InlineData("nonce", 15)]
    [InlineData("pin", 0)]
    [InlineData("pin", 31)]
    [InlineData("token", 0)]
    [InlineData("token", 31)]
    [InlineData("sessionId", 0)]
    public void Proof_Binds_Nonce_Pin_Token_And_Session_Id(string field, int index)
    {
        byte[] token = Convert.FromHexString(TokenHex);
        byte[] nonce = Convert.FromHexString(NonceHex);
        byte[] pin = Convert.FromHexString(PinHex);
        Guid sessionId = SessionId;
        switch (field)
        {
            case "nonce": nonce[index] ^= 1; break;
            case "pin": pin[index] ^= 1; break;
            case "token": token[index] ^= 1; break;
            case "sessionId": sessionId = new Guid("00112233-4455-6677-8899-aabbccddeefe"); break;
        }
        using AuthenticatedControlSession session = CreateSession(token, sessionId, pin);
        byte[] proof = session.CreateVideoAttachProof(nonce, pin);
        Assert.Equal(IndependentProof(token, sessionId, nonce, pin), proof);
        Assert.NotEqual(ProofHex, Convert.ToHexString(proof));
    }

    [Theory]
    [InlineData(0, 32, "attachNonce")]
    [InlineData(15, 32, "attachNonce")]
    [InlineData(17, 32, "attachNonce")]
    [InlineData(32, 32, "attachNonce")]
    [InlineData(16, 0, "actualVideoPin")]
    [InlineData(16, 16, "actualVideoPin")]
    [InlineData(16, 31, "actualVideoPin")]
    [InlineData(16, 33, "actualVideoPin")]
    public void Wrong_Length_Is_Rejected_Without_Changing_Inputs_Or_Consuming_Session(
        int nonceLength, int pinLength, string parameter)
    {
        using AuthenticatedControlSession session = CreateSession();
        byte[] nonce = Enumerable.Repeat((byte)0xA5, nonceLength).ToArray();
        byte[] pin = Enumerable.Repeat((byte)0x5A, pinLength).ToArray();
        Assert.Equal(parameter, Assert.Throws<ArgumentException>(
            () => session.CreateVideoAttachProof(nonce, pin)).ParamName);
        Assert.All(nonce, value => Assert.Equal((byte)0xA5, value));
        Assert.All(pin, value => Assert.Equal((byte)0x5A, value));
        Assert.Equal(ProofHex, Convert.ToHexString(session.CreateVideoAttachProof(
            Convert.FromHexString(NonceHex), Convert.FromHexString(PinHex))));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void Wrong_Actual_Video_Pin_Is_Rejected(int index)
    {
        using AuthenticatedControlSession session = CreateSession();
        byte[] nonce = Convert.FromHexString(NonceHex);
        byte[] pin = Convert.FromHexString(PinHex);
        pin[index] ^= 1;
        byte[] expectedPin = pin.ToArray();
        Assert.Throws<AuthenticationException>(() => session.CreateVideoAttachProof(nonce, pin));
        Assert.Equal(expectedPin, pin);
        Assert.Equal(NonceHex, Convert.ToHexString(nonce));
        Assert.Equal(ProofHex, Convert.ToHexString(session.CreateVideoAttachProof(
            nonce, Convert.FromHexString(PinHex))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mismatched_Control_Expected_Pin_Rejects_Both_Presented_And_Expected_Video_Pins(bool useExpected)
    {
        byte[] presentedPin = Convert.FromHexString(PinHex);
        byte[] expectedPin = presentedPin.ToArray();
        expectedPin[0] ^= 1;
        using AuthenticatedControlSession session = CreateSession(presentedPin: presentedPin, expectedPin: expectedPin);
        Assert.False(session.Identity.PinsMatch);
        byte[] videoPin = useExpected ? expectedPin : presentedPin;
        Assert.Throws<AuthenticationException>(() => session.CreateVideoAttachProof(
            Convert.FromHexString(NonceHex), videoPin));
    }

    [Fact]
    public void Cancelled_Caller_Is_Rejected_Without_Returning_Proof_Or_Consuming_Session()
    {
        using AuthenticatedControlSession session = CreateSession();
        using CancellationTokenSource caller = new();
        caller.Cancel();
        byte[] nonce = Convert.FromHexString(NonceHex);
        byte[] pin = Convert.FromHexString(PinHex);
        byte[]? proof = null;
        OperationCanceledException error = Assert.Throws<OperationCanceledException>(() =>
        {
            proof = session.CreateVideoAttachProof(nonce, pin, caller.Token);
        });
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Null(proof);
        Assert.Equal(NonceHex, Convert.ToHexString(nonce));
        Assert.Equal(PinHex, Convert.ToHexString(pin));
        Assert.Equal(ProofHex, Convert.ToHexString(session.CreateVideoAttachProof(nonce, pin)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Disposed_Session_Rejects_Proof_Even_With_Cancelled_Caller(bool cancel)
    {
        using AuthenticatedControlSession session = CreateSession();
        using CancellationTokenSource caller = new();
        if (cancel) caller.Cancel();
        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => session.CreateVideoAttachProof(
            Convert.FromHexString(NonceHex), Convert.FromHexString(PinHex), caller.Token));
    }

    [Fact]
    public void Repeated_Proof_And_Dispose_Preserve_Inputs_And_Clear_Original_Owned_Token_Before_Unlocked_Tls_Disposal()
    {
        byte[] token = Convert.FromHexString(TokenHex);
        byte[] nonce = [0xFF, .. Convert.FromHexString(NonceHex), 0xFE];
        byte[] pin = [0xFD, .. Convert.FromHexString(PinHex), 0xFC];
        byte[] expectedNonce = nonce.ToArray();
        byte[] expectedPin = pin.ToArray();
        RecordingDisposeStream transport = new();
        using AuthenticatedControlSession session = CreateSession(token: token, transport: transport);
        // 必须持有原数组而非快照，否则无法观察 Dispose 的真实清零。
        byte[] ownedToken = TestOnlyControlSessionSecrets.GetOwnedToken(session);
        Assert.NotSame(token, ownedToken);
        Assert.Equal(token, ownedToken);
        object gate = GetGate(session);
        bool? gateHeldAtTlsDispose = null;
        bool? tokenClearedAtTlsDispose = null;
        transport.Disposing = () =>
        {
            gateHeldAtTlsDispose = Monitor.IsEntered(gate);
            tokenClearedAtTlsDispose = ownedToken.All(value => value == 0);
        };

        byte[] first = session.CreateVideoAttachProof(nonce.AsSpan(1, 16), pin.AsSpan(1, 32));
        byte[] second = session.CreateVideoAttachProof(nonce.AsSpan(1, 16), pin.AsSpan(1, 32));
        Assert.NotSame(first, second);
        Assert.Equal(ProofHex, Convert.ToHexString(first));
        Assert.Equal(first, second);
        CryptographicOperations.ZeroMemory(first);
        Assert.Equal(ProofHex, Convert.ToHexString(second));
        Assert.Equal(token, ownedToken);
        session.Dispose();
        session.Dispose();

        Assert.Same(ownedToken, TestOnlyControlSessionSecrets.GetOwnedToken(session));
        Assert.All(ownedToken, value => Assert.Equal((byte)0, value));
        Assert.Equal(TokenHex, Convert.ToHexString(token));
        Assert.Equal(expectedNonce, nonce);
        Assert.Equal(expectedPin, pin);
        Assert.Equal(ProofHex, Convert.ToHexString(second));
        Assert.Equal(1, transport.DisposeCalls);
        Assert.False(gateHeldAtTlsDispose);
        Assert.True(tokenClearedAtTlsDispose);
    }

    [Theory]
    [InlineData("proof")]
    [InlineData("dispose")]
    [InlineData("cancel")]
    public void Gate_Boundary_Serializes_Proof_With_Dispose_And_Observes_Queued_Cancellation(string firstOperation)
    {
        using AuthenticatedControlSession session = CreateSession();
        using CancellationTokenSource caller = new();
        using ManualResetEventSlim entered = new();
        byte[] nonce = Convert.FromHexString(NonceHex);
        byte[] pin = Convert.FromHexString(PinHex);
        byte[] ownedToken = TestOnlyControlSessionSecrets.GetOwnedToken(session);
        object gate = GetGate(session);
        byte[]? proof = null;
        Exception? workerError = null;
        Thread worker = new(() =>
        {
            entered.Set();
            workerError = Record.Exception(() =>
            {
                if (firstOperation == "proof") session.Dispose();
                else proof = session.CreateVideoAttachProof(nonce, pin, caller.Token);
            });
        }) { IsBackground = true };
        bool started = false;
        // TEST-ONLY：控制的是入口锁边界；没有卡住或观察 MAC 内部执行。
        Monitor.Enter(gate);
        try
        {
            worker.Start();
            started = true;
            Assert.True(entered.Wait(Guard), "工作线程未到达调用边界。");
            Assert.True(SpinWait.SpinUntil(() =>
                (worker.ThreadState & ThreadState.WaitSleepJoin) != 0 || !worker.IsAlive, Guard),
                "工作线程未在锁边界等待。");
            Assert.True((worker.ThreadState & ThreadState.WaitSleepJoin) != 0);
            Assert.Equal(TokenHex, Convert.ToHexString(ownedToken));
            if (firstOperation == "proof")
            {
                // 同线程重入锁完成 proof，另一线程的 Dispose 尚不能清零 token。
                proof = session.CreateVideoAttachProof(nonce, pin);
                Assert.Equal(ProofHex, Convert.ToHexString(proof));
                Assert.Equal(TokenHex, Convert.ToHexString(ownedToken));
            }
            else if (firstOperation == "dispose")
            {
                session.Dispose();
                Assert.All(ownedToken, value => Assert.Equal((byte)0, value));
            }
            else
            {
                caller.Cancel();
            }
        }
        finally
        {
            Monitor.Exit(gate);
            if (started) Assert.True(worker.Join(Guard), "释放锁后工作线程未结束。");
        }

        if (firstOperation == "proof")
        {
            Assert.Null(workerError);
            Assert.NotNull(proof);
            Assert.Equal(ProofHex, Convert.ToHexString(proof));
        }
        else
        {
            Assert.Null(proof);
            if (firstOperation == "dispose") Assert.IsType<ObjectDisposedException>(workerError);
            else Assert.Equal(caller.Token, Assert.IsType<OperationCanceledException>(workerError).CancellationToken);
        }
        if (firstOperation == "cancel")
        {
            Assert.Equal(TokenHex, Convert.ToHexString(ownedToken));
            Assert.Equal(ProofHex, Convert.ToHexString(session.CreateVideoAttachProof(nonce, pin)));
        }
        else
        {
            Assert.All(ownedToken, value => Assert.Equal((byte)0, value));
            Assert.Throws<ObjectDisposedException>(() => session.CreateVideoAttachProof(nonce, pin));
        }
        Assert.Equal(NonceHex, Convert.ToHexString(nonce));
        Assert.Equal(PinHex, Convert.ToHexString(pin));
    }

    [Fact]
    public void Session_Has_No_Token_Getter_And_Proof_Operation_Is_Internal()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        Type type = typeof(AuthenticatedControlSession);
        Assert.Null(type.GetProperty("SessionToken", flags));
        Assert.DoesNotContain(type.GetMethods(flags), method =>
            method.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));
        MethodInfo? method = type.GetMethod("CreateVideoAttachProof", flags);
        Assert.NotNull(method);
        Assert.True(method.IsAssembly);
        Assert.False(method.IsStatic);
        Assert.Equal(typeof(byte[]), method.ReturnType);
        Assert.Equal(new[] { typeof(ReadOnlySpan<byte>), typeof(ReadOnlySpan<byte>), typeof(CancellationToken) },
            method.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
        Assert.True(method.GetParameters()[2].HasDefaultValue);
        Assert.DoesNotContain(type.GetMethods(BindingFlags.Instance | BindingFlags.Public), member =>
            typeof(Stream).IsAssignableFrom(member.ReturnType) || member.ReturnType == typeof(byte[])
            || member.ReturnType == typeof(ReadOnlyMemory<byte>));
    }

    // 测试自行拼装域、UUID 网络序及原始字节，不调用任何生产 transcript/proof helper。
    private static byte[] IndependentTranscript(Guid sessionId, byte[] nonce, byte[] pin)
    {
        byte[] transcript = new byte[83];
        "LANREMOTE-VIDEO-V1\0"u8.CopyTo(transcript);
        Convert.FromHexString(sessionId.ToString("N")).CopyTo(transcript, 19);
        nonce.CopyTo(transcript, 35);
        pin.CopyTo(transcript, 51);
        return transcript;
    }

    private static byte[] IndependentProof(byte[] token, Guid sessionId, byte[] nonce, byte[] pin)
    {
        using HMACSHA256 hmac = new(token);
        return hmac.ComputeHash(IndependentTranscript(sessionId, nonce, pin));
    }

    // 未连接的本地资源仅用于会话单元测试；不声称执行了真实第二 TLS 握手。
    private static AuthenticatedControlSession CreateSession(
        byte[]? token = null, Guid? sessionId = null, byte[]? presentedPin = null,
        byte[]? expectedPin = null, Stream? transport = null)
    {
        byte[] pin = presentedPin ?? Convert.FromHexString(PinHex);
        Assert.True(ConnectionTarget.TryCreate(new Guid("11111111-2222-3333-4444-555555555555"),
            IPAddress.Loopback, 12345, Convert.ToHexString(expectedPin ?? pin), out ConnectionTarget? target));
        Assert.True(ConnectionIdentity.TryCreate(target!, pin, out ConnectionIdentity? identity));
        TlsConnection connection = new(identity!, new TcpClient(),
            new SslStream(transport ?? new MemoryStream(), leaveInnerStreamOpen: false));
        return new AuthenticatedControlSession(connection, SessionPermission.Control,
            sessionId ?? SessionId, "ABCDEF", token ?? Convert.FromHexString(TokenHex), 15_000);
    }

    // TEST-ONLY：只用于受控锁边界和 TLS 释放时的锁状态观测。
    private static object GetGate(AuthenticatedControlSession session) =>
        typeof(AuthenticatedControlSession).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(session)!;

    private sealed class RecordingDisposeStream : MemoryStream
    {
        internal Action? Disposing { get; set; }
        internal int DisposeCalls { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCalls++;
                Disposing?.Invoke();
            }
            base.Dispose(disposing);
        }
    }
}

// TEST-ONLY：限独立 TLS oracle 与秘密生命周期/清零观测；返回原数组，不增加生产密钥接口。
internal static class TestOnlyControlSessionSecrets
{
    internal static byte[] GetOwnedToken(AuthenticatedControlSession session) =>
        Assert.IsType<byte[]>(typeof(AuthenticatedControlSession)
            .GetField("_sessionToken", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session));
}
