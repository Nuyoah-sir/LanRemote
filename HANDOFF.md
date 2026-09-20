# LanRemote HANDOFF

> 模板来源：`LanRemote_Implementation_Package/09_HANDOFF_TEMPLATE.md`
> 更新时间：**2026-09-20 14:45 (+08:00)**

---

## 1. 当前状态

- **当前里程碑：M2 — 网卡筛选 + UDP 局域网设备发现**
- 已完成：M0 → M1 → M1.1 → M1.2 → M1.3 → **M2**
- 版本：`0.1.0-m2`
- **Last code commit：`857eaa6`**（M2 代码 + 测试 + 版本推进）
- **Working tree at validation: clean**
- **M2 code 状态：Implementation complete；Two-machine manual DoD：NOT RUN（见第 9 节）**
- 下一阶段：**M3 — TLS Host/Client + 同子网连接校验**（本轮不施工）

### 关于 git 记账方式

HANDOFF 不写 HEAD hash（写完立刻过期的自引用）。固定使用：
`Last code commit`（最后一次代码/测试提交）+ `Working tree at validation`。
允许 HEAD 比 Last code commit 新（之后会有单独的文档提交）。

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
  `Broadcast = true` → `MulticastTimeToLive = 1`。这保证组播/广播是从对应网卡发出的。
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
  用 **unicast** 向 `remoteAddress:45872` 回一个正常 announcement
  （对方之所以发 directed broadcast，往往就是收不到组播，unicast 回应兼容性更好）。
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

### Two-machine manual discovery: **NOT RUN**

原因（两条，各自都足够）：

1. **只有一台物理测试机**；
2. **本机唯一的活跃网卡是 `172.100.166.220`，不属于 RFC1918**
   （172.16.0.0/12 只覆盖 172.16–172.31）。WLAN 与两个「本地连接*」均为「媒体已断开连接」。
   因此 `NetworkInterfaceSelector` 正确地判定「没有合格的私有 IPv4 网卡」，
   本机环境按设计不支持 LanRemote 的发现。

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

**未改动**：M1 全部安全实现（`DpapiSecretVault`、`DpapiAccessSecretStore`、`SecretGenerator`、
`DeviceCertificateService`、`DeviceIdentityService`、`CrockfordBase32`、`DeviceCode`）。
尤其没有重新引入 `PersistKeySet` / `Exportable` / `KeyEncipherment`。

## 11. 构建

```text
source scripts/env.sh
dotnet build LanRemote.sln -c Debug
```

结果：**PASS** —— 12 个项目全部生成，**0 个警告，0 个错误**

## 12. 测试

```text
dotnet test LanRemote.sln -c Debug --no-build
```

结果：**382 passed / 0 failed / 0 skipped**

| 项目 | M1.3 后 | M2 后 | 增量 |
|---|---:|---:|---:|
| LanRemote.Core.Tests | 125 | 125 | 0 |
| LanRemote.Security.Tests | 65 | 65 | 0 |
| LanRemote.Protocol.Tests | 7 | **189** | +182 |
| LanRemote.IntegrationTests | 3 | 3 | 0 |
| **合计** | **200** | **382** | **+182** |

既有 200 条全部继续通过；没有删除或弱化任何 M1 测试。

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

## 14. 已知问题 / 技术债

1. **M2 手工 DoD 未完成**（第 9 节）：需要一台有 RFC1918 网卡的机器，最好两台。
2. **本机开发环境无法验证发现**：唯一活跃网卡是 `172.100.166.220`（公网段）。
   若将来要在本机验证，需要改用 192.168 / 10.x 的网络，或另开实验性开关（当前不做）。
3. **网卡热插拔未处理**：`StartAsync` 做一次快照并保持运行期不变；`INetworkBindingProvider.Refresh()`
   已预留，M9 再接 `NetworkChange`。
4. **虚拟网卡过滤是启发式**：可能误杀名字里带 `tap`/`vpn` 的真实网卡；需要 VPN LAN 时应另开实验设置。
5. **真实 accept 路径未走真实 socket 验证**（第 9 节）。
6. UI 交互无自动化覆盖（累计遗留）。
7. 日志无轮转（M9，ADR-013）。
8. `LanRemote.Sessions` / `Capture` / `Input` 仍是空项目占位。

## 15. 下一步 —— M3（TLS Host/Client + 同子网连接校验）

按 `07_MILESTONES_AND_TASKS.md`：

1. Host 端 TCP 45873 listener；accept 后立刻用 `SubnetPolicy.IsAllowedPeer` 校验
   （`socket.LocalEndPoint.Address` / `RemoteEndPoint.Address`），不同子网立即关闭。
2. `SslStream` + 自签名 ECDSA 证书；客户端按 discovery 得到的 `certSha256` 做 pinning，
   用 `CryptographicOperations.FixedTimeEquals` 比较；**不允许 `return true` 无条件放过**。
3. **必须补一条真实 SslStream server/client 握手集成测试**——ADR-018 的未关闭风险就靠它收口：
   用实测结果确认 `EphemeralKeySet` 是否满足 Windows TLS 服务端要求，再决定是否维持。
4. Control channel 的 length-prefixed JSON framing（上限 1 MiB）与 `channel_hello`。
5. M3 **不要**实现 AuthChallenge / HMAC / 访问密钥认证（那是 M4）。

## 16. 下一位 AI 不要重复做

- **不要进 M3**：M2 已停在这里，下一步由审计确认后再施工
- **不要重新定义** `ISubnetPolicy` / `DiscoveredDevice` / `DeviceIdentity` / `DeviceCode`
- **不要**用字符串前缀或「前三段相同」判断同网段
- **不要**往 announcement 里加地址字段并相信它（地址必须来自 UDP source）
- **不要**为了「支持 VPN 局域网」在 M2 放宽虚拟网卡过滤
- **不要**在 M2 实现任何 TLS / Auth / 视频 / 输入
- **不要**在程序里自动改防火墙（属于 M10）
- **不要**声称两机手工验证通过——它明确是 NOT RUN
- **不要伪造构建/测试结果**：本文件所有数字均为实际执行输出

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
