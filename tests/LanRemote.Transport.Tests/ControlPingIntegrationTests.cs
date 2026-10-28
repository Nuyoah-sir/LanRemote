using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using LanRemote.Core.Models;

namespace LanRemote.Transport.Tests;

/// <summary>真实 Control 字节路径上的 ping/pong 与 RTT；用 TCP 裸流代替已认证 TLS 传输底座，不冒充握手。</summary>
public sealed class ControlPingIntegrationTests
{
    private static readonly Guid Session = new("f0c884b7-0ec3-4ed4-90e6-7ba35f39c49a");
    private static readonly TimeSpan PollLimit = TimeSpan.FromSeconds(2);

    [Fact]
    public void Telemetry_unregister_requires_the_same_meter_and_preserves_replacement()
    {
        ControlPingTelemetry telemetry = new();
        PingRttMeter first = new();
        PingRttMeter replacement = new();

        telemetry.Register(Session, first);
        Assert.Equal(1, telemetry.Count);
        Assert.True(telemetry.TryGet(Session, out ControlPingTelemetry.SessionSnapshot? before));
        Assert.NotNull(before);
        Assert.Null(before!.LatestRtt);
        Assert.Equal(0, before.PendingCount);
        Assert.Equal(0, before.SampleCount);

        telemetry.Unregister(Session, replacement);
        Assert.True(telemetry.TryGet(Session, out _));
        telemetry.Unregister(Session, first);
        Assert.False(telemetry.TryGet(Session, out _));

        telemetry.Register(Session, replacement);
        telemetry.Unregister(Session, first);
        Assert.True(telemetry.TryGet(Session, out _));
        telemetry.Unregister(Session, replacement);
        Assert.Equal(0, telemetry.Count);
    }

    [Fact(Timeout = 60_000)]
    public async Task Ping_write_shares_sequence_and_busy_catalog_skips_without_arm()
    {
        RecordingStream stream = new();
        ManualDeadlineClock clock = new();
        PingRttMeter meter = new(clock);
        TaskCompletionSource closeRequired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<DisplayCatalogFrame> catalog = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource catalogRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        HostControlWriter writer = new(stream, Session, clock, TimeSpan.FromSeconds(2), closeRequired, firstSequence: 41);
        try
        {
            Task<HostControlWriter.WriteResult> sendingCatalog = writer.WriteCatalogAsync(() =>
            {
                catalogRequested.TrySetResult();
                return new ValueTask<DisplayCatalogFrame>(catalog.Task);
            });
            await catalogRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(writer.HasActiveWrite);
            Assert.Null(writer.TryWritePingAsync(meter));
            Assert.Equal(0, meter.PendingCount);
            Assert.Empty(stream.Writes);

            catalog.TrySetResult(new DisplayCatalogFrame(91, 92,
                [new DisplayInfo(new DisplayId(0), "Screen A", 0, 0, 1920, 1080, true)]));
            HostControlWriter.WriteResult catalogResult = await sendingCatalog.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(41, catalogResult.Sequence);
            Assert.Equal("host_status", Payload(stream.Writes[0]).RootElement.GetProperty("type").GetString());

            Task<HostControlWriter.WriteResult> sendingPing = Assert.IsAssignableFrom<Task<HostControlWriter.WriteResult>>(
                writer.TryWritePingAsync(meter));
            Assert.Equal(1, meter.PendingCount);
            HostControlWriter.WriteResult pingResult = await sendingPing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(42, pingResult.Sequence);
            using var ping = Payload(stream.Writes[1]);
            Assert.Equal("ping", ping.RootElement.GetProperty("type").GetString());
            Assert.Equal(Session.ToString("D"), ping.RootElement.GetProperty("sessionId").GetString());
            Assert.Equal(42, ping.RootElement.GetProperty("seq").GetInt64());

            Assert.True(meter.Settle(42, out TimeSpan roundTrip));
            Assert.Equal(TimeSpan.Zero, roundTrip);
            Assert.Equal(0, meter.PendingCount);
            Assert.Equal(TimeSpan.Zero, meter.LatestRtt);
            Assert.Equal(1, meter.SampleCount);
            Assert.False(writer.StopRequired.IsCompleted);
        }
        finally
        {
            catalog.TrySetCanceled();
            writer.RequestStop();
            await ObserveAsync(writer.Completion);
            await stream.DisposeAsync();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Host_ping_receives_real_client_pong_and_settles_per_session_rtt()
    {
        TcpPair control = await TcpPair.CreateAsync();
        TcpPair video = await TcpPair.CreateAsync();
        byte[] pin = Enumerable.Range(128, 32).Select(value => (byte)value).ToArray();
        Assert.True(ConnectionTarget.TryCreate(new Guid("11111111-2222-3333-4444-555555555555"),
            IPAddress.Parse("192.168.50.7"), 12345, Convert.ToHexString(pin), out ConnectionTarget? target));
        Assert.True(ConnectionIdentity.TryCreate(target!, pin, out ConnectionIdentity? identity));
        TlsConnection controlConnection = new(identity!, control.Client, control.ClientSsl);
        TlsConnection videoConnection = new(identity!, video.Client, video.ClientSsl);
        byte[] sessionToken = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        ManualDeadlineClock clock = new();
        AuthenticatedControlSession client = new(controlConnection, SessionPermission.Control, Session,
            "ABCDEF", sessionToken, 15_000, clock.GetTimestamp(), clock, V2AgreementFixture.Create(Session));
        ControlPingTelemetry telemetry = new();
        PingRttMeter meter = new(TimeProvider.System);
        HostControlWriter? writer = null;
        HostDisplayRequestReader? reader = null;
        HostControlPingLoop? pingLoop = null;
        try
        {
            client.CommitDelivery();
            byte[] ack = Encoding.UTF8.GetBytes($$"""{"type":"video_attach_ack","channel":"video","protocol":1,"sessionId":"{{Session:D}}"}""");
            await FrameWriter.WriteFrameAsync(video.Server, ack, 4096, TimeSpan.FromSeconds(5));
            await client.AttachVideoWithControlMonitorCoreAsync((_, _, _, _) => Task.FromResult(videoConnection))
                .WaitAsync(TimeSpan.FromSeconds(5));

            telemetry.Register(Session, meter);
            TaskCompletionSource closeRequired = new(TaskCreationOptions.RunContinuationsAsynchronously);
            writer = new(control.Server, Session, TimeProvider.System, TimeSpan.FromSeconds(2), closeRequired, firstSequence: 41);
            Task<byte?> firstRead = ReadFirstByteAsync(control.Server);
            reader = new(control.Server, firstRead, Session, TimeProvider.System, TimeSpan.FromSeconds(2),
                (_, _) => Task.CompletedTask, closeRequired, CancellationToken.None) { PongMeter = meter };
            Task reading = reader.Start();
            pingLoop = new(writer, meter, TimeProvider.System,
                TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(500));
            Task looping = pingLoop.Start();

            bool settled = await PollUntilAsync(() => meter.SampleCount == 1, PollLimit);
            Assert.True(settled, "Host did not settle the echoed ping sample.");
            Assert.NotNull(meter.LatestRtt);
            Assert.True(meter.LatestRtt >= TimeSpan.Zero);
            Assert.Equal(0, meter.PendingCount);
            Assert.True(telemetry.TryGet(Session, out ControlPingTelemetry.SessionSnapshot? snapshot));
            Assert.NotNull(snapshot);
            Assert.Equal(meter.LatestRtt, snapshot!.LatestRtt);
            Assert.Equal(1, snapshot.SampleCount);
            Assert.NotNull(client.ControlMonitorCompletion);
            Assert.False(client.ControlMonitorCompletion!.IsCompleted);

            pingLoop.RequestStop();
            reader.RequestStop();
            writer.RequestStop();
            Task clientJoin = client.CloseAndJoinAsync();
            control.Dispose();
            video.Dispose();
            await clientJoin.WaitAsync(TimeSpan.FromSeconds(5));
            await ObserveAsync(Task.WhenAll(looping, reading, writer.Completion));
            Assert.True(looping.IsCompleted);
            Assert.True(reading.IsCompleted);
            Assert.True(writer.Completion.IsCompleted);
            telemetry.Unregister(Session, meter);
            Assert.False(telemetry.TryGet(Session, out _));
        }
        finally
        {
            telemetry.Unregister(Session, meter);
            pingLoop?.RequestStop();
            reader?.RequestStop();
            writer?.RequestStop();
            await ObserveAsync(client.CloseAndJoinAsync());
            control.Dispose();
            video.Dispose();
        }
    }

    private static async Task ObserveAsync(Task original)
    {
        try { await original; }
        catch { _ = original.Exception; }
    }

    private static async Task<bool> PollUntilAsync(Func<bool> predicate, TimeSpan limit)
    {
        long started = Environment.TickCount64;
        while (!predicate() && Environment.TickCount64 - started < limit.TotalMilliseconds)
            await Task.Delay(5);
        return predicate();
    }

    private static async Task<byte?> ReadFirstByteAsync(Stream stream)
    {
        byte[] first = new byte[1];
        int count = await stream.ReadAsync(first).ConfigureAwait(false);
        return count == 0 ? null : first[0];
    }

    private static System.Text.Json.JsonDocument Payload(byte[] frame)
    {
        Assert.Equal((uint)(frame.Length - 4), BinaryPrimitives.ReadUInt32BigEndian(frame));
        return System.Text.Json.JsonDocument.Parse(frame.AsMemory(4));
    }

    private sealed class RecordingStream : Stream
    {
        private readonly List<byte[]> _writes = [];
        internal IReadOnlyList<byte[]> Writes { get { lock (_writes) return _writes.ToArray(); } }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (_writes) _writes.Add(buffer.ToArray());
            return ValueTask.CompletedTask;
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            lock (_writes) _writes.Add(buffer.AsSpan(offset, count).ToArray());
            return Task.CompletedTask;
        }
    }

    private sealed class PlainSslStream : SslStream
    {
        private readonly Stream _inner;
        internal PlainSslStream(Stream inner) : base(inner, leaveInnerStreamOpen: false) => _inner = inner;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.WriteAsync(buffer, cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    }

    private sealed class TcpPair : IDisposable
    {
        private readonly TcpListener _listener;
        private TcpPair(TcpListener listener, TcpClient server, TcpClient client)
        {
            _listener = listener;
            ServerSide = server;
            Client = client;
            Server = server.GetStream();
            ClientSsl = new PlainSslStream(client.GetStream());
        }
        internal NetworkStream Server { get; }
        internal PlainSslStream ClientSsl { get; }
        internal TcpClient ServerSide { get; }
        internal TcpClient Client { get; }
        internal static async Task<TcpPair> CreateAsync()
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task<TcpClient> accepting = listener.AcceptTcpClientAsync();
            TcpClient client = new(AddressFamily.InterNetwork) { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, port);
            TcpClient server = await accepting;
            server.NoDelay = true;
            listener.Stop();
            return new(listener, server, client);
        }
        public void Dispose()
        {
            ClientSsl.Dispose();
            Client.Dispose();
            Server.Dispose();
            ServerSide.Dispose();
            _listener.Stop();
        }
    }
}
