using System.Buffers.Binary;
using LanRemote.Core.Models;

namespace LanRemote.Transport.Tests;

public sealed class VideoFrameWriterTests
{
    [Fact]
    public async Task Writes_Exact_Manual_Golden_Vector_With_No_Prefix_Or_Pool_Tail()
    {
        RecordingVideoOwner owner = new(1024);
        VideoFrameTestData.Payload().CopyTo(owner.Bytes, 0);
        using EncodedFrame frame = VideoFrameTestData.Frame(owner);
        RecordingVideoStream stream = new();
        using VideoFrameWriter writer = new(stream);
        await writer.WriteFrameAsync(frame);

        Assert.Equal(VideoFrameTestData.Wire(), stream.Written);
        Assert.Equal(45, stream.BytesWritten);
        Assert.Equal(new[] { 40, 5 }, stream.WriteRequests);
        Assert.Equal(1, stream.FlushCalls);
        Assert.All(owner.Bytes[5..], value => Assert.Equal(0xCC, value));
        Assert.Equal(0, owner.DisposeCalls);
        writer.Dispose();
        Assert.Equal(0, owner.DisposeCalls);
        Assert.Equal(VideoFrameTestData.Payload(), frame.Payload.ToArray());
        await AssertTerminated(writer, stream, frame);
        frame.Dispose();
        Assert.Equal(1, owner.DisposeCalls);
    }

    [Fact]
    public async Task Consecutive_Frames_Are_Whole_And_Allow_Unordered_Duplicate_Full_Range_Ids()
    {
        ulong[] ids = [ulong.MaxValue, 0, 0, 0x8000000000000000ul];
        List<byte> expected = [];
        RecordingVideoStream stream = new();
        using VideoFrameWriter writer = new(stream);
        foreach (ulong id in ids)
        {
            RecordingVideoOwner owner = new(32);
            VideoFrameTestData.Payload().CopyTo(owner.Bytes, 0);
            using EncodedFrame frame = new(VideoCodec.Jpeg, 1920, 1080, id,
                0x1020304050607080L, 60, owner, 5);
            byte[] wire = VideoFrameTestData.Wire();
            BinaryPrimitives.WriteUInt64BigEndian(wire.AsSpan(8, 8), id);
            expected.AddRange(wire);
            await writer.WriteFrameAsync(frame);
            Assert.Equal(expected.ToArray(), stream.Written);
            Assert.Equal(0, owner.DisposeCalls);
        }

        Assert.Equal(ids.Length, stream.FlushCalls);
    }

    [Theory]
    [InlineData(0, 0L, 60, "video-codec")]
    [InlineData(2, 0L, 60, "video-codec")]
    [InlineData(255, 0L, 60, "video-codec")]
    [InlineData(1, -1L, 60, "video-timestamp")]
    [InlineData(1, long.MinValue, 60, "video-timestamp")]
    [InlineData(1, 0L, 0, "video-quality")]
    [InlineData(1, 0L, 39, "video-quality")]
    [InlineData(1, 0L, 86, "video-quality")]
    [InlineData(1, 0L, 255, "video-quality")]
    public async Task Invalid_Model_Is_Rejected_Before_Payload_Access_Or_First_Byte(
        byte codec, long timestamp, byte quality, string reason)
    {
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = new((VideoCodec)codec, 1, 1, 0, timestamp, quality, owner, 5);
        RecordingVideoStream stream = new();
        using VideoFrameWriter writer = new(stream);
        FrameProtocolException error = await Assert.ThrowsAsync<FrameProtocolException>(() => writer.WriteFrameAsync(frame));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(1, owner.MemoryReads);
        Assert.Empty(stream.Written);
        Assert.Empty(stream.WriteRequests);
        Assert.Equal(0, owner.DisposeCalls);
        await AssertTerminated(writer, stream, frame);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Payload_Slice_Must_Be_Accessible_Before_First_Byte(bool throwsOnAccess)
    {
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = VideoFrameTestData.Frame(owner);
        if (throwsOnAccess)
        {
            owner.ThrowOnMemoryRead = 2;
        }
        else
        {
            owner.VisibleLength = 4;
        }

        RecordingVideoStream stream = new();
        using VideoFrameWriter writer = new(stream);
        if (throwsOnAccess)
        {
            await Assert.ThrowsAsync<IOException>(() => writer.WriteFrameAsync(frame));
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => writer.WriteFrameAsync(frame));
        }

        Assert.Empty(stream.WriteRequests);
        Assert.Empty(stream.Written);
        Assert.Equal(0, owner.DisposeCalls);
        await AssertTerminated(writer, stream, frame);
    }

    [Fact]
    public async Task Disposed_Frame_Is_Rejected_Without_Bytes_Or_Second_Owner_Release()
    {
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = VideoFrameTestData.Frame(owner);
        frame.Dispose();
        RecordingVideoStream stream = new();
        using VideoFrameWriter writer = new(stream);
        FrameProtocolException error = await Assert.ThrowsAsync<FrameProtocolException>(() => writer.WriteFrameAsync(frame));
        Assert.Equal(FrameReader.RejectZeroLength, error.Reason);
        Assert.Empty(stream.WriteRequests);
        await AssertTerminated(writer, stream, frame);
        Assert.Equal(1, owner.DisposeCalls);
    }

    [Fact]
    public async Task Null_Frame_Also_Permanently_Terminates_Before_Any_Write()
    {
        RecordingVideoStream stream = new();
        using VideoFrameWriter writer = new(stream);
        await Assert.ThrowsAsync<ArgumentNullException>(() => writer.WriteFrameAsync(null!));
        Assert.Empty(stream.WriteRequests);
        await AssertTerminated(writer, stream, null!);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(39)]
    [InlineData(40)]
    [InlineData(41)]
    [InlineData(44)]
    public async Task Write_Failure_Closes_And_Preserves_Caller_Ownership_Even_If_Close_Throws(int boundary)
    {
        RecordingVideoOwner owner = new(32);
        VideoFrameTestData.Payload().CopyTo(owner.Bytes, 0);
        using EncodedFrame frame = VideoFrameTestData.Frame(owner);
        RecordingVideoStream stream = new() { FailWriteAt = boundary, FailDispose = true };
        using VideoFrameWriter writer = new(stream);
        IOException error = await Assert.ThrowsAsync<IOException>(() => writer.WriteFrameAsync(frame));
        Assert.Equal("测试写入失败。", error.Message);
        Assert.Equal(VideoFrameTestData.Wire()[..boundary], stream.Written);
        Assert.Equal(0, stream.FlushCalls);
        Assert.Equal(0, owner.DisposeCalls);
        await AssertTerminated(writer, stream, frame);
        Assert.Equal(VideoFrameTestData.Payload(), frame.Payload.ToArray());
        frame.Dispose();
        Assert.Equal(1, owner.DisposeCalls);
    }

    [Fact]
    public async Task Flush_Failure_Terminates_Even_After_All_Bytes_Are_Written()
    {
        RecordingVideoOwner owner = new(32);
        VideoFrameTestData.Payload().CopyTo(owner.Bytes, 0);
        using EncodedFrame frame = VideoFrameTestData.Frame(owner);
        RecordingVideoStream stream = new() { FailFlush = true };
        using VideoFrameWriter writer = new(stream);
        await Assert.ThrowsAsync<IOException>(() => writer.WriteFrameAsync(frame));
        Assert.Equal(VideoFrameTestData.Wire(), stream.Written);
        Assert.Equal(1, stream.FlushCalls);
        Assert.Equal(0, owner.DisposeCalls);
        await AssertTerminated(writer, stream, frame);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(39)]
    [InlineData(40)]
    [InlineData(41)]
    [InlineData(44)]
    public async Task Caller_Cancellation_Stops_Partial_Frame_Without_Releasing_Input(int boundary)
    {
        RecordingVideoOwner owner = new(32);
        VideoFrameTestData.Payload().CopyTo(owner.Bytes, 0);
        using EncodedFrame frame = VideoFrameTestData.Frame(owner);
        RecordingVideoStream stream = new() { GateWriteAt = boundary };
        using VideoFrameWriter writer = new(stream);
        using CancellationTokenSource cancellation = new();
        Task writing = writer.WriteFrameAsync(frame, cancellation.Token);
        await stream.WriteBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(writing.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
        Assert.Equal(VideoFrameTestData.Wire()[..boundary], stream.Written);
        Assert.All(stream.Tokens, token => Assert.Equal(cancellation.Token, token));
        Assert.Equal(0, owner.DisposeCalls);
        stream.ReleaseWrite();
        await AssertTerminated(writer, stream, frame);
        Assert.Equal(boundary, stream.BytesWritten);
    }

    [Fact]
    public async Task Already_Canceled_Call_Writes_Nothing_And_Terminates()
    {
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = VideoFrameTestData.Frame(owner);
        RecordingVideoStream stream = new();
        using VideoFrameWriter writer = new(stream);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteFrameAsync(frame, cancellation.Token));
        Assert.Empty(stream.WriteRequests);
        Assert.Equal(0, owner.DisposeCalls);
        await AssertTerminated(writer, stream, frame);
    }

    [Fact]
    public async Task Write_Does_Not_Complete_Or_Flush_Before_Last_Payload_Byte()
    {
        RecordingVideoOwner owner = new(32);
        VideoFrameTestData.Payload().CopyTo(owner.Bytes, 0);
        using EncodedFrame frame = VideoFrameTestData.Frame(owner);
        RecordingVideoStream stream = new() { GateWriteAt = 44 };
        using VideoFrameWriter writer = new(stream);
        Task writing = writer.WriteFrameAsync(frame);
        await stream.WriteBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(writing.IsCompleted);
        Assert.Equal(44, stream.BytesWritten);
        Assert.Equal(0, stream.FlushCalls);
        stream.ReleaseWrite();
        await writing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(VideoFrameTestData.Wire(), stream.Written);
        Assert.Equal(1, stream.FlushCalls);
        Assert.Equal(0, owner.DisposeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_Or_Overlapping_Write_Aborts_InFlight_Frame_Without_Interleaving(bool overlap)
    {
        RecordingVideoOwner firstOwner = new(32);
        RecordingVideoOwner secondOwner = new(32);
        VideoFrameTestData.Payload().CopyTo(firstOwner.Bytes, 0);
        using EncodedFrame first = VideoFrameTestData.Frame(firstOwner);
        using EncodedFrame second = VideoFrameTestData.Frame(secondOwner);
        RecordingVideoStream stream = new() { GateWriteAt = 41 };
        using VideoFrameWriter writer = new(stream);
        Task writing = writer.WriteFrameAsync(first);
        await stream.WriteBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (overlap)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteFrameAsync(second));
        }
        else
        {
            writer.Dispose();
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(() => writing);
        Assert.Equal(VideoFrameTestData.Wire()[..41], stream.Written);
        Assert.Equal(0, firstOwner.DisposeCalls);
        Assert.Equal(0, secondOwner.DisposeCalls);
        await AssertTerminated(writer, stream, first);
    }

    [Theory]
    [InlineData(1, 1, 8192, 40, 0L)]
    [InlineData(FrameLimits.MaxPayloadBytes, 8192, 1, 85, long.MaxValue)]
    public async Task Boundary_Model_Writes_Standard_Library_Vector(int length, int width, int height, byte quality, long timestamp)
    {
        RecordingVideoOwner owner = new(length + 17);
        owner.Bytes[0] = 0xA1;
        owner.Bytes[length - 1] = 0xB2;
        using EncodedFrame frame = new(VideoCodec.Jpeg, width, height, ulong.MaxValue, timestamp, quality, owner, length);
        RecordingVideoStream stream = new();
        using VideoFrameWriter writer = new(stream);
        await writer.WriteFrameAsync(frame);

        byte[] expectedHeader = VideoFrameTestData.Header();
        BinaryPrimitives.WriteUInt64BigEndian(expectedHeader.AsSpan(8, 8), ulong.MaxValue);
        BinaryPrimitives.WriteUInt64BigEndian(expectedHeader.AsSpan(16, 8), (ulong)timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(expectedHeader.AsSpan(24, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(expectedHeader.AsSpan(28, 4), (uint)height);
        expectedHeader[32] = quality;
        BinaryPrimitives.WriteUInt32BigEndian(expectedHeader.AsSpan(36, 4), (uint)length);
        byte[] written = stream.Written;
        Assert.Equal(40 + length, written.Length);
        Assert.Equal(expectedHeader, written[..40]);
        Assert.True(owner.Bytes.AsSpan(0, length).SequenceEqual(written.AsSpan(40)));
        Assert.Equal(0, owner.DisposeCalls);
    }

    [Fact]
    public async Task Dispose_Before_First_Write_Closes_Exactly_Once_And_Leaves_Input_Owned_By_Caller()
    {
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = VideoFrameTestData.Frame(owner);
        RecordingVideoStream stream = new();
        using VideoFrameWriter writer = new(stream);
        writer.Dispose();
        await AssertTerminated(writer, stream, frame);
        Assert.Empty(stream.WriteRequests);
        Assert.Equal(0, owner.DisposeCalls);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("cancel")]
    [InlineData("direct")]
    public async Task Review_Close_Before_Resource_Release_Failure_Is_Observable_Without_Retry(string source)
    {
        using CancellationTokenSource cancellation = new();
        Exception primary = source == "cancel"
            ? new OperationCanceledException("原始写入取消。", cancellation.Token)
            : new IOException("原始写入失败。");
        RecordingVideoStream resource = new()
        {
            FailWriteAt = 40,
            WriteFailure = primary,
            BeforeIoFailure = source == "cancel" ? cancellation.Cancel : null,
        };
        BeforeCloseFailureVideoStream stream = new(resource);
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = VideoFrameTestData.Frame(owner);
        using VideoFrameWriter writer = new(stream);
        try
        {
            Exception? actual = source == "direct"
                ? Record.Exception(writer.Dispose)
                : await Record.ExceptionAsync(() => writer.WriteFrameAsync(frame, cancellation.Token));
            Assert.Same(source == "direct" ? stream.CleanupFailure : primary, actual);
            if (source == "cancel")
            {
                Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(actual).CancellationToken);
            }

            Assert.False(stream.IsClosed);
            IReadOnlyList<Exception> snapshot = VideoReviewDiagnostics.Errors(writer);
            Assert.Same(stream.CleanupFailure, Assert.Single(snapshot));
            Assert.True(VideoReviewDiagnostics.IsTerminated(writer));
            Assert.False(VideoReviewDiagnostics.StreamDisposeSucceeded(writer));
            for (int i = 0; i < 32; i++)
            {
                await Assert.ThrowsAsync<ObjectDisposedException>(() => writer.WriteFrameAsync(frame));
                writer.Dispose();
            }

            await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            {
                Assert.Same(stream.CleanupFailure, Assert.Single(VideoReviewDiagnostics.Errors(writer)));
            })));
            Assert.Equal(1, stream.DisposeAttempts);
            Assert.Equal(source == "direct" ? 0 : 40, resource.BytesWritten);
            Assert.Equal(0, owner.DisposeCalls);
            Assert.Same(stream.CleanupFailure, Assert.Single(snapshot));
            VideoReviewDiagnostics.AssertReadOnly(snapshot);
        }
        finally
        {
            stream.ReleaseResources();
            Assert.True(stream.IsClosed);
        }
    }

    [Fact]
    public async Task Review_Termination_Is_Not_Join_Or_Hard_Interruption_Of_Stream()
    {
        using CancellationTokenSource cancellation = new();
        RecordingVideoStream resource = new() { GateWriteAt = 41 };
        BeforeCloseFailureVideoStream stream = new(resource);
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = VideoFrameTestData.Frame(owner);
        using VideoFrameWriter writer = new(stream);
        Task writing = writer.WriteFrameAsync(frame, cancellation.Token);
        try
        {
            await resource.WriteBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(stream.CleanupFailure, Record.Exception(writer.Dispose));
            Assert.False(stream.IsClosed);
            Assert.False(writing.IsCompleted);
            cancellation.Cancel();
            OperationCanceledException canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
            Assert.Equal(cancellation.Token, canceled.CancellationToken);
            Assert.Equal(0, owner.DisposeCalls);
            Assert.True(VideoReviewDiagnostics.IsTerminated(writer));
            Assert.False(VideoReviewDiagnostics.StreamDisposeSucceeded(writer));
            Assert.Same(stream.CleanupFailure, Assert.Single(VideoReviewDiagnostics.Errors(writer)));
            Assert.Equal(1, stream.DisposeAttempts);
        }
        finally
        {
            cancellation.Cancel();
            await Record.ExceptionAsync(() => writing);
            stream.ReleaseResources();
            Assert.True(stream.IsClosed);
        }
    }

    [Fact]
    public async Task Review_Caller_Cancellation_Takes_Precedence_Over_Already_Terminated()
    {
        using CancellationTokenSource cancellation = new();
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = VideoFrameTestData.Frame(owner);
        RecordingVideoStream stream = new();
        using VideoFrameWriter writer = new(stream);
        writer.Dispose();
        cancellation.Cancel();
        OperationCanceledException error = await Assert.ThrowsAsync<OperationCanceledException>(
            () => writer.WriteFrameAsync(frame, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(stream.WriteRequests);
        Assert.Equal(0, owner.DisposeCalls);
        Assert.Equal(1, stream.DisposeCalls);
    }

    [Theory]
    [InlineData(0ul)]
    [InlineData(ulong.MaxValue)]
    [InlineData(0x8000000000000000ul)]
    public async Task OptIn_Only_Overrides_Golden_Id_And_Borrows_Original_Payload_Across_Entrypoints(ulong wireId)
    {
        RecordingVideoOwner owner = new(1024);
        VideoFrameTestData.Payload().CopyTo(owner.Bytes, 0);
        using EncodedFrame frame = VideoFrameTestData.Frame(owner);
        ulong originalId = frame.FrameId;
        ReadOnlyMemory<byte> originalPayload = frame.Payload;
        OptInPayloadCaptureStream stream = new();
        using VideoFrameWriter writer = new(stream);
        byte[] expected = VideoFrameTestData.Wire();
        BinaryPrimitives.WriteUInt64BigEndian(expected.AsSpan(8, 8), wireId);

        await writer.WriteFrameAsync(frame);
        await writer.WriteFrameWithIdAsync(frame, wireId);
        await writer.WriteFrameAsync(frame);

        Assert.Equal(VideoFrameTestData.Wire().Concat(expected).Concat(VideoFrameTestData.Wire()).ToArray(), stream.ToArray());
        Assert.Equal(new[] { 40, 5, 40, 5, 40, 5 }, stream.Buffers.Select(buffer => buffer.Length));
        foreach (int index in new[] { 1, 3, 5 })
        {
            Assert.True(originalPayload.Equals(stream.Buffers[index]));
        }

        Assert.Equal(originalId, frame.FrameId);
        Assert.Equal(5, frame.PayloadLength);
        Assert.True(originalPayload.Equals(frame.Payload));
        Assert.Equal(VideoFrameTestData.Payload(), frame.Payload.ToArray());
        Assert.All(owner.Bytes[5..], value => Assert.Equal(0xCC, value));
        Assert.Equal(0, owner.DisposeCalls);
        writer.Dispose();
        Assert.Equal(0, owner.DisposeCalls);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => writer.WriteFrameAsync(frame));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => writer.WriteFrameWithIdAsync(frame, wireId));
        frame.Dispose();
        Assert.Equal(1, owner.DisposeCalls);
    }

    [Theory]
    [InlineData(0, 0L, 60, "video-codec")]
    [InlineData(2, 0L, 60, "video-codec")]
    [InlineData(255, 0L, 60, "video-codec")]
    [InlineData(1, -1L, 60, "video-timestamp")]
    [InlineData(1, long.MinValue, 60, "video-timestamp")]
    [InlineData(1, 0L, 0, "video-quality")]
    [InlineData(1, 0L, 39, "video-quality")]
    [InlineData(1, 0L, 86, "video-quality")]
    [InlineData(1, 0L, 255, "video-quality")]
    public async Task OptIn_Validates_Original_Model_Before_Payload_Access_Or_First_Byte(
        byte codec, long timestamp, byte quality, string reason)
    {
        RecordingVideoOwner owner = new(32) { ThrowOnMemoryRead = 2 };
        using EncodedFrame frame = new((VideoCodec)codec, 1, 1, 17, timestamp, quality, owner, 5);
        RecordingVideoStream stream = new();
        using VideoFrameWriter writer = new(stream);

        FrameProtocolException error = await Assert.ThrowsAsync<FrameProtocolException>(
            () => writer.WriteFrameWithIdAsync(frame, ulong.MaxValue));

        Assert.Equal(reason, error.Reason);
        Assert.Equal(1, owner.MemoryReads);
        Assert.Equal(17ul, frame.FrameId);
        Assert.Empty(stream.Written);
        Assert.Empty(stream.WriteRequests);
        Assert.Equal(0, stream.FlushCalls);
        Assert.Equal(0, owner.DisposeCalls);
        await OptInAssertBothTerminated(writer, stream, frame);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("disposed")]
    [InlineData("short")]
    [InlineData("owner-throws")]
    public async Task OptIn_Inaccessible_Input_Fails_Before_Any_Byte_Without_Transferring_Ownership(string source)
    {
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = VideoFrameTestData.Frame(owner);
        if (source == "disposed")
        {
            frame.Dispose();
        }
        else if (source == "short")
        {
            owner.VisibleLength = 4;
        }
        else if (source == "owner-throws")
        {
            owner.ThrowOnMemoryRead = 2;
        }

        RecordingVideoStream stream = new();
        using VideoFrameWriter writer = new(stream);
        Exception? error = await Record.ExceptionAsync(
            () => writer.WriteFrameWithIdAsync(source == "null" ? null! : frame, ulong.MaxValue));
        switch (source)
        {
            case "null":
                Assert.IsType<ArgumentNullException>(error);
                break;
            case "disposed":
                Assert.Equal(FrameReader.RejectZeroLength, Assert.IsType<FrameProtocolException>(error).Reason);
                break;
            case "short":
                Assert.IsType<ArgumentOutOfRangeException>(error);
                break;
            default:
                Assert.Same(owner.MemoryFailure, error);
                break;
        }

        Assert.Equal(source is "null" or "disposed" ? 1 : 2, owner.MemoryReads);
        Assert.Empty(stream.WriteRequests);
        Assert.Empty(stream.Written);
        Assert.Equal(0, stream.FlushCalls);
        Assert.Equal(source == "disposed" ? 1 : 0, owner.DisposeCalls);
        await OptInAssertBothTerminated(writer, stream, frame);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(39)]
    [InlineData(40)]
    [InlineData(41)]
    [InlineData(44)]
    public async Task OptIn_Partial_Write_Preserves_Primary_Error_And_Owner_When_Close_Also_Fails(int boundary)
    {
        RecordingVideoOwner owner = new(32);
        VideoFrameTestData.Payload().CopyTo(owner.Bytes, 0);
        using EncodedFrame frame = VideoFrameTestData.Frame(owner);
        IOException primary = new("原始覆盖编号写入失败。");
        RecordingVideoStream stream = new() { FailWriteAt = boundary, WriteFailure = primary, FailDispose = true };
        using VideoFrameWriter writer = new(stream);
        byte[] expected = VideoFrameTestData.Wire();
        BinaryPrimitives.WriteUInt64BigEndian(expected.AsSpan(8, 8), ulong.MaxValue);

        Assert.Same(primary, await Record.ExceptionAsync(() => writer.WriteFrameWithIdAsync(frame, ulong.MaxValue)));
        Assert.Equal(expected[..boundary], stream.Written);
        Assert.Equal(0, stream.FlushCalls);
        Assert.Equal(0, owner.DisposeCalls);
        Assert.Equal(0x0123456789ABCDEFul, frame.FrameId);
        Assert.Equal("测试关闭失败。", Assert.Single(VideoReviewDiagnostics.Errors(writer)).Message);
        Assert.False(VideoReviewDiagnostics.StreamDisposeSucceeded(writer));
        await OptInAssertBothTerminated(writer, stream, frame);
        Assert.Equal(VideoFrameTestData.Payload(), frame.Payload.ToArray());
    }

    [Fact]
    public async Task OptIn_Flush_Failure_After_All_Bytes_Preserves_Primary_Error_And_Terminates_Both_Entrypoints()
    {
        RecordingVideoOwner owner = new(32);
        VideoFrameTestData.Payload().CopyTo(owner.Bytes, 0);
        using EncodedFrame frame = VideoFrameTestData.Frame(owner);
        RecordingVideoStream stream = new() { FailFlush = true, FailDispose = true };
        using VideoFrameWriter writer = new(stream);
        byte[] expected = VideoFrameTestData.Wire();
        BinaryPrimitives.WriteUInt64BigEndian(expected.AsSpan(8, 8), 0);

        IOException error = await Assert.ThrowsAsync<IOException>(() => writer.WriteFrameWithIdAsync(frame, 0));

        Assert.Equal("测试刷新失败。", error.Message);
        Assert.Equal(expected, stream.Written);
        Assert.Equal(new[] { 40, 5 }, stream.WriteRequests);
        Assert.Equal(1, stream.FlushCalls);
        Assert.Equal(0, owner.DisposeCalls);
        Assert.Equal("测试关闭失败。", Assert.Single(VideoReviewDiagnostics.Errors(writer)).Message);
        await OptInAssertBothTerminated(writer, stream, frame);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(39)]
    [InlineData(40)]
    [InlineData(41)]
    [InlineData(44)]
    public async Task OptIn_Caller_Cancellation_Joins_Partial_Write_Without_Releasing_Input(int boundary)
    {
        RecordingVideoOwner owner = new(32);
        VideoFrameTestData.Payload().CopyTo(owner.Bytes, 0);
        using EncodedFrame frame = VideoFrameTestData.Frame(owner);
        RecordingVideoStream stream = new() { GateWriteAt = boundary };
        using VideoFrameWriter writer = new(stream);
        using CancellationTokenSource cancellation = new();
        byte[] expected = VideoFrameTestData.Wire();
        BinaryPrimitives.WriteUInt64BigEndian(expected.AsSpan(8, 8), 0x8000000000000000ul);
        Task writing = writer.WriteFrameWithIdAsync(frame, 0x8000000000000000ul, cancellation.Token);
        try
        {
            await stream.WriteBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(writing.IsCompleted);
            cancellation.Cancel();
            OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Equal(expected[..boundary], stream.Written);
            Assert.All(stream.Tokens, token => Assert.Equal(cancellation.Token, token));
            Assert.Equal(0, stream.FlushCalls);
            Assert.Equal(0, owner.DisposeCalls);
            await OptInAssertBothTerminated(writer, stream, frame);
        }
        finally
        {
            cancellation.Cancel();
            stream.ReleaseWrite();
            await Record.ExceptionAsync(() => writing);
        }

        Assert.Equal(boundary, stream.BytesWritten);
        Assert.Equal(0x0123456789ABCDEFul, frame.FrameId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptIn_Already_Canceled_Call_Precedes_Termination_Without_Reading_Payload(bool alreadyTerminated)
    {
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = VideoFrameTestData.Frame(owner);
        RecordingVideoStream stream = new();
        using VideoFrameWriter writer = new(stream);
        using CancellationTokenSource cancellation = new();
        if (alreadyTerminated)
        {
            writer.Dispose();
        }

        cancellation.Cancel();
        OperationCanceledException error = await Assert.ThrowsAsync<OperationCanceledException>(
            () => writer.WriteFrameWithIdAsync(frame, 0, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, owner.MemoryReads);
        Assert.Empty(stream.WriteRequests);
        Assert.Equal(0, stream.FlushCalls);
        Assert.Equal(0, owner.DisposeCalls);
        await OptInAssertBothTerminated(writer, stream, frame);
    }

    [Fact]
    public async Task OptIn_Write_Does_Not_Complete_Or_Flush_Before_Original_Payload_Finishes()
    {
        RecordingVideoOwner owner = new(32);
        VideoFrameTestData.Payload().CopyTo(owner.Bytes, 0);
        using EncodedFrame frame = VideoFrameTestData.Frame(owner);
        RecordingVideoStream stream = new() { GateWriteAt = 44 };
        using VideoFrameWriter writer = new(stream);
        byte[] expected = VideoFrameTestData.Wire();
        BinaryPrimitives.WriteUInt64BigEndian(expected.AsSpan(8, 8), ulong.MaxValue);
        Task writing = writer.WriteFrameWithIdAsync(frame, ulong.MaxValue);
        try
        {
            await stream.WriteBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(writing.IsCompleted);
            Assert.Equal(expected[..44], stream.Written);
            Assert.Equal(0, stream.FlushCalls);
            Assert.Equal(0, owner.DisposeCalls);
        }
        finally
        {
            stream.ReleaseWrite();
            await writing;
        }

        Assert.Equal(expected, stream.Written);
        Assert.Equal(1, stream.FlushCalls);
        Assert.Equal(0, owner.DisposeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptIn_Mixed_Overlapping_Entrypoints_Share_State_And_Never_Interleave(bool firstUsesOverride)
    {
        RecordingVideoOwner firstOwner = new(32);
        RecordingVideoOwner secondOwner = new(32);
        VideoFrameTestData.Payload().CopyTo(firstOwner.Bytes, 0);
        using EncodedFrame first = VideoFrameTestData.Frame(firstOwner);
        using EncodedFrame second = VideoFrameTestData.Frame(secondOwner);
        RecordingVideoStream stream = new() { GateWriteAt = 41 };
        using VideoFrameWriter writer = new(stream);
        byte[] expected = VideoFrameTestData.Wire();
        if (firstUsesOverride)
        {
            BinaryPrimitives.WriteUInt64BigEndian(expected.AsSpan(8, 8), ulong.MaxValue);
        }

        Task writing = firstUsesOverride
            ? writer.WriteFrameWithIdAsync(first, ulong.MaxValue)
            : writer.WriteFrameAsync(first);
        try
        {
            await stream.WriteBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<InvalidOperationException>(() => firstUsesOverride
                ? writer.WriteFrameAsync(second)
                : writer.WriteFrameWithIdAsync(second, 0));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => writing);
            Assert.Equal(expected[..41], stream.Written);
            Assert.Equal(new[] { 40, 5 }, stream.WriteRequests);
            Assert.Equal(0, stream.FlushCalls);
            Assert.Equal(1, secondOwner.MemoryReads);
            Assert.Equal(0, firstOwner.DisposeCalls);
            Assert.Equal(0, secondOwner.DisposeCalls);
            await OptInAssertBothTerminated(writer, stream, first);
        }
        finally
        {
            stream.ReleaseWrite();
            await Record.ExceptionAsync(() => writing);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptIn_Mixed_Overlap_Termination_Is_Not_Join_Or_Hard_Interruption_Of_Original_Io(bool firstUsesOverride)
    {
        RecordingVideoOwner firstOwner = new(32);
        RecordingVideoOwner secondOwner = new(32);
        VideoFrameTestData.Payload().CopyTo(firstOwner.Bytes, 0);
        using EncodedFrame first = VideoFrameTestData.Frame(firstOwner);
        using EncodedFrame second = VideoFrameTestData.Frame(secondOwner);
        RecordingVideoStream resource = new() { GateWriteAt = 41 };
        BeforeCloseFailureVideoStream stream = new(resource);
        using VideoFrameWriter writer = new(stream);
        byte[] expected = VideoFrameTestData.Wire();
        if (firstUsesOverride)
        {
            BinaryPrimitives.WriteUInt64BigEndian(expected.AsSpan(8, 8), ulong.MaxValue);
        }

        Task writing = firstUsesOverride
            ? writer.WriteFrameWithIdAsync(first, ulong.MaxValue)
            : writer.WriteFrameAsync(first);
        try
        {
            await resource.WriteBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<InvalidOperationException>(() => firstUsesOverride
                ? writer.WriteFrameAsync(second)
                : writer.WriteFrameWithIdAsync(second, 0));
            Assert.True(VideoReviewDiagnostics.IsTerminated(writer));
            Assert.False(VideoReviewDiagnostics.StreamDisposeSucceeded(writer));
            Assert.Same(stream.CleanupFailure, Assert.Single(VideoReviewDiagnostics.Errors(writer)));
            Assert.False(stream.IsClosed);
            Assert.False(writing.IsCompleted);
            Assert.Equal(expected[..41], resource.Written);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => writer.WriteFrameAsync(second));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => writer.WriteFrameWithIdAsync(second, 0));
            Assert.False(writing.IsCompleted);
            resource.ReleaseWrite();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => writing);
            Assert.Equal(expected, resource.Written);
            Assert.Equal(new[] { 40, 5 }, resource.WriteRequests);
            Assert.Equal(0, resource.FlushCalls);
            Assert.Equal(1, stream.DisposeAttempts);
            Assert.Equal(1, secondOwner.MemoryReads);
            Assert.Equal(0, firstOwner.DisposeCalls);
            Assert.Equal(0, secondOwner.DisposeCalls);
        }
        finally
        {
            resource.ReleaseWrite();
            await Record.ExceptionAsync(() => writing);
            stream.ReleaseResources();
        }

        Assert.True(stream.IsClosed);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("cancel")]
    public async Task OptIn_Primary_Error_Identity_And_Cleanup_Snapshot_Survive_Failed_Resource_Close(string source)
    {
        using CancellationTokenSource cancellation = new();
        Exception primary = source == "cancel"
            ? new OperationCanceledException("原始覆盖编号取消。", cancellation.Token)
            : new IOException("原始覆盖编号写入失败。");
        RecordingVideoStream resource = new()
        {
            FailWriteAt = 40,
            WriteFailure = primary,
            BeforeIoFailure = source == "cancel" ? cancellation.Cancel : null,
        };
        BeforeCloseFailureVideoStream stream = new(resource);
        RecordingVideoOwner owner = new(32);
        using EncodedFrame frame = VideoFrameTestData.Frame(owner);
        using VideoFrameWriter writer = new(stream);
        try
        {
            Exception? actual = await Record.ExceptionAsync(
                () => writer.WriteFrameWithIdAsync(frame, ulong.MaxValue, cancellation.Token));
            Assert.Same(primary, actual);
            if (source == "cancel")
            {
                Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(actual).CancellationToken);
            }

            IReadOnlyList<Exception> snapshot = VideoReviewDiagnostics.Errors(writer);
            Assert.Same(stream.CleanupFailure, Assert.Single(snapshot));
            Assert.False(stream.IsClosed);
            Assert.True(VideoReviewDiagnostics.IsTerminated(writer));
            Assert.False(VideoReviewDiagnostics.StreamDisposeSucceeded(writer));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => writer.WriteFrameAsync(frame));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => writer.WriteFrameWithIdAsync(frame, 0));
            writer.Dispose();
            Assert.Equal(1, stream.DisposeAttempts);
            Assert.Equal(40, resource.BytesWritten);
            Assert.Equal(0, resource.FlushCalls);
            Assert.Equal(0, owner.DisposeCalls);
            Assert.Same(stream.CleanupFailure, Assert.Single(snapshot));
            Assert.Same(stream.CleanupFailure, Assert.Single(VideoReviewDiagnostics.Errors(writer)));
            VideoReviewDiagnostics.AssertReadOnly(snapshot);
        }
        finally
        {
            stream.ReleaseResources();
        }

        Assert.True(stream.IsClosed);
    }

    private sealed class OptInPayloadCaptureStream : MemoryStream
    {
        internal List<ReadOnlyMemory<byte>> Buffers { get; } = [];

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Buffers.Add(buffer);
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private static async Task OptInAssertBothTerminated(VideoFrameWriter writer, RecordingVideoStream stream, EncodedFrame frame)
    {
        int writes = stream.WriteRequests.Count;
        int flushes = stream.FlushCalls;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => writer.WriteFrameWithIdAsync(frame, 0));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => writer.WriteFrameWithIdAsync(frame, ulong.MaxValue));
        await AssertTerminated(writer, stream, frame);
        Assert.True(VideoReviewDiagnostics.IsTerminated(writer));
        Assert.Equal(writes, stream.WriteRequests.Count);
        Assert.Equal(flushes, stream.FlushCalls);
    }

    private static async Task AssertTerminated(VideoFrameWriter writer, RecordingVideoStream stream, EncodedFrame frame)
    {
        int writes = stream.WriteRequests.Count;
        int flushes = stream.FlushCalls;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => writer.WriteFrameAsync(frame));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => writer.WriteFrameAsync(frame));
        writer.Dispose();
        writer.Dispose();
        Assert.Equal(writes, stream.WriteRequests.Count);
        Assert.Equal(flushes, stream.FlushCalls);
        Assert.Equal(1, stream.DisposeCalls);
    }
}
