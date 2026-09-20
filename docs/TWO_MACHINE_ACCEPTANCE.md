# M2 两机手工验收手册

> 这是进入 **M3 — TLS Host/Client + 同子网连接校验** 之前的**必需关卡**。
> HANDOFF 第 9 节当前记为 `Two-machine manual discovery: NOT RUN`，验收通过后才能改为 PASS。
>
> 对应构建：M2.1（`Last code commit 313c542`），版本 `0.1.0-m2`。
>
> **⚠ 2026-09-20 首次实测失败，根因已定位：两台机器都在 `172.100.166.x`，不是 RFC1918。**
> 请先读第 1.1 / 1.2 节，用 `set-lab-ip.ps1` 加一个 `192.168.1.x` 私有地址后再往下做。

---

## 0. 你会拿到什么

| 文件 | 说明 |
|---|---|
| `LanRemote-0.1.0-m2-win-x64.zip` | **便携版（绿色解压包）**，不是 MSI 安装器（版本号仍是 `0.1.0-m2`，M2.1 未推进版本） |
| `check-env.ps1` | 验收**前**的环境自检（网卡 / 端口 / 防火墙配置文件 / 残留进程） |
| `check-logs.ps1` | 验收**后**的日志检查（发现事件计数 + 访问密钥泄漏扫描） |
| `set-lab-ip.ps1` | **加/删私有 lab 地址**（管理员运行）。本机网络不是私有段时**必须先跑这个**，见第 1.1 / 1.2 节 |

- **自带 .NET 10 运行时**，目标机不需要安装任何东西，也**不需要管理员权限**。
- 解压到任意目录（建议 `C:\LanRemote` 或桌面文件夹），双击 `LanRemote.App.exe` 即可。
- 数据目录固定为 `%LOCALAPPDATA%\LanRemote\`（`secrets.bin` / `config.json` / `logs\`）。
- **不要**把 A 机的 `secrets.bin` 拷到 B 机：DPAPI 绑定 Windows 用户，拷过去必然解密失败，
  而且两台机器本来就该有各自独立的身份。

---

## 1. 前置条件

两台 Windows 电脑（下文称 A / B），必须同时满足：

1. 位于**同一个 RFC1918 IPv4 子网**，例如 `192.168.1.10/24` 与 `192.168.1.20/24`；
2. 都能互相 ping 通；
3. **不是** `172.100.x.x` 这类看起来像私有、实际不在 172.16–172.31 范围内的地址
   （本机的开发机就栽在这一条上）。
   判据见 `check-env.ps1` 的第 [2] 项，它会逐条标注 `RFC1918` / `NOT-private`。

### 1.1 首次实测失败案例：`172.100.166.x` 不是私有地址

第一次两机实测的现场数据：

| | 电脑 A（DESKTOP-D132BMD） | 电脑 B（DESKTOP-CU2Z63D） |
|---|---|---|
| IPv4 | `172.100.166.220` | `172.100.166.65` |
| 默认网关 | `172.100.166.254` | — |
| 链接速度 | 1000 Mbps | 1000 Mbps |
| 现象 | 设备列表为空 | 设备列表为空 |

**根因**：RFC1918 的 172 段只覆盖 **`172.16.0.0` – `172.31.255.255`**（第二段 16~31）。
`172.100.166.x` 的第二段是 **100**，落在范围之外，**属于公网地址段**。

LanRemote 有一条硬性安全约束（规格第 3 节、ADR-022）：**只使用 RFC1918 私有网卡**。
于是 `NetworkInterfaceSelector` 正确地判定这两张网卡不合格，日志输出

```text
没有找到任何合格的私有 IPv4 网卡，局域网发现不会发送也不接收。
```

→ 不开 socket、不广播、不接收 → 设备列表永远为空。**软件行为正确，不是 bug。**

> 注意：网线是好的、两台能互通、同网段也正确。问题纯粹在**地址段本身**。
> `172.100.x.x` 这种"看着像私有"的地址，最容易骗过直觉。

### 1.2 修复：给两台机器各加一个私有地址（`set-lab-ip.ps1` v2）

用 `set-lab-ip.ps1`（**管理员 PowerShell**，两台各跑一次，角色不同）：

```powershell
# 电脑 A
powershell -ExecutionPolicy Bypass -File set-lab-ip.ps1 -Role A     # → 192.168.1.10/24

# 电脑 B
powershell -ExecutionPolicy Bypass -File set-lab-ip.ps1 -Role B     # → 192.168.1.20/24
```

#### 为什么脚本要"整口切静态"（v1 就是栽在这里）

直觉上"保留 DHCP 地址、再追加一个静态地址"应该可行 —— **实测证明不行**。
Windows IPv4 上，一张网卡只能是 **DHCP 或 静态**，不能共存：

| 操作 | 预期 | 实测结果 |
| --- | --- | --- |
| `New-NetIPAddress` 在 DHCP 接口上追加地址 | 两个地址共存 | 接口 `Dhcp` 被翻成 `Disabled`，DHCP 租约**丢失** |
| `netsh interface ipv4 add address` 追加地址 | 两个地址共存 | 同上，`Dhcp` → `Disabled` |
| 追加后再删掉该地址 | 回到原状 | 只剩 APIPA `169.254.x.x`，**无网关无 DNS** |

v1 就是按"追加共存"写的，结果 `-Undo` 之后电脑 A 的以太网**没有可用 IPv4**，
第二次 `-Undo` 还因为找不到非 APIPA 地址而直接报 `No IPv4 adapter found`。
（该脚本已归档为 `_set-lab-ip.v1.broken.ps1.bak`，不要再用。）

#### v2 的实际做法

1. 读取当前 IPv4 配置（地址 / 掩码 / 网关 / DNS / 是否 DHCP），存到
   `%TEMP%\lanremote-lab-ip-state.json`；
2. 把该网卡**切成静态**，但用的就是刚才读到的那套配置 —— 所以**不会断网**
   （实测切完后 `ping 172.100.166.254` 通，DNS 保留，默认路由还在）；
3. 再追加 `192.168.1.10`（或 `.20`）/24，**不带网关**，不与原网关抢默认路由；
4. 把网络配置文件设为 `Private`（带重试：刚切静态时网卡处于 `Identifying...`，
   首次设置会失败），并创建入站规则 `LanRemote Discovery UDP 45872`。

结果：网卡**同时**持有 `172.100.166.x`（Manual）和 `192.168.1.x`（Manual）。

`-InterfaceAlias "以太网"` 可显式指定网卡；不指定时自动挑**有线的、Up 的、有非 APIPA 地址**的那张。

#### 回滚

```powershell
powershell -ExecutionPolicy Bypass -File set-lab-ip.ps1 -Undo
```

会移除 lab 地址 → 接口切回 DHCP → `ipconfig /renew` → **自检**：
确认没有残留 lab 地址、确认拿到了非 APIPA 地址、确认 `Dhcp=Enabled`。
任何一项不过就打印手工修复命令并以退出码 1 结束。

#### v2 实测记录（本机 2026-09-20，Windows PowerShell 5.1）

| 场景 | 结果 |
| --- | --- |
| `-Role A`（DHCP → 静态 + 追加 lab 地址） | `EXIT=0`，以太网 `172.100.166.220` Manual + `192.168.1.10` Manual |
| 切静态后连通性 | `ping 172.100.166.254` = True，DNS `172.100.162.101/102` 保留 |
| `-Undo`（静态 → DHCP） | `EXIT=0`，回到 `172.100.166.220/24 Dhcp`，`OK:` 自检通过 |
| 已是 DHCP 时再 `-Undo`（幂等） | `EXIT=0` —— 这正是 v1 会崩的场景 |

> 两个踩过的坑，都已修掉：`netsh` 在"已经是 DHCP"时**返回非 0** 但实为成功
> （不能只看退出码，必须复核 `Get-NetIPInterface`）；
> `($x | ForEach-Object { $_.IPAddress } -join ', ')` 在 PS 5.1 会把 `-join`
> 当成 `ForEach-Object` 的参数而抛异常（要写成 `$x.IPAddress -join ', '`）。

（防火墙规则如需删：`Remove-NetFirewallRule -DisplayName "LanRemote Discovery UDP 45872"`）

在**每一台**上都先跑一次自检：

```powershell
powershell -ExecutionPolicy Bypass -File check-env.ps1
```

只有第 [2] 项输出 `RESULT: OK` 才继续。若输出 `FAIL`，先解决网络再往下做，
后面的步骤在这台机器上必然全部失败。

---

## 2. 防火墙（最容易卡住的一步）

LanRemote **不会**在程序里自动改防火墙（那是 M10 的事）。UDP 45872 入站如果被拦，
现象就是「两台都正常启动，但谁也看不见谁」。

首次启动时 Windows 会弹「是否允许访问网络」——**必须点允许**，至少勾选**专用网络**。

如果自检第 [4] 项显示网络配置文件是 `Public`，或者启动后互相看不见，用管理员 PowerShell 手工放行：

```powershell
New-NetFirewallRule -DisplayName "LanRemote Discovery UDP 45872" `
  -Direction Inbound -Protocol UDP -LocalPort 45872 `
  -Action Allow -Profile Private,Domain
```

排查时也可以临时关掉防火墙验证一次（验证完记得开回来）：

```powershell
Set-NetFirewallProfile -Profile Private,Domain -Enabled False
```

---

## 3. 验收步骤（20 步）

建议 A、B 两台并排放置，各自能看到对方的屏幕。表格里的「实测」列由你填写。

| # | 操作 | 预期结果 | 实测 | ✓/✗ |
|---|---|---|---|---|
| 1 | 在 A 上解压并启动 `LanRemote.App.exe` | 窗口出现「设备码」「证书指纹」；状态栏显示「已加载配置与本机身份」；日志出现 `局域网发现启动。有效网卡=1` | | |
| 2 | 在 B 上同样启动 | 同上 | | |
| 3 | 等 3~6 秒 | **A 的设备列表出现 B**（名称 / 设备码 / IP / ●在线） | | |
| 4 | 同时 | **B 的设备列表出现 A** | | |
| 5 | 检查两台列表 | 各自**只看到对方，看不到自己**（自公告去重） | | |
| 6 | 静置 20 秒 | 两台列表都**只有 1 条**，不出现重复条目 | | |
| 7 | 关闭 B（正常退出，不是杀进程） | — | | |
| 8 | 观察 A | **约 7~9 秒后 A 移除 B**；A 日志出现 `设备离线` | | |
| 9 | 重新启动 B | — | | |
| 10 | 等 ≤6 秒 | **B 再次出现在 A 的列表** | | |
| 11 | 在 B 上**取消勾选**「允许被发现」 | B 的状态栏/配置已保存 | | |
| 12 | 观察 A | **TTL 后（≤9 秒）A 移除 B** | | |
| 13 | 检查 B | **B 仍然能看到 A**（AllowDiscovery=false 只停「被看见」，不停止看见别人） | | |
| 14 | 在 B 上点「刷新」 | **B 仍能 probe 到 A**；B 日志出现 `已发送 probe` | | |
| 15 | 在 B 上**重新勾选**「允许被发现」 | — | | |
| 16 | 等 ≤6 秒 | **A 再次发现 B** | | |
| 17 | 在 A 上点「刷新」 | A 日志出现 `已发送 probe` | | |
| 18 | 看 **B 的日志** | B 日志出现 `已回应来自 192.168.1.x 的 probe：unicast → 192.168.1.y:45872（不使用源端口 5xxxx）`<br>**这一步是 M2.1 修的 bug 的现场验证**：回应目标必须是 `45872`，且明确标注没有使用源端口 | | |
| 19 | 全程 | 两台应用**均无崩溃**（无未处理异常弹窗，日志无 `Error` 级条目） | | |
| 20 | 两台各跑 `check-logs.ps1` | 输出 `VERDICT: PASS - no access key found in logs` | | |

### 补充验证（若环境允许，优先做）

- **一台走 Ethernet、一台走 Wi-Fi**，但两者在同一 subnet —— 这组最容易暴露组播问题。
- **某台机器同时有 Wi-Fi + Ethernet**（都是私有地址）：
  启动后看日志 `局域网发现启动。有效网卡=2`，
  并确认对端能正常看到它 —— 这是 M2.1 第 2 项（显式 `IP_MULTICAST_IF`）的实地验证。

---

## 4. 验收后：日志检查

在**每一台**上跑：

```powershell
powershell -ExecutionPolicy Bypass -File check-logs.ps1
```

它会输出：

- 关键事件计数：`no eligible NIC`（必须为 0）、`discovery started`（≥1）、
  `device discovered`（≥1）、`device went offline`、`probe sent`；
- 本机身份行（设备码 + 指纹前缀）；
- **访问密钥泄漏扫描**：正则 `[0-9A-HJKMNP-TV-Z]{26}`（Crockford Base32，128-bit 密钥 = 26 字符；
  设备码只有 8 字符，不会被误判）。命中数必须为 **0**。
  万一命中，脚本**只打印掩码后的尾部**，不会把真实密钥回显到控制台。

---

## 5. 结果回填模板

验收完成后，把下面的内容填好发回来（或者直接贴原始输出），我会据此更新 `HANDOFF.md`：

```text
验收日期        :
机器 A 主机名/IP :
机器 B 主机名/IP :
子网/掩码       :
A 网卡类型       : Ethernet / Wi-Fi / 两者都有
B 网卡类型       : Ethernet / Wi-Fi / 两者都有
防火墙           : 是否需要手工放行 UDP 45872（是/否）

步骤 1-20 结果   : 全部通过 / 第 X 步失败（第 X 步的实际现象：...）
步骤 18 B 日志原文: （把 "已回应来自 ..." 那一行贴出来）
check-logs  verdict（A）:
check-logs  verdict（B）:
崩溃            : 无 / 有（现象：...）
其他异常        :
```

---

## 6. 故障排查

| 现象 | 第一嫌疑 | 怎么确认 / 怎么办 |
|---|---|---|
| 两台都看不到对方 | 防火墙拦了 UDP 45872 入站 | 临时关防火墙（`Set-NetFirewallProfile -Profile Private,Domain -Enabled False`）再试；通了就说明是防火墙 |
| 一台看不到，另一台看得到 | 看不到那台的 `AllowDiscovery` 关了 / 它的网络是 Public 配置文件 | 跑 `check-env.ps1` 看第 [4] 项 |
| 日志出现 `没有找到任何合格的私有 IPv4 网卡` | 这台没有 RFC1918 地址（例如 `172.100.x.x` —— **已实际发生**） | 跑 `set-lab-ip.ps1 -Role A\|B` 加一个 `192.168.1.x`，见第 1.2 节 |
| 设备出现后 7 秒就消失 | 单向可达（A 收得到 B，B 收不到 A） | 双向都检查防火墙 |
| 刷新后对方不出现 | probe 回应被丢（M2.1 修的就是这个） | 看对方日志有没有 `已回应来自 ... 的 probe` |
| 列表里出现自己 | 自公告去重失效 | 属于 bug，立即回报 |
| 日志里出现疑似密钥 | 密钥泄漏 | 属于 **BLOCKER**，立即回报，不要进入 M3 |

---

## 7. 验收不通过时

- **不要**自己改代码然后继续测 —— 把现象和日志原文发回来。
- 只要第 20 步（无密钥泄漏）或第 5 步（看不到自己）失败，就判定为 **BLOCKER**，M3 不得开工。
- 其余步骤失败按现象回报，我会定位后再出一版。
