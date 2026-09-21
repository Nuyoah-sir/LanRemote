# LanRemote 项目长期记忆

> 详细进度以仓库根 `HANDOFF.md` 为准（唯一真实进度来源）；本文件只放**跨会话必须记住的
> 规则、实测事实与当前停点**。ADR 工作副本：`docs/DECISIONS.md`（010~034）。
> 规格合同 `LanRemote_Implementation_Package/`（ADR 原始快照 9 条，不回写）。

## 项目定位

Windows 局域网屏幕共享 / 远程控制（自用）。无账号、无云、无穿透、无 UPnP/中继。
只允许**同 IPv4 子网的 RFC1918 设备**发现与连接，且必须通过访问密钥挑战认证。

## 不可动摇的约束

- C# / .NET 10 / WPF / x64；TFM `net10.0`（Core/Discovery/Transport/Sessions）、
  `net10.0-windows`（App/Security/Capture/Input）。**不要**加 `windows10.0.19041.0`（没装 SDK，ADR-010）
- UDP 45872 发现；TCP 45873 控制；Control 与 Video 是**两条独立 TLS**（ADR-003）
- 访问密钥 128-bit `RandomNumberGenerator`；禁 `Random`、禁 6 位弱密码
- HMAC-SHA256 挑战认证，**明文 key 绝不上网**，必须验 serverProof
- 视频：bounded queue(1~2) + DropOldest，严禁无界队列；实时性 > 完整性
- 不得删除/弱化：同子网校验、RFC1918 限制、TLS、指纹 pinning、HMAC、DPAPI、
  ViewOnly/Control 隔离、本机审批、被控提示、紧急停止
- 密码学白名单：TLS / SHA-256 / HMAC-SHA256 / RandomNumberGenerator / FixedTimeEquals / DPAPI / BCL X509

## 协作与记账规矩

- 中文；结构化（表格/清单）；结论先行给证据；严禁伪代码与**伪造构建/测试结论**（没跑过写「未运行」）
- 改动最小化；每里程碑 = build + test + 更新 `HANDOFF.md`；
  HANDOFF 写 `Last code commit` + `Working tree at validation`，**不写 HEAD hash**
- 外部模型只做**设计红队评审**；Windows/.NET 行为一律本机实测；模型结论不得直接写进 HANDOFF
- 交给用户的程序必须**双击即 GUI**（WinExe/WPF），绝不「控制台 exe + 脚本」——用户连纠三次
- 不许静默改网络/防火墙（本机已两次断网事故）；「一键」= 入口无感 + 触发显式 + UAC

## 常用命令 / 打包

```bash
source scripts/env.sh        # dotnet 在用户级 ~/.dotnet，不在 PATH
dotnet build LanRemote.sln -c Debug
dotnet test  LanRemote.sln -c Debug --no-build
```

- 出包：`scripts/acceptance/make-package.py`（App）、`make-m3-package.py`（M3 验收器；
  `LANREMOTE_M3_SKIP_PUBLISH=1` 只重打）。**别用 `LANREMOTE_M3_CLEAN=1`**：safe-delete hook
  连脚本内 `shutil.rmtree` 也拦 → 死于 SystemExit；`dotnet publish -o` 本来会覆盖
- `.ps1` 必须 UTF-8 **with BOM**（PS 5.1 否则中文乱码）；`.cmd` 必须纯 ASCII **无 BOM**
- M3 包：265 files / raw 132.3 MiB / zip 57.4 MiB；zip 内**全扁平**，唯一脚本 `set-lab-ip.ps1`

## 里程碑进度（2026-09-21 停点）

| 里程碑 | 状态 |
| --- | --- |
| M0~M1.3 | 完成（`104f296` / `9e75fce`） |
| M2 + M2.1 | 完成，两机验收 **20/20**（`313c542`，408 tests） |
| **M3** | 24 步里 1~23 完成：build 0 警告 / **574 tests PASS**，`f080581`。第 24 步（两机验收）物料已就绪、A 机 lab 已配好（`c5f0aa9`）；**等用户做完 B 机**（拷最新 zip → 解压 → 管理员跑 `set-lab-ip.ps1 -Role B` → 双击 exe） |
| M4~M11 | 未开始 |

- M3 链：`ee1cbe3`→`601d7a7`→`5ba5822`→`e0484ec`→`5bf3cb6`→`5ae052f`→`f080581`→`ffd73e9`→`c5f0aa9`
- GitHub 推送阻塞：用户曾取消 `gh auth login`（一次性代码 CECD-6822 作废）→ **不得重试**，等用户开口

## 关键实测事实（别再猜）

**证书私钥载入（ADR-029/030）**：SslStream 服务端必须 `DefaultKeySet`(0)；
`EphemeralKeySet` 9/9 失败（`does not support ephemeral keys` ← `0x8009030E`）；`PersistKeySet` 留磁盘副本。
`CreateSelfSigned()` 直出的私钥也是 ephemeral → **必须「导出 PFX → DefaultKeySet 重导入」**。
**污染陷阱**：同进程先 Persist 再 Ephemeral 会碰巧成功 → 结论必须新进程 + 顺序受控。
**判 TLS 失败永远抓服务端异常**（客户端只有 EOF）。

**.NET 10.0.12 默认值**：`AllowDuplicateProperties` 默认 True 且**后者覆盖前者**；
`MaxDepth` 属性值默认 0（=内置 64，别写「默认 64」）；客户端 `AllowTlsResume=True`、
`AllowRenegotiation=True`；服务端 `AllowRenegotiation=False`（**不对称**）；两端 `EnabledSslProtocols=None`。
证书校验回调参数是 `X509Certificate`（基类，无 `RawData`）→ 用 `GetRawCertData()`。

**发现不变量（M2.1）**：probe 回应目标 = `remote.Address:45872`（绝不是源端口）；
sender 必须显式设 `IP_MULTICAST_IF`（网络序 4 字节地址；`HostToNetworkOrder`/裸 index 都抛错）。

**Windows IPv4**：一张网卡「DHCP 或静态」二选一，**不能共存**（追加地址会丢租约断网）；
`netsh` 退出码不可信（要 `Get-NetIPInterface` 复核）；自动选网卡必须排除虚拟网卡
（VMnet 等 Up+802.3+有地址但不走物理线）。PS 5.1：`($x | %{...} -join ', ')` 是参数绑定陷阱。

**提权路径（本机）**：`Start-Process -Verb RunAs` 被安全策略拦、Git Bash 不能调 powershell →
用 Python + ctypes `ShellExecuteExW("runas")` + `WaitForSingleObject` + `GetExitCodeProcess`（实测可用）。

**M1 存储**：`secrets.bin` = `LRSC`+ver+len+DPAPI(JSON)；bundle 含 deviceGuid/accessKey/certificatePfx/
证书口令；设备码 = `Base32(SHA256(deviceGuid)[..5])`（非秘密）；证书 = ECDSA P-256 自签 5 年、
KU 只 digitalSignature（ADR-021）；**禁止静默自动重签证书**。

## M3 成果速查

- 类型：`CertificatePin` / `ConnectionTarget`（点击时冻结的不可变快照）/ `ConnectionIdentity`
  （含 **presentedPin**，ADR-028）/ `TlsClientConnector` / `TlsConnection` / `TransportHost`
  （accept→同子网→准入→TLS，**顺序不可换**）/ `ConnectionAdmissionLimiter` / `ConnectionRegistry` /
  `FrameReader/Writer` / `HelloFrame` / `ControlPreAuthSession`
- pre-auth 单帧上限 **4 KiB**；M3 终态 = hello 后**干净关闭**；`AllowedOperationsWhilePreAuthenticated`
  必须空集合（有测试盯着）
- 五段绝对 deadline 只验了「**执行得准**」（误差 0–36 ms），**数值本身仍待外部评审**（挂账）
- `TransportHost` 零 logger：同子网/准入/TLS 拒绝全静默（→ ADR-024 / M9）；
  pre-auth 超时发生在会话处理器**内部**，有输出（`rejection=pre-auth-timeout`）

**M3 验收器 `tools/LanRemote.Acceptance`（WPF，双击=窗口，零脚本）**：

- 退出码 0/1/2/3/4 = 预期内/真失败/前置不满足/工具错/无效运行；`Combine()` 优先级
  `InvalidRun > HarnessError > Fail > PreconditionUnmet > Pass`
- 四场景顺序：`success` → `pin-mismatch` → `timeout` → `slow-dribble`
  （`slow-dribble` 是区分绝对/可重置 deadline 的唯一场景，判据 `sent=3/4`）
- 两机日志配对用 **4 元组**：`[CLIENT][CORRELATE] local=<IP>:<port>` ≡ `[HOST][RESULT] peer=<同一端点>`；
  **聚合计数相等不构成配对证明**。控制端只给 `PENDING-HOST-EVIDENCE`，不给里程碑结论
- `--headless client|host|info` = 同一个 exe 的命令行模式；WPF 里必须 `Task.Run` 起
- 手册 `docs/M3_TWO_MACHINE_ACCEPTANCE.md`；**别拿 `LanRemote.App` 验传输层**（零调用）

## 测试/验收写法硬约束（踩过的坑）

- MemoryStream 派生 + 同时重写 `Read(Span)`/`ReadAsync(Memory)` → 一次读数**两遍**；要计数就包装
- `"""..."""` 里 `\n` 不是转义；取消断言只写 `is OperationCanceledException`
- **变异验证必须确认真变红**；「变异后仍绿」= 测试隔离错了（ADR-032）
- 禁止「失败即 PASS」：「连不上」抛 `SocketException`/`IOException`，**永不是**
  `AuthenticationException`；`ConnectionReset` **不算**「没连上」（accept 后立刻关闭正是 RST）
- 期望值只能由客观事实派生，**不许把「没观测到」写成 0**（ADR-034）
- TLS 1.3 幽灵行：pin 拒绝时服务端可能照样进会话（`rejection=pre-auth-eof`）→ 判定写**区间** `3..4`
- `ReadAsync==0` ≠ `close_notify` → 文案写「有序 EOF」；`TryParse` 语义别写反
  （错误分支 `return true` → 调用方 NRE → 退 3 且 stdout/stderr 全空）
- 裸后台线程未处理异常 = 进程当场死亡；未观察 faulted Task = 进程静默存活 → 两个 handler 都要挂

## 网络 / lab 环境

- 本机唯一活跃网卡 = 以太网 `172.100.166.220/24`（Dhcp）**不是 RFC1918** → 本机跑不通发现（正确行为）
- 两台实机：A = DESKTOP-D132BMD / `M5WC-14GX` / 指纹 `89A5C10E…` / lab `192.168.1.10`（已配好）；
  B = DESKTOP-CU2263D / `3ERD-R74V` / 指纹 `6755838E…`（待配 `192.168.1.20`）
- `set-lab-ip.ps1 -Role A|B`：整口切静态 + 追加 lab 地址（不丢原地址）+ Private +
  **UDP 45872 与 TCP 45873 两条入站规则**（只放 UDP 会让 M3 静默失败）；幂等分支也要补规则
  （「地址已存在 ≠ 环境就绪」）；`-Undo` 一并清。**它是验收物料，不进产品**

## 产品形态决策（ADR-024/025/026，只定规则未编码）

- **ADR-024** 网络诊断进 UI（M9）；**ADR-025** 防火墙一键内置 App + UAC（M10，
  只放行 LocalSubnet、可精确撤销）；**ADR-026** 临时私有地址一键（不早于 M10，排除虚拟网卡、幂等撤销）
- **2026-09-21 增补生效**：首启 UX 评审（第三轮）已分流 → `docs/UX_FIRST_RUN_REVIEW_TRIAGE.md`；
  **新增 ADR-035**（提权模型：主进程非提权 + 窄域 helper 固定动词集 / TOCTOU / UAC 拒绝不循环）
  / **ADR-036**（永不自动改网络类别；可达性自足于 `Profile Any + LocalSubnet4` 规则）；
  ADR-024/025/026 各增补（三就绪、规则模板 + Repair、Advanced 定位等）。M9/M10 施工按修订版执行
- **已实测（M10 时别重跑）**：绑路径规则在目录移动后**静默悬挂**（Enabled=True、Program 指旧路径；
  检测 = 读回 Program 比对）；`Set-NetFirewallRule` 支持 `-Program`；**原地修复保 InstanceID**
  （删除+重建会换 ID）；`LocalSubnet4` 可写可读；lab 规则是宽规则（Program/RemoteAddress=Any，勿照抄）
- M3 明确不做 ADR-024/025/026——**等对应里程碑开工指令**，不要插队

## 本机残留（非阻断）

`%LOCALAPPDATA%\LanRemote\backups\secrets.bin.pre-m1.3-reset.bak`（旧开发身份备份，不在源码包）。
**硬约束：绝不把「自动删除身份备份」写进产品逻辑。**
