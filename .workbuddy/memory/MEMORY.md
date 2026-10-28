# MEMORY

> 唯一真实进度 = 仓库根 `HANDOFF.md`；本文件只放跨会话必记规则、实测事实与停点，上限10000字符。
> ADR `docs/DECISIONS.md`（至119：自动画质先证产品装配再谈真机；118换代成功才写档位；045现网只读）；原始规格 `LanRemote_Implementation_Package/` 不回写。
> 2026-10-10用户选B：只支持新版双方，旧端拒绝并提示升级；无legacy单屏兼容/静默降级。

## 当前续作点（2026-10-28，权威详HANDOFF§19.75/ADR119）

**Last code commit：`d42d65b77a14bd1bc168bc8af6d529b8c04df4d9`**，1file/+364/-0（仅新增产品接线测试，未改生产）。产品装配层已证明：HostRuntimeService 真装了 ControlPing + 来源观察 + 周期worker，真实回环会话按 sessionId 结算到真实 ping/pong RTT。**换代生效仍未端到端证明；真机验收待人工。**

**Validation（§19.75）。** 新3项（Acceptance 产品接线），三轮定向各3PASS；五变异 wiring-ping-telemetry-not-wired/wiring-observer-not-installed/wiring-loop-not-started/wiring-forget-not-wired/wiring-worker-kept 各1原Assert红恢复。final-v1 14:18:07–14:22:07+08:00 双配置各7031PASS/0FAIL/0SKIP（Acceptance611）。audit-v1 exit0，report1050220字节/SHA `d63016a312f992403e5b3b6c92f728bc4d94a38e9b4b363420d60a9799e2d1dc`；453源/179原件/40TRX/28132 execution，旧7028身份+3/-0。

**语义。** 产品Host装配后可断言：context.ControlPing与VideoSourcePublished/Withdrawn非空、worker Completion已启动、来源按sessionId登记/摘槽注销、停机join并清空。带Control monitor的会话Dispose必然抛「会话已排空，但存在操作或清理错误」，真实产品保留为诊断，测试只吞这一类。

**前片。** §19.74周期换代接线be022d5/7028；§19.73遥测接缝与决策核心4fd4099/7024；§19.72真实ping/pong与RTT 9fb249f/7010；§19.70预设ebd5e3d/6984；§19.69帧率dcd5eea/6965。

**Next。** 真实Host/Client产品联调与M6真机双端验收（多屏切换后鼠标坐标准确、每会话画质独立）；需真人/真机/实屏，不能自行判定。无M6产品包；不重验M4/M5、不启M7、不改网/防护/权限/Undo/旧ZIP。

## 定位与硬约束

Windows 局域网屏幕共享/远程控制（自用）。无账号/云/穿透/UPnP；仅同IPv4子网RFC1918，须过访问密钥挑战认证。

- C#/.NET 10/WPF/x64；TFM `net10.0` + `net10.0-windows`；**不要**加 `windows10.0.19041.0`（ADR-010）
- UDP 45872 发现；TCP 45873 控制；Control 与 Video 是**两条独立TLS**（ADR-003）
- 访问密钥128-bit `RandomNumberGenerator`；HMAC-SHA256挑战；明文key不上网；必验serverProof
- 视频管线 bounded queue(1~2)+DropOldest；**严禁无界队列**
- 永不删除/弱化：同子网校验、RFC1918、TLS、指纹pinning、HMAC、DPAPI、ViewOnly/Control隔离、本机审批、被控提示、紧急停止

## 协作规矩

中文；结构化；结论先行给证据；**严禁伪代码与伪造构建/测试结论**（没跑写「未运行」）；改动最小化。
每片 = build + test + 更新HANDOFF（记 `Last code commit` + `Working tree at validation`，**不写HEAD hash**）+ 需要时补ADR + 当日日志 + 本文件。
提交只用新提交+普通push，然后 `git ls-remote` 实读完整号核对；不强推/不amend/不改历史/不跳hooks。
证据目录 `outputs/m6-<主题>-<日期>` 独占新建，脚本沿用最近一片真名（run_stage/mutate/audit/package/launch_aux）；不覆盖旧ZIP，新旧一起核对。
**outputs被Git忽略：源码push ≠ 证据交付**，证据包须另行打包回读并真正交付。
**交付必须双击即GUI**（WinExe/WPF），绝不「控制台exe+脚本」。
用户2026-10-12持续授权：每片验证后新提交并推送再自主继续，仅真人/权限依赖停。

## 命令 / 打包

`source scripts/env.sh`（dotnet 在 `~/.dotnet`，不在PATH）；`dotnet build LanRemote.sln -c Debug && dotnet test LanRemote.sln -c Debug --no-build`。
M4包沿用 `scripts/acceptance/make-m3-package.py`，仅M4且 `--output-dir <已有目录>`；禁SKIP，每次独立目录。旧make-package.py拒绝。
`.ps1` 必须UTF-8 **with BOM**；`.cmd` 纯ASCII。新包不交付这些脚本。

## 里程碑（2026-10-23）

M0–M5完成、M6进行中、M7–M11未开始；M4历史不翻绿，M5已按用户确认收口。origin为public；勿裸跑helper-selector（含--help）。

## 实测事实（别再猜）

- **TFM（ADR039）**：net10.0不能引用net10.0-windows（NU1201）；无Windows API纯逻辑放Transport。
- **证书（ADR029/030）**：SslStream服务端须DefaultKeySet；EphemeralKeySet独立实测失败。TLS失败必抓服务端异常，客户端可能仅EOF。
- **.NET 10 默认值**：`AllowDuplicateProperties`=True且后者覆盖；`MaxDepth`属性值=0；客户端`AllowTlsResume/AllowRenegotiation`=True、服务端Renegotiation=**False**；`EnabledSslProtocols=None`须显式写；校验回调参数是`X509Certificate`基类 → 用`GetRawCertData()`。
- **认证解析**：非法UTF-8使`Utf8JsonReader.GetString`抛InvalidOperationException，`JsonSerializer.Deserialize`才包装JsonException，两类都捕；AA++/AA//为合法canonical base64不能拒。
- **发现**：probe回应目标=`remote.Address:45872`（非源端口）；sender须显式`SetSocketOption(MulticastInterface, 网络序4字节)`。
- **Windows**：一张网卡DHCP/静态不能共存；`netsh`退出码不可信（用`Get-NetIPInterface`复核）；`172.100.x.x` **不是**RFC1918（172段只到172.31）。
- **子进程编码**：.NET 10无CP936解码器；PS 5.1管道stdout按控制台代码页编码 → 脚本在`IsOutputRedirected`时切UTF-8，见U+FFFD记WARN。
- **M1存储**：`secrets.bin`=`LRSC`+ver+len+DPAPI(JSON)；证书ECDSA P-256自签5年；**禁静默自动重签**。
- **HelloTimeout** = 客户端写预算（服务端从不消费）；post-TLS pre-auth由「前缀5s+payload 10s+信封8s封顶」约束。

## 测试/验收写法硬约束（踩过的坑）

- 变异必须确认真变红（仍绿=测试隔离错了，ADR-032）；期望值只能由客观事实派生（ADR-034）。
- **构建告警门禁不算kill**：变异不能让私有字段只剩`Interlocked.Read`引用（会CS0649被零警告门禁拦下），须保持字段被赋值。
- **xunit v3 的 `Assert.True(cond, message)` 在TRX只上报自定义消息、不含"Assert."前缀**；原Assert由「消息含Assert.或堆栈落在新增测试类」双重认证，禁用Timeout/Guard/Watchdog/超时替代。
- **FramePipeline 禁止新增时钟调用**：既有测试断言构造/创建阶段`TimestampCalls==0`，并有7项注入时钟失败场景（须保留原异常）。实际帧率一类指标用Stopwatch，不在构造/Start取`TimeProvider.GetTimestamp()`。
- **形态契约坑**：给既有internal入口加**重载**会让旧测试反射取方法AmbiguousMatchException；旧测试不可改时改用独立命名入口。新增生产代码要先想反射形态影响。
- **带Control monitor的真实会话Dispose必然抛「会话已排空，但存在操作或清理错误」（monitor读被中止）**：产品ClientService把它保留为「操作诊断已保留」；测试只吞这一类既定诊断，且主体已释放的客户端不能在清理里二次释放，否则同一原诊断被算两次。
- MemoryStream派生+重写`Read(Span)`/`ReadAsync(Memory)` → 一次读被数两遍；`"""..."""`里`\n`非转义；取消断言只写`is OperationCanceledException`。
- 禁止「失败即PASS」：连不上抛`SocketException`/`IOException`永不是`AuthenticationException`；TLS 1.3幽灵行判定写区间`3..4`；`ReadAsync==0`写「有序EOF」。
- 裸后台线程未处理异常=进程瞬死；未观察faulted Task=静默存活 → 两个handler都要挂。
- `ArgumentOutOfRangeException.ThrowIfNegative` **不支持TimeSpan**（CS0315，TimeSpan不是INumberBase）；时限类参数要手写 `< TimeSpan.Zero` 比较。
- 测试里 lambda 参数名 `_` 与 `out _` 同处一个作用域会冲突（CS1503 out int），换掉lambda参数名。
- **本机NuGet还原可能整会话坏掉**（`dotnet restore`/`nuget locals`报`Value cannot be null (path1)`）：用 `build -t:Rebuild --no-restore`；审计比对argv要按旧/新runner区分。
- **别在装配路径重复校验配置期已约束的条件**：曾因ping+UseSessionWriter重复校验关掉真实产品连接，三项验收测试312ms即失败；改生产校验后整轮重跑，不留旧stage。
- Control周期/pong worker必须挂既有停止与join（Host ping loop由writer lifetime join；Client pong进CloseAndJoin/错误/释放证据）；写槽冲突跳过不排队。
- 新增测试跨多个项目会破坏证据链“定向Counter覆盖全部新增身份”；不要用解决方案级定向或放宽审计，应让新测试同处一个目标程序集（必要时只加测试友元/引用）。
- 变异替换串要唯一可逆：升降档两支的同形代码（如`index±1`）必须带上下文/注释区分，否则逆替换校验会拦下。
- `dotnet test`全量计数须看全部项目结果，禁用tail截断。长base64用`Convert.ToBase64String`现造。
- **夹具拆线竞速**：出结局先`await Task.WhenAny(clientTask, Task.Delay(3s))`再cancel+dispose。
- **整仓并行抖动**：`HostDisplayRequestReaderTests.StopBeforeOriginalContinuationExitNotifiesButNeverDispatches` 在整仓并行负载下偶发 Expected 1 / Actual 2，单独复跑恒绿；先单独复跑并确认未触及Transport，再决定缺陷轮重跑或真回归。

## 网络 / lab

- 当前A热点if13=`192.168.137.1/24`；上游以太网if6=`172.100.166.220/24` DHCP且非RFC1918。
- 两机验收及2026-09-21 lab撤销已完成；旧set-lab-ip.ps1仅保留历史正文、当前入口无条件throw，严禁按旧记录执行准备/Undo。
- **2026-09-24原始需求**：同LAN自动发现；只读网卡/地址，禁改DHCP/静态IP/网关/DNS/路由/热点/ICS/网络类别，不自动动防火墙。软件/验收不得使任一端断网。

## 产品形态（ADR-024/025/026/035/036）

ADR-045优先：只读网络诊断保留；主进程不提权，不得重加改网功能。

## 残留

`%LOCALAPPDATA%\LanRemote\backups\secrets.bin.pre-m1.3-reset.bak`。**绝不把「自动删除身份备份」写进产品。**
