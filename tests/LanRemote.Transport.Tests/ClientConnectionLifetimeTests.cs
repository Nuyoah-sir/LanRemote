using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace LanRemote.Transport.Tests;

public sealed class ClientConnectionLifetimeTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    [Fact(Timeout = 60_000)]
    public async Task ConcurrentCloseRequests_ReturnOneTask_AndWaitForTcpThenSslExactlyOnce()
    {
        using DisposeGate tcpGate = new();
        using DisposeGate sslGate = new();
        await using ConnectionFixture fixture = new(tcpGate.Block, new ProbeSslStream(sslGate.Block));
        TlsConnection connection = fixture.Connection;
        TaskCompletionSource start = NewSignal();
        Task<Task>[] requests = Enumerable.Range(0, 16).Select(_ => RequestAsync()).ToArray();

        async Task<Task> RequestAsync()
        {
            await start.Task.ConfigureAwait(false);
            return connection.CloseAsync();
        }

        try
        {
            start.TrySetResult();
            // join 的是请求调用者，而不是它们返回的关闭任务；TCP 尚未放行也必须全部返回。
            Task[] returned = await Task.WhenAll(requests).WaitAsync(Guard);
            Task close = returned[0];
            Assert.All(returned, task => Assert.Same(close, task));
            Assert.Same(close, connection.CloseAsync());
            await tcpGate.Entered.Task.WaitAsync(Guard);
            Assert.False(close.IsCompleted);
            Assert.Equal(1, fixture.Client.DisposeCount);
            Assert.Equal(0, fixture.Ssl.DisposeCount);
            Assert.False(sslGate.Entered.Task.IsCompleted);

            tcpGate.Release();
            await sslGate.Entered.Task.WaitAsync(Guard);
            Assert.Equal(1, fixture.Client.DisposeCount);
            Assert.Equal(1, fixture.Ssl.DisposeCount);
            Assert.False(close.IsCompleted);
            Assert.Same(close, connection.CloseAsync());

            sslGate.Release();
            await close.WaitAsync(Guard);
            Assert.True(close.IsCompletedSuccessfully);
            for (int i = 0; i < 16; i++)
            {
                Assert.Same(close, connection.CloseAsync());
                Assert.Null(Record.Exception(connection.Dispose));
            }
            Assert.Equal(1, fixture.Client.DisposeCount);
            Assert.Equal(1, fixture.Ssl.DisposeCount);
            Assert.Empty(connection.CleanupErrors);
        }
        finally
        {
            start.TrySetResult();
            tcpGate.Release();
            sslGate.Release();
            // 即使身份断言失败，也逐个 join 实际返回的任务，不假定它们确实共享。
            Task[] returned = await Task.WhenAll(requests).WaitAsync(Guard);
            await Task.WhenAll(returned).WaitAsync(Guard);
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FirstAndRepeatedPublicDispose_BothWaitForClose_OnlyFirstReportsErrors(
        bool tcpThrows, bool sslThrows)
    {
        using DisposeGate tcpGate = new();
        using DisposeGate sslGate = new();
        Exception tcpError = new InvalidOperationException("测试 TCP 释放失败");
        Exception sslError = new IOException("测试 SSL 释放失败");
        Exception[] expected = ExpectedErrors(tcpThrows, sslThrows, tcpError, sslError);
        await using ConnectionFixture fixture = new(
            () => { tcpGate.Block(); if (tcpThrows) throw tcpError; },
            new ProbeSslStream(() => { sslGate.Block(); if (sslThrows) throw sslError; }));
        TlsConnection connection = fixture.Connection;
        Task close = connection.CloseAsync();
        DisposeCaller first = new(connection);
        DisposeCaller repeated = new(connection);

        try
        {
            await tcpGate.Entered.Task.WaitAsync(Guard);
            // 关闭任务已建立，CloseAsync 的锁无人持有；专用调用线程唯一的等待点是 Dispose。
            first.Start();
            first.AssertWaiting();
            repeated.Start();
            repeated.AssertWaiting();
            Assert.False(close.IsCompleted);
            Assert.False(first.Result.Task.IsCompleted);
            Assert.False(repeated.Result.Task.IsCompleted);

            tcpGate.Release();
            await sslGate.Entered.Task.WaitAsync(Guard);
            first.AssertWaiting();
            repeated.AssertWaiting();
            Assert.False(close.IsCompleted);

            sslGate.Release();
            await close.WaitAsync(Guard);
            AssertDisposeError(await first.Result.Task.WaitAsync(Guard), expected);
            Assert.Null(await repeated.Result.Task.WaitAsync(Guard));
            Assert.True(close.IsCompletedSuccessfully);
            AssertErrors(expected, connection.CleanupErrors);
            Assert.Equal(1, fixture.Client.DisposeCount);
            Assert.Equal(1, fixture.Ssl.DisposeCount);
            Assert.Null(Record.Exception(connection.Dispose));
        }
        finally
        {
            tcpGate.Release();
            sslGate.Release();
            // 两个调用者均需 join；第一个 join 失败也不能跳过第二个。
            try { await first.JoinAsync(); }
            finally { await repeated.JoinAsync(); }
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CleanupErrors_AreOrderedOriginalReadOnlySnapshots_AndNeverGrowOnRepeatedClose(
        bool tcpThrows, bool sslThrows)
    {
        using DisposeGate tcpGate = new();
        using DisposeGate sslGate = new();
        Exception tcpError = new InvalidOperationException("测试 TCP 原始错误");
        Exception sslError = new IOException("测试 SSL 原始错误");
        Exception[] expected = ExpectedErrors(tcpThrows, sslThrows, tcpError, sslError);
        List<string> order = new();
        await using ConnectionFixture fixture = new(
            () => { order.Add("tcp"); tcpGate.Block(); if (tcpThrows) throw tcpError; },
            new ProbeSslStream(() => { order.Add("ssl"); sslGate.Block(); if (sslThrows) throw sslError; }));
        TlsConnection connection = fixture.Connection;
        IReadOnlyList<Exception> before = connection.CleanupErrors;

        try
        {
            Task close = connection.CloseAsync();
            await tcpGate.Entered.Task.WaitAsync(Guard);
            IReadOnlyList<Exception> duringTcp = connection.CleanupErrors;
            Assert.Empty(duringTcp);
            tcpGate.Release();
            await sslGate.Entered.Task.WaitAsync(Guard);
            IReadOnlyList<Exception> duringSsl = connection.CleanupErrors;
            Exception[] tcpOnly = tcpThrows ? [tcpError] : [];
            AssertErrors(tcpOnly, duringSsl);
            Assert.False(close.IsCompleted);

            sslGate.Release();
            await close.WaitAsync(Guard);
            Assert.True(close.IsCompletedSuccessfully);
            Assert.Equal(new[] { "tcp", "ssl" }, order);
            IReadOnlyList<Exception> completed = connection.CleanupErrors;
            AssertErrors(expected, completed);
            Assert.Empty(before);
            Assert.Empty(duringTcp);
            AssertErrors(tcpOnly, duringSsl);
            Assert.NotSame(completed, connection.CleanupErrors);

            IList<Exception> readOnly = Assert.IsAssignableFrom<IList<Exception>>(completed);
            Assert.True(readOnly.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => readOnly.Add(new Exception("不能追加")));
            Assert.Throws<NotSupportedException>(() => readOnly[0] = new Exception("不能替换"));
            Assert.Throws<NotSupportedException>(() => readOnly.Clear());
            AssertDisposeError(Record.Exception(connection.Dispose), expected);
            for (int i = 0; i < 16; i++)
            {
                Assert.Null(Record.Exception(connection.Dispose));
                Assert.Same(close, connection.CloseAsync());
            }
            AssertErrors(expected, completed);
            AssertErrors(expected, connection.CleanupErrors);
            AssertErrors(tcpOnly, duringSsl);
            Assert.Empty(before);
            Assert.Equal(1, fixture.Client.DisposeCount);
            Assert.Equal(1, fixture.Ssl.DisposeCount);
        }
        finally
        {
            tcpGate.Release();
            sslGate.Release();
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PublicDispose_InitiatesClose_AndRepeatedFinallyPreservesAuthenticationError(
        bool tcpThrows, bool sslThrows)
    {
        Exception tcpError = new InvalidOperationException("测试 TCP 原始错误");
        Exception sslError = new IOException("测试 SSL 原始错误");
        Exception[] expected = ExpectedErrors(tcpThrows, sslThrows, tcpError, sslError);
        await using ConnectionFixture fixture = new(
            () => { if (tcpThrows) throw tcpError; },
            new ProbeSslStream(() => { if (sslThrows) throw sslError; }));
        TlsConnection connection = fixture.Connection;
        Assert.False(connection.IsCloseRequested);
        AssertDisposeError(Record.Exception(connection.Dispose), expected);
        Task close = connection.CloseAsync();
        Assert.True(close.IsCompletedSuccessfully);
        Assert.True(connection.IsCloseRequested);

        AuthenticationException authenticationError = new("测试外层认证失败");
        Exception? observed = Record.Exception((Action)(() =>
        {
            try { throw authenticationError; }
            finally { connection.Dispose(); }
        }));
        Assert.Same(authenticationError, Assert.IsType<AuthenticationException>(observed));
        Assert.Null(Record.Exception(connection.Dispose));
        AssertErrors(expected, connection.CleanupErrors);
        Assert.Same(close, connection.CloseAsync());
        Assert.Equal(1, fixture.Client.DisposeCount);
        Assert.Equal(1, fixture.Ssl.DisposeCount);
    }

    [Theory(Timeout = 60_000)]
    [InlineData("connection")]
    [InlineData("adapter-sync")]
    [InlineData("adapter-async")]
    public async Task CloseRequest_ImmediatelyRejectsStreamAndAllAdapters_AndHidesLiveLocalEndpoint(string requester)
    {
        using DisposeGate tcpGate = new();
        await using ConnectionFixture fixture = new(tcpGate.Block, new RecordingSslStream());
        TlsConnection connection = fixture.Connection;
        Stream adapter = connection.CreateVideoStream();
        Stream sibling = connection.CreateVideoStream();
        Task? requested = null;
        try
        {
            fixture.Client.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            IPEndPoint endpoint = Assert.IsType<IPEndPoint>(connection.LocalEndPoint);
            Assert.True(endpoint.Port > 0);
            Assert.False(connection.IsCloseRequested);
            Assert.Same(fixture.Ssl, connection.Stream);
            Assert.IsType<ClientOwnedVideoStream>(adapter);
            Assert.True(adapter.CanRead);
            Assert.True(adapter.CanWrite);

            switch (requester)
            {
                case "connection": requested = connection.CloseAsync(); break;
                case "adapter-sync": adapter.Dispose(); break;
                case "adapter-async": requested = adapter.DisposeAsync().AsTask(); break;
                default: throw new InvalidOperationException("未知测试入口");
            }

            // 先断言请求发布，不等待 worker；物理 TCP 关闭被闸门阻止，不能靠底层已释放来通过。
            Assert.True(connection.IsCloseRequested);
            Assert.Null(connection.LocalEndPoint);
            Assert.Throws<ObjectDisposedException>(() => { _ = connection.Stream; });
            Assert.Throws<ObjectDisposedException>(() => { _ = connection.NegotiatedProtocol; });
            Assert.Throws<ObjectDisposedException>(() => connection.CreateVideoStream());
            await AssertAdapterRejectedAsync(adapter);
            await AssertAdapterRejectedAsync(sibling);
            Assert.Empty(((RecordingSslStream)fixture.Ssl).Calls);
            await tcpGate.Entered.Task.WaitAsync(Guard);
            Assert.Equal(endpoint, fixture.Client.Client.LocalEndPoint);
            Assert.Equal(0, fixture.Ssl.DisposeCount);
            Assert.False(connection.CloseAsync().IsCompleted);
            if (requested is not null) Assert.Same(requested, connection.CloseAsync());
        }
        finally
        {
            tcpGate.Release();
            if (requested is not null) await requested.WaitAsync(Guard);
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task VideoReaderDispose_OnlyRequestsClose_AdapterDisposeAsyncSharesTask_AndRetainsDiagnostics(
        bool tcpThrows, bool sslThrows)
    {
        using DisposeGate tcpGate = new();
        using DisposeGate sslGate = new();
        Exception tcpError = new InvalidOperationException("测试 TCP 清理失败");
        Exception sslError = new IOException("测试 SSL 清理失败");
        Exception[] expected = ExpectedErrors(tcpThrows, sslThrows, tcpError, sslError);
        await using ConnectionFixture fixture = new(
            () => { tcpGate.Block(); if (tcpThrows) throw tcpError; },
            new ProbeSslStream(() => { sslGate.Block(); if (sslThrows) throw sslError; }));
        TlsConnection connection = fixture.Connection;
        Stream adapter = connection.CreateVideoStream();
        VideoFrameReader reader = new(adapter);
        Task<Exception?>? request = null;
        Task? asyncDispose = null;
        Task? repeatedAsyncDispose = null;

        try
        {
            // 正向证明同步 Dispose 已经返回；若改成阻塞，Guard 仅负责使 finally 能放行闸门。
            request = Task.Run<Exception?>(() => Record.Exception(reader.Dispose));
            Assert.Null(await request.WaitAsync(Guard));
            await tcpGate.Entered.Task.WaitAsync(Guard);
            Task close = connection.CloseAsync();
            Assert.True(reader.IsTerminated);
            Assert.True(reader.StreamDisposeSucceeded);
            Assert.False(close.IsCompleted);
            Assert.Equal(0, fixture.Ssl.DisposeCount);
            Assert.Empty(reader.CleanupErrors);

            adapter.Dispose();
            asyncDispose = adapter.DisposeAsync().AsTask();
            repeatedAsyncDispose = adapter.DisposeAsync().AsTask();
            Assert.Same(close, asyncDispose);
            Assert.Same(close, repeatedAsyncDispose);
            Assert.False(asyncDispose.IsCompleted);
            tcpGate.Release();
            await sslGate.Entered.Task.WaitAsync(Guard);
            Assert.False(asyncDispose.IsCompleted);
            sslGate.Release();
            await asyncDispose.WaitAsync(Guard);
            Assert.True(close.IsCompletedSuccessfully);
            Assert.True(reader.StreamDisposeSucceeded);
            Assert.Empty(reader.CleanupErrors);
            AssertErrors(expected, connection.CleanupErrors);
            Assert.Same(close, adapter.DisposeAsync().AsTask());
            Assert.Null(Record.Exception(adapter.Dispose));
            // 适配器没有消费首个 public Dispose 的错误报告权，也没有把错误改称已成功释放。
            AssertDisposeError(Record.Exception(connection.Dispose), expected);
            Assert.Null(Record.Exception(connection.Dispose));
            AssertErrors(expected, connection.CleanupErrors);
            Assert.Equal(1, fixture.Client.DisposeCount);
            Assert.Equal(1, fixture.Ssl.DisposeCount);
        }
        finally
        {
            tcpGate.Release();
            sslGate.Release();
            List<Task> pending = new();
            if (request is not null) pending.Add(request);
            if (asyncDispose is not null) pending.Add(asyncDispose);
            if (repeatedAsyncDispose is not null) pending.Add(repeatedAsyncDispose);
            await Task.WhenAll(pending).WaitAsync(Guard);
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CloseCompletion_DoesNotJoinPendingRead_OriginalTaskAndOutcomeRemainObservable(
        bool memoryOverload, bool readThrows)
    {
        HangingReadSslStream ssl = new();
        await using ConnectionFixture fixture = new(ssl: ssl);
        Stream adapter = fixture.Connection.CreateVideoStream();
        using CancellationTokenSource caller = new();
        byte[] buffer = new byte[4];
        IOException readError = new("测试关闭后才返回的原始读取错误");
        Task<int>? read = null;
        try
        {
            read = memoryOverload
                ? adapter.ReadAsync(buffer.AsMemory(1, 2), caller.Token).AsTask()
                : adapter.ReadAsync(buffer, 1, 2, caller.Token);
            await ssl.ReadEntered.Task.WaitAsync(Guard);
            Assert.Same(ssl.PendingRead.Task, read);
            Assert.Equal(buffer.AsMemory(1, 2), ssl.Buffer);
            Assert.Equal(caller.Token, ssl.Token);

            adapter.Dispose();
            Task close = fixture.Connection.CloseAsync();
            Assert.Same(close, adapter.DisposeAsync().AsTask());
            await close.WaitAsync(Guard);
            Assert.True(close.IsCompletedSuccessfully);
            Assert.Equal(1, fixture.Client.DisposeCount);
            Assert.Equal(1, ssl.DisposeCount);
            Assert.Empty(fixture.Connection.CleanupErrors);
            // 这里未完成由尚未放行的 TCS 保证，不是对线程调度快慢的猜测。
            Assert.False(ssl.PendingRead.Task.IsCompleted);
            Assert.False(read.IsCompleted);
            Assert.False(caller.IsCancellationRequested);
            await AssertAdapterRejectedAsync(adapter);

            if (readThrows) ssl.PendingRead.TrySetException(readError);
            else ssl.PendingRead.TrySetResult(1);
            if (readThrows)
            {
                Exception? observed = await Record.ExceptionAsync(async () => { await read.WaitAsync(Guard); });
                Assert.Same(readError, Assert.IsType<IOException>(observed));
            }
            else
            {
                Assert.Equal(1, await read.WaitAsync(Guard));
            }
            Assert.True(read.IsCompleted);
            Assert.Empty(fixture.Connection.CleanupErrors);
        }
        finally
        {
            // 不以关闭任务替代原读任务；即使中途断言失败，也显式放行并观察原任务的故障。
            ssl.PendingRead.TrySetResult(1);
            Task[] reads = read is null ? [ssl.PendingRead.Task] : [ssl.PendingRead.Task, read];
            await Record.ExceptionAsync(() => Task.WhenAll(reads)).WaitAsync(Guard);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task VideoReaderTerminationAndConnectionClose_DoNotReplaceOriginalReadFrameJoin()
    {
        HangingReadSslStream ssl = new();
        await using ConnectionFixture fixture = new(ssl: ssl);
        VideoFrameReader reader = new(fixture.Connection.CreateVideoStream());
        Task? readFrame = null;
        try
        {
            readFrame = reader.ReadFrameAsync();
            await ssl.ReadEntered.Task.WaitAsync(Guard);
            reader.Dispose();
            Assert.True(reader.IsTerminated);
            Assert.True(reader.StreamDisposeSucceeded);
            Task close = fixture.Connection.CloseAsync();
            await close.WaitAsync(Guard);
            Assert.True(close.IsCompletedSuccessfully);
            Assert.False(ssl.PendingRead.Task.IsCompleted);
            Assert.False(readFrame.IsCompleted);
            Assert.NotSame(close, readFrame);
            Assert.Empty(reader.CleanupErrors);
            Assert.Empty(fixture.Connection.CleanupErrors);

            // 读取器已终止；底层明确放行后，原 ReadFrameAsync 才能观察终止并退出。
            ssl.PendingRead.TrySetResult(0);
            ObjectDisposedException error = await Assert.ThrowsAsync<ObjectDisposedException>(() => readFrame.WaitAsync(Guard));
            Assert.Equal(typeof(VideoFrameReader).FullName, error.ObjectName);
            Assert.True(readFrame.IsFaulted);
            Assert.Equal(1, fixture.Client.DisposeCount);
            Assert.Equal(1, ssl.DisposeCount);
        }
        finally
        {
            ssl.PendingRead.TrySetResult(0);
            try { reader.Dispose(); }
            finally
            {
                Task[] reads = readFrame is null ? [ssl.PendingRead.Task] : [ssl.PendingRead.Task, readFrame];
                await Record.ExceptionAsync(() => Task.WhenAll(reads)).WaitAsync(Guard);
            }
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Adapter_ForwardsSynchronousArraySpanAndFlushOverloads()
    {
        RecordingSslStream ssl = new();
        await using ConnectionFixture fixture = new(ssl: ssl);
        Stream adapter = fixture.Connection.CreateVideoStream();
        byte[] buffer = new byte[6];
        Assert.Equal(1, adapter.Read(buffer, 2, 3));
        Assert.Same(buffer, ssl.ArrayBuffer);
        Assert.Equal(2, ssl.Offset);
        Assert.Equal(3, ssl.Count);
        Assert.Equal((byte)0x31, buffer[2]);
        Assert.Equal((byte)0, buffer[1]);
        Assert.Equal((byte)0, buffer[3]);
        Assert.Equal(2, adapter.Read(buffer.AsSpan(1, 2)));
        Assert.Equal(2, ssl.Count);
        Assert.Equal(new byte[] { 0x32, 0x32 }, buffer.AsSpan(1, 2).ToArray());

        byte[] payload = [9, 8, 7, 6, 5];
        adapter.Write(payload, 1, 3);
        Assert.Same(payload, ssl.ArrayBuffer);
        Assert.Equal(1, ssl.Offset);
        Assert.Equal(3, ssl.Count);
        Assert.Equal(new byte[] { 8, 7, 6 }, ssl.Written);
        adapter.Write(payload.AsSpan(2, 2));
        Assert.Equal(new byte[] { 7, 6 }, ssl.Written);
        adapter.Flush();
        Assert.Equal(new[] { "read-array", "read-span", "write-array", "write-span", "flush" }, ssl.Calls);
        Assert.False(fixture.Connection.IsCloseRequested);
    }

    [Fact(Timeout = 60_000)]
    public async Task Adapter_ForwardsAsyncArrayMemoryAndFlushOverloads_WithOriginalTasksAndTokens()
    {
        RecordingSslStream ssl = new();
        await using ConnectionFixture fixture = new(ssl: ssl);
        Stream adapter = fixture.Connection.CreateVideoStream();
        using CancellationTokenSource caller = new();
        byte[] buffer = new byte[6];

        Task<int> arrayRead = adapter.ReadAsync(buffer, 1, 3, caller.Token);
        Assert.Same(ssl.ArrayReadTask, arrayRead);
        Assert.Equal(2, await arrayRead.WaitAsync(Guard));
        Assert.Same(buffer, ssl.ArrayBuffer);
        Assert.Equal(1, ssl.Offset);
        Assert.Equal(3, ssl.Count);
        Assert.Equal(caller.Token, ssl.Token);
        Task<int> memoryRead = adapter.ReadAsync(buffer.AsMemory(2, 3), caller.Token).AsTask();
        Assert.Same(ssl.MemoryReadTask, memoryRead);
        Assert.Equal(3, await memoryRead.WaitAsync(Guard));
        Assert.Equal(buffer.AsMemory(2, 3), ssl.ReadMemory);
        Assert.Equal(caller.Token, ssl.Token);

        Task arrayWrite = adapter.WriteAsync(buffer, 2, 4, caller.Token);
        Assert.Same(ssl.ArrayWriteTask, arrayWrite);
        await arrayWrite.WaitAsync(Guard);
        Assert.Same(buffer, ssl.ArrayBuffer);
        Assert.Equal(2, ssl.Offset);
        Assert.Equal(4, ssl.Count);
        Assert.Equal(caller.Token, ssl.Token);
        Task memoryWrite = adapter.WriteAsync(buffer.AsMemory(1, 4), caller.Token).AsTask();
        Assert.Same(ssl.MemoryWriteTask, memoryWrite);
        await memoryWrite.WaitAsync(Guard);
        Assert.Equal((ReadOnlyMemory<byte>)buffer.AsMemory(1, 4), ssl.WriteMemory);
        Assert.Equal(caller.Token, ssl.Token);
        Task flush = adapter.FlushAsync(caller.Token);
        Assert.Same(ssl.FlushTask, flush);
        await flush.WaitAsync(Guard);
        Assert.Equal(caller.Token, ssl.Token);
        Assert.Equal(new[] { "read-array-async", "read-memory", "write-array-async", "write-memory", "flush-async" }, ssl.Calls);
        Assert.False(fixture.Connection.IsCloseRequested);
    }

    [Fact(Timeout = 60_000)]
    public async Task Adapter_ForwardsCapabilitiesAndTimeouts_ButDoesNotSupportSeeking()
    {
        RecordingSslStream ssl = new();
        await using ConnectionFixture fixture = new(ssl: ssl);
        Stream adapter = fixture.Connection.CreateVideoStream();
        Assert.True(adapter.CanRead);
        Assert.True(adapter.CanWrite);
        Assert.True(adapter.CanTimeout);
        ssl.Readable = false;
        ssl.Writable = false;
        ssl.TimeoutCapable = false;
        Assert.False(adapter.CanRead);
        Assert.False(adapter.CanWrite);
        Assert.False(adapter.CanTimeout);
        adapter.ReadTimeout = 123;
        adapter.WriteTimeout = 456;
        Assert.Equal(123, ssl.ReadTimeout);
        Assert.Equal(456, ssl.WriteTimeout);
        ssl.ReadTimeout = 789;
        ssl.WriteTimeout = 987;
        Assert.Equal(789, adapter.ReadTimeout);
        Assert.Equal(987, adapter.WriteTimeout);
        Assert.False(adapter.CanSeek);
        Assert.Throws<NotSupportedException>(() => { _ = adapter.Length; });
        Assert.Throws<NotSupportedException>(() => { _ = adapter.Position; });
        Assert.Throws<NotSupportedException>(() => adapter.Position = 1);
        Assert.Throws<NotSupportedException>(() => adapter.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => adapter.SetLength(1));
        Assert.Empty(ssl.Calls);
        Assert.False(fixture.Connection.IsCloseRequested);
    }

    private static async Task AssertAdapterRejectedAsync(Stream adapter)
    {
        byte[] buffer = new byte[2];
        Assert.False(adapter.CanRead);
        Assert.False(adapter.CanWrite);
        Assert.False(adapter.CanSeek);
        Assert.Throws<ObjectDisposedException>(() => { _ = adapter.CanTimeout; });
        Assert.Throws<ObjectDisposedException>(() => { _ = adapter.ReadTimeout; });
        Assert.Throws<ObjectDisposedException>(() => adapter.ReadTimeout = 100);
        Assert.Throws<ObjectDisposedException>(() => { _ = adapter.WriteTimeout; });
        Assert.Throws<ObjectDisposedException>(() => adapter.WriteTimeout = 100);
        Assert.Throws<ObjectDisposedException>(() => adapter.Read(buffer, 0, 1));
        Assert.Throws<ObjectDisposedException>(() => adapter.Read(buffer.AsSpan(0, 1)));
        Assert.Throws<ObjectDisposedException>(() => adapter.Write(buffer, 0, 1));
        Assert.Throws<ObjectDisposedException>(() => adapter.Write(buffer.AsSpan(0, 1)));
        Assert.Throws<ObjectDisposedException>(adapter.Flush);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            adapter.ReadAsync(buffer, 0, 1, CancellationToken.None).WaitAsync(Guard));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            adapter.ReadAsync(buffer.AsMemory(0, 1)).AsTask().WaitAsync(Guard));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            adapter.WriteAsync(buffer, 0, 1, CancellationToken.None).WaitAsync(Guard));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            adapter.WriteAsync(buffer.AsMemory(0, 1)).AsTask().WaitAsync(Guard));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            adapter.FlushAsync(CancellationToken.None).WaitAsync(Guard));
    }

    private static Exception[] ExpectedErrors(bool tcpThrows, bool sslThrows, Exception tcpError, Exception sslError) =>
        (tcpThrows, sslThrows) switch
        {
            (true, true) => [tcpError, sslError],
            (true, false) => [tcpError],
            (false, true) => [sslError],
            _ => [],
        };

    private static void AssertErrors(IReadOnlyList<Exception> expected, IReadOnlyList<Exception> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.IsType(expected[i].GetType(), actual[i]);
            Assert.Same(expected[i], actual[i]);
        }
    }

    private static void AssertDisposeError(Exception? observed, IReadOnlyList<Exception> expected)
    {
        if (expected.Count == 0) Assert.Null(observed);
        else if (expected.Count == 1)
        {
            Assert.IsType(expected[0].GetType(), observed);
            Assert.Same(expected[0], observed);
        }
        else
        {
            // 不 Flatten，不接受包装层或乱序：必须是恰好两个原始异常。
            AssertErrors(expected, Assert.IsType<AggregateException>(observed).InnerExceptions);
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static ConnectionIdentity CreateIdentity()
    {
        byte[] pin = Convert.FromHexString("808182838485868788898A8B8C8D8E8F909192939495969798999A9B9C9D9E9F");
        Assert.True(ConnectionTarget.TryCreate(
            new Guid("11111111-2222-3333-4444-555555555555"), IPAddress.Loopback, 12345,
            Convert.ToHexString(pin), out ConnectionTarget? target));
        Assert.True(ConnectionIdentity.TryCreate(target!, pin, out ConnectionIdentity? identity));
        return identity!;
    }

    private sealed class ConnectionFixture : IAsyncDisposable
    {
        internal ProbeTcpClient Client { get; }
        internal ProbeSslStream Ssl { get; }
        internal TlsConnection Connection { get; }

        internal ConnectionFixture(Action? onClientDispose = null, ProbeSslStream? ssl = null)
        {
            Client = new ProbeTcpClient(onClientDispose);
            Ssl = ssl ?? new ProbeSslStream();
            Connection = new TlsConnection(CreateIdentity(), Client, Ssl);
        }

        public async ValueTask DisposeAsync()
        {
            try { await Connection.CloseAsync().WaitAsync(Guard); }
            finally
            {
                // 绕过注入和计数兜底清理；所有次数断言都发生在此之前。
                try { Ssl.DisposeForCleanup(); }
                finally { Client.DisposeForCleanup(); }
            }
        }
    }

    private sealed class DisposeGate : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        internal TaskCompletionSource Entered { get; } = NewSignal();
        internal void Block()
        {
            Entered.TrySetResult();
            if (!_release.Wait(Guard + Guard)) throw new TimeoutException("测试释放闸门未放行。");
        }
        internal void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
    }

    private sealed class DisposeCaller
    {
        private readonly Thread _thread;
        private bool _started;
        internal TaskCompletionSource<Exception?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _entered = NewSignal();

        internal DisposeCaller(TlsConnection connection)
        {
            _thread = new Thread(() =>
            {
                _entered.TrySetResult();
                Result.TrySetResult(Record.Exception(connection.Dispose));
            }) { IsBackground = true };
        }

        internal void Start()
        {
            _thread.Start();
            _started = true;
        }

        internal void AssertWaiting()
        {
            // 先确认进入调用边界，再保存一次等待态观测。Task 等待内部可能醒来重新等待，
            // 不能在 SpinUntil 返回后重读 ThreadState，并把瞬时 Running 误判为已经返回。
            Assert.True(_entered.Task.Wait(Guard), "Dispose 调用线程未进入调用边界。");
            bool observedWaiting = false;
            Assert.True(SpinWait.SpinUntil(() =>
            {
                observedWaiting = (_thread.ThreadState & ThreadState.WaitSleepJoin) != 0;
                return observedWaiting || Result.Task.IsCompleted;
            }, Guard), "Dispose 调用线程未到达等待态。");
            Assert.True(observedWaiting, "Dispose 在关闭闸门放行之前返回了。");
            Assert.False(Result.Task.IsCompleted);
        }

        internal async Task JoinAsync()
        {
            if (!_started) return;
            try { await Result.Task.WaitAsync(Guard); }
            finally { Assert.True(_thread.Join(Guard), "Dispose 调用线程未结束。"); }
        }
    }

    private sealed class ProbeTcpClient(Action? onDispose = null) : TcpClient(AddressFamily.InterNetwork)
    {
        private int _disposeCount;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    Interlocked.Increment(ref _disposeCount);
                    onDispose?.Invoke();
                }
            }
            finally { base.Dispose(disposing); }
        }
        internal void DisposeForCleanup() => base.Dispose(true);
    }

    private class ProbeSslStream(Action? onDispose = null) : SslStream(new MemoryStream(), leaveInnerStreamOpen: false)
    {
        private int _disposeCount;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    Interlocked.Increment(ref _disposeCount);
                    onDispose?.Invoke();
                }
            }
            finally { base.Dispose(disposing); }
        }
        internal void DisposeForCleanup() => base.Dispose(true);
    }

    private sealed class HangingReadSslStream : ProbeSslStream
    {
        internal TaskCompletionSource ReadEntered { get; } = NewSignal();
        internal TaskCompletionSource<int> PendingRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Memory<byte> Buffer { get; private set; }
        internal CancellationToken Token { get; private set; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Buffer = buffer.AsMemory(offset, count);
            Token = cancellationToken;
            ReadEntered.TrySetResult();
            return PendingRead.Task;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Buffer = buffer;
            Token = cancellationToken;
            ReadEntered.TrySetResult();
            return new ValueTask<int>(PendingRead.Task);
        }
    }

    private sealed class RecordingSslStream : ProbeSslStream
    {
        internal List<string> Calls { get; } = new();
        internal byte[]? ArrayBuffer { get; private set; }
        internal int Offset { get; private set; }
        internal int Count { get; private set; }
        internal byte[]? Written { get; private set; }
        internal Memory<byte> ReadMemory { get; private set; }
        internal ReadOnlyMemory<byte> WriteMemory { get; private set; }
        internal CancellationToken Token { get; private set; }
        internal Task<int> ArrayReadTask { get; } = Task.FromResult(2);
        internal Task<int> MemoryReadTask { get; } = Task.FromResult(3);
        internal Task ArrayWriteTask { get; } = CompletedTask();
        internal Task MemoryWriteTask { get; } = CompletedTask();
        internal Task FlushTask { get; } = CompletedTask();
        internal bool Readable { get; set; } = true;
        internal bool Writable { get; set; } = true;
        internal bool TimeoutCapable { get; set; } = true;
        public override bool CanRead => Readable;
        public override bool CanWrite => Writable;
        public override bool CanTimeout => TimeoutCapable;
        public override int ReadTimeout { get; set; }
        public override int WriteTimeout { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Calls.Add("read-array");
            (ArrayBuffer, Offset, Count) = (buffer, offset, count);
            buffer[offset] = 0x31;
            return 1;
        }
        public override int Read(Span<byte> buffer)
        {
            Calls.Add("read-span");
            Count = buffer.Length;
            buffer.Fill(0x32);
            return buffer.Length;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Calls.Add("read-array-async");
            (ArrayBuffer, Offset, Count, Token) = (buffer, offset, count, cancellationToken);
            return ArrayReadTask;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Calls.Add("read-memory");
            (ReadMemory, Token) = (buffer, cancellationToken);
            return new ValueTask<int>(MemoryReadTask);
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            Calls.Add("write-array");
            (ArrayBuffer, Offset, Count) = (buffer, offset, count);
            Written = buffer.AsSpan(offset, count).ToArray();
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Calls.Add("write-span");
            Written = buffer.ToArray();
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Calls.Add("write-array-async");
            (ArrayBuffer, Offset, Count, Token) = (buffer, offset, count, cancellationToken);
            return ArrayWriteTask;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Calls.Add("write-memory");
            (WriteMemory, Token) = (buffer, cancellationToken);
            return new ValueTask(MemoryWriteTask);
        }
        public override void Flush() => Calls.Add("flush");
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Calls.Add("flush-async");
            Token = cancellationToken;
            return FlushTask;
        }
        private static Task CompletedTask()
        {
            TaskCompletionSource signal = NewSignal();
            signal.SetResult();
            return signal.Task;
        }
    }
}
