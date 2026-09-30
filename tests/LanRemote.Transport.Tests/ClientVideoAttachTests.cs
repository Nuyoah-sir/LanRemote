using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanRemote.Core.Models;
using LanRemote.Transport.Auth;
using Xunit.Sdk;
using Child = LanRemote.Transport.AuthenticatedControlSession.ClientVideoLifetime;

namespace LanRemote.Transport.Tests;

public sealed class ClientVideoAttachTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private const int TestTimeout = 60_000;
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string SessionText = "00112233-4455-6677-8899-aabbccddeeff";
    private const string TokenHex = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
    private const string PinHex = "808182838485868788898A8B8C8D8E8F909192939495969798999A9B9C9D9E9F";
    private const string OtherPinHex = "A08182838485868788898A8B8C8D8E8F909192939495969798999A9B9C9D9E9F";
    private const string AckJson = """{"type":"video_attach_ack","channel":"video","protocol":1,"sessionId":"00112233-4455-6677-8899-aabbccddeeff"}""";
    private static readonly Guid DeviceId = new("11111111-2222-3333-4444-555555555555");

    [Theory(Timeout = TestTimeout)]
    [InlineData("not-delivered", false)]
    [InlineData("not-delivered", true)]
    [InlineData("cancelled", false)]
    [InlineData("cancelled", true)]
    [InlineData("null-factory", false)]
    public async Task EntryRejection_DoesNotConsumeAttemptOrTouchClock(string rejection, bool defaultConnector)
    {
        Fixture f = new(delivered: rejection != "not-delivered");
        using CancellationTokenSource caller = new();
        try
        {
            if (rejection == "cancelled") caller.Cancel();
            Exception error = await InvocationErrorAsync(() =>
            {
                if (rejection == "null-factory")
                    return f.Keep(f.Parent.AttachVideoCoreAsync(null!, caller.Token));
                return defaultConnector
                    ? f.Keep(f.Parent.AttachVideoCoreAsync(caller.Token)) : f.Start(token: caller.Token);
            });
            if (rejection == "null-factory") Assert.IsType<ArgumentNullException>(error);
            else if (rejection == "cancelled")
                Assert.Equal(caller.Token, Assert.IsAssignableFrom<OperationCanceledException>(error).CancellationToken);
            else Assert.IsType<InvalidOperationException>(error);
            Assert.Null(f.RegisteredChild);
            Assert.Equal(0, f.ConnectCalls);
            Assert.Equal(0, f.Clock.TimestampReads);
            Assert.Equal(0, f.Clock.TimerCreates);
            f.AssertControlLive();

            if (rejection == "not-delivered") f.Parent.CommitDelivery();
            Child child = await f.Start().WaitAsync(Guard);
            Assert.Same(child, f.RegisteredChild);
            Assert.Equal(1, f.ConnectCalls);
            await child.StopAndJoinAsync().WaitAsync(Guard);
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Connect_ReceivesFrozenControlIdentity_DefaultTimeoutsAndParentClock()
    {
        Fixture f = new();
        Pause connectReturn = f.NewPause();
        Task<TlsConnection> original = f.Keep(ConnectAsync());
        try
        {
            Task<Child> attach = f.Start(_ => original);
            await f.Connecting.Task.WaitAsync(Guard);
            await connectReturn.Reached.Task.WaitAsync(Guard);
            ConnectionTarget target = Assert.IsType<ConnectionTarget>(f.CapturedTarget);
            Assert.NotSame(f.Control.Target, target);
            Assert.NotSame(f.Parent.Identity.RemoteAddress, target.RemoteAddress);
            Assert.Equal(f.Parent.Identity.DeviceId, target.DeviceId);
            Assert.Equal(f.Parent.Identity.RemoteAddress, target.RemoteAddress);
            Assert.Equal(f.Parent.Identity.Port, target.Port);
            Assert.Equal(f.Parent.Identity.ExpectedCertSha256.ToArray(), target.ExpectedCertSha256.ToArray());
            Assert.Same(TransportTimeouts.Default, f.CapturedTimeouts);
            Assert.Same(f.Clock, f.CapturedClock);
            Assert.True(f.ConnectToken.CanBeCanceled);
            Assert.False(f.ConnectToken.IsCancellationRequested);
            Assert.True(MemoryMarshal.TryGetArray(f.Parent.Identity.ExpectedCertSha256, out var controlPin));
            Assert.True(MemoryMarshal.TryGetArray(target.ExpectedCertSha256, out var frozenPin));
            Assert.NotSame(controlPin.Array, frozenPin.Array);
            // 端点访问器返回的可变对象不是冻结目标；无需伪造一个全局 discovery 接缝。
            IPEndPoint endpointCopy = f.Parent.Identity.RemoteEndPoint;
            endpointCopy.Port = 54321;
            endpointCopy.Address = IPAddress.Parse("192.168.50.99");
            Assert.Equal(12345, target.Port);
            Assert.Equal(IPAddress.Parse("192.168.50.7"), target.RemoteAddress);
            await AssertAwaitPathAsync(original, attach);
            connectReturn.Open();
            Child child = await attach.WaitAsync(Guard);
            Assert.Same(f.Video.Connection, await original.WaitAsync(Guard));
            Assert.Same(child, f.RegisteredChild);
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }

        async Task<TlsConnection> ConnectAsync()
        {
            await connectReturn.WaitAsync();
            return f.Video.Connection;
        }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(1)]
    [InlineData(4096)]
    public async Task Success_UsesFreshNonceAndIndependentHmac_ClearsOriginalHello_LeavesFirstVideoFrame(int readChunkSize)
    {
        List<byte[]> nonces = [];
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Fixture f = new();
            f.Video.Ssl.ReadChunkSize = readChunkSize;
            try
            {
                Child child = await f.Start().WaitAsync(Guard);
                Assert.Same(child, f.RegisteredChild);
                byte[] nonce = AssertHello(f.Video.Ssl);
                Assert.DoesNotContain(nonces, previous => previous.SequenceEqual(nonce));
                nonces.Add(nonce);
                Assert.Equal(Framed(AckJson).Length, f.Video.Ssl.BytesRead);
                Assert.Equal(1, f.Video.Ssl.WriteCalls);
                Assert.Equal(1, f.Video.Ssl.FlushCalls);
                Assert.Equal(1, f.Clock.TimerCreates);
                Assert.All(f.Video.Ssl.IoTokens, token => Assert.Equal(f.ConnectToken, token));
                int payloadLength = Encoding.UTF8.GetByteCount(AckJson);
                int[] expectedReads = readChunkSize == 1
                    ? [.. Enumerable.Range(1, 4).Reverse(), .. Enumerable.Range(1, payloadLength).Reverse()]
                    : [4, payloadLength];
                Assert.Equal(expectedReads, f.Video.Ssl.ReadRequests);
                Assert.All(f.Video.Ssl.OriginalTasks, task => Assert.True(task.IsCompletedSuccessfully));
                Assert.DoesNotContain(typeof(Child).GetProperties(Fields), property =>
                    typeof(Stream).IsAssignableFrom(property.PropertyType));
                Assert.DoesNotContain(typeof(Child).GetMethods(Fields), method =>
                    method.Name.StartsWith("Write", StringComparison.Ordinal));

                int reads = f.Clock.TimestampReads;
                f.Clock.RejectSampling = true;
                using EncodedFrame frame = Assert.IsType<EncodedFrame>(await f.Read(child).WaitAsync(Guard));
                Assert.Equal(VideoFrameTestData.Payload(), frame.Payload.ToArray());
                Assert.Equal(Framed(AckJson).Length + VideoFrameTestData.Wire().Length, f.Video.Ssl.BytesRead);
                Assert.Null(await f.Read(child).WaitAsync(Guard));
                await child.StopAndJoinAsync().WaitAsync(Guard);
                Assert.Equal(reads, f.Clock.TimestampReads);
                Assert.Empty(child.LifetimeErrors);
                f.Video.AssertClosedOnce();
                f.AssertControlLive();
            }
            finally { await f.FinishAsync(); }
        }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("success", 1)]
    [InlineData("success", 4096)]
    [InlineData("wrong-session", 1)]
    [InlineData("wrong-session", 4096)]
    [InlineData("wrong-type", 1)]
    [InlineData("wrong-type", 4096)]
    [InlineData("payload-eof", 1)]
    [InlineData("payload-eof", 4096)]
    public async Task AckPayload_OriginalReadArray_IsClearedAfterSuccessRejectionOrTruncation(string outcome, int readChunkSize)
    {
        Fixture f = new();
        Pause payloadRead = f.NewPause();
        string json = outcome switch
        {
            "wrong-session" => AckJson.Replace(SessionText, "00112233-4455-6677-8899-aabbccddee00"),
            "wrong-type" => AckJson.Replace("video_attach_ack", "auth_success"),
            _ => AckJson,
        };
        byte[] ack = Framed(json);
        f.Video.Ssl.Input = outcome == "payload-eof" ? ack[..^1] : [.. ack, .. VideoFrameTestData.Wire()];
        f.Video.Ssl.ReadChunkSize = readChunkSize;
        f.Video.Ssl.GateStage = "ack-payload-read";
        f.Video.Ssl.IoPause = payloadRead;
        try
        {
            Task<Child> attach = f.Start();
            await payloadRead.Reached.Task.WaitAsync(Guard);
            // 引用来自 ReadAsync 的 MemoryMarshal.TryGetArray；开闸前原 I/O 尚未归还它。
            byte[] originalPayload = Assert.IsType<byte[]>(f.Video.Ssl.OriginalAckPayload);
            Assert.Equal(ack.Length - 4, originalPayload.Length);
            Assert.NotSame(f.Video.Ssl.Input, originalPayload);
            Assert.Equal((byte)'{', originalPayload[0]);
            Assert.Contains(originalPayload, value => value != 0);
            Task originalIo = Assert.Single(f.Video.Ssl.OriginalTasks, task => !task.IsCompleted);
            await AssertAwaitPathAsync(originalIo, attach);
            Assert.False(attach.IsCompleted);
            payloadRead.Open();

            if (outcome == "success")
                Assert.Same(f.Child, await attach.WaitAsync(Guard));
            else
            {
                Exception error = await ErrorAsync(attach);
                if (outcome == "payload-eof") Assert.IsType<EndOfStreamException>(error);
                else Assert.Equal(outcome == "wrong-session"
                        ? "video-attach-ack-session-mismatch" : VideoAttachAckFrame.RejectWrongType,
                    Assert.IsType<FrameProtocolException>(error).Reason);
                Assert.Same(error, Assert.Single(f.Child.LifetimeErrors));
                AssertOwnJoinCompleted(f);
                f.Video.AssertClosedOnce();
            }
            Assert.True(originalIo.IsCompletedSuccessfully);
            Assert.Same(originalPayload, f.Video.Ssl.OriginalAckPayload);
            Assert.All(originalPayload, value => Assert.Equal((byte)0, value));
            Assert.Equal((byte)'{', f.Video.Ssl.Input[4]);
            Assert.Equal(outcome == "payload-eof" ? ack.Length - 1 : ack.Length, f.Video.Ssl.BytesRead);
            AssertHello(f.Video.Ssl);
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("device")]
    [InlineData("address")]
    [InlineData("port")]
    [InlineData("expected-pin")]
    [InlineData("presented-pin")]
    [InlineData("both-pins")]
    public async Task ReturnedIdentityDeviation_IsRejectedBeforeAnyHelloWrite(string deviation)
    {
        Fixture f = new(deviation: deviation);
        try
        {
            // device/address/port/both-pins 的 PinsMatch 仍为真，不能只检查 TLS 自身的两个 pin。
            Assert.Equal(deviation is not ("expected-pin" or "presented-pin"), f.Video.Connection.Identity.PinsMatch);
            Exception error = await ErrorAsync(f.Start());
            Assert.IsType<AuthenticationException>(error);
            Assert.Equal(0, f.Video.Ssl.WriteCalls);
            Assert.Equal(0, f.Video.Ssl.FlushCalls);
            Assert.Empty(f.Video.Ssl.ReadRequests);
            AssertReportedRoots(error, f.Child.LifetimeErrors);
            AssertOwnJoinCompleted(f);
            f.Video.AssertClosedOnce();
            f.AssertControlLive();
            Assert.IsType<InvalidOperationException>(await InvocationErrorAsync(() => f.Start()));
            Assert.Equal(1, f.ConnectCalls);
        }
        finally { await f.FinishAsync(); }
    }

    public static IEnumerable<object[]> InvalidAcks()
    {
        yield return ["wrong-session", Framed(AckJson.Replace(SessionText, "00112233-4455-6677-8899-aabbccddee00")), false,
            "video-attach-ack-session-mismatch"];
        yield return ["wrong-type", Framed(AckJson.Replace("video_attach_ack", "auth_success")), false,
            VideoAttachAckFrame.RejectWrongType];
        yield return ["wrong-channel", Framed(AckJson.Replace("\"video\"", "\"control\"")), false,
            VideoAttachAckFrame.RejectWrongChannel];
        yield return ["wrong-protocol", Framed(AckJson.Replace("\"protocol\":1", "\"protocol\":2")), false,
            VideoAttachAckFrame.RejectWrongProtocol];
        yield return ["duplicate-key", Framed(AckJson[..^1] + ",\"protocol\":1}"), false,
            VideoAttachAckFrame.RejectMalformedJson];
        // AuthJson 先扫描完整单值，再检查尾随字节；{} 则反序列化为全空字段后报 missing。
        yield return ["trailing-json", Framed(AckJson + "{}"), false, VideoAttachAckFrame.RejectTrailingData];
        yield return ["missing-field", Framed("{}"), false, VideoAttachAckFrame.RejectMissingField];
        yield return ["zero-length", new byte[4], false, FrameReader.RejectZeroLength];
        yield return ["oversized", new byte[] { 0, 0, 0x10, 1 }, false, FrameReader.RejectTooLarge];
        yield return ["unsigned-length", new byte[] { 0xff, 0xff, 0xff, 0xff }, false, FrameReader.RejectTooLarge];
        yield return ["prefix-eof", new byte[] { 0, 0 }, true, null!];
        yield return ["payload-eof", new byte[] { 0, 0, 0, 4, (byte)'{' }, true, null!];
    }

    [Theory(Timeout = TestTimeout)]
    [MemberData(nameof(InvalidAcks))]
    public async Task InvalidFirstAck_FailsWithoutRetryOrDrainingFollowingFrame(
        string scenario, byte[] badAck, bool eof, string? expectedReason)
    {
        Fixture f = new();
        f.Video.Ssl.Input = eof ? badAck : [.. badAck, .. Framed(AckJson), .. VideoFrameTestData.Wire()];
        try
        {
            Exception error = await ErrorAsync(f.Start());
            if (eof) Assert.IsType<EndOfStreamException>(error);
            else
            {
                Assert.False(string.IsNullOrEmpty(expectedReason), scenario);
                Assert.Equal(expectedReason, Assert.IsType<FrameProtocolException>(error).Reason);
            }
            AssertReportedRoots(error, f.Child.LifetimeErrors);
            AssertOwnJoinCompleted(f);
            Assert.Equal(badAck.Length, f.Video.Ssl.BytesRead);
            AssertHello(f.Video.Ssl);
            f.Video.AssertClosedOnce();
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("sync")]
    [InlineData("async")]
    [InlineData("null-task")]
    [InlineData("null-connection")]
    public async Task ConnectFailure_ConsumesOnlyOwnAttempt_ThrowsOriginalSingleRoot(string outcome)
    {
        Fixture f = new();
        Pause connectReturn = f.NewPause();
        AggregateException primary = new("连接原始聚合。", new IOException("叶子一。"),
            new InvalidOperationException("必须保留的中间包装。", new IOException("叶子二。")));
        Task<TlsConnection>? original = outcome == "async" ? f.Keep(FailAsync()) : null;
        try
        {
            Task<Child> attach = f.Start(_ => outcome switch
            {
                "sync" => throw primary,
                "async" => original!,
                "null-task" => null!,
                _ => Task.FromResult<TlsConnection>(null!)
            });
            if (original is not null)
            {
                await f.Connecting.Task.WaitAsync(Guard);
                await connectReturn.Reached.Task.WaitAsync(Guard);
                await AssertAwaitPathAsync(original, attach);
                connectReturn.Open();
            }
            Exception observed = await ErrorAsync(attach);
            if (outcome is "sync" or "async") Assert.Same(primary, observed);
            else Assert.IsType<InvalidOperationException>(observed);
            Assert.Same(observed, Assert.Single(f.Child.LifetimeErrors));
            AssertOwnJoinCompleted(f);
            if (original is not null) await ObserveAsync(original);
            Assert.Empty(f.Video.Ssl.OriginalTasks);
            Assert.False(f.Video.Connection.IsCloseRequested);
            f.AssertControlLive();
            Assert.IsType<InvalidOperationException>(await InvocationErrorAsync(() => f.Start()));
            Assert.Equal(1, f.ConnectCalls);
        }
        finally { await f.FinishAsync(); }

        async Task<TlsConnection> FailAsync()
        {
            await connectReturn.WaitAsync();
            throw primary;
        }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task OriginalConnectTaskMultipleFaults_ReturnsWholeSavedRoot_NotOnlyAwaitFirstBranch()
    {
        Fixture f = new();
        Pause faultReturn = f.NewPause();
        IOException leaf = new("嵌套叶子。"), otherLeaf = new("兄弟叶子。");
        InvalidOperationException wrapper = new("中间包装。", leaf);
        AggregateException branch = new("用户聚合。", wrapper, new IOException("另一叶子。"));
        ApplicationException sibling = new("独立分支。", otherLeaf);
        Task<TlsConnection> original = f.Keep(Task.Factory.StartNew(() =>
        {
            faultReturn.WaitSynchronously();
            f.Keep(Task.Factory.StartNew(() => { throw branch; }, CancellationToken.None,
                TaskCreationOptions.AttachedToParent, TaskScheduler.Default));
            f.Keep(Task.Factory.StartNew(() => { throw sibling; }, CancellationToken.None,
                TaskCreationOptions.AttachedToParent, TaskScheduler.Default));
            return f.Video.Connection;
        }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default));
        try
        {
            Task<Child> attach = f.Start(_ => original);
            await f.Connecting.Task.WaitAsync(Guard);
            await faultReturn.Reached.Task.WaitAsync(Guard);
            await AssertAwaitPathAsync(original, attach);
            faultReturn.Open();
            Exception observed = await ErrorAsync(attach);
            await ObserveAsync(original);
            AggregateException originalTree = Assert.IsType<AggregateException>(original.Exception);
            AggregateException saved = Assert.IsType<AggregateException>(Assert.Single(f.Child.LifetimeErrors));
            Assert.Equal(2, originalTree.InnerExceptions.Count);
            Assert.Equal(2, saved.InnerExceptions.Count);
            for (int i = 0; i < 2; i++) Assert.Same(originalTree.InnerExceptions[i], saved.InnerExceptions[i]);
            Assert.Same(saved, observed);
            foreach (Exception node in new Exception[] { branch, sibling, wrapper, leaf, otherLeaf })
                Assert.True(ContainsReference(observed, node));
            AssertOwnJoinCompleted(f);
            Assert.False(f.Video.Connection.IsCloseRequested);
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("write")]
    [InlineData("flush")]
    public async Task OriginalSensitiveIoMultipleFaults_KeepsWholeTreeAndDeduplicatesCleanupSubtree(string stage)
    {
        Fixture f = new();
        Pause faultReturn = f.NewPause(), closeReturn = f.NewPause();
        IOException sharedLeaf = new("同时作为 TCP 清理错误的已存在子树。");
        AggregateException branch = new("用户聚合必须保留。", sharedLeaf, new IOException("第二叶子。"));
        ApplicationException sibling = new("另一原任务根。", new IOException("第三叶子。"));
        IOException sslError = new("额外 SSL 清理根。");
        Task first = f.Keep(FailAsync(branch));
        Task second = f.Keep(FailAsync(sibling));
        Task original = f.Keep(Task.WhenAll(first, second));
        f.Video.Ssl.GateStage = stage;
        f.Video.Ssl.OriginalFaultTask = original;
        f.Video.Ssl.ClosePause = closeReturn;
        f.Video.Client.DisposeFailure = sharedLeaf;
        f.Video.Ssl.DisposeFailure = sslError;
        try
        {
            Task<Child> attach = f.Start();
            await faultReturn.Reached.Task.WaitAsync(Guard);
            await AssertAwaitPathAsync(original, attach);
            AssertHelloStillOwned(f.Video.Ssl);
            faultReturn.Open();
            await ObserveAsync(original);
            await closeReturn.Reached.Task.WaitAsync(Guard);
            await ObserveAsync(f.AttachWorker);
            await AssertAwaitPathAsync(f.ChildTask("_join"), attach);
            closeReturn.Open();
            Exception observed = await ErrorAsync(attach);
            IReadOnlyList<Exception> roots = f.Child.LifetimeErrors;
            Assert.Equal(2, roots.Count);
            AggregateException saved = Assert.IsType<AggregateException>(roots[0]);
            // 非泛型 WhenAll 的多错顺序取原 Task 实际结果，不能假定等于输入顺序。
            AggregateException originalTree = Assert.IsType<AggregateException>(original.Exception);
            Assert.Equal(2, originalTree.InnerExceptions.Count);
            Assert.Contains(branch, originalTree.InnerExceptions);
            Assert.Contains(sibling, originalTree.InnerExceptions);
            Assert.Equal(originalTree.InnerExceptions.Count, saved.InnerExceptions.Count);
            for (int i = 0; i < originalTree.InnerExceptions.Count; i++)
                Assert.Same(originalTree.InnerExceptions[i], saved.InnerExceptions[i]);
            Assert.Same(sharedLeaf, branch.InnerExceptions[0]);
            Assert.Same(sslError, roots[1]);
            AssertReportedRoots(observed, roots);
            AssertOwnJoinCompleted(f);
            AssertHello(f.Video.Ssl);
            f.Video.AssertClosedOnce();
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }

        async Task FailAsync(Exception error)
        {
            await faultReturn.WaitAsync();
            throw error;
        }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("write")]
    [InlineData("flush")]
    [InlineData("ack-prefix")]
    [InlineData("ack-payload")]
    public async Task CloseRootContainingEarlierIoLeaf_ReplacesOnlyOuterRoot_LeavesChildHistoryIntact(string stage)
    {
        Fixture f = new();
        Pause ioReturn = f.NewPause(), closeReturn = f.NewPause();
        IOException leaf = new("最初原 I/O 失败。"), other = new("独立关闭叶子。");
        AggregateException closeRoot = new("关闭根反向包含原 I/O。", leaf, other);
        f.Video.Ssl.GateStage = stage;
        f.Video.Ssl.IoPause = ioReturn;
        f.Video.Ssl.IoFailure = leaf;
        f.Video.Ssl.ClosePause = closeReturn;
        f.Video.Ssl.DisposeFailure = closeRoot;
        try
        {
            Task<Child> attach = f.Start();
            await ioReturn.Reached.Task.WaitAsync(Guard);
            Task original = Assert.Single(f.Video.Ssl.OriginalTasks, task => !task.IsCompleted);
            await AssertAwaitPathAsync(original, attach);
            ioReturn.Open();
            Assert.Same(leaf, await ErrorAsync(original));
            await closeReturn.Reached.Task.WaitAsync(Guard);
            Assert.Same(leaf, await ErrorAsync(f.AttachWorker));
            IReadOnlyList<Exception> beforeClose = f.Child.LifetimeErrors;
            Assert.Same(leaf, Assert.Single(beforeClose));
            Task join = f.ChildTask("_join");
            await AssertAwaitPathAsync(join, attach);
            Assert.False(join.IsCompleted);
            Assert.False(attach.IsCompleted);

            closeReturn.Open();
            Exception observed = await ErrorAsync(attach);
            // 仅高层归并反向包含；不能将 child 的追加式历史合同改成只剩关闭根。
            Assert.Collection(f.Child.LifetimeErrors,
                error => Assert.Same(leaf, error), error => Assert.Same(closeRoot, error));
            Assert.Same(leaf, Assert.Single(beforeClose));
            Assert.Same(closeRoot, Assert.Single(f.Video.Connection.CleanupErrors));
            Assert.Same(closeRoot, observed);
            Assert.Collection(closeRoot.InnerExceptions,
                error => Assert.Same(leaf, error), error => Assert.Same(other, error));
            AssertOwnJoinCompleted(f);
            AssertHello(f.Video.Ssl);
            f.Video.AssertClosedOnce();
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("write")]
    [InlineData("flush")]
    [InlineData("ack-prefix")]
    [InlineData("ack-payload")]
    public async Task IoAndCloseTreesSharingOnlyLeaf_KeepBothOriginalRoots(string stage)
    {
        Fixture f = new();
        IOException sharedLeaf = new("仅共享这一叶子。"), ioLeaf = new("原 I/O 独立叶子。"), closeLeaf = new("关闭独立叶子。");
        InvalidOperationException ioBranch = new("原 I/O 包装。", sharedLeaf);
        ApplicationException closeBranch = new("关闭包装。", sharedLeaf);
        AggregateException primary = new("原 I/O 树。", ioBranch, ioLeaf);
        AggregateException closeRoot = new("关闭树。", closeBranch, closeLeaf);
        f.Video.Ssl.GateStage = stage;
        f.Video.Ssl.IoFailure = primary;
        f.Video.Ssl.DisposeFailure = closeRoot;
        try
        {
            Exception observed = await ErrorAsync(f.Start());
            Assert.Same(primary, await ErrorAsync(Assert.Single(f.Video.Ssl.OriginalTasks, task => task.IsFaulted)));
            Assert.False(ContainsReference(primary, closeRoot));
            Assert.False(ContainsReference(closeRoot, primary));
            Assert.Collection(f.Child.LifetimeErrors,
                error => Assert.Same(primary, error), error => Assert.Same(closeRoot, error));
            Assert.Collection(Assert.IsType<AggregateException>(observed).InnerExceptions,
                error => Assert.Same(primary, error), error => Assert.Same(closeRoot, error));
            Assert.Collection(primary.InnerExceptions,
                error => Assert.Same(ioBranch, error), error => Assert.Same(ioLeaf, error));
            Assert.Collection(closeRoot.InnerExceptions,
                error => Assert.Same(closeBranch, error), error => Assert.Same(closeLeaf, error));
            Assert.Same(sharedLeaf, ioBranch.InnerException);
            Assert.Same(sharedLeaf, closeBranch.InnerException);
            Assert.Same(closeRoot, Assert.Single(f.Video.Connection.CleanupErrors));
            AssertOwnJoinCompleted(f);
            AssertHello(f.Video.Ssl);
            f.Video.AssertClosedOnce();
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("write")]
    [InlineData("flush")]
    [InlineData("ack-prefix")]
    [InlineData("ack-payload")]
    public async Task SingleOriginalRoot_KeepsRepeatedBranchesAndLeavesWithoutFlattening(string stage)
    {
        Fixture f = new();
        IOException leaf = new("重复原叶子，也是关闭错误。");
        InvalidOperationException wrapper = new("原中间包装。", leaf);
        AggregateException branch = new("重复的原嵌套聚合。", wrapper, leaf);
        AggregateException primary = new("单根内的重复项必须原样保留。", branch, branch, leaf, leaf);
        f.Video.Ssl.GateStage = stage;
        f.Video.Ssl.IoFailure = primary;
        f.Video.Ssl.DisposeFailure = leaf;
        try
        {
            Exception observed = await ErrorAsync(f.Start());
            Assert.Same(primary, await ErrorAsync(Assert.Single(f.Video.Ssl.OriginalTasks, task => task.IsFaulted)));
            Assert.Same(primary, observed);
            Assert.Same(primary, Assert.Single(f.Child.LifetimeErrors));
            Assert.Same(leaf, Assert.Single(f.Video.Connection.CleanupErrors));
            Assert.Collection(primary.InnerExceptions,
                error => Assert.Same(branch, error), error => Assert.Same(branch, error),
                error => Assert.Same(leaf, error), error => Assert.Same(leaf, error));
            Assert.Collection(branch.InnerExceptions,
                error => Assert.Same(wrapper, error), error => Assert.Same(leaf, error));
            Assert.Same(leaf, wrapper.InnerException);
            AssertOwnJoinCompleted(f);
            AssertHello(f.Video.Ssl);
            f.Video.AssertClosedOnce();
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("write")]
    [InlineData("flush")]
    [InlineData("ack-prefix")]
    [InlineData("ack-payload")]
    public async Task CancelledOriginalIo_IgnoresClose_AttachWaitsForRealExitAndThenJoins(string stage)
    {
        Fixture f = new();
        using CancellationTokenSource caller = new();
        Pause ioReturn = f.NewPause();
        f.Video.Ssl.GateStage = stage;
        f.Video.Ssl.IoPause = ioReturn;
        try
        {
            Task<Child> attach = f.Start(token: caller.Token);
            await ioReturn.Reached.Task.WaitAsync(Guard);
            Task original = Assert.Single(f.Video.Ssl.OriginalTasks, task => !task.IsCompleted);
            await AssertAwaitPathAsync(original, attach);
            if (stage is "write" or "flush") AssertHelloStillOwned(f.Video.Ssl);
            else AssertHello(f.Video.Ssl);
            caller.Cancel();
            await f.ConnectCancelled.Task.WaitAsync(Guard);
            Task cancel = f.ChildTask("_cancel");
            await cancel.WaitAsync(Guard);
            Assert.True(f.Video.Connection.IsCloseRequested);
            await f.Video.Connection.CloseAsync().WaitAsync(Guard);
            await f.DeadlineWorker.WaitAsync(Guard);
            f.Video.AssertClosedOnce();
            // 关闭、取消 worker、deadline 都真正退出；仍须正向找到原 I/O 到返回任务的 await 链。
            await AssertAwaitPathAsync(original, attach);
            Assert.False(original.IsCompleted);
            Assert.False(attach.IsCompleted);
            if (stage is "write" or "flush") AssertHelloStillOwned(f.Video.Ssl);
            ioReturn.Open();
            await original.WaitAsync(Guard);
            Exception error = await ErrorAsync(attach);
            Assert.IsAssignableFrom<OperationCanceledException>(error);
            AssertReportedRoots(error, f.Child.LifetimeErrors);
            AssertOwnJoinCompleted(f);
            AssertHello(f.Video.Ssl);
            Assert.All(f.Video.Ssl.IoTokens, token => Assert.Equal(f.ConnectToken, token));
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task CancelledConnect_LateConnectionIsClosed_AndOuterWaitsForSlowClose()
    {
        Fixture f = new();
        using CancellationTokenSource caller = new();
        Pause connectReturn = f.NewPause(), closeReturn = f.NewPause();
        f.Video.Ssl.ClosePause = closeReturn;
        Task<TlsConnection> original = f.Keep(ConnectAsync());
        try
        {
            Task<Child> attach = f.Start(_ => original, caller.Token);
            await f.Connecting.Task.WaitAsync(Guard);
            await connectReturn.Reached.Task.WaitAsync(Guard);
            caller.Cancel();
            await f.ConnectCancelled.Task.WaitAsync(Guard);
            await Task.WhenAll(f.ChildTask("_cancel"), f.DeadlineWorker).WaitAsync(Guard);
            await AssertAwaitPathAsync(original, attach);
            Assert.False(f.Video.Connection.IsCloseRequested);
            connectReturn.Open();
            Assert.Same(f.Video.Connection, await original.WaitAsync(Guard));
            await closeReturn.Reached.Task.WaitAsync(Guard);
            await ObserveAsync(f.AttachWorker);
            Task join = f.ChildTask("_join");
            await AssertAwaitPathAsync(join, attach);
            Assert.Empty(f.Video.Ssl.OriginalTasks);
            Assert.False(join.IsCompleted);
            closeReturn.Open();
            Exception error = await ErrorAsync(attach);
            Assert.Equal(caller.Token, Assert.IsAssignableFrom<OperationCanceledException>(error).CancellationToken);
            AssertOwnJoinCompleted(f);
            f.Video.AssertClosedOnce();
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }

        async Task<TlsConnection> ConnectAsync()
        {
            await connectReturn.WaitAsync();
            return f.Video.Connection;
        }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("write", false)]
    [InlineData("write", true)]
    [InlineData("flush", false)]
    [InlineData("flush", true)]
    [InlineData("ack-prefix", false)]
    [InlineData("ack-prefix", true)]
    [InlineData("ack-payload", false)]
    [InlineData("ack-payload", true)]
    public async Task FailureOuterJoin_WaitsForSlowClose_AndPreservesOrderedRootsAfterLateCancellation(
        string stage, bool cleanupFailures)
    {
        Fixture f = new();
        using CancellationTokenSource caller = new();
        Pause ioReturn = f.NewPause(), closeReturn = f.NewPause();
        IOException leaf = new("原 I/O 的内层叶子。");
        AggregateException primary = new("原 I/O 根不能 flatten。", new InvalidOperationException("包装。", leaf));
        AggregateException callbackError = new("回调自己的根。", new IOException("回调叶子。"));
        IOException tcpError = new("TCP 关闭根。");
        ApplicationException sslError = new("SSL 关闭根。", new IOException("SSL 叶子。"));
        f.Video.Ssl.GateStage = stage;
        f.Video.Ssl.IoPause = ioReturn;
        f.Video.Ssl.IoFailure = primary;
        f.Video.Ssl.ClosePause = closeReturn;
        if (cleanupFailures)
        {
            f.Video.Client.DisposeFailure = tcpError;
            f.Video.Ssl.DisposeFailure = sslError;
        }
        try
        {
            Task<Child> attach = f.Start(token: caller.Token);
            await ioReturn.Reached.Task.WaitAsync(Guard);
            Task original = Assert.Single(f.Video.Ssl.OriginalTasks, task => !task.IsCompleted);
            if (cleanupFailures) f.Own(f.ConnectToken.Register(() => { throw callbackError; }));
            ioReturn.Open();
            Assert.Same(primary, await ErrorAsync(original));
            await closeReturn.Reached.Task.WaitAsync(Guard);
            await ObserveAsync(f.AttachWorker);
            await Task.WhenAll(f.ChildTask("_cancel"), f.DeadlineWorker).WaitAsync(Guard);
            Task join = f.ChildTask("_join");
            // 关键证据：不是 child 只是请求过关闭，而是外层返回任务实际 await 自己登记的 join。
            await AssertAwaitPathAsync(join, attach);
            Assert.False(join.IsCompleted);
            Assert.False(attach.IsCompleted);
            AssertHello(f.Video.Ssl);
            IReadOnlyList<Exception> beforeClose = f.Child.LifetimeErrors;
            Assert.Same(primary, beforeClose[0]);
            caller.Cancel();
            await AssertAwaitPathAsync(join, attach);
            closeReturn.Open();
            Exception observed = await ErrorAsync(attach);
            IReadOnlyList<Exception> roots = f.Child.LifetimeErrors;
            AssertReportedRoots(observed, roots);
            if (cleanupFailures)
            {
                Assert.Equal(4, roots.Count);
                Assert.Same(primary, roots[0]);
                AggregateException cancelRoot = Assert.IsType<AggregateException>(roots[1]);
                Assert.Same(callbackError, Assert.Single(cancelRoot.InnerExceptions));
                Assert.Same(tcpError, roots[2]);
                Assert.Same(sslError, roots[3]);
                Assert.Same(leaf, primary.InnerExceptions[0].InnerException);
                Assert.DoesNotContain(roots, root => root is OperationCanceledException);
            }
            else Assert.Same(primary, observed);
            AssertOwnJoinCompleted(f);
            f.Video.AssertClosedOnce();
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateLoser_NeverStopsOrJoinsWinner(bool winnerDelivered)
    {
        Fixture f = new();
        Pause connectReturn = f.NewPause();
        Task<TlsConnection> original = f.Keep(ConnectAsync());
        try
        {
            Task<Child> winner = f.Start(_ => original);
            await f.Connecting.Task.WaitAsync(Guard);
            await connectReturn.Reached.Task.WaitAsync(Guard);
            if (winnerDelivered)
            {
                connectReturn.Open();
                await winner.WaitAsync(Guard);
            }
            Child registered = f.Child;
            int clockReads = f.Clock.TimestampReads;
            Exception loser = await InvocationErrorAsync(() => f.Start());
            Assert.IsType<InvalidOperationException>(loser);
            AssertWinnerUntouched(f, registered);
            if (winnerDelivered) Assert.Equal(clockReads, f.Clock.TimestampReads);
            else await AssertAwaitPathAsync(original, winner);
            connectReturn.Open();
            Child child = await winner.WaitAsync(Guard);
            Assert.Same(registered, child);
            using EncodedFrame frame = Assert.IsType<EncodedFrame>(await f.Read(child).WaitAsync(Guard));
            Assert.Equal(VideoFrameTestData.Payload(), frame.Payload.ToArray());
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }

        async Task<TlsConnection> ConnectAsync()
        {
            await connectReturn.WaitAsync();
            return f.Video.Connection;
        }
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("timestamp")]
    [InlineData("frequency")]
    public async Task ReentrantClock_InnerRegistrationWins_OuterCannotCaptureAndStopIt(string clockPoint)
    {
        Fixture f = new();
        Pause connectReturn = f.NewPause();
        Task<TlsConnection> original = f.Keep(ConnectAsync());
        Task<Child>? winner = null;
        int outerConnectCalls = 0;
        try
        {
            f.Clock.Arm(clockPoint, () => winner = f.Start(_ => original));
            Exception loser = await InvocationErrorAsync(() => f.Keep(f.Parent.AttachVideoCoreAsync((_, _, _, _) =>
            {
                Interlocked.Increment(ref outerConnectCalls);
                return Task.FromResult(f.Video.Connection);
            })));
            Assert.IsType<InvalidOperationException>(loser);
            Assert.NotNull(winner);
            await f.Connecting.Task.WaitAsync(Guard);
            await connectReturn.Reached.Task.WaitAsync(Guard);
            Child registered = f.Child;
            AssertWinnerUntouched(f, registered);
            Assert.Equal(0, outerConnectCalls);
            await AssertAwaitPathAsync(original, winner);
            connectReturn.Open();
            Assert.Same(registered, await winner.WaitAsync(Guard));
            using EncodedFrame frame = Assert.IsType<EncodedFrame>(await f.Read(registered).WaitAsync(Guard));
            Assert.Equal(VideoFrameTestData.Payload(), frame.Payload.ToArray());
            f.AssertControlLive();
        }
        finally { await f.FinishAsync(); }

        async Task<TlsConnection> ConnectAsync()
        {
            await connectReturn.WaitAsync();
            return f.Video.Connection;
        }
    }

    private static void AssertWinnerUntouched(Fixture f, Child expected)
    {
        lock (f.ParentGate)
        {
            Assert.Same(expected, f.RegisteredChild);
            Assert.False(Field<bool>(expected, "_stopped"));
            Assert.Null(Field<Task?>(expected, "_join"));
            Assert.Null(Field<Task?>(expected, "_cancel"));
            Assert.Null(Field<Task?>(expected, "_videoClose"));
        }
        Assert.Equal(1, f.ConnectCalls);
        Assert.False(f.ConnectToken.IsCancellationRequested);
        Assert.False(f.Video.Connection.IsCloseRequested);
        Assert.Empty(expected.LifetimeErrors);
    }

    private static void AssertOwnJoinCompleted(Fixture f)
    {
        // 只读生产已经登记的原任务；不能调用 StopAndJoinAsync 修补被测外层遗漏的 join。
        Assert.True(f.AttachWorker.IsCompleted);
        Assert.True(f.ChildTask("_join").IsCompletedSuccessfully);
        Assert.True(f.ChildTask("_cancel").IsCompletedSuccessfully);
        Assert.True(f.DeadlineWorker.IsCompletedSuccessfully);
    }

    private static void AssertReportedRoots(Exception observed, IReadOnlyList<Exception> roots)
    {
        Assert.NotEmpty(roots);
        if (roots.Count == 1) Assert.Same(roots[0], observed);
        else
        {
            AggregateException aggregate = Assert.IsType<AggregateException>(observed);
            Assert.Equal(roots.Count, aggregate.InnerExceptions.Count);
            for (int i = 0; i < roots.Count; i++) Assert.Same(roots[i], aggregate.InnerExceptions[i]);
        }
    }

    private static bool ContainsReference(Exception root, Exception expected) => ReferenceEquals(root, expected) ||
        (root is AggregateException aggregate ? aggregate.InnerExceptions.Any(inner => ContainsReference(inner, expected))
            : root.InnerException is { } inner && ContainsReference(inner, expected));

    private static byte[] AssertHello(ProbeSslStream ssl)
    {
        byte[] original = Assert.IsType<byte[]>(ssl.OriginalWire);
        Assert.All(original, value => Assert.Equal((byte)0, value));
        byte[] snapshot = Assert.IsType<byte[]>(ssl.WireSnapshot);
        Assert.Equal(212, snapshot.Length);
        Assert.Equal(208u, BinaryPrimitives.ReadUInt32BigEndian(snapshot));
        using JsonDocument document = JsonDocument.Parse(snapshot.AsMemory(4));
        JsonElement json = document.RootElement;
        Assert.Equal(6, json.EnumerateObject().Count());
        Assert.Equal("channel_hello", json.GetProperty("type").GetString());
        Assert.Equal("video", json.GetProperty("channel").GetString());
        Assert.Equal(1, json.GetProperty("protocol").GetInt32());
        Assert.Equal(SessionText, json.GetProperty("sessionId").GetString());
        byte[] nonce = Convert.FromBase64String(json.GetProperty("attachNonce").GetString()!);
        byte[] proof = Convert.FromBase64String(json.GetProperty("attachProof").GetString()!);
        Assert.Equal(16, nonce.Length);
        Assert.Contains(nonce, value => value != 0);
        Assert.Equal(32, proof.Length);
        // 独立构造网络序 transcript，不调用生产 BuildTranscript/ComputeProof/VideoHelloWire。
        byte[] transcript = [.. Encoding.ASCII.GetBytes("LANREMOTE-VIDEO-V1\0"),
            .. Convert.FromHexString(SessionText.Replace("-", "")), .. nonce, .. Convert.FromHexString(PinHex)];
        Assert.Equal(83, transcript.Length);
        Assert.Equal(HMACSHA256.HashData(Convert.FromHexString(TokenHex), transcript), proof);
        return nonce;
    }

    private static void AssertHelloStillOwned(ProbeSslStream ssl)
    {
        byte[] original = Assert.IsType<byte[]>(ssl.OriginalWire);
        Assert.Equal(ssl.WireSnapshot, original);
        Assert.Contains(original, value => value != 0);
    }

    private static byte[] Framed(string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    private static async Task<Exception> InvocationErrorAsync(Func<Task> invoke)
    {
        Task? original = null;
        Exception? synchronous = Record.Exception(() => { original = invoke(); });
        return synchronous ?? await ErrorAsync(Assert.IsAssignableFrom<Task>(original));
    }

    private static async Task<Exception> ErrorAsync(Task original)
    {
        using CancellationTokenSource guardCancellation = new();
        Task guard = Task.Delay(Guard, guardCancellation.Token);
        try
        {
            Assert.Same(original, await Task.WhenAny(original, guard));
            Exception? error = await Record.ExceptionAsync(() => original);
            Assert.NotNull(error);
            return error;
        }
        finally { guardCancellation.Cancel(); }
    }

    private static async Task ObserveAsync(Task original)
    {
        using CancellationTokenSource guardCancellation = new();
        Task guard = Task.Delay(Guard, guardCancellation.Token);
        try
        {
            Assert.Same(original, await Task.WhenAny(original, guard));
            // Guard 断言在捕获范围之外，不能把保护超时混进业务异常或冒充原操作退出。
            try { await original; }
            catch { _ = original.Exception; }
        }
        finally { guardCancellation.Cancel(); }
    }

    private static async Task AssertAwaitPathAsync(Task awaited, Task returned)
    {
        using CancellationTokenSource guardCancellation = new();
        Task guard = Task.Delay(Guard, guardCancellation.Token);
        try
        {
            while (!HasAwaitPath(awaited, returned, new HashSet<Task>(), 16))
            {
                Assert.False(awaited.IsCompleted, "原等待任务已经退出，无法证明受闸控制的 await。");
                Assert.False(returned.IsCompleted, "返回任务提前退出，未等待原操作或 join。");
                if (guard.IsCompleted)
                    throw new XunitException("Guard：没有观察到原任务通向返回任务的 await 链；不支持的运行时布局必须显式失败。");
                await Task.Yield();
            }
        }
        finally { guardCancellation.Cancel(); }
    }

    private static bool HasAwaitPath(Task awaited, Task returned, HashSet<Task> visited, int remaining)
    {
        if (remaining == 0 || !visited.Add(awaited)) return false;
        foreach (object continuation in Continuations(awaited))
        {
            object? box = continuation is Delegate action ? action.Target : continuation;
            if (box is not Task next) continue;
            object? machine = RuntimeField(next.GetType(), "StateMachine")?.GetValue(next);
            bool exactAwait = machine is not null && machine.GetType().GetFields(Fields)
                .Where(field => field.Name.StartsWith("<>u__", StringComparison.Ordinal))
                .Select(field => field.GetValue(machine)).OfType<object>()
                .Any(awaiter => AwaiterReferencesTask(awaiter, awaited));
            // 只接受原 Task 上直接登记的运行时 UnwrapPromise，不把测试 TCS/WhenAny 当成证据。
            bool unwrap = next.GetType().FullName?.StartsWith("System.Threading.Tasks.UnwrapPromise`", StringComparison.Ordinal) == true;
            if (!exactAwait && !unwrap) continue;
            if (ReferenceEquals(next, returned) || HasAwaitPath(next, returned, visited, remaining - 1)) return true;
        }
        return false;
    }

    private static bool AwaiterReferencesTask(object awaiter, Task awaited) =>
        awaiter.GetType().GetFields(Fields).Any(field =>
        {
            object? value = field.GetValue(awaiter);
            if (value is Task task) return ReferenceEquals(task, awaited);
            // FrameReader 直接 await ReadAsync 的 ValueTask<int>；只认可其真实 Task 后端引用。
            if (field.FieldType != typeof(ValueTask<int>) && field.FieldType != typeof(ValueTask)) return false;
            return ReferenceEquals(RuntimeField(field.FieldType, "_obj")?.GetValue(value), awaited);
        });

    private static object[] Continuations(Task task)
    {
        FieldInfo field = RuntimeField(typeof(Task), "m_continuationObject")
            ?? throw new XunitException("运行时缺少 Task continuation 字段，必须适配探针，不能跳过 await 证明。");
        object? continuation = field.GetValue(task);
        if (continuation is IList list)
        {
            lock (list) return list.Cast<object?>().OfType<object>().ToArray();
        }
        return continuation is null ? [] : [continuation];
    }

    private static FieldInfo? RuntimeField(Type type, string name)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
            if (current.GetField(name, Fields | BindingFlags.DeclaredOnly) is { } field) return field;
        return null;
    }

    private static T Field<T>(object instance, string name) => (T)(RuntimeField(instance.GetType(), name)
        ?? throw new XunitException($"缺少已核对的生产字段 {instance.GetType().Name}.{name}。" )).GetValue(instance)!;
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // TCS 只负责开闸/到达通知，工厂、SSL 与关闭句柄始终来自实际 async/Task.Run。
    private sealed class Pause
    {
        private readonly TaskCompletionSource _release = Signal();
        internal TaskCompletionSource Reached { get; } = Signal();
        internal void Open() => _release.TrySetResult();
        internal async Task WaitAsync()
        {
            Reached.TrySetResult();
            await _release.Task.ConfigureAwait(false);
        }
        internal void WaitSynchronously()
        {
            Reached.TrySetResult();
            try { _release.Task.WaitAsync(Guard).GetAwaiter().GetResult(); }
            catch (TimeoutException) { throw new XunitException("Guard：测试未释放同步闸门，不是业务超时。"); }
        }
    }

    private sealed class ProbeClock : TimeProvider
    {
        private Action? _onTimestamp, _onFrequency;
        private int _timestampReads, _timerCreates;
        internal int TimestampReads => Volatile.Read(ref _timestampReads);
        internal int TimerCreates => Volatile.Read(ref _timerCreates);
        internal bool RejectSampling;
        internal void Arm(string point, Action callback)
        {
            if (point == "timestamp") _onTimestamp = callback;
            else _onFrequency = callback;
        }
        internal void Disarm()
        {
            _onTimestamp = _onFrequency = null;
            RejectSampling = false;
        }
        public override long GetTimestamp()
        {
            Interlocked.Increment(ref _timestampReads);
            if (RejectSampling) throw new XunitException("已交付视频不应再读取附着时钟。");
            Interlocked.Exchange(ref _onTimestamp, null)?.Invoke();
            return 0;
        }
        public override long TimestampFrequency
        {
            get
            {
                if (RejectSampling) throw new XunitException("已交付视频不应再读取附着时钟频率。");
                Interlocked.Exchange(ref _onFrequency, null)?.Invoke();
                return TimeSpan.TicksPerSecond;
            }
        }
        public override DateTimeOffset GetUtcNow() => throw new XunitException("不得读取墙钟。");
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _timerCreates);
            return new InertTimer();
        }
        private sealed class InertTimer : ITimer
        {
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class Fixture
    {
        private readonly ConcurrentBag<Task> _tasks = [];
        private readonly ConcurrentBag<CancellationTokenRegistration> _registrations = [];
        private readonly List<Pause> _pauses = [];
        private int _connectCalls;
        internal Endpoint Control { get; } = new(Identity(), []);
        internal Endpoint Video { get; }
        internal ProbeClock Clock { get; } = new();
        internal AuthenticatedControlSession Parent { get; }
        internal object ParentGate => Field<object>(Parent, "_gate");
        internal Child? RegisteredChild { get { lock (ParentGate) return Field<Child?>(Parent, "_videoLifetime"); } }
        internal Child Child => Assert.IsType<Child>(RegisteredChild);
        internal Task AttachWorker { get { lock (ParentGate) return Field<Task>(Field<object>(Child, "_attach"), "Worker"); } }
        internal Task DeadlineWorker { get { lock (ParentGate) return Field<Task>(Field<object>(Child, "_deadline"), "_worker"); } }
        internal Task ChildTask(string field) { lock (ParentGate) return Assert.IsAssignableFrom<Task>(Field<Task?>(Child, field)); }
        internal TaskCompletionSource Connecting { get; } = Signal();
        internal TaskCompletionSource ConnectCancelled { get; } = Signal();
        internal int ConnectCalls => Volatile.Read(ref _connectCalls);
        internal ConnectionTarget? CapturedTarget;
        internal TransportTimeouts? CapturedTimeouts;
        internal TimeProvider? CapturedClock;
        internal CancellationToken ConnectToken;

        internal Fixture(bool delivered = true, string deviation = "")
        {
            Video = new Endpoint(Identity(deviation), [.. Framed(AckJson), .. VideoFrameTestData.Wire()]);
            Parent = new AuthenticatedControlSession(Control.Connection, SessionPermission.Control,
                new Guid(SessionText), "ABCDEF", Convert.FromHexString(TokenHex), 15_000, 0, Clock);
            if (delivered) Parent.CommitDelivery();
        }

        private static (ConnectionTarget Target, ConnectionIdentity Identity) Identity(string deviation = "")
        {
            Assert.True(ConnectionTarget.TryCreate(deviation == "device" ? new Guid("22222222-2222-3333-4444-555555555555") : DeviceId,
                IPAddress.Parse(deviation == "address" ? "192.168.50.8" : "192.168.50.7"),
                deviation == "port" ? 12346 : 12345,
                deviation is "expected-pin" or "both-pins" ? OtherPinHex : PinHex, out ConnectionTarget? target));
            Assert.True(ConnectionIdentity.TryCreate(target!, Convert.FromHexString(
                deviation is "presented-pin" or "both-pins" ? OtherPinHex : PinHex), out ConnectionIdentity? identity));
            return (target!, identity!);
        }

        internal T Keep<T>(T task) where T : Task
        {
            if (task is not null) _tasks.Add(task);
            return task!;
        }
        internal void Own(CancellationTokenRegistration registration) => _registrations.Add(registration);
        internal Pause NewPause()
        {
            Pause pause = new();
            _pauses.Add(pause);
            return pause;
        }
        internal Task<Child> Start(Func<CancellationToken, Task<TlsConnection>>? connect = null, CancellationToken token = default) =>
            Keep(Parent.AttachVideoCoreAsync((target, timeouts, clock, cancellation) =>
            {
                Assert.False(Monitor.IsEntered(ParentGate));
                Interlocked.Increment(ref _connectCalls);
                CapturedTarget = target;
                CapturedTimeouts = timeouts;
                CapturedClock = clock;
                ConnectToken = cancellation;
                Own(cancellation.Register(() => ConnectCancelled.TrySetResult()));
                try { return Keep(connect is null ? Task.FromResult(Video.Connection) : connect(cancellation)); }
                finally { Connecting.TrySetResult(); }
            }, token));
        internal Task<EncodedFrame?> Read(Child child) => Keep(child.ReadFrameAsync());
        internal void AssertControlLive()
        {
            Assert.Same(Control.Ssl, Parent.Stream);
            Assert.False(Control.Connection.IsCloseRequested);
            Assert.Equal(0, Control.Ssl.DisposeCalls);
            Assert.Equal(0, Control.Client.DisposeCalls);
            Assert.Empty(Control.Ssl.OriginalTasks);
            Assert.Empty(Control.Connection.CleanupErrors);
        }
        internal async Task FinishAsync()
        {
            Clock.Disarm();
            foreach (Pause pause in _pauses) pause.Open();
            try
            {
                Task join = Parent.CloseAndJoinAsync();
                Task childJoin = RegisteredChild?.StopAndJoinAsync() ?? Task.CompletedTask;
                await ObserveAsync(Task.WhenAll(_tasks.Append(join).Append(childJoin)));
                // 原工厂可在开闸后才发布子 Task；再取快照，确保没有遗留原 Task。
                await ObserveAsync(Task.WhenAll(_tasks));
            }
            finally
            {
                try
                {
                    await ObserveAsync(Task.WhenAll(Control.Connection.CloseAsync(), Video.Connection.CloseAsync()));
                    await ObserveAsync(Task.WhenAll(Control.Ssl.OriginalTasks.Concat(Video.Ssl.OriginalTasks)));
                    foreach (CancellationTokenRegistration registration in _registrations) await registration.DisposeAsync();
                    foreach (Task<EncodedFrame?> read in _tasks.OfType<Task<EncodedFrame?>>())
                        if (read.IsCompletedSuccessfully) read.GetAwaiter().GetResult()?.Dispose();
                }
                finally
                {
                    try { Control.ReleaseResources(); }
                    finally { Video.ReleaseResources(); }
                }
            }
        }
    }

    // 未连接 TCP + 派生 SSL，只控制原 I/O/释放，不执行或宣称真实 TLS 握手。
    private sealed class Endpoint
    {
        internal ProbeTcpClient Client { get; } = new();
        internal ProbeSslStream Ssl { get; }
        internal ConnectionTarget Target { get; }
        internal TlsConnection Connection { get; }
        internal Endpoint((ConnectionTarget Target, ConnectionIdentity Identity) identity, byte[] input)
        {
            Target = identity.Target;
            Ssl = new ProbeSslStream { Input = input };
            Connection = new TlsConnection(identity.Identity, Client, Ssl);
        }
        internal void AssertClosedOnce()
        {
            Assert.True(Connection.IsCloseRequested);
            Assert.True(Connection.CloseAsync().IsCompletedSuccessfully);
            Assert.Equal(1, Client.DisposeCalls);
            Assert.Equal(1, Ssl.DisposeCalls);
        }
        internal void ReleaseResources()
        {
            try { Ssl.ReleaseResources(); }
            finally { Client.ReleaseResources(); }
        }
    }

    private sealed class ProbeTcpClient : TcpClient
    {
        private int _disposeCalls;
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        internal Exception? DisposeFailure;
        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    Interlocked.Increment(ref _disposeCalls);
                    if (DisposeFailure is not null) throw DisposeFailure;
                }
            }
            finally { base.Dispose(disposing); }
        }
        internal void ReleaseResources() => base.Dispose(true);
    }

    private sealed class ProbeSslStream() : SslStream(new MemoryStream(), leaveInnerStreamOpen: false)
    {
        private readonly object _ioGate = new();
        private readonly ConcurrentQueue<Task> _originalTasks = [];
        private readonly ConcurrentQueue<CancellationToken> _tokens = [];
        private readonly ConcurrentQueue<int> _readRequests = [];
        private int _offset, _writeCalls, _flushCalls, _disposeCalls;
        internal byte[] Input = [];
        internal int ReadChunkSize = int.MaxValue;
        internal string? GateStage;
        internal Pause? IoPause, ClosePause;
        internal Exception? IoFailure, DisposeFailure;
        internal Task? OriginalFaultTask;
        internal byte[]? OriginalWire, WireSnapshot, OriginalAckPayload;
        internal int BytesRead => Volatile.Read(ref _offset);
        internal int WriteCalls => Volatile.Read(ref _writeCalls);
        internal int FlushCalls => Volatile.Read(ref _flushCalls);
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        internal Task[] OriginalTasks { get { lock (_ioGate) return _originalTasks.ToArray(); } }
        internal CancellationToken[] IoTokens => _tokens.ToArray();
        internal int[] ReadRequests => _readRequests.ToArray();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (_ioGate)
            {
                Interlocked.Increment(ref _writeCalls);
                Assert.True(MemoryMarshal.TryGetArray(buffer, out var segment));
                Assert.Equal(0, segment.Offset);
                Assert.Equal(212, segment.Count);
                Assert.Equal(segment.Count, segment.Array!.Length);
                OriginalWire = segment.Array;
                WireSnapshot = buffer.ToArray();
                _tokens.Enqueue(cancellationToken);
                Task original = GateStage == "write" && OriginalFaultTask is not null ? OriginalFaultTask : WriteCoreAsync();
                _originalTasks.Enqueue(original);
                return new ValueTask(original);
            }
        }
        private async Task WriteCoreAsync() => await AtStageAsync("write").ConfigureAwait(false);
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            lock (_ioGate)
            {
                Interlocked.Increment(ref _flushCalls);
                _tokens.Enqueue(cancellationToken);
                AssertHelloStillOwned(this);
                Task original = GateStage == "flush" && OriginalFaultTask is not null ? OriginalFaultTask : FlushCoreAsync();
                _originalTasks.Enqueue(original);
                return original;
            }
        }
        private async Task FlushCoreAsync() => await AtStageAsync("flush").ConfigureAwait(false);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (_ioGate)
            {
                // 到达通知可能先于 async 方法返回；读探针快照也取此锁，不能漏掉尚未登记的原 Task。
                _readRequests.Enqueue(buffer.Length);
                _tokens.Enqueue(cancellationToken);
                bool ackPayload = _offset >= 4 && (OriginalAckPayload is null || _offset - 4 < OriginalAckPayload.Length);
                if (ackPayload)
                {
                    // 只保留生产 ReadAsync 借出的原数组；每个分片及 EOF 读取都核对同一数组和偏移。
                    Assert.True(MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)buffer, out var segment));
                    Assert.NotNull(segment.Array);
                    OriginalAckPayload ??= segment.Array;
                    Assert.Same(OriginalAckPayload, segment.Array);
                    Assert.Equal(_offset - 4, segment.Offset);
                    Assert.Equal(OriginalAckPayload.Length - segment.Offset, segment.Count);
                }
                Task<int> original = ReadCoreAsync(buffer, ackPayload);
                _originalTasks.Enqueue(original);
                return new ValueTask<int>(original);
            }
        }
        private async Task<int> ReadCoreAsync(Memory<byte> buffer, bool ackPayload)
        {
            await AtStageAsync(_offset == 0 ? "ack-prefix" : "ack-payload").ConfigureAwait(false);
            int count = Math.Min(ReadChunkSize, Math.Min(buffer.Length, Input.Length - _offset));
            Input.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            if (ackPayload && count > 0) await AtStageAsync("ack-payload-read").ConfigureAwait(false);
            return count;
        }
        private async Task AtStageAsync(string stage)
        {
            // 故意忽略 cancellation/Dispose；原内存仍借出，生产只能等待原 Task 退出。
            if (stage != GateStage) return;
            if (IoPause is not null) await IoPause.WaitAsync().ConfigureAwait(false);
            if (IoFailure is not null) throw IoFailure;
        }
        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    Interlocked.Increment(ref _disposeCalls);
                    ClosePause?.WaitSynchronously();
                    if (DisposeFailure is not null) throw DisposeFailure;
                }
            }
            finally { base.Dispose(disposing); }
        }
        internal void ReleaseResources() => base.Dispose(true);
    }
}
