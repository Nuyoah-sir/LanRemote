using LanRemote.Core.Models;

namespace LanRemote.Core.Abstractions;

/// <summary>
/// 本机访问密钥的存储与轮换。
/// </summary>
/// <remarks>
/// <para>ADR-011：规格样例中的同步签名 <c>AccessSecret LoadOrCreate()</c> 在此调整为异步。
/// 原因：<c>06_DEV_STANDARDS.md</c> 第 2 节要求「I/O 全异步、不使用 <c>.Result</c>/<c>.Wait()</c> 阻塞」，
/// 而 DPAPI 读写必定是文件 I/O。职责未变，仅方法与返回同步原语的形式变化。</para>
/// <para>存储必须走 Windows DPAPI (<c>ProtectedData</c>, CurrentUser)，文件落在 <c>secrets.bin</c>，
/// 绝不写入 <c>config.json</c>。Regenerate 之后旧密钥必须立即失效。M1 实现。</para>
/// </remarks>
public interface IAccessSecretStore
{
    /// <summary>加载既有访问密钥；不存在时按 128-bit 随机生成并持久化。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>访问密钥。</returns>
    Task<AccessSecret> LoadOrCreateAsync(CancellationToken cancellationToken = default);

    /// <summary>重新生成 128-bit 访问密钥，旧密钥立即失效。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新的访问密钥。</returns>
    Task<AccessSecret> RegenerateAsync(CancellationToken cancellationToken = default);
}
