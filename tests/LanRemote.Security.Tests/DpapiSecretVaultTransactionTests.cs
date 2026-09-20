using System.IO;
using LanRemote.Core.Models;
using LanRemote.Security.Secrets;
using Xunit;

namespace LanRemote.Security.Tests;

/// <summary>
/// <see cref="DpapiSecretVault.UpdateAsync{TResult}"/> 的事务一致性回归测试。
/// </summary>
/// <remarks>
/// M1.1 的核心修复：修改只发生在 working copy 上，只有落盘成功才替换缓存。
/// 这组用例专门验证「失败时磁盘与内存<b>同时</b>回到旧状态」，
/// 也就是不能出现「内存是新密钥、磁盘还是旧密钥」。
/// </remarks>
public sealed class DpapiSecretVaultTransactionTests : IDisposable
{
    private readonly TempSecretRoot _root = new();
    private readonly LogSink _logs = new();

    public void Dispose() => _root.Dispose();

    private DpapiSecretVault CreateVault() => new(_root.Paths, new CapturingLogger<DpapiSecretVault>(_logs));

    [Fact]
    public async Task CancelledPersist_LeavesDiskAndMemoryUnchanged()
    {
        DpapiSecretVault vault = CreateVault();

        // 先建立稳定的旧状态 A。
        string keyA = await vault.ReadAsync(bundle => bundle.AccessKey);

        using CancellationTokenSource cts = new();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => vault.UpdateAsync(
                bundle =>
                {
                    // 在 working copy 上写入一个「新密钥」，然后立刻取消。
                    bundle.AccessKey = SecretGenerator.NewAccessKey();
                    cts.Cancel();
                    return bundle.AccessKey;
                },
                cts.Token));

        // 1) 同一个 vault 的内存状态必须仍是 A。
        string stillInMemory = await vault.ReadAsync(bundle => bundle.AccessKey);
        Assert.Equal(keyA, stillInMemory);

        // 2) 重新从磁盘建立的新 vault 也必须是 A。
        string stillOnDisk = await CreateVault().ReadAsync(bundle => bundle.AccessKey);
        Assert.Equal(keyA, stillOnDisk);
    }

    [Fact]
    public async Task AccessKeyRotation_FailedPersist_KeepsOldKeyServable()
    {
        DpapiSecretVault vault = CreateVault();
        DpapiAccessSecretStore store = new(vault, new CapturingLogger<DpapiAccessSecretStore>(_logs));

        AccessSecret original = await store.LoadOrCreateAsync();

        using CancellationTokenSource cts = new();

        // rotation 走的正是 vault.UpdateAsync 这条路径；让它在落盘前取消。
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => vault.UpdateAsync(
                bundle =>
                {
                    bundle.AccessKey = SecretGenerator.NewAccessKey();
                    cts.Cancel();
                    return bundle.AccessKey;
                },
                cts.Token));

        // 失败之后对外提供的仍然是旧 key —— 否则会出现「UI 说已轮换、实际磁盘没换」的假象。
        AccessSecret afterFailure = await store.LoadOrCreateAsync();
        Assert.Equal(original.AccessKeyBytes, afterFailure.AccessKeyBytes);

        AccessSecret afterRestart = await new DpapiAccessSecretStore(
            CreateVault(),
            new CapturingLogger<DpapiAccessSecretStore>(_logs)).LoadOrCreateAsync();
        Assert.Equal(original.AccessKeyBytes, afterRestart.AccessKeyBytes);
    }

    [Fact]
    public async Task SuccessfulUpdate_IsVisibleToFreshVault()
    {
        DpapiSecretVault vault = CreateVault();
        await vault.ReadAsync(bundle => bundle.DeviceGuid);

        string rotated = await vault.UpdateAsync(
            bundle =>
            {
                bundle.AccessKey = SecretGenerator.NewAccessKey();
                return bundle.AccessKey;
            });

        Assert.Equal(rotated, await vault.ReadAsync(bundle => bundle.AccessKey));
        Assert.Equal(rotated, await CreateVault().ReadAsync(bundle => bundle.AccessKey));
    }

    [Fact]
    public async Task ReadProjectionMutation_DoesNotChangeCacheOrDisk()
    {
        DpapiSecretVault vault = CreateVault();

        // 1) 先建立稳定的旧状态 A。
        string keyA = await vault.ReadAsync(bundle => bundle.AccessKey);

        // 2) 在 projection 里故意改动拿到的实例。
        string tampered = await vault.ReadAsync(
            bundle =>
            {
                bundle.AccessKey = "TAMPERED";
                return bundle.AccessKey;
            });

        Assert.Equal("TAMPERED", tampered);

        // 3) 同一个 vault 再次读取仍必须是 A —— projection 拿到的是 clone。
        string stillInMemory = await vault.ReadAsync(bundle => bundle.AccessKey);
        Assert.Equal(keyA, stillInMemory);

        // 4) 全新 vault 从磁盘读取也必须是 A。
        string stillOnDisk = await CreateVault().ReadAsync(bundle => bundle.AccessKey);
        Assert.Equal(keyA, stillOnDisk);
    }

    [Fact]
    public async Task Clone_ProducesIndependentCopy()
    {
        DpapiSecretVault vault = CreateVault();
        string keyA = await vault.ReadAsync(bundle => bundle.AccessKey);

        await vault.UpdateAsync(
            bundle =>
            {
                // 即使外部拿到的 working copy 被改坏，只要没落盘成功，缓存就不受影响。
                bundle.AccessKey = "TAMPERED";
                return true;
            });

        // 上面那次是成功的（无异常），所以缓存确实变成了 TAMPERED。
        Assert.Equal("TAMPERED", await vault.ReadAsync(bundle => bundle.AccessKey));

        SecretBundle original = new() { AccessKey = keyA, DeviceGuid = Guid.NewGuid() };
        SecretBundle copy = original.Clone();
        copy.AccessKey = "CHANGED";

        Assert.Equal(keyA, original.AccessKey);
    }

    [Fact]
    public async Task CancelledPersist_DoesNotLeaveTemporaryFiles()
    {
        DpapiSecretVault vault = CreateVault();
        await vault.ReadAsync(bundle => bundle.DeviceGuid);

        using CancellationTokenSource cts = new();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => vault.UpdateAsync(bundle => { cts.Cancel(); return true; }, cts.Token));

        string[] files = Directory.GetFiles(_root.Paths.RootDirectory);
        Assert.DoesNotContain(files, f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }
}
