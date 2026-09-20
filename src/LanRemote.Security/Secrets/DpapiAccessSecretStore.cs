using System.Security.Cryptography;
using LanRemote.Core.Abstractions;
using LanRemote.Core.Models;
using Microsoft.Extensions.Logging;

namespace LanRemote.Security.Secrets;

/// <summary>
/// 基于 DPAPI 的访问密钥存储。
/// </summary>
/// <remarks>
/// <para>密钥本体为 <c>RandomNumberGenerator.GetBytes(16)</c>（128-bit），
/// 在 <c>secrets.bin</c> 中以 Crockford Base32 形式存放，整文件由 DPAPI(CurrentUser) 保护。</para>
/// <para>调用方拿到的 <see cref="AccessSecret"/> 是<b>内存中的副本</b>；
/// <see cref="RegenerateAsync"/> 用新的随机值覆盖存储的字段，因此旧密钥在下一次比较时必然失配——
/// 这就是「旧 key 立即失效」的实现方式，没有依赖任何缓存或 TTL。</para>
/// <para>本类型的所有日志都只描述操作本身，不含密钥内容。</para>
/// </remarks>
public sealed class DpapiAccessSecretStore : IAccessSecretStore
{
    private readonly DpapiSecretVault _vault;
    private readonly ILogger<DpapiAccessSecretStore>? _logger;

    /// <summary>构造存储。</summary>
    /// <param name="vault">秘密仓库。</param>
    /// <param name="logger">日志器，可为空。</param>
    public DpapiAccessSecretStore(DpapiSecretVault vault, ILogger<DpapiAccessSecretStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(vault);
        _vault = vault;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AccessSecret> LoadOrCreateAsync(CancellationToken cancellationToken = default)
    {
        (bool isValid, byte[] keyBytes) = await _vault
            .ReadAsync(
                bundle =>
                {
                    bool valid = SecretGenerator.TryDecodeAccessKey(bundle.AccessKey, out byte[] decoded);
                    return (valid, decoded);
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (isValid)
        {
            return new AccessSecret(keyBytes);
        }

        _logger?.LogWarning("secrets.bin 中的访问密钥不是合法的 128-bit 编码，将重新生成。");

        return await _vault
            .UpdateAsync(bundle => RepairAndMaterialize(bundle), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<AccessSecret> RegenerateAsync(CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("收到重新生成访问密钥的请求，旧密钥将立即失效。");

        return _vault.UpdateAsync(
            bundle =>
            {
                bundle.AccessKey = SecretGenerator.NewAccessKey();
                return Materialize(bundle.AccessKey);
            },
            cancellationToken);
    }

    private static AccessSecret RepairAndMaterialize(SecretBundle bundle)
    {
        // 这里解码只是为了「判断合法性」，不是要拿走密钥。
        // 所以探针字节用完必须清零，不能写成 out byte[] _ 让它躺在 GC 堆里（M1.2）。
        byte[] probe = Array.Empty<byte>();
        try
        {
            if (!SecretGenerator.TryDecodeAccessKey(bundle.AccessKey, out probe))
            {
                bundle.AccessKey = SecretGenerator.NewAccessKey();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(probe);
        }

        return Materialize(bundle.AccessKey);
    }

    private static AccessSecret Materialize(string base32AccessKey)
    {
        if (!SecretGenerator.TryDecodeAccessKey(base32AccessKey, out byte[] keyBytes))
        {
            throw new InvalidOperationException("无法把存储的访问密钥还原为 128-bit 原始字节。");
        }

        return new AccessSecret(keyBytes);
    }
}
