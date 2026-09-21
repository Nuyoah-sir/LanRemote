# M3 红队评审分流（外部模型 → 本机裁定）

**来源**：把 `docs/M3_EXTERNAL_REVIEW_PROMPT.md` 的 Prompt A 转给外部模型后返回的评审（A/B/C 三桶）。
**本文件的作用**：把评审结论按项目纪律**降级/升级**——只有本机实测或规格原文支持的才成为 M3 的约束，
`[RECOLLECTION]` 一律不得写进 HANDOFF 的事实陈述。
**状态**：仅记录设计与约束，**未启动编码**。M3 开工指令待用户下达。

---

## 1. 实测校正表（本机 .NET 10.0.12 / Windows 11 25H2 build 26200）

评审里有几条自称 `[VERIFIED KNOWLEDGE]` 的 .NET 默认值。**我全部实测了一遍**（临时 console 程序，
不在仓库内），结果如下：

| 评审断言 | 实测结果 | 裁定 |
|---|---|---|
| `AllowDuplicateProperties` 默认 `true` | **`True`**；且实测 `{"type":"channel_hello","type":"video"}` 默认解析成功、`type` 取**后一个**值 `"video"` | ✅ **证实，且比评审更严重**：不是"产生歧义"，是**后者覆盖前者**，可被用来夹带第二语义 |
| `AllowDuplicateProperties=false` 会拒绝 | 抛 `JsonException: Duplicate property 'type' encountered...` | ✅ 证实 |
| `UnmappedMemberHandling` 需显式设 `Disallow` | 默认 `Skip`；设 `Disallow` 后未知成员抛 `JsonException` | ✅ 证实 |
| 尾逗号 / 注释默认拒绝 | 两者默认均抛 `JsonException` | ✅ 证实（无需额外配置，但**必须显式写出来**，别靠默认） |
| `MaxDepth` 默认 64 | 属性值实测 **`0`**（0 表示采用内置上限 64） | ⚠️ **表述需修正**：属性值是 `0`，有效上限是 64。写文档时别写成「默认值为 64」 |
| 客户端 `AllowRenegotiation` 默认 `true` | **`True`** | ✅ 证实 |
| 服务端 `AllowRenegotiation` 默认 `false` | **`False`** | ✅ 证实 |
| `AllowTlsResume` 默认 `true` | 客户端 **`True`**、服务端 **`True`** | ✅ 证实（评审只点了客户端，实测**两端都是 True**，两端都要显式关） |
| `EnabledSslProtocols` 默认值 | **`None`** → 表示交给系统默认，不是"禁用" | ➕ **评审未提**：M3 必须显式传 `Tls12 \| Tls13`，不能依赖 `None` 的系统默认 |
| `PropertyNameCaseInsensitive` | 默认 `False` | ➕ 附加证据：我第一次的经验测试因大小写不匹配而"静默没映射上属性"，说明**大小写敏感是默认值，写 JSON 契约时字段名必须逐字对齐** |

> 教训保留：我第一版经验测试因为属性名大小写不匹配得出错误结论（看着像"没拒绝"），
> 改对属性名后才测出真实行为。**经验测试本身也要自检**，否则会得到假的"实测"。

---

## 2. A 桶 —— 采纳为 M3 设计约束

评审 A 桶 18 条我**全部采纳**，以下是落地形式与我的调整。

| # | 约束 | 我的落地 / 调整 |
|---|---|---|
| 1 | TLS 成功 = 显式 `PreAuthenticated`，不是"已连接/已认证" | 采纳。规格 DoD 已写「没有 auth 前不能进入 Session」，**M3 必须把它做成代码状态**；测试：hello 之后除「开始 M4 认证」外的每个应用动作都必须被拒 |
| 2 | Connect 点击时冻结不可变快照 `{deviceId, remoteIPv4, tcpPort, expectedCertSha256}` | 采纳。握手期间不许回读 discovery 缓存 |
| 3 | 连之前把期望指纹解码成**恰好 32 字节**并冻结；比较用 `FixedTimeEquals` 比字节 | 采纳。畸形/31/33 字节的 pin 必须在 `ConnectAsync` 之前就失败 |
| 4 | 同 deviceId 不同指纹不得静默 last-write-wins | **采纳但改归属**：见下 §4 与 ADR-027。M3 不改 M2 已验收的缓存语义，只靠第 2 条的快照止血 |
| 5 | M3 必须暴露不可变 `PresentedCertificateSha256`；M4 的 transcript 必须绑定它 | **采纳，这是本次评审最有价值的一条**（我原设计没写死这个接口）。见 ADR-028 |
| 6 | 同子网校验在**裸 socket accept 之后、TLS 之前**做，用接受它的那个 listener binding | 采纳。`LocalEndPoint.Address` 必须等于该 binding 地址，用该 binding 的掩码比 `RemoteEndPoint.Address` |
| 7 | 准入限额在 accept 之后、**握手之前**占用（全局 + 每源 IP），`finally` 里确定释放 | 采纳。backlog 只是待 accept 队列，不是应用层准入策略 |
| 8 | 用**阶段绝对 deadline**，不是"距上次读到字节 N 秒" | 采纳。TCP connect / 握手 / 4 字节头 / 整帧 payload / 首个 hello 各自独立 deadline；异步读不靠 `ReadTimeout` |
| 9 | 未认证的 `channel_hello` 用**远小于 1 MiB** 的独立上限（几 KiB） | 采纳，且这是对规格的新增约束：规格只说 control 最大 1 MiB；**M3 的 pre-auth 阶段另设小上限** |
| 10 | 长度先按 `uint` 校验再转 `int`，超限不 drain 直接关 | 采纳。测试长度：`0 / 1 / 上限 / 上限+1 / 0x7fffffff / 0x80000000 / 0xffffffff` |
| 11 | `channel_hello` 解析器必须显式严格 | 采纳且**实测支持**：`AllowDuplicateProperties=false`、显式 `MaxDepth`、`PropertyNameCaseInsensitive=false`、`UnmappedMemberHandling=Disallow`，注释与尾逗号拒绝 |
| 12 | 首帧必须是且仅是 `{type:"channel_hello", protocol:1, channel:"control"}` | 采纳。其它 channel / 其它 protocol / 缺字段 / 重复字段 / 第二个 hello 一律断开 |
| 13 | M3 单独成里程碑时必须有明确终态 | 采纳：hello 成功后**干净关闭**，或进入 `AwaitingAuthentication` 并带一个短绝对占位 deadline；M4 落地后由真实认证替换 |
| 14 | 精确 leaf pin 下，链信任与主机名不是独立身份证明；profile 检查是**不变量**不是解药 | 采纳。不变量集合：leaf 存在 / 32 字节 pin 精确匹配 / ECDSA P-256 / non-CA / KU 含 digitalSignature / EKU 含 serverAuth / 有效期当前有效 |
| 15 | 有效期必须检查（否则 5 年寿命在客户端侧形同虚设） | 采纳。测试**注入时间**，不改系统时钟 |
| 16 | 显式 `AllowTlsResume=false`（两端） | 采纳，且实测证实**两端默认都是 True** |
| 17 | 显式 `AllowRenegotiation=false`（两端） | 采纳，实测客户端默认 True、服务端默认 False → 两端都显式设 |
| 18 | 连接任务有界、可观测、停机 join | 采纳：有界连接登记表 + Host stop 时取消 + 有限停机 deadline |

---

## 3. B 桶 —— 做成实验 / 测试（**未运行**，不得写成结论）

| # | 内容 | 处理 |
|---|---|---|
| 19 | `EphemeralKeySet` ECDSA P-256 作 SChannel 服务端证书 | **ADR-018 的未关闭风险**，M3 第一条要做的实测；强制 TLS1.2 与强制 TLS1.3 各跑一遍，记录 OS build / runtime / 协商协议 / 异常原文 |
| 20 | TLS 1.2 与 1.3 分别验证，不能从枚举存在推断可用 | 本机只能验 **Win11 25H2 / 26200**；**Win10 22H2 本机无法验证**，需另找机器或标注未测 |
| 21 | 若将来重开 TLS 恢复，必须先证明回调在恢复会话上的行为 | 在证明之前保持 `AllowTlsResume=false`（已由第 16 条定死） |
| 22 | 多张合格网卡、RFC1918 子网重叠时的行为 | 记下来，M3 遇到再测；不猜 |
| 23 | 端口 45873 在多张合格本地地址上分别 bind | 同上；要决定 Host 启动是原子的还是按网卡降级 |
| 24 | 停滞握手 / 停滞 `ReadAsync` 的取消延迟 | 测得数值后再写进 HANDOFF，不预填 |
| 25 | deadline 包装不会把取消误报成通用网络错误、也不漏还限额 | 五个阶段分别取消（connect / 握手 / 2 字节 / 4 字节 / 部分 payload / 空闲 pre-auth），断言限额归零 |

## 4. C 桶 —— **不得写进 HANDOFF 的事实陈述**

- 26 SChannel 错误信息可能误导（**RECOLLECTION**）→ 只作为工作方式：每次失败记录异常类型/消息/inner，单变量 spike 归因，**不把错误文本解释写进设计**。
- 27 Windows 凭据/会话缓存可能产生意外行为（**RECOLLECTION**）→ 不作为设计依据；
  以「显式关闭恢复 + 无客户端证书 + 重启进程复现」作为 oracle。

---

## 5. 我与评审的分歧（只有一处，且是归属问题）

**第 4 条（身份冲突）评审建议就在 M3 做**：TTL 内同时看到同 deviceId 不同指纹 → 标记 `IdentityConflict` 并禁用连接。

我的判断是**方向对、时机不对**：

- 事实层面我复核过：`DiscoveryDeviceCache` 是 `Dictionary<Guid, DiscoveredDevice>`、`Upsert` 即 last-write-wins，
  同 deviceId 换 IP/换指纹确实**静默覆盖**（读代码所得，非推测）。
- 但改它就是改 **M2 已通过两机验收的行为**，需要单独立 ADR + 重跑验收，不该由 M3 顺手做。
- M3 阶段攻击者的收益边界很清楚：能诱导客户端连到攻击者证书，**但过不了 M4 的 access key proof**。
  所以 M3 用「不可变快照」止血即可，危害是"连错对象"而非"拿到授权"。
- 另外必须保留良性场景：**同 deviceId + 同指纹 + 不同 IP = 多网卡正常广播**，不能误杀。

→ 已立 **ADR-027**：现在就把"不得静默 last-write-wins"这条**写死**，
实施放在 **M4 之前**（最晚 M4），M3 不动缓存语义。

## 6. 评审额外纠正我的一点

评审指出原断言漏了：**若 M4 的 HMAC transcript 不绑定 M3 实际出示的证书指纹，
活跃攻击者可以在一条 TLS 连接上收下 nonce、在另一条连接上中继给真服务器**（凭据中继）。
我原以为规格里 M4 的「modified cert fingerprint fail」已覆盖，
但那测的是**篡改**场景，不等于 transcript **包含并校验** presented fingerprint。
→ 已立 **ADR-028** 把这个 M3→M4 接口写死。

---

## 7. M3 开工前的最小清单（照此收口）

1. 跑实测 19（EphemeralKeySet + ECDSA P-256 服务端，TLS1.2 / TLS1.3 各一遍）→ 收口 ADR-018
2. 跑实测 20（本机 Win11 25H2；Win10 22H2 标注未测）
3. 连接上下文类型落地：`{deviceId, endpoint, expectedPin(32B), presentedPin(32B)}` 不可变
4. `PreAuthenticated` 状态 + 除 hello 外全拒
5. accept → 同子网校验 → 准入限额 → TLS（顺序不可换）
6. 阶段绝对 deadline + 有界连接表 + 停机 join
7. 严格 hello 解析器（`AllowDuplicateProperties=false` 等，见 §1）
8. 显式 `AllowTlsResume=false` / `AllowRenegotiation=false` / `EnabledSslProtocols=Tls12|Tls13`
9. pre-auth hello 独立小上限（远小于 1 MiB）
10. M3 终态：hello 后干净关闭或带短占位 deadline 的 `AwaitingAuthentication`
