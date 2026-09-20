using LanRemote.Core.Configuration;
using LanRemote.Core.Models;

namespace LanRemote.Discovery;

/// <summary>
/// 本机发现用的不可变快照。
/// </summary>
/// <param name="LocalDeviceId">本机稳定标识。</param>
/// <param name="DeviceCode">本机设备码。</param>
/// <param name="DeviceName">本机显示名。</param>
/// <param name="AppVersion">应用版本。</param>
/// <param name="CertificateSha256">本机证书指纹。</param>
/// <param name="AllowDiscovery">是否允许被发现。</param>
/// <param name="AllowViewing">是否允许被查看。</param>
/// <param name="AllowControl">是否允许被控制。</param>
/// <remarks>
/// <para>announce 的所有内容都来自这个快照，DiscoveryService <b>不会</b>每 2 秒去读 config.json。</para>
/// <para>M2 只声明 <c>view</c> / <c>control</c>；M6 之前<b>不要</b>声明 <c>multi-monitor</c>，
/// 因为多显示器协议还没实现。capabilities 只是提示信息，不是权限授权。</para>
/// </remarks>
public sealed record DiscoveryRuntimeSnapshot(
    Guid LocalDeviceId,
    string DeviceCode,
    string DeviceName,
    string AppVersion,
    string CertificateSha256,
    bool AllowDiscovery,
    bool AllowViewing,
    bool AllowControl)
{
    /// <summary>是否应当主动 announce。</summary>
    public bool CanAnnounce => AllowDiscovery;

    /// <summary>是否应当回应别人的 probe。</summary>
    public bool CanReplyToProbe => AllowDiscovery;

    /// <summary>本次 announce 声明的能力集合。</summary>
    public IReadOnlySet<string> Capabilities => BuildCapabilities(AllowViewing, AllowControl);

    /// <summary>按开关计算能力集合。</summary>
    /// <param name="allowViewing">允许查看。</param>
    /// <param name="allowControl">允许控制。</param>
    /// <returns>能力集合。</returns>
    public static IReadOnlySet<string> BuildCapabilities(bool allowViewing, bool allowControl)
    {
        HashSet<string> capabilities = new(StringComparer.Ordinal);

        if (allowViewing)
        {
            capabilities.Add(DiscoveryConstants.CapabilityView);
        }

        if (allowControl)
        {
            capabilities.Add(DiscoveryConstants.CapabilityControl);
        }

        return capabilities;
    }
}

/// <summary>
/// 线程安全的运行时状态。
/// </summary>
/// <remarks>
/// <para>存在的目的：让「允许被发现」的开关变化<b>立即</b>生效，而不需要重启 UDP 服务，
/// 也不需要让 DiscoveryService 每 2 秒读一次磁盘。</para>
/// <para>在 <c>Initialize</c> 之前 <see cref="Current"/> 会抛异常：
/// 这样能确保「身份还没加载好就先广播」这类隐私事故不可能发生。</para>
/// </remarks>
public sealed class DiscoveryRuntimeState
{
    private readonly object _gate = new();

    private DiscoveryRuntimeSnapshot? _snapshot;

    /// <summary>是否已完成初始化（身份 + 配置都已就绪）。</summary>
    public bool IsInitialized
    {
        get
        {
            lock (_gate)
            {
                return _snapshot is not null;
            }
        }
    }

    /// <summary>当前快照。</summary>
    /// <exception cref="InvalidOperationException">尚未初始化。</exception>
    public DiscoveryRuntimeSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _snapshot
                    ?? throw new InvalidOperationException(
                        "DiscoveryRuntimeState 尚未初始化：必须先用本机身份与配置 Initialize。");
            }
        }
    }

    /// <summary>尝试取当前快照。</summary>
    /// <param name="snapshot">快照。</param>
    /// <returns>是否已初始化。</returns>
    public bool TryGetCurrent(out DiscoveryRuntimeSnapshot? snapshot)
    {
        lock (_gate)
        {
            snapshot = _snapshot;
            return snapshot is not null;
        }
    }

    /// <summary>用本机身份与配置完成首次初始化。</summary>
    /// <param name="identity">本机身份。</param>
    /// <param name="config">配置。</param>
    public void Initialize(DeviceIdentity identity, AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(config);

        lock (_gate)
        {
            _snapshot = new DiscoveryRuntimeSnapshot(
                identity.DeviceId,
                identity.DeviceCode,
                identity.DeviceName,
                Core.Infrastructure.AppVersion.Current,
                identity.CertificateSha256,
                config.AllowDiscovery,
                config.AllowViewing,
                config.AllowControl);
        }
    }

    /// <summary>
    /// 配置变化时更新快照（身份保持不变）。
    /// </summary>
    /// <param name="config">新配置。</param>
    /// <remarks>未初始化时是空操作：避免「配置先于身份到达」时把半截状态写进去。</remarks>
    public void UpdateFromConfig(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        lock (_gate)
        {
            if (_snapshot is null)
            {
                return;
            }

            _snapshot = _snapshot with
            {
                AllowDiscovery = config.AllowDiscovery,
                AllowViewing = config.AllowViewing,
                AllowControl = config.AllowControl,
            };
        }
    }
}
