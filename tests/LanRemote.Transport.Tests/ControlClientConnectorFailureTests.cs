using System.Net;
using System.Security.Authentication;
using LanRemote.Core.Models;

namespace LanRemote.Transport.Tests;

public sealed class ControlClientConnectorFailureTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    public static IEnumerable<object[]> CompoundFailures()
    {
        foreach (string kind in new[]
        {
            "empty", "single", "raw", "nested", "all-oce", "wrapped",
            "io-wrapped", "auth-wrapped", "deep-wrapped", "oce-wrapped", "oce-deep-wrapped",
        })
        foreach (bool asynchronous in new[] { false, true })
        foreach (bool cancelCaller in new[] { false, true })
            yield return [kind, asynchronous, cancelCaller];
    }

    public static IEnumerable<object[]> OrdinaryFailures()
    {
        foreach (string kind in new[]
        {
            "io", "authentication", "control-authentication", "wrapped",
            "oce-default", "oce-caller", "oce-other", "oce-inner", "task-canceled",
        })
        foreach (bool asynchronous in new[] { false, true })
        foreach (bool cancelCaller in new[] { false, true })
            yield return [kind, asynchronous, cancelCaller];
    }

    [Theory(Timeout = 60_000)]
    [MemberData(nameof(CompoundFailures))]
    public async Task CompoundTlsFailure_PreservesOriginalTreeAndAsyncExceptionStatus_EvenAfterCallerCancellation(
        string kind, bool asynchronous, bool cancelCaller)
    {
        using CancellationTokenSource caller = new();
        Exception failure = CreateCompoundFailure(kind, caller.Token);
        FailureFixture fixture = new(failure, asynchronous, caller);
        try
        {
            (Task<AuthenticatedControlSession> original, Exception? observed) =
                await fixture.FailAsync(cancelCaller);

            // Task.Exception 自带聚合包装，不能拿它冒充 await 抛出的原异常。
            AssertSameTree(failure, observed);
            // async builder 按最外层异常类型决定状态：原 OCE（即便包有 aggregate）
            // 必为 Canceled；保留其原实例/异常树与 token，不为强造 Faulted 改写异常。
            Assert.Equal(failure is OperationCanceledException ? TaskStatus.Canceled : TaskStatus.Faulted,
                original.Status);
            Assert.Same(observed, await Record.ExceptionAsync(() => original).WaitAsync(Guard));
        }
        finally
        {
            await fixture.FinishAsync();
        }
    }

    [Theory(Timeout = 60_000)]
    [MemberData(nameof(OrdinaryFailures))]
    public async Task OrdinaryTlsFailure_PreservesCallerCancellationOrUncanceledOriginal(
        string kind, bool asynchronous, bool cancelCaller)
    {
        using CancellationTokenSource caller = new();
        Exception failure = CreateOrdinaryFailure(kind, caller.Token);
        FailureFixture fixture = new(failure, asynchronous, caller);
        try
        {
            (Task<AuthenticatedControlSession> original, Exception? observed) =
                await fixture.FailAsync(cancelCaller);

            if (cancelCaller)
            {
                OperationCanceledException canceled = Assert.IsType<OperationCanceledException>(observed);
                Assert.Equal(caller.Token, canceled.CancellationToken);
                Assert.True(canceled.CancellationToken.IsCancellationRequested);
                Assert.Null(canceled.InnerException);
                Assert.Equal(TaskStatus.Canceled, original.Status);
                Assert.False(original.IsFaulted);
            }
            else
            {
                AssertSameTree(failure, observed);
                // 原 OCE 沿用 async builder 的取消状态；其他最外层异常保持 Faulted。
                Assert.Equal(failure is OperationCanceledException ? TaskStatus.Canceled : TaskStatus.Faulted,
                    original.Status);
            }
        }
        finally
        {
            await fixture.FinishAsync();
        }
    }

    private static Exception CreateCompoundFailure(string kind, CancellationToken caller)
    {
        AuthenticationException primary = new("测试 TLS 原始认证失败。");
        IOException cleanup = new("测试 TLS 原始清理失败。");
        AggregateException raw = new("测试主失败与清理失败。", primary, cleanup);
        return kind switch
        {
            "empty" => new AggregateException("测试空聚合也不是普通取消。"),
            "single" => new AggregateException("测试单项聚合不得拆开。", primary),
            "raw" => raw,
            "nested" => new AggregateException("测试有序嵌套根节点。",
                new AggregateException("测试主失败分支。", primary,
                    new OperationCanceledException("测试主失败分支的取消。", caller)),
                new AggregateException("测试清理分支。", cleanup,
                    new AggregateException("测试深层清理分支。", new IOException("测试深层清理失败。")))),
            "all-oce" => new AggregateException("测试全为 OCE 的聚合仍是复合故障。",
                new OperationCanceledException("测试 caller 取消分支。", caller),
                new OperationCanceledException("测试独立取消分支。", new CancellationToken(canceled: true))),
            "wrapped" => new InvalidOperationException("测试普通外层包装。", raw),
            "io-wrapped" => new IOException("测试 I/O 外层包装。", raw),
            "auth-wrapped" => new AuthenticationException("测试认证外层包装。", raw),
            "deep-wrapped" => new IOException("测试多层 I/O 包装。",
                new AuthenticationException("测试多层认证包装。",
                    new InvalidOperationException("测试多层普通包装。", raw))),
            "oce-wrapped" => new OperationCanceledException("测试 OCE 直接包装聚合。", raw, caller),
            "oce-deep-wrapped" => new OperationCanceledException("测试 OCE 间接包装聚合。",
                new IOException("测试 OCE 的普通内层。",
                    new AuthenticationException("测试内层认证包装。", raw)), new CancellationToken(canceled: true)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static Exception CreateOrdinaryFailure(string kind, CancellationToken caller) => kind switch
    {
        "io" => new IOException("测试普通 TLS I/O 失败。"),
        "authentication" => new AuthenticationException("测试普通 TLS 认证失败。"),
        "control-authentication" => new ControlClientAuthenticationException(
            "test-authentication-failed", "测试认证异常类别保持不变。"),
        "wrapped" => new IOException("测试无聚合的普通包装。",
            new AuthenticationException("测试无聚合的普通内层。")),
        "oce-default" => new OperationCanceledException("测试不携带 token 的普通取消。"),
        "oce-caller" => new OperationCanceledException("测试携带 caller token 的普通取消。", caller),
        "oce-other" => new OperationCanceledException("测试携带独立 token 的普通取消。",
            new CancellationToken(canceled: true)),
        "oce-inner" => new OperationCanceledException("测试只带普通内层的取消。",
            new IOException("测试不含聚合的取消内层。"), new CancellationToken(canceled: true)),
        "task-canceled" => new TaskCanceledException("测试普通 OCE 派生类型。", null,
            new CancellationToken(canceled: true)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static void AssertSameTree(Exception expected, Exception? actual)
    {
        Assert.Same(expected, actual);
        Assert.Same(expected.InnerException, actual!.InnerException);
        if (expected is OperationCanceledException expectedCancellation)
        {
            Assert.Equal(expectedCancellation.CancellationToken,
                Assert.IsAssignableFrom<OperationCanceledException>(actual).CancellationToken);
        }

        if (expected is AggregateException expectedAggregate)
        {
            AggregateException actualAggregate = Assert.IsType<AggregateException>(actual);
            Assert.Equal(expectedAggregate.InnerExceptions.Count, actualAggregate.InnerExceptions.Count);
            // 逐节点、逐位置比较，不 Flatten、不按消息比较，也不剥离普通包装。
            for (int i = 0; i < expectedAggregate.InnerExceptions.Count; i++)
                AssertSameTree(expectedAggregate.InnerExceptions[i], actualAggregate.InnerExceptions[i]);
        }
        else if (expected.InnerException is not null)
        {
            AssertSameTree(expected.InnerException, actual.InnerException);
        }
    }

    // 只在真实公开入口的 TLS 失败边界注入；不构造 TlsConnection 或认证成功结果。
    private sealed class FailureFixture
    {
        private readonly Exception _failure;
        private readonly bool _asynchronous;
        private readonly CancellationTokenSource _caller;
        private readonly byte[] _key = Convert.FromHexString("102132435465768798A9BACBDCEDFE0F");
        private readonly byte[] _keyBefore;
        private readonly ConnectionTarget _target;
        private readonly TransportTimeouts _timeouts = new(
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(6));
        private readonly TimeProvider _clock = new FakeClock(new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero));
        private readonly TaskCompletionSource<(ConnectionTarget Target, TransportTimeouts Timeouts,
            TimeProvider Clock, CancellationToken Token, bool Canceled)> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<TlsConnection> _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<Task<AuthenticatedControlSession>> _returned =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _thread;
        private bool _started;
        private int _calls;

        internal FailureFixture(Exception failure, bool asynchronous, CancellationTokenSource caller)
        {
            _failure = failure;
            _asynchronous = asynchronous;
            _caller = caller;
            _keyBefore = _key.ToArray();
            Assert.True(ConnectionTarget.TryCreate(
                Guid.Parse("11111111-2222-3333-4444-555555555555"), IPAddress.Loopback, 12345,
                "808182838485868788898A8B8C8D8E8F909192939495969798999A9B9C9D9E9F",
                out ConnectionTarget? target));
            _target = target!;
            ControlClientConnector connector = new(ConnectTls);
            _thread = new Thread(() =>
            {
                try
                {
                    // 单独传回公开方法的原 Task；不能用 Task.Run 解包后的代理任务断言状态。
                    _returned.TrySetResult(connector.ConnectAndAuthenticateAsync(
                        _target, Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), "连接失败测试客户端",
                        _key, SessionPermission.Control, timeouts: _timeouts, clock: _clock,
                        cancellationToken: _caller.Token));
                }
                catch (Exception error)
                {
                    _returned.TrySetException(error);
                }
            }) { IsBackground = true };
        }

        private Task<TlsConnection> ConnectTls(
            ConnectionTarget target, TransportTimeouts timeouts, TimeProvider clock, CancellationToken token)
        {
            Interlocked.Increment(ref _calls);
            _entered.TrySetResult((target, timeouts, clock, token, token.IsCancellationRequested));
            if (_asynchronous) return _pending.Task;

            // 专用线程只用于同步委托的闸门；释放后直接 throw，绝不伪装成异步失败。
            _release.Task.GetAwaiter().GetResult();
            throw _failure;
        }

        internal async Task<(Task<AuthenticatedControlSession> Original, Exception? Observed)> FailAsync(bool cancelCaller)
        {
            Assert.False(_caller.IsCancellationRequested);
            _thread.Start();
            _started = true;
            var invocation = await _entered.Task.WaitAsync(Guard);
            Assert.False(invocation.Canceled);
            Assert.Same(_target, invocation.Target);
            Assert.Same(_timeouts, invocation.Timeouts);
            Assert.Same(_clock, invocation.Clock);
            Assert.Equal(_caller.Token, invocation.Token);
            Assert.Equal(1, Volatile.Read(ref _calls));
            Assert.Equal(_keyBefore, _key);

            Task<AuthenticatedControlSession>? original = _asynchronous
                ? await _returned.Task.WaitAsync(Guard) : null;
            AssertPending(original);
            // 只有实际进入 TLS 委托且原操作尚未交付故障后，才允许取消 caller。
            if (cancelCaller) _caller.Cancel();
            Assert.Equal(cancelCaller, invocation.Token.IsCancellationRequested);
            AssertPending(original);
            ReleaseFailure();

            original ??= await _returned.Task.WaitAsync(Guard);
            Exception? observed = await Record.ExceptionAsync(() => original).WaitAsync(Guard);
            if (_asynchronous)
            {
                Assert.Equal(TaskStatus.Faulted, _pending.Task.Status);
                Assert.Same(_failure, await Record.ExceptionAsync(() => _pending.Task).WaitAsync(Guard));
            }
            Assert.Equal(1, Volatile.Read(ref _calls));
            Assert.Equal(cancelCaller, _caller.IsCancellationRequested);
            return (original, observed);
        }

        private void AssertPending(Task<AuthenticatedControlSession>? original)
        {
            Assert.False(_release.Task.IsCompleted);
            Assert.False(_pending.Task.IsCompleted);
            if (_asynchronous) Assert.False(original!.IsCompleted);
            else Assert.False(_returned.Task.IsCompleted);
        }

        private void ReleaseFailure()
        {
            _release.TrySetResult();
            if (_asynchronous) _pending.TrySetException(_failure);
        }

        internal async Task FinishAsync()
        {
            // 红测断言失败也先放闸，再观察原 TLS 任务、公开方法原任务并 join 调用线程。
            ReleaseFailure();
            try
            {
                if (_asynchronous)
                    _ = await Record.ExceptionAsync(() => _pending.Task).WaitAsync(Guard);
                if (_started)
                {
                    Task<AuthenticatedControlSession> original = await _returned.Task.WaitAsync(Guard);
                    _ = await Record.ExceptionAsync(async () => (await original).Dispose()).WaitAsync(Guard);
                    Assert.True(original.IsCompleted);
                }
            }
            finally
            {
                try
                {
                    if (_started) Assert.True(_thread.Join(Guard), "测试公开入口调用线程未结束。");
                }
                finally
                {
                    // 检查调用方传入的同一数组，不把生产私有副本清零误判成 caller 数组清零。
                    Assert.Equal(_keyBefore, _key);
                }
            }
        }
    }
}
