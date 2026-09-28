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
