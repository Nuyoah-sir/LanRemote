using System.Security.Cryptography;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;

namespace LanRemote.Transport;

/// <summary>供同一 Host/context 共享的访问密钥加载准入与迟到结果所有者。</summary>
internal sealed class AuthenticationSecretLoader
{
    private readonly SemaphoreSlim _admission;

    internal AuthenticationSecretLoader()
    {
        _admission = new SemaphoreSlim(1, 1);
    }

    internal async Task<AccessSecret> LoadAsync(
        IAccessSecretStore store,
        CancellationToken cancellationToken)
    {
        await _admission.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool releaseAdmission = true;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 直接执行存储的同步前缀，不用 Task.Run 制造无法约束的后台调用。
            Task<AccessSecret> pending = store.LoadOrCreateAsync(cancellationToken);
            try
            {
                return await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 即使此刻 pending 已成功，取消的等待者也不能再取得密钥所有权。
                // 先转交释放权；唯一迟到结果所有者保留准入，直到存储任务真正终结。
                releaseAdmission = false;
                _ = OwnLateResultAsync(pending);
                throw;
            }
        }
        finally
        {
            if (releaseAdmission)
            {
                _admission.Release();
            }
        }
    }

    private async Task OwnLateResultAsync(Task<AccessSecret> pending)
    {
        try
        {
            try
            {
                AccessSecret secret = await pending.ConfigureAwait(false);
                CryptographicOperations.ZeroMemory(secret.AccessKeyBytes);
            }
            finally
            {
                _admission.Release();
            }
        }
        catch (Exception)
        {
            // await 观察存储失败并消费取消；清理自身也不得留下未观察异常。
        }
    }
}
