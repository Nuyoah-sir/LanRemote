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

### ADR-016 — 证书加载使用 `PersistKeySet | Exportable`  【❌ SUPERSEDED by ADR-018 → 最终由 ADR-029 取代】
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

### ADR-022 — Discovery transport：IPv4 组播 + directed broadcast，无云端
**日期**：2026-09-20（M2）  
**Decision**：
1. 只处理 IPv4；
2. 发现用 UDP 组播 `239.255.77.77:45872` TTL=1，外加每张合格网卡的 directed broadcast probe 兜底；
3. **没有任何**云端注册、mDNS 中继、STUN/TURN、WebSocket、UPnP、NAT-PMP、PCP、端口映射、公网 API；
4. **source IP 权威**：远端地址一律取 UDP source endpoint，announcement payload 里就没有地址字段；
5. **报文一律不可信**：先看来源（IPv4 + RFC1918 + 同子网），通过后才进 JSON parser；单报文上限 2048 字节；
6. 发现包**不是**认证依据，只用于列表展示与后续证书 pinning。  
**Context**：`04_PROTOCOL_AND_SECURITY.md` 第 5 节与 M2 要求第二十五节。产品定位是纯局域网自用工具，任何公网依赖都会把威胁模型完全改变。  
**Consequence**：在完全断网、只剩一个交换机的环境也能工作；代价是跨网段/VPN 场景默认不可用（需另开实验设置，M2 不做）。  
**可逆性**：技术可逆，但与产品定位冲突，不建议。

### ADR-023 — `WatchAsync` 是 upsert-only，UI 自己按 LastSeen prune
**日期**：2026-09-20（M2）  
**Decision**：`IDiscoveryService.WatchAsync` 只推送「在线设备的插入或更新」，**不发送**任何「Removed」假设备。UI（MainViewModel）自己起一个 1 秒的清理循环，按 `LastSeen <= now - 7s` 从 `ObservableCollection` 删除。  
**Context**：现有 Core API 在 M0 就定为 `IAsyncEnumerable<DiscoveredDevice>`。M2 若临时改成 Added/Updated/Removed 事件模型，等于推翻已有契约；而设备离线本来就是「超时未再出现」，用 TTL 表达最自然。  
**Consequence**：任何新的消费者都必须自己实现 TTL prune，不能指望收到移除事件。缓存与更新队列本身仍有界（256 / 512 DropOldest）。  
**可逆性**：可逆，但需要一次独立的 ADR 与 API 变更，不要在后续里程碑里「顺手」改。

### ADR-018 — 证书加载使用 `EphemeralKeySet`（取代 ADR-016）　【❌ 2026-09-21 实测证伪，SUPERSEDED by ADR-029】
> **编号修正**：本条目原被误标为「ADR-021（ECDSA 证书的 Key Usage）」，导致 ADR-016 所声明的
> 「SUPERSEDED by ADR-018」指向一条不存在的记录。2026-09-20 核对后改回 **ADR-018**。
> 真正的 Key Usage 决策是下方另一条 **ADR-021**，两者内容不同，不要合并。
>
> **⚠️ 本 ADR 已被 2026-09-21 的实测证伪，请勿照此实现**：`EphemeralKeySet` 在 Windows 上
> 用作 **SslStream 服务端**证书时 **9/9 失败**，服务端抛
> `AuthenticationException: Authentication failed because the platform does not support ephemeral keys.`
> （inner `Win32Exception: 安全包中没有可用的凭证 / 0x8009030E`）。
> 替代方案见 **ADR-029**。本条目保留仅作历史追溯。

**日期**：2026-09-20（M1.1 审计）  
**Decision**：`DeviceCertificateService` 用 `X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.EphemeralKeySet)` 载入证书；**不使用** `PersistKeySet`，**不使用** `Exportable`。  
**Context**：ADR-016 当初为了「M3 做 TLS 服务端时 SslStream 能拿到私钥」而选了 `PersistKeySet | Exportable`，理由是传闻中 ephemeral 私钥在 Windows SslStream 上会失败。审计发现这条理由是**未经实测的猜测**，而代价很实在：私钥被额外持久化到用户的 CNG 密钥容器，等于在 DPAPI 保护的 `secrets.bin` 之外多留一份持久化副本，还把私钥标记为可导出。这与「持久化副本只有 secrets.bin」的安全目标冲突。  
**Consequence**：
- 磁盘上的私钥副本只剩 DPAPI 保护的 `secrets.bin`；
- 进程运行期间私钥仅在内存，窗口关闭即消失；
- 私钥不可导出（需要导出时应重新走一遍「生成 → 导出 → 存入 bundle」流程）；
- **风险未关闭**：Windows 上 SslStream 服务端使用 ephemeral 私钥是否可靠，M1/M1.1 阶段**没有真实 TLS 测试可证明**。因此**本 ADR** 附带一条强制要求：**M3 必须新增真实 SslStream server/client 握手集成测试**，用实测结果决定是否维持 EphemeralKeySet。在拿到该实测结果之前，**不得**凭猜测改回 PersistKeySet。  
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

---

## 产品形态决策（2026-09-20，M2 两机验收后由用户拍板）

> **后续约束（2026-09-24，ADR-045）**：用户重申的原始需求是沿用现网自动发现，不是让应用修改整机网络。下文保留历史讨论，不代表现行改网授权；ADR-026 的产品改 IP 计划已被取代，ADR-025/ADR-035 的网络修改默认落地已停止。确认、UAC、实验环境或可撤销均不构成豁免；只读诊断与主进程不提权原则保留。

用户原话：「**不应该要求用户安装了我们的软件还去 PowerShell 输指令；要管理员权限的话，叫用户使用管理员模式（或者打开 UAC 索取管理员权限）不就好啦？**」
并选中「**三个都做（含改 IP 一键）**」。以下三条把这句话落成可施工、可验收的约束。
**这三条只定规则与验收口径，当前不启动编码。**

### ADR-024 — 网络诊断必须进 UI，且必须「指名原因 + 指名网卡 + 给实际地址」

> **后续约束（2026-09-24，ADR-045）**：原因与现网信息的只读呈现、零提权及安全校验要求保留；「需要用户确认的操作入口」不再授权修改网络。下文地址文案是当时环境示例，不是当前唯一活跃接口结论；本次同步不新增产品 UI 或工具能力。

**日期**：2026-09-20（M2 两机验收后）  
**Decision**：
1. `LanDiscoveryService` 的「没有合格私有 IPv4 网卡」以及其他启动 / 运行失败原因，**必须呈现到 UI**，不能只写日志（当前的 `bindings.Count == 0` 只 log warning 后正常返回，UI 无从得知——这是既成事实的规格违反）。
2. UI 诊断必须给出**原因 + 网卡名 + 该网卡的实际地址**，至少覆盖 `05_UI_UX_SPEC.md` §8 的「未发现设备 / 防火墙阻止 / 网络断开」。文案必须是可判定的具体句子，本机实测原句作为基准：
   「没有找到任何合格的私有 IPv4 网卡：以太网 = 172.100.166.220。RFC1918 私有网段只有 10/8、172.16–172.31、192.168/16；172.100.x.x 属于公网地址段。」
3. 「发现失败」要能分因，不能只用一个 bool：至少要区分 **0 张合格网卡** / **有网卡但无人回应** / **有回应但被 RFC1918 或同子网校验丢弃**，后两类必须可计数可查。
4. 诊断区**只负责说明原因并提供需要用户确认的操作入口**，自身不得包含自动改系统配置的「智能修复」。

**Context**：验收当天本机跑 discovery 输出「没有找到任何合格的私有 IPv4 网卡」，而 UI 状态栏仍显示「已从磁盘加载配置与本机身份」，用户完全无法判断为什么搜不到设备——直接违反 `05_UI_UX_SPEC.md` §8。用户同时明确表示不接受「装了软件还要自己开 PowerShell」。
**Consequence**：需要新增诊断视图模型 / 面板，以及「原因枚举 → 文案」映射；`LanDiscoveryService` 需要把失败原因结构化暴露。**不得**为了 UI 好写而放宽 RFC1918 / 同子网校验（那两条是不可动摇的安全机制）。
**硬约束**：诊断结论不得伪造——没有测到的维度就写「未知 / 未检测」，不许猜。
**可逆性**：完全可逆。
**实施时机**：**M9（可靠性与 UX）**，最晚不晚于 M10 发布。M3~M8 不得顺手实现，也不要因为它去改 discovery 逻辑。

**增补（2026-09-21，首启 UX 评审分流；依据 `docs/UX_FIRST_RUN_REVIEW_TRIAGE.md`。以下为增补段内独立编号）**：

1. **零提权**：诊断 / 扫描 / 状态呈现必须在非提升进程内完成，不得为诊断申请提权。
   （既成事实：M3 验收器双击开窗与 `--headless info` 均为零提权。）
2. **三就绪拆分**：本机网络就绪（存在合格私有 IPv4）/ 可达性就绪（防火墙规则就位且与当前 exe 绑定）/
   信任就绪（身份与密钥可用）必须**分别**呈现；禁止用一个总 `Ready` 概括全部——
   它会被读成「一切都行，包括能连上对端」。
3. **文案禁令与前提**：不得写「运行就能看到所有在线设备」。只能表述为
   「满足 <条件> 时能看到运行中的 LanRemote 设备」，条件至少含：对端在运行、双方同子网且均为私有地址、
   防火墙已放行、网络允许同子网互访（如无 AP 隔离）。条件不满足时必须说清**是哪一条**不满足。
4. **可达性 ≠ 允许被控**：防火墙放行只影响发现与连接可达性；被控仍必须经过访问密钥认证与本机审批。
   任何文案不得把「已允许访问」表述成「可被控制」。
5. **发现前分类**：进入发现流程前先给出本机分类（Ready / Action needed（含理由）/ Unsupported）；
   健康时零摩擦（不占版面）。

### ADR-025 — 防火墙放行内置到 App（UAC 提权），`configure-firewall.ps1` 降级为可选入口

> **后续约束（2026-09-24，ADR-045）**：本段及增补中的防火墙添加、修复、移除与提权入口仅为历史计划，网络修改的默认落地已停止，不能按 M10 原计划自动恢复实施。当前不修改防火墙，显式点击、管理员权限或 UAC 也不构成豁免；旧脚本不构成替代授权。历史正文保留，不是当前操作指引。

**日期**：2026-09-20（M2 两机验收后）  
**Decision**：
1. 防火墙放行做成 **App 内一键按钮**（例如「允许局域网访问」）。权限不足时由 App 自己触发 UAC（manifest `requireAdministrator` 或以 `runas` 重启自身），用户点一下即可；**不需要**用户打开 PowerShell，**不需要**用户去找脚本路径。
2. 规则约束沿用 M10 DoD：**只放行 LocalSubnet**；端口覆盖 discovery UDP 45872 与 TLS/TCP 45873（Control 与 Video 是两条独立连接，按实际 listener 端口放行）。
3. 必须有确定的**命名与撤销契约**：规则名带统一前缀（如 `LanRemote ...`），App **只能**删除自己按该前缀创建的规则；「停止放行」必须精确撤销，不得误删用户自建规则，不得整段关闭防火墙。
4. `configure-firewall.ps1` / `remove-firewall.ps1` **降级为可选的离线维护 / 无人值守入口**，不再是终端用户主路径；M10 若仍交付，必须在文档里写明它只是等价的手工路径。

**Context**：`07_MILESTONES_AND_TASKS.md` M10 的任务列表把防火墙交付成两个 ps1，等于要求终端用户自己去开管理员 PowerShell。用户在验收中明确否决了这条路径。
**Consequence**：App 需要具备 UAC 提权路径，并定义提权后的命令行协议（如 `--elevated-firewall allow|remove`）与结果回报方式；发布包必须让用户能看清 UAC 弹窗的来源（名称 / 发布者），否则会被当成可疑程序。
**硬约束**：**绝不在后台静默创建防火墙规则**——必须同时经过「用户点击」与「UAC 确认」两道显式确认。
**可逆性**：可逆，但撤销语义必须在第一版就设计进去，不允许先做单向的 add。
**实施时机**：**M10（防火墙 / 发布）**，与 self-contained publish 一起做。

**增补（2026-09-21，首启 UX 评审分流；依据 `docs/UX_FIRST_RUN_REVIEW_TRIAGE.md` §2 W1/W2/W3/W6。以下为增补段内独立编号）**：

1. **产品规则模板**（与验收脚本的宽规则不同；**不要**照抄 `set-lab-ip.ps1` 现状——
   它的规则是 `Program=Any / RemoteAddress=Any`，实测读回确认）：
   **两条独立**入站 Allow 规则——UDP 45872 一条、TCP 45873 一条；每条都必须
   `-Program` = 当前 exe 完整路径、`-RemoteAddress LocalSubnet4`（IPv4 专属本地子网，实测可写可读）、
   `-Profile Any`；命名沿用统一前缀。
2. **启动健康检查 + Repair**：每次启动**静默**读回两条规则（存在 / Enabled /
   `Program` 与当前 exe 路径一致 / scope 仍为 `LocalSubnet4` / 端口正确）。
   失配 → UI 提示「修复局域网访问」（用户点击 + UAC 后才执行）。**修复必须原地更新**
   （`Set-NetFirewallRule -Program` 或 filter 对象 pipeline `Set-NetFirewallApplicationFilter -Program`，
   实测保住 InstanceID），**不得删除+重建**（实测会换 InstanceID）；
   修复后逐字段读回校验，**任何字段变化（尤其 scope 变宽）即失败**。
   读回比对前先 `ExpandEnvironmentVariables`（实测教训：不展开会误报）。
3. **不自动弹 UAC**：只有用户点击时才提权；启动与周期检查不得弹。
   UAC 被拒绝 = 正常结果（显示「未授权，未做更改」），不得循环重生，只在用户再次点击时重试。
4. **标准用户文案**：管理员账户 = 一次「是」；标准用户账户 = 需输入管理员凭据（可能两屏）——
   说明文案必须如实，不得承诺「永远只需点一下」。
5. **TOCTOU**：提权执行前重新读取现状（规则是否存在/字段、exe 路径），不信任 UI 打开时的快照；
   执行后读回。
6. **GPO 不对抗**：若失败原因指向策略限制（域环境）→ 明确报告「受组策略管理，LanRemote 不会尝试绕过」。
   域环境行为本机不可验证（本机 596 条规则 0 条 GroupPolicy 来源），标注**未测**。
7. **不采用 Windows 首次监听防火墙弹窗**作为可达性机制（不可控、可被环境关闭；
   与「显式 + UAC」原则不合）——这是设计决定，不是对其行为的断言。
8. **撤销入口**：「移除局域网访问」必须与添加入口同处可见，精确删除本前缀规则。

### ADR-026 — 临时私有地址一键：显式、确认、可撤销；**禁止静默自动改 IP**

> **已被取代（2026-09-24，ADR-045）**：产品改 IP 计划（含下方全部增补）不再适用；当前沿用现网，不允许以确认、UAC、实验环境或未来撤销为由修改网络。历史 DHCP/静态切换与 Undo 步骤不是现行工艺；`set-lab-ip.ps1` 已在参数块后直接 `throw`，不执行历史逻辑，也不再是可执行参考实现。下文历史往返结果不证明本次网络已恢复；当前 WFD 发现例外以 ADR-045 为准。

**日期**：2026-09-20（M2 两机验收后）  
**Decision**：
1. App 可提供「添加临时私有地址」一键（用于当前网络不是 RFC1918 又要两台机组网的场景），但**必须是显式、可撤销的高级操作**：
   - 先列出候选**真实**网卡并**让用户明确选一张**，不允许自动替用户选；
   - **排除虚拟网卡**：VMware / VMnet / VirtualBox / Hyper-V / TAP-Windows / OpenVPN / WireGuard / Npcap / Bluetooth / WAN Miniport / Wi-Fi Direct / Hosted Network / Loopback / Teredo / ISATAP / RAS Async / ZeroTier / Sangfor / PANGP / AnyConnect / Tunnel / Virtual，以及 `本地连接*` / `Local Area Connection *`（匹配 `InterfaceDescription` + `Name`，规则见 `scripts/acceptance/set-lab-ip.ps1` 的 `Test-VirtualAdapter`）；
   - 自动选择只允许作为兜底，且选中疑似虚拟网卡时必须显式警告；
   - 确认文案必须写明：「将向 <网卡名> 添加 <地址>/24（不设网关），现有上网配置 <原地址> 保持不变」。
2. **禁止静默自动改 IP**：启动时自动改、发现失败自动改、后台定时改，一律不允许。改 IP 只能来自用户的一次显式点击。
3. 必须遵守 Windows 实测约束（**两次断网换来的，不要凭直觉改**）：
   - IPv4 是「DHCP **或** 静态」二选一；`New-NetIPAddress` 与 `netsh interface ipv4 add address` 都会把接口 `Dhcp` 置为 `Disabled` 并丢掉租约；
   - 因此「追加第二个地址」的正确顺序是：**先把整张接口切成静态并逐项回填现有地址 / 掩码 / 网关 / DNS** → 再追加 lab 地址（/24，**不设网关**）→ 校验原地址仍在；
   - 撤销顺序：删掉**自己加的那个**地址 → `netsh interface ipv4 set address source=dhcp` + `set dnsservers source=dhcp` → 自校验；
   - **自校验三条缺一不可**：lab 地址必须消失、`Dhcp` 必须恢复 `Enabled`、必须拿回可用 IPv4；
   - **`netsh` 退出码不可信**：对已经是 DHCP 的接口执行 `set address source=dhcp` 会返回**非零**并打印「已在此接口上启用 DHCP。」（这是提示性信息，不是失败）——必须以 `Get-NetIPInterface` 的实际状态判成败，且**撤销必须幂等**（可重复执行且都返回成功）。
4. 状态文件必须持久化「撤销所需的全部信息」（接口 index / 别名、原先是否 DHCP、原地址掩码网关 DNS、追加的地址）。校验失败必须明确报失败并保留现场，**不得**假装成功。
5. `scripts/acceptance/set-lab-ip.ps1`（v3）是这套规则的**已实测参考实现**，产品实现时照抄其顺序与校验点；脚本本身保留为验收 / 离线工具，**不是**用户主路径。

**Context**：用户提出「这个操作应该就是无感的才对」。把改 IP 做成 App 内一键确实可行，但改 IP 是**影响用户整机联网**的操作，做成无感自动执行风险极高（本机实测已两次把自己搞断网）。因此本 ADR **采纳「一键」、拒绝「无感」**：入口在 App 内、提权走 UAC，但触发与网卡选择必须用户显式确认，且必须一键撤销。
**Consequence**：App 需要管理员能力，并要处理改完 IP 后网卡短暂处于 `Identifying...` 的窗口（`Set-NetConnectionProfile` 会失败，需重试，实测 5 次 × 3 s）；防火墙规则用 `Profile Any` 以免受网络 profile 变化影响。产品代码里**不得**出现自动清理备份 / 自动删除用户配置的逻辑。
**硬约束**：只追加地址，**绝不删除用户原有地址**；撤销只删自己加的那个。
**可逆性**：一键撤销是本 ADR 的强制组成部分，**不存在「只 add 不 remove」的实现**。
**验证**：`set-lab-ip.ps1` v3 已在两台实机跑完完整往返——`-Role A` / `-Role B` EXIT=0 → `-Undo` EXIT=0 → **再次 `-Undo` 幂等 EXIT=0**；A 机恢复 `172.100.166.220/24 Dhcp`、网关 `172.100.166.254`、ping 通、无 lab 残留；B 机恢复 `172.100.166.65/24 Dhcp`。B 机首次自动选中 `VMware Network Adapter VMnet1`，lab 地址落到虚拟网卡上，A 机收到 **0** 个来自 192.168.1.20 的包——这就是排除虚拟网卡规则的实证，改回自动选必踩。
**实施时机**：**不早于 M10**；M3~M9 不得实现。

**范围外**：discovery 的绑定是否也采用同一套虚拟网卡过滤，不在本 ADR 内。M2 的 `NetworkInterfaceSelector` 已有自己的启发式（HANDOFF §3）且被 408 个测试与两机验收基线锁住；将来若要复用本规则，必须**单独立 ADR 并重跑两机验收**，不得顺手改。

**增补（2026-09-21，首启 UX 评审分流；依据 `docs/UX_FIRST_RUN_REVIEW_TRIAGE.md` §3.1。以下为增补段内独立编号）**：

1. **产品定位 = Advanced 恢复选项**：默认不出现；入口藏在诊断的
   「为什么看不到设备 → 了解补救选项」之后。默认路径永远是「诊断 + 解释 + 拒绝」，不做任何自动动作。
2. **禁止硬编码 lab 地址**：192.168.1.10/.20 只属于验收物料。产品必须让用户确认/修改候选地址；
   候选预填策略 M10 设计。
3. **冲突预检与重叠预检**：添加前对候选地址做探测（ping；实测：能发现会应答的占用者，
   **不能证明空闲**——静默主机/屏蔽 ICMP 会漏报，结果必须如实呈现「未发现应答」而非「确认空闲」）；
   候选子网与任何现有接口子网重叠 → 警告或拒绝（机制 M10 实测后定稿）。
4. **两个 UAC 事务不合并**：防火墙与改地址是两次独立提权（两次明确同意）；
   不得设计「一次提权干两件事」。
5. **生命周期**：「临时地址」的实际语义 = **持续到用户显式撤销**
   （不随退出清理、不自动清理——自动清理有意外断网与半状态风险）；
    UI 必须常驻显示已添加的测试地址并提供撤销入口。
6. **receipt 式撤销**：状态文件只记录「本工具自己加的地址」与「为追加而必然连带变更的 DHCP 状态」；
    撤销只恢复这两类，不做任何「猜测式还原」。
7. **驳回评审的「只加不改」机制描述**（见 TRIAGE §3.1）：Windows 上不存在
    「保留 DHCP 的同时追加静态地址」的路径（`New-NetIPAddress` / `netsh add address`
    均置 `Dhcp=Disabled` 并丢租约，本机两次实测）。维持 §3 现行工艺；
    评审的**意图**（最小变更 + 精确撤销 + 诚实文案）已由 1~6 条吸收，
    其中文案必须明说「该网卡将从 DHCP 变为静态（已回填全部原参数），移除时恢复」——
    不许把有代价的操作描述成无感。

### ADR-027 — 发现层身份冲突（同 deviceId + 不同指纹）不得静默 last-write-wins
**日期**：2026-09-21（M3 红队评审产出）  
**Decision**：
1. **M3 不得改动 M2 已验收的缓存语义**：`DiscoveryDeviceCache` 目前是 `Dictionary<Guid, DiscoveredDevice>`
   且 `Upsert` 为 last-write-wins（读代码确认，非推测），即同 deviceId 换 IP / 换指纹会静默覆盖。
   这条行为在 M3 **保持不变**。
2. **最晚在 M4 交付前**必须引入冲突语义：在 discovery TTL 内同时观察到
   **同 deviceId 但不同 `certSha256`** → 该 deviceId 标记 `IdentityConflict`，**禁用**其连接入口，
   而不是二选一地挑一个指纹。
3. 良性场景必须保留：**同 deviceId + 同指纹 + 不同 IP** 是同一台机器的多网卡正常广播，不得判为冲突、不得误杀。
4. 解决手段**不包括** CA、持久信任库、「记住此设备」。**禁止把 discovery 来源的指纹持久化成信任**
   （那等于把未认证信道当成 TOFU 根）。
5. M3 期间的止血手段是「连接目标不可变快照」（见 `M3_REVIEW_TRIAGE.md` A 桶第 2 条）：
   `{deviceId, remoteIPv4, tcpPort, expectedCertSha256}` 在点击连接时冻结，握手期间不许回读缓存。

**Context**：外部红队评审与我复核代码后一致确认静默覆盖是真实缺口；但修它会改动 M2 已通过两机验收的行为，
必须由独立 ADR 授权后再动。M3 阶段的危害边界是清楚的：攻击者可诱导客户端连到攻击者的证书，
但**过不了 M4 的 access key proof**——所以是「连错对象」，不是「拿到授权」。
**Consequence**：M4 之前该冲突可被利用（仅限链路内攻击者）；UI 需要新增「身份冲突」状态与禁用连接的呈现；
改动后需重跑两机验收。
**可逆性**：可逆，但属于安全收紧，不建议回退。
**验证**：三条用例——① `A/X@IP1` 后 `A/X@IP2` → **无**冲突；② `A/X` 后 `A/Y` → 冲突且连接被禁用；
③ 所有 `Y` 观察过期后 → 冲突可清除。
**实施时机**：**M4**（最晚），**不在 M3**。→ 实际落地：**M4 阶段 0 落地件**（2026-09-21，见下）。

**M4 阶段 0 实现定案（2026-09-21）——「冲突字段与丢弃策略」**：

6. `DiscoveryDeviceCache` 内部存储改为 `Dictionary<Guid, Entry>`；
   `Entry = { DiscoveredDevice Device; Dictionary<string, DateTimeOffset> Conflicts }`
   （其他指纹 → 最近观察时刻）。指纹键用 `StringComparer.OrdinalIgnoreCase`
   （生产路径已由 evaluator 规范化为 64 字符大写 hex；缓存对直接 API 调用方的任意大小写稳健）。
7. `Upsert` 分流：同 deviceId 且指纹 ≠ 主条目指纹 → **只记冲突观察、不覆盖主条目**（即「不二选一」）；
   同指纹（含不同 IP/端口）→ 照常 last-write-wins 更新（良性多网卡路径与 M2 语义不变）。
   冲突观察的 `isNew` 返回 `false`（设备不算「新」）。
8. 冲突记录**有界**：每条目最多 8 条；超出先清自己的过期项，仍满则淘汰最旧一条。
   持续推送新指纹的攻击者 = 冲突标记持续存在（预期行为）；内存上界 = 8 × 64 字符 × `MaxCachedDevices`。
9. 查询 API：`bool IsIdentityConflicted(Guid deviceId, DateTimeOffset now)`——
   查询时先惰性清除该条目中过期（> TTL）的冲突记录，再判断是否非空。
   时间由调用方传入（与既有 `RemoveExpired` / `Upsert` 风格一致，单测无需 sleep）。
10. **显式边界（诚实写出）**：主条目整体过期 → 整条移除（含冲突记录）。
    含义：`X` 的最后一次观察超过 TTL 后缓存退化为「从未见过 X」；若此时 `Y` 仍在广播，
    `Y` 会作为新条目进入——这是 TTL 语义的一部分，v1 **不做跨 TTL 的身份记忆**
    （ADR-027 第 4 条已排除持久化）。可接受性论证：discovery 条目从不是信任源
    （连接仍须 pin + 访问密钥认证），该边界只影响「显示」，危害仍是「连错对象」而非「拿到授权」。
11. 落地范围（M4）：cache 语义 + 单测。「禁用连接入口」的**消费点**（UI 呈现与点击拦截）
    在 UI 里程碑接入；M4 不新增 App 侧消费，验收器不加相关场景。
    「改动后需重跑两机验收」→ 归入 M4 收口的验证批次。

### ADR-028 — M3→M4 身份绑定契约：transcript 必须绑定「M3 实际出示的」证书指纹
**日期**：2026-09-21（M3 红队评审产出）  
**Decision**：
1. M3 的连接上下文必须**不可变**且携带四元组：`{ deviceId, endpoint, expectedPin(32B), presentedPin(32B) }`。
2. `presentedPin = SHA256(实际出示证书的 RawData)`，在 TLS 证书校验回调里捕获，此后不可修改。
3. M4 的 canonical transcript **必须包含 `presentedPin`**，并且客户端必须校验
   `presentedPin == expectedPin`（连接时冻结的那一份）。三条缺一不可。
4. 只绑定 `expectedPin` **不算**满足本 ADR——那正是可被中继的形态。

**Context**：评审指出的缺口：若 M4 的 HMAC transcript 不绑定 M3 实际出示的证书指纹，活跃攻击者可以在
一条 TLS 连接上收下 nonce、在另一条连接上把 challenge/proof 中继给真正的服务器（凭据中继）。
我原本以为规格里 M4 的「modified cert fingerprint fail」已覆盖，但那条测的是**篡改**场景，
不等于 transcript **包含并校验** presented fingerprint——这是两个不同的要求。
**Consequence**：`presentedPin` 是 **M3 的交付物之一**（不是 M4 的内部细节），M3 必须把它暴露在连接上下文里；
M4 的 transcript 结构因此被提前固定。
**可逆性**：不可倒退——去掉这条就重新引入中继缺口。
**验证**：客户端连到证书 `X` 的 TLS，而真实服务器持有证书 `Y`，中继 nonce 与 proof；
认证必须失败，且失败原因**仅**来自 `X ≠ Y`。
**实施时机**：M3 出接口与不可变上下文，**M4** 完成 transcript 绑定与校验。

### ADR-029 — 证书加载使用 `X509KeyStorageFlags.Default`（不带任何 flag），取代 ADR-016 与 ADR-018
**日期**：2026-09-21（M3 开工前实测，ADR-018 风险收口）  
**Decision**：`DeviceCertificateService.ImportFlags` 改为 **`X509KeyStorageFlags.DefaultKeySet`（值为 0）**：
（`X509KeyStorageFlags` **没有名为 `Default` 的成员**——正确名字是 `DefaultKeySet`；实测用的 `(X509KeyStorageFlags)0`
与它等价。这条由编译器直接指出，写文档时别再写成 `Default`。）
**不使用** `EphemeralKeySet`、**不使用** `PersistKeySet`、**不使用** `Exportable`。
证书对象必须在使用结束后 `Dispose()`（临时密钥容器在 dispose / GC 时删除）。

**Context**：ADR-016 选了 `PersistKeySet | Exportable`（理由是传闻中 Windows SslStream 需要持久化私钥），
ADR-018 以「那是未经实测的猜测」为由换成 `EphemeralKeySet`——**两条都没有真实 TLS 测试支撑**。
2026-09-21 用真实 `SslStream` server/client 握手实测（loopback；证书 profile 严格复刻
`DeviceCertificateService`：ECDSA P-256 / non-CA / KeyUsage=digitalSignature(critical) /
EKU serverAuth / 5 年 / PFX 随机口令），矩阵 = 3 种 flag × 3 种协议 × 重复 3 次：

| 载入 flag | Tls12 | Tls13 | Tls12\|Tls13 | CNG key 文件（load → dispose+GC） |
|---|---|---|---|---|
| `EphemeralKeySet` | **FAIL ×3** | **FAIL ×3** | **FAIL ×3** | 145 → 145 → 145（不落盘，但**根本不能用**） |
| `PersistKeySet` | OK ×3 | OK ×3 | OK ×3 | 145 → 146 → **146**（**磁盘留下持久副本**） |
| `DefaultKeySet`(0) | OK ×3 | OK ×3 | OK ×3 | 148 → 149 → **148**（运行时有，dispose 后删除） |

- 服务端真实异常：`AuthenticationException: Authentication failed because the platform does not
  support ephemeral keys.` ← `Win32Exception: 安全包中没有可用的凭证（0x8009030E = SEC_E_NO_CREDENTIALS）`。
  客户端只能看到 `IOException: Received an unexpected EOF or 0 bytes from the transport stream`——
  **必须抓服务端异常才能定位**（评审 C 桶第 26 条的现实版）。
- 成功用例均完成真实帧收发（`echo='pong:hello-lanremote'`），协商结果：
  TLS 1.2 = `TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384`；TLS 1.3 = `TLS_AES_256_GCM_SHA384`；
  `Tls12|Tls13` 协商到 **TLS 1.3**。pin 校验（`GetRawCertData()` + `FixedTimeEquals`）全部 True。

**⚠️ 测试污染陷阱（本机实测，务必记住）**：同一进程内**先用 `PersistKeySet` 导入过同一把私钥**之后，
再用 `EphemeralKeySet` 导入同一证书 → **握手居然成功**。即 `EphemeralKeySet` 能否用取决于进程内此前的
导入历史。M1 单元测试里"看起来没报错"完全可能只是这个假象。
**任何关于 flag 的结论都必须在新进程、顺序受控的前提下测。**

**Consequence**：
- ADR-016 与 ADR-018 **同时作废**；`DeviceCertificateService.ImportFlags` 常量改为 `Default`。
- **必须同步改测试**：`DeviceCertificateTests.ImportFlags_UsesEphemeralKeySetOnly` 当前把
  「flags == EphemeralKeySet」锁成了断言——它锁住的是一个已被证伪的选择，M3 必须改成断言新 flag
  并按新语义改名 / 改注释。
- 残余风险（如实记录，不掩饰）：`Default` 在**进程运行期间**仍会在
  `%APPDATA%\Microsoft\Crypto\Keys` 生成一个临时密钥文件，dispose / GC 后删除；
  **进程崩溃**时可能残留。所以证书必须 `Dispose()`，且**不能**把「磁盘上绝无第二份副本」当硬保证。
- 私钥仍不可导出（无 `Exportable`）；持久化副本仍以 DPAPI 保护的 `secrets.bin` 为准。

**可逆性**：**不可回退**——退回 `EphemeralKeySet` 会让 TLS 服务端直接无法工作。
**验证**：M3 必须新增真实 `SslStream` 握手集成测试（即 ADR-018 遗留的那条强制要求），
断言：握手成功 ∧ 协商协议 ∈ {Tls12, Tls13} ∧ pin 匹配 ∧ 真实帧收发往返成功。
**实施时机**：**M3 第一步**——先改 flag 并跑通该集成测试，再写其它代码。


---

## ADR-030 — 自签发的私钥是 ephemeral 的，必须经 PFX 往返才能做服务端凭据

**日期**：2026-09-21（M3 阶段 1 实测）
**状态**：已定，实测结论
**关联**：收紧 **ADR-029**（不是取代）

**Context**：M3 阶段 1 写测试时，用 `CertificateRequest.CreateSelfSigned(...)` 直接造证书当 TLS
服务端，结果 4 个真实握手用例全挂，且客户端只报
`IOException: Received an unexpected EOF or 0 bytes from the transport stream`。
抓服务端异常后才看到真实原因。

**实测**（Windows 11 25H2 / build 26200，.NET 10.0.12，loopback + 真实 `SslStream`）：

```
System.Security.Authentication.AuthenticationException:
  Authentication failed because the platform does not support ephemeral keys.
 ---> System.ComponentModel.Win32Exception (0x8009030E): 安全包中没有可用的凭证
   at System.Net.SSPIWrapper.AcquireCredentialsHandle(...)
   at System.Net.Security.SslStreamPal.AcquireCredentialsHandle(...)
```

**这与 ADR-029 里 `EphemeralKeySet` 的失败是同一个错、同一个 `0x8009030E`。**

**Decision**：

1. Schannel 拒绝的是**密钥本身的 ephemeral 属性**，不是"某个叫 `EphemeralKeySet` 的导入 flag"。
   ADR-029 把导入 flag 改成 `DefaultKeySet` **只是必要条件**——
   如果签发得到的私钥本身就是 ephemeral 的，改 flag 也救不了。
2. 因此 `DeviceCertificateService` 里"签发后立刻导出 PFX、再用 `DefaultKeySet` 导入"这步
   **不是冗余代码，是必需环节**。任何人不得为了"简化"把它删掉。
3. 测试造证书时也必须复刻同一条路径（`X509CertificateLoader.LoadPkcs12(pfx, pwd, DefaultKeySet)`），
   否则测试证书与真实设备证书的密钥形态不一致，得出的结论不可外推。

**Consequence**：
- 新守护测试 `SelfSignedKeyEphemeralTests.CreateSelfSigned_Certificate_Cannot_Serve_Tls_Without_Pfx_Roundtrip`
  把这条行为钉住（非 Windows 平台自动跳过——这是 Schannel 的行为）。
- 再次印证：判定握手失败**不能只看客户端异常**。客户端端永远是 `IOException: unexpected EOF`，
  真实原因在对端。测试基建（含 `TestTlsServer`）必须收集服务端异常。

**可逆性**：不可回退。

---

## ADR-031 — Host 启动按网卡降级，不整体失败

> **后续说明（2026-09-24，ADR-045）**：本 ADR 的 Transport Host 按网卡降级策略保留，不等于 discovery receiver 可吞掉加组失败。下文「唯一活跃地址」仅描述 2026-09-21 现场；当前热点接口为 if13、`192.168.137.1/24`，原上游 `172.100.166.220` 仍非 RFC1918，不能据旧环境断言当前必然没有合格接口或 listener。

**日期**：2026-09-21（M3 阶段 2）
**状态**：已定

**Context**：每张合格网卡要起一个 45873 listener。多张网卡里只要有一张 bind 失败
（端口被占、地址消失、权限问题），Host 该怎么反应？

**Decision**：**按网卡降级**。失败的那张记进 `TransportHostStartResult.Failures`
（地址 + `SocketError` + 消息），其余照常监听；一个都没听上时 `IsListening == false`
且**不抛异常**，由调用方决定如何呈现。

**Rationale**：
- 一张网卡有问题不该放大成「整台机器不能被连接」。
- 安全性不受影响：每个 accept 都用**接受它的那个 listener 的本地地址**做同子网校验，
  少监听一张网卡只是那个子网连不进来。
- 「一个都没听上」在本机是**预期状态**（唯一活跃地址 `172.100.166.220` 不是 RFC1918），
  与 M2 discovery 的行为一致；抛异常会把正常状态变成崩溃。

**Consequence**：调用方（M9 的 UI 诊断，ADR-024）必须读 `Failures` 并呈现原因，
不能只看 `IsListening` 就报告"启动成功"。

---

## ADR-032 — 不要过度声称 `ExclusiveAddressUse` 的作用

**日期**：2026-09-21（M3 阶段 2 实测）
**状态**：已定（**修正我自己先前写错的断言**）

**Context**：我一度在代码注释与测试里写道「所有 listener 开 `ExclusiveAddressUse`，
这样若别的进程先占了更宽的地址就会失败」。**实测证明这句话是错的。**

**实测矩阵**（本机 Win11 25H2 / 26200，.NET 10.0.12，见 `MultiAddressListenTests`）：

| 场景 | 结果 |
| --- | --- |
| 同端口 + 两个不同具体地址 | 开不开都**可以** bind |
| 同地址同端口第二次 | 开不开都**被拒**（`AddressAlreadyInUse` 10048） |
| 别人先 bind `0.0.0.0`（未开 exclusive），我们再 bind 具体地址 | **仍会成功**——开 exclusive 也发现不了 |
| 我们先 bind 具体地址（exclusive），别人再 bind `0.0.0.0` | **也成功** |
| 带 `SO_REUSEADDR` 的后来者抢同地址同端口 | 开不开都**被拒**（`AccessDenied` 10013） |
| 连到具体地址的连接归谁 | 归**更具体**的那个 listener，不会被更宽的 socket 截走 |

**Decision**：
1. 仍然保留 `ExclusiveAddressUse = true`，但理由只能写成：
   防御「`SO_REUSEADDR` 语义更宽松的旧版 Windows」。
2. **不得**声称它能发现端口已被占用/已被更宽地址覆盖。真正的冲突信号是
   `AddressAlreadyInUse`，且已经记进 `Failures`。
3. 「我们的 bind 与更宽的 bind 共存」不是端口被抢：实测流量交给更具体的 listener。

**教训（比结论更重要）**：我第一次写的守护测试把 `ExclusiveAddressUse` 也开在了
**对照组**的 socket 上，导致「把被测属性改成 false」的变异**没有让测试失败**——
测试隔离错了，看起来通过其实是空断言。发现方式是：变异后测试仍然绿。
**变异验证必须做完并确认真的变红，否则等于没做。**

---

## ADR-033 — 未认证阶段（pre-auth）的输入预算：4 KiB 上限 + 五段绝对 deadline + 超限不 drain

**日期**：2026-09-21（M3 阶段 3）
**状态**：已定（**对规格的新增约束**，来自外部红队评审 A-8 / A-9 / A-10）

**Context**：规格只规定了「Control 消息最大 1 MiB」。但那条额度属于**认证之后**。
一个刚 accept 上来、还没通过任何认证的 TCP 连接，凭什么能逼我们 `new byte[1 MiB]`？
同样地，「超时」若实现成「距上次读到字节 N 秒」，攻击者每 `timeout - ε` 发一个字节就能
把一个未认证连接无限期挂住——名额不归还，等价于低成本资源耗尽。

**Decision**：
1. `TransportConstants.MaxPreAuthMessageBytes = 4 KiB` 作为 **pre-auth 单帧上限**，
   与认证后 `MaxControlMessageBytes = 1 MiB` 严格区分。4 KiB 对 `channel_hello` 绰绰有余。
2. 五段**绝对** deadline（connect / handshake / 长度前缀 / payload / hello）各自独立计时，
   由每段入口处的 `CancellationTokenSource.CancelAfter` 实现，**读循环内不得重置**
   （评审 A-8）。不使用 `Stream.ReadTimeout`——它只覆盖同步读。
3. 长度先在 `uint` 域校验（拒 `0`、拒超上限），**之后**才转 `int`（评审 A-10）。
   `0xFFFFFFFF` 直接转 int 是 `-1`。
4. **超限一律不 drain**：直接抛 `FrameProtocolException` 并关闭连接。
   违规的一侧没有资格让我们继续读它的数据。
5. 拒绝原因（`length-zero` / `length-exceeds-limit`）**只进本地日志，绝不下发给对端**——
   下发等于送一个免费的探测探针。

**实测（本机 Win11 25H2 / 26200，回环，真实 `SslStream`，见 `PreAuthDeadlineTests`）**：

| 场景 | 时限 | 实测 | 异常类型 | 名额归还 |
| --- | --- | --- | --- | --- |
| pre-auth 空闲 | 400 ms | **413 ms** | `System.OperationCanceledException` | 是 |
| 只发 2 字节（半截前缀） | 400 ms | **400 ms** | 同上 | 是 |
| 声称 64 字节只发 10 个 | 600 ms | **608 ms** | 同上 | 是 |
| 3 条 TCP 连上但永不握手 | 400 ms | **436 ms**（未调 `StopAsync`） | — | 是 |

**两点结论**：
- deadline 执行开销在 **0–36 ms** 量级，几百毫秒级时限可放心使用。
- 真实 `SslStream` 取消抛的是**基类** `OperationCanceledException`；
  而 `MemoryStream` / `Task.Delay` 替身抛的是 `TaskCanceledException`。
  **两者不是同一个类型**，所以断言只写 `is OperationCanceledException`，不锁死子类。

**变异验证（两条都确认真变红）**：
- 把 `CancelAfter` 挪进读循环 → 低速攻击用例变红（8 字节被全读完、耗时 1 s、根本不抛）。
- 注释掉握手 `CancelAfter` → 握手卡死用例变红（名额 12 s 未归还）。

**未决**：五个 deadline 的**数值本身**是否合适，不在本机实测范围内，
留给第二轮外部评审。本 ADR 只确立「绝对 deadline」这一机制与 4 KiB 上限。

---

## ADR-034 — 验收判定只写可证伪的约束，且证据不得由「症状推断因果」得出

**Status**：Accepted（2026-09-21）
**Context**：M3 两机验收前做「先修再跑」时，用环回上的假被控端跑了一轮 11 例变异矩阵。
过程中连续抓到四个缺陷，全都在**验收器自己**身上，而且**没有一个**是产品代码错：

1. 交叉核对清单断言「被控端 `connectionsEnteringSessionHandler` 应为 3（4 减 1）」——
   依据是「`pin-mismatch` 死在 TLS 阶段、服务端不会留行」。**实测是 4**。
2. `--all` 下 `pin-mismatch` 必然 FAIL——直连模式漏了「翻转一位」，
   四个场景共用同一个 `--pin`，于是拿真指纹去连、握手必然成功。
   失败信息却写着「本该在 TLS 阶段被拒绝，实际握手成功了」，看上去像产品坏了。
3. 同一份交叉核对输出里，逐条期望写 `pin-mismatch → (无行)`，
   而 ④ 的区间写 `3..4`（承认可能有幽灵行）。**两句话互相打脸**。
4. `HeadlessCommand.TryParse` 在参数错误时 `return true`，调用方按「成功」处理 →
   `RunHeadless(null)` → NRE → 被顶层兜底成退 3，**参数错误的消息一个字都没打出来**
   （stdout/stderr 均 0 字节）。

四个的共性是：**都不是被测对象错，而是「证据/接口自己说谎」**。
而验收器的错误会直接污染里程碑结论——它比产品 bug 更严重，因为它把「没测到」伪装成「测过了」。

**Decision**：

1. **验收器的判定只写可证伪的约束，不写推断出来的数字。**
   凡是要写一个具体数字（计数、行号、时长）的地方，必须先有本机实测或可机器判定的约束支撑。
   拿不到就写**区间**，并在输出里说明区间为什么是区间。

2. **期望值只能取决于「客观发生了什么」，不能取决于「场景是否通过」。**
   ④ 的区间原先按 PASS 计数算，于是场景一失败区间自己就漂，
   读者会把漂出来的数字当成实测值。现在按 `ReachedWire` / `TlsStageRejection` 算。

3. **任何「应当出现 N 行」的期望，必须同时声明它的不成立条件。**
   `pin-mismatch` 现在写的是「TLS 阶段被拒；被控端**可能**留一行 `rejection=pre-auth-eof`（TLS 1.3），
   也可能完全无行（TLS 1.2）——但绝不能是 `outcome=PreAuthenticated`」。

4. **返回值语义必须与调用方理解一致，且 CLI 错误路径必须有可读输出。**
   铁律：**新增任何命令行参数的第一件事，是把错误路径的输出字节数核一遍。**
   静默的失败比错误的失败更坏——后者至少能定位。

5. **同一件事只允许有一个实现。** `FlipOneBit` 现在被发现路径与直连路径共用；
   此前两条路径各写一份，改了一条就炸在另一条上。

6. **跨工具的隐含契约要写在两边的注释里。**「验收器会自己翻转一位指纹」这条
   只写在验收器侧，变异矩阵侧不知道，于是它的 `real/pin-mismatch` 用例以
   「看起来像产品坏了」的方式失败（两次翻转互相抵消）。

7. **不许把「没观测到」写成 0。** 被控端的同子网拒绝 / 准入拒绝 / TLS 失败全部静默
   （HANDOFF §14.12），验收器一律标 `UNOBSERVABLE`。填 0 等于宣称「我们测到了零次」。

**本机实测依据（2026-09-21，Win11 25H2 / 26200，环回，真实 exe 打假被控端）**：

- 变异矩阵 **11 例全部符合预期**。其中最关键的是一条**反向**证据：
  把假被控端的时限实现改成「每读到字节就重置」后，`slow-dribble` 变红
  （`sent=4/4`，16054 ms），而 `timeout` **仍然绿**——
  这直接证明 `timeout` 单独**区分不出**「绝对时限」与「可重置空闲时限」，
  也就证明了 `slow-dribble` 不是重复场景而是唯一证据。
- TLS 1.3 幽灵行的实测值：客户端拒绝证书发 TLS alert，服务端在收到该 alert 之前
  就已认为握手完成，于是照样进会话处理器、读到 EOF 给出 `rejection=pre-auth-eof`。
  TLS 1.2 下服务端握手直接失败、才真的不留行。
- 裸后台线程未处理异常 → 进程**当场死亡**（exit 127，无输出）；
  未 observe 的 faulted Task → 进程**存活且完全静默**。
- TCP 层连不上抛 `SocketException`/`IOException`，**永不**是 `AuthenticationException`
  → 独立 TCP 探针被删除，改用结构化断言。
- `ReadAsync == 0` **不等于**收到 `close_notify`（裸 TCP FIN 也返回 0）
  → 全部文案降级为「有序 EOF」。

**Consequences**：

- 验收器输出变长了，但每一行都可以被独立证伪——这是刻意的。
- ④ 是区间不是数字，人工核对时可能觉得「不确定」；但一个**错的确定值**更坏。
- 第二轮外部评审（具体实现、错误消息分类、五个 deadline 数值）仍是未决项：
  本 ADR 只确立「怎么给判定」，不确立「判定阈值该是多少」。

---

### ADR-035 — 提权模型：主进程永不提权 + 窄域 elevated helper（固定动词集）

> **后续约束（2026-09-24，ADR-045）**：**「主进程永不提权」继续有效**，UI / 发现 / 传输 / 诊断仍在普通用户令牌下运行。下文防火墙与地址 elevated helper 的网络修改默认落地已停止；固定动词、单次同意或 UAC 都不能豁免不改网约束。当前验收器 elevated/lab/Undo 改网路径直接拒绝，不调用 shell/UAC；历史分层设计不是恢复这些入口的授权。

**日期**：2026-09-21（首启 UX 评审分流；见 `docs/UX_FIRST_RUN_REVIEW_TRIAGE.md`）  
**Decision**：

1. **主进程永不提权**：UI / 发现 / 传输 / 诊断全部在普通用户令牌下运行。
   「只读操作零提权」（ADR-024 增补 5）是这条的下界。
2. 一切需要管理员权限的操作走 **elevated helper**：`runas` 启动的**自身实例** + 专用动词
   （本机提权路径已两次实测：`ShellExecuteExW(lpVerb="runas")` + `WaitForSingleObject` + `GetExitCodeProcess`）。
3. helper 只接受**固定动词集**（M10 生效范围内仅：防火墙 `firewall-add / firewall-remove / firewall-repair`
   与地址 `addr-add / addr-remove`）；参数一律**结构化值**（网卡索引、地址、调用方声明的 exe 路径）；
   **不接受**任意命令、任意路径拼接、脚本文件、下载执行。helper 不得成为通用提权原语。
4. 每次提权会话只做**一件事**，完成即退出（防火墙与地址 = 两次独立提权，不得合并）。
5. 执行前**重校验（TOCTOU）**：helper 重新读取系统现状（规则现状、地址现状、声明路径与自身路径是否一致），
   与调用方传入的预期不符 → 拒绝执行并报告原因。
6. **UAC 被拒绝是正常输入**：UI 显示「未授权，未做任何更改」；不得自动重弹；
   只在用户再次主动点击时提权。（`ERROR_CANCELLED` 路径待 M10 实测确认，当前不写死代码分支。）
7. **提权边界不得蔓延**：新增第三类提权操作必须先修订本 ADR。

**Context**：评审 Q1 的架构主张（主进程非提权 + 窄域 helper）与本项目既有铁律
（「绝不在后台静默改防火墙/网络配置」「终端用户不开 PowerShell」）同源。
失败模式「helper 变成通用提权原语」由 §3 的固定动词集 + 结构化参数 + 无脚本文件直接封死。  
**Consequence**：主程序需要一个「以管理员运行且带专用动词」的最小启动分支（不加载 UI/发现/传输），
并需独立测试与文档；UAC 弹窗的来源展示（名称/发布者）属于 M10 发布检查项。  
**硬约束**：helper 不做任何「顺便」的事——每次提权的作用域就是一次显式的用户同意。  
**可逆性**：可逆（纯分层）。  
**实施时机**：**M10**；文档与测试随实现一起交付。

---

### ADR-036 — LanRemote 永不自动更改网络位置（Network Category）

> **后续约束（2026-09-24，ADR-045）**：不修改网络类别的原则保留，并与不修改其他网络配置一并适用于产品、验收、维护及 Undo。下文「脚本维持现状」例外已取消，旧 PS 参数块后立即拒绝；防火墙规则自足的历史方案也不授权当前创建或修复规则。只读诊断仍允许，不能将本段当作改网操作指引。

**日期**：2026-09-21（首启 UX 评审分流；评审建议单独立 ADR）  
**Decision**：

1. 产品**任何路径**（启动、诊断、一键、修复、退出、卸载）都**不得**调用 `Set-NetConnectionProfile`
   或任何等效机制修改网络类别（Domain / Private / Public）。
2. 可达性必须**自足于防火墙规则**：规则用 `-Profile Any` + `-RemoteAddress LocalSubnet4` + `-Program`
   （ADR-025 增补 5 模板），因此不依赖网络类别——不需要、也不允许用「把网络改成 Private」让规则生效。
3. 允许**只读**读取类别用于诊断呈现（如「当前网络位置：公用网络；LanRemote 规则不受位置影响」）。
4. 验收 / 维护脚本（`scripts/acceptance/set-lab-ip.ps1`）属一次性物料、不是产品代码，**维持现状**；
   其「改网络类别」的做法禁止被产品实现拷贝（脚本 `-Undo` 不还原类别这一点已记录在
   TRIAGE §3.3 / §6，M3 收尾后另行处理）。
5. 未来若证明「必须动类别」→ **先立新 ADR + 两机验收**，不得以「顺手」方式绕过。

**Context**：评审 Q2/Q5-7 建议；本机事实：现行 lab 规则 `Profile=Any` 与类别无关，
而改类别会改变**整机**的规则匹配面（风险不对称、收益为零）。
与「永不静默改网络配置」同源，但本 ADR 把范围扩到**包括显式一键在内的所有路径**——
因为规则没有必要依赖它。  
**Consequence**：M10 防火墙一键实现中不得出现 `Set-NetConnectionProfile`；
两机验收需覆盖「网络类别为 Public 时规则仍正常生效」。  
**硬约束**：只读是允许的，写入（任何形式）是禁止的。  
**可逆性**：属于安全姿态收紧，不建议回退。  
**实施时机**：立即（约束后续所有实现）；M10 落地检查。

---

### ADR-037 — M4 衔接层：`ControlPreAuthSession → ControlAuthSession` 显式交接（线性所有权 + exactly-once）
**日期**：2026-09-21（M4 阶段 0 源码盘点定案；评审建议 (b) 与本机基线一致）  
**Decision**：

1. **衔接形态 (b) 显式交接，取代 M3 的「成功即干净关闭」终态**。
   `ControlPreAuthSession.RunAsync` 成功路径**不再**调用 `ShutdownAsync`；它在
   `PreAuthenticated` 状态上停下，并把「活着的流 + 冻结的安全上下文」交给一个
   **一次性（线性所有权）交接对象** `ControlPreAuthHandoff`。
   M3 的干净关闭是「没有后继」时的正确终态；M4 有了后继，关闭时机随所有权一并移交。
2. **交接对象 `ControlPreAuthHandoff`** 持有：
   - `SslStream Stream`（活的；pre-auth 会话此后不得再触碰它）；
   - `ConnectionSecurityContext Security`（冻结值，见第 3 条）；
   - `BeginAuthentication(...)`：**exactly-once**——用 `Interlocked.Exchange` 把
     「`PreAuthenticated` 只允许一次 `BeginAuthentication` 转移」做成可执行形式；
     第二次调用抛 `InvalidOperationException`（与 `RunAsync` 的「每实例只跑一次」同风格）。
     它返回 `ControlAuthSession`（每个连接一个实例；其 `RunAsync` 同样只允许一次）。
3. **`ConnectionSecurityContext`（冻结传递；评审点名）**：本连接的安全事实快照，
   在 `TransportHost` 接受连接后即构造（`AcceptedConnection` 携带），字段：
   `ConnectionId`（每连接 `Guid.NewGuid()`，日志/会话关联用）、
   `LocalAddress` / `RemoteAddress` / `RemotePort` / `NegotiatedProtocol`、
   `ServerCertificateSha256`（**本机服务端证书** DER 的 SHA-256，32 字节）。
   `auth_challenge` 的 `certSha256` 字段**必须**从该上下文派生（uppercase hex），
   保证「challenge 声称的指纹 = 本连接实际出示的证书」。
   实现形态：`AcceptedConnection` 收编为 `(ConnectionSecurityContext Security, SslStream Stream)` +
   便利转发属性（既有消费点不破）；落地与阶段 3 同批。
4. **`ControlSessionState` 扩展**（一个枚举，不另立平行枚举）：
   `AwaitingHello / PreAuthenticated / Authenticating / Authenticated / Closed`。
   `PreAuthenticated` 是交接点状态；`Authenticating` 由 `ControlAuthSession` 推进；
   `Authenticated` 只在认证成功后出现（DoD「auth success 才能有 session」的对应状态）；
   其余失败/停机一律 `Closed`。后续里程碑（Video Attach 等）继续扩展本枚举。
5. **`AllowedOperationsWhilePreAuthenticated` 的 M4 形态：恰好一项 `"begin-authentication"`**。
   门禁测试**有意识改写而非删除**：
   - `PreAuthenticated_Allows_Nothing_Before_M4` → 改写为**精确集合相等**
     （`["begin-authentication"]`；任何人加第二项即红），注释保留 M3 原文与改写理由；
   - 新增行为门禁：第二次 `BeginAuthentication` 抛 `InvalidOperationException`（exactly-once 可执行形式）。
6. **流所有权链闭合**：Host（socket 生到死）→ pre-auth 会话（读 hello 止）→ handoff →
   auth 会话（读 response / 写 challenge+success）→ 认证成功（连接保持，承载 session）或失败（关闭）。
   任何路径的最终释放仍在 `TransportHost.HandleAsync` 的 `finally`——所有权转移**不改变**
   「Host 是 socket 最终拥有者」这一 M2 起的事实。
7. **M4 内「认证成功」的归宿**：注册 session（`SessionRegistry`）+ **连接保持打开**
   （session 的宿主；规格 04 §10「session 断开立即废弃」）。M4 尚无 control 消息消费方，
   行为定义到「保持直到客户端断开或停机」为止。
8. **帧上限沿用 pre-auth 4 KiB（ADR-033）**：challenge / response / approval_pending /
   auth_success 全在认证完成前，读取一律走 `MaxPreAuthMessageBytes`；
   auth 帧解析照 `HelloFrame` 模式（严格 JSON：无重复键 / 无未映射成员 / 显式 MaxDepth /
   拒绝尾随数据 / 非法 UTF-8 不兜底）。

**Context**：M3 步骤 21 有意选择「hello 后干净关闭」，**没有**预留 `AwaitingAuthentication` 占位——
所以 M4 的衔接必须是显式的（这正是评审建议 (b) 与本机盘点的结论，见 HANDOFF §18.2 阶段 0）。
线性所有权 + exactly-once 直指两个具体失败模式：①「pre-auth 会话已经交出去了，
有人还拿着旧引用读流」（两个读者同时消费一条流）；②「同一连接开两次认证」
（第二份 challenge/response 重放面）。
**Consequence**：M3 的以下证据点将被改写而不是保留：
① `Valid_Hello_Reaches_PreAuthenticated_And_Closes` 测试（主题从「关闭」变为「成功交接」）；
② 验收器 `HostRole` 的 `localShutdownSent=best-effort` UNOBSERVED 行（关闭时机语义变了）；
③ 验收器客户端「等对端关闭」判据（阶段 4/5 随认证流程重写）。
**可逆性**：不可退——M3 的「成功后立刻关」在 M4 语义下就是「认证永远不能发生」。
**验证**：门禁测试改写 + exactly-once 行为测试 + 「第二次 BeginAuthentication」红测；
流所有权由「交接后 pre-auth 会话不再持有可用流」的结构保证（类型层面）。
**实施时机**：M4 阶段 3（服务端状态机）落地；门禁测试改写与实现同批（写了才有 → 能测）。

---

### ADR-038 — M4 认证协议定案：双 transcript 拆分、serverProof 绑 grant、限流口径、审批 v1 语义
**日期**：2026-09-21（第二轮外部评审结论落地；HANDOFF §18.4 B 的 ADR 化）  
**Decision**：

1. **transcript 拆两个**（修复评审最重要的设计发现：「双 proof 同 transcript，无法绑定尚未决定的
   `grantedPermission`」）。以规格 04 §9 建议为基础，**精确字节布局定案如下**
   （`\0` 为单字节 0x00；字段值编码：uuid/枚举按 UTF-8 串、nonce 按 base64 canonical、
   指纹按 uppercase hex；空值不存在——每个字段必选）：
   ```text
   ClientAuthTranscript =
     "LANREMOTE-AUTH-V1\0"
     sessionId "\0"
     serverDeviceId "\0"
     clientDeviceId "\0"
     serverNonce(base64) "\0"
     clientNonce(base64) "\0"
     certSha256(UPPER HEX) "\0"
     requestedPermission                     ← 末尾字段，无尾随 \0

   clientProof = HMAC-SHA256(accessKeyBytes, ClientAuthTranscript)
   ```
   ```text
   ServerGrantTranscript =
     "LANREMOTE-GRANT-V1\0"                   ← 域分隔：与 client 档不同域
     SHA256(ClientAuthTranscript 的 UTF-8 字节) 的 UPPER HEX "\0"
     grantedPermission                       ← 末尾字段，无尾随 \0

   serverProof = HMAC-SHA256(accessKeyBytes, "server\0" || ServerGrantTranscript)
     （注：`server\0` 前缀以规格 04 为准；「server|」写法作废。）
   ```
   要点：`grantedPermission` 只进 grant 档；客户端验证 serverProof 前先按自己的 transcript 与
   收到的 granted 重建 grant 档——**granted 被篡改即验证失败**。
   （精确字节串同时固化于 `AuthProtocol` 常量 + 确定性单测，两处不得漂移。）
2. **限流口径（D2/D3 裁定）**：failed auth limiter **只计「到达密码学校验且失败」**的事件
   （proof 重算不一致 / FixedTimeEquals 失败）。以下**不**计：帧格式违规、
   拒绝/超时/断连、审批拒绝、审批超时。原因：限流器防的是「猜密钥」；
   把协议层噪声计进去会让攻击者用垃圾帧给受害者 IP「刷封禁」（放大面），也会把
   用户自己的审批决定罚成攻击。其余参数照规格 04 §14：10 分钟窗口 / 5 次失败 → 拒 60s /
   每次失败 300–800ms 随机延时 / 成功清计数 / key 与 proof 绝不进日志。
3. **审批 v1 语义**：默认 `RequireLocalApprovalForUnknownController=true`；
   v1 = **每个新控制连接都要批**（不做运行期记住——规格 04 §15 的两个选项中取前者，
   保守侧）。`PendingApproval` 独立配额（初值：全局 3 / 单源 1），与连接准入配额分离；
   显示最小集 + 短关联码；请求不可变、原子终态、断连不发 token；UI 不可用 fail closed。
4. **时限初值（provisional，待数值实验后定案并回写）**：机器认证 10 s；
   人类审批 60 s（自获批面受理起算）。两者与既有五段 deadline 一同进
   数值实验批次（HANDOFF §18.4 C）。
5. **DoD 的两个可执行形式**：raw access key 绝不上网（协议里只有 nonce/proof/token，
   结构性保证 + 帧字段白名单测试）；auth success 后才能有 session
   （`SessionRegistry` 的注册入口只能被认证状态机在 `Authenticated` 状态调用）。

**Context**：规格 04 §9/§14/§15 与本 ADR 的关系是「细化 + 一处修订」：修订即 transcript 拆分
（04 原文是单 transcript 双 proof 形态）；其余为把论文级描述落成可实现的定案。
「双 proof 同 transcript」的问题：serverProof 与 clientProof 用同一 transcript 时，
serverProof 无法证明「服务端对**这条连接请求的权限**做过承诺」，因为 granted 没进任何 proof 的覆盖范围。
**Consequence**：`docs/PROTOCOL_AND_SECURITY.md`（工作副本）与规格 04 的差异以本 ADR 为准；
阶段 2 的帧实现按第 1 条演算；阶段 3 的限流实现按第 2 条接线（D2/D3 的否决面）。
**可逆性**：协议未上线（无外部对端），拆分可自由演进；一经两机验收即冻结。
**验证**：deterministic transcript（同输入字节级相同）；
correct / wrong key；modified cert fingerprint / modified permission（改任一字段必改 proof）；
granted 篡改 → 客户端必须拒绝（serverProof 验证失败）。
**实施时机**：M4 阶段 1–4 全程；数值（第 4 条）随数值实验定案。

---

### ADR-039 — M4 认证协议核心的层次归属：Transport（net10.0），非 Security
**日期**：2026-09-22（阶段 2 开工盘点；阶段 1 件重定位）
**Decision**：

1. M4 认证协议核心（`AuthProtocol` / `AuthTranscriptBuilder` / 后续认证帧）位于
   **`LanRemote.Transport/Auth/`**（命名空间 `LanRemote.Transport.Auth`，net10.0）；
   Security 不再含认证协议逻辑，继续只负责「必须 Windows 的东西」：
   DPAPI 秘密存储、自签名证书、访问密钥生成/存储。
2. **硬约束（实测，非判断）**：`LanRemote.Transport`（net10.0）引用
   `LanRemote.Security`（net10.0-windows）构建直接失败——
   `error NU1201: 项目 LanRemote.Security 与 net10.0 不兼容。支持: net10.0-windows7.0`
   （2026-09-22 本机实测）。而认证核心的**全部消费者都在 Transport 侧**：
   服务端状态机（阶段 3）接管 `ControlPreAuthHandoff`、客户端流程（阶段 4）在
   `TlsClientConnector`、验收器/App 均引用 Transport。
3. 先例一致性：`HelloFrame` / `CertificatePin` 已在 Transport
   （「传输层消息 + 传输层安全」的先例成立）。

**Context**：阶段 1 曾按 Security.csproj 自述职责（「访问密钥、认证挑战」）把 Auth 核心放
Security（`35506b5`）；阶段 2 开工盘点发现 TFM 约束后重定位——机械搬移（`git mv`）+
命名空间改名，**协议字节与测试语义零变化**（黄金向量不变）。
**Consequence**：Security.Tests 的 34 条认证测试迁至 Transport.Tests；
未来 M5 视频 attach 的 proof/帧同规则（Transport）；「Security 引用 Transport」的
反向依赖被明确禁止（会构成层次倒置）。
**可逆性**：低风险——纯物理位置；若将来 Security 改多目标（net10.0;net10.0-windows）
再议（当前不做，M1 决策不动）。
**验证**：NU1201 实测（Transport→Security 引用实验，已回退）；重定位后全量测试总数不变。

---

### ADR-040 — 认证帧的 canonical 判定式与拒绝分类（M4 阶段 2 落地）
**日期**：2026-09-22（M4 阶段 2）
**Decision**：

1. **canonical 的唯一定义 = round-trip 逐字符相等**：`CanonicalBase64` / `CanonicalHex` /
   `CanonicalGuid` 一律实现为「宽松 decode → 重新 encode → Ordinal 比较」。
   - **base64**：标准字母表（含 `+` `/`）、必须带正确填充、长度为 4 的倍数；
     非规范尾部位拒绝（`"AB=="` 拒）；`"AA++"` / `"AA//"` **是**合法 canonical
     （`+` `/` 属标准字母表，实测纠正过先入为主的误判）。
   - **HEX**：只接受 `Convert.ToHexString` 的输出形状（uppercase）；**长度语义不在编码层
     强制**（31 字节的大写 hex 形状合法），归帧层——有显式测试锁定这条边界防漂移。
   - **GUID**：只接受 `"D"` 格式（8-4-4-4-12）；大写 / 花括号 / 无连字符 / 前后空白全拒。
     实测：`Guid.TryParseExact("D")` 容忍大写**和前后空白**——round-trip 是唯一收窄者。
2. **两端字节一致的机制保证**（与 ADR-038 配合）：帧解析只产出强类型
   （`Guid` / `byte[]`）；transcript 构造器**不接收 string**——非规范形式进不来，
   「对端编码差异进 transcript」整类问题在类型层面被消灭；canonical 校验是第二道
   （**拒绝**而非规范化）。
3. **跨帧载荷的拒绝分类**：`AuthJson.TryDeserializeStrict` 在结构校验后做可选
   type 提示预读（`expectedType` / `wrongTypeCode`；扫根对象第一层 `type` 字符串）——
   结构合法但类型是别的帧时报 `*-wrong-type` 而非 `*-malformed-json`。
   预读**不影响接受与否**（主反序列化仍是唯一权威）。
4. **拒绝短码只用于本地日志、永不下发对端**（既有纪律的帧级重申）：五个
   `challenge-*` / `response-*` / `success-*` / `approval-pending-*` / `auth-failed-*`
   前缀族一律短码、不含输入内容；对外失败只有 `authentication_failed`（语义为空）。
5. **`AuthenticationFailedFrame` 提前落地**（阶段 2 附带件，记录在案）：
   `authentication_failed` 早在 `AuthProtocol` 词汇表中（阶段 1），本帧只是其唯一实现
   （static class、零字段、永不携带原因码）——阶段 3/4 的公共前置件，
   避免届时手写第二套 JSON 解析面。
6. **`clientName` 硬化（本地显示面策略，非规格常量）**：非空 / ≤64 字符
   （`ClientNameMaxLength`）/ 无控制字符 / UTF-16 良构（孤立代理拒）。名字最终出现在
   被控端**本机审批面**上；放宽需构造器与解析两处同步。

**Context**：阶段 2 的 5 个帧全部出现在未认证阶段（攻击面最敏感）；每一项宽松
（重复字段 / 未知字段 / 大小写 / 注释 / 尾逗号 / 尾随内容 / 非法 UTF-8 兜底）都必须显式拒绝
且逐项有测试（`HelloFrame` 先例 + ADR-037 第 8 条）。帧上限沿用 pre-auth 4 KiB（ADR-033）。
**Consequence**：帧层所有拒绝路径都有精确本地短码 → 阶段 3/4 状态机可直接用于本地诊断；
`wrong-type` 预读对恶意对端**无观测差异**（对端只见 generic 失败），纯本地可观测性收益。
**可逆性**：canonical 判定式**不放宽**（放宽 = 重新引入两端不一致面，危险）；
`clientName` 上限单点可改。
**验证**：3 个 canonical 测试文件（往返扫掠 1..96 + 各拒绝组）+ 5 个帧测试的逐项拒绝矩阵 +
跨帧交叉拒绝（真实序列化字节互喂）+ 变异 ×4；其中 M3 当场抓到并修复一条
「手抄 base64 坏字面量」造成的**假测试**（测试改用 BCL 编码器构造邻界值）。
**实施时机**：已完成（提交 `21a8829`；HANDOFF §18.7；Transport 226→460，全量 877 PASS）。

---

### ADR-041 — 认证状态机的合同级决定（M4 阶段 3 历史记录）：登记时点、密钥触碰时点、窗口后置校验、限流语义、审批单读者
**日期**：2026-09-22（M4 阶段 3）
**状态**：第 3 条旧读后检查与「排队不计入」、第 5 条未定义优先级的竞速解释已由 **ADR-042 取代**；第 1/2 条的本地写出与密钥所有权边界由 ADR-042 澄清。其余不冲突的限流、权限校验与短码规则保留。用户已拍板按状态机接受时刻、`elapsed >= budget` 拒绝，不再等待该合同的外部裁定。
**Decision（历史，须连同上述取代关系阅读）**：

1. **登记的精确时点 = 「`auth_success` 本地写出成功」**：`SessionRegistry.Register` 只在
   success 帧经 `FrameWriter` 本地写出且未抛异常之后调用；写失败（IOException / EndOfStream /
   ObjectDisposed）→ `auth-success-not-delivered` → **不登记、不保持、认证不成立**。
   **澄清**：这个历史短码表示本地写出失败；本地写成功不证明远端已经收到或接受，审批完成后的写出另有预算（ADR-042），不是远端交付确认。
   这是 DoD「auth success 才能有 session」的时序化可执行形式（与 ADR-038 第 5 条配套：
   注册入口 internal + 只有从 `Authenticated` 状态可达的路径调用它）。
2. **访问密钥的触碰时点 = response 帧通过严格解析之后**：challenge 生成、限流拒绝、
   帧级格式违规全程**不触碰** `IAccessSecretStore`；密钥整个 `RunAsync` 只加载一次、
   `finally` 清零。理由：未认证对端不得用任意垃圾帧驱使 DPAPI / 密钥路径工作
   （攻击面最小化，「格式合规才配见秘密」）。
3. **机器窗口的后置校验（已由 ADR-042 取代）**：**2026-09-22 复核结论：旧实现的 CTS 在密钥加载/校验前已释放，且审批计时晚于 gate 调用；「读后检查足够」与「排队不计入」已被真实 TLS 反例证伪（HANDOFF §18.9）。修复后的合同由 ADR-042 定案，不再标作等待评审。** 以下仅保留旧决定原意，不能作为当前实现满足合同的证明：读完 response 后**立即**检查窗口令牌 `IsCancellationRequested`
   ——旧文写「response 校验完成 ≤ MachineWindow」（又称读恰好压线完成也算超时）。
   旧解释认为没有它，窗口边界会漂移成「窗口 + 解析耗时」；审批窗口自请求提交给审批面起算（ADR-038 第 4 条），排队等待不计入。
   **现行替代**：接受必须满足 `elapsed < budget`，不能只看取消令牌；机器窗口覆盖至 MAC 判定的最终接受检查，审批窗口在调用 gate 前起算，包含同步前缀、排队、UI 调度与展示。
4. **限流实现语义（ADR-038 第 2 条的落地细化）**：
   ① **封禁到点即放行**（`IsBlocked` 不因窗口内保留的 ≥5 条旧记录继续拒）；到期后
   **任一**新失败若窗口内累计仍 ≥5，立即再封 60 s——净效果：持续攻击被压到
   「每 60 s 一次尝试」节奏，直到旧记录滑出 10 分钟窗；
   ② **成功清空该 IP 全部记录（含封禁）**（`RecordSuccess` 直接移除条目）；
   ③ 条目懒清理（只在 `IsBlocked` / `RecordFailure` 触达时回收；同子网地址空间有界）；
   ④ 限流是**只读前置**：被罚 IP 连 challenge 都不发，且「被拒」本身不写任何计数。
5. **审批三路竞速与单读者复用**：审批阶段 = 「决定 / 客户端活动（1 字节读）/ 窗口」三路
   竞速；「客户端活动」这路读同时承担**断连侦测**与**成功后的保持读**——决定胜出时该
   任务原样交接（handoff）给 `HoldUntilDisconnectAsync`，**全程只有一个读者**消费流
   （历史表述为「首个终态胜、其余作废」；不能据此让 `WhenAny` 返回顺序决定授权。现行优先级、决定校验后复核及已观察活动边界见 ADR-042）。
6. **审批决定的校验式**：`Approved` 必须携带 `granted` 且通过 `IsGrantable`——
   降级（control → view）永远可授 / 升级（view → control）仅当请求为 control /
   未定义枚举拒（**不依赖枚举序数**）；请求 ID 不回指、缺权限、越权、未知终态 →
   统一 `auth-approval-invalid`。5 个显式终态（Approved / Denied / TimedOut / Cancelled /
   Unavailable）→ 5 个本地拒绝码一对一；**无 bool、无「没有 handler = 同意」**
   （UI 不可用必须 Unavailable，fail closed）。
7. **短关联码（本地显示面策略，非规格常量）**：
   `SHA256("LANREMOTE-APPROVAL-CODE-V1" ‖ \0 ‖ sessionId ‖ clientNonce)` 前 3 字节大写 hex
   （6 字符）。输入两端各知且 clientNonce 已进 proof 绑定；**仅作人工关联、不构成认证**——
   任何展示与「自称」字段（clientDeviceId / clientName，discovery 数据未认证）同等对待。

**Context**：阶段 3 是 ADR-037（衔接）+ ADR-038（协议定案）+ ADR-040（帧）三层决定在
服务端状态机的汇合落地；本 ADR 固化实现中**超出三层既有文本**的语义决定——每一条都
直接映射一个具体失败模式：「本地 success 写出失败却登记了 session」（不等于能证明远端收到）/「垃圾帧触发密钥
路径」/「窗口因解析耗时漂移」/「把 60 s 封禁误读成 10 min 锁死或反之」/「审批期两个读者
抢一条流」/「越权授予」/「短码被当成认证凭据」。
**Consequence（该项被 ADR-048 取代）**：旧计划复用 `SessionRegistry.TryGetSessionToken`；共享视图不满足注销与附着的原子性，M5 改用登记表内部验证并绑定，不再导出 token。
`ControlAuthOptions` 是**测试缩放形态**（全部时限可调）——产品默认值单点定义在
`AuthProtocol`，不得被测试缩放误导；阶段 4 客户端的 serverProof 独立重算与高层连接所有权见 ADR-043。
短码可计算不等于批准前双端展示已接线。阶段 3/4 时过程 API 尚未定案；2026-09-23 已按 ADR-044 实现非秘密通知与验收器接线，人工双端展示仍待验收。步骤 19 的 Transport 文案与会话边界见 ADR-043。
**可逆性**：未上线（无外部对端）——除第 3 条（放宽 = 弱化 deadline 语义）与第 6 条
（fail closed 形态）外均可演进；一经两机验收冻结。
**验证（历史基线，不作为当前时限合同的证明）**：25 条 `ControlAuthSessionTests`（真实回环 TLS + 真实 TransportHost 全链）+
变异 ×4（M1 于提交后重放核实 = 4 红精确：错钥 / 篡改权限 / 篡改指纹 / 失败延时边界）+
当时全量 902 PASS / 0 FAIL。
**历史落地记录**：提交 `760e950`；HANDOFF §18.8；Transport 460→485，当时全量 902 PASS。后续反例与修复以 ADR-042/043 及对应定向证据为准。

---

### ADR-042 — 服务端单调安全接受 deadline、审批终态与有界迟到 key 所有权
**日期**：2026-09-22
**状态**：已定（用户已拍板按状态机接受时刻；不是 UI 点击时刻，不再等待该语义的外部裁定）。
**关联**：取代 ADR-041 第 3 条旧读后检查/排队不计入与第 5 条含糊的竞速解释；澄清其第 1/2 条的本地写出与密钥所有权。ADR-038 的双 transcript、限流计数口径、审批 fail closed 不变。

**Context**：真实 TLS 反例已证明：response 读后看一次 CTS，不能约束后续密钥加载和 MAC；在 gate 返回 awaitable 后才计时，会漏掉同步前缀与 UI 调度。取消回调延迟也不能成为延长授权窗口的理由。修复目标是「过期结果不得被安全接受」，不是以普通 CTS 承诺任意本机代码都能准点中断。

**Decision**：

1. **截止判据 = 单调时间 + 状态机接受点**。`AuthenticationDeadline` 用同一 `TimeProvider` 的
   `GetTimestamp` / `GetElapsedTime` 与固定起点计算预算；**`elapsed >= budget` 一律拒绝**，
   只有 `elapsed < budget` 才可能接受。timer / token 只负责唤醒合作式等待，不能替代接受点的单调复核；
   timer 未派发也不能接受已过期结果。UTC 只供审批到期时间展示，不参与授权裁决。
   构造 timer 前已消耗的时间必须扣除，零预算/初始化期间到点不等待 timer。
2. **机器窗口从 challenge 写出前起算，持续到 proof verdict 的接受点**（默认 10 s）。
   覆盖 challenge 写出、response 读取、严格解析、loader 准入等待、store 同步前缀/异步加载、
   transcript/HMAC 与 `FixedTimeEquals`；读取后、解析后、密钥加载后及 MAC 判定后均复核。
   **最后复核在 `RecordSuccess` / `RecordFailure`、进入审批之前**：过期时不提交正确/错误 proof 的结论，
   不清空或递增 limiter、不进入审批、不发 success、不登记。及时确定的错误 proof 才计一次失败；
   随机失败延时及 generic 失败帧是收尾，不延长安全接受窗口。调用方取消优先并保留取消语义；
   store 自己取消/出错且 caller 与窗口仍有效时是 `auth-key-unavailable`，不是伪装成机器超时。
3. **秘密加载有独立且有界的所有权**。严格解析 response 前不触碰 store；每次认证至多加载一次，
   store 返回独立 key 数组。`ControlAuthContext.SecretLoader` 跨该 context 的连接共享，
   **同一 context 最多一项尚未真正终结/清理的实际 store 工作**，不是每连接各建一个 loader，
   也不声称是进程全局上限。准入等待消耗该连接的机器预算，可取消且不启动新的 store 工作。
   - 正常返回：key 所有权交给认证会话，loader 释放准入；会话在 `RunAsync` 的 `finally` 清零自己的 key。
   - 等待取消但 store 不合作：等待者可以退出，**唯一 late-result owner 继续持有准入**；
     late success 的 `AccessKeyBytes` **先清零，再释放名额**，late fault/cancel 必须被观察并释放名额。
     不因「不再等待」就提前归还名额；永久不结束的 store 占住该 context 的一个名额，不能不断积累实际加载任务。
   - 不用 `Task.Run` 包裹同步 store 来伪造硬时限；有界准入不等于能强杀 DPAPI。
4. **审批从调用 gate 前起算，调用即受理**（默认独立 60 s）。待批配额仍为独立全局 3 / 单源 1；
   `approval_pending` 本地写出有自己的写预算，失败不得提交 gate。
   之后在调用 `RequestApprovalAsync` 前建立 deadline，包含 gate 同步前缀、排队、Dispatcher 调度、
   展示及人类等待；**不存在不计时的前置队列**。`ExpiresAt` 仅为显示用 UTC 提示。
   gate 必须快速返回 awaitable，取消回调也必须快速非阻塞；UI 不可用仍为 `Unavailable`。
5. **审批终态优先级固定为 `caller 取消 > 截止 > 已观察客户端活动 > 决定`**。
   `WhenAny` 只唤醒，不按参数顺序或哪个 Task 先返回来批准；先复核 stop 条件，再读取并校验决定，
   **校验 RequestId、Outcome、GrantedPermission 后，在最终接受前再次按同一优先级复核**。
   gate fault/cancel 的异常路径也要复核，不能用 `Unavailable` 掩盖 caller 取消或已发生的超时/活动。
   已观察的 0 字节/读故障是断连，>0 字节是越界数据；均不能发 token。只有合法 `Approved` 且
   grant 不高于请求权限才接受；迟到决定不能反转已定终态。UI 点击及时但状态机接受已到点，仍拒绝。
6. **单读者与清理边界**。审批期唯一的一字节活动读在批准后原样交给保持阶段；
   不为了复核、清理或探测「是否还有字节」新增读取。失败/取消时观察迟到任务异常，关闭流仍由 Host 最终负责。
   终态确定后的 gate 取消是合作式清理，不是重新裁决；取消回调抛错不得覆盖既定结局。
   这里的「活动优先」只指接受检查时**已经观察到的活动任务状态**，不保证识别尚未完成的网络读，
   也不保证批准之后的断连永远先于 success 写出被发现。
7. **审批接受、success 本地写出、会话登记是三个不同边界**。审批终态接受后，serverProof/token 构造及
   success 写出不再受已结束的审批窗口约束；success 使用 `FrameWriter` 的独立写预算
   （当前传入 `_timeouts.LengthPrefixTimeout`，覆盖整帧本地 write/flush）及 caller token。
   本地写失败/该写预算取消 → `auth-success-not-delivered`，不登记、不保持；本地 write/flush 成功后才登记。
   **本地写成功不证明远端已经收到、验过 serverProof 或接受会话**，协议没有交付确认。
   不把「接受 deadline 有效」扩大为「deadline 内一定写完 success、完成登记、远端收到或全部清理完成」。
8. **普通 CTS 的能力边界不变**。同步 DPAPI、gate 同步前缀、阻塞的取消回调都不能被普通 CTS 硬中断；
   返回后仍须拒绝迟到结果，但永不返回/永远阻塞的本机代码没有准点返回保证。
   `AuthenticationDeadline.Dispose` 不主动取消，避免释放时额外同步执行 gate 回调；
   回调异常隔离也不等于能消除阻塞。以上边界不得被测试替身的合作行为掩盖。

**源码依据**：`src/LanRemote.Transport/AuthenticationDeadline.cs:13`、
`src/LanRemote.Transport/AuthenticationSecretLoader.cs:17`、`src/LanRemote.Transport/ControlAuthContext.cs:59`、
`src/LanRemote.Transport/ControlAuthSession.cs:180`（机器窗口）、`src/LanRemote.Transport/ControlAuthSession.cs:410`（审批窗口）、
`src/LanRemote.Transport/ControlAuthSession.cs:467`（最终复核）、`src/LanRemote.Transport/ControlAuthSession.cs:605`（优先级）、
`src/LanRemote.Transport/LocalApprovalGate.cs:102`、`src/LanRemote.Transport/FrameWriter.cs:25`。

**定向证据与证明边界**：
- `outputs/m4-deadline-review/final-mutation-1..5-build.log` 均构建成功，随后 `final-mutation-1..5-tests.log`
  命中测试断言；不是把编译失败当作防线命中。逐项红/绿为：

  | 编号 | 定向防线 | 变异红 / 仍绿 |
  | --- | --- | --- |
  | 1 | 机器 MAC 后最终接受检查 | 2 / 2 |
  | 2 | 精确到点、timer 未派发也算过期 | 1 / 0 |
  | 3 | 审批决定校验后的最终接受复核 | 1 / 1 |
  | 4 | late secret 清零后才能释放名额 | 1 / 0 |
  | 5 | 取消等待不能放行仍被卡住的 store 准入 | 1 / 0 |

  `final-mutation-1..5-restored.log` 是三个目标源文件的恢复哈希检查，不冒充回绿测试日志。
- `tests/LanRemote.Transport.Tests/ControlAuthDeadlineTests.cs:16`（机器密钥/MAC 边界）、
  `tests/LanRemote.Transport.Tests/ControlAuthDeadlineTests.cs:114`（gate 同步前缀）、
  `tests/LanRemote.Transport.Tests/ControlAuthDeadlineTests.cs:138`（按接受时刻裁决）使用真实回环 TLS / Host，
  可控单调时钟允许故意不派发 timer。`tests/LanRemote.Transport.Tests/AuthenticationSecretLoaderTests.cs:127`
  与 `tests/LanRemote.Transport.Tests/AuthenticationSecretLoaderTests.cs:281` 是 loader 所有权与有界准入单元测试，不是 DPAPI 强杀证明。
- `tests/LanRemote.Transport.Tests/ControlAuthCleanupTests.cs:110` 是预先完成活动任务的优先级判定器测试，
  `tests/LanRemote.Transport.Tests/ControlAuthCleanupTests.cs:229` 是受控 I/O 的写取消映射；
  不冒充真实 TLS 竞速/网络写超时实测，也不声称直接观察到了私有包装读任务的异常清理。
  `tests/LanRemote.Transport.Tests/ControlAuthCleanupTests.cs:157` 的真实 TLS 迟到 gate 决定只证明已定拒绝不会被反转。

**Consequence / 可逆性**：旧「读后检查足够」「排队不计入」不得回退；清晰区分安全接受、合作式取消、
本地写出和资源回收。当前记录只引用定向证据，最终全量 Debug/Release 数字与收口结论留给 `HANDOFF.md`，本 ADR 不代填。

---

### ADR-043 — 客户端高层独占连接、实际 presentedPin、权限 serverProof、时限与内存清理边界
**日期**：2026-09-22
**状态**：已定（M4 阶段 4 Transport 合同；不等于验收器 UI 或阶段 5 已完成）。
**关联**：落实 ADR-028 的实际 TLS 指纹绑定、ADR-038 的双 transcript/grant proof、ADR-040 的严格帧解析；
客户端采用 ADR-042 的单调安全接受原则，但两端计时起点不同，不能把本地等待窗口当作远端审批时刻。

**Decision**：

1. **高层连接入口独占整个认证过程**。`ControlClientConnector.ConnectAndAuthenticateAsync` 接收冻结的
   `ConnectionTarget`，内部建立 TLS、写 hello 并认证；不接收调用方已持有的流，不交出中间 `TlsConnection`。
   所有验证与接受检查通过，且返回 `AuthenticatedControlSession` 时，才向高层公开成功并移交独占连接。
   失败/取消关闭仍由本次调用拥有的连接；调用方取消保留 `OperationCanceledException`，底层 TLS
   `AuthenticationException` 不冒充 serverProof 失败。既有低层 `TlsClientConnector` 不因此变成完整访问密钥认证入口。
2. **公开会话不暴露 stream、sessionToken 或输入发送能力**。公开信息仅含冻结身份、已验证权限、SessionId、
   ShortCode 及释放能力；stream/token 只供未来 Transport 消费方内部独占使用，不得另起并行读者。
   `Dispose` 清零私有 token 并关闭连接，允许重复调用；`videoAttachExpiresInMs` 只作为 internal 提示，
   不是已验证的本地权威 TTL，M5 消费时仍须施加本地上限。M4 不发送输入，也不新增输入 API。
3. **必须绑定实际 `presentedPin`，不能偷换为 expectedPin**。TLS 校验回调从实际证书 DER 计算并捕获指纹，
   冻结身份的 `PinsMatch` 必须成立；challenge 的 `serverDeviceId` 必须匹配冻结目标，
   challenge 指纹必须与 `Identity.PresentedCertSha256` 做定长比较。不匹配时在发送 response 之前拒绝。
   ClientAuthTranscript 的证书字段明确取 `PresentedCertSha256`，不回读 discovery，不以 challenge 自称或
   期望 pin 替代实际 TLS 事实；其余字节布局仍精确遵循 ADR-038。
4. **权限门禁与 serverProof 门禁独立且缺一不可**。仅允许保持请求权限或 `Control → ViewOnly` 降级，
   `ViewOnly → Control` 即使附有有效 MAC 也拒绝；未定义权限由严格解析拒绝。
   使用**本次本地保存的 ClientAuthTranscript + 实际收到的 grantedPermission** 重建 ServerGrantTranscript，
   按 ADR-038 的 `HMAC-SHA256(key, UTF8("server\0") || ServerGrantTranscript)` 重算并 `FixedTimeEquals`。
   不能用 requestedPermission 替代实际 grant。MAC 后再次检查接受 deadline，再提交 proof 判定；
   会话构造后还要复核，失败则清理未移交会话。验证成功前不把 token 交给会话消费者。
   serverProof 不符的本地短码为 `client-server-proof-mismatch`，固定展示文案精确为
   **「远端身份验证失败，可能是错误密码或伪造设备广播」**（常量本身无句末标点）。
   认证异常不携带远端载荷、key、proof、token 或可能包含它们的内层异常；本地短码不发送给对端。
5. **客户端时限按协议事件的原始单调起点计算，`elapsed >= budget` 拒绝**。
   - hello 使用自己的写预算；**hello 本地写完后**起算默认 **10 s machine**，覆盖 challenge 读取/解析/身份校验、
     response 构造/写出，以及首个合法 pending 的接受或直接 success 的完整权限/MAC/会话接受检查。
   - challenge 的 `expiresInMs` 仅收窄：以**收齐 challenge 的时刻**为起点，解析耗时也扣除，
     有效余量为本地 machine 与该提示窗口余量的最小值，父窗口从不重置；远端给 15 s 或更大也不能延长本地 10 s。
   - response 写完后才接受 pending；**首次严格解析合法的 pending 被状态机接受时**记录起点，
     开始独立默认 **60 s approval**，不链接已经结束的 machine/challenge token。
     **duplicate pending 立即拒绝**，不忽略后继续等待、不续期；pending 接受不是远端 gate 受理时刻的证明。
   - 审批期等首个长度前缀可用整个剩余 approval 预算，不被默认短前缀超时误杀；一旦开始 payload，
     仍取 payload 分段预算与窗口剩余量的最小值。机器期前缀同样取分段预算与窗口余量的最小值。
     分段读取、解析、身份/权限校验与 MAC 后均复核，timer/token 只是合作式唤醒，不能把收到字节当作已及时接受。
6. **认证只消费首个终帧**。允许 challenge → response → success，或 challenge → response → 一次 pending → success；
   也在相应等待阶段接受严格的 generic failure 并拒绝。已消费位置上的乱序/重复 challenge、duplicate pending、
   非法类型或格式均拒绝；**不为探测未来的第二个 success 再读一帧**。
   返回成功会话后的后续帧由未来协议消费方处理，不能声称认证层已经检测所有未来重复终帧。
7. **key 在首次 await 前复制；清理只承诺产品实际拥有且可擦写的缓冲区**。
   调用方提供 16 字节 key，高层入口同步复制后只读私有副本，不持有调用方可变输入；
   成功、失败、取消都在退出时清零私有副本，**不清零调用方数组**。
   客户端清理收到的认证 payload、response 序列化数组、临时 clientProof/expectedProof、
   自有 transcript 字节；success 帧内 token 在失败或复制入会话后清零，会话私有 token 在 Dispose 时清零。
   - `AuthSuccessFrame.TryParse` 的 token 解码使用 **32 字节 stack buffer**，canonical 重编码使用栈上 char buffer；
     仍要求解码恰好 32 字节且 round-trip 逐字符相等，不能只检查 BCL 可解码或长度。
     栈上临时 token、canonical chars 及临时 serverProof 在 `finally` 清理；合法 `+` / `/` 不误拒。
   - `FrameReader.ReadPayloadAsync` 在 EOF、I/O、取消及其他异常时清零**整块尚未交付的自有 payload buffer**，
     然后原样重抛；成功时交出原数组，由调用方负责清理。上层读完后若 deadline 复核失败，也清理尚未移交的 payload。
   - **不保证擦除 DTO 的不可变 string、JSON/编码器/运行时内部副本或 TLS 内部副本**。
     清零一个数组不等于全进程无残留；也不把客户端上述清理扩大为服务端 success 序列化、FrameWriter
     内部帧副本全部已擦除的保证。stack buffer 是缩小可控临时副本，不是消灭所有副本的证明。
8. **交付与 UI 接线边界**。步骤 19 的 Transport 异常类型、短码和固定 serverProof 文案已提供；
   **阶段 4 当时验收器展示尚未接线**，本 ADR 不声明产品 WPF UI 或阶段 5 人工收口完成。
   阶段 4 当时 ShortCode 只随成功会话公开；「两端都能算」不等于「批准前双端已展示」。
   **阶段 4 时批准前双端短码展示过程 API 尚未定案**；2026-09-23 已按 ADR-044 实现非秘密通知及验收器接线，不能靠提前泄露连接/token 代替。人工 GUI 与两机展示是否通过仍由后续实际验收确定。

**源码依据**：`src/LanRemote.Transport/ControlClientConnector.cs:23`（高层入口）、
`src/LanRemote.Transport/ControlClientConnector.cs:121`（窗口）、`src/LanRemote.Transport/ControlClientConnector.cs:223`（challenge）、
`src/LanRemote.Transport/ControlClientConnector.cs:271`（回复顺序）、`src/LanRemote.Transport/ControlClientConnector.cs:322`（权限/proof）、
`src/LanRemote.Transport/ControlClientConnector.cs:375`（分段读取）、
`src/LanRemote.Transport/TlsClientConnector.cs:55`、`src/LanRemote.Transport/AuthenticatedControlSession.cs:10`、
`src/LanRemote.Transport/ControlClientAuthenticationException.cs:8`、
`src/LanRemote.Transport/Auth/AuthSuccessFrame.cs:181`、`src/LanRemote.Transport/FrameReader.cs:118`。

**定向证据与不得扩大的范围**：
- 首轮 `outputs/m4-deadline-review/client-mutation-10-summary.log`：01–09 共 **9 项**真实产品变异，
  全部在构建成功后命中目标断言；变异 **24 红 / 16 绿**，逐项恢复 **0 红 / 40 绿**。
  防线包括 serverProof 比较、实际 grant 绑定、overgrant、challenge device/实际 pin、MAC 后 deadline、
  duplicate pending、token canonical round-trip、读失败 buffer 清零。
- 首轮**保留测试 clock 观察点改进**：到期事件移到权限/MAC 前检查取样之后，本次仍返回旧采样；
  删除 MAC 后检查也不能一并删掉测试的到期事件。该白盒具名阶段顺序以后若增加取时点必须复核，
  不应把这项测试改进当作待恢复的产品变异。
- 第二轮 `outputs/m4-deadline-review/client-mutation-rerun-summary.log`：11–19 同样 **9 项**，
  同样 **24 红 / 16 绿**、恢复 **0 红 / 40 绿**；前后 `src/tests` 被跟踪或未忽略的 **181 个文件**
  文件集及 SHA-256 清单一致，四份指定测试未改。两轮红绿数均为**测试执行次数，不是去重用例数**。
- MAC 后单点变异中，错误 proof 的两例由 timeout 错变 proof-mismatch 而红；正确 proof 两例仍被会话构造后复核拒绝，
  不能把它们称为 MAC 后单点独立覆盖。实际 pin 变异证明「错误 challenge pin 在 response 前被拒」，
  不声称在正常 `PinsMatch` 连接中用这项实验区分了数值相等的 expectedPin 与 presentedPin 来源。
- buffer 清零变异的四例失败来自可观察原数组的自定义 Stream；不是 TLS 内部缓冲或 DTO string 擦除证明。
  duplicate pending 变异保留原窗口，只证明立即拒绝的合同，不能外推成已单独验证所有窗口起点变异。

**Consequence / 可逆性**：高层调用方只消费已认证会话，不参与中间连接读写；任何放宽 pin、权限、proof、
接受 deadline 或所有权边界的改动都须重新评审。以上是 Transport 合同与定向证据，不是最终全量验收；
**最终全量 Debug/Release 数字、阶段 5 与两机验收结论留 `HANDOFF.md` 记账，本 ADR 不预写通过结论**。

---

### ADR-044 — 阶段 5 非秘密通知、有界审批与验收证据生命周期

> **后续状态（2026-09-24，ADR-045）**：旧双机尝试两端 GUI 已运行，但发现失败阻断认证，认证未开始；不能继续概括为「GUI/双机未运行」，也不能记为通过。下文「待执行」与自动化证据保留为 2026-09-23 阶段记录，不外推至当前修复版；测试与交付详情见 HANDOFF §18.13，不预填全量结果，不宣称网络恢复。

**日期**：2026-09-23。**状态**：已实施并完成自动化验证；人工 GUI/两机验收待执行。用户授权自主推进，不再等待外部模型评审。

1. **客户端通知不是认证成功**。`ControlClientAuthOptions.ApprovalPending` 在首个合法 pending 接受并建立本地审批窗口后同步调用一次；数据仅 SessionId、ShortCode、AcceptedAtTimestamp（连接时钟）、ApprovalWindow。回调必须快速返回，只更新有界内存；执行耗时计入原窗口，前后复核取消/单调截止。异常不透传消息或 inner exception，固定 `client-approval-notification-failed`；取消/截止优先。不能用普通 CTS 承诺强杀永不返回的本机回调。直接 success、错误/重复 pending 不新增通知。
2. **本机审批收件箱独立且限容**。每 run 新建 `LocalApprovalInbox`，默认 3，UI 拉当前快照而非逐请求排 Dispatcher。RequestId + 每条独立 Generation 标识决定；只接受合法批准（可降不可升）或拒绝。Stop/Dispose 不可重启、清除所有待批项；取消不等待 Dispatcher。TCS 异步 continuation、锁外完成、注册非阻塞注销。收件箱接受决定不等于状态机授权；最终安全接受仍遵循 ADR-042。
3. **密钥显示的工作拥有者先于 context 清理结束**。仅活跃 Host 显式确认后可查看，同一个真实 vault/store；一次附属 WorkLease，读取未归还不得再起。取消后等迟到工作返回、数组清零、故障记账再释放 context/结算，不以超时冒充 join。显示限 15 秒（原始单调起点），失焦/隐藏/停止清空显示；输入 PasswordBox 不保存不日志，caller 数组 finally 清零。正常查看不轮换；初始化/非法字段修复遵循既有 store 合同。不可变字符串/WPF 副本无法保证擦除，Dispatcher 不响应也无法保证即时清屏。
4. **双端角色真实接线**。Host 共享 auth context、store、限流器、独立 3/1 待批配额与 registry，消费 handoff 并 await auth。成功 Run 返回表示保持已经结束，不代表当前在线。Client success 在低层 TLS 之前分流到高层完整认证，验证 serverProof 后持有至少 5 秒并同步 Dispose；无安全凭据源/占位 DeviceId 的 headless success 为 UNMET，不关闭身份检查、不加 key argv、不自动审批。
5. **证据不填造**。仅一个 Host 级 100ms sampler，活动跟踪按连接上限有界。成功证据要求真实正向跨度至少 4 秒、样本间隔及结束距最后样本不超过 500ms、结束已注销、没有 Host 强关；这是验收配置，不是产品默认时限，也不能证明采样间隙内全部连续状态。缺样本/无成功/强关为 UNMET。SessionId 配对成功链，旧负例保留四元组；最后 registry=0 不能证明曾经保持。保持读结束不区分 EOF/RST/越界输入，须配客户端主动 Dispose 证据。
6. **先清理、后唯一结算**。Host/client 共用幂等 `AcceptanceRun.Complete`；operator abort 优先作废，后台/日志/清理故障不得被 PASS 覆盖。日志后续 append 失败也算不可用；footer 自己失败会修正返回值并在内存标废。180 秒 GUI Host 预定窗口可自然结算，提前停止/关窗仍 INVALID_RUN；关窗超过等待提示预算不强杀任务。附属工作/取消回调永不返回时继续等待，不声明清理完成。

**自动化证据**：`ControlClientApprovalNotificationTests` 真实 TLS；Acceptance.Tests 的真实 STA Dispatcher 卡住时后台取消、实际 Host handler + 高层客户端 TLS 四场景、WorkLease/RunState、日志/footer 与采样判据回归。8 项变异均构建成功后运行期变红、恢复同测试通过（7 项日志直接显示断言失败；M02 原异常外泄由夹具清理重抛，遮蔽原断言诊断，不能记作直接断言证据；M03/M05 为复合变异）。原始日志在 `outputs/m4-stage5-validation/mutations/`。最终全量数字与人工验收状态见 HANDOFF §18.11。未改变线上帧、密码学、Transport 单读者、产品 App 或系统网络配置。

---

### ADR-045 — 沿用现网自动发现：不改网、WFD 窄例外、receiver 去重与干净交付

**日期**：2026-09-24。**状态**：原始需求重申；下述发现修正、旧改网入口停用及打包约束已实现，不代表双机认证验收通过。

**覆盖关系**：本 ADR **取代 ADR-026 的产品改 IP 计划及其增补**，**停止 ADR-025 / ADR-035 网络修改计划的默认落地**，取消 ADR-036 对旧验收脚本「维持现状」的例外。历史正文保留，但不得据此恢复改网入口或继续按旧 M10 计划施工。ADR-024 的只读诊断要求、ADR-035 的**主进程永不提权**及既有认证安全原则继续有效。

**Context**：用户重申的是原始需求「沿用现有局域网自动发现」，不是新增一套组网功能。应用应适应现网，不能通过改变用户整机网络来迁就发现条件。旧 M4 双机尝试两端已经运行 GUI，但发现失败阻断认证，认证未开始；GUI 渲染日志不能证明完整布局、DPI、密钥、审批或认证交互通过。当前本机热点接口为 **if13，`192.168.137.1/24`**；原上游 `172.100.166.220` 仍非 RFC1918，旧「唯一活跃公网网卡」说明不再代表当前现场。

**Decision**：

1. **所有路径不改网，权限与可逆性不构成豁免**。
   - 不修改 **DHCP、IP、Gateway、DNS、Routes、热点/ICS、网络类别、防火墙**；覆盖启动、发现、诊断、准备、修复、退出与 Undo。
   - 用户确认框、管理员权限、UAC、「实验环境」或「以后可以撤销」都不能将这些修改变成允许操作；不通过自动恢复或补救流程继续改网。
   - UI / 发现 / 传输 / 诊断主进程始终不提权，诊断只读；不要求新增产品 UI 或工具能力。自动发现不是自动认证或自动授权。

2. **WFD 仅有精确、窄域的 `virtual` 关键词例外**。
   - 必须同时满足 `NetworkInterfaceType.Wireless80211`、`InterfaceIndex > 0`，以及系统 `Description` 精确匹配。
   - 允许的描述为 `Microsoft Wi-Fi Direct Virtual Adapter`，或其后接 ` #正整数`；使用 `Ordinal` 精确匹配，后缀首位只允许 ASCII `1`–`9`，后续只允许 ASCII `0`–`9`，不接受零、前导零、额外文本/空白、符号或非 ASCII 数字。接口 Name 冒充该描述不能取得例外。
   - 名称与描述中的**其他**拒绝关键词仍生效：`hyper-v`、`vethernet`、`vmware`、`virtualbox`、`wireguard`、`wintun`、`tailscale`、`zerotier`、`tap`、`tunnel`、`vpn`；例外不能绕过它们。
   - **只识别 WFD 接口，不识别 ICS 共享角色**，不因描述命中就推断热点下游或共享/转发职责。接口 Up、合格类型、RFC1918、可用子网掩码等筛选不放宽；同 IPv4 子网、访问密钥认证、serverProof 与本机审批约束全部保留。

3. **receiver 的 membership 按一致接口身份去重，地址级 binding 与 sender 不变**。
   - 在任何 join 之前验证整个 binding 快照：index 必须大于零、Id 非空白；同一 index 不能对应不同 Id，同一 Id 不能对应不同 index，Id 比较大小写不敏感。身份缺失或冲突直接拒绝，不猜测合并。
   - 每个 receiver 对同一接口、同一组只 join 一次，按首次出现顺序选取代表 binding。实际加组仍使用代表 binding 的地址，不将 socket 选项改成按 index 指定接口。
   - 保留接口的所有合格 IPv4 `NetworkBinding`，sender 继续按地址工作；membership 去重不能误删地址 binding 或改变发送语义。
   - 加组失败记录 `ifIndex`、`address`、`group`、`socketError` 与 `nativeError`，异常继续上抛；释放半初始化 receiver，启动层清理其余 socket、bindings 与 CTS 状态。**回滚的是应用初始化资源，不是还原系统网络**；既不吞错伪装启动成功，也不调用网络配置操作补救。

4. **停用的是改网路径，不是整个 GUI/CLI**。
   - GUI、CLI、elevated、lab、Undo 的旧网络修改入口均拒绝；不能仅隐藏按钮而保留解析、runner、role 或 launcher 的旁路。普通 GUI 验收、`info/host/client` 与只读检查继续可用。
   - 旧改网参数在解析阶段被拒绝；直接调用旧 lab/elevated 路径也返回禁用结果，不调用 shell 或 UAC。没有安全凭据来源的 headless success 仍为 UNMET，不绕过密钥与审批。
   - 仓库 `scripts/acceptance/set-lab-ip.ps1` 保留历史正文，但在参数块之后立即 `throw`，不进入历史权限检查、状态文件读写或网络逻辑。不能照旧手册执行准备或 Undo；历史脚本的成功往返记录不证明当前网络已恢复。

5. **M4 交付必须 fresh publish，并隔离历史物料**。
   - 旧 M2 `scripts/acceptance/make-package.py` 入口直接拒绝，不执行复制、发布或打包。
   - 当前 `scripts/acceptance/make-m3-package.py` 虽保留历史文件名，但只接受 M4；每次在 `artifacts/` 下新建独立 `m4-acceptance-*` 目录，以 Release / win-x64 / self-contained fresh publish，不复用或清理旧发布目录。
   - 不允许跳过 publish；`LANREMOTE_M3_SKIP_PUBLISH=1` 直接报错退出，不从旧二进制兜底。
   - 递归、大小写不敏感地排除发布树**所有层级** `.ps1`、`.cmd`、`.bat` 及旧 `START-HERE` / `START-HERE.*` 文件；随后只从 `--manual` 显式选择的路径装入一份手册。手册必须存在且不能是上述脚本类型，输出目录必须已存在。
   - 当前选定手册为 **`outputs/m4-network-fix/M4-现网验收说明.txt`**，在包内为 `START-HERE.txt`；README 出包命令使用该路径，不再选用旧手册。策略已实现不等于本次文档同步已经出包。

6. **验收与证据口径不得扩大**。
   - 旧双机尝试应记为「GUI 已运行；发现失败，认证未开始」，不是「GUI/双机未运行」，也不是通过；修复实现不等于现网发现或双机认证已经验证成功。
   - 本地 info PASS 不证明发现、互通、认证或互联网正常；不宣称网络恢复，不将旧里程碑的通过结果外推到当前修复版。
   - 当前全量测试结果不在此预填；**测试与交付详情见 HANDOFF §18.13**。

**已运行并恢复的定向变异证据**（本机开发验证；原始日志与逐字节恢复记录在 `outputs/m4-network-fix/mutations/`）：

| 变异 | 变异后 | 恢复后 | 证据范围 |
| --- | --- | --- | --- |
| 禁用 WFD 的窄例外 | 7 红 | 7 绿 | 允许条件不能被整体取消 |
| WFD 绕过其他拒绝关键词 | 11 红 | 11 绿 | 只能豁免 `virtual`，其他拒绝词仍须生效 |
| 取消 receiver 去重，重复 join | 2 红 | 2 绿 | 包含真实 `Socket` 回环重复加组，不只是回调计数 |

第三项证明真实 socket 上重复加组路径受到守护，但回环重复 binding **不等于 B 机双地址现场已完整复现**，不能据此把历史现场根因或修复后的双机通过写成已证实。上述定向红/绿也不能代替当前全量测试与实际双机证据。

**Consequence / 可逆性**：后续实现、验收和打包统一遵守现网只读边界。保留历史用于追溯，不把旧确认/UAC/Undo 设计当成继续改网的授权；遇到不满足的网络条件须如实报告，不能放宽 RFC1918、同子网或认证约束来取得表面通过。

### ADR-046 — 本机隔离专项入口与单调截止通知修正

**日期**：2026-09-28。实现`2596357`，实测与交付见HANDOFF§18.22。

**Context**：剩余实屏proof错误没有可操作安全入口，活动停止只有5秒抢点窗口；本机桌面自动化执行权限未解除。新增隔离入口应是正常验收工程能力，而不是绕过桌面权限或放松产品认证策略。全量另暴露服务端approval-timeout被归unavailable、客户端approval-timeout被归frame-timeout的竞态。

**Decision**：
- 新专用WinExe/WPF入口直接复用Acceptance.App和MainWindow；启动前不可变隔离模式，普通入口不变。专用入口不接受headless/远端/密钥参数；Loaded不触真实自检。实际审批依然手选，错误proof必须真实经TLS送达真实连接器并按原固定提示展示。
- 隔离仅Acceptance内私有精确IPv4 Loopback策略、随机端口、每轮临时身份/key/证书，不改产品SubnetPolicy、不加载真实vault或发现、不改系统网络。界面状态为每run有界快照，所有任务随role回收；Host先快照/Stop，配套客户端后取消。仅本地已认证对象观察120秒，普通success仍5秒；认证/审批预算不变。
- 负例维持FAIL、操作员停止维持INVALID_RUN、无人审批或未执行目标操作维持UNMET；这些标签本身不等于专项通过。相对lnk原生目标解析未通过，未交付，改为真实薄WinExe，不引入脚本。
- AuthenticationDeadline的timer只负责唤醒，回调先复核原单调截止。提前回调重排、不足1ms只延后下一次通知到1ms，不改变IsExpired接受判据。构造同步回调与Dispose重排有明确同步；外部取消通知在锁外。
- ControlClientConnector已持有deadline时，FrameReader使用内部令牌入口，禁止另起独立系统CancelAfter。公共读取接口保留；错误路径同样遵循父取消>绝对截止>帧错误，不扩大预算、不接受模糊拒绝码。

**Evidence / 边界**：服务端599.5/600ms提前回调在真实TLS场景、客户端399.5/400ms重复CTS在受控Stream场景分别确定性复现旧错误；不声称现场Windows timer具体提前量已采集。最终Debug/Release各1433 PASS、Python14；实际桌面窗口尚未观察，不替代原人工清单或整个M4。证书DefaultKeySet遵循既有Schannel实测，非“绝不触盘”承诺；旧A/B安装目录保留。

### ADR-047 — M5 视频帧所有权、固定头实施细则与失败后永久终止

**日期**：2026-09-29。**Context**：M4在HANDOFF§18.25按明确实屏确认收口后，按原M5顺序实现视频基础件。原规格给出40字节建议布局及有界管线，未定义flags/reserved细则；Core已有long时间戳、40..85质量设置与IMemoryOwner模型，但EncodedFrame将池容量当有效长度。此次补齐实施细则，不回写原始规格，也不提前放行VideoAttach。

**Decision**：

1. Core保留现有EncodedFrame构造，新增显式有效payloadLength重载。有效长度1..32MiB且不得超过owner容量；允许池容量更大，Payload仅暴露前段。只有构造成功才转移owner；失败由调用方释放。释放后长度0，借用视图不能跨越所有权移交/释放或与释放并发使用。
2. BoundedFrameQueue<T>容量仅1/2、默认2，短锁+有界Queue+共享异步唤醒实现原DropOldest语义，不公开Channel端点、不设异步写队列。TryWrite返回值决定所有权：成功交队列，拒绝归调用方；丢旧在锁外精确尝试一次Dispose，异常以out参数报告，不掩盖新帧已被接受。Stop先永久拒绝/清空/唤醒，再逐项释放并聚合异常。停止不等待消费者或并发丢旧释放；后续Session必须join全部生产/消费任务，不能把队列容量或Stop返回当全管线内存/清理证明。
3. wire直接40字节头+payload，不加Control长度前缀：LRVF、version1、codec1；flags按BE读取且v1必须0，三reserved也必须0。frameId为完整ulong，不在framing层定义初值/排序/回绕；timestamp按uint64 BE解析后限制到现模型可表示的0..long.MaxValue。width/height为1..8192；jpegQuality对齐既有40..85；payloadLength为1..32MiB，先在uint域验证再转int/分配。上述零位/范围是本ADR的v1实施决定，不伪称原建议布局已逐项规定。
4. VideoFrameHeader/Reader/Writer留在net10.0 Transport且均internal，仅供后续认证会话内部装配，不新增public流/token旁路、不改变M4 hello/准入。Reader完整头合法后才租用，完整体读取/模型构造/最终取消检查后以1→0 CAS作为交付提交点；若Dispose的终止2先发生，不交付未提交帧。成功后owner由消费者释放。Writer在写任何字节前验证模型与有效切片，不拥有调用方帧；完整帧写完才允许下帧。
5. 首次失败（含取消、重叠调用、错误模型）、帧首EOF或Dispose均永久终止，不重试半帧或扫描magic。首帧无数据EOF返回null，截断头/体分别报错。仅消费caller取消，无内置滑动/重复deadline；未来Session层须提供整帧绝对预算和在途join。只尝试一次底层Dispose，不承诺硬中断任意Stream。
6. 主协议/I/O/取消异常保留原实例、类型与令牌，清理异常另以internal有界只读快照公开（Reader最多stream+owner两项、Writer最多stream一项）；直接Dispose仍可抛出关闭错误。IsTerminated只代表不再接受操作；StreamDisposeSucceeded只代表底层Dispose正常返回，不代表实际资源关闭或join完成。清理失败不静默记成功，不盲目重试释放。

**Evidence / 可逆性**：实现前后使用同一组20个确定性回归，先20红再20绿，覆盖构造期取消/停止/重叠读、owner释放掩盖原错误、真正关闭前失败及稳定诊断；测试文件SHA不变。Python标准库struct独立生成40字节黄金向量，与手写测试向量一致。其余边界/分片/连帧/非法头先拒/池尾部和队列并发释放均有定向回归；全量结果见HANDOFF§19。payload测试字节不代表合法JPEG，不代表真实TLS视频/采集/显示/10分钟稳定性已验证。内部实现可后续替换但所有权、失败终止及限额不弱化；改变wire取值范围须明确兼容性决定。

### ADR-048 — VideoAttach 固定字节证明、写前绝对窗口与一次性原子绑定

**日期**：2026-09-29。**范围**：M5 安全基础片；以下是原规格 04 §10 的实施细化，不是用户额外指定的重连策略，也不表示第二 TLS 路由已经接入。取代 ADR-041 的共享 token 查询计划；M4 的 success 写后才登记、单读者与审批均不变。

1. **精确 transcript**：UTF-8 `LANREMOTE-VIDEO-V1` + 单字节 NUL（共19字节），紧接 UUID 的 RFC/network-order 16 字节、解码后的 nonce 16 字节、实际视频 TLS 证书 DER 的 SHA-256 原始32字节，共83字节，无额外分隔/终止符。UUID 不用 Guid 默认混合端序；HMAC-SHA256 的 key 是32字节 sessionToken，proof为32字节，FixedTimeEquals 比较。独立 Python uuid/hmac 黄金向量检验端序与长度；不复用 M4 的32字节nonce或文本transcript。
2. **严格 video hello**：只接受原六字段，type/channel/protocol 精确为 channel_hello/video/1；sessionId为小写D规范UUID，nonce/proof为规范base64且精确16/32字节。拒绝重复/未知/缺失/null/错类型/非法UTF-8/多余值/注释/尾逗号，payload上限4KiB。解析器和proof内部可用不等于默认Control入口放行video；旧HelloFrame与ControlPreAuthSession不放宽。
3. **单调窗口**：序列化success之后、首次write调用前捕获同一TimeProvider起点，权威预算为min(配置提示,15000ms)，必须正值。write/flush、写后调度和登记等待均计入；仅本地success写成功后Register，登记不重置。MAC前及最终提交前均要求elapsed < budget（负elapsed也拒绝）；timer派发和UTC无关。写成功但窗口已过仍可登记Control，仅视频资格失效；成功附着后不因15秒到点撤销已有视频。
4. **原子一次性消费**：Register防御复制token并保存Control取消令牌、原始窗口和原Control证书pin。TryAttachVideo只接受冻结视频TLS事实；须为不同ConnectionId、同一远端IPv4地址、与Control相同pin。登记表同一锁内校验活动entry、控制/调用取消、窗口、未消费资格，计算MAC并在最终时间/取消/entry身份复核后提交。错误proof不消耗资格；并发正确proof至多一个成功。不导出token视图/副本/回调。锁内无网络I/O、日志委托、Cancel或await；TimeProvider为可信快速时钟，不可用普通锁承诺中断阻塞本机实现。
5. **撤销与所有权**：成功返回内部VideoAttachLease，仅关联ID与RunContinuationsAsynchronously的Revoked任务，不持有流，不提供恢复资格的Release。注销按entry对象身份防ABA，同锁摘表、清零私有token、完成撤销通知。调用方收到成功时lease可能已撤销；后续handler必须观察撤销并在锁外关闭/取消/join。撤销通知不等于socket已关闭或任务已退出。附着后视频断开/初始化失败/取消都不恢复资格；v1本片不支持同Control会话内重连，需重新认证，避免回滚/重放状态机及无界nonce表。
6. **暂不可见与后续接线**：success被客户端收到与Register可见不是同一时刻。本片查不到entry立即fail closed，无预注册占位/暗中批准。后续显式首帧路由若补注册间隙等待，只可在同一未授权TLS连接、既有准入和pre-auth绝对预算内有界处理；精确等待/接受确认规则需在接线时另定，不能盲目重连或把一次成功退回未消费。第二TLS仍须同子网/RFC1918/原pinning；禁止对外公开流/token和改系统网络。
7. **秘密清理边界**：注册失败清零未转移的token副本，不动调用方和现有entry；success写出/注册后立即清理本方法的token、success对象token及可控序列化字节，失败亦同。访问key不延长到保持阶段。DTO字符串、FrameWriter/TLS内部副本不能保证擦除；客户端旧internal SessionToken借用面留待客户端附着同步改造，不能声称全Transport已无共享秘密视图。

**验证状态**：最终自动化结果、审查修复与未接线范围统一记入HANDOFF§19.4；独立复核已确认三项变异分别2/2/4红、精确恢复后同组8绿。success失败后的三份原秘密现先清理再尝试失败帧I/O，同一回归修复前4红/1绿、修复后双配置各5绿。注册冲突时失败新副本清零有生产finally覆盖，但尚无直接持有该失败副本的断言；不把该漏测表述为现有泄漏。不将registry或合成视频上下文测试当第二条真实TLS验证。

### ADR-049 — 停机超时保留连接跟踪与单次强制释放

**日期**：2026-09-29。**Context**：第二TLS生命周期接线前发现现有ConnectionRegistry停止后无条件清表/释放CTS，尚在运行的连接从后续Stop消失且令牌getter失效；并发Stop重复强制释放，合作handler已释放的旧snapshot也被再次释放。原单次Unfinished报告无法保护这些跨轮语义。本片只修复已证实跟踪问题，不等于实现视频Router或完整硬截止停机。

**Decision**：
1. 超时只返回报告，不删除未完成entry，不使其CTS失效；停止永久拒绝新登记。真实租约完成、强制释放退出、CTS回收都具备后才移除，后续Stop继续观察同一entry。
2. 根取消只由首轮Stop发起；普通完成认领CTS回收后仍留表。根取消开始前先等已认领回收结束，取消进行中不让handler同步解绑linked CTS，避免取消回调等handler而handler等父回调的死锁。Cancel、Dispose和等待均在锁外；取消finally接手已完成entry回收。不新增无跟踪后台清理。
3. 同锁检查未完成并认领每entry的强制释放，登记表至多尝试一次；已合作完成的旧snapshot跳过。强制释放仍在进行则保留表项，不能只因handler调用registration.Dispose便报全完。这不是handler自行Dispose与已认领强制Dispose之间全局exactly-once保证；后继Host共享关闭授权仍需处理。
4. 完成TCS在同锁标记RegistrationDisposed时先发布，再允许清理和摘表。已知旧快照与新快照不能出现“表已空但原完成通知未发”的窗口。TCS使用异步continuation。
5. 不放宽认证、同子网、准入或原超时数值。现有同步取消回调、任意资源Dispose/CTS解绑阻塞以及清理异常可能中断后续处理，仍是待处理局限：两段异步等待有预算，不伪称整个Stop可硬中断任意本机代码。后继视频生命周期须处理取消任务/关闭错误/真实join，禁止靠文案把这些要求取消。

**Evidence**：四个确定性新增用例先4红，修复后登记表双配置各11绿；随后审查补测“完成先于摘表/force未退出仍跟踪”先1红1绿，修复为完成通知前置。最终全方案结果见HANDOFF§19.5。完成通知用例直接调用内部Remove接缝并持有真实TCS，不冒称随机调度或真实TLS竞态复现；其他用例通过公有停止/租约入口和受控资源执行。未改Host或产品默认装配、未重跑M4人工验收。

### ADR-050 — 显式服务端双TLS路由、附着ACK与真实任务收尾

**日期**：2026-09-29。**Context**：ADR-047/048/049已提供帧、一次性附着及停机留表，本片接通服务端最小闭环；不改变产品默认装配，不实现客户端高层附着、采集或显示。

**Decision**：
1. internal `FirstFrameRouter` 每连接一次，只读取一个长度前缀首帧。严格Control hello成功直接交原`ControlPreAuthHandoff`，严格video hello走独立TLS附着；public pre-auth仍Control-only。首帧prefix/payload受原单调信封约束，不能把信封令牌传入长期Control holding或ACK之后的视频发送。Host提供internal显式工厂；测试也可显式装配以保留诊断。
2. success写后登记间隙仅允许初次查无entry的`NotRegistered`短暂重试：100ms总预算、最多3次立即/40ms/80ms查询，原信封只收窄。已经观察到entry但最终失效/ABA用`Unavailable`，立即失败。资格仍在registry锁内按ADR-048消费，调用越过路由预算不发ACK，不回滚资格，不续原15秒窗口。
3. ACK为四字段严格JSON：`type=video_attach_ack`、`channel=video`、`protocol=1`、canonical D小写`sessionId`；4字节BE长度前缀，payload最多4096。ACK无token/proof/nonce。write和flush均结束并后置检查后才能取第一帧；之后直接40字节LRVF头+有效payload，不再套Control前缀。ACK默认2秒、每整帧write+flush默认5秒，均为内部初值，不宣称性能调优结论。ACK成功不证明首帧显示，也不保证稍后不会撤销。
4. Host原始socket/SSL共享一个`ConnectionCloseHandle`关闭任务；同步Dispose仅请求，异步关闭分别尝试socket与SSL，即使前者失败也尝试后者，迟到SSL须回收。`AcceptedConnection`关闭能力绑定原始Security/Stream引用，public两参构造及替换引用的record副本无授权。`HostOwnedVideoStream`真实转发且Dispose发起同一关闭，不用空Dispose/leaveOpen逃避所有权。
5. Host先等关闭尝试，再分别归还准入和登记。未登记拒绝路径由accept持有至关闭结束，已登记连接并发由registry持有。固定七类生命周期诊断各保留首错，关闭句柄最多两项；完成表示尝试结束，不代表释放成功。不能用超时摘表或仅发送取消伪称所有任务退出。
6. 视频路由持有transfer（含ACK/sender）、唯一上行一字节读、CancelAsync回调任务、Host关闭任务；任一transfer/peer/撤销/Host stop触发独立关闭与取消，随后逐项join。不合作source继续保持handler/登记未完成；上行额外字节违规。Control只复用原审批读和原decision，新增内部join但不改变public认证入口兼容行为，不新增Control reader。
7. `IVideoFrameSource`返回非null即将帧所有权转给sender；串行取/写/释放，不预取无界队列。迟到帧先接管再检查取消，write结束后才释放。sender保存来源首错及固定清理错误，EDI保留原异常。Router完整读取原WhenAll的异常集合，主体/收尾逐项join后统一传播，单错原实例、多错聚合，按引用去重；不得将source IO/ODE或无取消OCE当成正常网络结束。
8. Host仅豁免已取消且异常token等于本次registration token的OCE，避免后来停机吞掉先发生的source故障。Router首帧/登记等待的子deadline取消在受控边界按caller优先归一为Host token，视频来源/清理不进入此豁免。不能据catch时后来的取消追认来源故障；任意handler故意伪造同Host token的故障与真实取消不可仅凭这两个事实区分，不承诺解决此不可区分性。

**Evidence**：Router异常保留11项先运行期全红，修复后全绿；source OCE先发生、Host Stop后到的真实双TLS交错1红后修复；子deadline正常取消归属3红后修复。最终定向双配置各225 PASS、solution各2196 PASS、四次build均0警告错误，见HANDOFF§19.6及outputs/m5-dual-tls。其余边界为受控SSL/手动时钟组件证据；真实TLS用例13项采用实际pin验证、独立proof/ACK/wire断言，不冒称截图、JPEG解码、两机现场或M5十分钟DoD。

**Limits**：关闭失败、任意同步Dispose/gate/source/取消回调可能阻塞，CTS不能硬中断本机任意代码；留表与join确保不遗弃，不提供无条件硬截止。测试payload仅为不透明黄金字节。无客户端安全交付、无GDI/JPEG/WPF显示；默认产品App、系统网络、防火墙、M4物料均不变。

### ADR-051 — 客户端会话内封闭VideoAttach证明与密钥释放互斥

**日期**：2026-09-29。**Context**：客户端后续第二TLS入口不得获取可跨Dispose存活的共享token视图。ADR-048证明合同不变，本片仅实现安全同步操作，不提前实现握手/ACK交付或父子生命周期。

**Decision**：移除AuthenticatedControlSession的internal SessionToken getter，新增internal `CreateVideoAttachProof(nonce, actualVideoPin, cancellationToken)`，只返回32字节proof。状态检查、caller取消、nonce16/pin32长度、控制expected/presented与第二pin匹配、MAC及末次caller检查均在同一私有锁内；校验和MAC使用同一nonce/pin栈快照。失败时已生成但未交付proof清零，退出清栈快照；成功proof所有权交调用方。Dispose同锁置空连接并清零原token，锁外关闭TLS，避免持有秘密锁执行IO。已释放优先拒绝，等待锁期间取消在进入操作后被观察。

**Evidence与界限**：独立83字节transcript/HMAC黄金、输入绑定/严格拒绝、原token数组清零、锁外TLS释放和入口锁并发回归；测试专用反射仅观测原秘密存储，不向生产恢复getter。既有双TLS独立proof oracle不改成调用被测方法。Python标准库独立核对83字节及黄金602656EA…78B7A3A。并发测试仅锁入口边界，不声称暂停MAC内部；失败proof清零有finally，但尚无直接持有该失败数组的确定性后置取消观察。复制不保证调用方恶意并发改写原数组时原子快照；传入pin必须由后继连接器取实际第二TLS身份，此同步方法不自行证明pin来源。不施加本地TTL/一次性附着、不提供客户端连接器/唯一交付/迟到回收/父子join，完整验证见HANDOFF§19.7。

### ADR-052 — 客户端共享关闭与真实视频流适配器

**日期**：2026-09-29。**Context**：TlsConnection原Dispose以普通bool判重，SSL释放抛错会跳过TCP，重复调用不会补收。后继VideoFrameReader拥有传入Stream，不能用空Dispose或leaveOpen逃避实际关闭，也不能把关闭完成冒称原读写已join。

**Decision**：
1. TlsConnection的internal CloseAsync在短锁内发布关闭状态与唯一Task，worker依次独立尝试TCP、SSL释放；先断TCP解堵，不在请求线程执行释放、不在锁内等待。Task成功完成表示两项尝试结束，不表示物理释放必然成功。CleanupErrors为固定两槽的独立只读快照，顺序TCP、SSL，保留原异常实例。
2. public Dispose仍同步等待该Task。首个public Dispose调用者单错EDI重抛原实例、双错Aggregate保留两项；后续调用同样等待但不重放异常，避免重复finally覆盖已翻译的认证异常。CloseAsync/adapter不消费首次错误报告权。首调用者在并发下由Interlocked认领，不承诺哪个线程先返回。既有AuthenticatedControlSession重复Dispose仍只做幂等逻辑释放，不自动继承此等待合同。
3. internal ClientOwnedVideoStream转发所有同步/异步SSL读写、flush和timeout，不转移最终TCP/SSL所有权。同步Dispose仅请求关闭，DisposeAsync等待同一个Task；高层仍须查看连接CleanupErrors并持有、join原read/write任务。Reader的StreamDisposeSucceeded只证明请求发出成功。
4. 请求发布后拒绝再次获取Stream/CreateVideoStream，所有既有adapter也经连接状态检查后才能开始新操作；已经取得原流或已经在途的操作不在此强行夺回。无独占reader登记、无父子状态机、无高层ACK交付。public接口未增加。
5. 测试中秘密锁检查改用跨线程Monitor.TryEnter，不能在worker用IsEntered假证调用线程已放锁。专用Dispose线程只保存一次等待态观测，不二次读取瞬时ThreadState；这只是受控入口等待证据，不是内部精确探针。旧审批配额测试须先join原clientB，再读完整Frames/待批事实，不能把服务端outcome当客户端终帧读取完成。

**Evidence**：旧实现两项确定性释放故障测试运行期全红（TCP未尝试、第二错误丢失），再修生产；新增组件26、真实回环TLS11及这两项回归共39例。实际计数、最终双配置证据以HANDOFF§19.8为准。回环TLS使用产品连接器与实际证书pin，验证双向字节和本端关闭使两端原读退出；不冒称视频附着、JPEG或两机实屏。

**Limits**：任意同步Dispose不合作时仍可永久阻塞，不提供硬截止；CloseAsync成功不代表清理成功或I/O完成。public Dispose首次清理错误仍可能覆盖调用者的既有主错误，该外层owner问题未在本片修复。TlsClientConnector返回前失败finally仍是独立待修缺口。LocalEndPoint原有并发关闭观察窗口不作为授权判据；本片不新增其同步保证。未改TLS/pinning/认证安全、系统网络、产品默认Control-only或M4物料。

### ADR-053 — 建连失败的单一 owner 与复合故障保真

**日期**：2026-09-29。**Context**：TlsClientConnector原失败finally先SSL后TCP，前项抛错会漏收后项、覆盖主错；返回前缺caller终检。新增复合异常若仍沿用高层广泛取消翻译及验收cross-subnet分类，会丢故障甚至负例误PASS。

**Decision**：
1. 生产ConnectAsync经internal ConnectOwnedAsync逐调用单owner执行TCP连接、SSL创建、握手/身份建立；没有可变全局factory或公开替换validator。选whole-owner而非已连接接缝，以确定性覆盖connect/create早期TCP-only失败。三个委托只借用资源，不释放/转交；factory在返回前内部失败时仍须自行处理未返回资源。
2. owner进入、connect原任务结束后、authenticate原任务结束后的交付前检查caller；不以代理取消任务取代原Task。检查与return之间不承诺原子取消。创建SSL期间或原操作不合作时，不承诺硬截止。
3. 失败以一个被实际await的worker先TCP后SSL独立释放；清理不链接caller。无清理错误直接throw保留主实例；有错固定为Aggregate(primary, Aggregate(cleanupErrors))，清理内部1~2项按TCP/SSL顺序，保留原实例/嵌套，不Flatten、不使用共享LastErrors或Exception.Data。既有pin拒绝包装保留，primary是operation向owner交出的异常，不冒称底层SSL异常始终位于顶层。
4. Control的实例readonly建连接缝用于失败路径测试；生产默认仍真实TLS。取消归一化不得替换含Aggregate的异常树（沿普通InnerException链查至Aggregate即止）。原最外层OCE仍按async builder形成Canceled，保留其异常树/token；低层生成的外层Aggregate形成Faulted，不能为统一状态篡改原异常。
5. Acceptance复合故障先于取消/TCP/pin/cross-subnet分类：场景HARNESS_ERROR、连接UNOBSERVED，脱敏记录故障；即便整轮操作员取消优先为INVALID_RUN，仍保留场景错误和故障记账。普通OCE的重抛延至cleanup完成后，以免复合cleanup绕过尾部判定。普通非复合错误既有取消语义保持。
6. footer不是事务：日志先写后回调。复合日志回调失败只记账后原样抛出，不整段重试Complete，避免两套结果/重复RUN COMPLETE；已写标记不证明方法成功。日志不输出任意异常正文/stack/inner。

**Evidence**：TLS owner先51/60运行期红；Control纠正OCE Task状态测试预期后22/80红；Acceptance分类96/144红、收尾补充5/151红，修后通过。最终双配置各2514 PASS（较上片+251），四build零警告错误；详细证据见HANDOFF§19.9。mock identity仅组件证据，真实TLS/pinning由原生产连接器回归承担。

**Limits / next**：Control已取得连接后的认证失败finally与Verify新session后置终检的清理仍可能覆盖认证主错，下一片先做逻辑撤销清零和外层异步owner收尾；不把本片称为全部客户端清理完成。success时间元数据/附着TTL、父子撤销join、ACK唯一交付、采集显示及十分钟DoD均未完成。未改系统网络、默认Control-only、M4物料或原安全门禁。

### ADR-054 — 未交付控制会话的秘密撤销与异步 owner 收尾

**日期**：2026-09-29。**Context**：认证已经取得TLS后，外层finally的同步Dispose会覆盖认证主错并阻塞调用线程；Verify在构造session后终检失败时立即Dispose，既推迟临时秘密清零，也可把超时覆盖成IOException并再次翻译成连接关闭。

**Decision**：
1. AuthenticatedControlSession提取同锁RevokeCore，internal void RevokeForOwnerCleanup仅逻辑失效、清零原token，不触发网络I/O。只有已经持有原连接且负责关闭的owner可用。public Dispose仍由取到连接者在锁外同步关闭；重复session Dispose幂等返回，不改变底层连接的重复等待/首报错合同。
2. Verify构造后终检失败仅撤销未交付session后重抛；认证各层finally清理parsed token、transcript、expectedProof/grantTranscript。外层catch先采样caller取消状态、清零私有key并撤销已取得session，才await原连接共享CloseAsync；finally只作key清零兜底。
3. 无cleanup错误沿用catch入口取消快照和原复合异常保真规则，清理期间才到的取消不追溯改写主错。有cleanup错误合成Aggregate(primary,Aggregate(TCP/SSL原槽))一次；不调用public Dispose再重复收集、不Flatten、不用错误是否曾被public报告过来判断有无清理故障。
4. 成功移交前终检仍保留；成功路径将两个owner引用清空，不回收已交付session。关闭任务完成不等于清理成功或所有外部I/O已join，任意不合作本机代码仍无硬中断。
5. 测试观察接缝仅internal构造参数/private readonly实例callback，public默认null；在真实session new后、受撤销catch保护且终检前调用，无public token/stream扩张。真实构造事件驱动到期，不以目标终检的第N次取时自证构造。observer自身异常仍受既有认证类别翻译，不能承诺任意类别原样返回。

**Evidence**：owner旧实现190例144红，旧构造后22例18红；修复后加强至26例，并新增11例撤销原语。实际删除外层/Verify撤销调用分别10/14红（各26例），原构造、到期与取消事件不删；精确恢复源码后26绿。最终再加强构造时持有session token原数组并检查非零前态、闸前同一数组全零，避免清零副本假证。最终双配置证据见HANDOFF§19.10。

**Limits / next**：测试直观测key/client transcript/parsed token/session自有token；expectedProof/grantTranscript只有代码finally顺序证据，JSON字符串/TLS内部副本不承诺擦除。受控SSL夹具不是实际TLS，真实TLS由未放宽的原用例提供。success原receivedAt/clock与附着TTL、父子生命周期/ACK后唯一交付、采集/JPEG/显示/十分钟DoD仍未完成；系统网络、M4、产品默认Control-only不动。

### ADR-055 — 客户端 success 原时刻与无副作用附着预算

**日期**：2026-09-30。**Context**：ReadFrameAsync 已在完整 payload 收齐后采样，但 ReadReplyAsync 丢弃 success 的时刻；若到构造或附着调用时才起表，会漏扣解析、校验与本地调度耗时。服务端从 success 写出前开始计时，因此客户端收齐锚点只能限制本地等待，不能代表准确的服务端剩余资格。

**Decision**：
1. ReadReplyAsync 分开返回 SuccessReceivedAt 与 PendingAcceptedAt；direct/pending 后 success 均传递原完整 payload 时刻和同源 TimeProvider。pending 仍在严格解析后接受，不把其收齐时间替换为接受时间，不改变独立审批窗口。
2. session internal 构造必须提供 successReceivedAt/clock，无自动重新起表旧重载；null clock/非正 hint 在复制 token 前拒绝。零或负 timestamp 原点本身合法，构造不取时；保留原 hint 属性，消费预算为 min(hint,15000) 毫秒。
3. internal GetRemainingAttachBudget 在原秘密锁内按已撤销/Dispose > caller 取消 > 到期判定；单次 GetTimestamp 与同源 GetElapsedTime 后，再查撤销/取消，包含可重入 frequency 读取。elapsed<0 或 elapsed>=budget 抛 TimeoutException，成功仅返回正余量，不续期、不创建 timer/I/O、不消费资格、不因到期清零或关闭 Control。
4. 预算查询不参与 Control 认证成功判定，也不附加到现有 proof 方法；小 hint 在构造前已耗尽，仍可交付在认证有效窗口内的 Control。该方法不是后续 Attach 的完整门禁，父子生命周期和 ACK 后唯一交付尚需单独实现。

**Evidence**：新增47例（预算27、公开认证入口传播20）；受控SSL走严格帧与独立HMAC oracle，不冒称真实TLS。最后一字节到达、payload timer释放、真实构造observer各自驱动时间，避免第N次取时自证；固定400ms构造前+600ms observer+250ms消费者延时由5000ms原预算扣至3750ms。非标准频率、UTC跳变、B±1 tick、负elapsed、重入撤销/取消和无关闭/原token/proof有效均有断言。未声称直接观察MAC内部耗时。

六轮单点变异（success重采样、pending换起点、去封顶、>=变>、删后置撤销、删后置取消）分别16/10/6/11/2/2红，每轮47例；恢复原字节后47绿。pending变异经通知边界脱敏为固定认证异常，结合唯一变异与断言位置定位，不把TRX包装误说成直接显示内层断言。初始2项同步Theory误用Timeout属测试错误，去除该标记后定向112通过。最终双配置全量结果见HANDOFF§19.11；未动M4、系统网络或默认Control-only。

### ADR-056 — 客户端父子生命周期与联合交付

**日期**：2026-09-30。**Context**：原认证构造 observer 能看到尚未最终交付的 session；连接关闭不代表原 connect/init/read 或取消回调已退出。高层第二 TLS 接线前必须先保证所有权与交付边界，不以缺少父撤销检查的 public Attach 抢跑。

**Decision**：
1. 认证公开入口在 AuthenticateConnectedAsync 及其窗口释放完全返回后，同秘密锁 CommitDelivery；已撤销优先于 caller 取消，提交后不再插入可失败步骤。只有新子操作要求已交付；旧 Stream、proof、预算查询及 public 表面不扩大或收紧。
2. 一个固定 attach 槽、一个固定 read 槽，worker 在父锁内登记后于锁外执行原操作并 await 原 Task；同步工厂前缀同样受跟踪。worker 不绑定 Task.Run 调度取消，避免已登记任务被取消得不执行。失败即永久耗用本地单次尝试，入口拒绝不消费，Control 不因此关闭。这是客户端保守政策，不改变服务端仅正确 proof 原子绑定才消费的规则。
3. 迟到连接先接管再裁决停止；init 完成后 reader 仍仅是候选，预算检查后复查可重入的父/子状态再提交。原 reader 返回的帧归高层暂持，同父锁终检才交给调用方；未交付帧在锁外精确释放。提交后的停止/取消不追溯回收。持续读不受 attach TTL 约束。
4. RevokeForOwnerCleanup 只逻辑失效/清零/标记子停止，零网络与回调；public Dispose 首个撤销者先独立请求 Control/video 关闭并只等原 Control 同步关闭，重复调用立即返回；新增 internal CloseAndJoinAsync 返回共享任务，等待两个连接及子原操作/取消/reader/迟到回收全部结束，不消费 public 错误报告权。
5. 子停止先逻辑标记，连接关闭、reader 释放与任意 CTS.Cancel 回调独立安排；join 先等已登记 attach/read，再补齐迟到资源。操作只能请求 stop，不能等待包含自身的 join。不承诺硬中断不合作代码，外部借用委托不得留下未返回的 I/O。caller 注册在锁外建立，原 worker 覆盖异步解绑，提交后的 caller 取消不追溯停止会话/帧。
6. 错误来源固定有界，join 完成只代表所有尝试退出，错误见 LifetimeErrors 稳定只读快照。原 Task 多错保留完整树；单错仅移除 Task 自加的最外单项容器，用户自己的 Aggregate/InnerException 不 Flatten。下层 helper 若已丢失分支，不声称本层可以还原。
7. 测试用实例级 joinScheduler 默认线程池，严格单项测试调度器用于同步推进 join 至原 read await；这比等待 close/cancel 信号后瞬时读取 IsCompleted 更强。未新增 public 或全局测试开关。

**Evidence**：新增63个展开用例（生命周期57、真实公开认证路径最终交付6）；定向175 PASS。初轮单错容器身份与ObjectDisposedException全名预期分别2/4失败，前者修生产诊断、后者校正新增测试预期，原断言未放宽。变异先揭示关闭信号单等造成Guard遮错、最后兜底Dispose覆盖主Assert、瞬时join采样盲区；分别改为观察原Task竞速、仅隔离最终资源兜底异常、显式单项调度推进。旧无效/存活证据保留，不将其改写成通过。

最终8轮单点变异分别4/3/2/2/2/3/2/2个运行期红例，覆盖最终交付、尚未交付门禁、入口重入、迟到连接接管、提交重入、帧联合终检、原read join、多故障保真；无Guard或编译错误充数。恢复逐字节一致后175绿；双配置各2851 PASS，四build零警告错误，见HANDOFF§19.12。

**Limits / next**：本片是 internal 生命周期底座，initialize 成功只是借用委托合同，不等于已接严格 ACK；没有执行期 attach timer，只做启动/交付预算检查。下一片先补 child 拥有的原截止计时器和回调join/诊断、敏感hello/wire缓冲释放，再接真实第二TLS实际pin/proof和ACK SessionId匹配。Control唯一reader、远端Control关闭主动感知、采集/JPEG/WPF/十分钟DoD均未完成；受控SSL测试不是新真实TLS证据，不改默认Control-only、M4或系统网络。

### ADR-057 — 客户端视频附着原截止的执行期通知与排空

**日期**：2026-09-30。**Context**：ADR056只在启动/交付检查预算，原工厂挂起期间没有执行期取消；简单复用重新起表且同步Dispose的AuthenticationDeadline不满足此所有权合同。

**Decision**：
1. 每次被接受的内部attach默认拥有一个固定deadline worker；与attach worker均先在父gate内登记，再以首个gate作为发布屏障。Control-only、构造和纯预算查询仍不建timer，交付后读帧不受attach TTL约束。
2. 原successReceivedAt、原clock和min(hint,15000)是唯一预算。禁用态CreateTimer后无条件接管返回timer；Create/Change/运行期clock/DisposeAsync同步前缀均在固定worker、父锁外执行。Change前后均采样与复查重入状态；负elapsed或>=预算失败，绝不阶段续期。原入口/最终预算查询的持父锁旧合同不改。
3. 原TimerCallback只在内部短锁内合并一项唤醒，不取时/关闭/取消、不派发每回调Task。早触发按剩余重排，最小1ms仅是通知粒度，无接受宽限；连续立即唤醒超过32次失败，避免非合作provider无限忙循环。
4. 显式停止立即关闭通知入口并唤醒worker，与连接关闭/reader释放/CTS取消独立推进，不等原connect/init结束才停timer。worker在finally调用并await原ITimer.DisposeAsync（ValueTask只消费一次），其同步前缀及provider在途回调排空同属原worker；不能从worker等待包含自己的join。纯owner撤销仍仅逻辑标记，无新增网络/取消/计时器调用。
5. 初始化成功后先await timer worker及caller注册排空，再同gate裁决父撤销>caller取消>deadline失败>普通子停止，随后创建仅存字段的流适配器/reader、原预算末检及重入复查才交付。先创建流会被已关闭TlsConnection的ODE抢走真实原因，首轮7个运行期红例已证实并修复。原I/O自身错误仍原样优先保留，不改成新Timeout。
6. 执行期首次失败与原timer释放失败独立有限槽；原Dispose任务多错保存完整树，单错只剥Task自动容器，用户Aggregate/InnerException不Flatten。caller回调/注册排空也有固定诊断槽。旧快照不随新错误变化，join成功只证明尝试退出。

**Evidence**：新增48例；原57生命周期用例升级手动timer但不关生产功能、不改纯预算禁timer断言。首轮212=205PASS/7FAIL是上面的reader顺序缺陷；修后新增clock阻塞/慢Change/同步重入/provider在途Fire尾部/连续同步通知11例，223定向全绿。受闸原Task的实际awaiter链验证attach→deadline→Dispose→Fire，严格关联state-machine类型/owner/原awaiter/Task.Run wrapper；运行时布局不兼容明确失败，不跳过。ErrorAsync先判WhenAny的原Task获胜，再读原异常；测试保护Timeout不冒充业务Timeout。

首轮变异第5项由fake timer提前断言下限导致外层Guard遮错，整轮无效并停止恢复，旧证据保留；将下限断言移至已有正文记录值检查。最终独立8轮有效运行期红例1/1/7/7/1/1/4/3，无Guard或编译错误充数；恢复原bytes并重建后223绿，目录outputs/m5-client-deadline/mutation-20260930-164647-29de2bd5。最终逐条解析十二份TRX：Debug/Release各2899 PASS（196/299/3/66/1942/393），0失败/跳过；solution与独立入口四build零警告错误，恢复源码SHA一致，见HANDOFF§19.13。

**Limits / next**：不保证硬中断不合作clock/工厂/Stream/provider；故障provider未返回的资源不能替它回收，DisposeAsync抛错后也不能声称它物理禁止未来callback（本层入口已无副作用）。Fire尾部证明provider原任务排空，不是暂停生产OnTimer本体；caller注册实际在途排空未增同等级定向证明。当前仍无高层AttachVideoAsync/真实第二TLS接线；下一片敏感hello/wire清零，再actual pin/proof与严格ACK SessionId。M4/现网/App不动，M5未完成。

### ADR-058 — 敏感视频 hello 单数组 wire 与原 I/O 后清零

**日期**：2026-09-30。**Context**：既有VideoHelloFrame.Serialize产生nonce/proof Base64 string，FrameWriter另复制wire且不清零。下一客户端附着路径需要独立敏感通道，不能改变服务端parser/旧DTO复制合同或复用两次framing。

**Decision**：新增internal VideoHelloWire直接在唯一212字节数组写4字节大端208前缀与固定六字段JSON，Guid.TryFormat和Base64.EncodeToUtf8直接写目标切片，不创建秘密string。输入nonce16/proof32借用不改，输出成功后转移给调用者；分配后异常清零。旧VideoHelloFrame及FrameWriter不改。

SensitiveFrameWriter.WriteOwnedFrameAsync接管完整非null wire；校验stream/token/完整前缀/有效payload上限/精确数组长度，任何入口失败也在finally全域清零。只以原Memory进行一次WriteAsync，原ValueTask仅AsTask一次，再await原FlushAsync；每次成功后检查同一外层token，不创建timer/CTS、不复制、不关闭借用流、不重试。取消不提前清零仍由原I/O使用的数组；原故障先于后置取消，多fault取原Task.Exception保持有序子树、重复引用与用户Aggregate层次，单故障原样throw。faulted Task单含OCE由async自然传播为Canceled，不额外承诺保留该Task状态。调用者移交后不得再并发访问数组，本层不保证TLS/Stream内部副本擦除。

**Evidence**：新增64展开例（wire17/writer47），原parser110保留，最终定向174绿；独立Python原向量重新生成并匹配两条完整wire黄金文本（208/212/000000D0）。验证实际Write Memory底层array恒等/offset0/count全部，原IO挂起及同步前缀期间未早清、入口/取消/失败/成功后全域零、不关流不重试、原Task多错、IValueTaskSource同步完成及挂起故障/取消单次消费。静态审查无阻断，source失败覆盖缺口已补12例。无秘密string/额外副本分配主要为源码审查边界，不能仅用行为用例声称任意内存擦除。

两次变异暴露测试保护遮蔽：直接await观察器死等原Task会被WaitAsync代理变异卡Guard，改为正向发现实际registered await Task后Assert.Same原Task；非法长度拒绝测试的IO闸会遮住漏检，改先放IO使错误发送直接触发缺少异常断言。未放宽产品合同。有效8轮来自serial-02的1–7及serial-03的8（不是同目录8轮，前7不重跑）：红例2/2/2/2/3/2/2/2，分别前缀/证明字节/清零/原数组/Write代理/Flush代理/多错/精确长度。serial-01第5和serial-02第8的Guard均不计kill，证据保留。恢复原bytes并重建174绿；最后双配置各2963 PASS/0失败跳过，四build零警告错误，详见HANDOFF§19.14。

**Limits / next**：当前是内部敏感写出前置，不是已接线的高层AttachVideoAsync，不改public表面/Control-only默认。下一片以Control冻结目标走生产TlsClientConnector、第二actual presented pin调用封闭CreateVideoAttachProof、敏感hello写出、精确一帧严格ACK并匹配SessionId；失败只join自己成功登记的attempt，不能stop赢家或在原worker内自join。真实客户端第二TLS、Control唯一reader/主动关闭感知、采集显示及十分钟DoD未完成；M4/现网/App不动。

### ADR-059 — 客户端真实第二 TLS、严格单帧 ACK 与本 attempt 收尾

**日期**：2026-09-30。**Context**：ADR-056/057/058已分别完成父子生命周期、原success截止和敏感wire；本片组合成internal真实附着路径，暂不公开视频会话或改变Control-only行为。

**Decision**：新增AuthenticatedControlSession.VideoAttach.cs。无委托AttachVideoCoreAsync固定调用生产TlsClientConnector；实例级internal接缝仅替换连接工厂，要求返回新建独占的第二连接，不允许父连接/共享连接。根据已交付Control Identity重建ConnectionTarget，对IPAddress和pin使用独立副本，不重新发现。第二TLS使用TransportTimeouts.Default（TCP3秒/TLS5秒），证书校验沿用父clock；TCP/TLS CancelAfter仍是系统timer。整体attach受原success预算约束，不声称继承父连接未保存的自定义timeouts。

同gate完成资格检查、StartVideoLifetimeAsync登记及捕获本child；登记失败的重复/clock重入输家不清理赢家。直接返回原connect Task，不加async包装丢兄弟异常。初始化必须验证第二Identity的PinsMatch、DeviceId、地址、端口和expected pin一致；proof使用实际PresentedCertSha256，生产来源为真实证书回调。Host同子网→准入→TLS→router不绕过，loopback特许仅测试。

先借用Stream再生成hello。同步helper用RNG生成nonce，调用封闭proof helper和VideoHelloWire，finally清理自有nonce/proof；writer接管后在await前放弃外层wire所有权，原Write/Flush退出才可擦除。ACK使用token-only FrameReader，无新阶段timer；uint域校验1..4096再分配，严格parser后匹配父SessionId，错会话固定FrameProtocolException("video-attach-ack-session-mismatch")。成功取得payload后finally清零，失败读取由FrameReader清零。只消费一个ACK，不探EOF、重试、drain或预读，后续第二ACK由视频reader以video-magic拒绝。

原attach失败退出后外层仅await本child.StopAndJoinAsync，原worker不自join，也不关闭仍有效的Control。稳定child诊断先于primary，防御性join异常最后；顶层按引用双向包含归并，后根包含旧根时替换旧根，互不包含但共享叶子仍保留两根。不Flatten，不按类型/文本去重，不改原树内部重复引用；单根EDI原样throw。底座成功联合提交之后，不追加caller/预算终检追溯推翻交付。

**Evidence**：新89展开例（组件70/真实TLS19）。覆盖真实客户端双TLS/router、独立HMAC、actual换证pin回调拒绝、ACK粘连/分片/错会话/第二ACK、身份偏差、原wire/ACK数组清零、不合作I/O与慢join、重复输家和完整异常树。独立审查补IPAddress非同引用断言。首build两个nullable警告加显式target非null检查消除；首宽筛137中1红来自Task.WhenAll错误排序假设，改为先验证原Task树组成，再逐项引用/顺序比较，不排序或Flatten。修正后89定向通过。

8轮单点变异红例2/6/8/4/1/1/1/2（共25），依次删除SessionId匹配、ACK清零、外层join等待、反向包含、增加connect async包装、删除DeviceId校验、删除地址复制、删除敏感write等待。主线程逐条解析10份TRX，全部目标运行期断言，无Guard/编译失败充数；第8轮未运行WaitAsync候选，不冒称该变异已验证。源码7373字节与备份相同，SHA256 c0b8d17d6205ff314bd361f6a835c67b1243b1cc0d222d57352d9dff16a8f8bd；385个保护文件未变、34个恢复产物与基线一致，恢复重建89绿。最终solution/IsolatedAcceptance双配置四次Rebuild零警告错误；主线程逐条核验12份TRX，Debug/Release各3052 PASS（196/299/3/66/2095/393），0失败跳过；验证后源码/测试未变。详见HANDOFF§19.15。

**Limits / next**：expected/presented正常一致时，替换proof入参可能是等价变异；实际来源靠源码链路、偏差拒绝与真实换证共同验证。83字节既有proof transcript含nonce副本、未擦除，但不包含sessionToken/proof；本片不扩修它，不承诺TLS/Stream/HMAC内部副本擦除。FrameReader更底层已丢失的多fault不能在此恢复。测试接缝不是恶意进程内安全边界。尚无public AttachVideoAsync、Control唯一reader/远端关闭主动感知、采集/JPEG/显示或十分钟DoD；下一片先补Control reader生命周期再封闭公开外壳。无M4重验、现网/防护/App修改或新包。

### ADR-060 — 显式 Control 单读监控与完整原操作 join

**日期**：2026-09-30验证，2026-10-01记账。**Decision**：internal AttachVideoWithControlMonitorCoreAsync固定生产第二TLS入口；旧AttachVideoCoreAsync与Control-only仍不新增Control读。只有成功登记child的attempt在同父gate登记monitor，重复/clock重入输家不能启动它。父拥有至多一次1字节CancellationToken.None原读，无CTS/循环/新timer/心跳；EOF、故障或额外数据均撤销会话，额外数据固定control-unexpected-data，不静默吞字节。不是完整Control消息分发器，也不保证检测无EOF/RST的网络黑洞。

monitor Worker在gate内登记，首次取gate作为发布屏障；锁外执行ReadAsync同步前缀、AsTask及原Task。终态/错误/纯逻辑RevokeCore在同gate发布，锁外独立请求Control close及child stop，不自join；慢Control关闭不串行阻止child停止。父CloseAndJoinAsync共享Task.WhenAll(controlClose, videoJoin, monitor)，任一fault不得漏等另一个原操作。关闭完成不代表原读退出，不合作同步前缀/AsTask/原Task仍须真正返回；原同步Dispose合同及owner纯逻辑撤销不变。

保留原Task多fault完整树，仅去Task自动单项容器，不Flatten或删除原树重复分支；父LifetimeErrors纳入monitor原根。不可变snapshot分开保存End、Error、LocalStopObservedBeforeCompletion。首次本地撤销只按已发布原Task.IsCompleted判断观察顺序；未发布包括同步前缀/AsTask区间，不能证明故障物理因果。SkippedLocalStop不启动读，标记true。不得按IO/ODE类型或该观察标志抹去错误；未来public释放报告单独定案。

**Evidence**：f3dbccc新增65例（组件57/真实TLS8），定向含既有回归154绿。首轮75中3红为SkippedLocalStop期望、持gate等WhenAny观察代理、SslStream私有字段类型假设；分别按合同修正、直接观察已完成原Task、实测.NET10.0.12的NestedState枚举后修正。review构建曾有xUnit2012，等价改Assert.Contains后最终四Rebuild均零警告错误，不改写旧日志。独立审查补完整父异常树、ACK已成功后的提交闸门、frameRead原reader返回而联合提交前的owner精确释放；仅贯通既有rent/frameRead实例接缝，不替换认证或monitor。

6单点变异依次旧入口默认启monitor/删除原Task完成检查/原错只留首根/删原read等待/父join漏monitor/monitor不撤销，共15目标失败（1/2/2/2/3/5），6/6有效。第4另有附着前ODE不计目标，不称3/3；无Guard或编译失败充数。首runner误判xUnit FAIL通知为基础设施错，已恢复后在独立续跑目录纠正；旧证据保留。主线程逐条核验13TRX、3源码原bytes、320保护SHA、34恢复产物、29自有Job排空。最终12TRX逐条：Debug/Release各3117 PASS（196/299/3/66/2160/393），0失败跳过；solution/IsolatedAcceptance双配置四Rebuild零警告错误，303源码/测试/工程/脚本文件哈希前后一致。证据outputs/m5-control-monitor。

**Limits / next**：尚未确定性固定Committed已设置而worker/外层Task尚未完成的最窄窗口；已有提交前撤销和完整交付后撤销证据，不能外推。登记竞争测试不独立证明所有生产锁边界；真实TLS事后await只证明最终退出，强join证据来自组件闸门/await链。下一片封闭public视频外壳固定monitor生产路径、原Task读帧与完整异步释放；本片public尚未实现。不改现网/防护/App，不重跑M4、不打新包。

### ADR-061 — 封闭 public 视频接口、交付投影与稳定异步释放报告

**日期**：2026-10-01。**Decision**：新增sealed AuthenticatedVideoSession，只提供ReadFrameAsync(token)与IAsyncDisposable，不公开Stream/TLS/token/工厂/目标。父增加AttachVideoAsync(token)和IAsyncDisposable，原同步Dispose合同不变。生产入口固定真实TLS/严格ACK/monitor路径；资格/重复拒绝同步抛出，成功登记前的失败不消费attempt，重入输家不停止或覆盖赢家。

child构造时预建PublicSession；public读取直接返回底座原Task并透传token，已交付帧归caller。投影仅await底座成功后返回预建对象，不新增取消、父状态或预算终检；父停止可使返回的对象已失效，但不能推翻此前联合提交。测试projectionScheduler只延迟空操作Task屏障，先建立await屏障的投影并在父gate发布，再Start；避免QueueTask同步重入父释放或抛错留下未追踪attempt。调度异常由原faulted屏障保留，投影停止自己的child、等待底座包装Task及raw join，完整聚合原调度/操作/清理错误，不把已接受后错误改成同步资格拒绝。默认TaskScheduler.Default，不继承任意调用方scheduler。

父DisposeAsync缓存一个报告Task，先逻辑撤销并启动raw CloseAndJoin，再WhenAll等待raw join和已接受public投影；子独立缓存报告Task，只等自身StopAndJoin，不等父或public投影。依赖单向，原worker不自join。排空后取稳定诊断，按引用双向包含归并顶层，完整保留原树、兄弟和重复叶子；不Flatten、不按IO/ODE/OCE或local-stop观察过滤。无错成功；任一错误统一AggregateException，包括单OCE，重复调用观察同一Task及异常实例。同步Dispose/操作/父子报告可分别观察同一原错，不实现全局消费一次。异步释放不能在被跟踪操作/回调内等待；不合作原代码仍无硬中断承诺。

**Evidence**：新增50展开（42组件/8真实TLS）。测试包含public表面封闭、真实身份/hello/ACK/monitor、重复/clock重入、预建投影受闸、原Task透传和owner归属、同步前缀/AsTask/原I/O完整join、父子报告范围与完整错误树。独立审查补投影未完成时子释放可独立结束、测试主错误保留/总清理预算，以及scheduler同步/失败/重入、有效caller取消。首build 7个xUnit2014和2个CS4014为测试写法错误，改Record.Exception同步断言和显式追踪，不放宽同步拒绝合同。review Debug构建0警告错误、50定向通过；修后复审无新增阻断。

八候选的有效证据跨两个目录：outputs/m5-public-video/mutation-20261001-043937-00a86d4f中1/3/4/5/6/7/8，加mutation-02-assert-null-20261001-050104-b231e5dc中的2。有效目标断言1/2/2/3/3/3/3/3共20；首轮1的组件Guard、首轮2的两个非显式断言ODE不计，原证据不覆盖。第2仅给成功合同加Guard外Record.ExceptionAsync及Assert.Null后单点复验；其余7项不重跑，不称单目录同测试版本8轮。主线程逐条核验14TRX及生产两文件原bytes，311保护文件、4产物前后核验；各基线/恢复50通过。最终双配置全量与具体提交见HANDOFF§19.17。

**Limits / next**：投影闸门固定的是core已完成/public尚未完成窗口，不扩称底层Committed已设而原worker未退出最窄窗口。真实TLS只证明最终退出，强join依赖组件实际awaiter链。子报告不包含父拥有的投影调度错误；父会报告它。主动关闭可能返回IO/ODE/OCE诊断，调用层须保留主错并单独呈现收尾错误。M5仍欠GDI/JPEG/有界流水线/WPF显示/Host产品装配与十分钟DoD，本片不是产品看屏完成。未改M4、现网、防护或App。

### ADR-062 — 主屏 GDI 拥有型采集与硬边界 JPEG 编码

**日期**：2026-10-01。**Decision**：Capture实现既有IScreenCaptureBackend/IFrameEncoder，保持net10.0-windows，仅增加UseWPF，不引入System.Drawing或版本化WinSDK。GDI仅当前主屏，EnumDisplayDevices/Settings取物理模式，设备DC本地(0,0)，top-down32bit DIB、BitBlt(SRCCOPY|CAPTUREBLT)、GdiFlush后复制并设alpha255。交付前复查显示身份/几何；不修改DPI awareness、权限或桌面。旧选入对象与bits借用，先恢复再删除；恢复失败先删除memory DC，若也失败不得删仍可能选入的bitmap。所有清理错误保留，失败不交付；无扩展错误合同的API不猜LastError，仅BitBlt/CreateDIBSection捕获可信扩展错误。

JPEG用确定性最近邻、BGR24、floor缩放到至少1×1，尊重源stride并清零padding；支持既有质量档位。输入raw借用直到原Encode完成，输出实际payloadLength独立owner；帧序号仅成功交付消耗，最大值后拒绝不回绕。PooledByteOwner只暴露精确长度、整个池数组清零归还。每实例单飞、不排队；Monitor.TryEnter保护公开operation登记，资格取决于其完成状态，公开operation直接await原Task.Run，取消不代理完成/不提前释放原生操作仍使用的资源。OriginalWorker仅internal只读测试观察，不作产品依赖。

**关键实测**：固定数组MemoryStream限制输出容量，但WPF/WIC Save会吞底层Write的NotSupportedException并正常返回（本机超限样本length0），不能只依赖Save是否抛错或Length>0。JpegOutputStream锁存首个底层写/长度/定位错误，Save后先重抛原错；Save自身另错时完整保留两原对象及原树。测试吞错后写4字节正长度片段仍拒绝，不能交付截断JPEG。32MiB是编码输出上限，非raw/池桶/WIC总内存上限。TargetFps只校验，上层调度初始5FPS/50%/quality60。

**Evidence**：99dfd76，157新增展开（GDI92/JPEG57/owner8），合成JPEG真实解码、3例真实离屏GDI，不读取实时桌面。首145中144绿/1红促成WIC吞错修复；追加12项回归。独立审查后修原await观察点：从公开operation读取任意已登记awaiter Task再Assert.Same原worker，就绪不能绑定正确目标；两批八变异有效32目标红(5/3/3/4/1/9/6/1)，首轮8Guard排除不追认，六有效不重跑。复验后4处xUnit2031等价改predicate重载。最终四完整Rebuild零警告错误，Debug/Release各3324 PASS，主线程14TRX与319源码SHA独立核验，详HANDOFF§19.18。

**Limits / next**：无真实DISPLAY DC/混合DPI/长期内存稳定或产品看屏结论；原同步native/WIC不返回则不承诺停止时限。删除API失败可残留资源但明确报告。Sessions两级有界生产管线、Host公开安全装配、WPF显示及十分钟DoD随后实现；M4/现网/防护/App不动。

### ADR-063 — Sessions 双级有界帧生产、原操作完整join与两阶段EOF

**日期**：2026-10-01。**Context / Decision**：50d54af（7 files/+3659）实现独立生产FramePipeline，不代表Host/App已接线。Sessions保持net10.0、仅引用Core；raw/encoded复用BoundedFrameQueue，各容量仅1或2（默认2）、DropOldest，无无界队列。固定5FPS/0.5缩放/quality60/Auto=false，每轮从实际capture启动计时，扣除本轮耗时后等待200ms剩余预算，超预算不补帧/追赶。capture/encoder须专用借用至Completion完成，管线不Dispose依赖，拥有型factory另片实施。

Start为一次性CAS；Stop先赢则永不启动，Start先赢则收尾必须等待发布屏障，覆盖部分创建失败及两个真实循环引用。构造不采集；Task.Run不传取消token以确保已调度的同步前缀/清理真正执行。internal Start发布观察仅friend Sessions.Tests可用，不公开回调、不借friend装配产品。

原Capture/Encode的ValueTask只AsTask一次，直接await原Task，不能以取消代理提前结束；raw借用活到Encode真实退出。迟到结果先获得ownership再检查停止；交付帧归caller、无后置取消检查。清理前清空本地owner并仅尝试一次Dispose，drop disposer错误也进入完整报告。StopAsync与DisposeAsync复用稳定Completion，完整join两个原循环（含同步前缀/异步尾部/本地清理）及raw.Stop、encoded.Stop、Cancel三个独立任务；全部退出才Dispose CTS，慢disposer/callback不能串行阻塞其他清理。原操作不合作仍不承诺停止时限，回调内部不得等待自身join。

错误保留全部原fault根/原Aggregate嵌套对象，不Flatten、不只取await抛出的第一根；首错请求停止，有限清理错追加。只有非Faulted操作的OCE携带本管线token且已请求取消时可作正常取消；Faulted任务内OCE、时钟/清理OCE仍是错误，统一Aggregate报告。EOF分两阶段：单消费者队列读取可取消，caller取消不停止生产；仅转换内部队列停止ODE，先释放读槽，再直接且不可取消地await Completion。干净返回null，否则共享稳定错误；允许多个调用同时等最终EOF，协调者不join read以免互等。已交付帧不被收尾回收。

**Evidence**：新增104展开（Flow36/Lifecycle60/Start8）。initial日志82测80绿/2红均Expected null：fixture Tail.Entered先于Invoke发布Worker，改先等InvocationExited再比较原await身份；不将其写成生产取消故障。initial xUnit2031改Assert.Single谓词重载，fixed CS4014改显式丢弃AssertLoopFailure返回Task，均等价且保留旧日志。证据`outputs/m5-frame-pipeline/initial-{build,test}.log`、`fixed-{build,test}.log`及对应TRX。

`outputs/m5-frame-pipeline/20261001-093617-singlepoint8/report.json`八单点8/8有效，目标红1/2/2/2/8/2/1/2共20，主线程19TRX逐项核验、0Guard/其他错误/编译失败，每轮恢复目标绿。1/2是发布屏障/EOF的实际await身份；3/4在首轮原Capture/Encode await身份即红，不声称进入取消时序；5实际红于根数，6红于剩余延迟，7红于迟到capture调用数，8由明确Assert.False报告协调者提前退出，不是join注册数或Guard超时。变异保护55项与final冻结328项不同scope。

最终`final-20261001-094907-708217/summary.json`（同上根目录）：四Rebuild零警告错误，六命令rc/hash核验一致；主线程16TRX逐条确认Debug/Release各3428 PASS（Core196/Security66/Protocol299/Integration3/Transport2210/Acceptance393/Capture157/Sessions104），0失败/跳过。328文件前后及记账前当前SHA一致，含HANDOFF，后续文档变更不冒充全清单仍相同。final枚举被拦，仅确认runner与6direct退出，不外推间接子进程或全机排空，不套用变异进程结论。完整实证见HANDOFF§19.19。

**Limits / next**：无App接线/实屏/十分钟DoD，M5未完成。下一片Core生产factory/producer安全contract + Sessions拥有型factory；之后Transport每已认证会话生命周期public装配，ACK flush后创建、停止join、无迟到read初始化，再App/WPF显示。以上Host合同是计划而非已实现，不以read委托或friend绕过生命周期/分层。无M4重跑、现网/防护修改、权限绕过或新包；本轮仅文档记账，不运行dotnet、不改代码测试、不提交。

### ADR-064 — 会话拥有型视频生产契约、完整依赖清理与Host认证装配边界

**日期**：2026-10-01。**Context / Decision**：`9cede86`（6 files/+2583）在Core增加public `IVideoFrameProducerFactory`与`IVideoFrameProducer`，供未来Transport真实Host生命周期装配，而非通过internal friend/裸read委托绕层。`CreateAsync(Guid sessionId, token)`接受非空会话关联ID，返回尚未启动的生产者；Guid不是凭证，工厂不验证认证。生产者只暴露稳定`Completion`、一次`Start`、单消费者`ReadNextAsync`、`StopAsync`和`DisposeAsync`，读取只交付`EncodedFrame`/EOF，不暴露Stream、TLS、secret、会话token或认证状态。工厂构造仅保存委托、1或2的raw/encoded容量与时钟，不采集；对象由调用方停止/释放，工厂本身借用。

Sessions公共`FramePipelineVideoProducerFactory`对每次创建依次取得会话专用capture、列表内唯一IsPrimary显示（不默认第一个）、会话专用encoder及未启动FramePipeline。委托同步前缀在不携带取消调度token的原`Task.Run`内，直接await原创建，不以`WaitAsync`/取消代理提前完成；各取得阶段返回后查创建取消，失败/取消完整回滚实际持有对象，全部原错和清理错保留。交付后创建token不再支配生产者。依赖须专用且不得由外部并发操作/释放；同一实例实现两个接口时仅按`ReferenceEquals`清理一次，不按值相等合并；工厂、委托目标、时钟不归生产者拥有。

适配器创建即观察管线Completion，生产失败自动收尾，停止/异步释放共享稳定任务。先完整等管线原循环及清理，再并行调度两个依赖清理并完整join，包括Dispose同步前缀和异步尾部；同时实现`IAsyncDisposable`/`IDisposable`时优先异步，不再重试同步。已交付帧属caller；普通读取取消不停止生产，EOF/生产错误须等依赖清理完成并给同一完整结果。收集原Task多个fault根和嵌套Aggregate实例，不Flatten，不将faulted任务的OCE/清理异常伪装为正常取消；不join读取以免EOF反向等待产生环。依赖和回调不得等待自身生产者Completion/Stop/Dispose；不合作原操作没有硬停止时限。

**Evidence**：工厂首轮200绿但CS4014/xUnit2031两警告，测试写法等价修复；生产适配器首编译CS0246来自边写边编译的夹具，第二轮217/218唯一tail故障夹具过早注错，修复后218绿及零警告/错误；原日志均保留，不把旧红改绿。Sessions新增114（Factory96/Producer18）加原104=218。七单点变异：创建取消代理/采集后漏取消/首显示/只等管线/WhenAny清理/值相等去重/只取首错，目标断言红3/1/2/2/12/6/3共29、0Guard，各轮恢复绿；旧分类器第1轮`not_killed`为漏报，审计及直接原TRX证实真红，变异保护21项不同于final334项。证据`outputs/m5-frame-pipeline/factory-mutation-20261001-104604/report.json`、14份变异/恢复TRX及`outputs/m5-video-producer/final-20261001-105537-153331700/summary.json`。最终四次solution/IsolatedAcceptance双配置完整Rebuild零警告/错误、六命令rc0及日志SHA一致；16份final TRX实际逐条Debug/Release各3542 PASS（196/66/299/3/2210/393/157/218），0失败跳过；334项SHA阶段前后及记账前当前一致，含HANDOFF，文档更改后此项例外。仅runner与六个直接dotnet子命令退出可证，全机进程枚举未有效，不能外推间接进程排空。详情见HANDOFF§19.20。

**Limits / next（仅§19.20/ADR-064当时）**：本工厂不承担认证，Transport Host公共入口尚未落地。下一片在每个已认证Host会话ACK flush成功后创建producer，父子停止完整join且禁止迟到read触发初始化；随后App/WPF显示及实屏/十分钟DoD。现阶段没有产品看屏闭环，M5仍未完成；不重跑M4、改现网/防护、绕过桌面权限或出新包。这里“此轮仅文档记账”是上一片历史，不适用于随后`2e48cb4`源码/测试变更。

### ADR-065 — 认证Host视频工厂装配、原异常实例证明与原Task身份报告

**日期**：2026-10-01。**Context / Decision**：`2e48cb4`（9 files/+1415/−23）提供public `TransportHost.CreateWithVideo`认证装配，把Core借用型`IVideoFrameProducerFactory`交每连接`FirstFrameRouter`，沿用已实施第二TLS/严格VideoAttach证明。原shared `IVideoFrameSource`和internal router、Control-only路径仍兼容，工厂不读取Stream/secret或仅凭Guid认证。VideoAttach成功后ACK写入、flush及阶段/信封核查均过才允许调用工厂；创建操作包含可注入同步前缀/异步尾部且被transfer完整join。迟到成功交付的producer仍接管清理，固定来源只读本已接管实例，不因迟到Read另建对象。`Start`须在producerGate**锁外**执行，使可注入同步阻塞不能使Stop连抢占资格也办不到。

Stop先赢时只有`IVideoFrameProducerStartRejection.IsStopBeforeStartRejection`对本次Start抛出的**原`InvalidOperationException`实例**返回true才可识别正常拒绝；不能按类型/消息/停机时序推断所有InvalidOperationException都是正常。探针抛出IOException/ObjectDisposedException/OperationCanceledException/嵌套AggregateException必须单独记账且不覆盖Start原错；证明true也不豁免后续Stop/Dispose故障。Control关闭、peer断开、Host取消、lease撤销触发Video路由关闭、取消、Stop，路由join自己的transfer/peer/关闭/取消/Stop/Dispose原操作，来源fault根与清理错保留嵌套、不Flatten、不按错误类型泛吞。Stop与Dispose若返回**同一原Task**，多次观察的同一错误树只入账一次；不同原Task即使抛出**同一Exception实例**仍分别记两根。已交付帧归消费方，原操作不合作不承诺有界退出。

**Evidence**：初轮Host边界57/57；锁内Start阻塞Stop及宽泛拒绝分类修复后80绿；同一Stop/Dispose原Task诊断重复的4红修正为按Task身份去重后83绿、Transport全量2252绿；再追加不同原Task同异常实例双根3例，最终Transport2255。证明探针自身IO/ODE/OCE/聚合抛错有四展开覆盖。首`outputs/m5-host-video/final-20261001-120711-095134700/summary.json`只完成Debug Rebuild（0警告错误），因测试文件并行变更触发源码SHA冻结失败并自动停，0次test，**保持failed**。新`outputs/m5-host-video/final-20261001-122116-867239300-retry/summary.json`独立终验：16份TRX逐实际结果/Counters两配置各3589 PASS（196/66/299/3/2255/393/157/220）、0失败/跳过；四完整Rebuild均0警告错误，六命令rc及六日志SHA一致，337项源清单前后/记账前当前逐SHA相同（含HANDOFF，文档编辑后例外）。仅runner与六直接子命令退出可证，全机枚举受拦，不能外推间接子进程排空。详HANDOFF§19.21。

**Limits / next（仅ADR-065当时）**：`CreateWithVideo`仅是已认证Video路由的局部装配，**当时不是Host产品生命周期完全闭环**。下一片Registry须同锁proof资格预约；`AttachAsync`成功后的截止/异常检查必须释放预约且一次性资格不得恢复；Control注销先零化token后能join Video原Task真实结束；Host预算Stop如实报告，Dispose完整无界排空。这些Transport生命周期项已由后续`b3e1273`/ADR-066实施；此处历史描述不覆盖当前进度。之后才App/WPF解码显示、实屏与十分钟DoD，**M5仍未完成**。不重跑M4、不修改现网/防护、不绕过权限、不出新包。

### ADR-066 — 一次性Video预约与Control子任务join、Host预算报告及完整释放

**日期**：2026-10-01。**Context / Decision**：`b3e1273`承接`45dd8db`/HANDOFF§19.21/ADR-065，16 files/+2225/−423。`SessionRegistry.TryAttachVideo`必须在同锁内复核会话存在、Control取消、Video取消、期限、Control/Video不同连接、同源IPv4、服务器证书SHA与proof后预约唯一`VideoAttachLease`并消费一次性资格；取时可能重入，因此其后再次按条目身份检查。`AttachAsync`成功后立即将lease登记为父持有对象，后续阶段/信封/登记等待预算检查任何一个失败也不退还一次性资格；由路由的关闭和原任务join释放预约。注销在同锁移除条目、零化登记表持有sessionToken并发布`Revoked`（TCS异步continuation），不在锁内跑外部回调；父在锁外等对应lease原`Completed`。Video路由仅在自己的`RunAsync`主体与Host发起的原关闭任务及Control owned操作真正退出后设置`Completed`，不能用请求关闭、取消通知或计数降零冒充真实子退出。只承诺登记表自有token零化，不承诺TLS/DTO/WPF内存副本擦除。

Host `StopAsync`维持显式预算：accept和连接未完成按真实结果报告，根取消同步回调单独未退出时抛`TimeoutException`，绝不虚构未完成连接数。`DisposeAsync`并发调用共享稳定任务，启动自身Stop、join当时在途各Stop、accept、Host原取消，再等Registry原连接、force和linked取消源解绑后释放Registry与Host两根；错误不能中断其他独立清理，返回完整在途故障原树。不合作原操作没有硬中断或Dispose完成时限。每个**已完成**Stop fault保持在调用方拿到的原Task可await，`HostLifecycleErrors`内部只按固定类别各留首错（最多七类），Dispose不重复上报这些历史任务；**Dispose开始时在途**Stop的故障必须完整汇总，不能因内部诊断有界而丢掉调用方原错。Stop的预算报告不能当成Dispose完整join，原Exception树不Flatten或按IO/ODE/OCE泛吞。

**Evidence**：六单点变异及每轮原bytes/SHA恢复，基线10目标参数全绿；六轮目标明确Assert红1/1/4/1/1/1，合计9红、0Guard/清理超时，每轮恢复绿，第5轮仅`stopFirst=False`红、`True`绿，因此“6/6被目标检出”不等于“六理论行全红”（实际5/6轮全部参数行红）。变异和恢复Debug Rebuild均rc0/零警告错误，证据`outputs/m5-host-video/parent-mutation-20261001-134448-012529700/summary.json`及`parent-mutation-final-20261001-134942-043130500/mutation-evaluation.json`。首轮基线xUnit1031警告停、第二轮脚本误递归outputs路径过长未运行；第三轮旧runner预计2278但实际Debug Transport2279全绿后按严格旧计数停，Release另目录2279全绿；旧失败证据不覆盖。变异35保护文件与终验337文件不同范围。

完整终验`outputs/m5-host-video/final-parent-dispose-20261001-135550-001799400/summary.json`与原六日志/16 TRX：Debug/Release各3613 PASS（Core196/Security66/Protocol299/Integration3/Transport2279/Acceptance393/Capture157/Sessions220）、0失败跳过；solution/IsolatedAcceptance双配置四次完整Rebuild 0警告错误，六命令rc0且六日志SHA核对一致。337文件在各阶段前后及本次文档编辑前当前SHA一致，含HANDOFF，编辑后其变化为明示例外。runner和六直接子进程退出，**全机枚举受限，不证明间接进程排空**。详HANDOFF§19.22；本次文档记账未另运行dotnet，不覆盖此前16文件代码/测试变更。

**Limits / next**：只完成Transport已认证父子生命周期及Host释放合同，不等于产品App Host/Client/WPF看屏可用。继续App真实Host启停+ViewOnly本地审批、Client高层认证/prompt key、WPF有界解码渲染/完整退出，再实屏与十分钟DoD；**M5未完成**。无M4重跑、现网/防护修改、权限绕过或新包。

### ADR-067 — 产品ViewOnly显式授权、原任务释放证书与首装发现边界

**日期**：2026-10-02。**状态**：产品App/Transport/Discovery及测试已实现于**未提交工作树**、限定自动化实证通过；不是已提交App代码、两机GUI实屏、发布或M5完成。**Last code commit：`b3e1273`**（已提交Transport历史基线，各配置3613 PASS），其后的文档提交不代表本轮产品代码提交。**Working tree at validation：`outputs/m5-host-video/final-m5-host-video-20261002-100918-163800800/summary.json`对应未提交产品工作树**，详HANDOFF§19.23。

**Context / Decision**：
1. Host沿用ADR-045现网只读原则与ADR-048/066认证资格门禁。配置严格读取安全布尔字段和固定端口，身份/证书/本机密钥校验后，**仅本机显式开启查看共享**且WPF审批门可用、合格RFC1918绑定存在时才装配`CreateWithVideo`监听；历史配置或单纯勾选`AllowViewing`不算本轮开启，`AllowControl`恒关闭。撤权/停机先同步禁止新监听和审批，再等待原Host Stop/Dispose；故障不伪称排空。
2. 本机真实`WpfLocalApprovalGate`对ViewOnly请求展示实际`Show()`窗口，显示自称身份、来源及仅供人工核对的短码；操作者须明确勾选“仅查看”才可批准，Control请求直接Denied。阻断时先把待批决定置不可用，后异步关窗并**等原`Closed`呈现任务**；Dispatcher被阻、重复RequestId、过期与迟到旧按钮均不能重新授权。批准/拒绝这一决定不等于窗口物理关闭、成功认证或显示真实画面；不将UI不可用当自动批准。
3. Client冻结目标，远端访问key只来自本次遮挡输入/调用方本次临时参数（非命令行密钥入口），不用本机密钥冒充；高层Control与独立Video均用真实TLS、实际pin/serverProof/权限验证与严格VideoAttach/ACK；屏幕接收端只解码经JPEG边界及像素预算检查的帧，容量1的拥有型队列丢旧、后台解码、WPF单槽呈现冻结`BitmapSource`。不公开裸TLS/secret或发送键鼠事件，ViewOnly不是控制权限。
4. 原I/O、取消或关闭异常不得按类型一概吞掉。Transport父`GetDisposalEvidenceAsync`为不透明**只读**资源证书：等待同一原Dispose报告任务，即使它fault，也核对原Control/Video worker已join及TCP/SSL双根、timer、reader、未交付帧owner、CTS与registration正向释放；子证书仅internal，旧异常树和原Task保留。Presenter和Client各绑定同Attempt的原清理Task/原worker，报告清理与业务错误而不洗绿。App缓存六个原停机Task并独立复核：六项全成功且Host/Client根排空为Clean；**仅**Client原Task fault而其他五项与复核全成功、Host根排空、同一原Client Task持完整资源根证书时，才可`DrainedWithDiagnostics`退出并保留诊断；其余fault/取消或证据不足为Unproven，尚未完成为Pending。重试不得覆盖原错。
5. **首次安装≠自动共享**：严格读取同一config文件确认确实缺失时，身份就绪后可自动公告本机身份，但view/control能力都为false且Host未监听；坏/旧配置不得公告，显式关闭发现不得被覆盖。历史`AllowViewing=true`也不自动启监听；只有显式开启成功才可广告view。此处本机隔离无socket发现测试不构成两台设备实际发现或双方互联网无影响的证明。

**Evidence / 可证范围**：`ProductRealViewOnlyLoopbackTests`经测试专用Loopback策略与手动gate明确批准，在真实Control+Video TLS中把2×2真实JPEG送至离屏已Show的WPF窗口并断言冻结像素；审批前无View/producer，第二次原读在途时Close，原OCE/IO诊断、同一Cleanup Task/释放证书及二次关闭按钮可观察。**该gate是夹具，不是产品真实审批按钮**；`ProductHostApprovalRaceTests`另以真实WPF审批窗口核对Shown/Closed、阻断先于Dispatcher及旧按钮/截止竞态。`ProductFirstInstallDiscoveryTests`覆盖首装、坏/旧配置和显式禁发现；`ProductExitDrainStateTests`及Client/Presenter/Transport测试核对原Task/错误树与释放分类。三个受控单点变异分别首装不公告3目标红→恢复33绿、坏配置错误公告3红→恢复6绿、旧六Task分类4红→恢复10绿；较早xUnit/构建警告、`Closed`测试误注错、80ms假红及运行时TLS取消OCE/IO竞速保留为历史，不反填通过。

最终冻结`summary.json`为passed/exit0；16份TRX逐`UnitTestResult`与Counters核对，Debug/Release各**3712 PASS/0失败/0跳过**（Core197/Security66/Protocol303/Integration3/Transport2297/Acceptance469/Capture157/Sessions220），solution/IsolatedAcceptance双配置四次完整Rebuild零警告错误，六命令rc0/原日志SHA一致。350项源/测试/工具/脚本及指定根文件在冻结前后与六阶段边界逐SHA一致（`ad8462c3b5784cf995485e90fe5b8d96487379da435bdd1703c7c943eb8feeb9`），Git状态原bytes前后同SHA `afb6f939e5eb5efff2a076c4ade01d1ca7bb99460cc9ba745dc454583f7e62f2`。**清单含HANDOFF，文档编辑后该项变化是明示例外，不宣称现时350/350相同或已重冻结**；本次记账不运行dotnet。只证runner与六个直接dotnet子任务wait退出，全机/间接进程不保证。

**Limits / next**：loopback合成画面≠两台实际LAN上产品GUI显示真实用户屏幕；实际本机审批人工可见/拒绝/停止恢复、前中后双方互联网无影响、十分钟内存增长与慢网丢旧/延迟及原任务完整排空DoD仍须按实测补齐；真实混合DPI不被离屏窗口覆盖。M5保持**进行中**，无键鼠控制，不重跑M4、不改DHCP/IP/DNS/route/热点/ICS/网络类别/防火墙或系统防护、不绕桌面授权、不出新包；未经提交的产品工作树不能写成提交事实。

**2026-10-02 后续收窄：已有配置的发现开关不能取默认值。** §19.23历史冻结后确认：有`config.json`且已提供查看/审批安全字段、却缺`allowDiscovery`时，反序列化默认`true`曾使设备身份误公告；大小写不同的重复发现字段也可由后者覆盖。Host加载同一配置时必须对`allowDiscovery`执行单值、布尔值、与`AppConfig`解析值一致的校验；遗漏/非布尔/null/重复一律拒公告和拒监听，只有**确实不存在配置文件**的首装独立分支才能公告身份（零view/control）。隔离测试旧实现42例中6目标Assert红/36绿，收窄后42/42绿，原红证据保留。**截至2026-10-02的Working tree at validation**：未提交产品源码/测试对应`outputs/m5-host-video/final-discovery-strict-20261002-103748-589902100/summary.json`，status=passed/exit=0；双配置各3721 PASS/0失败跳过（Acceptance由469增至478，其余七项目数量不变），四次solution/IsolatedAcceptance完整Rebuild均零警告错误，16TRX逐项与Counters一致，350项冻结前后及12阶段边界同SHA `891220822d0bb6b731279b001d24c37578eb59f9a08c142c33cb3dfc3b575546`。此后编辑HANDOFF是冻结清单内的文档例外；该次记账未重跑dotnet、未提交或出包，上一3712轮为修复前历史，最近已提交代码仍`b3e1273`。2026-10-08新预览档工作树另见HANDOFF§19.25，不反填此处旧冻结。

### ADR-068 — M5 显式主机增强预览只在下一次开启时生效

**日期**：2026-10-08。**背景**：用户反馈旧M5实机GUI“测试通过”但画面糊/帧率低。旧生产路径固定`FramePipeline` 5 FPS、50%缩放、JPEG60，加上Client WPF Uniform拉伸，画面模糊是预期第一档代价。不能操控则属于尚未开始的M7键鼠控制：本轮不能因为画质需求扩大Control授权或跳过本机审批；用户一句“测试通过”也不等于提交了十分钟内存/慢网/两端互联网无中断的数值验收。

**决定**：M5仅允许旧默认`(5 FPS, 0.5, JPEG60, Auto=false)`与本机主窗口**明确勾选**的临时增强预览`(10 FPS, 0.75, JPEG60, Auto=false)`。选择不写入`config.json`，点击“开启仅查看共享”时快照，下传`HostRuntimeService`已认证`CreateWithVideo`工厂，工厂为每个会话生成专属管线，按100/200ms节拍采集；编码后的主屏帧仍走两条独立TLS的认证附着。运行中的Host不允许热更，切档必须先紧急停止并完整排空，再本机显式开启；已发布档位与真正Host同源、状态文字报当前档。Host在读取身份/秘密或网卡前拒绝任意其他档位，Factory/FramePipeline也禁止20/30FPS、100%和Auto或非白名单参数；这不是M6的用户级每会话画质、质量预设与自适应。两级队列维持容量1–2 DropOldest，Client WPF仍只解码ViewOnly且不发键鼠；TLS/pin/HMAC/DPAPI/RFC1918/审批/紧急停止逻辑不削弱，不改系统网络/防火墙/防护。

**实证与边界**：旧入口缺参数的新增产品合同在`outputs/m5-host-video/20261008-preview-red-build.log`准确以CS1739编译红（只是接口红，不冒充运行期变异）。新增手动时钟100ms、厂向encoder透传10/0.75/60、拒非法参数且不获得资源、产品主机无权限不触网/不持久化档位，以及原默认5/0.5/60测试；中间工厂夹具未释放原fake致30s看门狗红，修夹具放行后6/6绿，保留失败日志不翻绿。**Last code commit：`b3e1273`（历史提交，与本轮未提交主机/管线测试不同）。Working tree at validation：`outputs/m5-host-video/20261008-preview-reviewed-{debug,release}-{build,test}.log`当前未提交工作树**，Debug/Release solution Build各0警告0错误、8项目各**3733 PASS/0失败/0跳过**（Core197/Security66/Protocol303/Integration3/Transport2297/Acceptance482/Capture157/Sessions228）。这是构建/文本测试阶段实证，**并非四次完整Rebuild/16份TRX/全源码SHA冻结、非新版两机实屏或稳定10FPS证明**。画质或FPS是否改善及10分钟内存/慢网积压/双方互联网无影响须用新哈希双机实测，旧2026-10-02 ZIP不能替代；M5继续进行中，M7控制未启用。详HANDOFF§19.25。

### ADR-069 — M5 流畅预览15FPS固定档，用户实机确认与独立证据分层

**日期**：2026-10-08。**背景**：用户明确确认十分钟内存、慢网积压、双方互联网始终可用逐项测试通过，允许继续。但未给原始分钟采样、帧龄曲线/网络时间线、两机运行ZIP的SHA；这些只能记作**用户确认的实机Pass**，不捏造数值或将结论外推到随后修改的二进制。原规格`07_MILESTONES_AND_TASKS.md`将10/15/20/30FPS与scale/quality置于M5“然后”，M6才为多屏与每会话预设，M7为控制，因此继续M5而不提前启Control。

**决定与不变项**：保留旧默认`(5,0.5,60,false)`与10FPS实验`(10,0.75,60,false)`，增加仅本机显式勾选的`(15,0.75,60,false)`、与10FPS互斥的流畅预览；勾选本身不授权共享/不写config，只有点击“开启仅查看共享”且安全资格通过后才映射到Host会话级不可变生产工厂。已监听更改选项仅提示当前真实档位，必须先紧急停止并join后再次本机显式开启；不支持的画质或Auto在身份/网络接触前拒绝，FramePipeline及Factory保持精确三档白名单与1~2 bounded DropOldest，不静默钳制到Balanced=20FPS。15FPS的`TimeSpan`目标约66.667ms，但`.NET Task.Delay(TimeProvider)`下fake定时器记录66ms毫秒粒度，目标值不能冒充真实墙钟帧率；JPEG60、主屏Only、双TLS/pin/HMAC/DPAPI/本机审批/同子网/紧急停止与ViewOnly隔离保持不变。M6 presets/多屏、M7键鼠未实现。

**验证与限度**：有一次测试因用名义66.667ms当作fake计时器66ms导致1红/11绿，属于期望公式误设，不以生产行为已变红或后来绿测覆盖该历史；修测试后定向Sessions12/12、App配置39/39绿。**Last code commit：`b3e1273`；Working tree at validation：`outputs/m5-host-video/20261008-15fps-{debug,release}-{build,test}.log`对应未提交产品/组件/测试**，Debug和Release各solution Build零警告错误、八程序集各3740 PASS（Core197/Security66/Protocol303/Integration3/Transport2297/Acceptance485/Capture157/Sessions232）；未跑四完整Rebuild/16TRX/源字节冻结，也未制作或双机运行15FPS新哈希包。用户旧版本的DoD口头Pass不等于新版本15FPS已实机通过。详HANDOFF§19.26，M5仍进行中。

### ADR-070 — M5 20FPS/75%单轴实验档保持显式授权与精确白名单

**日期**：2026-10-08。**背景**：原规格M5“然后”依次覆盖10/15/20/30FPS、scale与quality，现有15FPS/75%仅本机自动化尚未两机实测；用户提出继续按计划开发，不等于批准提前实现M6多屏或M7键鼠。继续保持旧5FPS/50%默认、10/15FPS/75%显式档和所有产品安全边界。

**决定**：本片仅新增精确 `(20FPS,0.75,JPEG60,Auto=false)` 的一次性Host快速预览；App三个实验勾选互斥，点击“开启仅查看共享”时检查任何同时选择并快照，已监听更改选项只报告真正已生效档位、须先本机紧急停止并完整排空后再显式开启；勾选不写config、不授监听、不改变Control/ViewOnly权限。`FramePipeline`构造期及每会话`FramePipelineVideoProducerFactory`和`HostRuntimeService.StartAsync`启动身份/网络之前同时精确白名单新增该档，其他`30FPS`、非本片缩放/JPEG质量、Auto仍拒，不使用`Balanced`隐式默认。队列raw/encoded各容量1–2且DropOldest、不可撤回TCP已缓冲帧的局限及双TLS/pinning/HMAC/DPAPI/RFC1918/本机审批/紧急停止契约不变；20FPS是**目标采集间隔50ms**而非实际FPS承诺。

**证据**：先扩`Sessions`合同用例在旧白名单观察预期2红/15绿，原日志`outputs/m5-host-video/20261008-20fps-red-sessions-test.log`，不是测试错配；新增UI互斥和无权限/无网络、Host拒非法20组合及factory透传后定向17/17、45/45绿。**Last code commit：`b3e1273`（Transport历史），Working tree at validation：未提交App/Sessions生产及测试对应`outputs/m5-host-video/20261008-20fps-{debug,release}-{build,test}.log`**，两配置solution Build各0 warning/error、8程序集各**3751 PASS/0 FAIL/0 SKIP**（Core197/Security66/Protocol303/Integration3/Transport2297/Acceptance491/Capture157/Sessions237）。没有四Rebuild/16TRX/全源码SHA冻结，没有20FPS新包或实机数据，不将较早用户确认的10分钟内存、慢网积压和上网Pass外推20FPS。这是20FPS阶段历史；30FPS与手动scale/quality已在随后切片实现且独立记账于ADR071/072及HANDOFF§19.28，M6/M7不提前实施。

### ADR-071 — M5 30FPS目标只按显式Host档位开启

**2026-10-08**：在原M5默认5FPS/50%与主机级实验10/15/20FPS/75%上，只扩精确 `(30,0.75,60,false)`，四勾选互斥，点击时二次拒任何多选，旧默认不升级到Balanced/LowLatency；30FPS为`Task.Delay(TimeProvider)`名义每33.333ms、毫秒定时器实际约33ms目标，慢网/内存不跨版继承用户旧轮Pass。FramePipeline/Factory/Host仍精确白名单，非法值在身份/网卡前拒，在线切档要求完整Stop/Join且本机再显式开启；TLS/证明/审批/同网/紧急停止/Control灰显与现网不动。先在旧白名单看到2目标红/19绿（`20261008-30fps-red-sessions-test.log`），随后定向21 Sessions和50 Host全绿，当时两配置各3760 PASS、零构建警告错误（`20261008-30fps-{debug,release}-{build,test}.log`）。**Last code commit：`b3e1273`；Working tree at validation：当时未提交App/Sessions实现与测试**。这轮无四Rebuild/16TRX/源SHA冻结、无30FPS实机性能证明；当前最终手动画质版以ADR072为准。

### ADR-072 — M5 手动scale与JPEG质量必须与原像素预算同时收窄

**2026-10-08**。原始规格M5“然后”包括scale/quality，产品规格手动scale仅0.50/0.67/0.75/1.00、JPEG整值40..85；M6另负责每Session预设与Auto，不能将M6隐式开启。主机GUI增加另需明确勾选的临时手动scale/JPEG选择；仅勾选后从默认5FPS或显式10/15/20/30FPS快照参数，未勾旧默认和固定实验档完全不变，运行中只报告实际Host档不热切，必须先紧急停止完整排空再本机显式开。`FramePipeline.ValidateSettings`为单源纯校验，在ViewModel/Host/Factory构造链逐处复核合法值，NaN/Infinity/非法fps/scale/JPEG/Auto在身份/网卡/采集委托之前拒，不Clamping、不持久化config、不开放键鼠。

**P1内存边界**：旧JPEG输出32MiB是**压缩payload上限**，不是解码/WPF或原始GDI多份内存预算；Client既有解码上限16,777,216像素，若允许100%而GDI仅限制宽高各8192，极端8192²主屏会先分配巨大BGRA/暂存、后由Client拒。决定在共享`FrameLimits.MaxDecodedPixels=16,777,216`，GDI读主屏模式建任何DC前拒超预算，Session factory主屏枚举后且建encoder前再次拒并回收已借capture，Client拒解码沿用同值；不强行缩小真实显示器，不误宣称独立覆盖混合DPI/全机内存多副本。可能因此拒旧边界可通过但超预算的高分辨率主屏，这是有意fail-closed兼容取舍，后续需方案而非擅自提高预算。JPEG编码32MiB输出仍单飞/有界队列，最高质量不保证CPU/LAN稳定。

**证据与限制**：旧手动白名单目标5红/0绿，之后手动值透传/factory超预算回收Sessions定向16/16，Capture GDI边界与真实小帧JPEG各缩放/质量18/18，Host拒非法手动并保证DPAPI身份原文件不变57/57绿；中间测试误把首装`LoadAsync()`已生成身份断言为零文件而导致5目标假红，修夹具原字节对比后绿（旧红不擦除）。**Last code commit：`b3e1273`（仅旧Transport提交）。Working tree at validation：当前未提交产品/Core/Capture/Sessions代码+测试，证据`outputs/m5-host-video/20261008-quality-{debug,release}-{build,test}.log`**，Debug/Release各solution Build0 warning/error、八项目各**3772 PASS/0 FAIL/0 SKIP**（Core197/Security66/Protocol303/Integration3/Transport2297/Acceptance503/Capture159/Sessions244）。未跑四Rebuild/16TRX/源码SHA终验，不宣告新版双机30FPS或100%/JPEG85十分钟与自然慢网DoD通过；用户之前Pass只属其原测试轮。M5产品新版验收仍依赖两台机器/真人审批；这段为v3独立冻结之前的阶段事实，后续详HANDOFF§19.29。M6/M7当时未开始。

### ADR-073 — Stop/Accept首调用与pending关闭竞态的窄域收尾

**2026-10-08。触发与两次真实失败**：M5手动画质代码两次常规solution Build/Test各3772绿不足以证明时序路径。首次独立四Rebuild/16TRX冻结`final-m5-quality-20261008-v1`中，Release 仅`TransportHostTests.Stop_Cancels_Connections_Stuck_In_Handshake_And_Releases_Admission`抛`InvalidOperationException: Not listening`、3771绿1红；单独重跑旧测试1绿不翻这轮真红。窄加短锁及`IsBound`判别的第二冻结`v2`又在Debug `ControlClientConnectorTests.Duplicate_Challenge_Unknown_And_Malformed_Replies_Fail_Closed(unknown,pending:true)`的Host清理中因`SocketException: OperationAborted`真实1红，证明`listener.Server.IsBound`在Stop后的状态不可作为唯一正常关闭判断。v1/v2都保持350项源/文档/Git原字节未漂移且四Rebuild零告警，不归咎于源码漂移。

**决定**：StopCore与AcceptLoop首次发起pending accept仅共用短锁，Stop不能在“已启动Host但accept Task.Run未开始”窗口抢先停止listener造成Not listening；await原异步任务在锁外，不阻塞物理关闭或取消回调。对已发起的accept，唯有停机已请求且SocketError=OperationAborted/Interrupted/NotSocket这些已知关闭码才视为预期；其它SocketException与所有InvalidOperationException仍按原错误树上报，不削弱原Registry/accept join与Dispose证明。无任何系统网卡/防火墙/桌面权限更改。不声称所有内核时序都经确定性单点变异证明。

**阶段证据**：目标Host/Connector合计23/23 PASS，最终`outputs/m5-host-video/final-m5-quality-20261008-v3/summary.json` status=passed/exit0、摘要SHA `fc57ba7fb50d8ac65b8658c043e7e86d297da3ab98a3081b98c901554841455c`，350项源/测试/HANDOFF前后与12边界逐SHA一致`dbff72f66cbbdabe6ea44c5d260df3ac3f8d079fa8b301b471d99c8ffd0329d5`；四次完整Rebuild零警告错误、16 TRX Debug/Release各3772 PASS/0失败/0跳过，六直接dotnet子任务rc0/wait，原TRX/Counters和summary SHA另命令核对。冻结后HANDOFF/docs/memory写入为明示文档例外，不能再声称全文件保持冻SHA。**Last code commit：`b3e1273`（既有Transport历史），Working tree at validation：当前未提交M5视频产品/画质/窄域停机改动**。新自包含WPF WinExe双机候选`LanRemote-M5-完整手动画质-20261008-win-x64.zip` SHA`0137f49542e0f79d43ed3022737a4eeb00653875d8677508ca1411601a8c9945` 已逐文件核验但未两机实机运行；不以旧包用户Pass替代新版M5 DoD。详HANDOFF§19.29。

### ADR-074 — M6 本机显示设备目录与M5采集/远端控制严格分离

**2026-10-09。范围与原因**：开工按磁盘HANDOFF§19.29、原M5/M6规格和实际git diff（含未跟踪App/测试）确认，M5候选已出包但新版双机DoD待人工；当时没有M6多屏在途实现。为推进不依赖真人的最小切片，新增独立`GdiDisplayCatalog`，而不是修改仍只支持主屏的`IScreenCaptureBackend.GetDisplays/CaptureAsync`合同、提前接远端切屏或启动M7。

**决定**：复用`GdiNative`的`EnumDisplayDevicesW`和`EnumDisplaySettingsExW`当前模式读取，借用仅两个只读方法的`IGdiDisplayNative`。仅列出附着且非镜像驱动的设备，保留系统枚举顺序与原始索引、物理负坐标和当前宽高（竖屏不再次交换）；友好名空白时用设备名。ID不是HMONITOR、持久身份、跨拓扑引用或次屏采集授权，修正原模型注释但不改变模型形状。列表需设备名称非空且OrdinalIgnoreCase唯一、恰一主屏、DM_POSITION/DM_PELSWIDTH/DM_PELSHEIGHT字段存在、正宽高及right/bottom可用int表示。两遍附着设备完整身份和核心模式（含方向/频率/固定输出/颜色位深/panning等）相等才发布只读快照；变化/原native异常直接失败，不自动重试、不返回部分成功。每遍最多256个原始枚举条目（含过滤项），index256只判断是否结束，若仍有项立即拒绝。

**非目标与限制**：无DC/像素采集，不改DPI、分辨率、权限或系统网络。元数据允许大于M5帧预算，不等于该屏可采；16,777,216像素与32MiB仅压缩payload上限不变。两遍一致只检测观察到的变化，不承诺原子拓扑、ABA或返回后不变；EnumDisplayDevices FALSE的原生合同不能冒充所有错误可区分。附着设备视角并非完整物理面板拓扑。新目录未接App/Session/协议；现有Control原读监视器仍拒绝额外字节，未来metadata/切屏必须设计单读者与会话/帧关联，不能直接并读或去掉fail-closed守卫。所有认证、审批、ViewOnly、紧急停止、有界队列与现网只读边界保持，原始规格不回写。

**实际证据**：新增80项首跑77绿/3红，其中生产遗漏panning宽高比较两项、fake名称查找误命中镜像项一项；分别修正，原`initial/`日志/TRX保留，不称人为变异kill。修正后Capture239/239绿且零构建警告错误。新`outputs/m6-displays-20261009/final-v1/summary.json` passed/exit0；四完整Rebuild零警告错误、六命令rc0、16TRX逐项/Counters双配置各3852 PASS（Capture239，其余197/66/303/3/2297/503/244），352项前后及12边界逐SHA一致，摘要SHA`21c88724c953e7701a3f090897dc42042ec17f9a08b82e04e1b9b5a0b2118f0f`；另命令`independent-audit.json`核实原证据SHA/计数/源码及旧ZIP保持。真实只读仅一个OrayIddDriver设备/index4/1920×1080，不能证明物理多屏/混合DPI；第257条恰为过滤项尚无独立定向/守卫顺序变异，未声称覆盖所有组合。评审收件勘误：上一轮已收到完整只读静态评审（未报告P0/P1，指出过滤第257项覆盖缺口），后续网关流错误不推翻正文已收到的事实；不将其计为运行测试、GUI或双机实测通过。

**Last code commit：`b3e1273`（Transport历史）；Working tree at validation：未提交M6目录叠加既有M5产品工作树。** 冻结后HANDOFF/docs/memory更新是文档例外，未改源码、不再假称全352项同SHA。没有新GUI包、没有M6远端切屏/鼠标映射/每Session质量DoD；旧§19.29 M5候选哈希不代表包含M6，旧用户Pass不外推新版。详HANDOFF§19.30。

### ADR-075 — M6 host_status显示目录纯载荷与现有Control生命周期隔离

**2026-10-09。范围**：按原规格M6 remote display metadata及04§6/11的Control JSON封装，先实现internal `DisplayCatalogFrame`纯构造/Serialize/TryParse，不接任何真实流、App、Session、Discovery capability或切屏。复用已列出的`host_status`类型作为严格的显示目录专用shape，不发明display_changed/quality/输入功能，也不改AuthJson或认证白名单。

**专用shape**：根字段恰`type="host_status" / protocol=1 / seq / timestampMs / displays`；条目恰`id / name / x / y / width / height / isPrimary`。根及条目拒重复（含转义后同名）、未知、大小写变体、缺失/null/错类型；不接受注释、尾逗号、BOM、非法UTF-8或孤立代理转义，最大JSON深度3。所有数值只接受无小数/指数的Int64或Int32词法，seq/timestampMs>=0（负零数值等于零可接受），不把远端时间当deadline或新鲜度证明。元数据只属于当前已认证连接上下文；seq不是拓扑世代，未做顺序/重放/跨帧关联。未来不能仅凭ID或seq执行选屏。

**预算与所有权**：单纯载荷1..256KiB（包括JSON空白，不含4字节前缀），在reader/条目分配前检查；未修改现有全局Control1MiB/pre-auth4KiB。条目1..256，ID0..255唯一、保留传入顺序、恰一主屏；名称非空白、最多128个UTF-16 code units、拒C0/C1和未配对代理项，允许合法代理对/同名友好名。解析name先限制raw<=768字节（128×6转义最坏值）再解码；读到第257项起始token后、解析字段和构造该DisplayInfo前拒绝。构造器先验证Count再有限索引拷贝和终态Count检查；只读集合中仅sealed不可变record/string，无输入list/byte[]别名。宽高为正int，负坐标保留，用long检查right/bottom<=int.MaxValue；允许重叠、大于M5像素预算的元数据，不分配屏幕像素。序列化只投影七个条目字段，不泄漏native DeviceId/DeviceKey、计算属性或会话密钥；固定项数/名称限制确保输出有界，另做最终载荷限长防御。

**为何暂不live接线**：客户端`AuthenticatedControlSession.ControlMonitor`的原始单字节读和服务端`ControlAuthSession`审批期`_pendingRead`→`HoldUntilDisconnectAsync`都是当前所有者，额外数据仍拒绝。本片不并读、不丢弃已消费字节、不取消后假称原读已退出。未来必须先设计双端唯一Control读者的交接/有界消息分发、挂起首字节归属、原I/O join、未知类型失败、版本协商及会话/目录/视频帧坐标关联；现LRVF v1没有显示器身份，不得未经设计扩展reserved字段。名称为远端不可信纯文本，未来UI不得解释为路径/HTML或把元数据写日志。M7不启动，原TLS/pin/HMAC/serverProof/DPAPI/审批/ViewOnly/有界队列/紧急停止与现网只读均保持。

**实际验证**：516项纯合同首次及变异恢复后均绿；长度上限/重复字段/宽算术三处真实变异分别1红、24红93绿、5红，原字节恢复且原红证据保留。另补ADR074第257项为detached/mirroring×两遍四例，guard移到过滤后变异4红2绿，恢复Capture243绿。`outputs/m6-metadata-20261009/final-v1/summary.json` passed/exit0，四Rebuild零警告错误、六命令rc0、16TRX Debug/Release各4372 PASS（元数据516、目录84），354项前后及12边界SHA一致；另一命令`independent-audit.json`核对原字节、TRX/Counters、变异恢复、旧M5 ZIP通过。摘要SHA`c7376e1ebf3af24edcc397d2d236e974c5ed89fe9eaff286a9f1918b79918498`。**Last code commit：b3e1273；Working tree at validation：未提交纯codec/测试叠加既有M5/M6目录。** 冻结后HANDOFF/docs/memory为文档例外，详§19.31。静态评审未报告P0/P1，不等同独立运行测试；仅单只读设备观察，不是物理多屏、live传输或新版双机DoD。

### ADR-076 — M6 首字节所有权先于真实Control交接

**2026-10-09。源码依据**：Host `ControlAuthSession.ReadOneByteAsync`用局部byte[1]并返回async包装Task<int>，只传计数而不保留可接续前缀的首字节；Client monitor当前消费一字节后即拒绝。不能把Host既有Task<int>冒作“字节已移交”，也不能在其挂起时另起FrameReader。当前帧读取器两段各有预算，不等于未来从首字节计的完整帧绝对预算；空闲无消息不得套用短前缀计时器自动断连。

**本片决定**：新增独立internal `ControlReadAhead`，不接任何真实认证/审批/Control/Video入口。构造不IO；Start在gate内登记唯一Task.Run worker，worker只在锁外执行一次ReadAsync(byte[1],None).AsTask并保留原Task异常树。TryTake仅在worker真正结束、未停止、未消费且Byte/EOF成功终态时交付一次不可变结果；nullable值表示EOF，0x00是有效字节。Snapshot不可变且不暴露字节/流/buffer，错误保留引用/嵌套多fault不Flatten；Completion只证明worker及取得的原Task排空，不代表读取成功，调用者必须检查Snapshot.Error。

**停止与所有权界限**：RequestStop幂等逻辑失效、不关闭流、不取消/等待读取；owner仍须锁外关闭借出流并await Completion。stop-before-worker不发读，stop-during-read不提前join、不交迟到数据且仍保留错误；越过准入检查但仍在同步前缀的读可以继续，不能称硬中断。Take与Stop按同gate线性化；Take已赢的不可变值不能追回。只有已取得原Task且await退出后才清本方一字节buffer；ReadAsync/AsTask抛错而无法取得Task时不清写借出buffer，不能保证违规流隐藏I/O已退出或TLS副本清零。胶囊自身保证一次读取，但不能禁止外部另一个对象误读同一流；没有跨owner Move或接管已有Task接口。

**明确非目标**：无队列/pump/帧解析/seq/时间戳/timer/deadline/网络写入/协议协商，不把取结果时刻冒作首字节到达或既有预算起点。未来live需一起设计Host审批期禁活动、auth_success与方向白名单/版本兼容、唯一读写者、无空闲误断连的整帧预算、完整join及帧目录坐标关联；本片不删除control-unexpected-data、不新增心跳或远端切屏，不启动M7，不动网络/防火墙/权限。验证结果须见最新HANDOFF，不以本决定替代实际测试。

**实际验证**：首次33 PASS伴两处测试CS8619；新增重复异常引用/迟到ODE×停止顺序/竞速后39 PASS。三处生产定向变异8红31绿、5红34绿、6红33绿，完整原字节恢复；仅修测试Task.Run可空泛型后clean-target零警告错误39绿。新`outputs/m6-read-ahead-20261009/final-v1/summary.json`及独立审计passed，四Rebuild零警告错误、六命令rc0、16TRX Debug/Release各4411 PASS，356项/12边界一致；摘要SHA `6e64943e5452707fe4785007362a80d6cebcb62e5aa2ad9d37748cc14b583d10`。Last code commit b3e1273，Working tree at validation未提交首字节片叠加既有产品；文档更新例外及反射/夹具/原IO边界详HANDOFF§19.32，不等同live/两机GUI。

### ADR-077 — M6 单帧接续先验证空间/原任务所有权，不提前接入真实Control

**2026-10-09，独立验证完成。** 新增internal `ControlFrameContinuation(Stream, byte firstByte, int maxBytes)`，不改既有FrameReader/认证/monitor。调用者必须独占流且已持有首字节并确认前一个原读退出；标量参数不是流身份/认证或所有权凭证。Start只一次登记worker，锁外顺序读取余三字节前缀，复用FrameReader.TryValidateLength于uint域拒0/超限后分配单payload；maxBytes仅1..全局Control1MiB，可由未来元数据消费者选256KiB。不消费下一帧，不自动恢复/重同步，无JSON、权限、seq或方向决定。

**明确没有deadline。** 本片不创建TimeProvider/timer/CTS，没有空闲或整帧时间预算，不能live。不能称Start时刻就是首字节物理到达时刻。后续统一预算须独立处理观察锚点、timer通知/关闭与原Task join，不以现有FrameReader两段各自预算冒作完整帧绝对预算；现FrameReader161–162行注释因果不清，但本片不改它的既有行为。

**读与清理合同。** 每个ReadAsync/AsTask之前重置original=null并记整块current为未知借用；取得Task后必须await，即使Stop已先赢也不能跳过。原Task fault保留兄弟顺序、重复引用、嵌套及迟到ODE；停止标记与Error并列，不吞错。没有取得Task的同步异常路径不清整块current（包括之前已经读完的slice），并标BorrowExitUnproven；其它已安全结束的数组仍清。故障不继续读，不证明违规流隐藏IO或TLS副本退出/清零。

**单次数组移交。** TryTake在worker完成、未Stop/未Take且完整成功时一次移交原byte[]并清内部引用；这是可变数组所有权，不是不可变DTO。Stop与Take同gate裁决；Take先赢之后Stop不擦结果。Stop只在gate内清最多1MiB已静止未交付payload，不触在途借用、不关流/不调回调，不宣称wait-free。正常worker清理本方prefix/未交付payload后才Completion结束；owner仍须锁外关流并await Completion。只有空间有界，不保证不合作流在有限时间退出、进程总内存上界或全部系统进程排空。实际验证见HANDOFF§19.33；M7不启动。补充精确边界：每次Stop检查与锁外Read非原子，越过检查的读可在Stop返回后进入同步前缀；Snapshot终态可先于worker结束，不作join；成功未Take数组可跨Completion保留，owner须Take或Stop明确处置。

**实际验证**：initial109绿，补独立小额度限长Fact后baseline/restored均110绿。四定向变异分别9红、1红、15红3绿、8红12绿，均精确恢复生产SHA `2c31ee8b9e43b9f618a383b4aeef866c4017eaea0e7796897b017e7d950f21a0`；stale-original先击中未知借用标志断言，不称已执行后续数组断言。新完整冻结四Rebuild零警告错误、六命令rc0、16TRX双配置各4521 PASS，358项/12边界一致，独立原证据核对passed，摘要SHA `30defbb5fd6597005fc1c884e34a79ac6a90677432418b6fb360ae5112f51355`。文档更新为冻结后例外、反射/原IO/只证明直接进程等限制见HANDOFF§19.33。

### ADR-078 — M6 显式观察锚点预算与帧/网络所有权分离

**2026-10-09，独立验证完成。** 新增独立internal `ControlFrameBudget(TimeProvider clock, long observedAt, long observedFrequency, TimeSpan budget)`，构造只验标量、不访问clock；owner保证锚点/频率来自同provider的可信本地观察，不声称物理首字节到达。当前首读/接续仍无生产接线。Start唯一登记worker后锁外取时，Ready/Decision只是通知，Completion覆盖clock/timer同步前缀与取得的原Dispose Task，不证明帧读/网络关闭或隐藏操作退出。

**固定时间合同。** 频率显式固定为正，每次采样前后必须一致；timestamp必须不小于上次样本（不止anchor）。Int128先扩宽相减，delta仅0..long.MaxValue，拒绝可观察回绕/倒退、频率变化与getter异常；delta×TicksPerSecond与budget.Ticks×frequency作精确整数比较，恰好到点即拒绝，不用double/截断TimeSpan延长接受。样本间异常后恢复无法识别；不支持任意恶意时钟。0预算直接到期且不取时/建timer；负值/无限/超过有限timer due-time上限4,294,967,294ms拒绝。上限是原语支持范围，非产品默认时限。剩余排期向上取整到整数ms只控制通知，不放宽接受边界。

**独占操作与停止。** worker禁用态Create，迟到timer仍接管；回调仅合并内部异步唤醒，无取时/重排/关流/CTS回调。Change后复查同锚点，连续同步立即唤醒最多32轮、33轮拒绝忙循环。RequestStop只标记/通知/唤醒，已越过检查的同步操作仍可执行，不能硬中断。RequestFinish只是owner声明原帧已结束，不是证明；成功需停止排期、原DisposeAsync/AsTask完整退出、再次同锚点末检并复查Stop。释放期间跨截止否决；原释放阻塞时不宣称及时到期通知或有限退出。Error/CleanupError两槽保留原引用/兄弟/顺序/重复/嵌套；无原Dispose Task的异常标CleanupExitUnproven，未取得timer也不证明损坏provider无隐藏资源。

**非原子交付边界。** 最后外部取时与内部gate发布并非同一原子事件，成功是该次采样下的历史裁决，不是未来任意时刻有效的payload交付许可；Stop与发布只按同gate裁决。此片不接stream、首字节观察采样、帧Take或父会话撤销，不替代live提交终检。真实Control还需之后合成组合、版本/方向与唯一读写者设计；本片不改FrameReader/审批/monitor，不启动M7、不改网/权限，不以§19.33的4521作为新源码实测。

**实际专项证据**：initial101绿但两CS8619、runner exit2，修两处Task.Run可空泛型后baseline/restored101绿且构建零警告错误。三变异各1红并精确恢复；anchor-renewal/final-sample首红是Expiry中缺TimeoutException，original-dispose首红是Completion提前退出的Assert.False自定义消息，非Guard。System timer MaxBudget Create/Change实际通过但不等待到期。完整只读审查无确认阻断生产缺陷；反射await布局与合作型异步IValueTaskSource未直接专项覆盖为局限。新完整冻结`outputs/m6-frame-budget-20261009/final-v1/`四Rebuild零警告错误、六命令rc0、16TRX Debug/Release各4622 PASS（预算101），360项/12边界一致，独立audit passed；清单SHA105496df558731d53beda71fd7cfd00ffc5d051b15cb760423cbc0422923d449，摘要SHAc7c755481528cb19e32ac49835e8b0e63dd6461a9a3aef75f6c947e73e27f1ff。文档更新是冻结后例外，未接live，不覆盖下一片。

### ADR-079 — M6 独立单帧组合以原任务结果一次交付，不造未来兑换许可

**2026-10-09，独立验证完成。** 新增独立internal `ControlFrameReadAttempt(Stream, TimeProvider, TimeSpan, int)`，不修改ADR076–078三原语，不接Host审批/Client monitor。Start登记唯一协调worker，返回同一`Task<FrameOutcome>`；指定唯一消费者负责Frame数组，重复await只得到同一引用，不是多次所有权。结果是当次历史接受，不是未来业务权限/时间许可，也不自动与真实父gate提交。

**组合顺序。** 首读原worker完整join/单次Take后，由协调worker锁外读取frequency/timestamp/frequency，形成同源本地观察anchor；非物理到达，首读完成至观察调度间隙不计预算。EOF不采clock、不创建timer，零字节仍是有效前缀。预算Ready后复查才接续，整帧完成才Finish；预算Decision拒绝先发AbortRequired，再等待各原worker/原Dispose退出。WhenAny仅唤醒、检查所有可见状态而非参数顺序判胜。所有已启动原任务均join，通知或Stop不代替退出。

**最终接受。** 接续Take只把静止payload移到组合worker，原预算释放完成后另做同锚点精确Int128末检，随后owner gate复查Stop再发布结果；结果容器分配在末检之前。私有ObservedClock只转发取时/原timer并保存三阶段已见timestamp高水位，拒绝跨预算末样本到最终样本的倒退，不包装timer释放或可信化恶意provider。样本与gate非物理原子事件、未来消费仍需业务自己的检查。未交付数组由本worker清理；交付先赢后Stop绝不回收清写。

**停止/边界。** AbortRequired只通知外层锁外关闭流并保有原关闭Task；组件借用流、不调用关闭回调、不取消任意用户回调、不join父自身。Snapshot运行中非事务，完整退出后才稳定；原组件错误/未知借用状态分来源保留，未取得原Task不证明隐藏IO退出。永久阻塞的stream/clock/timer不保证有限退出或及时到期通知。拒绝时已Take的静止自有payload必须在原join之前清零，不能被不合作的原Dispose拖延；在途/未知借用区不在提前清理范围。真实initial65绿1红（新增清理Fact的Assert.All），最小修复后同测试66绿；baseline/restored66绿、四合同变异各1红，均零警告错误并精确恢复。final-sample/stop-commit命中错误Frame交付，original-read-join命中Completion提前退出，abort-notification命中拒绝通知未完成，均非Guard。原日志及原/修复字节保留。4622仅预算历史；本片新完整冻结四Rebuild零警告错误、六命令rc0/直接wait、16TRX双配置各4688 PASS、362项/12边界一致，独立审计passed。清单SHAd43e390e75616bd7608a2a73ceba6f26613b3824339b6bc497941cf8472f7660，摘要SHA12dd3fc21cd0ff411fb27960858f7a5fc7a73b64458f457dc58f8b2d480246d2。审计以testId/executionId区分被截断的Theory显示名，保留初错脚本；不改TRX、不重跑已通过冻结。文档记账是冻结后例外，outputs脚本非源scope/仅末态库存，不声称连续锁。下一Host读槽需保留原包装活动边界，不能直接替换为新Task.Run或AsTask桥接；M7不启动、不改网络/防护/桌面权限。

### ADR-080 — Host 审批到保持阶段沿用同一活动读所有者

**2026-10-09，专项、四变异、完整冻结及独立核对均通过（未提交工作树）。** 本片仅改Host既有审批→holding读槽的内部表示；不接ControlFrameReadAttempt，不引入帧解析、读循环、worker、CTS、timer或可兑换的Claim/Lease，不改Client monitor。审批成功交给保持阶段的是同一具体私有owner，包含同一async计数包装Task及同一一字节buffer。

**观察边界不动。** 在原调用位置直接await流的ValueTask，不新增AsTask/Task.Run，也不以底层完成状态替代包装Task.IsCompleted。Read同步前缀仍在审批timer之前，审批判定保持caller取消 > 单调到期 > 已观察活动 > 决定。auth_success写期间的活动不追溯改变此前审批结论；写成功后登记与Video窗口锚点、秘密清理顺序不动。当前任意额外字节仍拒绝或结束，不能将保留buffer误称为允许Control消息。

**清理和异常边界。** owner只在Activity.IsCompletedSuccessfully后夺取一次清理权并清原数组；不以关闭通知、fault/cancel或未知异步源状态推断借用结束。holding和外层原读join均有清理入口，原任务依然完整等待。Interlocked.Exchange只分配清理权，不提供并发调用者共享的“擦除已完成”屏障；原Router仍Run→Close→Join顺序。无审批时在原holding调用位置创建局部owner，不写_pendingRead，保持原意外故障被holding吞掉而不再由Join重新传播的行为。有审批路径原field保留，后续join的意外异常仍传播；不新增全原始多fault树保证。

**实际验证**：旧生产49项31PASS/18FAIL（成功退出后原Memory应0实199），只修生产后同测试字节49PASS。另增5项helper自测取得2PASS/3FAIL，再仅修兄弟异常及取消token豁免逻辑后54PASS；244项原合同回归通过。四变异分别2FAIL/1PASS、4FAIL/2PASS、12FAIL、12FAIL，均真实断言而非Guard/构建失败，精确恢复后54PASS。新四Rebuild零警告错误、16TRX Debug/Release各4742PASS，363项源清单前后/12边界一致；独立原证据审计实际exit0，详HANDOFF§19.36与`outputs/m6-host-read-owner-20261009/independent-audit.json`。原警告、测试初红和首轮审计解析错误均保留，不覆盖；此验证补记属于冻结后文档例外。

**后续不外推**：本片仅解决既有活动读表示与成功后清理；未提供首字节一次取出、未接live帧或Client、未完成新版双机DoD。后续移交须与Clear同一原子夺权点并保留EOF/0x00/未取得三态，不能只因有buffer就跨过审批/协商或建立第二读者。

### ADR-081 — 私有Host读槽只移交成功原读的EOF或首字节

**2026-10-09实现及冻结，2026-10-10正式独立审计通过，未提交工作树。** 仅在既有private `OwnedControlActivityRead`内新增`internal bool TryTakeCompleted(out byte? firstByte)`及注释，34行/1140字节；切去此连续新增段必须逐字节还原原生产，CRLF保持。原字段/构造/直接async await包装、Activity、Clear及全部审批/holding/join/live调用不动，src中尚无该方法调用点。没有把历史读结果当作协商、父会话有效性、认证/输入许可或帧预算。

**三态与一次归属。** false/null表示未取得；true/null表示成功EOF；true/0x00仍是有效数据。仅Activity.IsCompletedSuccessfully后读取同一成功包装Task的结果，不再次消费底层ValueTask。0/1之外的成功计数在夺权前拒绝；pending/fault/cancel/未知借用不能取走数组，原Clear仍可处理成功非法计数。Take与Clear共用Interlocked.Exchange(ref _buffer, null)；赢家先复制标量，finally清零原数组，自身成功返回前完成清理。输家返回不是另一个赢家完成清理的屏障，测试须join全部竞争调用后核Memory。

**实际验证。** 原无入口仅保存entry-gap/原三bin，不算语义red。首次lazy-shell27绿19红，其中18真实Assert、1同步Fact误带Timeout的框架配置错误；原证据保留，仅去该入口Timeout后clean28绿18真实红。同测试原字节fixed46绿；八定向变异全部build0/test1、真实Assert击杀并逐次精确恢复，400回归及restored46绿。测试覆盖EOF/00/C7/FF、wrapper尚pending的IValueTaskSource、单次GetResult、原异常/token、未知迟到writer、非法计数、Take/Clear归属及合成同流首帧接续，后者不是live接线。

**完整证据和局限。** `outputs/m6-host-read-handoff-20261009/final-v1/`四Rebuild零警告错误、六直接dotnet命令wait/rc0记录、16原TRX Debug/Release各4788PASS/0FAIL/0SKIP（Transport3229、新移交46、旧owner54）；364源项前后/12边界相同，清单SHA fc3f584e4851237aef2e836bc593e9199a6c324ce979fd40b74453962651cadc。正式审计实际exit0，独立从原证据复核45TRX/19694唯一execution，报告SHA 9d0f754170577361fce15f76a1d3f706bbf775c8263f3e28964486dcc0afa82d。final历史冻结只含六主要阶段；八变异和其执行器/计划在本次审计绑定，不冒称被final前后覆盖。final旧后台句柄已无法回收，不能将落盘runner自报exit0当作本轮观察到外层进程rc；本次审计外层rc0另存audit-execution-v1.json。没有重跑冻结、修改原TRX或覆盖旧ZIP。此记录及HANDOFF/memory是正式审计后的文档例外，不证明全机/隐藏I/O退出、连续文件锁、TLS/WPF副本擦除或新版双机DoD。下一接缝不得自启第二首读、重置旧锚点或替换Client monitor；live仍须另行协商/方向白名单及父会话提交设计，M7不启动。

### ADR-082 — 外部已观察首字节沿用原锚点与既有单帧协调

**2026-10-10，未提交工作树；专项/十变异/新完整冻结/正式独立审计通过。** 仅扩展`ControlFrameReadAttempt`：internal静态`FromObservedFirstByte(Stream stream, byte firstByte, TimeProvider clock, long observedAt, long observedFrequency, TimeSpan budget, int maxBytes)`加私有数据record/构造链及RunAsync分支。构造只验证参数，不操作流/clock；caller负责同流、唯一读者、原读已退出及同源可信原锚点/频率。入口不接受Host owner、Task/ValueTask或父会话token，不替caller证明来源。外部分支无ControlReadAhead、不重读/不重采锚点，FirstRead快照恒null；后半原Budget/Continuation/join/最后采样/Stop gate复用。三个原语与Host/Client/认证/审批不改，src未有live调用。

**数据与所有权。** 外部byte不可表达EOF，00仍是合法前缀；原自首读入口继续以nullable局部保留EOF路径。首采样/Create/Change阶段已观察到期时不启动接续；Ready之后任意调度间隙不承诺物理零I/O，最终以原锚点和频率Int128精确判断，等于截止也拒绝。拒绝时已经Take且静止的自有payload先清零，再完整join原worker/Dispose；未知借用不清写。AbortRequired只通知外层锁外关闭并保有原关闭Task，Completion不证明外层或隐藏I/O退出。最后采样与gate非物理原子事件，不提供未来业务权限/时限许可，未来live另须真实父gate。Start仅允许一次，再次调用抛InvalidOperationException；重复await同一Task得到数组别名，不是新的所有权或幂等Start。

**验证与失败保真。** 原缺入口仅entry-gap，不算red；同测试lazy3PASS/45真实FAIL→fixed48PASS（17Fact+14Theory/31InlineData），十变异15真实FAIL、464回归/48恢复通过。新专项不宣称并发Start覆盖。原变异分类器因自定义Assert.False不以Assert.开头而exit2，保留原九阶段事实；补完只跑第十项exit0。审计绑定原消息、堆栈测试行、原测试字节及日志，不将Guard/框架错当击杀。首轮final-v1因验证器误要求lanremote.integration.tests而exit2；真实为lanremote.integrationtests，该轮Debug两Rebuild/test均0、4836PASS但Release未执行，不冒称完整成功。原失败目录/脚本/外层原样保留；新验证器用显式八程序集白名单，独立final-v2完整重做且绑定原失败库存，不弱化校验。

**正式冻结。** 新final-v2六命令实际rc0/wait，四Rebuild零警告错误、16TRX双配置各4836PASS/0FAIL/0SKIP，365源项/12边界和28文档一致；新48按程序集/稳定身份精确加入旧4788。外层真实rc0已回收。正式audit_final_v2.py main及外层实际rc0，独立54TRX/24707execution，完整源/文档重枚举、跨阶段全部非生产源码/文档一致、原失败/十变异恢复/旧ZIP均核。清单SHA aebc6475c4e608a265a8e76124843ec2ad32f48f2d16dfef980820bb158ce3f6，摘要SHA f5971eb33a13c6193ed5034cf1bbe7fb9794f86c384237dd1c881e213b2faabc，审计报告SHA f544a2bd6e5db12dba1d3c9e53292315f7e17c9f8236f8b2a757c1a5c6e59302。详细原证据在`outputs/m6-observed-entry-20261010/`及HANDOFF§19.38。边界快照非连续锁、直接wait非全机排空；HANDOFF/本ADR/本日日志为审计后的文档例外。

**下一接缝不是本ADR的wire定案。** 当前严格应用v1没有能力协商，TLS版本及Discovery不能代替双方确认，原认证transcript未含未来能力字段；保持Host holding/Client已启动monitor额外字节拒绝。目录只Host→Client；下一候选是先版本/legacy兼容政策，再专用Host Offer→Client Accept纯合同，随后才设计live唯一读写者和父gate。protocol2/4KiB/字段/降级策略在本ADR冻结时尚未批准或实施，不默默塞入现行v1或反转目录方向。随后用户B政策和纯codec见ADR083，本段保留历史。未交付新版双机DoD，M6仍未完成、M7不启动，不改现网/防护/桌面权限。

### ADR-083 — 新版-only政策与专用目录 Offer/Accept 纯合同

**2026-10-10，用户明确选择B；纯codec已实现并验证，live拒旧尚未实现。** 只支持新版双方连接，旧端明确拒绝并要求升级，不保留legacy单屏分支、不静默降级。该政策不等于现有Hello/Auth/Video已升级；它们仍为v1，原serverProof未绑定新协商。新增唯一生产类DisplayCatalogAgreementFrame和108项专项，不引入通用capabilities框架、dispatcher、版本范围或可协商预算。

**固定载荷。** 四必填字段type/applicationProfile=2/catalogSchema=1/sessionId；Host→Client的display_catalog_offer和反向display_catalog_accept。profile2为应用合同，目录schema1仅payload版式，非TLS/LRVF。1..4096字节（含JSON空白、不含前缀），MaxDepth1；拒未知/重复（含转义同名）/错大小写字段、非精确整数、非法JSON/UTF-8/代理、尾随值；会话必须canonical小写非空D Guid且与caller期望相同，token先限216字节再分配字符串。字段重排/合法空白/等值转义允许；序列化新数组，解析不持有借用输入。

**能力边界。** SessionId仅关联；调用方选择方向和可信上下文，本类不验证身份/双方确认/反重放/权限，不碰流、时钟、随机数或父gate。重复合法解析明确允许，不是已协商授权对象。4KiB只在span入口限制，live须自己在读取/分配前限长。一会话一次Offer/Accept、无重试重协商是后续状态机约束。proof前后顺序尚待下一片定案；若采用AUTH-V2前置协商，需另改XML已认证上下文前提及测试，不能混称此时已经认证。不得把当前V1 proof当成合同绑定，不放宽Host holding或Client monitor。

**实际验证。** 同测试拒绝壳94PASS/14真实Assert FAIL→生产实现108PASS；八生产变异实际FAIL分别1/2/2/4/2/8/4/1，全部build0/test1、逐次wait并保存恢复原bin，恢复后108PASS/外层0。部分错误矩阵主要Offer，不能称两方向所有排列直接覆盖。新final-v1四Rebuild零警告错误、16TRX Debug/Release各4944PASS/0FAIL/0SKIP（Transport3385）；367源/28文档/12命令边界一致，所有旧4836稳定身份+新108精确对应，无单独旧parser专项回归轮。完整冻结外层真实0，正式独立审计外层真实0；独立43TRX/19958唯一execution，green/restored/final完整源码相同、真实失败/原日志/时序/PID及完整历史/根脚本/旧ZIP绑定。原red保留，框架错误/编译错误不计击杀。

**证据。** `outputs/m6-catalog-agreement-20261010/`，详HANDOFF§19.39。生产SHA687c57766ed5959b1e3e44a4b38668f277919682018f9b611e5c9b007b715224；367项冻结清单SHA567806036322f92f83ae906958a6e29a3ae81279fbfe967d8b478af6bae690aa；摘要SHA9b95ef8c41d9910dd406bf7c18e94a444ea3de67ddf7417f168e718b1ca33b39；正式审计SHA015875406168e134466c2de334ec97a0eca41badd78f8a18d58d1e947ce0b334。HANDOFF/本ADR/当日日志为正式审计后文档例外，非当前全树仍等于冻结的声明。边界快照非连续锁，直接wait非全机/隐藏I/O排空，不证明双机GUI或完整M6。

**后续约束。** 按B继续V2强类型规范合同和proof绑定、严格版本拒绝/正确升级提示、Video父认证资格；不得全局替换TLS或LRVF版本。EOF/超时只能是不确定连接失败，不能冒称已识别旧端；pin/proof/密码/审批失败不能统一包装成升级错误。真实Control协商、目录传输、切屏/每会话质量仍未接，不启动M7、不重做M4人工项、不改现网/防护/桌面权限。

### ADR-084 — proof前Offer/Accept强类型绑定与AUTH/GRANT-V2纯核

**2026-10-12，纯核已实现并验证，live未接。** 承接用户B新版双方-only政策，Offer/Accept位于clientProof前，顺序固定为TLS/pinning→Hello2→Challenge2→Host Offer→Client Accept→clientProof V2→真实审批→serverProof V2→新版父资格。ADR083中的proof前后待定与已认证上下文前提为历史，本ADR定案并修订Frame XML；原codec行为不改。不能把顺序定案写成实际收发完成。

**强类型输入。** `DisplayCatalogAgreementBinding.Create(expectedSessionId, offer, accept)`强制非空会话、正确方向、Offer和Accept各与expected匹配；`AuthTranscriptV2.CreateClient`再次要求agreement.SessionId等于认证session。Guid非空，nonce/pin各32字节、key16字节。Binding及transcript均internal，不能从外部raw/V1 transcript构造grant/proof；导出返回副本。对象可重复使用，既不是一次性授权也不验证真实消息来源/新鲜性；真实连接冻结、一次协商、唯一读者、原预算和本机审批仍由live状态机承担。

**规范字节。** client为16字段、15个NUL、无尾NUL：AUTH-V2域+session/serverDevice/clientDevice/serverNonce/clientNonce/certSha256/requestedPermission+Offer四字段+Accept四字段。具体顺序见HANDOFF§19.40；Guid小写D、nonce canonical base64、pin大写hex，profile2/schema1用invariant整数。绑定严格解析后的语义，JSON空白/重排/等值转义不进入proof。grant为GRANT-V2域+NUL+SHA256(完整client)大写hex+NUL+actual grantedPermission；serverProof使用7字节`server\0`前缀后接grant进行HMAC-SHA256。ViewOnly不得升Control，Control可降ViewOnly。临时server message/grant清零，不承诺托管字符串或TLS/框架内部副本擦除。

**实测与拒收。** 新75专项同测试有效red-v2为67PASS/8Assert FAIL→green75PASS；十个生产变异全实际test1且恢复，restored75PASS，独立Python三组黄金向量复算。首次red分析器警告门禁invalid/exit2/0TRX原样保留，不计测试红灯。完整final-v1四Rebuild零警告错误，16TRX Debug/Release各5019PASS/0FAIL/0SKIP，371源保持，旧4944+新75稳定身份精确相加。原冻结父子52724→39612→17724以及外层wait0已核。

**审计护栏。** 固定授权rc1阶段RunInfo只放行与完整Failed名字、xUnit adapter/Assert本方法堆栈、机器/运行时窗及原日志整行计数一致的回显；不靠子串、不改原TRX，其他阶段拒全部RunInfo。独立审计修复了报告执行后字节未绑定、审计到before库存重新采基线两个缺口：audit及新selftest报告原字节摘要进入执行期stdout，launcher绑定日志；audit末尾完整manifest进入报告，before只允许审计空日志转终态及新增report/execution/wait三项，历史另核。旧60项正式结果与两旧脚本共八项固定摘要保留、不证明新修订；新68项正式自测及正式独立审计均真实wait0。独立核45TRX/20175唯一execution、1214冻结原项/130 final项及3214历史项，正式报告SHA `f1c7158d15aac4da28b24f1a251dbd411ab5c21b102f39d2c1386be84b3bdd62`。报告内当前audit pending在外层结束后另由post校验真实闭合。不是OS不可篡改审计或连续文件锁。

**现态与下一片。** 用户明确M5稳定并要求继续M6，按用户确认收口，不重复人工验收/无必要压测。当前真实认证仍V1，未实现Hello2/Challenge2/live拒旧或升级UI，纯核完成不是M6整体完成。下一最小片为独立Hello2/Challenge2严格wire合同和版本证据；V2失败不fallback、两Host入口同策略、协商不重置认证绝对deadline，EOF/超时不等于旧端，pin/proof/密码/审批失败保留原分类。新版父认证成功才赋Video资格；不全局改TLS/LRVF、不启动M7、不改现网/防护/权限、不覆盖M5ZIP。HANDOFF/本ADR/日志/MEMORY为审计后四文档例外；最终交接以handoff-v2/post-handoff-check.json实际结果为准。

### ADR-085 — 独立Hello2/Challenge2严格合同与未认证版本证据

**2026-10-12，纯载荷已实现并验证，live仍V1。** 仅新增internal的ControlHelloV2、AuthChallengeV2、WireV2Json、WireV2ParseStatus及93项专项；不改共享AuthJson/CanonicalGuid/AuthProtocol、不改两个Host入口或Client认证流程。应用protocol2不是TLS/LRVF版本；本ADR不赋予真实连接或Video父资格。

**严格边界。** Hello为type/channel/protocol三字段；Challenge为type/protocol/sessionId/serverDeviceId/serverNonce/certSha256/expiresInMs七字段，固定序列化V2。入口1..4096字节包含首尾空白，MaxDepth1，按解码字段名去重、拒未知/缺失/错类型/嵌套/非法编码/尾随值；捕获JsonException及InvalidOperationException。protocol/expiry为正Int32整数字面量，拒小数/指数/0/负数/溢出。Guid canonical小写D、nonce canonical base64和pin大写hex各解码32字节，V2 Guid非空。构造输入、Copy及Serialize均新副本；不暴露私有ReadOnlyMemory底层数组。空白/字段重排/等值JSON转义允许，expiry不建立时钟或重置预算。

**分类在完整校验之后。** Invalid默认0；完整合法protocol1为LegacyV1；完整已知形状的其他正Int32版本为UnsupportedVersion；仅protocol2成功为AcceptedV2且创建对象，其他out=null。V1现有合同允许canonical零Guid，完整旧报文保留LegacyV1/null；V2和未知版本零Guid仍Invalid，不收紧旧共享helper。LegacyV1仅未认证版本声明，不证明真实旧端、不触发fallback。未来消费方不得跳过目标device/实际TLS证书pin检查，或用升级提示覆盖proof/密码/审批错误；EOF/timeout也不等于旧端。

**真实证据。** 同93身份red84PASS/9Assert FAIL→green93PASS；八生产变异build0/test1全被杀死并恢复，restored93PASS。final-v1四Rebuild零警告错误、16TRX每配置5112PASS/0FAIL/0SKIP，原5019+新93稳定身份精确相等，376源码和29文档及12边界一致，完整冻结外层真实0。独立审计100项正式自测与7项交接护栏真实wait0；正式审计独立核43TRX/20574唯一execution/4598历史文件，外层真实0；不把自测107加入产品数。主线程修正审计候选的相对路径、交接字段、终态键和外置manifest偏差，新增正式入口内存正反例，不放宽post验证。报告完整字节绑定执行期stdout，内嵌1118项完整证据（含空audit log）；before允许精确3新增及空log转终态，实际passed并保护1121原项。

**交付边界。** 证据目录`outputs/m6-wire-v2-20261012/`；376源码SHA `bfd00f3a1a4965f9ae2ddbaa785f363fa090a37430c38b3d7bf972391ed14dd5`，正式报告SHA `90c61a45b181f13c60438bd242b4d152d96ff9536288589753234c0e01c8f0a8`。HANDOFF/本ADR/当日日志/MEMORY为审计后四文档例外，新增唯一验收TXT，最终after以handoff-v1/post-handoff-check.json实际结果为准。快照不是连续锁，自报PID/摘要不是OS不可篡改证明；直接wait非全机排空，dotnet历史parent_pid未采集。未接真实V2/拒旧/升级UI或双机多屏，不代表M6完成。下一片为两Host入口与Client的V2-only握手接缝，沿ADR084顺序/原绝对预算/唯一读者/真实审批；新版父认证成功后才给Video资格，不改网/防护/权限、不启动M7、不覆盖原M5ZIP。

### ADR-086 — Challenge2版本提示前的连接身份绑定

**2026-10-12，前置消费入口已实现，live未接。** 原Parse对非V2返回null，直接按status给升级提示会绕过既有目标device/实际TLS pin校验。因此增加internal `AuthChallengeV2.ValidateForConnection(ReadOnlySpan<byte>, ConnectionIdentity)`，与Parse共用严格ParseCore；只有完整且匹配连接的V2返回独立载荷对象，不构造V1/未知版对象，不引入fallback。

**固定失败优先级。** identity非null→已有TLS PinsMatch→完整已知payload结构/语义/尾部→目标DeviceId→actual presented pin恒时匹配→版本分类。TLS pin错保持精确AuthenticationException；其余为既有ControlClientAuthenticationException及固定本地短码。畸形载荷先invalid；完整合法V1零Guid的历史Parse语义保持，在身份入口零device必不匹配，零session且身份匹配可给升级要求。V2/未知版零Guid先invalid。版本仍是proof前未认证声明，不能证明软件身份，也不能将EOF/timeout当旧端证据。此入口不处理proof/密码/审批，不能把后续安全失败统一变成升级提示。

**非授权边界。** 无流、时钟、密钥或审批，无session注册和Video资格；对象非一次性能力，未来live须在消费前后检查原绝对预算。真实旧Host收Hello2可能直接关线，新Client无法仅从EOF判断旧版；已发布旧UI也不能保证识别新增拒绝文案。不伪装Challenge1，不新增legacy live分支。

**实测。** 31专项旧实现31Assert红（全部为入口缺失，非31独立行为红）→新实现31绿；TLS/device/payload pin/提前Legacy分类四产品变异各4/4/4/3失败并精确恢复。四Rebuild零警告错误，16TRX双配置各5143PASS，旧5112+新31精确身份核对；独立核验实际exit0。355项源码快照不含tools/docs/memory/HANDOFF，未沿用上一片完整audit/post合同或100/7自测，不混称同强度审计。详HANDOFF§19.42及outputs/m6-challenge-admission-20261012/。下一片直接推进ADR084完整V2-only真实链与新版父资格，保持真实审批、原预算/唯一读者/清理和原Video合同。

### ADR-087 — V2-only完整认证接线、父资格与原预算末检

**2026-10-13，真实收发接线。** 两个Host首帧入口均严格消费完整Hello2，handoff构造再次校验已读payload，不再有默认V1构造路径。Client固定Hello2，按连接身份消费Challenge2；在原机器/Challenge绝对窗口内完成Host Offer→Client Accept→clientProof V2→真实本机审批→serverProof V2。Host Register与Client AuthenticatedControlSession构造均要求非空、同SessionId的DisplayCatalogAgreementBinding；binding只是经过方向/会话绑定的协商事实，不是认证或一次性能力令牌。Host仍须proof、审批、success写出才登记，Client仍须serverProof与CommitDelivery才交付。

**版本声明与UI。** 完整合法V1/未知正版本才产生固定升级/不支持短码；畸形、EOF、timeout不确诊旧端，目标device/实际TLS pin、proof/密码/审批失败不得被升级提示覆盖。不伪造Challenge1，不fallback。Host仅保存本监听轮的两种短码单槽，UI独立显示“未经身份认证”的协议声明诊断，不覆盖停止/排空状态，停止意图立即隐藏，窗口关闭停表解绑。真正旧Host可能见Hello2直接EOF，旧版已发布UI也不保证识别新提示。

**清理及读所有权。** AuthTranscriptV2的导出/proof/Clear共锁；Clear清零私有原数组并进入不可逆终态。Host生成serverProof后立即Clear并将局部置null，外层finally兜底；Client原数组同样在finally清理。不声称清零JSON字符串、TLS/框架副本或全部进程内存。审批Activity复用于holding，原Run/审批决定/读必须真实close/join，意外gate故障不得吞掉。测试只反射原Run Task的实际状态机和_client原引用，不引入生产observer、不用Export副本代替原数组。

**deadline证据。** timer只负责唤醒/中断，接受仍由原父/子窗口单调截止决定。新增Offer解析后、Accept write/flush完成后、binding构造后十四例。Offer到期发生在及时payload读后检查及分段timer释放之后；write/flush在原IO挂起时先推进截止，再放行。binding在第三次有效采样后已客观到期，第四次只检查；直接原NegotiateCatalogAsync隔离外层末检，不将其称为公开完整认证链或同步构造耗时测量。

**实际敏感性。** 删除Host立即Clear、局部置null、finally Clear分别1/1/4项断言失败；终态标志改false产生5项ObjectDisposedException缺失断言失败。Offer末检删除4例中2个无效Offer报错优先级变红，2个有效Offer仍被写前检查拒绝；binding末检删除6例中2个binding分支因返回agreement而红，其余write/flush被前检拒绝。单删Accept末检4例仍绿；再删binding末检，隔离write/flush四例因过期仍返回agreement而红。保留全部原字节恢复记录；不能把纵深仍绿写成单点覆盖。最初自定义Assert.True文案无“Assert.”使固定runner负例分类exit2，保持原失败证据，只给断言文案加标识；删除终态赋值曾引入CS0649，被零警告门禁挡在测试前，改为显式false的等效行为变异后才运行。未改固定runner或预算，不把未执行测试的阶段算运行红灯。

**不变与边界。** VideoHello/ACK protocol1、LANREMOTE-VIDEO-V1、83字节Video transcript、第二条TLS、LRVF和attach/join保持；仅Control父认证升级V2，不全局替换协议常量。真实回环TLS、受控IO、静态审阅与双机人工体验分开记账。最终双配置各5246PASS/0FAIL/0SKIP，四Rebuild零警告错误；384项源码快照含tools、不含docs/memory/HANDOFF。相对5143基线新增115/消失12个身份实例，新增唯一键107；三组长JSON显示名截断形成3/3/5的multiplicity，保留Counter计数而不改写为唯一测试数。正式独立核验实际exit0，详HANDOFF§19.43和本片原证据。不宣称M6整体完成、目录传输/切屏/每会话画质或M7已接入。不重做M4/M5人工项，不改网络、防火墙、防护或桌面权限，不覆盖旧M5ZIP。

### ADR-088 — Client认证后目录单读者、有界接续与释放证据

**2026-10-13，Client接收侧子片。** 仅原显式monitored/public Video attach登记的Control monitor扩展为目录接收循环；不在所有认证成功/旧internal Control-only路径新增读者。空闲保留原单字节ReadAsync，不建空闲timer；原读完整退出后才调用FromObservedFirstByte接续最多256KiB载荷。非零大端长度首字节可立即拒绝；零长/超限沿用精确FrameProtocolException。只消费严格Host→Client `host_status`、目录载荷`protocol:1`，不将Control认证V2外推为所有帧protocol2。未知、反向、认证帧和畸形目录均关闭父子会话。

**时间及发布。** 每帧10秒为初始预算，从唯一读者本地观察首字节完成后采样起算，不是物理到达时刻；idle调度间隙不冒称计入预算。沿用原同源单调锚点及频率，不因接续、timer释放或解析重置；组合完成后解析和消费仍复核原截止及高水位。目录是internal不可变单槽，替换而不排队、不回调UI；同父gate检查真实交付及连接有效性再发布，撤销时清空。返回过的不可变对象不是可撤销能力；seq/timestamp不是重放、拓扑、选屏或视频坐标授权。

**失败及所有权。** AbortRequired只用于及时请求撤销/关线/停止child，随后仍await同一个组合原Task；组合等待原接续、原I/O与timer释放。保留原Task的完整异常树、兄弟和cleanup根，不Flatten，不把本地Stop吞作成功。只清零完整交付且已静止的payload，未知借用区及TLS内部副本不作擦除保证。GetDisposalEvidence分别记录已取得原操作完成与全部已拥有根释放，目录未知借用/timer释放失败必须使后者为false；不合作原操作可持续阻塞，不能用超时包装冒称join。

**测试取证合同。** 正常协调阶段是WhenAny，只证明原read被continuation直接await；进入失败收尾后才证明原timer dispose贯穿monitor及父join。Reached通知可能发生在async方法返回/原Task句柄赋值之前，夹具用独立TaskCompletionSource<Task>发布确切原Task，通知不替代join。原红灯保留，不增加Guard或认可WhenAny作为完整等待。真实TLS目录正例由测试侧Host显式写入，不能代表产品Host已发送。

**范围。** 未接产品Host发送、公开目录UI、远端切屏、每会话画质或M7；不出M6产品包、不重复M4/M5人工验收，不改网/防护/权限或旧ZIP。当前片恢复定向125PASS、双配置各5279PASS、四Rebuild零警告错误；四有效变异9/1/3/3断言红后原字节恢复。相对5246身份+35/-2，385源冻结与三份证据核验通过。截止首次Guard失败保留，新观察点在错误发布后由Assert.Null明确裁决；不把Guard超时算有效kill。详HANDOFF§19.44，不继承§19.43的5246冻结。

### ADR-089 — Host单次目录发送底座及原操作关停边界

**2026-10-13，独立internal底座，未live接线。** 只新增`HostDisplayCatalogSendAttempt(Stream, Func<ValueTask<DisplayCatalogFrame>>, TimeProvider, TimeSpan)`及测试，不改Auth/Router/App/GDI/public factory/holding。现有Router先await Auth再close，因此不能把不合作source/write直接await插入Auth而宣称deadline可解堵；产品接线另片建立关闭监督后再消费本底座。

**所有权。** 冷构造；Start锁内发布固定协调Task，worker跨同gate后才调用外部；另一个固定operation worker隔离来源/Write/Flush的同步前缀，复用原ControlFrameBudget worker。不创建CTS、不给source传取消令牌；借用流的Write/Flush均用CancellationToken.None，不read/close/dispose、不重试。AbortRequired仅要求将来外层先请求Host原close并保有原关闭Task；底座仍完整await source/write/flush确切原Task、operation和原预算worker。未知原同步前缀或AsTask未交Task不能伪造已退出；未知写借用不清零。OriginalOperationsFinished与AllOwnedRootsReleased分开，timer创建异常/null未交付、释放未知/失败都不能宣称全部根释放。原Task.Exception保留兄弟、重复引用与嵌套，不Flatten；Stop不吞迟到自发取消或原错误。

**帧及截止。** 唯一顺序writer由外层保证，内部只取得一次已严格验证的不可变DisplayCatalogFrame，序列化为有界256KiB host_status protocol1，四字节BE前缀，一次Write及一次Flush；不授予认证或选屏能力。source之前本地观察锚点计入来源、序列化、IO及原timer释放；每阶段与最终接受均复核固定频率/Int128精确>=截止，不重置预算。sampleGate覆盖完整inner频率前检→GetTimestamp→频率后检→共享高水位发布，不只锁高水位变量；owner gate不嵌套sampleGate，Stop不调用clock/外部回调。坏clock永久阻塞时不能保证及时截止通知或有限join。门禁与锁外调用非物理原子，已越过门禁的调用可能在Stop返回后才进入。Sent仅为历史接受，不证明对端收到、未来权限或物理到达；Rejected无法回滚已写字节。

**本片测试与证据口径。** 受控source/stream/clock/timer，不是新产品Host的回环TLS或双机实屏。新增65例包括原句柄发布、同步前缀/不合作原Task的Stop/截止通知与join、错误树、未知借用、原timer释放、精确截止、跨worker高水位以及独立160字节literal线格式和256条/128名称/最大数字组合。未穷举AsTask阻塞细分、强制双worker同时争锁、timer.Change阻塞/失败和全部错误笛卡尔积。首轮join变异6红但自定义文案无Assert.令固定runner分类exit2，原证据保留；仅补断言文案标识后基线65绿。四项v2变异分别6/1/3/1断言红并原字节恢复，恢复682绿；高水位红是ObservationError归因缺失，既有预算可能仍拒绝，不能称去掉一项即错误Sent。完整回归已实跑四Rebuild零警告错误、双配置各5344/0FAIL/0SKIP、六子命令及外层wait0；387源快照，本片最终独立核验/提交收据以HANDOFF§19.45为准，旧5279不外推；不出M6产品包、不启动M7、不重验M4/M5，不改网/防护/权限或旧ZIP。

### ADR-090 — Router固定关闭监督先于不合作主体的join

**2026-10-13，M6关闭监督前置切片。** 在`FirstFrameRouter.RunAsync`已验证Host关闭权限、读取原enteredAt之后，先发布一个固定监督Task，等待异步continuation的`closeRequired`信号。Host令牌回调仅发信号；原core正常返回/早退/异常的finally也发信号。监督锁外调用原`AcceptedConnection.CloseAsync`、保存且完整await原Task；Router随后join监督、原审批活动读和决定，再完成已有子预约lease。不把通知或CloseHandle任务正常完成等同于物理释放成功，原socket/SSL释放失败仍由Host有界诊断保留。

**最小边界。** 只把关闭监督放到worker，`RunCoreAsync`和Auth、首帧及holding读的同步前缀仍从原调用链进入，不加同流reader、不改public六参数factory、Registry入口、SessionRegistration属性或Auth授权/秘密清理。目录发送底座的AbortRequired尚未接该信号，产品自动目录发送仍未实现；后续才接认证后持有登记资格的生命周期，登记Dispose完成后取VideoCompletion的合同不变。

**非保证。** 不保证任意不合作同步source/读/写/timer/close能有限退出；首次外部clock调用仍在监督发布之前，Host自身其它取消回调可阻塞本通知派发。Stop只通知并不与外部调用物理原子。保留既有首帧IO/ODE/OCE分类，不利用本片吞掉将来的目录来源错误；下一片须在原首帧豁免之外保存目录故障。本片受控测试/回归和提交事实见HANDOFF§19.46：新17/恢复464、双配置各5361通过，四Rebuild零警告错误；两变异各2断言红并恢复。独立核验36原TRX/21895唯一execution、旧5344+17/-0、388源及126证据保持，实际exit0；代码e7d098b已提交推送核完整号。stop变异是10秒有界观察未收到关闭信号后的Assert.Same红，不是无看门狗或Timeout异常证明；未新增核验器穷举篡改自测。

### ADR-091 — 认证后目录生命周期先撤销资格再完整等待

**2026-10-13，internal可选接线已验证；新基线22、恢复486、双配置各5383通过，四Rebuild零警告错误。** 三项精确生产变异分别3/6/4个原断言失败，均逐字节恢复；独立原证据核验exit0，旧5361完整Counter新增22/删除0，37原TRX/22012唯一execution、391源与146证据保持。代码新提交8001488；完整提交/交付收据见HANDOFF§19.47。 `ControlAuthContext.DisplayCatalogSend`默认null；仅Router在原Auth启动前冷绑定来源、预算及已有close通知，public未绑定入口不自行启动。完整auth_success Write/Flush及取消后检、Register成功、原sessionToken/successPayload/DTO token finally清理后，仍在原位置直接调用holding取得原Task，再启动固定`HostAuthenticatedCatalogLifetime`。保留public真正async RunAsync及原秘密局部，不移读同步前缀、不新增Control读者、不改Registry/Video证明或public factory。

**通知与等待分离。** owner的固定worker跨Start同gate发布屏障后才启动发送；首次WhenAny含原holding、Abort、Host stop及原sending，Sent后仅继续等待前三项，意外fault/cancel/Rejected也停止。RequestStop按close通知→发送stop→StopRequired通知；Auth在using内只等StopRequired，先Dispose撤销登记资格，随后才取稳定VideoCompletion，与owner/原holding完整WhenAll。Router最终仍等待原close及Auth全部owned操作。通知不等于原Task退出，Sent不结束父会话；目录来源/原IO/预算timer不合作时不保证有限join。

**错误及范围。** Router在原首帧IO/ODE/OCE catch之外、原close及Auth join后收集目录各槽；每槽独立来源标签包装原异常，不Flatten、不按实例引用去重。同Host token的自发OCE也不静默归正常停机；holding保留原分类。owner快照不涵盖Host close/Video原根，AllOwnedRootsReleased不能外推物理释放。原holding同步前缀未交Task时不能越过它提前注销，仍由既有Router Host取消监督请求close。App/GDI来源、默认产品发送、UI/切屏/会话画质尚未装配；本片测试、变异、full和提交以HANDOFF§19.47实际记录为准，不沿用5361；Registry真实租约测试不是实际视频TLS子连接验收。

### ADR-092 — 公开惰性纯数据目录来源与App默认装配

**2026-10-14，Host来源片。** `ControlAuthContext.WithDisplayCatalogSource(Func<ValueTask<IReadOnlyList<DisplayInfo>>>, TimeSpan)`仅返回record副本，保留原SecretLoader、Registry、两个limiter、审批门、时钟和选项引用，原context不变且默认仍null。公开面不暴露wire DTO、流、token或序列化器。App在原StartAsync创建同轮context后显式装配，委托执行时每次创建独立GdiDisplayCatalog只读枚举；不在启动/配置时提前枚举、不改变现有CreateWithVideo、认证/审批/holding线程或视频采集。不同已认证连接可并发调用，来源必须并发安全且交还代表全部所属工作的原ValueTask；不得隐匿后台工作或交还后并发修改列表。

**原操作与失败。** 外层桥不是async，先直接调用source().AsTask()，只有原Task已交还才进入异步投影。因此source/GetStatus/OnCompleted同步失败未交Task仍由sender记录未知退出；GetResult失败但AsTask已交回faulted Task则为已知退出。投影完整await原Task，保留原Task.Exception的兄弟/嵌套/重复引用，不Flatten，不用WaitAsync或新增Task.Run/CTS取代原任务。单根faulted OCE可能经async投影变为Canceled，但原异常实例及token仍保留为来源故障；不声称投影与原Task状态等价，也不能重建上游已丢失的异常。

**消息与预算。** 每父连接单次发送，seq=0；来源成功后用UTC毫秒作为历史消息元数据，非拓扑世代、安全时钟或当前视频帧坐标。沿用内部DisplayCatalogFrame的有界不可变复制和严格校验；不造默认主屏、不截断目录、不授予采集/切屏资格。App发送总预算10秒，从原source前单调观察起算并覆盖投影、序列化、IO及timer释放，沿已有close/注销/原根join合同；不保证原生GDI可中断或最终等待有限。

**验证边界。** 新来源24例、产品3例定向已通过；产品用例实际StartAsync、单机Control TLS/pinning、WPF控件自动批准/拒绝、真实只读GDI、成功后自然注销；没有视频attach，不等同双机、真人审批观察或物理多屏验证。透明来源计数在StartAsync之后安装，不能单凭它证明启动期间cold。异常树/外桥async/App删装配三变异各3断言红并原字节恢复；App红停在配置非null断言，不称三条独立线缆行为kill。初始Expression命名歧义只属编译失败，别名修正后通过。新执行器完整build/test及原wait收据校验后才恢复；缺收据不会自动恢复，未做异常中断故障注入、不修改历史执行器。恢复Transport111/产品3、四Rebuild零警告错误、双配置各5410通过。独立核验实际exit0，旧5383完整Counter+27/-0、39原TRX/21738唯一execution、393源及168证据保持；代码c745ae42c92593e937ec85d8bbd8e0899f993d01已普通推送并核远端完整号，提交后393源/168原件再次核对一致。完整收据见HANDOFF§19.48。

**非目标。** 不修改Client目录UI、切屏、会话画质、M7、网络、防火墙、防护、桌面权限或旧ZIP；不重验已收口M4/M5，不宣告M6整体完成。

### ADR-093 — Client公开只读目录及WPF单槽采样与清理

**2026-10-14，Client投影片。** `DisplayCatalogSnapshot`为sealed纯数据外壳，仅有get-only的Sequence/TimestampMs/Displays，构造internal；DisplayCatalogFrame严格校验并复制后生成一次外壳，内部wire字段转发到它，不另设可变会话槽。`AuthenticatedControlSession.GetDisplayCatalogSnapshot()`在原父gate下，仅已交付且未撤销时返回当前外壳；没有完整目录返回null，重复读取返回同一实例。公开面不暴露wire DTO、流、token、写入或选择能力；撤销只能影响后续查询，不能抹除调用方已经保留的历史数据。保留原Control唯一reader、256KiB/10秒原锚、终态撤销和原根join，元数据不变成反重放/拓扑世代/视频选屏证明。

**App装配。** 真实ClientConnectService在原Video attach之后的Dispatcher建窗块中绑定父会话getter与本Attempt停止token。RemoteViewWindow每实例仅绑定一次，单DispatcherTimer以250ms名义间隔在UI线程读取；不另启网络reader，不启后台投递循环、不缓存目录队列。相同实例不重绑ItemsSource，seq下降的新实例仍显示。只读ItemsControl有120高滚动区域，显示枚举ID、名称、宽高、原点和主屏标记；明确标注非当前视频选屏状态，无选择/控制按钮。250ms不是实时刷新或撤销墙钟上界，Dispatcher阻塞和父gate竞争可推迟显示；目录纯数据不是持续授权能力。

**清理。** getter前后检查停止/关闭/终态，防取值期间取消或重入停止后的历史结果再次显示。本地Closing在请求网络停止之前清目录；服务在进入原四支Drain前解除绑定，窗口SessionEnded再幂等清理。停止计时器并退订Tick，清getter、快照、停止token与ItemsSource；迟到Tick不复活，不允许旧窗绑定新会话。原错误诊断保窗与断开回调释放保持，窗口排空证据扩展到目录所有权。UI清理错误追加到原错误树，不覆盖旧错，也不能阻止TLS/视频原任务join。

**测试与局限。** 新Transport11例实用受控原reader；WPF10例用反射构造纯数据，明示不等于认证证据；新增产品3例走真实双TLS、显式测试审批gate、合成两屏目录与2x2 JPEG，不等于真人WPF审批、双机或物理多屏。慢停止用例预先登记阻塞取消回调，确保最终WaitAsync先收到取消；禁用UI timer退路，以Control撤销证明已进入Drain后直接断言目录清除、原取消/清理仍未完成。原错误登记顺序和首次Guard变异失败保留不算kill；六项正式变异为公开getter丢失3红、取值后停止检查2红、timer启动1红、ItemsSource清除4红、App绑定3红、Drain前清理1红并恢复。App绑定变异止于非空getter接线断言；慢Drain变异为真实原Null断言，不拿Guard超时当通过。完整验证与交付收据见HANDOFF§19.49。首轮full因两旧公共方法精确白名单漏列GetDisplayCatalogSnapshot而2红，仅精确加入该纯数据方法，保留其他API约束。新恢复656/20、四Rebuild零警告错误、双配置各5434全通过；独立原证据核验exit0，旧5410完整Counter+24/-0，397源、357原件保持。正式变异使用v4字节，最终白名单两处修订由逆替换SHA精确衔接；55TRX/28711execution不含另外六个仅做原件/wait保持核对的失败历史阶段。

**非目标。** 不切屏、不改变采集/视频几何/会话画质，不开始M7，不改网络/防护/权限，不重验M4/M5，不重打旧ZIP。本片无必须先由双机人工完成才能继续的开发依赖；不取消后续M6实机验收。

### ADR-094 — 切屏前置的固定目标生产者准备，暂不冻结跨通道切屏消息

**2026-10-14，内部准备实现及验证完成，live切屏未接。** 原规格仅列出`request_display`/`display_changed`及通用消息元数据，未规定视频边界关联字段。现有Video租约只消费一次，writer取消/出错会永久关闭；停止pipeline不能收回sender或Client已持帧，编码器重建又从frameId=1开始且编码首帧可能被DropOldest丢弃。因此本片不先冻结`firstFrameId`等wire字段，不把Control通知当视频已显示，不以重连Video代替切屏。先建立无跨TLS假设的固定目标生产者准备入口，后续再确定同一视频流连续编号、整帧写出和Client消费/显示屏障。

**准备合同。** 在`FramePipelineVideoProducerFactory`增加internal的`CreateForDisplayAsync(Guid, DisplayInfo, CancellationToken)`，公开`CreateAsync`仍默认唯一主屏。目标是本次调用固定的不可变数据，不增设工厂共享可变选择槽；同一工厂不同调用仍必须取得各自专用的capture/encoder。目标分支在当前后端目录完整枚举中按ID唯一匹配，并比较完整`DisplayInfo`记录；缺失、重复、元数据不等明确拒绝，不回退主屏，不把数组位置当ID。只对选中目标执行既有维度/像素预算，其他非目标大屏不成为拒绝理由。该内部入口不追加wire的0..255限制，不授予认证、句柄或网络能力；记录相等也不能证明硬件身份、拓扑世代或之后不会热插拔。共享预算诊断中性化：公开默认路径超预算仍抛原`ArgumentOutOfRangeException`，但`ParamName`从`primary`变为`selected`、文案从“主屏”变为“目标显示器”；不宣称诊断字符串逐字兼容。

**生命周期。** 两入口共用原后台调度、创建核心及失败清理路径。全部工厂/枚举同步前缀仍在原Task.Run中，不传调度取消、不用取消代理提前交还；最后取消检查前由局部变量拥有依赖，成功仅交付尚未Start的生产者。目标失败时不创建encoder或pipeline，已取得的capture仍完整清理。原异常实例/错误树、借用对象不释放、同对象依赖去重及原Stop/Dispose完整join保持；准备成功不清除旧视频根，也不是可复用writer的暂停信号。

**验证状态与非目标。** 新增41case/11方法，保留原116工厂用例与夹具，初始及恢复工厂各157PASS；四Rebuild零警告错误、Debug/Release各5475PASS/0FAIL/0SKIP。独立核验exit0，旧5434完整Counter+41/-0，397源仅工厂/测试两文件变化、173观察原件保持，39原TRX/22163唯一execution；五变异metadata/duplicate/target-id/original-task/selection-cancel各6/3/3/4/1原断言失败，完整wait后逐项精确恢复。真实pipeline配受控capture/encoder，不是GDI/物理多屏/TLS联合切屏；大尺寸边界仅prepare/Stop。新核验器未做穷举篡改自测或异常中断注入。本片不改GDI主屏限制、Transport唯一读者/认证/租约/视频头、App只读目录或会话画质；没有live远端切屏、物理多屏/双机人工验收，不启动M7，不改网/防护/权限，不重验M4/M5、不覆盖旧ZIP。新增切屏消息与现有V2目录peer的能力协商及失败响应仍待设计，不能仅凭应用profile=2就发送新消息。

### ADR-095 — 显式可选的发送尝试编号与原整帧写出生命周期

**2026-10-14，内部实现；Router/live未启用。** 编码器每次重建从1起，编码结果还可能被有界队列丢弃；共享编码序列也不能保证并发发布后的实际发送顺序。故不加Capture共享序列作为必经前置，改在单次、串行sender即将调用writer时分配。保留原默认`VideoFrameSender()`的完整ulong/重复/倒序透传；显式internal `VideoFrameSender(ulong firstFrameId)`才启用该实例独占序列，起点允许0，不用static或跨会话共享。未修改Router构造点，产品路径仍默认透传。

**编号与耗尽。** source原调用完整完成→接管帧→caller检查→EOF→原整帧预算建立/复查→分配→await原writer→原预算末检→timer/frame清理。MaxValue只分配一次，不回绕；下一次仍正常读取来源，EOF正常结束，多一帧则在原写前预算检查后以InvalidOperationException拒绝并释放。等待来源期间仍无新增写预算/硬中断，caller取消和原来源错误优先级不变；耗尽不允许重用sender。分配是写入尝试顺序，不保证首字节写入、完整发送、Client消费或显示；写失败后不重试半帧，也不回收或复用编号。

**借用写出。** 新internal `WriteFrameWithIdAsync(EncodedFrame, ulong, CancellationToken)`与原入口直接返回同一个private async核心的原Task。先FromFrame验证原模型，再仅覆盖值类型header.FrameId；原frame.FrameId/有效payload切片/owner均不改，不构造第二个EncodedFrame，不形成双owner。两入口共用单writer状态、整帧header→payload→Flush→末检、永久终止与一次关闭/诊断；关闭通知不等于join或成功释放。sender仍在原写任务退出后才释放frame，单次运行、不预取、不新增取消代理；来源主异常、写出主异常实例/OCE token及有界清理诊断保持。

**验证与边界。** 新增61case（sender23/writer38），初始定向147PASS；五项生产字节变异：删header覆盖3红、删递增4红、绕过耗尽1红、原await之前释放owner6红、删整帧最终绝对预算2红。每项完整wait/原TRX断言核验后精确恢复；owner变异证明提前释放敏感性，不夸作取消代理或所有join删除的单点覆盖。恢复定向147PASS；四Rebuild零警告/错误、Debug和Release各5536PASS/0FAIL/0SKIP，原六dotnet命令及launcher/worker完整wait0。正式独立核验exit0：旧5475完整四字段身份保留、+61/-0，397源仅两生产/两测试改变，174观察原件重哈希保持；39TRX/22334唯一execution（含旧baseline、当前full、初始/恢复定向及五失败变异）。新增证据为受控Stream/来源与真实Task，不是GDI/JPEG可解码实屏/物理多屏/双机/真实TLS联合切屏。保持LRVF40字节/flags与reserved=0，reader排序规则不变；未冻结Control切屏字段、能力协商、跨TLS或Client显示屏障，未接运行中source/producer换代、每会话画质。无M6产品包、不启动M7、不重验M4/M5、不改网/权限/防护/旧ZIP。

### ADR-096 — 内部拥有型视频来源的生产者换代与完整退役

**2026-10-14；不接Router/live。** 新internal `ReplaceableVideoFrameSource(Guid sessionId)`实现`IVideoFrameSource/IAsyncDisposable`，初始无producer，读取等待安装而非返回EOF。单session、单read、单replace，不排队候选。`ReplaceAsync(IVideoFrameProducer next)`同步检查；正常返回Task即接管专用且尚未Start候选，之后异步失败也不返还。同步拒绝不接管新对象，同引用重复提交不改变既有归属。没有可撤销替换或替换超时代理。

**先退役后启动。** gate内封口旧代读准入并发布原worker，gate外分别发出Stop、Dispose和Completion观察，任何同步入口阻塞不妨碍其他入口发出。退役包含原Start、原底层Read的同步前缀及异步尾部、迟到帧清理；不等整个外层source Read，后者可能要等待新代，避免循环等待。旧代完全结算成功后才启动并发布候选；旧代或候选真错永久终止，不回滚到已经Stop的旧producer。Start/Read外部调用前先确认Task已在gate发布；所有外部方法、Completion getter、拒绝证明探针和owner Dispose都在gate外。

**所有权与终止。** 非null帧在gate内提交给读者后本来源立即清空局部owner，不做后置取消检查；换代/Dispose先赢时旧帧在锁外释放一次，原attempt退出才算结算。已交付sender的旧帧不被收回，换代不取消writer，换代成功不是发送或Client显示屏障。活动代真正EOF永久封口，退役代EOF只是内部过渡。当前不另起active Completion监视器，仅由EOF/真实读取故障/Dispose终止；不隐含生产者停止即source所有原Read已退出。终态协调先join代际根及原Replace，再发布内部settled使外层Read退出，最后完成稳定Completion；Dispose始终返回同一个Completion。

**错误与取消。** 原任务只AsTask一次；保留原Task多根和嵌套错误，不Flatten、不按异常引用全局去重。Stop/Dispose/Completion只有同一个原Task才去重，独立操作即使共用异常实例仍各保留。读取真错与迟到owner清理真错同时进入Replace失败和最终Completion；caller token真实Canceled原读可取消本次读取而不终止生产，Faulted OCE/异token及清理OCE仍是真错。Start拒绝只在已观察退役且有同实例显式证明时作为停止竞争处理，探针错误和原错误均保留；不靠事后Dispose重判。依赖/owner回调不得等待本来源操作或Completion，以免自join。

**验证边界。** 初始定向195PASS，新增48case；其中3case在旧帧header/payload/flush原IO仍受控阻塞时完成换代，核对owner存活、未预取、原token未取消，放行后同sender连续70/71编号写出。五项生产变异late-owner/delivered-owner/shared-task/original-roots/replace-read-error分别1/2/1/1/2原断言红并精确恢复；不是删除原join或取消代理的独立kill证明。恢复与完整双配置验证结果另记HANDOFF，不以静态审查替代实跑。受控producer/Stream非实际GDI/真实TLS/物理多屏；不改Router、Control codec、Client/UI、FramePipeline、LRVF、Video单次租约与现网安全边界。

### ADR-097 — 父资格约束的内部视频来源发布与同步候选接纳

**2026-10-15；内部借用入口，不接Router。** `SessionRegistry.PublishVideoSource(VideoAttachLease, ReplaceableVideoFrameSource)`在原registry gate核真实entry的lease引用、source只读SessionId、控制取消及lease Revoked/Completed。同一租约只允许一次发布，摘槽不恢复发布资格；返回的IDisposable只摘所捕获entry的槽，不查裸Guid删除新entry，不接管source生命周期。不扩大public API、Snapshot或秘密导出面；旧内部方法白名单有意仅增加PublishVideoSource，Register仍只由认证状态机调用。

**同步接纳。** `SessionRegistration.ReplaceVideoProducerAsync(IVideoFrameProducer)`是非async方法，同一registry gate核当前entry引用、句柄释放、控制取消、子租约终态和已发布槽，然后直接返回source.ReplaceAsync的原Task。同步拒绝不接管新候选，正常返回Task即按ADR096移交；无代理、请求队列或超时取消。候选专用且未Start是调用方合同，接口没有可验证的候选SessionId/IsStarted。registry→source锁序固定；source同步入口只登记worker，不在锁内调用外部producer。

**撤销不等于退出。** Unregister同gate摘表/摘槽/清token/通知Revoked；真实VideoAttachLease与registry共用gate，Complete与接纳可排序。publication Dispose、注销、Complete只能拒绝后续接纳，不撤回已接管候选；先接纳的候选仍可能在注销返回后Start，必须由未来生命周期owner主动Dispose source并完整join。控制CTS自身不与registry gate同锁，只承诺检查点的取消观察，不承诺墙钟硬截止。AttachWindow仅限制初次附着，窗口到期不拒绝合法在流换代。

**调用方与后片。** 发布前ACK完整退出、初装成功及sender原worker登记是调用方前置，本原语不验证Ready/ACK，空或已关闭source也不由publisher判定。后片Router先满足前置再发布；退出先摘槽，独立通知close/cancel/source Dispose，再join原factory或初装、sender、peer、source Completion、取消和close，最后原Router最外层Complete租约。source拒绝迟到工厂候选时局部owner仍需完整回收；source终态的早期通知与唯一权威错误槽留后片，避免把Replace/sender/source同一错误重复累计。现有Router默认透传编号/sole-error顶层形状保持，优先新增内部显式装配而非静默改变旧路径。不宣称发送/显示屏障或M6 live切屏；不改网络、防护、权限、Control/UI/GDI，不重验M4/M5、不启M7。

**证据。** 本片新20方法/31case；初始/恢复定向各150PASS/0FAIL/0SKIP（含既有登记表/换代来源回归），源码前后快照一致。五项生产字节变异：lease-identity删真实租约引用核对2红、withdraw漏摘槽1红、original-task以Task.WhenAll包装原Task**未击杀**（单任务WhenAll返回原Task，如实记not-killed，另以original-task-proxy换成确实不同的ContinueWith代理1红补证）、parent-cancel漏父取消1红；均完整wait/核验/精确恢复。双配置Debug/Release各5615PASS/0FAIL/0SKIP（Transport3997=3966+31），四Rebuild零警告零错误。独立核验exit0：旧5584完整四字段身份保留、+31/-0、400源码项、39TRX/22707唯一execution。测试的阻塞外部入口验证当前实现不持registry gate，未作同步外部调用回归的卡死变异或共享gate删除的独立kill，不穷举竞态。

### ADR-098 — Router内部显式视频换代装配与先摘槽的完整生命周期

**2026-10-15；内部可选装配，非live切屏。** FirstFrameRouter工厂构造追加`enableProducerReplacement=false`与`firstFrameId=null`。默认仍为FixedProducerFrameSource/默认sender；只有显式启用才使用ReplaceableVideoFrameSource，可选编号仅作用于该sender实例。未启用换代却指定编号同步ArgumentException拒绝，不静默忽略。public API不扩张，App及Host默认装配本片不切换。

**发布及移交。** ACK原写/Flush与末检查完整退出后才调用factory；其成功结果先由局部owner持有，source.ReplaceAsync正常返回原Task才移交。初装成功后，在共享生命周期gate先登记真实sender worker，再以真实VideoAttachLease发布；worker取得同gate检查后才进入SendAsync。同source/sender/连接承接SessionRegistration换代，仍不代表发送或显示屏障。同步拒绝的迟到factory候选由局部owner独立调度Stop/Dispose/Completion并完整join，仅同一原Task去重。

**固定监督与撤槽顺序。** RunAsync入口建立私有VideoPublicationLifetime，以共享gate串行发布/停止/摘槽。host取消始终先Withdraw再通知原close监督，覆盖附着之后仍可能阻塞的clock同步前缀。Withdraw仅标记停止、摘registry借用槽并发出稳定异步StopRequired，不调用外部producer/close或等待任务。HostOwnedVideoStream新增内部可选beforeClose回调，让writer先行失败或Dispose也先摘槽再请求Host关闭；两处WhenAny均消费StopRequired，避免sender帧清理和物理close同时阻塞时遗漏取消与source停止通知。

**通知不等于join。** peer在外部Stream同步读前缀之前登记worker。退出先摘槽，独立通知close/取消/source.Dispose，不让任一外部同步入口或取消回调截断其他通知；随后完整join setup内原factory/初装、sender、peer、source Dispose返回的稳定Completion、取消与原close监督。真实lease.Complete仍仅在Router最外层。source只新增只读internal Termination（原closingSignal.Task），用于早于最终Completion的封口通知，不监视活动producer.Completion。

**权威错误槽。** source.Completion是新路径唯一来源错误槽，初装/Replace/sender传播只join，不重复累计；保留Aggregate原树，不Flatten。sender新增internal SendFailure，区分来源、主发送、帧释放、writer释放；不同操作即使共用异常实例仍逐槽保留。新路径的来源Aggregate形状与旧路径区别明确，旧路径sole-error顶层和默认编号行为保持。

**范围与当前验证。** 新14方法/36case均属于FirstFrameRouterBoundaryTests。最终同树初始定向339PASS；writer-withdraw/source-double-count/sender-sequence三项生产字节变异各1原断言红，原进程完整wait后精确恢复。首轮测试编译失败保留，第二轮339绿后仅补测试时序，第三轮作为正式同树初始；四Rebuild零警告错误、双配置各5651PASS/0FAIL/0SKIP，独立核验实际exit0：旧5615+36/-0，402源含历史根prompt例外，38TRX/23553 execution；代码029032b已普通推送并实读远端一致，完整收据见HANDOFF§19.54。受控Task/Stream，不是实际TLS、双机、真人、GDI或物理多屏验收；仅证明可观察通知/完整收尾，不夸大已严格定序到第二WhenAny，不称穷举竞态或全部join删除变异。无Control/UI/GDI/live切屏/画质改动，不重验M4/M5、不启M7、不改网/防护/权限/旧ZIP。

### ADR-099 — Control切屏请求纯载荷与真实父会话单槽准入

**2026-10-15；最小内部合同，非live或完整切屏worker。** 原规格04只列`request_display`/`display_changed`及通用seq/timestampMs，未冻结成功屏障。先实现`DisplaySwitchRequestFrame`：type=`request_display`、protocol=1、canonical非空sessionId、非负Int64 seq/timestampMs/catalogSequence、0..255原始displayId，恰好七字段、载荷1..4096字节；严格拒绝缺失/未知/重复及转义等价重复、非整数/溢出/畸形JSON/非法编码。预期session只做关联；parser不触流/时钟、不提供授权。protocol1仅此载荷schema，不改变V2认证、VideoHello/ACK或LRVF；现有profile2本身不承诺已支持此操作，live能力接线后续再定。

**单会话准入。** `SessionRegistration.CreateDisplaySwitchAdmission(hostCatalog)`在真实registry gate创建一次并绑定entry内的准入实例身份；关闭后不能重建，手动构造内部对象不能绕过身份。前置为真实父entry仍在册/未取消、真实子lease未撤销/未完成、已发布source且尚未观察到Termination。仅持有已验证不可变目录，不证明它已发出或拓扑新鲜；调用方必须使用本Host目录，最终固定目标准备仍须完整枚举、唯一ID及record匹配。

**有界预约。** 同gate至多一项无资源预约，无后台队列/计时器/Task；结果优先为Unavailable→SessionMismatch→Busy→StaleSequence→CatalogMismatch→DisplayNotFound→Accepted。仅Accepted消耗请求seq，首个可0/跳号、此后严格递增；long.MaxValue可接纳一次后自然耗尽，不回绕。不把该比较推广为全Control通用序号或认证反重放。catalogSequence只与本准入绑定目录元数据匹配，不是拓扑世代；timestampMs不决定准入。ViewOnly/Control都可选屏，不改变已授予权限、不授予输入控制。

**所有权与边界。** 预约只保留原请求与Host目标record；Dispose幂等仅释放自己的槽，旧预约Dispose不清新槽。准入Dispose仅封口，不注销父/停止source/完成lease，也不撤回历史record。IsCurrent是父gate内瞬时快照，source终态和外部取消与registry不是统一关闭锁；不得跨锁把true当授权。后续原factory与Replace仍由完整worker持有并join，真实Replace需再次过原父gate；本片不执行factory/Replace、不接唯一读写者、不冻结display_changed/成功ACK/首帧编号、不做Client显示屏障/UI/GDI/画质。单次Video租约不重连。

**验证。** 代码580fc12已普通推送并实读一致。新增348case（codec312/准入36），正式初始/恢复491通过；四生产字节变异1/2/1/1原断言红、完整wait后精确恢复。四Rebuild零警告错误，双配置各5999PASS/0FAIL/0SKIP，独立审计实际exit0：5651完整身份+348/-0、405源、40TRX/25267execution、183观察原件退出后保持。v1/v2各490为补测试前历史绿，固定SHA和逆字节桥核实，不冒充最终同树。受控producer/Task非TLS/GDI/双机/真人/物理多屏联合验收；完整收据见HANDOFF§19.55。

### ADR-100 — 切屏请求执行器的候选所有权与完整收尾

**2026-10-15；内部显式装配，已验证并提交35a372d。** 从真实`DisplaySwitchAdmission.CreateExecutor(prepare)`同registry gate仅创建一次执行器；已有手动预约则拒绝，装配后手动准入不可绕过执行器。`TryExecute`保持ADR099裁决优先级，只Accepted消耗seq；同gate先预约、再发布原执行Task，原Task未退出即Busy，即使finally已释放预约也不提前开新执行。没有队列、计时器、重试或重新附着Video租约。

**原操作与候选。** prepare委托接收本Host目录中的原不可变目标record及本执行器创建令牌，必须交付专用且未Start、未被别处拥有的生产者。同步前缀在已登记worker内锁外执行；原Task成功返回即由执行器接管候选，不能在取消后丢弃迟到结果。工厂仍负责失败前未交出的依赖。此片只提供该委托接缝，不直接装配Sessions固定目标工厂或GDI；最终工厂仍须按ADR094重新完整枚举唯一ID/record并在预分配前检查预算。

**移交。** 不是用IsCurrent快照兑换授权：预约/执行器引用身份、关闭状态及父资格在原registry gate内核验，并同步调用既有Replace入口，直接取得其原Task。正常同步返回才清空局部owner；异步失败不返还候选。同步拒绝保留本地owner，StopAsync、DisposeAsync、Completion getter各自独立调度，完整join所有原根，只按相同原Task引用去重，不按异常实例去重、不Flatten错误树。执行器不自行Start/Read候选，不触帧owner。

**停止与结果。** RequestStop在同gate封口、发StopRequired并登记独立取消worker；回调在锁外运行，阻塞/抛错不能阻止局部候选三路清理，Completion仍须等回调及原执行全部退出。只抑制本执行器令牌对应且已取消的同步OCE/真正canceled原Task；faulted OCE保留。稳定Completion/DisposeAsync只证明本执行器已取得的操作完整退出，不能证明违规工厂的隐藏根已释放。执行结果Transferred/ReplacementCompleted只记历史换代事实，绝非发送/显示成功，Stop不能撤回已移交的候选或已经交出的帧。

**外层责任及非目标。** 不自动监听父会话退出，不拥有source/lease/流。外层在父关闭或StopRequired时必须独立停止相关source等依赖，再join本执行器，避免在途Replace等待原代际导致自join；factory、生产者及取消回调也不得等待本执行器。状态检查与锁外factory进入不是硬中断边界。错误采用原Task树逐操作保留；后续Router若汇总还须明确source与执行器传播错误去重合同。本片不接Control唯一reader/writer、成功回执、发送首帧边界、Client显示屏障、App默认路径或画质；不宣称完整M6或双机/物理多屏通过。

**实际验证。** 正式初始v3与恢复定向各181PASS；新增66case。五生产字节变异（预约最终有效性/移交owner清空/原清理Task去重/原错误树/原执行Task单飞）各1原断言红，完整等待后精确恢复。原Task单飞用例仅测试反射释放预约以隔离门禁，非自然调度窗口证明。首轮3warning/2编译error且test未运行、v2历史180绿保留，固定测试SHA逆向字节桥核实；最终Drain直接等待原根，超时只用于正文诊断。四Rebuild零警告错误、Debug/Release各6065PASS，5999旧身份全部保留；独立审计原进程exit0及主线程wait0，407源/40TRX/24680execution/195观察原件保持。全量原launcher收据证实worker/六dotnet完整wait0，但本轮主shell后台句柄丢失，未伪造该句柄回收结果。详HANDOFF§19.56。

### ADR-101 — 本地整帧写入成功边界与提交帧的代际归属

**2026-10-16；内部显式能力，已验证。** ReplaceableVideoFrameSource增加带out身份的ReplaceAsync重载，保持同一个原换代Task及同步所有权判据。身份是无producer/source/frame引用的不透明对象，既不是序号也不是授权。逐次ReadForSendAsync凭据与普通Read共用单读者与原Task链；只在原帧交付gate内写入该帧真实所属代际，迟到旧帧被释放或EOF不产生交付身份。不能在写完时读取“当前活动代”反推在途帧归属。

**成功边界。** VideoFrameSender显式构造(firstFrameId, source)绑定来源引用与原会话并启用既有串行wire编号。只有原writer任务正常完成（40字节头、有效payload、Flush及writer终态检查全部返回），且sender原绝对写预算/caller取消末检通过，才以单次Volatile发布不可变元数据快照。快照最多保留最近成功代际的首帧与最新帧wire编号/宽高，不含payload或producer、不新增队列/订阅/独立worker。取帧、编码、编号分配、Replace完成或仅写完payload都不是成功。部分失败/到期/取消不发布新成功、不重试或回绕；frame/timer清理随后失败不撤回已经成立的写入历史。

**换代、停止与局限。** 旧帧已提交给sender后，Replace完成或source关闭不能收回它；其后本地写入成功仍记旧身份，新代实际成功帧才替换快照，零成功帧的中间代不伪造记录。历史快照不是当前资格、接收ACK、对端解码或UI显示证明；末检后发生取消不反向抹去历史。单槽不保证消费者观察到每一个历史代际，后续Control协调必须先绑定具体请求/代际并建立自身屏障，而不能轮询当前值冒充可靠事件交付。默认sender/Router/App不启用该能力，不改Core帧、LRVF格式、权限/认证或单次Video租约，不接Control回执/Client显示屏障/画质。

**验证收据。** 新38case、正式初始及恢复定向各233PASS；early-publication/final-budget/stale-generation/first-overwrite/source-frame-id五项唯一生产字节变异分别24/2/3/4/15原断言红，原进程完整退出后精确恢复。旧身份6065全保留、新增38/删除0；四Rebuild零警告错误，Debug/Release各6103PASS/0FAIL/0SKIP。独立核验原进程exit0，408源/157观察原件、39TRX/24992唯一execution及十五旧ZIP摘要保持。只验证本片和固定6065基线，不递归重审全部历史；受控Task/Stream非TLS、GDI、双机或物理多屏验收。原件及完整提交/交付收据见HANDOFF§19.57。

### ADR-102 — 请求绑定的代际首发结果与无成功结算

**2026-10-16；内部显式底座，已验证。** 不轮询最近成功代际单槽，不增无界事件队列；每个真实接纳的代际持有一个异步continuation的原FirstSend任务。显式追踪sender交付帧时在source交付gate登记尝试，sender原writer退出并通过末预算检查才记首个成功；失败尝试在原操作退出后的finally结算。source退役在同交付gate封口，封口且没有未结算尝试才得到null；封口时已有旧帧在途仍可迟到成功，不能抢先记null。已有成功永不撤销，后续成功不能覆盖首帧。

**生命周期与范围。** FirstSend只携带本地wire帧元数据或null，不传播原生命周期异常，不拥有producer/frame/source。它不是sender/source完整join、当前权限或远端接收/显示ACK。source退役和Completion从不等待FirstSend或sender；外层仍须独立停止并完整join来源、发送器、执行器及物理关闭。发送失败但来源尚未封口时仍可未结算，不以“暂未成功”猜测永久失败。普通读取/未追踪sender不计入这项发送事实，null只表示没有已登记的追踪发送成功。

**请求绑定。** Registry→Reservation→Executor在原父gate使用Replace原Task及out代际身份，禁止完成后查询当前代。显式追踪执行器接纳时同步交付RequestExecutionReceipt，Binding原任务在原Replace正常同步返回并移交owner后绑定真实代际；未移交路径在finally完成null，异步换代失败不丢已移交身份，原错误仍归原执行Task。成功ExecutionResult另附相同请求/代际的SendReceipt。不把接受请求、Replace完成或执行器停止当首帧成功。原执行器任务不等待FirstSend，原单飞只覆盖执行生命周期；后续Control协调仍须定义准入到回执的整体占槽与停止资格。默认CreateExecutor/Router/App不开启，不改wire/Core/认证/权限或单次Video租约。本片不是完整Control回执协议或产品live接线。

**实现使用约束与覆盖。** ReadOrigin只在await对应原读后消费，登记尝试由同一个拥有者调用CompleteSend；手动调用内部追踪读若遗弃结算将保持pending，不能靠Task代理掩盖。生产装配仍要求单个串行sender，元数据计数不是新队列或可无限预取接口。新测试同时覆盖真实sender路径和直接内部结算的幂等合同，后者不冒称真实wire发送；未做全调度交错穷举。结果TCS不是资源根，测试可在客观原根结束后断言其完成，不能用超时替代缺失结算的失败证据。

**实际验证。** 新17方法/51case，正式初始/恢复各308PASS；begin-attempt/seal-pending/successful-send/failed-attempt/request-binding五项生产变异分别21/18/7/15/3原断言红，原任务完整退出后逐字恢复。四Rebuild零警告错误，Debug/Release各6154PASS/0FAIL/0SKIP，旧6103完整身份+51/-0。独立审计exit0，409源/159原件保持，39TRX/25385唯一execution；16旧ZIP摘要保持。代码e8969f3de6453972f92aa8617322d0ec666dad17已普通推送，原收据/交付详HANDOFF§19.58。

### ADR-103 — 成功切屏回执的纯合同、占槽与逻辑提交

**2026-10-16；内部显式模式，已验证。** display_changed仅定义成功载荷：type/protocol/sessionId/seq/timestampMs/requestSequence/catalogSequence/displayId/firstFrameId/width/height。Host回执seq与请求seq独立；firstFrameId为完整uint64，尺寸1..8192，不引入额外像素预算或JSON浮点缩窄。纯codec严格字段/类型/深度/编码/4096字节预算，不触stream/clock、不声明显示成功。

**占槽与真来源。** 新CreateReplyExecutor显式保留原请求预约直到成功回执任务退出；旧CreateExecutor/CreateTrackedExecutor语义不变。TrySendReply只接纳本执行器私有槽中exact请求句柄，且原执行任务已成功、真实代际FirstSend已得到首个成功；不相信调用者可构造的Binding/SendReceipt。回执内容由原Request、真实FirstSend和外层提供的Host序号/时间构建。没有队列/自动重试，重复/伪造/旧句柄不释放别人的预约；原回执Task未完成仍Busy。

**提交、停止与边界。** 准入/父会话/lease/publication及预约身份在registry gate核对，代际在source gate确认仍active且未关闭；worker先登记，再在相同gate终检，成功终检是逻辑提交点。外部writer委托在gate外调用且完整等待其原Task，不用取消代理代替原IO。提交先赢后停止仍可能有物理迟到字节，因此不承诺Stop后绝无写入；外层须独立close/cancel并完整join。Stop先赢则不进入writer。完成后的CurrentAtCompletion只为瞬时历史，不是未来授权。完成结果与资源释放分离，原错误树独立保留；执行器Completion包含原reply/cancel/执行根，不反等source或视频sender。

**未接范围。** writer委托必须由后续唯一Control写者装配，并完整包含序列化、原IO/Flush及预算；本片未接实际Control reader/writer、超时调度、失败回执、Client显示屏障或默认产品路径，不自动装sender。未成功/失去资格由外层停止并回收，不制造成功回执。M6未整体完成，不重验M4/M5、不启M7、不改网络/权限/旧ZIP。

**实际验证。** 新505 codec+33生命周期=538case，正式初始/恢复各722PASS；六生产变异44/2/1/7/1/1原断言红并精确恢复。前两变异包装器仅因xUnit自定义消息分类失败，审计依据精确方法/场景/599或605行实际Assert调用识别，不改原件。四Rebuild零警告错误，Debug/Release各6692PASS/0FAIL/0SKIP，旧6154完整身份+538/-0。独立审计exit0，412源/171原件、40TRX/27291唯一execution，17旧ZIP摘要保持。代码d99a7e0e8c779872d17e74ff041cd86a19bf7be9已普通推送，详HANDOFF§19.59。

### ADR-104 — 显式认证后Control会话唯一写者与原预算复用

**2026-10-16；内部显式装配已验证。** §19.59之前不存在通用会话writer，只有认证栈顺序写和单次目录sender的唯一写约定。新增HostControlWriter绑定单session/借用stream，以单个原写任务持槽、Busy同步拒绝无排队。接纳时预约非负long seq；Busy/参数拒绝不消费，已接纳失败不回收，MaxValue可发一次，下一次永久封口。该序列只覆盖显式post-auth目录及display_changed，不覆盖Hello或认证握手。UTC元数据和序列化在source前锚定的原绝对预算内，seq的本地预约早于该锚。

**原预算而非新计时器方案。** HostDisplayCatalogSendAttempt保持原构造路径，新增有界payload来源和目录序列化适配入口，复用原source/Write/Flush/ControlFrameBudget原任务及timer完整释放后末检。目录原source返回后先按旧CheckStage检查，再调用写者UTC与序列化，拒绝后的迟到source不得增加UTC根。源同步前缀/AsTask未返回的未知退出、写借用未证明退出时不清零、原错误树均保持。caller注册释放属于独立完整join，不宣称其额外调度耗时已被原attempt预算硬中断。

**真实可选装配。** HostDisplayCatalogSendOptions的UseSessionWriter=true显式init选项在ControlAuthSession的success完整写出/登记/秘密finally/原holding之后装配新writer；同流目录先经它写出，成功后才发布Ready和实际重编号Catalog。原配置默认false，App不启用此模式。DisplaySwitchExecutor的窄writer重载把真实请求首发资格接到该writer，重建只改Host seq/time，其余关联字段不变。仍未增加Host request_display reader或Client display_changed消费方；Ready仅历史入口，不是持续授权，写者停止后旧引用不可写。

**停止与错误。** 原closeRequired通知Router既有close监督，writer的Abort/holding结束/hostStop先封口关线并通知父撤销，后完整join原attempt/holding/写者/caller及host取消注册，不让视频来源反等回执。ControlAuthSession保存writer字段，owner建造/启动失败仍通知停止并兜底join。纯无原故障合作停止：当次write可Canceled，writer.Completion正常；来源/IO/时钟真实OCE先进入原故障树，仍Faulted，不能以取消豁免吞错。Lifetime只在初次写入尚未返回Task且相同host token已取消时窄豁免同步准入OCE；原异步异常由writer.Completion唯一报告到AddCatalogErrors。

**界限。** 唯一性依赖显式装配独占stream，不宣称抵御程序集内任意代码直接写流；发送成功是本地预算内原IO/Flush完成，不是远端接收/显示。停止晚于逻辑提交仍可物理迟到，外层必须关线和完整join。无网络/防护/权限修改，不重验M4/M5、不启M7、不覆盖旧ZIP，尚无M6产品包。

**验证与兼容修复。** 新29 writer+8真实认证组合=37case，最终树初始v3/恢复v2各157PASS；最终六生产变异1/1/2/1/1/1原断言红恢复。首次v1构建5个xUnit1031警告触门禁、未测；首次全量3个旧Acceptance反射构造器参数不匹配，未改旧测试，恢复options唯一两参构造并以init开关选新写者。修复后重新定向/六变异/双配置全量，四Rebuild零警告错误、Debug/Release各6729PASS/0FAIL/0SKIP。独立审计exit0、旧6692完整身份+37/-0，415源/295观察原件、56TRX/34213唯一execution，18旧ZIP保持；全部旧轮及首组六变异保留为历史并核字节逆桥，不当最终证据。代码7968725b264481a08e7a84ef5f83554ac4672c85已普通推送。详HANDOFF§19.60。

### ADR-105 — 显式Host请求读取与单处理槽停止监督

**2026-10-20；内部显式接线已验证。** 保持原options两参构造，新增internal DisplayRequestHandler init，必须同时UseSessionWriter=true才可绑定。默认handler=null沿旧holding。认证成功后的原审批/holding首字节Task移交reader，不重新读取或并行启动首读；0字节是长度前缀数据、null为EOF。后续唯一reader复用ControlReadAhead及ControlFrameReadAttempt，request_display限制4096字节、首字节本地观察起10秒绝对预算，解析末检仍使用原锚点/频率/单调高水位。

**处理与关闭。** reader不能等待业务处理后才继续读，否则不合作prepare/首帧等待会阻塞EOF检测。每次完整帧通过解析后登记至多一个Task.Run处理worker，内部锁外调用并完整等待原handler Task；reader持续唯一读。第二完整请求若原handler尚未完整退出，则fail-close，不排队、不编造失败回执。EOF或读错误先触StopRequired/closeRequired及独立取消，再完整join原read/attempt/handler/cancelcallback；通知不是退出。writerLifetime在目录前后同时监听InputStopRequired，因此不会等reader完整结束才撤销父资格，避免业务等待与父join形成循环。

**派发检查点。** 非async dispatcher只在派发时检查真实父registration.CheckCurrent()、同会话writer未停止、Ready成功与实际Catalog；未Ready立即拒绝，不等结果、不缓存请求、不刷新帧预算。Ready是历史结果，不保证物理到达时刻或长期授权；业务handler仍须走具体执行器原父/代际门禁。本片未自动装配固定目标准备、切屏执行器或Client回执消费；handler返回的原Task是可信内部装配合同，不抵抗任意内部代码伪造完成。

**归属与兼容。** 非async桥返回handler原Task，read/handler/取消多根错误逐槽保留到reader.Error，再经原AddCatalogErrors唯一request-reader槽报告；不吞Faulted OCE、不Flatten或按引用去重。审批首读Task转换独立保有fallback join；首读底层原Task多故障只供显式移交消费，旧Activity保持原异常形状。原登记两个只读属性保持，父检查用CheckCurrent()方法；handler闭包由独立CreateRequestDispatch构造，避免把原RunAsync的registration局部移进闭包而破坏既有反射夹具。默认App不启用此接缝，无新产品包，不改网络/防护/权限、不重验M4/M5、不启M7。

**验证。** 新51case，最终定向196/196，四生产变异各1原Assert红恢复，双配置各6780PASS/0FAIL/0SKIP、四Rebuild零警告错误。原6729完整身份全保留；闭包布局与登记属性白名单回归保持旧测试修复，watchdog-only旧变异不计kill。独立审计exit0，418源/312原件/58TRX/34950execution，前片四成品保持。代码4bf84587d751ad3b470ad7c37d8aabeffdab7558已普通推送，详HANDOFF§19.61。

### ADR-106 — 显式每会话自动切屏协调与真实首发回执

**2026-10-20；内部自动协调装配已验证。** 在原options两参构造上新增internal DisplayRequestPrepare init，必须UseSessionWriter=true且与DisplayRequestHandler互斥；默认App不设置此选项。Auth创建唯一会话协调器，原request reader经既有Ready/父检查直接派发本次处理Task，自动完成真实admission→reply executor→prepare→Replace→真实generation.FirstSend→同一writer回执。协调器Completion是稳定会话owner，成功请求不结束owner；不得返回owner Task代替单次Task。

**来源与路由。** admission惰性创建，只有实际视频source已由Router发布时可创建；目录Ready不等于视频可切换，无source的首个请求立即fail-close，不等待/轮询/重试。工厂Router发现prepare配置自动启用原换代路径，并用tracked VideoFrameSender(_firstFrameId??0, source)，首帧成功来自真实header/payload/Flush与最后预算终检，不能凭Replace或生产者Start宣布成功。frames-only Router拒绝不兼容配置，旧factory/frames与编号路径不变。

**停止与归属。** coordinator.Handle只拥有一个当前原请求worker，内部原execution/reply完整join；FirstSend仅是结果promise，停止后不反等它，source/sender资源由原父VideoCompletion监督。任何close/Host取消/coordinator错误先封口、通知executor.Stop，Auth先撤销父登记、视频Router独立停止source，再完整join协调器/reader/writer/video，不等reader取消回调才关闭。迟到未移交候选仍归executor本地三路清理，移交后不重复Dispose。

**错误唯一报告。** Handle及owner不向reader重复传播业务错误，coordinator.Error由Auth唯一request-coordinator槽报告；execution/reply故障仅经executor.Completion收一次。新增窄TrySendOwnedReply接缝只在自动协调装配使用：已取得且Faulted的writer原Task仍完整join/封口，但诊断只交writer.Completion；同步调用失败、Canceled原Task、独立取消回调错误仍由executor持有。旧TrySendReply合同不变，不按异常引用全局去重、不Flatten。

**范围。** 内部自动装配与受控真实协议链，非默认App切屏、Client显示屏障、TLS/GDI/双机验收；未新增公共API。下一步Client唯一reader回执消费及显示屏障，之后产品live与每会话画质。不重验M4/M5、不启M7、不改网络/防护/权限/旧包。

**验证。** 新20核心+12真实自动协议组合=32case，定向初始/恢复各191PASS；四生产变异fault-owner/reply-frame-id/null-first-send/frames-router分别1/2/1/1原断言红恢复。四Rebuild零警告错误、Debug/Release各6812PASS/0FAIL/0SKIP，原6780身份+32/-0；421源/176观察原件、39TRX/27762execution，独立审计v2退出0。首次构建4警告、十个新测试Reason断言错误、审计v1误将历史测试要求冻结均保留为失败历史，未修改旧用例或伪作最终证据。代码b4055c68f7c632a525ee132283d704740dc6a150已普通推送；详HANDOFF§19.62。

### ADR-107 — Client真实切屏请求与严格回执关联先于显示屏障

**2026-10-20；内部真实sender与严格回执关联已验证，视频/UI屏障未接。** Client此前没有认证后请求发送者，必须先有真实sender，不能用fake Arm伪造请求资格。故本片先补internal EnableDisplaySwitching/RequestDisplayAsync/LatestDisplayChange，保原public面与默认App。启用必须在真实认证最终交付后、视频附着/唯一monitor启动前；请求必须有当前已交付视频、有效原monitor、实际目录及唯一目标。单请求槽从0递增、long.MaxValue仅一次，Busy不消费编号、不排队。

**发送及关联。** 复用HostDisplayCatalogSendAttempt通用payload链（类名不意味着增加Host服务）：4KiB、10秒原发送预算，UTC/序列化/Write/Flush/原timer清理在同一预算内。原Control monitor扩展严格host_status/display_changed分派，无新读者、空闲无timer；目录256KiB旧合同保持，回执codec再限4KiB，解析仍做原10秒锚点末检。回执必须绑定真实本会话pending request seq/catalog seq/display、Host seq大于目录及已收序号，无pending/重复/错关联即fail-close。每次仅一个earlyACK，不阻塞reader等发送。

**发布。** ACK及原发送成功后只生成局部候选，必须caller注册完整退出，再在父gate复核并唯一发布回执；此前不得暴露Latest。新请求接纳即隐藏旧回执。目录实例冻结，pending或已有回执后换目录（即使同样数字/载荷）fail-close；默认未启用模式仍允许原目录降序/替换。

**停止。** 父或视频子撤销在原gate立即清回执/停止信号，随后独立关闭两流，原sender及caller注册纳入CloseAndJoin/LifetimeErrors/DisposalEvidence。停止通知不是原IO退出，发送失败不伪成功；各独立异常槽保完整树。

**边界。** LatestDisplayChange仅是可撤销查询的已关联回执元数据，先前返回对象是历史，非视频/解码/UI许可。等待ACK无额外空闲timer，依赖EOF/取消或caller停止；未接视频FrameId过滤、解码队列及WPF提交，更不是真机显示证明。下一片必须贯穿这些环节，采用首帧下界语义（允许后续更大帧而不声称精确F已显示），并处理回执/视频双向乱序和旧decode迟到。未新增public API、默认App未启；不重验M4/M5、不启M7、不改网络/防护/权限/Undo或旧ZIP。

**最终验证。** 新增35case，最终v3/恢复v1定向各208/208。16:54:54–16:59:02+08:00四Rebuild零warning/error，六dotnet及worker/launcher完整wait0；Debug/Release各6847PASS/0FAIL/0SKIP（Transport5229，其它原项目数不变）。summary10471字节/SHA `6e274bff21fd02ce8c05c234bfe8240bfb9a6385c334b23e98fbe8bbb0555450`。四mutation request-sequence/write-budget/reply-correlation/operation-error分别1/1/3/1原Assert红恢复。custom Assert.False消息不含Assert，触driver assert中断；独立audit以固定源1016/原栈/原消息认证真实Assert，未为误分类改测试或伪造timeout。早期新测试编译/握手常量失败保留，不能冒充最终证据。

**审计与提交。** audit-v1因合法旧格式outer receipt无log字段触KeyError，失败保留；audit-v2仍要求日志逐行等于launcher，有log字段则核SHA。17:00:49–17:00:53+08:00原31532/外层/主线程exit0，report341948字节/SHA `1b016873c74014e2dbb13deafb7b600883b0a9c3200ea2de2b6344a7c51fcf64`；423source/175observed/39TRX/27948execution，旧6812完整四字段身份+35/-0，前片3deliverable保持。代码`842a53ffd34b0f90201565fc59e7b84e0943f98a`已push并核remote一致，6files/+1578/-8；详HANDOFF§19.63。

### ADR-108 — 内部可选视频/JPEG/WPF同父提交屏障

**2026-10-20；内部真实视频/JPEG/STA Image.Source逻辑提交屏障验证通过，默认App仍未启。** 三参internal RemoteVideoPresenter(video,image,control)绑定已启用切屏且尚无读取/请求的真实child，默认public两参/App服务不启用。Transport桥与App三个worker共享父gate：真实读取终检签发私有owner/epoch的stamp，真实请求接纳换epoch，真实发送/回执及caller注册清理完成才安装F下界；撤销同锁失效。

**真实管线。** 原VideoFrameReader完整读入后签发，专用projection仍归原child读槽完整join，不增加reader或payload owner。新模式严格ulong帧号递增；原未绑定模式不收紧。单encoded/单decoded、后台复用真实baseline JPEG预检和WPF OnLoad/Freeze，帧身份贯穿，拒绝旧epoch/倒退候选挤新。回执F是下界，允许最新帧大于F；实际解码尺寸须同时匹配帧头和回执。帧先回执后保留最新bitmap，回执自身信号能唤醒，不依赖下一帧。

**UI提交/停止。** 一次最多一个原Dispatcher操作且renderer await其原Task；callback在父gate复核当前epoch/候选、pending/F/尺寸后写Image.Source，再复检setter重入，成功才记录LastPresented。旧回调不清新槽；停止先封口/撤销，原解码/owner释放/Dispatcher仍须退出。收尾并行通知父两线关闭与取消，再join三个worker及原取消；不在gate内解码/Dispose/网络/等待。LastPresented仅逻辑提交历史，不证明物理scanout，保存旧快照不授未来资格。

**可见性/范围。** 只为实际内部接线增加Transport→App/Acceptance.Tests、App→Acceptance.Tests友元，未新增public协议API。fixture合成已认证parent并真实跑request/ACK/LRVF/JPEG/STA Image.Source，已不再仅回执元数据，但不是完整TLS/serverProof/审批/双机live。默认App仍未启，LastPresented不代表物理scanout。

**失败与中间历史。** initial-v1新测试4warning（2个CS4014、2个xUnit）触门禁未测；v2在活动WhenAny阶段错误强求直接await原Flush→request链导致watchdog，修新测试行为断言，原停止join另测，非放宽生产。v3定向35PASS与首轮frame-floor/pending-gate/receipt-size/monotonic-id变异1/1/1/2原Assert红恢复均为history，不能代替最终同源证据。final-v1 Debug通过、Release 1FAIL源于fixtureEntered先于LastFlush引用发布；仅改新fixture为FlushPublished TCS<Task>交付真实原Task引用，生产未改，然后完整重跑最终定向/四变异/恢复/双配置全量。全部失败与原件保留。

**最终验证。** initial-v4/restored-v2定向各35PASS；新增22全部Acceptance，原13默认呈现测试保持。四最终mutation frame-floor/pending-gate/receipt-size/monotonic-id分别1/1/1/2原Assert红恢复。final-v2于19:20:23–19:24:28+08:00四Rebuild零警告错误，六dotnet及worker/launcher完整wait0；Debug/Release各6869PASS/0FAIL/0SKIP（Acceptance543、Transport5229，其它项目不变）。summary10453字节/SHA `aed523d3963a82a861514d7af2420398ffcfd9f080c830e16df12e071859a41b`。

**独立审计与提交。** 19:24:59–19:25:05+08:00原audit进程60052/外层/主线程exit0；report394170字节/SHA `1c5ca69d747b29969c285797a0e116613ef7f52911d475066d84ffdbe63c7993`，426源/285观察原件/61TRX/41361execution，旧6847完整四字段身份+22/-0，前片三成品保持。代码`7757298396686201a272cb61e6f61bbb855d53bc`已普通push并核remote一致，8files/+1731/-5；详HANDOFF§19.64。307条目验证包及验收TXT/SHA三成品已交付，原件保持。

### ADR-109 — 复用原GDI核心的内部显式多屏采集

**2026-10-21；内部显式多屏GDI采集已验证，默认App仍旧。** 不暗改M5主屏行为。GdiScreenCaptureBackend新增两个internal CreateMultiDisplay工厂；原public无参及internal(native)仍primary-only，App默认未启用多屏。GdiDisplayCatalog提取只读完整DisplayObservation稳定快照，public目录返回及两遍校验合同不变，不扩大IGdiNative/PInvoke。

**准备与交付。** 每帧先完整双遍枚举，仅从附着非镜像原始索引选目标，无匹配即拒不回退。目录可含超预算屏，只目标在任何DC/DIB/租用前受8192宽高、16,777,216像素预算限制。目标DeviceName创建设备DC，BitBlt源仍设备本地0,0，负坐标仅元数据。原单飞/原Task/取消/像素复制/alpha/清理/归还链共用，所有native成功清理后再完整双遍枚举并比较采前采后所有有序Observation字段，变化拒帧且清零归还。

**边界。** 快照仅历史观察，不能保证原子性、检测ABA或跨prepare/捕获的持久硬件身份；不改DPI awareness、桌面、网络或权限。真实离屏DIB证明生产BitBlt等核心在选择后的合成源上工作，不是真实副屏/混合DPI/热插拔/双机验收。下一片再接Host prepare/写者和Client产品交互，默认App不在本片打开，M4/M5不重验、M7不启动。

**唯一旧测试同步例外。** final-v1 Debug6922PASS，Release旧HostDisplayRequestReaderTests.IdleReadAheadHasNoTimerAndStopJoinsItsOriginalRead在line277原Assert.Single因Requests为空失败：独立handler与heldEntered无happens-before，held预读绕过BeforeIdleEof，Reached不保证派发入队。仅在原断言前补`await f.Dispatched.Task.WaitAsync(Guard);`这54字节，原断言及身份保留、Transport生产不改；固定hash与完整方法上下文唯一逆变换认证该例外，不笼统声称所有旧test未变。正式Capture生产和测试真正冻结，修正后完整重跑，失败轮原样保留。首次post-snapshot变异因CS8602 warning gate未运行测试，不算kill；仅修正变异replacement为snapshot!.Count，正式使用post-snapshot-final，原告警和收据保留。

**最终验证。** 新53case（45合成native+8真实offscreen），三轮定向各231PASS。四正式mutation target-dc/post-snapshot-final/pre-stability/pixel-budget分别3/21/1/1原Assert红且精确恢复。final-v2于11:22:49–11:26:54+08:00完成四Rebuild零警告错误，六dotnet及worker/launcher完整wait0；Debug/Release各6922PASS/0FAIL/0SKIP（Capture296/Transport5229/Acceptance543，其它原同）。summary10518字节/SHA `fd1d4036b66f7963cb61c6c880ab8003f4c5681120d543dd05edb3f7742b8b71`。

**独立审计与提交。** audit-v1错误地按Gdi substring跨项目匹配，把Acceptance一项用例计入Capture定向，旧失败保留；audit-v2按原argv project/storage边界正确筛选。11:30:13–11:30:17+08:00原进程4552/外层/主线程exit0，report1177362字节/SHA `805e61ef3de63184677a5862d2300825e62cc28368dcbed7d10b7a5624dd06d6`；428源/214原件/55TRX/42151execution，旧6869完整身份+53/-0，旧测试字节差异仅上述固定hash逆变换认证的同步例外。代码`1028888b768b351384e239afd1d6f86b0a4efad5`已普通push并核remote一致，5files/+1389/-19（2production改+2新test+1旧test单同步行）；详HANDOFF§19.65。本次仅文档回填，未重新运行dotnet、未提交。

**下一片。** internal CreateMultiDisplay多屏枚举/采集已实现，不再笼统称GDI无副屏能力；默认primary API及App原主屏路径保留。下一片接App→Capture/Sessions internal friend和固定目标准备；Host每个真实认证会话装配prepare/writer，禁共享lastSessionId或临时Guid假认证身份，若接口缺session须沿内部接缝传递。Client在认证最终交付后、attach/monitor前EnableDisplaySwitching并使用三参RemoteVideoPresenter，窗口补真实请求/busy/停止与请求解绑。冻结settings，目录仍每连接单发、无动态刷新。随后Host/Client联调、每会话画质和M6真机双端验收；真实offscreen不是实际副屏/混合DPI/热插拔验收。当前无需人工前置，不重验M4/M5、不启M7、不改网络/防护/权限/Undo或旧包，无M6产品包。

### ADR-110 — 按真实认证会话装配的产品侧切屏接线

**2026-10-21；产品侧切屏已接线，默认公开行为与目录发送节奏不变。** e82ccb73/6922起。App经Capture/Sessions的InternalsVisibleTo使用多屏GDI后端与固定目标准备工厂；Host按每个真实认证会话装配prepare与同一会话写者；Client在认证最终交付后、attach/monitor前EnableDisplaySwitching并用三参RemoteVideoPresenter；窗口目录项接真实请求、忙状态与停止解绑。不改网络、防护、权限、Undo或旧ZIP，冻结预览settings，目录仍每连接单发、无动态刷新。

**真实身份必须逐会话传递。** HostDisplayCatalogSendOptions新增internal SessionDisplayRequestPrepare(Guid, DisplayInfo, CancellationToken)，与DisplayRequestHandler互斥且只能装配一次；ControlAuthContext.WithSessionDisplayRequestPrepare须先有目录来源并以UseSessionWriter=true重建选项。ControlAuthSession以登记表真实SessionId的快照值包装协调器准备闭包，禁共享lastSessionId或临时Guid冒充；闭包只持值不持登记对象——首轮直接捕获registration使编译器把该局部提升到闭包类，异步状态机不再有<registration>5__N字段，25个旧Transport反射断言失败，改为快照取值后旧测试零改动恢复。FirstFrameRouter用统一IsCoordinated覆盖两种准备工厂。

**Client与窗口。** connect返回后先EnableDisplaySwitching再AttachVideoAsync，再以三参Presenter绑定请求代际屏障；窗口StartDisplaySwitching把请求接到同一Control会话真实登记/发送/回执等待，null表示未确认/被拒/已停止。按钮XAML默认禁用，绑定后才刷新；统一门禁（已停/关闭中/会话结束/忙/未绑定/取消/目标不在当前目录快照）与分类状态文本；StopDisplayCatalog/SessionEnded同目录解绑并清stop与回调，纳入DisconnectHandlerReleased证据。回执为逻辑下界，非物理scanout或远端授权。

**受控旧测试两行例外。** 产品目录状态文本由“只读目录”改为“可请求切换屏幕（回执不等同物理输出）”，旧ProductRealViewOnlyLoopbackTests两条Assert.Contains期望随之更新；仅替换两个期望子串，身份/断言形式/目标表达式不变，完整两行块唯一逆替换逐字恢复固定OLD原hash（34204字节 / SHA `a62f618aecc0f850d4c6d94920e387c21040806a6ffbc1c98b0c613d05c21353`）。审计仅此路径规范化比较，其余测试与生产严格逐字冻结。

**变异与未采用项。** 四正式变异session-identity / busy-flag / unbind-flag / client-target各1/1/1/1原Assert红并精确恢复。HostRuntimeService多屏后端选择未设变异（本机单显示、无测试经HostRuntimeService真实切屏，改回主屏无可观测差异，该选择已在ADR-109四变异验证）；移除EnableDisplaySwitching的变异未采用（BindDisplayPresentation先抛InvalidOperationException，属Guard非原Assert，时序由回环Fact A全链覆盖）。

**失败与中间历史。** 首轮final-v1暴露25个Transport状态机字段失败（上述闭包捕获问题，已修生产）。修复后两轮final-v1另有旧DisplayPresentationTests.EarlyAck与HostDisplayRequestReaderTests.StopBefore…在整仓并行负载下抖动；单独、Acceptance整项目及排除新增测试后的整仓复跑均全绿，本片未改其逻辑；两轮与被修复final不同源且审计history仅支持构建门禁形态，故删除后整体重跑并在此显式记录。audit-v1因旧测试逆变换针脚漏写16空格缩进失败，原报告保留；audit-v2通过。

**最终验证。** 新18项（回环2+窗口16），首轮及两次恢复定向各45PASS。final-v1于17:11:59–17:16:30+08:00四Rebuild零警告错误，六dotnet及worker/launcher完整wait0；Debug/Release各6940PASS/0FAIL/0SKIP（Acceptance561、Transport5229，Capture296/Sessions285/Core197/Security66/Protocol303/Integration3）。旧6922完整四字段身份+18/-0。summary10626字节/SHA `1c6094be1365b3c4982edc5be65420ac08a20c6d79ab739589034a62648aff63`。

**独立审计与提交。** 17:18:28–17:18:33+08:00原审计进程59376/外层/主线程exit0；report.json 1001213字节/SHA `3b8cf265d0db882ea973c59583c0107f87ba90ed63ccd12f50c0141832026f4b`，430源/168观察原件/39TRX/27863execution，前片三成品摘要保持。代码`93d72e2027a257157628b31633b3cebc88971038`已本地提交，13files/+227/-28（9生产改+2新test+1旧test两行受控更新）；`git push`三次均schannel TLS握手失败，未核远端一致，须在可联网环境补推；详HANDOFF§19.66。

**下一片。** 产品侧仅完成进程内接线：真实TLS回环、协调器装配与窗口交互已贯通，但不等于双机TLS联调、物理scanout或真人验收；回执仍是逻辑下界。下一步真实Host/Client产品联调、每会话画质与M6真机双端验收，当前无需人工前置。无M6产品包；不重验M4/M5、不启M7、不改网络/防护/权限/Undo或旧ZIP。

### ADR-111 — 每会话画质底座（Sessions层）

**2026-10-22；画质仍默认主机级，但底座已可按会话指定。** 93d72e2/6940起。M6 DoD要求“每个Session画质独立”，此前画质只在工厂构造时给定、运行中禁止更换，所有会话共用同一设置。本片只补Sessions底座：新增两个internal按会话画质入口，画质只作用于本会话管线、不改工厂默认、不影响其他会话；不改协议、不改App/Host默认行为。

**入口形态。** 不为既有CreateAsync/CreateForDisplayAsync增加重载——重载会让旧测试的反射形态断言出现AmbiguousMatchException，旧测试不可改；改为独立命名CreateWithSessionQualityAsync与CreateForDisplayWithSessionQualityAsync，形态契约与既有反射断言都不受影响。该缺陷轮不保留，改名后完整重跑定向/变异/恢复/双配置全量。

**校验前置。** 画质在任何委托、显示器枚举与像素预算之前校验（旧路径是先建编码器再在管线构造里校验、靠catch清理）：非法档位同步拒绝，不创建采集后端或编码器。空会话标识先于画质，空画质按参数空值拒绝。

**验证。** 新增11项，三轮定向各296PASS；四变异quality-ignored / quality-late-check / display-ignored / null-quality各1 / 5 / 1 / 1原Assert红恢复。final-v1于10:11:29–10:15:50+08:00四Rebuild零警告错误、六dotnet/worker/launcher完整wait0，Debug/Release各6951PASS/0FAIL/0SKIP（Sessions296、Acceptance561、Transport5229等）。旧6940身份+11/-0；本片未改任何旧测试，无同步修正桥。audit-v3 10:18:34–10:18:39+08:00原42944/外层/主线程exit0，report906345字节/SHA `a4f2cd1d8199cbcf80c8dba94c89f1a84baba7693541878639b088026856ae0c`，431源/165原件/39TRX/28678execution。代码`32420e4dce4f1391b545f7198a51af065996a8ee`，2files/+262/-6；详HANDOFF§19.67。

**下一片。** 把Host当前画质快照到每个认证会话（创建时固定、运行中互不影响），再接scale与quality presets、ping RTT、actual FPS与auto quality简单控制器，然后真实Host/Client产品联调与M6真机双端验收。当前无需人工前置；不重验M4/M5、不启M7、不改网络/防护/权限/Undo或旧ZIP，无M6产品包。

### ADR-112 — 按真实认证会话分配画质

**2026-10-22；画质已可按会话分配，但改档来源仍只有主机档位。** 32420e4/6951起。Transport只按sessionId调CreateAsync且不带画质，所以按会话分配必须由工厂侧解析回调完成，不能靠共享字段或启动参数局部冒充会话身份。工厂新增可选sessionQuality回调：非空即用、为空回退工厂默认，且在任何依赖创建之前校验；App新增SessionQualityResolver做首次快照与主机停止清理。

**快照而非现读。** 会话首次解析时快照主机当前档位，之后主机换档不影响该会话；Set优先级更高且拒绝非法/空输入；主机未指定档位时返回null、不落表；CompleteDrainAsync时整体清空。回调只读ActivePreviewSettings属性、不捕获启动局部——避免上片闭包提升破坏旧测试反射的同类问题。

**验证。** 新增10项（回调4+解析器6），三轮定向各35PASS；四变异callback-ignored / callback-late-check / resolver-no-snapshot / resolver-set-no-validate各1 / 2 / 1 / 1原Assert红恢复。final-v1于10:49:39–10:53:49+08:00四Rebuild零警告错误、六dotnet/worker/launcher完整wait0，Debug/Release各6961PASS/0FAIL/0SKIP（Acceptance571、Sessions296、Transport5229等）。旧6951身份+10/-0；本片未改任何旧测试，无同步修正桥。audit-v1 10:54:46–10:54:50+08:00原43708/外层/主线程exit0，report909769字节/SHA `849184bad9a6075481c0686324e9fd37adf1c052e79524ce366fac5006adab3e`，433源/166原件/39TRX/27934execution。代码`e250ca2c26872c89c0002d79cdb30518fa02a38d`，4files/+286/-4；详HANDOFF§19.68。

**下一片。** 接scale与quality presets、ping RTT、actual FPS与auto quality简单控制器（届时才有按会话改档的真实来源），再真实Host/Client产品联调与M6真机双端验收。当前无需人工前置；不重验M4/M5、不启M7、不改网络/防护/权限/Undo或旧ZIP，无M6产品包。

### ADR-113 — 实际帧率指标用 Stopwatch，不用注入时钟

**2026-10-23；只暴露指标，尚未接自动画质控制器。** e250ca2/6961起。M6清单把actual FPS列为auto quality的输入，但FramePipeline此前没有任何实际帧数/帧率出口。约束是既有7项测试断言构造与创建阶段`TimestampCalls==0`，且注入时钟失败场景须保留原异常，所以在构造或`Start()`里新增`TimeProvider.GetTimestamp()`会直接破坏旧测试。结论：计数与起点都用`Stopwatch`——`_startedTicks`只在真实`Start()`取一次，`EncodedFrames`只对真正进入编码队列的帧自增，`ActualFps`=帧数/经过秒数，未启动或零帧为0，读取不清空历史。

**断言口径。** `EncodedFrames`精确断言；`ActualFps`只写弱断言（0帧为0、有帧后>0、重复读取不清空），不把不可确定的比值写成精确期望。另有一条两条管线各自计数的用例，确认指标按管线独立而非静态共享。指标不等于目标帧率、显示帧率或网络发送帧率。

**验证。** 新增4项，三轮定向各4PASS；五变异counter-not-incremented / fps-always-zero / start-tick-not-recorded / fps-resets-history / producer-passthrough-zero各1个原Assert红并精确恢复。final-v1于13:59:07–14:03:30+08:00四Rebuild零警告错误、六dotnet/worker/launcher完整wait0，Debug/Release各6965PASS/0FAIL/0SKIP（Sessions300、Acceptance571、Transport5229等）。旧6961身份+4/-0；未改任何旧测试，无同步修正桥。audit-v1 14:03:55–14:04:00+08:00原46908/外层/主线程exit0，report994301字节/SHA `ecd067530ce4d7a04415e169e022ae5b0c431d3e78ce02608d5fdb5b2152cc65`，434源/180原件/40TRX/27869execution。代码`dcd5eea58fb6cde4390f1dc3c0735f5ad4f70db8`，3files/+205/-0；详HANDOFF§19.69。

**两处计量教训。** ①变异若让私有字段只剩`Interlocked.Read`引用，编译器会报CS0649并被零警告门禁拦下——告警门禁不是测试红，不能算kill，变异须保持字段被赋值。②xunit v3的`Assert.True(cond,message)`在TRX只上报自定义消息，原`"Assert." in message`判定会误拒真kill；改由「消息含Assert.或堆栈落在新增测试类」双重认证。

**下一片。** 接scale与quality presets、ping RTT，再让auto quality控制器消费本指标（届时才有按会话降档的真实依据），随后真实Host/Client产品联调与M6真机双端验收。当前无需人工前置；不重验M4/M5、不启M7、不改网/防护/权限/Undo/旧ZIP，无M6产品包。

### ADR-114 — 命名画质预设：严格解析，不静默回落

**2026-10-23；只补「预设名→档位」这一层，界面与控制器未接。** dcd5eea/6965起。M6清单的 `scale` 已真正落地（编码器按 `settings.Scale` 缩放）、`actual FPS` 上一片已完成；`quality presets` 此前只有 VideoQualitySettings 上三个零散静态属性，没有按名选档的入口。新增 App 层 internal `VideoQualityPresets`，并把 `SessionQualityResolver` 扩出 `SetPreset(Guid, string)` 复用既有 `Set`。

**严格而非宽松。** 预设解析与 `VideoQualitySettings.Clamp` 是两套语义：Clamp 用于把任意输入钳制回合法范围，保持原行为；预设解析则要求未知名、空、空白、大小写不符、前后带空白一律拒绝，**绝不静默回落到其他预设**——静默回落会让「我要高画质」变成「拿到均衡」，属于无声降级。名称按 `StringComparison.Ordinal` 精确匹配；`Resolve` 返回前仍走 `FramePipeline.ValidateSettings`。

**单一事实来源。** 档位数值直接引用 `VideoQualitySettings.LowLatency / Balanced / HighQuality`，预设表不重复定义数值；`NameOf` 反查预设名，不属于任何预设的手工档位返回 null，供界面显示与控制器判断「当前是否就是某档」。

**验证。** 新增19项，三轮定向各19PASS；五变异 preset-name-mismatch / resolve-fallback-unknown / try-resolve-always-true / nameof-never-null / set-preset-fallback 分别1 / 7 / 1 / 3 / 1个原Assert红并精确恢复。final-v1于17:04:38–17:08:53+08:00四Rebuild零警告错误、六dotnet/worker/launcher完整wait0，Debug/Release各6984PASS/0FAIL/0SKIP（Acceptance590、Sessions300、Transport5229等）。旧6965身份+19/-0；未改任何旧测试，无同步修正桥。audit-v1 17:09:18–17:09:23+08:00原20792/外层/主线程exit0，report1025903字节/SHA `ffc87cd91faf3211fa2821bbe9fcdfc79df62960765cd1293f8573ee7abf2497`，436源/180原件/40TRX/27968execution。代码`ebd5e3d71f7104f73e6b47060cb38b90a9099052`，3files/+219/-0；详HANDOFF§19.70。首轮全量曾出现既有 HostDisplayRequestReaderTests.StopBefore… 的整仓并行抖动（§19.66同款），单独复跑全绿、本片未触及Transport，缺陷轮删除后整体重跑。

**下一片。** ping RTT，然后 auto quality 简单控制器消费 RTT 与 EncodedFrames/ActualFps，通过本片预设名执行降档（届时才有真实改档来源），再真实 Host/Client 产品联调与 M6 真机双端验收。当前无需人工前置；不重验M4/M5、不启M7、不改网/防护/权限/Undo/旧ZIP，无M6产品包。

### ADR-115 — ping/pong 与往返时延计量：有界、对端数据不抛异常

**2026-10-24；只有合同与计量内核，尚未接定时 ping loop。** ebd5e3d/6984起。规格 04 章第 11 节要求 Control 消息至少含 `ping`/`pong`，而代码里此前完全没有。本片新增 PingFrame、PongFrame 与 PingRttMeter 三个 internal 类型；不接定时发送、不收发字节、不改既有认证或视频路径，因此**协议上还没有真正发出过一个 ping**。

**严格形态。** 沿用既有 Control 帧合同：4 KiB 预算、固定字段集、重复字段与非整数拒绝、非法 UTF-8 与残缺 JSON 拒绝、sessionId 必须等于预期会话；ping 与 pong 类型互不相认。pong 的 `seq` 与 `echoedSeq` 是两回事：前者是本消息序号，后者才用于结算往返时延，这一区分由专用变异守护。

**计量四条硬约束。** ①待确认样本有界（MaxPending = 32），超出拒绝登记，绝不无界增长；②重复或未知回显不结算、不改写最近值；③回显序号属于对端数据，负数或异常值只返回 false，**不让本进程抛异常**；④`Prune` 清理超期未确认，对端不回时不堆积。无样本时 `LatestRtt` 为 null，不虚构 0。时钟可注入，便于确定性测量。

**验证。** 新增23项，三轮定向各23PASS；五变异 pong-echo-ignored / meter-settle-any / meter-duplicate-settle / meter-unbounded / meter-no-prune 各1个原Assert红并精确恢复。final-v1于10:21:34–10:26:23+08:00四Rebuild零警告错误、六dotnet/worker/launcher完整wait0，Debug/Release各7007PASS/0FAIL/0SKIP（Transport5252、Acceptance590、Sessions300等）。旧6984身份+23/-0；未改任何旧测试，无同步修正桥。audit-v1 10:26:38–10:26:43+08:00原52524/外层/主线程exit0，report1034539字节/SHA `f0b228485e7567b7c7c036a0722ff886697c7d0c8e146a5c0cd5d4ed783c5a44`，441源/180原件/40TRX/28056execution。代码`0ffc111256a80edd9bbbdb350e41937e0eff94e1`，5files/+714/-0；详HANDOFF§19.71。

**下一片。** auto quality 简单控制器：消费本片 RTT 与 §19.69 的实际帧率，通过 §19.70 的预设名执行降档（同时把 ping 真正接到会话读写路径），再真实 Host/Client 产品联调与 M6 真机双端验收。当前无需人工前置；不重验M4/M5、不启M7、不改网/防护/权限/Undo/旧ZIP，无M6产品包。
