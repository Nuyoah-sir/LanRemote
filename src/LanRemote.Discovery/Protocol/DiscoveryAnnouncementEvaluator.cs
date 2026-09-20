using System.Net;
using LanRemote.Core.Identity;
using LanRemote.Core.Models;

namespace LanRemote.Discovery.Protocol;

/// <summary>
/// 公告的严格校验与「远端地址权威」落实。
/// </summary>
/// <remarks>
/// <para>校验项（M2 要求第十三节）：magic / protocol / type / deviceId / deviceCode 一致性 /
/// deviceName / tcpPort / certSha256 / nonce / capabilities。</para>
/// <para><b>远端地址必须来自 UDP source endpoint</b>，本类从 payload 里拿不到任何地址字段——
/// 这是刻意的：DTO 里就没有地址字段，避免以后有人「顺手」相信 payload。</para>
/// <para>deviceCode 一致性只是一层<b>数据完整性</b>检查，不是认证：
/// 攻击者完全可以自己生成 Guid 并算出匹配的 DeviceCode。</para>
/// </remarks>
public sealed class DiscoveryAnnouncementEvaluator
{
    /// <summary>
    /// 评估一条公告。
    /// </summary>
    /// <param name="announcement">已解析的公告。</param>
    /// <param name="sourceAddress">UDP 来源地址（权威）。</param>
    /// <param name="local">本机运行时快照。</param>
    /// <param name="now">当前时间（UTC）。</param>
    /// <param name="device">成功时产出设备条目。</param>
    /// <param name="rejectReason">失败原因，用于 Debug 日志；不含 payload 原文。</param>
    /// <returns>是否接受。</returns>
    public bool TryEvaluate(
        DiscoveryAnnouncement announcement,
        IPAddress sourceAddress,
        DiscoveryRuntimeSnapshot local,
        DateTimeOffset now,
        out DiscoveredDevice? device,
        out string? rejectReason)
    {
        device = null;
        rejectReason = null;

        if (announcement is null)
        {
            rejectReason = "空公告";
            return false;
        }

        if (sourceAddress is null)
        {
            rejectReason = "来源地址缺失";
            return false;
        }

        if (!string.Equals(announcement.Magic, DiscoveryConstants.Magic, StringComparison.Ordinal))
        {
            rejectReason = "magic 不匹配";
            return false;
        }

        if (announcement.Protocol != DiscoveryConstants.ProtocolVersion)
        {
            rejectReason = "protocol 版本不符";
            return false;
        }

        if (!string.Equals(announcement.Type, DiscoveryConstants.AnnounceType, StringComparison.Ordinal))
        {
            rejectReason = "type 不是 announce";
            return false;
        }

        if (announcement.DeviceId == Guid.Empty)
        {
            rejectReason = "deviceId 为空 Guid";
            return false;
        }

        // 自公告去重：即使是组播/定向广播回环收到自己的包，也绝不能出现在设备列表里。
        if (announcement.DeviceId == local.LocalDeviceId)
        {
            rejectReason = "本机自公告";
            return false;
        }

        if (!IsConsistentDeviceCode(announcement))
        {
            rejectReason = "deviceCode 与 deviceId 不一致";
            return false;
        }

        string deviceName = announcement.DeviceName?.Trim() ?? string.Empty;
        if (deviceName.Length == 0)
        {
            rejectReason = "deviceName 为空";
            return false;
        }

        if (deviceName.Length > DiscoveryConstants.MaxDeviceNameLength)
        {
            rejectReason = "deviceName 过长";
            return false;
        }

        if (ContainsControlCharacter(deviceName))
        {
            rejectReason = "deviceName 含控制字符";
            return false;
        }

        if (announcement.TcpPort != DiscoveryConstants.ExpectedTransportPort)
        {
            rejectReason = "tcpPort 不是期望端口";
            return false;
        }

        if (!TryNormalizeFingerprint(announcement.CertSha256, out string fingerprint))
        {
            rejectReason = "certSha256 格式非法";
            return false;
        }

        if (!TryValidateNonce(announcement.Nonce))
        {
            rejectReason = "nonce 非法";
            return false;
        }

        if (!TryNormalizeCapabilities(announcement.Capabilities, out IReadOnlySet<string> capabilities))
        {
            rejectReason = "capabilities 非法";
            return false;
        }

        device = new DiscoveredDevice(
            announcement.DeviceId,
            DeviceCode.Derive(announcement.DeviceId),
            deviceName,
            sourceAddress,
            announcement.TcpPort,
            fingerprint,
            now,
            capabilities);

        return true;
    }

    private static bool IsConsistentDeviceCode(DiscoveryAnnouncement announcement)
    {
        string? declared = announcement.DeviceCode;

        if (string.IsNullOrWhiteSpace(declared))
        {
            return false;
        }

        if (!DeviceCode.IsWellFormed(declared))
        {
            return false;
        }

        // DeviceCode 是 deviceId 的纯函数，因此可以直接重算比对。
        return string.Equals(
            DeviceCode.Derive(announcement.DeviceId),
            declared.Trim().ToUpperInvariant(),
            StringComparison.Ordinal);
    }

    private static bool ContainsControlCharacter(string value)
    {
        foreach (char c in value)
        {
            if (char.IsControl(c))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryNormalizeFingerprint(string? raw, out string fingerprint)
    {
        fingerprint = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string trimmed = raw.Trim();

        if (trimmed.Length != 64)
        {
            return false;
        }

        foreach (char c in trimmed)
        {
            if (!IsHexDigit(c))
            {
                return false;
            }
        }

        fingerprint = trimmed.ToUpperInvariant();
        return true;
    }

    private static bool IsHexDigit(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static bool TryValidateNonce(string? nonce)
    {
        if (string.IsNullOrWhiteSpace(nonce))
        {
            return false;
        }

        byte[] buffer = new byte[DiscoveryConstants.NonceByteCount + 16];
        if (!Convert.TryFromBase64String(nonce, buffer, out int written))
        {
            return false;
        }

        return written == DiscoveryConstants.NonceByteCount;
    }

    private static bool TryNormalizeCapabilities(
        IReadOnlyList<string>? raw,
        out IReadOnlySet<string> capabilities)
    {
        capabilities = new HashSet<string>(StringComparer.Ordinal);

        if (raw is null)
        {
            return true;
        }

        HashSet<string> set = new(StringComparer.Ordinal);

        foreach (string entry in raw)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            string normalized = entry.Trim();

            if (normalized.Length > DiscoveryConstants.MaxCapabilityLength)
            {
                return false;
            }

            set.Add(normalized);

            if (set.Count > DiscoveryConstants.MaxCapabilities)
            {
                return false;
            }
        }

        capabilities = set;
        return true;
    }
}
