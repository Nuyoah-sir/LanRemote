# LanRemote 项目长期记忆

> 唯一真实进度 = 仓库根 `HANDOFF.md`；本文件只放跨会话必记的规则、实测事实与停点。
> **2026-10-10用户选B：只支持新版双方，旧端拒绝并提示两端升级；无legacy单屏兼容/静默降级。产品政策不能冒充实际接线完成。**
> ADR `docs/DECISIONS.md`（至112：按真实认证会话分配画质；045现网只读）；原始规格 `LanRemote_Implementation_Package/` 不回写。
> 记忆上限10000字符，更新先验长度。

## 当前续作点（2026-10-22，权威详HANDOFF§19.68/ADR112）

**Last code commit：e250ca2c26872c89c0002d79cdb30518fa02a38d**，4files/+286/-4；画质已按真实认证会话分配（工厂sessionQuality回调 + App SessionQualityResolver首次快照、主机停止清空），但改档来源仍只有主机档位。

**Validation。** 新10项（回调4+解析器6），三轮定向各35PASS；四变异callback-ignored/callback-late-check/resolver-no-snapshot/resolver-set-no-validate各1/2/1/1原Assert红恢复。final-v1于10:49:39–10:53:49+08:00四Rebuild零警告错误、六dotnet/worker/launcher完整wait0，Debug/Release各6961PASS/0FAIL/0SKIP（Acceptance571/Sessions296/Transport5229/Capture296/Core197/Security66/Protocol303/Integration3）。audit-v1 10:54:46–10:54:50+08:00原43708/外层/主线程exit0，report909769字节/SHA `849184bad9a6075481c0686324e9fd37adf1c052e79524ce366fac5006adab3e`；433源/166原件/39TRX/27934execution，旧6951身份+10/-0。

**画质接线要点。** Transport只按sessionId调CreateAsync不带画质 → 按会话分配只能靠工厂解析回调；回调与画质都在依赖创建之前校验。回调只读ActivePreviewSettings属性、绝不捕获StartAsync的启动参数局部（捕获会让编译器把局部提升到闭包类，破坏旧测试反射的状态机字段）。会话首次解析即快照，主机换档不影响已运行会话。

**形态契约坑（延续）。** 给既有internal入口加**重载**会让旧测试反射取方法AmbiguousMatchException；旧测试不可改时改用独立命名入口。凡旧测试用反射读编译器生成成员，新增生产代码都要先想形态影响。

**§19.67（画质底座）简记。** 工厂新增CreateWithSessionQualityAsync与CreateForDisplayWithSessionQualityAsync，画质只作用于本会话管线、校验前置；11项、四变异1/5/1/1，6951双配置全绿，代码32420e4已push。

**§19.66（产品侧切屏）简记。** Host按真实认证会话装配prepare与同一写者（SessionId快照入闭包）；Client交付后attach前EnableDisplaySwitching+三参Presenter；窗口目录项接真实请求/busy/停止解绑；6940双配置全绿，唯一旧测试两行目录文本例外。

**M6 V2-only已接、未收口。** TLS/pinning→Hello2→Challenge2→Offer/Accept→clientProofV2→真实审批→serverProofV2→父资格；仅合法V1/未知版声明提示升级且UI标未认证。

**Next：画质调控与联调。** scale与quality presets、ping RTT、actual FPS、auto quality简单控制器（届时才有按会话改档的真实来源），再真实Host/Client产品联调与M6真机双端验收；当前无需人工前置。回执与画质都非物理scanout或真人验收证明。无M6产品包；不重验M4/M5、不启M7、不改网/防护/权限/Undo/旧ZIP。

M4详§18.25，不翻绿/重跑；pin仅顺序关联、SAC非产品功能。§19.16–18：先发布后Start，父等raw+投影/子仅raw，不吞close错；敏感wire等原IO退出清零，不保证TLS副本擦除。

审批按状态机接受时刻：elapsed>=budget拒，gate前计时含UI；caller取消>截止>已观察活动>决定，校验后再查。machine覆盖MAC后终检；context loader至多一项实际store，迟到key先清零再释放准入，CTS不能硬中断同步DPAPI/gate/阻塞回调。客户端独占TLS到认证，presentedPin及grant proof验完才交会话，public无流/token/输入；hello后machine10s、challenge只收窄、独立approval60s。只消费首个终帧；DTO/TLS内部副本不保证擦除；clock变异须在被删检查之前客观固定到期。

用户2026-10-12持续授权：每片验证后新提交并推送，再自主继续；仅真人/权限依赖停，不强推/改历史/跳hooks。Host180秒自然结束、提前停作废。

## 定位与硬约束

Windows 局域网屏幕共享/远程控制（自用）。无账号/云/穿透/UPnP；仅同 IPv4 子网 RFC1918 设备，须过访问密钥挑战认证。

- C# / .NET 10 / WPF / x64；TFM `net10.0` + `net10.0-windows`；**不要**加 `windows10.0.19041.0`（无 Win10 SDK，ADR-010）
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
**2026-09-24原始需求**：同LAN已运行软件的机器自动发现；适应现网、只读网卡/地址、标准通信，不重配网络。A/B仅角色非固定IP/lab网段；禁改DHCP/静态IP/网关/DNS/路由/热点/ICS/网络类别，不自动动防火墙。发现失败修代码或如实诊断，不要求用户改网。软件/验收不得使任一端断网，UAC/可撤销非豁免。旧lab停用；恢复先核状态并单独获准，禁默认Undo。

## 命令 / 打包

```bash
source scripts/env.sh   # dotnet 在 ~/.dotnet，不在 PATH
dotnet build LanRemote.sln -c Debug && dotnet test LanRemote.sln -c Debug --no-build
```

出包沿用 `scripts/acceptance/make-m3-package.py`，仅M4：`--manual outputs/m4-network-fix/M4-现网验收说明.txt --output-dir <已有目录>`；禁止SKIP，每次独立artifacts/m4-acceptance-*，不删除旧目录，CLEAN已移除。旧make-package.py拒绝。
`.ps1` 必须 UTF-8 **with BOM**；`.cmd` 纯ASCII。新包不交付这些脚本。
M4现网包：264 files / raw132.5MiB / zip57.5MiB，全扁平，程序集含代码提交。

## 里程碑（2026-10-20）

M0–M5完成、M6进行中、M7–M11未开始。两机/INVALID_RUN及收口历史见HANDOFF§9/§15/§18.4 A/§18.25/§19.29，M4/M5不重验、旧包保留。M6屏障已验证，默认live/画质未接。
origin为public，地址/历史见HANDOFF§1，不外推当前同步。gh无需。勿裸跑helper-selector（含--help），会弹GUI/改配置。凭据selected=manager、空值+manager链已修；按持续授权逐片提交推送。

## 实测事实（别再猜）

- **TFM（ADR039实测）**：net10.0不能引用net10.0-windows（NU1201）；无Windows API纯逻辑放Transport/net10.0，Security只留DPAPI/证书/密钥存储。
- **证书（ADR029/030）**：SslStream服务端须DefaultKeySet；EphemeralKeySet独立实测失败，PersistKeySet留磁盘副本。CreateSelfSigned私钥也是ephemeral，须导出PFX→Default重导入。同进程先Persist污染Ephemeral结论，须新进程验证。TLS失败必抓服务端异常，客户端可能仅EOF。
- **.NET 10 默认值**：`AllowDuplicateProperties`=True 且后者覆盖；`MaxDepth` 属性值=0（=内置 64）；客户端 `AllowTlsResume/AllowRenegotiation`=True，服务端 Renegotiation=**False**（不对称）；`EnabledSslProtocols=None` 须显式写；校验回调参数是 `X509Certificate` 基类 → 用 `GetRawCertData()`
- **认证解析实测**：非法UTF-8使Utf8JsonReader.GetString抛InvalidOperationException，JsonSerializer.Deserialize才包装JsonException，须两类都捕；孤立`\uD800`抛JsonException归malformed。Guid.TryParseExact("D")容忍大写/前后空白，canonical须round-trip；AA++/AA//为合法canonical base64，不能拒绝标准+/字母表。
- **发现**：probe 回应目标 = `remote.Address:45872`（非源端口）；sender 必须显式 `SetSocketOption(MulticastInterface, 网络序 4 字节)`
- **Windows**：一张网卡 DHCP/静态不能共存；`netsh` 退出码不可信（`Get-NetIPInterface` 复核）；选网卡排除 VMnet 等虚拟；PS 5.1 `($x|%{...} -join ', ')` 是绑定陷阱；`172.100.x.x` **不是** RFC1918（按数值判，172 段只到 172.31）
- **提权**：`ShellExecuteExW("runas")`+`WaitForSingleObject`+取退出码（UAC 拒绝=`ERROR_CANCELLED`1223）；主进程永不提权（ADR-035：窄域 helper + 固定动词 + TOCTOU 重校验）
- **子进程编码契约**：.NET 10 无 CP936 解码器；PS 5.1 管道 stdout 按控制台代码页编 → 脚本须在 `IsOutputRedirected` 时切 UTF-8，harness 固定 UTF-8 解，见 U+FFFD 记 WARN
- **M1 存储**：`secrets.bin`=`LRSC`+ver+len+DPAPI(JSON)；设备码=`Base32(SHA256(guid)[..5])`（非秘密）；证书 ECDSA P-256 自签 5 年、KU 只 digitalSignature（ADR-021）；**禁静默自动重签证书**
- **`HelloTimeout` = 客户端写预算（语义已定案）**：服务端从不消费；post-TLS pre-auth 由「前缀 5s + payload 10s + **信封 8s 封顶**」约束（M3.1 已落地 `PreAuthEnvelopeTimeout`，判据=对端可控等待被信封切）。信封数值 provisional，待数值实验定案

## M3 速查（细节在 HANDOFF.md）

accept→同子网→准入→TLS不可换；pre-auth≤4KiB；M4以ControlPreAuthHandoff→ControlAuthSession替代M3 hello后关闭(ADR037)。旧deadline只证执行精度，M3.1外层信封/拆分评审已回收，数值待本机实验；Host同子网/准入/TLS拒绝零logger待M9。
验收器WPF，`--headless client|host|info`；旧prepare/lab/Undo全拒。退出码0/1/2/3/4=预期内/真失败/前置不足/工具错/无效；Combine：InvalidRun>HarnessError>Fail>PreconditionUnmet>Pass。success→pin-mismatch→timeout→slow-dribble(sent=3/4)；两机用4元组配对，聚合计数非证明，控制端仅PENDING-HOST-EVIDENCE。gui.log仅进程事实，逐轮看per-run。M3旧App零调用不可验传输；被控端按钮停监听必INVALID_RUN，不能伪通过；定时`--headless host --seconds N`不走按钮路径，历史PASS。

## 测试/验收写法硬约束（踩过的坑）

- MemoryStream 派生 + 重写 `Read(Span)`/`ReadAsync(Memory)` → 一次读被数两遍；要计数必须包装
- `"""..."""` 里 `\n` 非转义；取消断言只写 `is OperationCanceledException`
- 变异验证必须确认真变红（仍绿 = 测试隔离错了，ADR-032）；期望值只能由客观事实派生，不许把「没观测到」写成 0（ADR-034）
- 禁止「失败即 PASS」：连不上抛 `SocketException`/`IOException` 永不是 `AuthenticationException`；`ConnectionReset` 不算「没连上」
- TLS 1.3 幽灵行 → 判定写区间 `3..4`；`ReadAsync==0` ≠ close_notify → 文案写「有序 EOF」；`TryParse` 语义别反
- 裸后台线程未处理异常 = 进程瞬死；未观察 faulted Task = 静默存活 → 两个 handler 都要挂
- tailer的`usable`须等于确定写完行数，配独立`CountCompleteLines`对账。
- 帧读取器「失败后恢复」场景：**消费掉的字节无法退回**——半前缀超时后再读必然错位；恢复性用例只能建在 **0 字节失败**上（B19 定案）
- `dotnet test`全量计数须看全部项目结果（`| grep -E "已通过!|失败!"`）；禁用tail截断，历史曾漏首个Protocol.Tests。
- **黄金向量**：期望来自独立第二实现`scripts/reference/gen-auth-golden-vectors.py`；NUL写`\u0000`，C#的`\0`接数字会成八进制；变异须触实现，常量自证是假绿。
- 长base64用`Convert.ToBase64String`/`B64(int)`现造；手抄长度错会被错误拒绝路径救活。
- **夹具拆线竞速**：出结局立刻cancel+dispose会抢掉客户端快速失败终帧；先`await Task.WhenAny(clientTask, Task.Delay(3s))`。迟到决定延时不绑stall令牌，避免夹具取消吞掉目标。
- **FramePipeline 禁止新增时钟调用**：既有测试断言构造/创建阶段 `TimestampCalls==0`，并有注入时钟失败场景（Clock_failures_preserve_original_exception_and_ownership，7 项）。实际帧率一类指标不能在 Start/构造里取 `TimeProvider.GetTimestamp()`；要测帧率须走不新增时钟调用的方案（如 Stopwatch，或只在首次编码成功后取一次并先验证旧断言不受影响）。

## 网络 / lab

- 当前A热点if13=`192.168.137.1/24`、Wi-Fi Direct Virtual Adapter #2；上游以太网if6=`172.100.166.220/24` DHCP且非RFC1918。旧“唯一活跃公网网卡”已过时。
- 两机验收及2026-09-21 lab撤销已完成，两机info均qualifiedNic=(无)；A/B旧机器名、lab地址与撤销回执已归HANDOFF，仅历史，不作为当前网配或新一轮恢复证明。
- 旧set-lab-ip.ps1仅保留历史正文，当前入口无条件throw；不会随包交付，严禁按旧记录执行准备/Undo。2026-09-21还原仅是历史，不能当作9月24日事故后的恢复证明。

## 产品形态（ADR-024/025/026/035/036，规则已定未编码）

ADR-045优先：只读网络诊断保留；旧ADR-026产品改IP计划取消，025/035网络修改默认落地停止；主进程不提权。不得按旧M10安排重加改网功能。
已实测（M10 别重跑）：绑路径防火墙规则目录移动后**静默悬挂**（读回 Program 检测）；`Set-NetFirewallRule -Program` 可改且**原地修复保 InstanceID**；`LocalSubnet4` 可写可读。
**M3 不插队做 024/025/026**，等对应里程碑指令。

## 残留

`%LOCALAPPDATA%\LanRemote\backups\secrets.bin.pre-m1.3-reset.bak`。**绝不把「自动删除身份备份」写进产品。**
