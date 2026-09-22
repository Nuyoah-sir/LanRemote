namespace LanRemote.Transport.Auth;

/// <summary>
/// canonical base64 的解码与判定（ADR-038 阶段 2 实现定案）。
/// </summary>
/// <remarks>
/// <para><b>canonical 的判定式 = round-trip 逐字符相等</b>：先宽松解码，再重新编码，
/// 与输入不完全一致即拒绝。这一条同时覆盖「缺 padding / 多余 padding / 空白字符 /
/// 非规范尾部位（如 <c>"AB=="</c>）/ 非法字符」全部非规范形态——
/// 比逐字符法更难写错，也与「两端必须字节一致」的合同直接对应。</para>
/// <para><c>Convert.FromBase64String</c> 会忽略空白，不能直接当判定器；本类是其严格包裹。</para>
/// <para>空串与 <see langword="null"/> 一律拒绝：协议中不存在空的 base64 字段，
/// 早拒一层可作防御纵深（字段尺寸校验之外的第二道）。</para>
/// </remarks>
public static class CanonicalBase64
{
    /// <summary>
    /// 尝试把 canonical base64 串解码为字节。
    /// </summary>
    /// <param name="value">待解码的串（必须是 canonical 形式）。</param>
    /// <param name="bytes">解码结果；失败时为 <see cref="Array.Empty{T}"/>。</param>
    /// <returns>是否为 canonical base64。</returns>
    public static bool TryDecode(string? value, out byte[] bytes)
    {
        bytes = [];

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        // 长度非法（非 4 的倍数）时 TryFromBase64String 自会失败；
        // 含空白时它会静默忽略——后者由下面的 round-trip 拒绝。
        byte[] buffer = new byte[(value.Length / 4 + 1) * 3];
        if (!Convert.TryFromBase64String(value, buffer, out int written))
        {
            return false;
        }

        ReadOnlySpan<byte> decoded = buffer.AsSpan(0, written);

        // round-trip：非规范输入（空白、缺/多 padding、非规范尾部位等）不可能与原串相等。
        if (!string.Equals(Convert.ToBase64String(decoded), value, StringComparison.Ordinal))
        {
            return false;
        }

        bytes = decoded.ToArray();
        return true;
    }
}
