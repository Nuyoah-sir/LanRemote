# M3 验收器 UI 评审分流（第二轮外部评审 → 本机裁定）

**来源**：把 `docs/M3_ACCEPTANCE_UI_REVIEW_PROMPT.md` 发给外部模型后返回的评审（20 条，分四档）。
**本文件的作用**：按项目纪律**降级/升级**——只有本机实测或代码事实支持的才成为约束，
模型对 `.NET/Windows` 行为的断言一律标注**待实测**，不得直接写进 HANDOFF。

**日期**：2026-09-21
**触发场景**：刚要跑 M3 两机验收（A 机=本机，B 机=用户操作）时收到本评审。

**一句话结论**：评审的**前三条**（slow-dribble / host 侧结构化证据 / pin-mismatch 依赖结构化原因）
成立，且第 1 条是**真缺陷**——当前 `timeout` 场景证明不了它声称证明的性质。
**我建议先修再跑，不要在 B 机窗口期跑出会作废的证据。**

---

## 0. 先纠一个前提：评审以为「什么都得新造」，其实一半已经有了

评审建议新增 `ConnectionAcceptanceResult` 结构化结果对象。**本机代码核对结果：大部分已经存在**，
只是验收器没把它当判定依据：

| 评审要求 | 现状 | 位置 |
| --- | --- | --- |
| 结构化拒绝原因（`PinMismatch` 等） | **已有**，共 10 个短码 | `PeerCertificateValidator.rejection` |
| 实际出示的指纹（失败时也要） | **已有**，失败时尽力返回 | `TryValidate(out byte[] presentedPin, …)` 的 `presentedPin` |
| 拒绝原因进入异常 | **已有**，格式 `对端证书未通过校验（pin-mismatch）。` | `TlsClientConnector` 的 `AuthenticationException` |
| 服务端 per-connection 结局 | **已有**，`State` / `Completed` / `Rejection` | `ControlPreAuthResult`，验收器已打印 `[HOST][RESULT]` |
| 两端显式关 TLS 恢复 / 重协商 | **已有**，两端都显式 `false` + 显式 `Tls12\|Tls13` | `TlsClientConnector:144-146`、`TransportHost:341-343` |

所以评审 #8 的「记得显式禁用 TLS 恢复」是**空转**（产品已做，且上一轮评审已实测过默认值），
#2/#3 的大部分**不需要改产品代码**，只需要验收器**改用已有的结构化值**。

---

## 1. 采纳（必修）

### 1.1 【真缺陷】`timeout` 场景证明不了「绝对时限」—— 评审第 1 条 ✅ 采纳

当前场景：完成 TLS 后**一个字节都不发**，等服务端在 `lengthPrefixTimeout`（5 s）切断。
**可重置的空闲超时同样会在 ~5 s 断开并 PASS**，两者无法区分。

> 步骤 15 确实用真实 `SslStream` 做过滴流实测并确认了绝对时限，
> 但那是 **spike**，不是这个场景。场景本身是弱证据。评审说得对。

**改法**：新增第 4 个场景 `slow-dribble`——完成 TLS、进入长度前缀阶段后，
按绝对 deadline 的**中途**分两次发字节（例如 deadline=5 s 时在 ~3.5 s 发第 1 个字节、
~4.5 s 发第 2 个字节），要求服务端仍然**从阶段进入时刻起算**约 5 s 切断。
证据需含：`stageEnteredElapsedMs` / 每次发字节的时刻 / 切断时刻 / 配置的 deadline /
被控端终止原因（长度前缀 deadline 到期）。

### 1.2 客户端「符合预期」不能单独成立 —— 评审第 2、4、5 条 ✅ 采纳

`success` 的判定目前只依赖**客户端**观察：TLS 成功 → 写出 hello → 读到 EOF。
服务端完全可能完成 TLS 后**根本不读 hello** 就直接干净关闭，客户端看到的几乎一样。

**改法**：
- `success` 的 PASS 必须**同时**要求被控端 `outcome=PreAuthenticated rejection=-`；
- 两端都**不得**宣布里程碑通过：客户端只能说「CLIENT BATCH PASS — 需与被控端日志关联」，
  被控端只能说「HOST RUN COMPLETE — 需与控制端日志关联」。里程碑判定只由人工比对两份日志后给出；
- **引入 4 元组关联**：客户端记录自己 connect 后的本地临时端口，被控端记录 accept 到的远端端点，
  评审者才能把 `client 192.168.1.10:53144 → host :45873` 与**唯一一条** host 记录配对。
  聚合计数（`accepted=2`）不足以证明「被计入的就是这三个场景」。

### 1.3 pin-mismatch 必须依赖结构化原因，而不是「异常类型是认证异常」 —— 评审第 3 条 ✅ 采纳（带修正）

现状：断言退化成「抛了 `AuthenticationException` 就算握手被按预期拒绝」。
`AuthenticationException` 也可能是别的原因（对端证书形状不对、无证书、过期…）。

**改法**：PASS 需同时满足
1. 抛 `AuthenticationException`；
2. 异常消息里的结构化短码 **恰好等于 `pin-mismatch`**；
3. 期望指纹与冻结的实际指纹**确实不同**；
4. 生成错误指纹的方式改为**翻转真实指纹的 1 个 bit**（而不是造一个无关的十六进制串），
   这样「不匹配」是**最小差异**、指向性最强；
5. 被控端在该场景中收到的**应用帧数 = 0**。

**修正评审的一点**：评审要求**删掉**独立 TCP 探针，理由是它污染被控端计数、
制造 TIME_WAIT、且不证明 pinning 分支跑过。前半句**成立**（探针是被控端 accept 到的一条真连接，
会进 `accepted` 计数并在 TLS 阶段静默失败）。

> **实测已收口（2026-09-21）**：TCP 层根本没连上时抛的是 `SocketException` /
> `IOException`（内层 `SocketException`），**不是** `AuthenticationException`。
> 于是第 1 条断言本身就足以排除「端口没开」→ **探针已删除**，
> 并由 `nothing-listening` 对照组证明「端口上什么都没有」会落到**前置条件不满足（退 2）**
> 而不是 PASS（退 0）。

### 1.4 其余采纳项

| # | 内容 | 成本 |
| --- | --- | --- |
| 6 | 计数不是划分：`accepted/preAuthenticated/rejected` 语义重叠。改为「事件计数 + 互斥终态桶」，停止前要求 `Σ终态 == accepted` 且 `active=0`；`cleanStop` 改名 `listenersStoppedCleanly` | 中 |
| 7 | 不要从「读到 EOF」推断对端发生了 `close_notify`；改为分别记录「服务端 `ShutdownAsync` 成功完成」+「客户端观察到有序 EOF」 | 低 |
| 9 | 每次运行一个**不可变**日志文件（`m3-acceptance-<UTC>-<guid>.log`），首行 Run ID、末行 `RUN COMPLETE`；**删掉「清空日志」**——验收仪器不该允许选择性抹除证据 | 低 |
| 10 | 日志头必须含构建溯源：验收器版本/程序集 SHA-256、传输层程序集版本、UTC 起始、OS 版本/build、架构、.NET 运行时、本机身份、合格网卡+掩码、各阶段 deadline、启用协议、恢复/重协商是否关闭 | 低 |
| 11 | 对端设备码解析要有溯源：冻结并记录 `deviceId` / 源 IP / 端口 / 完整指纹 / `lastSeen` 年龄 / 本地出口网卡；0 个或多个匹配 = 前置条件不满足 | 低 |
| 12 | **角色锁**：两个角色组不能同时可用。选定角色后另一组禁用，直到停止/复位；大幅常驻横幅区分 `HOST——让这台机器继续监听` / `CLIENT——在这台机器上跑测试`；拒绝选到自己（本地设备 ID / 本地合格地址） | 低 |
| 13 | 操作员点「停止/中止」必须把整轮**毒化**为 `INVALID RUN`，不得把由此产生的 socket 关闭翻译成场景 PASS | 低 |
| 14 | 保持「就绪检查不过则禁用角色按钮」，并把状态明确写成 `PRECONDITION — 未执行任何 M3 测试`，不要写成像产品失败的红色 FAIL；补「重新自检」与「复制前置条件证据」 | 低 |
| 15 | 引入第四类结局 `HARNESS_ERROR / INVALID`，与 `PRODUCT_FAIL`、`PRECONDITION_NOT_TESTED` 分开；证据写入失败或被观察者抛异常时，场景**不是** PASS | 低 |
| 17 | **不许把 Dispatcher 当测量时钟或 deadline 机制** | 中 |
| 18 | 日志 UI 不得进入计时关键路径（结构化记录先落盘，展示文本异步投递） | 中 |
| 19 | 去掉最终成功弹框，改常驻状态面板；`复制证据` 只在所有任务终态且日志已刷盘后可用 | 低 |
| 20 | 定义机器可读证据 schema，每个场景**恰好一条**终态 RESULT 记录 | 中 |

### 1.5 第 17、18 条是**真实缺陷**，不是理论担忧

`MainWindow` 的点击处理器是 `async void`，而 `ClientRole` 里**没有 `ConfigureAwait(false)`**——
于是每个 `await` 之后的续体都会**回到 UI 线程**。日志区在自动滚动、WPF 在排版时，
续体会被延后执行。`timeout` / `slow-dribble` 这类**靠时间判定**的场景正好被它影响。
这与本项目已有的教训一致（§16「不要把对端怎么收尾压成一个布尔」属于同一类：别让测量被表现层污染）。

---

## 2. 标为**待本机实测**（不得据模型结论下判断）—— **已全部实测完毕，2026-09-21**

| # | 评审断言 | 实测结论 | 证据 |
| --- | --- | --- | --- |
| 16 | 「后台线程未处理异常**不会**被 `DispatcherUnhandledException` 转发」+「`TaskScheduler.UnobservedTaskException` 默认不终止进程」 | **两个方向都实测到了，且比评审说的更极端**：裸后台线程上未处理异常 → **进程当场死亡**（exit 127，无任何输出，handler 来不及做什么）；未被 observe 的 faulted Task → **进程存活且完全静默**（.NET Core 起终结器不再杀进程） | 临时 spike（`tcp`/`eof`/`thread`/`task` 四组） |
| 3 | 「TCP 未连通时不会走到证书校验」 | **成立**。TCP 层失败抛的是 `SocketException`（积极拒绝）或 `IOException`（内层 `SocketException`），**永不**是 `AuthenticationException` → 独立 TCP 探针**已删除**，改用「必须抛 `AuthenticationException` 且短码恰为 `pin-mismatch`」这一更强判据 | 变异矩阵 `nothing-listening/success` → 退 2（UNMET），不是 FAIL |
| 7 | 「EOF 不等于 `close_notify`」 | **成立**。`SslStream.ReadAsync` 返回 0 **不能**证明对端发了 `close_notify`——裸 TCP FIN 也返回 0（RST 则抛 `IOException`）；`SslStream.ShutdownAsync` 只有在显式调用时才发送 `close_notify` → 全部文案降级为「**有序 EOF**（TLS 记录层 EOF）」，不再声称对端执行过 `close_notify` | spike `eof` 组 |

> 结论按项目纪律**由实测换成依据**后已写进 HANDOFF；第 16 条的工程结论
> （每个后台任务都必须被拥有、被 await、异常转成 `HARNESS_ERROR`）已落地为
> `App.xaml.cs` 的两个额外 handler + `AcceptanceRun.ReportBackgroundFault`。

---

## 3. 不采纳 / 已过时

| # | 评审建议 | 裁定 |
| --- | --- | --- |
| 8 | 「记得显式禁用 TLS 恢复」 | **已过时**：产品两端都已显式 `AllowTlsResume=false` + `AllowRenegotiation=false` + 显式 `Tls12\|Tls13`（上一轮评审已实测默认值）。验收器无此缺口 |
| 8 | 「不要为隔离重启 host」 | **采纳其结论**，但它当成一个待决问题提出——本设计本来就没有重启 host，也是对的：重启引入的变量比消除的多 |
| 3 | 「删掉 TCP 探针」 | **条件采纳**，取决于 §2 的实测。若实测支持，则删探针并用结构化断言替代（更强且无副作用） |
| 5 | 「成功场景可以在应用数据里带场景 ID，**只要不削弱真实解析器**」 | **不采纳**。为了让验收器好写而扩展产品协议，属于典型的「为测试改被测对象」。改用 4 元组关联即可，不动协议 |
| 19 | 「保留致命启动错误的弹框」 | **采纳**，与现有 `DispatcherUnhandledException` 落盘策略一致 |

---

## 4. 立刻要做的三个动作（评审建议的「只做三件事」）

1. **加 `slow-dribble` 场景**，让「绝对 deadline」第一次被真机场景证明（当前只被 spike 证明过）；
2. **`success` 的 PASS 必须等待被控端结构化证据**，并引入 4 元组关联；两端都不得自宣里程碑通过；
3. **pin-mismatch 的 PASS 改为依赖 `rejection == "pin-mismatch"`**（翻转 1 bit 造错指纹），
   并按 §2 实测结果决定 TCP 探针去留。

---

## 5. 对 M3 当前状态的影响

**M3 依然卡在第 24 步（两机验收），但「先修」这一半已经完成**（2026-09-21）。

已落地的修正：

| 项 | 状态 | 证据 |
| --- | --- | --- |
| `slow-dribble` 场景（§1.1） | **已加**，且是唯一能区分「绝对 deadline vs 可重置空闲时限」的场景 | 变异矩阵 `resettable/slow-dribble` → 退 1（**变红**），`resettable/timeout` → 退 0 |
| `success` 必须等被控端结构化证据（§1.2） | **已改**：控制端只给 `hostEvidence=REQUIRED` + `hostExpect="…"`，末尾打 `[VERDICT] M3 = PENDING-HOST-EVIDENCE`，**永不**自宣里程碑通过 | 每个场景的 `[CLIENT][RESULT]` 行 |
| 4 元组关联（§1.2） | **已加**：控制端打 `local=<IP>:<临时端口>`，被控端打 `peer=<同一个端点>`；为此给 `TlsConnection` 加了只读 `LocalEndPoint`（不参与判定）并有对侧一致性测试 | `TlsClientConnectorTests.Connect_Reports_Local_EndPoint_Matching_Server_Observation` |
| `pin-mismatch` 依赖结构化短码（§1.3） | **已改**：必须是 `AuthenticationException` + 消息里短码恰为 `pin-mismatch`；错指纹改为**翻转真指纹 1 位** | 变异矩阵 `real/pin-mismatch-doubleflip` → 退 1（证明翻转是承重的） |
| 删 TCP 探针（§1.3 条件项） | **已删**，条件已实测满足 | 见 §2 第 3 条 |
| 计数不是划分 / 互斥终态桶（#6） | **已改**：`[HOST][BUCKETS]` 带 `partitionOk`，`cleanStop` 改名 `listenersStoppedCleanly` | `[HOST][SUMMARY]` |
| 不可变日志 + 删「清空日志」（#9） | **已改**：`m3-<role>-<UTC>-<runId>.log`，删掉清空按钮 | 实际产出的日志文件名 |
| 构建溯源头（#10） | **已改**：`[BUILD]`（程序集 + SHA-256）/ `[ENV]`（OS/build/arch/.NET/五个 deadline） | 日志头 |
| 角色锁 / 自我拒绝（#12） | **已改**：`RoleBanner` + `IsSelf()` | 窗口 |
| 中止毒化（#13） | **已改**：`MarkOperatorAbort` → 整轮 `INVALID_RUN`（退 4） | `AcceptanceOutcome.InvalidRun` |
| 四类结局分离（#15） | **已改**：`PASS / FAIL / UNMET / HARNESS_ERROR / INVALID_RUN` 与退出码一一对应 | `AcceptanceOutcome` |
| Dispatcher 不当计时器（#17、#18） | **已改**：headless 走 `Task.Run`；`ConfigureAwait` 与「结构化先落盘、展示异步投递」按 `AcceptanceLog` 双写实现 | 见 HANDOFF §15 |

**新增：headless 入口**（用户选择「加 headless 入口」）。同一份 exe：
不带参数双击 → 开窗口；带 `--headless` → 命令行。两条路调用**完全相同**的
`HostRole` / `ClientRole`，因此不存在「脚本跑的是另一套逻辑」。

### 5.1 修这一轮时新抓到的三个真缺陷（都不是模型提出来的，是本机跑出来的）

1. **`--all` 下 `pin-mismatch` 必然 FAIL** —— 直连模式漏了「翻转一位」，
   四个场景共用同一个 `--pin`，于是拿**真指纹**去连、握手必然成功。
   失败信息还写着「本该在 TLS 阶段被拒绝，实际握手成功了」，看上去像产品坏了。
2. **交叉核对清单自相矛盾** —— 逐条期望写着 `pin-mismatch → (无行)`，
   而 ④ 的区间写着 `3..4`（承认可能有 TLS 1.3 幽灵行）。按逐条去核对的人会去找「零行」。
   同一份输出里的两句话互相打脸，比两句都错更危险。
3. **参数错误静默退出（最严重）** —— `HeadlessCommand.TryParse` 在参数错误时
   `return true`，调用方按「成功」处理 → `RunHeadless(null)` → `NullReferenceException`
   → 被顶层兜底成退 3。**错误消息一个字都没打出来**（stdout/stderr 均 0 字节，
   只有 crash.log 里一行 NRE）。使用者看到的是「验收器坏了」，真因是返回值语义反了。

三条的共性：**都不是产品代码错，而是「证据/接口自己说谎」**。所以它们的严重性高于普通 bug
——验收器的错误会污染结论。
