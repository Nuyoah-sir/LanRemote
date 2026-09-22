# LanRemote 项目长期记忆

> 唯一真实进度 = 仓库根 `HANDOFF.md`；本文件只放跨会话必记的规则、实测事实与停点。
> ADR 工作副本 `docs/DECISIONS.md`（010~038；037=衔接层 / 038=认证协议，均 2026-09-21 定案）；规格合同 `LanRemote_Implementation_Package/`（不回写）。
> 注入截断上限实测 ≈10000 字符（2026-09-21；超出即截）；本文件 ~6.8K 字符 = 安全。
> 再遇「MEMORY.md 超限」提示先核实大小，勿盲目整并。

## 定位与硬约束

Windows 局域网屏幕共享/远程控制（自用）。无账号/云/穿透/UPnP；仅同 IPv4 子网 RFC1918 设备，须过访问密钥挑战认证。

- C# / .NET 10 / WPF / x64；TFM `net10.0` + `net10.0-windows`。**不要**加 `windows10.0.19041.0`（无 Win10 SDK，ADR-010）
- UDP 45872 发现；TCP 45873 控制；Control 与 Video 是**两条独立 TLS**（ADR-003）
- 访问密钥 128-bit `RandomNumberGenerator`；HMAC-SHA256 挑战；明文 key 不上网；必须验 serverProof
- 视频管线 bounded queue(1~2)+DropOldest；**严禁无界队列**
- 永不删除/弱化：同子网校验、RFC1918、TLS、指纹 pinning、HMAC、DPAPI、ViewOnly/Control 隔离、本机审批、被控提示、紧急停止
- 密码学白名单：TLS/SHA-256/HMAC-SHA256/RandomNumberGenerator/FixedTimeEquals/DPAPI/BCL X509

## 协作规矩

中文；结构化；结论先行给证据；**严禁伪代码与伪造构建/测试结论**（没跑写「未运行」）；改动最小化。
每里程碑 = build + test + 更新 HANDOFF（记 `Last code commit` + `Working tree at validation`，**不写 HEAD hash**）。
外部模型只做设计红队评审（prompt 在 `docs/`）；Windows/.NET 行为一律本机实测；模型结论不直写 HANDOFF。
**交付必须双击即 GUI**（WinExe/WPF），绝不「控制台 exe + 脚本」（用户连纠三次）。
不许静默改网络/防火墙（两次断网事故）；「一键」= 入口无感 + 触发显式 + UAC。

## 命令 / 打包

```bash
source scripts/env.sh   # dotnet 在 ~/.dotnet，不在 PATH
dotnet build LanRemote.sln -c Debug && dotnet test LanRemote.sln -c Debug --no-build
```

出包 `scripts/acceptance/make-m3-package.py`（M3 验收器；`LANREMOTE_M3_SKIP_PUBLISH=1` 只重打，**别用 `LANREMOTE_M3_CLEAN=1`**——safe-delete hook 连脚本内 rmtree 也拦 → SystemExit）。
`.ps1` 必须 UTF-8 **with BOM**；`.cmd` 纯 ASCII 无 BOM（中文注释也会解析错）。
包形态：265 files / raw 132.3 MiB / zip 57.4 MiB，zip 内全扁平。

## 里程碑（2026-09-21）

| 阶段 | 状态 |
| --- | --- |
| M0~M1.3 | 完成（`104f296` / `9e75fce`） |
| M2+M2.1 | 完成，两机验收 **20/20**（`313c542`，408 tests） |
| **M3** | **完成**——24 步全完（0 警告 / **574 tests PASS**）；第 24 步两机验收 **PASS**（2026-09-21 真机，判定=证据配对；被控端结局字段 INVALID_RUN 系收尾机制机械产物，非失败）。明细见 HANDOFF §15 |
| **M3.1** | **完成（2026-09-21）**——加固：外层信封 8s（provisional）+ HelloTimeout 语义修正 + 停机报告（未完成计数）+ B15/16/18/19/20 测试补强 + 验收器同步；`2dee00c`；**601 tests PASS**（Debug+Release 0 警告）；变异验证全精确命中。明细 HANDOFF §18.4 A |
| **M4** | **进行中：阶段 0、1 完成（2026-09-22，`35506b5`）**——阶段 0：盘点定案 → ADR-037（衔接层）+ ADR-038（认证协议）+ ADR-027 落地（609 PASS）；阶段 1：**双档 transcript + HMAC proof 纯函数核心** + 独立 Python 黄金向量脚本入库 + `docs/PROTOCOL_AND_SECURITY.md` §9 本地修订 1（**643 PASS**）。下一站 = 阶段 2（认证帧 JSON 严格解析）。6 阶段 21 步见 HANDOFF §18 |
| M5~M11 | 未开始 |

M3 链 `ee1cbe3`→`601d7a7`→`5ba5822`→`e0484ec`→`5bf3cb6`→`5ae052f`→`f080581`→`ffd73e9`→`c5f0aa9`→**`1d5ffc8`**（一键准备本机）；M3.1 = **`2dee00c`**；M4 阶段 0 = **`2312e70`**（记账 `cddc071`）；M4 阶段 1 = **`35506b5`**（重定位 `e7687ec`）。
远端 `origin` = https://github.com/Nuyoah-sir/LanRemote.git（**public**，用户手动建库）——2026-09-21 起全部推送成功（远端 `main` = 本地）。`gh` 未装也不需要（`gh auth login` 挂账解除）。**两个坑**：① 链路间歇性抖动（push 挂到超时 / schannel 失败）→ 重试即过；② **helper-selector 陷阱**（源码级定论）：`git-credential-helper-selector` 每次被调必弹 GUI（无静默委托、无桌面即挂起、`--help` 也弹并写配置），「`<no helper>`+Always」= 把 `credential.helper` 写成空串（清链）。**已全局修复（2026-09-21）**：`selected = manager` + 链「空值+`manager`」（repo 级同配双保险）；机器级 fill rc=0、trace 只见 GCM；**勿裸跑 selector**。此后每轮收尾 `git push origin main`。

## 实测事实（别再猜）

- **TFM 依赖方向（2026-09-22 实测）**：**net10.0 项目不能引用 net10.0-windows 项目**（NU1201）——
  无 Windows API 依赖的纯逻辑（如认证协议核心）必须放 net10.0 层（Transport）否则传输层无法消费；Security（net10.0-windows）只留 DPAPI/证书/密钥存储。ADR-039
- **证书私钥（ADR-029/030）**：SslStream 服务端必须 `DefaultKeySet`；`EphemeralKeySet` 9/9 失败（`does not support ephemeral keys` ← `0x8009030E`）；`PersistKeySet` 留磁盘副本。`CreateSelfSigned()` 直出私钥也是 ephemeral → 必须「导出 PFX → Default 重导入」。污染陷阱：同进程先 Persist 再 Ephemeral 会碰巧成功 → 结论须新进程。**判 TLS 失败永远抓服务端异常**（客户端只有 EOF）
- **.NET 10 默认值**：`AllowDuplicateProperties`=True 且后者覆盖；`MaxDepth` 属性值=0（=内置 64）；客户端 `AllowTlsResume/AllowRenegotiation`=True，服务端 Renegotiation=**False**（不对称）；`EnabledSslProtocols=None` 须显式写；校验回调参数是 `X509Certificate` 基类 → 用 `GetRawCertData()`
- **发现**：probe 回应目标 = `remote.Address:45872`（非源端口）；sender 必须显式 `SetSocketOption(MulticastInterface, 网络序 4 字节)`
- **Windows**：一张网卡 DHCP/静态不能共存；`netsh` 退出码不可信（`Get-NetIPInterface` 复核）；选网卡排除 VMnet 等虚拟；PS 5.1 `($x|%{...} -join ', ')` 是绑定陷阱；`172.100.x.x` **不是** RFC1918（按数值判，172 段只到 172.31）
- **提权**：`ShellExecuteExW("runas")`+`WaitForSingleObject`+取退出码（UAC 拒绝=`ERROR_CANCELLED`1223）；主进程永不提权（ADR-035：窄域 helper + 固定动词 + TOCTOU 重校验）
- **子进程编码契约**：.NET 10 无 CP936 解码器；PS 5.1 管道 stdout 按控制台代码页编 → 脚本须在 `IsOutputRedirected` 时切 UTF-8，harness 固定 UTF-8 解，见 U+FFFD 记 WARN
- **M1 存储**：`secrets.bin`=`LRSC`+ver+len+DPAPI(JSON)；设备码=`Base32(SHA256(guid)[..5])`（非秘密）；证书 ECDSA P-256 自签 5 年、KU 只 digitalSignature（ADR-021）；**禁静默自动重签证书**
- **`HelloTimeout` = 客户端写预算（语义已定案）**：服务端从不消费；post-TLS pre-auth 由「前缀 5s + payload 10s + **信封 8s 封顶**」约束（M3.1 已落地 `PreAuthEnvelopeTimeout`，判据=对端可控等待被信封切）。信封数值 provisional，待数值实验定案

## M3 速查（细节在 HANDOFF.md）

accept→同子网→准入→TLS，**顺序不可换**；pre-auth 单帧上限 4 KiB；M3 终态 = hello 后干净关闭（**M4 已定案改为显式交接** `ControlPreAuthHandoff` → `ControlAuthSession`，ADR-037；落地在阶段 3）。五段绝对 deadline 只验了「执行得准」（误差 0–36 ms）；**第二轮评审已回收**（两处缺陷级：外层信封缺失 / transcript 拆分；数值待本机实验后定案，M3.1 先补信封）。`TransportHost` 零 logger（同子网/准入/TLS 拒绝全静默 → ADR-024/M9）。
验收器：双击=WPF 窗口，`--headless client|host|info|prepare-lab`；退出码 0/1/2/3/4=预期内/真失败/前置不满足/工具错/无效运行；`Combine` 优先级 `InvalidRun>HarnessError>Fail>PreconditionUnmet>Pass`；四场景 `success`→`pin-mismatch`→`timeout`→`slow-dribble`（判据 `sent=3/4`）；两机配对用 4 元组（聚合计数不算证明）；控制端只给 `PENDING-HOST-EVIDENCE`；`gui.log` 只记进程级事实、每轮证据在 per-run 文件；别拿 `LanRemote.App` 验传输层（零调用）。**被控端「停止监听」收尾 → 结局字段必为 `INVALID_RUN`（设计：按停=机械作废，防「按停伪造通过」），判定看逐条证据；`--headless host --seconds N` 定时轮不走该路径（实测 PASS）。**

## 测试/验收写法硬约束（踩过的坑）

- MemoryStream 派生 + 重写 `Read(Span)`/`ReadAsync(Memory)` → 一次读被数两遍；要计数必须包装
- `"""..."""` 里 `\n` 非转义；取消断言只写 `is OperationCanceledException`
- 变异验证必须确认真变红（仍绿 = 测试隔离错了，ADR-032）；期望值只能由客观事实派生，不许把「没观测到」写成 0（ADR-034）
- 禁止「失败即 PASS」：连不上抛 `SocketException`/`IOException` 永不是 `AuthenticationException`；`ConnectionReset` 不算「没连上」
- TLS 1.3 幽灵行 → 判定写区间 `3..4`；`ReadAsync==0` ≠ close_notify → 文案写「有序 EOF」；`TryParse` 语义别反
- 裸后台线程未处理异常 = 进程瞬死；未观察 faulted Task = 静默存活 → 两个 handler 都要挂
- 按行数记账的 tailer：`usable` 必须=「确定写完的行数」→ 配独立 `CountCompleteLines` 对账防回归
- 帧读取器「失败后恢复」场景：**消费掉的字节无法退回**——半前缀超时后再读必然错位；恢复性用例只能建在 **0 字节失败**上（B19 定案）
- `dotnet test` 全量数总数用 `| grep -E "已通过!|失败!"`：`tail -N` 会截掉**首个**项目结果行（Protocol.Tests 曾被整行吞掉，574→601 的「差值」据此而来）
- **黄金向量黄金律**：期望值必须来自被测实现之外的**独立第二实现**（`scripts/reference/gen-auth-golden-vectors.py`，纯标准库 Python）；NUL 字面量一律写 `\u0000`——C# 字符串 **`\0` 后跟数字会被解析成八进制转义**（uuid 串以数字开头时必踩，实测）；变异验证专抓「只断言常量关系、不触实现」的假测试（M3 变异实测抓到一条）

## 网络 / lab

- 本机唯一活跃网卡 = 以太网 `172.100.166.220/24`（Dhcp，非 RFC1918）→ 本机跑不通发现（正确行为）
- A = DESKTOP-D132BMD `M5WC-14GX`（lab `192.168.1.10`）；B = DESKTOP-CU2263D `3ERD-R74V`（lab `192.168.1.20`）；**两机验收已跑完；lab 已两机撤销还原**（2026-09-21：B 11:13Z 窗口按钮 / A 19:21 管理员直跑 `--headless lab-undo` runId `037fd836`），两机 `info` 均 `qualifiedNic=(无)`
- `set-lab-ip.ps1 -Role A|B`：整口切静态+追加 lab 地址+Private+**UDP 45872 与 TCP 45873 两条入站规则**；幂等分支也补规则；`-Undo` 一并清（**类别有记录才还原**，无记录保持不动=兜底）；脚本 v3 修过两坑（毒化 state 检测——A 机 state 曾被旧版毒化、注释 "Measured on machine A" 即本机；类别还原）。**验收物料不进产品**，验收器「一键准备」调的就是它；M3 期间「不要真跑 `-Undo`」红线 **2026-09-21 撤销收尾后作废**

## 产品形态（ADR-024/025/026/035/036，规则已定未编码）

网络诊断进 UI（M9）；防火墙一键（M10，只放行 LocalSubnet）；私有地址一键（不早于 M10）；提权模型 ADR-035；网络类别永不自动改（ADR-036）。
已实测（M10 别重跑）：绑路径防火墙规则目录移动后**静默悬挂**（读回 Program 检测）；`Set-NetFirewallRule -Program` 可改且**原地修复保 InstanceID**；`LocalSubnet4` 可写可读。
**M3 不插队做 024/025/026**，等对应里程碑指令。

## 残留

`%LOCALAPPDATA%\LanRemote\backups\secrets.bin.pre-m1.3-reset.bak`。**绝不把「自动删除身份备份」写进产品。**
