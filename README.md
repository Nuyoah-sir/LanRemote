# LanRemote

Windows 局域网屏幕共享与远程控制工具，自用性质。

**无账号、无云服务器、无公网穿透、无 UPnP、无中继、无 mDNS。**
只允许同 IPv4 子网的 RFC1918 设备发现与连接，且连接必须通过访问密钥挑战认证。

> 禁止提交真实访问密钥、DPAPI 数据、PFX 私钥或用户配置；验收日志分享前应检查机器名、地址等环境信息。仓库可见性不改变这些约束。

---

## 当前状态

**版本字符串仍为 `0.1.0-m2`，不代表当前里程碑。** 截至 2026-09-24，M4 核心与验收器认证接线已完成；旧双机尝试两端均已运行 GUI，但发现失败阻断了认证，不能记作「未运行」或「已通过」。当前已实现现网发现修正并停用改网路径，尚不宣称网络恢复或双机认证通过。**产品尚不支持看屏或键鼠控制。**

| 里程碑 | 状态 |
| --- | --- |
| M0 / M1 / M1.1–M1.3 | 完成 |
| M2 / M2.1 局域网发现 | 完成，两机验收 20/20 |
| M3 / M3.1 安全传输及加固 | M3 两机验收已通过；M3.1 加固与自动化验证完成（不是新版两机已重验） |
| M4 双向认证 | 核心与验收器接线完成；旧双机 GUI 已运行，发现失败、认证未开始；当前修复版测试与交付详情见 HANDOFF §18.13 |
| M5–M10 视频、画质、输入、多会话、稳定性与部署 | 未完成 |
| M11 后置优化 | 未开始 |

实时进度与逐步明细**一律以根目录 [`HANDOFF.md`](HANDOFF.md) 为准**——README 会滞后。

---

## 不可动摇的约束

改动代码前先读这几条，它们都有测试盯着：

- **沿用现有局域网自动发现，不改整机网络来迁就应用**：这是用户于 **2026-09-24 重申的原始需求**，不是新增功能。
  不修改 DHCP、IP、Gateway、DNS、Routes、热点/ICS、网络类别或防火墙；确认框、管理员权限、UAC、
  「实验环境」及「以后可撤销」均不构成豁免。**主进程永不提权**，诊断只读；详见 ADR-045。
- **不做**云、账号、CA/PKI、公网穿透、UPnP、中继
- 只允许 **RFC1918 且同子网**的 IPv4 对端；判私有必须按**数值区间**
  （`172` 段只到 `172.16`–`172.31`，`172.100` **不是**私有地址）
- 访问密钥 128-bit `RandomNumberGenerator`；认证是 HMAC-SHA256 挑战，
  **明文 key 绝不上网**，必须验证 serverProof
- 密码学白名单：TLS、SHA-256、HMAC-SHA256、`RandomNumberGenerator`、
  `FixedTimeEquals`、DPAPI、BCL `X509`/`SslStream`。禁自研算法
- UDP **45872** 发现，TCP **45873** 控制；Control 与 Video 是**两条独立 TLS**
- 视频管线 bounded queue + DropOldest，**严禁无界队列**；实时性 > 完整性

---

## 构建

.NET SDK 装在**用户级目录** `~/.dotnet`，不在 PATH 里。必须先加载环境：

```bash
# Git Bash
source scripts/env.sh
dotnet build LanRemote.sln -c Debug
dotnet test  LanRemote.sln -c Debug --no-build
```

```powershell
# PowerShell
. .\scripts\env.ps1
dotnet build LanRemote.sln -c Debug
```

| 环境 | 版本 |
| --- | --- |
| SDK | 10.0.401 |
| Microsoft.NETCore.App | 10.0.12 |
| Microsoft.WindowsDesktop.App（WPF） | 10.0.12 |
| OS（开发机） | Windows 11 专业版 25H2 / 26200 |

> **2026-09-24 本机现网**：热点接口为 **if13，`192.168.137.1/24`**；原上游地址
> `172.100.166.220` 仍不属于 RFC1918，不能参与私网发现。不能再用「唯一活跃公网网卡」解释当前现场。
> 私网地址存在不等于发现或认证成功；仍须满足接口筛选、同子网与认证约束，不得通过改网绕过。

---

## 项目结构

```text
src/
  LanRemote.App          WPF UI、窗口、ViewModel、DI 启动（net10.0-windows）
  LanRemote.Core         领域模型、配置、公共抽象（net10.0）
  LanRemote.Discovery    UDP 发现、网卡筛选、设备缓存
  LanRemote.Transport    TLS/TCP、帧协议、连接状态机
  LanRemote.Security     DPAPI、证书、访问密钥存储（认证协议核心位于 Transport）
  LanRemote.Capture      屏幕采集、缩放、JPEG 编码（net10.0-windows）
  LanRemote.Input        SendInput、坐标映射、权限闸门（net10.0-windows）
  LanRemote.Sessions     Host/Client 会话编排
tests/
  LanRemote.Core.Tests          单元测试
  LanRemote.Protocol.Tests      协议常量护栏
  LanRemote.Transport.Tests     传输层与 TLS
  LanRemote.Security.Tests      安全不变量（Windows TFM）
  LanRemote.IntegrationTests    文件系统集成测试
  LanRemote.Acceptance.Tests    审批/生命周期/结算、STA Dispatcher 与真实 TLS 接线测试
tools/
  LanRemote.Acceptance          M4 认证验收器（WPF 窗口程序，见下）
docs/                           规格副本、ADR 决策记录、验收手册
scripts/                        环境与验收脚本
LanRemote_Implementation_Package/  原始施工规格（快照，不回写）
```

> TFM 是 `net10.0`（Core/Discovery/Transport/Sessions）与 `net10.0-windows`
> （App/Security/Capture/Input）。**不要**改成 `...windows10.0.19041.0`——本机没装 Win10 SDK。

---

## 秘密存储

秘密与配置严格分离：`AppConfigStore` 永不碰 `secrets.bin`，`DpapiSecretVault` 永不写
`config.json`，两条都有测试守护。

```text
%LOCALAPPDATA%\LanRemote\config.json     普通设置
%LOCALAPPDATA%\LanRemote\secrets.bin     DPAPI 保护的秘密（M1 起写入）
%LOCALAPPDATA%\LanRemote\logs\           日志
```

`secrets.bin` = `LRSC`(4) + version(1) + length(4 大端) + DPAPI(JSON)，
bundle 含 `deviceGuid` / `accessKey`(Base32 26) / `certificatePfx` / `certificatePfxPassword`。

- 加密 `ProtectedData.Protect(..., CurrentUser)`：换用户或换机器都解不开
- 写入是临时文件 + 原子替换，不留半截文件
- **文件损坏时不静默重建**（否则「损坏」会被伪装成「首次运行」），抛 `InvalidDataException`
- 证书两个字段必须同存同缺，缺一半时**拒绝**而非重签——否则指纹会静默改变

设备证书：自签名 **ECDSA P-256**，5 年，EKU `serverAuth`，非 CA，
**KeyUsage 只有 `digitalSignature`**（RFC 5480：EC 证书不得声明 `keyEncipherment`）。
指纹 = `SHA256(RawData)` 大写 hex。

> 私钥载入：实测 `EphemeralKeySet` 在真实 `SslStream` 服务端 9/9 失败
> （`does not support ephemeral keys` ← `0x8009030E`），**必须用 `Default`(0)**，
> 且 `CreateSelfSigned` 直出的私钥本身就是 ephemeral，**必须走「导出 PFX → 重导入」往返**。
> 详见 `docs/DECISIONS.md` 的 ADR-029 / ADR-030。

---

## M4 当前验收器与 M3 历史验收

当前 `tools/LanRemote.Acceptance` 已升级：success 走双向认证，GUI 输入对端密钥、本机审批、批准前短码、持有会话后释放。双击 `LanRemote.Acceptance.exe` 仍是 WPF 窗口；成功链按 SessionId 双端配对，认证与证据生命周期见 ADR-044。

### 现网发现与只读边界（ADR-045）

- **WFD 窄例外**：必须同时满足 `Wireless80211`、`InterfaceIndex > 0`、系统 `Description` 精确匹配
  `Microsoft Wi-Fi Direct Virtual Adapter`，或该描述加 ` #正整数`（ASCII 数字、无前导零、无额外文本，大小写精确匹配）。
  只豁免 `virtual` 关键词，其他 VPN/虚拟接口关键词仍拒绝；**只识别 WFD 接口，不识别 ICS 共享角色**。
  RFC1918、同 IPv4 子网、访问密钥认证及审批约束全部保留，发现不等于授权。
- **receiver 加组按接口去重**：先验证完整快照的 index/Id 一致性，再让每个接口 join 一次；
  地址级 `NetworkBinding` 与 sender 保持不变。加组失败日志包含接口索引、地址、组播组、`socketError` 与 `nativeError`；
  异常不吞掉，释放半初始化 socket 并回滚启动状态。这里回滚的是**应用资源，不是系统网络**。
- **改网入口全部停用，不是禁用整个 GUI/CLI**：GUI、CLI、elevated、lab、Undo 的网络修改路径均拒绝；
  普通验收与只读检查继续可用，不调用 shell/UAC 来改网。仓库旧 `set-lab-ip.ps1` 在参数块后直接 `throw`，
  不进入历史权限检查、状态读写或网络操作；不能照旧说明执行 Undo。
- **证据边界**：旧双机尝试 GUI 已运行，但发现失败、认证未开始；窗口渲染或本地 info PASS 不代表完整 GUI、
  发现、互通、认证或互联网正常。三项已运行并恢复的定向变异及其边界见 ADR-045；**测试与交付详情见 HANDOFF §18.13**，这里不预填当前全量结果。

### M3 历史判据与物料（不可执行旧改网流程）

**以下仅保留 M3 历史记录，不可用于判定当前 M4 success**（M4 不再接受 hello 后快速 EOF 作为成功）。
当时使用 `tools/LanRemote.Acceptance` 而非尚未调用传输层的 `LanRemote.App` 验收。
旧包曾附 lab 配网脚本与旧手册，并要求准备 A/B 实验网段；**该流程已停用，确认或 UAC 也不构成例外**。
当前只按下方现网手册验收，不使用旧脚本、旧手册或自动 Undo。

**四个必做场景**：`success` / `pin-mismatch` / `timeout` / **`slow-dribble`**。

> `timeout` 单独**证明不了「绝对时限」**：一个「每读到字节就重置」的空闲超时同样会在
> 约 5 s 断开并 PASS。`slow-dribble` 才是唯一能区分两者的场景——它按 2 s 间隔逐字节
> 发长度前缀，服务端必须在**从进入阶段起算**的 5 s 上切断（即只发出 3/4 字节）。

### 判定是**交叉核对**，不是单侧自宣

控制端每个场景都以 `hostEvidence=REQUIRED` + `hostExpect="…"` 结尾，
并在最后打 `[VERDICT] M3 = PENDING-HOST-EVIDENCE`——**它永远不宣布里程碑通过**。
被控端结束时给 `[HOST][SUMMARY]`，两者的**连接用 4 元组配对**
（控制端 `local=<IP>:<端口>` ≡ 被控端 `peer=<IP>:<端口>`；靠计数相等配对是错的）。

被控端汇总行要满足五条约束，其中第 ④ 条是**区间不是数字**：

```
④ connectionsEnteringSessionHandler 落在 3..4
```

原因是被实测纠正过的：客户端拒绝服务端证书时发的是 TLS alert，
而 **TLS 1.3 下服务端在收到 alert 前就已认为握手完成**，于是照样进会话处理器、
读到 EOF 给出 `rejection=pre-auth-eof`；TLS 1.2 下才真的不留行。
原先写的「应为 3」是**从症状推断因果**，已被实测证伪。

> 这套判据本身是**变异验证过**的：环回上用一个可切换行为的假被控端打了 11 例变异矩阵
> （`real` / `deaf` / `resettable` + `nothing-listening` 对照组），
> 确认每条判据都能在**该红的地方红、其他地方不红**。逐例结果见 `HANDOFF.md`。

### 当前 CLI 与出包入口

同一个 exe 保留普通 `--headless info/host/client` 入口，供自动化与只读检查使用。
**headless success 没有安全凭据来源时为 UNMET**，不通过 key argv 或自动审批绕过认证；旧改网参数与角色被拒绝。

```bash
LanRemote.Acceptance.exe --headless info
LanRemote.Acceptance.exe --headless host [--seconds N]
LanRemote.Acceptance.exe --headless client --peer <设备码> [--scenario <场景>]... [--all]
LanRemote.Acceptance.exe --headless client --address <IP> --pin <指纹> --all   # 直连，用于自检
```

| 退出码 | 含义 |
| --- | --- |
| 0 | PASS — 符合预期 |
| 1 | FAIL — 真的不符合预期 |
| 2 | UNMET — 前置条件不满足（**不是**产品失败） |
| 3 | HARNESS_ERROR — 验收器自身故障（参数写错等） |
| 4 | INVALID_RUN — 操作员中止，整轮作废 |

当前出包显式选用已存在的 **`outputs/m4-network-fix/M4-现网验收说明.txt`**，输出目录必须已存在：

```bash
python scripts/acceptance/make-m3-package.py --milestone m4 --manual "outputs/m4-network-fix/M4-现网验收说明.txt" --output-dir "outputs/m4-network-fix"
```

- 旧 M2 `scripts/acceptance/make-package.py` 入口直接拒绝，不复制、发布或打包。
- 当前脚本虽保留 `make-m3-package.py` 名称，但只接受 M4；每次在 `artifacts/` 下创建独立全新
  `m4-acceptance-*` 目录并 fresh publish（Release / win-x64 / self-contained），不复用旧发布目录。
  拒绝跳过 publish，`LANREMOTE_M3_SKIP_PUBLISH=1` 会报错退出。
- 递归排除**所有层级**的 `.ps1`、`.cmd`、`.bat` 及旧 `START-HERE` / `START-HERE.*` 文件（大小写不敏感）；
  仅从 `--manual` 显式选择的路径装入一份手册，当前 `.txt` 手册在包内为 `START-HERE.txt`，不混入旧手册。

`outputs/` 为本地交付目录，不随普通源码提交；出包时手册须与当前候选代码匹配。命令与打包策略不代表本次已出包或双机已通过；测试与交付详情见 HANDOFF §18.13。

> 曾经做过「控制台 exe + `START.cmd` + ps1 驱动」的形态，**已废弃**。
> 本项目是桌面软件，总原则是「终端用户永远不需要打开 PowerShell」，
> 验收入口必须是 exe 窗口。

---

## 文档索引

| 文件 | 内容 |
| --- | --- |
| [`HANDOFF.md`](HANDOFF.md) | **权威进度与交接说明**，下一位接手先读这个 |
| [`docs/DECISIONS.md`](docs/DECISIONS.md) | ADR 决策记录（010~045），含历史覆盖关系与被证伪的结论 |
| [`docs/PROTOCOL_AND_SECURITY.md`](docs/PROTOCOL_AND_SECURITY.md) | 协议与安全约束 |
| [`docs/M3_TWO_MACHINE_ACCEPTANCE.md`](docs/M3_TWO_MACHINE_ACCEPTANCE.md) | M3 两机验收手册 |
| [`docs/M3_EXTERNAL_REVIEW_PROMPT.md`](docs/M3_EXTERNAL_REVIEW_PROMPT.md) | 外部设计评审 prompt |
| [`docs/M3_ACCEPTANCE_UI_REVIEW_PROMPT.md`](docs/M3_ACCEPTANCE_UI_REVIEW_PROMPT.md) | 验收器 UI 设计评审 prompt |
| [`docs/M3_ACCEPTANCE_UI_REVIEW_TRIAGE.md`](docs/M3_ACCEPTANCE_UI_REVIEW_TRIAGE.md) | 该评审的逐条裁定（含三项「待实测」的实测结论） |
| [`LanRemote_Implementation_Package/`](LanRemote_Implementation_Package/) | 原始施工规格快照（不回写） |

---

## 一条硬规矩

**Windows / .NET 的实际行为一律本机实测，不问模型；模型的结论不得直接写进 HANDOFF。**
外部模型只用于「设计红队评审」。
本机是 Win11 25H2，因此 **TLS 在 Win10 22H2 上的行为无法验证**，相关结论必须标注未测。
