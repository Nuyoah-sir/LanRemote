# MEMORY

> 唯一真实进度 = 仓库根 `HANDOFF.md`；本文件只放跨会话必记规则、实测事实与停点，上限10000字符。
> ADR `docs/DECISIONS.md`（至113：实际帧率指标用Stopwatch；045现网只读）；原始规格 `LanRemote_Implementation_Package/` 不回写。
> 2026-10-10用户选B：只支持新版双方，旧端拒绝并提示升级；无legacy单屏兼容/静默降级。

## 当前续作点（2026-10-23，权威详HANDOFF§19.69/ADR113）

**Last code commit：`dcd5eea58fb6cde4390f1dc3c0735f5ad4f70db8`**，3files/+205/-0；实际帧率指标（EncodedFrames计数 + ActualFps，Stopwatch测量）已暴露并按管线独立，但产品侧尚无读取者、auto quality控制器未接。

**Validation。** 新4项，三轮定向各4PASS；五变异counter-not-incremented/fps-always-zero/start-tick-not-recorded/fps-resets-history/producer-passthrough-zero各1原Assert红恢复。final-v1于13:59:07–14:03:30+08:00四Rebuild零警告错误、六dotnet/worker/launcher完整wait0，Debug/Release各6965PASS/0FAIL/0SKIP（Sessions300/Acceptance571/Transport5229/Capture296/Core197/Security66/Protocol303/Integration3）。audit-v1 14:03:55–14:04:00+08:00原46908/外层/主线程exit0，report994301字节/SHA `ecd067530ce4d7a04415e169e022ae5b0c431d3e78ce02608d5fdb5b2152cc65`；434源/180原件/40TRX/27869execution，旧6961身份+4/-0。

**前两片简记。** §19.68画质接线e250ca2/6961（工厂sessionQuality回调+App SessionQualityResolver快照）；§19.67画质底座32420e4/6951（两个独立命名internal入口）；§19.66产品侧切屏93d72e2/6940（SessionId快照入闭包）；§19.65非主屏GDI 1028888/6922。均已push核remote一致。

**Next。** scale与quality presets、ping RTT、让auto quality控制器消费本帧率指标，再真实Host/Client产品联调与M6真机双端验收；当前无需人工前置。无M6产品包；不重验M4/M5、不启M7、不改网/防护/权限/Undo/旧ZIP。

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

M0–M5完成、M6进行中、M7–M11未开始。M4详§18.25不翻绿/重跑；pin仅顺序关联、SAC非产品功能。
origin为public；gh无需；勿裸跑helper-selector（含--help），会弹GUI/改配置。M5已于2026-10-12按用户确认收口。

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
- **形态契约坑**：给既有internal入口加**重载**会让旧测试反射取方法AmbiguousMatchException；旧测试不可改时改用独立命名入口。凡旧测试用反射读编译器生成成员，新增生产代码都要先想形态影响（含闭包捕获局部会被提升到闭包类）。
- MemoryStream派生+重写`Read(Span)`/`ReadAsync(Memory)` → 一次读被数两遍；`"""..."""`里`\n`非转义；取消断言只写`is OperationCanceledException`。
- 禁止「失败即PASS」：连不上抛`SocketException`/`IOException`永不是`AuthenticationException`；TLS 1.3幽灵行判定写区间`3..4`；`ReadAsync==0`写「有序EOF」。
- 裸后台线程未处理异常=进程瞬死；未观察faulted Task=静默存活 → 两个handler都要挂。
- `dotnet test`全量计数须看全部项目结果，禁用tail截断。长base64用`Convert.ToBase64String`现造。
- **夹具拆线竞速**：出结局先`await Task.WhenAny(clientTask, Task.Delay(3s))`再cancel+dispose。

## 网络 / lab

- 当前A热点if13=`192.168.137.1/24`；上游以太网if6=`172.100.166.220/24` DHCP且非RFC1918。
- 两机验收及2026-09-21 lab撤销已完成；旧set-lab-ip.ps1仅保留历史正文、当前入口无条件throw，严禁按旧记录执行准备/Undo。
- **2026-09-24原始需求**：同LAN自动发现；只读网卡/地址，禁改DHCP/静态IP/网关/DNS/路由/热点/ICS/网络类别，不自动动防火墙。软件/验收不得使任一端断网。

## 产品形态（ADR-024/025/026/035/036）

ADR-045优先：只读网络诊断保留；旧改IP计划取消，主进程不提权。不得按旧M10安排重加改网功能。M3不插队做024/025/026。

## 残留

`%LOCALAPPDATA%\LanRemote\backups\secrets.bin.pre-m1.3-reset.bak`。**绝不把「自动删除身份备份」写进产品。**
