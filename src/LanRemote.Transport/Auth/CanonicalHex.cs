namespace LanRemote.Transport.Auth;

/// <summary>
/// canonical hex 的解码与判定（ADR-038 阶段 2 实现定案）：仅大写 <c>[0-9A-F]</c>、偶数长度。
/// </summary>
/// <remarks>
/// <para>判定式与 <see cref="CanonicalBase64"/> 同构：decode 后 re-encode 必须与输入逐字符相等——
/// <c>Convert.FromHexString</c> 容忍小写，round-trip 会把它拒掉。</para>
/// <para>空串与 <see langword="null"/> 一律拒绝（协议中不存在空的 hex 字段）。</para>
/// </remarks>
public static class CanonicalHex
{
    /// <summary>
    /// 尝试把 canonical（大写）hex 串解码为字节。
    /// </summary>
    /// <param name="value">待解码的串（必须是大写 hex）。</param>
    /// <param name="bytes">解码结果；失败时为 <see cref="Array.Empty{T}"/>。</param>
    /// <returns>是否为 canonical hex。</returns>
    public static bool TryDecode(string? value, out byte[] bytes)
    {
        bytes = [];

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromHexString(value);
        }
        catch (FormatException)
        {
            // 奇数长度 / 非 hex 字符。
            return false;
        }

        // round-trip：小写、大小写混写都会在这里被拒。
        if (!string.Equals(value, Convert.ToHexString(decoded), StringComparison.Ordinal))
        {
            return false;
        }

        bytes = decoded;
        return true;
    }
}
