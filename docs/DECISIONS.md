# Architecture Decision Records

## ADR-001 — Windows-first
**Decision**：v1 只做 Windows 10 22H2 / Windows 11 x64。  
**Context**：远程屏幕捕获和输入控制高度依赖 OS API。跨平台会显著增加第三方 AI 施工失败概率。  
**Consequence**：接口保留跨平台可能，但不在 v1 实现。

## ADR-002 — .NET 10 LTS + WPF
**Decision**：使用 .NET 10 LTS 和 WPF。  
**Context**：成熟 Windows 桌面生态、SslStream、DPAPI、Win32 interop、异步网络能力充足。  
**Consequence**：UI 非跨平台。

## ADR-003 — v1 使用 TLS/TCP，而不是 QUIC
**Decision**：control/video 各用独立 TCP+TLS。  
**Context**：需要兼容 Windows 10；.NET QUIC 在 Windows 的平台要求更偏向 Windows 11/Server 2022+，而 TCP/TLS 可覆盖目标系统。两条连接能显著降低视频写入对输入控制的阻塞。  
**Consequence**：弱 Wi-Fi 丢包时视频延迟可能不如专用 UDP/QUIC，但 v1 更可靠易实现。

## ADR-004 — Access key 为 128-bit 随机 key
**Decision**：系统生成高熵 key，而不是 6 位密码。  
**Context**：认证 proof 可被网络攻击者观察；若密码弱会有离线字典风险。  
**Consequence**：密钥较长，但安全；UI 用分组和复制提高易用性。

## ADR-005 — HMAC challenge，不发送明文 key
**Decision**：TLS 内仍不直接发送访问 key，而发送绑定 transcript 的 HMAC proof，并验证 server proof。  
**Context**：局域网发现可被伪造；这样恶意伪造服务端拿不到 key 本身。  
**Consequence**：协议稍复杂，但仍只使用标准原语。

## ADR-006 — 同局域网定义为同 IPv4 子网
**Decision**：v1 仅接受与 listener 本地地址相同 subnet 的 RFC1918 IPv4。  
**Context**：“同一个路由器”无法可靠由应用判断；同 subnet 可明确验证。  
**Consequence**：跨 VLAN、路由型企业 LAN 默认不可连接，这是安全设计。

## ADR-007 — 视频先 JPEG/MJPEG
**Decision**：v1 先完成 JPEG frame streaming，编码器抽象可替换。  
**Context**：H.264 硬件编码/Media Foundation 增加大量 COM/GPU/驱动复杂度，会阻塞主体功能。LAN 下通过缩放/FPS/quality 可先达到自用可用性。  
**Consequence**：带宽高于 H.264；后续性能阶段可升级。

## ADR-008 — GDI capture 首发
**Decision**：v1 可用 BitBlt，后续 Windows.Graphics.Capture。  
**Context**：BitBlt 更容易一次施工正确。  
**Consequence**：高 FPS/4K 性能不是 v1 目标；secure desktop 不支持。

## ADR-009 — 无隐身远控
**Decision**：被查看/控制必须有本机指示和紧急断开。  
**Context**：减少误连接和隐私风险。  
**Consequence**：不提供 stealth 功能。

---

## 施工后新增决定（超出原规格中的 ADR-001 ~ ADR-009）

按 `06_DEV_STANDARDS.md` 第 11 节格式记录。

### ADR-010 — WPF 项目 TFM 使用 `net10.0-windows`
**日期**：2026-09-20  
**Decision**：WPF 相关项目使用 `net10.0-windows`，不使用 `net10.0-windows10.0.19041.0`。  
**Context**：`01_MASTER_PROMPT.md` 第二节允许「或等价可支持目标系统的 Windows TFM」。带平台版本号的 TFM 会强制解析 Windows SDK 投影包，本机未安装 Windows 10 SDK 时会直接构建失败；且 v1 代码不使用任何 WinRT / CsWinRT API。  
**Consequence**：M0~M10 可正常构建。若将来要引入 RealWinRT API（例如 `Windows.Graphics.Capture` 的部分路径），必须显式升级 TFM 并把本 ADR 标记为 superseded。  
**可逆性**：完全可逆。

### ADR-011 — `IAccessSecretStore` 采用异步签名
**日期**：2026-09-20  
**Decision**：把规格样例里的同步 `AccessSecret LoadOrCreate()` 调整为 `Task<AccessSecret> LoadOrCreateAsync(CancellationToken)`。  
**Context**：`06_DEV_STANDARDS.md` 第 2 节要求 I/O 全异步、禁用 `.Result` / `.Wait()`；而 DPAPI 读写必然涉及文件 I/O。  
**Consequence**：职责与命名未变，只是同步原语形式变化。M1 实现据此编写，后续不要改回同步版本，否则 UI 会退回阻塞 I/O。  
**可逆性**：可逆，但不建议。

### ADR-012 — `SingleInstanceGuard` 放在 Core 而非 App
**日期**：2026-09-20  
**Decision**：单实例守卫实现放在 `LanRemote.Core.Infrastructure`，App 层直接调用。  
**Context**：它只依赖 `System.Threading`；放在 Core 才能被单元测试覆盖，放在 WPF 可执行项目里则测试项目无法引用。  
**Consequence**：进程内语义由单元测试守护；跨进程语义已通过人工双实例验证（见 `HANDOFF.md`）。  
**可逆性**：完全可逆。

### ADR-013 — 文件日志先用最小自实现提供器
**日期**：2026-09-20  
**Decision**：M0 使用约 120 行的 `SimpleFileLoggerProvider`，不引入第三方日志框架。  
**Context**：`06_DEV_STANDARDS.md` 第 13 节要求不为几行代码引入大依赖；Serilog / NLog 会带来额外依赖面与许可考量。  
**Consequence**：**当前没有日志轮转**，只按天分文件且不删除历史文件。`09_HANDOFF_TEMPLATE.md` 对应的 M9 必须把它替换为「保留约 7 天」的正式实现（01_MASTER_PROMPT.md 第九节要求日志轮转）。  
**可逆性**：完全可逆，且预期在 M9 被替换。

### ADR-014 — deviceGuid 与证书私钥一并放进 DPAPI 保护的 `secrets.bin`
**日期**：2026-09-20  
**Decision**：deviceGuid、访问密钥、设备证书 PFX 全部放在同一个 DPAPI 保护的 `secrets.bin` bundle 里；`config.json` 只放普通开关。  
**Context**：`02_PRODUCT_SPEC.md` 第 9 节要求 secret 绝不写入 `config.json`。deviceGuid 虽然不是秘密，但它是系统生成的身份原料，放在可被人随手编辑的 `config.json` 里会引入「改配置即改身份」的意外路径。  
**Consequence**：轮换访问密钥只覆盖 `AccessKey` 字段，deviceGuid 与证书不受影响（已有测试守护）。删除 `secrets.bin` 等同于放弃本机身份，设备码会变。  
**可逆性**：可逆，但需要一次数据迁移。

### ADR-015 — 证书 PFX 使用随机口令导出，口令与 PFX 同处 DPAPI 信封内
**日期**：2026-09-20  
**Decision**：`cert.Export(X509ContentType.Pfx, randomPassword)`，口令（32 字节随机 Base64）与 PFX 一起写进 bundle。  
**Context**：PFX 导出 API 强制要求口令。既然整个文件已被 DPAPI 保护，额外套一层口令并不能增加机密性。  
**Consequence**：口令字段**不提供**额外安全强度，它只是 API 要求；任何人拿到 secrets.bin 但解不开 DPAPI 时，也拿不到口令。不要在文档或评审中误以为它是一层独立加密。  
**可逆性**：可逆（未来可改用 DER + PKCS#8 私钥分开存）。

### ADR-016 — 证书加载使用 `PersistKeySet | Exportable`  【❌ SUPERSEDED by ADR-018】
**日期**：2026-09-20　**作废**：2026-09-20（M1.1 审计）  
**⚠️ 本 ADR 已被 ADR-018 取代，其中的 flags 组合已被移除，请勿照此实现。**
**Decision**：从 PFX 还原证书时使用 `X509CertificateLoader.LoadPkcs12(pfx, password, PersistKeySet | Exportable)`。  
**Context**：Windows 上 SslStream 服务端使用「只存在于内存」的私钥时，历史上会出现凭据不被识别的问题。M3 需要这个证书直接用于 TLS 服务端。  
**Consequence**：私钥会写入用户的 CNG 密钥容器（进程退出后容器由 .NET 在 Dispose 时清理）。副作用可接受，但单元测试反复创建证书会在用户 profile 里留下容器。  
**可逆性**：可逆；若 M3 验证不需要 PersistKeySet，可去掉。

### ADR-017 — 设备码算法固定为 `Base32(SHA256(deviceGuid) 前 5 字节)`
**日期**：2026-09-20  
**Decision**：取 `SHA256(guid.ToByteArray())` 的前 5 字节编码为 8 个 Crockford Base32 字符，展示为 `XXXX-XXXX`。  
**Context**：`01_MASTER_PROMPT.md` 第 7.1 节的既定规则；5 字节 = 40 bit 恰好对应 8 个字符，无需 padding。  
**Consequence**：算法一旦改动，所有已存在的设备码都会变，等同于换身份。有测试固定了「同一 guid 稳定 / 不同 guid 不同 / 格式匹配正则」。  
**可逆性**：技术可逆，产品上不可接受。

### ADR-018 — 私钥导入改为 `EphemeralKeySet`（取代 ADR-016）
**日期**：2026-09-20（M1.1 审计）  
**Decision**：`DeviceCertificateService` 用 `X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.EphemeralKeySet)` 载入证书；**不使用** `PersistKeySet`，**不使用** `Exportable`。  
**Context**：ADR-016 当初为了「M3 做 TLS 服务端时 SslStream 能拿到私钥」而选了 `PersistKeySet | Exportable`，理由是传闻中 ephemeral 私钥在 Windows SslStream 上会失败。审计发现这条理由是**未经实测的猜测**，而代价很实在：私钥被额外持久化到用户的 CNG 密钥容器，等于在 DPAPI 保护的 `secrets.bin` 之外多留一份持久化副本，还把私钥标记为可导出。这与「持久化副本只有 secrets.bin」的安全目标冲突。  
**Consequence**：
- 磁盘上的私钥副本只剩 DPAPI 保护的 `secrets.bin`；
- 进程运行期间私钥仅在内存，窗口关闭即消失；
- 私钥不可导出（需要导出时应重新走一遍「生成 → 导出 → 存入 bundle」流程）；
- **风险未关闭**：Windows 上 SslStream 服务端使用 ephemeral 私钥是否可靠，M1/M1.1 阶段**没有真实 TLS 测试可证明**。因此 ADR-018 附带一条强制要求：**M3 必须新增真实 SslStream server/client 握手集成测试**，用实测结果决定是否维持 EphemeralKeySet。在拿到该实测结果之前，**不得**凭猜测改回 PersistKeySet。  
**可逆性**：完全可逆（改一行常量），但方向受 M3 实测结果约束。  
**验证**：`DeviceCertificateTests.ReloadedCertificateCanSign`（重载后签名 + 公钥验签通过）、`ImportFlags_UsesEphemeralKeySetOnly`（防止把 flags 加回来）。

### ADR-019 — `DpapiSecretVault.UpdateAsync` 采用 copy-on-write
**日期**：2026-09-20（M1.1 审计）  
**Decision**：`UpdateAsync` 严格按「读缓存 → 克隆 working copy → operation 只改 working copy → **先落盘成功** → 再替换缓存」的顺序执行。  
**Context**：原实现把缓存里的 `SecretBundle` 直接交给 operation 修改，修改立即反映到内存，之后才落盘。一旦落盘因取消 / `IOException` / `UnauthorizedAccessException` / DPAPI 失败而抛异常，就会出现「内存是新密钥、磁盘还是旧密钥」的撕裂状态——UI 会告诉用户「已轮换」，但磁盘里仍是旧值，重启后打回原形。  
**Consequence**：任何失败路径下磁盘与内存**同时**保持旧状态。`SecretBundle` 新增 `Clone()` 深拷贝；`PersistCoreAsync` 在动手写文件之前先 `ThrowIfCancellationRequested()`，让取消行为确定而非竞态。  
**可逆性**：不可倒退——这是正确性修复。  
**验证**：`DpapiSecretVaultTransactionTests`（取消后同 vault 与全新 vault 都仍是旧 key；rotation 失败路径同样适用；无 `.tmp` 残留）。

### ADR-021 — ECDSA 证书的 Key Usage 只允许 `digitalSignature`
**日期**：2026-09-20（M1.3）  
**Decision**：设备证书的 KeyUsage 扩展为 `new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true)`，不再声明 `keyEncipherment`，也不声明 `keyAgreement`。  
**Context**：证书是 ECDSA / P-256 / `id-ecPublicKey` 的 end-entity server certificate。RFC 5480 对 `id-ecPublicKey` 的 end-entity 证书允许的 Key Usage 只有 `digitalSignature` / `nonRepudiation` / `keyAgreement`；`keyEncipherment` 属于 RSA 密钥传输语义，不属于 EC certificate profile——在 EC 证书里声明它属于 profile 违规，严格校验方可能直接拒绝。LanRemote 的 TLS 服务端身份使用 ECDSA 签名，只需要 `digitalSignature`。  
**Consequence**：
- 只影响**新签发**的证书；已存在的证书不会被自动替换（这一条是硬约束，见下）；
- serverAuth EKU（`1.3.6.1.5.5.7.3.1`）保持不变；
- 由于当时本机开发身份是 M1.2 之前签发的（带 `keyEncipherment`），M1.3 对**开发机**做了一次显式备份后的手工重置。这是 pre-release 开发身份处理，**不是**升级迁移逻辑。  
**硬约束**：不得为了实现「自动把旧证书换成新 profile」而在 production code 中加入静默重签逻辑。已有证书只能由用户显式轮换。  
**可逆性**：可逆，但会把 profile 违规带回来，不建议。  
**验证**：`CertificateKeyUsage_IsDigitalSignatureOnly`、`CertificateProfile_MatchesEcdsaServerIdentity`、
`KeyUsageSurvivesReloadFromSecretsBin`；`CertificateDeclaresServerAuthenticationUsage` 确认 EKU 未破。

### ADR-020 — 证书 bundle 状态 fail closed + secrets.bin 严格校验
**日期**：2026-09-20（M1.1 审计）  
**Decision**：
1. `certificatePfx` 与 `certificatePfxPassword` **必须同时存在或同时缺失**；恰好只有一个时抛 `InvalidDataException`，**不自动生成新证书**；
2. `SecretFile.UnpackProtected` 要求实际 payload 长度**严格等于**声明长度（原来是 `<` 即放过）；
3. JSON 解密后额外校验 `bundle.Version == CurrentVersion`。  
**Context**：原来的写法会在「证书字段半损坏」时静默重新签发一张证书，导致证书指纹悄悄改变——而 M3 的证书 pinning 完全依赖指纹稳定，这种静默变化是最难排查的一类 bug。宽松的长度校验则会让被追加/拼接过的文件蒙混过关。  
**Consequence**：状态损坏会显式失败，用户会看到错误而不是「看起来正常但换了身份」。副作用是需要人工介入（M9 应提供「重置本机身份」入口，见 HANDOFF 已知问题）。  
**可逆性**：技术可逆，但会重新引入静默换证书的风险，不建议。  
**验证**：`DeviceCertificateTests` 的两个 partial-state 用例、`SecretFileFormatTests` 的追加字节 / 截断 / 版本不一致用例。

