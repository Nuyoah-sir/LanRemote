# LanRemote HANDOFF

> 模板来源：`LanRemote_Implementation_Package/09_HANDOFF_TEMPLATE.md`
> 更新时间：**2026-09-20 17:25 (+08:00)**
>
> 本轮（15:26 之后）追加的内容**只动验收物料，不动产品代码**：
> `set-lab-ip.ps1` 重写为 v2（v1 有缺陷会弄丢 IPv4，已归档）、验收手册 §1.2 重写、
> HANDOFF 新增「验收核心已实证」与「v1 事故 / v2 重写」两节。
> **`src/` 与 `tests/` 零改动**，因此 §12 的 408 tests 结论继续有效、无需重跑。

---

## 1. 当前状态

- **当前里程碑：M2.1 — Discovery Final Fix**（M2 之后的收口修复轮，不是新里程碑）
- 已完成：M0 → M1 → M1.1 → M1.2 → M1.3 → M2 → **M2.1**
- 版本：`0.1.0-m2`（本轮**未**推进版本号）
- **Last code commit：`313c542`**（M2.1 代码 + 测试 + probe 回应日志；主修复提交为 `fb202eb`）
- **Acceptance kit commit：`905fcc8`**（只含 HANDOFF / 验收手册 / `set-lab-ip.ps1` v2，无产品代码）
- **Working tree at validation: clean**
- **M2.1 code 状态：Implementation complete；Two-machine manual DoD：PASS**
  （2026-09-20 17:30–18:18 两台实机跑完 20 步，20/20 通过，见第 9 节）
- **是否满足完整 M2 DoD：是**（两机手工验收已回填）
- 下一阶段：**M3 — TLS Host/Client + 同子网连接校验**
- **M3 已于 2026-09-21 开工**（用户指令：「开始步骤吧，遇到要我帮忙的地方你就停」）。
  **阶段 0～5 的代码部分已全部完成**（24 步里的第 1～23 步），
  当前停在**第 24 步：两机验收——需要用户参与**，本机没有 RFC1918 网卡，无法自证。
  逐步明细、实测数据与禁止回访项见**第 15、16 节**——以第 15 节为准，本节的 M3 描述可能滞后。
  最后代码提交 `5bf3cb6`，`dotnet build` 0 警告 0 错误，`dotnet test` **573 PASS / 0 FAIL**
- **第 24 步物料已就绪**（验收器 `tools/LanRemote.Acceptance` + 手册 `docs/M3_TWO_MACHINE_ACCEPTANCE.md`
  + 打包 `LanRemote-0.1.0-m2-m3-acceptance-win-x64.zip`）。
  **注意：不要拿 `LanRemote.App` 验收 M3**——它引用了 `LanRemote.Transport` 但一行都没调用，
  打它的包只能重证 discovery。本机所有**前置条件失败路径已实测**，happy path 必须两台
  `192.168.1.0/24` 实机才能跑 → **等用户执行**。详见第 15 节步骤 24。

### 关于 git 记账方式

HANDOFF 不写 HEAD hash（写完立刻过期的自引用）。固定使用：
`Last code commit`（最后一次代码/测试提交）+ `Working tree at validation`。
允许 HEAD 比 Last code commit 新（之后会有单独的文档提交）。

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

## 12. 测试

```text
dotnet test LanRemote.sln -c Debug --no-build
```

**M2.1 真实执行结果：408 passed / 0 failed / 0 skipped**

| 项目 | M1.3 后 | M2 后 | M2.1 后 | 本轮增量 |
|---|---:|---:|---:|---:|
| LanRemote.Core.Tests | 125 | 125 | 125 | 0 |
| LanRemote.Security.Tests | 65 | 65 | 65 | 0 |
| LanRemote.IntegrationTests | 3 | 3 | 3 | 0 |
| LanRemote.Protocol.Tests | 7 | 189 | **215** | **+26** |
| **合计** | **200** | **382** | **408** | **+26** |

既有 382 条全部继续通过（**没有删除任何测试换取通过**）。
唯一被替换的测试是 `Capabilities_AreDedupedAndEmptiesRemoved`——
它的语义（空 capability 被静默移除后仍接受整条报文）与 M2.1 收紧后的规格直接冲突，
按规格要求重写为 6 组更严格的新测试。

本轮新增的 26 条测试：

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

## 15. 下一步 —— M3（TLS Host/Client + 同子网连接校验）

**开工指令未下达**：下面是已定型的施工顺序，等用户说"开工"才动手。
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

    因此新建 `tools/LanRemote.Acceptance`（`net10.0-windows` 控制台，已加进 sln）：

    | 文件 | 作用 |
    | --- | --- |
    | `AcceptanceContext.cs` | 刻意照抄 `App.xaml.cs` 的 DI 接线（vault→证书→身份→binding→SubnetPolicy→discovery） |
    | `InfoRole.cs` | `info`：环境自检，打印身份/指纹/端口/合格网卡 |
    | `HostRole.cs` | `host [--seconds N]`：起 discovery + `TransportHost`，每条连接跑完整 pre-auth 会话 |
    | `ClientRole.cs` | `client --scenario …`：发现→冻结快照→TLS→hello→等对端收尾 |
    | `Program.cs` | 退出码 **0 = 符合预期 / 1 = 真失败 / 2 = 前置条件不满足** |

    **四个场景，前三个必做、第四个本次不跑：**

    | 场景 | 命令关键参数 | 通过标准 | 状态 |
    | --- | --- | --- | --- |
    | `success` | `--device-code <B>` | 控制端 `presentedPin` 与 discovery 的 `pin` 逐字符一致 + `outcome=PASS peerClosed=eof`；被控端 `outcome=PreAuthenticated rejection=-` | 待真机 |
    | `pin-mismatch` | `--device-code <B>` | 握手被拒；且 `tcpProbe=open` 必须出现 | 待真机 |
    | `timeout` | `--device-code <B>` | 不发 hello，约 5s（= `lengthPrefixTimeout`）被切；被控端 `rejection=pre-auth-timeout` | 待真机 |
    | `cross-subnet` | `--address <ip> --pin <64hex>` | —— | **本次不跑，见 §14 第 13 条** |

    被控端退出前的汇总行应为 `accepted=2 preAuthenticated=1 rejected=1 cleanStop=True`
    （`pin-mismatch` 死在 TLS 阶段，进不了会话处理器，故不计入 `accepted`）。

    **本机已实测（真实执行，不是推演）：**

    - `dotnet build` 0 警告 0 错误、`dotnet test` **573 PASS / 0 FAIL**（加入该工具项目后不变）
    - `info` 真跑通 DPAPI→身份→证书整条链（deviceCode `M5WC-14GX`，
      certSha256 `89A5C10E…5445`），正确判 `outcome=FAIL reason=no-qualified-rfc1918-nic` → exit 2
    - `host --seconds 3` → `[HOST][RESULT] outcome=FAIL reason=no-qualified-nic` → exit 2
    - `client --scenario success --device-code AAAA-AAAA` → discovery 真实告警 +
      `reason=peer-not-found` → exit 2
    - `client --scenario cross-subnet --address 192.168.1.20 …` → `tcpProbe=unreachable`
      → `reason=peer-port-unreachable` → exit 2
    - 发布版 `artifacts/m3-acceptance/LanRemote.Acceptance.exe info` 在 **`env -u DOTNET_ROOT`**
      （不加载 SDK 环境）下正常运行，自包含成立
    - `run-acceptance.ps1 -PeerDeviceCode AAAA-AAAA` 在 step 0 正确中断，exit 2，中文渲染正常

    **修复的一个真实缺陷（空洞断言）**：`pin-mismatch` / `cross-subnet` 原先是
    「握手失败即 PASS」。对端端口没开 / 防火墙拦掉 / host 没启动，同样会让握手失败，
    于是照样报 PASS——而同子网闸门和 pinning 一行都没被执行到。
    已加 ① 纯 TCP 探针（不通 → exit `2`，不判 PASS）② `LooksLikeNothingListening`
    按 `SocketError` 区分「没连上」与「被拒绝」（刻意**不**把 `ConnectionReset` 算进前者，
    因为 accept 后立刻关闭正是走 RST）。**变异验证**：修复前 `192.168.1.20:45873`
    （无人监听）报 PASS/exit 0，修复后报 `peer-port-unreachable`/exit 2。
    同时把 `WaitForPeerCloseAsync` 的 `catch → return true` 拆成
    `eof` / `reset` / `still-open` / `unexpected-data` 四种可判定结局，不再把超时和切断混为一谈。

    **物料**：`LanRemote-0.1.0-m2-m3-acceptance-win-x64.zip`
    （217 条目 / 原始 77.8 MiB / zip 34.6 MiB），由 `scripts/acceptance/make-m3-package.py` 产出
    （`LANREMOTE_M3_SKIP_PUBLISH=1` 跳过 publish 只重打包；`LANREMOTE_M3_CLEAN=1` 才先清空目录）。
    包内含 `START-HERE.md`（=`docs/M3_TWO_MACHINE_ACCEPTANCE.md`）、`START.cmd`、
    `run-acceptance.ps1`、`set-lab-ip.ps1`。两个 ps1 打包时强制加 BOM，
    **`START.cmd` 不加 BOM 且必须是纯 ASCII**（cmd.exe 按控制台代码页解析 `.cmd`，
    中文连注释都会解错并报「不是内部或外部命令」）。
    手册：§0.1 怎么启动、§3 逐场景命令与判定、§4 host 汇总行、§5 两个已知缺口、
    §6 要贴回来的四段证据、§7 排障表。
    **提交 `5ae052f`** · 该提交只含验收器 / 手册 / 打包脚本 / HANDOFF，**无产品代码改动**
    （`Last code commit` 仍是 `5bf3cb6`）。zip 与 `artifacts/` 都在 .gitignore 里，
    需要时用 `make-m3-package.py` 重现。

    **补：为什么又加了 `START.cmd`**（用户反馈「解压出来没有启动 exe」）。
    exe 其实在包里，叫 `LanRemote.Acceptance.exe`，但 ① 混在 200 多个 dll 里，
     ② 它是**控制台程序**，双击不带参数只会打 usage 然后 exit 2，窗口一闪而过——
    看起来就像没有 exe。`START.cmd` 做三件事：切到自身目录 → 调 `run-acceptance.ps1`
    → **末尾 `pause`** 让窗口不关。同时把驱动脚本改成**交互式**：
    先跑 `info` 自检（不合格直接中止），再问「这台是被控端还是控制端」（输 1/2），
    控制端再问对端设备码，然后跑完三个场景。非交互仍可
    `-Role host` / `-Role client -PeerDeviceCode XXXX-XXXX`。
    实测：`-Role host` → step 0 中止 exit 2 且中文渲染正常；`-Role bogus` → ValidateSet 拒绝；
    **全新解压目录**端到端跑通（自动找到同目录的 exe）。
    未本机实测：交互式 1/2 菜单与 host 分支的等待（被「本机无 RFC1918 网卡」挡在 step 0 之前）。

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

- **两机手工验收已完成并 PASS**（第 9.1 节，20/20）。M3 的开工前提已满足，
  但仍需用户明确下达开工指令——不要在没被要求时自行开始 M3
- **不要把 probe 回应发回 `remote.Port`**：probe 源端口是随机临时端口，回应必须打到 `remote.Address:45872`
  （实测三次源端口 `50193` / `54670` / `58869`，均为随机端口，回源端口对端必收不到）
- **不要删掉 sender 的 `MulticastInterface` 设置**：不设就会全部走系统默认组播路由（多网卡必踩）
- **不要在 capabilities 里恢复「空项静默跳过」或「按去重后数量判上限」**（两者都是可绕过的校验）
- **不要**为了图省事把 `StartDiscoveryAsync` 改回 `void`、或让 `LoadAsync` 无条件覆盖状态文本
- **不要重复做两机验收**：2026-09-20 已 PASS（第 9.1 节）。只有改动了 discovery
  收发逻辑（网卡筛选 / probe / announce / 缓存 TTL）才需要重跑
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
- **不要伪造构建/测试结果**：本文件所有数字均为实际执行输出
- **验收脚本别再犯这 5 个错**（详见第 9.1 节末表）：`netsh` 退出码不可信、
  `-join` 在 PS 5.1 的参数绑定陷阱、自动选网卡必须排除虚拟网卡、
  Windows IPv4 不支持「DHCP + 附加静态」共存、`check-logs` 必须带 `-Since` 才准
- **别拿 `LanRemote.App` 验收传输层**：它 `ProjectReference` 了 `LanRemote.Transport` 却**零调用**。
  任何「打 App 的包去两机跑」的方案都只能重证 discovery，碰不到 TLS/pinning/hello
- **验收器里禁止「失败即 PASS」**：对端端口没开、防火墙拦掉、host 没启动，都会让握手失败。
  必须先用**纯 TCP 探针**证明端口是开的，再判「被拒绝」；探针不通一律返回退出码 `2`
  （前置条件不满足），**绝不能**报 `0`。同理按 `SocketError` 区分「没连上」与「被拒绝」时，
  **不要**把 `ConnectionReset` 归进「没连上」——accept 后立刻关闭正是走 RST
- **不要把「对端怎么收尾」压成一个布尔**：`catch → return true` 会让「读超时」和「被切断」
  在输出里长得一模一样。要区分为 `eof` / `reset` / `still-open` / `unexpected-data` 四种，
  判定标准才写得出来
- **写验收手册前先确认被控端到底会不会打印**：`TransportHost` 无 logger，同子网拒绝 / 准入拒绝 /
  TLS 失败**全是静默**；只有发生在会话处理器**内部**的 pre-auth 超时才有输出。
  手册里不要凭"应该会记日志吧"去写判定标准（见 §14 第 12 条）

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
