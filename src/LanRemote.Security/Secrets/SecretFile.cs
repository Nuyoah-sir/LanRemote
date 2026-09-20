using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanRemote.Security.Secrets;

/// <summary>
/// 本机秘密在内存中的形态。
/// </summary>
/// <remarks>
/// <para>这个文件可能会被序列化进 <c>secrets.bin</c>，但<b>整个文件都由 DPAPI(CurrentUser) 保护</b>，
/// 并且<b>永远不会被写入 <c>config.json</c></b>（02_PRODUCT_SPEC.md 第 9 节）。</para>
/// <para>为降低拿到 secret 后被误改动的风险，所有写入都必须通过
/// <see cref="DpapiSecretVault.UpdateAsync{TResult}"/> 进行；读取通过
/// <see cref="DpapiSecretVault.ReadAsync{TResult}"/> 的投影函数，外部拿不到可变实例。</para>
/// <para>异常消息与日志里不得包含本类型的任何字段值。</para>
/// </remarks>
public sealed class SecretBundle
{
    /// <summary>bundle 格式版本。</summary>
    public int Version { get; set; } = SecretFile.CurrentVersion;

    /// <summary>本设备稳定标识，首次启动时随机生成，之后永不改变。</summary>
    public Guid DeviceGuid { get; set; }

    /// <summary>访问密钥的 Crockford Base32 形式（26 个字符，不含分隔符）。</summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>设备证书 PFX 的 Base64（含私钥）。</summary>
    public string? CertificatePfx { get; set; }

    /// <summary>
    /// 导出 PFX 时使用的随机口令的 Base64。
    /// </summary>
    /// <remarks>
    /// 它<b>不提供额外的机密性</b>——真正保护数据的是外层的 DPAPI。
    /// 之所以存在，是因为 PFX 导出 API 强制要求一个口令。它与 PFX 一起被封在同一个 DPAPI 信封里。
    /// </remarks>
    public string? CertificatePfxPassword { get; set; }

    /// <summary>
    /// 创建一份独立的深拷贝。
    /// </summary>
    /// <returns>与当前实例内容相同但互不影响的新实例。</returns>
    /// <remarks>
    /// <c>DpapiSecretVault.UpdateAsync</c> 的 copy-on-write 依赖它：
    /// 修改只发生在 working copy 上，只有在落盘成功后才会替换 vault 的缓存实例，
    /// 从而杜绝「内存是新值、磁盘是旧值」的撕裂状态（M1.1）。
    /// </remarks>
    public SecretBundle Clone() => new()
    {
        Version = Version,
        DeviceGuid = DeviceGuid,
        AccessKey = AccessKey,
        CertificatePfx = CertificatePfx,
        CertificatePfxPassword = CertificatePfxPassword,
    };
}

/// <summary>
/// <c>secrets.bin</c> 的二进制格式读写。
/// </summary>
/// <remarks>
/// 文件布局：
/// <code>
/// [0..3]   magic     = "LRSC"
/// [4]      version   = 1
/// [5..8]   length    = 后续 payload 字节数（大端 uint32）
/// [9..]    payload   = DPAPI(UTF-8 JSON(SecretBundle))
/// </code>
/// 先用 magic + version + length 做廉价校验，再做 DPAPI 解密，避免把任意大文件喂进阶密 API。
/// </remarks>
public static class SecretFile
{
    /// <summary>魔数 "LRSC"。</summary>
    public const string Magic = "LRSC";

    /// <summary>当前格式版本。</summary>
    public const int CurrentVersion = 1;

    /// <summary>头部长度（字节）。</summary>
    public const int HeaderBytes = 9;

    /// <summary>payload 上限，8 MiB。远超实际需要，用于拒绝被篡改的文件。</summary>
    public const int MaxPayloadBytes = 8 * 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>把 bundle 序列化并施加 DPAPI 保护。</summary>
    /// <param name="bundle">秘密集合。</param>
    /// <returns>可直接写入文件的字节。</returns>
    public static byte[] PackProtected(SecretBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        bundle.Version = CurrentVersion;

        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(bundle, SerializerOptions);
        try
        {
            byte[] protectedPayload = DataProtection.Protect(plaintext);

            if (protectedPayload.Length is <= 0 or > MaxPayloadBytes)
            {
                throw new InvalidOperationException("DPAPI 保护后的 payload 长度异常。");
            }

            byte[] file = new byte[HeaderBytes + protectedPayload.Length];
            System.Text.Encoding.ASCII.GetBytes(Magic).CopyTo(file, 0);
            file[4] = CurrentVersion;
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(5, 4), (uint)protectedPayload.Length);
            protectedPayload.CopyTo(file, HeaderBytes);

            return file;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>解析并解密 <c>secrets.bin</c> 内容。</summary>
    /// <param name="fileContent">文件全部字节。</param>
    /// <returns>还原出的 bundle。</returns>
    /// <exception cref="InvalidDataException">文件不是 LanRemote 秘密文件、版本不受支持或已损坏。</exception>
    public static SecretBundle UnpackProtected(byte[] fileContent)
    {
        ArgumentNullException.ThrowIfNull(fileContent);

        if (fileContent.Length < HeaderBytes)
        {
            throw new InvalidDataException("秘密文件过短，不是有效的 secrets.bin。");
        }

        if (!fileContent.AsSpan(0, 4).SequenceEqual(System.Text.Encoding.ASCII.GetBytes(Magic)))
        {
            throw new InvalidDataException("秘密文件魔数不匹配。");
        }

        byte version = fileContent[4];
        if (version != CurrentVersion)
        {
            throw new InvalidDataException($"不支持的秘密文件版本：{version}。");
        }

        uint declaredLength = BinaryPrimitives.ReadUInt32BigEndian(fileContent.AsSpan(5, 4));
        if (declaredLength == 0 || declaredLength > MaxPayloadBytes)
        {
            throw new InvalidDataException("秘密文件声明的 payload 长度非法。");
        }

        // 必须严格相等：多出来的字节意味着文件被人追加/拼接过，少一个字节则是截断。
        // 用「<= 加尾部忽略」会让被篡改的文件蒙混过关（M1.1）。
        int actualLength = fileContent.Length - HeaderBytes;
        if (actualLength != declaredLength)
        {
            throw new InvalidDataException(
                $"秘密文件长度与声明不一致：声明={declaredLength}，实际={actualLength}。");
        }

        byte[] plaintext = DataProtection.Unprotect(fileContent, HeaderBytes, (int)declaredLength);
        try
        {
            SecretBundle? bundle = JsonSerializer.Deserialize<SecretBundle>(plaintext, SerializerOptions)
                ?? throw new InvalidDataException("秘密文件内容为空。");

            // 头部 version 与 payload 里的 version 都必须匹配当前实现支持的值。
            if (bundle.Version != CurrentVersion)
            {
                throw new InvalidDataException(
                    $"秘密文件内部的 bundle 版本不受支持：{bundle.Version}。");
            }

            return bundle;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("秘密文件内容无法解析。", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
