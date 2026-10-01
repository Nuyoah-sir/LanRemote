using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using LanRemote.Core.Abstractions;

namespace LanRemote.Transport.Tests;

public sealed class HostConnectionLifetimeTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    [Fact]
    public void TwoArgumentConnection_HasNoCloseAuthority()
    {
        using SslStream ssl = new(new MemoryStream(), leaveInnerStreamOpen: false);
        AcceptedConnection connection = new(CreateSecurity(), ssl);

        Assert.False(connection.HasCloseAuthority);
        Assert.Throws<InvalidOperationException>(() => { _ = connection.CloseAsync(); });
        Assert.Throws<InvalidOperationException>(() => connection.CreateVideoStream());
    }

    [Theory(Timeout = 60_000)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RecordWith_ReplacedReferences_RejectsWithoutClosingEitherStream(
        bool replaceSecurity, bool replaceStream)
    {
        ProbeDisposable socket = new();
        ProbeSslStream ssl = new();
        using ProbeSslStream other = new();
        ConnectionCloseHandle closer = new(socket, new HostLifecycleErrors());
        closer.Attach(ssl);
        AcceptedConnection original = new(CreateSecurity(), ssl, closer);
        AcceptedConnection forged = original with
        {
            Security = replaceSecurity ? CreateSecurity() : original.Security,
            Stream = replaceStream ? other : original.Stream,
        };

        try
        {
            Assert.False(forged.HasCloseAuthority);
            Assert.Throws<InvalidOperationException>(() => { _ = forged.CloseAsync(); });
            Assert.Throws<InvalidOperationException>(() => forged.CreateVideoStream());
            Assert.Equal(0, socket.DisposeCount);
            Assert.Equal(0, ssl.DisposeCount);
            Assert.Equal(0, other.DisposeCount);

            AcceptedConnection unchanged = original with { };
            Assert.True(unchanged.HasCloseAuthority);
            Task close = original.CloseAsync();
            Assert.Same(close, unchanged.CloseAsync());
            await close.WaitAsync(Guard);
            Assert.Equal(1, socket.DisposeCount);
            Assert.Equal(1, ssl.DisposeCount);
            Assert.Equal(0, other.DisposeCount);
        }
        finally
        {
            await closer.CloseAsync().WaitAsync(Guard);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task CloseRequests_ShareOneTask_AndSynchronousDisposeDoesNotWait()
    {
        using DisposeGate gate = new();
        ProbeDisposable socket = new(gate.Block);
        ProbeSslStream ssl = new();
        ConnectionCloseHandle closer = new(socket, new HostLifecycleErrors());
        closer.Attach(ssl);

        try
        {
            // 直接在调用线程请求；生产 Dispose 若改成等待关闭，就不能在闸门放行前返回。
            ((IDisposable)closer).Dispose();
            await gate.Entered.Task.WaitAsync(Guard);
            Task first = closer.CloseAsync();
            Assert.False(first.IsCompleted);
            Assert.Same(first, closer.CloseAsync());
            ((IDisposable)closer).Dispose();
            Assert.Equal(1, socket.DisposeCount);
            Assert.Equal(0, ssl.DisposeCount);

            gate.Release();
            await first.WaitAsync(Guard);
            Assert.True(first.IsCompletedSuccessfully);
            Assert.Same(first, closer.CloseAsync());
            Assert.Equal(1, socket.DisposeCount);
            Assert.Equal(1, ssl.DisposeCount);
            Assert.Empty(closer.CleanupErrors);
        }
        finally
        {
            gate.Release();
            await closer.CloseAsync().WaitAsync(Guard);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task CloseBeforeAttach_WaitsForLateSslDisposal()
    {
        TaskCompletionSource socketDisposed = NewSignal();
        ProbeDisposable socket = new(() => socketDisposed.TrySetResult());
        using DisposeGate sslGate = new();
        ProbeSslStream ssl = new(sslGate.Block);
        ConnectionCloseHandle closer = new(socket, new HostLifecycleErrors());
        Task close = closer.CloseAsync();

        try
        {
            await socketDisposed.Task.WaitAsync(Guard);
            Assert.False(close.IsCompleted);
            closer.Attach(ssl);
            await sslGate.Entered.Task.WaitAsync(Guard);
            Assert.Equal(1, socket.DisposeCount);
            Assert.Equal(1, ssl.DisposeCount);
            Assert.False(close.IsCompleted);
            Assert.Same(close, closer.CloseAsync());

            sslGate.Release();
            await close.WaitAsync(Guard);
            Assert.True(close.IsCompletedSuccessfully);
            Assert.Empty(closer.CleanupErrors);
        }
        finally
        {
            closer.CompleteAttachment();
            sslGate.Release();
            await close.WaitAsync(Guard);
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AttachBeforeOrDuringSocketDisposal_IsReclaimedExactlyOnce(bool attachBeforeClose)
    {
        using DisposeGate socketGate = new();
        ProbeDisposable socket = new(socketGate.Block);
        ProbeSslStream ssl = new();
        ConnectionCloseHandle closer = new(socket, new HostLifecycleErrors());
        if (attachBeforeClose)
        {
            closer.Attach(ssl);
        }

        Task close = closer.CloseAsync();
        try
        {
            await socketGate.Entered.Task.WaitAsync(Guard);
            if (!attachBeforeClose)
            {
                closer.Attach(ssl);
            }

            Assert.Equal(0, ssl.DisposeCount);
            Assert.False(close.IsCompleted);
            socketGate.Release();
            await close.WaitAsync(Guard);
            Assert.Equal(1, socket.DisposeCount);
            Assert.Equal(1, ssl.DisposeCount);
            Assert.Empty(closer.CleanupErrors);
        }
        finally
        {
            closer.CompleteAttachment();
            socketGate.Release();
            await close.WaitAsync(Guard);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task CloseWithoutSsl_WaitsForHostToCompleteAttachment()
    {
        TaskCompletionSource socketDisposed = NewSignal();
        ProbeDisposable socket = new(() => socketDisposed.TrySetResult());
        ConnectionCloseHandle closer = new(socket, new HostLifecycleErrors());
        Task close = closer.CloseAsync();
        try
        {
            await socketDisposed.Task.WaitAsync(Guard);
            Assert.False(close.IsCompleted);
            closer.CompleteAttachment();
            closer.CompleteAttachment();
            await close.WaitAsync(Guard);
            Assert.Same(close, closer.CloseAsync());
            Assert.Equal(1, socket.DisposeCount);
            Assert.Empty(closer.CleanupErrors);
        }
        finally
        {
            closer.CompleteAttachment();
            await close.WaitAsync(Guard);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task CleanupFailures_AttemptSocketThenSsl_AndRetainBothOriginalErrors()
    {
        Exception socketError = new IOException("测试 socket 释放失败");
        Exception sslError = new InvalidOperationException("测试 SSL 释放失败");
        List<string> order = new();
        ProbeDisposable socket = new(() => { order.Add("socket"); throw socketError; });
        ProbeSslStream ssl = new(() => { order.Add("ssl"); throw sslError; });
        HostLifecycleErrors errors = new();
        ConnectionCloseHandle closer = new(socket, errors);
        closer.Attach(ssl);

        Task close = closer.CloseAsync();
        await close.WaitAsync(Guard);
        Assert.True(close.IsCompletedSuccessfully);
        Assert.Equal(new[] { "socket", "ssl" }, order);
        Assert.Collection(closer.CleanupErrors,
            error => Assert.Same(socketError, error),
            error => Assert.Same(sslError, error));
        AssertError(errors, HostLifecycleErrorKind.SocketCleanup, socketError);
        AssertError(errors, HostLifecycleErrorKind.StreamCleanup, sslError);
        ((IDisposable)closer).Dispose();
        Assert.Same(close, closer.CloseAsync());
        Assert.Equal(1, socket.DisposeCount);
        Assert.Equal(1, ssl.DisposeCount);
        Assert.Equal(2, closer.CleanupErrors.Count);
    }

    [Fact(Timeout = 60_000)]
    public async Task VideoWriterDispose_OnlyRequestsClose_AndDisposeAsyncJoinsSameTask()
    {
        using DisposeGate gate = new();
        ProbeDisposable socket = new(gate.Block);
        ProbeSslStream ssl = new();
        ConnectionCloseHandle closer = new(socket, new HostLifecycleErrors());
        closer.Attach(ssl);
        AcceptedConnection connection = new(CreateSecurity(), ssl, closer);
        Stream adapter = connection.CreateVideoStream();
        VideoFrameWriter writer = new(adapter);

        try
        {
            writer.Dispose();
            await gate.Entered.Task.WaitAsync(Guard);
            Task close = connection.CloseAsync();
            Assert.True(writer.StreamDisposeSucceeded);
            Assert.False(close.IsCompleted);
            Assert.Equal(0, ssl.DisposeCount);
            Assert.False(adapter.CanRead);
            Assert.False(adapter.CanWrite);
            Assert.Throws<ObjectDisposedException>(() => adapter.Read(new byte[1], 0, 1));
            Assert.Throws<ObjectDisposedException>(() => adapter.Write(new byte[1], 0, 1));

            Task asyncDispose = adapter.DisposeAsync().AsTask();
            Assert.Same(close, asyncDispose);
            Assert.False(asyncDispose.IsCompleted);
            gate.Release();
            await asyncDispose.WaitAsync(Guard);
            Assert.Equal(1, socket.DisposeCount);
            Assert.Equal(1, ssl.DisposeCount);
            Assert.Empty(closer.CleanupErrors);
        }
        finally
        {
            gate.Release();
            await connection.CloseAsync().WaitAsync(Guard);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task HostFinally_WaitsForPhysicalCloseBeforeReturningAdmissionAndRegistration()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        await using TransportHost host = CreateHost(certificate, (_, _) => Task.CompletedTask);
        using DisposeGate sslGate = new();
        ProbeDisposable socket = new();
        ProbeSslStream ssl = new(sslGate.Block);
        ConnectionCloseHandle closer = new(socket, host.LifecycleErrors);
        closer.Attach(ssl);
        Assert.True(host.Limiter.TryAcquire(IPAddress.Loopback, out AdmissionLease? admission));
        ConnectionRegistry registry = new();
        ConnectionRegistration registration = Assert.IsType<ConnectionRegistration>(registry.TryRegister(closer));
        Task finishing = host.FinishConnectionAsync(closer, admission, registration);

        try
        {
            await sslGate.Entered.Task.WaitAsync(Guard);
            Assert.False(finishing.IsCompleted);
            Assert.False(closer.CloseAsync().IsCompleted);
            Assert.Equal(1, host.AdmittedConnections);
            Assert.Equal(1, registry.Count);
            Assert.False(registration.Cancellation.IsCancellationRequested);

            sslGate.Release();
            await finishing.WaitAsync(Guard);
            Assert.Equal(0, host.AdmittedConnections);
            Assert.Equal(0, registry.Count);
            Assert.Empty(host.LifecycleErrors.Snapshot);
        }
        finally
        {
            sslGate.Release();
            await finishing.WaitAsync(Guard);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task HostFinally_AttemptsEveryCleanupEvenWhenEarlierItemsThrow()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        await using TransportHost host = CreateHost(certificate, (_, _) => Task.CompletedTask);
        Exception socketError = new IOException("测试 socket 错误");
        Exception sslError = new IOException("测试 SSL 错误");
        Exception admissionError = new InvalidOperationException("测试 admission 错误");
        Exception registrationError = new InvalidOperationException("测试 registration 错误");
        List<string> order = new();
        ProbeDisposable socket = new(() => { order.Add("socket"); throw socketError; });
        ProbeSslStream ssl = new(() => { order.Add("ssl"); throw sslError; });
        ProbeDisposable admission = new(() => { order.Add("admission"); throw admissionError; });
        ProbeDisposable registration = new(() => { order.Add("registration"); throw registrationError; });
        ConnectionCloseHandle closer = new(socket, host.LifecycleErrors);
        closer.Attach(ssl);

        await host.FinishConnectionAsync(closer, admission, registration).WaitAsync(Guard);

        Assert.Equal(new[] { "socket", "ssl", "admission", "registration" }, order);
        Assert.Equal(1, socket.DisposeCount);
        Assert.Equal(1, ssl.DisposeCount);
        Assert.Equal(1, admission.DisposeCount);
        Assert.Equal(1, registration.DisposeCount);
        Assert.Equal(4, host.LifecycleErrors.Snapshot.Count);
        AssertError(host.LifecycleErrors, HostLifecycleErrorKind.SocketCleanup, socketError);
        AssertError(host.LifecycleErrors, HostLifecycleErrorKind.StreamCleanup, sslError);
        AssertError(host.LifecycleErrors, HostLifecycleErrorKind.AdmissionCleanup, admissionError);
        AssertError(host.LifecycleErrors, HostLifecycleErrorKind.RegistrationCleanup, registrationError);
    }

    [Fact]
    public void HostDiagnostics_KeepOnlyFirstErrorPerFixedCategory()
    {
        HostLifecycleErrors errors = new();
        Exception first = new InvalidOperationException("首个错误");
        foreach (HostLifecycleErrorKind kind in Enum.GetValues<HostLifecycleErrorKind>())
        {
            errors.Record(kind, first);
        }

        IReadOnlyList<HostLifecycleError> snapshot = errors.Snapshot;
        for (int i = 0; i < 100; i++)
        {
            foreach (HostLifecycleErrorKind kind in Enum.GetValues<HostLifecycleErrorKind>())
            {
                errors.Record(kind, new IOException("后续错误"));
            }
        }

        Assert.Equal(7, errors.Snapshot.Count);
        Assert.All(errors.Snapshot, entry => Assert.Same(first, entry.Error));
        Assert.Equal(snapshot, errors.Snapshot);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<HostLifecycleError>)snapshot).Add(new(HostLifecycleErrorKind.Handler, first)));
    }

    [Theory(Timeout = 60_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HostHandlerFailure_ReturnsAdmissionAndRegistration_AndRecordsOriginalError(bool synchronous)
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        TaskCompletionSource<AcceptedConnection> accepted = NewAcceptedSignal();
        TaskCompletionSource release = NewSignal();
        using DisposeGate synchronousGate = new();
        Exception expected = new InvalidOperationException("测试 handler 原始错误");
        int port = GetFreePort();
        await using TransportHost host = CreateHost(certificate, (connection, _) =>
        {
            accepted.TrySetResult(connection);
            if (synchronous)
            {
                synchronousGate.Block();
                throw expected;
            }

            return FailAfterReleaseAsync();
        }, port);

        async Task FailAfterReleaseAsync()
        {
            await release.Task;
            throw expected;
        }

        try
        {
            Assert.True(host.Start().IsListening);
            using TlsConnection client = await ConnectAsync(port, certificate);
            AcceptedConnection server = await accepted.Task.WaitAsync(Guard);
            Assert.True(server.HasCloseAuthority);
            Assert.Equal(1, host.ActiveConnections);
            Assert.Equal(1, host.AdmittedConnections);
            release.TrySetResult();
            synchronousGate.Release();

            TransportHostStopReport report = await host.StopAsync(Guard).WaitAsync(Guard + Guard);
            Assert.True(report.AllFinished);
            Assert.Equal(0, host.ActiveConnections);
            Assert.Equal(0, host.AdmittedConnections);
            AssertError(host.LifecycleErrors, HostLifecycleErrorKind.Handler, expected);
            Assert.Single(host.LifecycleErrors.Snapshot);
            Task close = server.CloseAsync();
            Assert.True(close.IsCompletedSuccessfully);
            Assert.Same(close, server.CloseAsync());
        }
        finally
        {
            release.TrySetResult();
            synchronousGate.Release();
            await host.StopAsync(Guard).WaitAsync(Guard + Guard);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task HostVideoAdapter_ForwardsRealTlsReadsAndWrites()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        TaskCompletionSource<AcceptedConnection> accepted = NewAcceptedSignal();
        TaskCompletionSource release = NewSignal();
        int port = GetFreePort();
        await using TransportHost host = CreateHost(certificate, async (connection, _) =>
        {
            accepted.TrySetResult(connection);
            await release.Task;
        }, port);

        try
        {
            Assert.True(host.Start().IsListening);
            using TlsConnection client = await ConnectAsync(port, certificate);
            AcceptedConnection server = await accepted.Task.WaitAsync(Guard);
            Stream video = server.CreateVideoStream();
            Assert.IsType<HostOwnedVideoStream>(video);
            Assert.True(video.CanRead);
            Assert.True(video.CanWrite);
            Assert.False(video.CanSeek);
            video.ReadTimeout = (int)Guard.TotalMilliseconds;
            video.WriteTimeout = (int)Guard.TotalMilliseconds;
            using CancellationTokenSource guard = new(Guard);
            byte[] payload = [3, 1, 4, 1];
            byte[] received = new byte[payload.Length];

            await client.Stream.WriteAsync(payload, guard.Token);
            await video.ReadExactlyAsync(received, guard.Token);
            Assert.Equal(payload, received);
            await video.WriteAsync(payload.AsMemory(), guard.Token);
            await video.FlushAsync(guard.Token);
            await client.Stream.ReadExactlyAsync(received, guard.Token);
            Assert.Equal(payload, received);

            await client.Stream.WriteAsync(new byte[] { 9 }, guard.Token);
            Assert.Equal(1, await video.ReadAsync(received, 0, 1, guard.Token));
            Assert.Equal(9, received[0]);
            await video.WriteAsync(new byte[] { 8 }, 0, 1, guard.Token);
            await client.Stream.ReadExactlyAsync(received.AsMemory(0, 1), guard.Token);
            Assert.Equal(8, received[0]);

            await client.Stream.WriteAsync(new byte[] { 7 }, guard.Token);
            Assert.Equal(1, video.Read(received.AsSpan(0, 1)));
            Assert.Equal(7, received[0]);
            video.Write(new byte[] { 6 }.AsSpan());
            video.Flush();
            await client.Stream.ReadExactlyAsync(received.AsMemory(0, 1), guard.Token);
            Assert.Equal(6, received[0]);

            await client.Stream.WriteAsync(new byte[] { 5 }, guard.Token);
            Assert.Equal(1, video.Read(received, 0, 1));
            Assert.Equal(5, received[0]);
            video.Write(new byte[] { 4 }, 0, 1);
            await client.Stream.ReadExactlyAsync(received.AsMemory(0, 1), guard.Token);
            Assert.Equal(4, received[0]);

            video.Dispose();
            await server.CloseAsync().WaitAsync(Guard);
            Assert.False(server.Stream.CanRead);
            Assert.False(server.Stream.CanWrite);
            // 物理关闭不等于 handler 完成，更不能提前归还 registration。
            Assert.Equal(1, host.ActiveConnections);
            Assert.Equal(1, host.AdmittedConnections);
            Assert.Empty(host.LifecycleErrors.Snapshot);
        }
        finally
        {
            release.TrySetResult();
            Assert.True((await host.StopAsync(Guard).WaitAsync(Guard + Guard)).AllFinished);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task HostRegistryForce_RequestsSameClose_WithoutReleasingLiveHandlerRegistration()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        TaskCompletionSource<AcceptedConnection> accepted = NewAcceptedSignal();
        TaskCompletionSource release = NewSignal();
        int port = GetFreePort();
        await using TransportHost host = CreateHost(certificate, async (connection, _) =>
        {
            accepted.TrySetResult(connection);
            await release.Task;
        }, port);

        try
        {
            Assert.True(host.Start().IsListening);
            using TlsConnection client = await ConnectAsync(port, certificate);
            AcceptedConnection server = await accepted.Task.WaitAsync(Guard);
            // 预算仅推动 registry 进入 force；不以调度快慢断言物理关闭已完成。
            TransportHostStopReport report = await host.StopAsync(TimeSpan.FromMilliseconds(100)).WaitAsync(Guard);
            Assert.Equal(1, report.UnfinishedConnections);
            Assert.False(report.AllFinished);
            Assert.Equal(1, host.ActiveConnections);
            Assert.Equal(1, host.AdmittedConnections);

            // 在主动调用 CloseAsync 之前先观察对端 EOF/重置，证明 force 确实发出了关闭请求。
            using CancellationTokenSource guard = new(Guard);
            try
            {
                Assert.Equal(0, await client.Stream.ReadAsync(new byte[1], guard.Token));
            }
            catch (IOException)
            {
                // socket 先关闭，TLS 未必能发送 close_notify；重置也属于强制关闭。
            }

            Task close = server.CloseAsync();
            await close.WaitAsync(Guard);
            Assert.False(server.Stream.CanRead);
            Assert.Same(close, server.CloseAsync());
            Assert.Equal(1, host.ActiveConnections);
        }
        finally
        {
            release.TrySetResult();
            Assert.True((await host.StopAsync(Guard).WaitAsync(Guard + Guard)).AllFinished);
        }

        Assert.Equal(0, host.ActiveConnections);
        Assert.Equal(0, host.AdmittedConnections);
        Assert.Empty(host.LifecycleErrors.Snapshot);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnregisteredClose_ComponentGate_PreventsAllFinishedUntilCleanupCompletes(bool closeThrows)
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        await using TransportHost host = CreateHost(certificate, (_, _) => Task.CompletedTask);
        using DisposeGate gate = new();
        Exception expected = new IOException("测试未登记连接关闭失败");
        ProbeDisposable socket = new(() =>
        {
            gate.Block();
            if (closeThrows)
            {
                throw expected;
            }
        });
        ConnectionCloseHandle closer = new(socket, host.LifecycleErrors);
        Task finishing = host.FinishConnectionAsync(closer, admission: null, registration: null);
        Task acceptLifetime = host.AwaitUnregisteredHandlingAsync(finishing, registered: false);

        try
        {
            // 组件接缝：复用真实 Finish 与 accept 持有策略，用反射将任务放进 Stop 的 accept join 集合。
            // 没有运行真实 AcceptLoop/subnet/admission 拒绝分支，不能把本用例称为网络拒绝路径覆盖。
            FieldInfo acceptLoopsField = typeof(TransportHost).GetField(
                "_acceptLoops", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.IsType<List<Task>>(acceptLoopsField.GetValue(host)).Add(acceptLifetime);

            await gate.Entered.Task.WaitAsync(Guard);
            Assert.False(finishing.IsCompleted);
            Assert.False(acceptLifetime.IsCompleted);
            Assert.Equal(0, host.ActiveConnections);
            Assert.Equal(0, host.AdmittedConnections);

            TransportHostStopReport blocked =
                await host.StopAsync(TimeSpan.FromMilliseconds(100)).WaitAsync(Guard);
            Assert.False(blocked.AcceptLoopsFinished);
            Assert.False(blocked.AllFinished);
            Assert.Equal(0, blocked.UnfinishedConnections);
            Assert.False(finishing.IsCompleted);
            Assert.False(closer.CloseAsync().IsCompleted);

            gate.Release();
            await acceptLifetime.WaitAsync(Guard);
            Assert.True(finishing.IsCompletedSuccessfully);
            Assert.True(acceptLifetime.IsCompletedSuccessfully);
            Assert.Equal(1, socket.DisposeCount);
            TransportHostStopReport completed = await host.StopAsync(Guard).WaitAsync(Guard + Guard);
            Assert.True(completed.AcceptLoopsFinished);
            Assert.True(completed.AllFinished);
            if (closeThrows)
            {
                Assert.Same(expected, Assert.Single(closer.CleanupErrors));
                AssertError(host.LifecycleErrors, HostLifecycleErrorKind.SocketCleanup, expected);
                Assert.Single(host.LifecycleErrors.Snapshot);
            }
            else
            {
                Assert.Empty(closer.CleanupErrors);
                Assert.Empty(host.LifecycleErrors.Snapshot);
            }
        }
        finally
        {
            gate.Release();
            await finishing.WaitAsync(Guard);
            await acceptLifetime.WaitAsync(Guard);
            await host.StopAsync(Guard).WaitAsync(Guard + Guard);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task RegisteredHandling_Component_DoesNotBlockAccept()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        await using TransportHost host = CreateHost(certificate, (_, _) => Task.CompletedTask);
        TaskCompletionSource handling = NewSignal();
        try
        {
            Task acceptContinuation = host.AwaitUnregisteredHandlingAsync(handling.Task, registered: true);
            Assert.True(acceptContinuation.IsCompletedSuccessfully);
            Assert.False(handling.Task.IsCompleted);
            Assert.Empty(host.LifecycleErrors.Snapshot);
        }
        finally
        {
            handling.TrySetResult();
            await handling.Task.WaitAsync(Guard);
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandlingFault_Component_IsObservedWithoutFaultingAccept(bool registered)
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        await using TransportHost host = CreateHost(certificate, (_, _) => Task.CompletedTask);
        Exception expected = new InvalidOperationException("测试 handling 兜底故障");
        TaskCompletionSource handling = NewSignal();
        if (registered)
        {
            // 已登记分支返回时不等待观察者；预置故障让诊断断言不依赖线程调度。
            handling.SetException(expected);
        }

        Task acceptContinuation = host.AwaitUnregisteredHandlingAsync(handling.Task, registered);
        if (!registered)
        {
            Assert.False(acceptContinuation.IsCompleted);
            handling.SetException(expected);
        }

        await acceptContinuation.WaitAsync(Guard);
        Assert.True(acceptContinuation.IsCompletedSuccessfully);
        AssertError(host.LifecycleErrors, HostLifecycleErrorKind.Connection, expected);
        Assert.Single(host.LifecycleErrors.Snapshot);
    }

    [Fact(Timeout = 60_000)]
    public async Task RegisteredTlsHandlers_RunConcurrently_WithoutBlockingAccept()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        TaskCompletionSource<AcceptedConnection> firstAccepted = NewAcceptedSignal();
        TaskCompletionSource<AcceptedConnection> secondAccepted = NewAcceptedSignal();
        TaskCompletionSource release = NewSignal();
        int handlerCount = 0;
        int port = GetFreePort();
        await using TransportHost host = CreateHost(certificate, async (connection, _) =>
        {
            if (Interlocked.Increment(ref handlerCount) == 1)
            {
                firstAccepted.TrySetResult(connection);
            }
            else
            {
                secondAccepted.TrySetResult(connection);
            }

            await release.Task;
        }, port);

        try
        {
            Assert.True(host.Start().IsListening);
            using TlsConnection first = await ConnectAsync(port, certificate);
            await firstAccepted.Task.WaitAsync(Guard);
            using TlsConnection second = await ConnectAsync(port, certificate);
            await secondAccepted.Task.WaitAsync(Guard);

            Assert.False(release.Task.IsCompleted);
            Assert.Equal(2, Volatile.Read(ref handlerCount));
            Assert.Equal(2, host.ActiveConnections);
            Assert.Equal(2, host.AdmittedConnections);
        }
        finally
        {
            release.TrySetResult();
            Assert.True((await host.StopAsync(Guard).WaitAsync(Guard + Guard)).AllFinished);
        }

        Assert.Equal(0, host.ActiveConnections);
        Assert.Equal(0, host.AdmittedConnections);
        Assert.Empty(host.LifecycleErrors.Snapshot);
    }

    [Fact(Timeout = 60_000)]
    public async Task HostDispose_DoesNotReplayTwoCompletedFaultedStops()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        TransportHost host = new(Array.Empty<IPAddress>(), new AllowLoopbackPolicy(), certificate,
            (_, _) => Task.CompletedTask,
            new TransportHostOptions { ShutdownTimeout = TimeSpan.FromSeconds(5) });
        using DisposeGate gate = new();
        CancellationTokenSource root = Assert.IsType<CancellationTokenSource>(typeof(TransportHost)
            .GetField("_stop", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host));
        using CancellationTokenRegistration callback = root.Token.Register(gate.Block);
        Task? disposal = null;

        try
        {
            // 两次真正的 Stop 均因根取消回调超预算而 fault；调用方分别持有并观察原 Task。
            Task<TransportHostStopReport> first = host.StopAsync(TimeSpan.FromMilliseconds(80));
            await gate.Entered.Task.WaitAsync(Guard);
            TimeoutException firstError = await Assert.ThrowsAsync<TimeoutException>(() => first.WaitAsync(Guard));
            Assert.Same(firstError, Assert.Single(first.Exception!.InnerExceptions));
            Task<TransportHostStopReport> second = host.StopAsync(TimeSpan.FromMilliseconds(80));
            TimeoutException secondError = await Assert.ThrowsAsync<TimeoutException>(() => second.WaitAsync(Guard));
            Assert.Same(secondError, Assert.Single(second.Exception!.InnerExceptions));
            Assert.NotSame(firstError, secondError);
            Assert.StartsWith("Host 停机预算已耗尽", firstError.Message);
            Assert.StartsWith("Host 停机预算已耗尽", secondError.Message);
            AssertError(host.LifecycleErrors, HostLifecycleErrorKind.Connection, firstError);
            Assert.DoesNotContain(host.LifecycleErrors.Snapshot,
                entry => ReferenceEquals(entry.Error, secondError));
            Assert.Single(host.LifecycleErrors.Snapshot);

            gate.Release();
            await gate.Exited.Task.WaitAsync(Guard);
            disposal = host.DisposeAsync().AsTask();
            await disposal.WaitAsync(Guard);
            Assert.Same(disposal, host.DisposeAsync().AsTask());
            AssertError(host.LifecycleErrors, HostLifecycleErrorKind.Connection, firstError);
            Assert.DoesNotContain(host.LifecycleErrors.Snapshot,
                entry => ReferenceEquals(entry.Error, secondError));
        }
        finally
        {
            gate.Release();
            await (disposal ?? host.DisposeAsync().AsTask()).WaitAsync(Guard);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task HostDispose_JoinsAndReportsStopThatWasPendingAtDisposeCall()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        TransportHost host = new(Array.Empty<IPAddress>(), new AllowLoopbackPolicy(), certificate,
            (_, _) => Task.CompletedTask,
            new TransportHostOptions { ShutdownTimeout = TimeSpan.FromSeconds(5) });
        using DisposeGate gate = new();
        CancellationTokenSource root = Assert.IsType<CancellationTokenSource>(typeof(TransportHost)
            .GetField("_stop", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host));
        using CancellationTokenRegistration callback = root.Token.Register(gate.Block);
        Task? disposal = null;

        try
        {
            Task<TransportHostStopReport> stop = host.StopAsync(TimeSpan.FromSeconds(1));
            await gate.Entered.Task.WaitAsync(Guard);
            Assert.False(stop.IsCompleted);
            disposal = host.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            TimeoutException stopError = await Assert.ThrowsAsync<TimeoutException>(() => stop.WaitAsync(Guard));
            Assert.Same(stopError, Assert.Single(stop.Exception!.InnerExceptions));
            Assert.False(disposal.IsCompleted);

            gate.Release();
            AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() => disposal.WaitAsync(Guard));
            Assert.Same(stopError, Assert.Single(failure.InnerExceptions));
            Assert.True(gate.Exited.Task.IsCompleted);
            Assert.Same(disposal, host.DisposeAsync().AsTask());
        }
        finally
        {
            gate.Release();
            if (disposal is null)
            {
                disposal = host.DisposeAsync().AsTask();
            }

            try
            {
                await disposal.WaitAsync(Guard);
            }
            catch (AggregateException) when (disposal.IsFaulted)
            {
                // 上方已断言 Dispose 传播仍 pending 的 Stop 故障。
            }
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HostDispose_ConcurrentCallsJoinStubbornHandlerAfterStopBudget(bool stopFirst)
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        int port = GetFreePort();
        TaskCompletionSource handlerEntered = NewSignal();
        TaskCompletionSource releaseHandler = NewSignal();
        TransportHost host = new(
            new[] { IPAddress.Loopback }, new AllowLoopbackPolicy(), certificate,
            async (_, _) =>
            {
                handlerEntered.TrySetResult();
                await releaseHandler.Task;
            },
            new TransportHostOptions
            {
                Port = port,
                MaxConnections = 1,
                MaxConnectionsPerAddress = 1,
                ShutdownTimeout = TimeSpan.FromMilliseconds(100),
            });
        Task? disposal = null;

        try
        {
            Assert.True(host.Start().IsListening);
            using TlsConnection client = await ConnectAsync(port, certificate);
            await handlerEntered.Task.WaitAsync(Guard);
            Assert.Equal(1, host.ActiveConnections);
            Assert.Equal(1, host.AdmittedConnections);

            Task<TransportHostStopReport> stop = host.StopAsync(TimeSpan.FromMilliseconds(100));
            if (stopFirst)
            {
                TransportHostStopReport initial = await stop.WaitAsync(Guard);
                Assert.False(initial.AllFinished);
                Assert.Equal(1, initial.UnfinishedConnections);
            }

            disposal = host.DisposeAsync().AsTask();
            Assert.Same(disposal, host.DisposeAsync().AsTask());
            TransportHostStopReport concurrent = await stop.WaitAsync(Guard);
            Assert.False(concurrent.AllFinished);
            Assert.Equal(1, concurrent.UnfinishedConnections);
            Assert.False(disposal.IsCompleted);
            Assert.Equal(1, host.ActiveConnections);
            Assert.Equal(1, host.AdmittedConnections);
            Assert.Equal(1, host.Limiter.InUseFor(IPAddress.Loopback));

            releaseHandler.TrySetResult();
            await disposal.WaitAsync(Guard);
            Assert.Equal(0, host.ActiveConnections);
            Assert.Equal(0, host.AdmittedConnections);
        }
        finally
        {
            releaseHandler.TrySetResult();
            await (disposal ?? host.DisposeAsync().AsTask()).WaitAsync(Guard);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task HostDispose_JoinsUnregisteredPhysicalCloseEvenWithNoRegistryEntries()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        TransportHost host = new(Array.Empty<IPAddress>(), new AllowLoopbackPolicy(), certificate,
            (_, _) => Task.CompletedTask,
            new TransportHostOptions { ShutdownTimeout = TimeSpan.FromMilliseconds(80) });
        using DisposeGate gate = new();
        ProbeDisposable socket = new(gate.Block);
        ConnectionCloseHandle closer = new(socket, host.LifecycleErrors);
        Task finishing = host.FinishConnectionAsync(closer, admission: null, registration: null);
        Task acceptLifetime = host.AwaitUnregisteredHandlingAsync(finishing, registered: false);
        FieldInfo acceptLoopsField = typeof(TransportHost).GetField(
            "_acceptLoops", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.IsType<List<Task>>(acceptLoopsField.GetValue(host)).Add(acceptLifetime);
        Task? disposal = null;

        try
        {
            await gate.Entered.Task.WaitAsync(Guard);
            Assert.Equal(0, host.ActiveConnections);
            Assert.Equal(0, host.AdmittedConnections);
            TransportHostStopReport report =
                await host.StopAsync(TimeSpan.FromMilliseconds(80)).WaitAsync(Guard);
            Assert.False(report.AcceptLoopsFinished);
            Assert.False(report.AllFinished);
            Assert.Equal(0, report.UnfinishedConnections);

            // 完整释放不能把 Count=0 当作未登记 socket 已关闭；重复 Stop 仍须报告未完成。
            TransportHostStopReport concurrent =
                await host.StopAsync(TimeSpan.FromMilliseconds(200)).WaitAsync(Guard);
            Assert.False(concurrent.AcceptLoopsFinished);
            disposal = host.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            Assert.False(closer.CloseAsync().IsCompleted);

            gate.Release();
            await disposal.WaitAsync(Guard);
            Assert.True(acceptLifetime.IsCompletedSuccessfully);
            Assert.True(finishing.IsCompletedSuccessfully);
            Assert.Equal(1, socket.DisposeCount);
        }
        finally
        {
            gate.Release();
            await finishing.WaitAsync(Guard);
            await acceptLifetime.WaitAsync(Guard);
            await (disposal ?? host.DisposeAsync().AsTask()).WaitAsync(Guard);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task HostDispose_ClosesSocketWhileEarlierStopRootCancellationIsBlocked()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        int port = GetFreePort();
        TaskCompletionSource handlerEntered = NewSignal();
        TaskCompletionSource releaseHandler = NewSignal();
        TransportHost host = CreateHost(certificate, async (_, _) =>
        {
            handlerEntered.TrySetResult();
            await releaseHandler.Task;
        }, port);
        using DisposeGate rootGate = new();
        CancellationTokenSource root = Assert.IsType<CancellationTokenSource>(typeof(TransportHost)
            .GetField("_stop", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host));
        using CancellationTokenRegistration callback = root.Token.Register(rootGate.Block);
        Task<TransportHostStopReport>? stop = null;
        Task? disposal = null;

        try
        {
            Assert.True(host.Start().IsListening);
            using TlsConnection client = await ConnectAsync(port, certificate);
            await handlerEntered.Task.WaitAsync(Guard);
            Task<int> clientRead = client.Stream.ReadAsync(new byte[1]).AsTask();
            stop = Task.Run(() => host.StopAsync(TimeSpan.FromMilliseconds(100)));
            await rootGate.Entered.Task.WaitAsync(Guard);
            Assert.False(stop.IsCompleted);

            // Stop 卡在根取消回调；Dispose 必须独立请求物理关闭，不能只等待旧 Stop 退场。
            Task<Task> disposeCall = Task.Run<Task>(() => host.DisposeAsync().AsTask());
            disposal = await disposeCall.WaitAsync(Guard);
            Assert.Same(disposal, host.DisposeAsync().AsTask());
            try
            {
                Assert.Equal(0, await clientRead.WaitAsync(Guard));
            }
            catch (IOException)
            {
                // 底层 socket 被强制关闭时 TLS 对端也可能报告连接重置。
            }

            Assert.False(rootGate.Exited.Task.IsCompleted);
            Assert.False(stop.IsCompleted);
            Assert.False(disposal.IsCompleted);
            releaseHandler.TrySetResult();
            Assert.True(SpinWait.SpinUntil(() => host.ActiveConnections == 0, Guard));
            Assert.Equal(0, host.AdmittedConnections);
            Assert.False(disposal.IsCompleted);

            rootGate.Release();
            await rootGate.Exited.Task.WaitAsync(Guard);
            await stop.WaitAsync(Guard);
            await disposal.WaitAsync(Guard);
        }
        finally
        {
            releaseHandler.TrySetResult();
            rootGate.Release();
            if (stop is not null)
            {
                await stop.WaitAsync(Guard);
            }

            await (disposal ?? host.DisposeAsync().AsTask()).WaitAsync(Guard);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task HostDispose_BeforeStartRejectsAnyLaterStart()
    {
        using X509Certificate2 certificate = TestCertificateFactory.Create();
        TransportHost host = CreateHost(certificate, (_, _) => Task.CompletedTask);
        Task disposal = host.DisposeAsync().AsTask();
        await disposal.WaitAsync(Guard);
        Assert.Throws<ObjectDisposedException>(() => host.Start());
        await host.DisposeAsync().AsTask().WaitAsync(Guard);
    }

    private static ConnectionSecurityContext CreateSecurity() =>
        new(IPAddress.Loopback, IPAddress.Loopback, 12345, SslProtocols.Tls12, new byte[32]);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<AcceptedConnection> NewAcceptedSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void AssertError(HostLifecycleErrors errors, HostLifecycleErrorKind kind, Exception expected) =>
        Assert.Same(expected, Assert.Single(errors.Snapshot, entry => entry.Kind == kind).Error);

    private static TransportHost CreateHost(
        X509Certificate2 certificate,
        Func<AcceptedConnection, CancellationToken, Task> handler,
        int port = 0) => new(
            port == 0 ? Array.Empty<IPAddress>() : new[] { IPAddress.Loopback },
            new AllowLoopbackPolicy(), certificate, handler,
            new TransportHostOptions { Port = port == 0 ? TransportConstants.Port : port });

    private static async Task<TlsConnection> ConnectAsync(int port, X509Certificate2 certificate)
    {
        Assert.True(ConnectionTarget.TryCreate(
            Guid.Parse("99999999-8888-7777-6666-555555555555"),
            IPAddress.Loopback, port, TestCertificateFactory.Fingerprint(certificate), out ConnectionTarget? target));
        using CancellationTokenSource guard = new(Guard);
        return await new TlsClientConnector().ConnectAsync(target!, cancellationToken: guard.Token);
    }

    private static int GetFreePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class AllowLoopbackPolicy : ISubnetPolicy
    {
        public bool IsAllowedPeer(IPAddress localAddress, IPAddress remoteAddress) =>
            IPAddress.IsLoopback(localAddress) && IPAddress.IsLoopback(remoteAddress);
    }

    private sealed class ProbeDisposable(Action? onDispose = null) : IDisposable
    {
        private int _disposeCount;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            onDispose?.Invoke();
        }
    }

    private sealed class ProbeSslStream(Action? onDispose = null)
        : SslStream(new MemoryStream(), leaveInnerStreamOpen: false)
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
            finally
            {
                base.Dispose(disposing);
            }
        }
    }

    private sealed class DisposeGate : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        internal TaskCompletionSource Entered { get; } = NewSignal();
        internal TaskCompletionSource Exited { get; } = NewSignal();

        internal void Block()
        {
            Entered.TrySetResult();
            try
            {
                if (!_release.Wait(Guard))
                {
                    throw new TimeoutException("测试释放闸门未放行。");
                }
            }
            finally
            {
                Exited.TrySetResult();
            }
        }

        internal void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
    }
}
