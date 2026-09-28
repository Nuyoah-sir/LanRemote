using System.Buffers.Binary;
using LanRemote.Core.Models;

namespace LanRemote.Transport.Tests;

public sealed class VideoFrameReaderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(4096)]
    public async Task Reads_Manual_Golden_Vector_With_Fragmentation_And_Transfers_Only_Valid_Payload(int chunk)
    {
        RecordingVideoStream stream = new(VideoFrameTestData.Wire(), chunk);
        RecordingVideoPool pool = new();
        using VideoFrameReader reader = new(stream, pool.Rent);
        using EncodedFrame frame = Assert.IsType<EncodedFrame>(await reader.ReadFrameAsync());

        Assert.Equal(VideoCodec.Jpeg, frame.Codec);
        Assert.Equal(0x0123456789ABCDEFul, frame.FrameId);
        Assert.Equal(0x1020304050607080L, frame.TimestampUs);
        Assert.Equal(1920, frame.Width);
        Assert.Equal(1080, frame.Height);
        Assert.Equal(60, frame.JpegQuality);
        Assert.Equal(5, frame.PayloadLength);
        Assert.Equal(VideoFrameTestData.Payload(), frame.Payload.ToArray());
        Assert.Equal(45, stream.BytesRead);
        Assert.Equal(new[] { 5 }, pool.RequestedLengths);
        RecordingVideoOwner owner = Assert.Single(pool.Owners);
        Assert.All(owner.Bytes[5..], b => Assert.Equal(0xCC, b));
        Assert.Equal(0, owner.DisposeCalls);
        reader.Dispose();
        Assert.Equal(0, owner.DisposeCalls);
        frame.Dispose();
        frame.Dispose();
        Assert.Equal(1, owner.DisposeCalls);
        await AssertTerminated(reader, stream);
    }

    [Fact]
    public async Task Consecutive_Frames_Accept_Descending_And_Duplicate_Full_Range_Ids_Then_End_At_Eof()
    {
        ulong[] ids = [ulong.MaxValue, 0, 0, 0x8000000000000000ul];
        List<byte> wire = [];
        foreach (ulong id in ids)
        {
            byte[] bytes = VideoFrameTestData.Wire();
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8, 8), id);
            wire.AddRange(bytes);
        }

        RecordingVideoStream stream = new(wire.ToArray(), 7);
        RecordingVideoPool pool = new();
        using VideoFrameReader reader = new(stream, pool.Rent);
        for (int index = 0; index < ids.Length; index++)
        {
            using EncodedFrame frame = Assert.IsType<EncodedFrame>(await reader.ReadFrameAsync());
            Assert.Equal(ids[index], frame.FrameId);
            Assert.Equal(VideoFrameTestData.Payload(), frame.Payload.ToArray());
            Assert.Equal((index + 1) * 45, stream.BytesRead);
        }

        Assert.Null(await reader.ReadFrameAsync());
        Assert.Equal(ids.Length, pool.Owners.Count);
        Assert.All(pool.Owners, owner => Assert.Equal(1, owner.DisposeCalls));
        await AssertTerminated(reader, stream);
    }

    [Theory]
    [MemberData(nameof(VideoFrameHeaderTests.InvalidHeaders), MemberType = typeof(VideoFrameHeaderTests))]
    public async Task Bad_Header_Never_Reads_Body_Or_Rents_And_Cannot_Resynchronize(byte[] header, string reason)
    {
        // 在坏头后附合法帧也不可扫描重同步；即使声明 uint.MaxValue 也不能租用。
        RecordingVideoStream stream = new([.. header, .. VideoFrameTestData.Wire()]);
        RecordingVideoPool pool = new();
        using VideoFrameReader reader = new(stream, pool.Rent);
        FrameProtocolException error = await Assert.ThrowsAsync<FrameProtocolException>(() => reader.ReadFrameAsync());
        Assert.Equal(reason, error.Reason);
        Assert.Equal(40, stream.BytesRead);
        Assert.Equal(new[] { 40 }, stream.ReadRequests);
        Assert.Empty(pool.RequestedLengths);
        Assert.Empty(pool.Owners);
        await AssertTerminated(reader, stream);
    }

    [Fact]
    public async Task Control_Length_Prefix_Is_Not_Accepted_As_Video()
    {
        byte[] prefixed = [0, 0, 0, 45, .. VideoFrameTestData.Wire()];
        RecordingVideoStream stream = new(prefixed);
        RecordingVideoPool pool = new();
        using VideoFrameReader reader = new(stream, pool.Rent);
        FrameProtocolException error = await Assert.ThrowsAsync<FrameProtocolException>(() => reader.ReadFrameAsync());
        Assert.Equal("video-magic", error.Reason);
        Assert.Equal(40, stream.BytesRead);
        Assert.Empty(pool.RequestedLengths);
        await AssertTerminated(reader, stream);
    }

    [Fact]
    public async Task Empty_Eof_Returns_Null_Once_Closes_Stream_And_Does_Not_Rent()
    {
        RecordingVideoStream stream = new();
        RecordingVideoPool pool = new();
        using VideoFrameReader reader = new(stream, pool.Rent);
        Assert.Null(await reader.ReadFrameAsync());
        Assert.Equal(0, stream.BytesRead);
        Assert.Empty(pool.RequestedLengths);
        await AssertTerminated(reader, stream);
    }

    public static IEnumerable<object[]> TruncationBoundaries() =>
        Enumerable.Range(1, 44).Select(available => new object[] { available });

    [Theory]
    [MemberData(nameof(TruncationBoundaries))]
    public async Task Every_Truncated_Header_And_Body_Boundary_Reports_Part_And_Releases_Exactly_Once(int available)
    {
        RecordingVideoStream stream = new(VideoFrameTestData.Wire()[..available], 3);
        RecordingVideoPool pool = new();
        using VideoFrameReader reader = new(stream, pool.Rent);
        EndOfStreamException error = await Assert.ThrowsAsync<EndOfStreamException>(() => reader.ReadFrameAsync());
        Assert.StartsWith(available < 40 ? "video-header-truncated" : "video-payload-truncated", error.Message);
        Assert.Equal(available, stream.BytesRead);
        Assert.Equal(available < 40 ? 0 : 1, pool.Owners.Count);
        Assert.All(pool.Owners, owner => Assert.Equal(1, owner.DisposeCalls));
        await AssertTerminated(reader, stream);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(39)]
    [InlineData(40)]
    [InlineData(41)]
    [InlineData(44)]
    public async Task Caller_Cancellation_At_Header_And_Body_Boundaries_Releases_And_Terminates(int boundary)
    {
        RecordingVideoStream stream = new(VideoFrameTestData.Wire(), 3) { GateReadAt = boundary };
        RecordingVideoPool pool = new();
        using VideoFrameReader reader = new(stream, pool.Rent);
        using CancellationTokenSource cancellation = new();
        Task<EncodedFrame?> reading = reader.ReadFrameAsync(cancellation.Token);
        await stream.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(reading.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.Equal(boundary, stream.BytesRead);
        Assert.All(stream.Tokens, token => Assert.Equal(cancellation.Token, token));
        Assert.Equal(boundary < 40 ? 0 : 1, pool.Owners.Count);
        Assert.All(pool.Owners, owner => Assert.Equal(1, owner.DisposeCalls));
        stream.ReleaseRead();
        await AssertTerminated(reader, stream);
        Assert.Equal(boundary, stream.BytesRead);
    }

    [Fact]
    public async Task Already_Canceled_Call_Does_Not_Read_Or_Rent_But_Still_Terminates()
    {
        RecordingVideoStream stream = new(VideoFrameTestData.Wire());
        RecordingVideoPool pool = new();
        using VideoFrameReader reader = new(stream, pool.Rent);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadFrameAsync(cancellation.Token));
        Assert.Empty(stream.ReadRequests);
        Assert.Empty(pool.RequestedLengths);
        await AssertTerminated(reader, stream);
    }

    [Fact]
    public async Task Last_Byte_Must_Arrive_Before_Ownership_Transfer()
    {
        RecordingVideoStream stream = new(VideoFrameTestData.Wire(), 1) { GateReadAt = 44 };
        RecordingVideoPool pool = new();
        using VideoFrameReader reader = new(stream, pool.Rent);
        Task<EncodedFrame?> reading = reader.ReadFrameAsync();
        await stream.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(reading.IsCompleted);
        Assert.Equal(44, stream.BytesRead);
        Assert.Equal(0, Assert.Single(pool.Owners).DisposeCalls);
        stream.ReleaseRead();
        using EncodedFrame frame = Assert.IsType<EncodedFrame>(await reading.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(VideoFrameTestData.Payload(), frame.Payload.ToArray());
        Assert.Equal(45, stream.BytesRead);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(19)]
    [InlineData(40)]
    [InlineData(44)]
    public async Task Io_Failure_Preserves_Category_Even_If_Close_Throws(int boundary)
    {
        RecordingVideoStream stream = new(VideoFrameTestData.Wire()) { FailReadAt = boundary, FailDispose = true };
        RecordingVideoPool pool = new();
        using VideoFrameReader reader = new(stream, pool.Rent);
        IOException error = await Assert.ThrowsAsync<IOException>(() => reader.ReadFrameAsync());
        Assert.Equal("测试读取失败。", error.Message);
        Assert.Equal(boundary < 40 ? 0 : 1, pool.Owners.Count);
        Assert.All(pool.Owners, owner => Assert.Equal(1, owner.DisposeCalls));
        await AssertTerminated(reader, stream);
    }

    [Fact]
    public async Task Rent_Failure_Closes_Stream_Without_Reading_Body()
    {
        RecordingVideoStream stream = new(VideoFrameTestData.Wire());
        int rents = 0;
        using VideoFrameReader reader = new(stream, length =>
        {
            rents++;
            Assert.Equal(5, length);
            throw new IOException("测试租用失败。");
        });
        await Assert.ThrowsAsync<IOException>(() => reader.ReadFrameAsync());
        Assert.Equal(1, rents);
        Assert.Equal(40, stream.BytesRead);
        await AssertTerminated(reader, stream);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Owner_Access_Or_Frame_Construction_Failure_Releases_Owner(int failOnAccess)
    {
        RecordingVideoStream stream = new(VideoFrameTestData.Wire());
        RecordingVideoOwner owner = new(32) { ThrowOnMemoryRead = failOnAccess };
        using VideoFrameReader reader = new(stream, _ => owner);
        await Assert.ThrowsAsync<IOException>(() => reader.ReadFrameAsync());
        Assert.Equal(failOnAccess == 1 ? 40 : 45, stream.BytesRead);
        Assert.Equal(1, owner.DisposeCalls);
        await AssertTerminated(reader, stream);
    }

    [Fact]
    public async Task Too_Small_Rented_Buffer_Is_Released_Before_Body_Read()
    {
        RecordingVideoStream stream = new(VideoFrameTestData.Wire());
        RecordingVideoOwner owner = new(4);
        using VideoFrameReader reader = new(stream, _ => owner);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadFrameAsync());
        Assert.Equal(40, stream.BytesRead);
        Assert.Equal(1, owner.DisposeCalls);
        await AssertTerminated(reader, stream);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_Or_Overlapping_Read_Aborts_InFlight_Frame_And_Releases_Owner(bool overlap)
    {
        RecordingVideoStream stream = new(VideoFrameTestData.Wire()) { GateReadAt = 41 };
        RecordingVideoPool pool = new();
        using VideoFrameReader reader = new(stream, pool.Rent);
        Task<EncodedFrame?> reading = reader.ReadFrameAsync();
        await stream.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (overlap)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadFrameAsync());
        }
        else
        {
            reader.Dispose();
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(() => reading);
        Assert.Equal(41, stream.BytesRead);
        Assert.Equal(1, Assert.Single(pool.Owners).DisposeCalls);
        await AssertTerminated(reader, stream);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(FrameLimits.MaxPayloadBytes)]
    public async Task Payload_Min_And_Max_Are_Read_With_Default_Pool(int length)
    {
        byte[] wire = new byte[40 + length];
        VideoFrameTestData.Header().CopyTo(wire, 0);
        BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(36, 4), (uint)length);
        wire[40] = 0xA1;
        wire[^1] = 0xB2;
        RecordingVideoStream stream = new(wire, 65537);
        using VideoFrameReader reader = new(stream);
        using EncodedFrame frame = Assert.IsType<EncodedFrame>(await reader.ReadFrameAsync());
        Assert.Equal(length, frame.PayloadLength);
        Assert.Equal(wire.AsSpan(40).ToArray(), frame.Payload.ToArray());
        Assert.Equal(wire.Length, stream.BytesRead);
    }

    [Fact]
    public async Task Dispose_Before_First_Read_Closes_Exactly_Once()
    {
        RecordingVideoStream stream = new(VideoFrameTestData.Wire());
        using VideoFrameReader reader = new(stream);
        reader.Dispose();
        await AssertTerminated(reader, stream);
        Assert.Empty(stream.ReadRequests);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("dispose")]
    [InlineData("overlap")]
    [InlineData("dispose-and-cancel")]
    public async Task Review_Construction_Interruption_Prevents_Delivery_And_Releases_Once(string interruption)
    {
        using CancellationTokenSource cancellation = new();
        RecordingVideoStream stream = new(VideoFrameTestData.Wire());
        RecordingVideoOwner owner = new(32);
        using VideoFrameReader reader = new(stream, _ => owner);
        owner.OnMemoryRead = count =>
        {
            if (count != 2)
            {
                return;
            }

            if (interruption is "dispose" or "dispose-and-cancel")
            {
                reader.Dispose();
            }
            else if (interruption == "overlap")
            {
                Exception? overlap = Record.Exception(() => reader.ReadFrameAsync().GetAwaiter().GetResult());
                Assert.IsType<InvalidOperationException>(overlap);
            }

            if (interruption is "cancel" or "dispose-and-cancel")
            {
                cancellation.Cancel();
            }
        };

        EncodedFrame? delivered = null;
        Exception? failure = await Record.ExceptionAsync(async () => delivered = await reader.ReadFrameAsync(cancellation.Token));
        try
        {
            Assert.Null(delivered);
            if (interruption is "cancel" or "dispose-and-cancel")
            {
                OperationCanceledException canceled = Assert.IsType<OperationCanceledException>(failure);
                Assert.Equal(cancellation.Token, canceled.CancellationToken);
            }
            else
            {
                Assert.IsType<ObjectDisposedException>(failure);
            }

            Assert.Equal(2, owner.MemoryReads);
            Assert.Equal(1, owner.DisposeCalls);
            await AssertTerminated(reader, stream);
        }
        finally
        {
            // 红测若错误交付也必须由测试归还，不把实现泄漏掩盖为夹具泄漏。
            delivered?.Dispose();
        }
    }

    [Theory]
    [InlineData("io")]
    [InlineData("cancel")]
    [InlineData("constructor")]
    public async Task Review_Owner_Cleanup_Does_Not_Replace_Primary_Instance_Or_Cancellation_Token(string source)
    {
        using CancellationTokenSource cancellation = new();
        Exception primary = source == "cancel"
            ? new OperationCanceledException("原始读取取消。", cancellation.Token)
            : new IOException("原始读取或构造错误。");
        IOException cleanup = new("归还 owner 失败。");
        RecordingVideoOwner owner = new(32)
        {
            DisposeFailure = cleanup,
            ThrowOnMemoryRead = source == "constructor" ? 2 : int.MaxValue,
            MemoryFailure = primary,
        };
        RecordingVideoStream stream = new(VideoFrameTestData.Wire())
        {
            FailReadAt = source == "constructor" ? int.MaxValue : 40,
            ReadFailure = primary,
            BeforeIoFailure = source == "cancel" ? cancellation.Cancel : null,
        };
        using VideoFrameReader reader = new(stream, _ => owner);
        Exception? actual = await Record.ExceptionAsync(() => reader.ReadFrameAsync(cancellation.Token));
        Assert.Same(primary, actual);
        if (source == "cancel")
        {
            Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(actual).CancellationToken);
        }

        Assert.Equal(1, owner.DisposeCalls);
        Assert.Same(cleanup, Assert.Single(VideoReviewDiagnostics.Errors(reader)));
        Assert.True(VideoReviewDiagnostics.StreamDisposeSucceeded(reader));
        await AssertTerminated(reader, stream);
        Assert.Equal(1, owner.DisposeCalls);
    }

    [Fact]
    public async Task Review_Owner_Cleanup_Does_Not_Replace_Truncated_Payload_Eof()
    {
        IOException cleanup = new("归还 owner 失败。");
        RecordingVideoOwner owner = new(32) { DisposeFailure = cleanup };
        RecordingVideoStream stream = new(VideoFrameTestData.Wire()[..44]);
        using VideoFrameReader reader = new(stream, _ => owner);
        EndOfStreamException primary = await Assert.ThrowsAsync<EndOfStreamException>(() => reader.ReadFrameAsync());
        Assert.StartsWith("video-payload-truncated", primary.Message);
        Assert.Same(cleanup, Assert.Single(VideoReviewDiagnostics.Errors(reader)));
        Assert.Equal(1, owner.DisposeCalls);
        await AssertTerminated(reader, stream);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("cancel")]
    [InlineData("direct")]
    public async Task Review_Close_Before_Resource_Release_Failure_Is_Observable_Without_Retry(string source)
    {
        using CancellationTokenSource cancellation = new();
        Exception primary = source == "cancel"
            ? new OperationCanceledException("原始读取取消。", cancellation.Token)
            : new IOException("原始读取失败。");
        RecordingVideoStream resource = new(VideoFrameTestData.Wire())
        {
            FailReadAt = 40,
            ReadFailure = primary,
            BeforeIoFailure = source == "cancel" ? cancellation.Cancel : null,
        };
        BeforeCloseFailureVideoStream stream = new(resource);
        RecordingVideoOwner owner = new(32);
        using VideoFrameReader reader = new(stream, _ => owner);
        try
        {
            Exception? actual = source == "direct"
                ? Record.Exception(reader.Dispose)
                : await Record.ExceptionAsync(() => reader.ReadFrameAsync(cancellation.Token));
            Assert.Same(source == "direct" ? stream.CleanupFailure : primary, actual);
            if (source == "cancel")
            {
                Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(actual).CancellationToken);
            }

            Assert.False(stream.IsClosed);
            Assert.Same(stream.CleanupFailure, Assert.Single(VideoReviewDiagnostics.Errors(reader)));
            Assert.True(VideoReviewDiagnostics.IsTerminated(reader));
            Assert.False(VideoReviewDiagnostics.StreamDisposeSucceeded(reader));
            Assert.Equal(source == "direct" ? 0 : 1, owner.DisposeCalls);
            for (int i = 0; i < 32; i++)
            {
                await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.ReadFrameAsync());
                reader.Dispose();
            }

            Assert.Equal(1, stream.DisposeAttempts);
            Assert.Equal(source == "direct" ? 0 : 40, resource.BytesRead);
            Assert.Same(stream.CleanupFailure, Assert.Single(VideoReviewDiagnostics.Errors(reader)));
            VideoReviewDiagnostics.AssertReadOnly(VideoReviewDiagnostics.Errors(reader));
        }
        finally
        {
            stream.ReleaseResources();
            Assert.True(stream.IsClosed);
        }
    }

    [Fact]
    public async Task Review_Cleanup_Snapshots_Are_Bounded_Stable_And_Safe_During_InFlight_Cleanup()
    {
        IOException primary = new("原始读取失败。");
        IOException ownerFailure = new("归还 owner 失败。");
        RecordingVideoStream resource = new(VideoFrameTestData.Wire()) { FailReadAt = 40, ReadFailure = primary };
        BeforeCloseFailureVideoStream stream = new(resource);
        RecordingVideoOwner owner = new(32) { DisposeFailure = ownerFailure };
        using ManualResetEventSlim releaseOwner = new();
        TaskCompletionSource ownerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.BeforeDispose = () =>
        {
            ownerEntered.TrySetResult();
            Assert.True(releaseOwner.Wait(TimeSpan.FromSeconds(10)), "测试必须显式放行 owner 清理。");
        };
        using VideoFrameReader reader = new(stream, _ => owner);
        Task<EncodedFrame?>? reading = null;
        try
        {
            IReadOnlyList<Exception> emptySnapshot = VideoReviewDiagnostics.Errors(reader);
            Assert.Empty(emptySnapshot);
            reading = Task.Run(() => reader.ReadFrameAsync());
            await ownerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(VideoReviewDiagnostics.IsTerminated(reader));
            Assert.False(reading.IsCompleted);
            Assert.False(stream.IsClosed);
            IReadOnlyList<Exception> firstSnapshot = VideoReviewDiagnostics.Errors(reader);
            Assert.Same(stream.CleanupFailure, Assert.Single(firstSnapshot));
            await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            {
                Assert.Same(stream.CleanupFailure, Assert.Single(VideoReviewDiagnostics.Errors(reader)));
            })));
            releaseOwner.Set();
            Assert.Same(primary, await Record.ExceptionAsync(() => reading));

            IReadOnlyList<Exception> completeSnapshot = VideoReviewDiagnostics.Errors(reader);
            Assert.Equal(2, completeSnapshot.Count);
            Assert.Same(stream.CleanupFailure, completeSnapshot[0]);
            Assert.Same(ownerFailure, completeSnapshot[1]);
            Assert.Empty(emptySnapshot);
            Assert.Same(stream.CleanupFailure, Assert.Single(firstSnapshot));
            VideoReviewDiagnostics.AssertReadOnly(firstSnapshot);
            VideoReviewDiagnostics.AssertReadOnly(completeSnapshot);
            for (int i = 0; i < 32; i++)
            {
                reader.Dispose();
                await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.ReadFrameAsync());
            }

            Assert.Equal(2, VideoReviewDiagnostics.Errors(reader).Count);
            Assert.Equal(1, owner.DisposeCalls);
            Assert.Equal(1, stream.DisposeAttempts);
        }
        finally
        {
            releaseOwner.Set();
            if (reading is not null)
            {
                await Record.ExceptionAsync(() => reading);
            }

            // 即使红测在首次快照断言处结束，也要显式终止实例、释放夹具资源。
            Record.Exception(reader.Dispose);
            stream.ReleaseResources();
            Assert.True(stream.IsClosed);
        }
    }

    [Fact]
    public async Task Review_Termination_Is_Not_Join_Or_Hard_Interruption_Of_Stream()
    {
        using CancellationTokenSource cancellation = new();
        RecordingVideoStream resource = new(VideoFrameTestData.Wire()) { GateReadAt = 41 };
        BeforeCloseFailureVideoStream stream = new(resource);
        RecordingVideoOwner owner = new(32);
        using VideoFrameReader reader = new(stream, _ => owner);
        Task<EncodedFrame?> reading = reader.ReadFrameAsync(cancellation.Token);
        try
        {
            await resource.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(stream.CleanupFailure, Record.Exception(reader.Dispose));
            Assert.False(stream.IsClosed);
            Assert.False(reading.IsCompleted);
            Assert.Equal(0, owner.DisposeCalls);
            cancellation.Cancel();
            OperationCanceledException canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
            Assert.Equal(cancellation.Token, canceled.CancellationToken);
            Assert.Equal(1, owner.DisposeCalls);
            Assert.True(VideoReviewDiagnostics.IsTerminated(reader));
            Assert.False(VideoReviewDiagnostics.StreamDisposeSucceeded(reader));
            Assert.Same(stream.CleanupFailure, Assert.Single(VideoReviewDiagnostics.Errors(reader)));
            Assert.Equal(1, stream.DisposeAttempts);
        }
        finally
        {
            cancellation.Cancel();
            await Record.ExceptionAsync(() => reading);
            stream.ReleaseResources();
            Assert.True(stream.IsClosed);
        }
    }

    [Fact]
    public async Task Review_Dispose_After_Committed_Delivery_Does_Not_Reclaim_Frame()
    {
        BeforeCloseFailureVideoStream stream = new(new RecordingVideoStream(VideoFrameTestData.Wire()));
        RecordingVideoOwner owner = new(32);
        using VideoFrameReader reader = new(stream, _ => owner);
        try
        {
            using EncodedFrame delivered = Assert.IsType<EncodedFrame>(await reader.ReadFrameAsync());
            Assert.Same(stream.CleanupFailure, Record.Exception(reader.Dispose));
            Assert.Equal(VideoFrameTestData.Payload(), delivered.Payload.ToArray());
            Assert.Equal(0, owner.DisposeCalls);
            Assert.Same(stream.CleanupFailure, Assert.Single(VideoReviewDiagnostics.Errors(reader)));
            Assert.False(VideoReviewDiagnostics.StreamDisposeSucceeded(reader));
            delivered.Dispose();
            Assert.Equal(1, owner.DisposeCalls);
        }
        finally
        {
            stream.ReleaseResources();
            Assert.True(stream.IsClosed);
        }
    }

    [Fact]
    public async Task Review_Caller_Cancellation_Takes_Precedence_Over_Already_Terminated()
    {
        using CancellationTokenSource cancellation = new();
        RecordingVideoStream stream = new(VideoFrameTestData.Wire());
        using VideoFrameReader reader = new(stream);
        reader.Dispose();
        cancellation.Cancel();
        OperationCanceledException error = await Assert.ThrowsAsync<OperationCanceledException>(
            () => reader.ReadFrameAsync(cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(stream.ReadRequests);
        Assert.Equal(1, stream.DisposeCalls);
    }

    private static async Task AssertTerminated(VideoFrameReader reader, RecordingVideoStream stream)
    {
        int calls = stream.ReadRequests.Count;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.ReadFrameAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.ReadFrameAsync());
        reader.Dispose();
        reader.Dispose();
        Assert.Equal(calls, stream.ReadRequests.Count);
        Assert.Equal(1, stream.DisposeCalls);
    }
}
