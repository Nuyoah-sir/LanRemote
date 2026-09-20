using LanRemote.Core.Identity;
using LanRemote.Core.Models;
using LanRemote.Security.Certificates;
using LanRemote.Security.Secrets;
using Microsoft.Extensions.Logging;

namespace LanRemote.Security.Identity;

/// <summary>
/// 本机身份：稳定 deviceGuid、由它派生的可读设备码、机器名与 TLS 证书指纹。
/// </summary>
/// <remarks>
/// <para>deviceGuid 首次启动时随机生成，之后写入 DPAPI 保护的 <c>secrets.bin</c> 并保持不变；
/// 设备码由它派生（<see cref="DeviceCode"/>），因此设备码在重启后同样稳定。</para>
/// <para>重新生成访问密钥<b>不会</b>影响 deviceGuid 与设备码，
/// 因为轮换只覆盖 bundle 的 <c>AccessKey</c> 字段。</para>
/// <para>设备码不是秘密，日志允许出现；访问密钥绝不允许。</para>
/// </remarks>
public sealed class DeviceIdentityService
{
    private readonly DpapiSecretVault _vault;
    private readonly DeviceCertificateService _certificates;
    private readonly ILogger<DeviceIdentityService>? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private DeviceIdentity? _identity;

    /// <summary>构造服务。</summary>
    /// <param name="vault">秘密仓库。</param>
    /// <param name="certificates">设备证书服务。</param>
    /// <param name="logger">日志器，可为空。</param>
    public DeviceIdentityService(
        DpapiSecretVault vault,
        DeviceCertificateService certificates,
        ILogger<DeviceIdentityService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(certificates);

        _vault = vault;
        _certificates = certificates;
        _logger = logger;
    }

    /// <summary>取得本机身份；首次调用会同时确保设备证书已存在。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本机身份。</returns>
    public async Task<DeviceIdentity> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_identity is not null)
            {
                return _identity;
            }

            Guid deviceGuid = await _vault.ReadAsync(bundle => bundle.DeviceGuid, cancellationToken)
                .ConfigureAwait(false);

            DeviceCertificate certificate = await _certificates.GetOrCreateAsync(cancellationToken)
                .ConfigureAwait(false);

            _identity = new DeviceIdentity(
                deviceGuid,
                DeviceCode.Derive(deviceGuid),
                Environment.MachineName,
                certificate.Sha256FingerprintHex);

            _logger?.LogInformation(
                "本机身份就绪。名称={DeviceName}，设备码={DeviceCode}，证书指纹前缀={FingerprintPrefix}",
                _identity.DeviceName,
                _identity.DeviceCode,
                certificate.Sha256FingerprintHex[..8]);

            return _identity;
        }
        finally
        {
            _gate.Release();
        }
    }
}
