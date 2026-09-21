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

用户原话：「**不应该要求用户安装了我们的软件还去 PowerShell 输指令；要管理员权限的话，叫用户使用管理员模式（或者打开 UAC 索取管理员权限）不就好啦？**」
并选中「**三个都做（含改 IP 一键）**」。以下三条把这句话落成可施工、可验收的约束。
**这三条只定规则与验收口径，当前不启动编码。**

### ADR-024 — 网络诊断必须进 UI，且必须「指名原因 + 指名网卡 + 给实际地址」
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

### ADR-025 — 防火墙放行内置到 App（UAC 提权），`configure-firewall.ps1` 降级为可选入口
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

### ADR-026 — 临时私有地址一键：显式、确认、可撤销；**禁止静默自动改 IP**
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
**实施时机**：**M4**（最晚），**不在 M3**。

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
