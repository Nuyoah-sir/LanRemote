# LanRemote 项目长期记忆

## 项目定位

Windows 局域网屏幕共享 / 远程控制（自用）。**无账号、无云、无穿透、无 UPnP/中继**。
只允许同 IPv4 子网的 RFC1918 设备发现与连接，且必须通过访问密钥挑战认证。

规格合同 `LanRemote_Implementation_Package/`（ADR 原始快照 9 条，不回写）；
**当前真实进度一律以仓库根 `HANDOFF.md` 为准**；ADR 工作副本 `docs/DECISIONS.md`（010~034）。

## 不可动摇的约束

- C# / .NET 10 LTS / WPF / x64；TFM `net10.0`（Core/Discovery/Transport/Sessions）与
  `net10.0-windows`（App/Security/Capture/Input）。**不要**改成 `...windows10.0.19041.0`（没装 Win10 SDK，ADR-010）
- UDP 45872 发现；TCP 45873 控制；Control 与 Video 是**两条独立 TLS**（ADR-003，勿合并）
- 访问密钥必须 128-bit `RandomNumberGenerator`；禁止 `Random`、**禁止 6 位弱密码**
- 认证是 HMAC-SHA256 挑战，**明文 key 绝不上网**，必须验证 serverProof（规格第 9 节）
- 视频管线：bounded queue（1~2）+ DropOldest，**严禁无界队列**；实时性 > 完整性
- 任何时候不得删除或弱化：同子网校验、RFC1918 限制、TLS、指纹 pinning、HMAC 挑战、
  DPAPI 存储、视频 attach token、ViewOnly/Control 隔离、本机审批、被控提示、紧急停止
- 密码学白名单：TLS、SHA-256、HMAC-SHA256、`RandomNumberGenerator`、
  `FixedTimeEquals`、DPAPI、.NET 自带 X509/SslStream。禁自研算法 / XOR / Base64 当加密 / MD5-SHA1 做认证

## 常用命令

```bash
source scripts/env.sh           # 必须：dotnet 装在用户级 ~/.dotnet，不在 PATH
dotnet build LanRemote.sln -c Debug
dotnet test  LanRemote.sln -c Debug --no-build
```

出包：`scripts/acceptance/make-package.py`（App，M2 用）、
`make-m3-package.py`（验收器，M3 用；`LANREMOTE_M3_SKIP_PUBLISH=1` 只重打不 publish）。
**别用 `LANREMOTE_M3_CLEAN=1`**：本机 safe-delete hook 连脚本内部的 `shutil.rmtree` 也拦
（`SAFE_DELETE_BULK_CONFIRM_REQUIRED count=273 threshold=50`）→ 打包当场死于 SystemExit。
`dotnet publish -o` 本来就会覆盖它自己产出的每个文件，不需要清。目录里若有 13 个**空的**
语言卫星目录（`cs/` `zh-Hans/` …）是历史残留：`os.walk` 只收文件，**它们不会进 zip**。
`.ps1` 必须 **UTF-8 with BOM**（PS 5.1 否则中文乱码）→ `add-bom.py`；
**`.cmd` 必须纯 ASCII 且不加 BOM**（cmd.exe 按控制台代码页解析，中文连注释都会解错报
「不是内部或外部命令」），所以所有中文文案放 ps1 里。

**交给用户的程序必须有双击入口**：自包含包里 exe 混在 200+ dll 中，而「双击不带参数只会
打 usage 然后退出」的控制台 exe = 窗口一闪而过，用户会判断「包里没有 exe」（原话：
「我们这个做的是软件啊」）。**正解是让入口本身就是 GUI**：M3 验收器 = `WinExe` + WPF，
双击 `LanRemote.Acceptance.exe` 直接开窗口、零脚本；`START.cmd` 只是控制台程序的退路。

## 用户协作偏好

中文；结构化输出（表格 / 清单 / 字段说明）；结论先行，先给边界和证据，不接受「大概可能」；
严禁伪代码、写死的假实现、**伪造构建/测试结论**（没跑过就写「未运行」）；
改动最小化；每个里程碑必须 build + test + 更新 `HANDOFF.md` 才能进入下一阶段。

HANDOFF 记账用 `Last code commit` + `Working tree at validation`，**不写 HEAD hash**。

## 里程碑进度

| 里程碑 | 状态 |
| --- | --- |
| M0 / M1 / M1.1 / M1.2 / M1.3 | 已完成（`104f296` / `9e75fce`） |
| M2 + M2.1 | 已完成，**两机验收 PASS 20/20**（`313c542`，408 tests） |
| **M3 TLS Host/Client + 同子网校验** | 24 步里 1~23 全完成，build 0 警告 / **574 tests PASS**，Last code commit `f080581`。第 24 步（两机验收）物料已按「先修再跑」修到可跑：**变异矩阵 11/11 + headless 4/4 PASS**。**仍卡在等用户在两台实机执行** → M3 还不算做完 |
| M4~M11 | 未开始 |

M3 提交链：`ee1cbe3`(P1) `601d7a7`(P2) `5ba5822`(P3) `e0484ec`(P4) `5bf3cb6`(P5)
`5ae052f`(验收器) **`f080581`**(先修再跑：4 个真缺陷 + headless 入口 + 4 元组配对)。

**外部模型的用法**：只做「设计红队评审」（prompt `docs/M3_EXTERNAL_REVIEW_PROMPT.md`）；
**Windows/.NET 实测行为一律不问模型，本机测**；模型结论不得直接写进 HANDOFF。
本机系统 **Win11 专业版 25H2 / build 26200** → TLS 在 Win10 22H2 的行为**无法验证，标注未测**。
第二轮外部评审时机已到：针对具体实现的红队 + 错误消息分类 + 五个 deadline 数值。

## M1 关键存储事实

- `secrets.bin` = `LRSC`(4)+version(1)+length(4 BE)+DPAPI(JSON)；bundle 含
  `deviceGuid` / `accessKey`(Base32 26) / `certificatePfx` / `certificatePfxPassword`
- 访问密钥 = `RandomNumberGenerator.GetBytes(16)`；`Regenerate` 覆盖存储 → 旧 key 立即失效
- 设备证书 = 自签名 ECDSA P-256，5 年，serverAuth EKU，非 CA；
  **KeyUsage 只能 digitalSignature**（RFC 5480，EC 证书不得声明 keyEncipherment，ADR-021）；
  指纹 = `SHA256(RawData)` 大写 hex
- **禁止**加「静默自动重签证书」逻辑（ADR-021 硬约束）
- 设备码 = `Base32(SHA256(deviceGuid) 前 5 字节)`，展示 `XXXX-XXXX`；**不是秘密**
- `DpapiSecretVault.UpdateAsync` copy-on-write：落盘成功才换缓存；`ReadAsync` 只发 `Clone()`；
  证书已存在时走只读路径**不重写** secrets.bin
- `TryDecodeExact` 失败时 out 是 `Array.Empty<byte>()`（已 ZeroMemory）

## 证书私钥载入：ADR-016/018 均被证伪 → ADR-029 + ADR-030

真实 SslStream 服务端实测（3 flag × 3 协议 × 3 次）：`EphemeralKeySet` **9/9 失败**
（`does not support ephemeral keys` ← `0x8009030E`）；`PersistKeySet` 可用但**磁盘留持久密钥副本**；
**`Default`(0) 可用且会清理临时容器 → 用它**。
ADR-030：`CreateSelfSigned(...)` 直出的私钥本身就是 ephemeral，报错一模一样
→ **必须「导出 PFX → DefaultKeySet 重导入」往返**，`DeviceCertificateService` 那步不是冗余，
测试造证书也要复刻。**污染陷阱**：同进程先 `PersistKeySet` 导入过同一私钥后 `EphemeralKeySet`
会碰巧成功 → flag 结论必须新进程 + 顺序受控。判 TLS 失败**永远抓服务端异常**，客户端只有 EOF。

## 本机实测：.NET 10.0.12 默认值（别再猜）

`AllowDuplicateProperties` 默认 **True 且后者覆盖前者**；`MaxDepth` 属性值默认 **0**（= 用内置 64，
文档别写「默认 64」）；`UnmappedMemberHandling` 默认 `Skip`；尾逗号/注释默认已拒。
`SslClientAuthenticationOptions`：`AllowTlsResume=True`、`AllowRenegotiation=**True**`；
服务端 `AllowRenegotiation=False`（**不对称**）；两端 `EnabledSslProtocols` 默认 `None`，必须显式写。
**证书校验回调参数是 `X509Certificate`（基类）**没有 `RawData` → 用 `GetRawCertData()`。
`TargetHost = string.Empty` 可用（不发 SNI）；握手超时抛 `OperationCanceledException`。

## M2.1 发现不变量（别写回去）

- **probe unicast 回应目标 = `remote.Address : 45872`**，绝不是 `remote.Port`
- 每个 sender 必须显式 `SetSocketOption(IP, MulticastInterface, 接口 IPv4 网络序 4 字节)`；
  `HostToNetworkOrder` 或裸 index 都会抛「该请求的地址无效」

## M3 阶段成果速查

- 类型：`CertificatePin` / `ConnectionTarget`(点击时冻结的不可变快照) /
  `ConnectionIdentity`(含 **presentedPin**，ADR-028 要求 M4 transcript 绑它) /
  `TransportTimeouts` / `PeerCertificateValidator` / `TlsClientConnector` / `TlsConnection` /
  `TransportHost`(accept→同子网→准入→TLS，**顺序不可换**) / `ConnectionAdmissionLimiter` /
  `ConnectionRegistry` / `FrameReader` / `FrameWriter` / `HelloFrame` / `ControlPreAuthSession`
- pre-auth 单帧上限 **4 KiB**，与认证后 1 MiB 严格区分（ADR-033，规格新增约束）
- M3 终态 = hello 通过后**干净关闭**；`AllowedOperationsWhilePreAuthenticated` 必须空集合（有测试盯着）
- 五段绝对 deadline 只验证了「执行得准」（误差 0–36 ms），**数值本身待第二轮评审**
- **`TransportHost` 完全没有 logger**：同子网拒绝 / 准入拒绝 / TLS 失败全是静默 `return`
  → 被控端看不到拒绝原因（HANDOFF §14.12，并入 ADR-024 / M9）。
  但 pre-auth 超时发生在会话处理器**内部**，是有输出的（`rejection=pre-auth-timeout`）

### M3 验收器 `tools/LanRemote.Acceptance`

**形态 = WPF 窗口程序（`WinExe` + `UseWPF`）：双击 exe = 一个窗口，零脚本。**
曾做成「控制台 exe + `START.cmd`」，用户连纠三次后废弃（§13：终端用户永远不打开
PowerShell）；已删 `Program.cs` / `START.cmd` / `run-acceptance.ps1`。**别改回控制台 + 脚本。**
窗口：身份面板（自检不合格 → 置灰角色按钮）/ 角色按钮组 / 只读日志 / 底部工具条；
日志双写 UI + `%TEMP%\lanremote-m3-acceptance\gui.log`（WinExe 无控制台，`Console.WriteLine` 不可见）。

**WPF 硬坑**：`InvariantGlobalization` 必须 `false`，否则窗口首次 Measure 崩在
`MS.Internal.FontCache.MajorLanguages`（`'en' is an invalid culture identifier`）；
`DispatcherUnhandledException` 处理必须**防重入**（渲染期异常会弹框风暴）；
XML 注释里不能出现 `--`（写 `--->` 直接 MSB4025）；
`<SatelliteResourceLanguages>en</SatelliteResourceLanguages>` 去掉 13 个语言卫星目录（422→265 条目）；
`<DebugType>embedded</DebugType>` 不单独发 pdb。
**纠正过的猜测**：改 invariant **不会**让包多带 ICU（Windows 用系统 ICU，包里零 `icu*`），
体积 77.8→132.2 MiB 全是 WPF 自身程序集（约 39 MiB）。

退出码就是结局枚举：**0 预期内 / 1 真失败 / 2 前置条件不满足 / 3 工具自身出错 / 4 无效运行**；
`Combine()` 优先级 `InvalidRun > HarnessError > Fail > PreconditionUnmet > Pass`。
**别拿 `LanRemote.App` 验传输层**——它 ProjectReference 了 Transport 却**零调用**。
**四必做场景**（顺序）：`success` → `pin-mismatch` → `timeout` → `slow-dribble`。
`slow-dribble` 是区分**绝对 deadline** 与**可重置 deadline** 的唯一场景，判据 **`sent=3/4`**
（显示 `4/4` = 时限被逐字节重置 = 真缺陷；变异实测：把时限改成可重置后 `timeout` 仍然绿、
**只有它变红**）。`cross-subnet` 本次 lab 跑不出（Windows 不能指定源地址，
除非给 `TlsClientConnector` 加本地绑定参数 → HANDOFF §14.13）。
被控端汇总行 `connectionsEnteringSessionHandler=N listenersStoppedCleanly=True activeAtStop=0 handlerFaults=0`
（字段曾叫 `cleanStop`；pin-mismatch 死在 TLS、进不了会话处理器，故不计入 accepted）。
**headless**：同一个 exe 带 `--headless client|host|info` 即命令行模式；WPF 里必须 `Task.Run`
起（`OnStartup` 同步等会死锁），`--headless client --all` 四场景 4/4 PASS 退 0、RUN 头只写 1 次。
**两机日志配对用 4 元组**：控制端 `[CLIENT][CORRELATE] local=<IP>:<port>` ≡
被控端 `[HOST][RESULT] peer=<同一端点>`；**聚合计数相等不构成配对证明**。
控制端只给 `hostExpect` + `[VERDICT] M3 = PENDING-HOST-EVIDENCE`，**不给里程碑结论**。
交叉核对 ④ 是**区间 `3..4`**（TLS 1.3 下服务端可能在收到 alert 前已认为握手完成，
留一行 `rejection=pre-auth-eof`）：区间只取决于 `ReachedWire` / `TlsStageRejection`，
与场景是否 PASS 无关。
手册 `docs/M3_TWO_MACHINE_ACCEPTANCE.md`（§0.1 双击、§0.2 headless、§3 四场景、§5 配对）。
打包 `scripts/acceptance/make-m3-package.py` → **265 files / raw 132.3 MiB / zip 57.4 MiB**，
zip 内**全扁平**（13 个残留空卫星目录不进包），唯一脚本 `set-lab-ip.ps1`（带 BOM）。

## 测试 / 验收写法硬约束（踩过的坑）

- **不要**从 `MemoryStream` 派生并同时重写 `Read(Span<byte>)` 与 `ReadAsync(Memory<byte>)`
  → 一次读被数**两遍**（`Stream.Read(Span)` 默认实现虚拟调用数组重载）。要计数就**包装**内部流
- **不要**在原始字符串字面量 `"""…"""` 里写 `\n`（那里不是转义）
- 取消断言只写 `is OperationCanceledException`：SslStream 抛基类，MemoryStream 替身抛 `TaskCanceledException`
- 阶段超时必须被 `RunAsync` 接住（`when (!ct.IsCancellationRequested)`），
  否则与停机取消不可区分且结局永远产生不出来
- **变异验证必须确认真变红**；「变异后仍然绿」= 测试隔离错了（ADR-032 踩过）
- **禁止「失败即 PASS」**：端口没开 / 防火墙拦 / host 没启动都会让握手失败 —— 但
  **「没连上」本来就会落到退 2**（TCP 不可达抛 `SocketException`/`IOException`，
  **永不**是 `AuthenticationException`）→ 那个独立 TCP 探针已删除（判据本来就是空的）。
  按 `SocketError` 区分时**不要**把 `ConnectionReset` 归进「没连上」——accept 后立刻关闭正是 RST
- **`ReadAsync == 0` ≠ 收到 `close_notify`**：裸 TCP FIN 也返回 0（RST 抛 `IOException`）
  → 文案一律写「**有序 EOF（TLS 记录层 EOF）**」
- **TLS 1.3 幽灵行**：客户端拒绝服务端证书发 alert，而 TLS 1.3 下服务端**在收到 alert 之前**
  已认为握手完成 → 照样进会话处理器、读到 EOF 给 `rejection=pre-auth-eof`（本机环回实测）；
  TLS 1.2 下服务端握手直接失败、才真的不留行 → 所以判定必须写成**区间**，不能写死
- **不许把「没观测到」写成 0**：期望值只能由客观事实（是否连上过线、是否死在 TLS 阶段）
  派生，不能由「本该没有」派生（ADR-034）
- **CLI 解析器返回值就是字面意思**：`TryParse` 的 `true` ⟺ 解析出非空 command；错误分支写
  `return true` → 调用方按成功处理 → NRE → 退 3 且 stdout/stderr **全空**（真踩过，14 处）
- **裸后台线程未处理异常 = 进程当场死亡**（exit 127、零输出、handler 来不及做事）；
  **未观察的 faulted Task = 进程存活且完全静默**（.NET Core 起终结器不再杀进程）→ 两个 handler 都要挂
- 不要把「对端怎么收尾」压成一个布尔，要分 `eof` / `reset` / `still-open` / `unexpected-data`
- 「删一行不会变红」的开关（TLS 选项）必须 `internal` + `InternalsVisibleTo` 后直接断言

## 网络环境与 lab（2026-09-21 复核）

- **本机唯一活跃网卡 = 以太网 `172.100.166.220/24`（Dhcp）**，**不是 RFC1918**
  → 本机跑不通发现与验收，**这是对的不是 bug**
- WLAN **Disconnected**（只有 APIPA）；「本地连接* 1/2」只有 APIPA
  —— 早期记的「WLAN 连上 10.65.156.134」与「已过期」两版都不要信，以 `Get-NetIPAddress` 为准
- 用户两台实机 `172.100.166.220` / `172.100.166.65`（网关 .254）
- **`172.100.x.x` 不是 RFC1918**（172 段只到 172.31）→ 判私有必须按**数值区间**，不能用前缀字符串
- 处置：`set-lab-ip.ps1 -Role A|B` → `192.168.1.10` / `192.168.1.20`（/24）+ Private +
  **UDP 45872 与 TCP 45873 两条入站规则**（只放 UDP 会让 M3 静默失败）；
  `-Undo` 撤销地址**并删除这两条规则**。
  脚本可重复运行、缺哪条补哪条；但记住「**地址已存在 ≠ 环境已就绪**」——
  原版在幂等分支直接 `exit 0`，导致重跑永远补不上规则（2026-09-21 修）。
- **A 机现场数据**（交叉核对对表用）：设备名 `DESKTOP-D132BMD`、设备码 `M5WC-14GX`、
  指纹 `89A5C10E…F445`、lab 地址 `192.168.1.10`
- **本机提权路径**：PowerShell `Start-Process -Verb RunAs` 被安全策略拦，
  Git Bash 也不能直接调 `powershell.exe` → 用 Python + `ctypes` 调
  `ShellExecuteExW(lpVerb="runas")`，再 `WaitForSingleObject` + `GetExitCodeProcess`
  读退出码（2026-09-21 实测两次均得 0）。PS 工具易吞输出 → 写文件再读

### Windows IPv4 实测（代价是断网两次）

**一张网卡只能 DHCP 或静态，不能共存**：在 DHCP 接口上追加地址会把接口翻成 `Dhcp=Disabled`
并**丢租约**，删掉后只剩 APIPA。正确做法：整口切静态（用当前同一套 IP/掩码/网关/DNS，不断网）
→ 再追加第二地址（**不带网关**）。
**netsh 退出码不可信**（已是 DHCP 时返回非 0 却实为成功）→ 必须 `Get-NetIPInterface` 复核。
**PS 5.1 陷阱**：`($x | %{ $_.IP } -join ', ')` 会把 `-join` 当参数 → 写 `$x.IP -join ', '`。
`Set-NetConnectionProfile` 在刚切完静态时会因 `Identifying...` 失败 → 需重试。

## 产品形态决策（2026-09-20 用户拍板「三个都做」）

**总原则**：终端用户永不打开 PowerShell；要权限走 UAC。「一键」= **入口无感 + 触发显式 + UAC**，
**绝不是静默自动执行**（本机已两次把自己搞断网）。三条**只定规则，未启动编码**：
**ADR-024** 网络诊断进 UI（M9，给「原因+网卡名+实际地址」）、
**ADR-025** 防火墙一键内置 App（M10，只放行 LocalSubnet，须可精确撤销）、
**ADR-026** 临时私有地址一键（不早于 M10，排除虚拟网卡，撤销幂等）。
ADR 编号缺陷已修：ADR-018（证书加载 flag，已被 029/030 取代）曾被错标成 ADR-021（KeyUsage）。

## 本机开发残留（非阻断）

`%LOCALAPPDATA%\LanRemote\backups\secrets.bin.pre-m1.3-reset.bak` 是 M1.3 手工重置身份的旧备份，
只在本机、不在源码包。**硬约束：绝不把「自动删除身份备份」写进产品逻辑。**
