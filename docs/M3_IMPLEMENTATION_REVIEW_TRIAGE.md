# M3 实现评审分流（第二轮外部评审 → 本机裁定）

**来源**：把 `docs/M3_IMPLEMENTATION_REVIEW_PROMPT.md`（Prompt A + B）发给外部模型后返回的评审
（A. 必修 12 条 / B. 待测 10 条 / C. 提示 3 条 / D. 数值与分类学 / Prompt B 两决策）。
**本文件的作用**：按项目纪律**降级/升级**——只有本机实测或代码事实支持的才成为约束；
模型对 `.NET/Windows` 行为的断言一律**待实测**，不得直写 HANDOFF。

**日期**：2026-09-21（当日回收、当日分流）
**一句话结论**：评审有**两处缺陷级发现**（① transcript 拆分；② pre-auth 缺外层信封），
均已采纳；另有一处流程级论证（审批面进验收器）推翻本机此前倾向，待用户最终拍板。
**但评审的 3 处前提与本仓代码事实不符（§0，其中一处的源头在我写的 prompt），涉及它们的结论已按事实降级/改写。**

---

## 0. 先核对评审前提——3+2 处「评审所依据的模型」≠ 本仓代码事实

评审看不到源码（外发材料只有描述），它的 `[VERIFIED KNOWLEDGE]` 标签含义是「按其推理链不需要额外验证」，
**不是**「已对本仓核实」。逐条核对结果：

| # | 评审前提 | 本仓事实 | 证据 |
| --- | --- | --- | --- |
| P1 | 「prefix 5s / payload 10s / hello 5s 三段顺序执行 ⇒ 攻击者可占 ~20s」 | **`HelloTimeout` 服务端从未接线**：`ControlPreAuthSession.RunAsync` 只消费 `LengthPrefixTimeout` + `PayloadTimeout`；`HelloTimeout` 全仓仅两处消费——验收器**客户端写 hello** 的写超时（`ClientRole.cs:346`）与日志行（`AcceptanceRun.cs:164`）。服务端 post-TLS 最坏 = 5+10 = **15s**（不是 20s；也没有第三个顺序段） | `ControlPreAuthSession.cs:121-125`；全仓 grep `HelloTimeout` |
| P1b | （同上，附带）评审的 20s 数字 | 碰巧≈「整条准入持有期」最坏值：TLS 5s + 前缀 5s + payload 10s ≈ 20s。但推导过程不成立 | `TransportHost.cs:283-289`（握手时限）+ 准入租约从 accept 后持有至会话收尾 |
| P2 | 「部分 bind 失败没有稳定报告，需要新增 `BoundEndpoints/FailedEndpoints`」 | **报告已存在**：`TransportHostStartResult(BoundAddresses, Failures)` + `IsListening`，且有降级测试（ADR-031） | `TransportHostStartResult.cs:28-34`；`TransportHostTests.Start_Degrades_Per_Address_Instead_Of_Failing_Whole_Host` |
| P3 | 「停机放弃任务后可能返回与干净成功无法区分的状态」 | **机制存在**：`StopAllAsync` 返回 `bool`（未全部完成 = `false`），`TransportHost.StopAsync` 原样上抛。缺的是：false 路径**没有测试**、没有「未完成计数」 | `ConnectionRegistry.cs:124`；`ConnectionRegistryTests`（无 false 路径用例） |
| P4 | 「transcript 同时绑 requested 与 granted，自相矛盾」 | 规格只绑 **requestedPermission**；`grantedPermission` 在规格里**不被任何 proof 绑定**（是缺口，不是矛盾——矛盾出在我 prompt 的含混措辞） | 规格 `04_PROTOCOL_AND_SECURITY.md:208-217`（transcript 字段）、`:256`（serverProof）、`:264`（granted 在响应里、无绑定） |
| P5 | 「（#15）全局/每 IP 限额可能没跨 per-NIC listener 共享」 | **设计上就是共享的**：`TransportHost` 构造时建**一个** `ConnectionAdmissionLimiter`，所有 accept 循环共用。缺的只是跨 listener 的测试 | `TransportHost.cs:77-80、266` |

> **元教训**：P1 的源头是我——把「已声明但未接线」的 `HelloTimeout` 当成「五段已实现」写进了评审材料，
> 评审据此做了正确的加法推演，得出错误的 20s。**材料里每一条「已建成」都必须能指到代码/测试证据**
> （已记为 HANDOFF §17 教训 #24）。

---

## 1. 采纳（有明确落点）

### 1.1 【缺陷级 · M4 设计修订】评审 A5：`clientProof` / `serverProof` 拆两个 transcript ✅ 采纳

评审结论成立：**`clientProof` 不可能绑定 `grantedPermission`——客户端计算时它还不存在。**
规格的实情是「transcript 只含 `requestedPermission`，`granted` 不被任何 proof 绑定」，
评审提出的拆分是对规格的**改进**（两端皆我方、无兼容包袱，落 ADR-027 修订）：

```text
ClientAuthTranscript = 版本串 + sessionId + 双方 deviceId + 双方 nonce
                     + certSha256(本连接实际出示) + requestedPermission
   clientProof = HMAC-SHA256(key, ClientAuthTranscript)

ServerGrantTranscript = 域分隔符 + H(ClientAuthTranscript) + grantedPermission
   serverProof = HMAC-SHA256(key, "server\0" || ServerGrantTranscript)
```

- 字段顺序/编码沿用规格 §9 建议（`\0` 分隔、base64 canonical、uppercase hex）+ ADR-028（绑 **presentedPin**）。
- **顺带记一个规格内部冲突**：`MASTER_PROMPT*` 写 `"server|"`，`04_PROTOCOL_AND_SECURITY.md` 写 `"server\0"`——
  以 04 为准，M4 阶段 0 在 ADR 里记明。
- 采纳评审的 falsification 测试：请求 control、批 view → client proof 仍有效、server proof 只对 view 验得过、
  把 control 换进最终响应必破。

### 1.2 【缺陷级 · 立即加固】评审 A1+A2 合并：pre-auth 外层信封 ✅ 采纳（M3.1）

A1 的**方向**成立（P1 修正数字后依然成立）：分段时限各自绝对 **≠** 总量有界——顺序执行即可加和
（5+10=15s），8 个槽位循环占用依旧可行。A2 指出 hello 语义要定清（我们的事实是：服务端压根没接）。

**落法**（对评审方案①的精神的落实 + 消歧）：

- 新增 `PreAuthEnvelopeTimeout`（初值 **8s**，评审建议值，标注 provisional）：**自进入 pre-auth 起算 → 终局**，
  覆盖前缀 + payload + 解析 + 收尾，**永不重置、不随子阶段重置**；
- `HelloTimeout` 语义收拾：文档改为「验收器客户端写 hello 的预算」，不再暗示服务端存在独立顺序段；
- 分段时限保留为内层（信封是硬上限，分段是精细归因）；
- 测试（缩放值）：prefix 500ms / payload 1000ms / 信封 800ms——吃满前缀后拖 payload，
  必须 ~800ms 被切（而不是 ~1500ms），且 `rejection=pre-auth-timeout`、名额/登记表归零。

### 1.3 评审 A3：启动降级——部分采纳 ✅

- 「需要稳定报告」= **已存在**（§0 P2）。
- 采纳部分：① 三态语义（`None/Degraded/Ready`）作为**呈现层**计算属性补上（低成本）；
  ② **产品集成要求**：发现不得播报没有控制监听的端点（当前组件零接线，写成明确要求，
  M9/M10 接线时落实）；③ 采纳其测试口径（注入一个 bind 失败 → 结果必须含**那个**端点且不得呈现「完全就绪」）。
- **不采纳**「zero bind → 抛异常」：ADR-031 明确「库不抛、调用方呈现」，现状正确。

### 1.4 评审 A4：空能力集合不是授权边界 ✅ 采纳（M4 门禁形式）

属实。空集合测试只能防「手滑往集合里加东西」，防不了「新的 dispatch 路径绕过集合」。
M4 落地：dispatch 边界显式要求 `state == Authenticated && granted >= required`；
空集合测试保留为 defense-in-depth。采纳测试：枚举全部已注册控制操作、从 `PreAuthenticated` 逐一调用，
必须全部被公共门禁拒绝。

### 1.5 评审 A6：`approval_pending` 必须晚于 HMAC 验证 ✅ 采纳（M4）

顺序钉死：`parse → canonicalize → FixedTimeEquals 验 HMAC → 限流判定 → 才入审批队列`。
测试：100 个「结构合法但 proof 错」→ 审批门调用数 **恰为 0**。

### 1.6 评审 A7：限流只计「密码学失败」 ✅ 采纳（M4）

只对「到达 proof 校验且失败」的尝试计数。**不**计：framing 错、超时、类型不支持、审批拒绝、
审批超时、等待中断连、基础设施错误。测试：5 个 malformed 不触发锁定；5 个正确形状的错 HMAC 触发。

### 1.7 评审 A8：限流遥测不能全静默 ✅ 采纳（M4）

聚合结构化遥测（`badProofCount` / `lockoutEntered` / `lockoutExpired` / 来源 / 时间戳），日志自身限速；
绝不记单条 proof 素材、绝不逐条攻击一行。注意与 ADR-024 的分工：**传输层**（同子网/准入/TLS）日志
仍按 ADR-024 推迟到 M9；**M4 认证层**自带这套遥测，不等 M9。

### 1.8 评审 A9：冻结的 per-connection 安全上下文随交接传递 ✅ 采纳（M4）

在 `AcceptedConnection`（现含 local/remote/port/stream/TLS 版本）基础上，交接对象再冻结：
connectionId、**本连接实际使用的服务器证书指纹**、pre-auth 结局状态；
M4 认证期间**禁止**重读 discovery/config/实时证书状态。采纳测试：交接后立刻改 discovery 缓存 →
transcript 字节必须不变（客户端侧已有同型测试 `Frozen_Pin_Survives_Discovery_Cache_Mutation_Mid_Handshake`）。

### 1.9 评审 A10：#47 —— v1 不做「运行期记住」✅ 采纳（M4 定案）

依据成立：HMAC 证明「有人知道访问密钥」，**不证明**「持有某个控制器私钥」——知情者可以换一套 claims 重放。
v1：**每个新的控制连接都要人批**；`sessionToken` 只用于「已批会话自己的视频连接挂接」。本条目结掉
动工前挂的点「approve every time vs remember for this runtime」。

### 1.10 评审 A11：challenge 的 `certSha256` 从本连接实际证书派生 ✅ 采纳（M4）

服务端从这条 `SslStream` 实际用的证书构造；客户端先比对自己**实际验证过**的指纹（基础设施已就绪），
比对不上就在生成 proof **之前**中止。测试：注入不一致指纹 → 客户端必须拒绝生成 proof。

### 1.11 评审 A12 + #35：审批等待独立配额 ✅ 采纳（M4）

原全局 8 / 每 IP 2 是为「机器速度阶段」设计的；审批把人拖进来后必须换挡：
`PendingApproval` 独立有界配额（初值 **全局 3 / 每来源 1**，产品策略值），**永不出现无界待批**。
测试：超过配额的合法 proof 请求中，只有配额数量能到达审批门，其余直接终止且不产生 UI 提示。

### 1.12 评审 #37–#47：审批接口/显示/攻击面 —— 全部采纳为 M4 规格输入

| # | 条目（摘要） |
| --- | --- |
| 37 | 审批返回**显式终态**（`Approved(permission)/Denied/TimedOut/Cancelled/Unavailable`），**不是 bool**；无 handler 不得映射成 Approved |
| 38 | 审批请求**不可变**（请求 ID/源 IP/请求权限/自称设备/关联短码/绝对过期时刻）；UI 只回「决定 + 请求 ID」，绝不回填 transcript/HMAC/token/nonce/证书字段 |
| 39 | `granted ≤ requested`，由服务端计算，UI 文本不得越权 |
| 40 | 显示最小集：`访问密钥：已验证（VERIFIED）` + 来源 IP + **自称**设备（标注为自称）+ 请求权限 + 短关联码（由已认证素材派生、两端可见，仅作人工关联用）+ 剩余秒数；按钮 `允许请求的权限 / 仅允许查看 / 拒绝` |
| 41 | discovery 数据（名字/地址）永远是**未认证**的，不得以已认证的样式展示 |
| 42 | 反审批轰炸：先验 proof → 配额封顶（依赖 1.11） |
| 43 | 每请求唯一 ID + 可见短码；审批**针对该 ID**，不存在「批准当前任意待批」 |
| 44 | 请求状态单向原子转移 `Pending → Approved/Denied/TimedOut/Disconnected`，首个终态胜；迟到动作一律「请求已失效」 |
| 45 | 断连后绝不发放/注册 `sessionToken`（先转移终态、再注册） |
| 46 | 审批 UI 不可用 → **fail closed**；任何 headless 默认实现不得自动批 |
| 47 | = A10，见 1.9 |

---

## 2. 测试补强（B 组逐条核对：现状 → 缺口 → 落点）

| # | 评审要求 | 现状核对 | 落点 |
| --- | --- | --- | --- |
| 16 | 粘包：`hello‖auth` 同一次接收，首个读取只取 hello、第二帧完好 | **FrameReader 层已覆盖**（`Reads_Frame_And_Leaves_The_Rest_Of_The_Stream_Untouched`：粘连两帧、两次读、流恰好读完；`Oversized...` 证只消费 4 字节）。缺：真实 TLS/会话层同型用例 | M3.1 补真实 TLS 粘包用例（hello + 第二帧在同一次写内） |
| 17 | 交接所有权/释放 4 场景（失败归 pre-auth / 交接后 pre-auth 不得释放 / 交接后构造失败归 auth / 停机竞态恰一路径赢） | M4 设计尚不存在 | M4 测试清单（随线性所有权对象落地） |
| 18 | 停机必须报告「预算超限」，不得与干净成功不可区分；给出未完成计数 | 机制半在场（§0 P3）：bool 有、测试无、计数无 | M3.1：补 false 路径测试（永久卡死 handler）+ 暴露未完成计数 |
| 19 | 帧读取器**每个字节边界**的取消测试 | 部分：0 字节/半前缀(2)/半 payload(10/64) 已测；1、3 字节边界与「无二次完成/无状态复用」未测 | M3.1：参数化字节边界矩阵 |
| 20 | 准入名额在**每条终局路径**归还 | 部分：正常结束 / 握手卡死 / pre-auth 超时 / 停机 已测并断言归零 | M3.1：补 TLS 失败后、解析拒绝后、意外异常后三条 |
| 15 | 双 listener 竞争**同一**限额器 | 设计即共享（§0 P5）；缺跨 listener 用例 | M3.1：两个本地地址、其一耗尽、另一被拒 |
| 13 | connect 3s 在**首 SYN 丢失**下的表现 | 未测 | 实验（见 §3；涉及环境改动 → **用户通道**） |
| 14 | **冷启动** TLS（JIT/证书加载/忙 CPU）p50/p95/max | 未测 | 实验（本机可行） |
| 21 | 8 路并发握手的最弱机 CPU/内存 | 未测 | 实验（本机可行，低优先） |
| 22 | `per-IP=2` 不得静默变成 control+video 的最终配额 | 属实：两连接正好吃满 2 槽，重连与排空重叠时会撞第 3 槽 | **记录为设计约束**：视频挂接需要独立配额/预留决策（M5+），**不预调**认证前的限额 |

---

## 3. 评审 D1（五段数值表）裁定：**机制先行，数值等实验**

评审建议表（connect **5**s↑ / TLS 5s / prefix **3**s↓ / payload **5**s↓ / 信封 **8**s / hello 不作顺序段）
**全部自带 [NEEDS LOCAL EXPERIMENT] 标签**，与项目纪律一致：**本轮不动任何数值代码**。

- 机制差量（信封）按 §1.2 落地（初值 8s 同为 provisional）；
- 三个减值/增值候选进「待实测」桶，实验计划：
  ① 冷启动 + 忙 CPU（TLS 5s）；② 受控首 SYN 丢失（connect 3↔5s）；③ Wi-Fi 时延/抖动（如目标环境含 Wi-Fi）；
  ④ 8 并发握手资源占用（顺带核 global=8）；
- 完成后一次性定案（五段 + 信封）并进 ADR。**丢包实验需要环境改动（临时规则/中间件）——走用户通道，不静默动网**。

---

## 4. 评审 D2（失败消息分类学）裁定 ✅ 采纳框架 + 4 处修正

评审总原则正确且重要：**「对端一律 generic」只能在认证阶段内成立，无法把不同协议层变得不可观测一致**
（对端天然能区分：TCP 直接关 / TLS alert / TLS 成功后关 / 收到 `approval_pending`）。以此为准。

对照本机现行政策，4 处修正/新增：

1. **审批拒绝 / 审批超时** → 对端同一 generic 认证终态；本地分别记 `operator-denied` / `approval-timeout`（规格未定过，采纳）；
2. **审批子系统不可用** → generic 失败 + **绝不自动批准**，本地记 `approval-unavailable`（采纳，与 #46 呼应）；
3. **锁定期**（rate-limited）→ 对端只见 generic，**不泄露剩余次数**；本地聚合锁态（接 1.7 遥测）；
4. 表格「本地证据」列中**传输层**那几行的「rate-limited 日志」按 ADR-024 归 M9；M4 认证层不受此限（1.7）。

其余行与 M3 现状一致（10 个证书短码本地only、TLS 1.3 幽灵行区间 `0..1` 已在 M3 用两机证据确认）。

---

## 5. 评审 D3（300–800ms jitter）裁定 ✅ 采纳收窄版

- 只作用于**密码学认证失败**；**不** jitter：子网拒绝、准入拒绝、TLS 错误、framing 错误、传输时限；
- **不** jitter 审批拒绝/超时（此时 client proof 已验证，对方已知密钥）；
- 明确「jitter 不隐藏：协议相位、TLS 告警类别、是否出现过 `approval_pending`、大的处理时延差」——文档如实写；
- 300–800ms **保留**，等实测（槽位占用）再调；128-bit 密钥下限流才是主控制手段。

---

## 6. Prompt B 两决策裁定

### 6.1 Decision 1（认证交换落点）→ **(b) 显式交接** ✅（评审与本机基线一致）

采纳并加增强件（评审 #30–32）：

- 交接对象 = **线性所有权**对象（live `SslStream` + 冻结安全上下文 + connectionId + 取消生命周期）；
- 转移 **exactly-once**、以状态转移形式测试（#17 四场景）；
- 不变量改写：`PreAuthenticated` 只允许**一次** `BeginAuthentication` 转移；一切能力到 `Authenticated` 才可见；
- 旧测试 `PreAuthenticated_Allows_Nothing_Before_M4` 及其 clean-close 断言**有意识地改写**（保留为历史语义参照），不静默删除。

### 6.2 Decision 2（审批机制边界）→ 评审建议 **(b)、推翻本机此前 (a) 倾向**

评审论证（#36）：纯测试替身证明不了最易碎的一环——「真实请求可见、操作者**选对**待批项、决定与断连/超时竞态安全」；
而验收器正是为「真两机验证、不预设产品 UX」存在的。本机采纳该论证：

- 库内真接口 `ILocalApprovalGate`；单测走确定性替身；**验收器加最小审批面**（丑但对）；
- **产品 WPF UI 仍不动**（形态决策留后）。
- ⚠️ **治理备注**：prompt 明言「Prompt B 两决策的最终拍板 = 用户 + 本机事实」——本机采纳评审建议，
  **最终拍板留给用户，M4 开工前确认**（如实为否，退回 (a) 只需改验收器范围，不影响库内设计）。

### 6.3 评审给的 M4 时限初值（全部 provisional）

| 项 | 初值 | 说明 |
| --- | --- | --- |
| 机器认证（hello 交接 → proof 验证完成） | **10s** | 不含人类审批；falsification=双机 CPU 压测 + 滴流证明绝对性 |
| 人类审批 | **60s** | **从获批面受理**起算（排队等待不计入）；超时=拒（fail closed）、不污染限流；若真机演练出现合理超时再升 90s |
| PendingApproval 配额 | 全局 **3** / 每来源 **1** | 见 1.11 |

---

## 7. 净影响汇总（动作清单）

**M3.1 加固（先做，M4 之前）**
1. `PreAuthEnvelopeTimeout`（8s provisional）+ `HelloTimeout` 语义收拾 + 信封测试（§1.2）；
2. B 组测试补强：B15 / B16 / B18（含未完成计数）/ B19 / B20（§2）。

**M4 计划修订（阶段 0 落实为 ADR-027 修订 + 写进 HANDOFF §18.4）**
- transcript 拆分（§1.1）；交接 (b) + 所有权测试（§6.1）；门禁（§1.4）；限流范围+遥测（§1.6/1.7）；
  上下文冻结（§1.8）；审批全规格（§1.9–1.12、§6.2–6.3）；`certSha256` 派生（§1.10）。

**实验组（数值）**：§3 四项；丢包项走用户通道。

**不做**：不改 M3 五段数值（等实验）；不重开 M3 两机验收（信封用本机真实 TLS 集成测试证明；
如再跑验收物料需重打）；不在未接线时硬做 discovery 一致性（记为集成要求）。

---

## 8. 不采纳 / 降级明细

| 出处 | 条目 | 裁定 |
| --- | --- | --- |
| A1 | 「~20s 加法」数字 | **降级为 15s**（§0 P1）；方向采纳（§1.2） |
| A2 | 「hello 5s 是什么」 | 事实：从未接线（§0 P1）；按方案①精神落地为外层信封 + 语义收拾（§1.2） |
| A3 | 「需要新增报告对象」 | **已存在**（§0 P2）；只采纳三态呈现 + 集成要求 + 测试口径 |
| A3 | 「zero bind → 启动失败（抛）」 | **不采纳**：维持 ADR-031（库不抛、调用方呈现） |
| B18 | 「可能无法区分」 | **降级**：bool 机制已在；补测试 + 计数 |
| B15 | 「可能没共享限额器」 | **降级**：设计即共享；补测试 |
| #25 | 「10s 对 4 KiB 太宽松」 | 收入实验桶（payload 5s 候选），不在未测前改 |

**评审质量总评**：两处缺陷级发现（A5、A1/A2 合并）+ 一处流程级论证（#36）——这是三轮外部评审里
「设计层」产出最实的一轮；其失误集中在**被外发材料的前提所限**（P1–P5），其中 P1 源头在我。
