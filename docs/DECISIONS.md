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
**Consequence**：M5 video attach 校验复用 `SessionRegistry.TryGetSessionToken`（internal）；
`ControlAuthOptions` 是**测试缩放形态**（全部时限可调）——产品默认值单点定义在
`AuthProtocol`，不得被测试缩放误导；阶段 4 客户端的 serverProof 独立重算与高层连接所有权见 ADR-043。
短码可计算不等于批准前双端展示已接线；该过程 API 尚未定案，步骤 19 的 Transport 文案与阶段 5 展示接线边界见 ADR-043。
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
   **验收器展示接线仍在阶段 5**，本 ADR 不声明验收器 UI、产品 WPF UI 或阶段 5 收口完成。
   ShortCode 当前只随成功会话公开；「两端都能算」不等于「批准前双端已展示」。
   **批准前双端短码展示需要新的认证过程 API，形态当前未定**，不能靠提前泄露连接/token 或假称现有返回值已经支持。

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
