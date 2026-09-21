# LanRemote M3 两机验收手册

> 里程碑：**M3 — TLS Host/Client + 同子网校验**
> 版本：`0.1.0-m2`
> 物料：`LanRemote-0.1.0-m2-m3-acceptance-win-x64.zip`
> 日期：2026-09-21

---

## 0. 先说清楚：这个包里是什么，不是什么

这个包**不是** LanRemote 客户端。它装的是 `LanRemote.Acceptance.exe`，一个命令行验收器。

原因是：`LanRemote.App`（WPF 客户端）目前只是**引用**了 `LanRemote.Transport`，
**一行都没有调用**。在两个机器上跑 App，只能证明 M2 的 discovery 还能用，
碰不到 M3 新写的 TLS / pinning / `channel_hello` 的任何一行代码。

所以 M3 的验收必须通过这个 CLI 来做。它能真实地：

- 在合格网卡上 listen，走 `accept → 同子网校验 → 准入限额 → TLS` 这条真链路；
- 用自签名 ECDSA P-256 证书做 TLS，靠 **证书指纹 pinning**（不是 CA 校验）认对端；
- 收一帧 `channel_hello` 并严格解析，然后到达显式状态 `PreAuthenticated` 并干净关闭。

> 端口：UDP **45872**（发现）、TCP **45873**（控制通道）。控制与视频是两条独立 TLS 连接
> （ADR-003），M3 只做控制这一条。

---

## 0.1 怎么启动 —— 双击 `START.cmd`，不要双击 exe

解压后目录里有 200 多个文件，唯一该用的程序叫 **`LanRemote.Acceptance.exe`**。
**不要直接双击它**：它是控制台程序，不带参数时只会打印用法然后立刻退出，
窗口一闪而过，看起来就像"包里没有 exe"。

正确入口是 **`START.cmd`**：

```
START.cmd          <-- 双击这个
START-HERE.md      本手册
run-acceptance.ps1 交互式驱动（START.cmd 就是调用它）
set-lab-ip.ps1     配 lab 网段（需管理员）
LanRemote.Acceptance.exe   真正的程序，但请通过 START.cmd 用
...其余 200+ 个是 .NET 自包含运行时的 dll
```

双击 `START.cmd` 之后会：

1. 先跑 `info` 环境自检（不合格就直接中止并告诉你原因）；
2. 问你**这台机器是被控端还是控制端**（输 `1` 或 `2`）；
3. 被控端 → 直接起监听；控制端 → 再问你对端的设备码，然后跑完三个场景；
4. 跑完**窗口不关**（`START.cmd` 末尾有 `pause`），方便你把输出整段拷出来。

想跳过交互也可以：

```powershell
powershell -ExecutionPolicy Bypass -File .\run-acceptance.ps1 -Role host
powershell -ExecutionPolicy Bypass -File .\run-acceptance.ps1 -Role client -PeerDeviceCode XXXX-XXXX
```

> `START.cmd` 里**只有 ASCII 字符**，这是有意的：cmd.exe 用控制台代码页解析 `.cmd`，
> 中文字符（连注释里的也算）会被解错并报"不是内部或外部命令"。所有中文都放在
> `run-acceptance.ps1`（UTF-8 with BOM）里。

---

## 1. 前置条件

| # | 条件 | 怎么确认 |
| --- | --- | --- |
| 1 | 两台 Windows 机器，接在同一个交换机 / 同一根线上 | `ipconfig` 能看到同一段地址 |
| 2 | **两台机器都必须在 RFC1918 私有网段**（`10/8`、`172.16–172.31`、`192.168/16`） | 跑 `info`，看 `bindings` 不是 `(无)` |
| 3 | 管理员 PowerShell（只有配 IP 那一步需要） | 标题栏带"管理员" |
| 4 | 入站 UDP 45872 放行 | `set-lab-ip.ps1` 会顺手建规则 |

### 1.1 为什么第 2 条最容易踩

LanRemote **故意**只认 RFC1918。你这两台实机在 `172.100.166.x` 上，
而 `172` 段**只覆盖到 `172.16`–`172.31`**，`172.100` **不是私有地址**。
所以不做任何配置的话，LanRemote 会正确地拒绝它们，设备列表是空的——
这是**对的**，不是 bug。（判定私有必须按数值区间，不能拿 `172.` 当前缀匹配。）

`info` 会把这件事直说：

```
[INFO][RESULT] outcome=FAIL reason=no-qualified-rfc1918-nic
```

### 1.2 配 lab 网段（每台机器跑一次，角色不同）

在**管理员** PowerShell 里，把 zip 解压后的目录当成当前目录：

```powershell
# 机器 A（控制端，待会儿跑 client）
powershell -ExecutionPolicy Bypass -File .\set-lab-ip.ps1 -Role A

# 机器 B（被控端，待会儿跑 host）
powershell -ExecutionPolicy Bypass -File .\set-lab-ip.ps1 -Role B
```

结果：A 得到 `192.168.1.10/24`，B 得到 `192.168.1.20/24`，
**同时保留原来的 `172.100.166.x` 不断网**。

> 这个脚本为什么是"先整口切静态再追加"？因为 Windows 一张网卡**只能二选一**：
> DHCP 或静态，不能共存。直接给 DHCP 接口追加地址会把接口翻成 `Dhcp=Disabled`
> 并把租约丢掉——这台机器已经因此断网两次了。脚本 v2 的作法是先把当前这一套
> IP/掩码/网关/DNS 原样切成静态（不断网），再追加第二个地址（不带网关）。

**撤销**（验收完就撤）：

```powershell
powershell -ExecutionPolicy Bypass -File .\set-lab-ip.ps1 -Undo
```

---

## 2. 验收流程

### 第 1 步：两台机器各自自检

```powershell
.\LanRemote.Acceptance.exe info
```

**通过标准**：最后一行是

```
[INFO][RESULT] outcome=PASS // 本机可以参与两机验收
```

记下每台机器的 `deviceCode`（形如 `M5WC-14GX`）和 `certSha256`。
`deviceCode` **不是秘密**，可以贴在聊天里；`certSha256` 是要被 pin 的那个指纹。

### 第 2 步：在机器 B 起被控端

双击 `START.cmd`，输 `1`（HOST）。它会打印 `deviceCode`、监听地址，然后等 10 分钟。
**这个窗口不要关。**

等价的命令行写法：

```powershell
powershell -ExecutionPolicy Bypass -File .\run-acceptance.ps1 -Role host
# 或直接用程序：
.\LanRemote.Acceptance.exe host --seconds 600
```

### 第 3 步：在机器 A 跑三个场景

双击 `START.cmd`，输 `2`（CLIENT），再输入机器 B 的 `deviceCode`（形如 `M5WC-14GX`）。

它依次跑 `info` → `success` → `pin-mismatch` → `timeout`，
每个场景的完整输出存到 `%TEMP%\lanremote-m3-acceptance\client-<场景>.txt`，
最后打一张汇总表并给出总判定。

非交互写法（把 `XXXX-XXXX` 换成机器 B 的 `deviceCode`）：

```powershell
powershell -ExecutionPolicy Bypass -File .\run-acceptance.ps1 -Role client -PeerDeviceCode XXXX-XXXX
```

也可以手工一个个跑：

```powershell
.\LanRemote.Acceptance.exe client --scenario success      --device-code XXXX-XXXX
.\LanRemote.Acceptance.exe client --scenario pin-mismatch --device-code XXXX-XXXX
.\LanRemote.Acceptance.exe client --scenario timeout      --device-code XXXX-XXXX
```

### 第 4 步：收尾

- 机器 B 上 Ctrl+C 结束 `host`（或等它自己到点），把**整段控制台输出**存下来；
- 两台机器各跑一次 `set-lab-ip.ps1 -Undo`。

---

## 3. 四个场景与判定标准

退出码含义：`0` = 符合预期，`1` = **不符合预期（真失败）**，`2` = 前置条件不满足（环境没配好）。

> `2` 和 `1` 必须分清。`2` 是"没测成"，不是"测出来不合格"。
> 看到 `2` 先去解决环境问题，不要当成 M3 的缺陷。

### 场景 1：success（必做）

```powershell
.\LanRemote.Acceptance.exe client --scenario success --device-code XXXX-XXXX
```

证明：真机上 TLS 1.2/1.3 握手成功、证书指纹 pinning 通过、
`channel_hello` 被严格解析接受、服务端干净关闭。

**通过标准（两端都要看）**

机器 A：
```
[CLIENT] peer        = XXXX-XXXX 192.168.1.20:45873 pin=<64位十六进制>
[CLIENT] tls ok: proto=Tls13 presentedPin=<与上面 pin 完全一致>
[CLIENT] hello sent
[CLIENT] readBack    = eof // 对端发了 close_notify，连接干净关闭
[CLIENT][RESULT] outcome=PASS peerClosed=eof // hello 已被接受、服务端干净关闭（eof）
```

机器 B：
```
[HOST][RESULT] conn#1 peer=192.168.1.10 outcome=PreAuthenticated rejection=-
```

`presentedPin` 必须与 discovery 广播的 `pin` **逐字符一致**——
这是 ADR-028 要交给 M4 绑进 transcript 的那个值，验收时重点核对这一项。

### 场景 2：pin-mismatch（必做）

```powershell
.\LanRemote.Acceptance.exe client --scenario pin-mismatch --device-code XXXX-XXXX
```

证明：指纹不符时握手必须失败，且**失败前不会有任何应用数据被接受**。
工具会把期望指纹换成一个合法但不同的 64 位十六进制串。

**通过标准**

```
[CLIENT] tcpProbe     = open 192.168.1.20:45873
[CLIENT] 期望指纹被替换为 000…0001（原值 <真指纹>）
[CLIENT] handshake failed: System.Security.Authentication.AuthenticationException: 对端证书未通过校验（…）
[CLIENT][RESULT] outcome=PASS handshake=… // 握手按预期被拒绝
```

**注意 `tcpProbe = open` 这一行不能少。** 如果它是 `unreachable`，
工具会返回退出码 `2` 并拒绝判定——因为端口都没开的话，握手失败什么都证明不了
（可能是 host 没启动、防火墙拦了），那样报 PASS 是假通过。

机器 B 这边**不会打印任何 per-connection 的行**，这是预期的：
TLS 握手死在 `TransportHost` 内部，会话处理器根本没被调用（见 §5）。

### 场景 3：timeout（必做）

```powershell
.\LanRemote.Acceptance.exe client --scenario timeout --device-code XXXX-XXXX
```

证明：连上 TLS 却一直不发 hello 的对端，会被**绝对时限**切断。

host 端的时限设置：连接 3s / 握手 5s / 长度前缀 5s / 载荷 10s / hello 5s。
这里卡的是**长度前缀**那一段（客户端一个字节都不发），所以大约在 **5 秒**后被切。

**通过标准**

机器 A：
```
[CLIENT] 故意不发 hello，等服务端按 pre-auth 时限切断……
[CLIENT] readBack    = eof // 对端发了 close_notify，连接干净关闭
[CLIENT][RESULT] outcome=PASS peerClosed=eof // 服务端在 pre-auth 时限内切断（eof）
```

机器 B：
```
[HOST][RESULT] conn#1 peer=192.168.1.10 outcome=Rejected rejection=pre-auth-timeout
```

> 「绝对时限」的意思是：从进入这一阶段开始计时，**不因为期间读到了字节而重置**。
> 否则对端只要每 `时限-ε` 秒发一个字节就能永远挂着。这一点在步骤 15 已经用
> 真实 SslStream 实测过（滴流式发送仍在绝对时限上被切）。

### 场景 4：cross-subnet（**本次不跑**，见下）

```powershell
.\LanRemote.Acceptance.exe client --scenario cross-subnet --address <ip> --pin <64hex>
```

---

## 4. host 结束时那一行汇总

`host` 退出前会打印：

```
[HOST] accepted=2 preAuthenticated=1 rejected=1 cleanStop=True
```

跑完上面三个场景后，这一行**应该**是：

- `accepted=2` —— `success` 和 `timeout` 通过了同子网闸门并完成了 TLS；
  `pin-mismatch` 在 TLS 阶段就死了，不计入；
- `preAuthenticated=1` —— 只有 `success` 走到了 `PreAuthenticated`；
- `rejected=1` —— 只有 `timeout` 被 `ControlPreAuthSession` 明确拒绝；
- `cleanStop=True` —— 停机时没有残留连接。

---

## 5. 两个已知缺口（不是 bug，但要知道）

### 5.1 `TransportHost` 目前完全没有 logger

同子网校验失败、准入限额拒绝、TLS 握手失败，这三条都是**静默 `return`**，
控制台一行都不会有。所以场景 2 在被控端是"看不见"的，
判定完全依赖控制端的输出。

这在 M9 会被 ADR-024（网络诊断进 UI）正面解决；在那之前，
两机验收的证据就以**控制端为主、被控端为辅**。

### 5.2 场景 4（跨子网）在这套 lab 环境里跑不出来

要真正触发同子网拒绝，需要**控制端到被控端监听地址的包，源 IP 落在另一个子网**。

现在的 lab 配置是两台机器同时挂着 `172.100.166.x` 和 `192.168.1.x`。
host 只在 RFC1918 绑定上 listen，也就是只听 `192.168.1.20`；
而 A 发往 `192.168.1.20` 的包，Windows 会自动挑源地址 `192.168.1.10`——
**同一个子网**，于是闸门正确地放行，测不到拒绝那一支。

Windows 的 `New-NetRoute` / `route add` 都不能指定源地址，
所以**在不改产品代码（给 `TlsClientConnector` 加本地绑定参数）的前提下**，
两台机器做不出真正的跨子网样本。**本次验收不覆盖场景 4。**

它目前的覆盖来自自动化测试，不是真机：

| 层次 | 位置 |
| --- | --- |
| 私有地址 / 掩码判定（含 `172.100` 这个陷阱） | `LanRemote.Protocol.Tests` |
| `SubnetPolicy` 行为 | M2 单元测试 |
| 「同子网校验收到的必须是**接受连接的那个**本地地址」 | `TransportHostTests.Subnet_Check_Receives_The_Accepting_Listener_Address` |

如果你确实想在真机上补这一条，需要给我一个第三子网（比如让 A 只留 `172.100.166.x`、
B 只留 `192.168.1.20`，并且中间有路由能通），我再把场景 4 加回必做清单。

---

## 6. 请把这些贴回来

判定 M3 是否通过需要下面四段，缺一段我就只能写"未运行"：

1. 两台机器的 `info` 输出（确认都 `PASS`，并给出各自 `deviceCode` / `certSha256`）；
2. 机器 B 的 `host` **完整**控制台输出（含最后的汇总行）；
3. 机器 A 的 `run-acceptance.ps1` 输出（含汇总表）；
4. `%TEMP%\lanremote-m3-acceptance\` 下的三个 `client-*.txt`（失败时尤其需要）。

---

## 7. 失败时先查这些

| 现象 | 原因 |
| --- | --- |
| `reason=no-qualified-rfc1918-nic` | 没跑 `set-lab-ip.ps1`，或跑完没生效；`ipconfig` 复核 |
| `reason=peer-not-found deviceCode=…` | A 没发现 B。查 UDP 45872 入站规则、两台是否同一广播域、B 的 `host` 是否还活着 |
| `reason=peer-port-unreachable` | B 的 TCP 45873 连不上。`host` 没起？防火墙拦了？地址写错？ |
| `reason=missing --device-code` | 命令行少参数 |
| `success` 却报 `outcome=FAIL` | 真的不合格，把 `client-success.txt` 整段发我 |
| `host` 打印 `bind 失败 <地址>: …` | 该地址已被占用；其它地址不受影响（ADR-031 按网卡降级） |

---

## 8. 验收后

```powershell
powershell -ExecutionPolicy Bypass -File .\set-lab-ip.ps1 -Undo
```

两台都要跑。它会删掉 lab 地址、把网卡还原成 DHCP 并 `ipconfig /renew`，
最后自校验；如果发现只剩 APIPA `169.254.x.x` 会直接告诉你手工修复的三条命令。

> 注意：`netsh` 的退出码不可信。已经是 DHCP 时
> `netsh interface ipv4 set address source=dhcp` 会输出"已在此接口上启用 DHCP。"
> 却返回**非 0**。所以脚本不看退出码，只看 `Get-NetIPInterface` 的实际状态。
