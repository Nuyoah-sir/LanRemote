# LanRemote 项目长期记忆

> 唯一真实进度 = 仓库根 `HANDOFF.md`；本文件只放跨会话必记的规则、实测事实与停点。
> **2026-10-10用户选B：新协议只支持新版双方连接，旧端明确拒绝并要求两端升级；不保留legacy单屏兼容分支，不静默降级。此为产品政策，旧v1拒绝的实际接线不能在未实现时宣称完成。**
> ADR `docs/DECISIONS.md`（至096：生产者换代；045现网只读）；原始规格 `LanRemote_Implementation_Package/` 不回写。
> 记忆上限10000字符，更新先验长度。

## 当前续作点（2026-10-15，权威详HANDOFF§19.53/ADR097）

已推代码62afdf0/5615（完整号与应用程序集见HANDOFF）；当日早前直连github曾超时/重置致前两次push失败，第三次普通push成功并实读远端refs/heads/main一致。接手提示词见仓库根新对话接手prompt-20261015.md。

**发布接缝（§19.53）。** PublishVideoSource(真lease, source)在registry同gate单次发布并返回只摘槽句柄，不接管source；ReplaceVideoProducerAsync非async、同gate后直接return source.ReplaceAsync原Task。撤销三态只拒后续接纳、不撤回已接管候选；真lease与registry共gate完成。5files/新31case，定向150/150，双配置各5615PASS，五变异2/1/0not-killed/1/1，独立exit0：+31/-0、400源。Router完整装配留下一片。

**M5已收口。** 2026-10-12用户确认稳定、继续M6，撤回卡顿判断；不重查/重验/加压测。原十分钟/慢网/双方上网确认保留，不捏造数字/截图/审计。§19.29旧ZIP不覆盖/重打、不含M6（完整收据见HANDOFF）。Host默认5/50/JPEG60，显式10/15/20/30默认75/JPEG60，scale50/67/75/100、JPEG40..85；Auto禁，不写config/热切，停完整再开。16,777,216像素预分配预算；32MiB仅JPEG payload上限非全机内存上界。

**M6认证V2-only已接，非整体收口。** 两Host/Client：TLS/pinning→Hello2→Challenge2→Offer/Accept→clientProofV2→真实审批→serverProofV2→父资格。同session agreement非授权，绝对deadline/单读者/close/join保留。仅合法V1/未知版声明提示升级，UI标未经认证；EOF/timeout/畸形不确诊旧端，身份/pin/proof/密码/审批错误优先；无fallback。VideoHello/ACK protocol1、VIDEO-V1/83字节transcript、第二TLS/LRVF不变。

§19.43 Accept单删仍绿，不称单点kill。

**Client接收（§19.44）。** 唯一reader、256KiB/10秒原锚、严格host_status/单槽；父gate发布撤销、原IO/timer完整join。Reached非Task发布，WhenAny非join。

**Host/Router（§19.45–47）。** 同gate发布worker、None原IO；原锚含序列化/IO/timer及Int128末检。Abort仅通知、原Task join，退出≠释放。success flush→Register→秘密finally→holding→目录；StopRequired先close/注销再join全部根，Dispose后取VideoCompletion。逐槽保错误树；受控流非TLS。

**Host/Client目录（§19.48–49）。** Host record共享守卫，非async外桥/原Task保错误树；委托内new GDI，10秒非硬中断，seq0仅元数据。Client原父gate只读单槽、撤销留历史；250ms timer非实时，Closing/SessionEnded/Drain前清timer/getter/token/ItemsSource，拒迟到且异常不跳Drain。5410/5434历史是单机自动化，不是双机/真人/多屏联合。

**固定目标准备（§19.50）。** CreateForDisplayAsync完整枚举唯一ID及record匹配，无主屏回退；预算先于encoder，交付未Start，创建token不控制生产。共用原Task/回滚保错误树。41新/5475，记录相等非硬件拓扑锁，受控pipeline非GDI/多屏。

**可选编号（§19.51）。** sender(ulong)实例写前序列，默认new()透传；Max一次后EOF正常、额外帧释放耗尽不回绕。writer仅改header，原owner/Task/单飞保持。61新/5536；编号非发送/显示证明。

**拥有型换代（§19.52）。** ReplaceableVideoFrameSource单session/read/replace，正常返回Task即接管未Start候选；旧代原Start/Read及迟到owner结算后才新Start。三清理入口锁外独立、只同原Task去重；提交帧不可收回，旧EOF不外泄，无active Completion自动监视。Completion先退役/Replace→settled→外层Read避免自join。48新/5584；旧帧原IO阻塞时换代完成已测，非TLS/GDI/实屏。

**下一片：Router完整生命周期装配，再live/画质。** Router仍固定source/default sender；换代非发送/显示屏障，单次Video租约禁重连。无M6包；不重验M4/M5、不启M7、不改网/防护/权限/Undo/旧ZIP。16字段/15NUL/无尾NUL及AUTH/GRANT-V2不变。

M4详§18.25，不翻绿/重跑；pin仅顺序关联、SAC非产品功能。§19.16–18：先发布后Start，父等raw+投影/子仅raw，不吞close错；敏感wire等原IO退出清零，不保证TLS副本擦除。

审批按状态机接受时刻：elapsed>=budget拒，gate前计时含UI；caller取消>截止>已观察活动>决定，校验后再查。machine覆盖MAC后终检；context loader至多一项实际store，迟到key先清零再释放准入，CTS不能硬中断同步DPAPI/gate/阻塞回调。客户端独占TLS到认证，presentedPin及grant proof验完才交会话，public无流/token/输入；hello后machine10s、challenge只收窄、独立approval60s。只消费首个终帧；DTO/TLS内部副本不保证擦除；clock变异须在被删检查之前客观固定到期。

用户2026-10-12持续授权：每片验证后新提交并推送，再自主继续；仅真人/权限依赖停，不强推/改历史/跳hooks。context单WorkLease先清零归还；Host180秒自然结束、提前停作废，成功SessionId/负例四元组。

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
**交付必须双击即 GUI**（WinExe/WPF），绝不「控制台 exe + 脚本」。
**2026-09-24用户重申原始需求**：同局域网中安装并运行软件的机器应自动发现；软件适应现网，只读网卡/地址并使用标准通信，不重配系统网络。A/B仅角色，不代表固定IP/实验网段；不改DHCP/静态IP/网关/DNS/路由/热点/ICS/网络类别，不自动动防火墙。发现失败应修识别/通信代码或如实诊断，不能要求用户改网配合。软件/验收不能导致任一端断网，UAC/可撤销不是豁免。旧lab准备停用；恢复旧改动须核对状态并单独获准，不能默认Undo。

## 命令 / 打包

```bash
source scripts/env.sh   # dotnet 在 ~/.dotnet，不在 PATH
dotnet build LanRemote.sln -c Debug && dotnet test LanRemote.sln -c Debug --no-build
```

出包沿用 `scripts/acceptance/make-m3-package.py`，仅M4：`--manual outputs/m4-network-fix/M4-现网验收说明.txt --output-dir <已有目录>`；禁止SKIP，每次独立artifacts/m4-acceptance-*，不删除旧目录，CLEAN已移除。旧make-package.py拒绝。
`.ps1` 必须 UTF-8 **with BOM**；`.cmd` 纯ASCII。新包不交付这些脚本。
M4现网包：264 files / raw132.5MiB / zip57.5MiB，全扁平，程序集含代码提交。

## 里程碑（更新至2026-10-15）

| 阶段 | 状态 |
| --- | --- |
| M0~M1.3 | 完成（`104f296` / `9e75fce`） |
| M2+M2.1 | 完成，两机验收 **20/20**（`313c542`，408 tests） |
| **M3** | 完成，2026-09-21两机24步/574绿，证据配对与机械INVALID_RUN见HANDOFF§15。 |
| **M3.1** | 完成，2dee00c双配置601绿/零警告；8s provisional信封、HelloTimeout、停机报告及B15/16/18/19/20见§18.4 A。 |
| **M4** | **完成（2026-09-29）**。`2596357`双配置各1433 PASS/Python14，实屏明确确认配齐，原DoD/21步收口见HANDOFF§18.25；不重跑已通过项。 |
| **M5** | 已按2026-10-12用户稳定反馈收口；§19.29原包保留，不重复人工验收。 |
| **M6** | 进行中：§19.53发布接缝已推62afdf0、双配置各5615通过；Router完整装配/live/画质未接。 |
| M7~M11 | 未开始 |

提交链见HANDOFF。
origin为public，地址/历史见HANDOFF§1，不外推当前同步。gh无需。勿裸跑helper-selector（含--help），会弹GUI/改配置。凭据selected=manager、空值+manager链已修；按持续授权逐片提交推送。

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

accept→同子网→准入→TLS不可换；pre-auth≤4KiB；M3旧hello后关闭已由M4显式`ControlPreAuthHandoff`→`ControlAuthSession`替代(ADR037)。五段deadline只证执行精度(0–36ms)，M3.1补外层信封、transcript拆分评审已回收，数值仍待本机实验；Host同子网/准入/TLS拒绝零logger待M9。
验收器：双击=WPF 窗口，`--headless client|host|info`（旧prepare/lab/Undo全拒绝）；退出码 0/1/2/3/4=预期内/真失败/前置不满足/工具错/无效运行；`Combine` 优先级 `InvalidRun>HarnessError>Fail>PreconditionUnmet>Pass`；四场景 `success`→`pin-mismatch`→`timeout`→`slow-dribble`（判据 `sent=3/4`）；两机配对用 4 元组（聚合计数不算证明）；控制端只给 `PENDING-HOST-EVIDENCE`；`gui.log` 只记进程级事实、每轮证据在 per-run 文件；别拿 `LanRemote.App` 验传输层（零调用）。**被控端「停止监听」收尾 → 结局字段必为 `INVALID_RUN`（设计：按停=机械作废，防「按停伪造通过」），判定看逐条证据；`--headless host --seconds N` 定时轮不走该路径（实测 PASS）。**

## 测试/验收写法硬约束（踩过的坑）

- MemoryStream 派生 + 重写 `Read(Span)`/`ReadAsync(Memory)` → 一次读被数两遍；要计数必须包装
- `"""..."""` 里 `\n` 非转义；取消断言只写 `is OperationCanceledException`
- 变异验证必须确认真变红（仍绿 = 测试隔离错了，ADR-032）；期望值只能由客观事实派生，不许把「没观测到」写成 0（ADR-034）
- 禁止「失败即 PASS」：连不上抛 `SocketException`/`IOException` 永不是 `AuthenticationException`；`ConnectionReset` 不算「没连上」
- TLS 1.3 幽灵行 → 判定写区间 `3..4`；`ReadAsync==0` ≠ close_notify → 文案写「有序 EOF」；`TryParse` 语义别反
- 裸后台线程未处理异常 = 进程瞬死；未观察 faulted Task = 静默存活 → 两个 handler 都要挂
- tailer的`usable`须等于确定写完行数，配独立`CountCompleteLines`对账。
- 帧读取器「失败后恢复」场景：**消费掉的字节无法退回**——半前缀超时后再读必然错位；恢复性用例只能建在 **0 字节失败**上（B19 定案）
- `dotnet test` 全量数总数用 `| grep -E "已通过!|失败!"`：`tail -N` 会截掉**首个**项目结果行（Protocol.Tests 曾被整行吞掉，574→601 的「差值」据此而来）
- **黄金向量**：期望来自独立第二实现`scripts/reference/gen-auth-golden-vectors.py`；NUL写`\u0000`，C#的`\0`接数字会成八进制；变异须触实现，常量自证是假绿。
- **测试字面量**：长base64用`Convert.ToBase64String`/`B64(int)`现造，手抄31字节误成45字符曾让邻界值被错误拒绝路径救活。
- **夹具拆线竞速**：出结局立刻cancel+dispose会抢掉客户端快速失败终帧；先`await Task.WhenAny(clientTask, Task.Delay(3s))`。迟到决定延时不绑stall令牌，避免夹具取消吞掉目标。

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
