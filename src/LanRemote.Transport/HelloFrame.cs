using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanRemote.Transport;

/// <summary>
/// 控制通道的首帧 `channel_hello` —— M3 里<b>唯一</b>允许出现在未认证阶段的应用消息。
/// </summary>
/// <remarks>
/// <para><b>为什么手写一套严格解析，而不是"能反序列化成功就行"</b>：
/// 这帧是<b>认证之前</b>唯一被处理的输入。任何宽松（大小写不敏感、允许未知字段、
/// 允许重复字段、接受注释/尾逗号、非法 UTF-8 用替换字符兜底）都等于给对端一个
/// 我们无法用测试穷举的解析面。所以这里把每一项都写死，并且<b>逐项有测试</b>。</para>
/// <para><b>拒绝原因只用于本地日志</b>：这些字符串<b>绝不下发给对端</b>。
/// 把"你的 protocol 字段错了"发回去，等于送对方一个免费的探测探针。</para>
/// <para>解析成功后本帧的语义为空——三个字段都被固定成常量，没有信息量可传出。
/// M5 若要支持 video 通道，届时再扩展返回值，M3 只放行 <c>control</c>。</para>
/// </remarks>
public static class HelloFrame
{
    /// <summary>期望的 <c>type</c>。</summary>
    public const string ExpectedType = "channel_hello";

    /// <summary>期望的 <c>channel</c>（M3 只放行控制通道）。</summary>
    public const string ExpectedChannel = "control";

    /// <summary>期望的 <c>protocol</c> 版本号。</summary>
    public const int ExpectedProtocol = 1;

    /// <summary>不是合法 JSON / 编码非法 / 结构超深。</summary>
    public const string RejectMalformedJson = "hello-malformed-json";

    /// <summary>JSON 之后还有多余内容。</summary>
    public const string RejectTrailingData = "hello-trailing-data";

    /// <summary>缺字段（三个字段都是必填）。</summary>
    public const string RejectMissingField = "hello-missing-field";

    /// <summary><c>type</c> 不对（大小写也必须完全一致）。</summary>
    public const string RejectWrongType = "hello-wrong-type";

    /// <summary><c>channel</c> 不对。</summary>
    public const string RejectWrongChannel = "hello-wrong-channel";

    /// <summary><c>protocol</c> 不对。</summary>
    public const string RejectWrongProtocol = "hello-wrong-protocol";

    /// <summary>JSON 结构深度上限；求和用得上，hello 只有一层标量。</summary>
    public const int MaxJsonDepth = 4;

    private static readonly JsonSerializerOptions StrictOptions = new()
    {
        // 重复字段必须报错：默认的「后者覆盖前者」会让「两个 type」变成两种解释。
        AllowDuplicateProperties = false,
        // 未知字段一律拒绝，不做"宽容忽略"。
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        // 显式写死：属性值默认读出 0（= 内置 64），不写就等于没有主张。
        MaxDepth = MaxJsonDepth,
        // 大小写不敏感等于把 "Channel" 也算进来，属于凭空扩大解析面。
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    /// <summary>
    /// 解析并校验一个 hello 载荷。
    /// </summary>
    /// <param name="utf8">帧载荷（不含长度前缀），应当是 UTF-8 JSON。</param>
    /// <param name="rejection">通过时为 <see langword="null"/>；否则是只用于本地日志的短码。</param>
    /// <returns>是否是<b>且仅是</b> <c>{type:"channel_hello", channel:"control", protocol:1}</c>。</returns>
    public static bool TryParse(ReadOnlySpan<byte> utf8, out string? rejection)
    {
        rejection = null;

        if (utf8.IsEmpty)
        {
            rejection = RejectMalformedJson;
            return false;
        }

        // ① 先看这坨字节是不是「恰好一个 JSON 值」，把尾随内容挡在反序列化之前。
        if (!TryFindSingleJsonValueEnd(utf8, out long consumed, out bool malformed))
        {
            rejection = malformed ? RejectMalformedJson : RejectTrailingData;
            return false;
        }

        // ② 再做严格反序列化。非法 UTF-8 会在这里抛——不做替换字符兜底。
        HelloPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<HelloPayload>(utf8[..(int)consumed], StrictOptions);
        }
        catch (JsonException)
        {
            rejection = RejectMalformedJson;
            return false;
        }

        if (payload is null)
        {
            rejection = RejectMalformedJson;
            return false;
        }

        // ③ 三个字段都必须存在且精确相等。
        if (payload.Type is null || payload.Channel is null || payload.Protocol is null)
        {
            rejection = RejectMissingField;
            return false;
        }

        if (!string.Equals(payload.Type, ExpectedType, StringComparison.Ordinal))
        {
            rejection = RejectWrongType;
            return false;
        }

        if (!string.Equals(payload.Channel, ExpectedChannel, StringComparison.Ordinal))
        {
            rejection = RejectWrongChannel;
            return false;
        }

        if (payload.Protocol.Value != ExpectedProtocol)
        {
            rejection = RejectWrongProtocol;
            return false;
        }

        return true;
    }

    /// <summary>
    /// 序列化一个合法的 hello 帧载荷（客户端侧用）。
    /// </summary>
    /// <returns>UTF-8 JSON，不带长度前缀。</returns>
    public static byte[] Serialize()
    {
        return JsonSerializer.SerializeToUtf8Bytes(
            new HelloPayload
            {
                Type = ExpectedType,
                Channel = ExpectedChannel,
                Protocol = ExpectedProtocol,
            },
            StrictOptions);
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
                MaxDepth = MaxJsonDepth,
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

    /// <summary>
    /// hello 的反序列化载体。字段全是可空，用来把「缺字段」和「值不对」分开报。
    /// </summary>
    private sealed class HelloPayload
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("channel")]
        public string? Channel { get; set; }

        [JsonPropertyName("protocol")]
        public int? Protocol { get; set; }
    }
}
