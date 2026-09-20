namespace LanRemote.Core.Models;

/// <summary>
/// 本设备的稳定内部标识。
/// </summary>
/// <param name="DeviceId">首次启动生成的随机稳定 GUID，永不作为认证依据。</param>
/// <param name="DeviceCode">人类可读短码，展示形式如 <c>7K3M-P9QX</c>，不是秘密。</param>
/// <param name="DeviceName">可变的机器显示名。</param>
/// <param name="CertificateSha256">本设备 TLS 证书 SHA-256 指纹的大写十六进制，用于 pinning。</param>
/// <remarks>
/// <see cref="DeviceId"/>、<see cref="DeviceCode"/>、<see cref="DeviceName"/> 三者职责不同，
/// 不得混用（见 <c>06_DEV_STANDARDS.md</c> 第 3 节）。M1 负责生成，M0 仅定义模型。
/// </remarks>
public sealed record DeviceIdentity(
    Guid DeviceId,
    string DeviceCode,
    string DeviceName,
    string CertificateSha256);
