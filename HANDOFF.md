# LanRemote HANDOFF

> 模板来源：`LanRemote_Implementation_Package/09_HANDOFF_TEMPLATE.md`
> 更新时间：**2026-09-24（现网修复已实现并验证；当前交付及人工关口见 §18.13）**
>
> **优先停点：暂停旧候选包重试及“准备 A/B”流程。用户报告 A 准备后 B 热点上网中断；B 发现层 AddMembership 抛 10022。未进入认证，M4 未通过。**
> **软件及验收不得导致任一端断网，短暂也不接受；不能以确认框/UAC或可撤销为理由继续改现网。新版已移除改网执行链，旧脚本也无条件拒绝；不默认执行 Undo。两机重验前确认原有联网正常。**
>
> **当前状态：双端认证、非秘密 pending 通知、有界本机审批、密钥界面与真实会话证据已接通。**
> 认证阶段5主实现 `6c7a15b` / 测试修正 `7a9d199`；现网修复主实现 `bc8cf48`。当前 Debug / Release 全量各 **1305 PASS / 0 FAIL / 0 SKIP**，构建均 **0 警告 / 0 错误**；最终代码与证据对应见 §1 / §18.13。
> 阶段 5 新增 8 项运行期变异 kill 并恢复；证据与局限见 §18.11，现行合同 ADR-042/043/044。
> **2026-09-24 两台用户日志已记录 GUI 渲染；完整布局/DPI/密钥/审批未验，真实两机认证被发现层故障阻断。因此 M4 整体仍进行中。**
> 用户已授权自主推进，不再等待外部模型，不重问已采用的最小密钥交付/短码方案。
> 产品 App 尚不能看屏或键鼠控制；本轮未改产品 App、权威规格、系统网络或防火墙。
> **下一关：修复版 GUI / 热点两机认证及双方联网不受影响的人工验收；不能先推进 M5。** A侧现有热点识别、组播加入及TCP监听已实际运行成功（无对端为UNMET，不是认证PASS）。§18.12为事故记录，修复交付见§18.13。
> §18.9/§18.10 保留阶段 4 与评审等待历史，不再是当前停点。
>
> 上轮（**M4 · 阶段 3「服务端认证状态机」**）**动了代码**：
> 新建 7 个产品文件（`ConnectionSecurityContext` / `ControlAuthContext` /
> `ControlAuthSession` / `ControlPreAuthHandoff` / `FailedAuthLimiter` /
> `LocalApprovalGate` / `SessionRegistry`）+ 认证测试 `ControlAuthSessionTests.cs`
> （25 用例）+ 6 个修改文件（衔接层改写与指纹冻结）；提交 `760e950`
> （14 files，+2780/−89）；全量 **902 PASS / 0 FAIL**（Transport 460→485，+25）。
> 变异验证 ×4（M4 证明纵深防御「全拆才红」形态）；
> 合同级决定已立 **ADR-041**。逐条见 §18.8。
>
> 上轮（2026-09-22）：M4 阶段 2「认证消息帧（JSON 严格解析）」（ADR-040）见 §18.7；
> 阶段 1「transcript + HMAC proof 纯函数核心」+ 同日重定位 Security → Transport
> （ADR-039）见 §18.6；更早：阶段 0「衔接盘点与定案」（ADR-037/038 + ADR-027 落地）
> 见 §18.5，第二轮外部评审回收见 §18.4。

---

## 1. 当前状态

- **当前里程碑：M4 — Access Key Challenge Auth —— 进行中：阶段 0–4 实现完成；阶段 5 认证验收器接线、自动化测试及变异完成（2026-09-23）。** 步骤 19 固定文案已接入验收器错误显示路径；2026-09-24 用户两端已运行GUI但发现层故障阻断认证，不宣称完整 M4 DoD 通过。
- **下一步：使用§18.13修复包完成GUI/两机认证与持续联网验收，旧包停用。** WFD识别、按接口组播加入和改网入口停用已完成；不改 DHCP/IP/路由/DNS/热点/网络类别/防火墙凑验收。既有网络恢复须另行核实和授权，不执行旧Undo。
- 已完成：M0 → M1 → M1.1 → M1.2 → M1.3 → M2 → M2.1 → **M3 → M3.1** →（M4 阶段 0–4 实现；阶段 5 自动化完成，修复版人工验收待通过）
- 版本：`0.1.0-m2`（本轮**未**推进版本号）
- **Last code commit：`72bfb93`**（旧命令拒绝输出统一UTF-8）；现网修复主实现 `bc8cf48`（21 files，+1052/−1469）。认证阶段5主实现 `6c7a15b`，客户端/时限修复 `bc02a0c`。
- **Working tree at validation：最终 Debug/Release 各1305 PASS、0警告/0错误，对应 `bc8cf48` + 随后提交为 `72bfb93` 的拒绝提示编码修正**；三项网络变异均已恢复。证据 `outputs/m4-network-fix/14..17`，Python10项回归见08；之后只记账、fresh publish和包级实跑。前轮1190及1036证据仍保留，不冒充当前版本。
- M3.1 记录（历史）：Last code commit = `2dee00c`（pre-auth 外层信封 + 停机报告 +
  B15/B16/B18/B19/B20 测试补强 + 验收器同步）；601 PASS 验证后未再动代码。
- **注意：M3.1 起至 M4 阶段 3，每一轮都改动过 `src` / `tests`**——M3 两机验收的旧物料
  （zip `f81d194c…`，由 `1d5ffc8` 后工作树打出，harness / transport 哈希两端逐字符一致）
  **早已是历史版本**；如需重跑两机验收，物料必须按最新代码重打（§18.4 A 节 / §18.5 D 节）。
- **两机 lab 环境均已就绪，且第 24 步两机验收已跑完（PASS，2026-09-21）**：
  A（本机）= `192.168.1.10`、B = `192.168.1.20`，UDP 45872 + TCP 45873 入站放行；
  A 侧四场景 4/4 PASS（runId `8a7e3d03`）、B 侧四条连接行与五条汇总约束全部对上
  （runId `0e7f03d0`）。**收尾已完成：两机 lab 均已撤销还原**——B 机 2026-09-21 11:13Z
  （用户点窗口按钮）、A 机 11:21Z / 本地 19:21（管理员直跑同物料 `--headless lab-undo`，
  runId `037fd836`）；两机撤销后 `--headless info` 均 `qualifiedNic=(无)`。判定明细与
  `INVALID_RUN` 口径见第 15 节第 24 步记录。
- **M2.1 code 状态：Implementation complete；Two-machine manual DoD：PASS**
  （2026-09-20 17:30–18:18 两台实机跑完 20 步，20/20 通过，见第 9 节）
- **是否满足完整 M2 DoD：是**（两机手工验收已回填）
- **M3：完成**——24 步全部完成，第 24 步两机验收（真实 TLS + pinning）**PASS**（2026-09-21）。
  判定以逐条连接证据为准（五约束 + 四元组配对）；被控端结局字段 `INVALID_RUN` 是收尾机制
  的机械产物、**不是失败**（第 15 节第 24 步记录有专述）。
  `dotnet build` 0 警告 0 错误，`dotnet test` **574 PASS / 0 FAIL**（验收轮未改代码）。
  逐步明细、实测数据与禁止回访项见**第 15、16 节**——以第 15 节为准，本节可能滞后。
- **验收器是 WPF 窗口程序（`WinExe`），双击 `LanRemote.Acceptance.exe` 就是一个窗口**，
  不需要任何脚本；2026-09-24已移除准备A/B及撤销入口，网络检查只读；
  同一个 exe 带 `--headless` 仍可运行 info/host/client，旧改网动词一律拒绝。
  曾短暂采用「控制台 exe + `START.cmd`」的形态，被用户连纠三次后废弃——
  **别改回去**，理由与坑见第 15 节「M3 验收器的形态教训」。
- **注意：不要拿 `LanRemote.App` 验收 M3**——它引用了 `LanRemote.Transport` 但一行都没调用，
  打它的包只能重证 discovery。
- **远端仓库已上线（2026-09-21）：`https://github.com/Nuyoah-sir/LanRemote.git`（public，
  用户手动建库）**——完成**首次全量推送**：`main` 与本地一致（52 提交 / 693 对象 / 726 KiB，
  远端 `refs/heads/main` 与本地 HEAD 逐字符相同）。推送走 git+HTTPS（GCM 缓存凭据，
  `gh` 未安装也不需要）；此后记账提交按「关于 git 记账方式」同步推送。

### 关于 git 记账方式

HANDOFF 不写 HEAD hash（写完立刻过期的自引用）。固定使用：
`Last code commit`（最后一次代码/测试提交）+ `Working tree at validation`。
允许 HEAD 比 Last code commit 新（之后会有单独的文档提交）。
远端 `origin` 已配（GitHub，public）：每轮记账提交后 `git push origin main` 同步。
**两个已实测的坑**：① 链路间歇性抖动（push 挂到超时 / 偶见 schannel 握手失败）→ **重试即过**；
② **helper-selector 陷阱**：`git-credential-helper-selector` 每次被 git 当 helper 调用都会**弹 GUI**
（源码级：无「已选过即静默委托」分支；无桌面会话挂起；连 `--help` 都弹并写配置），选「`<no helper>` + Always」
会把它写成 `credential.helper = <空>`（清链）——2026-09-21 19:57 本机全局就是这样被写坏的（推送 `exit 128`）。
**修复（2026-09-21，全局 + repo 双层）**：`credential.helperselector.selected = manager` + 链「空值 + `manager`」
（空值重置 system 级 `helper-selector`）。机器级验证：从 `/tmp`（无 repo 配置参与）`git credential fill` rc=0、
`GIT_TRACE` 只见 `git-credential-manager`。**不要改回、不要裸跑那个 selector。**
push 仍带 `GIT_TERMINAL_PROMPT=0` + 关 GCM 交互（`credential.interactive=false`/`guiPrompt=false`）。

## 1.5 M2.1 — Discovery Final Fix（本轮修复明细）

源码审计 M2 之后发现 1 个真实功能 bug + 4 个协议/可靠性问题，本轮全部修复。
**没有**进入 M3：**没有** TCP、TLS、SslStream、Auth、HMAC、Video、Input、Session 的任何代码。

| # | 问题 | 状态 |
|---|---|---|
| 1 | probe unicast 回应发错端口（本轮阻断项） | ✅ 已修复 + 回归测试 |
| 2 | 未显式指定组播出口网卡（多网卡会走默认路由） | ✅ 已修复 + 实测验证 |
| 3 | capabilities 校验过松（空项静默跳过、上限按去重后算、控制字符放行） | ✅ 已修复 + 回归测试 |
| 4 | capability 测试语义错误 | ✅ 已重写 |
| 5 | discovery 启动失败的状态文本被 config 文案覆盖 | ✅ 已修复 |
| 6 | `StartAsync` 创建 socket 失败时 linked CTS 未 Dispose | ✅ 已修复 |
| 7 | probe 回应成功时**没有任何日志**，两机验收无法判定链路是否真的通 | ✅ 已补一行 Debug 日志（见下） |

### 1.5.6 补充：probe 回应日志（为两机验收的可判定性而加）

`ReplyToProbeAsync` 原先只在「拒绝回应」和「发送失败」时写日志，**成功回应时没有日志**，
导致验收第 18 步「B 应能正常响应」无法从日志判定。已补一条 Debug：

```text
已回应来自 {ProbeAddress} 的 probe：unicast → {ReplyTarget}（不使用源端口 {SourcePort}）。
```

它同时记录「被丢弃的源端口」与「实际回应目标」，现场就能确认回应没有再打到对方的随机临时端口上。
只记地址与端口，不记报文内容、不记任何秘密。**纯日志，未改动任何逻辑**（重跑后仍是 408 passed）。

### 1.5.1 probe 回应端口（阻断项）

- **probe 的来源端口示例**：`192.168.1.20:53742`
  —— 发送 socket 是 `Bind(binding.Address, 0)`，端口由系统分配的随机临时端口。
- **旧行为**：`ReplyToProbeAsync` 把 `remote` 整个当回应目标 → 发给 `192.168.1.20:53742`。
  那个临时端口上<b>没有任何人在监听</b>，回应必然丢弃。
  **症状**：点了「刷新」，对方就是收不到、列表就是不出现；announce 周期（2 s）未必能救回来，
  因为定向广播 probe 的使用场景恰恰是「对方收不到组播」。
- **修复后最终目标端口**：`192.168.1.20:45872` —— 即 `remote.Address : DiscoveryConstants.Port`。
  LanRemote 的 discovery receiver 永远监听 UDP 45872，端口是常量，只有地址取自报文来源。
- **实现**：新增 internal 纯函数 `DiscoveryReplyTarget.ForProbe(IPEndPoint)`；
  `ReplyToProbeAsync` 改为 `DiscoveryReplyTarget.ForProbe(remote)`，
  代码里已<b>禁止</b>再把 `remote` 直接当目标传下去。
  该类型是 internal，测试工程通过 `InternalsVisibleTo("LanRemote.Protocol.Tests")` 访问，
  **没有**为测试新增公共 API。

### 1.5.2 组播出口网卡（IP_MULTICAST_IF）

- **最终设置方法**：对每个 sender，在保留 `Bind(binding.Address, 0)` / `Broadcast=true` /
  `MulticastTimeToLive=1` 的基础上，追加
  `SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, optionValue)`。
- **optionValue 的形式**：**接口 IPv4 地址的 4 字节网络序**，
  由 `MulticastInterfaceOption.ForInterface(binding.Address)` 产出（内部就是 `IPAddress.GetAddressBytes()`，
  已是网络序，**不需要**任何额外字节序转换）。
- **为什么必须显式设置**：`Bind` 只决定 unicast 源地址，组播从哪张卡出去由 `IP_MULTICAST_IF` 决定。
  不设置 → 全部退化成系统默认组播路由；Ethernet + Wi-Fi 各有私有 IPv4 时，
  另一张卡所在子网永远收不到 announce。现在做到「sender for binding A → multicast interface A」。
- **本机实测证据（.NET 10 / Windows，设置后立即 `GetSocketOption` 回读，不是猜测）**：

  | 写法 | 结果 |
  |---|---|
  | `byte[4]` = 接口 IPv4 地址（`GetAddressBytes()`） | ✅ OK，回读 = 原地址（如 172.100.166.220） |
  | `int` = `IPAddress.HostToNetworkOrder(interfaceIndex)` | ✅ OK，回读 = `0.0.0.6` 形式（索引 6） |
  | `int` = `IPAddress.HostToNetworkOrder((int)addr.Address)` | ❌ `SocketException: 在其上下文中，该请求的地址无效` |
  | `int` = 裸接口索引（不转换字节序） | ❌ `SocketException: 在其上下文中，该请求的地址无效` |

  最终采用第一种（地址形式）。测试 `MulticastInterface_CanBeAppliedToRealSocket`
  会在 Windows 上真实建 socket 并断言设置不抛异常、回读值还原为设置的地址。

### 1.5.3 capabilities 语义（三处收紧）

| 语义 | 规则 |
|---|---|
| **raw count 上限** | **去重之前**先判：`raw.Count > 16` → 拒绝。不再按去重后的 `HashSet.Count` 判断。否则 `["view" × 100]` 去重后只剩 1 项，可以无限绕过「最多 16 项」。 |
| **空白 capability** | `null` / `""` / `" "` / `"   "` / `"\t"` / `"\r"` → **一律拒绝**（`return false`），不再 `continue` 静默跳过。对端声称自己有一个「空白能力」本身就是畸形报文。 |
| **控制字符** | 含 `\n` `\r` `\t` 等控制字符 → **拒绝**。原因：第一次发现设备时 capability 会进日志，放行 `\n`/`\r` 等于让局域网报文伪造日志行。<br>**判定发生在 `Trim()` 之前**——`\n`/`\r`/`\t` 本身也是空白，先 Trim 会把 `"view\n"` 洗成 `"view"` 从而放过（这条是写测试时才暴露的真实漏洞，已修）。 |
| **保持允许** | `view` / `control` / `future-capability`（unknown capability 仍允许）；合法重复项去重；首尾普通空格允许 Trim（`" control "` → `control`）；单项上限仍为 32 字符；`null` 列表与空列表仍视为「无能力」并接受。 |

### 1.5.4 discovery 启动失败的状态文本

- **已修复**。`StartDiscoveryAsync` 改为返回 `bool`；失败时由它**自己**写入
  `本机身份已加载，但局域网发现启动失败。`；
  `LoadAsync` **只有**在返回 `true` 时才写「已从磁盘加载配置与本机身份。」/「未发现配置文件…」。
- 旧行为：无论成功失败，`LoadAsync` 都会无条件覆盖，把「端口被占用」这类真实网络错误
  伪装成「加载成功」。
- 身份**仍然不会**因为 discovery 失败而重置。

### 1.5.5 CTS 失败路径

- **已修复**。`StartAsync` 中 socket 创建失败时：`CloseSocketsCore()` → `_cts = null` →
  **`cts.Dispose()`** → `throw`。
- 不会 double-dispose：失败路径上一个后台循环都没起来，且 `_cts` 已置空，
  `StopAsync` 会直接返回，碰不到这个 CTS。

## 2. M2 做了什么

两台 LanRemote 只要位于允许的同一 IPv4 私有子网，即可自动互相发现并在 MainWindow 显示真实设备。

本轮**没有**：TCP listener、SslStream、TLS 校验、channel_hello、AuthChallenge、HMAC、
访问密钥网络认证、SessionToken、视频、抓屏、JPEG、SendInput、SessionWindow、文件传输、
剪贴板同步、音频、公网穿透、UPnP、NAT、云服务器。**也没有**任何防火墙自动修改（属于 M10）。

设备列表里的「连接」按钮保持 disabled，提示「连接功能将在 M3/M4 实现」。

## 3. NetworkInterfaceSelector 最终筛选规则

`src/LanRemote.Discovery/Networking/NetworkInterfaceSelector.cs`

对 `NetworkInterface.GetAllNetworkInterfaces()` 的每个网卡，逐个检查：

| 条件 | 通过要求 |
|---|---|
| 运行状态 | `OperationalStatus == Up` |
| 网卡类型 | `Ethernet` / `FastEthernetT` / `FastEthernetFx` / `GigabitEthernet` / `Wireless80211`（显式排除 Loopback、Tunnel、Ppp、Unknown、Wwanpp 等） |
| 虚拟/VPN 过滤 | 名称或描述不含虚拟关键词（见下） |
| IPv4 地址 | `AddressFamily == InterNetwork` 且属于 RFC1918 |
| 子网掩码 | 非空、`Ipv4Math.IsUsableMask` 为真（连续前缀掩码，排除 0.0.0.0 与 255.255.255.255） |

虚拟/VPN 关键词（`VirtualAdapterFilter`，大小写不敏感）：
`virtual` `hyper-v` `vethernet` `vmware` `virtualbox` `wireguard` `wintun` `tailscale` `zerotier` `tap` `tunnel` `vpn`。
刻意**不**使用 `microsoft` / `intel` / `realtek` 这类会出现在真实网卡名称里的词（会误杀）。

一张 NIC 有多个合格 IPv4 时，每个地址各自形成一个 `NetworkBinding`。

`NetworkBinding` 至少包含：本机 IPv4、subnet mask、directed broadcast（`address | ~mask`，不硬编码 `.255`）、接口身份（`InterfaceId` / `InterfaceName` / `InterfaceType` / `InterfaceIndex`）。

为可测试性引入的极小 OS 抽象：`INetworkInterfaceSource` + `SystemNetworkInterfaceSource` + `NetworkInterfaceSnapshot`，
这样单元测试不必真的修改 Windows 网卡。

## 4. ISubnetPolicy 实现位置

`src/LanRemote.Discovery/Networking/SubnetPolicy.cs`，实现既有接口
`LanRemote.Core.Abstractions.ISubnetPolicy`（**未修改其语义**）。

判定顺序：

1. local / remote 都必须是 IPv4；
2. remote 必须 RFC1918；
3. 按 `localAddress` 找到**完全匹配**的本机 `NetworkBinding`，找不到 → `false`；
4. 用该绑定的**真实掩码**比较网络号（`local & mask == remote & mask`）；
5. 相等 → `true`。

「两者都是 RFC1918」**不**等于同子网（10.x 与 192.168.x 都是私有地址但不同网络）。
支持 /8、/12、/16、/20、/23、/24、/25 等任意掩码，不假定 /24。
禁止字符串前缀或「前三段相同」这类近似判断。

## 5. Discovery socket 结构

`src/LanRemote.Discovery/LanDiscoveryService.cs`

- **receiver**：`AddressFamily.InterNetwork / Dgram / Udp`。
  在 `Bind` **之前**依次设置 `ExclusiveAddressUse = false` 与 `ReuseAddress = true`（Windows 顺序敏感），
  然后 `Bind(0.0.0.0:45872)`，最后对每个合格 binding 加入组播 membership。
  接收缓冲区 `2049` 字节（协议上限 2048 + 1）。
- **sender**：每个合格 binding 一个 socket，`ReuseAddress = true` → `Bind(binding.Address:0)` →
  `Broadcast = true` → `MulticastTimeToLive = 1` →
  **`MulticastInterface = 该 binding 的 IPv4 地址（网络序 4 字节）`**（M2.1 新增）。
  最后这一步是决定性的：Bind 只管 unicast 源地址，组播出口必须靠 `IP_MULTICAST_IF` 指定，
  否则多网卡时会全部走系统默认组播路由（详见 §1.5.2，含本机实测证据）。
- **无合格网卡时不开任何 socket**（既不拿不到可信来源，也不该白占端口）。
- 三个后台循环：`ReceiveLoop` / `AnnounceLoop` / `CleanupLoop`，全部受同一个 `CancellationTokenSource` 控制；
  `StopAsync` 会 cancel、关闭 socket、`await` 三个循环、complete channel。
- `StartAsync` / `StopAsync` 均幂等。

## 6. Multicast group 与 probe 行为

| 项 | 值 |
|---|---|
| 组播地址 | `239.255.77.77` |
| 端口 | UDP `45872` |
| TTL | `1` |
| announce 周期 | 2000 ms |
| 缓存 TTL | 7000 ms |
| 清理周期 | 1000 ms |
| 协议版本 / 魔数 | `1` / `LANREMOTE` |
| 单报文上限 | 2048 字节 |

- **announce**：每 2 秒（含启动时立即一次）向 `239.255.77.77:45872` 发送（每个 binding 各一次）。
- **probe**：组播 probe **+ 每张合格网卡的 directed broadcast probe**（例如 `192.168.1.255:45872`）。
  启动后自动发一次；UI「刷新」按钮也只发 probe。
- **收到 probe**：来源先过 RFC1918 + 同子网；若 `AllowDiscovery == true`，
  用 **unicast** 回一个正常 announcement（对方之所以发 directed broadcast，往往就是收不到组播）。
  **回应目标 = `remote.Address : 45872`**（M2.1 修复）：
  probe 的来源端口是对方的随机临时端口（如 `192.168.1.20:53742`），绝不能作为回应目标端口；
  回应固定打到 `192.168.1.20:45872`。详见 §1.5.1。
- 协议端口护栏：`DiscoveryConstants.ExpectedTransportPort == TransportConstants.Port`（测试守护，防漂移）。

## 7. AllowDiscovery=false 的准确语义

| 行为 | AllowDiscovery = true | AllowDiscovery = false |
|---|---|---|
| 主动 announce | ✅ | ❌ |
| 回应别人的 probe | ✅ | ❌ |
| 接收别人的 announcement | ✅ | ✅（列表继续显示其他设备） |
| 主动 ProbeAsync 扫描 | ✅ | ✅（「允许被发现」不是「关闭扫描」） |

实现上：`DiscoveryRuntimeState` 保存不可变快照，`AllowDiscovery` 变化时
`UpdateFromConfig` 更新快照，下一次 announce / probe 响应立即使用新值——
**不重启 UDP 服务、不重读 config.json、不 stop/start socket**。

远端在 TTL（约 7 秒）后把本机移出列表；重新开启后最多约 2 秒再次出现。

## 8. WatchAsync 的 upsert-only 语义

`WatchAsync` 的返回类型未改。它只推送「在线设备的插入或更新」，
**不会**发送任何「Removed」假设备。UI 必须自己按 `DiscoveredDevice.LastSeen` 做 TTL prune。
这是当前 Core API 的既定语义，M2 不推翻（另见 ADR-023）。

- **cache**：`DiscoveryDeviceCache`，key = `DeviceId`（一台物理设备多 NIC 也只在 UI 出现一条；
  同 DeviceId 换 IP 时 v1 采用最新有效 endpoint），
  **容量上限 256**，满时先清过期再淘汰最旧，绝不无界。
- **更新队列**：`Channel<DiscoveredDevice>`，容量 **512**，`FullMode = DropOldest`。
- **离线 TTL**：内部缓存 7 秒；清理循环每 1 秒检查一次。
- **UI TTL removal**：`MainViewModel` 起一个 1 秒的 `PeriodicTimer` 清理循环，
  在 UI 线程从 `ObservableCollection` 删除 `LastSeen <= now - 7s` 的条目；
  窗口/应用退出时该循环随 `CancellationTokenSource` 停止，不用永久 `DispatcherTimer`。

## 9. 手工测试

### Two-machine manual discovery: **PASS**（2026-09-20 17:30–18:18，20/20）

> **这一项已从 NOT RUN 变为 PASS。** 下面第 9.1 节是完整的回填记录
> （时间线 + 20 步逐条结论 + 两台机器的证据）。
> M2.1 修的两件事（probe 回应端口、组播出口网卡）都在这次实测中拿到真实链路证据。

（保留原始说明：）单元测试无法替代两机验收——尤其本轮修的两件事
（probe 回应端口、组播出口网卡）本身就是「只有真实多网卡/多机环境才暴露」的问题，
纯单测只能覆盖端口计算与选项值构造，覆盖不了真实收发路径。

原因（两条，各自都足够）：

1. **只有一台物理测试机**；
2. **本机以太网的 `172.100.166.220` 不属于 RFC1918**
   （172.16.0.0/12 只覆盖 172.16–172.31）。两个「本地连接*」为「媒体已断开连接」，
   只有 APIPA `169.254.x.x`。
   > 2026-09-20 17:10 复测更正：WLAN 此时已连接并拿到 `10.65.156.134/24`（Dhcp，**是** RFC1918
   > 私有地址），所以本机现在**存在**合格网卡。但电脑 B 不在 `10.65.156.x` 网段，
   > 两机仍然互不可见 —— 结论不变：**必须**靠 `set-lab-ip.ps1` 造出双方共享的 `192.168.1.0/24`。

### 9.1 两机验收回填（2026-09-20，PASS）

#### 环境

| | 电脑 A（本机，由施工环境操作） | 电脑 B（由用户操作） |
|---|---|---|
| 机器名 | `DESKTOP-D132BMD` | `DESKTOP-CU2263D` |
| 设备码 | `M5WC-14GX` | `3ERD-R74V` |
| 证书指纹前缀 | `89A5C10E` | `6755838E` |
| 物理网卡 | Realtek PCIe GbE | Realtek Gaming 2.5GbE |
| 原有 IPv4 | `172.100.166.220`（**非** RFC1918） | `172.100.166.65`（**非** RFC1918） |
| lab 地址 | `192.168.1.10/24` | `192.168.1.20/24` |
| 解压目录 | `C:\DIYTools\LanRemote-0.1.0-m2-win-x64` | `D:\DIYTool\LanRemote-0.1.0-m2-win-x64` |

两台都在同一条物理网线上；`172.100.166.x` 不是私有网段，靠 `set-lab-ip.ps1` 造出共享的 `192.168.1.0/24`。

#### 关键时间线（A 机 `lanremote-20260920.log` 原文摘录）

```
17:30:42.424 [Information] 局域网发现启动。有效网卡=1，地址=以太网:192.168.1.10
17:30:42.444 [Debug     ] 丢弃发现报文：本机自公告（累计=1）          ← 第 5 条：不列自己
17:51:03.892 [Debug     ] 已回应来自 192.168.1.20 的 probe：unicast → 192.168.1.20:45872（不使用源端口 54670）。
17:51:03.896 [Information] 发现设备 DESKTOP-CU2263D（3ERD-R74V）于 192.168.1.20，能力=view,control。   ← 第 3 条
17:57:33.435 [Information] 设备离线：DESKTOP-CU2263D（3ERD-R74V）192.168.1.20。   ← 第 8 条（B 退出后 ~7 s）
18:01:15.999 [Information] 发现设备 DESKTOP-CU2263D（3ERD-R74V）于 192.168.1.20，能力=view,control。   ← 第 10 条
18:02:13.438 [Information] 设备离线：DESKTOP-CU2263D（3ERD-R74V）192.168.1.20。   ← 第 12 条（B 关广播后 TTL 到期）
18:04:07.620 [Debug     ] 已发送 probe：组播 + 1 个定向广播。        ← 第 17 条（A 点刷新）
18:04:13.550 [Debug     ] 已回应来自 192.168.1.20 的 probe：unicast → 192.168.1.20:45872（不使用源端口 58869）。  ← 第 18 条
18:04:23.990 [Information] 发现设备 DESKTOP-CU2263D（3ERD-R74V）于 192.168.1.20，能力=view,control。   ← 第 16 条
```

#### 20 步逐条结论

| # | 验收项 | 结果 | 证据 |
|---|---|---|---|
| 1 | A 启动 | ✅ | `17:30:42.424 局域网发现启动。有效网卡=1，地址=以太网:192.168.1.10` |
| 2 | B 启动 | ✅ | B 侧日志 `18:01:16.419 本机身份就绪…DESKTOP-CU2263D`；`discovery started=2` |
| 3 | A 看到 B | ✅ | `17:51:03.896` 发现设备（B 启动后约 5 s） |
| 4 | B 看到 A | ✅ | 用户在 B 机界面确认；B 侧 `device discovered=2` |
| 5 | 自己不出现在自己列表 | ✅ | `丢弃发现报文：本机自公告（累计=1024）` |
| 6 | 20 s 内无重复/抖动 | ✅ | 40 s 窗口内「发现设备」仅 1 次 |
| 7 | B 退出 | ✅ | 用户关闭 B 机 LanRemote |
| 8 | A 约 7~9 s 后移除 B | ✅ | `17:57:33.435` 设备离线（关闭后 ≈7 s，符合 TTL 7000 ms + 清理 1000 ms） |
| 9 | B 重启 | ✅ | `18:01:1x` |
| 10 | B 再次出现 | ✅ | `18:01:15.999` |
| 11 | B 关闭 `AllowDiscovery` | ✅ | 用户在 B 机取消勾选「允许被发现」 |
| 12 | A 在 TTL 后移除 B | ✅ | `18:02:13.438` 设备离线 |
| 13 | B **仍**能看到 A | ✅ | 用户在 B 机界面确认 |
| 14 | B 点刷新**仍**能 probe 到 A | ✅ | 用户在 B 机界面确认 |
| 15 | B 再打开 `AllowDiscovery` | ✅ | `18:06:33` |
| 16 | A 再次发现 B | ✅ | `18:04:23.990`；此后约 3 分钟 **0 次**「设备离线」（TTL 仅 7 s，收不到广播早该掉→证明广播已恢复且稳定） |
| 17 | A 点刷新 | ✅ | `18:04:07.620 已发送 probe：组播 + 1 个定向广播` |
| 18 | B 正常响应 | ✅ | `18:04:13.550` / `18:04:23.277` 收到 `192.168.1.20` 的 probe 并回应 |
| 19 | 两边无崩溃 | ✅ | A：pid 43572 从 `17:30:41` 连续运行至 `18:18` 被主动停止，无异常退出；B：全程在 A 列表可见且可响应 |
| 20 | 日志无 Access Key | ✅ | A、B 两侧 `check-logs.ps1` 均 `matches : 0` + `VERDICT: PASS` |

**M2.1 两个头条修复的真实链路证据：**

- **§1.5.1 probe 回应端口**：三次独立出现「不使用源端口」——`50193`（16:10 那轮）、
  `54670`、`58869`。源端口都是随机临时端口，若按 v1 回源端口则对端必然收不到。
- **§1.5.2 组播出口网卡**：B 机同时挂了 5 张带私有地址的网卡（以太网 `192.168.1.20`、
  以太网 3 `172.19.83.237`、VMnet1 `192.168.11.1`、VMnet8 `192.168.119.1`、
  本地连接* 10 `192.168.137.1`），A 只在 `192.168.1.20` 上收到 B，
  **没有出现跨网卡混淆**，也没有 `AddressNotAvailable`。

#### 未覆盖 / 需注意（如实标注，不算通过）

- **「Ethernet + Wi-Fi 同一 subnet」组合未测** —— 两台这次都是有线以太网。
- **第 19 条 B 侧**：用户未逐字回复「无崩溃」，结论由「全程在 A 列表可见、点刷新可响应」佐证，
  不是直接确认。若要严格化，需补一次 B 侧进程存活确认。
- **验收后两台均已回滚**：`-Undo` 均 `EXIT=0`，A 恢复 `172.100.166.220/24 Dhcp`
  （网关 `172.100.166.254`、ping 通、无 lab 地址残留），B 恢复 `172.100.166.65/24 Dhcp`。

#### 本轮实测暴露的验收脚本缺陷（5 个，全部已修并提交）

| # | 缺陷 | 后果 | 修复 |
|---|---|---|---|
| 1 | `set-lab-ip` v1 假设「DHCP 地址 + 附加静态地址」可共存 | `-Undo` 后电脑 A 只剩 APIPA、无网关/DNS | v2 整口切静态（保留原配置）再追加 |
| 2 | 只看 `netsh` 退出码 | 「已是 DHCP」时 netsh 返回非 0 被误判为失败 | 改用 `Get-NetIPInterface` 复核状态 |
| 3 | `($x \| ForEach-Object { $_.IPAddress } -join ', ')` | PS 5.1 把 `-join` 当参数 → 抛异常（语法检查查不出） | `$x.IPAddress -join ', '` |
| 4 | 自动选网卡未排除虚拟网卡 | B 机把 `192.168.1.20` 加到 **VMnet1** 上，A 完全收不到 | `Test-VirtualAdapter` 过滤 + 优先有默认网关的物理网卡 + 选中后告警 |
| 5 | `check-logs.ps1` 第 93 行括号不匹配；判定不区分时间窗口 | 脚本直接崩；且把配 lab IP **之前**的 5 条历史「无合格网卡」算进来误报 FAIL | 补括号；新增 `-Since` 参数 |

> 教训：这些缺陷**没有一个能被"读代码"发现**，全是真跑才暴露。
> 与 §1.5.2（IP_MULTICAST_IF 实测）是同一条纪律：Windows 行为必须实测。

### 已实际执行的单机验证

- [x] **应用启动 + 发现服务启动**：日志显示
  `没有找到任何合格的私有 IPv4 网卡，局域网发现不会发送也不接收。`（对本机环境是正确结论），
  应用无崩溃、M1 身份照常加载（设备码 `M5WC-14GX`、指纹前缀 `89A5C10E`）。
- [x] **真实 socket 接收 + 来源过滤（早期一次构建上验证）**：
  用脚本向本机 45872 发送 1 个合法 announcement 与 2 个畸形报文，
  日志出现 3 条 `丢弃发现报文：来源地址非 IPv4 / 非 RFC1918 / 不在任何本机子网内（累计=1/2/3）`——
  证明 socket 确实收到数据、来源过滤在 JSON 之前生效、畸形报文不会让程序崩溃。
  > 该证据来自「无合格网卡时仍创建 receiver」的那一版构建；
  > 之后为避免无谓占用端口改成「无合格网卡则不开 socket」，因此最终构建上不再有接收日志。
- [ ] 真实 accept 路径（收到合法报文 → 进缓存 → 显示在 UI）：**未执行**
  ——本机没有任何 RFC1918 接口，无法构造可信来源。该路径由 189 条 Protocol 单元测试覆盖
  （codec + evaluator + cache + runtime state），但**没有**走真实 socket。
- [ ] UI 按钮点击：仍未做（无 UI 自动化框架），**不伪装成已验证**。

首次运行可能触发 Windows 网络访问提示：防火墙规则属于 M10，本轮**没有**在程序里自动改防火墙。

### 交给用户执行的两机验收清单（进入 M3 前的必需关卡）

条件：两台都跑最终 M2.1 build，且位于同一 RFC1918 IPv4 子网（例如 `192.168.1.10/24` 与 `192.168.1.20/24`）。

1. A 启动；2. B 启动；3. 3~6 s 内 A 看到 B；4. 3~6 s 内 B 看到 A；5. 自己不会看到自己；
6. 等 20 s 无重复设备；7. B 退出；8. A 在约 7~9 s 后移除 B；9. B 重启；10. B 再次出现；
11. B 关闭 `AllowDiscovery`；12. A 在 TTL 后移除 B；13. B **仍然**可以看到 A；
14. B 点刷新**仍然**能 Probe 到 A；15. B 再打开 `AllowDiscovery`；16. A 再次发现 B；
17. A 点刷新；18. B 应能正常响应；19. 两边应用均无崩溃；20. 日志无 Access Key。

补充（若环境允许）：一台 Ethernet + 一台 Wi-Fi 但同一 subnet 的组合要优先测；
若有一台机器同时具备 Wi-Fi + Ethernet，额外确认组播没有全部错误地走系统默认接口
（这正是 §1.5.2 修复的目标）。

> 验收结果请回填到第 9 节与第 1 节「是否满足完整 M2 DoD」。
> **未回填前不要进入 M3。**

### 首次实测失败记录（2026-09-20，NOT RUN 原因已实证）

**用户于 2026-09-20 15:52 完成首次两机实测：两台设备列表均为空，失败。**

现场数据：

| | 电脑 A（DESKTOP-D132BMD） | 电脑 B（DESKTOP-CU2Z63D） |
|---|---|---|
| IPv4 | `172.100.166.220` | `172.100.166.65` |
| 默认网关 | `172.100.166.254` | — |
| 链接速度 | 1000 Mbps | 1000 Mbps |
| 网卡 | Realtek PCIe GbE | Realtek Gaming 2.5GbE |
| 设备码 | `M5WC-14GX` | `3ERD-R74V` |

**根因（不是 LanRemote 的 bug）**：RFC1918 的 172 段只覆盖 `172.16`–`172.31`，
而这两台机器是 `172.100.166.x`（第二段 **100**），**落在范围外，属于公网地址段**。
`NetworkInterfaceSelector` 据此正确判定「没有合格的私有 IPv4 网卡」，
`LanDiscoveryService` 因此不开 socket → 列表为空。软件行为符合规格与 ADR-022。

两台机器网线是通的（1000 Mbps、同网段、可互通），问题纯粹在地址段本身。

**处置**：新增 `scripts/acceptance/set-lab-ip.ps1`，给两台机器各安排
`192.168.1.10/24`（A）与 `192.168.1.20/24`（B），同时把网卡配置文件设为 `Private`
并放行入站 UDP 45872；`-Undo` 可一键回滚。**未修改任何产品代码**，
安全约束（只认 RFC1918）保持原样。验收手册新增第 1.1 / 1.2 节记录本案例。

> 该脚本第一版（v1）按「保留 DHCP 地址 + 追加静态地址」实现，被实测证明前提错误，
> 已归档为 `_set-lab-ip.v1.broken.ps1.bak`。当前是 **v2**，细节见下一小节。

### 验收核心已实证：A 机日志中的双向发现记录（2026-09-20 16:10–16:16）

回滚事故中断验收之前，电脑 A 的 `%LOCALAPPDATA%\LanRemote\logs\lanremote-20260920.log`
**已经完整记录了 M2.1 两个头条修复的真实收发**。摘录（原文）：

```
16:10:15.717 [Information] 局域网发现启动。有效网卡=1，地址=以太网:192.168.1.10
16:10:25.396 [Debug]      已回应来自 192.168.1.20 的 probe：unicast → 192.168.1.20:45872（不使用源端口 50193）。
16:10:25.401 [Information] 发现设备 DESKTOP-CU2263D（3ERD-R74V）于 192.168.1.20，能力=view,control。
16:16:18.730 [Information] 设备离线：DESKTOP-CU2263D（3ERD-R74V）192.168.1.20。
16:16:19.378 [Information] 发现设备 DESKTOP-CU2263D（3ERD-R74V）于 192.168.1.20，能力=view,control。
```

据此可判定：

| M2.1 修复项 | 证据 | 结论 |
| --- | --- | --- |
| §1.5.1 probe 回应端口 | `不使用源端口 50193` → `192.168.1.20:45872` | **真实链路上成立**（源端口 50193 是随机的，若仍回源端口则 B 收不到） |
| §1.5.2 组播出口网卡 | 单网卡 + 绑定源地址后 announce/probe 正常互达 | 未出现 `AddressNotAvailable`，出口选择正确 |
| 发现 → 缓存 → UI | `发现设备` 与 `设备离线` 成对出现，且离线后重新出现 | TTL 移除 + 重新发现路径**真实可用** |

> 16:14 之后反复出现的 `发送组播 announce 到 239.255.77.77 失败（SocketError=AddressNotAvailable）`
> 是**回滚把网卡绑定的源地址弄丢之后**的后果（当时以太网只剩 APIPA），不是产品缺陷。
> 该进程（pid 37700，源地址已消失仍在跑）已手工终止。

**所以：M2.1 的技术结论已被真实环境支撑，缺口只剩「20 步完整走完并正式签字」。**
这一项仍是 **NOT RUN**——证据是**中途截取**的，不是按清单逐条验收的结果。

### set-lab-ip.ps1 v1 事故与 v2 重写（2026-09-20）

**事故**：用户在电脑 A 上跑 `set-lab-ip.ps1 -Role A`（报告成功）、`check-env.ps1`（PRE-CHECK PASSED）、
随后 `-Undo` —— 以太网**失去全部可用 IPv4**（仅剩 APIPA `169.254.194.126`，无网关无 DNS）；
第二次 `-Undo` 又抛出 `No IPv4 adapter found. Pass -InterfaceAlias explicitly.`。
已用 `Remove-NetIPAddress` + `Set-NetIPInterface -Dhcp Enabled` + `ipconfig /renew` 手工恢复，
复核 `172.100.166.220/24`、`Dhcp=Enabled`、网关 `172.100.166.254`、DNS `172.100.162.101/102` 均复原。

**根因（实测，不是推测）**：Windows IPv4 上「DHCP 地址 + 额外静态地址」**不能共存**。

| 操作 | 预期 | 实测 |
| --- | --- | --- |
| `New-NetIPAddress` 在 DHCP 接口上追加 | 共存 | 接口 `Dhcp` → `Disabled`，租约丢失 |
| `netsh interface ipv4 add address` 追加 | 共存 | 同上 |
| 追加后再删除 | 复原 | 只剩 APIPA，无网关/DNS |

（在定位过程中本机网络又断了一次，同样用 `netsh ... source=dhcp` + `set dnsservers source=dhcp`
+ `ipconfig /renew` 恢复。**教训与 §1.5.2 一致：Windows 网络行为必须实测，不能凭直觉断言。**）

**v2 改法**：整口切静态 —— 先把当前 IP/掩码/网关/DNS 存盘到
`%TEMP%\lanremote-lab-ip-state.json`，再用**同一套配置**切成静态（不断网），
最后追加 `192.168.1.x/24`（不带网关）。`-Undo` 移除 lab 地址、切回 DHCP、`ipconfig /renew` 并自检。

**v2 实测（本机，Windows PowerShell 5.1）**：

| 场景 | 结果 |
| --- | --- |
| `-Role A` | `EXIT=0`；以太网 `172.100.166.220` Manual + `192.168.1.10` Manual 共存 |
| 切静态后连通性 | `ping 172.100.166.254` = True；DNS `172.100.162.101/102` 保留；默认路由仍在 |
| `-Undo` | `EXIT=0`；回到 `172.100.166.220/24 Dhcp`；`OK:` 自检通过 |
| 已是 DHCP 时再 `-Undo`（幂等） | `EXIT=0`（v1 在此场景崩溃） |

**v2 修掉的两个 bug**（都是实测才暴露）：

1. **不能只看 `netsh` 退出码** —— 在已是 DHCP 的接口上 `set address source=dhcp`，
   netsh 返回**非 0** 并输出「已在此接口上启用 DHCP。」，实为成功。改为**用
   `Get-NetIPInterface` 复核实际状态**，退出码仅供参考。
2. **`($x | ForEach-Object { $_.IPAddress } -join ', ')` 在 PS 5.1 会把 `-join` 当成
   `ForEach-Object` 的参数**并抛异常（语法检查查不出来）。已改为 `$x.IPAddress -join ', '`。

附带修正：`Set-NetConnectionProfile` 在刚切完静态时会因网卡处于 `Identifying...` 而失败，
现改为最多 5 次、间隔 3 s 重试。（即便失败也不阻断——防火墙规则是 `Profile Any`，
不依赖网络配置文件。）

### 验收物料（已就绪，等用户重测）

| 项 | 位置 |
|---|---|
| 两机验收手册（20 步 + 排查表 + 回填模板） | `docs/TWO_MACHINE_ACCEPTANCE.md` |
| 便携版验收包（自包含 win-x64，解压即用，目标机无需装运行时） | `scripts/acceptance/make-package.py` 生成 `LanRemote-0.1.0-m2-win-x64.zip` |
| 验收前环境自检脚本 | `scripts/acceptance/check-env.ps1` |
| 验收后日志检查 / 访问密钥泄漏扫描脚本 | `scripts/acceptance/check-logs.ps1` |
| 私有实验网段配置 / 回滚脚本（**v2**） | `scripts/acceptance/set-lab-ip.ps1`（v1 缺陷版已归档为 `_set-lab-ip.v1.broken.ps1.bak`） |

包内自带 `START-HERE.md`（即验收手册）、`check-env.ps1`、`check-logs.ps1`、`set-lab-ip.ps1`。
两个 `.ps1` 刻意存为 **UTF-8 with BOM**，否则 Windows PowerShell 5.1 会把中文按 ANSI 解析成乱码。

## 10. 修改文件

**新增（Discovery 项目）**

| 路径 | 说明 |
|---|---|
| `Networking/Ipv4Math.cs` | 地址/掩码数学、directed broadcast、连续前缀掩码校验 |
| `Networking/PrivateIpv4.cs` | RFC1918 / loopback / link-local 判定 |
| `Networking/NetworkBinding.cs` | `NetworkBinding` / `Ipv4UnicastAddress` / `NetworkInterfaceSnapshot` |
| `Networking/INetworkInterfaceSource.cs`、`SystemNetworkInterfaceSource.cs` | 网卡枚举抽象与真实实现 |
| `Networking/VirtualAdapterFilter.cs` | 虚拟/VPN 适配器关键词过滤 |
| `Networking/NetworkInterfaceSelector.cs` | 最终筛选规则 |
| `Networking/INetworkBindingProvider.cs`、`LocalNetworkBindingProvider.cs` | 绑定快照（含 `Refresh()` 供 M9 热插拔） |
| `Networking/SubnetPolicy.cs` | `ISubnetPolicy` 实现 |
| `Networking/SourceEndpointFilter.cs` | JSON 之前的来源地址准入 |
| `Protocol/DiscoveryPackets.cs` | announcement / probe DTO（无地址字段） |
| `Protocol/DiscoveryPacketCodec.cs` | camelCase JSON、长度上限、JsonException 吞掉 |
| `Protocol/DiscoveryAnnouncementEvaluator.cs` | 全项严格校验 + 地址取自 source |
| `DiscoveryRuntimeState.cs` | 不可变快照 + 线程安全状态 |
| `DiscoveryDeviceCache.cs` | 有界缓存 + TTL |
| `LanDiscoveryService.cs` | UDP 收发与三个后台循环 |

**修改**

| 路径 | 说明 |
|---|---|
| `src/LanRemote.Discovery/DiscoveryConstants.cs` | 新增 type 常量、`ExpectedTransportPort`、nonce/名称/capability 限制、缓存与队列容量 |
| `src/LanRemote.Discovery/LanRemote.Discovery.csproj` | 新增 `Microsoft.Extensions.Logging.Abstractions` |
| `src/LanRemote.Core/Abstractions/IDiscoveryService.cs` | **唯一一处 M2 API 扩展**：新增 `ProbeAsync`，Start/Stop/Watch 签名未改 |
| `src/LanRemote.Core/Infrastructure/AppVersion.cs`（新增） | 从程序集读版本，供 announcement 的 `appVersion` |
| `src/LanRemote.App/App.xaml.cs` | DI 注册；`OnExit` 先 `StopAsync` discovery 再停宿主 |
| `src/LanRemote.App/ViewModels/MainViewModel.cs` | 发现集成、UI 线程更新、TTL 清理、Refresh |
| `src/LanRemote.App/MainWindow.xaml` / `.xaml.cs` | 真实设备列表、刷新按钮、disabled 的连接按钮 |
| `Directory.Build.props` | 版本 `0.1.0-m1` → `0.1.0-m2` |

### M2.1 本轮改动

**新增**

| 路径 | 说明 |
|---|---|
| `src/LanRemote.Discovery/Protocol/DiscoveryReplyTarget.cs` | internal 纯函数：probe 回应目标 = 源地址 + 固定端口 45872 |
| `src/LanRemote.Discovery/Networking/MulticastInterfaceOption.cs` | internal 纯函数：`IP_MULTICAST_IF` 选项值（接口 IPv4 的网络序 4 字节） |
| `src/LanRemote.Discovery/AssemblyInfo.cs` | `InternalsVisibleTo("LanRemote.Protocol.Tests")`，仅为测试开放上述两个 internal 类型 |
| `tests/LanRemote.Protocol.Tests/DiscoveryReplyTargetTests.cs` | 回应端口回归测试（含 `ProbeReply_AlwaysTargetsDiscoveryPort_NotSourcePort`） |
| `tests/LanRemote.Protocol.Tests/MulticastInterfaceOptionTests.cs` | 选项值构造 + 真实 socket 上设置不抛异常 |
| `docs/TWO_MACHINE_ACCEPTANCE.md` | 两机验收手册（20 步 + 排查表 + 结果回填模板） |
| `scripts/acceptance/check-env.ps1` | 验收前环境自检（RFC1918 判定 / 端口占用 / 网络配置文件 / 残留进程） |
| `scripts/acceptance/check-logs.ps1` | 验收后日志检查 + 访问密钥泄漏扫描（命中只打掩码，不回显真实密钥） |
| `scripts/acceptance/set-lab-ip.ps1` | 追加/移除私有 lab IPv4（`-Role A\|B` / `-Undo`）；本机网络非私有段时必须先跑 |
| `scripts/acceptance/make-package.py` | 把 publish 产物打成便携版验收 zip |
| `scripts/acceptance/add-bom.py` | 给两个 `.ps1` 加 UTF-8 BOM（PS 5.1 否则乱码） |

**修改**

| 路径 | 说明 |
|---|---|
| `src/LanRemote.Discovery/LanDiscoveryService.cs` | ① 回应目标改用 `DiscoveryReplyTarget.ForProbe(remote)`；② sender 增加 `MulticastInterface`；③ socket 创建失败时 `cts.Dispose()`；④ probe 回应成功时补一行 Debug 日志（§1.5.6） |
| `src/LanRemote.Discovery/Protocol/DiscoveryAnnouncementEvaluator.cs` | capabilities：raw 数量先判上限、空白/null 拒绝、控制字符在 Trim 前拒绝 |
| `src/LanRemote.App/ViewModels/MainViewModel.cs` | `StartDiscoveryAsync` 改返回 `bool`；`LoadAsync` 只在成功时写正常状态 |
| `tests/LanRemote.Protocol.Tests/DiscoveryAnnouncementEvaluatorTests.cs` | 删掉语义错误的 `Capabilities_AreDedupedAndEmptiesRemoved`，换成 6 组新测试 |

**未改动**：M1 全部安全实现（`DpapiSecretVault`、`DpapiAccessSecretStore`、`SecretGenerator`、
`DeviceCertificateService`、`DeviceIdentityService`、`CrockfordBase32`、`DeviceCode`）。
尤其没有重新引入 `PersistKeySet` / `Exportable` / `KeyEncipherment`。

## 11. 构建

```text
source scripts/env.sh
dotnet build LanRemote.sln -c Debug
```

**M2.1 真实执行结果：PASS** —— 12 个项目全部生成，**0 个警告，0 个错误**（耗时 00:00:12.15）

**M3 第 24 步「先修再跑」后重新执行：PASS —— 0 个警告，0 个错误**（耗时 00:00:12.71）。
本轮产品代码只加了 `TlsConnection.LocalEndPoint`（只读），验收器改动见 §15 步骤 24。

## 12. 测试

```text
dotnet test LanRemote.sln -c Debug --no-build
```

**M2.1 真实执行结果：408 passed / 0 failed / 0 skipped**
**M3 阶段 5 后：573 passed / 0 failed / 0 skipped**
**M3 第 24 步「先修再跑」后：574 passed / 0 failed / 0 skipped**（本轮 +1）

| 项目 | M1.3 后 | M2 后 | M2.1 后 | M3 阶段 5 后 | 本轮后 |
|---|---:|---:|---:|---:|---:|
| LanRemote.Core.Tests | 125 | 125 | 125 | 125 | 125 |
| LanRemote.Security.Tests | 65 | 65 | 65 | 66 | 66 |
| LanRemote.IntegrationTests | 3 | 3 | 3 | 3 | 3 |
| LanRemote.Protocol.Tests | 7 | 189 | 215 | 215 | 215 |
| LanRemote.Transport.Tests | 0 | 0 | 0 | 164 | **165** |
| **合计** | **200** | **382** | **408** | **573** | **574** |

既有 408 条全部继续通过（**没有删除任何测试换取通过**）。
唯一被替换的测试是 `Capabilities_AreDedupedAndEmptiesRemoved`——
它的语义（空 capability 被静默移除后仍接受整条报文）与 M2.1 收紧后的规格直接冲突，
按规格要求重写为 6 组更严格的新测试。

**本轮新增的那 1 条**：

| 测试 | 断言 |
|---|---|
| `Connect_Reports_Local_End_Point_Matching_Server_Observation` | 客户端自报的 `LocalEndPoint` 必须与服务端 accept 时看到的 `RemoteEndPoint` **同一**（地址 + 端口），且释放后返回 `null` 而不是抛异常 |

> 为什么「只是个观测量」也要对侧断言：只断言非空的话，把它实现成「返回任意本地端口」
> 测试照样绿。而错的配对键会让两份日志**配错行**，比没有配对键更坏。
> 为此给测试用 `TestTlsServer` 加了 `AcceptedRemoteEndPoints`（accept 循环里记录远端端点）。

**M2.1 那一轮新增的 26 条测试**（保留，供追溯）：

| 测试 | 断言 |
|---|---|
| `ProbeReply_AlwaysTargetsDiscoveryPort_NotSourcePort` | 源 `192.168.1.20:53742` → 目标 `192.168.1.20:45872`，且 `NotEqual(53742)` |
| `ProbeReply_IgnoresAnySourcePort`（5 组） | 任意源端口（含 45871/45873 这两个最容易被写错的邻近端口）都不影响目标端口 |
| `ProbeReply_KeepsSourceAddressOnly` | 只有地址来自报文，端口恒为常量 |
| `ProbeReply_RejectsNull` / `RejectsNonIpv4` | 入参防御 |
| `Capabilities_RawCountAboveLimit_IsRejectedEvenWhenDuplicates` | 17 个全重复的 `view` → 拒绝（去重后是 1 项也不能放过） |
| `Capabilities_WhitespaceEntry_IsRejected`（5 组） | `""` `" "` `"   "` `"\t"` `"\r"` → 拒绝 |
| `Capabilities_NullEntry_IsRejected` | `null` → 拒绝 |
| `Capabilities_ControlCharacter_IsRejected`（4 组） | `"evil\nfake-log"` `"abc\rxyz"` `"abc\txyz"` `"view\n"` → 拒绝 |
| `Capabilities_ValidDuplicates_AreDeduped` | 合法重复项去重；`" control "` Trim 后生效 |
| `Capabilities_ExactlyMaxEntries_IsAccepted` | 恰好 16 项 → 接受 |
| `MulticastInterfaceOptionTests`（5 条） | 选项值 = 网络序 4 字节；不同 binding 产出不同值；拒绝 null/非 IPv4；**真实 socket 上设置不抛异常并回读一致** |

新增测试文件（都在 `LanRemote.Protocol.Tests`，未新建第五个测试项目）：

| 文件 | 覆盖 |
|---|---|
| `PrivateIpv4Tests.cs` | §46 RFC1918 边界（10/172.16/192.168 上下界）、127/169.254/公网、§47 directed broadcast 数学、掩码连续性 |
| `SubnetPolicyTests.cs` | §8 全部 8 组数据驱动用例；公网/169.254/IPv6/未知 local/null；「都私有但不同网段」；多网卡只匹配对应 binding |
| `NetworkInterfaceSelectorTests.cs` | §48 全部 10 类场景（Up/Down/无线/公网/169.254/禁类型/虚拟VPN/无掩码/一卡多地址/混合）＋真实 OS 枚举不抛异常 |
| `DiscoveryPacketCodecTests.cs` | roundtrip、camelCase、§49 的畸形输入（空/随机字节/非法 UTF-8/截断 JSON/`{}`/非对象/超大）、nonce、§9 端口护栏 |
| `DiscoveryAnnouncementEvaluatorTests.cs` | §13 全项校验、§50 来源地址权威、§51 自公告丢弃、未知 capability 放行、能力去重与上限 |
| `DiscoveryDeviceCacheTests.cs` | §52 add/update/换 IP 仍一条/过期/未过期/容量有界/满时先清过期 |
| `DiscoveryRuntimeStateTests.cs` | §53 AllowDiscovery 决策、Initialize 前拿不到数据、capabilities 不提前宣布 multi-monitor |
| `LanDiscoveryServiceLifecycleTests.cs` | §33 Start/Stop 幂等、无网卡不抛、Stop 后 WatchAsync 自然结束 |

## 13. ADR

`docs/DECISIONS.md` 新增：

- **ADR-022 — Discovery transport**：IPv4 only；UDP 组播 + directed broadcast probe；
  无云端/NAT 穿透；source IP 权威；报文一律不可信；announce 不是认证依据。
- **ADR-023 — WatchAsync upsert-only + UI TTL prune**：
  明确「不发送 Removed 假设备，UI 按 LastSeen 自 prune」，避免下一位 AI 在 M2 临时推翻现有 Core API。

### 2026-09-20 追加：产品形态决策（用户拍板「三个都做，含改 IP 一键」）

`docs/DECISIONS.md` 新增三条。**只定规则与验收口径，当前不启动编码**：

- **ADR-024 — 网络诊断必须进 UI**：`bindings.Count == 0` 不能再只写日志；UI 必须给出
  「原因 + 网卡名 + 实际地址」，至少覆盖 `05_UI_UX_SPEC.md` §8 的「未发现设备 / 防火墙阻止 / 网络断开」。
  实施时机 **M9**，最晚 M10 发布前闭合。
- **ADR-025 — 防火墙放行内置 App + UAC**：一键按钮 + UAC 提权；
  `configure-firewall.ps1` / `remove-firewall.ps1` **降级为可选离线入口**；规则仍只放行 LocalSubnet，
  且必须自带「按前缀精确撤销」契约。实施时机 **M10**。
- **ADR-026 — 临时私有地址一键：显式 / 确认 / 可撤销，禁止静默自动改 IP**：
  用户显式选网卡 + 排除虚拟网卡 + 先整张切静态再追加 + 撤销回 DHCP 并自校验。
  实施时机 **不早于 M10**。

**同时修掉一处编号缺陷**：ADR-016 声明「SUPERSEDED by ADR-018」，但那条记录被错标成了
「ADR-021（Key Usage）」，导致 ADR-018 实际不存在。已改回 **ADR-018**，并在条目顶部加了修正说明；
真正的 Key Usage 决策仍是 ADR-021，两者不要合并。

**M2.1 本轮未新增 ADR**：修的都是既有决策下的实现缺陷，没有推翻或新增架构决策。

### 2026-09-21 追加：M3 红队评审产出两条 ADR

外部模型对 M3 设计做了红队评审，分流结果见 **`docs/M3_REVIEW_TRIAGE.md`**。据此新增：

- **ADR-027 — 发现层身份冲突不得静默 last-write-wins**：`DiscoveryDeviceCache` 是
  `Dictionary<Guid,...>` + `Upsert` 覆盖式更新（读代码确认），同 deviceId 换指纹会静默覆盖。
  **M3 不动 M2 已验收行为**（只用连接目标不可变快照止血）；冲突语义最晚 **M4** 落地：
  同 deviceId + **不同指纹** → `IdentityConflict` 并禁用连接；同 deviceId + **同指纹** + 不同 IP
  是多网卡良性广播，**不得误杀**。
- **ADR-028 — M3→M4 身份绑定契约**：连接上下文必须不可变携带
  `{deviceId, endpoint, expectedPin, presentedPin}`；**M4 的 transcript 必须绑定 `presentedPin`**
  （M3 实际出示证书的指纹），只绑 `expectedPin` 不够——否则存在凭据中继缺口。
  `presentedPin` 是 **M3 的交付物**。

### 2026-09-21 追加：ADR-018 风险已实测收口 → **ADR-029**

- **ADR-029 — 证书加载改用 `X509KeyStorageFlags.Default`（0）**，同时作废 ADR-016 与 ADR-018。
- 实测矩阵（真实 `SslStream` server/client，loopback，3 flag × 3 协议 × 重复 3 次）：
  - `EphemeralKeySet`：**9/9 FAIL**。服务端 `AuthenticationException: ... platform does not support
    ephemeral keys.` ← `Win32Exception: 安全包中没有可用的凭证（0x8009030E）`；
    客户端只看到 `IOException: unexpected EOF`。
  - `PersistKeySet`：9/9 OK，但**磁盘留下持久密钥副本**（`%APPDATA%\Microsoft\Crypto\Keys` 文件数
    dispose+GC 后仍 +1）。
  - `DefaultKeySet`(0)：9/9 OK，且密钥文件在 dispose/GC 后**删除**。
    （`X509KeyStorageFlags` 没有 `Default` 成员，正确名字是 `DefaultKeySet`，值同为 0。）
- **⚠️ 污染陷阱**：同一进程先 `PersistKeySet` 导入过同一私钥后，再 `EphemeralKeySet` 导入 → **握手成功**。
  所以「测试里没报错」不等于 flag 可用；结论必须在新进程、顺序受控下测。
- **M3 第一步**：改 `ImportFlags` → `Default`，**并改掉锁住旧选择的测试**
  `DeviceCertificateTests.ImportFlags_UsesEphemeralKeySetOnly`，然后跑通真实握手集成测试。
两条不变量已就近写进代码注释与类型 XML doc（`DiscoveryReplyTarget`、`MulticastInterfaceOption`），
并由单元测试锁住。

### 2026-09-21 追加：ADR-029 被再次收紧 → **ADR-030**

- **ADR-030 — `CertificateRequest.CreateSelfSigned()` 直接产出的证书，私钥是 ephemeral 的，
  不能用作 Windows TLS 服务端凭据；必须经「导出 PFX → `DefaultKeySet` 导入」往返。**
- 实测：服务端在 `AcquireCredentialsHandle` 处抛
  `AuthenticationException: Authentication failed because the platform does not support ephemeral keys.`
  ← `Win32Exception 0x8009030E`；客户端只看到 `IOException: unexpected EOF`。
- **关键推论**：这与 ADR-029 的 `EphemeralKeySet` 失败是**同一个错**。说明 Schannel 拒绝的是
  **密钥的 ephemeral 属性本身**，而不是某个导入 flag 的名字。
  ADR-029 改 flag 只是必要条件；`DeviceCertificateService` 里那步 PFX 往返**不是冗余代码**。
- 守护测试：`SelfSignedKeyEphemeralTests.CreateSelfSigned_Certificate_Cannot_Serve_Tls_Without_Pfx_Roundtrip`。

## 14. 已知问题 / 技术债

1. ~~**M2 手工 DoD 未完成**~~ —— **已于 2026-09-20 两机验收 PASS（20/20）关闭**，见第 9.1 节。
   只有改动 discovery 收发逻辑（网卡筛选 / probe / announce / 缓存 TTL）才需要重跑。
2. **本机开发环境无法验证发现**（未变）：唯一活跃网卡是 `172.100.166.220`（公网段）。
   若将来要**单机**验证，需要改用 192.168 / 10.x 的网络，或另开实验性开关（当前不做）。
   两机验证请直接按 §9.1 用 `set-lab-ip.ps1` 搭临时私有地址。
2a. ~~**组播出口网卡只验证到 socket option 回读**~~ —— **两机验收已证伪风险关闭**：
   B 机带 5 张私有网卡，A 机只在 `192.168.1.20` 收到 B，无跨网卡串扰、无 `AddressNotAvailable`。
3. **网卡热插拔未处理**：`StartAsync` 做一次快照并保持运行期不变；`INetworkBindingProvider.Refresh()`
   已预留，M9 再接 `NetworkChange`。
4. **虚拟网卡过滤是启发式**：可能误杀名字里带 `tap`/`vpn` 的真实网卡；需要 VPN LAN 时应另开实验设置。
5. **真实 accept 路径未走真实 socket 验证**（M3 范围，第 15 节）。
6. UI 交互无自动化覆盖（累计遗留）。
7. 日志无轮转（M9，ADR-013）。
8. `LanRemote.Sessions` / `Capture` / `Input` 仍是空项目占位。
9. **UI 网络诊断缺失（规格违反，待修）**：`05_UI_UX_SPEC.md` §8 要求区分「未发现设备 / 防火墙阻止 /
   网络断开」，当前 discovery 启动失败只写日志，UI 仍显示「已从磁盘加载配置与本机身份」。
   已立 **ADR-024**，实施时机 **M9**，最晚 M10 发布前闭合。
10. **防火墙交付形态待改**：M10 原任务只有 `configure-firewall.ps1` / `remove-firewall.ps1`，
    等于要求终端用户开管理员 PowerShell。已立 **ADR-025**：改为 App 内一键 + UAC，ps1 降级为可选离线入口，
    且必须有「按前缀精确撤销」契约。实施时机 **M10**。
11. **本机开发残留**（非阻断）：`%LOCALAPPDATA%\LanRemote\backups\secrets.bin.pre-m1.3-reset.bak`
    是 M1.3 手工重置身份时留的旧开发备份，确认不再需要后由施工环境手工删除。
    **硬约束：绝不把「自动删除身份备份」写进产品逻辑。**
12. **`TransportHost` 完全没有 logger（可观测性缺口，2026-09-21 建 M3 验收器时发现）**：
    构造函数不收 `ILogger`，三条拒绝路径全是静默 `return`——
    ① 同子网校验失败（`HandleAsync` 第 260 行）、② 准入限额拒绝（第 266 行）、
    ③ TLS 握手失败（第 300 行块）。后果：被控端对「谁被拒了、为什么拒」一无所知，
    两机验收时**只能靠控制端输出**判定。
    与之相对，`ControlPreAuthSession` 的超时（发生在会话处理器**内部**）是有输出的
    （`rejection=pre-auth-timeout`）——所以缺口精确落在 `TransportHost` 这一层，不含 pre-auth 会话层。
    与第 9 条（UI 诊断）同源，建议并入 **ADR-024 / M9** 一起做，M3 不为它改产品代码。
13. **跨子网拒绝没有真机覆盖**：现有 lab 是两机同挂 `172.100.166.x` + `192.168.1.x`，
    host 只听 RFC1918 绑定，而 Windows 会自动挑同子网源地址，做不出「源 IP 在另一子网」的样本；
    `New-NetRoute` / `route add` 都不能指定源地址，除非给 `TlsClientConnector` 加本地绑定参数。
    当前覆盖全在自动化测试（见第 15 节步骤 24 的表）。**要真机补这一条，需要用户提供第三子网或批准改产品代码。**
14. **五个阶段 deadline 的具体取值尚未经第二轮评审**（2026-09-21 记）：
    `connect 3s / handshake 5s / lengthPrefix 5s / payload 10s / hello 5s`。
    已验证的只是「**执行得准**」（绝对时限在 0–36 ms 误差内生效，`slow-dribble` 也证了不可重置），
    **不是「取值合理」**。慢网络 / 高延迟下 3 s 的连接与 5 s 的握手是否会误杀，
    需要第二轮外部评审判定。这是「数值 vs 机制」的分界，别把机制已验当成数值已验。
15. **验收器与产品共用一个进程，不是隔离的**：`ClientRole` / `HostRole` 在同一进程内
    调产品代码。好处是接线与 `App.xaml.cs` 一致、能测到真实的 DI 路径；
    代价是产品里的静态状态（若有）会跨场景泄漏——**已发现并修过一个**：
    每个场景新建 `AcceptanceContext` 却从不停止 discovery，4 个场景残留 4 个 UDP socket，
    失败现象伪装成「发现不到对端」。现由 `IAsyncDisposable` + `StopAsync` 收口。
    **新增场景时务必确认 context 被释放**。
16. **`--address/--pin` 直连模式下 `pin-mismatch` 的自检有循环性**（2026-09-21 记）：
    该模式不知道对端真指纹，只能「翻转你给的 pin 一位」。所以若你给的本来就是错的 pin，
    翻一位后可能翻回真指纹 → 场景反而 PASS/FAIL 反了（变异矩阵真的撞上过这个双翻）。
    两机验收不受影响（pin 来自 discovery，是真值）。**自检时按提示传真指纹。**

## 15. 下一步 —— M3（TLS Host/Client + 同子网连接校验）

**已实施完毕（2026-09-21，含两机验收 PASS）**：下面是本阶段的施工顺序与逐步记录，原记的「等开工」已不适用。
**开工前必读 `docs/M3_REVIEW_TRIAGE.md`**（外部红队评审 + 本机实测校正）。

**可直接复用，不要重写**（M2 已就绪）：`ISubnetPolicy` / `SubnetPolicy` / `NetworkBinding` /
`LocalNetworkBindingProvider` / `NetworkInterfaceSelector` / `TransportConstants`。

### 阶段 0 —— 先修正证书载入（ADR-029，阻塞后面所有步骤）

1. ✅ **已完成** 把 `DeviceCertificateService.ImportFlags` 改成 `X509KeyStorageFlags.DefaultKeySet`（值 0）。
2. ✅ **已完成** 改掉把旧选择锁成断言的测试：
   `ImportFlags_UsesEphemeralKeySetOnly` → **`ImportFlags_UsesDefaultWithoutPersistOrExport`**，
   断言改为 `DefaultKeySet` 并显式禁止 `EphemeralKeySet` / `PersistKeySet` / `Exportable` / `MachineKeySet`。
3. ✅ **已完成** 新增 `tests/LanRemote.Security.Tests/DeviceCertificateTlsHandshakeTests.cs`
   （真实 SslStream 握手，ADR-018 遗留的强制要求）：断言回调被执行 ∧ pin 字节匹配 ∧
   服务端无异常 ∧ 协商协议 ∈ {Tls12, Tls13} ∧ 真实帧收发往返（`pong:hello-lanremote`）。
   **已做变异验证**：把 flag 改回 `EphemeralKeySet` 后，本测试与步骤 2 的测试**同时失败**
   （本测试报 `IOException: Received an unexpected EOF...`），证明守卫不是空断言；随后已还原。
   ⚠️ 注意污染陷阱：同一进程先 `PersistKeySet` 导入过同一私钥后 `EphemeralKeySet` 会碰巧成功，
   所以结论必须在新进程 / 顺序受控下测，不要依赖"没报错"。
4. ✅ **已完成** `dotnet build` PASS（0 警告 0 错误）+ `dotnet test` **409 PASS / 0 FAIL**
   （原 408 + 新增 1）。**阶段 0 通过，已进入阶段 1。**

### 阶段 1 —— 连接目标与身份契约（TOCTOU / ADR-028）

5. ✅ **已完成** 定义**不可变连接目标快照** `{deviceId, remoteIPv4, tcpPort, expectedCertSha256(32B)}`，
   在用户点击连接的那一刻冻结；握手期间**不得**回读 discovery 缓存。
   - 产物 `src/LanRemote.Transport/ConnectionTarget.cs`（`TryCreate(DiscoveredDevice)` /
     `TryCreate(Guid, IPAddress, int, string)`；指纹**复制**进内部数组，只暴露 `ReadOnlyMemory`）。
   - 验收用例 `TlsClientConnectorTests.Frozen_Pin_Survives_Discovery_Cache_Mutation_Mid_Handshake`：
     真实握手 + `DiscoveryDeviceCache`，服务端闸门保证「改缓存」发生在 TCP 已连上、TLS 未完成之间；
     改完仍按 A 校验成功，并从被污染的缓存重新冻结 → 按 B **失败**（对照组）。
     **已做变异验证**：把 pin 比较短路掉后，本用例的对照组与 `Connect_Rejects_Pin_Mismatch`
     同时失败（`No exception was thrown`），证明不是空断言。
   - ⚠️ 诚实边界：`ConnectAsync` 的参数类型就是快照、不持有缓存引用，所以「回读」在类型层面
     已不可表达；这个用例锁的是**这个契约**，不是在运行期抓到了一次真的回读。
6. ✅ **已完成** 定义**不可变连接上下文** `{deviceId, endpoint, expectedPin, presentedPin}`（ADR-028）。
   产物 `src/LanRemote.Transport/ConnectionIdentity.cs`：`presentedPin` 在证书校验回调里捕获、
   之后不可变、暴露给 M4；不保存 `IPEndPoint` 实例（其 `Port` 可写），每次调用返回新对象。
   另见步骤 13 的客户端校验器，它同时承担「pin 之外还有哪些不变量」。
7. ✅ **已完成** pin 解码与比较：连之前把 hex 解成**恰好 32 字节**，畸形 / 31 / 33 字节在
   `ConnectAsync` 之前失败；比较用 `cert.GetRawCertData()` +
   `CryptographicOperations.FixedTimeEquals`，**不比 hex 字符串**。
   产物 `src/LanRemote.Transport/CertificatePin.cs`
   （`TryDecode` 失败时 `out` 恒为 `Array.Empty<byte>()`；新增 `ReadOnlySpan` 比较重载避免临时分配）。

**阶段 1 附带产出（原本属于步骤 12/13，提前只为让步骤 5 的验收可测）**：

- `src/LanRemote.Transport/TransportTimeouts.cs`：五段**绝对**时限（连接 / 握手 / 长度前缀 /
  payload / hello），各自独立重新计时。**数值是初始值，不是实测结论**——步骤 15 实测后再调。
- `src/LanRemote.Transport/PeerCertificateValidator.cs`：客户端对服务端证书的校验。
  pin 是唯一安全边界；其后是形状不变量（ECDSA P-256 / 非 CA / KU 含 digitalSignature /
  EKU 含 serverAuth / 有效期）。**刻意不要求 `sslPolicyErrors == None`**——自签名必然触发
  `RemoteCertificateChainErrors`，本项目不用 CA、不用系统信任库（ADR-027）。
  时间用 `TimeProvider` 注入，**不改系统时钟**。
- `src/LanRemote.Transport/TlsClientConnector.cs` + `TlsConnection.cs`：客户端 TLS 连接器。
  `TargetHost = string.Empty`（**不发 SNI**——CN 是 `LanRemote-<设备码>`，放进 SNI 等于明文广播设备标识；
  pinning 下主机名校验不参与安全判定）。实测空串可用，握手正常完成。
- **阶段 1 结果**：`dotnet build` PASS（0 警告 0 错误）+ `dotnet test` **464 PASS / 0 FAIL**
  （阶段 0 的 409 + 新增 55）。

#### M3 已实测记录（只写实际跑出来的，不预填）

| 场景 | 实测结果 |
| --- | --- |
| `CreateSelfSigned` 直出证书做服务端 | **失败**：`AuthenticationException: ... does not support ephemeral keys.` ← `Win32Exception 0x8009030E`；客户端只看到 `IOException: unexpected EOF`（ADR-030） |
| PFX 往返 + `DefaultKeySet` 后做服务端 | **成功**，协商到 TLS 1.3 |
| `TargetHost = string.Empty` | **成功**：不发 SNI、不做主机名校验，握手正常完成 |
| 对端 accept 后一直不握手，握手时限 400 ms | 客户端在 **~400 ms** 被切断，异常类型是 **`System.OperationCanceledException`**（实测 `GetType().FullName`，非推断） |
| 证书按真实时间有效、按注入时钟已过期 | 客户端抛 `AuthenticationException`，消息含 `expired`（说明是**我们的**校验器拦下的） |
| 同端口 + 两个不同具体地址 bind | **可以**（开不开 `ExclusiveAddressUse` 都行）→ 步骤 8 成立 |
| 同地址同端口第二次 bind | **被拒**，`AddressAlreadyInUse`(10048) |
| 别人先 bind `0.0.0.0`（未开 exclusive），我们再 bind 具体地址 | **仍成功** —— 开 exclusive 也**发现不了**这种情况 |
| 我们先 bind 具体地址（exclusive），别人再 bind `0.0.0.0` | **也成功** |
| 带 `SO_REUSEADDR` 的后来者抢同地址同端口 | 开不开 exclusive **都被拒**（`AccessDenied` 10013） |
| 连到具体地址的连接归谁 | 归**更具体**的 listener，不会被更宽的 socket 截走 |
| **步骤 15**：pre-auth 空闲，长度前缀时限 400 ms | **413 ms** 切断，`System.OperationCanceledException`，名额归零 |
| **步骤 15**：只发 2 字节（半截长度前缀） | **400 ms** 切断，`System.OperationCanceledException` |
| **步骤 15**：声称 64 字节只发 10 个（半截 payload） | **608 ms** 切断（时限 600 ms），`System.OperationCanceledException`；证明 payload 段有**自己的**预算，没被前缀段吃掉 |
| **步骤 15**：3 条 TCP 连上但永不握手，时限 400 ms | **436 ms** 内名额全部归还，**全程没有调用 `StopAsync`** |
| deadline 执行开销 | **0–36 ms** 量级，几百毫秒级时限可放心使用 |
| 取消时在 `SslStream` 上的异常类型 | **基类** `OperationCanceledException`（不是 `TaskCanceledException`） |
| 取消时在 `MemoryStream` 替身上的异常类型 | **`TaskCanceledException`**（替身内部 `Task.Delay(delay, token)` 抛的）——与上一行**不是同一个类型**，所以断言只要求「是取消」 |
| 变异：把 `CancelAfter` 挪进读循环（滑动窗口） | 低速攻击用例**变红**（8 字节全读完、1 s、无异常） |
| 变异：注释掉握手 `CancelAfter` | 握手卡死用例**变红**（名额 12 s 未归还） |
| `JsonSerializer.Deserialize<T>(utf8)` 会拦尾随内容吗 | 实测**不保证**——必须自己先用 `Utf8JsonReader` 探一次「恰好一个 JSON 值」 |
| 原始字符串字面量 `"""…\n…"""` | `\n` **不是转义**，是字面反斜杠（我自己先踩了一次，测试假红） |
| 变异：pre-auth 改用认证后的 1 MiB 额度（A-9） | 该用例 **720 ms** 以 `pre-auth-frame:…` vs `pre-auth-timeout` 变红 |
| 变异：`HelloFrame.TryParse` 无条件返回 true | **43 条**测试变红 |
| 变异：删掉客户端 `AllowTlsResume = false` | `Client_Options_Are_Explicit` 4 ms 内变红 |
| .NET 10 `SslClientAuthenticationOptions` 默认值 | `AllowTlsResume=True`、`AllowRenegotiation=True`、`EnabledSslProtocols=None` —— 三个都必须显式写 |
| .NET 10 `SslServerAuthenticationOptions` 默认值 | `AllowTlsResume=True`、`AllowRenegotiation=**False**`、`EnabledSslProtocols=None`（两端不对称） |

**⚠️ ADR-032：别过度声称 `ExclusiveAddressUse`**。我最初据此写的注释与测试断言是错的
（以为开了它就能发现"别人先占了更宽地址"）。实测矩阵如上：
**在本机可观测范围内，开与不开没有任何差别**。仍保留 `true`，但理由只能是
「防御 `SO_REUSEADDR` 语义更宽松的旧版 Windows」。

**⚠️ 教训：变异验证必须真的做红**。我第一版守护测试把 `ExclusiveAddressUse` 也开在了
**对照组**的 socket 上，于是「把被测属性改成 false」的变异**没有让测试失败**——
隔离错了，绿着也是空断言。是"变异后仍然绿"这件事暴露了它。

### 阶段 2 —— Listener / 准入 / 同子网校验

8. ✅ **已完成** 每张合格网卡起一个 TCP 45873 listener（`TransportHost.Start()`）。
   - **启动语义定为按网卡降级（ADR-031）**：某张网卡 bind 失败只记进
     `TransportHostStartResult.Failures`（地址 + `SocketError` + 消息），其余照常监听；
     一个都没听上时 `IsListening == false` 且**不抛异常**（本机没合格 RFC1918 网卡时是预期状态）。
   - **前置实测（triage B-23）已完成**，结论见下面「M3 已实测记录」与 **ADR-032**：
     「同端口 + 不同具体地址」可以 bind（开不开 `ExclusiveAddressUse` 都行）——这是本步骤成立的前提。
9. ✅ **已完成** accept 之后**先做同子网校验**：用 `client.Client.LocalEndPoint.Address`
   （即接受它的那个 listener 的地址）调 `ISubnetPolicy.IsAllowedPeer(local, remote)`，不过立即关闭。
   顺序写死在 `TransportHost.HandleAsync`：accept → 同子网 → **准入** → TLS，不可调换。
   验收：`Rejects_Peer_That_Fails_Subnet_Check_Before_Any_Tls`（处理器根本不会被调用、
   `AdmittedConnections == 0`）；另有 `Real_SubnetPolicy_Rejects_Loopback_Peer` 证明
   127/8 不是 RFC1918 时真策略也会拒。**已做变异验证**：把子网校验短路后 4 个用例失败。
10. ✅ **已完成** 准入限额在 accept 之后、**TLS 握手之前**占用（`ConnectionAdmissionLimiter`）：
    全局上限 + 更小的每源 IP 上限，`finally` 里释放，租约释放**幂等**。
    验收：`Admission_Limit_Refuses_The_Extra_Connection`（第二条连接拿不到名额、拿不到 TLS）。
    （`TcpListener.Start(backlog)` 的 backlog 只是待 accept 队列，**不是**应用层 DoS 防线——已写进注释。）
11. ✅ **已完成** 有界连接登记表 `ConnectionRegistry`：停机先取消、再强制释放 socket、再 join，
    两阶段都在停机预算内。验收：`Stop_Cancels_Connections_Stuck_In_Handshake_And_Releases_Admission`
    ——3 个客户端卡在握手里触发 stop，全部 handler 结束、限额归零、`StopAsync` 返回 true。
    **阶段 2 结果**：`dotnet build` PASS（0 警告 0 错误）+ `dotnet test` **495 PASS / 0 FAIL**
    （阶段 1 的 464 + 新增 31）。

### 阶段 3 —— TLS

12. ✅ **已完成** 服务端显式设置（`TransportHost.CreateServerOptions`）：
    `EnabledSslProtocols = Tls12 | Tls13`、`AllowTlsResume = false`、`AllowRenegotiation = false`、
    `ClientCertificateRequired = false`、`CertificateRevocationCheckMode = NoCheck`；
    客户端 `TlsClientConnector` 同样显式关 resume / renegotiation。
    （默认值实测：`AllowTlsResume` 两端都 True；`AllowRenegotiation` 客户端 True / 服务端 False；
    `EnabledSslProtocols` 默认 `None`。三个都不能靠默认。）
13. ✅ **已完成** 客户端校验回调 `PeerCertificateValidator`：仅当**精确 pin 匹配** ∧ 本地不变量全过才接受
    —— leaf 存在 / 32 字节 pin 相等 / ECDSA P-256 / non-CA / KU 含 digitalSignature /
    EKU 含 serverAuth / 有效期当前有效。这些是**不变量检查不是独立安全边界**；
    注入 `TimeProvider` 测试过期证书（不许改系统时钟）。拒绝码只进本地日志，**绝不下发给对端**
    （否则等于免费探测探针）。
14. ✅ **已完成** **阶段绝对 deadline**（`FrameReader` + `TransportTimeouts`），不是"距上次读到字节 N 秒"：
    TCP connect / TLS 握手 / 4 字节帧头 / 整帧 payload / 首个 hello 各自独立、各自重新计时；
    异步读不靠 `ReadTimeout`（它只覆盖同步读）。
    - 实现要点：`ReadExactlyAsync` 在**每一段进入时**建一个 `CancelAfter` 的 CTS，
      循环里**不再重置**——这就是"绝对"的全部内容。
    - 验收：`Slow_Trickle_Does_Not_Extend_The_Absolute_Deadline`（8 字节 × 150 ms vs 400 ms 时限）。
      **已做变异验证**：把 `CancelAfter` 挪进循环变成滑动窗口后，该用例**变红**
      （滑动窗口版把 8 字节全读完了、耗时 1 s、根本不抛异常）。
    - 顺带把**步骤 16 的长度校验**也做完并落在这里（`FrameReader.TryValidateLength`）：
      `uint` 域内拒 0、拒超上限，**之后**才转 `int`；超限**不 drain**、不分配 payload。
      边界用例：`0 / 1 / 上限 / 上限+1 / 0x7fffffff / 0x80000000 / 0xffffffff`。
    - 新增常量 `TransportConstants.MaxPreAuthMessageBytes = 4 KiB`（评审 A-9）：
      规格里 1 MiB 是**认证之后**的额度，未认证连接不该能逼我们分配 1 MiB。
15. ✅ **已完成** 实测取消延迟与异常形态（triage B-24 / B-25）——**数据是跑出来的，不是推断的**，
    详见下面「M3 已实测记录」与新增用例 `PreAuthDeadlineTests`。
    五个场景各自断言 handler 终止、`ActiveConnections == 0`、`AdmittedConnections == 0`。
    - **握手卡死**这条**已做变异验证**：注释掉 `handshakeCts.CancelAfter(...)` 后，
      名额 12 s 都还不上，用例变红——证明它测的确实是时限而不是碰巧结束。
    - **本步骤只量了「时限执行得准不准」，没有量「五个数值本身合不合适」**。
      数值仍留给第二轮外部评审，不在本机实测范围。
    - 顺手修正了一处会误导后续实现的注释：`TransportHost.HandleAsync` 的
      `catch (OperationCanceledException)` 原先只写「停机取消」，但它同时也是**握手时限到点**的落点
      （卡住的是未认证连接，属于预期防御）。当前处理相同（直接断开），
      **阶段 4 引入会话状态机后应当能被区分**，届时再拆。
    **阶段 3 结果**：`dotnet build` PASS（0 警告 0 错误）+ `dotnet test` **513 PASS / 0 FAIL**
    （阶段 2 的 495 + 新增 18：14 条 `FrameReaderTests` + 4 条 `PreAuthDeadlineTests`）。
    **Last code commit：`5ba5822`** · **Working tree at validation: clean**

### 阶段 4 —— Framing / hello / 状态机

16. ✅ **已完成**（在阶段 3 随 `FrameReader.TryValidateLength` 一并落地）：`uint` 校验 → 拒绝 `0` →
    拒绝 `> 当前阶段上限` → **之后**才转 `int`；超限不 drain、不分配。边界长度
    `0 / 1 / 上限 / 上限+1 / 0x7fffffff / 0x80000000 / 0xffffffff` 全部有测试。
17. ✅ **已完成** pre-auth 独立小上限：验收 `Pre_Auth_Frame_Larger_Than_4KiB_Is_Rejected`
    —— 客户端只发一个声称 64 KiB 的长度前缀，服务端断在 `pre-auth-frame:length-exceeds-limit`，
    且 `FrameReaderTests` 用计数器 + 流位置两把尺子证明**只读了 4 字节、没分配 payload**。
    **已做变异验证**：把它换成认证后的 `MaxControlMessageBytes` 后，该用例 720 ms 内以
    `Expected: pre-auth-frame:length-exceeds-limit / Actual: pre-auth-timeout` 变红。
18. ✅ **已完成** 严格 JSON 解析器（`HelloFrame`）：`AllowDuplicateProperties=false`、
    `UnmappedMemberHandling=Disallow`、显式 `MaxDepth=4`（属性值默认读出 `0` = 内置 64，
    不写就等于没有主张）、拒绝注释与尾逗号、`PropertyNameCaseInsensitive=false`、
    拒绝 UTF-8 BOM、非法 UTF-8 **不做替换字符兜底**。
    另外单做了一步「整段字节必须**恰好**是一个 JSON 值」的检查（`Utf8JsonReader` 先探一次，
    尾随非空白一律 `hello-trailing-data`）——`JsonSerializer.Deserialize` 本身不保证这一点。
19. ✅ **已完成** 首帧必须**且仅能**是 `{type:"channel_hello", channel:"control", protocol:1}`
    （`HelloFrameTests` 44 条 + `First_Frame_Must_Be_Exactly_The_Hello` 5 条真链路）。
    「第二个 hello」用「同一会话 `RunAsync` 只允许一次」在类型层面堵掉。
    **已做变异验证**：让 `TryParse` 无条件返回 true 后，**43 条测试变红**。
20. ✅ **已完成** 状态机 `ControlSessionState`：`AwaitingHello` →（hello 通过）→ `PreAuthenticated`，
    任何失败路径 → `Closed`。「此后一律拒绝」的可执行形式是
    `AllowedOperationsWhilePreAuthenticated` **空集合**，并由
    `PreAuthenticated_Allows_Nothing_Before_M4` 盯着——M4 落地前谁往里加东西谁红。
21. ✅ **已完成** M3 终态选**干净关闭**（`SslStream.ShutdownAsync()` 发 close_notify 后由 Host 释放），
    **没有**启用「进入 AwaitingAuthentication + 占位 deadline」那条备选——
    M3 里没有任何东西值得为未认证连接继续留着它。

    #### ⚠️ 阶段 4 变异验证逼出来的一处真实缺陷（已修）

    第一版 `ControlPreAuthSession.RunAsync` 只接了 `FrameProtocolException` / `EndOfStreamException` /
    `IOException`，**没接阶段超时的 `OperationCanceledException`**。后果是：
    超时会一路飞出 `RunAsync`，`ControlPreAuthResult` **永远产生不出来**，
    而且和「停机取消」在调用方看来完全一样。做 A-9 变异时它表现为「30 秒没有结果」，
    而不是一条可读的断言——这个值 30 秒正是发现它的线索。
    修法：加 `catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)`
    → 产生 `pre-auth-timeout`；停机取消继续向上抛（那不是对端的错，不该记成拒绝原因）。
    修完同一变异变为 **720 ms 内以可读断言失败**。新增 `Idle_Client_Is_Rejected_With_A_Timeout_Reason` 固化。

    **阶段 4 结果**：`dotnet build` PASS（0 警告 0 错误）+ `dotnet test` **568 PASS / 0 FAIL**
    （阶段 3 的 513 + 新增 55：44 条 `HelloFrameTests` + 11 条 `ControlPreAuthSessionTests`）。
    **Last code commit：`e0484ec`** · **Working tree at validation: clean**

### 阶段 5 —— 收口

22. ✅ **已完成** triage A 桶 18 条逐条对账（`docs/M3_REVIEW_TRIAGE.md` §2）。
    对账时发现 **3 个真实缺口**（A-6 的本地地址来源、A-16/17 的 TLS 选项、A-1 的终态 e2e），已补。

    | A# | 约束 | 落在哪个测试 |
    | --- | --- | --- |
    | 1 | TLS 成功 = 显式 `PreAuthenticated`，不是"已认证" | `PreAuthenticated_Allows_Nothing_Before_M4`、`Valid_Hello_Reaches_PreAuthenticated_And_Closes`、`After_Hello_The_Connection_Is_Closed_And_A_Second_Hello_Gets_Nothing` |
    | 2 | 点击时冻结不可变快照，握手期间不回读缓存 | `Frozen_Pin_Survives_Discovery_Cache_Mutation_Mid_Handshake`、`TryCreate_FromDiscoveredDevice_FreezesAddressAndPin`、`ExpectedCertSha256_IsNotSharedWithCaller` |
    | 3 | pin 先解码成恰好 32 字节，比较用 `FixedTimeEquals` | `TryDecode_*`、`Matches_*`、`TryCreate_RejectsMalformedPin`、`TryCreate_RejectsPresentedWithWrongLength` |
    | 4 | 同 deviceId 不同指纹不得静默覆盖 | **ADR-027 有意推迟到 M4 之前**，M3 靠第 2 条止血；**本条在 M3 故意没有测试** |
    | 5 | M3 暴露不可变 `PresentedCertSha256`（ADR-028） | `Connect_Succeeds_And_Captures_Presented_Pin`、`TryCreate_CapturesExpectedAndPresented`、`Identity_IsNotAffectedByMutatingThePresentedArrayAfterCreation` |
    | 6 | 同子网在 accept 后、TLS 前，用<b>接受它的那个 listener 地址</b> | `Rejects_Peer_That_Fails_Subnet_Check_Before_Any_Tls`、`Real_SubnetPolicy_Rejects_Loopback_Peer_...`、**新增** `Subnet_Check_Receives_The_Accepting_Listener_Address` |
    | 7 | 准入在 accept 后、握手前；`finally` 释放 | `Admission_Limit_Refuses_The_Extra_Connection`、`Acquires_Up_To_Global_Limit_Then_Refuses`、`Refuses_Same_Address_Beyond_Per_Address_Limit_...`、`Release_Is_Idempotent` |
    | 8 | 阶段绝对 deadline | `Slow_Trickle_Does_Not_Extend_The_Absolute_Deadline`、`Prefix_And_Payload_Deadlines_Are_Independent`、`Handshake_Deadline_Fires_Even_When_Peer_Keeps_Silence`、`PreAuthDeadlineTests` ×4 |
    | 9 | pre-auth 独立小上限 | `Pre_Auth_Frame_Larger_Than_4KiB_Is_Rejected` |
    | 10 | `uint` 先校验，超限不 drain | `Validate_Length_Covers_The_Interesting_Boundaries`、`Oversized_Length_Is_Rejected_Without_Draining_The_Payload` |
    | 11 | 严格 JSON 解析器 | `HelloFrameTests` ×44 |
    | 12 | 首帧必须且仅能是 hello | `First_Frame_Must_Be_Exactly_The_Hello` ×5、`A_Session_Cannot_Be_Run_Twice` |
    | 13 | M3 必须有明确终态 | `Valid_Hello_Reaches_PreAuthenticated_And_Closes`、`After_Hello_...` |
    | 14 | pin 是唯一边界；其余是不变量 | `Rejects_Pin_Mismatch`、`Rejects_Ca_Certificate`、`Rejects_Rsa_Key`、`Rejects_Key_Usage_Without_Digital_Signature`、`Rejects_Enhanced_Key_Usage_Without_Server_Auth`、`Rejects_Missing_*` |
    | 15 | 有效期必须查，用注入时钟 | `Rejects_Expired_Certificate_Using_Injected_Clock`、`Rejects_Not_Yet_Valid_Certificate_Using_Injected_Clock`、`Accepts_Certificate_That_Expires_One_Second_Later` |
    | 16/17 | 两端显式 `AllowTlsResume=false` / `AllowRenegotiation=false` | **新增** `TlsOptionHardeningTests` ×3（`Defaults_Are_What_We_Must_Not_Rely_On` 把默认值也一起钉住，作为"为什么必须显式写"的证据） |
    | 18 | 连接任务有界 + 停机 join | `Stop_Cancels_Connections_Stuck_In_Handshake_And_Releases_Admission`、`Stop_Cancels_Ignored_Cancellation_And_Still_Joins`、`Stop_Marks_Stopping_And_Refuses_New_Registrations` |

    为了让 A-16/17 可测，把两端的选项构造提成 `internal`
    （`TlsClientConnector.CreateClientOptions` / `TransportHost.CreateServerOptions`），
    并对 `LanRemote.Transport.Tests` 开 `InternalsVisibleTo`——
    **不这么做的话，删掉一行 `false` 不会有任何东西变红**。
    已变异验证：注释掉客户端的 `AllowTlsResume = false` 后该用例立即变红。
23. ✅ **已完成** `dotnet build` PASS（0 警告 0 错误）+ `dotnet test` **573 PASS / 0 FAIL**
    （阶段 4 的 568 + 新增 5）。HANDOFF 与 `docs/DECISIONS.md`（ADR-033）已更新、变更已提交。
    **Last code commit：`5bf3cb6`** · **Working tree at validation: clean**
24. 🟡 **验收物料已就绪，等用户执行**：两机验收（跨机真实 TLS + pinning）。
    **M3 在用户回填之前不算做完**，本机没有 RFC1918 网卡无法自证。

    **关键结论：`LanRemote.App` 不能用来验 M3。** 它只是 `ProjectReference` 了
    `LanRemote.Transport`，`src/LanRemote.App/` 里 grep
    `TransportHost|TlsClientConnector|ControlPreAuthSession` **零命中**——
    打 App 的包去两机跑，只能重新证明 M2 的 discovery 还能用。

    因此新建 `tools/LanRemote.Acceptance`（`net10.0-windows`，**WinExe + WPF 窗口程序**，已加进 sln）：

    | 文件 | 作用 |
    | --- | --- |
    | `AcceptanceContext.cs` | 刻意照抄 `App.xaml.cs` 的 DI 接线（vault→证书→身份→binding→SubnetPolicy→discovery）；`IAsyncDisposable`，停机时 `StopAsync` 发现服务 |
    | `AcceptanceLog.cs` | WinExe 没有控制台 → 双写 UI（`LineWritten` 事件）+ 磁盘；**每次运行一个不可变文件** `m3-<role>-<UTC>-<runId>.log` |
    | `AcceptanceOutcome.cs` | 五类结局，**枚举值即退出码**；`Combine()` 带优先级 |
    | `AcceptanceRun.cs` | 一轮的上下文：runId / 角色 / 日志 / 中止标记 / 后台故障；头和尾由它写 |
    | `AcceptanceProfile.cs` | **两端共用**的五个 deadline 与各场景期望窗口（写在一处，避免两侧各写一份慢慢漂移） |
    | `AcceptanceLoggerProvider.cs` | 把产品 `ILogger` 事件转发进验收日志（WinExe 下 `AddSimpleConsole` 是死信投递） |
    | `App.xaml(.cs)` | 两个入口（无参开窗 / `--headless`）+ 三个异常 handler（见下） |
    | `MainWindow.xaml(.cs)` | 窗口：身份面板 + **角色锁** + 结论横幅 + 日志区 + 复制/打开目录 |
    | `HeadlessCommand.cs` / `HeadlessRunner.cs` | headless 参数解析与调度 |
    | `InfoRole.cs` | 环境自检，回填身份/指纹/端口/合格网卡 |
    | `HostRole.cs` | 被控端：起 discovery + `TransportHost`，每条连接跑完整 pre-auth 会话，**互斥终态桶** |
    | `ClientRole.cs` | 控制端：发现→冻结快照→TLS→hello→等对端收尾，跑完四个必做场景后打**交叉核对清单** |

    退出码改为 **0 PASS / 1 FAIL / 2 UNMET / 3 HARNESS_ERROR / 4 INVALID_RUN**，
    窗口把它翻译成人话显示。

    **双击 `LanRemote.Acceptance.exe` = 一个窗口，不需要任何脚本。**
    同一个 exe 带 `--headless` 就是命令行模式，**两条路调用完全相同的 `HostRole`/`ClientRole`**。

    **四个必做场景**（`ClientRole.MandatoryScenarios`）：

    | 场景 | 通过标准 | 状态 |
    | --- | --- | --- |
    | `success` | 控制端 `presentedPin` 与 discovery 的 `pin` 逐字符一致 + `peerClosed=eof` 且 `closedAtMs ≤ 3000`；被控端须有 `outcome=PreAuthenticated rejection=-` | 待真机 |
    | `pin-mismatch` | 抛 `AuthenticationException` **且**消息里短码恰为 `pin-mismatch`（不是"抛了认证异常就算"） | 待真机 |
    | `timeout` | 不发 hello，约 5 s（= `lengthPrefixTimeout`）被切；被控端 `rejection=pre-auth-timeout` | 待真机 |
    | `slow-dribble` | 2 s 间隔滴流长度前缀 → **只发出 3/4 字节**就被切在绝对时限上 | 待真机 |
    | `cross-subnet` | —— | **本次不跑，见 §14 第 13 条** |

    **本机已实测（真实执行，不是推演）：**

    - `dotnet build` 0 警告 0 错误、`dotnet test` **574 PASS / 0 FAIL**
    - WPF 窗口**真的渲染出来**（`[GUI] 窗口渲染完成。 ActualWidth=900 ActualHeight=700`），
      已截图肉眼确认四个区块与身份面板内容正确
    - **全新解压 + `env -u DOTNET_ROOT -u DOTNET_HOST_PATH`** 下双击 `LanRemote.Acceptance.exe`
      → 窗口正常打开，自包含成立、也不需要任何脚本
    - 自检真跑通 DPAPI→身份→证书整条链（deviceCode `M5WC-14GX`，
      certSha256 `89A5C10E…5445`），正确判 `outcome=FAIL reason=no-qualified-rfc1918-nic`，
      并在窗口里把两个角色按钮**置灰**（防止「点了没反应」）
    - `--headless info` 退 2、`--headless host` 退 2（"没有任何可监听地址"）、
      `--headless client --all` 打环回假被控端 **4/4 PASS 退 0**、参数错误五条路径全部退 3 且**都有可读输出**

    ### 24.1 变异矩阵：11 例，全部符合预期（2026-09-21，环回）

    **这是本轮最重要的一件事**：验收器的判据在真机跑之前必须先在环回上被**看见红过**。
    为此写了一个可切换行为的假被控端（`Mode.Real / Deaf / Resettable` + `AllowAllPolicy` + `StubHost`），
    用**真 exe** 打它：

    | # | 用例 | 变异内容 | 期望 | 实测 |
    | --- | --- | --- | --- | --- |
    | 1 | `real/success` | 真 `ControlPreAuthSession` | 退 0 | ✅ 退 0（43–59 ms 收尾） |
    | 2 | `real/pin-mismatch` | 同上 | 退 0 | ✅ 退 0，`rejection=pin-mismatch` |
    | 3 | `real/timeout` | 同上 | 退 0 | ✅ 退 0（4982 ms） |
    | 4 | `real/slow-dribble` | 同上 | 退 0 | ✅ 退 0（`sent=3/4`，4976 ms） |
    | 5 | `real/pin-mismatch-doubleflip` | 故意把**已翻转**的 pin 再给一次 | 退 1 | ✅ 退 1（证明「翻转一位」是**承重的**） |
    | 6 | `deaf/success` | 握完手**一个字节都不读**，等满时限才关 | **退 1** | ✅ 退 1（`reset`，4994 ms） |
    | 7 | `deaf/timeout` | 同上 | 退 0（本来就该等时限） | ✅ 退 0 |
    | 8 | `resettable/slow-dribble` | 把绝对时限改成**每读到字节就重置**的空闲时限 | **退 1** | ✅ 退 1（`sent=4/4`，16054 ms） |
    | 9 | `resettable/timeout` | 同上 | 退 0 | ✅ 退 0 ← **这条证明旧判据是空的** |
    | 10 | `resettable/success` | 同上 | 退 0 | ✅ 退 0 |
    | 11 | `nothing-listening/success` | 端口上什么都不开 | 退 **2**（UNMET，不是 PASS） | ✅ 退 2 |

    三条由此**坐实**的结论：

    - **第 9 条是关键**：`resettable` 下 `timeout` 仍然绿 → 单靠 `timeout` 区分不出
      「绝对时限」和「可重置空闲时限」。**`slow-dribble` 才是那个场景存在的唯一理由**
      （评审第 1 条是真缺陷，不是理论担忧）。
    - **第 6 条允许删掉 TCP 探针**：`deaf/success` 能红，靠的是「收尾时刻贴不贴时限」，
      而这条判据成立的前提是 TCP 真连上了——第 11 条证明「没连上」会落到退 2 而不是退 0。
      于是探针（污染被控端计数、制造 TIME_WAIT）可以删掉，改用更强的结构化断言。
    - **第 5 条保证「删一行不会变红」不会发生**：如果哪天有人把 `FlipOneBit` 的调用删掉，
      第 5 条会和第 2 条一起变红。

    完整输出留档：`%TEMP%\lrmut\matrix-full.txt`（94 行，含每例的 `[CLIENT][RESULT]` 与被控端逐条记录）。

    ### 24.2 交叉核对清单：从「猜出来的数字」改成「可证伪的约束」

    控制端跑完会给被控端列一份核对清单，**它永不宣布里程碑通过**：

    ```
    ① outcome=PreAuthenticated 的行恰好 1 条（只有 success 该走到这）
    ② rejection=pre-auth-timeout 的行恰好 2 条（timeout + slow-dribble）
    ③ 其余任何一行都不得是 PreAuthenticated，也不得是 pre-auth-timeout
    ④ connectionsEnteringSessionHandler 落在 3..4
    ⑤ listenersStoppedCleanly=True 且 activeAtStop=0
    ```

    **④ 为什么是区间**——这里我先写错过一次，被实测抓住：

    我原以为 `pin-mismatch` 死在 TLS 阶段、服务端不会留行，于是断言「应为 3（4 减 1）」。
    实测**是 4**：客户端拒绝证书发的是 TLS alert，而 **TLS 1.3 下服务端在收到该 alert
    之前就已经认为握手完成**，于是它照样进了会话处理器，读到 EOF 后给出
    `rejection=pre-auth-eof`（本机环回实测值）；TLS 1.2 下服务端握手会直接失败、才真的不留行。

    这正是「从症状推断因果」的典型错误：从「客户端看到握手失败」推到「服务端没进会话」，
    中间那一步（TLS 1.3 的半开窗口）被我先入为主地跳过了。
    现在 ④ 只依赖「连接走到了哪一步」（`ReachedWire` / `TlsStageRejection`），**不依赖场景通过与否**——
    否则场景一失败，区间自己就跟着漂，读者会以为区间是实测值。

    ①②③ 在**有场景未通过**时会额外打一行 ⚠ 说明「这些数字是『本该是多少』，不是实测值」。

    ### 24.3 配对方法：4 元组，不是计数相等

    控制端每个场景打 `[CLIENT][CORRELATE] local=<本机IP>:<临时端口> peerHost=<对端IP>:<端口>`，
    被控端每条连接打 `[HOST][RESULT] conn#N peer=<同一个端点>`。
    用 `local` ≡ `peer` **唯一配对**一条连接。

    > 为此给 `TlsConnection` 加了只读属性 `LocalEndPoint`（**不参与任何判定**）。
    > 它不参与判定 ≠ 可以不做对侧断言：只断言「非空」的话，把它实现成
    > `返回任意本地端口` 测试照样绿，而错的配对键会让两份日志配错行——
    > 比没有配对键更坏。所以测试断言的是**同一性**：
    > `TlsClientConnectorTests.Connect_Reports_Local_End_Point_Matching_Server_Observation`
    > 要求客户端自报的本地端点 == 服务端 accept 时看到的远端端点。

    ### 24.4 本轮新抓到的四个真缺陷（都不是模型提出来的，是跑出来的）

    | # | 缺陷 | 现象 | 根因 |
    | --- | --- | --- | --- |
    | 1 | **`--all` 下 `pin-mismatch` 必然 FAIL** | 退 1，信息写着「本该在 TLS 阶段被拒绝，实际握手成功了」，看上去像产品坏了 | 直连模式漏了「翻转一位」，四个场景共用同一个 `--pin` → 拿真指纹去连 |
    | 2 | **交叉核对输出自相矛盾** | 逐条期望写 `pin-mismatch → (无行)`，而 ④ 写 `3..4`（承认可能有幽灵行） | 同一份输出里两句话互相打脸，比两句都错更危险 |
    | 3 | **参数错误静默退出** | `--headless client --bogus` → 退 3、stdout/stderr **均 0 字节**，只有 `crash.log` 里一行 NRE | `HeadlessCommand.TryParse` 在错误分支写 `return true`，调用方按「成功」处理 → `RunHeadless(null)` → NRE → 被顶层兜底吞掉，**错误消息全程丢失** |
    | 4 | **④ 的下界在失败轮次里偏大** | 全部死在 TLS 时仍写 `3..4`，会让人去找不存在的行 | `success/timeout/slow-dribble` 的 TLS 失败分支漏标 `TlsStageRejection` |

    四个的共性：**都不是产品代码错，而是「证据/接口自己说谎」**。
    所以严重性高于普通 bug——验收器的错误会污染结论。

    教训各一条：

    - 同一件事有两处实现时（发现路径 vs 直连路径的翻转），**只改一个**必然出事；
      所以 `FlipOneBit` 现在是唯一实现，两条路共用。
    - 跨工具的隐含契约（「验收器会自己翻一位」）必须写在**两边**的注释里，
      否则改一边会以「看起来像产品坏了」的方式炸（变异矩阵的 `real/pin-mismatch` 就是这样被炸出来的）。
    - **返回值的语义必须和调用方的理解一致**。`TryParse` 那个 `return true` 极可能当时想的是
      "这确实是一次 headless 调用"——但调用方按字面意思读。这种错**不可能**被单元测试发现
      （因为两边各自自洽），只能靠端到端跑一遍、并且**看输出字节数**。

    ### 24.5 三个「待实测」项已全部实测收口

    评审把三条 .NET 行为标成「不得据模型结论下判断」，现已在本机测完（详见
    `docs/M3_ACCEPTANCE_UI_REVIEW_TRIAGE.md` §2）：

    | 评审断言 | 实测结论 |
    | --- | --- |
    | 裸后台线程异常不会被 `DispatcherUnhandledException` 转发 | **成立且更极端**：进程**当场死亡**（exit 127，无任何输出，handler 来不及做什么） |
    | `TaskScheduler.UnobservedTaskException` 默认不终止进程 | **成立**：进程存活且**完全静默**（.NET Core 起终结器不再杀进程） |
    | TCP 未连通时不会走到证书校验 | **成立**：抛 `SocketException` / `IOException`（内层 `SocketException`），**永不**是 `AuthenticationException` → TCP 探针删除的依据 |
    | `ReadAsync == 0` 不等于 `close_notify` | **成立**：裸 TCP FIN 也返回 0（RST 抛 `IOException`）；`SslStream.ShutdownAsync` 只有显式调用时才发 `close_notify` → 全部文案降级为「**有序 EOF**（TLS 记录层 EOF）」 |

    两个 handler（`AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException`）
    与 `AcceptanceRun.ReportBackgroundFault` 就是按这两条实测结论加的。
    `MainWindow` 的点击处理器是 `async void`、`ClientRole` 里没有 `ConfigureAwait(false)`——
    所以 headless 走 `Task.Run`，绝不让测量被 UI 线程污染。

    ### 24.6 本轮提交与打包记账

    本轮（4 个真缺陷 + headless 入口 + 4 元组配对 + 变异矩阵）提交为 **`f080581`**
    （24 files changed, +3399 −583）。内容清单：

    | 区域 | 文件 |
    | --- | --- |
    | 产品（唯一改动，只读观测量） | `src/LanRemote.Transport/TlsConnection.cs`（`LocalEndPoint`） |
    | 测试 | `tests/LanRemote.Transport.Tests/TestTlsServer.cs`（记录 accept 远端端点）、`TlsClientConnectorTests.cs`（对侧一致性 + Dispose 后为 null） |
    | 验收器 | `tools/LanRemote.Acceptance/`：新增 `HeadlessCommand.cs` / `HeadlessRunner.cs` / `AcceptanceOutcome.cs` / `AcceptanceProfile.cs` / `AcceptanceRun.cs` / `AcceptanceLoggerProvider.cs`，改 `App.xaml(.cs)` / `ClientRole.cs` / `HostRole.cs` / `InfoRole.cs` / `MainWindow.xaml(.cs)` / `AcceptanceContext.cs` / `AcceptanceLog.cs` |
    | 文档 | `docs/DECISIONS.md`（ADR-034）、`docs/M3_ACCEPTANCE_UI_REVIEW_{PROMPT,TRIAGE}.md`、`docs/M3_TWO_MACHINE_ACCEPTANCE.md`、`README.md`、`HANDOFF.md` |

    **打包从 `f080581` 之后的工作树做**（源码与验收器已全部提交，脚本会重新
    `dotnet publish`）。zip 与 `artifacts/` 都在 `.gitignore` 里，不入库、不追溯。
    本文件（`HANDOFF.md`）与其后的记账性修订属文档提交，**不改变代码内容**。

    **本次打包实测**（2026-09-21 16:40，`f080581` 之后的工作树）：
    `265 files packed / raw 132.3 MiB / zip 57.4 MiB` →
    `LanRemote-0.1.0-m2-m3-acceptance-win-x64.zip`。zip 校验：265 条目**全扁平**（无子目录）、
    无 `START.cmd`、唯一脚本是 `set-lab-ip.ps1`（UTF-8 with BOM，22304 B）、
    `START-HERE.md` 与 `docs/M3_TWO_MACHINE_ACCEPTANCE.md` **逐字节相同**
    （22197 B，sha256 前 16 位 `cda95eb812351b6b`）。

    **`LANREMOTE_M3_CLEAN=1` 在本机不可用**：脚本内部的 `shutil.rmtree` 同样被 safe-delete
    hook 拦下（`SAFE_DELETE_BULK_CONFIRM_REQUIRED count=273 threshold=50`）→ 脚本死于
    SystemExit。**不要清目录**：`dotnet publish -o` 本来就会覆盖它自己产出的每个文件；
    `artifacts/m3-acceptance/` 里那 13 个**空的**语言卫星目录（`cs/` `zh-Hans/` …）是历史残留，
    `os.walk` 只收文件，**不会进 zip**（上面 265 条扁平条目的实测即为证据）。

    ### 24.7 第二个 lab 准备缺口：TCP 45873 没有任何入站放行（2026-09-21）

    在 A 机（本机）准备 M3 验收环境时抓到的：`set-lab-ip.ps1` 只建了
    `LanRemote Discovery UDP 45872` 一条入站规则，**TCP 45873 一条都没有**。
    Windows 防火墙对入站默认拒绝 → 被控端静默丢掉对端的 SYN，控制端只看得到
    `SocketException stage=tcp`（"连接被拒/超时"），**日志里没有任何线索指向防火墙**。
    M2.1 的发现验收只用 UDP 45872，所以这个缺口一直没暴露；**只有 M3 才需要 TCP**。

    真正的坑不在「少一条规则」，而在**幂等分支**：原脚本发现 lab 地址已存在就
    直接 `exit 0`，根本走不到防火墙步骤 —— 也就是说**重跑脚本永远补不上规则**，
    唯一出路是 `-Undo` 再来一遍。已修：

    | 改动 | 说明 |
    | --- | --- |
    | 提取 `Set-LabNetworkProfileAndFirewall` | profile 重试 + 两条规则的查/建，一处实现 |
    | 幂等分支也调用它 | 「地址已存在」≠「环境已就绪」，地址、profile、规则是三件事 |
    | 规则改两条 | `LanRemote Discovery UDP 45872` + `LanRemote Control TCP 45873` |
    | `-Undo` 一并删规则 | 脚本动过的东西都能精确撤销 |

    **实测证据（A 机，提权运行）**：
    - 第一次运行：建 `192.168.1.10/24`（保留 `172.100.166.220`）+ `profile = Private`
      + `firewall rule already exists`（UDP）；
    - 第二次运行（幂等路径）：`192.168.1.10 already present - address left untouched.`
      → `rule exists : LanRemote Discovery UDP 45872` /
      **`rule created: LanRemote Control TCP 45873`**；
    - 随后 `--headless host --seconds 8`：`[HOST] boundAddresses = 192.168.1.10`、
      `[HOST] port = 45873`、`[HOST][SUMMARY] connectionsEnteringSessionHandler=0
      listenersStoppedCleanly=True activeAtStop=0 handlerFaults=0`、**退 0**；
      同一轮发现服务也起来了（`局域网发现启动。有效网卡=1，地址=以太网:192.168.1.10`）。
    - 脚本语法用 PS 解析器检查：`errors=0`；BOM 保留（24660 字节）。
    - 第二次打包（含本修正）：**265 files / raw 132.3 MiB / zip 57.4 MiB**，
      zip sha256 前 16 位 `234d0b5aebcd6798`，`START-HERE.md` 与手册仍逐字节相同。

    **A 机现场数据（2026-09-21，交叉核对时对表用）**：

    | 项 | 值 |
    | --- | --- |
    | 设备名 | `DESKTOP-D132BMD` |
    | 设备码 | `M5WC-14GX` |
    | 证书指纹 | `89A5C10E8C1950CB2C840F3054CB358274C41B71B9BECC89C8BBA5425320F445` |
    | lab 地址 | `192.168.1.10`（原 `172.100.166.220/24` 保留，未断网） |
    | 监听 | TCP 45873（`boundAddresses = 192.168.1.10`），UDP 45872 发现已启 |

    **提权路径笔记**：PowerShell 的 `Start-Process -Verb RunAs` 被本机安全策略拦
    （"spawns a child process that bypasses PowerShell command validation"）；
    `powershell.exe` 也不能从 Git Bash 直接调。可行路径是 **Python + `ctypes` 直调
    `ShellExecuteExW(lpVerb="runas")`**，拿 `hProcess` 后 `WaitForSingleObject` 等结束、
    `GetExitCodeProcess` 读退出码 —— 本机已实测（两次提权运行都返回 0）。

    **物料**：`LanRemote-0.1.0-m2-m3-acceptance-win-x64.zip`，由
    `scripts/acceptance/make-m3-package.py` 产出
    （`LANREMOTE_M3_SKIP_PUBLISH=1` 跳过 publish 只重打包；`LANREMOTE_M3_CLEAN=1` 才先清空目录）。
    包内含 `START-HERE.md`（=`docs/M3_TWO_MACHINE_ACCEPTANCE.md`）与 `set-lab-ip.ps1`，
    其余是自包含运行时。`set-lab-ip.ps1` 打包时强制加 BOM（PS 5.1 否则中文乱码）。
    手册：§0.1 双击启动、§0.2 headless 用法与退出码、§3 逐场景判定（含 `slow-dribble`）、
    §4 汇总与五条约束、§5 配对方法、§6 两个已知缺口、§7 要贴回来的证据、§8 排障表。

    ### 24.8 M3 第 24 步 · 续「一键准备本机」（2026-09-21）

    **起因**：用户第三次指出「这程序不是可以管理员模式打开吗，怎么还要求手动跑脚本」。
    核对后确认验收器**当时确实没有配置能力**——是功能没做，不是权限问题。用户裁定
    「等一键做好再用」。本小节 = 补齐过程与同轮抓到的 3 个真缺陷；落地 ADR-035
    （主进程永不提权，提权只在窄域 helper）。

    **功能面**（全部在 `tools/LanRemote.Acceptance/` 与 `set-lab-ip.ps1`，`src/` 一行未动）：

    | 组件 | 内容 |
    | --- | --- |
    | `LabAction.cs` | 三个动作（`ApplyA` / `ApplyB` / `Undo`）的枚举与角色映射 |
    | `LabWorkerRole.cs` | **提升实例专用**动词 `lab-apply` / `lab-undo`——只做「跑包内脚本 + 写运行尾」；脚本路径 TOCTOU 重校验（必须在 exe 目录内、非 ReparsePoint） |
    | `ElevatedLauncher.cs` | `ShellExecuteExW(runas)` + `WaitForSingleObject` + `GetExitCodeProcess`；UAC 拒绝 = `ERROR_CANCELLED`(1223) → `outcome=UNMET reason=uac-declined`（人工取消不是失败，不循环弹窗） |
    | `LabSetupRole.cs` | 编排：提权 → 跑脚本 → 尾随子实例日志（双写证据）→ 状态核验（`已就绪：<IP> 在监听地址里`）→ 结局；窗口按钮与 headless 走同一条路 |
    | 窗口 | 「准备为 A 机（192.168.1.10）」「准备为 B 机（192.168.1.20）」「撤销准备（还原网络设置）」+ 完成后自动重新自检；`_processLog` 恢复 `gui.log` 写渲染行 |
    | headless | 新动词 `prepare-lab`（普通权限可跑）/ `lab-apply` / `lab-undo`（提升专用）；`--log-file` 只属于后两者（提升实例的日志落点由编排者约定） |

    **同轮的 3 个真缺陷**（都是跑出来的，不是模型提出来的）：

    1. **子进程中文乱码**：`[LAB][PS]` 段全变 `U+FFFD`，而 `[LAB][SCRIPT]` 段正常。
       根因 = .NET 10 **没有内置 CP936 解码器**（`Encoding.GetEncoding(936)` 直接抛异常 →
       落 UTF-8 兜底），而 PS 5.1 在 **stdout 是管道**时按控制台代码页（936）编码
       `Write-Host`。修法 = 脚本 v4 在 `[Console]::IsOutputRedirected` 为真时主动把
       `[Console]::OutputEncoding` 切 UTF-8（`Invoke-Netsh` 的显式 936 save/restore
       不受影响）；harness 固定按 UTF-8 解；输出出现 `U+FFFD` 记 WARN（提示包内脚本
       可能不配套）。**顺带纠错**：早先「`GetEncoding(936)` 本机可用」的记账是错的；
       `System.Text.Encoding.CodePages.dll` 也不是陈旧残留，而是运行时 framework 资产
       （deps.json 里 `assemblyVersion 10.0.0.0` 的 secondary `"runtime": {}` 条目，
       无 csproj 引用）。
    2. **尾随转发静默丢 7 行**（子实例 90 行 vs 转发 83 行；Python 逐行差集证明缺的是
       adapter / profile / 两条 rule / 一条分隔线 / `[SCRIPT]` 一行）。根因 = tailer 的
       `usable` 越过「文件以换行结尾」的空占位 → 下一行恰好占用该位置 → **永不转发**。
       修法 = 修 `Flush` 记账（`usable` 必须 = 确定写完的行数），并加**独立对账**
       `CountCompleteLines`（数 `\n`，与 `Split` 是两条独立算法）：每次 prepare 打
       `[LAB] 转发对账 = X 行 / Y 行（一致）`，不一致就建议以子实例自己那份为准。
    3. **`gui.log` 的问题不是「丢了」而是「文档还在说双写」**：改 per-run 文件后
       「窗口渲染完成」只进了 UI，「窗口没崩」的证据带不走（排查 GUI 冒烟时被 7 小时前的
       遗留文件带偏过一次）。修法 = `MainWindow` 新增 `_processLog`（`gui.log` 重新写
       渲染行），并把「形态教训」第 3 条与 UI 评审 prompt 的口径一起更正。

    **实测（真升级路径）**：

    - `--headless lab-apply`（幂等复跑）：`elevated=yes` / `scriptExit=0` /
      `stateCheck=已就绪：192.168.1.10 在监听地址里（当前 192.168.1.10）` /
      `keeping the saved pre-lab state`（快照未被污染）→ **PASS**。
    - `--headless prepare-lab` 全流程：提权 → 尾随 → 双份证据 →
      `[LAB] 转发对账 = 提升实例 89 行 / 已转发 89 行（一致）` → 状态核验 → **退 0**。
    - GUI 冒烟：`gui.log` = `[GUI] 窗口渲染完成。 ActualWidth=960 ActualHeight=820`
      （与当前 XAML 一致）、自检 `qualifiedNic=192.168.1.10`、`outcome=PASS`、
      stdout 0 字节、进程存活。
    - `dotnet build` 0 警告 0 错误；`dotnet test` **574 PASS / 0 FAIL**（`src/` 未动）。
    - 重打包：265 files / raw 132.4 MiB / zip 57.4 MiB。**打包后手册又动过一次**
      （净 +6 行 / +535 B：§0.2 的 `prepare-lab` 命令行形态 + `lab-apply`/`lab-undo`
      警示）→ 用 `LANREMOTE_M3_SKIP_PUBLISH=1` 重打刷新 `START-HERE.md`（二进制未变）。
      **最终 zip**：`LanRemote-0.1.0-m2-m3-acceptance-win-x64.zip`，60 197 088 B，
      sha256 `f81d194c6ee4cd6dd5402bfd7085a9e83dd337af267b227b148e2b90c952e1a3`；
      265 条目全扁平、`START-HERE.md` 与手册逐字节相同（25 427 B）、`set-lab-ip.ps1`
      与源文件一致（32 669 B、含 BOM）。

    **提交**：`1d5ffc8`（本小节所述代码与文档都在这一个提交里；HANDOFF 与记忆是其后的
    记账提交）。**B 机换包流程**：拷新 zip → 解压 → 双击 exe → 点「准备为 B 机」→
    一次 UAC → 自检转绿 → 按手册 §3 跑四场景。

    遗留（低优先，不为它单独重打包）：`HostRole.cs` 的前置失败文案仍只写
    「用 `set-lab-ip.ps1`」——窗口路径下该分支基本不可达（自检不合格时角色按钮本来
    就是灰的），下次动代码时顺手改。

### M3 第 24 步 · 两机验收记录（2026-09-21 真机执行 —— **PASS**）

**判定口径**：本轮判定 = **逐条连接证据**（验收手册 §4 的五条约束 + §5 的 4 元组配对，
两份日志对得上）。被控端整轮**结局字段**是 `INVALID_RUN`（退 4）——那是「操作员按了停止」
触发收尾机制后的机械产物，**不是失败、不需要重跑**（来历见下）。

**两台机器与物料**（A = 控制端 / B = 被控端）

| 项 | A 机 | B 机 |
| --- | --- | --- |
| 设备名 / 设备码 | DESKTOP-D132BMD / `M5WC-14GX` | DESKTOP-CU2263D / `3ERD-R74V` |
| lab 地址（唯一合格网卡） | `192.168.1.10` | `192.168.1.20` |
| 设备 id | `fbdf903d-988c-4b38-b999-25998befe71f` | `ee59c0c0-c05c-4034-ba6b-5f1f006c7ae1` |
| 证书指纹 | `89A5C10E…5320F445` | `6755838E…49A73938` |
| osBuild / .NET | 10.0.26200 / 10.0.12 | 10.0.22631 / 10.0.12 |

物料：harness `B0FAF788…BF544A`（162 304 B）、`LanRemote.Transport.dll` `A47E64AF…8BCE2B`
（53 248 B），**两端 `[BUILD]` 行逐字符一致**；执行物料 = zip `f81d194c…`（60 197 088 B / 265 文件）。
跨端互证：A 的 `peerDeviceId` = B 的 `hostDeviceId`；A 的 `presentedPin` 与 B 的
`hostCertSha256` **逐字符相同**；B 的 `[HOST][CORRELATE]` 里 `boundAddresses="192.168.1.20"`
`port=45873` 与 A 每条 `peerHost=192.168.1.20:45873` 对上。

**A 侧**（runId `8a7e3d03`，`startedUtc=10:56:00.039Z`，`--headless client --peer 3ERD-R74V --all`，退 0：4/4 PASS）

| 场景 | 关键实测行（原文摘） |
| --- | --- |
| success | `tls = ok proto=Tls13`；`presentedPin= 6755838E…4938`；`local=192.168.1.10:49575`；`peerClosed = eof t=29 ms` |
| pin-mismatch | `wrongPin` 与真值仅 `byte[16] ^ 0x01` 之差（`…E2B4…`→`…E2B5…`）；`AuthenticationException: 对端证书未通过校验（pin-mismatch）`；**无 `local=` 行**（握手失败前拿不到端点） |
| timeout | `local=192.168.1.10:49577`；`peerClosed = eof t=5013 ms`（长度前缀绝对时限 5000 ms） |
| slow-dribble | `local=192.168.1.10:57131`；`已发 3/4 字节 t=4027 ms`；`peerClosed = eof sent=3/4 t=5000 ms` |

`[VERDICT] M3 = PENDING-HOST-EVIDENCE` 系设计（控制端不单独宣布里程碑）。

**B 侧**（runId `0e7f03d0`）四条连接行与 4 元组配对（唯一配对；配对方法见验收手册 §5）

| B 行（原文摘） | 配对依据 | 认定 |
| --- | --- | --- |
| `conn#1 peer=192.168.1.10:49575 outcome=PreAuthenticated rejection=-` | A `local=…:49575` | success |
| `conn#2 peer=192.168.1.10:49576 rejection=pre-auth-eof`（elapsedMs=3） | **唯一没有对应 `local=` 行的 B 行**；端口 49576 居 49575/49577 之间（顺序分配佐证） | pin-mismatch（TLS 1.3 半开窗口，验收手册 §3 场景 2 的预期形态） |
| `conn#3 peer=192.168.1.10:49577 rejection=pre-auth-timeout elapsedMs=5017` | A `local=…:49577` | timeout |
| `conn#4 peer=192.168.1.10:57131 rejection=pre-auth-timeout elapsedMs=5004` | A `local=…:57131` | slow-dribble |

**五约束核对：全过。** ① PreAuthenticated 恰 1 条；② pre-auth-timeout 恰 2 条（5017 / 5004 ms，
都贴 5 s）；③ 其余唯一一条是 `pre-auth-eof`；④ `connectionsEnteringSessionHandler=4` ∈ 3..4；
⑤ `listenersStoppedCleanly=True activeAtStop=0`。
另：`[HOST][BUCKETS] sessionHandled=4 active=0 sum=4 partitionOk=True {preauthenticated=1, rejected:pre-auth-eof=1, rejected:pre-auth-timeout=2}`、
`handlerFaults=0`、`tlsStageRejections=UNOBSERVABLE`（不填 0）。

**`INVALID_RUN` 的来历（设计使然，防误读）**

- 验收手册 §2 第 4 步要求 B 点「停止监听」收尾；该按钮**无条件**调 `MarkOperatorAbort`
  （`MainWindow.StopHostButton_Click`，注释原文：「停机会强行关掉 socket，而『连接被关闭』
  正是若干场景的通过条件。不整轮作废，就等于用一次按停操作伪造出通过」；`HostRole` 收尾处
  同条件再标一次）→ `Settle()` 机械返回 `InvalidRun` →
  `[RESULT] outcome=INVALID_RUN / aborted=True / exitCode=4`。
- 所以 **GUI 流程下被控端结局字段必为 `INVALID_RUN`**，证据不受影响。**别重跑、别当缺陷。**
- 替代形态（本机实测）：`--headless host --seconds N` 定时停机不走操作员中止路径——
  `--seconds 5` → `outcome=PASS`、`aborted=False`、退 0（该次 0 条连接，runId `9c4298e1`；
  顺带证明结局字段本来就不承担「场景跑没跑过」的判定）。
- 手册已澄清 4 处（§0.2 退出码表 / §2 第 4 步 / §4 / §8），并顺手修正 §4 两处滞后内容
  （BUCKETS 模板改为代码现状 `sum=… {桶=计数}`；UNOBSERVED 交叉引用 §5.1→§6.1）。
  **执行用 zip 未重打包**——物料必须与真机执行的字节一致；澄清只对日后重跑 / 后续里程碑生效。

**B 机 lab 全流程实测（同一份日志）**：apply ×2（第二次为 undo 后幂等重跑，两条规则均
`rule created`；第一次 `rule exists` + `rule created`）、undo ×1（`removed 192.168.1.20`、
两条规则删除、DHCP 恢复；网络类别保持 Private——ADR-036 既定行为）、info ×4；
`[LAB] 转发对账` 每次一致（129/129、104/104）。首次误操作轮（起监听后立即停止）→
INVALID_RUN 退 4、`sessionHandled=0`，与上述机制一致。

**遗留（文字性，不为它单独重打包）**：A 侧交叉核对清单标题写「下面四条」而实列 5 项
（`ClientRole` 文案）；与 `HostRole.cs` 前置失败文案一起，下次动代码时顺手改。

**收尾（已完成，两机 lab 均已不存在，第 24 步至此全链闭环）**：
B 机 2026-09-21 11:13Z 由用户在窗口点「撤销准备」（`lab-undo` PASS，转发对账 104/104：
`removed 192.168.1.20` + 两条规则删除 + DHCP 恢复）；
A 机 2026-09-21 11:21Z（本地 19:21）由开发侧以管理员身份直跑同物料 `--headless lab-undo`
（runId `037fd836`，`outcome=PASS` / 退 0；**不弹 UAC**——直跑的是提升动词本体，
进程本身已是管理员、没有父进程编排那一层，故没有「转发对账」行）。
A 机脚本自述：`removed 192.168.1.10` / `not present 192.168.1.20` / 两条规则删除 /
`switching interface back to DHCP … lease renewed.`；撤销后以太网 = `172.100.166.220/24`
（Dhcp，续租拿回同地址；非 RFC1918 属正常）。
撤销后两机 `--headless info` 均 `qualifiedNic=(无)` / `UNMET reason=no-qualified-rfc1918-nic`
（A 侧 runId `2f18741c` 退 2；B 侧 `8e702a4a`）。
A 机 undo 日志里的两条提示是**设计内路径**（脚本 v4 头部 revision notes 已预记，非缺陷）：
① `WARNING: saved state points at a lab address (192.168.1.10)`——旧版脚本曾把 lab 状态
写成「原始状态」，v3 起识别毒化状态文件并弃用、走 DHCP 兜底（脚本注释里的 "Measured on
machine A" 说的就是本机）；
② `network profile left as-is (no saved category to restore)`——无可用类别记录时保持
Private（B 机为 `already Private`）。类别还原逻辑自 v3 起存在（有记录才还原），
无记录不还原属设计内兜底——`UX_FIRST_RUN_REVIEW_TRIAGE` §6 已就此闭合（见该文件 §3.3 勘误）。
**本轮文档修订（手册 §1.2 / §9、TRIAGE §3.3 / §6）均不重打包**——执行用 zip 必须与
真机执行的字节一致；包内 `START-HERE.md` 保持打包时快照（25427 B），此后与
`docs/M3_TWO_MACHINE_ACCEPTANCE.md` 不再逐字节相同，属预期，日后重打自然刷新。

### M3 验收器的形态教训（用户连续三次纠错后定稿）

这三条是踩出来的，别改回去：

1. **入口必须是 exe，不是脚本。** 用户原话：「双击入口不应该就是一个exe可执行文件吗？
   为什么要用脚本？我们这个做的是**软件**啊」。本项目是桌面软件，
   M2 的两机验收就是两台机器开 WPF 界面跑的（§9.1「用户在 B 机界面确认」「B 点刷新」），
   §13 又定了「终端用户永远不需要打开 PowerShell」。
   → 验收器是 `WinExe` + `UseWPF`，双击 = 窗口。
2. **控制台 exe + `START.cmd` 是错解**（曾短暂加过，已删除）。
   它把验收又推回终端，而且双击控制台 exe 只会打一行 usage 然后退出，
   窗口一闪而过，用户会得出「包里没有 exe」的结论——用户的第一次反馈
   「你打包解压出来的，怎么没有启动exe」正是这个现象。
3. **`WinExe` 没有控制台**，所以 `Console.WriteLine` 全部不可见 →
   统一走 `AcceptanceLog`：**UI 日志区 + 每轮一个不可变文件**
   （`%TEMP%\lanremote-m3-acceptance\m3-<角色>-<UTC时刻>-<runId>.log`）；
   进程级事实（`[GUI] 窗口渲染完成` 这种「一次进程只有一次」的行）落 `gui.log`。
   另外必须给 `DispatcherUnhandledException` 落盘 `crash.log`，否则未捕获异常只会让窗口无声消失。

   > **更正（2026-09-21，实测发现）**：这一条原先写的是「UI + `gui.log` 双写」。
   > 那是 `f080581` **之前** `AcceptanceLog` 的行为；改成 per-run 文件之后这句话没跟着改，
   > 而「窗口渲染完成」那一行顺势只进了 UI——等于「窗口没崩」这个证据**带不走**
   > （排查 GUI 冒烟时被这个假线索带偏过一次：读到的 `gui.log` 是 7 小时前的遗留文件）。
   > 现在两处都补齐：per-run 文件是主证据，`gui.log` 记进程级事实，渲染行两边都写。

**踩到并修掉的坑：**

- **`InvariantGlobalization=true` 会让 WPF 直接崩**：窗口在首次 Measure 时抛
  `TypeInitializationException: 'MS.Internal.FontCache.MajorLanguages' 的类型初始值设定项引发异常`，
  内层是 `CultureNotFoundException: … 'en' is an invalid culture identifier`。
  WPF 字体缓存必须用真实 `CultureInfo`。**此项目必须 `false`。**
- **顺手纠正一个我写错的猜测**：改 `InvariantGlobalization` **不会**让包多带 ICU——
  Windows 上 .NET 用系统 ICU，发布目录里一个 `icu*` 文件都没有。
  体积从 77.8 → 132.2 MiB 全部来自 WPF 自身的程序集
  （`PresentationFramework` 15.8 MiB + `PresentationCore` 8.3 MiB + WPF 全套约 39 MiB），
  跟 globalization 无关。这就是「先测再写」的例子。
- **`<InvariantGlobalization>` 注释里不能出现 `--->`**：那是 XML 注释结束符，
  直接 `MSB4025: An XML comment cannot contain '--'`。
- **WPF 会带出 13 个语言的资源卫星目录**（`cs`/`de`/…/`zh-Hans`）→
  `<SatelliteResourceLanguages>en</SatelliteResourceLanguages>` 限掉，
  条目 422 → 265。配合 `<DebugType>embedded</DebugType>` 不再单独发 pdb。
- **`DispatcherUnhandledException` 处理里必须防重入**：
  若异常发生在排版/重绘路径（如上面的字体缓存崩溃），
  每次重绘都会立刻再抛一次，`MessageBox` 弹 → 重绘 → 再弹 …… 变成弹框风暴，
  用户除了强制结束进程什么都做不了。现在只弹第一次，写盘用 `AppendAllText` 保留全部现场。

    **提交 `5ae052f`** · 该提交只含验收器 / 手册 / 打包脚本 / HANDOFF，**无产品代码改动**
    （`Last code commit` 仍是 `5bf3cb6`）。zip 与 `artifacts/` 都在 .gitignore 里，
    需要时用 `make-m3-package.py` 重现。

### M3 明确不做

- AuthChallenge / HMAC / 访问密钥认证（M4）；视频通道与 Video Attach（M5）。
- UI 网络诊断（ADR-024，M9）、防火墙一键（ADR-025，M10）、临时私有地址一键（ADR-026，不早于 M10）。
- 改动 discovery 缓存语义（ADR-027 规定：M3 不动，最晚 M4 再引入 `IdentityConflict`）。

### 哪些步骤必须本机实测（**不能问模型**）

- 步骤 3、15：握手与取消延迟（本机有 oracle）
- 步骤 8：多网卡同端口 bind
- 步骤 24：跨机真实链路
- **TLS 1.2/1.3 在 Win10 22H2 上的行为本机无法验证**（本机是 Win11 25H2 / 26200）→ 标注未测，别外推

## 16. 下一位 AI 不要重复做

- **M2.1 两机验收已完成并 PASS**（第 9.1 节，20/20，discovery 轮）；**M3 两机验收
  （真实 TLS + pinning）已于 2026-09-21 跑完并 PASS**（第 15 节第 24 步记录）——
  **不要重复跑**，也不要被被控端结局字段 `INVALID_RUN` 迷惑（收尾机制的机械产物，
  判定看逐条连接证据）
- **不要重复做 M2.1 那次两机验收**：2026-09-20 已 PASS（第 9.1 节）。只有改动 discovery
  收发逻辑（网卡筛选 / probe / announce / 缓存 TTL）才需要重跑
- **不要把验收器改回「控制台 exe + 脚本驱动」**：用户已连纠三次。
  入口必须是 exe（双击开窗），headless 只是**同一个 exe** 的另一个入口
- **不要把 probe 回应发回 `remote.Port`**：probe 源端口是随机临时端口，回应必须打到 `remote.Address:45872`
  （实测三次源端口 `50193` / `54670` / `58869`，均为随机端口，回源端口对端必收不到）
- **不要删掉 sender 的 `MulticastInterface` 设置**：不设就会全部走系统默认组播路由（多网卡必踩）
- **不要在 capabilities 里恢复「空项静默跳过」或「按去重后数量判上限」**（两者都是可绕过的校验）
- **不要**为了图省事把 `StartDiscoveryAsync` 改回 `void`、或让 `LoadAsync` 无条件覆盖状态文本
- **不要重新定义** `ISubnetPolicy` / `DiscoveredDevice` / `DeviceIdentity` / `DeviceCode`
- **不要**用字符串前缀或「前三段相同」判断同网段
- **不要**往 announcement 里加地址字段并相信它（地址必须来自 UDP source）
- **不要**为了「支持 VPN 局域网」在 M2 放宽虚拟网卡过滤
- **不要**在 M2 实现任何 TLS / Auth / 视频 / 输入
- **不要在 M3~M8 实现 ADR-024 / 025 / 026**：UI 网络诊断属于 M9、防火墙一键与临时地址一键属于 M10。
  它们都已定好规则，缺的是时机，不是设计——**等用户下达对应里程碑的开工指令**
- **绝不在后台静默改防火墙或改 IP**：两者都必须经过「用户显式点击」+「UAC 确认」两道确认（ADR-025/026）。
  「改 IP 要无感」这句话被拆解为：**入口无感（在 App 内、不用开 PowerShell）+ 触发显式（用户自己点）**，
  不要把「无感」理解成「自动执行」
- **不要靠 `System.Text.Json` 的默认设置解析 `channel_hello`**：实测 `AllowDuplicateProperties` 默认
  **`True`** 且**后者覆盖前者**——`{"type":"channel_hello","type":"video"}` 默认会解析成 `video`。
  hello 解析器必须显式：`AllowDuplicateProperties=false`、`UnmappedMemberHandling=Disallow`、显式 `MaxDepth`
  （注意该属性默认值是 `0`，表示采用内置上限 64，文档里别写成「默认 64」）
- **不要依赖 TLS 相关默认值**：`AllowTlsResume` **两端默认都是 `True`**、`AllowRenegotiation` 客户端默认 `True`、
  `EnabledSslProtocols` 默认 `None`——三个都必须显式设置
- **不要把 `expectedPin` 当作 M4 transcript 的唯一绑定对象**：必须绑定 M3 实际出示的 `presentedPin`（ADR-028），
  否则留下「一条连接收 nonce、另一条连接中继」的凭据中继缺口
- **绝不使用 `EphemeralKeySet` 载入 TLS 服务端证书**（ADR-029，实测 9/9 失败）。
  也不要因为「单元测试没报错」就以为它可用——**同一进程先 `PersistKeySet` 导入过同一私钥后，
  `EphemeralKeySet` 会碰巧成功**（实测污染）。flag 结论必须在新进程、顺序受控下测
- **不要只根据客户端异常判断 TLS 失败原因**：客户端只会看到 `IOException: unexpected EOF`，
  真实原因在**服务端**异常里（`AuthenticationException` + Win32 inner）。spike 必须两边都捕获
- **别忘了 .NET 10 的证书校验回调参数是 `X509Certificate`（基类）**，没有 `RawData` 属性；
  算 pin 要用 `cert.GetRawCertData()`（或转型成 `X509Certificate2`）。编译器会直接报错提醒，别绕过去
- **不要把 `CertificateRequest.CreateSelfSigned(...)` 的结果直接当 TLS 服务端证书**（ADR-030）：
  它的私钥是 ephemeral 的，服务端会在 `AcquireCredentialsHandle` 处失败
  （`... does not support ephemeral keys.` ← `0x8009030E`，与 ADR-029 同一个错）。
  **必须**导出 PFX 后用 `DefaultKeySet` 重新导入——`DeviceCertificateService` 里那步
  PFX 往返是必需环节，不是可以"简化"掉的冗余
- **不要给 `SslClientAuthenticationOptions.TargetHost` 填设备相关的名字**：CN 是
  `LanRemote-<设备码>`，SNI 是明文，等于向整个局域网广播设备标识。pinning 下主机名校验不参与
  安全判定，`TargetHost = string.Empty` 实测可用（不发 SNI、不做名字校验）
- **不要把 `TransportTimeouts` 里的数值当成实测结论**：它们是初始值，步骤 15 实测取消延迟后要回来调。
  ⚠️ 步骤 15 **只验证了「时限执行得准」（0–36 ms 误差）**，**没有**验证「这五个数值合不合适」——
  后者留给第二轮外部评审，别把前者当成后者的结论
- **不要把 `FrameReader.ReadExactlyAsync` 里的 `CancelAfter` 挪进读循环**（评审 A-8）：
  那会把绝对 deadline 变成滑动窗口，被「每 `timeout - ε` 发一字节」无限续命。
  变异已验证：挪进循环后低速攻击用例立刻变红
- **不要按取消异常的子类下断言**：真实 `SslStream` 抛的是基类 `OperationCanceledException`，
  而 `MemoryStream`/`Task.Delay` 替身抛的是 `TaskCanceledException`。
  断言写 `is OperationCanceledException` 即可，写 `IsType<>` 两边必有一边假红
- **不要让阶段超时飞出 `RunAsync`**：它的异常类型与「停机取消」完全相同，
  不接住就等于「结局永远不产生 + 两种取消无法区分」。必须用
  `when (!cancellationToken.IsCancellationRequested)` 分开（阶段 4 已修，见第 15 节）
- **不要在 `JsonSerializer.Deserialize` 之后就以为报文是「恰好一个 JSON 值」**：
  尾随内容不保证被拒。pre-auth 这类输入必须自己先用 `Utf8JsonReader.TrySkip()` 探一次边界
- **不要往 `AllowedOperationsWhilePreAuthenticated` 里加东西**：它在 M3 必须是空集合，
  有测试盯着。真到了 M4，加的应当是「开始访问密钥认证」这一项，而不是「顺手先支持的」能力
- **不要在测试里从 `MemoryStream` 派生并同时重写 `Read(Span<byte>)` 与 `ReadAsync(Memory<byte>)`**：
  一次读会被数**两遍**——`Stream.Read(Span<byte>)` 的默认实现会**虚拟调用** `Read(byte[], int, int)`。
  实测输出 `async=1 span=0 array=1` 却 `BytesRead=8`（真实只消费 4）。要计数就**包装**内部流，
  且用「计数器 + 流自身 Position」两把尺子交叉验证
- **不要声称 `ExclusiveAddressUse` 能发现端口已被占用**（ADR-032，实测证伪）。
  它能挡的只有「带 `SO_REUSEADDR` 的后来者」，而且在本机开不开结果一样。
  真正的冲突信号是 `AddressAlreadyInUse`，已在 `TransportHostStartResult.Failures` 里
- **变异验证不能只做一半**：改完代码必须确认测试**真的变红**再还原。
  绿着不动手很可能是测试隔离错了（阶段 2 就踩过一次，见 ADR-032 末尾）
- **不要用改系统时钟来测证书有效期**：一律用 `TimeProvider` 注入（测试里 `FakeClock`）。
  另外造过期证书时要注意——证书按真实时间也得有效，否则服务端 Schannel 会先因过期自行拒绝，
  测出来的就不是"我们的校验器拒绝了它"
- **不要照抄直觉去改 IP**：Windows IPv4 是「DHCP 或静态」二选一；追加第二地址前必须先把整张接口
  切成静态并回填原配置，否则会丢 DHCP 租约只剩 169.254（本机已两次踩断）。撤销必须幂等，
  且**不能靠 `netsh` 退出码判成败**。完整约束见 ADR-026
- **不要给这个项目做「控制台 exe + 批处理启动器」形态的工具**：
  本项目是桌面软件，M2 验收就是在两台机器的 WPF 界面上做的（§9.1），
  §13 的总原则是「终端用户永远不需要打开 PowerShell」。
  用户已连续三次纠错（「怎么没有启动exe」→「双击入口不应该就是一个exe 」→「我们做的是软件」）。
  验收器必须是 `WinExe` + `UseWPF`，**双击 = 窗口**，不需要任何脚本
- **不要在前面的注释里写没验证过的因果**：我曾断言「关掉 `InvariantGlobalization` 会让包多带 ICU」——
  实测 Windows 上 .NET 用系统 ICU，发布目录一个 `icu*` 都没有；
  体积从 77.8 涨到 132.2 MiB 全部是 WPF 自身程序集（约 39 MiB）。
  写注释前先 `ls` 一下，别把猜测写成事实
- **不要给 WPF 程序开 `InvariantGlobalization`**：窗口会在首次 Measure 时崩在
  `MS.Internal.FontCache.MajorLanguages`（`CultureNotFoundException: … 'en' is an invalid
  culture identifier`）。同时 `DispatcherUnhandledException` 的处理**必须防重入**，
  否则渲染期异常会造成「弹框 → 重绘 → 再弹」风暴，用户连日志都读不到
- **不要忘了 XML 注释里不能出现 `--`**：`<PropertyGroup>` 里的注释写 `--->`（写异常链很自然）
  会直接 `MSB4025: An XML comment cannot contain '--'`，项目都加载不了
- **不要把「对端握手失败」当成 PASS**：端口没开 / 防火墙拦 / host 没启动同样会让握手失败。
  这条空洞断言在 M3 修过两轮：先补了独立 TCP 探针，**「先修再跑」时又删掉**（见 24.1：
  探针自身的判据是空的，还会污染被控端连接计数、制造 TIME_WAIT）。「没测成」与「测出来
  不合格」的分离改由**结局映射**承担：TCP 不可达抛 `SocketException`/`IOException`
  （**永不** `AuthenticationException`）→ 退 `2`；真的连上、死在 TLS 之后才退 `1`
- **不要伪造构建/测试结果**：本文件所有数字均为实际执行输出
- **验收脚本别再犯这 5 个错**（详见第 9.1 节末表）：`netsh` 退出码不可信、
  `-join` 在 PS 5.1 的参数绑定陷阱、自动选网卡必须排除虚拟网卡、
  Windows IPv4 不支持「DHCP + 附加静态」共存、`check-logs` 必须带 `-Since` 才准
- **别拿 `LanRemote.App` 验收传输层**：它 `ProjectReference` 了 `LanRemote.Transport` 却**零调用**。
  任何「打 App 的包去两机跑」的方案都只能重证 discovery，碰不到 TLS/pinning/hello
- **验收器里禁止「失败即 PASS」**：对端端口没开、防火墙拦掉、host 没启动，都会让握手失败。
  「没连上」**绝不能**报 `0`，它必须落退 `2`（前置条件不满足）；**不要**再为此加独立 TCP
  探针（已删，理由见上一条）。按 `SocketError` 区分「没连上」与「被拒绝」时，
  **不要**把 `ConnectionReset` 归进「没连上」——accept 后立刻关闭正是走 RST
- **不要把「对端怎么收尾」压成一个布尔**：`catch → return true` 会让「读超时」和「被切断」
  在输出里长得一模一样。要区分为 `eof` / `reset` / `still-open` / `unexpected-data` 四种，
  判定标准才写得出来
- **写验收手册前先确认被控端到底会不会打印**：`TransportHost` 无 logger，同子网拒绝 / 准入拒绝 /
  TLS 失败**全是静默**；只有发生在会话处理器**内部**的 pre-auth 超时才有输出。
  手册里不要凭"应该会记日志吧"去写判定标准（见 §14 第 12 条）
- **不要把子进程输出按「OEM 代码页」解**（2026-09-21 实测纠错）：Windows PowerShell 5.1
  在 stdout 是**管道**时按控制台代码页（936）编码 `Write-Host`，而 .NET 10 **没有内置
  CP936 解码器**（`Encoding.GetEncoding(936)` 直接抛；要用得引
  `System.Text.Encoding.CodePages` 包——本项目不引）。现行契约：脚本在
  `[Console]::IsOutputRedirected` 时主动切 UTF-8 输出，harness 固定按 UTF-8 解，
  输出出现 `U+FFFD` 记 WARN。中文权威副本永远看 `[LAB][SCRIPT]` 段
  （脚本自己 `Add-Content -Encoding UTF8` 写 `%TEMP%\lanremote-lab-ip.log`）
- **不要把 `gui.log` 当「每轮日志」**：它是**进程级事实**文件（如
  `[GUI] 窗口渲染完成`）；每轮验收证据在不可变的 per-run 文件
  `m3-<角色>-<UTC>-<runId>.log`。混淆过一次：排查 GUI 冒烟时读到的 `gui.log`
  是 7 小时前的遗留文件，「窗口没崩」的证据其实带不走
- **「按行数转发日志」的照抄陷阱**：tailer 的 `usable` 必须 = 「**确定写完**的行数」——
  `Split('\n')` 的末段永远是特例（空占位或半行）。本工具曾因此**静默丢 7 行**
  （文件正好以换行结尾时 `_emitted` 越过空占位，下一行恰好占用该位置 → 永不转发）。
  必须配**独立算法**对账（`CountCompleteLines` 数 `\n`），聚合计数相等不算证明
- **不要给提升实例动词（`lab-apply` / `lab-undo`）加功能或放宽参数面**：它们要求进程
  本身已是管理员，参数面故意做窄（ADR-035：一次提权一件事、TOCTOU 重校验、UAC 拒绝
  不循环）。要新能力加在编排者一侧（`prepare-lab` / 窗口按钮）

## 17. 关键上下文

1. **`dotnet` 不在 PATH**：先 `source scripts/env.sh`
2. **本机唯一活跃网卡 `172.100.166.220` 不是 RFC1918** → 本机跑不通发现（不是 bug）
3. **模拟「重启」/「新进程」必须新建 `DpapiSecretVault`**（有缓存）
4. **`WatchAsync` 只有 upsert，没有 remove event**；UI 自己按 `LastSeen` prune
5. **`AllowDiscovery=false` 只停 announce 与 probe 响应**，不停扫描、不停接收
6. **`ProbeAsync` 是 M2 唯一新增的接口方法**，不要再加别的
7. **`DiscoveryRuntimeState.Initialize` 之前拿不到快照**（抛异常）——这是隐私保证
8. **`X509Certificate2.NotBefore/NotAfter` 返回本地时间**，与 `DateTime.UtcNow` 比较前要 `.ToUniversalTime()`
9. **`TryDecodeExact` 失败时 out 是 `Array.Empty<byte>()`**
10. **HANDOFF 不写 HEAD hash**，写 `Last code commit` + `Working tree at validation`
11. **probe 回应端口是常量 45872，不是 probe 的源端口**——这是本轮修掉的阻断性 bug，别写回去
12. **`IP_MULTICAST_IF` 只接受网络序的接口 IPv4 地址或 `0.x.x.x` 形式的索引**；
    对地址做 `HostToNetworkOrder` 会抛 `SocketException`（本机实测，见 §1.5.2）
13. **Discovery 项目对 `LanRemote.Protocol.Tests` 开了 `InternalsVisibleTo`**，
    只为两个 internal 纯函数服务；不要顺手把别的内部类型变成测试依赖
14. **ADR-018 曾被错标成 ADR-021**（2026-09-20 已修正）：ADR-016 说的是「证书加载改用 EphemeralKeySet」
    被 ADR-018 取代，而 ADR-021 是另一条「ECDSA 证书 KeyUsage 只允许 digitalSignature」。
    引用时看清条目正文，不要按编号顺序去猜
15. **不要给验收器写「凭症状推断出来的数字」**（2026-09-21）：交叉核对里那个
    「`connectionsEnteringSessionHandler` 应为 3」就是这么写出来的，实测是 4（TLS 1.3 幽灵行）。
    只写**可证伪的约束**，并让区间取决于「连接走到了哪一步」而不是「场景是否通过」。
16. **不要让验收器的返回值语义和调用方的理解相反**（2026-09-21）：
    `HeadlessCommand.TryParse` 曾用 `return true` 表示「参数错误」，调用方按字面读成「成功」，
    于是 `RunHeadless(null)` → NRE → 被顶层兜底成退 3，**错误消息一个字都没打出来**。
    这种错两边各自自洽、单元测试发现不了——只有端到端跑一遍并**核对输出字节数**才看得见。
    现在加了一条铁律：**新增任何 CLI 参数的第一件事，是把错误路径的输出字节数核一遍。**
17. **不要靠「计数相等」配对两条日志**（2026-09-21）：用 4 元组
    （控制端 `local=<IP>:<临时端口>` ≡ 被控端 `peer=<IP>:<端口>`）。
    聚合数相等不构成配对证明。
18. **不要把 `ReadAsync == 0` 说成「对端发了 `close_notify`」**（2026-09-21 实测）：
    裸 TCP FIN 也返回 0。措辞只能是「**有序 EOF（TLS 记录层 EOF）**」。
19. **不要从「客户端看到握手失败」推出「服务端没进会话」**（2026-09-21 实测）：
    TLS 1.3 下服务端可能已经完成握手、照样进会话处理器并给出 `rejection=pre-auth-eof`。
    判定 TLS 失败**永远抓服务端异常**，客户端只有 EOF。
20. **`slow-dribble` 不能省**：`timeout` 单独区分不出「绝对时限」和「可重置空闲时限」
    （变异矩阵第 9 例实测：`resettable` 下 `timeout` 仍然绿）。它是那个场景存在的唯一理由。
21. **不要拿 `LanRemote.App` 验收传输层**：它 `ProjectReference` 了 `LanRemote.Transport`
    却**零调用**。打它的包只能重证 discovery。
22. **不要为了跑通场景去改产品协议**：评审提过「成功场景可以在应用数据里带场景 ID」，
    这是典型的「为测试改被测对象」。用 4 元组关联即可。
23. **不要在验收器里用 UI 线程做测量**：`MainWindow` 的点击处理器是 `async void`，
    而 `ClientRole` 没有 `ConfigureAwait(false)`——续体会被投回 UI 线程，
    日志区滚动/排版时会延迟执行，正好打在靠时间判定的场景上。headless 一律走 `Task.Run`。
24. **不要把「prompt 里怎么描述」当成「代码里怎么实现」**（2026-09-21）：第二轮评审材料把
    `HelloTimeout`（**已声明、未接线**）写成了「五段之一」，评审据此做了正确的加法推演、得出错误的
    「~20 s」结论；服务端实际最坏 = 前缀 5 s + payload 10 s = **15 s**。写「已建成系统」类材料前，
    每一条都要能指到代码/测试证据（该评审另有两处真发现，见 §18.4）。
25. **分段绝对时限不蕴含「总量有界」**（2026-09-21）：`FrameReader.ReadFrameAsync` 两段各自绝对
    （防滑动窗口），但**顺序执行即可加和**（5 s + 10 s = 15 s）；8 个准入槽循环占用依旧成立。
    凡「多段顺序等待」的场景必须显式外层信封 + 专项测试——M3.1 落地 `PreAuthEnvelopeTimeout`（§18.4）。

## 18. 下一步 —— M4（Access Key Challenge Auth）· 计划与执行

**状态：进行中。**阶段 0（衔接盘点与定案）**已完成（2026-09-21）**，含 ADR-027 落地件；
阶段 1（transcript + HMAC 纯函数核心）**已完成（2026-09-22）**；
阶段 2（认证消息帧 JSON 严格解析）**已完成（2026-09-22）**。
执行记录见 §18.5（阶段 0）/ §18.6（阶段 1）/ §18.7（阶段 2），下一步为阶段 3
（服务端认证状态机，步骤 13–17）。
（M3 已全链闭环：24 步 + 两机验收 PASS + 两机 lab 还原；仓库已推 GitHub。）
本计划按 `LanRemote_Implementation_Package/04_PROTOCOL_AND_SECURITY.md` §9/§14/§15 +
`07_MILESTONES_AND_TASKS.md` M4 编制；阶段 0 的盘点结论已落为 ADR-037/038（`docs/DECISIONS.md`）。

**规格任务（9 项）**：AuthChallenge；canonical transcript builder；HMAC proof；server proof；
timeout；failed auth limiter；local approval dialog；sessionToken；session registry。

**规格测试（8 项）**：deterministic transcript；correct key success；wrong key fail；
modified cert fingerprint fail；modified permission fail；expired challenge fail；
5 failures limiter；serverProof client validation。

**DoD（2 条）**：绝不发送 raw access key；auth success 后才能有 session。

### 18.1 从 M3 继承的硬约束（开工前必修）

- **ADR-028**：transcript 必须绑定 M3 实际出示的 `presentedPin`
  （`ConnectionIdentity.PresentedCertSha256`，不可变）——否则留下「一条连接收 nonce、
  另一条连接中继」的凭据中继缺口（§16 有专条）。
- **`PreAuthenticated_Allows_Nothing_Before_M4` 门禁**：M4 落地时往
  `AllowedOperationsWhilePreAuthenticated` 里加的应当是「开始访问密钥认证」**这一项**，
  而不是「顺手先支持的」能力（§16）。
- **ADR-027**：同 deviceId 不同指纹不得静默覆盖 → `IdentityConflict`，最晚 M4 落地
  （M3 有意推迟，A-4 在 M3 故意没有测试）。
- **auth 帧解析照 `HelloFrame` 模式**：`AllowDuplicateProperties=false`、
  `UnmappedMemberHandling=Disallow`、显式 `MaxDepth`、拒绝尾随数据、非法 UTF-8 不兜底。
- **pre-auth 单帧 4 KiB 上限（ADR-033）**：challenge / response 都在认证前，受此上限约束。
- **禁用**：raw key 绝不上网；key / proof 绝不进日志；不自研密码学（白名单外的一律不做）。

### 18.2 阶段划分（草案，21 步）

**阶段 0 —— 衔接盘点与定案（开工第一件事）—— ✅ 已完成（2026-09-21，见 §18.5）**

1. ✅ 读 `ControlPreAuthSession` / `ControlSessionState` / `TransportHost` 现状，定
   「PreAuthenticated 之后」的衔接：**定案 (b) 显式交接**，落为 **ADR-037**
   （`ControlPreAuthHandoff` 线性所有权 + exactly-once + `ConnectionSecurityContext` 冻结传递；
   M3 的「成功即干净关闭」终态被显式交接取代）。
2. ✅ 定 `AllowedOperationsWhilePreAuthenticated` 的 M4 改造形式与门禁测试的改写：**恰好一项
   `"begin-authentication"`**；旧空集合测试**有意识改写**（精确集合 + exactly-once 行为），
   见 ADR-037 第 5 条。
3. ✅ ADR-027 `IdentityConflict` 定案（`DiscoveryDeviceCache` 冲突字段与丢弃策略）——
   **并已随本阶段落地**（实现 + 8 条新测试 + 1 处既有测试有意识改写；见 §18.5）。
4. ✅ 第二轮外部评审输入：已回收（2026-09-21，见 §18.4）。

**阶段 1 —— transcript + HMAC proof（纯函数核心）** ✅ 完成（2026-09-22，执行记录见 §18.6）

5. ✅ `AuthProtocol` 常量：版本串 `LANREMOTE-AUTH-V1`、字段名、各时限。
6. ✅ `AuthTranscriptBuilder`：固定字段顺序、`\0` 分隔、base64 canonical、uppercase hex；
   输入全部字节级确定（uuid 串 / base64 / hex / 枚举）。
7. ✅ `clientProof = HMAC-SHA256(accessKeyBytes, ClientAuthTranscript)`；
   `serverProof = HMAC-SHA256(accessKeyBytes, UTF8("server\0") || ServerGrantTranscript)`
   ——**本条已按 ADR-038 修订为双档拆分**：计划原文的「serverProof 绑单 transcript」作废，
   现为「绑 ServerGrantTranscript（域分隔 `LANREMOTE-GRANT-V1` + SHA256(client 档) + granted）」。
8. ✅ 测试：deterministic transcript（同输入字节级相同）；correct / wrong key；
   modified cert fingerprint / modified permission（改任一字段必改 proof）；
   另加：黄金向量逐字节（3 向量 + granted 篡改对照）、双档 NUL 分割（8/3 段）、参数校验。

**阶段 2 —— 认证消息帧（JSON 严格解析）** ✅ 完成（2026-09-22，执行记录见 §18.7）

9. ✅ `AuthChallengeFrame`（→client）：`sessionId` / `serverDeviceId` / `serverNonce`(b64 32B) /
   `certSha256`(HEX) / `expiresInMs`——另含 `type` 共 7 字段；拒绝码族 `challenge-*`。
10. ✅ `AuthResponseFrame`（→server）：`clientDeviceId` / `clientName` / `clientNonce` /
    `requestedPermission` / `clientProof`(b64)——`clientName` 硬化（非空 / ≤64 /
    无控制字符 / UTF-16 良构；ADR-040 第 6 条）。
11. ✅ `AuthSuccessFrame` + `ApprovalPendingFrame`（→client）：`grantedPermission` /
    `serverProof` / `sessionToken` / `videoAttachExpiresInMs`——**另加计划外
    `AuthenticationFailedFrame`**（阶段 3/4 公共前置件；ADR-040 第 5 条，记录在案）。
12. ✅ `base64 canonical` 定义与测试——**判定式 = round-trip 逐字符相等**；
    另有 canonical HEX / canonical GUID 同规则、跨帧 wrong-type 分类（ADR-040 第 1/3 条）。

**阶段 3 —— 服务端认证状态机** ✅ 完成（2026-09-22，执行记录见 §18.8）

13. ✅ challenge 生成（**构造期定稿**：`sessionId` / nonce32 / `certSha256`←冻结上下文 /
    `expiresInMs=15000`）与超时（**绝对** deadline = 机器窗口 10 s，覆盖写/读/校验；
    压线后置校验）。
14. ✅ 服务端验证链：限流前置 / 机器窗口 / 帧级严格解析 / 密钥加载（**解析通过后才触碰**）/
    重算 + `FixedTimeEquals`（唯一计限流失败 + 300–800 ms 随机延时）/ 失败一律 generic
    `authentication_failed`。**「session+device 对齐」不做独立字段比对**——proof 重算把
    session / device / 两端 nonce / 证书指纹全部隐式绑死（跨会话重放必然 MAC 失败）。
15. ✅ failed auth limiter（按 remote IP）：10 分钟窗口 / 5 失败 → 拒 60 s /
    每次失败 300–800 ms 随机延时 / 成功清计数 / 不打日志；封禁到点放行、再失败即再封。
16. ✅ local approval（`ILocalApprovalGate` 5 值显式终态 + fail closed；v1 = 每个新控制连接
    都要批，ADR-038）+ `approval_pending` 流程（三路竞速 + 单读者复用）。
17. ✅ `sessionToken`（32 随机字节）+ `SessionRegistry`（**success 字节写出成功**才注册
    ——ADR-041 第 1 条；DoD 的可执行形式）。

**阶段 4 —— 客户端侧：实现/自动化验证完成，验收器展示接线已完成；实际窗口展示待人工验收（§18.10/§18.11）**

18. [完成] 客户端认证流程：hello → challenge → clientNonce → transcript（**绑 presentedPin**）→
    response → success → 验证 serverProof；高层入口独占中间连接，成功才返回已认证会话。
19. [实现/接线完成，人工显示未验] serverProof 验证失败 → 立即断开 + 固定文案「远端身份验证失败，可能是错误密码或
    伪造设备广播」+ 不发送输入；文案由认证异常提供，已接入验收器错误路径，不声称已完成人工窗口验证。
20. [完成] 客户端 e2e（真 TLS 双端）：correct key success；wrong key `authentication_failed`；
    serverProof 篡改必拒；独立 UTF-8/HMAC 对端及权限/身份/时限/秘密副本测试已覆盖。

**阶段 5 —— 收口**

21. [自动化完成 / 人工未完成] 全量 `build` + `test`、真实回环 TLS 接线、8 项运行期变异及 HANDOFF 记账完成；
    GUI 验收器仍为双击即窗口。**人工 GUI 与真实两机认证演练待执行，不以回环或发布成功代替**（§18.11）。

### 18.3 未决点（阶段 0 / 用户对齐）—— 均已关闭

- 衔接层位置：**已定 (b)**——显式交接（`ControlPreAuthSession → ControlAuthSession`），
  评审与本机基线一致；增强件（线性所有权对象、exactly-once、测试改写）见 §18.4。
  **已落为 ADR-037（2026-09-21）。**
- `local approval dialog` 的 M4 边界：**已拍板 (b)（2026-09-21）**——`ILocalApprovalGate` 库真接口
  + 单测替身 + 验收器最小审批面（评审论证推翻了本机此前 (a) 倾向；本机采纳评审论证）。
  产品 WPF UI 仍不动。
- ADR-027 冲突字段与丢弃策略：**已定案并落地（2026-09-21）**——ADR-027 修订段 + §18.5 C 节。
- 第二轮外部评审的时机：**已回收**（2026-09-21，先行完成，见 §18.4）。

### 18.4 第二轮外部评审结论 → 计划修订（2026-09-21 回收）

分流全文：`docs/M3_IMPLEMENTATION_REVIEW_TRIAGE.md`（含 3+2 处「评审前提 ≠ 代码事实」的逐条核对）。
计划层面的净修订如下。

**A. 先做：M3.1 加固（M4 之前；少量代码 + 测试补强）—— ✅ 已完成（2026-09-21）**

- `PreAuthEnvelopeTimeout`（初值 **8s**，provisional）：pre-auth **外层信封**——自会话进入起算、
  永不重置，覆盖前缀 + payload + 解析 + 收尾；修复「分段绝对 ≠ 总量有界（5+10=15 s 可加和）」。
  测试=吃满前缀再拖 payload，必须在信封到点被切（缩放值）。
- `HelloTimeout` 语义收拾：它**从未被服务端接线**（唯一消费=验收器客户端写超时 + 日志行）——
  文档改为写预算，不再暗示独立顺序段（§17 教训 #24）。
- 测试补强：跨 listener 共享限额（B15）；真实 TLS 粘包（B16）；字节边界取消矩阵（B19）；
  准入释放矩阵补路径（B20）；`StopAllAsync` 未完成计数 + false 路径测试（B18）。
- **不重开** M3 两机验收（信封用本机真实 TLS 集成测试证明）；如再跑验收：物料需重打（src 有改动）。

**A. 执行记录（2026-09-21 收尾，单机全量验证）**

- 全部按上述范围落地（9 步批次），实现 == 计划，无范围外改动。
- 变异验证（贯穿纪律；恢复后均以 `git diff` 确认为空）：真实 TLS 帧边界 ×2、跨 listener 限额
  与名额归还 ×2、停机未完成计数 ×1、字节边界矩阵 ×2——全部「禁用实现 → 精确变红 → 恢复 → 全绿」。
  例：字节边界矩阵的单读变异 13 红 / 14 绿；停机计数变异精确点红「未完成计数」与「停机预算」
  两条用例（其余 17 条不受影响）。
- B19 一处设计纠错（值得记住）：**读取器消费掉的字节无法退回**——「半前缀超时后再读」必然错位
  （读侧把后 2 个前缀字节当成新前缀头 → 前缀值巨大 → 超限拒绝，`FrameProtocolException`）。
  恢复性场景必须用 **0 字节失败**；对应用例定为「0 字节超时 → 后续读完整帧成功」。
- 验收器同步：`AcceptanceProfile`（信封 8 s + 既有判据核对：所有场景的期望收尾时刻早于信封；
  slow-dribble 判据 `sent<4` 不受影响）；`HostRole`（停机改用 `TransportHostStopReport`，SUMMARY
  增 `unfinishedConnections` / `acceptLoopsFinished`）；`AcceptanceRun`（`deadline.hello` 行语义
  修正——标注「客户端写预算；服务端不消费」；新增 `deadline.preAuthEnvelope` 行）。
- 全量验证：Debug + Release `dotnet build` **0 警告 0 错误**；`dotnet test` **601 PASS / 0 FAIL**
  （Protocol 215 + Transport 192 + Core 125 + Security 66 + Integration 3）。
- 未重开 M3 两机验收（同计划）；如之后重跑两机：验收物料必须重打。

**B. M4 计划修订（阶段 0 落实为 ADR-027 修订）**

- **transcript 拆两个**：`ClientAuthTranscript`（绑 requestedPermission）与 `ServerGrantTranscript`
  （域分隔 + H(客户端 transcript) + grantedPermission）；`serverProof` 绑后者——修复「双 proof 同 transcript
  无法绑定尚未决定的 granted」（评审最重要的设计发现）。规格内部冲突「`server|` vs `server\0`」以 04 为准。
- 衔接：**(b) 显式交接**；线性所有权对象（live stream + 冻结安全上下文 + connectionId + 生命周期），
  exactly-once；门禁改为「`PreAuthenticated` 只允许一次 `BeginAuthentication` 转移」；
  旧空集合测试**有意识改写**而非删除。
- dispatch 显式门禁 `state == Authenticated && granted >= required`（空集合仅 defense-in-depth）。
- 限流：只计「到达密码学校验且失败」；聚合遥测（A8）；审批拒绝/超时**不**计（D2/D3 裁定）。
- 审批：**v1 每个新控制连接都要批**（不做运行期记住）；`PendingApproval` 独立配额（初值全局 3 / 源 1）；
  显示最小集 + 短关联码；请求不可变、原子终态、断连不发 token；UI 不可用 fail closed。
- 时限初值（provisional）：机器认证 **10s** / 人类审批 **60s**（获批面受理起算）。
- `ConnectionSecurityContext` 冻结传递（含本连接实际证书指纹）；`certSha256` 从该上下文派生。

**C. 数值实验（评审 D1；全部 [NEEDS LOCAL EXPERIMENT]，先不动数值）**

- ① 冷启动 + 忙 CPU（TLS 5s）；② 受控首 SYN 丢失（connect 3↔5s）；③ Wi-Fi 抖动（如适用）；
  ④ 8 并发握手资源（顺带核 global=8）。完成后一次定案（五段 + 信封）并进 ADR。
- **丢包实验涉及环境改动——走用户通道，不静默动网。**

**D. 待用户拍板（M4 开工前）—— ✅ 已拍板（2026-09-21）：(b)**

- 审批机制边界：评审建议 (b)（验收器加最小审批面）vs 本机此前 (a)（纯抽象 + 替身）——
  用户已确认 **(b)**：`ILocalApprovalGate` 库真接口 + 单测替身 + 验收器最小审批面。
  M4 阶段 0 的审批设计按 (b) 落地。

---

### 18.5 阶段 0 执行记录（2026-09-21）—— 衔接盘点 / 定案 / ADR-027 落地

**A. 源码盘点结论（读代码得出的事实，非推测）**

- `ControlPreAuthSession.RunAsync` 成功路径 = `State=PreAuthenticated` → **立即 `ShutdownAsync`** →
  返回 `ControlPreAuthResult(true, PreAuthenticated, null)`；结果类型**不携带**任何续行对象
  （无流 / 无身份 / 无 connectionId）。M3 步骤 21 有意没做 `AwaitingAuthentication` 占位 → 衔接必须显式。
- `AcceptedConnection` 只有 `(LocalAddress, RemoteAddress, RemotePort, Stream, NegotiatedProtocol)`——
  **无 connectionId、无服务端证书指纹** → M4 需要 `ConnectionSecurityContext` 补这两样（ADR-037 第 3 条）。
- 唯一产品级接线点 = 验收器 `HostRole`（`new ControlPreAuthSession()`）；`LanRemote.App` 未接线
  （「别拿 `LanRemote.App` 验传输层」依旧成立）。
- 门禁测试 = `PreAuthenticated_Allows_Nothing_Before_M4`（`Assert.Empty`）——改写方案见 ADR-037 第 5 条。
- 流所有权：`TransportHost.HandleAsync` 的 `finally` 释放（Host 是最终拥有者）——M4 交接不改变这一点。

**B. 定案产出（`docs/DECISIONS.md`）**

- **ADR-027 修订**：M4 阶段 0 实现定案（冲突字段 `Entry { Device, Conflicts }` / 每目 8 条上限 /
  `IsIdentityConflicted` 惰性清除 / 主条目整体过期的显式边界）。
- **新增 ADR-037**：M4 衔接层（显式交接 + 线性所有权 `ControlPreAuthHandoff` + exactly-once +
  `ConnectionSecurityContext` 冻结传递 + `ControlSessionState` 扩展 + allowed-ops 恰好一项 +
  流所有权链 + 认证成功后的连接归宿 + 4 KiB 帧上限沿用 + 旧测试改写清单）。
- **新增 ADR-038**：M4 认证协议定案（**双 transcript 拆分**含精确字节布局 / `server\0` 域前缀 /
  限流只计「到达密码学校验且失败」 / 审批 v1「每个新连接都要批」+ PendingApproval 配额 3/1 /
  时限初值 10s + 60s（provisional） / DoD 两个可执行形式）。

**C. ADR-027 落地件（本阶段唯一的代码改动）**

- `DiscoveryDeviceCache`：`Dictionary<Guid, DiscoveredDevice>` → `Dictionary<Guid, Entry>`；
  `Upsert` 分流（同指纹照旧 last-write-wins；异指纹只记冲突、不覆盖主条目）；
  新增 `IsIdentityConflicted` / `ConflictingFingerprintCount`；冲突与条目过期清理。
- 测试：`DiscoveryDeviceCacheTests` **+8**（ADR 验证 ①②③ + 大小写不敏感 + 冲突被刷新则持续 +
  主条目整体过期边界 + 上限有界 + unknown）；**既有测试 1 处有意识改写**：
  `TlsClientConnectorTests.Frozen_Pin_Survives_Discovery_Cache_Mutation_Mid_Handshake`——
  M3 版本假设「缓存会被 last-write-wins 换指纹」，ADR-027 落地后该假设失效；改写后同时断言
  「投毒不落地（主条目保持 pinA + 冲突被标记）」与「冻结快照不受影响」+ 伪造目标必失败
  （验证面比 M3 版本更宽）。**测试名保留**（3 处文档引用：TRIAGE / 本节 / §16 表）。
- 变异验证（3 次；恢复后均以 `git diff` + `grep TEMP-MUTATION` 确认干净）：
  - α「静默覆盖」（恢复旧行为）：Protocol **4 红**（4 条冲突用例精确命中）；
  - γ 重放 α（验证改写后的 `Frozen_Pin`）：该测试亦红——**共 5 红**，两条防线都被测到；
  - β「不清理过期冲突」：**精确 1 红**（`Conflict_Clears_AfterConflictingObservation_Expires`）。
- 全量验证：Debug `dotnet build` **0 警告 0 错误**；`dotnet test` **609 PASS / 0 FAIL**
  （Protocol 223 + Transport 192 + Core 125 + Security 66 + Integration 3）。

**D. 遗留 / 下一站**

- 下一站 = 阶段 1（transcript + HMAC proof 纯函数核心）；落地时同步
  `docs/PROTOCOL_AND_SECURITY.md` 工作副本与 ADR-038 的差异（**以 ADR-038 为准**）。
- 两机验收重跑仍不急：物料需重打（本轮已含 Discovery 改动），归入 M4 收口批次。

### 18.6 阶段 1 执行记录（2026-09-22）—— 双档 transcript + HMAC proof 纯函数核心

**状态：完成**（提交 `35506b5`，6 files，+1113/−11；步骤 5–8 全落地）。
**同日重定位 `e7687ec`**（ADR-039）：阶段 2 开工盘点发现 TFM 硬约束（Transport net10.0 无法引用
Security net10.0-windows7.0——NU1201 本机实测），认证核心由 Security 搬至 Transport，见下。
§18.5 D 节遗留：「工作副本同步 ADR-038」**已完成**；「两机验收重跑」仍挂账（物料需重打）。

**产出与落点**（路径为重定位后；重定位为纯搬移，协议字节与测试语义零变化）

- `src/LanRemote.Transport/Auth/AuthProtocol.cs` —— 协议词汇表单一事实源
  （原 `Security/Auth`，ADR-039 重定位）：
  两个域串（`LANREMOTE-AUTH-V1` / `LANREMOTE-GRANT-V1`）、`server\0` 前缀、5 个帧 type、
  16 个 JSON 字段名、4 个尺寸（nonce/proof/token/cert 均 32B）、两个时限初值
  （认证 10s / 审批 60s，provisional，待数值实验回写）、权限词（`view`/`control`）
  + `EncodePermission`（未定义枚举值抛异常——防将来扩展枚举时静默误编码）。
- `src/LanRemote.Transport/Auth/AuthTranscriptBuilder.cs` —— 纯函数核心
  （原 `Security/Auth`，ADR-039 重定位）：
  `BuildClientTranscript` / `BuildGrantTranscript` / `ComputeClientProof` / `ComputeServerProof`。
  输入全部强类型（`Guid` / `ReadOnlySpan<byte>` / 枚举）、**不接收 string**（规范形式只由本类
  输出，从类型上消灭「对端编码差异（hex 大小写 / 非规范 base64 / uuid 格式）进 transcript」
  整类问题）；固定长度字段（nonce×2 / cert / key）不符即 `ArgumentException`（fail fast）。
- `scripts/reference/gen-auth-golden-vectors.py`（入库）—— 独立 Python 参考实现（只用标准库
  hmac/hashlib/base64/uuid）；输出 3 个黄金向量（control/control、view/view、
  requested=view+granted=control）+ granted 篡改对照向量 + 可直接粘贴的 C# 常量块
  （NUL 用 `\u0000` 转义——C# 的 `\0` 后跟数字会被解析为八进制转义，是实测踩过的坑）。
- 测试 +34：`AuthProtocolTests` 8 条字面量锁定；
  `AuthTranscriptBuilderTests` 26 条 case（黄金向量逐字节/hex、client 档 8 段 / grant 档 3 段
  NUL 分割、域分隔、字段敏感性、granted 篡改必致 serverProof 验证失败、前缀参与 MAC、参数校验）。
  落点随重定位迁移（Security.Tests 66→100→66、Transport.Tests 192→226）。
- `docs/PROTOCOL_AND_SECURITY.md` 工作副本 §9 同步 —— 头部「本地修订记录」+【本地修订 1】
  （双档 transcript + serverProof 绑 grant 档；原文保留对照）；该文件自此进入「随里程碑修订」模式。

**变异验证（4 次）** —— 恢复后均以 `git diff` + `grep TEMP-MUTATION` 确认干净：

- M1「client 档追加尾随 `\0`」→ **8 红**（3 向量链 + 8 段分割 + granted 篡改对照）；
- M2「cert 改小写 hex」→ **8 红**（同组；uppercase 合同受黄金向量保护）；
- M3「serverProof 去 `server\0` 前缀」→ 首轮 4 红；**由此发现
  `ServerProof_DependsOnServerPrefix` 只断言常量关系、不断言实现输出**（变异下不红）——
  立即强化为「实现输出 ≠ 无前缀 HMAC」，**重放 M3 确认 5 红**；
- M4「移除 serverNonce 长度校验」→ **精确 3 红**（theory 全 case，零误伤）。

**全量验证**：Debug `dotnet build` **0 警告 0 错误**；`dotnet test` **643 PASS / 0 FAIL**
（Protocol 223 + Transport 192 + Core 125 + Security 100 + Integration 3；重定位后分布 =
Transport **226** / Security **66**，总数不变）。

**下一站 = 阶段 2（认证消息帧 JSON 严格解析）**：4 类帧 + canonical base64 校验/解析；
照 `HelloFrame` 严格模式（重复字段 / 未知字段 / 大小写 / 深度全写死 + 逐项测试）。

### 18.7 阶段 2 执行记录（2026-09-22）—— 认证消息帧（JSON 严格解析）

**状态：完成**（提交 `21a8829`，18 files，+3031；§18.2 步骤 9–12 全落地，另加 1 个计划外帧）。

**产出与落点**

- **帧类 ×5**（`src/LanRemote.Transport/Auth/`；API 形态 = `TryParse(ReadOnlySpan<byte> utf8,
  out XFrame? frame, out string? rejection)` + `Serialize()`；纯标志帧 `ApprovalPendingFrame` /
  `AuthenticationFailedFrame` 用 static class + `TryParse(ReadOnlySpan<byte>, out string?)`）：
  - `AuthChallengeFrame`（步骤 9）：`type`/`protocol`/`sessionId`/`serverDeviceId`/
    `serverNonce`/`certSha256`/`expiresInMs`；拒绝码族 `challenge-*`（10 个短码）。
  - `AuthResponseFrame`（步骤 10）：`clientDeviceId`/`clientName`/`clientNonce`/
    `requestedPermission`/`clientProof` + `type`；`clientName` 硬化（非空 / ≤64 / 无控制字符 /
    UTF-16 良构——名字最终出现在被控端本机审批面）。
  - `AuthSuccessFrame`（步骤 11）：`grantedPermission`/`serverProof`/`sessionToken`/
    `videoAttachExpiresInMs` + `type`；`grantedPermission` 必须 `view`/`control`。
  - `ApprovalPendingFrame`（步骤 11）：唯一字段 `type`；序列化恰为
    `{"type":"approval_pending"}`（字节稳定测试锁定）。
  - `AuthenticationFailedFrame`（**计划外补充，已记录在案**）：语义为空、永不携带原因码——
    认证失败唯一对外形式（阶段 3/4 公共前置件，避免届时手写第二套 JSON 解析面）。
- **canonical 编码 ×3**（步骤 12）：`CanonicalBase64` / `CanonicalHex` / `CanonicalGuid`——
  判定式统一 =「宽松 decode → 重新 encode → Ordinal 比较」（round-trip 逐字符相等）。
- **`AuthJson` 增类型提示预读**（可选参数 `expectedType`/`wrongTypeCode` + `TryReadTypeHint`：
  扫根对象第一层 `type` 字符串，嵌套用 `TrySkip` 跳过）——跨帧载荷（结构合法、类型是别的帧）
  报 wrong-type 而非 malformed；主解析仍是唯一权威，提示不影响接受与否。
- **测试 +234**：3 个 canonical 测试文件（往返扫掠 1..96 覆盖 padding 余数 / 大写字面量 /
  `Accepts_Every_ToHexString_Output…` / D 格式收窄）+ 5 个帧测试文件（结构 = 基线字面量 +
  `Build(...)` 变体构造器 + `Patch(from,to)` + `Rejects(json, expected)` 断言器；逐项覆盖：
  重复/未知字段、大小写、注释、尾逗号、尾随数据、非法 UTF-8/BOM、null=missing、跨帧交叉拒绝
  （真实序列化字节互喂全 wrong-type）、构造器 fail-fast、输入缓冲复制、拒绝短码不含输入）。

**实测事实（本阶段新增，全部实锤）**

- `Utf8JsonReader.GetString()` 对非法 UTF-8 抛 `InvalidOperationException`（内层
  `DecoderFallbackException`），**不是** `JsonException`（`JsonSerializer.Deserialize` 才会包成
  JsonException）→ `TryReadTypeHint` 必须两类都捕。
- `System.Text.Json` 解码孤立代理转义 `\uD800` 时**直接抛 `JsonException`** → 归 malformed，
  到不了 bad-name（测试预期按实测修正并留注释；`IsAcceptableClientName` 的代理判定保留，
  保护构造器路径）。
- `Guid.TryParseExact("D")` 除容忍大写外**还容忍前后空白**——round-trip 是唯一收窄者
  （M2 变异意外收获：canonical GUID 的空白用例跟着红）。
- `"AA++"` / `"AA//"` 是**合法** canonical base64（`+`/`/` 属标准字母表）——写测试时先入
  为主误判为非法、被实测纠正（拒绝组换成 `AA--`/`AA__`）。

**设计微决策（记录在案；合同级内容已 ADR-040 化）**

- 帧内判定顺序：type 缺失 → type 不等（wrong-type）→ 其余字段存在性（missing）→ 逐字段判值；
  null 值按 missing 报。
- `expiresInMs` / `videoAttachExpiresInMs` 只做「正值」判定——上界不在帧层发明，
  由消费方绝对 deadline 兜底。
- 构造器 fail-fast：长度 / 非正数 / 未定义枚举（`_ = AuthProtocol.EncodePermission(...)`）
  立即抛；输入数组 `ToArray()` 复制防外部突变。
- `AuthProtocol.TryDecodePermission` 补齐解析方向（`EncodePermission` 的逆；大小写精确）。

**变异验证（4 次）** —— 恢复后均以 `git diff` + `grep TEMP-MUTATION` 确认干净：

- M1「`AllowDuplicateProperties=true`」→ **5 红**精确（3 条 `Rejects_Duplicate_Fields` +
  2 条无载荷帧重复字段）；
- M2「`CanonicalGuid` round-trip 失效」→ **6 红**（3 条 bad-id + 3 条 canonical 收窄用例）；
- M3「challenge `serverNonce` 长度判定摘除」→ **首轮 0 红** → 当场抓到一条**假测试**：
  「31 字节」手抄字面量实为 45 字符（excess padding，根本不是合法 base64），测试从未触到
  长度判定、被别的拒绝路径救活；修复 = 测试加 `B64(int)` helper（BCL `Convert.ToBase64String`
  构造 16/31/33 字节邻界值，不再手抄长串），**重放 M3 → 精确 1 红**
  （`Rejects_Bad_Server_Nonce`）；同款坏字面量在 response / success 两个测试文件同步修掉；
- M4「success `serverProof` 长度判定摘除」→ **精确 1 红**（`Rejects_Bad_Server_Proof`——
  验证修复后的 success 邻界用例同为真测试）。

**全量验证**：Debug `dotnet build` **0 警告 0 错误**；`dotnet test` **877 PASS / 0 FAIL**
（Protocol 223 + Transport 460 + Core 125 + Security 66 + Integration 3；
Transport 226→460 = +234）。

**下一站 = 阶段 3（服务端认证状态机，步骤 13–17）**：challenge 生成（绝对 deadline）/
验证链（重算 + `FixedTimeEquals`，失败一律 generic `authentication_failed`）/
failed auth limiter（按 remote IP）/ local approval + `approval_pending`（v1 = 每个新连接
都批，ADR-038）/ sessionToken + `SessionRegistry`。衔接层（ADR-037 的
`ControlPreAuthHandoff`）随之落地，门禁测试改写与实现同批（写了才有 → 能测）。

### 18.8 阶段 3 执行记录（2026-09-22）—— 服务端认证状态机（步骤 13–17 + 衔接层落地）

**状态：完成**（提交 `760e950`，14 files，+2780/−89；§18.2 步骤 13–17 全落地 + ADR-037 衔接层
同批落地；测试 +25，Transport 460→485；全量 **902 PASS / 0 FAIL**。）

**产出与落点**

- **衔接层（ADR-037 的可执行形式，与状态机同批）**：
  - `ConnectionSecurityContext`（77 行）：本连接安全事实冻结快照——`ConnectionId` / 地址端口 /
    协商 TLS 版本 / **本机证书 DER SHA-256（32 字节）**；`TransportHost` 构造期算一次、
    全生命周期冻结；challenge `certSha256` 的唯一来源（「声称 = 实际出示」）。
  - `AcceptedConnection` 由 5 参记录收编为 `(ConnectionSecurityContext Security, SslStream Stream)`
    + 4 个转发属性（既有消费点零破坏）。
  - `ControlPreAuthHandoff`（59 行）：线性所有权交接对象；`BeginAuthentication` **exactly-once**
    （第二次抛 `InvalidOperationException`，消息含「只允许」）。
  - `ControlPreAuthSession`：成功路径**不再** `ShutdownAsync`（M3 终态被有意识改写，注释保留
    理由与原文）；`AllowedOperationsWhilePreAuthenticated` 空集合 → **恰好一项**
    `begin-authentication`（门禁测试 = 精确集合相等）；`ControlPreAuthResult` 增 `Handoff` 字段。
  - `ControlSessionState` 扩展 `Authenticating` / `Authenticated`（一个枚举贯穿全链）。
- **状态机（步骤 13–17）**：
  - `ControlAuthContext` + `ControlAuthOptions`（85 行）：跨连接共享守卫（密钥存储 / 限流 /
    待批配额 / 审批面 / 登记表 / 时钟）+ **值旋钮**（全部时限可整体缩放 = 测试能测超时路径的前提）。
  - `ControlAuthSession`（664 行）：`RunAsync` 八段主干 = ①限流前置（被罚 IP 连 challenge
    都不发）→ ②机器窗口（10 s **绝对** deadline，覆盖 challenge 写 + response 读 + 校验判定）→
    ③帧级严格解析 → ④密钥加载（**解析通过后才触碰**）→ ⑤重算 + `FixedTimeEquals`（唯一计限流
    失败 + 300–800 ms 随机延时）→ ⑥审批（quota → `approval_pending` → 三路竞速）→
    ⑦serverProof + token + `auth_success` → ⑧登记 + 保持。challenge 构造期定稿
    （sessionId / nonce 此后不变）。
  - `FailedAuthLimiter`（166 行）：10 分钟滑窗 / 5 失败 → 60 s 封禁；封禁到点**放行**、
    再失败即再封（净效果：持续攻击被压到「每 60 s 一次尝试」直到旧记录滑出 10 分钟窗）；
    成功清全部（含封禁）；条目懒清理；`TimeProvider` 可注入；零日志。
  - `LocalApprovalGate`（119 行）：`ILocalApprovalGate` + `LocalApprovalOutcome` **5 值显式终态**
    （无 bool、无 handler ≠ 同意、UI 不可用必须 Unavailable）；不可变请求快照（自称字段标注）+
    短关联码 = `SHA256("LANREMOTE-APPROVAL-CODE-V1"\0‖sessionId‖clientNonce)` 前 3 字节大写 hex。
  - `SessionRegistry`（185 行）：`Register` 为 **internal 唯一入口**（认证状态机在
    `Authenticated` 调用）；token 防御性拷贝；`SessionRegistration.Dispose` → 注销 + token 清零；
    公开面只有计数 / Snapshot（无 token、无注册入口——反射测试钉死）。
- **修改 ×6**：`AcceptedConnection`（+37/−）、`AuthProtocol`（+31：8 常量）、
  `ControlPreAuthSession`（+76/−：交接改写）、`TransportHost`（+16：指纹冻结 + 上下文构造）、
  `ControlPreAuthSessionTests`（+108/−：适配 2 参构造 + 门禁改写）、`HostRole`（±5：
  `localShutdownSent` UNOBSERVED 行更新——M4 起成功路径不再关闭连接）。
- **测试 +25**（`ControlAuthSessionTests.cs`，1241 行；真实回环 TLS + 真实 `TransportHost` 全链；
  21 方法 = 20 Fact + 1 Theory×5）：
  - 交接 exactly-once ×2：`BeginAuthentication_Is_Exactly_Once_Per_Handoff`（detached 流）+
    `Auth_Session_Cannot_Be_Run_Twice`（第二次必抛「只允许跑一次」）。
  - 成功全链 ×1：帧序列**恰为** `[auth_challenge, approval_pending, auth_success]`；客户端
    重算 proof / 验证 serverProof（更正：该旧 probe 复用了产品构造器，不是独立密码学 oracle；阶段4另补独立 UTF-8/HMAC 对端）；登记摘要逐字段；token 与
    下发逐字节相同；保持探针；断开后注销清零。
  - 失败面 ×6：错钥（计数 = 1）/ 篡改 permission / 篡改指纹（签名页 ≠ 报文页构造）/
    帧违规（零长度前缀 → `auth-frame:length-zero`，不计）/ 断连 EOF（不计）/ 密钥存储失败
    fail closed。
  - 时限/限流 ×2：机器窗口 700 ms 截断拖流（可证伪耗时断言）/ 被限流源**收不到 challenge**。
  - 审批面 ×8 方法：终态 Theory ×5（denied / cancelled / unavailable / timedout /
    抛异常→Unavailable，全 fail closed）/ 错请求 ID / 越权授予拒 / 降级 view-only 允许 /
    窗口截断 + 迟到决定作废 / 断连作废迟到决定 / 配额截断第二个待批（双连接）。
  - 数值/DoD ×3：失败延时边界（200–400 ms 上下界）/ 反射（公开面无注册无 token）/
    帧键集白名单（`AssertJsonKeySet` 逐帧精确相等）。

**实测事实（本阶段新增，全部实锤）**

- **测试夹具竞速（本阶段最贵的一课）**：harness 在服务端出结局后立即拆线（cancel + dispose），
  与客户端脚本「读最后一帧」构成竞速——快速失败路径（错钥 / 被限流 / 即时拒绝）下偶发
  「收不到 generic 失败帧 / approval_pending」；修复 = 拆线前先
  `await Task.WhenAny(clientTask, Task.Delay(TimeSpan.FromSeconds(3)))` 给客户端自然收场机会
  （服务端收线后正文先于 FIN 到达，读类脚本毫秒级自结束；静止类脚本由后续 cancel 兜底）。
  **教训：测试夹具的「清理」也是被测系统的一部分**——拆线快 ≠ 对。
- 「迟到决定」测试的延时**不能绑 stall 令牌**（harness 取消它会把这笔「迟到」吞掉，测不到
  目标路径）——改用不绑令牌的 `Task.Delay(150)`。
- `Assert.False(probe.Frames.Contains(...))` 触发 xUnit2017 → 改 `Assert.DoesNotContain`。
- 机器窗口「压线后置校验」（读返回后立即查 `IsCancellationRequested`）在代码里只有两行——
  没有它，「窗口 + 解析耗时」会漂移成实际 deadline。

**设计微决策（记录在案；合同级内容已 ADR-041 化）**

- 失败帧是 best-effort（对端可能早走了，发不出去不改变「已拒绝」）；但**停机取消照常上抛**。
- `ControlAuthResult` 成败都带 `SessionId`（日志关联）；15 个 `auth-*` 前缀失败短码只进本地。
- `RequireLocalApproval=false` 时跳过审批阶段（发 success 前无 `approval_pending`）——测试用。

**变异验证（4 组；恢复后均以 `git diff` + `grep TEMP-MUTATION` 确认干净）**

- M1「proof 校验恒成功」（`false && !FixedTimeEquals(...)`）→ **4 红精确**：错钥 / 篡改权限 /
  篡改指纹 / 失败延时边界（2026-09-22 提交后原样重放核实）；
- M2「删 `RecordFailure`」→ **3 红**：错钥 + 两条篡改的计数断言；
- M3「`IsGrantable` 恒真」→ **1 红**：`Over_Grant_In_Decision_Is_Rejected`；
- M4「双重拆除断连防线」（`winner == clientActivity` 分支与 `clientActivity.IsCompleted`
  后置校验同时失效）→ **1 红**：`Approval_Disconnect_Discards_A_Later_Decision`——
  顺带证明纵深防御形态：两道防线各自在场时都测不出，**全拆才红**。

**全量验证**：Debug `dotnet build` **0 警告 0 错误**；`dotnet test` **902 PASS / 0 FAIL**
（Protocol 223 + Transport 485 + Core 125 + Security 66 + Integration 3；Transport 460→485 = +25）。

**下一站 = 阶段 4（客户端侧，步骤 18–20）**：客户端认证流程（hello → challenge → clientNonce →
transcript **绑 presentedPin** → response → success → **验证 serverProof**）；失败处理
（serverProof 验证失败 → 立即断开 + UI 文案「远端身份验证失败，可能是错误密码或伪造设备广播」+
不发送输入）；客户端 e2e（真 TLS 双端：correct key success / wrong key generic
`authentication_failed` / serverProof 篡改必拒）。落点估计：`TlsClientConnector` 侧接线 +
`AuthTranscriptBuilder` 客户端语义复用（**独立重算**，勿共享服务端算式）。

### 18.9 阶段 4 开工复核：时限合同反例与外部评审停点（2026-09-22，历史）

> 本节是修复前历史快照；评审已回收、用户已拍板、服务端修复和客户端实现已完成，当前进度以 §18.10 为准。下述「未修复/待转发」不再是当前要求。

**当时结论：阶段 3 历史测试保持绿色，但两条缺失覆盖的时限合同已被本机真实 TLS 反例证伪；产品修复未实施，阶段 4客户端尚未编码。** 按用户常驻指令的外部模型通道暂停，待用户转发材料并贴回评审。此处不是阶段完成记账，不回填阶段4为完成。

**代码事实**（基线 `760e950`）：

- `ControlAuthSession.cs:178–224` 的 machine CTS 只包住challenge写、response读和读后取消检查；严格解析、`LoadOrCreateAsync(cancellationToken)`及HMAC比较在外，无完成时限后置判断。ADR-041 §3/此前“覆盖校验全程”的解释不成立。
- `ControlAuthSession.cs:397–398` 先调用gate后创建审批Delay；同步前缀耗时未计入实际Delay。决定分支复核断连和权限，但接受批准前没有截止时间检查。
- “过期不接受”与“到点强制停止同步DPAPI/gate执行”是两个不同合同，不能混用；完整修复待评审，不通过改大provisional数值掩盖。

**实测方法与结果**：临时给 `ControlAuthSessionTests.cs` 加两条诊断用例，走真实回环TLS、真实TransportHost及完整pre-auth→auth状态机；仅store/gate注入耗时。没有改网络或访问实际用户秘密文件。

| 诊断 | 窗口 | 首轮 | 重放 | 两轮结果 |
| --- | --- | --- | --- | --- |
| `Diagnostic_Machine_Window_Must_Include_Key_Load_And_Proof` | 700ms | keyLoad 2111ms | 2113ms | Completed=True；serverProofValid=True；连接保持时registry=1；进入审批 |
| `Diagnostic_Approval_Window_Must_Include_Synchronous_Gate_Call` | 600ms | gate同步1803ms | 1807ms | Completed=True；serverProofValid=True；连接保持时registry=1 |

两轮各 **2 FAIL**，均精确失败于“应拒绝，但Completed=True”；前置条件和客户端无异常断言已通过。帧序列均为challenge→pending→success。诊断验证的是时限，不是独立密码学oracle；没有断言生产DPAPI自然发生这些延迟，也没有运行真实WPF审批面。诊断patch第一条要求慢store完成，仅用于证明当前反例；永久回归需兼容修复后的提前合作取消。

**恢复与验证**：

- 反例已保存 `outputs/m4-deadline-review/deadline-counterexamples.patch`，临时测试从源码删除恢复；`git diff --exit-code -- src tests tools`通过；`git apply --check`确认补丁可重放。
- 恢复后重新Debug build：**0警告/0错误**；全量原有 **902 PASS/0 FAIL/0 SKIP**，分项223+485+125+66+3。未跑Release、两机或数值实验。
- **Last code commit：`760e950`（未改变）。Working tree at validation：src/tests/tools与该代码基线一致；诊断patch及证据独立保留，验证后仅更新文档/记忆/评审材料。** 原有902全绿只表示恢复成功，不能作为两条缺陷已修复的证据。
- 本轮未提交/推送；评审材料和当前补记保留在工作区，待后续收口。不要把未提交文档误认为丢失的代码实现。

**交付/下一步（当时记录）**：用户转发 `outputs/m4-deadline-review/M4_AUTH_DEADLINE_REVIEW_PROMPT.md`，可附同目录 `M4_AUTH_DEADLINE_EVIDENCE.zip`（相关源码、原始日志、patch、哈希清单）。回收后逐条核对前提；先修服务端截止/取消/迟到结果所有权并补永久回归，再实现客户端presentedPin绑定、serverProof门禁和真实TLS e2e，最后阶段5验收器。仍遵守不改产品UI、双击即GUI、不静默改网。

### 18.10 服务端修复与阶段 4 Transport 收口（2026-09-22）

**状态**：代码提交 `bc02a0c`；步骤 18/20 完成，步骤 19 的库层失败处理完成、验收器展示待阶段 5。不是完整 M4 或两机 DoD 通过。外部评审已回收，用户明确选择「按状态机接受时刻」；现行决定 ADR-042/043，取代 ADR-041 的旧时限解释。

**服务端修复**

- `AuthenticationDeadline` 使用单调时间，`elapsed >= budget` 拒绝；timer/token 负责合作式唤醒，timer 未派发不延长接受窗口；统一隔离 timer、父取消和显式取消中的回调异常。
- `AuthenticationSecretLoader` 按认证 context 共用，最多一项未终结的实际 store 工作；取消等待后 late owner 先清零迟到 key 再归还准入，观察 fault/cancel。不每连接另起 Task.Run，不宣称硬中断同步 DPAPI。
- machine 自 challenge 写前覆盖至 MAC 后最终检查；最后检查之前不操作 limiter、不进入审批。及时错误 proof 才计失败，抖动改用 RandomNumberGenerator，默认数值不变。
- gate 调用前起独立审批窗口，同步前缀/Dispatcher/展示/人类等待都计入；`caller取消 > 截止 > 已观察活动 > 决定`，决定校验后再次复核；保持单读者并观察交接读 fault。
- store 自发取消归 key-unavailable；gate fault 与停机竞速保留 caller 取消；pending/success 本地写超时有明确拒绝码。本地 success 写出成功不是对端交付确认，写预算独立于已结束的审批窗口。

**客户端及永久回归**

- `ControlClientConnector.ConnectAndAuthenticateAsync(...)` 内部独占 TLS→hello→认证全链；challenge 的 device/pin 与冻结目标/实际出示证书对齐；client transcript 绑定实际 presentedPin；grant 与 serverProof 独立门禁，允许 Control→ViewOnly，不允许越权。
- 只有验证完成才返回 `AuthenticatedControlSession`；公开面只有身份、权限、sessionId、shortCode 与 Dispose，无裸流/token/输入接口。serverProof 失败固定文案为「远端身份验证失败，可能是错误密码或伪造设备广播」。失败/取消关闭连接。
- key 在首次 await 前复制，finally 清私有副本；会话拥有独立 token 并在释放时清零。`AuthSuccessFrame` 严格 canonical token 使用栈临时缓冲，`FrameReader` 失败时清未交出的 payload 数组。
- 客户端 machine 从 hello 本地写完起算；challenge 提示只收窄；一次合法 pending 接受后开始独立 approval；重复 pending 拒绝，prefix/payload 均受外层余量约束。解析、MAC 和会话构造后均做单调后置检查。
- 服务端新增 68 例（970 基线），客户端/帧与读失败清理再增 66 例，合计相对 902 增 134；Transport 485→619。测试混合真实回环 TLS、独立 HMAC 对端、可控 clock、纯判定器、替身 I/O；不混称为两机或真实 Dispatcher 验收。

**真实变异验证**

- 服务端最终五项：MAC 最后检查 2红/2绿；`>=` 边界 1红；审批末次检查 1红/1绿；迟到 key 清零 1红；准入保留 1红。每项构建成功，三个源文件恢复哈希一致，随后重新构建测试。
- 客户端首轮 01–09、重放 11–19，各九项：serverProof（4红）、实际 grant 绑定（2红）、overgrant（2红）、challenge device（1红）、实际 pin（1红）、MAC 后 deadline（2红）、重复 pending（1红）、token canonical（7红）、失败 payload 清零（4红）。每轮总计 24红/16绿，逐项恢复 40/40 绿；是执行次数，不是去重测试数。
- 首轮发现并修正测试 clock 观察点：到期事件放在权限/MAC 前采样之后，本次返回旧采样；删被测 MAC 后检查也不能删掉到期事件。正确 proof 的两例仍由会话构造后 deadline 兜底挡住，属于纵深防御，不冒称单点独立覆盖。测试依赖具名采样阶段，未来插入取时点须复核。
- 客户端重放前后整个 src/tests 的 181 个文件集及 SHA-256 一致；临时 device 变异触发过 1 条空性警告，恢复及最终构建均无警告。没有把编译错误当命中。

**恢复后的最终全量验证（本机实跑）**

| 项目 | Debug PASS | Release PASS |
| --- | ---: | ---: |
| Core | 125 | 125 |
| Protocol | 223 | 223 |
| Security | 66 | 66 |
| Integration | 3 | 3 |
| Transport | 619 | 619 |
| 合计 | **1036** | **1036** |

两种配置 build 均 **0警告/0错误**；test 均 **0失败/0跳过**。日志为 `outputs/m4-deadline-review/final-{debug,release}-{build,tests}.log`。`git diff --check` 通过；src/tests 无临时变异标记；主助手重新计算三产品文件 SHA 与重放清单一致。

**Last code commit：`bc02a0c`。Working tree at validation：上述验证对应此提交的完整代码/测试树；验证后仅文档/本地证据整理，未改产品或测试。** 所有日志/原始诊断与压缩包保留本机 outputs，不当源码入库；新交付 `M4_STAGE4_VALIDATION.txt` 与 `M4_STAGE4_VALIDATION_EVIDENCE.zip`，区别于旧诊断 ZIP。

**明确未证明或未完成**

1. 普通 CTS 不保证中断永不返回的同步 store/gate/取消回调；异常隔离不是阻塞隔离。有界 loader 只限该 context 的 store，不扩大到任意本机工作。
2. DTO 的不可变 Base64 string、JSON/FrameWriter/TLS 内部副本不在显式清零保证内；不能声称全进程无秘密残留。
3. 客户端只消费首个终帧，未来重复 success 属后续协议消费者；批准前短码目前无公开过程通知，不能假装现有成功返回值支持它。
4. 验收器 tools、本体 App 和权威规格未改；未跑真实 WPF Dispatcher、GUI/两机验收或数值定案实验；网络、防火墙、真实用户密钥未作操作。

**阶段 4 结束时的阶段 5 续作点（历史，现状见 §18.11）**

- 已定可直接做：真实 `DpapiAccessSecretStore` 复用现有 vault；Host 级共享认证 context；消费 handoff 并 await auth；已有最小审批字段/按钮、异步 Dispatcher 和请求 ID 绑定；client success 改走高层入口。旧 pin-mismatch/timeout/slow-dribble 保留原低层语义。
- 成功证据改为客户端已验证会话 + Host 结果的 sessionId 配对，不能继续以 hello 后 EOF/PreAuthenticated 判成功；不公开 token/流，不把认证异常误分为 TLS 握手失败。
- 待用户确认：仅验收器「本机 key 显式查看 + 对端 key 遮挡输入、不落盘」是否采用；本轮是否要求批准前双端显示/比较短码（需要最小非秘密过程通知 API）。不重问已定的最小审批面及接受时刻。
- headless 无审批面不得自动批准；缺目标真实 deviceId/key 不得降级伪成功。现有“停止监听=中止作废”保持，不静默变成正常通过按钮。

**2026-09-23 续记：阶段 5 实施前外部评审材料**

用户要求直接编写可转发 prompt。材料位于 `outputs/m4-stage5-review/`：`M4_STAGE5_ACCEPTANCE_REVIEW_PROMPT.md` 聚焦批准前短码通知、真实 WPF 审批生命周期、密钥显示/输入、会话保持与两端证据；配套 `M4_STAGE5_ACCEPTANCE_REVIEW_EVIDENCE.zip` 收录当前 src/tests、完整验收器源码、合同及上一轮原始验证证据，并附逐文件 SHA-256 清单。材料不包含真实密钥、PFX、DPAPI 文件或产品数据；不自动外发。

代码基线仍为 `bc02a0c`，本轮未修改 src/tests/tools、未重跑构建测试、未操作 GUI/网络/真实密钥。两项交互仍是待审推荐，不因编写 prompt 而视为用户批准；下一步由用户转发材料并贴回评审，再核对源码事实与最小方案，不重复转发旧时限评审、不重问已定审批接受时刻。阶段 5 接线和真实 Dispatcher/GUI/两机验收仍未开始。

**2026-09-23 授权更新（覆盖上一续记的等待点）**：用户明确“国外模型暂时用不了了，你现在全权继续下去吧”。不再等待外部评审，按最小方案推进：验收器本机密钥显式查看、对端遮挡输入且不持久化；批准前单次非秘密短码通知；有界审批收件箱，取消不依赖 Dispatcher。不会因此变更线上帧、安全边界或静默改网。先实现并验证通知/收件箱，再接双端角色及界面；GUI 人工检查、两机验收仍待后续实测。

### 18.11 阶段 5 认证验收器接线与自动化收口（2026-09-23）

**结论**：实现与自动化完成，**M4 人工 GUI/两机验收未运行，整体仍进行中**。不再等待外部模型。产品 `LanRemote.App` 尚不能看屏或键鼠控制；本轮没有运行网络准备/撤销、没有读取真实访问密钥、没有修改原始规格。

#### A. 提交与验证树

- **Last code commit：`7a9d199`**（测试断言等价改写，消除 8 条 xUnit2031）；阶段 5 主实现 `6c7a15b`，28 files / +5317 / −999。
- **Working tree at validation**：最终双配置构建/测试对应 `7a9d199` 的产品及测试树。此前 `6c7a15b` 的 1190 tests 已通过，但原始 build `08/10` 存在 8 条测试分析器警告；不得将其写为零警告。修正不改变测试数据、断言条件或产品行为，重新执行完整 Debug/Release，而非只看增量 build。
- 原始证据在 `outputs/m4-stage5-validation/`；该目录为本地交付/证据目录，不随普通源码提交。提交后文档独立记账，不把 HEAD hash 写入本文件。

#### B. 实现边界

1. **批准前通知**：`ControlClientApprovalPending` 仅公开 SessionId、ShortCode、AcceptedAtTimestamp、ApprovalWindow；首个合法 pending 后通知一次。同步回调前后及异常路径复核原始单调窗口，耗时不补回；异常脱敏、取消/超时优先。不提前公开已认证会话、token、stream 或输入接口。
2. **审批**：`LocalApprovalInbox` 每 run 容量 3，RequestId + 独立 Generation 防迟到点击；快照拉取，不堆 Dispatcher。Stop/Dispose 永久关闭，取消不依赖 UI；收件箱接受决定不等于 Transport 接受授权。
3. **密钥与关窗**：PasswordBox 输入不持久化；只在活跃 Host 显式确认查看本机 key，原始单调窗口 15 秒、失焦/隐藏/停止清显示。context 单一 WorkLease 追踪附属读取，停机阻止新工作并等待旧工作清理、清零、故障记账后才释放 context/结算。普通 CTS 不保证强杀永不返回的同步工作，不保证擦除不可变 string/WPF 内部副本。
4. **双端认证**：Host 共享真实 store、限流、独立 3/1 待批配额与 registry，消费 handoff 并 await 认证/保持。Client success 在低层 TLS 前分流到高层连接器，绑定实际身份、校验 serverProof/权限后持有至少 5 秒，再 Dispose。无 key/占位身份/无审批 UI 不自动降级成功；headless 无秘密 argv。
5. **证据**：Host 单个 100ms sampler，只追踪当前活动会话。成功要求正向样本跨度 >=4s、最大间隔及结束到最后正向样本 <=500ms、已注销且非 Host 强关；缺样本、无成功、强关为 UNMET。成功按完整 SessionId 配对；旧负例按四元组。采样不证明间隙内每个瞬间在线，保持结束也不区分 EOF/RST/越界字节，需客户端主动 Dispose 证据。
6. **结算/GUI**：幂等 Complete 在资源清理后写唯一 footer；append 失败和 footer 自身失败都不可冒充 PASS。优先级 `INVALID_RUN > HARNESS_ERROR > FAIL > UNMET > PASS`。单 DispatcherTimer 分批拉日志，显示限长但复制完整日志；Host GUI 180 秒自然结算，提前停止/关窗仍作废，等待超过 8 秒只提示不强杀。

#### C. 实测与变异

最终构建日志 `14-clean-build-debug.log` / `16-clean-build-release.log`：均 **0 警告 / 0 错误**。
最终测试日志 `15-clean-test-debug.log` / `17-clean-test-release.log`：

| 测试项目 | Debug PASS | Release PASS |
| --- | ---: | ---: |
| Core | 125 | 125 |
| Protocol | 223 | 223 |
| Integration | 3 | 3 |
| Security | 66 | 66 |
| Transport | 633 | 633 |
| Acceptance | 140 | 140 |
| 合计 | **1190** | **1190** |

两配置均 **0 FAIL / 0 SKIP**。相对阶段 4 的 1036 新增 154（Transport +14、Acceptance +140）。真实 TLS 接线四例调用实际 Host handler 与高层客户端，覆盖降权/自然释放、Host 强关、错 key、拒绝；STA Dispatcher 卡住时后台取消、WorkLease/RunState、真实独占文件锁制造日志失败均有测试。**没有启动人工视觉窗口，不把 STA/回环实测当作两机/DPI验收。**

变异目录 `mutations/`：8 项均成功构建后运行期变红，恢复同一目标测试全绿，survived=0，编译失败冒充 kill=0。

| 编号 | 目标 | 红测 PASS/FAIL | 恢复 PASS/FAIL |
| --- | --- | ---: | ---: |
| M01 | 重复 pending 通知 | 0/1 | 1/0 |
| M02 | 通知异常原文外泄 | 4/1 | 5/0 |
| M03 | RequestId+Generation 保护（复合） | 0/3 | 3/0 |
| M04 | ViewOnly→Control 越权 | 9/1 | 10/0 |
| M05 | 提交/交接取消复核（复合） | 0/2 | 2/0 |
| M06 | 样本不足假 PASS | 0/2 | 2/0 |
| M07 | 忽略 Host 强关 | 0/2 | 2/0 |
| M08 | 日志失败位失效 | 0/2 | 2/0 |

只读复核逐项 build/test/exit/hash 日志：7 项直接显示目标断言失败；M02 原异常外泄由夹具清理重新抛出，遮蔽原始断言诊断，**只能记运行期 kill，不宣称其日志直接展示了断言失败**。M03/M05 不是对每处检查的独立单点证明。目录未保存每项 patch/测试源码哈希，不能仅凭源 SHA 重建全部变异过程。四个目标源文件当前 SHA 与 baseline/final/各轮 green 一致；全部产品变异已恢复。8 为变异数，不是14个失败用例数；最后客户端定向67、Acceptance140均恢复通过。

#### D. 候选包与人工验收待办

最终发布日志 `outputs/m4-stage5-validation/18-package-final.log`。候选包：

- `outputs/m4-stage5-validation/LanRemote-0.1.0-m2-m4-acceptance-win-x64.zip`
- **60,265,589 bytes（约57.5 MiB）**，265个唯一扁平成员；CRC、全体成员与发布目录逐字节一致、说明文件一致均通过。
- **SHA-256：`4972edee30e7d213c9805c179c0e69f0f3a532fe261073e2207ab67932308f19`**。
- EXE 为 AMD64、PE Subsystem=2（GUI），程序集包含提交 `7a9d199`；这只是静态入口/版本验证，**不是人工启动和窗口渲染通过**。
- 所有包内 ps1 有 UTF-8 BOM；未含 PFX/P12/key/secrets.bin 文件名。说明为包内 `START-HERE.txt`，外置 `M4-验收说明.txt`；无须安装 .NET，但必须整包解压。
- 此包替代前两次打包，不能沿用第一次 `e41de8b5…` 的 SHA。打包之后产品/测试/脚本与提交无差异。

使用本轮自包含候选包；整包解压，双击 `LanRemote.Acceptance.exe`。两台同子网 RFC1918 Windows 设备；人工检查布局/DPI、显式查看/失焦清空、pending 短码、批准/拒绝/降权、错误提示与关窗。先正常 success+三个低层专项，等待 Host 180秒自然结束，提交双端完整日志，按 SessionId/四元组配对。拒绝、错 key、主动停止等负例单独运行，不与正常通过轮混淆。不要发真实 key/私钥或含 key 的截图。

**历史包已停用（2026-09-24）**：上述§18.11候选包及当时的准备A/B流程均不可继续使用。确认/UAC不构成改网豁免，现行包与不改网流程以§18.13及ADR-045为准。不重用历史M3 EOF成功判据；人工通过之前不勾选M4完成。真实serverProof错误显示若未制造对应场景，仍标为未验，不能用一般拒绝提示替代。

#### E. 剩余范围与条件化估算

当前剩余 M4 人工收尾，以及 M5 视频闭环、M6 多屏/画质、M7 键鼠和安全闸门、M8 多会话、M9 可靠性/UX/诊断、M10 发布集成；M11 后置优化。测试数不代表产品完成百分比。

从当前状态估算：主屏只看最小版约1–3周，基本控制版约3–6周，既定范围自用稳定版约6–12周或更久。假设范围稳定、持续投入开发、真机及时配合；这是工程工作量估算，不是承诺日历交付日期，也不代表对话结束后会持续后台开发。首条真实视频闭环后重估。当前需要用户协助的是两机 GUI/认证验收，而非外部模型转发。

### 18.12 热点真机验收受阻与不改网硬约束（2026-09-24）

本节保留事故发生时的诊断快照，不是新代码验证报告；后续已完成修复及本机验证见§18.13，下文“尚未”仅指当时。

**用户重申原始需求（不是新增功能）**：同一局域网中安装并运行本软件的机器应自动发现；软件适应并使用既有网络，不更改系统网络协议配置，不把DHCP改成静态、不要求设置实验IP。A/B只是设备/测试角色，不对应固定地址。发现失败应修复软件识别与通信，或如实诊断隔离/拦截原因，不得以改网作为普通使用前提。当前场景为A开热点、B连A热点；双方原有上网必须保留，任何一端因软件/验收断网（含短暂）都不接受。确认框/UAC、“只改实验环境”或“之后可撤销”不构成豁免。不能通过改 DHCP、IP、默认网关、DNS、路由、热点/ICS或网络类别凑验收。后续网络恢复必须先核对当前状态与原状态，解释影响并获单独许可，不能默认执行 lab-undo。

**用户提供日志（不是助手现场复测）**：
- 两端均有 `[GUI] M4 窗口渲染完成`，1000×940；只证明渲染日志存在，不证明全部布局/DPI/密钥/审批功能通过。两端 harness 与 Transport SHA分别一致。
- A：原上游以太网 if6 为 `172.100.166.220/24`、DHCP；日志还列热点下游 `本地连接* 2 = 192.168.137.1/24`。原 info 无合格网卡。10:20准备A选择上游以太网，改静态、附加 `192.168.1.10/24`；用户报告随后B无法上网，具体ICS/DNS/转发失效环节未定位。
- B：原WLAN if23为 `192.168.137.141/24`、网关 `192.168.137.1`，info已合格。准备B仍转静态、附加 `192.168.1.20/24`，把Public改Private。随后同接口两个binding，在 `LanDiscoveryService.CreateReceiver` 的 `AddMembership`（line667）反复抛10022，Host及Client四场景均HARNESS_ERROR，尚未进入认证。
- A正常Host轮 `081e04c0` 为UNMET，`sessionHandled=0`；另一轮主动停止INVALID_RUN。lab/info的PASS只反映本地地址核验，不证明热点可用、双方互通、持续上网或认证通过。相同192.168.1/24被写到A上游和B下游，不会自动把两条链路合成同一广播域。

**只读源码结论与待证事项**：`VirtualAdapterFilter` 的 `virtual` 匹配会排除描述带Wi-Fi Direct Virtual Adapter的接口；但日志没给A热点完整属性，尚不能确认实际命中分支。`CreateReceiver` 确实对每个IP binding在同一socket加入同一组播组，未按接口身份去重；B重复加组是高可信10022成因假设，缺逐次加入记录/受控复现，不能写成已定案。

**修复方向（尚未实施）**：撤下普通验收的lab改网引导；只读识别/核实热点下游接口并做窄范围支持，不放开所有虚拟/VPN接口；receiver按接口身份去重，保留各IP真实掩码与发送/监听binding；补诊断和回归。继续严格RFC1918、同子网、TLS/pinning/HMAC。应使用现有热点137网段，不硬编码热点IP，不通过改系统网络回避代码缺陷。

**诊断轮执行边界（历史）**：当时仅分析源码与用户日志，不改网、不默认Undo。后续实现及实测如下。

### 18.13 现网热点修复、验证与候选包（2026-09-24）

**结论**：不改网修复主提交 `bc8cf48`，拒绝提示UTF-8修正 `72bfb93`。A侧已在既有热点上实跑发现/监听并自然结束，未改系统网络；M4仍缺修复版GUI/两机认证及双方联网持续正常的人工证据，不能进入M5。

**实现**（ADR-045；旧ADR的改IP/网络配置默认计划已覆盖，不得按历史恢复）：
- WFD窄例外：Wireless80211、正接口索引、系统Description精确匹配（可带无前导零ASCII正整数实例号）；仅豁免virtual，其余VPN/Hyper-V等词继续拒绝。不硬编码137网段、不把名称当身份、不声称识别ICS角色。
- `MulticastMembership.JoinUniqueInterfaces`：join前验证index/Id完整一致，每接口一次；全部地址binding、真实掩码和逐地址sender保留。receiver失败按接口/地址/组/SocketError/NativeError记账，释放socket并回滚应用资源，不改网络补救。
- 删除GUI准备/撤销按钮及执行链；解析器、Runner、旧role/elevated壳均拒绝，普通info/host/client保持。旧PS参数后无条件throw，保留历史正文但不可执行；旧M2打包入口拒绝。
- 新包每次发布到独立空目录，禁止SKIP，不删除旧产物；递归过滤ps1/cmd/bat及旧START-HERE，只附带一份明确选择的现网说明。打开日志目录功能保留，无无关改动。
- 包级实跑发现旧命令解析拒绝在RunAsync之前，stdout仍系统代码页；补WriteUsage重定向UTF-8后重新双配置验证、提交及出包，原失败日志保留。

**自动化实测**：Debug/Release各 **1305 PASS / 0 FAIL / 0 SKIP**；Core125、Protocol299、Integration3、Security66、Transport633、Acceptance179；比1190增加115（筛选64、membership12、拒绝/只读39）。构建均0警告/0错误。最终日志 `outputs/m4-network-fix/14..17`；Python打包/旧脚本静态回归 **10 PASS**（08）。没有实际运行旧PS或UAC。

**三项真实变异**：禁WFD例外7红→7绿；WFD绕过其它token 11红→11绿；取消membership去重2红→2绿，六次独立构建均成功，红均运行期失败。baseline/mutant/测试源码哈希/逐例TRX均保存，最终字节恢复一致。M03原证据脚本错误期待SetSocketOption，实际堆栈SetMulticastOption；保留原success=false并加独立复核，不篡改原始证据。M02红证据来自名称冲突；M03为双地址回调计数+同回环binding真实socket重复加组的复合证明，不能当作B真实双地址10022完整复现。证据 `outputs/m4-network-fix/mutations/`。

**A侧现网实测**：
- 系统只读核实：if13 `Microsoft Wi-Fi Direct Virtual Adapter #2`，Wireless80211/Up，`192.168.137.1/24`；if6上游 `172.100.166.220/24` DHCP，未出现实验1.10；WSL仍被筛掉。
- Release DLL run `1d0fd025`（09）：`qualifiedNic=192.168.137.1`，组播加入if13成功，TCP45873绑定137.1，3秒自然结束，无handler/cleanup故障。此轮[BUILD] harness是启动宿主dotnet.exe，不能拿它当验收程序集哈希。
- 最终包内EXE再次运行同一现网检查（19），组播和TCP绑定成功、自然结束；旧prepare-lab+elevated参数真实执行被拒绝，退3且中文UTF-8可解（20）。无对端认证，所以Host正确为 **UNMET/退2**，不是PASS。
- 两次只读快照（network-before/after）对所列Up接口、Preferred IPv4/掩码、DHCP、默认路由和DNS逐结构相等；快照在首批现网实跑前后采集，不是持续监控，也不是最终包重跑后再采样。未检查B、未证明互联网连续可用或恢复，不能外推。

**最终交付**：`outputs/m4-network-fix/LanRemote-0.1.0-m2-m4-acceptance-win-x64.zip`，264唯一扁平成员，60,242,991 bytes（约57.5MiB）；SHA256 `9d947a68cdd127dd73ca46f9cdbf6e7e9eec08a52c4493ed84bc33ee4caa434e`。CRC通过、逐文件与此次publish一致，唯一START-HERE.txt与`M4-现网验收说明.txt`相同，无脚本；EXE AMD64 GUI subsystem2，托管程序集包含完整72bfb93提交标记。发布日志18，`package-verification.json`保存检验结果。静态PE/无窗口实跑不冒称新GUI布局已人工通过。

**下一关**：旧包停止使用，双方原有联网正常后换本包，各自解压新目录并双击。只读自检→Host180秒→查看/安全交付密钥→Client四场景及审批→Host自然结算→两端完整SessionId/负例四元组配对，同时记录双方使用前/中/后联网无影响。若旧配置仍造成断网，暂停并单独核实恢复，不使用旧Undo。当前软件仍未实现看屏和键鼠控制。
