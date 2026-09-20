using System.Diagnostics.CodeAnalysis;

namespace LanRemote.Core.Infrastructure;

/// <summary>
/// 单实例守卫。
/// </summary>
/// <remarks>
/// <para>进程模型要求单实例（03_ARCHITECTURE.md 第 2 节）：后台 Host listener 与 Discovery
/// 都驻留在主进程中，重复实例会造成端口抢占和重复的设备列表项。</para>
/// <para>使用 <c>Local\</c> 前缀的命名 Mutex，作用范围为同一登录会话。
/// 放在 Core 是为了可以被单元测试直接覆盖；它只依赖 <see cref="System.Threading"/>。</para>
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>互斥体名称。</summary>
    public const string MutexName = @"Local\LanRemote.SingleInstance.v1";

    private Mutex? _mutex;
    private bool _disposed;

    private SingleInstanceGuard(Mutex mutex)
    {
        _mutex = mutex;
    }

    /// <summary>
    /// 尝试获取单实例所有权。
    /// </summary>
    /// <returns>成功则返回一个守卫实例，调用方负责释放；已有实例在运行时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// 创建与判定必须原子完成，否则两个进程可能都走到「不存在」分支，
    /// 因此直接使用 <see cref="Mutex(bool, string, out bool)"/> 的 out 参数，而不是先 <c>TryOpenExisting</c>。
    /// </remarks>
    [SuppressMessage(
        "Reliability",
        "CA2002:不要锁定具有弱标识的对象",
        Justification = "Mutex 本身就是跨进程同步原语，这是它的既定用法。")]
    public static SingleInstanceGuard? TryAcquire()
    {
        Mutex mutex = new(initiallyOwned: true, MutexName, out bool createdNew);

        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        return new SingleInstanceGuard(mutex);
    }

    /// <summary>释放 Mutex 所有权。可重复调用。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        _mutex = null;
    }
}
