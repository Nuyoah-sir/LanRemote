# LanRemote 项目长期记忆

> 唯一真实进度 = 仓库根 `HANDOFF.md`；本文件只放跨会话必记的规则、实测事实与停点。
> ADR `docs/DECISIONS.md`（至064会话拥有型视频工厂；045现网只读）；原始规格 `LanRemote_Implementation_Package/` 不回写。
> 注入约10000字符超出截断；更新先验长度，超限先核实，不盲目整并。

## 当前续作点（2026-10-01，优先于下方历史记录）

最新9cede86/§19.20/ADR064（6files/+2583）：Core public IVideoFrameProducer/Factory仅Guid/帧，不含Stream/会话token；Sessions factory每会话专用唯一主屏，原Task.Run创建取消不使用WaitAsync代理，失败完整回滚；适配器Completion覆盖管线和独立双依赖释放，共享引用一次，Read已交付帧归caller，EOF等清理，原多fault嵌套不Flatten。factory不验证认证；Host仍未落地。
初factory200绿/2warning等价修；producer初build边写CS0246，次217/218仅tail夹具过早注错，修后218绿/0warn，旧日志保留。Sessions218=旧104+新114（Factory96/Producer18）；七变异3/1/2/2/12/6/3共29目标Assert红、0Guard，旧分类器首轮漏报但原TRX再次证实、各轮恢复绿；变异保护21≠final334 scope。final16TRX两配置各3542 PASS，四Rebuild0警告/错误，六命令rc0/hash核对；334源文件SHA前后及本轮编辑前当前一致（HANDOFF记账后例外）。仅runner+6direct退出，不称全机扫描。证据outputs/m5-frame-pipeline/factory-mutation-20261001-104604/report.json、outputs/m5-video-producer/final-20261001-105537-153331700/summary.json。
下一片Transport每已认证Host会话ACK flush后创建并父子join、防迟到read初始化，然后App/WPF显示与十分钟DoD；M5未完成。不重跑M4/改网/防护/绕权限/出新包。
上次记账历史：50d54af/§19.19/ADR063为双级有界FramePipeline(104新例)，八变异20目标红/0Guard；最终3428绿、四Rebuild0警告错误、328SHA；详情见原节及上次证据。本轮仅四文档，无dotnet/代码测试修改/提交/任务。

M4已收口（§18.25/c251d21），不翻绿/重跑/加DPI；pin仅顺序关联，采样跨度非清理耗时，不外推擦除。A可做的不推B；SAC授权非产品功能。隔离WinExe无vault/发现/改网；旧ZIP不动，包边界§18.22–23。

历史速查：99dfd76/§19.18/ADR062主屏GDI物理BGRA/JPEG最近邻与原worker单飞；WPF吞底层写错须JpegOutputStream锁存，32MiB仅输出上限。157新例、32目标红（8Guard不计），双配置3324绿/四Rebuild0警告错误/319SHA；仅合成JPEG/离屏GDI，非实屏/混合DPI。511ca6d/§19.17 public外壳预建/投影先发布再Start，父等raw+accepted投影、子仅raw，完整Aggregate不吞主动close错；f3dbccc/§19.16 Control一次原读monitor。冻结IP/pin/proof/ACK，敏感wire等原IO退出再清零；83字节nonce transcript未擦除但无token/proof，不承诺TLS内部清零或最窄Committed窗口。

审批按状态机接受时刻：elapsed>=budget拒，gate前计时含UI；caller取消>截止>已观察活动>决定，校验后再查。machine覆盖MAC后终检，之前不改limiter；context loader至多一项实际store，迟到key先清零再释放准入，CTS不能硬中断同步DPAPI/gate/阻塞回调。客户端独占TLS到认证，presentedPin及grant proof验完才交会话，public无流/token/输入；hello后machine10s、challenge只收窄、独立approval60s。只消费首个终帧；DTO/TLS内部副本不保证擦除；clock变异须在被删检查之前客观固定到期，不把后置兜底误称单点覆盖。

用户允许自动测试通过即接下一任务，只有真实人工/权限依赖停；M4不阻断M5。context单WorkLease先清零归还；Host180秒自然结束、提前停作废，成功SessionId/负例四元组，采样跨度>=4s、最大/末间隔<=500ms、非强关注销才PASS。

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
**2026-09-24用户重申原始需求**：同局域网中安装并运行软件的机器应自动发现；软件适应现网，只读网卡/地址并使用标准通信，不重配系统网络。A/B仅角色，不代表固定IP/实验网段；不改DHCP/静态IP/网关/DNS/路由/热点/ICS/网络类别，不自动动防火墙。发现失败应修识别/通信代码或如实诊断，不能要求用户改网配合。软件/验收均不能导致任何一端断网，短暂也不接受，UAC/可撤销不是豁免。旧lab准备停用；恢复旧改动须核对状态并单独获准，不能默认Undo。

## 命令 / 打包

```bash
source scripts/env.sh   # dotnet 在 ~/.dotnet，不在 PATH
dotnet build LanRemote.sln -c Debug && dotnet test LanRemote.sln -c Debug --no-build
```

出包沿用 `scripts/acceptance/make-m3-package.py`，仅M4：`--manual outputs/m4-network-fix/M4-现网验收说明.txt --output-dir <已有目录>`；禁止SKIP，每次独立artifacts/m4-acceptance-*，不删除旧目录，CLEAN已移除。旧make-package.py拒绝。
`.ps1` 必须 UTF-8 **with BOM**；`.cmd` 纯ASCII。新包不交付这些脚本。
M4现网包：264 files / raw132.5MiB / zip57.5MiB，全扁平，程序集含代码提交。

## 里程碑（2026-09-21）

| 阶段 | 状态 |
| --- | --- |
| M0~M1.3 | 完成（`104f296` / `9e75fce`） |
| M2+M2.1 | 完成，两机验收 **20/20**（`313c542`，408 tests） |
| **M3** | 完成，2026-09-21两机24步/574绿，证据配对与机械INVALID_RUN见HANDOFF§15。 |
| **M3.1** | 完成，2dee00c双配置601绿/零警告；8s provisional信封、HelloTimeout、停机报告及B15/16/18/19/20见§18.4 A。 |
| **M4** | **完成（2026-09-29）**。`2596357`双配置各1433 PASS/Python14，实屏明确确认配齐，原DoD/21步收口见HANDOFF§18.25；不重跑已通过项。 |
| **M5** | 进行中：9cede86拥有型生产工厂，各3542 PASS；Host/App/实屏/十分钟DoD未完，§19.20。 |
| M6~M11 | 未开始 |

M3最终1d5ffc8/M3.1 2dee00c；M4阶段0~3=2312e70/35506b5/21a8829/760e950；完整提交链见HANDOFF。
远端origin为public，地址/推送历史见HANDOFF§1；不将旧“远端=本地”外推当前。gh未装也无需。push曾遇超时/schannel；helper-selector每次弹GUI且可能清空helper，勿裸跑（含--help）。2026-09-21全局+repo已修selected=manager及“空值+manager”链，fill rc0/trace仅GCM。提交/推送服从当轮授权。

## 实测事实（别再猜）

- **TFM 依赖方向（2026-09-22 实测）**：**net10.0 项目不能引用 net10.0-windows 项目**（NU1201）——
  无 Windows API 依赖的纯逻辑（如认证协议核心）必须放 net10.0 层（Transport）否则传输层无法消费；Security（net10.0-windows）只留 DPAPI/证书/密钥存储。ADR-039
- **证书私钥（ADR-029/030）**：SslStream 服务端必须 `DefaultKeySet`；`EphemeralKeySet` 9/9 失败（`does not support ephemeral keys` ← `0x8009030E`）；`PersistKeySet` 留磁盘副本。`CreateSelfSigned()` 直出私钥也是 ephemeral → 必须「导出 PFX → Default 重导入」。污染陷阱：同进程先 Persist 再 Ephemeral 会碰巧成功 → 结论须新进程。**判 TLS 失败永远抓服务端异常**（客户端只有 EOF）
- **.NET 10 默认值**：`AllowDuplicateProperties`=True 且后者覆盖；`MaxDepth` 属性值=0（=内置 64）；客户端 `AllowTlsResume/AllowRenegotiation`=True，服务端 Renegotiation=**False**（不对称）；`EnabledSslProtocols=None` 须显式写；校验回调参数是 `X509Certificate` 基类 → 用 `GetRawCertData()`
- **认证帧解析（M4 阶段 2 实测）**：`Utf8JsonReader.GetString()` 对非法 UTF-8 抛 `InvalidOperationException`（**非** JsonException——`JsonSerializer.Deserialize` 才包成 JsonException）→ catch 必须两类都捕；孤立代理转义 `\uD800` 直接 JsonException → 归 malformed；`Guid.TryParseExact("D")` 容忍大写**和前后空白**（canonical 靠 round-trip 收窄）；`"AA++"`/`"AA//"` 是**合法** canonical base64（`+`/`/` 属标准字母表）
- **发现**：probe 回应目标 = `remote.Address:45872`（非源端口）；sender 必须显式 `SetSocketOption(MulticastInterface, 网络序 4 字节)`
- **Windows**：一张网卡 DHCP/静态不能共存；`netsh` 退出码不可信（`Get-NetIPInterface` 复核）；选网卡排除 VMnet 等虚拟；PS 5.1 `($x|%{...} -join ', ')` 是绑定陷阱；`172.100.x.x` **不是** RFC1918（按数值判，172 段只到 172.31）
- **提权**：`ShellExecuteExW("runas")`+`WaitForSingleObject`+取退出码（UAC 拒绝=`ERROR_CANCELLED`1223）；主进程永不提权（ADR-035：窄域 helper + 固定动词 + TOCTOU 重校验）
- **子进程编码契约**：.NET 10 无 CP936 解码器；PS 5.1 管道 stdout 按控制台代码页编 → 脚本须在 `IsOutputRedirected` 时切 UTF-8，harness 固定 UTF-8 解，见 U+FFFD 记 WARN
- **M1 存储**：`secrets.bin`=`LRSC`+ver+len+DPAPI(JSON)；设备码=`Base32(SHA256(guid)[..5])`（非秘密）；证书 ECDSA P-256 自签 5 年、KU 只 digitalSignature（ADR-021）；**禁静默自动重签证书**
- **`HelloTimeout` = 客户端写预算（语义已定案）**：服务端从不消费；post-TLS pre-auth 由「前缀 5s + payload 10s + **信封 8s 封顶**」约束（M3.1 已落地 `PreAuthEnvelopeTimeout`，判据=对端可控等待被信封切）。信封数值 provisional，待数值实验定案

## M3 速查（细节在 HANDOFF.md）

accept→同子网→准入→TLS，**顺序不可换**；pre-auth 单帧上限 4 KiB；M3 终态 = hello 后干净关闭（**M4 已定案改为显式交接** `ControlPreAuthHandoff` → `ControlAuthSession`，ADR-037；落地在阶段 3）。五段绝对 deadline 只验了「执行得准」（误差 0–36 ms）；**第二轮评审已回收**（两处缺陷级：外层信封缺失 / transcript 拆分；数值待本机实验后定案，M3.1 先补信封）。`TransportHost` 零 logger（同子网/准入/TLS 拒绝全静默 → ADR-024/M9）。
验收器：双击=WPF 窗口，`--headless client|host|info`（旧prepare/lab/Undo全拒绝）；退出码 0/1/2/3/4=预期内/真失败/前置不满足/工具错/无效运行；`Combine` 优先级 `InvalidRun>HarnessError>Fail>PreconditionUnmet>Pass`；四场景 `success`→`pin-mismatch`→`timeout`→`slow-dribble`（判据 `sent=3/4`）；两机配对用 4 元组（聚合计数不算证明）；控制端只给 `PENDING-HOST-EVIDENCE`；`gui.log` 只记进程级事实、每轮证据在 per-run 文件；别拿 `LanRemote.App` 验传输层（零调用）。**被控端「停止监听」收尾 → 结局字段必为 `INVALID_RUN`（设计：按停=机械作废，防「按停伪造通过」），判定看逐条证据；`--headless host --seconds N` 定时轮不走该路径（实测 PASS）。**

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
- **测试字面量纪律**：长 base64 **一律 `Convert.ToBase64String` 现造**（`B64(int)` helper），手抄必错——「31 字节」手抄串实为 45 字符（excess padding，非法 base64），测试被别的拒绝路径救活 = 假测试（M4 阶段 2 变异验证第二次抓到同类，邻界值必须走编码器构造）
- **夹具拆线竞速（M4 阶段 3 实测，最贵一课）**：harness 出结局后立即 `cancel+dispose` 会与客户端「读终帧」抢跑——快速失败路径（错钥/被限流/即时拒绝）偶发丢帧；修复 = 拆线前先 `await Task.WhenAny(clientTask, Task.Delay(3s))` 让客户端自然收场。**拆线快 ≠ 对**：「迟到决定」类测试的延时**不绑 stall 令牌**（绑了会被 harness 取消吞掉，测不到目标路径）

## 网络 / lab

- 当前A热点if13=`192.168.137.1/24`、Wi-Fi Direct Virtual Adapter #2；上游以太网if6=`172.100.166.220/24` DHCP且非RFC1918。旧“唯一活跃公网网卡”已过时。
- A = DESKTOP-D132BMD `M5WC-14GX`（lab `192.168.1.10`）；B = DESKTOP-CU2263D `3ERD-R74V`（lab `192.168.1.20`）；**两机验收已跑完；lab 已两机撤销还原**（2026-09-21：B 11:13Z 窗口按钮 / A 19:21 管理员直跑 `--headless lab-undo` runId `037fd836`），两机 `info` 均 `qualifiedNic=(无)`
- 旧set-lab-ip.ps1仅保留历史正文，当前入口无条件throw；不会随包交付，严禁按旧记录执行准备/Undo。2026-09-21还原仅是历史，不能当作9月24日事故后的恢复证明。

## 产品形态（ADR-024/025/026/035/036，规则已定未编码）

ADR-045优先：只读网络诊断保留；旧ADR-026产品改IP计划取消，025/035网络修改默认落地停止；主进程不提权。不得按旧M10安排重加改网功能。
已实测（M10 别重跑）：绑路径防火墙规则目录移动后**静默悬挂**（读回 Program 检测）；`Set-NetFirewallRule -Program` 可改且**原地修复保 InstanceID**；`LocalSubnet4` 可写可读。
**M3 不插队做 024/025/026**，等对应里程碑指令。

## 残留

`%LOCALAPPDATA%\LanRemote\backups\secrets.bin.pre-m1.3-reset.bak`。**绝不把「自动删除身份备份」写进产品。**
