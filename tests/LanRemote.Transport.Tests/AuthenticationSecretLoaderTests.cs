using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;

namespace LanRemote.Transport.Tests;

public sealed class AuthenticationSecretLoaderTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    [Fact(Timeout = 30_000)]
    public async Task Completed_Load_Transfers_Ownership_Without_Clearing_And_Releases_Admission()
    {
        AuthenticationSecretLoader loader = new();
        using CancellationTokenSource caller = new();
        AccessSecret firstSecret = NewSecret(0x31);
        AccessSecret secondSecret = NewSecret(0x52);
        StubAccessSecretStore store = new((call, token) =>
        {
            Assert.Equal(caller.Token, token);
            return Task.FromResult(call == 1 ? firstSecret : secondSecret);
        });

        AccessSecret first = await loader.LoadAsync(store, caller.Token).WaitAsync(WaitTimeout);
        AccessSecret second = await loader.LoadAsync(store, caller.Token).WaitAsync(WaitTimeout);
        caller.Cancel();

        Assert.Same(firstSecret, first);
        Assert.Same(secondSecret, second);
        Assert.All(first.AccessKeyBytes, value => Assert.Equal((byte)0x31, value));
        Assert.All(second.AccessKeyBytes, value => Assert.Equal((byte)0x52, value));
        Assert.Equal(2, store.CallCount);
    }

    [Fact(Timeout = 30_000)]
    public async Task Pending_Load_Serializes_Different_Stores_And_Preserves_Both_Results()
    {
        AuthenticationSecretLoader loader = new();
        TaskCompletionSource<AccessSecret> firstPending = NewPending();
        TaskCompletionSource<AccessSecret> secondPending = NewPending();
        TaskCompletionSource secondEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AccessSecret firstSecret = NewSecret(0x13);
        AccessSecret secondSecret = NewSecret(0x24);
        StubAccessSecretStore firstStore = new((_, _) => firstPending.Task);
        StubAccessSecretStore secondStore = new((_, _) =>
        {
            secondEntered.TrySetResult();
            return secondPending.Task;
        });

        Task<AccessSecret> first = loader.LoadAsync(firstStore, CancellationToken.None);
        Task<AccessSecret> second = loader.LoadAsync(secondStore, CancellationToken.None);

        Assert.Equal(1, firstStore.CallCount);
        Assert.Equal(0, secondStore.CallCount);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        firstPending.SetResult(firstSecret);
        Assert.Same(firstSecret, await first.WaitAsync(WaitTimeout));
        await secondEntered.Task.WaitAsync(WaitTimeout);
        Assert.Equal(1, secondStore.CallCount);
        Assert.False(second.IsCompleted);

        secondPending.SetResult(secondSecret);
        Assert.Same(secondSecret, await second.WaitAsync(WaitTimeout));
        Assert.All(firstSecret.AccessKeyBytes, value => Assert.Equal((byte)0x13, value));
        Assert.All(secondSecret.AccessKeyBytes, value => Assert.Equal((byte)0x24, value));
    }

    [Fact(Timeout = 30_000)]
    public async Task Uncontended_Load_Executes_The_Store_Synchronous_Prefix_Inline()
    {
        AuthenticationSecretLoader loader = new();
        TaskCompletionSource<AccessSecret> pending = NewPending();
        AccessSecret secret = NewSecret(0x35);
        int callingThread = Environment.CurrentManagedThreadId;
        int storeThread = 0;
        StubAccessSecretStore store = new((_, _) =>
        {
            storeThread = Environment.CurrentManagedThreadId;
            return pending.Task;
        });

        Task<AccessSecret> load = loader.LoadAsync(store, CancellationToken.None);

        Assert.Equal(1, store.CallCount);
        Assert.Equal(callingThread, storeThread);
        Assert.False(load.IsCompleted);
        pending.SetResult(secret);
        Assert.Same(secret, await load.WaitAsync(WaitTimeout));
    }

    [Fact(Timeout = 30_000)]
    public async Task Cooperative_Cancellation_Reaches_The_Store_And_Allows_Recovery()
    {
        AuthenticationSecretLoader loader = new();
        using CancellationTokenSource caller = new();
        TaskCompletionSource<AccessSecret> pending = NewPending();
        AccessSecret recoveredSecret = NewSecret(0x46);
        CancellationToken receivedToken = default;
        StubAccessSecretStore store = new(async (call, token) =>
        {
            if (call != 1)
            {
                return recoveredSecret;
            }

            receivedToken = token;
            using CancellationTokenRegistration registration =
                token.Register(() => pending.TrySetCanceled(token));
            return await pending.Task;
        });
        Task<AccessSecret> load = loader.LoadAsync(store, caller.Token);

        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load.WaitAsync(WaitTimeout));
        Assert.Equal(caller.Token, receivedToken);
        Assert.True(pending.Task.IsCanceled);
        Assert.Same(recoveredSecret,
            await loader.LoadAsync(store, CancellationToken.None).WaitAsync(WaitTimeout));
        Assert.Equal(2, store.CallCount);
        Assert.All(recoveredSecret.AccessKeyBytes, value => Assert.Equal((byte)0x46, value));
    }

    [Fact(Timeout = 30_000)]
    public async Task Noncooperative_Late_Success_Is_Cleared_And_Admission_Remains_Single()
    {
        AuthenticationSecretLoader loader = new();
        using CancellationTokenSource caller = new();
        TaskCompletionSource<AccessSecret> late = NewPending();
        TaskCompletionSource<AccessSecret> secondPending = NewPending();
        TaskCompletionSource secondEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AccessSecret lateSecret = NewSecret(0x57);
        AccessSecret secondSecret = NewSecret(0x68);
        AccessSecret thirdSecret = NewSecret(0x79);
        byte[]? lateBytesAtSecondEntry = null;
        StubAccessSecretStore store = new((call, _) =>
        {
            if (call == 1)
            {
                return late.Task;
            }

            if (call == 2)
            {
                lateBytesAtSecondEntry = lateSecret.AccessKeyBytes.ToArray();
                secondEntered.TrySetResult();
                return secondPending.Task;
            }

            return Task.FromResult(thirdSecret);
        });
        Task<AccessSecret> abandoned = loader.LoadAsync(store, caller.Token);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.WaitAsync(WaitTimeout));

        Assert.False(late.Task.IsCompleted);
        Assert.All(lateSecret.AccessKeyBytes, value => Assert.Equal((byte)0x57, value));
        Task<AccessSecret> second = loader.LoadAsync(store, CancellationToken.None);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, store.CallCount);

        late.SetResult(lateSecret);
        await secondEntered.Task.WaitAsync(WaitTimeout);
        Assert.Equal(new byte[AccessSecret.AccessKeyByteLength], lateBytesAtSecondEntry);
        Assert.All(lateSecret.AccessKeyBytes, value => Assert.Equal((byte)0, value));

        // 迟到清理只能归还一个名额：第二个实际任务未结束时，第三次调用仍须排队。
        Task<AccessSecret> third = loader.LoadAsync(store, CancellationToken.None);
        Assert.Equal(2, store.CallCount);
        Assert.False(second.IsCompleted);
        Assert.False(third.IsCompleted);

        secondPending.SetResult(secondSecret);
        Assert.Same(secondSecret, await second.WaitAsync(WaitTimeout));
        Assert.Same(thirdSecret, await third.WaitAsync(WaitTimeout));
        Assert.All(secondSecret.AccessKeyBytes, value => Assert.Equal((byte)0x68, value));
        Assert.All(thirdSecret.AccessKeyBytes, value => Assert.Equal((byte)0x79, value));
        Assert.Equal(3, store.CallCount);
    }

    [Fact(Timeout = 30_000)]
    public async Task Noncooperative_Late_Fault_Allows_A_Queued_Load_To_Recover()
    {
        AuthenticationSecretLoader loader = new();
        using CancellationTokenSource caller = new();
        TaskCompletionSource<AccessSecret> late = NewPending();
        AccessSecret recoveredSecret = NewSecret(0x8A);
        StubAccessSecretStore store = new((call, _) =>
            call == 1 ? late.Task : Task.FromResult(recoveredSecret));
        Task<AccessSecret> abandoned = loader.LoadAsync(store, caller.Token);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.WaitAsync(WaitTimeout));

        Task<AccessSecret> recovery = loader.LoadAsync(store, CancellationToken.None);
        Assert.False(recovery.IsCompleted);
        Assert.Equal(1, store.CallCount);
        // 不读取 late.Task.Exception，也不 await 原任务；其异常由加载器独占观察。
        late.SetException(new InvalidOperationException("迟到存储失败"));

        Assert.Same(recoveredSecret, await recovery.WaitAsync(WaitTimeout));
        Assert.Equal(2, store.CallCount);
        Assert.All(recoveredSecret.AccessKeyBytes, value => Assert.Equal((byte)0x8A, value));
    }

    [Fact(Timeout = 30_000)]
    public async Task Noncooperative_Late_Cancellation_Allows_A_Queued_Load_To_Recover()
    {
        AuthenticationSecretLoader loader = new();
        using CancellationTokenSource caller = new();
        using CancellationTokenSource storeCancellation = new();
        TaskCompletionSource<AccessSecret> late = NewPending();
        AccessSecret recoveredSecret = NewSecret(0x9B);
        StubAccessSecretStore store = new((call, _) =>
            call == 1 ? late.Task : Task.FromResult(recoveredSecret));
        Task<AccessSecret> abandoned = loader.LoadAsync(store, caller.Token);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.WaitAsync(WaitTimeout));

        Task<AccessSecret> recovery = loader.LoadAsync(store, CancellationToken.None);
        Assert.False(recovery.IsCompleted);
        Assert.Equal(1, store.CallCount);
        storeCancellation.Cancel();
        late.SetCanceled(storeCancellation.Token);

        Assert.Same(recoveredSecret, await recovery.WaitAsync(WaitTimeout));
        Assert.Equal(2, store.CallCount);
        Assert.All(recoveredSecret.AccessKeyBytes, value => Assert.Equal((byte)0x9B, value));
    }

    [Fact(Timeout = 30_000)]
    public async Task Synchronous_Store_Exception_Propagates_And_Releases_Admission()
    {
        AuthenticationSecretLoader loader = new();
        InvalidOperationException expected = new("同步存储失败");
        AccessSecret recoveredSecret = NewSecret(0xAC);
        StubAccessSecretStore store = new((call, _) =>
        {
            if (call == 1)
            {
                throw expected;
            }

            return Task.FromResult(recoveredSecret);
        });

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(store, CancellationToken.None).WaitAsync(WaitTimeout));

        Assert.Same(expected, actual);
        Assert.Same(recoveredSecret,
            await loader.LoadAsync(store, CancellationToken.None).WaitAsync(WaitTimeout));
        Assert.Equal(2, store.CallCount);
    }

    [Fact(Timeout = 30_000)]
    public async Task Store_Task_Fault_Propagates_And_Releases_Admission()
    {
        AuthenticationSecretLoader loader = new();
        TaskCompletionSource<AccessSecret> pending = NewPending();
        InvalidOperationException expected = new("异步存储失败");
        AccessSecret recoveredSecret = NewSecret(0xBD);
        StubAccessSecretStore store = new((call, _) =>
            call == 1 ? pending.Task : Task.FromResult(recoveredSecret));
        Task<AccessSecret> load = loader.LoadAsync(store, CancellationToken.None);
        Task<AccessSecret> recovery = loader.LoadAsync(store, CancellationToken.None);
        Assert.Equal(1, store.CallCount);
        Assert.False(recovery.IsCompleted);

        pending.SetException(expected);

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => load.WaitAsync(WaitTimeout));
        Assert.Same(expected, actual);
        Assert.Same(recoveredSecret, await recovery.WaitAsync(WaitTimeout));
        Assert.Equal(2, store.CallCount);
    }

    [Fact(Timeout = 30_000)]
    public async Task Queued_Callers_Can_Cancel_Without_Entering_A_Stuck_Store()
    {
        AuthenticationSecretLoader loader = new();
        using CancellationTokenSource firstCaller = new();
        using CancellationTokenSource queuedCallers = new();
        TaskCompletionSource<AccessSecret> stuck = NewPending();
        AccessSecret lateSecret = NewSecret(0xCE);
        AccessSecret recoveredSecret = NewSecret(0xDF);
        StubAccessSecretStore store = new((call, _) =>
            call == 1 ? stuck.Task : Task.FromResult(recoveredSecret));
        Task<AccessSecret> abandoned = loader.LoadAsync(store, firstCaller.Token);
        firstCaller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.WaitAsync(WaitTimeout));

        Task<AccessSecret>[] queued = Enumerable.Range(0, 32)
            .Select(_ => loader.LoadAsync(store, queuedCallers.Token))
            .ToArray();
        Assert.All(queued, task => Assert.False(task.IsCompleted));
        Assert.Equal(1, store.CallCount);
        queuedCallers.Cancel();
        foreach (Task<AccessSecret> task in queued)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(WaitTimeout));
        }

        Assert.False(stuck.Task.IsCompleted);
        Assert.Equal(1, store.CallCount);
        Task<AccessSecret> recovery = loader.LoadAsync(store, CancellationToken.None);
        Assert.False(recovery.IsCompleted);
        Assert.Equal(1, store.CallCount);
        stuck.SetResult(lateSecret);

        Assert.Same(recoveredSecret, await recovery.WaitAsync(WaitTimeout));
        Assert.All(lateSecret.AccessKeyBytes, value => Assert.Equal((byte)0, value));
        Assert.Equal(2, store.CallCount);
    }

    [Fact(Timeout = 30_000)]
    public async Task Already_Cancelled_Token_Does_Not_Call_Store_Or_Consume_Admission()
    {
        AuthenticationSecretLoader loader = new();
        using CancellationTokenSource caller = new();
        caller.Cancel();
        AccessSecret secret = NewSecret(0xE1);
        StubAccessSecretStore store = new((_, _) => Task.FromResult(secret));

        OperationCanceledException cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => loader.LoadAsync(store, caller.Token).WaitAsync(WaitTimeout));

        Assert.Equal(caller.Token, cancellation.CancellationToken);
        Assert.Equal(0, store.CallCount);
        Assert.Same(secret, await loader.LoadAsync(store, CancellationToken.None).WaitAsync(WaitTimeout));
        Assert.Equal(1, store.CallCount);
        Assert.All(secret.AccessKeyBytes, value => Assert.Equal((byte)0xE1, value));
    }

    [Fact(Timeout = 30_000)]
    public async Task Cancellation_During_Store_Synchronous_Prefix_Still_Owns_The_Late_Result()
    {
        AuthenticationSecretLoader loader = new();
        using CancellationTokenSource caller = new();
        TaskCompletionSource<AccessSecret> late = NewPending();
        AccessSecret lateSecret = NewSecret(0xF2);
        AccessSecret recoveredSecret = NewSecret(0x83);
        StubAccessSecretStore store = new((call, _) =>
        {
            if (call == 1)
            {
                caller.Cancel();
                return late.Task;
            }

            return Task.FromResult(recoveredSecret);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => loader.LoadAsync(store, caller.Token).WaitAsync(WaitTimeout));
        Task<AccessSecret> recovery = loader.LoadAsync(store, CancellationToken.None);
        Assert.False(late.Task.IsCompleted);
        Assert.False(recovery.IsCompleted);
        Assert.Equal(1, store.CallCount);
        late.SetResult(lateSecret);

        Assert.Same(recoveredSecret, await recovery.WaitAsync(WaitTimeout));
        Assert.All(lateSecret.AccessKeyBytes, value => Assert.Equal((byte)0, value));
        Assert.Equal(2, store.CallCount);
    }

    private static TaskCompletionSource<AccessSecret> NewPending() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static AccessSecret NewSecret(byte value) =>
        new(Enumerable.Repeat(value, AccessSecret.AccessKeyByteLength).ToArray());

    private sealed class StubAccessSecretStore(
        Func<int, CancellationToken, Task<AccessSecret>> load) : IAccessSecretStore
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<AccessSecret> LoadOrCreateAsync(CancellationToken cancellationToken = default) =>
            load(Interlocked.Increment(ref _callCount), cancellationToken);

        public Task<AccessSecret> RegenerateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
