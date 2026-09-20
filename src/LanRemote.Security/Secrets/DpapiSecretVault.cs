using System.IO;
using LanRemote.Core.Infrastructure;
using Microsoft.Extensions.Logging;

namespace LanRemote.Security.Secrets;

/// <summary>
/// <c>secrets.bin</c> 的唯一读写入口。
/// </summary>
/// <remarks>
/// <para>设计要点：</para>
/// <list type="bullet">
/// <item><description>所有访问都经过一把 <see cref="SemaphoreSlim"/>，避免并发创建/覆盖；</description></item>
/// <item><description>写文件用「临时文件 + 原子替换」，避免写到一半断电留下残缺文件；</description></item>
/// <item><description>外部拿不到可变的 <see cref="SecretBundle"/> 实例：<see cref="ReadAsync{TResult}"/>
/// 交给投影函数的是缓存实例的 <b>Clone()</b>，<see cref="UpdateAsync{TResult}"/> 则只把 working copy
/// 交出去；因此任何一条路径上「顺手改一下 bundle」都影响不到缓存，更影响不到磁盘。</description></item>
/// <item><description>日志只记录结构性事件（是否新建、版本号），绝不记录任何 secret 字段。</description></item>
/// </list>
/// </remarks>
public sealed class DpapiSecretVault
{
    private readonly AppPaths _paths;
    private readonly ILogger<DpapiSecretVault>? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private SecretBundle? _cached;

    /// <summary>构造仓库。</summary>
    /// <param name="paths">数据目录定位（决定 secrets.bin 位置，便于测试隔离）。</param>
    /// <param name="logger">日志器，可为空。</param>
    public DpapiSecretVault(AppPaths paths, ILogger<DpapiSecretVault>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _logger = logger;
    }

    /// <summary>secrets.bin 的路径。</summary>
    public string SecretsFilePath => _paths.SecretsFilePath;

    /// <summary>
    /// 只读访问：把 bundle 的副本投影为调用方需要的值。
    /// </summary>
    /// <typeparam name="TResult">投影结果类型。</typeparam>
    /// <param name="projection">投影函数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>投影结果。</returns>
    /// <remarks>
    /// <b>M1.2 修复</b>：投影函数拿到的是缓存实例的 <see cref="SecretBundle.Clone"/>。
    /// 旧实现直接把缓存实例交出去，等于允许调用方在不经过 <see cref="UpdateAsync{TResult}"/> 的情况下
    /// 改掉内存状态，造成「内存改了、磁盘没改」——正是 <see cref="UpdateAsync{TResult}"/> 里
    /// 用 copy-on-write 修掉的那类撕裂状态。
    /// 代价是每次读多一次浅拷贝（字段都是值/Guid/string，开销可忽略）。
    /// </remarks>
    public async Task<TResult> ReadAsync<TResult>(
        Func<SecretBundle, TResult> projection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projection);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SecretBundle bundle = await EnsureLoadedCoreAsync(cancellationToken).ConfigureAwait(false);
            return projection(bundle.Clone());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 读写访问：copy-on-write。
    /// </summary>
    /// <typeparam name="TResult">操作结果类型。</typeparam>
    /// <param name="operation">修改并返回结果的函数；它只能看见 working copy。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>操作结果。</returns>
    /// <remarks>
    /// <para>严格顺序（M1.1 修复）：</para>
    /// <list type="number">
    /// <item><description>读取当前缓存的 bundle；</description></item>
    /// <item><description>克隆出独立的 working copy；</description></item>
    /// <item><description><paramref name="operation"/> 只修改 working copy；</description></item>
    /// <item><description>把 working copy <b>先落盘</b>；</description></item>
    /// <item><description><b>只有落盘成功</b>后才用 working copy 替换缓存。</description></item>
    /// </list>
    /// <para>因此在取消、<see cref="IOException"/>、<see cref="UnauthorizedAccessException"/>、
    /// DPAPI 失败等任何异常下，磁盘与内存<b>同时</b>保持旧状态，
    /// 不会出现「内存是新密钥、磁盘还是旧密钥」的撕裂状态。</para>
    /// </remarks>
    public async Task<TResult> UpdateAsync<TResult>(
        Func<SecretBundle, TResult> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SecretBundle current = await EnsureLoadedCoreAsync(cancellationToken).ConfigureAwait(false);

            // operation 拿到的是副本；它造成的任何副作用在失败时都会被整体丢弃。
            SecretBundle working = current.Clone();
            TResult result = operation(working);

            await PersistCoreAsync(working, cancellationToken).ConfigureAwait(false);

            // 只有走到这里才承认这次修改。
            _cached = working;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>仅供 emergency/diagnostics 使用：秘密文件是否存在。</summary>
    public bool SecretFileExists => File.Exists(_paths.SecretsFilePath);

    private async Task<SecretBundle> EnsureLoadedCoreAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        SecretBundle bundle;
        if (File.Exists(_paths.SecretsFilePath))
        {
            byte[] fileContent = await File.ReadAllBytesAsync(_paths.SecretsFilePath, cancellationToken)
                .ConfigureAwait(false);
            bundle = SecretFile.UnpackProtected(fileContent);
            _logger?.LogDebug("已加载 secrets.bin，版本={Version}", bundle.Version);
        }
        else
        {
            bundle = new SecretBundle
            {
                Version = SecretFile.CurrentVersion,
                DeviceGuid = SecretGenerator.NewDeviceGuid(),
                AccessKey = SecretGenerator.NewAccessKey(),
            };

            await PersistCoreAsync(bundle, cancellationToken).ConfigureAwait(false);
            _logger?.LogInformation("首次运行：已创建本机身份与 secrets.bin。");
        }

        _cached = bundle;
        return bundle;
    }

    private async Task PersistCoreAsync(SecretBundle bundle, CancellationToken cancellationToken)
    {
        // 在进入「可能已经写了临时文件」的阶段之前先检查取消，
        // 让「取消 → 磁盘保持旧状态」成为确定性行为而不是竞态。
        cancellationToken.ThrowIfCancellationRequested();

        _paths.EnsureCreated();

        byte[] content = SecretFile.PackProtected(bundle);
        string directory = Path.GetDirectoryName(_paths.SecretsFilePath)!;
        string temporaryPath = Path.Combine(directory, $"{Path.GetFileName(_paths.SecretsFilePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllBytesAsync(temporaryPath, content, cancellationToken).ConfigureAwait(false);

            // 原子替换：要么完整的新文件，要么完全没写。
            File.Move(temporaryPath, _paths.SecretsFilePath, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 清理临时文件失败不影响主流程。
        }
    }
}
