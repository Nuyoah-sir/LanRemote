# LanRemote M3 两机验收手册

> 里程碑：**M3 — TLS Host/Client + 同子网校验**
> 版本：`0.1.0-m2`
> 物料：`LanRemote-0.1.0-m2-m3-acceptance-win-x64.zip`
> 日期：2026-09-21（本轮按第二轮外部评审「先修再跑」重写：场景从 3 个变 4 个，
> 判定改为**交叉核对**、退出码从 3 类变 5 类，并新增 headless 入口）

---

## 0. 先说清楚：这个包里是什么，不是什么

这个包**不是** LanRemote 客户端。它装的是 `LanRemote.Acceptance.exe`，一个验收器。

原因是：`LanRemote.App`（WPF 客户端）目前只是**引用**了 `LanRemote.Transport`，
**一行都没有调用**。在两个机器上跑 App，只能证明 M2 的 discovery 还能用，
碰不到 M3 新写的 TLS / pinning / `channel_hello` 的任何一行代码。

所以 M3 的验收必须通过这个验收器来做。它能真实地：

- 在合格网卡上 listen，走 `accept → 同子网校验 → 准入限额 → TLS` 这条真链路；
- 用自签名 ECDSA P-256 证书做 TLS，靠 **证书指纹 pinning**（不是 CA 校验）认对端；
- 收一帧 `channel_hello` 并严格解析，然后到达显式状态 `PreAuthenticated` 并干净关闭。

> 端口：UDP **45872**（发现）、TCP **45873**（控制通道）。控制与视频是两条独立 TLS 连接
> （ADR-003），M3 只做控制这一条。

---

## 0.1 怎么启动 —— 双击 `LanRemote.Acceptance.exe`，就这样

**双击 `LanRemote.Acceptance.exe`，会出现一个窗口。** 没有脚本，没有命令行，
不用开 PowerShell。

```
LanRemote.Acceptance.exe   <-- 双击这个，其余全在窗口里点
START-HERE.md              本手册
set-lab-ip.ps1             配 lab 网段（唯一还需要管理员的步骤）
...其余 200+ 个是 .NET 自包含运行时的 dll
```

窗口从上到下四块：

| 区域 | 作用 |
| --- | --- |
| **本机身份与网络** | 自动检测。显示设备码、证书指纹、监听地址、以及「能不能参与两机验收」 |
| **这台机器的角色** | 两组按钮：`本机作为被控端（开始监听）` / `本机作为控制端（跑全部场景）`。**选定一组后另一组禁用**，直到该任务结束——两机验收里角色混淆过一次就会得到一批看不懂的证据 |
| **日志** | 全部证据。**整段拷走** |
| **底部** | `复制全部日志` / `打开日志目录`。没有「清空日志」——验收仪器不该允许选择性抹除证据 |

打开时自动跑一次环境自检（等价于 `--headless info`）。如果本机没有合格网卡，
"状态"那一行会直接告诉你该跑哪条命令，两个角色按钮会被**禁用**——
所以不会出现"点了没反应、不知道为什么"的情况。

> **日志每次运行都是一个新文件**，文件名形如
> `%TEMP%\lanremote-m3-acceptance\m3-client-20260921-083156-63edc1ef.log`，
> 不可变、关窗口也不丢。`复制全部日志` 会整段进剪贴板。

### 0.2 也可以用命令行跑（脚本/自动化用）

**同一个 exe**，带 `--headless` 就是命令行模式：

```
LanRemote.Acceptance.exe --headless info
LanRemote.Acceptance.exe --headless host [--seconds N]
LanRemote.Acceptance.exe --headless client --peer <设备码> [--scenario <场景>]... [--all]
LanRemote.Acceptance.exe --headless client --address <IP> --pin <指纹> [--port N] [--all]
```

两条路调用的是**完全相同**的 `HostRole` / `ClientRole`，不存在「脚本跑的是另一套逻辑」。

退出码：

| 码 | 含义 |
| --- | --- |
| 0 | PASS —— 符合预期 |
| 1 | FAIL —— 真的不符合预期 |
| 2 | UNMET —— 前置条件不满足（没合格网卡 / 对端没跑 / 端口没开），**不是**产品失败 |
| 3 | HARNESS_ERROR —— 验收器自身故障（参数写错、内部异常） |
| 4 | INVALID_RUN —— 操作员中途点了停止，整轮作废，**不得**把由此产生的关闭算成 PASS |

> `--address` + `--pin` 是**直连模式**：跳过发现直接连，用于「本机自检」
> （一个环回上的假被控端就能把全部判据走一遍）。
> 此模式下 `--pin` 的语义是**你声称的对端真指纹**；
> `pin-mismatch` 场景的近失指纹由验收器**自己翻转一位**生成，你不要手动给个错的。

---

## 1. 前置条件

| # | 条件 | 怎么确认 |
| --- | --- | --- |
| 1 | 两台 Windows 机器，接在同一个交换机 / 同一根线上 | `ipconfig` 能看到同一段地址 |
| 2 | **两台机器都必须在 RFC1918 私有网段**（`10/8`、`172.16–172.31`、`192.168/16`） | 窗口里"监听地址"不是 `(无合格 RFC1918 网卡)` |
| 3 | 管理员 PowerShell（**只有配 IP 那一步需要**，验收本身不需要） | 标题栏带"管理员" |
| 4 | 入站 **UDP 45872 + TCP 45873** 放行 | `set-lab-ip.ps1` 会顺手建两条规则 |

### 1.1 为什么第 2 条最容易踩

LanRemote **故意**只认 RFC1918。你这两台实机在 `172.100.166.x` 上，
而 `172` 段**只覆盖到 `172.16`–`172.31`**，`172.100` **不是私有地址**。
所以不做任何配置的话，LanRemote 会正确地拒绝它们，设备列表是空的——
这是**对的**，不是 bug。（判定私有必须按数值区间，不能拿 `172.` 当前缀匹配。）

窗口会直说这件事：

```
[INFO][RESULT] outcome=FAIL reason=no-qualified-rfc1918-nic // 先用 set-lab-ip.ps1 配置 lab 网段
```

### 1.2 配 lab 网段（每台机器跑一次，角色不同）

这是**唯一**需要终端的步骤，因为它要 UAC 提权、要改本机网络配置——
按项目约定这类动作**绝不能静默执行**，必须由你显式触发。

在**管理员** PowerShell 里，把 zip 解压后的目录当成当前目录：

```powershell
# 机器 A（控制端，待会儿在窗口里点"控制端"）
powershell -ExecutionPolicy Bypass -File .\set-lab-ip.ps1 -Role A

# 机器 B（被控端，待会儿在窗口里点"被控端"）
powershell -ExecutionPolicy Bypass -File .\set-lab-ip.ps1 -Role B
```

结果：A 得到 `192.168.1.10/24`，B 得到 `192.168.1.20/24`，
**同时保留原来的 `172.100.166.x` 不断网**；网络配置文件被设为「专用」，
并建两条入站规则：`LanRemote Discovery UDP 45872` 与 `LanRemote Control TCP 45873`。

> 早先版本的脚本**只放行 UDP 45872**。那对 M2.1 的发现验收够用，对 M3 **不够**：
> 被控端会静默丢掉 B 的入站 SYN，B 那边只看到"连接被拒/超时"，而且没有任何线索
> 指向防火墙。现在两条都建；脚本可**重复运行**，缺哪条补哪条（地址已是 lab 地址时
> 也会照样检查防火墙）。`-Undo` 会把这两条规则一起删掉。

> 这个脚本为什么是"先整口切静态再追加"？因为 Windows 一张网卡**只能二选一**：
> DHCP 或静态，不能共存。直接给 DHCP 接口追加地址会把接口翻成 `Dhcp=Disabled`
> 并把租约丢掉——这台机器已经因此断网两次了。脚本的作法是先把当前这一套
> IP/掩码/网关/DNS 原样切成静态（不断网），再追加第二个地址（不带网关）。

配完后**重开一次窗口程序**（或点一下窗口重新触发自检），
"状态"应该变成绿色的 **可以参与两机验收**，两个角色按钮解禁。

---

## 2. 验收流程

### 第 1 步：两台机器各自看窗口

双击 `LanRemote.Acceptance.exe`，确认：

- 状态 = `可以参与两机验收`（绿色）；
- 记下 `设备码`（形如 `M5WC-14GX`）——**它不是秘密**，可以贴在聊天里；
- 记下 `证书指纹`（64 位十六进制）——这是要被 pin 的那个值。

### 第 2 步：机器 B 起被控端

在 B 的窗口里点 **`本机作为被控端（开始监听）`**。

日志会打印设备码、监听地址，然后**一直等下去**，直到你点 `停止监听`。
**这个窗口不要关。**

### 第 3 步：机器 A 跑全部场景

在 A 的窗口里：

1. 在 `对端设备码` 里填 **机器 B 的设备码**（形如 `M5WC-14GX`）；
2. 点 **`本机作为控制端（跑全部场景）`**。

它会依次跑 `success` → `pin-mismatch` → `timeout` → `slow-dribble`，
每个场景单独一段日志，末尾给出**两机交叉核对清单**（§4 的那五条约束），
窗口顶部的结论横幅会写 `x/4 个符合预期`。

> 中途想停就点 `中止`。**中止会把整轮标记成「本轮作废」**，
> 由此产生的连接关闭**不会**被翻译成场景 PASS——重跑一次即可。

### 第 4 步：收尾

- 机器 B 上点 `停止监听`，它的日志会打印最后那一行汇总（见 §4）；
- 两台各自点 `复制全部日志`，贴回给我；
- 两台机器各跑一次 `set-lab-ip.ps1 -Undo`。

---

## 3. 四个必做场景与判定标准

退出码含义见 §0.2。窗口里会把它翻译成人话：
`符合预期` / `不符合预期` / `前置条件不满足` / `验收器故障` / `本轮作废`。

> `2` 和 `1` 必须分清。`2` 是"没测成"，不是"测出来不合格"。
> 看到"前置条件不满足"先去解决环境问题，不要当成 M3 的缺陷。

> **控制端不会宣布里程碑通过。** 它的每个场景都以
> `hostEvidence=REQUIRED` + `hostExpect="…"` 结尾，并在最后打
> `[VERDICT] M3 = PENDING-HOST-EVIDENCE`。判定必须**把两台机器的日志放一起对**，
> 方法在 §5。

### 场景 1：success（必做）

证明：真机上 TLS 握手成功、证书指纹 pinning 通过、`channel_hello` 被严格解析接受、
服务端干净关闭。

**通过标准（两端都要看）**

机器 A：
```
[CLIENT] tls         = ok proto=Tls13
[CLIENT] presentedPin= <与 discovery 广播的 pin 逐字符一致>
[CLIENT][CORRELATE] local=192.168.1.10:53144 peerHost=192.168.1.20:45873 presentedPin=…
[CLIENT] hello sent  = t=8 ms
[CLIENT] peerClosed  = eof t=54 ms // 读到有序结束（TLS 记录层 EOF）
[CLIENT][RESULT] scenario=success clientOutcome=PASS … hostExpect="outcome=PreAuthenticated rejection=-"
```

机器 B：
```
[HOST][RESULT] conn#1 peer=192.168.1.10:53144 outcome=PreAuthenticated rejection=-
```

**逐字符核对 `presentedPin`**——它是 ADR-028 要交给 M4 绑进 transcript 的那个值。
另外核对被控端那一行的 `peer=` 端口是否等于控制端的 `local=` 端口（§5 的配对方法）。

> ⚠ 控制端能观测到的只有「**对端没等时限就收尾了**」这一件事。
> 「hello 被接受」在控制端**不可观测**，必须由被控端那行 `outcome=PreAuthenticated` 证。
> 所以 `success` 的 PASS **只在两份日志对上之后才成立**。

### 场景 2：pin-mismatch（必做）

证明：指纹不符时握手必须被拒，且**失败前不会有任何应用数据被接受**。

工具会把期望指纹换成**真指纹翻转 1 位**的近失值（不是 `000…001` 那种无关串）：
```
[CLIENT] expectedPin = <真指纹>
[CLIENT] wrongPin    = <只差 1 位>
[CLIENT] 改动幅度    = byte[16] ^ 0x01——256 位里只差 1 位，用于证明判定是逐字节比较
[CLIENT][RESULT] scenario=pin-mismatch clientOutcome=PASS handshake=…AuthenticationException rejection=pin-mismatch
```

**通过标准**：抛的是 `AuthenticationException`，**且**异常消息里的结构化短码**恰是**
`pin-mismatch`。只判「抛了认证异常」是不行的——证书形状不对、缺证书、过期都会抛同一个类型，
那样等于没测到 pinning 那一行。

> **早就没有 TCP 探针了。** 原先有个 `[CLIENT] tcpProbe = open` 行用来先证明端口开着；
> 它污染被控端计数、制造 TIME_WAIT，而且不证明 pinning 跑过。删掉它的依据是**本机实测**：
> TCP 层连不上时抛的是 `SocketException`/`IOException`，**永不**是 `AuthenticationException`，
> 所以上面那条断言本身就排除了「端口没开」。

机器 B 这边**可能有一行、也可能没有**，两种都正常：

- **TLS 1.3**：服务端在收到客户端的 alert 之前就已经认为握手完成 → 照样进会话处理器，
  读到 EOF 后给出 `rejection=pre-auth-eof`（**本机环回实测值**）；
- **TLS 1.2**：服务端握手直接失败 → 完全不留行。

**但绝不能出现 `outcome=PreAuthenticated`。** 出现了就说明 pinning 没拦住，是严重缺陷。

### 场景 3：timeout（必做）

证明：连上 TLS 却一直不发 hello 的对端，会被**绝对时限**切断。

时限：连接 3s / 握手 5s / 长度前缀 **5s** / 载荷 10s / hello 5s。
这里卡的是长度前缀那一段（客户端一个字节都不发），所以大约在 **5 秒**后被切。

**通过标准**

机器 A：
```
[CLIENT] 故意不发 hello，等服务端按 pre-auth 绝对时限切断……
[CLIENT] peerClosed  = eof t=5002 ms
[CLIENT][RESULT] scenario=timeout clientOutcome=PASS … windowMinMs=3000 windowMaxMs=12000 hostExpect="rejection=pre-auth-timeout"
```

机器 B：
```
[HOST][RESULT] conn#N peer=… outcome=Rejected rejection=pre-auth-timeout
```

### 场景 4：slow-dribble（必做 —— **它才是「绝对时限」的唯一证据**）

上面那个 `timeout` 场景证明不了它声称证明的东西：**一个「每读到字节就重置」的
空闲超时同样会在约 5 s 断开并 PASS**，两者在控制端看起来一模一样。

本场景在长度前缀阶段按 2 s 间隔**逐字节**滴流：deadline = 5 s 时只能发出 3 个字节
（第 4 个要等到 6 s，已经超时）。要求服务端**仍然从阶段进入时刻起算**约 5 s 切断。

**通过标准**

```
[CLIENT] 慢滴长度前缀 00000039（hello 共 57 字节），每 2 秒发 1 字节
[CLIENT] 已发 1/4 字节 t=2 ms
[CLIENT] 已发 2/4 字节 t=2002 ms
[CLIENT] 已发 3/4 字节 t=4011 ms
[CLIENT] peerClosed  = eof sent=3/4 t=4996 ms
[CLIENT][RESULT] scenario=slow-dribble clientOutcome=PASS … windowMinMs=3000 windowMaxMs=9000 hostExpect="rejection=pre-auth-timeout"
```

关键看 **`sent=3/4`**：如果显示 `4/4`，说明时限被逐字节重置了，是**真缺陷**。

> 这条判据是**变异验证过的**：把假被控端的时限实现改成「可重置」之后，
> `slow-dribble` 变红（`sent=4/4`，16054 ms）、而 `timeout` **仍然绿**——
> 正好证明旧判据对这类实现是**空的**。

### 场景 5：cross-subnet（**本次不跑**，见 §6.2）

---

## 4. 被控端结束时的汇总与「互斥终态桶」

被控端点 `停止监听` 后会打印：

```
[HOST][BUCKETS] sessionHandled=N active=0 completedTrue=… completedFalse=… partitionOk=True
[HOST][SUMMARY] connectionsEnteringSessionHandler=N listenersStoppedCleanly=True activeAtStop=0 handlerFaults=0
[HOST][UNOBSERVED] tlsStageRejections=UNOBSERVABLE // 同子网拒绝 / 准入拒绝 / TLS 失败全部静默 return（§5.1）
[HOST][CORRELATE] hostDeviceId=… hostDeviceCode=… hostCertSha256=… boundAddresses="…" port=45873
```

`accepted / preAuthenticated / rejected` 那三个旧数字已经**废弃**：

> 它们**语义重叠、不是一个划分**——`accepted` 是「进了会话处理器」，
> `preAuthenticated` 是它的子集，`rejected` 又是另一个方向上的切法。
> 三个数各自都能对上、合起来却会对不上，于是核对变成猜。
> 现在改成**互斥终态桶**：`Σ终态 == sessionHandled`（`partitionOk=True`）且 `activeAtStop=0`。
> `cleanStop` 也改名为 `listenersStoppedCleanly`——它说的是「监听器干净停了」，
> 不是「这一轮验收干净成功了」，旧名字会被误读。

跑完四个场景后，**被控端应当满足**（这就是控制端日志末尾那份核对清单的内容）：

| # | 约束 |
| --- | --- |
| ① | `outcome=PreAuthenticated` 的行**恰好 1 条**（只有 `success` 该走到这） |
| ② | `rejection=pre-auth-timeout` 的行**恰好 2 条**（`timeout` + `slow-dribble` 各一条） |
| ③ | 其余任何一行都**不得**是 `PreAuthenticated`，也不得是 `pre-auth-timeout` |
| ④ | `connectionsEnteringSessionHandler` 落在 **3..4** |
| ⑤ | `listenersStoppedCleanly=True` 且 `activeAtStop=0` |

**④ 为什么是一个区间而不是一个数**——这一条是被实测纠正过的，不要「优化」成 3：

我原先断言「`pin-mismatch` 死在 TLS 阶段、服务端不会留行，所以应为 3（4 减 1）」。
**实测是 4。** 客户端拒绝服务端证书时发的是 TLS alert，而 **TLS 1.3 下服务端在收到该 alert
之前就已经认为握手完成**，于是它照样进了会话处理器、读到 EOF 后给出 `rejection=pre-auth-eof`。
只有 TLS 1.2 下服务端握手会直接失败、才真的不留行。

这就是「从症状推断因果」的典型错误：从「客户端看到握手失败」推出「服务端没进会话」，
中间那一步（TLS 1.3 的半开窗口）我先入为主地跳过了。
所以现在只写**可证伪的约束**，不再写一个猜出来的数字。

---

## 5. 怎么把两份日志对起来（配对方法）

**不要靠「计数相等」来配对。** `accepted` 相等不代表「被计入的就是这几个场景」。

用 **4 元组**：

- 控制端每个场景都会打一行
  `[CLIENT][CORRELATE] local=<本机IP>:<临时端口> peerHost=<对端IP>:<端口> presentedPin=…`；
- 被控端每条连接的结束行是
  `[HOST][RESULT] conn#N peer=<同一个端点> outcome=… rejection=…`。

把 `local=192.168.1.10:53144` 和 `peer=192.168.1.10:53144` 对起来，
就能**唯一**配出一条连接，而不是「大概是这几条」。

> 万一 `local` 显示 `unavailable`（socket 已被内核拆掉，拿不到端点），
> 才退回按时间顺序配，并**必须在记录里注明是这么配的**。

---

## 6. 两个已知缺口（不是 bug，但要知道）

### 6.1 `TransportHost` 目前完全没有 logger

同子网校验失败、准入限额拒绝、TLS 握手失败，这三条都是**静默 `return`**，
一行日志都不会有。所以：

- 场景 2（`pin-mismatch`）在被控端**可能有一行、也可能没有**（见 §3 场景 2）；
- 场景 5（跨子网）在被控端**一定没有行**。

被控端自己也把这件事写成了 `[HOST][UNOBSERVED] tlsStageRejections=UNOBSERVABLE`——
**不填 0**。填 0 就等于宣称「我们观测到了零次」，而真相是「我们根本观测不到」。

这在 M9 会被 ADR-024（网络诊断进 UI）正面解决；在那之前，
两机验收的证据就以**控制端为主、被控端为辅**。

### 6.2 场景 5（跨子网）在这套 lab 环境里跑不出来

要真正触发同子网拒绝，需要**控制端到被控端监听地址的包，源 IP 落在另一个子网**。

现在的 lab 配置是两台机器同时挂着 `172.100.166.x` 和 `192.168.1.x`。
host 只在 RFC1918 绑定上 listen，也就是只听 `192.168.1.20`；
而 A 发往 `192.168.1.20` 的包，Windows 会自动挑源地址 `192.168.1.10`——
**同一个子网**，于是闸门正确地放行，测不到拒绝那一支。

Windows 的 `New-NetRoute` / `route add` 都不能指定源地址，
所以**在不改产品代码（给 `TlsClientConnector` 加本地绑定参数）的前提下**，
两台机器做不出真正的跨子网样本。**本次验收不覆盖场景 5。**

它目前的覆盖来自自动化测试，不是真机：

| 层次 | 位置 |
| --- | --- |
| 私有地址 / 掩码判定（含 `172.100` 这个陷阱） | `LanRemote.Protocol.Tests` |
| `SubnetPolicy` 行为 | M2 单元测试 |
| 「同子网校验收到的必须是**接受连接的那个**本地地址」 | `TransportHostTests.Subnet_Check_Receives_The_Accepting_Listener_Address` |

如果你确实想在真机上补这一条，需要给我一个第三子网（比如让 A 只留 `172.100.166.x`、
B 只留 `192.168.1.20`，并且中间有路由能通），我再把场景 5 加回必做清单。

---

## 7. 请把这些贴回来

判定 M3 是否通过需要下面几段，缺一段我就只能写"未运行"：

1. **两台机器**窗口顶部那块身份面板的截图或抄写（确认状态合格，并给出各自设备码 / 证书指纹）；
2. 机器 B 的**完整**日志（含 `[HOST][SUMMARY]` 与 `[HOST][CORRELATE]` 两行）；
3. 机器 A 的**完整**日志（含末尾那份「两机交叉核对」清单）；
4. 如果哪个场景不符合预期，把那一整段原样贴我，不要只贴最后一行。

> 日志文件名形如 `%TEMP%\lanremote-m3-acceptance\m3-client-<UTC>-<runId>.log`，
> **每次运行一个新文件、不可变**。`打开日志目录` 一键可达。

---

## 8. 失败时先查这些

| 现象 | 原因 |
| --- | --- |
| 状态红字 `no-qualified-rfc1918-nic` | 没跑 `set-lab-ip.ps1`，或跑完没生效；`ipconfig` 复核 |
| 两个角色按钮是灰的 | 同上——这是**故意的**，防止点了没反应 |
| `reason=peer-not-found deviceCode=…` | A 没发现 B。查 UDP 45872 入站规则、两台是否同一广播域、B 的窗口还在不在监听 |
| `clientOutcome=UNMET handshake=…SocketException stage=tcp` | B 的 TCP 45873 连不上。被控端没点开始？**TCP 45873 入站规则缺失**（重跑一次 `set-lab-ip.ps1` 就会补上）？别的安全软件拦了？地址填错？**这是"没测成"，不是"测出来不合格"** |
| `clientOutcome=FAIL` 且 `peerClosed=reset` | 收尾方式不是有序 EOF。若同时 `closedAtMs` 贴着 5000，说明 hello 根本没被接受 |
| `slow-dribble` 显示 `sent=4/4` | 长度前缀时限被逐字节重置了——**真缺陷**，把两段日志都贴我 |
| `② rejection=pre-auth-timeout 的行恰好 2 条` 对不上 | 某个场景提前/推迟收尾，看 `closedAtMs` 是否贴在 5000 |
| 整轮结局是 `本轮作废（INVALID_RUN）` | 你中途点了 `中止`。这是故意的：操作员中止产生的关闭**不许**被翻译成场景 PASS。重跑一次即可 |
| 退 3 / `验收器故障` | 参数写错或验收器自身异常。看 `%TEMP%\lanremote-m3-acceptance\crash.log` |
| `bind 失败 <地址>: …` | 该地址已被占用；其它地址不受影响（ADR-031 按网卡降级） |
| 窗口根本打不开 | 极少见。看 `%TEMP%\lanremote-m3-acceptance\crash.log`，整段贴我 |

---

## 9. 验收后

```powershell
powershell -ExecutionPolicy Bypass -File .\set-lab-ip.ps1 -Undo
```

两台都要跑。它会删掉 lab 地址、把网卡还原成 DHCP 并 `ipconfig /renew`，
最后自校验；如果发现只剩 APIPA `169.254.x.x` 会直接告诉你手工修复的三条命令。

> 注意：`netsh` 的退出码不可信。已经是 DHCP 时
> `netsh interface ipv4 set address source=dhcp` 会输出"已在此接口上启用 DHCP。"
> 却返回**非 0**。所以脚本不看退出码，只看 `Get-NetIPInterface` 的实际状态。
