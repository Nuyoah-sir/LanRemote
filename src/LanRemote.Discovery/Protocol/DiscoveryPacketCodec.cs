using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanRemote.Discovery.Protocol;

/// <summary>
/// 发现报文的 JSON 编解码。
/// </summary>
/// <remarks>
/// <para>只使用 <c>System.Text.Json</c>，不引入第三方 JSON 包；字段命名固定 camelCase。</para>
/// <para>任何反序列化失败都在这里被吞掉并返回 <see langword="false"/>：
/// 别人往 UDP 45872 发垃圾<b>绝不能</b>让 LanRemote 崩溃。</para>
/// </remarks>
public static class DiscoveryPacketCodec
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    /// <summary>生成一个新的随机 nonce（Base64）。</summary>
    /// <returns>Base64 形式的 12 字节随机数。</returns>
    /// <remarks>
    /// nonce 不是访问密钥，但仍然统一使用 <see cref="RandomNumberGenerator"/>；
    /// 全项目禁止 <see cref="Random"/>。
    /// </remarks>
    public static string NewNonce() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(DiscoveryConstants.NonceByteCount));

    /// <summary>nonce 是否为「合法 Base64 且解出恰好 12 字节」。</summary>
    /// <param name="nonce">待校验字符串。</param>
    /// <returns>是否合法。</returns>
    /// <remarks>协议 v1 固定 12 字节；长度不对的 nonce 一律视为伪造/损坏。</remarks>
    public static bool IsWellFormedNonce(string? nonce)
    {
        if (string.IsNullOrWhiteSpace(nonce))
        {
            return false;
        }

        byte[] buffer = new byte[DiscoveryConstants.NonceByteCount + 16];
        return Convert.TryFromBase64String(nonce, buffer, out int written)
            && written == DiscoveryConstants.NonceByteCount;
    }

    /// <summary>编码一条公告。</summary>
    /// <param name="announcement">公告。</param>
    /// <returns>UTF-8 字节。</returns>
    public static byte[] EncodeAnnouncement(DiscoveryAnnouncement announcement)
    {
        ArgumentNullException.ThrowIfNull(announcement);

        return JsonSerializer.SerializeToUtf8Bytes(announcement, SerializerOptions);
    }

    /// <summary>编码一条探测。</summary>
    /// <param name="probe">探测。</param>
    /// <returns>UTF-8 字节。</returns>
    public static byte[] EncodeProbe(DiscoveryProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        return JsonSerializer.SerializeToUtf8Bytes(probe, SerializerOptions);
    }

    /// <summary>
    /// 试解析公告。
    /// </summary>
    /// <param name="payload">原始字节。</param>
    /// <param name="announcement">解析结果；失败为 <see langword="null"/>。</param>
    /// <returns>是否为形态上可解析的 JSON 对象。</returns>
    /// <remarks>这里只负责「能不能解析出来」，字段是否合法由
    /// <see cref="DiscoveryAnnouncementEvaluator"/> 判断。</remarks>
    public static bool TryDecodeAnnouncement(ReadOnlySpan<byte> payload, out DiscoveryAnnouncement? announcement)
    {
        announcement = null;

        if (!IsPlausibleSize(payload.Length))
        {
            return false;
        }

        try
        {
            DiscoveryAnnouncement? parsed =
                JsonSerializer.Deserialize<DiscoveryAnnouncement>(payload, SerializerOptions);

            if (parsed is null)
            {
                return false;
            }

            announcement = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>试解析探测。</summary>
    /// <param name="payload">原始字节。</param>
    /// <param name="probe">解析结果；失败为 <see langword="null"/>。</param>
    /// <returns>是否解析成功。</returns>
    public static bool TryDecodeProbe(ReadOnlySpan<byte> payload, out DiscoveryProbe? probe)
    {
        probe = null;

        if (!IsPlausibleSize(payload.Length))
        {
            return false;
        }

        try
        {
            DiscoveryProbe? parsed = JsonSerializer.Deserialize<DiscoveryProbe>(payload, SerializerOptions);

            if (parsed is null)
            {
                return false;
            }

            probe = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// 只读出报文的 type 字段，用于分派，避免每个报文解两次完整对象。
    /// </summary>
    /// <param name="payload">原始字节。</param>
    /// <param name="type">type 值；无法读出时为 <see langword="null"/>。</param>
    /// <returns>是否读出。</returns>
    public static bool TryPeekType(ReadOnlySpan<byte> payload, out string? type)
    {
        type = null;

        if (!IsPlausibleSize(payload.Length))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload.ToArray());

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("type", out JsonElement typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            type = typeElement.GetString();
            return type is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsPlausibleSize(int length) =>
        length > 0 && length <= DiscoveryConstants.MaxAnnouncementBytes;
}
