namespace LanRemote.Transport.Auth;

/// <summary>
/// canonical uuid 串的解析与判定（ADR-038 阶段 2 实现定案）：小写 <c>D</c> 格式（8-4-4-4-12）。
/// </summary>
/// <remarks>
/// <para>判定式与 <see cref="CanonicalBase64"/> 同构：解析后重新格式化必须与输入逐字符相等——
/// <see cref="Guid.TryParseExact(string, string, out Guid)"/> 的 <c>"D"</c> 格式本身容忍大写 hex，
/// round-trip 把它收窄到「小写」这一种 wire 表示，避免同一 uuid 出现多种字节形态。</para>
/// <para>注意：transcript 构造（<see cref="AuthTranscriptBuilder"/>）无论如何都会用
/// <c>ToString("D")</c> 重新格式化，因此本判定的价值在「wire 表示唯一化 + 早拒非规范输入」，
/// 而不是密码学正确性。</para>
/// </remarks>
public static class CanonicalGuid
{
    /// <summary>
    /// 尝试把小写 <c>D</c> 格式的 uuid 串解析为 <see cref="Guid"/>。
    /// </summary>
    /// <param name="value">待解析的串。</param>
    /// <param name="guid">解析结果；失败时为 <see cref="Guid.Empty"/>。</param>
    /// <returns>是否为 canonical uuid 串。</returns>
    public static bool TryParse(string? value, out Guid guid)
    {
        guid = default;

        if (value is null)
        {
            return false;
        }

        if (!Guid.TryParseExact(value, "D", out Guid parsed))
        {
            return false;
        }

        // round-trip：大写 hex、含花括号等写法在 "D" 解析成功后仍会在这里被拒。
        if (!string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal))
        {
            return false;
        }

        guid = parsed;
        return true;
    }
}
