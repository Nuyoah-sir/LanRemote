# LanRemote

Windows 局域网屏幕共享与远程控制工具，自用性质。

**无账号、无云服务器、无公网穿透、无 UPnP、无中继、无 mDNS。**
只允许同 IPv4 子网的 RFC1918 设备发现与连接，且连接必须通过访问密钥挑战认证。

> 本仓库是**私有仓库**。源码里散布着开发机的真实内网信息
> （内网地址、主机名、设备码、证书指纹），公开前必须做一次脱敏。

---

## 当前状态

**版本 `0.1.0-m2`** · 已完成 **M0 → M1 → M1.1 → M1.2 → M1.3 → M2 → M2.1**。
**M3（TLS Host/Client + 同子网校验）代码已完成**，正卡在最后一步——两机真机验收。

| 里程碑 | 状态 |
| --- | --- |
| M0 脚手架 / M1 身份与秘密 / M1.1~M1.3 安全收口 | ✅ 完成 |
| M2 局域网发现 + M2.1 收口修复 | ✅ 完成，两机验收 PASS 20/20 |
| **M3 TLS Host/Client + 同子网校验** | 🟡 **代码完成，573 tests PASS；两机验收待执行** |
| M4 ~ M11 | 未开始 |

实时进度与逐步明细**一律以根目录 [`HANDOFF.md`](HANDOFF.md) 为准**——README 会滞后。

---

## 不可动摇的约束

改动代码前先读这几条，它们都有测试盯着：

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

> ⚠️ **本机唯一的活跃网卡是公网段地址，不属于 RFC1918**，所以在这台机器上
> 跑发现会输出「没有找到任何合格的私有 IPv4 网卡」——**这是预期行为，不是 bug**。
> 需要 `192.168.x.x` / `10.x.x.x` 网络才能验证发现与连接。

---

## 项目结构

```text
src/
  LanRemote.App          WPF UI、窗口、ViewModel、DI 启动（net10.0-windows）
  LanRemote.Core         领域模型、配置、公共抽象（net10.0）
  LanRemote.Discovery    UDP 发现、网卡筛选、设备缓存
  LanRemote.Transport    TLS/TCP、帧协议、连接状态机
  LanRemote.Security     DPAPI、证书、访问密钥、认证挑战（net10.0-windows）
  LanRemote.Capture      屏幕采集、缩放、JPEG 编码（net10.0-windows）
  LanRemote.Input        SendInput、坐标映射、权限闸门（net10.0-windows）
  LanRemote.Sessions     Host/Client 会话编排
tests/
  LanRemote.Core.Tests          单元测试
  LanRemote.Protocol.Tests      协议常量护栏
  LanRemote.Transport.Tests     传输层与 TLS
  LanRemote.Security.Tests      安全不变量（Windows TFM）
  LanRemote.IntegrationTests    文件系统集成测试
tools/
  LanRemote.Acceptance          M3 两机验收器（WPF 窗口程序，见下）
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

## M3 两机验收器

`LanRemote.App` 目前只是**引用**了 `LanRemote.Transport` 但**一行都没调用**，
所以**不能拿它验收 M3**。验收要用 `tools/LanRemote.Acceptance`：

```text
LanRemote.Acceptance.exe   <- 双击这个，其余全在窗口里点
START-HERE.md              验收手册
set-lab-ip.ps1             配 lab 网段（唯一需要管理员的步骤）
```

它是 **WPF 窗口程序**——双击就是一个窗口，不需要任何脚本。
流程：两台机器各跑 `set-lab-ip.ps1 -Role A` / `-Role B`（保留原 IP 不断网）
→ 一台点「被控端（开始监听）」→ 另一台填对端设备码点「控制端（跑三个场景）」
→ 两边各点「复制全部日志」。

三个必做场景：`success` / `pin-mismatch` / `timeout`。
被控端汇总行应为 `accepted=2 preAuthenticated=1 rejected=1 cleanStop=True`。

打包：

```bash
python scripts/acceptance/make-m3-package.py
```

> 曾经做过「控制台 exe + `START.cmd` + ps1 驱动」的形态，**已废弃**。
> 本项目是桌面软件，总原则是「终端用户永远不需要打开 PowerShell」，
> 验收入口必须是 exe 窗口。

---

## 文档索引

| 文件 | 内容 |
| --- | --- |
| [`HANDOFF.md`](HANDOFF.md) | **权威进度与交接说明**，下一位接手先读这个 |
| [`docs/DECISIONS.md`](docs/DECISIONS.md) | ADR 决策记录（010~033），含被证伪的结论 |
| [`docs/PROTOCOL_AND_SECURITY.md`](docs/PROTOCOL_AND_SECURITY.md) | 协议与安全约束 |
| [`docs/M3_TWO_MACHINE_ACCEPTANCE.md`](docs/M3_TWO_MACHINE_ACCEPTANCE.md) | M3 两机验收手册 |
| [`docs/M3_EXTERNAL_REVIEW_PROMPT.md`](docs/M3_EXTERNAL_REVIEW_PROMPT.md) | 外部设计评审 prompt |
| [`docs/M3_ACCEPTANCE_UI_REVIEW_PROMPT.md`](docs/M3_ACCEPTANCE_UI_REVIEW_PROMPT.md) | 验收器 UI 设计评审 prompt |
| [`LanRemote_Implementation_Package/`](LanRemote_Implementation_Package/) | 原始施工规格快照（不回写） |

---

## 一条硬规矩

**Windows / .NET 的实际行为一律本机实测，不问模型；模型的结论不得直接写进 HANDOFF。**
外部模型只用于「设计红队评审」。
本机是 Win11 25H2，因此 **TLS 在 Win10 22H2 上的行为无法验证**，相关结论必须标注未测。
