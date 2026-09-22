using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanRemote.Transport.Auth;

/// <summary>
/// 认证帧共用的严格 JSON 骨架：先确认「整段字节恰好是一个 JSON 值」，再用严格选项反序列化。
/// </summary>
/// <remarks>
/// <para>直接对应 <c>HelloFrame</c> 的解析哲学（重复字段 / 未知字段 / 大小写 / 注释 / 尾逗号 /
/// 尾随内容全部拒绝）——认证帧全部出现在<b>未认证阶段</b>，是攻击面最敏感的输入。</para>
/// <para><b>为什么不直接复用 <c>HelloFrame</c> 的私有实现</b>：它在 M3 已冻结（44 条测试锁定），
/// 本次刻意不动它；两边各自维护一份同款骨架是有意识的取舍（规模小、行为各自被测试锁定）。
/// 若将来出现第三、四个消费方，再做统一重构。</para>
/// <para>拒绝短码只用于本地日志，绝不下发对端（同 <c>HelloFrame</c> 的纪律）。</para>
/// </remarks>
internal static class AuthJson
{
    /// <summary>JSON 结构深度上限：各认证帧都是扁平对象（一层标量）。</summary>
    internal const int MaxDepth = 4;

    /// <summary>
    /// 共享的严格反序列化选项。每一项都是「写死的拒绝面」，不做宽容忽略。
    /// </summary>
    internal static readonly JsonSerializerOptions StrictOptions = new()
    {
        // 重复字段必须报错：默认的「后者覆盖前者」会让「两个 type」变成两种解释。
        AllowDuplicateProperties = false,
        // 未知字段一律拒绝，不做"宽容忽略"。
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        // 显式写死：属性值默认读出 0（= 内置 64），不写就等于没有主张。
        MaxDepth = MaxDepth,
        // 大小写不敏感等于把 "Type" 也算进来，属于凭空扩大解析面。
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    /// <summary>
    /// 解析一个认证帧载荷：必须是「恰好一个 JSON 值 + 仅尾随空白」。
    /// </summary>
    /// <typeparam name="T">私有载荷类型（字段全可空，用来把「缺字段」和「值不对」分开报）。</typeparam>
    /// <param name="utf8">帧载荷（不含长度前缀）。</param>
    /// <param name="payload">反序列化结果；失败时为 <see langword="null"/>。</param>
    /// <param name="rejection">失败短码（调用方传入本帧的码）。</param>
    /// <param name="malformedCode">「不是合法 JSON / 编码非法 / 结构超深」的短码。</param>
    /// <param name="trailingCode">「JSON 之后还有多余内容」的短码。</param>
    /// <param name="expectedType">本帧期望的 <c>type</c>；<see langword="null"/> 表示跳过类型提示。</param>
    /// <param name="wrongTypeCode"><c>type</c> 明确是别的帧时使用的短码；缺省回退到 <paramref name="malformedCode"/>。</param>
    /// <returns>是否反序列化成功。</returns>
    /// <remarks>
    /// 判定顺序：① 结构（单值 / 尾随 / 非法 UTF-8）→ ② 类型提示 → ③ 严格反序列化。
    /// 「类型提示」只在结构合法时生效，且<b>不影响接受与否</b>——它只让「合法的别的帧」
    /// 报 <paramref name="wrongTypeCode"/> 而不是因未知成员被报成 malformed。
    /// 载荷级 <c>type</c> 的权威判定仍由各帧自己的检查完成。
    /// </remarks>
    internal static bool TryDeserializeStrict<T>(
        ReadOnlySpan<byte> utf8,
        [NotNullWhen(true)] out T? payload,
        out string? rejection,
        string malformedCode,
        string trailingCode,
        string? expectedType = null,
        string? wrongTypeCode = null)
        where T : class
    {
        payload = null;
        rejection = null;

        if (utf8.IsEmpty)
        {
            rejection = malformedCode;
            return false;
        }

        // ① 先看这坨字节是不是「恰好一个 JSON 值」，把尾随内容挡在反序列化之前。
        if (!TryFindSingleJsonValueEnd(utf8, out long consumed, out bool malformed))
        {
            rejection = malformed ? malformedCode : trailingCode;
            return false;
        }

        // ② 类型提示：结构合法、但 type 明确是别的帧 → 直接报 wrong-type。
        if (expectedType is not null
            && TryReadTypeHint(utf8[..(int)consumed], out string? hintedType)
            && hintedType is not null
            && !string.Equals(hintedType, expectedType, StringComparison.Ordinal))
        {
            rejection = wrongTypeCode ?? malformedCode;
            return false;
        }

        // ③ 再做严格反序列化。非法 UTF-8 会在这里抛——不做替换字符兜底。
        try
        {
            payload = JsonSerializer.Deserialize<T>(utf8[..(int)consumed], StrictOptions);
        }
        catch (JsonException)
        {
            rejection = malformedCode;
            return false;
        }

        if (payload is null)
        {
            rejection = malformedCode;
            return false;
        }

        return true;
    }

    /// <summary>
    /// 只读根对象第一层的 <c>type</c> 字符串值（拒绝短码用的类型提示）。
    /// </summary>
    /// <remarks>
    /// <para>存在的理由：不同帧的载荷互相喂错时（阶段 3/4 联调必然发生），严格反序列化
    /// 会因「未知成员」报 malformed——日志上看不出「其实是一个结构合法的别的帧」。</para>
    /// <para>本提示只报告<b>第一个</b>字符串类型的 <c>type</c> 值；重复字段 / 转义写法 /
    /// 非字符串类型一律交还主路径裁决（主路径才是唯一权威）。嵌套结构里的 <c>type</c>
    /// 不会被看到（只扫根对象第一层）。</para>
    /// </remarks>
    private static bool TryReadTypeHint(ReadOnlySpan<byte> utf8, out string? type)
    {
        type = null;

        Utf8JsonReader reader = new(
            utf8,
            new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                MaxDepth = MaxDepth,
            });

        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    return true;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    // 预扫描已确认结构，这里理论上到不了；保守放弃提示。
                    return false;
                }

                bool isTypeProperty = reader.ValueTextEquals(AuthProtocol.FieldType);

                if (!reader.Read())
                {
                    return false;
                }

                if (isTypeProperty && reader.TokenType == JsonTokenType.String)
                {
                    type = reader.GetString();
                    return true;
                }

                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    if (!reader.TrySkip())
                    {
                        return false;
                    }
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // 实测事实（2026-09-22）：Utf8JsonReader.GetString() 遇到非法 UTF-8 抛的是
            // InvalidOperationException（内层 DecoderFallbackException）——不是 JsonException；
            // 而 JsonSerializer.Deserialize 会把它包成 JsonException。
            // 此处「提示失败 = 放弃提示」，交主路径裁决（主路径 → malformed）。
            return false;
        }
    }

    /// <summary>
    /// 确认整段字节<b>恰好</b>是一个 JSON 值：后面只允许空白。
    /// </summary>
    /// <param name="utf8">输入。</param>
    /// <param name="consumed">第一个 JSON 值消耗的字节数。</param>
    /// <param name="malformed">是不是「连一个 JSON 值都读不出来」。</param>
    /// <returns>是否可以继续反序列化。</returns>
    private static bool TryFindSingleJsonValueEnd(
        ReadOnlySpan<byte> utf8,
        out long consumed,
        out bool malformed)
    {
        consumed = 0;
        malformed = true;

        Utf8JsonReader reader = new(
            utf8,
            new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                MaxDepth = MaxDepth,
            });

        try
        {
            if (!reader.Read())
            {
                return false;
            }

            if (!reader.TrySkip())
            {
                return false;
            }

            consumed = reader.BytesConsumed;
        }
        catch (JsonException)
        {
            return false;
        }

        for (int i = (int)consumed; i < utf8.Length; i++)
        {
            if (!IsJsonWhitespace(utf8[i]))
            {
                malformed = false;
                return false;
            }
        }

        malformed = false;
        return true;
    }

    private static bool IsJsonWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
}
